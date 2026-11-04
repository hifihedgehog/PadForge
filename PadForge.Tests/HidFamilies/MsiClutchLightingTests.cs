using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The MSI Clutch GM11 against OpenRGB's MSIClutchController
    /// (#557).</summary>
    public class MsiClutchLightingTests
    {
        [Fact]
        public void TheStaticSequence_IsOpenRgbs_WithEachChecksum()
        {
            // SetMode for Static, red 11 green 22 blue 33
            // (MSIClutchGM11Controller.cpp:88-271): each feature report is
            // 00 and eight bytes whose last is FF minus the sum of the seven
            // before; the palette is a 33-byte output report.
            var frames = MsiClutchLighting.StaticFrames(0x112233);
            Assert.Equal(new[] { true, true, true, true, false, true }, frames.Select(f => f.Feature));
            Assert.Equal(new[]
            {
                "00 03 03 0F F2 40 00 00 B8",
                "00 85 02 00 00 00 00 00 78",
                "00 05 02 00 FF 00 00 00 F9",
                "00 10 00 18 00 00 00 00 D7",
                "00 FF 00 00 FF FF 00 00 FF 00 00 FF FF 00 00 FF FF 00 FF FF FF FF 11 22 33 00 00 00 00 00 00 00 D0",
                "00 05 02 08 07 00 00 00 E9",
            }, frames.Select(f => Hex(f.Report)));
        }

        [Fact]
        public void ASession_SendsTheSequence_FiveMillisecondsAfterEachReport()
        {
            var model = MsiClutchLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(MsiClutchLighting.OpenRgbFamily).Single(), output: 33, feature: 9));
            Assert.Equal(SinowealthLighting.StoredHoldMs, model.HoldMs);
            Assert.Same(VendorPrograms.MsiClutch, model.Programs);
            var io = new FakeReportChannel { OutputReportLength = 33, FeatureReportLength = 9 };
            var waits = new List<int>();
            var session = model.Begin(io, Recorder(waits));
            Assert.Empty(io.Calls);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, Recorder(waits), model.Map.Lights, new[] { 0x112233 }));
            Assert.Equal(new[] { "set", "set", "set", "set", "out", "set" }, Calls(io).Select(c => c.Kind));
            Assert.Equal(Enumerable.Repeat(5, 6), waits);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(6, io.Calls.Count);
            io.FailWrites = true;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, _ => { }, model.Map.Lights, new[] { 1 }));
            // Short reports of either kind are not the collection the
            // sequence reaches.
            var r = OpenRgbRegistrations.Of(MsiClutchLighting.OpenRgbFamily).Single();
            Assert.Null(MsiClutchLighting.Instance.Match(CollectionFor(r, output: 32, feature: 9)));
            Assert.Null(MsiClutchLighting.Instance.Match(CollectionFor(r, output: 33, feature: 8)));
        }
    }
}
