using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Glorious Model I against OpenRGB's
    /// LaviewTechnologyController (#557).</summary>
    public class LaviewTechnologyLightingTests
    {
        [Fact]
        public void TheCustomModeReport_IsOpenRgbs()
        {
            // A1 0C, profile 1 at 5, mode 1 at 16, RGB at 17, brightness 100,
            // the mode's most, at 56, speed 0 at 58 (LaviewTechnologyController.cpp:58-79).
            var expected = Bytes(64, (0, 0xA1), (1, 0x0C), (5, 0x01), (16, 0x01), (17, 0x12), (18, 0x34), (19, 0x56), (56, 100));
            Assert.Equal(Hex(expected), Hex(LaviewTechnologyLighting.Frame(0x123456)));
        }

        [Fact]
        public void TheModel_IsStored_AndLeavesTheMouseToGloriousCore()
        {
            var model = LaviewTechnologyLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(LaviewTechnologyLighting.OpenRgbFamily).Single(), feature: 64));
            Assert.Equal(SinowealthLighting.StoredHoldMs, model.HoldMs);
            Assert.Equal(SinowealthLighting.StoredRepaintMs, model.RepaintMs);
            Assert.Same(VendorPrograms.Glorious, model.Programs);
            var io = new FakeReportChannel { FeatureReportLength = 64 };
            var session = model.Begin(io, _ => { });
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x123456 }));
            Assert.Equal(Hex(LaviewTechnologyLighting.Frame(0x123456)), Hex(Calls(io).Single().Bytes));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Single(io.Calls);
        }
    }
}
