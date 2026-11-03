using System.Collections.Generic;
using System.Linq;
using System.Text;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Winbond gaming keyboards against OpenRGB's
    /// WinbondGamingKeyboardController (#557).</summary>
    public class WinbondKeyboardLightingTests
    {
        private static HidLightingModel Model()
            => WinbondKeyboardLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(WinbondKeyboardLighting.OpenRgbFamily).Single(), output: 64, input: 64));

        private static byte[] Answer(string version, byte at4 = 0)
        {
            // 0x1, 0x0D, 0, 0, 0, then the string
            // (WinbondGamingKeyboardController.cpp:173-176).
            var answer = new byte[64];
            answer[0] = 0x01;
            answer[1] = 0x0D;
            answer[4] = at4;
            Encoding.ASCII.GetBytes(version).CopyTo(answer, 5);
            return answer;
        }

        [Fact]
        public void TheVersionString_NamesTheRegion_AsParseVersionStringReadsIt()
        {
            // The ISO firmware's string, K221UKCVRGB (line 104), and the
            // updaters' names for ANSI and JIS (lines 147-153).
            Assert.Equal(KeyboardLayout.IsoQwerty, WinbondKeyboardLighting.RegionOf(Answer("2NUC,01,KB,FL,K221UKCVRGB,V1.05.03"), 64));
            Assert.Equal(KeyboardLayout.AnsiQwerty, WinbondKeyboardLighting.RegionOf(Answer("2NUC,01,KB,FL,K221CVRGB,V1.05.07"), 64));
            Assert.Equal(KeyboardLayout.Jis, WinbondKeyboardLighting.RegionOf(Answer("2NUC,01,KB,FL,K221JPCVRGB,V1.05.03"), 64));
            // No CVRGB: ISO, the controller's default (WinbondGamingKeyboardController.h:84).
            Assert.Equal(KeyboardLayout.IsoQwerty, WinbondKeyboardLighting.RegionOf(Answer("SOMETHING"), 64));
            // Not the answer: byte 4 not 0, or another command (line 188).
            Assert.Null(WinbondKeyboardLighting.RegionOf(Answer("K221JPCVRGB", 1), 64));
            Assert.Null(WinbondKeyboardLighting.RegionOf(Answer(""), 64));
        }

        [Fact]
        public void TheModeAndThePicture_AreOpenRgbs()
        {
            // setModeImpl for the Custom mode, brightness 4, speed 2, colors
            // blue and black, full color (lines 228-258 and 374-389).
            Assert.Equal("01 07 00 00 00 0E 0A 04 02 00 00 FF 00 00 00 00 01 00",
                Hex(WinbondKeyboardLighting.CustomModeReport(), 18));
            var model = Model();
            var lights = model.Map.Lights;
            var colors = lights.Select(_ => 0x80FF00).ToArray();
            var picture = WinbondKeyboardLighting.Picture(lights, colors);
            Assert.Equal(8, picture.Count);
            Assert.Equal(new byte[] { 0x36, 0x36, 0x36, 0x36, 0x36, 0x36, 0x36, 0x12 }, picture.Select(r => r[5]));
            Assert.All(picture.Select((r, i) => (r, i)), p => Assert.Equal($"01 09 00 00 {p.i:X2}", Hex(p.r, 5)));
            // Escape is KV(0,0): report 0 at byte 6, each value scaled to 0xC1
            // (lines 286-318): 0x80 to 0x60, 0xFF to 0xC1.
            Assert.Equal(0x10000, lights.First(l => l.Usage == 0x29).Address);
            Assert.Equal("60 C1 00", Hex(picture[0].Skip(6).ToArray(), 3));
            // Number Pad . is KV(7,5): report 7 at 6 + 15.
            Assert.Equal((1 << 16) | (7 << 8) | 5, lights.First(l => l.Usage == 0x63).Address);
            Assert.Equal(0x60, picture[7][21]);
            // The model holds its pictures: they land in memory.
            Assert.Equal((150, 500), (model.HoldMs, model.RepaintMs));
        }

        [Fact]
        public void ASession_AsksTheVersion_AndPublishesTheRegionsMap()
        {
            var model = Model();
            var answers = new Queue<byte[]>(new[] { new byte[64], Answer("2NUC,01,KB,FL,K221JPCVRGB,V1.05.03") });
            var io = new FakeReportChannel { OutputReportLength = 64, InputReportLength = 64, Input = () => answers.Count > 0 ? answers.Dequeue() : null };
            var session = model.Begin(io, _ => { });
            Assert.Equal(new[] { "out", "in", "in", "out" }, Calls(io).Select(c => c.Kind));
            Assert.Equal("01 0D 00", Hex(Calls(io)[0].Bytes, 3));
            Assert.Equal("01 07 00 00 00 0E 0A", Hex(Calls(io)[3].Bytes, 7));
            Assert.EndsWith(":Jis", session.Map.Product);
            // The JIS map has the Yen key OpenRGB's JIS overlay adds (lines 107-121).
            Assert.Contains(session.Map.Lights, l => l.Usage == 0x89);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, session.Map.Lights, Distinct(session.Map.Lights.Count)));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(12, io.Calls.Count);
            // ISO, the model's own map, when nothing answers.
            var silent = new FakeReportChannel { OutputReportLength = 64, InputReportLength = 64 };
            Assert.Null(model.Begin(silent, _ => { }).Map);
            Assert.Equal(10, Calls(silent).Count(c => c.Kind == "in"));
        }
    }

    /// <summary>The ZET Blade Optical against OpenRGB's ZETKeyboardController (#557).</summary>
    public class ZetKeyboardLightingTests
    {
        [Fact]
        public void TheLights_AreOpenRgbs104Names()
        {
            var lights = ZetKeyboardLighting.Lights();
            Assert.Equal(104, lights.Count);
            Assert.Equal(Enumerable.Range(0, 104), lights.Select(l => l.Address));
            Assert.Equal(0x29, lights[0].Usage);   // Escape
            Assert.Equal(0x3A, lights[1].Usage);   // F1
            Assert.Equal(0x63, lights[103].Usage); // Number Pad .
        }

        [Fact]
        public void APicture_IsOpenRgbsCustomReports_WithTheSkippedIds()
        {
            var colors = Distinct(104);
            var picture = ZetKeyboardLighting.Picture(colors);
            // 12 keys to a report (lines 141-180): 8 full reports and 8 keys
            // in the ninth, which ends with FF (lines 158-162).
            Assert.Equal(9, picture.Count);
            Assert.All(picture, r => Assert.Equal("04 AE 01 00 00 1C 02 04 FF", Hex(r, 9)));
            // IDs: 0x80 plus the index, stepping over SKIP_INDICES 1, 17 to
            // 20, ... (ZETBladeOpticalController.cpp:27 and 116-133).
            Assert.Equal(new byte[] { 0x80, 0x82, 0x83, 0x84 }, Enumerable.Range(0, 4).Select(q => picture[0][9 + q * 4]));
            Assert.Equal(new[] { (byte)(colors[1] >> 16), (byte)(colors[1] >> 8), (byte)colors[1] }, picture[0].Skip(14).Take(3));
            // Index 16, the backtick, comes after the four skips 17 to 20: 0x95.
            Assert.Equal(0x95, picture[1][9 + 4 * 4]);
            // The last key, Number Pad ., the 104th ID not skipped: 124, the
            // skip 125 lying past it, so 0x80 + 124.
            Assert.Equal(0xFC, picture[8][9 + 7 * 4]);
            Assert.Equal(0xFF, picture[8][9 + 8 * 4]);
            Assert.All(picture[8].Skip(9 + 8 * 4 + 1), b => Assert.Equal(0, b));
        }

        [Fact]
        public void ASession_SendsTheReports_TwelveMillisecondsApart_AndHoldsItsPictures()
        {
            var model = ZetKeyboardLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(ZetKeyboardLighting.OpenRgbFamily).Single(), output: 65));
            Assert.Equal((150, 500), (model.HoldMs, model.RepaintMs));
            var io = new FakeReportChannel { OutputReportLength = 65 };
            var waits = new List<int>();
            var session = model.Begin(io, Recorder(waits));
            Assert.Empty(io.Calls);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, Recorder(waits), model.Map.Lights, Distinct(104)));
            Assert.Equal(9, io.Calls.Count);
            Assert.Equal(Enumerable.Repeat(12, 9), waits);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(9, io.Calls.Count);
            Assert.Null(ZetKeyboardLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(ZetKeyboardLighting.OpenRgbFamily).Single(), output: 33)));
        }
    }

    /// <summary>The XPG Summoner against OpenRGB's XPGSummonerKeyboardController
    /// and SignalRGB's plugin (#557).</summary>
    public class XpgSummonerLightingTests
    {
        private static HidLightingModel Model()
            => XpgSummonerLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(XpgSummonerLighting.OpenRgbFamily).Single(), output: 265, feature: 265));

        [Fact]
        public void TheLights_AreOpenRgbsNames_AtTheirFrameIndexes()
        {
            var lights = XpgSummonerLighting.Lights();
            Assert.Equal(104, lights.Count);
            // matrix_map row 0: Esc 11, F1 22, F2 30 (RGBController_XPGSummoner.cpp:37).
            Assert.Equal(new[] { 11, 22, 30 }, lights.Take(3).Select(l => l.Address));
            Assert.Equal(new ushort[] { 0x29, 0x3A, 0x3B }, lights.Take(3).Select(l => l.Usage));
            // Number Pad . is the last cell, 107 (line 42).
            Assert.Equal((107, (ushort)0x63), (lights[103].Address, lights[103].Usage));
            Assert.Equal(104, lights.Select(l => l.Address).Distinct().Count());
            Assert.All(lights, l => Assert.InRange(l.Address, 0, 125));
        }

        [Fact]
        public void TheReports_AreOpenRgbsAndSignalRgbs()
        {
            var init = XpgSummonerLighting.Initialize();
            Assert.Equal(265, init.Length);
            Assert.Equal("07 EA 00 00 00", Hex(init, 5));
            Assert.Equal("07 A6 00 4D 00", Hex(XpgSummonerLighting.Hardware(), 5));
            var lights = XpgSummonerLighting.Lights();
            var colors = Distinct(lights.Count);
            var picture = XpgSummonerLighting.Picture(lights, colors);
            Assert.Equal(2, picture.Count);
            Assert.Equal("07 A3 08 00 00 00", Hex(picture[0], 6));
            Assert.Equal("07 A3 08 00 01 00", Hex(picture[1], 6));
            // Escape at index 11: 0x64 and its color at 6 + 44.
            Assert.Equal(new[] { (byte)0x64, (byte)(colors[0] >> 16), (byte)(colors[0] >> 8), (byte)colors[0] },
                picture[0].Skip(6 + 44).Take(4));
            // The second report carries bytes 256 to 503 of the frame and zeros after.
            Assert.All(picture[1].Skip(6 + 248), b => Assert.Equal(0, b));
        }

        [Fact]
        public void ASession_Initializes_AndHandsBackWithTheHardwareReport()
        {
            var model = Model();
            Assert.Equal(new[] { VendorPrograms.XpgPrime }, model.Programs);
            Assert.True(VendorPrograms.XpgPrime.Owns("XPG-Prime"));
            Assert.Contains(VendorPrograms.XpgPrime, VendorPrograms.All);
            var io = new FakeReportChannel { OutputReportLength = 265, FeatureReportLength = 265 };
            var waits = new List<int>();
            var session = model.Begin(io, Recorder(waits));
            Assert.Equal(new[] { "out" }, Calls(io).Select(c => c.Kind));
            Assert.True(session.Feature);
            Assert.Equal("07 A6 00 4D", Hex(session.CrashFrames.Single(), 4));
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, Recorder(waits), model.Map.Lights, Distinct(104)));
            Assert.Equal(new[] { 2, 2, 2 }, waits);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(("set", "07 A6 00 4D"), (Calls(io)[^1].Kind, Hex(Calls(io)[^1].Bytes, 4)));
            Assert.Empty(session.CrashFrames);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(4, io.Calls.Count);
        }
    }
}
