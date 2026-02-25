using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>
    /// Bridges the background <see cref="InputManager"/> engine with WPF ViewModels.
    /// 
    /// Responsibilities:
    ///   - Creates and owns the InputManager instance
    ///   - Runs a 30Hz DispatcherTimer on the UI thread
    ///   - Reads combined gamepad states from the engine and pushes them to PadViewModels
    ///   - Syncs the device list to DevicesViewModel
    ///   - Updates dashboard statistics
    ///   - Forwards engine events (DevicesUpdated, FrequencyUpdated) to the UI thread
    /// 
    /// Thread model:
    ///   InputManager runs on a background thread at ~1000Hz.
    ///   This service's timer runs on the WPF dispatcher at ~30Hz.
    ///   All ViewModel property sets happen on the UI thread (safe for data binding).
    /// </summary>
    public class InputService : IDisposable
    {
        // ─────────────────────────────────────────────
        //  Constants
        // ─────────────────────────────────────────────

        /// <summary>UI update interval (~30Hz).</summary>
        private const int UiTimerIntervalMs = 33;

        // ─────────────────────────────────────────────
        //  Fields
        // ─────────────────────────────────────────────

        private readonly MainViewModel _mainVm;
        private readonly Dispatcher _dispatcher;
        private InputManager _inputManager;
        private DispatcherTimer _uiTimer;
        private ForegroundMonitorService _foregroundMonitor;
        private ProfileData _defaultProfileSnapshot;
        private bool _disposed;

        /// <summary>
        /// Whether the Devices page is currently visible.
        /// When true, the UI timer syncs raw device state to DevicesViewModel.
        /// Set by MainWindow when navigation changes.
        /// </summary>
        public bool IsDevicesPageVisible { get; set; }

        /// <summary>
        /// Whether any Pad page is currently visible.
        /// When true, the UI timer updates mapping row live values.
        /// </summary>
        public bool IsPadPageVisible { get; set; }

        // ── Macro trigger recording state ──
        private MacroItem _recordingMacro;
        private int _recordingPadIndex;
        private ushort _recordedButtons;
        private Guid _recordingDeviceGuid;
        private HashSet<int> _recordedRawButtons;

        /// <summary>
        /// Tracks the previously selected device GUID for each pad slot,
        /// so we can save the old device's PadSetting before loading the new one.
        /// </summary>
        private readonly Dictionary<int, Guid> _previousSelectedDevice = new();

        // ─────────────────────────────────────────────
        //  Constructor
        // ─────────────────────────────────────────────

        /// <summary>
        /// Creates a new InputService.
        /// </summary>
        /// <param name="mainVm">The root ViewModel to push state into.</param>
        public InputService(MainViewModel mainVm)
        {
            _mainVm = mainVm ?? throw new ArgumentNullException(nameof(mainVm));
            _dispatcher = Dispatcher.CurrentDispatcher;

            // Subscribe to device selection changes on each pad.
            foreach (var padVm in _mainVm.Pads)
            {
                padVm.SelectedDeviceChanged += OnSelectedDeviceChanged;
            }
        }

        // ─────────────────────────────────────────────
        //  Start / Stop
        // ─────────────────────────────────────────────

        /// <summary>
        /// Creates the InputManager, subscribes to events, starts the engine
        /// and the UI update timer.
        /// </summary>
        public void Start()
        {
            if (_inputManager != null)
                return; // Already running.

            // Create engine with the configured polling interval.
            _inputManager = new InputManager();
            _inputManager.PollingIntervalMs = _mainVm.Settings.PollingRateMs;

            // Subscribe to engine events (raised on background thread).
            _inputManager.DevicesUpdated += OnDevicesUpdated;
            _inputManager.FrequencyUpdated += OnFrequencyUpdated;
            _inputManager.ErrorOccurred += OnErrorOccurred;

            // Subscribe to polling interval changes from the Settings UI.
            _mainVm.Settings.PropertyChanged += OnSettingsPropertyChanged;

            // Create foreground monitor for auto-profile switching.
            _foregroundMonitor = new ForegroundMonitorService();
            _foregroundMonitor.ProfileSwitchRequired += OnProfileSwitchRequired;

            // Capture default profile snapshot before any profile switches.
            _defaultProfileSnapshot = SnapshotCurrentProfile();

            // Start engine background thread.
            _inputManager.Start();

            // Create UI update timer on the dispatcher.
            _uiTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(UiTimerIntervalMs)
            };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();

            // Update main VM state.
            _mainVm.IsEngineRunning = true;
            _mainVm.StatusText = "Engine started.";
            _mainVm.RefreshCommands();
        }

        /// <summary>
        /// Stops the UI timer and engine, releases resources.
        /// </summary>
        public void Stop()
        {
            // Stop UI timer.
            if (_uiTimer != null)
            {
                _uiTimer.Stop();
                _uiTimer.Tick -= UiTimer_Tick;
                _uiTimer = null;
            }

            // Unsubscribe from settings changes.
            _mainVm.Settings.PropertyChanged -= OnSettingsPropertyChanged;

            // Dispose foreground monitor.
            if (_foregroundMonitor != null)
            {
                _foregroundMonitor.ProfileSwitchRequired -= OnProfileSwitchRequired;
                _foregroundMonitor = null;
            }

            // Stop and dispose engine.
            if (_inputManager != null)
            {
                _inputManager.DevicesUpdated -= OnDevicesUpdated;
                _inputManager.FrequencyUpdated -= OnFrequencyUpdated;
                _inputManager.ErrorOccurred -= OnErrorOccurred;
                _inputManager.Dispose();
                _inputManager = null;
            }

            // Update main VM state.
            _mainVm.IsEngineRunning = false;
            _mainVm.PollingFrequency = 0;
            _mainVm.StatusText = "Engine stopped.";
            _mainVm.RefreshCommands();
        }

        /// <summary>
        /// Returns the underlying InputManager (for advanced operations like test rumble).
        /// </summary>
        public InputManager Engine => _inputManager;

        // ─────────────────────────────────────────────
        //  UI Timer Tick (30Hz, UI thread)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Called ~30 times per second on the UI thread.
        /// Reads engine state and pushes it to ViewModels.
        /// </summary>
        private void UiTimer_Tick(object sender, EventArgs e)
        {
            if (_inputManager == null || !_inputManager.IsRunning)
                return;

            // ── Update Pad ViewModels ──
            for (int i = 0; i < InputManager.MaxPads && i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                var gp = _inputManager.CombinedXiStates[i];
                var vibration = _inputManager.VibrationStates[i];

                padVm.UpdateFromEngineState(gp, vibration);

                // Per-device state for stick/trigger tab previews.
                var selected = padVm.SelectedMappedDevice;
                if (selected != null && selected.InstanceGuid != Guid.Empty)
                {
                    var us = SettingsManager.UserSettings?.FindByInstanceGuid(selected.InstanceGuid);
                    padVm.UpdateDeviceState(us?.XiState ?? default);
                }
                else
                {
                    padVm.UpdateDeviceState(gp);
                }
            }

            // ── Update Dashboard ──
            UpdateDashboard();

            // ── Update Devices page (only if visible) ──
            if (IsDevicesPageVisible)
            {
                UpdateDevicesRawState();
            }

            // ── Update mapping row live values (only if a Pad page is visible) ──
            if (IsPadPageVisible)
            {
                UpdateMappingLiveValues();
            }

            // ── Macro trigger recording (accumulate buttons) ──
            UpdateMacroTriggerRecording();

            // ── Push ViewModel settings to PadSetting objects (runtime sync) ──
            SyncViewModelToPadSettings();

            // ── Sync macro snapshots to engine ──
            SyncMacroSnapshots();

            // ── Auto-profile switching (check foreground window) ──
            _foregroundMonitor?.CheckForegroundWindow();
        }

        // ─────────────────────────────────────────────
        //  Dashboard updates
        // ─────────────────────────────────────────────

        /// <summary>
        /// Pushes engine statistics to the DashboardViewModel.
        /// </summary>
        private void UpdateDashboard()
        {
            var dash = _mainVm.Dashboard;

            dash.EngineStatus = _inputManager.IsRunning ? "Running" : "Stopped";
            dash.PollingFrequency = _inputManager.CurrentFrequency;

            // Count online devices.
            var devices = SettingsManager.UserDevices?.Items;
            if (devices != null)
            {
                int total, online, mapped;
                lock (SettingsManager.UserDevices.SyncRoot)
                {
                    total = devices.Count;
                    online = devices.Count(d => d.IsOnline);
                    mapped = 0;

                    var settings = SettingsManager.UserSettings?.Items;
                    if (settings != null)
                    {
                        lock (SettingsManager.UserSettings.SyncRoot)
                        {
                            mapped = settings.Count(s =>
                                devices.Any(d => d.InstanceGuid == s.InstanceGuid && d.IsOnline));
                        }
                    }
                }

                dash.TotalDevices = total;
                dash.OnlineDevices = online;
                dash.MappedDevices = mapped;

                _mainVm.ConnectedDeviceCount = online;
            }

            // Update slot summaries.
            for (int i = 0; i < InputManager.MaxPads && i < dash.SlotSummaries.Count; i++)
            {
                var slot = dash.SlotSummaries[i];
                var padVm = _mainVm.Pads[i];

                slot.IsActive = padVm.IsDeviceOnline;
                slot.DeviceName = padVm.MappedDeviceName;

                // Count mapped and connected devices for this slot.
                var slotSettings = SettingsManager.UserSettings?.FindByPadIndex(i);
                int mappedCount = slotSettings?.Count ?? 0;
                int connectedCount = 0;
                if (slotSettings != null && devices != null)
                {
                    foreach (var us in slotSettings)
                    {
                        if (devices.Any(d => d.InstanceGuid == us.InstanceGuid && d.IsOnline))
                            connectedCount++;
                    }
                }

                slot.MappedDeviceCount = mappedCount;
                slot.ConnectedDeviceCount = connectedCount;
                slot.StatusText = mappedCount == 0 ? "No mapping"
                    : padVm.IsDeviceOnline ? "Active"
                    : "Idle";
            }

            // Update main VM frequency.
            _mainVm.PollingFrequency = _inputManager.CurrentFrequency;
        }

        // ─────────────────────────────────────────────
        //  Devices page raw state
        // ─────────────────────────────────────────────

        /// <summary>
        /// Updates the raw input state display for the selected device
        /// on the Devices page.
        /// </summary>
        private void UpdateDevicesRawState()
        {
            var devVm = _mainVm.Devices;
            var selected = devVm.SelectedDevice;
            if (selected == null)
                return;

            // Find the UserDevice for the selected row.
            UserDevice ud = FindUserDevice(selected.InstanceGuid);
            if (ud == null || ud.InputState == null)
            {
                devVm.RawAxisDisplay = "No data";
                devVm.RawButtonDisplay = "No data";
                devVm.RawPovDisplay = "No data";
                devVm.RawGyroDisplay = string.Empty;
                devVm.RawAccelDisplay = string.Empty;
                return;
            }

            var state = ud.InputState;

            // Format axes.
            var axisLines = new System.Text.StringBuilder();
            int axisCount = Math.Min(ud.CapAxeCount, CustomInputState.MaxAxis);
            for (int i = 0; i < axisCount; i++)
            {
                axisLines.AppendLine($"Axis {i}: {state.Axis[i],6} ({state.Axis[i] * 100.0 / 65535.0:F1}%)");
            }
            devVm.RawAxisDisplay = axisLines.ToString().TrimEnd();

            // Format buttons — use RawButtonCount to show all native buttons,
            // not just the 11 gamepad-mapped ones.
            var btnParts = new System.Collections.Generic.List<string>();
            int btnCount = Math.Min(
                ud.RawButtonCount > 0 ? ud.RawButtonCount : ud.CapButtonCount,
                CustomInputState.MaxButtons);
            for (int i = 0; i < btnCount; i++)
            {
                if (state.Buttons[i])
                    btnParts.Add($"[{i}]");
            }
            devVm.RawButtonDisplay = btnParts.Count > 0
                ? $"Pressed: {string.Join(", ", btnParts)}  ({btnCount} total)"
                : $"No buttons pressed  ({btnCount} total)";

            // Format POVs.
            var povLines = new System.Text.StringBuilder();
            int povCount = Math.Min(ud.CapPovCount, CustomInputState.MaxPovs);
            for (int i = 0; i < povCount; i++)
            {
                int pov = state.Povs[i];
                string povText = pov < 0 ? "Centered" : $"{pov / 100.0:F1}°";
                povLines.AppendLine($"POV {i}: {povText}");
            }
            devVm.RawPovDisplay = povLines.ToString().TrimEnd();

            // Format gyroscope (only if the device has a gyro sensor).
            if (ud.HasGyro)
            {
                devVm.RawGyroDisplay = $"X: {state.Gyro[0],8:F3} rad/s\n" +
                                       $"Y: {state.Gyro[1],8:F3} rad/s\n" +
                                       $"Z: {state.Gyro[2],8:F3} rad/s";
            }
            else
            {
                devVm.RawGyroDisplay = string.Empty;
            }

            // Format accelerometer (only if the device has an accel sensor).
            if (ud.HasAccel)
            {
                devVm.RawAccelDisplay = $"X: {state.Accel[0],8:F3} m/s²\n" +
                                        $"Y: {state.Accel[1],8:F3} m/s²\n" +
                                        $"Z: {state.Accel[2],8:F3} m/s²";
            }
            else
            {
                devVm.RawAccelDisplay = string.Empty;
            }
        }

        // ─────────────────────────────────────────────
        //  Mapping live values
        // ─────────────────────────────────────────────

        /// <summary>
        /// Updates the live value display on mapping rows for the active Pad page.
        /// </summary>
        private void UpdateMappingLiveValues()
        {
            var padVm = _mainVm.SelectedPad;
            if (padVm == null)
                return;

            // Find the selected device for this pad slot.
            UserDevice ud = FindSelectedDeviceForSlot(padVm);
            if (ud == null || ud.InputState == null)
                return;

            var state = ud.InputState;

            foreach (var mapping in padVm.Mappings)
            {
                if (string.IsNullOrEmpty(mapping.SourceDescriptor))
                {
                    mapping.CurrentValueText = string.Empty;
                    continue;
                }

                // Parse the descriptor and read the current value.
                int value = ReadMappedValue(state, mapping.SourceDescriptor);
                mapping.CurrentValueText = value.ToString();
            }
        }

        /// <summary>
        /// Reads a value from a CustomInputState using a mapping descriptor string.
        /// Simplified version of the Step 3 parser for display purposes.
        /// </summary>
        private static int ReadMappedValue(CustomInputState state, string descriptor)
        {
            if (string.IsNullOrEmpty(descriptor))
                return 0;

            string s = descriptor.Trim();

            // Strip prefixes.
            if (s.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);
            else if (s.StartsWith("I", StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
                s = s.Substring(1);
            else if (s.StartsWith("H", StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
                s = s.Substring(1);

            string[] parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out int index))
                return 0;

            string typeName = parts[0].ToLowerInvariant();

            return typeName switch
            {
                "axis" when index >= 0 && index < CustomInputState.MaxAxis => state.Axis[index],
                "slider" when index >= 0 && index < CustomInputState.MaxSliders => state.Sliders[index],
                "button" when index >= 0 && index < CustomInputState.MaxButtons => state.Buttons[index] ? 1 : 0,
                "pov" when index >= 0 && index < CustomInputState.MaxPovs => state.Povs[index],
                _ => 0
            };
        }

        // ─────────────────────────────────────────────
        //  Runtime sync: ViewModel → PadSetting
        // ─────────────────────────────────────────────

        /// <summary>
        /// Pushes ViewModel slider values (dead zones, force feedback, linear)
        /// directly to PadSetting objects so the engine picks them up immediately.
        /// Called at 30Hz on the UI thread. String reference writes are atomic in .NET.
        /// </summary>
        private void SyncViewModelToPadSettings()
        {
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                var selected = padVm.SelectedMappedDevice;
                if (selected == null || selected.InstanceGuid == Guid.Empty)
                    continue;

                SaveViewModelToPadSetting(padVm, selected.InstanceGuid);
            }
        }

        /// <summary>
        /// Saves the current PadViewModel state to a specific device's PadSetting.
        /// </summary>
        private static void SaveViewModelToPadSetting(PadViewModel padVm, Guid instanceGuid)
        {
            var us = SettingsManager.FindSettingByInstanceGuid(instanceGuid);
            if (us == null) return;

            var ps = us.GetPadSetting();
            if (ps == null) return;

            // Dead zones (independent X/Y).
            ps.LeftThumbDeadZoneX = padVm.LeftDeadZoneX.ToString();
            ps.LeftThumbDeadZoneY = padVm.LeftDeadZoneY.ToString();
            ps.RightThumbDeadZoneX = padVm.RightDeadZoneX.ToString();
            ps.RightThumbDeadZoneY = padVm.RightDeadZoneY.ToString();

            // Anti-dead zones (per-axis).
            ps.LeftThumbAntiDeadZoneX = padVm.LeftAntiDeadZoneX.ToString();
            ps.LeftThumbAntiDeadZoneY = padVm.LeftAntiDeadZoneY.ToString();
            ps.RightThumbAntiDeadZoneX = padVm.RightAntiDeadZoneX.ToString();
            ps.RightThumbAntiDeadZoneY = padVm.RightAntiDeadZoneY.ToString();

            // Linear response.
            ps.LeftThumbLinear = padVm.LeftLinear.ToString();
            ps.RightThumbLinear = padVm.RightLinear.ToString();

            // Trigger dead zones.
            ps.LeftTriggerDeadZone = padVm.LeftTriggerDeadZone.ToString();
            ps.RightTriggerDeadZone = padVm.RightTriggerDeadZone.ToString();
            ps.LeftTriggerAntiDeadZone = padVm.LeftTriggerAntiDeadZone.ToString();
            ps.RightTriggerAntiDeadZone = padVm.RightTriggerAntiDeadZone.ToString();

            // Force feedback.
            ps.ForceOverall = padVm.ForceOverallGain.ToString();
            ps.LeftMotorStrength = padVm.LeftMotorStrength.ToString();
            ps.RightMotorStrength = padVm.RightMotorStrength.ToString();
            ps.ForceSwapMotor = padVm.SwapMotors ? "1" : "0";

            // Mapping descriptors.
            foreach (var mapping in padVm.Mappings)
            {
                var prop = typeof(PadSetting).GetProperty(mapping.TargetSettingName);
                if (prop != null && prop.PropertyType == typeof(string) && prop.CanWrite)
                    prop.SetValue(ps, mapping.SourceDescriptor ?? string.Empty);
            }
        }

        /// <summary>
        /// Loads a specific device's PadSetting into the PadViewModel.
        /// </summary>
        private static void LoadPadSettingToViewModel(PadViewModel padVm, Guid instanceGuid)
        {
            var us = SettingsManager.FindSettingByInstanceGuid(instanceGuid);
            if (us == null) return;

            var ps = us.GetPadSetting();
            if (ps == null) return;

            // Dead zones.
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

            // Trigger dead zones.
            padVm.LeftTriggerDeadZone = TryParseInt(ps.LeftTriggerDeadZone, 0);
            padVm.RightTriggerDeadZone = TryParseInt(ps.RightTriggerDeadZone, 0);
            padVm.LeftTriggerAntiDeadZone = TryParseInt(ps.LeftTriggerAntiDeadZone, 0);
            padVm.RightTriggerAntiDeadZone = TryParseInt(ps.RightTriggerAntiDeadZone, 0);

            // Force feedback.
            padVm.ForceOverallGain = TryParseInt(ps.ForceOverall, 100);
            padVm.LeftMotorStrength = TryParseInt(ps.LeftMotorStrength, 100);
            padVm.RightMotorStrength = TryParseInt(ps.RightMotorStrength, 100);
            padVm.SwapMotors = ps.ForceSwapMotor == "1" ||
                (ps.ForceSwapMotor ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);

            // Mapping descriptors.
            foreach (var mapping in padVm.Mappings)
            {
                var prop = typeof(PadSetting).GetProperty(mapping.TargetSettingName);
                string value = (prop != null && prop.PropertyType == typeof(string))
                    ? prop.GetValue(ps) as string ?? string.Empty
                    : string.Empty;
                mapping.SourceDescriptor = value;
            }
        }

        private static int TryParseInt(string value, int defaultValue)
        {
            if (string.IsNullOrEmpty(value)) return defaultValue;
            return int.TryParse(value, out int result) ? result : defaultValue;
        }

        // ─────────────────────────────────────────────
        //  Copy / Paste settings
        // ─────────────────────────────────────────────

        /// <summary>
        /// Applies a source PadSetting to the currently selected device in the given pad slot.
        /// Used by both clipboard Paste and "Copy From" operations.
        /// </summary>
        public void ApplyPadSettingToCurrentDevice(int padIndex, PadSetting source)
        {
            if (source == null || padIndex < 0 || padIndex >= _mainVm.Pads.Count)
                return;

            var padVm = _mainVm.Pads[padIndex];
            var selected = padVm.SelectedMappedDevice;
            if (selected == null || selected.InstanceGuid == Guid.Empty)
                return;

            var us = SettingsManager.FindSettingByInstanceGuid(selected.InstanceGuid);
            if (us == null) return;

            var ps = us.GetPadSetting();
            if (ps == null) return;

            // Copy all settings from the source.
            ps.CopyFrom(source);

            // Reload the ViewModel to reflect the new values.
            LoadPadSettingToViewModel(padVm, selected.InstanceGuid);
        }

        /// <summary>
        /// Gets the PadSetting for the currently selected device in the given pad slot.
        /// Returns null if no device is selected.
        /// </summary>
        public PadSetting GetCurrentPadSetting(int padIndex)
        {
            if (padIndex < 0 || padIndex >= _mainVm.Pads.Count)
                return null;

            var padVm = _mainVm.Pads[padIndex];
            var selected = padVm.SelectedMappedDevice;
            if (selected == null || selected.InstanceGuid == Guid.Empty)
                return null;

            // First sync the ViewModel to the PadSetting to capture any unsaved slider changes.
            SaveViewModelToPadSetting(padVm, selected.InstanceGuid);

            var us = SettingsManager.FindSettingByInstanceGuid(selected.InstanceGuid);
            return us?.GetPadSetting();
        }

        // ─────────────────────────────────────────────
        //  Per-device settings swap
        // ─────────────────────────────────────────────

        /// <summary>
        /// Called when the user selects a different device in a pad slot's dropdown.
        /// Saves current ViewModel values to the old device's PadSetting, then loads
        /// the new device's PadSetting into the ViewModel.
        /// </summary>
        private void OnSelectedDeviceChanged(object sender, PadViewModel.MappedDeviceInfo newDevice)
        {
            if (sender is not PadViewModel padVm)
                return;

            // Save ViewModel state to the PREVIOUSLY selected device's PadSetting.
            if (_previousSelectedDevice.TryGetValue(padVm.PadIndex, out Guid previousGuid)
                && previousGuid != Guid.Empty)
            {
                SaveViewModelToPadSetting(padVm, previousGuid);
            }

            // Load the new device's PadSetting into the ViewModel.
            if (newDevice != null && newDevice.InstanceGuid != Guid.Empty)
            {
                LoadPadSettingToViewModel(padVm, newDevice.InstanceGuid);
                _previousSelectedDevice[padVm.PadIndex] = newDevice.InstanceGuid;
            }
        }

        // ─────────────────────────────────────────────
        //  Macro snapshot sync
        // ─────────────────────────────────────────────

        /// <summary>
        /// Pushes the current macro lists from PadViewModels to the engine's
        /// MacroSnapshots array. The engine reads these atomically each cycle.
        /// Called at 30Hz on the UI thread.
        /// </summary>
        private void SyncMacroSnapshots()
        {
            if (_inputManager == null)
                return;

            for (int i = 0; i < InputManager.MaxPads && i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                if (padVm.Macros.Count == 0)
                {
                    _inputManager.MacroSnapshots[i] = null;
                }
                else
                {
                    // Create a snapshot array. The MacroItem objects are shared references —
                    // runtime state (IsExecuting, CurrentActionIndex, etc.) is read/written
                    // by the engine thread, but the properties themselves are simple fields
                    // that don't need locking for this use case.
                    var snapshot = new MacroItem[padVm.Macros.Count];
                    padVm.Macros.CopyTo(snapshot, 0);
                    _inputManager.MacroSnapshots[i] = snapshot;
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Engine event handlers (background thread → UI thread)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Called on the background thread when the device list changes.
        /// Marshals to the UI thread to sync DevicesViewModel.
        /// </summary>
        private void OnDevicesUpdated(object sender, EventArgs e)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                SyncDevicesList();
                UpdatePadDeviceInfo();
            }));
        }

        /// <summary>
        /// Called on the background thread when the frequency measurement updates.
        /// </summary>
        private void OnFrequencyUpdated(object sender, EventArgs e)
        {
            // Frequency is read on the next UI timer tick, no immediate action needed.
        }

        /// <summary>
        /// Called on the background thread when a non-fatal error occurs.
        /// </summary>
        private void OnErrorOccurred(object sender, InputExceptionEventArgs e)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                _mainVm.StatusText = $"Error: {e.Message}";
            }));
        }

        /// <summary>
        /// Propagates settings changes to the engine at runtime.
        /// </summary>
        private void OnSettingsPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SettingsViewModel.PollingRateMs) && _inputManager != null)
            {
                _inputManager.PollingIntervalMs = _mainVm.Settings.PollingRateMs;
            }
        }

        // ─────────────────────────────────────────────
        //  Device list sync
        // ─────────────────────────────────────────────

        /// <summary>
        /// Synchronizes the DevicesViewModel.Devices collection with
        /// SettingsManager.UserDevices. Called on the UI thread.
        /// 
        /// Filtering strategy:
        ///   ViGEm virtual controllers are already filtered out by Step 1
        ///   (IsViGEmVirtualDevice) via device path inspection. This is a
        ///   defense-in-depth layer that catches any that leak through.
        /// </summary>
        private void SyncDevicesList()
        {
            var devVm = _mainVm.Devices;
            var userDevices = SettingsManager.UserDevices?.Items;
            if (userDevices == null)
                return;

            UserDevice[] snapshot;
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                snapshot = userDevices.ToArray();
            }

            // Update existing rows and add new ones (skip virtual devices).
            foreach (var ud in snapshot)
            {
                if (IsVirtualOrShadowDevice(ud))
                    continue;

                var row = devVm.FindByGuid(ud.InstanceGuid);
                if (row == null)
                {
                    row = new DeviceRowViewModel();
                    devVm.Devices.Add(row);
                }

                PopulateDeviceRow(row, ud);
            }

            // Remove rows for devices that are no longer valid or are virtual.
            for (int i = devVm.Devices.Count - 1; i >= 0; i--)
            {
                var row = devVm.Devices[i];

                bool found = false;
                bool isVirtual = false;

                foreach (var ud in snapshot)
                {
                    if (ud.InstanceGuid == row.InstanceGuid)
                    {
                        if (IsVirtualOrShadowDevice(ud))
                        {
                            isVirtual = true;
                            break;
                        }
                        found = true;
                        break;
                    }
                }

                if (isVirtual || !found)
                    devVm.Devices.RemoveAt(i);
            }

            devVm.RefreshCounts();
        }

        /// <summary>
        /// Determines whether a UserDevice is a virtual controller or a shadow device
        /// that should be hidden from the user-facing device list.
        ///
        /// With SDL3-only mode, ViGEm virtual controllers are primarily filtered
        /// at the engine level (Step 1, IsViGEmVirtualDevice). This is a
        /// defense-in-depth layer.
        /// </summary>
        private static bool IsVirtualOrShadowDevice(UserDevice ud)
        {
            // Offline devices are never virtual controllers — virtual controllers
            // only exist while the engine is running.
            if (!ud.IsOnline)
                return false;

            // ── Name-based detection ──
            string name = ud.ResolvedName;
            if (!string.IsNullOrEmpty(name))
            {
                if (name.Contains("ViGEm", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Virtual Gamepad", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // ── Device path detection ──
            string path = ud.DevicePath;
            if (!string.IsNullOrEmpty(path))
            {
                string pathLower = path.ToLowerInvariant();
                if (pathLower.Contains("vigem") || pathLower.Contains("virtual"))
                    return true;
            }

            // ── Hidden flag ──
            if (ud.IsHidden)
                return true;

            return false;
        }

        /// <summary>
        /// Populates a DeviceRowViewModel from a UserDevice.
        /// </summary>
        private void PopulateDeviceRow(DeviceRowViewModel row, UserDevice ud)
        {
            row.InstanceGuid = ud.InstanceGuid;
            row.DeviceName = ud.ResolvedName;
            row.ProductName = ud.ProductName;
            row.ProductGuid = ud.ProductGuid;
            row.VendorId = ud.VendorId;
            row.ProductId = ud.ProdId;
            row.IsOnline = ud.IsOnline;
            row.IsEnabled = ud.IsEnabled;
            row.IsHidden = ud.IsHidden;
            row.AxisCount = ud.CapAxeCount;
            row.ButtonCount = ud.CapButtonCount;
            row.PovCount = ud.CapPovCount;
            row.HasRumble = ud.HasForceFeedback;
            row.HasGyro = ud.HasGyro;
            row.HasAccel = ud.HasAccel;
            row.DevicePath = ud.DevicePath;

            // Resolve device type name.
            row.DeviceType = ud.CapType switch
            {
                InputDeviceType.Gamepad => "Gamepad",
                InputDeviceType.Joystick => "Joystick",
                InputDeviceType.Driving => "Wheel",
                InputDeviceType.Flight => "Flight Stick",
                InputDeviceType.FirstPerson => "First Person",
                InputDeviceType.Supplemental => "Supplemental",
                InputDeviceType.Mouse => "Mouse",
                InputDeviceType.Keyboard => "Keyboard",
                _ => "Device"
            };

            // Resolve slot assignment.
            var us = SettingsManager.UserSettings?.FindByInstanceGuid(ud.InstanceGuid);
            row.AssignedSlot = us?.MapTo ?? -1;
        }

        /// <summary>
        /// Updates PadViewModel device info (name, online status) for all pads.
        /// Populates the MappedDevices collection with ALL devices assigned to each slot.
        /// Called after the device list changes or after a device is assigned to a slot.
        /// </summary>
        /// <summary>
        /// Forces a full re-sync of the device list UI from the current
        /// SettingsManager.UserDevices state. Called by the Refresh button.
        /// </summary>
        public void RefreshDeviceList()
        {
            SyncDevicesList();
            UpdatePadDeviceInfo();
        }

        public void UpdatePadDeviceInfo()
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return;

            for (int i = 0; i < InputManager.MaxPads && i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                var slotSettings = settings.FindByPadIndex(i);

                if (slotSettings == null || slotSettings.Count == 0)
                {
                    padVm.MappedDevices.Clear();
                    padVm.MappedDeviceName = "No device mapped";
                    padVm.MappedDeviceGuid = Guid.Empty;
                    padVm.IsDeviceOnline = false;
                }
                else
                {
                    // Build list of all mapped devices for this slot.
                    var deviceInfos = new List<PadViewModel.MappedDeviceInfo>();
                    bool anyOnline = false;

                    foreach (var us in slotSettings)
                    {
                        var ud = FindUserDevice(us.InstanceGuid);
                        string name = ud?.ResolvedName ?? "Unknown device";
                        bool online = ud?.IsOnline ?? false;
                        if (online) anyOnline = true;

                        deviceInfos.Add(new PadViewModel.MappedDeviceInfo
                        {
                            Name = name,
                            InstanceGuid = us.InstanceGuid,
                            IsOnline = online
                        });
                    }

                    // Sync the ObservableCollection (minimize UI churn).
                    SyncMappedDevices(padVm.MappedDevices, deviceInfos);

                    // Auto-select first device if nothing is selected.
                    if (padVm.SelectedMappedDevice == null && padVm.MappedDevices.Count > 0)
                    {
                        padVm.SelectedMappedDevice = padVm.MappedDevices[0];
                    }

                    // Initialize the previous-device tracker if not set.
                    if (!_previousSelectedDevice.ContainsKey(i) && padVm.SelectedMappedDevice != null)
                    {
                        _previousSelectedDevice[i] = padVm.SelectedMappedDevice.InstanceGuid;
                    }

                    // Summary properties for backward compatibility / simple bindings.
                    var primary = slotSettings[0];
                    var primaryUd = FindUserDevice(primary.InstanceGuid);

                    padVm.MappedDeviceName = deviceInfos.Count == 1
                        ? deviceInfos[0].Name
                        : string.Join(" + ", deviceInfos.Select(d => d.Name));
                    padVm.MappedDeviceGuid = primary.InstanceGuid;
                    padVm.IsDeviceOnline = anyOnline;
                }

                padVm.RefreshCommands();
            }
        }

        /// <summary>
        /// Synchronizes the ObservableCollection with a new list,
        /// minimizing UI churn by updating in-place where possible.
        /// </summary>
        private static void SyncMappedDevices(
            System.Collections.ObjectModel.ObservableCollection<PadViewModel.MappedDeviceInfo> collection,
            List<PadViewModel.MappedDeviceInfo> newItems)
        {
            // Remove extras.
            while (collection.Count > newItems.Count)
                collection.RemoveAt(collection.Count - 1);

            // Update existing and add new.
            for (int i = 0; i < newItems.Count; i++)
            {
                if (i < collection.Count)
                {
                    collection[i].Name = newItems[i].Name;
                    collection[i].InstanceGuid = newItems[i].InstanceGuid;
                    collection[i].IsOnline = newItems[i].IsOnline;
                }
                else
                {
                    collection.Add(newItems[i]);
                }
            }
        }

        // ─────────────────────────────────────────────
        //  UserDevice lookup helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Finds a UserDevice by instance GUID from the SettingsManager collection.
        /// </summary>
        private static UserDevice FindUserDevice(Guid instanceGuid)
        {
            var devices = SettingsManager.UserDevices?.Items;
            if (devices == null) return null;

            lock (SettingsManager.UserDevices.SyncRoot)
            {
                return devices.FirstOrDefault(d => d.InstanceGuid == instanceGuid);
            }
        }

        /// <summary>
        /// Finds the UserDevice for the currently selected device in a pad slot's dropdown.
        /// Falls back to the first device in the slot if nothing is selected.
        /// </summary>
        private static UserDevice FindSelectedDeviceForSlot(PadViewModel padVm)
        {
            // Use the dropdown-selected device if available.
            if (padVm.SelectedMappedDevice != null &&
                padVm.SelectedMappedDevice.InstanceGuid != Guid.Empty)
            {
                return FindUserDevice(padVm.SelectedMappedDevice.InstanceGuid);
            }

            // Fallback: first device in slot.
            var settings = SettingsManager.UserSettings;
            if (settings == null) return null;

            var slotSettings = settings.FindByPadIndex(padVm.PadIndex);
            if (slotSettings == null || slotSettings.Count == 0)
                return null;

            return FindUserDevice(slotSettings[0].InstanceGuid);
        }

        // ─────────────────────────────────────────────
        //  Test rumble
        // ─────────────────────────────────────────────

        /// <summary>
        /// Sends a brief test rumble to a specific device (or all devices in a slot).
        /// </summary>
        /// <param name="padIndex">Pad slot index (0–3).</param>
        /// <param name="deviceGuid">Optional device GUID to target. When null, rumbles all devices in the slot.</param>
        public void SendTestRumble(int padIndex, Guid? deviceGuid)
        {
            SendTestRumble(padIndex, deviceGuid, true, true);
        }

        public void SendTestRumble(int padIndex, Guid? deviceGuid, bool left, bool right)
        {
            if (_inputManager == null || padIndex < 0 || padIndex >= InputManager.MaxPads)
                return;

            // Set device-level filter so the background thread only rumbles the target device.
            if (deviceGuid.HasValue && deviceGuid.Value != Guid.Empty)
                _inputManager.TestRumbleTargetGuid[padIndex] = deviceGuid.Value;

            // Set vibration via slot-level state (background thread applies it).
            if (left) _inputManager.VibrationStates[padIndex].LeftMotorSpeed = 32768;
            if (right) _inputManager.VibrationStates[padIndex].RightMotorSpeed = 32768;

            // Schedule clearing after 500ms.
            var clearTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            clearTimer.Tick += (s2, e2) =>
            {
                if (_inputManager != null && padIndex < InputManager.MaxPads)
                {
                    if (left) _inputManager.VibrationStates[padIndex].LeftMotorSpeed = 0;
                    if (right) _inputManager.VibrationStates[padIndex].RightMotorSpeed = 0;
                    _inputManager.TestRumbleTargetGuid[padIndex] = Guid.Empty;
                }
                clearTimer.Stop();
            };
            clearTimer.Start();
        }

        // ─────────────────────────────────────────────
        //  Macro trigger recording
        // ─────────────────────────────────────────────

        /// <summary>
        /// Starts recording button presses for a macro trigger combo.
        /// While recording, CombinedXiState button flags are OR'd together
        /// each UI tick. Call <see cref="StopMacroTriggerRecording"/> to
        /// finalize and write the result to the MacroItem.
        /// </summary>
        public void StartMacroTriggerRecording(MacroItem macro, int padIndex)
        {
            // Stop any existing recording.
            if (_recordingMacro != null)
                StopMacroTriggerRecording();

            _recordingMacro = macro;
            _recordingPadIndex = padIndex;
            _recordedButtons = 0;
            _recordingDeviceGuid = Guid.Empty;
            _recordedRawButtons = new HashSet<int>();
            macro.IsRecordingTrigger = true;
        }

        /// <summary>
        /// Stops the current macro trigger recording session and writes the
        /// accumulated trigger data to the MacroItem.
        /// </summary>
        public void StopMacroTriggerRecording()
        {
            if (_recordingMacro == null)
                return;

            if (_recordingMacro.TriggerSource == MacroTriggerSource.InputDevice
                && _recordingDeviceGuid != Guid.Empty
                && _recordedRawButtons != null && _recordedRawButtons.Count > 0)
            {
                // Raw device button path.
                _recordingMacro.TriggerDeviceGuid = _recordingDeviceGuid;
                _recordingMacro.TriggerRawButtons = _recordedRawButtons.OrderBy(x => x).ToArray();
                _recordingMacro.TriggerButtons = 0; // Clear legacy
            }
            else
            {
                // Xbox bitmask path (OutputController or fallback).
                _recordingMacro.TriggerButtons = _recordedButtons;
                _recordingMacro.TriggerDeviceGuid = Guid.Empty;
                _recordingMacro.TriggerRawButtons = Array.Empty<int>();
            }

            _recordingMacro.IsRecordingTrigger = false;
            _recordingMacro = null;
            _recordedButtons = 0;
            _recordingDeviceGuid = Guid.Empty;
            _recordedRawButtons = null;
        }

        /// <summary>
        /// Called each UI tick during macro trigger recording.
        /// When TriggerSource is InputDevice, reads raw button state from individual
        /// devices mapped to the pad slot; the first device to press a button "locks in".
        /// When TriggerSource is OutputController, reads from the combined Xbox-mapped state.
        /// </summary>
        private void UpdateMacroTriggerRecording()
        {
            if (_recordingMacro == null || _inputManager == null)
                return;

            if (_recordingPadIndex < 0 || _recordingPadIndex >= InputManager.MaxPads)
                return;

            if (_recordingMacro.TriggerSource == MacroTriggerSource.InputDevice)
            {
                // Scan raw buttons from devices mapped to this pad slot.
                var slotSettings = SettingsManager.UserSettings?.FindByPadIndex(_recordingPadIndex);
                if (slotSettings != null)
                {
                    foreach (var setting in slotSettings)
                    {
                        var ud = FindUserDevice(setting.InstanceGuid);
                        if (ud == null || !ud.IsOnline || ud.InputState == null)
                            continue;

                        // If already locked to a different device, skip.
                        if (_recordingDeviceGuid != Guid.Empty && _recordingDeviceGuid != ud.InstanceGuid)
                            continue;

                        // Check for any pressed buttons on this device.
                        var buttons = ud.InputState.Buttons;
                        int count = Math.Min(buttons.Length, ud.Device?.RawButtonCount ?? buttons.Length);
                        for (int i = 0; i < count; i++)
                        {
                            if (buttons[i])
                            {
                                // Lock to this device on first press.
                                if (_recordingDeviceGuid == Guid.Empty)
                                    _recordingDeviceGuid = ud.InstanceGuid;

                                _recordedRawButtons.Add(i);
                            }
                        }
                    }
                }
            }
            else
            {
                // OutputController: accumulate from the combined Xbox-mapped state.
                ushort xboxButtons = _inputManager.CombinedXiStates[_recordingPadIndex].Buttons;
                _recordedButtons |= xboxButtons;
            }
        }

        // ─────────────────────────────────────────────
        //  Profile switching
        // ─────────────────────────────────────────────

        /// <summary>
        /// Saves the current runtime PadSettings and macros into a ProfileData snapshot.
        /// Used to capture the current state before switching profiles.
        /// </summary>
        public ProfileData SnapshotCurrentProfile()
        {
            // Ensure ViewModel values are pushed to PadSettings first.
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                var selected = padVm.SelectedMappedDevice;
                if (selected != null && selected.InstanceGuid != Guid.Empty)
                    SaveViewModelToPadSetting(padVm, selected.InstanceGuid);
            }

            var entries = new List<ProfileEntry>();
            var padSettings = new List<PadSetting>();
            var seen = new HashSet<string>();

            lock (SettingsManager.UserSettings.SyncRoot)
            {
                foreach (var us in SettingsManager.UserSettings.Items)
                {
                    var ps = us.GetPadSetting();
                    if (ps == null) continue;

                    ps.UpdateChecksum();

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

            return new ProfileData
            {
                Entries = entries.ToArray(),
                PadSettings = padSettings.ToArray()
            };
        }

        /// <summary>
        /// Loads a profile's PadSettings into the runtime state.
        /// For each ProfileEntry, finds the matching UserSetting and swaps its PadSetting.
        /// </summary>
        private void ApplyProfile(ProfileData profile)
        {
            if (profile?.Entries == null || profile.Entries.Length == 0 ||
                profile.PadSettings == null || profile.PadSettings.Length == 0)
                return;

            lock (SettingsManager.UserSettings.SyncRoot)
            {
                foreach (var entry in profile.Entries)
                {
                    var us = SettingsManager.UserSettings.Items
                        .FirstOrDefault(s => s.InstanceGuid == entry.InstanceGuid);
                    if (us == null) continue;

                    // Find the PadSetting template by checksum.
                    var template = profile.PadSettings
                        .FirstOrDefault(p => p.PadSettingChecksum == entry.PadSettingChecksum);
                    if (template == null) continue;

                    // Clone and apply.
                    var ps = template.CloneDeep();
                    us.SetPadSetting(ps);
                }
            }

            // Reload ViewModels with new PadSettings.
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                var selected = padVm.SelectedMappedDevice;
                if (selected != null && selected.InstanceGuid != Guid.Empty)
                    LoadPadSettingToViewModel(padVm, selected.InstanceGuid);
            }
        }

        /// <summary>
        /// Called by <see cref="ForegroundMonitorService"/> when the foreground
        /// process matches a different profile. Runs on the UI thread.
        /// </summary>
        private void OnProfileSwitchRequired(string profileId)
        {
            // If switching to the same profile, skip.
            if (profileId == SettingsManager.ActiveProfileId)
                return;

            // Switch to the target profile (or revert to default).
            if (profileId != null)
            {
                var target = FindProfileById(profileId);
                if (target != null)
                {
                    ApplyProfile(target);
                    SettingsManager.ActiveProfileId = profileId;
                    _mainVm.StatusText = $"Profile switched: {target.Name}";
                }
            }
            else
            {
                // Revert to default (root) profile using the startup snapshot.
                if (_defaultProfileSnapshot != null)
                    ApplyProfile(_defaultProfileSnapshot);
                SettingsManager.ActiveProfileId = null;
                _mainVm.StatusText = "Profile switched: Default";
            }
        }

        private static ProfileData FindProfileById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return SettingsManager.Profiles?.FirstOrDefault(p => p.Id == id);
        }

        // ─────────────────────────────────────────────
        //  IDisposable
        // ─────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            _disposed = true;
        }
    }
}
