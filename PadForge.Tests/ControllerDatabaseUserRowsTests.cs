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
            ControllerDatabaseEngine.SetReadsRaw(PadSdlGuid, false);
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
                };
                Controller.Start();
            }

            public void Dispose() => Controller.Dispose();
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
            Assert.Equal(string.Format(PadForge.Resources.Strings.Strings.Instance.ControllerDatabase_Mine_Format, 2), h.Vm.Settings.ControllerDatabaseMineStatus);

            await h.Vm.Settings.ClearControllerDatabaseMineCommand.ExecuteAsync(null);
            Assert.Equal(0, ControllerDatabaseMine.Current.Count);
            Assert.False(h.Vm.Settings.HasControllerDatabaseMine);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.ControllerDatabase_MineNone, h.Vm.Settings.ControllerDatabaseMineStatus);
        }

        /// <summary>Copy for a raw joystick with no SDL mapping reads the
        /// picked controller's rows, under the device's ID in the
        /// community's form.</summary>
        [Fact]
        public async Task Copy_ForARawJoystick_ReadsItsControllersRows()
        {
            Arrange(PadSdlGuid);
            using var h = new Harness();
            var (text, leftOut) = await h.Controller.CopyAsync(Pad, 0);
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
