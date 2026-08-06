using System;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The linear resampler behind the DS4 persona's rate conversion. The
    /// DS5 persona streams 48 kHz and matched the sink 1:1, which is why
    /// the missing conversion never showed there; the DS4 persona streams
    /// 32 kHz out and 16 kHz mic, so unconverted audio played 1.5x fast
    /// and a fifth sharp, and the mic path's rate-equality bail dropped
    /// every frame.
    /// </summary>
    public class PersonaResamplerTests
    {
        private static float[] Sine(int frames, int channels, double freqHz, int rateHz)
        {
            var buf = new float[frames * channels];
            for (int f = 0; f < frames; f++)
            {
                float v = (float)Math.Sin(2 * Math.PI * freqHz * f / rateHz);
                for (int c = 0; c < channels; c++) buf[f * channels + c] = v;
            }
            return buf;
        }

        private static int ZeroCrossings(float[] buf, int frames, int channels)
        {
            int n = 0;
            for (int f = 1; f < frames; f++)
                if ((buf[(f - 1) * channels] <= 0) != (buf[f * channels] <= 0)) n++;
            return n;
        }

        /// <summary>32 k -> 48 k stretches the frame count by exactly 3:2
        /// (within the one-frame carry the continuity state holds back).</summary>
        [Fact]
        public void UpsampleRatio_IsThreeToTwo()
        {
            var src = Sine(3200, 2, 440, 32000);
            var dst = new float[9700 * 2];
            double phase = 0; var carry = new float[2];
            int written = AudioPassthroughService.LinearResampleInterleaved(
                src, 3200, 2, dst, 32000.0 / 48000.0, ref phase, carry);
            Assert.InRange(written, 4798, 4802);   // 3200 * 1.5 = 4800
        }

        /// <summary>The pitch survives the conversion: a 440 Hz tone at
        /// 32 k resampled to 48 k still crosses zero at 440 Hz against the
        /// 48 k clock. This is the assertion the shipped bug fails: without
        /// resampling the same samples read at 48 k measure ~660 Hz.</summary>
        [Fact]
        public void UpsamplePreservesPitch()
        {
            int srcFrames = 32000;   // 1 s at 32 k
            var src = Sine(srcFrames, 2, 440, 32000);
            var dst = new float[(int)(srcFrames * 1.5) * 2 + 8];
            double phase = 0; var carry = new float[2];
            int written = AudioPassthroughService.LinearResampleInterleaved(
                src, srcFrames, 2, dst, 32000.0 / 48000.0, ref phase, carry);

            // ~1 s of output at 48 k: zero crossings ≈ 2 * 440.
            double seconds = written / 48000.0;
            double hz = ZeroCrossings(dst, written, 2) / seconds / 2.0;
            Assert.InRange(hz, 435, 445);

            // And the unconverted read really is a fifth sharp, the
            // reported symptom: same samples clocked at 48 k.
            double wrongHz = ZeroCrossings(src, srcFrames, 2) / (srcFrames / 48000.0) / 2.0;
            Assert.InRange(wrongHz, 655, 665);
        }

        /// <summary>Cross-call continuity: a stream split at an arbitrary
        /// point resamples identically to the contiguous buffer, so the
        /// pacing thread's window boundaries cannot click.</summary>
        [Fact]
        public void SplitCalls_MatchContiguousResult()
        {
            var src = Sine(1000, 2, 700, 32000);
            double step = 32000.0 / 48000.0;

            var whole = new float[1600 * 2];
            double p1 = 0; var c1 = new float[2];
            int wWhole = AudioPassthroughService.LinearResampleInterleaved(
                src, 1000, 2, whole, step, ref p1, c1);

            var parts = new float[1600 * 2];
            double p2 = 0; var c2 = new float[2];
            int cut = 337;
            int wA = AudioPassthroughService.LinearResampleInterleaved(
                src.AsSpan(0, cut * 2), cut, 2, parts, step, ref p2, c2);
            var tail = new float[1600 * 2];
            int wB = AudioPassthroughService.LinearResampleInterleaved(
                src.AsSpan(cut * 2), 1000 - cut, 2, tail, step, ref p2, c2);
            Array.Copy(tail, 0, parts, wA * 2, wB * 2);

            Assert.Equal(wWhole, wA + wB);
            for (int i = 0; i < wWhole * 2; i++)
                Assert.Equal(whole[i], parts[i], 5);
        }

        /// <summary>Downsampling (the DS4 mic direction, 48 k capture to
        /// the persona's 16 k) preserves pitch the same way.</summary>
        [Fact]
        public void DownsamplePreservesPitch()
        {
            int srcFrames = 48000;
            var src = Sine(srcFrames, 1, 300, 48000);
            var dst = new float[16010];
            double phase = 0; var carry = new float[1];
            int written = AudioPassthroughService.LinearResampleInterleaved(
                src, srcFrames, 1, dst, 48000.0 / 16000.0, ref phase, carry);
            Assert.InRange(written, 15998, 16002);
            double hz = ZeroCrossings(dst, written, 1) / (written / 16000.0) / 2.0;
            Assert.InRange(hz, 296, 304);
        }

        /// <summary>Windows volume writes arrive tagged with the persona's
        /// own feature-unit name: "speaker" on the DualSense family,
        /// "headset" on the DualShock 4. Both are output volume; matching
        /// only "speaker" is how the DS4's device volume did nothing.</summary>
        [Theory]
        [InlineData("speaker", true)]
        [InlineData("headset", true)]
        [InlineData("microphone", false)]
        [InlineData("unit7", false)]
        public void OutputVolumeFunction_CoversBothPersonaNames(string fn, bool expected)
        {
            Assert.Equal(expected, AudioPassthroughService.IsOutputVolumeFunction(fn));
        }

        /// <summary>Identity step is byte-faithful apart from the one-frame
        /// carry delay, so the 48 k DS5 path through the same code would be
        /// lossless. (The live code keeps the zero-copy path for equal
        /// rates; this pins that routing either way is safe.)</summary>
        [Fact]
        public void UnityStep_ReproducesTheInput()
        {
            var src = Sine(500, 2, 440, 48000);
            var dst = new float[520 * 2];
            double phase = 0; var carry = new float[2];
            int written = AudioPassthroughService.LinearResampleInterleaved(
                src, 500, 2, dst, 1.0, ref phase, carry);
            Assert.InRange(written, 499, 501);
            // First output frame interpolates carry(0)->src0 at phase 0 =
            // exactly carry; thereafter it walks the source one step behind.
            for (int f = 5; f < written; f++)
                Assert.Equal(src[(f - 1) * 2], dst[f * 2], 5);
        }
    }
}
