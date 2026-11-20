using System;
using System.Globalization;
using PadForge.Engine.Data;

namespace PadForge.Engine.Steering
{
    /// <summary>The effects a wheel runs itself that centering may use.</summary>
    [Flags]
    public enum CenteringEffects
    {
        None = 0,
        Spring = 1,
        Damper = 2,
        Friction = 4,
        All = Spring | Damper | Friction,
    }

    /// <summary>
    /// One wheel's centering settings (#588), read from its
    /// <see cref="PadSetting"/> with the two facts the device gives: the
    /// most torque it has and the range it turns.
    /// </summary>
    public readonly struct CenteringSettings
    {
        /// <summary>Vehicle mode. Off is the spring of Auto Centering
        /// Strength, which this type takes no part in.</summary>
        public readonly bool Vehicle;

        public readonly SteeringVehicle Car;
        public readonly double WeightPercent, OnCenterPercent, AssistPercent, DampingPercent;

        /// <summary>Friction in N·m.</summary>
        public readonly double Friction;

        /// <summary>Speed comes from the game while it sends one.</summary>
        public readonly bool SpeedFromGame;

        /// <summary>The fixed speed in m/s.</summary>
        public readonly double FixedSpeed;

        /// <summary>0 from the game, 1 dry, 2 wet, 3 snow, 4 ice.</summary>
        public readonly int GripChoice;

        /// <summary>The most torque the wheel gives, in N·m.</summary>
        public readonly double WheelTorque;

        /// <summary>The wheel's rotation range in degrees.</summary>
        public readonly double Range;

        /// <summary>The effects the wheel runs itself. A wheel with no
        /// damper has its damping set from the PC, and one with no
        /// friction effect gets none.</summary>
        public readonly CenteringEffects Effects;

        /// <summary>The steps the wheel's damper keeps its coefficient
        /// in, so the damper is only asked for a coefficient it has: 15
        /// on a Logitech or a Fanatec wheel
        /// (WheelCalibration.FourBitDamperSteps), 100 where nothing says
        /// it is that coarse.</summary>
        public readonly int DamperSteps;

        public bool Has(CenteringEffects effect) => (Effects & effect) != 0;

        /// <summary>The torque taken for a wheel that is on no list and
        /// has no figure entered, in N·m. A figure above the wheel's own
        /// makes every force weaker, never stronger, so the start is
        /// high: 25 N·m is the most the listed bases give.</summary>
        public const double UnlistedWheelTorque = 25.0;

        public const double DryGrip = RoadGrip.Dry, WetGrip = RoadGrip.Wet, SnowGrip = RoadGrip.Snow, IceGrip = RoadGrip.Ice;

        public CenteringSettings(bool vehicle, SteeringVehicle car, double weight, double onCenter, double assist,
            double damping, double friction, bool speedFromGame, double fixedSpeed, int gripChoice,
            double wheelTorque, double range, CenteringEffects effects = CenteringEffects.All,
            int damperSteps = WheelCalibration.FineDamperSteps)
        {
            Vehicle = vehicle;
            Car = car ?? SteeringVehicle.All[0];
            WeightPercent = weight;
            OnCenterPercent = onCenter;
            AssistPercent = assist;
            DampingPercent = damping;
            Friction = friction;
            SpeedFromGame = speedFromGame;
            FixedSpeed = fixedSpeed;
            GripChoice = gripChoice;
            WheelTorque = wheelTorque;
            Range = range;
            Effects = effects;
            DamperSteps = damperSteps > 0 ? damperSteps : WheelCalibration.FineDamperSteps;
        }

        /// <summary>Reads a device's settings. <paramref name="listedTorque"/>
        /// is the figure listed for the wheel's model in N·m, or 0, and
        /// <paramref name="setRange"/> the range PadForge sets on the
        /// wheel in degrees, or 0 where it sets none and the stored
        /// wheel range applies.</summary>
        public static CenteringSettings From(PadSetting ps, double listedTorque, double setRange,
            CenteringEffects effects = CenteringEffects.All, int damperSteps = WheelCalibration.FineDamperSteps)
        {
            if (ps == null) return default;
            double entered = Int(ps.CenteringWheelTorque, 0, 0, 500) / 10.0;
            double torque = entered > 0 ? entered : listedTorque > 0 ? listedTorque : UnlistedWheelTorque;
            double range = setRange > 0 ? setRange : Int(ps.CenteringWheelRange, 900, 40, 2520);
            return new CenteringSettings(
                ps.CenteringMode == "1",
                SteeringVehicle.ById(ps.CenteringVehicle),
                Int(ps.CenteringWeight, 100, 0, 200),
                Int(ps.CenteringOnCenter, 100, 0, 200),
                Int(ps.CenteringAssist, 100, 0, 200),
                Int(ps.CenteringDamping, 100, 0, 200),
                Int(ps.CenteringFriction, 30, 0, 150) / 100.0,
                ps.CenteringSpeedSource != "1",
                Int(ps.CenteringFixedSpeed, SpeedUnits.DefaultFixedSpeedKmh, 0, 320) / 3.6,
                Int(ps.CenteringGrip, 0, 0, 4),
                torque,
                range,
                effects,
                damperSteps);
        }

        /// <summary>The grip the model runs at: the choice's own, or the
        /// game's while the choice is the game and it sends one
        /// (<paramref name="fromGame"/> above 0), dry otherwise.</summary>
        public double GripFor(double fromGame) => GripChoice switch
        {
            1 => DryGrip,
            2 => WetGrip,
            3 => SnowGrip,
            4 => IceGrip,
            _ => fromGame > 0 ? Math.Clamp(fromGame, 0.05, 1.5) : DryGrip,
        };

        /// <summary>The speed the model runs at in m/s: the game's while
        /// the source is the game and it sends one
        /// (<paramref name="fromGame"/> at or above 0), the fixed speed
        /// otherwise.</summary>
        public double SpeedFor(double fromGame) => SpeedFromGame && fromGame >= 0 ? fromGame : FixedSpeed;

        private static int Int(string s, int fallback, int min, int max)
            => Math.Clamp(int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback, min, max);
    }

