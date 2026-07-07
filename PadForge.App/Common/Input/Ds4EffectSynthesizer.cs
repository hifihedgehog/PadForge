using System;
using System.Collections.Generic;
using PadForge.ViewModels;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Resolves <see cref="PlayStationSlotConfig"/> into a parsed-field
    /// dictionary for DualShock 4 effect output. The dictionary is fed to
    /// <c>HMOutputEncoder.Encode(profile, fields)</c>, which packs the bytes
    /// per the active profile's <c>extendedOutputReport</c> spec — USB
    /// (Report 0x05, 32B) or BT (Report 0x11, 78B with CRC32). The same
    /// dictionary serves both transports; the encoder picks the fields it
    /// needs from the profile spec and ignores the rest.
    ///
    /// <para>HIDMaestro v1.3.5 introduced the data-driven encoder; the
    /// pre-v1.3.5 implementation hand-packed the full 32 / 78-byte report
    /// at compile-time-known offsets, plus a separate hand-rolled CRC32
    /// for the BT envelope. Now: one dictionary, the SDK does the rest.</para>
    ///
    /// <para>DS4 is much simpler than DualSense — no adaptive triggers,
    /// no player-indicator row, no mic LED, no audio output. Two output
    /// report shapes (both expressed via a single dictionary):</para>
    ///
    /// <list type="bullet">
    /// <item><b>USB Report 0x05</b> — 32 bytes. Encoder consumes
    /// <c>validFlag0 / validFlag1 / rightMotor / leftMotor / lightbar /
    /// flashOn / flashOff</c>.</item>
    /// <item><b>Bluetooth Report 0x11</b> — 78 bytes. Encoder additionally
    /// consumes <c>btTag / btReserved</c> (the ds4drv two-byte BT framing
    /// header) and writes the CRC32 footer at bytes 74-77.</item>
    /// </list>
    ///
    /// <para>Lightbar override priority matches the DS5 synthesizer:</para>
    /// <list type="number">
    /// <item>Game-driven Feature A passthrough (separate dispatcher,
    /// not handled here).</item>
    /// <item>Macro-driven override (<see cref="PlayStationSlotConfig.HasActiveMacroLightbarOverride"/>).</item>
    /// <item>Configured <see cref="LightbarMode"/> (animated audio /
    /// breathing / palette / etc.) — same <see cref="ComputeLightbarColor"/>
    /// helper as DS5.</item>
    /// <item>Off — bytes left zero.</item>
    /// </list>
    ///
    /// <para>══════════════════════════════════════════════════════════════</para>
    /// <para><b>SOLE-WRITER CONTRACT — read before changing rumble bytes.</b></para>
    /// <para>══════════════════════════════════════════════════════════════</para>
    /// <para>Same architecture as <see cref="Ds5EffectSynthesizer"/>:
    /// <c>InputManager.Step2.ApplyForceFeedback</c> skips Sony VID 0x054C
    /// / DS4 PIDs so SDL_RumbleJoystick is never called for these
    /// devices. This synthesizer's packet is the ONLY DS4 effect write
    /// from PadForge — rumble + lightbar + flash, all in one.</para>
    /// <para>Therefore: validFlag0 = 0xF7 unconditionally (bit 0 = rumble
    /// enable, bits 1-2 = lightbar/flash enable). Rumble bytes are always
    /// carried; the dispatcher produces audio-mixed + gain-scaled values
    /// via <c>InputService.SlotRumbleForDeviceProvider</c>. Do NOT clear
    /// bit 0 "when rumble is silent" — the firmware retains the last
    /// applied value, and gating bit 0 just creates dead zones in the
    /// dispatch stream.</para>
    /// <para>See memory: sony-rumble-sole-writer-architecture.md.</para>
    /// </summary>
    internal static class Ds4EffectSynthesizer
    {
        // Validity flags. 0xF7 enables rumble (bit 0), lightbar RGB (bit 1),
        // lightbar flash (bit 2), and a few additional update bits the
        // firmware checks. OpenRGB uses 0xF7 for both USB and BT.
        private const byte ValidFlagsAll = 0xF7;

        // BT framing header bytes — placed at byte 1 (btTag) and byte 2
        // (btReserved) of Report 0x11 by the encoder when the BT profile
        // is selected. ds4drv's reference implementation uses 0xC0 / 0xA0;
        // OpenRGB matches. The USB profile encoder ignores these fields
        // because they aren't declared in the USB spec.
        private const byte BtFramingTag      = 0xC0;
        private const byte BtFramingReserved = 0xA0;

        /// <summary>Builds the parsed-field dictionary for one DualShock 4
        /// effect packet. Pass to <c>HMOutputEncoder.Encode</c> with either
        /// the USB (Report 0x05) or BT (Report 0x11) DS4 profile — the dict
        /// carries both transports' fields and the encoder picks what it
        /// needs.</summary>
        public static Dictionary<string, object> BuildFields(
            PlayStationSlotConfig cfg,
            float audioPeak,
            long nowMs,
            uint randomColor,
            uint pulseColor,
            float pulseIntensity,
            byte rumbleRight,
            byte rumbleLeft,
            bool assertRumbleEnable = true,
            UserEffectsDispatcher.ExternalSubsystemOverrides overrides = default,
            byte batteryPercent = 100,
            int playerNumber = 0)
        {
            // Per-subsystem mirroring: when a host has recently written
            // rumble or lightbar to our virtual, mirror their bytes
            // verbatim instead of overwriting with PadForge's own values.
            // PadForge keeps owning whichever subsystems the external
            // writer DIDN'T touch (animation continues on lightbar if the
            // writer only sent rumble, and vice versa). Same pattern as
            // Ds5EffectSynthesizer — see there for the long version.

            bool rumbleExternal = overrides.RumbleRight.HasValue && overrides.RumbleLeft.HasValue;
            byte effectiveRumbleR = rumbleExternal ? overrides.RumbleRight.Value : rumbleRight;
            byte effectiveRumbleL = rumbleExternal ? overrides.RumbleLeft.Value  : rumbleLeft;

            byte r, g, bRgb;
            if (overrides.LightbarRgb != null && overrides.LightbarRgb.Length >= 3)
            {
                r = overrides.LightbarRgb[0];
                g = overrides.LightbarRgb[1];
                bRgb = overrides.LightbarRgb[2];
            }
            else
            {
                ResolveLightbarRgb(cfg, audioPeak, nowMs, randomColor, pulseColor, pulseIntensity, batteryPercent,
                    playerNumber, out r, out g, out bRgb);
            }

            // Rumble enable bit (validFlag0 bit 0) is asserted whenever
            // rumble is owned by either side (PadForge or external) so the
            // firmware applies the bytes the dict carries. PadForge's
            // own-rumble drop-frame logic is in the dispatcher upstream;
            // external mirroring forces the bit on for the duration of
            // the grace window.
            bool emitRumble = rumbleExternal || assertRumbleEnable;
            byte validFlag0 = emitRumble
                ? ValidFlagsAll
                : (byte)(ValidFlagsAll & ~0x01);

            // No user-configurable flash for now — leave on/off zeroed so
            // the firmware holds the chosen colour without blinking.
            return new Dictionary<string, object>
            {
                { "btTag",       BtFramingTag      },  // BT only
                { "btReserved",  BtFramingReserved },  // BT only
                { "validFlag0",  validFlag0        },
                { "validFlag1",  (byte)0           },
                { "rightMotor",  effectiveRumbleR  },
                { "leftMotor",   effectiveRumbleL  },
                { "lightbar",    new byte[] { r, g, bRgb } },
                { "flashOn",     (byte)0           },
                { "flashOff",    (byte)0           },
            };
        }

        // ────────────────────────────────────────────────
        //  Lightbar resolution
        // ────────────────────────────────────────────────

        private static void ResolveLightbarRgb(
            PlayStationSlotConfig cfg,
            float audioPeak,
            long nowMs,
            uint randomColor,
            uint pulseColor,
            float pulseIntensity,
            byte batteryPercent,
            int playerNumber,
            out byte r, out byte g, out byte b)
        {
            // Player-identity idle floor (#191): when the lighting is
            // fully unconfigured (no macro, mode Off, no reactive
            // overlay), the lightbar shows the Sony player color for
            // this pad's virtual controller number instead of the black
            // this method used to paint. Everything configured keeps
            // its existing priority below.
            bool unconfigured = cfg == null
                || (cfg.ComputeMacroOverrideIntensity() <= 0f
                    && cfg.LightbarMode == LightbarMode.Off
                    && cfg.InputReactiveMode == InputReactiveMode.Off);
            if (unconfigured)
            {
                if (playerNumber > 0)
                {
                    (r, g, b) = PlayerIdentityDefaults.ColorFor(playerNumber);
                    return;
                }
                r = 0; g = 0; b = 0;
                return;
            }

            r = 0; g = 0; b = 0;

            // Priority 1: macro-driven override. Intensity = 1.0 for
            // Sticky holds, fades 1.0 → 0.0 over the Reactive decay
            // window. RGB scaled by intensity so a Reactive flash fades
            // out smoothly. Macro override always wins.
            float overrideIntensity = cfg.ComputeMacroOverrideIntensity();
            if (overrideIntensity > 0f)
            {
                r = (byte)Math.Round(cfg.MacroOverrideR * overrideIntensity);
                g = (byte)Math.Round(cfg.MacroOverrideG * overrideIntensity);
                b = (byte)Math.Round(cfg.MacroOverrideB * overrideIntensity);
                return;
            }

            // Priority 2: configured base mode (animated / static / audio).
            // Off collapses to a black base — the overlay below can still
            // flash a reactive color over it.
            byte baseR = 0, baseG = 0, baseB = 0;
            if (cfg.LightbarMode != LightbarMode.Off)
            {
                (baseR, baseG, baseB) = Ds5EffectSynthesizer.ComputeLightbarColorPublic(
                    cfg, audioPeak, nowMs, randomColor, pulseColor, pulseIntensity, batteryPercent);
            }

            // Priority 3: input-reactive overlay. Lerps from the base
            // color toward the overlay color by pulseIntensity, so a
            // press flashes the reactive flavor and decays back to the
            // base. Off + reactive collapses to a black base, matching
            // legacy InputReactive*-as-base behaviour.
            if (cfg.InputReactiveMode != InputReactiveMode.Off && pulseIntensity > 0f)
            {
                var (rR, rG, rB) = Ds5EffectSynthesizer.ResolveReactiveOverlayColorPublic(
                    cfg, randomColor, pulseColor);
                float t = Math.Clamp(pulseIntensity, 0f, 1f);
                r = (byte)Math.Round(baseR + (rR - baseR) * t);
                g = (byte)Math.Round(baseG + (rG - baseG) * t);
                b = (byte)Math.Round(baseB + (rB - baseB) * t);
                return;
            }

            r = baseR; g = baseG; b = baseB;
        }
    }
}
