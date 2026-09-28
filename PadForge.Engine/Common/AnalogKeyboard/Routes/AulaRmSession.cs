using System;

namespace PadForge.Engine.Common.AnalogKeyboard.Routes
{
    /// <summary>One board of HallJoy's known RM 6x21 table
    /// (aula_win60he_protocol.h:29-36): the USB identity, the board ID its
    /// sync must return, and the model name.</summary>
    public sealed class AulaRmBoard
    {
        public ushort VendorId { get; init; }
        public ushort ProductId { get; init; }
        public uint BoardId { get; init; }
        public string Name { get; init; } = string.Empty;
    }

    /// <summary>
    /// The SparkPlayJoy RM 6x21 session (issue #468), HallJoy's "WIN 60 HE"
    /// backend (aula_win60he_backend.cpp, aula_win60he_client.cpp): AULA WIN
    /// 60 HE MAX and PRO, WIN 68 HE PRO and MAX, HERO 68 HE PRO, and the
    /// GravaStar Mercury V75, V75 Pro and V75 Lite, plus unlisted siblings that
    /// prove the AULA platform bytes. Every command is a read, and nothing is
    /// sent at the end (aula_win60he_backend.cpp:2662-2725).
    ///
    /// <para>Start is HallJoy's read-only proof (Client::Probe,
    /// aula_win60he_client.cpp:255-400): the sync (identity), the precision
    /// read, the three default-map reads, two complete generations of the Fn0
    /// key-function map that must agree, and both travel halves, each within
    /// tolerance. A board from the known table must return its own board ID
    /// (aula_win60he_backend.cpp:1550-1561). Every transaction starts from a
    /// flushed queue, because no answer carries a transaction ID and a travel
    /// answer does not name its half (aula_win60he_client.cpp:234-242), and
    /// any failure ends the session.</para>
    ///
    /// <para>A pass is one loop of HallJoy's worker
    /// (aula_win60he_backend.cpp:2083-2263): the two key-function generations
    /// again when 2000 ms have passed, then travel half 1 and half 2, and the
    /// whole matrix normalized against the proven maximum travel, with a 1 ms
    /// wait between passes. HallJoy's matrix has no freshness window: each
    /// matrix replaces the last.</para>
    ///
    /// <para>The keys are named by the active Fn0 map, which follows the
    /// keyboard's own remaps: HallJoy publishes it whenever its automatic
    /// layout remaps, its default, and for every board without a layout
    /// token, and publishes the factory map only while that layout is off
    /// (aula_win60he_backend.cpp:2204-2210, native_layout_state.h:30-33,
    /// keyboard_layout.cpp:1786-1787). PadForge has no layout presets, so the
    /// active map names the keys on every board.</para>
    /// </summary>
    public sealed class AulaRmSession : AnalogKeyboardSession
    {
        /// <summary>Deadline for a whole answer (kSingleResponseTimeoutMs and
        /// kMatrixResponseTimeoutMs, aula_win60he_client.h:14-15).</summary>
        public const int TransactionTimeoutMs = 1000;

        /// <summary>How often the active map is read again
        /// (kActiveMapRefreshIntervalMs, aula_win60he_backend.cpp:55).</summary>
        public const int ActiveMapRefreshMs = 2000;

        /// <summary>The wait between two matrices (kPollPauseMs,
        /// aula_win60he_backend.cpp:53).</summary>
        public const int PollPauseMs = 1;

        private readonly ushort _vendorId;
        private readonly ushort _productId;
        private readonly string _product;
        private readonly AulaRmBoard _board;
        private readonly Func<long> _clock;
        private readonly Action<int> _pause;

        private readonly byte[] _stream = new byte[AulaRmProtocol.MaxResponseBytes];
        private readonly byte[] _sync = new byte[AulaRmProtocol.SyncPayloadBytes];
        private readonly byte[] _defaultMap = new byte[AulaRmProtocol.Positions];
        private readonly ushort[] _functions = new ushort[AulaRmProtocol.Positions];
        private readonly int[] _activeMap = new int[AulaRmProtocol.Positions];
        private readonly ushort[] _travel = new ushort[AulaRmProtocol.Positions];
        private readonly ushort[] _nextFunctions = new ushort[AulaRmProtocol.Positions];
        private readonly int[] _nextActiveMap = new int[AulaRmProtocol.Positions];
        private readonly ushort[] _checkFunctions = new ushort[AulaRmProtocol.Positions];
        private readonly int[] _checkActiveMap = new int[AulaRmProtocol.Positions];
        private int _mappedKeys;
        private bool _started;
        private bool _poisoned;
        private long _passes;
        private long _nextRefresh;
        private string _modelName;
        private int[] _keyOrder;

