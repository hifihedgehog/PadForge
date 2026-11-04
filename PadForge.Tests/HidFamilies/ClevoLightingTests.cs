using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.LaptopFamilies.LaptopFamilyTest;

namespace PadForge.Tests.LaptopFamilies
{
    /// <summary>The Clevo keyboards against OpenRGB's
    /// ClevoKeyboardController (#557): the layouts by controller and DMI
    /// SKU (RGBController_ClevoKeyboard.cpp:375-434), and SendColors'
    /// reports (ClevoKeyboardController.cpp:366-435).</summary>
    public class ClevoLightingTests
    {
        private static HidCollectionInfo Of(OpenRgbRegistration r)
            => CollectionOf(r, feature: (ushort)(r.ProductId == 0x8910 ? 6 : 8), output: 65);

        [Fact]
        public void EveryRegistration_FindsItsModel()
        {
            var registrations = OpenRgbRegistrations.Of(ClevoLighting.OpenRgbFamily).ToList();
            Assert.Equal(2, registrations.Count);
            var family = new ClevoLighting(() => "");
            foreach (var r in registrations)
            {
                var model = family.Match(Of(r));
                Assert.NotNull(model);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(model, family.Match(Of(r)));
                Assert.Equal(50, model.RepaintMs);
                Assert.Empty(model.Programs);
            }
            var ite8291 = registrations.Single(r => r.ProductId == 0x600B);
            Assert.Null(family.Match(CollectionOf(ite8291, feature: 8, output: 64)));
            Assert.Null(family.Match(CollectionOf(ite8291, feature: 7, output: 65)));
            var ite829x = registrations.Single(r => r.ProductId == 0x8910);
            Assert.Null(family.Match(CollectionOf(ite829x, feature: 5)));
            Assert.NotNull(family.Match(CollectionOf(ite829x, feature: 6)));
            Assert.Null(family.Match(CollectionOf(ite829x, feature: 6) with { Usage = 0x01 }));
        }

        [Fact]
        public void TheSku_PicksTheStellarisLayout_AndAFailedReadIsAskedAgain()
        {
            var r = OpenRgbRegistrations.Of(ClevoLighting.OpenRgbFamily).Single(x => x.ProductId == 0x600B);
            Assert.Equal("clevo:048D:600B:STELLARIS16A07", new ClevoLighting(() => "STELLARIS16A07").Match(Of(r)).Map.Product);
            Assert.Equal("clevo:048D:600B", new ClevoLighting(() => "STELLARIS16A06").Match(Of(r)).Map.Product);
            int reads = 0;
            string sku = null;
            var family = new ClevoLighting(() => { reads++; return sku; });
            Assert.Null(family.Match(Of(r)));
            sku = "";
            Assert.NotNull(family.Match(Of(r)));
            Assert.NotNull(family.Match(Of(r)));
            Assert.Equal(2, reads);
            // The 829x reads no SKU.
            var ite829x = OpenRgbRegistrations.Of(ClevoLighting.OpenRgbFamily).Single(x => x.ProductId == 0x8910);
            Assert.NotNull(new ClevoLighting(() => throw new System.InvalidOperationException()).Match(Of(ite829x)));
        }

        [Fact]
        public void TheLayouts_AreOpenRgbsInTheIsoRegion()
        {
            Assert.Equal("", ClevoLighting.LayoutKey(false, ""));
            Assert.Equal("STELLARIS16A07", ClevoLighting.LayoutKey(false, "STELLARIS16A07"));
            Assert.Equal("CLEVO_KB_ITE829X", ClevoLighting.LayoutKey(true, "STELLARIS16A07"));
            var tkl = ClevoLighting.Lights("");
            Assert.Equal(102, tkl.Count);
            Assert.Equal(KeyboardLayouts.Build("clevo_keyboard_layout", KeyboardLayout.IsoQwerty).Keys.Select(k => (int)k.Value),
                tkl.Select(l => l.Address));
            Assert.Equal(102, ClevoLighting.Lights("STELLARIS16A07").Count);
            // Escape takes value 105 on the 8291's TKL layout (ClevoKeyboardDevices.cpp:42).
            Assert.Equal(105, tkl.Single(l => l.Usage == 0x29).Address);
            Assert.All(tkl, l => Assert.True(l.HasPosition));
        }

        /// <summary>The 829x layout builds 105 keys, five with the value 0:
        /// Escape, whose LED ID is 0, and the keys it has no LED for. Escape
        /// keeps the light, so a color given to Escape reaches LED 0.</summary>
        [Fact]
        public void The829xLayout_LeavesOutTheKeysWithNoLed()
        {
            var built = KeyboardLayouts.Build("clevo_829x_keyboard_layout", KeyboardLayout.IsoQwerty).Keys;
            Assert.Equal(105, built.Count);
            Assert.Equal(5, built.Count(k => k.Value == 0));
            var lights = ClevoLighting.Lights("CLEVO_KB_ITE829X");
            Assert.Equal(101, lights.Count);
            var zero = Assert.Single(lights, l => l.Address == 0);
            Assert.Equal((ushort)0x29, zero.Usage);
            Assert.DoesNotContain(lights, l => l.Usage is 0x47 or 0x48 or 0x32 or 0x64);
            var frames = ClevoLighting.Frames829x(lights, lights.Select(l => l.Usage == 0x29 ? 0xFF0000 : 0).ToList());
            Assert.Equal("CC 01 00 FF 00 00", Hex(frames[1]));
        }

