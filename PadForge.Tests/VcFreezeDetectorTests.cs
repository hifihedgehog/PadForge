using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The freeze detector's trip/clear contract.
    ///
    /// <para>The bug it hunts (OPEN, root cause unknown): the HM virtual
    /// controller stays present in joy.cpl while its output freezes at
    /// rest, PadForge's preview stays live, and only a VC toggle or app
    /// restart clears it. The detector must trip exactly once per episode
    /// on sustained outputs-changing-with-static-driver, never on an idle
    /// pad, never on a healthy one, and re-arm when the driver moves.</para></summary>
    public class VcFreezeDetectorTests
    {
        [Fact]
        public void HealthySessionNeverTrips()
        {
            var d = new VcFreezeDetector();
            for (int i = 0; i < 50; i++)
                Assert.False(d.Observe(outputsChanging: true, driverChanged: true));
            Assert.False(d.IsFrozen);
        }

        [Fact]
        public void IdlePadNeverTrips()
        {
            var d = new VcFreezeDetector();
            for (int i = 0; i < 50; i++)
                Assert.False(d.Observe(outputsChanging: false, driverChanged: false));
            Assert.False(d.IsFrozen);
        }

        /// <summary>THE BUG'S SIGNATURE: outputs moving, driver static,
        /// sustained. Trips exactly once, on the third bad tick.</summary>
        [Fact]
        public void SustainedDivergenceTripsOnceOnTheThirdTick()
        {
            var d = new VcFreezeDetector();
            Assert.False(d.Observe(true, false));
            Assert.False(d.Observe(true, false));
            Assert.True(d.Observe(true, false));
            Assert.True(d.IsFrozen);
            // Level, not edge, afterwards: no repeat alerts.
            Assert.False(d.Observe(true, false));
            Assert.False(d.Observe(true, false));
        }

        /// <summary>A one-tick hiccup (driver briefly quiet under load) must
        /// not alarm: any driver movement resets the count.</summary>
        [Fact]
        public void SingleBadTickIsForgivenByDriverMovement()
        {
            var d = new VcFreezeDetector();
            Assert.False(d.Observe(true, false));
            Assert.False(d.Observe(true, false));
            Assert.False(d.Observe(true, true));   // driver moved: reset
            Assert.False(d.Observe(true, false));
            Assert.False(d.Observe(true, false));
            Assert.False(d.IsFrozen);
        }

        /// <summary>The user pausing input mid-episode must not clear the
        /// episode (the VC is still frozen) and must not re-fire the alert
        /// when they resume.</summary>
        [Fact]
        public void PauseDuringEpisodeNeitherClearsNorRefires()
        {
            var d = new VcFreezeDetector();
            d.Observe(true, false); d.Observe(true, false);
            Assert.True(d.Observe(true, false));
            Assert.True(d.IsFrozen);

            Assert.False(d.Observe(false, false));  // user pauses
            Assert.True(d.IsFrozen);
            Assert.False(d.Observe(true, false));   // resumes: still one episode
            Assert.True(d.IsFrozen);
        }

        /// <summary>The user's workaround (VC toggle) makes the driver move
        /// again: the episode clears and the detector re-arms for the next
        /// one, which must trip again after another sustained divergence.</summary>
        [Fact]
        public void DriverMovementClearsAndRearms()
        {
            var d = new VcFreezeDetector();
            d.Observe(true, false); d.Observe(true, false);
            Assert.True(d.Observe(true, false));

            Assert.False(d.Observe(true, true));    // toggle healed it
            Assert.False(d.IsFrozen);

            d.Observe(true, false); d.Observe(true, false);
            Assert.True(d.Observe(true, false));    // next episode trips again
        }
    }
}
