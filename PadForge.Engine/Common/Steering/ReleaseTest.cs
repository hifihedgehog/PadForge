using System;
using System.Collections.Generic;

namespace PadForge.Engine.Steering
{
    /// <summary>What a Release Test ended with.</summary>
    public enum ReleaseTestOutcome
    {
        Running,
        Done,
        Canceled,

        /// <summary>The wheel would not come to rest before a step.</summary>
        Moving,

        /// <summary>No force up to the test's limit moved the wheel.</summary>
        NoMotion,

        /// <summary>The wheel came close to the end of its range.</summary>
        NearLock,

        /// <summary>The numbers measured are not a wheel's.</summary>
        Unreadable,
    }

    /// <summary>
    /// The Release Test (#588): with hands off the wheel it measures what
    /// no driver states and the centering math needs, then lets the
    /// wheel go from an angle twice, as it centered before and as it
    /// centers with the measurement, so the two returns can be seen
    /// side by side. The idea is ISO 17288-1's, the steering-release
    /// test: turn, let go, record the return.
    ///
    /// <para>The steps, each on a small torque: a slow ramp until the
    /// wheel moves gives its friction and which way a force pushes it.
    /// A short step and a coast give its inertia and the loop's delay:
    /// the step's acceleration is the torque less friction over the
    /// inertia, the coast's deceleration is friction over the inertia.
    /// The same step with the wheel's damper on, at each of two
    /// coefficients, shows how much the damper gives at each. A rising
    /// push against the wheel's spring, at each of three coefficients,
    /// shows how stiff the spring is at each.</para>
    ///
    /// <para>The test never waits on friction to stop the wheel: a base
    /// with little of it would coast to its lock. It brakes with the
    /// motor and then waits for rest.</para>
    ///
    /// <para>One step runs per poll: <see cref="Step"/> takes the axis
    /// and a millisecond clock and answers the frame to send.</para>
    /// </summary>
    public sealed class ReleaseTest
    {
        /// <summary>The most of the wheel's torque any step commands,
        /// as a share.</summary>
        public const double MostShare = 0.45;

        /// <summary>What the step adds to the torque that just moved
        /// the wheel, in N·m, so every wheel accelerates at a pace of
        /// the same order whatever its motor.</summary>
        public const double StepTorque = 0.30;

        /// <summary>The most the step adds as a share of the wheel's
        /// torque, which is what a wheel under 2.5 N·m gets.</summary>
        public const double StepShare = 0.12;

        /// <summary>The most the ramp reaches, in N·m, before the test
        /// takes the wheel to be held.</summary>
        public const double MostRampTorque = 1.5;

        /// <summary>The most the push against the spring reaches, in
        /// N·m.</summary>
        public const double MostSpringTorque = 3.0;

        /// <summary>Seconds the push against the spring takes to reach
        /// its most.</summary>
        public const double SpringRampSeconds = 1.5;

        /// <summary>Radians a second past which a wheel pushed against
        /// its spring is taken to have none.</summary>
        public const double SpringRunaway = 3.0;

        /// <summary>The share of half the range past which the test
        /// stops.</summary>
        public const double LockShare = 0.92;

        /// <summary>Radians a second at and above which the brake is at
        /// its full level.</summary>
        public const double FullBrakeSpeed = 1.5;

        /// <summary>Degrees the step, the coast and the push against
        /// the spring may each turn the wheel, on a wheel of 270 degrees
        /// or more. A wheel with less range turns less in proportion.</summary>
        public const double StepDegrees = 15.0, CoastDegrees = 40.0, SpringDegrees = 25.0;

        /// <summary>Degrees of motion that count as the wheel moving.</summary>
        public const double MovedDegrees = 0.5;

        /// <summary>The counts of the axis the step has to cross for
        /// its delay to be taken as read. Fifteen degrees are 273
        /// counts of a fourteen-bit axis on a 900 degree wheel and 68
        /// of a twelve-bit one.</summary>
        public const double CoarseCounts = 150.0;

        /// <summary>Milliseconds a release is recorded for.</summary>
        public const int ReleaseMs = 2500;

        /// <summary>The swing past center, in degrees, that reads as
        /// none: a wheel that crosses center by less returns without
        /// overshoot, which is what the test's damping aims for. In
        /// whole degrees, as the window states it, it is 0.</summary>
        public const double NoOvershootDegrees = 0.5;

        /// <summary>Where no damping the wheel has stops the swing, the
        /// damping chosen is the least that comes within this many
        /// degrees of the best there is, so the last part of a degree is
        /// not bought with the most damping the wheel can give.</summary>
        public const double NearBestDegrees = 1.0;

        /// <summary>The return ratios tried, least first, in tenths.</summary>
        private const int RatioSteps = 15;

