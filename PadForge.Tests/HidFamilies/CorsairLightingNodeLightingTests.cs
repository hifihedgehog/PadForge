using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Corsair LT100 towers and LS100 strips against OpenRGB's
    /// CorsairLightingNodeController, SignalRGB's two plugins for them and
    /// CorsairLightingProtocol. Every expected byte is written out from those
    /// files.</summary>
    public class CorsairLightingNodeLightingTests
    {
        private const ushort Lt100 = CorsairLightingNodeLighting.Lt100;
        private const ushort Ls100 = CorsairLightingNodeLighting.Ls100;

        /// <summary>The vendor collection: usage page FFC0 and usage 0C00, a
        /// 64-byte command and a 16-byte answer behind report ID 0
        /// (CorsairLightingProtocol RawHID.h:38-48).</summary>
        private static HidCollectionInfo Collection(ushort product) => new()
        {
            Path = $@"\\?\hid#vid_1b1c&pid_{product:x4}#7&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
            VendorId = 0x1B1C, ProductId = product, UsagePage = 0xFFC0, Usage = 0x0C00, InterfaceNumber = 0,
            OutputReportLength = 65, InputReportLength = 17,
        };

        private static FakeReportChannel Device(bool answers = true)
            => new() { OutputReportLength = 65, InputReportLength = 17, Input = () => answers ? new byte[17] : null };

        private static List<string> Writes(FakeReportChannel io)
            => Calls(io).Where(c => c.Kind == "out").Select(c => Hex(c.Bytes)).ToList();

        /// <summary>A report as OpenRGB's 65-byte buffer holds it: the bytes
        /// given, then zeros.</summary>
        private static string Report(params byte[] bytes)
        {
            var report = new byte[65];
            Array.Copy(bytes, report, bytes.Length);
            return Hex(report);
        }

        /// <summary>SendDirect's report (CorsairLightingNodeController.cpp:243-278)
        /// with every value the same.</summary>
        private static string DirectReport(byte channel, byte start, byte count, byte color, byte value)
            => Report(new byte[] { 0x00, 0x32, channel, start, count, color }.Concat(Enumerable.Repeat(value, count)).ToArray());

        /// <summary>OpenRGB registers the two with no interface, page or usage
        /// (CorsairLightingNodeControllerDetect.cpp:47 and 50). The LT100 drives
        /// one channel of up to four towers of 27 LEDs, the LS100 two channels of
        /// up to 138 (Corsair_LT100_Lighting_Tower.js:35-40,
        /// Corsair_LS100_Light_Strips.js:31-34, LEDController.h:276).</summary>
        [Theory]
        [InlineData(0x0C23, "Corsair LT100", new[] { 108 })]
        [InlineData(0x0C1E, "Corsair LS100 Lighting Kit", new[] { 138, 138 })]
        public void TheTowersAndTheStrips_AreTheFamilys(int product, string name, int[] most)
        {
            var model = CorsairLightingNodeLighting.Instance.Match(Collection((ushort)product));
            Assert.Equal(name, model.Name);
            Assert.Equal(new[] { VendorPrograms.CorsairIcue }, model.Programs);
            Assert.Equal(most.Select((leds, i) => new LightStrip(i + 1, leds, 0)), model.Map.Strips);
            Assert.Equal(most.Select((_, i) => i + 1), model.Map.Lights.Select(l => l.Zone));
            Assert.All(model.Map.Lights, l => Assert.Equal(LightKind.Zone, l.Kind));
            Assert.Equal(CorsairLightingNodeLighting.KeepAliveMs, model.KeepAliveMs);
            Assert.Equal(CorsairLightingNodeLighting.KeepAliveMs, model.KeepAliveIdleMs);
            Assert.Equal(0, model.HoldMs);
            Assert.Null(CorsairLightingNodeLighting.Instance.Match(Collection((ushort)product) with { OutputReportLength = 64 }));
            Assert.Null(CorsairLightingNodeLighting.Instance.Match(Collection((ushort)product) with { InputReportLength = 16 }));
        }

        /// <summary>The family's other five registrations are parts inside a PC
        /// (CorsairLightingNodeControllerDetect.cpp:44-46, 48 and 49), and the
        /// generator leaves them out of the table.</summary>
        [Theory]
        [InlineData(0x0C1A)]
        [InlineData(0x0C0B)]
        [InlineData(0x0C10)]
        [InlineData(0x1D00)]
        [InlineData(0x1D04)]
        public void TheControllersInsideAPc_StayOut(int product)
        {
            Assert.Null(CorsairLightingNodeLighting.Instance.Match(Collection((ushort)product)));
            Assert.Null(HidLightingFamilies.Match(Collection((ushort)product)));
            Assert.DoesNotContain(OpenRgbRegistrations.All, r => r.VendorId == 0x1B1C && r.ProductId == product);
        }

        [Fact]
        public void TheTable_HoldsTheTwoRegistrationsAlone()
        {
            Assert.Equal(new[] { (0x1B1C, 0x0C1E), (0x1B1C, 0x0C23) },
                OpenRgbRegistrations.Of(CorsairLightingNodeLighting.OpenRgbFamily).Select(r => (r.VendorId, r.ProductId)).OrderBy(p => p.ProductId));
        }

        /// <summary>The firmware request, 00 02 (SendFirmwareRequest,
        /// CorsairLightingNodeController.cpp:216-241). A device that does not
        /// answer gets no session and nothing more.</summary>
        [Fact]
        public void ASession_BeginsOnlyWhenTheDeviceAnswers()
        {
            var silent = Device(answers: false);
            Assert.Null(CorsairLightingNodeLighting.Begin(silent, Lt100));
            Assert.Equal(new[] { Report(0x00, 0x02) }, Writes(silent));
            Assert.Equal(CorsairLightingNodeLighting.BeginReadMs, silent.LongestReadMs);

            var io = Device();
            Assert.NotNull(CorsairLightingNodeLighting.Begin(io, Lt100));
            Assert.Equal(new[] { Report(0x00, 0x02) }, Writes(io));
            Assert.Null(CorsairLightingNodeLighting.Begin(new FakeReportChannel { FailWrites = true }, Lt100));
        }

        /// <summary>With no count set, the LT100's zone color goes to all 108 LEDs
        /// its channel takes: software control, 00 38 00 02, then runs of 50, 50
        /// and 8, each as a red, a green and a blue report, then the commit, 00 33
        /// FF (SetChannelLEDs, CorsairLightingNodeController.cpp:163-214), every
        /// report followed by a read of at most 15 ms (WriteAndRead, lines
        /// 511-536). The values are halved, as SignalRGB sends them
        /// (Corsair_LT100_Lighting_Tower.js:18 and 87-89).</summary>
        [Fact]
        public void APictureWithNoCount_LightsEveryTower()
        {
            var io = Device();
            var session = new CorsairLightingNodeLighting.Session(Lt100);
            var lights = CorsairLightingNodeLighting.Map(Lt100, StripCounts.None).Lights;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, new[] { 0xFF8040 }));
            var expected = new List<string> { Report(0x00, 0x38, 0x00, 0x02) };
            foreach (var (start, count) in new[] { ((byte)0, (byte)50), ((byte)50, (byte)50), ((byte)100, (byte)8) })
            {
                expected.Add(DirectReport(0, start, count, 0, 0x7F));
                expected.Add(DirectReport(0, start, count, 1, 0x40));
                expected.Add(DirectReport(0, start, count, 2, 0x20));
            }
            expected.Add(Report(0x00, 0x33, 0xFF));
            Assert.Equal(expected, Writes(io));
            Assert.Equal(new[] { "out", "in" }, Calls(io).Select(c => c.Kind).Distinct());
            Assert.Equal(Calls(io).Count(c => c.Kind == "out"), Calls(io).Count(c => c.Kind == "in"));
            Assert.Equal(CorsairLightingNodeLighting.ReadMs, io.LongestReadMs);
        }

        /// <summary>With counts set, each LS100 channel takes its own strip's
        /// colors in order, channel by channel, each with its software control
        /// and its commit.</summary>
        [Fact]
        public void APictureWithCounts_SendsEachStripItsOwnColors()
        {
            var io = Device();
            var session = new CorsairLightingNodeLighting.Session(Ls100);
            Assert.True(session.SizeStrips(io, _ => { }, new[] { 3, 2 }));
            Assert.Empty(Calls(io));
            var map = session.Map;
            Assert.Equal(new[] { new LightStrip(1, 138, 3), new LightStrip(2, 138, 2) }, map.Strips);
            Assert.Equal(new[] { 0x100, 0x101, 0x102, 0x200, 0x201 }, map.Lights.Select(l => l.Address));
            Assert.All(map.Lights, l => Assert.Equal(LightKind.Edge, l.Kind));
            var colors = new[] { 0x020406, 0x080A0C, 0x0E1012, 0xFEFCFA, 0x000000 };
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, map.Lights, colors));
            Assert.Equal(new[]
            {
                Report(0x00, 0x38, 0x00, 0x02),
                Report(0x00, 0x32, 0x00, 0x00, 0x03, 0x00, 0x01, 0x04, 0x07),
                Report(0x00, 0x32, 0x00, 0x00, 0x03, 0x01, 0x02, 0x05, 0x08),
                Report(0x00, 0x32, 0x00, 0x00, 0x03, 0x02, 0x03, 0x06, 0x09),
                Report(0x00, 0x33, 0xFF),
                Report(0x00, 0x38, 0x01, 0x02),
                Report(0x00, 0x32, 0x01, 0x00, 0x02, 0x00, 0x7F, 0x00),
                Report(0x00, 0x32, 0x01, 0x00, 0x02, 0x01, 0x7E, 0x00),
                Report(0x00, 0x32, 0x01, 0x00, 0x02, 0x02, 0x7D, 0x00),
                Report(0x00, 0x33, 0xFF),
            }, Writes(io));
        }

        /// <summary>The brightness OpenRGB sets on taking Direct mode, 00 39
        /// (CorsairLightingNodeController.cpp:91-97 and 466-499), is stored by
        /// the device and scales its own effects alone (CorsairLightingProtocol
        /// LEDController.cpp:140-145, FastLEDController.cpp:436-451), so no
        /// frame of a session sends it.</summary>
        [Fact]
        public void NoFrame_SetsTheStoredBrightness()
        {
            var io = Device();
            var session = new CorsairLightingNodeLighting.Session(Ls100);
            var lights = CorsairLightingNodeLighting.Map(Ls100, StripCounts.None).Lights;
            session.Paint(io, _ => { }, lights, new[] { 0x123456, 0x654321 });
            session.KeepAlive(io, _ => { }, lights, new[] { 0x123456, 0x654321 });
            session.HandBack(io, _ => { });
            Assert.DoesNotContain(Calls(io), c => c.Kind == "out" && c.Bytes[1] == 0x39);
        }

        /// <summary>The keep-alive is the commit alone, as OpenRGB's thread sends
        /// it (KeepaliveThread, CorsairLightingNodeController.cpp:51-61).</summary>
        [Fact]
        public void TheKeepAlive_IsTheCommitAlone()
        {
            var io = Device();
            var session = new CorsairLightingNodeLighting.Session(Lt100);
            Assert.True(session.KeepAlive(io, _ => { }, Array.Empty<LightInfo>(), Array.Empty<int>()));
            Assert.Equal(new[] { Report(0x00, 0x33, 0xFF) }, Writes(io));
            Assert.Equal(1, Calls(io).Count(c => c.Kind == "in"));
        }

        /// <summary>The hand-back is hardware control for each channel, as
        /// SignalRGB's Shutdown sends it (Corsair_LT100_Lighting_Tower.js:54-58,
        /// Corsair_LS100_Light_Strips.js:48-51), and a commit, and the same frames
        /// are the crash path once a picture went out. A session that never
        /// painted hands nothing back.</summary>
        [Theory]
        [InlineData(0x0C23, 1)]
        [InlineData(0x0C1E, 2)]
        public void TheHandBack_GivesEveryChannelBackToTheDevice(int product, int channels)
        {
            var io = Device();
            var idle = new CorsairLightingNodeLighting.Session((ushort)product);
            Assert.Empty(idle.CrashFrames);
            Assert.True(idle.HandBack(io, _ => { }));
            Assert.Empty(Calls(io));

            var session = new CorsairLightingNodeLighting.Session((ushort)product);
            var lights = CorsairLightingNodeLighting.Map((ushort)product, StripCounts.None).Lights;
            session.Paint(io, _ => { }, lights, Enumerable.Repeat(0x445566, lights.Count).ToArray());
            var expected = Enumerable.Range(0, channels).Select(c => Report(0x00, 0x38, (byte)c, 0x01)).Append(Report(0x00, 0x33, 0xFF)).ToList();
            Assert.Equal(expected, session.CrashFrames.Select(f => Hex(f)));
            Assert.Same(session.CrashFrames, session.CrashFrames);
            Assert.False(session.Feature);
            io.Calls.Clear();
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(expected, Writes(io));
        }

        [Fact]
        public void AFailedWriteOrRead_FailsThePicture()
        {
            var lights = CorsairLightingNodeLighting.Map(Lt100, StripCounts.None).Lights;
            var broken = new FakeReportChannel { OutputReportLength = 65, InputReportLength = 17, FailWrites = true };
            Assert.Equal(HidLightingPaint.Failed, new CorsairLightingNodeLighting.Session(Lt100).Paint(broken, _ => { }, lights, new[] { 0 }));
            var deaf = new FakeReportChannel { OutputReportLength = 65, InputReportLength = 17, FailReads = true };
            Assert.Equal(HidLightingPaint.Failed, new CorsairLightingNodeLighting.Session(Lt100).Paint(deaf, _ => { }, lights, new[] { 0 }));
            // A read that times out is no failure, as in WriteAndRead.
            Assert.Equal(HidLightingPaint.Sent, new CorsairLightingNodeLighting.Session(Lt100).Paint(Device(answers: false), _ => { }, lights, new[] { 0 }));
        }

        [Fact]
        public void TheSharedTable_HoldsTheFamily_BeforeThePluginRows()
        {
            var all = HidLightingFamilies.All.ToList();
            Assert.True(all.IndexOf(CorsairLightingNodeLighting.Instance) >= 0);
            Assert.True(all.IndexOf(CorsairLightingNodeLighting.Instance) < all.IndexOf(PluginDeviceLighting.Instance));
        }
    }
}