    /// <summary>
    /// What a wheel is sent for one poll in Vehicle mode: a constant
    /// force from the PC, and the three effects the wheel runs itself,
    /// in the units the writers take (coefficients and saturations of 0
    /// to 10000).
    /// </summary>
    public struct CenteringFrame
    {
        /// <summary>The constant force, -32767 to 32767. Its sign is the
        /// axis's: a positive level is a force that comes from the
        /// positive side and pushes toward lower axis values, the way a
        /// game's force is read (ForceFeedbackState.ComputeWheelSteeringPeak),
        /// so the force centers.</summary>
        public short Level;

        public int SpringCoefficient, SpringSaturation;
        public int DamperCoefficient, DamperSaturation;

        /// <summary>The friction effect's limit. Its coefficient is
        /// always full.</summary>
        public int FrictionSaturation;

        // What the card shows.
        public double AngleDegrees, TorqueNm, Speed, Grip;

        /// <summary>The damping in effect, in N·m·s/rad: the wheel's own
        /// damper at the coefficient sent, or the PC's where the wheel
        /// has none.</summary>
        public double Damping;

        /// <summary>The oscillation guard has cut the PC part's slope.</summary>
        public bool HeldBack;

        /// <summary>The PC part is off until the mode is set again or
        /// the Release Test runs: the wheel ran away from center as
        /// centering started, or it kept swinging however far the guard
        /// took the force down.</summary>
        public bool Stopped;
    }

