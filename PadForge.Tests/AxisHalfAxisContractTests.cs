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
    [Collection("SettingsManagerStatics")]
    public class AxisHalfAxisContractTests
    {
        private const int Center = 32768;
        private const int FullUp = 65535;   // upper half fully deflected
        private const int FullDown = 0;     // lower half fully deflected
        private const int HalfUp = Center + 16384;
        private const int HalfDown = Center - 16384;

        private static MappingSource Src(bool half = true, bool invert = false, bool bidir = false,
            bool invertOutput = false) => new()
        {
            Descriptor = "Axis 6",
            HalfAxis = half,
            Invert = invert,
            Bidirectional = bidir,
            InvertOutput = invertOutput,
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

        // ── InvertOutput: the output flip for sources whose Invert is
        //    already spoken for as the half selector (audit 2026-07-14) ──

        [Theory]
        [InlineData(HalfUp, -0.5f)]   // upper half still selected, result negated
        [InlineData(FullUp, -1f)]
        [InlineData(HalfDown, 0f)]    // the other half stays silent
        [InlineData(Center, 0f)]
        public void AxisTarget_InvertOutput_NegatesWithoutMovingTheHalf(int av, float expected)
        {
            // Invert stays false, so the read still selects the UPPER half.
            // InvertOutput flips the result. Before this existed the only way
            // to get the flip was to set Invert, which silently switched the
            // read to the lower half: a Workshop scroll-up on a trigger axis
            // ended up reading the half of the axis the trigger never enters.
            float v = SourceCoercion.EvaluateForBipolarAxisTarget(
                State(av), Src(invertOutput: true));
            Assert.Equal(expected, v, precision: 3);
        }

        [Theory]
        [InlineData(HalfDown, -0.5f)] // lower half selected by Invert, then negated
        [InlineData(HalfUp, 0f)]
        public void AxisTarget_InvertAndInvertOutput_AreIndependent(int av, float expected)
        {
            // The two roles compose: Invert picks the half, InvertOutput signs
            // the result. That combination was inexpressible before.
            float v = SourceCoercion.EvaluateForBipolarAxisTarget(
                State(av), Src(invert: true, invertOutput: true));
            Assert.Equal(expected, v, precision: 3);
        }

        [Fact]
        public void AxisTarget_InvertOutput_IsIgnoredWhereInvertIsAlreadyTheOutputFlip()
        {
            // Same-window negative control. On a FULL-range read Invert is not
            // consumed as a selector, so it is already the output flip and
            // InvertOutput must stay inert: otherwise every non-half source
            // would gain a second, silent negation.
            var plain = Src(half: false, invertOutput: true);
            float v = SourceCoercion.EvaluateForBipolarAxisTarget(State(FullUp), plain);
            Assert.Equal(1f, v, precision: 3);
        }

        // ── Producer closure: the legacy migrator is the OTHER writer ──
        //
        // SourceCoercion.InvertConsumedByHalfAxisRead is the one definition of
        // "Invert is spoken for on this source", and every producer that wants
        // an output flip has to ask it. The Workshop translator was one such
        // producer. MappingSetMigrator's bipolar negative leg is the other, and
        // it assigned polarity straight onto Invert, so a legacy paired-axis
        // field whose Neg descriptor was a half-axis came out wrong in BOTH
        // halves of its job: it read the opposite half AND failed to negate.

        [Fact]
        public void Migrator_NegLegOnAHalfAxis_NegatesInsteadOfMovingTheHalf()
        {
            var ps = new PadSetting
            {
                LeftThumbAxisX = "Axis 6",
                LeftThumbAxisXNeg = "HAxis 7",
            };

            var ms = MappingSetMigrator.BuildFromLegacy(0, new[]
            {
                (DeviceGuid: "11111111-1111-1111-1111-111111111111",
                 PadSetting: ps,
                 IsGamepadEligible: true),
            });

            var row = Assert.Single(ms.Rows, r => r.Target == "LeftThumbAxisX");
            var neg = Assert.Single(row.Sources, s => s.Descriptor == "Axis 7");

            // The half selection the descriptor's H prefix asked for survives...
            Assert.True(neg.HalfAxis);
            Assert.False(neg.Invert);
            // ...and the Neg leg's sign flip lands where the read will honour it.
            Assert.True(neg.InvertOutput);

            // The contract that matters: upper-half deflection on Axis 7 has to
            // drive the target NEGATIVE. Assigning Invert produced +1 from the
            // wrong half instead.
            var st = new CustomInputState();
            st.Axis[7] = FullUp;
            Assert.Equal(-1f, SourceCoercion.EvaluateForBipolarAxisTarget(st, neg), precision: 3);
        }

        [Fact]
        public void Migrator_NegLegOnAFullRangeSource_StillUsesInvert()
        {
            // Same-window negative control. A non-half source's Invert is NOT
            // consumed by the read, so it stays the output flip and InvertOutput
            // must stay off: the migrator has to ask, not assume either way.
            var ps = new PadSetting
            {
                LeftThumbAxisX = "Axis 6",
                LeftThumbAxisXNeg = "Button 3",
            };

            var ms = MappingSetMigrator.BuildFromLegacy(0, new[]
            {
                (DeviceGuid: "11111111-1111-1111-1111-111111111111",
                 PadSetting: ps,
                 IsGamepadEligible: true),
            });

            var row = Assert.Single(ms.Rows, r => r.Target == "LeftThumbAxisX");
            var neg = Assert.Single(row.Sources, s => s.Descriptor == "Button 3");
            Assert.True(neg.Invert);
            Assert.False(neg.InvertOutput);
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
