using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;
using F = PadForge.Common.Input.Peripherals.PluginDeviceLighting;

namespace PadForge.Tests
{
    /// <summary>The devices SignalRGB's plugins name on their beta branch and
    /// OpenRGB does not (#557). Every expected byte is worked out here from the
    /// plugin named beside it.</summary>
    [Collection("PeripheralOutputStatics")]
    public class PluginDevicesBetaTests
    {
        private static PluginDevice Device(ushort vid, ushort pid, int page = -1)
            => F.Devices.Single(d => d.VendorId == vid && d.ProductId == pid && (page < 0 || d.UsagePage == page));

        private static string Short(byte[] frame)
        {
            int end = frame.Length;
            while (end > 0 && frame[end - 1] == 0) end--;
            return Hex(frame, end);
        }

        private static string[] Steps(IEnumerable<PluginStep> steps) => steps.Select(s => s.Kind switch
        {
            PluginStepKind.Output => "out " + Short(s.Bytes),
            PluginStepKind.Feature => "set " + Short(s.Bytes),
            PluginStepKind.ReadFeature => "get " + s.Value.ToString("X2"),
            PluginStepKind.ReadInput => "read",
            _ => "wait " + s.Value,
        }).ToArray();

        private static int[] Colors(int slots, params (int At, int Color)[] set)
        {
            var colors = new int[slots];
            foreach (var (at, color) in set) colors[at] = color;
            return colors;
        }

        private static HidCollectionInfo CollectionOf(PluginDevice d)
        {
            int number = d.Interface == OpenRgbRegistration.Any ? 0 : d.Interface;
            return new HidCollectionInfo
            {
                Path = $@"\\?\hid#vid_{d.VendorId:x4}&pid_{d.ProductId:x4}&mi_{number:x2}#7&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
                VendorId = d.VendorId,
                ProductId = d.ProductId,
                InterfaceNumber = number,
                UsagePage = (ushort)(d.UsagePage == OpenRgbRegistration.Any ? 0xFF00 : d.UsagePage),
                Usage = (ushort)(d.Usage == OpenRgbRegistration.Any ? 0x01 : d.Usage),
                OutputReportLength = (ushort)d.OutputLength,
                FeatureReportLength = (ushort)d.FeatureLength,
                InputReportLength = 64,
            };
        }

        /// <summary>A channel that gives these answers to its reads in turn.</summary>
        private static FakeReportChannel Answering(string product = null, params byte[][] answers)
        {
            var queue = new Queue<byte[]>(answers);
            return new FakeReportChannel
            {
                OutputReportLength = 65,
                InputReportLength = 65,
                FeatureReportLength = 65,
                ProductString = product,
                Input = () => queue.Count > 0 ? queue.Dequeue() : null,
            };
        }

        private static byte[] Text(string text)
        {
            var answer = new byte[65];
            Encoding.ASCII.GetBytes(text).CopyTo(answer, 0);
            return answer;
        }

        // ── Turtle Beach ──

        [Fact]
        public void TheTurtleBeachKoneXpAirs_TakeTheRoccatFrames()
        {
            var dock = Device(0x10F5, 0x5019);
            Assert.Equal(new[] { "set 03 01 02 03 FF 00 00 00 FF 00 00 00 FF 00 00 00 FF" }, Steps(dock.Picture(Colors(4, (0, 0x010203)))));
            Assert.Equal(new[] { 0x02, 0x00, 0x09 }, dock.Start[0].Bytes.Take(3).Select(b => (int)b));
            Assert.Equal("set 01 FF FF", Steps(dock.Start)[1]);
            var wired = Device(0x10F5, 0x5015);
            Assert.Equal("set 06 00 4D 06 15 6D 70 61 01 02 03", Steps(wired.Picture(Colors(5, (0, 0x010203))))[0]);
            var wireless = Device(0x10F5, 0x5017);
            Assert.Equal("set 06 01 4D 06 15 00 00 00 01 02 03", Steps(wireless.Picture(Colors(5, (0, 0x010203))))[0]);
            Assert.Equal("set 06 01 13 07 02", Steps(wireless.Start)[0]);
        }

        [Theory]
        [InlineData(0x1E7D, 0x6978)]
        [InlineData(0x1E7D, 0x3AA9)]
        [InlineData(0x10F5, 0x501E)]
        [InlineData(0x10F5, 0x5021)]
        public void TheVulcanIIMiniAir_SendsFiveReportsOfZoneOne_ThenTwoOfZoneTwo(int vid, int pid)
        {
            var d = Device((ushort)vid, (ushort)pid);
            var frames = d.Picture(Colors(F.VulcanMiniAirSlots, (0, 0x010203), (75, 0x040506))).Where(s => s.Kind == PluginStepKind.Output).ToArray();
            Assert.Equal(7, frames.Length);
            Assert.Equal("00 07 31 33 01 00 01 02 03", Short(frames[0].Bytes));
            Assert.Equal("00 07 31 33 02 00 04 05 06", Short(frames[5].Bytes));
            Assert.Equal("00 07 31 33 02 01", Short(frames[6].Bytes));
            Assert.Equal((1, 0x00FF, 1), (d.Interface, d.UsagePage, d.Usage));
        }

