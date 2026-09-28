using System;
using System.Threading;

namespace PadForge.Engine.Common.AnalogKeyboard.Routes
{
    /// <summary>
    /// The AULA HERO family's side of the 09 frame protocol, HallJoy's
    /// aula_hero84he_backend.cpp and aula_hero84he_factory.h (AGPL-3.0): the
    /// assignment decode, the position plan, the adaptive normalization and
    /// the answer parsers.
    ///
    /// <para>HallJoy sends a HERO board three requests only: <c>82 01</c>,
    /// <c>83 00</c> and <c>94 02</c>. Its firmware analysis forbids the rest
    /// of the <c>94</c> table (<c>94 00</c> starts calibration, <c>94 03</c>
    /// writes a key setting and schedules a flash save, <c>94 04</c> changes
    /// calibration and mode state, <c>94 05</c> returns calibration extrema)
    /// and the <c>98</c> calibration flow (docs/research/AULA_HERO84HE_FIRMWARE_2026-08-31.md:42-51,
    /// 101-116). This route builds nothing else, and the Addressed route's
    /// <c>94 05</c> and <c>98 02</c> never reach a HERO board.</para>
    /// </summary>
    public static class AulaHeroProtocol
    {
        /// <summary>The read wait of one exchange, kCommandTimeoutMs
        /// (aula_hero84he_backend.cpp:38).</summary>
        public const int CommandTimeoutMs = 200;

        /// <summary>A position's depth stays current this long after its last
        /// sample, kFreshMs (aula_hero84he_backend.cpp:39, 595-598).</summary>
        public const long FreshMs = 750;

        /// <summary>Failed exchanges in a row that end the session
        /// (aula_hero84he_backend.cpp:439-453).</summary>
        public const int FailureLimit = 3;

        /// <summary>The loop's spacing, one request per millisecond at most
        /// (aula_hero84he_backend.cpp:463-468).</summary>
        public const long SpacingUs = 1000;

        /// <summary>Travel a position must show before it reports depth
        /// (aula_hero84he_backend.cpp:399).</summary>
        public const int MinimumSpan = 32;

        /// <summary>W, A, S, D and Space, in every request, kMovement
        /// (aula_hero84he_backend.cpp:47). All three HERO tables carry them.</summary>
        public static readonly ushort[] MovementPositions = { 30, 43, 44, 45, 70 };

        /// <summary>
        /// A layer-0 assignment to a key code (DecodeAssignment,
        /// aula_hero84he_factory.h:93-101): 0x0D000000 the Fn key, up to 0xE7
        /// the usage itself (0 is unassigned), a single modifier bit in bits 16
        /// to 23 the modifier usage 0xE0 to 0xE7, anything else 0.
        /// </summary>
        public static int DecodeAssignment(uint value)
        {
            if (value == 0x0D000000u) return AnalogKeyCodes.Fn;
            if (value <= 0xE7u) return (int)value;
            uint mask = value >> 16;
            if (value != mask << 16 || mask == 0 || mask > 128 || (mask & (mask - 1)) != 0) return 0;
            int hid = 0xE0;
            for (uint bits = mask; bits > 1; bits >>= 1) hid++;
            return hid;
        }

        /// <summary>
        /// The positions of the next <c>94 02</c> (Plan,
        /// aula_hero84he_backend.cpp:350-376): the five movement positions,
        /// then up to four more found by scanning positions 1 to 255 from a
        /// cursor that moves one step per request. HallJoy takes the
        /// positions whose key its realtime loop read in the last 300 ms,
        /// and its keyboard page reads every key of the layout each tick
        /// (backend.cpp:3324-3338, keyboard_page_main.cpp:464-480), so every
        /// position with a key is taken. Returns how many were written into
        /// <paramref name="positions"/>, which holds nine.
        /// </summary>
        public static int Plan(int[] codeByPosition, ref ushort cursor, Span<ushort> positions)
        {
            int n = 0;
            foreach (ushort p in MovementPositions) Add(positions, ref n, p);
            ushort first = cursor;
            cursor = unchecked((ushort)(cursor + 1));
            for (int offset = 0; offset < 255 && n < positions.Length; offset++)
            {
                // HallJoy's unsigned arithmetic, a wrapped cursor included.
                int p = (int)(unchecked((ulong)first + (ulong)offset - 1UL) % 255UL) + 1;
                if (p < codeByPosition.Length && codeByPosition[p] != 0) Add(positions, ref n, (ushort)p);
            }
            return n;
        }

        private static void Add(Span<ushort> positions, ref int n, ushort p)
        {
            if (p == 0 || n == positions.Length) return;
            for (int i = 0; i < n; i++)
                if (positions[i] == p) return;
            positions[n++] = p;
        }

