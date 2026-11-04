using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Trust GXT 114 and GXT 180 against OpenRGB's
    /// TrustController (#557).</summary>
    public class TrustLightingTests
    {
        private static HidLightingModel Model(string detector, ushort feature)
            => TrustLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(TrustLighting.OpenRgbFamily).First(r => r.Detector == detector), feature: feature));

        [Fact]
        public void TheGxt114Report_IsOpenRgbs()
        {
            // SetMode for Static, red 11 green 22 blue 33 (TrustGXT114Controller.cpp:61-125).
            var expected = new byte[154];
            expected[0] = 0x04;
            expected[8] = 0x4C;
            expected[71] = 0x11; expected[72] = 0xB0; expected[74] = 0x89; expected[75] = 0x0E;
            expected[76] = 0x9D; expected[77] = 0xA7; expected[78] = 0xB7;
            for (int i = 79; i <= 89; i++) expected[i] = 0x80;
            expected[93] = 0x28; expected[96] = 0xA2;
            foreach (int i in new[] { 101, 108, 109, 113, 114, 115, 117, 118, 119, 124, 127, 128, 131, 134, 135, 138, 139, 141, 142, 143, 144 })
                expected[i] = 0xFF;
            expected[103] = 0x11; expected[104] = 0x22; expected[105] = 0x33;
            expected[148] = 0x58; expected[149] = 0x30; expected[150] = 0x31;
            expected[151] = 0x30; expected[152] = 0x31; expected[153] = 0x30;
            Assert.Equal(Hex(expected), Hex(TrustLighting.Gxt114Frame(0x112233)));
        }

        [Fact]
        public void TheGxt180Report_IsOpenRgbs()
        {
            // 03 06 BB AA 2A 00 0A 00 R G B 01 00 05 00, 64 bytes (TrustGXT180Controller.cpp:51-81).
            var frame = TrustLighting.Gxt180Frame(0x445566);
            Assert.Equal(64, frame.Length);
            Assert.Equal("03 06 BB AA 2A 00 0A 00 44 55 66 01 00 05 00 00", Hex(frame, 16));
            Assert.All(frame.Skip(15), b => Assert.Equal(0, b));
        }

        [Fact]
        public void AGxt114Session_ReadsReport4First_AndIsRefusedUnanswered()
        {
            var model = Model(TrustLighting.Gxt114Detector, 154);
            var silent = new FakeReportChannel { FeatureReportLength = 154 };
            Assert.Null(model.Begin(silent, _ => { }));
            Assert.Equal(("get", "04"), (Calls(silent).Single().Kind, Hex(Calls(silent).Single().Bytes)));

            var io = new FakeReportChannel { FeatureReportLength = 154, Feature = id => new byte[154] };
            var session = model.Begin(io, _ => { });
            Assert.NotNull(session);
            Assert.True(session.Feature);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x112233 }));
            Assert.Equal(Hex(TrustLighting.Gxt114Frame(0x112233)), Hex(Calls(io).Last().Bytes));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Empty(session.CrashFrames);
            Assert.Equal(2, io.Calls.Count);
            io.FailWrites = true;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x112233 }));
        }

        [Fact]
        public void AGxt180Session_SendsOneReportAPicture()
        {
            var model = Model(TrustLighting.Gxt180Detector, 64);
            var io = new FakeReportChannel { FeatureReportLength = 64 };
            var session = model.Begin(io, _ => { });
            Assert.Empty(io.Calls);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x0A0B0C }));
            Assert.Equal(("set", Hex(TrustLighting.Gxt180Frame(0x0A0B0C))), (Calls(io).Single().Kind, Hex(Calls(io).Single().Bytes)));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Single(io.Calls);
        }

        [Fact]
        public void BothModels_AreStored_OneLight_AndRefuseAShortReport()
        {
            foreach (var (detector, size) in new[] { (TrustLighting.Gxt114Detector, 154), (TrustLighting.Gxt180Detector, 64) })
            {
                var model = Model(detector, (ushort)size);
                Assert.Equal(SinowealthLighting.StoredHoldMs, model.HoldMs);
                Assert.Equal(SinowealthLighting.StoredRepaintMs, model.RepaintMs);
                Assert.True(model.HoldMs > 0);
                Assert.Equal(LightKind.Zone, Assert.Single(model.Map.Lights).Kind);
                Assert.Empty(model.Programs);
                Assert.Null(Model(detector, (ushort)(size - 1)));
            }
            Assert.NotSame(Model(TrustLighting.Gxt114Detector, 154), Model(TrustLighting.Gxt180Detector, 64));
        }
    }
}
