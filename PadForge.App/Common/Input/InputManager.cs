using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    ///   Step 3: Map CustomInputState → XInput Gamepad via PadSetting rules
    ///   Step 4: Combine multiple devices per virtual controller slot
    ///   Step 5: Feed ViGEmBus virtual Xbox 360 controllers
    ///   Step 6: Retrieve XInput states for UI display
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

        /// <summary>Maximum number of virtual controller slots (4 Xbox 360 + 4 DS4).</summary>
        public const int MaxPads = 8;

        // ─────────────────────────────────────────────
        //  State
        // ─────────────────────────────────────────────

        private Thread _pollingThread;
        private volatile bool _running;
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
        private readonly UserSetting[] _padIndexBuffer = new UserSetting[8];

        /// <summary>
        /// Combined output gamepad states for the virtual controller slots.
        /// Written by Step 4 (background thread), read by UI (InputService).
        /// </summary>
        public Gamepad[] CombinedOutputStates { get; } = new Gamepad[MaxPads];

        /// <summary>
        /// Retrieved output states copied from Step 4 for UI display in Step 6.
        /// </summary>
        public Gamepad[] RetrievedOutputStates { get; } = new Gamepad[MaxPads];

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

                // SDL3: SDL_Init returns bool (true = success), and
                // SDL_INIT_GAMECONTROLLER is renamed to SDL_INIT_GAMEPAD.
                // SDL_INIT_VIDEO is required for keyboard/mouse enumeration.
                if (!SDL_Init(SDL_INIT_JOYSTICK | SDL_INIT_GAMEPAD | SDL_INIT_VIDEO | SDL_INIT_HAPTIC))
                {
                    string error = SDL_GetError();
                    RaiseError($"SDL_Init failed: {error}", null);
                    return false;
                }

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

            StopAllForceFeedback();
            DestroyAllVirtualControllers();
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

            try
            {
                var cycleTimer = new Stopwatch();
                cycleTimer.Start();

                // Run device enumeration immediately on the first cycle so that
                // controllers are detected, virtual devices are created, and force
                // feedback is wired without waiting for the 2-second interval.
                bool firstCycle = true;

                while (_running)
                {
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
                    }
                    catch (Exception ex)
                    {
                        RaiseError("Polling loop error", ex);
                    }

                    // Hybrid sleep/spin-wait for precise timing with low CPU usage.
                    //
                    // Strategy depends on how much time remains:
                    //   - >1.5ms remaining: Thread.Sleep(1) — real sleep, near-zero CPU.
                    //     With timeBeginPeriod(1), wakes in ~1.0-1.5ms.
                    //   - >0ms remaining: Thread.SpinWait(1) — precise busy-wait using
                    //     CPU PAUSE instructions for sub-ms accuracy.
                    //
                    // At PollingIntervalMs=1: mostly spin-wait (work takes ~0.3-0.5ms,
                    //   ~0.5-0.7ms of spinning). CPU is ~1-3% of one core.
                    // At PollingIntervalMs=2+: Thread.Sleep(1) absorbs the bulk of the
                    //   wait, CPU drops to near-zero while maintaining accurate timing.
                    long sleepThresholdTicks = Stopwatch.Frequency * 3 / 2000; // 1.5ms in ticks
                    long remaining = targetTicks - cycleTimer.ElapsedTicks;
                    while (remaining > 0)
                    {
                        if (remaining > sleepThresholdTicks)
                        {
                            Thread.Sleep(1);
                        }
                        else
                        {
                            Thread.SpinWait(1);
                        }
                        remaining = targetTicks - cycleTimer.ElapsedTicks;
                    }
                }
            }
            finally
            {
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
        //  Win32 timer resolution
        // ─────────────────────────────────────────────

        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint timeEndPeriod(uint uPeriod);

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