        /// <summary>
        /// A sample to depth in thousandths with the position's range learned
        /// this session (Publish, aula_hero84he_backend.cpp:377-409): the
        /// highest raw seen is rest and the lowest the bottom, since pressing
        /// lowers the raw value. Until the range exceeds 32 counts the key
        /// reads 0. Under 8 is rest. The caller skips a raw value of 0.
        /// </summary>
        public static ushort Normalize(ushort raw, ref ushort top, ref ushort bottom)
        {
            if (top == 0 || raw > top) top = raw;
            if (bottom == 0 || raw < bottom) bottom = raw;
            if (top <= bottom + MinimumSpan || raw >= top) return 0;
            uint span = (uint)(top - bottom);
            uint milli = Math.Min(1000u, ((uint)(top - raw) * 1000u + span / 2u) / span);
            return milli < 8 ? (ushort)0 : (ushort)milli;
        }

        /// <summary>A layer-0 <c>83 00</c> answer (ParseAssignmentResponse,
        /// aula_hero84he_diagnostic_protocol.cpp:127-145): positions echoed in
        /// request order, each with a big-endian 32-bit assignment.</summary>
        public static bool ParseAssignments(ReadOnlySpan<byte> frame, ReadOnlySpan<ushort> positions, Span<uint> values)
        {
            if (!AddressedFrame.OrderedRecords(frame, AddressedFrame.MapCommand, AddressedFrame.MapSubcommand, positions))
                return false;
            for (int i = 0; i < positions.Length; i++)
                values[i] = AddressedIpiProtocol.BigEndian32(frame,
                    AddressedFrame.DataOffset + i * AddressedFrame.RecordLength + 2);
            return true;
        }

        /// <summary>A <c>94 02</c> answer (ParseDirectResponse,
        /// aula_hero84he_diagnostic_protocol.cpp:147-167): positions echoed in
        /// request order, each with the scanner's current value, whose bit 15
        /// is a short-lived scanner lock HallJoy masks off, and an episode
        /// minimum HallJoy does not use.</summary>
        public static bool ParseSamples(ReadOnlySpan<byte> frame, ReadOnlySpan<ushort> positions, Span<ushort> current)
        {
            if (!AddressedFrame.OrderedRecords(frame, AddressedFrame.SampleCommand, AddressedFrame.SampleSubcommand,
                    positions))
                return false;
            for (int i = 0; i < positions.Length; i++)
                current[i] = (ushort)(AddressedIpiProtocol.BigEndian16(frame,
                    AddressedFrame.DataOffset + i * AddressedFrame.RecordLength + 2) & 0x7FFF);
            return true;
        }
    }

    /// <summary>
    /// The AULA HERO family on 372E:103E: HERO84 HE, HERO 68 HE, HERO 68 Air,
    /// HERO 68 MINI, WIN 68 HE Ultra and HERO 99 HE (issue #468), HallJoy's
    /// aula-hero84he-9402-experimental route, which HallJoy has not tested on
    /// hardware.
    ///
    /// <para>Start reads the UUID with <c>82 01</c> and refuses a board whose
    /// UUID is not in the table, then reads the layer-0 assignment of every
    /// position of the model, nine per request in the table's order
    /// (Identity and Map, aula_hero84he_backend.cpp:303-349). A pass sends one
    /// <c>94 02</c> for the planned positions and reads the one answer, which
    /// must echo them in order. Three failed exchanges in a row end the
    /// session, and requests are spaced at most one per millisecond
    /// (aula_hero84he_backend.cpp:433-469). Each key reports under the code
    /// its assignment decodes to, HallJoy's reading with its default
    /// automatic layout, and a key assigned nothing decodable is neither
    /// planned nor reported (aula_hero84he_backend.cpp:314-334, 370, 587-598).</para>
    /// </summary>
    public sealed class AulaHeroSession : AnalogKeyboardSession
    {
        /// <summary>Microseconds, for the spacing and the freshness window.
        /// Tests replace it.</summary>
        public Func<long> Clock { get; init; } = AddressedClock.Microseconds;

        /// <summary>The spacing sleep in milliseconds. Tests replace it.</summary>
        public Action<int> Sleep { get; init; } = Thread.Sleep;

        private AddressedModel _model;
        private readonly int[] _factory = new int[256];
        private readonly int[] _live = new int[256];
        private readonly ushort[] _top = new ushort[256];
        private readonly ushort[] _bottom = new ushort[256];
        private readonly AddressedPublication _publication = new(AulaHeroProtocol.FreshMs);
        private readonly ushort[] _positions = new ushort[AddressedFrame.MaxKeys];
        private ushort _cursor = 1;
        private int[] _keyOrder;
        private long _nextUs;
        private int _lastCount;

        public override string ModelName => _model?.Name;
        public override int[] KeyOrder => _keyOrder;
        public override int MissLimit => AulaHeroProtocol.FailureLimit;

        /// <summary>The model the UUID named, once Start succeeded.</summary>
        public AddressedModel Model => _model;

        /// <summary>The code each position's assignment decodes to.</summary>
        public int CodeAt(int position) => _live[position];

