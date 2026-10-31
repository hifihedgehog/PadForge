using System;
using System.Collections.Generic;
using System.Threading;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>One TouchSense mouse bound to WinUSB, as the look for bound
    /// mice finds it: the interface path Windows gives the bound node and
    /// the IDs in it.</summary>
    internal readonly record struct TouchSenseMouse(string Path, ushort VendorId, ushort ProductId);

    /// <summary>
    /// One WinUSB handle PadForge holds on a device bound to its driver:
    /// a TouchSense mouse's (#494) or a keyboard's (#549). What the table of
    /// open handles (<see cref="TouchSenseChannels"/>) needs of each, so a
    /// driver switch can close any of them before Windows restarts the node.
    /// </summary>
    internal interface IWinUsbChannel : IDisposable
    {
        /// <summary>The device's WinUSB interface path.</summary>
        string Path { get; }

        /// <summary>False from the moment anyone closed the channel. A
        /// closed channel never opens again: the device gets a new one.</summary>
        bool IsOpen { get; }
    }

    /// <summary>
    /// The one WinUSB handle of a bound TouchSense mouse (#494). WinUSB
    /// lets one handle open a device (SDL3-build SDL
    /// docs/README-vendor-usb.md:65-66), so the row that reads the mouse's
    /// pointer and the backend that buzzes its motor share one of these
    /// per mouse: one thread reads, one thread sends, and either may close
    /// it.
    /// </summary>
    internal interface ITouchSenseChannel : IWinUsbChannel
    {
        /// <summary>The mouse's input report is the iFeel Mouse's, so what
        /// <see cref="TryRead"/> returns decodes as buttons, motion and
        /// wheel (<see cref="TouchSenseProtocol.DeclaresPointerReport"/>).</summary>
        bool ReadsPointer { get; }

        /// <summary>Sends one seven-byte frame. False when the mouse did
        /// not take it or the channel is closed. The caller then closes the
        /// channel.</summary>
        bool Send(byte[] frame);

        /// <summary>Waits for one input report, for at most the pipe's
        /// timeout. True with a length above zero for a report, true with
        /// zero when none came in time, false when the mouse left or the
        /// channel is closed.</summary>
        bool TryRead(Span<byte> report, out int length);
    }

    /// <summary>
    /// One opened WinUSB interface, as little of WinUSB as the channel
    /// needs, so the channel's own rules run against a scripted one in the
    /// tests. A setup packet is the eight bytes that go on the wire.
    /// </summary>
    internal interface ITouchSenseUsb : IDisposable
    {
        /// <summary>The class of interface 0 and the addresses of its
        /// first interrupt IN and first interrupt OUT pipe, 0 for one it
        /// does not have. False when the interface cannot be read.</summary>
        bool Describe(out byte interfaceClass, out byte interruptIn, out byte interruptOut);

        /// <summary>The device's configuration descriptor, as many bytes
        /// as fit. The number of bytes read, 0 on failure.</summary>
        int ReadConfiguration(byte[] buffer);

        bool ControlIn(byte[] setup, byte[] buffer, out int length);
        bool ControlOut(byte[] setup, byte[] data);
        bool WritePipe(byte pipe, byte[] data);

        /// <summary>One read of a pipe: the bytes read, 0 when the pipe's
        /// timeout ran out first, or -1 with the Win32 error.</summary>
        int ReadPipe(byte pipe, byte[] buffer, out int error);

        /// <summary>The transfer timeout of a pipe, 0 for the control pipe.</summary>
        void SetTimeout(byte pipe, int milliseconds);

        /// <summary>Ends a transfer another thread waits in. Any thread.</summary>
        void CancelIo();
    }

    /// <summary>
    /// The channel's rules over one WinUSB interface: how it opens, which
    /// transfer carries a frame, what a failed read means and how it
    /// closes. The WinUSB calls are those <see cref="Ds3DirectService"/>
    /// makes for a DualShock 3 (its OpenUsb, UsbSetReport and UsbReadLoop,
    /// Ds3DirectService.cs:559-612, 886-897 and 964-1022).
    ///
    /// <para>Open. The interface must be HID class with an interrupt IN
    /// pipe. The report descriptor is read at the length the HID descriptor
    /// gives and must declare the buzz report
    /// (<see cref="TouchSenseProtocol.DeclaresBuzzReport"/>), so no frame
    /// ever goes to a device that does not declare it. SET_IDLE goes out
    /// once. The mouse has nothing to claim and nothing to restore: the
    /// motor is silent unless a frame is playing.</para>
    ///
    /// <para>Send. The control SET_REPORT first, the transfer the vendor's
    /// own stack used (<see cref="TouchSenseProtocol.SetReportSetup"/>).
    /// When it fails and the interrupt OUT pipe takes the same seven
    /// bytes, as Bobruk's Linux driver sends them (tactile-ifeel
    /// iFeel/ifeel.c:92-110 and 338-339), the channel stays on the pipe
    /// for the rest of its life.</para>
    ///
    /// <para>Close. Any thread may close it, once, and a close returns
    /// only after the handle is gone, so a mouse is never opened while a
    /// handle to it is still held.</para>
    /// </summary>
    internal sealed class TouchSenseChannel : ITouchSenseChannel
    {
        internal const byte HidInterfaceClass = 0x03;
        internal const byte ControlPipe = 0x00;

        /// <summary>How long a control transfer or an interrupt write may
        /// take, hidapi's own bounds (hidapi libusb/hid.c:1876-1882 and
        /// 1611-1615). WinUSB's default is five seconds on the control pipe
        /// and none on the others, and libusb sets the control pipe's
        /// through pipe 0 as this does (libusb-src
        /// libusb/os/windows_winusb.c:3575-3579).</summary>
        internal const int ControlTimeoutMs = 1000;
        internal const int WriteTimeoutMs = 1000;

        /// <summary>How long a read waits for a report before it returns
        /// with none, so the reader notices a close (Ds3DirectService.cs:
        /// 603-606).</summary>
        internal const int ReadTimeoutMs = 100;

        /// <summary>The most a read asks for: the pipe's packet size on the
        /// iFeel Mouse (ifeel.log,v:157-158), twice its report.</summary>
        internal const int ReadBufferLength = 8;

        internal const int MaxConfigurationLength = 512;

        /// <summary>How many reads in a row may fail for a reason that does
        /// not name the device as gone before the channel gives up, each
        /// after a pause of <see cref="ReadFaultPauseMs"/>: about a second,
        /// the time the DualShock 3 reader waits before it looks for its
        /// device again (Ds3DirectService.cs:1013-1019).</summary>
        internal const int MaxReadFaults = 500;
        internal const int ReadFaultPauseMs = 2;

        // The Win32 errors that say the device left or the transfer was
        // called off, the DualShock 3 reader's list (Ds3DirectService.cs:
        // 1009-1012).
        private const int ERROR_FILE_NOT_FOUND = 2, ERROR_INVALID_HANDLE = 6, ERROR_GEN_FAILURE = 31,
            ERROR_NO_SUCH_DEVICE = 433, ERROR_OPERATION_ABORTED = 995, ERROR_DEVICE_NOT_CONNECTED = 1167;

        private readonly ITouchSenseUsb _usb;
        private readonly byte _inPipe;
        private readonly byte _outPipe;
        private readonly int _maxReadFaults;
        private readonly int _readFaultPauseMs;
        private readonly byte[] _setReport = TouchSenseProtocol.SetReportSetup();
        private readonly byte[] _readBuffer = new byte[ReadBufferLength];

        // One sender and one reader at a time, each under its own lock, and
        // a close that waits for both. Order where two are held: the close
        // lock, then the send lock, then the read lock.
        private readonly object _closeLock = new();
        private readonly object _sendLock = new();
        private readonly object _readLock = new();
        private int _closed;
        private bool _freed;
        private bool _useInterruptOut;
        private int _readFaults;

        private TouchSenseChannel(string path, ITouchSenseUsb usb, byte inPipe, byte outPipe, bool readsPointer,
            int maxReadFaults, int readFaultPauseMs)
        {
            Path = path;
            _usb = usb;
            _inPipe = inPipe;
            _outPipe = outPipe;
            ReadsPointer = readsPointer;
            _maxReadFaults = maxReadFaults;
            _readFaultPauseMs = readFaultPauseMs;
        }

        public string Path { get; }
        public bool ReadsPointer { get; }
        public bool IsOpen => Volatile.Read(ref _closed) == 0;

        /// <summary>True once a frame went out on the interrupt OUT pipe
        /// because the control transfer failed.</summary>
        internal bool UsesInterruptOut
        {
            get { lock (_sendLock) return _useInterruptOut; }
        }

        /// <summary>Opens the bound mouse at a WinUSB interface path, or
        /// null when it cannot be opened or does not declare the buzz
        /// report. The test host opens no device
        /// (<see cref="RealTouchSenseAccess"/>): a test hands the other
        /// overload an interface of its own.</summary>
        public static TouchSenseChannel Open(string path)
        {
            if (RealTouchSenseAccess.Refused) return null;
            return Open(path, WinUsbTouchSenseUsb.Open(path));
        }

        /// <summary>The open sequence over an interface that is already
        /// open. Takes the interface over: it is closed here when the open
        /// fails. The last two parameters are a test's, so the read that
        /// gives up after a second of faults is run without the second.</summary>
        internal static TouchSenseChannel Open(string path, ITouchSenseUsb usb, int maxReadFaults = MaxReadFaults,
            int readFaultPauseMs = ReadFaultPauseMs)
        {
            if (usb == null) return null;
            TouchSenseChannel opened = null;
            try
            {
                // Before the open's own transfers, so each of them is bounded.
                usb.SetTimeout(ControlPipe, ControlTimeoutMs);
                if (!usb.Describe(out byte interfaceClass, out byte inPipe, out byte outPipe)) return null;
                // The addresses come from the device, as Bobruk's driver
                // takes them (ifeel.c:270-280). A mouse with no interrupt
                // OUT pipe still has the control transfer.
                if (interfaceClass != HidInterfaceClass || inPipe == 0) return null;

                var configuration = new byte[MaxConfigurationLength];
                int read = usb.ReadConfiguration(configuration);
                if (read <= 0 || read > configuration.Length) return null;
                int length = TouchSenseProtocol.ReportDescriptorLength(configuration.AsSpan(0, read));
                if (length == 0) return null;
                var descriptor = new byte[length];
                // A control IN can complete short, and a short descriptor
                // is no descriptor to judge a device by.
                if (!usb.ControlIn(TouchSenseProtocol.ReportDescriptorSetup(length), descriptor, out int got) || got != length)
                    return null;
                if (!TouchSenseProtocol.DeclaresBuzzReport(descriptor)) return null;
                bool readsPointer = TouchSenseProtocol.DeclaresPointerReport(descriptor);

                // Its answer is not waited on for anything: Bobruk's driver
                // ignores it too (ifeel.c:282).
                usb.ControlOut(TouchSenseProtocol.SetIdleSetup(), Array.Empty<byte>());
                usb.SetTimeout(inPipe, ReadTimeoutMs);
                if (outPipe != 0) usb.SetTimeout(outPipe, WriteTimeoutMs);
                opened = new TouchSenseChannel(path, usb, inPipe, outPipe, readsPointer, maxReadFaults, readFaultPauseMs);
                return opened;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (opened == null)
                {
                    try { usb.Dispose(); } catch { }
                }
            }
        }

        public bool Send(byte[] frame)
        {
            if (frame == null || frame.Length != TouchSenseProtocol.FrameLength) return false;
            lock (_sendLock)
            {
                if (!IsOpen) return false;
                try
                {
                    if (_useInterruptOut) return _usb.WritePipe(_outPipe, frame);
                    if (_usb.ControlOut(_setReport, frame)) return true;
                    // A close calls off the transfer a thread waits in. That
                    // failure is not the mouse refusing the control
                    // transfer, so nothing follows it onto a channel that
                    // is closing, and the choice of transfer stays.
                    if (!IsOpen) return false;
                    if (_outPipe == 0 || !_usb.WritePipe(_outPipe, frame)) return false;
                    _useInterruptOut = true;
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public bool TryRead(Span<byte> report, out int length)
        {
            length = 0;
            lock (_readLock)
            {
                if (!IsOpen) return false;
                int got, error;
                try { got = _usb.ReadPipe(_inPipe, _readBuffer, out error); }
                catch { return false; }
                if (got >= 0)
                {
                    _readFaults = 0;
                    length = Math.Min(Math.Min(got, _readBuffer.Length), report.Length);
                    _readBuffer.AsSpan(0, length).CopyTo(report);
                    return true;
                }
                if (IsGone(error) || ++_readFaults >= _maxReadFaults) return false;
            }
            // Outside the lock, so a close never waits out the pause.
            if (_readFaultPauseMs > 0) Thread.Sleep(_readFaultPauseMs);
            return IsOpen;
        }

        /// <summary>Whether a failed read's error says the device left or
        /// the read was called off, where any other error is tried again.</summary>
        internal static bool IsGone(int error)
            => error is ERROR_DEVICE_NOT_CONNECTED or ERROR_FILE_NOT_FOUND or ERROR_INVALID_HANDLE
                or ERROR_OPERATION_ABORTED or ERROR_GEN_FAILURE or ERROR_NO_SUCH_DEVICE;

        /// <summary>Closes the channel: no new transfer starts, the one a
        /// thread waits in is called off, and once both have returned the
        /// interface and its handle are freed, in that order, as the
        /// DualShock 3 service tears its own down (Ds3DirectService.cs:
        /// 1212-1236). Every caller returns only after the handle is gone.
        /// Never called from inside <see cref="Send"/> or
        /// <see cref="TryRead"/>, whose locks it takes.</summary>
        public void Dispose()
        {
            lock (_closeLock)
            {
                if (_freed) return;
                Volatile.Write(ref _closed, 1);
                try { _usb.CancelIo(); } catch { }
                lock (_sendLock)
                {
                    lock (_readLock)
                    {
                        try { _usb.Dispose(); } catch { }
                    }
                }
                _freed = true;
            }
        }
    }

    /// <summary>
    /// The channels of the bound mice that are open now, by interface path
    /// (#494). The rows open and close them, since a bound mouse has a row
    /// for as long as it is plugged in, and the backend that buzzes a mouse
    /// finds its channel here, so no second handle is ever asked of a
    /// device that takes one. A bound keyboard's channel is listed here too
    /// (#549), so the driver switch's <see cref="Hold"/> closes every WinUSB
    /// handle PadForge has on a node, whatever device it is.
    /// </summary>
    internal sealed class TouchSenseChannels
    {
        /// <summary>The table the app's rows and its backend share.</summary>
        internal static readonly TouchSenseChannels Shared = new();

        private readonly object _gate = new();
        private readonly Dictionary<string, IWinUsbChannel> _open = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The device nodes a driver switch holds, each as the
        /// token it has in an interface path, once for every hold on it.
        /// Guarded by <see cref="_gate"/>.</summary>
        private readonly List<string> _held = new();

        /// <summary>The paths an open is under way at, from
        /// <see cref="TryBeginOpen"/> to <see cref="EndOpen"/>. Guarded by
        /// <see cref="_gate"/>.</summary>
        private readonly List<string> _opening = new();

        /// <summary>How long a hold waits for an open that began before
        /// it. An open is over within its own timeouts: the close of the
        /// channel the mouse had, and three control transfers of a second
        /// each at most (TouchSenseChannel.Open).</summary>
        internal const int DefaultHoldWaitMs = 10000;

        /// <summary>Test seam: the wait of <see cref="Hold"/>, so the test
        /// of a wait that runs out does not sit through it.</summary>
        internal int HoldWaitMs = DefaultHoldWaitMs;

        /// <summary>Raised after a channel was listed, so a backend that
        /// waits for a mouse looks again at once. Any thread. Subscribers
        /// must not block.</summary>
        public event Action Opened;

        /// <summary>Lists a channel that was just opened. One whose node a
        /// driver switch holds by now is closed here and not listed: its
        /// open began before the hold did, and the switch waits on a node
        /// no handle may keep open (<see cref="Hold"/>).</summary>
        public void Publish(IWinUsbChannel channel)
        {
            if (channel == null || string.IsNullOrEmpty(channel.Path)) return;
            bool held;
            lock (_gate)
            {
                held = HeldLocked(channel.Path);
                if (!held) _open[channel.Path] = channel;
            }
            if (held)
            {
                // Outside the gate: a close waits for the transfer in flight.
                try { channel.Dispose(); } catch { }
                return;
            }
            try { Opened?.Invoke(); } catch { }
        }

        /// <summary>Says an open of the mouse at an interface path begins.
        /// False when a driver switch holds the mouse's node: nothing may
        /// open that mouse now. An open that does begin is ended with
        /// <see cref="EndOpen"/> on every way out of it, since a switch
        /// that starts meanwhile waits for it (<see cref="Hold"/>).</summary>
        public bool TryBeginOpen(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            lock (_gate)
            {
                if (HeldLocked(path)) return false;
                _opening.Add(path);
                return true;
            }
        }

        /// <summary>Says the open <see cref="TryBeginOpen"/> began is over:
        /// its channel is listed or closed, or it brought none.</summary>
        public void EndOpen(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (_gate)
            {
                int at = _opening.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) _opening.RemoveAt(at);
                Monitor.PulseAll(_gate);
            }
        }

        private bool HeldLocked(string path)
        {
            foreach (string token in _held)
                if (path.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private bool OpeningLocked(string token)
        {
            foreach (string path in _opening)
                if (path.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>The open channel of the mouse at a path, or null. A
        /// channel that was closed since it was listed is dropped here, and
        /// a keyboard's channel at the path is no mouse's.</summary>
        public ITouchSenseChannel Find(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (_gate)
            {
                if (!_open.TryGetValue(path, out var channel)) return null;
                if (channel.IsOpen) return channel as ITouchSenseChannel;
                _open.Remove(path);
                return null;
            }
        }

        /// <summary>Takes a channel off the list, unless a newer one for
        /// the same path took its place.</summary>
        public void Forget(IWinUsbChannel channel)
        {
            if (channel == null || string.IsNullOrEmpty(channel.Path)) return;
            lock (_gate)
            {
                if (_open.TryGetValue(channel.Path, out var listed) && ReferenceEquals(listed, channel))
                    _open.Remove(channel.Path);
            }
        }

        /// <summary>Closes the open channel of the mouse at a device node,
        /// returns once no handle of PadForge's is open on the node, and
        /// keeps the node closed until the hold it returns is let go. For
        /// the driver switch (VendorUsbDriverInstaller.Bind and Restore),
        /// which lets go of a node before it asks Windows to change that
        /// node's driver: the change restarts the node, and a handle of
        /// PadForge's is the one thing of its own that holds the node open.
        /// The switch takes seconds, and the device pass looks for bound
        /// mice every second, so the letting go lasts as long as the switch
        /// does: while the node is held no open of its mouse begins
        /// (<see cref="TryBeginOpen"/>). An open that began before the
        /// hold is waited for, up to <see cref="HoldWaitMs"/>, and the
        /// channel it brings is closed as it arrives
        /// (<see cref="Publish"/>). The mouse's row retires with its
        /// channel, and comes back if the mouse is still bound
        /// afterward.</summary>
        public IDisposable Hold(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return new NodeHold(null, null);
            // An interface path carries its node's instance ID with # for
            // each backslash, and a # after it.
            string token = instanceId.Replace('\\', '#') + "#";
            List<IWinUsbChannel> closing = null;
            lock (_gate)
            {
                _held.Add(token);
                foreach (var pair in _open)
                    if (pair.Key.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                        (closing ??= new List<IWinUsbChannel>()).Add(pair.Value);
                if (closing != null)
                    foreach (var channel in closing) _open.Remove(channel.Path);
            }
            if (closing != null)
            {
                // Outside the gate: a close waits for the transfer in flight.
                foreach (var channel in closing)
                {
                    try { channel.Dispose(); } catch { }
                }
            }
            // An open that began before the hold has a handle on the node,
            // or is about to. Its channel is closed as it is listed, and
            // the open says when it is over.
            lock (_gate)
            {
                long until = Environment.TickCount64 + HoldWaitMs;
                while (OpeningLocked(token))
                {
                    long left = until - Environment.TickCount64;
                    if (left <= 0 || !Monitor.Wait(_gate, (int)left)) break;
                }
            }
            return new NodeHold(this, token);
        }

        /// <summary>One hold on a node. Letting it go a second time does
        /// nothing, so a node held twice stays held until both are gone.</summary>
        private sealed class NodeHold : IDisposable
        {
            private TouchSenseChannels _table;
            private readonly string _token;

            public NodeHold(TouchSenseChannels table, string token)
            {
                _table = table;
                _token = token;
            }

            public void Dispose()
            {
                var table = Interlocked.Exchange(ref _table, null);
                if (table == null) return;
                lock (table._gate) table._held.Remove(_token);
            }
        }

        /// <summary>How many channels are listed.</summary>
        internal int Count
        {
            get { lock (_gate) return _open.Count; }
        }
    }
}