    /// <summary>
    /// Vehicle mode's centering for one wheel (#588). Each poll it turns
    /// the wheel's position, the car's speed and the road's grip into a
    /// <see cref="CenteringFrame"/>.
    ///
    /// <para>The torque curve is a force set from the PC, as racing
    /// simulators set theirs. A force set from the PC arrives late, and a
    /// spring that arrives late acts as a spring with negative damping
    /// of its stiffness times the delay. So one rule holds: the damping
    /// the wheel's own firmware gives exceeds the PC part's steepest
    /// slope times the delay, by <see cref="Margin"/>. Where the damping
    /// falls short, the PC part's slope is cut to what the damping
    /// carries. Friction and damping run in the wheel, where nothing
    /// waits on the PC. Once the Release Test has measured the wheel's
    /// own spring, that spring carries the steep part near center and
    /// the PC adds the rest.</para>
    ///
    /// <para>A wheel with no damper of its own gets its damping from
    /// the PC, in the same force as the curve, and a damper that gives
    /// less at its full coefficient than is asked gets the rest that
    /// way. Late damping takes nothing from the rule: to first order a
    /// late spring and a late damper together leave the damping less the
    /// stiffness times the delay, the same as a damper in the wheel.
    /// What late damping costs is inertia, the damping times the delay,
    /// so it is held to <see cref="PcDampingShare"/> of the wheel's
    /// inertia over the delay.</para>
    ///
    /// <para>The wheel's damper may use all the torque the wheel has.
    /// Held to a share of it, the damper stopped pushing back harder
    /// once a released wheel was moving fast, the wheel reached center
    /// at a speed no coefficient could take off, and it swung past
    /// center whatever the damping (a simulated wheel of 2.2 N·m swung 9
    /// degrees past at every coefficient with the limit at six tenths,
    /// and none with the limit at the whole).</para>
    /// </summary>
    public sealed class WheelCentering
    {
        /// <summary>How far the firmware damping exceeds what the delayed
        /// PC spring takes away.</summary>
        public const double Margin = 1.5;

        /// <summary>The loop delay taken for a wheel no test has
        /// measured, in seconds: a poll, a report each way and the
        /// firmware's own step, taken long.</summary>
        public const double UnmeasuredDelay = 0.010;

        /// <summary>The most of the slope at center the wheel's own
        /// spring takes. The spring runs at one of the coefficients the
        /// Release Test measured, the stiffest that stays within this
        /// share, and the rest stays with the PC so the curve's shape
        /// near center is the model's.</summary>
        public const double SpringShare = 0.85;

        /// <summary>The most damping the PC gives, to a wheel with no
        /// damper of its own or one whose damper falls short, as a share
        /// of the wheel's inertia over the delay.</summary>
        public const double PcDampingShare = 0.3;

        /// <summary>Milliseconds between the two angles the PC's damping
        /// reads the wheel's speed from.</summary>
        public const int VelocityMs = 8;

        /// <summary>The inertia taken for a wheel no test has measured,
        /// in kg·m²: a 30 cm rim of about a kilogram with a geared motor
        /// behind it.</summary>
        public const double UnmeasuredInertia = 0.015;

        /// <summary>The swing, in radians either side of center, at
        /// which friction's steadying is counted: 5 degrees.</summary>
        public const double SteadySwing = 5.0 * Math.PI / 180.0;

        /// <summary>Milliseconds over which centering comes in after it
        /// turns on.</summary>
        public const int SoftStartMs = 1000;

        /// <summary>Degrees per second away from center, as the force
        /// comes in, that stop the PC part: a wheel left alone off center
        /// moves toward center under a centering force, and one that
        /// speeds farther out is being pushed the wrong way. The check
        /// reads a wheel that stood more than <see cref="RunawayDegrees"/>
        /// from center when the force started and has gone that much
        /// farther out on the same side. A wheel that comes back and
        /// swings past center is leaving center too, on the other side,
        /// and is no runaway. A wheel that stood at center is not read:
        /// the force there is less than the wheel's friction whichever
        /// way it points, so nothing shows until a hand turns the wheel,
        /// and a hand's turn is all the check would catch.</summary>
        public const double RunawayDegreesPerSecond = 200.0;

        public const double RunawayDegrees = 3.0;

