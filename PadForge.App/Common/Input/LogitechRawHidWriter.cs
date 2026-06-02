using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Writes native Logitech wheel force feedback (constant force, autocenter,
    /// rotation range) via the Logitech custom 7-byte HID output report, bypassing
    /// SDL3 / DirectInput. Logitech wheels expose their full feature set only
    /// through this protocol; the DirectInput-compatible mode some wheels also
    /// expose loses autocenter quality, hardware range, and the LED tach.
    ///
    /// <para>Wire protocol verified against <c>berarma/new-lg4ff</c>
    /// (<c>hid-lg4ff.c</c>) 2026-06-01 — bytes are unprotectable Logitech
    /// hardware facts; this is an original C# implementation, no GPL source
    /// translation. Mirrors <see cref="SonyEffectWriter"/>'s overlapped raw-HID
    /// write plumbing (the current raw-HID-output writer pattern; the old
    /// <c>Ds5RawHidWriter</c>/vJoy model is gone as of v3).</para>
    ///
    /// <para>Phase 1: G29 / G920 / G923 family constant force + autocenter +
    /// range. Older G25/G27/Driving Force/MOMO devices use the same shape and
    /// light up by adding PIDs to <see cref="IsLogitechWheel"/>.</para>
    ///
    /// <para>HARDWARE VERIFICATION (Thursday): the Windows output report framing
    /// here assumes report ID 0 with the 7 command bytes at offset 1 (8-byte
    /// report). If the wheel's <c>OutputReportByteLength</c> differs, adjust
    /// <see cref="BuildReport"/> — that's the one device-specific unknown.</para>
    /// </summary>
    internal static class LogitechRawHidWriter
    {
        public const ushort LogitechVid = 0x046D;

        // Phase 1 native-mode PIDs. Compatibility-mode PIDs (e.g. 0xC298 DFGT
        // legacy) are intentionally absent — when a wheel boots in DInput-compat
        // mode the lookup misses and FFB falls back to the SDL path silently.
        public static bool IsLogitechWheel(ushort vid, ushort pid)
        {
            if (vid != LogitechVid) return false;
            switch (pid)
            {
                case 0xC24F: // G29 (PS3 / PC)
                case 0xC260: // G29 (PS4)
                case 0xC262: // G920 (Xbox / PC)
                case 0xC267: // G923 (PS / PC)
                case 0xC266: // G923 (Xbox / PC)
                    return true;
                default:
                    return false;
            }
        }

        // ─────────────────────────────────────────────
        //  Protocol — values from new-lg4ff/hid-lg4ff.c
        // ─────────────────────────────────────────────

        // TRANSLATE_FORCE(x) = (clamp_s16(x) + 0x8000) >> 8 : signed 16-bit force
        // level -> byte, 0x80 neutral, sign carries direction (single steering axis).
        private static byte TranslateForce(int levelS16)
        {
            if (levelS16 < -0x8000) levelS16 = -0x8000;
            else if (levelS16 > 0x7fff) levelS16 = 0x7fff;
            return (byte)((levelS16 + 0x8000) >> 8);
        }

        // Per-(devicePath, slot) download-vs-refresh state. Logitech wants a
        // download op (0x1) on first arm, then refresh (0xc) on updates; a stop
        // (0x3) resets it. Keyed by path#slot. The FFB loop is single-threaded
        // per slot; ConcurrentDictionary guards against the rare cross-thread
        // teardown race.
        private static readonly ConcurrentDictionary<string, bool> _loaded = new();

        /// <summary>Constant force on a slot. <paramref name="levelS16"/> is the
        /// signed steering-axis force (-0x8000..0x7fff); 0 centers. Returns false
        /// on write failure or unrecognized path.</summary>
        public static bool WriteConstantForce(string devicePath, int slot, int levelS16)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            string key = devicePath + "#" + slot;
            bool wasLoaded = _loaded.TryGetValue(key, out bool v) && v;
            int op = wasLoaded ? 0xc : 0x1;                 // refresh : download
            byte[] cmd = new byte[7];
            cmd[0] = (byte)((0x10 << slot) | op);
            cmd[1] = 0x00;                                  // FF_CONSTANT effect type
            cmd[2 + slot] = TranslateForce(levelS16);       // force at index 2+slot
            bool ok = RawHidOutput.Write(devicePath, BuildReport(cmd));
            if (ok) _loaded[key] = true;
            return ok;
        }

        /// <summary>Stops a slot's effect (op 0x3) and clears its loaded state.</summary>
        public static bool WriteStopEffect(string devicePath, int slot)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            byte[] cmd = new byte[7];
            cmd[0] = (byte)((0x10 << slot) | 0x3);
            bool ok = RawHidOutput.Write(devicePath, BuildReport(cmd));
            _loaded[devicePath + "#" + slot] = false;
            return ok;
        }

        /// <summary>Sets autocenter spring strength. <paramref name="magnitude"/>
        /// 0 deactivates; 1..0xffff sets the curve (new-lg4ff expand_a/expand_b),
        /// then activates. <paramref name="isMomo"/> skips the non-MOMO a>>=1.</summary>
        public static bool WriteAutocenter(string devicePath, int magnitude, bool isMomo = false)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            if (magnitude <= 0)
            {
                byte[] off = new byte[7];
                off[0] = 0xf5;
                return RawHidOutput.Write(devicePath, BuildReport(off));
            }
            if (magnitude > 0xffff) magnitude = 0xffff;

            long a, b;
            if (magnitude <= 0xaaaa)
            {
                a = 0x0cL * magnitude;
                b = 0x80L * magnitude;
            }
            else
            {
                a = 0x0cL * 0xaaaa + 0x06L * (magnitude - 0xaaaa);
                b = 0x80L * 0xaaaa + 0xffL * (magnitude - 0xaaaa);
            }
            if (!isMomo) a >>= 1;

            byte[] cmd = new byte[7];
            cmd[0] = 0xfe;
            cmd[1] = 0x0d;
            cmd[2] = (byte)(a / 0xaaaa);
            cmd[3] = (byte)(a / 0xaaaa);
            cmd[4] = (byte)(b / 0xaaaa);
            if (!RawHidOutput.Write(devicePath, BuildReport(cmd))) return false;

            byte[] activate = new byte[7];
            activate[0] = 0x14;
            return RawHidOutput.Write(devicePath, BuildReport(activate));
        }

        /// <summary>Sets the wheel's hardware rotation range in degrees
        /// (G25/G27/DFGT/G29/G920/G923 format: <c>f8 81 lo hi</c>).</summary>
        public static bool WriteRange(string devicePath, int degrees)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            if (degrees < 40) degrees = 40;
            else if (degrees > 1080) degrees = 1080;
            byte[] cmd = new byte[7];
            cmd[0] = 0xf8;
            cmd[1] = 0x81;
            cmd[2] = (byte)(degrees & 0xff);
            cmd[3] = (byte)((degrees >> 8) & 0xff);
            return RawHidOutput.Write(devicePath, BuildReport(cmd));
        }

        // Frames the 7-byte command into a Windows HID output report: byte[0] =
        // report ID (0 for these wheels), command bytes at offset 1. See the
        // class-level HARDWARE VERIFICATION note re: report length.
        private static byte[] BuildReport(byte[] cmd7)
        {
            byte[] report = new byte[8];
            report[0] = 0x00; // report ID
            Array.Copy(cmd7, 0, report, 1, 7);
            return report;
        }

    }
}
