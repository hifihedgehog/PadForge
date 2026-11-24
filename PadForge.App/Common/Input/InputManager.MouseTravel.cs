using PadForge.Common.Telemetry;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    // What the engine's "Mouse Travel" input (#597) asks the app for: the road
    // a game reports and the force a game sends a slot's virtual wheel. The
    // wheel itself is the engine's (SourceKindRuntime.TickMouseTravel).
    public partial class InputManager
    {
        /// <summary>The app's SourceCoercion.MouseTravelRoadProvider: the
        /// game's speed in meters a second, below 0 while none sends one,
        /// the grip under the front wheels, 0 while none says, and whether
        /// the Game Telemetry extra is on, which Lock at Speed needs. The
        /// ask keeps the telemetry hub reading, as a wheel in Vehicle mode
        /// does (StepWheelCentering): the hub starts on the first ask and
        /// stops a few seconds after the last.</summary>
        internal static (double speed, double grip, bool lockAtSpeed) MouseTravelRoad()
        {
            TelemetryHub.RequestActive();
            double speed = -1, grip = 0;
            if (TelemetryHub.TryGetCurrent(out var road))
            {
                if (road.HasSpeed) speed = road.Speed;
                grip = road.Grip;
            }
            return (speed, grip, Telemetry.GameTelemetryRuntime.Enabled);
        }

        /// <summary>The app's SourceCoercion.MouseTravelForceProvider: the
        /// force a game sends the slot's virtual wheel along its steering
        /// axis, from -1 to 1.</summary>
        internal double MouseTravelForce(int slotIndex)
            => slotIndex >= 0 && slotIndex < MaxPads ? MouseTravelForceOf(VibrationStates[slotIndex]) : 0;

        /// <summary>The steering force in a slot's feedback, from -1 to 1
        /// with the sign ForceFeedbackState.ComputeWheelSteeringPeak gives
        /// it: positive pushes toward lower axis values. Only a force with
        /// a direction turns the wheel, a constant force or a ramp. A
        /// periodic effect is a shake around where the wheel stands, and
        /// its peak, read as a steady force, would run the wheel to one
        /// lock. A spring, a damper, friction and inertia are no force by
        /// themselves.</summary>
        internal static double MouseTravelForceOf(Vibration feedback)
        {
            if (feedback == null || !feedback.HasDirectionalData) return 0;
            if (feedback.EffectType != FfbEffectTypes.Const && feedback.EffectType != FfbEffectTypes.Ramp) return 0;
            return ForceFeedbackState.ComputeWheelSteeringPeak(feedback, 100) / 32767.0;
        }
    }
}
