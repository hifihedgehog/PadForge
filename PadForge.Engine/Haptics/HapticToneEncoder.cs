using System;

namespace PadForge.Engine.Haptics
{
    /// <summary>
    /// Pure tone-encoder cores for issue #147 (HD haptic tones through Nintendo
    /// Switch HD Rumble and Steam Controller actuators). These turn a (frequency,
    /// amplitude/duration) request into the exact wire bytes each device's single
    /// LRA expects. They are deterministic and side-effect free so they can be
    /// unit-tested against the reference implementations without hardware.
    ///
    /// The streaming sink that pages a decoded-audio tone series through these per
    /// rumble tick (the AudioPassthroughService extension) is the remaining,
    /// hardware-gated part of M1; these cores are its foundation.
    ///
    /// Fidelity ceiling: a single actuator plays a tone with an amplitude
    /// envelope, not PCM. Beeps, alerts, and melodic cues land. Speech/music do
    /// not. Bytes are facts from BSD/MIT/unlicensed references; the C# is original.
    /// </summary>
    public static class HapticToneEncoder
    {
        // ─────────────────────────────────────────────────────────────
        //  Joy-Con / Switch Pro HD Rumble (output report 0x10 payload)
        //  Ground truth: joycon-singer rumble.h encode_rumble(), cross-
        //  checked against Nintendo_Switch_Reverse_Engineering
        //  rumble_data_table.md. Closed-form log2 encoding.
        // ─────────────────────────────────────────────────────────────

        public const float JoyConFreqMin = 41.0f;
        public const float JoyConFreqMax = 626.0f;
        public const float JoyConAmpMax = 0.8f;

        /// <summary>Neutral (silent) 4-byte rumble half: {0x00,0x01,0x40,0x40}
        /// (RUMBLE_NEUTRAL, rumble.h:10). The unused half of a single Joy-Con's
        /// packet carries this.</summary>
        public static byte[] JoyConNeutral() => new byte[] { 0x00, 0x01, 0x40, 0x40 };

        /// <summary>Octave-folds a frequency into the LF band [41, 626] Hz so an
        /// out-of-range note keeps its pitch class instead of clipping
        /// (fold_frequency, rumble.h:27-32).</summary>
        public static float FoldJoyConFrequency(float freq)
        {
            // NaN and infinity have no pitch class. Fold them to the band floor:
            // +Infinity would otherwise spin forever (Inf * 0.5f == Inf keeps
            // the upper loop's condition true), and NaN slips past every
            // comparison (all are unordered-false) and would propagate a
            // non-finite value into MathF.Log2 and a garbage byte. The reference
            // fold_frequency (rumble.h:27-32) has the same latent +Inf spin; the
            // guard is the deliberate divergence that keeps it bounded.
            if (float.IsNaN(freq) || float.IsInfinity(freq) || freq <= 0.0f) return JoyConFreqMin;
            while (freq < JoyConFreqMin) freq *= 2.0f;
            while (freq > JoyConFreqMax) freq *= 0.5f;
            return freq;
        }