        private enum Phase
        {
            Settle, Ramp, RampStop, Step, Coast, CoastStop, DamperStep, DamperStop, SpringSettle, SpringRamp, SpringStop,
            Search, PushBefore, RestBefore, ReleaseBefore, PushAfter, RestAfter, ReleaseAfter, Finished,
        }

        /// <summary>The stages the window's progress counts: the damper
        /// runs twice and the spring three times.</summary>
        private const int Stages = 24;

        private const double Deg = Math.PI / 180.0;

        private readonly CenteringSettings _settings;
        private readonly WheelCalibration _previous;
        private readonly double _torque, _halfRange, _speed, _grip, _increment, _springCap, _span;
        private readonly WheelCentering _centering = new WheelCentering();

        private Phase _phase = Phase.Settle;
        private int _stage;
        private long _phaseStart = long.MinValue;
        private double _phaseAngle;
        private double _quietAngle;
        private long _quietSince;

        // The least step the axis has shown between two polls, in radians: its grain, a count of the
        // axis once the wheel has moved slowly or flickered at rest. 0 until two readings differ.
        private double _grain, _grainAngle = double.NaN;

        // Speed in rad/s, signed as the axis, from the angle ten milliseconds apart.
        private double _velocity, _velocityAngle;
        private long _velocityTick = long.MinValue;

        private readonly List<(long tick, double angle)> _samples = new List<(long, double)>(4096);
        private readonly List<double> _pushes = new List<double>(4096);
        private double _rampLevel, _stepLevel, _pushSign;
        private int _forceSign = 1;
        private double _friction, _inertia, _delay, _stepAcceleration;
        private readonly double[] _damper = new double[WheelCalibration.DamperCoefficients.Length];
        private readonly double[] _spring = new double[WheelCalibration.SpringCoefficients.Length];
        private int _damperIndex, _springIndex;
        private long _stepEnd;
        private bool _braking;
        private int _ratioStep;
        private double _bestRatio, _bestPast;
        private readonly double[] _pasts = new double[RatioSteps + 1];
        private WheelCalibration _result;

        private readonly List<double> _before = new List<double>(ReleaseMs);
        private readonly List<double> _after = new List<double>(ReleaseMs);

        public ReleaseTestOutcome Outcome { get; private set; } = ReleaseTestOutcome.Running;

        /// <summary>The measurement, once the test is done.</summary>
        public WheelCalibration Result => Outcome == ReleaseTestOutcome.Done ? _result : null;

        /// <summary>The two releases, one angle a millisecond in degrees:
        /// as the wheel centered before the test, and with the
        /// measurement.</summary>
        public IReadOnlyList<double> Before => _before;
        public IReadOnlyList<double> After => _after;

        /// <summary>The angle the releases start from, in degrees.</summary>
        public double ReleaseAngle { get; }

        /// <summary>The damping the second release ran with, in
        /// N·m·s/rad: what the measurement sets. 0 until that release
        /// begins.</summary>
        public double Damping { get; private set; }

        /// <summary>Progress from 0 to 1 for the window.</summary>
        public double Progress { get; private set; }

        /// <param name="settings">The wheel's centering settings, with
        /// its torque and range. The releases run them in Vehicle mode
        /// at the fixed speed.</param>
        /// <param name="previous">The wheel's measurement from an
        /// earlier test, or null. The first release centers with it,
        /// as the wheel centered before this test.</param>
        public ReleaseTest(in CenteringSettings settings, WheelCalibration previous = null)
        {
            _settings = new CenteringSettings(true, settings.Car, settings.WeightPercent, settings.OnCenterPercent,
                settings.AssistPercent, settings.DampingPercent, settings.Friction, false, settings.FixedSpeed,
                settings.GripChoice, settings.WheelTorque, settings.Range, settings.Effects, settings.DamperSteps);
            _previous = previous;
            _torque = settings.WheelTorque;
            _halfRange = settings.Range * Math.PI / 360.0;
            _speed = settings.FixedSpeed;
            _grip = _settings.GripFor(0);
            _increment = Math.Min(StepShare, StepTorque / _torque);
            _springCap = Math.Min(MostShare, MostSpringTorque / _torque);
            _span = Math.Min(1.0, _halfRange / (135.0 * Deg)) * Deg;
            ReleaseAngle = ReleaseAngleFor(settings);
        }

        /// <summary>The angle a test lets a wheel go from, in degrees: 90,
        /// or six tenths of the way to the lock on a wheel that turns less
        /// than 300 degrees. The window names it before the test starts.</summary>
        public static double ReleaseAngleFor(in CenteringSettings settings) => Math.Min(90.0, 0.6 * settings.Range / 2.0);

        /// <summary>Stops the test. The next step answers an empty frame.</summary>
        public void Cancel()
        {
            if (Outcome == ReleaseTestOutcome.Running) End(ReleaseTestOutcome.Canceled);
        }

