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
    ///   Step 5: Feed virtual controllers (ViGEm, vJoy, MIDI)
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
        /// Combined vJoy raw output states for custom vJoy slots.
        /// Written by Step 4 (background thread), read by Step 5.
        /// </summary>
        public VJoyRawState[] CombinedVJoyRawStates { get; } = new VJoyRawState[MaxPads];

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
        /// Retrieved output states copied from Step 4 for UI display in Step 6.
        /// </summary>
        public Gamepad[] RetrievedOutputStates { get; } = new Gamepad[MaxPads];

        /// <summary>
        /// Retrieved KBM raw states for UI display (keyboard key + mouse state preview).
        /// </summary>
        public KbmRawState[] RetrievedKbmRawStates { get; } = new KbmRawState[MaxPads];

        /// <summary>
        /// Per-slot vibration states received from games via ViGEmBus.
        /// </summary>
        public Vibration[] VibrationStates { get; } = new Vibration[MaxPads];

        /// <summary>
        /// Per-slot motion snapshots for DSU (cemuhook) streaming.
        /// Written by the polling thread after Step 2, read by the DSU server.
        /// </summary>
        public MotionSnapshot[] MotionSnapshots { get; } = new MotionSnapshot[MaxPads];

        /// <summary>
        /// DSU motion server reference. When set, the polling thread broadcasts
        /// motion data to subscribed clients after snapshotting sensor data.
        /// </summary>
        public DsuMotionServer DsuServer { get; set; }

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
                if (!SDL_Init(SDL_INIT_JOYSTICK | SDL_INIT_GAMEPAD | SDL_INIT_VIDEO | SDL_INIT_HAPTIC))
                {
                    string error = SDL_GetError();
                    RaiseError($"SDL_Init failed: {error}", null);
                    return false;
                }

                // Load PadForge community mappings (extends SDL's built-in gamecontrollerdb).
                string mappingsPath = Path.Combine(AppContext.BaseDirectory, "gamecontrollerdb_padforge.txt");
                if (File.Exists(mappingsPath))
                    SDL_AddGamepadMappingsFromFile(mappingsPath);

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

            RawInputListener.Start();

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
        public void Stop(bool preserveVJoyNodes = false)
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

            StopAllForceFeedback();
            DestroyAllVirtualControllers(preserveVJoyNodes);
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
            // other system timing used by SDL, ViGEm, and the UI dispatcher.
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
                            SetWaitableTimerEx(hTimer, ref dueTime, 0,
                                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0);
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
        //  Slot swap
        // ─────────────────────────────────────────────

        /// <summary>
        /// Swaps controller slot data between two positions.
        /// Same-type swaps keep virtual controllers alive — only the input
        /// routing changes (via MapTo swap in SettingsManager). This avoids
        /// ViGEm disconnect/reconnect flicker and preserves XInput indices.
        /// Cross-type swaps destroy both VCs so Step 5 recreates them with
        /// the correct types.
        /// </summary>
        public void SwapSlots(int slotA, int slotB)
        {
            if (slotA == slotB) return;
            if (slotA < 0 || slotA >= MaxPads) return;
            if (slotB < 0 || slotB >= MaxPads) return;

            var vcA = _virtualControllers[slotA];
            var vcB = _virtualControllers[slotB];
            var typeA = vcA?.Type ?? SlotControllerTypes[slotA];
            var typeB = vcB?.Type ?? SlotControllerTypes[slotB];

            if (typeA == typeB)
            {
                // Same type — virtual controllers stay at their indices.
                // SettingsManager.SwapSlots (called by InputService) swaps
                // MapTo values, which reroutes the entire pipeline:
                //   Step 3 maps device input using MapTo → OutputState
                //   Step 4 combines by MapTo index → CombinedOutputStates
                //   Step 5 feeds CombinedOutputStates[i] → _virtualControllers[i]
                // All per-slot arrays are recomputed each frame from MapTo,
                // so no array swapping needed. Zero game disruption.
            }
            else
            {
                // Cross-type swap — must destroy both VCs so Step 5 recreates
                // them with the correct types in ascending slot order.
                DestroyVirtualController(slotA);
                DestroyVirtualController(slotB);
                _virtualControllers[slotA] = null;
                _virtualControllers[slotB] = null;
                _slotInactiveCounter[slotA] = 0;
                _slotInactiveCounter[slotB] = 0;
            }

            // Always swap type tracking and UI-associated state that
            // travels with the card (not recomputed from MapTo).
            (SlotControllerTypes[slotA], SlotControllerTypes[slotB]) =
                (SlotControllerTypes[slotB], SlotControllerTypes[slotA]);
            (SlotVJoyConfigs[slotA], SlotVJoyConfigs[slotB]) =
                (SlotVJoyConfigs[slotB], SlotVJoyConfigs[slotA]);
            (SlotVJoyIsCustom[slotA], SlotVJoyIsCustom[slotB]) =
                (SlotVJoyIsCustom[slotB], SlotVJoyIsCustom[slotA]);
            (TestRumbleTargetGuid[slotA], TestRumbleTargetGuid[slotB]) =
                (TestRumbleTargetGuid[slotB], TestRumbleTargetGuid[slotA]);
            (MacroSnapshots[slotA], MacroSnapshots[slotB]) =
                (MacroSnapshots[slotB], MacroSnapshots[slotA]);
        }

        /// <summary>
        /// Swaps only data arrays between two slots: SlotControllerTypes,
        /// SlotVJoyConfigs, SlotVJoyIsCustom, TestRumbleTargetGuid, MacroSnapshots.
        /// Does NOT touch virtual controllers or their lifecycle.
        /// Used by EnsureTypeGroupOrder so Step 5 detects the type mismatch
        /// and handles VC recreation naturally on the polling thread,
        /// avoiding the all-VCs-destroyed-at-once race that causes phantom
        /// Xbox controllers.
        /// </summary>
        public void SwapSlotData(int slotA, int slotB)
        {
            if (slotA == slotB) return;
            if (slotA < 0 || slotA >= MaxPads) return;
            if (slotB < 0 || slotB >= MaxPads) return;

            // Hold VJoySyncLock so the polling thread's descriptor sync doesn't
            // observe a half-swapped state (configs from one slot + VC from another).
            lock (VJoySyncLock)
            {

            // Swap engine config arrays.
            (SlotControllerTypes[slotA], SlotControllerTypes[slotB]) =
                (SlotControllerTypes[slotB], SlotControllerTypes[slotA]);
            (SlotVJoyConfigs[slotA], SlotVJoyConfigs[slotB]) =
                (SlotVJoyConfigs[slotB], SlotVJoyConfigs[slotA]);
            (SlotVJoyIsCustom[slotA], SlotVJoyIsCustom[slotB]) =
                (SlotVJoyIsCustom[slotB], SlotVJoyIsCustom[slotA]);
            (TestRumbleTargetGuid[slotA], TestRumbleTargetGuid[slotB]) =
                (TestRumbleTargetGuid[slotB], TestRumbleTargetGuid[slotA]);
            (MacroSnapshots[slotA], MacroSnapshots[slotB]) =
                (MacroSnapshots[slotB], MacroSnapshots[slotA]);
            (_midiConfigs[slotA], _midiConfigs[slotB]) =
                (_midiConfigs[slotB], _midiConfigs[slotA]);

            // Swap virtual controllers so Step 5 doesn't see a type mismatch
            // and needlessly destroy/recreate VCs for cross-type reorders.
            (_virtualControllers[slotA], _virtualControllers[slotB]) =
                (_virtualControllers[slotB], _virtualControllers[slotA]);
            (_slotInactiveCounter[slotA], _slotInactiveCounter[slotB]) =
                (_slotInactiveCounter[slotB], _slotInactiveCounter[slotA]);
            (_slotInitializing[slotA], _slotInitializing[slotB]) =
                (_slotInitializing[slotB], _slotInitializing[slotA]);
            (_createCooldown[slotA], _createCooldown[slotB]) =
                (_createCooldown[slotB], _createCooldown[slotA]);
            (VibrationStates[slotA], VibrationStates[slotB]) =
                (VibrationStates[slotB], VibrationStates[slotA]);
            (CombinedOutputStates[slotA], CombinedOutputStates[slotB]) =
                (CombinedOutputStates[slotB], CombinedOutputStates[slotA]);
            (CombinedVJoyRawStates[slotA], CombinedVJoyRawStates[slotB]) =
                (CombinedVJoyRawStates[slotB], CombinedVJoyRawStates[slotA]);
            (CombinedMidiRawStates[slotA], CombinedMidiRawStates[slotB]) =
                (CombinedMidiRawStates[slotB], CombinedMidiRawStates[slotA]);
            (CombinedKbmRawStates[slotA], CombinedKbmRawStates[slotB]) =
                (CombinedKbmRawStates[slotB], CombinedKbmRawStates[slotA]);

            // Update FeedbackPadIndex so rumble callbacks write to the correct
            // VibrationStates element after the swap.
            if (_virtualControllers[slotA] != null)
                _virtualControllers[slotA].FeedbackPadIndex = slotA;
            if (_virtualControllers[slotB] != null)
                _virtualControllers[slotB].FeedbackPadIndex = slotB;

            // Update vJoy FFB device map entries that reference the swapped indices.
            VJoyVirtualController.UpdateFfbPadIndex(slotA, slotB);

            } // end lock (VJoySyncLock)
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

                for (int i = 0; i < slotCount; i++)
                {
                    var us = _padIndexBuffer[i];
                    if (us == null) continue;

                    var ud = FindOnlineDeviceByInstanceGuid(us.InstanceGuid);
                    if (ud == null || !ud.IsOnline || ud.Device == null)
                        continue;

                    if (!ud.Device.HasGyro && !ud.Device.HasAccel)
                        continue;

                    var state = ud.InputState;
                    if (state == null)
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
                    break;
                }

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
