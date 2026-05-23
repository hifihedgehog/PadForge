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

        /// <summary>Internal helper for Step 5 to fan-out the
        /// non-active event without exposing direct invocation to other
        /// classes.</summary>
        internal void RaiseHmVcWentNonActive(int padIndex)
        {
            HmVcWentNonActive?.Invoke(this, padIndex);
        }

        /// <summary>Device re-enumeration interval in milliseconds (every 2 seconds).</summary>
        private const int EnumerationIntervalMs = 2000;


        /// <summary>Maximum number of virtual controller slots.</summary>
        public const int MaxPads = 16;

        // ─────────────────────────────────────────────
        //  State
        // ─────────────────────────────────────────────

        private Thread _pollingThread;
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

        /// <summary>Per-(device, touchpad-pad-index) gesture recognizer
        /// state. Lazily populated when a device with a touchpad shows
        /// up; read by <see cref="UpdateGestureContexts"/> each tick and
        /// by the SourceCoercion gesture-fired / gesture-axis providers
        /// during mapping evaluation. Cleared on device disconnect and
        /// on profile switch so a stale partial gesture doesn't carry
        /// across profiles.</summary>
        public readonly System.Collections.Concurrent.ConcurrentDictionary<(System.Guid DeviceId, int PadIdx), Engine.Touchpad.TouchpadGestureContext> GestureContexts
            = new System.Collections.Concurrent.ConcurrentDictionary<(System.Guid, int), Engine.Touchpad.TouchpadGestureContext>();

        /// <summary>Active shape templates ready for the gesture
        /// engine's $P matcher. Built at startup from the in-box
        /// catalog; profile load can append the user's custom
        /// gestures via <see cref="SetShapeTemplates"/>.</summary>
        public System.Collections.Generic.IReadOnlyList<Engine.Touchpad.PDollarTemplate> ShapeTemplates
        {
            get => System.Threading.Volatile.Read(ref _shapeTemplates);
        }
        private System.Collections.Generic.IReadOnlyList<Engine.Touchpad.PDollarTemplate> _shapeTemplates
            = Engine.Touchpad.InBoxShapeTemplates.Build();

        /// <summary>Atomically swaps the active shape-template catalog.
        /// Called on profile load when the per-profile custom gesture
        /// library lands; passes in the in-box set merged with the
        /// custom templates so the recognizer evaluates both.</summary>
        public void SetShapeTemplates(System.Collections.Generic.IReadOnlyList<Engine.Touchpad.PDollarTemplate> templates)
        {
            System.Threading.Volatile.Write(ref _shapeTemplates, templates ?? Engine.Touchpad.InBoxShapeTemplates.Build());
        }

        /// <summary>Per-(device, padIdx) gesture settings provider.
        /// Wired by the App layer against the active profile's
        /// <c>PadSetting.TouchpadSettings</c>; returns
        /// <see cref="Engine.Touchpad.TouchpadGestureSettings.Default"/>
        /// when unwired or when no per-pad settings exist for the
        /// requested device + pad.</summary>
        public System.Func<System.Guid, int, Engine.Touchpad.TouchpadGestureSettings> TouchpadGestureSettingsProvider { get; set; }

        /// <summary>Global suspend flag. Set by
        /// <see cref="ResetGestureSuspend"/> / its UI counterpart from
        /// the configurable suspend hotkey. While true, every
        /// gesture context's per-tick update skips early and returns
        /// no fires. Existing path / continuous-axis state stays so
        /// resuming picks up where it left off.</summary>
        public volatile bool GestureSuspendActive;

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

        /// <summary>Monotonic frame counter feeding the Sony Report 0x01
        /// timestamp / packet-sequence fields. Game-side parsers (e.g. SDL3's
        /// PS5 driver) reject duplicate packet-sequence values, so this MUST
        /// advance every frame regardless of input state.</summary>
        internal long SonyFrameCounter => _sonyFrameCounter;
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

                // Allow screensaver/sleep even while SDL video is active.
                SDL_SetHint(SDL_HINT_VIDEO_ALLOW_SCREENSAVER, "1");

                // SDL3: SDL_Init returns bool (true = success), and
                // SDL_INIT_GAMECONTROLLER is renamed to SDL_INIT_GAMEPAD.
                // SDL_INIT_VIDEO is required for keyboard/mouse enumeration.
                // Note: SDL_Init itself does not enumerate joysticks; the
                // orphan-sweep Wait lives in Step 1's UpdateDevices so the
                // wait happens on the polling thread, not here on the UI
                // thread (InputService.Start is called from MainWindow's
                // constructor before window.Show runs).
                if (!SDL_Init(SDL_INIT_JOYSTICK | SDL_INIT_GAMEPAD | SDL_INIT_VIDEO | SDL_INIT_HAPTIC))
                {
                    string error = SDL_GetError();
                    RaiseError($"SDL_Init failed: {error}", null);
                    return false;
                }

                // Load PadForge community mappings (extends SDL's built-in gamecontrollerdb).
                // File is embedded in the exe so the app ships as a single-file binary
                // with no loose resource files. Stream it in and apply per-line via
                // SDL_AddGamepadMapping rather than the file-path overload.
                LoadEmbeddedGamepadMappings();

                // SDL_INIT_VIDEO disables the screensaver and system sleep by
                // default.  Re-enable both so the PC can sleep when idle.
                SDL_EnableScreenSaver();
                SetThreadExecutionState(ES_CONTINUOUS);

                _sdlInitialized = true;
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

            SDL_Quit();
            _sdlInitialized = false;
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
        }

        /// <summary>
        /// Stops the background polling thread and waits for it to exit.
        /// </summary>
        public void Stop()
        {
            if (!_running)
                return;

            _running = false;

            if (_pollingThread != null && _pollingThread.IsAlive)
            {
                _pollingThread.Join(timeout: TimeSpan.FromSeconds(3));
                _pollingThread = null;
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
            // Keep timeBeginPeriod(1) — it still helps multimedia timers and
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
                            SDL_UpdateJoysticks();

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
                        SDL_UpdateJoysticks();

                        if (firstCycle || _enumerationTimer.ElapsedMilliseconds >= EnumerationIntervalMs)
                        {
                            firstCycle = false;
                            _enumerationTimer.Restart();
                            UpdateDevices();
                        }

                        UpdateInputStates();
                        UpdateGyroEngageStates();
                        UpdateMotionSnapshots();
                        BroadcastDsuMotion();
                        UpdateOutputStates();
                        CombineOutputStates();
                        EvaluateMacros();
                        UpdateVirtualDevices();
                        RetrieveOutputStates();

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

        // ─────────────────────────────────────────────
        //  Touchpad gesture per-tick driver
        // ─────────────────────────────────────────────

        /// <summary>Drives the gesture recognizer for every touchpad
        /// surface this device exposes. One context per
        /// <c>(device, pad)</c> pair, lazily allocated. Called from
        /// Step 2 after the device's <c>InputState</c> snapshot lands;
        /// recognizer fires populate
        /// <see cref="Engine.Touchpad.TouchpadGestureContext.FiredGesturesThisFrame"/>
        /// which the SourceCoercion gesture-fired provider reads each
        /// time a mapping row resolves a touchpad-gesture source.</summary>
        private void UpdateGestureContexts(Engine.Data.UserDevice ud, CustomInputState newState)
        {
            if (ud == null || newState == null) return;
            if (newState.Touchpads == null || newState.Touchpads.Length == 0) return;

            long nowMs = System.Environment.TickCount64;
            for (int p = 0; p < newState.Touchpads.Length; p++)
            {
                var pad = newState.Touchpads[p];
                if (pad == null) continue;

                var key = (ud.InstanceGuid, p);
                if (!GestureContexts.TryGetValue(key, out var ctx))
                {
                    ctx = new Engine.Touchpad.TouchpadGestureContext();
                    GestureContexts[key] = ctx;
                }

                if (GestureSuspendActive)
                {
                    ctx.State = Engine.Touchpad.GestureState.Suspended;
                    ctx.FiredGesturesThisFrame.Clear();
                    continue;
                }
                if (ctx.State == Engine.Touchpad.GestureState.Suspended)
                    ctx.State = Engine.Touchpad.GestureState.Idle;

                var settings = TouchpadGestureSettingsProvider?.Invoke(ud.InstanceGuid, p)
                    ?? Engine.Touchpad.TouchpadGestureSettings.Default();

                Engine.Touchpad.GestureRecognizer.Update(
                    padIdx: p, ctx: ctx, pad: pad, settings: settings,
                    nowMs: nowMs, shapeTemplates: _shapeTemplates);
            }
        }

        /// <summary>Drops every gesture context. Called on profile
        /// switch and on engine stop so a stale partial gesture doesn't
        /// carry across.</summary>
        public void ResetGestureContexts() => GestureContexts.Clear();

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

                // Battery scan stays as a first-online-with-data walk over
                // the slot's assigned devices. Independent of motion.
                int slotCount = settings.FindByPadIndex(padIndex, _padIndexBuffer);
                int batteryPercent = -1;
                bool batteryCharging = false;
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
                        break;
                    }
                }

                int prevPct = BatteryPercents[padIndex];
                BatteryPercents[padIndex] = batteryPercent;
                BatteryCharging[padIndex] = batteryCharging;
                if (prevPct != batteryPercent)
                    UserEffectsDispatcher.NotifyBatteryPercentChanged(padIndex);

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
                    if (s.Accel != null && s.Accel.Length >= 3)
                    {
                        ax = s.Accel[0] * MsToG;
                        ay = s.Accel[1] * MsToG;
                        az = s.Accel[2] * MsToG;
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
                            s, gyroSrc.Ud.InstanceGuid.ToString(), padIndex,
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
                    if (requireGyro ? !ud.Device.HasGyro : !ud.Device.HasAccel) continue;
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
