using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #9 surfaces "Touchpad N Finger M Pressure" in the mapping source picker
    /// beside the existing X / Y / Down entries. The engine coercion already
    /// existed (SourceCoercion.TryParseTouchpadAxis, axisOffset 2), so this locks
    /// in that the newly-surfaced descriptor feeds a real value end to end.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class TouchpadPressureSourceTests
    {
        private static CustomInputState StateWithPressure(float pressure, bool down = true)
        {
            var s = new CustomInputState();
            var pad = new TouchpadInputState(1);
            pad.FingerDown[0] = down;
            pad.FingerPressure[0] = pressure;
            s.Touchpads = new[] { pad };
            return s;
        }

        [Fact]
        public void Pressure_ReadsAsUnipolarTrigger()
        {
            var src = new MappingSource { Descriptor = "Touchpad 0 Finger 0 Pressure" };
            Assert.Equal(0.75f, SourceCoercion.EvaluateForTriggerTarget(StateWithPressure(0.75f), src), 3);
            // Lifted finger reads 0 regardless of the stale pressure sample.
            Assert.Equal(0f, SourceCoercion.EvaluateForTriggerTarget(StateWithPressure(0.9f, down: false), src), 3);
        }

        [Fact]
        public void Pressure_ReadsAsBipolarAbsolute()
        {
            // Absolute bipolar read keeps pressure unipolar [0..1]; it is not a
            // signed axis, so it is not recentered around 0.
            var src = new MappingSource { Descriptor = "Touchpad 0 Finger 0 Pressure" };
            Assert.Equal(0.5f, SourceCoercion.EvaluateForBipolarAxisTarget(StateWithPressure(0.5f), src), 3);
        }
    }
}
