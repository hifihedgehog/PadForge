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
    /// <para>The per-vendor command is 8 logical bytes (report ID 0 + 7 command
    /// bytes). <see cref="RawHidOutput"/> pads each write to the device's actual
    /// <c>OutputReportByteLength</c> before sending, so wheels whose joystick
    /// collection uses longer output reports (the G29 wants 17) accept the
    /// command instead of rejecting it with ERROR_INVALID_PARAMETER.</para>
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
                // 0xC262 (G920) intentionally absent: it uses HID++ 2.0 FFB, not this
                // classic lg4ff 7-byte protocol (lg4ff README; 0xC262 is not in the
                // lg4ff device table). Sending f8/fe/slot commands does nothing on it,
                // so it falls back to the SDL haptic path (Windows Logitech driver).
                case 0xC267: // G923 (PS / PC)
                case 0xC266: // G923 (PS / PC) - lg4ff variant; the Xbox G923 is 0xC26E (HID++, not this protocol)
                case 0xC29B: // G27 (native mode) — same lg4ff protocol + 5-LED strip
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Wheels with a real friction effect (lg4ff LG4FF_CAP_FRICTION).
        /// Wheels without it cast friction to damper, matching lg4ff_play_effect
        /// (hid-lg4ff.c:1107-1110); their device-table caps field is 0 (G29/G920/G923).
        /// Among PadForge's supported wheels only the G27 (0xC29B) qualifies; the
        /// legacy DFP/G25/DFGT also have it but are not in <see cref="IsLogitechWheel"/>.</summary>
        public static bool HasFrictionCap(ushort pid) => pid == 0xC29B;

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

        /// <summary>Clears the per-slot download/refresh state for a device. Called on
        /// unplug/reassign so the next effect re-downloads (op 0x1) instead of refreshing
        /// (op 0xc) a slot the power-cycled firmware reset to empty.</summary>
        public static void ResetDevice(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return;
            string prefix = devicePath + "#";
            foreach (var key in _loaded.Keys)
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    _loaded.TryRemove(key, out _);
        }

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
            else if (degrees > 900) degrees = 900; // every lg4ff Logitech wheel maxes at 900 deg (hid-lg4ff.c device table); lg4ff rejects higher
            byte[] cmd = new byte[7];
            cmd[0] = 0xf8;
            cmd[1] = 0x81;
            cmd[2] = (byte)(degrees & 0xff);
            cmd[3] = (byte)((degrees >> 8) & 0xff);
            return RawHidOutput.Write(devicePath, BuildReport(cmd));
        }

        // ── Condition effects: spring (0x0b) / damper (0x0c) / friction (0x0e) ──
        // Byte layout + scaling from lg4ff_update_slot. Coefficients/saturation/
        // deadband arrive in DirectInput units (coeff/offset ±10000, sat/deadband
        // 0..10000); converted to the wheel's HID logical range then scaled per
        // the driver's SCALE_COEFF / SCALE_VALUE_U16 macros. Coefficient feel is
        // hardware-tuned, but the wire encoding is verified.
        private const uint EffSpring = 0x0b, EffDamper = 0x0c, EffFriction = 0x0e;

        private static int ClampU16(int x) => x < 0 ? 0 : (x > 0xffff ? 0xffff : x);
        private static int ScaleU16(int x, int bits) => ClampU16(x) >> (16 - bits);
        private static int ScaleCoeff(int x, int bits) => ScaleU16(System.Math.Abs(x) * 2, bits);
        // DInput ±10000 (or 0..10000) -> HID logical ±0x7fff (0..0x7fff), with gain.
        private static int ToHid(int dinput, int gainPct) =>
            (int)((long)dinput * gainPct / 100 * 0x7fff / 10000);

        /// <summary>Spring/damper/friction on a slot. coeffPos/coeffNeg and offset
        /// are DInput ±10000; deadband/satPos/satNeg are 0..10000. <paramref name="ffbType"/>
        /// is a PadForge FfbEffectTypes value (Spring=8, Damper=9, Inertia=10,
        /// Friction=11); it selects the Logitech effect command.</summary>
        public static bool WriteCondition(string devicePath, int slot, uint ffbType,
            int coeffPos, int coeffNeg, int offset, int deadband, int satPos, int satNeg, int gainPct,
            bool capFriction)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            string key = devicePath + "#" + slot;
            int op = (_loaded.TryGetValue(key, out bool ld) && ld) ? 0xc : 0x1;

            // lg4ff: k1 = left_coeff = negative, k2 = right_coeff = positive,
            // clip = right_saturation = positive (calculate_spring/resistance +
            // the DInput condition mapping right=positive, hid-ftec.c:571-575).
            // The caller passes (coeffPos, coeffNeg, ..., satPos, satNeg).
            int k1 = ToHid(coeffNeg, gainPct);
            int k2 = ToHid(coeffPos, gainPct);
            int clip = ClampU16((int)((long)(satPos == 0 ? 10000 : satPos) * gainPct / 100 * 0xffff / 10000)); // 0..0xffff, gain-scaled like lg4ff (clip *= gain); SCALE_VALUE_U16(clip,8) -> 0xff at full
            int s1 = k1 < 0 ? 1 : 0, s2 = k2 < 0 ? 1 : 0;

            byte[] cmd = new byte[7];
            cmd[0] = (byte)((0x10 << slot) | op);
            if (ffbType == 8) // FfbEffectTypes.Spring
            {
                // Deadband edges around center, mapped to HID then SCALE_U16(,11).
                int center = ToHid(offset, 100), half = ToHid(deadband, 100);
                int d1 = ScaleU16(((center - half) + 0x8000) & 0xffff, 11);
                int d2 = ScaleU16(((center + half) + 0x8000) & 0xffff, 11);
                int ak1 = System.Math.Abs(k1), ak2 = System.Math.Abs(k2);
                if (ak1 < 2048) d1 = 0; else ak1 -= 2048;
                if (ak2 < 2048) d2 = 2047; else ak2 -= 2048;
                cmd[1] = (byte)EffSpring;
                cmd[2] = (byte)(d1 >> 3);
                cmd[3] = (byte)(d2 >> 3);
                cmd[4] = (byte)((ScaleCoeff(ak2, 4) << 4) + ScaleCoeff(ak1, 4));
                cmd[5] = (byte)(((d2 & 7) << 5) + ((d1 & 7) << 1) + (s2 << 4) + s1);
                cmd[6] = (byte)ScaleU16(clip, 8);
            }
            else if (ffbType == 11 && capFriction) // FfbEffectTypes.Friction; non-cap wheels fall through to damper (lg4ff_play_effect cast)
            {
                cmd[1] = (byte)EffFriction;
                cmd[2] = (byte)ScaleCoeff(k1, 8);
                cmd[3] = (byte)ScaleCoeff(k2, 8);
                cmd[4] = (byte)ScaleU16(clip, 8);
                cmd[5] = (byte)((s2 << 4) + s1);
            }
            else // damper / inertia
            {
                cmd[1] = (byte)EffDamper;
                cmd[2] = (byte)ScaleCoeff(k1, 4);
                cmd[3] = (byte)s1;
                cmd[4] = (byte)ScaleCoeff(k2, 4);
                cmd[5] = (byte)s2;
                cmd[6] = (byte)ScaleU16(clip, 8);
            }
            bool ok = RawHidOutput.Write(devicePath, BuildReport(cmd));
            if (ok) _loaded[key] = true;
            return ok;
        }

        /// <summary>Sets the 5 RPM / shift LEDs on the wheel face.
        /// <paramref name="ledMask"/> bit 0 = first (lowest-RPM) LED .. bit 4 = fifth,
        /// matching the kernel's RPM1..RPM5 fill order. Command <c>f8 12 [mask]</c>
        /// (new-lg4ff <c>lg4ff_set_leds</c>, cross-checked vs mainline Linux +
        /// oversteer). Driving these from telemetry is why native mode matters.</summary>
        public static bool WriteRpmLeds(string devicePath, byte ledMask)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            byte[] cmd = new byte[7];
            cmd[0] = 0xf8;
            cmd[1] = 0x12;
            cmd[2] = (byte)(ledMask & 0x1f);
            return RawHidOutput.Write(devicePath, BuildReport(cmd));
        }

        // Frames the 7-byte command into the logical HID output report: byte[0] =
        // report ID (0 for these wheels), command bytes at offset 1. RawHidOutput
        // zero-pads this to the device's OutputReportByteLength before WriteFile.
        private static byte[] BuildReport(byte[] cmd7)
        {
            byte[] report = new byte[8];
            report[0] = 0x00; // report ID
            Array.Copy(cmd7, 0, report, 1, 7);
            return report;
        }

    }
}
