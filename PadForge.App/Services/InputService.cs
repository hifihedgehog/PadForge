using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
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
        private DsuMotionServer _dsuServer;
        private WebControllerServer _webServer;
        private InputHookManager _hookManager;
        private SettingsService _settingsService;
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

        /// <summary>
        /// Optional reference to the settings service for triggering saves
        /// when cached data (e.g. HidHide instance IDs) is updated.
        /// </summary>
        public SettingsService SettingsService { set => _settingsService = value; }

        // ── Macro trigger recording state ──
        private MacroItem _recordingMacro;
        private int _recordingPadIndex;
        private ushort _recordedButtons;
        private uint[] _recordedCustomButtons;
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
                padVm.MappingsRebuilt += OnMappingsRebuilt;
            }

            // Subscribe to Devices page selection changes for offline detail display.
            _mainVm.Devices.PropertyChanged += OnDevicesVmPropertyChanged;
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

            // Remove stale ViGEm USB device nodes left over from previous sessions
            // (e.g., app crash without Dispose, or old builds that didn't call Dispose).
            // Must run BEFORE SDL initialization so stale nodes aren't enumerated.
            InputManager.CleanupStaleVigemDevices();

            // Don't remove vJoy nodes on startup — Step 5's EnsureDevicesAvailable
            // handles the correct descriptor count, creates missing nodes, removes excess,
            // and restarts when descriptors change. Removing here causes an unnecessary
            // 10+ second remove+recreate cycle on every normal restart (especially on
            // Win11 builds where pnputil /remove-device returns 3010 and scan-devices
            // takes ~10 seconds to clean up ghost PDOs).
            // Create engine with the configured polling interval.
            _inputManager = new InputManager();
            _inputManager.PollingIntervalMs = _mainVm.Settings.PollingRateMs;

            // Copy controller types and vJoy configs immediately so Step 5 creates
            // the correct VC types from the first polling cycle (don't wait for UI timer sync).
            int expectedXbox = 0, expectedDs4 = 0;
            for (int i = 0; i < InputManager.MaxPads && i < _mainVm.Pads.Count; i++)
            {
                _inputManager.SlotControllerTypes[i] = _mainVm.Pads[i].OutputType;
                SyncVJoyConfigToSlot(i, _mainVm.Pads[i]);
                _inputManager._midiConfigs[i] = _mainVm.Pads[i].MidiConfig;
                if (SettingsManager.SlotCreated[i] && SettingsManager.SlotEnabled[i])
                {
                    if (_mainVm.Pads[i].OutputType == VirtualControllerType.Xbox360) expectedXbox++;
                    else if (_mainVm.Pads[i].OutputType == VirtualControllerType.DualShock4) expectedDs4++;
                }
            }

            // Pre-initialize expected ViGEm counts so the device filter catches
            // ViGEm virtual controllers on the very first UpdateDevices() cycle
            // (before Step 5 has created any actual VCs).
            _inputManager.PreInitializeVigemCounts(expectedXbox, expectedDs4);

            // Subscribe to engine events (raised on background thread).
            _inputManager.DevicesUpdated += OnDevicesUpdated;
            _inputManager.FrequencyUpdated += OnFrequencyUpdated;
            _inputManager.ErrorOccurred += OnErrorOccurred;

            // Subscribe to settings/dashboard property changes for runtime propagation.
            _mainVm.Settings.PropertyChanged += OnSettingsPropertyChanged;
            _mainVm.Dashboard.PropertyChanged += OnDashboardPropertyChanged;

            // Create foreground monitor for auto-profile switching.
            _foregroundMonitor = new ForegroundMonitorService();
            _foregroundMonitor.ProfileSwitchRequired += OnProfileSwitchRequired;

            // Capture default profile snapshot before any profile switches.
            _defaultProfileSnapshot = SnapshotCurrentProfile();

            // Start engine background thread.
            _inputManager.Start();

            // Start DSU motion server if enabled.
            StartDsuServerIfEnabled();

            // Start web controller server if enabled.
            StartWebServerIfEnabled();

            // Clear stale HidHide blacklist entries from previous crash/kill.
            // _managedDeviceIds is in-memory so entries are lost on restart,
            // making RemoveManagedDevices() unable to clean up stale entries.
            try
            {
                if (HidHideController.IsAvailable())
                    HidHideController.ClearAll();
            }
            catch { /* best effort */ }

            // Apply device hiding (HidHide + input hooks) if master switch is on.
            ApplyDeviceHiding();

            // Create UI update timer on the dispatcher.
            _uiTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(UiTimerIntervalMs)
            };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();

            // Update main VM state.
            _mainVm.IsEngineRunning = true;
            _mainVm.Dashboard.EngineStatus = "Running";
            _mainVm.StatusText = "Engine started.";
            _mainVm.RefreshCommands();
        }

        /// <summary>
        /// Stops the UI timer and engine, releases resources.
        /// </summary>
        public void Stop(bool preserveVJoyNodes = false)
        {
            // Stop UI timer.
            if (_uiTimer != null)
            {
                _uiTimer.Stop();
                _uiTimer.Tick -= UiTimer_Tick;
                _uiTimer = null;
            }

            // Unsubscribe from settings/dashboard changes.
            _mainVm.Settings.PropertyChanged -= OnSettingsPropertyChanged;
            _mainVm.Dashboard.PropertyChanged -= OnDashboardPropertyChanged;

            // Dispose foreground monitor.
            if (_foregroundMonitor != null)
            {
                _foregroundMonitor.ProfileSwitchRequired -= OnProfileSwitchRequired;
                _foregroundMonitor = null;
            }

            // Stop DSU server.
            StopDsuServer();

            // Stop web controller server.
            StopWebServer();

            // Remove device hiding (HidHide blacklist entries + input hooks).
            RemoveDeviceHiding();

            // Stop and dispose engine.
            if (_inputManager != null)
            {
                _inputManager.DevicesUpdated -= OnDevicesUpdated;
                _inputManager.FrequencyUpdated -= OnFrequencyUpdated;
                _inputManager.ErrorOccurred -= OnErrorOccurred;
                // Pass preserveVJoyNodes so engine teardown disables (not removes)
                // vJoy device nodes when we're about to restart immediately.
                _inputManager.Stop(preserveVJoyNodes);
                _inputManager.Dispose();
                _inputManager = null;
            }

            // Update main VM state.
            _mainVm.IsEngineRunning = false;
            _mainVm.Dashboard.EngineStatus = "Stopped";
            _mainVm.Dashboard.PollingFrequency = 0;
            _mainVm.Dashboard.OnlineDevices = 0;
            _mainVm.PollingFrequency = 0;
            _mainVm.StatusText = "Engine stopped.";
            _mainVm.RefreshCommands();

            // Mark all device rows offline so indicators turn gray.
            foreach (var row in _mainVm.Devices.Devices)
                row.IsOnline = false;
            _mainVm.Devices.RefreshCounts();

            if (!preserveVJoyNodes)
            {
                // Remove vJoy device nodes so dormant devices don't appear in
                // Game Controllers — games may latch onto them as valid input.
                // Allow up to 3s for cleanup, then let the app exit regardless.
                if (VJoyVirtualController.CountExistingDevices() > 0)
                {
                    var cleanupTask = System.Threading.Tasks.Task.Run(
                        () => VJoyVirtualController.RemoveAllDeviceNodes());
                    cleanupTask.Wait(TimeSpan.FromSeconds(3));
                }
            }
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
                var gp = _inputManager.CombinedOutputStates[i];
                var vibration = _inputManager.VibrationStates[i];

                padVm.UpdateFromEngineState(gp, vibration);

                // For custom vJoy slots, also push the combined VJoyRawState.
                if (_inputManager.SlotVJoyIsCustom[i])
                    padVm.UpdateFromVJoyRawState(_inputManager.CombinedVJoyRawStates[i]);

                // For MIDI slots, push the combined MidiRawState.
                if (_inputManager.SlotControllerTypes[i] == VirtualControllerType.Midi)
                    padVm.UpdateFromMidiRawState(_inputManager.CombinedMidiRawStates[i]);

                // Per-device state for stick/trigger tab previews.
                var selected = padVm.SelectedMappedDevice;
                if (selected != null && selected.InstanceGuid != Guid.Empty)
                {
                    var us = SettingsManager.FindSettingByInstanceGuidAndSlot(selected.InstanceGuid, i);
                    if (_inputManager.SlotVJoyIsCustom[i] && us != null)
                        padVm.UpdateFromVJoyRawState(us.VJoyRawOutputState);
                    else
                        padVm.UpdateDeviceState(us?.RawMappedState ?? default);
                }
                else if (!_inputManager.SlotVJoyIsCustom[i])
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

            // Snapshot devices under lock to avoid cross-thread collection-modified
            // exceptions when the engine's UpdateDevices runs concurrently.
            UserDevice[] deviceSnapshot = null;
            var ud = SettingsManager.UserDevices;
            if (ud != null)
            {
                int total, online, mapped;
                lock (ud.SyncRoot)
                {
                    var devices = ud.Items;
                    deviceSnapshot = devices.ToArray();
                    total = deviceSnapshot.Length;
                    online = deviceSnapshot.Count(d => d.IsOnline);
                    mapped = 0;

                    var settings = SettingsManager.UserSettings?.Items;
                    if (settings != null)
                    {
                        lock (SettingsManager.UserSettings.SyncRoot)
                        {
                            mapped = settings.Count(s =>
                                deviceSnapshot.Any(d => d.InstanceGuid == s.InstanceGuid && d.IsOnline));
                        }
                    }
                }

                dash.TotalDevices = total;
                dash.OnlineDevices = online;
                dash.MappedDevices = mapped;

                _mainVm.ConnectedDeviceCount = online;
            }

            RefreshSlotSummaryProperties(deviceSnapshot);
            RefreshNavItemConnectedCounts(deviceSnapshot);

            // Update main VM frequency.
            _mainVm.PollingFrequency = _inputManager.CurrentFrequency;
        }

        /// <summary>
        /// Updates all SlotSummary properties on the dashboard (type, label, status, device info).
        /// Safe to call with or without the engine running.
        /// </summary>
        public void RefreshSlotSummaryProperties(IEnumerable<UserDevice> devices = null)
        {
            var dash = _mainVm.Dashboard;

            if (devices == null)
            {
                var ud = SettingsManager.UserDevices;
                if (ud != null)
                {
                    lock (ud.SyncRoot)
                        devices = ud.Items.ToArray();
                }
            }

            foreach (var slot in dash.SlotSummaries)
            {
                int padIndex = slot.PadIndex;
                if (padIndex < 0 || padIndex >= _mainVm.Pads.Count) continue;

                var padVm = _mainVm.Pads[padIndex];

                slot.IsActive = padVm.IsDeviceOnline;
                slot.DeviceName = padVm.MappedDeviceName;

                var slotSettings = SettingsManager.UserSettings?.FindByPadIndex(padIndex);
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
                slot.IsVirtualControllerConnected = _inputManager?.IsVirtualControllerConnected(padIndex) ?? false;
                slot.IsInitializing = _inputManager?.IsVirtualControllerInitializing(padIndex) ?? false;
                slot.IsEnabled = SettingsManager.SlotEnabled[padIndex];
                slot.StatusText = !SettingsManager.SlotEnabled[padIndex] ? "Disabled"
                    : slot.IsInitializing ? "Initializing"
                    : mappedCount == 0 ? "No mapping"
                    : padVm.IsDeviceOnline ? "Active"
                    : "Idle";
            }

            int xboxCount = 0, ds4Count = 0, vjoyCount = 0, midiCount = 0, globalCount = 0;
            foreach (var slot in dash.SlotSummaries)
            {
                globalCount++;
                slot.SlotNumber = globalCount;

                var padVm = _mainVm.Pads[slot.PadIndex];
                padVm.SlotNumber = globalCount;
                slot.OutputType = padVm.OutputType;

                switch (padVm.OutputType)
                {
                    case VirtualControllerType.DualShock4:
                        ds4Count++;
                        slot.TypeInstanceLabel = ds4Count.ToString();
                        break;
                    case VirtualControllerType.VJoy:
                        vjoyCount++;
                        slot.TypeInstanceLabel = vjoyCount.ToString();
                        break;
                    case VirtualControllerType.Midi:
                        midiCount++;
                        slot.TypeInstanceLabel = midiCount.ToString();
                        break;
                    default:
                        xboxCount++;
                        slot.TypeInstanceLabel = xboxCount.ToString();
                        break;
                }
            }
        }

        /// <summary>
        /// Updates NavControllerItem connected device counts for sidebar power icon colors.
        /// Safe to call with or without the engine running.
        /// </summary>
        private void RefreshNavItemConnectedCounts(IEnumerable<UserDevice> devices = null)
        {
            if (devices == null)
            {
                var ud = SettingsManager.UserDevices;
                if (ud != null)
                {
                    lock (ud.SyncRoot)
                        devices = ud.Items.ToArray();
                }
            }

            foreach (var nav in _mainVm.NavControllerItems)
            {
                int padIndex = nav.PadIndex;
                if (padIndex < 0 || padIndex >= _mainVm.Pads.Count) continue;

                var slotSettings = SettingsManager.UserSettings?.FindByPadIndex(padIndex);
                int connCount = 0;
                if (slotSettings != null && devices != null)
                {
                    foreach (var us in slotSettings)
                    {
                        if (devices.Any(d => d.InstanceGuid == us.InstanceGuid && d.IsOnline))
                            connCount++;
                    }
                }
                nav.ConnectedDeviceCount = connCount;
                nav.IsInitializing = _inputManager?.IsVirtualControllerInitializing(padIndex) ?? false;
            }
        }

        // ─────────────────────────────────────────────
        //  Devices page raw state
        // ─────────────────────────────────────────────

        /// <summary>
        /// Handles Devices page SelectedDevice changes.
        /// When the engine is off, populates the detail panel structure
        /// from cached UserDevice capabilities so the layout is visible.
        /// </summary>
        private void OnDevicesVmPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ViewModels.DevicesViewModel.SelectedDevice))
                return;

            // When engine is running, UpdateDevicesRawState handles everything.
            if (_inputManager != null && _inputManager.IsRunning)
                return;

            var devVm = _mainVm.Devices;
            var selected = devVm.SelectedDevice;
            if (selected == null)
            {
                devVm.ClearRawState();
                return;
            }

            // Find the UserDevice to get cached capabilities.
            UserDevice ud = FindUserDevice(selected.InstanceGuid);
            if (ud == null)
            {
                devVm.HasRawData = false;
                return;
            }

            // Build the structural layout from cached capabilities.
            if (selected.InstanceGuid != devVm.LastRawStateDeviceGuid)
            {
                devVm.LastRawStateDeviceGuid = selected.InstanceGuid;
                int axisCount = Math.Min(ud.CapAxeCount, CustomInputState.MaxAxis);
                int btnCount = Math.Min(
                    ud.RawButtonCount > 0 ? ud.RawButtonCount : ud.CapButtonCount,
                    CustomInputState.MaxButtons);
                int povCount = Math.Min(ud.CapPovCount, CustomInputState.MaxPovs);
                bool isKb = ud.CapType == InputDeviceType.Keyboard;
                devVm.RebuildRawStateCollections(axisCount, btnCount, povCount, isKb);
                devVm.HasGyroData = ud.HasGyro;
                devVm.HasAccelData = ud.HasAccel;
            }

            devVm.HasRawData = true;
        }

        /// <summary>
        /// Updates the raw input state display for the selected device
        /// on the Devices page using structured observable collections.
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
                devVm.HasRawData = false;
                return;
            }

            devVm.HasRawData = true;
            var state = ud.InputState;

            // Rebuild collections when the selected device changes.
            if (selected.InstanceGuid != devVm.LastRawStateDeviceGuid)
            {
                devVm.LastRawStateDeviceGuid = selected.InstanceGuid;
                int axisCount = Math.Min(ud.CapAxeCount, CustomInputState.MaxAxis);
                int btnCount = Math.Min(
                    ud.RawButtonCount > 0 ? ud.RawButtonCount : ud.CapButtonCount,
                    CustomInputState.MaxButtons);
                int povCount = Math.Min(ud.CapPovCount, CustomInputState.MaxPovs);
                bool isKb = ud.CapType == InputDeviceType.Keyboard;
                devVm.RebuildRawStateCollections(axisCount, btnCount, povCount, isKb);
                devVm.HasGyroData = ud.HasGyro;
                devVm.HasAccelData = ud.HasAccel;
            }

            // Update axis values in-place (no allocation).
            for (int i = 0; i < devVm.RawAxes.Count; i++)
            {
                var item = devVm.RawAxes[i];
                item.RawValue = state.Axis[i];
                item.NormalizedValue = state.Axis[i] / 65535.0;
            }

            // Update button states in-place.
            if (devVm.IsKeyboardDevice)
            {
                // Map keyboard layout keys to their VKey button indices.
                for (int i = 0; i < devVm.KeyboardKeys.Count; i++)
                {
                    int vk = devVm.KeyboardKeys[i].VKeyIndex;
                    devVm.KeyboardKeys[i].IsPressed = KeyboardKeyItem.IsVKeyPressed(state.Buttons, vk);
                }
            }
            else
            {
                for (int i = 0; i < devVm.RawButtons.Count; i++)
                    devVm.RawButtons[i].IsPressed = state.Buttons[i];
            }

            // Update POV hat values in-place.
            for (int i = 0; i < devVm.RawPovs.Count; i++)
                devVm.RawPovs[i].Centidegrees = state.Povs[i];

            // Update gyro/accel values.
            if (ud.HasGyro)
            {
                devVm.GyroX = state.Gyro[0];
                devVm.GyroY = state.Gyro[1];
                devVm.GyroZ = state.Gyro[2];
            }
            if (ud.HasAccel)
            {
                devVm.AccelX = state.Accel[0];
                devVm.AccelY = state.Accel[1];
                devVm.AccelZ = state.Accel[2];
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

                // Sync output type and vJoy config to engine (always, even when no device is selected).
                if (_inputManager != null && i < InputManager.MaxPads)
                {
                    _inputManager.SlotControllerTypes[i] = padVm.OutputType;
                    SyncVJoyConfigToSlot(i, padVm);
                    _inputManager._midiConfigs[i] = padVm.MidiConfig;
                }

                var selected = padVm.SelectedMappedDevice;
                if (selected == null || selected.InstanceGuid == Guid.Empty)
                    continue;

                SaveViewModelToPadSetting(padVm, selected.InstanceGuid);
            }
        }

        /// <summary>
        /// Syncs a PadViewModel's VJoyConfig to the InputManager's per-slot config array.
        /// </summary>
        private void SyncVJoyConfigToSlot(int slotIndex, PadViewModel padVm)
        {
            if (_inputManager == null || slotIndex >= InputManager.MaxPads) return;
            var cfg = padVm.VJoyConfig;
            _inputManager.SlotVJoyConfigs[slotIndex] = new VJoyVirtualController.VJoyDeviceConfig
            {
                Axes = cfg.TotalAxes,
                Buttons = cfg.ButtonCount,
                Povs = cfg.PovCount,
                Sticks = cfg.ThumbstickCount,
                Triggers = cfg.TriggerCount
            };
            _inputManager.SlotVJoyIsCustom[slotIndex] =
                padVm.OutputType == VirtualControllerType.VJoy && !cfg.IsGamepadPreset;
        }

        /// <summary>
        /// Saves the current PadViewModel state to a specific device's PadSetting.
        /// </summary>
        private static void SaveViewModelToPadSetting(PadViewModel padVm, Guid instanceGuid)
        {
            var us = SettingsManager.FindSettingByInstanceGuidAndSlot(instanceGuid, padVm.PadIndex);
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

            // Center offsets.
            ps.LeftThumbCenterOffsetX = padVm.LeftCenterOffsetX.ToString();
            ps.LeftThumbCenterOffsetY = padVm.LeftCenterOffsetY.ToString();
            ps.RightThumbCenterOffsetX = padVm.RightCenterOffsetX.ToString();
            ps.RightThumbCenterOffsetY = padVm.RightCenterOffsetY.ToString();

            // Max range.
            ps.LeftThumbMaxRangeX = padVm.LeftMaxRangeX.ToString();
            ps.LeftThumbMaxRangeY = padVm.LeftMaxRangeY.ToString();
            ps.RightThumbMaxRangeX = padVm.RightMaxRangeX.ToString();
            ps.RightThumbMaxRangeY = padVm.RightMaxRangeY.ToString();

            // Trigger dead zones.
            ps.LeftTriggerDeadZone = padVm.LeftTriggerDeadZone.ToString();
            ps.RightTriggerDeadZone = padVm.RightTriggerDeadZone.ToString();
            ps.LeftTriggerAntiDeadZone = padVm.LeftTriggerAntiDeadZone.ToString();
            ps.RightTriggerAntiDeadZone = padVm.RightTriggerAntiDeadZone.ToString();
            ps.LeftTriggerMaxRange = padVm.LeftTriggerMaxRange.ToString();
            ps.RightTriggerMaxRange = padVm.RightTriggerMaxRange.ToString();

            // Force feedback.
            ps.ForceOverall = padVm.ForceOverallGain.ToString();
            ps.LeftMotorStrength = padVm.LeftMotorStrength.ToString();
            ps.RightMotorStrength = padVm.RightMotorStrength.ToString();
            ps.ForceSwapMotor = padVm.SwapMotors ? "1" : "0";

            // Mapping descriptors.
            foreach (var mapping in padVm.Mappings)
            {
                string target = mapping.TargetSettingName;
                if (target.StartsWith("VJoy", StringComparison.Ordinal))
                {
                    ps.SetVJoyMapping(target, mapping.SourceDescriptor ?? string.Empty);
                }
                else if (target.StartsWith("Midi", StringComparison.Ordinal))
                {
                    ps.SetMidiMapping(target, mapping.SourceDescriptor ?? string.Empty);
                }
                else
                {
                    var prop = typeof(PadSetting).GetProperty(target);
                    if (prop != null && prop.PropertyType == typeof(string) && prop.CanWrite)
                        prop.SetValue(ps, mapping.SourceDescriptor ?? string.Empty);
                }
            }
        }

        /// <summary>
        /// Loads a specific device's PadSetting into the PadViewModel.
        /// </summary>
        private static void LoadPadSettingToViewModel(PadViewModel padVm, Guid instanceGuid)
        {
            var us = SettingsManager.FindSettingByInstanceGuidAndSlot(instanceGuid, padVm.PadIndex);
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

            // Max range.
            padVm.LeftMaxRangeX = TryParseInt(ps.LeftThumbMaxRangeX, 100);
            padVm.LeftMaxRangeY = TryParseInt(ps.LeftThumbMaxRangeY, 100);
            padVm.RightMaxRangeX = TryParseInt(ps.RightThumbMaxRangeX, 100);
            padVm.RightMaxRangeY = TryParseInt(ps.RightThumbMaxRangeY, 100);

            // Center offsets.
            padVm.LeftCenterOffsetX = TryParseInt(ps.LeftThumbCenterOffsetX, 0);
            padVm.LeftCenterOffsetY = TryParseInt(ps.LeftThumbCenterOffsetY, 0);
            padVm.RightCenterOffsetX = TryParseInt(ps.RightThumbCenterOffsetX, 0);
            padVm.RightCenterOffsetY = TryParseInt(ps.RightThumbCenterOffsetY, 0);

            // Trigger dead zones.
            padVm.LeftTriggerDeadZone = TryParseInt(ps.LeftTriggerDeadZone, 0);
            padVm.RightTriggerDeadZone = TryParseInt(ps.RightTriggerDeadZone, 0);
            padVm.LeftTriggerAntiDeadZone = TryParseInt(ps.LeftTriggerAntiDeadZone, 0);
            padVm.RightTriggerAntiDeadZone = TryParseInt(ps.RightTriggerAntiDeadZone, 0);

            // Trigger max range.
            padVm.LeftTriggerMaxRange = TryParseInt(ps.LeftTriggerMaxRange, 100);
            padVm.RightTriggerMaxRange = TryParseInt(ps.RightTriggerMaxRange, 100);

            // Force feedback.
            padVm.ForceOverallGain = TryParseInt(ps.ForceOverall, 100);
            padVm.LeftMotorStrength = TryParseInt(ps.LeftMotorStrength, 100);
            padVm.RightMotorStrength = TryParseInt(ps.RightMotorStrength, 100);
            padVm.SwapMotors = ps.ForceSwapMotor == "1" ||
                (ps.ForceSwapMotor ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);

            // Sync dynamic stick/trigger config items.
            padVm.SyncAllConfigItemsFromVm();

            // Mapping descriptors.
            var ud = FindUserDevice(instanceGuid);
            foreach (var mapping in padVm.Mappings)
            {
                string target = mapping.TargetSettingName;
                string value = GetMappingValue(ps, target);
                mapping.LoadDescriptor(value);
                ResolveDisplayText(mapping, ud);

                if (mapping.NegSettingName != null)
                {
                    string negTarget = mapping.NegSettingName;
                    string negValue = GetMappingValue(ps, negTarget);
                    mapping.LoadNegDescriptor(negValue);
                    ResolveNegDisplayText(mapping, ud);
                }
            }
        }

        private static string GetMappingValue(PadSetting ps, string key)
        {
            if (key.StartsWith("VJoy", StringComparison.Ordinal))
                return ps.GetVJoyMapping(key);
            if (key.StartsWith("Midi", StringComparison.Ordinal))
                return ps.GetMidiMapping(key);
            var prop = typeof(PadSetting).GetProperty(key);
            return (prop != null && prop.PropertyType == typeof(string))
                ? prop.GetValue(ps) as string ?? string.Empty
                : string.Empty;
        }

        private static int TryParseInt(string value, int defaultValue)
        {
            if (string.IsNullOrEmpty(value)) return defaultValue;
            return int.TryParse(value, out int result) ? result : defaultValue;
        }

        /// <summary>
        /// Resolves a mapping descriptor to a human-friendly display name using
        /// the device's object metadata. For keyboards, "Button 65" becomes "A".
        /// For mice, "Button 0" becomes "Left Click".
        /// </summary>
        /// <summary>
        /// Resolves a mapping descriptor to a human-friendly display name using
        /// the device identified by the given instance GUID.
        /// </summary>
        internal static void ResolveDisplayText(MappingItem mapping, Guid instanceGuid)
        {
            ResolveDisplayText(mapping, FindUserDevice(instanceGuid));
        }

        private static void ResolveDisplayText(MappingItem mapping, UserDevice ud)
        {
            if (mapping == null || string.IsNullOrEmpty(mapping.SourceDescriptor))
                return;

            var objects = ud?.DeviceObjects;
            if (objects == null || objects.Length == 0)
                return;

            // Parse the descriptor to get type + index.
            string s = mapping.SourceDescriptor;
            // Strip I/H prefixes for parsing.
            string prefix = "";
            if (s.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
            { prefix = s.Substring(0, 2); s = s.Substring(2); }
            else if (s.StartsWith("I", StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
            { prefix = s.Substring(0, 1); s = s.Substring(1); }
            else if (s.StartsWith("H", StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
            { prefix = s.Substring(0, 1); s = s.Substring(1); }

            string[] parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out int index))
                return;

            string typeName = parts[0].ToLowerInvariant();

            // Find the matching DeviceObjectItem.
            for (int i = 0; i < objects.Length; i++)
            {
                var obj = objects[i];
                if (obj.InputIndex != index)
                    continue;

                bool match = typeName switch
                {
                    "button" => obj.IsButton,
                    "axis" => obj.IsAxis && !obj.IsSlider,
                    "slider" => obj.IsSlider,
                    "pov" => obj.IsPov,
                    _ => false
                };

                if (match && !string.IsNullOrEmpty(obj.Name))
                {
                    // Build display text: e.g. "A" or "Inv. Left Stick X"
                    string display = obj.Name;

                    // For POV descriptors with a direction suffix (e.g., "POV 0 Up"),
                    // replace generic name like "Hat Switch" with "Hat Up".
                    if (typeName == "pov" && parts.Length >= 3)
                    {
                        string dir = parts[2] switch
                        {
                            "UpRight" => "Up-Right",
                            "DownRight" => "Down-Right",
                            "DownLeft" => "Down-Left",
                            "UpLeft" => "Up-Left",
                            _ => parts[2]
                        };
                        display = $"Hat {dir}";
                    }

                    if (!string.IsNullOrEmpty(prefix))
                    {
                        string prefixLabel = prefix.ToUpperInvariant() switch
                        {
                            "I" => "Inv.",
                            "H" => "Half",
                            "IH" => "Inv. Half",
                            _ => ""
                        };
                        if (!string.IsNullOrEmpty(prefixLabel))
                            display = $"{prefixLabel} {display}";
                    }
                    mapping.SetResolvedSourceText(display);
                    return;
                }
            }
        }

        /// <summary>
        /// Resolves the negative-direction descriptor to a human-friendly display name.
        /// </summary>
        internal static void ResolveNegDisplayText(MappingItem mapping, Guid instanceGuid)
        {
            ResolveNegDisplayText(mapping, FindUserDevice(instanceGuid));
        }

        private static void ResolveNegDisplayText(MappingItem mapping, UserDevice ud)
        {
            if (mapping == null || string.IsNullOrEmpty(mapping.NegSourceDescriptor))
                return;

            var objects = ud?.DeviceObjects;
            if (objects == null || objects.Length == 0)
                return;

            string resolved = ResolveDescriptorText(mapping.NegSourceDescriptor, objects);
            if (resolved != null)
                mapping.SetResolvedNegText(resolved);
        }

        /// <summary>
        /// Resolves a descriptor string to a human-readable name using device object metadata.
        /// Returns null if no match found.
        /// </summary>
        private static string ResolveDescriptorText(string descriptor, DeviceObjectItem[] objects)
        {
            string s = descriptor;
            string prefix = "";
            if (s.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
            { prefix = s.Substring(0, 2); s = s.Substring(2); }
            else if (s.StartsWith("I", StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
            { prefix = s.Substring(0, 1); s = s.Substring(1); }
            else if (s.StartsWith("H", StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
            { prefix = s.Substring(0, 1); s = s.Substring(1); }

            string[] parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out int index))
                return null;

            string typeName = parts[0].ToLowerInvariant();

            for (int i = 0; i < objects.Length; i++)
            {
                var obj = objects[i];
                if (obj.InputIndex != index)
                    continue;

                bool match = typeName switch
                {
                    "button" => obj.IsButton,
                    "axis" => obj.IsAxis && !obj.IsSlider,
                    "slider" => obj.IsSlider,
                    "pov" => obj.IsPov,
                    _ => false
                };

                if (match && !string.IsNullOrEmpty(obj.Name))
                {
                    string display = obj.Name;

                    if (typeName == "pov" && parts.Length >= 3)
                    {
                        string dir = parts[2] switch
                        {
                            "UpRight" => "Up-Right",
                            "DownRight" => "Down-Right",
                            "DownLeft" => "Down-Left",
                            "UpLeft" => "Up-Left",
                            _ => parts[2]
                        };
                        display = $"Hat {dir}";
                    }

                    if (!string.IsNullOrEmpty(prefix))
                    {
                        string prefixLabel = prefix.ToUpperInvariant() switch
                        {
                            "I" => "Inv.",
                            "H" => "Half",
                            "IH" => "Inv. Half",
                            _ => ""
                        };
                        if (!string.IsNullOrEmpty(prefixLabel))
                            display = $"{prefixLabel} {display}";
                    }
                    return display;
                }
            }
            return null;
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

            var us = SettingsManager.FindSettingByInstanceGuidAndSlot(selected.InstanceGuid, padIndex);
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

            var us = SettingsManager.FindSettingByInstanceGuidAndSlot(selected.InstanceGuid, padIndex);
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

            Guid newGuid = newDevice?.InstanceGuid ?? Guid.Empty;

            // Save ViewModel state to the PREVIOUSLY selected device's PadSetting,
            // but only when switching to a DIFFERENT device. When the same device is
            // re-added to the slot (remove + re-add), saving would overwrite the
            // freshly created automap PadSetting with stale empty ViewModel state.
            if (_previousSelectedDevice.TryGetValue(padVm.PadIndex, out Guid previousGuid)
                && previousGuid != Guid.Empty
                && previousGuid != newGuid)
            {
                SaveViewModelToPadSetting(padVm, previousGuid);
            }

            // Load the new device's PadSetting into the ViewModel.
            if (newGuid != Guid.Empty)
            {
                LoadPadSettingToViewModel(padVm, newGuid);
                _previousSelectedDevice[padVm.PadIndex] = newGuid;
            }
        }

        /// <summary>
        /// Called when a pad's mappings are rebuilt (e.g., OutputType or vJoy preset changed).
        /// Reloads mapping descriptors from the PadSetting so auto-mapped inputs are preserved.
        /// Does NOT reload dead zone / force feedback settings — those are intentionally reset
        /// by PadViewModel.ResetDeadZoneSettings() when the OutputType or vJoy preset changes.
        /// </summary>
        private void OnMappingsRebuilt(object sender, EventArgs e)
        {
            if (sender is PadViewModel padVm && padVm.SelectedMappedDevice != null
                && padVm.SelectedMappedDevice.InstanceGuid != Guid.Empty)
            {
                LoadMappingDescriptorsOnly(padVm, padVm.SelectedMappedDevice.InstanceGuid);
            }
        }

        /// <summary>
        /// Loads only mapping descriptors from a device's PadSetting into the ViewModel.
        /// Unlike <see cref="LoadPadSettingToViewModel"/>, this does NOT touch dead zone,
        /// force feedback, or other tuning properties — only mapping source descriptors.
        /// </summary>
        private static void LoadMappingDescriptorsOnly(PadViewModel padVm, Guid instanceGuid)
        {
            var us = SettingsManager.FindSettingByInstanceGuidAndSlot(instanceGuid, padVm.PadIndex);
            if (us == null) return;

            var ps = us.GetPadSetting();
            if (ps == null) return;

            var ud = FindUserDevice(instanceGuid);
            foreach (var mapping in padVm.Mappings)
            {
                string target = mapping.TargetSettingName;
                string value = GetMappingValue(ps, target);
                mapping.LoadDescriptor(value);
                ResolveDisplayText(mapping, ud);

                if (mapping.NegSettingName != null)
                {
                    string negTarget = mapping.NegSettingName;
                    string negValue = GetMappingValue(ps, negTarget);
                    mapping.LoadNegDescriptor(negValue);
                    ResolveNegDisplayText(mapping, ud);
                }
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

                // Re-apply device hiding so newly-connected devices get blacklisted
                // and their instance IDs get cached for future sessions.
                ApplyDeviceHiding();
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
            else if (e.PropertyName == nameof(SettingsViewModel.EnableInputHiding))
            {
                if (_mainVm.Settings.EnableInputHiding)
                    ApplyDeviceHiding();
                else
                    RemoveDeviceHiding();
            }
        }

        private void OnDashboardPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DashboardViewModel.EnableDsuMotionServer))
            {
                if (_mainVm.Dashboard.EnableDsuMotionServer)
                    StartDsuServerIfEnabled();
                else
                    StopDsuServer();
            }
            else if (e.PropertyName == nameof(DashboardViewModel.DsuMotionServerPort))
            {
                if (_mainVm.Dashboard.EnableDsuMotionServer)
                {
                    StopDsuServer();
                    StartDsuServerIfEnabled();
                }
            }
            else if (e.PropertyName == nameof(DashboardViewModel.EnableWebController))
            {
                if (_mainVm.Dashboard.EnableWebController)
                    StartWebServerIfEnabled();
                else
                    StopWebServer();
            }
            else if (e.PropertyName == nameof(DashboardViewModel.WebControllerPort))
            {
                if (_mainVm.Dashboard.EnableWebController)
                {
                    StopWebServer();
                    StartWebServerIfEnabled();
                }
            }
        }

        // ─────────────────────────────────────────────
        //  DSU Motion Server lifecycle
        // ─────────────────────────────────────────────

        private void StartDsuServerIfEnabled()
        {
            if (!_mainVm.Dashboard.EnableDsuMotionServer || _inputManager == null)
                return;

            if (_dsuServer != null)
                return; // Already running.

            _dsuServer = new DsuMotionServer();
            _dsuServer.StatusChanged += (_, status) =>
            {
                _dispatcher.BeginInvoke(() => _mainVm.Dashboard.DsuServerStatus = status);
            };

            int port = _mainVm.Dashboard.DsuMotionServerPort;
            if (port < 1024 || port > 65535)
                port = 26760;

            if (_dsuServer.Start(port))
            {
                _inputManager.DsuServer = _dsuServer;
            }
            else
            {
                _dsuServer.Dispose();
                _dsuServer = null;
            }
        }

        private void StopDsuServer()
        {
            if (_dsuServer == null)
                return;

            if (_inputManager != null)
                _inputManager.DsuServer = null;

            _dsuServer.Dispose();
            _dsuServer = null;
        }

        // ─────────────────────────────────────────────
        //  Web Controller Server lifecycle
        // ─────────────────────────────────────────────

        private void StartWebServerIfEnabled()
        {
            if (!_mainVm.Dashboard.EnableWebController || _inputManager == null)
                return;

            if (_webServer != null)
                return; // Already running.

            _webServer = new WebControllerServer();
            _webServer.StatusChanged += (_, status) =>
            {
                _dispatcher.BeginInvoke(() =>
                {
                    _mainVm.Dashboard.WebControllerStatus = status;
                    _mainVm.Dashboard.WebControllerClientCount = _webServer?.ClientCount ?? 0;
                });
            };
            _webServer.DeviceConnected += device =>
            {
                _inputManager.RegisterExternalDevice(device);
            };
            _webServer.DeviceDisconnected += device =>
            {
                _inputManager.UnregisterExternalDevice(device.InstanceGuid);
            };

            int port = _mainVm.Dashboard.WebControllerPort;
            if (port < 1024 || port > 65535)
                port = 8080;

            if (!_webServer.Start(port))
            {
                _webServer.Dispose();
                _webServer = null;
            }
        }

        private void StopWebServer()
        {
            if (_webServer == null)
                return;

            _webServer.Dispose();
            _webServer = null;
        }

        // ─────────────────────────────────────────────
        //  Device hiding (HidHide + input hooks)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Applies device hiding based on per-device toggle settings.
        /// HidHide: Adds devices with HidHideEnabled to the blacklist, whitelists PadForge, activates cloaking.
        /// Hooks: Starts input hook manager for devices with ConsumeInputEnabled.
        /// Only acts if the master switch (EnableInputHiding) is on.
        /// </summary>
        public void ApplyDeviceHiding()
        {
            if (!_mainVm.Settings.EnableInputHiding)
                return;

            var userDevices = SettingsManager.UserDevices?.Items;
            if (userDevices == null) return;

            UserDevice[] snapshot;
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                snapshot = userDevices.ToArray();
            }

            // ── HidHide ──
            bool anyHidHide = false;
            if (HidHideController.IsAvailable())
            {
                // Clear previous PadForge-managed entries before re-adding,
                // so devices that had HidHideEnabled turned off get removed.
                HidHideController.RemoveManagedDevices();

                // Whitelist PadForge itself.
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    HidHideController.EnsureWhitelisted(exePath);

                // Add devices with HidHideEnabled to the blacklist.
                bool cacheUpdated = false;
                foreach (var ud in snapshot)
                {
                    if (ud.HidHideEnabled && !string.IsNullOrEmpty(ud.DevicePath))
                    {
                        string instanceId = HidHideController.DevicePathToInstanceId(ud.DevicePath);

                        // If the DevicePath produced a valid HID instance ID, use it directly.
                        if (instanceId != null && instanceId.Contains("VID_", StringComparison.OrdinalIgnoreCase))
                        {
                            HidHideController.AddToBlacklist(instanceId);
                            anyHidHide = true;
                        }
                        // Fallback: synthetic paths (e.g., "XInput#0") — look up by VID/PID.
                        else if (ud.VendorId > 0 && ud.ProdId > 0)
                        {
                            var realIds = HidHideController.FindInstanceIdsByVidPid(
                                (ushort)ud.VendorId, (ushort)ud.ProdId);

                            if (realIds.Count > 0)
                            {
                                // Device is present — cache the resolved IDs for future use.
                                if (!new HashSet<string>(ud.HidHideInstanceIds, StringComparer.OrdinalIgnoreCase)
                                        .SetEquals(realIds))
                                {
                                    ud.HidHideInstanceIds = realIds;
                                    cacheUpdated = true;
                                }
                                foreach (var realId in realIds)
                                {
                                    HidHideController.AddToBlacklist(realId);
                                    anyHidHide = true;
                                }
                            }
                            else if (ud.HidHideInstanceIds.Count > 0)
                            {
                                // Device is offline — use cached IDs to pre-emptively blacklist.
                                System.Diagnostics.Debug.WriteLine(
                                    $"[ApplyDeviceHiding] Using {ud.HidHideInstanceIds.Count} cached instance IDs for offline device {ud.ResolvedName}");
                                foreach (var cachedId in ud.HidHideInstanceIds)
                                {
                                    HidHideController.AddToBlacklist(cachedId);
                                    anyHidHide = true;
                                }
                            }
                        }
                    }
                }

                // Persist updated cache to settings.
                if (cacheUpdated)
                    _settingsService?.MarkDirty();

                if (anyHidHide)
                    HidHideController.SetActive(true);
            }

            // ── Input hooks ──
            var suppressedKeys = new HashSet<int>();
            var suppressedMouse = new HashSet<int>();

            foreach (var ud in snapshot)
            {
                if (!ud.ConsumeInputEnabled) continue;
                if (!HasAnySlotAssignment(ud.InstanceGuid)) continue;

                // Collect all mapped virtual key codes / mouse buttons from this device's mappings.
                CollectSuppressedInputs(ud, suppressedKeys, suppressedMouse);
            }

            System.Diagnostics.Debug.WriteLine(
                $"[ApplyDeviceHiding] suppressedKeys={string.Join(",", suppressedKeys)} " +
                $"suppressedMouse={string.Join(",", suppressedMouse)}");

            if (suppressedKeys.Count > 0 || suppressedMouse.Count > 0)
            {
                if (_hookManager == null)
                {
                    _hookManager = new InputHookManager();
                    _hookManager.Start();
                }
                _hookManager.SetSuppressedKeys(suppressedKeys);
                _hookManager.SetSuppressedMouseButtons(suppressedMouse);
            }
            else
            {
                // No inputs to suppress — stop hooks if running.
                if (_hookManager != null)
                {
                    _hookManager.Stop();
                    _hookManager.Dispose();
                    _hookManager = null;
                }
            }
        }

        /// <summary>
        /// Removes all device hiding: clears PadForge-managed HidHide blacklist entries
        /// and stops input hooks.
        /// </summary>
        public void RemoveDeviceHiding()
        {
            // ── HidHide ──
            try
            {
                if (HidHideController.IsAvailable())
                    HidHideController.RemoveManagedDevices();
            }
            catch { /* Best effort — driver may not be available */ }

            // ── Input hooks ──
            if (_hookManager != null)
            {
                _hookManager.Stop();
                _hookManager.Dispose();
                _hookManager = null;
            }
        }

        /// <summary>
        /// Checks whether a device is assigned to any virtual controller slot.
        /// </summary>
        private static bool HasAnySlotAssignment(Guid instanceGuid)
        {
            var slots = SettingsManager.GetAssignedSlots(instanceGuid);
            return slots != null && slots.Count > 0;
        }

        /// <summary>
        /// Collects the virtual key codes and mouse button IDs that should be
        /// suppressed based on the device's active mappings across all assigned slots.
        /// Parses "Button {index}" descriptors from PadSetting properties.
        /// </summary>
        private static void CollectSuppressedInputs(UserDevice ud, HashSet<int> keys, HashSet<int> mouseButtons)
        {
            var assignedSlots = SettingsManager.GetAssignedSlots(ud.InstanceGuid);
            if (assignedSlots == null) return;

            foreach (int slotIndex in assignedSlots)
            {
                // Find the UserSetting for this device + slot.
                var us = SettingsManager.FindSettingByInstanceGuidAndSlot(ud.InstanceGuid, slotIndex);
                if (us == null) continue;

                var ps = us.GetPadSetting();
                if (ps == null) continue;

                foreach (string descriptor in ps.GetAllMappingDescriptors())
                {
                    // Parse "Button {index}" descriptors.
                    if (descriptor.StartsWith("Button ", StringComparison.Ordinal) &&
                        int.TryParse(descriptor.AsSpan(7), out int buttonIndex))
                    {
                        if (ud.IsKeyboard)
                            keys.Add(buttonIndex); // buttonIndex is the VKey code
                        else if (ud.IsMouse)
                            mouseButtons.Add(buttonIndex); // buttonIndex is 0=L, 1=R, 2=M, etc.
                    }
                }
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

            // Sort: alphabetically by name, then by VID:PID.
            var sorted = devVm.Devices.OrderBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
                                      .ThenBy(d => d.VendorId)
                                      .ThenBy(d => d.ProductId)
                                      .ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                int current = devVm.Devices.IndexOf(sorted[i]);
                if (current != i)
                    devVm.Devices.Move(current, i);
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

            // Resolve the HID instance path for display.
            // Individual devices have real HID paths; merged devices (aggregate://) do not.
            string instancePath = null;
            if (!string.IsNullOrEmpty(ud.DevicePath) && !ud.DevicePath.StartsWith("aggregate://"))
                instancePath = HidHideController.DevicePathToInstanceId(ud.DevicePath);

            if (!string.IsNullOrEmpty(instancePath) &&
                instancePath.Contains("VID_", StringComparison.OrdinalIgnoreCase))
                row.HidHideInstancePath = instancePath;
            else if (ud.HidHideInstanceIds.Count > 0)
                row.HidHideInstancePath = ud.HidHideInstanceIds[0];
            else if (ud.VendorId > 0 && ud.ProdId > 0)
            {
                // XInput devices have synthetic paths (e.g. "XInput#0") that can't be
                // resolved directly. Look up the real HID instance path by VID/PID.
                var realIds = HidHideController.FindInstanceIdsByVidPid(
                    (ushort)ud.VendorId, (ushort)ud.ProdId);
                row.HidHideInstancePath = realIds.Count > 0 ? realIds[0] : string.Empty;
            }
            else
                row.HidHideInstancePath = string.Empty;

            // Input hiding toggle state.
            row.HidHideEnabled = ud.HidHideEnabled;
            row.ConsumeInputEnabled = ud.ConsumeInputEnabled;
            row.ForceRawJoystickMode = ud.ForceRawJoystickMode;
            row.IsHidHideAvailable = _mainVm.Settings.IsHidHideInstalled;

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

            // Resolve slot assignments (device can be assigned to multiple slots).
            row.SetAssignedSlots(SettingsManager.GetAssignedSlots(ud.InstanceGuid));
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

                    // Sort alphabetically by name before syncing.
                    deviceInfos.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                    // Remember the previously selected device GUID before sync
                    // (sync may overwrite the same object in-place).
                    Guid prevSelectedGuid = padVm.SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;

                    // Sync the ObservableCollection (minimize UI churn).
                    SyncMappedDevices(padVm.MappedDevices, deviceInfos);

                    // Auto-select first device if nothing is selected.
                    if (padVm.SelectedMappedDevice == null && padVm.MappedDevices.Count > 0)
                    {
                        padVm.SelectedMappedDevice = padVm.MappedDevices[0];
                    }

                    // If the selected item was overwritten in-place (e.g. a device was
                    // deleted and the next device slid into index 0), reload the correct
                    // PadSetting so stale mappings don't bleed into another device.
                    if (padVm.SelectedMappedDevice != null
                        && prevSelectedGuid != Guid.Empty
                        && padVm.SelectedMappedDevice.InstanceGuid != prevSelectedGuid)
                    {
                        LoadPadSettingToViewModel(padVm, padVm.SelectedMappedDevice.InstanceGuid);
                        _previousSelectedDevice[i] = padVm.SelectedMappedDevice.InstanceGuid;
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

            // Refresh sidebar and dashboard to reflect which slots are created.
            _mainVm.RefreshNavControllerItems();

            // Build the list of created slot indices for the dashboard.
            var activeSlots = new List<int>();
            int xboxCount = 0, ds4Count = 0, vjoyCount = 0, midiCount = 0;
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                if (SettingsManager.SlotCreated[i])
                {
                    activeSlots.Add(i);
                    switch (_mainVm.Pads[i].OutputType)
                    {
                        case VirtualControllerType.Xbox360: xboxCount++; break;
                        case VirtualControllerType.DualShock4: ds4Count++; break;
                        case VirtualControllerType.VJoy: vjoyCount++; break;
                        case VirtualControllerType.Midi: midiCount++; break;
                    }
                }
            }
            bool canAddMore = xboxCount < SettingsManager.MaxXbox360Slots
                           || ds4Count < SettingsManager.MaxDS4Slots
                           || vjoyCount < SettingsManager.MaxVJoySlots
                           || midiCount < SettingsManager.MaxMidiSlots;
            _mainVm.Dashboard.RefreshActiveSlots(activeSlots, canAddMore);

            // Update slot summary properties so dashboard cards reflect current state
            // even when the engine (and its UI timer) is not running.
            RefreshSlotSummaryProperties();

            // Update the active profile's topology label so the Profiles page
            // reflects slot create/delete changes in real-time.
            RefreshActiveProfileTopologyLabel();
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
        /// While recording, CombinedOutputState button flags are OR'd together
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
            _recordedCustomButtons = new uint[4];
            _recordingDeviceGuid = Guid.Empty;
            _recordedRawButtons = new HashSet<int>();
            macro.RecordingLiveText = "Press buttons...";
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
                _recordingMacro.TriggerButtons = 0;
                _recordingMacro.TriggerCustomButtonWords = new uint[4];
            }
            else if (_recordingMacro.ButtonStyle == MacroButtonStyle.Numbered
                     && _recordedCustomButtons != null && _recordedCustomButtons.Any(w => w != 0))
            {
                // Custom vJoy button path.
                _recordingMacro.TriggerCustomButtonWords = (uint[])_recordedCustomButtons.Clone();
                _recordingMacro.TriggerButtons = 0;
                _recordingMacro.TriggerDeviceGuid = Guid.Empty;
                _recordingMacro.TriggerRawButtons = Array.Empty<int>();
            }
            else
            {
                // Xbox bitmask path (OutputController or fallback).
                _recordingMacro.TriggerButtons = _recordedButtons;
                _recordingMacro.TriggerDeviceGuid = Guid.Empty;
                _recordingMacro.TriggerRawButtons = Array.Empty<int>();
                _recordingMacro.TriggerCustomButtonWords = new uint[4];
            }

            _recordingMacro.RecordingLiveText = "";
            _recordingMacro.IsRecordingTrigger = false;
            _recordingMacro = null;
            _recordedButtons = 0;
            _recordedCustomButtons = null;
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

                // Update live display text with device name.
                if (_recordedRawButtons.Count > 0)
                {
                    string combo = string.Join(" + ", _recordedRawButtons.OrderBy(x => x).Select(b => $"Btn {b}"));
                    string deviceName = ResolveDeviceName(_recordingDeviceGuid);
                    _recordingMacro.RecordingLiveText = string.IsNullOrEmpty(deviceName)
                        ? combo : $"{combo} ({deviceName})";
                }
                else
                    _recordingMacro.RecordingLiveText = "Press buttons...";
            }
            else if (_recordingMacro.ButtonStyle == MacroButtonStyle.Numbered)
            {
                // Custom vJoy: accumulate from the combined raw state.
                var rawState = _inputManager.CombinedVJoyRawStates[_recordingPadIndex];
                if (rawState.Buttons != null && _recordedCustomButtons != null)
                {
                    for (int w = 0; w < rawState.Buttons.Length && w < _recordedCustomButtons.Length; w++)
                        _recordedCustomButtons[w] |= rawState.Buttons[w];
                }

                if (_recordedCustomButtons != null && _recordedCustomButtons.Any(w => w != 0))
                    _recordingMacro.RecordingLiveText = MacroButtonNames.FormatCustomButtons(_recordedCustomButtons);
                else
                    _recordingMacro.RecordingLiveText = "Press buttons...";
            }
            else
            {
                // Gamepad preset OutputController: accumulate from the combined Xbox-mapped state.
                ushort xboxButtons = _inputManager.CombinedOutputStates[_recordingPadIndex].Buttons;
                _recordedButtons |= xboxButtons;

                if (_recordedButtons != 0)
                    _recordingMacro.RecordingLiveText = MacroButtonNames.FormatButtons(
                        _recordedButtons, _recordingMacro.ButtonStyle);
                else
                    _recordingMacro.RecordingLiveText = "Press buttons...";
            }
        }

        /// <summary>Resolves a device GUID to a human-readable name.</summary>
        private static string ResolveDeviceName(Guid deviceGuid)
        {
            if (deviceGuid == Guid.Empty) return null;
            return SettingsManager.FindDeviceByInstanceGuid(deviceGuid)?.ResolvedName;
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
                        ProductGuid = us.ProductGuid,
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
                PadSettings = padSettings.ToArray(),
                SlotCreated = (bool[])SettingsManager.SlotCreated.Clone(),
                SlotEnabled = (bool[])SettingsManager.SlotEnabled.Clone(),
                SlotControllerTypes = Enumerable.Range(0, _mainVm.Pads.Count)
                    .Select(i => (int)_mainVm.Pads[i].OutputType).ToArray(),
                EnableDsuMotionServer = _mainVm.Dashboard.EnableDsuMotionServer,
                DsuMotionServerPort = _mainVm.Dashboard.DsuMotionServerPort,
                EnableWebController = _mainVm.Dashboard.EnableWebController,
                WebControllerPort = _mainVm.Dashboard.WebControllerPort
            };
        }

        /// <summary>
        /// Loads a profile's PadSettings and slot assignments into the runtime state.
        /// For each ProfileEntry, finds the matching UserSetting and swaps its
        /// PadSetting and MapTo slot.
        /// </summary>
        public void ApplyProfile(ProfileData profile)
        {
            if (profile == null)
                return;

            // ── Apply topology (if present in profile) ──
            if (profile.SlotCreated != null)
            {
                for (int i = 0; i < InputManager.MaxPads; i++)
                {
                    bool willCreate = i < profile.SlotCreated.Length && profile.SlotCreated[i];

                    // Unassign devices from slots being destroyed.
                    if (SettingsManager.SlotCreated[i] && !willCreate)
                    {
                        var settings = SettingsManager.UserSettings;
                        if (settings != null)
                        {
                            lock (settings.SyncRoot)
                            {
                                foreach (var us in settings.Items)
                                {
                                    if (us.MapTo == i)
                                        us.MapTo = -1;
                                }
                            }
                        }
                    }

                    // Set OutputType before SlotCreated (same order as DeviceService.CreateSlot).
                    if (profile.SlotControllerTypes != null && i < profile.SlotControllerTypes.Length)
                    {
                        if (Enum.IsDefined(typeof(VirtualControllerType), profile.SlotControllerTypes[i]))
                            _mainVm.Pads[i].OutputType = (VirtualControllerType)profile.SlotControllerTypes[i];
                    }

                    SettingsManager.SlotCreated[i] = willCreate;
                    SettingsManager.SlotEnabled[i] = (profile.SlotEnabled != null && i < profile.SlotEnabled.Length)
                        ? profile.SlotEnabled[i]
                        : willCreate;
                }
            }

            // ── Reset all device assignments, then apply profile entries ──
            // Each profile fully owns slot assignments; unassign everything
            // first so devices not in this profile don't leak from the previous one.
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                foreach (var us in SettingsManager.UserSettings.Items)
                    us.MapTo = -1;

                if (profile.Entries != null && profile.Entries.Length > 0 &&
                    profile.PadSettings != null && profile.PadSettings.Length > 0)
                {
                    foreach (var entry in profile.Entries)
                    {
                        // Find the PadSetting template by checksum first — skip if missing.
                        var template = profile.PadSettings
                            .FirstOrDefault(p => p.PadSettingChecksum == entry.PadSettingChecksum);
                        if (template == null) continue;

                        // Find an UNASSIGNED UserSetting for this device.
                        // A device mapped to multiple slots has multiple profile entries;
                        // each must claim a separate UserSetting (MapTo < 0 = unclaimed).
                        var us = SettingsManager.UserSettings.Items
                            .FirstOrDefault(s => s.InstanceGuid == entry.InstanceGuid && s.MapTo < 0);

                        if (us == null && entry.ProductGuid != Guid.Empty)
                        {
                            us = SettingsManager.UserSettings.Items
                                .FirstOrDefault(s => s.ProductGuid == entry.ProductGuid && s.MapTo < 0);
                        }

                        // No unclaimed UserSetting found — create one for this slot.
                        if (us == null)
                        {
                            us = new UserSetting
                            {
                                InstanceGuid = entry.InstanceGuid,
                                ProductGuid = entry.ProductGuid
                            };
                            SettingsManager.UserSettings.Items.Add(us);
                        }

                        // Clone and apply PadSetting + slot assignment.
                        var ps = template.CloneDeep();
                        us.SetPadSetting(ps);
                        us.MapTo = entry.MapTo;
                    }
                }
            }

            // ── Apply DSU motion server settings ──
            _mainVm.Dashboard.EnableDsuMotionServer = profile.EnableDsuMotionServer;
            if (profile.DsuMotionServerPort >= 1024 && profile.DsuMotionServerPort <= 65535)
                _mainVm.Dashboard.DsuMotionServerPort = profile.DsuMotionServerPort;

            // ── Apply web controller server settings ──
            _mainVm.Dashboard.EnableWebController = profile.EnableWebController;
            if (profile.WebControllerPort >= 1024 && profile.WebControllerPort <= 65535)
                _mainVm.Dashboard.WebControllerPort = profile.WebControllerPort;

            // Rebuild pad device lists based on new MapTo values.
            UpdatePadDeviceInfo();

            // Reload ViewModels with new PadSettings (after device lists are rebuilt).
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                var selected = padVm.SelectedMappedDevice;
                if (selected != null && selected.InstanceGuid != Guid.Empty)
                    LoadPadSettingToViewModel(padVm, selected.InstanceGuid);
            }

            // Refresh Devices page slot labels.
            SyncDevicesList();
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

            // Save outgoing profile state before switching.
            SaveActiveProfileState();

            // Switch to the target profile (or revert to default).
            // Set ActiveProfileId BEFORE ApplyProfile so that
            // RefreshActiveProfileTopologyLabel updates the correct profile.
            if (profileId != null)
            {
                var target = FindProfileById(profileId);
                if (target != null)
                {
                    SettingsManager.ActiveProfileId = profileId;
                    _mainVm.Settings.ActiveProfileInfo = target.Name;
                    ApplyProfile(target);
                    _mainVm.StatusText = $"Profile switched: {target.Name}";
                }
            }
            else
            {
                // Revert to default (root) profile using the startup snapshot.
                SettingsManager.ActiveProfileId = null;
                _mainVm.Settings.ActiveProfileInfo = "Default";
                if (_defaultProfileSnapshot != null)
                    ApplyProfile(_defaultProfileSnapshot);
                _mainVm.StatusText = "Profile switched: Default";
            }
        }

        /// <summary>
        /// Saves the current runtime state into the active profile (or the
        /// default snapshot if no named profile is active).  Call before
        /// switching away from any profile so changes are preserved.
        /// </summary>
        public void SaveActiveProfileState()
        {
            var snapshot = SnapshotCurrentProfile();
            string activeId = SettingsManager.ActiveProfileId;

            if (string.IsNullOrEmpty(activeId))
            {
                // Currently on the default profile — update the default snapshot.
                _defaultProfileSnapshot = snapshot;
            }
            else
            {
                // Currently on a named profile — update its stored data.
                var profile = SettingsManager.Profiles.Find(p => p.Id == activeId);
                if (profile != null)
                {
                    profile.Entries = snapshot.Entries;
                    profile.PadSettings = snapshot.PadSettings;
                    profile.SlotCreated = snapshot.SlotCreated;
                    profile.SlotEnabled = snapshot.SlotEnabled;
                    profile.SlotControllerTypes = snapshot.SlotControllerTypes;
                    profile.EnableDsuMotionServer = snapshot.EnableDsuMotionServer;
                    profile.DsuMotionServerPort = snapshot.DsuMotionServerPort;
                    profile.EnableWebController = snapshot.EnableWebController;
                    profile.WebControllerPort = snapshot.WebControllerPort;
                }
            }
        }

        /// <summary>
        /// Refreshes the default profile snapshot from the current runtime state.
        /// Call after saving when no profile is active so future reverts use the
        /// latest saved state.
        /// </summary>
        public void RefreshDefaultSnapshot()
        {
            _defaultProfileSnapshot = SnapshotCurrentProfile();
        }

        /// <summary>
        /// Applies the default profile snapshot, reverting to the state before
        /// any named profile was loaded.
        /// </summary>
        public void ApplyDefaultProfile()
        {
            if (_defaultProfileSnapshot != null)
                ApplyProfile(_defaultProfileSnapshot);
        }

        /// <summary>
        /// Updates the TopologyLabel on the active profile's list item so the
        /// Profiles page reflects slot create/delete/type changes immediately.
        /// </summary>
        /// <summary>
        /// Public wrapper so callers (e.g. MainWindow) can refresh the profile
        /// topology label after controller type changes.
        /// </summary>
        public void RefreshProfileTopology() => RefreshActiveProfileTopologyLabel();

        /// <summary>
        /// Swaps two virtual controller slots across all layers:
        /// engine arrays, settings, and ViewModel state, then refreshes UI.
        /// Must be called on the UI thread.
        /// </summary>
        /// <summary>
        /// Moves a controller slot from its current visual position to a new one
        /// by performing adjacent bubble swaps through the active slots list.
        /// UI is refreshed once after all swaps complete.
        /// </summary>
        public void SwapSlots(int padIndexA, int padIndexB)
        {
            if (padIndexA == padIndexB) return;
            _inputManager?.SwapSlots(padIndexA, padIndexB);
            SettingsManager.SwapSlots(padIndexA, padIndexB);
            SwapPadViewModelSlotData(padIndexA, padIndexB);
            RefreshAfterSlotReorder();
        }

        public void MoveSlot(int sourcePadIndex, int targetVisualPosition)
        {
            var activeSlots = new List<int>();
            for (int i = 0; i < InputManager.MaxPads; i++)
                if (SettingsManager.SlotCreated[i])
                    activeSlots.Add(i);

            int sourcePos = activeSlots.IndexOf(sourcePadIndex);
            if (sourcePos < 0) return;
            if (targetVisualPosition < 0 || targetVisualPosition >= activeSlots.Count) return;
            if (sourcePos == targetVisualPosition) return;

            // Bubble the source to the target via adjacent swaps (data only).
            int step = targetVisualPosition > sourcePos ? 1 : -1;
            for (int i = sourcePos; i != targetVisualPosition; i += step)
            {
                int a = activeSlots[i], b = activeSlots[i + step];
                _inputManager?.SwapSlots(a, b);
                SettingsManager.SwapSlots(a, b);
                SwapPadViewModelSlotData(a, b);
            }

            RefreshAfterSlotReorder();
        }

        /// <summary>
        /// Re-sorts created slots so types are grouped: Xbox 360, then DS4, then vJoy.
        /// Uses adjacent SwapSlots calls (same-type swaps are zero-flicker).
        /// Returns true if any reordering was performed.
        /// </summary>
        /// <param name="silent">
        /// When true, performs data swaps only without UI refresh.
        /// Use during startup before the window is loaded.
        /// </param>
        public bool EnsureTypeGroupOrder(bool silent = false)
        {
            var activeSlots = new List<int>();
            for (int i = 0; i < InputManager.MaxPads; i++)
                if (SettingsManager.SlotCreated[i])
                    activeSlots.Add(i);

            if (activeSlots.Count <= 1) return false;

            // Check if already in order.
            bool needsReorder = false;
            for (int i = 0; i < activeSlots.Count - 1; i++)
            {
                if (GetTypePriority(_mainVm.Pads[activeSlots[i]].OutputType) >
                    GetTypePriority(_mainVm.Pads[activeSlots[i + 1]].OutputType))
                {
                    needsReorder = true;
                    break;
                }
            }
            if (!needsReorder) return false;

            // Bubble sort by type priority using adjacent swaps.
            // Each swap uses current physical indices and current OutputTypes,
            // so the comparison is always against up-to-date data.
            bool swapped;
            do
            {
                swapped = false;
                for (int i = 0; i < activeSlots.Count - 1; i++)
                {
                    int a = activeSlots[i], b = activeSlots[i + 1];
                    if (GetTypePriority(_mainVm.Pads[a].OutputType) >
                        GetTypePriority(_mainVm.Pads[b].OutputType))
                    {
                        // Data-only swap: don't destroy VCs here.
                        // Step 5 detects the type mismatch on the polling thread
                        // and handles VC recreation naturally — avoids the
                        // all-VCs-destroyed-at-once race that causes phantom controllers.
                        _inputManager?.SwapSlotData(a, b);
                        SettingsManager.SwapSlots(a, b);
                        SwapPadViewModelSlotData(a, b);
                        swapped = true;
                    }
                }
            } while (swapped);

            if (!silent)
                RefreshAfterSlotReorder();
            return true;
        }

        /// <summary>
        /// Swaps ViewModel-side slot data that must travel with the slot during reorder:
        /// OutputType and VJoyConfig. Engine-side arrays are swapped separately by
        /// InputManager.SwapSlotData/SwapSlots.
        /// </summary>
        private void SwapPadViewModelSlotData(int a, int b)
        {
            (_mainVm.Pads[a].OutputType, _mainVm.Pads[b].OutputType) =
                (_mainVm.Pads[b].OutputType, _mainVm.Pads[a].OutputType);

            // Swap the entire VJoySlotConfig objects so the UI shows the correct
            // config after reorder. The setter re-subscribes PropertyChanged.
            (_mainVm.Pads[a].VJoyConfig, _mainVm.Pads[b].VJoyConfig) =
                (_mainVm.Pads[b].VJoyConfig, _mainVm.Pads[a].VJoyConfig);

            (_mainVm.Pads[a].MidiConfig, _mainVm.Pads[b].MidiConfig) =
                (_mainVm.Pads[b].MidiConfig, _mainVm.Pads[a].MidiConfig);
        }

        private static int GetTypePriority(VirtualControllerType type) => type switch
        {
            VirtualControllerType.Xbox360 => 0,
            VirtualControllerType.DualShock4 => 1,
            VirtualControllerType.VJoy => 2,
            VirtualControllerType.Midi => 3,
            _ => 4
        };

        private void RefreshAfterSlotReorder()
        {
            UpdatePadDeviceInfo();

            // Reload PadSettings into ViewModels so deadzones, mappings, etc. follow the device.
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                var selected = padVm.SelectedMappedDevice;
                if (selected != null && selected.InstanceGuid != Guid.Empty)
                    LoadPadSettingToViewModel(padVm, selected.InstanceGuid);
            }

            // Force a full sidebar rebuild — RefreshNavControllerItems() only updates
            // properties in-place (no NavControllerItemsRefreshed event) since slot count
            // doesn't change during a swap/move, but card visuals need a full rebuild.
            _mainVm.ForceNavControllerItemsRefreshed();

            SyncDevicesList();
            RefreshActiveProfileTopologyLabel();
        }

        private void RefreshActiveProfileTopologyLabel()
        {
            string activeId = SettingsManager.ActiveProfileId;
            var slotCreated = SettingsManager.SlotCreated;
            var slotTypes = Enumerable.Range(0, _mainVm.Pads.Count)
                .Select(i => (int)_mainVm.Pads[i].OutputType).ToArray();

            foreach (var item in _mainVm.Settings.ProfileItems)
            {
                if ((string.IsNullOrEmpty(activeId) && item.IsDefault) || item.Id == activeId)
                {
                    SettingsService.UpdateTopologyCounts(item, slotCreated, slotTypes);
                    break;
                }
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
