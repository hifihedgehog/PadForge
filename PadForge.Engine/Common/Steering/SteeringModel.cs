using System;

namespace PadForge.Engine.Steering
{
    /// <summary>
    /// The steering feel model of Balachandran and Gerdes (IEEE/ASME
    /// Transactions on Mechatronics, 2014) at steady cornering: for a
    /// handwheel angle, a speed and a grip, the torque a car's steering
    /// puts on the rim.
    ///
    /// <para>The car is the paper's bicycle model (equations 14 to 17)
    /// with nothing changing: front and rear lateral force carry the
    /// lateral acceleration through each axle's share of the mass. Tire
    /// force is the Fiala brush model (equation 8). The handwheel torque
    /// is the tire moment gain times the power assist weighting times
    /// the sum of the aligning moment and the jacking torque (equations
    /// 4, 7 and 9 to 13). The paper's damping and inertia terms are zero
    /// when nothing moves, and the wheel renders damping itself.</para>
    /// </summary>
    public static class SteeringModel
    {
        /// <summary>The Fiala model's lateral force for a slip angle, both
        /// as magnitudes (equation 8). With x = C tan(alpha) / (3 mu Fz)
        /// the force is mu Fz (1 - (1 - x)^3) up to x = 1 and mu Fz past
        /// it.</summary>
        public static double FialaForce(double slip, double stiffness, double load, double grip)
        {
            double peak = grip * load;
            if (peak <= 0) return 0;
            double x = stiffness * Math.Tan(slip) / (3.0 * peak);
            if (x >= 1.0) return peak;
            double rest = 1.0 - x;
            return peak * (1.0 - rest * rest * rest);
        }

        /// <summary>The slip angle that gives a lateral force, both as
        /// magnitudes: <see cref="FialaForce"/> solved for the angle. A
        /// force at or past the tire's limit answers the angle where the
        /// tire starts to slide.</summary>
        public static double FialaSlip(double force, double stiffness, double load, double grip)
        {
            double peak = grip * load;
            if (peak <= 0 || stiffness <= 0) return 0;
            double share = force / peak;
            double x = share >= 1.0 ? 1.0 : 1.0 - Math.Cbrt(1.0 - Math.Max(0.0, share));
            return Math.Atan(3.0 * peak * x / stiffness);
        }

        /// <summary>The share of a tire's limit the steady-state table
        /// stops at. The slip angle's slope is unbounded at the limit
        /// itself.</summary>
        internal const double LimitShare = 0.999999;

        /// <summary>The road wheel angle that holds a lateral
        /// acceleration in a steady turn, with the front axle's slip
        /// angle and lateral force there. From the bicycle model: delta =
        /// L ay / U^2 + alpha_f - alpha_r, slip angles as
        /// magnitudes.</summary>
        internal static double RoadWheelAngle(SteeringVehicle car, double lateralAcceleration, double speed, double grip,
            out double frontSlip, out double frontForce)
        {
            double wheelbase = car.Wheelbase;
            frontForce = car.Mass * lateralAcceleration * car.B / wheelbase;
            double rearForce = car.Mass * lateralAcceleration * car.A / wheelbase;
            frontSlip = FialaSlip(frontForce, car.FrontStiffness, car.FrontLoad, grip);
            double rearSlip = FialaSlip(rearForce, car.RearStiffness, car.RearLoad, grip);
            return wheelbase * lateralAcceleration / (speed * speed) + frontSlip - rearSlip;
        }

        /// <summary>The handwheel torque for a front axle state: gain
        /// times weighting times aligning moment plus jacking torque.
        /// The pneumatic trail falls in a line with the tangent of the
        /// slip angle and is gone where the tire slides (equation
        /// 9).</summary>
        internal static double TorqueAt(SteeringVehicle car, SteeringFeel feel, double roadWheelAngle,
            double frontSlip, double frontForce, double grip)
        {
            double trail = car.PneumaticTrail
                - car.PneumaticTrail * car.FrontStiffness / (3.0 * grip * car.FrontLoad) * Math.Tan(frontSlip);
            if (trail < 0) trail = 0;
            double aligning = frontForce * (car.MechanicalTrail + trail);
            double jacking = feel.JackingStiffness * roadWheelAngle;
            double width = feel.AssistWidth;
            double weighting = width > 0
                ? Math.Exp(-frontSlip * frontSlip / (2.0 * width * width)) * (1.0 - feel.AssistFloor) + feel.AssistFloor
                : feel.AssistFloor;
            return feel.Gain * weighting * (aligning + jacking);
        }
    }

    /// <summary>
    /// One car's torque against handwheel angle at one speed and grip,
    /// sampled once so the force feedback pass reads it with a lookup.
    /// The curve is odd in the angle, so it holds the positive half.
    ///
    /// <para>Samples sit on angle = lock times (i / N)^2, close together
    /// near center where the curve is steep.</para>
    /// </summary>
    public sealed class SteeringCurve
    {
        /// <summary>Samples from center to lock, less one.</summary>
        public const int Steps = 512;

