using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Patriot Viper V550 against OpenRGB's
    /// PatriotViperMouseController (#557).</summary>
    public class PatriotViperMouseLightingTests
    {
        [Fact]
        public void TheLedReports_AreOpenRgbs_WithTheirCheckByte()
        {
            // 01 13, index, FF, R G B. Byte 63 the xor of 0 to 62, plus 1
            // when even, minus 1 when odd (PatriotViperMouseController.cpp:50-85).
            // 01^13^00^FF^11^22^33 = ED, odd: EC.
            var first = PatriotViperMouseLighting.LedFrame(0, 0x112233);
            Assert.Equal("01 13 00 FF 11 22 33 00", Hex(first, 8));
            Assert.Equal(0xEC, first[63]);
            // 01^13^01^FF = EC, even: ED.
            Assert.Equal(0xED, PatriotViperMouseLighting.LedFrame(1, 0)[63]);
            // 01^13^06^FF = EB, odd: EA.
            Assert.Equal(0xEA, PatriotViperMouseLighting.LedFrame(6, 0)[63]);
            Assert.All(first.Skip(7).Take(56), b => Assert.Equal(0, b));
        }

        [Fact]
        public void ASession_SendsTheInitReport_ThenSevenReportsAPicture()
        {
            var model = PatriotViperMouseLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(PatriotViperMouseLighting.OpenRgbFamily).Single(), feature: 64));
            Assert.Same(VendorPrograms.PatriotViper, model.Programs);
            Assert.Equal(new[] { 1, 1, 1, 2, 2, 2, 3 }, model.Map.Lights.Select(l => l.Zone));
            var io = new FakeReportChannel { FeatureReportLength = 64 };
            var session = model.Begin(io, _ => { });
            var init = Calls(io).Single().Bytes;
            Assert.Equal("01 00 12 12 00 00 00 00 70 00 00 00 FF FF FF FF", Hex(init, 16));
            Assert.Equal("98 F5 19 08 00 00 00 EE", Hex(init.Skip(56).ToArray()));
            var colors = Distinct(7);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, colors));
            var leds = Calls(io).Skip(1).ToList();
            Assert.Equal(7, leds.Count);
            for (int i = 0; i < 7; i++)
                Assert.Equal(Hex(PatriotViperMouseLighting.LedFrame(i, colors[i])), Hex(leds[i].Bytes));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(8, io.Calls.Count);
            Assert.Null(model.Begin(new FakeReportChannel { FailWrites = true }, _ => { }));
        }
    }
}
