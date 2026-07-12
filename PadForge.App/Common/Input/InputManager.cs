using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using SDL3;
using static SDL3.SDL;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Central input manager that runs the device polling pipeline on a background thread.
    /// 
    /// Pipeline (runs at ~1000Hz on a background thread):
    ///   Step 1: Enumerate SDL devices, open new ones, close disconnected ones
    ///   Step 2: Read input states from SDL
    ///   Step 3: Map CustomInputState → OutputState via PadSetting rules
    ///   Step 4: Combine multiple devices per virtual controller slot
    ///   Step 5: Feed virtual controllers (HIDMaestro for Xbox / PlayStation / Extended, plus MIDI and KB+M)
    ///   Step 6: Copy combined output states for UI display
    /// 
    /// Thread safety: the background thread writes UserDevice.InputState (atomic reference swap).
    /// The UI thread reads it. Collection modifications to UserDevices use SyncRoot locking.
    /// </summary>
    public partial class InputManager : IDisposable
    {
        // ─────────────────────────────────────────────
        //  Constants
        // ─────────────────────────────────────────────

        /// <summary>Target polling interval in milliseconds. Default 1ms (~1000Hz).
        /// Higher values reduce CPU usage at the cost of input latency.</summary>
        public int PollingIntervalMs { get; set; } = 1;

        /// <summary>Seconds of all-mapped-devices-offline before an HM
        /// virtual controller is destroyed and its slot removed.  0 disables
        /// (HM VCs survive arbitrary offline windows).  Default 60.  When
        /// the destroy fires, surviving HM VCs in the stack bubble down via
        /// the bubble-up cascade so XInput indices stay contiguous.</summary>
        public int HmInactivityTimeoutSeconds { get; set; } = 60;

        /// <summary>Raised on the polling thread when an HM VC has reached
        /// its inactivity timeout.  Listener (MainWindow) marshals to the
        /// UI thread and runs DeviceService.DeleteSlot + InputService.OnSlotDeleted with
        /// the bubble-up cascade.  Argument is the pad index that timed
        /// out.</summary>
        public event System.EventHandler<int> HmVcInactivityDestroyed;

        /// <summary>Raised on the polling thread whenever an HM-backed
        /// slot (Xbox / PlayStation / Extended) has its live VC torn down
        /// for any non-delete reason — sidebar disable, all devices
        /// explicitly unassigned, or the HM inactivity timeout firing.
        /// The slot stays in its group's order list at the same position;
        /// only the live VC is gone.  Listener (InputService) marshals to
        /// the UI thread and runs the bubble-down cascade for surviving
        /// HM VCs at higher positions in the same subgroup so external
        /// observers re-bind kernel slots in compact ascending order.
        /// Argument is the pad index that went non-active.</summary>
        public event System.EventHandler<int> HmVcWentNonActive;

        /// <summary>Device re-enumeration interval in milliseconds (every 2 seconds).</summary>
        private const int EnumerationIntervalMs = 2000;


        /// <summary>Maximum number of virtual controller slots.</summary>
        public const int MaxPads = 16;

        // ─────────────────────────────────────────────
        //  State
        // ─────────────────────────────────────────────

        private Thread _pollingThread;
        // Injects accumulated macro mouse-move delta with a single SendInput per
        // tick, off the poll thread. See FlushPendingMouseMove: SendInput on the
        // 1000 Hz poll thread let a mouse-move macro drop the poll rate to ~200 Hz.
        private Thread _mouseInjectorThread;
        private volatile bool _running;
        private volatile bool _idle;
        private bool _sdlInitialized;
        private bool _disposed;

        /// <summary>Precision touchpad reader for laptop PTP input.</summary>
        private PrecisionTouchpadReader _ptpReader;

        /// <summary>Stopwatch for timing enumeration intervals.</summary>
        private readonly Stopwatch _enumerationTimer = new Stopwatch();

        /// <summary>Stopwatch for frequency measurement.</summary>
        private readonly Stopwatch _frequencyTimer = new Stopwatch();
        private int _frequencyCounter;

        // ── Pre-allocated snapshot buffers for hot path (avoid LINQ allocations) ──
        private UserDevice[] _deviceSnapshotBuffer = new UserDevice[16];
        private UserSetting[] _settingSnapshotBuffer = new UserSetting[16];
        private readonly UserSetting[] _padIndexBuffer = new UserSetting[MaxPads];
        private readonly UserSetting[] _instanceGuidBuffer = new UserSetting[MaxPads];

        /// <summary>
        /// Combined output gamepad states for the virtual controller slots.
        /// Written by Step 4 (background thread), read by UI (InputService).
        /// </summary>
        public Gamepad[] CombinedOutputStates { get; } = new Gamepad[MaxPads];

        /// <summary>
        /// Combined Extended raw output states for custom Extended slots.
        /// Written by Step 4 (background thread), read by Step 5.
        /// </summary>
        public ExtendedRawState[] CombinedExtendedRawStates { get; } = new ExtendedRawState[MaxPads];

        /// <summary>
        /// Combined MIDI raw output states for MIDI slots.
        /// Written by Step 4 (background thread), read by Step 5.
        /// </summary>
        public MidiRawState[] CombinedMidiRawStates { get; } = new MidiRawState[MaxPads];

        /// <summary>
        /// Combined KBM raw output states for KeyboardMouse slots.
        /// Written by Step 4 (background thread), read by Step 5.
        /// </summary>
        public KbmRawState[] CombinedKbmRawStates { get; } = new KbmRawState[MaxPads];

        /// <summary>
        /// Combined touchpad states for PlayStation slots.
        /// Written by Step 4 (background thread), read by Step 5.
        /// </summary>
        public TouchpadState[] CombinedTouchpadStates { get; } = new TouchpadState[MaxPads];

        /// <summary>Per-slot raw physical touchpad-click flag (any assigned device's
        /// SDL_GAMEPAD_BUTTON_TOUCHPAD = InputState.Buttons[16]), OR'd across devices each
        /// frame in Step 3 regardless of virtual-controller type or click mapping. The
        /// InputReactive lightbar reads this so a touchpad press flashes even on a
        /// non-PlayStation virtual (where TouchpadOutputState is never computed).</summary>
        public bool[] SlotRawTouchpadClick { get; } = new bool[MaxPads];

        /// <summary>Per-(slot, device, touchpad-pad-index) gesture
        /// recognizer state. Slot-keyed so two slots sharing one
        /// physical touchpad each keep their own context and settings —
        /// slot 1's "4-way OFF" toggle truly stops slot 1's mapping
        /// rows from receiving 4-way swipes even when slot 0 has 4-way
        /// ON. Lazily populated when a device with a touchpad is
        /// assigned to a slot; read by <see cref="UpdateGestureContexts"/>
        /// each tick and by the SourceCoercion gesture-fired /
        /// gesture-axis providers during mapping evaluation. Cleared on
        /// device disconnect and on profile switch so a stale partial
        /// gesture doesn't carry across profiles.</summary>
        public readonly System.Collections.Concurrent.ConcurrentDictionary<(int Slot, System.Guid DeviceId, int PadIdx), Engine.Touchpad.TouchpadGestureContext> GestureContexts
            = new System.Collections.Concurrent.ConcurrentDictionary<(int, System.Guid, int), Engine.Touchpad.TouchpadGestureContext>();

        /// <summary>Active shape templates ready for the gesture
        /// engine's point-cloud matcher (<see cref="Engine.Touchpad.ShapeRecognizer"/>).
        /// Built at startup from the in-box catalog; profile load can
        /// append the user's custom gestures via
        /// <see cref="SetShapeTemplates"/>.</summary>
        public System.Collections.Generic.IReadOnlyList<Engine.Touchpad.ShapeTemplate> ShapeTemplates
        {
            get => System.Threading.Volatile.Read(ref _shapeTemplates);
        }
        private System.Collections.Generic.IReadOnlyList<Engine.Touchpad.ShapeTemplate> _shapeTemplates
            = Engine.Touchpad.InBoxShapeTemplates.Build();

        /// <summary>Atomically swaps the active shape-template catalog.
        /// Called on profile load when the per-profile custom gesture
        /// library lands; passes in the in-box set merged with the
        /// custom templates so the recognizer evaluates both.</summary>
        public void SetShapeTemplates(System.Collections.Generic.IReadOnlyList<Engine.Touchpad.ShapeTemplate> templates)
        {
            System.Threading.Volatile.Write(ref _shapeTemplates, templates ?? Engine.Touchpad.InBoxShapeTemplates.Build());
        }

        /// <summary>Per-(slot, device, padIdx) gesture settings provider.
        /// Wired by the App layer against the slot's
        /// <c>PadSetting.TouchpadSettings</c> via a UserSettings walk
        /// filtered by both <c>MapTo == slot</c> and
        /// <c>InstanceGuid == device</c>. Returns
        /// <see cref="Engine.Touchpad.TouchpadGestureSettings.Default"/>
        /// when unwired or when no per-pad settings exist for the
        /// requested slot + device + pad.</summary>
        public System.Func<int, System.Guid, int, Engine.Touchpad.TouchpadGestureSettings> TouchpadGestureSettingsProvider { get; set; }

        /// <summary>Per-(slot, device) mouse-gesture recognizer contexts
        /// (issue #200), twin of <see cref="GestureContexts"/> minus the pad
        /// index (a mouse has one motion surface). Same lifecycle: lazy
        /// creation on the polling thread, cleared wholesale on profile
        /// switch via <see cref="ResetGestureContexts"/>. Offline devices
        /// leave frozen contexts; consumers that must not latch (the macro
        /// trigger path) apply the same online guard the touchpad lane
        /// uses.</summary>
        public readonly System.Collections.Concurrent.ConcurrentDictionary<(int Slot, System.Guid DeviceId), Engine.Mouse.MouseGestureContext> MouseGestureContexts
            = new System.Collections.Concurrent.ConcurrentDictionary<(int, System.Guid), Engine.Mouse.MouseGestureContext>();

        /// <summary>Per-(slot, device) mouse-gesture settings provider
        /// (issue #200). Wired by the App layer against the slot's
        /// <c>PadSetting.MouseGestureSettings</c> via a UserSettings walk
        /// filtered by both <c>MapTo == slot</c> and
        /// <c>InstanceGuid == device</c>. Returns
        /// <see cref="Engine.Mouse.MouseGestureSettings.Default"/> when
        /// unwired or when no per-device settings exist.</summary>
        public System.Func<int, System.Guid, Engine.Mouse.MouseGestureSettings> MouseGestureSettingsProvider { get; set; }

        /// <summary>Remote Link per-poll accumulation hook (issue #138), wired
        /// by InputService. Invoked once per polling tick right after Step 2
        /// publishes fresh InputState snapshots, so the stream tick can ship
        /// snapshot frames with per-poll delta fields (mouse counts, Joy-Con 2
        /// mouse) summed on the poll thread instead of calling the wrappers'
        /// destructive GetCurrentState from a second thread. Null when Remote
        /// Link is off or the engine is wired without it.</summary>
        public System.Action RemoteLinkPollTick { get; set; }

        // ─── Recording-mode hook (gesture recorder dialog) ───
        //
        // The recorder dialog sets RecordingTargetDeviceGuid +
        // RecordingTargetPadIdx to the (device, pad) it's capturing.
        // While set, UpdateGestureContexts skips recognition for that
        // specific pad and instead invokes RecordingTick with the raw
        // TouchpadInputState every poll. The dialog uses this to draw
        // live finger paths from the real touchpad onto its canvas.
        // Other pads on the same device — and every other device —
        // continue normal gesture detection.
        //
        // Guid can't be volatile (CS0677). All writes go through
        // SetRecordingTarget on the UI thread; reads happen on the
        // polling thread. The 128-bit Guid read isn't strictly atomic
        // on 64-bit Windows but the worst case is a one-tick mismatch
        // where the engine evaluates the wrong pad once during a
        // recording-target swap — visually invisible to the user.
        public Guid RecordingTargetDeviceGuid;
        public volatile int RecordingTargetPadIdx = -1;

        /// <summary>Per-tick callback fired with the raw touchpad
        /// state of whichever pad matches the recording target. The
        /// recorder dialog subscribes; the engine clears the subscription
        /// when the dialog closes via <see cref="ClearRecordingTarget"/>.
        /// Always fires on the polling thread — marshal to the UI thread
        /// before touching WPF visuals.</summary>
        public event Action<PadForge.Engine.TouchpadInputState> RecordingTick;

        /// <summary>Install (or replace) the recording target. Pass
        /// <c>Guid.Empty</c> / <c>-1</c> + null callback to clear.
        /// Idempotent.</summary>
        public void SetRecordingTarget(Guid deviceGuid, int padIdx, Action<PadForge.Engine.TouchpadInputState> onTick)
        {
            RecordingTick = null;
            RecordingTargetDeviceGuid = deviceGuid;
            RecordingTargetPadIdx = padIdx;
            if (onTick != null) RecordingTick += onTick;
        }

        public void ClearRecordingTarget()
        {
            RecordingTick = null;
            RecordingTargetDeviceGuid = Guid.Empty;
            RecordingTargetPadIdx = -1;
        }

        /// <summary>
        /// Retrieved output states copied from Step 4 for UI display in Step 6.
        /// </summary>
        public Gamepad[] RetrievedOutputStates { get; } = new Gamepad[MaxPads];

        /// <summary>
        /// Retrieved KBM raw states for UI display (keyboard key + mouse state preview).
        /// </summary>
        public KbmRawState[] RetrievedKbmRawStates { get; } = new KbmRawState[MaxPads];

        /// <summary>
        /// Retrieved touchpad states for UI display.
        /// </summary>
        public TouchpadState[] RetrievedTouchpadStates { get; } = new TouchpadState[MaxPads];

        /// <summary>
        /// Pending profile switch ID queued by global macro evaluation.
        /// "\0" = no pending switch. null = switch to default profile.
        /// Consumed by InputService on the UI thread.
        /// </summary>
        public volatile string PendingProfileSwitchId = "\0";

        /// <summary>Whether the pending profile switch was triggered manually (shortcut).</summary>
        public volatile bool PendingProfileSwitchIsManual;

        /// <summary>
        /// Pending window toggle queued by global macro evaluation.
        /// Consumed by InputService on the UI thread.
        /// </summary>
        public volatile bool PendingToggleWindow;

        /// <summary>
        /// Pending bulk VC disable/enable toggle queued by global macro
        /// evaluation. Consumed by InputService on the UI thread.
        /// </summary>
        public volatile bool PendingToggleVCsDisabled;

        /// <summary>
        /// Set true while recording a shortcut combo. Suppresses global macro
        /// evaluation so the recorded buttons don't immediately trigger a switch.
        /// </summary>
        public volatile bool SuppressGlobalMacros;

        /// <summary>
        /// Flag set by macro execution to request touchpad overlay toggle.
        /// Cleared by InputService on the UI thread after processing.
        /// </summary>
        public volatile bool ToggleTouchpadOverlayRequested;

        /// <summary>
        /// Per-slot vibration states received from games via the active virtual-controller backend.
        /// </summary>
        public Vibration[] VibrationStates { get; } = new Vibration[MaxPads];

        /// <summary>
        /// Per-slot ephemeral macro rumble overrides driven by
        /// <c>MacroActionType.Rumble</c> (set) and
        /// <c>MacroActionType.RumbleStop</c> (clear). Read at the
        /// three FFB injection points (Step 2 ApplyForceFeedback,
        /// InputService's Sony dispatcher rumble pump, and
        /// ComputeFinalVibrationStates for the FFB-tab meter) and
        /// combined with raw game-driven rumble via <c>max()</c>.
        /// </summary>
        public MacroRumbleOverride[] MacroRumbleOverrides { get; }
            = InitMacroRumbleOverrides();

        /// <summary>
        /// Per-slot ephemeral macro override for the TRIGGER channel, driven by
        /// <c>MacroActionType.RumbleTrigger</c> (set) and
        /// <c>MacroActionType.RumbleTriggerStop</c> (clear). Sibling to
        /// <see cref="MacroRumbleOverrides"/> (issue #102): same hold / fade timer,
        /// but its scalar output max-combines into the trigger channel
        /// (<c>LeftTriggerMotorSpeed</c> / <c>RightTriggerMotorSpeed</c>) alongside
        /// the game-driven impulse output and the main-motor → trigger routing pass,
        /// at the same three injection points.
        /// </summary>
        public MacroRumbleOverride[] MacroTriggerRumbleOverrides { get; }
            = InitMacroRumbleOverrides();

        /// <summary>Per-slot trigger-actuator pulse for the steering at-lock feedback
        /// (#94, channel 2). Reuses <see cref="MacroRumbleOverride"/> purely as a
        /// hold+fade timer; its scalar output is routed to the trigger actuators (Xbox
        /// impulse triggers in ApplyForceFeedback, DualSense trigger haptics via
        /// <c>UserEffectsDispatcher</c>) rather than the grip motors, so trigger vibration
        /// stays distinct from channel 1's rumble.</summary>
        public MacroRumbleOverride[] SteeringTrigVibOverrides { get; }
            = InitMacroRumbleOverrides();

        private static MacroRumbleOverride[] InitMacroRumbleOverrides()
        {
            var arr = new MacroRumbleOverride[MaxPads];
            for (int i = 0; i < arr.Length; i++) arr[i] = new MacroRumbleOverride();
            return arr;
        }

        /// <summary>
        /// Per-slot post-processed vibration. Each motor is the max
        /// across every device mapped to the slot, with each device's
        /// own PadSetting applied (gain/balance/swap, audio rumble,
        /// constant force). Drives the Controller-preview-tab motor
        /// meter — that meter answers "is anything rumbling right now?"
        /// so it is intentionally device-filter-independent. The SDL
        /// physical-rumble path and the DS5/DS4 dispatcher each compute
        /// their own per-device scaled rumble from each device's own
        /// PadSetting, so they do NOT read this array.
        /// </summary>
        public Vibration[] FinalVibrationStates { get; } = new Vibration[MaxPads];

        /// <summary>
        /// Per-slot vibration scaled by the FFB-tab dropdown's currently-
        /// selected device's PadSetting (gain/balance/swap, audio rumble,
        /// constant force). Drives the FFB tab's Motor Activity meter —
        /// that meter MUST be device-specific so users editing one
        /// device's settings see the effective output for THAT device,
        /// not the slot-wide max. Falls back to zero when no device is
        /// selected. Populated in the same loop as
        /// <see cref="FinalVibrationStates"/> by
        /// <c>ComputeFinalVibrationStates</c>.
        /// </summary>
        public Vibration[] SelectedDeviceVibrationStates { get; } = new Vibration[MaxPads];

        /// <summary>
        /// Per-slot InstanceGuid of the device the user has selected on
        /// the slot's FFB tab. Drives (a) the audio-bass detector's
        /// per-tick Sensitivity / CutoffHz settings via
        /// <c>ApplyDetectorSettingsForTick</c> and (b) which device's
        /// scaled vibration lands in
        /// <see cref="SelectedDeviceVibrationStates"/> for the FFB-tab
        /// meter. Updated by <c>InputService.SyncViewModelToPadSettings</c>
        /// at 30 Hz. <see cref="Guid.Empty"/> when no device is selected.
        /// </summary>
        public Guid[] SelectedDeviceGuids { get; } = new Guid[MaxPads];

        /// <summary>
        /// Per-slot motion snapshots for DSU (cemuhook) streaming.
        /// Written by the polling thread after Step 2, read by the DSU server.
        /// </summary>
        public MotionSnapshot[] MotionSnapshots { get; } = new MotionSnapshot[MaxPads];

        /// <summary>
        /// Per-slot battery percentage (0..100, or -1 if no assigned device
        /// reports battery). Aggregated alongside MotionSnapshots from the
        /// first online assigned device whose SDL3 power info is known.
        /// Read by the Sony Report 0x01 packer.
        /// </summary>
        public int[] BatteryPercents { get; } = new int[MaxPads];

        /// <summary>Per-slot battery charging flag, paired with <see cref="BatteryPercents"/>.</summary>
        public bool[] BatteryCharging { get; } = new bool[MaxPads];

        /// <summary>Per-slot hash of every assigned device's (percent, charging)
        /// so the Battery lightbar repaint kick fires on ANY device's change,
        /// not just the slot-collapsed first-match. Init -1 (never a real hash)
        /// so the first scan always kicks. Polling thread only.</summary>
        private readonly int[] _batterySignature = InitBatterySignature();
        private static int[] InitBatterySignature()
        {
            var a = new int[MaxPads];
            for (int i = 0; i < a.Length; i++) a[i] = -1;
            return a;
        }

        /// <summary>Per-slot gyro engage state contributed by the
        /// dedicated <c>GyroAimEngageButton</c> field. Settled once per
        /// tick by <see cref="UpdateGyroEngageStates"/>: Hold mode tracks
        /// the button state directly, Toggle mode flips on each rising
        /// edge. Reads OR-combine with <see cref="GyroEngagedFromMacro"/>
        /// in <see cref="SourceCoercion.AimEngageStateProvider"/>; either
        /// source can engage and neither can disengage what the other
        /// engaged.</summary>
        public volatile bool[] GyroEngagedFromButton = new bool[MaxPads];

        /// <summary>Per-slot gyro engage state contributed by the
        /// <c>SetGyroEngaged</c> macro action. Written from the macro
        /// evaluator (<c>Step4b.EvaluateMacros</c>), read alongside
        /// <see cref="GyroEngagedFromButton"/> by the gyro evaluators.</summary>
        public volatile bool[] GyroEngagedFromMacro = new bool[MaxPads];

        /// <summary>Previous-tick button state for each slot's engage
        /// button. Owned by <see cref="UpdateGyroEngageStates"/> as the
        /// edge-detection input for Toggle mode.</summary>
        private readonly bool[] _prevAimEngageButtonDown = new bool[MaxPads];

        /// <summary>Per-slot trigger-route engaged bits (issue #102), one each
        /// for the left and right trigger. Settled once per tick by
        /// <see cref="UpdateTriggerRouteEngageStates"/> (Hold tracks the
        /// activator, Toggle flips on rising edge, AlwaysOn / empty descriptor
        /// = always on). Read in the trigger routing pass
        /// (<c>ScaleTriggerRumbleForDevice</c>) to gate the main-motor → trigger
        /// routing per side.</summary>
        public volatile bool[] TriggerRouteEngagedLeft = new bool[MaxPads];
        public volatile bool[] TriggerRouteEngagedRight = new bool[MaxPads];
        private readonly bool[] _prevTriggerRouteLeftDown = new bool[MaxPads];
        private readonly bool[] _prevTriggerRouteRightDown = new bool[MaxPads];

        /// <summary>Per-slot resolved trigger-route config (issue #102), captured
        /// from the same first-device-wins PadSetting the engaged bits use so the
        /// routing pass reads one consistent slot config instead of re-resolving
        /// per device. Source: 0 None, 1 MainLeft, 2 MainRight, 3 MaxOfBoth,
        /// 4 SumOfBoth. Scale is the 0-200% slider as 0.0-2.0. Redirect = silence
        /// the main motor(s) the route drew from on the physical write.</summary>
        private readonly byte[] _routeSourceLeft = new byte[MaxPads];
        private readonly byte[] _routeSourceRight = new byte[MaxPads];
        private readonly double[] _routeScaleLeft = new double[MaxPads];
        private readonly double[] _routeScaleRight = new double[MaxPads];
        private readonly bool[] _routeRedirectLeft = new bool[MaxPads];
        private readonly bool[] _routeRedirectRight = new bool[MaxPads];

        // Low-cadence config snapshot for the trigger-route settle (#102), mirroring
        // _mirrorEngageCfg / UpdateHapticMirrorEngageStates (#185). Walking settings.Items
        // under SyncRoot for every created slot every tick put a lock + O(Items) scan on
        // the ~1 kHz loop; the route CONFIG changes only on user edit, so snapshot it at
        // 250 ms and keep only the live per-tick work (the activator's ButtonHeldProvider
        // read + edge/Toggle settle) hot. null slot = no active route.
        private sealed class TriggerRouteCfg
        {
            public byte SrcLeft, SrcRight;
            public double ScaleLeft, ScaleRight;
            public bool RedirectLeft, RedirectRight;
            public string ActLeft, ActLeftGuid, ActLeftMode;
            public string ActRight, ActRightGuid, ActRightMode;
        }
        private readonly TriggerRouteCfg[] _triggerRouteCfg = new TriggerRouteCfg[MaxPads];
        private long _triggerRouteCfgRefreshTick;

        /// <summary>Monotonic frame counter feeding the Sony Report 0x01
        /// timestamp / packet-sequence fields. Game-side parsers (e.g. SDL3's
        /// PS5 driver) reject duplicate packet-sequence values, so this MUST
        /// advance every frame regardless of input state.</summary>
        private long _sonyFrameCounter;

        /// <summary>
        /// DSU motion server reference. When set, the polling thread broadcasts
        /// motion data to subscribed clients after snapshotting sensor data.
        /// </summary>
        public DsuMotionServer DsuServer { get; set; }

        /// <summary>
        /// Audio bass detector. When set, the polling thread reads bass energy
        /// and combines it with game rumble via max() in ApplyForceFeedback.
        /// </summary>
        public AudioBassDetector AudioBassDetector { get; set; }

        /// <summary>
        /// When set (non-empty), the test rumble for this slot targets only the
        /// device with this GUID. ApplyForceFeedback skips other devices in the slot.
        /// </summary>
        public Guid[] TestRumbleTargetGuid { get; } = new Guid[MaxPads];

        /// <summary>
        /// Current measured polling frequency in Hz.
        /// </summary>
        public double CurrentFrequency { get; private set; }

        /// <summary>
        /// Whether the manager is currently running the polling loop.
        /// </summary>
        public bool IsRunning => _running;

        /// <summary>
        /// When true, the polling loop skips the expensive pipeline steps and sleeps
        /// at a low rate (~20Hz) to minimize CPU usage. Device enumeration continues
        /// at a reduced rate so new controllers still appear on the Devices page.
        /// Set by InputService when no virtual controller slots are created.
        /// </summary>
        public bool IsIdle
        {
            get => _idle;
            set => _idle = value;
        }

        // ─────────────────────────────────────────────
        //  Events
        // ─────────────────────────────────────────────

        /// <summary>
        /// Raised when the device list changes (device connected or disconnected).
        /// Raised on the background thread — UI consumers must marshal to dispatcher.
        /// </summary>
        public event EventHandler DevicesUpdated;

        /// <summary>
        /// Raised when the polling frequency measurement is updated (~once per second).
        /// </summary>
        public event EventHandler FrequencyUpdated;

        /// <summary>
        /// Raised when an error occurs during polling that doesn't stop the loop.
        /// </summary>
        public event EventHandler<InputExceptionEventArgs> ErrorOccurred;

        // ─────────────────────────────────────────────
        //  Constructor
        // ─────────────────────────────────────────────

        public InputManager()
        {
            // Initialize vibration states.
            for (int i = 0; i < MaxPads; i++)
            {
                VibrationStates[i] = new Vibration();
                FinalVibrationStates[i] = new Vibration();
                SelectedDeviceVibrationStates[i] = new Vibration();
            }
        }

        // ─────────────────────────────────────────────
        //  SDL Initialization
        // ─────────────────────────────────────────────

        /// <summary>
        /// Initializes the SDL3 library for joystick and gamepad support.
        /// Must be called before starting the polling loop.
        /// </summary>
        /// <returns>True if SDL initialized successfully.</returns>
        private bool InitializeSdl()
        {
            if (_sdlInitialized)
                return true;

            try
            {
                // Set hints before initialization.
                SDL_SetHint(SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");

                // Allow SDL3 to enumerate XInput controllers (Xbox, etc.).
                // Do NOT set SDL_HINT_JOYSTICK_RAWINPUT — it conflicts with
                // XInput enumeration and prevents Xbox controllers from appearing.
                SDL_SetHint(SDL_HINT_JOYSTICK_XINPUT, "1");

                // Enable Switch 2 Pro Controller HIDAPI driver (requires libusb-1.0.dll).
                SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_SWITCH2, "1");

                // Enable SDL's Wii HIDAPI driver (#116). SDL surfaces the
                // Bluetooth-paired Wii Remote / Nunchuk / Classic / Wii U Pro and
                // parses them, and lights the player LED (which stops the idle
                // flashing). This relies on the SDL3 fork's hid_write fix
                // (hifihedgehog/SDL#2): on Windows 8+ a Wii Remote's output
                // reports must go via HidD_SetOutputReport, since the Microsoft
                // Bluetooth stack rejects WriteFile for it. PadForge pairs the
                // controller (WiiPairingService) and SDL drives it from there.
                SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_WII, "1");

                // Enable the SDL3 fork's Bluetooth-LE Switch 2 driver (hifihedgehog/SDL#5).
                // Switch 2 controllers (Pro Controller 2, Joy-Con 2 L/R, NSO GameCube) speak a
                // custom BLE GATT service, not HID-over-Bluetooth, so SDL's hidapi can't see
                // them. This new WinRT driver scans for the Nintendo advertisement, connects,
                // and surfaces them as ordinary SDL gamepads (the existing fabricated Switch 2
                // mappings fire via the 'h' GUID signature). When on, it runs a BLE
                // advertisement scan while PadForge is open. Runtime is hypothesis-under-test
                // (no Switch 2 hardware has validated it yet).
                SDL_SetHint(SDL_HINT_JOYSTICK_BLE_SWITCH2, "1");

                // Enable the SDL3 fork's right Joy-Con NIR camera scalar (issue
                // #151, hifihedgehog/SDL#7, fork commit a31980950a). With the hint
                // on, a STANDALONE right Joy-Con powers its IR MCU when sensors are
                // enabled (PadForge enables sensors for gyro) and posts the MCU's
                // average-intensity byte, buf[53] scaled 0..32767, on dedicated
                // joystick axis 6. Covered sensor = bright = high value, so the
                // "IR Brightness" source reads cover/proximity. Combined pairs are
                // excluded by the fork (their shared joystick has no IR axis).
                SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_JOYCON_IR_SENSOR, "1");

                // Enable the SDL3 fork's Joy-Con 2 optical mouse axes (issue
                // #154, hifihedgehog/SDL#8, fork commit 9b32ec13b8). With the
                // hint on, the BLE driver enables the sensor via the feature
                // init+enable frames at connect and posts the absolute 16-bit
                // counters on joystick axes 6/7 (raw axis count 8 signals
                // availability). SdlDeviceWrapper derives per-poll wraparound
                // deltas for the "Mouse Motion X/Y" sources.
                SDL_SetHint(SDL_HINT_JOYSTICK_BLE_SWITCH2_MOUSE, "1");

                // Enable SDL's Sony-sixaxis PS3 driver (discussion #194). It
                // claims a DualShock 3 running DsHidMini's SixaxisCompatible
                // (SXS) mode, the only DsHidMini mode that serves the DS3's
                // motion data (GET_FEATURE report 0x00, HID.FeatureReport.c),
                // and reads buttons, sticks, the 10 pressure axes (joystick
                // axes 6-15, surfaced by the #193 generic-axis path), the
                // 3-axis accelerometer, and the yaw gyro through the standard
                // SDL sensor API that SdlDeviceWrapper already consumes.
                // Windows-only driver, hint-gated off by default in SDL, and
                // inert unless the user has put their DS3 in SXS mode. SDF /
                // GPJ nodes are unaffected (wrong report shape for this
                // driver's GET_FEATURE parse). Gyro and correct accel scaling
                // over DsHidMini depend on the fork patch in
                // sdl-patch-spec-ds3-sixaxis-motion.md; with a stock-driver
                // DLL the accel reads wrong (big-endian parse of DsHidMini's
                // little-endian serve) and no gyro is declared, so the fork
                // patch must land before this feature is announced. Do NOT
                // also set SDL_HINT_JOYSTICK_HIDAPI_PS3: the regular PS3
                // driver outranks sixaxis in SDL's driver table and its init
                // writes at the device.
                SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_PS3_SIXAXIS_DRIVER, "1");

                // Allow screensaver/sleep even while SDL video is active.
                SDL_SetHint(SDL_HINT_VIDEO_ALLOW_SCREENSAVER, "1");

                // In-memory diagnostics ring: captures the SDL drivers' own
                // debug narration plus the poll-loop stall watchdog as
                // crash context (the crash handler appends the ring to
                // crash.log). No file is written in normal operation.
                // Installed before SDL_Init so init-time messages land
                // too. Pre-v4 builds wrote these lines to diag.log beside
                // the exe; remove the stale file those builds left behind.
                try
                {
                    System.IO.File.Delete(System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "diag.log"));
                }
                catch { }
                Engine.SdlDiagLog.Install();

                // SDL3: SDL_Init returns bool (true = success), and
                // SDL_INIT_GAMECONTROLLER is renamed to SDL_INIT_GAMEPAD.
                // SDL_INIT_VIDEO is required for keyboard/mouse enumeration.
                if (!SDL_Init(SDL_INIT_JOYSTICK | SDL_INIT_GAMEPAD | SDL_INIT_VIDEO | SDL_INIT_HAPTIC))
                {
                    string error = SDL_GetError();
                    RaiseError($"SDL_Init failed: {error}", null);
                    return false;
                }

                // Load PadForge community mappings (extends SDL's built-in
                // gamecontrollerdb). Embedded in the exe; apply per-line via
                // SDL_AddGamepadMapping rather than the file-path overload.
                LoadEmbeddedGamepadMappings();

                // SDL_INIT_VIDEO disables the screensaver and system sleep by
                // default.  Re-enable both so the PC can sleep when idle.
                SDL_EnableScreenSaver();
                SetThreadExecutionState(ES_CONTINUOUS);

                _sdlInitialized = true;

                // Surface a Bluetooth DualShock 3 (behind BthPS3, no DsHidMini) to SDL
                // as a virtual joystick so the normal pipeline consumes it. Cheap when
                // absent (a periodic device-interface poll); attaches only on connect.
                try
                {
                    _ds3Direct = new Ds3DirectService(msg => Engine.SdlDiagLog.WriteLine("DS3 " + msg));
                    _ds3Direct.Start();

                    // Battery for the bridged DS3 (#167 lane): SDL has no power
                    // channel for virtual joysticks, so the wrapper falls back to
                    // this provider when SDL reports unknown.
                    Engine.SdlDeviceWrapper.ExternalPowerInfoProvider = Ds3DirectService.GetPowerInfo;

                    // Real transport path for the bridged DS3 (Dossier path + BT/USB
                    // classification): the virtual joystick has no SDL path of its own.
                    Engine.SdlDeviceWrapper.ExternalDevicePathProvider = Ds3DirectService.GetDevicePath;
                }
                catch (Exception ex) { Engine.SdlDiagLog.WriteLine("DS3 service start failed: " + ex.Message); }

                return true;
            }
            catch (DllNotFoundException ex)
            {
                RaiseError("SDL3.dll not found. Place SDL3.dll next to the exe. " +
                           "Download from https://github.com/libsdl-org/SDL/releases", ex);
                return false;
            }
            catch (Exception ex)
            {
                RaiseError("Failed to initialize SDL3.", ex);
                return false;
            }
        }

        /// <summary>
        /// Shuts down the SDL3 library. Called during disposal.
        /// </summary>
        private void ShutdownSdl()
        {
            if (!_sdlInitialized)
                return;

            try { _ds3Direct?.Stop(); } catch { }
            _ds3Direct = null;

            SDL_Quit();
            _sdlInitialized = false;
        }

        /// <summary>Bluetooth DualShock 3 -> SDL virtual joystick bridge (BthPS3 raw PDO).</summary>
        private Ds3DirectService _ds3Direct;

        private long _ds3PlayerNumberTick;

        /// <summary>
        /// Player LED idle floor for the bridged DS3 (#191 parity): of the
        /// virtual controllers the pad feeds, the smallest displayed player
        /// number picks its lit LED (SlotOrders.GetIdentityPlayerNumber),
        /// wrapping past 4 like SDL's PS3 driver. Unmapped keeps LED 1.
        /// Rate-limited to twice a second; SetPlayerNumber itself is
        /// change-detected, so steady state costs two short lock walks and
        /// no device I/O.
        /// </summary>
        private void UpdateDs3PlayerNumber()
        {
            var svc = _ds3Direct;
            if (svc == null || !svc.IsConnected) return;

            long now = Environment.TickCount64;
            if (now - _ds3PlayerNumberTick < 500) return;
            _ds3PlayerNumberTick = now;

            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            if (devices == null || settings == null) return;

            uint id = svc.InstanceId;
            Guid guid = Guid.Empty;
            lock (devices.SyncRoot)
            {
                foreach (var ud in devices.Items)
                {
                    if (ud?.Device is Engine.SdlDeviceWrapper w && w.SdlInstanceId == id)
                    {
                        guid = ud.InstanceGuid;
                        break;
                    }
                }
            }
            if (guid == Guid.Empty) { svc.SetPlayerNumber(0); return; }

            // Identity precedence: the shared fold over every slot the
            // pad feeds (smallest displayed number wins), same winner
            // every other identity writer computes.
            svc.SetPlayerNumber(SettingsManager.SlotOrders.GetIdentityPlayerNumber(guid));
        }

        /// <summary>
        /// Number of gamepad mappings successfully applied from the embedded
        /// gamecontrollerdb_padforge.txt. Zero means either the resource is
        /// missing (build misconfiguration) or every line was blank/comment.
        /// Exposed as a diagnostic so Settings / About can surface whether
        /// the embed is reaching SDL at runtime.
        /// </summary>
        public static int EmbeddedMappingsLoaded { get; private set; }

        /// <summary>
        /// Streams the embedded gamecontrollerdb_padforge.txt resource through
        /// SDL_AddGamepadMapping one line at a time. The file-path overload
        /// (SDL_AddGamepadMappingsFromFile) is unusable when the file ships
        /// inside the single-file exe rather than as a loose resource next to
        /// it. Per-line apply is cheap (one P/Invoke per mapping, a few dozen
        /// total) and avoids touching the filesystem.
        /// </summary>
        private static void LoadEmbeddedGamepadMappings()
        {
            int applied = 0;
            try
            {
                var asm = typeof(InputManager).Assembly;
                // Resource name is the default manifest name: "<RootNamespace>.<filename>".
                // PadForge's RootNamespace is "PadForge" (see csproj).
                string resourceName = "PadForge.gamecontrollerdb_padforge.txt";
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[InputManager] Embedded resource '{resourceName}' not found. " +
                        "Check <EmbeddedResource Include=\"gamecontrollerdb_padforge.txt\"/> in PadForge.App.csproj.");
                    return;
                }
                using var reader = new System.IO.StreamReader(stream);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                    if (SDL_AddGamepadMapping(trimmed) >= 0)
                        applied++;
                }
            }
            catch (Exception ex)
            {
                // Mapping load is best-effort — SDL's built-in gamecontrollerdb
                // is still active and recognizes most common gamepads. Any
                // failure here just means PadForge's community mappings aren't
                // applied on top, which isn't fatal.
                System.Diagnostics.Debug.WriteLine($"[InputManager] Embedded mappings load failed: {ex.Message}");
            }
            EmbeddedMappingsLoaded = applied;
            System.Diagnostics.Debug.WriteLine($"[InputManager] Applied {applied} embedded PadForge gamepad mapping(s).");
        }

        // ─────────────────────────────────────────────
        //  Start / Stop
        // ─────────────────────────────────────────────

        /// <summary>
        /// Starts the background polling thread. Safe to call multiple times;
        /// subsequent calls are ignored if already running.
        /// </summary>
        /// <summary>
        /// Pumps SDL's event queue. MUST be called on the thread that ran
        /// SDL_Init (the UI thread here), because SDL's hidapi creates its
        /// device-change message window there and Windows posts WM_DEVICECHANGE
        /// only to that thread. SDL's hidapi only re-scans for connected/removed
        /// controllers when those messages are dispatched (SDL_hidapi.c relies on
        /// the SDL_PumpEvents loop on the video thread). Without this, a device
        /// that drops on a read hiccup never comes back and a freshly-plugged one
        /// never appears until an app restart re-enumerates from scratch.
        /// </summary>
        public void PumpSdlEvents()
        {
            if (!_sdlInitialized)
                return;

            // Dispatch SDL's hidapi device-change messages (their hidden window
            // lives on this, the SDL_Init thread), then run the joystick update
            // HERE so the resulting hidapi re-scan / new-device enumeration runs
            // on the thread that owns the joystick subsystem. The polling thread
            // reads device STATE fine, but enumerating a newly-connected
            // controller only produces joysticks on the init thread (a re-scan on
            // the polling thread yields zero). This is what makes a hot-plugged
            // controller (e.g. a DualSense) appear in-process without a restart.
            // Stall watchdog (#210 follow-up): this UI-thread call enters the
            // same hidapi UpdateDevice paths as the poll loop, so a driver
            // stall here freezes the UI and must be attributable too.
            long tsPump = Stopwatch.GetTimestamp();
            SDL_PumpEvents();
            long tsUpd = Stopwatch.GetTimestamp();
            SDL_UpdateJoysticks();
            long tsEnd = Stopwatch.GetTimestamp();
            long pumpMs = (tsUpd - tsPump) * 1000 / Stopwatch.Frequency;
            long updMs = (tsEnd - tsUpd) * 1000 / Stopwatch.Frequency;
            if (pumpMs >= 25 || updMs >= 25)
                Engine.SdlDiagLog.WriteLine($"STALL uipump pump={pumpMs}ms upd={updMs}ms");
        }

        /// <summary>
        /// Forces SDL to cleanly re-open the Wii hidapi driver's devices after a
        /// pairing (#116). During the Bluetooth pairing ceremony SDL grabs the
        /// Wii Remote mid-pairing, then our BluetoothSetServiceState and the
        /// pairing churn invalidate that handle, so the joystick drops and SDL
        /// keeps a stale device that only a full app restart clears. Toggling the
        /// Wii hidapi hint off then on replicates the restart for just this driver:
        /// off runs SDL_HIDAPIDriverHintChanged which disables the driver (SDL
        /// cleans up the stale device and closes the dead handle) and resets the
        /// hidapi change count to force a re-enumerate; on re-opens and re-inits
        /// the now-stable device. Repeated over ~11s to cover the post-pair
        /// settling window. The UI-thread pump processes each toggle. Safe to call
        /// from any thread (SDL_SetHint is thread-safe).
        /// </summary>
        public void RescanWiiControllers()
        {
            Task.Run(() =>
            {
                for (int i = 0; i < 8 && !_disposed; i++)
                {
                    SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_WII, "0");
                    Thread.Sleep(200);
                    SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_WII, "1");
                    Thread.Sleep(1200);
                }
            });
        }

        public void Start()
        {
            if (_running || _disposed)
                return;

            if (!InitializeSdl())
                return;

            // Virtual-controller filtering is handled entirely by PadForge's
            // SDL3 fork: HID enumeration walks each device's PnP ancestor
            // chain for "HIDMaestro" and skips matches, and XInput enumeration
            // skips any slot whose VID/PID resolves only to HIDMaestro HIDs.
            // No per-process slot tracking, no function-pointer hook.

            RawInputListener.Start();

            // PTP reader always runs so Devices page can preview touchpad input.
            // Note: on shared hardware (laptop trackpads), the digitizer registration
            // stops Windows from synthesizing mouse reports for the same device.
            _ptpReader = new PrecisionTouchpadReader();
            _ptpReader.Start();

            _running = true;
            _enumerationTimer.Restart();
            _frequencyTimer.Restart();
            _frequencyCounter = 0;

            _pollingThread = new Thread(PollingLoop)
            {
                Name = "PadForge.InputManager",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _pollingThread.Start();

            _mouseInjectorThread = new Thread(MouseInjectorLoop)
            {
                Name = "PadForge.MouseInjector",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _mouseInjectorThread.Start();
        }

        /// <summary>
        /// Flushes macro mouse-move deltas that the poll thread accumulated, one
        /// SendInput per tick, so the SendInput syscall never runs on the 1000 Hz
        /// poll thread. Injected mouse movement is processed synchronously (it
        /// traverses every process's low-level mouse hook chain), which is why a
        /// per-poll SendInput could collapse the poll rate to ~200 Hz. ~500 Hz cap
        /// via a 2 ms sleep (timeBeginPeriod(1) from the poll loop keeps the sleep
        /// near 2 ms); accumulated delta is never lost, only batched.
        /// </summary>
        private void MouseInjectorLoop()
        {
            while (_running)
            {
                FlushPendingMouseInput();
                Thread.Sleep(2);
            }
            FlushPendingMouseInput(); // drain any final delta on shutdown
        }

        /// <summary>
        /// Stops the background polling thread and waits for it to exit.
        /// </summary>
        public void Stop()
        {
            if (!_running)
                return;

            _running = false;

            // Macro sounds die with the engine — releases the WASAPI clients.
            SoundMacroService.StopAll();

            if (_pollingThread != null && _pollingThread.IsAlive)
            {
                _pollingThread.Join(timeout: TimeSpan.FromSeconds(3));
                _pollingThread = null;
            }

            if (_mouseInjectorThread != null && _mouseInjectorThread.IsAlive)
            {
                _mouseInjectorThread.Join(timeout: TimeSpan.FromSeconds(1));
                _mouseInjectorThread = null;
            }

            RawInputListener.Stop();

            _ptpReader?.Stop();
            _ptpReader?.Dispose();
            _ptpReader = null;

            StopAllForceFeedback();

            // Wait for any in-flight HM lifecycle tasks (Pass 2 connects
            // and Pass 1 async-dispose teardowns) to complete before we
            // tear everything down.  Without this wait, a connect task
            // that's currently inside HMContext.CreateController would
            // run to completion AFTER Stop returns, set
            // _virtualControllers[i] to the just-built VC, and the new
            // VC would never be disposed — an orphaned controller in
            // the kernel device tree.  AwaitPendingLifecycleTasks is
            // bounded so a hung HM call can't deadlock shutdown.
            AwaitPendingLifecycleTasks();

            DestroyAllVirtualControllers();

            // Reset initializing flags so post-stop reads return false.
            // The UI tick has already been stopped by InputService.Stop
            // before getting here, but InputService also clears the same
            // flags on the slot ViewModels for immediate visual update.
            for (int i = 0; i < MaxPads; i++)
                _slotInitializing[i] = false;
            DisposeHMaestroContextOnShutdown();
            CloseAllDevices();

            _enumerationTimer.Stop();
            _frequencyTimer.Stop();
            CurrentFrequency = 0;
        }

        // ─────────────────────────────────────────────
        //  Main polling loop
        // ─────────────────────────────────────────────

        /// <summary>
        /// Background thread entry point. Runs the 6-step pipeline at ~1000Hz.
        ///
        /// Uses a Stopwatch-based spin-wait instead of Thread.Sleep(1) for precise
        /// timing. Thread.Sleep(1) has ~1.5-2ms latency on Windows even with
        /// timeBeginPeriod(1), capping the loop at ~500-600Hz. Spin-waiting on
        /// Stopwatch ticks (backed by QueryPerformanceCounter) achieves true 1000Hz.
        ///
        /// CPU impact is minimal: spin-waiting burns one core at ~1-3% utilization
        /// for sub-millisecond waits, and the thread priority is AboveNormal so it
        /// doesn't starve other work.
        /// </summary>
        private void PollingLoop()
        {
            // Keep timeBeginPeriod(1). It still helps multimedia timers and
            // other system timing used by SDL, HIDMaestro, and the UI dispatcher.
            timeBeginPeriod(1);

            // High-resolution waitable timer for sub-ms sleeps without
            // burning CPU.  Available on Windows 10 1803+.
            IntPtr hTimer = CreateWaitableTimerExW(
                IntPtr.Zero, IntPtr.Zero,
                CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);

            // Fallback: x360ce-style multimedia timer + ManualResetEvent.
            // timeSetEvent fires a periodic callback that signals the event,
            // letting the polling thread block with zero CPU. Precision is
            // ~1-2ms with timeBeginPeriod(1) — less accurate than the HR
            // timer but much better than Thread.Sleep(1) alone.
            ManualResetEvent mmTimerEvent = null;
            TimerCallback mmTimerCb = null;
            uint mmTimerId = 0;
            if (hTimer == IntPtr.Zero)
            {
                mmTimerEvent = new ManualResetEvent(false);
                var evt = mmTimerEvent; // capture for lambda
                mmTimerCb = (id, msg, user, dw1, dw2) =>
                {
                    try { evt.Set(); } catch { /* disposed at shutdown */ }
                };
                mmTimerId = timeSetEvent((uint)Math.Max(1, PollingIntervalMs), 0,
                    mmTimerCb, IntPtr.Zero, TIME_PERIODIC);
                if (mmTimerId == 0)
                {
                    // Timer failed — dispose the event to avoid a resource leak.
                    mmTimerEvent.Dispose();
                    mmTimerEvent = null;
                    mmTimerCb = null;
                }
            }

            try
            {
                var cycleTimer = new Stopwatch();
                cycleTimer.Start();

                // Periodically clear any execution-state flags that SDL may
                // re-assert during SDL_JoystickUpdate / event processing.
                var sleepGuardTimer = new Stopwatch();
                sleepGuardTimer.Start();

                // Wall-clock drift compensation: track cumulative expected
                // time vs actual elapsed time.  If we fall behind, shorten
                // future cycles to catch up so the average rate converges.
                var wallClock = new Stopwatch();
                wallClock.Start();
                long expectedTicks = 0;

                // Run device enumeration immediately on the first cycle so that
                // controllers are detected, virtual devices are created, and force
                // feedback is wired without waiting for the 2-second interval.
                bool firstCycle = true;

                while (_running)
                {
                    // ── Idle mode: skip expensive pipeline, sleep at ~20Hz ──
                    if (_idle)
                    {
                        try
                        {
                            long tsIdleSdl = Stopwatch.GetTimestamp();
                            SDL_UpdateJoysticks();
                            long idleSdlMs = (Stopwatch.GetTimestamp() - tsIdleSdl) * 1000 / Stopwatch.Frequency;
                            if (idleSdlMs >= 25)
                                Engine.SdlDiagLog.WriteLine($"STALL idle sdl={idleSdlMs}ms");

                            // Keep device enumeration at a reduced rate so the
                            // Devices page still discovers newly connected controllers.
                            if (_enumerationTimer.ElapsedMilliseconds >= 5000)
                            {
                                _enumerationTimer.Restart();
                                UpdateDevices();
                            }

                            // Read input states even in idle mode so the Devices
                            // page preview works for unassigned devices.
                            UpdateInputStates();
                            // Remote Link accumulation runs in idle mode too:
                            // shared devices keep streaming while no slot is
                            // active on this end.
                            RemoteLinkPollTick?.Invoke();

                            // Evaluate global macros (profile shortcuts) even in idle
                            // so the user can switch away from an empty profile.
                            EvaluateGlobalMacros();
                        }
                        catch (Exception ex)
                        {
                            RaiseError("Idle polling error", ex);
                        }

                        CurrentFrequency = 0;
                        _frequencyCounter = 0;
                        _frequencyTimer.Restart();
                        FrequencyUpdated?.Invoke(this, EventArgs.Empty);
                        Thread.Sleep(50);
                        firstCycle = true; // Ensure immediate enumeration on wake
                        // Reset wall-clock drift tracker so stale drift from
                        // before idle doesn't cause a burst of short cycles.
                        wallClock.Restart();
                        expectedTicks = 0;
                        continue;
                    }

                    // Calculate target ticks each cycle so PollingIntervalMs can be
                    // changed at runtime from the Settings UI.
                    long targetTicks = Stopwatch.Frequency / 1000 * PollingIntervalMs;

                    cycleTimer.Restart();

                    try
                    {
                        // Stall watchdog (#210 follow-up): attribute any poll
                        // hiccup to the segment that ate it. SDL_UpdateJoysticks
                        // is where driver-side sync I/O would stall; the
                        // enumeration sweep is where device opens would.
                        long tsSdl = Stopwatch.GetTimestamp();
                        SDL_UpdateJoysticks();
                        long sdlMs = (Stopwatch.GetTimestamp() - tsSdl) * 1000 / Stopwatch.Frequency;

                        // Advance the evaluator's poll-frame gate exactly once
                        // per cycle: SourceCoercion's smoothing/delta caches
                        // step once per poll no matter how many mapping rows
                        // read the same source (the Gyro tab's smoothing is
                        // per-device-per-slot, not per-row).
                        Engine.Common.Mapping.SourceCoercion.BeginPollFrame();

                        long enumMs = 0;
                        if (firstCycle || _enumerationTimer.ElapsedMilliseconds >= EnumerationIntervalMs)
                        {
                            firstCycle = false;
                            _enumerationTimer.Restart();
                            long tsEnum = Stopwatch.GetTimestamp();
                            UpdateDevices();
                            enumMs = (Stopwatch.GetTimestamp() - tsEnum) * 1000 / Stopwatch.Frequency;
                        }

                        UpdateInputStates();
                        // Remote Link (#138): fold this tick's fresh snapshots
                        // into the per-exposed-device delta accumulators. The
                        // poll thread is the SOLE GetCurrentState caller; the
                        // 125 Hz stream tick ships snapshots + drained deltas,
                        // never reads the wrappers (destructive mouse / JC2
                        // baseline reads split motion between two callers).
                        RemoteLinkPollTick?.Invoke();
                        UpdateGyroEngageStates();
                        UpdateTriggerRouteEngageStates();
                        UpdateHapticMirrorEngageStates();
                        UpdateMotionSnapshots();
                        BroadcastDsuMotion();
                        UpdateOutputStates();
                        CombineOutputStates();
                        EvaluateMacros();
                        UpdateVirtualDevices();
                        RetrieveOutputStates();
                        UpdateDs3PlayerNumber();

                        // Stall watchdog report: only outliers write anything.
                        long cycleMs = cycleTimer.ElapsedMilliseconds;
                        if (sdlMs >= 25 || enumMs >= 25 || cycleMs >= 50)
                            Engine.SdlDiagLog.WriteLine(
                                $"STALL poll sdl={sdlMs}ms enum={enumMs}ms cycle={cycleMs}ms");

                        // Frequency measurement.
                        _frequencyCounter++;
                        if (_frequencyTimer.ElapsedMilliseconds >= 1000)
                        {
                            CurrentFrequency = _frequencyCounter * 1000.0 / _frequencyTimer.ElapsedMilliseconds;
                            _frequencyCounter = 0;
                            _frequencyTimer.Restart();
                            FrequencyUpdated?.Invoke(this, EventArgs.Empty);
                        }

                        // Clear any execution-state flags SDL may have re-set
                        // during event processing so the PC can still sleep.
                        if (sleepGuardTimer.ElapsedMilliseconds >= 5000)
                        {
                            sleepGuardTimer.Restart();
                            SetThreadExecutionState(ES_CONTINUOUS);
                        }
                    }
                    catch (Exception ex)
                    {
                        RaiseError("Polling loop error", ex);
                    }

                    // Wall-clock drift-compensated precision wait.
                    //
                    // Instead of per-cycle overshoot tracking, we compare
                    // cumulative expected time against the wall clock.  If
                    // we're behind, we shorten this cycle; if ahead, we
                    // lengthen it.  This converges the average rate exactly.
                    expectedTicks += targetTicks;
                    long drift = wallClock.ElapsedTicks - expectedTicks;

                    // If drift exceeds 10 cycles (e.g. after system sleep/resume),
                    // reset the wall clock instead of sprinting to catch up.
                    if (drift > targetTicks * 10 || drift < -(targetTicks * 10))
                    {
                        wallClock.Restart();
                        expectedTicks = targetTicks;
                        drift = 0;
                    }

                    long adjustedTarget = targetTicks - drift;
                    if (adjustedTarget < targetTicks / 4)
                        adjustedTarget = targetTicks / 4; // safety floor

                    long spinThresholdTicks = Stopwatch.Frequency / 10000; // 0.1ms
                    long sleepThresholdTicks = Stopwatch.Frequency * 3 / 2000; // 1.5ms
                    long remaining = adjustedTarget - cycleTimer.ElapsedTicks;

                    if (remaining > spinThresholdTicks && hTimer != IntPtr.Zero)
                    {
                        // HR timer: precise sub-ms kernel sleep.
                        long waitTicks = remaining - spinThresholdTicks;
                        long dueTime = -(waitTicks * 10_000_000 / Stopwatch.Frequency);
                        if (dueTime < -1)
                        {
                            if (SetWaitableTimerEx(hTimer, ref dueTime, 0,
                                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                                WaitForSingleObject(hTimer, INFINITE);
                        }
                    }
                    else if (remaining > spinThresholdTicks && mmTimerEvent != null)
                    {
                        // x360ce-style: block until multimedia timer fires (~1ms).
                        mmTimerEvent.WaitOne(50);
                        mmTimerEvent.Reset();
                    }
                    else if (remaining > sleepThresholdTicks)
                    {
                        // Last resort: Thread.Sleep(1).
                        Thread.Sleep(1);
                    }

                    // Spin for the final sub-ms portion.
                    while (cycleTimer.ElapsedTicks < adjustedTarget)
                        Thread.SpinWait(1);
                }
            }
            finally
            {
                if (hTimer != IntPtr.Zero)
                    CloseHandle(hTimer);
                if (mmTimerId != 0)
                    timeKillEvent(mmTimerId);
                GC.KeepAlive(mmTimerCb); // prevent GC of native callback delegate
                mmTimerEvent?.Dispose();
                timeEndPeriod(1);
            }
        }

        // ─────────────────────────────────────────────
        //  Device cleanup helpers
        // ─────────────────────────────────────────────

        /// <summary>Last-chance hardware quiet for abnormal exits
        /// (crash handler, ProcessExit). The Steam Deck's 0xEB rumble
        /// command has no duration field and no firmware timeout in any
        /// reference, so a nonzero rumble left behind by a dying process
        /// buzzes the trackpad LRAs until something zeroes it
        /// (discussion #179). Best-effort by design.</summary>
        /// <summary>True once an abnormal-exit quiesce ran. Sticky:
        /// the polling thread keeps ticking through a crash dialog and
        /// during ProcessExit (verified empirically on .NET 10), and one
        /// StopAllForceFeedback sweep only clears the change-detection
        /// caches, so the next tick would re-assert the game's still
        /// nonzero VibrationStates within ~1 ms. ApplyForceFeedback and
        /// the Sony effects provider gate on this flag.</summary>
        public volatile bool OutputsQuiesced;

        public void QuiesceOutputs()
        {
            OutputsQuiesced = true;
            try { StopAllForceFeedback(); } catch { }
        }

        private void StopAllForceFeedback()
        {
            var devices = SettingsManager.UserDevices?.Items;
            if (devices == null) return;

            lock (SettingsManager.UserDevices.SyncRoot)
            {
                foreach (var ud in devices)
                {
                    if (ud?.ForceFeedbackState != null && ud.Device != null)
                    {
                        try { ud.ForceFeedbackState.StopDeviceForces(ud.Device); }
                        catch { /* best effort */ }
                    }
                }
            }
        }

        private void CloseAllDevices()
        {
            var devices = SettingsManager.UserDevices?.Items;
            if (devices == null) return;

            lock (SettingsManager.UserDevices.SyncRoot)
            {
                foreach (var ud in devices)
                {
                    if (ud?.Device != null)
                    {
                        try { ud.Device.Dispose(); }
                        catch { /* best effort */ }
                        ud.ClearRuntimeState();
                    }
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Gyro engage state (per-slot, per-tick)
        // ─────────────────────────────────────────────

        /// <summary>Settles each slot's <see cref="GyroEngagedFromButton"/>
        /// bit once per tick from the slot's configured
        /// <c>GyroAimEngageButton</c> + <c>GyroAimEngageMode</c>. Hold mode
        /// tracks the button state directly (empty descriptor = always-on);
        /// Toggle mode flips the sticky bit on each rising edge (empty
        /// descriptor = no-op, the bit stays whatever the macro last set).
        /// Runs between Step 2 (UpdateInputStates) and Step 3
        /// (UpdateOutputStates) so both gyro evaluators — mapping-row reads
        /// and motion passthrough — see a single, consistent engaged
        /// decision for the tick.
        ///
        /// <para>Picks the first device on the slot that has a non-empty
        /// engage button configured. Multi-device slots with multiple
        /// engage buttons configured pick the first per UserSettings
        /// storage order; this is intentional — the engage button is a
        /// per-slot ergonomic, not a per-device setting at the runtime
        /// level. Configuring engage on two devices simultaneously is
        /// supported by editing the second device's PadSetting but only
        /// the first-listed wins at runtime.</para></summary>
        private void UpdateGyroEngageStates()
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return;

            for (int slot = 0; slot < MaxPads; slot++)
            {
                if (!SettingsManager.SlotCreated[slot])
                {
                    GyroEngagedFromButton[slot] = false;
                    _prevAimEngageButtonDown[slot] = false;
                    continue;
                }

                // First device on the slot with a configured engage button
                // wins. Empty descriptor everywhere → always-on (Hold-default).
                string descriptor = "";
                string deviceGuid = "";
                string mode = "Hold";
                lock (settings.SyncRoot)
                {
                    for (int i = 0; i < settings.Items.Count; i++)
                    {
                        var us = settings.Items[i];
                        if (us == null || us.MapTo != slot) continue;
                        var ps = us.GetPadSetting();
                        if (ps == null) continue;
                        if (string.IsNullOrEmpty(ps.GyroAimEngageButton)) continue;
                        descriptor = ps.GyroAimEngageButton;
                        deviceGuid = ps.GyroAimEngageDeviceGuid ?? "";
                        mode = string.IsNullOrEmpty(ps.GyroAimEngageMode) ? "Hold" : ps.GyroAimEngageMode;
                        break;
                    }
                }

                bool buttonDown = !string.IsNullOrEmpty(descriptor)
                    && (SourceCoercion.ButtonHeldProvider?.Invoke(deviceGuid, descriptor, slot) ?? false);

                if (mode == "Toggle")
                {
                    // Rising edge → flip the sticky bit. Falling edge,
                    // empty descriptor, and held state all leave the bit
                    // alone — Toggle never auto-disengages.
                    if (buttonDown && !_prevAimEngageButtonDown[slot])
                        GyroEngagedFromButton[slot] = !GyroEngagedFromButton[slot];
                }
                else
                {
                    // Hold mode (default). Empty descriptor reads as
                    // always-on to preserve the pre-v3.2.4 behavior where
                    // no engage button = no gating from this source.
                    GyroEngagedFromButton[slot] = string.IsNullOrEmpty(descriptor) ? true : buttonDown;
                }
                _prevAimEngageButtonDown[slot] = buttonDown;
            }
        }

        // ── Haptic mirror engage (#185): third member of the engage family ──

        /// <summary>Engage configs for the haptic mirror, wired by InputService
        /// from the per-device configs (the same source the
        /// passthrough provider reads). Returns EVERY passthrough-enabled
        /// config on the slot with its device GUID, including mode 0 (Always),
        /// or null when none. The gate is per (slot, DEVICE): each device's
        /// config gates only its own sink's cell, so a stale config on another
        /// device GUID can never mute the selected device (the Steam
        /// Controller Always-silent bug). Mode: 0 = Always, 1 = Input (held
        /// descriptor), 2 = Rumble (game vibration active).</summary>
        public Func<int, List<(Guid Device, int Mode, string EngageDeviceGuid, string Button, int ReleaseMs)>>
            HapticMirrorEngageConfigProvider { get; set; }

        // Config snapshot per slot, refreshed at low cadence: the ENGAGE STATE
        // must settle per poll for edge fidelity, but the CONFIG (mode / button
        // / delay) changing within a quarter second of a UI edit is
        // imperceptible, and re-walking the ViewModel dictionaries through the
        // provider at 1000 Hz x 16 slots would put LINQ on the hot loop
        // (code-audit lens 1n). Each entry carries its device's EngageCell,
        // resolved at refresh time, so the per-poll loop touches no dictionary.
        private readonly (int Mode, string EngageDeviceGuid, string Button, int ReleaseMs, HapticToneService.EngageCell Cell)[][]
            _mirrorEngageCfg = new (int, string, string, int, HapticToneService.EngageCell)[MaxPads][];
        private long _mirrorEngageCfgRefreshTick;
        // Cells gated by the PREVIOUS snapshot: any cell that disappears from
        // the new snapshot (config removed, passthrough off, slot deleted) is
        // re-asserted engaged so its sink can never stay muted by stale state.
        private readonly HashSet<HapticToneService.EngageCell> _mirrorEngagePrevCells = new();
        private readonly HashSet<HapticToneService.EngageCell> _mirrorEngageNewCells = new();

        /// <summary>Settles each gated device's haptic-mirror engage cell once
        /// per tick (issue #185). Mirrors <see cref="UpdateGyroEngageStates"/>:
        /// Input mode holds while the chosen descriptor is down (empty
        /// descriptor = always on, the family convention), Rumble mode holds
        /// while any of the slot's four vibration motors is nonzero, and either
        /// source keeps the cell up for its release delay after it drops so the
        /// tone does not clip off instantly. Always entries re-assert engaged
        /// each tick.</summary>
        private void UpdateHapticMirrorEngageStates()
        {
            long now = Environment.TickCount64;

            // Low-cadence config refresh (see field comment).
            if (now - _mirrorEngageCfgRefreshTick >= 250)
            {
                _mirrorEngageCfgRefreshTick = now;
                var provider = HapticMirrorEngageConfigProvider;
                _mirrorEngageNewCells.Clear();
                for (int slot = 0; slot < MaxPads; slot++)
                {
                    (int, string, string, int, HapticToneService.EngageCell)[] entries = null;
                    if (provider != null && SettingsManager.SlotCreated[slot])
                    {
                        try
                        {
                            var cfgs = provider(slot);
                            if (cfgs != null && cfgs.Count > 0)
                            {
                                entries = new (int, string, string, int, HapticToneService.EngageCell)[cfgs.Count];
                                for (int i = 0; i < cfgs.Count; i++)
                                {
                                    var c = cfgs[i];
                                    var cell = HapticToneService.GetOrCreateEngageCell(slot, c.Device);
                                    entries[i] = (c.Mode, c.EngageDeviceGuid, c.Button, c.ReleaseMs, cell);
                                    _mirrorEngageNewCells.Add(cell);
                                }
                            }
                        }
                        catch { entries = null; }
                    }
                    _mirrorEngageCfg[slot] = entries;
                }
                // Re-open any cell the new snapshot no longer gates.
                foreach (var old in _mirrorEngagePrevCells)
                    if (!_mirrorEngageNewCells.Contains(old))
                        old.Engaged = true;
                _mirrorEngagePrevCells.Clear();
                foreach (var c in _mirrorEngageNewCells) _mirrorEngagePrevCells.Add(c);
            }

            for (int slot = 0; slot < MaxPads; slot++)
            {
                var entries = _mirrorEngageCfg[slot];
                if (entries == null) continue;
                for (int i = 0; i < entries.Length; i++)
                {
                    var cfg = entries[i];
                    if (cfg.Mode == 0)
                    {
                        // Always: assert open every tick, covering a cell left
                        // closed by a previous non-Always mode on this device.
                        cfg.Cell.Engaged = true;
                        continue;
                    }

                    bool active;
                    if (cfg.Mode == 2)
                    {
                        // Rumble: any motor the game drives, body or trigger.
                        var v = VibrationStates[slot];
                        active = v != null
                            && (v.LeftMotorSpeed > 0 || v.RightMotorSpeed > 0
                                || v.LeftTriggerMotorSpeed > 0 || v.RightTriggerMotorSpeed > 0);
                    }
                    else
                    {
                        // Input: empty descriptor reads as always-on, matching
                        // the gyro-engage convention.
                        active = string.IsNullOrEmpty(cfg.Button)
                            || (SourceCoercion.ButtonHeldProvider?.Invoke(cfg.EngageDeviceGuid ?? "", cfg.Button, slot) ?? false);
                    }

                    cfg.Cell.Engaged = HapticToneService.HoldEngaged(
                        active, now, ref cfg.Cell.LastActiveTick, cfg.ReleaseMs);
                }
            }
        }

        /// <summary>Settles each slot's left/right trigger-route engaged bits
        /// once per tick (issue #102), from the first device on the slot that
        /// has a non-None route source. Mirrors
        /// <see cref="UpdateGyroEngageStates"/>: Hold tracks the activator
        /// (empty descriptor = always on), Toggle flips the sticky bit on each
        /// rising edge, AlwaysOn ignores the descriptor. The route source
        /// itself being None leaves the side disengaged.</summary>
        private void UpdateTriggerRouteEngageStates()
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return;

            long now = Environment.TickCount64;
            // Low-cadence config refresh (see _triggerRouteCfg). Mirrors
            // UpdateHapticMirrorEngageStates' 250 ms snapshot: the SyncRoot lock +
            // Items scan + GetPadSetting run at 4 Hz, not the ~1 kHz poll rate.
            if (now - _triggerRouteCfgRefreshTick >= 250)
            {
                _triggerRouteCfgRefreshTick = now;
                for (int slot = 0; slot < MaxPads; slot++)
                {
                    TriggerRouteCfg cfg = null;
                    if (SettingsManager.SlotCreated[slot])
                    {
                        lock (settings.SyncRoot)
                        {
                            for (int i = 0; i < settings.Items.Count; i++)
                            {
                                var us = settings.Items[i];
                                if (us == null || us.MapTo != slot) continue;
                                var p = us.GetPadSetting();
                                if (p == null) continue;
                                bool lActive = RouteSideActive(p.LeftTriggerRouteSource, p.LeftTriggerRouteMode);
                                bool rActive = RouteSideActive(p.RightTriggerRouteSource, p.RightTriggerRouteMode);
                                if (!lActive && !rActive) continue;
                                cfg = new TriggerRouteCfg
                                {
                                    SrcLeft = lActive ? ParseRouteSource(p.LeftTriggerRouteSource) : (byte)0,
                                    SrcRight = rActive ? ParseRouteSource(p.RightTriggerRouteSource) : (byte)0,
                                    ScaleLeft = ParseRouteScale(p.LeftTriggerRouteScale),
                                    ScaleRight = ParseRouteScale(p.RightTriggerRouteScale),
                                    RedirectLeft = p.LeftTriggerRouteMode == "Redirect",
                                    RedirectRight = p.RightTriggerRouteMode == "Redirect",
                                    ActLeft = p.LeftTriggerRouteActivator,
                                    ActLeftGuid = p.LeftTriggerRouteActivatorDeviceGuid,
                                    ActLeftMode = p.LeftTriggerRouteActivatorMode,
                                    ActRight = p.RightTriggerRouteActivator,
                                    ActRightGuid = p.RightTriggerRouteActivatorDeviceGuid,
                                    ActRightMode = p.RightTriggerRouteActivatorMode,
                                };
                                break;
                            }
                        }
                    }
                    _triggerRouteCfg[slot] = cfg;
                }
            }

            for (int slot = 0; slot < MaxPads; slot++)
            {
                var cfg = _triggerRouteCfg[slot];
                if (cfg == null)
                {
                    TriggerRouteEngagedLeft[slot] = false;
                    TriggerRouteEngagedRight[slot] = false;
                    _prevTriggerRouteLeftDown[slot] = false;
                    _prevTriggerRouteRightDown[slot] = false;
                    _routeSourceLeft[slot] = 0;
                    _routeSourceRight[slot] = 0;
                    continue;
                }

                // Publish resolved config every tick (trivial array writes) so the
                // downstream routing pass stays unchanged.
                _routeSourceLeft[slot] = cfg.SrcLeft;
                _routeSourceRight[slot] = cfg.SrcRight;
                _routeScaleLeft[slot] = cfg.ScaleLeft;
                _routeScaleRight[slot] = cfg.ScaleRight;
                _routeRedirectLeft[slot] = cfg.RedirectLeft;
                _routeRedirectRight[slot] = cfg.RedirectRight;

                bool srcL = cfg.SrcLeft != 0;
                bool srcR = cfg.SrcRight != 0;

                // Settle unconditionally (the activator edge state must advance even
                // when the source is None) then AND with the source-active flag.
                bool leftSettled = SettleRouteActivator(
                    slot, cfg.ActLeft, cfg.ActLeftGuid, cfg.ActLeftMode,
                    _prevTriggerRouteLeftDown, TriggerRouteEngagedLeft[slot], out bool leftDown);
                TriggerRouteEngagedLeft[slot] = srcL && leftSettled;
                _prevTriggerRouteLeftDown[slot] = leftDown;

                bool rightSettled = SettleRouteActivator(
                    slot, cfg.ActRight, cfg.ActRightGuid, cfg.ActRightMode,
                    _prevTriggerRouteRightDown, TriggerRouteEngagedRight[slot], out bool rightDown);
                TriggerRouteEngagedRight[slot] = srcR && rightSettled;
                _prevTriggerRouteRightDown[slot] = rightDown;
            }
        }

        /// <summary>Trigger-route source enum parse:
        /// 0 None, 1 MainLeft, 2 MainRight, 3 MaxOfBoth, 4 SumOfBoth.</summary>
        internal static byte ParseRouteSource(string s) => s switch
        {
            "MainLeft" => 1, "MainRight" => 2, "MaxOfBoth" => 3, "SumOfBoth" => 4, _ => 0,
        };

        /// <summary>True when one trigger's routing is live: a real source picked
        /// (not None) and the Mode not explicitly Off. Source None and Mode Off are
        /// both off switches (the recipe exposes both), so either one disables.</summary>
        internal static bool RouteSideActive(string source, string mode)
            => ParseRouteSource(source) != 0 && mode != "Off";

        /// <summary>Parses the per-trigger route Scale slider (0-200%, stored as an
        /// integer string) to a 0.0-2.0 multiplier. Out-of-range or unparseable
        /// values clamp to the 0-200 band; default 100% maps to 1.0.</summary>
        private static double ParseRouteScale(string s)
            => System.Math.Clamp(int.TryParse(s, out int v) ? v : 100, 0, 200) / 100.0;

        /// <summary>Trigger rumble routing (#102): given a slot's post-gain
        /// main-motor amplitudes, computes the trigger-channel injection (routed
        /// main-motor amplitude max-combined with the macro trigger override) and
        /// flags which main motors to silence (Redirect mode), reading the per-slot
        /// engaged bits plus resolved source/scale/mode captured by
        /// <see cref="UpdateTriggerRouteEngageStates"/>. The routed value comes from
        /// the pre-redirect main motor so Redirect moves the energy to the trigger
        /// rather than dropping it. The macro override is independent of the route
        /// activator, so it contributes even when both routing sides are disengaged.</summary>
        private void ApplyTriggerRouting(int slot, ushort mainL, ushort mainR,
            out ushort routedLeft, out ushort routedRight, out bool zeroMainL, out bool zeroMainR)
        {
            routedLeft = 0; routedRight = 0; zeroMainL = false; zeroMainR = false;
            if (TriggerRouteEngagedLeft[slot])
            {
                routedLeft = RouteMain(_routeSourceLeft[slot], _routeScaleLeft[slot], mainL, mainR);
                if (_routeRedirectLeft[slot]) MarkRedirect(_routeSourceLeft[slot], ref zeroMainL, ref zeroMainR);
            }
            if (TriggerRouteEngagedRight[slot])
            {
                routedRight = RouteMain(_routeSourceRight[slot], _routeScaleRight[slot], mainL, mainR);
                if (_routeRedirectRight[slot]) MarkRedirect(_routeSourceRight[slot], ref zeroMainL, ref zeroMainR);
            }

            // Macro trigger override (#102) max-combines with the routed value, the
            // same way MacroRumbleOverride layers onto the main motors.
            MacroTriggerRumbleOverrides[slot].ComputeMotors(out ushort macroLT, out ushort macroRT);
            if (macroLT > routedLeft) routedLeft = macroLT;
            if (macroRT > routedRight) routedRight = macroRT;
        }

        /// <summary>Selects the routed source amplitude per <paramref name="source"/>
        /// (1 MainLeft, 2 MainRight, 3 MaxOfBoth, 4 SumOfBoth) and applies the
        /// 0.0-2.0 scale, clamped to the ushort range.</summary>
        private static ushort RouteMain(byte source, double scale, ushort mainL, ushort mainR)
        {
            int v = source switch
            {
                1 => mainL,
                2 => mainR,
                3 => System.Math.Max(mainL, mainR),
                4 => System.Math.Min(mainL + mainR, 65535),
                _ => 0,
            };
            if (v <= 0 || scale <= 0) return 0;
            return (ushort)System.Math.Clamp((long)System.Math.Round(v * scale), 0, 65535);
        }

        /// <summary>Flags the main motor(s) a Redirect route draws from: MainLeft
        /// silences left, MainRight silences right, Max / Sum silence both.</summary>
        private static void MarkRedirect(byte source, ref bool zeroL, ref bool zeroR)
        {
            if (source == 1 || source >= 3) zeroL = true;
            if (source == 2 || source >= 3) zeroR = true;
        }

        /// <summary>Sony dispatcher trigger path (#102): computes the trigger-channel
        /// injection (routed main-motor amplitude + macro trigger override) for one
        /// (slot, device) and max-combines it into the impulse-trigger amplitudes the
        /// caller already scaled, so the DualSense AT Vibration auto-route picks up
        /// routed rumble the same way Xbox impulse triggers do. The main-motor source
        /// mirrors the Sony main-rumble provider (macro main rumble + constant force +
        /// per-device gain). Runs on the dispatcher thread, so it takes caller-owned
        /// scratch to stay off the input thread's buffers.</summary>
        internal void ApplyTriggerRoutingForSony(int slot, PadSetting devicePs, Vibration raw,
            Vibration macroScratch, Vibration cfScratch, ref ushort triggerL, ref ushort triggerR)
        {
            if (slot < 0 || slot >= MaxPads || raw == null) return;
            var withMacro = MacroRumbleOverride.Merge(raw, MacroRumbleOverrides[slot], macroScratch);
            var eff = ConstantForceEvaluator.Resolve(withMacro, devicePs, cfScratch);
            ScaleRumbleForDevice(eff.LeftMotorSpeed, eff.RightMotorSpeed, devicePs,
                out ushort mainL, out ushort mainR);
            ApplyTriggerRouting(slot, mainL, mainR,
                out ushort routedLT, out ushort routedRT, out _, out _);
            if (routedLT > triggerL) triggerL = routedLT;
            if (routedRT > triggerR) triggerR = routedRT;
        }

        /// <summary>Sony dispatcher main-motor path (#102): reports whether the slot's
        /// engaged Redirect routing should silence each main motor on the physical
        /// DualSense, mirroring the Redirect zeroing the Xbox physical write applies in
        /// ApplyForceFeedback. The game-facing virtual-controller state is unaffected.</summary>
        internal void GetTriggerRouteMainRedirect(int slot, out bool zeroMainL, out bool zeroMainR)
        {
            zeroMainL = false; zeroMainR = false;
            if (slot < 0 || slot >= MaxPads) return;
            if (TriggerRouteEngagedLeft[slot] && _routeRedirectLeft[slot])
                MarkRedirect(_routeSourceLeft[slot], ref zeroMainL, ref zeroMainR);
            if (TriggerRouteEngagedRight[slot] && _routeRedirectRight[slot])
                MarkRedirect(_routeSourceRight[slot], ref zeroMainL, ref zeroMainR);
        }

        /// <summary>Settles one trigger's route activator. Returns the engaged
        /// state and outputs the raw button-down for next-tick edge detection.
        /// Hold tracks the button (empty descriptor = always on); Toggle flips
        /// on rising edge; AlwaysOn ignores the descriptor.</summary>
        private static bool SettleRouteActivator(int slot, string descriptor, string deviceGuid,
            string mode, bool[] prevDown, bool curEngaged, out bool buttonDown)
        {
            buttonDown = !string.IsNullOrEmpty(descriptor)
                && (SourceCoercion.ButtonHeldProvider?.Invoke(deviceGuid ?? "", descriptor, slot) ?? false);
            if (string.IsNullOrEmpty(mode)) mode = "Hold";
            if (mode == "AlwaysOn") return true;
            if (mode == "Toggle")
                return (buttonDown && !prevDown[slot]) ? !curEngaged : curEngaged;
            return string.IsNullOrEmpty(descriptor) || buttonDown; // Hold: empty = always on
        }

        /// <summary>Clears both gyro-engage per-slot bits and the edge-
        /// detection scratch. Called by the App layer after a profile
        /// switch / settings reload so the new profile's engage state
        /// doesn't carry the prior profile's Toggle stickiness.</summary>
        public void ResetGyroEngageStates()
        {
            for (int i = 0; i < MaxPads; i++)
            {
                GyroEngagedFromButton[i] = false;
                GyroEngagedFromMacro[i] = false;
                _prevAimEngageButtonDown[i] = false;
            }
        }

        /// <summary>Clears both trigger-route per-slot engaged bits and the
        /// edge-detection scratch (issue #102). Called by the App layer after a
        /// profile switch / settings reload so a new profile's Toggle activator
        /// doesn't inherit the prior profile's sticky engaged state.</summary>
        public void ResetTriggerRouteEngageStates()
        {
            for (int i = 0; i < MaxPads; i++)
            {
                TriggerRouteEngagedLeft[i] = false;
                TriggerRouteEngagedRight[i] = false;
                _prevTriggerRouteLeftDown[i] = false;
                _prevTriggerRouteRightDown[i] = false;
                _routeSourceLeft[i] = 0;
                _routeSourceRight[i] = 0;
            }
            // Force an immediate re-snapshot on the next poll: the config cache is
            // 250 ms-cadenced, so without this the reset (dropping sticky Toggle state
            // on a profile switch) would be re-applied from the STALE old-profile
            // snapshot for up to 250 ms.
            _triggerRouteCfgRefreshTick = 0;
        }

        // ─────────────────────────────────────────────
        //  Touchpad gesture per-tick driver
        // ─────────────────────────────────────────────

        /// <summary>250 ms snapshot of device → assigned slots for the two
        /// gesture walks below, refreshed on the poll thread. Per-poll
        /// UserSettings.SyncRoot walks per touchpad/mouse device shared the
        /// UI thread's whole-save lock holds and allocated a fresh int[] per
        /// poll (code-audit lens 1n); assignment changes only on user edit,
        /// so the same 250 ms cadence as _triggerRouteCfg / _mirrorEngageCfg
        /// applies. Poll thread only, no lock on the read path.</summary>
        private readonly Dictionary<Guid, int[]> _assignedSlotsSnapshot = new();
        private long _assignedSlotsRefreshTick;

        private int[] GetAssignedSlotsSnapshot(Guid deviceGuid)
        {
            long now = System.Environment.TickCount64;
            if (now - _assignedSlotsRefreshTick >= 250)
            {
                _assignedSlotsRefreshTick = now;
                _assignedSlotsSnapshot.Clear();
                var userSettings = SettingsManager.UserSettings;
                if (userSettings != null)
                {
                    // Build per-device slot lists under one lock hold per
                    // refresh. The 4 Hz allocation cost replaces a per-poll
                    // stackalloc + int[] per device.
                    lock (userSettings.SyncRoot)
                    {
                        for (int i = 0; i < userSettings.Items.Count; i++)
                        {
                            var us = userSettings.Items[i];
                            if (us == null || us.MapTo < 0) continue;
                            _assignedSlotsSnapshot.TryGetValue(us.InstanceGuid, out var slots);
                            if (slots == null)
                            {
                                _assignedSlotsSnapshot[us.InstanceGuid] = new[] { us.MapTo };
                                continue;
                            }
                            // Dedup. A device should only appear once per
                            // slot, but defensively skip duplicates so the
                            // recognizer doesn't tick twice for the same key.
                            bool dup = false;
                            for (int j = 0; j < slots.Length; j++) { if (slots[j] == us.MapTo) { dup = true; break; } }
                            if (dup) continue;
                            var grown = new int[slots.Length + 1];
                            System.Array.Copy(slots, grown, slots.Length);
                            grown[slots.Length] = us.MapTo;
                            _assignedSlotsSnapshot[us.InstanceGuid] = grown;
                        }
                    }
                }
            }
            return _assignedSlotsSnapshot.TryGetValue(deviceGuid, out var found)
                ? found : System.Array.Empty<int>();
        }

        /// <summary>Drives the gesture recognizer for every touchpad
        /// surface this device exposes, once per slot the device is
        /// assigned to. One context per <c>(slot, device, pad)</c>
        /// triple, lazily allocated. Called from Step 2 after the
        /// device's <c>InputState</c> snapshot lands; recognizer fires
        /// populate
        /// <see cref="Engine.Touchpad.TouchpadGestureContext.FiredGesturesThisFrame"/>
        /// which the SourceCoercion gesture-fired provider reads each
        /// time a mapping row resolves a touchpad-gesture source.
        ///
        /// <para>Slot fan-out: the same physical (device, pad) can be
        /// assigned to multiple slots with different Touchpad-tab
        /// toggles. Each slot ticks the recognizer with its own
        /// settings, so slot 1's "4-way OFF" truly stops slot 1's
        /// mapping rows from firing even when slot 0 has it ON.
        /// Recording-mode bypass is slot-agnostic (the recorder
        /// captures finger paths per (device, pad), not per slot)
        /// and runs once before any per-slot context update.</para></summary>
        private void UpdateGestureContexts(Engine.Data.UserDevice ud, CustomInputState newState)
        {
            if (ud == null || newState == null) return;
            if (newState.Touchpads == null || newState.Touchpads.Length == 0) return;

            // Slots this device is currently assigned to, from the shared
            // 250 ms snapshot (lock-free, allocation-free per poll). No
            // assigned slots → no contexts to tick (gestures don't need to
            // run for an unmapped device).
            int[] assignedSlots = GetAssignedSlotsSnapshot(ud.InstanceGuid);
            if (assignedSlots.Length == 0) return;

            long nowMs = System.Environment.TickCount64;
            for (int p = 0; p < newState.Touchpads.Length; p++)
            {
                var pad = newState.Touchpads[p];
                if (pad == null) continue;

                // Recording-mode bypass: while the recorder dialog is
                // capturing this (device, pad), feed it the raw
                // TouchpadInputState once (slot-agnostic) and skip
                // every slot's recognizer evaluation for this pad.
                // Drops any in-flight context for the pad so a stale
                // path doesn't fire the moment recording stops.
                if (RecordingTargetPadIdx == p &&
                    RecordingTargetDeviceGuid == ud.InstanceGuid)
                {
                    foreach (int slot in assignedSlots)
                    {
                        if (GestureContexts.TryGetValue((slot, ud.InstanceGuid, p), out var ctxR))
                            ctxR.Reset();
                    }
                    var tickHandler = RecordingTick;
                    if (tickHandler != null)
                    {
                        try { tickHandler(pad); }
                        catch { /* dialog teardown can race — ignore */ }
                    }
                    continue;
                }

                foreach (int slot in assignedSlots)
                {
                    var key = (slot, ud.InstanceGuid, p);
                    if (!GestureContexts.TryGetValue(key, out var ctx))
                    {
                        ctx = new Engine.Touchpad.TouchpadGestureContext();
                        GestureContexts[key] = ctx;
                    }

                    var settings = TouchpadGestureSettingsProvider?.Invoke(slot, ud.InstanceGuid, p)
                        ?? Engine.Touchpad.TouchpadGestureSettings.Default();

                    Engine.Touchpad.GestureRecognizer.Update(
                        padIdx: p, ctx: ctx, pad: pad, settings: settings,
                        nowMs: nowMs, shapeTemplates: _shapeTemplates);
                }
            }
        }

        /// <summary>Drops every gesture context. Called on profile
        /// switch and on engine stop so a stale partial gesture doesn't
        /// carry across.</summary>
        public void ResetGestureContexts()
        {
            GestureContexts.Clear();
            MouseGestureContexts.Clear();
        }

        /// <summary>Mouse-gesture recognizer walk (issue #200), sibling of
        /// <see cref="UpdateGestureContexts"/> for mouse-class devices, which
        /// never enter the touchpad walk (their state carries no Touchpads).
        /// Reads the already-published MouseRawDX/DY counts rather than
        /// consuming RawInput deltas again: the wrapper's consume-and-zero
        /// read owns that source, and a second consumer would starve it.
        /// Polling thread only.</summary>
        private void UpdateMouseGestureContexts(Engine.Data.UserDevice ud, CustomInputState newState)
        {
            if (ud == null || newState == null) return;
            if (!ud.IsMouse) return;

            // Slots this device is currently assigned to, from the same
            // 250 ms snapshot the touchpad lane reads.
            int[] assignedSlots = GetAssignedSlotsSnapshot(ud.InstanceGuid);
            if (assignedSlots.Length == 0) return;

            // Raw unclamped counts, published by the wrapper from the same
            // single RawInput consume that feeds the axes (#200). Recovery
            // from the clamped Axis[0/1] capped at ±16 counts per poll, so a
            // fast flick saturated both axes to a 1:1 ratio and the ax>=ay
            // tie-break classified a mostly-vertical flick as horizontal.
            double dxCounts = newState.MouseRawDX;
            double dyCounts = newState.MouseRawDY;

            long nowMs = System.Environment.TickCount64;
            foreach (int slot in assignedSlots)
            {
                var key = (slot, ud.InstanceGuid);
                if (!MouseGestureContexts.TryGetValue(key, out var ctx))
                {
                    ctx = new Engine.Mouse.MouseGestureContext();
                    MouseGestureContexts[key] = ctx;
                }

                var settings = MouseGestureSettingsProvider?.Invoke(slot, ud.InstanceGuid)
                    ?? Engine.Mouse.MouseGestureSettings.Default();

                // Each selected gesture button runs its own session; hand
                // the recognizer the raw pressed mask and let it fan out.
                int pressedMask = 0;
                for (int b = 0; b < Engine.Mouse.MouseGestureContext.ButtonCount
                    && b < newState.Buttons.Length; b++)
                {
                    if (newState.Buttons[b]) pressedMask |= 1 << b;
                }

                Engine.Mouse.MouseGestureRecognizer.Update(
                    ctx, settings, pressedMask, dxCounts, dyCounts, nowMs);
            }
        }

        // ─────────────────────────────────────────────
        //  Motion snapshots (for DSU server)
        // ─────────────────────────────────────────────

        /// <summary>Unit conversion: SDL gyro rad/s → DSU deg/s.</summary>
        private const float RadToDeg = 180f / MathF.PI;

        /// <summary>Unit conversion: SDL accel m/s² → DSU g-force.</summary>
        private const float MsToG = 1f / 9.80665f;

        /// <summary>
        /// Snapshots per-slot motion data driven by the slot's MappingSet
        /// rows. Gyro source comes from the first online device whose
        /// mapping row targets <c>MotionGyro</c>; accel source from the
        /// first online device whose mapping row targets <c>MotionAccel</c>.
        /// The two sub-channels can resolve to different devices.
        /// Called on the polling thread after Step 2 (UpdateInputStates).
        ///
        /// <para>Pre-v3.2.3 this function walked <c>UserSettings.Items</c>
        /// in storage order and took the first-online device with sensors
        /// as the motion source. That hid the source-selection decision
        /// from the user; v3.2.3 surfaces it as mapping-table rows that
        /// the user can add, remove, and reorder. Multi-device hand-off
        /// (Switch Pro off → DualSense on) still works naturally because
        /// the engine walks each row's sources in order every tick and
        /// the first online one wins.</para>
        /// </summary>
        private void UpdateMotionSnapshots()
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return;

            // Microseconds since an arbitrary epoch. Scale in double:
            // GetTimestamp() * 1_000_000 overflows Int64 once the machine
            // has been up long enough (~10 days at a 10 MHz QPC), which
            // would make the relayed sensor timestamp wrap and jump.
            long timestampUs = (long)(Stopwatch.GetTimestamp()
                * (1_000_000.0 / Stopwatch.Frequency));

            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
                // Empty pad — nothing mapped means no motion data to snapshot
                // and no battery to read. Skip the FindByPadIndex lock+scan
                // that would otherwise run 14× per cycle on a 2-active-slot
                // setup.
                if (!SettingsManager.SlotCreated[padIndex])
                {
                    var mb = MotionSnapshots[padIndex];
                    if (mb.HasMotion)
                        MotionSnapshots[padIndex] = new MotionSnapshot { HasMotion = false };
                    continue;
                }

                // Battery scan over the slot's assigned devices. Two values
                // come out of one walk: the slot-collapsed first-online-with-
                // data reading (BatteryPercents/Charging, which the virtual
                // controller reports to the OS as the slot's single battery),
                // and an all-device change signature so the Battery lightbar's
                // per-device repaint kick fires when ANY device's battery
                // changes, not just the first. Independent of motion.
                int slotCount = settings.FindByPadIndex(padIndex, _padIndexBuffer);
                int batteryPercent = -1;
                bool batteryCharging = false;
                int batterySignature = 17;
                for (int i = 0; i < slotCount; i++)
                {
                    var us = _padIndexBuffer[i];
                    if (us == null) continue;
                    var ud = FindOnlineDeviceByInstanceGuid(us.InstanceGuid);
                    if (ud == null || !ud.IsOnline || ud.Device == null) continue;
                    var state = ud.InputState;
                    if (state == null) continue;
                    if (batteryPercent < 0 && state.BatteryPercent >= 0)
                    {
                        batteryPercent = state.BatteryPercent;
                        batteryCharging = state.BatteryCharging;
                    }
                    batterySignature = batterySignature * 31
                        + state.BatteryPercent * 2 + (state.BatteryCharging ? 1 : 0);
                }

                BatteryPercents[padIndex] = batteryPercent;
                BatteryCharging[padIndex] = batteryCharging;
                if (_batterySignature[padIndex] != batterySignature)
                {
                    _batterySignature[padIndex] = batterySignature;
                    UserEffectsDispatcher.NotifyBatteryPercentChanged(padIndex);
                }

                // Motion source resolution from the slot's MappingSet.
                // Sony-class slots (and any future motion-capable VC) have
                // motion rows populated by EnsureMotionRows on load + on
                // device-assignment events. Non-Sony slots have no motion
                // rows → both resolves return null → HasMotion=false.
                var ms = (SettingsManager.SlotMappingSets != null
                    && padIndex < SettingsManager.SlotMappingSets.Length)
                    ? SettingsManager.SlotMappingSets[padIndex] : null;

                var gyroSrc  = ResolveMotionSource(ms, MappingSetMigrator.MotionGyroTarget,
                    requireGyro: true);
                var accelSrc = ResolveMotionSource(ms, MappingSetMigrator.MotionAccelTarget,
                    requireGyro: false);

                if (gyroSrc.Ud == null && accelSrc.Ud == null)
                {
                    MotionSnapshots[padIndex] = new MotionSnapshot
                    {
                        TimestampUs = timestampUs,
                        HasMotion = false
                    };
                    continue;
                }

                // Accel: raw scaled read, no tuning chain (PadForge has no
                // per-axis accel tuning; the Gyro tab knobs are gyro-only).
                // Per-row Invert on the source flips all three axes uniformly
                // — same semantics as the gyro path below.
                float ax = 0f, ay = 0f, az = 0f;
                if (accelSrc.Ud != null)
                {
                    var s = accelSrc.Ud.InputState;
                    // "Motion Accel L" sources the slot's single IMU stream
                    // from the aux (Nunchuk / left Joy-Con) accelerometer
                    // instead of the body's (#199 follow-up). Same scaling
                    // and invert semantics either way.
                    var accel = (accelSrc.Src != null
                        && MappingSetMigrator.IsMotionAccelAuxDescriptor(accelSrc.Src.Descriptor))
                        ? s.AccelAux : s.Accel;
                    if (accel != null && accel.Length >= 3)
                    {
                        ax = accel[0] * MsToG;
                        ay = accel[1] * MsToG;
                        az = accel[2] * MsToG;
                        if (accelSrc.Src != null && accelSrc.Src.Invert)
                        {
                            ax = -ax; ay = -ay; az = -az;
                        }
                    }
                }

                // Gyro: per-(device, slot) Gyro tab tuning chain via
                // GetPassthroughGyro — calibration bias, deadzone,
                // sensitivity, smoothing, invert, etc. The function
                // returns raw rad/s when the device's Apply Tuning to
                // Motion Passthrough toggle is off, or when every
                // Gyro tab control is at its default on an uncalibrated
                // device. Native sensor frame preserved (no sign
                // transform); the DSU server's BuildPadDataPacket and
                // the Sony report packers apply their own protocol-
                // specific frames downstream. The per-row Invert on the
                // mapping source stacks on top of the Gyro tab toggles
                // (both true = no net flip) so the mapping table's
                // checkbox behaves the same way it does for every
                // other source kind.
                float gx = 0f, gy = 0f, gz = 0f;
                if (gyroSrc.Ud != null)
                {
                    var s = gyroSrc.Ud.InputState;
                    if (s.Gyro != null && s.Gyro.Length >= 3)
                    {
                        SourceCoercion.GetPassthroughGyro(
                            s, gyroSrc.Ud.InstanceGuidString, padIndex,
                            out float tunedPitch, out float tunedYaw, out float tunedRoll);
                        gx = tunedPitch * RadToDeg;
                        gy = tunedYaw * RadToDeg;
                        gz = tunedRoll * RadToDeg;
                        if (gyroSrc.Src != null && gyroSrc.Src.Invert)
                        {
                            gx = -gx; gy = -gy; gz = -gz;
                        }
                    }
                }

                MotionSnapshots[padIndex] = new MotionSnapshot
                {
                    AccelX = ax,
                    AccelY = ay,
                    AccelZ = az,
                    GyroPitch = gx,
                    GyroYaw = gy,
                    GyroRoll = gz,
                    TimestampUs = timestampUs,
                    HasMotion = true
                };
            }
        }

        /// <summary>
        /// Walks the slot's mapping rows for the given motion target name,
        /// returning the first source whose owning device is online and
        /// (when <paramref name="requireGyro"/>) has gyro capability. The
        /// per-tick walk + first-online wins gives natural hand-off when
        /// devices come and go without restarting the engine.
        /// </summary>
        private (UserDevice Ud, MappingSource Src) ResolveMotionSource(
            MappingSet ms, string targetName, bool requireGyro)
        {
            if (ms?.Rows == null) return (null, null);
            for (int r = 0; r < ms.Rows.Count; r++)
            {
                var row = ms.Rows[r];
                if (row == null || row.Target != targetName || row.Sources == null) continue;
                for (int i = 0; i < row.Sources.Count; i++)
                {
                    var src = row.Sources[i];
                    if (src == null) continue;
                    if (!SourceCoercion.IsMotionDescriptor(src.Descriptor)) continue;
                    if (string.IsNullOrEmpty(src.DeviceGuid)) continue;
                    if (!Guid.TryParse(src.DeviceGuid, out var guid)) continue;
                    var ud = FindOnlineDeviceByInstanceGuid(guid);
                    if (ud == null || !ud.IsOnline || ud.Device == null) continue;
                    if (ud.InputState == null) continue;
                    // "Motion Accel L" needs the aux (Nunchuk / left Joy-Con)
                    // sensor, not the body accelerometer (#199 follow-up).
                    bool wantsAux = !requireGyro
                        && MappingSetMigrator.IsMotionAccelAuxDescriptor(src.Descriptor);
                    if (requireGyro ? !ud.Device.HasGyro
                        : (wantsAux ? !ud.Device.HasAccelAux : !ud.Device.HasAccel)) continue;
                    return (ud, src);
                }
            }
            return (null, null);
        }

        /// <summary>
        /// Broadcasts motion data to DSU clients if the server is active.
        /// </summary>
        private void BroadcastDsuMotion()
        {
            var server = DsuServer;
            if (server == null) return;

            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
                bool connected = IsSlotActive(padIndex);
                server.BroadcastMotion(padIndex, MotionSnapshots[padIndex], connected);
            }
        }

        // ─────────────────────────────────────────────
        //  Error helper
        // ─────────────────────────────────────────────

        private void RaiseError(string message, Exception ex)
        {
            ErrorOccurred?.Invoke(this, new InputExceptionEventArgs(message, ex));
        }

        // ─────────────────────────────────────────────
        //  Win32 timer resolution + power management
        // ─────────────────────────────────────────────

        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint timeEndPeriod(uint uPeriod);

        // Multimedia timer callback for x360ce-style fallback.
        private delegate void TimerCallback(uint uTimerID, uint uMsg,
            IntPtr dwUser, IntPtr dw1, IntPtr dw2);

        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint timeSetEvent(uint uDelay, uint uResolution,
            TimerCallback lpTimeProc, IntPtr dwUser, uint fuEvent);

        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint timeKillEvent(uint uTimerID);

        private const uint TIME_PERIODIC = 1;

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint ES_CONTINUOUS = 0x80000000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateWaitableTimerExW(
            IntPtr lpTimerAttributes, IntPtr lpTimerName, uint dwFlags, uint dwDesiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimerEx(
            IntPtr hTimer, ref long lpDueTime, int lPeriod,
            IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine,
            IntPtr WakeContext, uint TolerableDelay);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
        private const uint TIMER_ALL_ACCESS = 0x1F0003;
        private const uint INFINITE = 0xFFFFFFFF;

        // ─────────────────────────────────────────────
        //  IDisposable
        // ─────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            // Tear down NFC here, after Stop() has halted the poll loop (so the
            // suppression latch + a stopped sweep prevent a re-Start) but BEFORE
            // ShutdownSdl() tears down the device list that ShutdownNfcReaders'
            // FindOnlineDeviceByInstanceGuid walks. Calling it from
            // InputService.Dispose after this object was already disposed risked
            // a use-after-dispose whose swallowed throw would skip the actual
            // NfcReaderService teardown (#150, round-4 finding).
            ShutdownNfcReaders();
            ShutdownSdl();
            _disposed = true;

            GC.SuppressFinalize(this);
        }

        ~InputManager()
        {
            Dispose();
        }
    }

    /// <summary>
    /// Partial reference for SettingsManager — the actual implementation is in
    /// Common/SettingsManager.cs. Properties are declared in Step1.
    /// </summary>
    public static partial class SettingsManager
    {
        // See SettingsManager.cs for methods.
        // See InputManager.Step1.UpdateDevices.cs for UserDevices/UserSettings properties.
    }
}
