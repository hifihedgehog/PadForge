using System;

namespace PadForge.Engine.Steering
{
    /// <summary>
    /// How fast a car's front tires bring a let-go steering wheel back at a
    /// speed, a grip and an angle, against the spring that Return Time sets
    /// (#600). A travel source whose Centering is From Telemetry
    /// multiplies its spring's rate by the answer.
    ///
    /// <para>The torque is the steering model's
    /// (<see cref="SteeringCurve.Torque"/>) for the default car with no
    /// jacking torque, which leaves the tires' aligning moment alone. That
    /// moment comes from the car turning, so it is nothing in a car that
    /// stands still, where the jacking torque would still pull at the
    /// wheel. The answer is the torque at the wheel's angle over the
    /// straight line the reference's torque leaves center on (100 km/h on a
    /// dry road): a spring's pull grows with the angle along that line, and
    /// a car's grows as steeply near center and then less, peaks, and
    /// lightens past the peak as the front tires slide. So at the reference
    /// the answer is 1 at center and falls further out, a faster car pulls
    /// harder at every angle, and a slippery road lightens the pull where
    /// the tires reach their limit. Return Time is the time to come back
    /// from near center at the reference.</para>
    /// </summary>
    public sealed class TireReturn
    {
        /// <summary>The reference speed in m/s: 100 km/h.</summary>
        public const double ReferenceSpeed = 100.0 / 3.6;

        /// <summary>The wheel a position stands for, in degrees from lock
        /// to lock: the range Vehicle mode's centering starts a wheel
        /// with (CenteringSettings.From).</summary>
        public const double WheelRangeDegrees = 900.0;

        /// <summary>The steps the curve is sampled at, WheelCentering's:
        /// speed in quarter meters a second and grip in fiftieths, each
        /// held until the reading is well into the next step.</summary>
        private const double SpeedStep = 0.25, GripStep = 0.02, StepHold = 0.75;

        private static readonly double LockRadians = WheelRangeDegrees * Math.PI / 360.0;
        private static readonly SteeringVehicle Car = SteeringVehicle.All[0];
        private static readonly SteeringFeel Feel =
            new SteeringFeel(Car.Feel.Gain, 0.0, Car.Feel.AssistWidth, Car.Feel.AssistFloor);

        // The reference stands on the same steps a reading is taken on, so
        // a car at the reference reads the reference's own curve and the
        // answer there is exactly 1.
        private static readonly Lazy<SteeringCurve> s_reference = new Lazy<SteeringCurve>(
            () => new SteeringCurve(Car, Feel, Math.Round(ReferenceSpeed / SpeedStep) * SpeedStep,
                Math.Round(RoadGrip.Dry / GripStep) * GripStep, LockRadians));

        private SteeringCurve _curve;
        private double _curveSpeed = -1, _curveGrip = -1;

        /// <summary>The rate of return at a wheel position from -1 to 1, a
        /// speed in m/s and a grip against dry asphalt's, over the spring's
        /// rate: the torque there over the reference's slope at center times
        /// the angle. 0 at a standstill. The curve is rebuilt in place as the
        /// speed or the grip moves a step, so a moving car costs the polling
        /// thread work and no allocation after the first call.</summary>
        public double Scale(double position, double speed, double grip)
        {
            if (!(speed > 0) || !double.IsFinite(speed)) return 0.0;
            grip = double.IsFinite(grip) ? Math.Clamp(grip, 0.05, 1.5) : RoadGrip.Dry;

            double quantSpeed = Held(speed, _curveSpeed, SpeedStep);
            double quantGrip = Held(grip, _curveGrip, GripStep);
            if (_curve == null)
                _curve = new SteeringCurve(Car, Feel, quantSpeed, quantGrip, LockRadians);
            else if (_curveSpeed != quantSpeed || _curveGrip != quantGrip)
                _curve.Rebuild(Car, Feel, quantSpeed, quantGrip, LockRadians);
            _curveSpeed = quantSpeed;
            _curveGrip = quantGrip;

            var reference = s_reference.Value;
            double angle = Math.Min(Math.Abs(position), 1.0) * LockRadians;
            double scale;
            if (angle <= 0)
            {
                // At center both torques are nothing, and the two slopes
                // there stand for them.
                scale = reference.CenterSlope > 0 ? _curve.CenterSlope / reference.CenterSlope : 1.0;
            }
            else
            {
                // The spring's pull at this angle, on the line the
                // reference's torque leaves center on.
                double against = reference.CenterSlope * angle;
                scale = against > 0 ? _curve.Torque(angle) / against : 1.0;
            }
            // The model runs no slower than its least speed. Under it the
            // aligning moment goes on falling as the speed squared, the
            // way lateral acceleration does, down to nothing at rest.
            if (speed < SteeringCurve.LeastSpeed)
            {
                double share = speed / SteeringCurve.LeastSpeed;
                scale *= share * share;
            }
            return scale > 0 && double.IsFinite(scale) ? scale : 0.0;
        }

        /// <summary>A reading on the steps of <paramref name="step"/>: the
        /// step the curve was built at while the reading stays within
        /// <see cref="StepHold"/> of it, the nearest step otherwise.</summary>
        private double Held(double reading, double built, double step)
            => _curve != null && Math.Abs(reading - built) <= StepHold * step ? built : Math.Round(reading / step) * step;
    }
}
