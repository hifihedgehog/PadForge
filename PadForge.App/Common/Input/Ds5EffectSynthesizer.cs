using System;
using PadForge.ViewModels;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Builds DualSense USB Report 0x02 effect messages from a
    /// <see cref="PlayStationSlotConfig"/>. Wire format reference is the
    /// DS5 SDK's 47-byte <c>DS5EffectsState_t</c> layout — same form
    /// that game-driven effect output uses (which the
    /// <see cref="DualSensePassthroughDispatcher"/> forwards verbatim).
    ///
    /// <para>Standard 47-byte payload, written into the caller's buffer:</para>
    /// <code>
    /// [0..1]   enable_bits  (u16 LE — which feature blocks this packet updates)
    /// [2]      rumble_right
    /// [3]      rumble_left
    /// [4]      audio enable / volume
    /// [5]      mic light / audio mute
    /// [6..16]  right_trigger_effect (mode + 10 param bytes)
    /// [17..27] left_trigger_effect  (mode + 10 param bytes)
    /// [28..33] reserved
    /// [34..38] led_flags / led_animation / led_brightness / pad_lights / placeholder
    /// [39..41] led_red / led_green / led_blue
    /// [42..46] reserved
    /// </code>
    ///
    /// <para>Feature B writes don't drive rumble (game writes own that
    /// surface; the existing rumble pipeline still feeds non-DS5
    /// targets). Adaptive trigger and lightbar bytes are the meaningful
    /// payload here.</para>
    /// </summary>
    internal static class Ds5EffectSynthesizer
    {
        /// <summary>Length of the standard DualSense USB output report
        /// payload (excluding the Report ID prefix that SDL prepends).</summary>
        public const int PayloadSize = 47;

        // enable_bits flags from the DS5 effect message header.
        private const ushort EnableRumbleAndHaptic = 0x0001 | 0x0002; // rumble L/R
        private const ushort EnableRightTrigger = 0x0004;
        private const ushort EnableLeftTrigger = 0x0008;
        private const ushort EnableAudioVolume = 0x0010;
        private const ushort EnableMicLight = 0x0100;
        private const ushort EnableLedColor = 0x0004 << 6; // bit 8 = 0x0100, but reserve LED bit 0x0400
        // Sony's actual LED enable bit on byte 1: 0x04 (i.e. 0x0400 in the u16).
        // Use a clearer constant for readability:
        private const ushort EnableLightbar = 0x0400;

        /// <summary>Builds a single DS5 effect packet from the user's
        /// configuration into <paramref name="dst"/>. Returns the number
        /// of bytes written (always <see cref="PayloadSize"/>).
        /// <paramref name="dst"/> must be at least <c>PayloadSize</c>
        /// bytes. The buffer is fully written; no need to zero it
        /// beforehand.</summary>
        public static int Build(PlayStationSlotConfig cfg, Span<byte> dst)
        {
            if (cfg == null) return 0;
            if (dst.Length < PayloadSize) return 0;

            dst.Slice(0, PayloadSize).Clear();

            ushort enableBits = 0;

            // Lightbar.  Always assert when LightbarEnabled so Feature B
            // pushes the configured color even when it's all zeros (i.e.
            // user wants the bar dark).  The game-driven path writes its
            // own enable bit when applicable; this synthesizer represents
            // the "no game is writing" fallback layer.
            if (cfg.LightbarEnabled)
            {
                enableBits |= EnableLightbar;
                dst[39] = cfg.LightbarRed;
                dst[40] = cfg.LightbarGreen;
                dst[41] = cfg.LightbarBlue;
            }

            // Audio bytes (DualSense only — DS4 ignores).
            dst[4] = cfg.SpeakerVolume;
            enableBits |= EnableAudioVolume;
            // Mic light bit on byte 5 (Sony layout: bit 0 = mic light).
            // Mic mute is a separate audio-mute flag the firmware reads
            // from the audio control byte; PadForge surfaces it as a
            // user toggle though the bit position is informational —
            // games typically own this surface.
            byte audioCtl = 0;
            if (cfg.MicLightOn) audioCtl |= 0x01;
            if (cfg.MicMute) audioCtl |= 0x10;
            dst[5] = audioCtl;
            enableBits |= EnableMicLight;

            // Triggers.  Encoding is a one-byte mode followed by ten
            // mode-specific parameter bytes per trigger.  v3.1.0 ships
            // the four simplest modes (Off / Feedback / Weapon /
            // Vibration); the multi-position modes need their parameter
            // arrays plumbed through PlayStationSlotConfig and ship in
            // a follow-up commit.
            EncodeTrigger(cfg.RightTriggerMode,
                cfg.RightStartPosition, cfg.RightEndPosition,
                cfg.RightStrength, cfg.RightFrequency,
                dst.Slice(6, 11));
            EncodeTrigger(cfg.LeftTriggerMode,
                cfg.LeftStartPosition, cfg.LeftEndPosition,
                cfg.LeftStrength, cfg.LeftFrequency,
                dst.Slice(17, 11));
            if (cfg.RightTriggerMode != AdaptiveTriggerMode.Off) enableBits |= EnableRightTrigger;
            if (cfg.LeftTriggerMode != AdaptiveTriggerMode.Off) enableBits |= EnableLeftTrigger;

            // Header
            dst[0] = (byte)(enableBits & 0xFF);
            dst[1] = (byte)((enableBits >> 8) & 0xFF);

            return PayloadSize;
        }

        /// <summary>Encodes one trigger's 11-byte effect block (mode +
        /// 10 parameter bytes) per Sony's PS5 SDK conventions for the
        /// four simplest modes. Multi-position arrays for the remaining
        /// three modes ship in a follow-up commit (their array storage
        /// fields aren't on PlayStationSlotConfig yet — UI exposes the
        /// scalar parameters only).</summary>
        private static void EncodeTrigger(
            AdaptiveTriggerMode mode,
            byte startPosition,
            byte endPosition,
            byte strength,
            byte frequency,
            Span<byte> block)
        {
            block.Clear();
            switch (mode)
            {
                case AdaptiveTriggerMode.Off:
                    block[0] = 0x05; // ScePadTriggerEffectModeOff
                    break;

                case AdaptiveTriggerMode.Feedback:
                    // Mode 0x21 = Feedback. Param 0 = position (0-9),
                    // param 1 = strength (0-8). Strength 0 = release.
                    block[0] = 0x21;
                    block[1] = startPosition;
                    block[2] = strength;
                    break;

                case AdaptiveTriggerMode.Weapon:
                    // Mode 0x25 = Weapon. Param 0 = startPosition,
                    // param 1 = endPosition, param 2 = strength.
                    block[0] = 0x25;
                    block[1] = startPosition;
                    block[2] = endPosition;
                    block[3] = strength;
                    break;

                case AdaptiveTriggerMode.Vibration:
                    // Mode 0x26 = Vibration. Param 0 = position,
                    // param 1 = amplitude (strength), param 2 =
                    // frequency (Hz).
                    block[0] = 0x26;
                    block[1] = startPosition;
                    block[2] = strength;
                    block[3] = frequency;
                    break;

                case AdaptiveTriggerMode.MultiplePositionFeedback:
                case AdaptiveTriggerMode.SlopeFeedback:
                case AdaptiveTriggerMode.MultiplePositionVibration:
                    // Multi-position modes use a 10-byte strength array
                    // (or per-position frequency for vibration variants).
                    // Storage for those arrays isn't on
                    // PlayStationSlotConfig yet; UI surfaces the scalar
                    // parameters only.  Treat as Off until the array
                    // fields land — the user can pick a scalar mode
                    // (Feedback / Weapon / Vibration) for now.
                    block[0] = 0x05;
                    break;

                default:
                    block[0] = 0x05;
                    break;
            }
        }
    }
}
