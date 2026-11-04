using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.LaptopFamilies.LaptopFamilyTest;

namespace PadForge.Tests.LaptopFamilies
{
    /// <summary>The MSI 3-zone laptop keyboard against OpenRGB's
    /// MSI3ZoneController (#557): SetLEDs' seven reports
    /// (MSI3ZoneController.cpp:51-100).</summary>
    public class Msi3ZoneLightingTests
    {
        [Fact]
        public void TheRegistration_FindsTheModel()
        {
            var r = Assert.Single(OpenRgbRegistrations.Of(Msi3ZoneLighting.OpenRgbFamily));
            var model = Msi3ZoneLighting.Instance.Match(CollectionOf(r, feature: 8));
            Assert.NotNull(model);
            Assert.Equal("MSI 3-Zone Laptop", model.Name);
            Assert.Same(model, HidLightingFamilies.Match(CollectionOf(r, feature: 8)));
            Assert.Equal(50, model.RepaintMs);
            Assert.Equal(0, model.HoldMs);
            Assert.Empty(model.Programs);
            // A collection with shorter feature reports, such as one with
            // none, is not the one SetLEDs writes to.
            Assert.Null(Msi3ZoneLighting.Instance.Match(CollectionOf(r, feature: 7)));
            Assert.Null(Msi3ZoneLighting.Instance.Match(CollectionOf(r) with { ProductId = 0xFF01 }));
        }

        [Fact]
        public void TheLights_AreLeftMiddleRightAndAux()
        {
            var lights = Msi3ZoneLighting.Lights();
            Assert.Equal(new[] { 0, 1, 2, 3 }, lights.Select(l => l.Address));
            Assert.Equal(new[] { 1, 2, 3, 4 }, lights.Select(l => l.Zone));
            Assert.All(lights, l => Assert.Equal(LightKind.Zone, l.Kind));
        }

        /// <summary>01 02 40, region, R G B, EC, for regions 1 to 7, the last
        /// three with the fourth color left in the buffer.</summary>
        [Fact]
        public void APicture_IsSevenReports()
        {
            var frames = Msi3ZoneLighting.Frames(new[] { 0x112233, 0x445566, 0x778899, 0xAABBCC });
            Assert.Equal(new[]
            {
                "01 02 40 01 11 22 33 EC",
                "01 02 40 02 44 55 66 EC",
                "01 02 40 03 77 88 99 EC",
                "01 02 40 04 AA BB CC EC",
                "01 02 40 05 AA BB CC EC",
                "01 02 40 06 AA BB CC EC",
                "01 02 40 07 AA BB CC EC",
            }, frames.Select(f => Hex(f)));
            // A fifth color is never sent: regions 5 to 7 repeat the fourth.
            var more = Msi3ZoneLighting.Frames(new[] { 1, 2, 3, 4, 0x555555 });
            Assert.All(more.Skip(3), f => Assert.Equal("00 00 04", Hex(f.Skip(4).Take(3).ToArray())));
        }

        [Fact]
        public void TheSession_SendsThemAll_AndHandsBackNothing()
        {
            var io = new LoggingHidChannel();
            var session = new Msi3ZoneLighting.Session();
            var colors = new[] { 1, 2, 3, 4 };
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, Msi3ZoneLighting.Lights(), colors));
            Assert.Equal(Msi3ZoneLighting.Frames(colors), io.Sent("F"));
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(session.CrashFrames);
            Assert.Equal(7, io.Log.Count);
            Assert.Equal(HidLightingPaint.Failed,
                session.Paint(new LoggingHidChannel { FailAfter = 3 }, io.Wait, Msi3ZoneLighting.Lights(), colors));
        }
    }

    /// <summary>The MSI Vigor GK30 against OpenRGB's MSIVigorController
    /// (#557): GetColourIndex and SetMode's Custom report
    /// (MSIVigorGK30Controller.cpp:60-227).</summary>
    public class MsiVigorLightingTests
    {
        [Fact]
        public void TheRegistration_FindsTheModel()
        {
            var r = Assert.Single(OpenRgbRegistrations.Of(MsiVigorLighting.OpenRgbFamily));
            Assert.Equal(1, r.Interface);
            var model = MsiVigorLighting.Instance.Match(CollectionOf(r, feature: 8));
            Assert.NotNull(model);
            Assert.Same(model, HidLightingFamilies.Match(CollectionOf(r, feature: 8)));
            Assert.Equal(150, model.HoldMs);
            Assert.Equal(500, model.RepaintMs);
            Assert.Null(MsiVigorLighting.Instance.Match(CollectionOf(r, feature: 7)));
            Assert.Equal(6, model.Map.Lights.Count);
            Assert.All(model.Map.Lights, l => Assert.Equal(LightKind.Zone, l.Kind));
        }

        /// <summary>argb_colour_index_data: red 0, yellow 1, green 2, cyan
        /// 3, blue 4, magenta 5, white 6, by which channels equal the
        /// largest.</summary>
        [Theory]
        [InlineData(0xFF0000, 0x00)]
        [InlineData(0xFFFF00, 0x01)]
        [InlineData(0x00FF00, 0x02)]
        [InlineData(0x00FFFF, 0x03)]
        [InlineData(0x0000FF, 0x04)]
        [InlineData(0xFF00FF, 0x05)]
        [InlineData(0xFFFFFF, 0x06)]
        [InlineData(0x808080, 0x06)]
        [InlineData(0xFF8000, 0x00)]
        [InlineData(0x10FF10, 0x02)]
        [InlineData(0x000000, 0x00)]
        public void TheIndex_IsGetColourIndex(int rgb, int index)
            => Assert.Equal((byte)index, MsiVigorLighting.ColorIndex(rgb));

        /// <summary>07 FF 70, the indexes two to a byte with the first high,
        /// brightness 3.</summary>
        [Fact]
        public void APicture_IsTheCustomReport()
        {
            var frame = MsiVigorLighting.Frame(new[] { 0xFF0000, 0xFFFF00, 0x00FF00, 0x00FFFF, 0x0000FF, 0xFF00FF });
            Assert.Equal("07 FF 70 01 23 45 03 00", Hex(frame));
            // A black light among others shows red, index 0.
            Assert.Equal("07 FF 70 06 00 00 03 00", Hex(MsiVigorLighting.Frame(new[] { 0, 0xFFFFFF, 0, 0, 0, 0x200000 })));
        }

        [Fact]
        public void AllBlack_IsTheOffMode()
            => Assert.Equal("07 FF 00 00 00 00 00 00", Hex(MsiVigorLighting.Frame(new int[6])));

        [Fact]
        public void TheSession_WaitsTenMilliseconds_AfterTheReport()
        {
            var io = new LoggingHidChannel();
            var session = new MsiVigorLighting.Session();
            var colors = new[] { 0xFF0000, 0, 0, 0, 0, 0 };
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, MsiVigorLighting.Lights(), colors));
            Assert.Equal(new[] { "F 07 FF 70 00 00 00 03 00", "W 10" }, io.Lines());
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(session.CrashFrames);
            Assert.Equal(HidLightingPaint.Failed,
                session.Paint(new LoggingHidChannel { FailWrites = true }, io.Wait, MsiVigorLighting.Lights(), colors));
        }
    }
}
