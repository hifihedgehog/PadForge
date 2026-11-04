using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.LaptopFamilies.LaptopFamilyTest;

namespace PadForge.Tests.LaptopFamilies
{
    /// <summary>The Acer Nitro controller against OpenRGB's
    /// AcerNitroHidKeyboardController (#557): SetLEDs' reports
    /// (AcerNitroHidKeyboardController.cpp:40-60) for the keyboard's zones
    /// and the laptop's light.</summary>
    public class AcerNitroLightingTests
    {
        [Fact]
        public void TheRegistration_FindsTheModel()
        {
            var r = Assert.Single(OpenRgbRegistrations.Of(AcerNitroLighting.OpenRgbFamily));
            var model = AcerNitroLighting.Instance.Match(CollectionOf(r, feature: 11));
            Assert.NotNull(model);
            Assert.Same(model, HidLightingFamilies.Match(CollectionOf(r, feature: 11)));
            Assert.Null(AcerNitroLighting.Instance.Match(CollectionOf(r, feature: 10)));
            Assert.Null(AcerNitroLighting.Instance.Match(CollectionOf(r, feature: 11) with { UsagePage = 0xFF5B }));
            Assert.Equal(50, model.RepaintMs);
            Assert.Equal(new[] { LightKind.Zone, LightKind.Zone, LightKind.Zone, LightKind.Zone, LightKind.Other },
                model.Map.Lights.Select(l => l.Kind));
        }

        /// <summary>A4, device, 02 (static), 64 (100), 00 01, R G B, the zone
        /// bit, 00.</summary>
        [Fact]
        public void APicture_IsOneReportPerLight()
        {
            var frames = AcerNitroLighting.Frames(AcerNitroLighting.Lights(),
                new[] { 0x112233, 0x445566, 0x778899, 0xAABBCC, 0xDDEEFF });
            Assert.Equal(new[]
            {
                "A4 21 02 64 00 01 11 22 33 01 00",
                "A4 21 02 64 00 01 44 55 66 02 00",
                "A4 21 02 64 00 01 77 88 99 04 00",
                "A4 21 02 64 00 01 AA BB CC 08 00",
                "A4 65 02 64 00 01 DD EE FF 01 00",
            }, frames.Select(f => Hex(f)));
        }

        [Fact]
        public void TheSession_SendsThem_AndHandsBackNothing()
        {
            var io = new LoggingHidChannel();
            var session = new AcerNitroLighting.Session();
            var colors = new[] { 1, 2, 3, 4, 5 };
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, AcerNitroLighting.Lights(), colors));
            Assert.Equal(AcerNitroLighting.Frames(AcerNitroLighting.Lights(), colors), io.Sent("F"));
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(session.CrashFrames);
            Assert.Equal(HidLightingPaint.Failed,
                session.Paint(new LoggingHidChannel { FailAfter = 4 }, io.Wait, AcerNitroLighting.Lights(), colors));
        }
    }

    /// <summary>The Ionico keyboard and light bar against OpenRGB's
    /// IonicoController (#557): SetMode and SetColors
    /// (IonicoController.cpp:63-121).</summary>
    public class IonicoLightingTests
    {
        [Fact]
        public void EveryRegistration_FindsItsModel()
        {
            var registrations = OpenRgbRegistrations.Of(IonicoLighting.OpenRgbFamily).ToList();
            Assert.Equal(2, registrations.Count);
            foreach (var r in registrations)
            {
                var model = IonicoLighting.Instance.Match(CollectionOf(r, feature: 9, output: 65));
                Assert.NotNull(model);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(model, HidLightingFamilies.Match(CollectionOf(r, feature: 9, output: 65)));
                Assert.Null(IonicoLighting.Instance.Match(CollectionOf(r, feature: 8, output: 65)));
            }
            var bar = registrations.Single(r => r.ProductId == 0x6005);
            Assert.Null(IonicoLighting.Instance.Match(CollectionOf(bar, feature: 9, output: 64)));
            var keyboard = IonicoLighting.Instance.Match(CollectionOf(registrations.Single(r => r.ProductId == 0xCE00), feature: 9));
            Assert.Equal(4, keyboard.Map.Lights.Count);
            Assert.All(keyboard.Map.Lights, l => Assert.Equal(LightKind.Zone, l.Kind));
            var barModel = IonicoLighting.Instance.Match(CollectionOf(bar, feature: 9, output: 65));
            Assert.Equal(22, barModel.Map.Lights.Count);
            Assert.All(barModel.Map.Lights, l => Assert.Equal(LightKind.Edge, l.Kind));
        }

        [Fact]
        public void TheKeyboard_SetsDirectOnce_ThenEachZone()
        {
            var io = new LoggingHidChannel();
            var session = new IonicoLighting.Session(bar: false);
            var lights = IonicoLighting.Lights(false);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, new[] { 0x112233, 0x445566, 0x778899, 0xAABBCC }));
            Assert.Equal(new[]
            {
                "F 00 08 02 01 00 32 08 00 00",
                "F 00 14 00 01 11 22 33 00 00",
                "F 00 14 00 02 44 55 66 00 00",
                "F 00 14 00 03 77 88 99 00 00",
                "F 00 14 00 04 AA BB CC 00 00",
            }, io.Lines());
            io.Log.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, new int[4]));
            Assert.Equal(4, io.Log.Count);
            Assert.All(io.Lines(2), l => Assert.Equal("F 00 14", l));
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(session.CrashFrames);
        }

        /// <summary>Mode 0x33, then 12 00 00, the colors as R B G from byte
        /// 1 of the output report, then 12 00 01.</summary>
        [Fact]
        public void TheLightBar_SetsDirectOnce_ThenStartColorsEnd()
        {
            var io = new LoggingHidChannel();
            var session = new IonicoLighting.Session(bar: true);
            var colors = Colors(22, n => n == 0 ? 0x112233 : n == 21 ? 0xAABBCC : 0);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, IonicoLighting.Lights(true), colors));
            var lines = io.Lines(9);
            Assert.Equal("F 00 08 02 33 00 32 08 00 00", lines[0]);
            Assert.Equal("F 00 12 00 00 00 00 00 00 00", lines[1]);
            Assert.Equal("O 00 11 33 22 00 00 00 00 00", lines[2]);
            Assert.Equal("F 00 12 00 01 00 00 00 00 00", lines[3]);
            var report = io.Log[2].Bytes;
            Assert.Equal(65, report.Length);
            // The 22nd light's red is byte 64, the last: its blue and green
            // fall past the report, as OpenRGB writes them past its buffer.
            Assert.Equal(0xAA, report[64]);
            Assert.Equal(0, report[61] | report[62] | report[63]);
            Assert.Equal(HidLightingPaint.Failed,
                new IonicoLighting.Session(bar: true).Paint(new LoggingHidChannel { FailAfter = 2 }, io.Wait, IonicoLighting.Lights(true), colors));
        }
    }
}
