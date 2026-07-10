using NAudio.Dsp;
using NAudio.Wave;

namespace PadForge.Common.Input
{
    /// <summary>
    /// WDL resampler in windowed-sinc mode. NAudio's own
    /// WdlResamplingSampleProvider hardcodes SetMode(true, 2, false)
    /// (linear interpolation plus two IIR anti-alias passes), and for a
    /// downsample those biquads sit at 0.693x the target Nyquist
    /// (WdlResampler.cs: m_filterpos 0.693, setParms fpos*PI). For the
    /// 48 kHz to 8 kHz haptic-tone downmix that is a 4th-order lowpass at
    /// 2772 Hz: measured -0.9 dB at 1600 Hz, -8.9 dB at 3200 Hz, -29 dB at
    /// 6400 Hz, which made high tones fold down progressively quieter
    /// (owner-reported ladder, 2026-07-10). Sinc mode instead builds a
    /// 64-tap Blackman-Harris kernel cut at outputNyquist/1.03
    /// (WdlResampler.cs:279, BuildLowPass), so the passband stays flat to
    /// the edge of the tone domain and above-Nyquist content lands in a
    /// real stopband instead of a slow skirt.
    ///
    /// Body mirrors NAudio's WdlResamplingSampleProvider
    /// (NAudio.Core/Wave/SampleProviders/WdlResamplingSampleProvider.cs,
    /// v2.2.1) with only the mode call changed.
    /// </summary>
    internal sealed class SincResamplingSampleProvider : ISampleProvider
    {
        private readonly WdlResampler _resampler;
        private readonly WaveFormat _outFormat;
        private readonly ISampleProvider _source;
        private readonly int _channels;

        public SincResamplingSampleProvider(ISampleProvider source, int newSampleRate)
        {
            _channels = source.WaveFormat.Channels;
            _outFormat = WaveFormat.CreateIeeeFloatWaveFormat(newSampleRate, _channels);
            _source = source;

            _resampler = new WdlResampler();
            // Sinc overrides interp/filtercnt (WdlResampler.SetMode). The
            // kernel length sets the transition width (Blackman-Harris
            // mainlobe: ~8 x inputRate / taps). WDL's default 64 taps gives
            // a ~6 kHz transition at 48 kHz input, which still drooped
            // -2 dB at 3.2 kHz; 512 taps narrows it to ~750 Hz so the
            // passband is flat to ~3.5 kHz against the 3883 Hz cutoff.
            // Cost at 8 kHz mono output is ~4M MACs/s, comparable to the
            // pitch autocorrelation itself.
            _resampler.SetMode(false, 0, true, 512, 32);
            _resampler.SetFeedMode(false); // output driven
            _resampler.SetRates(source.WaveFormat.SampleRate, newSampleRate);
        }

        public WaveFormat WaveFormat => _outFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int framesRequested = count / _channels;
            int inNeeded = _resampler.ResamplePrepare(framesRequested, _outFormat.Channels,
                out float[] inBuffer, out int inBufferOffset);
            int inAvailable = _source.Read(inBuffer, inBufferOffset, inNeeded * _channels) / _channels;
            int outAvailable = _resampler.ResampleOut(buffer, offset, inAvailable, framesRequested, _channels);
            return outAvailable * _channels;
        }
    }
}
