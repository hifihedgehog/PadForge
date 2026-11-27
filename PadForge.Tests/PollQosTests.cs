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

        /// <summary>With the switch unset, the shipping behavior, the
        /// heartbeat line gains nothing: no mode word and no masks, so no
        /// GetProcessInformation call each line, as the pin word says
        /// nothing with its switch unset.</summary>
        [Fact]
        public void WithTheSwitchUnset_TheHeartbeatSaysNothingOfIt()
        {
            if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("PADFORGE_POLL_QOS"))) return;
            var im = (InputManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InputManager));
            var word = typeof(InputManager).GetMethod("PollQosWord",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.Equal("", (string)word.Invoke(im, null));
        }

        /// <summary>A Windows that refuses the pair (Windows 10 knows only
        /// execution speed) is asked once more for execution speed alone.
        /// One that takes the pair is asked once, the default is never asked
        /// for less, and a refusal of everything is null.</summary>
        [Fact]
        public void AWindowsThatRefusesThePair_IsAskedForExecutionSpeedAlone()
        {
            var asked = new System.Collections.Generic.List<uint>();
            Assert.Equal(0x1u, InputManager.SetOptOut(true, mask => { asked.Add(mask); return mask == 0x1; }));
            Assert.Equal(new uint[] { 0x5, 0x1 }, asked);
            asked.Clear();
            Assert.Equal(0x5u, InputManager.SetOptOut(true, mask => { asked.Add(mask); return true; }));
            Assert.Equal(new uint[] { 0x5 }, asked);
            asked.Clear();
            Assert.Null(InputManager.SetOptOut(true, mask => { asked.Add(mask); return false; }));
            Assert.Equal(new uint[] { 0x5, 0x1 }, asked);
            asked.Clear();
            Assert.Null(InputManager.SetOptOut(false, mask => { asked.Add(mask); return false; }));
            Assert.Equal(new uint[] { 0x0 }, asked);
        }

        /// <summary>The engine's start asks for the opt-out: the poll loop
        /// calls BeginPollPin and BeginPollQos before its first pass, and
        /// BeginPollQos reaches Windows, so the test host's own masks read
        /// the opt-out after it. Set back in a finally.</summary>
        [Fact]
        public void TheEngineStart_AsksForTheOptOut()
        {
            string manager = ExtrasPageTests.RepoText("PadForge.App", "Common", "Input", "InputManager.cs").Replace("\r", "");
            int loop = manager.IndexOf("        private void PollingLoop(int generation, int pollRun)\n", System.StringComparison.Ordinal);
            Assert.True(loop > 0, "no PollingLoop");
            int first = manager.IndexOf("\n            try\n", loop, System.StringComparison.Ordinal);
            Assert.True(first > loop, "PollingLoop has no try");
            string start = manager.Substring(loop, first - loop);
            Assert.Contains("\n            BeginPollPin();\n", start);
            Assert.Contains("\n            BeginPollQos();\n", start);

            if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("PADFORGE_POLL_QOS"))) return;
            var im = (InputManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InputManager));
            try
            {
                Assert.True(InputManager.SetPowerThrottling(false, out int error), "error " + error);
                Assert.Equal("0/0", InputManager.PowerThrottlingMasks());
                typeof(InputManager).GetMethod("BeginPollQos",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(im, null);
                Assert.Equal("5/0", InputManager.PowerThrottlingMasks());
            }
            finally { InputManager.SetPowerThrottling(false, out _); }
        }
    }
}
