using System;
using PadForge.ViewModels;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Builds DualShock 4 output report packets (lightbar RGB, rumble,
    /// flash). Stateless. Reads <see cref="PlayStationSlotConfig"/> for
    /// the configured base color, audio-driven mode, palette, and
    /// macro-driven override; the dispatcher feeds in the audio peak,
    /// random/pulse colour memory, and the slot's rumble state.
    ///
    /// <para>DS4 is much simpler than DualSense — no adaptive triggers,
    /// no player-indicator row, no mic LED, no audio output. Two output
    /// report shapes:</para>
    ///
    /// <list type="bullet">
    /// <item><b>USB Report 0x05</b> — 32 bytes total. Byte 0 = report ID,
    /// byte 1 = validity flags (0xF7 = enable rumble + lightbar + flash),
    /// bytes 2-3 = reserved, byte 4 = small (right) motor, byte 5 = big
    /// (left) motor, bytes 6-8 = R/G/B, bytes 9-10 = flash on/off
    /// duration in 100 ms units.</item>
    /// <item><b>Bluetooth Report 0x11</b> — 78 bytes total. Byte 0 =
    /// report ID, bytes 1-2 = poll-rate / feature header, byte 3 = same
    /// validity flags, bytes 6-12 = same rumble/lightbar/flash payload
    /// at +2 offset, bytes 74-77 = CRC32 over (0xA2 prefix + bytes 0..73).
    /// Same CRC seed and polynomial as the DS5 BT path.</item>
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
    /// <para>Therefore: ValidFlags = 0xF7 unconditionally (bit 0 = rumble
    /// enable, bits 1-2 = lightbar/flash enable). Rumble bytes are always
    /// carried; the dispatcher produces audio-mixed + gain-scaled values
    /// via <c>InputService.SlotRumbleForDeviceProvider</c>. Do NOT clear
    /// bit 0 "when rumble is silent" — the firmware retains the last
    /// applied value, and gating bit 0 just creates dead zones in the
    /// dispatch stream.</para>
    /// <para>Garbage-color bug history: with two writers, SDL's DS4
    /// effect packet alongside its rumble update could carry stale or
    /// zeroed lightbar bytes that raced the dispatcher's animated colors
    /// at 30 Hz, producing what users perceived as "fast cycling" or
    /// "random colors." Sole-writer mode resolved this. Don't reintroduce
    /// SDL DS4 writes.</para>
    /// <para>See memory: sony-rumble-sole-writer-architecture.md.</para>
    /// </summary>
    internal static class Ds4EffectSynthesizer
    {
        // USB: report ID + 31 payload bytes.
        public const int UsbPacketSize = 32;
        // BT: report ID + 1 (poll/config) + 1 (header) + 31 payload + reserved + 4 CRC = 78.
        public const int BluetoothPacketSize = 78;

        // USB report-byte offsets (from start of full 32-byte packet).
        private const int OffUsbReportId    = 0;
        private const int OffUsbValidFlags1 = 1;  // 0xF7 = rumble + lightbar + flash
        private const int OffUsbReserved2   = 2;
        private const int OffUsbReserved3   = 3;
        private const int OffUsbRumbleSmall = 4;  // right-side high-freq motor
        private const int OffUsbRumbleBig   = 5;  // left-side low-freq motor
        private const int OffUsbLedR        = 6;
        private const int OffUsbLedG        = 7;
        private const int OffUsbLedB        = 8;
        private const int OffUsbFlashOn     = 9;
        private const int OffUsbFlashOff    = 10;

        // BT report-byte offsets (from start of full 78-byte packet).
        // Same payload structure shifted by +2 (poll + header bytes).
        private const int OffBtReportId    = 0;   // 0x11
        private const int OffBtPollRate    = 1;   // 0xC0
        private const int OffBtHeader      = 2;   // 0xA0
        private const int OffBtValidFlags1 = 3;   // 0xF7 — same value as USB byte 1
        private const int OffBtReserved4   = 4;
        private const int OffBtReserved5   = 5;
        private const int OffBtRumbleSmall = 6;
        private const int OffBtRumbleBig   = 7;
        private const int OffBtLedR        = 8;
        private const int OffBtLedG        = 9;
        private const int OffBtLedB        = 10;
        private const int OffBtFlashOn     = 11;
        private const int OffBtFlashOff    = 12;

        // Validity flags. 0xF7 enables rumble (bit 0), lightbar RGB (bit 1),
        // lightbar flash (bit 2), and a few additional update bits the
        // firmware checks. OpenRGB uses 0xF7 for both USB and BT.
        private const byte ValidFlagsAll = 0xF7;

        /// <summary>Builds the full 32-byte USB output report into
        /// <paramref name="dst"/>. Returns the number of bytes written
        /// (always <see cref="UsbPacketSize"/>) on success, 0 on
        /// validation failure.</summary>
        public static int BuildUsb(
            PlayStationSlotConfig cfg,
            byte[] dst,
            float audioPeak,
            long nowMs,
            uint randomColor,
            uint pulseColor,
            float pulseIntensity,
            byte rumbleRight,
            byte rumbleLeft)
        {
            if (cfg == null || dst == null || dst.Length < UsbPacketSize) return 0;

            Array.Clear(dst, 0, UsbPacketSize);
            dst[OffUsbReportId]    = 0x05;
            // The dispatcher is now the sole writer of DS4 effect
            // packets — see Step 2 ApplyForceFeedback skip for Sony
            // VID. Always assert all valid flags + carry the
            // dispatcher-computed rumble bytes (audio-mix + per-device
            // gain). No second writer to race with.
            dst[OffUsbValidFlags1] = ValidFlagsAll;
            dst[OffUsbRumbleSmall] = rumbleRight;
            dst[OffUsbRumbleBig]   = rumbleLeft;

            ResolveLightbarRgb(cfg, audioPeak, nowMs, randomColor, pulseColor, pulseIntensity,
                out byte r, out byte g, out byte b);
            dst[OffUsbLedR] = r;
            dst[OffUsbLedG] = g;
            dst[OffUsbLedB] = b;

            // No user-configurable flash for now — leave on/off zeroed so
            // the firmware holds the chosen colour without blinking.
            dst[OffUsbFlashOn]  = 0;
            dst[OffUsbFlashOff] = 0;

            return UsbPacketSize;
        }

        /// <summary>Builds the full 78-byte Bluetooth output report into
        /// <paramref name="dst"/> including the CRC32 trailer. Returns
        /// the number of bytes written on success, 0 on validation
        /// failure.</summary>
        public static int BuildBluetooth(
            PlayStationSlotConfig cfg,
            byte[] dst,
            float audioPeak,
            long nowMs,
            uint randomColor,
            uint pulseColor,
            float pulseIntensity,
            byte rumbleRight,
            byte rumbleLeft)
        {
            if (cfg == null || dst == null || dst.Length < BluetoothPacketSize) return 0;

            Array.Clear(dst, 0, BluetoothPacketSize);
            dst[OffBtReportId]    = 0x11;
            dst[OffBtPollRate]    = 0xC0;
            dst[OffBtHeader]      = 0xA0;
            // Sole-writer model — see USB path above for rationale.
            dst[OffBtValidFlags1] = ValidFlagsAll;
            dst[OffBtRumbleSmall] = rumbleRight;
            dst[OffBtRumbleBig]   = rumbleLeft;

            ResolveLightbarRgb(cfg, audioPeak, nowMs, randomColor, pulseColor, pulseIntensity,
                out byte r, out byte g, out byte b);
            dst[OffBtLedR] = r;
            dst[OffBtLedG] = g;
            dst[OffBtLedB] = b;

            dst[OffBtFlashOn]  = 0;
            dst[OffBtFlashOff] = 0;

            // CRC32 trailer over (0xA2 + bytes 0..73). Matches OpenRGB's
            // SonyDualShock4Controller — same algorithm and 0xA2 seed
            // prefix used for DS5.
            uint crc = ComputeBtCrc(dst, BluetoothPacketSize - 4);
            dst[BluetoothPacketSize - 4] = (byte)(crc & 0xFF);
            dst[BluetoothPacketSize - 3] = (byte)((crc >> 8) & 0xFF);
            dst[BluetoothPacketSize - 2] = (byte)((crc >> 16) & 0xFF);
            dst[BluetoothPacketSize - 1] = (byte)((crc >> 24) & 0xFF);

            return BluetoothPacketSize;
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
            out byte r, out byte g, out byte b)
        {
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
                    cfg, audioPeak, nowMs, randomColor, pulseColor, pulseIntensity);
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

        // ────────────────────────────────────────────────
        //  CRC32 — same poly / seed as Ds5RawHidWriter
        // ────────────────────────────────────────────────

        private static readonly uint[] _crc32Table = BuildCrc32Table();

        private static uint[] BuildCrc32Table()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                t[i] = c;
            }
            return t;
        }

        /// <summary>CRC32 over the 0xA2 output-report prefix concatenated
        /// with the first <paramref name="length"/> bytes of
        /// <paramref name="buf"/>.</summary>
        private static uint ComputeBtCrc(byte[] buf, int length)
        {
            uint crc = 0xFFFFFFFFu;
            crc = _crc32Table[(crc ^ 0xA2) & 0xFF] ^ (crc >> 8);
            for (int i = 0; i < length; i++)
                crc = _crc32Table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
