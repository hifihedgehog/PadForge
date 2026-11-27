using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The headset sweep walked every HID interface and every present device
    /// node of every class every 3 s with no tracker anywhere. It runs on the
    /// interval only while it has work in flight, and otherwise after a
    /// device change.
    /// </summary>
    public class HeadsetSweepGateTests
    {
        [Fact]
        public void WithNothingInFlight_TheSweepWaitsForADeviceChange()
        {
            Assert.False(InputManager.HeadsetSweepDue(running: false, now: 10_000, nextAt: 0, keepSweeping: false, changed: () => false));
            Assert.True(InputManager.HeadsetSweepDue(running: false, now: 10_000, nextAt: 0, keepSweeping: false, changed: () => true));
        }

        [Fact]
        public void WorkInFlight_KeepsTheInterval_AndAsksNoGate()
        {
            bool asked = false;
            Assert.True(InputManager.HeadsetSweepDue(running: false, now: 10_000, nextAt: 10_000, keepSweeping: true,
                changed: () => { asked = true; return false; }));
            Assert.False(asked);
            Assert.False(InputManager.HeadsetSweepDue(running: false, now: 9_999, nextAt: 10_000, keepSweeping: true, changed: () => true));
            Assert.False(InputManager.HeadsetSweepDue(running: true, now: 10_000, nextAt: 0, keepSweeping: true, changed: () => true));
        }

        /// <summary>The sweep check runs on every device pass (2 s, 5 s with no
        /// slot online), and a lambda that captured a local made a closure and
        /// a delegate on each. The gate's ask is made once.</summary>
        [Fact]
        public void TheHeadsetGateAskIsMadeOnce()
        {
            var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "PadForge.sln"))) d = d.Parent;
            Assert.NotNull(d);
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, "PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs"));
            Assert.Contains("_headsetGateAsk ??= () => _headsetScan.Due(Environment.TickCount64)", source);
            Assert.DoesNotContain("() => _headsetScan.Due(now)", source);
        }
    }
}
