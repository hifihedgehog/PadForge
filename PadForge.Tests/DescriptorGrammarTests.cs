using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The 1k identifier-grammar guard (issue #146 hardware bring-up,
    /// 2026-07-01): descriptor families whose names legitimately begin with
    /// 'I' ("IR Pointer X/Y", "IR Brightness") must survive every legacy
    /// I/H prefix normalizer byte-identical. The legacy-to-set migrator once
    /// read "IR Pointer X" as Invert + "R Pointer X" and persisted the
    /// mangled descriptor, killing the pointer while the camera tracked
    /// perfectly. These tests pin the exemption predicate, the migrator
    /// round-trip, and the evaluator's intact handling, plus the legacy
    /// prefix grammar's continued correct operation for real prefixes.
    /// </summary>
    public class DescriptorGrammarTests
    {
        // ─── The exemption predicate itself ───

        [Theory]
        [InlineData("IR Pointer X", true)]
        [InlineData("IR Pointer Y", true)]
        [InlineData("IR Brightness", true)]
        [InlineData("IAxis 2", false)]      // real Invert prefix
        [InlineData("IHAxis 2", false)]     // real Invert+Half prefix
        [InlineData("Axis 2", false)]
        [InlineData("Balance Lean X", false)] // no I/H collision, needs no exemption
        [InlineData("", false)]
        public void IsPrefixExemptDescriptor_MatchesExactlyTheIFamilies(string descriptor, bool expected)
        {
            Assert.Equal(expected, SourceCoercion.IsPrefixExemptDescriptor(descriptor));
        }

        // ─── Migrator round-trip: the layer that corrupted the descriptor ───

        [Theory]
        [InlineData("IR Pointer X")]
        [InlineData("IR Pointer Y")]
        [InlineData("IR Brightness")]
        [InlineData("Balance Total Weight")]
        [InlineData("Balance Lean X")]
        [InlineData("Balance Lean Y")]
        [InlineData("Mouse Position X")]
        [InlineData("Mouse Position Y")]
        [InlineData("Mouse Motion X")]
        [InlineData("Mouse Motion Y")]
        public void BuildFromLegacy_KeepsExemptDescriptorsIntact_NoPhantomInvert(string descriptor)
        {
            var ps = new PadSetting { LeftThumbAxisX = descriptor };
            var ms = MappingSetMigrator.BuildFromLegacy(
                0, new[] { ("11111111-1111-1111-1111-111111111111", ps) });

            var row = ms.Rows.FirstOrDefault(r => r.Target == "LeftThumbAxisX");
            Assert.NotNull(row);
            var src = Assert.Single(row.Sources);
            Assert.Equal(descriptor, src.Descriptor); // byte-identical, leading I intact
            Assert.False(src.Invert);                 // no phantom Invert from the eaten prefix
        }

        [Fact]
        public void BuildFromLegacy_StillStripsRealPrefixes()
        {
            // Regression guard for the guard: the legacy grammar must keep
            // working for descriptors that genuinely carry I/H prefixes.
            var ps = new PadSetting { LeftThumbAxisX = "IAxis 2", LeftThumbAxisY = "IHAxis 3" };
            var ms = MappingSetMigrator.BuildFromLegacy(
                0, new[] { ("11111111-1111-1111-1111-111111111111", ps) });

            var x = ms.Rows.First(r => r.Target == "LeftThumbAxisX").Sources.Single();
            Assert.Equal("Axis 2", x.Descriptor);
            Assert.True(x.Invert);
            Assert.False(x.HalfAxis);

            var y = ms.Rows.First(r => r.Target == "LeftThumbAxisY").Sources.Single();
            Assert.Equal("Axis 3", y.Descriptor);
            Assert.True(y.Invert);
            Assert.True(y.HalfAxis);
        }

        // ─── Evaluator: the intact descriptor reaches the right family ───

        [Fact]
        public void IrPointerX_EvaluatesFromWiiIrState()
        {
            var state = new CustomInputState();
            state.Ir.X = 0.63f;
            state.Ir.Y = -0.4f;
            state.Ir.Detected = true;

            var src = new MappingSource { Descriptor = "IR Pointer X" };
            Assert.Equal(0.63f, SourceCoercion.EvaluateForBipolarAxisTarget(state, src), precision: 5);

            var srcY = new MappingSource { Descriptor = "IR Pointer Y" };
            Assert.Equal(-0.4f, SourceCoercion.EvaluateForBipolarAxisTarget(state, srcY), precision: 5);
        }

        [Fact]
        public void IrPointer_ReadsZeroWhenNoDotDetected()
        {
            // Sight lost: the source relaxes to center rather than sticking.
            var state = new CustomInputState();
            state.Ir.X = 0.9f;
            state.Ir.Detected = false;

            var src = new MappingSource { Descriptor = "IR Pointer X" };
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(state, src), precision: 5);
        }

        [Fact]
        public void MangledDescriptor_EvaluatesDead_ProvingTheFailureMode()
        {
            // The corrupted form the migrator used to emit must evaluate to
            // nothing (this is exactly why the hardware run showed a dead
            // pointer). If this ever starts returning a value, someone added
            // an "R Pointer" family and this suite needs updating.
            var state = new CustomInputState();
            state.Ir.X = 0.63f;
            state.Ir.Detected = true;

            var src = new MappingSource { Descriptor = "R Pointer X" };
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(state, src), precision: 5);
        }
    }
}
