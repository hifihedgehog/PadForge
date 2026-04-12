using System;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using System.Xml.Serialization;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>
    /// Service responsible for loading and saving PadForge settings to XML files.
    /// Handles the bidirectional sync between the SettingsManager's data collections
    /// and the WPF ViewModels.
    /// 
    /// Settings file search order:
    ///   1. PadForge.xml (preferred for new installs)
    ///   2. Settings.xml (generic fallback)
    /// 
    /// The settings file lives next to the executable.
    /// </summary>
    public class SettingsService
    {
        // ─────────────────────────────────────────────
        //  Constants
        // ─────────────────────────────────────────────

        /// <summary>Primary settings file name.</summary>
        public const string PrimaryFileName = "PadForge.xml";

        /// <summary>Fallback settings file name.</summary>
        public const string FallbackFileName = "Settings.xml";

        // ─────────────────────────────────────────────
        //  State
        // ─────────────────────────────────────────────

        private readonly MainViewModel _mainVm;
        private string _settingsFilePath;
        private DispatcherTimer _autoSaveTimer;

        /// <summary>
        /// Full path to the active settings file.
        /// </summary>
        public string SettingsFilePath => _settingsFilePath;

        /// <summary>
        /// Whether settings have been modified since last save.
        /// </summary>
        public bool IsDirty { get; private set; }

        /// <summary>
        /// Raised after autosave completes so callers can perform post-save actions
        /// (e.g. refreshing the default profile snapshot).
        /// </summary>
        public event EventHandler AutoSaved;

        // ─────────────────────────────────────────────
        //  Constructor
        // ─────────────────────────────────────────────

        public SettingsService(MainViewModel mainVm)
        {
            _mainVm = mainVm ?? throw new ArgumentNullException(nameof(mainVm));
        }

        // ─────────────────────────────────────────────
        //  Initialize
        // ─────────────────────────────────────────────

        /// <summary>
        /// Initializes the settings service: ensures SettingsManager collections
        /// exist, finds the settings file, and loads it.
        /// </summary>
        public void Initialize()
        {
            // Ensure SettingsManager collections are initialized.
            if (SettingsManager.UserDevices == null)
                SettingsManager.UserDevices = new DeviceCollection();
            if (SettingsManager.UserSettings == null)
                SettingsManager.UserSettings = new SettingsCollection();

            // Find or create the settings file.
            _settingsFilePath = FindSettingsFile();

            // Load settings from disk.
            if (File.Exists(_settingsFilePath))
            {
                LoadFromFile(_settingsFilePath);
            }
            else
            {
                // No settings file — initialize profiles with the Default entry.
                LoadProfiles(null, null);
            }

            // Push file path to ViewModel.
            _mainVm.Settings.SettingsFilePath = _settingsFilePath;
            _mainVm.Settings.HasUnsavedChanges = false;
            IsDirty = false;
        }

        // ─────────────────────────────────────────────
        //  File discovery
        // ─────────────────────────────────────────────

        /// <summary>
        /// Finds the settings file. Checks for the primary file first,
        /// then fallback, then creates the primary file path for new installs.
        /// </summary>
        private static string FindSettingsFile()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;

            // Check primary file.
            string primaryPath = Path.Combine(appDir, PrimaryFileName);
            if (File.Exists(primaryPath))
                return primaryPath;

            // Check fallback file.
            string fallbackPath = Path.Combine(appDir, FallbackFileName);
            if (File.Exists(fallbackPath))
                return fallbackPath;

            // Neither exists — use primary path for new file.
            return primaryPath;
        }

        // ─────────────────────────────────────────────
        //  Load
        // ─────────────────────────────────────────────

        /// <summary>
        /// Loads settings from an XML file into the SettingsManager collections.
        /// </summary>
        /// <param name="filePath">Path to the settings XML file.</param>
        public void LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath))
                return;

            try
            {
                SettingsFileData data;
                var serializer = new XmlSerializer(typeof(SettingsFileData));

                using (var stream = File.OpenRead(filePath))
                {
                    data = (SettingsFileData)serializer.Deserialize(stream);
                }

                if (data == null)
                    return;

                // Populate SettingsManager collections.
                lock (SettingsManager.UserDevices.SyncRoot)
                {
                    SettingsManager.UserDevices.Items.Clear();
                    if (data.Devices != null)
                    {
                        foreach (var ud in data.Devices)
                            SettingsManager.UserDevices.Items.Add(ud);
                    }
                }

                lock (SettingsManager.UserSettings.SyncRoot)
                {
                    SettingsManager.UserSettings.Items.Clear();
                    if (data.Settings != null)
                    {
                        foreach (var us in data.Settings)
                        {
                            // Link PadSetting — clone the template so each device
                            // has its own independent PadSetting instance. Without
                            // cloning, devices that share a checksum would share the
                            // same object, so modifying one device's settings would
                            // silently corrupt the other's.
                            if (data.PadSettings != null && us.PadSettingChecksum != null)
                            {
                                var template = data.PadSettings.FirstOrDefault(
                                    p => p.PadSettingChecksum == us.PadSettingChecksum);
                                if (template != null)
                                {
                                    // CloneDeep copies all properties + mapping arrays
                                    var ps = template.CloneDeep();
                                    us.SetPadSetting(ps);
                                }
                            }

                            SettingsManager.UserSettings.Items.Add(us);
                        }
                    }
                }

                // Purge orphaned UserSettings (MapTo == -1) left by older versions.
                SettingsManager.UserSettings.Items.RemoveAll(us => us.MapTo < 0);

                // Load app settings into ViewModel.
                if (data.AppSettings != null)
                    LoadAppSettings(data.AppSettings);

                // Load pad-specific settings.
                if (data.PadSettings != null)
                    LoadPadSettings(data.Settings, data.PadSettings);

                // Load macros into pad ViewModels.
                if (data.Macros != null)
                    LoadMacros(data.Macros);

                // Load profiles.
                LoadProfiles(data.Profiles, data.AppSettings);
            }
            catch (Exception ex)
            {
                _mainVm.StatusText = string.Format(Strings.Instance.Status_ErrorLoadingSettings_Format, ex.Message);
            }
        }

        /// <summary>
        /// Pushes application-level settings to the SettingsViewModel.
        /// </summary>
        private void LoadAppSettings(AppSettingsData appSettings)
        {
            var vm = _mainVm.Settings;
            vm.AutoStartEngine = appSettings.AutoStartEngine;
            vm.MinimizeToTray = appSettings.MinimizeToTray;
            vm.StartMinimized = appSettings.StartMinimized;
            vm.StartAtLogin = appSettings.StartAtLogin;
            vm.EnablePollingOnFocusLoss = appSettings.EnablePollingOnFocusLoss;
            vm.PollingRateMs = appSettings.PollingRateMs;
            vm.SelectedThemeIndex = appSettings.ThemeIndex;
            vm.EnableInputHiding = appSettings.EnableInputHiding;
            vm.HidHideWhitelistPaths.Clear();
            if (appSettings.HidHideWhitelistPaths != null)
            {
                foreach (var p in appSettings.HidHideWhitelistPaths)
                    if (!string.IsNullOrWhiteSpace(p))
                        vm.HidHideWhitelistPaths.Add(p);
            }
            vm.SetLanguageFromCode(appSettings.Language);
            vm.EnableAutoProfileSwitching = appSettings.EnableAutoProfileSwitching;
            SettingsManager.EnableAutoProfileSwitching = appSettings.EnableAutoProfileSwitching;
            SettingsManager.ActiveProfileId = appSettings.ActiveProfileId;
            // Migrate legacy global macros and store.
            if (appSettings.GlobalMacros != null)
                foreach (var gm in appSettings.GlobalMacros)
                    gm.MigrateLegacyTrigger();
            SettingsManager.GlobalMacros = appSettings.GlobalMacros;

            // Load per-slot created/enabled state BEFORE OutputType,
            // because setting OutputType fires PropertyChanged → RefreshNavControllerItems()
            // which reads SlotCreated[]. If SlotCreated isn't loaded yet, the sidebar
            // gets built with the wrong slot set and triggers a double-rebuild crash.
            if (appSettings.SlotCreated != null && appSettings.SlotCreated.Length >= 1)
            {
                int count = Math.Min(appSettings.SlotCreated.Length, SettingsManager.SlotCreated.Length);
                Array.Copy(appSettings.SlotCreated, SettingsManager.SlotCreated, count);
            }
            else
            {
                // Backward compat: auto-create slots for existing device assignments.
                AutoCreateSlotsFromExistingAssignments();
            }

            if (appSettings.SlotEnabled != null && appSettings.SlotEnabled.Length >= 1)
            {
                int count = Math.Min(appSettings.SlotEnabled.Length, SettingsManager.SlotEnabled.Length);
                Array.Copy(appSettings.SlotEnabled, SettingsManager.SlotEnabled, count);
            }
            // else: defaults are all true, which is correct for migration.

            // Load per-slot virtual controller types (after SlotCreated/SlotEnabled).
            if (appSettings.SlotControllerTypes != null)
            {
                for (int i = 0; i < _mainVm.Pads.Count && i < appSettings.SlotControllerTypes.Length; i++)
                {
                    // Only load types for created slots. Uncreated slots keep the
                    // default (Xbox360) to prevent stale values from previous sessions
                    // leaking into the engine's SlotControllerTypes array.
                    if (SettingsManager.SlotCreated[i] &&
                        Enum.IsDefined(typeof(Engine.VirtualControllerType), appSettings.SlotControllerTypes[i]))
                        _mainVm.Pads[i].OutputType = (Engine.VirtualControllerType)appSettings.SlotControllerTypes[i];
                }
            }

            ApplyVJoyConfigs(appSettings.VJoyConfigs);
            ApplyMidiConfigs(appSettings.MidiConfigs);

            // Load DSU motion server settings (now on Dashboard VM).
            _mainVm.Dashboard.EnableDsuMotionServer = appSettings.EnableDsuMotionServer;
            _mainVm.Dashboard.DsuMotionServerPort = appSettings.DsuMotionServerPort > 0
                ? appSettings.DsuMotionServerPort : 26760;

            // Load web controller server settings.
            _mainVm.Dashboard.EnableWebController = appSettings.EnableWebController;
            _mainVm.Dashboard.WebControllerPort = appSettings.WebControllerPort > 0
                ? appSettings.WebControllerPort : 8080;

            // Load touchpad overlay settings.
            _mainVm.Dashboard.EnableTouchpadOverlay = appSettings.EnableTouchpadOverlay;
            _mainVm.Dashboard.TouchpadOverlayOpacity = appSettings.TouchpadOverlayOpacity > 0
                ? appSettings.TouchpadOverlayOpacity : 0.25;
            _mainVm.Dashboard.TouchpadOverlayMonitor = appSettings.TouchpadOverlayMonitor;
            _mainVm.Dashboard.TouchpadOverlayLeft = appSettings.TouchpadOverlayLeft;
            _mainVm.Dashboard.TouchpadOverlayTop = appSettings.TouchpadOverlayTop;
            _mainVm.Dashboard.TouchpadOverlayWidth = appSettings.TouchpadOverlayWidth > 0
                ? appSettings.TouchpadOverlayWidth : 500;
            _mainVm.Dashboard.TouchpadOverlayHeight = appSettings.TouchpadOverlayHeight > 0
                ? appSettings.TouchpadOverlayHeight : 250;

            vm.Use2DControllerView = appSettings.Use2DControllerView;

            // Restore main window position/size (profile-independent).
            vm.MainWindowLeft = appSettings.MainWindowLeft;
            vm.MainWindowTop = appSettings.MainWindowTop;
            vm.MainWindowWidth = appSettings.MainWindowWidth > 0 ? appSettings.MainWindowWidth : 1100;
            vm.MainWindowHeight = appSettings.MainWindowHeight > 0 ? appSettings.MainWindowHeight : 720;
            vm.MainWindowState = appSettings.MainWindowState;
            vm.MainWindowFullScreen = appSettings.MainWindowFullScreen;
        }

        /// <summary>
        /// Applies per-slot vJoy configurations.
        /// Only restores configs for slots that are currently created as vJoy.
        /// </summary>
        private void ApplyVJoyConfigs(ViewModels.VJoySlotConfigData[] configs)
        {
            if (configs == null) return;
            foreach (var cfgData in configs)
            {
                int idx = cfgData.SlotIndex;
                if (idx >= 0 && idx < _mainVm.Pads.Count &&
                    SettingsManager.SlotCreated[idx] &&
                    _mainVm.Pads[idx].OutputType == Engine.VirtualControllerType.VJoy)
                {
                    var cfg = _mainVm.Pads[idx].VJoyConfig;
                    cfg.Preset = cfgData.Preset;
                    if (cfgData.Preset == ViewModels.VJoyPreset.Custom)
                    {
                        cfg.ThumbstickCount = cfgData.ThumbstickCount;
                        cfg.TriggerCount = cfgData.TriggerCount;
                        cfg.PovCount = cfgData.PovCount;
                        cfg.ButtonCount = cfgData.ButtonCount;
                    }
                }
            }
        }

        /// <summary>
        /// Applies per-slot MIDI configurations.
        /// Only restores configs for slots that are currently created as MIDI.
        /// </summary>
        private void ApplyMidiConfigs(ViewModels.MidiSlotConfigData[] configs)
        {
            if (configs == null) return;
            foreach (var cfgData in configs)
            {
                int idx = cfgData.SlotIndex;
                if (idx >= 0 && idx < _mainVm.Pads.Count &&
                    SettingsManager.SlotCreated[idx] &&
                    _mainVm.Pads[idx].OutputType == Engine.VirtualControllerType.Midi)
                {
                    var cfg = _mainVm.Pads[idx].MidiConfig;
                    cfg.Channel = cfgData.Channel;
                    cfg.Velocity = cfgData.Velocity;
                    cfg.StartCc = cfgData.StartCc;
                    cfg.CcCount = cfgData.CcCount;
                    cfg.StartNote = cfgData.StartNote;
                    cfg.NoteCount = cfgData.NoteCount;
                    _mainVm.Pads[idx].RebuildMappings();

                    lock (SettingsManager.UserSettings.SyncRoot)
                    {
                        foreach (var us in SettingsManager.UserSettings.Items)
                        {
                            if (us.MapTo != idx) continue;
                            var ps = us.GetPadSetting();
                            if (ps == null) continue;
                            foreach (var mapping in _mainVm.Pads[idx].Mappings)
                            {
                                string target = mapping.TargetSettingName;
                                string value = target.StartsWith("Midi", StringComparison.Ordinal)
                                    ? ps.GetMidiMapping(target) : string.Empty;
                                if (!string.IsNullOrEmpty(value))
                                    mapping.LoadDescriptor(value);
                                if (mapping.NegSettingName != null)
                                {
                                    string negValue = mapping.NegSettingName.StartsWith("Midi", StringComparison.Ordinal)
                                        ? ps.GetMidiMapping(mapping.NegSettingName) : string.Empty;
                                    if (!string.IsNullOrEmpty(negValue))
                                        mapping.LoadNegDescriptor(negValue);
                                }
                            }
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// For old settings files without SlotCreated: creates slots for any
        /// indices that have device assignments.
        /// </summary>
        private static void AutoCreateSlotsFromExistingAssignments()
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return;

            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    int idx = us.MapTo;
                    if (idx >= 0 && idx < InputManager.MaxPads)
                    {
                        SettingsManager.SlotCreated[idx] = true;
                        SettingsManager.SlotEnabled[idx] = true;
                    }
                }
            }
        }

        /// <summary>
        /// Pushes per-pad settings to PadViewModels.
        /// Only loads the first device encountered per slot — the user can switch
        /// to other devices via the dropdown, which triggers a live swap.
        /// </summary>
        private void LoadPadSettings(UserSetting[] settings, PadSetting[] padSettings)
        {
            if (settings == null || padSettings == null)
                return;

            var loadedSlots = new System.Collections.Generic.HashSet<int>();

            foreach (var us in settings)
            {
                int padIndex = us.MapTo;
                if (padIndex < 0 || padIndex >= _mainVm.Pads.Count)
                    continue;

                // Only load the first device's PadSetting into the ViewModel per slot.
                if (!loadedSlots.Add(padIndex))
                    continue;

                var padVm = _mainVm.Pads[padIndex];
                var ps = us.GetPadSetting();
                if (ps == null)
                    continue;

                // Load force feedback settings.
                padVm.ForceOverallGain = TryParseInt(ps.ForceOverall, 100);
                padVm.LeftMotorStrength = TryParseInt(ps.LeftMotorStrength, 100);
                padVm.RightMotorStrength = TryParseInt(ps.RightMotorStrength, 100);
                padVm.SwapMotors = ps.ForceSwapMotor == "1" ||
                    (ps.ForceSwapMotor ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);

                // Load audio bass rumble settings.
                padVm.AudioRumbleEnabled = ps.AudioRumbleEnabled == "1";
                padVm.AudioRumbleSensitivity = TryParseDouble(ps.AudioRumbleSensitivity, 4.0);
                padVm.AudioRumbleCutoffHz = TryParseDouble(ps.AudioRumbleCutoffHz, 80.0);
                padVm.AudioRumbleLeftMotor = TryParseInt(ps.AudioRumbleLeftMotor, 100);
                padVm.AudioRumbleRightMotor = TryParseInt(ps.AudioRumbleRightMotor, 100);

                // Load deadzone settings (independent X/Y).
                padVm.LeftDeadZoneShape = (int)InputManager.ParseDeadZoneShape(ps.LeftThumbDeadZoneShape);
                padVm.LeftDeadZoneX = TryParseDouble(ps.LeftThumbDeadZoneX, 0);
                padVm.LeftDeadZoneY = TryParseDouble(ps.LeftThumbDeadZoneY, 0);
                padVm.RightDeadZoneShape = (int)InputManager.ParseDeadZoneShape(ps.RightThumbDeadZoneShape);
                padVm.RightDeadZoneX = TryParseDouble(ps.RightThumbDeadZoneX, 0);
                padVm.RightDeadZoneY = TryParseDouble(ps.RightThumbDeadZoneY, 0);
                ps.MigrateAntiDeadZones();
                padVm.LeftAntiDeadZoneX = TryParseDouble(ps.LeftThumbAntiDeadZoneX, 0);
                padVm.LeftAntiDeadZoneY = TryParseDouble(ps.LeftThumbAntiDeadZoneY, 0);
                padVm.RightAntiDeadZoneX = TryParseDouble(ps.RightThumbAntiDeadZoneX, 0);
                padVm.RightAntiDeadZoneY = TryParseDouble(ps.RightThumbAntiDeadZoneY, 0);
                padVm.LeftLinear = TryParseDouble(ps.LeftThumbLinear, 0);
                padVm.RightLinear = TryParseDouble(ps.RightThumbLinear, 0);
                padVm.LeftSensitivityCurveX = ps.LeftThumbSensitivityCurveX ?? "0,0;1,1";
                padVm.LeftSensitivityCurveY = ps.LeftThumbSensitivityCurveY ?? "0,0;1,1";
                padVm.RightSensitivityCurveX = ps.RightThumbSensitivityCurveX ?? "0,0;1,1";
                padVm.RightSensitivityCurveY = ps.RightThumbSensitivityCurveY ?? "0,0;1,1";
                padVm.LeftTriggerSensitivityCurve = ps.LeftTriggerSensitivityCurve ?? "0,0;1,1";
                padVm.RightTriggerSensitivityCurve = ps.RightTriggerSensitivityCurve ?? "0,0;1,1";
                padVm.LeftMaxRangeX = TryParseDouble(ps.LeftThumbMaxRangeX, 100);
                padVm.LeftMaxRangeY = TryParseDouble(ps.LeftThumbMaxRangeY, 100);
                padVm.RightMaxRangeX = TryParseDouble(ps.RightThumbMaxRangeX, 100);
                padVm.RightMaxRangeY = TryParseDouble(ps.RightThumbMaxRangeY, 100);
                ps.MigrateMaxRangeDirections();
                padVm.LeftMaxRangeXNeg = TryParseDouble(ps.LeftThumbMaxRangeXNeg, 100);
                padVm.LeftMaxRangeYNeg = TryParseDouble(ps.LeftThumbMaxRangeYNeg, 100);
                padVm.RightMaxRangeXNeg = TryParseDouble(ps.RightThumbMaxRangeXNeg, 100);
                padVm.RightMaxRangeYNeg = TryParseDouble(ps.RightThumbMaxRangeYNeg, 100);
                padVm.LeftCenterOffsetX = TryParseDouble(ps.LeftThumbCenterOffsetX, 0);
                padVm.LeftCenterOffsetY = TryParseDouble(ps.LeftThumbCenterOffsetY, 0);
                padVm.RightCenterOffsetX = TryParseDouble(ps.RightThumbCenterOffsetX, 0);
                padVm.RightCenterOffsetY = TryParseDouble(ps.RightThumbCenterOffsetY, 0);

                // Load trigger deadzone settings.
                padVm.LeftTriggerDeadZone = TryParseDouble(ps.LeftTriggerDeadZone, 0);
                padVm.RightTriggerDeadZone = TryParseDouble(ps.RightTriggerDeadZone, 0);
                padVm.LeftTriggerAntiDeadZone = TryParseDouble(ps.LeftTriggerAntiDeadZone, 0);
                padVm.RightTriggerAntiDeadZone = TryParseDouble(ps.RightTriggerAntiDeadZone, 0);
                padVm.LeftTriggerMaxRange = TryParseDouble(ps.LeftTriggerMaxRange, 100);
                padVm.RightTriggerMaxRange = TryParseDouble(ps.RightTriggerMaxRange, 100);

                // Sync dynamic stick/trigger config items from the loaded VM properties.
                padVm.SyncAllConfigItemsFromVm();

                // Load vJoy custom stick/trigger settings for indices 2+ from dictionary.
                foreach (var stick in padVm.StickConfigs)
                {
                    if (stick.Index < 2) continue;
                    int g = stick.Index;
                    stick.DeadZoneShape = InputManager.ParseDeadZoneShape(ps.GetVJoyMapping($"VJoyStick{g}DzShape"));
                    stick.DeadZoneX = TryParseDouble(ps.GetVJoyMapping($"VJoyStick{g}DzX"), 0);
                    stick.DeadZoneY = TryParseDouble(ps.GetVJoyMapping($"VJoyStick{g}DzY"), 0);
                    stick.AntiDeadZoneX = TryParseDouble(ps.GetVJoyMapping($"VJoyStick{g}AdzX"), 0);
                    stick.AntiDeadZoneY = TryParseDouble(ps.GetVJoyMapping($"VJoyStick{g}AdzY"), 0);
                    stick.Linear = TryParseDouble(ps.GetVJoyMapping($"VJoyStick{g}Linear"), 0);
                    stick.SensitivityCurveX = ps.GetVJoyMapping($"VJoyStick{g}CurveX") ?? "0,0;1,1";
                    stick.SensitivityCurveY = ps.GetVJoyMapping($"VJoyStick{g}CurveY") ?? "0,0;1,1";
                    stick.CenterOffsetX = TryParseDouble(ps.GetVJoyMapping($"VJoyStick{g}CofX"), 0);
                    stick.CenterOffsetY = TryParseDouble(ps.GetVJoyMapping($"VJoyStick{g}CofY"), 0);
                    stick.MaxRangeX = TryParseDouble(ps.GetVJoyMapping($"VJoyStick{g}MrX"), 100);
                    stick.MaxRangeY = TryParseDouble(ps.GetVJoyMapping($"VJoyStick{g}MrY"), 100);
                    stick.MaxRangeXNeg = TryParseDouble(ps.GetVJoyMapping($"VJoyStick{g}MrXN"), stick.MaxRangeX);
                    stick.MaxRangeYNeg = TryParseDouble(ps.GetVJoyMapping($"VJoyStick{g}MrYN"), stick.MaxRangeY);
                }
                foreach (var trig in padVm.TriggerConfigs)
                {
                    if (trig.Index < 2) continue;
                    int g = trig.Index;
                    trig.DeadZone = TryParseDouble(ps.GetVJoyMapping($"VJoyTrigger{g}Dz"), 0);
                    trig.AntiDeadZone = TryParseDouble(ps.GetVJoyMapping($"VJoyTrigger{g}Adz"), 0);
                    trig.MaxRange = TryParseDouble(ps.GetVJoyMapping($"VJoyTrigger{g}Mr"), 100);
                    trig.SensitivityCurve = ps.GetVJoyMapping($"VJoyTrigger{g}Curve") ?? "0,0;1,1";
                }

                // Load mapping descriptors into mapping rows.
                LoadMappingDescriptors(padVm, ps);
            }
        }

        /// <summary>
        /// Populates PadViewModel mapping rows from a PadSetting's descriptor strings.
        /// </summary>
        private static void LoadMappingDescriptors(PadViewModel padVm, PadSetting ps)
        {
            foreach (var mapping in padVm.Mappings)
            {
                string value = GetPadSettingProperty(ps, mapping.TargetSettingName);
                mapping.SourceDescriptor = value ?? string.Empty;

                if (mapping.NegSettingName != null)
                {
                    string negValue = GetPadSettingProperty(ps, mapping.NegSettingName);
                    mapping.NegSourceDescriptor = negValue ?? string.Empty;
                }

                // Load per-mapping deadzone.
                string dzStr = ps.GetMappingDeadZone(mapping.TargetSettingName);
                mapping.MappingDeadZone = int.TryParse(dzStr, out int dz) && dz > 0 ? dz : 50;
            }
        }

        /// <summary>
        /// Populates pad ViewModels with macros from serialized data.
        /// </summary>
        private void LoadMacros(MacroData[] macros)
        {
            // Clear existing macros on all pads.
            foreach (var pad in _mainVm.Pads)
                pad.Macros.Clear();

            foreach (var md in macros)
            {
                if (md.PadIndex < 0 || md.PadIndex >= _mainVm.Pads.Count)
                    continue;

                var padVm = _mainVm.Pads[md.PadIndex];
                var macro = new MacroItem
                {
                    Name = md.Name ?? "Macro",
                    IsEnabled = md.IsEnabled,
                    TriggerButtons = md.TriggerButtons,
                    TriggerCustomButtons = md.TriggerCustomButtons,
                    TriggerDeviceGuid = Guid.TryParse(md.TriggerDeviceGuid, out var parsedGuid)
                        ? parsedGuid : Guid.Empty,
                    TriggerRawButtons = ParseRawButtonIndices(md.TriggerRawButtons),
                    TriggerSource = md.TriggerSource,
                    TriggerMode = md.TriggerMode,
                    ConsumeTriggerButtons = md.ConsumeTriggerButtons,
                    RepeatMode = md.RepeatMode,
                    RepeatCount = md.RepeatCount,
                    RepeatDelayMs = md.RepeatDelayMs,
                    TriggerAxisTargetList = md.TriggerAxisTargets,
                    TriggerAxisThreshold = md.TriggerAxisThreshold > 0 ? md.TriggerAxisThreshold : 50,
                    TriggerPovs = md.TriggerPovs ?? Array.Empty<string>()
                };

                if (md.Actions != null)
                {
                    foreach (var ad in md.Actions)
                    {
                        macro.Actions.Add(new MacroAction
                        {
                            Type = ad.Type,
                            ButtonFlags = ad.ButtonFlags,
                            CustomButtons = ad.CustomButtons,
                            KeyCode = ad.KeyCode,
                            KeyString = !string.IsNullOrEmpty(ad.KeyString)
                                ? ad.KeyString
                                : (ad.KeyCode != 0 ? $"{{{(VirtualKey)ad.KeyCode}}}" : ""),
                            DurationMs = ad.DurationMs,
                            AxisValue = ad.AxisValue,
                            AxisTarget = ad.AxisTarget,
                            AxisSource = ad.AxisSource,
                            SourceDeviceGuid = Guid.TryParse(ad.SourceDeviceGuid, out var devGuid)
                                ? devGuid : Guid.Empty,
                            SourceDeviceAxisIndex = ad.SourceDeviceAxisIndex,
                            ProcessName = ad.ProcessName ?? "",
                            VolumeLimit = ad.VolumeLimit > 0 ? ad.VolumeLimit : 100,
                            MouseSensitivity = ad.MouseSensitivity > 0 ? ad.MouseSensitivity : 10f,
                            MouseButton = ad.MouseButton,
                            InvertAxis = ad.InvertAxis,
                            ShowVolumeOsd = ad.ShowVolumeOsd
                        });
                    }
                }

                // Set after actions are populated so propagation reaches all of them.
                var style = MacroButtonNames.DeriveStyle(padVm.OutputType, padVm.VJoyConfig?.Preset ?? VJoyPreset.Xbox360);
                int btnCount = (padVm.OutputType == VirtualControllerType.VJoy ? padVm.VJoyConfig?.ButtonCount : null) ?? 11;
                macro.CustomButtonCount = btnCount;
                macro.ButtonStyle = style;
                foreach (var action in macro.Actions)
                    action.CustomButtonCount = btnCount;

                padVm.Macros.Add(macro);
            }
        }

        /// <summary>
        /// Parses a comma-separated string of button indices (e.g. "13,14") into an int array.
        /// Returns empty array for null/empty input.
        /// </summary>
        private static int[] ParseRawButtonIndices(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Array.Empty<int>();
            var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = new System.Collections.Generic.List<int>(parts.Length);
            foreach (var part in parts)
            {
                if (int.TryParse(part, out int idx))
                    result.Add(idx);
            }
            return result.ToArray();
        }

        /// <summary>
        /// Loads profiles from serialized data into SettingsManager and the ViewModel.
        /// </summary>
        private void LoadProfiles(ProfileData[] profiles, AppSettingsData appSettings)
        {
            SettingsManager.Profiles.Clear();
            _mainVm.Settings.ProfileItems.Clear();

            // Always include the built-in Default profile at the top.
            var defaultItem = new ViewModels.ProfileListItem
            {
                Id = ViewModels.ProfileListItem.DefaultProfileId,
                Name = Strings.Instance.Profile_Default,
            };
            var slotTypes = Enumerable.Range(0, SettingsManager.SlotCreated.Length)
                .Select(i => i < _mainVm.Pads.Count ? (int)_mainVm.Pads[i].OutputType : 0).ToArray();
            UpdateTopologyCounts(defaultItem, SettingsManager.SlotCreated, slotTypes);
            _mainVm.Settings.ProfileItems.Add(defaultItem);

            if (profiles != null)
            {
                foreach (var p in profiles)
                {
                    SettingsManager.Profiles.Add(p);
                    var item = new ViewModels.ProfileListItem
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Executables = FormatExePaths(p.ExecutableNames),
                    };
                    UpdateTopologyCounts(item, p.SlotCreated, p.SlotControllerTypes);
                    _mainVm.Settings.ProfileItems.Add(item);
                }
            }

            // Update active profile display.
            string activeId = appSettings?.ActiveProfileId;
            var active = SettingsManager.Profiles.Find(p => p.Id == activeId);
            _mainVm.Settings.ActiveProfileInfo = active?.Name ?? Strings.Instance.Profile_Default;

            // If a named profile was active at shutdown, snapshot the default
            // profile's state (loaded by LoadAppSettings) before overwriting with
            // the active profile's topology. InputService.Start uses this snapshot
            // so switching back to Default restores the correct state.
            if (active != null)
            {
                // Restore the default profile snapshot from the XML. This was
                // persisted by BuildAppSettings when a named profile was active,
                // and contains the default's full state (slots, device assignments,
                // configs). The runtime state at this point has the named profile's
                // device assignments (loaded by LoadPadSettings), so we can't build
                // the default snapshot from runtime — it must come from the XML.
                SettingsManager.PendingDefaultSnapshot = appSettings?.DefaultProfileSnapshot;

                if (active.SlotCreated != null)
                {
                    int count = Math.Min(active.SlotCreated.Length, SettingsManager.SlotCreated.Length);
                    Array.Copy(active.SlotCreated, SettingsManager.SlotCreated, count);
                }

                if (active.SlotEnabled != null)
                {
                    int count = Math.Min(active.SlotEnabled.Length, SettingsManager.SlotEnabled.Length);
                    Array.Copy(active.SlotEnabled, SettingsManager.SlotEnabled, count);
                }

                if (active.SlotControllerTypes != null)
                {
                    for (int i = 0; i < _mainVm.Pads.Count && i < active.SlotControllerTypes.Length; i++)
                    {
                        if (SettingsManager.SlotCreated[i] &&
                            Enum.IsDefined(typeof(Engine.VirtualControllerType), active.SlotControllerTypes[i]))
                            _mainVm.Pads[i].OutputType = (Engine.VirtualControllerType)active.SlotControllerTypes[i];
                    }
                }

                // Now that SlotCreated and OutputType are restored, apply vJoy/MIDI
                // configs from the profile's own snapshot.
                ApplyVJoyConfigs(active.VJoyConfigs);
                ApplyMidiConfigs(active.MidiConfigs);

                // Apply DSU/Web/overlay settings from the active profile.
                _mainVm.Dashboard.EnableDsuMotionServer = active.EnableDsuMotionServer;
                if (active.DsuMotionServerPort >= 1024 && active.DsuMotionServerPort <= 65535)
                    _mainVm.Dashboard.DsuMotionServerPort = active.DsuMotionServerPort;
                _mainVm.Dashboard.EnableWebController = active.EnableWebController;
                if (active.WebControllerPort >= 1024 && active.WebControllerPort <= 65535)
                    _mainVm.Dashboard.WebControllerPort = active.WebControllerPort;
                _mainVm.Dashboard.EnableTouchpadOverlay = active.EnableTouchpadOverlay;
                _mainVm.Dashboard.TouchpadOverlayOpacity = active.TouchpadOverlayOpacity > 0
                    ? active.TouchpadOverlayOpacity : 0.25;
                _mainVm.Dashboard.TouchpadOverlayMonitor = active.TouchpadOverlayMonitor;
                _mainVm.Dashboard.TouchpadOverlayLeft = active.TouchpadOverlayLeft;
                _mainVm.Dashboard.TouchpadOverlayTop = active.TouchpadOverlayTop;
                _mainVm.Dashboard.TouchpadOverlayWidth = active.TouchpadOverlayWidth > 0
                    ? active.TouchpadOverlayWidth : 500;
                _mainVm.Dashboard.TouchpadOverlayHeight = active.TouchpadOverlayHeight > 0
                    ? active.TouchpadOverlayHeight : 250;
            }
        }

        /// <summary>
        /// If a profile is currently active, updates its stored snapshot from
        /// the current runtime state so that edits made while the profile was
        /// active are persisted back to it. Called during Save after checksums
        /// have been recomputed.
        /// </summary>
        private void UpdateActiveProfileSnapshot()
        {
            string activeId = SettingsManager.ActiveProfileId;
            if (string.IsNullOrEmpty(activeId))
                return;

            var profile = SettingsManager.Profiles.Find(p => p.Id == activeId);
            if (profile == null)
                return;

            var entries = new System.Collections.Generic.List<ProfileEntry>();
            var padSettings = new System.Collections.Generic.List<PadSetting>();
            var seen = new System.Collections.Generic.HashSet<string>();

            lock (SettingsManager.UserSettings.SyncRoot)
            {
                foreach (var us in SettingsManager.UserSettings.Items)
                {
                    var ps = us.GetPadSetting();
                    if (ps == null) continue;

                    entries.Add(new ProfileEntry
                    {
                        InstanceGuid = us.InstanceGuid,
                        ProductGuid = us.ProductGuid,
                        MapTo = us.MapTo,
                        PadSettingChecksum = ps.PadSettingChecksum
                    });

                    if (seen.Add(ps.PadSettingChecksum))
                        padSettings.Add(ps.CloneDeep());
                }
            }

            profile.Entries = entries.ToArray();
            profile.PadSettings = padSettings.ToArray();
            profile.SlotCreated = (bool[])SettingsManager.SlotCreated.Clone();
            profile.SlotEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            profile.SlotControllerTypes = Enumerable.Range(0, _mainVm.Pads.Count)
                .Select(i => (int)_mainVm.Pads[i].OutputType).ToArray();
            profile.VJoyConfigs = BuildVJoyConfigSnapshot();
            profile.MidiConfigs = BuildMidiConfigSnapshot();
            profile.EnableDsuMotionServer = _mainVm.Dashboard.EnableDsuMotionServer;
            profile.DsuMotionServerPort = _mainVm.Dashboard.DsuMotionServerPort;
            profile.EnableWebController = _mainVm.Dashboard.EnableWebController;
            profile.WebControllerPort = _mainVm.Dashboard.WebControllerPort;
            profile.EnableTouchpadOverlay = _mainVm.Dashboard.EnableTouchpadOverlay;
            profile.TouchpadOverlayOpacity = _mainVm.Dashboard.TouchpadOverlayOpacity;
            profile.TouchpadOverlayMonitor = _mainVm.Dashboard.TouchpadOverlayMonitor;
            profile.TouchpadOverlayLeft = _mainVm.Dashboard.TouchpadOverlayLeft;
            profile.TouchpadOverlayTop = _mainVm.Dashboard.TouchpadOverlayTop;
            profile.TouchpadOverlayWidth = _mainVm.Dashboard.TouchpadOverlayWidth;
            profile.TouchpadOverlayHeight = _mainVm.Dashboard.TouchpadOverlayHeight;
        }

        /// <summary>
        /// Formats a profile's topology into a compact label like "2x Xbox, 1x DS4".
        /// Returns empty string for old profiles without topology data.
        /// </summary>
        internal static string FormatTopologyLabel(bool[] slotCreated, int[] slotControllerTypes)
        {
            CountTopology(slotCreated, slotControllerTypes, out int xbox, out int ds4, out int vjoy, out int midi, out int kbm);
            var parts = new System.Collections.Generic.List<string>();
            if (xbox > 0) parts.Add($"{xbox}x Xbox");
            if (ds4 > 0) parts.Add($"{ds4}x DS4");
            if (vjoy > 0) parts.Add($"{vjoy}x vJoy");
            if (midi > 0) parts.Add($"{midi}x MIDI");
            if (kbm > 0) parts.Add($"{kbm}x KB+M");
            return parts.Count > 0 ? string.Join(", ", parts) : Strings.Instance.Profiles_NoSlots;
        }

        internal static void UpdateTopologyCounts(ViewModels.ProfileListItem item,
            bool[] slotCreated, int[] slotControllerTypes)
        {
            CountTopology(slotCreated, slotControllerTypes, out int xbox, out int ds4, out int vjoy, out int midi, out int kbm);
            item.XboxCount = xbox;
            item.DS4Count = ds4;
            item.VJoyCount = vjoy;
            item.MidiCount = midi;
            item.KbmCount = kbm;
            item.TopologyLabel = FormatTopologyLabel(slotCreated, slotControllerTypes);
        }

        private static void CountTopology(bool[] slotCreated, int[] slotControllerTypes,
            out int xbox, out int ds4, out int vjoy, out int midi, out int kbm)
        {
            xbox = 0; ds4 = 0; vjoy = 0; midi = 0; kbm = 0;
            if (slotCreated == null) return;
            for (int i = 0; i < slotCreated.Length; i++)
            {
                if (!slotCreated[i]) continue;
                int type = (slotControllerTypes != null && i < slotControllerTypes.Length)
                    ? slotControllerTypes[i] : 0;
                switch (type)
                {
                    case 1: ds4++; break;
                    case 2: vjoy++; break;
                    case 3: midi++; break;
                    case 4: kbm++; break;
                    default: xbox++; break;
                }
            }
        }

        /// <summary>
        /// Formats pipe-separated full paths into a display string showing just file names.
        /// </summary>
        private static string FormatExePaths(string pipeSeparatedPaths)
        {
            if (string.IsNullOrEmpty(pipeSeparatedPaths))
                return string.Empty;

            var parts = pipeSeparatedPaths.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var names = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                names[i] = System.IO.Path.GetFileName(parts[i]);
            return string.Join(", ", names);
        }

        // ─────────────────────────────────────────────
        //  Save
        // ─────────────────────────────────────────────

        /// <summary>
        /// Saves current settings to the active settings file.
        /// </summary>
        public void Save()
        {
            SaveToFile(_settingsFilePath);
        }

        /// <summary>
        /// Saves all settings to the specified XML file.
        /// </summary>
        /// <param name="filePath">Output file path.</param>
        public void SaveToFile(string filePath)
        {
            try
            {
                var data = new SettingsFileData();

                // Push ViewModel values to PadSetting objects FIRST,
                // before collecting data for serialization.
                UpdatePadSettingsFromViewModels();

                // Flush vJoy mappings from in-memory dictionaries to serializable arrays,
                // then recompute checksums for ALL PadSettings and sync to UserSettings.
                lock (SettingsManager.UserSettings.SyncRoot)
                {
                    foreach (var us in SettingsManager.UserSettings.Items)
                    {
                        var ps = us.GetPadSetting();
                        if (ps != null)
                        {
                            ps.FlushVJoyMappings();
                            ps.FlushMidiMappings();
                            ps.FlushKbmMappings();
                            ps.FlushMappingDeadZones();
                            ps.UpdateChecksum();
                            us.PadSettingChecksum = ps.PadSettingChecksum;
                        }
                    }
                }

                // If a profile is currently active, update its snapshot so
                // any edits made while the profile was active are persisted.
                UpdateActiveProfileSnapshot();

                // Collect devices.
                lock (SettingsManager.UserDevices.SyncRoot)
                {
                    data.Devices = SettingsManager.UserDevices.Items.ToArray();
                }

                // Collect user settings and unique pad settings (deduplicated by checksum).
                lock (SettingsManager.UserSettings.SyncRoot)
                {
                    data.Settings = SettingsManager.UserSettings.Items.ToArray();

                    var seen = new System.Collections.Generic.HashSet<string>();
                    var uniquePadSettings = new System.Collections.Generic.List<PadSetting>();
                    foreach (var us in SettingsManager.UserSettings.Items)
                    {
                        var ps = us.GetPadSetting();
                        if (ps != null && seen.Add(ps.PadSettingChecksum))
                            uniquePadSettings.Add(ps);
                    }
                    data.PadSettings = uniquePadSettings.ToArray();
                }

                // Collect app settings from ViewModel.
                data.AppSettings = BuildAppSettings();

                // Collect macros from all pad ViewModels.
                data.Macros = BuildMacroData();

                // Collect profiles.
                if (SettingsManager.Profiles.Count > 0)
                    data.Profiles = SettingsManager.Profiles.ToArray();

                // Serialize.
                var serializer = new XmlSerializer(typeof(SettingsFileData));
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using (var stream = File.Create(filePath))
                {
                    serializer.Serialize(stream, data);
                }

                IsDirty = false;
                _mainVm.Settings.HasUnsavedChanges = false;
                _mainVm.StatusText = string.Format(Strings.Instance.Status_SettingsSaved_Format, Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                _mainVm.StatusText = string.Format(Strings.Instance.Status_ErrorSavingSettings_Format, ex.Message);
            }
        }

        /// <summary>
        /// Builds an AppSettingsData from the current SettingsViewModel state.
        /// </summary>
        private AppSettingsData BuildAppSettings()
        {
            var vm = _mainVm.Settings;
            // Sync the ViewModel toggle to the static state.
            SettingsManager.EnableAutoProfileSwitching = vm.EnableAutoProfileSwitching;

            // Collect per-slot controller types from PadViewModels.
            var slotTypes = new int[_mainVm.Pads.Count];
            for (int i = 0; i < _mainVm.Pads.Count; i++)
                slotTypes[i] = (int)_mainVm.Pads[i].OutputType;

            // Collect per-slot vJoy configurations.
            var vjoyConfigs = new System.Collections.Generic.List<ViewModels.VJoySlotConfigData>();
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var cfg = _mainVm.Pads[i].VJoyConfig;
                vjoyConfigs.Add(new ViewModels.VJoySlotConfigData
                {
                    SlotIndex = i,
                    Preset = cfg.Preset,
                    ThumbstickCount = cfg.ThumbstickCount,
                    TriggerCount = cfg.TriggerCount,
                    PovCount = cfg.PovCount,
                    ButtonCount = cfg.ButtonCount
                });
            }

            // AppSettings always stores the DEFAULT profile's per-slot state.
            // When a named profile is active, use the saved default snapshot
            // so the named profile's state doesn't contaminate the default.
            var defaultSnap = SettingsManager.PendingDefaultSnapshot;
            bool isDefault = string.IsNullOrEmpty(SettingsManager.ActiveProfileId)
                          || defaultSnap == null;

            return new AppSettingsData
            {
                AutoStartEngine = vm.AutoStartEngine,
                MinimizeToTray = vm.MinimizeToTray,
                StartMinimized = vm.StartMinimized,
                StartAtLogin = vm.StartAtLogin,
                EnablePollingOnFocusLoss = vm.EnablePollingOnFocusLoss,
                PollingRateMs = vm.PollingRateMs,
                ThemeIndex = vm.SelectedThemeIndex,
                Language = vm.LanguageCode,
                EnableAutoProfileSwitching = vm.EnableAutoProfileSwitching,
                ActiveProfileId = SettingsManager.ActiveProfileId,
                GlobalMacros = SettingsManager.GlobalMacros,
                SlotControllerTypes = isDefault ? slotTypes : defaultSnap.SlotControllerTypes,
                SlotCreated = isDefault
                    ? (bool[])SettingsManager.SlotCreated.Clone()
                    : defaultSnap.SlotCreated,
                SlotEnabled = isDefault
                    ? (bool[])SettingsManager.SlotEnabled.Clone()
                    : defaultSnap.SlotEnabled,
                EnableDsuMotionServer = _mainVm.Dashboard.EnableDsuMotionServer,
                DsuMotionServerPort = _mainVm.Dashboard.DsuMotionServerPort,
                EnableWebController = _mainVm.Dashboard.EnableWebController,
                WebControllerPort = _mainVm.Dashboard.WebControllerPort,
                EnableTouchpadOverlay = _mainVm.Dashboard.EnableTouchpadOverlay,
                TouchpadOverlayOpacity = _mainVm.Dashboard.TouchpadOverlayOpacity,
                TouchpadOverlayMonitor = _mainVm.Dashboard.TouchpadOverlayMonitor,
                TouchpadOverlayLeft = _mainVm.Dashboard.TouchpadOverlayLeft,
                TouchpadOverlayTop = _mainVm.Dashboard.TouchpadOverlayTop,
                TouchpadOverlayWidth = _mainVm.Dashboard.TouchpadOverlayWidth,
                TouchpadOverlayHeight = _mainVm.Dashboard.TouchpadOverlayHeight,
                MainWindowLeft = vm.MainWindowLeft,
                MainWindowTop = vm.MainWindowTop,
                MainWindowWidth = vm.MainWindowWidth,
                MainWindowHeight = vm.MainWindowHeight,
                MainWindowState = vm.MainWindowState,
                MainWindowFullScreen = vm.MainWindowFullScreen,
                Use2DControllerView = vm.Use2DControllerView,
                EnableInputHiding = vm.EnableInputHiding,
                HidHideWhitelistPaths = vm.HidHideWhitelistPaths.Count > 0
                    ? vm.HidHideWhitelistPaths.ToArray()
                    : null,
                VJoyConfigs = isDefault ? vjoyConfigs.ToArray() : defaultSnap.VJoyConfigs,
                MidiConfigs = isDefault ? BuildMidiConfigs() : defaultSnap.MidiConfigs,
                DefaultProfileSnapshot = isDefault ? null : defaultSnap
            };
        }

        /// <summary>
        /// Snapshots vJoy configs for only created vJoy slots (for profile storage).
        /// </summary>
        private ViewModels.VJoySlotConfigData[] BuildVJoyConfigSnapshot()
        {
            var list = new System.Collections.Generic.List<ViewModels.VJoySlotConfigData>();
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                if (!SettingsManager.SlotCreated[i] ||
                    _mainVm.Pads[i].OutputType != Engine.VirtualControllerType.VJoy)
                    continue;
                var cfg = _mainVm.Pads[i].VJoyConfig;
                list.Add(new ViewModels.VJoySlotConfigData
                {
                    SlotIndex = i,
                    Preset = cfg.Preset,
                    ThumbstickCount = cfg.ThumbstickCount,
                    TriggerCount = cfg.TriggerCount,
                    PovCount = cfg.PovCount,
                    ButtonCount = cfg.ButtonCount
                });
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>
        /// Snapshots MIDI configs for only created MIDI slots (for profile storage).
        /// </summary>
        private ViewModels.MidiSlotConfigData[] BuildMidiConfigSnapshot()
        {
            var list = new System.Collections.Generic.List<ViewModels.MidiSlotConfigData>();
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                if (!SettingsManager.SlotCreated[i] ||
                    _mainVm.Pads[i].OutputType != Engine.VirtualControllerType.Midi)
                    continue;
                var cfg = _mainVm.Pads[i].MidiConfig;
                list.Add(new ViewModels.MidiSlotConfigData
                {
                    SlotIndex = i,
                    Channel = cfg.Channel,
                    Velocity = cfg.Velocity,
                    CcCount = cfg.CcCount,
                    StartCc = cfg.StartCc,
                    NoteCount = cfg.NoteCount,
                    StartNote = cfg.StartNote
                });
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        private ViewModels.MidiSlotConfigData[] BuildMidiConfigs()
        {
            var list = new System.Collections.Generic.List<ViewModels.MidiSlotConfigData>();
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var cfg = _mainVm.Pads[i].MidiConfig;
                list.Add(new ViewModels.MidiSlotConfigData
                {
                    SlotIndex = i,
                    Channel = cfg.Channel,
                    Velocity = cfg.Velocity,
                    CcCount = cfg.CcCount,
                    StartCc = cfg.StartCc,
                    NoteCount = cfg.NoteCount,
                    StartNote = cfg.StartNote
                });
            }
            return list.ToArray();
        }

        /// <summary>
        /// Collects macro data from all pad ViewModels for serialization.
        /// </summary>
        private MacroData[] BuildMacroData()
        {
            var list = new System.Collections.Generic.List<MacroData>();

            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                foreach (var macro in padVm.Macros)
                {
                    list.Add(new MacroData
                    {
                        PadIndex = i,
                        Name = macro.Name,
                        IsEnabled = macro.IsEnabled,
                        TriggerButtons = macro.TriggerButtons,
                        TriggerDeviceGuid = macro.TriggerDeviceGuid != Guid.Empty
                            ? macro.TriggerDeviceGuid.ToString("N") : null,
                        TriggerRawButtons = macro.TriggerRawButtons.Length > 0
                            ? string.Join(",", macro.TriggerRawButtons) : null,
                        TriggerSource = macro.TriggerSource,
                        TriggerMode = macro.TriggerMode,
                        ConsumeTriggerButtons = macro.ConsumeTriggerButtons,
                        RepeatMode = macro.RepeatMode,
                        RepeatCount = macro.RepeatCount,
                        RepeatDelayMs = macro.RepeatDelayMs,
                        TriggerCustomButtons = macro.TriggerCustomButtons,
                        TriggerAxisTargets = macro.TriggerAxisTargetList,
                        TriggerAxisThreshold = macro.TriggerAxisThreshold,
                        TriggerPovs = macro.TriggerPovs?.Length > 0 ? macro.TriggerPovs : null,
                        Actions = macro.Actions.Select(a => new ActionData
                        {
                            Type = a.Type,
                            ButtonFlags = a.ButtonFlags,
                            CustomButtons = a.CustomButtons,
                            KeyCode = a.ParsedKeyCodes.Length > 0 ? a.ParsedKeyCodes[0] : a.KeyCode,
                            KeyString = a.KeyString,
                            DurationMs = a.DurationMs,
                            AxisValue = a.AxisValue,
                            AxisTarget = a.AxisTarget,
                            AxisSource = a.AxisSource,
                            SourceDeviceGuid = a.SourceDeviceGuid != Guid.Empty
                                ? a.SourceDeviceGuid.ToString("N") : null,
                            SourceDeviceAxisIndex = a.SourceDeviceAxisIndex,
                            ProcessName = a.ProcessName,
                            VolumeLimit = a.VolumeLimit,
                            MouseSensitivity = a.MouseSensitivity,
                            MouseButton = a.MouseButton,
                            InvertAxis = a.InvertAxis,
                            ShowVolumeOsd = a.ShowVolumeOsd
                        }).ToArray()
                    });
                }
            }

            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>
        /// Pushes ViewModel values back into the currently selected device's
        /// PadSetting per slot. Non-selected devices retain their own settings.
        /// </summary>
        private void UpdatePadSettingsFromViewModels()
        {
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                for (int i = 0; i < _mainVm.Pads.Count; i++)
                {
                    var padVm = _mainVm.Pads[i];
                    var selected = padVm.SelectedMappedDevice;
                    if (selected == null || selected.InstanceGuid == Guid.Empty)
                        continue;

                    var us = SettingsManager.FindSettingByInstanceGuidAndSlot(selected.InstanceGuid, i);
                    if (us == null) continue;

                    var ps = us.GetPadSetting();
                    if (ps == null) continue;

                    // Write force feedback settings.
                    ps.ForceOverall = padVm.ForceOverallGain.ToString();
                    ps.LeftMotorStrength = padVm.LeftMotorStrength.ToString();
                    ps.RightMotorStrength = padVm.RightMotorStrength.ToString();
                    ps.ForceSwapMotor = padVm.SwapMotors ? "1" : "0";

                    // Write audio bass rumble settings.
                    ps.AudioRumbleEnabled = padVm.AudioRumbleEnabled ? "1" : "0";
                    ps.AudioRumbleSensitivity = padVm.AudioRumbleSensitivity.ToString("F1");
                    ps.AudioRumbleCutoffHz = padVm.AudioRumbleCutoffHz.ToString("F0");
                    ps.AudioRumbleLeftMotor = padVm.AudioRumbleLeftMotor.ToString();
                    ps.AudioRumbleRightMotor = padVm.AudioRumbleRightMotor.ToString();

                    // Write deadzone settings (independent X/Y).
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    ps.LeftThumbDeadZoneShape = padVm.LeftDeadZoneShape.ToString();
                    ps.LeftThumbDeadZoneX = padVm.LeftDeadZoneX.ToString(ic);
                    ps.LeftThumbDeadZoneY = padVm.LeftDeadZoneY.ToString(ic);
                    ps.RightThumbDeadZoneShape = padVm.RightDeadZoneShape.ToString();
                    ps.RightThumbDeadZoneX = padVm.RightDeadZoneX.ToString(ic);
                    ps.RightThumbDeadZoneY = padVm.RightDeadZoneY.ToString(ic);
                    ps.LeftThumbAntiDeadZoneX = padVm.LeftAntiDeadZoneX.ToString(ic);
                    ps.LeftThumbAntiDeadZoneY = padVm.LeftAntiDeadZoneY.ToString(ic);
                    ps.RightThumbAntiDeadZoneX = padVm.RightAntiDeadZoneX.ToString(ic);
                    ps.RightThumbAntiDeadZoneY = padVm.RightAntiDeadZoneY.ToString(ic);
                    ps.LeftThumbLinear = padVm.LeftLinear.ToString(ic);
                    ps.RightThumbLinear = padVm.RightLinear.ToString(ic);
                    ps.LeftThumbSensitivityCurveX = padVm.LeftSensitivityCurveX;
                    ps.LeftThumbSensitivityCurveY = padVm.LeftSensitivityCurveY;
                    ps.RightThumbSensitivityCurveX = padVm.RightSensitivityCurveX;
                    ps.RightThumbSensitivityCurveY = padVm.RightSensitivityCurveY;
                    ps.LeftTriggerSensitivityCurve = padVm.LeftTriggerSensitivityCurve;
                    ps.RightTriggerSensitivityCurve = padVm.RightTriggerSensitivityCurve;
                    ps.LeftThumbMaxRangeX = padVm.LeftMaxRangeX.ToString(ic);
                    ps.LeftThumbMaxRangeY = padVm.LeftMaxRangeY.ToString(ic);
                    ps.RightThumbMaxRangeX = padVm.RightMaxRangeX.ToString(ic);
                    ps.RightThumbMaxRangeY = padVm.RightMaxRangeY.ToString(ic);
                    ps.LeftThumbMaxRangeXNeg = padVm.LeftMaxRangeXNeg.ToString(ic);
                    ps.LeftThumbMaxRangeYNeg = padVm.LeftMaxRangeYNeg.ToString(ic);
                    ps.RightThumbMaxRangeXNeg = padVm.RightMaxRangeXNeg.ToString(ic);
                    ps.RightThumbMaxRangeYNeg = padVm.RightMaxRangeYNeg.ToString(ic);
                    ps.LeftThumbCenterOffsetX = padVm.LeftCenterOffsetX.ToString(ic);
                    ps.LeftThumbCenterOffsetY = padVm.LeftCenterOffsetY.ToString(ic);
                    ps.RightThumbCenterOffsetX = padVm.RightCenterOffsetX.ToString(ic);
                    ps.RightThumbCenterOffsetY = padVm.RightCenterOffsetY.ToString(ic);

                    // Write trigger deadzone settings.
                    ps.LeftTriggerDeadZone = padVm.LeftTriggerDeadZone.ToString(ic);
                    ps.RightTriggerDeadZone = padVm.RightTriggerDeadZone.ToString(ic);
                    ps.LeftTriggerAntiDeadZone = padVm.LeftTriggerAntiDeadZone.ToString(ic);
                    ps.RightTriggerAntiDeadZone = padVm.RightTriggerAntiDeadZone.ToString(ic);
                    ps.LeftTriggerMaxRange = padVm.LeftTriggerMaxRange.ToString(ic);
                    ps.RightTriggerMaxRange = padVm.RightTriggerMaxRange.ToString(ic);

                    // Write vJoy custom stick/trigger settings for indices 2+ to dictionary.
                    foreach (var stick in padVm.StickConfigs)
                    {
                        if (stick.Index < 2) continue;
                        int g = stick.Index;
                        ps.SetVJoyMapping($"VJoyStick{g}DzShape", ((int)stick.DeadZoneShape).ToString());
                        ps.SetVJoyMapping($"VJoyStick{g}DzX", stick.DeadZoneX.ToString(ic));
                        ps.SetVJoyMapping($"VJoyStick{g}DzY", stick.DeadZoneY.ToString(ic));
                        ps.SetVJoyMapping($"VJoyStick{g}AdzX", stick.AntiDeadZoneX.ToString(ic));
                        ps.SetVJoyMapping($"VJoyStick{g}AdzY", stick.AntiDeadZoneY.ToString(ic));
                        ps.SetVJoyMapping($"VJoyStick{g}Linear", stick.Linear.ToString(ic));
                        ps.SetVJoyMapping($"VJoyStick{g}CurveX", stick.SensitivityCurveX);
                        ps.SetVJoyMapping($"VJoyStick{g}CurveY", stick.SensitivityCurveY);
                        ps.SetVJoyMapping($"VJoyStick{g}CofX", stick.CenterOffsetX.ToString(ic));
                        ps.SetVJoyMapping($"VJoyStick{g}CofY", stick.CenterOffsetY.ToString(ic));
                        ps.SetVJoyMapping($"VJoyStick{g}MrX", stick.MaxRangeX.ToString(ic));
                        ps.SetVJoyMapping($"VJoyStick{g}MrY", stick.MaxRangeY.ToString(ic));
                        ps.SetVJoyMapping($"VJoyStick{g}MrXN", stick.MaxRangeXNeg.ToString(ic));
                        ps.SetVJoyMapping($"VJoyStick{g}MrYN", stick.MaxRangeYNeg.ToString(ic));
                    }
                    foreach (var trig in padVm.TriggerConfigs)
                    {
                        if (trig.Index < 2) continue;
                        int g = trig.Index;
                        ps.SetVJoyMapping($"VJoyTrigger{g}Dz", trig.DeadZone.ToString(ic));
                        ps.SetVJoyMapping($"VJoyTrigger{g}Adz", trig.AntiDeadZone.ToString(ic));
                        ps.SetVJoyMapping($"VJoyTrigger{g}Mr", trig.MaxRange.ToString(ic));
                        ps.SetVJoyMapping($"VJoyTrigger{g}Curve", trig.SensitivityCurve);
                    }

                    // Write mapping descriptors and per-mapping deadzones.
                    foreach (var mapping in padVm.Mappings)
                    {
                        SetPadSettingProperty(ps, mapping.TargetSettingName, mapping.SourceDescriptor);
                        if (mapping.NegSettingName != null)
                            SetPadSettingProperty(ps, mapping.NegSettingName, mapping.NegSourceDescriptor);

                        if (mapping.MappingDeadZone > 0)
                            ps.SetMappingDeadZone(mapping.TargetSettingName, mapping.MappingDeadZone.ToString());
                        else
                            ps.SetMappingDeadZone(mapping.TargetSettingName, "");
                    }
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Reset
        // ─────────────────────────────────────────────

        /// <summary>
        /// Resets all settings to defaults. Clears all mappings and device records.
        /// </summary>
        public void ResetToDefaults()
        {
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                SettingsManager.UserDevices.Items.Clear();
            }

            lock (SettingsManager.UserSettings.SyncRoot)
            {
                SettingsManager.UserSettings.Items.Clear();
            }

            // Reset ViewModels.
            foreach (var padVm in _mainVm.Pads)
            {
                foreach (var mapping in padVm.Mappings)
                    mapping.SourceDescriptor = string.Empty;

                padVm.ForceOverallGain = 100;
                padVm.LeftMotorStrength = 100;
                padVm.RightMotorStrength = 100;
                padVm.SwapMotors = false;
                padVm.AudioRumbleEnabled = false;
                padVm.AudioRumbleSensitivity = 4.0;
                padVm.AudioRumbleCutoffHz = 80.0;
                padVm.AudioRumbleLeftMotor = 100;
                padVm.AudioRumbleRightMotor = 100;
                padVm.LeftDeadZoneX = 0;
                padVm.LeftDeadZoneY = 0;
                padVm.RightDeadZoneX = 0;
                padVm.RightDeadZoneY = 0;
                padVm.LeftAntiDeadZoneX = 0;
                padVm.LeftAntiDeadZoneY = 0;
                padVm.RightAntiDeadZoneX = 0;
                padVm.RightAntiDeadZoneY = 0;
                padVm.LeftLinear = 0;
                padVm.RightLinear = 0;
                padVm.LeftMaxRangeX = 100;
                padVm.LeftMaxRangeY = 100;
                padVm.RightMaxRangeX = 100;
                padVm.RightMaxRangeY = 100;
                padVm.LeftMaxRangeXNeg = 100;
                padVm.LeftMaxRangeYNeg = 100;
                padVm.RightMaxRangeXNeg = 100;
                padVm.RightMaxRangeYNeg = 100;
                padVm.LeftCenterOffsetX = 0;
                padVm.LeftCenterOffsetY = 0;
                padVm.RightCenterOffsetX = 0;
                padVm.RightCenterOffsetY = 0;
                padVm.LeftTriggerDeadZone = 0;
                padVm.RightTriggerDeadZone = 0;
                padVm.LeftTriggerAntiDeadZone = 0;
                padVm.RightTriggerAntiDeadZone = 0;
                padVm.LeftTriggerMaxRange = 100;
                padVm.RightTriggerMaxRange = 100;

                padVm.SyncAllConfigItemsFromVm();
            }

            var settingsVm = _mainVm.Settings;
            settingsVm.AutoStartEngine = true;
            settingsVm.MinimizeToTray = false;
            settingsVm.StartMinimized = false;
            settingsVm.StartAtLogin = false;
            settingsVm.EnablePollingOnFocusLoss = true;
            settingsVm.PollingRateMs = 1;
            settingsVm.SelectedThemeIndex = 0;
            settingsVm.EnableInputHiding = true;
            settingsVm.EnableAutoProfileSwitching = false;
            _mainVm.Dashboard.EnableDsuMotionServer = false;
            _mainVm.Dashboard.DsuMotionServerPort = 26760;
            _mainVm.Dashboard.EnableWebController = false;
            _mainVm.Dashboard.WebControllerPort = 8080;
            SettingsManager.EnableAutoProfileSwitching = false;
            SettingsManager.ActiveProfileId = null;
            SettingsManager.Profiles.Clear();
            settingsVm.ProfileItems.Clear();
            settingsVm.ProfileItems.Add(new ViewModels.ProfileListItem
            {
                Id = ViewModels.ProfileListItem.DefaultProfileId,
                Name = Strings.Instance.Profile_Default,
            });
            settingsVm.ActiveProfileInfo = Strings.Instance.Profile_Default;

            IsDirty = true;
            settingsVm.HasUnsavedChanges = true;
            _mainVm.StatusText = Strings.Instance.Status_SettingsResetDefaults;
        }

        // ─────────────────────────────────────────────
        //  Reload
        // ─────────────────────────────────────────────

        /// <summary>
        /// Reloads settings from disk, discarding any unsaved changes.
        /// </summary>
        public void Reload()
        {
            if (File.Exists(_settingsFilePath))
            {
                LoadFromFile(_settingsFilePath);
                _mainVm.StatusText = Strings.Instance.Status_SettingsReloaded;
            }
            else
            {
                _mainVm.StatusText = Strings.Instance.Status_NoSettingsFile;
            }

            IsDirty = false;
            _mainVm.Settings.HasUnsavedChanges = false;
        }

        /// <summary>
        /// Marks settings as dirty (unsaved changes) and schedules an autosave
        /// after a 2-second debounce period.
        /// </summary>
        public void MarkDirty()
        {
            IsDirty = true;
            _mainVm.Settings.HasUnsavedChanges = true;

            // Start or restart the autosave debounce timer.
            if (_autoSaveTimer == null)
            {
                _autoSaveTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _autoSaveTimer.Tick += (s, e) =>
                {
                    _autoSaveTimer.Stop();
                    if (IsDirty)
                    {
                        Save();
                        AutoSaved?.Invoke(this, EventArgs.Empty);
                    }
                };
            }

            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }

        // ─────────────────────────────────────────────
        //  PadSetting reflection helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Gets a string property value from a PadSetting by property name.
        /// For keys starting with "VJoy", uses the dictionary-based VJoy mapping system.
        /// </summary>
        private static string GetPadSettingProperty(PadSetting ps, string propertyName)
        {
            if (ps == null || string.IsNullOrEmpty(propertyName))
                return string.Empty;

            if (propertyName.StartsWith("VJoy", StringComparison.Ordinal))
                return ps.GetVJoyMapping(propertyName);
            if (propertyName.StartsWith("Midi", StringComparison.Ordinal))
                return ps.GetMidiMapping(propertyName);
            if (propertyName.StartsWith("Kbm", StringComparison.Ordinal))
                return ps.GetKbmMapping(propertyName);

            var prop = typeof(PadSetting).GetProperty(propertyName);
            if (prop == null || prop.PropertyType != typeof(string))
                return string.Empty;

            return prop.GetValue(ps) as string ?? string.Empty;
        }

        /// <summary>
        /// Sets a string property value on a PadSetting by property name.
        /// For keys starting with "VJoy", uses the dictionary-based VJoy mapping system.
        /// </summary>
        private static void SetPadSettingProperty(PadSetting ps, string propertyName, string value)
        {
            if (ps == null || string.IsNullOrEmpty(propertyName))
                return;

            // vJoy custom mappings use dictionary-based storage
            if (propertyName.StartsWith("VJoy", StringComparison.Ordinal))
            {
                ps.SetVJoyMapping(propertyName, value ?? string.Empty);
                return;
            }

            // MIDI mappings use dictionary-based storage
            if (propertyName.StartsWith("Midi", StringComparison.Ordinal))
            {
                ps.SetMidiMapping(propertyName, value ?? string.Empty);
                return;
            }

            // KBM mappings use dictionary-based storage
            if (propertyName.StartsWith("Kbm", StringComparison.Ordinal))
            {
                ps.SetKbmMapping(propertyName, value ?? string.Empty);
                return;
            }

            var prop = typeof(PadSetting).GetProperty(propertyName);
            if (prop == null || prop.PropertyType != typeof(string) || !prop.CanWrite)
                return;

            prop.SetValue(ps, value ?? string.Empty);
        }

        // ─────────────────────────────────────────────
        //  Parse helper
        // ─────────────────────────────────────────────

        private static int TryParseInt(string value, int defaultValue)
        {
            if (string.IsNullOrEmpty(value))
                return defaultValue;
            return int.TryParse(value, out int result) ? result : defaultValue;
        }

        private static double TryParseDouble(string value, double defaultValue)
        {
            if (string.IsNullOrEmpty(value))
                return defaultValue;
            return double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double result) ? result : defaultValue;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  Serialization data classes
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Root element for the PadForge settings XML file.
    /// </summary>
    [XmlRoot("PadForgeSettings")]
    public class SettingsFileData
    {
        [XmlArray("Devices")]
        [XmlArrayItem("Device")]
        public UserDevice[] Devices { get; set; }

        [XmlArray("UserSettings")]
        [XmlArrayItem("Setting")]
        public UserSetting[] Settings { get; set; }

        [XmlArray("PadSettings")]
        [XmlArrayItem("PadSetting")]
        public PadSetting[] PadSettings { get; set; }

        [XmlElement("AppSettings")]
        public AppSettingsData AppSettings { get; set; }

        [XmlArray("Macros")]
        [XmlArrayItem("Macro")]
        public MacroData[] Macros { get; set; }

        [XmlArray("Profiles")]
        [XmlArrayItem("Profile")]
        public ProfileData[] Profiles { get; set; }
    }

    /// <summary>
    /// Application-level settings stored in the XML file.
    /// </summary>
    public class AppSettingsData
    {
        [XmlElement]
        public bool AutoStartEngine { get; set; } = true;

        [XmlElement]
        public bool MinimizeToTray { get; set; }

        [XmlElement]
        public bool StartMinimized { get; set; }

        [XmlElement]
        public bool StartAtLogin { get; set; }

        [XmlElement]
        public bool EnablePollingOnFocusLoss { get; set; } = true;

        [XmlElement]
        public int PollingRateMs { get; set; } = 1;

        [XmlElement]
        public int ThemeIndex { get; set; }

        [XmlElement]
        public string Language { get; set; } = "";

        [XmlElement]
        public bool EnableAutoProfileSwitching { get; set; }

        [XmlElement]
        public string ActiveProfileId { get; set; }

        /// <summary>
        /// Per-slot virtual controller output types.
        /// Array of ints matching VirtualControllerType enum values.
        /// </summary>
        [XmlArray("SlotControllerTypes")]
        [XmlArrayItem("Type")]
        public int[] SlotControllerTypes { get; set; }

        /// <summary>
        /// Which virtual controller slots have been explicitly created.
        /// Null on old settings files — auto-populated from existing assignments.
        /// </summary>
        [XmlArray("SlotCreated")]
        [XmlArrayItem("Created")]
        public bool[] SlotCreated { get; set; }

        /// <summary>
        /// Which virtual controller slots are enabled for ViGEm output.
        /// Null on old settings files — defaults to all true.
        /// </summary>
        [XmlArray("SlotEnabled")]
        [XmlArrayItem("Enabled")]
        public bool[] SlotEnabled { get; set; }

        [XmlElement]
        public bool EnableDsuMotionServer { get; set; }

        [XmlElement]
        public int DsuMotionServerPort { get; set; } = 26760;

        [XmlElement]
        public bool EnableWebController { get; set; }

        [XmlElement]
        public int WebControllerPort { get; set; } = 8080;

        [XmlElement]
        public bool EnableTouchpadOverlay { get; set; }

        [XmlElement]
        public double TouchpadOverlayOpacity { get; set; } = 0.25;

        [XmlElement]
        public int TouchpadOverlayMonitor { get; set; }

        [XmlElement]
        public double TouchpadOverlayLeft { get; set; } = -1;

        [XmlElement]
        public double TouchpadOverlayTop { get; set; } = -1;

        [XmlElement]
        public double TouchpadOverlayWidth { get; set; } = 500;

        [XmlElement]
        public double TouchpadOverlayHeight { get; set; } = 250;

        [XmlElement]
        public double MainWindowLeft { get; set; } = -1;

        [XmlElement]
        public double MainWindowTop { get; set; } = -1;

        [XmlElement]
        public double MainWindowWidth { get; set; } = 1100;

        [XmlElement]
        public double MainWindowHeight { get; set; } = 720;

        [XmlElement]
        public int MainWindowState { get; set; } // 0=Normal, 2=Maximized

        [XmlElement]
        public bool MainWindowFullScreen { get; set; }

        [XmlElement]
        public bool Use2DControllerView { get; set; }

        /// <summary>
        /// Global master switch for device hiding (HidHide + input hooks).
        /// When false, no HidHide blacklisting or hook suppression occurs
        /// regardless of per-device toggles.
        /// </summary>
        [XmlElement]
        public bool EnableInputHiding { get; set; } = true;

        /// <summary>
        /// User-specified application paths to whitelist in HidHide.
        /// These are regular Windows paths (e.g. C:\Games\emulator.exe),
        /// converted to DOS device paths at runtime.
        /// </summary>
        [XmlArray("HidHideWhitelistPaths")]
        [XmlArrayItem("Path")]
        public string[] HidHideWhitelistPaths { get; set; }

        /// <summary>
        /// Per-slot vJoy configuration (preset, axis/button counts).
        /// Null on old settings files — uses Xbox360 preset defaults.
        /// </summary>
        [XmlArray("VJoyConfigs")]
        [XmlArrayItem("Config")]
        public ViewModels.VJoySlotConfigData[] VJoyConfigs { get; set; }

        /// <summary>
        /// Per-slot MIDI configuration (port, channel, CC/note mappings).
        /// Null on old settings files — uses defaults.
        /// </summary>
        [XmlArray("MidiConfigs")]
        [XmlArrayItem("Config")]
        public ViewModels.MidiSlotConfigData[] MidiConfigs { get; set; }

        /// <summary>
        /// Full snapshot of the default profile's state, saved when a named
        /// profile is active so the default can be restored on restart.
        /// Null when the default profile is active (its state is in the
        /// global UserSettings/SlotCreated/etc. fields).
        /// </summary>
        [XmlElement("DefaultProfileSnapshot")]
        public ProfileData DefaultProfileSnapshot { get; set; }

        /// <summary>
        /// Global macros for profile shortcuts and other app-wide actions.
        /// Null on old settings files — no shortcuts configured.
        /// </summary>
        [XmlArray("GlobalMacros")]
        [XmlArrayItem("GlobalMacro")]
        public GlobalMacroData[] GlobalMacros { get; set; }

    }

    /// <summary>
    /// Serializable DTO for a macro. Stored per pad slot.
    /// </summary>
    public class MacroData
    {
        [XmlAttribute]
        public int PadIndex { get; set; }

        [XmlElement]
        public string Name { get; set; } = "New Macro";

        [XmlElement]
        public bool IsEnabled { get; set; } = true;

        [XmlElement]
        public ushort TriggerButtons { get; set; }

        /// <summary>
        /// GUID of the device whose raw buttons are the trigger source (string form).
        /// Null/empty = use legacy Xbox bitmask path.
        /// </summary>
        [XmlElement]
        public string TriggerDeviceGuid { get; set; }

        /// <summary>
        /// Comma-separated raw button indices, e.g. "13,14".
        /// Null/empty = not using raw trigger path.
        /// </summary>
        [XmlElement]
        public string TriggerRawButtons { get; set; }

        [XmlElement]
        public MacroTriggerSource TriggerSource { get; set; }

        [XmlElement]
        public MacroTriggerMode TriggerMode { get; set; }

        [XmlElement]
        public bool ConsumeTriggerButtons { get; set; } = true;

        [XmlElement]
        public MacroRepeatMode RepeatMode { get; set; }

        [XmlElement]
        public int RepeatCount { get; set; } = 1;

        [XmlElement]
        public int RepeatDelayMs { get; set; } = 100;

        /// <summary>Hex-encoded custom vJoy trigger button words (e.g. "00000003,00000000,00000000,00000000").</summary>
        [XmlElement]
        public string TriggerCustomButtons { get; set; }

        /// <summary>Comma-separated axis targets (e.g. "LeftStickX,LeftTrigger").</summary>
        [XmlElement]
        public string TriggerAxisTargets { get; set; }

        /// <summary>Axis trigger threshold percentage (1-100).</summary>
        [XmlElement]
        public int TriggerAxisThreshold { get; set; } = 50;

        /// <summary>POV triggers stored as "povIndex:centidegrees" entries.</summary>
        [XmlArray("TriggerPovs")]
        [XmlArrayItem("Pov")]
        public string[] TriggerPovs { get; set; }

        [XmlArray("Actions")]
        [XmlArrayItem("Action")]
        public ActionData[] Actions { get; set; }
    }

    /// <summary>
    /// Serializable DTO for a single macro action.
    /// </summary>
    public class ActionData
    {
        [XmlElement]
        public MacroActionType Type { get; set; }

        [XmlElement]
        public ushort ButtonFlags { get; set; }

        /// <summary>Hex-encoded custom vJoy button words for this action.</summary>
        [XmlElement]
        public string CustomButtons { get; set; }

        [XmlElement]
        public int KeyCode { get; set; }

        /// <summary>
        /// Multi-key combo in "{Key1}{Key2}..." format. Takes precedence over KeyCode.
        /// </summary>
        [XmlElement]
        public string KeyString { get; set; }

        [XmlElement]
        public int DurationMs { get; set; } = 50;

        [XmlElement]
        public short AxisValue { get; set; }

        [XmlElement]
        public MacroAxisTarget AxisTarget { get; set; }

        [XmlElement]
        public MacroAxisSource AxisSource { get; set; }

        /// <summary>GUID of the source device for InputDevice axis source (string form).</summary>
        [XmlElement]
        public string SourceDeviceGuid { get; set; }

        [XmlElement]
        public int SourceDeviceAxisIndex { get; set; }

        /// <summary>Process name for AppVolume action (e.g., "firefox", "spotify").</summary>
        [XmlElement]
        public string ProcessName { get; set; }

        /// <summary>Maximum volume percentage (1-100) for SystemVolume/AppVolume. Default 100 (no limit).</summary>
        [XmlElement]
        public int VolumeLimit { get; set; } = 100;

        /// <summary>Pixels/scroll units per frame at full deflection for MouseMove/MouseScroll.</summary>
        [XmlElement]
        public float MouseSensitivity { get; set; } = 10f;

        /// <summary>Which mouse button for MouseButtonPress/MouseButtonRelease.</summary>
        [XmlElement]
        public MacroMouseButton MouseButton { get; set; }

        /// <summary>When true, invert the axis value (0→1 becomes 1→0).</summary>
        [XmlElement]
        public bool InvertAxis { get; set; }

        /// <summary>When true, show the Windows volume flyout OSD on volume changes.</summary>
        [XmlElement]
        public bool ShowVolumeOsd { get; set; } = true;
    }

    /// <summary>
    /// A named profile that stores per-device PadSettings and macros.
    /// When auto-switching is enabled, profiles activate when a matching
    /// executable's window comes to the foreground.
    /// </summary>
    public class ProfileData
    {
        [XmlAttribute]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        [XmlElement]
        public string Name { get; set; } = "New Profile";

        /// <summary>
        /// Pipe-separated full executable paths (e.g. "C:\Games\game.exe|D:\Other\game2.exe").
        /// Case-insensitive matching against the foreground window's process path.
        /// </summary>
        [XmlElement]
        public string ExecutableNames { get; set; } = string.Empty;

        [XmlArray("Entries")]
        [XmlArrayItem("Entry")]
        public ProfileEntry[] Entries { get; set; }

        [XmlArray("ProfilePadSettings")]
        [XmlArrayItem("PadSetting")]
        public PadSetting[] PadSettings { get; set; }

        [XmlArray("ProfileMacros")]
        [XmlArrayItem("Macro")]
        public MacroData[] Macros { get; set; }

        /// <summary>
        /// Which virtual controller slots were created when this profile was saved.
        /// Null on old profiles — topology application is skipped.
        /// </summary>
        [XmlArray("ProfileSlotCreated")]
        [XmlArrayItem("Created")]
        public bool[] SlotCreated { get; set; }

        /// <summary>
        /// Which virtual controller slots were enabled when this profile was saved.
        /// Null on old profiles — topology application is skipped.
        /// </summary>
        [XmlArray("ProfileSlotEnabled")]
        [XmlArrayItem("Enabled")]
        public bool[] SlotEnabled { get; set; }

        /// <summary>
        /// Per-slot virtual controller output types (VirtualControllerType enum cast to int).
        /// Null on old profiles — topology application is skipped.
        /// </summary>
        [XmlArray("ProfileSlotControllerTypes")]
        [XmlArrayItem("Type")]
        public int[] SlotControllerTypes { get; set; }

        /// <summary>Per-slot vJoy configurations saved with this profile.</summary>
        [XmlArray("ProfileVJoyConfigs")]
        [XmlArrayItem("VJoyConfig")]
        public ViewModels.VJoySlotConfigData[] VJoyConfigs { get; set; }

        /// <summary>Per-slot MIDI configurations saved with this profile.</summary>
        [XmlArray("ProfileMidiConfigs")]
        [XmlArrayItem("MidiConfig")]
        public ViewModels.MidiSlotConfigData[] MidiConfigs { get; set; }

        /// <summary>Whether the DSU motion server was enabled in this profile.</summary>
        [XmlElement]
        public bool EnableDsuMotionServer { get; set; }

        /// <summary>DSU motion server port for this profile.</summary>
        [XmlElement]
        public int DsuMotionServerPort { get; set; } = 26760;

        /// <summary>Whether the web controller server was enabled in this profile.</summary>
        [XmlElement]
        public bool EnableWebController { get; set; }

        /// <summary>Web controller server port for this profile.</summary>
        [XmlElement]
        public int WebControllerPort { get; set; } = 8080;

        [XmlElement]
        public bool EnableTouchpadOverlay { get; set; }

        [XmlElement]
        public double TouchpadOverlayOpacity { get; set; } = 0.25;

        [XmlElement]
        public int TouchpadOverlayMonitor { get; set; }

        [XmlElement]
        public double TouchpadOverlayLeft { get; set; } = -1;

        [XmlElement]
        public double TouchpadOverlayTop { get; set; } = -1;

        [XmlElement]
        public double TouchpadOverlayWidth { get; set; } = 500;

        [XmlElement]
        public double TouchpadOverlayHeight { get; set; } = 250;
    }

    /// <summary>
    /// Links a device (by instance GUID) to a slot and PadSetting within a profile.
    /// ProductGuid enables fallback matching when InstanceGuid changes (BT reconnect).
    /// </summary>
    public class ProfileEntry
    {
        [XmlElement]
        public Guid InstanceGuid { get; set; }

        [XmlElement]
        public Guid ProductGuid { get; set; }

        [XmlElement]
        public int MapTo { get; set; }

        [XmlElement]
        public string PadSettingChecksum { get; set; }
    }

    /// <summary>
    /// A global macro that runs regardless of which profile is active.
    /// Currently used for profile shortcuts; future uses include overlay
    /// toggles, global volume, etc.
    /// </summary>
    public class GlobalMacroData
    {
        [XmlAttribute]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        [XmlElement]
        public SwitchProfileMode SwitchMode { get; set; }

        /// <summary>For Specific mode: target profile ID. Null for Next/Previous.</summary>
        [XmlElement]
        public string TargetProfileId { get; set; }

        /// <summary>
        /// Per-button trigger entries. Each entry has a button index, the device instance
        /// GUID it was recorded from, and the product GUID for same-type matching.
        /// Supports cross-device combos (e.g., Shift on keyboard + Start on gamepad).
        /// </summary>
        [XmlArray("TriggerEntries")]
        [XmlArrayItem("Entry")]
        public TriggerButtonEntry[] TriggerEntries { get; set; }

        /// <summary>Legacy: flat button index array from old XML. Migrated on load.</summary>
        [XmlArray("TriggerButtons")]
        [XmlArrayItem("Index")]
        public int[] LegacyTriggerRawButtons { get; set; }

        /// <summary>Legacy: single device GUID from old XML.</summary>
        [XmlElement]
        public Guid TriggerDeviceGuid { get; set; }

        /// <summary>Runtime-only: previous frame trigger state for edge detection.</summary>
        [XmlIgnore]
        public bool WasTriggerActive { get; set; }

        /// <summary>True if this macro has any trigger buttons configured.</summary>
        [XmlIgnore]
        public bool HasTrigger => TriggerEntries != null && TriggerEntries.Length > 0;

        /// <summary>
        /// Migrates legacy flat button array to per-button entries.
        /// Called once during settings load.
        /// </summary>
        public void MigrateLegacyTrigger()
        {
            if (TriggerEntries != null || LegacyTriggerRawButtons == null)
                return;

            TriggerEntries = new TriggerButtonEntry[LegacyTriggerRawButtons.Length];
            for (int i = 0; i < LegacyTriggerRawButtons.Length; i++)
            {
                TriggerEntries[i] = new TriggerButtonEntry
                {
                    ButtonIndex = LegacyTriggerRawButtons[i],
                    DeviceInstanceGuid = TriggerDeviceGuid,
                    DeviceProductGuid = System.Guid.Empty
                };
            }
            LegacyTriggerRawButtons = null; // Clear so next save uses new format.
        }
    }

    /// <summary>
    /// A single button in a global macro trigger combo. Each button tracks
    /// which device it was recorded from, enabling cross-device combos.
    /// </summary>
    public class TriggerButtonEntry
    {
        /// <summary>Raw button index on the source device (when IsAxis=false).</summary>
        [XmlElement]
        public int ButtonIndex { get; set; }

        /// <summary>True if this entry represents an axis threshold, not a button.</summary>
        [XmlElement]
        public bool IsAxis { get; set; }

        /// <summary>Raw axis index on the source device (when IsAxis=true).</summary>
        [XmlElement]
        public int AxisIndex { get; set; }

        /// <summary>
        /// Axis threshold as normalized value (0.0–1.0). The axis must exceed this
        /// value to be considered active. Default 0.5 (50%).
        /// </summary>
        [XmlElement]
        public float AxisThreshold { get; set; } = 0.5f;

        /// <summary>
        /// Axis direction: Positive (axis > threshold) or Negative (axis &lt; 1-threshold).
        /// </summary>
        [XmlElement]
        public AxisTriggerDirection AxisDirection { get; set; }

        /// <summary>Instance GUID of the device this entry was recorded from.</summary>
        [XmlElement]
        public Guid DeviceInstanceGuid { get; set; }

        /// <summary>Product GUID for same-type device matching in "Any Device" mode.</summary>
        [XmlElement]
        public Guid DeviceProductGuid { get; set; }
    }

    public enum AxisTriggerDirection
    {
        Positive, // Axis value above threshold (e.g., stick right, trigger pulled)
        Negative  // Axis value below 1-threshold (e.g., stick left)
    }

    public enum SwitchProfileMode
    {
        Specific,
        Next,
        Previous,
        ToggleWindow
    }
}
