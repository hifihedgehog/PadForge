using System;
using System.IO;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The KBM cursor and scroll rate lanes: time-based, poll-rate
    /// independent, DS4Windows-scaled.
    ///
    /// <para>The old spend was 15 px and 3 notches per POLL, which made both
    /// speeds a function of the polling-interval setting and put full
    /// deflection at 15,000 px/s at the 1 ms default. The owner's report:
    /// "I barely nudge it and it almost instantly hits the other side of the
    /// screen." The proven reference is DS4Windows' stick-as-mouse
    /// (velocity x timeDelta, full scale 25 x 48 = 1,200 px/s,
    /// Mapping.cs:829/:5437) and its stick-wheel (one notch per three runs
    /// of a 10 ms gate, ~33 notches/s, GetMouseWheelMapping).</para>
    /// </summary>
    public class KbmMouseRateTests
    {
        [Fact]
        public void FullDeflectionForOneSecondIsTheReferenceRate()
        {
            // 1,200 px/s, DS4Windows' default full-scale cursor velocity.
            float px = KeyboardMouseVirtualController.MouseStickPixels(32767, 1.0f);
            Assert.Equal(1200f, px, 1);
        }

        [Fact]
        public void SpeedIsProportionalToDeflection()
        {
            // The complaint was "barely nudge -> across the screen". A nudge
            // must produce a proportionally small rate: 10% deflection is
            // 120 px/s, a slow drift, not a teleport.
            float px = KeyboardMouseVirtualController.MouseStickPixels(3277, 1.0f);
            Assert.Equal(120f, px, 0);
        }

        [Fact]
        public void TheRateIsPollRateIndependent()
        {
            // The defect, stated as a test: one second of full deflection must
            // move the same distance whether it arrives as 1000 polls of 1 ms
            // or 250 polls of 4 ms. The old per-poll spend moved 4x farther at
            // the faster rate.
            float thousandHz = 0f, twoFiftyHz = 0f;
            for (int i = 0; i < 1000; i++)
                thousandHz += KeyboardMouseVirtualController.MouseStickPixels(32767, 0.001f);
            for (int i = 0; i < 250; i++)
                twoFiftyHz += KeyboardMouseVirtualController.MouseStickPixels(32767, 0.004f);

            Assert.Equal(thousandHz, twoFiftyHz, 1);
            Assert.Equal(1200f, thousandHz, 0);
        }

        [Fact]
        public void ScrollMatchesTheStickWheelReference()
        {
            // ~33 notches/s at full tilt: DS4Windows' one-notch-per-three-runs
            // on a 10 ms gate. The old constant was 3,000/s at the default
            // poll rate, ninety times the reference.
            float notches = KeyboardMouseVirtualController.ScrollStickNotches(32767, 1.0f);
            Assert.Equal(100f / 3f, notches, 1);
        }

        [Theory]
        [InlineData(-1.0, 0.0)]     // negative gap (clock adjust): no motion
        [InlineData(0.0, 0.0)]      // first submit: no motion
        [InlineData(0.001, 0.001)]  // normal 1 kHz frame passes through
        [InlineData(0.004, 0.004)]  // normal 250 Hz frame passes through
        [InlineData(3.0, 0.05)]     // stall (debugger, suspend): capped
        public void SubmitDtIsClampedAgainstStalls(double raw, double expected)
        {
            Assert.Equal((float)expected, KeyboardMouseVirtualController.ClampSubmitDt(raw), 5);
        }

        [Fact]
        public void TheSpendSitesRideTheMeasuredDt()
        {
            // The submit path cannot run headless (SendInput), so the wiring
            // is pinned: both rate lanes must spend through the pure helpers
            // with the measured dt, and no per-poll constant may remain.
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            string src = File.ReadAllText(Path.Combine(d.FullName,
                "PadForge.App", "Common", "Input", "KeyboardMouseVirtualController.cs"));

            Assert.Contains("MouseStickPixels(raw.MouseDeltaX, _submitDt)", src);
            Assert.Contains("ScrollStickNotches(raw.ScrollDelta, _submitDt)", src);
            Assert.Contains("ScrollStickNotches(raw.ScrollDeltaH, _submitDt)", src);
            Assert.DoesNotContain("MouseSensitivity", src);
            Assert.DoesNotContain("ScrollSensitivity", src);
        }
    }
}