        [Fact]
        public void TheVulcanIIMiniAir_ASecondLed_LightsWithItsKey()
        {
            var lights = Device(0x10F5, 0x501E).Lights;
            Assert.True(lights.Count(l => l.Usage == 0x1E) == 2, "the 1 key has two LEDs");
        }

        // ── A4Tech, Alienware, ASUS, HyperX ──

        [Theory]
        [InlineData(0x1CCA)]
        [InlineData(0x11A1)]
        [InlineData(0x7452)]
        public void TheBloodyA70x_TakesOpenRgbsReports_ToEightLeds(int pid)
        {
            var d = Device(0x09DA, (ushort)pid);
            Assert.Equal(new[] { "set 07 03 06 01", "set 07 03 06 00 00 00 00 00 01" }, Steps(d.Start));
            Assert.Equal(new[] { "set 07 03 06 01", "set 07 03 06" }, Steps(d.HandBack));
            // LED 0 at byte 8 and LED 7 at byte 29.
            var frame = Assert.Single(d.Picture(Colors(8, (0, 0x010203), (7, 0x040506)))).Bytes;
            Assert.Equal("07 03 06 02 00 00 00 00 01 02 03", Hex(frame, 11));
            Assert.Equal("04 05 06", Hex(frame.Skip(29).ToArray(), 3));
            Assert.Equal(A4TechLighting.PacketSize, frame.Length);
            Assert.Equal((2, 0xFF32, 0x04C0), (d.Interface, d.UsagePage, d.Usage));
        }

        [Fact]
        public void TheAw558_ThreeColors_BetweenItsFixedBytes()
        {
            var d = Device(0x04CA, 0x00A5);
            Assert.Equal("out 00 0D 5A 03 00 01 02 03 04 05 06 07 08 09 A5", Steps(d.Picture(new[] { 0x010203, 0x040506, 0x070809 }))[0]);
            Assert.Equal("out 0D 5A 21 02 00 00 00 00 00 00 00 00 00 A5", Steps(d.Start)[0]);
        }

        [Fact]
        public void TheRogSpotlight_OneColorInTwoReports()
        {
            Assert.Equal(new[]
            {
                "out EC 3B 00 FF 01 02 03",
                "out EC 40 00 EE 01 01 02 03 00 00 84 F3 12 03 70 12 3B 75 47 A1",
                "wait 1",
            }, Steps(Device(0x0B05, 0x1881).Picture(new[] { 0x010203 })));
        }

        [Theory]
        [InlineData(0x0B97, 0xFF00, 0x0001)]
        [InlineData(0x0D97, 0xFF00, 0x0001)]
        [InlineData(0x0F98, 0xFF00, 0x0001)]
        [InlineData(0x0DA0, 0xFF90, 0xFF00)]
        [InlineData(0x0BA0, 0xFF90, 0xFF00)]
        public void TheHaste2_PrimesThenColors(int pid, int page, int usage)
        {
            var d = Device(0x03F0, (ushort)pid);
            Assert.Equal(new[] { "out 44 01 01", "out 44 02 00 00 01 02 03" }, Steps(d.Picture(new[] { 0x010203 })));
            Assert.Equal((2, page, usage), (d.Interface, d.UsagePage, d.Usage));
        }

        [Theory]
        [InlineData(0x048E)]
        [InlineData(0x028E)]
        public void TheHasteWireless_TakesNewerFirmwaresFrames_OnInterfaceTwo(int pid)
        {
            var d = F.Devices.Single(x => x.VendorId == 0x03F0 && x.ProductId == pid && x.Interface == 2);
            Assert.Equal(new[] { "out 00 46", "out 00 57 12 01", "out 00 51" }, Steps(d.Start));
            Assert.Equal(new[] { "out 00 D2 00 00 08 01 02 03 04 05 06 64" }, Steps(d.Picture(new[] { 0x010203, 0x040506 })));
            Assert.NotNull(F.Instance.Match(CollectionOf(d)));
            Assert.Same(F.Instance.Match(CollectionOf(d)), F.Instance.Match(CollectionOf(d)));
        }

        // ── Desks and mats ──

        [Fact]
        public void TheLevel20Desk_TakesGreenFirst_InOneLongReport()
        {
            var d = Device(0x264A, 0x07D1);
            var frame = Assert.Single(d.Picture(Colors(d.Slots, (1, 0x010203)))).Bytes;
            Assert.Equal(F.Level20ReportLength, frame.Length);
            Assert.Equal("00 32 52 04 24 00 00 00 02 01 03", Hex(frame, 11));
            Assert.Equal("out 00 FE 33", Steps(d.Start)[0]);
            Assert.Equal(d.Slots, d.Lights.Count);
        }

        [Fact]
        public void TheDasher_EachLightItsOwnColor()
        {
            var steps = Steps(Device(0x264A, 0x9008).Picture(new[] { 0x010203, 0x040506, 0, 0 }));
            Assert.Equal("set 0C C4 03 01 00 01 02 03", steps[0]);
            Assert.Equal("set 0C C4 03 01 01 04 05 06", steps[1]);
            Assert.Equal("set 0C C4 03 01 03", steps[3]);
            Assert.True(Device(0x264A, 0x9008).HeldByWindows);
        }

