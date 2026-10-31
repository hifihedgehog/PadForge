using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The low-battery edge rule (#293), ported from Gamepad Battery Monitor's
    /// transition test into percent space. The whole point is EDGE-triggered:
    /// one notification per descent, not one per poll.
    /// </summary>
    public class BatteryNotifyTests
    {
        private const int T = 15; // the default threshold

        [Fact]
        public void Fires_OnTheCrossing_NotWhileSitting()
        {
            // Descending: 20 -> 14 fires once.
            var (fire, notified) = InputService.BatteryEdgeDecision(true, 20, false, 14, false, T);
            Assert.True(fire);
            Assert.True(notified);

            // Still low next poll: no second shot.
            (fire, notified) = InputService.BatteryEdgeDecision(true, 14, true, 12, false, T);
            Assert.False(fire);
            Assert.True(notified);
        }

        [Fact]
        public void FirstSight_AlreadyLow_FiresOnce()
        {
            // App start with a dying pad: warn once even with no prior state.
            var (fire, notified) = InputService.BatteryEdgeDecision(false, 0, false, 8, false, T);
            Assert.True(fire);
            Assert.True(notified);
        }

        [Fact]
        public void Charging_Suppresses_AndReArms()
        {
            // Never fire while charging (a wired pad reads as charging).
            var (fire, notified) = InputService.BatteryEdgeDecision(true, 20, false, 10, true, T);
            Assert.False(fire);
            Assert.False(notified); // and the state re-armed

            // Unplugged again while still low: 10 <= T with lastPct now 10 (not
            // above threshold), so no immediate re-fire; the pad must rise
            // clear first. This is the bounce protection.
            (fire, _) = InputService.BatteryEdgeDecision(true, 10, false, 9, false, T);
            Assert.False(fire);
        }

        [Fact]
        public void RisePastMargin_ReArms_ThenNextDescentFires()
        {
            // Notified at 14, then a battery swap pushes it to 90: re-armed.
            var (fire, notified) = InputService.BatteryEdgeDecision(true, 14, true, 90, false, T);
            Assert.False(fire);
            Assert.False(notified);

            // Next descent fires again.
            (fire, _) = InputService.BatteryEdgeDecision(true, 90, false, 15, false, T);
            Assert.True(fire);
        }

        [Fact]
        public void BounceInsideTheMargin_DoesNotReArm()
        {
            // Notified at 15; a wobble to 18 (inside threshold+5) must NOT
            // re-arm, or the pad would buzz on every wobble around the line.
            var (fire, notified) = InputService.BatteryEdgeDecision(true, 15, true, 18, false, T);
            Assert.False(fire);
            Assert.True(notified); // still latched

            (fire, _) = InputService.BatteryEdgeDecision(true, 18, true, 14, false, T);
            Assert.False(fire); // still one notification per descent
        }

        [Fact]
        public void ExactThreshold_CountsAsLow()
        {
            var (fire, _) = InputService.BatteryEdgeDecision(true, 40, false, 15, false, T);
            Assert.True(fire);
        }

        // ── Full charge ──

        [Fact]
        public void Full_FiresOnTheRiseTo100_Once()
        {
            var (fire, notified) = InputService.BatteryFullDecision(true, 95, false, 100);
            Assert.True(fire);
            Assert.True(notified);

            (fire, notified) = InputService.BatteryFullDecision(true, 100, true, 100);
            Assert.False(fire);
            Assert.True(notified);
        }

        [Fact]
        public void Full_FirstSightAt100_IsNoNews()
        {
            // A pad that connects full (fresh off a charger, or an Xbox pad
            // whose level reads full) says nothing, then a wobble stays quiet.
            var (fire, notified) = InputService.BatteryFullDecision(false, 0, false, 100);
            Assert.False(fire);
            Assert.True(notified);
            (fire, _) = InputService.BatteryFullDecision(true, 100, true, 95);
            Assert.False(fire);
            (fire, _) = InputService.BatteryFullDecision(true, 95, true, 100);
            Assert.False(fire);
        }

        [Fact]
        public void Full_ReArmsOnlyBelowTheMargin()
        {
            // One step of a 10% gauge (100 -> 95 -> 100) does not re-arm.
            var (fire, notified) = InputService.BatteryFullDecision(true, 100, true, 95);
            Assert.False(fire);
            Assert.True(notified);

            // A real discharge to 90 re-arms, and the next charge to full fires.
            (fire, notified) = InputService.BatteryFullDecision(true, 95, true, 90);
            Assert.False(fire);
            Assert.False(notified);
            (fire, _) = InputService.BatteryFullDecision(true, 90, false, 100);
            Assert.True(fire);
        }

        // ── Optional repeat of the low notice ──

        private const long Minute = 60_000;

        [Fact]
        public void Repeat_Off_NeverRepeats()
            => Assert.False(InputService.BatteryRepeatDue(true, 10, false, T, 0, 600 * Minute, 0));

        [Fact]
        public void Repeat_DueAfterTheInterval_WhileStillLow()
        {
            Assert.False(InputService.BatteryRepeatDue(true, 10, false, T, 100 * Minute, 104 * Minute, 5));
            Assert.True(InputService.BatteryRepeatDue(true, 10, false, T, 100 * Minute, 105 * Minute, 5));
        }

        [Fact]
        public void Repeat_StopsWhenChargingOrAboveTheThreshold()
        {
            Assert.False(InputService.BatteryRepeatDue(true, 10, true, T, 0, 600 * Minute, 5));
            Assert.False(InputService.BatteryRepeatDue(true, 18, false, T, 0, 600 * Minute, 5));
            Assert.False(InputService.BatteryRepeatDue(false, 10, false, T, 0, 600 * Minute, 5));
        }
    }
}
