using System;
using PadForge.Engine.Common.Mapping;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The stick trackball (#291): TickStickCoast, fed post-deadzone
    /// deflections at a simulated 1 kHz. A fling (firm deflection, quick
    /// release) launches a coast at the drag's own speed; a slow guided
    /// return does not; touching the stick catches the ball; the decay is
    /// the touch ball's constant-deceleration physics and reaches exactly
    /// zero.
    /// </summary>
    public class StickCoastTests
    {
        private const long Freq = 10_000_000;   // 100 ns ticks
        private const float Dt = 0.001f;

        private static long T(double seconds) => (long)(seconds * Freq);

        private static int _nextSlot = 4900;
        private static int NewSlot() => System.Threading.Interlocked.Increment(ref _nextSlot);

        private static float TickX(int slot, string dev, float fx, double t,
            bool enabled = true, float glide = 0.90f, float fy = 0f)
            => SourceCoercion.TickStickCoast(slot, dev, fx, fy, Dt, T(t), Freq, enabled, glide).X;

        /// <summary>Hold a deflection for 50 ms of polls ending at
        /// <paramref name="tEnd"/>, then release. Returns the first coast
        /// poll's X counts.</summary>
        private static float Fling(int slot, string dev, float deflection, float glide = 0.90f)
        {
            double t = 0;
            for (int i = 0; i < 50; i++, t += 0.001)
                TickX(slot, dev, deflection, t, glide: glide);
            return TickX(slot, dev, 0f, t, glide: glide);
        }

        [Fact]
        public void AFling_LaunchesAtTheDragSpeed_AndDecays()
        {
            int slot = NewSlot();
            float c1 = Fling(slot, "d", 0.9f);
            // Launch speed = 0.9 x 1200 counts/s, spent over 1 ms.
            Assert.True(c1 > 0f, "the fling produced no coast");
            Assert.True(Math.Abs(c1 - 0.9f * 1200f * Dt) < 0.05f,
                $"launch speed off: {c1} counts in one poll");

            float c2 = TickX(slot, "d", 0f, 0.051);
            Assert.True(c2 > 0f && c2 < c1, $"not decaying: {c1} then {c2}");

            // Runs out to exactly zero, never merely small.
            float last = c2;
            double t = 0.052;
            for (int i = 0; i < 5000 && last != 0f; i++, t += 0.001)
                last = TickX(slot, "d", 0f, t);
            Assert.Equal(0f, last);
        }

        [Fact]
        public void ASlowGuidedReturn_DoesNotCoast()
        {
            int slot = NewSlot();
            double t = 0;
            // Push to 0.9, then walk back to centre over 400 ms: by the
            // release, no sample in the last 100 ms clears the launch gate.
            for (int i = 0; i < 50; i++, t += 0.001)
                TickX(slot, "d", 0.9f, t);
            for (int i = 0; i < 400; i++, t += 0.001)
                TickX(slot, "d", 0.9f * (1f - (i + 1) / 400f), t);
            Assert.Equal(0f, TickX(slot, "d", 0f, t));
        }

        [Fact]
        public void TouchingTheStick_CatchesTheBall()
        {
            int slot = NewSlot();
            Assert.True(Fling(slot, "d", 0.9f) > 0f, "no coast to catch");
            // Any deflection stops it, every reference's unconditional rule.
            Assert.Equal(0f, TickX(slot, "d", 0.05f, 0.051));
            // And release of that small touch does not re-fling (under gate).
            double t = 0.052;
            for (int i = 0; i < 5; i++, t += 0.001)
                TickX(slot, "d", 0.05f, t);
            Assert.Equal(0f, TickX(slot, "d", 0f, t));
        }

        [Fact]
        public void GlideOne_IsFrictionless_UntilCaught()
        {
            int slot = NewSlot();
            float c1 = Fling(slot, "d", 0.5f, glide: 1.00f);
            Assert.True(c1 > 0f);
            float c100 = 0f;
            double t = 0.051;
            for (int i = 0; i < 100; i++, t += 0.001)
                c100 = TickX(slot, "d", 0f, t, glide: 1.00f);
            Assert.Equal(c1, c100, 4);
            Assert.Equal(0f, TickX(slot, "d", 0.3f, t, glide: 1.00f));
        }

        [Fact]
        public void HigherGlide_CoastsLonger()
        {
            static int CoastPolls(float glide)
            {
                int slot = NewSlot();
                float c = Fling(slot, "d", 0.9f, glide);
                int polls = 0;
                double t = 0.051;
                while (c != 0f && polls < 5000) { c = TickX(slot, "d", 0f, t, glide: glide); polls++; t += 0.001; }
                return polls;
            }
            int low = CoastPolls(0.82f);
            int high = CoastPolls(0.95f);
            Assert.True(high > low * 2, $"glide did not lengthen the coast: {low} vs {high} polls");
        }

        [Fact]
        public void Disabled_TicksProduceNothing_AndDropState()
        {
            int slot = NewSlot();
            Assert.True(Fling(slot, "d", 0.9f) > 0f);
            // Turning the setting off mid-coast kills it immediately.
            Assert.Equal(0f, TickX(slot, "d", 0f, 0.051, enabled: false));
            Assert.Equal(0f, TickX(slot, "d", 0f, 0.052));
        }

        [Fact]
        public void ResetTouchMomentum_KillsAStickCoastToo()
        {
            int slot = NewSlot();
            Assert.True(Fling(slot, "d", 0.9f) > 0f);
            SourceCoercion.ResetTouchMomentum();
            Assert.Equal(0f, TickX(slot, "d", 0f, 0.051));
        }

        [Fact]
        public void PerSlotReset_IsScoped()
        {
            int a = NewSlot(), b = NewSlot();
            Assert.True(Fling(a, "d", 0.9f) > 0f);
            Assert.True(Fling(b, "d", 0.9f) > 0f);
            SourceCoercion.ResetTouchMomentumForSlot(a);
            Assert.Equal(0f, TickX(a, "d", 0f, 0.051));
            Assert.True(TickX(b, "d", 0f, 0.051) > 0f, "the per-slot reset leaked");
        }

        [Fact]
        public void ASuppressionGap_StopsTheCoast()
        {
            int slot = NewSlot();
            Assert.True(Fling(slot, "d", 0.9f) > 0f);
            // The pass stops running for 300 ms, then resumes: stopped.
            Assert.Equal(0f, TickX(slot, "d", 0f, 0.351));
        }

        [Fact]
        public void TheCoastIsRateIndependent()
        {
            // The same fling integrated at 1 kHz and at 250 Hz covers the
            // same distance: v -= a*dt with a real dt, the property that
            // separates the proven references from the fixed-rate ports.
            static float TotalDistance(float dt)
            {
                int slot = NewSlot();
                double t = 0;
                for (int i = 0; i < (int)(0.05f / dt); i++, t += dt)
                    SourceCoercion.TickStickCoast(slot, "d", 0.9f, 0f, dt, T(t), Freq, true, 0.90f);
                float total = 0f, c;
                do
                {
                    c = SourceCoercion.TickStickCoast(slot, "d", 0f, 0f, dt, T(t), Freq, true, 0.90f).X;
                    total += c; t += dt;
                } while (c != 0f);
                return total;
            }
            float at1k = TotalDistance(0.001f);
            float at250 = TotalDistance(0.004f);
            Assert.True(at1k > 0f);
            Assert.True(Math.Abs(at1k - at250) / at1k < 0.05f,
                $"distance depends on poll rate: {at1k} vs {at250}");
        }

        [Fact]
        public void ALongSession_FlingsKeepWorking()
        {
            // Owner-reported: momentum dies after 5-10 seconds of use and
            // needs the checkbox toggled. If the engine reproduces it, the
            // ball degrades; if not, the settings plumbing is the suspect.
            int slot = NewSlot();
            double t = 0;
            float[] flings = new float[4];
            for (int f = 0; f < 4; f++)
            {
                // ~3 s of realistic activity between flings: circling
                // (continuously engaged), then idle at centre.
                for (int i = 0; i < 1500; i++, t += 0.001)
                {
                    double a = i * 0.05;
                    SourceCoercion.TickStickCoast(slot, "d",
                        (float)(0.6 * Math.Cos(a)), (float)(0.6 * Math.Sin(a)),
                        Dt, T(t), Freq, true, 0.90f);
                }
                for (int i = 0; i < 1500; i++, t += 0.001)
                    TickX(slot, "d", 0f, t);

                // The fling.
                for (int i = 0; i < 50; i++, t += 0.001)
                    TickX(slot, "d", 0.9f, t);
                flings[f] = TickX(slot, "d", 0f, t); t += 0.001;
                // Let the coast run out before the next round.
                for (int i = 0; i < 1000; i++, t += 0.001)
                    TickX(slot, "d", 0f, t);
            }
            for (int f = 0; f < 4; f++)
                Assert.True(flings[f] > 0f, $"fling {f} died after {f * 4} seconds of session time");
            Assert.Equal(flings[0], flings[3], 3);
        }

        [Fact]
        public void TuningKeys_SurviveTheDescriptorRewrite()
        {
            // The owner-reported dying toggle (#291): ClearMappingDescriptors
            // wipes the raw dict on every descriptor rewrite, preserving only
            // whitelisted per-device TUNING keys. The momentum keys (and
            // #292's gyro-tilt keys, the unlisted sibling) must be on that
            // whitelist, or the setting evaporates seconds after being set.
            var ps = new PadForge.Engine.Data.PadSetting();
            ps.SetRawMapping("KbmMouseMomentum", "1");
            ps.SetRawMapping("KbmMouseMomentumGlide", "0.95");
            ps.SetRawMapping("GyroTiltRange", "30");
            ps.SetRawMapping("GyroTiltInner", "5");
            ps.SetRawMapping("RawBtn3", "Button 5");   // a routing descriptor

            ps.ClearMappingDescriptors();

            Assert.Equal("1", ps.GetRawMapping("KbmMouseMomentum"));
            Assert.Equal("0.95", ps.GetRawMapping("KbmMouseMomentumGlide"));
            Assert.Equal("30", ps.GetRawMapping("GyroTiltRange"));
            Assert.Equal("5", ps.GetRawMapping("GyroTiltInner"));
            Assert.Equal("", ps.GetRawMapping("RawBtn3"));
        }

        [Fact]
        public void DiagonalFling_KeepsItsLine()
        {
            int slot = NewSlot();
            double t = 0;
            for (int i = 0; i < 50; i++, t += 0.001)
                SourceCoercion.TickStickCoast(slot, "d", 0.6f, 0.6f, Dt, T(t), Freq, true, 0.90f);
            var (cx, cy) = SourceCoercion.TickStickCoast(slot, "d", 0f, 0f, Dt, T(t), Freq, true, 0.90f);
            Assert.True(cx > 0f && cy > 0f, "no diagonal coast");
            Assert.Equal(cx, cy, 4);
        }
    }
}