        // ── Cooler Master, OEM, Royuan ──

        [Fact]
        public void TheCk720_EighteenLedsToAReport()
        {
            var d = Device(0x2516, 0x016B);
            Assert.Equal(new[] { "out 00 80 01 01 02 02", "out 00 01 81 C8 00 01 00 12" }, Steps(d.Start));
            Assert.Equal(Steps(d.Start), Steps(Device(0x2516, 0x016D).Start));
            var frames = Steps(d.Picture(Colors(d.Slots, (0, 0x010203), (18, 0x040506))));
            Assert.Equal("out 00 56 42 00 00 02 12 00 00 01 02 03", frames[0]);
            Assert.Equal("out 00 56 42 00 00 02 12 12 00 04 05 06", frames[1]);
            Assert.All(d.Lights, l => Assert.InRange(l.Address, 0, d.Slots - 1));
            Assert.Contains(d.Lights, l => l.Usage == 0x29);
        }

        [Fact]
        public void TheOemKeyboard_TakesTheSumOfItsBytes()
        {
            Assert.Equal("out 13 88 01 00 23 01 02 03 00 00 00 00 00 00 00 00 00 00 00 C5",
                Steps(Device(0x3554, 0xFA09).Picture(new[] { 0x010203 }))[0]);
        }

        [Fact]
        public void TheRoyuan4002_TakesTheCheckByteOfItsColors()
        {
            var d = Device(0x3151, 0x4002);
            Assert.Equal("set 00 0E 01 02 03 00 00 00 EB", Steps(d.Picture(new[] { 0x010203 }))[0]);
            Assert.Equal("set 00 07 15 04 04 07 00 00 00 D4", Steps(d.Start)[0]);
            Assert.True(d.HeldByWindows);
        }

        // ── Boards told apart by a name ──

        [Fact]
        public void Sonix_TakesItsLayoutFromTheProductString_AndAnUnknownOneStaysDark()
        {
            var model = F.Instance.Match(CollectionOf(Device(0x0C45, 0x8006)));
            var session = model.Begin(Answering("AK820"), _ => { });
            Assert.NotNull(session);
            var board = PluginLibraryTables.Sonix2.Single(b => b.Model == "AK820").Table();
            Assert.Equal(board.Indexes.Length, session.Map.Lights.Count);
            Assert.Null(model.Begin(Answering("SOMETHING ELSE"), _ => { }));
        }

        [Fact]
        public void Sonix_EachLedItsNumberAndColor_BetweenTheOpeningAndClosing()
        {
            Assert.Equal(new[]
            {
                "set 00 04 20 00 00 00 00 00 00 08",
                "get 00",
                "set 00 00 01 02 03 01 04 05 06",
                "wait 2",
                "set",
                "set 00 04 02",
                "get 00",
            }, Steps(F.Sonix2Picture(new[] { 0, 1 }, new[] { 0x010203, 0x040506 })).Select(s => s.TrimEnd()));
        }

        /// <summary>An LED the layout does not have stays zero, as in the plugin's array.</summary>
        [Fact]
        public void Sonix2_AnLedTheLayoutLacks_StaysZero()
        {
            var steps = Steps(F.Sonix2Picture(new[] { 0, 2 }, new[] { 0x010203, 0, 0x040506 }));
            Assert.Contains(steps, s => s.StartsWith("set 00 00 01 02 03 00 00 00 00 02 04 05 06"));
        }

        [Fact]
        public void EVision_TheChecksumChangesAtTheSixthReport()
        {
            var one = Steps(F.EVisionPicture(new[] { 0x010203 }));
            // 1 + 2 + 3 + 74, and 56 bytes in the size byte.
            Assert.Equal("out 04 50 00 12 38 00 00 00 01 02 03", one[0]);
            var frames = F.EVisionPicture(new int[112]).Where(s => s.Kind == PluginStepKind.Output).ToArray();
            Assert.Equal(6, frames.Length);
            // The second report: 0 + 56 + 74, 56 bytes sent.
            Assert.Equal("04 82 00 12 38 38", Short(frames[1].Bytes));
            // The sixth: 0 + 99, 280 bytes sent.
            Assert.Equal("04 63 00 12 38 18 01", Short(frames[5].Bytes));
        }

        [Fact]
        public void ByTech_OneColor_TakesAllItsLeds_WithTheCheckByte()
        {
            var colors = Colors(256, (5, 0x010203), (6, 0x010203));
            var frame = F.ByTechPicture(new[] { 5, 6 }, colors).First().Bytes;
            Assert.Equal("09 08 01 00 01 00 06 01 02 03 02 05 06", Hex(frame, 13));
            Assert.Equal(0xD3, frame[63]);
        }

