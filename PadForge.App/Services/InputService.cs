using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
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
        // Reused across SlotRumbleForDeviceProvider invocations so the
        // dispatcher's per-device rumble pump doesn't allocate per tick.
        private Vibration _constantForceScratchSony;
        private Vibration _macroRumbleScratchSony;
        private Vibration _constantTriggerForceScratchSony;
        // #102 trigger-routing main-motor source for the Sony AT Vibration path.
        private Vibration _routeMainScratchSony;
        private Vibration _routeCfScratchSony;
        // #107 absolute cursor-position sampler (publishes SourceCoercion.MouseCursorProvider).
        private CursorControlService _cursorControlService;
        // #146 Wii Balance Board: per-board parsed kg calibration (12 floats) and
        // tare offset (kg), keyed by device GUID. Calibration is parsed once from
        // the SDL "balance_board_calibration" hex property; tare is captured on
        // demand. Both back the SourceCoercion.Balance* providers.
        private readonly System.Collections.Generic.Dictionary<Guid, float[]> _balanceCalibration = new();
        private readonly System.Collections.Generic.Dictionary<Guid, float> _balanceTareKg = new();
        private readonly object _balanceLock = new();
        private DispatcherTimer _uiTimer;
        private ForegroundMonitorService _foregroundMonitor;
        private ProfileData _defaultProfileSnapshot;

        // Active profile's touchpad custom-gesture working list. Mirrors
        // whichever profile is active so AddCustomTouchpadGesture /
        // DeleteCustomTouchpadGesture can mutate in place and the snapshot
        // path captures the result without a round-trip through profile
        // reload. ApplyProfileTouchpadGestures re-seeds this from the
        // incoming profile on every profile switch.
        private readonly List<PadForge.Engine.Touchpad.TouchpadCustomGesture> _activeTouchpadGestures = new();
        private DsuMotionServer _dsuServer;
        private WebControllerServer _webServer;
        private LinkServer _linkServer;
        private LinkDiscovery _linkDiscovery;
        private bool _remoteLinkConnectWired;
        private System.Threading.Timer _remoteLinkStreamTimer;
        private System.Threading.Timer _remoteLinkSyncTimer;
        private readonly List<(RemotePeerDeviceInfo info, ISdlInputDevice source, UserDevice ud, RemoteDeltaAccumulator acc, byte slot)> _remoteLinkExposed = new();
        private readonly object _remoteLinkExposedLock = new();
        // Lock-free snapshot of _remoteLinkExposed for the two hot readers
        // (the poll thread's per-tick delta accumulation and the 125 Hz
        // stream tick). Rebuilt under the exposed lock; readers only swap
        // the array reference.
        private volatile (RemotePeerDeviceInfo info, ISdlInputDevice source, UserDevice ud, RemoteDeltaAccumulator acc, byte slot)[] _remoteLinkExposedSnapshot =
            System.Array.Empty<(RemotePeerDeviceInfo, ISdlInputDevice, UserDevice, RemoteDeltaAccumulator, byte)>();
        // Per shared device, keyed by InstanceGuid so pending deltas survive
        // the 2 s device-list rebuild. Under the exposed lock.
        private readonly Dictionary<Guid, RemoteDeltaAccumulator> _remoteDeltaAccs = new();

        /// <summary>Per-poll delta bridge between the polling thread and the
        /// 125 Hz Remote Link stream tick (issue #138). The poll thread is the
        /// SOLE GetCurrentState caller (a second caller starved the wrappers'
        /// destructive mouse-delta / Joy-Con 2 baseline reads, splitting the
        /// motion between local mapping and the peer); the stream tick ships
        /// the published poll snapshot instead. Snapshot sampling at 125 Hz
        /// would drop the per-poll delta fields between ticks, so the poll
        /// thread sums them here and the tick drains the sum into the outgoing
        /// frame. The wire fields stay PER-POLL deltas (the consumer re-reads
        /// each frame once per consumer poll until the next lands), so the
        /// drain writes the average per accumulated poll, which reconstructs
        /// the same net motion the pre-fix residue split delivered remotely
        /// while the local mapping now sees the full stream.</summary>
        private sealed class RemoteDeltaAccumulator
        {
            private readonly object _sync = new();
            private CustomInputState _lastSeen;
            private long _dx, _dy, _scroll;
            private double _jc2dx, _jc2dy;
            private int _polls;

            /// <summary>Poll thread: fold one fresh snapshot in. The reference
            /// guard makes re-observing the same snapshot (idle mode, offline
            /// device) a no-op so a delta is never double-counted.</summary>
            public void Accumulate(CustomInputState s, bool isMouse)
            {
                if (s == null) return;
                lock (_sync)
                {
                    if (ReferenceEquals(_lastSeen, s)) return;
                    _lastSeen = s;
                    _polls++;
                    if (isMouse)
                    {
                        _dx += s.MouseRawDX;
                        _dy += s.MouseRawDY;
                        // SdlMouseWrapper: Axis[2] = 32767 + scroll * 128,
                        // integer-exact below the clamp, so this recovers the
                        // per-poll scroll count.
                        _scroll += (s.Axis[2] - 32767) / 128;
                    }
                    _jc2dx += s.JoyCon2MouseDX;
                    _jc2dy += s.JoyCon2MouseDY;
                }
            }

            /// <summary>Stream tick: overwrite the cloned outgoing frame's
            /// per-poll delta fields with the per-poll average since the last
            /// drain, then reset. Zero polls since the last drain means the
            /// deltas were already shipped, so the fields go neutral rather
            /// than repeating stale motion.</summary>
            public void DrainInto(CustomInputState s, bool isMouse)
            {
                lock (_sync)
                {
                    int n = _polls;
                    if (n <= 0)
                    {
                        if (isMouse)
                        {
                            s.MouseRawDX = 0; s.MouseRawDY = 0;
                            s.Axis[0] = 32767; s.Axis[1] = 32767; s.Axis[2] = 32767;
                        }
                        s.JoyCon2MouseDX = 0f; s.JoyCon2MouseDY = 0f;
                        return;
                    }
                    if (isMouse)
                    {
                        double adx = (double)_dx / n, ady = (double)_dy / n, asc = (double)_scroll / n;
                        s.MouseRawDX = (int)Math.Round(adx);
                        s.MouseRawDY = (int)Math.Round(ady);
                        s.Axis[0] = Math.Clamp(32767 + (int)(adx * 2048), 0, 65535);
                        s.Axis[1] = Math.Clamp(32767 + (int)(ady * 2048), 0, 65535);
                        s.Axis[2] = Math.Clamp(32767 + (int)(asc * 128), 0, 65535);
                    }
                    s.JoyCon2MouseDX = (float)(_jc2dx / n);
                    s.JoyCon2MouseDY = (float)(_jc2dy / n);
                    _dx = 0; _dy = 0; _scroll = 0;
                    _jc2dx = 0; _jc2dy = 0;
                    _polls = 0;
                }
            }
        }
        // Stable link slot per shared device id (#138 live device sync) — a device keeps
        // its slot while shared, so a device hot-plugged after connect routes by a slot
        // that never shifts. Freed when the device stops being shared. Under the lock above.
        private readonly Dictionary<string, byte> _exposedSlots = new();
        // Auto-reconnect throttle (#138): last auto-dial per peer fingerprint, so a failing
        // attempt doesn't spam ConnectAsync on every ~2s discovery tick. Concurrent because
        // OnLinkPeersChanged fires from the discovery, UDP-reconcile, reaper and handshake threads.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _autoReconnectCooldown = new(StringComparer.OrdinalIgnoreCase);
        private const long AutoReconnectCooldownMs = 5000;
        // How long an NFC tag row stays lit in the Devices preview after a tap
        // (issue #150). Longer than the ~175 ms button pulse so the highlight is
        // clearly visible, then it clears on its own.
        private const long NfcPreviewHoldMs = 900;
        private InputHookManager _hookManager;
        private SettingsService _settingsService;
        // 0 = live, 1 = disposed. Interlocked for the same reason as
        // _stopped: Dispose is reachable from a worker-thread Task.Run on
        // app close while the UI thread can still call it.
        private int _disposed;
        private readonly HashSet<string> _managedWhitelistDosPaths = new(StringComparer.OrdinalIgnoreCase);
        private GyroCalibratorService _gyroCalibrator;
        // Track which (device, slot) pairs have had auto-calibration
        // kicked off so we don't double-fire the worker if
        // UpdatePadDeviceInfo sees the same pair pre-completion.
        private readonly HashSet<(Guid InstanceGuid, int Slot)> _gyroAutoCalibKicked = new();

        // — per-device gravity vector for Player/World Space gyro
        // projection. Low-pass-filtered against state.Accel[] each
        // Update tick. Cleared in Stop().
        private readonly Dictionary<Guid, (float gx, float gy, float gz)> _gravityState = new();
        // Aux (Nunchuk / left Joy-Con) gravity twin (#199), same lock.
        private readonly Dictionary<Guid, (float gx, float gy, float gz)> _gravityStateAux = new();
        private readonly object _gravityStateLock = new();

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

        /// <summary>Lazy accessor for the gyro calibrator. Auto-creates
        /// the instance with a persist callback that marks settings
        /// dirty so calibration writes round-trip to PadForge.xml.</summary>
        public GyroCalibratorService GyroCalibrator
            => _gyroCalibrator ??= new GyroCalibratorService(() => _settingsService?.MarkDirty());

        /// <summary>Clears the per-(device, slot) auto-calibrate dedup
        /// latch so the next <see cref="UpdatePadDeviceInfo"/> pass
        /// re-fires the 1500 ms auto-calibration for this pair. Called
        /// by the Pad page "Reset Calibration" handler after
        /// <c>GyroCalibrator.ResetCalibration</c> zeroes the bias.</summary>
        public void ClearGyroAutoCalibLatch(Guid instanceGuid, int slot)
        {
            if (instanceGuid == Guid.Empty) return;
            lock (SettingsManager.UserDevices.SyncRoot)
                _gyroAutoCalibKicked.Remove((instanceGuid, slot));
        }

        /// <summary>Proxy to <see cref="InputManager.IsHmVcAt"/> so callers
        /// outside InputService (notably MainWindow's pre-delete capture)
        /// can ask whether a slot currently has an HM virtual controller
        /// without reaching through GetVirtualControllers.</summary>
        public bool IsHmVcAt(int padIndex)
        {
            return _inputManager != null && _inputManager.IsHmVcAt(padIndex);
        }

        /// <summary>Callback to toggle main window visibility. Set by MainWindow.</summary>
        public Action ToggleMainWindow { get; set; }

        /// <summary>Callback to bulk-toggle all created VC slots enabled/disabled.
        /// Set by MainWindow because DeviceService.SetSlotEnabled and
        /// MainViewModel.RefreshNavControllerItems are reachable there. Triggered
        /// from the #91 profile-shortcut mode <see cref="SwitchProfileMode.ToggleVCsDisabled"/>.</summary>
        public Action ToggleVCsDisabled { get; set; }

        /// <summary>Raised on the UI thread after an auto (foreground-match)
        /// switch actually changes the active profile. Manual paths
        /// (shortcuts, Load button, switcher flyout) never raise it. The
        /// profile pills flare on it (#175 item 8).</summary>
        public event Action AutoProfileSwitchApplied;

        /// <summary>
        /// Records a manual profile switch with the foreground monitor so
        /// auto-switching won't re-trigger the profile the user just
        /// overrode. Mirror of the shortcut path in UiTimer_Tick: call
        /// BEFORE the switch, the override id is the pre-switch active id.
        /// </summary>
        public void NoteManualProfileSwitch()
        {
            _foregroundMonitor?.SetManualOverride(SettingsManager.ActiveProfileId);
        }

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
        /// <summary>Multi-device accumulator for the legacy macro trigger
        /// recorder. Holds the current frame's set of held buttons and
        /// active POVs across EVERY assigned device on the slot — no
        /// device-lock semantics — so users can record cross-device combos
        /// (controller + keyboard + mouse) as one trigger. Mirrored into
        /// <see cref="_recordedRawButtons"/> + <see cref="_recordingDeviceGuid"/>
        /// for the first device only, for back-compat with the single-device
        /// finalize path.</summary>
        private List<MacroItem.TriggerInputEntry> _recordedInputEntries;

        // ── Per-device axis-deflection tracking for multi-device combos ──
        // Buttons + POVs are rebuilt each frame from current state; axes
        // need accumulator-style hold-confirmation (3 cycles past threshold)
        // before they're committed. These dictionaries hold the per-device
        // baseline + candidate state. Confirmed axes are stored in
        // _recordedPerDeviceAxisEntries and merged into _recordedInputEntries
        // each frame.
        private Dictionary<Guid, int[]> _perDeviceAxisBaseline;
        private Dictionary<Guid, AxisCandidate> _perDeviceAxisCandidates;
        private List<MacroItem.TriggerInputEntry> _recordedPerDeviceAxisEntries;

        private sealed class AxisCandidate
        {
            public MacroAxisTarget Target = MacroAxisTarget.None;
            public float RawDelta = 0f;
            public int HoldCounter = 0;
        }
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
                padVm.LayerActivated += OnLayerActivated;
                // RebuildStickConfigs resets the new items' steering to defaults; reload the
                // selected assigned device's steering into them (per-device, #94).
                var capturedPad = padVm;
                capturedPad.SteeringReloadCallback = () =>
                {
                    var sel = capturedPad.SelectedMappedDevice;
                    if (sel == null || sel.InstanceGuid == Guid.Empty) return;
                    var ps = SettingsManager.FindSettingByInstanceGuidAndSlot(sel.InstanceGuid, capturedPad.PadIndex)?.GetPadSetting();
                    if (ps != null) capturedPad.LoadSteeringConfigItems(key => ps.GetExtendedMapping(key));
                };
            }

            // Subscribe to Devices page selection changes for offline detail display.
            _mainVm.Devices.PropertyChanged += OnDevicesVmPropertyChanged;

            // Remote Link peer-manager actions (issue #138).
            _mainVm.Settings.PeerRevokeRequested += OnPeerRevokeRequested;
            _mainVm.Settings.PeerRevokeAllRequested += OnPeerRevokeAllRequested;
            _mainVm.Settings.PeerRenameRequested += OnPeerRenameRequested;
            _mainVm.Settings.PeerConnectRequested += OnConnectToPeerRequested;
            _mainVm.Settings.IdentityProtectionModeChangeRequested += OnIdentityProtectionModeChangeRequested;
            // Reflect the persisted identity-protection mode in the dropdown.
            var ipm0 = _settingsService?.RemoteLink?.IdentityProtection ?? PadForge.Engine.RemoteLink.IdentityProtectionMode.Secure;
            _mainVm.Settings.SetIdentityProtectionModeSilently((int)ipm0 - 1);
        }

        private void OnPeerRevokeRequested(string fingerprintHex)
        {
            var trust = _settingsService?.RemoteLink?.Trust;
            if (trust == null || string.IsNullOrEmpty(fingerprintHex)) return;
            var entry = trust.Peers.FirstOrDefault(p => string.Equals(p.FingerprintHex, fingerprintHex, StringComparison.OrdinalIgnoreCase));
            if (entry?.PublicKey != null) trust.Revoke(entry.PublicKey);
            _linkServer?.RevokePeer(fingerprintHex);
            try { _settingsService?.Save(); } catch { }
            _mainVm.Settings.RefreshTrustedPeers(trust.Peers, _linkServer?.ConnectedFingerprints());
        }

        /// <summary>Persist a peer's friendly-name edit (issue #138). The VM already holds
        /// the new name, so no list rebuild — just update the trust store and save.</summary>
        private void OnPeerRenameRequested(string fingerprintHex, string newName)
        {
            var trust = _settingsService?.RemoteLink?.Trust;
            if (trust == null || string.IsNullOrEmpty(fingerprintHex)) return;
            var entry = trust.Peers.FirstOrDefault(p => string.Equals(p.FingerprintHex, fingerprintHex, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;
            entry.Name = newName ?? "";
            try { _settingsService?.Save(); } catch { }
        }

        /// <summary>The user picked a different identity-protection mode (issue #138). Re-wrap
        /// the SAME live key under the new mode — the fingerprint is unchanged so pairings
        /// survive and no reconnect is needed. Collects a password for the password mode;
        /// reverts the dropdown if the identity can't be unlocked or the user cancels.</summary>
        private void OnIdentityProtectionModeChangeRequested(int index)
        {
            // Defer off the ComboBox's own selection-change. Showing the password
            // dialog and reverting the selection synchronously from inside the
            // binding setter leaves the dropdown stuck on the new mode when the
            // user cancels, since a control mid-SelectionChanged ignores the
            // revert. Running after the selection settles rolls a cancel back.
            _dispatcher.BeginInvoke(() =>
            {
                var holder = _settingsService?.RemoteLink;
                if (holder == null) return;
                var newMode = (PadForge.Engine.RemoteLink.IdentityProtectionMode)(index + 1);
                int oldIndex = (int)holder.IdentityProtection - 1;
                if (newMode == holder.IdentityProtection) return;

                // The live key, unlocked. Loads via the current mode (+ session password).
                var identity = EnsureIdentityUnlocked();
                if (identity == null)
                {
                    _mainVm.Settings.SetIdentityProtectionModeSilently(oldIndex);
                    _mainVm.Dashboard.RemoteLinkStatus = Strings.Instance.RemoteLink_StatusUnlockBeforeChange;
                    return;
                }

                string password = null;
                if (newMode == PadForge.Engine.RemoteLink.IdentityProtectionMode.PortablePassword)
                {
                    var dlg = new Views.RemoteLinkPasswordDialog(true, Strings.Instance.RemoteLink_PasswordSetPrompt)
                    { Owner = System.Windows.Application.Current?.MainWindow };
                    if (dlg.ShowDialog() != true) { _mainVm.Settings.SetIdentityProtectionModeSilently(oldIndex); return; }
                    password = dlg.Password;
                }

                byte[] priv = identity.ExportPrivateKey();
                try { holder.ProtectedPrivateBase64 = IdentityProtector.Protect(priv, newMode, password); }
                catch { _mainVm.Settings.SetIdentityProtectionModeSilently(oldIndex); return; }
                finally { PadForge.Engine.RemoteLink.PeerCrypto.Zeroize(priv); }

                holder.IdentityProtection = newMode;
                _remoteLinkSessionPassword = newMode == PadForge.Engine.RemoteLink.IdentityProtectionMode.PortablePassword ? password : null;
                try { _settingsService?.Save(); } catch { }
                _mainVm.Dashboard.RemoteLinkStatus = Strings.Instance.RemoteLink_StatusIdentityUpdated;
            });
        }

        /// <summary>The live identity, loading it if needed. For password mode without a
        /// session password it prompts once. Returns null if it stays locked.</summary>
        private PeerIdentity EnsureIdentityUnlocked()
        {
            if (_remoteLinkIdentity != null) return _remoteLinkIdentity;
            var id = EnsureRemoteLinkIdentity();
            if (id != null) return id;

            // Locked password identity: prompt once and retry. A modal dialog needs the UI
            // thread — if we're off it (a background start path), stay locked; the status
            // already explains why, and the user can unlock from Settings.
            var holder = _settingsService?.RemoteLink;
            if (holder?.IdentityProtection == PadForge.Engine.RemoteLink.IdentityProtectionMode.PortablePassword
                && _dispatcher.CheckAccess())
            {
                var dlg = new Views.RemoteLinkPasswordDialog(false, Strings.Instance.RemoteLink_PasswordUnlockPrompt)
                { Owner = System.Windows.Application.Current?.MainWindow };
                if (dlg.ShowDialog() == true)
                {
                    _remoteLinkSessionPassword = dlg.Password;
                    return EnsureRemoteLinkIdentity();
                }
            }
            return null;
        }

        private void OnPeerRevokeAllRequested()
        {
            var trust = _settingsService?.RemoteLink?.Trust;
            if (trust == null) return;
            foreach (var p in trust.Peers.ToList()) _linkServer?.RevokePeer(p.FingerprintHex);
            trust.RevokeAll();
            try { _settingsService?.Save(); } catch { }
            _mainVm.Settings.RefreshTrustedPeers(trust.Peers);
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

            System.Threading.Interlocked.Exchange(ref _stopped, 0);

            // Heal any gaps in pad indices left over from saves taken
            // before compaction-on-delete landed. Done before the engine
            // starts so the InputManager sees a contiguous topology, and
            // before _defaultProfileSnapshot is captured below so the
            // default snapshot also reflects the compacted layout.
            CompactSlotsForGaps();

            // Sync each PadViewModel's shift-layer tab strip from the
            // loaded MappingSet. PadViewModel constructors run before
            // SettingsService loads PadForge.xml, so a slot that has saved
            // ShiftActivators in the file shows up with just the Base tab
            // until something triggers a rebuild (a profile apply, an
            // activator add). Rebuild here so pre-existing layers show up
            // on first display of the Mappings tab.
            for (int i = 0; i < _mainVm.Pads.Count && i < SettingsManager.SlotMappingSets.Length; i++)
            {
                var slotMs = SettingsManager.SlotMappingSets[i];
                _mainVm.Pads[i].RebuildLayerTabs(slotMs?.ShiftActivators);
            }

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
                _inputManager._kbmConfigs[i] = _mainVm.Pads[i].KbmConfig;
                _inputManager._deviceSlotConfigs[i] = _mainVm.Pads[i].DeviceConfig;
                _inputManager._perDeviceSlotConfigs[i] = _mainVm.Pads[i].PerDeviceSlotConfigs;
                // Subscribe to PadVm's forwarder so the handler follows
                // the per-device anchor across SelectedMappedDevice
                // swaps, not just the initial config instance.
                _mainVm.Pads[i].ActiveDeviceConfigPropertyChanged += OnDeviceConfigChanged;
            }

            // NFC tag registry (issue #150): when a tag is registered/removed,
            // rebuild every pad's input picker so the named tag appears/disappears
            // as a bindable row, and persist the registry. Subscribed here (after
            // settings load), so the load-time LoadRegistry fan-out is not handled.
            PadForge.Common.Input.NfcTagRegistry.RegistryChanged += OnNfcTagRegistryChanged;

            // Subscribe to engine events (raised on background thread).
            _inputManager.DevicesUpdated += OnDevicesUpdated;
            _inputManager.FrequencyUpdated += OnFrequencyUpdated;
            _inputManager.ErrorOccurred += OnErrorOccurred;
            _inputManager.HmVcInactivityDestroyed += OnHmVcInactivityDestroyed;
            _inputManager.HmVcWentNonActive += OnHmVcWentNonActive;

            // Expose per-slot button activity to the user-effects dispatcher so the
            // InputReactive lightbar can detect rising edges. The 16-bit Gamepad.Buttons
            // mask is full (DPAD..Y), so two presses that live outside it would otherwise
            // never flash: the Share / Create button (its own bool field, where a Mic /
            // Misc1 mapping lands) and the touchpad click. Fold both into the wider uint
            // mask on spare bits. Bound to the manager via a captured field so .NET keeps
            // the delegate alive for the manager's lifetime.
            UserEffectsDispatcher.SlotButtonsProvider = padIndex =>
            {
                if (_inputManager == null) return 0u;
                if (padIndex < 0 || padIndex >= InputManager.MaxPads) return 0u;
                var gp = _inputManager.CombinedOutputStates[padIndex];
                uint mask = gp.Buttons;
                if (gp.Share) mask |= 0x10000u;                                  // Share / Create / Mic
                if (_inputManager.SlotRawTouchpadClick[padIndex]) mask |= 0x20000u; // raw touchpad click
                return mask;
            };

            // ══════════════════════════════════════════════════════════════
            // SOLE-WRITER RUMBLE INPUT FOR DS5 / DS4.
            // ══════════════════════════════════════════════════════════════
            // This provider IS the input side of the sole-writer rumble
            // architecture. The dispatcher reads from this lambda once
            // per (slot, device) per packet build and writes the
            // returned bytes into the DS5/DS4 effect packet — that
            // packet is the only path rumble takes to a Sony pad.
            // SDL_RumbleJoystick is skipped for Sony VID/PID devices
            // in InputManager.Step2.ApplyForceFeedback (banner there).
            //
            // We compute audio mix + per-device gain here via
            // ScaleRumbleForDevice exactly the way SDL used to. The
            // critical property is single-writer: even though
            // AudioBassDetector.MotorValue updates asynchronously from
            // the WASAPI callback, only ONE consumer samples it per
            // dispatcher tick, so there's no race partner producing a
            // different byte from a different sample of the same value.
            //
            // Per-DEVICE PadSetting lookup (not the slot's selected
            // device): two Sony pads on one slot can have different
            // audio-rumble sensitivity / gain / motor-balance, and
            // each must see its own settings. The lookup walks
            // UserSettings.Items under SyncRoot — cheap (~16 entries
            // worst case) and matches the lock pattern used elsewhere
            // in this file.
            //
            // If you find yourself wanting to change this to read raw
            // VibrationStates (no audio mix) or to pull from
            // FinalVibrationStates (slot's anchor PadSetting), STOP.
            // Both were tried during the v3.1.x debugging; raw bytes
            // killed audio rumble outright on this path, slot-level
            // bytes broke per-device gain. The sole-writer architecture
            // requires per-device audio-mixed bytes here. See memory:
            // sony-rumble-sole-writer-architecture.md.
            //
            // Vibration structs use ushort (0..65535); DS5/DS4 firmware
            // takes byte (0..255), so shift down 8 bits.
            UserEffectsDispatcher.SlotRumbleForDeviceProvider = (padIndex, deviceGuid) =>
            {
                if (_inputManager == null) return ((byte)0, (byte)0);
                if (_inputManager.OutputsQuiesced) return ((byte)0, (byte)0);
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

                // Sony dispatcher path: layer the macro rumble override
                // via max() over raw, then apply the constant-force
                // override-with-resume rule. Same shape as Step 2's
                // ApplyForceFeedback so DS5 / DS4 motors respond to
                // macro rumble identically to non-Sony pads.
                if (_macroRumbleScratchSony == null)
                    _macroRumbleScratchSony = new Vibration();
                var withMacro = MacroRumbleOverride.Merge(raw,
                    _inputManager.MacroRumbleOverrides[padIndex],
                    _macroRumbleScratchSony);

                if (_constantForceScratchSony == null)
                    _constantForceScratchSony = new Vibration();
                var effective = ConstantForceEvaluator.Resolve(withMacro, devicePs, _constantForceScratchSony);

                _inputManager.ScaleRumbleForDevice(
                    effective.LeftMotorSpeed, effective.RightMotorSpeed,
                    devicePs, out ushort scaledL, out ushort scaledR);

                // #102 Redirect: silence the main motor(s) the engaged trigger route
                // drew from on the physical DualSense, mirroring the Xbox physical
                // write. The game still reads the unredirected virtual-controller state.
                _inputManager.GetTriggerRouteMainRedirect(padIndex, out bool zMainL, out bool zMainR);
                if (zMainL) scaledL = 0;
                if (zMainR) scaledR = 0;
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

            // Per-(slot, device) trigger magnitudes for the DualSense Adaptive
            // Trigger Vibration auto-route. Game-written impulse triggers only
            // arrive on Xbox-class VCs, but the #102 main-motor to trigger routing
            // and the macro trigger override source from the main motor, so they
            // apply on any output VC type (Xbox, DualShock 4, DualSense, generic).
            // Mirrors the main-rumble provider shape: ConstantTriggerForceEvaluator
            // (override-with-resume), ScaleTriggerRumbleForDevice (per-device
            // strength + audio-trigger mix + ImpulseSwapTriggers), then
            // ApplyTriggerRoutingForSony, returning the high byte of each ushort.
            UserEffectsDispatcher.SteeringAtResistanceProvider = padIndex =>
                (_inputManager != null && padIndex >= 0 && padIndex < InputManager.MaxPads)
                    ? _inputManager.SteeringAtResistance[padIndex] : 0f;

            UserEffectsDispatcher.SteeringTriggerVibProvider = padIndex =>
                (_inputManager != null && padIndex >= 0 && padIndex < InputManager.MaxPads)
                    ? _inputManager.GetSteeringTrigVib(padIndex) : 0f;

            UserEffectsDispatcher.SlotImpulseTriggerForDeviceProvider = (padIndex, deviceGuid) =>
            {
                if (_inputManager == null) return ((byte)0, (byte)0);
                if (padIndex < 0 || padIndex >= InputManager.MaxPads) return ((byte)0, (byte)0);
                if (padIndex >= _mainVm.Pads.Count) return ((byte)0, (byte)0);
                // No output-VC gate (#102): game-written impulse triggers only arrive
                // on Xbox-class VCs (so raw.*TriggerMotorSpeed is 0 for others), but
                // the main-motor -> trigger routing and the macro trigger override
                // below source from the main motor, which every VC type drives. They
                // must reach the physical DualSense's AT Vibration whatever the slot
                // outputs as (Xbox, DualShock 4, DualSense, generic).

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

                if (_constantTriggerForceScratchSony == null)
                    _constantTriggerForceScratchSony = new Vibration();
                var effective = ConstantTriggerForceEvaluator.Resolve(
                    raw, devicePs, _constantTriggerForceScratchSony);

                _inputManager.ScaleTriggerRumbleForDevice(
                    effective.LeftTriggerMotorSpeed, effective.RightTriggerMotorSpeed,
                    devicePs, out ushort scaledL, out ushort scaledR);

                // #102: route the device's main-motor amplitude + macro trigger
                // override into the AT Vibration amplitude, the same max-combine the
                // Xbox impulse path applies in ApplyForceFeedback. Reaches DualSense
                // running as an Xbox-class VC (the gate above already passed).
                if (_routeMainScratchSony == null) _routeMainScratchSony = new Vibration();
                if (_routeCfScratchSony == null) _routeCfScratchSony = new Vibration();
                _inputManager.ApplyTriggerRoutingForSony(padIndex, devicePs, raw,
                    _routeMainScratchSony, _routeCfScratchSony, ref scaledL, ref scaledR);
                return ((byte)(scaledR >> 8), (byte)(scaledL >> 8));
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

            // Per-(slot, device) battery percent for Battery lightbar mode.
            // The lightbar is a per-device output, so read the SPECIFIC
            // device's live battery rather than the slot-collapsed value.
            // Clamp negative ("unknown") to 100 so unknown reads as full
            // charge, matching the SlotBatteryPercentProvider default.
            // padIndex is unused by design: battery is a physical per-device
            // property, so the same device on two slots reads one charge. The
            // slot param is kept on the signature only for call-site symmetry
            // with the other (slot, device) providers; do not add a per-slot
            // lookup expecting it to matter.
            UserEffectsDispatcher.SlotBatteryPercentProvider = (padIndex, deviceGuid) =>
            {
                var ud = FindUserDevice(deviceGuid);
                // Same effective-battery path as the indicators (#187), so a
                // battery-driven lightbar following a device whose SDL battery
                // is unknown still gets the Bluetooth devnode value.
                var (pct, _) = GetEffectiveBattery(ud);
                if (pct < 0 || pct > 100) return (byte)100;
                return (byte)pct;
            };

            // Per-(device, slot) gyro at-rest bias for SourceCoercion's
            // gyro descriptor reader. SourceCoercion lives in the Engine
            // library and can't reach PadSettings directly — App-side
            // wires this static delegate at startup so the binding layer
            // can subtract bias inline. Returns zeros for unknown /
            // uncalibrated (device, slot) pairs or when slotIndex is
            // negative (raw - 0 = raw).
            // 250 ms snapshot (see ProviderSnapshot): the resolver's SyncRoot
            // walk runs at 4 Hz per (device, slot), not per poll.
            var gyroBiasSnapshot = new ProviderSnapshot<(Guid, int), (float, float, float)>(key =>
            {
                var (g, slotIndex) = key;
                var settings = SettingsManager.UserSettings;
                if (settings == null) return (0f, 0f, 0f);
                PadSetting ps = null;
                lock (settings.SyncRoot)
                {
                    for (int i = 0; i < settings.Items.Count; i++)
                    {
                        var us = settings.Items[i];
                        if (us == null) continue;
                        if (us.InstanceGuid != g) continue;
                        if (us.MapTo != slotIndex) continue;
                        ps = us.GetPadSetting();
                        break;
                    }
                }
                if (ps == null) return (0f, 0f, 0f);
                return (
                    TryParseFloatPs(ps.GyroBiasPitch, 0f),
                    TryParseFloatPs(ps.GyroBiasYaw,   0f),
                    TryParseFloatPs(ps.GyroBiasRoll,  0f)
                );
            });
            PadForge.Engine.Common.Mapping.SourceCoercion.GyroBiasProvider = (deviceGuid, slotIndex) =>
            {
                if (string.IsNullOrEmpty(deviceGuid)) return (0f, 0f, 0f);
                if (slotIndex < 0 || slotIndex >= InputManager.MaxPads) return (0f, 0f, 0f);
                if (!Guid.TryParse(deviceGuid, out var g)) return (0f, 0f, 0f);
                return gyroBiasSnapshot.Get((g, slotIndex));
            };

            // Per-(device, slot) gyro tuning bundle (H/V sens,
            // deadzone, smoothing, acceleration, output curve, Easy
            // Aim threshold). Lookup goes through the slot's PadSetting
            // for the named device so each binding config has its own
            // gyro feel — matches SteamInput semantics. Deadzone is
            // converted from the PadSetting's deg/s string storage to
            // rad/s for the SourceCoercion read site.
            const float DegToRad = (float)(System.Math.PI / 180.0);
            var defaultTuning = new PadForge.Engine.Common.Mapping.SourceCoercion.GyroTuning
            {
                SensH = 1f, SensV = 1f, OutputCurve = "Linear",
                ApplyToPassthrough = false,
            };
            // 250 ms snapshot, same mechanism as the gyro-bias provider.
            var gyroTuningSnapshot = new ProviderSnapshot<(Guid, int), PadForge.Engine.Common.Mapping.SourceCoercion.GyroTuning>(key =>
            {
                var (g, slotIndex) = key;
                var settings = SettingsManager.UserSettings;
                if (settings == null) return defaultTuning;
                PadSetting ps = null;
                lock (settings.SyncRoot)
                {
                    for (int i = 0; i < settings.Items.Count; i++)
                    {
                        var us = settings.Items[i];
                        if (us == null) continue;
                        if (us.InstanceGuid != g) continue;
                        if (us.MapTo != slotIndex) continue;
                        ps = us.GetPadSetting();
                        break;
                    }
                }
                if (ps == null) return defaultTuning;
                return new PadForge.Engine.Common.Mapping.SourceCoercion.GyroTuning
                {
                    SensH = TryParseFloatPs(ps.GyroSensitivityH, 1f),
                    SensV = TryParseFloatPs(ps.GyroSensitivityV, 1f),
                    DeadZoneRadPerSec = TryParseFloatPs(ps.GyroDeadZoneDegPerSec, 0f) * DegToRad,
                    SmoothingAlpha = TryParseFloatPs(ps.GyroSmoothingAlpha, 0f),
                    Acceleration = TryParseFloatPs(ps.GyroAcceleration, 0f),
                    OutputCurve = ps.GyroOutputCurve ?? "Linear",
                    EasyAimStickThreshold01 = TryParseFloatPs(ps.GyroEasyAimStickThreshold, 0f) / 100f,
                    EasyAimStickSide = string.IsNullOrEmpty(ps.GyroEngageStickSide) ? "Right" : ps.GyroEngageStickSide,
                    EasyAimStickDirection = string.IsNullOrEmpty(ps.GyroEngageStickDirection) ? "Full" : ps.GyroEngageStickDirection,
                    // Jibb-canon extensions
                    Space = string.IsNullOrEmpty(ps.GyroSpace) ? "Local" : ps.GyroSpace,
                    PlayerYawRelax = TryParseFloatPs(ps.GyroPlayerSpaceYawRelaxFactor, 1.41f),
                    WorldSideReduction = TryParseFloatPs(ps.GyroWorldSpaceSideReductionThreshold, 0.125f),
                    TighteningRadPerSec = TryParseFloatPs(ps.GyroTighteningThresholdDegPerSec, 0f) * DegToRad,
                    SmoothingThresholdRadPerSec = TryParseFloatPs(ps.GyroSmoothingThresholdDegPerSec, 0f) * DegToRad,
                    SmoothingWindowSeconds = TryParseFloatPs(ps.GyroSmoothingWindowMs, 50f) / 1000f,
                    RealWorldCalibration = TryParseFloatPs(ps.GyroRealWorldCalibration, 0f),
                    AimEngageDevice = ps.GyroAimEngageDeviceGuid ?? "",
                    AimEngageDescriptor = ps.GyroAimEngageButton ?? "",
                    InvertPitch = TryParseBoolPs(ps.GyroInvertPitch, false),
                    InvertYawRoll = TryParseBoolPs(ps.GyroInvertYawRoll, false),
                    ApplyToPassthrough = TryParseBoolPs(ps.GyroApplyTuningToPassthrough, false),
                };
            });
            PadForge.Engine.Common.Mapping.SourceCoercion.GyroTuningProvider = (deviceGuid, slotIndex) =>
            {
                if (string.IsNullOrEmpty(deviceGuid)) return defaultTuning;
                if (slotIndex < 0 || slotIndex >= InputManager.MaxPads) return defaultTuning;
                if (!Guid.TryParse(deviceGuid, out var g)) return defaultTuning;
                return gyroTuningSnapshot.Get((g, slotIndex));
            };

            // Stick-position provider for Easy Aim gating. The gyro reader
            // passes its slotIndex and the side it needs (issue #120: true =
            // left stick, false = right). Reads the slot's PRE-DEADZONE mapped
            // thumbs (each device's RawMappedState, combined per axis by
            // largest magnitude like Step 4), NOT the post-deadzone combined
            // output: the requester's headline QoL is a gyro threshold BELOW
            // the stick's own deadzone, so a micro-deflection activates gyro
            // without moving the camera. Steam gates on physical deflection
            // the same way. XInput frame: x>0 right, x<0 left, y>0 up,
            // y<0 down (HMaestroVirtualController: ThumbLY +32767 = up).
            PadForge.Engine.Common.Mapping.SourceCoercion.SlotStickDeflectionProvider = (slotIndex, isLeft) =>
            {
                if (slotIndex < 0 || slotIndex >= InputManager.MaxPads) return (0f, 0f);
                var dzSettings = SettingsManager.UserSettings;
                if (dzSettings == null) return (0f, 0f);
                short rawX = 0, rawY = 0;
                lock (dzSettings.SyncRoot)
                {
                    for (int i = 0; i < dzSettings.Items.Count; i++)
                    {
                        var us = dzSettings.Items[i];
                        if (us == null || us.MapTo != slotIndex) continue;
                        var rm = us.RawMappedState;
                        short sx = isLeft ? rm.ThumbLX : rm.ThumbRX;
                        short sy = isLeft ? rm.ThumbLY : rm.ThumbRY;
                        if (Math.Abs((int)sx) > Math.Abs((int)rawX)) rawX = sx;
                        if (Math.Abs((int)sy) > Math.Abs((int)rawY)) rawY = sy;
                    }
                }
                float x = (rawX - (float)short.MinValue) / 65535f * 2f - 1f;
                float y = (rawY - (float)short.MinValue) / 65535f * 2f - 1f;
                return (x, y);
            };

            // Player/World Space gyro — gravity vector estimator.
            // Per-device low-pass on state.Accel[] (alpha 0.02 at 60Hz
            // poll ≈ 0.5Hz cutoff). Stored in _gravityState dict,
            // updated each Update tick alongside the live-rate readout.
            // Returns (0, 0, -1) (flat, face-up) for unknown devices —
            // matches the v3.2 motion-snapshot default orientation.
            PadForge.Engine.Common.Mapping.SourceCoercion.GravityProvider = deviceGuid =>
            {
                if (string.IsNullOrEmpty(deviceGuid)) return (0f, 0f, -1f);
                if (!Guid.TryParse(deviceGuid, out var g)) return (0f, 0f, -1f);
                lock (_gravityStateLock)
                {
                    return _gravityState.TryGetValue(g, out var v) ? v : (0f, 0f, -1f);
                }
            };

            // Aux gravity twin (#199): same filter over AccelAux (the Nunchuk /
            // left Joy-Con), read by the "Motion Lean L" family.
            PadForge.Engine.Common.Mapping.SourceCoercion.GravityProviderAux = deviceGuid =>
            {
                if (string.IsNullOrEmpty(deviceGuid)) return (0f, 0f, -1f);
                if (!Guid.TryParse(deviceGuid, out var g)) return (0f, 0f, -1f);
                lock (_gravityStateLock)
                {
                    return _gravityStateAux.TryGetValue(g, out var v) ? v : (0f, 0f, -1f);
                }
            };

            // Aim Engage button gate — reads the named device's
            // current button state via SourceCoercion's existing bool
            // reader. The synthetic MappingSource carries just the
            // device + descriptor; tuning fields don't matter here
            // because we read at the boolean level.
            // Reused across polls (mutate-in-place). EvaluateForButtonTarget/ReadAsBool
            // are pure reads that never retain the source reference, and this provider
            // is invoked only from the poll thread's three engage settles (gyro / trigger
            // route / haptic mirror, run sequentially), non-reentrant. Allocating a
            // MappingSource per activator per poll at ~1 kHz was pure GC churn. Kind is
            // fixed; only Guid/Descriptor change per call. Default DeadZone(50) matches
            // the threshold arg, Invert(false) matches the old literal.
            var buttonHeldSynth = new PadForge.Engine.Data.MappingSource { Kind = "Direct" };
            PadForge.Engine.Common.Mapping.SourceCoercion.ButtonHeldProvider = (deviceGuid, descriptor, slotIndex) =>
            {
                if (string.IsNullOrEmpty(descriptor)) return true; // unconfigured = pass-through
                if (string.IsNullOrEmpty(deviceGuid) || !Guid.TryParse(deviceGuid, out var g)) return false;
                var ud = FindUserDevice(g);
                if (ud == null || ud.InputState == null) return false;
                buttonHeldSynth.DeviceGuid = deviceGuid;
                buttonHeldSynth.Descriptor = descriptor;
                return PadForge.Engine.Common.Mapping.SourceCoercion.EvaluateForButtonTarget(
                    ud.InputState, buttonHeldSynth, 50, slotIndex);
            };

            // — sample rate for the dual-threshold smoothing buffer.
            // Reads the live PollingRateMs setting; falls back to 60Hz
            // if the setting is missing or invalid.
            PadForge.Engine.Common.Mapping.SourceCoercion.PollHzProvider = () =>
            {
                int ms = _mainVm?.Settings?.PollingRateMs ?? 0;
                return ms > 0 ? 1000f / ms : 60f;
            };

            // Wii Balance Board (#146): per-board kg calibration + tare. The
            // calibration arrives as the SDL "balance_board_calibration" hex
            // property once the driver has read the board's EEPROM; parse it once
            // and cache. SourceCoercion's Total-Weight reader interpolates each raw
            // corner to kg through this and subtracts the tare.
            PadForge.Engine.Common.Mapping.SourceCoercion.BalanceCalibrationProvider = deviceGuid =>
            {
                if (string.IsNullOrEmpty(deviceGuid) || !Guid.TryParse(deviceGuid, out var g)) return null;
                lock (_balanceLock)
                {
                    if (_balanceCalibration.TryGetValue(g, out var cached)) return cached;
                }
                var ud = FindUserDevice(g);
                if (ud?.Device is not PadForge.Engine.SdlDeviceWrapper sdl || sdl.Joystick == IntPtr.Zero)
                    return null;
                uint props = SDL3.SDL.SDL_GetJoystickProperties(sdl.Joystick);
                if (props == 0) return null;
                string hex = SDL3.SDL.SDL_GetStringProperty(props, "SDL.joystick.wii.balance_board_calibration", "");
                var parsed = ParseBalanceCalibrationHex(hex);
                if (parsed != null) lock (_balanceLock) _balanceCalibration[g] = parsed;
                return parsed;
            };
            PadForge.Engine.Common.Mapping.SourceCoercion.BalanceTareKgProvider = deviceGuid =>
            {
                if (string.IsNullOrEmpty(deviceGuid) || !Guid.TryParse(deviceGuid, out var g)) return 0f;
                lock (_balanceLock)
                {
                    return _balanceTareKg.TryGetValue(g, out var t) ? t : 0f;
                }
            };

            // Wii IR pointer tuning (#146 Pointer tab), per (device, slot): the
            // sensor-bar vertical offset (above -> negative, below -> positive,
            // like Touchmote's offsetY) and the smoothing factor come from the
            // pair's PadSetting, same lookup shape as GyroTuningProvider, so
            // each virtual controller sharing one remote keeps its own feel.
            // 250 ms snapshot: invoked per IR mapping-row evaluation per poll
            // (2+ kHz with X and Y rows mapped), so the SyncRoot walk + three
            // string parses must not run per call.
            var irTuningSnapshot = new ProviderSnapshot<(Guid, int), (float, float)>(key =>
            {
                var (g, slotIndex) = key;
                var irSettings = SettingsManager.UserSettings;
                if (irSettings == null) return (0f, 0f);
                PadSetting irPs = null;
                lock (irSettings.SyncRoot)
                {
                    for (int i = 0; i < irSettings.Items.Count; i++)
                    {
                        var us = irSettings.Items[i];
                        if (us == null) continue;
                        if (us.InstanceGuid != g) continue;
                        if (us.MapTo != slotIndex) continue;
                        irPs = us.GetPadSetting();
                        break;
                    }
                }
                if (irPs == null) return (0f, 0f);
                int pos = (int)TryParseFloatPs(irPs.IrSensorBarPos, 0f);
                float comp = TryParseFloatPs(irPs.IrSensorBarComp, 0f);
                float off = pos == 1 ? -comp : pos == 2 ? comp : 0f;
                return (off, TryParseFloatPs(irPs.IrSmoothing, 0f));
            });
            PadForge.Engine.Common.Mapping.SourceCoercion.IrTuningProvider = (deviceGuid, slotIndex) =>
            {
                if (string.IsNullOrEmpty(deviceGuid) || !Guid.TryParse(deviceGuid, out var g)) return (0f, 0f);
                if (slotIndex < 0 || slotIndex >= InputManager.MaxPads) return (0f, 0f);
                return irTuningSnapshot.Get((g, slotIndex));
            };

            // Wii pointer mode (#203 Pointer tab), per (device, slot): same
            // lookup shape as IrTuningProvider above. Mode ints match the
            // SourceCoercion doc: 0 Mouse, 1 FpsMouse, 2 Mouse43, 3 Mouse169.
            // 250 ms snapshot, same mechanism as IrTuningProvider. A
            // PointerModeCycle write lands within a quarter second, which the
            // house standard treats as imperceptible for a config change.
            var irPointerModeSnapshot = new ProviderSnapshot<(Guid, int), (int, float)>(key =>
            {
                var (pg, slotIndex) = key;
                var pmSettings = SettingsManager.UserSettings;
                if (pmSettings == null) return (0, 35f);
                PadSetting pmPs = null;
                lock (pmSettings.SyncRoot)
                {
                    for (int i = 0; i < pmSettings.Items.Count; i++)
                    {
                        var us = pmSettings.Items[i];
                        if (us == null) continue;
                        if (us.InstanceGuid != pg) continue;
                        if (us.MapTo != slotIndex) continue;
                        pmPs = us.GetPadSetting();
                        break;
                    }
                }
                if (pmPs == null) return (0, 35f);
                int mode = pmPs.PointerMode switch
                {
                    "FpsMouse" => 1,
                    "Mouse43" => 2,
                    "Mouse169" => 3,
                    _ => 0,
                };
                return (mode, TryParseFloatPs(pmPs.PointerFpsSpeed, 35f));
            });
            PadForge.Engine.Common.Mapping.SourceCoercion.IrPointerModeProvider = (deviceGuid, slotIndex) =>
            {
                if (string.IsNullOrEmpty(deviceGuid) || !Guid.TryParse(deviceGuid, out var pg)) return (0, 35f);
                if (slotIndex < 0 || slotIndex >= InputManager.MaxPads) return (0, 35f);
                return irPointerModeSnapshot.Get((pg, slotIndex));
            };

            // PointerModeCycle applier (#203): ALL PointerMode writes happen
            // here on the dispatcher, never on the poll thread, because the
            // 30 Hz UI tick copies the VM's PointerMode over PadSetting and
            // a poll-thread write could be reverted by a tick already
            // mid-execution. Lock order: UserDevices before UserSettings.
            InputManager.PointerModeCycleApply = (slotIdx, targetMode) =>
            {
                try
                {
                    System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        if (slotIdx < 0 || slotIdx >= _mainVm.Pads.Count) return;
                        if (string.IsNullOrEmpty(targetMode)) return;

                        var pmDevices = SettingsManager.UserDevices;
                        var pmSettings2 = SettingsManager.UserSettings;
                        if (pmDevices == null || pmSettings2 == null) return;

                        var irGuids = new System.Collections.Generic.HashSet<Guid>();
                        lock (pmDevices.SyncRoot)
                        {
                            for (int i = 0; i < pmDevices.Items.Count; i++)
                            {
                                var ud = pmDevices.Items[i];
                                if (ud != null && ud.HasIrCamera) irGuids.Add(ud.InstanceGuid);
                            }
                        }
                        if (irGuids.Count == 0) return;

                        bool wrote = false;
                        lock (pmSettings2.SyncRoot)
                        {
                            for (int i = 0; i < pmSettings2.Items.Count; i++)
                            {
                                var us = pmSettings2.Items[i];
                                if (us == null || us.MapTo != slotIdx) continue;
                                if (!irGuids.Contains(us.InstanceGuid)) continue;
                                var ps = us.GetPadSetting();
                                if (ps == null) continue;
                                ps.PointerMode = targetMode;
                                wrote = true;
                            }
                        }
                        if (!wrote) return;

                        // Targeted VM update (never a full reload: the user
                        // may be mid-edit elsewhere on the tab), only when
                        // the slot's selected device is one of the IR
                        // devices just written.
                        var padVm = _mainVm.Pads[slotIdx];
                        if (_previousSelectedDevice.TryGetValue(slotIdx, out var selGuid)
                            && irGuids.Contains(selGuid))
                            padVm.PointerMode = targetMode;
                        _settingsService?.MarkDirty();
                    }));
                }
                catch { /* engine shutting down mid-fire */ }
            };

            // Guide LED macro applier (#209): the poll thread hands
            // (slot, percent) here and the dispatcher walks the slot's
            // mapped devices, routing each through the Xbox GIP writer
            // (which only enqueues, its own worker does the I/O) or the
            // Steam home-LED hint. Transient by design: nothing is
            // written into DeviceSlotConfig, so a flash-on-engage macro
            // never dirties settings, and the Lighting tab's Battery
            // mode simply reasserts on its next cadence.
            InputManager.GuideLedApply = (slotIdx, percent) =>
            {
                try
                {
                    System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        if (slotIdx < 0 || slotIdx >= _mainVm.Pads.Count) return;
                        var padVm = _mainVm.Pads[slotIdx];
                        if (padVm == null) return;
                        int pct = Math.Clamp(percent, 0, 100);
                        foreach (var dev in padVm.MappedDevices)
                        {
                            if (dev == null || dev.InstanceGuid == Guid.Empty) continue;
                            var ud = FindUserDevice(dev.InstanceGuid);
                            if (ud == null || !ud.IsOnline) continue;
                            // A shared peer device's Guide LED lives on the OWNER's
                            // machine: relay the percent (#209) instead of writing
                            // local hardware. A shared 2015 Steam Controller would
                            // otherwise dim THIS PC's local SCs via the global hint.
                            if (ud.Device is PadForge.Engine.RemoteLink.RemotePeerDevice rpd)
                                PadForge.Common.Input.RemoteLinkOutputRouter.ShipGuideLed(rpd.DevicePath, pct);
                            else if (PadForge.Common.Input.XboxGipGuideLedWriter.IsXboxGipPathed(ud))
                                PadForge.Common.Input.XboxGipGuideLedWriter.Instance.TrySetBrightness(ud, pct);
                            else if (PadForge.Common.Input.SteamHomeLedSetter.IsSteamController2015(ud.VendorId, ud.ProdId))
                                PadForge.Common.Input.SteamHomeLedSetter.TrySet(pct);
                        }
                    }));
                }
                catch { /* engine shutting down mid-fire */ }
            };

            // Cursor-position source (#107): a 200 Hz sampler publishes the
            // normalized desktop cursor position into MouseCursorProvider for
            // "Mouse Position X/Y" mapping sources. Disposed on engine stop.
            _cursorControlService?.Dispose();
            _cursorControlService = new CursorControlService();

            // Resolved Aim-Engage state for the slot. OR-combines the
            // per-slot bit settled by UpdateGyroEngageStates (engage
            // button under Hold / Toggle semantics) with the bit written
            // by the SetGyroEngaged macro action. Returns true (always-on)
            // when the InputManager isn't wired yet, matching the gyro
            // evaluator's null-provider fallback.
            PadForge.Engine.Common.Mapping.SourceCoercion.AimEngageStateProvider = slotIndex =>
            {
                if (_inputManager == null) return true;
                if (slotIndex < 0 || slotIndex >= InputManager.MaxPads) return true;
                return _inputManager.GyroEngagedFromButton[slotIndex]
                    || _inputManager.GyroEngagedFromMacro[slotIndex];
            };

            // — touchpad-gesture fire lookup. Reads from the per-
            // (slot, device, pad) gesture context the recognizer
            // populates each polling tick in Step 2. Slot-keyed so two
            // slots sharing one physical touchpad each get their own
            // FiredGesturesThisFrame driven by their own Touchpad-tab
            // toggles. Also routes the joystick D-pad bool descriptors
            // ("DPadUp" etc.) through the same hook by computing them
            // on the fly from the per-slot context's FingerPaths plus
            // the per-slot joystick settings. Returns false when the
            // InputManager isn't wired, no gesture context exists for
            // the (slot, device, pad) triple, the gesture didn't fire
            // on the current tick, or (for joystick) joystick output
            // is disabled / no finger is active.
            PadForge.Engine.Common.Mapping.SourceCoercion.TouchpadGestureFiredProvider =
                (slotIndex, deviceGuid, padIdx, gestureName) =>
            {
                if (_inputManager == null) return false;
                if (string.IsNullOrEmpty(deviceGuid) || !Guid.TryParse(deviceGuid, out var g)) return false;
                if (!_inputManager.GestureContexts.TryGetValue((slotIndex, g, padIdx), out var ctx)) return false;

                // Touchpad-stick D-pad descriptors compute their bool
                // from the same FingerPaths anchor + current delta the
                // analog stick reader uses. Routed through the same
                // provider so SourceCoercion only needs one bool hook.
                if (gestureName == "DPadUp" || gestureName == "DPadRight"
                    || gestureName == "DPadDown" || gestureName == "DPadLeft")
                {
                    var settings = _inputManager.TouchpadGestureSettingsProvider?.Invoke(slotIndex, g, padIdx)
                        ?? PadForge.Engine.Touchpad.TouchpadGestureSettings.Default();
                    var (u, r, d, l) = PadForge.Engine.Touchpad.GestureRecognizer.ComputeJoystickDPad(ctx, settings);
                    return gestureName switch
                    {
                        "DPadUp"    => u,
                        "DPadRight" => r,
                        "DPadDown"  => d,
                        "DPadLeft"  => l,
                        _ => false,
                    };
                }

                return ctx.FiredGesturesThisFrame.Contains(
                    $"Touchpad {padIdx} {gestureName}");
            };

            // Mouse-gesture fired reads (issue #200): the recognizer stores
            // bare gesture names ("Left".."Click") in its per-(slot, device)
            // context's fired set.
            PadForge.Engine.Common.Mapping.SourceCoercion.MouseGestureFiredProvider =
                (slotIndex, deviceGuid, gestureName) =>
            {
                if (_inputManager == null) return false;
                if (string.IsNullOrEmpty(deviceGuid) || !Guid.TryParse(deviceGuid, out var g)) return false;
                if (!_inputManager.MouseGestureContexts.TryGetValue((slotIndex, g), out var ctx)) return false;
                return ctx.FiredGesturesThisFrame.Contains(gestureName);
            };

            // — touchpad-gesture continuous-axis reader for PinchAxis,
            // RotateAxis, and the joystick stick axes. Same per-(slot,
            // device, pad) context lookup; returns 0 when no source is
            // active.
            PadForge.Engine.Common.Mapping.SourceCoercion.TouchpadGestureAxisProvider =
                (slotIndex, deviceGuid, padIdx, axisName) =>
            {
                if (_inputManager == null) return 0f;
                if (string.IsNullOrEmpty(deviceGuid) || !Guid.TryParse(deviceGuid, out var g)) return 0f;
                if (!_inputManager.GestureContexts.TryGetValue((slotIndex, g, padIdx), out var ctx)) return 0f;
                if (axisName == "StickX" || axisName == "StickY")
                {
                    var settings = _inputManager.TouchpadGestureSettingsProvider?.Invoke(slotIndex, g, padIdx)
                        ?? PadForge.Engine.Touchpad.TouchpadGestureSettings.Default();
                    var (sx, sy) = PadForge.Engine.Touchpad.GestureRecognizer.ComputeJoystickAxis(ctx, settings);
                    return axisName == "StickX" ? sx : sy;
                }
                return axisName switch
                {
                    "PinchAxis"  => ctx.CurrentPinchAxis,
                    "RotateAxis" => ctx.CurrentRotateAxis,
                    _ => 0f
                };
            };

            // Touchpad-as-mouse tuning. Slot-keyed: the same physical
            // touchpad in two virtual-controller slots carries its own
            // MouseSensitivityX/Y + MouseInvertX/Y per slot, stored on
            // each slot's UserSetting's PadSetting. Walk UserSettings
            // filtered by `MapTo == slotIndex && InstanceGuid == device`,
            // the same (slot, device, pad) key TouchpadGestureSettingsProvider
            // uses since the May 2026 slot-collapse fix.
            PadForge.Engine.Common.Mapping.SourceCoercion.TouchpadMouseSettingsProvider =
                (slotIndex, deviceGuid, padIdx) =>
            {
                if (string.IsNullOrEmpty(deviceGuid) || !Guid.TryParse(deviceGuid, out var g)) return null;
                var settings = SettingsManager.UserSettings;
                if (settings == null) return null;
                PadSetting ps = null;
                lock (settings.SyncRoot)
                {
                    for (int i = 0; i < settings.Items.Count; i++)
                    {
                        var us = settings.Items[i];
                        if (us == null) continue;
                        if (us.MapTo != slotIndex) continue;
                        if (us.InstanceGuid != g) continue;
                        ps = us.GetPadSetting();
                        break;
                    }
                }
                if (ps?.TouchpadSettings == null) return null;
                string guidStr = g.ToString();
                for (int i = 0; i < ps.TouchpadSettings.Length; i++)
                {
                    var entry = ps.TouchpadSettings[i];
                    if (entry == null) continue;
                    if (entry.TouchpadIndex != padIdx) continue;
                    if (!string.Equals(entry.DeviceGuid, guidStr, StringComparison.OrdinalIgnoreCase))
                        continue;
                    return entry.Settings;
                }
                return null;
            };

            // — per-(slot, device, pad) touchpad gesture settings.
            // Walks UserSettings filtered by both `MapTo == slot` and
            // `InstanceGuid == device` so two slots sharing one
            // touchpad each carry their own toggles / thresholds /
            // joystick tuning. Returns defaults when no per-pad entry
            // exists (matches the engine-side fallback so a fresh
            // assignment gets sensible behavior without the user
            // opening the Touchpad tab).
            // Issue #83 — per-slot per-device passthrough flags for the
            // controller-audio service, sourced from the same per-device
            // configs the lighting dispatcher uses.
            PadForge.Common.Input.AudioPassthroughService.PassthroughConfigProvider = slotIndex =>
            {
                if (slotIndex < 0 || slotIndex >= _mainVm.Pads.Count)
                    return System.Linq.Enumerable.Empty<(Guid, bool, string)>();
                return _mainVm.Pads[slotIndex].PerDeviceSlotConfigs
                    .Select(kv => (kv.Key, kv.Value.AudioPassthroughEnabled, kv.Value.AudioMirrorSourceId))
                    .ToList();
            };

            // Issue #185: haptic-mirror engage configs, same per-device configs
            // as the passthrough provider above. Returns EVERY passthrough-
            // enabled config with its DEVICE GUID and mode (Always included):
            // the gate is per (slot, device), so each device's config gates
            // only its own sink. The first cut returned one "first non-Always"
            // config per slot, which let a stale config on another device GUID
            // mute the whole slot (the Steam Controller Always-silent bug).
            // Called at low cadence (~4 Hz) from the poll thread's config
            // refresh, never per poll.
            _inputManager.HapticMirrorEngageConfigProvider = slotIndex =>
            {
                if (slotIndex < 0 || slotIndex >= _mainVm.Pads.Count) return null;
                List<(Guid, int, string, string, int)> list = null;
                foreach (var kv in _mainVm.Pads[slotIndex].PerDeviceSlotConfigs)
                {
                    var c = kv.Value;
                    if (c == null || !c.AudioPassthroughEnabled) continue;
                    int mode = c.AudioMirrorEngageMode switch
                    {
                        "Input" => 1,
                        "Rumble" => 2,
                        _ => 0,
                    };
                    (list ??= new()).Add((kv.Key, mode, c.AudioMirrorEngageDeviceGuid ?? "",
                        c.AudioMirrorEngageButton ?? "", c.AudioMirrorEngageReleaseMs));
                }
                return list;
            };

            // Issue #202: per-(slot, device) high-tone filter for the
            // haptic-tone sinks, same per-device configs. Mode ints match
            // HapticToneService: 0 = Off, 1 = Cut, 2 = Fold. Each sink's
            // stream thread re-reads its pair at ~4 Hz, so this stays
            // allocation-light: one TryGetValue on the concurrent
            // per-device dictionary, no LINQ.
            PadForge.Common.Input.HapticToneService.ToneFilterProvider = (slotIndex, deviceGuid) =>
            {
                if (slotIndex >= 0 && slotIndex < _mainVm.Pads.Count
                    && _mainVm.Pads[slotIndex].PerDeviceSlotConfigs.TryGetValue(deviceGuid, out var c)
                    && c != null)
                {
                    int mode = c.AudioToneFilterMode switch
                    {
                        "Cut" => 1,
                        "Fold" => 2,
                        _ => 0,
                    };
                    return (mode, c.AudioToneLimitHz);
                }
                return (0, 800);
            };

            // A persisted mirror toggle must resume on launch — the service
            // otherwise only starts when poked (toggle change, assignment
            // change, or a macro's sink lookup). One signal is enough: the
            // worker self-heals device timing on its 5 s cadence. Skipped
            // when no device has the toggle on, so the audio threads stay
            // off for users who never use controller audio.
            if (_mainVm.Pads.Any(p => p.PerDeviceSlotConfigs.Any(kv => kv.Value.AudioPassthroughEnabled)))
            {
                PadForge.Common.Input.AudioPassthroughService.Reconcile();
                PadForge.Common.Input.WiiSpeakerService.Reconcile(); // pick up the Wii mirror too
                PadForge.Common.Input.HapticToneService.Reconcile(); // and the Switch/Steam haptic mirror
            }

            // Wii Remote speaker sinks (#146): self-healing periodic reconcile
            // builds/tears down a sink as a Wii Remote is assigned to a slot.
            PadForge.Common.Input.WiiSpeakerService.EnsureStarted();
            // Switch HD Rumble + Steam Controller haptic-tone sinks (#147).
            PadForge.Common.Input.HapticToneService.EnsureStarted();

            // 250 ms snapshot: invoked per (slot, device, pad) per poll from
            // the Step 2 gesture walk, so the SyncRoot walk and the per-call
            // Guid.ToString() must not run per poll.
            var touchpadGestureSnapshot = new ProviderSnapshot<(int, Guid, int), PadForge.Engine.Touchpad.TouchpadGestureSettings>(key =>
            {
                var (slotIndex, deviceGuid, padIdx) = key;
                var settings = SettingsManager.UserSettings;
                if (settings == null) return PadForge.Engine.Touchpad.TouchpadGestureSettings.Default();
                PadSetting ps = null;
                lock (settings.SyncRoot)
                {
                    for (int i = 0; i < settings.Items.Count; i++)
                    {
                        var us = settings.Items[i];
                        if (us == null) continue;
                        if (us.MapTo != slotIndex) continue;
                        if (us.InstanceGuid != deviceGuid) continue;
                        ps = us.GetPadSetting();
                        break;
                    }
                }
                if (ps?.TouchpadSettings == null)
                    return PadForge.Engine.Touchpad.TouchpadGestureSettings.Default();
                string guidStr = deviceGuid.ToString();
                for (int i = 0; i < ps.TouchpadSettings.Length; i++)
                {
                    var entry = ps.TouchpadSettings[i];
                    if (entry == null) continue;
                    if (entry.TouchpadIndex != padIdx) continue;
                    if (!string.Equals(entry.DeviceGuid, guidStr, StringComparison.OrdinalIgnoreCase))
                        continue;
                    return entry.Settings ?? PadForge.Engine.Touchpad.TouchpadGestureSettings.Default();
                }
                return PadForge.Engine.Touchpad.TouchpadGestureSettings.Default();
            });
            _inputManager.TouchpadGestureSettingsProvider = (slotIndex, deviceGuid, padIdx) =>
                touchpadGestureSnapshot.Get((slotIndex, deviceGuid, padIdx));

            // Per-(slot, device) mouse-gesture settings (issue #200), twin
            // of the touchpad provider above minus the pad index. Same
            // UserSettings walk under SyncRoot, Default() on every miss,
            // through the same 250 ms snapshot.
            var mouseGestureSnapshot = new ProviderSnapshot<(int, Guid), PadForge.Engine.Mouse.MouseGestureSettings>(key =>
                ResolveMouseGestureSettingsForSlot(key.Item1, key.Item2));
            _inputManager.MouseGestureSettingsProvider = (slotIndex, deviceGuid) =>
                mouseGestureSnapshot.Get((slotIndex, deviceGuid));

            // Remote Link delta bridge (#138): the poll thread folds each
            // exposed device's per-poll deltas after Step 2; the 125 Hz stream
            // tick ships the snapshot + drained deltas instead of calling the
            // wrappers' destructive GetCurrentState from its timer thread.
            // Cheap no-op while nothing is exposed. Dies with the instance.
            _inputManager.RemoteLinkPollTick = RemoteLinkPollAccumulate;

            // Per-(slot, device) lightbar configs — drives the
            // dispatcher's per-device synthesis loop and per-device
            // pulse rolls. Lighting tab is per-device (parallel to
            // PadSetting), so two DualSenses on the same slot can have
            // different LightbarMode / colors / palette.
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider = padIndex =>
            {
                if (_inputManager == null) return null;
                if (padIndex < 0 || padIndex >= InputManager.MaxPads) return null;
                return _inputManager._perDeviceSlotConfigs[padIndex];
            };

            // Subscribe to settings/dashboard property changes for runtime propagation.
            _mainVm.Settings.PropertyChanged += OnSettingsPropertyChanged;
            _mainVm.Dashboard.PropertyChanged += OnDashboardPropertyChanged;
            _mainVm.Dashboard.ResetTouchpadOverlayPositionRequested += OnResetTouchpadOverlayPosition;

            // Bridge between InputService's _activeTouchpadGestures
            // working list and SettingsService's save / load paths.
            // Provider: SettingsService calls this at save time so
            // UpdateActiveProfileSnapshot (named profile autosave)
            // and BuildAppSettingsForSave (default profile autosave)
            // capture the live gesture catalog. Applier: SettingsService
            // calls this on load with the default profile's gestures
            // so they re-seed the working list at startup. Named profile
            // load uses ApplyProfileTouchpadGestures via ApplyProfile.
            _settingsService.TouchpadGesturesProvider =
                () => _activeTouchpadGestures.ToArray();
            _settingsService.TouchpadGesturesApplier = gestures =>
            {
                _activeTouchpadGestures.Clear();
                if (gestures != null)
                {
                    foreach (var g in gestures)
                        if (g != null) _activeTouchpadGestures.Add(g);
                }
                RebuildShapeTemplatesFromWorkingList();
                try
                {
                    foreach (var padVm in _mainVm.Pads)
                    {
                        padVm?.RefreshCustomTouchpadGestures(_activeTouchpadGestures);
                        if (padVm != null) RefreshAvailableInputsForSlot(padVm);
                    }
                }
                catch { /* refresh is cosmetic */ }
            };

            // Rebuild every Pad page's mapping-row dropdown now that the
            // gesture-settings provider is wired. SettingsService.LoadFromFile
            // ran BEFORE this StartEngine path and populated AvailableInputs
            // with no provider available, so the dropdowns currently show
            // every gesture regardless of the user's per-pad enable / mode /
            // category toggles. Re-running the picker build here with the
            // provider in place applies the gating immediately on first paint.
            try
            {
                foreach (var padVm in _mainVm.Pads)
                    if (padVm != null) RefreshAvailableInputsForSlot(padVm);
            }
            catch { /* picker refresh is cosmetic */ }

            // Create foreground monitor for auto-profile switching.
            _foregroundMonitor = new ForegroundMonitorService();
            _foregroundMonitor.ProfileSwitchRequired += OnAutoProfileSwitchRequired;

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

            // Start Remote Link server if enabled (issue #138).
            StartRemoteLinkIfEnabled();

            // Show touchpad overlay if enabled.
            if (_mainVm.Dashboard.EnableTouchpadOverlay)
                ShowTouchpadOverlay();

            // Start audio bass rumble detector if any slot has it enabled.
            SyncAudioBassDetector();

            // Clear stale HidHide blacklist entries from previous crash/kill.
            // _managedDeviceIds is in-memory so entries are lost on restart,
            // making RemoveManagedDevices() unable to clean up stale entries.
            //
            // Skipped when KeepHidHideCloaksBetweenLaunches is on so the
            // persisted cloaks survive into the new session and
            // ApplyDeviceHiding's per-device walk re-asserts them
            // idempotently — without a visible decloak window between
            // PadForge restarts.
            if (!_mainVm.Settings.KeepHidHideCloaksBetweenLaunches)
            {
                try
                {
                    if (HidHideController.IsAvailable())
                        HidHideController.ClearAll();
                }
                catch { /* best effort */ }
            }
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
        // 0 = running, 1 = stopped. Interlocked because Stop() is reachable
        // concurrently from the engine-toggle Task.Run and Dispose(); a
        // plain bool check-then-set raced into double teardown.
        private int _stopped;

        public void Stop()
        {
            if (System.Threading.Interlocked.Exchange(ref _stopped, 1) != 0) return;

            // Re-enable any Bluetooth LE devnodes still inside their #162
            // disconnect window. The delayed re-enable task dies with the
            // process, and a stranded-disabled node leaves the pad unable
            // to reconnect until the user finds it in Device Manager.
            PadForge.Common.Input.BluetoothLinkHelper.ReEnablePendingDevNodes();

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

                // The pipeline heat chips (#175 item 10) ride the timer
                // just torn down, so nothing would ever clear the last
                // tick's rowfire flags. Drop every row's IsInputActive
                // and every pad's Live flags (including the SHIFT flame)
                // so no chip stays lit on a dead engine.
                foreach (var padVm in _mainVm.Pads)
                {
                    if (padVm == null) continue;
                    foreach (var m in padVm.Mappings)
                        if (m != null) m.IsInputActive = false;
                    padVm.ClearPipelineLiveness();
                }

                // Unsubscribe from ViewModel property changes (event
                // subscriptions are thread-safe but the surrounding state
                // touches PadVMs, so we keep this on the UI thread for
                // the per-pad iteration).
                _mainVm.Settings.PropertyChanged -= OnSettingsPropertyChanged;
                _mainVm.Dashboard.PropertyChanged -= OnDashboardPropertyChanged;
                _mainVm.Dashboard.ResetTouchpadOverlayPositionRequested -= OnResetTouchpadOverlayPosition;

                // NOTE: do NOT unsubscribe the constructor-only handlers here
                // (Devices.PropertyChanged, and per-pad SelectedDeviceChanged /
                // MappingsRebuilt / LayerActivated). Start() never re-adds them,
                // so tearing them down on an engine Stop permanently breaks
                // device-selection / mapping-rebuild / layer-switch until the
                // app restarts. They are bound to app-lifetime VMs, not the
                // engine, so they correctly persist across Stop/Start.

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
                if (_shiftLayerFlyout != null)
                {
                    _shiftLayerFlyout.Close();
                    _shiftLayerFlyout = null;
                }
            });

            // Background-safe: foreground monitor, servers, audio detector,
            // device hiding teardown.  None of these touch WPF VMs or UI
            // controls.
            if (_foregroundMonitor != null)
            {
                _foregroundMonitor.ProfileSwitchRequired -= OnAutoProfileSwitchRequired;
                _foregroundMonitor = null;
            }
            StopDsuServer();
            StopWebServer();
            StopRemoteLink();
            StopAudioBassDetector();
            // Honor the persistent-cloaks setting on shutdown only.
            // Mid-session toggling EnableInputHiding off still decloaks
            // immediately (handled by the property-change branch around
            // line ~2080), as expected.
            RemoveDeviceHiding(keepCloaks: _mainVm.Settings.KeepHidHideCloaksBetweenLaunches);

            // Heavy engine teardown — InputManager.Stop calls
            // AwaitPendingLifecycleTasks (waits for in-flight HM connect /
            // dispose tasks), DestroyAllVirtualControllers, and
            // DisposeHMaestroContextOnShutdown.  Each can take many
            // seconds.  Runs on whatever thread Stop was called from;
            // engine-toggle button wraps this whole method in Task.Run
            // for that reason.
            try { PadForge.Common.Input.NfcTagRegistry.RegistryChanged -= OnNfcTagRegistryChanged; } catch { }
            if (_inputManager != null)
            {
                _inputManager.DevicesUpdated -= OnDevicesUpdated;
                _inputManager.FrequencyUpdated -= OnFrequencyUpdated;
                _inputManager.ErrorOccurred -= OnErrorOccurred;
                _inputManager.HmVcInactivityDestroyed -= OnHmVcInactivityDestroyed;
                _inputManager.HmVcWentNonActive -= OnHmVcWentNonActive;
                foreach (var pad in _mainVm.Pads)
                    pad.ActiveDeviceConfigPropertyChanged -= OnDeviceConfigChanged;
                _inputManager.Stop();
                _inputManager.Dispose();
                _inputManager = null;
                UserEffectsDispatcher.SlotButtonsProvider = null;
                UserEffectsDispatcher.SlotRumbleForDeviceProvider = null;
                UserEffectsDispatcher.SlotRawRumbleProvider = null;
                UserEffectsDispatcher.SlotImpulseTriggerForDeviceProvider = null;
                UserEffectsDispatcher.SteeringAtResistanceProvider = null;
                UserEffectsDispatcher.SteeringTriggerVibProvider = null;
                UserEffectsDispatcher.TestRumbleTargetGuidProvider = null;
                UserEffectsDispatcher.SlotBatteryPercentProvider = null;
                UserEffectsDispatcher.SlotPerDeviceConfigsProvider = null;
                PadForge.Engine.Common.Mapping.SourceCoercion.GyroBiasProvider = null;
                PadForge.Engine.Common.Mapping.SourceCoercion.GyroTuningProvider = null;
                PadForge.Engine.Common.Mapping.SourceCoercion.SlotStickDeflectionProvider = null;
                PadForge.Engine.Common.Mapping.SourceCoercion.GravityProvider = null;
                PadForge.Engine.Common.Mapping.SourceCoercion.GravityProviderAux = null;
                PadForge.Engine.Common.Mapping.SourceCoercion.ButtonHeldProvider = null;
                PadForge.Engine.Common.Mapping.SourceCoercion.BalanceCalibrationProvider = null;
                PadForge.Engine.Common.Mapping.SourceCoercion.BalanceTareKgProvider = null;
                PadForge.Engine.Common.Mapping.SourceCoercion.IrTuningProvider = null;
                PadForge.Engine.Common.Mapping.SourceCoercion.IrPointerModeProvider = null;
                InputManager.PointerModeCycleApply = null;
                InputManager.GuideLedApply = null;
                PadForge.Engine.Common.Mapping.SourceCoercion.PollHzProvider = null;
                PadForge.Engine.Common.Mapping.SourceCoercion.AimEngageStateProvider = null;
                PadForge.Engine.Common.Mapping.SourceCoercion.TouchpadGestureFiredProvider = null;
                PadForge.Engine.Common.Mapping.SourceCoercion.MouseGestureFiredProvider = null;
                PadForge.Engine.Common.Mapping.SourceCoercion.TouchpadGestureAxisProvider = null;
                PadForge.Engine.Common.Mapping.SourceCoercion.TouchpadMouseSettingsProvider = null;
                // #107: stop sampling the cursor and unhook MouseCursorProvider.
                _cursorControlService?.Dispose();
                _cursorControlService = null;
                lock (_gravityStateLock) { _gravityState.Clear(); _gravityStateAux.Clear(); }
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
            // Multi-finger path: pull the full TouchpadInputState snapshot
            // from the overlay (slots 0..OverlayMaxFingers-1, contact IDs
            // mapped from WPF TouchDevice.Id) and forward to the engine
            // device via UpdateStateMulti. The 2-finger UpdateState(...)
            // overload is still available for callers that only need the
            // DS4-shape struct (e.g. virtual-output sites).
            if (_touchpadOverlay?.IsVisible == true && _touchpadOverlayDevice != null)
            {
                var snap = _touchpadOverlay.GetMultiFingerState(out bool click);
                _touchpadOverlayDevice.UpdateStateMulti(snap, click);
            }

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

            // ── Handle macro-requested bulk VC disable/enable toggle (#91) ──
            // Action wired by MainWindow — fans out to DeviceService.SetSlotEnabled
            // across every created slot and refreshes the sidebar power visuals.
            // Overlay confirmation slides in afterward with the resulting state.
            if (_inputManager.PendingToggleVCsDisabled)
            {
                _inputManager.PendingToggleVCsDisabled = false;

                // Skip silently when no slots are created — combo pressed with
                // an empty config produces no observable state, so no toast.
                bool anyCreated = false;
                for (int i = 0; i < InputManager.MaxPads; i++)
                {
                    if (SettingsManager.SlotCreated[i]) { anyCreated = true; break; }
                }
                if (anyCreated)
                {
                    ToggleVCsDisabled?.Invoke();

                    // Resulting state: any created slot still enabled after the
                    // bulk flip ⇒ we just enabled them; otherwise we disabled them.
                    bool anyEnabled = false;
                    for (int i = 0; i < InputManager.MaxPads; i++)
                    {
                        if (SettingsManager.SlotCreated[i] && SettingsManager.SlotEnabled[i])
                        { anyEnabled = true; break; }
                    }
                    ShowVCsToggleOverlay(anyEnabled);
                }
            }

            // ── Update Pad ViewModels ──
            for (int i = 0; i < InputManager.MaxPads && i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                var gp = _inputManager.CombinedOutputStates[i];
                // Two meter feeds:
                //   FinalVibrationStates           → preview-tab bar (slot max)
                //   SelectedDeviceVibrationStates  → FFB-tab bar (selected dev)
                var vibration = _inputManager.FinalVibrationStates[i];
                var selectedDeviceVibration = _inputManager.SelectedDeviceVibrationStates[i];

                padVm.UpdateFromEngineState(gp, vibration, selectedDeviceVibration);
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
                        {
                            // Per-device Sticks/Triggers preview reads the
                            // PHYSICAL device's raw stick/trigger axes, NOT
                            // the post-MappingSet intermediate. Multi-device
                            // slots use MappingSet rows whose sources are
                            // bound to a specific InstanceGuid, so a row
                            // sourcing from Device A produces zero output
                            // when Device B's UserSetting is evaluated —
                            // which is correct for output mapping but wrong
                            // for a per-device input preview. Reading
                            // ud.InputState directly shows each physical
                            // device's actual stick/trigger position.
                            padVm.UpdateDeviceState(BuildPerDeviceSticksFromInputState(selected.InstanceGuid, us));
                        }
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

                // Push live gyro rate + calibration label so the
                // Gyro tab readouts track the selected (device, slot).
                // — also tick the per-device gravity low-pass so
                // Player/World Space gyro projection has fresh state.
                {
                    var selected = padVm.SelectedMappedDevice;
                    if (selected != null && selected.InstanceGuid != Guid.Empty)
                    {
                        UserDevice ud = FindUserDevice(selected.InstanceGuid);
                        if (ud != null && (ud.HasGyro || ud.HasAccel))
                        {
                            const double RadToDeg = 180.0 / System.Math.PI;
                            const double MsToG    = 1.0 / 9.80665;
                            var us = SettingsManager.FindSettingByInstanceGuidAndSlot(selected.InstanceGuid, i);
                            var ps = us?.GetPadSetting();
                            float bp = ps != null ? TryParseFloatPs(ps.GyroBiasPitch, 0f) : 0f;
                            float by = ps != null ? TryParseFloatPs(ps.GyroBiasYaw,   0f) : 0f;
                            float br = ps != null ? TryParseFloatPs(ps.GyroBiasRoll,  0f) : 0f;
                            var st = ud.InputState;
                            if (st != null && ud.HasGyro && st.Gyro != null && st.Gyro.Length >= 3)
                            {
                                padVm.GyroLiveRatePitch = (st.Gyro[0] - bp) * RadToDeg;
                                padVm.GyroLiveRateYaw   = (st.Gyro[1] - by) * RadToDeg;
                                padVm.GyroLiveRateRoll  = (st.Gyro[2] - br) * RadToDeg;
                            }
                            if (st != null && ud.HasAccel && st.Accel != null && st.Accel.Length >= 3)
                            {
                                padVm.AccelLiveX = st.Accel[0] * MsToG;
                                padVm.AccelLiveY = st.Accel[1] * MsToG;
                                padVm.AccelLiveZ = st.Accel[2] * MsToG;
                            }
                            string ts = ps?.GyroCalibratedAtUtc;
                            if (string.IsNullOrEmpty(ts) ||
                                !DateTime.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture,
                                                   System.Globalization.DateTimeStyles.RoundtripKind, out var when))
                            {
                                padVm.GyroCalibrationLabel = Strings.Instance.Settings_GyroNeverCalibrated;
                            }
                            else
                            {
                                padVm.GyroCalibrationLabel = string.Format(Strings.Instance.Settings_GyroLastCalibrated_Format, when.ToLocalTime());
                            }
                        }
                    }
                }
            }

            // ── gravity low-pass for Player/World Space gyro ──
            UpdateGravityEstimates();

            // ── Update Dashboard ──
            UpdateDashboard();

            // ── Drive the v3 shift-layer flyout. Polls the engine's
            //    engagement state for the currently-selected pad and
            //    surfaces a Win11-style bottom-center flyout with the
            //    layer name + color whenever the slot is on a non-Base
            //    layer.
            UpdateShiftLayerFlyout();

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

            // ── Battery indicators (#167): slow lane, the wrapper's own cache
            //    only refreshes every 5 s, so ticking faster reads the same
            //    numbers. Updates the Devices rows and the pad device roster. ──
            long nowTick = Environment.TickCount64;
            if (nowTick - _lastBatteryUiRefreshTick >= 5000)
            {
                _lastBatteryUiRefreshTick = nowTick;
                UpdateBatteryIndicators();
            }

            // ── Guide Button LED (#209): slow lane. Battery mode tracks the
            //    charge level, and Fixed mode heals from missed writes (a pad
            //    that had not announced yet when the config was applied). The
            //    writers change-detect, so an unchanged brightness costs no
            //    device I/O. ──
            if (nowTick - _lastGuideLedApplyTick >= 30000)
            {
                _lastGuideLedApplyTick = nowTick;
                ApplyGuideLeds();
            }

            // ── Macro trigger recording (accumulate buttons) ──
            UpdateMacroTriggerRecording();

            // ── Macro custom-expression per-variable recording (single input) ──
            UpdateExpressionVariableRecording();

            // ── Push ViewModel settings to PadSetting objects (runtime sync) ──
            SyncViewModelToPadSettings();

            // ── Sync macro snapshots to engine ──
            SyncMacroSnapshots();

            // ── Update audio rumble level meters + sync detector on/off ──
            if (_audioBassDetector != null)
            {
                double level = _audioBassDetector.BassEnergy;
                double triggerLevel = _audioBassDetector.TriggerBassEnergy;
                for (int i = 0; i < _mainVm.Pads.Count; i++)
                {
                    if (!SettingsManager.SlotCreated[i]) continue;
                    if (_mainVm.Pads[i].AudioRumbleEnabled)
                        _mainVm.Pads[i].AudioRumbleLevelMeter = level;
                    if (_mainVm.Pads[i].AudioRumbleTriggersEnabled)
                        _mainVm.Pads[i].AudioRumbleTriggersLevelMeter = triggerLevel;
                }
            }

            // ── Auto-idle engine when no slots are created ──
            UpdateIdleState();

            // ── Auto-profile switching (check foreground window) ──
            _foregroundMonitor?.CheckForegroundWindow();

            // ── Profiles-page foreground readout (#175 item 8): 1 s slow
            //    lane riding this timer like the battery lane above. Only
            //    fed while auto-switching is enabled, matching the page,
            //    which hides the line then (the monitor is not tracking). ──
            if (nowTick - _lastForegroundUiRefreshTick >= 1000)
            {
                _lastForegroundUiRefreshTick = nowTick;
                if (_foregroundMonitor != null && SettingsManager.EnableAutoProfileSwitching)
                {
                    string exe = System.IO.Path.GetFileName(_foregroundMonitor.LastForegroundExePath);
                    _mainVm.Settings.ForegroundExeName = string.IsNullOrEmpty(exe) ? "-" : exe;
                    _mainVm.Settings.IsForegroundMatched = _foregroundMonitor.LastMatchedProfileId != null;
                }
            }
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

            // Stay at full poll rate while a paired peer is connected: this PC may be
            // sharing its physical devices, and idling to ~20 Hz would sample that
            // shared input choppily on the consumer even with no local slot active (#138).
            bool remoteSharing = _linkServer != null && _linkServer.IsRunning && _linkServer.HasConnections;

            _inputManager.IsIdle = !anyActive && !remoteSharing;
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
                "Running" => Strings.Instance.Engine_Forging,
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

        // Reusable buffer for the non-allocating FindByPadIndex overload,
        // grown to the live UserSettings count so a per-slot query never
        // truncates. Shared by the two 30 Hz refreshes below, which both run
        // on the UI thread in sequence, so no cross-thread contention.
        private UserSetting[] _slotSettingsBuf = System.Array.Empty<UserSetting>();

        private UserSetting[] RentSlotSettingsBuffer(SettingsCollection settings)
        {
            int need = settings?.Count ?? 0;
            if (_slotSettingsBuf.Length < need)
                _slotSettingsBuf = new UserSetting[need];
            return _slotSettingsBuf;
        }

        /// <summary>Closure-free replacement for devices.Any(d =&gt; d.Guid == g
        /// &amp;&amp; d.IsOnline). The LINQ form allocated a capture per call at
        /// 30 Hz across every slot and roster line.</summary>
        private static bool AnyOnlineMatch(UserDevice[] devices, Guid guid)
        {
            if (devices == null) return false;
            for (int i = 0; i < devices.Length; i++)
            {
                var d = devices[i];
                if (d != null && d.InstanceGuid == guid && d.IsOnline) return true;
            }
            return false;
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

            var devArr = devices as UserDevice[] ?? devices?.ToArray();
            var settings = SettingsManager.UserSettings;
            var buf = RentSlotSettingsBuffer(settings);

            foreach (var slot in dash.SlotSummaries)
            {
                int padIndex = slot.PadIndex;
                if (padIndex < 0 || padIndex >= _mainVm.Pads.Count) continue;

                var padVm = _mainVm.Pads[padIndex];

                slot.IsActive = padVm.IsDeviceOnline;
                slot.DeviceName = padVm.MappedDeviceName;
                // Ember card device roster (#175): the card renders one entry
                // per mapped device with its own dynamic battery glyph.
                slot.MappedDevices = padVm.MappedDevices;

                int mappedCount = settings?.FindByPadIndex(padIndex, buf) ?? 0;
                int connectedCount = 0;
                for (int s = 0; s < mappedCount; s++)
                {
                    if (AnyOnlineMatch(devArr, buf[s].InstanceGuid))
                        connectedCount++;
                }

                // Per-device connect state (#175 phase 2 item 14): refresh
                // each roster item's IsOnline from the same snapshot the
                // connectedCount above reads, so a card can never say
                // "0 connected" while its roster lines render as live.
                foreach (var dev in padVm.MappedDevices)
                    dev.IsOnline = AnyOnlineMatch(devArr, dev.InstanceGuid);

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

            // ── Pipeline heat ledger (#175 item 10): 1 s slow lane riding
            //    this refresh. Config only moves on user edits, so a 30 Hz
            //    recompute would just churn strings. ──
            long ledgerTick = Environment.TickCount64;
            if (ledgerTick - _lastStageLedgerRefreshTick >= 1000)
            {
                _lastStageLedgerRefreshTick = ledgerTick;
                foreach (var slot in dash.SlotSummaries)
                {
                    if (slot.PadIndex < 0 || slot.PadIndex >= _mainVm.Pads.Count) continue;
                    RefreshSlotStageLedger(slot, _mainVm.Pads[slot.PadIndex], devices);
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Pipeline heat ledger (#175 item 10)
        // ─────────────────────────────────────────────

        private long _lastStageLedgerRefreshTick;

        // Stage glyphs are the same characters the matching tab page
        // headers use (Gyro E7AD, Lighting E781, Touchpad EFA5, Audio
        // E767). Sticks / Triggers render the Zacksly shapes instead of
        // a font glyph, matching their LabeledShapeIcon headers.
        private const string StageGlyphGyro = "\uE7AD";
        private const string StageGlyphLighting = "\uE781";
        private const string StageGlyphTouchpad = "\uEFA5";
        private const string StageGlyphAudio = "\uE767";

        /// <summary>
        /// Rebuilds one slot card's stage ledger. Stage membership mirrors
        /// the PadPage.SyncTabVisibility capability gates (union across the
        /// slot's assigned devices instead of the selected one, because the
        /// card summarizes the whole slot); heat compares each stage's
        /// stored settings against the PadSetting / config defaults. Every
        /// value shown in a summary is read from the real setting object.
        /// </summary>
        private void RefreshSlotStageLedger(SlotSummary slot, PadViewModel padVm, IEnumerable<UserDevice> devices)
        {
            var slotSettings = SettingsManager.UserSettings?.FindByPadIndex(slot.PadIndex);

            // Pair each assigned setting with its device caps + PadSetting.
            bool capGyro = false, capLightbar = false, capTouchpad = false, capAudio = false;
            var bindings = new List<(PadSetting Ps, UserDevice Ud, Guid Guid)>();
            if (slotSettings != null)
            {
                foreach (var us in slotSettings)
                {
                    if (us == null) continue;
                    UserDevice ud = null;
                    if (devices != null)
                    {
                        foreach (var d in devices)
                        {
                            if (d != null && d.InstanceGuid == us.InstanceGuid) { ud = d; break; }
                        }
                    }
                    bindings.Add((us.GetPadSetting(), ud, us.InstanceGuid));
                    if (ud == null) continue;

                    capGyro |= ud.HasGyro;
                    capTouchpad |= ud.HasTouchpad;
                    // Same VID/PID gate SyncTabVisibility uses for the
                    // Lighting tab: DualSense / Edge / DS4, plus the #209
                    // Guide LED population (XInput-pathed pads and the
                    // 2015 Steam Controller) now that the tab shows for
                    // them too.
                    bool sonyLightbar = ud.VendorId == 0x054C
                        && (ud.ProdId == 0x0CE6 || ud.ProdId == 0x0DF2
                         || ud.ProdId == 0x05C4 || ud.ProdId == 0x09CC || ud.ProdId == 0x0BA0);
                    capLightbar |= sonyLightbar
                        || PadForge.Common.Input.XboxGipGuideLedWriter.IsXboxGipPathed(ud)
                        || PadForge.Common.Input.SteamHomeLedSetter.IsSteamController2015(ud.VendorId, ud.ProdId);
                    // Audio tab gate: Sony speaker family, Wii Remote
                    // speaker, or haptic-tone actuators (#147).
                    capAudio |= sonyLightbar
                        || WiiSpeakerService.DeviceHasSpeaker(ud)
                        || HapticToneService.DeviceHasHaptics(ud);
                }
            }

            // Sticks / Triggers follow the output-type gates SyncTabVisibility
            // applies to their tabs (MIDI has neither; KBM keeps Sticks for
            // Mouse X/Y but drops Triggers). Both need at least one assigned
            // device so an empty slot's ledger stays empty.
            bool isMidi = padVm.OutputType == VirtualControllerType.Midi;
            bool isKbm = padVm.OutputType == VirtualControllerType.KeyboardMouse;
            bool capSticks = !isMidi && bindings.Count > 0;
            bool capTriggers = !isMidi && !isKbm && bindings.Count > 0;

            // One readout line per device per stage (user report
            // 2026-07-06: the flat cross-device union hid which device
            // owned which value and dropped devices entirely). Devices
            // still at defaults read STOCK so every assigned device is
            // accounted for. Attribution comes from the card roster,
            // which keeps offline devices' stored names.
            (string Glyph, string Name) Attr(Guid g)
            {
                var roster = slot.MappedDevices;
                if (roster != null)
                {
                    foreach (var mi in roster)
                        if (mi != null && mi.InstanceGuid == g)
                            return (mi.TypeGlyph, mi.Name);
                }
                string s = g.ToString();
                return ("", s.Length > 8 ? s.Substring(0, 8) + "…" : s);
            }

            void AddLine(List<StageSummaryLine> lines, ref bool hot, Guid g, List<string> parts)
            {
                hot |= parts.Count > 0;
                var (gl, name) = Attr(g);
                lines.Add(new StageSummaryLine
                {
                    // Trailing space rides the glyph so glyph-less lines
                    // (slot-level VOL) don't start with a stray indent.
                    DeviceGlyph = gl.Length > 0 ? gl + " " : string.Empty,
                    // Icon, NAME, then settings (user direction 2026-07-06),
                    // the same order as the card's main device label. The
                    // name carries the separator so slot-level lines with
                    // no attribution stay clean.
                    DeviceName = name.Length > 0 ? name + "  ·  " : string.Empty,
                    Tokens = parts.Count > 0 ? string.Join(" · ", parts) : "STOCK",
                });
            }

            var stickLines = new List<StageSummaryLine>();
            var triggerLines = new List<StageSummaryLine>();
            var gyroLines = new List<StageSummaryLine>();
            var lightingLines = new List<StageSummaryLine>();
            var touchpadLines = new List<StageSummaryLine>();
            var audioLines = new List<StageSummaryLine>();
            bool stickHot = false, triggerHot = false, gyroHot = false,
                 lightingHot = false, touchpadHot = false, audioHot = false;

            foreach (var (ps, ud, guid) in bindings)
            {
                if (ps != null && capSticks)
                {
                    var parts = new List<string>();
                    AppendStickStageTokens(parts, ps);
                    AddLine(stickLines, ref stickHot, guid, parts);
                }
                if (ps != null && capTriggers)
                {
                    var parts = new List<string>();
                    AppendTriggerStageTokens(parts, ps);
                    AddLine(triggerLines, ref triggerHot, guid, parts);
                }
                // Device-capability stages only line up the devices that
                // actually have the hardware; a STOCK gyro line on a
                // gyro-less sibling would be noise.
                if (ps != null && ud != null && ud.HasGyro)
                {
                    var parts = new List<string>();
                    AppendGyroStageTokens(parts, ps);
                    AddLine(gyroLines, ref gyroHot, guid, parts);
                }

                bool sonyLightbar = ud != null && ud.VendorId == 0x054C
                    && (ud.ProdId == 0x0CE6 || ud.ProdId == 0x0DF2
                     || ud.ProdId == 0x05C4 || ud.ProdId == 0x09CC || ud.ProdId == 0x0BA0);
                bool devAudio = sonyLightbar
                    || (ud != null && (WiiSpeakerService.DeviceHasSpeaker(ud)
                                    || HapticToneService.DeviceHasHaptics(ud)));
                DeviceSlotConfig cfg = null;
                if (ud != null)
                    padVm.PerDeviceSlotConfigs.TryGetValue(ud.InstanceGuid, out cfg);

                // Guide LED population (#209): the lighting stage line
                // covers these devices too, mirroring the Lighting tab.
                bool devGuideLed = ud != null
                    && (PadForge.Common.Input.XboxGipGuideLedWriter.IsXboxGipPathed(ud)
                     || PadForge.Common.Input.SteamHomeLedSetter.IsSteamController2015(ud.VendorId, ud.ProdId));
                if (sonyLightbar || devGuideLed)
                {
                    // Lighting defaults: LightbarMode.PlayerNumber base +
                    // InputReactiveMode.Off overlay (DeviceSlotConfig
                    // field initializers). Tokens = the picked mode names,
                    // including the deliberate Off (hard dark).
                    var parts = new List<string>();
                    if (cfg != null && sonyLightbar)
                    {
                        if (cfg.LightbarMode != LightbarMode.PlayerNumber)
                            AddToken(parts, MacroAction.LightbarModeDisplayName(cfg.LightbarMode));
                        if (cfg.InputReactiveMode != InputReactiveMode.Off)
                            AddToken(parts, InputReactiveModeDisplayName(cfg.InputReactiveMode));
                        // Indicator LEDs. Defaults: PlayerLedMode
                        // PlayerNumber, PlayerLedBrightness High,
                        // MicLedMode Off (DeviceSlotConfig field
                        // initializers; ResetIndicatorLedsAllCommand
                        // restores them).
                        if (cfg.PlayerLedMode != PlayerLedMode.PlayerNumber)
                            AddToken(parts, cfg.PlayerLedMode switch
                            {
                                PlayerLedMode.All => "PLED ALL",
                                PlayerLedMode.Off => "PLED OFF",
                                _ => "PLED P" + (int)cfg.PlayerLedMode,
                            });
                        if (cfg.PlayerLedBrightness != PlayerLedBrightness.High)
                            AddToken(parts, cfg.PlayerLedBrightness == PlayerLedBrightness.Medium
                                ? "PLED MED" : "PLED LOW");
                        if (cfg.MicLedMode != MicLedMode.Off)
                            AddToken(parts, cfg.MicLedMode switch
                            {
                                MicLedMode.Pulse => "MIC PULSE",
                                MicLedMode.FollowDeviceMute => "MIC FOLLOW",
                                _ => "MIC SOLID",
                            });
                    }
                    // Guide LED default: DeviceDefault (write nothing,
                    // DeviceSlotConfig field initializer;
                    // ResetGuideLedAllCommand restores it).
                    if (cfg != null && devGuideLed
                        && cfg.GuideLedMode != GuideLedMode.DeviceDefault)
                    {
                        AddToken(parts, cfg.GuideLedMode == GuideLedMode.Battery
                            ? "GUIDE BATT"
                            : "GUIDE " + cfg.GuideLedBrightness + "%");
                    }
                    AddLine(lightingLines, ref lightingHot, guid, parts);
                }
                if (devAudio)
                {
                    // Audio default: passthrough off (DeviceSlotConfig
                    // initializer; ResetSoundOutputAllCommand restores it).
                    var parts = new List<string>();
                    if (cfg != null && cfg.AudioPassthroughEnabled)
                        AddToken(parts, "PASSTHROUGH");
                    AddLine(audioLines, ref audioHot, guid, parts);
                }
                if (ud != null && ud.HasTouchpad)
                {
                    var parts = new List<string>();
                    if (ps?.TouchpadSettings != null)
                    {
                        foreach (var entry in ps.TouchpadSettings)
                        {
                            var ts = entry?.Settings;
                            if (ts == null) continue;
                            // Touchpad defaults: every feature toggle off
                            // (TouchpadGestureSettings.Default(), "touchpad
                            // mappings are opt-in").
                            if (ts.Enabled) AddToken(parts, "GESTURES");
                            if (ts.EnableJoystickOutput) AddToken(parts, "JOYSTICK");
                            if (ts.MouseSensitivityX != 1.0f || ts.MouseSensitivityY != 1.0f
                                || ts.MouseInvertX || ts.MouseInvertY)
                                AddToken(parts, "MOUSE");
                        }
                    }
                    AddLine(touchpadLines, ref touchpadHot, guid, parts);
                }
            }

            // Macro-sound master volume default 100 (PadViewModel
            // _soundMasterVolume initializer / reset command). Slot-level,
            // so the line carries no device attribution.
            if (capAudio && padVm.SoundMasterVolume != 100)
            {
                audioHot = true;
                audioLines.Add(new StageSummaryLine
                {
                    Tokens = "VOL " + padVm.SoundMasterVolume.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%",
                });
            }

            // Desired membership in tab order. A stage only appears when
            // the slot HAS it (a gyro glyph on a gyro-less pad is noise).
            var desired = new List<(string Kind, string Glyph, bool Stick, bool Trig, bool Hot, List<StageSummaryLine> Lines)>();
            if (capSticks) desired.Add(("Sticks", string.Empty, true, false, stickHot, stickLines));
            if (capTriggers) desired.Add(("Triggers", string.Empty, false, true, triggerHot, triggerLines));
            if (capGyro) desired.Add(("Gyro", StageGlyphGyro, false, false, gyroHot, gyroLines));
            if (capLightbar) desired.Add(("Lighting", StageGlyphLighting, false, false, lightingHot, lightingLines));
            if (capTouchpad) desired.Add(("Touchpad", StageGlyphTouchpad, false, false, touchpadHot, touchpadLines));
            if (capAudio) desired.Add(("Audio", StageGlyphAudio, false, false, audioHot, audioLines));

            // Membership changed → rebuild; otherwise mutate in place so
            // the card's ItemsControl doesn't re-template every second.
            bool sameMembership = slot.StageLedger.Count == desired.Count;
            if (sameMembership)
            {
                for (int i = 0; i < desired.Count; i++)
                {
                    if (!string.Equals(slot.StageLedger[i].Kind, desired[i].Kind, StringComparison.Ordinal))
                    { sameMembership = false; break; }
                }
            }
            if (!sameMembership)
            {
                slot.StageLedger.Clear();
                foreach (var d in desired)
                    slot.StageLedger.Add(new SlotStageInfo(d.Kind, d.Glyph, d.Stick, d.Trig));
            }
            for (int i = 0; i < desired.Count; i++)
            {
                var info = slot.StageLedger[i];
                info.IsHot = desired[i].Hot;
                // Inert stages keep no tooltip (design: the ashen glyph
                // is the whole statement). Hot stages list every covered
                // device. The composite string is the change key so the
                // line collection only churns when values changed.
                string composite = string.Empty;
                if (desired[i].Hot)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var ln in desired[i].Lines)
                        sb.Append(ln.DeviceGlyph).Append('|').Append(ln.Tokens)
                          .Append('|').Append(ln.DeviceName).Append('\n');
                    composite = sb.ToString();
                }
                if (!string.Equals(info.Summary, composite, StringComparison.Ordinal))
                {
                    info.Summary = composite;
                    info.SummaryLines.Clear();
                    if (desired[i].Hot)
                        foreach (var ln in desired[i].Lines)
                            info.SummaryLines.Add(ln);
                }
            }
        }

        /// <summary>Adds a token once (union across devices dedupes).</summary>
        private static void AddToken(List<string> parts, string token)
        {
            if (!parts.Contains(token)) parts.Add(token);
        }

        /// <summary>Adds "label n%" when the stored value differs from
        /// its default. Values are PadSetting invariant strings.</summary>
        private static void AddPctToken(List<string> parts, string label, string value, float defaultValue)
        {
            float v = TryParseFloatPs(value, defaultValue);
            if (Math.Abs(v - defaultValue) < 0.0001f) return;
            AddToken(parts, label + " " + v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%");
        }

        private static bool PsFlagSet(string value) => TryParseFloatPs(value, 0f) != 0f;

        /// <summary>Stick-stage tokens for one PadSetting side pair.
        /// Defaults compared against are the PadSetting field initializers:
        /// deadzone / anti-deadzone / linear / center offset "0", max range
        /// "100" (Neg variants empty = default), deadzone shape "2"
        /// (ScaledRadial), sensitivity curves "0" (linear), inverts "0".</summary>
        private static void AppendStickStageTokens(List<string> parts, PadSetting ps)
        {
            AppendStickSideTokens(parts, "L",
                ps.LeftThumbDeadZoneX, ps.LeftThumbDeadZoneY,
                ps.LeftThumbAntiDeadZoneX, ps.LeftThumbAntiDeadZoneY, ps.LeftThumbAntiDeadZone,
                ps.LeftThumbLinear,
                ps.LeftThumbSensitivityCurveX, ps.LeftThumbSensitivityCurveY,
                ps.LeftThumbDeadZoneShape,
                ps.LeftThumbAxisXInvert, ps.LeftThumbAxisYInvert,
                ps.LeftThumbMaxRangeX, ps.LeftThumbMaxRangeY,
                ps.LeftThumbMaxRangeXNeg, ps.LeftThumbMaxRangeYNeg,
                ps.LeftThumbCenterOffsetX, ps.LeftThumbCenterOffsetY);
            AppendStickSideTokens(parts, "R",
                ps.RightThumbDeadZoneX, ps.RightThumbDeadZoneY,
                ps.RightThumbAntiDeadZoneX, ps.RightThumbAntiDeadZoneY, ps.RightThumbAntiDeadZone,
                ps.RightThumbLinear,
                ps.RightThumbSensitivityCurveX, ps.RightThumbSensitivityCurveY,
                ps.RightThumbDeadZoneShape,
                ps.RightThumbAxisXInvert, ps.RightThumbAxisYInvert,
                ps.RightThumbMaxRangeX, ps.RightThumbMaxRangeY,
                ps.RightThumbMaxRangeXNeg, ps.RightThumbMaxRangeYNeg,
                ps.RightThumbCenterOffsetX, ps.RightThumbCenterOffsetY);
        }

        private static void AppendStickSideTokens(List<string> parts, string side,
            string dzX, string dzY, string adzX, string adzY, string adzLegacy,
            string linear, string curveX, string curveY, string shape,
            string invX, string invY, string maxX, string maxY,
            string maxXNeg, string maxYNeg, string offX, string offY)
        {
            // Per-axis values carry the axis in the label (LX DZ 10% /
            // LY DZ 20%). Side-wide values keep the bare side prefix.
            string ax = side + "X", ay = side + "Y";
            AddPctToken(parts, ax + " DZ", dzX, 0f);
            AddPctToken(parts, ay + " DZ", dzY, 0f);
            AddPctToken(parts, ax + " ADZ", adzX, 0f);
            AddPctToken(parts, ay + " ADZ", adzY, 0f);
            AddPctToken(parts, side + " ADZ", adzLegacy, 0f);
            AddPctToken(parts, side + " LIN", linear, 0f);
            if (!CurveLut.IsLinear(curveX))
                AddToken(parts, ax + " CURVE " + CurveLut.MatchPreset(curveX));
            if (!CurveLut.IsLinear(curveY))
                AddToken(parts, ay + " CURVE " + CurveLut.MatchPreset(curveY));
            // Shape default 2 = ScaledRadial (PadSetting initializer).
            if (!string.IsNullOrEmpty(shape) && TryParseFloatPs(shape, 2f) != 2f)
                AddToken(parts, side + " SHAPE");
            if (PsFlagSet(invX)) AddToken(parts, ax + " INV");
            if (PsFlagSet(invY)) AddToken(parts, ay + " INV");
            AddPctToken(parts, ax + " RANGE", maxX, 100f);
            AddPctToken(parts, ay + " RANGE", maxY, 100f);
            // Neg ranges default to null (unset = mirror of positive).
            // The "-" marks the negative direction of the axis.
            if (!string.IsNullOrEmpty(maxXNeg)) AddPctToken(parts, ax + "- RANGE", maxXNeg, 100f);
            if (!string.IsNullOrEmpty(maxYNeg)) AddPctToken(parts, ay + "- RANGE", maxYNeg, 100f);
            if (PsFlagSet(offX) || PsFlagSet(offY)) AddToken(parts, side + " OFFSET");
        }

        /// <summary>Trigger-stage tokens. Defaults per the PadSetting
        /// initializers: deadzone / anti "0", max range "100", curve "0"
        /// (linear).</summary>
        private static void AppendTriggerStageTokens(List<string> parts, PadSetting ps)
        {
            AddPctToken(parts, "LT DZ", ps.LeftTriggerDeadZone, 0f);
            AddPctToken(parts, "RT DZ", ps.RightTriggerDeadZone, 0f);
            AddPctToken(parts, "LT ADZ", ps.LeftTriggerAntiDeadZone, 0f);
            AddPctToken(parts, "RT ADZ", ps.RightTriggerAntiDeadZone, 0f);
            AddPctToken(parts, "LT RANGE", ps.LeftTriggerMaxRange, 100f);
            AddPctToken(parts, "RT RANGE", ps.RightTriggerMaxRange, 100f);
            if (!CurveLut.IsLinear(ps.LeftTriggerSensitivityCurve))
                AddToken(parts, "LT CURVE " + CurveLut.MatchPreset(ps.LeftTriggerSensitivityCurve));
            if (!CurveLut.IsLinear(ps.RightTriggerSensitivityCurve))
                AddToken(parts, "RT CURVE " + CurveLut.MatchPreset(ps.RightTriggerSensitivityCurve));
        }

        /// <summary>Gyro-stage tokens. Defaults are the PadSetting
        /// initializers the Gyro tab authors: sensitivity 1.0, deadzone
        /// 3.0°/s, tightening 3.0°/s, smoothing threshold 8.0°/s over a
        /// 50 ms window, acceleration 0, curve Linear, real-world
        /// calibration 0 (off), Easy Aim 0, space Local, no engage
        /// button, passthrough tuning 0 (off), inverts 0. Parse
        /// fallbacks mirror the engine consumer (GyroTuningProvider).
        /// The legacy v3.3 GyroSmoothingAlpha has no UI, so it never
        /// counts as heat. Bias / calibration fields are excluded.
        /// InputService auto-calibrates those without user intent, so
        /// they never count as configuration heat.</summary>
        private static void AppendGyroStageTokens(List<string> parts, PadSetting ps)
        {
            float sensH = TryParseFloatPs(ps.GyroSensitivityH, 1f);
            float sensV = TryParseFloatPs(ps.GyroSensitivityV, 1f);
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (Math.Abs(sensH - 1f) > 0.0001f || Math.Abs(sensV - 1f) > 0.0001f)
                AddToken(parts, "SENS " + sensH.ToString("0.##", ic) + "×" + sensV.ToString("0.##", ic));
            float dz = TryParseFloatPs(ps.GyroDeadZoneDegPerSec, 0f);
            if (Math.Abs(dz - 3f) > 0.0001f)
                AddToken(parts, "DZ " + dz.ToString("0.#", ic) + "°/s");
            float tight = TryParseFloatPs(ps.GyroTighteningThresholdDegPerSec, 0f);
            if (Math.Abs(tight - 3f) > 0.0001f)
                AddToken(parts, "TIGHT " + tight.ToString("0.#", ic));
            float smoothThr = TryParseFloatPs(ps.GyroSmoothingThresholdDegPerSec, 0f);
            float smoothWin = TryParseFloatPs(ps.GyroSmoothingWindowMs, 50f);
            if (Math.Abs(smoothThr - 8f) > 0.0001f || Math.Abs(smoothWin - 50f) > 0.0001f)
                AddToken(parts, "SMOOTH " + smoothThr.ToString("0.#", ic) + "/" + smoothWin.ToString("0.#", ic));
            if (PsFlagSet(ps.GyroAcceleration)) AddToken(parts, "ACCEL");
            if (!string.IsNullOrEmpty(ps.GyroOutputCurve)
                && !string.Equals(ps.GyroOutputCurve, "Linear", StringComparison.Ordinal))
                AddToken(parts, "CURVE " + ps.GyroOutputCurve);
            float rwc = TryParseFloatPs(ps.GyroRealWorldCalibration, 0f);
            if (Math.Abs(rwc) > 0.0001f)
                AddToken(parts, "RWC " + rwc.ToString("0.#", ic));
            AddPctToken(parts, "EASYAIM", ps.GyroEasyAimStickThreshold, 0f);
            if (!string.IsNullOrEmpty(ps.GyroSpace)
                && !string.Equals(ps.GyroSpace, "Local", StringComparison.Ordinal))
                AddToken(parts, "SPACE " + ps.GyroSpace);
            if (!string.IsNullOrEmpty(ps.GyroAimEngageButton)) AddToken(parts, "ENGAGE");
            if (PsFlagSet(ps.GyroInvertPitch)) AddToken(parts, "INV P");
            if (PsFlagSet(ps.GyroInvertYawRoll)) AddToken(parts, "INV Y");
            if (PsFlagSet(ps.GyroApplyTuningToPassthrough)) AddToken(parts, "PASSTHRU");
        }

        // Lightbar base-mode display names reuse MacroAction's existing
        // localized mapping (MacroItem.cs); no second copy here.

        /// <summary>Localized display name for the input-reactive overlay
        /// mode, same strings the Lighting tab's overlay picker shows.</summary>
        private static string InputReactiveModeDisplayName(InputReactiveMode mode) => mode switch
        {
            InputReactiveMode.Random => Strings.Instance.Pad_Lighting_InputReactive_Random,
            InputReactiveMode.Cycle => Strings.Instance.Pad_Lighting_InputReactive_Cycle,
            _ => Strings.Instance.Pad_Lighting_InputReactive_Fixed,
        };

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

            var devArr = devices as UserDevice[] ?? devices?.ToArray();
            var settings = SettingsManager.UserSettings;
            var buf = RentSlotSettingsBuffer(settings);

            foreach (var nav in _mainVm.NavControllerItems)
            {
                int padIndex = nav.PadIndex;
                if (padIndex < 0 || padIndex >= _mainVm.Pads.Count) continue;

                int mappedCount = settings?.FindByPadIndex(padIndex, buf) ?? 0;
                int connCount = 0;
                for (int s = 0; s < mappedCount; s++)
                {
                    if (AnyOnlineMatch(devArr, buf[s].InstanceGuid))
                        connCount++;
                }
                nav.ConnectedDeviceCount = connCount;
                nav.MappedDeviceCount = mappedCount;
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
                bool isMidi = ud.CapType == InputDeviceType.Midi;
                bool isNfc = ud.CapType == InputDeviceType.Nfc;
                int[] btnIndices = ResolveButtonIndices(ud);
                devVm.RebuildRawStateCollections(axisCount, btnIndices, povCount, isKb, isMouse, isTouchpad, isMidi, isNfc,
                    consumerButtons: BuildConsumerPreviewItems(ud));
                devVm.HasGyroData = ud.HasGyro;
                devVm.HasAccelData = ud.HasAccel;
                devVm.HasAccelAuxData = ud.HasAccelAux;
                devVm.HasTouchpadData = ud.HasTouchpad || isTouchpad;
            }

            devVm.HasRawData = true;
        }

        /// <summary>Builds the named chips for the Consumer Control live
        /// preview (issue #168): every canonical-table button with its
        /// localized name, plus any session-dynamic usage the device has
        /// reported ("Consumer 0xNNNN"). Returns null for other device types
        /// so the VM keeps its generic sections.</summary>
        private static System.Collections.Generic.List<ConsumerButtonDisplayItem> BuildConsumerPreviewItems(UserDevice ud)
        {
            if (ud.CapType != InputDeviceType.ConsumerControl) return null;

            // A Remote Link consumer device carries the OWNER's named inputs on
            // the device list (fixed table + whatever dynamic usages the owner
            // has seen), so the preview here reads identically to the owner's
            // own Devices page. The local dynamic-slot table below describes
            // this machine's aggregate consumer state and would be wrong for a
            // remote device.
            if (ud.Device is PadForge.Engine.RemoteLink.RemotePeerDevice remote)
            {
                var remoteItems = new System.Collections.Generic.List<ConsumerButtonDisplayItem>();
                foreach (var obj in remote.GetDeviceObjects())
                {
                    if (!obj.IsButton) continue;
                    remoteItems.Add(new ConsumerButtonDisplayItem
                    {
                        Index = obj.InputIndex,
                        Name = PadForge.Common.MappingDisplayResolver.LocalizeObjectName(obj.Name),
                    });
                }
                return remoteItems;
            }

            var items = new System.Collections.Generic.List<ConsumerButtonDisplayItem>(
                PadForge.Engine.Common.ConsumerUsageTable.Fixed.Length);
            for (int i = 0; i < PadForge.Engine.Common.ConsumerUsageTable.Fixed.Length; i++)
            {
                items.Add(new ConsumerButtonDisplayItem
                {
                    Index = i,
                    Name = PadForge.Common.MappingDisplayResolver.LocalizeObjectName(
                        PadForge.Engine.Common.ConsumerUsageTable.Fixed[i].Name),
                });
            }
            for (int i = PadForge.Engine.Common.ConsumerUsageTable.Fixed.Length;
                 i < PadForge.Engine.Common.ConsumerUsageTable.TotalSlots; i++)
            {
                ushort usage = PadForge.Engine.RawInputListener.GetDynamicSlotUsage(i);
                if (usage == 0) continue; // no oddball usage seen this session
                items.Add(new ConsumerButtonDisplayItem
                {
                    Index = i,
                    Name = PadForge.Engine.Common.ConsumerUsageTable.DynamicName(usage),
                });
            }
            return items;
        }

        private long _lastBatteryUiRefreshTick;
        private long _lastForegroundUiRefreshTick;

        /// <summary>Pushes the latest per-device battery readings into the
        /// Devices-page rows and every pad's device-roster items (#167).
        /// Runs on the UI timer at a 5 s cadence, matching the SDL wrapper's
        /// own battery cache interval. Offline devices read as no-battery so
        /// the indicator disappears instead of freezing.</summary>
        /// <summary>The one battery answer for a device (#187): SDL's value
        /// when the backend reports one (HIDAPI pads), else the device's own
        /// Bluetooth devnode battery property (Xbox pads, whose XInput lane is
        /// dead for Bluetooth and whose DevicePath is the synthetic
        /// "XInput#N"). Every battery writer must route through here: the
        /// Devices row seed once wrote raw SDL battery and blinked the page
        /// against the 5 s tick's overlay. The devnode property carries no
        /// charging flag; a pad on the charger enumerates wired and takes the
        /// SDL branch.</summary>
        private (int Pct, bool Charging) GetEffectiveBattery(UserDevice ud)
        {
            if (ud == null || !ud.IsOnline) return (-1, false);
            int sdlPct = ud.InputState?.BatteryPercent ?? -1;
            if (sdlPct >= 0) return (sdlPct, ud.InputState?.BatteryCharging ?? false);
            int btPct = PadForge.Common.Input.BluetoothBatteryService.TryGetPercent(
                ud.DevicePath, ud.VendorId, ud.ProdId);
            return (btPct, false);
        }

        private long _lastGuideLedApplyTick;

        /// <summary>Applies every (slot, device) Guide Button LED config
        /// (#209). DeviceDefault writes nothing, ever. Fixed pushes the
        /// configured percent. Battery maps the device's battery percent
        /// (the same GetEffectiveBattery overlay every battery consumer
        /// uses) to brightness with a floor of 10, skipping devices whose
        /// battery is unreadable so the last written level stands. Runs
        /// on the dispatcher: device connect (OnDevicesUpdated), Lighting
        /// tab edits (ActiveDeviceConfigPropertyChanged in MainWindow),
        /// and the 30 s slow lane. Both writers change-detect, so
        /// re-entry is cheap.</summary>
        private string _lastGuideLedApplySummary;

        internal void ApplyGuideLeds()
        {
            try
            {
                // The Steam home-LED hint is process-global (every 2015 SC
                // shares it), so per-device writes from two slots with
                // different configs flip-flopped the LED every pass. One
                // winner per pass instead: the configured SC on the slot
                // with the smallest displayed player number, the same
                // precedence contract shared devices use everywhere else.
                int steamPercent = -1;
                int steamWinnerNumber = int.MaxValue;
                // Positive control: an apply pass that walks a configured
                // device but reaches neither writer is distinguishable
                // from a silent writer, both lanes in the same log window.
                int configured = 0, xboxPathed = 0, steam2015 = 0, offline = 0, peer = 0;

                foreach (var padVm in _mainVm.Pads)
                {
                    if (padVm == null) continue;
                    foreach (var dev in padVm.MappedDevices)
                    {
                        if (dev == null || dev.InstanceGuid == Guid.Empty) continue;
                        if (!padVm.PerDeviceSlotConfigs.TryGetValue(dev.InstanceGuid, out var cfg)
                            || cfg == null) continue;
                        if (cfg.GuideLedMode == ViewModels.GuideLedMode.DeviceDefault) continue;
                        configured++;

                        var ud = FindUserDevice(dev.InstanceGuid);
                        if (ud == null || !ud.IsOnline) { offline++; continue; }

                        int percent;
                        if (cfg.GuideLedMode == ViewModels.GuideLedMode.Battery)
                        {
                            var (pct, _) = GetEffectiveBattery(ud);
                            percent = PadForge.Common.Input.XboxGipGuideLedWriter.BatteryToBrightnessPercent(pct);
                            if (percent < 0) continue;
                        }
                        else
                        {
                            percent = Math.Clamp(cfg.GuideLedBrightness, 0, 100);
                        }

                        // A shared peer device's Guide LED lives on the OWNER's
                        // machine. Relay the percent (#209) and take the device OUT
                        // of the LOCAL Steam single-winner competition below, or a
                        // shared 2015 Steam Controller (VID 0x28DE) would dim THIS
                        // PC's local Steam Controllers through the process-global hint.
                        if (ud.Device is PadForge.Engine.RemoteLink.RemotePeerDevice rpd)
                        {
                            peer++;
                            PadForge.Common.Input.RemoteLinkOutputRouter.ShipGuideLed(rpd.DevicePath, percent);
                            continue;
                        }

                        if (PadForge.Common.Input.XboxGipGuideLedWriter.IsXboxGipPathed(ud))
                        {
                            xboxPathed++;
                            PadForge.Common.Input.XboxGipGuideLedWriter.Instance.TrySetBrightness(ud, percent);
                        }
                        else if (PadForge.Common.Input.SteamHomeLedSetter.IsSteamController2015(ud.VendorId, ud.ProdId))
                        {
                            steam2015++;
                            int n = SettingsManager.SlotOrders.GetGlobalSlotNumber(padVm.PadIndex);
                            if (n <= 0) n = padVm.PadIndex + 1;
                            if (n < steamWinnerNumber)
                            {
                                steamWinnerNumber = n;
                                steamPercent = percent;
                            }
                        }
                    }
                }

                if (steamPercent >= 0)
                    PadForge.Common.Input.SteamHomeLedSetter.TrySet(steamPercent);

                // Positive-control summary, logged only when it CHANGES:
                // the periodic lanes re-run this every pass and identical
                // summaries were pure diag churn.
                if (configured > 0)
                {
                    string summary =
                        $"GUIDELED apply configured={configured} xboxPathed={xboxPathed} steam2015={steam2015} peer={peer} offline={offline}";
                    if (!string.Equals(summary, _lastGuideLedApplySummary, StringComparison.Ordinal))
                    {
                        _lastGuideLedApplySummary = summary;
                        PadForge.Engine.SdlDiagLog.WriteLine(summary);
                    }
                }
            }
            catch { /* LED writes are cosmetic, never block a tick */ }
        }

        private void UpdateBatteryIndicators()
        {
            // Memoized per device per tick so both loops below agree; the
            // computation itself lives in GetEffectiveBattery so EVERY battery
            // writer (this tick, the Devices row seed, the lightbar provider)
            // sees the same overlay. The row seed writing raw SDL battery was
            // exactly how the Devices page blinked while the dashboard held.
            var btOverlay = new Dictionary<Guid, (int, bool)>();
            (int Pct, bool Charging) EffectiveBattery(UserDevice ud)
            {
                if (ud == null) return (-1, false);
                if (!btOverlay.TryGetValue(ud.InstanceGuid, out var val))
                {
                    val = GetEffectiveBattery(ud);
                    btOverlay[ud.InstanceGuid] = val;
                }
                return val;
            }

            foreach (var row in _mainVm.Devices.Devices)
            {
                var ud = FindUserDevice(row.InstanceGuid);
                var (rowPct, rowCharging) = EffectiveBattery(ud);
                row.BatteryPercent = rowPct;
                row.BatteryCharging = rowCharging;
            }

            foreach (var padVm in _mainVm.Pads)
            {
                foreach (var dev in padVm.MappedDevices)
                {
                    var ud = FindUserDevice(dev.InstanceGuid);
                    var (pct, charging) = EffectiveBattery(ud);
                    dev.BatteryText = pct >= 0 ? $"{pct}%" : string.Empty;
                    // Same MDL2 bucketing as DeviceRowViewModel.BatteryGlyph.
                    if (pct < 0)
                    {
                        dev.BatteryGlyph = string.Empty;
                    }
                    else
                    {
                        int tenth = Math.Clamp((pct + 5) / 10, 0, 10);
                        int code = charging
                            ? (tenth == 10 ? 0xEA93 : tenth == 9 ? 0xE83E : 0xE85A + tenth)
                            : (tenth == 10 ? 0xE83F : 0xE850 + tenth);
                        dev.BatteryGlyph = char.ConvertFromUtf32(code);
                    }
                }
            }

            // Card device lines are composed in RefreshDashboardSlots (#175),
            // which owns slot.DeviceName; per-device BatteryText above is the
            // only battery state this tick maintains.
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
                bool isMidi2 = ud.CapType == InputDeviceType.Midi;
                bool isNfc2 = ud.CapType == InputDeviceType.Nfc;
                int[] btnIndices = ResolveButtonIndices(ud);
                devVm.RebuildRawStateCollections(axisCount, btnIndices, povCount, isKb, isMouse, isTouchpad2, isMidi2, isNfc2,
                    consumerButtons: BuildConsumerPreviewItems(ud));
                devVm.HasGyroData = ud.HasGyro;
                devVm.HasAccelData = ud.HasAccel;
                devVm.HasAccelAuxData = ud.HasAccelAux;
                devVm.HasTouchpadData = ud.HasTouchpad || isTouchpad2;
            }

            // Gyro UI lives on the Pad page Gyro tab now; no
            // calibration label / tuning sync happens here.

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
            else if (devVm.IsConsumerDevice)
            {
                // Consumer Control (issue #168): light the named chips.
                for (int i = 0; i < devVm.ConsumerButtons.Count; i++)
                {
                    var chip = devVm.ConsumerButtons[i];
                    int idx = chip.Index;
                    chip.IsPressed = idx >= 0 && idx < state.Buttons.Length && state.Buttons[idx];
                }

                // Session-dynamic usages can be assigned their slot AFTER this
                // device was selected, and re-selecting the same device does
                // not retrigger the structural rebuild, so append their chips
                // here. Slots are handed out in strictly increasing order and
                // chips are stored in slot order, so the next unprobed slot is
                // Fixed.Length + (chip count - Fixed.Length). O(1) when
                // nothing new has appeared. LOCAL devices only: the dynamic
                // table describes this machine's consumer aggregate; a Remote
                // Link device's chips (including the owner's dynamic usages)
                // arrive on the device list and refresh via the periodic push.
                if (ud.Device is not PadForge.Engine.RemoteLink.RemotePeerDevice)
                {
                    int fixedLen = PadForge.Engine.Common.ConsumerUsageTable.Fixed.Length;
                    for (int slot = fixedLen + (devVm.ConsumerButtons.Count - fixedLen);
                         slot < PadForge.Engine.Common.ConsumerUsageTable.TotalSlots; slot++)
                    {
                        ushort usage = PadForge.Engine.RawInputListener.GetDynamicSlotUsage(slot);
                        if (usage == 0) break;
                        devVm.ConsumerButtons.Add(new ConsumerButtonDisplayItem
                        {
                            Index = slot,
                            Name = PadForge.Engine.Common.ConsumerUsageTable.DynamicName(usage),
                            IsPressed = slot < state.Buttons.Length && state.Buttons[slot],
                        });
                    }
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

            // MIDI preview: hand the live state to the MidiPreviewView, which
            // polls it each render frame (issue #128). Null until the first
            // message arrives.
            if (devVm.IsMidiDevice)
                devVm.LiveMidi = state.Midi;

            // NFC tag preview (issue #150): light the tapped tag's named row.
            // The button pulse is only ~175 ms, so latch IsActive for a longer
            // visible window (NfcPreviewHoldMs) past the last tap so it doesn't
            // just flicker -- and clears itself afterward.
            if (devVm.IsNfcDevice)
            {
                long now = Environment.TickCount64;
                var btns = state.Buttons;
                for (int i = 0; i < devVm.NfcTags.Count; i++)
                {
                    var tag = devVm.NfcTags[i];
                    if (tag.Button >= 0 && tag.Button < btns.Length && btns[tag.Button])
                        tag.LastActiveTick = now;
                    tag.IsActive = tag.LastActiveTick != 0 && (now - tag.LastActiveTick) < NfcPreviewHoldMs;
                }
            }

            // Update POV hat values in-place.
            for (int i = 0; i < devVm.RawPovs.Count; i++)
                devVm.RawPovs[i].Centidegrees = state.Povs[i];

            // Update gyro/accel values. (Pad page's Gyro tab pulls its
            // own live-rate readout in the UI-tick loop above; this is
            // just the Devices-page raw-state display.)
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
            if (ud.HasAccelAux)
            {
                devVm.AccelAuxX = state.AccelAux[0];
                devVm.AccelAuxY = state.AccelAux[1];
                devVm.AccelAuxZ = state.AccelAux[2];
            }

            // Update touchpad finger positions. Click state is shown in
            // the Buttons grid at slot 16 (SDL_GAMEPAD_BUTTON_TOUCHPAD),
            // so no separate VM write here.
            if ((ud.HasTouchpad || ud.IsTouchpad)
                && state.Touchpads != null && state.Touchpads.Length > 0
                && state.Touchpads[0] != null)
            {
                // Devices preview surfaces the first touchpad's first
                // five fingers (Windows PTP max; SDL gamepad touchpads
                // typically expose 1-2 so the higher slots stay idle
                // there). Multi-pad devices (Steam Controller / Deck /
                // Triton) have their additional pads surfaced through
                // the mapping table's per-pad descriptors; preview
                // shows pad 0 for parity with the pre-v3.3 single-
                // pad model. Surfacing slots 2-4 lets the user verify
                // their PTP actually reports 3+ contacts (which the
                // gesture engine then sees) instead of seeing only two
                // dots and inferring "PTP doesn't support that here."
                var pad = state.Touchpads[0];
                if (pad.MaxFingers > 0)
                {
                    devVm.TouchpadX0 = pad.FingerX[0];
                    devVm.TouchpadY0 = pad.FingerY[0];
                    devVm.TouchpadDown0 = pad.FingerDown[0];
                }
                if (pad.MaxFingers > 1)
                {
                    devVm.TouchpadX1 = pad.FingerX[1];
                    devVm.TouchpadY1 = pad.FingerY[1];
                    devVm.TouchpadDown1 = pad.FingerDown[1];
                }
                if (pad.MaxFingers > 2)
                {
                    devVm.TouchpadX2 = pad.FingerX[2];
                    devVm.TouchpadY2 = pad.FingerY[2];
                    devVm.TouchpadDown2 = pad.FingerDown[2];
                }
                if (pad.MaxFingers > 3)
                {
                    devVm.TouchpadX3 = pad.FingerX[3];
                    devVm.TouchpadY3 = pad.FingerY[3];
                    devVm.TouchpadDown3 = pad.FingerDown[3];
                }
                if (pad.MaxFingers > 4)
                {
                    devVm.TouchpadX4 = pad.FingerX[4];
                    devVm.TouchpadY4 = pad.FingerY[4];
                    devVm.TouchpadDown4 = pad.FingerDown[4];
                }

                // Second touchpad surface (Steam Controller 2026 / Deck / original
                // Steam Controller). The preview previously stopped at pad 0; feed
                // pad 1's fingers so both surfaces render. HasSecondTouchpadData
                // gates the second preview's visibility.
                bool hasSecondPad = state.Touchpads.Length > 1 && state.Touchpads[1] != null;
                devVm.HasSecondTouchpadData = hasSecondPad;
                if (hasSecondPad)
                {
                    var pad2 = state.Touchpads[1];
                    if (pad2.MaxFingers > 0) { devVm.Pad2X0 = pad2.FingerX[0]; devVm.Pad2Y0 = pad2.FingerY[0]; devVm.Pad2Down0 = pad2.FingerDown[0]; }
                    if (pad2.MaxFingers > 1) { devVm.Pad2X1 = pad2.FingerX[1]; devVm.Pad2Y1 = pad2.FingerY[1]; devVm.Pad2Down1 = pad2.FingerDown[1]; }
                    if (pad2.MaxFingers > 2) { devVm.Pad2X2 = pad2.FingerX[2]; devVm.Pad2Y2 = pad2.FingerY[2]; devVm.Pad2Down2 = pad2.FingerDown[2]; }
                    if (pad2.MaxFingers > 3) { devVm.Pad2X3 = pad2.FingerX[3]; devVm.Pad2Y3 = pad2.FingerY[3]; devVm.Pad2Down3 = pad2.FingerDown[3]; }
                    if (pad2.MaxFingers > 4) { devVm.Pad2X4 = pad2.FingerX[4]; devVm.Pad2Y4 = pad2.FingerY[4]; devVm.Pad2Down4 = pad2.FingerDown[4]; }
                }
            }
            else
            {
                devVm.HasSecondTouchpadData = false;
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
            if (padVm == null) return;

            int padIndex = padVm.PadIndex;
            bool haveEngine = _inputManager != null
                && padIndex >= 0
                && padIndex < InputManager.MaxPads;

            // The Value column should show each row's post-combine
            // output (multi-source contributions merged via the row's
            // CombineMode), not just the primary source's raw value.
            // For every VC type we pull from the engine's combined
            // output for that type:
            //   Xbox / PlayStation → CombinedOutputStates  (Gamepad)
            //   Extended           → CombinedExtendedRawStates
            //   KbM                → CombinedKbmRawStates
            //   MIDI               → CombinedMidiRawStates
            // The legacy per-device read on the slot's selected device
            // is the final fallback for rows whose target name the
            // combined readers don't recognize.
            UserDevice ud = FindSelectedDeviceForSlot(padVm);
            var fallbackState = ud?.InputState;
            var outputType = padVm.OutputType;

            // MotionSnapshot for the slot carries the post-tuning gyro
            // (deg/s) and accel (g) in SDL's native sensor frame —
            // exactly what the virtual DualSense's HID report ships and
            // what the DSU server broadcasts. Read once outside the loop
            // so both Motion rows use the same tick's reading.
            MotionSnapshot snap = haveEngine
                ? _inputManager.MotionSnapshots[padIndex]
                : default;

            foreach (var mapping in padVm.Mappings)
            {
                string target = mapping.TargetSettingName;

                // Motion rows: three-axis live readout from the slot's
                // MotionSnapshot. Gyro in deg/s, accel in g.
                if (target == MappingSetMigrator.MotionGyroTarget)
                {
                    mapping.CurrentValueText = snap.HasMotion
                        ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "P:{0,4:F0}° Y:{1,4:F0}° R:{2,4:F0}°",
                            snap.GyroPitch, snap.GyroYaw, snap.GyroRoll)
                        : string.Empty;
                    mapping.IsInputActive = false;
                    continue;
                }
                if (target == MappingSetMigrator.MotionAccelTarget)
                {
                    mapping.CurrentValueText = snap.HasMotion
                        ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "X:{0,5:+0.00;-0.00}g Y:{1,5:+0.00;-0.00}g Z:{2,5:+0.00;-0.00}g",
                            snap.AccelX, snap.AccelY, snap.AccelZ)
                        : string.Empty;
                    mapping.IsInputActive = false;
                    continue;
                }

                int? combined = null;
                if (haveEngine)
                    combined = ReadCombinedOutputValue(padVm, padIndex, outputType, target);

                if (combined.HasValue)
                {
                    mapping.CurrentValueText = combined.Value.ToString();
                    // Rowfire (#175): discrete targets fire on any nonzero,
                    // axes need a deadband so resting sticks stay dark.
                    mapping.IsInputActive = mapping.IsTargetDiscrete
                        ? combined.Value != 0
                        : Math.Abs(combined.Value) > 1500;
                    continue;
                }

                if (string.IsNullOrEmpty(mapping.SourceDescriptor) || fallbackState == null)
                {
                    mapping.CurrentValueText = string.Empty;
                    mapping.IsInputActive = false;
                    continue;
                }
                int fallbackValue = ReadMappedValue(fallbackState, mapping.SourceDescriptor);
                mapping.CurrentValueText = fallbackValue.ToString();
                mapping.IsInputActive = mapping.IsTargetDiscrete
                    ? fallbackValue != 0
                    : Math.Abs(fallbackValue) > 1500;
            }

            // ── Pipeline heat chips (#175 item 10): recompute from this
            //    tick's rowfire flags on the same lane. The engaged shift
            //    layer comes from the same engine poll the shift-layer
            //    flyout rides (GetEngagedLayerMask). ──
            string engagedMask = "Base";
            var mappingSets = SettingsManager.SlotMappingSets;
            if (haveEngine && mappingSets != null && padIndex < mappingSets.Length)
            {
                string mask = Common.Input.InputManager.GetEngagedLayerMask(padIndex, mappingSets[padIndex]);
                if (!string.IsNullOrEmpty(mask)) engagedMask = mask;
            }
            padVm.RefreshPipelineChips(engagedMask);
        }

        /// <summary>Reads a target's current post-combine output value
        /// from the engine's per-VC-type CombinedXxxRawStates. Returns
        /// null when the target name doesn't match any known shape for
        /// the slot's OutputType — callers then fall back to the legacy
        /// per-device read path.</summary>
        private int? ReadCombinedOutputValue(PadViewModel padVm, int padIndex,
            VirtualControllerType outputType, string target)
        {
            if (string.IsNullOrEmpty(target)) return null;

            // Touchpad targets live on PlayStation slots but aren't part
            // of the Gamepad struct, so they need to be checked before
            // the OutputType switch. Step 4 merges every assigned
            // device's touchpad contribution into CombinedTouchpadStates
            // through the gated multi-source evaluator — reading from
            // there makes the Mappings preview reflect the post-combine
            // output instead of just the primary source's last raw
            // value (which is what the fallback per-device read returns
            // and is the bug the user reported).
            if (target.StartsWith("Touchpad", StringComparison.Ordinal))
            {
                var tp = _inputManager.CombinedTouchpadStates[padIndex];
                return target switch
                {
                    "TouchpadX1"       => (int)(tp.X0 * 1000),
                    "TouchpadY1"       => (int)(tp.Y0 * 1000),
                    "TouchpadX2"       => (int)(tp.X1 * 1000),
                    "TouchpadY2"       => (int)(tp.Y1 * 1000),
                    "TouchpadContact1" => tp.Down0 ? 1 : 0,
                    "TouchpadContact2" => tp.Down1 ? 1 : 0,
                    "TouchpadClick"    => tp.Click ? 1 : 0,
                    _ => null,
                };
            }

            // Standard gamepad output (Xbox / PlayStation slots).
            if (outputType == VirtualControllerType.Xbox
                || outputType == VirtualControllerType.PlayStation)
            {
                var gp = _inputManager.CombinedOutputStates[padIndex];
                return target switch
                {
                    "ButtonA"          => (gp.Buttons & Gamepad.A) != 0 ? 1 : 0,
                    "ButtonB"          => (gp.Buttons & Gamepad.B) != 0 ? 1 : 0,
                    "ButtonX"          => (gp.Buttons & Gamepad.X) != 0 ? 1 : 0,
                    "ButtonY"          => (gp.Buttons & Gamepad.Y) != 0 ? 1 : 0,
                    "LeftShoulder"     => (gp.Buttons & Gamepad.LEFT_SHOULDER) != 0 ? 1 : 0,
                    "RightShoulder"    => (gp.Buttons & Gamepad.RIGHT_SHOULDER) != 0 ? 1 : 0,
                    "ButtonBack"       => (gp.Buttons & Gamepad.BACK) != 0 ? 1 : 0,
                    "ButtonStart"      => (gp.Buttons & Gamepad.START) != 0 ? 1 : 0,
                    "ButtonGuide"      => (gp.Buttons & Gamepad.GUIDE) != 0 ? 1 : 0,
                    "ButtonShare"      => gp.Share ? 1 : 0,
                    "LeftThumbButton"  => (gp.Buttons & Gamepad.LEFT_THUMB) != 0 ? 1 : 0,
                    "RightThumbButton" => (gp.Buttons & Gamepad.RIGHT_THUMB) != 0 ? 1 : 0,
                    "DPadUp"           => (gp.Buttons & Gamepad.DPAD_UP) != 0 ? 1 : 0,
                    "DPadDown"         => (gp.Buttons & Gamepad.DPAD_DOWN) != 0 ? 1 : 0,
                    "DPadLeft"         => (gp.Buttons & Gamepad.DPAD_LEFT) != 0 ? 1 : 0,
                    "DPadRight"        => (gp.Buttons & Gamepad.DPAD_RIGHT) != 0 ? 1 : 0,
                    "LeftTrigger"      => gp.LeftTrigger,
                    "RightTrigger"     => gp.RightTrigger,
                    "LeftThumbAxisX"   => gp.ThumbLX,
                    "LeftThumbAxisY"   => gp.ThumbLY,
                    "RightThumbAxisX"  => gp.ThumbRX,
                    "RightThumbAxisY"  => gp.ThumbRY,
                    _ => null,
                };
            }

            // Extended (game controller of arbitrary shape) — Axes /
            // Buttons / POVs of customizable count.
            if (outputType == VirtualControllerType.Extended)
            {
                var ext = _inputManager.CombinedExtendedRawStates[padIndex];
                // ExtendedAxis{N} / ExtendedAxis{N}Neg
                if (target.StartsWith("ExtendedAxis", StringComparison.Ordinal))
                {
                    string rest = target.Substring("ExtendedAxis".Length);
                    if (rest.EndsWith("Neg", StringComparison.Ordinal))
                        rest = rest.Substring(0, rest.Length - 3);
                    if (int.TryParse(rest, out int axisIdx) && ext.Axes != null
                        && axisIdx >= 0 && axisIdx < ext.Axes.Length)
                        return ext.Axes[axisIdx];
                    return null;
                }
                // ExtendedBtn{N}
                if (target.StartsWith("ExtendedBtn", StringComparison.Ordinal)
                    && int.TryParse(target.Substring("ExtendedBtn".Length), out int btn))
                    return ext.IsButtonPressed(btn) ? 1 : 0;
                // ExtendedPov{N}Up/Down/Left/Right
                if (target.StartsWith("ExtendedPov", StringComparison.Ordinal))
                {
                    string rest = target.Substring("ExtendedPov".Length);
                    int dirIdx = -1;
                    string dir = "";
                    foreach (var d in new[] { "Up", "Down", "Left", "Right" })
                    {
                        if (rest.EndsWith(d, StringComparison.Ordinal))
                        { dir = d; dirIdx = rest.Length - d.Length; break; }
                    }
                    if (dirIdx > 0 && int.TryParse(rest.Substring(0, dirIdx), out int povIdx)
                        && ext.Povs != null && povIdx >= 0 && povIdx < ext.Povs.Length)
                    {
                        return PovInDirection(ext.Povs[povIdx], dir) ? 1 : 0;
                    }
                    return null;
                }
                return null;
            }

            // KbM — keys, mouse buttons, mouse axes, scroll.
            if (outputType == VirtualControllerType.KeyboardMouse)
            {
                var kbm = _inputManager.CombinedKbmRawStates[padIndex];
                if (target.StartsWith("KbmKey", StringComparison.Ordinal)
                    && byte.TryParse(target.Substring("KbmKey".Length),
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out byte vk))
                    return kbm.GetKey(vk) ? 1 : 0;
                if (target.StartsWith("KbmMBtn", StringComparison.Ordinal)
                    && int.TryParse(target.Substring("KbmMBtn".Length), out int mb))
                    return kbm.GetMouseButton(mb) ? 1 : 0;
                return target switch
                {
                    "KbmMouseX"  => kbm.MouseDeltaX,
                    "KbmMouseY"  => kbm.MouseDeltaY,
                    "KbmScroll"  => kbm.ScrollDelta,
                    "KbmScrollH" => kbm.ScrollDeltaH,
                    _ => null,
                };
            }

            // MIDI — CC values (0..127, center 64) + notes (on/off).
            if (outputType == VirtualControllerType.Midi)
            {
                var midi = _inputManager.CombinedMidiRawStates[padIndex];
                if (target.StartsWith("MidiCC", StringComparison.Ordinal))
                {
                    string rest = target.Substring("MidiCC".Length);
                    if (rest.EndsWith("Neg", StringComparison.Ordinal))
                        rest = rest.Substring(0, rest.Length - 3);
                    if (int.TryParse(rest, out int cc) && midi.CcValues != null
                        && cc >= 0 && cc < midi.CcValues.Length)
                        return midi.CcValues[cc];
                    return null;
                }
                if (target.StartsWith("MidiNote", StringComparison.Ordinal)
                    && int.TryParse(target.Substring("MidiNote".Length), out int note)
                    && midi.Notes != null && note >= 0 && note < midi.Notes.Length)
                    return midi.Notes[note] ? 1 : 0;
                return null;
            }

            return null;
        }

        /// <summary>True when an Extended POV's centidegree value is in
        /// the sector matching the named cardinal direction. Matches
        /// the engine's 4-way sector partition used in Step 3/4.</summary>
        private static bool PovInDirection(int centidegrees, string dir)
        {
            if (centidegrees < 0) return false;
            // Normalize to [0, 36000).
            int cd = centidegrees % 36000;
            // Same 90°-sector mapping the engine uses: any angle within
            // 45° of the cardinal counts. Up = [-45°, +45°] mod 360.
            return dir switch
            {
                "Up"    => cd >= 31500 || cd <  4500,
                "Right" => cd >=  4500 && cd < 13500,
                "Down"  => cd >= 13500 && cd < 22500,
                "Left"  => cd >= 22500 && cd < 31500,
                _ => false,
            };
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
            else if (s.StartsWith("I", StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1])
                     && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(s))
                s = s.Substring(1);
            else if (s.StartsWith("H", StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
                s = s.Substring(1);

            // Touchpad descriptors: "Touchpad N Finger M X/Y/Down" or "Touchpad N Click".
            if (s.StartsWith("Touchpad", StringComparison.Ordinal))
            {
                // Format: "Touchpad N Finger M X|Y|Pressure|Down", "Touchpad N Click"
                var tParts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tParts.Length < 3) return 0;
                if (!int.TryParse(tParts[1], out int padIdx)) return 0;
                if (tParts.Length == 3 && tParts[2].Equals("Click", StringComparison.Ordinal))
                    return (state.Buttons != null && state.Buttons.Length > 16 && state.Buttons[16]) ? 1 : 0;
                if (tParts.Length != 5
                    || !tParts[2].Equals("Finger", StringComparison.Ordinal)
                    || !int.TryParse(tParts[3], out int fingerIdx))
                    return 0;
                if (state.Touchpads == null || padIdx < 0 || padIdx >= state.Touchpads.Length) return 0;
                var pad = state.Touchpads[padIdx];
                if (pad == null || fingerIdx < 0 || fingerIdx >= pad.MaxFingers) return 0;
                return tParts[4] switch
                {
                    "X"        => (int)(pad.FingerX[fingerIdx] * 1000),
                    "Y"        => (int)(pad.FingerY[fingerIdx] * 1000),
                    "Pressure" => (int)(pad.FingerPressure[fingerIdx] * 1000),
                    "Down"     => pad.FingerDown[fingerIdx] ? 1 : 0,
                    _          => 0
                };
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
                    _inputManager._kbmConfigs[i] = padVm.KbmConfig;
                    _inputManager._deviceSlotConfigs[i] = padVm.DeviceConfig;
                    // Per-(slot, device) lighting configs — source of
                    // truth for the dispatcher's per-device synthesis and
                    // macro lightbar fan-out. Mirroring is a reference
                    // copy (shared dictionary instance), so config edits
                    // on the UI thread are visible to the polling thread
                    // without an extra sync step.
                    _inputManager._perDeviceSlotConfigs[i] = padVm.PerDeviceSlotConfigs;
                }

                if (SettingsManager.SlotCreated[i] && (padVm.AudioRumbleEnabled || padVm.AudioRumbleTriggersEnabled))
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
            // VID/PID override — Customize-gated like the OEM/FFB fields. 0 means
            // "use the active profile's value" (the build path falls back to it).
            _inputManager.SlotExtendedVendorId[slotIndex] = customize ? cfg.VendorId : 0;
            _inputManager.SlotExtendedProductId[slotIndex] = customize ? cfg.ProductId : 0;
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

            // Boundary calibration (#174): serialized maps, culture-invariant by
            // construction (space-separated integers), stored verbatim.
            ps.LeftThumbBoundaryMap = padVm.LeftThumbBoundaryMap ?? "";
            ps.RightThumbBoundaryMap = padVm.RightThumbBoundaryMap ?? "";

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
            ps.RotationRange = padVm.WheelRotationRange.ToString();
            ps.AutoCenterStrength = padVm.WheelAutoCenter.ToString();
            ps.WheelRpmLeds = padVm.WheelRpmLeds ? "1" : "0";
            ps.LeftMotorStrength = padVm.LeftMotorStrength.ToString();
            ps.RightMotorStrength = padVm.RightMotorStrength.ToString();
            ps.ForceSwapMotor = padVm.SwapMotors ? "1" : "0";

            // Impulse triggers (Xbox One+).
            ps.ImpulseOverallGain = padVm.ImpulseOverallGain.ToString();
            ps.ImpulseLeftStrength = padVm.ImpulseLeftStrength.ToString();
            ps.ImpulseRightStrength = padVm.ImpulseRightStrength.ToString();
            ps.ImpulseSwapTriggers = padVm.ImpulseSwapTriggers ? "1" : "0";
            ps.ConstantTriggerForceEnabled = padVm.ConstantTriggerForceEnabled ? "1" : "0";
            ps.ConstantTriggerForceLeft = padVm.ConstantTriggerForceLeft.ToString("F4", ic);
            ps.ConstantTriggerForceRight = padVm.ConstantTriggerForceRight.ToString("F4", ic);
            ps.AudioRumbleTriggersEnabled = padVm.AudioRumbleTriggersEnabled ? "1" : "0";
            ps.AudioRumbleTriggersSensitivity = padVm.AudioRumbleTriggersSensitivity.ToString("F1", ic);
            ps.AudioRumbleTriggersCutoffHz = padVm.AudioRumbleTriggersCutoffHz.ToString("F0", ic);
            ps.AudioRumbleLeftTrigger = padVm.AudioRumbleLeftTrigger.ToString();
            ps.AudioRumbleRightTrigger = padVm.AudioRumbleRightTrigger.ToString();

            // Audio bass rumble.
            ps.AudioRumbleEnabled = padVm.AudioRumbleEnabled ? "1" : "0";
            ps.AudioRumbleSensitivity = padVm.AudioRumbleSensitivity.ToString("F1", ic);
            ps.AudioRumbleCutoffHz = padVm.AudioRumbleCutoffHz.ToString("F0", ic);
            ps.AudioRumbleLeftMotor = padVm.AudioRumbleLeftMotor.ToString();
            ps.AudioRumbleRightMotor = padVm.AudioRumbleRightMotor.ToString();

            // Gyro tuning (per-(device, slot)) — sliders push live
            // changes to the polling-thread read site via the
            // GyroTuningProvider's PadSetting lookup. Every gyro field
            // listed here also needs a load mirror in
            // LoadPadSettingToViewModel and a serialization mirror in
            // SettingsService.UpdatePadSettingsFromViewModels for the
            // four-way sync (VM ↔ PadSetting ↔ XML ↔ engine) to be
            // race-free.
            ps.GyroSensitivityH = padVm.GyroSensitivityH.ToString("F2", ic);
            ps.GyroSensitivityV = padVm.GyroSensitivityV.ToString("F2", ic);
            ps.GyroDeadZoneDegPerSec = padVm.GyroDeadZoneDegPerSec.ToString("F1", ic);
            ps.GyroSmoothingAlpha = padVm.GyroSmoothingAlpha.ToString("F2", ic);
            ps.GyroAcceleration = padVm.GyroAcceleration.ToString("F2", ic);
            ps.GyroOutputCurve = padVm.GyroOutputCurve ?? "Linear";
            ps.GyroSensitivityUnits = padVm.GyroSensitivityUnits ?? "Multiplier";
            ps.GyroEasyAimStickThreshold = padVm.GyroEasyAimStickThreshold.ToString("F0", ic);
            ps.GyroEngageStickSide = string.IsNullOrEmpty(padVm.GyroEngageStickSide) ? "Right" : padVm.GyroEngageStickSide;
            ps.GyroEngageStickDirection = string.IsNullOrEmpty(padVm.GyroEngageStickDirection) ? "Full" : padVm.GyroEngageStickDirection;
            ps.IrSensorBarPos = padVm.IrSensorBarPos.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ps.IrSensorBarComp = (padVm.IrSensorBarCompPercent / 100.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            ps.IrSmoothing = (padVm.IrSmoothingPercent / 100.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            ps.PointerMode = string.IsNullOrEmpty(padVm.PointerMode) ? "Mouse" : padVm.PointerMode;
            ps.PointerFpsSpeed = padVm.PointerFpsSpeed.ToString(ic);
            // JoyShockMapper-canon extensions.
            ps.GyroSpace = padVm.GyroSpace ?? "Local";
            ps.GyroPlayerSpaceYawRelaxFactor = padVm.GyroPlayerSpaceYawRelaxFactor.ToString("F2", ic);
            ps.GyroWorldSpaceSideReductionThreshold = padVm.GyroWorldSpaceSideReductionThreshold.ToString("F3", ic);
            ps.GyroTighteningThresholdDegPerSec = padVm.GyroTighteningThresholdDegPerSec.ToString("F1", ic);
            ps.GyroSmoothingThresholdDegPerSec = padVm.GyroSmoothingThresholdDegPerSec.ToString("F1", ic);
            ps.GyroSmoothingWindowMs = padVm.GyroSmoothingWindowMs.ToString("F0", ic);
            ps.GyroRealWorldCalibration = padVm.GyroRealWorldCalibration.ToString("F2", ic);
            ps.GyroAimEngageButton = padVm.GyroAimEngageButton ?? "";
            ps.GyroAimEngageDeviceGuid = padVm.GyroAimEngageDeviceGuid ?? "";
            ps.GyroAimEngageMode = string.IsNullOrEmpty(padVm.GyroAimEngageMode) ? "Hold" : padVm.GyroAimEngageMode;

            // Trigger rumble routing (#102), per trigger.
            ps.LeftTriggerRouteSource = string.IsNullOrEmpty(padVm.LeftTriggerRouteSource) ? "None" : padVm.LeftTriggerRouteSource;
            ps.RightTriggerRouteSource = string.IsNullOrEmpty(padVm.RightTriggerRouteSource) ? "None" : padVm.RightTriggerRouteSource;
            ps.LeftTriggerRouteMode = string.IsNullOrEmpty(padVm.LeftTriggerRouteMode) ? "Duplicate" : padVm.LeftTriggerRouteMode;
            ps.RightTriggerRouteMode = string.IsNullOrEmpty(padVm.RightTriggerRouteMode) ? "Duplicate" : padVm.RightTriggerRouteMode;
            ps.LeftTriggerRouteScale = padVm.LeftTriggerRouteScale.ToString();
            ps.RightTriggerRouteScale = padVm.RightTriggerRouteScale.ToString();
            ps.LeftTriggerRouteActivator = padVm.LeftTriggerRouteActivator ?? "";
            ps.RightTriggerRouteActivator = padVm.RightTriggerRouteActivator ?? "";
            ps.LeftTriggerRouteActivatorDeviceGuid = padVm.LeftTriggerRouteActivatorDeviceGuid ?? "";
            ps.RightTriggerRouteActivatorDeviceGuid = padVm.RightTriggerRouteActivatorDeviceGuid ?? "";
            ps.LeftTriggerRouteActivatorMode = string.IsNullOrEmpty(padVm.LeftTriggerRouteActivatorMode) ? "Hold" : padVm.LeftTriggerRouteActivatorMode;
            ps.RightTriggerRouteActivatorMode = string.IsNullOrEmpty(padVm.RightTriggerRouteActivatorMode) ? "Hold" : padVm.RightTriggerRouteActivatorMode;

            // Motion Lean steering tunables (extended-mapping KVs), mirroring
            // SettingsService.UpdatePadSettingsFromViewModels. Absent here,
            // a device-dropdown switch clobbered them with the previous
            // device's values (audit lens 1m, F2).
            ps.SetExtendedMapping("MotionSteerInner", padVm.MotionSteerInnerDz.ToString(ic));
            ps.SetExtendedMapping("MotionSteerOuter", padVm.MotionSteerOuterDz.ToString(ic));
            ps.SetExtendedMapping("MotionSteerOrient", padVm.MotionSteerOrient);

            // Sensitivity curves. The load mirror at LoadPadSettingToViewModel
            // reads all six; the save side must cover the SAME fields or a
            // curve edit is lost when the device dropdown switches inside the
            // autosave debounce window (audit lens 1m, F3).
            ps.LeftThumbSensitivityCurveX = padVm.LeftSensitivityCurveX;
            ps.LeftThumbSensitivityCurveY = padVm.LeftSensitivityCurveY;
            ps.RightThumbSensitivityCurveX = padVm.RightSensitivityCurveX;
            ps.RightThumbSensitivityCurveY = padVm.RightSensitivityCurveY;
            ps.LeftTriggerSensitivityCurve = padVm.LeftTriggerSensitivityCurve;
            ps.RightTriggerSensitivityCurve = padVm.RightTriggerSensitivityCurve;

            ps.GyroInvertPitch = padVm.GyroInvertPitch ? "1" : "0";
            ps.GyroInvertYawRoll = padVm.GyroInvertYawRoll ? "1" : "0";
            ps.GyroApplyTuningToPassthrough = padVm.GyroApplyTuningToPassthrough ? "1" : "0";

            // Constant force (per-device override).
            ps.ConstantForceEnabled = padVm.ConstantForceEnabled ? "1" : "0";
            ps.ConstantForceX = padVm.ConstantForceX.ToString("F4", ic);
            ps.ConstantForceY = padVm.ConstantForceY.ToString("F4", ic);

            // Steering at-lock feedback (#94) — per assigned device (VM-prop pattern, like
            // wheel/gyro): safe in every sync because these VM fields survive a
            // RebuildStickConfigs (unlike the StickConfigs steering below).
            ps.SteeringLockRumbleEnabled = padVm.SteeringLockRumbleEnabled ? "1" : "0";
            ps.SteeringLockTriggerVibEnabled = padVm.SteeringLockTriggerVibEnabled ? "1" : "0";
            ps.SteeringLockLightbarEnabled = padVm.SteeringLockLightbarEnabled ? "1" : "0";
            ps.SteeringLockATResistanceEnabled = padVm.SteeringLockATResistanceEnabled ? "1" : "0";
            ps.SteeringLockPulseMs = ((int)padVm.SteeringLockPulseMs).ToString(ic);
            ps.SteeringLockLightbarColor = padVm.SteeringLockLightbarColor ?? "#FF0000";
            ps.SteeringLockLightbarColorSource = padVm.SteeringLockLightbarColorSource.ToString();
            ps.SteeringLockLightbarPaletteCsv = padVm.SteeringLockLightbarPaletteCsv ?? "";
            ps.SteeringLockLightbarHoldMs = ((int)padVm.SteeringLockLightbarHoldMs).ToString(ic);
            ps.SteeringLockLightbarFadeMs = ((int)padVm.SteeringLockLightbarFadeMs).ToString(ic);

            // Per-stick steering mode + tunables (#94) — per assigned device. ONLY on a full
            // sync (syncMappings: device-switch flush + explicit saves), NEVER the 30Hz
            // path: a RebuildStickConfigs momentarily resets StickConfigs steering to
            // defaults, and a 30Hz save would persist those defaults over the device's
            // saved steering (the exact data loss that was reverted).
            if (syncMappings)
            {
                foreach (var stick in padVm.StickConfigs)
                {
                    int g = stick.Index;
                    if (g < 0) continue;
                    ps.SetExtendedMapping($"Stick{g}SteerKind", stick.SteeringKind);
                    ps.SetExtendedMapping($"Stick{g}SteerWindRange", stick.WindRangeDeg.ToString(ic));
                    ps.SetExtendedMapping($"Stick{g}SteerWindPower", stick.WindPower.ToString(ic));
                    ps.SetExtendedMapping($"Stick{g}SteerWindUnwind", stick.WindUnwindRate.ToString(ic));
                    ps.SetExtendedMapping($"Stick{g}SteerAngleInner", stick.AngleInnerDz.ToString(ic));
                    ps.SetExtendedMapping($"Stick{g}SteerAngleOuter", stick.AngleOuterDz.ToString(ic));
                }

                // Extended custom stick/trigger tuning for indices 2+, mirroring
                // SettingsService.UpdatePadSettingsFromViewModels field-for-field.
                // Same syncMappings guard as the steering block above and for the
                // same reason: a RebuildStickConfigs momentarily resets the config
                // items to defaults, and a 30 Hz save would persist those defaults
                // over the device's saved tuning.
                foreach (var stick in padVm.StickConfigs)
                {
                    if (stick.Index < 2) continue;
                    int g = stick.Index;
                    ps.SetExtendedMapping($"ExtendedStick{g}DzShape", ((int)stick.DeadZoneShape).ToString());
                    ps.SetExtendedMapping($"ExtendedStick{g}DzX", stick.DeadZoneX.ToString(ic));
                    ps.SetExtendedMapping($"ExtendedStick{g}DzY", stick.DeadZoneY.ToString(ic));
                    ps.SetExtendedMapping($"ExtendedStick{g}AdzX", stick.AntiDeadZoneX.ToString(ic));
                    ps.SetExtendedMapping($"ExtendedStick{g}AdzY", stick.AntiDeadZoneY.ToString(ic));
                    ps.SetExtendedMapping($"ExtendedStick{g}Linear", stick.Linear.ToString(ic));
                    ps.SetExtendedMapping($"ExtendedStick{g}CurveX", stick.SensitivityCurveX);
                    ps.SetExtendedMapping($"ExtendedStick{g}CurveY", stick.SensitivityCurveY);
                    ps.SetExtendedMapping($"ExtendedStick{g}CofX", stick.CenterOffsetX.ToString(ic));
                    ps.SetExtendedMapping($"ExtendedStick{g}CofY", stick.CenterOffsetY.ToString(ic));
                    ps.SetExtendedMapping($"ExtendedStick{g}MrX", stick.MaxRangeX.ToString(ic));
                    ps.SetExtendedMapping($"ExtendedStick{g}MrY", stick.MaxRangeY.ToString(ic));
                    ps.SetExtendedMapping($"ExtendedStick{g}MrXN", stick.MaxRangeXNeg.ToString(ic));
                    ps.SetExtendedMapping($"ExtendedStick{g}MrYN", stick.MaxRangeYNeg.ToString(ic));
                }
                foreach (var trig in padVm.TriggerConfigs)
                {
                    if (trig.Index < 2) continue;
                    int g = trig.Index;
                    ps.SetExtendedMapping($"ExtendedTrigger{g}Dz", trig.DeadZone.ToString(ic));
                    ps.SetExtendedMapping($"ExtendedTrigger{g}Adz", trig.AntiDeadZone.ToString(ic));
                    ps.SetExtendedMapping($"ExtendedTrigger{g}Mr", trig.MaxRange.ToString(ic));
                    ps.SetExtendedMapping($"ExtendedTrigger{g}Curve", trig.SensitivityCurve);
                }
            }

            // Mapping descriptors: clear + rewrite only when explicitly requested.
            // The 30Hz SyncViewModelToPadSettings path passes syncMappings=false
            // because ClearMappingDescriptors() creates a race window — the polling
            // thread can read the PadSetting between the clear and the rewrite,
            // seeing empty mapping strings → zero Gamepad output.
            // Mappings are only synced on explicit save, preset change, or device switch.
            //
            // Phase 2C — issue #61: a mapping descriptor authored via the
            // unified-view picker can target a DIFFERENT device than the
            // slot's currently-selected one (the user can pick from any
            // device's grouped section). Writing all descriptors
            // unconditionally to the selected device's PadSetting bled
            // gamepad-class descriptors (Axis 0, Button N) into a
            // keyboard's fields — and once the gamepad was unassigned,
            // those stale fields became the row's "primary" via the
            // legacy fallback, sticking the joystick at -1. Now we
            // route each descriptor to the OWNING device's PadSetting
            // and explicitly clear the same target on every OTHER
            // device in the slot so any historical bleed heals over
            // saves.
            // GUARD: only run the destructive mapping clear+rewrite when the
            // Mappings ViewModel actually mirrors the slot's current MappingSet.
            // RefreshMappingsCore sets MappingsViewLoaded; a device assignment
            // clears it (MainWindow's DeviceAssignmentChanged handler) for the
            // window between auto-mapping the new device and reloading the
            // ViewModel. During that window OnSelectedDeviceChanged can fire this
            // save with a STALE (typically empty) padVm.Mappings — clearing every
            // slot device's descriptors and rewriting from it would erase the
            // freshly auto-mapped pad (the trace caught a DualSense dropping from
            // 21 descriptors to 0). The MappingSet is authoritative and already
            // holds the auto-map, so skipping the push loses nothing; the next
            // save, after RefreshMappingsToViewModel, persists the mappings.
            // Per-device tuning (saved above this block) is unaffected.
            if (syncMappings && padVm.MappingsViewLoaded)
            {
                // Snapshot every assigned device for this slot so the
                // bleed-cleanup pass can iterate without re-locking.
                var slotDevices = new System.Collections.Generic.List<(Guid g, PadSetting devPs)>();
                lock (SettingsManager.UserSettings.SyncRoot)
                {
                    foreach (var devUs in SettingsManager.UserSettings.Items)
                    {
                        if (devUs == null || devUs.MapTo != padVm.PadIndex) continue;
                        var devPs = devUs.GetPadSetting();
                        if (devPs == null) continue;
                        slotDevices.Add((devUs.InstanceGuid, devPs));
                    }
                }

                // Clear every assigned device's mapping fields. We'll
                // rewrite below on the owning device only; everyone
                // else stays cleared.
                foreach (var (_, devPs) in slotDevices)
                    devPs.ClearMappingDescriptors();

                foreach (var mapping in padVm.Mappings)
                {
                    string target = mapping.TargetSettingName;

                    // Resolve the owning device's PadSetting from the
                    // row's PrimarySourceDeviceGuid. Falls back to the
                    // passed instanceGuid (selected device) when the
                    // row has no recorded source device, which keeps
                    // the legacy single-device flow working.
                    PadSetting owningPs = ps;
                    if (!string.IsNullOrEmpty(mapping.PrimarySourceDeviceGuid)
                        && Guid.TryParse(mapping.PrimarySourceDeviceGuid, out var owningGuid))
                    {
                        foreach (var (g, devPs) in slotDevices)
                        {
                            if (g == owningGuid) { owningPs = devPs; break; }
                        }
                    }

                    if (target.StartsWith("Extended", StringComparison.Ordinal))
                    {
                        owningPs.SetExtendedMapping(target, mapping.SourceDescriptor ?? string.Empty);
                        if (mapping.NegSettingName != null)
                            owningPs.SetExtendedMapping(mapping.NegSettingName, mapping.NegSourceDescriptor ?? string.Empty);
                    }
                    else if (target.StartsWith("Midi", StringComparison.Ordinal))
                    {
                        owningPs.SetMidiMapping(target, mapping.SourceDescriptor ?? string.Empty);
                        if (mapping.NegSettingName != null)
                            owningPs.SetMidiMapping(mapping.NegSettingName, mapping.NegSourceDescriptor ?? string.Empty);
                    }
                    else if (target.StartsWith("Kbm", StringComparison.Ordinal))
                    {
                        owningPs.SetKbmMapping(target, mapping.SourceDescriptor ?? string.Empty);
                        if (mapping.NegSettingName != null)
                            owningPs.SetKbmMapping(mapping.NegSettingName, mapping.NegSourceDescriptor ?? string.Empty);
                    }
                    else
                    {
                        var prop = typeof(PadSetting).GetProperty(target);
                        if (prop != null && prop.PropertyType == typeof(string) && prop.CanWrite)
                            prop.SetValue(owningPs, mapping.SourceDescriptor ?? string.Empty);

                        if (mapping.NegSettingName != null)
                        {
                            var negProp = typeof(PadSetting).GetProperty(mapping.NegSettingName);
                            if (negProp != null && negProp.PropertyType == typeof(string) && negProp.CanWrite)
                                negProp.SetValue(owningPs, mapping.NegSourceDescriptor ?? string.Empty);
                        }
                    }

                    // Save per-mapping deadzone on the owning device.
                    if (mapping.MappingDeadZone > 0)
                        owningPs.SetMappingDeadZone(target, mapping.MappingDeadZone.ToString());
                    else
                        owningPs.SetMappingDeadZone(target, "");

                    // Save per-mapping Bidirectional flag.
                    owningPs.SetMappingBidirectional(target, mapping.IsBidirectional ? "1" : "");
                }
            }
        }

        /// <summary>
        /// Refreshes only the per-VC mappings on the PadViewModel from
        /// the slot's MappingSet. Safe to call when no device is
        /// selected (e.g. after unassigning the only / last device on
        /// a slot) — mappings are per-VC so they're authoritative
        /// regardless of which physical device the dropdown is on.
        /// Use this on device-assignment changes to keep the
        /// Mappings tab in sync without forcing a per-device-tuning
        /// reload.
        /// </summary>
        internal static void RefreshMappingsToViewModel(PadViewModel padVm)
        {
            if (padVm == null) return;
            // Pass Guid.Empty so the per-device tuning fields short-
            // circuit but the mapping pass still runs. Resolves the
            // "Mappings tab doesn't refresh after unassign" bug.
            LoadPadSettingToViewModel(padVm, Guid.Empty);
        }

        /// <summary>
        /// Loads a specific device's PadSetting into the PadViewModel.
        /// </summary>
        internal static void LoadPadSettingToViewModel(PadViewModel padVm, Guid instanceGuid)
        {
            // Per-device tuning fields (deadzones, FFB, sensitivity,
            // etc.) only make sense in the context of a real device.
            // The mapping pass below runs regardless so the Mappings
            // tab stays current even after the slot's only device
            // gets unassigned.
            var us = instanceGuid == Guid.Empty
                ? null
                : SettingsManager.FindSettingByInstanceGuidAndSlot(instanceGuid, padVm.PadIndex);
            var ps = us?.GetPadSetting();
            if (ps == null)
            {
                RefreshMappingsCore(padVm);
                return;
            }

            LoadPadSettingIntoViewModel(padVm, ps);
        }

        /// <summary>Applies every writer-mirrored tuning field from
        /// <paramref name="ps"/> into <paramref name="padVm"/>. This is
        /// the load half of the four-way VM ↔ PadSetting ↔ XML sync, so
        /// its field set stays complete by the same invariant that keeps
        /// normal persistence working. Feeding a fresh
        /// <c>new PadSetting()</c> therefore yields the canonical default
        /// for the WHOLE per-pad tuning surface, which is exactly what
        /// <see cref="SettingsService.ResetToDefaults"/> leans on for
        /// closure instead of maintaining a parallel hand-list of fields
        /// (the hand-list rotted once already: gyro, steering, wheel,
        /// trigger routing, and touchpad gestures all survived reset).</summary>
        internal static void LoadPadSettingIntoViewModel(PadViewModel padVm, PadSetting ps)
        {
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

            // Motion Lean steering tunables, mirroring the startup load in
            // SettingsService.LoadPadSettings. Absent here, switching the
            // assigned-device dropdown kept the previous device's values in
            // the VM (audit lens 1m, F2).
            padVm.MotionSteerInnerDz = TryParseDouble(ps.GetExtendedMapping("MotionSteerInner"), 15);
            padVm.MotionSteerOuterDz = TryParseDouble(ps.GetExtendedMapping("MotionSteerOuter"), 135);
            padVm.SetMotionSteerOrient(ps.GetExtendedMapping("MotionSteerOrient"));

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

            // Boundary calibration (#174). SyncAllConfigItemsFromVm pushes these
            // into the StickConfigItems (radar + button state) after load.
            padVm.LeftThumbBoundaryMap = ps.LeftThumbBoundaryMap ?? "";
            padVm.RightThumbBoundaryMap = ps.RightThumbBoundaryMap ?? "";

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
            padVm.WheelRotationRange = TryParseInt(ps.RotationRange, 900);
            padVm.WheelAutoCenter = TryParseInt(ps.AutoCenterStrength, 0);
            padVm.WheelRpmLeds = ps.WheelRpmLeds == "1";
            padVm.LeftMotorStrength = TryParseInt(ps.LeftMotorStrength, 100);
            padVm.RightMotorStrength = TryParseInt(ps.RightMotorStrength, 100);
            padVm.SwapMotors = ps.ForceSwapMotor == "1" ||
                (ps.ForceSwapMotor ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);

            // Impulse triggers (Xbox One+).
            padVm.ImpulseOverallGain = TryParseInt(ps.ImpulseOverallGain, 100);
            padVm.ImpulseLeftStrength = TryParseInt(ps.ImpulseLeftStrength, 100);
            padVm.ImpulseRightStrength = TryParseInt(ps.ImpulseRightStrength, 100);
            padVm.ImpulseSwapTriggers = ps.ImpulseSwapTriggers == "1" ||
                (ps.ImpulseSwapTriggers ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);
            padVm.ConstantTriggerForceEnabled = ps.ConstantTriggerForceEnabled == "1";
            padVm.ConstantTriggerForceLeft = TryParseDouble(ps.ConstantTriggerForceLeft, 0.0);
            padVm.ConstantTriggerForceRight = TryParseDouble(ps.ConstantTriggerForceRight, 0.0);
            padVm.AudioRumbleTriggersEnabled = ps.AudioRumbleTriggersEnabled == "1";
            padVm.AudioRumbleTriggersSensitivity = TryParseDouble(ps.AudioRumbleTriggersSensitivity, 4.0);
            padVm.AudioRumbleTriggersCutoffHz = TryParseDouble(ps.AudioRumbleTriggersCutoffHz, 80.0);
            padVm.AudioRumbleLeftTrigger = TryParseInt(ps.AudioRumbleLeftTrigger, 100);
            padVm.AudioRumbleRightTrigger = TryParseInt(ps.AudioRumbleRightTrigger, 100);

            // Audio bass rumble.
            padVm.AudioRumbleEnabled = ps.AudioRumbleEnabled == "1";
            padVm.AudioRumbleSensitivity = TryParseDouble(ps.AudioRumbleSensitivity, 4.0);
            padVm.AudioRumbleCutoffHz = TryParseDouble(ps.AudioRumbleCutoffHz, 80.0);
            padVm.AudioRumbleLeftMotor = TryParseInt(ps.AudioRumbleLeftMotor, 100);
            padVm.AudioRumbleRightMotor = TryParseInt(ps.AudioRumbleRightMotor, 100);

            // Gyro tuning (per-(device, slot)). Must mirror the field
            // set written by SaveViewModelToPadSetting above — every
            // sync direction in the VM ↔ PadSetting ↔ XML chain has to
            // cover the SAME fields or the missing ones get clobbered
            // when the device dropdown is switched.
            padVm.GyroSensitivityH = TryParseDouble(ps.GyroSensitivityH, 1.0);
            padVm.GyroSensitivityV = TryParseDouble(ps.GyroSensitivityV, 1.0);
            padVm.GyroDeadZoneDegPerSec = TryParseDouble(ps.GyroDeadZoneDegPerSec, 3.0);
            padVm.GyroSmoothingAlpha = TryParseDouble(ps.GyroSmoothingAlpha, 0);
            padVm.GyroAcceleration = TryParseDouble(ps.GyroAcceleration, 0);
            padVm.GyroOutputCurve = string.IsNullOrEmpty(ps.GyroOutputCurve) ? "Linear" : ps.GyroOutputCurve;
            padVm.GyroSensitivityUnits = string.IsNullOrEmpty(ps.GyroSensitivityUnits) ? "Multiplier" : ps.GyroSensitivityUnits;
            padVm.GyroEasyAimStickThreshold = TryParseDouble(ps.GyroEasyAimStickThreshold, 0);
            padVm.GyroEngageStickSide = string.IsNullOrEmpty(ps.GyroEngageStickSide) ? "Right" : ps.GyroEngageStickSide;
            padVm.GyroEngageStickDirection = string.IsNullOrEmpty(ps.GyroEngageStickDirection) ? "Full" : ps.GyroEngageStickDirection;
            padVm.IrSensorBarPos = (int)TryParseFloatPs(ps.IrSensorBarPos, 0f);
            padVm.IrSensorBarCompPercent = (int)Math.Round(TryParseFloatPs(ps.IrSensorBarComp, 0f) * 100f);
            padVm.IrSmoothingPercent = (int)Math.Round(TryParseFloatPs(ps.IrSmoothing, 0f) * 100f);
            padVm.PointerMode = string.IsNullOrEmpty(ps.PointerMode) ? "Mouse" : ps.PointerMode;
            padVm.PointerFpsSpeed = (int)TryParseFloatPs(ps.PointerFpsSpeed, 35f);
            // JoyShockMapper-canon extensions.
            padVm.GyroSpace = string.IsNullOrEmpty(ps.GyroSpace) ? "Local" : ps.GyroSpace;
            padVm.GyroPlayerSpaceYawRelaxFactor = TryParseDouble(ps.GyroPlayerSpaceYawRelaxFactor, 1.41);
            padVm.GyroWorldSpaceSideReductionThreshold = TryParseDouble(ps.GyroWorldSpaceSideReductionThreshold, 0.125);
            padVm.GyroTighteningThresholdDegPerSec = TryParseDouble(ps.GyroTighteningThresholdDegPerSec, 3.0);
            padVm.GyroSmoothingThresholdDegPerSec = TryParseDouble(ps.GyroSmoothingThresholdDegPerSec, 8.0);
            padVm.GyroSmoothingWindowMs = TryParseDouble(ps.GyroSmoothingWindowMs, 50);
            padVm.GyroRealWorldCalibration = TryParseDouble(ps.GyroRealWorldCalibration, 0);
            padVm.GyroAimEngageButton = ps.GyroAimEngageButton ?? "";
            padVm.GyroAimEngageDeviceGuid = ps.GyroAimEngageDeviceGuid ?? "";
            padVm.GyroAimEngageMode = string.IsNullOrEmpty(ps.GyroAimEngageMode) ? "Hold" : ps.GyroAimEngageMode;

            // Trigger rumble routing (#102), per trigger.
            padVm.LeftTriggerRouteSource = string.IsNullOrEmpty(ps.LeftTriggerRouteSource) ? "None" : ps.LeftTriggerRouteSource;
            padVm.RightTriggerRouteSource = string.IsNullOrEmpty(ps.RightTriggerRouteSource) ? "None" : ps.RightTriggerRouteSource;
            padVm.LeftTriggerRouteMode = string.IsNullOrEmpty(ps.LeftTriggerRouteMode) ? "Duplicate" : ps.LeftTriggerRouteMode;
            padVm.RightTriggerRouteMode = string.IsNullOrEmpty(ps.RightTriggerRouteMode) ? "Duplicate" : ps.RightTriggerRouteMode;
            padVm.LeftTriggerRouteScale = TryParseInt(ps.LeftTriggerRouteScale, 100);
            padVm.RightTriggerRouteScale = TryParseInt(ps.RightTriggerRouteScale, 100);
            padVm.LeftTriggerRouteActivator = ps.LeftTriggerRouteActivator ?? "";
            padVm.RightTriggerRouteActivator = ps.RightTriggerRouteActivator ?? "";
            padVm.LeftTriggerRouteActivatorDeviceGuid = ps.LeftTriggerRouteActivatorDeviceGuid ?? "";
            padVm.RightTriggerRouteActivatorDeviceGuid = ps.RightTriggerRouteActivatorDeviceGuid ?? "";
            padVm.LeftTriggerRouteActivatorMode = string.IsNullOrEmpty(ps.LeftTriggerRouteActivatorMode) ? "Hold" : ps.LeftTriggerRouteActivatorMode;
            padVm.RightTriggerRouteActivatorMode = string.IsNullOrEmpty(ps.RightTriggerRouteActivatorMode) ? "Hold" : ps.RightTriggerRouteActivatorMode;
            padVm.GyroInvertPitch = ps.GyroInvertPitch == "1";
            padVm.GyroInvertYawRoll = ps.GyroInvertYawRoll == "1";
            padVm.GyroApplyTuningToPassthrough = ps.GyroApplyTuningToPassthrough == "1";

            // Constant force.
            padVm.ConstantForceEnabled = ps.ConstantForceEnabled == "1";
            padVm.ConstantForceX = TryParseDouble(ps.ConstantForceX, 0.0);
            padVm.ConstantForceY = TryParseDouble(ps.ConstantForceY, 0.0);

            // Steering at-lock feedback (#94) — per assigned device (VM-prop pattern, like
            // wheel/gyro): reload the selected device's values on dropdown swap.
            padVm.SteeringLockRumbleEnabled = ps.SteeringLockRumbleEnabled == "1";
            padVm.SteeringLockTriggerVibEnabled = ps.SteeringLockTriggerVibEnabled == "1";
            padVm.SteeringLockLightbarEnabled = ps.SteeringLockLightbarEnabled == "1";
            padVm.SteeringLockATResistanceEnabled = ps.SteeringLockATResistanceEnabled == "1";
            padVm.SteeringLockPulseMs = TryParseDouble(ps.SteeringLockPulseMs, 80);
            padVm.SteeringLockLightbarColor = string.IsNullOrWhiteSpace(ps.SteeringLockLightbarColor) ? "#FF0000" : ps.SteeringLockLightbarColor;
            padVm.SteeringLockLightbarColorSource =
                Enum.TryParse<ViewModels.MacroLightbarColorSource>(ps.SteeringLockLightbarColorSource, out var slcs)
                    ? slcs : ViewModels.MacroLightbarColorSource.Fixed;
            padVm.SteeringLockLightbarPaletteCsv = ps.SteeringLockLightbarPaletteCsv ?? "";
            padVm.SteeringLockLightbarHoldMs = TryParseDouble(ps.SteeringLockLightbarHoldMs, 80);
            padVm.SteeringLockLightbarFadeMs = TryParseDouble(ps.SteeringLockLightbarFadeMs, 250);

            // Touchpad-gestures tab — per-(device, pad) settings live
            // under PadSetting.TouchpadSettings as a typed sub-tree
            // (TouchpadSettingsEntry[]). Reading them into the VM
            // requires resolving the active pad index; defer that to
            // the VM-side loader, which reads the active device and
            // selected touchpad index off itself.
            padVm.LoadTouchpadGestureSettingsForActiveDevice();

            // Mouse-gestures tab (issue #200): same per-(slot, device)
            // typed sub-tree contract as the touchpad settings above, same
            // VM-side loader so Reset to Defaults resets it by construction.
            padVm.LoadMouseGestureSettingsForActiveDevice();

            // Sync dynamic stick/trigger config items.
            padVm.SyncAllConfigItemsFromVm();

            // Extended custom stick/trigger tuning for indices 2+ lives in the
            // extended-mapping dictionary, per device. Mirrors the startup load
            // in SettingsService.LoadPadSettings field-for-field. Absent here,
            // a device-dropdown switch kept the previous device's indices-2+
            // tuning in the VM and the next save wrote it into the new
            // device's PadSetting (same clobber family as the Motion Lean /
            // curve mirrors above, audit lens 1m).
            foreach (var stick in padVm.StickConfigs)
            {
                if (stick.Index < 2) continue;
                int g = stick.Index;
                stick.DeadZoneShape = Common.Input.InputManager.ParseDeadZoneShape(ps.GetExtendedMapping($"ExtendedStick{g}DzShape"));
                stick.DeadZoneX = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}DzX"), 0);
                stick.DeadZoneY = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}DzY"), 0);
                stick.AntiDeadZoneX = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}AdzX"), 0);
                stick.AntiDeadZoneY = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}AdzY"), 0);
                stick.Linear = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}Linear"), 0);
                // GetExtendedMapping returns "" (never null) for a missing
                // key, so a bare ?? default never fires (audit G4).
                string curveX = ps.GetExtendedMapping($"ExtendedStick{g}CurveX");
                string curveY = ps.GetExtendedMapping($"ExtendedStick{g}CurveY");
                stick.SensitivityCurveX = string.IsNullOrEmpty(curveX) ? "0,0;1,1" : curveX;
                stick.SensitivityCurveY = string.IsNullOrEmpty(curveY) ? "0,0;1,1" : curveY;
                stick.CenterOffsetX = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}CofX"), 0);
                stick.CenterOffsetY = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}CofY"), 0);
                stick.MaxRangeX = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}MrX"), 100);
                stick.MaxRangeY = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}MrY"), 100);
                stick.MaxRangeXNeg = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}MrXN"), stick.MaxRangeX);
                stick.MaxRangeYNeg = TryParseDouble(ps.GetExtendedMapping($"ExtendedStick{g}MrYN"), stick.MaxRangeY);
            }
            foreach (var trig in padVm.TriggerConfigs)
            {
                if (trig.Index < 2) continue;
                int g = trig.Index;
                trig.DeadZone = TryParseDouble(ps.GetExtendedMapping($"ExtendedTrigger{g}Dz"), 0);
                trig.AntiDeadZone = TryParseDouble(ps.GetExtendedMapping($"ExtendedTrigger{g}Adz"), 0);
                trig.MaxRange = TryParseDouble(ps.GetExtendedMapping($"ExtendedTrigger{g}Mr"), 100);
                string trigCurve = ps.GetExtendedMapping($"ExtendedTrigger{g}Curve");
                trig.SensitivityCurve = string.IsNullOrEmpty(trigCurve) ? "0,0;1,1" : trigCurve;
            }

            // Steering is per assigned device (#94): load THIS device's steering into the
            // sticks (guarded, no dirty). Routes startup selection, device switch, preset/
            // paste, and profile switch through one place so the card always shows the
            // selected device's wheel config.
            padVm.LoadSteeringConfigItems(key => ps.GetExtendedMapping(key));

            // Per-device tuning load is done; mapping descriptors are
            // per-VC and intentionally NOT refreshed here. The Mappings
            // tab is decoupled from the assigned-device dropdown: the
            // user toggling which device is "selected" for tuning must
            // never alter what mappings appear. Callers that legitimately
            // need a mapping refresh (slot init, MappingsRebuilt event,
            // device added/removed) call RefreshMappingsCore directly.
        }

        /// <summary>Per-VC mapping refresh. Reads the slot's MappingSet
        /// — the SOLE source of truth — and populates every MappingItem
        /// in <paramref name="padVm"/>. The Mappings tab is intentionally
        /// device-agnostic: changing the assigned-device dropdown does
        /// NOT affect what's shown here. Legacy per-device descriptors
        /// are converted into the MappingSet at settings load time
        /// (<see cref="SettingsService.LoadFromFile"/> →
        /// <c>BuildOneSlotFromLegacy</c>) and on device assignment
        /// (<see cref="SettingsService.RefreshMappingSetsFromLegacy"/>);
        /// from that point on the MappingSet is authoritative.</summary>
        /// <summary>#155 re-entrancy guard. RefreshMappingsCore mutates the
        /// live MappingItem instances (Clear then repopulate), and every
        /// watched setter raises PropertyChanged synchronously. The
        /// MainWindow mapping handler's immediate domain-push must NOT
        /// re-enter mid-reload: while ExtraSources is transiently empty it
        /// would rewrite the shared domain row to primary-only and destroy
        /// a StickTrim/Custom combine mapping. UI-thread-only, so a plain
        /// bool is sufficient. The user's edits are already pushed at edit
        /// time, so suppressing the push during a reload loses nothing.</summary>
        internal static bool SuppressMappingEditPush;

        private static void RefreshMappingsCore(PadViewModel padVm)
        {
            if (padVm == null) return;

            SuppressMappingEditPush = true;
            try
            {

            Engine.Data.MappingSet slotMs = (padVm.PadIndex >= 0
                && padVm.PadIndex < SettingsManager.SlotMappingSets.Length)
                ? SettingsManager.SlotMappingSets[padVm.PadIndex]
                : null;

            // Read padVm.ActiveLayerMask so the Mappings tab reflects
            // whichever shift layer the user is authoring (defaults to
            // "Base"). A row matching this mask wins; if no row exists for
            // a target on this layer, the MappingItem renders empty so
            // the user can author it in place (the engine still falls
            // through to Base at runtime if NoInherit isn't set).
            string activeMask = string.IsNullOrEmpty(padVm.ActiveLayerMask) ? "Base" : padVm.ActiveLayerMask;

            var msRowsByTarget = new System.Collections.Generic.Dictionary<string, Engine.Data.MappingRow>(
                StringComparer.Ordinal);
            if (slotMs?.Rows != null)
            {
                foreach (var r in slotMs.Rows)
                {
                    if (r == null) continue;
                    if (!string.Equals(r.LayerMask, activeMask, StringComparison.Ordinal)) continue;
                    if (string.IsNullOrEmpty(r.Target)) continue;
                    msRowsByTarget[r.Target] = r;
                }
            }

            foreach (var mapping in padVm.Mappings)
            {
                string target = mapping.TargetSettingName;
                UserDevice primaryUd = null;

                // Load NoInherit from the matching layer row when present
                // (false otherwise — Base rows never carry this flag).
                mapping.NoInherit = msRowsByTarget.TryGetValue(target, out var preCheck)
                                    && preCheck != null && preCheck.NoInherit;

                // Sources[0] is the row's primary. A Direct first source feeds the
                // descriptor-based primary (the usual case). A stateful first source
                // (Incremental / Ramped / InvertOnHold) feeds the reused
                // PrimaryKindSource so the primary can carry a kind, not only Direct
                // (#111 follow-up). Either way the extras start at index 1.
                msRowsByTarget.TryGetValue(target, out var msRow);
                var primarySrc = (msRow?.Sources != null && msRow.Sources.Count > 0) ? msRow.Sources[0] : null;
                bool primaryIsKind = primarySrc != null
                    && !string.Equals(primarySrc.Kind ?? "Direct", "Direct", StringComparison.Ordinal);

                if (primarySrc != null && !primaryIsKind)
                {
                    var primary = primarySrc;
                    string encoded = ReencodePrefixForLegacy(
                        primary.Descriptor, primary.Invert, primary.HalfAxis);
                    mapping.LoadDescriptor(encoded);
                    if (primary.DeadZone > 0) mapping.MappingDeadZone = primary.DeadZone;
                    mapping.IsBidirectional = primary.Bidirectional;
                    mapping.GyroSensitivity = primary.GyroSensitivity > 0 ? primary.GyroSensitivity : 1.0;
                    mapping.MouseCursorSensitivity = primary.MouseCursorSensitivity > 0 ? primary.MouseCursorSensitivity : 1.0;
                    mapping.IrPointerSensitivity = primary.IrPointerSensitivity > 0 ? primary.IrPointerSensitivity : 1.0;
                    mapping.Sensitivity = primary.Sensitivity > 0 ? primary.Sensitivity : 1.0;
                    mapping.PrimarySourceDeviceGuid = primary.DeviceGuid ?? "";
                    mapping.PrimarySourceDeviceLabel = ResolveDeviceLabel(primary.DeviceGuid);
                    mapping.LoadPrimaryKind(null); // Direct primary, reset the kind holder

                    if (!string.IsNullOrEmpty(primary.DeviceGuid)
                        && Guid.TryParse(primary.DeviceGuid, out var primaryGuid))
                    {
                        primaryUd = FindUserDevice(primaryGuid);
                    }
                }
                else
                {
                    // No row (unmapped), or a stateful primary kind whose descriptor
                    // is unused. Clear the descriptor primary and load the kind (null
                    // for the unmapped case) into PrimaryKindSource.
                    mapping.LoadDescriptor("");
                    mapping.PrimarySourceDeviceGuid = "";
                    mapping.PrimarySourceDeviceLabel = "";
                    mapping.MappingDeadZone = 50;
                    mapping.IsBidirectional = false;
                    mapping.GyroSensitivity = 1.0;
                    mapping.MouseCursorSensitivity = 1.0;
                    mapping.IrPointerSensitivity = 1.0;
                    mapping.Sensitivity = 1.0;
                    mapping.LoadPrimaryKind(primaryIsKind ? primarySrc : null);
                }

                MappingDisplayResolver.ResolveDisplayText(mapping, primaryUd);

                // Bipolar Neg-pair lives inside the MappingSet's
                // Sources[1] now (with Invert flipped relative to the
                // primary). The legacy NegSourceDescriptor field is no
                // longer read — leaving it empty signals the UI to
                // render the Neg source as a visible ExtraSources row.
                if (mapping.NegSettingName != null)
                    mapping.LoadNegDescriptor(string.Empty);

                // ExtraSources / CombineMode / CombineExpression from
                // the matching MappingSet row.
                mapping.ExtraSources.Clear();
                mapping.CombineMode = "";
                mapping.CombineExpression = "";
                mapping.TrimDeadzone = 25;
                mapping.TrimRate = 100;
                mapping.TrimResetOnRelease = true;
                if (msRowsByTarget.TryGetValue(target, out var msRow2))
                {
                    mapping.CombineMode = msRow2.CombineMode ?? "";
                    mapping.CombineExpression = msRow2.CombineExpression ?? "";
                    mapping.TrimDeadzone = msRow2.TrimDeadzone;
                    mapping.TrimRate = msRow2.TrimRate;
                    mapping.TrimResetOnRelease = msRow2.TrimResetOnRelease;
                    if (msRow2.Sources != null)
                    {
                        // Sources[0] is the primary (Direct descriptor or a kind loaded
                        // into PrimaryKindSource above), so extras start at index 1.
                        for (int si = 1; si < msRow2.Sources.Count; si++)
                        {
                            var extra = ViewModels.MappingSourceItem.FromDomain(msRow2.Sources[si]);
                            // Resolve the extra's own device to a label, exactly
                            // as the primary above does, so an empty guid reads
                            // "(Any device)". FromDomain leaves the label empty;
                            // without this an imported (empty-guid) secondary
                            // showed blank, or once synced against the slot's
                            // inputs borrowed the slot's first concrete controller.
                            extra.DeviceLabel = ResolveDeviceLabel(msRow2.Sources[si].DeviceGuid);
                            mapping.ExtraSources.Add(extra);
                        }
                    }
                }
            }

            // Mappings now mirror the slot's authoritative MappingSet. Clears the
            // stale flag so SaveViewModelToPadSetting may persist mappings again
            // (see PadViewModel.MappingsViewLoaded for the mid-assign clobber this
            // guards against).
            padVm.MappingsViewLoaded = true;
            }
            finally
            {
                SuppressMappingEditPush = false;
            }
        }

        /// <summary>
        /// Re-encodes a clean descriptor + Invert/HalfAxis flags back into
        /// the legacy "I"/"H"/"IH" prefix form that the existing
        /// MappingItem.LoadDescriptor / Step 3 parser expect. The new
        /// schema stores Invert and HalfAxis as separate per-source bool
        /// flags, but the UI's MappingItem still consumes the prefix-
        /// encoded form for back-compat.
        /// </summary>
        private static string ReencodePrefixForLegacy(string descriptor, bool invert, bool halfAxis)
        {
            if (string.IsNullOrEmpty(descriptor)) return "";
            string prefix = (invert, halfAxis) switch
            {
                (true,  true)  => "IH",
                (true,  false) => "I",
                (false, true)  => "H",
                _              => "",
            };
            return prefix + descriptor;
        }

        /// <summary>
        /// Looks up the user-friendly device label for a DeviceGuid by
        /// scanning UserDevices. Returns "(Any device)" for empty GUID
        /// (the "first available device on this VC" sentinel) and the
        /// raw GUID truncated to 8 chars when the device is unknown.
        /// </summary>
        private static string ResolveDeviceLabel(string deviceGuid)
        {
            if (string.IsNullOrEmpty(deviceGuid)) return "(Any device)";
            if (!Guid.TryParse(deviceGuid, out Guid g)) return deviceGuid;
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                foreach (var ud in SettingsManager.UserDevices.Items)
                {
                    if (ud != null && ud.InstanceGuid == g)
                        return LocalizedDeviceName(ud) ?? deviceGuid;
                }
            }
            // Unknown device — show truncated GUID so the row is still
            // legible.
            string s = deviceGuid;
            return s.Length > 8 ? s.Substring(0, 8) + "…" : s;
        }

        /// <summary>Returns the user-facing device name with localized
        /// strings substituted for aggregate/overlay devices (so the
        /// Mappings tab and recording status text match what the Devices
        /// page shows for "All Keyboards (Merged)" / "All Mice (Merged)" /
        /// "All Touchpads (Merged)" / the touchpad overlay). Falls back
        /// to ResolvedName → ProductName → InstanceName.</summary>
        public static string LocalizedDeviceName(UserDevice ud)
        {
            if (ud == null) return null;
            switch (ud.DevicePath)
            {
                case "aggregate://keyboards": return Strings.Instance.Devices_AllKeyboardsMerged;
                case "aggregate://mice":      return Strings.Instance.Devices_AllMiceMerged;
                case "aggregate://touchpads": return Strings.Instance.Devices_AllTouchpadsMerged;
                case "overlay://touchpad":    return Strings.Instance.Dashboard_TouchpadOverlay;
                default: return ud.ResolvedName ?? ud.ProductName ?? ud.InstanceName;
            }
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

        /// <summary>InvariantCulture float parse used by the gyro tuning
        /// provider to convert PadSetting's string-typed schema fields.</summary>
        private static float TryParseFloatPs(string value, float defaultValue)
        {
            if (string.IsNullOrEmpty(value)) return defaultValue;
            return float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float result) ? result : defaultValue;
        }

        private static bool TryParseBoolPs(string value, bool defaultValue)
        {
            if (string.IsNullOrEmpty(value)) return defaultValue;
            if (value == "1") return true;
            if (value == "0") return false;
            return bool.TryParse(value, out bool result) ? result : defaultValue;
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
        /// <summary>One 250 ms config-snapshot mechanism for the per-(device,
        /// slot) setting providers (IR tuning / pointer mode, gyro bias /
        /// tuning, touchpad + mouse gesture settings). Each resolver still
        /// takes UserSettings.SyncRoot and walks Items, but at most once per
        /// key per 250 ms instead of per poll: the UI thread holds the same
        /// SyncRoot for whole multi-slot save loops, so kHz-rate provider
        /// calls on the poll thread would stall the rate-holding thread
        /// exactly while the user aims. Mirrors InputManager's
        /// _triggerRouteCfg / _mirrorEngageCfg 250 ms snapshots; a user edit
        /// propagating within a quarter second is imperceptible by that same
        /// house standard. ConcurrentDictionary keeps the occasional
        /// UI-thread caller (picker builds) safe; growth is bounded by
        /// (device, slot) pairs. Instances are created per engine start in
        /// the wiring block and dropped with the providers on stop.</summary>
        private sealed class ProviderSnapshot<TKey, TValue>
        {
            private readonly System.Collections.Concurrent.ConcurrentDictionary<TKey, (long Tick, TValue Value)> _map = new();
            private readonly Func<TKey, TValue> _resolve;
            public ProviderSnapshot(Func<TKey, TValue> resolve) { _resolve = resolve; }
            public TValue Get(TKey key)
            {
                long now = Environment.TickCount64;
                if (_map.TryGetValue(key, out var e) && now - e.Tick < 250)
                    return e.Value;
                var v = _resolve(key);
                _map[key] = (now, v);
                return v;
            }
        }

        /// <summary>Resolves the per-(slot, device) mouse-gesture settings
        /// straight from the active profile's PadSetting (issue #200), without
        /// depending on the engine being started. Used by both the runtime
        /// provider and the mapping-picker build so the two agree. Returns
        /// Default() when nothing is stored for the pair.</summary>
        private static PadForge.Engine.Mouse.MouseGestureSettings ResolveMouseGestureSettingsForSlot(
            int slotIndex, System.Guid deviceGuid)
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return PadForge.Engine.Mouse.MouseGestureSettings.Default();
            PadSetting ps = null;
            lock (settings.SyncRoot)
            {
                for (int i = 0; i < settings.Items.Count; i++)
                {
                    var us = settings.Items[i];
                    if (us == null) continue;
                    if (us.MapTo != slotIndex) continue;
                    if (us.InstanceGuid != deviceGuid) continue;
                    ps = us.GetPadSetting();
                    break;
                }
            }
            if (ps?.MouseGestureSettings == null)
                return PadForge.Engine.Mouse.MouseGestureSettings.Default();
            string guidStr = deviceGuid.ToString();
            for (int i = 0; i < ps.MouseGestureSettings.Length; i++)
            {
                var entry = ps.MouseGestureSettings[i];
                if (entry == null) continue;
                if (!string.Equals(entry.DeviceGuid, guidStr, StringComparison.OrdinalIgnoreCase))
                    continue;
                return entry.Settings ?? PadForge.Engine.Mouse.MouseGestureSettings.Default();
            }
            return PadForge.Engine.Mouse.MouseGestureSettings.Default();
        }

        /// <summary>Public entry point for the assignment-change path:
        /// rebuilds <see cref="MappingItem.AvailableInputs"/> from the
        /// slot's current device list without requiring a specific
        /// "primary" device. Resolves stale dropdown selections that
        /// would otherwise survive an unassign. The previously
        /// selected source's InputChoice gets dropped from the rebuilt
        /// list, so SyncSelectedInputFromDescriptor clears
        /// SelectedInput when no device-matched choice remains.</summary>
        public void RefreshAvailableInputsForSlot(PadViewModel padVm)
        {
            if (padVm == null) return;
            // PopulateAvailableInputs orders the slot's primary device
            // first; pass null to use MappedDevices order alone, which
            // is what callers want when no device is selected.
            UserDevice ud = null;
            var sel = padVm.SelectedMappedDevice;
            if (sel != null && sel.InstanceGuid != Guid.Empty)
                ud = FindUserDevice(sel.InstanceGuid);
            PopulateAvailableInputs(padVm, ud);
        }

        private void PopulateAvailableInputs(PadViewModel padVm, UserDevice ud)
        {
            if (padVm == null) return;

            // Build a flat cross-device InputChoice list ordered
            // primary-device-first (so the picker's GroupStyle headers
            // come out in slot-display order). Each choice is tagged
            // with its device's GUID + friendly label so the picker
            // groups by device.
            var orderedDevices = new System.Collections.Generic.List<(System.Guid g, UserDevice u)>();
            if (ud != null && ud.InstanceGuid != System.Guid.Empty)
                orderedDevices.Add((ud.InstanceGuid, ud));
            foreach (var md in padVm.MappedDevices)
            {
                if (md == null || md.InstanceGuid == System.Guid.Empty) continue;
                if (orderedDevices.Exists(t => t.g == md.InstanceGuid)) continue;
                orderedDevices.Add((md.InstanceGuid, FindUserDevice(md.InstanceGuid)));
            }

            var flat = new System.Collections.Generic.List<PadForge.ViewModels.InputChoice>();

            // The "(Any device)" group comes FIRST, always. It carries the
            // device-agnostic descriptor namespaces (abstract "Gamepad ..."
            // family, gyro, the touchpad families) tagged with the empty
            // DeviceGuid: exactly what the Workshop translator stores on
            // every imported row ("resolves on whichever device feeds the
            // slot"). Empty-guid sources select out of THIS group instead
            // of borrowing a concrete device's entry, so a device-agnostic
            // mapping never renders under a concrete controller's header,
            // and a slot with no devices at all still offers a working
            // picker (owner report 2026-07-13: imported rows either showed
            // the previous profile's controller as their group, or, with no
            // stale device to borrow, lost their selection entirely).
            // Leading position also means SyncSelectedInputFromDescriptor's
            // first-match walk lands here for empty-guid sources.
            string anyLabel = ResolveDeviceLabel(null);
            foreach (var c in MappingDisplayResolver.BuildDeviceAgnosticChoices())
            {
                flat.Add(new PadForge.ViewModels.InputChoice
                {
                    Descriptor = c.Descriptor,
                    DisplayName = c.DisplayName,
                    DeviceGuid = string.Empty,
                    DeviceLabel = anyLabel,
                });
            }

            foreach (var (g, udi) in orderedDevices)
            {
                string key = g.ToString().ToLowerInvariant();
                string label = ResolveDeviceLabel(g.ToString());

                // Per-pad gesture-settings provider: gates which gesture
                // categories appear in the dropdown per the user's
                // Touchpad-tab toggles. Scoped to the slot whose picker
                // is being populated so per-slot toggles drive per-slot
                // dropdown contents. _inputManager is null pre-engine-
                // start so guard for that case.
                System.Func<int, PadForge.Engine.Touchpad.TouchpadGestureSettings> tpSettingsForPad = null;
                if (_inputManager?.TouchpadGestureSettingsProvider != null)
                {
                    int slot = padVm.PadIndex;
                    tpSettingsForPad = padIdx => _inputManager.TouchpadGestureSettingsProvider(slot, g, padIdx);
                }

                // Engine-independent: resolve straight from PadSetting so the
                // picker reflects the checked gesture buttons whether or not
                // the engine is running (the provider is null when stopped,
                // which otherwise falls back to the X1-only default).
                int mgSlot = padVm.PadIndex;
                System.Guid mgDevice = g;
                System.Func<PadForge.Engine.Mouse.MouseGestureSettings> mouseGestures =
                    () => ResolveMouseGestureSettingsForSlot(mgSlot, mgDevice);

                var raw = MappingDisplayResolver.BuildInputChoices(udi, tpSettingsForPad, mouseGestures)
                          ?? System.Array.Empty<PadForge.ViewModels.InputChoice>();
                foreach (var c in raw)
                {
                    flat.Add(new PadForge.ViewModels.InputChoice
                    {
                        Descriptor = c.Descriptor,
                        DisplayName = c.DisplayName,
                        DeviceGuid = key,
                        DeviceLabel = label,
                    });
                }

                // Surface the active profile's custom touchpad gestures
                // for any touchpad-capable device that matches the
                // per-gesture DeviceClass / TouchpadIndex filter.
                // BuildInputChoices is device-only and doesn't have
                // profile context, so the custom-gesture surfacing
                // lives here next to the per-device flat-list build.
                if ((udi.HasTouchpad || udi.IsTouchpad) && _activeTouchpadGestures.Count > 0)
                {
                    string devClass = ResolveDeviceClass(udi);
                    // Match MappingDisplayResolver's pad-disambiguator
                    // policy: only prepend "Pad N — " when the device has
                    // more than one touchpad surface (Triton / Steam
                    // Deck / SC original). Single-pad devices keep the
                    // bare gesture name to avoid label clutter.
                    int numPads = 1;
                    try
                    {
                        var st = udi.Device?.GetCurrentState();
                        if (st?.Touchpads != null && st.Touchpads.Length > 0)
                            numPads = st.Touchpads.Length;
                    }
                    catch { /* numPads stays 1 */ }
                    bool multiPad = numPads > 1;
                    var si = PadForge.Resources.Strings.Strings.Instance;

                    foreach (var cg in _activeTouchpadGestures)
                    {
                        if (cg == null || string.IsNullOrWhiteSpace(cg.Name)) continue;
                        if (!cg.Enabled) continue;
                        bool classOk = string.IsNullOrEmpty(cg.DeviceClass)
                                       || string.Equals(cg.DeviceClass, "any", System.StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(cg.DeviceClass, devClass, System.StringComparison.OrdinalIgnoreCase);
                        if (!classOk) continue;
                        int padIdx = cg.TouchpadIndex < 0 ? 0 : cg.TouchpadIndex;
                        // Per-pad settings gate: respects the user's
                        // Touchpad-tab toggles same as the in-box list.
                        // "InBoxOnly" mode hides custom; disabled pads
                        // contribute nothing.
                        var ps = tpSettingsForPad?.Invoke(padIdx);
                        if (ps != null)
                        {
                            if (!ps.Enabled) continue;
                            if (string.Equals(ps.Mode, "InBoxOnly", System.StringComparison.OrdinalIgnoreCase))
                                continue;
                        }
                        // Display pad number is 1-based (matches the in-box
                        // gesture prefix in MappingDisplayResolver and the
                        // Devices previews); the Descriptor below stays 0-based.
                        string display = multiPad
                            ? string.Format(si.Mapping_TouchpadGesture_PadPrefix_Format, padIdx + 1, cg.Name)
                            : cg.Name;
                        flat.Add(new PadForge.ViewModels.InputChoice
                        {
                            Descriptor = $"Touchpad {padIdx} Custom_{cg.Name}",
                            DisplayName = display,
                            DeviceGuid = key,
                            DeviceLabel = label,
                        });
                    }
                }
            }

            foreach (var mapping in padVm.Mappings)
            {
                mapping.InputSelectedFromDropdown -= OnInputSelectedFromDropdown;
                mapping.InputSelectedFromDropdown += OnInputSelectedFromDropdown;

                mapping.AvailableInputs.Clear();
                foreach (var c in flat)
                    mapping.AvailableInputs.Add(c);
                mapping.SyncSelectedInputFromDescriptor();
                mapping.RefreshAllExtraSourceInputs();
            }

            // — also refresh the slot-level cross-device picker
            // list (used by the Gyro tab's Aim Engage button picker).
            // Fire SelectedInput re-eval so the ComboBox resolves the
            // saved descriptor against the freshly-populated list.
            padVm.SlotAvailableInputs.Clear();
            foreach (var c in flat)
                padVm.SlotAvailableInputs.Add(c);
            padVm.OnGyroAimEngageSelectedInputRefresh();
            padVm.OnLeftTriggerRouteActivatorSelectedInputRefresh();
            padVm.OnRightTriggerRouteActivatorSelectedInputRefresh();
            padVm.OnMirrorEngageSelectedInputRefresh();

            // Macro trigger dropdown (#177): only the choices that
            // convert to a TriggerInputEntry. Same source list, so the
            // Touchpad tab's enable gates carry over unchanged.
            padVm.SlotMacroTriggerChoices.Clear();
            foreach (var c in flat)
                if (MacroItem.TryBuildTriggerEntry(c, out _))
                    padVm.SlotMacroTriggerChoices.Add(c);
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

            // Issue #61 — also paste the multi-source ExtraSources +
            // CombineMode + Custom formula payload onto the target
            // slot's MappingSet, with the target device's GUID
            // substituted into each Source.
            ApplyMultiSourceRowsToCurrentDevice(padIndex, selected.InstanceGuid,
                source.DeviceScopedMultiSourceRows);

            // Reload the ViewModel to reflect the new values.
            LoadPadSettingToViewModel(padVm, selected.InstanceGuid);
            PopulateAvailableInputs(padVm, FindUserDevice(selected.InstanceGuid));
        }

        /// <summary>
        /// Applies a PadSetting from a source layout to a device on the given
        /// slot, with cross-layout translation.
        ///
        /// <para><paramref name="targetDeviceGuidOverride"/>: which physical
        /// device on <paramref name="padIndex"/> receives the copy. Default
        /// (null) = the slot's currently-selected device. <b>Copy From</b>
        /// passes the SOURCE device's GUID here when that device is also
        /// mapped to the target slot — so "Copy From [DualSense on slot 0]"
        /// lands on the DualSense on this slot rather than being re-tagged
        /// onto whatever happens to be selected (e.g. the slot's keyboard,
        /// which has no analog axes — that produced phantom "keyboard Axis 0"
        /// sources and doubled every row, see the Copy From corruption
        /// report). The descriptors a recording produces are device-specific;
        /// re-tagging them onto a different KIND of device yields garbage, so
        /// we prefer the same-device target whenever one exists.</para>
        /// </summary>
        public void ApplyPadSettingToCurrentDeviceTranslated(int padIndex, PadSetting source,
            VirtualControllerType sourceType, bool sourceIsExtended,
            VirtualControllerType targetType, bool targetIsExtended,
            Guid? targetDeviceGuidOverride = null)
        {
            if (source == null || padIndex < 0 || padIndex >= _mainVm.Pads.Count)
                return;

            var padVm = _mainVm.Pads[padIndex];
            Guid targetGuid = targetDeviceGuidOverride
                ?? padVm.SelectedMappedDevice?.InstanceGuid
                ?? Guid.Empty;
            if (targetGuid == Guid.Empty)
                return;

            var us = SettingsManager.FindSettingByInstanceGuidAndSlot(targetGuid, padIndex);
            if (us == null) return;

            var ps = us.GetPadSetting();
            if (ps == null) return;

            // Copy with cross-layout translation.
            ps.CopyFromTranslated(source, sourceType, sourceIsExtended, targetType, targetIsExtended);

            // Multi-source rows only round-trip when source and target
            // share a layout — cross-layout target names don't line up.
            if (MappingTranslation.IsSameLayout(sourceType, sourceIsExtended, targetType, targetIsExtended))
            {
                ApplyMultiSourceRowsToCurrentDevice(padIndex, targetGuid,
                    source.DeviceScopedMultiSourceRows);
            }

            // Reload the ViewModel to reflect the new values. The mapping
            // pass reads the slot's MappingSet (device-agnostic), so it
            // doesn't matter which device GUID we pass for that; pass the
            // currently-selected device so its per-device tuning fields
            // (deadzones / sensitivity / FFB) stay on screen.
            Guid viewDevice = padVm.SelectedMappedDevice?.InstanceGuid ?? targetGuid;
            LoadPadSettingToViewModel(padVm, viewDevice);
            // LoadPadSettingToViewModel only loads per-device TUNING; mapping
            // rows stay stale unless we explicitly refresh them from the
            // freshly-written MappingSet. Without this, the autosave 250 ms
            // later runs PushUiExtraSourcesIntoSlotMappingSets against the
            // pre-paste MappingItems and clobbers the just-copied rows —
            // i.e. Copy / Paste / Copy From appear to do nothing.
            RefreshMappingsCore(padVm);
            PopulateAvailableInputs(padVm, FindUserDevice(viewDevice));
        }

        /// <summary>Builds the per-device PadSetting snapshot array for
        /// a source slot. One entry per UserSetting whose MapTo equals
        /// <paramref name="sourcePadIndex"/>. The nested PadSettingJson
        /// strings are produced via <see cref="PadSetting.ToJson"/> with
        /// the slot-level fields (SlotDeviceConfigsJson,
        /// SlotExtendedConfigJson, SlotMidiConfigJson,
        /// SlotPerDeviceSettingsJson, SlotMultiSourceRows,
        /// DeviceScopedMultiSourceRows) cleared so the nesting doesn't
        /// recurse and the slot-level data isn't redundantly carried on
        /// every entry. Caller passes the layout type so the outer JSON
        /// signals the source layout once at the wrapping PadSetting
        /// level. Returns null when the slot has zero UserSettings.</summary>
        public static PadForge.Engine.Data.PerDeviceSettingsEntry[]
            BuildPerDeviceSettingsSnapshot(int sourcePadIndex,
                VirtualControllerType layoutType, bool layoutIsExtended)
        {
            var settings = SettingsManager.UserSettings;
            if (settings?.Items == null) return null;

            var usList = settings.FindByPadIndex(sourcePadIndex);
            if (usList == null || usList.Count == 0) return null;

            var entries = new System.Collections.Generic.List<
                PadForge.Engine.Data.PerDeviceSettingsEntry>(usList.Count);
            foreach (var us in usList)
            {
                if (us == null) continue;
                var sourcePs = us.GetPadSetting();
                if (sourcePs == null) continue;

                // Clone so we can clear slot-level fields without mutating
                // the live PadSetting attached to the live UserSetting.
                var clone = sourcePs.CloneDeep();
                clone.SlotDeviceConfigsJson = null;
                clone.SlotExtendedConfigJson = null;
                clone.SlotMidiConfigJson = null;
                clone.SlotKbmConfigJson = null;
                clone.SlotPerDeviceSettingsJson = null;
                clone.SlotMultiSourceRows = null;
                clone.DeviceScopedMultiSourceRows = null;

                entries.Add(new PadForge.Engine.Data.PerDeviceSettingsEntry
                {
                    InstanceGuid = us.InstanceGuid.ToString(),
                    ProductGuid = us.ProductGuid.ToString(),
                    ProductName = us.ProductName ?? "",
                    PadSettingJson = clone.ToJson(layoutType, layoutIsExtended),
                });
            }
            return entries.Count > 0 ? entries.ToArray() : null;
        }

        /// <summary>Applies a per-device PadSetting snapshot array to a
        /// target slot. Each entry is matched to a target-slot device by
        /// InstanceGuid first, then ProductGuid as a fallback (covers the
        /// "same controller model, different physical unit" case). Entries
        /// with no match are skipped — paste never auto-creates devices.
        /// </summary>
        /// <remarks>Source-layout / target-layout pairs are passed
        /// through to <see cref="ApplyPadSettingToCurrentDeviceTranslated"/>
        /// per entry, so cross-layout pastes (e.g. Xbox→PS) still get
        /// the layout translation that single-device paste enjoys. The
        /// outer Copy / Paste flow's wholesale MappingSet replacement
        /// already ran by the time this helper is called; this method
        /// only carries per-device tuning (deadzones, sensitivity, FFB,
        /// Gyro, TouchpadSettings).</remarks>
        public void ApplyPerDeviceSettingsToSlot(int targetPadIndex,
            PadForge.Engine.Data.PerDeviceSettingsEntry[] entries,
            VirtualControllerType sourceLayoutType, bool sourceLayoutIsExtended,
            VirtualControllerType targetLayoutType, bool targetLayoutIsExtended)
        {
            if (entries == null || entries.Length == 0) return;
            if (targetPadIndex < 0 || targetPadIndex >= _mainVm.Pads.Count) return;

            // Build the target slot's device manifest once so the
            // per-entry match loop doesn't take the UserSettings lock
            // N times.
            var targetSlotDevices = new System.Collections.Generic.List<UserSetting>();
            var us = SettingsManager.UserSettings;
            if (us != null)
            {
                lock (us.SyncRoot)
                {
                    for (int i = 0; i < us.Items.Count; i++)
                    {
                        if (us.Items[i] != null && us.Items[i].MapTo == targetPadIndex)
                            targetSlotDevices.Add(us.Items[i]);
                    }
                }
            }
            if (targetSlotDevices.Count == 0) return;

            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.PadSettingJson)) continue;

                // First-preference: match by InstanceGuid (same physical
                // device on same machine — perfect round-trip).
                Guid matchTarget = Guid.Empty;
                if (Guid.TryParse(entry.InstanceGuid, out var srcInstance))
                {
                    foreach (var tu in targetSlotDevices)
                    {
                        if (tu.InstanceGuid == srcInstance)
                        { matchTarget = tu.InstanceGuid; break; }
                    }
                }

                // Fallback: match by ProductGuid (same controller model,
                // different physical unit — e.g. a second DualSense).
                if (matchTarget == Guid.Empty
                    && Guid.TryParse(entry.ProductGuid, out var srcProduct)
                    && srcProduct != Guid.Empty)
                {
                    foreach (var tu in targetSlotDevices)
                    {
                        if (tu.ProductGuid == srcProduct)
                        { matchTarget = tu.InstanceGuid; break; }
                    }
                }

                // No match → skip. Paste does not auto-create devices.
                if (matchTarget == Guid.Empty) continue;

                // Re-deserialize the nested PadSetting so we get a fresh
                // instance with no cross-entry state aliasing.
                var devicePs = PadSetting.FromJson(entry.PadSettingJson,
                    out var entrySourceType, out var entrySourceIsExtended);
                if (devicePs == null) continue;

                // Honour the entry's own layout metadata if present, else
                // fall back to the wrapping payload's layout.
                var srcType = entrySourceType != VirtualControllerType.Xbox
                    || entrySourceIsExtended
                    ? entrySourceType
                    : sourceLayoutType;
                bool srcExt = entrySourceIsExtended || sourceLayoutIsExtended;

                ApplyPadSettingToCurrentDeviceTranslated(
                    targetPadIndex, devicePs,
                    srcType, srcExt,
                    targetLayoutType, targetLayoutIsExtended,
                    matchTarget);
            }
        }

        /// <summary>Issue #61 paste helper. For each row in
        /// <paramref name="deviceRows"/> (a snapshot of the source
        /// slot's multi-source rows where the source device
        /// participated), find or create the matching row in the
        /// target slot's MappingSet, remove the target device's
        /// existing Sources contribution, then add the snapshot's
        /// Sources with their DeviceGuid substituted for the target
        /// device's GUID. Other devices' contributions on the same
        /// row are preserved.</summary>
        private static void ApplyMultiSourceRowsToCurrentDevice(int padIndex,
            Guid targetDeviceGuid,
            System.Collections.Generic.IList<Engine.Data.MappingRow> deviceRows)
        {
            if (deviceRows == null || deviceRows.Count == 0) return;
            if (padIndex < 0 || padIndex >= SettingsManager.SlotMappingSets.Length) return;

            var ms = SettingsManager.SlotMappingSets[padIndex]
                  ?? (SettingsManager.SlotMappingSets[padIndex] = new Engine.Data.MappingSet());
            string targetGuid = targetDeviceGuid.ToString().ToLowerInvariant();

            foreach (var srcRow in deviceRows)
            {
                if (srcRow == null || string.IsNullOrEmpty(srcRow.Target)) continue;
                string layer = string.IsNullOrEmpty(srcRow.LayerMask) ? "Base" : srcRow.LayerMask;

                Engine.Data.MappingRow targetRow = null;
                foreach (var r in ms.Rows)
                {
                    if (r == null) continue;
                    if (string.Equals(r.Target, srcRow.Target, StringComparison.Ordinal)
                        && string.Equals(r.LayerMask ?? "Base", layer, StringComparison.Ordinal))
                    { targetRow = r; break; }
                }
                if (targetRow == null)
                {
                    targetRow = new Engine.Data.MappingRow
                    {
                        Target = srcRow.Target,
                        LayerMask = layer,
                        CombineMode = srcRow.CombineMode ?? "",
                        CombineExpression = srcRow.CombineExpression ?? "",
                        TrimDeadzone = srcRow.TrimDeadzone,
                        TrimRate = srcRow.TrimRate,
                        TrimResetOnRelease = srcRow.TrimResetOnRelease,
                        Sources = new System.Collections.Generic.List<Engine.Data.MappingSource>(),
                    };
                    ms.Rows.Add(targetRow);
                }
                else
                {
                    // Carry over the source row's combine choice so a
                    // user-authored Sum / Average / Custom comes along.
                    targetRow.CombineMode = srcRow.CombineMode ?? "";
                    targetRow.CombineExpression = srcRow.CombineExpression ?? "";
                    targetRow.TrimDeadzone = srcRow.TrimDeadzone;
                    targetRow.TrimRate = srcRow.TrimRate;
                    targetRow.TrimResetOnRelease = srcRow.TrimResetOnRelease;
                }

                // Strip the target device's existing Sources — we're
                // replacing this device's contribution wholesale.
                if (targetRow.Sources != null)
                {
                    targetRow.Sources.RemoveAll(s =>
                        s != null
                        && string.Equals(s.DeviceGuid ?? "", targetGuid, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    targetRow.Sources = new System.Collections.Generic.List<Engine.Data.MappingSource>();
                }

                // Inject the snapshot's Sources with target device GUID.
                if (srcRow.Sources != null)
                {
                    foreach (var s in srcRow.Sources)
                    {
                        if (s == null) continue;
                        var clonedSrc = s.Clone();
                        clonedSrc.DeviceGuid = targetGuid;   // Clone() carries every Param* field
                        targetRow.Sources.Add(clonedSrc);
                    }
                }
            }
        }

        /// <summary>Deep-clones a <see cref="Engine.Data.MappingSet"/>
        /// including every row's Sources list. Profile snapshots and the
        /// live SlotMappingSets MUST own independent copies — reference
        /// sharing meant a runtime mutation in one profile bled across
        /// every other profile snapshot that happened to share the ref
        /// (e.g. all profiles created from the same starting state in a
        /// single session, since every snapshot grabbed the live ref).</summary>
        public static Engine.Data.MappingSet CloneMappingSetDeep(Engine.Data.MappingSet src)
        {
            if (src == null) return null;
            var copy = new Engine.Data.MappingSet();
            CopyShiftActivators(src, copy);
            if (src.Rows != null)
            {
                foreach (var r in src.Rows)
                {
                    if (r == null) continue;
                    var rc = new Engine.Data.MappingRow
                    {
                        Target = r.Target,
                        LayerMask = r.LayerMask ?? "Base",
                        CombineMode = r.CombineMode ?? "",
                        CombineExpression = r.CombineExpression ?? "",
                        NoInherit = r.NoInherit,
                        TrimDeadzone = r.TrimDeadzone,
                        TrimRate = r.TrimRate,
                        TrimResetOnRelease = r.TrimResetOnRelease,
                        Sources = new System.Collections.Generic.List<Engine.Data.MappingSource>(),
                    };
                    if (r.Sources != null)
                    {
                        foreach (var s in r.Sources)
                        {
                            if (s == null) continue;
                            rc.Sources.Add(s.Clone());   // Clone() carries every Param* field
                        }
                    }
                    copy.Rows.Add(rc);
                }
            }
            return copy;
        }

        /// <summary>Deep-copies every <see cref="Engine.Data.ShiftActivator"/>
        /// from <paramref name="src"/> into <paramref name="dst"/>'s
        /// <see cref="Engine.Data.MappingSet.ShiftActivators"/> list. Used by
        /// both <see cref="CloneMappingSetDeep"/> and the Copy-From-Slot path
        /// so shift authoring round-trips alongside row data.</summary>
        private static void CopyShiftActivators(Engine.Data.MappingSet src, Engine.Data.MappingSet dst,
            int retargetSlot = -1)
        {
            if (src == null) return;
            // Base-layer flyout appearance (#119) travels with the shift authoring.
            dst.BaseLayerName = src.BaseLayerName ?? "";
            dst.BaseColor = src.BaseColor ?? "";
            dst.BaseIcon = src.BaseIcon ?? "";
            if (src.ShiftActivators == null) return;
            dst.ShiftActivators ??= new System.Collections.Generic.List<Engine.Data.ShiftActivator>();
            foreach (var a in src.ShiftActivators)
            {
                if (a == null) continue;
                string deviceGuid = a.DeviceGuid ?? "";
                string chordSecondGuid = a.ChordSecondDeviceGuid ?? "";
                string cyclePrevGuid = a.CyclePrevDeviceGuid ?? "";
                string cyclePrevDesc = a.CyclePrevDescriptor ?? "";
                if (retargetSlot >= 0)
                {
                    var retargeted = RetargetDeviceGuidForSlot(deviceGuid, retargetSlot);
                    if (retargeted == null) continue;
                    deviceGuid = retargeted;
                    if (!string.IsNullOrEmpty(chordSecondGuid))
                    {
                        var retargetedChord = RetargetDeviceGuidForSlot(chordSecondGuid, retargetSlot);
                        if (retargetedChord == null) continue;
                        chordSecondGuid = retargetedChord;
                    }
                    // Cycle Previous button (#119). Unlike a chord, a prev-button
                    // device that isn't on the target slot drops just that button,
                    // not the whole activator (the Next button still works).
                    if (!string.IsNullOrEmpty(cyclePrevGuid))
                    {
                        var retargetedPrev = RetargetDeviceGuidForSlot(cyclePrevGuid, retargetSlot);
                        if (retargetedPrev == null) { cyclePrevGuid = ""; cyclePrevDesc = ""; }
                        else cyclePrevGuid = retargetedPrev;
                    }
                }
                dst.ShiftActivators.Add(new Engine.Data.ShiftActivator
                {
                    DeviceGuid = deviceGuid,
                    Descriptor = a.Descriptor ?? "",
                    Mode = a.Mode ?? "Hold",
                    LayerMask = a.LayerMask ?? "Shift",
                    LayerName = a.LayerName ?? "",
                    InheritUnmapped = a.InheritUnmapped,
                    JumpToLayer = a.JumpToLayer ?? "",
                    DelayMs = a.DelayMs,
                    PostponeMapping = a.PostponeMapping,
                    Color = a.Color ?? "",
                    Kind = a.Kind ?? "Button",
                    ChordSecondDeviceGuid = chordSecondGuid,
                    ChordSecondDescriptor = a.ChordSecondDescriptor ?? "",
                    AxisThreshold = a.AxisThreshold,
                    CycleLayers = a.CycleLayers ?? "",
                    CyclePrevDeviceGuid = cyclePrevGuid,
                    CyclePrevDescriptor = cyclePrevDesc,
                    CycleWrap = a.CycleWrap,
                    CycleIncludeBase = a.CycleIncludeBase,
                    Icon = a.Icon ?? "",
                    AutoCancelMs = a.AutoCancelMs,
                });
            }
        }

        /// <summary>Clipboard snapshot of a slot's shift authoring (activators
        /// + Base flyout appearance). Serialized into the Copy payload so
        /// Copy / Paste carries shift layers, matching Copy From (#119).</summary>
        public sealed class ShiftLayerSnapshot
        {
            public System.Collections.Generic.List<Engine.Data.ShiftActivator> Activators { get; set; }
            public string BaseLayerName { get; set; } = "";
            public string BaseColor { get; set; } = "";
            public string BaseIcon { get; set; } = "";
        }

        /// <summary>Builds the shift-layer clipboard snapshot JSON for a slot,
        /// or null when the slot has no shift authoring. Device GUIDs are kept
        /// source-side; the paste path retargets them.</summary>
        public static string BuildShiftLayerSnapshotJson(int padIndex)
        {
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null || padIndex < 0 || padIndex >= sets.Length) return null;
            var ms = sets[padIndex];
            if (ms == null) return null;
            bool hasActivators = ms.ShiftActivators != null && ms.ShiftActivators.Count > 0;
            bool hasBase = !string.IsNullOrEmpty(ms.BaseLayerName)
                || !string.IsNullOrEmpty(ms.BaseColor) || !string.IsNullOrEmpty(ms.BaseIcon);
            if (!hasActivators && !hasBase) return null;
            var snap = new ShiftLayerSnapshot
            {
                Activators = ms.ShiftActivators,
                BaseLayerName = ms.BaseLayerName ?? "",
                BaseColor = ms.BaseColor ?? "",
                BaseIcon = ms.BaseIcon ?? "",
            };
            return System.Text.Json.JsonSerializer.Serialize(snap,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
        }

        /// <summary>Paste companion: replaces a slot's shift authoring from a
        /// clipboard snapshot, retargeting activator device GUIDs onto the
        /// target slot's same-product devices (#119).</summary>
        public static void ApplyShiftLayerSnapshotJson(int padIndex, string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null || padIndex < 0 || padIndex >= sets.Length) return;
            ShiftLayerSnapshot snap;
            try { snap = System.Text.Json.JsonSerializer.Deserialize<ShiftLayerSnapshot>(json); }
            catch { return; }
            if (snap == null) return;

            var slotMs = sets[padIndex] ??= new Engine.Data.MappingSet();
            var tempSrc = new Engine.Data.MappingSet
            {
                ShiftActivators = snap.Activators,
                BaseLayerName = snap.BaseLayerName ?? "",
                BaseColor = snap.BaseColor ?? "",
                BaseIcon = snap.BaseIcon ?? "",
            };
            // Build into a detached MappingSet, then swap the finished list
            // onto the live slot in one atomic reference assignment. The
            // engine polling thread enumerates slotMs.ShiftActivators every
            // frame without our lock, so it must never observe a list that
            // is mid-fill (an incremental Add path can throw inside its
            // foreach). This mirrors how ApplySlotMappingSetFromRows swaps a
            // whole fresh MappingSet rather than mutating the live one.
            var built = new Engine.Data.MappingSet();
            CopyShiftActivators(tempSrc, built, retargetSlot: padIndex);
            slotMs.ShiftActivators = built.ShiftActivators;
            slotMs.BaseLayerName = built.BaseLayerName;
            slotMs.BaseColor = built.BaseColor;
            slotMs.BaseIcon = built.BaseIcon;
        }

        /// <summary>Whole-slot snapshot of every row in the given slot's
        /// MappingSet, with source DeviceGuids preserved as-is. Used by
        /// Copy so the clipboard round-trip carries every device's
        /// contribution, not just the slot's currently-selected device's
        /// slice. Each MappingSource is cloned so the snapshot is safe to
        /// mutate without touching the live MappingSet.</summary>
        public static System.Collections.Generic.List<Engine.Data.MappingRow>
            ExtractAllRowsForSlot(int padIndex)
        {
            var result = new System.Collections.Generic.List<Engine.Data.MappingRow>();
            if (padIndex < 0 || padIndex >= SettingsManager.SlotMappingSets.Length) return result;
            var ms = SettingsManager.SlotMappingSets[padIndex];
            if (ms?.Rows == null) return result;

            foreach (var row in ms.Rows)
            {
                if (row == null) continue;
                var clonedSources = new System.Collections.Generic.List<Engine.Data.MappingSource>();
                if (row.Sources != null)
                {
                    foreach (var s in row.Sources)
                    {
                        if (s == null) continue;
                        clonedSources.Add(s.Clone());   // Clone() carries every Param* field
                    }
                }
                result.Add(new Engine.Data.MappingRow
                {
                    Target = row.Target,
                    LayerMask = row.LayerMask ?? "Base",
                    CombineMode = row.CombineMode ?? "",
                    CombineExpression = row.CombineExpression ?? "",
                    TrimDeadzone = row.TrimDeadzone,
                    TrimRate = row.TrimRate,
                    TrimResetOnRelease = row.TrimResetOnRelease,
                    Sources = clonedSources,
                });
            }
            return result;
        }

        /// <summary>Paste companion: replaces a target slot's MappingSet
        /// wholesale from a snapshot built by <see cref="ExtractAllRowsForSlot"/>.
        /// Each source's DeviceGuid is retargeted onto the target slot's
        /// same-ProductGuid (same "variation") device — see
        /// <see cref="RetargetDeviceGuidForSlot"/> for the exact rule.
        /// Sources whose product isn't represented on the target slot are
        /// dropped from the cloned row.</summary>
        public static void ApplySlotMappingSetFromRows(int padIndex,
            System.Collections.Generic.IList<Engine.Data.MappingRow> rows)
        {
            if (padIndex < 0 || padIndex >= SettingsManager.SlotMappingSets.Length) return;
            if (rows == null) return;

            var copy = new Engine.Data.MappingSet();
            foreach (var r in rows)
            {
                if (r == null) continue;
                var rc = new Engine.Data.MappingRow
                {
                    Target = r.Target,
                    LayerMask = r.LayerMask ?? "Base",
                    CombineMode = r.CombineMode ?? "",
                    CombineExpression = r.CombineExpression ?? "",
                    TrimDeadzone = r.TrimDeadzone,
                    TrimRate = r.TrimRate,
                    TrimResetOnRelease = r.TrimResetOnRelease,
                    Sources = new System.Collections.Generic.List<Engine.Data.MappingSource>(),
                };
                if (r.Sources != null)
                {
                    foreach (var s in r.Sources)
                    {
                        if (s == null) continue;
                        var retargeted = RetargetDeviceGuidForSlot(s.DeviceGuid, padIndex);
                        if (retargeted == null) continue;
                        var clonedSrc = s.Clone();
                        clonedSrc.DeviceGuid = retargeted;   // Clone() carries every Param* field
                        rc.Sources.Add(clonedSrc);
                    }
                }
                copy.Rows.Add(rc);
            }
            SettingsManager.SlotMappingSets[padIndex] = copy;
        }

        /// <summary>
        /// Retargets a source row's DeviceGuid onto the target slot's
        /// equivalent same-ProductGuid device, so Copy From / Copy / Paste
        /// don't carry the source slot's physical-device GUIDs onto a
        /// different slot's same-variation devices.
        ///
        /// <para>Rule (per user spec): if the source's exact instance is
        /// itself assigned to the target slot, keep it. Otherwise pick the
        /// first target-slot UserSetting whose ProductGuid matches the
        /// source device's ProductGuid. Returns null when the target slot
        /// has no device of that variation — caller drops the source.</para>
        /// </summary>
        private static string RetargetDeviceGuidForSlot(string sourceDeviceGuidStr, int targetSlot)
        {
            if (string.IsNullOrEmpty(sourceDeviceGuidStr)) return sourceDeviceGuidStr;
            if (!Guid.TryParse(sourceDeviceGuidStr, out var sourceInstanceGuid))
                return sourceDeviceGuidStr;

            var sourceDevice = SettingsManager.FindDeviceByInstanceGuid(sourceInstanceGuid);
            if (sourceDevice == null) return null;
            var sourceProductGuid = sourceDevice.ProductGuid;
            if (sourceProductGuid == Guid.Empty) return null;

            bool sourceIsOnTargetSlot = false;
            Guid firstOtherSameProductOnTarget = Guid.Empty;

            var settings = SettingsManager.UserSettings;
            if (settings?.Items == null) return null;
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    if (us == null || us.MapTo != targetSlot) continue;
                    if (us.ProductGuid != sourceProductGuid) continue;

                    if (us.InstanceGuid == sourceInstanceGuid)
                        sourceIsOnTargetSlot = true;
                    else if (firstOtherSameProductOnTarget == Guid.Empty)
                        firstOtherSameProductOnTarget = us.InstanceGuid;
                }
            }

            if (sourceIsOnTargetSlot)
                return sourceInstanceGuid.ToString();
            if (firstOtherSameProductOnTarget != Guid.Empty)
                return firstOtherSameProductOnTarget.ToString();
            return null;
        }

        /// <summary>Issue #61 copy helper. Builds the per-device slice
        /// of a slot's MappingSet rows: every row where the source
        /// device's GUID appears in Sources, with only those device-
        /// owned Sources retained. Other devices' contributions are
        /// stripped so a "copy from device A" snapshot describes
        /// only A's authored mappings.</summary>
        public static System.Collections.Generic.List<Engine.Data.MappingRow>
            ExtractDeviceScopedRowsForSlot(int padIndex, Guid sourceDeviceGuid)
        {
            var result = new System.Collections.Generic.List<Engine.Data.MappingRow>();
            if (padIndex < 0 || padIndex >= SettingsManager.SlotMappingSets.Length) return result;
            var ms = SettingsManager.SlotMappingSets[padIndex];
            if (ms?.Rows == null) return result;

            string srcGuid = sourceDeviceGuid.ToString().ToLowerInvariant();
            foreach (var row in ms.Rows)
            {
                if (row?.Sources == null || row.Sources.Count == 0) continue;
                var deviceSources = new System.Collections.Generic.List<Engine.Data.MappingSource>();
                foreach (var s in row.Sources)
                {
                    if (s == null) continue;
                    if (string.Equals(s.DeviceGuid ?? "", srcGuid, StringComparison.OrdinalIgnoreCase))
                        deviceSources.Add(s);
                }
                if (deviceSources.Count == 0) continue;

                result.Add(new Engine.Data.MappingRow
                {
                    Target = row.Target,
                    LayerMask = row.LayerMask ?? "Base",
                    CombineMode = row.CombineMode ?? "",
                    CombineExpression = row.CombineExpression ?? "",
                    TrimDeadzone = row.TrimDeadzone,
                    TrimRate = row.TrimRate,
                    TrimResetOnRelease = row.TrimResetOnRelease,
                    Sources = deviceSources,
                });
            }
            return result;
        }

        /// <summary>Issue #61 — "Copy From" is a SLOT-level copy: it replaces
        /// <paramref name="targetSlot"/>'s per-VC MappingSet with a deep copy
        /// of <paramref name="sourceSlot"/>'s rows. Each source's DeviceGuid
        /// is retargeted onto the target slot's same-ProductGuid (same
        /// "variation") device — see <see cref="RetargetDeviceGuidForSlot"/>
        /// for the exact rule. Carries every device's contribution, every
        /// extra source, combine modes, and Custom formulas (not just one
        /// device's slice). Sources whose product isn't represented on the
        /// target slot are dropped from the cloned row.</summary>
        public static void ReplaceSlotMappingSet(int targetSlot, int sourceSlot)
        {
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null) return;
            if (targetSlot < 0 || targetSlot >= sets.Length) return;
            if (sourceSlot < 0 || sourceSlot >= sets.Length) return;
            if (targetSlot == sourceSlot) return;

            var src = sets[sourceSlot];
            if (src == null) { sets[targetSlot] = new Engine.Data.MappingSet(); return; }

            var copy = new Engine.Data.MappingSet();
            CopyShiftActivators(src, copy, retargetSlot: targetSlot);
            if (src.Rows != null)
            {
                foreach (var r in src.Rows)
                {
                    if (r == null) continue;
                    var rc = new Engine.Data.MappingRow
                    {
                        Target = r.Target,
                        LayerMask = r.LayerMask ?? "Base",
                        CombineMode = r.CombineMode ?? "",
                        CombineExpression = r.CombineExpression ?? "",
                        NoInherit = r.NoInherit,
                        TrimDeadzone = r.TrimDeadzone,
                        TrimRate = r.TrimRate,
                        TrimResetOnRelease = r.TrimResetOnRelease,
                        Sources = new System.Collections.Generic.List<Engine.Data.MappingSource>(),
                    };
                    if (r.Sources != null)
                    {
                        foreach (var s in r.Sources)
                        {
                            if (s == null) continue;
                            var retargeted = RetargetDeviceGuidForSlot(s.DeviceGuid, targetSlot);
                            if (retargeted == null) continue;
                            var clonedSrc = s.Clone();
                            clonedSrc.DeviceGuid = retargeted;   // Clone() carries every Param* field
                            rc.Sources.Add(clonedSrc);
                        }
                    }
                    copy.Rows.Add(rc);
                }
            }
            sets[targetSlot] = copy;
        }

        /// <summary>Returns true if the given slot's MappingSet carries any
        /// row or shift activator. Use this instead of
        /// <see cref="PadSetting.HasAnyMapping"/> when deciding whether a
        /// slot is "configured" — the v3 source of truth is the per-slot
        /// MappingSet, not the legacy PadSetting descriptor fields, which
        /// reflect whichever layer the user last had visible in the UI.
        /// A slot whose user is currently viewing an empty shift layer will
        /// have empty PadSetting descriptors but still have a fully populated
        /// MappingSet on the Base layer.</summary>
        public static bool SlotHasAnyMapping(int slot)
        {
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null || slot < 0 || slot >= sets.Length) return false;
            var ms = sets[slot];
            if (ms == null) return false;
            if (ms.Rows != null)
            {
                foreach (var r in ms.Rows)
                {
                    if (r == null) continue;
                    if (r.Sources != null && r.Sources.Count > 0) return true;
                }
            }
            if (ms.ShiftActivators != null && ms.ShiftActivators.Count > 0) return true;
            return false;
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
            // PopulateAvailableInputs MUST run before LoadPadSettingToViewModel.
            // The Aim Engage InputChoice projection's getter walks
            // SlotAvailableInputs to resolve the (descriptor, deviceGuid)
            // pair into a real ComboBox entry; if the list still holds the
            // PREVIOUS device's inputs when GyroAimEngageButton gets
            // assigned, the getter returns null and the ComboBox's TwoWay
            // binding writes that null back through the setter, silently
            // clearing the freshly-loaded binding.
            if (newGuid != Guid.Empty)
            {
                PopulateAvailableInputs(padVm, FindUserDevice(newGuid));
                LoadPadSettingToViewModel(padVm, newGuid);
                _previousSelectedDevice[padVm.PadIndex] = newGuid;
            }

            // The slot's DeviceConfig anchor (PadVm.DeviceConfig)
            // just swapped to the new device's per-device entry inside
            // BindDeviceConfigForDevice. Re-attach the slot's HM
            // dispatcher so it follows the new anchor (and re-subscribes
            // its inner OnConfigChanged to the right instance).
            if (_inputManager != null && padVm.PadIndex >= 0 && padVm.PadIndex < InputManager.MaxPads)
            {
                var vcs = _inputManager.GetVirtualControllers();
                if (vcs != null && padVm.PadIndex < vcs.Length
                    && vcs[padVm.PadIndex] is HMaestroVirtualController hmVc)
                {
                    var anchor = padVm.DeviceConfig;
                    if (anchor != null)
                        hmVc.AttachDeviceConfig(anchor);
                }
            }

            // Steering is per assigned device: each source reads the selected device's live
            // StickConfigs or another device's PadSetting. Re-stamp the in-memory MappingSet
            // so the engine reflects the per-device steering immediately on selection
            // (covers startup's first selection and every dropdown switch) without waiting
            // for the next save.
            _settingsService?.PushUiExtraSourcesIntoSlotMappingSets();
        }

        /// <summary>
        /// Called when a pad's mappings are rebuilt (e.g., OutputType or Extended preset changed).
        /// Reloads mapping descriptors from the PadSetting so auto-mapped inputs are preserved.
        /// Does NOT reload deadzone / force feedback settings — those are intentionally reset
        /// by PadViewModel.ResetDeadZoneSettings() when the OutputType or Extended preset changes.
        /// </summary>
        private void OnMappingsRebuilt(object sender, EventArgs e)
        {
            if (sender is not PadViewModel padVm) return;

            // Issue #61: must go through RefreshMappingsCore (the per-VC
            // MappingSet path) so ExtraSources / CombineMode / CombineExpression
            // re-hydrate alongside the primary descriptor. A descriptor-only
            // reload would read just the SELECTED device's PadSetting fields
            // and drop secondary mappings on language change.
            RefreshMappingsCore(padVm);
            var ud = padVm.SelectedMappedDevice != null
                && padVm.SelectedMappedDevice.InstanceGuid != Guid.Empty
                ? FindUserDevice(padVm.SelectedMappedDevice.InstanceGuid) : null;
            PopulateAvailableInputs(padVm, ud);
        }

        /// <summary>Reloads every MappingItem on the slot when the user
        /// switches the nested Mappings tab to a different shift layer.
        /// RefreshMappingsCore reads <see cref="PadViewModel.ActiveLayerMask"/>
        /// to pick which rows the MappingItems mirror.</summary>
        private void OnLayerActivated(object sender, EventArgs e)
        {
            if (sender is not PadViewModel padVm) return;
            RefreshMappingsCore(padVm);
            // The cross-device AvailableInputs list is layer-INDEPENDENT (it
            // depends on the slot's assigned devices, not which shift layer is
            // active). So a layer switch only needs to re-resolve each row's
            // selected input against the EXISTING list, NOT rebuild it.
            // PopulateAvailableInputs is an O(rows x inputs) clear-and-refill of
            // every row's ObservableCollection; running it per layer activation
            // made the Mappings tab lag on every layer switch / load when a slot
            // had shift layers. The light resync below avoids that.
            ResyncMappingSelectedInputs(padVm);
        }

        /// <summary>Re-resolves every row's selected input (primary + extras)
        /// against the row's already-populated AvailableInputs list, without
        /// rebuilding that list. Used on a shift-layer switch, where the input
        /// list is unchanged but the per-row descriptors just changed.</summary>
        private static void ResyncMappingSelectedInputs(PadViewModel padVm)
        {
            if (padVm?.Mappings == null) return;
            foreach (var mapping in padVm.Mappings)
            {
                if (mapping == null) continue;
                mapping.SyncSelectedInputFromDescriptor();
                mapping.RefreshAllExtraSourceInputs();
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
        /// Issue #150: a tag was registered or removed. Rebuild every pad's input
        /// picker so the named tag appears/disappears as a bindable row, and persist
        /// the registry. Marshalled to the UI thread (the dialog raises this there;
        /// the picker rebuild and Save touch UI/VM state).
        /// </summary>
        private void OnNfcTagRegistryChanged(object sender, EventArgs e)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                // UserDevice.DeviceObjects is a cache populated ONCE at device init
                // (UserDevice.LoadFromSdlDevice). Re-read it from the NFC reader's
                // live GetDeviceObjects() so a just-registered/removed tag is present
                // before the picker rebuilds off it -- otherwise the rebuild reads a
                // stale snapshot and the new tag never appears. Snapshot under
                // UserDevices.SyncRoot, then refresh OUTSIDE the lock (UserDevices
                // before any UserSettings lock RefreshAvailableInputsForSlot may take).
                try
                {
                    lock (SettingsManager.UserDevices.SyncRoot)
                    {
                        foreach (var ud in SettingsManager.UserDevices.Items)
                            if (ud != null && ud.CapType == PadForge.Engine.InputDeviceType.Nfc && ud.Device != null)
                                ud.DeviceObjects = ud.Device.GetDeviceObjects();
                    }
                }
                catch { /* refresh is best-effort */ }

                try
                {
                    foreach (var padVm in _mainVm.Pads)
                        if (padVm != null) RefreshAvailableInputsForSlot(padVm);
                }
                catch { /* picker refresh is cosmetic */ }
                // Refresh the Devices-page tag preview if an NFC reader is selected,
                // so a just-registered/removed tag appears in the list immediately.
                try { if (_mainVm.Devices?.IsNfcDevice == true) _mainVm.Devices.RebuildNfcTags(); }
                catch { }
                try { _settingsService?.Save(); } catch { /* persisted on next save regardless */ }
            }));
        }

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

                // Player-identity idle floor (#191): freshly connected or
                // re-assigned pads pick up their controller number. Sony
                // dispatchers skip here: the per-VC re-apply loop below
                // already runs ApplyOnce for every PlayStation slot.
                ReseedPlayerIdentities(applySonyDispatchers: false);

                // Guide Button LED (#209): a freshly connected or
                // reconnected pad picks its configured brightness back up.
                // The GIP writer retries internally until the pad's
                // announce arrives, so firing in the same window as the
                // connect is safe.
                ApplyGuideLeds();

                // Re-push user-configured DS5 effects to every PlayStation
                // slot's assigned DualSense.  Catches the "DS5 disconnected
                // and reconnected mid-session" case — without this hook the
                // dispatcher only fires on PropertyChanged, so a fresh-
                // reconnected pad would sit at firmware default until the
                // user touched a slider.
                //
                // Always re-attach the slot's DeviceSlotConfig before
                // re-applying. If the inactivity timeout tore down and
                // recreated the VC while the physical pad was unplugged,
                // the new VC's dispatcher needs a fresh bind. AttachDeviceConfig
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
                                var psCfg = _inputManager._deviceSlotConfigs[i];
                                if (psCfg != null)
                                    hmVc.AttachDeviceConfig(psCfg);
                                hmVc.ReApplyUserEffects();
                            }
                        }
                        // Sony pads mapped to KBM / MIDI slots use a
                        // parallel slot-level dispatcher (owned by Step 5,
                        // not by an HM VC). Re-fire those too so the
                        // reconnected pad's lightbar / triggers / mic LED
                        // refresh in the same window as HM-side pads.
                        _inputManager.ReApplyNonHmUserEffects();

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
                            var psCfg = _inputManager._deviceSlotConfigs[i];
                            if (psCfg != null)
                                hmVc.AttachDeviceConfig(psCfg);
                            hmVc.ReApplyUserEffects();
                        }
                    }
                    _inputManager.ReApplyNonHmUserEffects();
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
                _mainVm.SetStatus(string.Format(Strings.Instance.Status_Error_Format, e.Message), persist: true);
            }));
        }

        /// <summary>
        /// Raised on the UI thread after the engine reported an HM virtual
        /// controller's inactivity timeout fired. MainWindow listens and
        /// calls <see cref="OnSlotInactivityTimedOut"/>, which destroys the
        /// VC and runs the bubble-down cascade so any surviving HM VCs at
        /// higher pad indices in the same group fall to the lowest
        /// available kernel slot. The slot configuration stays intact —
        /// only the live VC is torn down. Argument is the pad index that
        /// timed out.
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
            else if (e.PropertyName == nameof(DashboardViewModel.EnableRemoteLink))
            {
                if (_mainVm.Dashboard.EnableRemoteLink)
                    StartRemoteLinkIfEnabled();
                else
                    StopRemoteLink();
            }
            else if (e.PropertyName == nameof(DashboardViewModel.RemoteLinkPort))
            {
                if (_mainVm.Dashboard.EnableRemoteLink)
                {
                    StopRemoteLink();
                    StartRemoteLinkIfEnabled();
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
                _dispatcher.BeginInvoke(() =>
                {
                    _mainVm.Dashboard.DsuServerStatus = status;
                    // Flame truth (#175 phase 2 item 2): evaluated at dispatch
                    // time, after a failed Start has already torn the server
                    // down, so the flame tracks the lifecycle, not the checkbox.
                    _mainVm.Dashboard.IsDsuServerRunning = _dsuServer != null;
                });
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
            // Belt for the flame (#175 phase 2 item 2): Dispose raises the
            // "Stopped" status before the field is nulled, so on a non-UI
            // caller the dispatched status lambda could still read a live
            // server. This queues after it and settles the truth.
            _dispatcher.BeginInvoke(() => _mainVm.Dashboard.IsDsuServerRunning = false);
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
                        if (ps != null && (ps.AudioRumbleEnabled == "1" || ps.AudioRumbleTriggersEnabled == "1"))
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
                // per-device-bound). Walk PadViewModel.PerDeviceSlotConfigs
                // so a non-selected device's audio-mode lightbar still
                // keeps the detector alive.
                for (int i = 0; i < _mainVm.Pads.Count && !anyEnabled; i++)
                {
                    if (!SettingsManager.SlotCreated[i]) continue;
                    var pad = _mainVm.Pads[i];
                    if (pad.PerDeviceSlotConfigs == null) continue;
                    foreach (var kvp in pad.PerDeviceSlotConfigs)
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
        private void OnDeviceConfigChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.DeviceSlotConfig.LightbarMode))
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
                // Flame truth (#175 phase 2 item 2): lifecycle, not checkbox.
                _mainVm.Dashboard.IsWebControllerRunning = _webServer != null;
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
            // Events are detached before Dispose, so no "Stopped" status
            // flows through the handler; settle the flame here too (#175
            // phase 2 item 2).
            _mainVm.Dashboard.IsWebControllerRunning = false;
        }

        // ─────────────────────────────────────────────
        //  Remote Link server lifecycle (issue #138)
        // ─────────────────────────────────────────────

        private void StartRemoteLinkIfEnabled()
        {
            if (!_mainVm.Dashboard.EnableRemoteLink || _inputManager == null) return;
            if (_linkServer != null) return;

            // Loads the identity, prompting for the portable-password unlock if needed.
            var identity = EnsureIdentityUnlocked();
            if (identity == null) return;
            var trust = _settingsService?.RemoteLink?.Trust ?? new PeerTrustStore();

            if (!_remoteLinkConnectWired)
            {
                _mainVm.Dashboard.ConnectToPeerRequested += OnConnectToPeerRequested;
                _remoteLinkConnectWired = true;
            }

            _linkServer = new LinkServer(identity, trust, ApprovePairing);
            // Expose this PC's devices to inbound peers too (bidirectional sharing).
            _linkServer.ExposeProvider = () => BuildExposedDevices();
            _linkServer.StatusChanged += st => _dispatcher.BeginInvoke(() =>
            {
                _mainVm.Dashboard.RemoteLinkStatus = FormatLinkStatus(st);
                // Flame truth (#175 phase 2 item 2): a failed Start has
                // already nulled the field by the time this dispatches, and
                // peer-level errors (timeout, rejected) keep a listening
                // server lit. Lifecycle, not the checkbox or the text.
                _mainVm.Dashboard.IsRemoteLinkRunning = _linkServer != null;
            });
            // Reverse output relay (#138): our game's output for a remote device is
            // shipped to its owner; a peer's output for OUR device drives our hardware.
            RemoteLinkOutputRouter.SendOutput = (fp, slot, payload) => _linkServer?.PushOutputEffect(fp, slot, payload);
            RemoteLinkOutputRouter.SendAudio = (fp, slot, payload) => _linkServer?.PushAudio(fp, slot, payload);
            _linkServer.OutputReceived += OnRemoteOutputReceived;
            _linkServer.AudioReceived += OnRemoteAudioReceived;
            _linkServer.DeviceConnected += device =>
            {
                // Mark the restriction BEFORE the device goes online, or there is a
                // window where its frames stream while IsSlotRestricted is still false.
                bool restricted = _settingsService?.RemoteLink?.Trust?.Peers?
                    .Any(t => string.Equals(t.FingerprintHex, device.Info.PeerFingerprintHex, StringComparison.OrdinalIgnoreCase) && t.GamepadOnly) ?? false;
                _inputManager.SetDeviceRestricted(device.InstanceGuid, restricted);
                _inputManager.RegisterPeerDevice(device);
                // Map this device's "peer://" path to its owner so every output
                // chokepoint can ship its config-baked output back (issue #138).
                RemoteLinkOutputRouter.Register(device.DevicePath, device.Info.PeerFingerprintHex, device.LinkSlot);
                // A remote pad connects AFTER the startup audio-reconcile check, so its
                // speaker-passthrough config is never seen. Kick the audio service now so
                // the worker starts and evaluates this peer:// pad as a ship target (#138).
                PadForge.Common.Input.AudioPassthroughService.Reconcile();
                // Persist a freshly granted peer + refresh the manager list (the grant
                // happened inside the handshake, just before this fires).
                _dispatcher.BeginInvoke(() =>
                {
                    try { _settingsService?.Save(); } catch { }
                    _mainVm.Settings.RefreshTrustedPeers(_settingsService?.RemoteLink?.Trust?.Peers, _linkServer?.ConnectedFingerprints());
                });
                OnLinkPeersChanged(); // refresh Nearby PCs so this peer reads "Connected"
            };
            _linkServer.DeviceDisconnected += device =>
            {
                _inputManager.SetDeviceRestricted(device.InstanceGuid, false);
                _inputManager.UnregisterExternalDevice(device.InstanceGuid);
                RemoteLinkOutputRouter.Unregister(device.DevicePath);
                OnLinkPeersChanged();
            };

            int port = _mainVm.Dashboard.RemoteLinkPort;
            if (port < 1024 || port > 65535) port = 27500;
            if (!_linkServer.Start(port))
            {
                // Port unavailable: tear down so the _linkServer != null re-entry guard clears
                // and a later enable can retry on a free port (the Web/DSU server idiom) (#138 F05).
                _linkServer.Dispose();
                _linkServer = null;
                return;
            }

            // LAN auto-discovery so peers appear by name — no IP typing.
            _linkDiscovery = new LinkDiscovery();
            _linkDiscovery.PeersChanged += OnLinkPeersChanged;
            _linkDiscovery.Start(port, Environment.MachineName, identity.FingerprintHex);

            // Stream exposed devices at ~125 Hz off the hot path.
            _remoteLinkStreamTimer?.Dispose();
            _remoteLinkStreamTimer = new System.Threading.Timer(RemoteLinkStreamTick, null, 8, 8);

            // Live device sync (#138): every 2s re-publish OUR current shareable set to peers
            // so a controller plugged in after connect appears (and one unplugged disappears)
            // on the other end, and keep the peer-manager online dots live. Cheap; once per 2 s.
            _remoteLinkSyncTimer?.Dispose();
            _remoteLinkSyncTimer = new System.Threading.Timer(_ =>
            {
                var s = _linkServer;
                if (s == null) return;
                // BuildExposedDevices rebuilds the stream snapshot too, so a new device starts streaming.
                try { s.PushDeviceList(BuildExposedDevices()); } catch { }
                // Connect/disconnect doesn't always raise a discovery change; refresh on the UI thread.
                var fps = s.ConnectedFingerprints();
                _dispatcher.BeginInvoke(() => _mainVm.Settings.UpdatePeerOnlineStatus(fps));
            }, null, 2000, 2000);
        }

        private void OnLinkPeersChanged()
        {
            var disc = _linkDiscovery;
            if (disc == null) return;
            var peers = disc.Peers;
            var trust = _settingsService?.RemoteLink?.Trust;
            var connectedFps = _linkServer?.ConnectedFingerprints() ?? (IReadOnlyList<string>)System.Array.Empty<string>();

            // Auto-reconnect (#138): when a paired PC appears on the LAN and we aren't linked,
            // dial it automatically — no click. Only ONE side initiates (the lower fingerprint)
            // so two PCs that both see each other don't both connect; the other just listens.
            // Per-peer cooldown keeps a failing dial from spamming every discovery tick.
            if (_mainVm.Dashboard.AutoReconnect && _remoteLinkIdentity != null)
            {
                string myFp = _remoteLinkIdentity.FingerprintHex;
                long now = Environment.TickCount64;
                foreach (var p in peers)
                {
                    var entry = trust?.Peers?.FirstOrDefault(t => string.Equals(t.FingerprintHex, p.FingerprintHex, StringComparison.OrdinalIgnoreCase));
                    if (entry == null || !entry.ReconnectEnabled) continue;                                  // not a trusted auto-reconnect peer
                    if (connectedFps.Any(f => string.Equals(f, p.FingerprintHex, StringComparison.OrdinalIgnoreCase))) continue; // already linked
                    if (string.CompareOrdinal(myFp, p.FingerprintHex) >= 0) continue;                        // the lower fingerprint dials; the other listens
                    if (_autoReconnectCooldown.TryGetValue(p.FingerprintHex, out var last) && now - last < AutoReconnectCooldownMs) continue;
                    _autoReconnectCooldown[p.FingerprintHex] = now;
                    // Marshal the dial to the UI thread: OnConnectToPeerRequested mutates VM
                    // state and starts the server, while OnLinkPeersChanged runs on background
                    // (discovery / UDP / reaper / handshake) threads (#138 F09).
                    _dispatcher.BeginInvoke(() => OnConnectToPeerRequested($"{p.Endpoint.Address}:{p.Endpoint.Port}"));
                }
            }

            // Keep each paired peer's machine (NetBIOS) name current from discovery (#138):
            // it's the default friendly name and the always-shown host label. Persist + a
            // one-time list refresh only when it actually changes (not every 2 s tick).
            bool trustDirty = false;
            if (trust?.Peers != null)
            {
                foreach (var p in peers)
                {
                    if (string.IsNullOrWhiteSpace(p.Name)) continue;
                    var e = trust.Peers.FirstOrDefault(t => string.Equals(t.FingerprintHex, p.FingerprintHex, StringComparison.OrdinalIgnoreCase));
                    if (e != null && !string.Equals(e.HostName, p.Name, StringComparison.Ordinal)) { e.HostName = p.Name; trustDirty = true; }
                }
            }

            _dispatcher.BeginInvoke(() =>
            {
                var nearbyUnpaired = new System.Collections.Generic.List<ViewModels.RemoteLinkNearbyPeer>();
                // fingerprint -> host:port for paired peers seen on the LAN, so the paired
                // list can show a Connect button on a discovered-but-offline peer.
                var reachable = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in peers)
                {
                    bool paired = trust?.Peers?.Any(t => string.Equals(t.FingerprintHex, p.FingerprintHex, StringComparison.OrdinalIgnoreCase)) ?? false;
                    bool connected = connectedFps.Any(f => string.Equals(f, p.FingerprintHex, StringComparison.OrdinalIgnoreCase));
                    string hostPort = $"{p.Endpoint.Address}:{p.Endpoint.Port}";
                    if (paired)
                        reachable[p.FingerprintHex] = hostPort;
                    else
                        // Not yet paired — list it under the paired manager with a Pair button.
                        nearbyUnpaired.Add(new ViewModels.RemoteLinkNearbyPeer(p.Name, hostPort, p.FingerprintHex, false, connected, OnConnectToPeerRequested));
                }
                _mainVm.Settings.SetNearbyUnpaired(nearbyUnpaired);
                _mainVm.Settings.UpdatePeerOnlineStatus(connectedFps);
                _mainVm.Settings.UpdatePeerReachability(reachable);
                // A host name just changed -> persist it + rebuild so the new name + default
                // show. The Save runs here on the UI thread, not on the background discovery
                // thread (an off-thread Save races the UI-thread save and reads VM
                // collections off-thread) (#138 F09).
                if (trustDirty)
                {
                    try { _settingsService?.Save(); } catch { }
                    _mainVm.Settings.RefreshTrustedPeers(trust?.Peers, connectedFps);
                }
            });
        }

        private void StopRemoteLink()
        {
            _remoteLinkStreamTimer?.Dispose();
            _remoteLinkStreamTimer = null;
            _remoteLinkSyncTimer?.Dispose();
            _remoteLinkSyncTimer = null;
            lock (_remoteLinkExposedLock)
            {
                _remoteLinkExposed.Clear();
                _remoteLinkExposedSnapshot = System.Array.Empty<(RemotePeerDeviceInfo, ISdlInputDevice, UserDevice, RemoteDeltaAccumulator, byte)>();
                _remoteDeltaAccs.Clear();
            }
            RemoteLinkOutputRouter.SendOutput = null;
            RemoteLinkOutputRouter.SendAudio = null;
            RemoteLinkOutputRouter.Clear();
            _remoteWheelOneShot.Clear();
            if (_linkDiscovery != null)
            {
                _linkDiscovery.PeersChanged -= OnLinkPeersChanged;
                _linkDiscovery.Dispose();
                _linkDiscovery = null;
            }
            _dispatcher.BeginInvoke(() =>
            {
                _mainVm.Settings.SetNearbyUnpaired(null);
                _mainVm.Settings.UpdatePeerOnlineStatus(System.Array.Empty<string>());
            });
            if (_linkServer == null) return;
            _linkServer.Dispose();
            _linkServer = null;
            _mainVm.Dashboard.RemoteLinkStatus = Strings.Instance.Common_Stopped;
            // Same belt as StopDsuServer: Dispose's "Stopped" status lambda
            // may have captured a still-live field (#175 phase 2 item 2).
            _dispatcher.BeginInvoke(() => _mainVm.Dashboard.IsRemoteLinkRunning = false);
        }

        /// <summary>Map an engine LinkServer status CODE to a localized dashboard string. The
        /// engine can't reach the App's resources, so it emits a code and the App localizes (#138 F35).</summary>
        private static string FormatLinkStatus(LinkServer.LinkStatus st)
        {
            var S = Strings.Instance;
            switch (st.Kind)
            {
                case LinkServer.LinkStatusKind.Listening:     return string.Format(S.RemoteLink_StatusListening, st.Port);
                case LinkServer.LinkStatusKind.Stopped:       return S.Common_Stopped;
                case LinkServer.LinkStatusKind.StartFailed:   return string.Format(S.RemoteLink_StatusStartFailed, st.Message);
                case LinkServer.LinkStatusKind.PeerTimedOut:  return string.Format(S.RemoteLink_StatusPeerTimedOut, st.Peer);
                case LinkServer.LinkStatusKind.ConnectFailed: return string.Format(S.RemoteLink_StatusConnectFailed, st.Message);
                case LinkServer.LinkStatusKind.LinkRejected:  return string.Format(S.RemoteLink_StatusLinkRejected, st.Message);
                case LinkServer.LinkStatusKind.PeerConnected: return string.Format(S.RemoteLink_StatusPeerConnected, st.Peer, st.DeviceCount);
                default:                                      return "";
            }
        }

        /// <summary>Load the persisted Remote Link identity, minting + saving one on first use.</summary>
        private PeerIdentity EnsureRemoteLinkIdentity()
        {
            var holder = _settingsService?.RemoteLink;
            if (holder == null) return PeerIdentity.Generate();

            // Password for a portable-password identity is collected by the UI and held for
            // the session; null in Secure / open modes (and until the prompt lands).
            string password = _remoteLinkSessionPassword;
            var status = IdentityProtector.LoadOrMint(
                holder.ProtectedPrivateBase64, holder.PublicBase64, password,
                holder.IdentityProtection, password,
                out var identity, out var newPriv, out var newPub);

            if (status == IdentityUnprotect.Minted)
            {
                holder.ProtectedPrivateBase64 = newPriv;
                holder.PublicBase64 = newPub;
                _settingsService.Save();
                _remoteLinkIdentity = identity;
                return identity;
            }
            if (status == IdentityUnprotect.Ok)
            {
                // The stored public was blank/garbage and got re-derived from the private:
                // persist the heal so it isn't recomputed every launch (#138 F26).
                if (newPub != null) { holder.PublicBase64 = newPub; try { _settingsService.Save(); } catch { } }
                _remoteLinkIdentity = identity;
                return identity;
            }

            // Locked: a real identity exists but can't be opened here. NEVER overwrite it —
            // surface why so the user can fix it (the right machine, or the password), and
            // leave Remote Link off until then.
            string msg = status switch
            {
                IdentityUnprotect.WrongMachine => Strings.Instance.RemoteLink_StatusWrongMachine,
                IdentityUnprotect.NeedsPassword => Strings.Instance.RemoteLink_PasswordUnlockPrompt,
                IdentityUnprotect.WrongPassword => Strings.Instance.RemoteLink_StatusWrongPassword,
                _ => Strings.Instance.RemoteLink_StatusIdentityUnavailable,
            };
            _dispatcher.BeginInvoke(() => _mainVm.Dashboard.RemoteLinkStatus = msg);
            return null;
        }

        /// <summary>Portable-password identity unlock for this session (issue #138). Set by
        /// the UI password prompt; never persisted. Null in Secure / open modes.</summary>
        private string _remoteLinkSessionPassword;

        /// <summary>The live unlocked identity, cached so a mode switch can re-wrap the same
        /// key (preserving the fingerprint) without reloading. Null until first loaded.</summary>
        private PeerIdentity _remoteLinkIdentity;

        /// <summary>Show the SAS pairing dialog on the UI thread and block the socket
        /// thread until the user decides. First contact only; trusted peers reconnect
        /// without prompting.</summary>
        private PairingApproval ApprovePairing(PendingPairing pending)
        {
            (bool approved, bool gamepadOnly) r;
            // CRITICAL: if the handshake resumed on the UI thread (an outbound pair
            // started from a button captures the WPF SynchronizationContext), a
            // BeginInvoke+Wait would deadlock the UI thread against itself and the
            // dialog could never show. Show it directly when already on the UI
            // thread; only marshal+block from a background (socket) thread.
            if (_dispatcher.CheckAccess())
            {
                r = ShowPairDialog(pending);
            }
            else
            {
                (bool approved, bool gamepadOnly) captured = (false, false);
                var done = new System.Threading.ManualResetEventSlim(false);
                _dispatcher.BeginInvoke(() =>
                {
                    try { captured = ShowPairDialog(pending); }
                    // Guard: on a wait timeout below we deliberately leak `done`
                    // rather than dispose it, so this late Set (when the user finally
                    // dismisses the orphaned dialog) is a harmless no-op instead of an
                    // ObjectDisposedException on the UI thread.
                    finally { try { done.Set(); } catch (ObjectDisposedException) { } }
                });
                if (!done.Wait(TimeSpan.FromMinutes(2)))
                    return false;   // do NOT dispose: the queued delegate still holds it
                done.Dispose();     // delegate has signalled and completed; safe now
                r = captured;
            }
            // Persistence happens in DeviceConnected, after the grant lands in the trust store.
            return new PairingApproval { Approved = r.approved, GamepadOnly = r.gamepadOnly };
        }

        private (bool approved, bool gamepadOnly) ShowPairDialog(PendingPairing pending)
        {
            try
            {
                var dlg = new Views.RemoteLinkPairDialog(pending.Sas, pending.PeerFingerprintHex);
                var owner = System.Windows.Application.Current?.MainWindow;
                if (owner != null && owner.IsLoaded) dlg.Owner = owner;
                bool ok = dlg.ShowDialog() == true;
                return (ok, dlg.GamepadOnly);
            }
            catch { return (false, false); }
        }

        private async void OnConnectToPeerRequested(string hostPort)
        {
            if (_linkServer == null)
            {
                _mainVm.Dashboard.EnableRemoteLink = true;
                StartRemoteLinkIfEnabled();
            }
            var server = _linkServer;
            if (server == null || string.IsNullOrWhiteSpace(hostPort)) return;

            string host = hostPort.Trim();
            int port = _mainVm.Dashboard.RemoteLinkPort;
            int colon = host.LastIndexOf(':');
            if (colon > 0 && int.TryParse(host.Substring(colon + 1), out int p)) { host = host.Substring(0, colon); port = p; }

            var expose = BuildExposedDevices();
            _ = _dispatcher.BeginInvoke(() => _mainVm.Dashboard.RemoteLinkStatus = string.Format(Strings.Instance.RemoteLink_StatusConnecting, host, port));
            await server.ConnectAsync(host, port, expose);
        }

        /// <summary>Build descriptors for every online physical controller this PC
        /// shares, and remember their live sources for the stream timer. The slot id
        /// is the list index, matching the order the peer rebuilds the devices in.</summary>
        private IReadOnlyList<RemotePeerDeviceInfo> BuildExposedDevices()
        {
            var list = new List<RemotePeerDeviceInfo>();
            var sources = new List<(RemotePeerDeviceInfo info, ISdlInputDevice source, UserDevice ud, RemoteDeltaAccumulator acc, byte slot)>();

            // Hold the exposed lock around stable-slot allocation + the snapshot update so
            // concurrent calls (handshake / periodic push) agree on slots. Lock order is
            // always exposed-lock -> device SyncRoot (never the reverse), so no deadlock.
            lock (_remoteLinkExposedLock)
            {
                var seenIds = new HashSet<string>();
                var used = new HashSet<byte>(_exposedSlots.Values);
                var devices = SettingsManager.UserDevices;
                if (devices != null)
                {
                    lock (devices.SyncRoot)
                    {
                        foreach (var ud in devices.Items)
                        {
                            var dev = ud?.Device;
                            if (ud == null || dev == null || !ud.IsOnline) continue;
                            if (!IsShareableDevice(dev)) continue;
                            if (list.Count >= 250) break; // slot id is a byte

                            string id = ud.InstanceGuid.ToString("N");
                            seenIds.Add(id);
                            if (!_exposedSlots.TryGetValue(id, out byte slot))
                            {
                                slot = 0; while (used.Contains(slot)) slot++; // lowest free slot
                                _exposedSlots[id] = slot; used.Add(slot);
                            }
                            // Forward named inputs ONLY for device types whose
                            // names the consumer cannot derive locally: consumer
                            // usages (per-session dynamic slots) and NFC tags
                            // (named from THIS machine's tag registry). Gamepad /
                            // joystick / keyboard / mouse / MIDI shapes are
                            // synthesized identically on the consumer, and the
                            // periodic device list rides ONE UDP datagram into a
                            // 4 KB receive buffer on old peers, so shipping every
                            // device's objects could push past it and silently
                            // kill device sync (audit F1).
                            int devType = dev.GetInputDeviceType();
                            DeviceObjectItem[] objects = null;
                            if (devType == InputDeviceType.ConsumerControl || devType == InputDeviceType.Nfc)
                                try { objects = dev.GetDeviceObjects(); } catch { }
                            var info = new RemotePeerDeviceInfo
                            {
                                Slot = slot,
                                Online = true,
                                PeerLocalDeviceId = id,
                                Name = dev.Name,
                                VendorId = dev.VendorId,
                                ProductId = dev.ProductId,
                                SerialNumber = dev.SerialNumber ?? "",
                                NumAxes = dev.NumAxes,
                                NumButtons = dev.NumButtons,
                                RawButtonCount = dev.RawButtonCount,
                                NumHats = dev.NumHats,
                                HasRumble = dev.HasRumble,
                                HasRumbleTriggers = dev.HasRumbleTriggers,
                                HasHaptic = dev.HasHaptic,
                                HasGyro = dev.HasGyro,
                                HasAccel = dev.HasAccel,
                                // Aux (left-side) accelerometer (#199/#208): the
                                // device-list wire already reserves bit 128 and the
                                // consumer's picker gates 'Motion Accel L' on it, but
                                // the flag was never populated here, so it shipped
                                // dead (always false) and the source stayed un-pickable.
                                HasAccelAux = dev.HasAccelAux,
                                HasTouchpad = dev.HasTouchpad,
                                NumTouchpads = dev.NumTouchpads,
                                TouchpadFingerCounts = dev.TouchpadFingerCounts,
                                InputDeviceType = dev.GetInputDeviceType(),
                                // Forward the owner's named inputs so the peer's
                                // mapping picker and Devices preview read identically
                                // to local (consumer-control names, wheel axes, NFC
                                // tag buttons). The periodic device-list push refreshes
                                // this as dynamic slots appear.
                                DeviceObjects = objects,
                            };
                            list.Add(info);
                            // Reuse the device's delta accumulator across
                            // rebuilds so pending per-poll deltas survive the
                            // 2 s device-list refresh.
                            if (!_remoteDeltaAccs.TryGetValue(ud.InstanceGuid, out var acc))
                            {
                                acc = new RemoteDeltaAccumulator();
                                _remoteDeltaAccs[ud.InstanceGuid] = acc;
                            }
                            sources.Add((info, dev, ud, acc, slot));
                        }
                    }
                }
                // Release slots held by devices that are no longer shared, so they can be
                // reused — and the consumer drops them on the next sync.
                foreach (var goneId in _exposedSlots.Keys.Where(k => !seenIds.Contains(k)).ToList())
                {
                    _exposedSlots.Remove(goneId);
                    // Also drop the owner-side wheel one-shot cache so range / autocenter / RPM
                    // LEDs re-apply from scratch on replug. The wheel power-cycles to factory
                    // defaults while gone, and the cached tuple would suppress the re-send (#138 F38).
                    if (Guid.TryParseExact(goneId, "N", out var goneGuid))
                    {
                        _remoteWheelOneShot.TryRemove(goneGuid, out _);
                        _remoteDeltaAccs.Remove(goneGuid);
                    }
                }

                _remoteLinkExposed.Clear();
                _remoteLinkExposed.AddRange(sources);
                _remoteLinkExposedSnapshot = sources.ToArray();
            }
            return list;
        }

        /// <summary>Share every device the user sees in Devices — gamepads, joysticks,
        /// wheels, keyboards, mice, MIDI, web, overlay. The ONE exclusion is a device
        /// that is itself a remote peer's (peer://): re-sharing it would loop / relay.</summary>
        private static bool IsShareableDevice(ISdlInputDevice dev)
        {
            string path = dev.DevicePath ?? "";
            return !path.StartsWith("peer://", StringComparison.Ordinal);
        }

        private int _streamTickGuard;

        /// <summary>Poll-thread half of the Remote Link delta bridge, invoked
        /// once per polling tick via <see cref="InputManager.RemoteLinkPollTick"/>
        /// right after Step 2 publishes fresh snapshots. Folds each exposed
        /// device's per-poll delta fields into its accumulator; the stream
        /// tick below drains them. Lock-free walk over the exposed snapshot
        /// array (the accumulator serializes internally).</summary>
        private void RemoteLinkPollAccumulate()
        {
            var exposed = _remoteLinkExposedSnapshot;
            for (int i = 0; i < exposed.Length; i++)
            {
                var e = exposed[i];
                var s = e.ud?.InputState;
                if (s == null) continue;
                try { e.acc.Accumulate(s, e.source is PadForge.Engine.SdlMouseWrapper); }
                catch { /* teardown race: never disturb the poll loop */ }
            }
        }

        private void RemoteLinkStreamTick(object state)
        {
            // Non-reentrant: a tick slower than the 8 ms period must not overlap the
            // next one, or two concurrent Seal calls could race the send counter.
            if (System.Threading.Interlocked.Exchange(ref _streamTickGuard, 1) == 1) return;
            try
            {
                var server = _linkServer;
                if (server == null) return;
                var exposed = _remoteLinkExposedSnapshot;
                if (exposed.Length == 0) return;

                ulong ts = (ulong)(System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
                foreach (var e in exposed)
                {
                    // Ship the poll thread's published snapshot, NEVER
                    // source.GetCurrentState(): the mouse wrapper's
                    // consume-and-zero delta read and the Joy-Con 2 baseline
                    // advance are destructive, so a second caller here split
                    // the motion between local mapping and the peer. Clone
                    // before touching the delta fields: the published
                    // instance is read by the UI and Step 3 and must not be
                    // mutated (Step 2's swap contract).
                    var snap = e.ud?.InputState;
                    if (snap == null || !e.ud.IsOnline) continue;
                    var s = snap.Clone();
                    e.acc.DrainInto(s, e.source is PadForge.Engine.SdlMouseWrapper);
                    var caps = new CustomInputStateCodec.Caps(e.source.HasGyro, e.source.HasAccel, e.source.HasAccelAux);
                    server.PushLocalFrame(e.slot, s, caps, ts);
                }
            }
            finally { System.Threading.Volatile.Write(ref _streamTickGuard, 0); }
        }

        /// <summary>Rebuild the consumer-side reverse-output routes (#138 M2): VC pad
        /// slot -&gt; the remote targets (owner fingerprint + link slot) whose devices are
        /// mapped to that slot. Called on peer connect/disconnect and on remap, so the
        /// capture taps in HMaestroVirtualController forward to the right owner.</summary>
        // Owner-side neutral PadSetting for replaying a relayed Vibration: the consumer
        // already baked every gain in, so the owner must not re-scale (ForceOverall=100).
        private static readonly PadSetting _remoteApplyPs = new PadSetting { ForceOverall = "100" };
        // Owner-side wheel one-shot dedup (range/autocenter/LEDs are in every frame but
        // change rarely; re-writing them per frame would halve the wheel poll rate).
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (int range, int ac, int ledMask, bool ledValid)> _remoteWheelOneShot = new();
        /// <summary>Resolve a link slot to the owner's physical source + UserDevice.</summary>
        private bool ResolveExposed(byte slot, out ISdlInputDevice source, out UserDevice ud)
        {
            source = null; ud = null;
            lock (_remoteLinkExposedLock)
                foreach (var e in _remoteLinkExposed)
                    if (e.slot == slot) { source = e.source; break; }
            if (source == null) return false;
            ud = SettingsManager.FindDeviceByInstanceGuid(source.InstanceGuid);
            return true;
        }

        /// <summary>A paired peer sent reverse output for one of THIS PC's shared devices
        /// (issue #138). Map the link slot to the physical source and drive the hardware
        /// directly — no local game / virtual controller is involved. The consumer baked
        /// in all config; the owner only re-encodes for its real device. Runs on the UDP
        /// receive thread (one writer).</summary>
        private void OnRemoteOutputReceived(string peerFingerprint, byte slot, byte[] payload)
        {
            if (!OutputEffectCodec.TryDecode(payload, out var effect))
                return;
            if (!ResolveExposed(slot, out var source, out var ud))
                return;
            // Sole-writer guard (#138): this frame means a remote game is driving the
            // shared device. Refresh the output lease so the owner's LOCAL output pipeline
            // yields. The apply below is the sole hardware writer (no two-writer stutter).
            // PlayerIndex and GuideLed are exempt: both are cosmetic LED updates, not
            // effect claims, so they must not blank a locally-mapped device's
            // rumble/lightbar for OutputLeaseMs.
            if (effect.Kind != OutputEffectCodec.Kind.PlayerIndex
                && effect.Kind != OutputEffectCodec.Kind.GuideLed)
                RemoteLinkOutputRouter.ClaimOutput(ud?.DevicePath ?? source.DevicePath);
            try
            {
                var handle = source.GamepadHandle;
                switch (effect.Kind)
                {
                    case OutputEffectCodec.Kind.SonyEffect:
                        // Replay the DualSense effect body through SDL, which re-frames it
                        // for the physical pad's transport (USB 0x02 / BT 0x31 + CRC).
                        if (handle != IntPtr.Zero && effect.SonyBody != null && effect.SonyBody.Length > 0)
                            SDL3.SDL.SDL_SendGamepadEffect(handle, effect.SonyBody, 0, effect.SonyBody.Length);
                        break;

                    case OutputEffectCodec.Kind.Vibration:
                        // Rumble + impulse triggers + directional/condition haptic, replayed
                        // through the device's real SDL handle per its capabilities. A Fanatec
                        // pedal has no usable SDL rumble, so re-route it through the raw-HID
                        // pedal writer (as the local path does) instead of dropping it (#138 F31).
                        if (FanatecRawHidWriter.IsFanatecPedal(source.VendorId, source.ProductId))
                        {
                            byte brake    = (byte)(effect.Vibration.LeftMotorSpeed >> 8);  // XInput left  -> brake
                            byte throttle = (byte)(effect.Vibration.RightMotorSpeed >> 8); // XInput right -> throttle
                            FanatecRawHidWriter.WritePedalRumble(source.DevicePath, throttle, brake);
                        }
                        else if (ud != null
                            && PadForge.Engine.XboxControllerIdentity.IsImpulseTriggerDevice(ud.VendorId, ud.ProdId))
                        {
                            // Xbox One+ sole-writer path (mirrors InputManager.Step2's
                            // isXboxImpulse gate): write all FOUR channels (large/small +
                            // LT/RT) via raw HID and NEVER call SDL rumble, or the impulse
                            // triggers never fire and the family's sole-writer contract
                            // breaks. The consumer baked every gain in, so write verbatim.
                            var vib = effect.Vibration;
                            if (ud.ForceFeedbackState != null
                                && ud.ForceFeedbackState.TryRecordXboxImpulseSnapshot(
                                    vib.LeftMotorSpeed, vib.RightMotorSpeed,
                                    vib.LeftTriggerMotorSpeed, vib.RightTriggerMotorSpeed))
                            {
                                PadForge.Common.Input.XboxImpulseHidWriter.Write(
                                    ud, vib.LeftMotorSpeed, vib.RightMotorSpeed,
                                    vib.LeftTriggerMotorSpeed, vib.RightTriggerMotorSpeed);
                            }
                        }
                        else
                        {
                            ud?.ForceFeedbackState?.SetDeviceForces(ud, source, _remoteApplyPs, effect.Vibration);
                        }
                        break;

                    case OutputEffectCodec.Kind.Wheel:
                        ApplyRemoteWheel(source, in effect.Wheel);
                        break;

                    case OutputEffectCodec.Kind.HapticTone:
                        // #147 over the link: the consumer reduced its macro mix
                        // to a (freq, amp) pair; re-encode with the owner's own
                        // per-family tone writer (Joy-Con / Steam / Triton / Deck).
                        HapticToneService.ApplyRemoteTone(ud, effect.HapticToneHz, effect.HapticToneAmp);
                        break;

                    case OutputEffectCodec.Kind.PlayerIndex:
                    {
                        // #191 over the link: light the owner's real pad from the
                        // consumer slot's number. Nintendo -> SDL player index (the
                        // wrapper allowlists 057E); BT DS3 -> the direct service, reached
                        // by SDL instance id (same match as UpdateDs3PlayerNumber).
                        int n = effect.PlayerIndex;
                        if (source.VendorId == 0x057E)
                        {
                            if (source is PadForge.Engine.SdlDeviceWrapper w2 && n > 0)
                                w2.SetPlayerIndex(n - 1);
                        }
                        else if (source.VendorId == 0x054C && source.ProductId == 0x0268)
                        {
                            PadForge.Common.Input.Ds3DirectService.TrySetPlayerNumber(source.SdlInstanceId, n);
                        }
                        break;
                    }

                    case OutputEffectCodec.Kind.GuideLed:
                    {
                        // #209 over the link: light the owner's real pad's Guide/Home
                        // LED from the consumer slot's configured percent. SAME writers
                        // and SAME gate order as the local ApplyGuideLeds path, so a
                        // relayed device behaves identically to a locally-mapped one.
                        if (ud != null)
                        {
                            int pct = effect.GuideLedPercent;
                            if (PadForge.Common.Input.XboxGipGuideLedWriter.IsXboxGipPathed(ud))
                                PadForge.Common.Input.XboxGipGuideLedWriter.Instance.TrySetBrightness(ud, pct);
                            else if (PadForge.Common.Input.SteamHomeLedSetter.IsSteamController2015(ud.VendorId, ud.ProdId))
                                PadForge.Common.Input.SteamHomeLedSetter.TrySet(pct);
                        }
                        break;
                    }
                }
            }
            catch { /* a malformed frame must never take down the receive thread */ }
        }

        /// <summary>Re-encode a relayed wheel frame with the owner's own per-vendor writer
        /// (the vendor PID quantization + report sizing + stateful upload/play caches must
        /// live on the machine that owns the wheel).</summary>
        private void ApplyRemoteWheel(ISdlInputDevice source, in OutputEffectCodec.WheelFrame w)
        {
            string path = source.DevicePath;
            ushort vid = source.VendorId, pid = source.ProductId;
            bool isLogi = LogitechRawHidWriter.IsLogitechWheel(vid, pid);
            bool isFan  = FanatecRawHidWriter.IsFanatecWheel(vid, pid);
            bool isTm   = ThrustmasterRawHidWriter.IsThrustmasterWheel(vid, pid);
            if (!isLogi && !isFan && !isTm) return; // generic SDL wheel would ride Vibration

            // Per-frame FFB (force / condition / periodic).
            if (isLogi)
            {
                if (w.HasCond)
                    LogitechRawHidWriter.WriteCondition(path, 0, w.Effect, w.Pc, w.Nc, w.Off, w.Db, w.Ps, w.Ns, w.CondGain, LogitechRawHidWriter.HasFrictionCap(pid));
                else if (w.Force == 0) LogitechRawHidWriter.WriteStopEffect(path, 0);
                else LogitechRawHidWriter.WriteConstantForce(path, 0, w.Force);
            }
            else if (isFan)
            {
                if (w.HasCond)
                    FanatecRawHidWriter.WriteWheelCondition(path, w.Effect, w.Pc, w.Nc, w.Off, w.Db, w.Ps, w.Ns, w.CondGain);
                else
                {
                    FanatecRawHidWriter.WriteWheelConstantForce(path, w.Force, pid);
                    if (w.Ac > 0) FanatecRawHidWriter.WriteAutocenter(path, w.Ac);
                }
            }
            else // Thrustmaster
            {
                if (w.HasCond)
                    ThrustmasterRawHidWriter.WriteCondition(path, w.Effect, w.Pc, w.Nc, w.Off, w.Db, w.Ps, w.Ns, w.CondGain);
                else if (w.Peak != 0)
                    ThrustmasterRawHidWriter.WritePeriodic(path, w.Effect, w.Peak, w.Period);
                else ThrustmasterRawHidWriter.WriteConstantForce(path, w.Force);
            }

            // Range + auto-center + RPM LEDs — re-send only when changed.
            var prev = _remoteWheelOneShot.TryGetValue(source.InstanceGuid, out var p) ? p : (-1, -1, -1, false);
            if (prev.Item1 != w.RangeDeg || prev.Item2 != w.Ac)
            {
                int acMag = w.Ac;
                if (isLogi) { LogitechRawHidWriter.WriteRange(path, w.RangeDeg, pid); LogitechRawHidWriter.WriteAutocenter(path, acMag, LogitechRawHidWriter.IsMomo(pid)); }
                else if (isFan) { FanatecRawHidWriter.WriteRange(path, w.RangeDeg, pid); FanatecRawHidWriter.WriteAutocenter(path, acMag); }
                else { ThrustmasterRawHidWriter.WriteRange(path, w.RangeDeg, pid); ThrustmasterRawHidWriter.WriteAutocenter(path, acMag); }
            }
            if (prev.Item3 != w.LedMask || prev.Item4 != w.LedValid)
            {
                int mask = w.LedValid ? w.LedMask : 0;
                if (isLogi) LogitechRawHidWriter.WriteRpmLeds(path, (byte)mask);
                else if (isFan) FanatecRawHidWriter.WriteRpmLeds(path, mask);
                else ThrustmasterRawHidWriter.WriteRpmLeds(path, mask);
            }
            _remoteWheelOneShot[source.InstanceGuid] = (w.RangeDeg, w.Ac, w.LedMask, w.LedValid);
        }

        /// <summary>A paired peer sent a speaker PCM block for one of THIS PC's shared
        /// pads (issue #138). Feed it to the owner's audio passthrough, which renders it
        /// to the real DualSense/DualShock speaker (BT Opus/SBC or USB UAC).</summary>
        private void OnRemoteAudioReceived(string peerFingerprint, byte slot, byte[] payload)
        {
            if (payload == null || payload.Length < 2) return;
            if (!ResolveExposed(slot, out var source, out _)) return;
            AudioPassthroughService.FeedRemoteAudio(source.InstanceGuid, payload);
        }

        // ─────────────────────────────────────────────
        //  Touchpad Overlay lifecycle
        // ─────────────────────────────────────────────

        private Views.TouchpadOverlay _touchpadOverlay;
        private TouchpadOverlayDevice _touchpadOverlayDevice;

        // v3.2 shift-layer flyout. Created lazily on first non-Base
        // engagement and reused thereafter. State is read by polling
        // InputManager.GetEngagedLayerMask in UiTimer_Tick.
        private Views.ShiftLayerFlyout _shiftLayerFlyout;
        private int _shiftLayerFlyoutLastSlot = -1;
        private string _shiftLayerFlyoutLastShown = "Base";

        /// <summary>Polls every slot's engaged layer and surfaces the flyout
        /// for whichever slot has a non-Base layer active. Independent of
        /// which page/tab the user is viewing — a Shift engagement on Pad 3
        /// shows the flyout even while the user is on the Dashboard or
        /// Devices page. When more than one slot has a Shift engaged at the
        /// same time, the currently-selected pad wins (if any); otherwise
        /// the lowest-numbered engaged slot wins, for stable display
        /// without flip-flopping.</summary>
        private void UpdateShiftLayerFlyout()
        {
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null)
            {
                if (_shiftLayerFlyoutLastSlot >= 0)
                {
                    _shiftLayerFlyout?.HideFlyout();
                    _shiftLayerFlyoutLastSlot = -1;
                    _shiftLayerFlyoutLastShown = "Base";
                }
                return;
            }

            // Selection priority: currently-viewed pad first, then any
            // engaged slot in ascending order.
            int engagedSlot = -1;
            string engagedMask = "Base";
            int viewed = _mainVm.SelectedPadIndex;
            if (viewed >= 0 && viewed < sets.Length)
            {
                string mask = Common.Input.InputManager.GetEngagedLayerMask(viewed, sets[viewed]);
                if (!string.IsNullOrEmpty(mask) && !string.Equals(mask, "Base", System.StringComparison.Ordinal))
                {
                    engagedSlot = viewed;
                    engagedMask = mask;
                }
            }
            if (engagedSlot < 0)
            {
                for (int s = 0; s < sets.Length; s++)
                {
                    if (s == viewed) continue; // already checked
                    string mask = Common.Input.InputManager.GetEngagedLayerMask(s, sets[s]);
                    if (!string.IsNullOrEmpty(mask) && !string.Equals(mask, "Base", System.StringComparison.Ordinal))
                    {
                        engagedSlot = s;
                        engagedMask = mask;
                        break;
                    }
                }
            }

            // Capture the prior (slot, mask) before updating so a transition
            // from a shift layer back to Base can surface the Base flyout (#119).
            int prevSlot = _shiftLayerFlyoutLastSlot;
            string prevShown = _shiftLayerFlyoutLastShown;

            // No-op when (slot, mask) tuple hasn't changed since last tick.
            if (engagedSlot == prevSlot
                && string.Equals(engagedMask, prevShown, System.StringComparison.Ordinal))
                return;

            if (engagedSlot >= 0)
            {
                _shiftLayerFlyoutLastSlot = engagedSlot;
                _shiftLayerFlyoutLastShown = engagedMask;

                // Resolve activator (for LayerName + Color + Icon) by the engaged mask.
                var ms = sets[engagedSlot];
                string layerName = engagedMask;
                string color = "";
                string icon = "";
                if (ms?.ShiftActivators != null)
                {
                    foreach (var a in ms.ShiftActivators)
                    {
                        if (a == null) continue;
                        if (!string.Equals(a.LayerMask, engagedMask, System.StringComparison.Ordinal)) continue;
                        if (!string.IsNullOrEmpty(a.LayerName)) layerName = a.LayerName;
                        color = a.Color ?? "";
                        icon = a.Icon ?? "";
                        break;
                    }
                }

                if (_shiftLayerFlyout == null)
                    _shiftLayerFlyout = new Views.ShiftLayerFlyout();
                _shiftLayerFlyout.ShowLayer(layerName, color, icon);
                return;
            }

            // Everything is at Base now. Surface that slot's Base flyout once on
            // the shift-layer -> Base transition (#119). A steady Base state, or
            // a transition we already showed, shows nothing.
            _shiftLayerFlyoutLastSlot = -1;
            _shiftLayerFlyoutLastShown = "Base";
            if (prevSlot < 0 || prevSlot >= sets.Length
                || string.Equals(prevShown, "Base", System.StringComparison.Ordinal))
                return;

            var baseMs = sets[prevSlot];
            string baseName = PadForge.Resources.Strings.Strings.Instance.Pad_Shift_BaseTabLabel;
            string baseColor = "";
            string baseIcon = "";
            if (baseMs != null)
            {
                if (!string.IsNullOrEmpty(baseMs.BaseLayerName)) baseName = baseMs.BaseLayerName;
                baseColor = baseMs.BaseColor ?? "";
                baseIcon = baseMs.BaseIcon ?? "";
            }

            if (_shiftLayerFlyout == null)
                _shiftLayerFlyout = new Views.ShiftLayerFlyout();
            _shiftLayerFlyout.ShowLayer(baseName, baseColor, baseIcon);
        }

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

        private void ShowVCsToggleOverlay(bool enabled)
        {
            if (_switchOverlay == null)
            {
                _switchOverlay = new Views.ProfileSwitchOverlay();
                _switchOverlay.CheckInitState = CheckAllSlotsInitState;
                _switchOverlay.CheckAnyOffline = CheckAnyControllerOffline;
            }
            _switchOverlay.ShowVCsToggle(enabled);
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
            // Per-slot MappedDevices dropdown items snapshot the localized
            // device name at population time, so they keep showing the old
            // language until UpdatePadDeviceInfo re-runs.
            UpdatePadDeviceInfo();
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
                "Running" => Strings.Instance.Engine_Forging,
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

        /// <summary>Resolves a device's live PnP instance IDs by VID/PID for the
        /// synthetic-path fallbacks (blacklist sync and the details pane). The
        /// combined Joy-Con pair (issue #184 sibling) is an SDL-manufactured
        /// virtual device whose PID exists on no PnP node, so it searches the
        /// physical children's PIDs instead; the halves are what games see.
        /// Per SDL usb_ids.h: gen-1 pair 0x2008 has children 0x2006 (Left) and
        /// 0x2007 (Right); pair PID 0x2068 covers a real Switch 2 pair (children
        /// 0x2067 Left / 0x2066 Right) AND a gen-1 pair docked in the charging
        /// grip, whose only PnP node is the grip itself (0x200E), so all three
        /// are searched.</summary>
        private static List<string> FindInstanceIdsForDevice(UserDevice ud)
        {
            var pidLookups = (ud.VendorId, ud.ProdId) switch
            {
                (0x057E, 0x2008) => new ushort[] { 0x2006, 0x2007 },
                (0x057E, 0x2068) => new ushort[] { 0x2067, 0x2066, 0x200E },
                _ => new[] { (ushort)ud.ProdId },
            };
            var realIds = new List<string>();
            foreach (var pid in pidLookups)
                realIds.AddRange(HidHideController.FindInstanceIdsByVidPid(
                    (ushort)ud.VendorId, pid));
            return realIds;
        }

        /// <summary>
        /// Applies device hiding based on per-device toggle settings.
        /// HidHide: Adds devices with HidHideEnabled to the blacklist, whitelists PadForge, activates cloaking.
        /// Hooks: Starts input hook manager for devices with ConsumeInputEnabled.
        /// Only acts if the master switch (EnableInputHiding) is on.
        /// </summary>
        /// <summary>Player-identity idle floor (#191): re-seeds every
        /// assigned device's player number. Nintendo-family pads get the
        /// SDL player index (wrapper-allowlisted to VID 0x057E; the SDL
        /// call is how their player LEDs light), and every live Sony
        /// dispatcher re-applies so its per-dispatch player number picks
        /// up topology changes. Called on device-list updates and after
        /// slot create / delete / reorder.</summary>
        public void ReseedPlayerIdentities(bool applySonyDispatchers = true)
        {
            try
            {
                // Snapshot-then-resolve, the canonical shape: the lock
                // order rule is UserDevices BEFORE UserSettings, and
                // FindDeviceByInstanceGuid takes the devices lock
                // internally, so it must never run while the settings
                // lock is held. The SDL call below also does a
                // synchronous Switch subcommand round-trip (retried BT
                // I/O), which must not run under ANY of our locks.
                // One write per DEVICE, not per (device, slot) pair. A
                // device feeding several virtual controllers used to get
                // every slot's number written back to back in Items order,
                // flashing the losing slot's player LED on every reseed.
                // The identity winner is the smallest displayed number
                // (SlotOrders.GetIdentityPlayerNumber).
                var guids = new List<Guid>();
                var settings = SettingsManager.UserSettings;
                if (settings != null)
                {
                    lock (settings.SyncRoot)
                    {
                        for (int i = 0; i < settings.Items.Count; i++)
                        {
                            var us = settings.Items[i];
                            if (us == null || us.MapTo < 0) continue;
                            if (us.InstanceGuid == Guid.Empty) continue;
                            if (!guids.Contains(us.InstanceGuid))
                                guids.Add(us.InstanceGuid);
                        }
                    }
                }
                foreach (var guid in guids)
                {
                    var ud = SettingsManager.FindDeviceByInstanceGuid(guid);
                    if (ud == null || !ud.IsOnline) continue;
                    int n = SettingsManager.SlotOrders.GetIdentityPlayerNumber(guid);
                    if (n <= 0) continue;
                    if (ud.Device is PadForge.Engine.SdlDeviceWrapper w)
                    {
                        // Xbox 360 rings are deliberately NOT repainted here:
                        // the XUSB setter exists (OpenXInput SendLEDState,
                        // OpenXinput.cpp:2427), but every xinput1_4 instance
                        // recovers a pad's port FROM the ring LED on
                        // enumeration (OpenXinput.cpp:1217-1233), so writing
                        // a player number that differs from the port re-seats
                        // the pad on the wrong XInput slot in other processes.
                        w.SetPlayerIndex(n - 1);
                    }
                    else if (ud.Device is PadForge.Engine.RemoteLink.RemotePeerDevice rpd)
                    {
                        // A shared non-Sony pad's player LED is machine-local on the
                        // owner, so relay the consumer's winning global number over
                        // the link (#191). DualSense/DS4 peers carry the player LED
                        // in the SonyEffect body already, so they are excluded here.
                        bool nintendo = rpd.VendorId == 0x057E;
                        bool ds3 = rpd.VendorId == 0x054C && rpd.ProductId == 0x0268;
                        if (nintendo || ds3)
                            PadForge.Common.Input.RemoteLinkOutputRouter.ShipPlayerIndex(rpd.DevicePath, n);
                    }
                }
            }
            catch { }
            if (applySonyDispatchers)
                PadForge.Common.Input.UserEffectsDispatcher.ApplyOnceAll();
        }

        /// <summary>Best-effort quiet of every hardware output lane:
        /// zero rumble on all devices and stop any sustained haptic
        /// tones. Wired into the crash handler and ProcessExit so an
        /// abnormal exit cannot leave a Steam Deck (or any pad) buzzing
        /// on the last command it received (discussion #179).</summary>
        public void PanicQuiesceOutputs()
        {
            try { _inputManager?.QuiesceOutputs(); } catch { }
            try { PadForge.Common.Input.HapticToneService.Shutdown(); } catch { }
        }

        /// <summary>Clears every HidHide blacklist entry, exactly like the
        /// engine-start stale-cloak sweep. Reset to Defaults calls this
        /// because ApplyDeviceHiding's diff only removes cloaks in the
        /// in-process managed set: cloaks persisted by "keep cloaks
        /// between launches" in a session where the engine never started
        /// would otherwise stay asserted with every UI record of them
        /// wiped.</summary>
        public void PurgeStaleHidHideCloaks()
        {
            try
            {
                if (HidHideController.IsAvailable())
                    HidHideController.ClearAll();
            }
            catch { /* best effort, same as the engine-start sweep */ }
        }

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
                        // Fallback for synthetic paths (e.g., "XInput#0"): look up by VID/PID.
                        else if (ud.VendorId > 0 && ud.ProdId > 0)
                        {
                            var realIds = FindInstanceIdsForDevice(ud);

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
                EnsureHookManager();
                _hookManager.SetSuppressedKeys(suppressedKeys);
                _hookManager.SetSuppressedMouseButtons(suppressedMouse);
            }
            else
            {
                // No inputs to suppress and no global hotkeys registered — stop hooks if running.
                if (_hookManager != null)
                {
                    _hookManager.Stop();
                    _hookManager.Dispose();
                    _hookManager = null;
                }
            }
        }

        /// <summary>
        /// Idempotent: create + start the keyboard / mouse hook manager if it
        /// is not yet running. Called by SyncInputHooks AND by global-hotkey
        /// registration paths that need the hook alive even with zero
        /// suppressed inputs.
        /// </summary>
        private void EnsureHookManager()
        {
            if (_hookManager != null) return;
            _hookManager = new InputHookManager();
            _hookManager.Start();
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
        /// <param name="keepCloaks">When true, the HidHide-removal portion is
        /// skipped — managed device entries stay asserted and the in-memory
        /// whitelist DOS paths are kept so a follow-up reapply doesn't have
        /// to re-walk every device. Input hooks are still torn down (they're
        /// in-process state with nothing to persist). Used by the
        /// shutdown path when KeepHidHideCloaksBetweenLaunches is on.</param>
        public void RemoveDeviceHiding(bool keepCloaks = false)
        {
            // ── HidHide ──
            if (!keepCloaks)
            {
                try
                {
                    if (HidHideController.IsAvailable())
                        HidHideController.RemoveManagedDevices();
                }
                catch { /* Best effort — driver may not be available */ }
                _managedWhitelistDosPaths.Clear();
            }

            // ── Input hooks ──
            if (_hookManager != null)
            {
                _hookManager.ClearGlobalHotkeys();
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

            string deviceGuidStr = ud.InstanceGuid.ToString();

            // Parse a "Button {index}" descriptor and route the index to the
            // right suppression set. For a keyboard the index is a VKey code,
            // for a mouse it is a button id (0=L, 1=M, 2=R, 3=X1, 4=X2).
            void AddDescriptor(string descriptor)
            {
                if (string.IsNullOrEmpty(descriptor)) return;
                if (descriptor.StartsWith("Button ", StringComparison.Ordinal) &&
                    int.TryParse(descriptor.AsSpan(7), out int index))
                {
                    if (ud.IsKeyboard) keys.Add(index);
                    else if (ud.IsMouse) mouseButtons.Add(index);
                }
            }

            foreach (int slotIndex in assignedSlots)
            {
                // Legacy single-source PadSetting descriptors. These hold the
                // owning Direct mapping's descriptor only, so they catch the
                // primary Direct case (and stay as a fallback for configs not
                // resaved since the MappingSet UI shipped).
                var us = SettingsManager.FindSettingByInstanceGuidAndSlot(ud.InstanceGuid, slotIndex);
                var ps = us?.GetPadSetting();
                if (ps != null)
                {
                    foreach (string descriptor in ps.GetAllMappingDescriptors())
                        AddDescriptor(descriptor);
                }

                // Authoritative per-slot MappingSet: every source on every row.
                // The legacy PadSetting cannot encode Ramp/Incremental up/down
                // keys (they live in ParamUp/ParamDown, not SourceDescriptor) or
                // secondary sources on a multi-source row, so reading the same
                // data the engine evaluates is what makes consumption cover all
                // mapping kinds and both primary and secondary sources.
                var msArr = SettingsManager.SlotMappingSets;
                if (msArr != null && slotIndex >= 0 && slotIndex < msArr.Length)
                {
                    var ms = msArr[slotIndex];
                    if (ms?.Rows != null)
                    {
                        foreach (var row in ms.Rows)
                        {
                            if (row?.Sources == null) continue;
                            foreach (var src in row.Sources)
                            {
                                if (src == null) continue;

                                // Same device scoping the engine uses for a
                                // per-device pass: empty DeviceGuid means "any
                                // device" (it resolves to this one at runtime).
                                if (!string.IsNullOrEmpty(src.DeviceGuid) &&
                                    !string.Equals(src.DeviceGuid, deviceGuidStr, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                // Collect exactly the descriptors each kind reads
                                // as input, mirroring SourceEvaluator's dispatch.
                                switch (src.Kind ?? "Direct")
                                {
                                    case "Incremental":
                                    case "Ramped":
                                        AddDescriptor(src.ParamUp);
                                        AddDescriptor(src.ParamDown);
                                        break;
                                    case "InvertOnHold":
                                        AddDescriptor(src.Descriptor);
                                        AddDescriptor(src.ParamModifier);
                                        break;
                                    case "WindingStick":
                                    case "AngleToAxisX":
                                    case "AngleToAxisY":
                                    case "MotionLeanX":
                                        AddDescriptor(src.Descriptor);
                                        AddDescriptor(src.ParamYDescriptor);
                                        break;
                                    default: // Direct + unknown kinds
                                        AddDescriptor(src.Descriptor);
                                        break;
                                }
                            }
                        }
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

            // Sort: physical hardware first, merged aggregate:// sources
            // below it (#175 phase 2 item 17), then alphabetically by name,
            // then by VID:PID.
            var sorted = devVm.Devices.OrderBy(d => d.DevicePath?.StartsWith("aggregate://", StringComparison.Ordinal) == true ? 1 : 0)
                                      .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
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
            // Dossier (#175 item 7): SDL's serial (BT MAC on most wireless
            // pads), already persisted on UserDevice. Empty when unreported.
            row.SerialNumber = ud.SerialNumber ?? string.Empty;
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
            // Rumble mirrors the Pad page gate (PadPage.SyncTabVisibility):
            // impulse-trigger Xbox pads and the Sony lightbar family rumble
            // through their raw-HID sole writers, so SDL's view of the shared
            // handle can read no-rumble while the device very much rumbles.
            // HD-haptic families (Switch gen-1 / Steam Controller, #147)
            // rumble through their sole writer (HapticToneService), and the
            // Switch 2 family (0x2066-0x2069) through the fork's SendEffect
            // passthrough (SDL#9, hardware-confirmed #162), both invisible
            // to SDL's shared-handle rumble flag (user report 2026-07-05:
            // Steam Controller and Switch 2 Pro dossiers showed no rumble
            // chip despite hardware-confirmed rumble).
            bool sonyLightbarPad = ud.VendorId == 0x054C
                && ud.ProdId is 0x0CE6 or 0x0DF2 or 0x05C4 or 0x09CC or 0x0BA0;
            bool switch2Pad = ud.VendorId == 0x057E
                && ud.ProdId is 0x2066 or 0x2067 or 0x2068 or 0x2069;
            // Stick-class gate (same class list as the Pad page's Force
            // Feedback tab): keyboards, mice, touchpads, MIDI, NFC, and
            // consumer-control devices never rumble, whatever a generic HID
            // haptic flag claims (user report 2026-07-05: a HID keyboard
            // wore the rumble chip).
            bool rumbleClass = ud.CapType == InputDeviceType.Gamepad
                || ud.CapType == InputDeviceType.Joystick
                || ud.CapType == InputDeviceType.Driving
                || ud.CapType == InputDeviceType.Flight
                || ud.CapType == InputDeviceType.FirstPerson
                || ud.CapType == InputDeviceType.Supplemental;
            row.HasRumble = rumbleClass
                && (ud.HasForceFeedback || ud.HasRumbleTriggers || sonyLightbarPad
                    || switch2Pad
                    || PadForge.Common.Input.HapticToneService.DeviceHasHaptics(ud));
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
                !instancePath.StartsWith("XInput", StringComparison.OrdinalIgnoreCase) &&
                // The combined Joy-Con pair's SDL path is a synthetic token, not an
                // instance ID (#184). Fall through to the VID/PID child lookup so
                // the pane shows a real child instance instead of the raw token.
                !instancePath.Equals("nintendo_joycons_combined", StringComparison.OrdinalIgnoreCase))
                row.HidHideInstancePath = instancePath;
            else if (ud.HidHideInstanceIds.Count > 0)
                row.HidHideInstancePath = ud.HidHideInstanceIds[0];
            else if (ud.VendorId > 0 && ud.ProdId > 0)
            {
                // XInput devices have synthetic paths (e.g. "XInput#0") that can't be
                // resolved directly. Look up the real HID instance path by VID/PID
                // (child PIDs for the combined Joy-Con pair).
                var realIds = FindInstanceIdsForDevice(ud);
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

            // Idle disconnect countdown (#162): stored in seconds, edited in
            // minutes, shown for any disconnect-capable device (Bluetooth HID
            // path, or a wireless XInput-backend pad).
            row.IdleDisconnectMinutes = ud.IdleDisconnectSeconds / 60;
            row.ShowIdleDisconnect = PadForge.Common.Input.BluetoothLinkHelper.IsDisconnectTarget(ud.DevicePath, ud.VendorId, ud.ProdId);

            // Battery indicator (#167): seed through the same effective-battery
            // path the 5 s tick uses (#187). Seeding raw SDL battery here blinked
            // the Devices page for Xbox pads: every list refresh stamped -1 and
            // the next tick restored the overlay value.
            var (seedPct, seedCharging) = GetEffectiveBattery(ud);
            row.BatteryPercent = seedPct;
            row.BatteryCharging = seedCharging;

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
                InputDeviceType.Midi => "Midi",
                InputDeviceType.Nfc => "Nfc",
                InputDeviceType.ConsumerControl => "ConsumerControl",
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
        /// Tears down all MIDI input connections and suppresses their
        /// re-enumeration. Called before uninstalling Windows MIDI Services.
        /// </summary>
        public void ShutdownMidiInputs() => _inputManager?.ShutdownMidiInputs();

        /// <summary>
        /// Forces a full re-sync of the device list UI from the current
        /// SettingsManager.UserDevices state. Called by the Refresh button.
        /// </summary>
        public void RefreshDeviceList()
        {
            SyncDevicesList();
            UpdatePadDeviceInfo();
        }

        /// <summary>Pumps SDL's event queue on the UI thread so SDL's hidapi
        /// device-change messages are dispatched and controller hot-plug works
        /// in-process (#116). Call only from the UI thread (the SDL_Init thread).</summary>
        public void PumpSdlEvents() => _inputManager?.PumpSdlEvents();

        /// <summary>Forces SDL to cleanly re-open the Wii hidapi devices after a
        /// pairing (#116). Call after the pairing dialog closes so a just-paired
        /// Wii controller appears without an app restart.</summary>
        public void RescanWiiControllers() => _inputManager?.RescanWiiControllers();

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

        /// <summary>Player/World Space gyro — low-pass-filters every
        /// online accel-capable UserDevice's <c>state.Accel[]</c> into a
        /// gravity vector. Per-device, stored in <c>_gravityState</c>.
        /// Alpha 0.02 at 60Hz UI tick ≈ 0.5Hz cutoff: tracks slow tilt,
        /// rejects motion impulses. The SourceCoercion gravity provider
        /// reads this dict under <c>_gravityStateLock</c>.</summary>
        private void UpdateGravityEstimates()
        {
            const float a = 0.02f;
            var devs = SettingsManager.UserDevices?.Items;
            if (devs == null) return;
            lock (SettingsManager.UserDevices.SyncRoot)
            lock (_gravityStateLock)
            {
                foreach (var d in devs)
                {
                    if (d == null || !d.IsOnline) continue;
                    var st = d.InputState;
                    if (st == null) continue;

                    if (d.HasAccel && st.Accel != null && st.Accel.Length >= 3)
                    {
                        if (!_gravityState.TryGetValue(d.InstanceGuid, out var prev))
                        {
                            // Seed with the first observed accel sample so the
                            // filter converges fast on (re)connect.
                            _gravityState[d.InstanceGuid] = (st.Accel[0], st.Accel[1], st.Accel[2]);
                        }
                        else
                        {
                            _gravityState[d.InstanceGuid] = (
                                prev.gx * (1f - a) + st.Accel[0] * a,
                                prev.gy * (1f - a) + st.Accel[1] * a,
                                prev.gz * (1f - a) + st.Accel[2] * a);
                        }
                    }

                    // Aux (Nunchuk / left Joy-Con) twin (#199): same filter,
                    // separate dict, independent sensor stream.
                    if (d.HasAccelAux && st.AccelAux != null && st.AccelAux.Length >= 3)
                    {
                        if (!_gravityStateAux.TryGetValue(d.InstanceGuid, out var prevAux))
                        {
                            _gravityStateAux[d.InstanceGuid] = (st.AccelAux[0], st.AccelAux[1], st.AccelAux[2]);
                        }
                        else
                        {
                            _gravityStateAux[d.InstanceGuid] = (
                                prevAux.gx * (1f - a) + st.AccelAux[0] * a,
                                prevAux.gy * (1f - a) + st.AccelAux[1] * a,
                                prevAux.gz * (1f - a) + st.AccelAux[2] * a);
                        }
                    }
                }
            }
        }

        /// <summary>For each (UserDevice × assigned slot) pair where the
        /// device is online + gyro-capable and the slot's PadSetting has
        /// no calibration timestamp, fire a background recalibration on
        /// that (device, slot)'s PadSetting. Idempotent — guarded by
        /// _gyroAutoCalibKicked (keyed by (InstanceGuid, slot)) to
        /// survive concurrent UpdatePadDeviceInfo passes while the
        /// 1500 ms worker is still running.</summary>
        private void TryAutoCalibrateGyros()
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return;
            var devs = SettingsManager.UserDevices?.Items;
            if (devs == null) return;
            (UserDevice ud, PadSetting ps)[] candidates;
            // Canonical lock order is UserDevices -> UserSettings (see
            // MappingSetEval's snapshot doc); Settings-first here was one half
            // of an ABBA pair against the disconnect path's Devices-first
            // nesting on the websocket thread.
            lock (SettingsManager.UserDevices.SyncRoot)
            lock (settings.SyncRoot)
            {
                var found = new List<(UserDevice, PadSetting)>();
                for (int i = 0; i < settings.Items.Count; i++)
                {
                    var us = settings.Items[i];
                    if (us == null) continue;
                    int slot = us.MapTo;
                    if (slot < 0 || slot >= InputManager.MaxPads) continue;
                    UserDevice ud = null;
                    foreach (var d in devs)
                    {
                        if (d != null && d.InstanceGuid == us.InstanceGuid) { ud = d; break; }
                    }
                    if (ud == null) continue;
                    if (!ud.HasGyro) continue;
                    if (!ud.IsOnline) continue;
                    var ps = us.GetPadSetting();
                    if (ps == null) continue;
                    if (!string.IsNullOrEmpty(ps.GyroCalibratedAtUtc)) continue;
                    var key = (ud.InstanceGuid, slot);
                    if (_gyroAutoCalibKicked.Contains(key)) continue;
                    _gyroAutoCalibKicked.Add(key);
                    found.Add((ud, ps));
                }
                candidates = found.ToArray();
            }
            foreach (var (ud, ps) in candidates)
                _ = GyroCalibrator.EnsureAutoCalibratedAsync(ud, ps);
        }

        public void UpdatePadDeviceInfo()
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return;

            // Auto-calibrate any newly-seen gyro-capable (device, slot)
            // pair. Worker task; non-blocking; guarded by
            // _gyroAutoCalibKicked so a (device, slot) polling the
            // still-running calibration window doesn't get double-fired.
            TryAutoCalibrateGyros();

            for (int i = 0; i < InputManager.MaxPads && i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                var slotSettings = settings.FindByPadIndex(i);

                if (slotSettings == null || slotSettings.Count == 0)
                {
                    // Clearing MappedDevices alone is not enough: the
                    // selection property and the per-row AvailableInputs
                    // lists are populated state of their own. Leaving
                    // SelectedMappedDevice pointing at the outgoing
                    // profile's device made every downstream consumer that
                    // gates on "selected != null" (ApplyProfile's picker
                    // rebuild first among them) keep rendering that
                    // device's inputs on a slot whose card showed no
                    // device at all (owner report 2026-07-13, Workshop
                    // import). Drop the selection through the setter so
                    // the full notification chain runs, retire the
                    // previous-device tracker BEFORE the setter fires so
                    // OnSelectedDeviceChanged cannot save stale VM state
                    // onto the departed device, and rebuild the pickers
                    // into the device-less state.
                    bool hadDeviceState = padVm.MappedDevices.Count > 0
                                          || padVm.SelectedMappedDevice != null;
                    // Never-populated rows (fresh slot, no device ever
                    // assigned) also need the pass so the "(Any device)"
                    // group shows from the start. All rows are filled
                    // together by PopulateAvailableInputs, so row 0 is
                    // representative.
                    bool pickersEmpty = padVm.Mappings.Count > 0
                                        && padVm.Mappings[0].AvailableInputs.Count == 0;
                    padVm.MappedDevices.Clear();
                    _previousSelectedDevice.Remove(i);
                    if (padVm.SelectedMappedDevice != null)
                        padVm.SelectedMappedDevice = null;
                    padVm.MappedDeviceName = Strings.Instance.Mapping_NoDeviceMapped;
                    padVm.MappedDeviceGuid = Guid.Empty;
                    padVm.IsDeviceOnline = false;
                    if (hadDeviceState || pickersEmpty)
                        PopulateAvailableInputs(padVm, null);
                }
                else
                {
                    // Build list of all mapped devices for this slot.
                    var deviceInfos = new List<PadViewModel.MappedDeviceInfo>();
                    bool anyOnline = false;

                    foreach (var us in slotSettings)
                    {
                        var ud = FindUserDevice(us.InstanceGuid);
                        string name = LocalizedDeviceName(ud) ?? "Unknown device";
                        bool online = ud?.IsOnline ?? false;
                        if (online) anyOnline = true;

                        deviceInfos.Add(new PadViewModel.MappedDeviceInfo
                        {
                            Name = name,
                            InstanceGuid = us.InstanceGuid,
                            IsOnline = online,
                            // Device-class glyph (#175): the roster icon
                            // matches the device type, not a blanket gamepad.
                            TypeGlyph = DeviceTypeGlyph.For(ud?.CapType ?? 0),
                            // Transport (#175 competitor item 9): shared
                            // classifier (classic {00001124}/BTHENUM, BLE
                            // {00001812}, fork BLE Switch 2 by VID/PID plus
                            // empty path). Xbox pads over BT (XInput#N)
                            // answer by Bluetooth-mode PID, see
                            // DeviceTransport.
                            TransportGlyph = DeviceTransport.IsBluetooth(
                                    ud?.DevicePath, ud?.VendorId ?? (ushort)0, ud?.ProdId ?? (ushort)0)
                                ? "" : string.Empty
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
                    padVm.EnsureDeviceSlotConfigsForMappedDevices();

                    // SyncMappedDevices trims removed entries from the tail
                    // of the collection. If the selection object was one of
                    // them it is now detached: not null, so the auto-select
                    // below never runs, and its guid still names a device
                    // that is no longer on this slot (the same phantom
                    // family as the empty-branch stale selection above).
                    // Null it through the setter so the auto-select
                    // re-anchors the selection to a real member.
                    if (padVm.SelectedMappedDevice != null
                        && !padVm.MappedDevices.Contains(padVm.SelectedMappedDevice))
                    {
                        padVm.SelectedMappedDevice = null;
                    }

                    // Auto-select first device if nothing is selected.
                    if (padVm.SelectedMappedDevice == null && padVm.MappedDevices.Count > 0)
                    {
                        padVm.SelectedMappedDevice = padVm.MappedDevices[0];
                    }

                    // If the selected item was overwritten in-place (e.g. a device was
                    // deleted and the next device slid into index 0), fire the same
                    // notification chain the SelectedMappedDevice setter would. The
                    // OnSelectedDeviceChanged listener picks the event up and runs
                    // LoadPadSettingToViewModel + PopulateAvailableInputs + the HM
                    // dispatcher anchor reattach, and PadPage's SyncTabVisibility
                    // listens to SelectedMappedDevice PropertyChanged so the FFB /
                    // Lighting / Adaptive Triggers tabs refresh against the device's
                    // actual capabilities. Without this, the in-place mutation kept
                    // tabs pinned to the unassigned device's capability mask (e.g.
                    // a kbm "All Keyboards (Merged)" hide of FFB / Lighting / AT
                    // would persist after the keyboard was unassigned, even though
                    // a DualSense was still mapped to the slot).
                    if (padVm.SelectedMappedDevice != null
                        && prevSelectedGuid != Guid.Empty
                        && padVm.SelectedMappedDevice.InstanceGuid != prevSelectedGuid)
                    {
                        padVm.NotifySelectedMappedDeviceIdentityChanged();
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
                    collection[i].TypeGlyph = newItems[i].TypeGlyph;
                    collection[i].TransportGlyph = newItems[i].TransportGlyph;
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
        /// only show the extended slots (Misc1, paddles, Touchpad, Misc2-6)
        /// the device actually has. Falls back to a dense 0..count-1 list
        /// (using RawButtonCount in raw passthrough mode, otherwise
        /// CapButtonCount) when the device is offline or doesn't expose a
        /// supported list.
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

        // Parse the Wii Balance Board calibration (#146) from the SDL hex property
        // (24 bytes = 12 int16 big-endian). The board reports three reference rows
        // (0 kg, 17 kg, 34 kg), each four sensors in the order TopRight,
        // BottomRight, TopLeft, BottomLeft (WiiBrew / WiimoteLib calibration block
        // at 0x04a40024). Re-laid out to the 12-float [corner × {Kg0,Kg17,Kg34}]
        // shape SourceCoercion.ReadTunedBalanceBoard expects, with corner order
        // TopLeft, BottomLeft, TopRight, BottomRight. Returns null on a malformed or
        // not-yet-available blob. Row/sensor order and the signed reads are
        // reference-confirmed against WiimoteLib-Trihy Wiimote.cs:534-547 (Kg0/17/34
        // rows x TR,BR,TL,BL sensors, each (short) big-endian, starting 4 bytes into
        // the 0x04a40020 block, which is exactly the fork's 24-byte 0x04a40024 read).
        // Live-board byte truth remains the only hardware-gated residual.
        private static float[] ParseBalanceCalibrationHex(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length < 48) return null;
            var raw = new int[12]; // [row*4 + sensor], row 0/1/2 = Kg0/Kg17/Kg34
            for (int i = 0; i < 12; i++)
            {
                if (!int.TryParse(hex.Substring(i * 4, 4), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out int u16))
                    return null;
                raw[i] = (short)u16; // sign-extend the big-endian 16-bit word
            }
            // Source sensor order per row: 0=TR, 1=BR, 2=TL, 3=BL.
            // Target corner order: 0=TL, 1=BL, 2=TR, 3=BR.
            int[] srcSensor = { 2, 3, 0, 1 }; // TL, BL, TR, BR -> source column
            var cal = new float[12];
            for (int corner = 0; corner < 4; corner++)
            {
                int col = srcSensor[corner];
                cal[corner * 3 + 0] = raw[0 * 4 + col]; // Kg0
                cal[corner * 3 + 1] = raw[1 * 4 + col]; // Kg17
                cal[corner * 3 + 2] = raw[2 * 4 + col]; // Kg34
            }
            return cal;
        }

        /// <summary>Captures the Wii Balance Board's current total weight (#146) as
        /// the tare zero-point for the given device, so "Balance Total Weight" reads
        /// 0 with whatever is currently on the board. Call again with no load to
        /// clear. Reads the live corner load cells from the device's last input
        /// state and the cached kg calibration.</summary>
        public void SetBalanceTare(Guid deviceGuid)
        {
            var ud = FindUserDevice(deviceGuid);
            var st = ud?.InputState;
            if (st?.Axis == null) return;
            float tl = Math.Max(0, st.Axis[0] - 32768);
            float bl = Math.Max(0, st.Axis[1] - 32768);
            float tr = Math.Max(0, st.Axis[3] - 32768);
            float br = Math.Max(0, st.Axis[4] - 32768);

            float[] cal = PadForge.Engine.Common.Mapping.SourceCoercion.BalanceCalibrationProvider?
                .Invoke(deviceGuid.ToString());
            float kg;
            if (cal != null && cal.Length >= 12)
                kg = CornerKg(tl, cal, 0) + CornerKg(bl, cal, 1) + CornerKg(tr, cal, 2) + CornerKg(br, cal, 3);
            else
                kg = (tl + bl + tr + br) / 200f;

            lock (_balanceLock) _balanceTareKg[deviceGuid] = kg;
        }

        private static float CornerKg(float raw, float[] cal, int corner)
        {
            float kg0 = cal[corner * 3 + 0], kg17 = cal[corner * 3 + 1], kg34 = cal[corner * 3 + 2];
            if (raw < kg17)
            {
                float span = kg17 - kg0;
                return span > 0f ? 17f * (raw - kg0) / span : 0f;
            }
            float span2 = kg34 - kg17;
            return span2 > 0f ? 17f * (raw - kg17) / span2 + 17f : 17f;
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

        /// <summary>
        /// Fires an impulse-trigger rumble pulse on the targeted device (or
        /// all devices in the slot when <paramref name="deviceGuid"/> is null).
        /// Writes into <c>VibrationStates[padIndex].Left/RightTriggerMotorSpeed</c>
        /// and lets the existing Step-2 ApplyForceFeedback path forward via
        /// SDL_RumbleGamepadTriggers — same architecture as main-motor test
        /// rumble, just on the parallel trigger channel.
        /// </summary>
        public void SendTestImpulseTrigger(int padIndex, Guid? deviceGuid, bool left, bool right)
        {
            if (_inputManager == null || padIndex < 0 || padIndex >= InputManager.MaxPads)
                return;

            if (deviceGuid.HasValue && deviceGuid.Value != Guid.Empty)
                _inputManager.TestRumbleTargetGuid[padIndex] = deviceGuid.Value;

            var vib = _inputManager.VibrationStates[padIndex];
            if (left) vib.LeftTriggerMotorSpeed = 65535;
            if (right) vib.RightTriggerMotorSpeed = 65535;

            var clearTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            clearTimer.Tick += (s2, e2) =>
            {
                if (_inputManager != null && padIndex < InputManager.MaxPads)
                {
                    if (left) vib.LeftTriggerMotorSpeed = 0;
                    if (right) vib.RightTriggerMotorSpeed = 0;
                    _inputManager.TestRumbleTargetGuid[padIndex] = Guid.Empty;
                }
                clearTimer.Stop();
            };
            clearTimer.Start();
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

        // ── Per-variable recording state for the macro custom-expression editor ──
        private MacroExpressionVariable _recordingVariable;
        private int _recordingVariablePadIndex;
        private DateTime _recordingVariableStartTime;
        /// <summary>Snapshot of axis values at the start of variable recording.
        /// Used to detect deflection rather than absolute axis position.</summary>
        private Dictionary<Guid, int[]> _recordingVariableAxisBaseline;
        /// <summary>Snapshot of POV values at recording start (for the same reason).</summary>
        private Dictionary<Guid, int[]> _recordingVariablePovBaseline;
        /// <summary>Snapshot of the slot's combined virtual controller output
        /// at the start of an OutputController-source recording — baseline for
        /// detecting "what changed".</summary>
        private Gamepad _recordingVariableOutputBaseline;
        private bool _recordingVariableOutputBaselineSet;
        private const float ExpressionVariableAxisDeflectionThreshold = 0.30f;
        private const double ExpressionVariableRecordTimeoutSeconds = 5;

        /// <summary>Starts recording a single input binding for one variable in
        /// a macro's custom-expression trigger. The first detected button press,
        /// POV deflection, or axis deflection on any device assigned to the slot
        /// is captured and written into the variable.</summary>
        public void StartExpressionVariableRecording(MacroExpressionVariable variable, int padIndex)
        {
            if (variable == null) return;
            if (_recordingVariable != null && _recordingVariable != variable)
                StopExpressionVariableRecording();

            _recordingVariable = variable;
            _recordingVariablePadIndex = padIndex;
            _recordingVariableStartTime = DateTime.UtcNow;
            _recordingVariableAxisBaseline = new Dictionary<Guid, int[]>();
            _recordingVariablePovBaseline = new Dictionary<Guid, int[]>();
            _recordingVariableOutputBaselineSet = false;
            variable.LiveText = Strings.Instance.Macro_RecordHint;
            variable.IsRecording = true;
            CaptureExpressionVariableBaseline();
        }

        /// <summary>Stops a per-variable recording session without writing a
        /// new binding. Called when the user clicks the same button again or
        /// when the recording times out.</summary>
        public void StopExpressionVariableRecording()
        {
            if (_recordingVariable == null) return;
            _recordingVariable.LiveText = "";
            _recordingVariable.IsRecording = false;
            _recordingVariable = null;
            _recordingVariableAxisBaseline = null;
            _recordingVariablePovBaseline = null;
        }

        private void CaptureExpressionVariableBaseline()
        {
            int padIndex = _recordingVariablePadIndex;
            if (padIndex < 0 || padIndex >= InputManager.MaxPads) return;
            // Baseline scope = devices assigned to this slot's virtual
            // controller. Users who want a keyboard, mouse, or aggregate
            // device to be recordable must assign it to the slot first —
            // that's the contract the "Assigned Devices" source label
            // represents.
            var slotSettings = SettingsManager.GetSettingsForSlot(padIndex);
            if (slotSettings != null)
            {
                for (int i = 0; i < slotSettings.Count; i++)
                {
                    var ud = FindUserDevice(slotSettings[i].InstanceGuid);
                    if (ud?.InputState?.Axis != null)
                        _recordingVariableAxisBaseline[ud.InstanceGuid] = (int[])ud.InputState.Axis.Clone();
                    if (ud?.InputState?.Povs != null)
                        _recordingVariablePovBaseline[ud.InstanceGuid] = (int[])ud.InputState.Povs.Clone();
                }
            }
            // Output-controller baseline — used when the variable's Source
            // is OutputController so we can detect "first changed button /
            // first axis deflected past threshold" against the slot's
            // current combined Gamepad state.
            if (_inputManager != null)
            {
                _recordingVariableOutputBaseline = _inputManager.CombinedOutputStates[padIndex];
                _recordingVariableOutputBaselineSet = true;
            }
        }

        /// <summary>Polls live device state once per UI tick during a variable
        /// recording session. Captures the first detectable input and writes it
        /// to the variable, then stops.</summary>
        private void UpdateExpressionVariableRecording()
        {
            if (_recordingVariable == null) return;
            if ((DateTime.UtcNow - _recordingVariableStartTime).TotalSeconds >= ExpressionVariableRecordTimeoutSeconds)
            {
                StopExpressionVariableRecording();
                return;
            }
            int padIndex = _recordingVariablePadIndex;
            if (padIndex < 0 || padIndex >= InputManager.MaxPads) return;

            // Route based on the variable's Source choice. Virtual Controller
            // samples the slot's combined output; Assigned Devices walks only
            // the devices the user has assigned to this slot's virtual
            // controller (matching the "Assigned Devices" source label).
            if (_recordingVariable.Source == MacroTriggerSource.OutputController)
            {
                if (ScanOutputControllerForFirstChange(padIndex)) return;
                return;
            }

            var slotSettings = SettingsManager.GetSettingsForSlot(padIndex);
            if (slotSettings == null) return;

            for (int sIdx = 0; sIdx < slotSettings.Count; sIdx++)
            {
                var ud = FindUserDevice(slotSettings[sIdx].InstanceGuid);
                if (ud?.InputState == null) continue;

                // 1. First-button-pressed wins.
                var buttons = ud.InputState.Buttons;
                if (buttons != null)
                {
                    for (int b = 0; b < buttons.Length; b++)
                    {
                        if (buttons[b])
                        {
                            FinalizeExpressionVariableInputDevice(ud.InstanceGuid, rawButton: b, pov: null, axis: MacroAxisTarget.None);
                            return;
                        }
                    }
                }

                // 2. POV deflection from centered baseline.
                var povs = ud.InputState.Povs;
                if (povs != null)
                {
                    _recordingVariablePovBaseline.TryGetValue(ud.InstanceGuid, out var basePovs);
                    for (int p = 0; p < povs.Length; p++)
                    {
                        int now = povs[p];
                        int wasCentered = basePovs == null || p >= basePovs.Length || basePovs[p] < 0 ? 1 : 0;
                        if (now >= 0 && wasCentered == 1)
                        {
                            FinalizeExpressionVariableInputDevice(ud.InstanceGuid, rawButton: -1, pov: $"{p}:{now}", axis: MacroAxisTarget.None);
                            return;
                        }
                    }
                }

                // 3. Axis deflection past threshold from baseline.
                var axes = ud.InputState.Axis;
                if (axes != null && _recordingVariableAxisBaseline.TryGetValue(ud.InstanceGuid, out var baseAxes))
                {
                    int detected = -1;
                    float bestDelta = 0f;
                    int limit = Math.Min(axes.Length, baseAxes.Length);
                    for (int a = 0; a < limit; a++)
                    {
                        float deltaNorm = Math.Abs(axes[a] - baseAxes[a]) / 65535f;
                        if (deltaNorm > bestDelta) { bestDelta = deltaNorm; detected = a; }
                    }
                    if (detected >= 0 && bestDelta >= ExpressionVariableAxisDeflectionThreshold)
                    {
                        MacroAxisTarget axTarget = detected switch
                        {
                            0 => MacroAxisTarget.LeftStickX,
                            1 => MacroAxisTarget.LeftStickY,
                            2 => MacroAxisTarget.LeftTrigger,
                            3 => MacroAxisTarget.RightStickX,
                            4 => MacroAxisTarget.RightStickY,
                            5 => MacroAxisTarget.RightTrigger,
                            _ => MacroAxisTarget.None
                        };
                        if (axTarget != MacroAxisTarget.None)
                        {
                            FinalizeExpressionVariableInputDevice(ud.InstanceGuid, rawButton: -1, pov: null, axis: axTarget);
                            return;
                        }
                    }
                }
            }
        }

        private void FinalizeExpressionVariableInputDevice(Guid deviceGuid, int rawButton, string pov, MacroAxisTarget axis)
        {
            if (_recordingVariable == null) return;
            _recordingVariable.Source = MacroTriggerSource.InputDevice;
            _recordingVariable.DeviceGuid = deviceGuid;
            _recordingVariable.RawButton = rawButton;
            _recordingVariable.Pov = pov;
            _recordingVariable.AxisTarget = axis;
            _recordingVariable.OutputChannel = MacroOutputChannel.None;
            StopExpressionVariableRecording();
        }

        /// <summary>Scans the slot's current combined virtual controller output
        /// for a first detectable change since the recording-start baseline.
        /// Returns true if a binding was captured (and the session has been
        /// stopped) so the caller can exit immediately.</summary>
        private bool ScanOutputControllerForFirstChange(int padIndex)
        {
            if (_inputManager == null || !_recordingVariableOutputBaselineSet) return false;
            var cur = _inputManager.CombinedOutputStates[padIndex];
            var bs = _recordingVariableOutputBaseline;

            // 1. Button bit that wasn't set in baseline.
            (ushort flag, MacroOutputChannel ch)[] buttonMap = new (ushort, MacroOutputChannel)[]
            {
                (Gamepad.A,             MacroOutputChannel.A),
                (Gamepad.B,             MacroOutputChannel.B),
                (Gamepad.X,             MacroOutputChannel.X),
                (Gamepad.Y,             MacroOutputChannel.Y),
                (Gamepad.LEFT_SHOULDER, MacroOutputChannel.LB),
                (Gamepad.RIGHT_SHOULDER,MacroOutputChannel.RB),
                (Gamepad.LEFT_THUMB,    MacroOutputChannel.LS),
                (Gamepad.RIGHT_THUMB,   MacroOutputChannel.RS),
                (Gamepad.BACK,          MacroOutputChannel.Back),
                (Gamepad.START,         MacroOutputChannel.Start),
                (Gamepad.GUIDE,         MacroOutputChannel.Guide),
                (Gamepad.DPAD_UP,       MacroOutputChannel.DpadUp),
                (Gamepad.DPAD_DOWN,     MacroOutputChannel.DpadDown),
                (Gamepad.DPAD_LEFT,     MacroOutputChannel.DpadLeft),
                (Gamepad.DPAD_RIGHT,    MacroOutputChannel.DpadRight),
            };
            for (int i = 0; i < buttonMap.Length; i++)
            {
                bool was = (bs.Buttons & buttonMap[i].flag) != 0;
                bool now = (cur.Buttons & buttonMap[i].flag) != 0;
                if (!was && now)
                {
                    FinalizeExpressionVariableOutputChannel(buttonMap[i].ch);
                    return true;
                }
            }

            // 2. Trigger or stick axis past 30% deflection from baseline.
            float deflectLT = Math.Abs(cur.LeftTrigger - bs.LeftTrigger) / 65535f;
            float deflectRT = Math.Abs(cur.RightTrigger - bs.RightTrigger) / 65535f;
            float deflectLX = Math.Abs(cur.ThumbLX - bs.ThumbLX) / 65535f;
            float deflectLY = Math.Abs(cur.ThumbLY - bs.ThumbLY) / 65535f;
            float deflectRX = Math.Abs(cur.ThumbRX - bs.ThumbRX) / 65535f;
            float deflectRY = Math.Abs(cur.ThumbRY - bs.ThumbRY) / 65535f;

            float best = 0f;
            MacroOutputChannel bestCh = MacroOutputChannel.None;
            if (deflectLT > best) { best = deflectLT; bestCh = MacroOutputChannel.LT; }
            if (deflectRT > best) { best = deflectRT; bestCh = MacroOutputChannel.RT; }
            if (deflectLX > best) { best = deflectLX; bestCh = MacroOutputChannel.LX; }
            if (deflectLY > best) { best = deflectLY; bestCh = MacroOutputChannel.LY; }
            if (deflectRX > best) { best = deflectRX; bestCh = MacroOutputChannel.RX; }
            if (deflectRY > best) { best = deflectRY; bestCh = MacroOutputChannel.RY; }

            if (best >= ExpressionVariableAxisDeflectionThreshold && bestCh != MacroOutputChannel.None)
            {
                FinalizeExpressionVariableOutputChannel(bestCh);
                return true;
            }
            return false;
        }

        private void FinalizeExpressionVariableOutputChannel(MacroOutputChannel channel)
        {
            if (_recordingVariable == null) return;
            _recordingVariable.Source = MacroTriggerSource.OutputController;
            _recordingVariable.OutputChannel = channel;
            _recordingVariable.DeviceGuid = Guid.Empty;
            _recordingVariable.RawButton = -1;
            _recordingVariable.Pov = null;
            _recordingVariable.AxisTarget = MacroAxisTarget.None;
            StopExpressionVariableRecording();
        }

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
            _recordedInputEntries = new List<MacroItem.TriggerInputEntry>();
            _perDeviceAxisBaseline = new Dictionary<Guid, int[]>();
            _perDeviceAxisCandidates = new Dictionary<Guid, AxisCandidate>();
            _recordedPerDeviceAxisEntries = new List<MacroItem.TriggerInputEntry>();
            // For InputDevice source, snapshot every assigned device's axis
            // baseline so the per-tick scan can detect deflection from rest.
            // Skip keyboards (no axes) and mice (delta-not-positional axes).
            if (macro.TriggerSource == MacroTriggerSource.InputDevice)
            {
                var slotSettings = SettingsManager.UserSettings?.FindByPadIndex(padIndex);
                if (slotSettings != null)
                {
                    foreach (var setting in slotSettings)
                    {
                        var ud = FindUserDevice(setting.InstanceGuid);
                        if (ud == null || !ud.IsOnline || ud.InputState?.Axis == null) continue;
                        if (ud.IsKeyboard || ud.IsMouse) continue;
                        _perDeviceAxisBaseline[ud.InstanceGuid] = (int[])ud.InputState.Axis.Clone();
                        _perDeviceAxisCandidates[ud.InstanceGuid] = new AxisCandidate();
                    }
                }
            }
            _macroAxisCandidate = MacroAxisTarget.None;
            _macroAxisCandidateDelta = 0f;
            _macroAxisHoldCounter = 0;

            // Capture axis baseline so we detect movement delta, not absolute position.
            _macroAxisBaseline = CaptureAxisBaseline(padIndex, macro.TriggerSource, macro.ButtonStyle);

            _macroRecordStartTime = DateTime.UtcNow;
            macro.RecordingLiveText = Strings.Instance.Macro_LiveRecord_Placeholder;
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

            // Save recorded POV triggers (legacy single-device path).
            _recordingMacro.TriggerPovs = _recordedPovs?.Count > 0
                ? _recordedPovs.ToArray()
                : Array.Empty<string>();

            // Preserve gesture entries added via the trigger dropdown
            // (#177): recording can never produce them (the recorder
            // scans raw buttons / POVs / axes only), so every branch
            // below that rewrites the entry list would otherwise
            // silently wipe a picked gesture. That includes a recording
            // stopped with nothing captured and the OutputController
            // paths. Gestures survive every re-record; the Clear button
            // remains the way to remove them.
            var priorGestures = new List<MacroItem.TriggerInputEntry>();
            foreach (var prior in _recordingMacro.GetTriggerInputEntries())
                if (prior != null && !string.IsNullOrEmpty(prior.GestureDescriptor))
                    priorGestures.Add(prior);

            // InputDevice triggers always serialize through the multi-device
            // TriggerInputEntries list. This unifies the single-device and
            // multi-device cases into one code path and handles per-device
            // axis entries which legacy fields can't express.
            if (_recordingMacro.TriggerSource == MacroTriggerSource.InputDevice
                && _recordedInputEntries != null && _recordedInputEntries.Count > 0)
            {
                var merged = new List<MacroItem.TriggerInputEntry>(_recordedInputEntries);
                merged.AddRange(priorGestures);
                _recordingMacro.SetTriggerInputEntries(merged);

                // Back-compat: when the combo is a single device with only
                // buttons + POVs (no per-device axes), mirror into the
                // legacy fields so older PadForge builds still see the
                // trigger. Multi-device combos and axis-bearing combos
                // can't be expressed in the legacy format — clear the
                // legacy fields in those cases.
                bool singleDevice = _recordedInputEntries
                    .Select(e => e.DeviceGuid).Distinct().Count() == 1;
                bool hasPerDeviceAxes = _recordedInputEntries
                    .Any(e => e.AxisTarget != MacroAxisTarget.None);

                if (singleDevice && !hasPerDeviceAxes)
                {
                    var firstGuid = _recordedInputEntries[0].DeviceGuid;
                    _recordingMacro.TriggerDeviceGuid = firstGuid;
                    _recordingMacro.TriggerRawButtons = _recordedInputEntries
                        .Where(e => e.RawButton >= 0)
                        .Select(e => e.RawButton).OrderBy(x => x).ToArray();
                    _recordingMacro.TriggerPovs = _recordedInputEntries
                        .Where(e => !string.IsNullOrEmpty(e.Pov))
                        .Select(e => e.Pov).ToArray();
                }
                else
                {
                    _recordingMacro.TriggerDeviceGuid = Guid.Empty;
                    _recordingMacro.TriggerRawButtons = Array.Empty<int>();
                    _recordingMacro.TriggerPovs = Array.Empty<string>();
                }
                _recordingMacro.TriggerButtons = 0;
                _recordingMacro.TriggerCustomButtonWords = new uint[4];
                // Per-device axes live in entries; clear the slot-combined
                // legacy axis fields so the evaluator's legacy axis check
                // doesn't double-fire.
                _recordingMacro.TriggerAxisTargets = Array.Empty<MacroAxisTarget>();
                _recordingMacro.TriggerAxisDirections = Array.Empty<MacroAxisDirection>();
            }
            else if (_recordingMacro.ButtonStyle == MacroButtonStyle.Numbered
                     && _recordedCustomButtons != null && _recordedCustomButtons.Any(w => w != 0))
            {
                // Custom Extended button path (OutputController source on
                // an Extended slot — Xbox bitmask + custom button words).
                _recordingMacro.TriggerCustomButtonWords = (uint[])_recordedCustomButtons.Clone();
                _recordingMacro.TriggerButtons = 0;
                _recordingMacro.TriggerDeviceGuid = Guid.Empty;
                _recordingMacro.TriggerRawButtons = Array.Empty<int>();
                _recordingMacro.SetTriggerInputEntries(priorGestures);
            }
            else
            {
                // Xbox bitmask path (OutputController source). Slot-combined
                // axes are in TriggerAxisTargets via the legacy path above.
                _recordingMacro.TriggerButtons = _recordedButtons;
                _recordingMacro.TriggerDeviceGuid = Guid.Empty;
                _recordingMacro.TriggerRawButtons = Array.Empty<int>();
                _recordingMacro.TriggerCustomButtonWords = new uint[4];
                _recordingMacro.SetTriggerInputEntries(priorGestures);
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
            _recordedInputEntries = null;
            _perDeviceAxisBaseline = null;
            _perDeviceAxisCandidates = null;
            _recordedPerDeviceAxisEntries = null;
            _macroAxisBaseline = null;
            _macroAxisCandidate = MacroAxisTarget.None;
            _macroAxisCandidateDelta = 0f;
            _macroAxisHoldCounter = 0;
        }

        /// <summary>Per-device axis detection for multi-device combos. Iterates
        /// every assigned device's standard SDL gamepad axes (LX/LY/LT/RX/RY/RT,
        /// indices 0-5) against the per-device baseline captured at recording
        /// start. An axis must hold past <see cref="AxisRecordThreshold"/> for
        /// <see cref="MacroAxisHoldCycles"/> consecutive frames before it's
        /// added to <see cref="_recordedPerDeviceAxisEntries"/>; that prevents
        /// stick-noise false positives. Keyboards / mice are skipped at
        /// baseline-capture time (no held-position axes).</summary>
        private void DetectPerDeviceAxisDeflections()
        {
            if (_perDeviceAxisBaseline == null || _perDeviceAxisCandidates == null) return;

            MacroAxisTarget[] axisMap = {
                MacroAxisTarget.LeftStickX, MacroAxisTarget.LeftStickY,
                MacroAxisTarget.LeftTrigger,
                MacroAxisTarget.RightStickX, MacroAxisTarget.RightStickY,
                MacroAxisTarget.RightTrigger
            };

            foreach (var kv in _perDeviceAxisBaseline)
            {
                var deviceGuid = kv.Key;
                var baseline = kv.Value;
                var ud = FindUserDevice(deviceGuid);
                if (ud == null || !ud.IsOnline || ud.InputState?.Axis == null) continue;
                var axes = ud.InputState.Axis;
                if (!_perDeviceAxisCandidates.TryGetValue(deviceGuid, out var candidate))
                {
                    candidate = new AxisCandidate();
                    _perDeviceAxisCandidates[deviceGuid] = candidate;
                }

                MacroAxisTarget bestTarget = MacroAxisTarget.None;
                float bestDelta = 0f;
                float bestRawDelta = 0f;
                int limit = Math.Min(axisMap.Length, Math.Min(axes.Length, baseline.Length));
                for (int i = 0; i < limit; i++)
                {
                    // Skip axis already recorded on THIS device.
                    if (_recordedPerDeviceAxisEntries.Any(e => e.DeviceGuid == deviceGuid && e.AxisTarget == axisMap[i]))
                        continue;
                    float rawDelta = (axes[i] - baseline[i]) / 65535f;
                    float delta = Math.Abs(rawDelta);
                    if (delta > AxisRecordThreshold && delta > bestDelta)
                    {
                        bestDelta = delta;
                        bestRawDelta = rawDelta;
                        bestTarget = axisMap[i];
                    }
                }

                if (bestTarget != MacroAxisTarget.None)
                {
                    if (bestTarget == candidate.Target)
                    {
                        candidate.HoldCounter++;
                        if (candidate.HoldCounter >= MacroAxisHoldCycles)
                        {
                            // Defaults mirror the merge-mapping recorder: HalfAxis
                            // off, Invert set when the axis deflected in the
                            // negative direction during recording, DeadZone = 50.
                            _recordedPerDeviceAxisEntries.Add(new MacroItem.TriggerInputEntry
                            {
                                DeviceGuid = deviceGuid,
                                AxisTarget = bestTarget,
                                HalfAxis = false,
                                Invert = candidate.RawDelta < 0,
                                DeadZone = 50
                            });
                            candidate.Target = MacroAxisTarget.None;
                            candidate.RawDelta = 0f;
                            candidate.HoldCounter = 0;
                        }
                    }
                    else
                    {
                        candidate.Target = bestTarget;
                        candidate.RawDelta = bestRawDelta;
                        candidate.HoldCounter = 1;
                    }
                }
                else
                {
                    candidate.Target = MacroAxisTarget.None;
                    candidate.RawDelta = 0f;
                    candidate.HoldCounter = 0;
                }
            }
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

            // Axis detection — two paths based on source. OutputController keeps
            // the legacy slot-combined gamepad scan (writes to _recordedAxisTargets).
            // InputDevice scans EACH assigned device's own axis baseline +
            // direction independently, so a multi-device combo can mix a
            // controller stick + a keyboard key + a mouse button. Confirmed
            // per-device axes land in _recordedPerDeviceAxisEntries and get
            // merged into the multi-device entry list below.
            if (_recordingMacro.TriggerSource == MacroTriggerSource.InputDevice)
            {
                DetectPerDeviceAxisDeflections();
            }
            else
            {
                // Read current axis values for delta detection (legacy slot-combined).
                float[] currentAxes = ReadCurrentAxes(
                    _recordingPadIndex, _recordingMacro.TriggerSource, _recordingMacro.ButtonStyle);

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
            }

            if (_recordingMacro.TriggerSource == MacroTriggerSource.InputDevice)
            {
                // Scan raw buttons + POVs from EVERY device assigned to this
                // pad slot. The recorder no longer locks to the first device
                // that fires — instead it accumulates per-device entries so
                // the user can combo a controller button + keyboard key +
                // mouse button into one trigger.
                var currentEntries = new List<MacroItem.TriggerInputEntry>();

                var slotSettings = SettingsManager.UserSettings?.FindByPadIndex(_recordingPadIndex);
                if (slotSettings != null)
                {
                    foreach (var setting in slotSettings)
                    {
                        var ud = FindUserDevice(setting.InstanceGuid);
                        if (ud == null || !ud.IsOnline || ud.InputState == null)
                            continue;

                        var buttons = ud.InputState.Buttons;
                        if (buttons != null)
                        {
                            // Cap by UserDevice.RawButtonCount which combines the
                            // wrapper's RawButtonCount with NumButtons via Max
                            // (keyboards report ~256 = full VK range, mice report
                            // their button count, controllers report their physical
                            // button count).
                            int wrapperCount = ud.RawButtonCount > 0 ? ud.RawButtonCount : buttons.Length;
                            int count = Math.Min(buttons.Length, wrapperCount);
                            for (int i = 0; i < count; i++)
                            {
                                if (buttons[i])
                                    currentEntries.Add(new MacroItem.TriggerInputEntry
                                    {
                                        DeviceGuid = ud.InstanceGuid,
                                        RawButton = i
                                    });
                            }
                        }

                        var povs = ud.InputState.Povs;
                        if (povs != null)
                        {
                            for (int p = 0; p < povs.Length; p++)
                            {
                                if (povs[p] >= 0)
                                    currentEntries.Add(new MacroItem.TriggerInputEntry
                                    {
                                        DeviceGuid = ud.InstanceGuid,
                                        Pov = $"{p}:{povs[p]}"
                                    });
                            }
                        }
                    }
                }

                // Merge confirmed per-device axis entries — these accumulate
                // across frames (held for 3 cycles before being confirmed),
                // unlike buttons/POVs which are rebuilt each frame.
                if (_recordedPerDeviceAxisEntries != null)
                    currentEntries.AddRange(_recordedPerDeviceAxisEntries);

                // Replace the recorded set with the current frame's state.
                // Only update if SOMETHING is pressed (keeps the last combo
                // visible after the user releases all keys).
                if (currentEntries.Count > 0)
                {
                    _recordedInputEntries = currentEntries;
                    // Mirror into the legacy per-device fields for the first
                    // device only — keeps the StopMacroTriggerRecording's
                    // single-device finalize path happy for back-compat. The
                    // multi-device list is the authoritative result.
                    var first = currentEntries[0];
                    _recordingDeviceGuid = first.DeviceGuid;
                    _recordedRawButtons = new HashSet<int>(
                        currentEntries
                            .Where(e => e.DeviceGuid == first.DeviceGuid && e.RawButton >= 0)
                            .Select(e => e.RawButton));
                    _recordedPovs = new HashSet<string>(
                        currentEntries
                            .Where(e => e.DeviceGuid == first.DeviceGuid && !string.IsNullOrEmpty(e.Pov))
                            .Select(e => e.Pov));
                }

                // Live display text — render multi-device combo grouped by
                // device, axes appended at the end (axes still come from the
                // combined Xbox output, not per-device).
                var parts = new List<string>();
                if (_recordedInputEntries != null && _recordedInputEntries.Count > 0)
                {
                    var byDevice = _recordedInputEntries.GroupBy(e => e.DeviceGuid);
                    foreach (var grp in byDevice)
                    {
                        var objects = ResolveDeviceObjects(grp.Key);
                        var inputs = new List<string>();
                        foreach (var entry in grp)
                        {
                            if (entry.RawButton >= 0)
                            {
                                var obj = objects?.FirstOrDefault(o => o.IsButton && o.InputIndex == entry.RawButton);
                                inputs.Add(obj != null && !string.IsNullOrEmpty(obj.Name)
                                    ? obj.Name
                                    : string.Format(Strings.Instance.Macro_Button_Format, entry.RawButton));
                            }
                            else if (!string.IsNullOrEmpty(entry.Pov))
                            {
                                inputs.Add(MacroItem.FormatPovTrigger(entry.Pov));
                            }
                            else if (entry.AxisTarget != MacroAxisTarget.None)
                            {
                                var tags = new List<string>();
                                if (entry.HalfAxis) tags.Add(Strings.Instance.Macro_Axis_Half);
                                if (entry.HalfAxis && entry.Bidirectional) tags.Add(Strings.Instance.Pad_Either.ToLowerInvariant());
                                if (entry.Invert && !(entry.HalfAxis && entry.Bidirectional)) tags.Add(Strings.Instance.Macro_Axis_Inverted);
                                string tagText = tags.Count > 0 ? $" ({string.Join(", ", tags)})" : "";
                                inputs.Add($"{entry.AxisTarget.DisplayName()} > {entry.DeadZone}%{tagText}");
                            }
                        }
                        string deviceName = ResolveDeviceName(grp.Key);
                        parts.Add(!string.IsNullOrEmpty(deviceName)
                            ? deviceName + " [" + string.Join(" + ", inputs) + "]"
                            : string.Join(" + ", inputs));
                    }
                }
                // Legacy slot-combined axes appended at the end — only
                // populated when source=OutputController (per-device axis
                // path is used for InputDevice).
                foreach (var ax in _recordedAxisTargets)
                    parts.Add($"{ax.DisplayName()} > {_recordingMacro.TriggerAxisThreshold}%");

                _recordingMacro.RecordingLiveText = parts.Count > 0
                    ? string.Join(" + ", parts)
                    : Strings.Instance.Macro_LiveRecord_Placeholder;
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
                        ? string.Join(" + ", parts) : Strings.Instance.Macro_LiveRecord_Placeholder;
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
                        ? string.Join(" + ", parts) : Strings.Instance.Macro_LiveRecord_Placeholder;
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

        /// <summary>Resolves a device GUID to a human-readable name,
        /// substituting localized strings for aggregate/overlay devices.</summary>
        private static string ResolveDeviceName(Guid deviceGuid)
        {
            if (deviceGuid == Guid.Empty) return null;
            return LocalizedDeviceName(SettingsManager.FindDeviceByInstanceGuid(deviceGuid));
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

            // Flush MappingItem state into SettingsManager.SlotMappingSets
            // BEFORE deep-cloning the array below. Without this, in-flight UI
            // edits that haven't yet been autosaved (the 250 ms debounce
            // hasn't fired) live only on MappingItem.SourceDescriptor —
            // SlotMappingSets still holds the pre-edit state. Snapshotting
            // here without flushing captures the stale state and silently
            // drops the user's edit when the outgoing profile is saved
            // during a rapid profile-cycle.
            _settingsService?.PushUiExtraSourcesIntoSlotMappingSets();

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

            // Snapshot the per-VC MappingSet array (Issue #61). Profiles
            // round-trip multi-source rows + per-row CombineMode and
            // ShiftActivator alongside the legacy PadSettings. MUST be a
            // DEEP CLONE — reference-copy lets the profile snapshot share
            // MappingSet objects with the live array (and with other
            // profile snapshots taken around the same time), so a runtime
            // mutation in any profile bled across every snapshot that
            // happened to share the ref. Deep cloning isolates each
            // profile's stored MappingSet from every other profile and
            // from the live working set.
            var msSnapshot = new Engine.Data.MappingSet[InputManager.MaxPads];
            for (int s = 0; s < msSnapshot.Length && s < SettingsManager.SlotMappingSets.Length; s++)
                msSnapshot[s] = CloneMappingSetDeep(SettingsManager.SlotMappingSets[s]);

            return new ProfileData
            {
                Entries = entries.ToArray(),
                PadSettings = padSettings.ToArray(),
                SlotMappingSets = msSnapshot,
                SlotCreated = (bool[])SettingsManager.SlotCreated.Clone(),
                SlotEnabled = (bool[])SettingsManager.SlotEnabled.Clone(),
                SlotControllerTypes = Enumerable.Range(0, _mainVm.Pads.Count)
                    .Select(i => (int)_mainVm.Pads[i].OutputType).ToArray(),
                SlotProfileIds = Enumerable.Range(0, _mainVm.Pads.Count)
                    .Select(i => _mainVm.Pads[i].ProfileId).ToArray(),
                ExtendedConfigs = SnapshotExtendedConfigs(),
                MidiConfigs = SnapshotMidiConfigs(),
                KbmConfigs = SnapshotKbmConfigs(),
                // Macros ride profiles (and .pfprofile exports, where the
                // import side's sound-package bundling consumes them).
                Macros = _settingsService?.BuildMacroData(),
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
                TouchpadOverlayHeight = _mainVm.Dashboard.TouchpadOverlayHeight,
                TouchpadGestures = _activeTouchpadGestures.Count > 0
                    ? _activeTouchpadGestures.ToArray()
                    : null
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
                    VendorId = cfg.VendorId,
                    ProductId = cfg.ProductId,
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

        private KbmSlotConfigData[] SnapshotKbmConfigs()
        {
            var list = new List<KbmSlotConfigData>();
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                if (!SettingsManager.SlotCreated[i] ||
                    _mainVm.Pads[i].OutputType != VirtualControllerType.KeyboardMouse)
                    continue;
                var cfg = _mainVm.Pads[i].KbmConfig;
                list.Add(new KbmSlotConfigData
                {
                    SlotIndex = i,
                    SocdMode = cfg.SocdMode,
                    SocdPairs = cfg.SocdPairs
                });
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>Re-entrancy guard for <see cref="CompactSlotsForGaps"/>.
        /// CompactSlotsForGaps drives the compaction by calling ApplyProfile
        /// against a shifted snapshot, and ApplyProfile auto-runs
        /// CompactSlotsForGaps at the end. Without this guard the two would
        /// recurse forever.</summary>
        private bool _compactingSlots;

        /// <summary>
        /// Eliminates gaps in pad indices so the controllers list is
        /// always contiguous from index 0. Triggered after slot deletion,
        /// settings load, and profile apply so legacy gappy data heals
        /// in place. Returns true if compaction ran.
        /// </summary>
        /// <remarks>
        /// Strategy: snapshot the entire current runtime state via
        /// SnapshotCurrentProfile, rewrite every slot-indexed field in
        /// the snapshot (Entries.MapTo, ExtendedConfigs.SlotIndex,
        /// MidiConfigs.SlotIndex, SlotOrder arrays, SlotCreated /
        /// SlotEnabled / SlotMappingSets / SlotControllerTypes /
        /// SlotProfileIds) using a single old→new index map, then
        /// apply that shifted snapshot. ApplyProfile already knows how
        /// to drive the full PadViewModel rebuild — reusing it keeps
        /// the per-property copy logic (config classes, mappings, per-
        /// device tuning) in one place.
        /// </remarks>
        internal bool CompactSlotsForGaps()
        {
            if (_compactingSlots) return false;
            int maxPads = Common.Input.InputManager.MaxPads;

            // Build old→new index map from live SettingsManager state.
            var oldToNew = new Dictionary<int, int>();
            int writeIdx = 0;
            bool needsCompaction = false;
            for (int oldIdx = 0; oldIdx < maxPads; oldIdx++)
            {
                if (SettingsManager.SlotCreated[oldIdx])
                {
                    oldToNew[oldIdx] = writeIdx;
                    if (oldIdx != writeIdx) needsCompaction = true;
                    writeIdx++;
                }
            }
            if (!needsCompaction) return false;

            _compactingSlots = true;
            try
            {
                var snap = SnapshotCurrentProfile();
                CompactProfileDataInPlace(snap, oldToNew, maxPads);

                // Apply the shifted snapshot. ApplyProfile rebuilds every
                // PadViewModel from the new layout. The recursion guard
                // suppresses the ApplyProfile→CompactSlotsForGaps tail call.
                ApplyProfile(snap);

                // Persist the compacted layout so the file no longer has gaps.
                _settingsService?.MarkDirty();
                return true;
            }
            finally
            {
                _compactingSlots = false;
            }
        }

        /// <summary>
        /// Compact a ProfileData snapshot in place using the supplied
        /// old→new index map. Rewrites every slot-indexed array and
        /// every per-element slot-index field. Used both by the live
        /// compaction path and by settings-load to heal gappy profiles
        /// stored on disk.
        /// </summary>
        internal static void CompactProfileDataInPlace(
            ProfileData p,
            Dictionary<int, int> oldToNew,
            int maxPads)
        {
            // Fresh per-slot arrays defaulted to "uncreated", then place each
            // old slot at its new index.
            var newCreated = new bool[maxPads];
            var newEnabled = new bool[maxPads];
            for (int i = 0; i < maxPads; i++) newEnabled[i] = true;
            var newMappingSets = new Engine.Data.MappingSet[maxPads];

            int controllerTypeLen = p.SlotControllerTypes?.Length ?? 0;
            int profileIdLen = p.SlotProfileIds?.Length ?? 0;
            var newControllerTypes = new int[controllerTypeLen];
            var newProfileIds = new string[profileIdLen];
            for (int i = 0; i < newProfileIds.Length; i++) newProfileIds[i] = "";

            foreach (var (oldIdx, newIdx) in oldToNew)
            {
                newCreated[newIdx] = true;
                if (p.SlotEnabled != null && oldIdx < p.SlotEnabled.Length)
                    newEnabled[newIdx] = p.SlotEnabled[oldIdx];
                if (p.SlotMappingSets != null && oldIdx < p.SlotMappingSets.Length)
                    newMappingSets[newIdx] = p.SlotMappingSets[oldIdx];
                if (oldIdx < controllerTypeLen && newIdx < controllerTypeLen)
                    newControllerTypes[newIdx] = p.SlotControllerTypes[oldIdx];
                if (oldIdx < profileIdLen && newIdx < profileIdLen)
                    newProfileIds[newIdx] = p.SlotProfileIds[oldIdx];
            }

            p.SlotCreated = newCreated;
            p.SlotEnabled = newEnabled;
            p.SlotMappingSets = newMappingSets;
            if (p.SlotControllerTypes != null) p.SlotControllerTypes = newControllerTypes;
            if (p.SlotProfileIds != null) p.SlotProfileIds = newProfileIds;

            if (p.Entries != null)
            {
                foreach (var entry in p.Entries)
                    if (oldToNew.TryGetValue(entry.MapTo, out var ni))
                        entry.MapTo = ni;
            }
            if (p.ExtendedConfigs != null)
            {
                foreach (var cfg in p.ExtendedConfigs)
                    if (oldToNew.TryGetValue(cfg.SlotIndex, out var ni))
                        cfg.SlotIndex = ni;
            }
            if (p.MidiConfigs != null)
            {
                foreach (var cfg in p.MidiConfigs)
                    if (oldToNew.TryGetValue(cfg.SlotIndex, out var ni))
                        cfg.SlotIndex = ni;
            }
            if (p.KbmConfigs != null)
            {
                foreach (var cfg in p.KbmConfigs)
                    if (oldToNew.TryGetValue(cfg.SlotIndex, out var ni))
                        cfg.SlotIndex = ni;
            }
            RemapSlotOrder(p.XboxSlotOrder, oldToNew);
            RemapSlotOrder(p.PlayStationSlotOrder, oldToNew);
            RemapSlotOrder(p.ExtendedSlotOrder, oldToNew);
            RemapSlotOrder(p.KeyboardMouseSlotOrder, oldToNew);
            RemapSlotOrder(p.MidiSlotOrder, oldToNew);
        }

        /// <summary>
        /// Build the old→new index map for a ProfileData. Returns the map
        /// and whether any shift is actually needed. Created slots map to
        /// sequential indices starting at 0; uncreated slots aren't in the map.
        /// </summary>
        internal static (Dictionary<int, int> map, bool needsCompaction) BuildCompactionMap(ProfileData p)
        {
            var map = new Dictionary<int, int>();
            if (p.SlotCreated == null) return (map, false);
            int writeIdx = 0;
            bool needs = false;
            for (int oldIdx = 0; oldIdx < p.SlotCreated.Length; oldIdx++)
            {
                if (p.SlotCreated[oldIdx])
                {
                    map[oldIdx] = writeIdx;
                    if (oldIdx != writeIdx) needs = true;
                    writeIdx++;
                }
            }
            return (map, needs);
        }

        private static void RemapSlotOrder(int[] arr, Dictionary<int, int> oldToNew)
        {
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
                if (oldToNew.TryGetValue(arr[i], out var ni))
                    arr[i] = ni;
        }

        /// <summary>
        /// Loads a profile's PadSettings and slot assignments into the runtime state.
        /// For each ProfileEntry, finds the matching UserSetting and swaps its
        /// PadSetting and MapTo slot.
        /// </summary>
        /// <summary>Merges the profile's custom touchpad gestures with
        /// the in-box shape catalog and hands the combined list to the
        /// InputManager. Called from ApplyProfile and on initial
        /// LoadFromFile after the default profile snapshot is
        /// established. Null / empty TouchpadGestures arrays still
        /// install the in-box catalog so the engine evaluates Circle /
        /// Square / etc. when the per-pad EnableShapeGestures toggle
        /// is on.</summary>
        private void ApplyProfileTouchpadGestures(ProfileData profile)
        {
            if (_inputManager == null) return;

            // Re-seed the in-memory working list from the profile so
            // AddCustomTouchpadGesture / DeleteCustomTouchpadGesture
            // mutate the same list that the snapshot path picks up.
            _activeTouchpadGestures.Clear();
            if (profile?.TouchpadGestures != null)
            {
                foreach (var g in profile.TouchpadGestures)
                {
                    if (g == null) continue;
                    _activeTouchpadGestures.Add(g);
                }
            }

            var templates = new List<PadForge.Engine.Touchpad.ShapeTemplate>(
                PadForge.Engine.Touchpad.InBoxShapeTemplates.Build());
            foreach (var g in _activeTouchpadGestures)
            {
                var tpl = g.ToTemplate();
                if (tpl != null) templates.Add(tpl);
            }
            _inputManager.SetShapeTemplates(templates);

            // Mirror the profile's custom-gesture list onto every PadViewModel
            // so the Touchpad tab's Custom Gestures card reflects the new
            // profile's library. PadViewModel scopes per-slot, but the
            // gesture library is profile-wide, so every PadVM gets the same
            // list. Also refresh each slot's mapping-row dropdown so the
            // custom gestures show up in the input picker after a profile
            // switch / initial load.
            try
            {
                foreach (var padVm in _mainVm.Pads)
                {
                    padVm?.RefreshCustomTouchpadGestures(_activeTouchpadGestures);
                    if (padVm != null) RefreshAvailableInputsForSlot(padVm);
                }
            }
            catch { /* refresh is cosmetic — ignore VM enumeration races */ }
        }

        /// <summary>Add a recorded custom gesture to the active profile's
        /// library. Replaces any existing gesture with the same Name
        /// (case-insensitive). Rebuilds the engine's shape-template
        /// catalog, refreshes every Pad page's Custom Gestures list,
        /// and marks settings dirty so the next save persists it.</summary>
        public void AddCustomTouchpadGesture(PadForge.Engine.Touchpad.TouchpadCustomGesture gesture)
        {
            if (gesture == null || string.IsNullOrWhiteSpace(gesture.Name)) return;
            // Remove same-named duplicates (case-insensitive) — overwrite
            // semantics keeps the working list as a unique-name keyed set.
            _activeTouchpadGestures.RemoveAll(g =>
                g != null && string.Equals(g.Name, gesture.Name, StringComparison.OrdinalIgnoreCase));
            _activeTouchpadGestures.Add(gesture);
            RebuildShapeTemplatesFromWorkingList();
            try
            {
                foreach (var padVm in _mainVm.Pads)
                {
                    padVm?.RefreshCustomTouchpadGestures(_activeTouchpadGestures);
                    // Re-populate the slot's mapping-row dropdown so the
                    // new gesture is selectable immediately after recording
                    // (without waiting for a device-assignment refresh).
                    if (padVm != null) RefreshAvailableInputsForSlot(padVm);
                }
            }
            catch { }
            _settingsService?.MarkDirty();
        }

        /// <summary>Remove a recorded custom gesture by name. Idempotent
        /// when the name isn't in the working list.</summary>
        public void DeleteCustomTouchpadGesture(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            int removed = _activeTouchpadGestures.RemoveAll(g =>
                g != null && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return;
            RebuildShapeTemplatesFromWorkingList();
            try
            {
                foreach (var padVm in _mainVm.Pads)
                {
                    padVm?.RefreshCustomTouchpadGestures(_activeTouchpadGestures);
                    if (padVm != null) RefreshAvailableInputsForSlot(padVm);
                }
            }
            catch { }
            _settingsService?.MarkDirty();
        }

        private void RebuildShapeTemplatesFromWorkingList()
        {
            if (_inputManager == null) return;
            var templates = new List<PadForge.Engine.Touchpad.ShapeTemplate>(
                PadForge.Engine.Touchpad.InBoxShapeTemplates.Build());
            foreach (var g in _activeTouchpadGestures)
            {
                var tpl = g.ToTemplate();
                if (tpl != null) templates.Add(tpl);
            }
            _inputManager.SetShapeTemplates(templates);
        }

        /// <summary>Snapshot of the active profile's custom-gesture list.
        /// Returns a copy, so callers may not mutate the working set.</summary>
        public PadForge.Engine.Touchpad.TouchpadCustomGesture[] GetActiveTouchpadGestures()
            => _activeTouchpadGestures.ToArray();

        /// <summary>Maps a UserDevice to one of the canonical
        /// device-class labels custom gestures use for their
        /// <see cref="PadForge.Engine.Touchpad.TouchpadCustomGesture.DeviceClass"/>
        /// filter. Returns "any" when no specific class is recognized so
        /// gesture matching falls through to the unrestricted path.</summary>
        private static string ResolveDeviceClass(PadForge.Engine.Data.UserDevice ud)
        {
            if (ud == null) return "any";
            if (ud.IsTouchpad) return "overlay";  // built-in TouchpadOverlay
            // Sony — VID 054C.
            if (ud.VendorId == 0x054C)
            {
                ushort pid = ud.ProdId;
                if (pid == 0x0CE6 || pid == 0x0DF2) return "dualsense";
                if (pid == 0x05C4 || pid == 0x09CC || pid == 0x0BA0) return "ds4";
            }
            // Valve — VID 28DE.
            if (ud.VendorId == 0x28DE)
            {
                ushort pid = ud.ProdId;
                if (pid == 0x1205) return "steamdeck";   // Deck OLED + LCD
                if (pid == 0x11FF) return "steamcontroller";
                if (pid == 0x35F0 || pid == 0x35F1) return "triton"; // Steam Controller 2026
            }
            return "any";
        }

        /// <summary>Hand the recorder dialog a live stream of finger
        /// snapshots from the supplied (device, pad). While the target
        /// is set, normal gesture recognition for that pad is bypassed
        /// (so drawing custom shapes doesn't spam fires). Pass null
        /// + Guid.Empty + -1 to clear.</summary>
        public void SetTouchpadRecordingTarget(Guid deviceGuid, int padIdx,
            Action<PadForge.Engine.TouchpadInputState> onTick)
        {
            _inputManager?.SetRecordingTarget(deviceGuid, padIdx, onTick);
        }

        public void ClearTouchpadRecordingTarget()
        {
            _inputManager?.ClearRecordingTarget();
        }

        public void ApplyProfile(ProfileData profile)
        {
            if (profile == null)
                return;

            // Clear shift-activator runtime state (toggle latches, was-down
            // markers, engagement stack, custom-layer state) so the new
            // profile starts every activator un-engaged. Without this a
            // held activator at swap time can leave the new profile mid-
            // engagement and the wrong layer effective from frame zero.
            Common.Input.InputManager.ClearAllShiftRuntime();

            // Restore per-VC MappingSet from the profile snapshot
            // (Issue #61). Multi-source rows + per-row CombineMode +
            // ShiftActivator round-trip with the profile. Profiles
            // captured before multi-source landed have null
            // SlotMappingSets — leave the live array untouched in that
            // case so it falls back to whatever the loader (legacy
            // migration or persisted-XML state) set up.
            // DEEP CLONE on apply so live mutations (auto-map on device
            // reassignment, in-tab edits) don't poison the profile's
            // stored snapshot.
            if (profile.SlotMappingSets != null)
            {
                var live = SettingsManager.SlotMappingSets;
                for (int s = 0; s < live.Length && s < profile.SlotMappingSets.Length; s++)
                    live[s] = CloneMappingSetDeep(profile.SlotMappingSets[s]);
            }

            // Rebuild the gesture engine's shape-template catalog for
            // the new profile: in-box catalog + this profile's custom
            // user-recorded templates compiled via TouchpadCustomGesture.ToTemplate().
            // Atomic swap on the InputManager so the polling thread
            // never sees a half-built catalog mid-tick.
            ApplyProfileTouchpadGestures(profile);

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
                        cfg.VendorId = cfgData.VendorId;
                        cfg.ProductId = cfgData.ProductId;
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

            if (profile.KbmConfigs != null)
            {
                foreach (var cfgData in profile.KbmConfigs)
                {
                    int idx = cfgData.SlotIndex;
                    if (idx >= 0 && idx < _mainVm.Pads.Count &&
                        SettingsManager.SlotCreated[idx] &&
                        _mainVm.Pads[idx].OutputType == VirtualControllerType.KeyboardMouse)
                    {
                        var cfg = _mainVm.Pads[idx].KbmConfig;
                        cfg.SocdMode = cfgData.SocdMode;
                        cfg.SocdPairs = cfgData.SocdPairs;
                    }
                }
            }

            // ── Apply macros ──
            // Profiles carry their macro set, so switching profiles switches
            // macros (and a shared .pfprofile brings its macros along). Null
            // means the profile was saved before macros rode profiles: leave
            // the live macros untouched so legacy profiles keep the old
            // "macros are global" behavior instead of wiping them. Applied
            // after the Extended configs above so each macro rebuilds with
            // the correct per-pad button style and count.
            if (profile.Macros != null)
                _settingsService?.LoadMacros(profile.Macros);

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
            // LoadPadSettingToViewModel loads per-device TUNING only; mapping
            // rows MUST also be refreshed from the just-swapped SlotMappingSets
            // or the next autosave's PushUiExtraSourcesIntoSlotMappingSets will
            // rebuild the live MappingSet from the OUTGOING profile's stale
            // MappingItems and silently clobber the incoming profile.
            //
            // ORDER MATTERS: RefreshMappingsToViewModel sets the new
            // MappingItem.SourceDescriptor values. PopulateAvailableInputs
            // then calls SyncSelectedInputFromDescriptor on each MappingItem
            // so the per-row ComboBox SelectedInput resolves against the
            // FRESH descriptors. Doing them in the reverse order leaves
            // SelectedInput stale (synced against the previous profile's
            // descriptors), so even though the underlying data is correct,
            // the picker cells render blank until something else triggers
            // a re-sync (toggling the assigned-device dropdown was the
            // symptom — that path runs PopulateAvailableInputs again).
            for (int i = 0; i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                var selected = padVm.SelectedMappedDevice;
                if (selected != null && selected.InstanceGuid != Guid.Empty)
                    LoadPadSettingToViewModel(padVm, selected.InstanceGuid);

                // Rebuild the shift-layer tab strip from the slot's
                // ShiftActivators list so the nested Mappings tabs reflect
                // the profile's authored layers. Reset active layer back
                // to Base for cleanliness — held-button continuity across
                // profile swap is intentionally not preserved.
                padVm.ActiveLayerMask = "Base";
                var slotMs = (i >= 0 && i < SettingsManager.SlotMappingSets.Length)
                    ? SettingsManager.SlotMappingSets[i]
                    : null;
                padVm.RebuildLayerTabs(slotMs?.ShiftActivators);

                RefreshMappingsToViewModel(padVm);
                // Rebuild the picker for EVERY slot against the
                // post-switch device set, not only slots with a selected
                // device. Gating this on the selection left device-less
                // slots (a Workshop import carries no device assignments
                // by design) rendering the OUTGOING profile's
                // AvailableInputs: the owner-reported phantom "Xbox
                // Series X controller" groups on unassigned imported
                // slots. With no devices the populate emits the
                // device-agnostic "(Any device)" choices, so the
                // imported abstract "Gamepad ..." rows stay editable.
                PopulateAvailableInputs(padVm,
                    selected != null && selected.InstanceGuid != Guid.Empty
                        ? FindUserDevice(selected.InstanceGuid)
                        : null);
            }

            // Refresh Devices page slot labels.
            SyncDevicesList();

            // Compact any gaps the profile carried over from legacy data.
            // Profile snapshots saved before compaction-on-delete landed may
            // have slots at non-contiguous indices. Compact now so the live
            // state never has gaps, then mark the file dirty so the healed
            // layout persists. Guarded against re-entry because the
            // CompactSlotsForGaps path drives compaction BY calling
            // ApplyProfile with a shifted snapshot.
            if (!_compactingSlots)
                CompactSlotsForGaps();
        }

        /// <summary>
        /// ForegroundMonitorService event shim (#175 item 8): applies the
        /// switch, then raises <see cref="AutoProfileSwitchApplied"/> only
        /// when the active profile actually changed (the target may be
        /// missing or already active). Manual paths call
        /// <see cref="OnProfileSwitchRequired"/> directly and never flare.
        /// </summary>
        private void OnAutoProfileSwitchRequired(string profileId)
        {
            string before = SettingsManager.ActiveProfileId;
            OnProfileSwitchRequired(profileId);
            if (SettingsManager.ActiveProfileId != before)
                AutoProfileSwitchApplied?.Invoke();
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
                    // Drop stateful source-kind accumulators (Incremental
                    // cruise/ramp throttle), shift-toggle latches, and the
                    // gyro engage stickies so the new profile starts neutral.
                    Common.Input.InputManager.ClearSourceKindRuntime();
                    Common.Input.InputManager.ClearAllShiftRuntime();
                    _inputManager?.ResetGyroEngageStates();
                    _inputManager?.ResetTriggerRouteEngageStates();
                    _inputManager?.ResetGestureContexts();
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
                Common.Input.InputManager.ClearSourceKindRuntime();
                Common.Input.InputManager.ClearAllShiftRuntime();
                _inputManager?.ResetGyroEngageStates();
                _inputManager?.ResetTriggerRouteEngageStates();
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
                    profile.SlotMappingSets = snapshot.SlotMappingSets;
                    profile.SlotCreated = snapshot.SlotCreated;
                    profile.SlotEnabled = snapshot.SlotEnabled;
                    profile.SlotControllerTypes = snapshot.SlotControllerTypes;
                    profile.SlotProfileIds = snapshot.SlotProfileIds;
                    profile.ExtendedConfigs = snapshot.ExtendedConfigs;
                    profile.MidiConfigs = snapshot.MidiConfigs;
                    profile.KbmConfigs = snapshot.KbmConfigs;
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
                    profile.TouchpadGestures = snapshot.TouchpadGestures;
                    // Identity members (Id, Name, ExecutableNames, WorkshopSource)
                    // are intentionally NOT copied. SnapshotCurrentProfile never
                    // captures them, so copying would null them on the stored
                    // profile. Keep them off this list.
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
        public void OnSlotDeleted(int padIndex, VirtualControllerType deletedType, int oldGroupPosition, bool deletedSlotHadActiveVc = true)
        {
            if (_inputManager != null && deletedSlotHadActiveVc)
            {
                RunBubbleDownCascadeAfterDelete(deletedType, oldGroupPosition);
            }

            // Compact slot indices so the controllers list stays
            // contiguous from index 0. CompactSlotsForGaps drives the
            // PadViewModel rebuild via ApplyProfile when it actually runs,
            // so only fall through to RefreshAfterSlotReorder when there
            // were no gaps to compact (the typical case when deleting the
            // last-created slot).
            if (!CompactSlotsForGaps())
                RefreshAfterSlotReorder();

            // Player-identity idle floor (#191): every surviving slot's
            // display number may have shifted.
            ReseedPlayerIdentities();
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

            // Player-identity idle floor (#191): reorders renumber slots.
            ReseedPlayerIdentities();
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
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            // NFC teardown (cancel the monitor thread + release the PC/SC
            // context, #150) is owned by InputManager.Dispose, which Stop()
            // invokes while the device list is still alive and the poll loop is
            // already halted. Doing it here, after Stop() disposed the manager,
            // risked a use-after-dispose (round-4 finding), so it lives there.
            try { Stop(); } catch { /* Best effort on shutdown */ }
            GC.SuppressFinalize(this);
        }

        /// <summary>Builds a <see cref="Gamepad"/> for the per-device
        /// Sticks/Triggers tab preview. Triggers come from the engine's
        /// per-device <see cref="UserSetting.RawMappedState"/> (descriptor-
        /// resolved + inverted, pre-tuning); sticks come from the physical
        /// device's raw <see cref="UserDevice.InputState"/> axes. Bypasses
        /// the post-tuning output so each physical device shows its own
        /// stick/trigger position in a multi-device slot.
        /// Stick layout per <see cref="SdlDeviceWrapper.GetGamepadState"/>:
        /// Axis[0]=LX, [1]=LY, [3]=RX, [4]=RY (unsigned 0..65535).</summary>
        private Gamepad BuildPerDeviceSticksFromInputState(Guid instanceGuid, UserSetting us = null)
        {
            var ud = FindUserDevice(instanceGuid);
            var devState = ud?.InputState;
            Gamepad gp = default;

            // Triggers: read RawMappedState (axis-selected + inverted, PRE-tuning)
            // so the Triggers-tab preview honors the user's actual LeftTrigger/
            // RightTrigger descriptors and their Invert flag. A wheel pedal mapped
            // to "IAxis 3" then rests at released (0), not raw 65535, and the brake
            // on "IAxis 2" drives the right trigger — the old fixed Axis[2]/Axis[5]
            // read showed the wrong (un-inverted) pedal and left the other trigger
            // dead. RawMappedState is pre-tuning, so the UI's ProcessTriggerForPreview
            // re-applies deadzone/curve without double-processing. For default gamepad
            // maps (LeftTrigger="Axis 2", RightTrigger="Axis 5") this is value-identical
            // to the old raw read, so XInput-shaped pads don't change.
            if (us != null)
            {
                // Per-device trigger preview. Evaluate THIS device's own trigger
                // sources directly rather than reading us.RawMappedState: a
                // multi-source (e.g. MaxAbs) LeftTrigger/RightTrigger row is
                // evaluated once per slot in Step 3, so its combined value lands
                // on only the first-evaluated device's RawMappedState, leaving a
                // secondary-source device's preview blank (the Triggers-tab "left
                // trigger doesn't preview when it's a secondary mapping" bug).
                // Single-source / legacy slots have no MappingSet, so keep reading
                // RawMappedState there (already per-device-correct).
                MappingSet ms = (us.MapTo >= 0 && us.MapTo < SettingsManager.SlotMappingSets.Length)
                    ? SettingsManager.SlotMappingSets[us.MapTo] : null;
                if (ms != null && devState != null)
                {
                    string g = instanceGuid.ToString();
                    gp.LeftTrigger  = InputManager.EvaluatePerDeviceTriggerPreview(devState, ms, g, "LeftTrigger",  us.MapTo);
                    gp.RightTrigger = InputManager.EvaluatePerDeviceTriggerPreview(devState, ms, g, "RightTrigger", us.MapTo);
                }
                else
                {
                    gp.LeftTrigger = us.RawMappedState.LeftTrigger;
                    gp.RightTrigger = us.RawMappedState.RightTrigger;
                }
            }

            // A mouse has no absolute stick axes. Its raw axes are relative motion
            // deltas (Mouse Speed X/Y) and it has no right-stick axes at all, so the
            // raw-axis read below would show delta motion on the left stick and pin
            // the right stick to a corner (axes 3/4 read 0, which is full deflection,
            // not center). The #107 "Mouse Position" source is a separate absolute
            // cursor signal that reaches the sticks only through the mapping, and
            // RawMappedState can't be reused here: a stick target that combines the
            // mouse with another device is a multi-source row, evaluated once per
            // slot, so the mouse's copy reads center. Read the live cursor directly
            // for each stick axis the mouse maps a Mouse Position source to, and
            // leave the rest centered.
            if (us != null && ud?.CapType == InputDeviceType.Mouse)
            {
                int slot = us.MapTo;
                MappingSet ms = (slot >= 0 && slot < SettingsManager.SlotMappingSets.Length)
                    ? SettingsManager.SlotMappingSets[slot] : null;
                string mouseGuid = instanceGuid.ToString();
                gp.ThumbLX = MouseCursorStickValue(ms, "LeftThumbAxisX", mouseGuid);
                gp.ThumbLY = MouseCursorStickValue(ms, "LeftThumbAxisY", mouseGuid);
                gp.ThumbRX = MouseCursorStickValue(ms, "RightThumbAxisX", mouseGuid);
                gp.ThumbRY = MouseCursorStickValue(ms, "RightThumbAxisY", mouseGuid);
                return gp;
            }

            if (devState?.Axis == null || devState.Axis.Length < 6)
                return gp;

            // Sticks: 0..65535 → signed -32768..32767. Y axes are negated: SDL
            // convention is positive-down (screen coords); Gamepad/XInput convention
            // is positive-up. The standard SDL→Gamepad path applies the same negation
            // (SdlDeviceWrapper -> MapToThumbAxisWithNeg via PadSetting).
            // NOTE: this still assumes the XInput stick-axis layout, so a wheel/
            // joystick with remapped stick axes can mis-preview sticks; triggers
            // were the reported case and are fixed above.
            gp.ThumbLX = (short)(devState.Axis[0] + short.MinValue);
            gp.ThumbLY = (short)Math.Clamp(-(devState.Axis[1] + short.MinValue), short.MinValue, short.MaxValue);
            gp.ThumbRX = (short)(devState.Axis[3] + short.MinValue);
            gp.ThumbRY = (short)Math.Clamp(-(devState.Axis[4] + short.MinValue), short.MinValue, short.MaxValue);
            return gp;
        }

        /// <summary>Live #107 cursor value for a stick target the mouse maps a
        /// "Mouse Position" source to, in XInput short convention. Returns center (0)
        /// when the mouse doesn't drive that axis. Mirrors
        /// <c>SourceCoercion.ReadTunedMouseCursor</c> (component select, per-source
        /// sensitivity, clamp, Invert) and <c>WriteBipolarAxisTarget</c> (Y negated),
        /// so the Sticks-tab preview tracks the cursor on whatever stick it's mapped
        /// to without depending on per-slot multi-source dedup.</summary>
        private static short MouseCursorStickValue(MappingSet ms, string target, string mouseGuid)
        {
            if (ms?.Rows == null) return 0;
            var provider = PadForge.Engine.Common.Mapping.SourceCoercion.MouseCursorProvider;
            if (provider == null) return 0;

            bool negateY = target == "LeftThumbAxisY" || target == "RightThumbAxisY";
            for (int r = 0; r < ms.Rows.Count; r++)
            {
                var row = ms.Rows[r];
                if (row?.Target != target || row.Sources == null) continue;
                for (int s = 0; s < row.Sources.Count; s++)
                {
                    var src = row.Sources[s];
                    if (src == null) continue;
                    if (!string.Equals(src.DeviceGuid ?? "", mouseGuid, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!PadForge.Engine.Common.Mapping.SourceCoercion.IsMouseCursorDescriptor(src.Descriptor)) continue;

                    var (nx, ny) = provider();
                    string d = src.Descriptor ?? "";
                    float v = d.EndsWith(" X", StringComparison.Ordinal) ? nx
                            : d.EndsWith(" Y", StringComparison.Ordinal) ? ny : 0f;
                    v *= (float)src.MouseCursorSensitivity;
                    if (v < -1f) v = -1f; else if (v > 1f) v = 1f;
                    if (src.Invert) v = -v;
                    if (negateY) v = -v;
                    return (short)Math.Clamp((int)(v * 32767f), short.MinValue, short.MaxValue);
                }
            }
            return 0;
        }
    }
}
