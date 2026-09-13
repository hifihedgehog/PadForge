using System;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The pass-through lane frames a game's DS5 effect payload
    /// for the physical pad itself (#434). The frame has to match what
    /// SDL3 writes (SDL_hidapi_ps5.c HIDAPI_DriverPS5_InternalSendJoystickEffect),
    /// since that is the wire shape the lane produced before it left SDL's
    /// throttled rumble thread: USB report 0x02 plus 47 payload bytes, or
    /// Bluetooth report 0x31, a zero tag byte, the 0x10 magic, the payload,
    /// and a CRC32 over the 0xA2 header byte and bytes 0..73.</summary>
    public class DualSenseEffectFramingTests
    {
        private static byte[] Payload(int length)
        {
            var p = new byte[length];
            for (int i = 0; i < length; i++) p[i] = (byte)(i * 7 + 3);
            return p;
        }

        // Independent table-driven CRC32 (IEEE 802.3, reflected 0xEDB88320),
        // so the stamp is checked against a second implementation.
        private static uint Crc32(ReadOnlySpan<byte> data)
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        [Fact]
        public void Usb_IsReport02WithFortySevenPayloadBytes()
        {
            var payload = Payload(47);
            var report = DualSensePassthroughDispatcher.BuildEffectReport(false, payload);
            Assert.Equal(48, report.Length);
            Assert.Equal((byte)0x02, report[0]);
            Assert.Equal(payload, report.AsSpan(1, 47).ToArray());
        }

        [Fact]
        public void Usb_CutsTheEdgeFormWhereSdlCutsIt()
        {
            var payload = Payload(63);
            var report = DualSensePassthroughDispatcher.BuildEffectReport(false, payload);
            Assert.Equal(48, report.Length);
            Assert.Equal(payload.AsSpan(0, 47).ToArray(), report.AsSpan(1, 47).ToArray());
        }

        [Fact]
        public void Bluetooth_IsReport31WithHeaderPayloadAndCrc()
        {
            var payload = Payload(47);
            var report = DualSensePassthroughDispatcher.BuildEffectReport(true, payload);
            Assert.Equal(78, report.Length);
            Assert.Equal((byte)0x31, report[0]);
            Assert.Equal((byte)0x00, report[1]);
            Assert.Equal((byte)0x10, report[2]);
            Assert.Equal(payload, report.AsSpan(3, 47).ToArray());
            for (int i = 50; i < 74; i++) Assert.Equal((byte)0, report[i]);

            var crcInput = new byte[75];
            crcInput[0] = 0xA2;
            Array.Copy(report, 0, crcInput, 1, 74);
            uint expected = Crc32(crcInput);
            uint stamped = (uint)(report[74] | (report[75] << 8) | (report[76] << 16) | (report[77] << 24));
            Assert.Equal(expected, stamped);
        }

        [Fact]
        public void Bluetooth_CrcCoversThePayloadBytes()
        {
            var a = DualSensePassthroughDispatcher.BuildEffectReport(true, Payload(47));
            var payload = Payload(47);
            payload[2] = (byte)(payload[2] + 1);   // one motor byte differs
            var b = DualSensePassthroughDispatcher.BuildEffectReport(true, payload);
            Assert.NotEqual(a.AsSpan(74, 4).ToArray(), b.AsSpan(74, 4).ToArray());
        }

        [Fact]
        public void Bluetooth_StampMatchesTheWriterHelper()
        {
            var report = new byte[78];
            report[0] = 0x31; report[2] = 0x10; report[3] = 0x03; report[6] = 145;
            PlayStationEffectWriter.StampSonyBtOutputCrc(report);
            var crcInput = new byte[75];
            crcInput[0] = 0xA2;
            Array.Copy(report, 0, crcInput, 1, 74);
            uint expected = Crc32(crcInput);
            uint stamped = (uint)(report[74] | (report[75] << 8) | (report[76] << 16) | (report[77] << 24));
            Assert.Equal(expected, stamped);
        }
    }
}
