using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using PadForge.Engine;
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
        /// Per-slot post-processed vibration: <see cref="VibrationStates"/> with
        /// audio bass mixed in and ForceOverall × Left/Right motor strength ×
        /// ForceSwapMotor applied. Populated each polling tick by Step 2's
        /// <c>ComputeFinalVibrationStates</c>. The FFB-tab activity meter, the
        /// DS5/DS4 effect-packet rumble bytes (via
        /// <c>UserEffectsDispatcher.SlotRumbleProvider</c>), and the SDL
        /// physical-rumble path (<c>SetDeviceForces</c>) all read this so the
        /// three surfaces stay in sync. <c>SetDeviceForces</c> therefore does
        /// NOT reapply gain on the scalar branch.
        /// </summary>
        public Vibration[] FinalVibrationStates { get; } = new Vibration[MaxPads];

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
        //  Motion snapshots (for DSU server)
        // ─────────────────────────────────────────────

        /// <summary>Unit conversion: SDL gyro rad/s → DSU deg/s.</summary>
        private const float RadToDeg = 180f / MathF.PI;

        /// <summary>Unit conversion: SDL accel m/s² → DSU g-force.</summary>
        private const float MsToG = 1f / 9.80665f;

        /// <summary>
        /// Snapshots per-slot motion data from the first online device with sensors.
        /// Called on the polling thread after Step 2 (UpdateInputStates).
        /// </summary>
        private void UpdateMotionSnapshots()
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return;

            long timestampUs = Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency;

            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
                int slotCount = settings.FindByPadIndex(padIndex, _padIndexBuffer);
                bool found = false;
                int batteryPercent = -1;
                bool batteryCharging = false;

                for (int i = 0; i < slotCount; i++)
                {
                    var us = _padIndexBuffer[i];
                    if (us == null) continue;

                    var ud = FindOnlineDeviceByInstanceGuid(us.InstanceGuid);
                    if (ud == null || !ud.IsOnline || ud.Device == null)
                        continue;

                    var state = ud.InputState;
                    if (state == null)
                        continue;

                    // First assigned device that reports battery wins. Battery
                    // percent is independent of motion presence — a Sony pad
                    // with no sensors enabled still wants its battery surfaced.
                    if (batteryPercent < 0 && state.BatteryPercent >= 0)
                    {
                        batteryPercent = state.BatteryPercent;
                        batteryCharging = state.BatteryCharging;
                    }

                    if (found) continue;

                    if (!ud.Device.HasGyro && !ud.Device.HasAccel)
                        continue;

                    // SDL standard: Accel in m/s² (Y=up has gravity), Gyro in rad/s
                    // DSU/DS4 convention: negated accel signs, consistent frame
                    // Derived from Switch Pro SDL→DSU mapping (BetterJoy reference)
                    float ax = state.Accel[0] * MsToG;
                    float ay = state.Accel[1] * MsToG;
                    float az = state.Accel[2] * MsToG;
                    float gx = state.Gyro[0] * RadToDeg;
                    float gy = state.Gyro[1] * RadToDeg;
                    float gz = state.Gyro[2] * RadToDeg;

                    MotionSnapshots[padIndex] = new MotionSnapshot
                    {
                        AccelX = -ax,
                        AccelY = -ay,
                        AccelZ = -az,
                        GyroPitch = -gx,
                        GyroYaw = gy,
                        GyroRoll = -gz,
                        TimestampUs = timestampUs,
                        HasMotion = true
                    };
                    found = true;
                }

                BatteryPercents[padIndex] = batteryPercent;
                BatteryCharging[padIndex] = batteryCharging;

                if (!found)
                {
                    MotionSnapshots[padIndex] = new MotionSnapshot
                    {
                        TimestampUs = timestampUs,
                        HasMotion = false
                    };
                }
            }
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
