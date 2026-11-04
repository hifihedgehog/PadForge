using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.LaptopFamilies.LaptopFamilyTest;

namespace PadForge.Tests.LaptopFamilies
{
    /// <summary>The Aorus laptops against OpenRGB's
    /// GigabyteAorusLaptopController (#557): SetDirect, SetCustom and SetMode
    /// (GigabyteAorusLaptopController.cpp:70-274) and the keyboard layout
    /// (RGBController_GigabyteAorusLaptop.cpp:42-70).</summary>
    public class GigabyteAorusLaptopLightingTests
    {
        [Fact]
        public void EveryRegistration_FindsItsModel()
        {
            var registrations = OpenRgbRegistrations.Of(GigabyteAorusLaptopLighting.OpenRgbFamily).ToList();
            Assert.Equal(4, registrations.Count);
            foreach (var r in registrations)
            {
                var model = GigabyteAorusLaptopLighting.Instance.Match(CollectionOf(r, feature: 9, output: 65));
                Assert.NotNull(model);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(model, HidLightingFamilies.Match(CollectionOf(r, feature: 9, output: 65)));
                Assert.Equal(150, model.HoldMs);
                Assert.Equal(500, model.RepaintMs);
                bool keyboard = r.Name.EndsWith("Keyboard");
                Assert.Equal(keyboard ? 101 : 1, model.Map.Lights.Count);
                Assert.Null(GigabyteAorusLaptopLighting.Instance.Match(CollectionOf(r, feature: 8, output: 65)));
                if (keyboard) Assert.Null(GigabyteAorusLaptopLighting.Instance.Match(CollectionOf(r, feature: 9, output: 64)));
                else Assert.NotNull(GigabyteAorusLaptopLighting.Instance.Match(CollectionOf(r, feature: 9)));
            }
        }

        [Fact]
        public void TheKeyboard_IsOpenRgbsLayout()
        {
            var lights = GigabyteAorusLaptopLighting.KeyboardLights();
            Assert.Equal(GigabyteAorusLaptopLighting.Positions.Select(p => (int)p), lights.Select(l => l.Address));
            // Escape: slot 11, top left (led_sequence_positions line 63).
            Assert.Equal((ushort)0x29, lights[0].Usage);
            Assert.Equal(11, lights[0].Address);
            Assert.Equal((0f, 0f), (lights[0].X, lights[0].Y));
            Assert.All(lights, l => Assert.True(l.HasPosition));
            Assert.Equal(101, lights.Select(l => l.Address).Distinct().Count());
            var fn = lights.Single(l => l.Kind == LightKind.Other);
            Assert.Equal(LightLabel.Fn, fn.Label);
        }

        /// <summary>00 08 01 R G B 32 00, then 0xFF less the sum of bytes 1
        /// to 7.</summary>
        [Fact]
        public void TheReports_CarryTheirChecksum()
        {
            Assert.Equal("00 08 01 11 22 33 32 00 5E", Hex(GigabyteAorusLaptopLighting.DirectFrame(0x112233)));
            Assert.Equal("00 08 00 33 01 32 05 01 8B", Hex(GigabyteAorusLaptopLighting.CustomFrame()));
            // OpenRGB's own two literals hold the same checksum rule.
            Assert.Equal(GigabyteAorusLaptopLighting.Checked(0x92, 0, 0, 0, 0, 0, 0), GigabyteAorusLaptopLighting.StartFrame);
            Assert.Equal(GigabyteAorusLaptopLighting.Checked(0x12, 0, 0, 0x08, 0, 0, 0), GigabyteAorusLaptopLighting.SecondFrame);
        }

        [Theory]
        [InlineData(0xFF0000, 0x01)]
        [InlineData(0x00FF00, 0x02)]
        [InlineData(0x0000FF, 0x04)]
        [InlineData(0x00FFFF, 0x04)]
        [InlineData(0xFFFF00, 0x05)]
        [InlineData(0xFF00FF, 0x06)]
        [InlineData(0xFFFFFF, 0x07)]
        [InlineData(0x000000, 0x01)]
        public void TheIndex_IsGetColourIndex(int rgb, int index)
            => Assert.Equal((byte)index, GigabyteAorusLaptopLighting.ColorIndex(rgb));

        /// <summary>08 00 01, speed 0, brightness, index, direction 1;
        /// black at brightness 0.</summary>
        [Fact]
        public void TheBacklight_TakesTheStaticMode()
        {
            Assert.Equal(GigabyteAorusLaptopLighting.Checked(0x08, 0x00, 0x01, 0x00, 0x32, 0x05, 0x01),
                GigabyteAorusLaptopLighting.StaticFrame(0xFFFF00));
            Assert.Equal("00 08 00 01 00 00 01 01", Hex(GigabyteAorusLaptopLighting.StaticFrame(0), 8));
            var io = new LoggingHidChannel();
            var session = new GigabyteAorusLaptopLighting.Session(keyboard: false);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, GigabyteAorusLaptopLighting.BacklightLights(), new[] { 0xFF0000 }));
            Assert.Equal(new[] { GigabyteAorusLaptopLighting.StaticFrame(0xFF0000) }, io.Sent("F"));
        }

        [Fact]
        public void OneColor_GoesDirect_AndManyGoCustom()
        {
            var lights = GigabyteAorusLaptopLighting.KeyboardLights();
            var io = new LoggingHidChannel();
            var session = new GigabyteAorusLaptopLighting.Session(keyboard: true);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, Enumerable.Repeat(0x123456, lights.Count).ToList()));
            Assert.Equal(new[] { GigabyteAorusLaptopLighting.DirectFrame(0x123456) }, io.Sent("F"));

            io.Log.Clear();
            io.Answers.Enqueue(new byte[9]);
            var colors = lights.Select(l => l.Usage == 0x29 ? 0xAABBCC : 0).ToList();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, colors));
            Assert.Equal(new[] { "F", "F", "G", "F", "O", "O", "O", "O", "O", "O", "O", "O" }, io.Log.Select(e => e.Kind));
            Assert.Equal(GigabyteAorusLaptopLighting.CustomFrame(), io.Log[0].Bytes);
            Assert.Equal(GigabyteAorusLaptopLighting.StartFrame, io.Log[1].Bytes);
            Assert.Equal(GigabyteAorusLaptopLighting.SecondFrame, io.Log[3].Bytes);
            // Escape's slot 11: buffer bytes 45 to 47, the first report's
            // bytes 46 to 48 after its report ID.
            var first = io.Log[4].Bytes;
            Assert.Equal(65, first.Length);
            Assert.Equal("00 AA BB CC", Hex(first.Skip(45).Take(4).ToArray()));
            Assert.Equal(0xAA + 0xBB + 0xCC, io.Log.Skip(4).Sum(e => e.Bytes.Sum(b => b)));
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(session.CrashFrames);
            // An unanswered read fails the picture.
            Assert.Equal(HidLightingPaint.Failed, session.Paint(new LoggingHidChannel(), io.Wait, lights, colors));
        }
    }
}
