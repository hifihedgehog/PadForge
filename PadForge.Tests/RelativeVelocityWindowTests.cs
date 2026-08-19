using System.Diagnostics;
using PadForge.Engine.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #331: the mouse-to-stick lanes publish a windowed velocity instead of
    /// the per-poll delta. These tests pin the contracts the fix rests on:
    /// scale parity with the old code at the 1 kHz default, closure of the
    /// report-rate comb, polling-interval independence, stall-lump averaging,
    /// and release decay.
    /// </summary>
    public class RelativeVelocityWindowTests
    {
        private static readonly long MsTicks = Stopwatch.Frequency / 1000;

        /// <summary>A steady 1000 Hz mouse at the 1 ms default: one count per
        /// poll must read as 1000 counts/s, which through the caller's
        /// scale/1000 factor is numerically the old per-poll value. This is
        /// the fidelity anchor: capable setups feel nothing.</summary>
        [Fact]
        public void SteadyKilohertzStream_MatchesOldPerPollScale()
        {
            var w = new RelativeVelocityWindow();
            long t = MsTicks * 1000;
            for (int i = 0; i < 100; i++)
            {
                w.Add(t, 1f, 0f, 0f);
                t += MsTicks;
            }
            w.CountsPerSecond(out float x, out _, out _);
            Assert.InRange(x, 900f, 1100f);
        }

        /// <summary>A 125 Hz mouse at 1 kHz polling: 8 counts every 8th poll,
        /// zero on the rest. The OLD code read zero on 7 of 8 polls (the
        /// stick snapped to center between reports, the reported stutter).
        /// The window must hold a steady velocity on EVERY poll: the minimum
        /// across a full report period stays above 60% of the mean, and the
        /// mean preserves the true 1000 counts/s.</summary>
        [Fact]
        public void OfficeMouseComb_ClosesWithoutCenterDropouts()
        {
            var w = new RelativeVelocityWindow();
            long t = MsTicks * 1000;
            // Warm-up: two full windows of the 125 Hz pattern.
            for (int i = 0; i < 64; i++)
            {
                w.Add(t, i % 8 == 0 ? 8f : 0f, 0f, 0f);
                t += MsTicks;
            }
            float min = float.MaxValue, max = float.MinValue, sum = 0f;
            const int Samples = 32;
            for (int i = 0; i < Samples; i++)
            {
                w.Add(t, i % 8 == 0 ? 8f : 0f, 0f, 0f);
                w.CountsPerSecond(out float x, out _, out _);
                if (x < min) min = x;
                if (x > max) max = x;
                sum += x;
                t += MsTicks;
            }
            float mean = sum / Samples;
            Assert.InRange(mean, 850f, 1150f);   // average velocity preserved
            Assert.True(min > 0.6f * mean,
                $"velocity dropped to {min} against mean {mean}: the comb is back");
        }

        /// <summary>The same physical motion at a 4 ms polling interval must
        /// read the same velocity as at 1 ms. The old per-poll form scaled
        /// with the interval (4x deflection at 4 ms), which punished exactly
        /// the low-end machines that benefit from a longer interval.</summary>
        [Fact]
        public void PollingInterval_DoesNotChangeTheScale()
        {
            var at1ms = new RelativeVelocityWindow();
            var at4ms = new RelativeVelocityWindow();
            long t1 = MsTicks * 1000, t4 = MsTicks * 1000;

            // 1000 counts/s stream, delivered per each loop's own cadence.
            for (int i = 0; i < 100; i++) { at1ms.Add(t1, 1f, 0f, 0f); t1 += MsTicks; }
            for (int i = 0; i < 25; i++) { at4ms.Add(t4, 4f, 0f, 0f); t4 += 4 * MsTicks; }

            at1ms.CountsPerSecond(out float x1, out _, out _);
            at4ms.CountsPerSecond(out float x4, out _, out _);
            // The boxcar quantizes by whole impulses, so a coarse cadence
            // can hold one extra (7 x 4 = 28 counts in a 25 ms window).
            Assert.InRange(x1, 900f, 1200f);
            Assert.InRange(x4, 900f, 1200f);
        }

        /// <summary>A lump that piled up across a poll stall must read as the
        /// stall's average velocity, not a one-window spike. 100 counts over
        /// a 100 ms stall is 1000 counts/s, not 4000.</summary>
        [Fact]
        public void StallLump_AveragesInsteadOfSpiking()
        {
            var w = new RelativeVelocityWindow();
            long t = MsTicks * 1000;
            for (int i = 0; i < 30; i++) { w.Add(t, 1f, 0f, 0f); t += MsTicks; }
            t += 100 * MsTicks; // the poll thread stalled 100 ms
            w.Add(t, 100f, 0f, 0f);
            w.CountsPerSecond(out float x, out _, out _);
            Assert.InRange(x, 500f, 1500f); // ~1000; the un-scaled spike would be 4000
        }

        /// <summary>Motion stopping must decay the output to zero within one
        /// window, so releasing the mouse re-centers the stick promptly.</summary>
        [Fact]
        public void Idle_DecaysToZeroWithinTheWindow()
        {
            var w = new RelativeVelocityWindow();
            long t = MsTicks * 1000;
            for (int i = 0; i < 50; i++) { w.Add(t, 5f, 0f, 0f); t += MsTicks; }
            for (int i = 0; i < RelativeVelocityWindow.WindowMs + 2; i++)
            {
                w.Add(t, 0f, 0f, 0f);
                t += MsTicks;
            }
            w.CountsPerSecond(out float x, out _, out _);
            Assert.Equal(0f, x);
        }

        /// <summary>All three channels ride one ring: scroll shares the
        /// window with X/Y and resets with them.</summary>
        [Fact]
        public void Reset_ClearsAllChannels()
        {
            var w = new RelativeVelocityWindow();
            long t = MsTicks * 1000;
            for (int i = 0; i < 10; i++) { w.Add(t, 1f, 2f, 3f); t += MsTicks; }
            w.CountsPerSecond(out float x, out float y, out float z);
            Assert.True(x > 0f && y > 0f && z > 0f);
            w.Reset();
            w.CountsPerSecond(out x, out y, out z);
            Assert.Equal(0f, x);
            Assert.Equal(0f, y);
            Assert.Equal(0f, z);
        }
    }
}
