using System;
using System.Threading;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// The SteelSeries Apex Pro legacy vendor protocol on firmware 4.16.8,
    /// HallJoy's steelseries_apex_protocol.h. Requests and answers are
    /// unnumbered 64-byte reports on the vendor collection FFC0:1, 65 bytes
    /// with the report ID (docs/current/STEELSERIES_APEX_PRO_IMPLEMENTATION_2026-09-25.md:16-23).
    /// Answers carry no command or bank echo, so only one request is ever in
    /// flight. Three read commands are used and nothing else can be sent:
    /// 90 (firmware version), D7 bank (14 sensors of ADC samples) and DA
    /// (the firmware's calibration extrema of up to 12 sensors, each named by
    /// its HID code). The keyboard has 70 sensor slots, 68 of them analog
    /// (the OmniPoint section only: no Esc, F-row, navigation or arrows).
    ///
    /// <para>Command 01 with byte 02 reboots this firmware (SYSRESETREQ), which
    /// an older HallJoy SparkLink probe sent to every FFxx:1 collection
    /// (STEELSERIES_APEX_PRO_IMPLEMENTATION_2026-09-25.md:90-111). No other
    /// route may send its probe to a 1038 collection.</para>
    /// </summary>
    public static class SteelSeriesApexProtocol
    {
        public const ushort VendorId = 0x1038;
        public const ushort ApexProProductId = 0x1610;
        public const ushort ApexProTklProductId = 0x1614;
        public const ushort ApexProGen3ProductId = 0x1640;
        public const ushort UsagePage = 0xFFC0;
        public const ushort Usage = 0x0001;

        /// <summary>Windows report lengths, report ID included
        /// (steelseries_apex_protocol.h:13).</summary>
        public const int ReportLength = 65;
        public const int FeatureLength = 643;

        /// <summary>Sensor slots in the map and the depth banks
        /// (steelseries_apex_protocol.h:17).</summary>
        public const int Sensors = 70;

        /// <summary>Sensors with a map entry, the ones ranges are read and
        /// depths published for (steelseries_apex_backend.cpp:118, 153, 186).</summary>
        public const int AnalogSensors = 68;

        /// <summary>Sensors per D7 bank, and D7 banks per frame.</summary>
        public const int SensorsPerBank = 14, Banks = 5;

        /// <summary>Most sensors one DA request may name
        /// (steelseries_apex_protocol.h:27).</summary>
        public const int RangesPerRequest = 12;

        /// <summary>The OEM code at sensor 65, never bound to a key
        /// (steelseries_apex_backend.cpp:153).</summary>
        public const int OemCode = 240;

        /// <summary>Largest 12-bit ADC value (steelseries_apex_protocol.h:46, 56).</summary>
        public const int MaxAdc = 4095;

        /// <summary>The first and last sensor of the main alphanumeric
        /// block, which must all carry a calibration
        /// (steelseries_apex_backend.cpp:125-126).</summary>
        public const int FirstRequiredSensor = 14, LastRequiredSensor = 39;

        public const byte VersionCommand = 0x90;
        public const byte DepthCommand = 0xD7;
        public const byte RangeCommand = 0xDA;

        /// <summary>The one firmware HallJoy admits, with its NUL: "4.16.8"
        /// (steelseries_apex_protocol.h:24).</summary>
        public static ReadOnlySpan<byte> KnownVersionBytes => "4.16.8\0"u8;

        public static bool SupportedIdentity(ushort vendorId, ushort productId)
            => vendorId == VendorId
               && (productId == ApexProProductId || productId == ApexProTklProductId || productId == ApexProGen3ProductId);

        /// <summary>HallJoy's model names (steelseries_apex_protocol.h:9-11).</summary>
        public static string ModelName(ushort productId) => productId switch
        {
            ApexProTklProductId => "SteelSeries Apex Pro TKL",
            ApexProGen3ProductId => "SteelSeries Apex Pro Gen 3",
            _ => "SteelSeries Apex Pro",
        };

        /// <summary>FFC0:1 with 65-byte input and output and a 643-byte
        /// feature report (steelseries_apex_protocol.h:12-14). The feature
        /// report is part of the shape only: it is never used.</summary>
        public static bool SupportedCollection(ushort usagePage, ushort usage, int input, int output, int feature)
            => usagePage == UsagePage && usage == Usage && input == ReportLength && output == ReportLength
               && feature == FeatureLength;

        /// <summary>The collection HallJoy admits
        /// (steelseries_apex_backend.cpp:66-70). Strings and interface number
        /// are not checked.</summary>
        public static bool Matches(AnalogKeyboardDeviceInfo info)
            => info != null && SupportedIdentity(info.VendorId, info.ProductId)
               && SupportedCollection(info.UsagePage, info.Usage, info.InputReportLength,
                   info.OutputReportLength, info.FeatureReportLength);

        /// <summary>The HID code per sensor, the inverse of firmware 4.16.8's
        /// ROM map at 0x08023DD4 (steelseries_apex_protocol.h:18-19),
        /// verbatim: OEM 240 at sensor 65 and 0 at the spare sensors 68 and
        /// 69. The DA request names sensors by these codes.</summary>
        public static int[] Map => AnalogKeyboardData.Table(NeoApexMixRoutes.DataFile, "steelseries_apex_kmap");

        /// <summary>The map as keys: sensors 0 to 67 without the OEM code, 67
        /// keys (steelseries_apex_backend.cpp:153).</summary>
        public static int[] BindableTable()
        {
            var map = Map;
            var table = new int[Sensors];
            for (int i = 0; i < AnalogSensors; i++)
                if (map[i] != OemCode) table[i] = map[i];
            return table;
        }

        private static int Le16(ReadOnlySpan<byte> p) => p[0] | (p[1] << 8);

        /// <summary>A sensor's calibration: rest at or below
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

        /// <summary><c>00 90</c>, then zeros to 65 bytes
        /// (steelseries_apex_protocol.h:23).</summary>
        public static byte[] VersionRequest()
        {
            var r = new byte[ReportLength];
            r[1] = VersionCommand;
            return r;
        }

        /// <summary>True for firmware 4.16.8: the answer's bytes 1 to 7 are
        /// "4.16.8" and its NUL (steelseries_apex_protocol.h:24).</summary>
        public static bool KnownVersion(ReadOnlySpan<byte> answer)
            => answer.Length >= 8 && answer[0] == 0 && answer.Slice(1, 7).SequenceEqual(KnownVersionBytes);

        /// <summary><c>00 D7 bank</c> for bank 1 to 5, all zeros for any
        /// other bank (steelseries_apex_protocol.h:25).</summary>
        public static byte[] DepthRequest(int bank)
        {
            var r = new byte[ReportLength];
            if (bank >= 1 && bank <= Banks)
            {
                r[1] = DepthCommand;
                r[2] = (byte)bank;
            }
            return r;
        }

        /// <summary><c>00 DA count</c>, then per sensor its HID code followed
        /// by four zeros. All zeros for a start or count out of range
        /// (steelseries_apex_protocol.h:26-31).</summary>
        public static byte[] RangeRequest(int start, int count)
        {
            var r = new byte[ReportLength];
            if (count <= 0 || count > RangesPerRequest || start < 0 || start >= AnalogSensors
                || count > AnalogSensors - start)
                return r;
            var map = Map;
            r[1] = RangeCommand;
            r[2] = (byte)count;
            for (int i = 0; i < count; i++) r[3 + i * 5] = (byte)map[start + i];
            return r;
        }

        /// <summary>The allowlist every request passes before it is written
        /// (steelseries_apex_protocol.h:32-39, steelseries_apex_backend.cpp:88):
        /// exactly a version request, a depth request for bank 1 to 5, or a
        /// range request for some start and a count of 1 to 12.</summary>
        public static bool ReadOnlyRequest(byte[] request)
        {
            if (request == null || request.Length != ReportLength || request[0] != 0) return false;
            if (request[1] == VersionCommand) return request.AsSpan().SequenceEqual(VersionRequest());
            if (request[1] == DepthCommand)
                return request[2] >= 1 && request[2] <= Banks && request.AsSpan().SequenceEqual(DepthRequest(request[2]));
            if (request[1] != RangeCommand || request[2] == 0 || request[2] > RangesPerRequest) return false;
            for (int start = 0; start < AnalogSensors; start++)
                if (request.AsSpan().SequenceEqual(RangeRequest(start, request[2]))) return true;
            return false;
        }

        /// <summary>
        /// Reads a DA answer into <paramref name="ranges"/> at
        /// <paramref name="start"/>, all or nothing
        /// (steelseries_apex_protocol.h:40-52). Record i at byte 1 + 5i is the
        /// echoed HID code, then the maximum and the minimum as little-endian
        /// values of at most 4095. A sensor whose maximum is not more than 16
        /// above its minimum gets an empty range: unpopulated regional
        /// positions keep reversed factory extrema, and a range is never
        /// invented (steelseries_apex_protocol.h:47). Every byte after the
        /// records must be zero.
        /// </summary>
        public static bool ParseRanges(ReadOnlySpan<byte> answer, int start, int count, Range[] ranges)
        {
            if (answer.Length < ReportLength || answer[0] != 0 || count <= 0 || count > RangesPerRequest
                || start < 0 || start >= AnalogSensors || count > AnalogSensors - start)
                return false;
            var map = Map;
            Span<Range> next = stackalloc Range[count];
            for (int i = 0; i < count; i++)
            {
                var q = answer.Slice(1 + i * 5);
                if (q[0] != map[start + i]) return false;
                int hi = Le16(q.Slice(1)), lo = Le16(q.Slice(3));
                if (hi > MaxAdc || lo > MaxAdc) return false;
                next[i] = hi > lo + 16 ? new Range(lo, hi) : default;
            }
            for (int i = 1 + count * 5; i < ReportLength; i++)
                if (answer[i] != 0) return false;
            next.CopyTo(ranges.AsSpan(start, count));
            return true;
        }

        /// <summary>
        /// Reads a D7 answer for <paramref name="bank"/> into
        /// <paramref name="values"/>, all or nothing
        /// (steelseries_apex_protocol.h:53-59). Bytes 1-28 hold 14 raw ADC
        /// samples and bytes 29-56 the 14 filtered samples, little-endian,
        /// all 28 at most 4095. Bytes 57-64 must be zero. The filtered
        /// samples are the ones kept, for sensors (bank - 1) * 14 on.
        /// </summary>
        public static bool ParseDepth(ReadOnlySpan<byte> answer, int bank, ushort[] values)
        {
            if (answer.Length < ReportLength || answer[0] != 0 || bank < 1 || bank > Banks) return false;
            for (int i = 57; i < ReportLength; i++)
                if (answer[i] != 0) return false;
            for (int i = 0; i < 2 * SensorsPerBank; i++)
                if (Le16(answer.Slice(1 + 2 * i)) > MaxAdc) return false;
            for (int i = 0; i < SensorsPerBank; i++)
                values[(bank - 1) * SensorsPerBank + i] = (ushort)Le16(answer.Slice(29 + 2 * i));
            return true;
        }

        /// <summary>A filtered sample as HallJoy's 0..1000 between the
        /// firmware's calibration minimum and maximum
        /// (steelseries_apex_protocol.h:60-64): a normalized sensor response,
        /// not millimeters.</summary>
        public static int Normalize(int raw, Range range)
        {
            if (range.High <= range.Low || raw <= range.Low) return 0;
            if (raw >= range.High) return 1000;
            int span = range.High - range.Low;
            return ((raw - range.Low) * 1000 + span / 2) / span;
        }
    }

    /// <summary>
    /// One Apex Pro read the way HallJoy's steelseries_apex route reads it
    /// (steelseries_apex_backend.cpp:132-192). The handshake asks for the
    /// firmware version and sends nothing else unless it is 4.16.8, reads the
    /// calibration of sensors 0 to 67 in six DA requests and requires every
    /// sensor of the main block (14 to 39) to carry one, binds the calibrated
    /// sensors except the OEM code, and proves the path with one complete
    /// five-bank frame within 100 ms. A pass re-reads the calibration when 2
    /// seconds have passed since the last read, then reads the five D7 banks.
    ///
    /// <para>Any failed exchange or rejected answer ends the session before
    /// another request goes out, because a late answer without an echo
    /// would be taken for the next bank's
    /// (STEELSERIES_APEX_PRO_IMPLEMENTATION_2026-09-25.md:58-59). A frame
    /// slower than 100 ms, the calibration refresh included, and a change in
    /// which sensors are calibrated end it too
    /// (steelseries_apex_backend.cpp:163-178). So <see cref="MissLimit"/> is
    /// 1. Nothing is sent at the end.</para>
    /// </summary>
    public sealed class SteelSeriesApexSession : AnalogKeyboardSession
    {
        /// <summary>One deadline for a request's write and its answer
        /// (steelseries_apex_backend.cpp:104).</summary>
        public const int ExchangeMs = 100;

        /// <summary>Longest frame before the session ends
        /// (steelseries_apex_backend.cpp:178).</summary>
        public const int FrameMs = 100;

        /// <summary>How often the calibration is read again
        /// (steelseries_apex_backend.cpp:165).</summary>
        public const int RangeRefreshMs = 2000;

        private readonly int[] _map;
        private SteelSeriesApexProtocol.Range[] _ranges = new SteelSeriesApexProtocol.Range[SteelSeriesApexProtocol.Sensors];
        private readonly bool[] _bound = new bool[SteelSeriesApexProtocol.AnalogSensors];
        private readonly ushort[] _values = new ushort[SteelSeriesApexProtocol.Sensors];
        private long _refreshed;

        /// <summary>The millisecond clock deadlines and the refresh interval
        /// are measured on, GetTickCount64 as in HallJoy. Tests substitute a
        /// scripted one.</summary>
        internal Func<long> Clock = () => Environment.TickCount64;

        public SteelSeriesApexSession(ushort productId)
        {
            ProductId = productId;
            _map = SteelSeriesApexProtocol.Map;
        }

        public ushort ProductId { get; }

        public override string ModelName => SteelSeriesApexProtocol.ModelName(ProductId);

        /// <summary>The keys the handshake bound, in sensor order: the
        /// calibrated sensors, the ones HallJoy publishes.</summary>
        public override int[] KeyOrder
        {
            get
            {
                var table = new int[SteelSeriesApexProtocol.Sensors];
                for (int i = 0; i < _bound.Length; i++)
                    if (_bound[i]) table[i] = _map[i];
                return AnalogKeyboardData.KeysOf(table);
            }
        }

        public override int MissLimit => 1;

        /// <summary>The calibration the session holds for a sensor.</summary>
        public SteelSeriesApexProtocol.Range RangeAt(int sensor) => _ranges[sensor];

        public override bool Start(IAnalogKeyboardTransport io)
        {
            // steelseries_apex_backend.cpp:144-149: the version first, and no
            // other command for any firmware but 4.16.8. The identity recheck
            // on the opened handle (lines 137-141) has no counterpart on the
            // transport: the collection was matched on the same path.
            if (Exchange(io, SteelSeriesApexProtocol.VersionRequest()) != AnalogPollResult.Ok) return false;
            if (!SteelSeriesApexProtocol.KnownVersion(Buffer.AsSpan(0, SteelSeriesApexProtocol.ReportLength)))
                return false;

            // steelseries_apex_backend.cpp:150-157: calibration, then bind
            // every calibrated sensor except the OEM code.
            var ranges = new SteelSeriesApexProtocol.Range[SteelSeriesApexProtocol.Sensors];
            if (ReadRanges(io, ranges) != AnalogPollResult.Ok) return false;
            _ranges = ranges;
            for (int i = 0; i < SteelSeriesApexProtocol.AnalogSensors; i++)
                _bound[i] = !ranges[i].IsEmpty && _map[i] != SteelSeriesApexProtocol.OemCode;
            _refreshed = Clock();

            // steelseries_apex_backend.cpp:178-184: one complete frame within
            // 100 ms before the collection is claimed. Its values are not
            // kept: the first pass reads a fresh frame.
            long frameStart = Clock();
            if (ReadBanks(io) != AnalogPollResult.Ok) return false;
            return Clock() - frameStart <= FrameMs;
        }

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            long frameStart = Clock();

            // steelseries_apex_backend.cpp:163-171: the firmware's learned
            // extrema, every 2 s. A sensor that gains or loses its
            // calibration needs a new handshake, not a silent new binding.
            if (frameStart - _refreshed >= RangeRefreshMs)
            {
                var next = new SteelSeriesApexProtocol.Range[SteelSeriesApexProtocol.Sensors];
                var refresh = ReadRanges(io, next);
                if (refresh != AnalogPollResult.Ok) return refresh;
                for (int i = 0; i < SteelSeriesApexProtocol.AnalogSensors; i++)
                    if (_ranges[i].IsEmpty != next[i].IsEmpty) return AnalogPollResult.Failed;
                _ranges = next;
                _refreshed = Clock();
            }

            // steelseries_apex_backend.cpp:172-178: the five banks, all within
            // 100 ms of the frame's start, the refresh included.
            var result = ReadBanks(io);
            if (result != AnalogPollResult.Ok) return result;
            if (Clock() - frameStart > FrameMs) return AnalogPollResult.NoAnswer;

            // steelseries_apex_backend.cpp:185-188: the bound sensors. A code
            // on two sensors would read as the deeper one, HallJoy's
            // Publication::Read. Sensors 0 to 67 carry each code once.
            long now = Clock();
            output.ResetForReuse();
            for (int i = 0; i < SteelSeriesApexProtocol.AnalogSensors; i++)
            {
                if (!_bound[i]) continue;
                int milli = SteelSeriesApexProtocol.Normalize(_values[i], _ranges[i]);
                int code = _map[i];
                if (milli > 0 && milli / 1000f > output.Get(code)) output.Set(code, milli / 1000f);
            }
            if (now == frameStart) Thread.Sleep(1);
            return AnalogPollResult.Ok;
        }

        /// <summary>The six DA requests (starts 0, 12, 24, 36, 48 with 12
        /// sensors, 60 with 8), then the main-block check
        /// (steelseries_apex_backend.cpp:116-131). A rejected answer or a
        /// missing main-block calibration is Failed.</summary>
        private AnalogPollResult ReadRanges(IAnalogKeyboardTransport io, SteelSeriesApexProtocol.Range[] ranges)
        {
            for (int start = 0; start < SteelSeriesApexProtocol.AnalogSensors; start += SteelSeriesApexProtocol.RangesPerRequest)
            {
                int count = Math.Min(SteelSeriesApexProtocol.RangesPerRequest, SteelSeriesApexProtocol.AnalogSensors - start);
                var result = Exchange(io, SteelSeriesApexProtocol.RangeRequest(start, count));
                if (result != AnalogPollResult.Ok) return result;
                if (!SteelSeriesApexProtocol.ParseRanges(Buffer.AsSpan(0, SteelSeriesApexProtocol.ReportLength), start, count, ranges))
                    return AnalogPollResult.Failed;
            }
            for (int i = SteelSeriesApexProtocol.FirstRequiredSensor; i <= SteelSeriesApexProtocol.LastRequiredSensor; i++)
                if (ranges[i].IsEmpty) return AnalogPollResult.Failed;
            return AnalogPollResult.Ok;
        }

        /// <summary>D7 banks 1 to 5 into the sample array
        /// (steelseries_apex_backend.cpp:172-177).</summary>
        private AnalogPollResult ReadBanks(IAnalogKeyboardTransport io)
        {
            for (int bank = 1; bank <= SteelSeriesApexProtocol.Banks; bank++)
            {
                var result = Exchange(io, SteelSeriesApexProtocol.DepthRequest(bank));
                if (result != AnalogPollResult.Ok) return result;
                if (!SteelSeriesApexProtocol.ParseDepth(Buffer.AsSpan(0, SteelSeriesApexProtocol.ReportLength), bank, _values))
                    return AnalogPollResult.Failed;
            }
            return AnalogPollResult.Ok;
        }

        /// <summary>
        /// One request and its one answer, HallJoy's Exchange
        /// (steelseries_apex_backend.cpp:85-115): the request must pass the
        /// read-only allowlist, then one 65-byte write and one 65-byte read
        /// whose report ID is 0, both within one 100 ms deadline. A write
        /// that outlasts the deadline fails the exchange, as HallJoy cancels
        /// it. Ok leaves the answer in <see cref="AnalogKeyboardSession.Buffer"/>.
        /// NoAnswer is a timeout, Failed anything else.
        /// </summary>
        private AnalogPollResult Exchange(IAnalogKeyboardTransport io, byte[] request)
        {
            if (!SteelSeriesApexProtocol.ReadOnlyRequest(request)) return AnalogPollResult.Failed;
            long deadline = Clock() + ExchangeMs;
            if (!io.Send(request)) return AnalogPollResult.Failed;
            long left = deadline - Clock();
            if (left <= 0) return AnalogPollResult.NoAnswer;
            int n = io.Receive(Buffer, (int)left);
            if (n < 0) return AnalogPollResult.Failed;
            if (n == 0) return AnalogPollResult.NoAnswer;
            if (n != SteelSeriesApexProtocol.ReportLength || Buffer[0] != 0) return AnalogPollResult.Failed;
            return AnalogPollResult.Ok;
        }
    }
}
