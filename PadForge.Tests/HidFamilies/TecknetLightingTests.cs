using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Tecknet M008 against OpenRGB's TecknetController (#557).</summary>
    public class TecknetLightingTests
    {
        [Fact]
        public void TheReport_IsOpenRgbs_WithInvertedColors()
        {
            // 02 04, 255 minus each of R, G, B, brightness 3, speed 0, 16
            // bytes (TecknetController.cpp:85-113).
            Assert.Equal("02 04 EE DD CC 03 00 00 00 00 00 00 00 00 00 00", Hex(TecknetLighting.Frame(0x112233)));
            Assert.Equal("02 04 FF FF FF 03 00", Hex(TecknetLighting.Frame(0), 7));
        }

        [Fact]
        public void ASession_SendsOneReportAPicture_ForTheLogo()
        {
            var registration = OpenRgbRegistrations.Of(TecknetLighting.OpenRgbFamily).Single();
            Assert.Equal(-1, registration.Interface);
            var model = TecknetLighting.Instance.Match(CollectionFor(registration, feature: 16));
            Assert.Equal(LightKind.Logo, Assert.Single(model.Map.Lights).Kind);
            var io = new FakeReportChannel { FeatureReportLength = 16 };
            var session = model.Begin(io, _ => { });
            Assert.Empty(io.Calls);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x112233 }));
            Assert.Equal(Hex(TecknetLighting.Frame(0x112233)), Hex(Calls(io).Single().Bytes));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Single(io.Calls);
        }
    }
}