        /// <summary>The least speed the model runs at, in m/s. The
        /// bicycle model divides by the speed squared, and below walking
        /// pace the aligning moment is nothing beside the jacking
        /// torque.</summary>
        public const double LeastSpeed = 0.5;

        private readonly double[] _torque = new double[Steps + 1];
        private readonly double[] _lateral = new double[Steps + 1];

        /// <summary>Handwheel angle at the end of the table, in radians.</summary>
        public double Lock { get; private set; }

        public double Speed { get; private set; }
        public double Grip { get; private set; }

        /// <summary>The steepest the torque rises along the curve, in
        /// N·m per radian of handwheel.</summary>
        public double SteepestSlope { get; private set; }

        /// <summary>The slope at center, in N·m per radian of handwheel.</summary>
        public double CenterSlope { get; private set; }

        /// <summary>The greatest torque inside the lock, in N·m.</summary>
        public double PeakTorque { get; private set; }

        /// <summary>The handwheel angle of <see cref="PeakTorque"/>, in radians.</summary>
        public double PeakAngle { get; private set; }

        public SteeringCurve(SteeringVehicle car, SteeringFeel feel, double speed, double grip, double lockRadians)
            => Build(car, feel, speed, grip, lockRadians);

        /// <summary>Samples the curve again, for another car, speed, grip
        /// or lock, into the same tables. The force feedback pass keeps
        /// one curve a wheel and rebuilds it as the car's speed changes,
        /// so a moving car costs the polling thread no allocation.</summary>
        internal void Rebuild(SteeringVehicle car, SteeringFeel feel, double speed, double grip, double lockRadians)
            => Build(car, feel, speed, grip, lockRadians);

        private void Build(SteeringVehicle car, SteeringFeel feel, double speed, double grip, double lockRadians)
        {
            if (speed < LeastSpeed) speed = LeastSpeed;
            if (grip < 0.05) grip = 0.05;
            if (lockRadians < 0.01) lockRadians = 0.01;
            Speed = speed;
            Grip = grip;
            Lock = lockRadians;

            // The car's steady states on a lateral acceleration grid, close together near zero.
            const int States = 512;
            Span<double> angle = stackalloc double[States + 1];
            Span<double> slip = stackalloc double[States + 1];
            Span<double> force = stackalloc double[States + 1];
            Span<double> lateral = stackalloc double[States + 1];
            double top = grip * SteeringVehicle.Gravity * SteeringModel.LimitShare;
            int last = 0;
            for (int k = 0; k <= States; k++)
            {
                double share = (double)k / States;
                double ay = top * share * share;
                double d = SteeringModel.RoadWheelAngle(car, ay, speed, grip, out double a, out double f);
                // The angle has to rise with lateral acceleration for the lookup. Where it stops rising the
                // rear axle gives out before the front, and the table ends at the last state that rose.
                if (k > 0 && d <= angle[last]) continue;
                last = k == 0 ? 0 : last + 1;
                angle[last] = d;
                slip[last] = a;
                force[last] = f;
                lateral[last] = ay;
            }

            int at = 0;
            double peak = 0, peakAngle = 0, steepest = 0;
            for (int i = 0; i <= Steps; i++)
            {
                double share = (double)i / Steps;
                double handwheel = lockRadians * share * share;
                double roadWheel = handwheel / car.SteeringRatio;
                double a, f, ay;
                if (roadWheel >= angle[last])
                {
                    // Past the last steady state the car turns no harder, and the extra angle is front slip.
                    a = slip[last] + (roadWheel - angle[last]);
                    f = force[last];
                    ay = lateral[last];
                }
                else
                {
                    while (at < last - 1 && angle[at + 1] <= roadWheel) at++;
                    double span = angle[at + 1] - angle[at];
                    double t = span > 0 ? (roadWheel - angle[at]) / span : 0;
                    a = slip[at] + t * (slip[at + 1] - slip[at]);
                    f = force[at] + t * (force[at + 1] - force[at]);
                    ay = lateral[at] + t * (lateral[at + 1] - lateral[at]);
                }
                double torque = SteeringModel.TorqueAt(car, feel, roadWheel, a, f, grip);
                _torque[i] = torque;
                _lateral[i] = ay;
                if (torque > peak) { peak = torque; peakAngle = handwheel; }
                if (i > 0)
                {
                    double before = lockRadians * (i - 1.0) / Steps * ((i - 1.0) / Steps);
                    double slope = (torque - _torque[i - 1]) / (handwheel - before);
                    if (slope > steepest) steepest = slope;
                    if (i == 1) CenterSlope = slope;
                }
            }
            PeakTorque = peak;
            PeakAngle = peakAngle;
            SteepestSlope = steepest;
        }

        /// <summary>The size of the centering torque in N·m at a
        /// handwheel angle in radians, carrying the angle's sign. The
        /// force on the wheel points the other way, toward center. An
        /// angle past the lock reads the lock's torque.</summary>
        public double Torque(double handwheelRadians)
        {
            double magnitude = Math.Abs(handwheelRadians);
            double value = Lookup(_torque, magnitude);
            return handwheelRadians < 0 ? -value : value;
        }

