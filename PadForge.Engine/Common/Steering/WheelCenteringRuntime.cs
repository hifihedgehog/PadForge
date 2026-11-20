using System;
using System.Diagnostics;

namespace PadForge.Engine.Steering
{
    /// <summary>What the Centering card shows of a wheel as it runs.</summary>
    public readonly struct CenteringLive
    {
        /// <summary>Vehicle mode stepped within the last second.</summary>
        public readonly bool Active;

        /// <summary>The wheel's angle in degrees and the centering torque
        /// at the rim in N·m, both signed as the axis.</summary>
        public readonly double AngleDegrees, TorqueNm;

        /// <summary>The speed in use in m/s, and the grip: what the game
        /// or the settings gave, a stopped car's 0 among them. The model
        /// runs no slower than SteeringCurve.LeastSpeed, and keeps that to
        /// itself.</summary>
        public readonly double Speed, Grip;

        /// <summary>The speed came from a game, and so did the grip.</summary>
        public readonly bool SpeedFromGame, GripFromGame;

        /// <summary>The oscillation guard has cut the PC's force, and the
        /// PC's force is off.</summary>
        public readonly bool HeldBack, Stopped;

        public CenteringLive(bool active, in CenteringFrame frame, bool speedFromGame, bool gripFromGame)
        {
            Active = active;
            AngleDegrees = frame.AngleDegrees;
            TorqueNm = frame.TorqueNm;
            Speed = frame.Speed;
            Grip = frame.Grip;
            SpeedFromGame = speedFromGame;
            GripFromGame = gripFromGame;
            HeldBack = frame.HeldBack;
            Stopped = frame.Stopped;
        }
    }

    /// <summary>What the Release Test window shows of a test.</summary>
    public sealed class ReleaseTestStatus
    {
        public ReleaseTestOutcome Outcome { get; init; }

        /// <summary>0 to 1.</summary>
        public double Progress { get; init; }

        /// <summary>The wheel stopped being polled while the test ran: it
        /// was unplugged, or the engine stopped.</summary>
        public bool Stalled { get; init; }

        /// <summary>The two releases so far, one angle a millisecond in
        /// degrees.</summary>
        public double[] Before { get; init; } = Array.Empty<double>();
        public double[] After { get; init; } = Array.Empty<double>();

        public double ReleaseAngle { get; init; }

        /// <summary>The damping the measurement sets, in N·m·s/rad.</summary>
        public double Damping { get; init; }

        /// <summary>The measurement, once the test is done.</summary>
        public WheelCalibration Result { get; init; }

        /// <summary>How far past center a release swung, in degrees: the
        /// most it reached on the side opposite its start.</summary>
        public static double PastCenter(double[] trace)
        {
            if (trace == null || trace.Length == 0) return 0;
            double start = trace[0], most = 0;
            foreach (double angle in trace)
            {
                double past = start > 0 ? -angle : angle;
                if (past > most) most = past;
            }
            return most;
        }

        /// <summary>How far from center a release came to rest, in
        /// degrees: where it stood at its end.</summary>
        public static double RestsAt(double[] trace)
            => trace == null || trace.Length == 0 ? 0 : Math.Abs(trace[^1]);
    }

    /// <summary>
    /// One wheel's centering as it runs (#588): the composer, the wheel's
    /// measurement, a Release Test while one runs, and the last frame for
    /// the card. The force feedback pass steps it on the polling thread.
    /// The card and the Release Test window read it, and start and cancel
    /// a test, from the UI thread, so every member takes the one lock.
    /// </summary>
    public sealed class WheelCenteringRuntime
    {
        /// <summary>Milliseconds without a step after which the card
        /// takes centering to be off and a running test to have
        /// stalled, and after which centering starts afresh when steps
        /// come back.</summary>
        public const int StaleMs = 1000;

        private readonly object _sync = new object();
        private readonly WheelCentering _centering = new WheelCentering();
        private ReleaseTest _test;
        private CenteringFrame _last;
        private long _lastTick = long.MinValue, _lastVehicleTick = long.MinValue, _lastTestTick = long.MinValue;
        private bool _lastSpeedFromGame, _lastGripFromGame;
        private bool _wasVehicle;
        private string _calibrationText;
        private WheelCalibration _calibration;

        /// <summary>A millisecond clock that only goes forward, for
        /// <see cref="Step"/> and the readers alike.</summary>
        public static long NowMs() => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