        private void End(ReleaseTestOutcome outcome)
        {
            Outcome = outcome;
            _phase = Phase.Finished;
            Progress = 1.0;
        }

        private void Enter(Phase phase, long tick, double angle)
        {
            if (_phaseStart != long.MinValue) _stage++;
            _phase = phase;
            _phaseStart = tick;
            _phaseAngle = angle;
            _quietAngle = angle;
            _quietSince = tick;
            _braking = false;
            _samples.Clear();
            _pushes.Clear();
            Progress = Math.Min(1.0, _stage / (double)Stages);
        }

        /// <summary>An angle no smaller than a number of the axis's own
        /// counts. A wheel at rest on the edge of a count flickers by
        /// one, and on an axis of twelve bits or fewer one count is
        /// more than the fifth of a degree that reads as rest.</summary>
        private double AtLeast(double radians, double counts) => Math.Max(radians, counts * _grain);

        /// <summary>Whether the wheel has stayed within a fifth of a
        /// degree, or a count and a half of a coarser axis, for this
        /// many milliseconds.</summary>
        private bool Quiet(double angle, long tick, int ms)
        {
            if (Math.Abs(angle - _quietAngle) > AtLeast(0.2 * Deg, 1.6))
            {
                _quietAngle = angle;
                _quietSince = tick;
            }
            return tick - _quietSince >= ms;
        }

        /// <summary>Brings a moving wheel to rest: the motor pushes
        /// against the motion in step with the wheel's speed, as a
        /// damper would, up to the brake's level at
        /// <see cref="FullBrakeSpeed"/>. A push of one size whatever
        /// the speed would carry a light wheel through rest and back
        /// again. Once the inertia is known the damping is also held
        /// to what a late force may give (WheelCentering's
        /// PcDampingShare), the lateness here being the loop's delay
        /// and the ten milliseconds the speed is read over. True once
        /// the wheel has been still for this many milliseconds.</summary>
        private bool Still(ref CenteringFrame frame, double angle, long tick, int ms)
        {
            double damping = BrakeLevel * _torque / FullBrakeSpeed;
            if (_inertia > 0)
                damping = Math.Min(damping, WheelCentering.PcDampingShare * _inertia / (_delay + 0.010));
            double brake = Math.Clamp(damping * _velocity / _torque, -BrakeLevel, BrakeLevel);
            // A positive level pushes toward lower axis values, against a wheel moving toward higher ones.
            frame.Level = Level(brake);
            return Quiet(angle, tick, ms);
        }

        /// <summary>The level the test brakes and pushes with: the step's,
        /// or before the step is known, a little over the ramp's.</summary>
        private double BrakeLevel => _stepLevel > 0 ? _stepLevel : _rampLevel + _increment;

        private short Level(double share) => (short)Math.Clamp(Math.Round(share * 32767.0), -32767, 32767);

        /// <summary>The level's sign that pushes a wheel at this angle
        /// toward center: the angle's own.</summary>
        private static double TowardCenter(double angle) => angle >= 0 ? 1 : -1;

