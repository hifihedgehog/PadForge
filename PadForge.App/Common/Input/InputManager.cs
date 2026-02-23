using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Data;
using SDL2;
using static SDL2.SDL;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Central input manager that runs the device polling pipeline on a background thread.
    /// 
    /// Pipeline (runs at ~1000Hz on a background thread):
    ///   Step 1: Enumerate SDL devices, open new ones, close disconnected ones
    ///   Step 2: Read input states from SDL (or XInput for native Xbox controllers)
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

        /// <summary>Target polling interval in milliseconds (~1000Hz).</summary>
        private const int PollingIntervalMs = 1;

        /// <summary>Device re-enumeration interval in milliseconds (every 2 seconds).</summary>
        private const int EnumerationIntervalMs = 2000;

        /// <summary>Maximum number of virtual controller slots.</summary>
        public const int MaxPads = 4;

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
        /// Combined XInput gamepad states for the 4 virtual controller slots.
        /// Written by Step 4 (background thread), read by UI (InputService).
        /// </summary>
        public Gamepad[] CombinedXiStates { get; } = new Gamepad[MaxPads];

        /// <summary>
        /// Retrieved XInput states read back from the XInput DLL in Step 6.
        /// </summary>
        public Gamepad[] RetrievedXiStates { get; } = new Gamepad[MaxPads];

        /// <summary>
        /// Per-slot vibration states received from games via ViGEmBus.
        /// </summary>
        public Vibration[] VibrationStates { get; } = new Vibration[MaxPads];

        /// <summary>
        /// Current measured polling frequency in Hz.
        /// </summary>
        public double CurrentFrequency { get; private set; }

        /// <summary>
        /// Whether the manager is currently running the polling loop.
        /// </summary>
        public bool IsRunning => _running;

        /// <summary>
        /// Checks whether the given XInput user index (0–3) is currently occupied
        /// by one of our ViGEm virtual controllers. Used by the UI layer to filter
        /// virtual devices out of the device list.
        /// </summary>
        public bool IsViGEmOccupiedSlot(int userIndex)
        {
            lock (_vigemOccupiedXInputSlots)
            {
                return _vigemOccupiedXInputSlots.Contains(userIndex);
            }
        }

        /// <summary>
        /// Returns the set of XInput instance GUIDs for slots currently occupied
        /// by our ViGEm virtual controllers. These devices should be hidden from
        /// the user-facing device list.
        /// </summary>
        public HashSet<Guid> GetViGEmVirtualDeviceGuids()
        {
            var guids = new HashSet<Guid>();
            lock (_vigemOccupiedXInputSlots)
            {
                foreach (int slot in _vigemOccupiedXInputSlots)
                {
                    if (slot >= 0 && slot < XInputInstanceGuids.Length)
                        guids.Add(XInputInstanceGuids[slot]);
                }
            }
            return guids;
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
        /// Initializes the SDL2 library for joystick and game controller support.
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

                // Enable RawInput for better DirectInput device detection on Windows.
                SDL_SetHint(SDL_HINT_JOYSTICK_RAWINPUT, "1");

                // Disable SDL's built-in XInput handling — we handle it natively via XInputInterop.
                SDL_SetHint(SDL_HINT_XINPUT_ENABLED, "0");

                int result = SDL_Init(SDL_INIT_JOYSTICK | SDL_INIT_GAMECONTROLLER);
                if (result != 0)
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
                RaiseError("SDL2.dll not found. Place SDL2.dll next to the exe. " +
                           "Download from https://github.com/libsdl-org/SDL/releases", ex);
                return false;
            }
            catch (Exception ex)
            {
                RaiseError("Failed to initialize SDL2.", ex);
                return false;
            }
        }

        /// <summary>
        /// Shuts down the SDL2 library. Called during disposal.
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
                // Pre-calculate the target interval in high-resolution ticks.
                // Stopwatch.Frequency is ticks per second (e.g. 10,000,000 on most PCs).
                long targetTicks = Stopwatch.Frequency / 1000 * PollingIntervalMs;

                var cycleTimer = new Stopwatch();
                cycleTimer.Start();

                // Run device enumeration immediately on the first cycle so that
                // controllers are detected, virtual devices are created, and force
                // feedback is wired without waiting for the 2-second interval.
                bool firstCycle = true;

                while (_running)
                {
                    cycleTimer.Restart();

                    try
                    {
                        SDL_JoystickUpdate();

                        if (firstCycle || _enumerationTimer.ElapsedMilliseconds >= EnumerationIntervalMs)
                        {
                            firstCycle = false;
                            _enumerationTimer.Restart();
                            UpdateDevices();
                        }

                        UpdateInputStates();
                        UpdateXiStates();
                        CombineXiStates();
                        EvaluateMacros();
                        UpdateVirtualDevices();
                        RetrieveXiStates();

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

                    // Spin-wait for the remainder of the 1ms interval.
                    // Thread.SpinWait executes PAUSE instructions which hint to the
                    // CPU to reduce power during the spin (more efficient than a
                    // pure busy loop, ~1-3% of one core at 1000Hz).
                    while (cycleTimer.ElapsedTicks < targetTicks)
                    {
                        Thread.SpinWait(1);
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
