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

        // Byte-offset constants — verified against
        // daidr/dualsense-tester's outputStruct.ts. See memory:
        // dualsense-tester-byte-layout-reference.md.
        private const int OffEnableLow   = 0;   // validFlag0
        private const int OffEnableHigh  = 1;   // validFlag1 (0xF7 = permissive default)
        private const int OffSpeakerVol  = 5;
        private const int OffMicLight    = 8;   // muteLedControl
        private const int OffMicMute     = 9;   // powerSaveMuteControl
        private const int OffRightTrig   = 10;  // mode + 10 params
        private const int OffLeftTrig    = 21;  // mode + 10 params
        private const int OffLedRed      = 44;
        private const int OffLedGreen    = 45;
        private const int OffLedBlue     = 46;

        // HID-form trigger mode opcodes from dualsense-tester's
        // TriggerEffect.vue. NOT the same as Sony's PS5 SDK abstract
        // 0x21/0x25/0x26 values — those are higher-level abstractions
        // that don't map to firmware behavior on PC HID.
        private const byte HidModeOff           = 0x00;
        private const byte HidModeResistance    = 0x01;  // [start_pos, force]
        private const byte HidModeSoftTrigger   = 0x02;  // [start_pos, end_pos, force]
        private const byte HidModeAutoTrigger   = 0x06;  // [frequency, force, start_pos]

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

            // Lightbar — RGB at bytes 44/45/46 with the EnableLightbar
            // bit in validFlag1 (byte 1 bit 2 = 0x04, packs to 0x0400 in
            // the u16). Per dualsense-tester, the byte-41 lightbarSetup
            // value is for fade / blend control and defaults to 0; not
            // required for solid RGB writes.
            if (cfg.LightbarEnabled)
            {
                enableBits |= EnableLightbar;
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
            // Always assert the trigger-write enable bits when User
            // Effects are on. Without these, switching the mode to
            // Off doesn't release the trigger because the firmware
            // ignores the trigger bytes entirely (mode byte 0x00 +
            // zeros never reaches the haptic motor). Setting the
            // enable bit unconditionally tells the firmware "process
            // the trigger bytes," which carries the 0x00 mode through
            // and releases.
            enableBits |= EnableRightTrigger | EnableLeftTrigger;

            // Header — pack the u16 enable bits LE.
            dst[OffEnableLow]  = (byte)(enableBits & 0xFF);
            dst[OffEnableHigh] = (byte)((enableBits >> 8) & 0xFF);

            return PayloadSize;
        }

        /// <summary>Encodes one trigger's 11-byte effect block (mode +
        /// 10 parameter bytes) per the HID wire form used by
        /// dualsense-tester (verified end-to-end with real hardware
        /// via WebHID).
        ///
        /// <para>The PadForge UI uses Sony's abstract 7-mode list
        /// (Off / Feedback / Weapon / Vibration / MultiPosFeedback /
        /// Slope / MultiPosVibration) but the firmware on PC HID only
        /// understands four modes: Off / Resistance / Soft Trigger /
        /// Auto Trigger. We map abstract → HID per the table:</para>
        ///
        /// <list type="bullet">
        /// <item>Off → 0x00 (no params)</item>
        /// <item>Feedback → 0x01 Resistance: <c>[start_pos, force]</c></item>
        /// <item>Weapon → 0x02 Soft Trigger: <c>[start_pos, end_pos, force]</c></item>
        /// <item>Vibration → 0x06 Auto Trigger: <c>[frequency, force, start_pos]</c></item>
        /// <item>Multi-position modes → 0x00 (Off) until 10-byte array
        /// storage is added to PlayStationSlotConfig in a follow-up</item>
        /// </list>
        ///
        /// <para>All parameter values are 0-255 (full byte range).</para>
        /// </summary>
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
                    block[0] = HidModeOff;
                    break;

                case AdaptiveTriggerMode.Feedback:
                    // HID Resistance — params: [start_pos, force].
                    block[0] = HidModeResistance;
                    block[1] = startPosition;
                    block[2] = strength;
                    break;

                case AdaptiveTriggerMode.Weapon:
                    // HID Soft Trigger — params: [start_pos, end_pos, force].
                    block[0] = HidModeSoftTrigger;
                    block[1] = startPosition;
                    block[2] = endPosition;
                    block[3] = strength;
                    break;

                case AdaptiveTriggerMode.Vibration:
                    // HID Auto Trigger — params: [frequency, force, start_pos].
                    // Note the parameter ORDER differs from the other
                    // modes — frequency is param 0, not param 3.
                    block[0] = HidModeAutoTrigger;
                    block[1] = frequency;
                    block[2] = strength;
                    block[3] = startPosition;
                    break;

                case AdaptiveTriggerMode.MultiplePositionFeedback:
                case AdaptiveTriggerMode.SlopeFeedback:
                case AdaptiveTriggerMode.MultiplePositionVibration:
                    // Multi-position modes need 10-byte parameter arrays
                    // that aren't on PlayStationSlotConfig yet. Encode
                    // as Off so an unsupported selection doesn't lock
                    // the trigger; user can pick Feedback / Weapon /
                    // Vibration for now.
                    block[0] = HidModeOff;
                    break;

                default:
                    block[0] = HidModeOff;
                    break;
            }
        }
    }
}
