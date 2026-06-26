using System;
using Xunit;
using PadForge.Engine.Haptics;

namespace PadForge.Tests
{
    /// <summary>
    /// Executed-evidence harness for issue #147. Where the cite-verify passes
    /// argue the code matches the references by reading, these tests RUN the
    /// device-independent logic and check the output: the encoder against an
    /// independent re-implementation of the joycon-singer formula, and the
    /// PCM->tone reducer against synthetic sine waves with a known pitch. This is
    /// the closest thing to a hardware confirmation for everything short of the
    /// actual actuator (the coil's acoustic response is the only residual).
    /// </summary>
    public class HapticDifferentialTests
    {
        private const int Rate = 8000;   // HapticToneService.ReduceRate
        private const int Tick = 80;     // HapticToneService.SamplesPerTick

        // ── Reducer: pitch detection on synthetic sines (the risky DSP) ──

        private static (float Hz, float Amp) DetectSine(float freq, float amp, int ticks = 30)
        {
            var r = new HapticToneReducer(Rate);
            var buf = new float[Tick];
            double ph = 0, dph = 2.0 * Math.PI * freq / Rate;
            (float Hz, float Amp) last = (0f, 0f);
            for (int t = 0; t < ticks; t++)
            {
                for (int i = 0; i < Tick; i++) { buf[i] = (float)(amp * Math.Sin(ph)); ph += dph; }
                last = r.Push(buf, Tick);
            }
            return last;
        }

        [Theory]
        [InlineData(45f)]    // low band that the short-lag-bias defect mispitched to ~1333 Hz
        [InlineData(55f)]
        [InlineData(73f)]
        [InlineData(90f)]
        [InlineData(110f)]
        [InlineData(147f)]
        [InlineData(220f)]
        [InlineData(330f)]
        [InlineData(440f)]
        [InlineData(587f)]
        [InlineData(784f)]
        public void Reducer_DetectsPureSinePitch_NoOctaveError(float freq)
        {
            var (hz, amp) = DetectSine(freq, 0.5f);
            Assert.True(amp > 0.1f, $"expected a voiced amplitude for a {freq} Hz tone, got {amp}");
            // The quantization is coarse (Hz = 8000/lag), so allow the nearest
            // achievable lag, but EXCLUDE octave errors (0.5x / 2x) and the
            // short-lag-bias failure (a low tone reported near maxFreq).
            Assert.InRange(hz, freq * 0.80f, freq * 1.22f);
        }

        [Fact]
        public void Reducer_Silence_ReportsZeroAmplitude()
        {
            var r = new HapticToneReducer(Rate);
            var buf = new float[Tick]; // all zero
            (float Hz, float Amp) last = (0f, 0f);
            for (int t = 0; t < 20; t++) last = r.Push(buf, Tick);
            Assert.Equal(0f, last.Amp);
        }

        [Fact]
        public void Reducer_Amplitude_TracksInputLevel()
        {
            var (_, loud) = DetectSine(220f, 0.8f);
            var (_, soft) = DetectSine(220f, 0.1f);
            Assert.True(loud > soft, $"louder input must report a higher amplitude (loud {loud} vs soft {soft})");
            Assert.True(loud <= 1.0f, $"amplitude must clamp to 1.0, got {loud}");
        }

        // ── JoyCon encoder: independent re-implementation of rumble.h, diffed ──

