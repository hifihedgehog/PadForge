using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;
using F = PadForge.Common.Input.Peripherals.PluginDeviceLighting;

namespace PadForge.Tests
{
    /// <summary>The devices OpenRGB has no controller for, against SignalRGB's
    /// plugins (#557). Every expected byte is written out here from the
    /// plugin named beside it.</summary>
    [Collection("PeripheralOutputStatics")]
    public class PluginDeviceLightingTests
    {
        private static PluginDevice Device(ushort vid, ushort pid)
            => F.Devices.Single(d => d.VendorId == vid && d.ProductId == pid);

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

        private static string Picture(ushort vid, ushort pid, params int[] colors)
            => string.Join("\n", Device(vid, pid).Picture(Pad(colors, Device(vid, pid).Slots)).Select(Text));

        private static int[] Pad(int[] colors, int slots)
        {
            var all = new int[slots];
            colors.CopyTo(all, 0);
            return all;
        }

        private static string Text(PluginStep step) => step.Kind switch
        {
            PluginStepKind.Output => "out " + Hex(step.Bytes),
            PluginStepKind.Feature => "set " + Hex(step.Bytes),
            PluginStepKind.ReadInput => "read",
            PluginStepKind.ReadFeature => "get " + step.Value.ToString("X2"),
            _ => "wait " + step.Value,
        };

        /// <summary>A frame as hex with its trailing zeros gone, up to the length
        /// it must have.</summary>
        private static string Short(byte[] frame)
        {
            int end = frame.Length;
            while (end > 0 && frame[end - 1] == 0) end--;
            return Hex(frame, end);
        }

        private static string[] ShortSteps(IEnumerable<PluginStep> steps)
            => steps.Select(s => s.Bytes == null ? Text(s) : (s.Kind == PluginStepKind.Output ? "out " : "set ") + Short(s.Bytes)).ToArray();

        // ── The table ──

        [Fact]
        public void EveryDevice_IsMatchedAtItsOwnCollection_AndNowhereElse()
        {
            foreach (var d in F.Devices)
            {
                var collection = CollectionOf(d);
                var model = F.Instance.Match(collection);
                Assert.True(model != null, d.Name);
                Assert.Equal(d.Name, model.Name);
                Assert.Same(F.Instance, model.Family);
                Assert.Equal($"plugin:{d.VendorId:X4}:{d.ProductId:X4}", model.Map.Product);
                Assert.Same(model, F.Instance.Match(collection));
                Assert.Null(F.Instance.Match(collection with { VendorId = (ushort)(d.VendorId ^ 0x5555) }));
                if (d.Interface != OpenRgbRegistration.Any)
                    Assert.Null(F.Instance.Match(collection with
                    {
                        InterfaceNumber = d.Interface + 1,
                        Path = collection.Path.Replace($"mi_{d.Interface:x2}", $"mi_{d.Interface + 1:x2}"),
                    }));
                if (d.UsagePage != OpenRgbRegistration.Any)
                    Assert.Null(F.Instance.Match(collection with { UsagePage = (ushort)(d.UsagePage ^ 0x0100) }));
                if (d.Usage != OpenRgbRegistration.Any)
                    Assert.Null(F.Instance.Match(collection with { Usage = (ushort)(d.Usage ^ 0x0040) }));
                if (d.OutputLength > 0)
                    Assert.Null(F.Instance.Match(collection with { OutputReportLength = (ushort)(d.OutputLength - 1) }));
                if (d.FeatureLength > 0)
                    Assert.Null(F.Instance.Match(collection with { FeatureReportLength = (ushort)(d.FeatureLength - 1) }));
            }
            Assert.Null(F.Instance.Match(null));
        }

        [Fact]
        public void TheFamily_IsInTheWorkersList_AndNoOtherFamilyTakesItsDevices()
        {
            Assert.Single(HidLightingFamilies.All, f => ReferenceEquals(f, F.Instance));
            foreach (var d in F.Devices)
            {
                var model = HidLightingFamilies.Match(CollectionOf(d));
                Assert.True(model != null, d.Name);
                Assert.True(ReferenceEquals(model.Family, F.Instance), d.Name + " goes to " + model.Family.Name);
            }
        }

