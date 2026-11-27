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
    }
}