        /// <summary>Encodes one (frequency Hz, amplitude 0..1) tone into the
        /// 4-byte Joy-Con rumble group. Mirrors encode_rumble (rumble.h:87-113)
        /// byte for byte.</summary>
        public static byte[] EncodeJoyConRumble(float freqHz, float amp)
        {
            freqHz = FoldJoyConFrequency(freqHz);
            if (amp < 0.0f) amp = 0.0f;
            if (amp > JoyConAmpMax) amp = JoyConAmpMax;

            // Frequency: enc_freq = round(log2(freq/10) * 32). Computed in
            // float32 with MathF.Log2 and MathF.Round, NOT Math.Log2 (double),
            // to be bit-faithful to the proven-on-hardware reference
            // rumble.h:95 (roundf(log2f(...) * 32.0f), all float). A double log2
            // of the same input diverges from the reference by one enc_freq step
            // at a thin set of boundary frequencies, shifting the wire pitch one
            // quantization step. MidpointRounding.AwayFromZero matches C roundf
            // (round half away from zero, not C# default banker's ToEven).
            byte encFreq = (byte)MathF.Round(MathF.Log2(freqHz / 10.0f) * 32.0f, MidpointRounding.AwayFromZero);
            ushort hf = (ushort)((encFreq - 0x60) * 4);
            byte lf = (byte)(encFreq - 0x40);

            // Amplitude: two log segments, dead below 0.12. The second segment
            // log2(amp*17)*16 is only non-negative for amp >= 1/17 (~0.0588),
            // so the threshold MUST sit above that or a quiet tone computes a
            // negative enc_amp that the (byte) cast wraps to a near-max value
            // (quietest tones play loudest). rumble.h:103 uses 0.012f, but that
            // value lands inside the negative-garbage band; the
            // Nintendo_Switch_Reverse_Engineering rumble_data_table.md:30 value
            // 0.12f is authoritative for this threshold because it is the one
            // that keeps the formula in its valid domain. The float32 path
            // itself still mirrors rumble.h:102/104. The Max(0, .) is a
            // belt-and-suspenders clamp so a negative round can never wrap.
            byte encAmp = 0;
            if (amp > 0.23f)
                encAmp = (byte)Math.Max(0f, MathF.Round(MathF.Log2(amp * 8.7f) * 32.0f, MidpointRounding.AwayFromZero));
            else if (amp > 0.12f)
                encAmp = (byte)Math.Max(0f, MathF.Round(MathF.Log2(amp * 17.0f) * 16.0f, MidpointRounding.AwayFromZero));

            ushort hfAmp = (ushort)(encAmp * 2);
            byte lfAmp = (byte)((encAmp >> 1) + 0x40);

            return new byte[]
            {
                (byte)(hf & 0xFF),
                (byte)(((hf >> 8) & 0xFF) | (hfAmp & 0xFF)),
                (byte)(lf | ((lfAmp << 7) & 0x80)),
                (byte)(lfAmp >> 1),
            };
        }

        /// <summary>Converts a MIDI note number to its frequency in Hz
        /// (midi_note_to_freq, rumble.h). Convenience for melodic cues.</summary>
        public static float MidiNoteToFrequency(int note)
            => 440.0f * (float)Math.Pow(2.0, (note - 69) / 12.0);

        // ─────────────────────────────────────────────────────────────
        //  Steam Controller 2015 (feature report 0x8F, 64-byte blob)
        //  Ground truth: SteamControllerSinger main.cpp:100-134.
        // ─────────────────────────────────────────────────────────────

        /// <summary>Period-to-command scale (STEAM_CONTROLLER_MAGIC_PERIOD_RATIO,
        /// main.cpp:14). periodCommand = period_seconds * this.</summary>
        public const double SteamMagicPeriodRatio = 495483.0;

        /// <summary>Encodes a square-wave tone for the Steam Controller 2015
        /// haptic into the 64-byte <c>0x8F</c> feature blob. <paramref name="haptic"/>
        /// 0 = right, 1 = left. <paramref name="durationSeconds"/> &lt; 0 sustains
        /// (repeat 0x7FFF). Mirrors SteamController_PlayNote.
        ///
        /// Note (hypothesis-under-test): the reference splits the 16-bit period and
        /// repeat values with <c>% 0xFF</c> / <c>/ 0xFF</c>, not <c>0x100</c>. That
        /// is reproduced verbatim because it is the proven-on-hardware code, but it
        /// reads like an off-by-one LSB/MSB split. A hardware pass should confirm
        /// whether 0x100 sounds truer; until then the proven bytes win.</summary>
        public static byte[] EncodeSteamClassic(float freqHz, double durationSeconds, int haptic = 0)
        {
            var blob = new byte[64];
            blob[0] = 0x8F;
            blob[1] = 0x07;

            // NOTE_STOP: the reference does not zero the blob. It sets note 0
            // (midiFrequency[0] = 8.1758 Hz) and duration 0, then falls through
            // the same encode path, so the note-0 period is still written and
            // only the repeat count is zeroed (main.cpp:114-134). Reproduce that
            // exactly: a stop is "this tone, repeated zero times", not a blank
            // packet. Use the same 8.1758 Hz note-0 frequency.
            if (freqHz <= 0f)
            {
                freqHz = 8.1758f;
                durationSeconds = 0.0;
            }

            double period = 1.0 / freqHz;
            ushort periodCommand = (ushort)(period * SteamMagicPeriodRatio);
            ushort repeatCount = durationSeconds >= 0.0
                ? (ushort)(durationSeconds / period)
                : (ushort)0x7FFF;

            blob[2] = (byte)haptic;
            blob[3] = (byte)(periodCommand % 0xFF); // LSB pulse-high
            blob[4] = (byte)(periodCommand / 0xFF); // MSB pulse-high
            blob[5] = (byte)(periodCommand % 0xFF); // LSB pulse-low (square: same period)
            blob[6] = (byte)(periodCommand / 0xFF); // MSB pulse-low
            blob[7] = (byte)(repeatCount % 0xFF);   // LSB repeat
            blob[8] = (byte)(repeatCount / 0xFF);   // MSB repeat
            return blob;
        }

