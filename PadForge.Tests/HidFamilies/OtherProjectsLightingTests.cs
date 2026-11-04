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

        /// <summary>GetUpdateLightingModePacket with zone All, the one G-Helper's window and its
        /// stream send a mouse with one zone, at each model's USBPacketSize (AsusMouse.cs:566,
        /// HarpeAceMini.cs:110, HarpeIIAce.cs:122).</summary>
        [Theory]
        [InlineData(0x1B0A, 65)]
        [InlineData(0x1B67, 65)]
        [InlineData(0x1B63, 64)]
        [InlineData(0x1C8F, 64)]
        [InlineData(0x1C69, 64)]
        [InlineData(0x1B16, 65)]
        [InlineData(0x1C56, 65)]
        [InlineData(0x1C57, 65)]
        public void AGHelperMouse_TakesItsZoneFrame(int product, int length)
        {
            var row = Row(0x0B05, (ushort)product);
            Assert.Equal("00 51 28 03 00 00 64 11 22 33", First(row, 0x112233, 10));
            Assert.Equal(0, row.Interface);
            Assert.Equal(0xFF01, row.UsagePage);
            Assert.Equal(OpenRgbRegistration.Any, row.Usage);
            Assert.Equal(length, row.OutputLength);
            Assert.Equal(length, Assert.Single(row.Picture(new[] { 0x112233 })).Bytes.Length);
            Assert.Equal(new[] { VendorPrograms.ArmouryCrate }, row.Programs);
        }

        [Fact]
        public void TheKerisIIOriginKjp_TakesAFramePerZone()
        {
            var row = Row(0x0B05, 0x1D4C);
            Assert.Equal(64, row.OutputLength);
            var steps = row.Picture(new[] { 0x010203, 0x040506, 0x070809 }).ToList();
            Assert.Equal(3, steps.Count);
            Assert.Equal(64, steps[0].Bytes.Length);
            Assert.Equal("00 51 28 00 00 00 64 01 02 03", Hex(steps[0].Bytes, 10));
            Assert.Equal("00 51 28 01 00 00 64 04 05 06", Hex(steps[1].Bytes, 10));
            Assert.Equal("00 51 28 02 00 00 64 07 08 09", Hex(steps[2].Bytes, 10));
        }

        /// <summary>"mi_02&amp;col03", report 03 (HarpeIIAce.cs).</summary>
        [Fact]
        public void TheHarpeIIAceOnItsDongle_IsReport03()
        {
            var row = Row(0x0B05, 0x1AD0);
            Assert.Equal("03 51 28 03 00 00 64 11 22 33", First(row, 0x112233, 10));
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
            {
                var start = Row(0x0B05, product).Start;
                Assert.Equal(2, start.Count);
                Assert.Equal("CC 51 10 00 00 01", Hex(start[0].Bytes, 6));
                Assert.Equal(PluginStepKind.ReadInput, start[1].Kind);
            }
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
            Assert.Equal(PluginStepKind.ReadInput, steps[1].Kind);
            Assert.Equal("CC 50 55", Hex(steps[2].Bytes, 3));
            Assert.Equal(PluginStepKind.ReadInput, steps[3].Kind);
            Assert.Equal(LenovoLegionLighting.HoldMs, row.HoldMs);
            Assert.Equal(LenovoLegionLighting.RepaintMs, row.RepaintMs);
        }

        /// <summary>DeltaS.cs:97-126: direct mode on each earcup's controller, sixteen LEDs red,
        /// blue, green, and direct mode off for the hand-back.</summary>
        [Fact]
        public void TheDeltaS_StreamsDirect_AndLeavesDirectMode()
        {
            var row = Row(0x0B05, 0x1943);
            // Each setup write waits for its echo, as WriteRegister and PauseBus do.
            Assert.All(row.Start.Where((_, i) => i % 2 == 1), s => Assert.Equal(PluginStepKind.ReadInput, s.Kind));
            Assert.All(row.HandBack.Where((_, i) => i % 2 == 1), s => Assert.Equal(PluginStepKind.ReadInput, s.Kind));
            var start = row.Start.Where(s => s.Kind == PluginStepKind.Output).Select(s => s.Bytes).ToList();
            Assert.Equal(9, start.Count);
            Assert.Equal("CC 60 10 00 00 00", Hex(start[0], 6));
            Assert.Equal("CC 80 00 CC 00 01 00 02 80 20", Hex(start[1], 10));
            Assert.Equal("CC 80 00 CC 00 01 01 01 01", Hex(start[2], 9));
            Assert.Equal("CC 80 00 CC 00 01 00 02 80 2F", Hex(start[3], 10));
            Assert.Equal("CC 80 00 CC 00 01 01 01 01", Hex(start[4], 9));
            Assert.Equal("CC 80 00 CE 00 01 00 02 80 20", Hex(start[5], 10));
            var back = row.HandBack.Where(s => s.Kind == PluginStepKind.Output).Select(s => s.Bytes).ToList();
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
            var start = Row(0x1038, 0x1369).Start;
            Assert.Equal("00 07 01 01", Hex(start[0].Bytes, 4));
            Assert.Equal(PluginStepKind.Wait, start[1].Kind);
            Assert.Equal(10, start[1].Value);
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

        /// <summary>A picture or a hand-back waits for the mouse, as roccat_write_profile does.</summary>
        [Fact]
        public void TheKoneEmp_FailsAPictureTheMouseRefuses()
        {
            byte ready = 0x01;
            var io = new FakeReportChannel { FeatureReportLength = 41 };
            io.Feature = id => id switch
            {
                0x04 => new byte[] { 0x04, ready },
                0x05 => new byte[] { 0x05, 0x03, 0x02 },
                0x06 => EmpSettings(),
                _ => null,
            };
            var session = RoccatKoneEmpLighting.Begin(io, _ => { });
            var lights = RoccatKoneEmpLighting.Instance.Match(Collection(0x1E7D, 0x2E24, feature: 41)).Map.Lights;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, new[] { 1, 2, 3, 4 }));
            ready = 0x02;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, _ => { }, lights, new[] { 1, 2, 3, 4 }));
            Assert.False(session.HandBack(io, _ => { }));
        }

        /// <summary>roccat_wait_ready gives up at the first error and after ten polls, and a
        /// mouse that answers 03 gets 100 ms more each time (roccat_is_ready).</summary>
        [Fact]
        public void TheKoneEmp_StopsAtAnError_AndGivesABusyMouseLonger()
        {
            var error = EmpChannel(ready: 0x02);
            Assert.False(RoccatKoneEmpLighting.WaitReady(error, _ => { }));
            Assert.Single(error.Calls, c => c.Kind == "get");

            var busy = EmpChannel(ready: 0x03);
            var waits = new List<int>();
            Assert.False(RoccatKoneEmpLighting.WaitReady(busy, waits.Add));
            Assert.Equal(10, busy.Calls.Count(c => c.Kind == "get"));
            Assert.Equal(10 + 10 * (100 + 10), waits.Sum());

            var notYet = EmpChannel(ready: 0x00);
            waits.Clear();
            Assert.False(RoccatKoneEmpLighting.WaitReady(notYet, waits.Add));
            Assert.Equal(10, notYet.Calls.Count(c => c.Kind == "get"));
            Assert.Equal(10 + 10 * 10, waits.Sum());
        }

        /// <summary>driver_probe_callb (ite_8291_lb.c:551-556): a Stellaris Gen5's 6010 that the
        /// laptop does not use.</summary>
        [Fact]
        public void AStellarisGen5sUnusedLightbar_IsLeftAlone()
        {
            Assert.True(PluginDeviceLighting.LightbarUnused(0x6010, "STELLARIS1XI05", "STELLARIS15I05"));
            Assert.False(PluginDeviceLighting.LightbarUnused(0x6010, "STELLARIS1XI05", "STELLARIS17I05"));
            Assert.False(PluginDeviceLighting.LightbarUnused(0x7000, "STELLARIS1XI05", "STELLARIS15I05"));
            Assert.False(PluginDeviceLighting.LightbarUnused(0x6010, "OTHER", "STELLARIS15I05"));
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

        // ── ITE's other IDs ──

        /// <summary>ite8291r3.py:9 and ite_8291.c:899-902: the same 8291 reports.</summary>
        [Theory]
        [InlineData(0x6004)]
        [InlineData(0x6006)]
        [InlineData(0x600A)]
        public void TheOther8291Ids_TakeThe8291sModel(int product)
        {
            var collection = new HidCollectionInfo
            {
                Path = "fake", VendorId = 0x048D, ProductId = (ushort)product, UsagePage = 0xFF03, Usage = 1,
                FeatureReportLength = 8, OutputReportLength = 65,
            };
            var model = new ClevoLighting(() => "").Match(collection);
            Assert.Equal("Clevo Keyboard (ITE 8291)", model.Name);
            Assert.True(model.Map.Lights.Count > 80);
            Assert.Null(new ClevoLighting(() => "").Match(collection with { OutputReportLength = 64 }));
        }

        /// <summary>ite_8291_lb.c:198-226, by product, at the Direct-free mono mode.</summary>
        [Theory]
        [InlineData(0x6010, "14 00 01 11 22 33 00 00", "08 02 01 01 64 08 00 00", "1A 00 01 04 00 00 00 01")]
        [InlineData(0x7000, "14 01 01 11 22 33 00 00", "08 21 01 01 64 01 00 00", "1A 01 01 04 00 00 00 01")]
        public void ALightbar_IsItsProductsTwoReports(int product, string color, string mode, string on)
        {
            Assert.Equal(on, Hex(Row(0x048D, (ushort)product).Start.Single().Bytes, 8));
            var steps = PluginDeviceLighting.TuxedoLightbarPicture((ushort)product, 0x112233, "", "").ToList();
            Assert.Equal(color, Hex(steps[0].Bytes, 8));
            Assert.Equal(mode, Hex(steps[1].Bytes, 8));
            Assert.Equal(PluginStepKind.Feature, steps[1].Kind);
            Assert.Equal(8, Row(0x048D, (ushort)product).FeatureLength);
        }

        /// <summary>color_scaling (ite_8291_lb.c:102-119).</summary>
        [Fact]
        public void ALightbarsGreenAndBlue_AreScaledOnTheMachinesThatNeedIt()
        {
            Assert.Equal(((byte)100, (byte)49), PluginDeviceLighting.LightbarScaled(0x6010, "STEPOL1XA04", "", 255, 127));
            Assert.Equal(((byte)100, (byte)49), PluginDeviceLighting.LightbarScaled(0x6010, "STELLARIS1XI05", "", 255, 127));
            Assert.Equal(((byte)100, (byte)49), PluginDeviceLighting.LightbarScaled(0x6010, "STELLARIS17I06", "", 255, 127));
            Assert.Equal(((byte)255, (byte)127), PluginDeviceLighting.LightbarScaled(0x7000, "STEPOL1XA04", "", 255, 127));
            Assert.Equal(((byte)200, (byte)127), PluginDeviceLighting.LightbarScaled(0x7000, "", "XxAF5xxx", 255, 127));
            Assert.Equal(((byte)255, (byte)127), PluginDeviceLighting.LightbarScaled(0x6010, "", "", 255, 127));
        }

        /// <summary>ite_8297.c:58-65, and only on TUXEDO's machines, since Gigabyte's motherboards
        /// use the same chip and IDs.</summary>
        [Fact]
        public void The8297_IsOneReport_AndOnlyOnTuxedosMachines()
        {
            var row = Row(0x048D, 0x8297);
            Assert.Equal("CC B0 01 01 11 22 33", First(row, 0x112233, 7));
            Assert.Equal(64, row.FeatureLength);
            Assert.NotNull(row.OnlyWhen);
            Assert.False(row.OnlyWhen());
            Assert.Null(PluginDeviceLighting.Instance.Match(Collection(0x048D, 0x8297, feature: 64)));
            Assert.True(PluginDeviceLighting.IsTuxedo("TUXEDO", "InfinityBook"));
            Assert.True(PluginDeviceLighting.IsTuxedo("Notebook", "TUXEDO Stellaris"));
            Assert.False(PluginDeviceLighting.IsTuxedo("Gigabyte Technology Co., Ltd.", "X570 AORUS"));
            Assert.False(PluginDeviceLighting.IsTuxedo(null, null));
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
            Assert.Equal("02 03 01 FF FF FF 11 22 33 00 00 00", Hex(out12.First(f => f[1] == 0x03), 12));
            Assert.Equal("02 04", Hex(out12[^2], 2));
            Assert.Equal("02 05", Hex(out12[^1], 2));
            Assert.Equal("02 03 01 FF FF FF 12 30 00", Hex(AlienFxLighting.V1Color(AlienFxLighting.Api.V2, 0x112233), 9));
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

        /// <summary>A session's opening reports go out once, before its first picture.</summary>
        [Fact]
        public void AReportsSession_OpensOnce()
        {
            var io = new FakeReportChannel { OutputReportLength = 2 };
            var session = new ReportsSession(false, 1, colors => new[] { new byte[] { 2, (byte)colors[0] } }, new[] { new byte[] { 1, 0 } });
            var light = new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } };
            session.Paint(io, _ => { }, light, new[] { 7 });
            session.Paint(io, _ => { }, light, new[] { 8 });
            Assert.Equal(new byte[] { 1, 2, 2 }, io.Calls.Select(c => c.Bytes[0]).ToArray());
        }

        /// <summary>A channel with no input reports waits the time the device needs instead of asking.</summary>
        [Theory]
        [InlineData(AlienFxLighting.Api.V2)]
        [InlineData(AlienFxLighting.Api.V3)]
        [InlineData(AlienFxLighting.Api.V4)]
        internal void TheLaptopController_WaitsUntilItIsReady(AlienFxLighting.Api api)
        {
            var waits = new List<int>();
            var io = new FakeReportChannel();
            Assert.Equal(HidLightingPaint.Sent, new AlienFxLighting.Session(api).Paint(io, waits.Add, null, new[] { 0x112233 }));
            Assert.NotEmpty(waits);
            Assert.All(waits, ms => Assert.True(ms > 0));
        }

        /// <summary>The V7 mouse answers each light, and each answer is read before the next.</summary>
        [Fact]
        public void TheV7Mouse_ReadsAnAnswerForEachLight()
        {
            var io = new FakeReportChannel { InputReportLength = 65 };
            Assert.Equal(HidLightingPaint.Sent, new AlienFxLighting.Session(AlienFxLighting.Api.V7).Paint(io, _ => { }, null, new[] { 0x112233 }));
            var kinds = io.Calls.Select(c => c.Kind).ToList();
            Assert.True(kinds.Count >= 2);
            for (int i = 0; i < kinds.Count; i += 2)
            {
                Assert.Equal("out", kinds[i]);
                Assert.Equal("in", kinds[i + 1]);
            }
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
            var waits = new List<int>();
            new AlienFxLighting.Session(AlienFxLighting.Api.V8).Paint(io, waits.Add, null, new[] { 0x112233 });
            Assert.Equal(new[] { 4, 6 }, waits);
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
            var families = HidLightingFamilies.All.ToList();
            Assert.True(families.IndexOf(AlienFxLighting.Instance) > families.IndexOf(PluginDeviceLighting.Instance));
            var dell = new HidCollectionInfo { Path = "fake", VendorId = 0x187C, ProductId = 0x0550, FeatureReportLength = 34, OutputReportLength = 34 };
            Assert.Same(AlienwareLighting.Instance, HidLightingFamilies.Match(dell, HidLightingFamilies.All).Family);
        }
    }
}