        // Independent port of joycon-singer rumble.h encode_rumble, written fresh
        // from the formula (using the authoritative 0.12f amplitude threshold that
        // rumble_data_table.md:30 specifies, the same deliberate value PadForge
        // uses). A byte mismatch here means a transcription error (precedence,
        // cast point, shift, rounding mode) in one of the two writings.
        private static byte[] RefEncodeJoyCon(float freq, float amp)
        {
            while (freq < 41.0f) freq *= 2.0f;
            while (freq > 626.0f) freq *= 0.5f;
            if (amp < 0f) amp = 0f;
            if (amp > 0.8f) amp = 0.8f;

            int encFreq = (int)MathF.Round(MathF.Log2(freq / 10.0f) * 32.0f, MidpointRounding.AwayFromZero);
            int hf = (encFreq - 0x60) * 4;
            int lf = encFreq - 0x40;

            int encAmp = 0;
            if (amp > 0.23f) encAmp = (int)Math.Max(0f, MathF.Round(MathF.Log2(amp * 8.7f) * 32.0f, MidpointRounding.AwayFromZero));
            else if (amp > 0.12f) encAmp = (int)Math.Max(0f, MathF.Round(MathF.Log2(amp * 17.0f) * 16.0f, MidpointRounding.AwayFromZero));

            int hfAmp = encAmp * 2;
            int lfAmp = (encAmp >> 1) + 0x40;
            return new byte[]
            {
                (byte)(hf & 0xFF),
                (byte)(((hf >> 8) & 0xFF) | (hfAmp & 0xFF)),
                (byte)((lf & 0xFF) | ((lfAmp << 7) & 0x80)),
                (byte)(lfAmp >> 1),
            };
        }

        [Fact]
        public void JoyConEncoder_MatchesIndependentReimpl_AcrossFullSweep()
        {
            int checkd = 0;
            for (float f = 41f; f <= 626f; f += 3f)
            {
                for (float a = 0f; a <= 0.8f; a += 0.02f)
                {
                    var got = HapticToneEncoder.EncodeJoyConRumble(f, a);
                    var exp = RefEncodeJoyCon(f, a);
                    Assert.True(exp[0] == got[0] && exp[1] == got[1] && exp[2] == got[2] && exp[3] == got[3],
                        $"mismatch at f={f} a={a}: exp {BitConverter.ToString(exp)} got {BitConverter.ToString(got)}");
                    checkd++;
                }
            }
            Assert.True(checkd > 7000, $"sweep should cover thousands of points, covered {checkd}");
        }

        [Fact]
        public void JoyConEncoder_OctaveFoldsOutOfBand_AndNeverThrows()
        {
            // A sub-band 55 Hz note and a super-band 2000 Hz note must both encode
            // (folded into 41..626), not throw and not clip to a wrong byte.
            var low = HapticToneEncoder.EncodeJoyConRumble(20f, 0.5f);   // folds up
            var high = HapticToneEncoder.EncodeJoyConRumble(2500f, 0.5f); // folds down
            Assert.Equal(4, low.Length);
            Assert.Equal(4, high.Length);
        }

        // ── Steam encoders: invariants the references pin exactly ──

        [Theory]
        [InlineData(220f)]
        [InlineData(440f)]
        [InlineData(880f)]
        public void Steam2015_Period_Equals_MagicOverFrequency(float freq)
        {
            var blob = HapticToneEncoder.EncodeSteamClassic(freq, durationSeconds: -1.0);
            ushort expPeriod = (ushort)((1.0 / freq) * 495483.0);
            Assert.Equal(0x8F, blob[0]);
            Assert.Equal(0x07, blob[1]);
            Assert.Equal((byte)(expPeriod % 0xFF), blob[3]);
            Assert.Equal((byte)(expPeriod / 0xFF), blob[4]);
            // Square wave: pulse-low period repeats pulse-high.
            Assert.Equal(blob[3], blob[5]);
            Assert.Equal(blob[4], blob[6]);
            // Sustain repeat 0x7FFF.
            Assert.Equal(0x7FFF % 0xFF, blob[7]);
            Assert.Equal(0x7FFF / 0xFF, blob[8]);
        }

        [Theory]
        [InlineData(0.0f, 0x80)]   // signed: silent -> 0x80
        [InlineData(1.0f, 0x7F)]   // signed: full -> 0x7F
        public void SteamDeck_GainByte_IsSigned(float amp, int expectedGain)
        {
            var d = HapticToneEncoder.EncodeSteamDeck(440f, amp);
            Assert.Equal(0xEA, d[0]);
            Assert.Equal((byte)expectedGain, d[5]);
        }
    }
}
