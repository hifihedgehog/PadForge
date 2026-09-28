using System;
using System.Collections.Generic;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// The RY5088 feature-report command set both RongYuan routes use
    /// (rongyuan_snapshot_protocol.h, rongyuan_stream_protocol.h). Pure
    /// functions over Windows buffers: byte 0 is the report ID, 0 for these
    /// unnumbered 65-byte reports.
    /// </summary>
    public static class RongYuanProtocol
    {
        public const int ReportLength = 65;

        /// <summary>A request (rongyuan_snapshot_protocol.h:122-139): the
        /// command, two arguments and a page at bytes 1 to 4, and at byte 8
        /// the checksum 255 minus the sum of bytes 1 to 7. E5 and 8A fill
        /// bytes 9 to 64 with FF: the firmware answers in the buffer the
        /// request arrived in, so a read that comes before the answer returns
        /// the request, and the FF tail fails every value check.</summary>
        public static byte[] Request(byte command, byte a = 0, byte b = 0, byte page = 0)
        {
            var r = new byte[ReportLength];
            r[1] = command;
            r[2] = a;
            r[3] = b;
            r[4] = page;
            int sum = 0;
            for (int i = 1; i <= 7; i++) sum += r[i];
            r[8] = (byte)((255 - sum) & 0xFF);
            if (command == 0xE5 || command == 0x8A)
                for (int i = 9; i < ReportLength; i++) r[i] = 0xFF;
            return r;
        }

        /// <summary>A 4-byte action code as a key code
        /// (rongyuan_snapshot_protocol.h:11-15): 00 00 usage 00 is that HID
        /// usage, 0A 01 is Fn (0x409), and everything else (consumer keys,
        /// macros, encoders, lighting) is 0 and never publishes.</summary>
        public static int Decode(ReadOnlySpan<byte> action)
        {
            if (action.Length < 4) return 0;
            if (action[0] == 0 && action[1] == 0 && action[3] == 0) return action[2];
            if (action[0] == 10 && action[1] == 1) return AnalogKeyCodes.Fn;
            return 0;
        }

        /// <summary>The code each slot publishes: its assignment on a slot the
        /// board's table fills, 0 elsewhere and where the assignment decodes
        /// to nothing (Map, rongyuan_snapshot_backend.cpp:253-265).</summary>
        public static void Publication(IReadOnlyList<int> factory, IReadOnlyList<int> assigned, int[] publish)
        {
            for (int slot = 0; slot < publish.Length; slot++)
                publish[slot] = slot < factory.Count && factory[slot] != 0 && slot < assigned.Count
                    ? assigned[slot]
                    : 0;
        }

        /// <summary>The board ID of an 8F answer, little-endian at bytes 2 to
        /// 5, or 0 when the buffer is not one (rongyuan_snapshot_protocol.h:116-121).</summary>
        public static uint Board(ReadOnlySpan<byte> reply)
            => reply.Length >= 6 && reply[0] == 0 && reply[1] == 0x8F
                ? (uint)(reply[2] | reply[3] << 8 | reply[4] << 16 | reply[5] << 24)
                : 0;

        /// <summary>Units per millimeter from a firmware version when no E6
        /// answer gives them: 200 from 5.00, 100 from 3.00, else 10
        /// (rongyuan_snapshot_protocol.h:145).</summary>
        public static int LegacyUnits(int version) => version >= 0x500 ? 200 : version >= 0x300 ? 100 : 10;

        /// <summary>The snapshot route's unit rule (rongyuan_snapshot_protocol.h:140-146):
        /// a valid E6 answer (E6 AA) gives 100, 200 or 1000 per millimeter for
        /// precision 0, 1 or 2 and 0 past that, otherwise the version scale.</summary>
        public static int Units(int version, ReadOnlySpan<byte> features)
        {
            if (features.Length >= 4 && features[1] == 0xE6 && features[2] == 0xAA)
            {
                return features[3] switch
                {
                    0 => 100,
                    1 => 200,
                    2 => 1000,
                    _ => 0,
                };
            }
            return LegacyUnits(version);
        }

        /// <summary>The stream route's unit rule (rongyuan_stream_protocol.h:277-281):
        /// a valid E6 answer as in <see cref="Units"/>. Without one, 0 when the
        /// row needs the precision answer or the version is 0 or FFFF, else
        /// the version scale.</summary>
        public static int StreamUnits(int version, ReadOnlySpan<byte> features, bool precisionRequired)
        {
            if (features.Length >= 4 && features[1] == 0xE6 && features[2] == 0xAA) return Units(version, features);
            if (precisionRequired || version == 0 || version == 0xFFFF) return 0;
            return LegacyUnits(version);
        }

        /// <summary>A travel page (rongyuan_snapshot_protocol.h:147-158): 32
        /// little-endian values after report ID 0, none past six millimeters.
        /// <paramref name="values"/> changes only when the page is valid.</summary>
        public static bool ParseTravel(ReadOnlySpan<byte> reply, int units, Span<int> values)
        {
            if (reply.Length < ReportLength || values.Length < 32 || reply[0] != 0 || units <= 0) return false;
            Span<int> page = stackalloc int[32];
            for (int i = 0; i < 32; i++)
            {
                page[i] = reply[1 + 2 * i] | reply[2 + 2 * i] << 8;
                if (page[i] > 6 * units) return false;
            }
            page.CopyTo(values);
            return true;
        }

        /// <summary>An 8A assignment page (rongyuan_snapshot_protocol.h:159-166):
        /// report ID 0 and sixteen 4-byte codes, each with a type byte of 0x20
        /// or less, a last byte other than FF, and a last byte of 0 whenever
        /// the type is 0.</summary>
        public static bool ValidAssignments(ReadOnlySpan<byte> reply)
        {
            if (reply.Length < ReportLength || reply[0] != 0) return false;
            for (int i = 1; i < ReportLength; i += 4)
                if (reply[i] > 0x20 || reply[i + 3] == 0xFF || (reply[i] == 0 && reply[i + 3] != 0))
                    return false;
            return true;
        }

        /// <summary>Depth in thousandths, rounded, of <paramref name="value"/>
        /// units against a range in micrometers, 64-bit integer math
        /// (rongyuan_snapshot_protocol.h:167-174).</summary>
        public static int Normalize(int value, int units, int rangeUm)
        {
            if (units <= 0 || rangeUm <= 0 || value <= 0) return 0;
            ulong divisor = (ulong)units * (ulong)rangeUm;
            return (int)Math.Min(1000UL, ((ulong)value * 1000000UL + divisor / 2) / divisor);
        }

        /// <summary>A stream event (rongyuan_stream_protocol.h:272-276): exactly
        /// 32 bytes, report ID 5, event type 1B, a slot under 128 at byte 4
        /// and a little-endian depth at bytes 2 and 3. Bytes 5 to 31 are
        /// padding the vendor's decoders ignore.</summary>
        public static bool ParseStreamEvent(ReadOnlySpan<byte> report, out int slot, out int raw)
        {
            slot = 0;
            raw = 0;
            if (report.Length != 32 || report[0] != 5 || report[1] != 0x1B || report[4] >= 128) return false;
            slot = report[4];
            raw = report[2] | report[3] << 8;
            return true;
        }
    }

    /// <summary>
    /// The request and answer loop both RongYuan routes share
    /// (rongyuan_snapshot_backend.cpp:80-95 and 176-197,
    /// rongyuan_stream_backend.cpp:87-105 and 205-227). A query writes one
    /// request, then reads until the validator accepts an answer, 1 ms
    /// apart, for 50 ms after the write. A read counts only when the driver
    /// returns all 64 payload bytes behind report ID 0. A failed write or
    /// read, or a required query that runs out of time, poisons the channel,
    /// and every later query fails at once. An optional query that runs out
    /// of time just returns false.
    /// </summary>
    public sealed class RongYuanCommandChannel
    {
        public const int ReportLength = 65;
        /// <summary>The byte count Windows reports for a full unnumbered
        /// 64-byte feature report (rongyuan_snapshot_backend.cpp:94).</summary>
        public const int AnswerBytes = 64;
        public const int QueryWindowMs = 50;
        public const int RetryWaitMs = 1;

        private readonly Func<long> _clock;
        private readonly Action<int> _wait;

        public RongYuanCommandChannel(Func<long> clock, Action<int> wait)
        {
            _clock = clock;
            _wait = wait;
        }

        public bool Poisoned { get; private set; }

        public static bool Write(IAnalogKeyboardTransport io, byte[] request) => io.SetFeature(request);

        /// <summary>One feature read into a zeroed buffer.</summary>
        public static bool Read(IAnalogKeyboardTransport io, byte[] reply)
        {
            Array.Clear(reply);
            return io.GetFeature(reply) == AnswerBytes && reply[0] == 0;
        }

        public bool Query(IAnalogKeyboardTransport io, byte[] request, byte[] reply, Func<byte[], bool> valid,
            bool optional = false)
        {
            if (Poisoned) return false;
            if (!Write(io, request))
            {
                Poisoned = true;
                return false;
            }
            long deadline = _clock() + QueryWindowMs;
            do
            {
                if (!Read(io, reply))
                {
                    Poisoned = true;
                    return false;
                }
                if (valid(reply)) return true;
                _wait(RetryWaitMs);
            } while (_clock() < deadline);
            if (!optional) Poisoned = true;
            return false;
        }

        /// <summary>The keyboard's current base-layer assignment of every
        /// slot (Map, rongyuan_snapshot_backend.cpp:239-275 and
        /// rongyuan_stream_backend.cpp:288-325): 84 FF answers the active
        /// profile (0 to 7), then 8A profile FF page answers sixteen 4-byte
        /// codes per page for pages 0 to 7. Any failed page fails admission.
        /// HallJoy binds each slot to its factory key and to this assignment
        /// and reads the assignments whenever its automatic layout remaps,
        /// its default (Get, rongyuan_snapshot_backend.cpp:41-43, 457-465,
        /// native_layout_state.h:30-33, keyboard_layout.cpp:1786-1787).
        /// PadForge has no layout presets, so the assignments name the keys
        /// on every board, and a slot assigned nothing decodable is not
        /// published. The layout list HallJoy publishes here would reject a
        /// key map with a repeated factory key, and none of the admitted
        /// boards that publish one has such a repeat, so reading the pages is
        /// the whole check.</summary>
        public bool ReadAssignments(IAnalogKeyboardTransport io, int[] assigned)
        {
            var reply = new byte[ReportLength];
            if (!Query(io, RongYuanProtocol.Request(0x84, 0xFF), reply, x => x[1] == 0x84 && x[2] < 8))
                return false;
            byte profile = reply[2];
            for (int page = 0; page < 8; page++)
            {
                if (!Query(io, RongYuanProtocol.Request(0x8A, profile, 0xFF, (byte)page), reply,
                        x => RongYuanProtocol.ValidAssignments(x)))
                    return false;
                for (int k = 0; k < 16; k++)
                    assigned[page * 16 + k] = RongYuanProtocol.Decode(reply.AsSpan(1 + k * 4, 4));
            }
            return true;
        }
    }

    /// <summary>
    /// MonsGeek M1 V5 HE and EPOMAKER G84 HE on 3151:5030, HallJoy's
    /// "rongyuan-snapshot" route (rongyuan_snapshot_backend.cpp). Start
    /// proves the board: 8F must answer one of the three boards, the unit
    /// scale must resolve (an optional E6, else the firmware version), and
    /// all four E5 FE 01 travel pages must parse. Then it reads the
    /// assignment pages. A pass reads the next travel page in turn, 0 to 3,
    /// 1 ms after the last one, and a key reads 0 once its page is older
    /// than 150 ms (rongyuan_snapshot_backend.cpp:32, 319-346, 460-465).
    /// Every command is a read. Nothing is sent at shutdown.
    /// </summary>
    public sealed class RongYuanSnapshotSession : AnalogKeyboardSession
    {
        public const int FreshMs = 150;
        public const int PageWaitMs = 1;

        private readonly Func<long> _clock;
        private readonly Action<int> _wait;
        private readonly RongYuanPreciseDelay _precise;
        private readonly RongYuanCommandChannel _channel;
        private readonly byte[] _reply = new byte[RongYuanProtocol.ReportLength];
        private readonly int[] _values = new int[32];
        private readonly int[] _milli = new int[128];
        private readonly bool[] _seen = new bool[4];
        private readonly long[] _stamp = new long[4];
        private readonly int[] _assigned = new int[128];
        private readonly int[] _publish = new int[128];
        private RongYuanSnapshotModel _model;
        private int[] _keyOrder;
        private int _version;
        private int _units;
        private int _page;
        private bool _polling;

        /// <param name="clock">Milliseconds, GetTickCount64's scale.</param>
        /// <param name="wait">The 1 ms waits. Null waits on a
        /// high-resolution timer.</param>
        public RongYuanSnapshotSession(Func<long> clock = null, Action<int> wait = null)
        {
            _clock = clock ?? (() => Environment.TickCount64);
            if (wait == null)
            {
                _precise = new RongYuanPreciseDelay();
                _wait = _precise.Wait;
            }
            else
            {
                _wait = wait;
            }
            _channel = new RongYuanCommandChannel(_clock, _wait);
        }

        public RongYuanSnapshotModel Model => _model;
        public int FirmwareVersion => _version;
        public int UnitsPerMillimeter => _units;
        public bool Poisoned => _channel.Poisoned;

        /// <summary>The key each slot is assigned on the keyboard's active
        /// profile, from the 8A pages.</summary>
        public IReadOnlyList<int> AssignedCodes => _assigned;

        /// <summary>The key code each slot publishes.</summary>
        public IReadOnlyList<int> PublishedCodes => _publish;

        public override string ModelName => _model?.Name;
        public override int[] KeyOrder => _keyOrder;

        public override bool Start(IAnalogKeyboardTransport io)
        {
            if (!Proof(io) || !_channel.ReadAssignments(io, _assigned))
            {
                _precise?.Dispose();
                return false;
            }
            RongYuanProtocol.Publication(_model.Codes, _assigned, _publish);
            _keyOrder = AnalogKeyboardData.KeysOf(_publish);
            return true;
        }

        /// <summary>Proof (rongyuan_snapshot_backend.cpp:217-238).</summary>
        private bool Proof(IAnalogKeyboardTransport io)
        {
            if (!_channel.Query(io, RongYuanProtocol.Request(0x8F), _reply,
                    x => RongYuanCatalog.FindSnapshot(RongYuanProtocol.Board(x)) != null))
                return false;
            _model = RongYuanCatalog.FindSnapshot(RongYuanProtocol.Board(_reply));
            _version = _reply[8] | _reply[9] << 8;
            // Old firmware has no E6 answer and uses its version's scale. The
            // rule reads whatever the query left in the buffer, as HallJoy's does.
            var features = new byte[RongYuanProtocol.ReportLength];
            _channel.Query(io, RongYuanProtocol.Request(0xE6), features, x => x[1] == 0xE6 && x[2] == 0xAA,
                optional: true);
            _units = RongYuanProtocol.Units(_version, features);
            if (_units == 0) return false;
            for (int page = 0; page < 4; page++)
                if (!Travel(io, page)) return false;
            return true;
        }

        private bool Travel(IAnalogKeyboardTransport io, int page)
            => _channel.Query(io, RongYuanProtocol.Request(0xE5, 0xFE, 1, (byte)page), _reply,
                x => RongYuanProtocol.ParseTravel(x, _units, _values));

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            if (_model == null) return AnalogPollResult.Failed;
            if (_polling) _wait(PageWaitMs);
            _polling = true;
            // A failed query ends HallJoy's session (lines 322-325).
            if (!Travel(io, _page)) return AnalogPollResult.Failed;
            long now = _clock();
            var codes = _model.Codes;
            for (int i = 0; i < 32; i++)
            {
                int slot = _page * 32 + i;
                if (codes[slot] != 0) _milli[slot] = RongYuanProtocol.Normalize(_values[i], _units, _model.RangeUm);
            }
            _seen[_page] = true;
            _stamp[_page] = now;
            _page = (_page + 1) % 4;
            Compose(output, now);
            return AnalogPollResult.Ok;
        }

        /// <summary>A key reads the largest depth of its slots whose page is
        /// at most 150 ms old (physical_analog_state.h:39-52).</summary>
        private void Compose(AnalogKeyInputState output, long now)
        {
            output.ResetForReuse();
            var codes = _publish;
            for (int slot = 0; slot < 128; slot++)
            {
                int code = codes[slot];
                if (code == 0 || _milli[slot] == 0) continue;
                int page = slot / 32;
                if (!_seen[page] || now < _stamp[page] || now - _stamp[page] > FreshMs) continue;
                float depth = _milli[slot] / 1000f;
                if (depth > output.Get(code)) output.Set(code, depth);
            }
        }

        public override void Stop(IAnalogKeyboardTransport io) => _precise?.Dispose();
    }

    /// <summary>
    /// RY5088 boards of many brands, HallJoy's "rongyuan-stream" route
    /// (rongyuan_stream_backend.cpp). Start proves the board on the control
    /// collection: 8F must answer a board that one profile row lists for the
    /// collection's USB identity, an optional 80 answer's radio version
    /// replaces the firmware version for the unit scale, and an optional E6
    /// answer gives the precision, which some rows require. Then it reads
    /// the assignment pages, flushes the input queue and sends 1B 01, which
    /// turns on the event stream. Each event on the paired input collection
    /// is one key's new depth, so a key keeps its depth until its next event
    /// (rongyuan_stream_backend.cpp:37). Stop sends 1B 00 whenever the enable
    /// was attempted, including when that write failed, since a timed-out
    /// write may still have reached the keyboard (lines 183-188, 232-236).
    /// </summary>
    public sealed class RongYuanStreamSession : AnalogKeyboardSession
    {
        public const int ReportLength = 32;
        public const byte StreamReportId = 5;
        public const byte EventType = 0x1B;

        /// <summary>Longest single wait for an event, so the reader sees a
        /// stop request. A keyboard at rest sends nothing, which is a quiet
        /// pass, not a miss.</summary>
        public const int WaitMs = 250;

        private readonly ushort _vendorId;
        private readonly ushort _productId;
        private readonly Action<int> _wait;
        private readonly RongYuanPreciseDelay _precise;
        private readonly RongYuanCommandChannel _channel;
        private readonly byte[] _reply = new byte[RongYuanProtocol.ReportLength];
        private readonly int[] _milli = new int[128];
        private readonly int[] _assigned = new int[128];
        private readonly int[] _publish = new int[128];
        private RongYuanStreamModel _model;
        private int[] _keyOrder;
        private int _usbVersion;
        private int _radioVersion;
        private int _units;
        private bool _enabled;

        /// <param name="vendorId">The control collection's vendor ID.</param>
        /// <param name="productId">The control collection's product ID. A
        /// board is admitted only under a USB identity its row or an alias
        /// lists.</param>
        /// <param name="clock">Milliseconds, GetTickCount64's scale.</param>
        /// <param name="wait">The 1 ms waits. Null waits on a
        /// high-resolution timer.</param>
        public RongYuanStreamSession(ushort vendorId, ushort productId, Func<long> clock = null,
            Action<int> wait = null)
        {
            _vendorId = vendorId;
            _productId = productId;
            clock ??= () => Environment.TickCount64;
            if (wait == null)
            {
                _precise = new RongYuanPreciseDelay();
                _wait = _precise.Wait;
            }
            else
            {
                _wait = wait;
            }
            _channel = new RongYuanCommandChannel(clock, _wait);
        }

        public RongYuanStreamModel Model => _model;
        public int UsbVersion => _usbVersion;
        /// <summary>The radio or controller firmware version from 80, or 0
        /// when absent.</summary>
        public int RadioVersion => _radioVersion;
        public int UnitsPerMillimeter => _units;
        public bool StreamEnabled => _enabled;
        public bool Poisoned => _channel.Poisoned;
        public IReadOnlyList<int> AssignedCodes => _assigned;

        /// <summary>The key code each slot publishes.</summary>
        public IReadOnlyList<int> PublishedCodes => _publish;

        public override string ModelName => _model?.Name;
        public override int[] KeyOrder => _keyOrder;

        public override bool Start(IAnalogKeyboardTransport io)
        {
            if (!Proof(io) || !_channel.ReadAssignments(io, _assigned))
            {
                _precise?.Dispose();
                return false;
            }
            RongYuanProtocol.Publication(_model.Codes, _assigned, _publish);
            _keyOrder = AnalogKeyboardData.KeysOf(_publish);
            // HallJoy opens the input collection, sets its 128 buffers and
            // flushes its queue right before the enable (lines 228-231, 342).
            io.DiscardStale();
            // The device calls Stop only after a successful Start, so a failed
            // enable is undone here, the way HallJoy's Session destructor
            // undoes it at the end of every Run.
            _enabled = true;
            bool enabled;
            try { enabled = RongYuanCommandChannel.Write(io, RongYuanProtocol.Request(0x1B, 1)); }
            catch { enabled = false; }
            if (!enabled)
            {
                Disable(io);
                _precise?.Dispose();
                return false;
            }
            return true;
        }

        /// <summary>Proof (rongyuan_stream_backend.cpp:264-287).</summary>
        private bool Proof(IAnalogKeyboardTransport io)
        {
            if (!_channel.Query(io, RongYuanProtocol.Request(0x8F), _reply,
                    x => RongYuanCatalog.FindStream(RongYuanProtocol.Board(x), _vendorId, _productId) != null))
                return false;
            _model = RongYuanCatalog.FindStream(RongYuanProtocol.Board(_reply), _vendorId, _productId);
            _usbVersion = _reply[8] | _reply[9] << 8;
            int version = _usbVersion;
            // The vendor's scale follows the radio or controller firmware when
            // it answers, even on wired USB, never the USB bcdDevice (lines 272-277).
            var rf = new byte[RongYuanProtocol.ReportLength];
            if (_channel.Query(io, RongYuanProtocol.Request(0x80), rf, x => x[1] == 0x80, optional: true))
            {
                int radio = rf[2] | rf[3] << 8;
                if (radio != 0 && radio != 0xFFFF)
                {
                    _radioVersion = radio;
                    version = radio;
                }
            }
            var features = new byte[RongYuanProtocol.ReportLength];
            _channel.Query(io, RongYuanProtocol.Request(0xE6), features, x => x[1] == 0xE6 && x[2] == 0xAA,
                optional: true);
            _units = RongYuanProtocol.StreamUnits(version, features, _model.PrecisionEnum);
            return _units != 0;
        }

        /// <summary>One event (rongyuan_stream_backend.cpp:372-394). Report 5
        /// with another event type is a vendor notification and is skipped.
        /// Anything else that does not parse, or a depth past six
        /// millimeters, ends the session. An event for a slot with no key is
        /// skipped (line 328).</summary>
        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            if (_model == null) return AnalogPollResult.Failed;
            int n = io.Receive(Buffer, WaitMs);
            if (n < 0) return AnalogPollResult.Failed;
            if (n == 0) return AnalogPollResult.Idle;
            if (n == ReportLength && Buffer[0] == StreamReportId && Buffer[1] != EventType)
                return AnalogPollResult.Idle;
            if (!RongYuanProtocol.ParseStreamEvent(Buffer.AsSpan(0, n), out int slot, out int raw))
                return AnalogPollResult.Failed;
            if (raw > 6 * _units) return AnalogPollResult.Failed;
            if (_model.Codes[slot] == 0) return AnalogPollResult.Idle;
            _milli[slot] = RongYuanProtocol.Normalize(raw, _units, _model.RangeUm);
            Compose(output);
            return AnalogPollResult.Ok;
        }

        /// <summary>Every key at the largest depth of its slots. Depths never
        /// expire: a held key sends no events (rongyuan_stream_backend.cpp:37).</summary>
        private void Compose(AnalogKeyInputState output)
        {
            output.ResetForReuse();
            var codes = _publish;
            for (int slot = 0; slot < 128; slot++)
            {
                int code = codes[slot];
                if (code == 0 || _milli[slot] == 0) continue;
                float depth = _milli[slot] / 1000f;
                if (depth > output.Get(code)) output.Set(code, depth);
            }
        }

        public override void Stop(IAnalogKeyboardTransport io)
        {
            Disable(io);
            _precise?.Dispose();
        }

        /// <summary>SET 1B 00 once, best effort (rongyuan_stream_backend.cpp:183-188).</summary>
        private void Disable(IAnalogKeyboardTransport io)
        {
            if (!_enabled) return;
            _enabled = false;
            try { RongYuanCommandChannel.Write(io, RongYuanProtocol.Request(0x1B, 0)); }
            catch { }
        }
    }
}
