using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The GameSir Nova 2 Lite against OpenRGB's GameSirController
    /// (#557).</summary>
    public class GameSirLightingTests
    {
        [Fact]
        public void TheReport_IsOpenRgbs()
        {
            // 00 05 08 0A 01 03 R G B 00, then the low byte of the sum of
            // bytes 1 to 9, 65 bytes (GameSirController.cpp:46-71).
            // 05+08+0A+01+03+11+22+33 = 0x81.
            var frame = GameSirLighting.Frame(0x112233);
            Assert.Equal(65, frame.Length);
            Assert.Equal("00 05 08 0A 01 03 11 22 33 00 81 00", Hex(frame, 12));
            // FF FF FF: 0x1B + 0x2FD = 0x318, low byte 18.
            Assert.Equal(0x18, GameSirLighting.Frame(0xFFFFFF)[10]);
            Assert.All(frame.Skip(11), b => Assert.Equal(0, b));
        }

        [Fact]
        public void BothRegistrations_AreOnePadEach_LitByOneOutputReport()
        {
            var registrations = OpenRgbRegistrations.Of(GameSirLighting.OpenRgbFamily).ToList();
            Assert.Equal(new[] { 0x100F, 0x1098 }, registrations.Select(r => r.ProductId));
            var wired = GameSirLighting.Instance.Match(CollectionFor(registrations[0], output: 65));
            var wireless = GameSirLighting.Instance.Match(CollectionFor(registrations[1], output: 65));
            Assert.NotSame(wired, wireless);
            Assert.Null(GameSirLighting.Instance.Match(CollectionFor(registrations[0], output: 64)));
            var io = new FakeReportChannel { OutputReportLength = 65 };
            var session = wireless.Begin(io, _ => { });
            Assert.Empty(io.Calls);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, wireless.Map.Lights, new[] { 0x112233 }));
            Assert.Equal(("out", Hex(GameSirLighting.Frame(0x112233))), (Calls(io).Single().Kind, Hex(Calls(io).Single().Bytes)));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Single(io.Calls);
        }
    }
}
