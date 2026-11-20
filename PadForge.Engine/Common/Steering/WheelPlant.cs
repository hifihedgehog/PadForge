using System;
using System.Collections.Generic;

namespace PadForge.Engine.Steering
{
    /// <summary>
    /// A wheel as the centering math sees one (#588): a rim with inertia
    /// and friction, a motor that answers a constant force after a delay,
    /// and a firmware spring, damper and friction effect that answer at
    /// once. The Release Test uses it to find the damping that returns
    /// the wheel it measured without overshoot, and the tests use it to
    /// hold the stability rule and the test's own measurements against a
    /// wheel whose numbers are known.
    /// </summary>
    public sealed class WheelPlant
    {
        /// <summary>Moment of inertia in kg·m².</summary>
        public double Inertia = 0.015;

        /// <summary>The wheel's own friction in N·m.</summary>
        public double Friction = 0.10;

        /// <summary>The wheel's own damping in N·m·s/rad.</summary>
        public double Damping = 0.0;

        /// <summary>Seconds from a constant force command to its torque.</summary>
        public double Delay = 0.006;

        /// <summary>The most torque the motor gives, in N·m.</summary>
        public double WheelTorque = 2.2;

        /// <summary>Damping at the damper's full coefficient, in N·m·s/rad.</summary>
        public double DamperFull = 0.5;

        /// <summary>Stiffness at the spring's full coefficient, in N·m
        /// per unit of deflection, a unit being half the range.</summary>
        public double SpringFull = 20.0;

        /// <summary>How the damper's and the spring's strength follow
        /// the coefficient: the full strength times the coefficient's
        /// share to this power. 1 is a firmware where twice the
        /// coefficient is twice the strength.</summary>
        public double DamperExponent = 1.0, SpringExponent = 1.0;

        /// <summary>The damper's and the spring's strength for a
        /// coefficient of 0 to 10000, where a wheel's own table is known
        /// and takes the place of the two figures above.</summary>
        public Func<int, double> DamperStrength, SpringStrength;

        /// <summary>The slope of the friction effect near rest, in
        /// N·m·s/rad: a steep damper under a limit.</summary>
        public double FrictionSlope = 5.0;

        /// <summary>-1 for a wheel whose motor answers a positive level
        /// by pushing toward higher axis values.</summary>
        public int ForceSign = 1;

        /// <summary>Half the rotation range in radians.</summary>
        public double HalfRange = 450.0 * Math.PI / 180.0;

        /// <summary>Angle in radians and speed in rad/s.</summary>
        public double Angle, Velocity;

        /// <summary>A torque from outside, a hand's, in N·m.</summary>
        public double HandTorque;

        private readonly Queue<(double due, short level)> _pending = new Queue<(double, short)>();
        private short _level;
        private int _springSaturation, _damperSaturation, _frictionSaturation;
        private double _spring, _damper;
        private double _time, _owed;

        /// <summary>The steering axis from -1 to 1.</summary>
        public double Axis => Math.Clamp(Angle / HalfRange, -1.0, 1.0);

        public double Time => _time;

        /// <summary>Takes a poll's commands. The constant force arrives
        /// after the delay. The firmware effects take hold at once.</summary>
        public void Command(short level, int springCoefficient, int springSaturation,
            int damperCoefficient, int damperSaturation, int frictionSaturation)
        {
            _pending.Enqueue((_time + Delay, level));
            _springSaturation = springSaturation;
            _damperSaturation = damperSaturation;
            _frictionSaturation = frictionSaturation;
            _spring = SpringStrength != null ? SpringStrength(springCoefficient)
                : SpringFull * Math.Pow(Math.Clamp(springCoefficient, 0, 10000) / 10000.0, SpringExponent);
            _damper = DamperStrength != null ? DamperStrength(damperCoefficient)
                : DamperFull * Math.Pow(Math.Clamp(damperCoefficient, 0, 10000) / 10000.0, DamperExponent);
        }

        public void Command(in CenteringFrame frame)
            => Command(frame.Level, frame.SpringCoefficient, frame.SpringSaturation,
                frame.DamperCoefficient, frame.DamperSaturation, frame.FrictionSaturation);

        /// <summary>Moves the wheel on by a time in seconds, in steps of
        /// a tenth of a millisecond. A time that is no whole number of
        /// steps carries its remainder to the next call.</summary>
        public void Advance(double seconds)
        {
            const double Dt = 0.0001;
            _owed += seconds;
            int steps = (int)Math.Floor(_owed / Dt + 1e-6);
            _owed -= steps * Dt;
            for (int i = 0; i < steps; i++)
            {
                _time += Dt;
                while (_pending.Count > 0 && _pending.Peek().due <= _time) _level = _pending.Dequeue().level;

                // A positive level comes from the positive side and pushes toward lower axis values.
                double torque = -ForceSign * (_level / 32767.0) * WheelTorque + HandTorque;
                double springLimit = _springSaturation / 10000.0 * WheelTorque;
                torque -= Math.Clamp(_spring * (Angle / HalfRange), -springLimit, springLimit);
                double damperLimit = _damperSaturation / 10000.0 * WheelTorque;
                torque -= Math.Clamp(_damper * Velocity, -damperLimit, damperLimit);
                torque -= Damping * Velocity;
                double frictionLimit = _frictionSaturation / 10000.0 * WheelTorque;
                torque -= Math.Clamp(FrictionSlope * Velocity, -frictionLimit, frictionLimit);

                // The wheel's own friction holds a wheel at rest and opposes one that moves.
                if (Velocity == 0)
                {
                    if (Math.Abs(torque) <= Friction) continue;
                    torque -= Friction * Math.Sign(torque);
                    Velocity = torque / Inertia * Dt;
                }
                else
                {
                    double before = Velocity;
                    Velocity += (torque - Friction * Math.Sign(Velocity)) / Inertia * Dt;
                    // Friction stops a wheel, it never turns it around.
                    if (before * Velocity < 0) Velocity = 0;
                }
                Angle += Velocity * Dt;
                if (Angle > HalfRange) { Angle = HalfRange; Velocity = 0; }
                else if (Angle < -HalfRange) { Angle = -HalfRange; Velocity = 0; }
            }
        }

        /// <summary>Runs a wheel's centering on this plant for a time
        /// and answers the angles, one a millisecond, in radians. The
        /// wheel is let go where it stands.</summary>
        public double[] Run(WheelCentering centering, in CenteringSettings settings, WheelCalibration measured,
            double speed, double grip, int milliseconds)
        {
            var trace = new double[milliseconds];
            long tick = (long)Math.Round(_time * 1000.0);
            for (int ms = 0; ms < milliseconds; ms++)
            {
                var frame = centering.Step(settings, measured, Axis, speed, grip, tick + ms);
                Command(frame);
                Advance(0.001);
                trace[ms] = Angle;
            }
            return trace;
        }

        /// <summary>How far past center a trace swings, in radians: the
        /// most it reaches on the side opposite its start.</summary>
        public static double Overshoot(double[] trace, double start)
        {
            double most = 0;
            foreach (double angle in trace)
            {
                double past = start > 0 ? -angle : angle;
                if (past > most) most = past;
            }
            return most;
        }
    }
}
