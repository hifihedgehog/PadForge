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

        // EnableBits2 (high byte).
        private const ushort EnableMicLight         = 0x0100;  // byte[1] bit 0
        private const ushort EnableLightbar         = 0x0400;  // byte[1] bit 2
        private const ushort EnablePlayerIndicator  = 0x1000;  // byte[1] bit 4

        // Byte-offset constants — verified against
        // daidr/dualsense-tester's outputStruct.ts. See memory:
        // dualsense-tester-byte-layout-reference.md.
        private const int OffEnableLow       = 0;   // validFlag0
        private const int OffEnableHigh      = 1;   // validFlag1 (0xF7 = permissive default)
        private const int OffRumbleRight     = 2;   // compatibility rumble — right motor (high-frequency)
        private const int OffRumbleLeft      = 3;   // compatibility rumble — left motor (low-frequency)
        private const int OffMicLight        = 8;   // muteLedControl
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

        // DS5 firmware trigger mode opcodes. The simple set (0x01/0x02/0x06)
        // takes scalar parameters; the official set (0x21/0x26) takes a
        // 10-zone bitmap + packed 3-bit strengths and is what
        // multi-position and slope effects need. Both sets are recognized
        // by current PC HID firmware — see Nielk1's TriggerEffectGenerator
        // (DualSenseY-v2/thirdparty/duaLib/src/source/triggerFactory.cpp).
        private const byte HidModeOff           = 0x00;
        private const byte HidModeResistance    = 0x01;  // [start_pos, force]
        private const byte HidModeSoftTrigger   = 0x02;  // [start_pos, end_pos, force]
        private const byte HidModeAutoTrigger   = 0x06;  // [frequency, force, start_pos]
        private const byte HidModeFeedback      = 0x21;  // multi-position resistance
        private const byte HidModeVibration     = 0x26;  // multi-position vibration

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

            // Game rumble passthrough. Bit 0 of validFlag1 gates bytes
            // 2-3 — when bit 0 is clear, the firmware ignores the
            // rumble bytes entirely and leaves the motor state from
            // the most-recent write in place.
            //
            // We assert bit 0 ONLY when the dispatcher actually has a
            // non-zero game-rumble value to carry. With both bytes
            // zero (the AUDIO RUMBLE case — raw VibrationStates is 0
            // because audio mix is applied to SDL's writes only), we
            // leave bit 0 clear so SDL's separate SDL_RumbleJoystick
            // writes are the SOLE writer of the rumble fields. Two
            // writers competing on async-sampled audio peaks produce
            // a 30 Hz stutter the small DS5 motors perceive as weak;
            // staying out of SDL's lane lets the audio-mixed bytes it
            // writes survive untouched.
            //
            // Test rumble + game rumble: raw VibrationStates is set,
            // both writers carry the same scaled value, motors run
            // steady. (See InputService.SlotRumbleForDeviceProvider
            // for the input-side contract.)
            if ((rumbleRight | rumbleLeft) != 0)
            {
                dst[OffRumbleRight] = rumbleRight;
                dst[OffRumbleLeft]  = rumbleLeft;
                enableBits |= EnableRumbleEmulation;
            }

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
            // Snapshot the override window once so the rest of the function
            // sees a single time-of-check (the UtcNow comparison can flip
            // between calls within the same packet build). Intensity is
            // 1.0 for Sticky, ramps 1.0 → 0.0 over the decay window for
            // Reactive, and is 0 when no override is active.
            float macroOverrideIntensity = cfg.ComputeMacroOverrideIntensity();
            bool macroOverrideActive = macroOverrideIntensity > 0f;

            bool anyLightFeature =
                cfg.LightbarMode != LightbarMode.Off
                || cfg.PlayerLedMode != PlayerLedMode.Off
                || macroOverrideActive;

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

                if (macroOverrideActive)
                {
                    // Macro-driven override beats both the configured mode
                    // and the base-color path for its hold window. Game
                    // writes still win at packet level via Feature A.
                    // RGB scaled by intensity so a Reactive flash fades
                    // smoothly to black (mode takes over once intensity
                    // hits 0 → HasActiveMacroLightbarOverride flips false).
                    dst[OffLedRed]   = (byte)Math.Round(cfg.MacroOverrideR * macroOverrideIntensity);
                    dst[OffLedGreen] = (byte)Math.Round(cfg.MacroOverrideG * macroOverrideIntensity);
                    dst[OffLedBlue]  = (byte)Math.Round(cfg.MacroOverrideB * macroOverrideIntensity);
                }
                else if (cfg.LightbarMode != LightbarMode.Off)
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

            // Mic LED mode @ byte 8: 0 = off, 1 = solid, 2 = pulse.
            // Maps directly from MicLedMode enum.
            dst[OffMicLight] = (byte)cfg.MicLedMode;
            enableBits |= EnableMicLight;

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

        /// <summary>Public adapter for <see cref="ComputeLightbarColor"/>
        /// so the <see cref="Ds4EffectSynthesizer"/> can reuse the same
        /// per-mode logic without duplicating it. The DS4 path skips DS5-
        /// only fields (player LEDs, mic LED, AT) but the lightbar-mode
        /// resolution itself is device-agnostic.</summary>
        public static (byte r, byte g, byte b) ComputeLightbarColorPublic(
            PlayStationSlotConfig cfg,
            float audioPeak,
            long nowMs,
            uint randomColor,
            uint pulseColor,
            float pulseIntensity)
            => ComputeLightbarColor(cfg, audioPeak, nowMs, randomColor, pulseColor, pulseIntensity);

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
                    // Snapshot once: timer thread can't safely Count + index
                    // the live ObservableCollection while the UI thread is
                    // mutating it.
                    var palette = cfg.SnapshotLightbarPalette();
                    int n = palette.Length;
                    if (n == 0) return (0, 0, 0);
                    if (n == 1) return PaletteAt(palette, 0);
                    double scaled = phase * n;
                    int idx = (int)Math.Floor(scaled) % n;
                    int next = (idx + 1) % n;
                    var (r1, g1, b1) = PaletteAt(palette, idx);
                    if (!cfg.LightbarColorCycleSmooth)
                        return (r1, g1, b1);
                    var (r2, g2, b2) = PaletteAt(palette, next);
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
                case LightbarMode.InputReactiveCycle:
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

                case LightbarMode.InputReactiveFixed:
                {
                    float i = Math.Clamp(pulseIntensity, 0f, 1f);
                    return (
                        (byte)Math.Round(cfg.LightbarRed * i),
                        (byte)Math.Round(cfg.LightbarGreen * i),
                        (byte)Math.Round(cfg.LightbarBlue * i));
                }

                default:
                    return (0, 0, 0);
            }
        }

        private static (byte r, byte g, byte b) PaletteAt(LightbarPaletteEntry[] palette, int idx)
        {
            if (palette == null || palette.Length == 0) return (0, 0, 0);
            int n = palette.Length;
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
                    // When the user collapses the low band to zero
                    // (lowMid == 0) the only sample that lands in this
                    // branch is p == 0 (silence). Treat that as the
                    // bottom of the gradient so silence shows the low
                    // color rather than jumping to the mid color via a
                    // 1f fallback.
                    float t = lowMid > 0 ? p / lowMid : 0f;
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
        /// 10 parameter bytes). The simple modes (Feedback / Weapon /
        /// Vibration) use scalar opcodes 0x01/0x02/0x06 with the
        /// dualsense-tester layout; the multi-position modes
        /// (MultiplePositionFeedback / SlopeFeedback /
        /// MultiplePositionVibration) use the official 0x21/0x26 zone-
        /// bitmap encoding from Nielk1's TriggerEffectGenerator.
        ///
        /// <list type="bullet">
        /// <item>Off → 0x00 (no params)</item>
        /// <item>Feedback → 0x01: <c>[start_pos, force]</c></item>
        /// <item>Weapon → 0x02: <c>[start_pos, end_pos, force]</c></item>
        /// <item>Vibration → 0x06: <c>[frequency, force, start_pos]</c></item>
        /// <item>MultiplePositionFeedback → 0x21 with active-zone bitmap +
        /// per-zone 3-bit strengths covering [start_pos, end_pos]</item>
        /// <item>SlopeFeedback → 0x21 with strengths interpolated linearly
        /// from 1 at start_pos to <c>strength</c> at end_pos</item>
        /// <item>MultiplePositionVibration → 0x26 with active-zone bitmap +
        /// per-zone amplitudes covering [start_pos, end_pos] and
        /// frequency in byte 9</item>
        /// </list>
        ///
        /// <para>UI parameter values are 0-255 (full byte range). The 10
        /// multi-position zones are at trigger positions 0..9 mapped
        /// linearly across the byte range.</para>
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
                    EncodeMultiPosFeedback(block, startPosition, endPosition, strength);
                    break;

                case AdaptiveTriggerMode.SlopeFeedback:
                    EncodeSlopeFeedback(block, startPosition, endPosition, strength);
                    break;

                case AdaptiveTriggerMode.MultiplePositionVibration:
                    EncodeMultiPosVibration(block, startPosition, endPosition, strength, frequency);
                    break;

                default:
                    block[0] = HidModeOff;
                    break;
            }
        }

        // ────────────────────────────────────────────────
        //  Multi-position helpers (mode 0x21 / 0x26).
        //
        //  10 zones map linearly across the trigger throw, so a
        //  byte position p ∈ [0, 255] corresponds to zone index
        //  ⌊p / 25.6⌋ ∈ [0, 9]. Each zone carries a 3-bit strength
        //  (1-8 in user-facing terms; firmware decodes (strength-1)).
        //  Strength 0 = inactive zone. The wire format packs all 10
        //  3-bit strengths into a 32-bit forceZones word and the
        //  active-zone bitmap into a 16-bit activeZones word.
        // ────────────────────────────────────────────────

        private static int PositionToZone(byte position) => Math.Clamp(position * 10 / 256, 0, 9);

        // Convert a 0-255 strength byte to a 0-8 zone strength
        // (0 = off, 1-8 = increasing force). Round-half-up so a slider
        // at 255 hits the maximum 8 and 0 stays exactly 0.
        private static int StrengthToZone(byte strength)
        {
            if (strength == 0) return 0;
            int v = (strength * 8 + 127) / 255;
            return Math.Clamp(v, 1, 8);
        }

        private static void EncodeMultiPosFeedback(Span<byte> block, byte startPosition, byte endPosition, byte strength)
        {
            int strZone = StrengthToZone(strength);
            if (strZone == 0)
            {
                block[0] = HidModeOff;
                return;
            }

            int startIdx = PositionToZone(startPosition);
            int endIdx   = PositionToZone(endPosition);
            if (endIdx < startIdx) (startIdx, endIdx) = (endIdx, startIdx);

            // Alternating active/inactive zones in [start, end] — gives a
            // distinct ratcheting feel: trigger meets force at one zone,
            // releases at the next, meets force again, etc. Without the
            // alternation, "constant strength across a range" is exactly
            // what Weapon mode already does, and the two presets are
            // indistinguishable.
            uint forceZones = 0;
            ushort activeZones = 0;
            int forceValue = (strZone - 1) & 0x07;
            for (int i = startIdx; i <= endIdx; i++)
            {
                if (((i - startIdx) & 1) != 0) continue; // skip every other zone
                forceZones |= (uint)(forceValue << (3 * i));
                activeZones |= (ushort)(1 << i);
            }

            WriteFeedbackBlock(block, HidModeFeedback, activeZones, forceZones);
        }

        private static void EncodeSlopeFeedback(Span<byte> block, byte startPosition, byte endPosition, byte strength)
        {
            int endZone = StrengthToZone(strength);
            if (endZone == 0)
            {
                block[0] = HidModeOff;
                return;
            }

            int startIdx = PositionToZone(startPosition);
            int endIdx   = PositionToZone(endPosition);
            if (endIdx <= startIdx) endIdx = Math.Min(9, startIdx + 1);

            // Linear ramp from 1 at startIdx to endZone at endIdx, held
            // at endZone past endIdx so a fully pressed trigger keeps
            // the peak resistance.
            uint forceZones = 0;
            ushort activeZones = 0;
            int span = endIdx - startIdx;
            for (int i = startIdx; i < 10; i++)
            {
                int s;
                if (i <= endIdx)
                {
                    double t = span > 0 ? (double)(i - startIdx) / span : 1.0;
                    s = (int)Math.Round(1.0 + t * (endZone - 1));
                }
                else
                {
                    s = endZone;
                }
                s = Math.Clamp(s, 1, 8);
                int forceValue = (s - 1) & 0x07;
                forceZones |= (uint)(forceValue << (3 * i));
                activeZones |= (ushort)(1 << i);
            }

            WriteFeedbackBlock(block, HidModeFeedback, activeZones, forceZones);
        }

        private static void EncodeMultiPosVibration(Span<byte> block, byte startPosition, byte endPosition, byte strength, byte frequency)
        {
            int ampZone = StrengthToZone(strength);
            if (ampZone == 0 || frequency == 0)
            {
                block[0] = HidModeOff;
                return;
            }

            int startIdx = PositionToZone(startPosition);
            int endIdx   = PositionToZone(endPosition);
            if (endIdx < startIdx) (startIdx, endIdx) = (endIdx, startIdx);

            // Alternating active/inactive zones across [start, end] —
            // gives a stuttering / pulsing buzz feel as the trigger
            // pulls through the range. Without alternation, "buzz inside
            // a range" is what users already get from Vibration with a
            // narrowed Range slider, and the two presets feel the same.
            uint strengthZones = 0;
            ushort activeZones = 0;
            int strengthValue = (ampZone - 1) & 0x07;
            for (int i = startIdx; i <= endIdx; i++)
            {
                if (((i - startIdx) & 1) != 0) continue;
                strengthZones |= (uint)(strengthValue << (3 * i));
                activeZones |= (ushort)(1 << i);
            }

            block[0] = HidModeVibration;
            block[1] = (byte)(activeZones & 0xff);
            block[2] = (byte)((activeZones >> 8) & 0xff);
            block[3] = (byte)(strengthZones & 0xff);
            block[4] = (byte)((strengthZones >> 8) & 0xff);
            block[5] = (byte)((strengthZones >> 16) & 0xff);
            block[6] = (byte)((strengthZones >> 24) & 0xff);
            block[9] = frequency;
        }

        private static void WriteFeedbackBlock(Span<byte> block, byte mode, ushort activeZones, uint forceZones)
        {
            block[0] = mode;
            block[1] = (byte)(activeZones & 0xff);
            block[2] = (byte)((activeZones >> 8) & 0xff);
            block[3] = (byte)(forceZones & 0xff);
            block[4] = (byte)((forceZones >> 8) & 0xff);
            block[5] = (byte)((forceZones >> 16) & 0xff);
            block[6] = (byte)((forceZones >> 24) & 0xff);
        }
    }
}