        [Fact]
        public void EveryLight_HasAnAddressThePictureReads()
        {
            foreach (var d in F.Devices)
            {
                Assert.NotEmpty(d.Lights);
                Assert.All(d.Lights, l => Assert.InRange(l.Address, 0, d.Slots - 1));
                Assert.Equal(d.Lights.Count, d.Lights.Select(l => l.Address).Distinct().Count());
            }
        }

        /// <summary>A light's color reaches the frames, and no other light's
        /// picture is the same.</summary>
        [Fact]
        public void EveryLight_ChangesThePicture()
        {
            foreach (var d in F.Devices)
            {
                var picture = d.Picture ?? d.PictureWith(new IHidReportChannel[0]);
                string Run(int[] colors) => string.Join("\n", picture(colors).Select(Text));
                string black = Run(new int[d.Slots]);
                var seen = new HashSet<string> { black };
                foreach (var light in d.Lights)
                {
                    var colors = new int[d.Slots];
                    colors[light.Address] = 0x123456;
                    Assert.True(seen.Add(Run(colors)), $"{d.Name} light {light.Address}");
                }
            }
        }

        [Fact]
        public void EveryKeyName_IsAUsageOrALabel()
        {
            foreach (var table in new[]
            {
                PluginKeyTables.SpeedlinkOriosKb, PluginKeyTables.FnaticMiniStreak, PluginKeyTables.ClaymoreIiRight,
                PluginKeyTables.ClaymoreIiLeft, PluginKeyTables.K65Mini, PluginKeyTables.Strafe, PluginKeyTables.Rk61, PluginKeyTables.Rk84,
            })
            {
                Assert.Empty(PluginKeyNames.Unresolved(table));
                Assert.Equal(table.Names.Length, table.Indexes.Length);
                Assert.Equal(table.Names.Length, table.X.Length);
                Assert.Equal(table.Names.Length, table.Y.Length);
            }
        }

        // ── The session ──

