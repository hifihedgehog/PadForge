using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The VSG Mintaka against OpenRGB's MintakaKeyboardController (#557).</summary>
    public class MintakaKeyboardLightingTests
    {
        private static HidLightingModel Model()
            => MintakaKeyboardLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(MintakaKeyboardLighting.OpenRgbFamily).Single(), feature: 65));

        [Fact]
        public void TheRegistration_AnswersWithTheModel_WhichHoldsItsPictures()
        {
            var r = OpenRgbRegistrations.Of(MintakaKeyboardLighting.OpenRgbFamily).Single();
            // MintakaKeyboardControllerDetect.cpp:44.
            Assert.Equal((0x05AC, 0x0256, 0, 0x0001, 0x06), (r.VendorId, r.ProductId, r.Interface, r.UsagePage, r.Usage));
            var model = Model();
            Assert.Equal("VSG Mintaka", model.Name);
            Assert.Same(model, HidLightingFamilies.Match(CollectionFor(r, feature: 65)));
            Assert.Null(MintakaKeyboardLighting.Instance.Match(CollectionFor(r, feature: 33)));
            // Every picture lands in the keyboard's memory.
            Assert.Equal(150, model.HoldMs);
            Assert.Equal(500, model.RepaintMs);
            Assert.Empty(model.Programs);
        }

        [Fact]
        public void TheKeys_AreOpenRgbsIsoLayout_AtTheirColorPlaces()
        {
            var lights = Model().Map.Lights;
            // mintaka_offset_values (RGBController_MintakaKeyboard.cpp:21-34).
            Assert.Equal(19, lights.First(l => l.Usage == 0x29).Address);   // Escape
            Assert.Equal(20, lights.First(l => l.Usage == 0x1E).Address);   // 1
            Assert.Equal(103, lights.First(l => l.Usage == 0x2A).Address);  // Backspace
            Assert.Equal(85, lights.First(l => l.Usage == 0x28).Address);   // Enter
            Assert.Equal(94, lights.First(l => l.Usage == 0x2C).Address);   // Space
            Assert.Equal(lights.Count, lights.Select(l => l.Address).Distinct().Count());
            // The ISO key between Left Shift and Z, KEY_NORD_ANGLE_BRACKET at
            // 109 (lines 30-31 and 51), is named by a legend, not a usage.
            Assert.Contains(lights, l => l.Address == 109 && l.Usage == 0);
            Assert.All(lights, l => Assert.InRange(l.Address * 4 + 3, 0, MintakaKeyboardLighting.ColorBufSize - 1));
        }

        [Fact]
        public void APicture_RewritesTheEffectArea_AsOpenRgbsSetModeDoes()
        {
            var lights = Model().Map.Lights;
            var colors = Distinct(lights.Count);
            var steps = MintakaKeyboardLighting.Picture(lights, colors);
            // 04 18 and a read, 04 13 with 0x12 at byte 8 and a read
            // (MintakaKeyboardController.cpp:232-261).
            Assert.Equal("00 04 18 00", Hex(steps[0], 4));
            Assert.Null(steps[1]);
            Assert.Equal("00 04 13 00 00 00 00 00 00 12", Hex(steps[2], 10));
            Assert.Null(steps[3]);
            // Five effect pages, four effects of 16 bytes each, then three
            // empty reports, nine color reports, the active page, 04 02 and
            // a read, 04 F0 (lines 113-218).
            Assert.Equal(4 + 5 + 3 + 9 + 1 + 2 + 1, steps.Count);
            Assert.Equal("00 01 00 00 00 00 00 00 00 00 0F 00 00 00 00 AA 55 02",
                Hex(steps[4], 18));
            Assert.Equal(0x80, steps[8][1 + 48]);
            Assert.Equal(0x0F, steps[8][1 + 48 + 9]);
            Assert.All(steps.Skip(9).Take(3), s => Assert.All(s, b => Assert.Equal(0, b)));
            // The color pages: 80 RR GG BB per place, Escape at 19.
            var escape = colors[lights.ToList().FindIndex(l => l.Usage == 0x29)];
            Assert.Equal(new byte[] { 0x80, (byte)(escape >> 16), (byte)(escape >> 8), (byte)escape },
                steps[13].Skip(1 + 76 - 64).Take(4));
            Assert.Equal(0x80, steps[12 + 8][1 + 60]);
            // The active page is the Off effect's, at brightness 15 (lines 140-152 and 204-206).
            Assert.Equal("00 80 00 00 00 00 00 00 00 00 0F 00 00 00 00 AA 55 00", Hex(steps[21], 18));
            Assert.Equal("00 04 02 00", Hex(steps[22], 4));
            Assert.Null(steps[23]);
            Assert.Equal("00 04 F0 00", Hex(steps[24], 4));
            Assert.All(steps.Where(s => s != null), s => Assert.Equal(65, s.Length));
        }

        [Fact]
        public void ASession_SendsEachStep_TenMillisecondsApart_AndHandsBackNothing()
        {
            var model = Model();
            var io = new FakeReportChannel { FeatureReportLength = 65, Feature = _ => new byte[65] };
            var waits = new System.Collections.Generic.List<int>();
            var session = model.Begin(io, Recorder(waits));
            Assert.Empty(io.Calls);
            Assert.True(session.Feature);
            var colors = Distinct(model.Map.Lights.Count);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, Recorder(waits), model.Map.Lights, colors));
            Assert.Equal(22, Calls(io).Count(c => c.Kind == "set"));
            Assert.Equal(3, Calls(io).Count(c => c.Kind == "get"));
            Assert.Equal(25, waits.Count);
            Assert.All(waits, w => Assert.Equal(10, w));
            int before = io.Calls.Count;
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(before, io.Calls.Count);
            Assert.Empty(session.CrashFrames);
            io.FailWrites = true;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, _ => { }, model.Map.Lights, colors));
        }
    }
}
