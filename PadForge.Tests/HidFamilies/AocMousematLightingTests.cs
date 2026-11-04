using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The AOC AGON AMM700 against OpenRGB's AOCMousematController
    /// (#557).</summary>
    public class AocMousematLightingTests
    {
        [Fact]
        public void TheDirectReport_IsOpenRgbs()
        {
            // 20, brightness 3, speed 2, direction 0, 01, mode 0, red, green,
            // blue, and the constants (AOCMousematController.cpp:67-117).
            var expected = Bytes(32,
                (0x00, 0x20), (0x01, 0x03), (0x02, 0x02), (0x03, 0x00), (0x04, 0x01), (0x05, 0x00),
                (0x06, 0xAB), (0x07, 0xCD), (0x08, 0xEF), (0x09, 0xFF), (0x0C, 0xFF), (0x0D, 0x3F), (0x0F, 0xFF),
                (0x10, 0xFF), (0x13, 0xFF), (0x17, 0xFF), (0x19, 0xFF), (0x1A, 0xFF), (0x1B, 0xFF), (0x1D, 0xFF),
                (0x1E, 0x32), (0x1F, 0x32));
            Assert.Equal(Hex(expected), Hex(AocMousematLighting.Direct(0xABCDEF)));
        }

        [Fact]
        public void TheMat_IsOneEdgeLight_LitByOneFeatureReport()
        {
            var model = AocMousematLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(AocMousematLighting.OpenRgbFamily).Single(), feature: 32));
            Assert.Equal(LightKind.Edge, Assert.Single(model.Map.Lights).Kind);
            Assert.Null(AocMousematLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(AocMousematLighting.OpenRgbFamily).Single(), feature: 31)));
            var io = new FakeReportChannel { FeatureReportLength = 32 };
            var session = model.Begin(io, _ => { });
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x0102FF }));
            Assert.Equal(Hex(AocMousematLighting.Direct(0x0102FF)), Hex(Calls(io).Single().Bytes));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Single(io.Calls);
        }
    }
}