        [Fact]
        public void TheFirstPicture_RunsTheStart_AndLaterOnesDoNot()
        {
            var d = Device(0x260D, 0x2002);
            var io = new FakeReportChannel { OutputReportLength = 16, InputReportLength = 16 };
            var session = new F.Session(d, d.Picture);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, d.Lights, new[] { 0x112233 }));
            Assert.Equal(4 + 2, io.Calls.Count);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, d.Lights, new[] { 0x112233 }));
            Assert.Equal(4 + 2 + 2, io.Calls.Count);
        }

        [Fact]
        public void AWriteThatFails_FailsThePicture_ButAStartThatFailsDoesNot()
        {
            var d = Device(0x260D, 0x2002);
            var failing = new FakeReportChannel { OutputReportLength = 16, FailWrites = true };
            var session = new F.Session(d, d.Picture);
            Assert.Equal(HidLightingPaint.Failed, session.Paint(failing, _ => { }, d.Lights, new[] { 0 }));
            Assert.Equal(HidLightingPaint.Failed, session.Paint(null, _ => { }, d.Lights, new[] { 0 }));
        }

        [Fact]
        public void AReadThatBringsNothing_IsNoFailure()
        {
            var d = Device(0x3842, 0x2415);
            var io = new FakeReportChannel { FeatureReportLength = 40, Feature = _ => null };
            var session = new F.Session(d, d.Picture);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, d.Lights, new int[4]));
            Assert.Contains(io.Calls, c => c.Kind == "get" && c.Bytes[0] == 0x07);
        }

        [Fact]
        public void AWait_GoesThroughTheWorkersWait()
        {
            var d = Device(0x0951, 0x1729);
            var io = new FakeReportChannel { OutputReportLength = 65 };
            var waits = new List<int>();
            new F.Session(d, d.Picture).Paint(io, Recorder(waits), d.Lights, new int[2]);
            Assert.Equal(new[] { 1 }, waits);
        }

        [Fact]
        public void NoPictureMeansNoHandBack()
        {
            var d = Device(0x0B05, 0x196B);
            var session = new F.Session(d, d.PictureWith(new IHidReportChannel[0]));
            Assert.Empty(session.CrashFrames);
            var io = new FakeReportChannel { OutputReportLength = 65 };
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Empty(io.Calls);
        }

        [Fact]
        public void AHandBack_GoesOutOnce_AfterAPicture_AndIsTheCrashFrames()
        {
            var d = Device(0x0B05, 0x196B);
            var session = new F.Session(d, d.PictureWith(new IHidReportChannel[0]));
            var io = new FakeReportChannel { OutputReportLength = 65, InputReportLength = 65 };
            session.Paint(io, _ => { }, d.Lights, new int[d.Lights.Count]);
            Assert.Equal(d.HandBack[0].Bytes, Assert.Single(session.CrashFrames));
            int before = io.Calls.Count;
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(before + 1, io.Calls.Count);
            Assert.Empty(session.CrashFrames);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(before + 1, io.Calls.Count);
        }

        [Fact]
        public void AFailedHandBack_IsReported_AndStaysOwed()
        {
            var d = Device(0x0B05, 0x196B);
            var session = new F.Session(d, d.PictureWith(new IHidReportChannel[0]));
            var io = new FakeReportChannel { OutputReportLength = 65 };
            session.Paint(io, _ => { }, d.Lights, new int[d.Lights.Count]);
            io.FailWrites = true;
            Assert.False(session.HandBack(io, _ => { }));
            Assert.NotEmpty(session.CrashFrames);
        }

        // ── The frames, from each plugin ──

        [Fact]
        public void Evga_TwoZoneReports_TwoAnswers_TwoApplies()
        {
            var steps = F.EvgaPicture(new[] { 0x010203, 0x040506, 0x070809, 0x0A0B0C }).ToArray();
            string colors = "FF 01 02 03 FF 04 05 06 FF 07 08 09 FF 0A 0B 0C 01 01 01 01";
            Assert.Equal(new[]
            {
                "set 07 EA 01 02 " + colors, "get 07",
                "set 07 EA 02 02 " + colors, "get 07",
                "set 07 EA 01 01 01 01 01 01", "set 07 EA 02 01 01 01 01 01",
            }, ShortSteps(steps));
            Assert.All(steps.Where(s => s.Bytes != null), s => Assert.Equal(40, s.Bytes.Length));
        }

        [Fact]
        public void Patriot_SendsGreenBlueRed()
        {
            var d = Device(0x260D, 0x2002);
            Assert.Equal(new[]
            {
                "out FF 02 00 00 01 00 01", "out FF 03 01 01 00 0A", "out FF 04 00 00 FF 00 FF", "out FF 01",
            }, ShortSteps(d.Start));
            Assert.Equal(new[] { "out FF 03 01 01 00 0A", "out FF 04 00 00 22 33 11" },
                ShortSteps(d.Picture(new[] { 0x112233 })));
        }

        [Fact]
        public void SpeedlinkMouse_TakesTheThreeWrites_WithColorsAtThePluginsOffsets()
        {
            var d = Device(0x0C45, 0x5F01);
            var steps = d.Picture(new[] { 0x010203, 0x040506, 0x070809, 0x0A0B0C, 0x0D0E0F, 0x101112 }).ToArray();
            Assert.Equal(3, steps.Length);
            Assert.Equal("04 01 00 01", Short(steps[0].Bytes));
            Assert.Equal("04 02 00 02", Short(steps[2].Bytes));
            var frame = steps[1].Bytes;
            Assert.Equal(64, frame.Length);
            Assert.Equal("04 00 00 12 38", Hex(frame, 5));
            // Red at 8 + 3i, blue at 10 + 3i, green at 12 + 3i.
            Assert.Equal(new byte[] { 0x01, 0x03, 0x02 }, new[] { frame[8], frame[10], frame[12] });
            Assert.Equal(new byte[] { 0x10, 0x12, 0x11 }, new[] { frame[23], frame[25], frame[27] });
        }

        [Fact]
        public void SpeedlinkMousepad_HasItsOwnHeader()
        {
            var d = Device(0x0C45, 0x763D);
            var frame = d.Picture(new int[10]).ElementAt(1).Bytes;
            Assert.Equal("04 00 00 12 21", Hex(frame, 5));
        }

        [Fact]
        public void SpeedlinkKeyboard_SevenBlocks_ColorsByLedIndex()
        {
            var d = Device(0x0C45, 0x763E);
            var colors = new int[d.Slots];
            colors[1] = 0xAABBCC;
            colors[76] = 0x112233;
            var steps = d.Picture(colors).ToArray();
            Assert.Equal(7, steps.Length);
            string[] heads =
            {
                "04 00 00 12 38 00 00 00", "04 00 00 12 38 38 00 00", "04 00 00 12 38 70 00 00", "04 00 00 12 38 A8 00 00",
                "04 00 00 12 38 E0 00 00", "04 00 00 12 38 18 01 00", "04 00 00 12 2A 50 01 00",
            };
            for (int i = 0; i < 7; i++) Assert.Equal(heads[i], Hex(steps[i].Bytes, 8));
            Assert.Equal("00 00 00 AA BB CC", Hex(steps[0].Bytes.Skip(8).ToArray(), 6));
            // LED 76 is byte 228 of the data, byte 4 of the fifth block.
            Assert.Equal("11 22 33", Hex(steps[4].Bytes.Skip(8 + 4).ToArray(), 3));
            // Y and Z by place on the German board.
            var lights = d.Lights;
            Assert.Equal(0x1D, lights[Array.IndexOf(PluginKeyTables.SpeedlinkOriosKb.Names, "Y")].Usage);
            Assert.Equal(0x1C, lights[Array.IndexOf(PluginKeyTables.SpeedlinkOriosKb.Names, "Z")].Usage);
        }

        [Fact]
        public void ThermaltakeBungee_OneReport_ThirteenEntries()
        {
            var d = Device(0x264A, 0x8020);
            Assert.Equal(new[] { "out 41 03", "out 12 22" }, ShortSteps(d.Start));
            var colors = new int[13];
            colors[0] = 0x010203;
            colors[11] = 0x040506;
            var steps = d.Picture(colors).ToArray();
            var frame = Assert.Single(steps).Bytes;
            Assert.Equal(65, frame.Length);
            Assert.Equal("00 C0 01 0D 00 00 01 02 03 01 00 00 00", Hex(frame, 13));
            Assert.Equal("0B 04 05 06 0C", Hex(frame.Skip(5 + 11 * 4).ToArray(), 5));
        }

        [Fact]
        public void ThermaltakeE1_FifteenThenTen_ByItsWireIds()
        {
            var d = Device(0x264A, 0x8002);
            var colors = new int[25];
            colors[1] = 0x010203;
            colors[24] = 0x040506;
            var steps = d.Picture(colors).ToArray();
            Assert.Equal(new[] { PluginStepKind.Output, PluginStepKind.Wait, PluginStepKind.Output, PluginStepKind.Wait }, steps.Select(s => s.Kind));
            Assert.Equal("00 C0 01 0F 00 00 00 00 00 08 01 02 03", Hex(steps[0].Bytes, 13));
            Assert.Equal("00 C0 01 0A 00", Hex(steps[2].Bytes, 5));
            Assert.Equal("24 04 05 06", Hex(steps[2].Bytes.Skip(5 + 9 * 4).ToArray(), 4));
        }

        [Theory]
        [InlineData(0x9011)]
        [InlineData(0x8022)]
        public void ThermaltakeTwentyLights_FifteenThenFive(int pid)
        {
            var d = Device(0x264A, (ushort)pid);
            var colors = new int[20];
            colors[19] = 0x040506;
            var steps = d.Picture(colors).ToArray();
            Assert.Equal("00 C0 01 0F 00", Hex(steps[0].Bytes, 5));
            Assert.Equal("00 C0 01 05 00", Hex(steps[2].Bytes, 5));
            Assert.Equal("13 04 05 06", Hex(steps[2].Bytes.Skip(5 + 4 * 4).ToArray(), 4));
        }

        [Fact]
        public void Katana_SendsBlueGreenRed_AfterABrightnessByte()
        {
            var d = Device(0x041E, 0x3247);
            var colors = new int[7];
            colors[0] = 0x010203;
            colors[6] = 0x040506;
            var frame = Assert.Single(d.Picture(colors)).Bytes;
            Assert.Equal(65, frame.Length);
            Assert.Equal("00 5A 3A 21 0A 00 03 01 02 FF 03 02 01 FF 00 00 00", Hex(frame, 17));
            Assert.Equal("FF 06 05 04", Hex(frame.Skip(9 + 6 * 4).ToArray(), 4));
        }

        [Fact]
        public void Alienware15_AZoneBlockPerLight_ThenTheApply()
        {
            var colors = new int[13];
            colors[0] = 0x010203;
            colors[12] = 0x040506;
            var steps = ShortSteps(F.Alienware15Picture(colors));
            Assert.Equal(2 + 13 * 2 + 1, steps.Length);
            Assert.Equal("out 02 07 04", steps[0]);
            Assert.Equal("wait 1", steps[1]);
            Assert.Equal("out 02 03 01 00 00 01 01 02 03", steps[2]);
            Assert.Equal("out 02 04", steps[3]);
            Assert.Equal("out 02 03 0D 00 40 00 04 05 06", steps[26]);
            Assert.Equal("out 02 05", steps[^1]);
        }

        [Fact]
        public void AlienwareMonitors_AReportPerLight_TheRestFF_ReportIdZero()
        {
            foreach (ushort pid in new ushort[] { 0x100B, 0x1008 })
            {
                var d = Device(0x187C, pid);
                Assert.Equal(new[] { "out 00 92 37 05 00 51 82 D0 F4 99" }, ShortSteps(d.Start));
                var steps = d.Picture(new[] { 0x010203, 0x040506, 0x070809, 0x0A0B0C }).ToArray();
                Assert.Equal(4, steps.Length);
                Assert.Equal("00 92 37 0A 00 51 87 D0 04 01 01 02 03 23 B8 FF", Hex(steps[0].Bytes, 16));
                Assert.Equal("00 92 37 0A 00 51 87 D0 04 08 0A 0B 0C 23 B8 FF", Hex(steps[3].Bytes, 16));
                Assert.Equal(0xFF, steps[0].Bytes[64]);
            }
        }

        [Fact]
        public void Pg43uq_SixFeatureReports_RedBlueGreen()
        {
            var d = Device(0x0B05, 0x1931);
            Assert.Equal(new[]
            {
                "set 03 02 A1 80 20 01", "set 03 02 A1 80 30 01", "set 03 02 A1 80 A0 01",
                "set 03 02 A1 80 00 11", "set 03 02 A1 80 01 33", "set 03 02 A1 80 02 22",
            }, ShortSteps(d.Picture(new[] { 0x112233 })));
        }

        [Fact]
        public void Keris_AReportPerZone()
        {
            var d = Device(0x0B05, 0x1A59);
            Assert.Equal(new[] { "out 00 51 28 00 00 00 04 01 02 03", "out 00 51 28 01 00 00 04 04 05 06" },
                ShortSteps(d.Picture(new[] { 0x010203, 0x040506 })));
        }

        [Fact]
        public void Claymore_EightReportsOfFifteen_NumberFirst()
        {
            var d = Device(0x0B05, 0x196B);
            var colors = new int[d.Slots];
            colors[6] = 0x010203;
            var steps = F.ClaymorePicture(colors).ToArray();
            Assert.Equal(8, steps.Length);
            byte[] counts = { 0x90, 0x81, 0x72, 0x63, 0x54, 0x45, 0x36, 0x27 };
            for (int i = 0; i < 8; i++) Assert.Equal(new byte[] { 0x00, 0xC0, 0x81, counts[i], 0x00 }, steps[i].Bytes.Take(5));
            // The left logo is LED 6, the first entry. Then the right logo, 14.
            Assert.Equal("06 01 02 03 0E 00 00 00", Hex(steps[0].Bytes.Skip(5).ToArray(), 8));
            // Past the keyboard's 108 keys the plugin leaves FF.
            Assert.Equal("FF FF FF FF", Hex(steps[7].Bytes.Skip(5 + 12 * 4).ToArray(), 4));
        }

        [Fact]
        public void Claymore_TheNumberPadMoves_TheLedNumbersMove_AndTheSaveStaysOut()
        {
            var d = Device(0x0B05, 0x196B);
            Assert.Equal(new[] { new HidSibling(2, 0xFFC0) }, d.Siblings);
            var plate = new FakeReportChannel { InputReportLength = 65 };
            var reports = new Queue<byte[]>();
            reports.Enqueue(new byte[] { 0x03, 0x81, 0x04, 0x00, 0x00 });
            plate.Input = () => reports.Count > 0 ? reports.Dequeue() : null;
            var picture = d.PictureWith(new IHidReportChannel[] { plate });
            var colors = new int[d.Slots];
            colors[0] = 0x010203;
            // Esc is LED 0 with the pad on the right and LED 32 with it on the
            // left, and on the left the ISO keys drop out.
            var frame = picture(colors).First().Bytes;
            Assert.Equal("20 01 02 03", Hex(frame.Skip(5 + 2 * 4).ToArray(), 4));
            reports.Enqueue(new byte[] { 0x03, 0x81, 0x04, 0x00, 0x01 });
            frame = picture(colors).First().Bytes;
            Assert.Equal("00 01 02 03", Hex(frame.Skip(5 + 2 * 4).ToArray(), 4));
            // The hand-back sets the effect and leaves out the plugin's 50 55.
            var handBack = Assert.Single(d.HandBack).Bytes;
            Assert.Equal("00 51 2C 04 00 48 64", Hex(handBack, 7));
            Assert.Equal(65, handBack.Length);
        }

        [Fact]
        public void Fnatic_NineteenToAReport_TheFirstCarries0F03()
        {
            var d = Device(0x2F0E, 0x0103);
            var colors = new int[d.Slots];
            colors[0] = 0x010203;
            colors[19] = 0x040506;
            var steps = d.Picture(colors).ToArray();
            Assert.Equal(6, steps.Length);
            Assert.Equal("00 0F 40 01 00 00 00 00 0F 03 01 02 03", Hex(steps[0].Bytes, 13));
            Assert.Equal("00 0F 40 01 00 39 00 00", Hex(steps[1].Bytes, 8));
            // 106 LEDs, nineteen to a report: the last holds eleven.
            Assert.Equal("00 0F 40 01 00 1D 01 00", Hex(steps[5].Bytes, 8));
            // Byte 57 of the data is the second report's first: the first
            // report held 55. LED 19 is at byte 57, so its color opens the
            // second report two bytes in.
            Assert.Equal(new byte[] { 0x04, 0x05, 0x06 }, steps[1].Bytes.Skip(10).Take(3));
        }

        [Fact]
        public void Fnatic_NamesItsIsoKeysByPlace()
        {
            var d = Device(0x2F0E, 0x0103);
            var names = PluginKeyTables.FnaticMiniStreak.Names;
            Assert.Equal(0x32, d.Lights[Array.IndexOf(names, "ISO /")].Usage);
            Assert.Equal(0x64, d.Lights[Array.IndexOf(names, "ISO #")].Usage);
        }

        [Fact]
        public void PulsefireFpsPro_BothLights_ThenTheBrightness()
        {
            var d = Device(0x0951, 0x1729);
            Assert.Equal(new[] { "out 00 D8 00 00 06 01 02 03 04 05 06 64", "wait 1" },
                ShortSteps(d.Picture(new[] { 0x010203, 0x040506 })));
        }

        [Theory]
        [InlineData(0x048E)]
        [InlineData(0x028E)]
        public void HasteWireless_ThePrimer_TheColor_TheEmptyReport(int pid)
        {
            var d = Device(0x03F0, (ushort)pid);
            Assert.Equal(new[]
            {
                "set 00 04 F2 00 00 00 00 00 00 02", "get 00",
                "set 00 81 01 02 03 00 00 00 00 02", "get 00",
                "set", "get 00",
            }, ShortSteps(d.Picture(new[] { 0x010203 })).Select(s => s.TrimEnd()));
            var start = d.Start.ToArray();
            // The plugin's Initialize, setting aside the mouse settings.
            Assert.Equal("set 00 04 05 00 00 00 00 00 00 01", ShortSteps(start)[0]);
            Assert.Equal("get 00", ShortSteps(start)[1]);
            Assert.Equal("set 00 04 02", ShortSteps(start)[^2].Substring(0, 12));
            Assert.Equal(0xAA, start[^2].Bytes[63]);
            Assert.Equal(0x55, start[^2].Bytes[64]);
            Assert.Equal(38, start.Length);
        }

        [Theory]
        [InlineData(0x051A)]
        [InlineData(0x051F)]
        [InlineData(0x051E)]
        public void Nari_OneReport_WithItsWaitAsTheRate(int pid)
        {
            var d = Device(0x1532, (ushort)pid);
            Assert.Equal(new[] { "set FF 0A 00 FF 04 12 F1 05 72 01 02 03" }, ShortSteps(d.Picture(new[] { 0x010203 })));
            Assert.Equal(60, d.RepaintMs);
        }

        [Fact]
        public void Raptor_TwelveLights_AndTheXorOfBytes3To88()
        {
            var colors = new int[12];
            colors[0] = 0x010203;
            colors[11] = 0x040506;
            var frame = F.RaptorPicture(colors).Single().Bytes;
            Assert.Equal(91, frame.Length);
            Assert.Equal("00 00 1F 00 00 00 29 0F 03 00 00 00 00 0B 01 02 03", Hex(frame, 17));
            Assert.Equal("04 05 06", Hex(frame.Skip(14 + 33).ToArray(), 3));
            byte xor = (byte)(0x29 ^ 0x0F ^ 0x03 ^ 0x0B ^ 0x01 ^ 0x02 ^ 0x03 ^ 0x04 ^ 0x05 ^ 0x06);
            Assert.Equal(xor, frame[89]);
        }

        [Fact]
        public void G19_OneFeatureReportOfFour()
        {
            var d = Device(0x046D, 0xC229);
            var frame = Assert.Single(d.Picture(new[] { 0x010203 })).Bytes;
            Assert.Equal(new byte[] { 0x07, 0x01, 0x02, 0x03 }, frame);
        }

        [Fact]
        public void Makalu_ColorsFrom17_BrightnessAt41()
        {
            var d = Device(0x3282, 0x0003);
            Assert.Equal((byte)0x01, d.Start[0].Bytes[16]);
            Assert.Equal("A1 3C", Hex(d.Start[0].Bytes, 2));
            var colors = new int[8];
            colors[0] = 0x010203;
            colors[7] = 0x040506;
            var frame = Assert.Single(d.Picture(colors)).Bytes;
            Assert.Equal(64, frame.Length);
            Assert.Equal("A1 3C", Hex(frame, 2));
            Assert.Equal(0x0F, frame[16]);
            Assert.Equal("01 02 03", Hex(frame.Skip(17).ToArray(), 3));
            Assert.Equal("04 05 06", Hex(frame.Skip(38).ToArray(), 3));
            Assert.Equal(0x64, frame[41]);
        }

        [Fact]
        public void Mp860_ControlThenThreeReports_ThenTheApply()
        {
            var d = Device(0x2516, 0x006D);
            var colors = new int[d.Slots];
            colors[0] = 0x010203;
            colors[27] = 0x040506;
            var steps = d.Picture(colors).ToArray();
            Assert.Equal(5, steps.Length);
            Assert.Equal("00 41 80", Short(steps[0].Bytes));
            Assert.Equal("00 51 A8 00 00 01 02 03", Short(steps[1].Bytes));
            // LED 27 is the fourth of the third report.
            Assert.Equal("00 51 A8 02", Short(steps[2].Bytes));
            Assert.Equal("00 51 A8 04 00 00 00 00 00 00 00 00 00 00 04 05 06", Short(steps[3].Bytes));
            Assert.Equal("00 51 28 00 00 B0", Short(steps[4].Bytes));
            Assert.Equal(19, d.Lights.Count);
        }
    }
}
