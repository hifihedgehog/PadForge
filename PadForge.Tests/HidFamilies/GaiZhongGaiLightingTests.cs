using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using G = PadForge.Common.Input.Peripherals.GaiZhongGaiLighting;

namespace PadForge.Tests
{
    /// <summary>The GaiZhongGai family against OpenRGB's
    /// GaiZongGaiKeyboardController at the pinned commit (#557).</summary>
    [Collection("PeripheralOutputStatics")]
    public class GaiZhongGaiLightingTests
    {
        private static string Hex(byte[] bytes, int count) => ReportLog.Hex(bytes, count);
        private static string Hex(byte[] bytes, int from, int count) => ReportLog.Hex(bytes, from, count);

        private static HidCollectionInfo Raw(ushort pid, ushort output = 65)
            => KeyboardFamilyRig.Collection(0x3061, pid, 0xFF00, 0x01, iface: 3, output: output, input: 65);

        private static readonly IReadOnlyList<IHidLightingFamily> Families = new IHidLightingFamily[] { G.Instance };

        [Fact]
        public void TheTables_AreOpenRgbsAtThePinnedCommit()
        {
            Assert.Equal("b9d903b544a40486709c4ca097d0b234b617db9c", GaiZhongGaiLayouts.OpenRgbCommit);
            // RGBController_GaiZhongGai.cpp:21-70 with led_names_general and
            // led_names_42key (lines 85-233).
            Assert.Equal(68, GaiZhongGaiLayouts.Pro68.Length);
            Assert.Equal(new GaiZhongGaiLed(0, "Key: Left Control", 4, 0), GaiZhongGaiLayouts.Pro68[0]);
            Assert.Equal(new GaiZhongGaiLed(52, "Key: Escape", 0, 0), GaiZhongGaiLayouts.Pro68[52]);
            Assert.Equal(new GaiZhongGaiLed(41, "Key: Escape", 0, 0), GaiZhongGaiLayouts.Pro42[41]);
            Assert.Equal(17, GaiZhongGaiLayouts.Pad17.Length);
            Assert.Equal(20, GaiZhongGaiLayouts.Pad20.Length);
            Assert.Equal(20, GaiZhongGaiLayouts.PadTouch.Length);
            Assert.Equal(new[] { 85, 86, 87, 88 }, GaiZhongGaiLayouts.Dial.Select(l => l.Number));
        }

        [Fact]
        public void EveryRegistration_IsMatchedOnItsOutputCollection()
        {
            var registrations = OpenRgbRegistrations.Of(G.FamilyName).ToList();
            Assert.Equal(9, registrations.Count);
            foreach (var r in registrations)
            {
                Assert.Equal(r.Name, G.Instance.Match(Raw((ushort)r.ProductId)).Name);
                // A collection of the interface without the 65-byte output
                // report is not the one OpenRGB writes to.
                Assert.Null(G.Instance.Match(Raw((ushort)r.ProductId, output: 0)));
            }
            Assert.Null(G.Instance.Match(KeyboardFamilyRig.Collection(0x3061, G.Pro68, 0xFF00, 1, iface: 2, output: 65)));
        }

        [Fact]
        public void TheLights_AreTheMatrixLights_StripsAsZones()
        {
            var pad = G.Lights(G.Pad20);
            Assert.Equal(20, pad.Count);
            Assert.Equal(0x58, pad.Single(l => l.Address == 68).Usage);
            Assert.Equal((LightKind.Zone, 2), (pad.Single(l => l.Address == 86).Kind, pad.Single(l => l.Address == 86).Zone));
            Assert.Equal(4, new LightMap(G.Lights(G.Dial), "t").ZoneCount);
        }

        private static (IReadOnlyList<LightInfo>, int[]) All(ushort pid, int rgb)
        {
            var lights = G.Lights(pid);
            return (lights, Enumerable.Repeat(rgb, lights.Count).ToArray());
        }

