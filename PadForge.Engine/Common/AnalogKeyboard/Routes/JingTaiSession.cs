using System;

namespace PadForge.Engine.Common.AnalogKeyboard.Routes
{
    /// <summary>
    /// One keyboard model on the JingTai V1 frame: HallJoy's jt::Model
    /// (jingtai_v1_profiles.h:5), its IROK MG75 Pro kLegacyModel
    /// (mg75_pro_backend.cpp:31), or the Chilkey Slice75 HE
    /// (slice75_protocol.h:10-37). The table holds the key code of every slot.
    /// </summary>
    public sealed class JingTaiModel
    {
        /// <summary>HallJoy's model identity, for example "JT1-K".</summary>
        public string Identity { get; init; } = string.Empty;

        /// <summary>The name HallJoy gives the model.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Key code per slot, 126 entries, 0 where no key sits.</summary>
        public int[] Table { get; init; } = Array.Empty<int>();

        /// <summary>Occupied slots the table must have (InstallMap's check).</summary>
        public int Count { get; init; }

        /// <summary>Raw travel at the bottom of the press, in micrometers.</summary>
        public int Range { get; init; }

        /// <summary>True for the models whose identity proof starts with the
        /// three factory-row reads: the MG75 Pro (mg75_pro_backend.cpp:276-282)
        /// and the Slice75 (slice75_backend.cpp:266-271). The newer JingTai V1
        /// models use pinned vendor maps and skip it.</summary>
        public bool FactoryProof { get; init; }
    }

    /// <summary>
    /// The JingTai V1 session (issue #468): HallJoy's IROK MG75 Pro route,
    /// which serves the pinned-map JingTai V1 models too
    /// (mg75_pro_backend.cpp:213-445), and its Chilkey Slice75 HE route, the
    /// same code over the same frame (slice75_backend.cpp:205-403). Read-only:
    /// the keyboard only ever hears 0x2B, 0x12 and 0x23 reads, and nothing is
    /// sent at the end (mg75_pro_backend.cpp:493-519).
    ///
    /// <para>Start is HallJoy's admission (Run, mg75_pro_backend.cpp:360-379):
    /// the three factory-row reads for the models that carry that proof, both
    /// travel halves, then the base-layer assignment of every key, fourteen
    /// selectors per read, and InstallMap's table checks. A pass reads one
    /// travel half, alternating 1, 2, 1, with HallJoy's 1 ms wait between
    /// exchanges (mg75_pro_backend.cpp:389-415). Each value is stamped when it
    /// arrives, and a key whose value was not refreshed within 150 ms reads 0
    /// (mg75_pro_backend.cpp:34, 530-535, physical_analog_state.h:39-52), so a
    /// pass reports the fresh half it read and the other half while it is
    /// still fresh.</para>
    ///
    /// <para>Each key is published under the code its base-layer assignment
    /// decodes to, so a key remapped on the keyboard moves the key it now
    /// types, and a slot assigned nothing HallJoy can publish is not
    /// published. HallJoy binds every slot to its factory code and to its
    /// assignment and reads the assignments whenever its automatic layout
    /// remaps, its default (InstallMap and Get, mg75_pro_backend.cpp:42-44,
    /// 291-315, 527-535, native_layout_state.h:30-33,
    /// keyboard_layout.cpp:1786-1787). PadForge has no layout presets, so
    /// the assignments name the keys on every model.</para>
    /// </summary>
    public sealed class JingTaiSession : AnalogKeyboardSession
    {
        /// <summary>A write that takes longer fails the exchange
        /// (mg75_pro_backend.cpp:240).</summary>
        public const int WriteTimeoutMs = 50;

        /// <summary>How long a whole answer may take, counted from the end of
        /// the write (mg75_pro_backend.cpp:245).</summary>
        public const int AnswerDeadlineMs = 120;

        /// <summary>A value older than this reads 0 (mg75_pro_backend.cpp:34,
        /// slice75_backend.cpp:30).</summary>
        public const int FreshMs = 150;

