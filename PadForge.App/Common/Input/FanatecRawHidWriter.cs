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
        public static bool WritePedalRumble(string devicePath, byte throttle, byte brake)
        {
            // Report ID 0x01 is byte[0] of the report (per the Fanatec descriptor),
            // not a prepended placeholder — the 8-byte buffer is the report.
            byte[] report = { 0x01, 0xF8, 0x09, 0x01, 0x04, throttle, brake, 0x00 };
            return RawHidOutput.Write(devicePath, report);
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
        public static bool WriteWheelConstantForce(string devicePath, int levelS16)
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
                cmd[2] = TranslateForce(levelS16);
            }
            return RawHidOutput.Write(devicePath, BuildWheelReport(cmd));
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