        /// <param name="vendorId">HIDD_ATTRIBUTES.VendorID of the collection.</param>
        /// <param name="productId">HIDD_ATTRIBUTES.ProductID.</param>
        /// <param name="productString">The HID product string, which tells
        /// a WIN 60 HE PRO from a MAX.</param>
        /// <param name="clock">Milliseconds, Environment.TickCount64 (HallJoy's
        /// GetTickCount64) unless a test supplies its own.</param>
        /// <param name="pause">The wait between passes, Thread.Sleep unless a
        /// test supplies its own.</param>
        public AulaRmSession(ushort vendorId, ushort productId, string productString,
            Func<long> clock = null, Action<int> pause = null)
        {
            _vendorId = vendorId;
            _productId = productId;
            _product = productString ?? string.Empty;
            _board = JingTaiRoutes.FindAulaBoard(vendorId, productId);
            _clock = clock ?? (() => Environment.TickCount64);
            _pause = pause ?? System.Threading.Thread.Sleep;
        }

        public override string ModelName => _modelName;

        public override int[] KeyOrder => _keyOrder;

        /// <summary>The board the sync returned.</summary>
        public uint BoardId { get; private set; }

        public int PrecisionUm { get; private set; }
        public int MinimumTravelUm { get; private set; }

        /// <summary>The proven full travel every value is normalized against.</summary>
        public int MaximumTravelUm { get; private set; }

        /// <summary>Distinct key codes of the active Fn0 map.</summary>
        public int MappedKeys => _mappedKeys;

        public bool Poisoned => _poisoned;

        /// <summary>The factory identifiers the keyboard reported, row-major.</summary>
        public ReadOnlySpan<byte> DefaultMap => _defaultMap;

        /// <summary>The key code each position publishes: the active Fn0 map.</summary>
        public ReadOnlySpan<int> PublicationMap => _activeMap;

        public override bool Start(IAnalogKeyboardTransport io)
        {
            // Sync. A known board must return its own board ID. Any other
            // keyboard must show the AULA platform bytes C0 01 00
            // (ProbePolicyForSession, aula_win60he_backend.cpp:1522-1539).
            // HallJoy sorts a failed proof by its stage: a transfer or a
            // decode that fails is tried again 100 ms later, and a proof that
            // decoded but names the wrong firmware, precision, default map,
            // an unstable active map or implausible travel waits for the
            // device to change (IsDeterministicSemanticFailure,
            // aula_win60he_backend.cpp:1819-1834, 1956-1963, and the stages
            // of Client::Probe, aula_win60he_client.cpp:275-375, 436, 570).
            if (!Transact(io, AulaRmProtocol.SyncRequest(), AulaRmProtocol.CommandSync, 1, 0, 0, out int length))
                return Poison();
            if (!AulaRmProtocol.DecodeSync(_stream[2], Payload(length), out uint board))
                return Poison();
            Payload(length).CopyTo(_sync);
            BoardId = board;
            bool firmware = _board != null
                ? AulaRmProtocol.IsKnownBoardFirmware(_sync, board, _board.BoardId)
                : AulaRmProtocol.IsFamilyFirmware(_sync, board);
            if (!firmware) return Refuse();

            // Precision and stroke. HallJoy's decode already holds the family
            // range (DecodePrecisionStroke, aula_win60he_protocol.cpp:244-261),
            // so a precision out of range fails as a decode and is retried.
            if (!Transact(io, AulaRmProtocol.PrecisionRequest(), AulaRmProtocol.CommandApi, 1,
                    AulaRmProtocol.OrderPrecisionStroke, 0, out length))
                return Poison();
            if (!AulaRmProtocol.DecodePrecision(_stream[2], Payload(length),
                    out int precision, out int minimum, out int maximum))
                return Poison();
            PrecisionUm = precision;
            MinimumTravelUm = minimum;
            MaximumTravelUm = maximum;

            // The factory (default) map, two rows per read.
            for (int row = 0; row < AulaRmProtocol.Rows; row += 2)
            {
                if (!Transact(io, AulaRmProtocol.DefaultKeyRequest(row, row + 1), AulaRmProtocol.CommandDefaultKeys, 1,
                        0, (byte)row, out length)
                    || !AulaRmProtocol.DecodeDefaultRows(_stream[2], Payload(length), row, row + 1, _defaultMap))
                    return Poison();
            }
            if (!AulaRmProtocol.IsFamilyDefaultMap(_defaultMap)) return Refuse();

            // Two identical Fn0 generations, then both travel halves.
            if (!ReadActiveMap(io, _functions, _activeMap)) return Poison();
            _mappedKeys = AulaRmProtocol.CountMappedKeyCodes(_activeMap);
            if (!ReadTravelMatrix(io, _travel)) return Poison();

            // A table identity must answer with its own board
            // (IsKnownUsbIdentityBoardCompatible, aula_win60he_protocol.h:55-62).
            if (_board != null && board != _board.BoardId) return Refuse();

            // The model: WIN 60 HE PRO by the exact product string, a known
            // board by its board ID, none for a sibling
            // (aula_win60he_backend.cpp:1570-1571, 1593-1601, sparkplayjoy_layout.h:8-22).
            bool win60Pro = _vendorId == 0x1CA2 && _productId == 0x1902
                && string.Equals(_product, AulaRmProtocol.Win60ProProduct, StringComparison.Ordinal);
            _modelName = win60Pro ? AulaRmProtocol.Win60ProName : _board?.Name;
            _keyOrder = AnalogKeyboardData.KeysOf(_activeMap);
            _nextRefresh = _clock() + ActiveMapRefreshMs;
            _started = true;
            return true;
        }

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            if (!_started || _poisoned) return AnalogPollResult.Failed;
            if (_passes++ > 0) _pause(PollPauseMs);

