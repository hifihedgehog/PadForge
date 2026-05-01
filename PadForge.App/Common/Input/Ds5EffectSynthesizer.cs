using System;
using PadForge.ViewModels;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Builds DualSense USB Report 0x02 effect messages from a
    /// <see cref="PlayStationSlotConfig"/>. Wire format reference is the
    /// DS5 SDK's 47-byte effect-state layout — same form that
    /// game-driven effect output uses (which the
    /// <see cref="DualSensePassthroughDispatcher"/> forwards verbatim).
    ///
    /// <para>Standard 47-byte payload byte map (consensus across DS5W,
    /// dualsensectl, DSY, AntiMicroX's EffectMessagePs5):</para>
    /// <code>
    /// [0]      EnableBits1 — bit 0=R rumble, 1=L rumble, 2=R trigger,
    ///                       3=L trigger, 4=headphone vol, 5=speaker vol,
    ///                       6=mic vol, 7=audio control flags
    /// [1]      EnableBits2 — bit 0=mic mute light, 1=power save,
    ///                       2=lightbar (RGB), 3=release lights,
    ///                       4=player indicator, 5=motor scale,
    ///                       6=trigger scale, 7=audio mute
    /// [2]      RumbleRight
    /// [3]      RumbleLeft
    /// [4]      HeadphoneVolume
    /// [5]      SpeakerVolume
    /// [6]      MicVolume
    /// [7]      AudioControlFlags
    /// [8]      MicLightMode    (0=off, 1=on, 2=pulse)
    /// [9]      AudioMuteBits   (bit 4 = mic mute)
    /// [10..20] RightTriggerEffect (mode + 10 param bytes)
    /// [21..31] LeftTriggerEffect  (mode + 10 param bytes)
    /// [32..37] reserved
    /// [38]     LightbarSetupFlags  (0x02 = enable RGB write)
    /// [39..40] reserved
    /// [41]     LedAnimation
    /// [42]     LedBrightness
    /// [43]     PlayerIndicator
    /// [44]     LedRed
    /// [45]     LedGreen
    /// [46]     LedBlue
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

        // EnableBits1 (low byte of the u16 LE header).
        private const ushort EnableRightTrigger     = 0x0004;
        private const ushort EnableLeftTrigger      = 0x0008;
        private const ushort EnableSpeakerVolume    = 0x0020;

        // EnableBits2 (high byte).
        private const ushort EnableMicLight         = 0x0100;  // byte[1] bit 0
        private const ushort EnableLightbar         = 0x0400;  // byte[1] bit 2
        private const ushort EnableAudioMute        = 0x8000;  // byte[1] bit 7

        // Byte-offset constants — keep close to the byte map above so
        // anyone editing this file can cross-check at a glance.
        private const int OffEnableLow   = 0;
        private const int OffEnableHigh  = 1;
        private const int OffSpeakerVol  = 5;
        private const int OffMicLight    = 8;
        private const int OffMicMute     = 9;
        private const int OffRightTrig   = 10;  // 11 bytes
        private const int OffLeftTrig    = 21;  // 11 bytes
        private const int OffLightFlags  = 38;
        private const int OffLedRed      = 44;
        private const int OffLedGreen    = 45;
        private const int OffLedBlue     = 46;

        // LightbarSetupFlags @ byte 38: bit 1 (0x02) = "set lightbar
        // RGB". Other bits control fade and player indicator overrides;
        // we only need the RGB write flag for solid-color set.
        private const byte LightbarSetupRgb = 0x02;

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

            // Lightbar — write RGB at bytes 44/45/46 with the LightbarSetupRgb
            // flag at byte 38 plus the EnableLightbar bit in EnableBits2.
            // All three together are needed for the firmware to honor the
            // RGB write — DS5 ignores writes that don't have both the
            // enable bit AND the setup flag set.
            if (cfg.LightbarEnabled)
            {
                enableBits |= EnableLightbar;
                dst[OffLightFlags] = LightbarSetupRgb;
                dst[OffLedRed]   = cfg.LightbarRed;
                dst[OffLedGreen] = cfg.LightbarGreen;
                dst[OffLedBlue]  = cfg.LightbarBlue;
            }

            // Audio bytes — speaker volume + mic light + mic mute.
            // DualSense only; DS4 firmware ignores these even when
            // present in the report.
            dst[OffSpeakerVol] = cfg.SpeakerVolume;
            enableBits |= EnableSpeakerVolume;

            // Mic light mode @ byte 8: 0 = off, 1 = on, 2 = pulse.
            // PadForge surfaces a binary toggle.
            dst[OffMicLight] = cfg.MicLightOn ? (byte)1 : (byte)0;
            enableBits |= EnableMicLight;

            // Audio mute bits @ byte 9: bit 4 (0x10) = mic mute.
            if (cfg.MicMute)
            {
                dst[OffMicMute] = 0x10;
                enableBits |= EnableAudioMute;
            }

            // Triggers — 11 bytes per trigger (mode + 10 param bytes)
            // at the canonical Right=10, Left=21 offsets.  Encoding for
            // the four scalar modes shipping in v3.1.0 is in
            // EncodeTrigger; multi-position modes need parameter arrays
            // plumbed through PlayStationSlotConfig in a follow-up.
            EncodeTrigger(cfg.RightTriggerMode,
                cfg.RightStartPosition, cfg.RightEndPosition,
                cfg.RightStrength, cfg.RightFrequency,
                dst.Slice(OffRightTrig, 11));
            EncodeTrigger(cfg.LeftTriggerMode,
                cfg.LeftStartPosition, cfg.LeftEndPosition,
                cfg.LeftStrength, cfg.LeftFrequency,
                dst.Slice(OffLeftTrig, 11));
            if (cfg.RightTriggerMode != AdaptiveTriggerMode.Off) enableBits |= EnableRightTrigger;
            if (cfg.LeftTriggerMode != AdaptiveTriggerMode.Off) enableBits |= EnableLeftTrigger;

            // Header — pack the u16 enable bits LE.
            dst[OffEnableLow]  = (byte)(enableBits & 0xFF);
            dst[OffEnableHigh] = (byte)((enableBits >> 8) & 0xFF);

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
