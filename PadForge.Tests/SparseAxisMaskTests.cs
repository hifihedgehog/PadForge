using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #277: the PS Move Navigation controller's only analog trigger read at
    /// rest forever.
    ///
    /// <para>SDL binds a virtual gamepad's axes to SEQUENTIAL joystick
    /// indices, one per PRESENT bit of the descriptor's axis_mask, so an
    /// index is a count of present lower bits and not the bit position. A
    /// CONTIGUOUS mask hides the distinction, which is why the DS3 (0x3F)
    /// survived hand-written indices while the Nav (0x13) did not: its
    /// LEFT_TRIGGER is bit 4 but only the third present axis, so the
    /// pressure was written to index 4 while the gamepad trigger read index
    /// 2. Nothing surfaced index 4 either, since the generic extra-axis
    /// seam starts at 6.</para>
    ///
    /// <para>The button block in the same method already documented this
    /// rule and applied it correctly. These pin the axis half so the next
    /// sparse mask cannot repeat it.</para>
    /// </summary>
    public class SparseAxisMaskTests
    {
        // SDL_GamepadAxis bit positions.
        private const int LeftX = 0, LeftY = 1, RightX = 2, RightY = 3;
        private const int LeftTrigger = 4, RightTrigger = 5;

        private const uint Ds3Mask = 0x3Fu;   // LX LY RX RY L2 R2, contiguous
        private const uint NavMask = 0x13u;   // LX LY L2, sparse

        /// <summary>THE REGRESSION. The Nav's single analog trigger is L2,
        /// and with only LX/LY below it in the mask it binds to index 2.
        /// Writing 4, the bit position, put it on an axis nothing maps.</summary>
        [Fact]
        public void NavLeftTrigger_BindsToIndexTwo_NotItsBitPosition()
        {
            Assert.Equal(2, Ds3DirectService.SeqAxis(NavMask, LeftTrigger));
            Assert.NotEqual(LeftTrigger, Ds3DirectService.SeqAxis(NavMask, LeftTrigger));
        }

        /// <summary>The Nav's sticks are unaffected: both sit below the gap,
        /// so their indices already matched their bit positions.</summary>
        [Fact]
        public void NavSticks_KeepTheirIndices()
        {
            Assert.Equal(0, Ds3DirectService.SeqAxis(NavMask, LeftX));
            Assert.Equal(1, Ds3DirectService.SeqAxis(NavMask, LeftY));
        }

        /// <summary>The DS3 is untouched by the change: a contiguous mask
        /// makes index and bit position identical, so every hand-written
        /// index it shipped with is reproduced exactly.</summary>
        [Theory]
        [InlineData(LeftX, 0)]
        [InlineData(LeftY, 1)]
        [InlineData(RightX, 2)]
        [InlineData(RightY, 3)]
        [InlineData(LeftTrigger, 4)]
        [InlineData(RightTrigger, 5)]
        public void Ds3ContiguousMask_IndexEqualsBitPosition(int bit, int expected)
            => Assert.Equal(expected, Ds3DirectService.SeqAxis(Ds3Mask, bit));

        /// <summary>The Move wand proves the rule independently and is the
        /// hardware-validated case: mask 0x20 carries RIGHT_TRIGGER alone,
        /// so its one trigger binds to index 0, which is what
        /// PsMoveDirectService writes.</summary>
        [Fact]
        public void MoveWandSingleTrigger_BindsToIndexZero()
            => Assert.Equal(0, Ds3DirectService.SeqAxis(0x20u, RightTrigger));

        /// <summary>The lowest present bit is always index 0, whatever it
        /// is. A mask's first axis cannot land anywhere else.</summary>
        [Fact]
        public void LowestPresentBit_IsAlwaysIndexZero()
        {
            Assert.Equal(0, Ds3DirectService.SeqAxis(0x13u, LeftX));
            Assert.Equal(0, Ds3DirectService.SeqAxis(0x20u, RightTrigger));
            Assert.Equal(0, Ds3DirectService.SeqAxis(0x30u, LeftTrigger));
        }
    }
}