            if (_clock() >= _nextRefresh)
            {
                // An active map that no longer reads the same twice ends the
                // session (aula_win60he_backend.cpp:2085-2117).
                if (!ReadActiveMap(io, _nextFunctions, _nextActiveMap)) return Fail();
                if (!_nextFunctions.AsSpan().SequenceEqual(_functions)
                    || !_nextActiveMap.AsSpan().SequenceEqual(_activeMap))
                {
                    _nextFunctions.CopyTo(_functions, 0);
                    _nextActiveMap.CopyTo(_activeMap, 0);
                    _mappedKeys = AulaRmProtocol.CountMappedKeyCodes(_activeMap);
                    _keyOrder = AnalogKeyboardData.KeysOf(_activeMap);
                }
                _nextRefresh = _clock() + ActiveMapRefreshMs;
            }

            if (!ReadTravelMatrix(io, _travel)) return Fail();
            BuildSnapshot(PublicationMap, _travel, MaximumTravelUm, output);
            return AnalogPollResult.Ok;
        }

        /// <summary>The published depths of one matrix, the largest over the
        /// positions that carry the same code (BuildHidMilliSnapshot,
        /// aula_win60he_client.cpp:640-666).</summary>
        public static void BuildSnapshot(ReadOnlySpan<int> map, ReadOnlySpan<ushort> travel, int maximumUm,
            AnalogKeyInputState output)
        {
            Span<int> milli = stackalloc int[AulaRmProtocol.Positions];
            for (int i = 0; i < AulaRmProtocol.Positions; i++)
                milli[i] = AulaRmProtocol.NormalizeTravel(travel[i], maximumUm);
            JingTaiFrames.WriteDepths(map, milli, output);
        }

        /// <summary>Two complete Fn0 generations that must agree, since the
        /// firmware has no generation counter and no atomic full-map read
        /// (ReadActiveMap, aula_win60he_client.cpp:402-445).</summary>
        private bool ReadActiveMap(IAnalogKeyboardTransport io, ushort[] functions, int[] keyMap)
        {
            if (!ReadActiveMapGeneration(io, _checkFunctions, _checkActiveMap)) return false;
            if (!ReadActiveMapGeneration(io, functions, keyMap)) return false;
            // Equal functions give equal key maps and counts, which HallJoy
            // compares as well. Two generations that differ are an unstable
            // map, which waits for the device to change
            // (aula_win60he_client.cpp:425-440).
            if (_checkFunctions.AsSpan().SequenceEqual(functions)
                && _checkActiveMap.AsSpan().SequenceEqual(keyMap))
                return true;
            return Refuse();
        }

