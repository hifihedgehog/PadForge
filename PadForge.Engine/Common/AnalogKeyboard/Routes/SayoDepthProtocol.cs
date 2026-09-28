using System;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// The SayoDevice depth protocol (issue #468) as HallJoy runs it
    /// (backend_sayo.inc, sayo_o3c_protocol.h, AGPL-3.0, commit 378f9fe8).
    /// Every frame, both ways, is one report 0x22: an echo byte (0x12 depth,
    /// 0x13 configuration), a checksum, a length, a command and an index, then
    /// the payload (sayo_o3c_protocol.h:10-27). The depth poll asks command
    /// 0x15 index 1 for the three keys' raw travel, and the configuration
    /// reads send an empty payload, the firmware's read branch, for the model
    /// (command 0) and each key's binding (command 0x10). HallJoy never sends
    /// Save or any payload that writes (:28).
    ///
    /// <para>The O3C (8089:0009) is admitted without a probe. Any other
    /// SayoDevice PID is admitted only when its collection answers a depth
    /// poll with a valid depth frame (backend_sayo.inc:599-631, :269-303).</para>
    /// </summary>
    public static class SayoDepthProtocol
    {
        public const ushort VendorId = 0x8089;

        /// <summary>SayoDevice O3C, three magnetic keys and an encoder without
        /// depth (backend_sayo.inc:15).</summary>
        public const ushort O3cProductId = 0x0009;

        /// <summary>The one collection HallJoy keeps (backend_sayo.inc:641-649).</summary>
        public const ushort UsagePage = 0xFF12;
        public const ushort Usage = 0x0002;
        public const int OutputReportLength = 1024;
        public const int MinInputReportLength = 14;

        /// <summary>The configuration reads need a 64-byte input report
        /// (backend_sayo.inc:204-205).</summary>
        public const int ConfigMinInputReportLength = 64;

        /// <summary>HidD_SetNumInputBuffers(h, 64) (backend_sayo.inc:571).</summary>
        public const int InputBuffers = 64;

        public const byte ReportId = 0x22;
        public const byte DepthEcho = 0x12;
        public const byte ConfigEcho = 0x13;
        public const byte DepthCommand = 0x15;
        public const byte DepthIndex = 1;
        public const byte InfoCommand = 0x00;
        public const byte KeyInfoCommand = 0x10;

        /// <summary>Raw full press (backend_sayo.inc:21), which HallJoy calls
        /// "capture-derived, not a universal firmware scale claim"
        /// (docs/v1.4/PROTOCOL_AUDIT_RM30_SAYO_2026-09-06.md:22-24).</summary>
        public const int RawFullScale = 4000;

        /// <summary>A frame carrying any raw value above this is rejected
        /// whole (sayo_o3c_protocol.h:63).</summary>
        public const int RawLimit = 8000;

        /// <summary>At least 8 ms between depth polls, and a 25 ms read wait
        /// (backend_sayo.inc:19, :336, :355).</summary>
        public const long PollIntervalMs = 8;
        public const int ReadWaitMs = 25;

        /// <summary>Depth older than 160 ms reads as 0 (backend_sayo.inc:20,
        /// backend.cpp:4486-4489).</summary>
        public const long DepthFreshMs = 160;

        /// <summary>No report for longer than this ends the session
        /// (backend_sayo.inc:18, :1003-1015).</summary>
        public const long NoPacketRestartMs = 1800;

        /// <summary>The probe reads for 250 ms in 25 ms slices
        /// (backend_sayo.inc:286-292).</summary>
        public const int ProbeWindowMs = 250;
        public const int ProbeReadSliceMs = 25;

        /// <summary>A configuration exchange: two attempts, each read for 100 ms
        /// in 20 ms slices (backend_sayo.inc:207-225).</summary>
        public const int ConfigAttempts = 2;
        public const int ConfigWindowMs = 100;
        public const int ConfigReadSliceMs = 20;

        /// <summary>The model value the O3C's info answer carries
        /// (sayo_o3c_protocol.h:35-37).</summary>
        public const int O3cModel = 9;

        /// <summary>HallJoy's name for the O3C (docs/SUPPORTED_HARDWARE.md:48,
        /// preset "SayoDevice O3C ANSI").</summary>
        public const string O3cName = "SayoDevice O3C";

        /// <summary>The depth poll's header (backend_sayo.inc:260-262): echo
        /// 0x12, checksum 0x133C, length 5, command 0x15, index 1, and one
        /// payload byte of 0 that the zero padding supplies.</summary>
        public static readonly byte[] DepthPollHeader = { 0x22, 0x12, 0x3C, 0x13, 0x05, 0x00, 0x15, 0x01 };

        private static readonly Lazy<int[]> _factory = new(() =>
            AnalogKeyboardData.Table(SparkSayoRoutes.DataFile, "sayo_o3c_factory"));
        private static readonly Lazy<int[]> _channels = new(() =>
            AnalogKeyboardData.Table(SparkSayoRoutes.DataFile, "sayo_o3c_channels"));

        /// <summary>The O3C's factory assignments Z, X and C by channel, the
        /// keys the manual layout publishes (sayo_o3c_protocol.h:8).</summary>
        public static int[] Factory => _factory.Value;

        /// <summary>The stable physical codes 0x470 to 0x472 by channel, the
        /// keys the automatic layout publishes (analog_key_codes.h:16-18).</summary>
        public static int[] Channels => _channels.Value;

        /// <summary>
        /// The metadata test for the one collection HallJoy keeps
        /// (backend_sayo.inc:641-649): VID 8089, vendor collection FF12:2, an
        /// output report of exactly 1024 bytes and an input report of at least
        /// 14. The PID choice, O3C without a probe and any other PID after a
        /// valid depth answer, happens in Start. Product and serial strings
        /// and the interface number are not checked, as HallJoy checks none
        /// of them.
        ///
        /// <para>HallJoy opens every unclaimed HID collection for reading and
        /// writing before it checks the vendor (backend_sayo.inc:532-563). It
        /// writes nothing to them, but the open is needless. Here the metadata
        /// comes from the sweep's query-only handle, and only a collection
        /// this test accepts is opened for writing.</para>
        /// </summary>
        public static bool Matches(AnalogKeyboardDeviceInfo info)
            => info != null && info.VendorId == VendorId
               && info.UsagePage == UsagePage && info.Usage == Usage
               && info.OutputReportLength == OutputReportLength
               && info.InputReportLength >= MinInputReportLength;

        /// <summary>"SayoDevice O3C" for the O3C, null for the other PIDs,
        /// which HallJoy does not name, so their product string names them.</summary>
        public static string ModelName(ushort productId) => productId == O3cProductId ? O3cName : null;

        // ── Frames ─────────────────────────────────────────────────────────

        public static int U16(ReadOnlySpan<byte> p, int at) => p[at] | (p[at + 1] << 8);

        /// <summary>The depth poll as HallJoy writes it, one full 1024-byte
        /// output report (backend_sayo.inc:253-267).</summary>
        public static byte[] DepthPollRequest()
        {
            var r = new byte[OutputReportLength];
            DepthPollHeader.CopyTo(r, 0);
            return r;
        }

        /// <summary>so3c::Read (sayo_o3c_protocol.h:29-34): a configuration
        /// read with an empty payload, length 4, and the checksum of its three
        /// counted words.</summary>
        public static byte[] ReadRequest(byte command, byte index = 0)
        {
            var p = new byte[OutputReportLength];
            p[0] = ReportId;
            p[1] = ConfigEcho;
            p[4] = 4;
            p[6] = command;
            p[7] = index;
            int sum = (U16(p, 0) + 4 + U16(p, 6)) & 0xFFFF;
            p[2] = (byte)sum;
            p[3] = (byte)(sum >> 8);
            return p;
        }

        /// <summary>
        /// so3c::Parse (sayo_o3c_protocol.h:15-27): report 0x22, a length of 4
        /// to 1020 (larger values are continuation or error flags), the frame
        /// complete in what was read, and a checksum equal to the 16-bit sum of
        /// the little-endian words at the even offsets below (length + 5)
        /// rounded down to even, the checksum word itself left out.
        /// </summary>
        public static bool Parse(ReadOnlySpan<byte> p, out SayoFrame frame)
        {
            frame = default;
            if (p.Length < 8 || p[0] != ReportId) return false;
            int length = U16(p, 4);
            if (length < 4 || length > 1020 || length + 4 > p.Length) return false;
            int end = (length + 5) & ~1;
            if (end > p.Length) return false;
            int sum = 0;
            for (int i = 0; i < end; i += 2)
                if (i != 2) sum = (sum + U16(p, i)) & 0xFFFF;
            if (sum != U16(p, 2)) return false;
            frame = new SayoFrame(p.Slice(8, length - 4), p[6], p[7], p[1]);
            return true;
        }

        /// <summary>so3c::Identity (sayo_o3c_protocol.h:35-37): the info
        /// answer names model 9.</summary>
        public static bool Identity(in SayoFrame f)
            => f.Echo == ConfigEcho && f.Command == InfoCommand && f.Index == 0
               && f.Payload.Length >= 4 && U16(f.Payload, 0) == O3cModel;

        /// <summary>
        /// so3c::Binding (sayo_o3c_protocol.h:38-58): a KeyInfo answer of 56
        /// bytes for the key asked, with the audited O3C geometry (class 1,
        /// x 1000 + 2000 per key, y 3000, 1800 square) and a plain base-layer
        /// keyboard action. One modifier alone is its usage 0xE0 + bit, a key
        /// alone must be 0x04 to 0xE7, and both zero is an unassigned key
        /// (hid 0). Chords, other action modes and unknown geometry are
        /// rejected rather than flattened to a guessed key.
        /// </summary>
        public static bool Binding(in SayoFrame f, int index, out int hid)
        {
            hid = 0;
            if (index < 0 || index >= 3 || f.Echo != ConfigEcho || f.Command != KeyInfoCommand
                || f.Index != index || f.Payload.Length != 56)
                return false;
            var p = f.Payload;
            if (p[0] != 1 || U16(p, 4) != 1000 + index * 2000 || U16(p, 6) != 3000
                || U16(p, 8) != 1800 || U16(p, 10) != 1800)
                return false;
            if (p[16] != 0 || p[22] != 0 || p[23] != 0) return false;
            int modifiers = p[20], key = p[21];
            if (modifiers != 0 && key != 0) return false;
            if (modifiers != 0)
            {
                if ((modifiers & (modifiers - 1)) != 0) return false;
                for (int bit = 0; bit < 8; bit++)
                    if (modifiers == 1 << bit) hid = 0xE0 + bit;
            }
            else
            {
                if (key != 0 && (key < 0x04 || key > 0xE7)) return false;
                hid = key;
            }
            return true;
        }

        /// <summary>so3c::Depth (sayo_o3c_protocol.h:59-66): the depth answer,
        /// exactly six payload bytes of three little-endian raw values, none
        /// above 8000.</summary>
        public static bool Depth(in SayoFrame f, Span<int> raw)
        {
            if (f.Echo != DepthEcho || f.Command != DepthCommand || f.Index != DepthIndex || f.Payload.Length != 6)
                return false;
            for (int i = 0; i < 3; i++)
            {
                raw[i] = U16(f.Payload, 2 * i);
                if (raw[i] > RawLimit) return false;
            }
            return true;
        }

        /// <summary>so3c::Normalize (sayo_o3c_protocol.h:67-70): raw over 4000
        /// rounded half up to milli, under 4 milli (raw 13 or less) reads 0,
        /// and full press is 1000.</summary>
        public static int Normalize(int raw)
        {
            int m = (raw * 1000 + 2000) / RawFullScale;
            return m < 4 ? 0 : m > 1000 ? 1000 : m;
        }

        /// <summary>so3c::Matches (sayo_o3c_protocol.h:80-83): the code a
        /// channel answers. In the automatic layout that is its physical code
        /// 0x470 + index, and also the binding read from the keyboard when it
        /// has one. In the manual layout it is the factory key.</summary>
        public static bool ChannelAnswers(int index, int code, bool automatic, int binding)
        {
            if (index < 0 || index >= 3 || code == 0) return false;
            return automatic
                ? code == Channels[index] || (binding != 0 && code == binding)
                : code == Factory[index];
        }
    }

    /// <summary>A parsed Sayo frame over the bytes it was read from.</summary>
    public readonly ref struct SayoFrame
    {
        public SayoFrame(ReadOnlySpan<byte> payload, byte command, byte index, byte echo)
        {
            Payload = payload;
            Command = command;
            Index = index;
            Echo = echo;
        }

        public ReadOnlySpan<byte> Payload { get; }
        public byte Command { get; }
        public byte Index { get; }
        public byte Echo { get; }
    }

    /// <summary>
    /// One SayoDevice keyboard (issue #468), HallJoy's SayoStart and
    /// SayoReaderThreadBody (backend_sayo.inc:305-372, :779-991).
    ///
    /// <para>Start runs what HallJoy runs before the first depth poll. For
    /// the O3C that is the configuration read: the model, then each key's
    /// binding, whose failures never remove the keyboard (:202-251). For any
    /// other PID it is the depth probe that admits it (:269-303).</para>
    ///
    /// <para>A pass polls at most every 8 ms and waits 25 ms for a report.
    /// Any valid depth frame counts, whichever request produced it, and
    /// publishes the three keys. Other reports only prove the keyboard is
    /// there. A quiet pass is Idle, since HallJoy never counts one as a miss.
    /// The keys fall to 0 once the last depth frame is 160 ms old, and the
    /// session ends after 1800 ms without any report once one has arrived
    /// (SayoTickHotplug, :1001-1015). Nothing is sent at Stop (:653-755).</para>
    ///
    /// <para>The O3C publishes HallJoy's automatic layout: channel i as the
    /// physical code 0x470 + i and as the binding read from the keyboard,
    /// duplicate bindings taking the largest depth (backend.cpp:4484-4494).
    /// A model answer other than 9, or an input report too short for the
    /// configuration read, leaves the manual layout, Z, X and C, which every
    /// other PID uses too.</para>
    /// </summary>
    public sealed class SayoDepthSession : AnalogKeyboardSession
    {
        private const int DeviceGone = -1;

        private readonly ushort _productId;
        private readonly Func<long> _clock;
        private readonly int[] _bindings = new int[3];
        private readonly int[] _milli = new int[3];
        private readonly byte[] _poll = SayoDepthProtocol.DepthPollRequest();
        private bool _automatic;
        private bool _configurationRead;
        private bool _published;
        private long _lastPollMs;
        private long _lastPacketMs;
        private long _lastDepthMs;
        private int[] _keys;

        /// <param name="productId">The keyboard's PID: the O3C reads its
        /// configuration, any other PID must answer the probe.</param>
        /// <param name="clock">Milliseconds, GetTickCount64 by default. Tests pass their own.</param>
        public SayoDepthSession(ushort productId, Func<long> clock = null)
        {
            _productId = productId;
            _clock = clock ?? (() => Environment.TickCount64);
        }

        /// <summary>True when the O3C publishes the automatic layout.</summary>
        public bool Automatic => _automatic;

        /// <summary>The keyboard's binding per channel, 0 where none was read.</summary>
        public int[] Bindings => (int[])_bindings.Clone();

        /// <summary>At least one binding was read (backend_sayo.inc:248).</summary>
        public bool ConfigurationRead => _configurationRead;

        /// <summary>Depth poll writes that failed in a row. HallJoy counts
        /// them and never stops for them (backend_sayo.inc:345-350).</summary>
        public int PollFailStreak { get; private set; }

        public override string ModelName => SayoDepthProtocol.ModelName(_productId);

        public override int[] KeyOrder => _keys;

        public override bool Start(IAnalogKeyboardTransport io)
        {
            if (_productId == SayoDepthProtocol.O3cProductId)
            {
                // The O3C is admitted with no handshake (backend_sayo.inc:610-614).
                if (ReadConfiguration(io) == DeviceGone) return false;
            }
            else if (Probe(io) != 1)
            {
                return false;
            }
            _keys = BuildKeys();
            _lastPollMs = 0;
            _lastPacketMs = 0;
            _lastDepthMs = 0;
            return true;
        }

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            if (_keys == null) return AnalogPollResult.Failed;
            long now = _clock();

            // SayoTickHotplug (backend_sayo.inc:1003-1015), monotonic_time.h's
            // IsStale, the same rule as SparkLink's (:18-25).
            if (SparkLinkProtocol.IsPacketStale(now, _lastPacketMs, SayoDepthProtocol.NoPacketRestartMs))
                return AnalogPollResult.Failed;

            // At most one poll per 8 ms. A failed write is counted and the
            // loop goes on (backend_sayo.inc:336-351).
            if (now - _lastPollMs >= SayoDepthProtocol.PollIntervalMs)
            {
                _lastPollMs = now;
                if (io.Send(_poll)) PollFailStreak = 0;
                else PollFailStreak++;
            }

            int n = io.Receive(Buffer, SayoDepthProtocol.ReadWaitMs);
            if (n < 0) return AnalogPollResult.Failed;
            if (n > 0)
            {
                // Any report proves the keyboard is there. Only a valid depth
                // frame publishes (backend_sayo.inc:365-367, :174-198).
                long at = _clock();
                _lastPacketMs = at;
                Span<int> raw = stackalloc int[3];
                if (Buffer[0] == SayoDepthProtocol.ReportId
                    && SayoDepthProtocol.Parse(Buffer.AsSpan(0, n), out var frame)
                    && SayoDepthProtocol.Depth(frame, raw))
                {
                    for (int i = 0; i < 3; i++) _milli[i] = SayoDepthProtocol.Normalize(raw[i]);
                    _lastDepthMs = at;
                    Publish(output);
                    return AnalogPollResult.Ok;
                }
            }

            // Depth older than 160 ms reads 0 (backend.cpp:4486-4489).
            if (_published && SparkLinkProtocol.FreshnessAgeMs(_clock(), _lastDepthMs) > SayoDepthProtocol.DepthFreshMs)
            {
                Array.Clear(_milli);
                output.ResetForReuse();
                _published = false;
                return AnalogPollResult.Ok;
            }
            return AnalogPollResult.Idle;
        }

        /// <summary>BackendNative_SayoGetMilli (backend.cpp:4484-4494): each
        /// listed code takes the largest depth of the channels that answer it.</summary>
        private void Publish(AnalogKeyInputState output)
        {
            output.ResetForReuse();
            foreach (int code in _keys)
            {
                int milli = 0;
                for (int i = 0; i < 3; i++)
                    if (_milli[i] > milli && SayoDepthProtocol.ChannelAnswers(i, code, _automatic, _bindings[i]))
                        milli = _milli[i];
                if (milli > 0) output.Set(code, milli / 1000f);
            }
            _published = output.Count > 0;
        }

        /// <summary>The automatic layout lists the three physical codes, then
        /// the bindings read. The manual layout lists Z, X and C.</summary>
        private int[] BuildKeys()
        {
            if (!_automatic) return (int[])SayoDepthProtocol.Factory.Clone();
            var table = new int[6];
            SayoDepthProtocol.Channels.CopyTo(table, 0);
            _bindings.CopyTo(table, 3);
            return AnalogKeyboardData.KeysOf(table);
        }

        /// <summary>
        /// SayoReadConfiguration (backend_sayo.inc:202-251), O3C only, and only
        /// on a collection whose input report can hold the answers. The O3C
        /// takes the automatic layout from here on. A model answer other than
        /// 9 takes it back, a timeout keeps it with no bindings, and each key
        /// whose answer fails or is rejected keeps binding 0.
        /// </summary>
        private int ReadConfiguration(IAnalogKeyboardTransport io)
        {
            if (io.OutputLength != SayoDepthProtocol.OutputReportLength
                || io.InputLength < SayoDepthProtocol.ConfigMinInputReportLength)
                return 1;
            _automatic = true;

            int n = Exchange(io, SayoDepthProtocol.InfoCommand, 0);
            if (n == DeviceGone) return DeviceGone;
            if (n == 0) return 1;
            SayoDepthProtocol.Parse(Buffer.AsSpan(0, n), out var info);
            if (!SayoDepthProtocol.Identity(info))
            {
                _automatic = false;
                return 1;
            }

            int read = 0;
            for (int key = 0; key < 3; key++)
            {
                n = Exchange(io, SayoDepthProtocol.KeyInfoCommand, (byte)key);
                if (n == DeviceGone) return DeviceGone;
                if (n == 0) continue;
                SayoDepthProtocol.Parse(Buffer.AsSpan(0, n), out var keyInfo);
                if (!SayoDepthProtocol.Binding(keyInfo, key, out int hid)) continue;
                _bindings[key] = hid;
                read++;
            }
            _configurationRead = read != 0;
            return 1;
        }

        /// <summary>
        /// The exchange of SayoReadConfiguration (backend_sayo.inc:207-225):
        /// up to two attempts, each a 1024-byte request then reads in 20 ms
        /// slices until 100 ms have passed. A failed write moves to the next
        /// attempt. The first configuration answer for this command and index
        /// wins, and everything else read meanwhile, depth frames included, is
        /// dropped. Returns the answer's byte count, left in the buffer, or 0,
        /// or -1 for a gone device (HallJoy ends the exchange on any read
        /// error, and the reader loop that follows ends on the same error).
        /// </summary>
        private int Exchange(IAnalogKeyboardTransport io, byte command, byte index)
        {
            var request = SayoDepthProtocol.ReadRequest(command, index);
            for (int attempt = 0; attempt < SayoDepthProtocol.ConfigAttempts; attempt++)
            {
                if (!io.Send(request)) continue;
                long deadline = _clock() + SayoDepthProtocol.ConfigWindowMs;
                while (_clock() < deadline)
                {
                    int n = io.Receive(Buffer, SayoDepthProtocol.ConfigReadSliceMs);
                    if (n < 0) return DeviceGone;
                    if (n == 0) continue;
                    if (SayoDepthProtocol.Parse(Buffer.AsSpan(0, n), out var frame)
                        && frame.Echo == SayoDepthProtocol.ConfigEcho
                        && frame.Command == command && frame.Index == index)
                        return n;
                }
            }
            return 0;
        }

        /// <summary>
        /// SayoProbeDepthProtocol (backend_sayo.inc:269-303), for a PID other
        /// than the O3C: one depth poll, then reads in 25 ms slices for 250 ms
        /// until a valid depth frame arrives. HallJoy may probe another
        /// interface of the PID and then keep this one. Here the collection
        /// that will be read is the one probed. Returns 1 when admitted, 0
        /// when not, -1 when the device is gone.
        /// </summary>
        private int Probe(IAnalogKeyboardTransport io)
        {
            if (io.InputLength < SayoDepthProtocol.MinInputReportLength
                || io.OutputLength < SayoDepthProtocol.OutputReportLength)
                return 0;
            if (!io.Send(_poll)) return 0;
            long deadline = _clock() + SayoDepthProtocol.ProbeWindowMs;
            Span<int> raw = stackalloc int[3];
            while (_clock() < deadline)
            {
                int n = io.Receive(Buffer, SayoDepthProtocol.ProbeReadSliceMs);
                if (n < 0) return DeviceGone;
                if (n == 0) continue;
                if (SayoDepthProtocol.Parse(Buffer.AsSpan(0, n), out var frame)
                    && SayoDepthProtocol.Depth(frame, raw))
                    return 1;
            }
            return 0;
        }
    }
}
