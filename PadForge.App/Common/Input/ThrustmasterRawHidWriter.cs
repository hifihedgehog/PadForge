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
    /// <para>Each report is report ID 0x60 + payload; <see cref="RawHidOutput"/>
    /// pads it to the device's <c>OutputReportByteLength</c> (64 for the PS3/PC
    /// T300RS, 32 for the PS4 variant 0xB66D). Report ID 0x60 is from the descriptor.</para>
    /// </summary>
    internal static class ThrustmasterRawHidWriter
    {
        public const ushort ThrustmasterVid = 0x044F;

        // The complete set of T300RS-protocol (hid-tmff2) wheels, in their ACTIVE
        // (post-init) PIDs. Thrustmaster wheels two-stage enumerate: they appear as a
        // boot/init PID, then the Thrustmaster Windows driver switches them to the
        // active PID before PadForge writes any FFB, so only the active PIDs belong
        // here. Verified 1:1 against hid-tmff2.h and oversteer (2026-06-06).
        //   - T248 active = 0xB696 (TMT248_PC_ID, hid-tmff2.h:112). 0xB69C is the T248
        //     BOOT PID (hid-tminit), not an FFB PID; 0xB69D is unattested in any source.
        //   - T-GT / T-GT II have no distinct PID (the T-GT II reuses the T300, so it is
        //     already covered by 0xB66E). 0xB677 is the T150 (separate hid-t150 driver,
        //     different protocol) and must NOT be added here; 0xB66B is unattested.
        public static bool IsThrustmasterWheel(ushort vid, ushort pid)
        {
            if (vid != ThrustmasterVid) return false;
            switch (pid)
            {
                case 0xB66E: // T300RS (PS3 normal) — also the T-GT II
                case 0xB66F: // T300RS (PS3 advanced / Ferrari F1)
                case 0xB66D: // T300RS (PS4 normal / GT Edition)
                case 0xB696: // T248 (active)
                case 0xB669: // TX (active)
                case 0xB692: // TS-XW (active)
                case 0xB689: // TS-PC Racer
                    return true;
                default:
                    return false;
            }
        }

        private const byte ReportId = 0x60;
        private const byte HeaderId = 0x01;          // effect.id 0 -> header id (+1)

        // Per-device armed state: which effect kind is currently uploaded + playing
        // in the wheel's single effect slot (id 0). The kind matters because the wheel
        // slot holds one effect at a time; switching kind (constant <-> condition <->
        // periodic) must re-upload, not send a same-kind update to a slot of a
        // different shape. Within a kind, updates only modify the live parameters.
        private const int KindNone = 0, KindConstant = 1, KindCondition = 2, KindPeriodic = 3;
        private static readonly ConcurrentDictionary<string, int> _armedKind = new();

        /// <summary>Clears the upload/play armed state for a device. Called on
        /// unplug/reassign so the next force re-uploads + re-plays instead of sending an
        /// update to a slot the power-cycled firmware no longer holds.</summary>
        public static void ResetDevice(string devicePath)
        {
            if (!string.IsNullOrEmpty(devicePath)) _armedKind.TryRemove(devicePath, out _);
        }

        /// <summary>Constant force from the shared signed steering level
        /// (-0x8000..0x7fff). Halved to the Thrustmaster range. 0 stops the
        /// effect. Uploads + plays on first arm, then updates magnitude.</summary>
        public static bool WriteConstantForce(string devicePath, int steeringLevelS16)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            // Halved to the wheel's [-16383..] force range, matching the reference
            // (t300rs_calculate_constant_level: "the Windows driver uses the range
            // [-16385;16381]", hid-tmt300rs.c:347-356). 0 stops the effect.
            short level = (short)(Math.Clamp(steeringLevelS16, -0x7ffe, 0x7ffe) / 2);
            if (level == 0) return WriteStop(devicePath);

            int kind = _armedKind.TryGetValue(devicePath, out int v) ? v : KindNone;
            if (kind != KindConstant)
            {
                // Slot is empty or holds a different effect kind — upload + play.
                if (!Send(devicePath, BuildUpload(level))) return false;
                if (!Send(devicePath, BuildPlay())) return false;
                _armedKind[devicePath] = KindConstant;
                return true;
            }
            // Already a constant effect — modify the live magnitude.
            return Send(devicePath, BuildUpdate(level));
        }

        /// <summary>Stops the active effect and disarms.</summary>
        public static bool WriteStop(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            _armedKind[devicePath] = KindNone;
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
        /// scaled by 0x3c, <c>08 11 lo hi</c>). Clamped per model.</summary>
        public static bool WriteRange(string devicePath, int degrees, ushort pid)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            // T300RS 40..1080; T248/TX 140..900; TS-XW/TS-PC 140..1080 (hid-tmff2 per-model set_range).
            int min = (pid == 0xB66E || pid == 0xB66F || pid == 0xB66D) ? 40 : 140;
            int max = (pid == 0xB696 || pid == 0xB669) ? 900 : 1080;
            if (degrees < min) degrees = min; else if (degrees > max) degrees = max;
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
            // halfHid = deadband_u16/2 in HID units. ToHid maps DInput 0..10000 to
            // 0..0x7fff, which ALREADY equals the driver's deadband_u16/2 (the Linux
            // ff deadband is 0..0xffff, so /2 lands at 0x7fff full scale). No extra
            // /2 - that halved the dead zone (t300rs_calculate_deadband, hid-tmt300rs.c:430-437).
            int centerHid = ToHid(offset, 100), halfHid = ToHid(deadband, 100);
            int rBand = Clamp16(centerHid + halfHid);
            int lBand = Clamp16(centerHid - halfHid);
            int rSat = satPos == 0 ? maxSat : (int)((long)satPos * 0xffff / 10000) * maxSat / 0xffff;
            int lSat = satNeg == 0 ? maxSat : (int)((long)satNeg * 0xffff / 10000) * maxSat / 0xffff;

            byte[] payload = BuildConditionUpload((short)rCoeff, (short)lCoeff, (short)rBand, (short)lBand,
                (ushort)rSat, (ushort)lSat, (ushort)maxSat, type);

            int kind = _armedKind.TryGetValue(devicePath, out int v) ? v : KindNone;
            if (!Send(devicePath, payload)) return false;            // upload (0x64) sets all params
            if (kind != KindCondition)
            {
                if (!Send(devicePath, BuildPlay())) return false;
                _armedKind[devicePath] = KindCondition;
            }
            return true;
        }

        // PadForge FfbEffectTypes -> Thrustmaster firmware waveform code (Linux FF
        // constant - 0x57: FF_SQUARE 0x58->1, FF_TRIANGLE 0x59->2, FF_SINE 0x5a->3,
        // FF_SAW_UP 0x5b->4, FF_SAW_DOWN 0x5c->5). 0 = not a periodic effect.
        private static int WaveformCode(uint ffbType) => ffbType switch
        {
            3 => 1, // Square
            5 => 2, // Triangle
            4 => 3, // Sine
            6 => 4, // SawUp
            7 => 5, // SawDown
            _ => 0,
        };

        // 180-degree phase in the wheel's 0..32677 phase space (Linux phase 0x8000
        // mapped via *32677/0x10000), used to invert a negative projected magnitude
        // since the wheel takes positive magnitudes only (t300rs_calculate_periodic_values).
        private const ushort PeriodicPhase180 = 16338;

        /// <summary>Native firmware periodic effect (square/sine/triangle/sawtooth). The
        /// T300 runs the waveform onboard from {waveform, magnitude, period}, so this is
        /// higher fidelity than host-sampled constant force (which is what Logitech and
        /// Fanatec must use — they have no firmware periodic generator). Mirrors
        /// t300rs_upload_periodic (code 0x6b) + play. <paramref name="steeringPeakS16"/>
        /// is the projected steering amplitude (-0x7fff..0x7fff), NOT halved — unlike
        /// constant force, t300rs_calculate_periodic_values applies no /2.
        /// <paramref name="periodMs"/> is the waveform period in ms.</summary>
        public static bool WritePeriodic(string devicePath, uint ffbType, int steeringPeakS16, int periodMs)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            int waveform = WaveformCode(ffbType);
            if (waveform == 0 || periodMs <= 0) return false;   // not a periodic effect

            int signed = Math.Clamp(steeringPeakS16, -0x7fff, 0x7fff);
            ushort phase = 0;
            if (signed < 0) { signed = -signed; phase = PeriodicPhase180; }
            if (signed == 0) return WriteStop(devicePath);
            ushort mag = (ushort)signed;
            ushort period = (ushort)Math.Clamp(periodMs, 1, 0xffff);

            int kind = _armedKind.TryGetValue(devicePath, out int v) ? v : KindNone;
            if (kind != KindPeriodic)
            {
                // First arm (or switching from another kind): upload + play. Re-uploading
                // every frame would restart the waveform phase, so subsequent frames
                // UPDATE instead (the wheel keeps advancing the onboard waveform).
                if (!Send(devicePath, BuildPeriodicUpload(mag, phase, period, (byte)waveform))) return false;
                if (!Send(devicePath, BuildPlay())) return false;
                _armedKind[devicePath] = KindPeriodic;
                return true;
            }
            return Send(devicePath, BuildPeriodicUpdate(mag, phase, period, (byte)waveform));
        }

        // Periodic upload (t300rs_packet_periodic, code 0x6b): header(3) + magnitude(2) +
        // periodic_offset(2)=0 + phase(2) + period(2) + marker(2)=0x8000 + envelope(8)=0 +
        // waveform(1) + timing(10) = 32 bytes, all little-endian.
        private static byte[] BuildPeriodicUpload(ushort magnitude, ushort phase, ushort period, byte waveform)
        {
            var p = new byte[32];
            int i = 0;
            p[i++] = 0x00; p[i++] = HeaderId; p[i++] = 0x6b;   // header: zero1, id (effect.id+1), code
            void W16(int x) { p[i++] = (byte)(x & 0xff); p[i++] = (byte)((x >> 8) & 0xff); }
            W16(magnitude);
            W16(0);             // periodic_offset (DC offset) = 0
            W16(phase);
            W16(period);
            W16(0x8000);        // marker
            i += 8;             // envelope (attack_length/level, fade_length/level) = 0
            p[i++] = waveform;  // FF_* - 0x57
            // timing (10): start 0x4f, duration=0xffff (infinite), zero1[2], offset=0, zero2, end 0xffff
            p[i++] = 0x4f; W16(0xffff); p[i++] = 0x00; p[i++] = 0x00; W16(0x0000); p[i++] = 0x00; W16(0xffff);
            return p;
        }

        // Periodic update (t300rs_packet_mod_periodic, code 0x6e): header(3) + type(1)=0x0f +
        // magnitude(2) + offset(2)=0 + phase(2) + period(2) + envelope(8)=0 + effect_type(1) +
        // update_type(1)=0x45 + duration(2)=0xffff + play_offset(2)=0 = 26 bytes. Modifies the
        // live waveform without restarting its phase.
        private static byte[] BuildPeriodicUpdate(ushort magnitude, ushort phase, ushort period, byte waveform)
        {
            var p = new byte[26];
            int i = 0;
            p[i++] = 0x00; p[i++] = HeaderId; p[i++] = 0x6e;   // header
            p[i++] = 0x0f;                                     // type
            void W16(int x) { p[i++] = (byte)(x & 0xff); p[i++] = (byte)((x >> 8) & 0xff); }
            W16(magnitude);
            W16(0);             // offset (DC) = 0
            W16(phase);
            W16(period);
            i += 8;             // envelope = 0
            p[i++] = waveform;  // effect_type (FF_* - 0x57)
            p[i++] = 0x45;      // update_type
            W16(0xffff);        // duration = infinite
            W16(0);             // play_offset = 0
            return p;
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

        // Report: [0x60][payload]. RawHidOutput pads up to the device's actual
        // OutputReportByteLength (64 for the PS3/PC T300RS, 32 for the PS4 variant
        // 0xB66D per hid-tmff2 T300RS_PS4_BUFFER_LENGTH), so the report is
        // device-correct without a hardcoded length that breaks the 31-byte PS4 wheel.
        private static bool Send(string devicePath, byte[] payload)
        {
            byte[] report = new byte[1 + payload.Length];
            report[0] = ReportId;
            Array.Copy(payload, 0, report, 1, payload.Length);
            return RawHidOutput.Write(devicePath, report);
        }
    }
}
