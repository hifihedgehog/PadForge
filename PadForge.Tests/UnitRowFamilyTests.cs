using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A lit device no mouse or keyboard row reaches gets a row of its own
    /// in every family that lights a device through a unit of its own
    /// (#557): LampArray, Razer, SteelSeries, the shared worker and HID++.
    /// Each rule of <see cref="PeripheralLinker.Orphans"/> is tested for
    /// each family, with the link of a unit row to its own unit and the
    /// row's name.
    /// </summary>
    public class UnitRowFamilyTests
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-0000000000a1");
        private static readonly Guid B = new("00000000-0000-0000-0000-0000000000b2");
        private static readonly Guid C = new("00000000-0000-0000-0000-0000000000c3");
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");
        private static readonly Guid OtherBox = new("99999999-2222-3333-4444-555555555555");
        private static readonly Guid Pc = PeripheralLinker.LocalMachineContainer;

        private const ushort Logitech = PeripheralLinker.LogitechVid;
        private const ushort Razer = PeripheralLinker.RazerVid;
        private const ushort SteelSeries = PeripheralLinker.SteelSeriesVid;
        private const ushort Lenovo = 0x048D;
        private const ushort Asus = 0x0B05;

        private static LinkRow Row(Guid device, int capType, ushort vid, ushort pid, Guid container)
            => new(device, capType, vid, pid, container, null);

        private static LinkRow UnitRow(Guid device, OutputPath unit)
            => new(device, InputDeviceType.PeripheralLighting, 0x5046, PeripheralUnitRow.UnitProductId, Guid.Empty, null)
            {
                Unit = unit,
            };

        private static PeripheralPresence Nothing => PeripheralPresence.None;

        private static PeripheralPresence Running(params string[] images)
            => PeripheralPresence.WithRunning(PeripheralPresence.None, images);

        private static LinkInputs Inputs(PeripheralPresence presence, params DirectUnit[] units)
            => new(HidppSnapshot.Empty, presence) { Units = units };

        private static LinkInputs Inputs(PeripheralPresence presence, HidppUnit[] hidpp, params DirectUnit[] units)
            => new(new HidppSnapshot(hidpp, Array.Empty<Guid>()), presence) { Units = units };

        /// <summary>A Razer Firefly V2 mousepad (openrazer
        /// daemon/openrazer_daemon/hardware/mouse_mat.py:23-29, OpenRGB
        /// RazerDevices.h:267): no mouse or keyboard collection.</summary>
        private static DirectUnit Mousepad(Guid container) => new(OutputFamily.RazerUnit, "firefly", container, Razer, 0x0C04)
        {
            Name = "Razer Firefly V2",
        };

        private static DirectUnit Rival(Guid container) => new(OutputFamily.SteelSeriesUnit, "rival", container, SteelSeries, 0x1870);

        /// <summary>An ASUS Aura motherboard controller of the shared worker
        /// (OpenRGB AsusAuraUSBControllerDetect.cpp:32 and 156) whose model
        /// names Armoury Crate.</summary>
        private static DirectUnit AuraStrip(Guid container) => new(OutputFamily.HidLightingUnit, "aura", container, Asus, 0x19AF)
        {
            Name = "ASUS Aura Motherboard",
            Programs = new[] { VendorPrograms.ArmouryCrate },
        };

        private static DirectUnit Lamp(string key, Guid container, ushort vid, ushort pid, int kind = 4)
            => new(OutputFamily.LampArrayUnit, key, container, vid, pid) { Kind = kind };

        /// <summary>A zone-only 0x8070 HID++ unit, a light plan of its own,
        /// named as the G560 speaker, which OpenRGB reaches on its 0xFF43
        /// HID++ collection (LogitechControllerDetect.cpp:1525 and 1779).</summary>
        private static HidppUnit Hidpp(byte index, int type, Guid container, string channel = "rx") => new()
        {
            ChannelPath = channel, ContainerId = container, DeviceIndex = index, DeviceType = type,
            Name = "G560 Gaming Speaker",
            RgbFeatureId = HidppUnitProtocol.ColorLedEffects, RgbIndex = 0x0E, Zones = new[] { new HidppZone(0, 1) },
        };

        // ── Every family, every rule ──

        /// <summary>Not reached by any row: an orphan, in every family that
        /// lights a unit of its own. The rumble-only TouchSense unit is
        /// none.</summary>
        [Fact]
        public void AUnitNoRowReaches_IsAnOrphan_InEveryLightingFamily()
        {
            var keyboard = Row(A, InputDeviceType.Keyboard, 0x1234, 0x0001, Box);
            var pad = Mousepad(OtherBox);
            var rival = Rival(OtherBox);
            var aura = AuraStrip(OtherBox);
            var lamp = Lamp("lamp", OtherBox, 0x2222, 0x0001);
            var buzz = new DirectUnit(OutputFamily.TouchSenseUnit, "buzz", OtherBox, Logitech, 0xC030);
            var speaker = Hidpp(0xFF, 8, OtherBox);

            var orphans = PeripheralLinker.Orphans(new[] { keyboard },
                Inputs(Nothing, new[] { speaker }, pad, rival, aura, lamp, buzz));
            Assert.Equal(new[] { pad, rival, aura, lamp, PeripheralLinker.UnitOf(speaker) }, orphans);
            Assert.All(new[] { OutputFamily.LampArrayUnit, OutputFamily.RazerUnit, OutputFamily.SteelSeriesUnit, OutputFamily.HidLightingUnit },
                family => Assert.True(PeripheralLinker.GetsUnitRow(family)));
            Assert.False(PeripheralLinker.GetsUnitRow(OutputFamily.TouchSenseUnit));
            Assert.False(PeripheralLinker.GetsUnitRow(OutputFamily.HidppUnit));
            Assert.False(PeripheralLinker.GetsUnitRow(OutputFamily.ChromaCategory));
        }

        /// <summary>A row of the unit's own device reaches it, by the rule
        /// its family links by: the same container, vendor and product.</summary>
        [Fact]
        public void AUnitTheRowOfItsOwnDeviceReaches_IsNoOrphan()
        {
            var pad = Mousepad(Box);
            var rival = Rival(Box);
            var aura = AuraStrip(Box);
            foreach (var unit in new[] { pad, rival, aura })
            {
                var own = Row(A, InputDeviceType.Mouse, unit.VendorId, unit.ProductId, Box);
                Assert.Empty(PeripheralLinker.Orphans(new[] { own }, Inputs(Nothing, unit)));
                // The keyboard collection of the same device too.
                Assert.Empty(PeripheralLinker.Orphans(new[] { own with { CapType = InputDeviceType.Keyboard } }, Inputs(Nothing, unit)));
                // Another product in the same container, another container,
                // or a row whose container is not known yet: not reached.
                Assert.Equal(new[] { unit }, PeripheralLinker.Orphans(new[] { own with { ProductId = 0x7777 } }, Inputs(Nothing, unit)));
                Assert.Equal(new[] { unit }, PeripheralLinker.Orphans(new[] { own with { Container = OtherBox } }, Inputs(Nothing, unit)));
                Assert.Equal(new[] { unit }, PeripheralLinker.Orphans(new[] { own with { Container = Guid.Empty } }, Inputs(Nothing, unit)));
                // A gamepad, a vendor row and another unit's row reach no unit.
                Assert.Equal(new[] { unit }, PeripheralLinker.Orphans(new[] { own with { CapType = InputDeviceType.Gamepad } }, Inputs(Nothing, unit)));
                Assert.Equal(new[] { unit }, PeripheralLinker.Orphans(new[] { own with { VendorRow = PeripheralRowKind.RazerChroma } }, Inputs(Nothing, unit)));
                Assert.Equal(new[] { unit }, PeripheralLinker.Orphans(new[] { UnitRow(B, Lamp("x", Box, 1, 1).Path) with { Container = Box } },
                    Inputs(Nothing, unit)));
            }
        }

        /// <summary>A unit is reached only by the rows its family is read
        /// for in the linker: a Razer unit by a Razer row, a SteelSeries
        /// unit by a SteelSeries row, a shared-worker unit by a row of any
        /// other vendor. A row the linker would never light the unit from
        /// leaves it an orphan, so it is never left with no row at all.</summary>
        [Fact]
        public void AUnitIsReached_OnlyByTheRowsItsFamilyIsReadFor()
        {
            // A shared-worker unit that lights the PC's own keyboard
            // through its firmware goes to the keyboard rows in the PC's
            // container, whatever their IDs.
            var builtIn = new DirectUnit(OutputFamily.HidLightingUnit, "omen", Pc, 0, 0) { BuiltInKeyboard = true };
            var keyboard = Row(A, InputDeviceType.Keyboard, 0x1234, 0x5678, Pc);
            Assert.Empty(PeripheralLinker.Orphans(new[] { keyboard }, Inputs(Nothing, builtIn)));
            Assert.Equal(new[] { new OutputPath(OutputFamily.HidLightingUnit, "omen") },
                PeripheralLinker.Build(new[] { keyboard }, Inputs(Nothing, builtIn)).For(A).Lighting);
            // The touchpad is no keyboard, and a keyboard outside the PC is
            // not the PC's.
            Assert.Equal(new[] { builtIn },
                PeripheralLinker.Orphans(new[] { Row(B, InputDeviceType.Mouse, 0x1234, 0x5678, Pc) }, Inputs(Nothing, builtIn)));
            Assert.Equal(new[] { builtIn },
                PeripheralLinker.Orphans(new[] { keyboard with { Container = Box } }, Inputs(Nothing, builtIn)));

            // The linker reads Logitech, Razer and SteelSeries rows for
            // their own families alone, so the same keyboard row with one of
            // those vendors never lights the unit, and leaves it an orphan.
            foreach (ushort vendor in new[] { Logitech, Razer, SteelSeries })
            {
                var theirs = keyboard with { VendorId = vendor };
                Assert.Null(PeripheralLinker.Build(new[] { theirs }, Inputs(Nothing, builtIn)).For(A));
                Assert.Equal(new[] { builtIn }, PeripheralLinker.Orphans(new[] { theirs }, Inputs(Nothing, builtIn)));
                Assert.False(PeripheralLinker.Reaches(theirs, builtIn, new Dictionary<Guid, int>()));
            }
            Assert.True(PeripheralLinker.Reaches(keyboard, builtIn, new Dictionary<Guid, int>()));

            // A Razer or SteelSeries unit is reached only by its vendor's
            // rows. MatchesDevice compares the vendor ID already, so this
            // holds by that rule too.
            var pad = Mousepad(Box);
            var rival = Rival(Box);
            Assert.False(PeripheralLinker.Reaches(Row(A, InputDeviceType.Mouse, SteelSeries, pad.ProductId, Box), pad, new Dictionary<Guid, int>()));
            Assert.False(PeripheralLinker.Reaches(Row(A, InputDeviceType.Mouse, Razer, rival.ProductId, Box), rival, new Dictionary<Guid, int>()));
            Assert.True(PeripheralLinker.Reaches(Row(A, InputDeviceType.Mouse, Razer, pad.ProductId, Box), pad, new Dictionary<Guid, int>()));
            Assert.True(PeripheralLinker.Reaches(Row(A, InputDeviceType.Mouse, SteelSeries, rival.ProductId, Box), rival, new Dictionary<Guid, int>()));
            // No family of another kind is reached by any row.
            Assert.False(PeripheralLinker.Reaches(Row(A, InputDeviceType.Mouse, Logitech, 0xC030, Box),
                new DirectUnit(OutputFamily.TouchSenseUnit, "t", Box, Logitech, 0xC030), new Dictionary<Guid, int>()));
        }

        /// <summary>A unit whose container is not known yet is no orphan:
        /// its row may be a lookup away.</summary>
        [Fact]
        public void AUnitWhoseContainerIsNotKnownYet_IsNoOrphan_InAnyFamily()
        {
            var none = Array.Empty<LinkRow>();
            Assert.Empty(PeripheralLinker.Orphans(none, Inputs(Nothing, new[] { Hidpp(0xFF, 8, Guid.Empty) },
                Mousepad(Guid.Empty), Rival(Guid.Empty), AuraStrip(Guid.Empty), Lamp("lamp", Guid.Empty, 0x2222, 1))));
            // Known, each is one.
            Assert.Equal(5, PeripheralLinker.Orphans(none, Inputs(Nothing, new[] { Hidpp(0xFF, 8, OtherBox) },
                Mousepad(Box), Rival(Box), AuraStrip(Box), Lamp("lamp", Box, 0x2222, 1))).Count);
        }

        /// <summary>A unit that is asleep keeps the row it has, so that row
        /// stays open while it sleeps, and gets none it has not: no row is
        /// opened for a device that has not answered.</summary>
        [Fact]
        public void AnAsleepUnit_IsAnOrphanOnlyWhileItHasARow_InEveryFamily()
        {
            foreach (var awake in new[] { Mousepad(Box), Rival(Box), AuraStrip(Box), Lamp("lamp", Box, 0x2222, 1) })
            {
                var asleep = awake with { Asleep = true };
                Assert.Empty(PeripheralLinker.Orphans(Array.Empty<LinkRow>(), Inputs(Nothing, asleep)));
                var own = UnitRow(A, asleep.Path);
                Assert.Equal(new[] { asleep }, PeripheralLinker.Orphans(new[] { own }, Inputs(Nothing, asleep)));
                // Another unit's row is not its own.
                Assert.Empty(PeripheralLinker.Orphans(new[] { UnitRow(A, new OutputPath(asleep.Family, "other")) }, Inputs(Nothing, asleep)));
                Assert.Empty(PeripheralLinker.Orphans(new[] { UnitRow(A, new OutputPath(OutputFamily.TouchSenseUnit, asleep.Key)) },
                    Inputs(Nothing, asleep)));
                // Awake, it is one with a row or without.
                Assert.Equal(new[] { awake }, PeripheralLinker.Orphans(Array.Empty<LinkRow>(), Inputs(Nothing, awake)));
                Assert.Equal(new[] { awake }, PeripheralLinker.Orphans(new[] { own }, Inputs(Nothing, awake)));
            }
        }

        /// <summary>While a program that drives the unit's device runs, the
        /// unit is no orphan: the vendor's program by vendor ID for a
        /// Logitech, Razer or SteelSeries device, the programs its model
        /// names for a unit of the shared worker. Each program takes its
        /// own devices and no other's.</summary>
        [Fact]
        public void AUnitIsNoOrphan_WhileAProgramThatDrivesItsDeviceRuns()
        {
            var none = Array.Empty<LinkRow>();
            var pad = Mousepad(Box);
            var rival = Rival(Box);
            var aura = AuraStrip(Box);
            var omen = new DirectUnit(OutputFamily.HidLightingUnit, "omen", Pc, 0x03F0, 0x0001)
            {
                Programs = new[] { VendorPrograms.OmenGamingHub },
            };
            var plain = new DirectUnit(OutputFamily.HidLightingUnit, "dygma", OtherBox, 0x1209, 0x2201);
            var speaker = Hidpp(0xFF, 8, OtherBox);
            var hidpp = new[] { speaker };
            var all = new[] { pad, rival, aura, omen, plain };
            DirectUnit Speaker() => PeripheralLinker.UnitOf(speaker);

            Assert.Equal(all.Append(Speaker()), PeripheralLinker.Orphans(none, Inputs(Running("explorer.exe"), hidpp, all)));
            Assert.Equal(new[] { rival, aura, omen, plain, Speaker() },
                PeripheralLinker.Orphans(none, Inputs(Running("RazerAppEngine.exe"), hidpp, all)));
            Assert.Equal(new[] { pad, aura, omen, plain, Speaker() },
                PeripheralLinker.Orphans(none, Inputs(Running("SteelSeriesGG.exe"), hidpp, all)));
            Assert.Equal(new[] { pad, rival, omen, plain, Speaker() },
                PeripheralLinker.Orphans(none, Inputs(Running("ArmouryCrate.Service.exe"), hidpp, all)));
            Assert.Equal(new[] { pad, rival, aura, plain, Speaker() },
                PeripheralLinker.Orphans(none, Inputs(Running("OmenCommandCenterBackground.exe"), hidpp, all)));
            Assert.Equal(all, PeripheralLinker.Orphans(none, Inputs(Running("lghub_agent.exe"), hidpp, all)));
            Assert.Equal(new[] { plain }, PeripheralLinker.Orphans(none, Inputs(Running("RazerAppEngine.exe", "SteelSeriesGG.exe",
                "ArmouryCrate.Service.exe", "OmenCommandCenterBackground.exe", "lghub_agent.exe"), hidpp, all)));

            // Installed is not running.
            Assert.Equal(all.Append(Speaker()),
                PeripheralLinker.Orphans(none, Inputs(new PeripheralPresence(true, false, true, true, true), hidpp, all)));
            // The unit's own read, the one behind its row's path too.
            Assert.True(PeripheralLinker.UnitProgramRuns(aura, Running("armourycrate.service.exe")));
            Assert.False(PeripheralLinker.UnitProgramRuns(aura, Running("explorer.exe")));
            Assert.True(PeripheralLinker.UnitProgramRuns(pad, Running("Razer Synapse 3.exe")));
            Assert.False(PeripheralLinker.UnitProgramRuns(plain, Running("RazerAppEngine.exe", "ArmouryCrate.Service.exe")));
            Assert.False(PeripheralLinker.UnitProgramRuns(null, Nothing));
            Assert.False(PeripheralLinker.UnitProgramRuns(aura, null));
        }

        /// <summary>A unit that stopped being an orphan, because a program
        /// started or a row reached it, waits out the whole hold again
        /// before it gets a row: the clock is the one the LampArray units
        /// keep.</summary>
        [Fact]
        public void AnOrphanOfAnyFamily_GetsARowOnlyAfterTheWholeHold_AndWaitsAgainAfterABreak()
        {
            var pad = Mousepad(Box);
            var mouse = Row(A, InputDeviceType.Mouse, Razer, pad.ProductId, Box);
            var since = new Dictionary<OutputPath, long>();
            DirectUnit[] Held(IReadOnlyList<LinkRow> rows, PeripheralPresence presence, long now)
                => PeripheralLinker.HeldOrphans(since, PeripheralLinker.Orphans(rows, Inputs(presence, pad)), now);
            var none = Array.Empty<LinkRow>();

            Assert.Empty(Held(none, Nothing, 1000));
            Assert.Empty(Held(none, Nothing, 1000 + PeripheralLinker.UnitRowHoldMs - 1));
            Assert.Equal(new[] { pad }, Held(none, Nothing, 1000 + PeripheralLinker.UnitRowHoldMs));
            // Synapse starts: listed no more, and the clock is dropped.
            Assert.Empty(Held(none, Running("RazerAppEngine.exe"), 7000));
            Assert.Empty(since);
            Assert.Empty(Held(none, Nothing, 8000));
            Assert.Empty(Held(none, Nothing, 8000 + PeripheralLinker.UnitRowHoldMs - 1));
            Assert.Equal(new[] { pad }, Held(none, Nothing, 8000 + PeripheralLinker.UnitRowHoldMs));
            // A mouse row of the device comes online: listed no more at once.
            Assert.Empty(Held(new[] { mouse }, Nothing, 20000));
            Assert.Empty(since);
        }

        // ── HID++ ──

        /// <summary>A HID++ unit with a light plan of its own that no
        /// Logitech row takes is an orphan: a speaker or a headset on a
        /// collection with no mouse or keyboard row, or a device on a
        /// receiver whose kind matches no row there. Its row's unit carries
        /// the name the device gave and no product ID, which the worker
        /// does not know.</summary>
        [Fact]
        public void AHidppUnitNoLogitechRowTakes_IsAnOrphan()
        {
            var mouse = Row(A, InputDeviceType.Mouse, Logitech, 0xC548, Box);
            var keyboard = Row(B, InputDeviceType.Keyboard, Logitech, 0xC548, Box);
            var receiverMouse = Hidpp(1, 3, Box);
            var stand = Hidpp(2, 7, Box);
            var speaker = Hidpp(0xFF, 8, OtherBox, channel: "speaker");
            var inputs = Inputs(Nothing, new[] { receiverMouse, stand, speaker });

            var orphans = PeripheralLinker.Orphans(new[] { mouse, keyboard }, inputs);
            Assert.Equal(new[] { PeripheralLinker.UnitOf(stand), PeripheralLinker.UnitOf(speaker) }, orphans);
            var unit = orphans[1];
            Assert.Equal(new OutputPath(OutputFamily.HidppUnit, "speaker|FF"), unit.Path);
            Assert.Equal(OtherBox, unit.ContainerId);
            Assert.Equal(Logitech, unit.VendorId);
            Assert.Equal(0, unit.ProductId);
            Assert.Equal("G560 Gaming Speaker", unit.Name);
            Assert.False(unit.Asleep);
            Assert.Empty(unit.Programs);

            // A unit that gave no kind goes to every Logitech row in its
            // container, so it is reached.
            Assert.Empty(PeripheralLinker.Orphans(new[] { mouse }, Inputs(Nothing, new[] { Hidpp(3, -1, Box) })));
            // A row of another vendor in the container reaches none of them.
            Assert.Equal(new[] { PeripheralLinker.UnitOf(receiverMouse) },
                PeripheralLinker.Orphans(new[] { mouse with { VendorId = Razer } }, Inputs(Nothing, new[] { receiverMouse })));
            // Nor does a gamepad of Logitech's, or a row with no container yet.
            Assert.Single(PeripheralLinker.Orphans(new[] { mouse with { CapType = InputDeviceType.Gamepad } },
                Inputs(Nothing, new[] { receiverMouse })));
            Assert.Single(PeripheralLinker.Orphans(new[] { mouse with { Container = Guid.Empty } },
                Inputs(Nothing, new[] { receiverMouse })));
            Assert.True(PeripheralLinker.Reaches(mouse, receiverMouse));
            Assert.False(PeripheralLinker.Reaches(UnitRow(C, PeripheralLinker.UnitOf(receiverMouse).Path) with { Container = Box, VendorId = Logitech, CapType = InputDeviceType.Mouse },
                receiverMouse));
            Assert.False(PeripheralLinker.Reaches(mouse with { VendorRow = PeripheralRowKind.LogitechLightsync }, receiverMouse));
        }

        /// <summary>Only a HID++ unit the linker could light on its own is
        /// an orphan: one with a light plan, its container known, and no
        /// Logitech program running.</summary>
        [Fact]
        public void AHidppUnitIsNoOrphan_WithoutALightPlan_ItsContainer_OrWhileGHubRuns()
        {
            var none = Array.Empty<LinkRow>();
            var speaker = Hidpp(0xFF, 8, OtherBox);
            Assert.Single(PeripheralLinker.Orphans(none, Inputs(Nothing, new[] { speaker })));
            // RGB it cannot paint, no RGB at all, or rumble alone.
            var unplanned = speaker with { RgbFeatureId = HidppUnitProtocol.RgbEffects, Zones = Array.Empty<HidppZone>() };
            var dark = speaker with { RgbFeatureId = 0, Zones = Array.Empty<HidppZone>() };
            var buzz = dark with { HapticIndex = 0x0B, WaveformMask = 0x7FFF };
            Assert.Empty(PeripheralLinker.Orphans(none, Inputs(Nothing, new[] { unplanned, dark, buzz })));
            Assert.Empty(PeripheralLinker.Orphans(none, Inputs(Nothing, new[] { speaker with { ContainerId = Guid.Empty } })));
            // G HUB, or Logitech Gaming Software, runs.
            Assert.Empty(PeripheralLinker.Orphans(none, Inputs(Running("lghub_agent.exe"), new[] { speaker })));
            Assert.Empty(PeripheralLinker.Orphans(none, Inputs(Running("LCore.exe"), new[] { speaker })));
            Assert.Single(PeripheralLinker.Orphans(none, Inputs(Running("RazerAppEngine.exe"), new[] { speaker })));
            // A 0x8071 unit with a cluster to paint is one.
            var planned = speaker with
            {
                RgbFeatureId = HidppUnitProtocol.RgbEffects, EffectFeatureId = HidppUnitProtocol.RgbEffects, EffectIndex = 0x0C,
            };
            Assert.Single(PeripheralLinker.Orphans(none, Inputs(Nothing, new[] { planned })));
        }

        // ── One family per device ──

        /// <summary>One device can carry a LampArray collection beside its
        /// vendor's own. A row lights it through one family at a time, so
        /// only one family's units of it get rows: the LampArray units while
        /// Windows lights one of them and no unit of the device's own stays
        /// in charge beside Windows, else the device's own.</summary>
        [Fact]
        public void OfADevicesUnitsOfTwoFamilies_OnlyTheFamilyARowWouldTakeGetsRows()
        {
            var none = Array.Empty<LinkRow>();
            var pad = Mousepad(Box);
            var lamp = Lamp("lamp", Box, Razer, pad.ProductId);
            var lit = lamp with { WindowsLit = true };

            Assert.Equal(new[] { pad }, PeripheralLinker.Orphans(none, Inputs(Nothing, lamp, pad)));
            Assert.Equal(new[] { lit }, PeripheralLinker.Orphans(none, Inputs(Nothing, lit, pad)));
            // A unit of its own that stays in charge beside Windows.
            var beside = pad with { LitBesideWindows = true };
            Assert.Equal(new[] { beside }, PeripheralLinker.Orphans(none, Inputs(Nothing, lit, beside)));

            // HID++ and the shared worker the same way.
            var speaker = Hidpp(0xFF, 8, Box);
            var logiLamp = Lamp("logi", Box, Logitech, 0x0A78);
            Assert.Equal(new[] { PeripheralLinker.UnitOf(speaker) },
                PeripheralLinker.Orphans(none, Inputs(Nothing, new[] { speaker }, logiLamp)));
            Assert.Equal(new[] { logiLamp with { WindowsLit = true } },
                PeripheralLinker.Orphans(none, Inputs(Nothing, new[] { speaker }, logiLamp with { WindowsLit = true })));
            // A row the HID++ unit already has holds no path then.
            Assert.Null(PeripheralLinker.Build(new[] { UnitRow(B, PeripheralLinker.UnitOf(speaker).Path) },
                Inputs(Nothing, new[] { speaker }, logiLamp with { WindowsLit = true })).For(B));
            Assert.NotNull(PeripheralLinker.Build(new[] { UnitRow(B, PeripheralLinker.UnitOf(speaker).Path) },
                Inputs(Nothing, new[] { speaker }, logiLamp)).For(B));
            var aura = AuraStrip(Box);
            var auraLamp = Lamp("auralamp", Box, Asus, 0x19AF);
            Assert.Equal(new[] { aura }, PeripheralLinker.Orphans(none, Inputs(Nothing, auraLamp, aura)));

            // Another vendor in the same container is another device, and
            // a lone family is never touched.
            var other = Lamp("other", Box, 0x2222, 0x0001);
            Assert.Equal(new[] { other, pad }, PeripheralLinker.Orphans(none, Inputs(Nothing, other, pad)));
            // The same vendor in another container is another device.
            var far = lamp with { ContainerId = OtherBox };
            Assert.Equal(new[] { far, pad }, PeripheralLinker.Orphans(none, Inputs(Nothing, far, pad)));
            Assert.Equal(new[] { lamp, lit with { Key = "lit" } },
                PeripheralLinker.Orphans(none, Inputs(Nothing, lamp, lit with { Key = "lit" })));
            // In the PC's own container every built-in device sits
            // together, so nothing is merged there.
            var pcLamp = Lamp("pclamp", Pc, Lenovo, 0xC193, kind: 7);
            var pcUnit = new DirectUnit(OutputFamily.HidLightingUnit, "pcunit", Pc, Lenovo, 0xC197);
            Assert.Equal(new[] { pcLamp, pcUnit }, PeripheralLinker.Orphans(none, Inputs(Nothing, pcLamp, pcUnit)));
        }

        /// <summary>The family is chosen as if every unit answered, as a
        /// row's is. A device whose own unit sleeps gets no row through its
        /// LampArray unit in the meantime, which it would hand back when
        /// the unit wakes, and a row its own unit already has stays.</summary>
        [Fact]
        public void TheFamilyIsChosenAsIfEveryUnitAnswered()
        {
            var pad = Mousepad(Box) with { Asleep = true };
            var lamp = Lamp("lamp", Box, Razer, pad.ProductId);
            Assert.Empty(PeripheralLinker.Orphans(Array.Empty<LinkRow>(), Inputs(Nothing, lamp, pad)));
            Assert.Equal(new[] { pad }, PeripheralLinker.Orphans(new[] { UnitRow(A, pad.Path) }, Inputs(Nothing, lamp, pad)));
            // A row the LampArray unit had stays no longer: the device is
            // the native unit's.
            Assert.Empty(PeripheralLinker.Orphans(new[] { UnitRow(A, lamp.Path) }, Inputs(Nothing, lamp, pad)));
            // Windows lights the LampArray unit: that is the device's
            // family, asleep native unit or not.
            Assert.Equal(new[] { lamp with { WindowsLit = true } },
                PeripheralLinker.Orphans(Array.Empty<LinkRow>(), Inputs(Nothing, lamp with { WindowsLit = true }, pad)));
        }

        // ── The unit row's link ──

        /// <summary>A unit row holds its own unit and nothing else, in
        /// every family, for as long as the unit is there and answers and
        /// no program that drives its device runs. It ranks as a device,
        /// asks for colors alone, and a program that could take it marks it
        /// watched for.</summary>
        [Fact]
        public void AUnitRow_LinksExactlyItsOwnUnit_InEveryFamily()
        {
            var pad = Mousepad(Box);
            var rival = Rival(OtherBox);
            var aura = AuraStrip(Pc);
            var plain = new DirectUnit(OutputFamily.HidLightingUnit, "dygma", OtherBox, 0x1209, 0x2201);
            var all = new[] { pad, rival, aura, plain };
            foreach (var unit in all)
            {
                var row = UnitRow(A, unit.Path);
                var links = PeripheralLinker.Build(new[] { row }, Inputs(Nothing, all)).For(A);
                Assert.Equal(new[] { unit.Path }, links.Lighting);
                Assert.Empty(links.Haptics);
                Assert.Empty(links.HidppUnits);
                Assert.True(links.UnitRow && links.OutputOnly);
                Assert.False(links.CatchAll);
                Assert.Equal(0, links.MaxClaimLevel);
                Assert.Null(links.LeftToProgram);
                Assert.Equal(unit != plain, links.HasVendorProgram);

                // Asleep, or gone: no path.
                Assert.Null(PeripheralLinker.Build(new[] { row },
                    Inputs(Nothing, all.Select(u => u == unit ? u with { Asleep = true } : u).ToArray())).For(A));
                Assert.Null(PeripheralLinker.Build(new[] { row }, Inputs(Nothing, all.Where(u => u != unit).ToArray())).For(A));
                // Of another family under the same key: not its unit.
                Assert.Null(PeripheralLinker.Build(new[] { UnitRow(A, new OutputPath(OutputFamily.LampArrayUnit, unit.Key)) },
                    Inputs(Nothing, all)).For(A));
            }

            // A program that drives the device runs: no path, from the pass
            // that sees it, and only for that program's devices.
            var running = Running("RazerAppEngine.exe", "ArmouryCrate.Service.exe");
            Assert.Null(PeripheralLinker.Build(new[] { UnitRow(A, pad.Path) }, Inputs(running, all)).For(A));
            Assert.Null(PeripheralLinker.Build(new[] { UnitRow(A, aura.Path) }, Inputs(running, all)).For(A));
            Assert.Equal(new[] { rival.Path }, PeripheralLinker.Build(new[] { UnitRow(A, rival.Path) }, Inputs(running, all)).For(A).Lighting);
            Assert.Null(PeripheralLinker.Build(new[] { UnitRow(A, rival.Path) }, Inputs(Running("SteelSeriesGGClient.exe"), all)).For(A));
            Assert.Equal(new[] { plain.Path }, PeripheralLinker.Build(new[] { UnitRow(A, plain.Path) }, Inputs(running, all)).For(A).Lighting);
        }

        /// <summary>A HID++ unit's row holds that unit's path, read from
        /// the HID++ worker's list, carries the unit for the Battery mode,
        /// and lets go while a Logitech program runs.</summary>
        [Fact]
        public void AHidppUnitRow_LinksItsUnit_FromTheHidppWorkersList()
        {
            var speaker = Hidpp(0xFF, 8, OtherBox) with { MaxClaimLevel = 2 };
            var other = Hidpp(1, 7, Box);
            var hidpp = new[] { other, speaker };
            var path = PeripheralLinker.UnitOf(speaker).Path;
            var row = UnitRow(A, path);

            var links = PeripheralLinker.Build(new[] { row }, Inputs(Nothing, hidpp)).For(A);
            Assert.Equal(new[] { path }, links.Lighting);
            Assert.Equal(new[] { speaker.Key }, links.HidppUnits);
            Assert.Empty(links.Haptics);
            Assert.True(links.UnitRow);
            Assert.True(links.HasVendorProgram);
            // A unit row asks for colors alone, whatever the unit offers.
            Assert.Equal(0, links.MaxClaimLevel);

            // G HUB runs: no path, and no row in the table.
            Assert.Null(PeripheralLinker.Build(new[] { row }, Inputs(Running("lghub_agent.exe"), hidpp)).For(A));
            // The unit left the worker's list, asleep or gone.
            Assert.Null(PeripheralLinker.Build(new[] { row }, Inputs(Nothing, new[] { other })).For(A));
            // It lost its light plan: nothing to light.
            Assert.Null(PeripheralLinker.Build(new[] { row },
                Inputs(Nothing, new[] { speaker with { RgbFeatureId = 0, Zones = Array.Empty<HidppZone>() } })).For(A));
            // A direct unit under the same key is not it.
            Assert.Null(PeripheralLinker.Build(new[] { row }, Inputs(Nothing,
                new DirectUnit(OutputFamily.RazerUnit, speaker.Key, OtherBox, Razer, 1))).For(A));
        }

        /// <summary>A mouse or keyboard row of the device that comes online
        /// takes the device back from its unit row in the same pass: the
        /// unit row's path closes, so no path is held by two rows, the unit
        /// is no orphan, so the row retires at the input manager's next
        /// scan, and the device's own row links it.</summary>
        [Fact]
        public void ARowOfTheDevice_TakesItBackFromItsUnitRow_InEveryFamily()
        {
            var pad = Mousepad(Box);
            var rival = Rival(Box);
            var aura = AuraStrip(Box);
            foreach (var unit in new[] { pad, rival, aura })
            {
                var unitRow = UnitRow(A, unit.Path);
                Assert.Equal(new[] { unit }, PeripheralLinker.Orphans(new[] { unitRow }, Inputs(Nothing, unit)));
                var mouse = Row(B, InputDeviceType.Mouse, unit.VendorId, unit.ProductId, Box);
                Assert.Empty(PeripheralLinker.Orphans(new[] { unitRow, mouse }, Inputs(Nothing, unit)));
                var table = PeripheralLinker.Build(new[] { unitRow, mouse }, Inputs(Nothing, unit));
                Assert.Equal(new[] { unit.Path }, table.For(B).Lighting);
                Assert.Null(table.For(A));
                Assert.Equal(new[] { B }, table.ByPath[unit.Path]);
                // The order the rows come in changes nothing.
                Assert.Null(PeripheralLinker.Build(new[] { mouse, unitRow }, Inputs(Nothing, unit)).For(A));
                // A row that does not reach the unit leaves the unit row lit.
                Assert.Equal(new[] { unit.Path }, PeripheralLinker.Build(new[] { unitRow, mouse with { Container = OtherBox } },
                    Inputs(Nothing, unit)).For(A).Lighting);
            }

            var speaker = Hidpp(1, 7, Box);
            var hidppRow = UnitRow(A, PeripheralLinker.UnitOf(speaker).Path);
            Assert.Single(PeripheralLinker.Orphans(new[] { hidppRow }, Inputs(Nothing, new[] { speaker })));
            // The probe reads the kind again and the device is a mouse now.
            var asMouse = speaker with { DeviceType = 3 };
            var logitech = Row(B, InputDeviceType.Mouse, Logitech, 0xC548, Box);
            Assert.Empty(PeripheralLinker.Orphans(new[] { hidppRow, logitech }, Inputs(Nothing, new[] { asMouse })));
            var hidppTable = PeripheralLinker.Build(new[] { hidppRow, logitech }, Inputs(Nothing, new[] { asMouse }));
            Assert.Equal(new[] { PeripheralLinker.UnitOf(asMouse).Path }, hidppTable.For(B).Lighting);
            Assert.Null(hidppTable.For(A));

            // A LampArray unit the same way.
            var headset = Lamp("headset", OtherBox, 0x2222, 0x0001);
            var lampRow = UnitRow(A, headset.Path);
            var keyboard = Row(B, InputDeviceType.Keyboard, 0x2222, 0x0001, OtherBox);
            var lampTable = PeripheralLinker.Build(new[] { lampRow, keyboard }, Inputs(Nothing, headset));
            Assert.Equal(new[] { headset.Path }, lampTable.For(B).Lighting);
            Assert.Null(lampTable.For(A));
            Assert.Equal(new[] { headset.Path }, PeripheralLinker.Build(new[] { lampRow }, Inputs(Nothing, headset)).For(A).Lighting);
        }

        /// <summary>A device whose own row lights it through its vendor's
        /// unit gives no row to a LampArray unit of the same device that
        /// row leaves unreached, a strip beside the keys, and a unit row it
        /// already has holds no path: two families would write to one
        /// device. The order is <see cref="PeripheralLinker.ChooseLighting"/>'s,
        /// so while Windows lights a LampArray unit of the device and no
        /// unit of its own stays in charge beside Windows, the vendor's unit
        /// is the one left out.</summary>
        [Fact]
        public void ALampArrayUnit_BesideAUnitTheDevicesRowLights_GetsNoRow()
        {
            var keyboard = Row(A, InputDeviceType.Keyboard, Razer, 0x0C04, Box);
            var native = Mousepad(Box);
            var keys = Lamp("keys", Box, Razer, 0x0C04, kind: 1);
            var strip = Lamp("strip", Box, Razer, 0x0C04, kind: 4);
            var inputs = Inputs(Nothing, native, keys, strip);

            // The row lights the device through its vendor's unit, and the
            // strip is reached by no row.
            Assert.Equal(new[] { native.Path }, PeripheralLinker.Build(new[] { keyboard }, inputs).For(A).Lighting);
            Assert.False(PeripheralLinker.Reaches(keyboard, strip, PeripheralLinker.LampArraysPerContainer(inputs.Units)));
            Assert.Empty(PeripheralLinker.Orphans(new[] { keyboard }, inputs));
            // A row the strip already has holds no path.
            var stripRow = UnitRow(B, strip.Path);
            Assert.Null(PeripheralLinker.Build(new[] { keyboard, stripRow }, inputs).For(B));
            Assert.Empty(PeripheralLinker.Orphans(new[] { keyboard, stripRow }, inputs));

            // The vendor's program runs: the row is the program's, and the
            // strip is still the device's.
            Assert.Empty(PeripheralLinker.Orphans(new[] { keyboard }, Inputs(Running("RazerAppEngine.exe"), native, keys, strip)));
            // The vendor's unit sleeps: the family is the device's whatever
            // its unit does.
            Assert.Empty(PeripheralLinker.Orphans(new[] { keyboard }, Inputs(Nothing, native with { Asleep = true }, keys, strip)));

            // Windows lights a LampArray unit of the device: the row is lit
            // through the LampArray units, and the strip, of that family,
            // gets its row.
            var litKeys = keys with { WindowsLit = true };
            var windows = Inputs(Nothing, native, litKeys, strip);
            Assert.Equal(new[] { litKeys.Path }, PeripheralLinker.Build(new[] { keyboard }, windows).For(A).Lighting);
            Assert.Equal(new[] { strip }, PeripheralLinker.Orphans(new[] { keyboard }, windows));
            Assert.Equal(new[] { strip.Path }, PeripheralLinker.Build(new[] { keyboard, stripRow }, windows).For(B).Lighting);
            // Unless the device's own unit stays in charge beside Windows.
            var beside = Inputs(Nothing, native with { LitBesideWindows = true }, litKeys, strip);
            Assert.Empty(PeripheralLinker.Orphans(new[] { keyboard }, beside));

            // The family a device row links wins: Windows lights a strip no
            // row reaches, the keyboard row's own LampArray unit is not lit
            // by Windows, so the row links the vendor's unit, and neither
            // the lit strip nor a bar beside it gets a row.
            var litStrip = strip with { WindowsLit = true };
            var bar = Lamp("bar", Box, Razer, 0x0C04, kind: 7);
            var mixed = Inputs(Nothing, native, keys, litStrip, bar);
            Assert.Equal(new[] { native.Path }, PeripheralLinker.Build(new[] { keyboard }, mixed).For(A).Lighting);
            Assert.Empty(PeripheralLinker.Orphans(new[] { keyboard }, mixed));
            Assert.Null(PeripheralLinker.Build(new[] { keyboard, UnitRow(B, bar.Path) }, mixed).For(B));
            // Whatever order the rows come in.
            Assert.Null(PeripheralLinker.Build(new[] { UnitRow(B, bar.Path), keyboard }, mixed).For(B));
            // With no keyboard row, the census decides: Windows lights the
            // strip, so the LampArray units are the device's.
            Assert.Equal(new[] { keys, litStrip, bar }, PeripheralLinker.Orphans(Array.Empty<LinkRow>(), mixed));
            // A row that links the LampArray units makes the vendor's unit
            // lose, whatever the census says.
            var lampKeyboard = Row(A, InputDeviceType.Keyboard, Razer, 0x7777, Box);
            var keysOnly = Inputs(Nothing, native, keys);
            Assert.Equal(new[] { keys.Path }, PeripheralLinker.Build(new[] { lampKeyboard }, keysOnly).For(A).Lighting);
            Assert.Empty(PeripheralLinker.Orphans(new[] { lampKeyboard }, keysOnly));
            Assert.Null(PeripheralLinker.Build(new[] { lampKeyboard, UnitRow(B, native.Path) }, keysOnly).For(B));

            // A shared-worker device and a HID++ device the same way.
            var aura = AuraStrip(Box);
            var auraRow = Row(A, InputDeviceType.Keyboard, Asus, aura.ProductId, Box);
            var auraKeys = Lamp("aurakeys", Box, Asus, aura.ProductId, kind: 1);
            var auraStrip = Lamp("aurastrip", Box, Asus, aura.ProductId, kind: 4);
            Assert.Empty(PeripheralLinker.Orphans(new[] { auraRow }, Inputs(Nothing, aura, auraKeys, auraStrip)));
            // Its program runs and holds the device: still the device's.
            Assert.Empty(PeripheralLinker.Orphans(new[] { auraRow },
                Inputs(Running("ArmouryCrate.Service.exe"), aura, auraKeys, auraStrip)));
            var mouse = Row(A, InputDeviceType.Mouse, Logitech, 0xC548, Box);
            var logiMouse = Hidpp(1, 3, Box);
            var logiKeys = Lamp("logikeys", Box, Logitech, 0xC548, kind: 2);
            var logiStrip = Lamp("logistrip", Box, Logitech, 0xC548, kind: 4);
            Assert.Empty(PeripheralLinker.Orphans(new[] { mouse }, Inputs(Nothing, new[] { logiMouse }, logiKeys, logiStrip)));

            // Another vendor's strip in the container is another device.
            var other = Lamp("other", Box, 0x2222, 0x0001, kind: 4);
            Assert.Equal(new[] { other }, PeripheralLinker.Orphans(new[] { keyboard }, Inputs(Nothing, native, keys, other)));
            // The PC's own container is never merged by vendor.
            var pcKeyboard = Row(A, InputDeviceType.Keyboard, Lenovo, 0xC197, Pc);
            var spectrum = new DirectUnit(OutputFamily.HidLightingUnit, "spectrum", Pc, Lenovo, 0xC197);
            var pcKeys = Lamp("pckeys", Pc, Lenovo, 0xC193, kind: 1);
            var pcBar = Lamp("pcbar", Pc, Lenovo, 0xC196, kind: 7);
            Assert.Equal(new[] { pcBar }, PeripheralLinker.Orphans(new[] { pcKeyboard }, Inputs(Nothing, spectrum, pcKeys, pcBar)));
        }

        // ── Names ──

        /// <summary>A unit row is named by the product name the unit's
        /// backend knows, and where there is none by its kind and IDs. A
        /// product ID its backend does not know is left out.</summary>
        [Fact]
        public void AUnitRow_IsNamedByItsBackendsProductName()
        {
            Assert.Equal("Razer Firefly V2", new PeripheralUnitRow(Mousepad(Box)).Name);
            Assert.Equal("ASUS Aura Motherboard", new PeripheralUnitRow(AuraStrip(Box)).Name);
            Assert.Equal("G560 Gaming Speaker", new PeripheralUnitRow(PeripheralLinker.UnitOf(Hidpp(0xFF, 8, Box))).Name);
            Assert.Equal("Device 1038:1870", new PeripheralUnitRow(Rival(Box)).Name);
            Assert.Equal("Device 046D", PeripheralUnitRow.NameFor(PeripheralLinker.UnitOf(Hidpp(0xFF, 8, Box) with { Name = null })));
            Assert.Equal("LampArray 2222", PeripheralUnitRow.NameFor(Lamp("x", Box, 0x2222, 0)));
            // The row of a HID++ unit is the same row in every session.
            var path = PeripheralLinker.UnitOf(Hidpp(0xFF, 8, Box, channel: "speaker")).Path;
            Assert.Equal(PeripheralUnitRow.IdentityFor(OutputFamily.HidppUnit, "speaker|FF"),
                new PeripheralUnitRow(PeripheralLinker.UnitOf(Hidpp(0xFF, 8, OtherBox, channel: "SPEAKER") with { Name = "Other" })).InstanceGuid);
            Assert.Equal(OutputFamily.HidppUnit, path.Family);
        }

        // ── Over generated inputs ──

        private static readonly ushort[] Vendors = { Logitech, Razer, SteelSeries, Asus, Lenovo };
        private static readonly ushort[] Products = { 0x0C04, 0x1870, 0x19AF, 0xC548, 0xC195 };
        private static readonly int[] RowTypes = { InputDeviceType.Mouse, InputDeviceType.Keyboard, InputDeviceType.AnalogKeyboard, InputDeviceType.Gamepad };
        private static readonly Guid[] Containers = { Box, OtherBox, Pc, Guid.Empty };

        private static T Pick<T>(Random random, T[] from) => from[random.Next(from.Length)];

        /// <summary>Whatever the PC holds: no unit that gets a row of its
        /// own is lit by a device row too, a row made for any of them links
        /// that unit and nothing else, and no device outside the PC gets
        /// rows of two families.</summary>
        [Fact]
        public void AnOrphanIsNeverLitByADeviceRow_AndItsRowLightsItAlone_OverGeneratedInputs()
        {
            var random = new Random(557);
            var families = new[]
            {
                OutputFamily.LampArrayUnit, OutputFamily.RazerUnit, OutputFamily.SteelSeriesUnit,
                OutputFamily.HidLightingUnit, OutputFamily.TouchSenseUnit,
            };
            var images = new[] { "RazerAppEngine.exe", "SteelSeriesGG.exe", "lghub_agent.exe", "ArmouryCrate.Service.exe", "explorer.exe" };
            int orphansSeen = 0;
            for (int round = 0; round < 4000; round++)
            {
                var rows = new List<LinkRow>();
                for (int i = random.Next(0, 5); i > 0; i--)
                    rows.Add(Row(new Guid(i, 0, 0, new byte[8]), Pick(random, RowTypes), Pick(random, Vendors),
                        Pick(random, Products), Pick(random, Containers)));
                var direct = new DirectUnit[random.Next(0, 6)];
                for (int i = 0; i < direct.Length; i++)
                    direct[i] = new DirectUnit(Pick(random, families), "unit" + i, Pick(random, Containers),
                        Pick(random, Vendors), Pick(random, Products))
                    {
                        Kind = random.Next(0, 5), WindowsLit = random.Next(3) == 0, Asleep = random.Next(4) == 0,
                        LitBesideWindows = random.Next(4) == 0, BuiltInKeyboard = random.Next(5) == 0,
                        Programs = random.Next(3) == 0 ? new[] { VendorPrograms.ArmouryCrate } : Array.Empty<VendorProgram>(),
                    };
                // Each HID++ unit on a channel of its own: a channel path is
                // one collection and sits in one container, and two units in
                // two containers under one key are no PC's.
                var hidpp = new HidppUnit[random.Next(0, 3)];
                for (int i = 0; i < hidpp.Length; i++)
                    hidpp[i] = Hidpp((byte)(random.Next(3) == 0 ? 0xFF : i + 1), Pick(random, new[] { -1, 0, 3, 7, 8 }),
                        Pick(random, Containers), channel: "rx" + i);
                // Some unit rows, for units listed or not.
                foreach (var unit in direct)
                    if (random.Next(3) == 0) rows.Add(UnitRow(new Guid(100 + rows.Count, 0, 0, new byte[8]), unit.Path));
                var presence = Running(images.Where(_ => random.Next(4) == 0).ToArray());
                var inputs = Inputs(presence, hidpp, direct);

                var orphans = PeripheralLinker.Orphans(rows, inputs);
                orphansSeen += orphans.Count;
                var table = PeripheralLinker.Build(rows, inputs);
                // The unit each lit path names, of either list, as a unit
                // row's unit carries it.
                DirectUnit UnitAt(OutputPath path)
                    => path.Family == OutputFamily.HidppUnit
                        ? hidpp.Where(h => h.Key == path.Key).Select(PeripheralLinker.UnitOf).FirstOrDefault()
                        : direct.FirstOrDefault(u => u.Path == path);
                var byDeviceRows = rows.Where(r => r.Unit == null)
                    .SelectMany(r => table.For(r.Device)?.Lighting ?? Array.Empty<OutputPath>()).ToList();
                foreach (var row in rows.Where(r => r.Unit == null))
                {
                    var lit = table.For(row.Device)?.Lighting ?? Array.Empty<OutputPath>();
                    foreach (var orphan in orphans)
                        Assert.False(Array.IndexOf(lit, orphan.Path) >= 0, $"round {round}: {orphan} lit by {row}");
                }
                // No path is held by a unit row and a device row at once.
                foreach (var row in rows.Where(r => r.Unit != null))
                    foreach (var path in table.For(row.Device)?.Lighting ?? Array.Empty<OutputPath>())
                        Assert.False(byDeviceRows.Contains(path), $"round {round}: {path} held twice");
                // No orphan, and no lit unit row, sits on a device outside the
                // PC that a device row lights through the other family.
                var litByRows = byDeviceRows.Select(UnitAt).Where(u => u != null && u.ContainerId != Pc).ToList();
                var standing = orphans.Concat(rows.Where(r => r.Unit != null && table.For(r.Device) != null)
                    .Select(r => UnitAt(r.Unit.Value)).Where(u => u != null));
                foreach (var unit in standing.Where(u => u.ContainerId != Pc))
                    Assert.DoesNotContain(litByRows, u => u.ContainerId == unit.ContainerId && u.VendorId == unit.VendorId
                        && (u.Family == OutputFamily.LampArrayUnit) != (unit.Family == OutputFamily.LampArrayUnit));
                foreach (var orphan in orphans)
                {
                    Assert.NotEqual(Guid.Empty, orphan.ContainerId);
                    Assert.NotEqual(OutputFamily.TouchSenseUnit, orphan.Family);
                    Assert.False(orphan.Asleep && !rows.Any(r => r.Unit == orphan.Path), $"round {round}");
                    var links = PeripheralLinker.Build(new[] { UnitRow(C, orphan.Path) }, inputs).For(C);
                    if (orphan.Asleep) Assert.Null(links);
                    else Assert.Equal(new[] { orphan.Path }, links.Lighting);
                }
                foreach (var device in orphans.Where(o => o.ContainerId != Pc).GroupBy(o => (o.ContainerId, o.VendorId)))
                {
                    bool lamps = device.Any(o => o.Family == OutputFamily.LampArrayUnit);
                    bool natives = device.Any(o => o.Family != OutputFamily.LampArrayUnit);
                    Assert.False(lamps && natives, $"round {round}");
                }
            }
            Assert.True(orphansSeen > 1000, orphansSeen.ToString());
        }
    }
}
