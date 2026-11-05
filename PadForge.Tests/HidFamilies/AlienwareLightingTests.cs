using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.LaptopFamilies.LaptopFamilyTest;

namespace PadForge.Tests.LaptopFamilies
{
    /// <summary>The Dell G series controller against OpenRGB's
    /// AlienwareController (#557): the configuration read and the zone
    /// tables (AlienwareController.cpp:30-194), and UpdateDim and
    /// UpdateMode's reports, waits and answer checks (lines 68-907).</summary>
    public class AlienwareLightingTests
    {
        [Fact]
        public void EveryRegistration_FindsItsModel()
        {
            var registrations = OpenRgbRegistrations.Of(AlienwareLighting.OpenRgbFamily).ToList();
            Assert.Equal(new[] { 0x0550, 0x0551 }, registrations.Select(r => r.ProductId));
            foreach (var r in registrations)
            {
                var model = AlienwareLighting.Instance.Match(CollectionOf(r, feature: 34));
                Assert.NotNull(model);
                Assert.Same(model, HidLightingFamilies.Match(CollectionOf(r, feature: 34)));
                Assert.Equal("Dell G Series LED Controller", model.Name);
                Assert.Equal(new[] { VendorPrograms.AlienwareCommandCenter }, model.Programs);
                Assert.Equal(AlienwareLighting.RepaintMs, model.RepaintMs);
                Assert.Equal(4, model.Map.Lights.Count);
                Assert.Null(AlienwareLighting.Instance.Match(CollectionOf(r, feature: 33)));
            }
            Assert.True(VendorPrograms.AlienwareCommandCenter.Owns("AWCC"));
            Assert.True(VendorPrograms.AlienwareCommandCenter.Owns("AWCC.Service"));
            Assert.Contains(VendorPrograms.AlienwareCommandCenter, VendorPrograms.All);
        }

        [Fact]
        public void TheZones_ComeFromThePlatform()
        {
            // The G15 5520 numbers its zones from 0x10 and reports a wrong count.
            Assert.Equal(4, AlienwareLighting.ZoneCount(0x0E07, 9));
            Assert.Equal(new[] { 0x10, 0x11, 0x12, 0x13 }, AlienwareLighting.Lights(0x0E07, 4).Select(l => l.Address));
            // The G7 7500: four keyboard zones and twelve light bar lights.
            var g7 = AlienwareLighting.Lights(0x0A01, AlienwareLighting.ZoneCount(0x0A01, 3));
            Assert.Equal(16, g7.Count);
            Assert.Equal(Enumerable.Range(0, 16), g7.Select(l => l.Address));
            Assert.Equal(new[] { 1, 2, 3, 4 }, g7.Take(4).Select(l => l.Zone));
            Assert.All(g7.Take(4), l => Assert.Equal(LightKind.Zone, l.Kind));
            Assert.All(g7.Skip(4), l => Assert.Equal(LightKind.Edge, l.Kind));
            // An unknown platform: the reported count, IDs from 0.
            Assert.Equal(6, AlienwareLighting.ZoneCount(0x1234, 6));
            Assert.Equal(Enumerable.Range(0, 6), AlienwareLighting.Lights(0x1234, 6).Select(l => l.Address));
            Assert.Equal(new[] { "Unknown", "Unknown" }, AlienwareLighting.ZoneNamesOf(0x1234, 2));
        }

        [Fact]
        public void TheReports_AreOpenRgbs()
        {
            Assert.Equal(Padded("00 03 20 02", 34), AlienwareLighting.ConfigRequest());
            Assert.Equal(Padded("00 03 21 00 01 FF FF 00 00", 34), AlienwareLighting.Animation(0x0001, 0xFFFF));
            Assert.Equal(Padded("00 03 21 00 03 FF FF 00 00", 34), AlienwareLighting.Animation(0x0003, 0xFFFF));
            Assert.Equal(Padded("00 03 23 01 00 01 12", 34), AlienwareLighting.Select(new byte[] { 0x12 }, 0, 1));
            Assert.Equal(Padded("00 03 24 00 07 D0 00 FA 11 22 33", 34), AlienwareLighting.ColorAction(0x112233));
            Assert.Equal(Padded("00 03 26 00 00 03 00 01 02", 34), AlienwareLighting.Dim(0, new byte[] { 0, 1, 2 }, 0, 3));
        }

