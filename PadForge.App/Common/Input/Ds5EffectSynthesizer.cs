using System;
using System.Collections.Generic;
using PadForge.ViewModels;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Resolves <see cref="PlayStationSlotConfig"/> into a parsed-field
    /// dictionary for DualSense effect output. The dictionary is fed to
    /// <c>HMOutputEncoder.Encode(profile, fields)</c>, which packs the bytes
    /// per the active profile's <c>extendedOutputReport</c> spec — USB
    /// (Report 0x02, 48B) or BT (Report 0x31, 78B with CRC32). The same
    /// dictionary serves both transports; the encoder picks the fields it
    /// needs from the profile spec and ignores the rest.
    ///
    /// <para>HIDMaestro v1.3.5 introduced the data-driven encoder; the
    /// pre-v1.3.5 implementation hand-packed a 47-byte <c>Span&lt;byte&gt;</c>
    /// at compile-time-known offsets, plus a separate BT-envelope wrapper
    /// with hand-rolled CRC32. Now: one dictionary, the SDK does the rest.</para>
    ///
    /// <para>══════════════════════════════════════════════════════════════</para>
    /// <para><b>SOLE-WRITER CONTRACT — read before changing rumble bytes.</b></para>
    /// <para>══════════════════════════════════════════════════════════════</para>
    /// <para>This synthesizer's output is the ONLY effect packet that
    /// reaches a DualSense from PadForge. <c>InputManager.Step2.ApplyForceFeedback</c>
    /// returns early for Sony VID 0x054C / DS5 PIDs so SDL_RumbleJoystick
    /// is never called for these devices. That asymmetry is load-bearing:
    /// two writers (PadForge dispatcher + SDL3's PS5 driver) competing on
    /// an asynchronously-sampled audio peak (<see cref="AudioBassDetector"/>)
    /// produced the v3.1.x audio-rumble + animated-lightbar regression.
    /// One writer cannot race with itself.</para>
    /// <para>Therefore: the rumble bytes (rightMotor / leftMotor) and bit
    /// 0 of validFlag0 are written UNCONDITIONALLY in every dispatch. The
    /// dispatcher computes audio-mix + per-device gain in
    /// <c>InputService.SlotRumbleForDeviceProvider</c> and feeds those
    /// bytes here. Do NOT add conditional gating "for safety" — there is
    /// no second writer to coordinate with, and conditional bytes would
    /// just leave gaps in the rumble stream during silent audio frames.</para>
    /// <para>See memory: sony-rumble-sole-writer-architecture.md.</para>
    /// </summary>
    internal static class Ds5EffectSynthesizer
    {
        // EnableBits1 (low byte of the u16 LE header; HM field "validFlag0").
        // Bits 0 + 1 both engage motor rumble per Linux's hid-playstation
        // (bit 0 = COMPATIBLE_VIBRATION, bit 1 = HAPTICS_SELECT). Steam
        // Input asserts bit 1 on its DS5 effect writes; OpenRGB sets all
        // 8 bits (0xFF). Setting both is defensive — whichever bit any
        // host firmware actually keys on, the rumble bytes apply.
        private const ushort EnableRumbleEmulation  = 0x0003;  // bits 0 + 1
        private const ushort EnableRightTrigger     = 0x0004;
        private const ushort EnableLeftTrigger      = 0x0008;

        // EnableBits2 (high byte; HM field "validFlag1").
        private const ushort EnableMicLight         = 0x0100;
        private const ushort EnableLightbar         = 0x0400;
        private const ushort EnablePlayerIndicator  = 0x1000;

        // BT DS5 framing tag — byte 1 of Report 0x31. Real Sony DS5 BT
        // firmware infers header length from byte 1's low nibble:
        //   - low nibble nonzero (e.g. 0x02): 1-byte header → firmware
        //     reads validFlag0 at byte 2 of the on-wire packet.
        //   - low nibble zero (e.g. 0x10, 0x20, …, 0xF0): 2-byte header
        //     → firmware reads byte 2 as a framing flag (must be 0x10)
        //     and validFlag0 at byte 3.
        // HM v1.3.5's extendedOutputReport places validFlag0 at byte 3
        // and writes 0x10 at byte 2, so we MUST signal 2-byte header
        // mode via a rolling (seq << 4) counter at byte 1. A constant
        // like 0x02 here mixes the two conventions and the firmware
        // silently drops the effect packet (no lightbar, no AT, no
        // rumble enable). Cycles 0x10, 0x20, …, 0xF0, then wraps.
        // dualsense-tester reference: ds.util.ts sendOutputReport.
        private static int s_btSeqCounter;
        private static byte NextBtSeqTag()
        {
            int s = System.Threading.Interlocked.Increment(ref s_btSeqCounter);
            return (byte)((s & 0x0F) << 4);
        }

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

        // Frequency parameter for impulse-trigger → AT Vibration auto-routing.
        // Matches Special K's value (SpecialK/src/input/hid_reports/playstation.cpp:4926
        // — the AutoTrigger effect data array's byte 1 default of 15). Produces
        // a noticeable buzz across the trigger's range without feeling too sharp.
        private const byte ImpulseAtVibrationFrequency = 15;

        /// <summary>Builds the parsed-field dictionary for one DualSense
        /// effect packet. Pass to <c>HMOutputEncoder.Encode</c> with either
        /// the USB (Report 0x02) or BT (Report 0x31) DualSense profile —
        /// the dict carries both transports' fields and the encoder picks
        /// what it needs.</summary>
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
        public static Dictionary<string, object> BuildFields(
            PlayStationSlotConfig cfg,
            float audioPeak = 0f,
            long nowMs = 0,
            uint randomColor = 0,
            uint pulseColor = 0,
            float pulseIntensity = 0f,
            byte rumbleRight = 0,
            byte rumbleLeft = 0,
            bool assertRumbleEnable = true,
            bool assertRightTriggerEnable = true,
            bool assertLeftTriggerEnable = true,
            UserEffectsDispatcher.ExternalSubsystemOverrides overrides = default,
            byte batteryPercent = 100)
        {
            ushort enableBits = 0;

            // Per-subsystem external mirroring: when a host has recently
            // written a particular subsystem to the virtual, our dispatch
            // mirrors that subsystem's bytes verbatim instead of writing
            // our own animated / configured value. PadForge keeps owning
            // every subsystem the external writer didn't touch, so the
            // animation lightbar (or any other unaffected subsystem)
            // continues running while rumble / triggers / mic stay under
            // the external writer's control. Each PadForge packet always
            // carries a complete validFlag bitset and field dict — this
            // is just about which value goes in for each owned subsystem.

            // Rumble: when external owns it, mirror their bytes and assert
            // bit 0 so the firmware applies the mirrored values. When
            // PadForge owns it, gate bit 0 on PadForge's own rumble state
            // (drop-frame already handled by the caller).
            bool rumbleExternal = overrides.RumbleRight.HasValue && overrides.RumbleLeft.HasValue;
            byte effectiveRumbleR = rumbleExternal ? overrides.RumbleRight.Value : rumbleRight;
            byte effectiveRumbleL = rumbleExternal ? overrides.RumbleLeft.Value  : rumbleLeft;
            if (rumbleExternal || assertRumbleEnable)
                enableBits |= EnableRumbleEmulation;

            // Snapshot the override window once so the rest of the function
            // sees a single time-of-check. Intensity is 1.0 for Sticky,
            // ramps 1.0 → 0.0 over the decay window for Reactive, 0 when
            // no override is active.
            float macroOverrideIntensity = cfg?.ComputeMacroOverrideIntensity() ?? 0f;
            bool macroOverrideActive = macroOverrideIntensity > 0f;
            bool inputReactiveActive = cfg != null && cfg.InputReactiveMode != InputReactiveMode.Off;

            bool anyLightFeature = cfg != null && (
                cfg.LightbarMode != LightbarMode.Off
                || cfg.PlayerLedMode != PlayerLedMode.Off
                || macroOverrideActive
                || inputReactiveActive);

            // Always assert the player-indicator update bit and write the
            // playerIndicator byte, even when PlayerLedMode == Off. Without
            // setting validFlag1 bit 4 the firmware ignores the byte
            // entirely, so a transition from a pattern (say, Player1) back
            // to Off would leave the row stuck on the previous pattern.
            // PlayerLedBits[Off] is 0, so the byte degenerates to
            // PlayerIndicatorNoFade alone (0x20) — no LED bits set, no-fade
            // asserted — which cleanly extinguishes the row.
            // Mirror the external writer's value when they own this
            // subsystem.
            enableBits |= EnablePlayerIndicator;
            byte playerIndicator;
            if (overrides.PlayerIndicator.HasValue)
            {
                playerIndicator = overrides.PlayerIndicator.Value;
            }
            else
            {
                int ledIdx = cfg != null ? (int)cfg.PlayerLedMode : 0;
                if (ledIdx < 0 || ledIdx >= PlayerLedBits.Length) ledIdx = 0;
                playerIndicator = (byte)(PlayerIndicatorNoFade | PlayerLedBits[ledIdx]);
            }
            byte ledBrightness = overrides.LedBrightness
                ?? (cfg != null ? (byte)cfg.PlayerLedBrightness : (byte)0);

            // Lightbar / RGB block. Reference: OpenRGB's
            // SonyDualSenseController.cpp + dualsense-tester's
            // OutputPanel.vue. The firmware needs ALL of:
            //   - validFlag1 bit 2 (lightbar enable) — gate for the RGB bytes
            //   - validFlag1 bit 4 (player indicator) — already set above
            //   - validFlag2 = 0xFF — without higher bits set, hot-plug
            //     locks the lightbar even though SDL_SendGamepadEffect
            //     succeeds. Matched OpenRGB exactly to fix this.
            //   - lightbarSetup = 0x02 — bypass BT default blue
            //   - playerIndicator bit 0x20 (PlayerIndicatorNoFade) —
            //     releases the in-progress connection animation. SDL3's
            //     PS5 driver also ORs this bit in SetLightsForPlayerIndex.
            byte ledR = 0, ledG = 0, ledB = 0;
            bool lightbarExternal = overrides.LightbarRgb != null && overrides.LightbarRgb.Length >= 3;
            if (lightbarExternal)
            {
                // External writer owns the lightbar this frame: mirror their
                // RGB verbatim and assert the lightbar enable bit so the
                // firmware applies it. PadForge's animation pauses on this
                // subsystem only — every other subsystem still updates.
                enableBits |= EnableLightbar;
                ledR = overrides.LightbarRgb[0];
                ledG = overrides.LightbarRgb[1];
                ledB = overrides.LightbarRgb[2];
            }
            else if (anyLightFeature)
            {
                enableBits |= EnableLightbar;

                // Priority: macro override > input-reactive overlay > base
                // mode. Macro override blends the configured macro RGB
                // ×macroIntensity directly (legacy behaviour, full
                // override during the hold window). The input-reactive
                // overlay layers OVER the base mode by lerping between
                // the base color and the reactive flash by pulseIntensity
                // — at intensity 1.0 you see the flash, as it decays
                // toward 0 the base mode shows through. Off + overlay
                // collapses to a black base, matching legacy
                // InputReactive*-as-base-mode behaviour.
                if (macroOverrideActive)
                {
                    ledR = (byte)Math.Round(cfg.MacroOverrideR * macroOverrideIntensity);
                    ledG = (byte)Math.Round(cfg.MacroOverrideG * macroOverrideIntensity);
                    ledB = (byte)Math.Round(cfg.MacroOverrideB * macroOverrideIntensity);
                }
                else
                {
                    byte baseR = 0, baseG = 0, baseB = 0;
                    if (cfg.LightbarMode != LightbarMode.Off)
                    {
                        (baseR, baseG, baseB) = ComputeLightbarColor(
                            cfg, audioPeak, nowMs, randomColor, pulseColor, pulseIntensity, batteryPercent);
                    }

                    if (inputReactiveActive && pulseIntensity > 0f)
                    {
                        var (rR, rG, rB) = ResolveReactiveOverlayColor(
                            cfg, randomColor, pulseColor);
                        LerpColor(pulseIntensity,
                            baseR, baseG, baseB,
                            rR, rG, rB,
                            out ledR, out ledG, out ledB);
                    }
                    else
                    {
                        ledR = baseR; ledG = baseG; ledB = baseB;
                    }
                }
            }

            // Mic LED mode: 0 = off, 1 = solid, 2 = pulse. Values 0-2
            // map directly from MicLedMode enum. FollowDeviceMute (3) is
            // resolved at write-time via AudioMuteService — muted
            // endpoint -> Solid (1), unmuted -> Off (0). External
            // writer's value (overrides.MuteLed) wins when they own the
            // mic-LED subsystem during the grace window.
            byte muteLed;
            if (overrides.MuteLed.HasValue)
            {
                muteLed = overrides.MuteLed.Value;
            }
            else if (cfg != null && cfg.MicLedMode == MicLedMode.FollowDeviceMute)
            {
                bool? muted = AudioMuteService.GetMuteState(cfg.MicLedFollowDeviceId);
                muteLed = (muted == true) ? (byte)1 : (byte)0;
            }
            else
            {
                muteLed = cfg != null ? (byte)cfg.MicLedMode : (byte)0;
            }
            enableBits |= EnableMicLight;

            // Triggers — 11 bytes per trigger (mode + 10 param bytes). The
            // simple modes (Feedback / Weapon / Vibration) use scalar
            // opcodes 0x01/0x02/0x06; multi-position modes (MultiplePosition*,
            // Slope) use the official 0x21/0x26 zone-bitmap encoding.
            // Always assert the trigger-write enable bits when User Effects
            // are on. Without these, switching the mode to Off doesn't
            // release the trigger because the firmware ignores the trigger
            // bytes entirely (mode byte 0x00 + zeros never reaches the
            // haptic motor). Setting the enable bit unconditionally tells
            // the firmware "process the trigger bytes," which carries the
            // 0x00 mode through and releases.
            byte[] rightTrig;
            byte[] leftTrig;
            if (overrides.RightTriggerEffect != null && overrides.RightTriggerEffect.Length >= 11)
            {
                rightTrig = overrides.RightTriggerEffect;
            }
            else
            {
                rightTrig = new byte[11];
                if (cfg != null)
                {
                    EncodeTrigger(cfg.RightTriggerMode,
                        cfg.RightStartPosition, cfg.RightEndPosition,
                        cfg.RightStrength, cfg.RightFrequency,
                        rightTrig);
                }
            }
            if (overrides.LeftTriggerEffect != null && overrides.LeftTriggerEffect.Length >= 11)
            {
                leftTrig = overrides.LeftTriggerEffect;
            }
            else
            {
                leftTrig = new byte[11];
                if (cfg != null)
                {
                    EncodeTrigger(cfg.LeftTriggerMode,
                        cfg.LeftStartPosition, cfg.LeftEndPosition,
                        cfg.LeftStrength, cfg.LeftFrequency,
                        leftTrig);
                }
            }
            // AT trigger enable bits are gated by the dispatcher's
            // per-device tracking. Asserted when PadForge wants AT (cfg
            // mode != Off), when external is currently mirroring, or for
            // a one-shot drop frame on cfg's Off-transition. Otherwise
            // cleared so the firmware retains the last applied state —
            // critical for letting an external program's AT engagement
            // persist past our 1500ms mirror grace window when the user
            // has no PadForge-side AT configured.
            if (assertRightTriggerEnable) enableBits |= EnableRightTrigger;
            if (assertLeftTriggerEnable)  enableBits |= EnableLeftTrigger;

            // BT DS5 spec adds "btTag" at byte 1 (USB spec ignores it).
            // The encoder writes only the fields its profile declares.
            return new Dictionary<string, object>
            {
                { "btTag",            NextBtSeqTag() },
                { "validFlag0",       (byte)(enableBits & 0xFF) },
                { "validFlag1",       (byte)((enableBits >> 8) & 0xFF) },
                { "rightMotor",       effectiveRumbleR },
                { "leftMotor",        effectiveRumbleL },
                { "muteLed",          muteLed },
                { "rightTriggerEffect", rightTrig },
                { "leftTriggerEffect",  leftTrig  },
                { "validFlag2",       (byte)0xFF },
                { "lightbarSetup",    overrides.LightbarSetup ?? (byte)0x02 },
                { "ledBrightness",    ledBrightness },
                { "playerIndicator",  playerIndicator },
                { "lightbar",         new byte[] { ledR, ledG, ledB } },
            };
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
            float pulseIntensity,
            byte batteryPercent = 100)
            => ComputeLightbarColor(cfg, audioPeak, nowMs, randomColor, pulseColor, pulseIntensity, batteryPercent);

        /// <summary>Public adapter for <see cref="ResolveReactiveOverlayColor"/>
        /// so the DS4 synthesizer can compose the same overlay logic
        /// without duplicating it. Reactive variants (Random, Cycle,
        /// Fixed) all read dispatcher-rolled state for randomColor /
        /// pulseColor + the slot's static base RGB for Fixed.</summary>
        public static (byte r, byte g, byte b) ResolveReactiveOverlayColorPublic(
            PlayStationSlotConfig cfg,
            uint randomColor,
            uint pulseColor)
            => ResolveReactiveOverlayColor(cfg, randomColor, pulseColor);

        /// <summary>Picks the overlay color based on
        /// <see cref="PlayStationSlotConfig.InputReactiveMode"/>:
        /// Random uses the dispatcher's per-press random hue,
        /// Cycle uses the per-press palette pick, and Fixed
        /// uses the slot's configured base RGB. Caller is
        /// responsible for blending this with the base mode color
        /// via <see cref="LerpColor"/> at the current pulse intensity.</summary>
        private static (byte r, byte g, byte b) ResolveReactiveOverlayColor(
            PlayStationSlotConfig cfg,
            uint randomColor,
            uint pulseColor)
        {
            switch (cfg.InputReactiveMode)
            {
                case InputReactiveMode.Random:
                case InputReactiveMode.Cycle:
                    return (
                        (byte)((pulseColor >> 16) & 0xFF),
                        (byte)((pulseColor >> 8) & 0xFF),
                        (byte)(pulseColor & 0xFF));
                case InputReactiveMode.Fixed:
                    return (cfg.InputReactiveR, cfg.InputReactiveG, cfg.InputReactiveB);
                default:
                    return (0, 0, 0);
            }
        }

        /// <summary>Reduces the active <see cref="LightbarMode"/> plus
        /// dynamic inputs (audio peak, wall-clock timestamp, dispatcher-
        /// rolled random color, dispatcher-tracked input pulse) to a final
        /// RGB triple for the lightbar field. Stateless; the dispatcher
        /// owns all state.</summary>
        private static (byte r, byte g, byte b) ComputeLightbarColor(
            PlayStationSlotConfig cfg,
            float audioPeak,
            long nowMs,
            uint randomColor,
            uint pulseColor,
            float pulseIntensity,
            byte batteryPercent = 100)
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
                {
                    // Scale HSV V by user-configured brightness (0..100 →
                    // 0..1.0). Rainbow has no per-color picker for the user
                    // to dim explicitly, so this slider is the only knob.
                    double rainbowV = Math.Clamp(cfg.LightbarRainbowBrightness / 100.0, 0.0, 1.0);
                    return HsvToRgb(phase * 360.0, 1.0, rainbowV);
                }

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

                case LightbarMode.Battery:
                {
                    // Linear interpolation between the user-configured Low
                    // (default red @ 0%) and High (default green @ 100%)
                    // colors driven by the per-device battery percent. -1
                    // sentinel from SDL ("unknown") falls back to High so
                    // the user sees the full-charge color rather than the
                    // empty-battery red on a controller that's plugged in
                    // but not yet reporting.
                    float t = batteryPercent >= 100 ? 1f
                            : batteryPercent <=   0 ? 0f
                            : batteryPercent / 100f;
                    return (
                        (byte)Math.Round(cfg.LightbarBatteryLowR  + (cfg.LightbarBatteryHighR - cfg.LightbarBatteryLowR) * t),
                        (byte)Math.Round(cfg.LightbarBatteryLowG  + (cfg.LightbarBatteryHighG - cfg.LightbarBatteryLowG) * t),
                        (byte)Math.Round(cfg.LightbarBatteryLowB  + (cfg.LightbarBatteryHighB - cfg.LightbarBatteryLowB) * t));
                }

                case LightbarMode.Strobe:
                {
                    // Square wave at LightbarPeriodMs cadence: first half
                    // of the period shows the configured base color, second
                    // half is off. Phase already wraps via the period
                    // calculation at the top of this method.
                    if (phase < 0.5)
                        return (cfg.LightbarRed, cfg.LightbarGreen, cfg.LightbarBlue);
                    return (0, 0, 0);
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
        /// <summary>Builds an 11-byte trigger effect block in AdaptiveTrigger
        /// Vibration mode (HidModeAutoTrigger, 0x06) for the impulse-trigger
        /// auto-route. When the virtual controller is XInput-class and the
        /// game writes an impulse trigger motor value (XINPUT_VIBRATION_EX
        /// bytes 4 / 5), the dispatcher synthesizes one of these blocks and
        /// stuffs it into <see cref="UserEffectsDispatcher.ExternalSubsystemOverrides.RightTriggerEffect"/>
        /// (or LeftTriggerEffect), which takes precedence over the user's
        /// configured Adaptive Triggers tab cfg. The user's cfg resumes the
        /// moment the game stops writing the motor — override-with-resume
        /// semantics, same shape as <c>ConstantTriggerForceEvaluator</c>.
        /// Reference: Special K's playstation.cpp:3004 / 4926.</summary>
        /// <param name="strength">Amplitude byte (0..255). Caller has already
        /// scaled by ImpulseOverallGain / Impulse{Left,Right}Strength + audio-
        /// trigger mix + ImpulseSwapTriggers via
        /// <c>InputManager.ScaleTriggerRumbleForDevice</c>.</param>
        public static byte[] BuildAtVibrationOverrideBlock(byte strength)
        {
            var block = new byte[11];
            block[0] = HidModeAutoTrigger;
            block[1] = ImpulseAtVibrationFrequency;
            block[2] = strength;
            block[3] = 0; // start position 0 — buzz active across full trigger range
            return block;
        }

        /// <summary>Builds a HID Resistance (Feedback) override block — constant
        /// resistance from the start of the trigger pull, scaled by
        /// <paramref name="force"/> (0..255). Used by the steering at-lock
        /// AT-resistance ramp (#94): force tracks how close the wheel is to lock.</summary>
        public static byte[] BuildAtResistanceOverrideBlock(byte force)
        {
            var block = new byte[11];
            block[0] = HidModeResistance;  // 0x01 — [start_pos, force]
            block[1] = 0;                  // start position 0 — resist across the full pull
            block[2] = force;
            return block;
        }

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