        /// <summary>Milliseconds the wheel's motion is read over for that
        /// check. A poll is a millisecond, a wheel may report every eight,
        /// and a ten-bit axis moves nine tenths of a degree a count: read
        /// over one poll, a single count was 880 degrees a second and a
        /// wheel at rest on the edge of a count was a runaway.</summary>
        public const int MotionMs = 40;

        /// <summary>Milliseconds past the soft start's end that the check
        /// stays on, for a wheel its own friction held until the force
        /// was nearly whole.</summary>
        public const int RunawayTailMs = 300;

        /// <summary>Centering comes in once the wheel has stayed within
        /// <see cref="CalmDegrees"/> of one place for this many
        /// milliseconds. A wheel turns itself from lock to lock when it is
        /// plugged in, and a driver may be steering when the mode turns
        /// on: a force that came in then met a wheel already speeding
        /// away from center, which is what the runaway check stops.</summary>
        public const int CalmMs = 300;

        public const double CalmDegrees = 5.0;

        private SteeringCurve _curve;
        private string _curveCar;
        private double _curveWeight, _curveOnCenter, _curveAssist, _curveSpeed, _curveGrip, _curveLock;
        private readonly double[] _pc = new double[SteeringCurve.Steps + 1];
        private double _pcCap = -1, _pcSpringSlope = -1, _pcSpringLimit = -1;
        private SteeringCurve _pcCurve;
        private SteeringCurve _splitCurve;
        private WheelCalibration _splitMeasured;
        private double _splitTorque = -1, _splitSlope, _splitLimit, _splitSteepest;
        private int _splitCoefficient;
        private WheelCalibration _scaledFrom, _scaled;
        private double _scaledTorque;
        private double _velocity, _velocityAngle;
        private long _velocityTick = long.MinValue;

        private readonly OscillationGuard _guard = new OscillationGuard();
        private long _startTick = long.MinValue;
        private long _calmSince = long.MinValue, _motionTick = long.MinValue;
        private double _calmAngle, _motionAngle, _awaySpeed, _startAngle;
        private bool _motionRead;
        private bool _stopped;
        private bool _fullStart;

        /// <summary>The curve the last step used, for the card's chart.</summary>
        public SteeringCurve Curve => _curve;

        /// <summary>Forgets the soft start, the guard and the stop, so
        /// the next step starts centering afresh. Called when the mode
        /// turns on, the wheel connects or the engine starts.</summary>
        public void Reset()
        {
            _startTick = long.MinValue;
            _calmSince = long.MinValue;
            _motionTick = long.MinValue;
            _motionRead = false;
            _fullStart = false;
            _stopped = false;
            _velocityTick = long.MinValue;
            _velocity = 0;
            _guard.Reset();
        }

        /// <summary>Resets, and has the next step start at full force
        /// with no soft start: the Release Test's releases, where the
        /// wheel is let go into centering that is already there.</summary>
        public void ResetAtFull()
        {
            Reset();
            _fullStart = true;
        }

