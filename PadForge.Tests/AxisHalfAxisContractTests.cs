using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins the HalfAxis contract for generic "Axis N" sources across all
    /// three read paths: default = upper half only, Invert = lower half
    /// only, Bidirectional = either side. The bool path has always
    /// implemented this selection; the analog reads used to fold BOTH
    /// halves to positive magnitude and leave Invert to the evaluators'
    /// output transforms, so the same row selected one half as a button but
    /// fired on both directions as an axis/trigger source, and
    /// HalfAxis+Invert on a trigger target read full-pressed at rest
    /// (1 - 0 at center). Invert is consumed inside the analog reads as
    /// the half selector, mirroring the Mouse Motion family (issue #154).
    /// </summary>
    public class AxisHalfAxisContractTests
    {
        private const int Center = 32768;
        private const int FullUp = 65535;   // upper half fully deflected
        private const int FullDown = 0;     // lower half fully deflected
        private const int HalfUp = Center + 16384;
        private const int HalfDown = Center - 16384;

        private static MappingSource Src(bool half = true, bool invert = false, bool bidir = false) => new()
        {
            Descriptor = "Axis 6",
            HalfAxis = half,
            Invert = invert,
            Bidirectional = bidir,
        };

        private static CustomInputState State(int axisValue)
        {
            var s = new CustomInputState();
            s.Axis[6] = axisValue;
            return s;
        }

        // ── Trigger target (unipolar) ────────────────────────────────

        [Theory]
        [InlineData(Center, 0f)]      // rest: no pull
        [InlineData(HalfUp, 0.5f)]    // upper half drives the pull
        [InlineData(FullUp, 1f)]
        [InlineData(HalfDown, 0f)]    // lower half is the OTHER half: silent
        [InlineData(FullDown, 0f)]
        public void Trigger_DefaultHalf_UsesUpperOnly(int av, float expected)
        {
            float v = SourceCoercion.EvaluateForTriggerTarget(State(av), Src());
            Assert.Equal(expected, v, precision: 3);
        }

        [Theory]
        [InlineData(Center, 0f)]      // rest: no pull (the old fold+1-raw read 1.0 here)
        [InlineData(HalfDown, 0.5f)]  // Invert selects the lower half
        [InlineData(FullDown, 1f)]
        [InlineData(HalfUp, 0f)]      // upper half silent under Invert
        [InlineData(FullUp, 0f)]
        public void Trigger_InvertedHalf_UsesLowerOnly_AndRestsAtZero(int av, float expected)
        {
            float v = SourceCoercion.EvaluateForTriggerTarget(State(av), Src(invert: true));
            Assert.Equal(expected, v, precision: 3);
        }

        [Theory]
        [InlineData(HalfUp, 0.5f)]
        [InlineData(HalfDown, 0.5f)]
        [InlineData(Center, 0f)]
        public void Trigger_BidirectionalHalf_FiresEitherSide(int av, float expected)
        {
            // Invert must be irrelevant in Bidirectional mode (bool-path parity).
            float v = SourceCoercion.EvaluateForTriggerTarget(State(av), Src(invert: true, bidir: true));
            Assert.Equal(expected, v, precision: 3);
        }

        // ── Axis target (bipolar evaluator, HalfAxis output is 0..1) ─

        [Theory]
        [InlineData(HalfUp, 0.5f)]
        [InlineData(HalfDown, 0f)]
        public void AxisTarget_DefaultHalf_UsesUpperOnly(int av, float expected)
        {
            float v = SourceCoercion.EvaluateForBipolarAxisTarget(State(av), Src());
            Assert.Equal(expected, v, precision: 3);
        }

        [Theory]
        [InlineData(HalfDown, 0.5f)]  // lower half selected, output stays positive (no double-apply)
        [InlineData(HalfUp, 0f)]
        [InlineData(Center, 0f)]
        public void AxisTarget_InvertedHalf_SelectsLowerWithoutNegating(int av, float expected)
        {
            float v = SourceCoercion.EvaluateForBipolarAxisTarget(State(av), Src(invert: true));
            Assert.Equal(expected, v, precision: 3);
        }

        // ── Bool path agreement (the pre-existing contract) ──────────

        [Theory]
        [InlineData(FullUp, false, true)]    // upper deflection, default half: fires
        [InlineData(FullDown, false, false)] // lower deflection, default half: silent
        [InlineData(FullDown, true, true)]   // lower deflection, Invert: fires
        [InlineData(FullUp, true, false)]    // upper deflection, Invert: silent
        public void BoolAndAnalogReads_SelectTheSameHalf(int av, bool invert, bool boolFires)
        {
            var src = Src(invert: invert);
            bool pressed = SourceCoercion.EvaluateForButtonTarget(State(av), src, globalThresholdPercent: 25);
            float pull = SourceCoercion.EvaluateForTriggerTarget(State(av), src);
            Assert.Equal(boolFires, pressed);
            Assert.Equal(boolFires, pull > 0.5f);
        }

        // ── Non-HalfAxis behavior unchanged ──────────────────────────

        [Fact]
        public void PlainAxis_InvertStillNegatesAtTheEvaluator()
        {
            var src = Src(half: false, invert: true);
            float v = SourceCoercion.EvaluateForBipolarAxisTarget(State(HalfUp), src);
            Assert.Equal(-0.5f, v, precision: 3);
        }
    }
}
