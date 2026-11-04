using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The AOC GM500 mouse against OpenRGB's AOCMouseController
    /// (#557).</summary>
    public class AocMouseLightingTests
    {
        [Fact]
        public void TheDirectReport_IsOpenRgbs()
        {
            // SendDirect: mode 0, speed 2, brightness 3, direction 0, the
            // constant bytes, logo at 0x33 and wheel at 0x36
            // (AOCMouseController.cpp:55-136).
            var expected = Bytes(60,
                (0x00, 0x20), (0x01, 0x03), (0x02, 0x01), (0x03, 0x00), (0x04, 0x02), (0x05, 0x03), (0x06, 0x00),
                (0x07, 0x01), (0x08, 0x02), (0x09, 0xFF), (0x0D, 0x01), (0x0E, 0x03), (0x0F, 0xFF), (0x10, 0x7F),
                (0x13, 0x01), (0x14, 0x04), (0x17, 0xFF), (0x19, 0x01), (0x1A, 0x05), (0x1C, 0xFF), (0x1F, 0x01),
                (0x20, 0x06), (0x21, 0xFF), (0x23, 0xFF), (0x25, 0x01), (0x26, 0x07), (0x27, 0xFF), (0x28, 0xFF),
                (0x2C, 0x0A), (0x2D, 0x0A), (0x30, 0x14), (0x31, 0x08), (0x3A, 0x32), (0x3B, 0x32),
                (0x33, 0x11), (0x34, 0x22), (0x35, 0x33), (0x36, 0x44), (0x37, 0x55), (0x38, 0x66));
            var lights = AocMouseLighting.Lights();
            Assert.Equal(Hex(expected), Hex(AocMouseLighting.Direct(lights, new[] { 0x112233, 0x445566 })));
        }

        [Fact]
        public void TheLights_AreTheLogoThenTheWheel()
        {
            var lights = AocMouseLighting.Lights();
            Assert.Equal((0, LightKind.Logo), (lights[0].Address, lights[0].Kind));
            Assert.Equal((1, LightKind.Zone, 1), (lights[1].Address, lights[1].Kind, lights[1].Zone));
            // The map's order is the color order; a reversed map still puts
            // each color by its address.
            var reversed = new[] { lights[1], lights[0] };
            var frame = AocMouseLighting.Direct(reversed, new[] { 0x445566, 0x112233 });
            Assert.Equal("11 22 33 44 55 66", Hex(frame.Skip(0x33).ToArray(), 6));
        }

        [Fact]
        public void ASession_SendsOneFeatureReportAPicture_AndNothingBack()
        {
            var model = AocMouseLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(AocMouseLighting.OpenRgbFamily).Single(), feature: 60));
            Assert.Null(AocMouseLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(AocMouseLighting.OpenRgbFamily).Single(), feature: 59)));
            Assert.Equal(0, model.HoldMs);
            Assert.Equal(AocMouseLighting.RepaintMs, model.RepaintMs);
            var io = new FakeReportChannel { FeatureReportLength = 60 };
            var session = model.Begin(io, _ => { });
            Assert.Empty(io.Calls);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 1, 2 }));
            Assert.Equal("set", Calls(io).Single().Kind);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Single(io.Calls);
            Assert.Empty(session.CrashFrames);
            io.FailWrites = true;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, _ => { }, model.Map.Lights, new[] { 1, 2 }));
        }
    }
}