        /// <summary>Each position's latest depth under the code it reports as.</summary>
        public AddressedPublication Publication => _publication;

        /// <summary>The last pass's positions, in request order.</summary>
        public ReadOnlySpan<ushort> LastPositions => _positions.AsSpan(0, _lastCount);

        public override bool Start(IAnalogKeyboardTransport io)
        {
            if (!Exchange(io, AddressedFrame.IdentityRequest(), out _)) return false;
            var model = AddressedRoutes.FindAulaHeroModel(AddressedFrame.ParseUuid(Buffer.AsSpan(0, AddressedFrame.Length)));
            if (model == null) return false;

            var assignments = new uint[256];
            Span<uint> values = stackalloc uint[AddressedFrame.MaxKeys];
            var order = model.Order;
            for (int start = 0; start < order.Length; start += AddressedFrame.MaxKeys)
            {
                var batch = new ReadOnlySpan<ushort>(order, start, Math.Min(AddressedFrame.MaxKeys, order.Length - start));
                if (!Exchange(io, AddressedFrame.MapRequest(batch), out _)) return false;
                if (!AulaHeroProtocol.ParseAssignments(Buffer.AsSpan(0, AddressedFrame.Length), batch, values))
                    return false;
                for (int i = 0; i < batch.Length; i++) assignments[batch[i]] = values[i];
            }

            // InstallMap (aula_hero84he_backend.cpp:314-334): the factory
            // key of every position, and the assigned code bound when it
            // decodes to one.
            foreach (ushort position in order)
            {
                _factory[position] = model.Table[position];
                _live[position] = AulaHeroProtocol.DecodeAssignment(assignments[position]);
                _publication.Bind(position, _live[position]);
            }
            _model = model;
            _keyOrder = AnalogKeyboardData.KeysOf(_live);
            _nextUs = Clock();
            return true;
        }

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            if (_model == null) return AnalogPollResult.Failed;
            _lastCount = AulaHeroProtocol.Plan(_live, ref _cursor, _positions);
            var positions = _positions.AsSpan(0, _lastCount);
            var result = AnalogPollResult.NoAnswer;
            Span<ushort> current = stackalloc ushort[AddressedFrame.MaxKeys];
            if (Exchange(io, AddressedFrame.SampleRequest(positions), out bool gone)
                && AulaHeroProtocol.ParseSamples(Buffer.AsSpan(0, AddressedFrame.Length), positions, current))
            {
                long nowMs = Clock() / 1000;
                for (int i = 0; i < positions.Length; i++) Record(positions[i], current[i], nowMs);
                _publication.Fill(output, nowMs);
                result = AnalogPollResult.Ok;
            }
            else if (gone)
            {
                return AnalogPollResult.Failed;
            }
            Pace();
            return result;
        }

        /// <summary>
        /// One request and its answer, HallJoy's Session::Exchange
        /// (aula_hero84he_backend.cpp:259-277): the write, then one read of
        /// exactly 64 bytes within 200 ms, taken as the answer. HallJoy reads
        /// whatever report is queued first, so a stale report fails the
        /// exchange. This discards queued reports before each request, so the
        /// read waits for the request's own answer.
        /// </summary>
        private bool Exchange(IAnalogKeyboardTransport io, byte[] request, out bool gone)
        {
            gone = false;
            if (request == null) return false;
            io.DiscardStale();
            if (!io.Send(request)) return false;
            int n = io.Receive(Buffer, AulaHeroProtocol.CommandTimeoutMs);
            if (n < 0)
            {
                gone = true;
                return false;
            }
            return n == AddressedFrame.Length;
        }

        /// <summary>Publish's per-position step (aula_hero84he_backend.cpp:377-409).
        /// A position without a factory key or a zero sample is skipped. The
        /// range is learned for every sampled position, and the depth is
        /// published when the position's assignment is bound.</summary>
        private void Record(ushort position, ushort raw, long nowMs)
        {
            if (position >= _factory.Length || _factory[position] == 0 || raw == 0) return;
            ushort milli = AulaHeroProtocol.Normalize(raw, ref _top[position], ref _bottom[position]);
            _publication.Publish(position, milli, nowMs);
        }

        /// <summary>HallJoy's pacing: the next request is due 1 ms after the
        /// last one was due, the loop sleeps only when ahead, and a late loop
        /// restarts the schedule from now (aula_hero84he_backend.cpp:463-468).</summary>
        private void Pace()
        {
            _nextUs += AulaHeroProtocol.SpacingUs;
            long now = Clock();
            if (_nextUs > now) Sleep((int)((_nextUs - now) / 1000));
            else _nextUs = now;
        }

        /// <summary>The route is read-only and HallJoy sends nothing when it
        /// stops (aula_hero84he_backend.cpp:548-573, 633-634).</summary>
        public override void Stop(IAnalogKeyboardTransport io) { }
    }
}