        /// <summary>The answer of a command fails when it equals the
        /// request or byte 1 is not 03 (AlienwareController.cpp:341).</summary>
        [Fact]
        public void AnAnswer_TookWhenItDiffersAndCarries03()
        {
            var report = AlienwareLighting.Select(new byte[] { 1 }, 0, 1);
            Assert.False(AlienwareLighting.Took(report, (byte[])report.Clone()));
            var answer = (byte[])report.Clone();
            answer[20] = 1;
            Assert.True(AlienwareLighting.Took(report, answer));
            answer[1] = 0x02;
            Assert.False(AlienwareLighting.Took(report, answer));
            Assert.False(AlienwareLighting.Took(report, null));
        }

        /// <summary>A controller that answers with byte 20 set, so no
        /// answer equals its request, and with byte 5 clear.</summary>
        private static LoggingHidChannel Controller(int platform, int zones)
        {
            var io = new LoggingHidChannel { FeatureReportLength = 34 };
            var config = new byte[34];
            config[1] = 0x03;
            config[4] = (byte)(platform >> 8);
            config[5] = (byte)platform;
            config[6] = (byte)zones;
            io.Answers.Enqueue(config);
            io.Echo = sent =>
            {
                var answer = (byte[])sent.Clone();
                answer[20] ^= 0x5A;
                answer[5] = 0;
                return answer;
            };
            return io;
        }

        [Fact]
        public void ASession_ReadsThePlatform_AndPaintsAsUpdateModeDoes()
        {
            var io = Controller(0x0E07, 4);
            var model = AlienwareLighting.Instance.Match(CollectionOf(OpenRgbRegistrations.Of(AlienwareLighting.OpenRgbFamily).First(), feature: 34));
            var session = model.Begin(io, io.Wait);
            Assert.NotNull(session);
            Assert.Equal(new[] { "F 00 03 20 02", "W 60", "G 00" }, io.Lines(4));
            Assert.Equal(new[] { 0x10, 0x11, 0x12, 0x13 }, session.Map.Lights.Select(l => l.Address));
            io.Log.Clear();

            var colors = new[] { 0x110000, 0x002200, 0x000033, 0x444444 };
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, session.Map.Lights, colors));
            var expected = new List<string>
            {
                "F 00 03 26 00 00 04 10 11 12 13 00", "W 60", "G 00",
                "F 00 03 21 00 01 FF FF 00 00 00 00", "W 60", "G 00",
            };
            for (int z = 0; z < 4; z++)
            {
                expected.Add($"F 00 03 23 01 00 01 {0x10 + z:X2} 00 00 00 00");
                expected.AddRange(new[] { "W 60", "G 00" });
                expected.Add("F 00 03 24 00 07 D0 00 FA " + Hex(new[] { (byte)(colors[z] >> 16), (byte)(colors[z] >> 8), (byte)colors[z] }));
                expected.AddRange(new[] { "W 60", "G 00" });
            }
            // 60 ms after "finish and play" too: SendHIDReport looks for the
            // step in byte 3, which holds its high byte (AlienwareController.cpp:80-92).
            expected.AddRange(new[] { "F 00 03 21 00 03 FF FF 00 00 00 00", "W 60", "G 00" });
            Assert.Equal(expected, io.Lines(11));

