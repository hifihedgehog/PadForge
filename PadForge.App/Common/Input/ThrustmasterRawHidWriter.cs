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
    /// = {0x00 zero1, effect.id + 1, code} (t300rs_packet_header is 3 bytes, zero1
    /// is a leading data byte); little-endian fields. Upload code 0x6a + level +
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
                0x00, HeaderId, 0x6a,          // header: zero1, id (effect.id+1), code
                (byte)(level & 0xff), (byte)((level >> 8) & 0xff),
                0, 0, 0, 0, 0, 0, 0, 0,        // envelope (attack/fade len+level), none
                0x00,                          // zero separator
                0x4f,                          // timing start marker (t300rs_packet_timing, 10 bytes)
                0xff, 0xff,                    // duration = 0xffff (infinite)
                0x00, 0x00,                    // zero1[2] gap
                0x00, 0x00,                    // offset = 0
                0x00,                          // zero2 gap
                0xff, 0xff,                    // end marker
            };
        }

        // Play: header{id,0x89} + 0x41 + count(LE16)=0 (infinite).
        private static byte[] BuildPlay()
        {
            return new byte[] { 0x00, HeaderId, 0x89, 0x41, 0x00, 0x00 };
        }

        // Update: header{id,0x6a} + magnitude(LE16) + envelope(8=0) +
        // effect_type(0x00 constant) + update_type(0x45) + duration(LE16)=0xffff
        // + offset(LE16)=0.
        private static byte[] BuildUpdate(short level)
        {
            return new byte[]
            {
                0x00, HeaderId, 0x6a,          // header: zero1, id, code
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
            return new byte[] { 0x00, HeaderId, 0x89, 0x00 };
        }

        /// <summary>Sets the wheel's rotation range in degrees (t300rs_set_range:
        /// scaled by 0x3c, <c>08 11 lo hi</c>, 40..1080).</summary>
        public static bool WriteRange(string devicePath, int degrees)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            if (degrees < 40) degrees = 40; else if (degrees > 1080) degrees = 1080;
            int scaled = degrees * 0x3c;
            return Send(devicePath, new byte[] { 0x08, 0x11, (byte)(scaled & 0xff), (byte)((scaled >> 8) & 0xff) });
        }

        /// <summary>Sets autocenter strength (0..0xffff; 0 = off). Enable packet
        /// (<c>08 04 01 00</c>) then strength (<c>08 03 lo hi</c>), per
        /// t300rs_set_autocenter.</summary>
        public static bool WriteAutocenter(string devicePath, int strength)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            if (strength < 0) strength = 0; else if (strength > 0xffff) strength = 0xffff;
            if (!Send(devicePath, new byte[] { 0x08, 0x04, 0x01, 0x00 })) return false;
            return Send(devicePath, new byte[] { 0x08, 0x03, (byte)(strength & 0xff), (byte)((strength >> 8) & 0xff) });
        }

        /// <summary>Sets the rim's RPM / rev LEDs. <paramref name="ledMask15"/> is a
        /// 15-bit mask, bit 0 = first LED. The LED strip lives on the rim, not the
        /// base, so the command goes to the base and is relayed to whatever rim is
        /// attached (harmless on a rim without LEDs). Payload after the 0x60 report
        /// ID = <c>00 41 02 [low] [high]</c> (LEDs 0-7 low, 8-14 high).
        ///
        /// <para>Protocol verified against wKoja/thrustmaster-led-linux,
        /// prodigal.knight's SimHub Thrustmaster LED plugin, and mplutka/tm-bt-led
        /// (three independent reverse-engineered sources, 2026-06-02). Rims with rev
        /// LEDs: Ferrari 488 Challenge, SF1000 Formula, T248. Brightness defaults to
        /// the wheel's own setting; not driven here. HARDWARE-VERIFY on a real rim.</para></summary>
        public static bool WriteRpmLeds(string devicePath, int ledMask15)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            int mask = ledMask15 & 0x7fff;
            byte[] payload = { 0x00, 0x41, 0x02, (byte)(mask & 0xff), (byte)((mask >> 8) & 0xff) };
            return Send(devicePath, payload);
        }

        // ── Condition effects: spring / damper / friction ──
        // Upload packet (header 0x64) from t300rs_upload_condition. Spring uses
        // type 0x06 + sat-max 0x6aa6; damper/friction/inertia type 0x07 + 0x7ffc.
        // Coefficients/deadband/saturation in DInput units (coeff/offset ±10000,
        // deadband/sat 0..10000), converted to the wheel's HID range. Feel is
        // hardware-tuned; wire format verified from hid-tmff2.
        private static readonly byte[] ConditionValues = { 0xfe, 0xff, 0xfe, 0xff, 0xfe, 0xff, 0xfe, 0xff };

        private static int ToHid(int dinput, int gainPct) =>
            (int)((long)dinput * gainPct / 100 * 0x7fff / 10000);
        private static int Clamp16(int x) => x < -0x7fff ? -0x7fff : (x > 0x7fff ? 0x7fff : x);

        /// <summary>Spring/damper/friction. coeffPos/coeffNeg and offset are DInput
        /// ±10000; deadband/satPos/satNeg 0..10000. <paramref name="ffbType"/> is a
        /// PadForge FfbEffectTypes value (8=Spring else damper/friction/inertia).</summary>
        public static bool WriteCondition(string devicePath, uint ffbType,
            int coeffPos, int coeffNeg, int offset, int deadband, int satPos, int satNeg, int gainPct)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            if (coeffPos == 0 && coeffNeg == 0) return WriteStop(devicePath);

            bool isSpring = ffbType == 8;
            int maxSat = isSpring ? 0x6aa6 : 0x7ffc;
            byte type = (byte)(isSpring ? 0x06 : 0x07);

            int rCoeff = ToHid(coeffPos, gainPct);
            int lCoeff = ToHid(coeffNeg, gainPct);
            int centerHid = ToHid(offset, 100), halfHid = ToHid(deadband, 100) / 2;
            int rBand = Clamp16(centerHid + halfHid);
            int lBand = Clamp16(centerHid - halfHid);
            int rSat = satPos == 0 ? maxSat : (int)((long)satPos * 0xffff / 10000) * maxSat / 0xffff;
            int lSat = satNeg == 0 ? maxSat : (int)((long)satNeg * 0xffff / 10000) * maxSat / 0xffff;

            byte[] payload = BuildConditionUpload((short)rCoeff, (short)lCoeff, (short)rBand, (short)lBand,
                (ushort)rSat, (ushort)lSat, (ushort)maxSat, type);

            bool armed = _armed.TryGetValue(devicePath, out bool v) && v;
            if (!Send(devicePath, payload)) return false;
            if (!armed)
            {
                if (!Send(devicePath, BuildPlay())) return false;
                _armed[devicePath] = true;
            }
            return true;
        }

        private static byte[] BuildConditionUpload(short rCoeff, short lCoeff, short rBand, short lBand,
            ushort rSat, ushort lSat, ushort maxSat, byte type)
        {
            // header(3) + 6×i16(12) + hardcoded(8) + 2×u16(4) + type(1) + timing(10) = 38
            var p = new byte[38];
            int i = 0;
            p[i++] = 0x00; p[i++] = HeaderId; p[i++] = 0x64; // header: zero1, id, code
            void W16(int x) { p[i++] = (byte)(x & 0xff); p[i++] = (byte)((x >> 8) & 0xff); }
            W16(rCoeff); W16(lCoeff); W16(rBand); W16(lBand); W16(rSat); W16(lSat);
            Array.Copy(ConditionValues, 0, p, i, ConditionValues.Length); i += ConditionValues.Length;
            W16(maxSat); W16(maxSat);
            p[i++] = type;
            // timing (10): start 0x4f, duration=0xffff, zero1[2], offset=0, zero2, end 0xffff
            p[i++] = 0x4f; W16(0xffff); p[i++] = 0x00; p[i++] = 0x00; W16(0x0000); p[i++] = 0x00; W16(0xffff);
            return p;
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
