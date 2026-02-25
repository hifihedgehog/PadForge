using System;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using System.Xml.Serialization;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine.Data;
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
                                    var ps = new PadSetting();
                                    ps.CopyFrom(template);
                                    ps.PadSettingChecksum = template.PadSettingChecksum;
                                    ps.GameFileName = template.GameFileName;
                                    us.SetPadSetting(ps);
                                }
                            }

                            SettingsManager.UserSettings.Items.Add(us);
                        }
                    }
                }

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
                _mainVm.StatusText = $"Error loading settings: {ex.Message}";
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
            vm.EnableAutoProfileSwitching = appSettings.EnableAutoProfileSwitching;
            SettingsManager.EnableAutoProfileSwitching = appSettings.EnableAutoProfileSwitching;
            SettingsManager.ActiveProfileId = appSettings.ActiveProfileId;
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

                // Load dead zone settings (independent X/Y).
                padVm.LeftDeadZoneX = TryParseInt(ps.LeftThumbDeadZoneX, 0);
                padVm.LeftDeadZoneY = TryParseInt(ps.LeftThumbDeadZoneY, 0);
                padVm.RightDeadZoneX = TryParseInt(ps.RightThumbDeadZoneX, 0);
                padVm.RightDeadZoneY = TryParseInt(ps.RightThumbDeadZoneY, 0);
                ps.MigrateAntiDeadZones();
                padVm.LeftAntiDeadZoneX = TryParseInt(ps.LeftThumbAntiDeadZoneX, 0);
                padVm.LeftAntiDeadZoneY = TryParseInt(ps.LeftThumbAntiDeadZoneY, 0);
                padVm.RightAntiDeadZoneX = TryParseInt(ps.RightThumbAntiDeadZoneX, 0);
                padVm.RightAntiDeadZoneY = TryParseInt(ps.RightThumbAntiDeadZoneY, 0);
                padVm.LeftLinear = TryParseInt(ps.LeftThumbLinear, 0);
                padVm.RightLinear = TryParseInt(ps.RightThumbLinear, 0);

                // Load trigger dead zone settings.
                padVm.LeftTriggerDeadZone = TryParseInt(ps.LeftTriggerDeadZone, 0);
                padVm.RightTriggerDeadZone = TryParseInt(ps.RightTriggerDeadZone, 0);
                padVm.LeftTriggerAntiDeadZone = TryParseInt(ps.LeftTriggerAntiDeadZone, 0);
                padVm.RightTriggerAntiDeadZone = TryParseInt(ps.RightTriggerAntiDeadZone, 0);

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
                    TriggerDeviceGuid = Guid.TryParse(md.TriggerDeviceGuid, out var parsedGuid)
                        ? parsedGuid : Guid.Empty,
                    TriggerRawButtons = ParseRawButtonIndices(md.TriggerRawButtons),
                    TriggerSource = md.TriggerSource,
                    TriggerMode = md.TriggerMode,
                    ConsumeTriggerButtons = md.ConsumeTriggerButtons,
                    RepeatMode = md.RepeatMode,
                    RepeatCount = md.RepeatCount,
                    RepeatDelayMs = md.RepeatDelayMs
                };

                if (md.Actions != null)
                {
                    foreach (var ad in md.Actions)
                    {
                        macro.Actions.Add(new MacroAction
                        {
                            Type = ad.Type,
                            ButtonFlags = ad.ButtonFlags,
                            KeyCode = ad.KeyCode,
                            // Migrate legacy single KeyCode to KeyString format.
                            KeyString = !string.IsNullOrEmpty(ad.KeyString)
                                ? ad.KeyString
                                : (ad.KeyCode != 0 ? $"{{{(VirtualKey)ad.KeyCode}}}" : ""),
                            DurationMs = ad.DurationMs,
                            AxisValue = ad.AxisValue,
                            AxisTarget = ad.AxisTarget
                        });
                    }
                }

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

            if (profiles != null)
            {
                foreach (var p in profiles)
                {
                    SettingsManager.Profiles.Add(p);
                    _mainVm.Settings.ProfileItems.Add(new ViewModels.ProfileListItem
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Executables = FormatExePaths(p.ExecutableNames)
                    });
                }
            }

            // Update active profile display.
            string activeId = appSettings?.ActiveProfileId;
            var active = SettingsManager.Profiles.Find(p => p.Id == activeId);
            _mainVm.Settings.ActiveProfileInfo = active?.Name ?? "Default";
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
                        MapTo = us.MapTo,
                        PadSettingChecksum = ps.PadSettingChecksum
                    });

                    if (seen.Add(ps.PadSettingChecksum))
                        padSettings.Add(ps.CloneDeep());
                }
            }

            profile.Entries = entries.ToArray();
            profile.PadSettings = padSettings.ToArray();
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

                // Recompute checksums for ALL PadSettings and sync to UserSettings.
                // This ensures each PadSetting's checksum reflects its actual content,
                // preventing checksum collisions that cause settings to be swapped on reload.
                lock (SettingsManager.UserSettings.SyncRoot)
                {
                    foreach (var us in SettingsManager.UserSettings.Items)
                    {
                        var ps = us.GetPadSetting();
                        if (ps != null)
                        {
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
                _mainVm.StatusText = $"Settings saved to {Path.GetFileName(filePath)}.";
            }
            catch (Exception ex)
            {
                _mainVm.StatusText = $"Error saving settings: {ex.Message}";
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
            return new AppSettingsData
            {
                AutoStartEngine = vm.AutoStartEngine,
                MinimizeToTray = vm.MinimizeToTray,
                StartMinimized = vm.StartMinimized,
                StartAtLogin = vm.StartAtLogin,
                EnablePollingOnFocusLoss = vm.EnablePollingOnFocusLoss,
                PollingRateMs = vm.PollingRateMs,
                ThemeIndex = vm.SelectedThemeIndex,
                EnableAutoProfileSwitching = vm.EnableAutoProfileSwitching,
                ActiveProfileId = SettingsManager.ActiveProfileId
            };
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
                        Actions = macro.Actions.Select(a => new ActionData
                        {
                            Type = a.Type,
                            ButtonFlags = a.ButtonFlags,
                            KeyCode = a.ParsedKeyCodes.Length > 0 ? a.ParsedKeyCodes[0] : a.KeyCode,
                            KeyString = a.KeyString,
                            DurationMs = a.DurationMs,
                            AxisValue = a.AxisValue,
                            AxisTarget = a.AxisTarget
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

                    var us = SettingsManager.FindSettingByInstanceGuid(selected.InstanceGuid);
                    if (us == null) continue;

                    var ps = us.GetPadSetting();
                    if (ps == null) continue;

                    // Write force feedback settings.
                    ps.ForceOverall = padVm.ForceOverallGain.ToString();
                    ps.LeftMotorStrength = padVm.LeftMotorStrength.ToString();
                    ps.RightMotorStrength = padVm.RightMotorStrength.ToString();
                    ps.ForceSwapMotor = padVm.SwapMotors ? "1" : "0";

                    // Write dead zone settings (independent X/Y).
                    ps.LeftThumbDeadZoneX = padVm.LeftDeadZoneX.ToString();
                    ps.LeftThumbDeadZoneY = padVm.LeftDeadZoneY.ToString();
                    ps.RightThumbDeadZoneX = padVm.RightDeadZoneX.ToString();
                    ps.RightThumbDeadZoneY = padVm.RightDeadZoneY.ToString();
                    ps.LeftThumbAntiDeadZoneX = padVm.LeftAntiDeadZoneX.ToString();
                    ps.LeftThumbAntiDeadZoneY = padVm.LeftAntiDeadZoneY.ToString();
                    ps.RightThumbAntiDeadZoneX = padVm.RightAntiDeadZoneX.ToString();
                    ps.RightThumbAntiDeadZoneY = padVm.RightAntiDeadZoneY.ToString();
                    ps.LeftThumbLinear = padVm.LeftLinear.ToString();
                    ps.RightThumbLinear = padVm.RightLinear.ToString();

                    // Write trigger dead zone settings.
                    ps.LeftTriggerDeadZone = padVm.LeftTriggerDeadZone.ToString();
                    ps.RightTriggerDeadZone = padVm.RightTriggerDeadZone.ToString();
                    ps.LeftTriggerAntiDeadZone = padVm.LeftTriggerAntiDeadZone.ToString();
                    ps.RightTriggerAntiDeadZone = padVm.RightTriggerAntiDeadZone.ToString();

                    // Write mapping descriptors.
                    foreach (var mapping in padVm.Mappings)
                    {
                        SetPadSettingProperty(ps, mapping.TargetSettingName, mapping.SourceDescriptor);
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
                padVm.LeftTriggerDeadZone = 0;
                padVm.RightTriggerDeadZone = 0;
                padVm.LeftTriggerAntiDeadZone = 0;
                padVm.RightTriggerAntiDeadZone = 0;
            }

            var settingsVm = _mainVm.Settings;
            settingsVm.AutoStartEngine = true;
            settingsVm.MinimizeToTray = false;
            settingsVm.StartMinimized = false;
            settingsVm.StartAtLogin = false;
            settingsVm.EnablePollingOnFocusLoss = true;
            settingsVm.PollingRateMs = 1;
            settingsVm.SelectedThemeIndex = 0;
            settingsVm.EnableAutoProfileSwitching = false;
            SettingsManager.EnableAutoProfileSwitching = false;
            SettingsManager.ActiveProfileId = null;
            SettingsManager.Profiles.Clear();
            settingsVm.ProfileItems.Clear();
            settingsVm.ActiveProfileInfo = "Default";

            IsDirty = true;
            settingsVm.HasUnsavedChanges = true;
            _mainVm.StatusText = "Settings reset to defaults.";
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
                _mainVm.StatusText = "Settings reloaded from disk.";
            }
            else
            {
                _mainVm.StatusText = "No settings file found on disk.";
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
        /// </summary>
        private static string GetPadSettingProperty(PadSetting ps, string propertyName)
        {
            if (ps == null || string.IsNullOrEmpty(propertyName))
                return string.Empty;

            var prop = typeof(PadSetting).GetProperty(propertyName);
            if (prop == null || prop.PropertyType != typeof(string))
                return string.Empty;

            return prop.GetValue(ps) as string ?? string.Empty;
        }

        /// <summary>
        /// Sets a string property value on a PadSetting by property name.
        /// </summary>
        private static void SetPadSettingProperty(PadSetting ps, string propertyName, string value)
        {
            if (ps == null || string.IsNullOrEmpty(propertyName))
                return;

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
        public bool EnableAutoProfileSwitching { get; set; }

        [XmlElement]
        public string ActiveProfileId { get; set; }
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
    }

    /// <summary>
    /// Links a device (by instance GUID) to a slot and PadSetting within a profile.
    /// </summary>
    public class ProfileEntry
    {
        [XmlElement]
        public Guid InstanceGuid { get; set; }

        [XmlElement]
        public int MapTo { get; set; }

        [XmlElement]
        public string PadSettingChecksum { get; set; }
    }
}
