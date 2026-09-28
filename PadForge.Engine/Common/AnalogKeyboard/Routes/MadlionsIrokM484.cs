using System;
using System.Text;
using System.Threading;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>The fields of an M484 identity reply HallJoy keeps
    /// (irok_nd75_protocol.h:24-29).</summary>
    public sealed class M484Identity
    {
        public M484Identity(string controller, string product, string firmware)
        {
            Controller = controller;
            Product = product;
            Firmware = firmware;
        }

        /// <summary>Field 0, "M484".</summary>
        public string Controller { get; }
        /// <summary>Field 4, the board, for example "GK8260HERGB".</summary>
        public string Product { get; }
        /// <summary>Field 5 when present, else empty.</summary>
        public string Firmware { get; }
    }

    /// <summary>What an IROK M484 session reads.</summary>
    public enum IrokM484Mode
    {
        None,
        /// <summary>IROK NA87 Mag: subscribed depth events, 0 to 40.</summary>
        Na87Events,
        /// <summary>AJAZZ AK820 MAX RGB: raw ADC rows, normalized per key.</summary>
        AjazzRawRows,
    }

    /// <summary>
    /// The IROK M484 transport (irok_nd75_protocol.h, irok_nd75_protocol.cpp,
    /// irok_na87_protocol.h, irok_na87_backend.cpp): 64-byte reports with
    /// report ID 1 on the vendor collection FF1B:0091 of 0416:7372. The
    /// identity request (0x0D) answers a comma-separated firmware string, the
    /// capability request (0x21, channel 0x18, subcommand 4) answers the
    /// nominal travel maximum, 0x10 answers the six-row key map, and 0x21
    /// subcommands 2 and 3 subscribe to and unsubscribe from depth events.
    /// The AJAZZ AK820 MAX RGB's 0x23 switches raw ADC rows on and off.
    /// </summary>
    public static class IrokM484Protocol
    {
        public const ushort VendorId = 0x0416;
        public const ushort ProductId = 0x7372;
        public const ushort UsagePage = 0xFF1B;
        public const ushort Usage = 0x0091;
        public const int ReportBytes = 64;
        public const int Rows = 6;
        public const int Columns = 22;
        public const int MatrixSlots = Rows * Columns;
        public const byte ReportId = 1;
        public const byte IdentityCommand = 0x0D;
        /// <summary>The wire opcode. The vendor SDK's 0x29 is its own alias
        /// and is never sent (irok_nd75_protocol.h:15-17).</summary>
        public const byte WireAnalogCommand = 0x21;
        public const byte AnalogChannel = 0x18;
        public const byte MapCommand = 0x10;
        public const byte RawRowCommand = 0x23;
        public const int NominalTravelMaximum = 40;

        /// <summary>The M484 maps' Fn pseudo-usage, which HallJoy keeps out of
        /// the HID usages on purpose (irok_na87_factory.h:12,
        /// irok_nd75_protocol.cpp:75-77).</summary>
        public const int FnPseudoUsage = 0xFA;

        /// <summary>The descriptor's name for the NA87 (irok_na87_backend.cpp:1098).</summary>
        public const string Na87ModelName = "IROK NA87 Mag";

        /// <summary>The name HallJoy reports for the AJAZZ (irok_na87_backend.cpp:1063).</summary>
        public const string AjazzModelName = "AJAZZ AK820 MAX RGB";

        private static readonly Lazy<int[]> _factory =
            new(() => AnalogKeyboardData.Table(MadlionsRoutes.DataFile, "irokNa87FactoryKeys"));

        /// <summary>HallJoy's metadata test (Enumerate, irok_na87_backend.cpp:226-238):
        /// VID and PID, usage FF1B:0091, and 64-byte input and output reports.
        /// Strings and the interface number are not checked.</summary>
        public static bool Matches(AnalogKeyboardDeviceInfo info)
            => info != null
               && info.VendorId == VendorId
               && info.ProductId == ProductId
               && info.UsagePage == UsagePage
               && info.Usage == Usage
               && info.InputReportLength == ReportBytes
               && info.OutputReportLength == ReportBytes;

        /// <summary>The NA87 factory map by position (row * 22 + column), in
        /// HallJoy's own codes, Fn as 0xFA (irok_na87_factory.h:5-18).</summary>
        public static int[] FactoryMap() => (int[])_factory.Value.Clone();

        /// <summary>
        /// A map code in the shared code space. HallJoy's AJAZZ path publishes
        /// the M484 Fn pseudo-usage 0xFA as its Fn code 0x409 and reads either
        /// as that key (HostCode, ajazz_raw_limits.h:10, irok_na87_backend.cpp:1046, 1054).
        /// Its NA87 path publishes 0xFA as is. Both modes use this translation,
        /// since the shared code space has one Fn, 0x409.
        /// </summary>
        public static int HostCode(int code) => code == FnPseudoUsage ? AnalogKeyCodes.Fn : code;

        /// <summary>The NA87 factory keys in the shared code space, position order.</summary>
        public static int[] FactoryKeyOrder()
        {
            var map = FactoryMap();
            for (int i = 0; i < map.Length; i++) map[i] = HostCode(map[i]);
            return AnalogKeyboardData.KeysOf(map);
        }

        private static byte[] Base(byte command)
        {
            var report = new byte[ReportBytes];
            report[0] = ReportId;
            report[1] = command;
            return report;
        }

        /// <summary>01 0D (irok_nd75_protocol.cpp:42-45).</summary>
        public static byte[] IdentityRequest() => Base(IdentityCommand);

        /// <summary>01 21 00 00 00 18 04 (irok_nd75_protocol.cpp:47-53).</summary>
        public static byte[] CapabilityRequest()
        {
            var report = Base(WireAnalogCommand);
            report[5] = AnalogChannel;
            report[6] = 0x04;
            return report;
        }

        /// <summary>01 21 00 00 00 18 02 and 22 column masks from byte 7
        /// (irok_nd75_protocol.cpp:55-63).</summary>
        public static byte[] SubscriptionRequest(byte[] mask)
        {
            var report = Base(WireAnalogCommand);
            report[5] = AnalogChannel;
            report[6] = 0x02;
            Array.Copy(mask, 0, report, 7, Columns);
            return report;
        }

        /// <summary>01 21 00 00 00 18 03 (irok_nd75_protocol.cpp:65-71).</summary>
        public static byte[] UnsubscribeRequest()
        {
            var report = Base(WireAnalogCommand);
            report[5] = AnalogChannel;
            report[6] = 0x03;
            return report;
        }

        /// <summary>01 10 (irok_na87_protocol.h:14).</summary>
        public static byte[] MapRequest() => Base(MapCommand);

        /// <summary>01 23 00 00 00 00 01 (irok_na87_backend.cpp:849).</summary>
        public static byte[] RawRowsOnRequest()
        {
            var report = Base(RawRowCommand);
            report[6] = 1;
            return report;
        }

        /// <summary>01 23 (irok_na87_backend.cpp:845).</summary>
        public static byte[] RawRowsOffRequest() => Base(RawRowCommand);

        /// <summary>Bit <c>row</c> of <c>mask[column]</c> for every position
        /// the map assigns (SubscriptionMask, irok_nd75_protocol.cpp:94-103).</summary>
        public static byte[] SubscriptionMask(int[] map)
        {
            var mask = new byte[Columns];
            for (int position = 0; position < map.Length && position < MatrixSlots; position++)
                if (map[position] != 0)
                    mask[position % Columns] |= (byte)(1 << (position / Columns));
            return mask;
        }

        private static bool IsResponse(ReadOnlySpan<byte> r, byte command)
            => r.Length >= ReportBytes && r[0] == ReportId && r[1] == command;

        private static bool StoreField(ReadOnlySpan<byte> r, int begin, int end, int capacity, out string value)
        {
            value = null;
            if (end < begin || end - begin + 1 > capacity) return false;
            for (int i = begin; i < end; i++)
                if (r[i] < 0x20 || r[i] > 0x7E) return false;
            if (end == begin) return false;
            value = Encoding.ASCII.GetString(r.Slice(begin, end - begin));
            return true;
        }

        /// <summary>
        /// The identity reply (DecodeDeviceInfo, irok_nd75_protocol.cpp:105-137):
        /// 01 0D 00, byte 4 zero, 5 &lt; byte 5 &lt; 64, then ASCII from byte 6
        /// up to byte 5 inclusive, cut at the first NUL and split on commas,
        /// at least five and at most six fields. Field 0 is the controller (at
        /// most 15 characters), field 4 the product (at most 31) and field 5,
        /// when present and not empty, the firmware (at most 23). Every kept
        /// character is printable ASCII.
        /// </summary>
        public static bool DecodeDeviceInfo(ReadOnlySpan<byte> r, out M484Identity identity)
        {
            identity = null;
            if (!IsResponse(r, IdentityCommand) || r[2] != 0 || r[4] != 0 || r[5] <= 5 || r[5] >= ReportBytes)
                return false;
            const int begin = 6;
            int end = r[5] + 1;
            if (end > r.Length || end <= begin) return false;

            Span<int> starts = stackalloc int[6];
            Span<int> stops = stackalloc int[6];
            int field = 0;
            int fieldBegin = begin;
            for (int index = begin; index <= end; index++)
            {
                bool terminal = index == end || r[index] == 0;
                if (!terminal && r[index] != (byte)',') continue;
                if (field >= starts.Length) return false;
                starts[field] = fieldBegin;
                stops[field] = index;
                field++;
                fieldBegin = index + 1;
                if (terminal) break;
            }
            if (field < 5) return false;
            if (!StoreField(r, starts[0], stops[0], 16, out string controller)
                || !StoreField(r, starts[4], stops[4], 32, out string product))
                return false;
            string firmware = string.Empty;
            if (field >= 6 && starts[5] != stops[5] && !StoreField(r, starts[5], stops[5], 24, out firmware))
                return false;
            identity = new M484Identity(controller, product, firmware);
            return true;
        }

        /// <summary>M484 / GK8260HERGB, any firmware (irok_na87_protocol.h:10-13).</summary>
        public static bool IsNa87(M484Identity identity)
            => identity != null && identity.Controller == "M484" && identity.Product == "GK8260HERGB";

        /// <summary>M484 / SG8994HERGB with firmware exactly V1.13.17 once
        /// trailing spaces are stripped (IsAjazzRgb, irok_na87_backend.cpp:344-353).</summary>
        public static bool IsAjazzRgb(M484Identity identity)
            => identity != null && identity.Controller == "M484" && identity.Product == "SG8994HERGB"
               && (identity.Firmware ?? string.Empty).TrimEnd(' ') == "V1.13.17";

        /// <summary>The capability reply (DecodeCapabilityInfo,
        /// irok_nd75_protocol.cpp:150-159): 01 21, byte 5 at least 2, byte 6
        /// = 04, and the nominal travel maximum in byte 7, at most 40.</summary>
        public static bool DecodeCapabilityInfo(ReadOnlySpan<byte> r, out int sensitivity)
        {
            sensitivity = 0;
            if (!IsResponse(r, WireAnalogCommand) || r[5] < 2 || r[6] != 0x04) return false;
            sensitivity = r[7];
            return sensitivity <= NominalTravelMaximum;
        }

        /// <summary>A live event (DecodeLiveEvent, irok_nd75_protocol.cpp:161-175):
        /// 01 21, byte 5 at least 3, byte 6 = 01, then row (0 to 5), column (0
        /// to 21) and travel.</summary>
        public static bool DecodeLiveEvent(ReadOnlySpan<byte> r, out int row, out int column, out int travel)
        {
            row = column = travel = 0;
            if (!IsResponse(r, WireAnalogCommand) || r[5] < 3 || r[6] != 0x01) return false;
            if (r[7] >= Rows || r[8] >= Columns) return false;
            row = r[7];
            column = r[8];
            travel = r[9];
            return true;
        }

        /// <summary>The NA87 path's stricter event shape: bytes 2 to 4 zero and
        /// byte 5 exactly 3 (irok_na87_backend.cpp:592-594).</summary>
        public static bool IsNa87EventShape(ReadOnlySpan<byte> r)
            => r.Length >= ReportBytes && r[2] == 0 && r[3] == 0 && r[4] == 0 && r[5] == 3;

        /// <summary>0 to 40 onto 0 to 1000 in steps of 25, rounded
        /// (ToMilli, irok_nd75_protocol.cpp:193-198).</summary>
        public static int ToMilli(int travel)
            => Math.Min(1000, (travel * 1000 + NominalTravelMaximum / 2) / NominalTravelMaximum);

        /// <summary>False for a travel above 40, which changes nothing
        /// (TryTravelToMilli, irok_nd75_protocol.cpp:185-191).</summary>
        public static bool TryTravelToMilli(int travel, out int milli)
        {
            milli = 0;
            if (travel < 0 || travel > NominalTravelMaximum) return false;
            milli = ToMilli(travel);
            return true;
        }

        /// <summary>An AJAZZ raw row (IsAjazzRawRow, irok_na87_backend.cpp:777-781):
        /// 01 23 00 00, the row plus one (1 to 6) in byte 4 and 44 in byte 5.</summary>
        public static bool IsAjazzRawRow(ReadOnlySpan<byte> r)
            => r.Length >= ReportBytes && r[0] == ReportId && r[1] == RawRowCommand && r[2] == 0 && r[3] == 0
               && r[4] >= 1 && r[4] <= 6 && r[5] == 44;
    }

    /// <summary>
    /// The six-row key map reply reader (MapReader, irok_na87_protocol.h:17-47).
    /// Rows 0 to 4 carry 00 01 to 00 05 in bytes 3 and 4 and the last row FF
    /// FF, byte 5 is 22 and bytes 6 to 27 the row's codes. A repeated row
    /// with different content is a conflict. The map is complete once all
    /// six rows arrived without a conflict, every code is 0x04 to 0xE7 or
    /// 0xFA, no code repeats unless duplicates are allowed, and 60 to 110
    /// positions are assigned.
    /// </summary>
    public sealed class IrokM484MapReader
    {
        private readonly int[] _map = new int[IrokM484Protocol.MatrixSlots];
        private readonly bool _allowDuplicateHids;
        private int _rows;
        private bool _conflict;

        public IrokM484MapReader(bool allowDuplicateHids = false)
        {
            _allowDuplicateHids = allowDuplicateHids;
        }

        public bool Feed(ReadOnlySpan<byte> r)
        {
            if (r.Length < IrokM484Protocol.ReportBytes || r[0] != 1 || r[1] != IrokM484Protocol.MapCommand
                || r[2] != 0 || r[5] != 22)
                return false;
            int row = 6;
            if (r[3] == 0 && r[4] >= 1 && r[4] <= 5) row = r[4] - 1;
            else if (r[3] == 255 && r[4] == 255) row = 5;
            if (row >= 6) return false;
            int begin = row * IrokM484Protocol.Columns;
            if ((_rows & (1 << row)) != 0)
            {
                for (int c = 0; c < IrokM484Protocol.Columns; c++)
                    if (_map[begin + c] != r[6 + c]) _conflict = true;
            }
            for (int c = 0; c < IrokM484Protocol.Columns; c++) _map[begin + c] = r[6 + c];
            _rows |= 1 << row;
            return true;
        }

        public bool Complete()
        {
            if (_rows != 63 || _conflict) return false;
            var seen = new bool[256];
            int count = 0;
            foreach (int code in _map)
            {
                if (code == 0) continue;
                if (code < 4 || (code > 0xE7 && code != IrokM484Protocol.FnPseudoUsage)
                    || (!_allowDuplicateHids && seen[code]))
                    return false;
                seen[code] = true;
                count++;
            }
            return count >= 60 && count <= 110;
        }

        /// <summary>The map by position, in the keyboard's own codes.</summary>
        public int[] Map() => (int[])_map.Clone();
    }

    /// <summary>
    /// The AJAZZ AK820 MAX RGB's per-key range learning (DynamicLimits,
    /// ajazz_raw_limits.h:14-67). The ADC falls as a key goes down. Each
    /// physical position starts released at its first sample, with a full
    /// press seeded from one tester keyboard's observed minima, and widens its
    /// own range from the median of its last three samples. A span of 32
    /// counts or less reads as released. The output is linear in ADC counts,
    /// explicitly not calibrated millimeters (ajazz_raw_limits.h:12-13).
    /// </summary>
    public sealed class AjazzRawLimits
    {
        private struct Key
        {
            public int R0, R1, R2;
            public int Samples, Next, Rest, Full;
        }

        private readonly Key[] _keys = new Key[IrokM484Protocol.MatrixSlots];
        private static readonly Lazy<int[]> _sensorRows =
            new(() => AnalogKeyboardData.Table(MadlionsRoutes.DataFile, "ajazzAk820SensorRows"));
        private static readonly Lazy<int[]> _fullSeeds =
            new(() => AnalogKeyboardData.Table(MadlionsRoutes.DataFile, "ajazzAk820FullSeeds"));

        /// <summary>The 82 positions the SG8994HERGB V1.13.17 has sensors at,
        /// row bitmasks 0xFFBD, 0xDFFF, 0xDFFF, 0xDFFD, 0xEFFD and 0xFC47
        /// (Physical, ajazz_raw_limits.h:8-9).</summary>
        public static bool Physical(int position)
            => position >= 0 && position < IrokM484Protocol.MatrixSlots
               && (_sensorRows.Value[position / IrokM484Protocol.Columns] & (1 << (position % IrokM484Protocol.Columns))) != 0;

        /// <summary>The seed for a position's full press (InitialFull,
        /// ajazz_raw_limits.h:20-38).</summary>
        public static int InitialFull(int position) => _fullSeeds.Value[position];

        /// <summary>Feeds one raw sample. False, with 0 out, for a
        /// non-physical position, a zero sample, or a span too narrow to
        /// trust (Raw, ajazz_raw_limits.h:41-59).</summary>
        public bool Raw(int position, int raw, out int milli)
        {
            milli = 0;
            if (!Physical(position) || raw == 0 || raw > 65535) return false;
            ref var k = ref _keys[position];
            if (k.Samples == 0)
            {
                k.R0 = k.R1 = k.R2 = raw;
                k.Rest = raw;
                k.Full = InitialFull(position);
            }
            switch (k.Next)
            {
                case 0: k.R0 = raw; break;
                case 1: k.R1 = raw; break;
                default: k.R2 = raw; break;
            }
            k.Next = (k.Next + 1) % 3;
            k.Samples++;
            int v = Median(k.R0, k.R1, k.R2);
            if (v > k.Rest) k.Rest = v;
            if (v < k.Full) k.Full = v;
            // Startup with a held key must not amplify a tiny noise-only span.
            if (k.Rest <= k.Full + 32) return false;
            milli = v >= k.Rest ? 0 : v <= k.Full ? 1000 : (k.Rest - v) * 1000 / (k.Rest - k.Full);
            return true;
        }

        private static int Median(int a, int b, int c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
    }

    /// <summary>
    /// One IROK M484 session. <see cref="Start"/> is HallJoy's proof (Prove,
    /// irok_na87_backend.cpp:402-415): identity, capability 40 and a complete
    /// key map, each within 1200 ms read in 100 ms slices. The identity picks
    /// the mode, as HallJoy's worker does (irok_na87_backend.cpp:884-907).
    ///
    /// <para>IROK NA87 Mag (M484 / GK8260HERGB): unsubscribe, then subscribe to
    /// every position the device map or the factory map assigns (Run,
    /// irok_na87_backend.cpp:486-724). The keyboard pushes one event per
    /// changed key, travel 0 to 40, and a silent key keeps its value. The
    /// identity request goes out every second, and no NA87 identity reply
    /// for 3 s ends the session. The keys publish under the device's live
    /// map, HallJoy's automatic layout path, on by default and taken when the
    /// NA87 is its only analog keyboard (keyboard_layout.cpp:1787, 1904-1918,
    /// 1981, irok_na87_backend.cpp:1047-1049, 1055-1056).</para>
    ///
    /// <para>AJAZZ AK820 MAX RGB (M484 / SG8994HERGB / V1.13.17): raw rows on
    /// (RunAjazz, irok_na87_backend.cpp:826-876). All six rows must arrive
    /// before anything publishes, each row must refresh within 100 ms and the
    /// first within 1200 ms of the start, and a key needs a sample no older
    /// than 100 ms. Its 82 physical positions publish under the device map,
    /// the deepest position winning when a code repeats.</para>
    /// </summary>
    public sealed class IrokM484Session : AnalogKeyboardSession
    {
        /// <summary>irok_na87_backend.cpp:48.</summary>
        public const int ProofTimeoutMs = 1200;
        /// <summary>irok_na87_backend.cpp:363, 383, 396.</summary>
        public const int ProofReadSliceMs = 100;
        /// <summary>irok_na87_backend.cpp:549.</summary>
        public const int EventReadSliceMs = 100;
        /// <summary>irok_na87_backend.cpp:649.</summary>
        public const int HeartbeatIntervalMs = 1000;
        /// <summary>irok_na87_backend.cpp:653.</summary>
        public const int HeartbeatTimeoutMs = 3000;
        /// <summary>irok_nd75_protocol.cpp:180.</summary>
        public const int ReadErrorLimit = 3;
        /// <summary>irok_na87_backend.cpp:584.</summary>
        public const int ReadErrorPauseMs = 10;
        /// <summary>irok_na87_backend.cpp:855.</summary>
        public const int AjazzReadSliceMs = 20;
        /// <summary>irok_na87_backend.cpp:872.</summary>
        public const int AjazzRowSilenceMs = 100;
        /// <summary>irok_na87_backend.cpp:872.</summary>
        public const int AjazzFirstRowMs = 1200;
        /// <summary>irok_na87_backend.cpp:1054.</summary>
        public const int AjazzFreshMs = 100;

        private readonly Func<long> _clock;
        private readonly Action<int> _sleep;
        private readonly int[] _positionCode = new int[IrokM484Protocol.MatrixSlots];
        private readonly int[] _positionMilli = new int[IrokM484Protocol.MatrixSlots];
        private readonly long[] _positionStamp = new long[IrokM484Protocol.MatrixSlots];
        private readonly AnalogKeyInputState _scratch = new();
        private IrokM484Mode _mode;
        private string _modelName;
        private int[] _keyOrder;

        // NA87 events.
        private long _lastProofMs;
        private long _lastProbeMs;
        private int _consecutiveReadErrors;
        private bool _transportLost;

        // AJAZZ raw rows.
        private AjazzRawLimits _limits;
        private readonly long[] _rowSeen = new long[IrokM484Protocol.Rows];
        private long _ajazzStarted;
        private bool _ajazzConnected;

        public IrokM484Session(Func<long> clock = null, Action<int> sleep = null)
        {
            _clock = clock ?? (() => Environment.TickCount64);
            _sleep = sleep ?? Thread.Sleep;
        }

        public override string ModelName => _modelName;

        public override int[] KeyOrder => _keyOrder == null ? null : (int[])_keyOrder.Clone();

        /// <summary>The mode the identity picked.</summary>
        public IrokM484Mode Mode => _mode;

        /// <summary>The identity the keyboard reported.</summary>
        public M484Identity Identity { get; private set; }

        /// <summary>True once the NA87 session ended on a lost transport, when
        /// HallJoy skips its closing unsubscribe.</summary>
        public bool TransportLost => _transportLost;

        /// <summary>True once every AJAZZ row has arrived.</summary>
        public bool AjazzConnected => _ajazzConnected;

        public override bool Start(IAnalogKeyboardTransport io)
        {
            if (!ReceiveIdentity(io, out var identity)) return false;
            Identity = identity;
            bool na87 = IrokM484Protocol.IsNa87(identity);
            if (!ReceiveCapability(io, out int sensitivity) || sensitivity != IrokM484Protocol.NominalTravelMaximum)
                return false;
            // Duplicate codes are allowed only for the AJAZZ (irok_na87_backend.cpp:409-410, 832).
            if (!ReceiveMap(io, !na87, out int[] map)) return false;
            return na87 ? StartNa87(io, map) : StartAjazz(io, map);
        }

        /// <summary>Unsubscribe, then subscribe with mask(device map) OR
        /// mask(factory map) (irok_na87_backend.cpp:506-517). When the
        /// subscribe write fails, HallJoy returns without undoing it. It may
        /// still have reached the keyboard, so the unsubscribe goes out here
        /// before the route gives up.</summary>
        private bool StartNa87(IAnalogKeyboardTransport io, int[] map)
        {
            for (int p = 0; p < IrokM484Protocol.MatrixSlots; p++)
                _positionCode[p] = IrokM484Protocol.HostCode(map[p]);
            _keyOrder = AnalogKeyboardData.KeysOf(_positionCode);

            var mask = IrokM484Protocol.SubscriptionMask(map);
            var factoryMask = IrokM484Protocol.SubscriptionMask(IrokM484Protocol.FactoryMap());
            for (int c = 0; c < mask.Length; c++) mask[c] |= factoryMask[c];

            if (!io.Send(IrokM484Protocol.UnsubscribeRequest())) return false;
            if (!io.Send(IrokM484Protocol.SubscriptionRequest(mask)))
            {
                io.Send(IrokM484Protocol.UnsubscribeRequest());
                return false;
            }
            _mode = IrokM484Mode.Na87Events;
            _modelName = IrokM484Protocol.Na87ModelName;
            _lastProofMs = _lastProbeMs = _clock();
            return true;
        }

        /// <summary>At least one physical position must carry a code, then
        /// raw rows on. HallJoy's cleanup guard sends raw rows off on every
        /// exit once the map is bound, a failed enable included
        /// (irok_na87_backend.cpp:834-850).</summary>
        private bool StartAjazz(IAnalogKeyboardTransport io, int[] map)
        {
            int mapped = 0;
            for (int p = 0; p < IrokM484Protocol.MatrixSlots; p++)
                if (AjazzRawLimits.Physical(p) && map[p] != 0) mapped++;
            if (mapped == 0) return false;

            for (int p = 0; p < IrokM484Protocol.MatrixSlots; p++)
                _positionCode[p] = AjazzRawLimits.Physical(p) ? IrokM484Protocol.HostCode(map[p]) : 0;
            _keyOrder = AnalogKeyboardData.KeysOf(_positionCode);
            _limits = new AjazzRawLimits();

            if (!io.Send(IrokM484Protocol.RawRowsOnRequest()))
            {
                io.Send(IrokM484Protocol.RawRowsOffRequest());
                return false;
            }
            _mode = IrokM484Mode.AjazzRawRows;
            _modelName = IrokM484Protocol.AjazzModelName;
            _ajazzStarted = _clock();
            return true;
        }

        /// <summary>One read of a 64-byte report. Returns the byte count, 0
        /// for a slice that timed out, -1 when the device is gone.</summary>
        private int Read(IAnalogKeyboardTransport io, int timeoutMs) => io.Receive(Buffer, Math.Max(1, timeoutMs));

        /// <summary>ReceiveIdentity (irok_na87_backend.cpp:355-373): the first
        /// identity reply decides, and it must name the NA87 or the AJAZZ.</summary>
        private bool ReceiveIdentity(IAnalogKeyboardTransport io, out M484Identity identity)
        {
            identity = null;
            if (!io.Send(IrokM484Protocol.IdentityRequest())) return false;
            long deadline = _clock() + ProofTimeoutMs;
            while (_clock() < deadline)
            {
                int n = Read(io, ProofReadSliceMs);
                if (n < 0) return false;
                if (n != IrokM484Protocol.ReportBytes) continue;
                if (!IrokM484Protocol.DecodeDeviceInfo(Buffer.AsSpan(0, n), out identity)) continue;
                return IrokM484Protocol.IsNa87(identity) || IrokM484Protocol.IsAjazzRgb(identity);
            }
            return false;
        }

        /// <summary>ReceiveCapability (irok_na87_backend.cpp:375-387).</summary>
        private bool ReceiveCapability(IAnalogKeyboardTransport io, out int sensitivity)
        {
            sensitivity = 0;
            if (!io.Send(IrokM484Protocol.CapabilityRequest())) return false;
            long deadline = _clock() + ProofTimeoutMs;
            while (_clock() < deadline)
            {
                int n = Read(io, ProofReadSliceMs);
                if (n < 0) return false;
                if (n != IrokM484Protocol.ReportBytes) continue;
                if (IrokM484Protocol.DecodeCapabilityInfo(Buffer.AsSpan(0, n), out sensitivity)) return true;
            }
            return false;
        }

        /// <summary>ReceiveMap (irok_na87_backend.cpp:389-400): feeds every
        /// report to the reader and returns as soon as the map is complete.</summary>
        private bool ReceiveMap(IAnalogKeyboardTransport io, bool allowDuplicateHids, out int[] map)
        {
            map = null;
            if (!io.Send(IrokM484Protocol.MapRequest())) return false;
            var reader = new IrokM484MapReader(allowDuplicateHids);
            long deadline = _clock() + ProofTimeoutMs;
            while (_clock() < deadline)
            {
                int n = Read(io, ProofReadSliceMs);
                if (n < 0) return false;
                if (n == IrokM484Protocol.ReportBytes) reader.Feed(Buffer.AsSpan(0, n));
                if (reader.Complete())
                {
                    map = reader.Map();
                    return true;
                }
            }
            return false;
        }

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            return _mode switch
            {
                IrokM484Mode.Na87Events => Na87Pass(io, output),
                IrokM484Mode.AjazzRawRows => AjazzPass(io, output),
                _ => AnalogPollResult.Failed,
            };
        }

        /// <summary>
        /// One iteration of Run's loop (irok_na87_backend.cpp:546-656). A read
        /// that times out is ordinary. A lost device, or three failed reads in
        /// a row, ends the session, and any other failed read waits 10 ms. A
        /// report of the wrong length fails HallJoy's read the same way.
        /// </summary>
        private AnalogPollResult Na87Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output)
        {
            int n = Read(io, EventReadSliceMs);
            bool received = n == IrokM484Protocol.ReportBytes;
            if (!received)
            {
                if (n == 0)
                {
                    _consecutiveReadErrors = 0;
                }
                else
                {
                    _consecutiveReadErrors++;
                    if (n < 0 || _consecutiveReadErrors >= ReadErrorLimit)
                    {
                        _transportLost = true;
                        return AnalogPollResult.Failed;
                    }
                    _sleep(ReadErrorPauseMs);
                }
            }
            else
            {
                _consecutiveReadErrors = 0;
            }

            long now = _clock();
            var report = Buffer.AsSpan(0, Math.Max(0, n));
            if (received && IrokM484Protocol.IsNa87EventShape(report)
                && IrokM484Protocol.DecodeLiveEvent(report, out int row, out int column, out int travel)
                && IrokM484Protocol.TryTravelToMilli(travel, out int milli))
            {
                int position = row * IrokM484Protocol.Columns + column;
                _positionMilli[position] = milli;
                _positionStamp[position] = now;
            }

            if (received && IrokM484Protocol.DecodeDeviceInfo(report, out var heartbeat)
                && IrokM484Protocol.IsNa87(heartbeat))
                _lastProofMs = now;
            if (now - _lastProbeMs >= HeartbeatIntervalMs)
            {
                if (!io.Send(IrokM484Protocol.IdentityRequest()))
                {
                    _transportLost = true;
                    return AnalogPollResult.Failed;
                }
                _lastProbeMs = now;
            }
            if (now - _lastProofMs >= HeartbeatTimeoutMs)
            {
                _transportLost = true;
                return AnalogPollResult.Failed;
            }
            return Publish(output, now, long.MaxValue) ? AnalogPollResult.Ok : AnalogPollResult.Idle;
        }

        /// <summary>One iteration of RunAjazz's loop (irok_na87_backend.cpp:853-874).
        /// Any read failure but a timeout ends the session, as does a row
        /// silent for more than 100 ms or a row missing 1200 ms after the
        /// start.</summary>
        private AnalogPollResult AjazzPass(IAnalogKeyboardTransport io, AnalogKeyInputState output)
        {
            int n = Read(io, AjazzReadSliceMs);
            if (n == IrokM484Protocol.ReportBytes)
            {
                var report = Buffer.AsSpan(0, n);
                if (IrokM484Protocol.IsAjazzRawRow(report))
                {
                    long stamp = _clock();
                    int row = report[4] - 1;
                    PublishRawRow(report, row, stamp);
                    _rowSeen[row] = _clock();
                    if (!_ajazzConnected && Array.TrueForAll(_rowSeen, seen => seen != 0))
                        _ajazzConnected = true;
                }
            }
            else if (n != 0)
            {
                return AnalogPollResult.Failed;
            }

            long now = _clock();
            for (int row = 0; row < IrokM484Protocol.Rows; row++)
            {
                long seen = _rowSeen[row];
                long since = now - (seen != 0 ? seen : _ajazzStarted);
                if (since > (seen != 0 ? AjazzRowSilenceMs : AjazzFirstRowMs))
                {
                    _ajazzConnected = false;
                    return AnalogPollResult.Failed;
                }
            }
            if (!_ajazzConnected)
            {
                bool changed = output.Count != 0;
                output.ResetForReuse();
                return changed ? AnalogPollResult.Ok : AnalogPollResult.Idle;
            }
            return Publish(output, now, AjazzFreshMs) ? AnalogPollResult.Ok : AnalogPollResult.Idle;
        }

        /// <summary>PublishAjazzRawRow (irok_na87_backend.cpp:782-801): every
        /// physical position of the row through the range learning.</summary>
        private void PublishRawRow(ReadOnlySpan<byte> report, int row, long stamp)
        {
            for (int column = 0; column < IrokM484Protocol.Columns; column++)
            {
                int position = row * IrokM484Protocol.Columns + column;
                if (!AjazzRawLimits.Physical(position)) continue;
                int raw = (report[6 + column * 2] << 8) | report[7 + column * 2];
                _limits.Raw(position, raw, out int milli);
                _positionMilli[position] = milli;
                _positionStamp[position] = stamp;
            }
        }

        /// <summary>Each code at the deepest of the positions bound to it
        /// whose sample is no older than <paramref name="freshMs"/>, HallJoy's
        /// Publication::Read (physical_analog_state.h:41-54). True when the
        /// output changed.</summary>
        private bool Publish(AnalogKeyInputState output, long now, long freshMs)
        {
            _scratch.ResetForReuse();
            for (int p = 0; p < IrokM484Protocol.MatrixSlots; p++)
            {
                int code = _positionCode[p];
                if (code == 0) continue;
                long stamp = _positionStamp[p];
                if (stamp == 0 || now < stamp || now - stamp > freshMs) continue;
                float depth = _positionMilli[p] / 1000f;
                if (depth > _scratch.Get(code)) _scratch.Set(code, depth);
            }
            if (_scratch.SameAs(output)) return false;
            _scratch.CopyInto(output);
            return true;
        }

        /// <summary>NA87: unsubscribe unless the transport was lost
        /// (irok_na87_backend.cpp:721-722). AJAZZ: raw rows off on every exit
        /// (irok_na87_backend.cpp:841-848).</summary>
        public override void Stop(IAnalogKeyboardTransport io)
        {
            if (_mode == IrokM484Mode.Na87Events)
            {
                if (!_transportLost) io.Send(IrokM484Protocol.UnsubscribeRequest());
            }
            else if (_mode == IrokM484Mode.AjazzRawRows)
            {
                io.Send(IrokM484Protocol.RawRowsOffRequest());
                _ajazzConnected = false;
            }
        }
    }
}
