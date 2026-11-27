using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;
using static PadForge.Tests.ControllerDatabaseFixture;
using static SDL3.SDL;

namespace PadForge.Tests
{
    /// <summary>
    /// How the stored controller database rows reach SDL (#597): what SDL
    /// is given as it starts and in which order, and when a new row is
    /// given for a joystick that just connected. Plain logic, with SDL's
    /// answers handed in, since the test host starts no SDL.
    /// </summary>
    public class ControllerDatabaseUseTests
    {
        private const string Version = "6.0.1";
        private static readonly DateTime Fetched = new(2026, 11, 23, 14, 17, 35, DateTimeKind.Utc);

        private static readonly string KeptRow = Row(0x1000, 0x0001, "Kept Pad");
        private static readonly string SdlRow = Row(0x1000, 0x0002, "SDL Pad");
        private static readonly string NewRow = Row(0x2000, 0x0001, "New Pad");
        private static readonly string NewId = Id(0x2000, 0x0001);

        private static ControllerDatabaseBaseline Pinned() => Baseline(('K', KeptRow), ('S', SdlRow));

        private static ControllerDatabaseSection Section(ControllerDatabaseBaseline baseline, params string[] rows)
        {
            var all = new List<string>(rows);
            foreach (string pinned in new[] { KeptRow, SdlRow })
                if (!all.Any(r => r.StartsWith(pinned.Substring(0, 33), StringComparison.Ordinal))) all.Add(pinned);
            var read = ControllerDatabaseDownload.Read(CommunityFile(all), baseline);
            Assert.Equal(ControllerDatabaseRefusal.None, read.Refusal);
            return ControllerDatabaseSection.FromDownload(read, null, Fetched, baseline, Version, null);
        }

        private static readonly string[] Own =
        {
            "0300d8234c0500006802000000000000,Sony DualShock 3 (DsHidMini SDF and SXS),a:b2,b:b1,platform:Windows,",
            "03000000373500002210000000000000,GameSir G7 Pro,a:b0,b:b1,platform:Windows,",
        };

        // ── As SDL starts ──

        /// <summary>The changed rows go in first and PadForge's own after
        /// them. Both are added at one priority, where the later row for an
        /// ID stands, so PadForge's own row is the one SDL keeps wherever
        /// the two name the same controller.</summary>
        [Fact]
        public void AtStart_SdlIsGivenTheChangedRows_ThenPadForgesOwn()
        {
            string changed = KeptRow.Replace("a:b0", "a:b7");
            var section = Section(Pinned(), changed, NewRow);
            var rows = ControllerDatabaseUse.StartupRows(section, Pinned(), Own);
            Assert.Equal(new[] { changed, Own[0], Own[1] }, rows);
        }

        /// <summary>A new row is never given at the start: it would be read
        /// for every controller of its ID, whatever reads them now.</summary>
        [Fact]
        public void AtStart_NoNewRowIsGiven()
        {
            var section = Section(Pinned(), NewRow, Row(0x2000, 0x0002));
            Assert.Equal(2, section.NewRows);
            Assert.Equal(Own, ControllerDatabaseUse.StartupRows(section, Pinned(), Own));
        }

        [Fact]
        public void AtStart_WithNoSectionOrNoBaseline_SdlIsGivenPadForgesOwnAlone()
        {
            Assert.Equal(Own, ControllerDatabaseUse.StartupRows(null, Pinned(), Own));
            var section = Section(Pinned(), KeptRow.Replace("a:b0", "a:b7"));
            Assert.Equal(Own, ControllerDatabaseUse.StartupRows(section, null, Own));
        }

        /// <summary>A row stored as Changed is given only while the baseline
        /// in force says the fork carries a community row for its ID. Added
        /// for an ID of SDL's own Windows section, it would take the place
        /// of SDL's row.</summary>
        [Fact]
        public void AtStart_AChangedRowIsGivenOnlyForAnIdTheForkCarries()
        {
            var section = Section(Pinned(), KeptRow.Replace("a:b0", "a:b7"));
            Assert.Equal(1, section.ChangedRows);
            // The same section under a baseline that reads the ID as SDL's own.
            var sdlNow = Baseline(('S', KeptRow), ('S', SdlRow));
            Assert.Equal(Own, ControllerDatabaseUse.StartupRows(section, sdlNow, Own));
            var gone = Baseline(('S', SdlRow));
            Assert.Equal(Own, ControllerDatabaseUse.StartupRows(section, gone, Own));
        }

