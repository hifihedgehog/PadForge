using System;
using System.Threading;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// The Neo65 SONIC HE+ wire protocol, HallJoy's neo65_protocol.h. Every
    /// request and every answer is one unnumbered 33-byte report on the vendor
    /// collection FF60:61: byte 0 is the report ID 00, then D0, an opcode, a
    /// start slot and a count (neo65_protocol.h:17-20). An answer echoes those
    /// five bytes and carries its payload from byte 5 (neo65_protocol.h:14-16).
    /// Three read-only opcodes are used: A9 answers the size of one per-key
    /// range record, AA the range records, A6 the key depths. The matrix has
    /// 80 slots, five rows of 16. Depths and travels are in 1/10000 mm
    /// (docs/current/NEO65_AND_PROTOCOL_EXPANSION_2026-09-24.md:30-32).
    /// </summary>
    public static class Neo65Protocol
    {
        public const ushort VendorId = 0xE560;
        public const ushort AnsiProductId = 0xEE65;
        public const ushort IsoProductId = 0xEF65;
        public const ushort UsagePage = 0xFF60;
        public const ushort Usage = 0x61;

        /// <summary>Windows length of every report, the report ID byte
        /// included (neo65_backend.cpp:62-63).</summary>
        public const int ReportLength = 33;

        /// <summary>Matrix slots, five rows of 16 (neo65_protocol.h:8).</summary>
        public const int Slots = 80;

        public const byte Command = 0xD0;
        public const byte StrideOp = 0xA9;
        public const byte RangeOp = 0xAA;
        public const byte DepthOp = 0xA6;

        /// <summary>Most depths one A6 answer carries: 14 big-endian values
        /// fill bytes 5 to 32 (neo65_protocol.h:22).</summary>
        public const int DepthsPerPage = 14;

        /// <summary>Range records one AA request may ask for, divided by the
        /// record size (neo65_protocol.h:30).</summary>
        public const int RangeBytesPerPage = 28;

        /// <summary>Bounds of the A9 record size (neo65_backend.cpp:108).</summary>
        public const int MinStride = 9, MaxStride = 28;

        /// <summary>Largest depth or key travel the firmware reports
        /// (neo65_protocol.h:23 and 36).</summary>
        public const int MaxRaw = 40000;

        /// <summary>Shortest key travel a populated slot may report
        /// (neo65_protocol.h:36).</summary>
        public const int MinAxis = 1000;

        /// <summary>HallJoy's name for the keyboard (neo65_backend.cpp:199).</summary>
        public const string ModelName = "Neo65 SONIC HE+";

        /// <summary>The collection HallJoy admits: exact VID and PID, 33-byte
        /// input and output reports, usage FF60:61 (neo65_backend.cpp:59-63).
        /// Strings and interface number are not checked.</summary>
        public static bool Matches(AnalogKeyboardDeviceInfo info)
            => info != null && info.VendorId == VendorId
               && (info.ProductId == AnsiProductId || info.ProductId == IsoProductId)
               && info.InputReportLength == ReportLength && info.OutputReportLength == ReportLength
               && info.UsagePage == UsagePage && info.Usage == Usage;

        /// <summary>Factory key code per matrix slot, ISO for EF65 and ANSI
        /// otherwise (neo65_protocol.h:10-12).</summary>
        public static int[] Table(ushort productId)
            => AnalogKeyboardData.Table(NeoApexMixRoutes.DataFile,
                productId == IsoProductId ? "neo65_iso" : "neo65_ansi");

        /// <summary>A request: 00 D0, the opcode, the start slot and the
        /// count, zero to 33 bytes (neo65_protocol.h:17-20).</summary>
        public static byte[] Request(byte op, int start = 0, int count = 0)
        {
            var r = new byte[ReportLength];
            r[1] = Command;
            r[2] = op;
            r[3] = (byte)start;
            r[4] = (byte)count;
            return r;
        }

        /// <summary>True when an answer's first five bytes are the header of
        /// the request it answers (neo65_protocol.h:14-16).</summary>
        public static bool Echo(ReadOnlySpan<byte> answer, byte op, int start, int count)
            => answer.Length >= 5 && answer[0] == 0 && answer[1] == Command && answer[2] == op
               && answer[3] == start && answer[4] == count;

        private static int Be16(ReadOnlySpan<byte> p) => (p[0] << 8) | p[1];

        /// <summary>A key's travel window: rest at or below
        /// <see cref="Low"/>, the bottom at or above <see cref="High"/>.
        /// Empty when High is not above Low.</summary>
        public readonly struct Range
        {
            public Range(int low, int high)
            {
                Low = (ushort)low;
                High = (ushort)high;
            }

            public ushort Low { get; }
            public ushort High { get; }
            public bool IsEmpty => High <= Low;
        }

        /// <summary>Reads the depths of one A6 answer into
        /// <paramref name="values"/> at <paramref name="start"/>. The whole
        /// page is rejected, and nothing written, when the header does not
        /// match or any value is above 40000 (neo65_protocol.h:21-26).</summary>
        public static bool ParseDepth(ReadOnlySpan<byte> answer, int start, int count, ushort[] values)
        {
            if (answer.Length < ReportLength || count <= 0 || count > DepthsPerPage || start < 0
                || start >= Slots || count > Slots - start || !Echo(answer, DepthOp, start, count))
                return false;
            for (int i = 0; i < count; i++)
                if (Be16(answer.Slice(5 + i * 2)) > MaxRaw) return false;
            for (int i = 0; i < count; i++)
                values[start + i] = (ushort)Be16(answer.Slice(5 + i * 2));
            return true;
        }

        /// <summary>
        /// Reads the range records of one AA answer into
        /// <paramref name="ranges"/> at <paramref name="start"/>, all or
        /// nothing (neo65_protocol.h:29-40). Record i starts at byte
        /// 5 + i * stride: byte 0 enables the dead zones, bytes 1-2 are the
        /// key's full travel ("axisID", rounded down to a multiple of 100),
        /// bytes 5-6 the top dead zone and bytes 7-8 the bottom dead zone,
        /// big-endian. A zero travel is an unpopulated slot. Otherwise the
        /// travel must lie in 1000..40000 and exceed both dead zones together,
        /// and the range runs from the top dead zone to the travel less the
        /// bottom dead zone. Bytes 3-4 and 9 on are not read (UNKNOWN in the
        /// spec).
        /// </summary>
        public static bool ParseRanges(ReadOnlySpan<byte> answer, int start, int count, int stride, Range[] ranges)
        {
            if (answer.Length < ReportLength || stride < MinStride || stride > MaxStride || count <= 0
                || count > RangeBytesPerPage / stride || start < 0 || start >= Slots || count > Slots - start
                || !Echo(answer, RangeOp, start, count))
                return false;
            Span<Range> next = stackalloc Range[count];
            for (int i = 0; i < count; i++)
            {
                var q = answer.Slice(5 + i * stride);
                int axis = Be16(q.Slice(1)) / 100 * 100;
                int top = q[0] != 0 ? Be16(q.Slice(5)) : 0;
                int bottom = q[0] != 0 ? Be16(q.Slice(7)) : 0;
                if (axis == 0)
                {
                    next[i] = default;
                    continue;
                }
                if (axis > MaxRaw || axis < MinAxis || top + bottom >= axis) return false;
                next[i] = new Range(top, axis - bottom);
            }
            next.CopyTo(ranges.AsSpan(start, count));
            return true;
        }

        /// <summary>A depth as HallJoy's 0..1000 (neo65_protocol.h:41-45):
        /// 0 at or below the range's low end, 1000 at or above its high end,
        /// linear and rounded to nearest between.</summary>
        public static int Normalize(int raw, Range range)
        {
            if (range.High <= range.Low || raw <= range.Low) return 0;
            if (raw >= range.High) return 1000;
            int span = range.High - range.Low;
            return ((raw - range.Low) * 1000 + span / 2) / span;
        }
    }

    /// <summary>
    /// One Neo65 SONIC HE+ read the way HallJoy's neo65 route reads it
    /// (neo65_backend.cpp:97-143). The handshake asks for the range record
    /// size (A9), reads the per-key travel ranges (AA) in pages of
    /// 28 / stride records, requires a nonzero range for every slot that
    /// carries a key, and then proves the path with one complete depth frame
    /// within 100 ms, which is when HallJoy claims the collection
    /// (neo65_backend.cpp:130-135). A pass is one frame: six A6 pages of up
    /// to 14 slots. The route only reads: no calibration, mode or setting is
    /// ever written (docs/SUPPORTED_HARDWARE.md:189-192), and nothing is sent
    /// at the end (neo65_backend.cpp:142).
    ///
    /// <para>Ranges are read once per session, as HallJoy does. Any failed
    /// exchange, a rejected page or a frame slower than 100 ms ends the
    /// session (neo65_backend.cpp:130), so <see cref="MissLimit"/> is 1.</para>
    /// </summary>
    public sealed class Neo65Session : AnalogKeyboardSession
    {
        /// <summary>One deadline for a request's write and every read of its
        /// answer (neo65_backend.cpp:89).</summary>
        public const int ExchangeMs = 100;

        /// <summary>Longest frame before the session ends (neo65_backend.cpp:130).</summary>
        public const int FrameMs = 100;

        /// <summary>Answers read per request before giving up on its echo
        /// (neo65_backend.cpp:91).</summary>
        public const int MaxAnswersPerRequest = 16;

        private readonly int[] _table;
        private readonly Neo65Protocol.Range[] _ranges = new Neo65Protocol.Range[Neo65Protocol.Slots];
        private readonly ushort[] _values = new ushort[Neo65Protocol.Slots];

        /// <summary>The millisecond clock deadlines are measured on,
        /// GetTickCount64 as in HallJoy. Tests substitute a scripted one.</summary>
        internal Func<long> Clock = () => Environment.TickCount64;

        public Neo65Session(ushort productId)
        {
            ProductId = productId;
            _table = Neo65Protocol.Table(productId);
        }

        public ushort ProductId { get; }

        /// <summary>The range record size the keyboard reported, 0 before
        /// the handshake.</summary>
        public int Stride { get; private set; }

        public override string ModelName => Neo65Protocol.ModelName;

        public override int[] KeyOrder => AnalogKeyboardData.KeysOf(_table);

        public override int MissLimit => 1;

        /// <summary>The range the handshake read for a slot.</summary>
        public Neo65Protocol.Range RangeAt(int slot) => _ranges[slot];

        public override bool Start(IAnalogKeyboardTransport io)
        {
            // neo65_backend.cpp:104-108: the range record size, 9 to 28.
            var request = Neo65Protocol.Request(Neo65Protocol.StrideOp);
            if (Exchange(io, request, 3) != AnalogPollResult.Ok) return false;
            int stride = Buffer[3];
            if (stride < Neo65Protocol.MinStride || stride > Neo65Protocol.MaxStride) return false;

            // neo65_backend.cpp:109-114: every slot's range, 28 / stride per page.
            var ranges = new Neo65Protocol.Range[Neo65Protocol.Slots];
            for (int start = 0; start < Neo65Protocol.Slots;)
            {
                int count = Math.Min(Neo65Protocol.Slots - start, Neo65Protocol.RangeBytesPerPage / stride);
                request = Neo65Protocol.Request(Neo65Protocol.RangeOp, start, count);
                if (Exchange(io, request, 5) != AnalogPollResult.Ok
                    || !Neo65Protocol.ParseRanges(Buffer.AsSpan(0, Neo65Protocol.ReportLength), start, count, stride, ranges))
                    return false;
                start += count;
            }

            // neo65_backend.cpp:115-119: a slot that carries a key needs a range.
            for (int i = 0; i < Neo65Protocol.Slots; i++)
                if (_table[i] != 0 && ranges[i].IsEmpty) return false;
            ranges.CopyTo(_ranges, 0);
            Stride = stride;

            // neo65_backend.cpp:130-135: one complete frame within 100 ms
            // before the collection is claimed. Its values are not kept: the
            // first pass reads a fresh frame.
            return ReadFrame(io) == AnalogPollResult.Ok;
        }

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            long frameStart = Clock();
            var result = ReadFrame(io);
            if (result != AnalogPollResult.Ok) return result;

            // neo65_backend.cpp:136-137: every slot that carries a key. A
            // code on two slots would read as the deeper one, HallJoy's
            // Publication::Read (physical_analog_state.h:39-52). The factory
            // tables carry each code once.
            long now = Clock();
            output.ResetForReuse();
            for (int i = 0; i < Neo65Protocol.Slots; i++)
            {
                int code = _table[i];
                if (code == 0) continue;
                int milli = Neo65Protocol.Normalize(_values[i], _ranges[i]);
                if (milli > 0 && milli / 1000f > output.Get(code)) output.Set(code, milli / 1000f);
            }

            // neo65_backend.cpp:139-140: a frame that finished within the
            // tick it started in waits 1 ms, so an answer that completes at
            // once cannot turn the reader into a busy loop.
            if (now == frameStart) Thread.Sleep(1);
            return AnalogPollResult.Ok;
        }

        /// <summary>One frame, the six A6 pages (starts 0, 14, 28, 42, 56,
        /// 70), into the depth array. Not Ok when any page fails or the frame
        /// takes more than 100 ms (neo65_backend.cpp:122-130).</summary>
        private AnalogPollResult ReadFrame(IAnalogKeyboardTransport io)
        {
            long frameStart = Clock();
            for (int start = 0; start < Neo65Protocol.Slots;)
            {
                int count = Math.Min(Neo65Protocol.Slots - start, Neo65Protocol.DepthsPerPage);
                var request = Neo65Protocol.Request(Neo65Protocol.DepthOp, start, count);
                var result = Exchange(io, request, 5);
                if (result != AnalogPollResult.Ok) return result;
                if (!Neo65Protocol.ParseDepth(Buffer.AsSpan(0, Neo65Protocol.ReportLength), start, count, _values))
                    return AnalogPollResult.Failed;
                start += count;
            }
            return Clock() - frameStart > FrameMs ? AnalogPollResult.NoAnswer : AnalogPollResult.Ok;
        }

        /// <summary>
        /// One request and its answer, HallJoy's Exchange
        /// (neo65_backend.cpp:72-96): one 33-byte write, then up to 16 reads
        /// of 33 bytes until an answer whose first <paramref name="echoBytes"/>
        /// bytes equal the request's. Answers to other requests are skipped.
        /// One 100 ms deadline covers the write and all reads, and a write
        /// that outlasts it fails the exchange, as HallJoy cancels it. Ok
        /// leaves the answer in <see cref="AnalogKeyboardSession.Buffer"/>.
        /// NoAnswer is a timeout, Failed a failed write, a gone device, a
        /// read of another length or 16 answers without the echo.
        /// </summary>
        private AnalogPollResult Exchange(IAnalogKeyboardTransport io, byte[] request, int echoBytes)
        {
            long deadline = Clock() + ExchangeMs;
            if (!io.Send(request)) return AnalogPollResult.Failed;
            for (int attempt = 0; attempt < MaxAnswersPerRequest; attempt++)
            {
                long left = deadline - Clock();
                if (left <= 0) return AnalogPollResult.NoAnswer;
                int n = io.Receive(Buffer, (int)left);
                if (n < 0) return AnalogPollResult.Failed;
                if (n == 0) return AnalogPollResult.NoAnswer;
                if (n != Neo65Protocol.ReportLength) return AnalogPollResult.Failed;
                if (Buffer.AsSpan(0, echoBytes).SequenceEqual(request.AsSpan(0, echoBytes)))
                    return AnalogPollResult.Ok;
            }
            return AnalogPollResult.Failed;
        }
    }
}
