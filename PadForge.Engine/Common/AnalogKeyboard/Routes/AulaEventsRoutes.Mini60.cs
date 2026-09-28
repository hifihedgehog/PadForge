using System;
using System.Collections.Generic;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>One MINI 60 model: its USB product ID, HallJoy's name for it,
    /// and the device-info product it must report, 0 when HallJoy does not
    /// check it (aula_mini60_native_model.h:14-19).</summary>
    public sealed record AulaMini60Model(ushort ProductId, string Name, int DeviceInfoProduct);

    /// <summary>One MINI 60 sample report (0xFB) after
    /// <see cref="AulaMini60Protocol.TryDecodeSample"/>. Only the key index,
    /// travel and stroke drive publication. The rest HallJoy validates or
    /// logs (aula_mini60_diagnostic_protocol.h:23-28).</summary>
    public readonly record struct AulaMini60Sample(int Key, int Status, int High, int Low, int Adc, int Travel,
        int Stroke);

    /// <summary>
    /// The AULA MINI 60 HE, HE Pro and HE MAX wire protocol, HallJoy's
    /// "aula-mini60-he-pro" route (aula_mini60_diagnostic.cpp:672-680), as pure
    /// functions. Requests are 65-byte output reports, report ID 0 and then
    /// the 0xAA marker. Replies and samples are 64-byte payloads that start
    /// with 0x55. Only four commands exist here: 0x10 device info, 0x12 assignment
    /// table, 0x66 and 0x67 simulation mode on and off
    /// (aula_mini60_diagnostic_protocol.h:7-16). The calibration pair 0x64 and
    /// 0x65 cannot be built.
    /// </summary>
    public static class AulaMini60Protocol
    {
        public const ushort VendorId = 0x0C45;
        public const ushort UsagePage = 0xFF68;
        public const ushort Usage = 0x0061;

        /// <summary>Input and output report length, report ID byte included
        /// (aula_mini60_diagnostic.cpp:108).</summary>
        public const int ReportLength = 65;
        public const int PayloadLength = 64;
        public const int InputBuffers = 512;

        public const byte RequestMarker = 0xAA;
        public const byte ReplyMarker = 0x55;
        public const byte CommandDeviceInfo = 0x10;
        public const byte CommandAssignments = 0x12;
        public const byte CommandSimulationOn = 0x66;
        public const byte CommandSimulationOff = 0x67;
        public const byte CommandSample = 0xFB;

        public const int DeviceInfoLength = 56;
        public const int ChunkLength = 56;
        public const int AssignmentBytes = 512;
        public const int Positions = 126;

        /// <summary>Device-info manufacturer every model reports
        /// (aula_mini60_native_model.h:18).</summary>
        public const int Manufacturer = 0x0166;

        /// <summary>The assignment usage byte of the vendor Fn action
        /// (aula_mini60_native_model.h:33-34).</summary>
        public const int FnAction = 0xAF;

        /// <summary>A key whose last sample is older than this reads 0
        /// (aula_mini60_native_model.h:20, 43-46).</summary>
        public const int FreshMs = 50;

        /// <summary>How long an exchange waits for its reply
        /// (aula_mini60_diagnostic.cpp:161).</summary>
        public const int ReplyWaitMs = 450;

        /// <summary>One read's wait (aula_mini60_diagnostic.cpp:130).</summary>
        public const int ReadWaitMs = 20;

        /// <summary>How long a cleanup waits for the 0x67 acknowledgement
        /// (aula_mini60_diagnostic.cpp:168).</summary>
        public const int CleanupAckMs = 180;

        /// <summary>How long streaming may run without a 0x66 acknowledgement
        /// or a sample before the session ends
        /// (aula_mini60_diagnostic.cpp:249, 254).</summary>
        public const int StartProofMs = 2000;

        /// <summary>Connection attempts per worker
        /// (aula_mini60_diagnostic.cpp:194).</summary>
        public const int ConnectionAttempts = 3;

        private const string TableName = "aula_mini60_factory";
        private const string ModelsName = "aula_mini60_models";

        private static readonly Lazy<IReadOnlyList<AulaMini60Model>> _models = new(LoadModels);

        /// <summary>The factory key at each HFD key index, 61 keys shared by
        /// every model (aula_mini60_native_model.h:21-23).</summary>
        public static int[] FactoryTable => AnalogKeyboardData.Table(AulaEventsRoutes.DataFile, TableName);

        /// <summary>The three admitted models (aula_mini60_native_model.h:6-13).</summary>
        public static IReadOnlyList<AulaMini60Model> Models => _models.Value;

        /// <summary>The model with <paramref name="productId"/>, or null. The
        /// 2.4 GHz receivers (0xFEFE, 0xFEFC) and the alternate PRO product ID
        /// 0x80B2 have none (aula_mini60_diagnostic.cpp:95, 104-105).</summary>
        public static AulaMini60Model Model(ushort productId)
        {
            foreach (var model in Models)
                if (model.ProductId == productId) return model;
            return null;
        }

        public static bool SupportedProduct(ushort productId) => Model(productId) != null;

        /// <summary>
        /// HallJoy's collection filter, Find (aula_mini60_diagnostic.cpp:80-112):
        /// a USB path naming VID 0C45 and a supported product ID, the same IDs
        /// in the attributes, usage FF68:0061 and 65-byte input and output
        /// reports. Feature length and interface number are not checked.
        ///
        /// <para>HallJoy sends no command when more than one collection
        /// qualifies (:188-191). PadForge reads every keyboard in its own
        /// session, so it keeps the part of that rule that concerns one
        /// keyboard: a collection with a sibling that also qualifies is
        /// refused.</para>
        /// </summary>
        public static bool Matches(AnalogKeyboardDeviceInfo info)
        {
            if (!IsEligible(info)) return false;
            foreach (var sibling in info.Siblings)
                if (IsEligible(sibling)) return false;
            return true;
        }

        /// <summary>The filter for one collection, without the sibling rule.</summary>
        public static bool IsEligible(AnalogKeyboardDeviceInfo info)
            => info != null
               && info.VendorId == VendorId
               && SupportedProduct(info.ProductId)
               && PathNamesProduct(info.Path, info.ProductId)
               && info.UsagePage == UsagePage
               && info.Usage == Usage
               && info.InputReportLength == ReportLength
               && info.OutputReportLength == ReportLength;

        /// <summary>HallJoy looks for "vid_0c45&amp;pid_XXXX" in the lowercased
        /// interface path (aula_mini60_diagnostic.cpp:93-95), which only a
        /// wired USB collection carries.</summary>
        private static bool PathNamesProduct(string path, ushort productId)
            => !string.IsNullOrEmpty(path)
               && path.IndexOf("vid_0c45&pid_" + productId.ToString("x4"), StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// A request buffer, report ID first: <c>00 AA command length
        /// offset-low offset-high 00 flag</c>, the rest zero. The flag is 0 for
        /// an assignment read that ends below byte 512 and 1 otherwise.
        /// HallJoy's Request (aula_mini60_diagnostic_protocol.h:8-16). Null for
        /// every other command or shape, which HallJoy builds as an all-zero
        /// buffer its Send refuses (aula_mini60_diagnostic.cpp:121).
        /// </summary>
        public static byte[] Request(int command, int length = 0, int offset = 0)
        {
            bool allowed = (command == CommandDeviceInfo && length == DeviceInfoLength && offset == 0)
                || (command == CommandAssignments && length > 0 && length <= ChunkLength && offset >= 0
                    && offset + length <= AssignmentBytes)
                || ((command == CommandSimulationOn || command == CommandSimulationOff) && length == 0 && offset == 0);
            if (!allowed) return null;
            var r = new byte[ReportLength];
            r[1] = RequestMarker;
            r[2] = (byte)command;
            r[3] = (byte)length;
            r[4] = (byte)offset;
            r[5] = (byte)(offset >> 8);
            r[7] = (byte)(command == CommandAssignments && offset + length < AssignmentBytes ? 0 : 1);
            return r;
        }

        /// <summary>The 64-byte payload of a read: a 65-byte read loses its
        /// zero report ID, and the payload must start with 0x55. Empty
        /// otherwise (aula_mini60_diagnostic_protocol.h:18-22).</summary>
        public static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> raw)
        {
            if (raw.Length == ReportLength && raw[0] == 0) raw = raw.Slice(1);
            if (raw.Length != PayloadLength || raw[0] != ReplyMarker) return ReadOnlySpan<byte>.Empty;
            return raw;
        }

        /// <summary>Little-endian u16 at <paramref name="at"/>.</summary>
        public static int U16(ReadOnlySpan<byte> data, int at) => data[at] | (data[at + 1] << 8);

        /// <summary>A reply echoes the command, the length and the offset
        /// after the 0x55 marker (aula_mini60_diagnostic_protocol.h:29-31).
        /// Its data starts at payload byte 8 (aula_mini60_diagnostic.cpp:224).</summary>
        public static bool IsReply(ReadOnlySpan<byte> payload, int command, int length, int offset)
            => payload.Length == PayloadLength && payload[0] == ReplyMarker && payload[1] == command
               && payload[2] == length && U16(payload, 3) == offset;

        /// <summary>
        /// A pushed sample: <c>55 FB key status</c>, then little-endian high
        /// endpoint, low endpoint (masked with 0x7FFF), ADC, travel in 0.01 mm
        /// and stroke in 0.1 mm. The key index must be below 126 and the
        /// status 0 or 1 (aula_mini60_diagnostic_protocol.h:24-28).
        /// </summary>
        public static bool TryDecodeSample(ReadOnlySpan<byte> raw, out AulaMini60Sample sample)
        {
            sample = default;
            var p = Payload(raw);
            if (p.IsEmpty || p[1] != CommandSample || p[2] >= Positions || p[3] > 1) return false;
            sample = new AulaMini60Sample(p[2], p[3], U16(p, 4), U16(p, 6) & 0x7FFF, U16(p, 8), U16(p, 10),
                U16(p, 12));
            return true;
        }

        /// <summary>Usages an assignment may name
        /// (aula_mini60_native_model.h:24).</summary>
        public static bool KeyboardUsage(int usage) => (usage >= 4 && usage <= 0xA4) || (usage >= 0xE0 && usage <= 0xE7);

        /// <summary>
        /// The key a position's 4-byte assignment record
        /// <c>[type, modifiers, usage, extra]</c> gives it, or 0. Type 0 keeps
        /// the factory key. Type 2 with extra 0 names either one modifier bit
        /// (0xE0 plus the bit) or a usage, 0xAF being the vendor Fn action.
        /// Everything else is unpublished (aula_mini60_native_model.h:25-36).
        /// </summary>
        public static int Assigned(int position, ReadOnlySpan<byte> record)
        {
            var factory = FactoryTable;
            if (position < 0 || position >= factory.Length || factory[position] == 0) return 0;
            if (record[0] == 0) return factory[position];
            if (record[0] != 2 || record[3] != 0) return 0;
            int modifiers = record[1];
            if (modifiers != 0)
            {
                if (record[2] != 0 || (modifiers & (modifiers - 1)) != 0) return 0;
                for (int bit = 0; bit < 8; bit++)
                    if (modifiers == 1 << bit) return 0xE0 + bit;
            }
            if (record[2] == FnAction) return AnalogKeyCodes.Fn;
            return KeyboardUsage(record[2]) ? record[2] : 0;
        }

        /// <summary>
        /// Depth in thousandths: travel over stroke in the same unit, integer
        /// math, capped at 1000. A stroke outside 1.0 to 5.0 mm or a travel
        /// over 1000 reads 0. Overshoot is clipped, never learned
        /// (aula_mini60_native_model.h:37-42).
        /// </summary>
        public static int Milli(int travel, int stroke)
        {
            if (stroke < 10 || stroke > 50 || travel > 1000 || travel < 0) return 0;
            return Math.Min(1000, travel * 1000 / (stroke * 10));
        }

        /// <summary>
        /// HallJoy's identity check on the device-info reply
        /// (aula_mini60_native_model.h:14-19): VID 0C45, a supported product ID
        /// equal to the collection's, manufacturer 0x0166, and for the HE Pro
        /// the product 0x110C. The base and MAX product comes from an MCU
        /// register, so HallJoy does not check it.
        /// </summary>
        public static bool Identity(ushort descriptorProductId, int vendorId, int productId, int manufacturer,
            int product)
        {
            var model = Model(descriptorProductId);
            return model != null && vendorId == VendorId && productId == descriptorProductId
                   && manufacturer == Manufacturer
                   && (model.DeviceInfoProduct == 0 || product == model.DeviceInfoProduct);
        }

        private static IReadOnlyList<AulaMini60Model> LoadModels()
        {
            var list = new List<AulaMini60Model>();
            var root = AnalogKeyboardData.File(AulaEventsRoutes.DataFile);
            foreach (var entry in root.GetProperty(ModelsName).EnumerateArray())
                list.Add(new AulaMini60Model(
                    (ushort)entry.GetProperty("pid").GetInt32(),
                    entry.GetProperty("name").GetString(),
                    entry.GetProperty("infoProduct").GetInt32()));
            return list;
        }
    }

    /// <summary>
    /// One MINI 60 HE conversation, HallJoy's "play" worker
    /// (aula_mini60_diagnostic.cpp:184-262) and its publication
    /// (:382-446).
    ///
    /// <para>Start proves the keyboard with device info 0x10, reads the
    /// 512-byte assignment table with 0x12, and turns on the firmware's
    /// simulation mode with 0x66, after which the keyboard pushes a 0xFB
    /// sample for every key that moves. Stop sends 0x67 whenever 0x66 was
    /// sent, acknowledged or not. The keyboard stops sending near rest, so a
    /// key whose samples stop reads 0 fifty milliseconds after its last
    /// one.</para>
    ///
    /// <para>HallJoy binds each position twice, to its factory key and to the
    /// key the keyboard's own assignment table gives it, and reads the
    /// assigned channel when its automatic layout is on (:440-446,
    /// native_layout_state.h:30-33), which is its default
    /// (keyboard_layout.cpp:1787). PadForge has no layout presets, so it
    /// publishes the assigned channel: a key remapped in the AULA software
    /// moves the key it now types. Positions bound to one key read the
    /// deepest of them (physical_analog_state.h:39-52).</para>
    /// </summary>
    public sealed class AulaMini60Session : AnalogKeyboardSession
    {
        private const int P = AulaMini60Protocol.Positions;

        private readonly ushort _productId;
        private readonly Func<long> _clock;
        private readonly int[] _factory;
        private readonly int[] _assigned = new int[P];
        private readonly bool[] _sampled = new bool[P];
        private readonly int[] _travel = new int[P];
        private readonly int[] _stroke = new int[P];
        private readonly long[] _receivedAt = new long[P];
        private readonly byte[] _reply = new byte[AulaMini60Protocol.PayloadLength];
        private readonly AnalogKeyInputState _next = new();
        private bool _control;
        private bool _broken;
        private bool _mapReady;
        private bool _modeAttempted;
        private bool _started;
        private int _decoded;
        private long _proofDeadline;
        private string _model;
        private int[] _keyOrder;

        /// <param name="productId">The collection's USB product ID.</param>
        /// <param name="clock">Milliseconds, for tests. Defaults to
        /// <see cref="Environment.TickCount64"/>.</param>
        public AulaMini60Session(ushort productId, Func<long> clock = null)
        {
            _productId = productId;
            _clock = clock ?? (() => Environment.TickCount64);
            _factory = AulaMini60Protocol.FactoryTable;
        }

        public override string ModelName => _model;
        public override int[] KeyOrder => _keyOrder;

        /// <summary>True while requests go out as HidD_SetOutputReport
        /// control transfers, HallJoy's second route.</summary>
        public bool UsesOutputReports => _control;

        /// <summary>True from the 0x66 request until a cleanup sent 0x67.</summary>
        public bool SimulationModeSent => _modeAttempted;

        /// <summary>True once the 0x66 acknowledgement or a sample for a
        /// mapped key arrived, HallJoy's start proof
        /// (aula_mini60_diagnostic.cpp:149-154, 248).</summary>
        public bool Started => _started;

        /// <summary>The key each HFD key index publishes, 0 for none.</summary>
        public int[] AssignedKeys => (int[])_assigned.Clone();

        public override bool Start(IAnalogKeyboardTransport io)
        {
            try
            {
                if (!Connect(io)) return false;

                // Simulation mode (aula_mini60_diagnostic.cpp:243-245). A
                // second 0x66 goes out only when nothing answered the first
                // and no sample arrived either.
                _modeAttempted = true;
                bool ack = Exchange(io, AulaMini60Protocol.CommandSimulationOn, 0, 0);
                if (!ack && _decoded == 0 && !_broken)
                    ack = Exchange(io, AulaMini60Protocol.CommandSimulationOn, 0, 0);
                if (ack) _started = true;
                if (_broken)
                {
                    // The framework calls Stop only after a successful Start,
                    // so the mode is undone here.
                    Stop(io);
                    return false;
                }

                _proofDeadline = _clock() + AulaMini60Protocol.StartProofMs;
                _model = AulaMini60Protocol.Model(_productId)?.Name;
                _keyOrder = AnalogKeyboardData.KeysOf(_assigned);
                return true;
            }
            catch
            {
                try { Stop(io); } catch { }
                return false;
            }
        }

        /// <summary>
        /// Device info, then the assignment table, up to three times
        /// (aula_mini60_diagnostic.cpp:193-241). Each attempt tries the
        /// WriteFile route twice, then the HidD_SetOutputReport route twice,
        /// and keeps the route that answered. A reply that names another
        /// keyboard ends everything at once and nothing more is sent (:208).
        /// HallJoy opens a new handle for each attempt. PadForge keeps its one
        /// handle and discards the reports queued before the new attempt.
        /// </summary>
        private bool Connect(IAnalogKeyboardTransport io)
        {
            for (int attempt = 0; attempt < AulaMini60Protocol.ConnectionAttempts; attempt++)
            {
                if (attempt > 0) io.DiscardStale();
                _control = false;
                _decoded = 0;

                bool info = false;
                for (int route = 0; route < 2 && !info && !_broken; route++)
                {
                    _control = route != 0;
                    for (int retry = 0; retry < 2; retry++)
                    {
                        if (Exchange(io, AulaMini60Protocol.CommandDeviceInfo, AulaMini60Protocol.DeviceInfoLength, 0))
                        {
                            ReadOnlySpan<byte> reply = _reply;
                            if (!AulaMini60Protocol.Identity(_productId,
                                    AulaMini60Protocol.U16(reply, 12), AulaMini60Protocol.U16(reply, 14),
                                    AulaMini60Protocol.U16(reply, 20), AulaMini60Protocol.U16(reply, 22)))
                                return false;
                            info = true;
                            break;
                        }
                        if (_broken) break;
                    }
                }
                // HallJoy reopens the path after a transport failure
                // (:242). A session cannot reopen its handle, so it stops.
                if (_broken) return false;
                if (!info) continue;
                if (ReadAssignments(io)) return true;
                if (_broken) return false;
            }
            return false;
        }

        /// <summary>Ten 0x12 reads of 56 bytes (8 for the last), two tries
        /// each. Every byte must arrive, or the attempt starts over
        /// (aula_mini60_diagnostic.cpp:219-241). Record k is bytes 4k to 4k+3.
        /// Bytes 504 to 511 are read and not used.</summary>
        private bool ReadAssignments(IAnalogKeyboardTransport io)
        {
            var table = new byte[AulaMini60Protocol.AssignmentBytes];
            bool complete = true;
            for (int offset = 0; offset < AulaMini60Protocol.AssignmentBytes && !_broken;
                 offset += AulaMini60Protocol.ChunkLength)
            {
                int length = Math.Min(AulaMini60Protocol.ChunkLength, AulaMini60Protocol.AssignmentBytes - offset);
                bool ok = false;
                for (int retry = 0; retry < 2 && !ok; retry++)
                    ok = Exchange(io, AulaMini60Protocol.CommandAssignments, length, offset);
                if (ok) Array.Copy(_reply, 8, table, offset, length);
                else complete = false;
            }
            if (_broken || !complete) return false;
            for (int k = 0; k < P; k++)
                _assigned[k] = AulaMini60Protocol.Assigned(k, table.AsSpan(k * 4, 4));
            _mapReady = true;
            return true;
        }

        /// <summary>
        /// Streams. One pass reads one report, stores a sample for a mapped
        /// key, and composes every key from the samples no older than 50 ms
        /// (aula_mini60_diagnostic.cpp:246-257, 405-421). The read waits at
        /// most 20 ms, and less when a key is about to expire, so a released
        /// key drops on time. A stream that shows no start proof within 2
        /// seconds ends the session (:249, 254).
        /// </summary>
        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            if (!_mapReady) return AnalogPollResult.Failed;
            int n = io.Receive(Buffer, WaitMs(_clock()));
            if (n < 0)
            {
                _broken = true;
                Release(output);
                return AnalogPollResult.Failed;
            }
            bool heard = n > 0 && Take(new ReadOnlySpan<byte>(Buffer, 0, n));
            long now = _clock();
            if (!_started && now >= _proofDeadline)
            {
                Release(output);
                return AnalogPollResult.Failed;
            }
            Compose(now, _next);
            if (!heard && _next.SameAs(output)) return AnalogPollResult.Idle;
            _next.CopyInto(output);
            return AnalogPollResult.Ok;
        }

        /// <summary>
        /// Leaves simulation mode, as HallJoy's session cleanup does
        /// (aula_mini60_diagnostic.cpp:165-172): 0x67 on the current route,
        /// up to 180 ms for its acknowledgement, and the other route when the
        /// send failed. When that cleanup was not acknowledged, HallJoy's
        /// supervisor runs a cleanup worker that sends 0x67 again from the
        /// WriteFile route (:465, :196). PadForge runs that second round on
        /// the same handle.
        /// </summary>
        public override void Stop(IAnalogKeyboardTransport io)
        {
            if (_modeAttempted)
            {
                bool sent = Cleanup(io, out bool acked);
                if (!(sent && acked))
                {
                    io.DiscardStale();
                    _modeAttempted = true;
                    _control = false;
                    _broken = false;
                    Cleanup(io, out _);
                }
            }
            ClearSamples();
        }

        private bool Cleanup(IAnalogKeyboardTransport io, out bool acked)
        {
            acked = false;
            if (!_modeAttempted) return true;
            bool sent = SendRequest(io, AulaMini60Protocol.CommandSimulationOff, 0, 0);
            long deadline = _clock() + AulaMini60Protocol.CleanupAckMs;
            while (sent && _clock() < deadline && !_broken)
            {
                if (Pump(io, AulaMini60Protocol.CommandSimulationOff, 0, 0))
                {
                    acked = true;
                    break;
                }
            }
            if (!sent)
            {
                _control = !_control;
                sent = SendRequest(io, AulaMini60Protocol.CommandSimulationOff, 0, 0);
            }
            _modeAttempted = false;
            return sent;
        }

        /// <summary>Send, then read until the matching reply or 450 ms,
        /// taking samples on the way (aula_mini60_diagnostic.cpp:159-164).</summary>
        private bool Exchange(IAnalogKeyboardTransport io, int command, int length, int offset)
        {
            if (!SendRequest(io, command, length, offset)) return false;
            long deadline = _clock() + AulaMini60Protocol.ReplyWaitMs;
            while (_clock() < deadline && !_broken)
                if (Pump(io, command, length, offset)) return true;
            return false;
        }

        /// <summary>WriteFile, or HidD_SetOutputReport on the second route
        /// (aula_mini60_diagnostic.cpp:120-129).</summary>
        private bool SendRequest(IAnalogKeyboardTransport io, int command, int length, int offset)
        {
            var request = AulaMini60Protocol.Request(command, length, offset);
            if (request == null) return false;
            return _control ? io.SendOutputReport(request) : io.Send(request);
        }

        /// <summary>One read of up to 20 ms. True when it is the reply
        /// expected. Anything else that decodes as a sample is taken
        /// (aula_mini60_diagnostic.cpp:140-158).</summary>
        private bool Pump(IAnalogKeyboardTransport io, int command, int length, int offset)
        {
            int n = io.Receive(Buffer, AulaMini60Protocol.ReadWaitMs);
            if (n < 0)
            {
                _broken = true;
                return false;
            }
            if (n == 0) return false;
            var raw = new ReadOnlySpan<byte>(Buffer, 0, n);
            var payload = AulaMini60Protocol.Payload(raw);
            if (AulaMini60Protocol.IsReply(payload, command, length, offset))
            {
                payload.CopyTo(_reply);
                return true;
            }
            Take(raw);
            return false;
        }

        /// <summary>Stores a sample for a key with a factory position once
        /// simulation mode was requested on a complete map, stamped with its
        /// arrival time (aula_mini60_diagnostic.cpp:147-155). True when it
        /// was stored.</summary>
        private bool Take(ReadOnlySpan<byte> raw)
        {
            if (!AulaMini60Protocol.TryDecodeSample(raw, out var sample)) return false;
            _decoded++;
            if (!_modeAttempted || !_mapReady || _factory[sample.Key] == 0) return false;
            _sampled[sample.Key] = true;
            _travel[sample.Key] = sample.Travel;
            _stroke[sample.Key] = sample.Stroke;
            _receivedAt[sample.Key] = _clock();
            _started = true;
            return true;
        }

        /// <summary>Every published key at <paramref name="now"/>: the
        /// deepest fresh sample among the positions bound to it.</summary>
        private void Compose(long now, AnalogKeyInputState state)
        {
            state.ResetForReuse();
            for (int k = 0; k < P; k++)
            {
                int code = _assigned[k];
                if (code == 0 || !_sampled[k] || now - _receivedAt[k] > AulaMini60Protocol.FreshMs) continue;
                int milli = AulaMini60Protocol.Milli(_travel[k], _stroke[k]);
                if (milli == 0) continue;
                float depth = milli / 1000f;
                if (depth > state.Get(code)) state.Set(code, depth);
            }
        }

        /// <summary>The read wait: 20 ms, cut short at the next key expiry
        /// or the start-proof deadline, at least 1 ms.</summary>
        private int WaitMs(long now)
        {
            long wait = AulaMini60Protocol.ReadWaitMs;
            if (!_started) wait = Math.Min(wait, _proofDeadline - now);
            for (int k = 0; k < P; k++)
            {
                if (!_sampled[k] || _assigned[k] == 0) continue;
                if (AulaMini60Protocol.Milli(_travel[k], _stroke[k]) == 0) continue;
                long expires = _receivedAt[k] + AulaMini60Protocol.FreshMs + 1;
                if (expires > now) wait = Math.Min(wait, expires - now);
            }
            return (int)Math.Max(1, wait);
        }

        private void Release(AnalogKeyInputState output)
        {
            ClearSamples();
            output.ResetForReuse();
        }

        private void ClearSamples()
        {
            Array.Clear(_sampled);
            Array.Clear(_travel);
            Array.Clear(_stroke);
            Array.Clear(_receivedAt);
        }
    }
}
