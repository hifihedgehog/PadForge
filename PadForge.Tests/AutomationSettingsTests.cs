using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Automation switch and its level in the settings (#597). The switch
    /// was External Control's (#366): one bool on the Settings view model,
    /// saved as <c>EnableExternalControl</c>. It is the Extras view model's
    /// now, with a level beside it, and a file from before the move loads with
    /// what it allowed and nothing more.
    ///
    /// <para>These drive the real SettingsService on the real MainViewModel,
    /// the pair MainWindow builds.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AutomationSettingsTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;
        private readonly List<ProfileData> _savedProfiles = SettingsManager.Profiles;
        private readonly string _savedActiveProfileId = SettingsManager.ActiveProfileId;
        private readonly ProfileData _savedPendingDefault = SettingsManager.PendingDefaultSnapshot;
        private readonly bool[] _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool[] _savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
        private readonly MappingSet[] _savedMappingSets = SettingsManager.SlotMappingSets;
        private readonly List<int> _savedXboxOrder = SettingsManager.XboxSlotOrder;
        private readonly List<int> _savedPsOrder = SettingsManager.PlayStationSlotOrder;
        private readonly List<int> _savedExtOrder = SettingsManager.ExtendedSlotOrder;
        private readonly List<int> _savedKbmOrder = SettingsManager.KeyboardMouseSlotOrder;
        private readonly List<int> _savedMidiOrder = SettingsManager.MidiSlotOrder;
        private readonly bool _savedAutomation = SettingsManager.EnableAutomation;
        private readonly AutomationLevel _savedLevel = SettingsManager.AutomationLevel;
        private readonly bool _savedPin = SettingsManager.ExternalProfilePinActive;
        private readonly bool _savedHeadTrackingEnabled = HeadTrackingRuntime.Enabled;
        private readonly bool _savedHeadTrackingFreeTrack = HeadTrackingRuntime.FreeTrackEnabled;
        private readonly bool _savedHeadTrackingOpenXr = HeadTrackingRuntime.OpenXrEnabled;
        private readonly string _savedOpenXrRuntime = HeadTrackingRuntime.OpenXrRuntimeManifest;

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.Profiles = _savedProfiles;
            SettingsManager.ActiveProfileId = _savedActiveProfileId;
            SettingsManager.PendingDefaultSnapshot = _savedPendingDefault;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
            Array.Copy(_savedEnabled, SettingsManager.SlotEnabled, _savedEnabled.Length);
            SettingsManager.SlotMappingSets = _savedMappingSets;
            SettingsManager.XboxSlotOrder = _savedXboxOrder;
            SettingsManager.PlayStationSlotOrder = _savedPsOrder;
            SettingsManager.ExtendedSlotOrder = _savedExtOrder;
            SettingsManager.KeyboardMouseSlotOrder = _savedKbmOrder;
            SettingsManager.MidiSlotOrder = _savedMidiOrder;
            SettingsManager.EnableAutomation = _savedAutomation;
            SettingsManager.AutomationLevel = _savedLevel;
            SettingsManager.ExternalProfilePinActive = _savedPin;
            HeadTrackingRuntime.Enabled = _savedHeadTrackingEnabled;
            HeadTrackingRuntime.FreeTrackEnabled = _savedHeadTrackingFreeTrack;
            HeadTrackingRuntime.OpenXrEnabled = _savedHeadTrackingOpenXr;
            HeadTrackingRuntime.OpenXrRuntimeManifest = _savedOpenXrRuntime;
        }

        /// <summary>One created Xbox slot, no devices, no active profile, and
        /// the real service on the real view model (ProfileServiceToggleTests).</summary>
        private static (MainViewModel Vm, SettingsService Settings) Arrange()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.ActiveProfileId = null;
            SettingsManager.PendingDefaultSnapshot = null;
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotEnabled[0] = true;
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SettingsManager.XboxSlotOrder = new List<int> { 0 };
            SettingsManager.PlayStationSlotOrder = new List<int>();
            SettingsManager.ExtendedSlotOrder = new List<int>();
            SettingsManager.KeyboardMouseSlotOrder = new List<int>();
            SettingsManager.MidiSlotOrder = new List<int>();

            var vm = new MainViewModel();
            return (vm, new SettingsService(vm));
        }

        private static AppSettingsData BuildApp(SettingsService settings)
            => (AppSettingsData)typeof(SettingsService).GetMethod("BuildAppSettings", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(settings, null);

        private static void LoadApp(SettingsService settings, AppSettingsData data)
            => typeof(SettingsService).GetMethod("LoadAppSettings", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(settings, new object[] { data });

        /// <summary>A settings file's text, read as the app reads it at a
        /// start: deserialized, then its old spellings moved forward.</summary>
        private static SettingsFileData ReadFile(string appSettingsXml)
        {
            var serializer = new XmlSerializer(typeof(SettingsFileData));
            using var reader = new StringReader("<PadForgeSettings><AppSettings>" + appSettingsXml + "</AppSettings></PadForgeSettings>");
            var data = (SettingsFileData)serializer.Deserialize(reader);
            data.MigrateLegacySchema();
            return data;
        }

        private static string WriteFile(AppSettingsData app)
        {
            var serializer = new XmlSerializer(typeof(SettingsFileData));
            using var writer = new StringWriter();
            serializer.Serialize(writer, new SettingsFileData { AppSettings = app });
            return writer.ToString();
        }

        // ── An old file ──

        /// <summary>A file a build from before Automation saved with External
        /// Control on loads with Automation on at Commands, which allows what
        /// the old switch allowed. The old element is read and never written
        /// again.</summary>
        [Fact]
        public void AnOldFile_WithExternalControlOn_LoadsAsAutomationOnAtCommands()
        {
            var old = ReadFile("<EnableExternalControl>true</EnableExternalControl>").AppSettings;
            Assert.True(old.EnableAutomation);
            Assert.Equal(0, old.AutomationLevel);
            Assert.False(old.LegacyEnableExternalControl);

            var (vm, settings) = Arrange();
            LoadApp(settings, old);
            Assert.True(vm.Dashboard.EnableAutomation);
            Assert.Equal(AutomationLevel.Commands, vm.Dashboard.AutomationLevel);
            Assert.True(SettingsManager.EnableAutomation);
            Assert.Equal(AutomationLevel.Commands, SettingsManager.AutomationLevel);

            var saved = BuildApp(settings);
            Assert.True(saved.EnableAutomation);
            Assert.Equal(0, saved.AutomationLevel);
            Assert.False(saved.LegacyEnableExternalControl);
            string xml = WriteFile(saved);
            Assert.Contains("<EnableAutomation>true</EnableAutomation>", xml);
            Assert.Contains("<AutomationLevel>0</AutomationLevel>", xml);
            Assert.DoesNotContain("EnableExternalControl", xml);
        }

        /// <summary>The old element is never written, whatever the object
        /// holds: a file saved now cannot turn a later load's Automation
        /// on by it.</summary>
        [Fact]
        public void TheOldElement_IsNeverWritten()
        {
            string xml = WriteFile(new AppSettingsData { LegacyEnableExternalControl = true });
            Assert.DoesNotContain("EnableExternalControl", xml);
            Assert.Contains("<EnableAutomation>false</EnableAutomation>", xml);

            // The element keeps the name old files gave it.
            var element = typeof(AppSettingsData).GetProperty(nameof(AppSettingsData.LegacyEnableExternalControl))
                .GetCustomAttribute<XmlElementAttribute>();
            Assert.Equal("EnableExternalControl", element.ElementName);
            Assert.False(new AppSettingsData { LegacyEnableExternalControl = true }.ShouldSerializeLegacyEnableExternalControl());
        }

        [Theory]
        [InlineData("<EnableExternalControl>false</EnableExternalControl>")]
        [InlineData("")]
        [InlineData("<EnableAutoProfileSwitching>true</EnableAutoProfileSwitching>")]
        public void AnOldFile_WithExternalControlOffOrAbsent_LoadsWithAutomationOff(string appSettingsXml)
        {
            var old = ReadFile(appSettingsXml).AppSettings;
            Assert.False(old.EnableAutomation);
            Assert.Equal(0, old.AutomationLevel);

            var (vm, settings) = Arrange();
            vm.Dashboard.EnableAutomation = true;
            vm.Dashboard.AutomationLevel = AutomationLevel.ChangeSettings;
            LoadApp(settings, old);
            Assert.False(vm.Dashboard.EnableAutomation);
            Assert.Equal(AutomationLevel.Commands, vm.Dashboard.AutomationLevel);
            Assert.False(SettingsManager.EnableAutomation);
        }

        /// <summary>Nothing widens. The old switch turns Automation on at
        /// Commands whatever level a hand put beside it, and where the file
        /// holds Automation's own switch on, that and its level stand.</summary>
        [Theory]
        [InlineData("<EnableExternalControl>true</EnableExternalControl><AutomationLevel>3</AutomationLevel>", true, 0)]
        [InlineData("<EnableExternalControl>true</EnableExternalControl><EnableAutomation>false</EnableAutomation><AutomationLevel>2</AutomationLevel>", true, 0)]
        [InlineData("<EnableExternalControl>true</EnableExternalControl><EnableAutomation>true</EnableAutomation><AutomationLevel>2</AutomationLevel>", true, 2)]
        [InlineData("<EnableExternalControl>false</EnableExternalControl><EnableAutomation>true</EnableAutomation><AutomationLevel>1</AutomationLevel>", true, 1)]
        [InlineData("<EnableExternalControl>false</EnableExternalControl><AutomationLevel>3</AutomationLevel>", false, 3)]
        public void TheOldSwitch_WidensNothing(string appSettingsXml, bool on, int level)
        {
            var read = ReadFile(appSettingsXml).AppSettings;
            Assert.Equal(on, read.EnableAutomation);
            Assert.Equal(level, read.AutomationLevel);
            Assert.False(read.LegacyEnableExternalControl);
        }

        /// <summary>The migration is the app settings' own and runs with the
        /// file's (SettingsFileData.MigrateLegacySchema), so every load takes
        /// it.</summary>
        [Fact]
        public void TheMoveRunsWithEveryLoad()
        {
            string source = OffLimitsSource.Read("PadForge.App/Services/SettingsService.cs");
            Assert.Contains("AppSettings?.MigrateLegacySchema();", source, StringComparison.Ordinal);
            Assert.Contains("if (LegacyEnableExternalControl && !EnableAutomation)", source, StringComparison.Ordinal);
            Assert.Contains("LegacyEnableExternalControl = false;", source, StringComparison.Ordinal);
            // No view model, static or saved record keeps the old name.
            Assert.DoesNotContain("SettingsManager.EnableExternalControl", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Settings.EnableExternalControl", source, StringComparison.Ordinal);
            Assert.Null(typeof(SettingsManager).GetProperty("EnableExternalControl"));
            Assert.Null(typeof(SettingsViewModel).GetProperty("EnableExternalControl"));
            Assert.Null(typeof(AppSettingsData).GetProperty("EnableExternalControl"));
        }

        /// <summary>The External Control card folded until #597. The key a
        /// file kept for its open fold names nothing now and goes at the
        /// load, and the other keys stay.</summary>
        [Fact]
        public void TheRetiredFoldKey_IsDroppedAtLoad()
        {
            Assert.Equal("Profiles.ExternalControl", AppSettingsData.RetiredExternalControlFoldKey);
            var data = new SettingsFileData
            {
                AppSettings = new AppSettingsData
                {
                    OpenCards = new List<string> { "Profiles.Shortcuts", "Profiles.ExternalControl", "Extras.GameMods.More" },
                },
            };
            data.MigrateLegacySchema();
            Assert.Equal(new[] { "Profiles.Shortcuts", "Extras.GameMods.More" }, data.AppSettings.OpenCards);

            // A file with no open cards loads as before.
            var none = new SettingsFileData { AppSettings = new AppSettingsData { OpenCards = null } };
            none.MigrateLegacySchema();
            Assert.Null(none.AppSettings.OpenCards);
        }

        // ── A new file ──

        [Theory]
        [InlineData(false, AutomationLevel.Commands)]
        [InlineData(true, AutomationLevel.Commands)]
        [InlineData(true, AutomationLevel.Read)]
        [InlineData(true, AutomationLevel.SendInputs)]
        [InlineData(true, AutomationLevel.ChangeSettings)]
        [InlineData(false, AutomationLevel.SendInputs)]
        public void TheSwitchAndItsLevel_SurviveTheActualSaveAndLoad(bool on, AutomationLevel level)
        {
            var (vm, settings) = Arrange();
            vm.Dashboard.EnableAutomation = on;
            vm.Dashboard.AutomationLevel = level;

            var built = BuildApp(settings);
            Assert.Equal(on, built.EnableAutomation);
            Assert.Equal((int)level, built.AutomationLevel);
            // The save puts the statics in step too.
            Assert.Equal(on, SettingsManager.EnableAutomation);
            Assert.Equal(level, SettingsManager.AutomationLevel);

            // The level is saved by its number.
            string xml = WriteFile(built);
            Assert.Contains("<EnableAutomation>" + (on ? "true" : "false") + "</EnableAutomation>", xml);
            Assert.Contains("<AutomationLevel>" + (int)level + "</AutomationLevel>", xml);

            var serializer = new XmlSerializer(typeof(SettingsFileData));
            using var reader = new StringReader(xml);
            var loaded = (SettingsFileData)serializer.Deserialize(reader);
            loaded.MigrateLegacySchema();

            var (vm2, settings2) = Arrange();
            LoadApp(settings2, loaded.AppSettings);
            Assert.Equal(on, vm2.Dashboard.EnableAutomation);
            Assert.Equal(level, vm2.Dashboard.AutomationLevel);
            Assert.Equal(on, SettingsManager.EnableAutomation);
            Assert.Equal(level, SettingsManager.AutomationLevel);
        }

        /// <summary>The numbers are the file's, so they are pinned. A number
        /// no level has reads as the lowest, never as a higher one.</summary>
        [Theory]
        [InlineData(0, AutomationLevel.Commands)]
        [InlineData(1, AutomationLevel.Read)]
        [InlineData(2, AutomationLevel.SendInputs)]
        [InlineData(3, AutomationLevel.ChangeSettings)]
        [InlineData(4, AutomationLevel.Commands)]
        [InlineData(-1, AutomationLevel.Commands)]
        [InlineData(int.MaxValue, AutomationLevel.Commands)]
        public void ASavedNumber_ReadsAsItsLevel_AndAnUnknownOneAsCommands(int saved, AutomationLevel level)
        {
            Assert.Equal(level, AutomationLevels.FromSaved(saved));

            var (vm, settings) = Arrange();
            LoadApp(settings, new AppSettingsData { EnableAutomation = true, AutomationLevel = saved });
            Assert.True(vm.Dashboard.EnableAutomation);
            Assert.Equal(level, vm.Dashboard.AutomationLevel);
            Assert.Equal(level, SettingsManager.AutomationLevel);
        }

        /// <summary>A fresh file is off at Commands, and Reset All puts both
        /// back there through the load of a fresh record.</summary>
        [Fact]
        public void TheDefault_IsOffAtCommands()
        {
            var fresh = new AppSettingsData();
            Assert.False(fresh.EnableAutomation);
            Assert.Equal(0, fresh.AutomationLevel);

            var vm = new MainViewModel();
            Assert.False(vm.Dashboard.EnableAutomation);
            Assert.Equal(AutomationLevel.Commands, vm.Dashboard.AutomationLevel);
            Assert.False(vm.Dashboard.IsAutomationRunning);
            Assert.Equal(Strings.Instance.Common_Stopped, vm.Dashboard.AutomationStatus);
        }

        // ── The reset, on the view model the switch moved to ──

        [Fact]
        public void TheResets_PutTheSwitchOffAndTheLevelAtCommands()
        {
            var dash = new MainViewModel().Dashboard;
            Assert.True(DashboardViewModel.CanResetSetting(nameof(DashboardViewModel.EnableAutomation)));
            Assert.True(DashboardViewModel.CanResetSetting(nameof(DashboardViewModel.AutomationLevel)));
            Assert.True(dash.ResetSettingCommand.CanExecute(nameof(DashboardViewModel.EnableAutomation)));

            dash.EnableAutomation = true;
            dash.AutomationLevel = AutomationLevel.SendInputs;
            dash.ResetSettingCommand.Execute(nameof(DashboardViewModel.EnableAutomation));
            Assert.False(dash.EnableAutomation);
            // One reset is one setting's.
            Assert.Equal(AutomationLevel.SendInputs, dash.AutomationLevel);

            dash.EnableAutomation = true;
            dash.ResetSettingCommand.Execute(nameof(DashboardViewModel.AutomationLevel));
            Assert.Equal(AutomationLevel.Commands, dash.AutomationLevel);
            Assert.True(dash.EnableAutomation);

            // A reset of a value already at its default still says so, for
            // an editor that holds pending text.
            var raised = new List<string>();
            dash.PropertyChanged += (s, e) => raised.Add(e.PropertyName);
            dash.ResetSettingCommand.Execute(nameof(DashboardViewModel.AutomationLevel));
            Assert.Contains(nameof(DashboardViewModel.AutomationLevel), raised);

            // The Settings view model has neither the property nor its reset.
            Assert.False(SettingsViewModel.CanResetSetting("EnableExternalControl"));
            Assert.False(SettingsViewModel.CanResetSetting(nameof(DashboardViewModel.EnableAutomation)));
        }

        // ── The dirty flag ──

        /// <summary>A change to either marks the file dirty, in the Extras
        /// view model's list, where the switch lives now. The old name is in
        /// no list.</summary>
        [Fact]
        public void AChangeToEither_MarksTheFileDirty()
        {
            string window = OffLimitsSource.Read("PadForge.App/MainWindow.xaml.cs");
            int list = window.IndexOf("nameof(DashboardViewModel.EnableDsuMotionServer)", StringComparison.Ordinal);
            int mark = window.IndexOf("_settingsService.MarkDirty();", list, StringComparison.Ordinal);
            Assert.True(list > 0 && mark > list);
            string dashboardList = window[list..mark];
            Assert.Contains("or nameof(DashboardViewModel.EnableAutomation)", dashboardList, StringComparison.Ordinal);
            Assert.Contains("or nameof(DashboardViewModel.AutomationLevel)", dashboardList, StringComparison.Ordinal);
            Assert.DoesNotContain("EnableExternalControl", window, StringComparison.Ordinal);
            // The host's own state is not the file's: it changes with no edit.
            Assert.DoesNotContain("nameof(DashboardViewModel.IsAutomationRunning)", dashboardList, StringComparison.Ordinal);
            Assert.DoesNotContain("nameof(DashboardViewModel.AutomationStatus)", dashboardList, StringComparison.Ordinal);
        }

        // ── The line the Profiles page prints ──

        [Fact]
        public void TheLine_SaysWhatTheSwitchAndTheLevelAllow()
        {
            var dash = new MainViewModel().Dashboard;
            var s = Strings.Instance;
            Assert.Equal(s.Profiles_Automation_Off, dash.AutomationAllowsLine);

            var raised = new List<string>();
            dash.PropertyChanged += (sender, e) => raised.Add(e.PropertyName);

            // The level says nothing while the switch is off.
            dash.AutomationLevel = AutomationLevel.SendInputs;
            Assert.Equal(s.Profiles_Automation_Off, dash.AutomationAllowsLine);
            Assert.Contains(nameof(DashboardViewModel.AutomationAllowsLine), raised);

            raised.Clear();
            dash.EnableAutomation = true;
            Assert.Contains(nameof(DashboardViewModel.AutomationAllowsLine), raised);
            Assert.Equal(s.Profiles_Automation_SendInputs, dash.AutomationAllowsLine);
            dash.AutomationLevel = AutomationLevel.Commands;
            Assert.Equal(s.Profiles_Automation_Commands, dash.AutomationAllowsLine);
            dash.AutomationLevel = AutomationLevel.Read;
            Assert.Equal(s.Profiles_Automation_Read, dash.AutomationAllowsLine);
            dash.AutomationLevel = AutomationLevel.ChangeSettings;
            Assert.Equal(s.Profiles_Automation_ChangeSettings, dash.AutomationAllowsLine);

            // Five states, five lines.
            var lines = new[] { s.Profiles_Automation_Off, s.Profiles_Automation_Commands, s.Profiles_Automation_Read,
                s.Profiles_Automation_SendInputs, s.Profiles_Automation_ChangeSettings };
            Assert.Equal(5, lines.Distinct().Count());
            Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line)));

            // The line is said again in a new language.
            string view = OffLimitsSource.Read("PadForge.App/ViewModels/DashboardViewModel.cs");
            Assert.Contains("RefreshAutomationText();", view, StringComparison.Ordinal);
        }

        /// <summary>The host's line for where its pipe stands: running and
        /// stopped in the words every extra uses, and the two refusals with
        /// the pipe's name in them.</summary>
        [Fact]
        public void TheStatusLine_NamesThePipeWhereProgramsCannotConnect()
        {
            var s = Strings.Instance;
            Assert.Equal(s.Common_Running, MainWindow.AutomationStatusLine(ExternalControlService.PipeState.Serving));
            Assert.Equal(s.Common_Stopped, MainWindow.AutomationStatusLine(ExternalControlService.PipeState.Stopped));
            string taken = MainWindow.AutomationStatusLine(ExternalControlService.PipeState.NameTaken);
            string failed = MainWindow.AutomationStatusLine(ExternalControlService.PipeState.Failed);
            Assert.Contains(ExternalControlService.PipeName, taken, StringComparison.Ordinal);
            Assert.Contains(ExternalControlService.PipeName, failed, StringComparison.Ordinal);
            Assert.NotEqual(taken, failed);
            Assert.DoesNotContain("{0}", taken, StringComparison.Ordinal);
            Assert.DoesNotContain("{0}", failed, StringComparison.Ordinal);

            // A status the host never gave reads as stopped.
            var dash = new MainViewModel().Dashboard;
            dash.AutomationStatus = null;
            Assert.Equal(s.Common_Stopped, dash.AutomationStatus);
        }
    }
}
