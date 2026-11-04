using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Gigabyte Aorus M2 against OpenRGB's
    /// GigabyteAorusMouseController (#557).</summary>
    public class GigabyteAorusMouseLightingTests
    {
        [Fact]
        public void TheReports_AreOpenRgbs()
        {
            // SetMode CC, mode, brightness, R, G, B, speed, 00 and SendDirect
            // CD 00 R G B 00 00 00 (GigabyteAorusMouseController.cpp:57-85).
            Assert.Equal("CC 01 64 11 22 33 00 00", Hex(GigabyteAorusMouseLighting.ModeFrame(0x112233, 0x01, 0x64, 0)));
            Assert.Equal("CD 00 11 22 33 00 00 00", Hex(GigabyteAorusMouseLighting.DirectFrame(0x112233)));
        }

        [Fact]
        public void ASession_SelectsStaticWithThePicturesColor_Once_ThenSendsDirect()
        {
            var model = GigabyteAorusMouseLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(GigabyteAorusMouseLighting.OpenRgbFamily).Single(), feature: 8));
            var io = new FakeReportChannel { FeatureReportLength = 8 };
            var session = model.Begin(io, _ => { });
            Assert.Empty(io.Calls);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x0A0B0C }));
            Assert.Equal(new[] { "CC 01 64 0A 0B 0C 00 00", "CD 00 0A 0B 0C 00 00 00" }, Calls(io).Select(c => Hex(c.Bytes)));
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x010203 }));
            Assert.Equal("CD 00 01 02 03 00 00 00", Hex(Calls(io).Last().Bytes));
            Assert.Equal(3, io.Calls.Count);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(3, io.Calls.Count);
            Assert.Empty(session.CrashFrames);
            // A failed mode report leaves the next picture to send it again.
            var failing = new FakeReportChannel { FeatureReportLength = 8, FailWrites = true };
            var fresh = model.Begin(failing, _ => { });
            Assert.Equal(HidLightingPaint.Failed, fresh.Paint(failing, _ => { }, model.Map.Lights, new[] { 1 }));
            failing.FailWrites = false;
            Assert.Equal(HidLightingPaint.Sent, fresh.Paint(failing, _ => { }, model.Map.Lights, new[] { 1 }));
            Assert.Equal(0xCC, Calls(failing).First().Bytes[0]);
        }
    }
}