        /// <summary>One poll: the axis from -1 to 1 and a millisecond
        /// clock. Answers what to send the wheel.</summary>
        public CenteringFrame Step(double axis, long tick)
        {
            var frame = new CenteringFrame();
            if (Outcome != ReleaseTestOutcome.Running) return frame;
            double angle = Math.Clamp(axis, -1.0, 1.0) * _halfRange;
            if (_phaseStart == long.MinValue) Enter(Phase.Settle, tick, angle);
            if (!double.IsNaN(_grainAngle))
            {
                double step = Math.Abs(angle - _grainAngle);
                if (step > 1e-9 && (_grain == 0 || step < _grain)) _grain = step;
            }
            _grainAngle = angle;
            if (_velocityTick == long.MinValue)
            {
                _velocityTick = tick;
                _velocityAngle = angle;
            }
            else if (tick - _velocityTick >= 10)
            {
                _velocity = (angle - _velocityAngle) / ((tick - _velocityTick) / 1000.0);
                _velocityTick = tick;
                _velocityAngle = angle;
            }
            if (Math.Abs(angle) > LockShare * _halfRange)
            {
                End(ReleaseTestOutcome.NearLock);
                return frame;
            }
            long elapsed = tick - _phaseStart;
            frame.AngleDegrees = angle / Deg;

            switch (_phase)
            {
                case Phase.Settle:
                    if (Quiet(angle, tick, 400))
                    {
                        // Push toward the side with more room: away from the nearer end of the range.
                        _pushSign = angle > 0 ? 1 : -1;
                        Enter(Phase.Ramp, tick, angle);
                    }
                    else if (elapsed > 3000) End(ReleaseTestOutcome.Moving);
                    break;

                case Phase.Ramp:
                    // A slow ramp, a quarter of a newton meter a second at most, until the wheel moves.
                    _rampLevel = elapsed / 1000.0 * Math.Min(0.10, 0.25 / _torque);
                    if (Math.Abs(angle - _phaseAngle) >= AtLeast(MovedDegrees * Deg, 2.5))
                    {
                        _friction = _rampLevel * _torque;
                        // A positive level pushes toward lower axis values on a wheel that answers the usual way.
                        bool lower = angle < _phaseAngle;
                        _forceSign = lower == (_pushSign > 0) ? 1 : -1;
                        Enter(Phase.RampStop, tick, angle);
                    }
                    else if (_rampLevel >= Math.Min(MostShare - _increment, MostRampTorque / _torque)) End(ReleaseTestOutcome.NoMotion);
                    else frame.Level = Level(_rampLevel * _pushSign);
                    break;

                case Phase.RampStop:
                    if (Still(ref frame, angle, tick, 300))
                    {
                        _stepLevel = Math.Min(MostShare, _rampLevel + _increment);
                        Enter(Phase.Step, tick, angle);
                    }
                    else if (elapsed > 3000) End(ReleaseTestOutcome.Moving);
                    break;

                case Phase.Step:
                    // From here every push points at center, whichever way that is.
                    Sample(tick, angle, 0);
                    if (elapsed >= 250 || Math.Abs(angle - _phaseAngle) >= StepDegrees * _span)
                    {
                        if (!ReadStep()) { End(ReleaseTestOutcome.Unreadable); break; }
                        _stepEnd = tick;
                        Enter(Phase.Coast, tick, angle);
                    }
                    else frame.Level = Level(_stepLevel * TowardCenter(_phaseAngle));
                    break;

                case Phase.Coast:
                    // A fifth of a second with no force is the coast, less where the wheel stops by itself or
                    // has turned as far as a coast may.
                    Sample(tick, angle, 0);
                    if (elapsed >= 200 + (long)(_delay * 1000.0) || Math.Abs(angle - _phaseAngle) >= CoastDegrees * _span
                        || Quiet(angle, tick, 150))
                    {
                        if (!ReadCoast()) { End(ReleaseTestOutcome.Unreadable); break; }
                        Enter(Phase.CoastStop, tick, angle);
                    }
                    break;

                case Phase.CoastStop:
                    if (Still(ref frame, angle, tick, 300) || elapsed > 3000)
                    {
                        _damperIndex = 0;
                        Enter(Phase.DamperStep, tick, angle);
                    }
                    break;

                case Phase.DamperStep:
                    Sample(tick, angle, 0);
                    frame.DamperCoefficient = WheelCalibration.DamperCoefficients[_damperIndex];
                    frame.DamperSaturation = 10000;
                    if (elapsed >= 300 || Math.Abs(angle - _phaseAngle) >= StepDegrees * _span)
                    {
                        _damper[_damperIndex] = ReadDamper();
                        Enter(Phase.DamperStop, tick, angle);
                    }
                    else frame.Level = Level(_stepLevel * TowardCenter(_phaseAngle));
                    break;

                case Phase.DamperStop:
                    if (Still(ref frame, angle, tick, 300) || elapsed > 3000)
                    {
                        if (++_damperIndex < _damper.Length) Enter(Phase.DamperStep, tick, angle);
                        else
                        {
                            // The spring's strongest coefficient first. One that shows no spring ends the
                            // spring's part, since a weaker coefficient shows less.
                            _springIndex = _spring.Length - 1;
                            Enter(Phase.SpringSettle, tick, angle);
                        }
                    }
                    break;

                case Phase.SpringSettle:
                    // The spring at the coefficient under test brings the wheel to where it and friction leave it.
                    SpringOn(ref frame);
                    if (Still(ref frame, angle, tick, 250) || elapsed > 3000) Enter(Phase.SpringRamp, tick, angle);
                    break;

                case Phase.SpringRamp:
                {
                    // A rising push away from center against the spring. The wheel's motion under it gives the
                    // spring's stiffness whatever the friction is (ReadSpring).
                    SpringOn(ref frame);
                    double side = _phaseAngle >= 0 ? 1 : -1;
                    double push = Math.Min(_springCap, elapsed / 1000.0 * _springCap / SpringRampSeconds);
                    frame.Level = Level(-push * side);
                    Sample(tick, angle * side, push * _torque);
                    double moved = (angle - _phaseAngle) * side;
                    bool away = Math.Abs(_velocity) > SpringRunaway || Math.Abs(angle) > 0.5 * _halfRange;
                    if (away || moved >= SpringDegrees * _span || elapsed >= SpringRampSeconds * 1000.0 + 200)
                    {
                        _spring[_springIndex] = away ? 0 : ReadSpring();
                        if (away || _springIndex == 0)
                        {
                            // Skipped coefficients are stages the progress still counts.
                            _stage += 2 * _springIndex;
                            Enter(Phase.SpringStop, tick, angle);
                        }
                        else
                        {
                            _springIndex--;
                            Enter(Phase.SpringSettle, tick, angle);
                        }
                    }
                    break;
                }

                case Phase.SpringStop:
                    if (Still(ref frame, angle, tick, 300) || elapsed > 3000)
                    {
                        _ratioStep = 0;
                        _bestRatio = 0;
                        _bestPast = double.MaxValue;
                        Enter(Phase.Search, tick, angle);
                    }
                    break;

                case Phase.Search:
                    // One return ratio a poll, so no poll waits on the whole search.
                    if (SearchStep()) Enter(Phase.PushBefore, tick, angle);
                    break;

                case Phase.PushBefore:
                case Phase.PushAfter:
                    // Turn out to the release angle on the side the wheel is on, and brake so it stops there.
                    if (MoveOut(ref frame, angle) || elapsed > 4000)
                        Enter(_phase == Phase.PushBefore ? Phase.RestBefore : Phase.RestAfter, tick, angle);
                    break;

                case Phase.RestBefore:
                case Phase.RestAfter:
                    if (Still(ref frame, angle, tick, 250) || elapsed > 2500)
                    {
                        _centering.ResetAtFull();
                        Enter(_phase == Phase.RestBefore ? Phase.ReleaseBefore : Phase.ReleaseAfter, tick, angle);
                    }
                    break;

                case Phase.ReleaseBefore:
                case Phase.ReleaseAfter:
                {
                    bool before = _phase == Phase.ReleaseBefore;
                    frame = _centering.Step(_settings, before ? BeforeMeasurement() : _result, axis, _speed, _grip, tick);
                    if (!before) Damping = frame.Damping;
                    // One angle a millisecond, whatever the polling rate.
                    var trace = before ? _before : _after;
                    while (trace.Count < ReleaseMs && trace.Count <= elapsed) trace.Add(angle / Deg);
                    Progress = Math.Min(1.0, (_stage + Math.Min(1.0, elapsed / (double)ReleaseMs)) / Stages);
                    if (elapsed >= ReleaseMs)
                    {
                        if (before) Enter(Phase.PushAfter, tick, angle);
                        else End(ReleaseTestOutcome.Done);
                    }
                    // The centering carries the wheel's sign itself.
                    return frame;
                }
            }

            // The wheel's own sign, for every level the test itself commands.
            frame.Level = (short)(frame.Level * _forceSign);
            return frame;
        }