        /// <summary>One poll. <paramref name="axis"/> is the steering
        /// axis from -1 to 1, <paramref name="speed"/> in m/s and
        /// <paramref name="tickMs"/> a millisecond clock.</summary>
        public CenteringFrame Step(in CenteringSettings s, WheelCalibration measured, double axis, double speed, double grip, long tickMs)
        {
            var frame = new CenteringFrame();
            if (!s.Vehicle || s.WheelTorque <= 0) return frame;

            double halfRange = s.Range * Math.PI / 360.0;
            double angle = Math.Clamp(axis, -1.0, 1.0) * halfRange;
            EnsureCurve(s, speed, grip, halfRange);

            double torqueLimit = s.WheelTorque;
            // A measurement is in the newton meters of the torque figure it was taken with.
            measured = Scaled(measured, torqueLimit);
            double delay = measured != null ? Math.Clamp(measured.Delay, 0.002, 0.030) : UnmeasuredDelay;
            double inertia = measured != null ? measured.Inertia : UnmeasuredInertia;

            // The wheel's own spring, at a coefficient whose strength is known: a line under the curve from
            // center, cut off where the curve's own rise falls below it.
            EnsureSplit(s.Has(CenteringEffects.Spring) ? measured : null, torqueLimit);
            double springSlope = _splitSlope, springLimit = _splitLimit;

            // Damping: what the delayed PC part needs, or what returns the wheel without overshoot, whichever is more.
            double needed = _splitSteepest * delay * Margin;
            if (measured != null && measured.ReturnRatio > 0)
                needed = Math.Max(needed, 2.0 * measured.ReturnRatio * Math.Sqrt(_curve.CenterSlope * inertia));
            double asked = needed * s.DampingPercent / 100.0;

            int damperCoefficient = 0;
            double damping, pcDamping = 0;
            if (!s.Has(CenteringEffects.Damper) || (measured != null && !measured.HasDamper))
            {
                // No damper in the wheel, or one the test saw nothing of: the PC damps.
                pcDamping = Math.Min(asked, PcDampingShare * inertia / delay);
                damping = pcDamping;
            }
            else if (measured != null)
            {
                damperCoefficient = measured.DamperCoefficientFor(asked, s.DamperSteps);
                damping = measured.DampingAt(damperCoefficient);
                // A damper that falls short at its full coefficient has the rest from the PC.
                if (damping < asked)
                {
                    pcDamping = Math.Min(asked - damping, PcDampingShare * inertia / delay);
                    damping += pcDamping;
                }
            }
            else
            {
                // An unmeasured damper is taken to give a tenth of the wheel's torque per radian a second at its
                // full coefficient, and in proportion below it. The least step that gives what is asked.
                double damperFull = torqueLimit / 10.0;
                int step = (int)Math.Clamp(Math.Ceiling(asked / damperFull * s.DamperSteps - 1e-9), 0, s.DamperSteps);
                damperCoefficient = WheelCalibration.DamperGrid(step, s.DamperSteps);
                damping = damperCoefficient / 10000.0 * damperFull;
            }

            // Friction steadies a small swing as damping does: over a swing of amplitude A at the wheel's own
            // frequency it takes out the energy a damper of 4 F / (pi A w) would. Counted at SteadySwing, it
            // keeps a PC part with no damper from having no slope at all.
            bool wheelFriction = s.Has(CenteringEffects.Friction);
            double friction = (wheelFriction ? s.Friction : 0) + (measured != null ? measured.Friction : 0);
            double frequency = Math.Sqrt(Math.Max(_curve.CenterSlope, 1e-6) / inertia);
            double frictionAsDamping = 4.0 * friction / (Math.PI * SteadySwing * frequency);

            // The steepest the PC part may be for that damping. What the guard has taken off comes off the
            // slope and off the torque both: a wheel that swings wide is driven by the PC part's size, and a
            // gentler slope alone leaves that as it was.
            double cap = (damping + frictionAsDamping) / (delay * Margin) * _guard.Factor;
            EnsurePcTable(springSlope, springLimit, cap);

            TrackMotion(angle, tickMs);
            long sinceStart = Start(tickMs, angle);
            double ramp = sinceStart < 0 ? 0.0 : Math.Clamp(sinceStart / (double)SoftStartMs, 0.0, 1.0);
            CheckRunaway(angle, sinceStart, ramp);
            TrackVelocity(angle, tickMs);

            double magnitude = Math.Abs(angle);
            double pcTorque = _stopped ? 0 : Lookup(_pc, magnitude, _curve.Lock) * ramp;
            double springTorque = Math.Min(springSlope * magnitude, springLimit);
            // The two together never pass the wheel's own limit. What the guard has taken off comes off after
            // that, so a car that asks more than the wheel has still loses the guard's share.
            pcTorque = Math.Min(pcTorque, Math.Max(0, torqueLimit - springTorque)) * _guard.Factor;

            // The force, positive toward lower axis values: the curve's pull to center, and the PC's damping
            // against the wheel's motion. Damping from the PC stops with the PC's force, since on a wheel
            // that answers the other way it would drive the wheel on.
            double force = pcTorque * Math.Sign(angle);
            if (pcDamping > 0 && !_stopped && _guard.Factor > 0) force += pcDamping * _velocity * ramp;
            force = Math.Clamp(force, -torqueLimit, torqueLimit);

            int sign = measured != null ? measured.ForceSign : 1;
            frame.Level = (short)Math.Clamp(Math.Round(force / torqueLimit * 32767.0 * sign), -32767, 32767);
            if (springSlope > 0)
            {
                frame.SpringCoefficient = _splitCoefficient;
                frame.SpringSaturation = (int)Math.Clamp(Math.Round(springLimit / torqueLimit * 100.0) * 100.0, 0, 10000);
            }
            frame.DamperCoefficient = damperCoefficient;
            // The damper has the wheel's whole torque to push back with (the class's last paragraph).
            frame.DamperSaturation = damperCoefficient > 0 ? 10000 : 0;
            frame.FrictionSaturation = wheelFriction
                ? (int)Math.Clamp(Math.Round(s.Friction / torqueLimit * 1000.0) * 10.0, 0, 10000)
                : 0;

            // The guard reads a wheel the PC's force is on. Before the force starts, a swing is a hand's or
            // the wheel's own turn at power-on, and counting it took the force down before it had come in.
            if (sinceStart >= 0) _guard.Add(angle, tickMs);
            frame.AngleDegrees = angle * 180.0 / Math.PI;
            frame.Damping = damping;
            frame.TorqueNm = (pcTorque + springTorque) * Math.Sign(angle);
            frame.Speed = _curve.Speed;
            frame.Grip = _curve.Grip;
            frame.HeldBack = _guard.Factor < 1.0;
            frame.Stopped = _stopped || _guard.Factor <= 0;
            return frame;
        }

