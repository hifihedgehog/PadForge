using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The Cherry family against OpenRGB's CherryKeyboardController
    /// and the CHERRY Utility reports cherryrgb-rs captured (#557). Expected
    /// bytes are written out from the reference lines named.</summary>
    public class CherryKeyboardLightingTests
    {
        internal static string Hex(IEnumerable<byte> bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));

        internal static HidCollectionInfo CollectionOf(OpenRgbRegistration r, ushort featureLength = 0)
            => new()
            {
                Path = $@"\\?\hid#vid_{r.VendorId:x4}&pid_{r.ProductId:x4}&mi_{Math.Max(0, r.Interface):x2}&col01#7&1&0&0000#{{4d1e55b2}}",
                VendorId = (ushort)r.VendorId,
                ProductId = (ushort)r.ProductId,
                InterfaceNumber = r.Interface,
                UsagePage = (ushort)(r.UsagePage < 0 ? 0xFF00 : r.UsagePage),
                Usage = (ushort)(r.Usage < 0 ? 1 : r.Usage),
                FeatureReportLength = featureLength,
            };

        /// <summary>CherryKeyboardControllerDetect.cpp:44-68: 25 products.</summary>
        [Fact]
        public void EveryRegistration_IsAModel()
        {
            var registrations = OpenRgbRegistrations.Of("CherryKeyboardController").ToList();
            Assert.Equal(25, registrations.Count);
            foreach (var r in registrations)
            {
                var model = CherryKeyboardLighting.Instance.Match(CollectionOf(r));
                Assert.NotNull(model);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(model, HidLightingFamilies.Match(CollectionOf(r)));
                Assert.Equal(LenovoLegionLighting.HoldMs, model.HoldMs);
                Assert.Equal(LenovoLegionLighting.RepaintMs, model.RepaintMs);
                Assert.Equal($"cherry:{r.VendorId:X4}:{r.ProductId:X4}", model.Map.Product);
                Assert.Null(CherryKeyboardLighting.Instance.Match(CollectionOf(r) with { UsagePage = 0xFF00 }));
                Assert.Null(CherryKeyboardLighting.Instance.Match(CollectionOf(r) with { InterfaceNumber = 0 }));
            }
            Assert.Contains(CherryKeyboardLighting.Instance, HidLightingFamilies.All);
        }

        /// <summary>RGBController_CherryKeyboard.cpp:14-29 and 320-344: 126
        /// LEDs, a 6 by 21 grid, no key names.</summary>
        [Fact]
        public void TheMap_IsOpenRgbsGrid()
        {
            var lights = MatrixKeyboardLayouts.Lights(MatrixKeyboardLayouts.Cherry);
            Assert.Equal(126, lights.Count);
            Assert.Equal(Enumerable.Range(0, 126), lights.Select(l => l.Address));
            // Row 0 of the grid: 0, NA, 12, 18 ...; LED 12 at column 2.
            Assert.Equal((0f, 0f), (lights[0].X, lights[0].Y));
            Assert.Equal((2f, 0f), (lights[12].X, lights[12].Y));
            // LED 124 sits at row 5, column 20.
            Assert.Equal((20f, 5f), (lights[124].X, lights[124].Y));
            Assert.All(lights, l => Assert.Equal(0, l.Usage));
            var map = new LightMap(lights, "t");
            Assert.True(map.HasKeys);
            // 126 cells less the 16 NA of the grid are keys to name.
            Assert.Equal(126 - 16, map.UnnamedKeyCount);
        }

        /// <summary>The Custom mode report a CHERRY Utility capture holds,
        /// built by SendKeyboardParameter's layout (CherryKeyboardController.cpp:231-260)
        /// with the utility's unsigned sum (cherryrgb-rs lib.rs test packets,
        /// line "19 - Custom").</summary>
        [Fact]
        public void TheModeReport_MatchesTheUtilitysCapture()
        {
            var report = SonixKeyboard.Parameter(0x06, new byte[] { 0x00, 0x08, 0x03, 0x00, 0x00, 0x00, 0xE0, 0x03, 0x00 }, cherry: true);
            Assert.Equal(64, report.Length);
            Assert.Equal("04 52 01 06 09 00 00 55 00 08 03 00 00 00 E0 03 00", Hex(report.Take(17)));
            Assert.All(report.Skip(17), b => Assert.Equal(0, b));
            // The Static red capture (line "18 - Static - Red").
            Assert.Equal("04 4D 01 06 09 00 00 55 00 03 03 00 00 00 E0 03 00",
                Hex(SonixKeyboard.Parameter(0x06, new byte[] { 0x00, 0x03, 0x03, 0x00, 0x00, 0x00, 0xE0, 0x03, 0x00 }, cherry: true).Take(17)));
        }

        /// <summary>SetKeyboardColors (CherryKeyboardController.cpp:51-81 and
        /// 190-229): 378 bytes in seven reports of 54, command 0B. The
        /// utility's 0B reports have the same header (cherryrgb-rs lib.rs
        /// test packets "04 43 00 0b 38 00 00").</summary>
        [Fact]
        public void TheColorReports_AreOpenRgbs()
        {
            var colors = new int[126];
            colors[0] = 0x102030;
            colors[18] = 0x405060;
            var packets = SonixKeyboard.ColorPackets(0x0B, SonixKeyboard.ColorData(colors, 126));
            Assert.Equal(7, packets.Count);
            // 0B + 36 + 10 + 20 + 30 = 0xA1.
            Assert.Equal("04 A1 00 0B 36 00 00 00 10 20 30", Hex(packets[0].Take(11)));
            // LED 18 at byte 54 of the data: the second report's first color.
            // 0B + 36 + 36 + 40 + 50 + 60 = 0x167.
            Assert.Equal("04 67 01 0B 36 36 00 00 40 50 60", Hex(packets[1].Take(11)));
            // Offset 324 = 0x144, 0B + 36 + 44 + 01 = 0x86.
            Assert.Equal("04 86 00 0B 36 44 01 00", Hex(packets[6].Take(8)));
        }

        [Fact]
        public void ASession_SetsCustomOnce_ReadsEachAnswer_AndHandsBackNothing()
        {
            var io = new FakeReportChannel { OutputReportLength = 64, InputReportLength = 64 };
            var model = CherryKeyboardLighting.Instance.Match(CollectionOf(OpenRgbRegistrations.Of("CherryKeyboardController").First()));
            var session = model.Begin(io, _ => { });
            Assert.False(session.Feature);
            var colors = new int[126];
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, colors));
            var calls = io.Calls.ToList();
            Assert.Equal(20, calls.Count);
            Assert.Equal(Enumerable.Repeat(new[] { "out", "in" }, 10).SelectMany(x => x), calls.Select(c => c.Kind));
            // The Custom mode, brightness 4: 06 + 09 + 55 + 08 + 04 = 0x70.
            Assert.Equal("04 70 00 06 09 00 00 55 00 08 04 00 00 00 00 00 00", Hex(calls[0].Bytes.Take(17)));
            // The colors go between a begin and an end report, each answered, as CHERRY
            // Utility sends them (RGBController_CherryKeyboard.cpp:357-370 at b9d903b5,
            // SendKeyboardBegin and SendKeyboardEnd, CherryKeyboardController.cpp:138-188).
            Assert.Equal("04 01 00 01 00", Hex(calls[2].Bytes.Take(5)));
            Assert.All(Enumerable.Range(0, 7), i => Assert.Equal(0x0B, calls[4 + 2 * i].Bytes[3]));
            Assert.Equal("04 02 00 02 00", Hex(calls[18].Bytes.Take(5)));
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, colors));
            Assert.Equal(20 + 18, io.Calls.Count);
            Assert.Empty(session.CrashFrames);
            int before = io.Calls.Count;
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(before, io.Calls.Count);

            var broken = new FakeReportChannel { FailWrites = true };
            Assert.Equal(HidLightingPaint.Failed, model.Begin(broken, _ => { }).Paint(broken, _ => { }, model.Map.Lights, colors));
        }
    }
}