        /// <summary>08 02 33 00 32: Direct at full brightness; per row
        /// 16 00 row and an output report of 21 blues, 21 greens and 21
        /// reds from byte 2, each at the column of the key's value.</summary>
        [Fact]
        public void The8291_TakesTheModeThenSixRows()
        {
            var lights = ClevoLighting.Lights("");
            // Escape (value 105: row 5, column 0) red, Left Control (value 0)
            // green.
            var colors = lights.Select(l => l.Usage == 0x29 ? 0xFF0000 : l.Usage == 0xE0 ? 0x00AA00 : 0).ToList();
            var frames = ClevoLighting.Frames8291(lights, colors);
            Assert.Equal(13, frames.Count);
            Assert.True(frames[0].Feature);
            Assert.Equal("08 02 33 00 32 00 00 00", Hex(frames[0].Report));
            for (int row = 0; row < 6; row++)
            {
                Assert.True(frames[1 + 2 * row].Feature);
                Assert.Equal($"16 00 0{row} 00 00 00 00 00", Hex(frames[1 + 2 * row].Report));
                Assert.False(frames[2 + 2 * row].Feature);
                Assert.Equal(65, frames[2 + 2 * row].Report.Length);
            }
            var row0 = frames[2].Report;
            Assert.Equal(0xAA, row0[2 + 21]);
            Assert.Equal(0xAA, row0.Sum(b => b));
            var row5 = frames[12].Report;
            Assert.Equal(0xFF, row5[2 + 42]);
            Assert.Equal(0xFF, row5.Sum(b => b));
        }

        /// <summary>CC 09 0A 00: full brightness at speed 0; then CC 01, the
        /// LED ID and the color for each of the 6 by 20 cells, row by row.</summary>
        [Fact]
        public void The829x_TakesTheBrightnessThenEveryCell()
        {
            var lights = ClevoLighting.Lights("CLEVO_KB_ITE829X");
            var colors = lights.Select(l => l.Usage == 0x2C ? 0x123456 : 0).ToList();
            var frames = ClevoLighting.Frames829x(lights, colors);
            Assert.Equal(121, frames.Count);
            Assert.Equal("CC 09 0A 00 00 00", Hex(frames[0]));
            Assert.Equal("CC 01 B3 00 00 00", Hex(frames[120]));
            Assert.Equal("CC 01 20 00 00 00", Hex(frames[21]));
            // Space takes the value 164, 0xA4, in OpenRGB's own build of the
            // layout (tools/openrgb_klm/klm_dump.tsv): row 5, column 4.
            Assert.Equal(0xA4, lights.Single(l => l.Usage == 0x2C).Address);
            Assert.Equal("CC 01 A4 12 34 56", Hex(frames[1 + 5 * 20 + 4]));
            Assert.Equal(0xA4, ClevoLighting.LedId829x(5, 4));
        }

        [Fact]
        public void TheSessions_SendTheirReports_AndHandBackNothing()
        {
            var tkl = ClevoLighting.Lights("");
            var io = new LoggingHidChannel();
            var s8291 = new ClevoLighting.Session(false);
            Assert.Equal(HidLightingPaint.Sent, s8291.Paint(io, io.Wait, tkl, new int[tkl.Count]));
            Assert.Equal(ClevoLighting.Frames8291(tkl, new int[tkl.Count]).Select(f => (f.Feature ? "F" : "O") + " " + Hex(f.Report)),
                io.Log.Select(e => e.Kind + " " + Hex(e.Bytes)));
            Assert.True(s8291.HandBack(io, io.Wait));
            Assert.Empty(s8291.CrashFrames);
            Assert.Equal(HidLightingPaint.Failed, s8291.Paint(new LoggingHidChannel { FailAfter = 2 }, io.Wait, tkl, new int[tkl.Count]));

            var full = ClevoLighting.Lights("CLEVO_KB_ITE829X");
            var io2 = new LoggingHidChannel();
            var s829x = new ClevoLighting.Session(true);
            Assert.Equal(HidLightingPaint.Sent, s829x.Paint(io2, io2.Wait, full, new int[full.Count]));
            Assert.Equal(121, io2.Sent("F").Count());
            Assert.Equal(HidLightingPaint.Failed, s829x.Paint(new LoggingHidChannel { FailAfter = 60 }, io2.Wait, full, new int[full.Count]));
        }
    }
}