        /// <summary>The measurement in the newton meters of the torque
        /// figure in use. Every torque in it was read as a share of the
        /// figure it was taken with, so a changed figure rescales
        /// them.</summary>
        private WheelCalibration Scaled(WheelCalibration measured, double wheelTorque)
        {
            if (measured == null) return null;
            if (!ReferenceEquals(_scaledFrom, measured) || _scaledTorque != wheelTorque)
            {
                _scaledFrom = measured;
                _scaledTorque = wheelTorque;
                _scaled = measured.ScaledTo(wheelTorque);
            }
            return _scaled;
        }

        /// <summary>The wheel's speed in radians a second, signed as the
        /// axis, for the PC's damping.</summary>
        private void TrackVelocity(double angle, long tickMs)
        {
            long dt = tickMs - _velocityTick;
            if (_velocityTick == long.MinValue || dt < 0)
            {
                _velocityTick = tickMs;
                _velocityAngle = angle;
                _velocity = 0;
            }
            else if (dt >= VelocityMs)
            {
                _velocity = (angle - _velocityAngle) / (dt / 1000.0);
                _velocityTick = tickMs;
                _velocityAngle = angle;
            }
        }

        /// <summary>The split between the wheel's own spring and the PC
        /// part, worked out once for a curve: the spring's coefficient,
        /// slope and limit, and the steepest the rest rises. The spring
        /// runs only at a coefficient the Release Test measured, since
        /// nothing says a coefficient between two of them gives a
        /// strength between theirs.</summary>
        private void EnsureSplit(WheelCalibration measured, double torqueLimit)
        {
            if (ReferenceEquals(_splitCurve, _curve) && ReferenceEquals(_splitMeasured, measured) && _splitTorque == torqueLimit)
                return;
            _splitSlope = 0;
            _splitLimit = 0;
            _splitCoefficient = 0;
            if (measured != null)
            {
                double most = SpringShare * _curve.CenterSlope;
                for (int i = 0; i < WheelCalibration.SpringCoefficients.Length; i++)
                {
                    double slope = measured.SpringAt(i) / _curve.Lock;
                    if (slope <= _splitSlope || slope > most) continue;
                    _splitSlope = slope;
                    _splitCoefficient = WheelCalibration.SpringCoefficients[i];
                }
                if (_splitSlope > 0)
                {
                    _splitLimit = Math.Min(LineUnderCurve(_curve, _splitSlope), torqueLimit);
                    if (_splitLimit < 0.02 * torqueLimit)
                    {
                        _splitSlope = 0;
                        _splitLimit = 0;
                        _splitCoefficient = 0;
                    }
                }
            }
            _splitSteepest = _splitSlope > 0 ? SteepestRise(_curve, _splitSlope, _splitLimit) : _curve.SteepestSlope;
            _splitCurve = _curve;
            _splitMeasured = measured;
            _splitTorque = torqueLimit;
        }

