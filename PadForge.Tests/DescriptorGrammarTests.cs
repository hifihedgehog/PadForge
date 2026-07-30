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
    [Collection("SettingsManagerStatics")]
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
            // Raw aim is scaled by the lineage margin stretch (all three
            // Touchmote variants default pointer_marginsLeftRight = 0.4 and
            // pointer_marginsTopBottom = 0.5, a centered stretch of
            // 1 + 2*margin): x1.8 on X, x2.0 on Y, then clamped.
            var state = new CustomInputState();
            state.Ir.X = 0.3f;
            state.Ir.Y = -0.4f;
            state.Ir.Detected = true;

            var src = new MappingSource { Descriptor = "IR Pointer X" };
            Assert.Equal(0.3f * SourceCoercion.IrMarginStretchX,
                SourceCoercion.EvaluateForBipolarAxisTarget(state, src), precision: 5);

            var srcY = new MappingSource { Descriptor = "IR Pointer Y" };
            Assert.Equal(-0.4f * SourceCoercion.IrMarginStretchY,
                SourceCoercion.EvaluateForBipolarAxisTarget(state, srcY), precision: 5);
        }

        [Fact]
        public void IrPointer_MarginStretch_MatchesTheLineageDefaults()
        {
            // 1 + 2*0.4 on X, 1 + 2*0.5 on Y (Touchmote/Suegrini/Ryochan7
            // WiiTUIO/Properties/Settings.cs defaults, Trihy agrees). The
            // pair-trackable aim cannot reach +/-1 (both LEDs must stay in
            // the camera view), so without this stretch the cursor walls
            // off inside the screen in EVERY pointer mode: the border
            // transform is identity inside its region, so 4:3 stopped at
            // the same wall (owner bench, 2026-07-11: a border in
            // "boundaryless" vanilla Mouse, with the stop rectangle INSIDE
            // the 4:3 pillar geometry).
            Assert.Equal(1.8f, SourceCoercion.IrMarginStretchX, 3);
            Assert.Equal(2.0f, SourceCoercion.IrMarginStretchY, 3);

            // Reachability: aim ~0.556 hits the screen edge on X, well
            // inside the pair-trackable range.
            var state = new CustomInputState();
            state.Ir.X = 0.56f;
            state.Ir.Detected = true;
            var src = new MappingSource { Descriptor = "IR Pointer X" };
            Assert.Equal(1f, SourceCoercion.EvaluateForBipolarAxisTarget(state, src), precision: 2);
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
