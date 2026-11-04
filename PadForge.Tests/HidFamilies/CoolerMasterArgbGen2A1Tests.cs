using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A scripted ARGB Gen 2 A1: each command written is answered,
    /// once, by <see cref="Answer"/>, as hidapi hands an answer over
    /// (report ID 0 dropped), and the writes and waits are kept in
    /// order.</summary>
    internal sealed class FakeA1Device : IHidReportChannel
    {
        public readonly List<byte[]> Outs = new();
        public readonly List<string> Log = new();
        private readonly Queue<byte[]> _answers = new();

        /// <summary>The answer's bytes from the page on, for a report
        /// written, or null for none.</summary>
        public Func<byte[], byte[]> Answer = _ => null;

        /// <summary>Answers queued after <see cref="Answer"/>'s, as a
        /// device's later answers come.</summary>
        public Func<byte[], IEnumerable<byte[]>> More = _ => Array.Empty<byte[]>();

        /// <summary>An answer already waiting before the next write.</summary>
        public void Waiting(params byte[] answer)
        {
            var full = new byte[65];
            answer.CopyTo(full, 1);
            _answers.Enqueue(full);
        }

        public string Path => @"\\?\hid#vid_2516&pid_0173&mi_01#fake";
        public bool FeatureOnly => false;
        public int InputReportLength => 65;
        public int OutputReportLength => 65;
        public int FeatureReportLength => 0;
        public bool FailWrites;

        public bool SetFeature(byte[] report) => false;
        public bool GetFeature(byte reportId, byte[] buffer) => false;

        public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs)
        {
            if (FailWrites) return false;
            Outs.Add((byte[])report.Clone());
            Log.Add("out " + BitConverter.ToString(report, 1, 4).Replace('-', ' '));
            if (Answer(report) is { } answer) Waiting(answer);
            foreach (var later in More(report)) Waiting(later);
            return true;
        }

        public int ReadInput(byte[] buffer, int timeoutMs)
        {
            if (_answers.Count == 0) return 0;
            var answer = _answers.Dequeue();
            Array.Copy(answer, buffer, Math.Min(answer.Length, buffer.Length));
            return Math.Min(answer.Length, buffer.Length);
        }

        public void Wait(int ms) => Log.Add("wait " + ms);

        public void Dispose() { }
    }

    /// <summary>The Cooler Master ARGB Gen 2 A1 controllers against OpenRGB's
    /// CMARGBGen2A1Controller at the pinned commit (#557). Every expected
    /// byte is written out from the line the test names.</summary>
    public class CoolerMasterArgbGen2A1Tests
    {
        private static string Hex(IEnumerable<byte> bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));

        private static HidLightingModel Model(ushort pid)
        {
            var r = OpenRgbRegistrations.Of(CoolerMasterLighting.OpenRgbFamily).Single(x => x.ProductId == pid);
            return CoolerMasterLighting.Instance.Match(new HidCollectionInfo
            {
                Path = $@"\\?\hid#vid_2516&pid_{pid:x4}&mi_01#7&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
                VendorId = 0x2516, ProductId = pid, InterfaceNumber = 1, UsagePage = 0xFF01, Usage = 1,
                OutputReportLength = 65, InputReportLength = 65,
            });
        }

        /// <summary>Port 1 Gen2 with two devices, the second reporting 70
        /// LEDs; port 2 Gen1 with geometry factors 2 and 30; port 3 silent.
        /// Realtime on and off are answered.</summary>
        private static byte[] Topology(byte[] report)
        {
            byte cmd = report[2], op = report[3], arg = report[4];
            return (cmd, op, arg) switch
            {
                (0x0B, 0x01, 0x01) => new byte[] { 0x80, 0x0B, 0x03, 0x01, 0x00 },
                (0x0B, 0x01, 0x02) => new byte[] { 0x80, 0x0B, 0x03, 0x02, 0x01 },
                (0x07, 0x01, 0x01) => new byte[] { 0x80, 0x07, 0x03, 0x01, 0x03, 0x01, 24, 0x00, 0x00, 0x04, 70 },
                (0x06, 0x01, 0x02) => new byte[] { 0x80, 0x06, 0x03, 0x02, 0x02, 30 },
                (0x01, 0x02, _) => new byte[] { 0x80, 0x01, 0x03, arg },
                _ => null,
            };
        }

        [Fact]
        public void TheRegistrations_AreTheA1s_WhichStreamEvery40Ms()
        {
            foreach (ushort pid in new ushort[] { 0x0173, 0x01C9, 0x01CB })
            {
                var model = Model(pid);
                Assert.NotNull(model);
                // RealtimeWorker's 40 ms period (CMARGBGen2A1Controller.cpp:655-696,
                // CMARGBGen2A1Controller.h:37), nothing stored.
                Assert.Equal(40, model.RepaintMs);
                Assert.Equal(40, model.KeepAliveMs);
                Assert.Equal(40, model.KeepAliveIdleMs);
                Assert.Equal(0, model.HoldMs);
                Assert.Null(model.Identify);
                // Three ports, the Mini one (lines 36-39).
                Assert.Equal(pid == 0x01CB ? 1 : 3, model.Map.Lights.Count);
            }
        }

        /// <summary>RefreshTopology (CMARGBGen2A1Controller.cpp:133-276) and
        /// SetupZones (RGBController_CMARGBGen2A1Controller.cpp:1320-1446).</summary>
        [Fact]
        public void TheSession_ReadsEachPortsGenerationAndStrips_AndLightsThem()
        {
            var device = new FakeA1Device { Answer = Topology };
            var session = Assert.IsType<CoolerMasterLighting.ArgbGen2A1Session>(Model(0x0173).Begin(device, device.Wait));
            Assert.Equal(new[]
            {
                "out 80 0B 01 01", "out 80 07 01 01", "out 80 0B 01 02", "out 80 06 01 02", "out 80 0B 01 04",
            }, device.Log);
            // Gen2 devices 01 x 24 and 04 x 60 (70 held to 60, line 1353), the
            // empty 00 x 00 left out (line 269). Gen1 port 2 at factor 30.
            Assert.Equal(new[]
            {
                new CoolerMasterLighting.A1Target(0x01, 0x01, 24), new CoolerMasterLighting.A1Target(0x01, 0x04, 60),
                new CoolerMasterLighting.A1Target(0x02, 0x00, 30),
            }, session.Targets);
            Assert.Equal(114, session.Map.Lights.Count);
            Assert.Equal(new[] { 1, 2, 3 }, session.Map.Lights.Select(l => l.Zone).Distinct());
            Assert.Equal(Enumerable.Range(0, 24).Select(i => i)
                .Concat(Enumerable.Range(0, 60).Select(i => 1 << 8 | i))
                .Concat(Enumerable.Range(0, 30).Select(i => 2 << 8 | i)), session.Map.Lights.Select(l => l.Address));
            Assert.Empty(session.CrashFrames);
            // Only the Gen1 port's strip is its user's to size: Gen2 reports
            // its own devices (lines 1353-1355 and 1400-1404).
            Assert.Equal(new[] { new LightStrip(2, 60, 0) }, session.Map.Strips);
        }

        /// <summary>A count on a Gen1 port is its strip's length, held to 6 to
        /// 60, as OpenRGB's sized Gen1 zone (RGBController_CMARGBGen2A1Controller.cpp:1400-1404).
        /// No write goes out, since every frame carries 60 LEDs. A count on a
        /// Gen2 port changes nothing, and the first strips' addresses stay.</summary>
        [Fact]
        public void ACount_SizesAGen1Port_AndLeavesGen2Alone()
        {
            var device = new FakeA1Device { Answer = Topology };
            var session = Assert.IsType<CoolerMasterLighting.ArgbGen2A1Session>(Model(0x0173).Begin(device, device.Wait));
            var before = session.Map.Lights.Where(l => l.Address >> 8 < 2).Select(l => l.Address).ToArray();
            device.Outs.Clear();
            device.Log.Clear();
            Assert.True(session.SizeStrips(device, device.Wait, new[] { 10, 40 }));
            Assert.Empty(device.Log);
            Assert.Equal(new[] { 24, 60, 40 }, session.Targets.Select(t => t.Leds));
            Assert.Equal(124, session.Map.Lights.Count);
            Assert.Equal(new[] { new LightStrip(2, 60, 40) }, session.Map.Strips);
            Assert.Equal(before, session.Map.Lights.Where(l => l.Address >> 8 < 2).Select(l => l.Address));

            Assert.True(session.SizeStrips(device, device.Wait, new[] { 0, 3 }));
            Assert.Equal(6, session.Targets[2].Leds);
            Assert.True(session.SizeStrips(device, device.Wait, new[] { 0, 200 }));
            Assert.Equal(60, session.Targets[2].Leds);
            // No count: the length the port reported comes back.
            Assert.True(session.SizeStrips(device, device.Wait, StripCounts.None));
            Assert.Equal(30, session.Targets[2].Leds);
        }

        /// <summary>A picture reaches each LED by its address, whichever strip
        /// it is on, and a strip's frame carries its own LEDs' colors.</summary>
        [Fact]
        public void APicture_GoesToEachStripByItsLightsAddresses()
        {
            var device = new FakeA1Device { Answer = Topology };
            var session = Assert.IsType<CoolerMasterLighting.ArgbGen2A1Session>(Model(0x0173).Begin(device, device.Wait));
            Assert.True(session.SizeStrips(device, device.Wait, new[] { 0, 6 }));
            var lights = session.Map.Lights;
            var colors = lights.Select(l => (l.Address >> 8) + 1 << 16 | (l.Address & 0xFF)).ToArray();
            device.Answer = report => report[2] == 0x01 ? new byte[] { 0x80, 0x01, 0x03, 0x01 } : Topology(report);
            device.Outs.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(device, device.Wait, lights, colors));
            var frames = device.Outs.Where(o => o[2] == 0x09 && o[3] == 0x02 && o[4] == 0x00).ToList();
            var first = Assert.Single(frames);
            // The Gen1 port's six LEDs: strip 3, LEDs 0 to 5, then black.
            Assert.Equal(new byte[] { 3, 0, 0, 3, 0, 1 }, first.Skip(6).Take(6).ToArray());
            Assert.Equal(new byte[] { 3, 0, 5, 0, 0, 0 }, first.Skip(21).Take(6).ToArray());
        }

        [Fact]
        public void AGen1StripsCount_IsTheSecondFactorHeldToSixToSixty()
        {
            // DecodeGen1NominalLEDCount (RGBController_CMARGBGen2A1Controller.cpp:222-226)
            // and SetupZones's floor of 6 (lines 1386-1404).
            Assert.Equal(6, CoolerMasterLighting.A1Gen1Leds(0));
            Assert.Equal(6, CoolerMasterLighting.A1Gen1Leds(3));
            Assert.Equal(30, CoolerMasterLighting.A1Gen1Leds(30));
            Assert.Equal(60, CoolerMasterLighting.A1Gen1Leds(200));
        }

        /// <summary>SendCommandPage skips an answer for another target or
        /// another command and fails one that is not a response
        /// (CMARGBGen2A1Controller.cpp:1386-1444).</summary>
        [Fact]
        public void AnAnswer_MustBeThePagesCommandForThisTarget_AndAResponse()
        {
            var query = CoolerMasterLighting.A1Command(0x0B, 0x01, 0x02);
            Assert.Equal("00 80 0B 01 02", Hex(query.Take(5)));
            Assert.Equal(65, query.Length);

            // No answer: nothing.
            Assert.Null(CoolerMasterLighting.A1Exchange(new FakeA1Device(), query, 0x02));

            // An answer left from before is drained first, and one for port
            // 1 or for another command is stepped over before port 2's.
            var device = new FakeA1Device
            {
                Answer = _ => new byte[] { 0x80, 0x0B, 0x03, 0x01, 0x00 },
                More = _ => new[] { new byte[] { 0x80, 0x07, 0x03, 0x02, 0x00 }, new byte[] { 0x80, 0x0B, 0x03, 0x02, 0x01 } },
            };
            device.Waiting(0x80, 0x0B, 0x03, 0x02, 0x00);
            var answer = CoolerMasterLighting.A1Exchange(device, query, 0x02);
            Assert.Equal("80 0B 03 02 01", Hex(answer.Take(5)));

            // Not a response: 80 0B 02.
            var refused = new FakeA1Device { Answer = _ => new byte[] { 0x80, 0x0B, 0x02, 0x02, 0x01 } };
            Assert.Null(CoolerMasterLighting.A1Exchange(refused, query, 0x02));
        }

        [Fact]
        public void TheFirstPicture_EntersRealtimeAsMasterPlusDoes_ThenSendsEachStripsFrame()
        {
            var device = new FakeA1Device { Answer = Topology };
            var model = Model(0x0173);
            var session = model.Begin(device, device.Wait);
            device.Log.Clear();
            device.Outs.Clear();
            var lights = session.Map.Lights;
            var colors = Enumerable.Range(0, lights.Count).Select(i => 0x010203 * (i + 1) & 0xFFFFFF).ToArray();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(device, device.Wait, lights, colors));
            // 01 on, two black frames of three reports, 01 on, five black
            // frames (SendRealtimePreamble, CMARGBGen2A1Controller.cpp:482-530),
            // then three strips' frames.
            var outs = device.Outs;
            Assert.Equal(1 + 6 + 1 + 15 + 9, outs.Count);
            Assert.Equal("00 80 01 02 01", Hex(outs[0].Take(5)));
            Assert.Equal("00 00 09 FF FF 3C 00", Hex(outs[1].Take(7)));
            Assert.Equal("00 01 09 00", Hex(outs[2].Take(4)));
            Assert.Equal("00 82 09 00", Hex(outs[3].Take(4)));
            Assert.Equal("00 80 01 02 01", Hex(outs[7].Take(5)));
            Assert.All(outs.Skip(8).Take(15), r => Assert.All(r.Skip(3), b => Assert.True(b == 0 || b == 0x3C || b == 0xFF)));
            // The first strip: 00 09 01 01 3C and LED 0's color at 6
            // (SendRealtimeFrameLocked, lines 730-797), LED 20's at the second
            // report's 3, LED 23's in the second report.
            var strip = outs.Skip(23).ToList();
            Assert.Equal("00 00 09 01 01 3C 01 02 03 02 04 06", Hex(strip[0].Take(12)));
            Assert.Equal(colors[19] >> 8 & 0xFF, strip[0][6 + 19 * 3 + 1]);
            Assert.Equal("00 01 09", Hex(strip[1].Take(3)));
            Assert.Equal(colors[19] & 0xFF, strip[1][3]);
            Assert.Equal(colors[20] >> 16 & 0xFF, strip[1][4]);
            Assert.All(strip[1].Skip(3 + 4 * 3 + 1), b => Assert.Equal(0, b));
            Assert.Equal("00 82 09", Hex(strip[2].Take(3)));
            Assert.All(strip[2].Skip(3), b => Assert.Equal(0, b));
            // The second strip, subport 04, its first LED light 24's color.
            Assert.Equal("00 00 09 01 04 3C", Hex(strip[3].Take(6)));
            Assert.Equal(colors[24] >> 16 & 0xFF, strip[3][6]);
            // Its third report: data byte 121, LED 40's green, at 3.
            Assert.Equal(colors[24 + 40] >> 8 & 0xFF, strip[5][3]);
            Assert.Equal(colors[24 + 59] & 0xFF, strip[5][3 + 58]);
            // The Gen1 strip, subport 00.
            Assert.Equal("00 00 09 02 00 3C", Hex(strip[6].Take(6)));
            Assert.Equal(colors[84] >> 16 & 0xFF, strip[6][6]);
            // 2 ms after every report, and the wait to 104 ms after the
            // second 01 on.
            Assert.Equal(outs.Count - 2, device.Log.Count(l => l == "wait 2"));
            Assert.Contains(device.Log, l => l.StartsWith("wait ") && l != "wait 2" && int.Parse(l.Substring(5)) > 60);
            Assert.Equal("00 80 01 02 00", Hex(Assert.Single(session.CrashFrames).Take(5)));

            // The next picture sends the strips alone.
            device.Outs.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(device, device.Wait, lights, colors));
            Assert.Equal(9, device.Outs.Count);
            // So does the keep-alive.
            device.Outs.Clear();
            Assert.True(session.KeepAlive(device, device.Wait, lights, colors));
            Assert.Equal(9, device.Outs.Count);
        }

        /// <summary>ExitRealtimeMode (CMARGBGen2A1Controller.cpp:532-572): 10 ms,
        /// 01 off and its answer, a second try 10 ms later.</summary>
        [Fact]
        public void TheHandBack_TurnsRealtimeOff_TryingTwice()
        {
            var device = new FakeA1Device { Answer = Topology };
            var session = Model(0x0173).Begin(device, device.Wait);
            var lights = session.Map.Lights;
            session.Paint(device, device.Wait, lights, new int[lights.Count]);
            device.Log.Clear();
            Assert.True(session.HandBack(device, device.Wait));
            Assert.Equal(new[] { "wait 10", "out 80 01 02 00" }, device.Log);
            Assert.Empty(session.CrashFrames);
            // Nothing more to hand back.
            device.Log.Clear();
            Assert.True(session.HandBack(device, device.Wait));
            Assert.Empty(device.Log);

            var silent = new FakeA1Device { Answer = Topology };
            var second = Model(0x0173).Begin(silent, silent.Wait);
            second.Paint(silent, silent.Wait, lights, new int[lights.Count]);
            silent.Answer = _ => null;
            silent.Log.Clear();
            Assert.False(second.HandBack(silent, silent.Wait));
            Assert.Equal(new[] { "wait 10", "out 80 01 02 00", "wait 10", "out 80 01 02 00" }, silent.Log);
            Assert.Single(second.CrashFrames);
            // A write that fails fails the picture.
            silent.FailWrites = true;
            Assert.Equal(HidLightingPaint.Failed, second.Paint(silent, silent.Wait, lights, new int[lights.Count]));
        }

        [Fact]
        public void AnA1Mini_AsksOnePort_AndAControllerWithNothingPluggedIn_GetsNoSession()
        {
            var device = new FakeA1Device { Answer = Topology };
            var session = Model(0x01CB).Begin(device, device.Wait);
            Assert.Equal(new[] { "out 80 0B 01 01", "out 80 07 01 01" }, device.Log);
            Assert.Equal(2, ((CoolerMasterLighting.ArgbGen2A1Session)session).Targets.Count);
            var empty = new FakeA1Device();
            Assert.Null(Model(0x0173).Begin(empty, empty.Wait));
            Assert.Equal(3, empty.Outs.Count);
        }
    }
}
