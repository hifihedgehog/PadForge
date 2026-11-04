using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Robobloq's monitor light strip (#557), lit as OpenRGB's
    /// RobobloqLightStripController lights it: 1A86:FE07 on usage page
    /// 0xFF00, usage 1 (RobobloqLightStripControllerDetect.cpp:14 and 34).
    ///
    /// <para>The protocol. Every command is a 64-byte packet, 52 42, its
    /// length, a packet index that counts up from 2, the command and its
    /// bytes, and the low byte of the sum of all of them, sent as a 65-byte
    /// output report with ID 0, after which the waiting input reports are
    /// read and dropped (SendPacket and WriteReport,
    /// RobobloqLightStripController.cpp:287-337, and the index at lines 31
    /// and 282-285). A command that answers is read back until an input report
    /// carries its index in byte 3, at most three times, with the no-op
    /// 93 00 sent between tries (SendPacketWithReply, lines 339-375).</para>
    ///
    /// <para>The lights. The device answers 82 with its screen size in
    /// inches in byte 8 and its LED count in byte 11 (RequestDeviceInfo,
    /// lines 377-421). hidapi drops the report ID, so these are bytes 9 and
    /// 12 of the report PadForge reads. The session's map is that many
    /// lights, as OpenRGB lays them out: on a 34-inch strip, 15 on the
    /// right, the top, and 15 on the left, else one strip
    /// (RGBController_RobobloqLightStrip.cpp:113-157,
    /// GetLEDsPerSide, lines 68-77). A strip that does not answer gets no
    /// session, since OpenRGB's Direct mode needs the count (SetCustom,
    /// lines 204-216). The model's own map is empty until a session reads
    /// the count, as QMK's is.</para>
    ///
    /// <para>The frames. A session begins as OpenRGB's constructor and its
    /// Direct mode do: the 82 request, the no-op 93 00 that stops the
    /// device from opening its driver page through its keyboard interface,
    /// and the Direct mode's brightness, 87 FF (lines 26-36 and 85-98,
    /// DeviceUpdateMode, RGBController_RobobloqLightStrip.cpp:177-198).
    /// OpenRGB's constructor also sends brightness F9 and the effects'
    /// speed 32, and asks for the device's information twice. The session
    /// asks once and leaves out those two writes, which Direct overwrites
    /// at once or which only change the speed of the device's own effects,
    /// a setting no reference reads back. A picture merges the colors into
    /// 34 runs as OpenRGB's MergeRobobloqRanges does (RobobloqRangeMerger.cpp:26-155)
    /// and sends them as the multi-packet sync-screen command 80: 53 43,
    /// the length in two bytes, the index, 80, the runs as start, red,
    /// green, blue and end, and the low byte of the sum, padded to whole
    /// 64-byte packets with 1 ms between them (SetCustom, SendSyncScreen
    /// and SendMultiPacket, RobobloqLightStripController.cpp:204-280).
    /// This is the stream the vendor's app sends from its screen sync
    /// (lines 195-203), not a stored effect.</para>
    ///
    /// <para>Hand-back. OpenRGB sends nothing when it closes and names no
    /// way back to the strip's own effect, so the strip keeps PadForge's
    /// last picture until its app or its controls set an effect. No source
    /// names a process of Robobloq's app, so the model names no program.</para>
    /// </summary>
    internal sealed class RobobloqLightStripLighting : IHidLightingFamily
    {
        public static readonly RobobloqLightStripLighting Instance = new();

        internal const string OpenRgbFamily = "RobobloqLightStripController";

        internal const int PacketSize = 64;
        internal const int ReportSize = 65;

        /// <summary>The commands (RobobloqLightStripController.h:56-66).</summary>
        internal const byte SyncScreen = 0x80;
        internal const byte ReadDeviceInfo = 0x82;
        internal const byte SetBrightness = 0x87;
        internal const byte SetOpenUrl = 0x93;

        /// <summary>ROBOBLOQ_TUPLE_COUNT (line 83).</summary>
        internal const int TupleCount = 34;

        /// <summary>The index of the first packet (line 31).</summary>
        internal const byte FirstIndex = 0x02;

        /// <summary>The Direct mode's brightness (RGBController_RobobloqLightStrip.cpp:57).</summary>
        internal const byte DirectBrightness = 0xFF;

        /// <summary>The waits SendPacketWithReply and SendMultiPacket take
        /// (lines 352-353 and 276).</summary>
        internal const int ReplySettleMs = 10;
        internal const int ReplyTimeoutMs = 1000;
        internal const int ReplyTries = 3;
        internal const int PacketGapMs = 1;

        /// <summary>The device information OpenRGB needs at the least
        /// (line 392), in hidapi's bytes.</summary>
        internal const int InfoLength = 24;

        /// <summary>No reference bounds the rate, so the bound is PadForge's,
        /// the one the other families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "Robobloq";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.OutputReportLength != 0 && collection.OutputReportLength < ReportSize) return null;
            var r = registrations[0];
            return _models.Get(r.Name, () => Model(r.Name, (ushort)r.VendorId, (ushort)r.ProductId));
        }

        private static HidLightingModel Model(string name, ushort vendorId, ushort productId)
        {
            string product = $"robobloq:{vendorId:X4}:{productId:X4}";
            return new HidLightingModel
            {
                Family = Instance,
                Name = name,
                Map = new LightMap(Array.Empty<LightInfo>(), "OpenRGB RobobloqLightStripController, from the device") { Product = product },
                RepaintMs = RepaintMs,
                Begin = (io, wait) => Begin(io, wait, product),
            };
        }

        // ─────────────────────────────────────────────
        //  Packets
        // ─────────────────────────────────────────────

        /// <summary>SendPacket's packet as the report WriteReport sends.</summary>
        internal static byte[] Packet(byte index, params byte[] command)
        {
            var report = new byte[ReportSize];
            report[1] = 0x52;
            report[2] = 0x42;
            report[3] = (byte)(command.Length + 5);
            report[4] = index;
            Array.Copy(command, 0, report, 5, command.Length);
            int sum = 0;
            for (int i = 1; i < 5 + command.Length; i++) sum += report[i];
            report[5 + command.Length] = (byte)sum;
            return report;
        }

        /// <summary>SendMultiPacket's reports for a command and its payload.</summary>
        internal static List<byte[]> MultiPacket(byte index, byte command, IReadOnlyList<byte> payload)
        {
            int length = payload.Count + 7;
            var data = new List<byte>(length + PacketSize) { 0x53, 0x43, (byte)(length >> 8), (byte)length, index, command };
            data.AddRange(payload);
            int sum = 0;
            foreach (byte b in data) sum += b;
            data.Add((byte)sum);
            while (data.Count % PacketSize != 0) data.Add(0x00);
            var reports = new List<byte[]>(data.Count / PacketSize);
            for (int i = 0; i < data.Count; i += PacketSize)
            {
                var report = new byte[ReportSize];
                data.CopyTo(i, report, 1, PacketSize);
                reports.Add(report);
            }
            return reports;
        }

        /// <summary>MergeRobobloqRanges (RobobloqRangeMerger.cpp:26-155): one
        /// run per LED, then the adjacent pair whose merge loses the least
        /// detail merged until <paramref name="tupleCount"/> remain, each
        /// run written as its first LED from 1, its average red, green and
        /// blue rounded half away from zero, and its last LED.</summary>
        internal static List<byte> MergeRanges(IReadOnlyList<int> colors, int tupleCount)
        {
            var bytes = new List<byte>(tupleCount * 5);
            if (tupleCount == 0) return bytes;
            var ranges = new List<(int Start, int End, int N, double R, double G, double B, double Term)>(colors.Count);
            for (int i = 0; i < colors.Count; i++)
            {
                double r = LightReports.Red(colors[i]), g = LightReports.Green(colors[i]), b = LightReports.Blue(colors[i]);
                ranges.Add((i + 1, i + 1, 1, r, g, b, r * r + g * g + b * b));
            }
            while (ranges.Count > tupleCount)
            {
                double bestDelta = double.MaxValue;
                int bestIndex = -1;
                double bestTerm = 0;
                for (int i = 0; i < ranges.Count - 1; i++)
                {
                    var r1 = ranges[i];
                    var r2 = ranges[i + 1];
                    double sumR = r1.R + r2.R, sumG = r1.G + r2.G, sumB = r1.B + r2.B;
                    int n = r1.N + r2.N;
                    double merged = (sumR * sumR + sumG * sumG + sumB * sumB) / n;
                    double delta = r1.Term + r2.Term - merged;
                    if (delta < bestDelta)
                    {
                        bestDelta = delta;
                        bestIndex = i;
                        bestTerm = merged;
                    }
                }
                if (bestIndex == -1) break;
                var left = ranges[bestIndex];
                var right = ranges[bestIndex + 1];
                ranges[bestIndex] = (left.Start, right.End, left.N + right.N, left.R + right.R, left.G + right.G, left.B + right.B, bestTerm);
                ranges.RemoveAt(bestIndex + 1);
            }
            foreach (var range in ranges)
            {
                bytes.Add((byte)range.Start);
                bytes.Add((byte)Math.Round(range.R / range.N, MidpointRounding.AwayFromZero));
                bytes.Add((byte)Math.Round(range.G / range.N, MidpointRounding.AwayFromZero));
                bytes.Add((byte)Math.Round(range.B / range.N, MidpointRounding.AwayFromZero));
                bytes.Add((byte)range.End);
            }
            return bytes;
        }

        /// <summary>The map OpenRGB lays out for a strip of
        /// <paramref name="count"/> LEDs on a screen of
        /// <paramref name="inches"/>: on 34 inches, the right, top and left
        /// as zones 1 to 3, else one strip as zone 1.</summary>
        internal static LightMap MapOf(int count, int inches, string product)
        {
            int perSide = inches == 34 ? 15 : 0;
            var lights = new LightInfo[count];
            for (int i = 0; i < count; i++)
            {
                int zone = perSide == 0 ? 1 : i < perSide ? 1 : i < count - perSide ? 2 : 3;
                lights[i] = new LightInfo(i, LightKind.Zone, 0, LightKeySource.None) { Zone = zone };
            }
            return new LightMap(lights, "OpenRGB RGBController_RobobloqLightStrip.cpp, from the device") { Product = product };
        }

        // ─────────────────────────────────────────────
        //  The session
        // ─────────────────────────────────────────────

        /// <summary>A session on the LED count the device answers, or null
        /// when it answered none.</summary>
        private static HidLightingSession Begin(IHidReportChannel io, Action<int> wait, string product)
        {
            if (io == null) return null;
            var session = new Session(io, wait);
            var info = session.Ask(ReadDeviceInfo);
            if (info == null || info.Length < InfoLength) return null;
            int inches = info[8];
            int count = info[11];
            if (count == 0) return null;
            return session.Start(MapOf(count, inches, product), count) ? session : null;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly IHidReportChannel _io;
            private readonly Action<int> _wait;
            private byte _index = FirstIndex;
            private LightMap _map;
            private int _count;

            public Session(IHidReportChannel io, Action<int> wait)
            {
                _io = io;
                _wait = wait ?? (_ => { });
            }

            public override bool Feature => false;

            public override LightMap Map => _map;

            /// <summary>The LEDs the device answered.</summary>
            public int Count => _count;

            /// <summary>Nothing undoes a picture: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            /// <summary>SendPacket: the packet at the next index, then, when
            /// <paramref name="flush"/> is set, every waiting input report
            /// read and dropped.</summary>
            internal bool Send(bool flush, params byte[] command)
            {
                bool sent = _io.WriteOutput(Packet(_index, command));
                _index++;
                if (!sent) return false;
                if (flush)
                {
                    var buffer = new byte[ReportSize];
                    while (_io.ReadInput(buffer, 0) > 0) { }
                }
                return true;
            }

            /// <summary>SendPacketWithReply: the answer as hidapi hands it,
            /// the report ID dropped, or null when none came.</summary>
            internal byte[] Ask(params byte[] command)
            {
                byte expected = _index;
                if (!Send(false, command)) return null;
                var buffer = new byte[ReportSize];
                for (int tries = 0; tries < ReplyTries; tries++)
                {
                    int read;
                    do
                    {
                        _wait(ReplySettleMs);
                        read = _io.ReadInput(buffer, ReplyTimeoutMs);
                        if (read > 4 && buffer[4] == expected) return buffer.AsSpan(1, read - 1).ToArray();
                    }
                    while (read > 0);
                    if (read < 0) return null;
                    if (!Send(false, SetOpenUrl, 0x00)) return null;
                }
                return null;
            }

            /// <summary>The opening writes and the map the session lights by.</summary>
            internal bool Start(LightMap map, int count)
            {
                _map = map;
                _count = count;
                return Send(true, SetOpenUrl, 0x00) && Send(true, SetBrightness, DirectBrightness);
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                var byAddress = KeyboardLedNames.ByAddress(lights, colors, _count);
                var reports = MultiPacket(_index, SyncScreen, MergeRanges(byAddress, TupleCount));
                _index++;
                foreach (var report in reports)
                {
                    if (!_io.WriteOutput(report)) return HidLightingPaint.Failed;
                    (wait ?? _wait)(PacketGapMs);
                }
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
