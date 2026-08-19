using System;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Locks the #330 fix: the anti-deadzone floors the PAIR magnitude
    /// radially, direction preserved, instead of flooring each axis alone.
    /// The scalar floor forbade the per-axis band (0, anti), so a slow
    /// full-deflection circle showed empty wedges at every cardinal (the
    /// reporter's circularity trace), the minor axis jumping from -anti to
    /// +anti as it crossed zero. On-axis output stays bit-identical to the
    /// legacy scalar formula, the Axial shape keeps its per-axis contract,
    /// and the round-34 rest guards keep holding, now including the
    /// zero-deadzone hole in ApplySingleDeadZone.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AntiDeadZoneRadialTests
    {
        private const double Dz = 20;
        private const double Adz = 25;

        // ParseDeadZoneShape parses persisted INT strings ("2"), never the
        // enum names, so tests pass the enum directly.
        private static (short X, short Y) Apply(short x, short y, DeadZoneShape shape,
            double dz = Dz, double adz = Adz)
        {
            InputManager.ApplyDeadZoneForTest(ref x, ref y, dz, dz, adz, adz, 0,
                100, 100, 100, 100, null, null, shape);
            return (x, y);
        }

        [Fact]
        public void SlowCircle_HasNoForbiddenBandAtTheCardinals()
        {
            // Full-deflection sweep in 1-degree steps. Before the fix the
            // minor axis could never output inside (0, adz): near a cardinal
            // it jumped the full 2 x adz. After it, the band is populated
            // and successive samples stay close.
            bool bandPopulated = false;
            short prevX = 0, prevY = 0;
            bool first = true;
            int maxJump = 0;
            for (int deg = 0; deg <= 360; deg++)
            {
                double rad = deg * Math.PI / 180.0;
                short x = (short)Math.Round(32767 * Math.Cos(rad));
                short y = (short)Math.Round(32767 * Math.Sin(rad));
                var (ox, oy) = Apply(x, y, DeadZoneShape.ScaledRadial);
                int band = (int)(Adz / 100.0 * 32767);
                if ((Math.Abs((int)ox) > 0 && Math.Abs((int)ox) < band)
                    || (Math.Abs((int)oy) > 0 && Math.Abs((int)oy) < band))
                    bandPopulated = true;
                if (!first)
                {
                    maxJump = Math.Max(maxJump, Math.Abs(ox - prevX));
                    maxJump = Math.Max(maxJump, Math.Abs(oy - prevY));
                }
                prevX = ox; prevY = oy; first = false;
            }
            Assert.True(bandPopulated,
                "no output ever landed inside (0, adz): the per-axis floor is back");
            // The legacy defect jumped 2 x adz = 16383 counts at a cardinal.
            // A continuous ring at 1-degree steps moves a few hundred counts.
            Assert.True(maxJump < 3000,
                $"cardinal crossing is discontinuous (max jump {maxJump})");
        }

        [Theory]
        [InlineData(DeadZoneShape.ScaledRadial)]
        [InlineData(DeadZoneShape.Radial)]
        [InlineData(DeadZoneShape.Hybrid)]
        [InlineData(DeadZoneShape.SlopedScaledAxial)]
        public void OnAxisDeflection_MatchesTheLegacyScalarFormula(DeadZoneShape shape)
        {
            // With the companion at rest the pair magnitude IS the axis
            // magnitude, and ApplyPostDeadZone takes the direct branch:
            // adz + rem * (1 - adz), the exact pre-#330 output.
            var (x, _) = Apply(20000, 0, shape);
            short remOnly = ApplyNoAdz(20000, 0, shape).X;
            double rem = remOnly / 32767.0;
            short expected = (short)Math.Clamp(
                (Adz / 100.0 + rem * (1.0 - Adz / 100.0)) * 32767.0,
                short.MinValue, short.MaxValue);
            Assert.Equal(expected, x);
        }

        private static (short X, short Y) ApplyNoAdz(short x, short y, DeadZoneShape shape)
            => ApplyPair(x, y, shape, adz: 0);

        private static (short X, short Y) ApplyPair(short x, short y, DeadZoneShape shape, double adz)
        {
            InputManager.ApplyDeadZoneForTest(ref x, ref y, Dz, Dz, adz, adz, 0,
                100, 100, 100, 100, null, null, shape);
            return (x, y);
        }

        [Fact]
        public void FullCorner_IsNotReducedByTheFloor()
        {
            var floored = ApplyPair(32767, 32767, DeadZoneShape.ScaledRadial, Adz);
            var plain = ApplyPair(32767, 32767, DeadZoneShape.ScaledRadial, 0);
            Assert.Equal(plain.X, floored.X);
            Assert.Equal(plain.Y, floored.Y);
        }

        [Fact]
        public void AxialShape_KeepsThePerAxisFloor()
        {
            // Axial's contract is per-axis independence: X's output must not
            // change when Y deflects.
            var alone = Apply(20000, 0, DeadZoneShape.Axial);
            var together = Apply(20000, 20000, DeadZoneShape.Axial);
            Assert.Equal(alone.X, together.X);
        }

        [Theory]
        [InlineData(DeadZoneShape.ScaledRadial)]
        [InlineData(DeadZoneShape.Radial)]
        [InlineData(DeadZoneShape.Axial)]
        public void ZeroDeadZone_RestingStick_StillReadsZero(DeadZoneShape shape)
        {
            var (x, y) = Apply(0, 0, shape, dz: 0);
            Assert.Equal(0, x);
            Assert.Equal(0, y);
        }

        [Theory]
        [InlineData(DeadZoneShape.ScaledRadial)]
        [InlineData(DeadZoneShape.Axial)]
        public void RestingStick_WithCurveStartingAboveZero_StillReadsZero(DeadZoneShape shape)
        {
            // The rest guard must test the PRE-curve remap: CurveLut keeps an
            // authored point at x=0 with y > 0, and looking that curve up at
            // rest re-opened the round-34 rest drift when the lookup ran
            // before the guard (2026-08-18 audit).
            var lut = PadForge.Common.CurveLut.GetOrBuild("0.000,0.400;1.000,1.000");
            Assert.True(PadForge.Common.CurveLut.Lookup(lut, 0) > 0.3,
                "fixture check: the curve must start above zero");
            short x = 0, y = 0;
            InputManager.ApplyDeadZoneForTest(ref x, ref y, Dz, Dz, Adz, Adz, 0,
                100, 100, 100, 100, lut, lut, shape);
            Assert.Equal(0, x);
            Assert.Equal(0, y);
        }

        [Fact]
        public void SlopedShape_HasNoSnapAcrossTheDeadzoneBoundary()
        {
            // The floor RAMPS with the raw pair's elliptical distance: a
            // binary inside/outside gate produced a ~6,600-count output snap
            // the moment a deliberate on-axis pull crossed the ellipse on the
            // Sloped shapes, whose center pass-through is by design
            // (2026-08-18 audit). Sweep the on-axis travel and bound the
            // successive delta.
            short prev = 0;
            bool first = true;
            int maxJump = 0;
            for (int raw = 0; raw <= 32767; raw += 64)
            {
                var (x, _) = Apply((short)raw, 0, DeadZoneShape.SlopedScaledAxial);
                if (!first) maxJump = System.Math.Max(maxJump, System.Math.Abs(x - prev));
                prev = x; first = false;
            }
            Assert.True(maxJump < 600,
                $"on-axis travel is discontinuous (max successive jump {maxJump})");
        }

        [Fact]
        public void TriggerRest_WithZeroDeadzoneAndAnti_ReadsZero()
        {
            // The trigger lane's own dz=0 rest hole (#330 audit): a released
            // trigger with anti-deadzone configured shipped a permanent
            // phantom pull of adz% while the preview showed zero.
            Assert.Equal(0, InputManager.ApplyTriggerDeadZone(0, 0, 25, 100));
            // Positive control: a genuine pull still gets the floor.
            Assert.True(InputManager.ApplyTriggerDeadZone(32768, 0, 25, 100) > 16000);
        }

        [Fact]
        public void AntiDeadzoneOver100_IsClampedNotDivergent()
        {
            // The persisted field has no upper bound; past 100 the radial
            // rescale diverged as the pair magnitude shrank. Clamped to 100
            // the output saturates cleanly.
            var (x, _) = Apply(20000, 0, DeadZoneShape.ScaledRadial, adz: 150);
            Assert.Equal(32767, x);
        }

        // ── Per-source lane (SourceCoercion, Steam Circle rows) ─────────────

        private static MappingSource CircleSrc(string descriptor) => new()
        {
            Descriptor = descriptor,
            ParamStickDeadZoneShape = 2,
            ParamStickDeadZoneInner = 0.10,
            ParamAntiDeadzone = 0.25,
        };

        private static CustomInputState PairState(int ax0, int ax1)
        {
            var s = new CustomInputState();
            s.Axis[0] = ax0;
            s.Axis[1] = ax1;
            return s;
        }

        [Fact]
        public void SteamCircleRow_MinorAxisPassesThroughTheBandNearACardinal()
        {
            // Major axis at full deflection, minor axis at 3%: with the
            // scalar floor the minor row output jumped to at least 0.25.
            // Radially the pair magnitude is ~1, so the floor adds almost
            // nothing and the minor axis stays small.
            var state = PairState(65535, 32768 + 1000);
            float minor = SourceCoercion.EvaluateForBipolarAxisTarget(state, CircleSrc("Axis 1"));
            Assert.True(Math.Abs(minor) < 0.20f,
                $"minor axis jumped to {minor}: the scalar floor is back on Steam Circle rows");
            Assert.True(Math.Abs(minor) > 0f, "minor axis should still register");
        }

        [Fact]
        public void SteamCircleRow_OnAxis_KeepsTheScalarFloorValue()
        {
            // Companion at rest: radial reduces to the legacy scalar formula,
            // asserted against the HAND-COMPUTED value rather than a second
            // call to the same function (the original assertion compared the
            // function to itself and was vacuous, 2026-08-18 audit).
            // Half deflection, inner 0.10: shaped = (0.5 - 0.1) / 0.9 = 0.4444,
            // floored = 0.25 + 0.4444 * 0.75 = 0.5833.
            var state = PairState(32768 + 16384, 32768);
            float major = SourceCoercion.EvaluateForBipolarAxisTarget(state, CircleSrc("Axis 0"));
            Assert.Equal(0.5833f, major, 3);
        }

        [Fact]
        public void SteamCircleRow_AsymmetricSensitivity_KeepsTheScalarFloor()
        {
            // The radial pair path requires symmetric feel: the primary is
            // scaled by PerSourceSensitivity before the floor while the
            // companion read is not, so a row with non-unit sensitivity
            // falls back to the scalar floor rather than computing a wrong
            // pair magnitude (2026-08-18 audit).
            var src = CircleSrc("Axis 1");
            src.Sensitivity = 2.0;
            var state = PairState(65535, 32768 + 1000);
            float minor = SourceCoercion.EvaluateForBipolarAxisTarget(state, src);
            Assert.True(System.Math.Abs(minor) > 0.20f,
                $"non-unit sensitivity must keep the scalar floor (got {minor})");
        }

        [Fact]
        public void NonCircleRow_KeepsTheScalarFloor()
        {
            // Shape 0 rows carry single-axis semantics (steering imports):
            // the floor must ignore the companion axis entirely.
            var src = new MappingSource { Descriptor = "Axis 0", ParamAntiDeadzone = 0.25 };
            var alone = SourceCoercion.EvaluateForBipolarAxisTarget(PairState(49152, 32768), src);
            var together = SourceCoercion.EvaluateForBipolarAxisTarget(PairState(49152, 65535), src);
            Assert.Equal(alone, together, 6);
        }
    }
}
