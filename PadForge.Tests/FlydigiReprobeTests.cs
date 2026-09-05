using System.IO;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #395, second evidence set: SDL's Flydigi probe failed on two
    /// of three arrivals ("driver = NONE") while Flydigi's own service, one
    /// second later, was answered in 24 ms, and the slot bound to the enhanced
    /// view stayed offline. PadForge asks SDL to probe again after the pad's
    /// first second by changing the hint's string and not its meaning.
    /// </summary>
    public class FlydigiReprobeTests
    {
        [Fact]
        public void AnArrivalOffTheEnhancedBackend_ArmsOneProbe_AfterTheDelay()
        {
            var p = new FlydigiReprobePolicy();
            p.OnArrival(onHidapi: false, nowTicks: 1000);
            Assert.True(p.Armed);
            Assert.False(p.ShouldNudge(1000 + FlydigiReprobePolicy.DelayMs - 1, true, false));   // not yet
            Assert.True(p.ShouldNudge(1000 + FlydigiReprobePolicy.DelayMs, true, false));        // due
            Assert.Equal(1, p.Attempts);
            Assert.False(p.ShouldNudge(1000 + FlydigiReprobePolicy.DelayMs + 10, true, false));  // spaced, not every tick
        }

        [Fact]
        public void AnArrivalOnTheEnhancedBackend_Disarms()
        {
            var p = new FlydigiReprobePolicy();
            p.OnArrival(false, 1000);
            p.OnArrival(true, 1100);          // the enhanced view came up by itself
            Assert.False(p.Armed);
            Assert.False(p.ShouldNudge(10_000, true, true));
        }

        [Fact]
        public void TheEnhancedViewAppearing_OrThePadLeaving_EndsTheProbes()
        {
            var p = new FlydigiReprobePolicy();
            p.OnArrival(false, 0);
            Assert.True(p.ShouldNudge(FlydigiReprobePolicy.DelayMs, true, false));
            Assert.False(p.ShouldNudge(FlydigiReprobePolicy.DelayMs * 2, true, enhancedPresent: true));   // it worked
            Assert.False(p.Armed);
            p.OnArrival(false, 0);
            Assert.False(p.ShouldNudge(FlydigiReprobePolicy.DelayMs, anyFlydigiPresent: false, false));    // it left
            Assert.False(p.Armed);
        }

        [Fact]
        public void ThreeRefusals_AndTheProbesStop()
        {
            var p = new FlydigiReprobePolicy();
            p.OnArrival(false, 0);
            int fired = 0;
            for (long t = 0; t <= FlydigiReprobePolicy.DelayMs * 10; t += 100)
                if (p.ShouldNudge(t, true, false)) fired++;
            Assert.Equal(FlydigiReprobePolicy.MaxAttempts, fired);
            Assert.False(p.Armed);
            // A second arrival of the pad (a replug) starts a fresh budget.
            p.OnArrival(false, 100_000);
            Assert.True(p.ShouldNudge(100_000 + FlydigiReprobePolicy.DelayMs, true, false));
            Assert.Equal(1, p.Attempts);
        }

        [Fact]
        public void ASecondArrivalWhileArmed_DoesNotResetTheClock()
        {
            // The pad's other views arrive within milliseconds of the first.
            var p = new FlydigiReprobePolicy();
            p.OnArrival(false, 0);
            p.OnArrival(false, 50);
            p.OnArrival(false, 90);
            Assert.True(p.ShouldNudge(FlydigiReprobePolicy.DelayMs, true, false));
        }

        [Theory]
        [InlineData("1", "true")]
        [InlineData("true", "1")]
        [InlineData("0", "1")]
        [InlineData(null, "1")]
        public void TheHintString_Alternates_AndAlwaysMeansOn(string current, string expected)
            => Assert.Equal(expected, FlydigiReprobePolicy.NextHintValue(current));

        [Fact]
        public void Step1_ArmsOnArrival_AndNudgesOnlyWhileTheSwitchIsOn()
        {
            string step1 = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs"));
            Assert.Contains("_flydigiReprobe.OnArrival(wrapper.Backend == \"hidapi\", Environment.TickCount64);", step1);
            Assert.Contains("if (_flydigiReprobe.Armed && FlydigiEnhancedProtocolDesired)", step1);
            Assert.Contains("SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_FLYDIGI, next)", step1);
            Assert.Contains("FLYDIGI reprobe attempt=", step1);
            string im = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "InputManager.cs"));
            Assert.Contains("if (accepted) _flydigiHintValue = enabled ? \"1\" : \"0\";", im);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }
    }
}
