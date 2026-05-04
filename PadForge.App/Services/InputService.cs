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

        /// <summary>Callback to toggle main window visibility. Set by MainWindow.</summary>
        public Action ToggleMainWindow { get; set; }

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
        private const double MacroRecordTimeoutSeconds = 5;
        private DateTime _macroRecordStartTime;
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

            // Create engine with the configured polling interval.
            _inputManager = new InputManager();
            _inputManager.PollingIntervalMs = _mainVm.Settings.PollingRateMs;
            _inputManager.HmInactivityTimeoutSeconds = _mainVm.Settings.HmInactivityDestroyTimeoutSeconds;

            // Copy controller types and per-slot configs immediately so Step 5
            // creates the correct VC types from the first polling cycle.
            for (int i = 0; i < InputManager.MaxPads && i < _mainVm.Pads.Count; i++)
            {
                _inputManager.SlotControllerTypes[i] = _mainVm.Pads[i].OutputType;
                _inputManager.SlotProfileIds[i] = _mainVm.Pads[i].ProfileId;
                SyncExtendedConfigToSlot(i, _mainVm.Pads[i]);
                _inputManager._midiConfigs[i] = _mainVm.Pads[i].MidiConfig;
                _inputManager._playStationConfigs[i] = _mainVm.Pads[i].PlayStationConfig;
                _inputManager._perDevicePlayStationConfigs[i] = _mainVm.Pads[i].PerDevicePlayStationConfigs;
                // Subscribe to PadVm's forwarder so the handler follows
                // the per-device anchor across SelectedMappedDevice
                // swaps, not just the initial config instance.
                _mainVm.Pads[i].ActivePlayStationConfigPropertyChanged += OnPlayStationConfigChanged;
            }

            // Subscribe to engine events (raised on background thread).
            _inputManager.DevicesUpdated += OnDevicesUpdated;
            _inputManager.FrequencyUpdated += OnFrequencyUpdated;
            _inputManager.ErrorOccurred += OnErrorOccurred;
            _inputManager.HmVcInactivityDestroyed += OnHmVcInactivityDestroyed;
            _inputManager.HmVcWentNonActive += OnHmVcWentNonActive;

            // Expose per-slot button bitmaps to the user-effects dispatcher
            // so InputReactive lightbar can detect button rising edges.
            // Bound to the manager via a captured field so .NET keeps the
            // delegate alive for the manager's lifetime.
            UserEffectsDispatcher.SlotButtonsProvider = padIndex =>
            {
                if (_inputManager == null) return (ushort)0;
                if (padIndex < 0 || padIndex >= InputManager.MaxPads) return (ushort)0;
                return _inputManager.CombinedOutputStates[padIndex].Buttons;
            };

            // Rumble bytes for the DS5/DS4 effect-packet path.
            //
            // ── CRITICAL: this MUST return the SAME audio-mixed +
            // gain-scaled values SDL is writing for THIS specific
            // device on THIS slot. ──
            //
            // Two writers reach the physical DS5/DS4 simultaneously:
            //   1. PadForge's UserEffectsDispatcher writes raw HID effect
            //      packets via Ds5RawHidWriter — at most 30 Hz, but
            //      ONLY while an animated lightbar mode is selected
            //      (the AnimTickMs timer). Otherwise it fires only on
            //      UI PropertyChanged events and is effectively idle.
            //   2. SDL3's PS5 driver writes effect packets every time
            //      SDL_RumbleJoystick is called from
            //      ForceFeedbackState.SetDeviceForces — once per
            //      polling tick (300+ Hz typical) carrying audio-mixed
            //      bytes from per-device ScaleRumbleForDevice.
            //
            // Per Ds5RawHidWriter's docstring: "the firmware applies
            // whichever WriteFile lands most recently." If the two
            // writers carry DIFFERENT rumble bytes, the firmware sees
            // them alternate and motors stutter. That's perceived as
            // weak.
            //
            // Empirical evidence (2026-05-04 user A/B):
            //   - Test rumble (raw VibrationStates = 65535): both
            //     writers carry 255. Motors steady → STRONG. Works
            //     with animated lightbar concurrent.
            //   - Audio rumble alone (no animated lightbar): dispatcher
            //     idle, only SDL writes audio-mixed values. Motors
            //     steady at audio level → STRONG.
            //   - Audio rumble + animated lightbar (DS5 OR DS4 on same
            //     slot): if dispatcher writes 0 while SDL writes audio
            //     peak, motors pulse 30 Hz → WEAK (the bug user
            //     reported as "Otherwise, it gets weak").
            //
            // The fix: dispatcher must compute per-device audio-mix +
            // gain just like ApplyForceFeedback does for its SDL
            // writes. Same input (raw VibrationStates), same per-device
            // PadSetting, same ScaleRumbleForDevice path → identical
            // output bytes, no pulse race regardless of which writer
            // lands last.
            //
            // Per-DEVICE (not slot's selected device) is mandatory:
            // when DS5 + DS4 share a slot with different audio-rumble
            // gains, each must see its own setting. Slot-level
            // FinalVibrationStates pulls only the slot's selected
            // device PadSetting and is wrong for the other device on
            // the same slot.
            //
            // Vibration structs use ushort (0..65535); DS5/DS4 firmware
            // takes byte (0..255), so shift down 8 bits.
            UserEffectsDispatcher.SlotRumbleForDeviceProvider = (padIndex, deviceGuid) =>
            {
                if (_inputManager == null) return ((byte)0, (byte)0);
                if (padIndex < 0 || padIndex >= InputManager.MaxPads) return ((byte)0, (byte)0);
                var raw = _inputManager.VibrationStates[padIndex];
                if (raw == null) return ((byte)0, (byte)0);

                PadSetting devicePs = null;
                var settings = SettingsManager.UserSettings;
                if (settings != null && deviceGuid != Guid.Empty)
                {
                    lock (settings.SyncRoot)
                    {
                        for (int i = 0; i < settings.Items.Count; i++)
                        {
                            var us = settings.Items[i];
                            if (us == null) continue;
                            if (us.MapTo != padIndex) continue;
                            if (us.InstanceGuid != deviceGuid) continue;
                            devicePs = us.GetPadSetting();
                            break;
                        }
                    }
                }

                _inputManager.ScaleRumbleForDevice(
                    raw.LeftMotorSpeed, raw.RightMotorSpeed,
                    devicePs, out ushort scaledL, out ushort scaledR);
                return ((byte)(scaledR >> 8), (byte)(scaledL >> 8));
            };

            // Slot's raw rumble for change-detection inside the audio
            // dispatch tick — see SlotRawRumbleProvider docs.
            UserEffectsDispatcher.SlotRawRumbleProvider = padIndex =>
            {
                if (_inputManager == null) return ((byte)0, (byte)0);
                if (padIndex < 0 || padIndex >= InputManager.MaxPads) return ((byte)0, (byte)0);
                var raw = _inputManager.VibrationStates[padIndex];
                if (raw == null) return ((byte)0, (byte)0);
                return ((byte)(raw.RightMotorSpeed >> 8), (byte)(raw.LeftMotorSpeed >> 8));
            };

            // Active test-rumble target for the slot, so the dispatcher's
            // device loop zeros rumble bytes on every Sony device whose
            // GUID doesn't match — same scoping that Step 2 already applies
            // for the SDL physical-rumble path.
            UserEffectsDispatcher.TestRumbleTargetGuidProvider = padIndex =>
            {
                if (_inputManager == null) return Guid.Empty;
                if (padIndex < 0 || padIndex >= InputManager.MaxPads) return Guid.Empty;
                return _inputManager.TestRumbleTargetGuid[padIndex];
            };

            // Per-(slot, device) lightbar configs — drives the
            // dispatcher's per-device synthesis loop and per-device
            // pulse rolls. Lighting tab is per-device (parallel to
            // PadSetting), so two DualSenses on the same slot can have
            // different LightbarMode / colors / palette.
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider = padIndex =>
            {
                if (_inputManager == null) return null;
                if (padIndex < 0 || padIndex >= InputManager.MaxPads) return null;
                return _inputManager._perDevicePlayStationConfigs[padIndex];
            };

            // Subscribe to settings/dashboard property changes for runtime propagation.
            _mainVm.Settings.PropertyChanged += OnSettingsPropertyChanged;
            _mainVm.Dashboard.PropertyChanged += OnDashboardPropertyChanged;
            _mainVm.Dashboard.ResetTouchpadOverlayPositionRequested += OnResetTouchpadOverlayPosition;

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

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;

            // UI-bound housekeeping (timer, event subscriptions, overlay
            // windows, foreground monitor) — dispatch via _dispatcher so
            // this method is safe to call from a worker thread (e.g. the
            // engine-toggle button wraps Stop in Task.Run to keep the UI
            // responsive during the multi-second HM kernel teardown).
            // _dispatcher.Invoke runs inline if we're already on the UI
            // thread, so the app-close path (which calls Dispose from a
            // Task.Run) doesn't double-marshal.
            _dispatcher.Invoke(() =>
            {
                // Stop UI timer (DispatcherTimer must be stopped on the
                // dispatcher that owns it).
                if (_uiTimer != null)
                {
                    _uiTimer.Stop();
                    _uiTimer.Tick -= UiTimer_Tick;
                    _uiTimer = null;
                }

                // Unsubscribe from ViewModel property changes (event
                // subscriptions are thread-safe but the surrounding state
                // touches PadVMs, so we keep this on the UI thread for
                // the per-pad iteration).
                _mainVm.Settings.PropertyChanged -= OnSettingsPropertyChanged;
                _mainVm.Dashboard.PropertyChanged -= OnDashboardPropertyChanged;
                _mainVm.Dashboard.ResetTouchpadOverlayPositionRequested -= OnResetTouchpadOverlayPosition;
                _mainVm.Devices.PropertyChanged -= OnDevicesVmPropertyChanged;

                foreach (var padVm in _mainVm.Pads)
                {
                    padVm.SelectedDeviceChanged -= OnSelectedDeviceChanged;
                    padVm.MappingsRebuilt -= OnMappingsRebuilt;
                }

                // Close overlay windows (not just hide — prevents shutdown hang).
                if (_touchpadOverlay != null)
                {
                    _touchpadOverlay.PositionChanged -= OnTouchpadOverlayPositionChanged;
                    _touchpadOverlay.Close();
                    _touchpadOverlay = null;
                }
                if (_switchOverlay != null)
                {
                    _switchOverlay.StopTimers();
                    _switchOverlay.Close();
                    _switchOverlay = null;
                }
            });

            // Background-safe: foreground monitor, servers, audio detector,
            // device hiding teardown.  None of these touch WPF VMs or UI
            // controls.
            if (_foregroundMonitor != null)
            {
                _foregroundMonitor.ProfileSwitchRequired -= OnProfileSwitchRequired;
                _foregroundMonitor = null;
            }
            StopDsuServer();
            StopWebServer();
            StopAudioBassDetector();
            RemoveDeviceHiding();

            // Heavy engine teardown — InputManager.Stop calls
            // AwaitPendingLifecycleTasks (waits for in-flight HM connect /
            // dispose tasks), DestroyAllVirtualControllers, and
            // DisposeHMaestroContextOnShutdown.  Each can take many
            // seconds.  Runs on whatever thread Stop was called from;
            // engine-toggle button wraps this whole method in Task.Run
            // for that reason.
            if (_inputManager != null)
            {
                _inputManager.DevicesUpdated -= OnDevicesUpdated;
                _inputManager.FrequencyUpdated -= OnFrequencyUpdated;
                _inputManager.ErrorOccurred -= OnErrorOccurred;
                _inputManager.HmVcInactivityDestroyed -= OnHmVcInactivityDestroyed;
                _inputManager.HmVcWentNonActive -= OnHmVcWentNonActive;
                foreach (var pad in _mainVm.Pads)
                    pad.ActivePlayStationConfigPropertyChanged -= OnPlayStationConfigChanged;
                _inputManager.Stop();
                _inputManager.Dispose();
                _inputManager = null;
                UserEffectsDispatcher.SlotButtonsProvider = null;
                UserEffectsDispatcher.SlotRumbleForDeviceProvider = null;
                UserEffectsDispatcher.SlotRawRumbleProvider = null;
                UserEffectsDispatcher.TestRumbleTargetGuidProvider = null;
                UserEffectsDispatcher.SlotPerDeviceConfigsProvider = null;
            }

            // Final UI-thread VM updates: marshal back to the dispatcher
            // so a Task.Run caller sees its visible "Stopped" state
            // without WPF cross-thread errors.
            _dispatcher.Invoke(() =>
            {
                _mainVm.IsEngineRunning = false;
                _mainVm.Dashboard.EngineStateKey = "Stopped";
                _mainVm.Dashboard.EngineStatus = Strings.Instance.Common_Stopped;
                _mainVm.Dashboard.PollingFrequency = 0;
                _mainVm.Dashboard.OnlineDevices = 0;
                _mainVm.PollingFrequency = 0;
                _mainVm.StatusText = Strings.Instance.Status_EngineStopped;
                _mainVm.RefreshCommands();

                // Clear "Initializing" indicators on dashboard cards and
                // sidebar nav items.  Engine-side _slotInitializing[] is
                // also cleared inside InputManager.Stop for symmetry;
                // this is the bound-to-visual companion.
                foreach (var slot in _mainVm.Dashboard.SlotSummaries)
                    slot.IsInitializing = false;
                foreach (var nav in _mainVm.NavControllerItems)
                    nav.IsInitializing = false;
            });

            // Mark all device rows offline so indicators turn gray.
            _dispatcher.Invoke(() =>
            {
                foreach (var row in _mainVm.Devices.Devices)
                    row.IsOnline = false;
                _mainVm.Devices.RefreshCounts();
            });
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

            // ── Handle macro-requested profile switch ──
            string pendingSwitch = _inputManager.PendingProfileSwitchId;
            if (pendingSwitch != "\0")
            {
                bool isManual = _inputManager.PendingProfileSwitchIsManual;
                _inputManager.PendingProfileSwitchId = "\0";
                _inputManager.PendingProfileSwitchIsManual = false;

                if (isManual && _foregroundMonitor != null)
                    _foregroundMonitor.SetManualOverride(SettingsManager.ActiveProfileId);

                OnProfileSwitchRequired(pendingSwitch);
                ShowProfileSwitchOverlay(pendingSwitch);
                _settingsService?.MarkDirty();
            }

            // ── Handle macro-requested window toggle ──
            if (_inputManager.PendingToggleWindow)
            {
                _inputManager.PendingToggleWindow = false;
                ToggleMainWindow?.Invoke();
            }

            // ── Update Pad ViewModels ──
            for (int i = 0; i < InputManager.MaxPads && i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                var gp = _inputManager.CombinedOutputStates[i];
                // Meter reads post-mix-post-gain values so the activity bars
                // match what the physical device and the DS5/DS4 effect
                // packet are sending.
                var vibration = _inputManager.FinalVibrationStates[i];

                padVm.UpdateFromEngineState(gp, vibration);
                padVm.UpdateFromTouchpadState(_inputManager.CombinedTouchpadStates[i]);

                // For custom Extended slots, also push the combined ExtendedRawState.
                if (_inputManager.SlotExtendedIsCustom[i])
                    padVm.UpdateFromExtendedRawState(_inputManager.CombinedExtendedRawStates[i]);

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
                        if (_inputManager.SlotExtendedIsCustom[i] && us != null)
                            padVm.UpdateFromExtendedDeviceState(us.ExtendedRawOutputState);
                        else
                            padVm.UpdateDeviceState(us?.RawMappedState ?? default);
                    }
                    else if (_inputManager.SlotExtendedIsCustom[i])
                    {
                        // No device selected: fall back to combined for the
                        // stick/trigger tabs so they aren't stuck on stale
                        // per-device data from a previous selection.
                        padVm.UpdateFromExtendedDeviceState(_inputManager.CombinedExtendedRawStates[i]);
                    }
                    else
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

            int xboxCount = 0, playstationCount = 0, extendedCount = 0, midiCount = 0, globalCount = 0;
            foreach (var slot in dash.SlotSummaries)
            {
                globalCount++;
                slot.SlotNumber = globalCount;

                var padVm = _mainVm.Pads[slot.PadIndex];
                padVm.SlotNumber = globalCount;
                slot.OutputType = padVm.OutputType;

                switch (padVm.OutputType)
                {
                    case VirtualControllerType.PlayStation:
                        playstationCount++;
                        slot.TypeInstanceLabel = playstationCount.ToString();
                        break;
                    case VirtualControllerType.Extended:
                        extendedCount++;
                        slot.TypeInstanceLabel = extendedCount.ToString();
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
                nav.IsVirtualControllerConnected = _inputManager?.IsVirtualControllerConnected(padIndex) ?? false;
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
                int povCount = Math.Min(ud.CapPovCount, CustomInputState.MaxPovs);
                bool isKb = ud.CapType == InputDeviceType.Keyboard;
                bool isMouse = ud.CapType == InputDeviceType.Mouse;
                bool isTouchpad = ud.CapType == InputDeviceType.Touchpad;
                int[] btnIndices = ResolveButtonIndices(ud);
                devVm.RebuildRawStateCollections(axisCount, btnIndices, povCount, isKb, isMouse, isTouchpad);
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
                int povCount = Math.Min(ud.CapPovCount, CustomInputState.MaxPovs);
                bool isKb = ud.CapType == InputDeviceType.Keyboard;
                bool isMouse = ud.CapType == InputDeviceType.Mouse;
                bool isTouchpad2 = ud.CapType == InputDeviceType.Touchpad;
                int[] btnIndices = ResolveButtonIndices(ud);
                devVm.RebuildRawStateCollections(axisCount, btnIndices, povCount, isKb, isMouse, isTouchpad2);
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
                {
                    var item = devVm.RawButtons[i];
                    int idx = item.Index;
                    item.IsPressed = idx >= 0 && idx < state.Buttons.Length && state.Buttons[idx];
                }
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

            // Update touchpad finger positions and click state.
            if (ud.HasTouchpad || ud.IsTouchpad)
            {
                devVm.TouchpadX0 = state.TouchpadFingers[0];
                devVm.TouchpadY0 = state.TouchpadFingers[1];
                devVm.TouchpadDown0 = state.TouchpadDown[0];
                devVm.TouchpadX1 = state.TouchpadFingers[3];
                devVm.TouchpadY1 = state.TouchpadFingers[4];
                devVm.TouchpadDown1 = state.TouchpadDown[1];
                devVm.TouchpadClickPressed = state.TouchpadClick;
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

            // Touchpad descriptors: "Touchpad N Finger M X/Y/Down" or "Touchpad N Click".
            if (s.StartsWith("Touchpad", StringComparison.Ordinal))
            {
                // Parse finger index and axis from descriptor.
                // Format: "Touchpad 0 Finger 0 X", "Touchpad 0 Finger 1 Down", "Touchpad 0 Click"
                if (s.Contains("Finger 0 X")) return (int)(state.TouchpadFingers[0] * 1000);
                if (s.Contains("Finger 0 Y")) return (int)(state.TouchpadFingers[1] * 1000);
                if (s.Contains("Finger 0 Down")) return state.TouchpadDown[0] ? 1 : 0;
                if (s.Contains("Finger 1 X")) return (int)(state.TouchpadFingers[3] * 1000);
                if (s.Contains("Finger 1 Y")) return (int)(state.TouchpadFingers[4] * 1000);
                if (s.Contains("Finger 1 Down")) return state.TouchpadDown[1] ? 1 : 0;
                if (s.EndsWith(" Click", StringComparison.Ordinal)) return state.TouchpadClick ? 1 : 0;
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
        /// Pushes ViewModel slider values (deadzones, force feedback, linear)
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

                // Sync output type and per-slot config to engine (always, even when no device is selected).
                if (_inputManager != null && i < InputManager.MaxPads)
                {
                    _inputManager.SlotControllerTypes[i] = padVm.OutputType;
                    _inputManager.SlotProfileIds[i] = padVm.ProfileId;
                    SyncExtendedConfigToSlot(i, padVm);
                    _inputManager._midiConfigs[i] = padVm.MidiConfig;
                    _inputManager._playStationConfigs[i] = padVm.PlayStationConfig;
                    // Per-(slot, device) lighting configs — source of
                    // truth for the dispatcher's per-device synthesis and
                    // macro lightbar fan-out. Mirroring is a reference
                    // copy (shared dictionary instance), so config edits
                    // on the UI thread are visible to the polling thread
                    // without an extra sync step.
                    _inputManager._perDevicePlayStationConfigs[i] = padVm.PerDevicePlayStationConfigs;
                }

                if (SettingsManager.SlotCreated[i] && padVm.AudioRumbleEnabled)
                    anyAudioRumble = true;

                var selected = padVm.SelectedMappedDevice;
                if (selected == null || selected.InstanceGuid == Guid.Empty)
                {
                    if (_inputManager != null && i < InputManager.MaxPads)
                        _inputManager.SelectedDeviceGuids[i] = Guid.Empty;
                    continue;
                }

                SaveViewModelToPadSetting(padVm, selected.InstanceGuid, syncMappings: false);

                // Mirror SelectedMappedDevice to the polling thread so
                // ComputeFinalVibrationStates can read the user's selected
                // device PadSetting for the meter, and the per-device
                // rumble paths (SDL + DS5 dispatcher) can resolve each
                // mapped device's own PadSetting independently.
                if (_inputManager != null && i < InputManager.MaxPads)
                    _inputManager.SelectedDeviceGuids[i] = selected.InstanceGuid;
            }

            // Start/stop audio bass detector when per-slot enable changes.
            if (anyAudioRumble != _lastAudioRumbleAnyEnabled)
            {
                _lastAudioRumbleAnyEnabled = anyAudioRumble;
                SyncAudioBassDetector();
            }
        }

        /// <summary>
        /// Syncs a PadViewModel's per-slot custom controller layout to the
        /// InputManager. The Extended pipeline reads these counts to translate
        /// per-mapping output into raw HID report indices.
        /// </summary>
        private void SyncExtendedConfigToSlot(int slotIndex, PadViewModel padVm)
        {
            if (_inputManager == null || slotIndex >= InputManager.MaxPads) return;
            var cfg = padVm.ExtendedConfig;

            // Resolve the effective label for the OEM-name override and the
            // custom ProductString. cfg.ProductString is empty until the user
            // explicitly edits the textbox; fall back to the active profile's
            // catalog ProductString so toggling OEM override alone (without
            // typing anything) still picks up a meaningful label from the
            // same value the UI is showing.
            string effectiveLabel = cfg.ProductString ?? string.Empty;
            if (string.IsNullOrEmpty(effectiveLabel))
            {
                var profile = HMaestroProfileCatalog.GetProfileById(padVm.ProfileId);
                effectiveLabel = !string.IsNullOrEmpty(profile?.ProductString)
                    ? profile.ProductString
                    : profile?.Name ?? string.Empty;
            }

            bool customize = padVm.OutputType == VirtualControllerType.Extended && cfg.Customize;

            // Layout counts must always flow through — Step 3 reads them to
            // populate ExtendedRawState's axes/buttons/POVs from the
            // per-mapping targets. Zeroing them when Customize is off would
            // silently drop every mapped button/axis for a non-customized
            // Extended slot because Step 3's population loops are bounded by
            // these counts. The values come from ExtendedConfig which
            // SyncExtendedConfigFromProfile already seeds to match the
            // active profile's HID descriptor when a profile is selected.
            _inputManager.SlotCustomLayouts[slotIndex] = new CustomControllerLayout
            {
                Axes = cfg.TotalAxes,
                Buttons = cfg.ButtonCount,
                Povs = cfg.PovCount,
                Sticks = cfg.ThumbstickCount,
                Triggers = cfg.TriggerCount
            };
            // Extended always produces raw HID axes/buttons per the active
            // HIDMaestro profile; the gate is OutputType alone.
            _inputManager.SlotExtendedIsCustom[slotIndex] =
                padVm.OutputType == VirtualControllerType.Extended;

            // The Customize flag gates only the override-producing paths
            // (custom HMProfile build, OEM name override). When off we still
            // push the label value so it's available if Customize later
            // flips on without re-editing, but SlotExtendedCustomize tells
            // CreateHMaestroController and ApplyLiveOemOverrideUpdates to
            // ignore it until the user opts in.
            _inputManager.SlotExtendedCustomize[slotIndex] = customize;
            _inputManager.SlotOemOverrideEnabled[slotIndex] = customize && cfg.OemNameOverride;
            _inputManager.SlotOemOverrideLabel[slotIndex] = customize ? effectiveLabel : string.Empty;
            // FFB toggle is Customize-gated, same shape as OemNameOverride /
            // OemOverrideLabel above: push the user's value through only when
            // Customize is on; push the catalog default (true) when off, so
            // the engine treats an uncustomized slot as the catalog profile
            // says regardless of any sticky non-default value the user set
            // earlier with Customize on. cfg.ForceFeedbackEnabled stays on the
            // VM for restoration when Customize comes back on. Step 5 detects
            // a flip vs the applied snapshot and triggers destroy + recreate
            // so HIDMaestro regenerates the descriptor with or without the
            // PID block to match.
            _inputManager.SlotExtendedFfbEnabled[slotIndex] = customize ? cfg.ForceFeedbackEnabled : true;
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

            // Issue #50: all double→string conversions MUST use InvariantCulture.
            // Without it, locales like German produce "20,5" (comma separator),
            // which the load-side TryParseDouble (InvariantCulture, expects "20.5")
            // silently fails on → returns 0 → the 30Hz sync loop overwrites the
            // user's setting with 0, destroying it permanently.
            //
            // WARNING: if you add a new double property below, use .ToString(ic)
            // — NOT bare .ToString(). Bare ToString is locale-sensitive and will
            // silently destroy user settings on non-English systems.
            var ic = System.Globalization.CultureInfo.InvariantCulture;

            // Dead zones (independent X/Y).
            ps.LeftThumbDeadZoneX = padVm.LeftDeadZoneX.ToString(ic);
            ps.LeftThumbDeadZoneY = padVm.LeftDeadZoneY.ToString(ic);
            ps.RightThumbDeadZoneX = padVm.RightDeadZoneX.ToString(ic);
            ps.RightThumbDeadZoneY = padVm.RightDeadZoneY.ToString(ic);

            // Dead zone shapes (enum — not affected, but consistent).
            ps.LeftThumbDeadZoneShape = padVm.LeftDeadZoneShape.ToString();
            ps.RightThumbDeadZoneShape = padVm.RightDeadZoneShape.ToString();

            // Anti-deadzones (per-axis).
            ps.LeftThumbAntiDeadZoneX = padVm.LeftAntiDeadZoneX.ToString(ic);
            ps.LeftThumbAntiDeadZoneY = padVm.LeftAntiDeadZoneY.ToString(ic);
            ps.RightThumbAntiDeadZoneX = padVm.RightAntiDeadZoneX.ToString(ic);
            ps.RightThumbAntiDeadZoneY = padVm.RightAntiDeadZoneY.ToString(ic);

            // Linear response.
            ps.LeftThumbLinear = padVm.LeftLinear.ToString(ic);
            ps.RightThumbLinear = padVm.RightLinear.ToString(ic);

            // Center offsets.
            ps.LeftThumbCenterOffsetX = padVm.LeftCenterOffsetX.ToString(ic);
            ps.LeftThumbCenterOffsetY = padVm.LeftCenterOffsetY.ToString(ic);
            ps.RightThumbCenterOffsetX = padVm.RightCenterOffsetX.ToString(ic);
            ps.RightThumbCenterOffsetY = padVm.RightCenterOffsetY.ToString(ic);

            // Max range.
            ps.LeftThumbMaxRangeX = padVm.LeftMaxRangeX.ToString(ic);
            ps.LeftThumbMaxRangeY = padVm.LeftMaxRangeY.ToString(ic);
            ps.RightThumbMaxRangeX = padVm.RightMaxRangeX.ToString(ic);
            ps.RightThumbMaxRangeY = padVm.RightMaxRangeY.ToString(ic);
            ps.LeftThumbMaxRangeXNeg = padVm.LeftMaxRangeXNeg.ToString(ic);
            ps.LeftThumbMaxRangeYNeg = padVm.LeftMaxRangeYNeg.ToString(ic);
            ps.RightThumbMaxRangeXNeg = padVm.RightMaxRangeXNeg.ToString(ic);
            ps.RightThumbMaxRangeYNeg = padVm.RightMaxRangeYNeg.ToString(ic);

            // Trigger deadzones.
            ps.LeftTriggerDeadZone = padVm.LeftTriggerDeadZone.ToString(ic);
            ps.RightTriggerDeadZone = padVm.RightTriggerDeadZone.ToString(ic);
            ps.LeftTriggerAntiDeadZone = padVm.LeftTriggerAntiDeadZone.ToString(ic);
            ps.RightTriggerAntiDeadZone = padVm.RightTriggerAntiDeadZone.ToString(ic);
            ps.LeftTriggerMaxRange = padVm.LeftTriggerMaxRange.ToString(ic);
            ps.RightTriggerMaxRange = padVm.RightTriggerMaxRange.ToString(ic);

            // Force feedback (int properties — not locale-affected).
            ps.ForceOverall = padVm.ForceOverallGain.ToString();
            ps.LeftMotorStrength = padVm.LeftMotorStrength.ToString();
            ps.RightMotorStrength = padVm.RightMotorStrength.ToString();
            ps.ForceSwapMotor = padVm.SwapMotors ? "1" : "0";

            // Audio bass rumble.
            ps.AudioRumbleEnabled = padVm.AudioRumbleEnabled ? "1" : "0";
            ps.AudioRumbleSensitivity = padVm.AudioRumbleSensitivity.ToString("F1", ic);
            ps.AudioRumbleCutoffHz = padVm.AudioRumbleCutoffHz.ToString("F0", ic);
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
                    if (target.StartsWith("Extended", StringComparison.Ordinal))
                    {
                        ps.SetExtendedMapping(target, mapping.SourceDescriptor ?? string.Empty);
                        if (mapping.NegSettingName != null)
                            ps.SetExtendedMapping(mapping.NegSettingName, mapping.NegSourceDescriptor ?? string.Empty);
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

                    // Save per-mapping deadzone.
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

            // Trigger deadzones.
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

                // Load per-mapping deadzone.
                string dzStr = ps.GetMappingDeadZone(target);
                mapping.MappingDeadZone = int.TryParse(dzStr, out int dz) && dz > 0 ? dz : 50;
            }
        }

        private static string GetMappingValue(PadSetting ps, string key)
        {
            if (key.StartsWith("Extended", StringComparison.Ordinal))
                return ps.GetExtendedMapping(key);
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
            VirtualControllerType sourceType, bool sourceIsExtended,
            VirtualControllerType targetType, bool targetIsExtended)
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
            ps.CopyFromTranslated(source, sourceType, sourceIsExtended, targetType, targetIsExtended);

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

            // The slot's PlayStationConfig anchor (PadVm.PlayStationConfig)
            // just swapped to the new device's per-device entry inside
            // BindPlayStationConfigForDevice. Re-attach the slot's HM
            // dispatcher so it follows the new anchor (and re-subscribes
            // its inner OnConfigChanged to the right instance).
            if (_inputManager != null && padVm.PadIndex >= 0 && padVm.PadIndex < InputManager.MaxPads)
            {
                var vcs = _inputManager.GetVirtualControllers();
                if (vcs != null && padVm.PadIndex < vcs.Length
                    && vcs[padVm.PadIndex] is HMaestroVirtualController hmVc)
                {
                    var anchor = padVm.PlayStationConfig;
                    if (anchor != null)
                        hmVc.AttachPlayStationConfig(anchor);
                }
            }
        }

        /// <summary>
        /// Called when a pad's mappings are rebuilt (e.g., OutputType or Extended preset changed).
        /// Reloads mapping descriptors from the PadSetting so auto-mapped inputs are preserved.
        /// Does NOT reload deadzone / force feedback settings — those are intentionally reset
        /// by PadViewModel.ResetDeadZoneSettings() when the OutputType or Extended preset changes.
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
        /// Unlike <see cref="LoadPadSettingToViewModel"/>, this does NOT touch deadzone,
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

                // Load per-mapping deadzone.
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

                // Re-push user-configured DS5 effects to every PlayStation
                // slot's assigned DualSense.  Catches the "DS5 disconnected
                // and reconnected mid-session" case — without this hook the
                // dispatcher only fires on PropertyChanged, so a fresh-
                // reconnected pad would sit at firmware default until the
                // user touched a slider.
                //
                // Always re-attach the slot's PlayStationSlotConfig before
                // re-applying. If the inactivity timeout tore down and
                // recreated the VC while the physical pad was unplugged,
                // the new VC's dispatcher needs a fresh bind. AttachPlayStationConfig
                // is idempotent (Rebind on existing dispatcher, construct
                // on null) and ApplyOnce runs internally so a single call
                // covers both the "still alive, push update" and "fresh
                // VC, first push" cases.
                if (_inputManager != null)
                {
                    var vcs = _inputManager.GetVirtualControllers();
                    if (vcs != null)
                    {
                        for (int i = 0; i < vcs.Length; i++)
                        {
                            if (vcs[i] is HMaestroVirtualController hmVc)
                            {
                                var psCfg = _inputManager._playStationConfigs[i];
                                if (psCfg != null)
                                    hmVc.AttachPlayStationConfig(psCfg);
                                hmVc.ReApplyUserEffects();
                            }
                        }

                        // Retry burst — SDL3's PS5 driver writes the
                        // player-index DEFAULT color to the lightbar at
                        // multiple points after a fresh open:
                        //   - Immediately on SDL_SetJoystickIDForPlayerIndex
                        //     (USB: hits firmware right away).
                        //   - On the first SDL_SendGamepadEffect call when
                        //     enhanced_mode is false (sets enhanced mode +
                        //     fires UpdateEffects(LED|PadLights), then
                        //     SDL_Delay(10) before sending our packet).
                        //   - For Bluetooth, CheckPendingLEDReset fires
                        //     UpdateEffects(LED|PadLights) with player-
                        //     default color when the BT sensor timestamp
                        //     hits ~10.2 seconds post-first-packet
                        //     (SDL_hidapi_ps5.c connection_complete = 10200000
                        //     microseconds).
                        // The early retries (250/750/1500ms) win against
                        // the synchronous USB writes; the late retries
                        // (3s/6s/12s/15s) win against the BT 10.2s
                        // CheckPendingLEDReset overwrite. Without the
                        // late retries, BT users see player-default color
                        // stick after late-connect even though our packets
                        // returned success at sub-2s.
                        ScheduleDelayedReApply(250);
                        ScheduleDelayedReApply(750);
                        ScheduleDelayedReApply(1500);
                        ScheduleDelayedReApply(3000);
                        ScheduleDelayedReApply(6000);
                        ScheduleDelayedReApply(12000);
                        ScheduleDelayedReApply(15000);
                    }
                }
            }));
        }

        private void ScheduleDelayedReApply(int delayMs)
        {
            System.Threading.Tasks.Task.Delay(delayMs).ContinueWith(_ =>
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_inputManager == null) return;
                    var vcs = _inputManager.GetVirtualControllers();
                    if (vcs == null) return;
                    for (int i = 0; i < vcs.Length; i++)
                    {
                        if (vcs[i] is HMaestroVirtualController hmVc)
                        {
                            var psCfg = _inputManager._playStationConfigs[i];
                            if (psCfg != null)
                                hmVc.AttachPlayStationConfig(psCfg);
                            hmVc.ReApplyUserEffects();
                        }
                    }
                }));
            });
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
        /// Raised on the UI thread after the engine reported an HM virtual
        /// controller's inactivity timeout fired. MainWindow listens and
        /// runs DeviceService.DeleteSlot + InputService.OnSlotDeleted with
        /// rebuildHmVcs:true so any surviving Xbox HM VCs at higher
        /// pad indices in the Xbox group bubble down to the lowest
        /// available kernel slot. Argument is the pad index that timed out.
        /// </summary>
        public event EventHandler<int> SlotInactivityTimedOut;

        /// <summary>
        /// Handle the engine's HM inactivity timeout. The slot stays
        /// created, enabled, mapped — only the live VC is torn down so
        /// its kernel slot frees up. The slot then sits in "awaiting
        /// devices" state; when its mapped devices come back online,
        /// Pass 2 recreates the VC automatically. The slot's data
        /// identity (PadSetting, UserSettings, SlotOrders position, etc.)
        /// is durable and never touched here. PadForge.xml is not
        /// modified.
        ///
        /// The bubble-down cascade fires for any HM-backed subgroup
        /// (Xbox / PlayStation / Extended) so surviving HM VCs at
        /// higher visual positions in the same group drop their kernel
        /// slot, matching the natural disconnect/reconnect shape an
        /// external observer would see (xinputhid for Xbox, DirectInput
        /// / SDL / raw HID for PlayStation and Extended — all care
        /// about creation order).
        /// </summary>
        public void OnSlotInactivityTimedOut(int padIndex)
        {
            if (_inputManager == null) return;
            if (padIndex < 0 || padIndex >= InputManager.MaxPads) return;
            if (!SettingsManager.SlotCreated[padIndex]) return;

            var slotType = _mainVm.Pads[padIndex].OutputType;

            try { _inputManager.DestroyVirtualControllerAsync(padIndex); }
            catch { /* best effort */ }

            RunBubbleDownCascadeFromPosition(padIndex, slotType);

            // Refresh UI status (slot will show as "awaiting devices").
            UpdatePadDeviceInfo();
        }

        private void OnHmVcInactivityDestroyed(object sender, int padIndex)
        {
            // Engine fires on the polling thread.  Marshal to the UI thread
            // before the listener does the actual delete + compact, since
            // those touch PadVMs, settings, and the swap pipeline.
            _dispatcher.BeginInvoke(new Action(() =>
            {
                SlotInactivityTimedOut?.Invoke(this, padIndex);
            }));
        }

        /// <summary>
        /// Engine fired <see cref="InputManager.HmVcWentNonActive"/> after
        /// destroying an HM VC for a non-delete reason (sidebar disable,
        /// all devices unassigned). The VC is already gone by the time
        /// this runs; the only job left is the bubble-down cascade
        /// across the slot's HM subgroup. Slot stays in its order list
        /// at the same position.
        /// </summary>
        private void OnHmVcWentNonActive(object sender, int padIndex)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (_inputManager == null) return;
                if (padIndex < 0 || padIndex >= InputManager.MaxPads) return;
                if (!SettingsManager.SlotCreated[padIndex]) return;
                var slotType = _mainVm.Pads[padIndex].OutputType;
                RunBubbleDownCascadeFromPosition(padIndex, slotType);
                UpdatePadDeviceInfo();
            }));
        }

        /// <summary>
        /// Shared bubble-down cascade for non-delete inactivity transitions
        /// (HM inactivity timeout, sidebar disable, all-devices-unassigned).
        /// The slot at <paramref name="padIndex"/> is still in its group's
        /// order list at its existing position; this method finds that
        /// position and async-destroys every surviving HM VC at a strictly
        /// higher position in the same subgroup. Pass 2 recreates them in
        /// ascending position order so each lands at a kernel slot one step
        /// lower than before.
        ///
        /// Applies to Xbox / PlayStation / Extended uniformly. MIDI and
        /// KeyboardMouse have no kernel-slot ordering concern and are
        /// no-ops here.
        /// </summary>
        private void RunBubbleDownCascadeFromPosition(int padIndex, VirtualControllerType slotType)
        {
            if (slotType != VirtualControllerType.Xbox
                && slotType != VirtualControllerType.PlayStation
                && slotType != VirtualControllerType.Extended)
            {
                return;
            }

            var order = SettingsManager.SlotOrders.GetOrderFor(slotType);
            int inactivePos = order.IndexOf(padIndex);
            if (inactivePos < 0) return;

            for (int p = inactivePos + 1; p < order.Count; p++)
            {
                int higherPad = order[p];
                if (!_inputManager.IsHmVcAt(higherPad)) continue;
                try { _inputManager.DestroyVirtualControllerAsync(higherPad); }
                catch { /* best effort, Pass 2 retries */ }
            }
        }

        /// <summary>
        /// Bubble-down cascade for the deletion path. The slot has already
        /// been removed from its group's order list, so we iterate by
        /// the captured pre-removal position: in the post-removal list,
        /// every entry at index &gt;= <paramref name="oldPosition"/> is
        /// a survivor that just shifted up by one position and needs its
        /// kernel slot to drop accordingly.
        ///
        /// Applies to Xbox / PlayStation / Extended uniformly.
        /// </summary>
        private void RunBubbleDownCascadeAfterDelete(VirtualControllerType deletedType, int oldPosition)
        {
            if (oldPosition < 0) return;
            if (deletedType != VirtualControllerType.Xbox
                && deletedType != VirtualControllerType.PlayStation
                && deletedType != VirtualControllerType.Extended)
            {
                return;
            }

            var order = SettingsManager.SlotOrders.GetOrderFor(deletedType);
            for (int p = oldPosition; p < order.Count; p++)
            {
                int survivor = order[p];
                if (!_inputManager.IsHmVcAt(survivor)) continue;
                try { _inputManager.DestroyVirtualControllerAsync(survivor); }
                catch { /* best effort, Pass 2 retries */ }
            }
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
            else if (e.PropertyName == nameof(SettingsViewModel.HmInactivityDestroyTimeoutSeconds) && _inputManager != null)
            {
                _inputManager.HmInactivityTimeoutSeconds = _mainVm.Settings.HmInactivityDestroyTimeoutSeconds;
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
            // ── CRITICAL: detector lifecycle is gated by per-device
            // PadSettings, NOT by the VM's AudioRumbleEnabled property. ──
            //
            // The VM property mirrors whichever device is currently
            // selected in the assigned-devices dropdown — it loads from
            // SelectedMappedDevice's PadSetting on selection switch.
            // If we keyed the detector off the VM, switching the
            // dropdown to a device that doesn't have audio rumble
            // enabled would STOP THE DETECTOR for the whole app, even
            // though another device on the slot still has it on. The
            // assigned-devices dropdown's job is JUST configuration —
            // its current selection must not change which slots
            // produce audio rumble at runtime.
            //
            // Walk the actual UserSetting → PadSetting storage instead.
            // Detector runs when ANY (device, slot) PadSetting has
            // AudioRumbleEnabled == "1", or when ANY slot's lightbar
            // mode is an audio-driven mode (audio-to-LED reuses this
            // capture).
            bool anyEnabled = false;
            var settings = SettingsManager.UserSettings;
            if (settings != null)
            {
                lock (settings.SyncRoot)
                {
                    for (int i = 0; i < settings.Items.Count; i++)
                    {
                        var us = settings.Items[i];
                        if (us == null) continue;
                        if (us.MapTo < 0 || us.MapTo >= InputManager.MaxPads) continue;
                        if (!SettingsManager.SlotCreated[us.MapTo]) continue;
                        var ps = us.GetPadSetting();
                        if (ps != null && ps.AudioRumbleEnabled == "1")
                        {
                            anyEnabled = true;
                            break;
                        }
                    }
                }
            }
            if (!anyEnabled)
            {
                // Audio-driven lightbar modes still gate on the slot's
                // SelectedMappedDevice PSConfig (per-device by design;
                // editing that lives on the Lighting tab which is also
                // per-device-bound). Walk PadViewModel.PerDevicePlayStationConfigs
                // so a non-selected device's audio-mode lightbar still
                // keeps the detector alive.
                for (int i = 0; i < _mainVm.Pads.Count && !anyEnabled; i++)
                {
                    if (!SettingsManager.SlotCreated[i]) continue;
                    var pad = _mainVm.Pads[i];
                    if (pad.PerDevicePlayStationConfigs == null) continue;
                    foreach (var kvp in pad.PerDevicePlayStationConfigs)
                    {
                        if (kvp.Value != null && IsAudioLightbarMode(kvp.Value.LightbarMode))
                        {
                            anyEnabled = true;
                            break;
                        }
                    }
                }
            }

            if (anyEnabled && _audioBassDetector == null)
                StartAudioBassDetector();
            else if (!anyEnabled && _audioBassDetector != null)
                StopAudioBassDetector();
        }

        private static bool IsAudioLightbarMode(ViewModels.LightbarMode? m) =>
            m is ViewModels.LightbarMode.AudioPulse
              or ViewModels.LightbarMode.AudioPulseRandom
              or ViewModels.LightbarMode.AudioPulseRainbow
              or ViewModels.LightbarMode.AudioThresholds
              or ViewModels.LightbarMode.AudioGradient
              or ViewModels.LightbarMode.AudioCrossFade;

        // Re-evaluate WASAPI capture on every LightbarMode change so the
        // detector starts the moment a user picks an audio mode and stops
        // when the last slot leaves audio. Without this hook, the gate
        // only re-evaluates on AudioRumble toggle changes.
        private void OnPlayStationConfigChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.PlayStationSlotConfig.LightbarMode))
                SyncAudioBassDetector();
        }

        private void StartAudioBassDetector()
        {
            if (_audioBassDetector != null || _inputManager == null)
                return;

            _audioBassDetector = new AudioBassDetector();

            if (_audioBassDetector.Start())
            {
                _inputManager.AudioBassDetector = _audioBassDetector;
                // Wire the dispatcher's peak provider — audio-to-lightbar
                // pulls from the same capture as audio-rumble, but reads
                // the pre-filter FullSpectrumPeak so the lightbar follows
                // the full waveform regardless of bass-cutoff.
                UserEffectsDispatcher.AudioPeakProvider =
                    () => _audioBassDetector?.FullSpectrumPeak ?? 0f;
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

            UserEffectsDispatcher.AudioPeakProvider = null;
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
                // Self-heal stale off-screen saves (e.g. from older builds
                // where centering on a scaled monitor pushed the window past
                // the physical edge, or a now-detached display).
                _touchpadOverlay.EnsureOnScreen(dash.TouchpadOverlayMonitor);
                dash.IsTouchpadOverlayRunning = true;

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
                    _mainVm.Dashboard.IsTouchpadOverlayRunning = false;
                }
                // Unregister the overlay device.
                if (_touchpadOverlayDevice != null)
                    _inputManager?.UnregisterExternalDevice(_touchpadOverlayDevice.InstanceGuid);
            });
        }

        /// <summary>Suppresses or resumes global macro evaluation (during shortcut recording).</summary>
        internal void SetSuppressGlobalMacros(bool suppress)
        {
            if (_inputManager != null) _inputManager.SuppressGlobalMacros = suppress;
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

        private void OnResetTouchpadOverlayPosition(object sender, EventArgs e)
        {
            _dispatcher.BeginInvoke(() =>
            {
                var dash = _mainVm.Dashboard;
                if (_touchpadOverlay != null && _touchpadOverlay.IsVisible)
                {
                    // Recenter live, then capture the new DIPs into settings.
                    _touchpadOverlay.MoveToMonitor(dash.TouchpadOverlayMonitor);
                    OnTouchpadOverlayPositionChanged();
                }
                else
                {
                    // Clear the saved coords so the next Show() takes the
                    // MoveToMonitor branch in ShowTouchpadOverlay.
                    dash.TouchpadOverlayLeft = -1;
                    dash.TouchpadOverlayTop = -1;
                }
            });
        }

        // ─────────────────────────────────────────────
        //  Profile switch overlay
        // ─────────────────────────────────────────────

        private Views.ProfileSwitchOverlay _switchOverlay;

        private void ShowProfileSwitchOverlay(string profileId)
        {
            string name = profileId != null
                ? SettingsManager.Profiles.Find(p => p.Id == profileId)?.Name
                : Strings.Instance.Common_Default;

            if (_switchOverlay == null)
            {
                _switchOverlay = new Views.ProfileSwitchOverlay();
                _switchOverlay.CheckInitState = CheckAllSlotsInitState;
                _switchOverlay.CheckAnyOffline = CheckAnyControllerOffline;
            }

            _switchOverlay.ShowProfileName(name ?? Strings.Instance.Common_Default);
        }

        private (bool anyInitializing, bool allReady) CheckAllSlotsInitState()
        {
            if (_inputManager == null)
                return (false, true);

            bool anyInit = false;
            bool allReady = true;

            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (!SettingsManager.SlotCreated[i] || !SettingsManager.SlotEnabled[i])
                    continue;

                if (_inputManager.IsVirtualControllerInitializing(i))
                {
                    anyInit = true;
                    allReady = false;
                }
                else if (!_inputManager.IsVirtualControllerConnected(i))
                {
                    allReady = false;
                }
            }

            return (anyInit, allReady);
        }

        /// <summary>
        /// Returns true if any created+enabled controller slot has no online
        /// physical devices assigned. Used by the flyout to show a warning
        /// after the "Active" state.
        /// </summary>
        private bool CheckAnyControllerOffline()
        {
            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (!SettingsManager.SlotCreated[i] || !SettingsManager.SlotEnabled[i])
                    continue;

                var slotSettings = SettingsManager.GetSettingsForSlot(i);
                if (slotSettings.Count == 0)
                    return true; // No devices assigned — controller is offline.

                bool anyOnline = false;
                var devices = SettingsManager.UserDevices;
                if (devices != null)
                {
                    lock (devices.SyncRoot)
                    {
                        foreach (var s in slotSettings)
                        {
                            foreach (var ud in devices.Items)
                            {
                                if (ud.InstanceGuid == s.InstanceGuid && ud.IsOnline)
                                {
                                    anyOnline = true;
                                    break;
                                }
                            }
                            if (anyOnline) break;
                        }
                    }
                }

                if (!anyOnline)
                    return true; // This controller has no online devices.
            }

            return false;
        }

        private void OnCultureChanged() => _dispatcher.BeginInvoke(() =>
        {
            RefreshServerStatusStrings();
            SyncDevicesList(); // Re-resolve localized device names (merged keyboards/mice/touchpads).
        });

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
                        // Match three transports:
                        //   USB:        HID\VID_054C&PID_0CE6\...           (underscore form)
                        //   BLE:        HID\{...}&DEV&VID_045E&PID_0B13&... (underscore form, GATT)
                        //   BT Classic: HID\{...}_VID&0002054c_PID&0ce6\... (ampersand form, BR/EDR over RFCOMM)
                        // The previous "VID_" substring check rejected BT Classic outright,
                        // so DualSense over Bluetooth was never blacklisted.
                        if (instanceId != null
                            && (instanceId.Contains("VID_", StringComparison.OrdinalIgnoreCase)
                                || instanceId.Contains("VID&", StringComparison.OrdinalIgnoreCase)))
                        {
                            // Expand to base-container + sibling HIDs, mirroring
                            // HidHide Configuration Client. Without this, only
                            // the SDL-visible HID interface gets blacklisted —
                            // XInput / WGI continue to see the controller via
                            // the XUSB base container or other HID children
                            // (Xbox 360 wired exposes an XUSB-class parent
                            // with multiple HID descendants).
                            foreach (var id in HidHideController.ExpandToBaseContainerAndChildren(instanceId))
                                desiredIds.Add(id);
                        }
                        // Fallback: synthetic paths (e.g., "XInput#0") — look up by VID/PID.
                        else if (ud.VendorId > 0 && ud.ProdId > 0)
                        {
                            var realIds = HidHideController.FindInstanceIdsByVidPid(
                                (ushort)ud.VendorId, (ushort)ud.ProdId);

                            // Scrub any HIDMaestro-manufactured instance IDs that
                            // got cached from a previous PadForge version whose
                            // FindInstanceIdsByVidPid didn't yet filter them.
                            // Without this scrub, pre-existing XML records keep
                            // blacklisting our own virtual devices via HidHide on
                            // every load, hiding them from DirectInput.
                            //
                            // First pass: collect VID&PID&IG signatures of any
                            // ROOT\VID_* siblings in the cached list. Those are
                            // HIDMaestro root devices — any HID\VID_ child sharing
                            // their VID/PID/IG combo is also HIDMaestro's.
                            var hmVidPidIgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var cachedId in ud.HidHideInstanceIds)
                            {
                                if (cachedId.StartsWith(@"ROOT\VID_", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Extract the "VID_XXXX&PID_YYYY&IG_NN" signature.
                                    int slash = cachedId.IndexOf('\\', 5);
                                    if (slash > 0)
                                        hmVidPidIgs.Add(cachedId.Substring(5, slash - 5));
                                }
                            }

                            for (int i = ud.HidHideInstanceIds.Count - 1; i >= 0; i--)
                            {
                                string cachedId = ud.HidHideInstanceIds[i];
                                bool scrub = HidHideController.IsHidMaestroDeviceInstance(cachedId);

                                if (!scrub && hmVidPidIgs.Count > 0
                                    && cachedId.StartsWith(@"HID\VID_", StringComparison.OrdinalIgnoreCase))
                                {
                                    int slash = cachedId.IndexOf('\\', 4);
                                    if (slash > 0)
                                    {
                                        string sig = cachedId.Substring(4, slash - 4);
                                        if (hmVidPidIgs.Contains(sig))
                                            scrub = true;
                                    }
                                }

                                if (scrub)
                                {
                                    ud.HidHideInstanceIds.RemoveAt(i);
                                    cacheUpdated = true;
                                }
                            }

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
                                    foreach (var expandedId in HidHideController.ExpandToBaseContainerAndChildren(id))
                                        desiredIds.Add(expandedId);
                            }
                            else if (ud.HidHideInstanceIds.Count > 0)
                            {
                                // Device is offline — use cached IDs to pre-emptively blacklist.
                                foreach (var cachedId in ud.HidHideInstanceIds)
                                    foreach (var expandedId in HidHideController.ExpandToBaseContainerAndChildren(cachedId))
                                        desiredIds.Add(expandedId);
                            }
                        }
                    }
                }

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
        ///   Virtual controllers (HIDMaestro today, or v2 ViGEm residue on
        ///   upgraders' machines) are already filtered out by Step 1
        ///   (IsHidMaestroVirtualDevice) via device path inspection. This
        ///   is a defense-in-depth layer that catches any that leak through.
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
        /// Virtual controllers (HIDMaestro today, v2 ViGEm residue on
        /// upgraders' machines) are primarily filtered at the engine level
        /// (Step 1, IsHidMaestroVirtualDevice). This is a defense-in-depth
        /// layer.
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
                           : ud.DevicePath == "overlay://touchpad" ? Strings.Instance.Dashboard_TouchpadOverlay
                           : ud.ResolvedName;
            row.ProductName = ud.ProductName;
            row.ProductGuid = ud.ProductGuid;
            row.VendorId = ud.VendorId;
            row.ProductId = ud.ProdId;
            row.IsOnline = ud.IsOnline;
            row.IsEnabled = ud.IsEnabled;
            row.IsHidden = ud.IsHidden;
            row.AxisCount = ud.CapAxeCount;
            // Prefer the live device's gated count (Xbox 360 → 11, Elite with paddles → 15+)
            // so the Devices summary doesn't always read 21 on SDL3 gamepads.
            // Falls back to CapButtonCount when the device is offline.
            int liveBtns = ud.Device?.SupportedButtonIndices?.Length ?? 0;
            row.ButtonCount = liveBtns > 0 ? liveBtns : ud.CapButtonCount;
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

                // Persist the resolved IDs onto the UserDevice so the details
                // pane can still show the instance path after the device goes
                // offline. FindInstanceIdsByVidPid only returns a result while
                // the device is physically attached; without this cache, a
                // disconnected XInput gamepad had no fallback and the path
                // went blank. Keyboards/mice already have non-XInput
                // DevicePaths that resolve via DevicePathToInstanceId so they
                // stayed populated when offline — this closes the gap for
                // XInput devices.
                if (realIds.Count > 0)
                {
                    ud.HidHideInstanceIds.Clear();
                    ud.HidHideInstanceIds.AddRange(realIds);
                }
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

                    // Per-device Lighting tab configs follow the mapped
                    // devices set. Newly-mapped devices get a fresh
                    // default lighting config (the user customizes each
                    // device's Lighting tab independently from there).
                    padVm.EnsurePlayStationConfigsForMappedDevices();

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

            // Build the dashboard's active-slot list by walking each group's
            // order list in fixed group order. Iterating ascending pad index
            // here would render the dashboard in pad-index order while the
            // sidebar renders in per-group order, so the two views would
            // disagree any time a slot was reordered or a pad index was
            // sparse within a group.
            var activeSlots = new List<int>();
            int totalActive = 0;
            foreach (var groupType in VirtualControllerGroups.InOrder)
            {
                foreach (int padIndex in SettingsManager.SlotOrders.GetOrderFor(groupType))
                {
                    if (padIndex < 0 || padIndex >= _mainVm.Pads.Count) continue;
                    if (!SettingsManager.SlotCreated[padIndex]) continue;
                    activeSlots.Add(padIndex);
                    totalActive++;
                }
            }
            bool canAddMore = totalActive < InputManager.MaxPads;
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
        /// Returns the button positions to surface in the Devices preview for
        /// <paramref name="ud"/>. When the live <c>ISdlInputDevice</c> is
        /// available, prefer its <c>SupportedButtonIndices</c> so SDL3 gamepads
        /// only show the extended slots (paddles, Misc1-6) the device actually
        /// has. Falls back to a dense 0..count-1 list (using RawButtonCount in
        /// raw passthrough mode, otherwise CapButtonCount) when the device is
        /// offline or doesn't expose a supported list.
        /// </summary>
        private static int[] ResolveButtonIndices(UserDevice ud)
        {
            int max = CustomInputState.MaxButtons;

            // Live SDL device: use its computed sparse list, capped at MaxButtons.
            // Raw passthrough mode bypasses the gamepad-aware filter and uses
            // the dense raw range so every native HID button is visible.
            if (ud.Device != null && !ud.ForceRawJoystickMode)
            {
                int[] sparse = ud.Device.SupportedButtonIndices;
                if (sparse != null && sparse.Length > 0)
                {
                    if (sparse[sparse.Length - 1] < max) return sparse;
                    var trimmed = new System.Collections.Generic.List<int>(sparse.Length);
                    foreach (int idx in sparse) if (idx < max) trimmed.Add(idx);
                    return trimmed.ToArray();
                }
            }

            int count = Math.Min(
                ud.ForceRawJoystickMode && ud.RawButtonCount > 0 ? ud.RawButtonCount : ud.CapButtonCount,
                max);
            if (count <= 0) return Array.Empty<int>();
            int[] dense = new int[count];
            for (int i = 0; i < count; i++) dense[i] = i;
            return dense;
        }

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

            // For Extended slots, send directional force instead of scalar rumble so FFB
            // devices (joysticks, wheels) push in the correct direction rather than
            // just rattling. Direction uses "force comes from" convention:
            // 9000 = from East = pushes left, 27000 = from West = pushes right.
            bool isExtended = _inputManager.SlotControllerTypes[padIndex] == VirtualControllerType.Extended;
            if (isExtended && (left != right))
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
                    if (isExtended)
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

            _macroRecordStartTime = DateTime.UtcNow;
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
                // Custom Extended button path.
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

            // Auto-stop after timeout.
            if ((DateTime.UtcNow - _macroRecordStartTime).TotalSeconds >= MacroRecordTimeoutSeconds)
            {
                StopMacroTriggerRecording();
                return;
            }

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
                // Capture only the CURRENT simultaneously-held set (not accumulated).
                var currentRawButtons = new HashSet<int>();
                var currentPovs = new HashSet<string>();
                Guid currentDeviceGuid = Guid.Empty;

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

                        var buttons = ud.InputState.Buttons;
                        int count = Math.Min(buttons.Length, ud.Device?.RawButtonCount ?? buttons.Length);
                        for (int i = 0; i < count; i++)
                        {
                            if (buttons[i])
                            {
                                if (currentDeviceGuid == Guid.Empty)
                                    currentDeviceGuid = ud.InstanceGuid;
                                currentRawButtons.Add(i);
                            }
                        }

                        var povs = ud.InputState.Povs;
                        if (povs != null)
                        {
                            for (int p = 0; p < povs.Length; p++)
                            {
                                if (povs[p] >= 0)
                                {
                                    if (currentDeviceGuid == Guid.Empty)
                                        currentDeviceGuid = ud.InstanceGuid;
                                    currentPovs.Add($"{p}:{povs[p]}");
                                }
                            }
                        }
                    }
                }

                // Replace the recorded set with the current frame's state.
                // Only update if something is pressed (keep last combo when released).
                if (currentRawButtons.Count > 0 || currentPovs.Count > 0)
                {
                    _recordedRawButtons = currentRawButtons;
                    _recordedPovs = currentPovs;
                    _recordingDeviceGuid = currentDeviceGuid;
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
                // Custom Extended: capture current frame's buttons (not accumulated).
                var rawState = _inputManager.CombinedExtendedRawStates[_recordingPadIndex];
                if (rawState.Buttons != null && _recordedCustomButtons != null)
                {
                    bool anyPressed = false;
                    for (int w = 0; w < rawState.Buttons.Length && w < _recordedCustomButtons.Length; w++)
                        if (rawState.Buttons[w] != 0) anyPressed = true;
                    if (anyPressed)
                        Array.Copy(rawState.Buttons, _recordedCustomButtons,
                            Math.Min(rawState.Buttons.Length, _recordedCustomButtons.Length));
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
                // Gamepad preset OutputController: capture current frame's buttons (not accumulated).
                var gp = _inputManager.CombinedOutputStates[_recordingPadIndex];
                ushort xboxButtons = gp.Buttons;
                if (xboxButtons != 0)
                    _recordedButtons = xboxButtons;

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
                // Extended raw state path.
                var rawState = _inputManager.CombinedExtendedRawStates[padIndex];
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
                SlotProfileIds = Enumerable.Range(0, _mainVm.Pads.Count)
                    .Select(i => _mainVm.Pads[i].ProfileId).ToArray(),
                ExtendedConfigs = SnapshotExtendedConfigs(),
                MidiConfigs = SnapshotMidiConfigs(),
                XboxSlotOrder          = SettingsManager.XboxSlotOrder.ToArray(),
                PlayStationSlotOrder   = SettingsManager.PlayStationSlotOrder.ToArray(),
                ExtendedSlotOrder      = SettingsManager.ExtendedSlotOrder.ToArray(),
                KeyboardMouseSlotOrder = SettingsManager.KeyboardMouseSlotOrder.ToArray(),
                MidiSlotOrder          = SettingsManager.MidiSlotOrder.ToArray(),
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
                TouchpadOverlayHeight = _mainVm.Dashboard.TouchpadOverlayHeight
            };
        }

        private ExtendedSlotConfigData[] SnapshotExtendedConfigs()
        {
            var list = new List<ExtendedSlotConfigData>();
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                if (!SettingsManager.SlotCreated[i] ||
                    _mainVm.Pads[i].OutputType != VirtualControllerType.Extended)
                    continue;
                var cfg = _mainVm.Pads[i].ExtendedConfig;
                list.Add(new ExtendedSlotConfigData
                {
                    SlotIndex = i,
                    ThumbstickCount = cfg.ThumbstickCount,
                    TriggerCount = cfg.TriggerCount,
                    PovCount = cfg.PovCount,
                    ButtonCount = cfg.ButtonCount,
                    OemNameOverride = cfg.OemNameOverride,
                    ProductString = cfg.ProductString,
                    Customize = cfg.Customize,
                    ForceFeedbackEnabled = cfg.ForceFeedbackEnabled
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

                    // HM profile slug. Step 5's per-slot diff
                    // (InputManager.Step5.VirtualDevices.cs:514-527) reads this
                    // via _inputManager.SlotProfileIds[i] and only destroys +
                    // recreates the live VC when the new slug differs from the
                    // current HMaestroVirtualController.ProfileId. Slots whose
                    // HM slug matches across profiles stay live, pointer-stable.
                    // Skipping this apply leaves the slot stuck on the previous
                    // profile's slug, so the HM identity never switches.
                    if (willCreate
                        && profile.SlotProfileIds != null
                        && i < profile.SlotProfileIds.Length)
                    {
                        _mainVm.Pads[i].ProfileId = profile.SlotProfileIds[i];
                    }
                }
            }

            // ── Single-pass transition of device assignments ──
            // Each profile fully owns slot assignments. Avoid the reset-then-
            // rebuild shape (set every us.MapTo = -1, then reapply from
            // profile.Entries) — that opens a window where the polling thread
            // sees HasAnyDeviceMapped == false for surviving slots and falls
            // into the immediate-destroy path at
            // InputManager.Step5.VirtualDevices.cs:590-600, tearing down VCs
            // that should survive the switch (slots whose mapping is unchanged
            // between old and new profile would still get destroyed and
            // recreated needlessly, including kernel-slot reallocation and the
            // bubble-up cascade).
            //
            // Build the desired final assignment map first, then transition
            // each UserSetting directly: old → new MapTo for entries that
            // survive, or → -1 for entries dropped from the new profile.
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                var assignments = new System.Collections.Generic.Dictionary<UserSetting, (int MapTo, PadSetting Ps)>();
                var consumed = new System.Collections.Generic.HashSet<UserSetting>();

                if (profile.Entries != null && profile.Entries.Length > 0 &&
                    profile.PadSettings != null && profile.PadSettings.Length > 0)
                {
                    foreach (var entry in profile.Entries)
                    {
                        var template = profile.PadSettings
                            .FirstOrDefault(p => p.PadSettingChecksum == entry.PadSettingChecksum);
                        if (template == null) continue;

                        // Find a UserSetting for this entry, gated on
                        // "not yet consumed by a prior entry in this same
                        // apply pass" rather than the old MapTo<0 check —
                        // that check required the bulk reset we're avoiding.
                        // A device mapped to multiple slots in the new profile
                        // still claims one UserSetting per entry.
                        var us = SettingsManager.UserSettings.Items
                            .FirstOrDefault(s => s.InstanceGuid == entry.InstanceGuid && !consumed.Contains(s));

                        if (us == null && entry.ProductGuid != Guid.Empty)
                        {
                            us = SettingsManager.UserSettings.Items
                                .FirstOrDefault(s => s.ProductGuid == entry.ProductGuid && !consumed.Contains(s));
                        }

                        if (us == null)
                        {
                            us = new UserSetting
                            {
                                InstanceGuid = entry.InstanceGuid,
                                ProductGuid = entry.ProductGuid
                            };
                            SettingsManager.UserSettings.Items.Add(us);
                        }

                        consumed.Add(us);
                        assignments[us] = (entry.MapTo, template.CloneDeep());
                    }
                }

                foreach (var us in SettingsManager.UserSettings.Items)
                {
                    if (assignments.TryGetValue(us, out var assign))
                    {
                        us.SetPadSetting(assign.Ps);
                        us.MapTo = assign.MapTo;
                    }
                    else if (us.MapTo >= 0)
                    {
                        us.MapTo = -1;
                    }
                }
            }

            // ── Reconcile per-group order lists with the new topology ──
            // Profile activation has just reset SlotCreated and OutputType for
            // every slot, so the order lists must be rebuilt from the profile's
            // saved arrays (or ascending defaults if the profile predates them).
            SettingsManager.SlotOrders.RebuildFromCurrentTopology(
                pi => _mainVm.Pads[pi].OutputType,
                profile.XboxSlotOrder,
                profile.PlayStationSlotOrder,
                profile.ExtendedSlotOrder,
                profile.KeyboardMouseSlotOrder,
                profile.MidiSlotOrder);

            // ── Apply Extended/MIDI configurations ──
            if (profile.ExtendedConfigs != null)
            {
                foreach (var cfgData in profile.ExtendedConfigs)
                {
                    int idx = cfgData.SlotIndex;
                    if (idx >= 0 && idx < _mainVm.Pads.Count &&
                        SettingsManager.SlotCreated[idx] &&
                        _mainVm.Pads[idx].OutputType == VirtualControllerType.Extended)
                    {
                        var cfg = _mainVm.Pads[idx].ExtendedConfig;
                        cfg.ThumbstickCount = cfgData.ThumbstickCount;
                        cfg.TriggerCount = cfgData.TriggerCount;
                        cfg.PovCount = cfgData.PovCount;
                        cfg.ButtonCount = cfgData.ButtonCount;
                        cfg.OemNameOverride = cfgData.OemNameOverride;
                        cfg.ProductString = cfgData.ProductString ?? string.Empty;
                        cfg.Customize = cfgData.Customize;
                        cfg.ForceFeedbackEnabled = cfgData.ForceFeedbackEnabled;
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

            // ── Apply touchpad overlay settings ──
            _mainVm.Dashboard.EnableTouchpadOverlay = profile.EnableTouchpadOverlay;
            _mainVm.Dashboard.TouchpadOverlayOpacity = profile.TouchpadOverlayOpacity;
            _mainVm.Dashboard.TouchpadOverlayMonitor = profile.TouchpadOverlayMonitor;
            _mainVm.Dashboard.TouchpadOverlayLeft = profile.TouchpadOverlayLeft;
            _mainVm.Dashboard.TouchpadOverlayTop = profile.TouchpadOverlayTop;
            _mainVm.Dashboard.TouchpadOverlayWidth = profile.TouchpadOverlayWidth > 0
                ? profile.TouchpadOverlayWidth : 500;
            _mainVm.Dashboard.TouchpadOverlayHeight = profile.TouchpadOverlayHeight > 0
                ? profile.TouchpadOverlayHeight : 250;

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
                    profile.SlotProfileIds = snapshot.SlotProfileIds;
                    profile.ExtendedConfigs = snapshot.ExtendedConfigs;
                    profile.MidiConfigs = snapshot.MidiConfigs;
                    profile.XboxSlotOrder          = snapshot.XboxSlotOrder;
                    profile.PlayStationSlotOrder   = snapshot.PlayStationSlotOrder;
                    profile.ExtendedSlotOrder      = snapshot.ExtendedSlotOrder;
                    profile.KeyboardMouseSlotOrder = snapshot.KeyboardMouseSlotOrder;
                    profile.MidiSlotOrder          = snapshot.MidiSlotOrder;
                    profile.EnableDsuMotionServer = snapshot.EnableDsuMotionServer;
                    profile.DsuMotionServerPort = snapshot.DsuMotionServerPort;
                    profile.EnableWebController = snapshot.EnableWebController;
                    profile.WebControllerPort = snapshot.WebControllerPort;
                    profile.EnableTouchpadOverlay = snapshot.EnableTouchpadOverlay;
                    profile.TouchpadOverlayOpacity = snapshot.TouchpadOverlayOpacity;
                    profile.TouchpadOverlayMonitor = snapshot.TouchpadOverlayMonitor;
                    profile.TouchpadOverlayLeft = snapshot.TouchpadOverlayLeft;
                    profile.TouchpadOverlayTop = snapshot.TouchpadOverlayTop;
                    profile.TouchpadOverlayWidth = snapshot.TouchpadOverlayWidth;
                    profile.TouchpadOverlayHeight = snapshot.TouchpadOverlayHeight;
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
        /// Swap two slots' visual positions within their (shared) group.
        ///
        /// Pad indices are data identity (mappings, profile, devices,
        /// settings live at the pad index and never move). Visual position
        /// is the kernel-slot anchor: in an HM-backed group, the VC at
        /// visual position V holds kernel slot V. Swapping mutates
        /// <c>SettingsManager.SlotOrders</c> (the visual order), then
        /// routes through <see cref="RebuildKernelOrderAfterReorder"/> →
        /// <see cref="InputManager.RerouteVirtualControllersForReorder"/>,
        /// which decides per-position whether to reuse the VC at slot V
        /// (same profile, pure pointer swap) or destroy + recreate
        /// (profile mismatch).
        ///
        /// Cross-group calls are rejected; the upstream drag affordance
        /// already prevents them.
        /// </summary>
        public void SwapSlots(int padIndexA, int padIndexB)
        {
            if (padIndexA == padIndexB) return;
            if (padIndexA < 0 || padIndexA >= InputManager.MaxPads) return;
            if (padIndexB < 0 || padIndexB >= InputManager.MaxPads) return;

            var typeA = _mainVm.Pads[padIndexA].OutputType;
            var typeB = _mainVm.Pads[padIndexB].OutputType;
            if (typeA != typeB) return; // upstream drag affordance already enforces

            var oldOrder = SettingsManager.SlotOrders.GetOrderFor(typeA).ToList();
            SettingsManager.SlotOrders.SwapWithinGroup(padIndexA, padIndexB, typeA);
            RebuildKernelOrderAfterReorder(typeA, oldOrder);
            RefreshAfterSlotReorder();
        }

        /// <summary>
        /// Move a slot from its current visual position to a new visual
        /// position within its own group.
        ///
        /// Same model as <see cref="SwapSlots"/>: pad indices are data
        /// identity, visual position is the kernel-slot anchor.
        /// <c>SettingsManager.SlotOrders</c> mutates first, then
        /// <see cref="RebuildKernelOrderAfterReorder"/> →
        /// <see cref="InputManager.RerouteVirtualControllersForReorder"/>
        /// re-points the VC pointers position-by-position, reusing the
        /// kernel VC at each position when the profile matches and
        /// destroying + recreating only the positions whose profile
        /// changed.
        ///
        /// Cross-group moves go through <see cref="MoveSlotToGroupTail"/>;
        /// this method is intra-group only.
        /// </summary>
        public void MoveSlot(int sourcePadIndex, int targetVisualPosition)
        {
            if (sourcePadIndex < 0 || sourcePadIndex >= InputManager.MaxPads) return;
            if (!SettingsManager.SlotCreated[sourcePadIndex]) return;

            var groupType = _mainVm.Pads[sourcePadIndex].OutputType;
            var orderList = SettingsManager.SlotOrders.GetOrderFor(groupType);

            int sourcePos = orderList.IndexOf(sourcePadIndex);
            if (sourcePos < 0) return;
            if (targetVisualPosition < 0 || targetVisualPosition >= orderList.Count) return;
            if (sourcePos == targetVisualPosition) return;

            var oldOrder = orderList.ToList();
            SettingsManager.SlotOrders.MoveWithinGroup(groupType, sourcePos, targetVisualPosition);
            RebuildKernelOrderAfterReorder(groupType, oldOrder);
            RefreshAfterSlotReorder();
        }

        /// <summary>
        /// Re-route active VCs after a same-group visual reorder.
        /// Delegates to <see cref="InputManager.RerouteVirtualControllersForReorder"/>
        /// which walks <paramref name="oldOrder"/> against the new order
        /// position by position. Same-profile positions reuse their VC
        /// via a pointer-only swap; different-profile positions destroy
        /// the old VC and let Pass 2 recreate.
        ///
        /// Non-HM groups (KBM, MIDI) skip; their slot order is not tied
        /// to a kernel-side index allocation.
        /// </summary>
        private void RebuildKernelOrderAfterReorder(
            VirtualControllerType groupType,
            IReadOnlyList<int> oldOrder)
        {
            if (_inputManager == null) return;
            var newOrder = SettingsManager.SlotOrders.GetOrderFor(groupType);
            _inputManager.RerouteVirtualControllersForReorder(groupType, oldOrder, newOrder);
        }

        /// <summary>
        /// Move a slot to the tail of its (possibly new) group's order list.
        /// Used when the user changes a slot's type from the sidebar context
        /// menu or dashboard popup. The slot's pad index stays put; only the
        /// group membership changes. Step 5 Pass 1's existing type-mismatch
        /// detection destroys the old VC and Pass 2 creates the new one.
        /// Slots in OTHER groups are not touched.
        /// </summary>
        public void MoveSlotToGroupTail(int padIndex)
        {
            if (padIndex < 0 || padIndex >= InputManager.MaxPads) return;
            if (!SettingsManager.SlotCreated[padIndex]) return;

            var newType = _mainVm.Pads[padIndex].OutputType;

            // Find the group the slot is currently in (may differ from
            // newType if the caller already updated OutputType).
            VirtualControllerType? oldType = null;
            foreach (var g in VirtualControllerGroups.InOrder)
            {
                if (SettingsManager.SlotOrders.GetOrderFor(g).Contains(padIndex))
                {
                    oldType = g;
                    break;
                }
            }

            if (oldType == null)
            {
                // Slot was not in any group's list (newly created via a path
                // that didn't call SlotOrders.Add). Just append to its target
                // group's tail.
                SettingsManager.SlotOrders.Add(padIndex, newType);
                _settingsService?.MarkDirty();
                RefreshAfterSlotReorder();
                return;
            }

            if (oldType.Value == newType)
            {
                // Type didn't actually change. Nothing to move.
                return;
            }

            SettingsManager.SlotOrders.MoveToGroupTail(padIndex, oldType.Value, newType);
            _settingsService?.MarkDirty();
            RefreshAfterSlotReorder();
        }

        /// <summary>
        /// Called after a slot is deleted. <see cref="DeviceService.DeleteSlot"/>
        /// already removed the pad index from its group's order list; the
        /// caller passes the captured pre-removal position so the cascade
        /// knows which post-removal entries are survivors that just
        /// bubbled up.
        ///
        /// Applies the bubble-down cascade across the matching HM
        /// subgroup (Xbox / PlayStation / Extended). All three groups
        /// have observable creation-order semantics — xinputhid for
        /// Xbox, DirectInput / SDL / raw HID for PlayStation and
        /// Extended — so the cascade applies uniformly. MIDI and
        /// KeyboardMouse are no-ops here.
        /// </summary>
        public void OnSlotDeleted(int padIndex, VirtualControllerType deletedType, int oldGroupPosition, bool rebuildHmVcs = true)
        {
            if (rebuildHmVcs && _inputManager != null)
            {
                RunBubbleDownCascadeAfterDelete(deletedType, oldGroupPosition);
            }

            RefreshAfterSlotReorder();
        }

        private void RefreshAfterSlotReorder()
        {
            UpdatePadDeviceInfo();

            // Rebuild mapping item collections so each pad's mapping rows
            // match its current OutputType. RebuildMappings must run before
            // LoadPadSettingToViewModel because the latter populates rows
            // that the former rebuilds.
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

            // The per-group order lists drive the sidebar collection;
            // RefreshNavControllerItems detects sequence changes and
            // rebuilds NavControllerItems in the same step.
            _mainVm.RefreshNavControllerItems();

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