        [Fact]
        public void AMappingFile_IsReadWithoutItsCommentsAndBlankLines()
        {
            const string file = "# PadForge Community Gamepad Mappings\r\n\r\n# One\r\n  " + "030000004c050000b20b000000000000,Pad,a:b1,platform:Windows,"
                + "  \r\n\r\n#Two\n03000000373500002210000000000000,GameSir G7 Pro,a:b0,platform:Windows,\n";
            var rows = ControllerDatabaseUse.ReadRows(new StringReader(file));
            Assert.Equal(new[]
            {
                "030000004c050000b20b000000000000,Pad,a:b1,platform:Windows,",
                "03000000373500002210000000000000,GameSir G7 Pro,a:b0,platform:Windows,",
            }, rows);
        }

        /// <summary>The exe carries PadForge's own rows, and they are the
        /// file's.</summary>
        [Fact]
        public void PadForgesOwnRows_AreTheEmbeddedFiles()
        {
            var onDisk = ControllerDatabaseUse.ReadRows(new StringReader(
                System.IO.File.ReadAllText(Path.Combine(OffLimitsSource.RepoRoot(), "PadForge.App", "gamecontrollerdb_padforge.txt"))));
            Assert.NotEmpty(onDisk);
            Assert.Equal(onDisk, ControllerDatabaseUse.OwnRows);
        }

        /// <summary>An ID of PadForge's own is compared as SDL's lookup
        /// reaches it, so a row that names a name hash stands for its
        /// controller whatever name it reports.</summary>
        [Fact]
        public void PadForgesOwnIds_AreReadAsSdlsLookupReachesThem()
        {
            var ids = ControllerDatabaseUse.OwnIds(Own.Append("no comma here").Append("xinput,XInput Controller,a:b0,"));
            Assert.Equal(new[] { "030000004c0500006802000000000000", "03000000373500002210000000000000" }.OrderBy(i => i),
                ids.OrderBy(i => i));
        }

        /// <summary>A controller the user mapped while PadForge read it raw
        /// keeps that read once SDL maps it through a community row this
        /// release carries (SDL#53), here the 2DC8:3100 the fork took in at
        /// 68f6b71aef. Opened as a gamepad, its saved mapping would read
        /// renumbered inputs. Read raw, it stays a joystick, so neither the
        /// gamepad auto-map nor the trigger rest rule reads it.</summary>
        [Fact]
        public void AControllerMappedRaw_KeepsItsRawRead_WhenSdlStartsMappingIt()
        {
            const SDL_JoystickType Pad = SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD;
            const ushort vendor = 0x2DC8, product = 0x3100;
            var raw = new UserDevice { VendorId = vendor, ProdId = product, CapType = InputDeviceType.Joystick };
            Assert.True(ControllerDatabaseUse.KeepsRawRead(SdlMappingSource.Community, Pad, vendor, product, new[] { raw }, (_, _) => true));
            Assert.False(ControllerDatabaseUse.KeepsRawRead(SdlMappingSource.Community, Pad, vendor, product, new[] { raw }, (_, _) => false));
            Assert.False(ControllerDatabaseUse.KeepsRawRead(SdlMappingSource.Yours, Pad, vendor, product, new[] { raw }, (_, _) => true));
            Assert.False(ControllerDatabaseUse.KeepsRawRead(SdlMappingSource.Community, Pad, vendor, product, Array.Empty<UserDevice>(), (_, _) => true));
            var pad = new UserDevice { VendorId = vendor, ProdId = product, CapType = InputDeviceType.Gamepad };
            Assert.False(ControllerDatabaseUse.KeepsRawRead(SdlMappingSource.Community, Pad, vendor, product, new[] { raw, pad }, (_, _) => true));
            var forced = new UserDevice { VendorId = vendor, ProdId = product, CapType = InputDeviceType.Joystick, ForceRawJoystickMode = true };
            Assert.False(ControllerDatabaseUse.KeepsRawRead(SdlMappingSource.Community, Pad, vendor, product, new[] { forced }, (_, _) => true));
            // A Bliss-Box port read raw is saved as a joystick by PadForge's
            // own switch, which decides how it opens (#469).
            var port = new UserDevice { VendorId = 0x16D0, ProdId = 0x0D04, CapType = InputDeviceType.Joystick };
            Assert.False(ControllerDatabaseUse.KeepsRawRead(SdlMappingSource.Community, Pad, 0x16D0, 0x0D04, new[] { port }, (_, _) => true));

            Assert.Equal(InputDeviceType.Joystick, SdlDeviceWrapper.InputDeviceTypeFor(SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD, false, vendor, product, true));
            Assert.Equal(InputDeviceType.Gamepad, SdlDeviceWrapper.InputDeviceTypeFor(SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD, false, vendor, product));
        }

