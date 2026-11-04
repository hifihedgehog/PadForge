using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace PadForge.Common.Input
{
    /// <summary>
    /// HID++ 2.0 frames for Logitech's haptic feature 0x19B0 (#494, asked in
    /// discussion #488). Logitech has not published the feature, and three
    /// implementations cloned beside the other references agree on it:
    /// Solaar (hidpp20_constants.py HapticWaveForms, settings_templates.py
    /// HapticLevel and PlayHapticWaveForm), OpenLogi (haptic_feedback.rs
    /// HapticFeedbackFeature) and LiveHaptics (HidppDevice.cpp). Function 0
    /// returns the supported-waveform mask big-endian in payload bytes 4 to
    /// 7, function 1 the enable flag in bit 0 of byte 0 and the 0 to 100
    /// intensity in byte 1, and function 4 plays one waveform. LiveHaptics
    /// sends 100 after the waveform byte, and Solaar and OpenLogi send zero,
    /// which this follows.
    ///
    /// <para>Every frame is a 20-byte long report. Windows splits the HID++
    /// short and long reports into two collections, and OpenRGB
    /// (LogitechHIDPP20Controller.cpp SendStandard) opens the long one only
    /// and forces long frames on Windows, because that collection rejects
    /// 7-byte writes. Its detector table puts the HID++ collection of a
    /// receiver and of a Bluetooth mouse on usage page 0xFF00 usage 2
    /// ("mice, the collection Solaar drives them on"), and reaches the
    /// Bluetooth devices on page 0xFF43 as well, where the HID++ worker reads
    /// usage 0x0202 (HidppBackend.IsHidppLong).</para>
    /// </summary>
    internal static class HidppHapticProtocol
    {
        public const ushort VendorId = 0x046D;
        public const ushort UsagePage = 0xFF00;
        public const ushort LongUsage = 0x0002;
        public const byte LongReportId = 0x11;
        public const int LongReportLength = 20;

        /// <summary>The 64-byte very long report (#557). A keyboard of the
        /// G810 class takes key colors on it 14 at a time and answers its
        /// key list on it: Logitech Gaming Software sends 12 FF 0C 3A ...
        /// and gets the answers to 11 FF 0C 2A ... back as 12 FF 0C 2A ...
        /// (g810-led-resources dumps/g810-spectrum/set_keys_colors.pcapng).
        /// Windows puts the report in a collection of its own, usage page
        /// 0xFF43 usage 0x0604, which OpenRGB opens beside the 20-byte one
        /// (LogitechControllerDetect.cpp:1237-1265).</summary>
        public const byte VeryLongReportId = 0x12;
        public const int VeryLongReportLength = 64;
        public const ushort VeryLongUsage = 0x0604;

        /// <summary>The software ID in the low nibble of every request, which
        /// the device echoes in its answer. Solaar's table (base.py
        /// SOLAAR_SOFTWARE_ID) lists the IDs other tools claim, OpenRGB 0x07,
        /// LGSTrayEx 0x0A, Solaar 0x0B, G HUB 0x0D and the firmware 0x0F, and
        /// LiveHaptics uses 0x01. 0x0C is none of them, so an answer meant for
        /// Logi Options+ or another tool never reads as PadForge's.</summary>
        public const byte SoftwareId = 0x0C;

        /// <summary>The device itself, on Bluetooth or a cable. A receiver
        /// numbers its paired devices 1 to 6 (LiveHaptics connect,
        /// OpenRGB's receiver slots).</summary>
        public const byte DirectDeviceIndex = 0xFF;
        public const byte FirstReceiverIndex = 1;
        public const byte LastReceiverIndex = 6;

        /// <summary>The index of a device a receiver carries itself, past its
        /// six slots: the POWERPLAY mat's own lighting behind its LIGHTSPEED
        /// receiver (#557, libratbag data/devices/logitech-g-powerplay.device:3-8,
        /// "DeviceIndex=7"). Solaar asks indexes 1 to 7 of every receiver,
        /// since "some receivers have devices past their max # devices"
        /// (Solaar lib/logitech_receiver/receiver.py:352-361).</summary>
        public const byte BuiltInDeviceIndex = 7;

        public const ushort DeviceNameFeature = 0x0005;
        public const ushort HapticFeature = 0x19B0;

        /// <summary>A HID++ 2.0 error answer carries 0xFF where the feature
        /// index goes, then the request's feature index, function byte and
        /// the error code (LiveHaptics Packet.isError, OpenRGB SendAcked).</summary>
        public const byte ErrorFeatureIndex = 0xFF;
        public const byte ErrorBusy = 0x08;

        /// <summary>Waveform IDs from Solaar's HapticWaveForms table, in the
        /// order Logitech's Actions SDK lists the waveforms. The SDK describes
        /// sharp_collision as a "High-intensity impact simulation",
        /// damp_collision as a "Medium-intensity impact with gradual decay"
        /// and subtle_collision as "Low-intensity feedback for light contact
        /// events".</summary>
        public const byte SharpCollision = 0x02;
        public const byte DampCollision = 0x03;
        public const byte SubtleCollision = 0x04;

        public const byte FunctionGetCapabilities = 0;
        public const byte FunctionGetConfiguration = 1;
        public const byte FunctionPlay = 4;

        /// <summary>A long request: report ID, device index, feature index,
        /// function in the high nibble with the software ID in the low one,
        /// and up to 16 parameter bytes.</summary>
        public static byte[] Frame(byte deviceIndex, byte featureIndex, byte function, ReadOnlySpan<byte> parameters)
        {
            var frame = new byte[LongReportLength];
            frame[0] = LongReportId;
            frame[1] = deviceIndex;
            frame[2] = featureIndex;
            frame[3] = (byte)((function << 4) | SoftwareId);
            parameters.Slice(0, Math.Min(parameters.Length, LongReportLength - 4)).CopyTo(frame.AsSpan(4));
            return frame;
        }

        /// <summary>A very long request, laid out as a long one with room for
        /// 60 parameter bytes (OpenRGB LogitechHIDPP20Controller.cpp
        /// SendVeryLongFrame).</summary>
        public static byte[] VeryLongFrame(byte deviceIndex, byte featureIndex, byte function, ReadOnlySpan<byte> parameters)
        {
            var frame = new byte[VeryLongReportLength];
            frame[0] = VeryLongReportId;
            frame[1] = deviceIndex;
            frame[2] = featureIndex;
            frame[3] = (byte)((function << 4) | SoftwareId);
            parameters.Slice(0, Math.Min(parameters.Length, VeryLongReportLength - 4)).CopyTo(frame.AsSpan(4));
            return frame;
        }

        /// <summary>Root (feature index 0) function 0, getFeature: the answer
        /// carries the feature's index in byte 0, or 0 when the device lacks
        /// it.</summary>
        public static byte[] GetFeature(byte deviceIndex, ushort featureId)
            => Frame(deviceIndex, 0x00, 0, new[] { (byte)(featureId >> 8), (byte)featureId });

        /// <summary>Function 4, play, as OpenLogi documents it: the waveform
        /// and two zero bytes.</summary>
        public static byte[] Play(byte deviceIndex, byte hapticIndex, byte waveform)
            => Frame(deviceIndex, hapticIndex, FunctionPlay, new[] { waveform, (byte)0, (byte)0 });

        /// <summary>Whether a report answers the request named by the device
        /// index, feature index and function: an answer echoes all three with
        /// PadForge's software ID, and an error carries them after 0xFF.</summary>
        public static HidppReplyKind Match(ReadOnlySpan<byte> report, byte deviceIndex, byte featureIndex,
            byte function, out byte errorCode)
        {
            errorCode = 0;
            if (report.Length < 6 || report[0] is not (LongReportId or VeryLongReportId) || report[1] != deviceIndex)
                return HidppReplyKind.None;
            byte functionByte = (byte)((function << 4) | SoftwareId);
            if (report[2] == featureIndex && report[3] == functionByte)
                return HidppReplyKind.Answer;
            if (report[2] == ErrorFeatureIndex && report[3] == featureIndex && report[4] == functionByte)
            {
                errorCode = report[5];
                return HidppReplyKind.Error;
            }
            return HidppReplyKind.None;
        }

        /// <summary>A HID++ 1.0 error for a request: a short report from a
        /// receiver, 0x8F, then the request's feature index and function
        /// byte, then the code (Solaar base.py:841-854). It comes on the
        /// receiver's short collection, which a channel reads only for a
        /// known receiver.</summary>
        public static HidppReplyKind MatchHidpp10Error(ReadOnlySpan<byte> report, byte deviceIndex, byte featureIndex,
            byte function, out byte errorCode)
        {
            errorCode = 0;
            if (report.Length < 6 || report[0] != Peripherals.HidppReceiverProtocol.ShortReportId
                || report[1] != deviceIndex || report[2] != Peripherals.HidppReceiverProtocol.ErrorMessage)
                return HidppReplyKind.None;
            if (report[3] != featureIndex || report[4] != (byte)((function << 4) | SoftwareId))
                return HidppReplyKind.None;
            errorCode = report[5];
            return HidppReplyKind.Error;
        }

        /// <summary>HID++ 1.0's INVALID_SUB_ID: the device answers only HID++
        /// 1.0 (Solaar base.py:847-849).</summary>
        public const byte Hidpp10InvalidSubId = 0x01;

        /// <summary>The supported-waveform mask from a getCapabilities answer,
        /// big-endian in payload bytes 4 to 7 (Solaar reads
        /// int.from_bytes(response[4:8]), OpenLogi the same bytes as a
        /// big-endian u32). Bit N set means waveform N plays.</summary>
        public static uint WaveformMask(HidppReply capabilities)
            => ((uint)capabilities.Param(4) << 24) | ((uint)capabilities.Param(5) << 16)
               | ((uint)capabilities.Param(6) << 8) | capabilities.Param(7);

        /// <summary>Bit 0 of byte 0 of a getConfiguration answer (Solaar
        /// HapticLevel.read: result[0] &amp; 0x01 == 0 means disabled).</summary>
        public static bool FeedbackEnabled(HidppReply configuration) => (configuration.Param(0) & 0x01) != 0;

        /// <summary>The Bluetooth LE HID service class in a Windows interface
        /// path. A Bluetooth mouse is always the device itself and answers
        /// slower: OpenRGB sizes its Bluetooth first-contact window at 700 ms
        /// for a link that answers "in ~300ms cold" (HIDPP20_POLICY_BLUETOOTH),
        /// against 300 ms for a receiver's first contact.</summary>
        public static bool IsBluetoothPath(string path)
            => path != null && path.IndexOf("00001812-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase) >= 0;

        public static int TimeoutMs(bool bluetooth) => bluetooth ? 700 : 300;
    }

    internal enum HidppReplyKind
    {
        /// <summary>Not an answer to the pending request, or, from a register
        /// read, no request at all.</summary>
        None,
        Answer,
        Error,
        Timeout,
        /// <summary>The write itself failed: the collection is gone.</summary>
        WriteFailed,
    }

    /// <summary>One answer to a request, or the reason there is none.</summary>
    internal readonly struct HidppReply
    {
        private readonly byte[] _report;

        public HidppReplyKind Kind { get; }
        public byte ErrorCode { get; }

        public HidppReply(HidppReplyKind kind, byte errorCode = 0, byte[] report = null)
        {
            Kind = kind;
            ErrorCode = errorCode;
            _report = report;
        }

        /// <summary>Payload byte <paramref name="index"/>, after the 4-byte
        /// header, or 0 past the end.</summary>
        public byte Param(int index)
            => _report != null && index >= 0 && 4 + index < _report.Length ? _report[4 + index] : (byte)0;

        /// <summary>An error a receiver sent in HID++ 1.0's short form, whose
        /// codes differ from HID++ 2.0's.</summary>
        public bool Hidpp10
            => Kind == HidppReplyKind.Error && _report != null && _report.Length > 0
               && _report[0] == Peripherals.HidppReceiverProtocol.ShortReportId;
    }

    /// <summary>
    /// How a request waits for its answer (#494). The worker that owns the
    /// HID++ channels also plays rumble on them, and a request to a device
    /// that is asleep waits out its whole timeout, 300 ms for a slot and
    /// 1800 ms for a receiver's pairing record. So the wait is cut into
    /// slices, and after each slice that brought no answer the worker gets a
    /// turn to send the pulses that came due.
    ///
    /// <para>A pulse may go out while a request is unanswered, to another
    /// device index or to the same one. An answer repeats its request's
    /// device index, feature index, function and software ID unchanged
    /// (Logitech's "HID++ 2.0 Draft Specification", p.3), which is all a
    /// request is matched by, here (<see cref="HidppHapticProtocol.Match"/>)
    /// as in Solaar (base.py:729-781). A pulse is function 4 of the haptic
    /// feature, which no request ever is, so its answer can end no wait.
    /// Logitech's own example in that document sends nine requests to nine
    /// device indexes of one receiver within 50 ms and reads the answers
    /// afterward (p.15). OpenRGB fires the frames of a claim at one device
    /// back to back and matches each answer afterward by feature, function
    /// and software ID (LogitechHIDPP20Controller.cpp:4103-4104 and
    /// 4169-4172). A device that is still working on one request when
    /// another comes answers the newcomer busy, "already processing a
    /// request from the same or another SW" (p.3). So the most a pulse
    /// beside a request can cost is that pulse, and the next one follows
    /// on its interval.</para>
    ///
    /// <para>The timeout is counted on the worker's clock, so a test moves
    /// it, and the slices are real waits on the answer itself, so an answer
    /// ends the wait at once.</para>
    /// </summary>
    internal sealed class HidppWaiter
    {
        private static readonly ManualResetEventSlim s_never = new(false);

        private readonly Func<long> _ticks;
        private readonly int _sliceMs;
        private readonly Action _betweenSlices;

        public HidppWaiter(Func<long> ticks, int sliceMs, Action betweenSlices)
        {
            _ticks = ticks ?? (() => Environment.TickCount64);
            _sliceMs = Math.Max(1, sliceMs);
            _betweenSlices = betweenSlices;
        }

        /// <summary>Waits until <paramref name="answered"/> is set or
        /// <paramref name="timeoutMs"/> have passed on the clock. True when
        /// it was set.</summary>
        public bool Wait(ManualResetEventSlim answered, int timeoutMs)
        {
            long start = _ticks();
            while (true)
            {
                long left = timeoutMs - (_ticks() - start);
                if (left <= 0) return answered.IsSet;
                if (answered.Wait((int)Math.Min(left, _sliceMs))) return true;
                try { _betweenSlices?.Invoke(); } catch { }
            }
        }

        /// <summary>Lets <paramref name="ms"/> pass on the clock, in the
        /// same slices: the pause before a busy request is sent again.</summary>
        public void Pause(int ms) => Wait(s_never, ms);
    }

    /// <summary>A HID++ long collection that can be asked questions. The
    /// production channel reads with <see cref="VendorHidReader"/> and writes
    /// with <see cref="RawHidOutput"/>. Tests answer from a script.</summary>
    internal interface IHidppChannel : IDisposable
    {
        string Path { get; }
        bool Bluetooth { get; }

        /// <summary>How a request and a register read on this channel wait
        /// for their answer. Null waits the whole timeout in one piece. The
        /// worker sets its own, which lets its pulses out meanwhile.</summary>
        HidppWaiter Waiter { get; set; }

        /// <summary>Writes one frame and returns false when the write failed.</summary>
        bool Write(byte[] frame);

        /// <summary>Sends a request and waits up to <paramref name="timeoutMs"/>
        /// for its answer, retrying once when the device answers busy.</summary>
        HidppReply Request(byte deviceIndex, byte featureIndex, byte function, byte[] parameters, int timeoutMs);

        /// <summary>Sends a frame as it was built, long or very long, as a
        /// request, and waits as <see cref="Request"/> does for the answer
        /// that repeats its header (#557).</summary>
        HidppReply RequestFrame(byte[] frame, int timeoutMs);

        /// <summary>Whether the channel reaches the device's very long
        /// report (<see cref="HidppHapticProtocol.VeryLongReportId"/>), to
        /// write it and to read its answers.</summary>
        bool HasVeryLong { get; }

        /// <summary>Reads one record of a receiver's long register
        /// (<see cref="Peripherals.HidppReceiverProtocol"/>): the record, a
        /// HID++ 1.0 error, or a timeout. A channel opened without the
        /// receiver's short collection, or one already closed, asks nothing
        /// and answers None, and a short write that fails answers
        /// WriteFailed.</summary>
        HidppReply ReadReceiverRegister(byte register, byte sub, int timeoutMs);
    }

    /// <summary>
    /// A shared open of one HID++ long collection. Reads run on a
    /// <see cref="VendorHidReader"/> thread, so Logi Options+ and other HID++
    /// clients holding the same collection keep their own reports, and
    /// writes go through <see cref="RawHidOutput.Write"/>. One request is in
    /// flight at a time, the worker's. A frame the worker writes without
    /// waiting, a pulse above all, may go out while that request is still
    /// unanswered (<see cref="HidppWaiter"/>).
    /// </summary>
    internal sealed class HidppChannel : IHidppChannel
    {
        /// <summary>How long a request waits before it is sent again to a
        /// device that answered busy.</summary>
        internal const int BusyRetryMs = 50;

        private sealed class Pending
        {
            public readonly byte DeviceIndex;
            public readonly byte FeatureIndex;
            public readonly byte Function;
            public readonly ManualResetEventSlim Done = new(false);
            public HidppReply Reply;
            public int Claimed;

            public Pending(byte deviceIndex, byte featureIndex, byte function)
            {
                DeviceIndex = deviceIndex;
                FeatureIndex = featureIndex;
                Function = function;
            }
        }

        /// <summary>A receiver register read in flight: its record comes back
        /// on the long collection, its error on the short one.</summary>
        private sealed class RegisterPending
        {
            public readonly byte Register;
            public readonly byte Sub;
            public readonly ManualResetEventSlim Done = new(false);
            public HidppReply Reply;
            public int Claimed;

            public RegisterPending(byte register, byte sub)
            {
                Register = register;
                Sub = sub;
            }
        }

        private readonly VendorHidReader _reader;
        private VendorHidReader _shortReader;
        private string _shortPath;
        private VendorHidReader _veryLongReader;
        private string _veryLongPath;
        private Pending _pending;
        private RegisterPending _registerPending;
        private int _disposed;

        /// <summary>Where a frame goes: <see cref="RawHidOutput.Write"/>,
        /// or a test's own writer.</summary>
        private readonly Func<string, byte[], bool> _write;

        public string Path { get; }
        public bool Bluetooth { get; }
        public HidppWaiter Waiter { get; set; }

        private HidppChannel(VendorHidCollection collection, VendorHidCollection shortCollection,
            VendorHidCollection veryLongCollection)
        {
            Path = collection.Path;
            Bluetooth = HidppHapticProtocol.IsBluetoothPath(collection.Path);
            _write = RawHidOutput.Write;
            _reader = new VendorHidReader(collection);
            _reader.ReportReceived += OnReport;
            if (shortCollection != null)
            {
                _shortPath = shortCollection.Path;
                _shortReader = new VendorHidReader(shortCollection);
                _shortReader.ReportReceived += OnReport;
            }
            if (veryLongCollection != null)
            {
                _veryLongPath = veryLongCollection.Path;
                _veryLongReader = new VendorHidReader(veryLongCollection);
                _veryLongReader.ReportReceived += OnReport;
            }
        }

        /// <summary>Test seam: a channel with no reader and no device. Its
        /// frames go to <paramref name="write"/> with the path they were
        /// meant for, and the test hands it the reports a device would send
        /// through <see cref="Feed"/>, so the matching and the waiting that
        /// run are the shipping ones.</summary>
        internal HidppChannel(string path, string shortPath, Func<string, byte[], bool> write, string veryLongPath = null)
        {
            Path = path;
            Bluetooth = HidppHapticProtocol.IsBluetoothPath(path);
            _shortPath = shortPath;
            _veryLongPath = veryLongPath;
            _write = write;
        }

        /// <summary>Test seam: one report, as a reader thread delivers it.</summary>
        internal void Feed(byte[] report) => OnReport(null, report, report.Length);

        /// <summary>Opens the collection's reader, or returns null when it
        /// cannot be opened. A receiver's short collection, when given, is
        /// opened beside it for the pairing read, and a channel that cannot
        /// open it only loses that read. A keyboard's very long collection,
        /// when given, is opened the same way for its key colors (#557),
        /// and a channel that cannot open it paints the keyboard one color.</summary>
        public static HidppChannel Open(VendorHidCollection collection, VendorHidCollection shortCollection = null,
            VendorHidCollection veryLongCollection = null)
        {
            var channel = new HidppChannel(collection, shortCollection, veryLongCollection);
            if (!channel._reader.Open())
            {
                channel.Dispose();
                return null;
            }
            if (channel._shortReader != null && !channel._shortReader.Open())
            {
                channel._shortReader.ReportReceived -= channel.OnReport;
                channel._shortReader.Dispose();
                channel._shortReader = null;
                channel._shortPath = null;
            }
            if (channel._veryLongReader != null && !channel._veryLongReader.Open())
            {
                channel._veryLongReader.ReportReceived -= channel.OnReport;
                channel._veryLongReader.Dispose();
                channel._veryLongReader = null;
                channel._veryLongPath = null;
            }
            return channel;
        }

        public bool HasVeryLong => _veryLongPath != null;

        private void OnReport(VendorHidReader reader, byte[] buffer, int length)
        {
            var register = Volatile.Read(ref _registerPending);
            if (register != null)
            {
                var registerKind = Peripherals.HidppReceiverProtocol.Match(buffer.AsSpan(0, length),
                    register.Register, register.Sub, out byte registerError);
                if (registerKind != HidppReplyKind.None)
                {
                    if (Interlocked.Exchange(ref register.Claimed, 1) == 0)
                    {
                        register.Reply = new HidppReply(registerKind, registerError, buffer.AsSpan(0, length).ToArray());
                        register.Done.Set();
                    }
                    return;
                }
            }
            var pending = Volatile.Read(ref _pending);
            if (pending == null) return;
            var kind = HidppHapticProtocol.Match(buffer.AsSpan(0, length), pending.DeviceIndex,
                pending.FeatureIndex, pending.Function, out byte errorCode);
            if (kind == HidppReplyKind.None)
                kind = HidppHapticProtocol.MatchHidpp10Error(buffer.AsSpan(0, length), pending.DeviceIndex,
                    pending.FeatureIndex, pending.Function, out errorCode);
            if (kind == HidppReplyKind.None) return;
            if (Interlocked.Exchange(ref pending.Claimed, 1) != 0) return;
            pending.Reply = new HidppReply(kind, errorCode, buffer.AsSpan(0, length).ToArray());
            pending.Done.Set();
        }

        /// <summary>Writes a frame to the collection that carries its report:
        /// a very long frame to the very long collection, which a channel
        /// without one cannot send.</summary>
        public bool Write(byte[] frame)
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            if (frame.Length > 0 && frame[0] == HidppHapticProtocol.VeryLongReportId)
                return _veryLongPath != null && _write(_veryLongPath, frame);
            return _write(Path, frame);
        }

        /// <summary>Waits for an answer through the worker's waiter when it
        /// set one, so its pulses go out meanwhile, and in one piece
        /// otherwise.</summary>
        private bool Await(ManualResetEventSlim done, int timeoutMs)
        {
            var waiter = Waiter;
            return waiter != null ? waiter.Wait(done, timeoutMs) : done.Wait(timeoutMs);
        }

        public HidppReply Request(byte deviceIndex, byte featureIndex, byte function, byte[] parameters, int timeoutMs)
            => RequestFrame(HidppHapticProtocol.Frame(deviceIndex, featureIndex, function, parameters), timeoutMs);

        public HidppReply RequestFrame(byte[] frame, int timeoutMs)
        {
            byte deviceIndex = frame[1], featureIndex = frame[2], function = (byte)(frame[3] >> 4);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var pending = new Pending(deviceIndex, featureIndex, function);
                Volatile.Write(ref _pending, pending);
                try
                {
                    if (!Write(frame))
                        return new HidppReply(HidppReplyKind.WriteFailed);
                    if (!Await(pending.Done, timeoutMs))
                        return new HidppReply(HidppReplyKind.Timeout);
                    var reply = pending.Reply;
                    // OpenRGB retries a busy answer after a backoff
                    // (SendAcked, retry_on_busy), and so does this, once.
                    // HID++ 2.0's BUSY only: HID++ 1.0's 0x08 is UNKNOWN_DEVICE.
                    if (reply.Kind == HidppReplyKind.Error && !reply.Hidpp10
                        && reply.ErrorCode == HidppHapticProtocol.ErrorBusy && attempt == 0)
                    {
                        var waiter = Waiter;
                        if (waiter != null) waiter.Pause(BusyRetryMs);
                        else Thread.Sleep(BusyRetryMs);
                        continue;
                    }
                    return reply;
                }
                finally
                {
                    // Not disposed: the reader thread may still be setting
                    // it, and a ManualResetEventSlim whose WaitHandle was
                    // never read holds no kernel handle.
                    Volatile.Write(ref _pending, null);
                }
            }
            return new HidppReply(HidppReplyKind.Timeout);
        }

        public HidppReply ReadReceiverRegister(byte register, byte sub, int timeoutMs)
        {
            string shortPath = _shortPath;
            // Nothing to ask through: the read never goes out, which is not
            // a receiver that left it unanswered.
            if (shortPath == null || Volatile.Read(ref _disposed) != 0)
                return new HidppReply(HidppReplyKind.None);
            var pending = new RegisterPending(register, sub);
            Volatile.Write(ref _registerPending, pending);
            try
            {
                if (!_write(shortPath, Peripherals.HidppReceiverProtocol.ReadRegister(register, sub)))
                    return new HidppReply(HidppReplyKind.WriteFailed);
                return Await(pending.Done, timeoutMs) ? pending.Reply : new HidppReply(HidppReplyKind.Timeout);
            }
            finally
            {
                Volatile.Write(ref _registerPending, null);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // A channel a test built has no reader and no handle to drop.
            if (_reader == null) return;
            _reader.ReportReceived -= OnReport;
            _reader.Dispose();
            RawHidOutput.ResetDevice(Path);
            var shortReader = _shortReader;
            if (shortReader != null)
            {
                shortReader.ReportReceived -= OnReport;
                shortReader.Dispose();
                RawHidOutput.ResetDevice(_shortPath);
            }
            var veryLongReader = _veryLongReader;
            if (veryLongReader != null)
            {
                veryLongReader.ReportReceived -= OnReport;
                veryLongReader.Dispose();
                RawHidOutput.ResetDevice(_veryLongPath);
            }
        }
    }

    /// <summary>What a channel has answered so far, kept for as long as its
    /// path is present so a scan asks again only what is still open.</summary>
    internal sealed class HidppPathState
    {
        /// <summary>Index 0 stands for device index 0xFF, 1 to 6 for a
        /// receiver's slots, and 7 for the device a receiver carries itself
        /// (<see cref="BuiltInDevice"/>).</summary>
        internal readonly bool[] Settled = new bool[HidppHapticProtocol.BuiltInDeviceIndex + 1];
        internal readonly long[] RetryAt = new long[HidppHapticProtocol.BuiltInDeviceIndex + 1];
        internal readonly int[] Misses = new int[HidppHapticProtocol.BuiltInDeviceIndex + 1];

        /// <summary>Set once device index 0xFF answered: the collection is a
        /// device, never a receiver, and slots 1 to 6 are not asked.</summary>
        internal bool Direct;

        /// <summary>Set once a slot answered: the collection is a receiver,
        /// and 0xFF is not asked again.</summary>
        internal bool Receiver;

        /// <summary>A write failed: the collection went away.</summary>
        internal bool Dead;

        /// <summary>How the receiver behind this path keeps its pairing
        /// table, from its product ID, or None for a device or a receiver
        /// whose table is not read.</summary>
        internal Peripherals.HidppReceiverKind ReceiverKind;

        /// <summary>The receiver carries a device of its own at
        /// <see cref="HidppHapticProtocol.BuiltInDeviceIndex"/>, which its
        /// pairing table does not list, so that index is asked as a slot
        /// that is never read as empty
        /// (<see cref="Peripherals.HidppReceiverProtocol.HasBuiltInDevice"/>).</summary>
        internal bool BuiltInDevice;

        /// <summary>Slots the receiver's pairing table says hold no device
        /// (<see cref="Peripherals.HidppReceiverProtocol"/>). Never asked, and
        /// never pending: nothing there can wake. Read again at each scan, so
        /// a device paired later is found.</summary>
        internal readonly bool[] Unpaired = new bool[HidppHapticProtocol.BuiltInDeviceIndex + 1];

        /// <summary>Slots where a device with an output was found, so one
        /// that stops answering there went to sleep.</summary>
        internal readonly bool[] Held = new bool[HidppHapticProtocol.BuiltInDeviceIndex + 1];

        /// <summary>Pairing reads that went unanswered in a row. A receiver
        /// that never answers the register stops being asked after
        /// <see cref="Peripherals.HidppUnitProbe.PairingGiveUp"/>, and its
        /// slots go back to the walk. A read that never went out counts
        /// toward nothing.</summary>
        internal int PairingMisses;

        /// <summary>Misses after which a slot that never answered stops
        /// waiting: its first walk of backoffs, 5, 10 and 15 seconds, is
        /// over.</summary>
        internal const int FirstWalkMisses = 3;

        internal static int Slot(byte deviceIndex)
            => deviceIndex == HidppHapticProtocol.DirectDeviceIndex ? 0 : deviceIndex;

        /// <summary>Forgets a slot's answer so the next scan asks again, for a
        /// device that stopped answering.</summary>
        internal void Reopen(byte deviceIndex)
        {
            int slot = Slot(deviceIndex);
            Settled[slot] = false;
            Misses[slot] = 0;
            RetryAt[slot] = 0;
        }

        /// <summary>A slot still to be asked: one the walk has not settled
        /// and the receiver has not reported empty.</summary>
        internal bool Open(int slot) => !Settled[slot] && !Unpaired[slot];

        /// <summary>An open slot that keeps the scan on its fast cadence:
        /// one that held a device before it went to sleep, or one still on
        /// its first walk. A slot that never answered, a device asleep since
        /// launch or an empty slot of a receiver whose table is not read,
        /// stops waiting after its first walk. It is still asked whenever a
        /// scan runs, and while it is open a row in its container keeps the
        /// outputs it has.</summary>
        internal bool Waiting(int slot)
            => Open(slot) && (Held[slot] || Misses[slot] < FirstWalkMisses);
    }

    /// <summary>
    /// The haptic reads a found unit still needs (#494): its name, the way
    /// Solaar reads feature 0x0005, and its 0x19B0 configuration, which the
    /// HID++ worker asks again to tell an awake device from one that went to
    /// sleep. A slot that never answers is asked again after a backoff that
    /// doubles from 5 seconds to 15, so a device that wakes is found within
    /// about 15 seconds and an idle receiver costs a few requests a minute.
    /// LiveHaptics probes every 2. <see cref="Peripherals.HidppUnitProbe"/>
    /// walks the slots.
    /// </summary>
    internal static class HidppHapticProbe
    {
        internal const int FirstRetryMs = 5000;
        internal const int MaxRetryMs = 15000;

        /// <summary>Feature 0x0005 the way Solaar reads it (hidpp20.py
        /// get_name): function 0 gives the length, function 1 at the current
        /// offset gives the next fragment, and the bytes are UTF-8.</summary>
        internal static string ReadName(IHidppChannel channel, byte deviceIndex, int timeout)
        {
            var feature = channel.Request(deviceIndex, 0x00, 0, FeatureId(HidppHapticProtocol.DeviceNameFeature), timeout);
            if (feature.Kind != HidppReplyKind.Answer || feature.Param(0) == 0) return null;
            return ReadName(channel, deviceIndex, feature.Param(0), timeout);
        }

        /// <summary>The name through a 0x0005 index the caller already has,
        /// with no second Root lookup. A Root answer carries the feature's
        /// index and not its ID, and every request goes out under the one
        /// software ID, so a late answer to one lookup would be taken for the
        /// next one's, the 0x19B0 lookup that follows the name.</summary>
        internal static string ReadName(IHidppChannel channel, byte deviceIndex, byte nameIndex, int timeout)
        {
            var count = channel.Request(deviceIndex, nameIndex, 0, Array.Empty<byte>(), timeout);
            if (count.Kind != HidppReplyKind.Answer) return null;
            int length = Math.Min((int)count.Param(0), 64);

            var bytes = new List<byte>(length);
            while (bytes.Count < length)
            {
                var fragment = channel.Request(deviceIndex, nameIndex, 1, new[] { (byte)bytes.Count }, timeout);
                if (fragment.Kind != HidppReplyKind.Answer) return null;
                for (int i = 0; i < HidppHapticProtocol.LongReportLength - 4 && bytes.Count < length; i++)
                    bytes.Add(fragment.Param(i));
            }
            string name = Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\0').Trim();
            return name.Length == 0 ? null : name;
        }

        /// <summary>The configuration again, for a unit already found: a live
        /// device answers, so a timeout says it went to sleep or away. Null on
        /// no answer.</summary>
        internal static bool? ReadFeedbackEnabled(IHidppChannel channel, byte deviceIndex, byte hapticIndex)
        {
            var configuration = channel.Request(deviceIndex, hapticIndex,
                HidppHapticProtocol.FunctionGetConfiguration, Array.Empty<byte>(),
                HidppHapticProtocol.TimeoutMs(channel.Bluetooth));
            // A receiver's HID++ 1.0 error says the device cannot be reached,
            // asleep or away (Solaar base.py:841-854), so it counts as no
            // answer. A HID++ 2.0 error comes from the device itself.
            return configuration.Kind switch
            {
                HidppReplyKind.Answer => HidppHapticProtocol.FeedbackEnabled(configuration),
                HidppReplyKind.Error when configuration.Hidpp10 => null,
                HidppReplyKind.Error => true,
                _ => null,
            };
        }

        private static byte[] FeatureId(ushort featureId) => new[] { (byte)(featureId >> 8), (byte)featureId };
    }
}
