using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Cooler Master's ARGB Gen 2 A1, A1 V2 and A1 Mini controllers (#557),
    /// 2516:0173, 01C9 and 01CB on interface 1, usage page 0xFF01
    /// (CoolerMasterControllerDetect.cpp:52-54 and 412-414), lit as OpenRGB's
    /// CMARGBGen2A1Controller lights them in its Direct mode.
    ///
    /// <para>The protocol. Every frame is a 65-byte output report, report ID
    /// 0, then the command page 80, the command and the operation (01 read,
    /// 02 write) and the payload. A command that answers is answered with an
    /// input report 80, the command, 03 and the request's own first payload
    /// bytes, read within 150 ms after the queued answers are drained, and
    /// another answer is skipped (SendCommandPage and DrainPendingResponses,
    /// CMARGBGen2A1Controller.cpp:1339-1468, and CMARGBGen2A1Controller.h:26-58
    /// and 81-110).</para>
    ///
    /// <para>The ports. A session reads what is plugged in, as OpenRGB's
    /// constructor does without writing anything (lines 26-46 and 133-276):
    /// for each port, three or the A1 Mini's one, the generation, 0B with the
    /// port's mask. A Gen2 port's devices come from 07: up to five, each its
    /// subport mask and LED count, at most 60. A Gen1 port is one strip,
    /// whose LED count OpenRGB takes from the second geometry factor of 06,
    /// held to 6 to 60, and 6 where that read gave none
    /// (RGBController_CMARGBGen2A1Controller.cpp:186-226 and 1320-1446,
    /// the count at 1386-1404).
    /// Each strip's LEDs are the session's lights, in that order
    /// (<see cref="HidLightingSession.Map"/>).</para>
    ///
    /// <para>The picture. OpenRGB's Direct mode streams. Its first picture
    /// enters realtime mode as MasterPlus does: 01 on and its answer, two
    /// black frames for every target, 01 on again, five black frames, and
    /// the first target frame 104 ms after the second 01 on
    /// (EnterRealtimeMode and SendRealtimePreamble, lines 455-530). Each
    /// target's frame is three reports, 00 09 with the port, the subport and
    /// 3C and 59 color bytes, then 01 09 and 62, then 82 09 and 59, the
    /// colors of 60 LEDs, each report followed by 2 ms (SendRealtimeFrameLocked,
    /// lines 730-797). OpenRGB's realtime thread sends every target again
    /// every 40 ms (RealtimeWorker, lines 655-696), so the model repaints at
    /// most every 40 ms and keeps the picture alive on that period
    /// (<see cref="HidLightingModel.KeepAliveMs"/>). Nothing of it is stored
    /// (Direct is "a host-owned realtime mode", RGBController_CMARGBGen2A1Controller.cpp:450-453).</para>
    ///
    /// <para>The hand-back. Realtime mode off, 01 off after 10 ms of quiet,
    /// tried twice 10 ms apart (ExitRealtimeMode, lines 532-572), which
    /// OpenRGB sends when its controller closes (lines 48-60). The controller
    /// then runs its own stored effect again. The crash path sends the same
    /// report.</para>
    ///
    /// <para>Not ported: OpenRGB's hardware effects, Custom colors, overlay
    /// tables, multilayer modes, flash save, UID and Gen1 geometry writes
    /// (lines 799-1337). Each writes the controller's stored configuration,
    /// which PadForge's pictures never change.</para>
    /// </summary>
    internal sealed partial class CoolerMasterLighting
    {
        /// <summary>COOLERMASTER_ARGB_GEN2_A1_MINI_PID, whose controller has one
        /// port (CMARGBGen2A1Controller.cpp:36-39).</summary>
        internal const ushort A1Mini = 0x01CB;

        /// <summary>The command page, operations and commands
        /// (CMARGBGen2A1Controller.h:81-110).</summary>
        internal const byte A1Page = 0x80, A1Read = 0x01, A1Write = 0x02, A1Response = 0x03;
        internal const byte A1Realtime = 0x01, A1Gen1Geometry = 0x06, A1Gen2Topology = 0x07, A1RealtimeRgb = 0x09,
            A1Generation = 0x0B;

        /// <summary>The generations (lines 112-117).</summary>
        internal const byte A1Gen2 = 0x00, A1Gen1 = 0x01;

        /// <summary>The bounds and waits of CMARGBGen2A1Controller.h:26-55.</summary>
        internal const int A1Ports = 3, A1Subports = 5, A1MaxLeds = 60, A1Gen1MinLeds = 6;
        internal const int A1ResponseMs = 150, A1PeriodMs = 40, A1PacketGapMs = 2, A1PrerollFirst = 2,
            A1PrerollSecond = 5, A1SecondOnToTargetMs = 104, A1ExitQuietMs = 10, A1ExitAttempts = 2;

        /// <summary>CM_ARGB_GEN2_A1_CHANNEL_ALL and _SUBCHANNEL_ALL, the black
        /// frames' target (lines 133-138).</summary>
        internal const byte A1All = 0xFF;

        /// <summary>How many queued answers a drain reads at most. OpenRGB
        /// reads until none comes in 1 ms (lines 1460-1468). The bound is
        /// PadForge's.</summary>
        internal const int A1DrainLimit = 64;

        /// <summary>One strip a frame goes to: the port's mask, the subport's
        /// (00 on a Gen1 port), and its LED count.</summary>
        internal readonly record struct A1Target(byte Port, byte Subport, int Leds);

        private static HidLightingModel ArgbGen2A1Model(string name, ushort productId)
        {
            int ports = productId == A1Mini ? 1 : A1Ports;
            var lights = new LightInfo[ports];
            for (int i = 0; i < ports; i++) lights[i] = ZoneLight(i, i + 1);
            return new HidLightingModel
            {
                Family = Instance,
                Name = name,
                Map = new LightMap(lights, "OpenRGB RGBController_CMARGBGen2A1Controller.cpp, ports not asked yet")
                {
                    Product = Product(productId),
                },
                Programs = new[] { VendorPrograms.MasterPlus },
                RepaintMs = A1PeriodMs,
                KeepAliveMs = A1PeriodMs,
                KeepAliveIdleMs = A1PeriodMs,
                Begin = (io, wait) => BeginArgbGen2A1(io, wait, productId),
            };
        }

        /// <summary>A command report: 80, the command, the operation and the
        /// payload (lines 1349-1353).</summary>
        internal static byte[] A1Command(byte command, byte operation, params byte[] payload)
        {
            var frame = new byte[PacketLength];
            frame[1] = A1Page;
            frame[2] = command;
            frame[3] = operation;
            payload.CopyTo(frame, 4);
            return frame;
        }

        /// <summary>Reads away the answers waiting, as DrainPendingResponses
        /// does before a command that answers. False when a read failed.</summary>
        internal static bool A1Drain(IHidReportChannel io)
        {
            for (int i = 0; i < A1DrainLimit; i++)
            {
                var answer = Read(io, 1);
                if (answer == null) return false;
                if (answer.Length == 0) return true;
            }
            return true;
        }

        /// <summary>SendCommandPage with an answer (lines 1339-1444): the
        /// queued answers drained, the command written, then input read for
        /// up to 150 ms until one is the page and the command, with
        /// <paramref name="prefix"/> from its byte 3. A report ID 0 hidapi
        /// left in front is stepped over. Null when nothing matching came or
        /// it was not a response (byte 2 not 03).</summary>
        internal static byte[] A1Exchange(IHidReportChannel io, byte[] frame, params byte[] prefix)
        {
            if (io == null || !A1Drain(io) || !io.WriteOutput(frame)) return null;
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < A1ResponseMs)
            {
                var raw = Read(io, (int)Math.Max(1, A1ResponseMs - clock.ElapsedMilliseconds));
                if (raw == null || raw.Length == 0) return null;
                int offset = 0;
                if (raw[0] == 0x00 && raw.Length > 1 && raw[1] == A1Page) offset = 1;
                else if (raw[0] != A1Page) continue;
                if (raw.Length - offset < 3) continue;
                var candidate = new byte[64];
                Array.Copy(raw, offset, candidate, 0, Math.Min(raw.Length - offset, candidate.Length));
                if (candidate[0] != A1Page || candidate[1] != frame[2]) continue;
                bool matches = true;
                for (int i = 0; i < prefix.Length; i++)
                    if (candidate[3 + i] != prefix[i])
                    {
                        matches = false;
                        break;
                    }
                if (!matches) continue;
                return candidate[2] == A1Response ? candidate : null;
            }
            return null;
        }

        /// <summary>The second Gen1 geometry factor as a strip's LED count,
        /// DecodeGen1NominalLEDCount held to 6 to 60, and 6 where the read
        /// gave none (RGBController_CMARGBGen2A1Controller.cpp:222-226 and
        /// 1386-1404).</summary>
        internal static int A1Gen1Leds(int factorB)
            => factorB == 0 ? A1Gen1MinLeds : Math.Clamp(factorB, A1Gen1MinLeds, A1MaxLeds);

        /// <summary>RefreshTopology's reads (CMARGBGen2A1Controller.cpp:133-276)
        /// and the strips SetupZones makes of them
        /// (RGBController_CMARGBGen2A1Controller.cpp:1320-1446): a port whose
        /// generation does not answer has none, a Gen2 port one per device
        /// it reports with a mask and LEDs, a Gen1 port one.</summary>
        internal static List<A1Target> A1Topology(IHidReportChannel io, int ports)
        {
            var targets = new List<A1Target>();
            for (int port = 0; port < ports; port++)
            {
                byte mask = (byte)(1 << port);
                var generation = A1Exchange(io, A1Command(A1Generation, A1Read, mask), mask);
                if (generation == null || generation[3] != mask) continue;
                if (generation[4] == A1Gen1)
                {
                    var geometry = A1Exchange(io, A1Command(A1Gen1Geometry, A1Read, mask), mask);
                    int factorB = geometry != null && geometry[3] == mask ? geometry[5] : 0;
                    targets.Add(new A1Target(mask, 0x00, A1Gen1Leds(factorB)));
                }
                else if (generation[4] == A1Gen2)
                {
                    var topology = A1Exchange(io, A1Command(A1Gen2Topology, A1Read, mask), mask);
                    if (topology == null || topology[3] != mask) continue;
                    int count = Math.Min((int)topology[4], A1Subports);
                    for (int i = 0; i < count; i++)
                    {
                        int at = 5 + i * 2;
                        if (at + 1 >= topology.Length) break;
                        byte subport = topology[at];
                        byte leds = topology[at + 1];
                        if (subport != 0 && leds != 0) targets.Add(new A1Target(mask, subport, Math.Min((int)leds, A1MaxLeds)));
                    }
                }
            }
            return targets;
        }

        /// <summary>A target's realtime frame, three reports
        /// (SendRealtimeFrameLocked, lines 730-797), from 60 LEDs' colors.</summary>
        internal static byte[][] A1RealtimeFrame(byte port, byte subport, ReadOnlySpan<int> colors)
        {
            var data = new byte[A1MaxLeds * 3];
            for (int i = 0; i < Math.Min(colors.Length, A1MaxLeds); i++)
            {
                data[i * 3] = R(colors[i]);
                data[i * 3 + 1] = G(colors[i]);
                data[i * 3 + 2] = B(colors[i]);
            }
            var first = new byte[PacketLength];
            first[2] = A1RealtimeRgb;
            first[3] = port;
            first[4] = subport;
            first[5] = A1MaxLeds;
            Array.Copy(data, 0, first, 6, 59);
            var second = new byte[PacketLength];
            second[1] = 0x01;
            second[2] = A1RealtimeRgb;
            Array.Copy(data, 59, second, 3, 62);
            var third = new byte[PacketLength];
            third[1] = 0x82;
            third[2] = A1RealtimeRgb;
            Array.Copy(data, 121, third, 3, 59);
            return new[] { first, second, third };
        }

        /// <summary>The session's lights: each strip's LEDs in order, each at
        /// its place among all of them, with the strip's number.</summary>
        internal static LightMap A1Map(ushort productId, IReadOnlyList<A1Target> targets)
        {
            var lights = new List<LightInfo>();
            var key = new StringBuilder();
            for (int t = 0; t < targets.Count; t++)
            {
                for (int i = 0; i < targets[t].Leds; i++)
                    lights.Add(new LightInfo(lights.Count, LightKind.Edge, 0, LightKeySource.None) { Zone = t + 1 });
                key.Append($":{targets[t].Port:X2}{targets[t].Subport:X2}x{targets[t].Leds}");
            }
            return new LightMap(lights, "OpenRGB RGBController_CMARGBGen2A1Controller.cpp") { Product = Product(productId) + key };
        }

        private static HidLightingSession BeginArgbGen2A1(IHidReportChannel io, Action<int> wait, ushort productId)
        {
            if (io == null) return null;
            var targets = A1Topology(io, productId == A1Mini ? 1 : A1Ports);
            return targets.Count == 0 ? null : new ArgbGen2A1Session(targets, A1Map(productId, targets));
        }

        internal sealed class ArgbGen2A1Session : HidLightingSession
        {
            private readonly LightMap _map;
            private bool _realtime;
            private byte[][] _crash = Array.Empty<byte[]>();

            public ArgbGen2A1Session(IReadOnlyList<A1Target> targets, LightMap map)
            {
                Targets = targets;
                _map = map;
            }

            public IReadOnlyList<A1Target> Targets { get; }

            public override LightMap Map => _map;

            public override bool Feature => false;

            /// <summary>Realtime mode off, once it may be on.</summary>
            public override byte[][] CrashFrames => _crash;

            private static bool Send(IHidReportChannel io, Action<int> wait, byte[][] reports)
            {
                foreach (var report in reports)
                {
                    if (!io.WriteOutput(report)) return false;
                    wait(A1PacketGapMs);
                }
                return true;
            }

            /// <summary>SendRealtimePreamble (lines 482-530).</summary>
            private bool Enter(IHidReportChannel io, Action<int> wait)
            {
                // Marked first: the first 01 on may land and its answer not.
                _crash = new[] { A1Command(A1Realtime, A1Write, 0x00) };
                var black = A1RealtimeFrame(A1All, A1All, ReadOnlySpan<int>.Empty);
                if (A1Exchange(io, A1Command(A1Realtime, A1Write, 0x01), 0x01) == null) return false;
                for (int i = 0; i < A1PrerollFirst; i++)
                    if (!Send(io, wait, black)) return false;
                var second = Stopwatch.StartNew();
                if (A1Exchange(io, A1Command(A1Realtime, A1Write, 0x01), 0x01) == null) return false;
                for (int i = 0; i < A1PrerollSecond; i++)
                    if (!Send(io, wait, black)) return false;
                long left = A1SecondOnToTargetMs - second.ElapsedMilliseconds;
                if (left > 0) wait((int)left);
                _realtime = true;
                return true;
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                if (!_realtime && !Enter(io, wait)) return HidLightingPaint.Failed;
                var byAddress = ByAddress(lights, colors, _map.Lights.Count);
                int at = 0;
                foreach (var target in Targets)
                {
                    var span = byAddress.AsSpan(at, Math.Min(target.Leds, byAddress.Length - at));
                    at += target.Leds;
                    if (!Send(io, wait, A1RealtimeFrame(target.Port, target.Subport, span))) return HidLightingPaint.Failed;
                }
                return HidLightingPaint.Sent;
            }

            /// <summary>ExitRealtimeMode (lines 532-572).</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (_crash.Length == 0) return true;
                wait(A1ExitQuietMs);
                for (int attempt = 0; attempt < A1ExitAttempts; attempt++)
                {
                    if (A1Exchange(io, A1Command(A1Realtime, A1Write, 0x00), 0x00) != null)
                    {
                        _realtime = false;
                        _crash = Array.Empty<byte[]>();
                        return true;
                    }
                    if (attempt + 1 < A1ExitAttempts) wait(A1ExitQuietMs);
                }
                return false;
            }
        }
    }
}
