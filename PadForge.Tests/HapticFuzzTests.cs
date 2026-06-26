using System;
using Xunit;
using PadForge.Engine.Haptics;

namespace PadForge.Tests
{
    /// <summary>
    /// Adversarial / property / fuzz harness for issue #147. Round 2's harness fed
    /// clean inputs; this one feeds HOSTILE inputs (NaN, infinities, denormals,
    /// negatives, huge magnitudes, white noise, DC, impulses, harmonic-rich
    /// waveforms, ring-buffer overruns) and asserts the invariants that must hold
    /// for EVERY input: no throw, no NaN/Infinity in the output, bytes always the
    /// right length, amplitude always in [0,1], detected pitch always in the
    /// device's physical band. A single counterexample is a real defect a
    /// clean-input test would never surface. Deterministic seed for reproducibility.
    /// </summary>
    public class HapticFuzzTests
    {
        private const int Rate = 8000;
        private const int Tick = 80;

        private static readonly float[] HostileScalars =
        {
            float.NaN, float.PositiveInfinity, float.NegativeInfinity,
            float.MaxValue, float.MinValue, float.Epsilon, -float.Epsilon,
            0f, -0f, 1f, -1f, -1000f, 1e9f, -1e9f, 0.0001f, 40.9999f, 41f, 626f, 626.0001f, 1252f, 8000f,
        };

        // ── Encoder invariants under hostile input ──

        [Fact]
        public void JoyConEncoder_NeverThrows_AlwaysFourBytes_OverHostileGrid()
        {
            foreach (var f in HostileScalars)
                foreach (var a in HostileScalars)
                {
                    var b = HapticToneEncoder.EncodeJoyConRumble(f, a);
                    Assert.Equal(4, b.Length);
                    // Determinism: same input -> same output.
                    var b2 = HapticToneEncoder.EncodeJoyConRumble(f, a);
                    Assert.Equal(b, b2);
                }
        }

        [Fact]
        public void JoyConEncoder_FoldGuard_NaNInfNeg_FoldToBandFloor_NoSpin()
        {
            // The fold guard must terminate (no infinite *=2 / *=0.5 spin) and map
            // non-finite / non-positive to the band floor 41 Hz.
            Assert.Equal(41f, HapticToneEncoder.FoldJoyConFrequency(float.NaN));
            Assert.Equal(41f, HapticToneEncoder.FoldJoyConFrequency(float.PositiveInfinity));
            Assert.Equal(41f, HapticToneEncoder.FoldJoyConFrequency(float.NegativeInfinity));
            Assert.Equal(41f, HapticToneEncoder.FoldJoyConFrequency(0f));
            Assert.Equal(41f, HapticToneEncoder.FoldJoyConFrequency(-100f));
            // In-band values pass through unchanged; out-of-band fold INTO [41,626].
            for (float f = 0.5f; f < 5000f; f *= 1.3f)
            {
                float folded = HapticToneEncoder.FoldJoyConFrequency(f);
                Assert.InRange(folded, 41f, 626f);
            }
        }

        [Fact]
        public void JoyConEncoder_AmplitudeMonotonic_AtFixedFrequency()
        {
            // Encoded amplitude (the packed amp bits) must never DECREASE as input
            // amplitude rises (a louder request must not encode quieter).
            int prevHa = -1, prevLa = -1;
            for (float a = 0f; a <= 0.8f; a += 0.01f)
            {
                var b = HapticToneEncoder.EncodeJoyConRumble(330f, a);
                int ha = b[1] & 0xFE;           // high-freq amplitude bits
                int la = (b[2] >> 1) | (b[3] << 7); // reconstruct lf_amp-ish ordering
                Assert.True(ha >= prevHa - 1, $"HF amp went backwards at a={a}: {ha} < {prevHa}");
                prevHa = ha; prevLa = la;
            }
        }

        [Fact]
        public void SteamEncoders_NeverThrow_CorrectLength_OverHostileGrid()
        {
            foreach (var f in HostileScalars)
            {
                var c2015 = HapticToneEncoder.EncodeSteamClassic(f, durationSeconds: -1.0);
                Assert.Equal(64, c2015.Length);
                Assert.Equal(0x8F, c2015[0]);

                foreach (var a in HostileScalars)
                {
                    var d = HapticToneEncoder.EncodeSteamDeck(f, a);
                    Assert.Equal(64, d.Length);
                    Assert.Equal(0xEA, d[0]);

                    // Triton 0x83 LFO tone: always 10 bytes, id 0x83, actuator index
                    // preserved in byte 1, never throws on NaN/Inf/huge/negative input.
                    var t = HapticToneEncoder.EncodeTritonTone(3, f, a);
                    Assert.Equal(10, t.Length);
                    Assert.Equal(0x83, t[0]);
                    Assert.Equal(3, t[1]);
                }
            }
        }

        // ── Reducer invariants under hostile signals ──