        private void EnsureCurve(in CenteringSettings s, double speed, double grip, double halfRange)
        {
            // Speed in quarter meters a second and grip in fiftieths, so a moving car rebuilds the table a few
            // times a second at most.
            double quantSpeed = Math.Round(Math.Max(0, speed) * 4.0) / 4.0;
            double quantGrip = Math.Round(grip * 50.0) / 50.0;
            if (_curve != null && _curveCar == s.Car.Id && _curveWeight == s.WeightPercent
                && _curveOnCenter == s.OnCenterPercent && _curveAssist == s.AssistPercent
                && _curveSpeed == quantSpeed && _curveGrip == quantGrip && _curveLock == halfRange)
                return;
            _curve = new SteeringCurve(s.Car, s.Car.Feel.Scaled(s.WeightPercent, s.OnCenterPercent, s.AssistPercent),
                quantSpeed, quantGrip, halfRange);
            _curveCar = s.Car.Id;
            _curveWeight = s.WeightPercent;
            _curveOnCenter = s.OnCenterPercent;
            _curveAssist = s.AssistPercent;
            _curveSpeed = quantSpeed;
            _curveGrip = quantGrip;
            _curveLock = halfRange;
        }

        /// <summary>The limit for a spring of this slope from center, so
        /// the wheel's own spring never gives more than the car asks:
        /// the torque at which the line first passes above the curve,
        /// or the least the curve falls to after that point, whichever
        /// is lower. Past the tires' limit the curve falls, and a spring
        /// held at a higher limit would keep the wheel heavy there.</summary>
        internal static double LineUnderCurve(SteeringCurve curve, double slope)
        {
            double limit = 0;
            int i = 1;
            for (; i <= SteeringCurve.Steps; i++)
            {
                double angle = AngleAt(i, curve.Lock);
                double line = slope * angle;
                if (line > curve.Torque(angle)) break;
                limit = line;
            }
            for (; i <= SteeringCurve.Steps; i++)
                limit = Math.Min(limit, curve.Torque(AngleAt(i, curve.Lock)));
            return limit;
        }

        /// <summary>The steepest the curve rises once the wheel's own
        /// spring is taken off it.</summary>
        internal static double SteepestRise(SteeringCurve curve, double springSlope, double springLimit)
        {
            double steepest = 0, before = 0;
            for (int i = 1; i <= SteeringCurve.Steps; i++)
            {
                double angle = AngleAt(i, curve.Lock);
                double rest = curve.Torque(angle) - Math.Min(springSlope * angle, springLimit);
                double slope = (rest - before) / (angle - AngleAt(i - 1, curve.Lock));
                if (slope > steepest) steepest = slope;
                before = rest;
            }
            return steepest;
        }

