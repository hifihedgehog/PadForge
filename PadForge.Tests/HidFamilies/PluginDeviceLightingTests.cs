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
            => F.Devices.First(d => d.VendorId == vid && d.ProductId == pid);

        /// <summary>A device that drops back to its own lighting when no picture comes takes
        /// its start again before the picture.</summary>
        [Fact]
        public void AKeepAlive_SendsTheStart_ThenThePicture()
        {
            var d = F.Devices.First(x => x.KeepAliveMs > 0 && x.Start.Count > 0 && x.Siblings.Count == 0 && x.Answers == null && x.AskVariant == null);
            var io = new FakeReportChannel { FeatureReportLength = 65, OutputReportLength = 65, InputReportLength = 65 };
            var session = F.Instance.Match(CollectionOf(d)).Begin(io, _ => { });
            var colors = new int[Math.Max(d.Slots, d.Lights.Count)];
            Assert.True(session.KeepAlive(io, _ => { }, d.Lights, colors));
            var first = io.Calls.First(c => c.Kind is "out" or "set").Bytes;
            var start = d.Start.First(s => s.Kind is PluginStepKind.Output or PluginStepKind.Feature).Bytes;
            Assert.Equal(start, first);
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
            foreach (var d in F.Devices.Where(x => x.OnlyWhen == null))
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
            foreach (var d in F.Devices.Where(x => x.OnlyWhen == null))
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
                var lights = F.MapOf(d, Array.Empty<int>()).Lights;
                Assert.NotEmpty(lights);
                Assert.All(lights, l => Assert.InRange(l.Address, 0, d.Slots - 1));
                Assert.Equal(lights.Count, lights.Select(l => l.Address).Distinct().Count());
            }
        }

        /// <summary>A light's color reaches the frames, and no other light's
        /// picture is the same.</summary>
        [Fact]
        public void EveryLight_ChangesThePicture()
        {
            foreach (var d in F.Devices)
            {
                // A board that says what it is takes its frames from that answer:
                // PluginDevicesBetaTests has those.
                if (d.AskVariant != null && d.Picture == null) continue;
                var noCounts = d.StripMaxLeds.Select(_ => -d.StripDefaultLeds).ToArray();
                Func<int[], IEnumerable<PluginStep>> picture = d.StripPicture != null
                    ? colors => d.StripPicture(colors, noCounts)
                    : d.Picture ?? d.PictureWith(new IHidReportChannel[0]);
                string Run(int[] colors) => string.Join("\n", picture(colors).Select(Text));
                string black = Run(new int[d.Slots]);
                var seen = new HashSet<string> { black };
                foreach (var light in F.MapOf(d, Array.Empty<int>()).Lights)
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
                PluginKeyTables.ApexM750Tkl, PluginKeyTables.ApexProMini,
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

        /// <summary>The input-report read too: an answer that does not come is
        /// no failure, the same as a feature report's.</summary>
        [Fact]
        public void AnInputReadThatBringsNothing_IsNoFailure()
        {
            var d = Device(0x1E71, 0x2004);
            var io = new FakeReportChannel { OutputReportLength = 64, InputReportLength = 64, Input = () => null };
            var session = new F.Session(d, null);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, session.Map.Lights, new int[session.Map.Lights.Count]));
            Assert.Contains(io.Calls, c => c.Kind == "in");
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

        [Theory]
        [InlineData(0x0A40, 0x09)]
        [InlineData(0x0A41, 0x08)]
        [InlineData(0x0A42, 0x09)]
        [InlineData(0x0A43, 0x08)]
        [InlineData(0x0A44, 0x09)]
        [InlineData(0x0A4B, 0x09)]
        [InlineData(0x0A4C, 0x09)]
        [InlineData(0x0A5A, 0x08)]
        [InlineData(0x0A5C, 0x09)]
        [InlineData(0x0A3D, 0x08)]
        [InlineData(0x0A3E, 0x09)]
        [InlineData(0x0A62, 0x08)]
        [InlineData(0x0A64, 0x09)]
        [InlineData(0x0A69, 0x08)]
        [InlineData(0x0A6B, 0x09)]
        [InlineData(0x0A71, 0x08)]
        [InlineData(0x0A73, 0x09)]
        [InlineData(0xA346, 0x09)]
        public void ACorsairHeadset_TakesSoftwareControl_ThenTheReds_Greens_Blues(int pid, int device)
        {
            var d = Device(0x1B1C, (ushort)pid);
            string m = device.ToString("X2");
            Assert.Equal(new[]
            {
                $"out 02 {m} 01 03 00 02", "wait 100", $"out 02 {m} 0D 00 01", "wait 100", $"out 02 {m} 01 02 00 E8 03",
            }, ShortSteps(d.Start));
            Assert.Equal(new[] { $"out 02 {m} 06 00 09 00 00 00 01 04 07 02 05 08 03 06 09" },
                ShortSteps(d.Picture(new[] { 0x010203, 0x040506, 0x070809 })));
            Assert.Equal(new[] { $"out 02 {m} 01 03 00 01" }, ShortSteps(d.HandBack));
            Assert.Equal((OpenRgbRegistration.Any, 0xFF42, 1), (d.Interface, d.UsagePage, d.Usage));
            Assert.Equal(device == 0x09 ? 5000 : 0, d.KeepAliveMs);
        }

        [Fact]
        public void Hs80Wired_OneReport()
        {
            var d = Device(0x1B1C, 0x0A88);
            Assert.Equal(new[] { "out CB 03 1C 01 16 02 17 03", "wait 1" }, ShortSteps(d.Picture(new[] { 0x010203 })));
            Assert.Equal(16, d.Picture(new int[1]).First().Bytes.Length);
        }

        [Theory]
        [InlineData(0x0A14)]
        [InlineData(0x0A17)]
        [InlineData(0x0A19)]
        [InlineData(0x0A1A)]
        [InlineData(0x0A1D)]
        [InlineData(0x1B23)]
        [InlineData(0x0A56)]
        [InlineData(0x0A52)]
        [InlineData(0x0A51)]
        [InlineData(0x0A55)]
        [InlineData(0x0A75)]
        public void Void_BothCans_WithItsWaitAsTheRate(int pid)
        {
            var d = Device(0x1B1C, (ushort)pid);
            Assert.Equal(new[] { "out C8 01" }, ShortSteps(d.Start));
            Assert.Equal(new[] { "out CB 06 1C 01 16 02 17 03 1D 04 18 05 19 06" }, ShortSteps(d.Picture(new[] { 0x010203, 0x040506 })));
            Assert.Equal(new[] { "out C8" }, ShortSteps(d.HandBack));
            Assert.Equal(20, d.HandBack[0].Bytes.Length);
            Assert.Equal(30, d.RepaintMs);
        }

        [Theory]
        [InlineData(0x1B35)]
        [InlineData(0x1B64)]
        [InlineData(0x1B51)]
        public void DarkCore_AReportPerZone_FiveMsApart(int pid)
        {
            var d = Device(0x1B1C, (ushort)pid);
            Assert.Equal(new[] { "out 00 07 05 02 00 01", "out 00 07 04 02" }, ShortSteps(d.Start));
            Assert.Equal(new[]
            {
                "out 00 07 AA 00 00 01 07 00 00 64 01 02 03 00 00 00 05", "wait 5",
                "out 00 07 AA 00 00 02 07 00 00 64 04 05 06 00 00 00 04", "wait 5",
                "out 00 07 AA 00 00 04 07 00 00 64 07 08 09 00 00 00 03", "wait 5",
            }, ShortSteps(d.Picture(new[] { 0x010203, 0x040506, 0x070809 })));
            Assert.Equal(new[] { "out 00 07 05 01 00 01", "out 00 07 04 01" }, ShortSteps(d.HandBack));
        }

        [Fact]
        public void Nexus_TheWholeScreen_In121Reports()
        {
            var d = Device(0x1B1C, 0x1B8E);
            Assert.Equal(new[]
            {
                "set 03 1D 01", "get 03", "set 03 19", "get 03",
                "set 03 20 00 19 79 E7 32 2E 30 2E 30 2E 33", "get 03",
                "set 03 0B 40 01 79 E7 32 2E 30 2E 30 2E 33", "get 03",
            }, ShortSteps(d.Start));
            var frames = d.Picture(new[] { 0x010203 }).Select(s => s.Bytes).ToArray();
            Assert.Equal(121, frames.Length);
            Assert.Equal("02 05 40 00 00 00 F8 03 03 02 01 FF", Hex(frames[0], 12));
            Assert.Equal("02 05 40 01 78 00 F8 03 03 02 01 FF", Hex(frames[120], 12));
            Assert.All(frames, f => Assert.Equal(1024, f.Length));
            // The hand-back is feature reports, and the crash path sends the
            // session's own kind, so it leaves them out.
            Assert.All(d.HandBack, s => Assert.Equal(PluginStepKind.Feature, s.Kind));
            Assert.Equal("03 0D 01 01 78 00 C0 03 2F 2F 2F FF", Hex(d.HandBack[0].Bytes, 12));
            Assert.Equal("03 01 64 01 78 00 C0 03 2F 2F 2F FF", Hex(d.HandBack[1].Bytes, 12));
            var session = new F.Session(d, d.Picture);
            session.Paint(new FakeReportChannel { OutputReportLength = 1024, FeatureReportLength = 32 }, _ => { }, d.Lights, new int[1]);
            Assert.Empty(session.CrashFrames);
        }

        // ── Corsair IDs of the families OpenRGB holds ──

        [Theory]
        [InlineData(0x1BCF, 0x1BAF)]
        [InlineData(0x1BC3, 0x1BAF)]
        [InlineData(0x1BBD, 0x1BAF)]
        [InlineData(0x1B15, 0x1B20)]
        [InlineData(0x1B14, 0x1B2F)]
        [InlineData(0x1B3A, 0x1B49)]
        public void TheCorsairFamily_TakesAnotherIdAsTheDeviceItIs(int other, int same)
        {
            var r = OpenRgbRegistrations.Of(CorsairPeripheralLighting.OpenRgbFamily).First(x => x.ProductId == same);
            var known = CollectionFor(r, output: 65, feature: 65, input: 64);
            var collection = known with { ProductId = (ushort)other };
            var model = CorsairPeripheralLighting.Instance.Match(collection);
            Assert.NotNull(model);
            var row = CorsairPeripheralLighting.Instance.Match(known);
            Assert.Equal(row.Name, model.Name);
            Assert.Equal(row.Map.Product, model.Map.Product);
            // A board with one LED color has the same row and a model of its own.
            if (!CorsairPeripheralLighting.OneColor.Contains((ushort)other)) Assert.Same(row, model);
        }

        [Theory]
        [InlineData(0x1B79, 5)]
        [InlineData(0x1B98, 3)]
        [InlineData(0x1B80, 12)]
        [InlineData(0x1B81, 12)]
        [InlineData(0x1B7F, 12)]
        [InlineData(0x1BB2, 13)]
        public void TheBragiMice_AreMatched_WithThePluginsLights(int pid, int lights)
        {
            var collection = new HidCollectionInfo
            {
                Path = $@"\\?\hid#vid_1b1c&pid_{pid:x4}&mi_01#7&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
                VendorId = 0x1B1C,
                ProductId = (ushort)pid,
                InterfaceNumber = 1,
                UsagePage = 0xFF42,
                Usage = 1,
                OutputReportLength = 65,
                InputReportLength = 65,
            };
            var model = CorsairPeripheralV2Lighting.Instance.Match(collection);
            Assert.NotNull(model);
            Assert.Equal(lights, model.Map.Lights.Count);
            Assert.Null(CorsairPeripheralV2Lighting.Instance.Match(collection with { UsagePage = 0xFF00 }));
            Assert.Null(CorsairPeripheralV2Lighting.Instance.Match(collection with
            {
                InterfaceNumber = 2,
                Path = collection.Path.Replace("mi_01", "mi_02"),
            }));
        }

        [Fact]
        public void TheK70ProMini_TakesTheFamilysNumbers()
        {
            var device = CorsairPeripheralV2Lighting.DeviceOf(0x1BB6);
            var lights = device.OwnLights;
            // OpenRGB's K70 has Esc at 41 and A at 4, and so does the plugin's Mini.
            Assert.Equal(41, lights.Single(l => l.Usage == 0x29).Address);
            Assert.Equal(4, lights.Single(l => l.Usage == 0x04).Address);
            Assert.All(lights, l => Assert.True(l.Address >= 0));
            // Each key's LED is its usage on the keyboard page.
            Assert.All(lights.Where(l => l.Kind == LightKind.Key && l.Usage is >= 0x04 and <= 0x1D), l => Assert.Equal(l.Usage, l.Address));
            var air = CorsairPeripheralV2Lighting.DeviceOf(0x1BAB).OwnLights;
            Assert.Equal(41, air.Single(l => l.Usage == 0x29).Address);
            Assert.Equal(4, air.Single(l => l.Usage == 0x04).Address);
        }

        [Fact]
        public void TheK70Pros_ThirdId_IsTheK70Pro()
        {
            var third = CorsairPeripheralV2Lighting.DeviceOf(0x1BD4);
            var first = CorsairPeripheralV2Lighting.DeviceOf(0x1BC4);
            Assert.Equal(first.Layout, third.Layout);
            Assert.Equal(first.Zones, third.Zones);
        }

        [Fact]
        public void ADeviceOfThePlugin_HasALightMap_WithAColorBufferPastItsLastLight()
        {
            var device = CorsairPeripheralV2Lighting.DeviceOf(0x1BB2);
            var map = CorsairPeripheralV2Lighting.MapOf(device, KeyboardLayout.AnsiQwerty, out int length);
            Assert.Equal(13, length);
            Assert.Equal(13, map.Lights.Count);
        }

        // ── Roccat ──

        [Fact]
        public void RoccatDock_FourColorsWithFF()
        {
            var d = Device(0x1E7D, 0x2CB8);
            Assert.Equal(39, d.Start[0].Bytes.Length);
            Assert.Equal("02 00 09 12 FA", Hex(d.Start[0].Bytes, 5));
            Assert.Equal("01 FF FF", Short(d.Start[1].Bytes));
            Assert.Equal(new[] { "set 03 01 02 03 FF 04 05 06 FF 07 08 09 FF 0A 0B 0C FF" },
                ShortSteps(d.Picture(new[] { 0x010203, 0x040506, 0x070809, 0x0A0B0C })));
        }

        [Fact]
        public void KoneXpAir_WiredAndWireless_DifferInThePluginsHeaders()
        {
            var colors = new[] { 0x010203, 0, 0, 0, 0x040506 };
            Assert.Equal("set 06 00 4D 06 15 6D 70 61 01 02 03 00 00 00 00 00 00 00 00 00 04 05 06",
                ShortSteps(Device(0x1E7D, 0x2CB2).Picture(colors))[0]);
            Assert.Equal("set 06 01 4D 06 15 00 00 00 01 02 03 00 00 00 00 00 00 00 00 00 04 05 06",
                ShortSteps(Device(0x1E7D, 0x2CB6).Picture(colors))[0]);
            var start = ShortSteps(Device(0x1E7D, 0x2CB6).Start).Where(s => s != "wait 30").ToArray();
            Assert.Equal("set 06 01 13 07 02", start[0]);
            Assert.Equal("set 06", start[1].Substring(0, 6));
            Assert.Equal("set 06 01 4E 06 04 01 01 01 FF", start[6]);
            Assert.Equal(10, start.Length);
        }

        [Fact]
        public void BurstProAir_RunsItsStartTwice_AndFourColors()
        {
            var d = Device(0x1E7D, 0x2CAB);
            Assert.Equal(20, d.Start.Count);
            Assert.Equal(new[] { "set 06 00 4D 06 0C 01 02 03 04 05 06 07 08 09 0A 0B 0C", "wait 1" },
                ShortSteps(d.Picture(new[] { 0x010203, 0x040506, 0x070809, 0x0A0B0C })));
        }

        [Fact]
        public void Torch_PacksItsColor()
        {
            var d = Device(0x1E7D, 0x3A56);
            // 0xF8 >> 3 = 1F, 0xF8 >> 6 | 0x84 >> 2 = 03 | 21 = 23, 0x84 >> 7 | 0x40 >> 2 = 01 | 10 = 11.
            Assert.Equal(new[] { "out 00 80 1F 23 11", "wait 7", "out" }, ShortSteps(d.Picture(new[] { 0xF88440 })).Select(s => s.TrimEnd()));
        }

        [Fact]
        public void Kain100And120_OneReport_AndAHandBack()
        {
            var d = Device(0x1E7D, 0x2D5C);
            Assert.Equal(new[] { "set 0E 06 01 01 00 FF" }, ShortSteps(d.Start));
            Assert.Equal(new[] { "set 0D 0B 01 02 03 04 05 06", "wait 1" }, ShortSteps(d.Picture(new[] { 0x010203, 0x040506 })));
            Assert.Equal(new[] { "set 0E 06 00 00 00 FF" }, ShortSteps(d.HandBack));
            Assert.Equal(new[] { "set 0D 0B 01 02 03", "wait 1" }, ShortSteps(Device(0x1E7D, 0x2D00).Picture(new[] { 0x010203 })));
        }

        [Fact]
        public void KovaAimo_TheBackIsPlaceZero()
        {
            var d = Device(0x1E7D, 0x2CF1);
            Assert.Equal(new[] { "set 0A 08 01 02 03 04 05 06", "wait 1" }, ShortSteps(d.Picture(new[] { 0x010203, 0x040506 })));
            Assert.Equal(LightKind.Logo, d.Lights.Single(l => l.Address == 0).Kind);
        }

        [Fact]
        public void Kain200_WiredTakesAnXor_WirelessThePluginsSum()
        {
            var colors = new[] { 0x010203, 0x040506 };
            int xor = 0x08 ^ 0x09 ^ 0x33 ^ 0x01 ^ 0x02 ^ 0x03 ^ 0x04 ^ 0x05 ^ 0x06;
            Assert.Equal($"set 08 09 33 00 01 02 03 04 05 06 {xor:X2}", ShortSteps(Device(0x1E7D, 0x2D5F).Picture(colors))[0]);
            int sum = Math.Abs(1 - 2 + 3 - 4 + 5 - 6 - 0x32);
            Assert.Equal($"set 08 09 33 00 01 02 03 04 05 06 {sum:X2}", ShortSteps(Device(0x1E7D, 0x2D60).Picture(colors))[0]);
        }

        [Fact]
        public void EloAndSyn_ColorAtByteFive()
        {
            var elo = Device(0x1E7D, 0x3A37);
            Assert.Equal(new[] { "out 00 FF 04 00 00 01 02 03" }, ShortSteps(elo.Picture(new[] { 0x010203 })));
            Assert.Equal(5000, elo.KeepAliveMs);
            var syn = Device(0x1E7D, 0x3A3B);
            Assert.Equal(new[] { "out 00 FE 03 00 00 01 02 03" }, ShortSteps(syn.Picture(new[] { 0x010203 })));
            Assert.Equal("out 00 FE 01 00 00 00 09 08 02 00 FF FF FF FF FF FF FF FF FF", ShortSteps(syn.Start)[0]);
        }

        // ── Royal Kludge ──

        [Theory]
        [InlineData(0x0055)]
        [InlineData(0x007A)]
        [InlineData(0x0060)]
        [InlineData(0x004A)]
        public void Rk61_GreenFirst_ColorsAtTheirByte(int pid)
        {
            var d = Device(0x258A, (ushort)pid);
            var colors = new int[d.Slots];
            colors[PluginKeyTables.Rk61.Indexes[0]] = 0x010203;
            var steps = d.Picture(colors).ToArray();
            var frames = steps.Where(s => s.Bytes != null).Select(s => s.Bytes).ToArray();
            Assert.Equal(7, frames.Length);
            Assert.Equal("0A 07 01 06", Hex(frames[0], 4));
            Assert.Equal("0A 07 02", Hex(frames[1], 3));
            Assert.Equal("0A 07 06", Short(frames[5]));
            Assert.Equal("0A 07 07", Short(frames[6]));
            int at = PluginKeyTables.Rk61.Indexes[0];
            var all = frames[0].Skip(4).Take(61).Concat(frames[1].Skip(3).Take(62)).ToArray();
            Assert.Equal(new byte[] { 0x02, 0x01, 0x03 }, all.Skip(at).Take(3));
            Assert.Equal(PluginStepKind.Wait, steps[2].Kind);
        }

        [Theory]
        [InlineData(0x005D)]
        [InlineData(0x0059)]
        [InlineData(0x00C8)]
        [InlineData(0x010D)]
        [InlineData(0x00F4)]
        public void Rk84_RedFirst_WaitsBeforeAReport(int pid)
        {
            var d = Device(0x258A, (ushort)pid);
            var colors = new int[d.Slots];
            colors[PluginKeyTables.Rk84.Indexes[0]] = 0x010203;
            var steps = d.Picture(colors).ToArray();
            Assert.Equal(PluginStepKind.Wait, steps[1].Kind);
            var frames = steps.Where(s => s.Bytes != null).Select(s => s.Bytes).ToArray();
            Assert.Equal(7, frames.Length);
            int at = PluginKeyTables.Rk84.Indexes[0];
            var all = frames[0].Skip(4).Take(61).Concat(frames[1].Skip(3).Take(62)).ToArray();
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, all.Skip(at).Take(3));
        }

        // ── NZXT ──

        [Theory]
        [InlineData(0x2103, 0)]
        [InlineData(0x2106, 0)]
        [InlineData(0x2104, 0)]
        [InlineData(0x2105, 1)]
        [InlineData(0x2108, 1)]
        [InlineData(0x2107, 2)]
        public void Function_FourReports_ZonesAtThePluginsPlaces(int pid, int boardNumber)
        {
            var board = (F.FunctionBoard)boardNumber;
            var d = Device(0x1E71, (ushort)pid);
            Assert.Equal(new[] { "out 43 81 00 84", "out 43 81 00 86", "out 43 82 00 41 64", "out 43 97 00 10 01" }, ShortSteps(d.Start));
            var colors = new int[10];
            for (int i = 0; i < 10; i++) colors[i] = 0x010101 * (i + 1);
            var frames = d.Picture(colors).Select(s => s.Bytes).ToArray();
            Assert.Equal(4, frames.Length);
            Assert.All(frames, f => Assert.Equal(65, f.Length));
            Assert.Equal("43 BD 03 10", Hex(frames[0], 4));
            Assert.Equal("43 3D 02", Hex(frames[1], 3));
            Assert.Equal("43 3D 01", Hex(frames[2], 3));
            Assert.Equal("43 26 00 1C", Hex(frames[3], 4));
            // Zone by zone: 23 and 45, then 6, 28 and 50, then 11, 33 and 55, then 16 and 38.
            int[][] at = { new[] { 23, 45 }, new[] { 6, 28, 50 }, new[] { 11, 33, 55 }, new[] { 16, 38 } };
            int zone = 1;
            for (int f = 0; f < 4; f++)
                foreach (int offset in at[f])
                {
                    Assert.Equal(new[] { (byte)zone, (byte)zone, (byte)zone }, frames[f].Skip(offset).Take(3));
                    zone++;
                }
            Assert.Equal(board == F.FunctionBoard.TklIso ? 0x03 : 0x01, frames[0][0x0D]);
            Assert.Equal(board == F.FunctionBoard.MiniTkl ? 0xF0 : 0x30, frames[2][0x26]);
            Assert.Equal(board == F.FunctionBoard.MiniTkl ? 0xE0 : 0x60, frames[2][0x2A]);
            Assert.Equal(board == F.FunctionBoard.TklIso ? 0x70 : 0x60, frames[2][0x2C]);
        }

        // ── Glorious ──

        [Theory]
        [InlineData(0x2024, false)]
        [InlineData(0x2025, false)]
        [InlineData(0x2013, true)]
        [InlineData(0x2014, true)]
        public void Glorious_TheColorReport_AndTheHandBackOfTheDongle(int pid, bool wired)
        {
            var d = Device(0x258A, (ushort)pid);
            var steps = ShortSteps(d.Picture(new[] { 0x010203 }));
            Assert.Equal("set 00 00 00 02 08 02 00 01 FF 04 00 09 01 02 03", steps[0]);
            Assert.Equal(wired ? 3 : 1, steps.Length);
            Assert.Equal(wired ? 0 : 3, d.HandBack.Count);
            if (wired)
            {
                Assert.Equal("set 00 00 00 02 02 02 02 01 FF", steps[1]);
                Assert.Equal("set 00 00 00 02 02 02 02 00 FF", steps[2]);
            }
            else
            {
                Assert.Equal(new[]
                {
                    "set 00 00 00 02 05 02 00 01 FF 01 00 09", "set 00 00 00 02 02 02 02 01 FF", "set 00 00 00 02 02 02 02 00 FF",
                }, ShortSteps(d.HandBack));
            }
        }

        // ── SteelSeries ──

        [Fact]
        public void ApexM750_ColorsFromByte9_ThenItsOwnApply()
        {
            var d = Device(0x1038, 0x0619);
            var colors = new int[d.Slots];
            int index = PluginKeyTables.ApexM750Tkl.Indexes[0];
            colors[index] = 0x010203;
            var steps = d.Picture(colors).ToArray();
            Assert.Equal("00 00 00 00 FF 01 03 06 12", Hex(steps[0].Bytes, 9));
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, steps[0].Bytes.Skip(index * 3 + 9).Take(3));
            Assert.Equal("set 00 00 00 00 03 01 04 00 2D 01", ShortSteps(steps)[1]);
            Assert.Equal("wait 2", ShortSteps(steps)[2]);
            Assert.Equal(513, steps[0].Bytes.Length);
            Assert.Equal(6, d.Start.Count);
        }

        [Theory]
        [InlineData(0x1626, 0x21, 3)]
        [InlineData(0x161E, 0x40, 1)]
        public void ApexProMini_NumberAndColor_ByIdsCommandAndInterface(int pid, int command, int number)
        {
            var d = Device(0x1038, (ushort)pid);
            Assert.Equal(number, d.Interface);
            var table = PluginKeyTables.ApexProMini;
            var colors = new int[d.Slots];
            colors[table.Indexes[1]] = 0x010203;
            var frame = d.Picture(colors).Single().Bytes;
            Assert.Equal(642, frame.Length);
            Assert.Equal(new byte[] { 0x00, (byte)command, (byte)table.Indexes.Length }, frame.Take(3));
            Assert.Equal(new byte[] { (byte)table.Indexes[1], 0x01, 0x02, 0x03 }, frame.Skip(7).Take(4));
        }

        [Fact]
        public void TheQckDestinysOtherId_IsAClothMat()
        {
            Assert.Contains(SteelSeriesDirectProtocol.Models, m => m.ProductId == 0x3769 && m.Family == SteelSeriesFamily.QckPrismCloth);
        }

        // ── LG ──

        [Fact]
        public void The38gl950g_TakesTheFamilysFrames_OnItsOwnInterface()
        {
            var r = OpenRgbRegistrations.Of(LgMonitorLighting.OpenRgbFamily).Single();
            var known = CollectionFor(r, output: 65, input: 64);
            var collection = known with
            {
                ProductId = LgMonitorLighting.Ultragear38,
                InterfaceNumber = 0,
                Path = known.Path.Replace("pid_9a8a&mi_01", "pid_9a57&mi_00"),
                UsagePage = 0xFF00,
            };
            var model = LgMonitorLighting.Instance.Match(collection);
            Assert.NotNull(model);
            Assert.Equal("LG UltraGear 38GL950G", model.Name);
            Assert.Equal("lg:043E:9A57", model.Map.Product);
            Assert.Equal(48, model.Map.Lights.Count);
            Assert.Null(LgMonitorLighting.Instance.Match(collection with { UsagePage = 0xFF01 }));
            Assert.Null(LgMonitorLighting.Instance.Match(collection with { InterfaceNumber = 1, Path = collection.Path.Replace("mi_00", "mi_01") }));
        }

        // ── Strips ──

        [Fact]
        public void AStrip_WithNoCount_IsOneColorAcrossItsDefaultLeds()
        {
            var d = Device(0x1532, 0x0F2C);
            var map = F.MapOf(d, Array.Empty<int>());
            Assert.Equal(3, map.Lights.Count);
            Assert.Equal(new[] { 120, 120, 120 }, map.Strips.Select(s => s.MaxLeds));
            Assert.All(map.Strips, s => Assert.Equal(0, s.Leds));
            var colors = new int[d.Slots];
            colors[0] = 0x010203;
            var steps = F.ChromaStripPicture(colors, new[] { -80, -80, -80 }).ToArray();
            Assert.Equal("00 04 00 00 00 4F 01 02 03 01 02 03", Hex(steps[0].Bytes, 12));
            Assert.Equal("wait 10", ShortSteps(steps)[1]);
            Assert.Equal("00 04 01 01 00 4F 00 00 00", Hex(steps[2].Bytes, 9));
            Assert.Equal("00 84 02 02 00 4F", Hex(steps[4].Bytes, 6));
            Assert.Equal(381, steps[0].Bytes.Length);
        }

        [Fact]
        public void AStrip_WithACount_TakesAColorPerLed()
        {
            var d = Device(0x1532, 0x0F2C);
            var map = F.MapOf(d, new[] { 3, 0, 200 });
            Assert.Equal(new[] { 3, 0, 120 }, map.Strips.Select(s => s.Leds));
            Assert.Equal(3 + 1 + 120, map.Lights.Count);
            Assert.Contains(map.Lights, l => l.Address == F.StripSpan * 2 + 119);
            Assert.DoesNotContain(map.Lights, l => l.Address == F.StripSpan * 2 + 120);
            var colors = new int[d.Slots];
            colors[0] = 0x010203;
            colors[2] = 0x040506;
            var frame = F.ChromaStripPicture(colors, new[] { 3, -80, -80 }).First().Bytes;
            Assert.Equal("00 04 00 00 00 02 01 02 03 00 00 00 04 05 06", Hex(frame, 15));
        }

        [Fact]
        public void TheSession_SizesItsStrips_AndPaintsByThem()
        {
            var d = Device(0x16D0, 0x1205);
            var io = new FakeReportChannel { OutputReportLength = 65 };
            var session = new F.Session(d, null);
            Assert.Single(session.Map.Lights);
            Assert.True(session.SizeStrips(io, _ => { }, new[] { 2 }));
            Assert.Equal(2, session.Map.Lights.Count);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, session.Map.Lights, new[] { 0x010203, 0x040506 }));
            var call = Assert.Single(io.Calls);
            Assert.Equal("00 01 01 00 AA 01 02 03 04 05 06", Hex(call.Bytes, 11));
        }

        [Fact]
        public void TheChromaStrips_StartAndHandBackGoThroughTheOtherInterface()
        {
            var d = Device(0x1532, 0x0F2C);
            Assert.Equal(new[] { new HidSibling(0, 0x000C) }, d.Siblings);
            var unit = new FakeReportChannel { FeatureReportLength = 381 };
            var other = new FakeReportChannel { FeatureReportLength = 91 };
            var session = new F.Session(d, null, new IHidReportChannel[] { other });
            session.Paint(unit, _ => { }, session.Map.Lights, new int[session.Map.Lights.Count]);
            var start = other.Calls.Select(c => c.Bytes).ToArray();
            Assert.Equal(4, start.Length);
            Assert.Equal("00 00 1F 00 00 00 01 00 37 01", Hex(start[0], 10));
            Assert.Equal("00 00 9F 00 00 00 02 00 47 01 01", Hex(start[1], 11));
            Assert.Equal("00 00 1F 00 00 00 02 00 84 00 01", Hex(start[2], 11));
            Assert.Equal("00 00 1F 00 00 00 02 00 04 03 01", Hex(start[3], 11));
            foreach (var report in start) Assert.Equal(F.RazerCrc(report), report[89]);
            Assert.Equal(3, unit.Calls.Count);
            Assert.True(session.HandBack(unit, _ => { }));
            var last = other.Calls.Last().Bytes;
            Assert.Equal("00 00 1F 00 00 00 01 00 37 00", Hex(last, 10));
            Assert.Equal(F.RazerCrc(last), last[89]);
        }

        [Fact]
        public void Vertagear_TwentyLedsToAReport_GreenFirst_ThenTheSubmit()
        {
            var colors = new int[2 * F.StripSpan];
            colors[0] = 0x010203;
            var steps = ShortSteps(F.VertagearPicture(colors, new[] { -40, 1 }));
            // Forty LEDs of one color in two reports, the submit, then one LED.
            Assert.StartsWith("out 22 10 01 00 02 01 03 02 01 03", steps[0]);
            Assert.Equal("read", steps[1]);
            Assert.StartsWith("out 22 11 01 00 02 01 03", steps[2]);
            Assert.Equal("out 22 A0 01 00 01 00 00 0A 00 00 80 00 32 00 00 01", steps[4]);
            Assert.Equal("out 22 10 02", steps[6]);
            Assert.Equal("out 22 A0 02 00 01 00 00 0A 00 00 80 00 32 00 00 01", steps[8]);
        }

        [Fact]
        public void Pico_TwentyLedsToAReport_ByChannel()
        {
            var colors = new int[2 * F.StripSpan];
            colors[0] = 0x010203;
            colors[F.StripSpan] = 0x040506;
            var steps = ShortSteps(F.PicoPicture(colors, new[] { 21, 1 }));
            Assert.Equal("out 00 01 00 02 01 01 02 03", steps[0]);
            Assert.Equal("out 00 02 00 02 01", steps[1]);
            Assert.Equal("out 00 01 00 01 02 04 05 06", steps[2]);
            Assert.Equal("wait 1", steps[3]);
        }

        [Fact]
        public void LcV2_APacketOf169()
        {
            var colors = new int[F.StripSpan];
            colors[0] = 0x010203;
            colors[169] = 0x040506;
            var d = Device(0x16D0, 0x1201);
            var frames = d.StripPicture(colors, new[] { 170 }).Select(s => s.Bytes).ToArray();
            Assert.Equal(2, frames.Length);
            Assert.Equal("00 01 00 02 00 AA 01 02 03", Hex(frames[0], 9));
            Assert.Equal("00 02 00 02 00 AA 04 05 06", Hex(frames[1], 9));
            Assert.Equal(513, frames[0].Length);
        }

        // ── QMK ──

        private static PluginDevice Q1 => Device(0x3434, 0x0107);

        private static FakeReportChannel QmkChannel(params byte[][] answers)
        {
            var queue = new Queue<byte[]>(answers);
            return new FakeReportChannel
            {
                OutputReportLength = F.QmkReportLength,
                InputReportLength = 33,
                Input = () => queue.Count > 0 ? queue.Dequeue() : null,
            };
        }

        [Theory]
        [InlineData(0x01)]
        [InlineData(0x02)]
        public void Qmk_LightsOnlyWhatAnswersTheFirmwareTypes(int type)
        {
            var io = QmkChannel(new byte[] { 0x00, 0x28, (byte)type });
            var waits = new List<int>();
            var session = F.Instance.Match(CollectionOf(Q1)).Begin(io, Recorder(waits));
            Assert.NotNull(session);
            Assert.Equal("00 28", Short(io.Calls.Single(c => c.Kind == "out").Bytes));
            Assert.Equal(new[] { 30 }, waits);
        }

        [Fact]
        public void Qmk_AFirmwareThatAnswersNothing_StaysDark_AndIsOnlyAsked()
        {
            var silent = QmkChannel();
            Assert.Null(F.Instance.Match(CollectionOf(Q1)).Begin(silent, _ => { }));
            // Its own question, VIA's and Vial's. None sets anything.
            var asked = silent.Calls.Where(c => c.Kind == "out").Select(c => c.Bytes[1]).ToList();
            Assert.Equal(0x28, asked[0]);
            Assert.Contains((byte)0x01, asked);
            Assert.DoesNotContain((byte)0x07, asked);
            Assert.DoesNotContain((byte)0x25, asked);
            // VIA answers an unknown command with FF.
            Assert.Null(F.Instance.Match(CollectionOf(Q1)).Begin(QmkChannel(new byte[] { 0x00, 0xFF, 0x28 }), _ => { }));
            Assert.Null(F.Instance.Match(CollectionOf(Q1)).Begin(QmkChannel(new byte[] { 0x00, 0x28, 0x03 }), _ => { }));
        }

        [Fact]
        public void Qmk_FindsItsAnswerBehindOtherReports()
        {
            var io = QmkChannel(new byte[] { 0x00, 0x99 }, new byte[] { 0x00, 0x28, 0x01 });
            Assert.NotNull(F.Instance.Match(CollectionOf(Q1)).Begin(io, _ => { }));
        }

        [Fact]
        public void Qmk_NineLedsToAReport_ThenTurnsItselfOff()
        {
            var d = Q1;
            Assert.Equal(new[] { "out 00 25", "wait 30" }, ShortSteps(d.Start));
            Assert.Equal(new[] { "out 00 26", "wait 30" }, ShortSteps(d.HandBack));
            var colors = new int[20];
            colors[0] = 0x010203;
            colors[9] = 0x040506;
            colors[19] = 0x070809;
            var steps = F.QmkPicture(colors).ToArray();
            Assert.Equal(3, steps.Length);
            Assert.Equal("00 24 00 09 01 02 03", Hex(steps[0].Bytes, 7));
            Assert.Equal("00 24 09 09 04 05 06", Hex(steps[1].Bytes, 7));
            Assert.Equal("00 24 12 02 00 00 00 07 08 09", Hex(steps[2].Bytes, 10));
            Assert.Equal(33, steps[0].Bytes.Length);
        }

        private static PluginQmkBoard QmkBoard(ushort vendor, ushort product)
            => PluginQmkTables.Boards.Single(b => b.Vendor == vendor && b.Products.Contains(product));

        /// <summary>The usage of the key a board's table puts at a place.</summary>
        private static ushort UsageAt(PluginKeyTable table, int x, int y)
        {
            for (int i = 0; i < table.Indexes.Length; i++)
                if (table.X[i] == x && table.Y[i] == y) return table.Usages[i];
            throw new InvalidOperationException($"no key at {x},{y}");
        }

        [Fact]
        public void Qmk_LegendsGoByPlace()
        {
            // The Q1 ISO's legends are Nordic.
            var iso = QmkBoard(0x3434, 0x0109).Table();
            Assert.Equal(0x35, UsageAt(iso, 0, 1));
            Assert.Equal(0x2D, UsageAt(iso, 11, 1));
            Assert.Equal(0x2E, UsageAt(iso, 12, 1));
            Assert.Equal(0x2F, UsageAt(iso, 11, 2));
            Assert.Equal(0x30, UsageAt(iso, 12, 2));
            Assert.Equal(0x32, UsageAt(iso, 12, 3));
            // The Q1 ANSI's list of names has a comma where the quote is.
            var ansi = QmkBoard(0x3434, 0x0107).Table();
            Assert.Equal(0x2D, UsageAt(ansi, 11, 1));
            Assert.Equal(0x2E, UsageAt(ansi, 12, 1));
            Assert.Equal(0x34, UsageAt(ansi, 11, 3));
            // QWERTZ: the K8 Pro ISO DE's Z sits where a US board has Y.
            var german = QmkBoard(0x3434, 0x0281).Table();
            Assert.Contains((ushort)0x1C, german.Usages);
            Assert.Contains((ushort)0x1D, german.Usages);
            Assert.Contains((ushort)0x64, german.Usages);
            Assert.Contains((ushort)0x32, german.Usages);
        }

        [Fact]
        public void Qmk_TheAnsiAndIsoIdsTakeTheirOwnKeys()
        {
            foreach (var (ansi, iso) in new (ushort, ushort)[] { (0x0107, 0x0109), (0x0161, 0x0162) })
            {
                Assert.DoesNotContain((ushort)0x64, QmkBoard(0x3434, ansi).Table().Usages);
                Assert.Contains((ushort)0x64, QmkBoard(0x3434, iso).Table().Usages);
            }
        }

        /// <summary>Every table is whole: as many LEDs as places and usages, each
        /// LED once where the plugin is right, and most keys named.</summary>
        [Fact]
        public void EveryQmkTable_IsInStep()
        {
            foreach (var board in PluginQmkTables.Boards)
                foreach (var table in new[] { board.Table }.Concat(board.Variants?.Select(v => v.Table) ?? Array.Empty<Func<PluginKeyTable>>()))
                {
                    var t = table();
                    Assert.Equal(t.Indexes.Length, t.Names.Length);
                    Assert.Equal(t.Indexes.Length, t.X.Length);
                    Assert.Equal(t.Indexes.Length, t.Y.Length);
                    Assert.Equal(t.Indexes.Length, t.Usages.Length);
                    Assert.True(t.Indexes.Max() < 256, board.Name);
                    Assert.Equal(t.Indexes.Length, PluginKeyNames.Lights(t).Count);
                }
        }

        /// <summary>Each product is one row, so the first row that matches is
        /// the only one, and every board names at least one product.</summary>
        [Fact]
        public void NoQmkProduct_IsListedTwice()
        {
            var seen = new HashSet<(ushort, ushort)>();
            foreach (var board in PluginQmkTables.Boards)
            {
                Assert.NotEmpty(board.Products);
                foreach (var product in board.Products) Assert.True(seen.Add((board.Vendor, product)), $"{board.Name} {product:X4}");
            }
        }

        [Theory]
        [InlineData(6, true)]
        [InlineData(5, false)]
        public void TheGmmkPro_TakesItsLayoutFromTheFirmware(int unique, bool isIso)
        {
            var board = QmkBoard(0x320F, 0x5044);
            var iso = board.Variants.Single(v => v.Unique == 6).Table();
            var ansi = board.Variants.Single(v => v.Unique == null).Table();
            var io = QmkChannel(new byte[] { 0x00, 0x23, 0x00, 0x00, (byte)unique });
            var variant = F.QmkVariant(board, new Lazy<PluginKeyTable>(board.Table), io, _ => { });
            Assert.Equal((isIso ? iso : ansi).Indexes.Length, variant.Lights.Count);
            Assert.Equal("00 23", Hex(io.Calls.First().Bytes, 2));
        }

        [Fact]
        public void TheGmmk3_ReportsItsVendorAsItsProduct_AsItsFirmwareDoes()
        {
            var board = QmkBoard(0x504B, 0x320F);
            Assert.Equal(new byte?[] { 11, 12, 13, 14, 15, 16 }, board.Variants.Select(v => v.Unique));
        }

        [Fact]
        public void Macropad_TwelveKeys_ByNumber()
        {
            var d = Device(0x3282, 0x0008);
            Assert.Equal(new[] { 1, 12 }, new[] { d.Lights[0].LabelNumber, d.Lights[11].LabelNumber });
            Assert.All(d.Lights, l => Assert.Equal(LightLabel.MacroKey, l.Label));
            var colors = new int[12];
            colors[0] = 0x010203;
            colors[11] = 0x040506;
            var frame = Assert.Single(d.Picture(colors)).Bytes;
            Assert.Equal("00 14 2C 00 01 00 4B 00 01 02 03", Hex(frame, 11));
            Assert.Equal("04 05 06", Hex(frame.Skip(8 + 33).ToArray(), 3));
            Assert.Equal("out 00 11", ShortSteps(d.Start)[0]);
            Assert.Equal("14 2C 0A 00 FF 64 FF", Hex(d.Start[2].Bytes, 7));
            Assert.Equal(0xFF, d.Start[2].Bytes[64]);
        }

        [Fact]
        public void M65B_NineToAReport_ThenTheApply()
        {
            var colors = new int[F.RamaLeds];
            colors[0] = 0x010203;
            colors[80] = 0x040506;
            var steps = ShortSteps(F.RamaPicture(colors));
            Assert.Equal(10, steps.Length);
            Assert.Equal("out 00 07 41 00 01 02 03", steps[0]);
            Assert.Equal("out 00 07 41 09", steps[1]);
            Assert.EndsWith("04 05 06", steps[8]);
            Assert.StartsWith("out 00 07 41 48", steps[8]);
            Assert.Equal("out 00 07 42", steps[9]);
            var d = Device(0x5241, 0x065B);
            Assert.Equal(F.RamaLeds, F.MapOf(d, Array.Empty<int>()).UnnamedKeyCount);
        }

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
