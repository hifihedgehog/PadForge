using System;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Writes native Fanatec pedal rumble and wheel-base constant force via the
    /// Fanatec custom HID output protocol, bypassing SDL3 (Fanatec hardware does
    /// not expose rumble/FFB through the standard HID PID channel SDL3 speaks).
    ///
    /// <para>Wire protocol verified against <c>gotzl/hid-fanatecff</c>
    /// (<c>hid-ftec.c</c> rumble, <c>hid-ftecff.c</c> <c>ftecff_update_slot</c>)
    /// 2026-06-01. Bytes are unprotectable Fanatec hardware facts; original C#,
    /// no GPL source translation. Uses the shared <see cref="RawHidOutput"/>
    /// write path.</para>
    ///
    /// <para>HARDWARE VERIFICATION (real device): the wheel FFB output report ID
    /// is assumed 0 (the Linux driver leaves <c>report-&gt;id</c> unset);
    /// the pedal rumble report ID is 0x01 per the driver. Confirm both against
    /// the device's HID descriptor (<c>OutputReportByteLength</c> / report IDs).</para>
    /// </summary>
    internal static class FanatecRawHidWriter
    {
        public const ushort FanatecVid = 0x0EB7;

        /// <summary>ClubSport Pedals V3 (0x183B) + CSL Elite / Loadcell pedal
        /// PIDs. Pedals use the rumble protocol (two motors).</summary>
        public static bool IsFanatecPedal(ushort vid, ushort pid)
        {
            if (vid != FanatecVid) return false;
            switch (pid)
            {
                case 0x183B: // ClubSport Pedals V3
                case 0x6204: // CSL Elite Pedals
                case 0x6205: // CSL Pedals Loadcell
                case 0x6206: // CSL Pedals LC V2
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Fanatec wheel-base PIDs (FFB via the slot/constant-force
        /// protocol). VID 0x0EB7.</summary>
        public static bool IsFanatecWheel(ushort vid, ushort pid)
        {
            if (vid != FanatecVid) return false;
            switch (pid)
            {
                case 0x0E03: // CSL Elite Wheel Base
                case 0x0005: // CSL Elite Wheel Base PS4
                case 0x0020: // CSL DD / DD Pro / ClubSport DD
                case 0x0001: // ClubSport V2
                case 0x0004: // ClubSport V2.5
                case 0x0006: // Podium DD1
                case 0x0007: // Podium DD2
                case 0x0011: // CSR Elite
                case 0x0197: // Porsche 911 GT3 RS
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Bases with the FTEC_HIGHRES quirk (16-bit constant force) from
        /// the hid-fanatecff device table (hid-ftec.c:1135-1150). The other supported
        /// bases (ClubSport V2/V2.5, CSR Elite, Porsche 911) use 8-bit force.</summary>
        public static bool IsFanatecHighRes(ushort pid)
        {
            switch (pid)
            {
                case 0x0E03: // CSL Elite Wheel Base
                case 0x0005: // CSL Elite Wheel Base PS4
                case 0x0020: // CSL DD / DD Pro / ClubSport DD
                case 0x0006: // Podium DD1
                case 0x0007: // Podium DD2
                    return true;
                default:
                    return false;
            }
        }

        // TRANSLATE_FORCE(x, 8) = (clamp_s16(x) + 0x8000) >> 8 — same as Logitech;
        // 0x80 neutral, sign carries direction (single steering axis).
        private static byte TranslateForce(int levelS16)
        {
            if (levelS16 < -0x8000) levelS16 = -0x8000;
            else if (levelS16 > 0x7fff) levelS16 = 0x7fff;
            return (byte)((levelS16 + 0x8000) >> 8);
        }

        // ─────────────────────────────────────────────
        //  Pedals — rumble (hid-ftec.c ftec_set_rumble)
        // ─────────────────────────────────────────────

        /// <summary>Pedal rumble. throttle/brake each 0..255. Report ID 0x01,
        /// payload <c>F8 09 01 04 [throttle] [brake] 00</c>.</summary>
        // Poll thread is the sole caller (Step 2 ApplyForceFeedback), and
        // RawHidOutput.Write copies/pins under its per-path gate, so one
        // scratch avoids an 8-byte allocation at pedal-rumble rate.
        private static readonly byte[] s_pedalScratch =
            { 0x01, 0xF8, 0x09, 0x01, 0x04, 0x00, 0x00, 0x00 };

        public static bool WritePedalRumble(string devicePath, byte throttle, byte brake)
        {
            // Report ID 0x01 is byte[0] of the report (per the Fanatec descriptor),
            // not a prepended placeholder — the 8-byte buffer is the report.
            s_pedalScratch[5] = throttle;
            s_pedalScratch[6] = brake;
            return RawHidOutput.Write(devicePath, s_pedalScratch);
        }

        // ─────────────────────────────────────────────
        //  Wheel base — constant force (hid-ftecff.c ftecff_update_slot)
        // ─────────────────────────────────────────────

        // slot 0 constant-force command byte[1]. From ftecff: drv_data->slots[0].cmd.
        private const byte ConstantSlotCmd = 0x08;

        /// <summary>Wheel constant force on slot 0. <paramref name="levelS16"/> is
        /// the signed steering-axis force (-0x8000..0x7fff); 0 disables the slot.
        /// Command: <c>[slot&lt;&lt;4|1] 08 [TRANSLATE_FORCE] …</c>, disable
        /// flips byte[0] to <c>(slot&lt;&lt;4|3)</c>.</summary>
        public static bool WriteWheelConstantForce(string devicePath, int levelS16, ushort pid)
        {
            const int slot = 0;
            byte[] cmd = new byte[7];
            cmd[1] = ConstantSlotCmd;
            if (levelS16 == 0)
            {
                cmd[0] = (byte)((slot << 4) | 0x3); // disable slot
            }
            else
            {
                cmd[0] = (byte)((slot << 4) | 0x1); // select + enable
                if (IsFanatecHighRes(pid))
                {
                    // High-res bases take 16-bit force: TRANSLATE_FORCE(level,16) =
                    // clamp_s16(level) + 0x8000, low byte cmd[2] / high byte cmd[3],
                    // marker cmd[6] = 0x01 (hid-ftecff.c:723-727). 8-bit alone would
                    // quantize the force to 256 steps on these direct-drive bases.
                    int clamped = levelS16 < -0x8000 ? -0x8000 : (levelS16 > 0x7fff ? 0x7fff : levelS16);
                    int d1 = clamped + 0x8000;
                    cmd[2] = (byte)(d1 & 0xff);
                    cmd[3] = (byte)((d1 >> 8) & 0xff);
                    cmd[6] = 0x01;
                }
                else
                {
                    cmd[2] = TranslateForce(levelS16); // TRANSLATE_FORCE(level, 8)
                }
            }
            return RawHidOutput.Write(devicePath, BuildWheelReport(cmd));
        }

        // ── Wheel condition effects: spring (slot 1, cmd 0x0b) / damper +
        // friction + inertia (slot 2, cmd 0x0c) — from ftecff_update_slot.
        // Fanatec selects effect type by SLOT (slots[1].cmd=0x0b, [2].cmd=0x0c),
        // byte[0] = (slot<<4)|1 enable / |3 disable, byte[1] = slot cmd. Stateless.
        // Coefficient feel is hardware-tuned; wire format is verified.
        private const byte SpringSlot = 1, SpringCmd = 0x0b;
        // Damper/inertia/friction share command 0x0c but live in distinct slots
        // (hid-ftecff.c:1023-1036: slots[2]=damper, [3]=inertia, [4]=friction).
        private const byte DamperSlot = 2, InertiaSlot = 3, FrictionSlot = 4, DamperCmd = 0x0c;

        private static int ClampU16(int x) => x < 0 ? 0 : (x > 0xffff ? 0xffff : x);
        private static int ScaleU16(int x, int bits) => ClampU16(x) >> (16 - bits);
        private static int ScaleCoeff(int x, int bits) => ScaleU16(System.Math.Abs(x) * 2, bits);
        private static int ToHid(int dinput, int gainPct) =>
            (int)((long)dinput * gainPct / 100 * 0x7fff / 10000);

        /// <summary>Software centering spring for the Wheel-tab auto-center slider.
        /// Fanatec wheels expose no firmware autocenter command (hid-ftecff.c registers
        /// only upload/playback/destroy, and FF_AUTOCENTER is absent from the effects
        /// list), and calling ftec_set_range disables the stock centering spring
        /// (hid-ftecff.c:1216 "Set range so that centering spring gets disabled"; its
        /// leading f5 is the coarse-limit command). So centering is a slot-1 spring
        /// effect (cmd 0x0b, hid-ftecff.c:1024,1030) - the same plumbing game-driven
        /// springs use. Symmetric, centered at 0, no deadband. <paramref name="magnitude"/>
        /// is 0..0xffff (the Logitech/Thrustmaster autocenter scale); 0 disables the slot.</summary>
        public static bool WriteAutocenter(string devicePath, int magnitude)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            int coeff = (int)((long)ClampU16(magnitude) * 10000 / 0xffff); // 0..0xffff -> DInput 0..10000
            // coeff 0 -> WriteWheelCondition disables slot 1; coeff > 0 -> centering spring.
            return WriteWheelCondition(devicePath, 8 /* Spring */, coeff, coeff, 0, 0, 10000, 10000, 100);
        }

        /// <summary>Spring/damper/friction. coeffPos/coeffNeg and offset are DInput
        /// ±10000; deadband/satPos/satNeg 0..10000. <paramref name="ffbType"/> is a
        /// PadForge FfbEffectTypes value (8=Spring, 9=Damper, 10=Inertia, 11=Friction).</summary>
        public static bool WriteWheelCondition(string devicePath, uint ffbType,
            int coeffPos, int coeffNeg, int offset, int deadband, int satPos, int satNeg, int gainPct)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            bool isSpring = ffbType == 8;
            // Route each condition type to its own slot (cmd[0] high nibble = slot id);
            // inertia=3, friction=4 share the damper command 0x0c (hid-ftecff.c:1023-1036).
            int slot = ffbType == 8 ? SpringSlot : ffbType == 10 ? InertiaSlot : ffbType == 11 ? FrictionSlot : DamperSlot;
            byte slotCmd = isSpring ? SpringCmd : DamperCmd;

            // hid-ftecff: k1 = left_coeff = negative, k2 = right_coeff = positive,
            // clip = right_saturation = positive (calculate_spring/resistance +
            // hid-ftec.c:571-575). Caller passes (coeffPos, coeffNeg, ..., satPos, satNeg).
            int k1 = ToHid(coeffNeg, gainPct);
            int k2 = ToHid(coeffPos, gainPct);
            int clip = ScaleU16(ClampU16((int)((long)(satPos == 0 ? 10000 : satPos) * gainPct / 100 * 0xffff / 10000)), 8); // gain-scaled SCALE_VALUE_U16(right_sat,8)
            bool disable = coeffPos == 0 && coeffNeg == 0;

            byte[] cmd = new byte[7];
            cmd[0] = (byte)((slot << 4) | (disable ? 0x3 : 0x1));
            cmd[1] = slotCmd;
            if (disable && !isSpring)
                cmd[6] = 0xff; // ftecff resets damper/inertia/friction cmd[6]=0xff on disable (hid-ftecff.c:701-706)
            if (!disable)
            {
                if (isSpring)
                {
                    int center = ToHid(offset, 100), half = ToHid(deadband, 100);
                    int d1 = ScaleU16(((center - half) + 0x8000) & 0xffff, 11);
                    int d2 = ScaleU16(((center + half) + 0x8000) & 0xffff, 11);
                    cmd[2] = (byte)(d1 >> 3);
                    cmd[3] = (byte)(d2 >> 3);
                    cmd[4] = (byte)((ScaleCoeff(k2, 4) << 4) + ScaleCoeff(k1, 4));
                    cmd[6] = (byte)clip;
                }
                else // damper / inertia / friction
                {
                    cmd[2] = (byte)ScaleCoeff(k1, 4);
                    cmd[4] = (byte)ScaleCoeff(k2, 4);
                    cmd[6] = (byte)clip;
                }
            }
            return RawHidOutput.Write(devicePath, BuildWheelReport(cmd));
        }

        // Per-base max rotation from the hid-ftec.c device table (the driver's 'auto'
        // values 1090/2530 stand for the real 1080/2520 hardware maxima): direct-drive
        // bases 2520, belt ClubSport/CSR/Porsche 900, CSL Elite 1080 (and default).
        private static int MaxRange(ushort pid)
        {
            switch (pid)
            {
                case 0x0020: case 0x0006: case 0x0007: return 2520; // CSL DD / Podium DD1 / DD2
                case 0x0001: case 0x0004: case 0x0011: case 0x0197: return 900; // ClubSport V2/V2.5, CSR Elite, Porsche 911
                default: return 1080; // CSL Elite 0x0E03 / CSL Elite PS4 0x0005 / others
            }
        }

        /// <summary>Sets the wheel's hardware rotation range in degrees, clamped to the
        /// device max (ftec_set_range: f5 coarse-limit + f8 09 01 06 01 + f8 81 lo hi).</summary>
        public static bool WriteRange(string devicePath, int degrees, ushort pid)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            if (degrees < 90) degrees = 90; // min_range = 90 (hid-ftec.c:862)
            int max = MaxRange(pid);
            if (degrees > max) degrees = max;
            // ftec_set_range sends THREE reports in order (hid-ftecff.c:207-235): the
            // f5 coarse-limit (which also disables the firmware centering spring), the
            // f8 09 01 06 01 setup, then f8 81 lo hi. Sending only f8 81 left the
            // Fanatec firmware auto-center engaged (the same class as the G29 bug).
            byte[] coarse = { 0xf5, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            byte[] setup  = { 0xf8, 0x09, 0x01, 0x06, 0x01, 0x00, 0x00 };
            byte[] rng    = { 0xf8, 0x81, (byte)(degrees & 0xff), (byte)((degrees >> 8) & 0xff), 0x00, 0x00, 0x00 };
            bool ok = RawHidOutput.Write(devicePath, BuildWheelReport(coarse));
            ok &= RawHidOutput.Write(devicePath, BuildWheelReport(setup));
            ok &= RawHidOutput.Write(devicePath, BuildWheelReport(rng));
            return ok;
        }

        /// <summary>Sets the wheel RPM / rev LEDs. <paramref name="ledMask9"/> bit 0
        /// = first LED .. bit 8 = ninth (LEDS = 9). Replicates ftec_set_leds: the
        /// wheelbase strip takes the low 8 bits direct (<c>f8 13</c>), the rim strip
        /// takes a reshuffled 9-bit value where the first LED is the highest bit
        /// (<c>f8 09 08 [hi] [lo]</c>). Sends both so base- and rim-LED wheels light.
        ///
        /// <para>HARDWARE VERIFICATION (real device): the f8 13 / f8 09 path is
        /// verified against hid-fanatecff, but which Fanatec models carry which LED
        /// strip varies; confirm on hardware. Logitech is the bench-verified LED
        /// device.</para></summary>
        public static bool WriteRpmLeds(string devicePath, int ledMask9)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            const int leds = 9;

            // Wheelbase strip: low 8 bits direct.
            byte[] baseCmd = new byte[7];
            baseCmd[0] = 0xf8;
            baseCmd[1] = 0x13;
            baseCmd[2] = (byte)(ledMask9 & 0xff);
            bool ok = RawHidOutput.Write(devicePath, BuildWheelReport(baseCmd));

            // Rim strip: reshuffle so the first LED is the highest bit.
            int reshuffled = 0;
            for (int i = 0; i < leds; i++)
                if (((ledMask9 >> i) & 1) != 0) reshuffled |= 1 << (leds - i - 1);
            byte[] rim = new byte[7];
            rim[0] = 0xf8;
            rim[1] = 0x09;
            rim[2] = 0x08;
            rim[3] = (byte)((reshuffled >> 8) & 0xff);
            rim[4] = (byte)(reshuffled & 0xff);
            ok &= RawHidOutput.Write(devicePath, BuildWheelReport(rim));
            return ok;
        }

        // Wheel FFB report: report ID 0 (driver leaves id unset) + 7 command
        // bytes at offset 1. See HARDWARE VERIFICATION note re: report ID/length.
        private static byte[] BuildWheelReport(byte[] cmd7)
        {
            byte[] report = new byte[8];
            report[0] = 0x00; // report ID
            Array.Copy(cmd7, 0, report, 1, 7);
            return report;
        }
    }
}