        /// <summary>One Fn0 generation (ReadActiveMapGeneration,
        /// aula_win60he_client.cpp:447-522): the default map's identifiers
        /// row by row, fourteen per read, a short last read padded with its
        /// last identifier.</summary>
        private bool ReadActiveMapGeneration(IAnalogKeyboardTransport io, ushort[] functions, int[] keyMap)
        {
            Span<byte> keys = stackalloc byte[AulaRmProtocol.Positions];
            int count = 0;
            foreach (byte key in _defaultMap)
                if (key != 0) keys[count++] = key;
            if (count == 0) return false;

            Array.Clear(functions);
            Span<byte> query = stackalloc byte[AulaRmProtocol.KeyFunctionRecords];
            Span<ushort> values = stackalloc ushort[AulaRmProtocol.KeyFunctionRecords];
            for (int begin = 0; begin < count; begin += AulaRmProtocol.KeyFunctionRecords)
            {
                int n = Math.Min(count - begin, AulaRmProtocol.KeyFunctionRecords);
                keys.Slice(begin, n).CopyTo(query);
                for (int i = n; i < query.Length; i++) query[i] = query[n - 1];
                byte batch = (byte)(begin / AulaRmProtocol.KeyFunctionRecords);
                if (!Transact(io, AulaRmProtocol.KeyFunctionRequest(query, AulaRmProtocol.LayoutFn0),
                        AulaRmProtocol.CommandKeyFunctions, 1, AulaRmProtocol.LayoutFn0, batch, out int length)
                    || !AulaRmProtocol.DecodeKeyFunctions(_stream[2], Payload(length), query,
                        AulaRmProtocol.LayoutFn0, values)
                    || !AulaRmProtocol.ApplyKeyFunctions(_defaultMap, query, values, functions))
                    return Poison();
            }
            AulaRmProtocol.BuildActiveKeyMap(_defaultMap, functions, keyMap);
            return true;
        }

        /// <summary>Travel half 1 (rows 0 to 2), then half 2 (rows 3 to 5),
        /// each three reports and each checked for plausibility
        /// (ReadTravelMatrix, aula_win60he_client.cpp:524-580).</summary>
        private bool ReadTravelMatrix(IAnalogKeyboardTransport io, ushort[] travel)
        {
            Span<ushort> half = stackalloc ushort[AulaRmProtocol.ValuesPerHalf];
            for (int h = 1; h <= 2; h++)
            {
                if (!Transact(io, AulaRmProtocol.TravelRequest(h), AulaRmProtocol.CommandMatrix,
                        AulaRmProtocol.MaxResponseReports, AulaRmProtocol.SelectorTravel, (byte)h, out int length)
                    || !AulaRmProtocol.DecodeTravelHalf(_stream[2], Payload(length), half))
                    return Poison();
                // Travel that decodes but is implausible waits for the device
                // to change (aula_win60he_client.cpp:561-574).
                if (!AulaRmProtocol.TravelPlausible(half, _defaultMap, h == 1 ? 0 : AulaRmProtocol.RowsPerHalf,
                        PrecisionUm, MinimumTravelUm, MaximumTravelUm))
                    return Refuse();
                half.CopyTo(travel.AsSpan((h - 1) * AulaRmProtocol.ValuesPerHalf));
            }
            return true;
        }

        /// <summary>One transaction (Client::Transact and ReadResponse,
        /// aula_win60he_client.cpp:140-253): flush, write, then the expected
        /// number of 65-byte reports within 1000 ms, report ID 0 on each, the
        /// first matching the request before any continuation is read, and the
        /// frame parsed over the whole stream. The frame lands in the stream
        /// buffer, its payload from byte 4.</summary>
        private bool Transact(IAnalogKeyboardTransport io, byte[] request, byte command, int reports,
            byte selector, byte index, out int payloadBytes)
        {
            payloadBytes = 0;
            if (_poisoned) return false;
            io.DiscardStale();
            if (!io.Send(request)) return Poison();
            Array.Clear(_stream);
            long deadline = _clock() + TransactionTimeoutMs;
            for (int r = 0; r < reports; r++)
            {
                long remaining = deadline - _clock();
                if (remaining <= 0) return Poison();
                int n = io.Receive(Buffer, (int)remaining);
                if (n != AulaRmProtocol.WindowsReportBytes || Buffer[0] != 0) return Poison();
                var block = Buffer.AsSpan(1, AulaRmProtocol.WireReportBytes);
                if (r == 0 && !AulaRmProtocol.FirstReportMatches(block, command, reports, selector, index))
                    return Poison();
                block.CopyTo(_stream.AsSpan(r * AulaRmProtocol.WireReportBytes));
            }
            if (!AulaRmProtocol.ParseResponseFrame(_stream.AsSpan(0, reports * AulaRmProtocol.WireReportBytes),
                    command, out payloadBytes)
                || AulaRmProtocol.ResponseReportCount(payloadBytes) != reports)
                return Poison();
            return true;
        }

        private ReadOnlySpan<byte> Payload(int length) => _stream.AsSpan(4, length);

        private AnalogPollResult Fail()
        {
            _poisoned = true;
            return AnalogPollResult.Failed;
        }

        private bool Poison()
        {
            _poisoned = true;
            return false;
        }

        /// <summary>A proof the keyboard is not one of this route's: no timed
        /// retry follows.</summary>
        private bool Refuse()
        {
            NoStartRetry = true;
            return Poison();
        }
    }
}