            // The dim level goes once a session.
            io.Log.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, session.Map.Lights, colors));
            Assert.DoesNotContain(io.Lines(3), l => l == "F 00 03 26");
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(session.CrashFrames);
        }

        [Fact]
        public void ARefusedStep_FailsThePicture()
        {
            var io = Controller(0x0C01, 4);
            var session = AlienwareLighting.Instance.Match(CollectionOf(OpenRgbRegistrations.Of(AlienwareLighting.OpenRgbFamily).First(), feature: 34))
                .Begin(io, io.Wait);
            // An answer equal to the request is a refusal.
            io.Echo = sent => (byte[])sent.Clone();
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, io.Wait, session.Map.Lights, new int[4]));
            // Finish and play refused in byte 5.
            var io2 = Controller(0x0C01, 4);
            var s2 = AlienwareLighting.Instance.Match(CollectionOf(OpenRgbRegistrations.Of(AlienwareLighting.OpenRgbFamily).First(), feature: 34))
                .Begin(io2, io2.Wait);
            io2.Echo = sent =>
            {
                var answer = (byte[])sent.Clone();
                answer[20] ^= 0x5A;
                answer[5] = sent[4] == 0x03 && sent[2] == 0x21 ? (byte)1 : (byte)0;
                return answer;
            };
            Assert.Equal(HidLightingPaint.Failed, s2.Paint(io2, io2.Wait, s2.Map.Lights, new int[4]));
        }

        [Fact]
        public void AControllerWithNoAnswerOrNoZones_StartsNoSession()
        {
            var model = AlienwareLighting.Instance.Match(CollectionOf(OpenRgbRegistrations.Of(AlienwareLighting.OpenRgbFamily).First(), feature: 34));
            Assert.Null(model.Begin(new LoggingHidChannel(), _ => { }));
            Assert.Null(model.Begin(Controller(0x1234, 0), _ => { }));
        }
    }

    /// <summary>The AW410K and AW510K against OpenRGB's
    /// AlienwareKeyboardController (#557): the tables and DeviceUpdateLEDs'
    /// reports (AlienwareAW410KController.cpp:54-304,
    /// RGBController_AlienwareAW410K.cpp:323-352 and
    /// RGBController_AlienwareAW510K.cpp:322-373).</summary>
    public class AlienwareKeyboardLightingTests
    {
        private static HidLightingModel ModelOf(ushort pid)
        {
            var r = OpenRgbRegistrations.Of(AlienwareKeyboardLighting.OpenRgbFamily).Single(x => x.ProductId == pid);
            return AlienwareKeyboardLighting.Instance.Match(CollectionOf(r, feature: 65, output: 65));
        }

        [Fact]
        public void EveryRegistration_FindsItsModel()
        {
            var registrations = OpenRgbRegistrations.Of(AlienwareKeyboardLighting.OpenRgbFamily).ToList();
            Assert.Equal(2, registrations.Count);
            foreach (var r in registrations)
            {
                var model = AlienwareKeyboardLighting.Instance.Match(CollectionOf(r, feature: 65, output: 65));
                Assert.NotNull(model);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(model, HidLightingFamilies.Match(CollectionOf(r, feature: 65, output: 65)));
                Assert.Equal(new[] { VendorPrograms.AlienwareCommandCenter }, model.Programs);
                Assert.Null(AlienwareKeyboardLighting.Instance.Match(CollectionOf(r, feature: 65, output: 64)));
                Assert.Null(AlienwareKeyboardLighting.Instance.Match(CollectionOf(r, feature: 64, output: 65)));
            }
        }

        [Fact]
        public void TheLights_AreOpenRgbsTables()
        {
            var aw410 = ModelOf(0x1968).Map;
            Assert.Equal(107, aw410.Lights.Count);
            Assert.Equal(AlienwareKeyboardLighting.Aw410kLeds.Select(l => (int)l.Value), aw410.Lights.Select(l => l.Address));
            // Escape is index 0xB0 at the top left (led_names line 55, matrix_map line 23).
            Assert.Equal((ushort)0x29, aw410.Lights[0].Usage);
            Assert.Equal(0xB0, aw410.Lights[0].Address);
            Assert.Equal((0f, 0f), (aw410.Lights[0].X, aw410.Lights[0].Y));
            Assert.All(aw410.Lights, l => Assert.True(l.HasPosition));
            // The three media keys have no Keyboard page usage.
            var mute = aw410.Lights[16];
            Assert.Equal(LightKind.Other, mute.Kind);
            Assert.Equal(LightLabel.Mute, mute.Label);
            var aw510 = ModelOf(0x1830).Map;
            Assert.Equal(106, aw510.Lights.Count);
            Assert.Equal(LightKind.Logo, aw510.Lights[^1].Kind);
            Assert.Equal(0x07, aw510.Lights[^1].Address);
            Assert.All(aw510.Lights, l => Assert.True(l.HasPosition));
        }

        [Fact]
        public void TheReports_AreOpenRgbs()
        {
            Assert.Equal(Padded("00 0E 01 00 01 AD 80 10 A5 00 0A 00 00 00 00 00 00 00 01", 65), AlienwareKeyboardLighting.Initialize());
            Assert.Equal(Padded("00 05 01 00 00 00 00 00 00 00 10 0A 01 02 01", 65), AlienwareKeyboardLighting.Commit());
            Assert.Equal(Padded("00 05 01 51 00", 65), AlienwareKeyboardLighting.FeatureFrame(0x05, 0x01, 0x51, 0x00));
        }

        /// <summary>Four keys to a report, each slot index 81 00 A5 00 0A
        /// R G B 00 00 00 00 01 00, numbered from 1. Five keys pad to eight,
        /// the padding loop adding three.</summary>
        [Fact]
        public void TheKeys_GoFourToAReport()
        {
            var keys = Enumerable.Range(0, 5).Select(i => ((byte)(0xB0 + i), 0x010203 * (i + 1))).ToList();
            var reports = AlienwareKeyboardLighting.DirectFrames(keys);
            Assert.Equal(2, reports.Count);
            Assert.Equal("00 0E 01 00 01 B0 81 00 A5 00 0A 01 02 03 00 00 00 00 01 00 B1 81 00 A5 00 0A 02 04 06 00 00 00 00 01",
                Hex(reports[0], 34));
            Assert.Equal("00 0E 01 00 02 B4 81 00 A5 00 0A 05 0A 0F", Hex(reports[1], 14));
            // The padded slots carry index 0 and black.
            Assert.Equal("00 81 00 A5 00 0A 00 00 00", Hex(reports[1].Skip(20).ToArray(), 9));
            // 107 keys pad to 108: 27 reports.
            Assert.Equal(27, AlienwareKeyboardLighting.DirectFrames(Enumerable.Repeat(((byte)1, 0), 107).ToList()).Count);
        }

        [Fact]
        public void APicture_IsInitializeFeatureCommitCountAndKeys()
        {
            var map = ModelOf(0x1968).Map;
            var io = new LoggingHidChannel();
            var session = new AlienwareKeyboardLighting.Session(false);
            Assert.False(session.Feature);
            var colors = Colors(map.Lights.Count, n => 0x10101 * n);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, map.Lights, colors));
            var lines = io.Lines(5);
            Assert.Equal(new[] { "O 00 0E 01 00 01", "W 2", "F 00 05 01 51 00", "W 10", "O 00 05 01 00 00", "W 20",
                "F 00 0E 6B 00 01", "W 10" }, lines.Take(8));
            Assert.Equal(27, lines.Skip(8).Count());
            Assert.All(lines.Skip(8), l => Assert.StartsWith("O 00 0E 01 00", l));
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(session.CrashFrames);
        }

        [Fact]
        public void TheAw510k_SendsEveryKeyFirst_ThenTheChangedOnes()
        {
            var map = ModelOf(0x1830).Map;
            var session = new AlienwareKeyboardLighting.Session(true);
            var colors = new int[map.Lights.Count];
            var io = new LoggingHidChannel();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, map.Lights, colors));
            Assert.Contains("F 00 0E 6A 00 01", io.Lines(5));
            colors[3] = 0xFFFFFF;
            io.Log.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, map.Lights, colors));
            var lines = io.Lines(9);
            Assert.Contains("F 00 0E 01 00 01 00 00 00 00", lines);
            Assert.Equal("O 00 0E 01 00 01 " + Hex(new[] { (byte)map.Lights[3].Address }) + " 81 00 A5", lines[^1]);
            // Nothing changed: the three setup reports alone.
            io.Log.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, map.Lights, colors));
            Assert.Equal(new[] { "O", "W", "F", "W", "O", "W" }, io.Log.Select(e => e.Kind));
        }

        [Fact]
        public void AFailedWrite_FailsThePicture()
        {
            var map = ModelOf(0x1968).Map;
            for (int after = 0; after < 5; after++)
                Assert.Equal(HidLightingPaint.Failed, new AlienwareKeyboardLighting.Session(false)
                    .Paint(new LoggingHidChannel { FailAfter = after }, _ => { }, map.Lights, new int[map.Lights.Count]));
        }
    }
}
