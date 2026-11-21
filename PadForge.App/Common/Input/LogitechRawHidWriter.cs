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
    /// translation. Mirrors <see cref="PlayStationEffectWriter"/>'s overlapped raw-HID
    /// write plumbing (the current raw-HID-output writer pattern; the old
    /// <c>Ds5RawHidWriter</c>/vJoy model is gone as of v3).</para>
    ///
    /// <para>The wheels driven here are the ones <see cref="IsLogitechWheel"/>
    /// lists: the G29, the G923 for PlayStation and PC, the G27, the G25, the
    /// Driving Force GT and Pro, the two MOMO wheels, the Driving Force and
    /// the WingMan Formula Force GP. The G920 and the Xbox G923 speak HID++
    /// 2.0 and stay on SDL's path.</para>
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

        // The wheels that take the classic lg4ff commands, by product id. A
        // wheel with several modes that starts in its Driving Force mode
        // reports 0xC294 (new-lg4ff, lg4ff_alternate_modes), and on arrival
        // is told to switch to its native mode (SwitchToNativeMode). Until it
        // comes back as its own model, and on a Driving Force itself, 0xC294
        // is driven as that wheel: 270 degrees and no range command.
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
                case 0xC299: // G25 (set_range_g25, friction, 900 deg)
                case 0xC29A: // Driving Force GT (set_range_g25, friction, 900 deg)
                case 0xC298: // Driving Force Pro (coarse+fine range, friction, 900 deg)
                case 0xCA03: // MOMO Racing (MOMO_WHEEL2, fixed 270 deg, no range command)
                case 0xC295: // MOMO Force (MOMO_WHEEL, fixed 270 deg, no range command)
                case 0xC294: // Driving Force (fixed 270 deg, no range command)
                case 0xC293: // WingMan Formula Force GP (fixed 180 deg, no range command)
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Wheels with a real friction effect (lg4ff LG4FF_CAP_FRICTION).
        /// Wheels without it cast friction to damper, matching lg4ff_play_effect
        /// (hid-lg4ff.c:1107-1110). Per the lg4ff device table only G27/G25/DFGT/DFP
        /// carry the cap; every other lg4ff wheel (G29/G920/G923, MOMO, Driving Force,
        /// WingMan) has caps field 0.</summary>
        public static bool HasFrictionCap(ushort pid) => pid is 0xC29B or 0xC299 or 0xC29A or 0xC298; // G27 / G25 / DFGT / DFP (lg4ff LG4FF_CAP_FRICTION)

        /// <summary>MOMO Force (0xC295) / MOMO Racing (0xCA03) keep the full autocenter
        /// expand_a; every other lg4ff wheel halves it. Mirrors the per-product branch in
        /// lg4ff_set_autocenter_default (hid-lg4ff.c:1322-1330).</summary>
        public static bool IsMomo(ushort pid) => pid is 0xC295 or 0xCA03;

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

        // Per-(devicePath, slot) loaded effect: the type byte of the effect the
        // slot holds (0x00 constant, 0x0b spring, 0x0c damper, 0x0e friction),
        // or -1 for none. Logitech wants a download op (0x1) on first arm, then
        // refresh (0xc) on updates of the same type; a stop (0x3) resets it.
        // Keyed by path#slot. The FFB loop is single-threaded per slot;
        // ConcurrentDictionary guards against the rare cross-thread teardown race.
        private static readonly ConcurrentDictionary<string, int> _loaded = new();

        private const int NoEffect = -1;

        /// <summary>The op a write of an effect of <paramref name="type"/>
        /// to a slot takes: a refresh (0xc) of the effect the slot holds, or a
        /// download (0x1). A slot that holds an effect of another type is
        /// stopped first: new-lg4ff never refreshes a slot into another type,
        /// and downloads a slot's effect afresh after a stop (lg4ff_update_slot,
        /// op 3 and then 1). The first slot switched between a game's constant
        /// force and a game's spring with a refresh.</summary>
        private static int OpFor(string devicePath, string key, int slot, byte type)
        {
            if (!_loaded.TryGetValue(key, out int held) || held == NoEffect) return 0x1;
            if (held == type) return 0xc;
            WriteStopEffect(devicePath, slot);
            return 0x1;
        }

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
        /// on write failure or unrecognized path.
        /// <paramref name="levelAtEverySlot"/> writes the level at all four
        /// slots' bytes, for a constant in a slot other than the first. new-lg4ff
        /// puts a slot's level at byte 2 plus the slot (lg4ff_update_slot) and only
        /// ever runs a constant in the first, so nothing on disk shows which byte a
        /// wheel reads for another slot's constant. With the level at all four it
        /// reads the right one whichever that is, and the bytes of the slots the
        /// command does not select are not read.</summary>
        public static bool WriteConstantForce(string devicePath, int slot, int levelS16, bool levelAtEverySlot = false)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            string key = devicePath + "#" + slot;
            int op = OpFor(devicePath, key, slot, 0x00);    // refresh : download
            byte[] cmd = new byte[7];
            cmd[0] = (byte)((0x10 << slot) | op);
            cmd[1] = 0x00;                                  // FF_CONSTANT effect type
            cmd[2 + slot] = TranslateForce(levelS16);       // force at index 2+slot
            if (levelAtEverySlot) cmd[2] = cmd[3] = cmd[4] = cmd[5] = cmd[2 + slot];
            bool ok = RawHidOutput.Write(devicePath, BuildReport(cmd));
            if (ok) _loaded[key] = 0x00;
            return ok;
        }

        /// <summary>Stops a slot's effect (op 0x3) and clears its loaded state.</summary>
        public static bool WriteStopEffect(string devicePath, int slot)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            byte[] cmd = new byte[7];
            cmd[0] = (byte)((0x10 << slot) | 0x3);
            bool ok = RawHidOutput.Write(devicePath, BuildReport(cmd));
            _loaded[devicePath + "#" + slot] = NoEffect;
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

        // Per-wheel max rotation (lg4ff device table): G25/DFGT/DFP/G27/G29/G923 = 900,
        // MOMO Force / MOMO Racing / Driving Force = 270, WingMan Formula Force GP = 180.
        private static int MaxRange(ushort pid) => pid switch
        {
            0xC293 => 180,                      // WingMan Formula Force GP
            0xC294 or 0xC295 or 0xCA03 => 270,  // Driving Force / MOMO Force / MOMO Racing
            _ => 900,                           // G25 / DFGT / DFP / G27 / G29 / G923
        };

        /// <summary>Whether the wheel turns a fixed range and takes no range
        /// command (lg4ff_devices: no set_range for these four).</summary>
        private static bool FixedRange(ushort pid) => pid is 0xC293 or 0xC294 or 0xC295 or 0xCA03;

        /// <summary>The rotation range a model takes, in degrees, which both
        /// <see cref="WriteRange"/> and the Wheel group's slider follow
        /// (<see cref="WheelRangeLimits"/>): 40 up to the lg4ff device table's
        /// maximum, or that one angle for a wheel with a fixed range.</summary>
        internal static (int Min, int Max) RangeLimits(ushort pid)
            => FixedRange(pid) ? (MaxRange(pid), MaxRange(pid)) : (40, MaxRange(pid));

        /// <summary>Sets the wheel's hardware rotation range in degrees, clamped to the
        /// per-wheel max. G25/DFGT/G27/G29/G923 use <c>f8 81 lo hi</c> (lg4ff_set_range_g25);
        /// the Driving Force Pro uses a coarse+fine command (lg4ff_set_range_dfp); MOMO /
        /// Driving Force / WingMan have a fixed range and no range command.</summary>
        public static bool WriteRange(string devicePath, int degrees, ushort pid)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            var (min, max) = RangeLimits(pid);
            if (degrees < min) degrees = min; else if (degrees > max) degrees = max;

            if (FixedRange(pid)) return true; // fixed range, no command
            if (pid == 0xC298) return WriteRangeDfp(devicePath, degrees);     // Driving Force Pro

            byte[] cmd = new byte[7]; // G25 / DFGT / G27 / G29 / G923
            cmd[0] = 0xf8;
            cmd[1] = 0x81;
            cmd[2] = (byte)(degrees & 0xff);
            cmd[3] = (byte)((degrees >> 8) & 0xff);
            return RawHidOutput.Write(devicePath, BuildReport(cmd));
        }

        // Driving Force Pro range (lg4ff_set_range_dfp, hid-lg4ff.c:1401): a coarse limit
        // (f8 00 -> 03 for >200 deg, else 02) then a fine limit (81 0b ...) unless the
        // requested range is exactly 200 or 900.
        private static bool WriteRangeDfp(string devicePath, int degrees)
        {
            byte[] coarse = new byte[7];
            coarse[0] = 0xf8;
            int fullRange;
            if (degrees > 200) { coarse[1] = 0x03; fullRange = 900; }
            else { coarse[1] = 0x02; fullRange = 200; }
            bool ok = RawHidOutput.Write(devicePath, BuildReport(coarse));

            byte[] fine = new byte[7];
            fine[0] = 0x81;
            fine[1] = 0x0b;
            if (degrees == 200 || degrees == 900)
                return ok & RawHidOutput.Write(devicePath, BuildReport(fine));

            int startLeft = ((fullRange - degrees + 1) * 2047) / fullRange;
            int startRight = 0xfff - startLeft;
            fine[2] = (byte)(startLeft >> 4);
            fine[3] = (byte)(startRight >> 4);
            fine[4] = 0xff;
            fine[5] = (byte)(((startRight & 0xe) << 4) | (startLeft & 0xe));
            fine[6] = 0xff;
            return ok & RawHidOutput.Write(devicePath, BuildReport(fine));
        }

        // ─────────────────────────────────────────────
        //  Native mode for a wheel with several modes
        // ─────────────────────────────────────────────

        // A wheel with several modes starts in its Driving Force mode, which
        // reports 0xC294 and turns 270 degrees, until the host tells it to
        // switch. The model shows in bcdDevice, checked in new-lg4ff's order
        // (lg4ff_main_checklist): the G29 before the DFGT and the G27 before
        // the G25, whose masks also match them, and the Driving Force Pro
        // last. The G923 is told apart only in its PlayStation mode, which
        // reports 0xC267 (lg4ff_g923_ident_info lists no Driving Force mode).
        private static readonly (ushort Mask, ushort Result, ushort RealPid)[] DrivingForceModeModels =
        {
            (0xfff8, 0x1350, 0xC24F), // G29 (lg4ff_g29_ident_info)
            (0xff00, 0x8900, 0xC24F), // G29 (lg4ff_g29_ident_info2)
            (0xff00, 0x1300, 0xC29A), // Driving Force GT (lg4ff_dfgt_ident_info)
            (0xfff0, 0x1230, 0xC29B), // G27 (lg4ff_g27_ident_info)
            (0xff00, 0x1200, 0xC299), // G25 (lg4ff_g25_ident_info)
            (0xf000, 0x1000, 0xC298), // Driving Force Pro (lg4ff_dfp_ident_info)
        };

        /// <summary>The native model a wheel that reports
        /// <paramref name="pid"/> is switched to, told by its
        /// <paramref name="bcdDevice"/> (new-lg4ff
        /// lg4ff_identify_multimode_wheel), or 0 for every wheel that is not
        /// switched: one in its own mode, one in a compatibility mode other
        /// than Driving Force, a Driving Force itself, or a model nothing
        /// lists.</summary>
        internal static ushort RealProductId(ushort vid, ushort pid, ushort bcdDevice)
        {
            if (vid != LogitechVid) return 0;
            if (pid == 0xC294)
            {
                foreach (var (mask, result, real) in DrivingForceModeModels)
                    if ((bcdDevice & mask) == result) return real;
                return 0;
            }
            if (pid == 0xC267 && (bcdDevice & 0xff00) == 0x3800) return 0xC266; // G923 (lg4ff_g923_ident_info)
            return 0;
        }

        /// <summary>The output reports, report ID first, that tell a wheel
        /// with several modes to switch to its native mode, or none. As
        /// new-lg4ff does on every probe (lg4ff_handle_multimode_wheel), only a
        /// wheel in its Driving Force mode or a G923 in its PlayStation mode is
        /// switched. A wheel in another mode of its own was put there by
        /// someone, and stays. The Driving Force mode takes the model's own
        /// switch (lg4ff_get_mode_switch_command with the real id twice):
        /// EXT_CMD9 for the G29, the G27 and the DFGT, sent after the command
        /// that makes the switch last only until the next USB reset, EXT_CMD16
        /// for the G25 and EXT_CMD1 for the Driving Force Pro. The G923 takes
        /// its switch in report 0x30 (lg4ff_switch_from_ps_mode). The wheel
        /// then detaches and comes back as its native model.</summary>
        internal static byte[][] NativeModeReports(ushort vid, ushort pid, ushort bcdDevice)
        {
            ushort real = RealProductId(vid, pid, bcdDevice);
            if (real == 0) return Array.Empty<byte[]>();
            if (pid == 0xC267)
                return new[] { Report(0x30, 0xf8, 0x09, 0x07, 0x01, 0x01, 0x00, 0x00) };   // lg4ff_mode_switch_30_g923
            byte[] revert = Report(0x00, 0xf8, 0x0a, 0x00, 0x00, 0x00, 0x00, 0x00);          // revert mode upon USB reset
            return real switch
            {
                0xC24F => new[] { revert, Report(0x00, 0xf8, 0x09, 0x05, 0x01, 0x01, 0x00, 0x00) }, // lg4ff_mode_switch_ext09_g29
                0xC29B => new[] { revert, Report(0x00, 0xf8, 0x09, 0x04, 0x01, 0x00, 0x00, 0x00) }, // lg4ff_mode_switch_ext09_g27
                0xC29A => new[] { revert, Report(0x00, 0xf8, 0x09, 0x03, 0x01, 0x00, 0x00, 0x00) }, // lg4ff_mode_switch_ext09_dfgt
                0xC299 => new[] { Report(0x00, 0xf8, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00) },         // lg4ff_mode_switch_ext16_g25
                0xC298 => new[] { Report(0x00, 0xf8, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00) },         // lg4ff_mode_switch_ext01_dfp
                _ => Array.Empty<byte[]>(),
            };
        }

        private static byte[] Report(byte id, params byte[] cmd7)
        {
            byte[] report = new byte[8];
            report[0] = id;
            Array.Copy(cmd7, 0, report, 1, 7);
            return report;
        }

        /// <summary>Tells a wheel that has just arrived in a mode other than
        /// its own to switch to its native mode (<see cref="NativeModeReports"/>).
        /// Returns true once every report went out. The wheel stays in the
        /// device list until its USB reset takes it away, as the kernel leaves
        /// its input device in place.</summary>
        public static bool SwitchToNativeMode(string devicePath, ushort vid, ushort pid, ushort bcdDevice)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            var reports = NativeModeReports(vid, pid, bcdDevice);
            if (reports.Length == 0) return false;
            foreach (var report in reports)
                if (!RawHidOutput.Write(devicePath, report)) return false;
            return true;
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
            byte type = ffbType == 8 ? (byte)EffSpring : ffbType == 11 && capFriction ? (byte)EffFriction : (byte)EffDamper;
            int op = OpFor(devicePath, key, slot, type);

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
            if (ok) _loaded[key] = type;
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