        [Fact]
        public void ByTech_MoreThanFortyColors_MergeCloseOnes_AndStopAtForty()
        {
            var leds = Enumerable.Range(0, 60).ToArray();
            // Sixty colors at least sixteen apart: nothing merges, and forty stay.
            var colors = new int[256];
            for (int i = 0; i < 60; i++) colors[i] = (i % 16 * 16) << 16 | (i / 16 * 64) << 8;
            int Groups(int[] picture)
            {
                var data = new List<byte>();
                foreach (var step in F.ByTechPicture(leds, picture).Where(s => s.Kind == PluginStepKind.Output))
                    data.AddRange(step.Bytes.Skip(7).Take(step.Bytes[6]));
                int groups = 0;
                for (int at = 0; at < data.Count; at += 4 + data[at + 3]) groups++;
                return groups;
            }
            Assert.Equal(40, Groups(colors));
            // Close colors merge, so far fewer than sixty come out.
            for (int i = 0; i < 60; i++) colors[i] = (i % 45) << 16;
            Assert.InRange(Groups(colors), 1, 10);
        }

        [Fact]
        public void Nuvoton_TakesTheFifthFieldOfTheFirmwaresAnswer()
        {
            Assert.Equal("XS61BRGB", F.NuvotonModel(Answering(null, Text("\u0001a,b,c,d,XS61BRGB,1.2")), 0x01));
            Assert.Null(F.NuvotonModel(Answering(), 0x01));
            var model = F.Instance.Match(CollectionOf(Device(0x0416, 0x7372)));
            Assert.NotNull(model.Begin(Answering(null, Text("\u0001a,b,c,d,XS61BRGB,1.2")), _ => { }));
            Assert.Null(model.Begin(Answering(null, Text("\u0001a,b,c,d,NOBODY,1.2")), _ => { }));
        }

        [Fact]
        public void Nuvoton_AnOneZoneBoard_ClosesWithABlankSecondZone()
        {
            var steps = Steps(F.NuvotonPicture(0x01, new[] { 19 }, Colors(512, (0, 0x010203), (18, 0x040506)), true, new[]
            {
                PluginStep.Output(F.Frame(65, 0xAA)),
            }));
            Assert.Equal("out 01 0F 00 00 00 36 01 02 03", steps[0]);
            Assert.Equal("wait 1", steps[1]);
            Assert.Equal("out 01 0F 00 00 01 03 04 05 06", steps[2]);
            Assert.Equal("out AA", steps[^1]);
        }

        [Fact]
        public void Nuvoton_TwoZones_ReadTheirOwnColors()
        {
            var steps = Steps(F.NuvotonPicture(0x06, new[] { 1, 1 }, Colors(512, (0, 0x010203), (256, 0x040506)), false, Array.Empty<PluginStep>()));
            Assert.Equal(new[] { "out 06 0F 00 00 00 03 01 02 03", "out 06 0F 01 00 00 03 04 05 06" }, steps);
        }

        [Theory]
        [InlineData(0xE987, "MK69_JLRGB")]
        [InlineData(0xE989, "JLMK60V2RGB")]
        public void Kbm_AppliesWithOneReport_AfterItsThreeStarts(int pid, string model)
        {
            var d = Device(0x4A4C, (ushort)pid);
            Assert.Equal("out 01 07 00 00 00 0E 00 04 03 FF", Steps(d.Start)[0]);
            Assert.Equal(3, d.Start.Count);
            Assert.Equal("out 01 0F 01 00 02 1B", Steps(d.Picture(new int[512]))[^1]);
            var board = PluginLibraryTables.Kbm.Single(b => b.Model == model).Table();
            Assert.Equal(board.Indexes.Length, d.Lights.Count);
        }

        [Fact]
        public void Xiaohua_LeavesOutTheFirstStart_AndUsesItsReportId()
        {
            var d = Device(0x2E88, 0xB30A);
            Assert.Equal(new[] { "out 06 17 00 00 00 01 01", "out 06 08 00 00 00 0D 02 03 03 FF 00 00 00 00 00 00 01" }, Steps(d.Start));
            var session = F.Instance.Match(CollectionOf(d)).Begin(Answering(null, Text("\u0006a,b,c,d,XS63RGB,1")), _ => { });
            Assert.NotNull(session);
        }

        [Fact]
        public void Nuvoton2_FiveLedsNamedFirst_BetweenItsFixedReports()
        {
            var steps = Steps(F.Nuvoton2Picture(new[] { 7, 8 }, Colors(256, (7, 0x010203), (8, 0x040506))));
            Assert.Equal("out 06 BE 15 00 01 01 0E 05 06 00 00 00 FF", steps[0]);
            Assert.Equal("out 06 BE 19 00 01 01 0E 07 08 00 00 00 01 02 03 00 04 05 06", steps[1]);
            Assert.Equal("wait 1", steps[2]);
            Assert.Equal("out 06 BE 15 00 02 01 01 05 09 00 00 00 FF 00 00 14 FF 00 00 01", steps[3]);
            var answer = new byte[33];
            Encoding.ASCII.GetBytes("WKB-NUC-NP87B-US").CopyTo(answer, 4);
            var model = F.Instance.Match(CollectionOf(Device(0x0416, 0xA0F8)));
            var io = new FakeReportChannel { OutputReportLength = 33, InputReportLength = 33, Input = () => answer };
            Assert.NotNull(model.Begin(io, _ => { }));
        }

