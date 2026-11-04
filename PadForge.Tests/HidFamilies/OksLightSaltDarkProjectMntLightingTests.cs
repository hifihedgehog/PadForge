using System.Collections.Generic;
using System.Linq;
using System.Text;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The OKS Optical Axis RGB against OpenRGB's OKSController (#557).</summary>
    public class OksKeyboardLightingTests
    {
        [Fact]
        public void TheKb2Packets_AreOpenRgbs_WithTheirChecksums()
        {
            // SendInitialize: write-RGB with brightness 0, mode 0, speed 2,
            // direction 1; the checksum 0x35 + 5C + 09 + 15 + FF (lines 130-176).
            var init = OksKeyboardLighting.Reports(OksKeyboardLighting.WriteRgbPacket(0, 0, 2, 1)).Single();
            Assert.Equal("04 5C 09 15 AE 00 00 00 00 00 02 01 FF FF 00", Hex(init, 15));
            Assert.Equal(65, init.Length);
            // The Direct mode at brightness 2 (RGBController_OKSKeyboard.cpp:121-133).
            Assert.Equal("04 5C 09 15 AE 02 00 00 00 00 02 01 FF FF", Hex(
                OksKeyboardLighting.Reports(OksKeyboardLighting.WriteRgbPacket(2, 0, 2, 1)).Single(), 14));
        }

        [Fact]
        public void APicture_IsNineWriteLedPackets_OfFourteenPlaces()
        {
            var lights = OksKeyboardLighting.Lights();
            // 126 places less the unused ones (RGBController_OKSKeyboard.cpp:54-69).
            Assert.Equal((0, (ushort)0x29), (lights[0].Address, lights[0].Usage));
            Assert.Equal((2, (ushort)0x3A), (lights[1].Address, lights[1].Usage));
            Assert.Contains(lights, l => l.Address == 124 && l.Usage == 0x63);
            var colors = Distinct(lights.Count);
            var picture = OksKeyboardLighting.Picture(lights, colors);
            Assert.Equal(9, picture.Count);
            // Length 58, write-LED 0x17, checksum 0x35 + 5C + 3A + 17 + FF = E1.
            Assert.All(picture, r => Assert.Equal("04 5C 3A 17 E1", Hex(r, 5)));
            // Place 0, Escape: 00 RR GG BB; place 1, unused: 01 00 00 00.
            Assert.Equal(new[] { (byte)0, (byte)(colors[0] >> 16), (byte)(colors[0] >> 8), (byte)colors[0] }, picture[0].Skip(5).Take(4));
            Assert.Equal("01 00 00 00", Hex(picture[0].Skip(9).ToArray(), 4));
            // The last packet: places 112 to 125, FF FF, then zeros.
            Assert.Equal(112, picture[8][5]);
            Assert.Equal(125, picture[8][5 + 13 * 4]);
            Assert.Equal("FF FF 00 00", Hex(picture[8].Skip(5 + 56).ToArray(), 4));
        }

        [Fact]
        public void ASession_SendsTheOpeningAndTheDirectMode()
        {
            var model = OksKeyboardLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(OksKeyboardLighting.OpenRgbFamily).Single(), output: 65));
            var io = new FakeReportChannel { OutputReportLength = 65 };
            var waits = new List<int>();
            var session = model.Begin(io, Recorder(waits));
            Assert.Equal(new[] { "04 5C 09 15 AE 00", "04 5C 09 15 AE 02" }, Calls(io).Select(c => Hex(c.Bytes, 6)));
            Assert.Equal(new[] { 2, 2 }, waits);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, Recorder(waits), model.Map.Lights, Distinct(model.Map.Lights.Count)));
            Assert.Equal(11, io.Calls.Count);
            Assert.Equal(5, waits[^1]);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(11, io.Calls.Count);
            Assert.Null(OksKeyboardLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(OksKeyboardLighting.OpenRgbFamily).Single(), output: 9)));
        }
    }

    /// <summary>The LightSalt keyboard and keypad against OpenRGB's LightSaltController (#557).</summary>
    public class LightSaltLightingTests
    {
        private static readonly LightSaltLighting Family = LightSaltLighting.Instance;

        private static OpenRgbRegistration Registration() => OpenRgbRegistrations.Of(LightSaltLighting.OpenRgbFamily).Single();

        private static byte[] Answer(string text)
        {
            // A 32-byte input report with no report ID, as Windows hands it
            // over: a zero ahead of the 32 bytes.
            var answer = new byte[33];
            Encoding.ASCII.GetBytes(text).CopyTo(answer, 1);
            return answer;
        }

        [Fact]
        public void TheFamily_WritesToNoDeviceWhoseStringsItHasNotSeen()
        {
            var collection = CollectionFor(Registration(), output: 33);
            // STMicroelectronics' 0483:5750 (LightSaltControllerDetect.cpp:18-19)
            // answers with the model, whose check the worker asks of the
            // strings before it lists the collection.
            var model = LightSaltLighting.Instance.Match(collection);
            Assert.Same(model, HidLightingFamilies.Match(collection));
            Assert.NotNull(model.Identify);
            // Both strings hold Light&Salt (LightSaltController.cpp:44-63).
            Assert.True(model.Identify(new FakeReportChannel { ManufacturerString = "Light&Salt", ProductString = "Light&Salt Keyboard" }));
            Assert.False(model.Identify(new FakeReportChannel { ManufacturerString = "STMicroelectronics", ProductString = "Light&Salt" }));
            Assert.False(model.Identify(new FakeReportChannel { ManufacturerString = "Light&Salt", ProductString = "Custom HID" }));
            Assert.False(model.Identify(new FakeReportChannel { ManufacturerString = "light&salt", ProductString = "light&salt" }));
            // A string that could not be read holds nothing.
            Assert.False(model.Identify(new FakeReportChannel { ProductString = "Light&Salt" }));
            Assert.False(model.Identify(new FakeReportChannel()));
        }

        [Fact]
        public void TheMaps_AreOpenRgbsCells()
        {
            var keyboard = LightSaltLighting.Map(false, 0x5750);
            var keypad = LightSaltLighting.Map(true, 0x5750);
            Assert.Equal(83, keyboard.Lights.Count);
            Assert.Equal(18, keypad.Lights.Count);
            // led_indices: Escape at 0, the backtick at 15, the last arrow at 89
            // (RGBController_LightSaltKeyboard.cpp:126-145).
            Assert.Equal((0, (ushort)0x29), (keyboard.Lights[0].Address, keyboard.Lights[0].Usage));
            Assert.Equal((15, (ushort)0x35), (keyboard.Lights[13].Address, keyboard.Lights[13].Usage));
            Assert.Equal(89, keyboard.Lights[82].Address);
            Assert.Equal(5, keyboard.Lights.Count(l => l.Usage == 0x2C));
            // The keypad's Number Pad Enter at 18 (RGBController_LightSaltKeypad.cpp:59-75).
            Assert.Equal((18, (ushort)0x58), (keypad.Lights[17].Address, keypad.Lights[17].Usage));
        }

        [Fact]
        public void APicture_IsTheFilter_EachRowOfEachSet_AndTheApply()
        {
            var map = LightSaltLighting.Map(false, 0x5750);
            var colors = map.Lights.Select(_ => 0x000000).ToArray();
            var picture = LightSaltLighting.Picture(false, map.Lights, colors);
            // Filter, 4 sets of 9 rows, apply (DeviceUpdateColors, lines 373-403).
            Assert.Equal(1 + 36 + 1, picture.Count);
            Assert.Equal("00 FE 04 FF FF FF 00", Hex(picture[0], 7));
            Assert.Equal("00 03 01", Hex(picture[1], 3));
            Assert.Equal("00 03 0B", Hex(picture[2], 3));
            Assert.Equal("00 04 01", Hex(picture[10], 3));
            Assert.Equal("00 FE 01 03 04 05 06 00", Hex(picture[^1], 8));
            Assert.All(picture, r => Assert.Equal(33, r.Length));
            // The key "1" at cell 16 has deflection 0xA: black in set 0,
            // halfway to white, 127, in set 1 (DeflectColor, lines 16-32).
            Assert.Equal("00 00 00", Hex(picture[2].Skip(3 + 6 * 3).ToArray(), 3));
            Assert.Equal("7F 7F 7F", Hex(picture[1 + 9 + 1].Skip(3 + 6 * 3).ToArray(), 3));
            Assert.Equal(0x7F7F7F, LightSaltLighting.Deflect(true, 0));
            Assert.Equal(0xFFFFFF, LightSaltLighting.Deflect(true, 0xFFFFFF));
            Assert.Equal(0x123456, LightSaltLighting.Deflect(false, 0x123456));
            // The keypad: one set of five rows, applied as set 3.
            var keypad = LightSaltLighting.Map(true, 0x5750);
            var small = LightSaltLighting.Picture(true, keypad.Lights, Distinct(18));
            Assert.Equal(1 + 5 + 1, small.Count);
            Assert.Equal("00 FE 01 03 00", Hex(small[^1], 5));
        }

        [Fact]
        public void ASession_AsksTheModelAndTheClass_ThenSelectsCustom()
        {
            var model = Family.Match(CollectionFor(Registration(), output: 33));
            var answers = new Queue<byte[]>(new[] { Answer("LS-K1"), Answer("Light&Salt_KEY_PAD\0V1.0") });
            var io = new FakeReportChannel { OutputReportLength = 33, InputReportLength = 33, Input = () => answers.Count > 0 ? answers.Dequeue() : null };
            var waits = new List<int>();
            var session = model.Begin(io, Recorder(waits));
            Assert.NotNull(session);
            Assert.Equal(new[] { "out", "in", "out", "in", "out", "out" }, Calls(io).Select(c => c.Kind));
            Assert.Equal("00 FE F5 06", Hex(Calls(io)[0].Bytes, 4));
            Assert.Equal("00 FE FF 00", Hex(Calls(io)[2].Bytes, 4));
            Assert.Equal("00 FE 05 FF", Hex(Calls(io)[4].Bytes, 4));
            Assert.Equal("00 FE 03 00 00 00", Hex(Calls(io)[5].Bytes, 6));
            Assert.Equal(new[] { 25 }, waits);
            Assert.EndsWith(":keypad", session.Map.Product);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, session.Map.Lights, Distinct(18)));
            Assert.Equal(6 + 7, io.Calls.Count);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(6 + 7, io.Calls.Count);

            // Any other class ends the session before anything is lit
            // (LightSaltControllerDetect.cpp:33-46).
            var other = new Queue<byte[]>(new[] { Answer("X"), Answer("Something_else") });
            var stranger = new FakeReportChannel { OutputReportLength = 33, Input = () => other.Count > 0 ? other.Dequeue() : null };
            Assert.Null(model.Begin(stranger, _ => { }));
            Assert.Equal(new[] { "out", "in", "out", "in" }, Calls(stranger).Select(c => c.Kind));
        }
    }

    /// <summary>The Dark Project KD3B V2 against OpenRGB's DarkProject controller (#557).</summary>
    public class DarkProjectKeyboardLightingTests
    {
        [Fact]
        public void APicture_IsTheTwoReports_ByPacketMap()
        {
            var lights = DarkProjectKeyboardLighting.Lights();
            Assert.Equal(87, lights.Count);
            Assert.Equal((0, (ushort)0x29), (lights[0].Address, lights[0].Usage));
            var colors = Distinct(87);
            var (rg, blue) = DarkProjectKeyboardLighting.Picture(lights, colors);
            Assert.Equal(256, rg.Length);
            Assert.Equal("08 07 00 00 00", Hex(rg, 5));
            Assert.Equal("08 07 00 01 00", Hex(blue, 5));
            // Escape at packet_map 5: red 5 + 5, green 107 + 5, blue 5 + 5;
            // the backtick, place 16, at packet_map 0 (DarkProjectKeyboardController.cpp:16-46).
            Assert.Equal((byte)(colors[0] >> 16), rg[10]);
            Assert.Equal((byte)(colors[0] >> 8), rg[112]);
            Assert.Equal((byte)colors[0], blue[10]);
            Assert.Equal((byte)(colors[16] >> 16), rg[5]);
            Assert.Equal(0x35, lights[16].Usage);
            // The right arrow, place 86, at 100: red at 105, green at 207.
            Assert.Equal((byte)(colors[86] >> 8), rg[207]);
        }

        [Fact]
        public void ASession_SendsBothReports_AndNothingElse()
        {
            var model = DarkProjectKeyboardLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(DarkProjectKeyboardLighting.OpenRgbFamily).Single(), output: 256));
            var io = new FakeReportChannel { OutputReportLength = 256 };
            var session = model.Begin(io, _ => { });
            Assert.Empty(io.Calls);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, Distinct(87)));
            Assert.Equal(2, io.Calls.Count);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(2, io.Calls.Count);
            io.FailWrites = true;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, _ => { }, model.Map.Lights, Distinct(87)));
        }
    }

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
            // A keyboard collection whose output report cannot carry a row.
            Assert.Null(MntKeyboardLighting.Instance.Match(CollectionFor(registrations[0], output: 2)));
            var io = new FakeReportChannel { OutputReportLength = 42 };
            var session = pocket.Begin(io, _ => { });
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, pocket.Map.Lights, Distinct(64)));
            Assert.Equal(6, io.Calls.Count);
            Assert.All(Calls(io), c => Assert.Equal(42, c.Bytes.Length));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(6, io.Calls.Count);
        }
    }
}
