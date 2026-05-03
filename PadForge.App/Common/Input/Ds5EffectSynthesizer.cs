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
        private const ushort EnableRumbleEmulation  = 0x0001;  // bit 0 — gates bytes 2-3 (right/left motor)
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
        private const int OffRumbleRight     = 2;   // compatibility rumble — right motor (high-frequency)
        private const int OffRumbleLeft      = 3;   // compatibility rumble — left motor (low-frequency)
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
        /// <param name="audioPeak">System audio peak in 0..1. Used by
        /// the AudioPulse* and AudioThresholds/Gradient/CrossFade modes
        /// only; ignored by static / time-based / input-reactive modes.</param>
        /// <param name="nowMs">Wall-clock timestamp in milliseconds for
        /// time-based animations (Breathing / Rainbow / ColorCycle /
        /// AudioPulseRainbow). 0 is fine for non-animated dispatches.</param>
        /// <param name="randomColor">Packed RGB (0xRRGGBB) the dispatcher
        /// rolled at the most recent audio onset. Read by
        /// <see cref="LightbarMode.AudioPulseRandom"/>.</param>
        /// <param name="pulseColor">Packed RGB (0xRRGGBB) of the current
        /// input-reactive pulse. Read by
        /// <see cref="LightbarMode.InputReactive"/>.</param>
        /// <param name="pulseIntensity">Decay envelope of the current
        /// input-reactive pulse, 0..1. Read by
        /// <see cref="LightbarMode.InputReactive"/>.</param>
        public static int Build(
            PlayStationSlotConfig cfg,
            Span<byte> dst,
            float audioPeak = 0f,
            long nowMs = 0,
            uint randomColor = 0,
            uint pulseColor = 0,
            float pulseIntensity = 0f,
            byte rumbleRight = 0,
            byte rumbleLeft = 0)
        {
            if (cfg == null) return 0;
            if (dst.Length < PayloadSize) return 0;

            dst.Slice(0, PayloadSize).Clear();

            ushort enableBits = 0;

            // Game rumble passthrough. The dispatcher's animated-lightbar
            // timer fires raw HID writes at 30 Hz, which on Bluetooth
            // crowds SDL3's separate SDL_RumbleJoystick writes off the
            // channel. Carrying the current motor state in every packet
            // keeps rumble alive regardless of how much lightbar
            // bandwidth we're using. Bit 0 of validFlag1 gates bytes 2-3.
            dst[OffRumbleRight] = rumbleRight;
            dst[OffRumbleLeft]  = rumbleLeft;
            enableBits |= EnableRumbleEmulation;

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
                cfg.LightbarMode != LightbarMode.Off
                || cfg.PlayerLedMode != PlayerLedMode.Off;

            // Always assert the player-indicator update bit and write byte
            // 43, even when PlayerLedMode == Off. Without setting validFlag1
            // bit 4 the firmware ignores byte 43 entirely, so a transition
            // from a pattern (say, Player1) back to Off would leave the row
            // stuck on the previous pattern. PlayerLedBits[Off] is 0, so the
            // byte degenerates to PlayerIndicatorNoFade alone (0x20) — no
            // LED bits set, no-fade asserted — which cleanly extinguishes
            // the row.
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

                if (cfg.LightbarMode != LightbarMode.Off)
                {
                    var (r, g, b) = ComputeLightbarColor(
                        cfg, audioPeak, nowMs, randomColor, pulseColor, pulseIntensity);
                    dst[OffLedRed]   = r;
                    dst[OffLedGreen] = g;
                    dst[OffLedBlue]  = b;
                }
                // else: anyLightFeature is true purely because the player
                // pattern is on with LightbarMode == Off. Leave bytes 44-46
                // at the buffer's initial zero so the lightbar stays dark;
                // we still need EnableLightbar set above for byte 42
                // (ledBrightness) to apply.
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

        // ────────────────────────────────────────────────
        //  Lightbar mode dispatch (LightbarMode -> RGB triple)
        // ────────────────────────────────────────────────

        /// <summary>Reduces the active <see cref="LightbarMode"/> plus
        /// dynamic inputs (audio peak, wall-clock timestamp, dispatcher-
        /// rolled random color, dispatcher-tracked input pulse) to a final
        /// RGB triple for bytes 44-46 of the effect packet. Stateless;
        /// the dispatcher owns all state.</summary>
        private static (byte r, byte g, byte b) ComputeLightbarColor(
            PlayStationSlotConfig cfg,
            float audioPeak,
            long nowMs,
            uint randomColor,
            uint pulseColor,
            float pulseIntensity)
        {
            float p = Math.Clamp(audioPeak, 0f, 1f);
            int periodMs = Math.Max(cfg.LightbarPeriodMs, 250);
            double phase = nowMs > 0 ? (double)((nowMs % periodMs + periodMs) % periodMs) / periodMs : 0.0;

            switch (cfg.LightbarMode)
            {
                case LightbarMode.Static:
                    return (cfg.LightbarRed, cfg.LightbarGreen, cfg.LightbarBlue);

                case LightbarMode.Breathing:
                {
                    // Triangle envelope 0 → 1 → 0 across the period.
                    double m = phase < 0.5 ? phase * 2.0 : (1.0 - phase) * 2.0;
                    return (
                        (byte)Math.Round(cfg.LightbarRed   * m),
                        (byte)Math.Round(cfg.LightbarGreen * m),
                        (byte)Math.Round(cfg.LightbarBlue  * m));
                }

                case LightbarMode.Rainbow:
                    return HsvToRgb(phase * 360.0, 1.0, 1.0);

                case LightbarMode.ColorCycle:
                {
                    int n = cfg.LightbarPalette?.Count ?? 0;
                    if (n == 0) return (0, 0, 0);
                    if (n == 1) return PaletteAt(cfg, 0);
                    double scaled = phase * n;
                    int idx = (int)Math.Floor(scaled) % n;
                    int next = (idx + 1) % n;
                    var (r1, g1, b1) = PaletteAt(cfg, idx);
                    if (!cfg.LightbarColorCycleSmooth)
                        return (r1, g1, b1);
                    var (r2, g2, b2) = PaletteAt(cfg, next);
                    double t = scaled - Math.Floor(scaled);
                    return (
                        (byte)Math.Round(r1 + (r2 - r1) * t),
                        (byte)Math.Round(g1 + (g2 - g1) * t),
                        (byte)Math.Round(b1 + (b2 - b1) * t));
                }

                case LightbarMode.AudioPulse:
                    return (
                        (byte)Math.Round(cfg.LightbarRed   * p),
                        (byte)Math.Round(cfg.LightbarGreen * p),
                        (byte)Math.Round(cfg.LightbarBlue  * p));

                case LightbarMode.AudioPulseRandom:
                {
                    byte rr = (byte)((randomColor >> 16) & 0xFF);
                    byte rg = (byte)((randomColor >> 8) & 0xFF);
                    byte rb = (byte)(randomColor & 0xFF);
                    return (
                        (byte)Math.Round(rr * p),
                        (byte)Math.Round(rg * p),
                        (byte)Math.Round(rb * p));
                }

                case LightbarMode.AudioPulseRainbow:
                {
                    var (rr, rg, rb) = HsvToRgb(phase * 360.0, 1.0, 1.0);
                    return (
                        (byte)Math.Round(rr * p),
                        (byte)Math.Round(rg * p),
                        (byte)Math.Round(rb * p));
                }

                case LightbarMode.AudioThresholds:
                case LightbarMode.AudioGradient:
                case LightbarMode.AudioCrossFade:
                    return ComputeAudioBands(cfg, p);

                case LightbarMode.InputReactive:
                {
                    float i = Math.Clamp(pulseIntensity, 0f, 1f);
                    byte pr = (byte)((pulseColor >> 16) & 0xFF);
                    byte pg = (byte)((pulseColor >> 8) & 0xFF);
                    byte pb = (byte)(pulseColor & 0xFF);
                    return (
                        (byte)Math.Round(pr * i),
                        (byte)Math.Round(pg * i),
                        (byte)Math.Round(pb * i));
                }

                default:
                    return (0, 0, 0);
            }
        }

        private static (byte r, byte g, byte b) PaletteAt(PlayStationSlotConfig cfg, int idx)
        {
            var palette = cfg.LightbarPalette;
            if (palette == null || palette.Count == 0) return (0, 0, 0);
            int n = palette.Count;
            int wrapped = ((idx % n) + n) % n;
            var entry = palette[wrapped];
            return (entry.R, entry.G, entry.B);
        }

        private static (byte r, byte g, byte b) HsvToRgb(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;
            double rp, gp, bp;
            if (h < 60)       { rp = c; gp = x; bp = 0; }
            else if (h < 120) { rp = x; gp = c; bp = 0; }
            else if (h < 180) { rp = 0; gp = c; bp = x; }
            else if (h < 240) { rp = 0; gp = x; bp = c; }
            else if (h < 300) { rp = x; gp = 0; bp = c; }
            else              { rp = c; gp = 0; bp = x; }
            return (
                (byte)Math.Round((rp + m) * 255),
                (byte)Math.Round((gp + m) * 255),
                (byte)Math.Round((bp + m) * 255));
        }

        private static (byte r, byte g, byte b) ComputeAudioBands(PlayStationSlotConfig cfg, float p)
        {
            float lowMid  = (float)(cfg.AudioLowToMidPercent / 100.0);
            float midHigh = (float)(cfg.AudioMidToHighPercent / 100.0);
            // Self-correct if the user dragged sliders out of order — the
            // Mid band would otherwise be stranded.
            if (midHigh < lowMid) midHigh = lowMid;

            byte r, g, b;

            if (cfg.LightbarMode == LightbarMode.AudioThresholds)
            {
                if (p < lowMid)        { r = cfg.AudioLowR;  g = cfg.AudioLowG;  b = cfg.AudioLowB; }
                else if (p < midHigh)  { r = cfg.AudioMidR;  g = cfg.AudioMidG;  b = cfg.AudioMidB; }
                else                   { r = cfg.AudioHighR; g = cfg.AudioHighG; b = cfg.AudioHighB; }
                return (r, g, b);
            }

            if (cfg.LightbarMode == LightbarMode.AudioGradient)
            {
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
                return (r, g, b);
            }

            // CrossFade — discrete with crossfade window around each threshold.
            float halfWindow = (float)(cfg.AudioCrossFadePercent / 100.0);
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
            return (r, g, b);
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
