using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.Services.Catalog;
using PadForge.ViewModels;
using Xunit;
using static PadForge.Tests.ControllerDatabaseFixture;

namespace PadForge.Tests
{
    /// <summary>
    /// The user's own rows end to end in the app's model (#600 items 47 to
    /// 49): Paste keeps a row with what was there before, Remove Mine puts it
    /// back, a device SDL does not read is laid out by its row, the rows are
    /// catalog entries, and the engine's door does nothing while no engine
    /// runs. The test host starts no SDL: the poll thread never looks, so
    /// every SDL step answers that the engine is stopped.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ControllerDatabaseUserRowsTests : IDisposable
    {
        private static readonly Guid Pad = new("55555555-5555-4555-8555-555555555555");
        private const string PadSdlGuid = "050057564c050000e60c000000006800";

        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;
        private readonly MappingSet[] _savedSets = SettingsManager.SlotMappingSets;
        private readonly bool[] _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly ControllerDatabaseSection _savedSection = SettingsManager.ControllerDatabase;
        private readonly ControllerDatabaseMine _savedMine = ControllerDatabaseMine.Current;
        private readonly List<int> _savedXboxOrder = SettingsManager.XboxSlotOrder;
        private readonly string _savedProfile = SettingsManager.ActiveProfileId;

        public void Dispose()
        {
            SettingsManager.ActiveProfileId = _savedProfile;
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.SlotMappingSets = _savedSets;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
            SettingsManager.ControllerDatabase = _savedSection;
            ControllerDatabaseMine.Current = _savedMine;
            SettingsManager.XboxSlotOrder = _savedXboxOrder;
            ControllerDatabaseEngine.SdlStarting();
        }

        private sealed class Harness : IDisposable
        {
            public readonly MainViewModel Vm = new();
            public readonly ControllerDatabaseController Controller;
            public int Dirty, Refreshed;

            public Harness()
            {
                Controller = new ControllerDatabaseController(Vm, () => Dirty++, () => Refreshed++)
                {
                    Baseline = () => Baseline(),
                    OwnRows = Array.Empty<string>(),
                    Zone = TimeZoneInfo.Utc,
                    UtcNow = () => new DateTime(2026, 11, 24, 14, 17, 35, 400, DateTimeKind.Utc),
                    Log = _ => { },
                    // A test settles each profile switch itself
                    // (ProfileSettledAsync). Posted to the test's context,
                    // the switch's own run raced the test's on another
                    // thread.
                    SettleAfterSwitch = _ => { },
                };
                Controller.Start();
            }

            public void Dispose() => Controller.Dispose();
        }

        /// <summary>An engine that runs, for the controller's door: SDL takes
        /// each row unless <see cref="Refuse"/> says otherwise, opens again
        /// the devices <see cref="Reopen"/> names, and answers
        /// <see cref="Facts"/> for every device. Each give is kept.</summary>
        private sealed class FakeEngine
        {
            public readonly List<IReadOnlyList<string>> Given = new();
            public int Refuse;
            public readonly List<Guid> Reopen = new();
            public SdlDeviceFacts Facts;

            public void Attach(ControllerDatabaseController controller)
            {
                controller.EngineRuns = () => true;
                controller.ReadFacts = _ => Task.FromResult(Facts);
                controller.HeldFor = _ => Task.FromResult(string.Empty);
                controller.HeldRows = () => Task.FromResult(new List<string>());
                controller.GiveSdl = (rows, _) =>
                {
                    var list = new List<string>(rows());
                    Given.Add(list);
                    return Task.FromResult(new ControllerDatabaseApplied(true, list.Count - Refuse, Refuse, new List<Guid>(Reopen)));
                };
            }
        }

        private static PadSetting Setting() => SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0).GetPadSetting();

