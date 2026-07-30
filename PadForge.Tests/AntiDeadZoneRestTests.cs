using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Round-34 guard: a stick inside its deadzone reads zero, whatever the
    /// anti-deadzone is set to.
    ///
    /// <para>Anti-deadzone exists to jump the output past a GAME's own
    /// internal deadzone once the user actually deflects the stick. Applied
    /// at rest it is a drift bug, and worse than a constant offset: the sign
    /// comes from the raw sensor reading, so a resting stick jittering either
    /// side of centre oscillated between +anti and -anti every tick. The
    /// Axial path never had this because it returns 0 inside the deadzone
    /// unconditionally; the shaped paths reached the post-deadzone stage
    /// with remapped == 0 and an anti-deadzone floor that ignored it.</para>
    /// </summary>
    public class AntiDeadZoneRestTests
    {
        private const double Dz = 20;      // 20% deadzone
        private const double Adz = 25;     // 25% anti-deadzone

        private static (short X, short Y) Apply(short x, short y, string shape)
        {
            var s = InputManager.ParseDeadZoneShape(shape);
            InputManager.ApplyDeadZoneForTest(ref x, ref y, Dz, Dz, Adz, Adz, 0,
                100, 100, 100, 100, null, null, s);
            return (x, y);
        }

        public static TheoryData<string> Shapes => new()
        {
            "", "Radial", "ScaledRadial", "SlopedAxial", "SlopedScaledAxial", "Hybrid", "Axial",
        };

        [Theory]
        [MemberData(nameof(Shapes))]
        public void RestingStick_ReadsZero_NotTheAntiDeadZone(string shape)
        {
            var (x, y) = Apply(0, 0, shape);
            Assert.Equal(0, x);
            Assert.Equal(0, y);
        }

        [Theory]
        [MemberData(nameof(Shapes))]
        public void NoiseInsideTheDeadZone_DoesNotFlipSign(string shape)
        {
            // The observable failure: one tick reads a few counts positive,
            // the next a few counts negative, and the output swung the full
            // 2 x anti-deadzone. Both must be zero.
            var (px, py) = Apply(600, -600, shape);      // ~1.8% deflection
            var (nx, ny) = Apply(-600, 600, shape);
            Assert.Equal(0, px);
            Assert.Equal(0, py);
            Assert.Equal(0, nx);
            Assert.Equal(0, ny);
        }

        [Theory]
        [MemberData(nameof(Shapes))]
        public void RealDeflection_StillGetsTheAntiDeadZoneFloor(string shape)
        {
            // The positive control: past the deadzone the floor must still
            // apply, or this "fix" would have removed the feature.
            var (x, _) = Apply(20000, 0, shape);        // ~61% deflection
            Assert.True(x > (short)(0.25 * 32767),
                $"{shape}: anti-deadzone floor missing past the deadzone (x={x})");
        }

        [Theory]
        [MemberData(nameof(Shapes))]
        public void NegativeDeflection_IsSymmetric(string shape)
        {
            var (px, _) = Apply(20000, 0, shape);
            var (nx, _) = Apply(-20000, 0, shape);
            Assert.True(nx < 0, $"{shape}: negative deflection lost its sign");
            Assert.Equal(px, (short)-nx);
        }
    }
}