        [Fact]
        public void TheBoardReports_AreOpenRgbsGreenRedBlue()
        {
            // SendColors (GaiZhongGaiController.cpp:205-257), each light
            // green, red, blue (RGBController_GaiZhongGai.cpp:703-705).
            var (lights, colors) = All(G.Pro68, 0x112233);
            var frames = G.BoardFrames(G.Pro68, lights, colors);
            Assert.Equal(new byte[] { 0x10, 0x11, 0x12, 0x13 }, frames.Select(f => f.Report[1]));
            Assert.Equal(new[] { true, true, true, false }, frames.Select(f => f.Wait));
            Assert.Equal("00 10 22 11 33 22 11 33", Hex(frames[0].Report, 8));
            Assert.Equal("22 11 33", Hex(frames[0].Report, 62, 3));
            // The fourth carries lights 63 to 67, 15 bytes, then zeros.
            Assert.Equal("22 11 33", Hex(frames[3].Report, 14, 3));
            Assert.Equal(0, frames[3].Report[17]);
            var pad17 = G.BoardFrames(G.Pad17, G.Lights(G.Pad17), Enumerable.Repeat(0x0000FF, 17).ToArray());
            var only = Assert.Single(pad17);
            Assert.Equal("00 10 00 00 FF", Hex(only.Report, 5));
            Assert.Equal("00 00 FF 00", Hex(only.Report, 50, 4));
            var dial = Assert.Single(G.BoardFrames(G.Dial, G.Lights(G.Dial), new[] { 0xFF0000, 0, 0, 0x00FF00 }));
            Assert.Equal("00 10 00 FF 00 00 00 00 00 00 00 FF 00 00 00", Hex(dial.Report, 15));
        }

        [Fact]
        public void TheStreamReports_AreNumbered_TheOneAfterTheLastWholeIncluded()
        {
            // Lines 259-283: i up to size / 60, so 20 lights make two.
            var lights = Enumerable.Range(0, 20).Select(i => new LightInfo(i, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 }).ToList();
            var frames = G.StreamFrames(20, lights, Enumerable.Repeat(0x010203, 20).ToArray());
            Assert.Equal(2, frames.Count);
            Assert.Equal("00 00 02 01 03", Hex(frames[0], 5));
            Assert.Equal(1, frames[1][1]);
            Assert.All(frames[1].Skip(2), b => Assert.Equal(0, b));
            Assert.Single(G.StreamFrames(19, lights.Take(19).ToList(), new int[19]));
        }

        [Fact]
        public void TheLightBoard_LaysOutAHexagonsLights()
        {
            // One hexagon (0x20, top bits 1, no child bits), then the end
            // (0xE0): six lights, one at every other of its twelve points
            // (RGBController_GaiZhongGai.cpp:267-339). Scale 100, width 32.
            var flash = new byte[128];
            flash[0] = 0x20;
            flash[1] = 0xE0;
            flash[120] = 100;
            flash[124] = 32;
            Assert.Equal(6, G.LightBoardPositions(flash).Count);
            // A hexagon whose first side holds a square: 6 + 4 lights.
            flash[0] = 0x21;
            flash[1] = 0x08;
            Assert.Equal(10, G.LightBoardPositions(flash).Count);
            // An empty record lays out nothing.
            Assert.Empty(G.LightBoardPositions(new byte[128]));
        }

