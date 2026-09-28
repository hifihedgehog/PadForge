using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PadForge.Engine.Common.AnalogKeyboard.Routes
{
    /// <summary>The microsecond clock the Addressed and HERO sessions time
    /// their rules with, HallJoy's NowUs over the performance counter
    /// (addressed_analog_backend.cpp:240-248).</summary>
    public static class AddressedClock
    {
        public static long Microseconds()
        {
            long ticks = Stopwatch.GetTimestamp();
            long frequency = Stopwatch.Frequency;
            return ticks / frequency * 1_000_000 + ticks % frequency * 1_000_000 / frequency;
        }
    }

    /// <summary>
    /// The IPI side of the Addressed protocol, HallJoy's ipi_protocol.h
    /// (AGPL-3.0): the key code decode, the stored calibration check and the
    /// normalization, and the record parsers of the three setup reads.
    /// </summary>
    public static class AddressedIpiProtocol
    {
        /// <summary>Each setup read (UUID, map batch, calibration batch) gets
        /// two attempts of 40 ms (addressed_analog_backend.cpp:488-491, 524-527).</summary>
        public const int SetupWindowMs = 40;
        public const int SetupAttempts = 2;

        /// <summary>
        /// A key map record's 32-bit key code to a key code (ipi::Hid,
        /// ipi_protocol.h:65-72): below 256 a keyboard usage, 0x0D000000 the
        /// vendor Fn key, a single modifier bit in bits 16 to 23 the modifier
        /// usage 0xE0 to 0xE7, and anything else (macros, media, compound
        /// bindings) 0, unassigned.
        /// </summary>
        public static int Hid(uint keycode)
        {
            if (keycode < 256) return (int)keycode;
            if (keycode == 0x0D000000u) return AnalogKeyCodes.Fn;
            uint bits = keycode >> 16;
            if ((keycode & 0xFFFF) != 0 || bits == 0 || bits > 128 || (bits & (bits - 1)) != 0) return 0;
            for (int i = 0; i < 8; i++)
                if (bits == 1u << i) return 0xE0 + i;
            return 0;
        }

        /// <summary>A stored calibration HallJoy trusts (ipi::Valid,
        /// ipi_protocol.h:73-75): a nonzero bottom, a released value within 15
        /// bits, and more than 256 counts of travel between them.</summary>
        public static bool Valid(ushort released, ushort bottom)
            => bottom > 0 && released <= 0x7FFF && released > bottom + 256;

        /// <summary>
        /// A raw sample to depth in thousandths against the key's stored
        /// calibration (ipi::Normalise, ipi_protocol.h:108-114). Raw falls as
        /// the key goes down: at or above the released value it is 0, at or
        /// below the bottom 1000, between them rounded half up, and under 8
        /// it is rest.
        /// </summary>
        public static ushort Normalize(ushort raw, ushort released, ushort bottom)
        {
            if (raw == 0 || !Valid(released, bottom) || raw >= released) return 0;
            if (raw <= bottom) return 1000;
            uint span = (uint)(released - bottom);
            ushort value = (ushort)(((uint)(released - raw) * 1000u + span / 2u) / span);
            return value < 8 ? (ushort)0 : value;
        }

        /// <summary>
        /// A <c>83 00</c> answer (ipi::Map, ipi_protocol.h:76-83): each
        /// record's big-endian 32-bit key code, decoded, into
        /// <paramref name="map"/> at the record's key ID. Validated in full
        /// first, so a bad answer changes nothing.
        /// </summary>
        public static bool Map(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> ids, int[] map)
        {
            if (!AddressedFrame.Records(frame, AddressedFrame.MapCommand, AddressedFrame.MapSubcommand, ids))
                return false;
            for (int i = 0; i < ids.Length; i++)
            {
                int at = AddressedFrame.DataOffset + i * AddressedFrame.RecordLength;
                map[AddressedFrame.RecordId(frame, i)] = Hid(BigEndian32(frame, at + 2));
            }
            return true;
        }

        /// <summary>
        /// A <c>94 05</c> answer (ipi::Calibrations, ipi_protocol.h:84-95):
        /// each record carries the key ID, the stored released value and the
        /// stored bottom value, big-endian. One record HallJoy would not trust
        /// rejects the whole answer, and nothing is stored unless all pass.
        /// </summary>
        public static bool Calibrations(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> ids,
            ushort[] released, ushort[] bottom)
        {
            if (!AddressedFrame.Records(frame, AddressedFrame.CalibrationCommand,
                    AddressedFrame.CalibrationSubcommand, ids))
                return false;
            for (int i = 0; i < ids.Length; i++)
            {
                int at = AddressedFrame.DataOffset + i * AddressedFrame.RecordLength;
                if (!Valid(BigEndian16(frame, at + 2), BigEndian16(frame, at + 4))) return false;
            }
            for (int i = 0; i < ids.Length; i++)
            {
                int at = AddressedFrame.DataOffset + i * AddressedFrame.RecordLength;
                int id = AddressedFrame.RecordId(frame, i);
                released[id] = BigEndian16(frame, at + 2);
                bottom[id] = BigEndian16(frame, at + 4);
            }
            return true;
        }

        /// <summary>
        /// The IPI check on a <c>94 02</c> answer (ipi::Samples,
        /// ipi_protocol.h:96-107): the full header, the exact requested set,
        /// and a nonzero raw value (bits 14 to 0) in every record. Bit 15 is a
        /// status flag and the last two bytes a second field, and HallJoy
        /// uses neither.
        /// </summary>
        public static bool Samples(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> ids)
        {
            if (!AddressedFrame.Records(frame, AddressedFrame.SampleCommand, AddressedFrame.SampleSubcommand, ids))
                return false;
            for (int i = 0; i < ids.Length; i++)
            {
                int at = AddressedFrame.DataOffset + i * AddressedFrame.RecordLength;
                if ((BigEndian16(frame, at + 2) & 0x7FFF) == 0) return false;
            }
            return true;
        }

        public static ushort BigEndian16(ReadOnlySpan<byte> frame, int at) => (ushort)((frame[at] << 8) | frame[at + 1]);

        public static uint BigEndian32(ReadOnlySpan<byte> frame, int at)
            => (uint)frame[at] << 24 | (uint)frame[at + 1] << 16 | (uint)frame[at + 2] << 8 | frame[at + 3];
    }

    /// <summary>
    /// The generic side of the Addressed protocol, for keyboards HallJoy
    /// admits by probe alone (addressed_analog_backend.cpp:565-599, 655-680,
    /// 870-893).
    /// </summary>
    public static class AddressedGenericProtocol
    {
        /// <summary>Up to three empty map reads, 35 ms each
        /// (addressed_analog_backend.cpp:55, 706-724).</summary>
        public const int MapWindowMs = 35;
        public const int MapAttempts = 3;

        /// <summary>A device map with this many entries defines the profile
        /// on its own (addressed_analog_backend.cpp:668).</summary>
        public const int DeviceMapMinimum = 20;

        /// <summary>W, A, S, D: the keys the probe asks for
        /// (addressed_analog_backend.cpp:726).</summary>
        public static readonly ushort[] ProbeHids = { AnalogKeyCodes.W, AnalogKeyCodes.A, AnalogKeyCodes.S, AnalogKeyCodes.D };

        /// <summary>
        /// A map answer to the empty <c>83 00</c> (ParseMapPacket,
        /// addressed_analog_backend.cpp:565-590). HallJoy reads each 6-byte
        /// entry little-endian from byte 8: the key ID from bytes 8 and 9 and
        /// the usage from bytes 12 and 13. Against the firmware's big-endian
        /// records that yields the right key for plain usages below 256 and
        /// drops modifier bitmasks and Fn, which HallJoy's audit records
        /// (spec 5.2). Kept as HallJoy reads it. Entries with a key ID or
        /// usage of 0 or 256 and up are skipped. Returns how many stored
        /// usages changed.
        /// </summary>
        public static int ParseMapPacket(ReadOnlySpan<byte> frame, ushort[] keyToHid)
        {
            if (frame.Length < AddressedFrame.Length || frame[0] != AddressedFrame.ReportId
                || frame[1] != AddressedFrame.MapCommand || frame[2] != AddressedFrame.MapSubcommand
                || frame[3] != 0x00 || frame[4] != 0x01 || frame[5] != 0x00)
                return 0;
            int length = frame[6] | (frame[7] << 8);
            if (length == 0 || length > AddressedFrame.MaxDataLength || length % AddressedFrame.RecordLength != 0)
                return 0;
            int updates = 0;
            for (int off = 0; off + 5 < length; off += AddressedFrame.RecordLength)
            {
                int keyId = frame[8 + off] | (frame[9 + off] << 8);
                int hid = frame[12 + off] | (frame[13 + off] << 8);
                if (keyId == 0 || keyId >= 256 || hid == 0 || hid >= 256) continue;
                if (keyToHid[keyId] == hid) continue;
                keyToHid[keyId] = (ushort)hid;
                updates++;
            }
            return updates;
        }

        /// <summary>The lowest key ID the device map gives a usage, else the
        /// canonical one, else 0 (FindKeyIdForHid, addressed_analog_backend.cpp:592-599).</summary>
        public static byte FindKeyIdForHid(ushort[] map, ushort hid)
        {
            for (int keyId = 1; keyId < 256; keyId++)
                if (map[keyId] == hid) return (byte)keyId;
            int[] table = AddressedRoutes.CanonicalTable;
            foreach (int keyId in AddressedRoutes.CanonicalOrder)
                if (table[keyId] == hid) return (byte)keyId;
            return 0;
        }

        /// <summary>
        /// The keys a generic session polls (BuildProfile,
        /// addressed_analog_backend.cpp:655-680). A map of 20 or more entries
        /// is the profile on its own, every mapped key ID ascending. Otherwise
        /// the 82 canonical IDs in their source order, each taking the map's
        /// usage where it has one. The count is of map changes, not distinct
        /// keys, as HallJoy counts it.
        /// </summary>
        public static List<(byte KeyId, ushort Hid)> BuildProfile(ushort[] dynamicMap, int dynamicEntries,
            out string source)
        {
            var profile = new List<(byte, ushort)>();
            if (dynamicEntries >= DeviceMapMinimum)
            {
                for (int keyId = 1; keyId < 256; keyId++)
                    if (dynamicMap[keyId] != 0) profile.Add(((byte)keyId, dynamicMap[keyId]));
                source = "device-map";
                return profile;
            }
            int[] table = AddressedRoutes.CanonicalTable;
            foreach (int keyId in AddressedRoutes.CanonicalOrder)
            {
                ushort hid = dynamicMap[keyId] != 0 ? dynamicMap[keyId] : (ushort)table[keyId];
                profile.Add(((byte)keyId, hid));
            }
            source = dynamicEntries != 0 ? "canonical+partial-map" : "canonical-fallback";
            return profile;
        }

        /// <summary>
        /// A raw sample to depth in thousandths with endpoints learned per key
        /// per session (Normalise, addressed_analog_backend.cpp:875-892): raw
        /// outside 500 to 20000 is 0, the first sample becomes the released
        /// value (held or not), the bottom starts 8400 below it (or at 500),
        /// and both widen with what the key reports. Under 8 is rest.
        /// </summary>
        public static ushort Normalize(byte keyId, ushort raw, ref ushort released, ref ushort bottom)
        {
            if (keyId == 0 || raw < 500 || raw > 20000) return 0;
            if (released == 0) released = raw;
            if (bottom == 0) bottom = released > 8400 ? (ushort)(released - 8400) : (ushort)500;
            if (raw > released && raw < 20000) released = raw;
            if (raw < bottom && raw > 500) bottom = raw;
            if (released <= bottom + 256 || raw >= released) return 0;
            uint den = (uint)(released - bottom);
            uint milli = Math.Min(1000u, ((uint)(released - raw) * 1000u + den / 2u) / den);
            return milli < 8 ? (ushort)0 : (ushort)milli;
        }
    }

    /// <summary>
    /// What the IPI and generic Addressed sessions share: the admission
    /// probe, the session start, and the <c>94 02</c> polling of
    /// RunSession (addressed_analog_backend.cpp:1302-1492) with the
    /// correlation rules of PublishResponse (:1008-1122).
    ///
    /// <para>HallJoy's worker sends a request and its reader thread accepts
    /// the one answer whose key IDs match it, within 8 ms plus a 20 ms late
    /// window, and drops everything else. A pass here does the same in one
    /// thread: it discards what queued up since the last request (the late
    /// answers HallJoy's reader drops), sends the plan, and reads for up to
    /// 28 ms, skipping frames that do not correlate. HallJoy sends the next
    /// request as soon as an answer arrives, so passes run back to back.</para>
    ///
    /// <para>A key's depth stays current for 500 ms after its last sample,
    /// and keys that share a code report the deepest of them
    /// (physical_analog_state.h:39-52). The session reports the keys under
    /// the codes the keyboard assigns them, which is HallJoy's reading with
    /// its default automatic layout (keyboard_layout.cpp:1786, 1981,
    /// addressed_analog_backend.cpp:1744-1754).</para>
    /// </summary>
    public abstract class AddressedPollingSession : AnalogKeyboardSession
    {
        public const int ResponseTimeoutMs = 8;
        public const int LateResponseWindowMs = 20;
        public const int MaxConsecutiveMisses = 8;
        public const long MaxNoResponseUs = 1_000_000;
        public const int ProbeWindowMs = 120;
        public const long FreshMs = 500;
        public const long BindingRefreshMs = 25;

        /// <summary>The probe asks for the profile's first four keys
        /// (addressed_analog_backend.cpp:697).</summary>
        public const int ProbeKeys = 4;

        protected const int NoFrame = -1, TimedOut = -2, Gone = -3;

        /// <summary>Microseconds, for the scheduler, the freshness window and
        /// the no-answer limit. Tests replace it.</summary>
        public Func<long> Clock { get; init; } = AddressedClock.Microseconds;

        private (byte KeyId, ushort Hid)[] _profile = Array.Empty<(byte, ushort)>();
        private AddressedPollScheduler _scheduler;
        private readonly AddressedPollPlan _plan = new();
        private readonly AddressedPublication _publication = new(FreshMs);
        private int[] _keyOrder;
        private long _lastBindMs;
        private long _lastGoodUs;

        public override int MissLimit => MaxConsecutiveMisses;
        public override int[] KeyOrder => _keyOrder;

        /// <summary>The keys the session polls, in profile order.</summary>
        public IReadOnlyList<(byte KeyId, ushort Hid)> Profile => _profile;

        public AddressedPollScheduler Scheduler => _scheduler;

        /// <summary>Each key's latest depth under the code it reports as.</summary>
        public AddressedPublication Publication => _publication;

        /// <summary>The last request's plan.</summary>
        public AddressedPollPlan LastPlan => _plan;

        /// <summary>A raw sample of one key to depth in thousandths.</summary>
        protected abstract ushort Normalize(byte keyId, ushort raw);

        /// <summary>The variant's own check on a <c>94 02</c> answer, run
        /// before the shared ID check.</summary>
        protected virtual bool SamplesValid(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> ids) => true;

        /// <summary>HallJoy's Transport::Send (addressed_analog_backend.cpp:409-422):
        /// WriteFile, and HidD_SetOutputReport when the write fails, with the
        /// frame laid out for the collection's output report length.</summary>
        public static bool SendFrame(IAnalogKeyboardTransport io, byte[] frame)
        {
            if (frame == null) return false;
            var wire = AddressedFrame.Wire(frame, io.OutputLength);
            return io.Send(wire) || io.SendOutputReport(wire);
        }

        /// <summary>One read before <paramref name="deadlineMs"/>
        /// (Environment.TickCount64): the frame's offset in the buffer, or
        /// NoFrame, TimedOut or Gone (ReadNextPayload,
        /// addressed_analog_backend.cpp:453-477).</summary>
        protected int ReadFrame(IAnalogKeyboardTransport io, long deadlineMs)
        {
            long remaining = deadlineMs - Environment.TickCount64;
            if (remaining <= 0) return TimedOut;
            int n = io.Receive(Buffer, (int)Math.Min(remaining, int.MaxValue));
            if (n < 0) return Gone;
            if (n == 0) return TimedOut;
            int at = AddressedFrame.Locate(Buffer.AsSpan(0, n));
            return at < 0 ? NoFrame : at;
        }

        /// <summary>
        /// The admission probe (ProbeAddressedResponse,
        /// addressed_analog_backend.cpp:601-653): one <c>94 02</c> for the
        /// given keys and up to 120 ms for an answer that correlates with it.
        /// </summary>
        protected bool Probe(IAnalogKeyboardTransport io, ReadOnlySpan<byte> ids)
        {
            if (!SendFrame(io, AddressedFrame.SampleRequest(ids))) return false;
            long deadline = Environment.TickCount64 + ProbeWindowMs;
            while (true)
            {
                int at = ReadFrame(io, deadline);
                if (at == Gone || at == TimedOut) return false;
                if (at == NoFrame) continue;
                if (AddressedFrame.ProbeAccepts(Buffer.AsSpan(at, AddressedFrame.Length), ids)) return true;
            }
        }

        /// <summary>
        /// Starts polling a proven keyboard, RunSession's opening
        /// (addressed_analog_backend.cpp:1304-1342): the scheduler over the
        /// profile, reset, then the one <c>09 98 02</c>, whose result HallJoy
        /// ignores and whose answer, if any, is dropped as not a <c>94 02</c>
        /// (addressed_analog_backend.cpp:1011, 1332).
        /// </summary>
        protected bool Begin(IAnalogKeyboardTransport io, IReadOnlyList<(byte KeyId, ushort Hid)> profile)
        {
            _profile = new (byte, ushort)[profile.Count];
            for (int i = 0; i < profile.Count; i++) _profile[i] = profile[i];
            // ResetPublished binds each key to its code when the code is
            // nonzero and below 0x410 (addressed_analog_backend.cpp:842-847).
            var table = new int[256];
            foreach (var (keyId, hid) in _profile)
                if (_publication.Bind(keyId, hid)) table[keyId] = hid;
            _keyOrder = AnalogKeyboardData.KeysOf(table);

            _scheduler = new AddressedPollScheduler(_profile);
            long now = Clock();
            _scheduler.Reset((ulong)now);
            SendFrame(io, AddressedFrame.SessionStart());
            _lastGoodUs = Clock();
            _lastBindMs = now / 1000 - BindingRefreshMs;
            return true;
        }

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            if (_scheduler == null) return AnalogPollResult.Failed;
            long nowUs = Clock();
            if (nowUs / 1000 - _lastBindMs >= BindingRefreshMs)
            {
                RefreshBindings(isHeld);
                _lastBindMs = nowUs / 1000;
            }
            _scheduler.BuildPlan((ulong)nowUs, _plan);
            if (_plan.Count == 0)
            {
                Thread.Sleep(1);
                return AnalogPollResult.Idle;
            }

            io.DiscardStale();
            // A failed send ends HallJoy's session (addressed_analog_backend.cpp:1376-1384).
            if (!SendFrame(io, AddressedFrame.SampleRequest(_plan.Ids))) return AnalogPollResult.Failed;

            long deadline = Environment.TickCount64 + ResponseTimeoutMs + LateResponseWindowMs;
            while (true)
            {
                int at = ReadFrame(io, deadline);
                if (at == Gone) return AnalogPollResult.Failed;
                if (at == TimedOut) break;
                if (at == NoFrame) continue;
                if (!Accept(Buffer.AsSpan(at, AddressedFrame.Length))) continue;
                _lastGoodUs = Clock();
                _publication.Fill(output, _lastGoodUs / 1000);
                return AnalogPollResult.Ok;
            }

            // A miss. HallJoy ends the session after 8 in a row, which the
            // reader's miss limit enforces, or after a second with no valid
            // answer (addressed_analog_backend.cpp:1422-1432).
            return Clock() - _lastGoodUs >= MaxNoResponseUs ? AnalogPollResult.Failed : AnalogPollResult.NoAnswer;
        }

        /// <summary>
        /// HallJoy's bound flag, refreshed every 25 ms
        /// (RefreshBindings, addressed_analog_backend.cpp:1227-1236), gives a
        /// key the shortest poll interval even at rest. HallJoy sets it for
        /// the keys its gamepad bindings use (Bindings_IsHidBound), and the
        /// device row answers the same question through
        /// <see cref="AnalogKeyboardSession.IsBound"/>: the keys a mapping
        /// reads. Without a row, the keys Windows sees held stand in.
        /// </summary>
        private void RefreshBindings(Func<int, bool> isHeld)
        {
            var isBound = IsBound;
            foreach (var (keyId, hid) in _profile)
            {
                bool bound = hid != 0 && (isBound != null ? isBound(hid) : isHeld != null && isHeld(hid));
                _scheduler.SetPhysicalBound(keyId, bound);
            }
        }

        /// <summary>PublishResponse's checks and, when they pass, its stores
        /// (addressed_analog_backend.cpp:1010-1109).</summary>
        private bool Accept(ReadOnlySpan<byte> frame)
        {
            if (frame[1] != AddressedFrame.SampleCommand || frame[2] != AddressedFrame.SampleSubcommand) return false;
            int payload = frame[6] | (frame[7] << 8);
            if (payload == 0 || payload > AddressedFrame.MaxDataLength || payload % AddressedFrame.RecordLength != 0)
                return false;
            if (payload / AddressedFrame.RecordLength != _plan.Count) return false;
            var ids = _plan.Ids;
            if (!SamplesValid(frame, ids)) return false;
            Span<bool> expected = stackalloc bool[256];
            Span<bool> seen = stackalloc bool[256];
            foreach (byte id in ids) expected[id] = true;
            for (int off = 0; off + 5 < payload; off += AddressedFrame.RecordLength)
            {
                byte id = frame[8 + off];
                if (!expected[id] || seen[id]) return false;
                seen[id] = true;
            }

            long receivedUs = Clock();
            for (int off = 0; off + 5 < payload; off += AddressedFrame.RecordLength)
            {
                byte id = frame[8 + off];
                ushort raw = (ushort)(((frame[9 + off] & 0x7F) << 8) | frame[10 + off]);
                ushort milli = Normalize(id, raw);
                // A key whose code is 0 is polled but not published
                // (addressed_analog_backend.cpp:1096-1102).
                _publication.Publish(id, milli, receivedUs / 1000);
                _scheduler.OnSample(id, raw, milli, (ulong)receivedUs);
            }
            return true;
        }

        /// <summary>
        /// HallJoy sends nothing when a session ends
        /// (addressed_analog_backend.cpp:1444-1466, 1661-1711), and the one
        /// state-changing command, <c>98 02</c>, has no counterpart it ever
        /// sends. So Stop writes nothing either.
        /// </summary>
        public override void Stop(IAnalogKeyboardTransport io) { }
    }

    /// <summary>The identity read both UUID-checking sessions share.</summary>
    public abstract class AddressedIdentitySession : AddressedPollingSession
    {
        /// <summary>Two attempts, each reading for 40 ms and skipping answers
        /// that are not the UUID (addressed_analog_backend.cpp:487-500). A
        /// failed send ends the attempts. 0 when no UUID arrived.</summary>
        protected ulong ReadUuid(IAnalogKeyboardTransport io)
        {
            for (int attempt = 0; attempt < AddressedIpiProtocol.SetupAttempts; attempt++)
            {
                if (!SendFrame(io, AddressedFrame.IdentityRequest())) return 0;
                long deadline = Environment.TickCount64 + AddressedIpiProtocol.SetupWindowMs;
                while (true)
                {
                    int at = ReadFrame(io, deadline);
                    if (at == Gone) return 0;
                    if (at == TimedOut) break;
                    if (at == NoFrame) continue;
                    ulong uuid = AddressedFrame.ParseUuid(Buffer.AsSpan(at, AddressedFrame.Length));
                    if (uuid != 0) return uuid;
                }
            }
            return 0;
        }
    }

    /// <summary>
    /// IPI keyboards on the Addressed protocol, admitted by UUID
    /// (ReadIpiProfile, addressed_analog_backend.cpp:479-563, then the probe
    /// at :694-702). Start reads the UUID with <c>82 01</c> and refuses a
    /// keyboard whose UUID is missing or not one of the eight models. It then
    /// reads the model's live key map (<c>83 00</c>) and stored calibration
    /// (<c>94 05</c>) nine keys at a time, map before calibration for each
    /// batch, probes with <c>94 02</c>, and sends <c>98 02</c>. Samples are
    /// normalized against the stored calibration.
    /// </summary>
    public sealed class AddressedIpiSession : AddressedIdentitySession
    {
        private AddressedModel _model;
        private readonly int[] _live = new int[256];
        private readonly ushort[] _released = new ushort[256];
        private readonly ushort[] _bottom = new ushort[256];

        public override string ModelName => _model?.Name;

        /// <summary>The model the UUID named, once Start succeeded.</summary>
        public AddressedModel Model => _model;

        public ulong Uuid { get; private set; }

        /// <summary>The key codes the keyboard assigns, by key ID.</summary>
        public IReadOnlyList<int> LiveMap => _live;

        public (ushort Released, ushort Bottom) CalibrationOf(byte keyId) => (_released[keyId], _bottom[keyId]);

        public override bool Start(IAnalogKeyboardTransport io)
        {
            ulong uuid = ReadUuid(io);
            // No UUID: HallJoy disables generic mapping for this shared USB
            // identity rather than guess (addressed_analog_backend.cpp:501-505).
            if (uuid == 0) return false;
            var model = AddressedRoutes.FindIpiModel(uuid);
            if (model == null) return false;

            var ids = new byte[model.Order.Length];
            for (int i = 0; i < ids.Length; i++) ids[i] = (byte)model.Order[i];
            for (int start = 0; start < ids.Length; start += AddressedFrame.MaxKeys)
            {
                int count = Math.Min(AddressedFrame.MaxKeys, ids.Length - start);
                if (!ReadBatch(io, ids, start, count, calibration: false)) return false;
                if (!ReadBatch(io, ids, start, count, calibration: true)) return false;
            }

            var profile = new (byte KeyId, ushort Hid)[ids.Length];
            for (int i = 0; i < ids.Length; i++) profile[i] = (ids[i], (ushort)_live[ids[i]]);
            if (!Probe(io, ids.AsSpan(0, Math.Min(ProbeKeys, ids.Length)))) return false;

            _model = model;
            Uuid = uuid;
            return Begin(io, profile);
        }

        /// <summary>One map or calibration batch, two attempts of 40 ms,
        /// skipping answers the parser refuses (addressed_analog_backend.cpp:518-545).</summary>
        private bool ReadBatch(IAnalogKeyboardTransport io, byte[] ids, int start, int count, bool calibration)
        {
            var batch = new ReadOnlySpan<byte>(ids, start, count);
            var request = calibration ? AddressedFrame.CalibrationRequest(batch) : AddressedFrame.MapRequest(batch);
            if (request == null) return false;
            for (int attempt = 0; attempt < AddressedIpiProtocol.SetupAttempts; attempt++)
            {
                if (!SendFrame(io, request)) return false;
                long deadline = Environment.TickCount64 + AddressedIpiProtocol.SetupWindowMs;
                while (true)
                {
                    int at = ReadFrame(io, deadline);
                    if (at == Gone) return false;
                    if (at == TimedOut) break;
                    if (at == NoFrame) continue;
                    var frame = Buffer.AsSpan(at, AddressedFrame.Length);
                    bool accepted = calibration
                        ? AddressedIpiProtocol.Calibrations(frame, batch, _released, _bottom)
                        : AddressedIpiProtocol.Map(frame, batch, _live);
                    if (accepted) return true;
                }
            }
            return false;
        }

        protected override ushort Normalize(byte keyId, ushort raw)
            => AddressedIpiProtocol.Normalize(raw, _released[keyId], _bottom[keyId]);

        protected override bool SamplesValid(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> ids)
            => AddressedIpiProtocol.Samples(frame, ids);
    }

    /// <summary>
    /// Keyboards admitted by the Addressed probe alone (ProbeCandidate,
    /// addressed_analog_backend.cpp:704-737). Start discovers what key map
    /// the keyboard offers with the empty <c>83 00</c>, probes with
    /// <c>94 02</c> for W, A, S and D, builds the profile, and sends
    /// <c>98 02</c>. HallJoy names no model here. Samples are normalized
    /// with endpoints learned during the session.
    ///
    /// <para>One addition to HallJoy's probe. <c>98 02</c> turns an
    /// Addressed keyboard's legacy last-key mode off (HallJoy decision D-085,
    /// docs/v1.4/DECISIONS.md:1844-1858), but AULA's HERO firmware reads
    /// <c>98 00</c> to <c>98 02</c> as its calibration-distance flow
    /// (docs/research/AULA_HERO84HE_FIRMWARE_2026-08-31.md:44-51), and a HERO
    /// answers the W, A, S and D probe the way an Addressed keyboard does. So
    /// on AULA's vendor ID a keyboard must first name an IPI model's UUID with
    /// <c>82 01</c>, the identity read both families answer. A keyboard of
    /// that vendor that names none is not probed further.</para>
    /// </summary>
    public sealed class AddressedGenericSession : AddressedIdentitySession
    {
        private readonly ushort _vendorId;
        private readonly ushort[] _released = new ushort[256];
        private readonly ushort[] _bottom = new ushort[256];

        public AddressedGenericSession(ushort vendorId = 0)
        {
            _vendorId = vendorId;
        }

        /// <summary>Map changes counted during discovery.</summary>
        public int MapEntries { get; private set; }

        /// <summary>"device-map", "canonical+partial-map" or
        /// "canonical-fallback", HallJoy's profile labels.</summary>
        public string ProfileSource { get; private set; }

        public override bool Start(IAnalogKeyboardTransport io)
        {
            if (_vendorId == AddressedRoutes.AulaVendorId
                && AddressedRoutes.FindIpiModel(ReadUuid(io)) == null)
            {
                NoStartRetry = true;
                return false;
            }
            var map = new ushort[256];
            int entries = 0;
            for (int attempt = 0; attempt < AddressedGenericProtocol.MapAttempts; attempt++)
            {
                if (!SendFrame(io, AddressedFrame.EmptyMapRequest())) break;
                long deadline = Environment.TickCount64 + AddressedGenericProtocol.MapWindowMs;
                int before = entries;
                while (true)
                {
                    int at = ReadFrame(io, deadline);
                    if (at == Gone) return false;
                    if (at == TimedOut) break;
                    if (at == NoFrame) continue;
                    entries += AddressedGenericProtocol.ParseMapPacket(Buffer.AsSpan(at, AddressedFrame.Length), map);
                }
                // A second or third read that adds nothing ends discovery.
                if (entries == before && attempt > 0) break;
            }

            Span<byte> probe = stackalloc byte[AddressedGenericProtocol.ProbeHids.Length];
            int count = 0;
            foreach (ushort hid in AddressedGenericProtocol.ProbeHids)
            {
                byte keyId = AddressedGenericProtocol.FindKeyIdForHid(map, hid);
                if (keyId != 0) probe[count++] = keyId;
            }
            if (count < 2) return false;
            // A probe set with a repeated ID can never pass the probe's
            // uniqueness check (addressed_analog_backend.cpp:636), so the
            // builder's refusal ends here with HallJoy's result.
            if (!Probe(io, probe.Slice(0, count))) return false;

            var profile = AddressedGenericProtocol.BuildProfile(map, entries, out string source);
            if (profile.Count == 0) return false;
            MapEntries = entries;
            ProfileSource = source;
            return Begin(io, profile);
        }

        protected override ushort Normalize(byte keyId, ushort raw)
            => AddressedGenericProtocol.Normalize(keyId, raw, ref _released[keyId], ref _bottom[keyId]);
    }
}