        /// <summary>Keeps one sample a millisecond: the angle and the
        /// push commanded with it, in N·m.</summary>
        private void Sample(long tick, double angle, double push)
        {
            if (_samples.Count > 0 && _samples[^1].tick >= tick) return;
            _samples.Add((tick, angle));
            _pushes.Add(push);
        }

        /// <summary>The spring at the coefficient under test, with the
        /// damper at the lower of its two to steady the wheel.</summary>
        private void SpringOn(ref CenteringFrame frame)
        {
            frame.SpringCoefficient = WheelCalibration.SpringCoefficients[_springIndex];
            frame.SpringSaturation = 10000;
            frame.DamperCoefficient = WheelCalibration.DamperCoefficients[0];
            frame.DamperSaturation = 10000;
        }

        /// <summary>Turns the wheel out to the release angle and stops
        /// it there: the step's level outward, then the same level
        /// inward once the distance left is what braking takes, which
        /// is the speed squared over twice the braking deceleration,
        /// and what the wheel covers in the loop's delay. True once the
        /// wheel has stopped or turned back.</summary>
        private bool MoveOut(ref CenteringFrame frame, double angle)
        {
            double side = angle >= 0 ? 1 : -1;
            double outward = _velocity * side;
            double left = ReleaseAngle * Deg - Math.Abs(angle);
            double braking = (_stepLevel * _torque + _friction) / _inertia;
            if (!_braking && (left <= 0 || outward * outward / (2.0 * braking) + outward * _delay >= left))
                _braking = true;
            if (_braking)
            {
                if (outward <= 0.05) return true;
                frame.Level = Level(_stepLevel * side);
                return false;
            }
            frame.Level = Level(-_stepLevel * side);
            return false;
        }

        /// <summary>What the first release centers with: the wheel's
        /// earlier measurement where it has one that pushes the way
        /// this test found, or nothing but the sign, so the wheel
        /// centers the way it did before the test, pushed the right
        /// way.</summary>
        private WheelCalibration BeforeMeasurement()
        {
            if (_previous != null && _previous.ForceSign == _forceSign) return _previous;
            return _forceSign > 0 ? null : new WheelCalibration
            {
                Inertia = WheelCentering.UnmeasuredInertia, Delay = WheelCentering.UnmeasuredDelay, WheelTorque = _torque, ForceSign = -1,
                DamperMid = _torque / 10.0 * WheelCalibration.DamperCoefficients[0] / 10000.0, DamperFull = _torque / 10.0,
            };
        }

