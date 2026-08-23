using System;
using System.Linq;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The controller-audio DSP chain (#347): crossfeed, parametric EQ, and
    /// the limiter that stops a gain-positive EQ from clipping the Opus
    /// encoder downstream.
    ///
    /// <para>Crossfeed is a port of Boris Mikhaylov's bs2b as vendored in
    /// openal-soft (core/bs2b.cpp, MIT). The coefficient derivation is checked
    /// here by its own strongest property rather than by re-listing the
    /// constants: the lowpass and the highboost sum to EXACTLY unity at DC,
    /// which is what the g = 1 / (1 - G_hi + G_lo) normalization is for. Any
    /// error in any of the five coefficients moves that sum off 1.</para>
    /// </summary>
    public class MirrorDspTests
    {
        private const int Rate = 48000;

        private static float[] Interleave(float[] l, float[] r)
        {
            var b = new float[l.Length * 2];
            for (int i = 0; i < l.Length; i++) { b[i * 2] = l[i]; b[i * 2 + 1] = r[i]; }
            return b;
        }

        // ───────────────────────── crossfeed ─────────────────────────

        /// <summary>THE COEFFICIENT CHECK. Feed DC and the output converges to
        /// the same DC, on both channels, for every level. This is exact, not
        /// approximate, and it fails if any of the five coefficients or the g
        /// normalization is wrong.</summary>
        [Theory]
        [InlineData(CrossfeedStage.LowCrossfeed)]
        [InlineData(CrossfeedStage.MiddleCrossfeed)]
        [InlineData(CrossfeedStage.HighCrossfeed)]
        [InlineData(CrossfeedStage.LowEasy)]
        [InlineData(CrossfeedStage.MiddleEasy)]
        [InlineData(CrossfeedStage.HighEasy)]
        [InlineData(CrossfeedStage.JanMeier)]
        [InlineData(CrossfeedStage.Bs2bDefault)]
        public void Crossfeed_DcGain_IsUnity(int level)
        {
            var cf = new CrossfeedStage();
            cf.SetParams(level, Rate);

            const int n = 24000;                    // half a second, well past settling
            var buf = new float[n * 2];
            for (int i = 0; i < n * 2; i++) buf[i] = 0.5f;
            cf.Process(buf, n);

            // Settled tail, not the transient at the start.
            Assert.Equal(0.5f, buf[(n - 1) * 2], 4);
            Assert.Equal(0.5f, buf[(n - 1) * 2 + 1], 4);
        }

        /// <summary>A mono signal stays mono. The speaker paths route a mono
        /// downmix, so this is what makes the stage harmless there without a
        /// special case.</summary>
        [Fact]
        public void Crossfeed_MonoIn_StaysMono()
        {
            var cf = new CrossfeedStage();
            cf.SetParams(CrossfeedStage.HighEasy, Rate);

            var rnd = new Random(1234);
            const int n = 4096;
            var mono = Enumerable.Range(0, n).Select(_ => (float)(rnd.NextDouble() * 2 - 1)).ToArray();
            var buf = Interleave(mono, mono);
            cf.Process(buf, n);

            for (int i = 0; i < n; i++)
                Assert.Equal(buf[i * 2], buf[i * 2 + 1], 6);
        }

        /// <summary>The point of the stage: a hard-panned left signal reaches
        /// the right channel. Without this it is an expensive no-op.</summary>
        [Fact]
        public void Crossfeed_HardPanned_BleedsIntoTheOtherEar()
        {
            var cf = new CrossfeedStage();
            cf.SetParams(CrossfeedStage.HighEasy, Rate);

            const int n = 4096;
            var buf = new float[n * 2];
            for (int i = 0; i < n; i++) buf[i * 2] = 0.8f;   // left only, DC

            cf.Process(buf, n);

            float right = buf[(n - 1) * 2 + 1];
            Assert.True(Math.Abs(right) > 0.05f,
                $"crossfeed produced no opposite-channel signal (right={right})");
        }

        /// <summary>Off is bit-exact passthrough, not "nearly" passthrough.</summary>
        [Fact]
        public void Crossfeed_Off_LeavesTheBufferUntouched()
        {
            var cf = new CrossfeedStage();
            cf.SetParams(CrossfeedStage.Off, Rate);
            Assert.False(cf.Active);

            var buf = new float[] { 0.1f, -0.2f, 0.3f, -0.4f };
            var copy = (float[])buf.Clone();
            cf.Process(buf, 2);
            Assert.Equal(copy, buf);
        }

        /// <summary>Reset drops history, so a stream discontinuity cannot ring
        /// stale state into new audio. Same input after a reset gives the same
        /// output as a fresh stage.</summary>
        [Fact]
        public void Crossfeed_Reset_ClearsHistory()
        {
            var a = new CrossfeedStage(); a.SetParams(CrossfeedStage.HighEasy, Rate);
            var b = new CrossfeedStage(); b.SetParams(CrossfeedStage.HighEasy, Rate);

            var noise = new float[512 * 2];
            var rnd = new Random(7);
            for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rnd.NextDouble() * 2 - 1);

            a.Process((float[])noise.Clone(), 512);   // dirty a's history
            a.Reset();

            var x1 = (float[])noise.Clone();
            var x2 = (float[])noise.Clone();
            a.Process(x1, 512);
            b.Process(x2, 512);
            Assert.Equal(x2, x1);
        }

        // ───────────────────────── parametric EQ ─────────────────────────

        [Fact]
        public void Eq_NoBands_IsInactive()
        {
            var eq = new ParametricEqStage();
            eq.SetBands(Array.Empty<EqBand>(), Rate);
            Assert.False(eq.Active);
        }

        /// <summary>A 0 dB peaking band is the identity, so it is dropped
        /// rather than costing a biquad per sample per channel.</summary>
        [Fact]
        public void Eq_ZeroGainPeak_IsDropped()
        {
            var eq = new ParametricEqStage();
            eq.SetBands(new[] { new EqBand { Type = EqBandType.Peaking, FrequencyHz = 1000, GainDb = 0f } }, Rate);
            Assert.False(eq.Active);
        }

        /// <summary>A boost at the band frequency raises that tone's level,
        /// and a cut lowers it. Measured on a settled sine rather than on
        /// coefficients, so it survives any future change of filter library.</summary>
        [Theory]
        [InlineData(+9f, true)]
        [InlineData(-9f, false)]
        public void Eq_PeakingBand_MovesItsOwnFrequency(float gainDb, bool louder)
        {
            const float f = 1000f;
            float Rms(float[] b)
            {
                double s = 0; int n = 0;
                for (int i = b.Length; i > b.Length / 2; i -= 2) { s += b[i - 2] * b[i - 2]; n++; }
                return (float)Math.Sqrt(s / n);
            }

            const int n2 = 24000;
            var dry = new float[n2 * 2];
            for (int i = 0; i < n2; i++)
            {
                float v = (float)(0.4 * Math.Sin(2 * Math.PI * f * i / Rate));
                dry[i * 2] = v; dry[i * 2 + 1] = v;
            }
            var wet = (float[])dry.Clone();

            var eq = new ParametricEqStage();
            eq.SetBands(new[] { new EqBand { Type = EqBandType.Peaking, FrequencyHz = f, GainDb = gainDb, Q = 1.0f } }, Rate);
            Assert.True(eq.Active);
            eq.Process(wet, n2);

            float dryRms = Rms(dry), wetRms = Rms(wet);
            if (louder) Assert.True(wetRms > dryRms * 1.5f, $"boost did not raise level ({wetRms} vs {dryRms})");
            else Assert.True(wetRms < dryRms * 0.75f, $"cut did not lower level ({wetRms} vs {dryRms})");
        }

        /// <summary>A disabled band contributes nothing.</summary>
        [Fact]
        public void Eq_DisabledBand_IsSkipped()
        {
            var eq = new ParametricEqStage();
            eq.SetBands(new[] { new EqBand { Enabled = false, Type = EqBandType.Peaking, FrequencyHz = 1000, GainDb = 12f } }, Rate);
            Assert.False(eq.Active);
        }

        // ───────────────────────── limiter ─────────────────────────

        /// <summary>THE GUARANTEE. The ceiling is never exceeded, on any
        /// sample, however hot the input. Gain riding reduces how often the
        /// clamp is reached; the clamp is what makes the ceiling true, because
        /// there is no lookahead to catch a transient.</summary>
        [Fact]
        public void Limiter_NeverExceedsTheCeiling()
        {
            var lim = new LimiterStage();
            lim.SetParams(true, 0.9f, Rate);

            var rnd = new Random(99);
            const int n = 48000;
            var buf = new float[n * 2];
            for (int i = 0; i < n * 2; i++) buf[i] = (float)(rnd.NextDouble() * 8 - 4);   // wildly over full scale
            // a step transient the envelope cannot have anticipated
            for (int i = 1000; i < 1010; i++) { buf[i * 2] = 40f; buf[i * 2 + 1] = -40f; }

            lim.Process(buf, n);

            foreach (var v in buf)
                Assert.True(Math.Abs(v) <= 0.9f + 1e-6f, $"limiter let {v} through a 0.9 ceiling");
        }

        /// <summary>Disabled is bit-exact passthrough.</summary>
        [Fact]
        public void Limiter_Off_LeavesTheBufferUntouched()
        {
            var lim = new LimiterStage();
            lim.SetParams(false, 0.9f, Rate);
            var buf = new float[] { 3f, -3f, 0.1f, -0.1f };
            var copy = (float[])buf.Clone();
            lim.Process(buf, 2);
            Assert.Equal(copy, buf);
        }

        /// <summary>Quiet material passes without gain reduction, so the
        /// limiter is not a compressor on everything.</summary>
        [Fact]
        public void Limiter_QuietSignal_PassesUnchanged()
        {
            var lim = new LimiterStage();
            lim.SetParams(true, 0.9f, Rate);
            const int n = 2048;
            var buf = new float[n * 2];
            for (int i = 0; i < n; i++)
            {
                float v = (float)(0.2 * Math.Sin(2 * Math.PI * 440 * i / Rate));
                buf[i * 2] = v; buf[i * 2 + 1] = v;
            }
            var copy = (float[])buf.Clone();
            lim.Process(buf, n);
            for (int i = 0; i < buf.Length; i++)
                Assert.Equal(copy[i], buf[i], 5);
        }

        // ───────────── the contract every stage owes the lane ─────────────

        /// <summary>Frame count preserved and the span length untouched, for
        /// every stage. The Bluetooth lane rate-matches against an adaptive
        /// cushion, so a stage that changed the frame count would not merely
        /// sound wrong, it would fight that control loop.</summary>
        [Fact]
        public void EveryStage_PreservesFrameCount()
        {
            const int n = 777;
            var stages = new IMirrorStage[]
            {
                Make(() => { var c = new CrossfeedStage(); c.SetParams(CrossfeedStage.HighEasy, Rate); return c; }),
                Make(() => { var e = new ParametricEqStage(); e.SetBands(new[] { new EqBand { FrequencyHz = 800, GainDb = 6f } }, Rate); return e; }),
                Make(() => { var l = new LimiterStage(); l.SetParams(true, 0.9f, Rate); return l; }),
            };
            foreach (var s in stages)
            {
                var buf = new float[n * 2];
                for (int i = 0; i < buf.Length; i++) buf[i] = 0.25f;
                int before = buf.Length;
                s.Process(buf, n);
                Assert.Equal(before, buf.Length);
                Assert.All(buf, v => Assert.False(float.IsNaN(v) || float.IsInfinity(v)));
            }
            static IMirrorStage Make(Func<IMirrorStage> f) => f();
        }

        // ───────────────────────── AutoEq import ─────────────────────────

        private const string Sennheiser = @"
Preamp: -6.7 dB
Filter 1: ON LSC Fc 105 Hz Gain 5.5 dB Q 0.70
Filter 2: ON PK Fc 1050 Hz Gain -3.5 dB Q 1.20
Filter 3: ON HSC Fc 10000 Hz Gain 2.0 dB Q 0.70
Filter 4: OFF PK Fc 4000 Hz Gain 1.0 dB Q 2.00
";

        [Fact]
        public void AutoEq_ParsesBandsTypesAndPreamp()
        {
            var (bands, preamp) = AutoEqProfile.Parse(Sennheiser);
            Assert.Equal(-6.7f, preamp, 2);
            Assert.Equal(4, bands.Count);

            Assert.Equal(EqBandType.LowShelf, bands[0].Type);
            Assert.Equal(105f, bands[0].FrequencyHz, 2);
            Assert.Equal(5.5f, bands[0].GainDb, 2);
            Assert.Equal(0.70f, bands[0].Q, 2);

            Assert.Equal(EqBandType.Peaking, bands[1].Type);
            Assert.Equal(-3.5f, bands[1].GainDb, 2);

            Assert.Equal(EqBandType.HighShelf, bands[2].Type);

            Assert.False(bands[3].Enabled);   // the OFF row round-trips as disabled
        }

        /// <summary>A pasted profile is user input. One unparseable line must
        /// not lose the other twenty.</summary>
        [Fact]
        public void AutoEq_SkipsJunkWithoutLosingGoodBands()
        {
            var (bands, _) = AutoEqProfile.Parse(
                "hello\nFilter 1: ON PK Fc 100 Hz Gain 1.0 dB Q 1.0\n???\nFilter 2: ON XX Fc 200 Hz Gain 1.0 dB Q 1.0\nFilter 3: ON PK Fc 300 Hz Gain 2.0 dB Q 1.0\n");
            Assert.Equal(2, bands.Count);      // the unknown XX type is dropped
            Assert.Equal(100f, bands[0].FrequencyHz, 2);
            Assert.Equal(300f, bands[1].FrequencyHz, 2);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   \n  \n")]
        public void AutoEq_EmptyInput_IsEmptyNotAnException(string text)
        {
            var (bands, preamp) = AutoEqProfile.Parse(text);
            Assert.Empty(bands);
            Assert.Equal(0f, preamp);
        }

        /// <summary>Parsed bands drive the EQ, which is the whole point of
        /// supporting the format.</summary>
        [Fact]
        public void AutoEq_ParsedProfile_BuildsAWorkingEq()
        {
            var (bands, _) = AutoEqProfile.Parse(Sennheiser);
            var eq = new ParametricEqStage();
            eq.SetBands(bands, Rate);
            Assert.True(eq.Active);
        }
    }
}
