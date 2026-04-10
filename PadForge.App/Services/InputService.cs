using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
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
        private readonly HashSet<string> _managedWhitelistDosPaths = new(StringComparer.OrdinalIgnoreCase);

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
        private HashSet<MacroAxisTarget> _recordedAxisTargets;
        private Dictionary<MacroAxisTarget, MacroAxisDirection> _recordedAxisDirections;
        private HashSet<string> _recordedPovs; // stored as "povIndex:centidegrees"
        private const float AxisRecordThreshold = 0.25f; // 25% of full range (delta from baseline)
        private float[] _macroAxisBaseline;              // axis values at recording start
        private MacroAxisTarget _macroAxisCandidate;     // axis being held
        private float _macroAxisCandidateDelta;          // delta sign of the candidate axis
        private int _macroAxisHoldCounter;               // hold confirmation cycles
        private const int MacroAxisHoldCycles = 3;       // cycles needed to confirm

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

            // Refresh server status strings when language changes.
            Strings.CultureChanged += OnCultureChanged;

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

            _stopped = false;

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
            // If the app restarted with a named profile active, LoadProfiles
            // already captured the default's state before overwriting with the
            // profile's topology. Use that instead of the current (profile) state.
            if (SettingsManager.PendingDefaultSnapshot != null)
            {
                _defaultProfileSnapshot = SettingsManager.PendingDefaultSnapshot;
            }
            else
            {
                _defaultProfileSnapshot = SnapshotCurrentProfile();
                SettingsManager.PendingDefaultSnapshot = _defaultProfileSnapshot;
            }

            // Start engine background thread.
            _inputManager.Start();

            // Start DSU motion server if enabled.
            StartDsuServerIfEnabled();

            // Start web controller server if enabled.
            StartWebServerIfEnabled();

            // Show touchpad overlay if enabled.
            if (_mainVm.Dashboard.EnableTouchpadOverlay)
                ShowTouchpadOverlay();

            // Start audio bass rumble detector if any slot has it enabled.
            SyncAudioBassDetector();

            // Clear stale HidHide blacklist entries from previous crash/kill.
            // _managedDeviceIds is in-memory so entries are lost on restart,
            // making RemoveManagedDevices() unable to clean up stale entries.
            try
            {
                if (HidHideController.IsAvailable())
                    HidHideController.ClearAll();
            }
            catch { /* best effort */ }
            _managedWhitelistDosPaths.Clear();

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
            _mainVm.StatusText = Strings.Instance.Status_EngineStarted;
            _mainVm.RefreshCommands();

            // Enter idle immediately if no slots are created.
            UpdateIdleState();
        }

        /// <summary>
        /// Stops the UI timer and engine, releases resources.
        /// </summary>
        private bool _stopped;

        public void Stop(bool preserveVJoyNodes = false)
        {
            if (_stopped) return;
            _stopped = true;

            // Stop UI timer.
            if (_uiTimer != null)
            {
                _uiTimer.Stop();
                _uiTimer.Tick -= UiTimer_Tick;
                _uiTimer = null;
            }

            // Unsubscribe from ViewModel property changes.
            _mainVm.Settings.PropertyChanged -= OnSettingsPropertyChanged;
            _mainVm.Dashboard.PropertyChanged -= OnDashboardPropertyChanged;
            _mainVm.Devices.PropertyChanged -= OnDevicesVmPropertyChanged;

            // Unsubscribe from per-pad events.
            foreach (var padVm in _mainVm.Pads)
            {
                padVm.SelectedDeviceChanged -= OnSelectedDeviceChanged;
                padVm.MappingsRebuilt -= OnMappingsRebuilt;
            }

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

            // Close touchpad overlay (not just hide — prevents shutdown hang).
            HideTouchpadOverlay(close: true);

            // Stop audio bass rumble detector.
            StopAudioBassDetector();

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
            _mainVm.Dashboard.EngineStateKey = "Stopped";
            _mainVm.Dashboard.EngineStatus = Strings.Instance.Common_Stopped;
            _mainVm.Dashboard.PollingFrequency = 0;
            _mainVm.Dashboard.OnlineDevices = 0;
            _mainVm.PollingFrequency = 0;
            _mainVm.StatusText = Strings.Instance.Status_EngineStopped;
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

            // ── Feed touchpad overlay state into the virtual device ──
            if (_touchpadOverlay?.IsVisible == true && _touchpadOverlayDevice != null)
                _touchpadOverlayDevice.UpdateState(_touchpadOverlay.GetTouchpadState());

            // ── Handle macro-requested touchpad overlay toggle ──
            if (_inputManager.ToggleTouchpadOverlayRequested)
            {
                _inputManager.ToggleTouchpadOverlayRequested = false;
                ToggleTouchpadOverlay();
            }

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

                // For KBM slots, push the combined KbmRawState.
                if (_inputManager.SlotControllerTypes[i] == VirtualControllerType.KeyboardMouse)
                    padVm.KbmOutputSnapshot = _inputManager.CombinedKbmRawStates[i];

                // Per-device state for stick/trigger tab previews.
                if (_inputManager.SlotControllerTypes[i] == VirtualControllerType.KeyboardMouse)
                {
                    // Feed PRE-deadzone KBM values so ProcessStickForPreview applies the
                    // full pipeline once (center offset → deadzone → curves) with correct
                    // jump-to-boundary visual behavior.
                    var kbm = _inputManager.CombinedKbmRawStates[i];
                    var synth = new Gamepad();
                    synth.ThumbLX = kbm.PreDzMouseDeltaX;
                    synth.ThumbLY = kbm.PreDzMouseDeltaY;
                    synth.ThumbRY = kbm.PreDzScrollDelta;
                    padVm.UpdateDeviceState(synth);
                }
                else
                {
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

            // ── Update audio rumble level meters + sync detector on/off ──
            if (_audioBassDetector != null)
            {
                double level = _audioBassDetector.BassEnergy;
                for (int i = 0; i < _mainVm.Pads.Count; i++)
                {
                    if (SettingsManager.SlotCreated[i] && _mainVm.Pads[i].AudioRumbleEnabled)
                        _mainVm.Pads[i].AudioRumbleLevelMeter = level;
                }
            }

            // ── Auto-idle engine when no slots are created ──
            UpdateIdleState();

            // ── Auto-profile switching (check foreground window) ──
            _foregroundMonitor?.CheckForegroundWindow();
        }

        // ─────────────────────────────────────────────
        //  Auto-idle
        // ─────────────────────────────────────────────

        /// <summary>
        /// Sets the engine to idle when no virtual controller slots have active
        /// mappings, and wakes it when at least one slot does. A slot counts as
        /// active when it is created, enabled, and has at least one device assigned.
        /// Idle mode skips the expensive input/mapping/output pipeline and sleeps
        /// at ~20Hz, reducing CPU to ~0%.
        /// </summary>
        private void UpdateIdleState()
        {
            if (_inputManager == null) return;

            bool anyActive = false;
            for (int i = 0; i < InputManager.MaxPads && i < _mainVm.Pads.Count; i++)
            {
                if (SettingsManager.SlotCreated[i]
                    && SettingsManager.SlotEnabled[i]
                    && _mainVm.Pads[i].MappedDevices.Count > 0)
                {
                    anyActive = true;
                    break;
                }
            }

            _inputManager.IsIdle = !anyActive;
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

            var engineKey = !_inputManager.IsRunning ? "Stopped"
                : _inputManager.IsIdle ? "Idle" : "Running";
            dash.EngineStateKey = engineKey;
            dash.EngineStatus = engineKey switch
            {
                "Running" => Strings.Instance.Common_Running,
                "Idle" => Strings.Instance.Common_Idle,
                _ => Strings.Instance.Common_Stopped,
            };
            _mainVm.HasActiveSlots = !_inputManager.IsIdle;
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
                slot.StatusText = !SettingsManager.SlotEnabled[padIndex] ? Strings.Instance.Common_Disabled
                    : slot.IsInitializing ? Strings.Instance.Main_Initializing
                    : mappedCount == 0 ? Strings.Instance.Status_NoMapping
                    : padVm.IsDeviceOnline ? Strings.Instance.Main_Active
                    : Strings.Instance.Common_Idle;
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
                    ud.ForceRawJoystickMode && ud.RawButtonCount > 0 ? ud.RawButtonCount : ud.CapButtonCount,
                    CustomInputState.MaxButtons);
                int povCount = Math.Min(ud.CapPovCount, CustomInputState.MaxPovs);
                bool isKb = ud.CapType == InputDeviceType.Keyboard;
                bool isMouse = ud.CapType == InputDeviceType.Mouse;
                bool isTouchpad = ud.CapType == InputDeviceType.Touchpad;
                devVm.RebuildRawStateCollections(axisCount, btnCount, povCount, isKb, isMouse, isTouchpad);
                devVm.HasGyroData = ud.HasGyro;
                devVm.HasAccelData = ud.HasAccel;
                devVm.HasTouchpadData = ud.HasTouchpad || isTouchpad;
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
            if (ud == null)
            {
                devVm.HasRawData = false;
                return;
            }

            // Rebuild collections when the selected device changes.
            if (selected.InstanceGuid != devVm.LastRawStateDeviceGuid)
            {
                devVm.LastRawStateDeviceGuid = selected.InstanceGuid;
                int axisCount = Math.Min(ud.CapAxeCount, CustomInputState.MaxAxis);
                int btnCount = Math.Min(
                    ud.ForceRawJoystickMode && ud.RawButtonCount > 0 ? ud.RawButtonCount : ud.CapButtonCount,
                    CustomInputState.MaxButtons);
                int povCount = Math.Min(ud.CapPovCount, CustomInputState.MaxPovs);
                bool isKb = ud.CapType == InputDeviceType.Keyboard;
                bool isMouse = ud.CapType == InputDeviceType.Mouse;
                bool isTouchpad2 = ud.CapType == InputDeviceType.Touchpad;
                devVm.RebuildRawStateCollections(axisCount, btnCount, povCount, isKb, isMouse, isTouchpad2);
                devVm.HasGyroData = ud.HasGyro;
                devVm.HasAccelData = ud.HasAccel;
                devVm.HasTouchpadData = ud.HasTouchpad || isTouchpad2;
            }

            devVm.HasRawData = true;

            // Device exists but disconnected — structural layout is visible, skip value updates.
            if (ud.InputState == null)
                return;

            var state = ud.InputState;

            // Mouse visual — update motion and scroll display properties.
            if (devVm.IsMouseDevice)
            {
                devVm.MouseMotionX = (state.Axis[0] - 32767.0) / 32767.0;
                devVm.MouseMotionY = -(state.Axis[1] - 32767.0) / 32767.0;
                if (ud.CapAxeCount > 2)
                    devVm.MouseScrollIntensity = (state.Axis[2] - 32767.0) / 32767.0;
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

            // Update touchpad finger positions.
            if (ud.HasTouchpad || ud.IsTouchpad)
            {
                devVm.TouchpadX0 = state.TouchpadFingers[0];
                devVm.TouchpadY0 = state.TouchpadFingers[1];
                devVm.TouchpadDown0 = state.TouchpadDown[0];
                devVm.TouchpadX1 = state.TouchpadFingers[3];
                devVm.TouchpadY1 = state.TouchpadFingers[4];
                devVm.TouchpadDown1 = state.TouchpadDown[1];
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

            // Touchpad descriptors: "Touchpad 0 Finger N X/Y/Down"
            if (s.StartsWith("Touchpad", StringComparison.Ordinal))
            {
                // Parse finger index and axis from descriptor.
                // Format: "Touchpad 0 Finger 0 X", "Touchpad 0 Finger 1 Down"
                if (s.Contains("Finger 0 X")) return (int)(state.TouchpadFingers[0] * 1000);
                if (s.Contains("Finger 0 Y")) return (int)(state.TouchpadFingers[1] * 1000);
                if (s.Contains("Finger 0 Down")) return state.TouchpadDown[0] ? 1 : 0;
                if (s.Contains("Finger 1 X")) return (int)(state.TouchpadFingers[3] * 1000);
                if (s.Contains("Finger 1 Y")) return (int)(state.TouchpadFingers[4] * 1000);
                if (s.Contains("Finger 1 Down")) return state.TouchpadDown[1] ? 1 : 0;
                return 0;
            }

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
        private bool _lastAudioRumbleAnyEnabled;

        private void SyncViewModelToPadSettings()
        {
            bool anyAudioRumble = false;
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

                if (SettingsManager.SlotCreated[i] && padVm.AudioRumbleEnabled)
                    anyAudioRumble = true;

                var selected = padVm.SelectedMappedDevice;
                if (selected == null || selected.InstanceGuid == Guid.Empty)
                    continue;

                SaveViewModelToPadSetting(padVm, selected.InstanceGuid, syncMappings: false);
            }

            // Start/stop audio bass detector when per-slot enable changes.
            if (anyAudioRumble != _lastAudioRumbleAnyEnabled)
            {
                _lastAudioRumbleAnyEnabled = anyAudioRumble;
                SyncAudioBassDetector();
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
        private static void SaveViewModelToPadSetting(PadViewModel padVm, Guid instanceGuid, bool syncMappings = true)
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

            // Dead zone shapes.
            ps.LeftThumbDeadZoneShape = padVm.LeftDeadZoneShape.ToString();
            ps.RightThumbDeadZoneShape = padVm.RightDeadZoneShape.ToString();

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
            ps.LeftThumbMaxRangeXNeg = padVm.LeftMaxRangeXNeg.ToString();
            ps.LeftThumbMaxRangeYNeg = padVm.LeftMaxRangeYNeg.ToString();
            ps.RightThumbMaxRangeXNeg = padVm.RightMaxRangeXNeg.ToString();
            ps.RightThumbMaxRangeYNeg = padVm.RightMaxRangeYNeg.ToString();

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

            // Audio bass rumble.
            ps.AudioRumbleEnabled = padVm.AudioRumbleEnabled ? "1" : "0";
            ps.AudioRumbleSensitivity = padVm.AudioRumbleSensitivity.ToString("F1");
            ps.AudioRumbleCutoffHz = padVm.AudioRumbleCutoffHz.ToString("F0");
            ps.AudioRumbleLeftMotor = padVm.AudioRumbleLeftMotor.ToString();
            ps.AudioRumbleRightMotor = padVm.AudioRumbleRightMotor.ToString();

            // Mapping descriptors: clear + rewrite only when explicitly requested.
            // The 30Hz SyncViewModelToPadSettings path passes syncMappings=false
            // because ClearMappingDescriptors() creates a race window — the polling
            // thread can read the PadSetting between the clear and the rewrite,
            // seeing empty mapping strings → zero Gamepad output.
            // Mappings are only synced on explicit save, preset change, or device switch.
            if (syncMappings)
            {
                ps.ClearMappingDescriptors();

                foreach (var mapping in padVm.Mappings)
                {
                    string target = mapping.TargetSettingName;
                    if (target.StartsWith("VJoy", StringComparison.Ordinal))
                    {
                        ps.SetVJoyMapping(target, mapping.SourceDescriptor ?? string.Empty);
                        if (mapping.NegSettingName != null)
                            ps.SetVJoyMapping(mapping.NegSettingName, mapping.NegSourceDescriptor ?? string.Empty);
                    }
                    else if (target.StartsWith("Midi", StringComparison.Ordinal))
                    {
                        ps.SetMidiMapping(target, mapping.SourceDescriptor ?? string.Empty);
                        if (mapping.NegSettingName != null)
                            ps.SetMidiMapping(mapping.NegSettingName, mapping.NegSourceDescriptor ?? string.Empty);
                    }
                    else if (target.StartsWith("Kbm", StringComparison.Ordinal))
                    {
                        ps.SetKbmMapping(target, mapping.SourceDescriptor ?? string.Empty);
                        if (mapping.NegSettingName != null)
                            ps.SetKbmMapping(mapping.NegSettingName, mapping.NegSourceDescriptor ?? string.Empty);
                    }
                    else
                    {
                        var prop = typeof(PadSetting).GetProperty(target);
                        if (prop != null && prop.PropertyType == typeof(string) && prop.CanWrite)
                            prop.SetValue(ps, mapping.SourceDescriptor ?? string.Empty);

                        if (mapping.NegSettingName != null)
                        {
                            var negProp = typeof(PadSetting).GetProperty(mapping.NegSettingName);
                            if (negProp != null && negProp.PropertyType == typeof(string) && negProp.CanWrite)
                                negProp.SetValue(ps, mapping.NegSourceDescriptor ?? string.Empty);
                        }
                    }

                    // Save per-mapping dead zone.
                    if (mapping.MappingDeadZone > 0)
                        ps.SetMappingDeadZone(target, mapping.MappingDeadZone.ToString());
                    else
                        ps.SetMappingDeadZone(target, "");
                }
            }
        }

        /// <summary>
        /// Loads a specific device's PadSetting into the PadViewModel.
        /// </summary>
        internal static void LoadPadSettingToViewModel(PadViewModel padVm, Guid instanceGuid)
        {
            var us = SettingsManager.FindSettingByInstanceGuidAndSlot(instanceGuid, padVm.PadIndex);
            if (us == null) return;

            var ps = us.GetPadSetting();
            if (ps == null) return;

            // Dead zones.
            padVm.LeftDeadZoneShape = (int)Common.Input.InputManager.ParseDeadZoneShape(ps.LeftThumbDeadZoneShape);
            padVm.RightDeadZoneShape = (int)Common.Input.InputManager.ParseDeadZoneShape(ps.RightThumbDeadZoneShape);
            padVm.LeftDeadZoneX = TryParseDouble(ps.LeftThumbDeadZoneX, 0);
            padVm.LeftDeadZoneY = TryParseDouble(ps.LeftThumbDeadZoneY, 0);
            padVm.RightDeadZoneX = TryParseDouble(ps.RightThumbDeadZoneX, 0);
            padVm.RightDeadZoneY = TryParseDouble(ps.RightThumbDeadZoneY, 0);
            ps.MigrateAntiDeadZones();
            padVm.LeftAntiDeadZoneX = TryParseDouble(ps.LeftThumbAntiDeadZoneX, 0);
            padVm.LeftAntiDeadZoneY = TryParseDouble(ps.LeftThumbAntiDeadZoneY, 0);
            padVm.RightAntiDeadZoneX = TryParseDouble(ps.RightThumbAntiDeadZoneX, 0);
            padVm.RightAntiDeadZoneY = TryParseDouble(ps.RightThumbAntiDeadZoneY, 0);
            padVm.LeftLinear = TryParseDouble(ps.LeftThumbLinear, 0);
            padVm.RightLinear = TryParseDouble(ps.RightThumbLinear, 0);

            // Sensitivity curves (string format: control points "x,y;x,y;..." or legacy single number).
            padVm.LeftSensitivityCurveX = ps.LeftThumbSensitivityCurveX ?? "0,0;1,1";
            padVm.LeftSensitivityCurveY = ps.LeftThumbSensitivityCurveY ?? "0,0;1,1";
            padVm.RightSensitivityCurveX = ps.RightThumbSensitivityCurveX ?? "0,0;1,1";
            padVm.RightSensitivityCurveY = ps.RightThumbSensitivityCurveY ?? "0,0;1,1";
            padVm.LeftTriggerSensitivityCurve = ps.LeftTriggerSensitivityCurve ?? "0,0;1,1";
            padVm.RightTriggerSensitivityCurve = ps.RightTriggerSensitivityCurve ?? "0,0;1,1";

            // Max range.
            padVm.LeftMaxRangeX = TryParseDouble(ps.LeftThumbMaxRangeX, 100);
            padVm.LeftMaxRangeY = TryParseDouble(ps.LeftThumbMaxRangeY, 100);
            padVm.RightMaxRangeX = TryParseDouble(ps.RightThumbMaxRangeX, 100);
            padVm.RightMaxRangeY = TryParseDouble(ps.RightThumbMaxRangeY, 100);
            ps.MigrateMaxRangeDirections();
            padVm.LeftMaxRangeXNeg = TryParseDouble(ps.LeftThumbMaxRangeXNeg, 100);
            padVm.LeftMaxRangeYNeg = TryParseDouble(ps.LeftThumbMaxRangeYNeg, 100);
            padVm.RightMaxRangeXNeg = TryParseDouble(ps.RightThumbMaxRangeXNeg, 100);
            padVm.RightMaxRangeYNeg = TryParseDouble(ps.RightThumbMaxRangeYNeg, 100);

            // Center offsets.
            padVm.LeftCenterOffsetX = TryParseDouble(ps.LeftThumbCenterOffsetX, 0);
            padVm.LeftCenterOffsetY = TryParseDouble(ps.LeftThumbCenterOffsetY, 0);
            padVm.RightCenterOffsetX = TryParseDouble(ps.RightThumbCenterOffsetX, 0);
            padVm.RightCenterOffsetY = TryParseDouble(ps.RightThumbCenterOffsetY, 0);

            // Trigger dead zones.
            padVm.LeftTriggerDeadZone = TryParseDouble(ps.LeftTriggerDeadZone, 0);
            padVm.RightTriggerDeadZone = TryParseDouble(ps.RightTriggerDeadZone, 0);
            padVm.LeftTriggerAntiDeadZone = TryParseDouble(ps.LeftTriggerAntiDeadZone, 0);
            padVm.RightTriggerAntiDeadZone = TryParseDouble(ps.RightTriggerAntiDeadZone, 0);

            // Trigger max range.
            padVm.LeftTriggerMaxRange = TryParseDouble(ps.LeftTriggerMaxRange, 100);
            padVm.RightTriggerMaxRange = TryParseDouble(ps.RightTriggerMaxRange, 100);

            // Force feedback.
            padVm.ForceOverallGain = TryParseInt(ps.ForceOverall, 100);
            padVm.LeftMotorStrength = TryParseInt(ps.LeftMotorStrength, 100);
            padVm.RightMotorStrength = TryParseInt(ps.RightMotorStrength, 100);
            padVm.SwapMotors = ps.ForceSwapMotor == "1" ||
                (ps.ForceSwapMotor ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);

            // Audio bass rumble.
            padVm.AudioRumbleEnabled = ps.AudioRumbleEnabled == "1";
            padVm.AudioRumbleSensitivity = TryParseDouble(ps.AudioRumbleSensitivity, 4.0);
            padVm.AudioRumbleCutoffHz = TryParseDouble(ps.AudioRumbleCutoffHz, 80.0);
            padVm.AudioRumbleLeftMotor = TryParseInt(ps.AudioRumbleLeftMotor, 100);
            padVm.AudioRumbleRightMotor = TryParseInt(ps.AudioRumbleRightMotor, 100);

            // Sync dynamic stick/trigger config items.
            padVm.SyncAllConfigItemsFromVm();

            // Mapping descriptors.
            var ud = FindUserDevice(instanceGuid);
            foreach (var mapping in padVm.Mappings)
            {
                string target = mapping.TargetSettingName;
                string value = GetMappingValue(ps, target);
                mapping.LoadDescriptor(value);
                MappingDisplayResolver.ResolveDisplayText(mapping, ud);

                if (mapping.NegSettingName != null)
                {
                    string negTarget = mapping.NegSettingName;
                    string negValue = GetMappingValue(ps, negTarget);
                    mapping.LoadNegDescriptor(negValue);
                    MappingDisplayResolver.ResolveNegDisplayText(mapping, ud);
                }

                // Load per-mapping dead zone.
                string dzStr = ps.GetMappingDeadZone(target);
                mapping.MappingDeadZone = int.TryParse(dzStr, out int dz) && dz > 0 ? dz : 50;
            }
        }

        private static string GetMappingValue(PadSetting ps, string key)
        {
            if (key.StartsWith("VJoy", StringComparison.Ordinal))
                return ps.GetVJoyMapping(key);
            if (key.StartsWith("Midi", StringComparison.Ordinal))
                return ps.GetMidiMapping(key);
            if (key.StartsWith("Kbm", StringComparison.Ordinal))
                return ps.GetKbmMapping(key);
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

        private static double TryParseDouble(string value, double defaultValue)
        {
            if (string.IsNullOrEmpty(value)) return defaultValue;
            return double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double result) ? result : defaultValue;
        }

        /// <summary>
        /// Resolves a mapping descriptor to a human-friendly display name using
        /// the device identified by the given instance GUID.
        /// For keyboards, "Button 65" becomes "A". For mice, "Button 0" becomes "Left Click".
        /// </summary>
        // Display text resolution delegated to MappingDisplayResolver.
        internal static void ResolveDisplayText(MappingItem mapping, Guid instanceGuid) =>
            MappingDisplayResolver.ResolveDisplayText(mapping, FindUserDevice(instanceGuid));

        internal static void ResolveNegDisplayText(MappingItem mapping, Guid instanceGuid) =>
            MappingDisplayResolver.ResolveNegDisplayText(mapping, FindUserDevice(instanceGuid));

        /// <summary>
        /// Handles dropdown input selection: resolves the display text for the newly
        /// selected input and syncs the selected item.
        /// </summary>
        private void OnInputSelectedFromDropdown(object sender, EventArgs e)
        {
            if (sender is not MappingItem mapping) return;
            // Find the device for this mapping's pad slot.
            foreach (var padVm in _mainVm.Pads)
            {
                if (!padVm.Mappings.Contains(mapping)) continue;
                var selected = padVm.SelectedMappedDevice;
                if (selected == null || selected.InstanceGuid == Guid.Empty) break;
                var ud = FindUserDevice(selected.InstanceGuid);
                MappingDisplayResolver.ResolveDisplayText(mapping, ud);
                mapping.SyncSelectedInputFromDescriptor();
                break;
            }
        }

        /// <summary>
        /// Populates the AvailableInputs dropdown for all mappings in a pad's mapping list.
        /// Builds the list from the device's DeviceObjects (friendly names for gamepads,
        /// numbered names for raw/non-gamepad devices). Also wires the dropdown selection
        /// event for display text resolution.
        /// </summary>
        private void PopulateAvailableInputs(PadViewModel padVm, UserDevice ud)
        {
            if (padVm == null) return;

            var choices = MappingDisplayResolver.BuildInputChoices(ud);
            foreach (var mapping in padVm.Mappings)
            {
                mapping.InputSelectedFromDropdown -= OnInputSelectedFromDropdown;
                mapping.InputSelectedFromDropdown += OnInputSelectedFromDropdown;

                mapping.AvailableInputs.Clear();
                foreach (var c in choices)
                    mapping.AvailableInputs.Add(c);
                mapping.SyncSelectedInputFromDescriptor();
            }
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
            PopulateAvailableInputs(padVm, FindUserDevice(selected.InstanceGuid));
        }

        /// <summary>
        /// Applies a PadSetting from a source layout to the current device with cross-layout translation.
        /// </summary>
        public void ApplyPadSettingToCurrentDeviceTranslated(int padIndex, PadSetting source,
            VirtualControllerType sourceType, bool sourceIsCustomVJoy,
            VirtualControllerType targetType, bool targetIsCustomVJoy)
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

            // Copy with cross-layout translation.
            ps.CopyFromTranslated(source, sourceType, sourceIsCustomVJoy, targetType, targetIsCustomVJoy);

            // Reload the ViewModel to reflect the new values.
            LoadPadSettingToViewModel(padVm, selected.InstanceGuid);
            PopulateAvailableInputs(padVm, FindUserDevice(selected.InstanceGuid));
        }

        /// <summary>
        /// Flushes all active pad ViewModels back to their PadSettings so that
        /// stored PadSettings reflect the latest UI state. Call before reading
        /// PadSettings across multiple slots (e.g., Copy From dialog).
        /// </summary>
        public void FlushAllPadViewModels()
        {
            foreach (var padVm in _mainVm.Pads)
            {
                var selected = padVm.SelectedMappedDevice;
                if (selected != null && selected.InstanceGuid != Guid.Empty)
                    SaveViewModelToPadSetting(padVm, selected.InstanceGuid);
            }
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
                PopulateAvailableInputs(padVm, FindUserDevice(newGuid));
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
                var guid = padVm.SelectedMappedDevice.InstanceGuid;
                LoadMappingDescriptorsOnly(padVm, guid);
                PopulateAvailableInputs(padVm, FindUserDevice(guid));
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
                MappingDisplayResolver.ResolveDisplayText(mapping, ud);

                if (mapping.NegSettingName != null)
                {
                    string negTarget = mapping.NegSettingName;
                    string negValue = GetMappingValue(ps, negTarget);
                    mapping.LoadNegDescriptor(negValue);
                    MappingDisplayResolver.ResolveNegDisplayText(mapping, ud);
                }

                // Load per-mapping dead zone.
                string dzStr = ps.GetMappingDeadZone(target);
                mapping.MappingDeadZone = int.TryParse(dzStr, out int dz) && dz > 0 ? dz : 50;
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
                _mainVm.StatusText = string.Format(Strings.Instance.Status_Error_Format, e.Message);
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
            else if (e.PropertyName == nameof(DashboardViewModel.EnableTouchpadOverlay))
            {
                if (_mainVm.Dashboard.EnableTouchpadOverlay)
                    ShowTouchpadOverlay();
                else
                    HideTouchpadOverlay();
            }
            else if (e.PropertyName == nameof(DashboardViewModel.TouchpadOverlayOpacity))
            {
                _touchpadOverlay?.SetSurfaceOpacity(_mainVm.Dashboard.TouchpadOverlayOpacity);
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
        //  Audio Bass Rumble lifecycle
        // ─────────────────────────────────────────────

        private AudioBassDetector _audioBassDetector;

        /// <summary>
        /// Checks whether any slot has audio rumble enabled and starts/stops
        /// the global detector accordingly. Called on engine start, slot changes,
        /// and during the UI timer sync.
        /// </summary>
        internal void SyncAudioBassDetector()
        {
            bool anyEnabled = false;
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                if (SettingsManager.SlotCreated[i] && _mainVm.Pads[i].AudioRumbleEnabled)
                {
                    anyEnabled = true;
                    break;
                }
            }

            if (anyEnabled && _audioBassDetector == null)
                StartAudioBassDetector();
            else if (!anyEnabled && _audioBassDetector != null)
                StopAudioBassDetector();
        }

        private void StartAudioBassDetector()
        {
            if (_audioBassDetector != null || _inputManager == null)
                return;

            _audioBassDetector = new AudioBassDetector();

            if (_audioBassDetector.Start())
            {
                _inputManager.AudioBassDetector = _audioBassDetector;
            }
            else
            {
                _audioBassDetector.Dispose();
                _audioBassDetector = null;
            }
        }

        private void StopAudioBassDetector()
        {
            if (_audioBassDetector == null)
                return;

            if (_inputManager != null)
                _inputManager.AudioBassDetector = null;

            _audioBassDetector.Dispose();
            _audioBassDetector = null;

            // Clear level meters on all pads.
            foreach (var pad in _mainVm.Pads)
                pad.AudioRumbleLevelMeter = 0;
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
            _webServer.StatusChanged += OnWebServerStatusChanged;
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

        private void OnWebServerStatusChanged(object sender, string status)
        {
            _dispatcher.BeginInvoke(() =>
            {
                _mainVm.Dashboard.WebControllerStatus = status;
                _mainVm.Dashboard.WebControllerClientCount = _webServer?.ClientCount ?? 0;
            });
        }

        private void StopWebServer()
        {
            if (_webServer == null)
                return;

            _webServer.StatusChanged -= OnWebServerStatusChanged;
            _webServer.Dispose();
            _webServer = null;
            _mainVm.Dashboard.WebControllerStatus = Strings.Instance.Common_Stopped;
            _mainVm.Dashboard.WebControllerClientCount = 0;
        }

        // ─────────────────────────────────────────────
        //  Touchpad Overlay lifecycle
        // ─────────────────────────────────────────────

        private Views.TouchpadOverlay _touchpadOverlay;
        private TouchpadOverlayDevice _touchpadOverlayDevice;

        private void ShowTouchpadOverlay()
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (_touchpadOverlay == null)
                {
                    _touchpadOverlay = new Views.TouchpadOverlay();
                    _touchpadOverlay.PositionChanged += OnTouchpadOverlayPositionChanged;
                }

                var dash = _mainVm.Dashboard;

                // Restore persisted size.
                _touchpadOverlay.Width = dash.TouchpadOverlayWidth;
                _touchpadOverlay.Height = dash.TouchpadOverlayHeight;

                // Restore persisted position or center on monitor.
                if (dash.TouchpadOverlayLeft >= 0 && dash.TouchpadOverlayTop >= 0)
                {
                    _touchpadOverlay.Left = dash.TouchpadOverlayLeft;
                    _touchpadOverlay.Top = dash.TouchpadOverlayTop;
                }
                else
                {
                    _touchpadOverlay.MoveToMonitor(dash.TouchpadOverlayMonitor);
                }

                _touchpadOverlay.SetSurfaceOpacity(dash.TouchpadOverlayOpacity);
                _touchpadOverlay.Show();
                dash.TouchpadOverlayStatus = Strings.Instance.Common_Running;

                // Register as a virtual touchpad device so it appears in Devices page.
                if (_touchpadOverlayDevice == null)
                    _touchpadOverlayDevice = new TouchpadOverlayDevice();
                _inputManager?.RegisterOverlayDevice(_touchpadOverlayDevice);
            });
        }

        private void HideTouchpadOverlay(bool close = false)
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (_touchpadOverlay != null)
                {
                    if (close)
                    {
                        _touchpadOverlay.PositionChanged -= OnTouchpadOverlayPositionChanged;
                        _touchpadOverlay.Close();
                        _touchpadOverlay = null;
                    }
                    else
                    {
                        _touchpadOverlay.Hide();
                    }
                    _mainVm.Dashboard.TouchpadOverlayStatus = Strings.Instance.Common_Stopped;
                }
                // Unregister the overlay device.
                if (_touchpadOverlayDevice != null)
                    _inputManager?.UnregisterExternalDevice(_touchpadOverlayDevice.InstanceGuid);
            });
        }

        /// <summary>Toggles the touchpad overlay visibility (for macro action).</summary>
        internal void ToggleTouchpadOverlay()
        {
            _dispatcher.BeginInvoke(() =>
            {
                var dash = _mainVm.Dashboard;
                dash.EnableTouchpadOverlay = !dash.EnableTouchpadOverlay;
            });
        }

        private void OnTouchpadOverlayPositionChanged()
        {
            if (_touchpadOverlay == null) return;
            var dash = _mainVm.Dashboard;
            dash.TouchpadOverlayLeft = _touchpadOverlay.Left;
            dash.TouchpadOverlayTop = _touchpadOverlay.Top;
            dash.TouchpadOverlayWidth = _touchpadOverlay.Width;
            dash.TouchpadOverlayHeight = _touchpadOverlay.Height;
            dash.TouchpadOverlayMonitor = _touchpadOverlay.GetCurrentMonitor();
        }

        private void OnCultureChanged() => _dispatcher.BeginInvoke(RefreshServerStatusStrings);

        /// <summary>
        /// Re-sets server status display strings after a language change.
        /// </summary>
        private void RefreshServerStatusStrings()
        {
            var dash = _mainVm.Dashboard;

            // Engine status — re-derive localized text from the invariant key.
            dash.EngineStatus = dash.EngineStateKey switch
            {
                "Running" => Strings.Instance.Common_Running,
                "Idle" => Strings.Instance.Common_Idle,
                _ => Strings.Instance.Common_Stopped,
            };

            // DSU server
            if (_dsuServer == null)
                dash.DsuServerStatus = Strings.Instance.Common_Stopped;
            else
                dash.DsuServerStatus = string.Format(Strings.Instance.Server_ListeningOn_Format, _mainVm.Dashboard.DsuMotionServerPort);

            // Web controller server
            if (_webServer == null)
                dash.WebControllerStatus = Strings.Instance.Common_Stopped;
            else
            {
                int clients = dash.WebControllerClientCount;
                dash.WebControllerStatus = clients > 0
                    ? string.Format(Strings.Instance.Server_RunningClients_Format, clients)
                    : string.Format(Strings.Instance.Server_RunningOn_Format, _webServer.Url ?? "");
            }
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
            if (HidHideController.IsAvailable())
            {
                // Build the set of desired whitelist paths (PadForge + user list).
                var desiredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    desiredPaths.Add(exePath);
                foreach (var path in _mainVm.Settings.HidHideWhitelistPaths)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                        desiredPaths.Add(path);
                }
                SyncWhitelist(desiredPaths);

                // Collect all desired blacklist IDs first, then sync atomically
                // to avoid a window where devices briefly become visible.
                var desiredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool cacheUpdated = false;

                foreach (var ud in snapshot)
                {
                    if (ud.HidHideEnabled && !string.IsNullOrEmpty(ud.DevicePath))
                    {
                        string instanceId = HidHideController.DevicePathToInstanceId(ud.DevicePath);

                        // If the DevicePath produced a valid HID instance ID, use it directly.
                        if (instanceId != null && instanceId.Contains("VID_", StringComparison.OrdinalIgnoreCase))
                        {
                            desiredIds.Add(instanceId);
                        }
                        // Fallback: synthetic paths (e.g., "XInput#0") — look up by VID/PID.
                        else if (ud.VendorId > 0 && ud.ProdId > 0)
                        {
                            var realIds = HidHideController.FindInstanceIdsByVidPid(
                                (ushort)ud.VendorId, (ushort)ud.ProdId);

                            if (realIds.Count > 0)
                            {
                                // Merge — never discard cached IDs. Preserves
                                // Controller 2's ID when only Controller 1 is online.
                                foreach (var id in realIds)
                                {
                                    if (!ud.HidHideInstanceIds.Contains(id))
                                    {
                                        ud.HidHideInstanceIds.Add(id);
                                        cacheUpdated = true;
                                    }
                                }
                                foreach (var id in ud.HidHideInstanceIds)
                                    desiredIds.Add(id);
                            }
                            else if (ud.HidHideInstanceIds.Count > 0)
                            {
                                // Device is offline — use cached IDs to pre-emptively blacklist.
                                System.Diagnostics.Debug.WriteLine(
                                    $"[ApplyDeviceHiding] Using {ud.HidHideInstanceIds.Count} cached instance IDs for offline device {ud.ResolvedName}");
                                foreach (var cachedId in ud.HidHideInstanceIds)
                                    desiredIds.Add(cachedId);
                            }
                        }
                    }
                }

                // Diagnostic: log exactly what we're blacklisting.
                foreach (var id in desiredIds)
                    System.Diagnostics.Debug.WriteLine($"[ApplyDeviceHiding] Blacklisting: {id}");
                System.Diagnostics.Debug.WriteLine($"[ApplyDeviceHiding] Total: {desiredIds.Count} instance IDs");

                // Atomically sync — only adds/removes the diff, never clears the blacklist.
                HidHideController.SyncManagedDevices(desiredIds);

                // Persist updated cache to settings.
                if (cacheUpdated)
                    _settingsService?.MarkDirty();

                if (desiredIds.Count > 0)
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
        /// Syncs the HidHide whitelist to match the desired set of application paths.
        /// Only adds/removes entries that PadForge manages — entries added by HidHide Client
        /// or other tools are left untouched.
        /// </summary>
        private void SyncWhitelist(HashSet<string> desiredWinPaths)
        {
            // Convert desired Windows paths to DOS device paths.
            var desiredDosPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var winPath in desiredWinPaths)
            {
                string dosPath = HidHideController.ToDosDevicePathPublic(winPath);
                if (dosPath != null)
                    desiredDosPaths.Add(dosPath);
            }

            var currentWhitelist = HidHideController.GetWhitelist();
            bool changed = false;

            // Remove PadForge-managed entries that are no longer desired.
            var toRemove = new List<string>();
            foreach (var managed in _managedWhitelistDosPaths)
            {
                if (!desiredDosPaths.Contains(managed))
                    toRemove.Add(managed);
            }
            foreach (var path in toRemove)
            {
                _managedWhitelistDosPaths.Remove(path);
                if (currentWhitelist.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0)
                    changed = true;
            }

            // Add new desired entries that aren't already in the whitelist.
            foreach (var dosPath in desiredDosPaths)
            {
                _managedWhitelistDosPaths.Add(dosPath);
                if (!currentWhitelist.Contains(dosPath, StringComparer.OrdinalIgnoreCase))
                {
                    currentWhitelist.Add(dosPath);
                    changed = true;
                }
            }

            if (changed)
                HidHideController.SetWhitelist(currentWhitelist);
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
            _managedWhitelistDosPaths.Clear();

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
                            mouseButtons.Add(buttonIndex); // buttonIndex is 0=L, 1=M, 2=R, 3=X1, 4=X2
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
            row.SdlGuid = ud.SdlGuid;
            row.DeviceName = ud.DevicePath == "aggregate://keyboards" ? Strings.Instance.Devices_AllKeyboardsMerged
                           : ud.DevicePath == "aggregate://mice" ? Strings.Instance.Devices_AllMiceMerged
                           : ud.DevicePath == "aggregate://touchpads" ? Strings.Instance.Devices_AllTouchpadsMerged
                           : ud.ResolvedName;
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
            row.HasTouchpad = ud.HasTouchpad;
            row.DevicePath = ud.DevicePath;

            // Resolve the HID instance path for display.
            // Individual devices have real HID paths; merged devices (aggregate://) do not.
            string instancePath = null;
            if (!string.IsNullOrEmpty(ud.DevicePath) && !ud.DevicePath.StartsWith("aggregate://"))
                instancePath = HidHideController.DevicePathToInstanceId(ud.DevicePath);

            if (!string.IsNullOrEmpty(instancePath) &&
                !instancePath.StartsWith("XInput", StringComparison.OrdinalIgnoreCase))
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

            // Set internal device type key (DeviceType display is computed from this).
            row.DeviceTypeKey = ud.CapType switch
            {
                InputDeviceType.Gamepad => "Gamepad",
                InputDeviceType.Joystick => "Joystick",
                InputDeviceType.Driving => "Wheel",
                InputDeviceType.Flight => "FlightStick",
                InputDeviceType.FirstPerson => "FirstPerson",
                InputDeviceType.Supplemental => "Supplemental",
                InputDeviceType.Mouse => "Mouse",
                InputDeviceType.Keyboard => "Keyboard",
                InputDeviceType.Touchpad => "Touchpad",
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

        /// <summary>
        /// Repopulates the source dropdown choices for all pads.
        /// Called when ForceRawJoystickMode changes to refresh display names.
        /// </summary>
        public void RefreshMappingDropdowns()
        {
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                var selected = padVm.SelectedMappedDevice;
                if (selected != null && selected.InstanceGuid != Guid.Empty)
                    PopulateAvailableInputs(padVm, FindUserDevice(selected.InstanceGuid));
            }
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
                        var devGuid = padVm.SelectedMappedDevice.InstanceGuid;
                        LoadPadSettingToViewModel(padVm, devGuid);
                        PopulateAvailableInputs(padVm, FindUserDevice(devGuid));
                        _previousSelectedDevice[i] = devGuid;
                    }

                    // Initialize the previous-device tracker if not set, and populate
                    // dropdowns for the initial selection (including offline devices).
                    if (!_previousSelectedDevice.ContainsKey(i) && padVm.SelectedMappedDevice != null)
                    {
                        var initGuid = padVm.SelectedMappedDevice.InstanceGuid;
                        PopulateAvailableInputs(padVm, FindUserDevice(initGuid));
                        _previousSelectedDevice[i] = initGuid;
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
        /// <param name="padIndex">Pad slot index (0–15).</param>
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

            var vib = _inputManager.VibrationStates[padIndex];

            // For vJoy slots, send directional force instead of scalar rumble so FFB
            // devices (joysticks, wheels) push in the correct direction rather than
            // just rattling. Direction uses "force comes from" convention:
            // 9000 = from East = pushes left, 27000 = from West = pushes right.
            bool isVJoy = _inputManager.SlotControllerTypes[padIndex] == VirtualControllerType.VJoy;
            if (isVJoy && (left != right))
            {
                vib.HasDirectionalData = true;
                vib.EffectType = (uint)1; // FfbEffectTypes.Const
                vib.SignedMagnitude = 10000;
                vib.Direction = (ushort)(left ? 8192 : 24576); // East (~90°) or West (~270°) in HID logical units
                vib.DeviceGain = 255;
            }

            // Always set scalar motors too (used by rumble-only devices in the same slot).
            if (left) vib.LeftMotorSpeed = 65535;
            if (right) vib.RightMotorSpeed = 65535;

            // Schedule clearing after 500ms.
            var clearTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            clearTimer.Tick += (s2, e2) =>
            {
                if (_inputManager != null && padIndex < InputManager.MaxPads)
                {
                    if (left) vib.LeftMotorSpeed = 0;
                    if (right) vib.RightMotorSpeed = 0;
                    if (isVJoy)
                    {
                        vib.HasDirectionalData = false;
                        vib.SignedMagnitude = 0;
                        vib.Direction = 0;
                        vib.EffectType = 0;
                    }
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
            _recordedAxisTargets = new HashSet<MacroAxisTarget>();
            _recordedAxisDirections = new Dictionary<MacroAxisTarget, MacroAxisDirection>();
            _recordedPovs = new HashSet<string>();
            _macroAxisCandidate = MacroAxisTarget.None;
            _macroAxisCandidateDelta = 0f;
            _macroAxisHoldCounter = 0;

            // Capture axis baseline so we detect movement delta, not absolute position.
            _macroAxisBaseline = CaptureAxisBaseline(padIndex, macro.TriggerSource, macro.ButtonStyle);

            macro.RecordingLiveText = "Press buttons or move axis...";
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

            // Save recorded axis triggers (can combine with buttons).
            var axisTargets = _recordedAxisTargets?.Count > 0
                ? _recordedAxisTargets.ToArray()
                : Array.Empty<MacroAxisTarget>();
            _recordingMacro.TriggerAxisTargets = axisTargets;

            // Save recorded axis directions (parallel to targets).
            if (axisTargets.Length > 0 && _recordedAxisDirections != null)
            {
                _recordingMacro.TriggerAxisDirections = axisTargets
                    .Select(t => _recordedAxisDirections.TryGetValue(t, out var d) ? d : MacroAxisDirection.Any)
                    .ToArray();
            }
            else
            {
                _recordingMacro.TriggerAxisDirections = Array.Empty<MacroAxisDirection>();
            }

            // Save recorded POV triggers.
            _recordingMacro.TriggerPovs = _recordedPovs?.Count > 0
                ? _recordedPovs.ToArray()
                : Array.Empty<string>();

            // Save recorded buttons (independent of axis).
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
            _recordedAxisTargets = null;
            _recordedAxisDirections = null;
            _recordedPovs = null;
            _macroAxisBaseline = null;
            _macroAxisCandidate = MacroAxisTarget.None;
            _macroAxisCandidateDelta = 0f;
            _macroAxisHoldCounter = 0;
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

            // Read current axis values for delta detection.
            float[] currentAxes = ReadCurrentAxes(
                _recordingPadIndex, _recordingMacro.TriggerSource, _recordingMacro.ButtonStyle);

            // Detect axes via baseline+delta+hold (shared across all paths).
            // Accumulates into _recordedAxisTargets — multiple axes can be recorded.
            if (_macroAxisBaseline != null && currentAxes != null)
            {
                MacroAxisTarget bestCandidate = MacroAxisTarget.None;
                float bestDelta = 0f;
                float bestRawDelta = 0f; // signed delta for direction detection

                MacroAxisTarget[] axes = {
                    MacroAxisTarget.LeftStickX, MacroAxisTarget.LeftStickY,
                    MacroAxisTarget.RightStickX, MacroAxisTarget.RightStickY,
                    MacroAxisTarget.LeftTrigger, MacroAxisTarget.RightTrigger
                };
                for (int i = 0; i < axes.Length && i < currentAxes.Length && i < _macroAxisBaseline.Length; i++)
                {
                    // Skip axes already recorded.
                    if (_recordedAxisTargets.Contains(axes[i])) continue;

                    float rawDelta = currentAxes[i] - _macroAxisBaseline[i];
                    float delta = Math.Abs(rawDelta);
                    if (delta > AxisRecordThreshold && delta > bestDelta)
                    {
                        bestDelta = delta;
                        bestRawDelta = rawDelta;
                        bestCandidate = axes[i];
                    }
                }

                if (bestCandidate != MacroAxisTarget.None)
                {
                    if (bestCandidate == _macroAxisCandidate)
                    {
                        _macroAxisHoldCounter++;
                        if (_macroAxisHoldCounter >= MacroAxisHoldCycles)
                        {
                            _recordedAxisTargets.Add(bestCandidate);
                            // Record the direction the axis was deflected.
                            _recordedAxisDirections[bestCandidate] =
                                _macroAxisCandidateDelta > 0 ? MacroAxisDirection.Positive
                                : _macroAxisCandidateDelta < 0 ? MacroAxisDirection.Negative
                                : MacroAxisDirection.Any;
                            _macroAxisCandidate = MacroAxisTarget.None;
                            _macroAxisCandidateDelta = 0f;
                            _macroAxisHoldCounter = 0;
                        }
                    }
                    else
                    {
                        _macroAxisCandidate = bestCandidate;
                        _macroAxisCandidateDelta = bestRawDelta;
                        _macroAxisHoldCounter = 1;
                    }
                }
                else
                {
                    _macroAxisCandidate = MacroAxisTarget.None;
                    _macroAxisCandidateDelta = 0f;
                    _macroAxisHoldCounter = 0;
                }
            }

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
                                if (_recordingDeviceGuid == Guid.Empty)
                                    _recordingDeviceGuid = ud.InstanceGuid;
                                _recordedRawButtons.Add(i);
                            }
                        }

                        // Check for any active POV hats on this device.
                        var povs = ud.InputState.Povs;
                        if (povs != null)
                        {
                            for (int p = 0; p < povs.Length; p++)
                            {
                                if (povs[p] >= 0)
                                {
                                    if (_recordingDeviceGuid == Guid.Empty)
                                        _recordingDeviceGuid = ud.InstanceGuid;
                                    _recordedPovs.Add($"{p}:{povs[p]}");
                                }
                            }
                        }
                    }
                }

                // Update live display text (buttons + POVs + axes combined, device name at end).
                var parts = new List<string>();
                if (_recordedRawButtons.Count > 0)
                {
                    var objects = ResolveDeviceObjects(_recordingDeviceGuid);
                    foreach (int b in _recordedRawButtons.OrderBy(x => x))
                    {
                        var obj = objects?.FirstOrDefault(o => o.IsButton && o.InputIndex == b);
                        parts.Add(obj != null && !string.IsNullOrEmpty(obj.Name) ? obj.Name : $"Button {b}");
                    }
                }
                foreach (var pov in _recordedPovs)
                    parts.Add(MacroItem.FormatPovTrigger(pov));
                foreach (var ax in _recordedAxisTargets)
                    parts.Add($"{ax.DisplayName()} > {_recordingMacro.TriggerAxisThreshold}%");

                if (parts.Count > 0)
                {
                    string result = string.Join(" + ", parts);
                    string deviceName = ResolveDeviceName(_recordingDeviceGuid);
                    if (!string.IsNullOrEmpty(deviceName))
                        result = $"{result} ({deviceName})";
                    _recordingMacro.RecordingLiveText = result;
                }
                else
                    _recordingMacro.RecordingLiveText = "Press buttons or move axis...";
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

                // Update live display (buttons + axes combined).
                {
                    var parts = new List<string>();
                    if (_recordedCustomButtons != null && _recordedCustomButtons.Any(w => w != 0))
                        parts.Add(MacroButtonNames.FormatCustomButtons(_recordedCustomButtons));
                    foreach (var ax in _recordedAxisTargets)
                        parts.Add($"{ax.DisplayName()} > {_recordingMacro.TriggerAxisThreshold}%");
                    _recordingMacro.RecordingLiveText = parts.Count > 0
                        ? string.Join(" + ", parts) : "Press buttons or move axis...";
                }
            }
            else
            {
                // Gamepad preset OutputController: accumulate from the combined Xbox-mapped state.
                var gp = _inputManager.CombinedOutputStates[_recordingPadIndex];
                ushort xboxButtons = gp.Buttons;
                _recordedButtons |= xboxButtons;

                // Update live display (buttons + axes combined).
                {
                    var parts = new List<string>();
                    if (_recordedButtons != 0)
                        parts.Add(MacroButtonNames.FormatButtons(_recordedButtons, _recordingMacro.ButtonStyle));
                    foreach (var ax in _recordedAxisTargets)
                        parts.Add($"{ax.DisplayName()} > {_recordingMacro.TriggerAxisThreshold}%");
                    _recordingMacro.RecordingLiveText = parts.Count > 0
                        ? string.Join(" + ", parts) : "Press buttons or move axis...";
                }
            }
        }

        /// <summary>
        /// Captures the current axis values as a 6-element float array (0..1 normalized)
        /// for use as a baseline during macro trigger recording.
        /// </summary>
        private float[] CaptureAxisBaseline(int padIndex, MacroTriggerSource source, MacroButtonStyle style)
        {
            return ReadCurrentAxes(padIndex, source, style);
        }

        /// <summary>
        /// Reads the current 6-axis values (LX, LY, RX, RY, LT, RT) as 0..1 floats
        /// from the appropriate source for the recording path.
        /// </summary>
        private float[] ReadCurrentAxes(int padIndex, MacroTriggerSource source, MacroButtonStyle style)
        {
            if (_inputManager == null || padIndex < 0 || padIndex >= InputManager.MaxPads)
                return null;

            float[] result = new float[6];

            if (source == MacroTriggerSource.InputDevice)
            {
                // Read raw axes from the first assigned device that has axis data.
                var slotSettings = SettingsManager.UserSettings?.FindByPadIndex(padIndex);
                if (slotSettings == null) return null;
                foreach (var setting in slotSettings)
                {
                    var ud = FindUserDevice(setting.InstanceGuid);
                    if (ud == null || !ud.IsOnline || ud.InputState == null) continue;
                    var rawAxes = ud.InputState.Axis;
                    if (rawAxes == null || rawAxes.Length < 6) continue;
                    for (int i = 0; i < 6 && i < rawAxes.Length; i++)
                        result[i] = (rawAxes[i] + 32768f) / 65535f;
                    return result;
                }
                return null;
            }
            else if (style == MacroButtonStyle.Numbered)
            {
                // vJoy raw state path.
                var rawState = _inputManager.CombinedVJoyRawStates[padIndex];
                MacroAxisTarget[] axes = {
                    MacroAxisTarget.LeftStickX, MacroAxisTarget.LeftStickY,
                    MacroAxisTarget.RightStickX, MacroAxisTarget.RightStickY,
                    MacroAxisTarget.LeftTrigger, MacroAxisTarget.RightTrigger
                };
                for (int i = 0; i < axes.Length; i++)
                    result[i] = InputManager.ReadAxisAsVolumeRaw(in rawState, axes[i]);
                return result;
            }
            else
            {
                // Gamepad OutputController path.
                var gp = _inputManager.CombinedOutputStates[padIndex];
                MacroAxisTarget[] axes = {
                    MacroAxisTarget.LeftStickX, MacroAxisTarget.LeftStickY,
                    MacroAxisTarget.RightStickX, MacroAxisTarget.RightStickY,
                    MacroAxisTarget.LeftTrigger, MacroAxisTarget.RightTrigger
                };
                for (int i = 0; i < axes.Length; i++)
                    result[i] = InputManager.ReadAxisAsVolume(in gp, axes[i]);
                return result;
            }
        }

        /// <summary>Resolves a device GUID to a human-readable name.</summary>
        private static string ResolveDeviceName(Guid deviceGuid)
        {
            if (deviceGuid == Guid.Empty) return null;
            return SettingsManager.FindDeviceByInstanceGuid(deviceGuid)?.ResolvedName;
        }

        private static DeviceObjectItem[] ResolveDeviceObjects(Guid deviceGuid)
        {
            if (deviceGuid == Guid.Empty) return null;
            return SettingsManager.FindDeviceByInstanceGuid(deviceGuid)?.DeviceObjects;
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
                VJoyConfigs = SnapshotVJoyConfigs(),
                MidiConfigs = SnapshotMidiConfigs(),
                EnableDsuMotionServer = _mainVm.Dashboard.EnableDsuMotionServer,
                DsuMotionServerPort = _mainVm.Dashboard.DsuMotionServerPort,
                EnableWebController = _mainVm.Dashboard.EnableWebController,
                WebControllerPort = _mainVm.Dashboard.WebControllerPort
            };
        }

        private VJoySlotConfigData[] SnapshotVJoyConfigs()
        {
            var list = new List<VJoySlotConfigData>();
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                if (!SettingsManager.SlotCreated[i] ||
                    _mainVm.Pads[i].OutputType != VirtualControllerType.VJoy)
                    continue;
                var cfg = _mainVm.Pads[i].VJoyConfig;
                list.Add(new VJoySlotConfigData
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

        private MidiSlotConfigData[] SnapshotMidiConfigs()
        {
            var list = new List<MidiSlotConfigData>();
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                if (!SettingsManager.SlotCreated[i] ||
                    _mainVm.Pads[i].OutputType != VirtualControllerType.Midi)
                    continue;
                var cfg = _mainVm.Pads[i].MidiConfig;
                list.Add(new MidiSlotConfigData
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

            // ── Apply vJoy/MIDI configurations ──
            if (profile.VJoyConfigs != null)
            {
                foreach (var cfgData in profile.VJoyConfigs)
                {
                    int idx = cfgData.SlotIndex;
                    if (idx >= 0 && idx < _mainVm.Pads.Count &&
                        SettingsManager.SlotCreated[idx] &&
                        _mainVm.Pads[idx].OutputType == VirtualControllerType.VJoy)
                    {
                        var cfg = _mainVm.Pads[idx].VJoyConfig;
                        cfg.Preset = cfgData.Preset;
                        if (cfgData.Preset == VJoyPreset.Custom)
                        {
                            cfg.ThumbstickCount = cfgData.ThumbstickCount;
                            cfg.TriggerCount = cfgData.TriggerCount;
                            cfg.PovCount = cfgData.PovCount;
                            cfg.ButtonCount = cfgData.ButtonCount;
                        }
                    }
                }
            }

            if (profile.MidiConfigs != null)
            {
                foreach (var cfgData in profile.MidiConfigs)
                {
                    int idx = cfgData.SlotIndex;
                    if (idx >= 0 && idx < _mainVm.Pads.Count &&
                        SettingsManager.SlotCreated[idx] &&
                        _mainVm.Pads[idx].OutputType == VirtualControllerType.Midi)
                    {
                        var cfg = _mainVm.Pads[idx].MidiConfig;
                        cfg.Channel = cfgData.Channel;
                        cfg.Velocity = cfgData.Velocity;
                        cfg.StartCc = cfgData.StartCc;
                        cfg.CcCount = cfgData.CcCount;
                        cfg.StartNote = cfgData.StartNote;
                        cfg.NoteCount = cfgData.NoteCount;
                        _mainVm.Pads[idx].RebuildMappings();
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
                {
                    LoadPadSettingToViewModel(padVm, selected.InstanceGuid);
                    PopulateAvailableInputs(padVm, FindUserDevice(selected.InstanceGuid));
                }
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
                    _mainVm.StatusText = string.Format(Strings.Instance.Status_ProfileSwitched_Format, target.Name);
                }
            }
            else
            {
                // Revert to default (root) profile using the startup snapshot.
                SettingsManager.ActiveProfileId = null;
                _mainVm.Settings.ActiveProfileInfo = Strings.Instance.Profile_Default;
                if (_defaultProfileSnapshot != null)
                    ApplyProfile(_defaultProfileSnapshot);
                _mainVm.StatusText = Strings.Instance.Status_ProfileSwitchedDefault;
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
                SettingsManager.PendingDefaultSnapshot = snapshot;
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
                    profile.VJoyConfigs = snapshot.VJoyConfigs;
                    profile.MidiConfigs = snapshot.MidiConfigs;
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
            SettingsManager.PendingDefaultSnapshot = _defaultProfileSnapshot;
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

        // ─────────────────────────────────────────────
        //  Profile CRUD (domain logic, called by MainWindow UI handlers)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Creates a new empty profile (no VCs, no device assignments).
        /// Returns the created ProfileData.
        /// </summary>
        public ProfileData CreateEmptyProfile(string name, string pipeSeparatedExePaths)
        {
            var profile = new ProfileData
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name.Trim(),
                ExecutableNames = pipeSeparatedExePaths,
                Entries = Array.Empty<ProfileEntry>(),
                PadSettings = Array.Empty<PadSetting>(),
                SlotCreated = new bool[InputManager.MaxPads],
                SlotEnabled = new bool[InputManager.MaxPads],
                SlotControllerTypes = new int[InputManager.MaxPads],
            };
            SettingsManager.Profiles.Add(profile);
            return profile;
        }

        /// <summary>
        /// Snapshots the current runtime state into a new named profile.
        /// Returns the created ProfileData.
        /// </summary>
        public ProfileData CreateSnapshotProfile(string name, string pipeSeparatedExePaths)
        {
            var snapshot = SnapshotCurrentProfile();
            snapshot.Id = Guid.NewGuid().ToString("N");
            snapshot.Name = name.Trim();
            snapshot.ExecutableNames = pipeSeparatedExePaths;
            SettingsManager.Profiles.Add(snapshot);
            return snapshot;
        }

        /// <summary>
        /// Deletes a profile by ID. If the deleted profile was active, reverts to default.
        /// Returns true if the active profile changed (reverted to default).
        /// </summary>
        public bool DeleteProfile(string profileId)
        {
            SettingsManager.Profiles.RemoveAll(p => p.Id == profileId);

            bool wasActive = SettingsManager.ActiveProfileId == profileId;
            if (wasActive)
            {
                SettingsManager.ActiveProfileId = null;
                ApplyDefaultProfile();
            }
            RefreshProfileTopology();
            return wasActive;
        }

        /// <summary>
        /// Updates a profile's name and executable paths.
        /// Returns the updated ProfileData, or null if not found.
        /// </summary>
        public ProfileData EditProfile(string profileId, string newName, string newPipeSeparatedExePaths)
        {
            var profile = SettingsManager.Profiles.Find(p => p.Id == profileId);
            if (profile == null) return null;
            profile.Name = newName;
            profile.ExecutableNames = newPipeSeparatedExePaths;
            return profile;
        }

        /// <summary>
        /// Loads (activates) a profile by ID. Saves outgoing profile state first.
        /// </summary>
        public void LoadProfile(string profileId)
        {
            var profile = SettingsManager.Profiles.Find(p => p.Id == profileId);
            if (profile == null) return;
            if (SettingsManager.ActiveProfileId == profile.Id) return;

            SaveActiveProfileState();
            SettingsManager.ActiveProfileId = profile.Id;
            ApplyProfile(profile);
        }

        /// <summary>
        /// Reverts to the default profile. Saves outgoing profile state first.
        /// </summary>
        public void RevertToDefaultProfile()
        {
            if (SettingsManager.ActiveProfileId == null) return;
            SaveActiveProfileState();
            SettingsManager.ActiveProfileId = null;
            ApplyDefaultProfile();
        }

        /// <summary>
        /// Formats pipe-separated full paths into a display string showing just file names.
        /// </summary>
        public static string FormatExePaths(string pipeSeparatedPaths)
        {
            if (string.IsNullOrEmpty(pipeSeparatedPaths))
                return string.Empty;
            var parts = pipeSeparatedPaths.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var names = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                names[i] = System.IO.Path.GetFileName(parts[i]);
            return string.Join(", ", names);
        }

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
            VirtualControllerType.KeyboardMouse => 3,
            VirtualControllerType.Midi => 4,
            _ => 5
        };

        private void RefreshAfterSlotReorder()
        {
            UpdatePadDeviceInfo();

            // Rebuild mapping item collections and reload PadSettings into ViewModels.
            // RebuildMappings must come first: SwapPadViewModelSlotData swaps OutputType
            // and VJoyConfig, but when both slots are vJoy (same OutputType), the setter's
            // SetProperty returns false and RebuildMappings is never called. This leaves
            // the wrong mapping layout (e.g., custom VJoyBtn0 items in an Xbox 360 preset slot).
            for (int i = 0; i < _mainVm.Pads.Count; i++)
                _mainVm.Pads[i].RebuildMappings();

            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                var selected = padVm.SelectedMappedDevice;
                if (selected != null && selected.InstanceGuid != Guid.Empty)
                {
                    LoadPadSettingToViewModel(padVm, selected.InstanceGuid);
                    PopulateAvailableInputs(padVm, FindUserDevice(selected.InstanceGuid));
                }
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

            try { Stop(); } catch { /* Best effort on shutdown */ }
            _disposed = true;
        }
    }
}
