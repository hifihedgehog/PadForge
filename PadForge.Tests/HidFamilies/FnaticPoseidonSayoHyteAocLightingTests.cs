using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Fnatic Streak, miniStreak and Streak65 against OpenRGB's
    /// FnaticStreakController (#557).</summary>
    public class FnaticStreakLightingTests
    {
        private static HidLightingModel Model(FnaticStreakLighting family, int pid)
            => family.Match(CollectionFor(OpenRgbRegistrations.Of(FnaticStreakLighting.OpenRgbFamily).First(r => r.ProductId == pid), output: 65));

        [Fact]
        public void TheMaps_AreOpenRgbsLedIndexes_AndTheStreak65sOneColor()
        {
            var full = FnaticStreakLighting.Lights(0x0101);
            var mini = FnaticStreakLighting.Lights(0x0102);
            var one = FnaticStreakLighting.Lights(0x0105);
            // led_names_full: 0 Fn Lock, 1 Escape, 2 backtick (RGBController_FnaticStreak.cpp:69-75).
            Assert.Equal(LightKind.Other, full[0].Kind);
            Assert.Equal((1, (ushort)0x29), (full[1].Address, full[1].Usage));
            Assert.All(full, l => Assert.InRange(l.Address, 0, 123));
            Assert.All(mini, l => Assert.InRange(l.Address, 0, 105));
            Assert.Equal(full.Count, full.Select(l => l.Address).Distinct().Count());
            Assert.Equal(LightKind.Zone, Assert.Single(one).Kind);
        }

        [Fact]
        public void TheRequests_AreOpenRgbs()
        {
            // SendRequest: ID 0, the command, the total length and the offset
            // as three little-endian bytes, 57 bytes of the request
            // (FnaticStreakController.cpp:128-170).
            var full = FnaticStreakLighting.Picture(124, FnaticStreakLighting.Lights(0x0101), Enumerable.Repeat(0xABCDEF, 124).ToArray());
            // 2 + 372 bytes: seven reports.
            Assert.Equal(7, full.Count);
            Assert.Equal("00 0F 76 01 00 00 00 00 0F 03", Hex(full[0], 10));
            Assert.Equal("00 0F 76 01 00 39 00 00", Hex(full[1], 8));
            Assert.Equal("00 0F 76 01 00 56 01 00", Hex(full[6], 8));
            // LED 0's red right after 0F 03, LED 1's after it.
            Assert.Equal("AB CD EF AB CD EF", Hex(full[0].Skip(10).ToArray(), 6));
            var mini = FnaticStreakLighting.Picture(106, FnaticStreakLighting.Lights(0x0102), Distinct(FnaticStreakLighting.Lights(0x0102).Count));
            Assert.Equal("00 0F 40 01 00", Hex(mini[0], 5));
            Assert.Equal(6, mini.Count);
            // The keep-alive 07 (lines 110-119).
            Assert.Equal("00 07 01 00 00 00 00 00 07 00", Hex(FnaticStreakLighting.KeepAlive(), 10));
            // The Streak65's one color (lines 233-254).
            Assert.Equal("0F 15 00 00 00 00 00 0F 03 FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF 11 22 33 00",
                Hex(FnaticStreakLighting.Streak65Picture(0x112233), 29));
            Assert.Equal(64, FnaticStreakLighting.Streak65Picture(0).Length);
        }

        [Fact]
        public void ASession_KeepsItAlive_OnlyAfterHalfASecondIdle()
        {
            long now = 1000;
            var family = new FnaticStreakLighting(() => now);
            var model = Model(family, 0x0101);
            Assert.Equal(100, model.RecheckMs);
            var io = new FakeReportChannel { OutputReportLength = 65 };
            var session = model.Begin(io, _ => { });
            Assert.Empty(io.Calls);
            now = 1400;
            Assert.True(session.Recheck(io, _ => { }));
            Assert.Empty(io.Calls);
            now = 1501;
            Assert.True(session.Recheck(io, _ => { }));
            Assert.Equal("00 07 01", Hex(Calls(io).Single().Bytes, 3));
            // Every check while idle sends one (RGBController_FnaticStreak.cpp:683-699).
            now = 1601;
            Assert.True(session.Recheck(io, _ => { }));
            Assert.Equal(2, io.Calls.Count);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, Distinct(model.Map.Lights.Count)));
            Assert.Equal(2 + 7, io.Calls.Count);
            now = 2000;
            Assert.True(session.Recheck(io, _ => { }));
            Assert.Equal(9, io.Calls.Count);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(9, io.Calls.Count);
            Assert.Empty(session.CrashFrames);
            io.FailWrites = true;
            now = 3000;
            Assert.False(session.Recheck(io, _ => { }));
        }

        [Fact]
        public void TheStreak65Session_SendsItsOneReport()
        {
            var model = Model(FnaticStreakLighting.Instance, 0x0105);
            var io = new FakeReportChannel { OutputReportLength = 64 };
            var session = model.Begin(io, _ => { });
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x445566 }));
            Assert.Equal("0F 15", Hex(Calls(io).Single().Bytes, 2));
            Assert.Equal("44 55 66", Hex(Calls(io).Single().Bytes.Skip(25).ToArray(), 3));
        }
    }

    /// <summary>The Thermaltake Poseidon Z RGB against OpenRGB's
    /// ThermaltakePoseidonZRGBController (#557).</summary>
    public class PoseidonZRgbLightingTests
    {
        [Fact]
        public void APicture_IsTheTwoDirectReports()
        {
            var lights = PoseidonZRgbLighting.Lights();
            Assert.Equal(104, lights.Count);
            // led_names starts Escape, backtick, Tab (RGBController_ThermaltakePoseidonZRGB.cpp:41-48).
            Assert.Equal(new ushort[] { 0x29, 0x35, 0x2B }, lights.Take(3).Select(l => l.Usage));
            var colors = Distinct(104);
            var (rg, blue) = PoseidonZRgbLighting.Picture(lights, colors);
            Assert.Equal(264, rg.Length);
            Assert.Equal("07 0E 01 01 00 00 00 00", Hex(rg, 8));
            Assert.Equal("07 0E 01 02 00 00 00 00", Hex(blue, 8));
            // keys[0] is 0x08, keys[103] is 0x81 (ThermaltakePoseidonZRGBController.cpp:18-26).
            Assert.Equal((byte)(colors[0] >> 16), rg[0x08]);
            Assert.Equal((byte)(colors[0] >> 8), rg[0x08 + 128]);
            Assert.Equal((byte)colors[0], blue[0x08]);
            Assert.Equal((byte)(colors[103] >> 8), rg[0x81 + 128]);
        }

        [Fact]
        public void ASession_SendsTheDirectReports_FiveMillisecondsApart()
        {
            var model = PoseidonZRgbLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(PoseidonZRgbLighting.OpenRgbFamily).Single(), feature: 264));
            var io = new FakeReportChannel { FeatureReportLength = 264 };
            var waits = new List<int>();
            var session = model.Begin(io, Recorder(waits));
            Assert.Empty(io.Calls);
            Assert.True(session.Feature);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, Recorder(waits), model.Map.Lights, Distinct(104)));
            Assert.Equal(new[] { "set", "set" }, Calls(io).Select(c => c.Kind));
            Assert.Equal(new[] { 5 }, waits);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(2, io.Calls.Count);
            Assert.Null(PoseidonZRgbLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(PoseidonZRgbLighting.OpenRgbFamily).Single(), feature: 65)));
        }
    }

    /// <summary>The SayoDevice E1 against OpenRGB's SayoDeviceController (#557).</summary>
    public class SayoDeviceLightingTests
    {
        [Fact]
        public void TheDirectReport_IsSetModesPacket_WithItsChecksum()
        {
            // 21 12, then 0x1221 plus the command's little-endian words:
            // 001C 0011 0001 0000 0015 0028 0026 004C 0026 0000 00C0 8080
            // 2211 0033 sum to A487, and with 1221, B6A8 (SendPacket,
            // SayoDeviceController.cpp:79-111; SetMode, lines 40-65).
            Assert.Equal(0xC0, SayoDeviceLighting.DirectModeByte);
            var report = SayoDeviceLighting.Packet(SayoDeviceLighting.DirectCommand(0x112233));
            Assert.Equal(64, report.Length);
            Assert.Equal("21 12 A8 B6 1C 00 11 00 01 00 00 00 15 00 28 00 26 00 4C 00 26 00 00 00 C0 00 80 80 11 22 33 00",
                Hex(report, 32));
        }

        [Fact]
        public void ASession_WritesThenReadsAwayWhatWaits()
        {
            var model = SayoDeviceLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(SayoDeviceLighting.OpenRgbFamily).Single(), output: 64));
            Assert.Single(model.Map.Lights);
            int waiting = 2;
            var io = new FakeReportChannel { OutputReportLength = 64, Input = () => waiting-- > 0 ? new byte[64] : null };
            var session = model.Begin(io, _ => { });
            Assert.Empty(io.Calls);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x112233 }));
            // The write, two reports waiting, and the read that finds none
            // (lines 115-127).
            Assert.Equal(new[] { "out", "in", "in", "in" }, Calls(io).Select(c => c.Kind));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(4, io.Calls.Count);
            // A device that never stops sending is read a bounded number of times.
            var chatty = new FakeReportChannel { OutputReportLength = 64, Input = () => new byte[64] };
            Assert.Equal(HidLightingPaint.Sent, session.Paint(chatty, _ => { }, model.Map.Lights, new[] { 0 }));
            Assert.Equal(1 + SayoDeviceLighting.FlushReads, chatty.Calls.Count);
        }
    }

    /// <summary>The HYTE Keeb TKL against OpenRGB's HYTEKeyboardController (#557).</summary>
    public class HyteKeyboardLightingTests
    {
        [Fact]
        public void TheLights_AreOpenRgbsLayout_AndTheUnderglow()
        {
            var lights = HyteKeyboardLighting.Lights();
            Assert.Equal(63, lights.Count(l => l.Kind == LightKind.Edge));
            Assert.Equal(HyteKeyboardLighting.UnderglowBase, lights.First(l => l.Kind == LightKind.Edge).Address);
            // hyte_keeb_tkl_values: Escape 0, Right Arrow 121; the space bar
            // lit at 109 to 113 and the media keys over the F-row
            // (RGBController_HYTEKeyboard.cpp:18-58).
            Assert.Equal(0, lights.First(l => l.Usage == 0x29).Address);
            Assert.Equal(121, lights.First(l => l.Usage == 0x4F).Address);
            Assert.Equal(new[] { 109, 110, 111, 112, 113 }, lights.Where(l => l.Usage == 0x2C).Select(l => l.Address).OrderBy(a => a));
            Assert.Contains(lights, l => l.Address == 77 && l.Label == LightLabel.Stop);
            Assert.Contains(lights, l => l.Address == 100 && l.Label == LightLabel.Mute);
        }

        [Fact]
        public void APicture_StreamsTheKeysThenTheUnderglow()
        {
            var model = HyteKeyboardLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(HyteKeyboardLighting.OpenRgbFamily).Single(), output: 65, feature: 9));
            var lights = model.Map.Lights;
            var colors = lights.Select(l => l.Kind == LightKind.Edge ? 0x0A0B0C : 0x010203).ToArray();
            var io = new FakeReportChannel { OutputReportLength = 65, FeatureReportLength = 9 };
            var session = model.Begin(io, _ => { });
            Assert.Empty(io.Calls);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, colors));
            var calls = Calls(io);
            // 00 04 F0 and six reports, 00 04 F1 and three (HYTEKeyboardController.cpp:55-201).
            Assert.Equal(new[] { "set", "out", "out", "out", "out", "out", "out", "set", "out", "out", "out" }, calls.Select(c => c.Kind));
            Assert.Equal("00 04 F0 00 00 00 00 00 00", Hex(calls[0].Bytes));
            Assert.Equal("00 04 F1", Hex(calls[7].Bytes, 3));
            // Escape, value 0: the first three color bytes after report ID 0.
            Assert.Equal("00 01 02 03", Hex(calls[1].Bytes, 4));
            Assert.All(calls.Where(c => c.Kind == "out"), c => Assert.Equal(65, c.Bytes.Length));
            // The underglow's 63 lights, then black for the 64th.
            Assert.Equal("00 0A 0B 0C", Hex(calls[8].Bytes, 4));
            Assert.Equal("0A 0B 0C 00 00 00", Hex(calls[10].Bytes.Skip(65 - 6).ToArray(), 6));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(11, io.Calls.Count);
        }
    }

    /// <summary>The AOC GK500 against OpenRGB's AOCKeyboardController (#557).</summary>
    public class AocKeyboardLightingTests
    {
        [Fact]
        public void TheReports_AreOpenRgbs()
        {
            Assert.Equal("09 21 00", Hex(AocKeyboardLighting.Command(false), 3));
            Assert.Equal("09 22 00", Hex(AocKeyboardLighting.Command(true), 3));
            Assert.Equal(64, AocKeyboardLighting.Command(true).Length);
            // The Custom mode's configuration: 14 01, mode 9 in byte 6, its
            // settings at 7 + 81 with brightness 3, and the checksum
            // 0x4A9E + 14 + 01 + 09 + 03 = 0x4ABF (lines 169-216).
            var config = AocKeyboardLighting.Config(0x09, 0, 0x03, 0, 0, 0);
            Assert.Equal(117, config.Length);
            Assert.Equal("14 01 00 00 00 00 09", Hex(config, 7));
            Assert.Equal("00 00 00 00 00 00 03", Hex(config.Skip(88).ToArray(), 7));
            Assert.Equal("BF 4A", Hex(config.Skip(115).ToArray()));
            // The color report: reds, greens, blues in three runs of 120 by value.
            var lights = AocKeyboardLighting.Lights();
            var colors = lights.Select(_ => 0x112233).ToArray();
            var custom = AocKeyboardLighting.Custom(lights, colors);
            Assert.Equal(361, custom.Length);
            Assert.Equal(0x20, custom[0]);
            // Escape at value 90 (RGBController_AOCKeyboard.cpp:33).
            Assert.Equal(90, lights.First(l => l.Usage == 0x29).Address);
            Assert.Equal((0x11, 0x22, 0x33), (custom[91], custom[211], custom[331]));
        }

        [Fact]
        public void ASession_SelectsTheCustomMode_ThenFramesEachPicture()
        {
            var model = AocKeyboardLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(AocKeyboardLighting.OpenRgbFamily).First(), output: 64, feature: 361));
            var io = new FakeReportChannel { OutputReportLength = 64, FeatureReportLength = 361 };
            var waits = new List<int>();
            var session = model.Begin(io, Recorder(waits));
            Assert.Equal(new[] { "out", "set", "out" }, Calls(io).Select(c => c.Kind));
            Assert.Equal("14 01 00 00 00 00 09", Hex(Calls(io)[1].Bytes, 7));
            Assert.Equal(new[] { 5, 5, 10 }, waits);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, Recorder(waits), model.Map.Lights, Distinct(model.Map.Lights.Count)));
            Assert.Equal(new[] { "out", "set", "out" }, Calls(io).Skip(3).Select(c => c.Kind));
            Assert.Equal(0x20, Calls(io)[4].Bytes[0]);
            Assert.Equal(new[] { 5, 5, 10, 5, 5, 5 }, waits);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(6, io.Calls.Count);
            // Two products, two models.
            var other = AocKeyboardLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(AocKeyboardLighting.OpenRgbFamily).Last(), output: 64, feature: 361));
            Assert.NotSame(model, other);
        }
    }
}
