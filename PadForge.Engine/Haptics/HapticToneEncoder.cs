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
        //  Steam Controller 2026 / Triton (OUTPUT report 0x83, 10 bytes)
        //
        //  The Triton does NOT speak the 2015 classic 0x8f TriggerHapticPulse
        //  FEATURE report. Two independent references agree: Valve's own SDL
        //  driver (SDL_hidapi_steam_triton.c) drives its actuators only through
        //  OUTPUT reports and never sends 0x8f, and OpenPuck (haptics.cpp, whose
        //  comments are marked "CONFIRMED from real Windows USB captures of the
        //  Valve puck") uses output reports 0x80 (rumble) / 0x82 (click) / 0x87
        //  (settings), also never 0x8f.
        //
        //  The Triton's tone generator is ID_OUT_REPORT_HAPTIC_LFO_TONE = 0x83
        //  (SDL steam/controller_structs.h:229), payload MsgHapticLfoTone
        //  (controller_structs.h:193-202, #pragma pack(1)):
        //    side u8 | gain_db i8 | frequency u16 | duration_ms u16 |
        //    lfo_freq u16 | lfo_depth u8        -> 9 bytes + 1 report-id = 10
        //    (HID_HAPTIC_LFO_TONE_REPORT_BYTES, the #def includes the OR id).
        //  The "side" byte is the actuator index 0,1,3,4 (not a bitmask), per
        //  SteamHapticsSinger main.cpp:255-259. The byte layout is reference-grounded
        //  and runtime-confirmed audible. The remaining open part is per-actuator
        //  tuning fidelity, not whether it makes sound.
        // ─────────────────────────────────────────────────────────────

        /// <summary>The Triton's four addressable LRAs, by actuator index. Per
        /// SteamHapticsSinger main.cpp:255-259, the 4 channels map to haptic ids
        /// 0,1,3,4 (the 0x83 byte-1 field): 0/1 = trackpads, 3/4 = grips. (Index 2
        /// is skipped by the reference's <c>haptic + (haptic &gt;&gt; 1)</c> map.)</summary>
        public static readonly int[] TritonActuators = { 0, 1, 3, 4 };
        public static bool TritonIsGrip(int haptic) => haptic > 2;

        /// <summary>Grip-LRA drive-frequency correction. SteamHapticsSinger's grip
        /// table (midiFrequencyRb, main.cpp:36) is a constant 1.0205x the real note
        /// frequency across the musical range (e.g. A4 440 Hz -&gt; Rb 449), while the
        /// trackpad table (midiFrequencyTr) is ~= the real Hz. So a grip needs ~2%
        /// higher drive than a trackpad to sound the same pitch; without it the grips
        /// run ~0.35 semitone flat and beat against the trackpads.</summary>
        public const float TritonGripFreqScale = 1.0205f;

        /// <summary>Encodes one (frequency Hz, amplitude 0..1) tone for ONE Triton
        /// actuator into the 10-byte <c>0x83</c> LFO-tone OUTPUT report, byte for
        /// byte against SteamHapticsSinger's Triton path (main.cpp:252-285):
        /// byte1 = actuator index, byte2 = <c>gain_db</c> (signed int8 dB), freq
        /// u16 LE, duration 0x7FFF = sustain. amp&lt;=0 or freq&lt;=0 emits the
        /// reference stop form (byte2 = 0x80 silent, byte6 = 0x80). Drive every
        /// actuator with its own report. The firmware addresses one LRA per command.
        ///
        /// gain_db is dB, NOT a linear velocity. SteamHapticsSinger's proven default
        /// (directVel = false, main.cpp:51) sends the per-note gain CURVE, and the
        /// shipped Triton curves are all zero (DEFAULT_GAIN = 0, main.cpp:24) -- so
        /// the audible reference level is 0 dB = unity. amp tracks the envelope BELOW
        /// 0 dB (20*log10(amp)), never positive. Positive dB drives the firmware
        /// limiter into harsh clipping. The directVel velocity*255/127-128 path, which
        /// hits +127, is a non-default experimental flag, not the song-playing one.</summary>
        public static byte[] EncodeTritonTone(int haptic, float freqHz, float amp)
        {
            var b = new byte[10];
            b[0] = 0x83;              // ID_OUT_REPORT_HAPTIC_LFO_TONE
            b[1] = (byte)haptic;      // actuator index (0,1,3,4)

            if (amp <= 0f || freqHz <= 0f)
            {
                b[2] = 0x80;          // gain -128 = silent (reference stop form)
                b[6] = 0x80;
                return b;
            }

            if (amp > 1f) amp = 1f;
            b[2] = (byte)(sbyte)AmpToGainDb(amp);   // 0 dB at amp=1, floored at -40

            // Frequency: the SDL struct (controller_structs.h MsgHapticLfoTone) is a
            // uint16 LE in Hz. Use exact LE (the firmware reads LE). The reference's
            // freq%0xFF / freq/0xFF split is a propagated SteamControllerSinger quirk
            // that is ~1 Hz off and inaudible, so the exact form is preferred.
            // Grips need the +2% drive correction so they sound the same pitch as the
            // trackpads (midiFrequencyRb vs midiFrequencyTr, main.cpp:36-37).
            float driveHz = TritonIsGrip(haptic) ? freqHz * TritonGripFreqScale : freqHz;
            ushort f = driveHz > 65535f ? (ushort)65535 : (ushort)driveHz;
            b[3] = (byte)(f & 0xFF);
            b[4] = (byte)(f >> 8);

            b[5] = 0xFF; b[6] = 0x7F; // duration 0x7FFF = sustain (re-armed on note edges)
            b[7] = 0; b[8] = 0;       // lfo_freq = 0
            b[9] = 0;                 // lfo_depth = 0 (pure tone, no tremolo)
            return b;
        }

        /// <summary>Maps amplitude 0..1 to the Steam haptic <c>gain_db</c> field: 0 dB
        /// at amp=1 (unity, the proven reference ceiling), 20*log10(amp) below, floored
        /// at -40 dB. Never positive -- positive dB drives the firmware limiter into
        /// clipping. Shared by the Triton (0x83) and Deck (0xEA) gain bytes.</summary>
        public static int AmpToGainDb(float amp)
        {
            if (amp <= 0f) return -128;          // silent
            if (amp > 1f) amp = 1f;
            double db = 20.0 * Math.Log10(amp);  // amp=1 -> 0 dB
            if (db > 0.0) db = 0.0;
            if (db < -40.0) db = -40.0;
            return (int)Math.Round(db, MidpointRounding.AwayFromZero);
        }

        // ─────────────────────────────────────────────────────────────
        //  Steam Deck (Jupiter, report 0xEA). Ground truth: SteamHapticsSinger
        //  main.cpp:279-294 (the device-specific Jupiter path).
        // ─────────────────────────────────────────────────────────────

        /// <summary>Steam Deck (Jupiter) tone, report <c>0xEA</c>, 64-byte SET_FEATURE.
        /// <paramref name="haptic"/> is the channel 0/1, both driven. amp&lt;=0 or
        /// freq&lt;=0 emits the reference NOTE_STOP form (gain 0x80, byte9 0x80). The
        /// active form, byte for byte against SteamHapticsSinger main.cpp:287-305:
        /// byte2 = !channel, byte3 = 0x03, byte5 = gain_db (AmpToGainDb, 0 dB unity,
        /// NOT the +127 velocity slam), byte6:7 = freq %0xFF / /0xFF, byte8:9 = 0xFF/0x7F
        /// (0x7FFF sustain, hardcoded, not a %0xFF split).
        ///
        /// THIS PATH IS THE LEAST REFERENCE-GROUNDED, unresolved without Deck hardware.
        /// Three references disagree. SteamControllerSinger drives the Deck (0x1205,
        /// main.cpp:58) via the COMPLETE generic 0x8F path with a working period
        /// frequency. SteamHapticsSinger drives it via this 0xEA Jupiter report but left
        /// its freq table a stub (midiFrequencyDk = {440,0,0...}, main.cpp:35), so its
        /// 0xEA frequency was never real. SDL's own Deck driver (SDL_hidapi_steamdeck.c:
        /// 429) uses 0xEB SimpleRumbleCmd (amplitude rumble, NOT tones), so it cannot
        /// break the 0xEA-vs-0x8F tie. PadForge sends raw Hz over 0xEA (the
        /// device-specific report), but the choice is a coin flip until a Deck is tested.</summary>
        public static byte[] EncodeSteamDeck(float freqHz, float amp, int haptic = 0)
        {
            var blob = new byte[64];
            blob[0] = 0xEA;
            blob[2] = (byte)(haptic == 0 ? 1 : 0); // !channel (main.cpp:291/298)
            blob[3] = 0x03;
            if (amp <= 0f || freqHz <= 0f)
            {
                // NOTE_STOP (main.cpp:289-294): gain 0x80 silent, byte9 0x80.
                blob[5] = 0x80;
                blob[9] = 0x80;
                return blob;
            }
            // gain_db (dB): 0 dB at amp=1 = unity, the proven reference level. The
            // shipped Jupiter gain curve is all zero (DEFAULT_GAIN = 0); +N dB would
            // clip the limiter. (directVel velocity*255/127-128 at main.cpp:300 is the
            // non-default flag.) Same mapping as the Triton 0x83 gain byte.
            blob[5] = (byte)(sbyte)AmpToGainDb(amp);
            int f = freqHz > 65535f ? 65535 : (int)freqHz;
            blob[6] = (byte)(f % 0xFF);            // freq %0xFF / /0xFF (main.cpp:301-302)
            blob[7] = (byte)(f / 0xFF);
            blob[8] = 0xFF; blob[9] = 0x7F;        // duration 0x7FFF = sustain (hardcoded, main.cpp:303-304)
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
