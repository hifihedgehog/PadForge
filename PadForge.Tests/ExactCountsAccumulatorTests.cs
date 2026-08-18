using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    // Locks the exact-counts accumulator step (#324, discussion #319 by
    // vlue-c): the reversal check and the accumulation see the SAME
    // screen-space operand, so steady sub-count motion accumulates across
    // polls instead of being cleared as a phantom reversal, which is the
    // bug that suppressed all gyro-to-mouse movement below ~20 deg/s.
    [Collection("SettingsManagerStatics")]
    public class ExactCountsAccumulatorTests
    {
        [Fact]
        public void SteadySubCountMotion_AccumulatesToTheExactTotal()
        {
            // 10 polls x 0.3 counts = 3.0: exactly three whole counts must
            // come out. Under the old sign-mismatched check this emitted
            // ZERO, because every poll cleared the remainder.
            float acc = 0f;
            int total = 0;
            for (int i = 0; i < 10; i++)
                total += KeyboardMouseVirtualController.StepExactCounts(ref acc, 0.3f);
            Assert.Equal(3, total);
            Assert.Equal(0f, acc, 3);
        }

        [Fact]
        public void GyroSemantics_NegatedOperandAccumulates()
        {
            // The gyro lane passes -raw per poll. Steady positive raw means
            // steady negative screen motion, and it must accumulate: this
            // is the exact configuration the shipped check misread as a
            // reversal every poll.
            float acc = 0f;
            int total = 0;
            for (int i = 0; i < 10; i++)
                total += KeyboardMouseVirtualController.StepExactCounts(ref acc, -0.3f);
            Assert.Equal(-3, total);
        }

        [Fact]
        public void GenuineReversal_ClearsTheRemainder()
        {
            // A real flick back must not first spend the old direction's
            // sub-count motion (the DS4Windows same-operand contract).
            float acc = 0f;
            KeyboardMouseVirtualController.StepExactCounts(ref acc, 0.6f);
            Assert.Equal(0.6f, acc, 3);
            int whole = KeyboardMouseVirtualController.StepExactCounts(ref acc, -0.4f);
            Assert.Equal(0, whole);
            Assert.Equal(-0.4f, acc, 3);
        }

        [Fact]
        public void ZeroInput_ClearsTheRemainder()
        {
            float acc = 0f;
            KeyboardMouseVirtualController.StepExactCounts(ref acc, 0.7f);
            KeyboardMouseVirtualController.StepExactCounts(ref acc, 0f);
            Assert.Equal(0f, acc, 3);
        }

        [Fact]
        public void WholeCountsEmitImmediately_RemainderCarries()
        {
            float acc = 0f;
            int whole = KeyboardMouseVirtualController.StepExactCounts(ref acc, 2.5f);
            Assert.Equal(2, whole);
            Assert.Equal(0.5f, acc, 3);
            whole = KeyboardMouseVirtualController.StepExactCounts(ref acc, 0.6f);
            Assert.Equal(1, whole);
            Assert.Equal(0.1f, acc, 2);
        }
    }
}
