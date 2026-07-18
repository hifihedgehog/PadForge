using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Engine coverage for the translator v18 channels: the curve/range
    /// shaping on the unipolar trigger tail, the anti-deadzone floor, the
    /// per-source AND gate (MappingSource.GateDescriptor), and the new
    /// touchpad windows (Upper / Lower vertical halves, the diamond
    /// quadrants, the half-composed quadrants, and the windowed click).
    /// Every knob defaults off, so unstamped sources pin pass-through.
    /// </summary>
    public class WorkshopV18ChannelTests
    {
        // ── Curve / range on the unipolar trigger tail ──

        [Fact]
        public void Trigger_CurveExponent_ShapesThePull()
        {
            var s = new CustomInputState();
            s.Axis[2] = 32768; // half pull on the 0..65535 trigger scale
            var src = new MappingSource { Descriptor = "Axis 2", ParamCurveExponent = 2.0 };
            Assert.Equal(0.25f, SourceCoercion.EvaluateForTriggerTarget(s, src), 3);
        }

        [Fact]
        public void Trigger_RangeOuter_ReachesFullPullAtTheRadius()
        {
            var s = new CustomInputState();
            s.Axis[2] = 32768; // 0.5 pull
            var src = new MappingSource { Descriptor = "Axis 2", ParamRangeOuter = 0.5 };
            Assert.Equal(1.0f, SourceCoercion.EvaluateForTriggerTarget(s, src), 3);
        }

        [Fact]
        public void Trigger_Defaults_ArePassThrough()
        {
            var s = new CustomInputState();
            s.Axis[2] = 32768;
            var src = new MappingSource { Descriptor = "Axis 2" };
            Assert.Equal(0.5f, SourceCoercion.EvaluateForTriggerTarget(s, src), 3);
        }

        [Fact]
        public void AntiDeadzone_FloorsTheOutput_AndZeroStaysZero()
        {
            var s = new CustomInputState();
            for (int i = 0; i < 6; i++) s.Axis[i] = 32768;
            var src = new MappingSource { Descriptor = "Axis 0", ParamAntiDeadzone = 0.2 };

            // Rest reads exactly 0: the floor applies to real input only.
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);

            s.Axis[0] = 32768 + 16384; // +0.5 -> 0.2 + 0.8 * 0.5 = 0.6
            Assert.Equal(0.6f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);

            s.Axis[0] = 32768 - 16384; // sign carried
            Assert.Equal(-0.6f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        // ── The per-source AND gate ──

        [Fact]
        public void GateDescriptor_GatesEveryEvaluatorLane()
        {
            var s = new CustomInputState();
            for (int i = 0; i < 6; i++) s.Axis[i] = 32768;
            s.Buttons[0] = true;      // the source
            s.Axis[2] = 65535;        // a full trigger pull source
            var gatedButton = new MappingSource { Descriptor = "Button 0", GateDescriptor = "Button 1" };
            var gatedAxis = new MappingSource { Descriptor = "Axis 2", GateDescriptor = "Button 1" };

            // Gate up: nothing contributes.
            Assert.False(SourceEvaluator.EvaluateForButtonTarget(s, gatedButton, 50, 0, "ButtonA", 0, null, 0));
            Assert.Equal(0f, SourceEvaluator.EvaluateForBipolarAxisTarget(s, gatedButton, 0, "LeftThumbAxisX", 0, null, 0));
            Assert.Equal(0f, SourceEvaluator.EvaluateForTriggerTarget(s, gatedAxis, 0, "LeftTrigger", 0, null, 0));

            // Gate held: the source reads normally.
            s.Buttons[1] = true;
            Assert.True(SourceEvaluator.EvaluateForButtonTarget(s, gatedButton, 50, 0, "ButtonA", 0, null, 0));
            Assert.Equal(1f, SourceEvaluator.EvaluateForBipolarAxisTarget(s, gatedButton, 0, "LeftThumbAxisX", 0, null, 0));
            Assert.Equal(1f, SourceEvaluator.EvaluateForTriggerTarget(s, gatedAxis, 0, "LeftTrigger", 0, null, 0), 3);
        }

        [Fact]
        public void GateDescriptor_Empty_IsUngated()
        {
            var s = new CustomInputState();
            s.Buttons[0] = true;
            var src = new MappingSource { Descriptor = "Button 0" };
            Assert.True(SourceEvaluator.EvaluateForButtonTarget(s, src, 50, 0, "ButtonA", 0, null, 0));
        }

        // ── Touchpad windows (v18) ──

        private static CustomInputState Touch(float x, float y, bool down = true, bool clicked = false)
        {
            var s = new CustomInputState();
            var pad = new TouchpadInputState(1);
            pad.FingerDown[0] = down;
            pad.FingerX[0] = x;
            pad.FingerY[0] = y;
            s.Touchpads = new[] { pad };
            if (clicked && s.Buttons.Length > 16) s.Buttons[16] = true;
            return s;
        }

        private static bool DownReads(string descriptor, float x, float y, bool clicked = false)
            => SourceCoercion.EvaluateForButtonTarget(
                Touch(x, y, down: true, clicked), new MappingSource { Descriptor = descriptor }, 50);

        [Fact]
        public void VerticalHalves_SplitAtHalfHeight()
        {
            Assert.True(DownReads("Touchpad 0 Finger 0 Down Upper", 0.5f, 0.2f));
            Assert.False(DownReads("Touchpad 0 Finger 0 Down Upper", 0.5f, 0.8f));
            Assert.True(DownReads("Touchpad 0 Finger 0 Down Lower", 0.5f, 0.8f));
            // Boundary Y == 0.5 belongs to Lower, the X convention's twin.
            Assert.True(DownReads("Touchpad 0 Finger 0 Down Lower", 0.5f, 0.5f));
            Assert.False(DownReads("Touchpad 0 Finger 0 Down Upper", 0.5f, 0.5f));
        }

        [Fact]
        public void DiamondQuadrants_PartitionByDominantAxis()
        {
            // North: above center with |dy| >= |dx|.
            Assert.True(DownReads("Touchpad 0 Finger 0 Down North", 0.5f, 0.1f));
            Assert.False(DownReads("Touchpad 0 Finger 0 Down North", 0.1f, 0.45f)); // West wins
            Assert.True(DownReads("Touchpad 0 Finger 0 Down West", 0.1f, 0.45f));
            Assert.True(DownReads("Touchpad 0 Finger 0 Down East", 0.9f, 0.55f));
            Assert.True(DownReads("Touchpad 0 Finger 0 Down South", 0.55f, 0.9f));
            // The four windows are exhaustive off-center: exactly one fires.
            int firing = 0;
            foreach (var q in new[] { "North", "South", "East", "West" })
                if (DownReads($"Touchpad 0 Finger 0 Down {q}", 0.62f, 0.31f)) firing++;
            Assert.Equal(1, firing);
        }

        [Fact]
        public void ComposedQuadrants_TestAgainstTheHalfCenter()
        {
            // "North Left": the quadrant runs against the LEFT half's own
            // center (0.25, 0.5), so a touch north of THAT fires.
            Assert.True(DownReads("Touchpad 0 Finger 0 Down North Left", 0.25f, 0.1f));
            // Same spot fails the whole-pad North (it is west of center).
            Assert.False(DownReads("Touchpad 0 Finger 0 Down North", 0.25f, 0.35f));
            // A right-half touch never fires a Left-composed window.
            Assert.False(DownReads("Touchpad 0 Finger 0 Down North Left", 0.75f, 0.1f));
            Assert.True(DownReads("Touchpad 0 Finger 0 Down North Right", 0.75f, 0.1f));
        }

        [Fact]
        public void WindowedClick_RequiresClickAndTheHalf()
        {
            // Clicked with the finger on the right half.
            Assert.True(DownReads("Touchpad 0 Click Right", 0.75f, 0.5f, clicked: true));
            // Clicked but the finger sits left: the Right window fails.
            Assert.False(DownReads("Touchpad 0 Click Right", 0.25f, 0.5f, clicked: true));
            // Finger in the half but no click.
            Assert.False(DownReads("Touchpad 0 Click Right", 0.75f, 0.5f, clicked: false));
        }
    }
}