        /// <summary>The raw read is kept whatever made SDL start mapping the
        /// controller: a HIDAPI driver that now claims it (the Xbox Nano
        /// Controller, upstream 74815f4fd7) or a new row of SDL's own table, as
        /// a community row does. A row of the user's, or a downloaded row the
        /// user took, is the user's choice. An arcade stick SDL types as one is
        /// a joystick by class while SDL reads it as a gamepad, so its saved
        /// class says nothing of a raw read.</summary>
        [Fact]
        public void AControllerMappedRaw_KeepsItsRawRead_WhateverMakesSdlMapIt()
        {
            const ushort vendor = 0x045E, product = 0x0B4E;
            var raw = new[] { new UserDevice { VendorId = vendor, ProdId = product, CapType = InputDeviceType.Joystick } };
            const SDL_JoystickType pad = SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD;
            Assert.True(ControllerDatabaseUse.KeepsRawRead(SdlMappingSource.Sdl, pad, vendor, product, raw, (_, _) => true));
            Assert.True(ControllerDatabaseUse.KeepsRawRead(SdlMappingSource.Community, pad, vendor, product, raw, (_, _) => true));
            Assert.True(ControllerDatabaseUse.KeepsRawRead(SdlMappingSource.PadForge, pad, vendor, product, raw, (_, _) => true));
            Assert.False(ControllerDatabaseUse.KeepsRawRead(SdlMappingSource.Yours, pad, vendor, product, raw, (_, _) => true));
            Assert.False(ControllerDatabaseUse.KeepsRawRead(SdlMappingSource.Downloaded, pad, vendor, product, raw, (_, _) => true));
            Assert.False(ControllerDatabaseUse.KeepsRawRead(SdlMappingSource.Sdl, SDL_JoystickType.SDL_JOYSTICK_TYPE_ARCADE_STICK,
                vendor, product, raw, (_, _) => true));
            // Force Raw Joystick Mode leaves the saved class at SDL's
            // (UserDevice.LoadCapabilities takes GetInputDeviceType), so a
            // pad with the switch turned off again opens as a gamepad.
            var offAgain = new[] { new UserDevice { VendorId = vendor, ProdId = product, CapType = InputDeviceType.Gamepad } };
            Assert.False(ControllerDatabaseUse.KeepsRawRead(SdlMappingSource.Community, pad, vendor, product, offAgain, (_, _) => true));
        }

        // ── As a joystick connects ──

        private static readonly HashSet<string> NoOwnIds = new();

        private static ControllerDatabaseUse.SdlAnswers Unmapped(string guid) =>
            new(guid, IsGamepad: false, SDL_JoystickType.SDL_JOYSTICK_TYPE_UNKNOWN, ReadRaw: false);

        private static ControllerDatabaseRow Offer(ControllerDatabaseSection section, ControllerDatabaseUse.SdlAnswers sdl,
            out bool firstUse, IReadOnlySet<string> ownIds = null, bool assigned = false, bool forcedRaw = false) =>
            ControllerDatabaseUse.NewRowFor(section, sdl, ownIds ?? NoOwnIds, (v, p) => assigned, (v, p) => forcedRaw, out firstUse);

        /// <summary>All five hold: the row is given, and it is its first
        /// use.</summary>
        [Fact]
        public void ANewRow_IsGivenForAJoystickNothingReadsAndNothingSavedNames()
        {
            var section = Section(Pinned(), NewRow);
            var row = Offer(section, Unmapped(NewId), out bool firstUse);
            Assert.NotNull(row);
            Assert.Equal(NewRow, row.Text);
            Assert.True(firstUse);
            // SDL types a controller of its list as a gamepad before any row
            // makes it one.
            Assert.NotNull(Offer(section, Unmapped(NewId) with { Type = SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD }, out _));
        }