        /// <summary>Lateral acceleration in m/s² at a handwheel angle in
        /// radians, as a magnitude.</summary>
        public double LateralAcceleration(double handwheelRadians) => Lookup(_lateral, Math.Abs(handwheelRadians));

        private double Lookup(double[] table, double magnitude)
        {
            if (magnitude >= Lock) return table[Steps];
            double position = Math.Sqrt(magnitude / Lock) * Steps;
            int i = (int)position;
            if (i >= Steps) return table[Steps];
            // Between two samples the angle runs as the square of the position, so the weight comes from the angle.
            double low = Lock * ((double)i / Steps) * ((double)i / Steps);
            double high = Lock * ((i + 1.0) / Steps) * ((i + 1.0) / Steps);
            double t = (magnitude - low) / (high - low);
            return table[i] + t * (table[i + 1] - table[i]);
        }
    }

    /// <summary>
    /// The measures steering feel is tested by, from the weave test of
    /// ISO 13674-1 as the paper states them (its section III), read off
    /// the steady-state curve: the paper takes them from a 0.2 Hz weave,
    /// slow enough that the steady state stands in for it.
    /// </summary>
    public readonly struct SteeringMeasures
    {
        /// <summary>On-center feel: the torque gradient up to 0.05 g of
        /// lateral acceleration, in N·m/g. Not a number where the car
        /// never reaches 0.05 g inside the lock.</summary>
        public readonly double OnCenterFeel;

        /// <summary>Effective torque stiffness: the torque gradient over
        /// the first fifth of the angle that gives 0.2 g, in N·m per
        /// degree of handwheel.</summary>
        public readonly double TorqueStiffness;

        /// <summary>Linearity: the torque gradient between 0.10 and
        /// 0.15 g over the on-center feel, in percent.</summary>
        public readonly double Linearity;

        /// <summary>Steering sensitivity: lateral acceleration per
        /// handwheel angle up to 0.2 g, in g per 100 degrees.</summary>
        public readonly double Sensitivity;

        /// <summary>The greatest torque inside the lock, in N·m.</summary>
        public readonly double PeakTorque;

        private SteeringMeasures(double onCenter, double stiffness, double linearity, double sensitivity, double peak)
        {
            OnCenterFeel = onCenter;
            TorqueStiffness = stiffness;
            Linearity = linearity;
            Sensitivity = sensitivity;
            PeakTorque = peak;
        }

        /// <summary>Reads the measures off a curve. Each is a least
        /// squares gradient over samples a fiftieth of a degree apart,
        /// as the fit that set the cars' feel took them.</summary>
        public static SteeringMeasures From(SteeringCurve curve)
        {
            const double Step = 0.02 * Math.PI / 180.0;
            double g = SteeringVehicle.Gravity;
            double weaveAngle = double.NaN;
            int samples = (int)(curve.Lock / Step);
            for (int i = 0; i <= samples; i++)
            {
                if (curve.LateralAcceleration(i * Step) >= 0.2 * g) { weaveAngle = i * Step; break; }
            }

            var onCenter = new Gradient();
            var linear = new Gradient();
            var stiffness = new Gradient();
            var sensitivity = new Gradient();
            for (int i = 0; i <= samples; i++)
            {
                double angle = i * Step;
                double ay = curve.LateralAcceleration(angle) / g;
                double torque = curve.Torque(angle);
                if (ay <= 0.05) onCenter.Add(ay, torque);
                if (ay >= 0.10 && ay <= 0.15) linear.Add(ay, torque);
                if (ay <= 0.2) sensitivity.Add(angle * 180.0 / Math.PI, ay);
                if (!double.IsNaN(weaveAngle) && angle <= 0.2 * weaveAngle) stiffness.Add(angle * 180.0 / Math.PI, torque);
                if (ay > 0.2 && !double.IsNaN(weaveAngle) && angle > weaveAngle) break;
            }

            // A gradient needs the car to reach the top of its range: on-center feel 0.05 g, linearity 0.15 g.
            bool reachesOnCenter = curve.LateralAcceleration(curve.Lock) >= 0.05 * g;
            bool reachesLinear = curve.LateralAcceleration(curve.Lock) >= 0.15 * g;
            double center = reachesOnCenter ? onCenter.Slope : double.NaN;
            return new SteeringMeasures(
                center,
                double.IsNaN(weaveAngle) ? double.NaN : stiffness.Slope,
                reachesLinear && center > 0 ? 100.0 * linear.Slope / center : double.NaN,
                double.IsNaN(weaveAngle) ? double.NaN : 100.0 * sensitivity.Slope,
                curve.PeakTorque);
        }

        /// <summary>A running least squares line through added points.</summary>
        private struct Gradient
        {
            private double _n, _x, _y, _xx, _xy;

            public void Add(double x, double y)
            {
                _n++;
                _x += x;
                _y += y;
                _xx += x * x;
                _xy += x * y;
            }

            public double Slope
            {
                get
                {
                    double spread = _n * _xx - _x * _x;
                    return _n < 2 || spread <= 0 ? double.NaN : (_n * _xy - _x * _y) / spread;
                }
            }
        }
    }
}
