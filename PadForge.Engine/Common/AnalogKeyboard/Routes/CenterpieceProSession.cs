using System;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// Finalmouse Centerpiece Pro (361D:0200), read the way LeiterConsulting's
    /// Soup fork reads it (github.com/LeiterConsulting/Soup, commits 85e6de1
    /// to e73548e, soup/CpproProtocol.hpp and soup/AnalogueKeyboard.cpp).
    ///
    /// <para>The host asks for key telemetry with output report 3,
    /// <c>03 02 F0 1D</c> padded to 64 bytes (CpproProtocol.hpp:12-14), and
    /// asks again every 2.5 s: the fork's comment says Finalmouse's XPanel
    /// refreshes the same read-only request every 3 s, and refreshing a
    /// little sooner keeps the telemetry coming while no key moves
    /// (AnalogueKeyboard.cpp:1273-1286). The fork's first commit names the
    /// frame "report 3, CMD_KEY_REPORTS (0x1D)", and its hwHid writes the
    /// buffer as is, so 03 is the output report ID on every platform
    /// (hwHid.cpp:763-796). The route also requires the collection to declare
    /// output report 3, so the write can only go where the fork's would.</para>
    ///
    /// <para>Each key report is input report 4 carrying one key: byte 2 is an
    /// event type from 1 to 3, byte 3 the hardware key index H1 to H68, and
    /// byte 6 the travel from 0 to 40 (CpproProtocol.hpp:51-73). The fork's
    /// first commit describes the fields after the report ID as "length,
    /// event type, hardware key, pressed, distance (0.1 mm)", and its travel
    /// fix moved the distance to byte 6, byte 5 being reserved and zero in
    /// the live <c>04 08 03</c> reports (commit 1395db0). So 40 is 4.0 mm.
    /// A report updates its one key and leaves the others.</para>
    /// </summary>
    public static class CenterpieceProProtocol
    {
        public const ushort VendorId = 0x361D;
        public const ushort ProductId = 0x0200;
        public const ushort UsagePage = 0xFF00;
        public const ushort Usage = 0x0001;

        /// <summary>The input report that carries key travel.</summary>
        public const byte KeyReportId = 4;

        /// <summary>The output report the telemetry request goes out on.</summary>
        public const byte RequestReportId = 3;

        /// <summary>The fork's request length, report ID included
        /// (OUTPUT_REPORT_SIZE, CpproProtocol.hpp:12).</summary>
        public const int RequestLength = 64;

        /// <summary>How often the request is repeated
        /// (KEY_REPORT_REFRESH_MS, CpproProtocol.hpp:13).</summary>
        public const int RefreshMs = 2500;

        /// <summary>Travel at the bottom of the press, 4.0 mm.</summary>
        public const int FullTravel = 40;

        /// <summary>Smallest key report the decoder reads (CpproProtocol.hpp:53).</summary>
        public const int MinReportLength = 8;

        public const string ModelName = "Finalmouse Centerpiece Pro";

        /// <summary>The H1 to H68 key table in the data file, index 0 unused
        /// (KEY_LAYOUT, CpproProtocol.hpp:16-37).</summary>
        public const string TableName = "cppro";

        /// <summary>The fork's identity test: 361D:0200, usage page FF00,
        /// usage 1, input report 4 (AnalogueKeyboard.cpp:206-217). Output
        /// report 3 and room for the four request bytes are PadForge's own
        /// checks that the request's report ID belongs to this collection.</summary>
        public static bool Matches(AnalogKeyboardDeviceInfo info)
            => info != null
               && info.VendorId == VendorId
               && info.ProductId == ProductId
               && info.UsagePage == UsagePage
               && info.Usage == Usage
               && info.HasInputReport(KeyReportId)
               && info.HasOutputReport(RequestReportId)
               && info.OutputReportLength >= 4;

        /// <summary>The H index to key code table.</summary>
        public static int[] Table() => AnalogKeyboardData.Table(OtherRoutes.DataFile, TableName);

        /// <summary>The key telemetry request, <c>03 02 F0 1D</c> and zeros
        /// to 64 bytes as the fork sends it. When the collection's output
        /// report is shorter than 64 bytes, only trailing zeros are dropped so
        /// the write is exactly the declared length: the fork would write 64
        /// bytes there, which Windows need not accept.</summary>
        public static byte[] Request(int outputLength = RequestLength)
        {
            int length = outputLength >= 4 && outputLength < RequestLength ? outputLength : RequestLength;
            var r = new byte[length];
            r[0] = RequestReportId;
            r[1] = 0x02;
            r[2] = 0xF0;
            r[3] = 0x1D;
            return r;
        }

        /// <summary>Depth for a travel byte: over 40 counts as the bottom,
        /// normalizeDistance's clamp (CpproProtocol.hpp:45-49). The fork keeps
        /// the value as a byte, (travel * 255 + 20) / 40, and divides by 255
        /// later. The depth here is the travel over 40 without that rounding,
        /// the same value to within 1/510.</summary>
        public static float Depth(int travel)
            => travel <= 0 ? 0f : Math.Min(travel, FullTravel) / (float)FullTravel;

        /// <summary>decodeKeyReport (CpproProtocol.hpp:51-73): report 4 at
        /// least 8 bytes long, event type 1 to 3, a hardware index the table
        /// maps to a key. False for anything else, which the reader skips.</summary>
        public static bool TryDecode(ReadOnlySpan<byte> report, int[] table, out int code, out float depth)
        {
            code = 0;
            depth = 0f;
            if (table == null || report.Length < MinReportLength || report[0] != KeyReportId) return false;
            if (report[2] < 1 || report[2] > 3) return false;
            if (report[3] >= table.Length) return false;
            code = table[report[3]];
            if (code == 0) return false;
            depth = Depth(report[6]);
            return true;
        }
    }

    /// <summary>
    /// A Centerpiece Pro conversation: the telemetry request at the start and
    /// again every 2.5 s, and one key report per pass. Soup's
    /// getActiveKeysCppro (AnalogueKeyboard.cpp:1269-1321) in PadForge's pass
    /// shape. The fork calls the request read-only (AnalogueKeyboard.cpp:1273)
    /// and sends nothing to end it, so <see cref="AnalogKeyboardSession.Stop"/>
    /// has nothing to undo.
    /// </summary>
    public sealed class CenterpieceProSession : AnalogKeyboardSession
    {
        /// <summary>Longest single wait for a report, so the refresh stays on
        /// time and the reader sees a stop request.</summary>
        public const int WaitMs = 250;

        private readonly Func<long> _clock;
        private readonly int[] _table;
        private long _nextRequest;

        public CenterpieceProSession() : this(null) { }

        /// <summary><paramref name="clock"/> returns milliseconds, by default
        /// <see cref="Environment.TickCount64"/>. Tests pass their own.</summary>
        public CenterpieceProSession(Func<long> clock)
        {
            _clock = clock ?? (() => Environment.TickCount64);
            _table = CenterpieceProProtocol.Table();
        }

        /// <summary>When the next request is due, on the session's clock.</summary>
        public long NextRequestAt => _nextRequest;

        public override string ModelName => CenterpieceProProtocol.ModelName;

        public override int[] KeyOrder => AnalogKeyboardData.KeysOf(_table);

        /// <summary>Sends the first request. The fork sends it on its first
        /// read (next_key_reports_request starts at 0, AnalogueKeyboard.cpp:418-421).
        /// No answer is defined for it, so the identity rests on the
        /// collection's metadata.</summary>
        public override bool Start(IAnalogKeyboardTransport io) => SendRequest(io);

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            if (_clock() >= _nextRequest && !SendRequest(io)) return AnalogPollResult.Failed;
            int wait = (int)Math.Clamp(_nextRequest - _clock(), 1, WaitMs);
            int n = io.Receive(Buffer, wait);
            if (n < 0) return AnalogPollResult.Failed;
            if (n == 0) return AnalogPollResult.Idle;
            if (!CenterpieceProProtocol.TryDecode(Buffer.AsSpan(0, n), _table, out int code, out float depth))
                return AnalogPollResult.Idle;
            output.Set(code, depth);
            return AnalogPollResult.Ok;
        }

        /// <summary>A failed write ends the session, as the fork marks the
        /// keyboard disconnected (AnalogueKeyboard.cpp:1280-1284). The next
        /// request is due 2.5 s after this one.</summary>
        private bool SendRequest(IAnalogKeyboardTransport io)
        {
            if (!io.Send(CenterpieceProProtocol.Request(io.OutputLength))) return false;
            _nextRequest = _clock() + CenterpieceProProtocol.RefreshMs;
            return true;
        }
    }
}
