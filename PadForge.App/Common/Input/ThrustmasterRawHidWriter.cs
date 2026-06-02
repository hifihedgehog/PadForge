using System;
using System.Collections.Concurrent;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Writes native Thrustmaster T-series wheel constant force via the
    /// Thrustmaster custom HID output protocol, bypassing SDL3. Unlike Logitech
    /// (single 7-byte command) the Thrustmaster protocol is a stateful,
    /// variable-length multi-packet lifecycle: upload an effect into a slot,
    /// play it, then update its magnitude live, and stop it.
    ///
    /// <para>Wire protocol verified against <c>Kimplul/hid-tmff2</c>
    /// (<c>src/tmt300rs/hid-tmt300rs.c</c>) 2026-06-01: report ID 0x60; header
    /// = {effect.id + 1, code}; little-endian fields. Upload code 0x6a + level +
    /// envelope + timing; play code 0x89 + 0x41 + count; update code 0x6a +
    /// magnitude + envelope + 0x00 0x45 + duration/offset; stop code 0x89.
    /// Level = sin-projected steering force halved to the wheel's [-16383..]
    /// range. Bytes are unprotectable hardware facts; original C#, no GPL source
    /// translation; uses the shared <see cref="RawHidOutput"/> write path.</para>
    ///
    /// <para>HARDWARE VERIFICATION (real device): <see cref="ReportPayloadLen"/>
    /// is the T300RS output-report data length (driver buffer_length, ~63);
    /// confirm against the device's <c>OutputReportByteLength</c>. Report ID 0x60
    /// is from the descriptor.</para>
    /// </summary>
    internal static class ThrustmasterRawHidWriter
    {
        public const ushort ThrustmasterVid = 0x044F;

        public static bool IsThrustmasterWheel(ushort vid, ushort pid)
        {
            if (vid != ThrustmasterVid) return false;
            switch (pid)
            {
                case 0xB66E: // T300RS (PS3 normal)
                case 0xB66F: // T300RS (PS3 advanced)
                case 0xB66D: // T300RS (PS4 normal)
                case 0xB696: // T248 (PC)
                case 0xB669: // TX (active)
                case 0xB692: // TS-XW (active)
                case 0xB689: // TS-PC Racer
                    return true;
                default:
                    return false;
            }
        }

        private const byte ReportId = 0x60;
        private const int  ReportPayloadLen = 63;   // T300RS FFB report data length
        private const byte HeaderId = 0x01;          // effect.id 0 -> header id (+1)

        // Per-device armed state: has the constant-force effect been uploaded +
        // started on this device. Updates only modify magnitude once armed.
        private static readonly ConcurrentDictionary<string, bool> _armed = new();

        /// <summary>Constant force from the shared signed steering level
        /// (-0x8000..0x7fff). Halved to the Thrustmaster range. 0 stops the
        /// effect. Uploads + plays on first arm, then updates magnitude.</summary>
        public static bool WriteConstantForce(string devicePath, int steeringLevelS16)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            short level = (short)(Math.Clamp(steeringLevelS16, -0x7ffe, 0x7ffe) / 2);
            if (level == 0) return WriteStop(devicePath);

            bool armed = _armed.TryGetValue(devicePath, out bool v) && v;
            if (!armed)
            {
                // Upload (sets initial level) + play.
                if (!Send(devicePath, BuildUpload(level))) return false;
                if (!Send(devicePath, BuildPlay())) return false;
                _armed[devicePath] = true;
                return true;
            }
            // Already armed — modify the live magnitude.
            return Send(devicePath, BuildUpdate(level));
        }

        /// <summary>Stops the constant-force effect and disarms.</summary>
        public static bool WriteStop(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            _armed[devicePath] = false;
            return Send(devicePath, BuildStop());
        }

        // ── packet builders (payload only; Send prepends report ID + pads) ──

        // Upload: header{id,0x6a} + level(LE16) + envelope(8=0) + zero(1) +
        // timing{0x4f, duration(LE16)=0xffff infinite, offset(LE16)=0, end 0xffff}.
        private static byte[] BuildUpload(short level)
        {
            return new byte[]
            {
                HeaderId, 0x6a,
                (byte)(level & 0xff), (byte)((level >> 8) & 0xff),
                0, 0, 0, 0, 0, 0, 0, 0,        // envelope (attack/fade len+level), none
                0x00,                          // zero separator
                0x4f,                          // timing start marker
                0xff, 0xff,                    // duration = 0xffff (infinite)
                0x00, 0x00,                    // offset = 0
                0xff, 0xff,                    // timing end marker
            };
        }

        // Play: header{id,0x89} + 0x41 + count(LE16)=0 (infinite).
        private static byte[] BuildPlay()
        {
            return new byte[] { HeaderId, 0x89, 0x41, 0x00, 0x00 };
        }

        // Update: header{id,0x6a} + magnitude(LE16) + envelope(8=0) +
        // effect_type(0x00 constant) + update_type(0x45) + duration(LE16)=0xffff
        // + offset(LE16)=0.
        private static byte[] BuildUpdate(short level)
        {
            return new byte[]
            {
                HeaderId, 0x6a,
                (byte)(level & 0xff), (byte)((level >> 8) & 0xff),
                0, 0, 0, 0, 0, 0, 0, 0,        // envelope, none
                0x00,                          // effect_type = constant
                0x45,                          // update_type
                0xff, 0xff,                    // duration = 0xffff
                0x00, 0x00,                    // offset = 0
            };
        }

        // Stop: header{id,0x89} + value(0).
        private static byte[] BuildStop()
        {
            return new byte[] { HeaderId, 0x89, 0x00 };
        }

        // Report: [0x60][payload][zero-pad to ReportPayloadLen].
        private static bool Send(string devicePath, byte[] payload)
        {
            byte[] report = new byte[1 + ReportPayloadLen];
            report[0] = ReportId;
            int n = Math.Min(payload.Length, ReportPayloadLen);
            Array.Copy(payload, 0, report, 1, n);
            return RawHidOutput.Write(devicePath, report);
        }
    }
}