        /// <summary>The step: from rest the angle runs as half the
        /// acceleration times the square of the time since the force
        /// arrived, a parabola in time. The parabola that fits the
        /// angles best, by least squares, has the acceleration in its
        /// curvature and the moment the force arrived at its lowest
        /// point. Only the angles past the first thirtieth of the
        /// travel are fitted, and the parabola may stand a little off
        /// the angle the step started from: an axis of few bits reads
        /// in steps, the wheel may start anywhere inside one, and a
        /// fit held to the starting reading took the first step up
        /// for the start of the motion, which read a ten-bit wheel's
        /// delay as a millisecond where it was eight.</summary>
        private bool ReadStep()
        {
            if (_samples.Count < 8) return false;
            double travel = _samples[^1].angle - _phaseAngle;
            double direction = Math.Sign(travel);
            if (direction == 0) return false;
            double least = Math.Abs(travel) / 30.0;
            double n = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, y0 = 0, y1 = 0, y2 = 0;
            bool moving = false;
            foreach (var (tick, angle) in _samples)
            {
                double moved = direction * (angle - _phaseAngle);
                if (!moving && moved < least) continue;
                moving = true;
                double x = (tick - _phaseStart) / 1000.0, x2 = x * x;
                n++; s1 += x; s2 += x2; s3 += x2 * x; s4 += x2 * x2;
                y0 += moved; y1 += x * moved; y2 += x2 * moved;
            }
            if (n < 8) return false;
            // Least squares parabola y = c0 + c1 x + c2 x^2, by Cramer's rule.
            double det = n * (s2 * s4 - s3 * s3) - s1 * (s1 * s4 - s3 * s2) + s2 * (s1 * s3 - s2 * s2);
            if (Math.Abs(det) < 1e-18) return false;
            double c1 = (n * (y1 * s4 - s3 * y2) - y0 * (s1 * s4 - s3 * s2) + s2 * (s1 * y2 - y1 * s2)) / det;
            double c2 = (n * (s2 * y2 - y1 * s3) - s1 * (s1 * y2 - y1 * s2) + y0 * (s1 * s3 - s2 * s2)) / det;
            if (c2 <= 0) return false;
            _stepAcceleration = 2.0 * c2;
            _delay = Math.Clamp(-c1 / (2.0 * c2), 0.001, 0.040);
            // The lowest point of a parabola drawn through few counts is known to a few milliseconds at
            // best, and a delay read short lets the wheel swing. So an axis that showed fewer than
            // CoarseCounts counts over the step keeps no delay under the one taken for an unmeasured wheel.
            if (_grain > 0 && Math.Abs(travel) < CoarseCounts * _grain)
                _delay = Math.Max(_delay, WheelCentering.UnmeasuredDelay);
            return true;
        }

        /// <summary>The coast: with the force gone, friction alone slows
        /// the wheel, so the angle runs as a parabola. Its curvature is
        /// the deceleration. With the step's acceleration that gives the
        /// inertia and the friction.</summary>
        private bool ReadCoast()
        {
            // From when the force is gone, the delay after the step's end, to the last sample still moving.
            long from = _stepEnd + (long)Math.Ceiling(_delay * 1000.0) + 2;
            double last = _samples.Count > 0 ? _samples[^1].angle : 0;
            bool stopped = Math.Abs(_velocity) < 0.3;
            double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, t0 = 0, t1 = 0, t2 = 0;
            foreach (var (tick, angle) in _samples)
            {
                if (tick < from) continue;
                // A wheel that stopped inside the window is at rest for the rest of it, which is no parabola.
                if (stopped && Math.Abs(angle - last) < AtLeast(0.05 * Deg, 1.1)) break;
                double x = (tick - from) / 1000.0, x2 = x * x;
                s0++; s1 += x; s2 += x2; s3 += x2 * x; s4 += x2 * x2;
                t0 += angle; t1 += x * angle; t2 += x2 * angle;
            }
            double stepTorque = _stepLevel * _torque;
            double deceleration = 0;
            if (s0 >= 8)
            {
                // Least squares parabola y = c0 + c1 x + c2 x^2. The deceleration is twice c2, against the motion.
                double det = s0 * (s2 * s4 - s3 * s3) - s1 * (s1 * s4 - s3 * s2) + s2 * (s1 * s3 - s2 * s2);
                if (Math.Abs(det) > 1e-24)
                {
                    double c2 = (s0 * (s2 * t2 - s3 * t1) - s1 * (s1 * t2 - s3 * t0) + s2 * (s1 * t1 - s2 * t0)) / det;
                    deceleration = Math.Abs(2.0 * c2);
                }
            }
            // A wheel that stopped at once has more friction than the coast could show: the ramp's figure stands.
            _inertia = deceleration > 0 ? stepTorque / (_stepAcceleration + deceleration) : (stepTorque - _friction) / _stepAcceleration;
            if (deceleration > 0) _friction = _inertia * deceleration;
            return _inertia >= 0.0005 && _inertia <= 0.5 && _friction >= 0 && _friction < stepTorque;
        }

