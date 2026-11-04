using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Mountain Everest and Everest 60 against OpenRGB's
    /// MountainKeyboardController and SignalRGB's plugins (#557).</summary>
    public class MountainKeyboardLightingTests
    {
        private static HidLightingModel Everest()
            => MountainKeyboardLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(MountainKeyboardLighting.OpenRgbFamily).First(r => r.ProductId == 0x0001), output: 65));

        private static HidLightingModel Everest60(int pid = 0x0005)
            => MountainKeyboardLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(MountainKeyboardLighting.OpenRgbFamily).First(r => r.ProductId == pid), feature: 65));

        [Fact]
        public void EveryRegistration_AnswersWithItsModel()
        {
            var registrations = OpenRgbRegistrations.Of(MountainKeyboardLighting.OpenRgbFamily).ToList();
            // MountainKeyboardControllerDetect.cpp:69-71.
            Assert.Equal(3, registrations.Count);
            foreach (var r in registrations)
            {
                var model = MountainKeyboardLighting.Instance.Match(CollectionFor(r, output: 65, feature: 65));
                Assert.NotNull(model);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(model, HidLightingFamilies.Match(CollectionFor(r, output: 65, feature: 65)));
                Assert.Empty(model.Programs);
            }
            // Another interface of the Everest is not its lighting collection.
            var everest = registrations.First(r => r.ProductId == 0x0001);
            Assert.Null(MountainKeyboardLighting.Instance.Match(CollectionFor(everest, output: 65) with { InterfaceNumber = 0 }));
            Assert.Null(MountainKeyboardLighting.Instance.Match(CollectionFor(everest, output: 64)));
        }

        [Fact]
        public void TheEverestsLights_AreOpenRgbsFourZones()
        {
            var lights = Everest().Map.Lights;
            // 87 + 17 keys, 32 + 14 edge lights (RGBController_MountainKeyboard.cpp:26-41).
            Assert.Equal(150, lights.Count);
            Assert.Equal(46, lights.Count(l => l.Kind == LightKind.Edge));
            // matrix_tkl_map_lut: Escape, `, Tab, Caps Lock, Left Shift, Left
            // Control, 1, Left Windows (lines 248-249), at their color indexes
            // (the IDX_KEY_EN enum, lines 96-223).
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 10, 14 }, lights.Take(8).Select(l => l.Address));
            Assert.Equal(new ushort[] { 0x29, 0x35, 0x2B, 0x39, 0xE1, 0xE0, 0x1E, 0xE3 }, lights.Take(8).Select(l => l.Usage));
            // The number pad's first, Num Lock, at index 6 (line 271).
            Assert.Equal((6, (ushort)0x53), (lights[87].Address, lights[87].Usage));
            // The edges at 126 plus IDX_EDGE_16, and the number pad edge's
            // first, IDX_EDGE_31, and last, IDX_EDGE_38 (lines 279 and 297-301).
            Assert.Equal(126 + 16, lights[104].Address);
            Assert.Equal(126 + 31, lights[136].Address);
            Assert.Equal(126 + 38, lights[149].Address);
            Assert.Equal(lights.Count, lights.Select(l => l.Address).Distinct().Count());
            // Right Fn has no usage and carries its label.
            Assert.Single(lights, l => l.Kind == LightKind.Other && l.Label == LightLabel.Fn);
            Assert.Equal(103, lights.Count(l => l.Kind == LightKind.Key));
        }

        [Fact]
        public void TheEverestsReports_AreOpenRgbsCustomMode()
        {
            // SelectMode (MountainKeyboardController.cpp:55-66), as SignalRGB's
            // Initialize writes it (Mountain Everest Max.js:75).
            Assert.Equal("00 14 00 00 00 01 06 00", Hex(MountainKeyboardLighting.SelectMode(0x06), 8));
            // SendColorStartPacketCmd: FF throughout, the header over it
            // (lines 353-366).
            var start = MountainKeyboardLighting.StartReport(100);
            Assert.Equal("00 14 2C 0A 00 FF 64 FF FF", Hex(start, 9));
            Assert.All(start.Skip(7), b => Assert.Equal(0xFF, b));
            Assert.Equal(65, start.Length);
            // A key packet and an edge packet (lines 368-412).
            var key = MountainKeyboardLighting.ColorReport(false, 3, 100, new byte[] { 0xAA, 0xBB });
            Assert.Equal("00 14 2C 00 01 03 64 00 AA BB 00", Hex(key, 11));
            var edge = MountainKeyboardLighting.ColorReport(true, 2, 100, new byte[] { 0xCC });
            Assert.Equal("00 14 2D 0A 00 02 FF 00 CC 00", Hex(edge, 10));
            // The confirm reports (lines 414-428).
            Assert.Equal("00 14 A0 02 00", Hex(MountainKeyboardLighting.ConfirmReport(2), 5));
        }

        [Fact]
        public void AnEverestPicture_IsSevenKeyReportsAndThreeEdgeReports_EachSetFramed()
        {
            var lights = Everest().Map.Lights;
            var colors = Distinct(lights.Count);
            var picture = MountainKeyboardLighting.EverestPicture(lights, colors);
            // Start, 378 bytes in 57-byte packets (7), three confirms; start,
            // 138 bytes (3), three confirms.
            Assert.Equal(1 + 7 + 3 + 1 + 3 + 3, picture.Count);
            Assert.Equal(new[] { 10, 5, 5, 5, 5, 5, 5, 5, 10, 10, 10, 10, 5, 5, 5, 10, 10, 10 }, picture.Select(p => p.WaitMs));
            Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6 }, picture.Skip(1).Take(7).Select(p => p.Report[5]));
            // Escape's color, index 0, opens packet 0 at byte 8.
            Assert.Equal(new[] { (byte)(colors[0] >> 16), (byte)(colors[0] >> 8), (byte)colors[0] },
                picture[1].Report.Skip(8).Take(3));
            // Up Arrow, index 124, in packet 6 at 8 + 372 - 342.
            int up = lights.ToList().FindIndex(l => l.Usage == 0x52);
            Assert.Equal(124, lights[up].Address);
            Assert.Equal((byte)(colors[up] >> 16), picture[7].Report[8 + 30]);
            // The last key packet carries 36 bytes and zeros after them.
            Assert.All(picture[7].Report.Skip(8 + 36), b => Assert.Equal(0, b));
            // Edge index 16, the first edge light, in edge packet 0 at 8 + 48.
            Assert.Equal((byte)(colors[104] >> 8), picture[12].Report[8 + 48 + 1]);
            Assert.Equal(0x2D, picture[12].Report[2]);
            Assert.All(picture.Skip(1).Take(7), p => Assert.Equal(0x64, p.Report[6]));
            // Both start reports carry the Custom mode's brightness, 100.
            Assert.Equal("00 14 2C 0A 00 FF 64", Hex(picture[0].Report, 7));
            Assert.Equal("00 14 2C 0A 00 FF 64", Hex(picture[11].Report, 7));
        }

        [Fact]
        public void AnEverestSession_SelectsTheCustomMode_AndHandsBackNothing()
        {
            var model = Everest();
            var io = new FakeReportChannel { OutputReportLength = 65 };
            var waits = new List<int>();
            var session = model.Begin(io, Recorder(waits));
            Assert.NotNull(session);
            Assert.False(session.Feature);
            Assert.Equal(new[] { "out" }, Calls(io).Select(c => c.Kind));
            Assert.Equal("00 14 00 00 00 01 06", Hex(Calls(io)[0].Bytes, 7));
            Assert.Equal(new[] { 200 }, waits);
            var colors = Distinct(model.Map.Lights.Count);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, Recorder(waits), model.Map.Lights, colors));
            Assert.Equal(19, Calls(io).Count);
            int before = Calls(io).Count;
            Assert.True(session.HandBack(io, Recorder(waits)));
            Assert.Equal(before, Calls(io).Count);
            Assert.Empty(session.CrashFrames);
            io.FailWrites = true;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, Recorder(waits), model.Map.Lights, colors));
            Assert.Null(model.Begin(io, Recorder(waits)));
        }

        [Fact]
        public void TheEverest60Lights_AreOpenRgbsLayoutWithItsEdges()
        {
            var model = Everest60();
            var lights = model.Map.Lights;
            // Edge 1 to 44 and Numpad Edge 1 to 22 (RGBController_Mountain60Keyboard.cpp:75-150).
            Assert.Equal(66, lights.Count(l => l.Kind == LightKind.Edge));
            Assert.Contains(lights, l => l.Kind == LightKind.Edge && l.Address == 126);
            Assert.Contains(lights, l => l.Kind == LightKind.Edge && l.Address == 191);
            // Keys at their LED values: Escape 0, Delete 56, Num Lock 38,
            // Number Pad Enter 125 (lines 22-34 and 152-168).
            Assert.Equal(0, lights.First(l => l.Usage == 0x29).Address);
            Assert.Equal(56, lights.First(l => l.Usage == 0x4C).Address);
            Assert.Equal(38, lights.First(l => l.Usage == 0x53).Address);
            Assert.Equal(125, lights.First(l => l.Usage == 0x58).Address);
            // One model per product, made once.
            Assert.Same(model, Everest60());
            Assert.NotSame(Everest60(0x0005), Everest60(0x0006));
            Assert.Equal("mountain:3282:0006", Everest60(0x0006).Map.Product);
            // Every 500 ms whatever else goes out (RGBController_Mountain60Keyboard.cpp:434-442).
            Assert.Equal(500, model.KeepAliveMs);
            Assert.Equal(0, model.KeepAliveIdleMs);
            Assert.Equal(0, model.RecheckMs);
        }

        [Fact]
        public void TheEverest60Reports_AreOpenRgbsAndSignalRgbs()
        {
            // The reset (Mountain60KeyboardController.cpp:30-35).
            Assert.Equal("00 03 46 23 EA 00", Hex(MountainKeyboardLighting.Reset60(), 6));
            // The keep-alive (lines 74-79).
            Assert.Equal("00 08 46 23 EA 00", Hex(MountainKeyboardLighting.KeepAlive60(), 6));
            // SelectMode 7, as SignalRGB's Initialize (Mountain Everest 60.js:64).
            Assert.Equal("00 16 46 23 EA 01 00 00 00 07 00", Hex(MountainKeyboardLighting.SelectMode60(0x07), 11));
            // The start at brightness 4 times 25 (lines 157-163), SignalRGB's 0x64.
            Assert.Equal("00 34 46 23 EA 64 C0 00", Hex(MountainKeyboardLighting.Start60(100), 8));
            Assert.Equal("00 36 46 23 EA 00", Hex(MountainKeyboardLighting.Finish60(), 6));
            // A short packet is padded with FF (lines 187-190).
            var packet = MountainKeyboardLighting.Packet60(0x0A, new byte[] { 1, 2, 3, 4 });
            Assert.Equal("00 35 46 23 EA 0A 00 00 00 01 02 03 04 FF", Hex(packet, 14));
            Assert.All(packet.Skip(13), b => Assert.Equal(0xFF, b));
            Assert.Equal(65, packet.Length);
        }

        [Fact]
        public void AnEverest60Picture_IsQuadsFourteenToAReport_EachReadBack()
        {
            var model = Everest60();
            var lights = model.Map.Lights;
            var colors = Distinct(lights.Count);
            var picture = MountainKeyboardLighting.Everest60Picture(lights, colors);
            int bytes = lights.Count * 4;
            int full = bytes / 56;
            bool rest = bytes % 56 != 0;
            Assert.Equal(1 + full + (rest ? 1 : 0) + 1, picture.Count);
            Assert.All(picture.Skip(1).Take(full), r => Assert.Equal(0x0E, r[5]));
            if (rest) Assert.Equal(0x0A, picture[^2][5]);
            // The first quad: the first light's value, red, green, blue.
            Assert.Equal(new[] { (byte)lights[0].Address, (byte)(colors[0] >> 16), (byte)(colors[0] >> 8), (byte)colors[0] },
                picture[1].Skip(9).Take(4));

            var io = new FakeReportChannel { FeatureReportLength = 65, Feature = _ => new byte[65] };
            var session = model.Begin(io, _ => { });
            Assert.NotNull(session);
            Assert.True(session.Feature);
            // Reset and select, each read back (lines 35-37 and 128-130).
            Assert.Equal(new[] { "set", "get", "set", "get" }, Calls(io).Select(c => c.Kind));
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, colors));
            Assert.Equal(4 + picture.Count * 2, Calls(io).Count);
            int painted = Calls(io).Count;
            Assert.True(session.KeepAlive(io, _ => { }, lights, colors));
            // The keep-alive alone, read back: set then get.
            Assert.Equal(painted + 2, Calls(io).Count);
            Assert.Equal("00 08 46 23 EA", Hex(Calls(io)[^2].Bytes, 5));
            int before = Calls(io).Count;
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(before, Calls(io).Count);
            io.FailWrites = true;
            Assert.False(session.KeepAlive(io, _ => { }, lights, colors));
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, _ => { }, lights, colors));
        }
    }
}