        /// <summary>Sets the pad's ButtonA on controller 1, in its settings
        /// and its row, as a profile's load or a hand edit leaves it.</summary>
        private static void SetButtonA(string descriptor)
        {
            SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0).SetPadSetting(new PadSetting { ButtonA = descriptor });
            SettingsManager.SlotMappingSets[0] = new MappingSet
            {
                Rows =
                {
                    new MappingRow { Target = "ButtonA", Sources = new List<MappingSource> { new() { DeviceGuid = Pad.ToString(), Descriptor = descriptor } } },
                },
            };
        }

        /// <summary>One device on controller 1, with ButtonA read from its
        /// button 2.</summary>
        private static UserDevice Arrange(string sdlGuid, int type = InputDeviceType.Joystick)
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.XboxSlotOrder = new List<int> { 0 };
            SettingsManager.ControllerDatabase = null;
            ControllerDatabaseMine.Current = ControllerDatabaseMine.Empty;
            var ud = new UserDevice
            {
                InstanceGuid = Pad,
                ProductName = "T.16000M",
                SdlGuid = sdlGuid,
                CapType = type,
                CapAxeCount = 4,
                CapButtonCount = 16,
                CapPovCount = 1,
                IsOnline = false,
            };
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
            var us = new UserSetting { InstanceGuid = Pad, MapTo = 0 };
            us.SetPadSetting(new PadSetting { ButtonA = "Button 2" });
            lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(us);
            SettingsManager.SlotMappingSets[0] = new MappingSet
            {
                Rows =
                {
                    new MappingRow { Target = "ButtonA", Sources = new List<MappingSource> { new() { DeviceGuid = Pad.ToString(), Descriptor = "Button 2" } } },
                },
            };
            return ud;
        }

        private static SdlMappingRow Read(string text)
        {
            Assert.True(SdlMappingRow.TryParse(text, out var row, out var why), why.ToString());
            return row;
        }

        [Fact]
        public async Task TheEnginesDoor_AnswersAtOnce_WhileNoEngineRuns()
        {
            Assert.False(ControllerDatabaseEngine.Running);
            var started = DateTime.UtcNow;
            var answer = await ControllerDatabaseEngine.Post(_ => "ran", "stopped");
            Assert.Equal("stopped", answer);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
        }

        /// <summary>Paste on an SDL device while the engine is stopped keeps
        /// the row as the user's, under the device's own ID with its
        /// checksum, says it is in use once the engine runs, and keeps the
        /// device's rows from before for Remove Mine.</summary>
        [Fact]
        public async Task Paste_KeepsTheRowAsYours_WithWhatWasThereBefore()
        {
            Arrange(PadSdlGuid);
            using var h = new Harness();
            var result = await h.Controller.ApplyMineAsync(Pad, Read("050000004c050000e60c000000006800,Pasted,a:b1,b:b2,platform:Windows,"));

            Assert.Equal(SdlApplyResult.AtStart, result);
            var mine = ControllerDatabaseMine.Current.ForSdlDevice(PadSdlGuid);
            Assert.NotNull(mine);
            Assert.Equal(PadSdlGuid, mine.Id);
            Assert.StartsWith(PadSdlGuid + ",Pasted,a:b1,b:b2,", mine.Text);
            Assert.Null(mine.OldRow);
            Assert.Equal(new DateTime(2026, 11, 24, 14, 17, 35, DateTimeKind.Utc), mine.AddedUtc);
            var before = Assert.Single(mine.Before);
            Assert.Equal(0, before.Slot);
            Assert.Equal("Button 2", before.PadSetting.ButtonA);
            Assert.Equal("Button 2", before.Rows.Single().Sources.Single().Descriptor);
            Assert.True(h.Dirty > 0);
            // SDL is given it as it starts, after every other row.
            Assert.Contains(mine.Text, ControllerDatabaseMine.Current.SdlRows());
            // The device's rows wait for it to be read through the row: until
            // then they keep the reading they were made for.
            Assert.Empty(mine.Mapped);
            Assert.Equal("Button 2", SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0).GetPadSetting().ButtonA);

            // A second paste keeps the first one's before, so Remove Mine
            // goes back to what the device had before any row of the user's.
            SettingsManager.SlotMappingSets[0].Rows[0].Sources[0].Descriptor = "Button 9";
            await h.Controller.ApplyMineAsync(Pad, Read("050000004c050000e60c000000006800,Again,a:b3,platform:Windows,"));
            Assert.Equal("Button 2", ControllerDatabaseMine.Current.ForSdlDevice(PadSdlGuid).Before.Single().Rows.Single().Sources.Single().Descriptor);
            Assert.Equal(1, ControllerDatabaseMine.Current.Count);
        }

        /// <summary>The first time the device is read through the row, its
        /// rows are mapped again from the new layout, once, and the device is
        /// recorded as mapped, so a restart does not map them again.</summary>
        [Fact]
        public async Task ADeviceReadThroughTheRow_HasItsRowsMappedAgainOnce()
        {
            var ud = Arrange(PadSdlGuid);
            using var h = new Harness();
            await h.Controller.ApplyMineAsync(Pad, Read("050000004c050000e60c000000006800,Pasted,a:b1,platform:Windows,"));

            // The engine opens it through the row: SDL reads it as a gamepad.
            ud.CapType = InputDeviceType.Gamepad;
            h.Controller.HandleUserRowOpened(Pad);
            var mine = ControllerDatabaseMine.Current.ForSdlDevice(PadSdlGuid);
            Assert.Equal(new[] { Pad.ToString() }, mine.Mapped);
            Assert.Equal("Button 0", SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0).GetPadSetting().ButtonA);
            Assert.Contains(SettingsManager.SlotMappingSets[0].Rows,
                r => r.Target == "ButtonA" && r.Sources.Any(s => s.DeviceGuid == Pad.ToString() && s.Descriptor == "Button 0"));
            // The rows from before stay the ones from before the paste.
            Assert.Equal("Button 2", mine.Before.Single().PadSetting.ButtonA);

            // A later edit is the user's, and a second open leaves it.
            SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0).GetPadSetting().ButtonA = "Button 7";
            h.Controller.HandleUserRowOpened(Pad);
            Assert.Equal("Button 7", SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0).GetPadSetting().ButtonA);
        }

        /// <summary>Another profile keeps its own rows of the device, made for
        /// the old reading: they are mapped again the first time that profile
        /// is active with the device read through the row. Remove Mine there
        /// puts back that profile's rows at once and owes the default its
        /// rows, put back when the default is active again. Under Force Raw
        /// Joystick Mode nothing is mapped, since the default authors nothing
        /// then.</summary>
        [Fact]
        public async Task AnotherProfile_IsMappedAgainWhenActive_AndItsRowsComeBackWhenTheRowGoes()
        {
            var ud = Arrange(string.Empty);
            using var h = new Harness();
            SettingsManager.ActiveProfileId = null;
            await h.Controller.ApplyMineAsync(Pad, Read(Pad.ToString("N") + ",Pedal Box,a:b5,platform:Windows,"));
            Assert.Equal("Button 5", SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0).GetPadSetting().ButtonA);

            // Profile p2's rows, made before the row, come in with the switch.
            var us = SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0);
            us.SetPadSetting(new PadSetting { ButtonA = "Button 3" });
            SettingsManager.SlotMappingSets[0].Rows.Single(r => r.Target == "ButtonA").Sources.Single().Descriptor = "Button 3";
            ud.ForceRawJoystickMode = true;
            SettingsManager.ActiveProfileId = "p2";
            await h.Controller.ProfileSettledAsync();
            Assert.Equal("Button 3", us.GetPadSetting().ButtonA);
            Assert.False(ControllerDatabaseMine.Current.ForPadForgeDevice(Pad).HasMapped(Pad));

            ud.ForceRawJoystickMode = false;
            await h.Controller.ProfileSettledAsync();
            var row = ControllerDatabaseMine.Current.ForPadForgeDevice(Pad);
            Assert.True(row.HasMapped(Pad, "p2"));
            Assert.True(row.HasMapped(Pad, string.Empty));
            Assert.Equal("Button 5", SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0).GetPadSetting().ButtonA);
            Assert.Contains(row.Before, b => b.Profile == "p2" && b.PadSetting.ButtonA == "Button 3");

            await h.Controller.RemoveMineAsync(Pad);
            Assert.Equal("Button 3", SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0).GetPadSetting().ButtonA);
            var owed = Assert.Single(ControllerDatabaseMine.Current.PutBacks);
            Assert.Equal(string.Empty, owed.Profile);
            Assert.Equal("Button 2", owed.PadSetting.ButtonA);

            SettingsManager.ActiveProfileId = null;
            await h.Controller.ProfileSettledAsync();
            Assert.Empty(ControllerDatabaseMine.Current.PutBacks);
            Assert.Equal("Button 2", SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0).GetPadSetting().ButtonA);
        }

        /// <summary>A profile shortcut whose trigger reads the device keeps
        /// its inputs, and the dialog says so.</summary>
        [Fact]
        public void TheDialog_SaysWhenAShortcutReadsTheDevice()
        {
            var saved = SettingsManager.GlobalMacros;
            try
            {
                SettingsManager.GlobalMacros = new[] { new GlobalMacroData { TriggerDeviceGuid = Pad } };
                Assert.True(ControllerDatabaseController.NamedByShortcuts(Pad));
                Assert.False(ControllerDatabaseController.NamedByShortcuts(Guid.NewGuid()));
                Assert.EndsWith(string.Format(PadForge.Resources.Strings.Strings.Instance.SdlMappingDialog_Shortcuts_Format, "T.16000M"),
                    PadForge.Views.SdlMappingDialog.ChangeWords("T.16000M", new[] { "Xbox #1" }, true));
            }
            finally
            {
                SettingsManager.GlobalMacros = saved;
            }
        }

        /// <summary>Remove Mine puts back the rows of a device that was mapped
        /// again through the row, and since SDL had no mapping for it before,
        /// PadForge reads it raw for the rest of the session: SDL keeps a
        /// mapping once given.</summary>
        [Fact]
        public async Task RemoveMine_PutsTheRowsBack_AndReadsADeviceThatHadNoMappingRaw()
        {
            var ud = Arrange(PadSdlGuid);
            using var h = new Harness();
            await h.Controller.ApplyMineAsync(Pad, Read("050000004c050000e60c000000006800,Pasted,a:b1,platform:Windows,"));
            // SDL had no mapping for the ID: what an engine that runs reads.
            ControllerDatabaseMine.Change(m => m.With(m.ForSdlDevice(PadSdlGuid) with { OldRow = string.Empty }));
            ud.CapType = InputDeviceType.Gamepad;
            h.Controller.HandleUserRowOpened(Pad);
            Assert.Equal("Button 0", SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0).GetPadSetting().ButtonA);

            await h.Controller.RemoveMineAsync(Pad);

            Assert.Equal(0, ControllerDatabaseMine.Current.Count);
            Assert.True(ControllerDatabaseEngine.ReadsRaw(PadSdlGuid));
            var row = SettingsManager.SlotMappingSets[0].Rows.Single(r => r.Target == "ButtonA");
            Assert.Equal(new[] { "Button 2" }, row.Sources.Where(s => s.DeviceGuid == Pad.ToString()).Select(s => s.Descriptor));
            Assert.Equal("Button 2", SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0).GetPadSetting().ButtonA);
            Assert.True(h.Refreshed > 0);
        }

        /// <summary>A row removed before any device was read through it, and
        /// before SDL was ever given it, leaves the rows and SDL as they
        /// are.</summary>
        [Fact]
        public async Task RemoveMine_OfARowNeverUsed_ChangesNothing()
        {
            Arrange(PadSdlGuid);
            using var h = new Harness();
            await h.Controller.ApplyMineAsync(Pad, Read("050000004c050000e60c000000006800,Pasted,a:b1,platform:Windows,"));
            SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0).GetPadSetting().ButtonA = "Button 4";

            await h.Controller.RemoveMineAsync(Pad);

            Assert.Equal(0, ControllerDatabaseMine.Current.Count);
            Assert.False(ControllerDatabaseEngine.ReadsRaw(PadSdlGuid));
            Assert.Equal("Button 4", SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0).GetPadSetting().ButtonA);
        }

        /// <summary>The session's raw list holds a removed row's ID and reads
        /// every device that ID maps raw, and a row given again for one of
        /// them takes it off.</summary>
        [Fact]
        public void TheRawList_ReadsEveryDeviceTheRemovedRowMapped()
        {
            const string modelRow = "050000004c050000e60c000000006800";
            ControllerDatabaseMine.Current = ControllerDatabaseMine.Empty;
            ControllerDatabaseEngine.SetReadsRaw(modelRow, true);
            Assert.True(ControllerDatabaseEngine.ReadsRaw(PadSdlGuid));
            Assert.False(ControllerDatabaseEngine.ReadsRaw("030057564c050000e60c000000006800"));
            ControllerDatabaseEngine.SetReadsRaw(PadSdlGuid, false);
            Assert.False(ControllerDatabaseEngine.ReadsRaw(PadSdlGuid));
        }

        /// <summary>The row's reset takes the downloaded rows and leaves the
        /// user's. Reset to Defaults takes the user's.</summary>
        [Fact]
        public async Task TheRowsReset_LeavesTheUsersRows_AndResetToDefaultsTakesThem()
        {
            Arrange(PadSdlGuid);
            using var h = new Harness();
            await h.Controller.ApplyMineAsync(Pad, Read("050000004c050000e60c000000006800,Pasted,a:b1,platform:Windows,"));
            h.Controller.Reset();
            Assert.Null(SettingsManager.ControllerDatabase);
            Assert.Equal(1, ControllerDatabaseMine.Current.Count);

            ControllerDatabaseController.ForgetAllMine();
            Assert.Equal(0, ControllerDatabaseMine.Current.Count);
        }

        /// <summary>The paste dialog takes clipboard text only when it passes
        /// the check, so a program that opens it reads nothing else of the
        /// user's clipboard.</summary>
        [Fact]
        public void ThePasteDialog_TakesOnlyARowFromTheClipboard()
        {
            Assert.Null(PadForge.Views.SdlMappingDialog.Prefill(PadForge.Views.SdlMappingDialog.Mode.Paste, "my bank password", PadSdlGuid));
            Assert.Null(PadForge.Views.SdlMappingDialog.Prefill(PadForge.Views.SdlMappingDialog.Mode.Paste, null, PadSdlGuid));
            string row = "050000004c050000e60c000000006800,Pad,a:b1,platform:Windows,";
            Assert.Equal(row, PadForge.Views.SdlMappingDialog.Prefill(PadForge.Views.SdlMappingDialog.Mode.Paste, row, PadSdlGuid));
            Assert.Equal(row, PadForge.Views.SdlMappingDialog.Prefill(PadForge.Views.SdlMappingDialog.Mode.AddRows, row, null));
        }

        /// <summary>A device SDL does not read, a plugin's, is laid out by
        /// its row in PadForge: its rows are made again from the row's
        /// places, and the card says so.</summary>
        [Fact]
        public async Task APluginsDevice_IsLaidOutByItsRow()
        {
            Arrange(string.Empty);
            using var h = new Harness();
            var result = await h.Controller.ApplyMineAsync(Pad, Read(Pad.ToString("N") + ",Pedal Box,a:b5,b:b6,leftx:a0,platform:Windows,"));

            Assert.Equal(SdlApplyResult.InPadForge, result);
            Assert.Empty(ControllerDatabaseMine.Current.SdlRows());
            var ps = SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0).GetPadSetting();
            Assert.Equal("Button 5", ps.ButtonA);
            Assert.Equal("Button 6", ps.ButtonB);
            Assert.Equal("Axis 0", ps.LeftThumbAxisX);
            Assert.Contains(SettingsManager.SlotMappingSets[0].Rows,
                r => r.Target == "ButtonA" && r.Sources.Any(s => s.DeviceGuid == Pad.ToString() && s.Descriptor == "Button 5"));

            var facts = await h.Controller.ReadAsync(Pad);
            Assert.Equal(SdlReading.PadForgeDevice, facts.Reading);
            Assert.Equal(SdlMappingSource.YoursPadForge, facts.Source);

            await h.Controller.RemoveMineAsync(Pad);
            Assert.Equal("Button 2", SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0).GetPadSetting().ButtonA);
        }

        /// <summary>The user's rows are catalog entries through wave 1's
        /// one seam: each a free setting with its row as its value and Remove
        /// Mine in its menu, gone with the controller.</summary>
        [Fact]
        public async Task TheUsersRows_AreCatalogEntries()
        {
            Arrange(PadSdlGuid);
            using (var h = new Harness())
            {
                await h.Controller.ApplyMineAsync(Pad, Read("050000004c050000e60c000000006800,Pasted,a:b1,platform:Windows,"));
                var items = LiveCatalog.Items();
                var row = Assert.Single(items, i => i.Id == "db/" + PadSdlGuid);
                Assert.Equal(LiveKind.Setting, row.Kind);
                Assert.Equal(ReachClass.Free, row.Reach);
                Assert.StartsWith(PadSdlGuid + ",Pasted,", row.Read());
                var remove = Assert.Single(items, i => i.Id == "db/" + PadSdlGuid + "/remove");
                Assert.Equal(LiveKind.MenuItem, remove.Kind);
                remove.Press();
                for (int i = 0; i < 50 && ControllerDatabaseMine.Current.Count > 0; i++) await Task.Delay(10);
                Assert.Equal(0, ControllerDatabaseMine.Current.Count);
            }
            Assert.DoesNotContain(LiveCatalog.Items(), i => i.Id.StartsWith("db/", StringComparison.Ordinal));
        }

        /// <summary>Add Rows keeps each Windows row as the user's under its
        /// own ID, for every device of that ID, and the Your Rows line
        /// counts them. Clear takes them all.</summary>
        [Fact]
        public async Task AddRows_KeepsEachRowAsYours_AndClearTakesThemAll()
        {
            Arrange(PadSdlGuid);
            using var h = new Harness();
            var rows = SdlMappingRow.ReadAll(Row(0x1000, 0x0001, "One") + "\n" + Row(0x1000, 0x0002, "Two"), out _, out _, out _);
            Assert.Equal(2, await h.Vm.Settings.AddControllerDatabaseRows(rows));
            Assert.Equal(2, ControllerDatabaseMine.Current.Count);
            Assert.Equal(Id(0x1000, 0x0001), ControllerDatabaseMine.Current.ForSdlDevice(Id(0x1000, 0x0001)).Id);
            Assert.True(h.Vm.Settings.HasControllerDatabaseMine);
            Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, PadForge.Resources.Strings.Strings.Instance.ControllerDatabase_Rows_Format, 2), h.Vm.Settings.ControllerDatabaseMineStatus);

            await h.Vm.Settings.ClearControllerDatabaseMineCommand.ExecuteAsync(null);
            Assert.Equal(0, ControllerDatabaseMine.Current.Count);
            Assert.False(h.Vm.Settings.HasControllerDatabaseMine);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.ControllerDatabase_MineNone, h.Vm.Settings.ControllerDatabaseMineStatus);
        }

        /// <summary>Two of SDL's rows that share an ID and differ by crc are
        /// kept as two rows of the user's, each under the ID SDL gives it.</summary>
        [Fact]
        public async Task AddRows_KeepsTwoRowsThatDifferByCrc()
        {
            Arrange(PadSdlGuid);
            using var h = new Harness();
            var rows = SdlMappingRow.ReadAll("03000000491900000204000000000000,GameSir T4 Pro,crc:1aa4,a:b0,platform:Windows,\n"
                + "03000000491900000204000000000000,Ipega PG-9023,a:b1,platform:Windows,", out _, out _, out _);
            Assert.Equal(2, await h.Controller.AddRowsAsync(rows));
            Assert.Equal(2, ControllerDatabaseMine.Current.Count);
            Assert.Equal("GameSir T4 Pro", ControllerDatabaseMine.Current.ForSdlDevice("0300a41a491900000204000000000000").Row.Name);
            Assert.Equal("Ipega PG-9023", ControllerDatabaseMine.Current.ForSdlDevice("0300bbbb491900000204000000000000").Row.Name);
        }

        /// <summary>A saved device that is away is said to be away, and a
        /// running engine with no answer from SDL is not called stopped: the
        /// stopped line is for a stopped engine alone.</summary>
        [Fact]
        public async Task TheCard_SaysAwayOrNoAnswer_AndStoppedOnlyForAStoppedEngine()
        {
            var ud = Arrange(PadSdlGuid);
            using var h = new Harness();
            h.Controller.EngineRuns = () => true;
            h.Controller.ReadFacts = _ => Task.FromResult<SdlDeviceFacts>(null);
            Assert.Equal(SdlReading.Away, (await h.Controller.ReadAsync(Pad)).Reading);

            ud.IsOnline = true;
            Assert.Equal(SdlReading.NoAnswer, (await h.Controller.ReadAsync(Pad)).Reading);

            h.Controller.EngineRuns = () => false;
            Assert.Equal(SdlReading.Stopped, (await h.Controller.ReadAsync(Pad)).Reading);
        }

        /// <summary>Copy for a device on a controller whose rows read it only
        /// for keyboard keys says so, naming the controller, and never that
        /// the device is on no controller.</summary>
        [Fact]
        public async Task Copy_ForRowsSdlsFormCannotHold_SaysSo()
        {
            var ud = Arrange(PadSdlGuid);
            ud.IsOnline = true;
            SettingsManager.SlotMappingSets[0].Rows[0].Target = "KeyA";
            using var h = new Harness();
            // SDL lists the device and has no mapping for it.
            new FakeEngine { Facts = new SdlDeviceFacts(PadSdlGuid, null, false, false) }.Attach(h.Controller);
            var (text, leftOut, empty) = await h.Controller.CopyAsync(Pad, 0);
            Assert.Null(text);
            Assert.Equal(SdlCopyEmpty.NothingInRows, empty);
            Assert.Equal(SdlLeftOut.KeyboardOutputs, leftOut);
            var words = SdlMappingCard.NothingWords(empty, "Xbox #1", leftOut);
            Assert.NotEqual(PadForge.Resources.Strings.Strings.Instance.SdlMapping_CopyNothing, words);
            Assert.Equal(string.Format(PadForge.Resources.Strings.Strings.Instance.SdlMapping_CopyOnlyLeftOut_Format, "Xbox #1",
                PadForge.Resources.Strings.Strings.Instance.SdlMapping_LeftOut_Keys), words);
        }

        /// <summary>A device whose reading read no SDL text (away, the engine
        /// stopped, no answer) is never said to have no SDL mapping, and the
        /// user's row for it is copied when the controller's rows give no
        /// binding.</summary>
        [Fact]
        public async Task Copy_WithNoSdlTextRead_NeverSaysSdlHasNoMapping_AndCopiesTheUsersRow()
        {
            Arrange(PadSdlGuid);
            SettingsManager.SlotMappingSets[0].Rows[0].Target = "KeyA";
            using var h = new Harness();
            Assert.Equal(SdlReading.Away, (await h.Controller.ReadAsync(Pad)).Reading);
            var (text, leftOut, empty) = await h.Controller.CopyAsync(Pad, 0);
            Assert.Null(text);
            Assert.Equal(SdlCopyEmpty.NotRead, empty);
            Assert.Equal(string.Format(PadForge.Resources.Strings.Strings.Instance.SdlMapping_CopyNotReadInRows_Format, "Xbox #1"),
                SdlMappingCard.NothingWords(empty, "Xbox #1", leftOut));
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.SdlMapping_CopyNotRead, SdlMappingCard.NothingWords(empty, null, leftOut));

            await h.Controller.ApplyMineAsync(Pad, Read("050000004c050000e60c000000006800,Pasted,a:b1,platform:Windows,"));
            (text, leftOut, empty) = await h.Controller.CopyAsync(Pad, 0);
            Assert.Equal(SdlCopyEmpty.None, empty);
            Assert.Equal("050000004c050000e60c000000006800,T.16000M,a:b1,platform:Windows,", text);
            Assert.Equal(SdlLeftOut.KeyboardOutputs, leftOut);
        }

        /// <summary>Picking another controller in the card's More: the
        /// table's header and the More row follow it, the More row stays
        /// while the picker has a choice, and the last Copy's line, which was
        /// for the other controller, goes.</summary>
        [Fact]
        public void PickingAnotherController_MovesTheHeaderAndTheMoreRow_AndClearsTheLastLine()
        {
            using var h = new Harness();
            var card = h.Vm.Devices.SdlMapping;
            var keyboard = new SdlControllerChoice(0, "Keyboard + Mouse #1");
            var xbox = new SdlControllerChoice(1, "Xbox #1");
            var facts = SdlCardFacts.Nothing with
            {
                Reading = SdlReading.Joystick,
                Places = new[] { ("Xbox #1", (IReadOnlyList<SdlPlaceLine>)new[] { new SdlPlaceLine("A", "Button 2", "a", null, false) }) },
            };
            card.Show(facts, new[] { keyboard, xbox });
            Assert.Equal(0, card.PickedSlot);
            Assert.False(card.ShowPlaces);
            Assert.True(card.ShowMore);

            card.Result = "Copied.";
            var raised = new List<string>();
            card.PropertyChanged += (s, e) => raised.Add(e.PropertyName);
            card.PickedController = xbox;
            Assert.True(card.ShowPlaces);
            Assert.Contains(nameof(SdlMappingCard.PlacesHeader), raised);
            Assert.Contains(nameof(SdlMappingCard.ShowMore), raised);
            Assert.Contains("Xbox #1", card.PlacesHeader);
            Assert.Null(card.Result);
        }

        /// <summary>A second Paste for the device keeps what the first one's
        /// mapping owed every other profile: Remove Mine puts the active
        /// profile's rows back and owes the others theirs, put back when each
        /// is active again.</summary>
        [Fact]
        public async Task ASecondPaste_KeepsWhatTheFirstOwedOtherProfiles()
        {
            var ud = Arrange(PadSdlGuid);
            using var h = new Harness();
            SettingsManager.ActiveProfileId = "p1";
            await h.Controller.ApplyMineAsync(Pad, Read("050000004c050000e60c000000006800,First,a:b1,platform:Windows,"));
            ud.CapType = InputDeviceType.Gamepad;
            h.Controller.HandleUserRowOpened(Pad);
            Assert.Equal("Button 0", Setting().ButtonA);

            // p2's rows, made before any row of the user's, come in with the switch.
            SetButtonA("Button 3");
            SettingsManager.ActiveProfileId = "p2";
            h.Controller.HandleUserRowOpened(Pad);
            await h.Controller.ApplyMineAsync(Pad, Read("050000004c050000e60c000000006800,Second,a:b2,platform:Windows,"));
            h.Controller.HandleUserRowOpened(Pad);

            await h.Controller.RemoveMineAsync(Pad);
            Assert.Equal("Button 3", Setting().ButtonA);

            // p1's rows, made through the first row, come back on the switch.
            SetButtonA("Button 0");
            SettingsManager.ActiveProfileId = "p1";
            await h.Controller.ProfileSettledAsync();
            Assert.Equal("Button 2", Setting().ButtonA);
        }

        /// <summary>Clear keeps the rows from before that an earlier Remove
        /// Mine owes another profile.</summary>
        [Fact]
        public async Task Clear_KeepsWhatAnEarlierRemovalOwes()
        {
            Arrange(string.Empty);
            using var h = new Harness();
            SettingsManager.ActiveProfileId = null;
            await h.Controller.ApplyMineAsync(Pad, Read(Pad.ToString("N") + ",Pedal Box,a:b5,platform:Windows,"));
            SetButtonA("Button 3");
            SettingsManager.ActiveProfileId = "p2";
            await h.Controller.ProfileSettledAsync();
            await h.Controller.RemoveMineAsync(Pad);
            Assert.Single(ControllerDatabaseMine.Current.PutBacks);

            await h.Controller.AddRowsAsync(new[] { Read(Row(0x1000, 0x0001)) });
            await h.Vm.Settings.ClearControllerDatabaseMineCommand.ExecuteAsync(null);
            Assert.Equal(0, ControllerDatabaseMine.Current.Count);
            var owed = Assert.Single(ControllerDatabaseMine.Current.PutBacks);
            Assert.Equal(string.Empty, owed.Profile);
        }

        /// <summary>A device mapped through a row while it was on no
        /// controller, and put on one since, has no rows from before: the
        /// catalog's Remove Mine maps its rows again from the layout it is
        /// read through, as the card's does.</summary>
        [Fact]
        public async Task RemoveRow_MapsADeviceWithNoRowsFromBeforeAgain()
        {
            var ud = Arrange(PadSdlGuid);
            using var h = new Harness();
            var log = new List<string>();
            h.Controller.Log = log.Add;
            var us = SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0);
            us.MapTo = -1;
            await h.Controller.ApplyMineAsync(Pad, Read("050000004c050000e60c000000006800,Pasted,a:b1,platform:Windows,"));
            ud.CapType = InputDeviceType.Gamepad;
            h.Controller.HandleUserRowOpened(Pad);
            var row = ControllerDatabaseMine.Current.ForSdlDevice(PadSdlGuid);
            Assert.True(row.HasMapped(Pad));
            Assert.Empty(row.Before);
            us.MapTo = 0;
            log.Clear();

            await h.Controller.RemoveRowAsync(row);
            Assert.Contains(log, l => l.Contains("slot=0 (SDL Mapping)", StringComparison.Ordinal));
        }

        /// <summary>Add Rows that replaces a row of the user's with one of
        /// other bindings has the devices read through it mapped again, as
        /// Paste does, since their rows were made for the old layout.</summary>
        [Fact]
        public async Task AddRows_ThatChangesARowsBindings_MapsTheRowsAgain()
        {
            var ud = Arrange(PadSdlGuid);
            ud.IsOnline = true;
            ud.CapType = InputDeviceType.Gamepad;
            using var h = new Harness();
            var engine = new FakeEngine();
            engine.Reopen.Add(Pad);
            engine.Attach(h.Controller);
            const string model = "050000004c050000e60c000000006800";
            await h.Controller.AddRowsAsync(new[] { Read(model + ",First,a:b1,paddle1:b12,platform:Windows,") });
            Assert.True(ControllerDatabaseMine.Current.ForSdlDevice(PadSdlGuid).HasMapped(Pad));
            SetButtonA("Button 9");

            await h.Controller.AddRowsAsync(new[] { Read(model + ",Second,a:b1,platform:Windows,") });
            Assert.Equal("Button 0", Setting().ButtonA);
        }

        /// <summary>Paste and Remove Mine raise the Settings page's Your Rows
        /// line and its Clear.</summary>
        [Fact]
        public async Task PasteAndRemoveMine_RaiseTheYourRowsLine()
        {
            Arrange(PadSdlGuid);
            using var h = new Harness();
            var raised = new List<string>();
            h.Vm.Settings.PropertyChanged += (s, e) => raised.Add(e.PropertyName);
            await h.Controller.ApplyMineAsync(Pad, Read("050000004c050000e60c000000006800,Pasted,a:b1,platform:Windows,"));
            Assert.Contains(nameof(SettingsViewModel.HasControllerDatabaseMine), raised);
            raised.Clear();
            await h.Controller.RemoveMineAsync(Pad);
            Assert.Contains(nameof(SettingsViewModel.HasControllerDatabaseMine), raised);
        }

        /// <summary>A row SDL does not take, or takes and reads the device
        /// past, is not kept: nothing changed, as the card says, and in the
        /// second case SDL is given back what it held.</summary>
        [Fact]
        public async Task ARowSdlDoesNotReadTheDeviceThrough_IsNotKept()
        {
            var ud = Arrange(PadSdlGuid);
            ud.IsOnline = true;
            using var h = new Harness();
            var engine = new FakeEngine { Refuse = 1, Facts = new SdlDeviceFacts(PadSdlGuid, null, false, false) };
            engine.Attach(h.Controller);
            Assert.Equal(SdlApplyResult.Refused, await h.Controller.ApplyMineAsync(Pad, Read("050000004c050000e60c000000006800,Pasted,a:b1,platform:Windows,")));
            Assert.Equal(0, ControllerDatabaseMine.Current.Count);

            engine.Refuse = 0;
            engine.Facts = new SdlDeviceFacts(PadSdlGuid, PadSdlGuid + ",Other,a:b5,platform:Windows,", true, true);
            Assert.Equal(SdlApplyResult.Refused, await h.Controller.ApplyMineAsync(Pad, Read("050000004c050000e60c000000006800,Pasted,a:b1,platform:Windows,")));
            Assert.Equal(0, ControllerDatabaseMine.Current.Count);
            Assert.Contains(engine.Given[^1], t => t.StartsWith(PadSdlGuid + ",Other,a:b5,", StringComparison.Ordinal));
        }

        /// <summary>A profile made from the live setup while a row of the
        /// user's is in force holds rows made under that row: its first
        /// activation keeps them, a hand edit included.</summary>
        [Fact]
        public async Task AProfileSavedFromTheLiveSetup_KeepsItsRowsOnItsFirstActivation()
        {
            Arrange(string.Empty);
            using var h = new Harness();
            SettingsManager.ActiveProfileId = null;
            await h.Controller.ApplyMineAsync(Pad, Read(Pad.ToString("N") + ",Pedal Box,a:b5,platform:Windows,"));
            SetButtonA("Button 7");
            ControllerDatabaseMine.Change(m => m.CopyProfile(string.Empty, "p3"));
            SettingsManager.ActiveProfileId = "p3";
            await h.Controller.ProfileSettledAsync();
            Assert.Equal("Button 7", Setting().ButtonA);

            // Save As and Save Current Setup Here both say so.
            string service = OffLimitsSource.Read("PadForge.App/Services/InputService.cs");
            Assert.Contains("m.CopyProfile(ControllerDatabaseMineRow.ActiveProfile, snapshot.Id)",
                FenceSource.Body(service, "public ProfileData CreateSnapshotProfile("), StringComparison.Ordinal);
            Assert.Contains("m.CopyProfile(ControllerDatabaseMineRow.ActiveProfile, snapshot.Id)",
                FenceSource.Body(service, "public ProfileData SaveCurrentSetupInto("), StringComparison.Ordinal);
        }

        /// <summary>A settings file's rows from before, put-backs and mapped
        /// keys for a profile it no longer holds are dropped as it loads,
        /// and the file is saved again without them.</summary>
        [Fact]
        public void ALoad_DropsWhatADeletedProfileWasOwed()
        {
            string pad = Pad.ToString();
            ControllerDatabaseBeforeData Before(string profile) => new() { Instance = pad, Slot = "0", Profile = profile };
            var data = new[]
            {
                new ControllerDatabaseMineData
                {
                    Added = "2026-11-24T14:17:35Z",
                    Text = PadSdlGuid + ",Pasted,a:b1,platform:Windows,",
                    Mapped = pad + "," + pad + "@gone," + pad + "@p2",
                    Before = new[] { Before(null), Before("gone"), Before("p2") },
                },
            };
            var putBacks = new[] { Before("gone"), Before("p2") };

            var kept = ControllerDatabaseMine.FromData(data, putBacks, new[] { "p2" }, out bool changed);
            Assert.True(changed);
            Assert.Equal(new[] { "p2" }, kept.PutBacks.Select(b => b.Profile));
            var row = kept.Rows.Single();
            Assert.Equal(new[] { string.Empty, "p2" }, row.Before.Select(b => b.Profile));
            Assert.Equal(new[] { pad, pad + "@p2" }, row.Mapped);

            // A file whose profiles are all there loads as it is.
            ControllerDatabaseMine.FromData(data, putBacks, new[] { "p2", "gone" }, out changed);
            Assert.False(changed);
        }

        /// <summary>The owed devices of a row are saved and read back, and a
        /// deleted profile's owed key goes on a load.</summary>
        [Fact]
        public void TheOwedDevices_AreSavedAndReadBack()
        {
            string pad = Pad.ToString();
            Assert.True(SdlMappingRow.TryParse(PadSdlGuid + ",Pasted,a:b1,platform:Windows,", out var parsed, out _));
            var row = new ControllerDatabaseMineRow(PadSdlGuid, parsed, new DateTime(2026, 11, 24, 14, 17, 35, DateTimeKind.Utc), null, Guid.Empty,
                Array.Empty<ControllerDatabaseBefore>()) { Owed = new[] { pad + "@p2", pad + "@gone" } };
            var data = ControllerDatabaseMine.Empty.With(row).ToData();
            Assert.Equal(pad + "@p2," + pad + "@gone", data.Single().Owed);

            var back = ControllerDatabaseMine.FromData(data, null, out bool changed);
            Assert.False(changed);
            Assert.Equal(new[] { pad + "@p2", pad + "@gone" }, back.Rows.Single().Owed);
            var kept = ControllerDatabaseMine.FromData(data, null, new[] { "p2" }, out changed);
            Assert.True(changed);
            Assert.Equal(new[] { pad + "@p2" }, kept.Rows.Single().Owed);
        }

        /// <summary>Copy for a device on no controller with a row of the
        /// user's copies that row, which SDL reads the device through once it
        /// runs, rather than saying SDL has no mapping.</summary>
        [Fact]
        public async Task Copy_OnNoController_CopiesTheUsersRow()
        {
            Arrange(PadSdlGuid);
            SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0).MapTo = -1;
            using var h = new Harness();
            await h.Controller.ApplyMineAsync(Pad, Read("050000004c050000e60c000000006800,Pasted,a:b1,platform:Windows,"));
            var (text, _, empty) = await h.Controller.CopyAsync(Pad, -1);
            Assert.Equal(SdlCopyEmpty.None, empty);
            Assert.Equal("050000004c050000e60c000000006800,T.16000M,a:b1,platform:Windows,", text);
        }

        /// <summary>Remove Mine maps a device's rows again in the active
        /// profile when only another profile kept rows from before for it.</summary>
        [Fact]
        public async Task RemoveMine_MapsAgainWhereOnlyAnotherProfileKeptRows()
        {
            var ud = Arrange(PadSdlGuid);
            using var h = new Harness();
            var log = new List<string>();
            h.Controller.Log = log.Add;
            SettingsManager.ActiveProfileId = "p2";
            await h.Controller.ApplyMineAsync(Pad, Read("050000004c050000e60c000000006800,Pasted,a:b1,platform:Windows,"));
            ud.CapType = InputDeviceType.Gamepad;
            h.Controller.HandleUserRowOpened(Pad);
            // In the default profile the pad was on no controller when it was
            // read through the row, and is on one now.
            SettingsManager.ActiveProfileId = null;
            var us = SettingsManager.FindSettingByInstanceGuidAndSlot(Pad, 0);
            us.MapTo = -1;
            h.Controller.HandleUserRowOpened(Pad);
            us.MapTo = 0;
            log.Clear();

            await h.Controller.RemoveMineAsync(Pad);
            Assert.Contains(log, l => l.Contains("slot=0 (SDL Mapping)", StringComparison.Ordinal));
        }

        /// <summary>Find with the engine stopped says the rows SDL holds are
        /// left out until it runs.</summary>
        [Fact]
        public async Task Find_WithTheEngineStopped_SaysWhatIsLeftOut()
        {
            Arrange(PadSdlGuid);
            using var h = new Harness();
            var (_, missing) = await h.Controller.AllRowsAsync(Pad);
            Assert.Equal(SdlFindMissing.Stopped, missing);
            Assert.Contains(PadForge.Resources.Strings.Strings.Instance.SdlMappingDialog_FindStopped,
                PadForge.Views.SdlMappingDialog.FindNoteWords("T.16000M", missing), StringComparison.Ordinal);

            h.Controller.EngineRuns = () => true;
            Assert.Equal(SdlFindMissing.NoAnswer, (await h.Controller.AllRowsAsync(Pad)).Missing);
        }

        /// <summary>Open a File reads the file through one handle, and no
        /// more than the cap: a longer one is refused.</summary>
        [Fact]
        public void OpenAFile_ReadsAtMostTheCap()
        {
            var over = new System.IO.MemoryStream(new byte[ControllerDatabaseDownload.MaxBytes + 1]);
            Assert.Null(PadForge.Views.SdlMappingDialog.ReadCapped(over, ControllerDatabaseDownload.MaxBytes));
            var bytes = new List<byte> { 0xFF, 0xFE };
            bytes.AddRange(System.Text.Encoding.Unicode.GetBytes("row"));
            Assert.Equal("row", PadForge.Views.SdlMappingDialog.ReadCapped(new System.IO.MemoryStream(bytes.ToArray()), ControllerDatabaseDownload.MaxBytes));
        }

        /// <summary>A double-click applies a row only when it lands on a row
        /// with the left button: the list's scroll bar and other buttons
        /// raise the list's double-click too.</summary>
        [Fact]
        public void FindsDoubleClick_AppliesOnlyARow()
        {
            Exception failure = null;
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    var list = new System.Windows.Controls.ListBox();
                    var text = new System.Windows.Controls.TextBlock { Text = "Pad" };
                    var item = new System.Windows.Controls.ListBoxItem { Content = text };
                    list.Items.Add(item);
                    Assert.True(PadForge.Views.SdlMappingDialog.PicksARow(list, text, System.Windows.Input.MouseButton.Left));
                    Assert.False(PadForge.Views.SdlMappingDialog.PicksARow(list, text, System.Windows.Input.MouseButton.Right));
                    Assert.False(PadForge.Views.SdlMappingDialog.PicksARow(list, new System.Windows.Controls.Primitives.ScrollBar(),
                        System.Windows.Input.MouseButton.Left));
                    Assert.False(PadForge.Views.SdlMappingDialog.PicksARow(list, list, System.Windows.Input.MouseButton.Left));
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(30000), "STA test body timed out");
            if (failure != null) throw failure;
        }

        /// <summary>Copy for a raw joystick with no SDL mapping reads the
        /// picked controller's rows, under the device's ID in the
        /// community's form.</summary>
        [Fact]
        public async Task Copy_ForARawJoystick_ReadsItsControllersRows()
        {
            Arrange(PadSdlGuid);
            using var h = new Harness();
            var (text, leftOut, _) = await h.Controller.CopyAsync(Pad, 0);
            Assert.Equal("050000004c050000e60c000000006800,T.16000M,a:b2,platform:Windows,", text);
            Assert.Equal(SdlLeftOut.None, leftOut);
        }

        /// <summary>The card follows the selected device, offers Guess for a
        /// joystick no database maps, and Remove Mine once a row is the
        /// user's.</summary>
        [Fact]
        public async Task TheCard_FollowsTheDevice_AndOffersWhatFits()
        {
            Arrange(string.Empty);
            using var h = new Harness();
            var card = h.Vm.Devices.SdlMapping;
            h.Vm.Devices.SelectedDevice = new DeviceRowViewModel { InstanceGuid = Pad, DeviceName = "T.16000M" };
            await card.RefreshAsync();
            Assert.True(card.ShowCard);
            Assert.Equal(SdlReading.PadForgeDevice, card.Reading);
            Assert.True(card.ShowGuess);
            Assert.True(card.ShowPaste);
            Assert.False(card.HasMine);

            await card.ApplyAsync(Read(Pad.ToString("N") + ",Pedal Box,a:b5,platform:Windows,"));
            Assert.True(card.HasMine);
            Assert.False(card.ShowGuess);
            Assert.True(card.FromIsYours);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.SdlMapping_Applied, card.Result);
            Assert.Contains("Pedal Box", card.RowText);
        }
    }
}