        // ─────────────────────────────────────────────────────────────
        //  Steam Controller 2026 (Triton, report 0x83) + Steam Deck
        //  (Jupiter, report 0xEA). Ground truth: SteamHapticsSinger
        //  main.cpp:34-36 (tables), 244-294 (report framing).
        // ─────────────────────────────────────────────────────────────

        // MIDI note -> Hz (SteamHapticsSinger main.cpp:34). Used to map a
        // continuous frequency to the nearest note for the Triton command tables.
        private static readonly double[] MidiHz =
        {
            0,8.662,9.177,9.723,10.301,10.913,11.562,12.250,12.978,13.750,14.568,15.434,16.352,17.324,18.354,19.445,20.602,21.827,23.125,24.500,25.957,27.500,29.135,30.868,32.703,34.648,36.708,38.891,41.203,43.654,46.249,48.999,51.913,55.000,58.270,61.735,65.406,69.296,73.416,77.782,82.407,87.307,92.499,97.999,103.826,110.000,116.541,123.471,130.813,138.591,146.832,155.563,164.814,174.614,184.997,195.998,207.652,220.000,233.082,246.942,261.626,277.183,293.665,311.127,329.628,349.228,369.994,391.995,415.305,440.000,466.164,493.883,523.251,554.365,587.330,622.254,659.255,698.456,739.989,783.991,830.609,880.000,932.328,987.767,1046.502,1108.731,1174.659,1244.508,1318.510,1396.913,1479.978,1567.982,1661.219,1760.000,1864.655,1975.533,2093.005,2217.461,2349.318,2489.016,2637.020,2793.826,2959.955,3135.963,3322.438,3520.000,3729.310,3951.066,4186.009,4434.922,4698.636,4978.032,5274.041,5587.652,5919.911,6271.927,6644.875,7040.000,7458.620,7902.133,8372.018,8869.844,9397.273,9956.063,10548.082,11175.303,11839.822,12543.854
        };
        // MIDI note -> Triton trackpad command value (main.cpp:35).
        private static readonly ushort[] TritonTrackpad =
        {
            0,10,10,11,11,12,13,13,14,15,16,16,17,18,19,20,22,23,24,25,27,29,30,32,34,36,38,40,42,45,47,50,53,56,59,63,66,70,75,80,84,89,94,100,107,113,120,126,134,142,151,160,169,179,189,200,213,226,239,253,267,283,300,318,336,357,377,399,423,449,477,505,535,566,598,636,674,713,756,800,848,898,951,1008,1068,1131,1199,1270,1345,1425,1510,1600,1693,1792,1897,2008,2125,2249,2381,2521,2669,2826,2992,3168,3354,3552,3761,3983,4218,4467,4731,5010,5306,5620,5952,6304,6677,7072,7491,7934,8404,8902,9429,9988,10580,11207,11872,12576
        };

        /// <summary>Nearest MIDI note to a frequency, for the Triton tables.</summary>
        private static int NearestMidiNote(float freqHz)
        {
            int best = 69; double bestErr = double.MaxValue;
            for (int n = 1; n < MidiHz.Length; n++)
            {
                double e = Math.Abs(MidiHz[n] - freqHz);
                if (e < bestErr) { bestErr = e; best = n; }
            }
            return best;
        }

