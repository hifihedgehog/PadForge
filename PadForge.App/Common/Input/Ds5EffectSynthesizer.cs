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
        private const ushort EnablePlayerIndicator  = 0x1000;  // byte[1] bit 4
        private const ushort EnableAudioMute        = 0x8000;  // byte[1] bit 7

        // Byte-offset constants — verified against
        // daidr/dualsense-tester's outputStruct.ts. See memory:
        // dualsense-tester-byte-layout-reference.md.
        private const int OffEnableLow       = 0;   // validFlag0
        private const int OffEnableHigh      = 1;   // validFlag1 (0xF7 = permissive default)
        private const int OffSpeakerVol      = 5;
        private const int OffMicLight        = 8;   // muteLedControl
        private const int OffMicMute         = 9;   // powerSaveMuteControl
        private const int OffRightTrig       = 10;  // mode + 10 params
        private const int OffLeftTrig        = 21;  // mode + 10 params
        private const int OffValidFlag2      = 38;  // ledBrightness gate
        private const int OffLightbarSetup   = 41;
        private const int OffLedBrightness   = 42;
        private const int OffPlayerIndicator = 43;
        private const int OffLedRed          = 44;
        private const int OffLedGreen        = 45;
        private const int OffLedBlue         = 46;

        // playerIndicator bit 5 (0x20) is the "no fade" flag — tells the
        // firmware to skip any in-progress lightbar fade animation
        // (notably the BT-connect blue fade) and apply the requested
        // state immediately. SDL3's PS5 driver ORs this same bit in
        // SetLightsForPlayerIndex; OpenRGB ALSO sets it in their
        // SonyDualSenseController. Without it, late-connect (and BT
        // reconnect) packets are received but visually overridden by
        // the firmware's default lightbar animation/state.
        private const byte PlayerIndicatorNoFade = 0x20;

        // Wire bits for the 5-LED player indicator strip below the
        // touchpad. Indexed by PlayerLedMode (0=Off..5=All). Per
        // dualsense-tester's PlayerLedControl enum.
        private static readonly byte[] PlayerLedBits =
            { 0x00, 0x04, 0x0A, 0x15, 0x1B, 0x1F };

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
        /// <param name="audioPeak">System audio peak in 0..1, applied
        /// only when <see cref="PlayStationSlotConfig.AudioLightbarEnabled"/>
        /// is true. Pass 0 for non-audio dispatch paths — the
        /// modulation is gated on the config flag, not the peak value.</param>
        public static int Build(PlayStationSlotConfig cfg, Span<byte> dst, float audioPeak = 0f)
        {
            if (cfg == null) return 0;
            if (dst.Length < PayloadSize) return 0;

            dst.Slice(0, PayloadSize).Clear();

            ushort enableBits = 0;

            // Lightbar / player-LED block. Reference: OpenRGB's
            // SonyDualSenseController.cpp + dualsense-tester's
            // OutputPanel.vue. The firmware needs ALL of:
            //   - validFlag1 bit 2 (lightbar enable) — gate for byte 44-46 RGB
            //   - validFlag1 bit 4 (player indicator) — gate for byte 43
            //   - validFlag2 = 0xFF — without higher bits set, hot-plug
            //     locks the lightbar even though SDL_SendGamepadEffect
            //     succeeds. Matched OpenRGB exactly to fix this.
            //   - byte 41 lightbarSetup = 0x02 — bypass BT default blue
            //   - byte 43 bit 0x20 — "no fade" flag, releases the
            //     in-progress connection animation. SDL3's PS5 driver
            //     also ORs this bit in SetLightsForPlayerIndex.
            // The player-LED bits 0-4 of byte 43 select which of the 5
            // bottom-row LEDs are lit (PlayerLedMode enum). Bit 0x20
            // is always set when ANY lightbar/player feature is active.
            bool anyLightFeature =
                cfg.LightbarEnabled
                || cfg.AudioLightbarEnabled
                || cfg.PlayerLedMode != PlayerLedMode.Off;

            if (anyLightFeature)
            {
                enableBits |= EnablePlayerIndicator;
                dst[OffValidFlag2]      = 0xFF;
                dst[OffLightbarSetup]   = 0x02;
                dst[OffLedBrightness]   = (byte)cfg.PlayerLedBrightness;
                int ledIdx = (int)cfg.PlayerLedMode;
                if (ledIdx < 0 || ledIdx >= PlayerLedBits.Length) ledIdx = 0;
                dst[OffPlayerIndicator] = (byte)(PlayerIndicatorNoFade | PlayerLedBits[ledIdx]);
            }

            // Set the lightbar-enable bit whenever any LED feature is in
            // play, not just when the user toggled the base-color override.
            // Byte 42 (ledBrightness) only takes effect when validFlag1
            // bit 2 is set — without this, brightness writes are silently
            // ignored, so changing High/Medium/Low looked like a no-op
            // unless the user also turned on the base-color toggle. The
            // saved RGB is written in the else branch below, so the
            // lightbar shows the user's configured colour rather than
            // black when an indicator feature is on without the toggle.
            if (anyLightFeature)
            {
                enableBits |= EnableLightbar;

                if (cfg.AudioLightbarEnabled)
                {
                    float p = Math.Clamp(audioPeak, 0f, 1f);
                    byte r, g, b;

                    switch (cfg.AudioLightbarMode)
                    {
                        case AudioLightbarMode.Thresholds:
                        case AudioLightbarMode.Gradient:
                        case AudioLightbarMode.CrossFade:
                        {
                            float lowMid  = (float)(cfg.AudioLowToMidPercent / 100.0);
                            float midHigh = (float)(cfg.AudioMidToHighPercent / 100.0);
                            // Self-correct if the user dragged sliders
                            // out of order — Mid band would otherwise
                            // get stranded.
                            if (midHigh < lowMid) midHigh = lowMid;

                            if (cfg.AudioLightbarMode == AudioLightbarMode.Thresholds)
                            {
                                // Hard discrete buckets — original behavior.
                                if (p < lowMid)
                                {
                                    r = cfg.AudioLowR; g = cfg.AudioLowG; b = cfg.AudioLowB;
                                }
                                else if (p < midHigh)
                                {
                                    r = cfg.AudioMidR; g = cfg.AudioMidG; b = cfg.AudioMidB;
                                }
                                else
                                {
                                    r = cfg.AudioHighR; g = cfg.AudioHighG; b = cfg.AudioHighB;
                                }
                            }
                            else if (cfg.AudioLightbarMode == AudioLightbarMode.Gradient)
                            {
                                // Linear lerp across the whole peak range.
                                // [0, lowMid]: Low → Mid
                                // [lowMid, midHigh]: Mid → High
                                // [midHigh, 1]: stays at High
                                if (p <= lowMid)
                                {
                                    float t = lowMid > 0 ? p / lowMid : 1f;
                                    LerpColor(t,
                                        cfg.AudioLowR, cfg.AudioLowG, cfg.AudioLowB,
                                        cfg.AudioMidR, cfg.AudioMidG, cfg.AudioMidB,
                                        out r, out g, out b);
                                }
                                else if (p <= midHigh)
                                {
                                    float span = midHigh - lowMid;
                                    float t = span > 0 ? (p - lowMid) / span : 1f;
                                    LerpColor(t,
                                        cfg.AudioMidR,  cfg.AudioMidG,  cfg.AudioMidB,
                                        cfg.AudioHighR, cfg.AudioHighG, cfg.AudioHighB,
                                        out r, out g, out b);
                                }
                                else
                                {
                                    r = cfg.AudioHighR; g = cfg.AudioHighG; b = cfg.AudioHighB;
                                }
                            }
                            else // CrossFade — discrete with crossfade window
                            {
                                float halfWindow = (float)(cfg.AudioCrossFadePercent / 100.0);
                                // Sane clamp: window can't exceed half the
                                // distance to the next threshold or it
                                // overlaps the neighbor's window.
                                float maxAtLowMid = MathF.Min(lowMid, MathF.Min(midHigh - lowMid, 1f - midHigh)) * 0.5f;
                                if (halfWindow > maxAtLowMid && maxAtLowMid > 0)
                                    halfWindow = maxAtLowMid;

                                float lo1 = lowMid  - halfWindow;
                                float hi1 = lowMid  + halfWindow;
                                float lo2 = midHigh - halfWindow;
                                float hi2 = midHigh + halfWindow;

                                if (p < lo1)
                                {
                                    r = cfg.AudioLowR; g = cfg.AudioLowG; b = cfg.AudioLowB;
                                }
                                else if (p < hi1)
                                {
                                    float span = hi1 - lo1;
                                    float t = span > 0 ? (p - lo1) / span : 1f;
                                    LerpColor(t,
                                        cfg.AudioLowR, cfg.AudioLowG, cfg.AudioLowB,
                                        cfg.AudioMidR, cfg.AudioMidG, cfg.AudioMidB,
                                        out r, out g, out b);
                                }
                                else if (p < lo2)
                                {
                                    r = cfg.AudioMidR; g = cfg.AudioMidG; b = cfg.AudioMidB;
                                }
                                else if (p < hi2)
                                {
                                    float span = hi2 - lo2;
                                    float t = span > 0 ? (p - lo2) / span : 1f;
                                    LerpColor(t,
                                        cfg.AudioMidR,  cfg.AudioMidG,  cfg.AudioMidB,
                                        cfg.AudioHighR, cfg.AudioHighG, cfg.AudioHighB,
                                        out r, out g, out b);
                                }
                                else
                                {
                                    r = cfg.AudioHighR; g = cfg.AudioHighG; b = cfg.AudioHighB;
                                }
                            }

                            dst[OffLedRed]   = r;
                            dst[OffLedGreen] = g;
                            dst[OffLedBlue]  = b;
                            break;
                        }

                        case AudioLightbarMode.Pulse:
                        default:
                            // DSY-style brightness modulation. Multiply
                            // user's static base color by the peak each
                            // tick (black at silence, full color at peak).
                            dst[OffLedRed]   = (byte)Math.Round(cfg.LightbarRed   * p);
                            dst[OffLedGreen] = (byte)Math.Round(cfg.LightbarGreen * p);
                            dst[OffLedBlue]  = (byte)Math.Round(cfg.LightbarBlue  * p);
                            break;
                    }
                }
                else
                {
                    dst[OffLedRed]   = cfg.LightbarRed;
                    dst[OffLedGreen] = cfg.LightbarGreen;
                    dst[OffLedBlue]  = cfg.LightbarBlue;
                }
            }

            // Audio bytes — speaker volume + mic light + mic mute.
            // DualSense only; DS4 firmware ignores these even when
            // present in the report.
            dst[OffSpeakerVol] = cfg.SpeakerVolume;
            enableBits |= EnableSpeakerVolume;

            // Mic LED mode @ byte 8: 0 = off, 1 = solid, 2 = pulse.
            // Maps directly from MicLedMode enum.
            dst[OffMicLight] = (byte)cfg.MicLedMode;
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

        // Linear interpolation between two RGB colors. t is clamped
        // 0..1; t=0 returns color A, t=1 returns color B.
        private static void LerpColor(
            float t,
            byte aR, byte aG, byte aB,
            byte bR, byte bG, byte bB,
            out byte r, out byte g, out byte b)
        {
            t = Math.Clamp(t, 0f, 1f);
            r = (byte)Math.Round(aR + (bR - aR) * t);
            g = (byte)Math.Round(aG + (bG - aG) * t);
            b = (byte)Math.Round(aB + (bB - aB) * t);
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
