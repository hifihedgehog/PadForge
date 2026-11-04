using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Lego Dimensions Toypad Base against OpenRGB's
    /// LegoDimensionsToypadBaseController (#557).</summary>
    public class LegoDimensionsToypadLightingTests
    {
        [Fact]
        public void TheReports_AreOpenRgbs()
        {
            // Activate: 00 55 0F B0 01 "(c) LEGO 2014" F7 (LegoDimensionsToypadBaseController.cpp:53-79).
            var activate = LegoDimensionsToypadLighting.Activate();
            Assert.Equal(32, activate.Length);
            Assert.Equal("00 55 0F B0 01 28 63 29 20 4C 45 47 4F 20 32 30 31 34 F7 00", Hex(activate, 20));
            // SetDirect pad 2, 11 22 33: 55+06+C0+02+02+11+22+33 = 0x185, low byte 85 (lines 81-102).
            Assert.Equal("00 55 06 C0 02 02 11 22 33 85 00", Hex(LegoDimensionsToypadLighting.Direct(2, 0x112233), 11));
            Assert.All(LegoDimensionsToypadLighting.Direct(2, 0x112233).Skip(10), b => Assert.Equal(0, b));
        }

        [Fact]
        public void ASession_Activates_ThenLightsThePadsInOrder()
        {
            var r = OpenRgbRegistrations.Of(LegoDimensionsToypadLighting.OpenRgbFamily).Single();
            var model = LegoDimensionsToypadLighting.Instance.Match(CollectionFor(r, output: 32));
            Assert.Null(LegoDimensionsToypadLighting.Instance.Match(CollectionFor(r, output: 31)));
            Assert.Equal(new[] { 1, 2, 3 }, model.Map.Lights.Select(l => l.Address));
            Assert.Equal(new[] { 1, 2, 3 }, model.Map.Lights.Select(l => l.Zone));
            var io = new FakeReportChannel { OutputReportLength = 32 };
            var session = model.Begin(io, _ => { });
            Assert.Equal(Hex(LegoDimensionsToypadLighting.Activate()), Hex(Calls(io).Single().Bytes));
            var colors = new[] { 0x010101, 0x020202, 0x030303 };
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, colors));
            Assert.Equal(new[] { 1, 2, 3 }.Select(p => Hex(LegoDimensionsToypadLighting.Direct(p, colors[p - 1]))),
                Calls(io).Skip(1).Select(c => Hex(c.Bytes)));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(4, io.Calls.Count);
            Assert.Null(model.Begin(new FakeReportChannel { FailWrites = true }, _ => { }));
        }
    }
}
