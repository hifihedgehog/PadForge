using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Lexip Np93 ALPHA against OpenRGB's LexipMouseController
    /// (#557).</summary>
    public class LexipMouseLightingTests
    {
        [Fact]
        public void TheReport_IsOpenRgbs_AnOutputReport()
        {
            // 00 24 01 RR GG BB 00 64 80, 64 bytes (LexipMouseController.cpp:51-75).
            var frame = LexipMouseLighting.Frame(0xA1B2C3);
            Assert.Equal(64, frame.Length);
            Assert.Equal("00 24 01 A1 B2 C3 00 64 80 00", Hex(frame, 10));
            Assert.All(frame.Skip(9), b => Assert.Equal(0, b));
            var model = LexipMouseLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(LexipMouseLighting.OpenRgbFamily).Single(), output: 64));
            var io = new FakeReportChannel { OutputReportLength = 64 };
            var session = model.Begin(io, _ => { });
            Assert.False(session.Feature);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0xA1B2C3 }));
            Assert.Equal(("out", Hex(frame)), (Calls(io).Single().Kind, Hex(Calls(io).Single().Bytes)));
        }

        [Fact]
        public void TheMouseCollection_IsNotOneTheWorkerWritesWithNoAccessRight()
        {
            // The registration is the mouse collection, page 1 usage 2, and
            // its frame is an output report, which a handle with no access
            // right does not carry.
            var registration = OpenRgbRegistrations.Of(LexipMouseLighting.OpenRgbFamily).Single();
            Assert.Equal((0x0001, 0x02), (registration.UsagePage, registration.Usage));
            var model = LexipMouseLighting.Instance.Match(CollectionFor(registration, output: 64));
            Assert.False(model.HeldByWindows);
            var held = new FakeReportChannel { OutputReportLength = 64, FeatureOnly = true };
            var session = model.Begin(held, _ => { });
            Assert.Equal(HidLightingPaint.Failed, session.Paint(held, _ => { }, model.Map.Lights, new[] { 1 }));
            Assert.Null(LexipMouseLighting.Instance.Match(CollectionFor(registration, output: 63)));
        }
    }
}