        [Fact]
        public void Sinowealth2_AsksItsModelOnTheCollectionBeside_AndColorsAtItsLeds()
        {
            var d = Device(0x258A, 0x0049);
            var model = F.Instance.Match(CollectionOf(d));
            var answer = new byte[8];
            answer[7] = 13;
            var query = new FakeReportChannel { FeatureReportLength = 8, Feature = _ => answer };
            var session = model.BeginWith(new FakeReportChannel { FeatureReportLength = F.Sinowealth2Length }, new IHidReportChannel[] { query }, _ => { });
            Assert.NotNull(session);
            Assert.Contains(query.Calls, c => c.Kind == "set" && Short(c.Bytes) == "05 05 81");
            answer[7] = 200;
            answer[6] = 200;
            Assert.Null(model.BeginWith(new FakeReportChannel { FeatureReportLength = F.Sinowealth2Length }, new IHidReportChannel[] { query }, _ => { }));
            var frame = F.Sinowealth2Picture(Colors(256, (2, 0x010203))).First().Bytes;
            Assert.Equal("08 0A 7A 01 00 00 00 00 00 00 01 02 03", Hex(frame, 13));
            Assert.Equal(382, frame.Length);
        }

        [Fact]
        public void TheSinowealthSibling_IsTheCollectionWithShortFeatureReports()
        {
            var sibling = Device(0x258A, 0x0049).Siblings[0];
            var unit = CollectionOf(Device(0x258A, 0x0049));
            var query = unit with { Path = unit.Path.Replace("0000#", "0001#"), FeatureReportLength = 8 };
            Assert.True(sibling.Is(unit, query));
            Assert.False(sibling.Is(unit, query with { FeatureReportLength = F.Sinowealth2Length }));
        }

        // ── ASUS ──

        [Theory]
        [InlineData(0x18F6)]
        [InlineData(0x1A55)]
        [InlineData(0x1A64)]
        [InlineData(0x1B04)]
        [InlineData(0x1B7E)]
        public void AnAsusKeyboard_HandsBackItsOwnEffect(int pid)
        {
            var d = Device(0x0B05, (ushort)pid);
            Assert.NotEmpty(d.Lights);
            Assert.Equal("00 51 2C 04 00 48 64", Hex(d.HandBack[0].Bytes, 7));
            Assert.Contains(d.Lights, l => l.Usage == 0x29);
        }

        [Fact]
        public void TheAsusKeyboards_FifteenLeds_CountingWhatIsLeft()
        {
            var leds = Enumerable.Range(0, 16).Select(i => i + 1).ToArray();
            var steps = Steps(F.AsusKeyboardPicture(leds, Colors(256, (1, 0x010203), (16, 0x040506))));
            Assert.StartsWith("out 00 C0 81 10 00 01 01 02 03 02", steps[0]);
            Assert.Equal("out 00 C0 81 01 00 10 04 05 06", steps[1]);
        }

        [Fact]
        public void TheAsusMice_ModernAndLegacy()
        {
            var ace = Device(0x0B05, 0x1A94);
            Assert.Equal("out 00 51 28 03 00 02 64 02", Steps(ace.Start)[0]);
            Assert.Equal(new[] { "out 00 51 29 FF 00 00 01 02 03" }, Steps(ace.Picture(new[] { 0x010203 })));
            var keris = Device(0x0B05, 0x1C0C);
            Assert.Empty(keris.Start);
            Assert.Equal("out 00 51 28 01 00 00 64 01 02 03", Steps(keris.Picture(new[] { 0, 0x010203, 0 }))[2]);
        }

        [Theory]
        [InlineData(0x186E, 3)]
        [InlineData(0x1AC5, 1)]
        [InlineData(0x1941, 5)]
        [InlineData(0x18D4, 5)]
        public void TheAsusMonitors_RedBlueGreen_ByLed(int pid, int lights)
        {
            var d = Device(0x0B05, (ushort)pid);
            Assert.Equal(lights, d.Lights.Count);
            var colors = new int[lights];
            colors[^1] = 0x010203;
            var steps = Steps(d.Picture(colors));
            Assert.Equal("set 03 02 A1 80 20 01", steps[0]);
            Assert.Equal("set 03 02 A1 80 A0 01", steps[2]);
            int first = (lights - 1) * 3;
            Assert.Equal($"set 03 02 A1 80 {first:X2} 01", steps[3 + first]);
            Assert.Equal($"set 03 02 A1 80 {first + 1:X2} 03", steps[4 + first]);
            Assert.Equal($"set 03 02 A1 80 {first + 2:X2} 02", steps[5 + first]);
        }

