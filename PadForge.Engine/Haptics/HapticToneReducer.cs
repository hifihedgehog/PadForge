using System;

namespace PadForge.Engine.Haptics
{
    /// <summary>
    /// Reduces a stream of mono float audio to one (dominant frequency Hz,
    /// amplitude 0..1) per rumble tick. This is the PCM-to-tone step a single LRA
    /// needs (issue #147). A Switch HD Rumble coil or a Steam Controller actuator plays
    /// one tone with an amplitude envelope, not PCM, so the macro audio mix has to
    /// collapse to a (pitch, loudness) pair each tick before
    /// <see cref="HapticToneEncoder"/> turns it into wire bytes.
    ///
    /// Amplitude is windowed RMS. Pitch is the normalized autocorrelation peak over
    /// the playable lag range. This is standard DSP, not copied from a reference
    /// repo (the reference singers play discrete MIDI notes, not decoded audio).
    /// Deterministic and allocation-light so it is unit-testable against synthetic
    /// tones without hardware; how musical the reduction sounds on a real coil is
    /// the hardware-gated, hypothesis-under-test part.
    /// </summary>
    public sealed class HapticToneReducer
    {
        private readonly int _rate;
        private readonly float[] _ring;
        private int _filled;
        private float _lastFreq = 220f;

        // Detect down to ~40 Hz (Joy-Con LF floor 41) and up to ~1300 Hz (HF
        // ceiling 1252). The window must hold at least one full period of the
        // lowest detectable tone, so 83 ms at 8 kHz comfortably covers a 40 Hz
        // period (25 ms) with several cycles for a stable autocorrelation.
        private const float MinFreq = 40f;
        private const float MaxFreq = 1300f;

        // Below this RMS the window is treated as silence (no tone, amp 0).
        public const float SilenceRms = 0.02f;
        // Autocorrelation peak must clear this fraction of the zero-lag energy to
        // count as a voiced pitch; below it the last pitch is held so an unvoiced
        // burst does not jump the coil around.
        private const double VoicedThreshold = 0.30;

        public HapticToneReducer(int rate)
        {
            _rate = rate;
            _ring = new float[Math.Max(512, rate / 12)]; // ~83 ms ring
        }

        /// <summary>Appends one tick of samples (newest at the end of the ring)
        /// and returns the current (dominant frequency Hz, amplitude 0..1). When
        /// the window is near-silent or unvoiced the last detected pitch is held
        /// and the amplitude reports the true loudness (0 for silence).</summary>
        public (float Hz, float Amp) Push(float[] samples, int count)
        {
            int n = Math.Min(count, _ring.Length);
            if (n < _ring.Length)
                Array.Copy(_ring, n, _ring, 0, _ring.Length - n);
            Array.Copy(samples, count - n, _ring, _ring.Length - n, n);
            _filled = Math.Min(_filled + count, _ring.Length);

            int len = _filled;
            if (len < 64) return (_lastFreq, 0f);
            int baseOff = _ring.Length - len;

            // Amplitude: RMS over the window, lightly scaled. The macro
            // VolumeSampleProvider already applied the user's level, so this is a
            // straight loudness read. A full-scale sine RMS is ~0.707, so x1.4
            // brings a normal cue near the encoder's amplitude ceiling.
            double sumSq = 0;
            for (int i = 0; i < len; i++) { float v = _ring[baseOff + i]; sumSq += v * v; }
            float rms = (float)Math.Sqrt(sumSq / len);
            float amp = Math.Min(rms * 1.4f, 1.0f);
            if (amp < SilenceRms) return (_lastFreq, 0f);

            // Pitch: normalized autocorrelation peak over the playable lag range.
            int minLag = Math.Max(2, (int)(_rate / MaxFreq));
            int maxLag = Math.Min(len - 1, (int)(_rate / MinFreq));
            double energy = sumSq;
            float bestFreq = _lastFreq;
            double bestScore = 0;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                double corr = 0;
                int m = len - lag;
                for (int i = 0; i < m; i++) corr += _ring[baseOff + i] * _ring[baseOff + i + lag];
                double score = energy > 1e-9 ? corr / energy : 0;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestFreq = (float)_rate / lag;
                }
            }
            if (bestScore > VoicedThreshold) _lastFreq = bestFreq;
            return (_lastFreq, amp);
        }
    }
}
