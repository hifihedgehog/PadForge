using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PadForge.Common.Input;

namespace PadForge.Tests
{
    /// <summary>Pins the frequency response of the 48 kHz to 8 kHz
    /// haptic-tone downmix (owner-reported ladder, 2026-07-10): NAudio's
    /// stock WdlResamplingSampleProvider runs interpolation plus two IIR
    /// anti-alias passes at 0.693x the target Nyquist (2772 Hz here), which
    /// measured -0.9 dB at 1600 Hz, -8.9 dB at 3200 Hz, and -29 dB at
    /// 6400 Hz. That made the #202 fold progressively quieter on exactly
    /// the high tones it exists to rescue. The sinc-mode provider keeps the
    /// passband flat to the tone domain's edge and puts above-Nyquist
    /// content in a real stopband.</summary>
    public class ToneDownmixTests
    {
        private sealed class SineProvider : ISampleProvider
        {
            private readonly double _freq;
            private long _n;
            public SineProvider(double freq) { _freq = freq; }
            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
            public int Read(float[] buffer, int offset, int count)
            {
                for (int i = 0; i < count; i++)
                    buffer[offset + i] = (float)Math.Sin(2 * Math.PI * _freq * _n++ / 48000.0);
                return count;
            }
        }

        /// <summary>Output RMS of a full-scale 48 kHz sine after the sinc
        /// downmix to 8 kHz, with the kernel's warm-up skipped.</summary>
        private static double DownmixRms(double freq)
        {
            var rs = new SincResamplingSampleProvider(new SineProvider(freq), 8000);
            var buf = new float[8000];
            rs.Read(buf, 0, 800); // discard the first 100 ms (kernel warm-up)
            int got = rs.Read(buf, 0, 8000);
            Assert.True(got > 4000);
            double sum = 0;
            for (int i = 0; i < got; i++) sum += (double)buf[i] * buf[i];
            return Math.Sqrt(sum / got);
        }

        [Theory]
        [InlineData(1600.0)]
        [InlineData(3200.0)]
        public void Passband_Is_Flat_Relative_To_800_Hz(double freq)
        {
            // Within half a dB of the 800 Hz reference. The stock IIR mode
            // fails this at both points (-0.9 dB and -8.9 dB).
            double reference = DownmixRms(800.0);
            double ratio = DownmixRms(freq) / reference;
            Assert.InRange(ratio, 0.94, 1.06);
        }

        [Fact]
        public void Above_Nyquist_Is_Stopband_Not_Skirt()
        {
            // 6400 Hz is above the 4 kHz output Nyquist: it must be removed
            // (aliasing suppressed), not merely attenuated. A full-scale
            // sine's RMS is ~0.707; require at least ~29 dB down.
            Assert.True(DownmixRms(6400.0) < 0.025);
        }
    }
}
