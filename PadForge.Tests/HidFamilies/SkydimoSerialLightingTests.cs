using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>Skydimo's serial strips against OpenRGB's
    /// SkydimoSerialController (#557).</summary>
    public class SkydimoSerialLightingTests
    {
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");
        private const string Ch340 = @"USB\VID_1A86&PID_7523\5&1&0&2";

        private static (SkydimoSerialLighting Family, FakeSerialPort Port, List<string> Opened) Family(string answer,
            List<ComPort> ports = null)
        {
            var port = new FakeSerialPort
            {
                Answer = bytes => Encoding.ASCII.GetString(bytes) == "Moni-A" && answer != null ? Encoding.ASCII.GetBytes(answer) : null,
            };
            var opened = new List<string>();
            ports ??= new List<ComPort> { new("COM5", "USB-SERIAL CH340 (COM5)", Ch340) };
            var family = new SkydimoSerialLighting(() => ports, _ => Box, key => { opened.Add(key); return port; }, (_, _, _) => true);
            return (family, port, opened);
        }

        private static HidLightingSession Begin(SkydimoSerialLighting family, string key = @"\\.\COM5")
            => family.Model.BeginOwn(family.Model.Open(key), _ => { });

        [Fact]
        public void TheAnswer_NamesTheModel_ByOpenRgbsRules()
        {
            byte[] A(string s) => Encoding.ASCII.GetBytes(s);
            // QueryInfo (SkydimoSerialController.cpp:220-264).
            Assert.Equal("SK0L32", SkydimoSerialLighting.ModelOf(A("SK0L32,\x01\x02\r\n")));
            Assert.Equal("SK0402", SkydimoSerialLighting.ModelOf(A("sk0402\n")));
            Assert.Equal("SKA124", SkydimoSerialLighting.ModelOf(A("SKA124")));
            Assert.Null(SkydimoSerialLighting.ModelOf(A("SK04\r\n")));
            Assert.Null(SkydimoSerialLighting.ModelOf(A("SK0,L32\r\n")));
            Assert.Null(SkydimoSerialLighting.ModelOf(A("OK1234\r\n")));
            Assert.Null(SkydimoSerialLighting.ModelOf(A("XK0402")));
        }

        [Fact]
        public void ASession_AsksMoniA_AfterDroppingWhatWaits_AndLightsTheModelsLeds()
        {
            var (family, port, _) = Family("SK0402,\x01\r\n");
            port.Pending.Enqueue(0x99);
            var session = Begin(family);
            Assert.NotNull(session);
            Assert.Equal("Moni-A", Encoding.ASCII.GetString(Assert.Single(port.Written())));
            // The byte that waited is not the answer.
            var map = session.Map;
            Assert.Equal(72, map.Lights.Count);
            Assert.All(map.Lights, l => Assert.Equal(LightKind.Other, l.Kind));
            Assert.All(map.Lights, l => Assert.False(l.HasPosition));
            var colors = Distinct(72);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(null, _ => { }, map.Lights, colors));
            var frame = port.Written()[1];
            // WriteFrame (lines 417-475): 41 64 61 00, the last index 71, RGB.
            Assert.Equal(6 + 72 * 3, frame.Length);
            Assert.Equal("41 64 61 00 00 47 01 02 03 02 04 06", Hex(frame, 12));
            Assert.Equal("48 90 D8", Hex(frame.Skip(frame.Length - 3).ToArray()));
            // The hand-back is the black frame SetBlack sends, and the crash
            // frame from the first picture on.
            var black = Assert.Single(session.CrashFrames);
            Assert.Equal("41 64 61 00 00 47", Hex(black, 6));
            Assert.All(black.Skip(6), b => Assert.Equal(0, b));
            Assert.True(session.HandBack(null, _ => { }));
            Assert.Equal(Hex(black), Hex(port.Written()[2]));
            Assert.Empty(session.CrashFrames);
        }

        [Fact]
        public void APerimeterModel_IsLaidOutAsConfigureOutputLaysIt()
        {
            // SK0L32: 20, 37, 20, 37 around a 39 by 22 matrix (lines 61-64,
            // 307-311 and 325-400): the right side up from the bottom, the
            // top right to left, the left top to bottom, the bottom left to right.
            var (leds, map) = SkydimoSerialLighting.Output("SK0L32");
            Assert.Equal(114, leds);
            Assert.Equal(114, map.Lights.Count);
            Assert.All(map.Lights, l => Assert.True(l.HasPosition));
            (float, float) At(int i) => (map.Lights[i].X, map.Lights[i].Y);
            Assert.Equal((38f, 20f), At(0));
            Assert.Equal((38f, 1f), At(19));
            Assert.Equal((37f, 0f), At(20));
            Assert.Equal((1f, 0f), At(56));
            Assert.Equal((0f, 1f), At(57));
            Assert.Equal((0f, 20f), At(76));
            Assert.Equal((1f, 21f), At(77));
            Assert.Equal((37f, 21f), At(113));

            // SK0121, three edges: the right from the matrix's last row up,
            // no bottom (lines 312-316 and 342-387).
            (leds, map) = SkydimoSerialLighting.Output("SK0121");
            Assert.Equal(51, leds);
            Assert.Equal((26f, 13f), (map.Lights[0].X, map.Lights[0].Y));
            Assert.Equal((0f, 13f), (map.Lights[50].X, map.Lights[50].Y));

            // SK0201, two sides in a 16:9 box (lines 317-321 and 327-376).
            (leds, map) = SkydimoSerialLighting.Output("SK0201");
            Assert.Equal(40, leds);
            Assert.Equal((0f, 20f), (map.Lights[0].X, map.Lights[0].Y));
            Assert.Equal((38f, 1f), (map.Lights[20].X, map.Lights[20].Y));
            Assert.Equal(40, map.Lights.Select(l => (l.X, l.Y)).Distinct().Count());
        }

        [Fact]
        public void EveryTableModel_PlacesEachLedOnce()
        {
            foreach (var entry in SkydimoSerialLighting.Entries.Where(e => e.Layout != SkydimoSerialLighting.Layout.None))
            {
                var (leds, map) = SkydimoSerialLighting.Output(entry.Model);
                Assert.Equal(entry.Total, leds);
                Assert.Equal(entry.Total, map.Lights.Count);
                Assert.Equal(entry.Zone1 + entry.Zone2 + entry.Zone3 + entry.Zone4, entry.Total);
                if (entry.Layout == SkydimoSerialLighting.Layout.Strip1) continue;
                Assert.All(map.Lights, l => Assert.True(l.HasPosition, entry.Model));
                Assert.Equal(entry.Total, map.Lights.Select(l => (l.X, l.Y)).Distinct().Count());
            }
            Assert.Equal(42, SkydimoSerialLighting.Entries.Length);
        }

        [Fact]
        public void AModelOfUnknownLength_LightsOneZoneOverItsMost()
        {
            var (family, port, _) = Family("SK9999\r\n");
            var session = Begin(family);
            var light = Assert.Single(session.Map.Lights);
            Assert.Equal(LightKind.Zone, light.Kind);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(null, _ => { }, session.Map.Lights, new[] { 0x0A0B0C }));
            var frame = port.Written()[1];
            // 150 LEDs, the last index 149 (SKYDIMO_SERIAL_DEFAULT_MAX_LED_COUNT).
            Assert.Equal(6 + 150 * 3, frame.Length);
            Assert.Equal("41 64 61 00 00 95 0A 0B 0C", Hex(frame, 9));
            Assert.Equal("0A 0B 0C", Hex(frame.Skip(frame.Length - 3).ToArray()));
            // SK0410's most is 300 (line 49).
            var (leds, map) = SkydimoSerialLighting.Output("SK0410");
            Assert.Equal(300, leds);
            Assert.Single(map.Lights);
            Assert.Equal("41 64 61 00 01 2B", Hex(SkydimoSerialLighting.Frame(new int[300], 300), 6));
        }

        [Fact]
        public void APortThatIsNoSkydimo_GetsNoSession_AndLeavesTheLook_UntilItIsGone()
        {
            var ports = new List<ComPort> { new("COM5", "USB-SERIAL CH340 (COM5)", Ch340) };
            var (family, port, _) = Family("OK\r\n", ports);
            Assert.Single(family.Look());
            Assert.Null(Begin(family));
            Assert.True(family.Refused(@"\\.\COM5"));
            // Nothing but the question went out.
            Assert.Single(port.Written());
            Assert.Empty(family.Look());
            // A device that answers nothing is refused the same way.
            ports.Clear();
            Assert.Empty(family.Look());
            Assert.False(family.Refused(@"\\.\COM5"));
            ports.Add(new ComPort("COM5", "USB-SERIAL CH340 (COM5)", Ch340));
            Assert.Single(family.Look());
            var (silent, _, _) = Family(null);
            Assert.Null(Begin(silent));
            Assert.True(silent.Refused(@"\\.\COM5"));
        }

        [Fact]
        public void APortThatFails_GetsNoSession_ButStaysInTheLook()
        {
            var (reads, readPort, _) = Family("SK0402\r\n");
            readPort.FailReads = true;
            Assert.Null(Begin(reads));
            Assert.False(reads.Refused(@"\\.\COM5"));
            Assert.Empty(readPort.Written());
            Assert.Single(reads.Look());
            var (writes, writePort, _) = Family("SK0402\r\n");
            writePort.FailWrites = true;
            Assert.Null(Begin(writes));
            Assert.False(writes.Refused(@"\\.\COM5"));
            Assert.Single(writes.Look());
        }

        [Fact]
        public void TheAnswer_IsReadAtMostTenTimes_AndStopsAtALineEnd()
        {
            var (family, port, _) = Family("SK0402\r\n");
            port.Chunk = 64;
            Assert.NotNull(Begin(family));
            // One drain read, then the answer whole in one read, which ends
            // at its line end.
            Assert.Equal(2, port.Reads);
            // An answer with no line end is read ten times over, and its
            // first six bytes still name the model.
            var (unended, unendedPort, _) = Family("SK0402,AB");
            unendedPort.Chunk = 64;
            Assert.NotNull(Begin(unended));
            Assert.Equal(1 + 10, unendedPort.Reads);
            var (slow, slowPort, _) = Family(null);
            slowPort.Answer = _ => null;
            Assert.Null(Begin(slow));
            Assert.Equal(1 + 10, slowPort.Reads);
        }

        [Fact]
        public void TheModel_KeepsOpenRgbsKeepAlive_AndNamesSkydimosApp()
        {
            var model = SkydimoSerialLighting.Instance.Model;
            // SKYDIMO_SERIAL_KEEPALIVE_PERIOD 1500 ms, sent once 1400 ms passed
            // since a frame (RGBController_SkydimoSerial.cpp:16-17 and 163-187).
            Assert.Equal(1500, model.KeepAliveMs);
            Assert.Equal(1400, model.KeepAliveIdleMs);
            Assert.Equal(new[] { VendorPrograms.Skydimo }, model.Programs);
            Assert.Null(model.Begin);
            Assert.NotNull(model.WriteOnce);
            // The keep-alive sends the picture again.
            var (family, port, _) = Family("SK0402\r\n");
            var session = Begin(family);
            var colors = Distinct(72);
            session.Paint(null, _ => { }, session.Map.Lights, colors);
            Assert.True(session.KeepAlive(null, _ => { }, session.Map.Lights, colors));
            Assert.Equal(Hex(port.Written()[1]), Hex(port.Written()[2]));
        }

        [Fact]
        public void AFailedWrite_FailsThePicture_AndAFailedHandBackKeepsTheCrashFrame()
        {
            var (family, port, _) = Family("SK0402\r\n");
            var session = Begin(family);
            port.FailWrites = true;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(null, _ => { }, session.Map.Lights, Distinct(72)));
            Assert.Single(session.CrashFrames);
            Assert.False(session.HandBack(null, _ => { }));
            Assert.Single(session.CrashFrames);
        }
    }
}
