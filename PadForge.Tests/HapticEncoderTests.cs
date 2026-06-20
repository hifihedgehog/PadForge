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
        public void JoyConFold_NonFiniteFoldsToFloor_NoHang()
        {
            // +Infinity must NOT spin the fold loop (Inf * 0.5f == Inf), and NaN
            // (all comparisons unordered-false) must not slip through to a
            // garbage byte. Both fold to the band floor. Run the +Inf case on a
            // worker thread and bound it with Join(2000): a regression that
            // removes the guard turns +Inf into an infinite loop, and this fails
            // in 2s instead of hanging the whole run (xunit Timeout is async-only
            // and would not interrupt a synchronous spin anyway).
            float plusInf = float.NaN;
            var worker = new System.Threading.Thread(
                () => plusInf = HapticToneEncoder.FoldJoyConFrequency(float.PositiveInfinity))
            { IsBackground = true };
            worker.Start();
            Assert.True(worker.Join(2000), "+Inf fold did not terminate within 2s (the NaN/Inf guard is missing).");
            Assert.Equal(HapticToneEncoder.JoyConFreqMin, plusInf);

            Assert.Equal(HapticToneEncoder.JoyConFreqMin, HapticToneEncoder.FoldJoyConFrequency(float.NegativeInfinity));
            Assert.Equal(HapticToneEncoder.JoyConFreqMin, HapticToneEncoder.FoldJoyConFrequency(float.NaN));
            // And the encoder itself must terminate and emit a defined packet.
            byte[] bytes = HapticToneEncoder.EncodeJoyConRumble(float.PositiveInfinity, 0.5f);
            Assert.Equal(4, bytes.Length);
        }

        [Fact]
        public void JoyConEncode_MatchesReferenceFormulaByteForByte()
        {
            // Recompute encode_rumble (rumble.h:87-113) independently for a
            // mid-band tone and assert the implementation agrees bit for bit.
            // Float32 (MathF) + AwayFromZero mirrors the reference roundf(log2f).
            float freq = 320f, amp = 0.5f;
            byte encFreq = (byte)MathF.Round(MathF.Log2(freq / 10.0f) * 32.0f, MidpointRounding.AwayFromZero);
            ushort hf = (ushort)((encFreq - 0x60) * 4);
            byte lf = (byte)(encFreq - 0x40);
            byte encAmp = (byte)MathF.Round(MathF.Log2(amp * 8.7f) * 32.0f, MidpointRounding.AwayFromZero); // amp > 0.23 segment
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
        public void JoyConEncode_UsesFloatAwayFromZero_NotDoubleNotBankers()
        {
            // rumble.h:95 computes enc_freq = roundf(log2f(freq/10) * 32) in
            // float32, round-half-away-from-zero. The encoder must match THAT,
            // not C# default double Math.Log2 and not banker's ToEven. Both a
            // regression to double and to ToEven diverge from the float-away
            // reference at the analytic tie frequencies f = 10*2^((k+0.5)/32),
            // where float32 log2f lands exactly on k+0.5. Sweep those ties and
            // assert the encoder equals the float-away reference everywhere, and
            // confirm at least one tie actually discriminates (so this test has
            // teeth, unlike a single-point check that may miss the midpoint).
            bool discriminating = false;
            for (int k = 66; k <= 180; k += 2) // even k: at a true .5, away != even
            {
                float freq = (float)(10.0 * Math.Pow(2.0, (k + 0.5) / 32.0));
                if (freq < HapticToneEncoder.JoyConFreqMin || freq > HapticToneEncoder.JoyConFreqMax)
                    continue;

                byte refAway = (byte)MathF.Round(MathF.Log2(freq / 10.0f) * 32.0f, MidpointRounding.AwayFromZero);
                byte refEven = (byte)MathF.Round(MathF.Log2(freq / 10.0f) * 32.0f, MidpointRounding.ToEven);
                byte dblAway = (byte)Math.Round(Math.Log2(freq / 10.0) * 32.0, MidpointRounding.AwayFromZero);

                int implLf = HapticToneEncoder.EncodeJoyConRumble(freq, 0.5f)[2] & 0x7F;

                // Must equal the float away-from-zero reference. A regression to
                // double or ToEven would diverge here at a discriminating tie.
                Assert.Equal((refAway - 0x40) & 0x7F, implLf);

                if (refAway != refEven) discriminating = true;   // catches ToEven regression
                if (refAway != dblAway) discriminating = true;   // catches double regression
            }
            Assert.True(discriminating,
                "no tie frequency exercised the float/away-from-zero divergence; the guard would be vacuous");
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
        public void Adpcm_ChunkedStreamMatchesWholeCue()
        {
            // A streaming sink encodes per report payload, not whole-cue. The
            // state-carrying overloads must carry predictor/step across chunks
            // so the chunked byte stream is IDENTICAL to a single-call encode
            // (else the Wii decoder, which never resets mid-cue, hears a click
            // at each report boundary). Reset-per-chunk would diverge after the
            // first chunk.
            int n = 256;
            var pcm = new short[n];
            for (int i = 0; i < n; i++)
                pcm[i] = (short)(6000 * Math.Sin(2 * Math.PI * i / 24.0));

            byte[] whole = WiiSpeakerAdpcm.Encode(pcm);

            // Encode in even-length chunks carrying state forward.
            var s = WiiSpeakerAdpcm.State.Initial;
            var chunked = new System.Collections.Generic.List<byte>();
            for (int off = 0; off < n; off += 40) // 40 = even chunk
            {
                int len = Math.Min(40, n - off);
                var chunk = new short[len];
                Array.Copy(pcm, off, chunk, 0, len);
                chunked.AddRange(WiiSpeakerAdpcm.Encode(chunk, ref s));
            }

            Assert.Equal(whole, chunked.ToArray());

            // And a reset-per-chunk encode must NOT match (proves the test has
            // teeth: state continuity is what makes the streams equal).
            var resetChunked = new System.Collections.Generic.List<byte>();
            for (int off = 0; off < n; off += 40)
            {
                int len = Math.Min(40, n - off);
                var chunk = new short[len];
                Array.Copy(pcm, off, chunk, 0, len);
                resetChunked.AddRange(WiiSpeakerAdpcm.Encode(chunk)); // fresh state each chunk
            }
            Assert.NotEqual(whole, resetChunked.ToArray());
        }

        [Fact]
        public void Adpcm_StreamingEncode_RejectsOddChunk()
        {
            // An odd streaming chunk would ship a padding nibble the decoder
            // consumes as a real sample, permanently desyncing the stream. The
            // overload must throw rather than corrupt silently. The one-shot
            // Encode stays lenient (self-contained, no following chunk).
            var s = WiiSpeakerAdpcm.State.Initial;
            Assert.Throws<System.ArgumentException>(() => WiiSpeakerAdpcm.Encode(new short[] { 1, 2, 3 }, ref s));
            // One-shot odd input is allowed (no desync hazard).
            Assert.Equal(2, WiiSpeakerAdpcm.Encode(new short[] { 1, 2, 3 }).Length);
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
