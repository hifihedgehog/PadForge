using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Skydimo SK0902 against OpenRGB's SkydimoHIDController
    /// (#557).</summary>
    public class SkydimoHidLightingTests
    {
        [Fact]
        public void TheLights_AreOpenRgbsMatrix()
        {
            var lights = SkydimoHidLighting.Lights();
            Assert.Equal(49, lights.Count);
            Assert.Equal(Enumerable.Range(0, 49), lights.Select(l => l.Address));
            Assert.All(lights, l => Assert.True(l.HasPosition));
            // skydimo_hid_led_positions (RGBController_SkydimoHID.cpp:25-34):
            // column, row, the columns snaking up and down.
            Assert.Equal((0f, 6f), (lights[0].X, lights[0].Y));
            Assert.Equal((0f, 0f), (lights[6].X, lights[6].Y));
            Assert.Equal((1f, 0f), (lights[7].X, lights[7].Y));
            Assert.Equal((1f, 6f), (lights[13].X, lights[13].Y));
            Assert.Equal((6f, 0f), (lights[48].X, lights[48].Y));
            Assert.Equal(49, lights.Select(l => (l.X, l.Y)).Distinct().Count());
        }

        [Fact]
        public void APicture_IsSetLedsReports_WithTheirCrc()
        {
            // Written from SetLEDs, SendPacket and CalculateCRC8
            // (SkydimoHIDController.cpp:17-39 and 107-195) for colors
            // 0x010203 times the light's number: green, red and blue, 60
            // bytes a report, the CRC-8 over bytes 0 to 62, then the frame's
            // end.
            var expected = new[]
            {
                "01 00 00 02 01 03 04 02 06 06 03 09 08 04 0C 0A 05 0F 0C 06 12 0E 07 15 10 08 18 12 09 1B 14 0A 1E 16 0B 21 18 0C 24 1A 0D 27 1C 0E 2A 1E 0F 2D 20 10 30 22 11 33 24 12 36 26 13 39 28 14 3C C2",
                "01 14 00 2A 15 3F 2C 16 42 2E 17 45 30 18 48 32 19 4B 34 1A 4E 36 1B 51 38 1C 54 3A 1D 57 3C 1E 5A 3E 1F 5D 40 20 60 42 21 63 44 22 66 46 23 69 48 24 6C 4A 25 6F 4C 26 72 4E 27 75 50 28 78 8A",
                "01 28 00 52 29 7B 54 2A 7E 56 2B 81 58 2C 84 5A 2D 87 5C 2E 8A 5E 2F 8D 60 30 90 62 31 93 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 5C",
                "01 FF FF 31 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 1E 00 00 00",
            };
            var model = DeskLightKit.ModelOf(SkydimoHidLighting.Instance, "SkydimoHIDController");
            Assert.Equal(new[] { VendorPrograms.Skydimo }, model.Programs);
            var (io, session) = DeskLightKit.Painted(model, Distinct(49));
            var calls = Calls(io);
            Assert.Equal(expected, calls.Select(c => Hex(c.Bytes)));
            Assert.All(calls, c => Assert.Equal("out", c.Kind));
            DeskLightKit.HandsBackNothing(io, session);
        }

        [Fact]
        public void TheCrc_IsCrc8WithPolynomial7()
        {
            // CRC-8 (poly 0x07, init 0) of "123456789" is 0xF4.
            var check = System.Text.Encoding.ASCII.GetBytes("123456789");
            Assert.Equal(0xF4, SkydimoHidLighting.Crc8(check, check.Length));
        }
    }
}
