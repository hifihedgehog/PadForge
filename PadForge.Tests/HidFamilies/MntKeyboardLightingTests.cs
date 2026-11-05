using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The MNT Reform and Pocket Reform keyboards against OpenRGB's
    /// MNTKeyboardController (#557).</summary>
    public class MntKeyboardLightingTests
    {
        [Fact]
        public void TheMaps_AreOpenRgbsGrids()
        {
            var reform = MntKeyboardLighting.Lights(false);
            var pocket = MntKeyboardLighting.Lights(true);
            // 84 places less four unused, 72 less eight
            // (RGBController_MNTReformKeyboard.cpp:30-121, RGBController_MNTPocketReformKeyboard.cpp:31-111).
            Assert.Equal(80, reform.Count);
            Assert.Equal(64, pocket.Count);
            Assert.Equal((0, (ushort)0x29), (reform[0].Address, reform[0].Usage));
            Assert.Equal((13, (ushort)0x66), (reform[13].Address, reform[13].Usage)); // Power
            Assert.Contains(pocket, l => l.Address == 63 && l.Kind == LightKind.Other); // Left Button
            Assert.Contains(pocket, l => l.Address == 51 && l.Usage == 0x31);         // backslash
        }

        [Fact]
        public void APicture_IsOneReportPerRow_InBlueGreenRed()
        {
            var lights = MntKeyboardLighting.Lights(false);
            var colors = lights.Select(_ => 0x112233).ToArray();
            var picture = MntKeyboardLighting.Picture(14, lights, colors);
            Assert.Equal(6, picture.Count);
            // "xXRGB", the row, then blue, green, red (MNTKeyboardController.cpp:19-32,
            // RGBController_MNTKeyboard.cpp:51-64).
            Assert.Equal("78 58 52 47 42 00 33 22 11", Hex(picture[0], 9));
            Assert.Equal("78 58 52 47 42 05", Hex(picture[5], 6));
            Assert.All(picture, r => Assert.Equal(6 + 14 * 3, r.Length));
            // Row 3, place 55, is unused: black.
            Assert.Equal("00 00 00", Hex(picture[3].Skip(6 + 13 * 3).ToArray(), 3));
            Assert.All(MntKeyboardLighting.Picture(12, MntKeyboardLighting.Lights(true), Distinct(64)), r => Assert.Equal(42, r.Length));
        }

        [Fact]
        public void TheModels_AreThePocketAndTheReform_ByProduct()
        {
            var registrations = OpenRgbRegistrations.Of(MntKeyboardLighting.OpenRgbFamily).ToList();
            var reform = MntKeyboardLighting.Instance.Match(CollectionFor(registrations.First(r => r.ProductId == 0x6D02), output: 48));
            var pocket = MntKeyboardLighting.Instance.Match(CollectionFor(registrations.First(r => r.ProductId == 0x6D06), output: 42));
            Assert.Equal(80, reform.Map.Lights.Count);
            Assert.Equal(64, pocket.Map.Lights.Count);
            // The rows are output reports, which do not pass on a collection
            // Windows holds, so the worker is not asked to keep such a handle
            // (reform2-keyboard4-fw/src/main.c:589-662).
            Assert.False(reform.HeldByWindows);
            Assert.False(pocket.HeldByWindows);
            var io = new FakeReportChannel { OutputReportLength = 42 };
            var session = pocket.Begin(io, _ => { });
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, pocket.Map.Lights, Distinct(64)));
            Assert.Equal(6, io.Calls.Count);
            Assert.All(Calls(io), c => Assert.Equal(42, c.Bytes.Length));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(6, io.Calls.Count);
        }

        /// <summary>The firmware's keyboard collection declares its LED report
        /// and no row report (usb_descriptors.c:296-302). It is a unit all the
        /// same, so the keyboard has a row that says why it stays dark.</summary>
        [Fact]
        public void TheRealKeyboardCollection_IsAUnit_WhateverItDeclares()
        {
            var registration = OpenRgbRegistrations.Of(MntKeyboardLighting.OpenRgbFamily).First();
            Assert.NotNull(MntKeyboardLighting.Instance.Match(CollectionFor(registration, output: 2)));
            Assert.NotNull(MntKeyboardLighting.Instance.Match(CollectionFor(registration, output: 0)));
        }
    }
}