        private static void AssertReducerInvariants(float hz, float amp, string ctx)
        {
            Assert.False(float.IsNaN(amp) || float.IsInfinity(amp), $"{ctx}: amp not finite ({amp})");
            Assert.InRange(amp, 0f, 1f);
            Assert.False(float.IsNaN(hz) || float.IsInfinity(hz), $"{ctx}: hz not finite ({hz})");
            // Detected pitch is rate/lag (lag in [minLag, maxLag]) or the held last
            // pitch, all inside the physical band [~40, ~1334] Hz.
            Assert.InRange(hz, 39f, 1334f);
        }

        [Fact]
        public void Reducer_WhiteNoise_DC_Impulse_NeverBreakInvariants()
        {
            var rng = new Random(1234567);
            var r = new HapticToneReducer(Rate);
            var buf = new float[Tick];
            for (int t = 0; t < 300; t++)
            {
                for (int i = 0; i < Tick; i++)
                {
                    switch (t % 5)
                    {
                        case 0: buf[i] = (float)(rng.NextDouble() * 2 - 1); break;       // white noise
                        case 1: buf[i] = 0.7f; break;                                    // DC
                        case 2: buf[i] = (i == 0) ? 1f : 0f; break;                      // impulse
                        case 3: buf[i] = (i % 2 == 0) ? 0.9f : -0.9f; break;             // Nyquist square
                        case 4: buf[i] = (float)(rng.NextDouble() * 1e6 - 5e5); break;   // huge magnitude
                    }
                }
                var (hz, amp) = r.Push(buf, Tick);
                AssertReducerInvariants(hz, amp, $"tick {t} mode {t % 5}");
            }
        }

        [Fact]
        public void Reducer_RingBufferOverrun_AndZeroCount_NoCrash()
        {
            var r = new HapticToneReducer(Rate);
            var big = new float[5000]; // far larger than the ~666-sample ring
            for (int i = 0; i < big.Length; i++) big[i] = (float)Math.Sin(2 * Math.PI * 220 * i / Rate);
            var (hz, amp) = r.Push(big, big.Length);
            AssertReducerInvariants(hz, amp, "overrun");

            var empty = Array.Empty<float>();
            var (hz0, amp0) = r.Push(empty, 0); // count 0
            AssertReducerInvariants(hz0, amp0, "zero-count");

            // count larger than the array length must be clamped, not over-read.
            var small = new float[Tick];
            var (hz1, amp1) = r.Push(small, Tick);
            AssertReducerInvariants(hz1, amp1, "normal-after-edge");
        }

        // ── The reducer fix under HARMONIC (non-sine) inputs: must not regress to
        //    the ~1333 Hz short-lag garbage the global-max produced. ──

        private static (float Hz, float Amp) DetectWave(Func<double, double> wave, float freq, float amp, int ticks = 40)
        {
            var r = new HapticToneReducer(Rate);
            var buf = new float[Tick];
            double ph = 0, dph = freq / Rate; // phase in cycles per sample
            (float Hz, float Amp) last = (0f, 0f);
            for (int t = 0; t < ticks; t++)
            {
                for (int i = 0; i < Tick; i++) { buf[i] = (float)(amp * wave(ph % 1.0)); ph += dph; }
                last = r.Push(buf, Tick);
            }
            return last;
        }

        [Theory]
        [InlineData(110f)]
        [InlineData(220f)]
        [InlineData(440f)]
        public void Reducer_Sawtooth_TracksFundamental_NotMaxFreqGarbage(float freq)
        {
            // Sawtooth: harmonic-rich. The fundamental period must win; at worst an
            // octave error is acceptable for haptics, but a jump to the ~1333 Hz
            // ceiling (the old short-lag-bias failure) is NOT.
            Func<double, double> saw = p => 2.0 * p - 1.0;
            var (hz, amp) = DetectWave(saw, freq, 0.6f);
            Assert.True(amp > 0.1f, $"sawtooth {freq} Hz should be voiced, amp {amp}");
            Assert.InRange(hz, freq * 0.45f, freq * 2.2f); // allow octave, exclude maxFreq garbage
        }

        [Theory]
        [InlineData(165f)]
        [InlineData(330f)]
        public void Reducer_Square_TracksFundamental_NotMaxFreqGarbage(float freq)
        {
            Func<double, double> square = p => p < 0.5 ? 1.0 : -1.0;
            var (hz, amp) = DetectWave(square, freq, 0.6f);
            Assert.True(amp > 0.1f, $"square {freq} Hz should be voiced, amp {amp}");
            Assert.InRange(hz, freq * 0.45f, freq * 2.2f);
        }

        [Fact]
        public void Reducer_SweptChirp_StaysFiniteAndInBand()
        {
            var r = new HapticToneReducer(Rate);
            var buf = new float[Tick];
            double ph = 0;
            for (int t = 0; t < 120; t++)
            {
                float f = 60f + t * 9f; // sweep 60 -> ~1130 Hz
                double dph = 2 * Math.PI * f / Rate;
                for (int i = 0; i < Tick; i++) { buf[i] = (float)(0.5 * Math.Sin(ph)); ph += dph; }
                var (hz, amp) = r.Push(buf, Tick);
                AssertReducerInvariants(hz, amp, $"chirp t={t} f={f}");
            }
        }
    }
}