        /// <summary>The PC part: the curve less the wheel's own spring,
        /// with no rise steeper than the cap. It never passes what is
        /// left of the curve, so where the curve falls below it, past
        /// the tires' limit, it falls with the curve.</summary>
        private void EnsurePcTable(double springSlope, double springLimit, double cap)
        {
            if (ReferenceEquals(_pcCurve, _curve) && _pcCap == cap && _pcSpringSlope == springSlope && _pcSpringLimit == springLimit)
                return;
            double held = 0;
            _pc[0] = 0;
            for (int i = 1; i <= SteeringCurve.Steps; i++)
            {
                double angle = AngleAt(i, _curve.Lock);
                double rest = Math.Max(0, _curve.Torque(angle) - Math.Min(springSlope * angle, springLimit));
                double most = Math.Max(0, cap) * (angle - AngleAt(i - 1, _curve.Lock));
                held = Math.Min(rest, held + most);
                _pc[i] = held;
            }
            _pcCurve = _curve;
            _pcCap = cap;
            _pcSpringSlope = springSlope;
            _pcSpringLimit = springLimit;
        }

        private static double AngleAt(int i, double lockRadians)
        {
            double share = (double)i / SteeringCurve.Steps;
            return lockRadians * share * share;
        }

        private static double Lookup(double[] table, double magnitude, double lockRadians)
        {
            if (magnitude >= lockRadians) return table[SteeringCurve.Steps];
            double position = Math.Sqrt(magnitude / lockRadians) * SteeringCurve.Steps;
            int i = (int)position;
            if (i >= SteeringCurve.Steps) return table[SteeringCurve.Steps];
            double low = AngleAt(i, lockRadians), high = AngleAt(i + 1, lockRadians);
            return table[i] + (magnitude - low) / (high - low) * (table[i + 1] - table[i]);
        }

        /// <summary>Where the wheel is and how fast it leaves center: the
        /// place it has stayed near, for the start, and its speed away
        /// from center over the last <see cref="MotionMs"/>, for the
        /// runaway check.</summary>
        private void TrackMotion(double angle, long tickMs)
        {
            if (_calmSince == long.MinValue || tickMs < _calmSince
                || Math.Abs(angle - _calmAngle) > CalmDegrees * Math.PI / 180.0)
            {
                _calmSince = tickMs;
                _calmAngle = angle;
            }

            long dt = tickMs - _motionTick;
            if (_motionTick == long.MinValue || dt < 0)
            {
                _motionTick = tickMs;
                _motionAngle = angle;
                _motionRead = false;
            }
            else if (dt >= MotionMs)
            {
                _awaySpeed = (Math.Abs(angle) - Math.Abs(_motionAngle)) / (dt / 1000.0) * 180.0 / Math.PI;
                _motionRead = true;
                _motionTick = tickMs;
                _motionAngle = angle;
            }
        }

        /// <summary>Starts the soft start once the wheel has been at rest
        /// after a reset, and answers the milliseconds since, or -1 while
        /// the wheel still moves and no force has come in.</summary>
        private long Start(long tickMs, double angle)
        {
            if (_startTick == long.MinValue)
            {
                if (_fullStart)
                {
                    _startTick = tickMs - SoftStartMs - RunawayTailMs - 1;
                    _fullStart = false;
                }
                else if (tickMs - _calmSince >= CalmMs)
                {
                    _startTick = tickMs;
                    _startAngle = angle;
                }
                else return -1;
            }
            return tickMs - _startTick;
        }

        /// <summary>As the force comes in, a wheel that speeds farther
        /// out from where it stood stops the PC part
        /// (<see cref="RunawayDegreesPerSecond"/>). The wheel was at
        /// rest when the force started, so what moves it is the force.</summary>
        private void CheckRunaway(double angle, long sinceStart, double ramp)
        {
            if (_stopped || !_motionRead || sinceStart < 0 || sinceStart > SoftStartMs + RunawayTailMs || ramp < 0.2) return;
            double band = RunawayDegrees * Math.PI / 180.0;
            double stood = Math.Abs(_startAngle);
            if (stood <= band || Math.Sign(angle) != Math.Sign(_startAngle)) return;
            if (_awaySpeed > RunawayDegreesPerSecond && Math.Abs(angle) > stood + band)
                _stopped = true;
        }
    }
}
