using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Keychron K3 V2 against OpenRGB's
    /// KeychronKeyboardController (#557). Every expected byte and place is
    /// written out from the reference line the test names.</summary>
    public class KeychronKeyboardLightingTests
    {
        private static OpenRgbRegistration Registration()
            => OpenRgbRegistrations.Of(KeychronKeyboardLighting.OpenRgbFamily).Single();

        private static HidLightingModel Model()
            => KeychronKeyboardLighting.Instance.Match(CollectionFor(Registration(), feature: 65));

        [Fact]
        public void TheRegistration_AnswersWithTheModel_OnTheKeyboardCollectionWindowsHolds()
        {
            var r = Registration();
            // KeychronKeyboardControllerDetect.cpp:19-24 and 44.
            Assert.Equal((0x05AC, 0x024F, 0, 0x0001, 0x06), (r.VendorId, r.ProductId, r.Interface, r.UsagePage, r.Usage));
            var model = Model();
            Assert.Equal("Keychron Gaming Keyboard 1", model.Name);
            Assert.Same(KeychronKeyboardLighting.Instance, model.Family);
            Assert.Same(model, HidLightingFamilies.Match(CollectionFor(r, feature: 65)));
            Assert.Contains(KeychronKeyboardLighting.Instance, HidLightingFamilies.All);
            // A collection whose feature report cannot carry a 65-byte frame
            // is another collection of the device.
            Assert.Null(KeychronKeyboardLighting.Instance.Match(CollectionFor(r, feature: 33)));
            Assert.Null(KeychronKeyboardLighting.Instance.Match(CollectionFor(r, feature: 65) with { InterfaceNumber = 1 }));
            // The keyboard collection opens with no access right only, and
            // its frames are all feature reports.
            Assert.True(model.HeldByWindows);
            // Custom saves by itself (RGBController_KeychronKeyboard.cpp:444).
            Assert.Equal(150, model.HoldMs);
            Assert.Equal(500, model.RepaintMs);
            Assert.Empty(model.Programs);
            Assert.Equal("keychron:05AC:024F", model.Map.Product);
        }

        /// <summary>SetupZones walks k3_keychron's grid column by column
        /// (RGBController_KeychronKeyboard.cpp:211-410 and 627-646), and each
        /// LED's color goes at four times its led_sequence_positions place
        /// (KeychronKeyboardController.cpp:178-185).</summary>
        [Fact]
        public void TheKeys_AreTheGridsLeds_AtTheirColorPlaces()
        {
            var lights = Model().Map.Lights;
            Assert.Equal(84, lights.Count);
            // The first column: Escape, `, Tab, Caps Lock, Left Shift, Left
            // Control at 143, 18, 34, 50, 66, 81 (lines 216-221, 224-229 and 325-330).
            Assert.Equal(new[] { 143, 18, 34, 50, 66, 81 }, lights.Take(6).Select(l => l.Address));
            Assert.Equal(new ushort[] { 0x29, 0x35, 0x2B, 0x39, 0xE1, 0xE0 }, lights.Take(6).Select(l => l.Usage));
            // The second column: F1, 1, Left Windows at 2, 19, 82.
            Assert.Equal(new[] { 2, 19, 82 }, lights.Skip(6).Take(3).Select(l => l.Address));
            Assert.Equal(new ushort[] { 0x3A, 0x1E, 0xE3 }, lights.Skip(6).Take(3).Select(l => l.Usage));
            // The last LED: Right Arrow at 90.
            Assert.Equal(90, lights[^1].Address);
            Assert.Equal(0x4F, lights[^1].Usage);
            Assert.Equal(84, lights.Select(l => l.Address).Distinct().Count());
            Assert.All(lights, l => Assert.InRange(l.Address * 4 + 3, 0, MintakaKeyboardLighting.ColorBufSize - 1));
        }

        /// <summary>Index 64, the number row's cell between "-" and
        /// Backspace, takes place 30 of the number row's places 18 to 32, and
        /// index 67, the shift row's cell between "/" and Up Arrow, place 77
        /// of the shift row's 66 to 80: the "=" and Right Shift keys the table
        /// names Number Pad + and Left Shift. The Light key has no
        /// usage.</summary>
        [Fact]
        public void TheTwoMisnamedCells_AreTheKeysAtThem_AndNoUsageLightsTwoKeys()
        {
            Assert.Equal("Key: Number Pad +", KeyboardLedNames.KeychronNames[64]);
            Assert.Equal("Key: Left Shift", KeyboardLedNames.KeychronNames[67]);
            Assert.Equal("Key: Left Shift", KeyboardLedNames.KeychronNames[4]);
            var lights = Model().Map.Lights;
            Assert.Equal(30, Assert.Single(lights, l => l.Usage == 0x2E).Address);
            Assert.Equal(77, Assert.Single(lights, l => l.Usage == 0xE5).Address);
            Assert.Equal(66, Assert.Single(lights, l => l.Usage == 0xE1).Address);
            Assert.DoesNotContain(lights, l => l.Usage == 0x57);
            var keys = lights.Where(l => l.Usage != 0).ToList();
            Assert.Equal(keys.Count, keys.Select(l => l.Usage).Distinct().Count());
            // "Key: Light", index 78, place 16.
            var light = Assert.Single(lights, l => l.Address == 16);
            Assert.Equal(0, light.Usage);
            Assert.Equal(LightKind.Other, light.Kind);
        }

        /// <summary>SetMode's 18 pages (KeychronKeyboardController.cpp:58-214),
        /// each report and read 10 ms apart (lines 271-297).</summary>
        [Fact]
        public void APicture_IsOpenRgbsSetMode_StepForStep()
        {
            var model = Model();
            var lights = model.Map.Lights;
            var colors = Distinct(lights.Count);
            var io = new FakeReportChannel { FeatureReportLength = 65, Feature = _ => new byte[65] };
            var waits = new List<int>();
            var session = model.Begin(io, Recorder(waits));
            Assert.True(session.Feature);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, Recorder(waits), lights, colors));
            var calls = Calls(io);
            // 04 18 and a read (SetCustomization, lines 246-256), 04 13 with
            // 0x12 at byte 8 and a read (StartEffectPage, 227-244).
            Assert.Equal("set", calls[0].Kind);
            Assert.Equal("00 04 18 00", Hex(calls[0].Bytes, 4));
            Assert.Equal("get", calls[1].Kind);
            Assert.Equal("00 04 13 00 00 00 00 00 00 12", Hex(calls[2].Bytes, 10));
            Assert.Equal("get", calls[3].Kind);
            // The first effect page: Static 01, brightness 0F at 9, AA 55 at
            // 14 and 15, then Keystroke light up 02 (lines 108-151).
            // Stored white, not OpenRGB's black (MintakaKeyboardLighting.Effect).
            Assert.Equal("00 01 FF FF FF 00 00 00 00 00 0F 00 00 00 00 AA 55 02", Hex(calls[4].Bytes, 18));
            // Three empty pages (lines 158-162).
            Assert.All(calls.Skip(9).Take(3), c => Assert.All(c.Bytes, b => Assert.Equal(0, b)));
            // The color pages: Escape's color at place 143, byte 572, the
            // ninth page's byte 60 (lines 170-191).
            int escape = colors[0];
            Assert.Equal(new byte[] { 0x80, (byte)(escape >> 16), (byte)(escape >> 8), (byte)escape },
                calls[12 + 8].Bytes.Skip(1 + 60).Take(4));
            // The active page, the Off effect's at Custom's brightness (lines 139-147 and 199-201).
            Assert.Equal("00 80 00 00 00 00 00 00 00 00 0F 00 00 00 00 AA 55 00", Hex(calls[21].Bytes, 18));
            Assert.Equal("00 04 02 00", Hex(calls[22].Bytes, 4));
            Assert.Equal("get", calls[23].Kind);
            Assert.Equal("00 04 F0 00", Hex(calls[24].Bytes, 4));
            Assert.Equal(25, calls.Count);
            Assert.Equal(25, waits.Count);
            Assert.All(waits, w => Assert.Equal(10, w));
            // Nothing hands it back (the destructor, RGBController_KeychronKeyboard.cpp:597-602).
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(25, io.Calls.Count);
            Assert.Empty(session.CrashFrames);
        }
    }
}
