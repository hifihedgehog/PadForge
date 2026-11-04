using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The devices only G-Helper, ckb-next, libratbag or alienfx-tools light,
    /// byte for byte against those projects (#557).</summary>
    public class OtherProjectsLightingTests
    {
        private static string Hex(byte[] bytes, int count) => BitConverter.ToString(bytes, 0, count).Replace('-', ' ');

        private static PluginDevice Row(ushort vendor, ushort product)
            => PluginDeviceLighting.Devices.Single(d => d.VendorId == vendor && d.ProductId == product);

        private static string First(PluginDevice row, int color, int count)
        {
            var colors = new int[Math.Max(row.Slots, 1)];
            colors[0] = color;
            return Hex(row.Picture(colors).First().Bytes, count);
        }

        private static HidCollectionInfo Collection(ushort vendor, ushort product, ushort output = 0, ushort feature = 0, ushort usage = 1, ushort page = 0xFF00)
            => new() { Path = "fake", VendorId = vendor, ProductId = product, OutputReportLength = output, FeatureReportLength = feature, Usage = usage, UsagePage = page };

        // ── G-Helper's mice ──

        /// <summary>GetUpdateLightingModePacket, for each mouse's zones.</summary>
        [Theory]
        [InlineData(0x1B0A, "00 51 28 00 00 00 64 11 22 33")]
        [InlineData(0x1B67, "00 51 28 01 00 00 64 11 22 33")]
        [InlineData(0x1B63, "00 51 28 01 00 00 64 11 22 33")]
        [InlineData(0x1C8F, "00 51 28 01 00 00 64 11 22 33")]
        [InlineData(0x1C69, "00 51 28 01 00 00 64 11 22 33")]
        [InlineData(0x1B16, "00 51 28 03 00 00 64 11 22 33")]
        [InlineData(0x1C56, "00 51 28 03 00 00 64 11 22 33")]
        [InlineData(0x1C57, "00 51 28 03 00 00 64 11 22 33")]
        public void AGHelperMouse_TakesItsZoneFrame(int product, string frame)
        {
            var row = Row(0x0B05, (ushort)product);
            Assert.Equal(frame, First(row, 0x112233, 10));
            Assert.Equal(0, row.Interface);
            Assert.Equal(0xFF01, row.UsagePage);
            Assert.Equal(65, row.OutputLength);
            Assert.Equal(new[] { VendorPrograms.ArmouryCrate }, row.Programs);
        }

        [Fact]
        public void TheKerisIIOriginKjp_TakesAFramePerZone()
        {
            var row = Row(0x0B05, 0x1D4C);
            var steps = row.Picture(new[] { 0x010203, 0x040506, 0x070809 }).ToList();
            Assert.Equal(3, steps.Count);
            Assert.Equal("00 51 28 00 00 00 64 01 02 03", Hex(steps[0].Bytes, 10));
            Assert.Equal("00 51 28 01 00 00 64 04 05 06", Hex(steps[1].Bytes, 10));
            Assert.Equal("00 51 28 02 00 00 64 07 08 09", Hex(steps[2].Bytes, 10));
        }

        /// <summary>"mi_02&amp;col03", report 03 (HarpeIIAce.cs).</summary>
        [Fact]
        public void TheHarpeIIAceOnItsDongle_IsReport03()
        {
            var row = Row(0x0B05, 0x1AD0);
            Assert.Equal("03 51 28 01 00 00 64 11 22 33", First(row, 0x112233, 10));
            Assert.Equal((2, 0xFF01), (row.Interface, row.UsagePage));
        }

        /// <summary>BulwarkDock.cs: report EC on interface 1.</summary>
        [Fact]
        public void TheBulwarkDock_IsReportEc()
        {
            var row = Row(0x0B05, 0x1C7F);
            Assert.Equal("EC 51 00 00 00 00 64 11 22 33 00 00 00 FF 00 00 FF", First(row, 0x112233, 17));
            Assert.Equal(1, row.Interface);
        }

        // ── G-Helper's headsets ──

        [Theory]
        [InlineData(0x1AFA, "CC 51 28 00 00 01 64 11 22 33", 0xFF00, 64)]
        [InlineData(0x1D41, "CC 51 28 00 00 01 64 11 22 33", 0xFF00, 64)]
        [InlineData(0x1B84, "CC 51 28 00 00 01 64 11 22 33", 0xFF00, 64)]
        [InlineData(0x195B, "CC 51 28 00 00 01 00 04 00 00 11 22 33", 0xFF00, 64)]
        [InlineData(0x1965, "CC 51 28 00 00 01 00 00 FF 00 11 22 33", 0xFF53, 32)]
        public void AHeadset_TakesItsMusicModesPacket(int product, string frame, int page, int length)
        {
            var row = Row(0x0B05, (ushort)product);
            Assert.Equal(frame, First(row, 0x112233, frame.Split(' ').Length));
            Assert.Equal(page, row.UsagePage);
            Assert.Equal(length, row.OutputLength);
            // No save after it (WriteStream).
            Assert.Single(row.Picture(new[] { 0 }));
        }

        [Fact]
        public void TheHeadsetsWithALedSwitch_TurnItOnFirst()
        {
            foreach (ushort product in new ushort[] { 0x1AFA, 0x1D41, 0x1B84 })
                Assert.Equal("CC 51 10 00 00 01", Hex(Assert.Single(Row(0x0B05, product).Start).Bytes, 6));
            Assert.Empty(Row(0x0B05, 0x195B).Start);
            Assert.Empty(Row(0x0B05, 0x1965).Start);
        }

        /// <summary>CetraSpeedNova.cs:59-67. G-Helper has no way to show a color without the
        /// save, so a picture takes the stored bounds.</summary>
        [Fact]
        public void TheCetraSpeedNova_SavesEachColor_AtAStoredWritesPace()
        {
            var row = Row(0x0B05, 0x1AD3);
            var steps = row.Picture(new[] { 0x112233 }).ToList();
            Assert.Equal("CC 51 28 00 00 01 01 11 22 33", Hex(steps[0].Bytes, 10));
            Assert.Equal("CC 50 55", Hex(steps[1].Bytes, 3));
            Assert.Equal(LenovoLegionLighting.HoldMs, row.HoldMs);
            Assert.Equal(LenovoLegionLighting.RepaintMs, row.RepaintMs);
        }

        /// <summary>DeltaS.cs:97-126: direct mode on each earcup's controller, sixteen LEDs red,
        /// blue, green, and direct mode off for the hand-back.</summary>
        [Fact]
        public void TheDeltaS_StreamsDirect_AndLeavesDirectMode()
        {
            var row = Row(0x0B05, 0x1943);
            var start = row.Start.Select(s => s.Bytes).ToList();
            Assert.Equal(9, start.Count);
            Assert.Equal("CC 60 10 00 00 00", Hex(start[0], 6));
            Assert.Equal("CC 80 00 CC 00 01 00 02 80 20", Hex(start[1], 10));
            Assert.Equal("CC 80 00 CC 00 01 01 01 01", Hex(start[2], 9));
            Assert.Equal("CC 80 00 CC 00 01 00 02 80 2F", Hex(start[3], 10));
            Assert.Equal("CC 80 00 CC 00 01 01 01 01", Hex(start[4], 9));
            Assert.Equal("CC 80 00 CE 00 01 00 02 80 20", Hex(start[5], 10));
            var back = row.HandBack.Select(s => s.Bytes).ToList();
            Assert.Equal(10, back.Count);
            Assert.Equal("CC 80 00 CC 00 01 01 01 00", Hex(back[2], 9));
            Assert.Equal("CC 60 10 00 00 01", Hex(back[9], 6));
            var colors = new int[16];
            colors[0] = 0x112233;
            colors[15] = 0x445566;
            var frame = row.Picture(colors).Single().Bytes;
            Assert.Equal("CC 60 20 00 00 11 33 22", Hex(frame, 8));
            Assert.Equal(new byte[] { 0x44, 0x66, 0x55 }, frame.Skip(5 + 45).Take(3).ToArray());
            Assert.Equal(16, row.Lights.Count);
        }

        // ── G-Helper's aliases ──

        [Theory]
        [InlineData(0x1B3F, "ASUS ROG Azoth Extreme", 1)]
        [InlineData(0x1CEF, "ASUS ROG Azoth Extreme SE", 1)]
        [InlineData(0x1C24, "ASUS ROG Azoth X", 1)]
        [InlineData(0x1C25, "ASUS ROG Azoth X", 1)]
        [InlineData(0x1A07, "ASUS ROG Strix Scope RX TKL Wireless Deluxe", 1)]
        [InlineData(0x1C60, "ASUS TUF Gaming K3 GEN II Miku", 1)]
        [InlineData(0x1B3A, "ASUS TX 98", 1)]
        [InlineData(0x1A57, "ASUS ROG Keris Wireless EVA", 0)]
        public void AProductGHelperKnows_TakesTheModelItIs(int product, string name, int iface)
        {
            var collection = new HidCollectionInfo
            {
                Path = "fake", VendorId = 0x0B05, ProductId = (ushort)product, InterfaceNumber = iface,
                UsagePage = (ushort)(iface == 1 ? 0xFF00 : 0xFF01), Usage = 1, OutputReportLength = 65,
            };
            var model = AsusAuraUsbLighting.Instance.Match(collection);
            Assert.Equal(name, model.Name);
            Assert.NotEmpty(model.Map.Lights);
        }

        // ── ckb-next ──

        [Fact]
        public void CkbNextsOldCorsairs_TakeOpenRgbsFrames()
        {
            Assert.Equal(0x1B3F, CorsairPeripheralLighting.OtherIds[0x1B40]);
            Assert.Equal(0x1B2F, CorsairPeripheralLighting.OtherIds[0x1B19]);
            Assert.Equal(0x1B2F, CorsairPeripheralLighting.OtherIds[0x1B22]);
        }

        /// <summary>device_bragi.c:83-86 and led_bragi.c:94-99.</summary>
        [Fact]
        public void TheK60ProMono_IsTheK60ProInOneChannel()
        {
            var device = CorsairPeripheralV2Lighting.DeviceOf(0x1B83);
            Assert.True(device.Monochrome);
            Assert.Equal(CorsairLights.K60RgbProDevice.Zones, device.Zones);
            Assert.Equal(0x10, CorsairPeripheralV2Lighting.LightCtrlMonochrome);
        }

        // ── libratbag ──

        [Fact]
        public void TheMarsGamingMm4_TakesItsColorInverted()
        {
            var row = Row(0x04D9, 0xFA58);
            Assert.Equal("02 04 EE DD CC 03 00", First(row, 0x112233, 7));
            Assert.Equal(PluginStepKind.Feature, row.Picture(new[] { 0 }).Single().Kind);
            Assert.Equal(16, row.FeatureLength);
        }

        [Fact]
        public void TheNubwoX7_IsItsAestheticReport()
        {
            var row = Row(0x258A, 0x0012);
            Assert.Equal("03 06 BB AA 2A 00 0A 00 11 22 33 01 00 03 01", First(row, 0x112233, 15));
            Assert.Equal(LenovoLegionLighting.HoldMs, row.HoldMs);
        }

        /// <summary>driver-steelseries.c:716-727: 1 is off, and a level takes value/86 + 2.</summary>
        [Theory]
        [InlineData(0x000000, 1)]
        [InlineData(0x000001, 2)]
        [InlineData(0x560000, 3)]
        [InlineData(0xFFFFFF, 4)]
        [InlineData(0x0000FF, 4)]
        public void TheSenseiRaw_TakesThreeLevelsOfWhite(int color, int level)
        {
            Assert.Equal(level, PluginDeviceLighting.SenseiRawLevel(color));
            Assert.Equal($"00 05 01 {level:X2}", First(Row(0x1038, 0x1369), color, 4));
            Assert.Equal("00 07 01 01", Hex(Row(0x1038, 0x1369).Start.Single().Bytes, 4));
        }

        // ── The Kone EMP ──

        private static byte[] EmpSettings()
        {
            var settings = new byte[RoccatKoneEmpLighting.SettingsLength];
            settings[0] = 0x06;
            settings[1] = 0x29;
            settings[2] = 0x01;
            settings[19] = 0xF0;
            settings[20] = 0x01;
            settings[21] = 0x03;
            return settings;
        }

        private static FakeReportChannel EmpChannel(byte ready = 0x01)
        {
            var io = new FakeReportChannel { FeatureReportLength = 41 };
            io.Feature = id => id switch
            {
                0x04 => new byte[] { 0x04, ready },
                0x05 => new byte[] { 0x05, 0x03, 0x02 },
                0x06 => EmpSettings(),
                _ => null,
            };
            return io;
        }

        /// <summary>roccat_current_profile, roccat_set_config_profile, roccat_wait_ready and the
        /// read of report 06. Hand-back writes back what it read.</summary>
        [Fact]
        public void TheKoneEmp_ReadsItsProfile_WritesColors_AndHandsItBack()
        {
            var io = EmpChannel();
            var session = RoccatKoneEmpLighting.Begin(io, _ => { });
            var sets = io.Calls.Where(c => c.Kind == "set").Select(c => c.Bytes).ToList();
            Assert.Equal("04 02 80", Hex(Assert.Single(sets), 3));
            var lights = RoccatKoneEmpLighting.Instance.Match(Collection(0x1E7D, 0x2E24, feature: 41)).Map.Lights;
            Assert.Equal(4, lights.Count);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, new[] { 0x112233, 0x445566, 0x778899, 0xAABBCC }));
            var written = io.Calls.Where(c => c.Kind == "set").Last().Bytes;
            Assert.Equal(0xFF, written[19]);
            Assert.Equal(0x00, written[20]);
            Assert.Equal(0x01, written[21]);
            Assert.Equal("1E 11 22 33 1E 44 55 66 1E 77 88 99 1E AA BB CC", Hex(written.Skip(23).ToArray(), 16));
            int sum = written.Take(39).Sum(b => b);
            Assert.Equal((byte)sum, written[39]);
            Assert.Equal((byte)(sum >> 8), written[40]);
            // Stored, so the stored bounds, and what it read goes back.
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(EmpSettings(), io.Calls.Where(c => c.Kind == "set").Last().Bytes);
            Assert.Equal(EmpSettings(), Assert.Single(session.CrashFrames));
        }

        [Fact]
        public void TheKoneEmp_NeedsItsReports_AndAReadyDevice()
        {
            Assert.Null(RoccatKoneEmpLighting.Instance.Match(Collection(0x1E7D, 0x2E24, feature: 40)));
            Assert.Null(RoccatKoneEmpLighting.Instance.Match(Collection(0x1E7D, 0x2E23, feature: 41)));
            Assert.Null(RoccatKoneEmpLighting.Begin(null, _ => { }));
            Assert.Null(RoccatKoneEmpLighting.Begin(EmpChannel(ready: 0x02), _ => { }));
            Assert.Null(RoccatKoneEmpLighting.Begin(new FakeReportChannel { FeatureReportLength = 41 }, _ => { }));
            Assert.Contains(RoccatKoneEmpLighting.Instance, HidLightingFamilies.All);
            var model = RoccatKoneEmpLighting.Instance.Match(Collection(0x1E7D, 0x2E24, feature: 41));
            Assert.Equal(LenovoLegionLighting.HoldMs, model.HoldMs);
            Assert.Equal(LenovoLegionLighting.RepaintMs, model.RepaintMs);
        }

        // ── Sinowealth mice by firmware ──

        [Fact]
        public void TheOrderOfAColor_GoesByTheFirmware()
        {
            var configuration = new byte[SinowealthMouseLighting.ConfigSize];
            var rbg = SinowealthMouseLighting.GloriousWrite(configuration, 0x112233);
            Assert.Equal("11 33 22", Hex(rbg.Skip(0x39).ToArray(), 3));
            var rgb = SinowealthMouseLighting.GloriousWrite(configuration, 0x112233, true);
            Assert.Equal("11 22 33", Hex(rgb.Skip(0x39).ToArray(), 3));
            foreach (string firmware in new[] { "3106", "3110", "2642", "V127", "V287" })
                Assert.Contains(firmware, SinowealthMouseLighting.RgbFirmware);
            Assert.DoesNotContain("V102", SinowealthMouseLighting.RgbFirmware);
            Assert.DoesNotContain("1009", SinowealthMouseLighting.RgbFirmware);
        }

        [Theory]
        [InlineData(0x258A, 0x0027)]
        [InlineData(0x258A, 0x0051)]
        [InlineData(0x3794, 0xA000)]
        public void LibratbagsMice_AreFoundOnTheVendorPage(int vendor, int product)
        {
            var row = SinowealthMouseLighting.RegistrationOf(Collection((ushort)vendor, (ushort)product));
            Assert.Equal(SinowealthMouseLighting.MouseDetector, row.Detector);
            Assert.Null(SinowealthMouseLighting.RegistrationOf(Collection((ushort)vendor, 0x0001)));
        }

        // ── alienfx-tools ──

        [Theory]
        [InlineData(0x187C, 0x0527, 9, 0, 1, AlienFxLighting.Api.V2)]
        [InlineData(0x187C, 0x0527, 12, 0, 1, AlienFxLighting.Api.V3)]
        [InlineData(0x187C, 0x0527, 34, 0, 1, AlienFxLighting.Api.V4)]
        [InlineData(0x187C, 0x0527, 65, 0, 1, AlienFxLighting.Api.V6)]
        [InlineData(0x0D62, 0xCA01, 0, 64, 0xCC, AlienFxLighting.Api.V5)]
        [InlineData(0x0424, 0x1234, 65, 0, 1, AlienFxLighting.Api.V6)]
        [InlineData(0x0461, 0x1234, 65, 0, 1, AlienFxLighting.Api.V7)]
        [InlineData(0x04F2, 0x1234, 65, 0, 1, AlienFxLighting.Api.V8)]
        internal void TheProtocol_GoesByVendorAndReportLength(int vendor, int product, int output, int feature, int usage, AlienFxLighting.Api api)
            => Assert.Equal(api, AlienFxLighting.ApiOf(Collection((ushort)vendor, (ushort)product, (ushort)output, (ushort)feature, (ushort)usage)));

        [Theory]
        [InlineData(0x187C, 0x0527, 32, 0, 1)]
        [InlineData(0x0D62, 0xCA01, 64, 64, 0xCC)]
        [InlineData(0x0D62, 0xCA01, 0, 64, 1)]
        [InlineData(0x0424, 0x274C, 65, 0, 1)]
        [InlineData(0x0461, 0x1234, 64, 0, 1)]
        [InlineData(0x1234, 0x1234, 65, 0, 1)]
        public void AnythingElse_HasNoProtocol(int vendor, int product, int output, int feature, int usage)
            => Assert.Null(AlienFxLighting.ApiOf(Collection((ushort)vendor, (ushort)product, (ushort)output, (ushort)feature, (ushort)usage)));

        /// <summary>Chip makers that sell to anyone: a collection must name Alienware.</summary>
        [Fact]
        public void TheChipMakers_MustNameAlienware()
        {
            var model = new AlienFxLighting().Match(Collection(0x0461, 0x1234, 65));
            Assert.False(model.Identify(new FakeReportChannel { ProductString = "USB Optical Mouse" }));
            Assert.True(model.Identify(new FakeReportChannel { ProductString = "Alienware AW610M" }));
            Assert.True(model.Identify(new FakeReportChannel { ManufacturerString = "alienware" }));
            Assert.Null(new AlienFxLighting().Match(Collection(0x187C, 0x0527, 34)).Identify);
        }

        [Fact]
        public void TheOldProtocols_AreReset_Color_Loop_Update()
        {
            var io = new FakeReportChannel { InputReportLength = 0 };
            var session = new AlienFxLighting.Session(AlienFxLighting.Api.V3);
            Assert.False(session.Feature);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, null, new[] { 0x112233 }));
            var out12 = io.Calls.Where(c => c.Kind == "out").Select(c => c.Bytes).ToList();
            Assert.Equal("02 07 04", Hex(out12[0], 3));
            Assert.Equal("02 03 01 FF FF FF 11 22 33 11 22 33", Hex(out12.First(f => f[1] == 0x03), 12));
            Assert.Equal("02 04", Hex(out12[^2], 2));
            Assert.Equal("02 05", Hex(out12[^1], 2));
            Assert.Equal("02 03 01 FF FF FF 12 31 23", Hex(AlienFxLighting.V1Color(AlienFxLighting.Api.V2, 0x112233), 9));
        }

        [Fact]
        public void TheLaptopController_TakesControlColorControl()
        {
            var io = new FakeReportChannel();
            Assert.Equal(HidLightingPaint.Sent, new AlienFxLighting.Session(AlienFxLighting.Api.V4).Paint(io, _ => { }, null, new[] { 0x112233 }));
            var frames = io.Calls.Select(c => c.Bytes).ToList();
            Assert.Equal(4, frames.Count);
            Assert.Equal("00 03 21 00 04 FF FF", Hex(frames[0], 7));
            Assert.Equal("00 03 21 00 01 FF FF", Hex(frames[1], 7));
            Assert.Equal("00 03 27 11 22 33 00 1A 00 01 02", Hex(frames[2], 11));
            Assert.Equal(25, frames[2][33]);
            Assert.Equal("00 03 21 00 03 FF FF", Hex(frames[3], 7));
        }

        /// <summary>Four bytes per key, the ID from 1, fifteen to a report of 64.</summary>
        [Fact]
        public void TheLaptopKeyboard_IsFeatureReports_FifteenKeysEach()
        {
            var io = new FakeReportChannel { FeatureReportLength = 64 };
            var session = AlienFxLighting.Begin(io, _ => { }, AlienFxLighting.Api.V5);
            Assert.True(session.Feature);
            var start = io.Calls.Select(c => c.Bytes).ToList();
            Assert.Equal("CC 94", Hex(start[0], 2));
            Assert.Equal("CC 83 38 9C FF", Hex(start[1], 5));
            session.Paint(io, _ => { }, null, new[] { 0x112233 });
            var frames = io.Calls.Skip(2).Select(c => c.Bytes).ToList();
            Assert.Equal("CC 94", Hex(frames[0], 2));
            Assert.Equal("CC 93", Hex(frames[1], 2));
            Assert.Equal("CC", Hex(frames[2], 1));
            Assert.Equal("CC 8C 02 00 01 11 22 33 02 11 22 33", Hex(frames[3], 12));
            Assert.Equal("CC 8C 13", Hex(frames[^2], 3));
            Assert.Equal("CC 8B 01 FF", Hex(frames[^1], 4));
            var picture = AlienFxLighting.V5Picture(64, 0x112233);
            Assert.Equal((int)Math.Ceiling(AlienFxLighting.V5Lights / 15.0), picture.Count);
            Assert.Equal(AlienFxLighting.V5Lights, picture.Last()[4 + (AlienFxLighting.V5Lights - 1) % 15 * 4]);
        }

        /// <summary>A frame filled with FF, and the checksum in XOR.</summary>
        [Fact]
        public void The65ByteProtocol_IsFilledWithFf()
        {
            var frame = AlienFxLighting.V6Color(0x112233);
            Assert.Equal("00 92 37 0A FF FF 87 FF 04 FF 11 22 33 64", Hex(frame, 14));
            Assert.Equal((byte)(0x11 ^ 0x22 ^ 0x33 ^ 0xFF ^ 8), frame[14]);
            Assert.Equal(0xFF, frame[15]);
            Assert.Equal("00 95 00 00 00 FF", Hex(AlienFxLighting.V6Reset(), 6));
            var io = new FakeReportChannel();
            Assert.IsType<AlienFxLighting.Session>(AlienFxLighting.Begin(io, _ => { }, AlienFxLighting.Api.V6));
            Assert.Equal("00 95", Hex(io.Calls.Single().Bytes, 2));
        }

        [Fact]
        public void TheMice_TakeAFramePerLight_AndTheKeyboardsAReadyThenBlocks()
        {
            var io = new FakeReportChannel { InputReportLength = 65 };
            new AlienFxLighting.Session(AlienFxLighting.Api.V7).Paint(io, _ => { }, null, new[] { 0x112233 });
            var mouse = io.Calls.Where(c => c.Kind == "out").Select(c => c.Bytes).ToList();
            Assert.Equal(AlienFxLighting.V7Lights, mouse.Count);
            Assert.Equal("00 40 10 0C 00 01 64 03 11 22 33", Hex(mouse[3], 11));

            io = new FakeReportChannel();
            new AlienFxLighting.Session(AlienFxLighting.Api.V8).Paint(io, _ => { }, null, new[] { 0x112233 });
            Assert.Equal("01 0E 80 00 01", Hex(io.Calls.First(c => c.Kind == "set").Bytes, 5));
            var blocks = io.Calls.Where(c => c.Kind == "out").Select(c => c.Bytes).ToList();
            Assert.Equal(AlienFxLighting.V8Lights / 4, blocks.Count);
            Assert.Equal("01 0E 01 00 01 00 81 00 A5 00 11 22 33 11 22 33 02", Hex(blocks[0], 17));
            Assert.Equal(0x01, blocks[0][20]);
            Assert.Equal(2, blocks[1][4]);
        }

        [Fact]
        public void AWriteThatFails_FailsThePicture()
        {
            foreach (AlienFxLighting.Api api in Enum.GetValues(typeof(AlienFxLighting.Api)))
            {
                var io = new FakeReportChannel { FeatureReportLength = 64, FailWrites = true };
                if (api is not AlienFxLighting.Api.V5 and not AlienFxLighting.Api.V6)
                    Assert.NotNull(AlienFxLighting.Begin(io, _ => { }, api));
                else
                    Assert.Null(AlienFxLighting.Begin(io, _ => { }, api));
                Assert.Equal(HidLightingPaint.Failed, new AlienFxLighting.Session(api).Paint(io, _ => { }, null, new[] { 1 }));
            }
        }

        /// <summary>The family comes last, so the rows OpenRGB and SignalRGB describe keep them.</summary>
        [Fact]
        public void TheFamily_ComesAfterTheRowsThatDescribeADevice()
        {
            Assert.Same(AlienFxLighting.Instance, HidLightingFamilies.All[^1]);
            var dell = new HidCollectionInfo { Path = "fake", VendorId = 0x187C, ProductId = 0x0550, FeatureReportLength = 34, OutputReportLength = 34 };
            Assert.Same(AlienwareLighting.Instance, HidLightingFamilies.Match(dell, HidLightingFamilies.All).Family);
        }
    }
}