        /// <summary>One poll of a wheel. <paramref name="axis"/> is the
        /// steering axis from -1 to 1, <paramref name="gameSpeed"/> a
        /// game's speed in m/s or below 0 for none,
        /// <paramref name="gameGrip"/> a game's grip or 0 for none.
        /// Answers what to send the wheel: the test's frame while a
        /// Release Test runs, Vehicle mode's otherwise, and an empty
        /// frame in Spring mode. <paramref name="testing"/> says a test
        /// owns the wheel this poll, which silences every other
        /// force.</summary>
        public CenteringFrame Step(in CenteringSettings settings, string calibrationText, double axis,
            double gameSpeed, double gameGrip, long tickMs, out bool testing)
        {
            lock (_sync)
            {
                var measured = Calibration(calibrationText);
                CenteringFrame frame = default;
                testing = false;
                // A wheel that went unstepped had its force let go, or the engine stopped. When steps come
                // back centering starts afresh, as it does when the mode turns on.
                if (_lastTick != long.MinValue && tickMs - _lastTick > StaleMs) _centering.Reset();
                if (_test != null && _test.Outcome == ReleaseTestOutcome.Running)
                {
                    frame = _test.Step(axis, tickMs);
                    _lastTestTick = tickMs;
                    testing = true;
                    // The test over, the PC's force comes back in softly, as it does when the mode turns on.
                    if (_test.Outcome != ReleaseTestOutcome.Running) _centering.Reset();
                }
                else if (settings.Vehicle)
                {
                    if (!_wasVehicle) _centering.Reset();
                    frame = _centering.Step(settings, measured, axis, settings.SpeedFor(gameSpeed), settings.GripFor(gameGrip), tickMs);
                    _lastVehicleTick = tickMs;
                    _lastSpeedFromGame = settings.SpeedFromGame && gameSpeed >= 0;
                    _lastGripFromGame = settings.GripChoice == 0 && gameGrip > 0;
                }
                _wasVehicle = settings.Vehicle;
                _last = frame;
                _lastTick = tickMs;
                return frame;
            }
        }

        /// <summary>The wheel's measurement, parsed once for a saved text.</summary>
        private WheelCalibration Calibration(string text)
        {
            if (!string.Equals(text, _calibrationText, StringComparison.Ordinal))
            {
                _calibrationText = text;
                _calibration = WheelCalibration.Parse(text);
            }
            return _calibration;
        }

        /// <summary>Forgets the soft start, the guard and the stop, so
        /// centering starts afresh: the wheel connected, or its force
        /// feedback was let go.</summary>
        public void Reset()
        {
            lock (_sync)
            {
                _centering.Reset();
                _wasVehicle = false;
            }
        }

        /// <summary>The last frame, for the card.</summary>
        public CenteringLive Live(long nowMs)
        {
            lock (_sync)
            {
                bool active = _lastVehicleTick != long.MinValue && nowMs - _lastVehicleTick <= StaleMs
                    && _lastVehicleTick == _lastTick;
                return new CenteringLive(active, _last, _lastSpeedFromGame, _lastGripFromGame);
            }
        }

        /// <summary>Whether a wheel is being polled, so a test can run.</summary>
        public bool IsPolled(long nowMs)
        {
            lock (_sync) return _lastTick != long.MinValue && nowMs - _lastTick <= StaleMs;
        }

        /// <summary>Where a Release Test runs its search for the return
        /// ratio: null is the thread pool, and a test of the engine runs
        /// it in place.</summary>
        internal Action<Action> TestSchedule { get; set; }

        /// <summary>Starts a Release Test with the wheel's settings and
        /// its earlier measurement. False while one is already
        /// running.</summary>
        public bool StartTest(in CenteringSettings settings, string calibrationText, long nowMs)
        {
            lock (_sync)
            {
                if (_test != null && _test.Outcome == ReleaseTestOutcome.Running && nowMs - _lastTestTick <= StaleMs)
                    return false;
                _test = new ReleaseTest(settings, WheelCalibration.Parse(calibrationText), TestSchedule);
                _lastTestTick = nowMs;
                return true;
            }
        }

        /// <summary>Stops a running test. On the next poll in Vehicle mode
        /// the wheel's own spring, damper and friction go back at once,
        /// and the PC's force waits for the wheel to come to rest and then
        /// comes in over a second, as after a test that ran to its end:
        /// the test may have left the wheel off center and turning.</summary>
        public void CancelTest()
        {
            lock (_sync) StopTest();
        }

        private void StopTest()
        {
            if (_test == null || _test.Outcome != ReleaseTestOutcome.Running) return;
            _test.Cancel();
            _centering.Reset();
        }

        /// <summary>Lets go of a finished or canceled test, so the window
        /// can close on it.</summary>
        public void ClearTest()
        {
            lock (_sync)
            {
                if (_test == null) return;
                StopTest();
                _test = null;
            }
        }

        /// <summary>The running or last test for the window, or null
        /// where none was started.</summary>
        public ReleaseTestStatus Test(long nowMs)
        {
            lock (_sync)
            {
                if (_test == null) return null;
                bool running = _test.Outcome == ReleaseTestOutcome.Running;
                return new ReleaseTestStatus
                {
                    Outcome = _test.Outcome,
                    Progress = _test.Progress,
                    Stalled = running && nowMs - _lastTestTick > StaleMs,
                    Before = Copy(_test.Before),
                    After = Copy(_test.After),
                    ReleaseAngle = _test.ReleaseAngle,
                    Damping = _test.Damping,
                    Result = _test.Result,
                };
            }
        }

        private static double[] Copy(System.Collections.Generic.IReadOnlyList<double> list)
        {
            if (list.Count == 0) return Array.Empty<double>();
            var copy = new double[list.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = list[i];
            return copy;
        }
    }
}