        [Fact]
        public void AClaim_SendsTheReports_AndTheHandBackRestoresTheBuiltInEffect()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 65 };
            var collection = Raw(G.Pro42);
            using var backend = rig.Lit(Families, collection, 0xFF0000);
            var outputs = rig.Log.Of('O');
            Assert.Equal(2, outputs.Count);
            Assert.Equal("00 10 00 FF 00", Hex(outputs[0], 5));
            Assert.Equal("00 FF", Hex(Assert.Single(Assert.Single(backend.Recorded).Frames), 2));
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Equal("00 FF 00", Hex(Assert.Single(rig.Log.Of('O')), 3));
        }

        [Fact]
        public void AHub_WritesACountLikeSetChannelLen_AndReadsItsLightsBack()
        {
            byte[] lengths = { 0x00, 0x03, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0 };
            var log = new ReportLog();
            log.InputAnswer = o =>
            {
                var written = log.Of('O').LastOrDefault(r => r[1] == 0x86);
                if (written != null) Array.Copy(written, 3, lengths, 0, 16);
                if (o[1] == 0x88) lengths[7] = 0x09;
                if (o[1] != 0x87 && o[1] != 0x88) return null;
                var answer = new byte[65];
                answer[1] = o[1];
                Array.Copy(lengths, 0, answer, 3, 16);
                return answer;
            };
            var io = new ReportLogChannel { Device = log, Path = "hub" };
            var session = G.Session.Begin(io, G.HubGreen, "p");
            Assert.Equal(new[] { 1, 2, 3, 4 }.Select(n => new LightStrip(n, 637, 0)), session.Map.Strips);
            Assert.Equal(5, session.Map.Lights.Count);

            // Channel 2 takes four lights: SetChannelLen's 0x86 with every
            // channel's count from byte 3, then 0x87 (GaiZhongGaiController.cpp:126-145).
            log.Clear();
            int waited = 0;
            Assert.True(session.SizeStrips(io, ms => waited += ms, new[] { 0, 4 }));
            var outputs = log.Of('O');
            Assert.Equal(2, outputs.Count);
            Assert.Equal("00 86 00 00 03 00 04 00 02", Hex(outputs[0], 9));
            Assert.Equal("00 87", Hex(outputs[1], 2));
            Assert.Equal(50, waited);
            Assert.Equal(new[] { 1, 1, 1, 2, 2, 2, 2, 3, 3 }, session.Map.Lights.Select(l => l.Zone));
            Assert.Equal(new[] { new LightStrip(1, 637, 0), new LightStrip(2, 637, 4) }, session.Map.Strips.Take(2));

            // The count it holds already goes nowhere.
            log.Clear();
            Assert.True(session.SizeStrips(io, _ => { }, new[] { 0, 4 }));
            Assert.Empty(log.Of('O'));

            // The green hub's fourth channel at its most is an order to
            // measure (lines 104-119): 0x88, and the hub's answer.
            log.Clear();
            Assert.True(session.SizeStrips(io, _ => { }, new[] { 0, 4, 0, 637 }));
            Assert.Equal("00 88", Hex(Assert.Single(log.Of('O')), 2));
            Assert.Equal(3 + 4 + 2 + 9, session.Map.Lights.Count);
        }

        /// <summary>A hub can hold another count than the one written. The map
        /// follows what it answers, as OpenRGB's reads its counts back after
        /// the write (GaiZhongGaiController.cpp:126-145).</summary>
        [Fact]
        public void AHub_TakesTheCountsItAnswers_NotTheOnesWritten()
        {
            byte[] lengths = { 0x00, 0x03, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0 };
            var log = new ReportLog();
            log.InputAnswer = o =>
            {
                if (o[1] != 0x87) return null;
                var answer = new byte[65];
                answer[1] = o[1];
                Array.Copy(lengths, 0, answer, 3, 16);
                return answer;
            };
            var io = new ReportLogChannel { Device = log, Path = "hub" };
            var session = G.Session.Begin(io, G.HubGreen, "p");
            // The hub stays at its first counts, whatever the write asked.
            Assert.True(session.SizeStrips(io, _ => { }, new[] { 0, 4 }));
            Assert.Equal(new[] { 1, 1, 1, 3, 3 }, session.Map.Lights.Select(l => l.Zone));
        }

        /// <summary>The blue hub has eight channels and a lower most, and a board
        /// that is no hub has no strips.</summary>
        [Fact]
        public void TheBlueHub_HasEightStrips_AndABoardHasNone()
        {
            Assert.Equal(Enumerable.Range(1, 8).Select(n => new LightStrip(n, 633, 0)), G.HubStrips(G.HubBlue, null));
            var log = new ReportLog();
            var io = new ReportLogChannel { Device = log, Path = "board" };
            var session = G.Session.Begin(io, G.Pro68, "p");
            Assert.True(session.SizeStrips(io, _ => { }, new[] { 4 }));
            Assert.Empty(log.Of('O'));
            Assert.Null(session.Map);
        }

        [Fact]
        public void AHub_ReadsItsChannelCounts_AndLightsEachChannelAsAZone()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 65, InputLength = 65 };
            // 0x87 answers the counts from byte 2: channel 1 three lights,
            // channel 3 two (GaiZhongGaiController.cpp:80-84).
            rig.Log.InputAnswer = o => o[1] == 0x87
                ? new byte[] { 0x00, 0x87, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x02 }
                : null;
            var collection = Raw(G.HubGreen);
            using var backend = rig.Lit(Families, collection, 0x00FF00);
            var map = PeripheralLightMaps.For(KeyboardFamilyRig.PathOf(collection));
            Assert.Equal(new[] { 1, 1, 1, 3, 3 }, map.Lights.Select(l => l.Zone));
            var outputs = rig.Log.Of('O');
            Assert.Equal("00 87", Hex(outputs[0], 2));
            Assert.Equal("00 00 FF 00 00 FF 00 00", Hex(outputs[1], 8));
        }
    }
}
