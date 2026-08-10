using System;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Regression guards for the composite USB persona audio bridge
    /// (#255, HM#39). Every assertion here corresponds to a defect that
    /// actually shipped and was caught on hardware during the 2026-07-31
    /// bring-up, when internal telemetry read healthy while the consumer
    /// received garbage. The wire format is pinned deterministically so
    /// those specific regressions cannot recur silently.
    /// </summary>
    public class PersonaAudioBridgeTests
    {
        private static short[] Tick(Func<int, short> l, Func<int, short> r)
        {
            // One 10.667 ms haptic tick: 512 stereo frames at 48 kHz.
            var pcm = new short[512 * 2];
            for (int i = 0; i < 512; i++) { pcm[i * 2] = l(i); pcm[i * 2 + 1] = r(i); }
            return pcm;
        }

        private static byte[] NewReport() => new byte[AudioPassthroughService.Ds5HapticBtReportSize];

        // ── The mic-session byte: audio reports must not close the mic ──
        //
        // Every 0x35 speaker and 0x32 haptic report carries the packet
        // 0x11 session header, whose payload byte 0 is the mic command.
        // Hardcoding 0xFE (close) silenced the pad's microphone the moment
        // any audio played: capture rms fell 0.0556 to 0.0000.

        [Fact]
        public void MicSessionByte_OpensWhenSessionIsOpen()
        {
            Assert.Equal(0xFF, AudioPassthroughService.Ds5MicSessionByte(true));
            Assert.Equal(0xFE, AudioPassthroughService.Ds5MicSessionByte(false));
        }

        [Fact]
        public void HapticReport_CarriesTheLiveMicSession_NotAConstantClose()
        {
            var open = NewReport();
            var shut = NewReport();
            var pcm = Tick(_ => 0, _ => 0);

            AudioPassthroughService.BuildDs5BtHapticReport(open, 0, 0, micOpen: true, pcm);
            AudioPassthroughService.BuildDs5BtHapticReport(shut, 0, 0, micOpen: false, pcm);

            Assert.Equal(0xFF, open[4]);
            Assert.Equal(0xFE, shut[4]);
        }

        // ── Packet grammar, byte for byte (SAxense SAxense.c) ──

        [Fact]
        public void HapticReport_MatchesTheSAxensePacketGrammar()
        {
            var report = NewReport();
            AudioPassthroughService.BuildDs5BtHapticReport(report, 0, 0, false, Tick(_ => 0, _ => 0));

            Assert.Equal(0x32, report[0]);          // report id
            Assert.Equal(0x11 | 0x80, report[2]);   // packet 0x11, sized
            Assert.Equal(7, report[3]);             // its 7-byte payload
            Assert.Equal(0xFE, report[4]);          // mic command (closed here)
            Assert.Equal(0xFF, report[9]);
            Assert.Equal(0x12 | 0x80, report[11]);  // packet 0x12, sized
            Assert.Equal(64, report[12]);           // 64 bytes of s8 stereo
            Assert.Equal(142, AudioPassthroughService.Ds5HapticBtReportSize);
        }

        // ── Per-STREAM sequence, not per-pad ──
        //
        // The 0x32 haptic stream sharing the 0x35 speaker stream's counter
        // made each stream see jumps of two, which the firmware garbles.
        // Heard as distorted, pitch-shifted speaker audio.

        [Fact]
        public void HapticReport_SequenceLivesInTheHighNibbleAndWraps()
        {
            var pcm = Tick(_ => 0, _ => 0);
            for (int seq = 0; seq < 16; seq++)
            {
                var report = NewReport();
                AudioPassthroughService.BuildDs5BtHapticReport(report, seq, 0, false, pcm);
                Assert.Equal((byte)(seq << 4), report[1]);
            }

            var wrapped = NewReport();
            AudioPassthroughService.BuildDs5BtHapticReport(wrapped, 16, 0, false, pcm);
            Assert.Equal(0x00, wrapped[1]);     // 16 wraps to 0, never spills
        }

        [Fact]
        public void HapticReport_PacketCounterIsCarriedVerbatim()
        {
            var report = NewReport();
            AudioPassthroughService.BuildDs5BtHapticReport(report, 0, 0xAB, false, Tick(_ => 0, _ => 0));
            Assert.Equal(0xAB, report[10]);
        }

        // ── 48 kHz s16 to 3 kHz s8 decimation ──

        [Fact]
        public void Decimation_TakesTheBlockMeanHighByte()
        {
            // Constant full-positive left, constant half-negative right.
            var pcm = Tick(_ => 0x4000, _ => -0x2000);
            var report = NewReport();
            AudioPassthroughService.BuildDs5BtHapticReport(report, 0, 0, false, pcm);

            // mean == the constant, then >> 8 into signed 8-bit.
            byte expectL = unchecked((byte)(0x4000 >> 8));           // 0x40
            byte expectR = unchecked((byte)Math.Clamp((-0x2000) >> 8, -128, 127)); // 0xE0
            for (int o = 0; o < 32; o++)
            {
                Assert.Equal(expectL, report[13 + o * 2]);
                Assert.Equal(expectR, report[14 + o * 2]);
            }
        }

        [Fact]
        public void Decimation_ClampsRatherThanWrapping()
        {
            var pcm = Tick(_ => short.MaxValue, _ => short.MinValue);
            var report = NewReport();
            AudioPassthroughService.BuildDs5BtHapticReport(report, 0, 0, false, pcm);
            for (int o = 0; o < 32; o++)
            {
                Assert.Equal(127, unchecked((sbyte)report[13 + o * 2]));
                Assert.Equal(-128, unchecked((sbyte)report[14 + o * 2]));
            }
        }

        // ── The silence gate that keeps the lanes from interleaving ──

        [Fact]
        public void SilenceGate_ReportsNoSignalForAnAllZeroTick()
        {
            var report = NewReport();
            bool signal = AudioPassthroughService.BuildDs5BtHapticReport(
                report, 0, 0, false, Tick(_ => 0, _ => 0));
            Assert.False(signal);
        }

        [Fact]
        public void SilenceGate_ReportsSignalWhenTheBlockMeanSurvivesDecimation()
        {
            var report = NewReport();
            bool signal = AudioPassthroughService.BuildDs5BtHapticReport(
                report, 0, 0, false, Tick(_ => 0x4000, _ => 0x4000));
            Assert.True(signal);
        }

        [Fact]
        public void SilenceGate_TreatsSubDecimationDitherAsSilence()
        {
            // Values below the >> 8 threshold decimate to zero, so they must
            // not hold the 0x32 stream open and interleave with the speaker.
            var report = NewReport();
            bool signal = AudioPassthroughService.BuildDs5BtHapticReport(
                report, 0, 0, false, Tick(_ => 0x00FF, _ => 0x00FF));
            Assert.False(signal);
        }

        // ── CRC32 lands in the tail and covers the payload ──

        [Fact]
        public void Crc_OccupiesTheFinalFourBytesAndTracksThePayload()
        {
            int size = AudioPassthroughService.Ds5HapticBtReportSize;
            var quiet = NewReport();
            var loud = NewReport();
            AudioPassthroughService.BuildDs5BtHapticReport(quiet, 0, 0, false, Tick(_ => 0, _ => 0));
            AudioPassthroughService.BuildDs5BtHapticReport(loud, 0, 0, false, Tick(_ => 0x4000, _ => 0x4000));

            var quietCrc = new[] { quiet[size - 4], quiet[size - 3], quiet[size - 2], quiet[size - 1] };
            var loudCrc = new[] { loud[size - 4], loud[size - 3], loud[size - 2], loud[size - 1] };
            Assert.NotEqual(quietCrc, loudCrc);
            Assert.NotEqual(new byte[] { 0, 0, 0, 0 }, quietCrc);
        }

        [Fact]
        public void Builder_IsDeterministic()
        {
            var a = NewReport();
            var b = NewReport();
            var pcm = Tick(i => (short)(i * 37), i => (short)(-i * 11));
            AudioPassthroughService.BuildDs5BtHapticReport(a, 5, 0x22, true, pcm);
            AudioPassthroughService.BuildDs5BtHapticReport(b, 5, 0x22, true, pcm);
            Assert.Equal(a, b);
        }

        // ── The mic decoder channel count ──
        //
        // The Opus TOC on every DualSense mic frame is 0xD4, whose stereo
        // bit IS set. Reading that as authority and switching the decoder
        // to stereo made Windows receive full-scale noise (rms 0.5118 vs
        // 0.0208 for mono, measured at the endpoint). A mono decoder
        // decoding a stereo-flagged packet is legal and downmixes.

        [Fact]
        public void BtMicDecoder_StaysMono_DoNotReDeriveFromTheOpusTocStereoBit()
        {
            Assert.Equal(1, AudioPassthroughService.BtMicChannels);
        }

        // ── Whole-block submits, or the mic ring misaligns forever ──
        //
        // HM's SubmitMicSamples truncates to its free byte count, computed
        // as (capacity - 1 - buffered). The -1 makes that figure odd, so a
        // truncated submit ends mid-sample and every later read is one
        // byte off: the low byte becomes the high byte and quiet audio
        // arrives as a full-scale sawtooth. Measured on hardware by
        // submitting a known 440 Hz sine at amplitude 1000 and capturing
        // +0.0703 +0.5078 +0.9531 -0.6016 (steps of +0.445, wrapping)
        // instead of the sine. With the guard, the same tone captured
        // byte-exact at rms 0.0216.

        [Fact]
        public void MicSubmit_IsRefusedWhenAWholeBlockWouldNotFit()
        {
            const int block = 1920;   // one 10 ms stereo block at 48 kHz
            int cap = AudioPassthroughService.HmMicRingBytes;

            Assert.True(AudioPassthroughService.MicSubmitFits(0, block));
            Assert.True(AudioPassthroughService.MicSubmitFits(cap - 1 - block, block));

            // One byte tighter than a whole block: HM would copy a partial,
            // odd-length run and misalign the ring.
            Assert.False(AudioPassthroughService.MicSubmitFits(cap - block, block));
            Assert.False(AudioPassthroughService.MicSubmitFits(cap - 1, block));
            Assert.False(AudioPassthroughService.MicSubmitFits(cap, block));
        }

        [Fact]
        public void MicSubmit_NeverPermitsATruncationThatEndsMidFrame()
        {
            const int block = 1920;
            int cap = AudioPassthroughService.HmMicRingBytes;
            for (int buffered = 0; buffered <= cap; buffered += 7)
            {
                int free = cap - 1 - buffered;
                if (AudioPassthroughService.MicSubmitFits(buffered, block))
                    Assert.True(free >= block, $"buffered={buffered} admitted with free={free}");
            }
        }

        // ── Consumer-side verdicts (shared shape with tools/PersonaVerify) ──

        [Theory]
        [InlineData(0.0000, 0.0, "silence")]   // dead lane: muted pad, or nothing feeding it
        [InlineData(0.0002, 3.0, "silence")]
        [InlineData(0.5118, 1.95, "noise")]    // the measured stereo-decode failure
        [InlineData(0.5897, 1.69, "noise")]
        [InlineData(0.0208, 10.6, "audio")]    // the measured mono-decode success
        [InlineData(0.0346, 6.8, "audio")]
        [InlineData(0.3000, 8.0, "audio")]     // loud but peaky is real audio, not noise
        public void ClassifyCapture_SeparatesSilenceNoiseAndAudio(double rms, double crest, string expected)
        {
            Assert.Equal(expected, AudioPassthroughService.ClassifyCapture(rms, crest));
        }
    }
}