        /// <summary>The step again with the damper on: the wheel covers
        /// less ground in the same time. With the damping over the
        /// inertia as k and the free acceleration as A, the distance is
        /// A / k times (t - (1 - e^(-k t)) / k), solved for k. Answers
        /// the damping at the coefficient under test in N·m·s/rad, or 0
        /// where the damper showed nothing.</summary>
        private double ReadDamper()
        {
            if (_samples.Count < 8) return 0;
            double t = (_samples[^1].tick - _phaseStart) / 1000.0 - _delay;
            double moved = Math.Abs(_samples[^1].angle - _phaseAngle);
            double free = (_stepLevel * _torque - _friction) / _inertia;
            if (t <= 0.02 || free <= 0) return 0;
            double without = 0.5 * free * t * t;
            // Within a twentieth of the free distance the damper showed nothing.
            if (moved >= 0.95 * without) return 0;
            double lo = 1e-3, hi = 5000;
            for (int i = 0; i < 60; i++)
            {
                double k = Math.Sqrt(lo * hi);
                double distance = free / k * (t - (1 - Math.Exp(-k * t)) / k);
                if (distance > moved) lo = k; else hi = k;
            }
            return Math.Sqrt(lo * hi) * _inertia;
        }

        /// <summary>The rising push against the spring. While the wheel
        /// moves outward, its inertia times its acceleration is the
        /// push less the spring's pull, the damper's and friction.
        /// Summed twice over time from the first motion, that reads
        /// with no slope of a noisy signal in it: the push summed twice,
        /// less the inertia times the distance moved, is the stiffness
        /// times the distance summed twice, plus a constant times half
        /// the time squared, plus the damping times the distance summed
        /// once, less the inertia times the speed the wheel already had
        /// times the time. Least squares gives the four, and the first
        /// is the spring. Friction and the spring's pull at the start
        /// are the constant, so neither needs to be known. Answers N·m
        /// per unit of deflection, or 0 where the wheel showed no
        /// spring.</summary>
        private double ReadSpring()
        {
            int n = _samples.Count;
            double start = n > 0 ? _samples[0].angle : 0;
            int first = -1;
            for (int i = 0; i < n; i++)
                if (_samples[i].angle - start >= AtLeast(0.1 * Deg, 1.6)) { first = i; break; }
            if (first < 0 || n - first < 50) return 0;
            double moved = _samples[n - 1].angle - _samples[first].angle;
            // Under a degree of travel is a spring too stiff for this push to read.
            if (moved < 1.0 * Deg) return 0;

            long lag = (long)Math.Round(_delay * 1000.0);
            double PushAt(ref int at, long tick)
            {
                // The push that had reached the motor at this tick: the one commanded a delay earlier.
                while (at + 1 < n && _samples[at + 1].tick <= tick - lag) at++;
                return _samples[at].tick <= tick - lag ? _pushes[at] : 0;
            }

            const int Unknowns = 4, Stiffness = 0, Damping = 2;
            var matrix = new double[Unknowns, Unknowns];
            var right = new double[Unknowns];
            var row = new double[Unknowns];
            double theta0 = _samples[first].angle;
            long tick0 = _samples[first].tick;
            int cursor = 0;
            double pushBefore = PushAt(ref cursor, tick0), pushStart = pushBefore, pushEnd = pushBefore;
            double sumTheta = 0, sumTheta2 = 0, sumPush = 0, sumPush2 = 0;
            for (int i = first + 1; i < n; i++)
            {
                double dt = (_samples[i].tick - _samples[i - 1].tick) / 1000.0;
                double theta = _samples[i].angle - theta0, thetaBefore = _samples[i - 1].angle - theta0;
                double push = PushAt(ref cursor, _samples[i].tick);
                double nextTheta = sumTheta + 0.5 * (theta + thetaBefore) * dt;
                sumTheta2 += 0.5 * (sumTheta + nextTheta) * dt;
                sumTheta = nextTheta;
                double nextPush = sumPush + 0.5 * (push + pushBefore) * dt;
                sumPush2 += 0.5 * (sumPush + nextPush) * dt;
                sumPush = nextPush;
                pushBefore = push;
                pushEnd = push;

                double t = (_samples[i].tick - tick0) / 1000.0;
                row[Stiffness] = sumTheta2;
                row[1] = 0.5 * t * t;
                row[Damping] = sumTheta;
                row[3] = -t;
                double y = sumPush2 - _inertia * theta;
                for (int a = 0; a < Unknowns; a++)
                {
                    right[a] += row[a] * y;
                    for (int b = 0; b < Unknowns; b++) matrix[a, b] += row[a] * row[b];
                }
            }

            // Each column to unit length, so the four are of a size.
            var scale = new double[Unknowns];
            for (int a = 0; a < Unknowns; a++)
            {
                scale[a] = Math.Sqrt(matrix[a, a]);
                if (scale[a] <= 0) return 0;
            }
            for (int a = 0; a < Unknowns; a++)
            {
                right[a] /= scale[a];
                for (int b = 0; b < Unknowns; b++) matrix[a, b] /= scale[a] * scale[b];
            }
            // At a steady pace the damper's column and the constant's run alike, so the damping leans on what
            // the damper's own step measured, a twentieth as heavily as on this push.
            const double Lean = 0.05;
            matrix[Damping, Damping] += Lean;
            right[Damping] += Lean * _damper[0] * scale[Damping];
            if (!Solve(matrix, right)) return 0;
            double stiffness = right[Stiffness] / scale[Stiffness];
            // A spring carries most of what the push rose by. A wheel with none moved freely.
            if (stiffness <= 0 || stiffness * moved < 0.25 * (pushEnd - pushStart)) return 0;
            return stiffness * _halfRange;
        }