        [Fact]
        public void TheOmniDongle_AsksWhatIsConnected_ThenItsSerial()
        {
            var keyboard = Device(0x0B05, 0x1ACE, 0xFF00);
            var connected = new byte[65];
            connected[5] = 1;
            var serial = new byte[65];
            Encoding.ASCII.GetBytes("A23114802919").CopyTo(serial, 5);
            var model = F.Instance.Match(CollectionOf(keyboard));
            Assert.NotNull(model.Begin(Answering(null, connected, serial), _ => { }));
            Assert.Null(model.Begin(Answering(null, new byte[65], new byte[65], new byte[65], new byte[65]), _ => { }));
            // A serial no library lists still takes the keys' shared numbers.
            Assert.NotEmpty(model.Begin(Answering(null, connected, Text("nobody")), _ => { }).Map.Lights);
            var mouse = Device(0x0B05, 0x1ACE, 0xFF01);
            Assert.Single(F.Instance.Match(CollectionOf(mouse)).Begin(Answering(null, connected, Text("nobody")), _ => { }).Map.Lights);
            var mouseSerial = new byte[65];
            Encoding.ASCII.GetBytes("SAMPGDD00EAH").CopyTo(mouseSerial, 5);
            var session = F.Instance.Match(CollectionOf(mouse)).Begin(Answering(null, connected, mouseSerial), _ => { });
            Assert.Equal(3, session.Map.Lights.Count);
        }

        [Fact]
        public void TheOmniFrames_CarryTheirReportIds()
        {
            var leds = Enumerable.Range(0, 15).ToArray();
            var steps = Steps(F.OmniKeyboardPicture(leds, Colors(256, (14, 0x010203))));
            Assert.StartsWith("out 02 C0 81 0E 00", steps[0]);
            Assert.Equal("out 02 C0 81 01 00 0E 01 02 03", steps[1]);
            Assert.Equal(new[] { "out 03 51 29 FF 00 00 01 02 03" }, Steps(F.OmniMousePicture(new[] { 0x010203 })));
        }

        // ── Alienware monitors ──

        [Theory]
        [InlineData(0x274A)]
        [InlineData(0x274B)]
        [InlineData(0x274C)]
        public void AlienwaresFirstMonitors_EachLedItsBit_InAFrameOfFF(int pid)
        {
            var d = Device(0x0424, (ushort)pid);
            var start = d.Start[0].Bytes;
            Assert.Equal("FF 95 00 00 00 FF", Hex(start, 6));
            Assert.Equal(0xFF, start[64]);
            var frames = d.Picture(new[] { 0x010203, 0, 0, 0x040506 }).ToArray();
            Assert.Equal(4, frames.Length);
            Assert.Equal("FF 92 48 05 00 04 01 01 02 03 FF", Hex(frames[0].Bytes, 11));
            Assert.Equal("FF 92 48 05 00 04 08 04 05 06 FF", Hex(frames[3].Bytes, 11));
        }

        [Fact]
        public void TheAw2725df_HasNoLightAtTheBitItLacks()
        {
            var d = Device(0x187C, 0x1014);
            Assert.Equal(new[] { 0, 1, 2 }, d.Lights.Select(l => l.Address));
            var bits = d.Picture(new[] { 1, 2, 3 }).Select(s => s.Bytes[9]);
            Assert.Equal(new byte[] { 1, 2, 8 }, bits);
        }

        // ── SteelSeries ──

        [Theory]
        [InlineData(0x113A, 1)]
        [InlineData(0x113C, 0)]
        public void TheMsiLaptops_OneReport_ThenApply(int pid, int number)
        {
            var d = Device(0x1038, (ushort)pid);
            Assert.Equal(number, d.Interface);
            Assert.Equal("set 00 4B", Steps(d.Start)[0]);
            int led = PluginLibraryTables.SteelSeriesLaptop[0].Table().Indexes[0];
            var steps = F.SteelSeriesLaptopPicture(new[] { led }, Colors(256, (led, 0x010203))).ToArray();
            Assert.Equal("00 0E 00 00 00", Hex(steps[0].Bytes, 5));
            Assert.Equal("01 02 03", Hex(steps[0].Bytes.Skip(5 + led * 3).ToArray(), 3));
            Assert.Equal("00 0D 00 02", Short(steps[2].Bytes));
        }

        [Fact]
        public void TheApexProMiniGen3_NamesEachLedWithItsColor()
        {
            var d = Device(0x1038, 0x1648);
            Assert.Equal(1, d.Interface);
            var frame = d.Picture(Colors(256, (41, 0x010203))).First().Bytes;
            Assert.Equal(0x40, frame[1]);
            Assert.Equal(d.Lights.Count, frame[2]);
            Assert.Equal("29 01 02 03", Hex(frame.Skip(3).ToArray(), 4));
        }

        [Theory]
        [InlineData(0x0617)]
        [InlineData(0x0619)]
        public void TheApexM750Tkl_BothProducts(int pid)
        {
            Assert.Equal(PluginKeyTables.ApexM750Tkl.Plugin, Device(0x1038, (ushort)pid).Plugin);
        }

        // ── NZXT ──

        private static byte[] Version(byte major, byte minor, byte patch)
        {
            var answer = new byte[65];
            answer[3] = major;
            answer[4] = minor;
            answer[5] = patch;
            return answer;
        }

        [Theory]
        [InlineData(1, 3, 71, true)]
        [InlineData(1, 4, 0, true)]
        [InlineData(2, 0, 0, true)]
        [InlineData(1, 3, 70, false)]
        [InlineData(1, 2, 200, false)]
        public void TheFunctionKeyboards_FirmwarePicksTheFrames(int major, int minor, int patch, bool isNew)
        {
            var io = Answering(null, new byte[65], Version((byte)major, (byte)minor, (byte)patch));
            Assert.Equal(isNew, F.FunctionFirmwareIsNew(io));
            Assert.Equal("43 81 00 01", Short(io.Calls.First().Bytes));
        }

