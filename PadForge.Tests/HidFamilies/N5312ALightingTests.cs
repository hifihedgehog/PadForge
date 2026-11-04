using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The N5312A mice against OpenRGB's N5312AController (#557).</summary>
    public class N5312ALightingTests
    {
        [Fact]
        public void TheReports_AreOpenRgbs()
        {
            // SendInit, SetColor and SetMode (N5312AController.cpp:54-104).
            Assert.Equal("07 A0 00 00 00 00 00 00", Hex(N5312ALighting.Init()));
            Assert.Equal("07 0B 01 11 22 33 00 00", Hex(N5312ALighting.Color(0x112233)));
            Assert.Equal("07 0A 02 01 01 01 00 64", Hex(N5312ALighting.Mode(0x02, 0x64, 0)));
        }

        [Fact]
        public void ASession_InitsOnBegin_SelectsDirectWithItsFirstPicture_ThenSendsColors()
        {
            var model = N5312ALighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(N5312ALighting.OpenRgbFamily).Single(), feature: 8));
            var io = new FakeReportChannel { FeatureReportLength = 8 };
            var session = model.Begin(io, _ => { });
            Assert.Equal("07 A0 00 00 00 00 00 00", Hex(Calls(io).Single().Bytes));
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x112233 }));
            Assert.Equal(new[] { "07 0B 01 11 22 33 00 00", "07 0A 02 01 01 01 00 64", "07 0B 01 11 22 33 00 00" },
                Calls(io).Skip(1).Select(c => Hex(c.Bytes)));
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x445566 }));
            Assert.Equal("07 0B 01 44 55 66 00 00", Hex(Calls(io).Last().Bytes));
            Assert.Equal(5, io.Calls.Count);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(5, io.Calls.Count);
            Assert.Null(model.Begin(new FakeReportChannel { FailWrites = true }, _ => { }));
        }
    }
}