        /// <summary>The wait between two travel exchanges
        /// (mg75_pro_backend.cpp:412-414).</summary>
        public const int PauseMs = 1;

        private readonly JingTaiModel _model;
        private readonly Func<long> _clock;
        private readonly Action<int> _pause;
        private readonly JingTaiFrame _frame = new();
        private readonly ushort[] _values = new ushort[JingTaiFrames.ValuesPerHalf];
        private readonly int[] _assigned = new int[JingTaiFrames.Slots];
        private readonly int[] _publish = new int[JingTaiFrames.Slots];
        private readonly int[] _milli = new int[JingTaiFrames.Slots];
        private readonly long[] _stamps = new long[JingTaiFrames.Slots];
        private bool _started;
        private bool _poisoned;
        private int _half = 1;
        private long _passes;

        /// <param name="model">The model the collection's identity named.</param>
        /// <param name="clock">Milliseconds, Environment.TickCount64 (HallJoy's
        /// GetTickCount64) unless a test supplies its own.</param>
        /// <param name="pause">The wait between exchanges, Thread.Sleep unless
        /// a test supplies its own.</param>
        public JingTaiSession(JingTaiModel model, Func<long> clock = null, Action<int> pause = null)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _clock = clock ?? (() => Environment.TickCount64);
            _pause = pause ?? System.Threading.Thread.Sleep;
            Array.Fill(_stamps, -1L);
        }

        public JingTaiModel Model => _model;

        public override string ModelName => _model.Name;

        public override int[] KeyOrder => AnalogKeyboardData.KeysOf(_started ? _publish : _model.Table);

        /// <summary>True once an exchange failed. Every later exchange fails
        /// without touching the keyboard (mg75_pro_backend.cpp:236-237).</summary>
        public bool Poisoned => _poisoned;

        /// <summary>The key each slot's base layer assigns, from the 0x23
        /// reads, 0 where nothing HallJoy can publish is assigned.</summary>
        public ReadOnlySpan<int> Assigned => _assigned;

        /// <summary>The key code each slot publishes: its assignment on a slot
        /// the model's table fills, else 0.</summary>
        public ReadOnlySpan<int> PublicationMap => _publish;

        /// <summary>The travel half the next pass reads.</summary>
        public int NextHalf => _half;

        public override bool Start(IAnalogKeyboardTransport io)
        {
            // HallJoy's Session::Open sets 64 input buffers and flushes once
            // (mg75_pro_backend.cpp:221-234). The channel did both when the
            // route's Exclusive and InputBuffers settings opened it, so the
            // proof starts at once. InstallMap checks the table after the
            // reads. The table is fixed, so checking it first changes no
            // outcome and spares a keyboard the reads when it would fail.
            if (!JingTaiFrames.IsInstallable(_model.Table, _model.Count)) return Poison();
            if (_model.FactoryProof)
            {
                for (int row = 0; row < 6; row += 2)
                {
                    if (!Exchange(io, JingTaiFrames.Factory(row))
                        || !JingTaiFrames.MatchFactory(_frame, row, _model.Table))
                        return Poison();
                }
            }
            for (int half = 1; half <= 2; half++)
            {
                if (!Exchange(io, JingTaiFrames.Travel(half)) || !JingTaiFrames.ParseTravel(_frame, _values))
                    return Poison();
            }
            if (!ReadMap(io)) return Poison();
            var table = _model.Table;
            for (int slot = 0; slot < JingTaiFrames.Slots; slot++)
                _publish[slot] = table[slot] != 0 ? _assigned[slot] : 0;
            _started = true;
            return true;
        }