        /// <summary>The row's session takes the frames its firmware's version names.</summary>
        [Theory]
        [InlineData(1, 3, 71, 1)]
        [InlineData(1, 3, 70, 2)]
        public void TheFunctionKeyboards_TheirVersion_PicksThePicture(int major, int minor, int patch, int frames)
        {
            var row = F.Devices.First(d => d.Plugin.EndsWith("NZXT_Function_Keyboard.js"));
            var variant = row.AskVariant(Answering(null, new byte[65], Version((byte)major, (byte)minor, (byte)patch)), Array.Empty<IHidReportChannel>(), _ => { });
            int sent = variant.Picture(new int[variant.Slots]).Count(s => s.Bytes != null && s.Kind == PluginStepKind.Output);
            if (frames == 1) Assert.Equal(F.FunctionNewPicture(new int[10]).Count(s => s.Bytes != null), sent);
            else Assert.Equal(F.FunctionPicture(new int[10], false, false).Count(s => s.Bytes != null), sent);
        }

        [Fact]
        public void TheFunctionKeyboards_ASilentOne_KeepsTheOlderFrames()
        {
            Assert.False(F.FunctionFirmwareIsNew(Answering()));
        }

        [Fact]
        public void TheFunctionKeyboards_NewFrames_TenZonesInOneReport()
        {
            var steps = F.FunctionNewPicture(Colors(10, (0, 0x010203), (1, 0x040506))).ToArray();
            Assert.Equal("43 BD 01 10 02", Hex(steps[0].Bytes, 5));
            Assert.All(steps[0].Bytes.Skip(5).Take(18), b => Assert.Equal(0xFF, b));
            Assert.Equal("00 0A 01 02 03 00 04 05 06 00", Hex(steps[0].Bytes.Skip(23).ToArray(), 10));
            Assert.Equal(64, steps[0].Bytes.Length);
            Assert.Equal("43 01", Short(steps[1].Bytes));
        }

        [Theory]
        [InlineData(0x2130)]
        [InlineData(0x2131)]
        [InlineData(0x2136)]
        public void TheFunction2_IsAFunction(int pid)
        {
            var d = Device(0x1E71, (ushort)pid);
            Assert.Equal(10, d.Lights.Count);
            Assert.Equal(4, d.Start.Count);
            Assert.Equal("out 43 82 00 41 64", Steps(d.Start)[2]);
        }

        // ── Products that take another product's frames ──

        private static HidCollectionInfo RegisteredCollection(string family, ushort product, ushort vendor, ushort asProduct, ushort output = 65)
        {
            var r = OpenRgbRegistrations.Of(family).First(x => x.ProductId == product);
            var known = CollectionFor(r, output: output, feature: 65, input: 65);
            return known with
            {
                VendorId = vendor,
                ProductId = asProduct,
                Path = known.Path.Replace($"vid_{known.VendorId:x4}", $"vid_{vendor:x4}").Replace($"pid_{product:x4}", $"pid_{asProduct:x4}"),
            };
        }

        [Fact]
        public void TheTurtleBeachVulcanIIMax_IsTheRoccatBoard()
        {
            var model = RoccatVulcanLighting.Instance.Match(RegisteredCollection(RoccatVulcanLighting.FamilyName, RoccatVulcanLighting.VulcanIIMax,
                RoccatVulcanLighting.TurtleBeachVendorId, RoccatVulcanLighting.TurtleBeachVulcanIIMax));
            Assert.NotNull(model);
            Assert.Equal(RoccatVulcanLighting.Product(RoccatVulcanLighting.RoccatVendorId, RoccatVulcanLighting.VulcanIIMax), model.Map.Product);
            // No other Turtle Beach product is taken for it.
            Assert.Null(RoccatVulcanLighting.Instance.Match(RegisteredCollection(RoccatVulcanLighting.FamilyName, RoccatVulcanLighting.VulcanIIMax,
                RoccatVulcanLighting.TurtleBeachVendorId, 0x501D)));
        }

        [Fact]
        public void TheAimoPadWide_IsTheSenseAimo()
        {
            var model = RoccatLighting.Instance.Match(RegisteredCollection(RoccatLighting.FamilyName, RoccatLighting.SenseAimoMid,
                RoccatVulcanLighting.TurtleBeachVendorId, RoccatLighting.TurtleBeachAimoWide));
            Assert.NotNull(model);
            Assert.Equal(2, model.Map.Lights.Count);
        }

        [Fact]
        public void TheOtherQuadcastS_IsTheQuadcastS()
        {
            Assert.NotNull(HyperXMicrophoneLighting.Instance.Match(RegisteredCollection(HyperXMicrophoneLighting.OpenRgbFamily, HyperXMicrophoneLighting.QuadcastS,
                HyperXMicrophoneLighting.KingstonVid, HyperXMicrophoneLighting.OtherQuadcastS)));
        }

