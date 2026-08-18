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
            // The round-34 guard's last hole: with deadZone == 0 the strict
            // "magnitude < dzNorm" in ApplySingleDeadZone never fired, so a
            // resting Axial stick (and the trigger lane) emitted the floor.
            var (x, y) = Apply(0, 0, shape, dz: 0);
            Assert.Equal(0, x);
            Assert.Equal(0, y);
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
            // Companion at rest: radial reduces to the legacy scalar formula.
            var state = PairState(65535, 32768);
            float major = SourceCoercion.EvaluateForBipolarAxisTarget(state, CircleSrc("Axis 0"));
            var scalarOnly = new MappingSource
            {
                Descriptor = "Axis 0",
                ParamStickDeadZoneShape = 2,
                ParamStickDeadZoneInner = 0.10,
                ParamAntiDeadzone = 0.25,
            };
            float again = SourceCoercion.EvaluateForBipolarAxisTarget(PairState(65535, 32768), scalarOnly);
            Assert.Equal(again, major, 6);
            Assert.True(major > 0.99f, $"full on-axis deflection should stay full (got {major})");
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