        /// <summary>Solves a small set of linear equations in place by
        /// elimination, the largest pivot first. The answer replaces
        /// the right side. False where the equations do not settle
        /// every unknown.</summary>
        private static bool Solve(double[,] matrix, double[] right)
        {
            int n = right.Length;
            for (int column = 0; column < n; column++)
            {
                int pivot = column;
                for (int r = column + 1; r < n; r++)
                    if (Math.Abs(matrix[r, column]) > Math.Abs(matrix[pivot, column])) pivot = r;
                if (Math.Abs(matrix[pivot, column]) < 1e-12) return false;
                if (pivot != column)
                {
                    for (int c = 0; c < n; c++) (matrix[column, c], matrix[pivot, c]) = (matrix[pivot, c], matrix[column, c]);
                    (right[column], right[pivot]) = (right[pivot], right[column]);
                }
                for (int r = column + 1; r < n; r++)
                {
                    double factor = matrix[r, column] / matrix[column, column];
                    if (factor == 0) continue;
                    for (int c = column; c < n; c++) matrix[r, c] -= factor * matrix[column, c];
                    right[r] -= factor * right[column];
                }
            }
            for (int r = n - 1; r >= 0; r--)
            {
                double sum = right[r];
                for (int c = r + 1; c < n; c++) sum -= matrix[r, c] * right[c];
                right[r] = sum / matrix[r, r];
            }
            return true;
        }

        /// <summary>Tries one return ratio against a simulation of the
        /// measured wheel: the least damping, of the wheel's inertia
        /// against the curve's stiffness at center, that returns it
        /// from the release angle without overshoot. A wheel that swings
        /// past center at every ratio takes the least ratio that comes
        /// near the best of them. True once the ratio is chosen.</summary>
        private bool SearchStep()
        {
            double ratio = _ratioStep * 0.1;
            double past = SimulatedOvershoot(ratio);
            _pasts[_ratioStep] = past;
            if (past < _bestPast) _bestPast = past;
            // The ratios run from the least, so the first that stops the swing is the least that does.
            if (past <= NoOvershootDegrees) _bestRatio = ratio;
            else
            {
                if (++_ratioStep <= RatioSteps) return false;
                for (int i = 0; i <= RatioSteps; i++)
                {
                    if (_pasts[i] > _bestPast + NearBestDegrees) continue;
                    _bestRatio = i * 0.1;
                    break;
                }
            }
            _result = Measurement(_bestRatio);
            return true;
        }

        private WheelCalibration Measurement(double ratio) => new WheelCalibration
        {
            Inertia = _inertia, Friction = _friction, Delay = _delay,
            DamperMid = _damper[0], DamperFull = _damper[1],
            SpringThird = _spring[0], SpringTwoThirds = _spring[1], SpringFull = _spring[2],
            ForceSign = _forceSign, ReturnRatio = ratio, WheelTorque = _torque,
        };

        /// <summary>The swing past center, in degrees, of the measured
        /// wheel let go from the release angle with this return
        /// ratio.</summary>
        private double SimulatedOvershoot(double ratio)
        {
            var measured = Measurement(ratio);
            var plant = new WheelPlant
            {
                Inertia = _inertia, Friction = _friction, Delay = _delay, WheelTorque = _torque,
                DamperStrength = measured.DampingAt, SpringStrength = measured.SpringStiffnessAt,
                HalfRange = _halfRange, ForceSign = _forceSign,
            };
            plant.Angle = ReleaseAngle * Deg;
            var centering = new WheelCentering();
            centering.ResetAtFull();
            var trace = plant.Run(centering, _settings, measured, _speed, _grip, ReleaseMs);
            return WheelPlant.Overshoot(trace, 1) / Deg;
        }
    }
}