        /// <summary>Steam Controller 2026 (Triton) tone, report <c>0x83</c>, 65-byte
        /// hid_write. <paramref name="haptic"/> is the channel (0/1 trackpads).
        /// Mirrors SteamHapticsSinger main.cpp:258-266. The frequency command comes
        /// from the per-haptic calibration table (Hz mapped to the nearest note).</summary>
        public static byte[] EncodeSteam2026(float freqHz, float amp, int haptic = 0)
        {
            var blob = new byte[65];
            blob[0] = 0x83;
            blob[1] = (byte)haptic;
            // gain: reference default 0xFE; scale the reducer amplitude into the
            // gain byte so a quiet cue is quieter (directVel path, main.cpp:261).
            blob[2] = (byte)Math.Clamp((int)(amp * 0xFE), 0, 0xFE);
            ushort cmd = TritonTrackpad[NearestMidiNote(freqHz)];
            blob[3] = (byte)(cmd % 0xFF);
            blob[4] = (byte)(cmd / 0xFF);
            blob[5] = 0xFF;
            blob[6] = 0x7F;
            return blob;
        }

        /// <summary>Steam Controller 2026 note-off, report <c>0x82</c>
        /// (main.cpp:254-257). Stops the tone without rebooting the pad.</summary>
        public static byte[] EncodeSteam2026Stop(int haptic = 0)
        {
            var blob = new byte[65];
            blob[0] = 0x82;
            blob[1] = (byte)haptic;
            return blob;
        }

        /// <summary>Steam Deck (Jupiter) tone, report <c>0xEA</c>, 64-byte SET_FEATURE
        /// (control transfer in the reference). <paramref name="haptic"/> 0/1.
        /// Frequency is carried in Hz directly. Mirrors main.cpp:279-286.</summary>
        public static byte[] EncodeSteamDeck(float freqHz, float amp, int durationMs = 0x7FFF, int haptic = 0)
        {
            var blob = new byte[64];
            blob[0] = 0xEA;
            blob[2] = (byte)(haptic == 0 ? 1 : 0); // !channel (main.cpp:280)
            blob[3] = 0x03;
            blob[5] = (byte)Math.Clamp((int)(amp * 0xFF), 0, 0xFF);
            int f = (int)freqHz;
            blob[6] = (byte)(f % 0xFF);
            blob[7] = (byte)(f / 0xFF);
            blob[8] = (byte)(durationMs % 0xFF);
            blob[9] = (byte)(durationMs / 0xFF);
            return blob;
        }

        // ─────────────────────────────────────────────────────────────
        //  Switch 2 HD Rumble (5-byte pack). Ground truth: SDL fork
        //  SDL_hidapi_switch2.c EncodeHDRumble (line 1106-1113).
        // ─────────────────────────────────────────────────────────────

        // The Switch 2 carrier is FIXED by the driver init (rumble_hi_freq 0x187,
        // rumble_lo_freq 0x112, SDL_hidapi_switch2.c:689-690); no reference exposes
        // a Switch 2 frequency encoding, so a Switch 2 "tone" can only modulate
        // amplitude at that fixed carrier (a fixed-pitch buzz envelope, honestly
        // not a pitch-varying tone like the Joy-Con gen-1 / Steam paths).
        public const ushort Switch2HiFreq = 0x187;
        public const ushort Switch2LoFreq = 0x112;

        /// <summary>Packs one HD-rumble group into 5 bytes for the Switch 2 output
        /// report, at the fixed carrier with the given amplitude (0..1). Bit layout
        /// from EncodeHDRumble (SDL_hidapi_switch2.c:1106-1113).</summary>
        public static byte[] EncodeSwitch2HD(float amp)
        {
            if (amp < 0f) amp = 0f; if (amp > 1f) amp = 1f;
            ushort a = (ushort)(amp * ushort.MaxValue);
            ushort hi = Switch2HiFreq, lo = Switch2LoFreq, hiA = a, loA = a;
            return new byte[]
            {
                (byte)(hi & 0xFF),
                (byte)(((hiA >> 4) & 0xfc) | ((hi >> 8) & 0x03)),
                (byte)((hiA >> 12) | (lo << 4)),
                (byte)((loA & 0xc0) | ((lo >> 4) & 0x3f)),
                (byte)(loA >> 8),
            };
        }
    }
}
