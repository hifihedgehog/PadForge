using PadForge.Common.Telemetry;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    // What the engine's "Mouse Travel" input (#600) asks the app for: the road
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

        // True on the polling thread, the only one that may step a slot's
        // runtime (PollingLoop sets it).
        [System.ThreadStatic] private static bool t_onPollThread;

        /// <summary>Marks the calling thread as the polling thread, for the
        /// loop and for a test.</summary>
        internal static void MarkPollThread() => t_onPollThread = true;

        // One key per travel input and the device its source names: every
        // reader that keeps no wheel of its own and reads the same input of
        // the same mouse on a slot reads the same wheel, as one physical
        // wheel is. Another mouse's input is another wheel. A source that
        // names no device folds every device it is read on, as a row's
        // does.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string device, string descriptor), string> s_travelReadKeys = new();

        private static string TravelReadKey(string deviceGuid, string descriptor)
            => s_travelReadKeys.GetOrAdd(((deviceGuid ?? string.Empty).ToLowerInvariant(), descriptor ?? string.Empty),
                static k => "Travel read " + k.device + " " + k.descriptor);

        // The wheels of those readers, one runtime a slot, apart from the
        // rows'. Its frame is one poll pass (BeginTravelReadPass): menus
        // read in Step 2 and macros after Step 3 has stamped the rows' next
        // frame, so on the rows' frames one poll's counts were fresh twice.
        private static readonly PadForge.Engine.Common.Mapping.SourceKindRuntime[] _travelReadRuntimes = new PadForge.Engine.Common.Mapping.SourceKindRuntime[MaxPads];
        private static long s_travelReadPass, s_travelReadTicks;
        private static double s_travelReadDt;

        /// <summary>Starts a poll pass for the travel readers: their frame and
        /// its length. Step 2 calls it before any reader of the pass, on the
        /// polling thread.</summary>
        internal static void BeginTravelReadPass()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            s_travelReadDt = s_travelReadTicks == 0 ? 0 : (now - s_travelReadTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
            s_travelReadTicks = now;
            s_travelReadPass++;
        }

        /// <summary>The app's SourceCoercion.TravelPositionProvider (#600):
        /// the wheel of a travel input that a menu, a macro's trigger, an
        /// activator or an engage input reads, stepped as a row's is on a
        /// runtime of the slot's for these readers, so it moves and presses
        /// the same way. Null off the polling thread or with no slot, where
        /// it reads at rest.</summary>
        internal static double? TravelPosition(CustomInputState state, PadForge.Engine.Data.MappingSource src,
            int slotIndex, string deviceGuid)
        {
            if (!t_onPollThread || slotIndex < 0 || slotIndex >= _travelReadRuntimes.Length) return null;
            var runtime = _travelReadRuntimes[slotIndex] ??= new PadForge.Engine.Common.Mapping.SourceKindRuntime();
            runtime.FrameSeq = s_travelReadPass;
            return PadForge.Engine.Common.Mapping.SourceEvaluator.ReadTravel(state, src, slotIndex,
                TravelReadKey(src.DeviceGuid, src.Descriptor), runtime, s_travelReadDt, deviceGuid);
        }

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
