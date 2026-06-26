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
        /// Note: the reference splits the 16-bit period and repeat values with
        /// <c>% 0xFF</c> / <c>/ 0xFF</c>, not <c>0x100</c> / <c>&gt;&gt; 8</c>. This
        /// is a real protocol quirk (the split desyncs from a clean low/high byte at
        /// byte boundaries), but it is confirmed byte-identical to the proven
        /// SteamControllerSinger main.cpp:129-134 on all six bytes (e.g. period
        /// 0x0466 -> LSB 0x67, which 0x100 would render 0x66). Do not "fix" it to
        /// 0x100; that would diverge from the only same-transport reference.</summary>
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
        //  Steam Deck (Jupiter, report 0xEA). Ground truth: SteamHapticsSinger
        //  main.cpp:279-294 (the device-specific Jupiter path).
        //  The Steam Controller 2026 (Triton) plays tones via the classic 0x8f
        //  TriggerHapticPulse FEATURE path (EncodeSteamClassic), the
        //  Bluetooth-working route proven by the cloned steam_controller_tools
        //  (WebHID sendFeatureReport) and SteamlessController (0x45 BLE state
        //  report). Its old 0x83 OUTPUT encoder was SteamHapticsSinger's USB-only
        //  note-player and never reached a BLE pad, so it was removed.
        // ─────────────────────────────────────────────────────────────

        /// <summary>Steam Deck (Jupiter) tone, report <c>0xEA</c>, 64-byte SET_FEATURE
        /// (control transfer in the reference). <paramref name="haptic"/> 0/1.
        /// Frequency is carried in Hz directly. Mirrors SteamHapticsSinger
        /// main.cpp:279-286, the device-specific Jupiter path.
        ///
        /// Reference choice (hypothesis-under-test on hardware): the two Steam
        /// references disagree on the Deck. SteamHapticsSinger drives it with this
        /// dedicated 0xEA Jupiter report; the older SteamControllerSinger drives the
        /// Deck (0x1205) through the generic 2015 0x8F path. PadForge follows the
        /// device-specific one (0xEA). The gain uses the same signed directVel
        /// mapping as Triton (amp=1 -> 0x7F, amp=0 -> 0x80), not the reference's
        /// 0x00 non-directVel default, because the sink feeds a continuous
        /// amplitude, not discrete note velocities. haptic is the trackpad channel
        /// only (the rumble-motor channel swap/table is out of this path's scope).</summary>
        public static byte[] EncodeSteamDeck(float freqHz, float amp, int durationMs = 0x7FFF, int haptic = 0)
        {
            var blob = new byte[64];
            blob[0] = 0xEA;
            blob[2] = (byte)(haptic == 0 ? 1 : 0); // !channel (main.cpp:280)
            blob[3] = 0x03;
            // Same velocity->gain mapping as Triton (main.cpp:282 directVel path),
            // signed: amp=1 -> 0x7F, amp=0 -> 0x80.
            int velDeck = (int)(amp * 127f);
            blob[5] = (byte)(velDeck * 255 / 127 - 128);
            int f = (int)freqHz;
            blob[6] = (byte)(f % 0xFF);
            blob[7] = (byte)(f / 0xFF);
            blob[8] = (byte)(durationMs % 0xFF);
            blob[9] = (byte)(durationMs / 0xFF);
            return blob;
        }

        // Switch 2 was dropped from the #147 tone scope. No reference plays an
        // audible tone on a Switch 2 actuator: switch2-controllers/controller.py
        // defines en_tone/lf_freq/hf_freq but never sets them, and the one PC
        // project that drives Switch 2 frequency (TommyWabg/switch2-controllers-
        // windows10-gyro) uses it to shape RUMBLE feel (bass_thump vs sharp_click),
        // never en_tone, never a melody. The gen-1 Joy-Con (joycon-singer) and
        // Steam (SteamControllerSinger) tone paths above are the grounded ones.
    }
}
