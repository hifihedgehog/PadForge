using System;
using PadForge.Engine.Haptics;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Argues the issue #146/#147 encoder cores against their cloned references
    /// (joycon-singer rumble.h, SteamControllerSinger main.cpp, dolphin
    /// Speaker.cpp). These are the runtime positive controls the cite-verify
    /// pass can cite as Confirmed: the logic runs here even though the device
    /// hardware does not.
    /// </summary>
    public class HapticEncoderTests
    {
        // ─── Joy-Con HD rumble (joycon-singer rumble.h) ───

        [Fact]
        public void JoyConNeutral_IsTheReferenceSilencePacket()
        {
            // RUMBLE_NEUTRAL { 0x00, 0x01, 0x40, 0x40 } (rumble.h:10).
            Assert.Equal(new byte[] { 0x00, 0x01, 0x40, 0x40 }, HapticToneEncoder.JoyConNeutral());
        }

        [Fact]
        public void JoyConFold_KeepsPitchClassInBand()
        {
            // 1000 Hz folds down by an octave until <= 626; 20 Hz folds up past 41.
            float high = HapticToneEncoder.FoldJoyConFrequency(1000f);
            Assert.InRange(high, HapticToneEncoder.JoyConFreqMin, HapticToneEncoder.JoyConFreqMax);
            float low = HapticToneEncoder.FoldJoyConFrequency(20f);
            Assert.InRange(low, HapticToneEncoder.JoyConFreqMin, HapticToneEncoder.JoyConFreqMax);
        }

        [Fact]
        public void JoyConEncode_MatchesReferenceFormulaByteForByte()
        {
            // Recompute encode_rumble (rumble.h:87-113) independently for a
            // mid-band tone and assert the implementation agrees bit for bit.
            // AwayFromZero matches the reference roundf (NOT C# default ToEven).
            float freq = 320f, amp = 0.5f;
            byte encFreq = (byte)Math.Round(Math.Log2(freq / 10.0) * 32.0, MidpointRounding.AwayFromZero);
            ushort hf = (ushort)((encFreq - 0x60) * 4);
            byte lf = (byte)(encFreq - 0x40);
            byte encAmp = (byte)Math.Round(Math.Log2(amp * 8.7) * 32.0, MidpointRounding.AwayFromZero); // amp > 0.23 segment
            ushort hfAmp = (ushort)(encAmp * 2);
            byte lfAmp = (byte)((encAmp >> 1) + 0x40);
            var expected = new byte[]
            {
                (byte)(hf & 0xFF),
                (byte)(((hf >> 8) & 0xFF) | (hfAmp & 0xFF)),
                (byte)(lf | ((lfAmp << 7) & 0x80)),
                (byte)(lfAmp >> 1),
            };
            Assert.Equal(expected, HapticToneEncoder.EncodeJoyConRumble(freq, amp));
        }

        [Fact]
        public void JoyConEncode_RoundsHalfAwayFromZero_NotBankers()
        {
            // The reference roundf rounds half away from zero. At the analytic
            // boundary f = 10*2^((k+0.5)/32) the product log2(f/10)*32 == k+0.5.
            // For k=88 -> 68.004254 Hz: away-from-zero yields enc_freq 89,
            // banker's (ToEven) would yield 88. enc_freq feeds lf = enc_freq-0x40,
            // and out[2]'s low 7 bits carry lf. So the low 7 bits must be
            // 89-0x40 = 25, never 24.
            float freq = (float)(10.0 * Math.Pow(2.0, 88.5 / 32.0));
            byte[] bytes = HapticToneEncoder.EncodeJoyConRumble(freq, 0.5f);

            byte awayEncFreq = (byte)Math.Round(Math.Log2(freq / 10.0) * 32.0, MidpointRounding.AwayFromZero);
            int expectedLf = (awayEncFreq - 0x40) & 0x7F;
            Assert.Equal(expectedLf, bytes[2] & 0x7F);
            // And explicitly: it must not be the banker's-rounding result.
            byte evenEncFreq = (byte)Math.Round(Math.Log2(freq / 10.0) * 32.0, MidpointRounding.ToEven);
            if (awayEncFreq != evenEncFreq)
                Assert.NotEqual((evenEncFreq - 0x40) & 0x7F, bytes[2] & 0x7F);
        }

        [Fact]
        public void JoyConEncode_SilenceBelowDeadZone()
        {
            // amp <= 0.012 -> enc_amp 0 -> hf_amp 0, lf_amp 0x40 (the amplitude
            // floor; pitch bytes still set). Confirms the dead-zone branch.
            var bytes = HapticToneEncoder.EncodeJoyConRumble(320f, 0.0f);
            // lf_amp = (0 >> 1) + 0x40 = 0x40; out[3] = 0x40 >> 1 = 0x20.
            Assert.Equal(0x20, bytes[3]);
        }

        [Fact]
        public void MidiNote69_Is440Hz()
        {
            Assert.Equal(440f, HapticToneEncoder.MidiNoteToFrequency(69), 3);
        }

        // ─── Steam Controller 2015 (SteamControllerSinger main.cpp) ───

        [Fact]
        public void SteamClassic_HeaderAndPeriodMatchReference()
        {
            // A4 = 440 Hz, sustain. periodCommand = (1/440)*495483 (main.cpp:120-121).
            byte[] blob = HapticToneEncoder.EncodeSteamClassic(440f, -1.0, haptic: 1);
            Assert.Equal(64, blob.Length);
            Assert.Equal(0x8F, blob[0]);
            Assert.Equal(0x07, blob[1]);
            Assert.Equal(0x01, blob[2]); // haptic = left

            ushort periodCommand = (ushort)((1.0 / 440f) * HapticToneEncoder.SteamMagicPeriodRatio);
            Assert.Equal((byte)(periodCommand % 0xFF), blob[3]);
            Assert.Equal((byte)(periodCommand / 0xFF), blob[4]);
            Assert.Equal(blob[3], blob[5]); // square wave: low duration == high
            Assert.Equal(blob[4], blob[6]);
            // Sustain -> repeat 0x7FFF -> LSB/MSB by the reference's 0xFF split.
            Assert.Equal((byte)(0x7FFF % 0xFF), blob[7]);
            Assert.Equal((byte)(0x7FFF / 0xFF), blob[8]);
        }

        [Fact]
        public void SteamClassic_StopMatchesReferenceNote0RepeatZero()
        {
            // The reference NOTE_STOP is "note 0 (8.1758 Hz), duration 0", which
            // falls through encode: the note-0 period stays in bytes 3-6 and only
            // the repeat count (bytes 7-8) is zeroed (main.cpp:114-134). So a stop
            // is not a blank packet.
            byte[] blob = HapticToneEncoder.EncodeSteamClassic(0f, 0.0, haptic: 0);
            Assert.Equal(0x8F, blob[0]);
            Assert.Equal(0x07, blob[1]);
            Assert.Equal(0x00, blob[2]);

            ushort periodCommand = (ushort)((1.0 / 8.1758f) * HapticToneEncoder.SteamMagicPeriodRatio);
            Assert.Equal((byte)(periodCommand % 0xFF), blob[3]); // note-0 period present
            Assert.Equal((byte)(periodCommand / 0xFF), blob[4]);
            Assert.Equal(0, blob[7]); // repeat count zeroed -> silent
            Assert.Equal(0, blob[8]);
        }

        // ─── Wii speaker Yamaha ADPCM (dolphin Speaker.cpp) ───

        [Fact]
        public void Adpcm_DecodeInitialState_FirstNibbleMatchesFormula()
        {
            // predictor 0, step 127. Nibble 0 -> predictor += 127*1/8 = 15.
            var s = WiiSpeakerAdpcm.State.Initial;
            short first = WiiSpeakerAdpcm.ExpandNibble(ref s, 0);
            Assert.Equal(15, first);          // 127*1/8 = 15 (integer)
            // step = (127*230)>>8 = 114, clamped up to the 127 floor
            // (av_clip(step, 127, 24576), Speaker.cpp:53).
            Assert.Equal(127, s.Step);

            // A large nibble (7, indexscale 614) pushes step above the floor:
            // (127*614)>>8 = 304, unclamped.
            var s2 = WiiSpeakerAdpcm.State.Initial;
            WiiSpeakerAdpcm.ExpandNibble(ref s2, 7);
            Assert.Equal((127 * 614) >> 8, s2.Step);
        }

        [Fact]
        public void Adpcm_RoundTrip_TracksASineWithinTolerance()
        {
            // Encode a low-rate sine, decode it back, assert the reconstruction
            // follows the signal. ADPCM is lossy; assert mean abs error is a
            // small fraction of full scale rather than exact equality.
            int n = 512;
            var pcm = new short[n];
            for (int i = 0; i < n; i++)
                pcm[i] = (short)(8000 * Math.Sin(2 * Math.PI * i / 32.0));

            byte[] enc = WiiSpeakerAdpcm.Encode(pcm);
            Assert.Equal((n + 1) / 2, enc.Length); // 2 samples per byte

            short[] dec = WiiSpeakerAdpcm.Decode(enc);
            Assert.Equal(n, dec.Length);

            double sumErr = 0;
            for (int i = 0; i < n; i++) sumErr += Math.Abs(dec[i] - pcm[i]);
            double meanErr = sumErr / n;
            // Yamaha ADPCM on a smooth tone tracks well; well under 10% FS.
            Assert.True(meanErr < 3276, $"mean abs error {meanErr} too high");
        }

        [Fact]
        public void Adpcm_HighNibbleDecodedFirst()
        {
            // One byte 0x0F: high nibble 0 then low nibble 15. Sample 0 uses
            // nibble 0 (+15), sample 1 uses nibble 15 (-step*15/8 from there).
            short[] dec = WiiSpeakerAdpcm.Decode(new byte[] { 0x0F });
            Assert.Equal(2, dec.Length);
            Assert.Equal(15, dec[0]);          // high nibble 0 first
            Assert.True(dec[1] < dec[0]);      // low nibble 15 is a negative step
        }
    }
}