        /// <summary>SDL reaches a row from a joystick's GUID with its name
        /// hash cleared, then its version too, so the row is found for a
        /// joystick that reports either. A joystick on the Bluetooth bus with
        /// no driver mark reaches it on the USB bus (SDL_gamepad.c:1652-1667).</summary>
        [Theory]
        [InlineData("03000000002000000100000000000000", true)]
        [InlineData("0300a1b2002000000100000000000000", true)]
        [InlineData("0300A1B2002000000100000011010000", true)]
        // The XInput backend's GUID for the same vendor and product.
        [InlineData("03000000002000000100000000007801", false)]
        // The Bluetooth bus.
        [InlineData("05000000002000000100000000000000", true)]
        // Another product.
        [InlineData("03000000002000000200000000000000", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void ANewRow_IsFoundByTheIdSdlLooksUp(string guid, bool found)
        {
            var section = Section(Pinned(), NewRow);
            Assert.Equal(found, Offer(section, Unmapped(guid), out _) != null);
        }

        /// <summary>The first condition: SDL reads the joystick as a
        /// gamepad already. Its own rows, the fork's, the XInput mapping
        /// and a driver's all answer so, and none is displaced.</summary>
        [Fact]
        public void ANewRow_IsNotGiven_WhenSdlReadsTheJoystickAsAGamepad()
        {
            var section = Section(Pinned(), NewRow);
            Assert.Null(Offer(section, Unmapped(NewId) with { IsGamepad = true }, out bool firstUse));
            Assert.False(firstUse);
        }

        /// <summary>The second: SDL types it as something with no gamepad's
        /// shape.</summary>
        [Theory]
        [InlineData(SDL_JoystickType.SDL_JOYSTICK_TYPE_WHEEL)]
        [InlineData(SDL_JoystickType.SDL_JOYSTICK_TYPE_ARCADE_STICK)]
        [InlineData(SDL_JoystickType.SDL_JOYSTICK_TYPE_FLIGHT_STICK)]
        [InlineData(SDL_JoystickType.SDL_JOYSTICK_TYPE_DANCE_PAD)]
        [InlineData(SDL_JoystickType.SDL_JOYSTICK_TYPE_GUITAR)]
        [InlineData(SDL_JoystickType.SDL_JOYSTICK_TYPE_DRUM_KIT)]
        [InlineData(SDL_JoystickType.SDL_JOYSTICK_TYPE_ARCADE_PAD)]
        [InlineData(SDL_JoystickType.SDL_JOYSTICK_TYPE_THROTTLE)]
        public void ANewRow_IsNotGiven_WhenSdlTypesTheJoystickAsNoGamepad(SDL_JoystickType type)
        {
            var section = Section(Pinned(), NewRow);
            Assert.Null(Offer(section, Unmapped(NewId) with { Type = type }, out bool firstUse));
            Assert.False(firstUse);
        }

        /// <summary>The third: PadForge's own file maps the controller, or
        /// PadForge reads it raw whatever SDL maps.</summary>
        [Fact]
        public void ANewRow_IsNotGiven_WhenPadForgesOwnFileHasTheController_OrPadForgeReadsItRaw()
        {
            var section = Section(Pinned(), NewRow);
            Assert.Null(Offer(section, Unmapped(NewId), out bool firstUse, ownIds: new HashSet<string> { NewId }));
            Assert.False(firstUse);
            Assert.Null(Offer(section, Unmapped(NewId) with { ReadRaw = true }, out firstUse));
            Assert.False(firstUse);
            // Another ID in PadForge's file changes nothing.
            Assert.NotNull(Offer(section, Unmapped(NewId), out _, ownIds: ControllerDatabaseUse.OwnIds(Own)));
        }

        /// <summary>The fourth: a saved assignment names the controller. It
        /// was read raw until now, and its mappings are by raw buttons.</summary>
        [Fact]
        public void ANewRow_IsNotGiven_WhenASavedAssignmentNamesTheController()
        {
            var section = Section(Pinned(), NewRow);
            Assert.Null(Offer(section, Unmapped(NewId), out bool firstUse, assigned: true));
            Assert.False(firstUse);
        }

        /// <summary>The fifth: Force Raw Joystick Mode is on for it.</summary>
        [Fact]
        public void ANewRow_IsNotGiven_WhenTheControllerIsForcedRaw()
        {
            var section = Section(Pinned(), NewRow);
            Assert.Null(Offer(section, Unmapped(NewId), out bool firstUse, forcedRaw: true));
            Assert.False(firstUse);
        }

        /// <summary>The saved state is asked of the row's vendor and
        /// product, and only once the first three hold.</summary>
        [Fact]
        public void TheSavedState_IsAskedOfTheRowsVendorAndProduct_AndOnlyWhenItDecides()
        {
            var section = Section(Pinned(), NewRow);
            var asked = new List<string>();
            bool Assigned(ushort v, ushort p) { asked.Add($"assigned {v:X4}:{p:X4}"); return false; }
            bool Raw(ushort v, ushort p) { asked.Add($"raw {v:X4}:{p:X4}"); return false; }

            Assert.NotNull(ControllerDatabaseUse.NewRowFor(section, Unmapped(NewId), NoOwnIds, Assigned, Raw, out _));
            Assert.Equal(new[] { "assigned 2000:0001", "raw 2000:0001" }, asked);

            asked.Clear();
            Assert.Null(ControllerDatabaseUse.NewRowFor(section, Unmapped(NewId) with { IsGamepad = true }, NoOwnIds, Assigned, Raw, out _));
            Assert.Null(ControllerDatabaseUse.NewRowFor(section, Unmapped(Id(0x9999, 0x0001)), NoOwnIds, Assigned, Raw, out _));
            Assert.Null(ControllerDatabaseUse.NewRowFor(null, Unmapped(NewId), NoOwnIds, Assigned, Raw, out _));
            Assert.Empty(asked);
        }

        /// <summary>A row in use was given under all five once, and the
        /// assignment made since was made through it. The saved state is
        /// not asked again, so the controller keeps the reading its
        /// mappings were made for. The first three still hold each time.</summary>
        [Fact]
        public void ARowInUse_IsGivenAgain_WhateverWasAssignedSince()
        {
            var section = Section(Pinned(), NewRow).WithRowInUse(NewId);
            var row = Offer(section, Unmapped(NewId), out bool firstUse, assigned: true, forcedRaw: true);
            Assert.NotNull(row);
            Assert.False(firstUse);

            Assert.Null(Offer(section, Unmapped(NewId) with { IsGamepad = true }, out _, assigned: true));
            Assert.Null(Offer(section, Unmapped(NewId) with { Type = SDL_JoystickType.SDL_JOYSTICK_TYPE_WHEEL }, out _));
            Assert.Null(Offer(section, Unmapped(NewId), out _, ownIds: new HashSet<string> { NewId }));
            Assert.Null(Offer(section, Unmapped(NewId) with { ReadRaw = true }, out _));
        }

        /// <summary>A changed row is never given this way: SDL has had it
        /// since it started.</summary>
        [Fact]
        public void AChangedRow_IsNotGivenAsAJoystickConnects()
        {
            var section = Section(Pinned(), KeptRow.Replace("a:b0", "a:b7"));
            Assert.Null(Offer(section, Unmapped(Id(0x1000, 0x0001)), out _));
        }

        // ── What counts as a saved assignment ──

        private static readonly Guid Unit = new("5b0c7e1a-9d43-4f2e-8a61-0c3d2e7f9a14");
        private static readonly Guid OtherUnit = new("6c1d8f2b-0e54-4a3f-9b72-1d4e3f8a0b25");

        private static UserDevice Device(Guid instance, ushort vendor, ushort product, bool forceRaw = false) => new()
        {
            InstanceGuid = instance, VendorId = vendor, ProdId = product, ForceRawJoystickMode = forceRaw,
        };

        private static bool Assigned(IEnumerable<UserDevice> devices = null, IEnumerable<UserSetting> assignments = null,
            IEnumerable<ProfileData> profiles = null, IEnumerable<GlobalMacroData> shortcuts = null) =>
            ControllerDatabaseUse.HasSavedAssignment(0x2000, 0x0001, devices, assignments, profiles, shortcuts);

        [Fact]
        public void AControllerNothingNames_HasNoSavedAssignment()
        {
            Assert.False(Assigned());
            // A saved record alone is no assignment: PadForge keeps one for
            // every controller it has seen.
            Assert.False(Assigned(new[] { Device(Unit, 0x2000, 0x0001) }));
            // Another model's assignment is not this one's.
            Assert.False(Assigned(new[] { Device(Unit, 0x2000, 0x0001), Device(OtherUnit, 0x2000, 0x0002) },
                new[] { new UserSetting { InstanceGuid = OtherUnit, MapTo = 0 } }));
            Assert.False(Assigned(new[] { Device(Unit, 0x2000, 0x0001) }, new UserSetting[] { null },
                new ProfileData[] { null, new ProfileData() }, new GlobalMacroData[] { null, new GlobalMacroData() }));
        }

        /// <summary>An assignment in force, and one parked with its mappings
        /// while another profile is active (MapTo below zero).</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void AnAssignmentToAVirtualController_IsASavedAssignment(int mapTo)
        {
            var devices = new[] { Device(Unit, 0x2000, 0x0001) };
            Assert.True(Assigned(devices, new[] { new UserSetting { InstanceGuid = Unit, MapTo = mapTo } }));
        }

        /// <summary>A row maps every controller of its ID, so another unit
        /// of the model counts: its raw mappings would change meaning the
        /// next time it connects.</summary>
        [Fact]
        public void AnAssignmentOfAnotherUnitOfTheModel_IsASavedAssignment()
        {
            var devices = new[] { Device(Unit, 0x2000, 0x0001), Device(OtherUnit, 0x2000, 0x0001) };
            Assert.True(Assigned(devices, new[] { new UserSetting { InstanceGuid = OtherUnit, MapTo = 2 } }));
        }

        /// <summary>An assignment that names the model by its product
        /// GUID, with no saved record of the unit.</summary>
        [Fact]
        public void AnAssignmentByProduct_IsASavedAssignment()
        {
            Guid product = SdlDeviceWrapper.BuildProductGuid(0x2000, 0x0001);
            Assert.True(Assigned(assignments: new[] { new UserSetting { InstanceGuid = Guid.NewGuid(), ProductGuid = product, MapTo = 0 } }));
            Assert.False(Assigned(assignments: new[]
            {
                new UserSetting { InstanceGuid = Guid.NewGuid(), ProductGuid = SdlDeviceWrapper.BuildProductGuid(0x2000, 0x0002), MapTo = 0 },
            }));
        }

        [Fact]
        public void AnEntryOfAStoredProfile_IsASavedAssignment()
        {
            var devices = new[] { Device(Unit, 0x2000, 0x0001) };
            var profile = new ProfileData { Entries = new[] { null, new ProfileEntry { InstanceGuid = Unit, MapTo = 1 } } };
            Assert.True(Assigned(devices, profiles: new[] { new ProfileData(), profile }));
            var byProduct = new ProfileData
            {
                Entries = new[] { new ProfileEntry { InstanceGuid = Guid.NewGuid(), ProductGuid = SdlDeviceWrapper.BuildProductGuid(0x2000, 0x0001) } },
            };
            Assert.True(Assigned(profiles: new[] { byProduct }));
        }

        /// <summary>A profile shortcut's trigger is a saved binding by raw
        /// button on a controller that needs no assignment.</summary>
        [Fact]
        public void AProfileShortcutsTrigger_IsASavedAssignment()
        {
            var devices = new[] { Device(Unit, 0x2000, 0x0001) };
            Assert.True(Assigned(devices, shortcuts: new[] { new GlobalMacroData { TriggerDeviceGuid = Unit } }));
            Assert.True(Assigned(devices, shortcuts: new[]
            {
                new GlobalMacroData { TriggerEntries = new[] { null, new TriggerButtonEntry { ButtonIndex = 3, DeviceInstanceGuid = Unit } } },
            }));
            Assert.True(Assigned(shortcuts: new[]
            {
                new GlobalMacroData
                {
                    TriggerEntries = new[] { new TriggerButtonEntry { DeviceProductGuid = SdlDeviceWrapper.BuildProductGuid(0x2000, 0x0001) } },
                },
            }));
            Assert.False(Assigned(devices, shortcuts: new[]
            {
                new GlobalMacroData { TriggerEntries = new[] { new TriggerButtonEntry { DeviceInstanceGuid = OtherUnit } } },
            }));
        }

        [Fact]
        public void ForceRaw_IsReadFromEverySavedControllerOfTheModel()
        {
            Assert.False(ControllerDatabaseUse.IsForcedRaw(0x2000, 0x0001, null));
            Assert.False(ControllerDatabaseUse.IsForcedRaw(0x2000, 0x0001, new[] { null, Device(Unit, 0x2000, 0x0001) }));
            Assert.False(ControllerDatabaseUse.IsForcedRaw(0x2000, 0x0001, new[] { Device(Unit, 0x2000, 0x0002, forceRaw: true) }));
            Assert.True(ControllerDatabaseUse.IsForcedRaw(0x2000, 0x0001,
                new[] { Device(Unit, 0x2000, 0x0001), Device(OtherUnit, 0x2000, 0x0001, forceRaw: true) }));
        }

        /// <summary>Step 1 offers the row before it opens the joystick,
        /// since the open is where SDL is asked whether it is a gamepad, and
        /// marks a row in use only for a device it keeps. What the offer
        /// reads of saved state comes through the collections' locks and
        /// the profile list's copy.</summary>
        [Fact]
        public void Step1_OffersTheRowBeforeItOpensTheJoystick()
        {
            string step1 = OffLimitsSource.Read("PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs");
            int offered = step1.IndexOf("OfferControllerDatabaseRow(instanceId);", StringComparison.Ordinal);
            int opened = step1.IndexOf("if (!wrapper.Open(instanceId))", StringComparison.Ordinal);
            Assert.True(offered > 0 && opened > offered, "the row is not offered before the joystick is opened");
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(step1, @"OfferControllerDatabaseRow\("));
            // A row used for the first time is marked only for a device
            // Step 1 keeps: past the self-readback guard, once tracked.
            int guarded = step1.IndexOf("if (_suppressedSelfVirtualIds.PassOver(instanceId,", StringComparison.Ordinal);
            int kept = step1.IndexOf("_openedSdlInstanceIds[wrapper.SdlInstanceId] = wrapper;", StringComparison.Ordinal);
            int marked = step1.IndexOf("MarkControllerDatabaseRowInUse(databaseRowFirstUsed);", StringComparison.Ordinal);
            Assert.True(guarded > opened && kept > guarded && marked > kept, "the row is marked before the device is kept");
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(step1, @"MarkControllerDatabaseRowInUse\("));

            string offer = OffLimitsSource.Read("PadForge.App/Common/Input/InputManager.ControllerDatabase.cs");
            // SDL's answer before the row, the row, then SDL's answer again.
            int asked = offer.IndexOf("SDL_IsGamepad(instanceId), SDL_GetJoystickTypeForID(instanceId)", StringComparison.Ordinal);
            int added = offer.IndexOf("SDL_AddGamepadMapping(row.Text)", StringComparison.Ordinal);
            int checkedAgain = offer.IndexOf("added >= 0 && SDL_IsGamepad(instanceId)", StringComparison.Ordinal);
            Assert.True(asked > 0 && added > asked && checkedAgain > added);
            Assert.Contains("lock (devices.SyncRoot) return devices.Items.ToArray();", offer, StringComparison.Ordinal);
            Assert.Contains("lock (settings.SyncRoot) assignments = settings.Items.ToArray();", offer, StringComparison.Ordinal);
            Assert.Contains("new List<ProfileData>(ProfilesSnapshot())", offer, StringComparison.Ordinal);

            // And SDL is given its startup rows in the tested order.
            string manager = OffLimitsSource.Read("PadForge.App/Common/Input/InputManager.cs");
            Assert.Contains("ControllerDatabaseUse.StartupRows(SettingsManager.ControllerDatabase, baseline, own)", manager, StringComparison.Ordinal);
            int init = manager.IndexOf("if (!SDL_Init(", StringComparison.Ordinal);
            int load = manager.IndexOf("LoadEmbeddedGamepadMappings();", StringComparison.Ordinal);
            Assert.True(init > 0 && load > init, "the rows are not given right after SDL starts");
        }
    }
}