        /// <summary>The 0x23 reads (ReadMap, mg75_pro_backend.cpp:317-341):
        /// the occupied slots in order, fourteen selectors per read, the last
        /// read's unused selectors 0.</summary>
        private bool ReadMap(IAnalogKeyboardTransport io)
        {
            var table = _model.Table;
            Span<byte> selectors = stackalloc byte[JingTaiFrames.LayoutKeys];
            Span<int> slots = stackalloc int[JingTaiFrames.LayoutKeys];
            Span<int> values = stackalloc int[JingTaiFrames.LayoutKeys];
            int slot = 0;
            while (slot < JingTaiFrames.Slots)
            {
                selectors.Clear();
                int count = 0;
                for (; slot < JingTaiFrames.Slots && count < JingTaiFrames.LayoutKeys; slot++)
                {
                    if (table[slot] == 0) continue;
                    slots[count] = slot;
                    selectors[count++] = JingTaiFrames.Selector(table[slot]);
                }
                if (count == 0) break;
                if (!Exchange(io, JingTaiFrames.Layout(selectors))
                    || !JingTaiFrames.ParseLayout(_frame, selectors, values))
                    return false;
                for (int i = 0; i < count; i++) _assigned[slots[i]] = values[i];
            }
            return true;
        }

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            if (!_started || _poisoned) return AnalogPollResult.Failed;
            if (_passes++ > 0) _pause(PauseMs);
            if (!Exchange(io, JingTaiFrames.Travel(_half)) || !JingTaiFrames.ParseTravel(_frame, _values))
            {
                // A failed or late exchange, or a half with a value over 6000,
                // ends the session (mg75_pro_backend.cpp:392-395).
                _poisoned = true;
                return AnalogPollResult.Failed;
            }
            long now = _clock();
            Publish(_half, _values, now);
            _half = 3 - _half;
            Read(output, now);
            return AnalogPollResult.Ok;
        }

        /// <summary>Stamps one half's values (Publish,
        /// mg75_pro_backend.cpp:346-359): half h carries slots (h - 1) * 63 + i,
        /// and an empty slot is skipped.</summary>
        private void Publish(int half, ReadOnlySpan<ushort> values, long now)
        {
            var table = _model.Table;
            for (int i = 0; i < JingTaiFrames.ValuesPerHalf; i++)
            {
                int slot = (half - 1) * JingTaiFrames.ValuesPerHalf + i;
                if (table[slot] == 0) continue;
                _milli[slot] = JingTaiFrames.Normalize(values[i], _model.Range);
                _stamps[slot] = now;
            }
        }

        /// <summary>Writes every key whose value is fresh, the largest over the
        /// slots that carry the same code (Publication::Read,
        /// physical_analog_state.h:39-52). A value stamped more than 150 ms ago
        /// reads 0.</summary>
        private void Read(AnalogKeyInputState output, long now)
        {
            Span<int> fresh = stackalloc int[JingTaiFrames.Slots];
            for (int slot = 0; slot < JingTaiFrames.Slots; slot++)
            {
                long stamp = _stamps[slot];
                fresh[slot] = stamp >= 0 && now >= stamp && now - stamp <= FreshMs ? _milli[slot] : 0;
            }
            JingTaiFrames.WriteDepths(_publish, fresh, output);
        }

        /// <summary>One request and its answer (Session::Exchange,
        /// mg75_pro_backend.cpp:235-260): a write that must finish within
        /// 50 ms, then 65-byte reports until the frame is whole, all within
        /// 120 ms of the end of the write. Any failure poisons the session,
        /// because a late answer could otherwise be taken for the next
        /// request's.</summary>
        private bool Exchange(IAnalogKeyboardTransport io, byte[] request)
        {
            if (_poisoned) return false;
            long before = _clock();
            if (!io.Send(request) || _clock() - before > WriteTimeoutMs) return Poison();
            _frame.Reset();
            long deadline = _clock() + AnswerDeadlineMs;
            while (!_frame.Complete)
            {
                long now = _clock();
                if (now >= deadline) return Poison();
                int n = io.Receive(Buffer, (int)(deadline - now));
                if (n <= 0 || !_frame.Push(Buffer, n, request[3])) return Poison();
            }
            return true;
        }

        private bool Poison()
        {
            _poisoned = true;
            return false;
        }
    }
}
