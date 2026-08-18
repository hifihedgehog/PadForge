using System;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    // Locks the PS Move lane's pure protocol contracts (#277), each against
    // its cloned reference: the button-word assembly and bit meanings
    // (psmoveapi psmove.c psmove_get_buttons + psmove.h Btn_*), the two
    // models' sensor-word decodes (psmove_decode_16bit vs the two's-complement
    // variant), the ZCM1 magnetometer's 12-bit packing
    // (psmove_get_magnetometer), the interrupt-channel output frame hid-sony
    // sends (motion_send_output_report, report 0x02, 49 bytes), and the USB
    // calibration blob's linear mapping (psmove_calibration.c).
    [Collection("SettingsManagerStatics")]
    public class PsMoveDecodeTests
    {
        [Fact]
        public void Buttons_AssembleAcrossTheFourBytes()
        {
            // byte2 -> bits 0-7, byte1 -> bits 8-15, byte3 bit0 -> bit 16,
            // byte4 bits 6-7 -> bits 19-20 (psmove.c's bit diagram).
            Assert.Equal(0u, PsMoveDirectService.DecodeButtons(0, 0, 0, 0));
            Assert.Equal(PsMoveDirectService.BtnTriangle, PsMoveDirectService.DecodeButtons(0, 0x10, 0, 0));
            Assert.Equal(PsMoveDirectService.BtnCircle,   PsMoveDirectService.DecodeButtons(0, 0x20, 0, 0));
            Assert.Equal(PsMoveDirectService.BtnCross,    PsMoveDirectService.DecodeButtons(0, 0x40, 0, 0));
            Assert.Equal(PsMoveDirectService.BtnSquare,   PsMoveDirectService.DecodeButtons(0, 0x80, 0, 0));
            Assert.Equal(PsMoveDirectService.BtnSelect,   PsMoveDirectService.DecodeButtons(0x01, 0, 0, 0));
            Assert.Equal(PsMoveDirectService.BtnStart,    PsMoveDirectService.DecodeButtons(0x08, 0, 0, 0));
            Assert.Equal(PsMoveDirectService.BtnPs,       PsMoveDirectService.DecodeButtons(0, 0, 0x01, 0));
            Assert.Equal(PsMoveDirectService.BtnMove,     PsMoveDirectService.DecodeButtons(0, 0, 0, 0x40));
            Assert.Equal(PsMoveDirectService.BtnT,        PsMoveDirectService.DecodeButtons(0, 0, 0, 0x80));
        }

        [Fact]
        public void Buttons_SequenceNibbleNeverLeaksIntoTheWord()
        {
            // buttons4's low nibble is the frame sequence counter
            // (psmove.c:1466); only bits 6-7 carry buttons.
            Assert.Equal(0u, PsMoveDirectService.DecodeButtons(0, 0, 0, 0x0F));
            Assert.Equal(0u, PsMoveDirectService.DecodeButtons(0, 0, 0x02, 0x30));
        }

        [Fact]
        public void SensorWords_DecodePerModel()
        {
            // ZCM1: little-endian minus 0x8000 (psmove_decode_16bit).
            Assert.Equal(0, PsMoveDirectService.DecodeZcm1(0x00, 0x80));
            Assert.Equal(1, PsMoveDirectService.DecodeZcm1(0x01, 0x80));
            Assert.Equal(-0x8000, PsMoveDirectService.DecodeZcm1(0x00, 0x00));
            Assert.Equal(0x7FFF, PsMoveDirectService.DecodeZcm1(0xFF, 0xFF));

            // ZCM2: little-endian two's complement.
            Assert.Equal(0, PsMoveDirectService.DecodeZcm2(0x00, 0x00));
            Assert.Equal(-1, PsMoveDirectService.DecodeZcm2(0xFF, 0xFF));
            Assert.Equal(0x1234, PsMoveDirectService.DecodeZcm2(0x34, 0x12));
            Assert.Equal(short.MinValue, PsMoveDirectService.DecodeZcm2(0x00, 0x80));
        }

        [Fact]
        public void Magnetometer_UnpacksTwelveBitSigned()
        {
            // X = (templow_mXhigh & 0x0F) << 8 | mXlow; Y = mYhigh << 4 |
            // mYlow_mZhigh >> 4; Z = (mYlow_mZhigh & 0x0F) << 8 | mZlow;
            // each TWELVE_BIT_SIGNED (psmove.c:135, 2034-2042).
            var (x, y, z) = PsMoveDirectService.DecodeMagnetometer(0x01, 0x23, 0x45, 0xA7, 0x89);
            Assert.Equal(0x123, x);            // (0x01 & 0x0F) << 8 | 0x23
            Assert.Equal(0x45A, y);            // 0x45 << 4 | high nibble of 0xA7
            Assert.Equal(0x789, z);            // (0xA7 & 0x0F) << 8 | 0x89, bit 11 clear -> positive

            // Bit 11 set -> negative via TWELVE_BIT_SIGNED.
            var negZ = PsMoveDirectService.DecodeMagnetometer(0x00, 0x00, 0x00, 0x0F, 0x89);
            Assert.Equal(0xF89 - 0x1000, negZ.Z);

            var neg = PsMoveDirectService.DecodeMagnetometer(0x0F, 0xFF, 0xFF, 0xFF, 0xFF);
            Assert.Equal(-1, neg.X);
            Assert.Equal(-1, neg.Y);
            Assert.Equal(-1, neg.Z);
        }

        [Fact]
        public void OutputFrame_MatchesHidSonyReport02()
        {
            // 0xA2 DATA|Output header + the 49-byte report hid-sony sends:
            // [type=0x02, zero, r, g, b, zero2, rumble] zero-padded
            // (motion_output_report_02 + MOTION_REPORT_0x02_SIZE).
            byte[] o = PsMoveDirectService.BuildOutputFrame(0x11, 0x22, 0x33, 0x44);
            Assert.Equal(50, o.Length);
            Assert.Equal(0xA2, o[0]);
            Assert.Equal(0x02, o[1]);
            Assert.Equal(0x00, o[2]);
            Assert.Equal(0x11, o[3]);
            Assert.Equal(0x22, o[4]);
            Assert.Equal(0x33, o[5]);
            Assert.Equal(0x00, o[6]);
            Assert.Equal(0x44, o[7]);
            for (int i = 8; i < o.Length; i++) Assert.Equal(0x00, o[i]);
        }

        [Fact]
        public void ReportSizes_MatchTheModels()
        {
            // 0xA1 + 49 (ZCM1) / 0xA1 + 44 (ZCM2), per the psmove input
            // structs and hid-sony's size==49 BT gate.
            Assert.Equal(50, PsMoveDirectService.Zcm1BtReportSize);
            Assert.Equal(45, PsMoveDirectService.Zcm2BtReportSize);
        }

        [Fact]
        public void MacFromPath_SkipsGuidSegmentsAndFindsTheSerial()
        {
            // The interface GUID's last segment is also 12 hex digits; only
            // the serial outside braces may match.
            string path = @"\\?\bthps3#{2ffad411-8a38-4a36-957a-c2e2d769be62}&dev&vid_054c&pid_03d5#9&2a1b3c4d&0&d0bcc1f57961#{bcec605d-233c-4bef-9a10-f2b81b5297f6}";
            Assert.Equal("d0bcc1f57961", PsMoveDirectService.ExtractMacFromPath(path));
            Assert.Null(PsMoveDirectService.ExtractMacFromPath(null));
            Assert.Null(PsMoveDirectService.ExtractMacFromPath(@"\\?\hid#vid_054c&pid_03d5#7&col01"));
        }

        [Fact]
        public void UsbReport_NormalizesIntoTheSharedBtFrame()
        {
            // USB reads deliver [0x01, payload...] (hidapi/HidD framing); the
            // normalizer prepends the 0xA1 the shared parser expects.
            var raw = new byte[49];
            raw[0] = 0x01;
            raw[1] = 0x5A;   // buttons1
            raw[48] = 0x77;  // last ZCM1 byte
            var frame = new byte[PsMoveDirectService.Zcm1BtReportSize];
            Assert.True(PsMoveDirectService.NormalizeUsbReport(raw, 49, frame, zcm2: false));
            Assert.Equal(0xA1, frame[0]);
            Assert.Equal(0x01, frame[1]);
            Assert.Equal(0x5A, frame[2]);
            Assert.Equal(0x77, frame[49]);

            // Short reads and foreign report ids are refused.
            Assert.False(PsMoveDirectService.NormalizeUsbReport(raw, 40, frame, zcm2: false));
            raw[0] = 0x02;
            Assert.False(PsMoveDirectService.NormalizeUsbReport(raw, 49, frame, zcm2: false));

            // ZCM2 needs only its 44-byte report.
            var raw2 = new byte[44];
            raw2[0] = 0x01;
            var frame2 = new byte[PsMoveDirectService.Zcm2BtReportSize];
            Assert.True(PsMoveDirectService.NormalizeUsbReport(raw2, 44, frame2, zcm2: true));
            Assert.Equal(0xA1, frame2[0]);
        }

        [Fact]
        public void UsbOutputReport_IsThePsmoveLedsWrite()
        {
            // Report 0x06: [id, zero, r, g, b, rumble2, rumble, pad...]
            // (PSMove_Data_LEDs, psmove.c:123-132), padded to the collection's
            // OutputReportByteLength.
            byte[] o = PsMoveDirectService.BuildUsbOutputReport(0x11, 0x22, 0x33, 0x44, outLen: 9);
            Assert.Equal(9, o.Length);
            Assert.Equal(0x06, o[0]);
            Assert.Equal(0x00, o[1]);
            Assert.Equal(0x11, o[2]);
            Assert.Equal(0x22, o[3]);
            Assert.Equal(0x33, o[4]);
            Assert.Equal(0x00, o[5]);
            Assert.Equal(0x44, o[6]);

            // Caps-driven padding, and a floor at the report's own size when
            // the caps read failed.
            Assert.Equal(49, PsMoveDirectService.BuildUsbOutputReport(1, 2, 3, 4, 49).Length);
            Assert.Equal(9, PsMoveDirectService.BuildUsbOutputReport(1, 2, 3, 4, 0).Length);
        }

        [Fact]
        public void JoystickBlacklist_CoversTheWholeFamilyInSdlHintFormat()
        {
            // SDL_hints.h:1401-1404: comma-separated 0xVVVV/0xPPPP pairs.
            string v = PadForge.Common.Input.InputManager.JoystickBlacklistDevices;
            Assert.Contains("0x054c/0x03d5", v);   // Move ZCM1
            Assert.Contains("0x054c/0x0c5e", v);   // Move ZCM2
            Assert.Contains("0x054c/0x042f", v);   // Navigation
            foreach (string pair in v.Split(','))
                Assert.Matches("^0x[0-9a-f]{4}/0x[0-9a-f]{4}$", pair);
        }

        [Fact]
        public void SphereColors_FollowThePlayerPaletteAndWrap()
        {
            Assert.Equal(PsMoveDirectService.DefaultSphereColor(1), PsMoveDirectService.DefaultSphereColor(5));
            Assert.NotEqual(PsMoveDirectService.DefaultSphereColor(1), PsMoveDirectService.DefaultSphereColor(2));
            Assert.Equal(PsMoveDirectService.DefaultSphereColor(1), PsMoveDirectService.DefaultSphereColor(0));
        }

        // ── calibration blob decode (psmove_calibration.c byte-exact) ───────────

        private static void EncU16(byte[] d, int off, int value)
        {
            int stored = value + 0x8000;   // psmove_calibration_decode_16bit_unsigned inverse
            d[off] = (byte)(stored & 0xFF);
            d[off + 1] = (byte)((stored >> 8) & 0xFF);
        }

        private static void EncS16(byte[] d, int off, int value)
        {
            d[off] = (byte)(value & 0xFF);
            d[off + 1] = (byte)((value >> 8) & 0xFF);
        }

        [Fact]
        public void CalibrationZcm1_DecodesTheLinearMapping()
        {
            var blob = new byte[143];
            // Accel ±1g points at the orientation slots psmove_calibration.c
            // reads (x: orientations 1/3, y: 5/4 (+2), z: 2/0 (+4)).
            EncU16(blob, 0x04 + 6 * 1, -4300); EncU16(blob, 0x04 + 6 * 3, 4300);       // x low/high
            EncU16(blob, 0x04 + 6 * 5 + 2, -4200); EncU16(blob, 0x04 + 6 * 4 + 2, 4200); // y
            EncU16(blob, 0x04 + 6 * 2 + 4, -4400); EncU16(blob, 0x04 + 6 * 0 + 4, 4400); // z
            // Gyro bias words at 0x2A and the 80 RPM points at 0x46 + 8*axis.
            EncU16(blob, 0x2A, 0); EncU16(blob, 0x2A + 2, 0); EncU16(blob, 0x2A + 4, 0);
            EncU16(blob, 0x46 + 8 * 0, 6000);
            EncU16(blob, 0x46 + 8 * 1 + 2, 6000);
            EncU16(blob, 0x46 + 8 * 2 + 4, 6000);

            var cal = PsMoveDirectService.DecodeCalibrationBlob(blob, zcm2: false);
            Assert.NotNull(cal);
            Assert.Equal(2.0f / 8600.0f, cal.Fax, 6);
            Assert.Equal(2.0f / 8400.0f, cal.Fay, 6);
            Assert.Equal(2.0f / 8800.0f, cal.Faz, 6);
            // c = -(f*low) - 1: symmetric points give a zero offset.
            Assert.Equal(0.0f, cal.Cax, 5);
            // 80 RPM in rad/s divided by the raw point.
            float expectedG = 80.0f * 2.0f * (float)Math.PI / 60.0f / 6000.0f;
            Assert.Equal(expectedG, cal.Fgx, 7);
            Assert.Equal(expectedG, cal.Fgy, 7);
            Assert.Equal(expectedG, cal.Fgz, 7);
            Assert.Equal(0, cal.Dgx);
        }

        [Fact]
        public void CalibrationZcm2_DecodesSignedPointsAndDrift()
        {
            var blob = new byte[96];
            EncS16(blob, 0x02 + 6 * 1, -16384); EncS16(blob, 0x02 + 6 * 0, 16384);       // x
            EncS16(blob, 0x02 + 6 * 3 + 2, -16384); EncS16(blob, 0x02 + 6 * 2 + 2, 16384); // y
            EncS16(blob, 0x02 + 6 * 5 + 4, -16384); EncS16(blob, 0x02 + 6 * 4 + 4, 16384); // z
            EncS16(blob, 0x26, 10); EncS16(blob, 0x26 + 2, 20); EncS16(blob, 0x26 + 4, 30); // drift
            EncS16(blob, 0x30 + 6 * 3, -5000); EncS16(blob, 0x30 + 6 * 0, 5000);           // gx ±90rpm
            EncS16(blob, 0x30 + 6 * 4 + 2, -5000); EncS16(blob, 0x30 + 6 * 1 + 2, 5000);   // gy
            EncS16(blob, 0x30 + 6 * 5 + 4, -5000); EncS16(blob, 0x30 + 6 * 2 + 4, 5000);   // gz

            var cal = PsMoveDirectService.DecodeCalibrationBlob(blob, zcm2: true);
            Assert.NotNull(cal);
            Assert.Equal(2.0f / 32768.0f, cal.Fax, 7);
            float expectedG = 2.0f * 90.0f * 2.0f * (float)Math.PI / 60.0f / 10000.0f;
            Assert.Equal(expectedG, cal.Fgx, 7);
            Assert.Equal(10, cal.Dgx);
            Assert.Equal(20, cal.Dgy);
            Assert.Equal(30, cal.Dgz);
        }

        [Fact]
        public void Calibration_RefusesWrongSizesAndDegeneratePoints()
        {
            Assert.Null(PsMoveDirectService.DecodeCalibrationBlob(new byte[10], zcm2: false));
            Assert.Null(PsMoveDirectService.DecodeCalibrationBlob(new byte[96], zcm2: false));
            Assert.Null(PsMoveDirectService.DecodeCalibrationBlob(new byte[143], zcm2: true));
            // All-zero ZCM1 blob: every high==low, every gyro point zero.
            Assert.Null(PsMoveDirectService.DecodeCalibrationBlob(new byte[143], zcm2: false));
            Assert.Null(PsMoveDirectService.DecodeCalibrationBlob(new byte[96], zcm2: true));
        }

        [Fact]
        public void CalibrationRegistry_RoundTripsAndFallsBackToASinglePad()
        {
            PsMoveCalibrationRegistry.LoadRegistry(null);
            Assert.Null(PsMoveCalibrationRegistry.Get("aabbccddeeff"));

            PsMoveCalibrationRegistry.Store("AABBCCDDEEFF", new byte[] { 1, 2, 3 });
            Assert.Equal(new byte[] { 1, 2, 3 }, PsMoveCalibrationRegistry.Get("aabbccddeeff"));
            // Single stored pad answers even without a MAC (PDO path carried none).
            Assert.Equal(new byte[] { 1, 2, 3 }, PsMoveCalibrationRegistry.Get(null));

            string[] persisted = PsMoveCalibrationRegistry.SaveRegistry();
            Assert.Single(persisted);
            Assert.Equal("aabbccddeeff=010203", persisted[0], ignoreCase: true);

            PsMoveCalibrationRegistry.Store("112233445566", new byte[] { 4 });
            // Two pads stored: the no-MAC fallback must refuse to guess.
            Assert.Null(PsMoveCalibrationRegistry.Get(null));

            PsMoveCalibrationRegistry.LoadRegistry(persisted);
            Assert.Equal(new byte[] { 1, 2, 3 }, PsMoveCalibrationRegistry.Get("aabbccddeeff"));
            Assert.Null(PsMoveCalibrationRegistry.Get("112233445566"));
        }
    }
}
