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
        private readonly double[] _scores; // per-lag autocorrelation, reused (no per-tick alloc)
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
            _scores = new double[(int)(rate / MinFreq) + 2]; // covers every lag up to maxLag
        }

        /// <summary>Clears the held pitch and the sample ring. Call when the sink
        /// goes idle (silence) so the next, unrelated sound does not inherit the
        /// previous one's pitch. Without it the 880 Hz Audio-tab test beep leaves
        /// _lastFreq at 880, and a macro played afterwards rings its unvoiced
        /// segments at 880 Hz instead of the 220 default -- a phantom high-frequency
        /// component that appears only after the test tone has played.</summary>
        public void Reset()
        {
            _filled = 0;
            _lastFreq = 220f;
            Array.Clear(_ring, 0, _ring.Length);
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

            // Pitch: normalized autocorrelation over the playable lag range, then
            // the FIRST local-maximum lag above the voiced threshold (not the
            // global max). The normalization divides by the fixed zero-lag energy
            // (sumSq), which suppresses octave-DOWN errors but biases the raw peak
            // toward the shortest lags: a low tone barely decorrelates over a few
            // samples, so the descent off the zero-lag plateau near minLag scores
            // ~1.0 and the global max would mis-report a near-maxFreq pitch. The
            // true fundamental is the first peak AFTER that descending plateau, so
            // walk up and take the first rise-then-fall above the threshold.
            int minLag = Math.Max(2, (int)(_rate / MaxFreq));
            int maxLag = Math.Min(len - 1, (int)(_rate / MinFreq));
            double energy = sumSq;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                double corr = 0;
                int m = len - lag;
                for (int i = 0; i < m; i++) corr += _ring[baseOff + i] * _ring[baseOff + i + lag];
                _scores[lag] = energy > 1e-9 ? corr / energy : 0;
            }
            for (int lag = minLag + 1; lag <= maxLag; lag++)
            {
                // The lowest detectable tone has its period at maxLag, so treat the
                // right edge as a falling neighbour to let a boundary peak count.
                double right = lag < maxLag ? _scores[lag + 1] : double.NegativeInfinity;
                if (_scores[lag] > VoicedThreshold && _scores[lag] >= _scores[lag - 1] && _scores[lag] > right)
                {
                    _lastFreq = (float)_rate / lag;
                    break;
                }
            }
            // No voiced local maximum: hold the last pitch (an unvoiced burst must
            // not jump the coil), with the true amplitude already computed above.
            return (_lastFreq, amp);
        }
    }
}
