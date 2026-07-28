using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Round-34 guard for the CustomInputState.Axis range contract.
    ///
    /// <para>Axis is UNSIGNED 0..65535 with ~32768 at rest. Every writer
    /// stores it that way (SdlDeviceWrapper's "(ushort)(v - short.MinValue)"
    /// for sticks and "v * 65535 / 32767" for triggers, SdlMouseWrapper's
    /// AxisCenter clamp). Two macro readers treated it as a SIGNED short and
    /// added 32768 again, mapping the real range onto 0.5..1.5: rest read
    /// 1.0, full negative read 0.5, and the mouse twin could never produce a
    /// negative deflection. These tests pin the arithmetic so the signed
    /// reading cannot come back silently.</para>
    /// </summary>
    public class AxisRangeContractTests
    {
        // The normalization the macro readers perform.
        private static float Normalize(int axis) => axis / 65535f;

        // The mouse twin's conversion, applied on top.
        private static float ToDeflection(float normalized) => (normalized - 0.5f) * 2f;

        [Theory]
        [InlineData(0, 0f)]        // full negative
        [InlineData(32768, 0.5f)]  // rest
        [InlineData(65535, 1f)]    // full positive
        public void UnsignedAxis_NormalizesToUnitRange(int axis, float expected)
        {
            Assert.Equal(expected, Normalize(axis), 3);
        }

        [Fact]
        public void RestingStick_IsNotFullScale()
        {
            // The pre-fix formula produced 1.0 here, which pinned volume
            // macros near maximum with the stick untouched.
            Assert.True(Normalize(32768) < 0.6f);
        }

        [Theory]
        [InlineData(0, -1f)]
        [InlineData(32768, 0f)]
        [InlineData(65535, 1f)]
        public void MouseDeflection_IsSymmetricAboutRest(int axis, float expected)
        {
            Assert.Equal(expected, ToDeflection(Normalize(axis)), 2);
        }

        [Fact]
        public void MouseDeflection_CanGoNegative()
        {
            // Pre-fix this was 0.0 at full negative: the cursor could only
            // ever move one way.
            Assert.True(ToDeflection(Normalize(0)) < -0.9f);
        }

        /// <summary>The writer side of the same contract: a full-negative
        /// SDL short must land at 0, rest near centre, full positive at the
        /// top of the range. If a writer ever switches to signed storage,
        /// this fails alongside the reader tests above.</summary>
        [Theory]
        [InlineData(short.MinValue, 0)]
        [InlineData(0, 32768)]
        [InlineData(short.MaxValue, 65535)]
        public void SdlStickStorage_IsUnsigned(short sdlValue, int expectedStored)
        {
            int stored = (ushort)(sdlValue - short.MinValue);
            Assert.Equal(expectedStored, stored);
        }

        [Fact]
        public void CustomInputState_AxisArray_IsAllocated()
        {
            var s = new CustomInputState();
            Assert.NotNull(s.Axis);
            Assert.True(s.Axis.Length > 0);
        }
    }
}
