using System.Collections.Generic;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The PADFORGE_POLL_PIN bench switch: its values, and the core it
    /// holds the poll thread to.
    /// </summary>
    public class PollPinTests
    {
        [Theory]
        [InlineData(null, "Free")]
        [InlineData("", "Free")]
        [InlineData("0", "Free")]
        [InlineData("2", "Free")]
        [InlineData("1", "Pinned")]
        [InlineData("flip", "Flip")]
        [InlineData("FLIP", "Flip")]
        public void TheSwitchReadsItsThreeValues(string value, string expected)
            => Assert.Equal(expected, InputManager.ParsePollPin(value).ToString());

        private static List<(int, byte)> Cpus(params byte[] classes)
        {
            var cpus = new List<(int, byte)>();
            for (int i = 0; i < classes.Length; i++) cpus.Add((i, classes[i]));
            return cpus;
        }

        [Fact]
        public void TheCoreIsNeverProcessorZero_AndTheHighestOfOneClass()
        {
            Assert.Equal(7, InputManager.ChoosePollCore(0xFF, Cpus(0, 0, 0, 0, 0, 0, 0, 0)));
            Assert.Equal(-1, InputManager.ChoosePollCore(0x1, Cpus(0, 0)));
            Assert.Equal(1, InputManager.ChoosePollCore(0x3, Cpus(0, 0)));
        }

        [Fact]
        public void TheCoreIsOfTheHighestPerformanceClass_InsideTheProcessMask()
        {
            // Processors 0 to 3 performance (class 1), 4 to 7 efficient (0).
            var hybrid = Cpus(1, 1, 1, 1, 0, 0, 0, 0);
            Assert.Equal(3, InputManager.ChoosePollCore(0xFF, hybrid));
            // The mask leaves out 2 and 3: 1 is the last performance core.
            Assert.Equal(1, InputManager.ChoosePollCore(0xF3, hybrid));
            // No performance core but 0 in the mask: an efficient one.
            Assert.Equal(7, InputManager.ChoosePollCore(0xF1, hybrid));
        }
    }
}
