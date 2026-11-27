using System.Runtime.InteropServices;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The power throttling opt-out the poll loop sets at its start, and
    /// the PADFORGE_POLL_QOS bench switch.
    /// </summary>
    public class PollQosTests
    {
        [Theory]
        [InlineData(null, "High")]
        [InlineData("", "High")]
        [InlineData("1", "High")]
        [InlineData("on", "High")]
        [InlineData("off", "Default")]
        [InlineData("OFF", "Default")]
        [InlineData("flip", "Flip")]
        public void TheSwitchReadsItsThreeValues(string value, string expected)
            => Assert.Equal(expected, InputManager.ParsePollQos(value).ToString());

        /// <summary>The shape SetProcessInformation takes: version 1, and for
        /// the opt-out execution speed (0x1) and timer resolution (0x4) in
        /// the control mask with the state mask clear. Default hands both
        /// back with both masks clear. The structure is three ULONGs.</summary>
        [Fact]
        public void TheCallTakesTheShapeMicrosoftDocuments()
        {
            Assert.Equal(12, Marshal.SizeOf<InputManager.ProcessPowerThrottlingState>());
            Assert.Equal(4, InputManager.ProcessPowerThrottlingClass);
            var high = InputManager.PowerThrottlingFor(true);
            Assert.Equal(1u, high.Version);
            Assert.Equal(0x5u, high.ControlMask);
            Assert.Equal(0u, high.StateMask);
            var none = InputManager.PowerThrottlingFor(false);
            Assert.Equal(1u, none.Version);
            Assert.Equal(0u, none.ControlMask);
            Assert.Equal(0u, none.StateMask);
        }

        /// <summary>Windows takes the opt-out and reports it back, and takes
        /// the default again. Run on the test host's own process.</summary>
        [Fact]
        public void WindowsReportsTheMasksItWasGiven()
        {
            try
            {
                Assert.True(InputManager.SetPowerThrottling(true, out int error), "error " + error);
                Assert.Equal("5/0", InputManager.PowerThrottlingMasks());
                Assert.True(InputManager.SetPowerThrottling(false, out error), "error " + error);
                Assert.Equal("0/0", InputManager.PowerThrottlingMasks());
            }
            finally { InputManager.SetPowerThrottling(false, out _); }
        }

        [Fact]
        public void TheSwitchIsAmongTheVariablesACopyWithRightsGets()
            => Assert.Contains("PADFORGE_POLL_QOS", PadForge.Services.RightsVariables.PadForgeVariables);
    }
}