        [Fact]
        public void TheAw2724dm_IsTheAw3423dwf()
        {
            Assert.NotNull(AlienwareMonitorLighting.Instance.Match(RegisteredCollection(AlienwareMonitorLighting.OpenRgbFamily, AlienwareMonitorLighting.Aw3423Dwf,
                AlienwareMonitorLighting.VendorId, AlienwareMonitorLighting.Aw2724Dm, (ushort)AlienwareMonitorLighting.DwfReportSize)));
        }

        [Fact]
        public void TheSk621_IsTheSk622()
        {
            var model = CoolerMasterLighting.Instance.Match(RegisteredCollection(CoolerMasterLighting.OpenRgbFamily, CoolerMasterLighting.Sk622B,
                CoolerMasterLighting.VendorId, CoolerMasterLighting.Sk621));
            Assert.NotNull(model);
            Assert.Equal(CoolerMasterLighting.KeyboardLights(CoolerMasterLighting.Sk622B, KeyboardLayout.AnsiQwerty).Count, model.Map.Lights.Count);
        }

        // ── Corsair's wireless family ──

        [Theory]
        [InlineData(0x1BFE, true)]
        [InlineData(0x1BFF, true)]
        [InlineData(0x2B10, true)]
        [InlineData(0x2B14, true)]
        [InlineData(0x1BC0, true)]
        [InlineData(0x1BD0, true)]
        [InlineData(0x1BB8, false)]
        [InlineData(0x1BDB, false)]
        [InlineData(0x2B08, false)]
        public void TheBragiLibrary_GivesEachProductItsLights(int pid, bool keyboard)
        {
            var device = CorsairPeripheralV2Lighting.DeviceOf((ushort)pid);
            Assert.Equal(keyboard ? CorsairDeviceType.Keyboard : CorsairDeviceType.Mouse, device.Type);
            Assert.NotEmpty(device.OwnLights);
            Assert.Equal(1, device.OwnInterface);
        }

        [Fact]
        public void ABlockDevice_SpacesItsColorsAsItsPluginSays()
        {
            var voyager = CorsairPeripheralV2Lighting.DeviceOf(0x1BD0);
            CorsairPeripheralV2Lighting.MapOf(voyager, KeyboardLayout.AnsiQwerty, out int length);
            Assert.Equal(123, length);
            // Three lights, spaced four apart.
            var scimitar = CorsairPeripheralV2Lighting.DeviceOf(0x1BDB);
            CorsairPeripheralV2Lighting.MapOf(scimitar, KeyboardLayout.AnsiQwerty, out length);
            Assert.Equal(4, length);
        }

        [Theory]
        [InlineData(0x2A00)]
        [InlineData(0x2A02)]
        [InlineData(0x2A08)]
        [InlineData(0x2A09)]
        [InlineData(0x0A96)]
        [InlineData(0x0A97)]
        [InlineData(0x2A0F)]
        public void TheBragiHeadsets_AreOnTheirOwnInterface(int pid)
        {
            var device = CorsairPeripheralV2Lighting.DeviceOf((ushort)pid);
            Assert.Equal(4, device.OwnInterface);
            var collection = new HidCollectionInfo
            {
                Path = $@"\\?\hid#vid_1b1c&pid_{pid:x4}&mi_04#7&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
                VendorId = 0x1B1C,
                ProductId = (ushort)pid,
                InterfaceNumber = 4,
                UsagePage = 0xFF42,
                Usage = 1,
            };
            Assert.NotNull(CorsairPeripheralV2Lighting.PluginRegistration(collection));
            Assert.Null(CorsairPeripheralV2Lighting.PluginRegistration(collection with { InterfaceNumber = 1, Path = collection.Path.Replace("mi_04", "mi_01") }));
        }

        [Fact]
        public void TheLaptopDongle_IsAReceiver()
        {
            Assert.True(CorsairPeripheralV2Lighting.IsReceiver(CorsairPeripheralV2Lighting.LaptopReceiver));
            var collection = new HidCollectionInfo
            {
                Path = @"\\?\hid#vid_1b1c&pid_0d01&mi_01#7&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
                VendorId = 0x1B1C,
                ProductId = CorsairPeripheralV2Lighting.LaptopReceiver,
                InterfaceNumber = 1,
                UsagePage = 0xFF42,
                Usage = 1,
            };
            Assert.NotNull(CorsairPeripheralV2Lighting.PluginRegistration(collection));
        }

        // ── Lights ──

        [Fact]
        public void AGeneratedTable_GoesByItsUsages_AndNamesTheRest()
        {
            var table = new PluginKeyTable("x", new string[] { null, "Underglow", "M2", "Volume Up", "" },
                new[] { 0, 1, 2, 3, 4 }, new byte[5], new byte[5], new ushort[] { 0x14, 0, 0, 0, 0 });
            var lights = PluginKeyNames.Lights(table);
            Assert.Equal((LightKind.Key, (ushort)0x14), (lights[0].Kind, lights[0].Usage));
            Assert.Equal(LightKind.Edge, lights[1].Kind);
            Assert.Equal((LightLabel.MacroKey, 2), (lights[2].Label, lights[2].LabelNumber));
            Assert.Equal(LightLabel.VolumeUp, lights[3].Label);
            Assert.Equal((LightKind.Key, (ushort)0), (lights[4].Kind, lights[4].Usage));
        }
    }
}
