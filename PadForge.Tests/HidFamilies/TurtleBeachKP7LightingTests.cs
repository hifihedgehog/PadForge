using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Turtle Beach KP7 against OpenRGB's
    /// TurtleBeachKP7Controller (#557).</summary>
    public class TurtleBeachKP7LightingTests
    {
        private static HidLightingModel Model()
            => TurtleBeachKP7Lighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(TurtleBeachKP7Lighting.OpenRgbFamily).Single(), output: 64, feature: 5));

        [Fact]
        public void TheMap_IsOpenRgbsLightsBySlot()
        {
            var lights = Model().Map.Lights;
            Assert.Equal(33, lights.Count);
            // turtle_beach_kp7_led_order (RGBController_TurtleBeachKP7.cpp:37-48).
            Assert.Equal(new[]
            {
                28, 29, 30, 21, 0, 7, 14, 8, 22, 15, 1, 11, 23, 2, 9, 4, 18,
                10, 24, 16, 25, 5, 17, 3, 13, 12, 19, 27, 6, 20, 26, 31, 32,
            }, lights.Select(l => l.Address));
            Assert.Equal(33, lights.Select(l => l.Address).Distinct().Count());
            // The indicators, then F13 at the matrix's top left, the Number
            // Pad + and Enter keys twice each, Space under the lower Enter,
            // and the wrist rest (lines 62-74 and 131-147).
            Assert.Equal((LightKind.Status, LightLabel.NumLock), (lights[0].Kind, lights[0].Label));
            Assert.Equal(LightKind.Status, lights[1].Kind);
            Assert.Equal((LightKind.Other, (ushort)0), (lights[2].Kind, lights[2].Usage));
            Assert.Equal(((ushort)0x68, 0f, 0f, true), (lights[3].Usage, lights[3].X, lights[3].Y, lights[3].HasPosition));
            Assert.Equal(LightKind.Other, lights[6].Kind);
            Assert.Equal(2, lights.Count(l => l.Usage == 0x57));
            Assert.Equal(2, lights.Count(l => l.Usage == 0x58));
            var space = lights.Single(l => l.Usage == 0x2C);
            Assert.Equal((26, 3f, 6f), (space.Address, space.X, space.Y));
            var zero = lights.Single(l => l.Usage == 0x62);
            Assert.Equal((0f, 5f), (zero.X, zero.Y));
            Assert.Equal(new[] { LightKind.Edge, LightKind.Edge }, lights.Skip(31).Select(l => l.Kind));
            Assert.All(lights.Skip(3).Take(28), l => Assert.True(l.HasPosition));
            Assert.All(lights.Take(3).Concat(lights.Skip(31)), l => Assert.False(l.HasPosition));
            Assert.Equal(new[] { "Key: Profile Switch", "Key: Mode 1", "Key: Mode 2", "Key: Mode 3", "Key: Mode 4" },
                TurtleBeachKP7Lighting.LedNames.Where(n => n.StartsWith("Key: ") && !OpenRgbKeys.TryGet(n, out _, out _) && n != "Key: Shift Mode"));
        }

        [Fact]
        public void APicture_IsTwoPagesOfSlotColors()
        {
            var lights = Model().Map.Lights;
            var colors = Distinct(33);
            var frames = TurtleBeachKP7Lighting.Frames(lights, colors);
            Assert.Equal(2, frames.Length);
            Assert.All(frames, f => Assert.Equal(64, f.Length));
            Assert.Equal("EF 00", Hex(frames[0], 2));
            Assert.Equal("EF 01", Hex(frames[1], 2));
            string Rgb(int c) => Hex(new[] { (byte)(c >> 16), (byte)(c >> 8), (byte)c });
            // F14, the fifth light, in slot 0: page 0 at 2.
            Assert.Equal(Rgb(colors[4]), Hex(frames[0].Skip(2).ToArray(), 3));
            // F13 in slot 21: byte 63 of the buffer, page 1 at 3.
            Assert.Equal(Rgb(colors[3]), Hex(frames[1].Skip(3).ToArray(), 3));
            // Slot 20 straddles the pages: bytes 60 and 61 on page 0, 62 on page 1.
            int at20 = lights.ToList().FindIndex(l => l.Address == 20);
            Assert.Equal(Rgb(colors[at20]), Hex(new[] { frames[0][62], frames[0][63], frames[1][2] }));
            // The Num Lock indicator in slot 28: byte 84, page 1 at 24.
            Assert.Equal(Rgb(colors[0]), Hex(frames[1].Skip(24).ToArray(), 3));
            // Wrist rest 2 in slot 32: bytes 96 to 98, page 1 at 36; the rest is zero.
            Assert.Equal(Rgb(colors[32]), Hex(frames[1].Skip(36).ToArray(), 3));
            Assert.All(frames[1].Skip(39), b => Assert.Equal(0, b));
        }

        [Fact]
        public void ASession_EntersDirect_KeepsItAlive_AndHandsBackBySilence()
        {
            var model = Model();
            Assert.Equal(50, model.KeepAliveMs);
            Assert.Equal(0, model.KeepAliveIdleMs);
            Assert.Same(VendorPrograms.TurtleBeach, model.Programs);
            var io = new FakeReportChannel { OutputReportLength = 64, FeatureReportLength = 5 };
            var session = model.Begin(io, _ => { });
            Assert.Equal(("set", "0E 00 01 00 00"), (Calls(io).Single().Kind, Hex(Calls(io).Single().Bytes)));
            var colors = Distinct(33);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, colors));
            Assert.True(session.KeepAlive(io, _ => { }, model.Map.Lights, colors));
            var outs = Calls(io).Where(c => c.Kind == "out").Select(c => Hex(c.Bytes)).ToList();
            Assert.Equal(4, outs.Count);
            Assert.Equal(outs.Take(2), outs.Skip(2));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(5, io.Calls.Count);
            Assert.Empty(session.CrashFrames);
            Assert.False(session.Feature);
            Assert.Null(model.Begin(new FakeReportChannel { FailWrites = true }, _ => { }));
        }

        [Fact]
        public void OnlyTheCollectionThatTakesBothReports_IsTheKeypads()
        {
            var r = OpenRgbRegistrations.Of(TurtleBeachKP7Lighting.OpenRgbFamily).Single();
            Assert.Equal((4, -1, -1), (r.Interface, r.UsagePage, r.Usage));
            Assert.NotNull(TurtleBeachKP7Lighting.Instance.Match(CollectionFor(r, output: 64, feature: 5)));
            Assert.Null(TurtleBeachKP7Lighting.Instance.Match(CollectionFor(r, output: 63, feature: 5)));
            Assert.Null(TurtleBeachKP7Lighting.Instance.Match(CollectionFor(r, output: 64, feature: 4)));
            Assert.Null(TurtleBeachKP7Lighting.Instance.Match(CollectionFor(r, output: 0, feature: 64)));
        }
    }
}
