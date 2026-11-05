using System;
using System.Threading;
using PadForge.Common.Input.Peripherals;

namespace PadForge.Common.Input.WinUsbKeyboards
{
    /// <summary>
    /// One opened WinUSB interface, as little of WinUSB as the keyboard
    /// channel needs, so the channel's rules run against a scripted one in
    /// the tests. <see cref="WinUsbTouchSenseUsb"/> serves it on the PC,
    /// the layer a bound TouchSense mouse is read through (#494). A setup
    /// packet is the eight bytes that go on the wire.
    /// </summary>
    internal interface IKeyboardUsb : IDisposable
    {
        /// <summary>The opened interface's number and class, its first
        /// interrupt IN pipe and that pipe's packet size, 0 for a pipe it
        /// does not have. False when the interface cannot be read.</summary>
        bool DescribeInterface(out byte number, out byte interfaceClass, out byte interruptIn, out ushort packetSize);

        /// <summary>The device's configuration descriptor, as many bytes
        /// as fit. The number of bytes read, 0 on failure.</summary>
        int ReadConfiguration(byte[] buffer);

        bool ControlIn(byte[] setup, byte[] buffer, out int length);
        bool ControlOut(byte[] setup, byte[] data);

        /// <summary>One read of a pipe: the bytes read, 0 when the pipe's
        /// timeout ran out first, or -1 with the Win32 error.</summary>
        int ReadPipe(byte pipe, byte[] buffer, out int error);

        /// <summary>The transfer timeout of a pipe, 0 for the control pipe.</summary>
        void SetTimeout(byte pipe, int milliseconds);

        /// <summary>Ends a transfer another thread waits in. Any thread.</summary>
        void CancelIo();
    }

    /// <summary>
    /// A bound keyboard's or mouse's open channel as its row reads it, so
    /// the row's rules run against a scripted channel in the tests.
    /// </summary>
    internal interface IKeyboardChannel : IWinUsbChannel
    {
        /// <summary>How a keyboard's input reports lay out its keys, null
        /// on a mouse's channel.</summary>
        HidKeyboardReports Reports { get; }

        /// <summary>How a mouse's input reports lay out its buttons, motion
        /// and wheel, null on a keyboard's channel.</summary>
        HidMouseReports Pointer { get; }

        /// <summary>The bytes one read asks for.</summary>
        int ReadLength { get; }

        /// <summary>The wheel counts a mouse's notch takes, 1 for a wheel
        /// with no Resolution Multiplier and on a keyboard's channel.</summary>
        int WheelMultiplier => 1;

        /// <summary>Waits for one input report, for at most the pipe's
        /// timeout. True with a length above zero for a report, true with
        /// zero when none came in time, false when the keyboard left or the
        /// channel is closed.</summary>
        bool TryRead(Span<byte> report, out int length);
    }

    /// <summary>
    /// The HID class requests a bound keyboard's open sends, for the
    /// interface it is read on. A keyboard on a composite device or a
    /// receiver sits on an interface above 0, so each request names its
    /// interface in wIndex, where the TouchSense mouse's requests name
    /// interface 0 (<see cref="TouchSenseProtocol"/>).
    /// </summary>
    internal static class WinUsbKeyboardProtocol
    {
        /// <summary>The longest report descriptor read from a keyboard
        /// (<see cref="HidKeyboardReports.MaxDescriptorLength"/>).</summary>
        public const int MaxReportDescriptor = HidKeyboardReports.MaxDescriptorLength;

        /// <summary>HID SET_IDLE with no duration for every report (USB HID
        /// 1.11 section 7.2.4, tinyusb hid.h:99), so the keyboard sends a
        /// report when a key changes and not again while it is held. The
        /// TouchSense open sends the same request to interface 0
        /// (<see cref="TouchSenseProtocol.SetIdleSetup"/>).</summary>
        public static byte[] SetIdleSetup(byte interfaceNumber)
            => new byte[] { 0x21, 0x0A, 0x00, 0x00, interfaceNumber, 0x00, 0x00, 0x00 };

        /// <summary>GET_DESCRIPTOR for an interface's report descriptor:
        /// type 0x22 in wValue's high byte, the interface in wIndex
        /// (<see cref="TouchSenseProtocol.ReportDescriptorSetup"/> asks
        /// interface 0 the same way).</summary>
        public static byte[] ReportDescriptorSetup(byte interfaceNumber, int length)
            => new byte[] { 0x81, 0x06, 0x00, 0x22, interfaceNumber, 0x00, (byte)length, (byte)(length >> 8) };

        /// <summary>HID GET_REPORT for a feature report (USB HID 1.11 section
        /// 7.2.1): type 3 in wValue's high byte, the report's ID in its low
        /// byte, the interface in wIndex.</summary>
        public static byte[] GetFeatureSetup(byte interfaceNumber, byte reportId, int length)
            => new byte[] { 0xA1, 0x01, reportId, 0x03, interfaceNumber, 0x00, (byte)length, (byte)(length >> 8) };

        /// <summary>The length of one interface's report descriptor, from
        /// the HID descriptor that follows that interface's descriptor in
        /// the configuration descriptor, alternate setting 0, or 0 when
        /// there is none or it is longer than
        /// <see cref="MaxReportDescriptor"/>. The walk of
        /// <see cref="TouchSenseProtocol.ReportDescriptorLength"/>, for any
        /// interface.</summary>
        public static int ReportDescriptorLength(ReadOnlySpan<byte> configuration, byte interfaceNumber)
        {
            const byte InterfaceType = 0x04, HidType = 0x21, ReportType = 0x22;
            bool inInterface = false;
            for (int at = 0; at + 2 <= configuration.Length;)
            {
                int length = configuration[at];
                if (length < 2 || at + length > configuration.Length) return 0;
                byte type = configuration[at + 1];
                if (type == InterfaceType && length >= 4)
                {
                    inInterface = configuration[at + 2] == interfaceNumber && configuration[at + 3] == 0;
                }
                else if (type == HidType && inInterface && length >= 6)
                {
                    int count = configuration[at + 5];
                    for (int i = 0; i < count && 6 + 3 * i + 3 <= length; i++)
                    {
                        int entry = at + 6 + 3 * i;
                        if (configuration[entry] != ReportType) continue;
                        int size = configuration[entry + 1] | (configuration[entry + 2] << 8);
                        return size > 0 && size <= MaxReportDescriptor ? size : 0;
                    }
                    return 0;
                }
                at += length;
            }
            return 0;
        }
    }

    /// <summary>
    /// The one WinUSB handle of a keyboard bound to PadForge's driver
    /// (#549). The rules are the TouchSense mouse channel's
    /// (<see cref="TouchSenseChannel"/>, after the DualShock 3 service's
    /// WinUSB calls), less the frames a mouse is sent: a keyboard is only
    /// read.
    ///
    /// <para>Open. The interface must be HID class with an interrupt IN
    /// pipe. The report descriptor is read at the length the interface's
    /// HID descriptor gives and must declare keys
    /// (<see cref="HidKeyboardReports.Parse"/>). SET_IDLE goes out once.
    /// Nothing else is asked of the keyboard, and nothing about it is
    /// changed: it stays in the report protocol Windows left it in, so the
    /// reports read the way its descriptor says. A mouse whose wheel has a
    /// Resolution Multiplier is also asked what that feature holds, and the
    /// row divides by the answer. The feature itself stays as it was.</para>
    ///
    /// <para>Close. Any thread may close it, once, and a close returns
    /// only after the handle is gone, so a keyboard is never opened while
    /// a handle to it is still held. The channel is listed in
    /// <see cref="TouchSenseChannels.Shared"/>, the table of every WinUSB
    /// handle PadForge holds, so a driver switch closes it before Windows
    /// restarts its node.</para>
    /// </summary>
    internal sealed class WinUsbKeyboardChannel : IKeyboardChannel
    {
        internal const byte HidInterfaceClass = TouchSenseChannel.HidInterfaceClass;
        internal const byte ControlPipe = TouchSenseChannel.ControlPipe;
        internal const int ControlTimeoutMs = TouchSenseChannel.ControlTimeoutMs;
        internal const int ReadTimeoutMs = TouchSenseChannel.ReadTimeoutMs;
        internal const int MaxConfigurationLength = TouchSenseChannel.MaxConfigurationLength;
        internal const int MaxReadFaults = TouchSenseChannel.MaxReadFaults;
        internal const int ReadFaultPauseMs = TouchSenseChannel.ReadFaultPauseMs;

        /// <summary>The read size when the pipe names no packet size: a
        /// full-speed interrupt pipe's largest packet.</summary>
        internal const int DefaultReadLength = 64;

        /// <summary>The largest packet a high-speed interrupt pipe carries.</summary>
        internal const int MaxReadLength = 1024;

        /// <summary>The longest report a read is sized for, the longest the
        /// parser takes (<see cref="HidKeyboardReports.MaxReportBits"/>).</summary>
        internal const int MaxReportLength = HidKeyboardReports.MaxReportBits / 8;

        private readonly IKeyboardUsb _usb;
        private readonly byte _inPipe;
        private readonly int _maxReadFaults;
        private readonly int _readFaultPauseMs;
        private readonly byte[] _readBuffer;

        // One reader at a time under its own lock, and a close that waits
        // for it. Order where both are held: the close lock, then the read
        // lock.
        private readonly object _closeLock = new();
        private readonly object _readLock = new();
        private int _closed;
        private bool _freed;
        private int _readFaults;

        private WinUsbKeyboardChannel(string path, IKeyboardUsb usb, byte interfaceNumber, byte inPipe, int readLength,
            HidKeyboardReports reports, HidMouseReports pointer, int wheelMultiplier, int maxReadFaults, int readFaultPauseMs)
        {
            WheelMultiplier = wheelMultiplier;
            Path = path;
            _usb = usb;
            InterfaceNumber = interfaceNumber;
            _inPipe = inPipe;
            _readBuffer = new byte[readLength];
            Reports = reports;
            Pointer = pointer;
            _maxReadFaults = maxReadFaults;
            _readFaultPauseMs = readFaultPauseMs;
        }

        public string Path { get; }
        public bool IsOpen => Volatile.Read(ref _closed) == 0;

        /// <summary>The interface the keyboard is read on.</summary>
        internal byte InterfaceNumber { get; }

        /// <summary>How a keyboard's input reports lay out its keys, null
        /// on a mouse's channel.</summary>
        public HidKeyboardReports Reports { get; }

        /// <summary>How a mouse's input reports lay out its buttons, motion
        /// and wheel, null on a keyboard's channel.</summary>
        public HidMouseReports Pointer { get; }

        /// <summary>The bytes one read asks for: the longest input report
        /// the descriptor declares, or the pipe's packet size when that is
        /// more. usbhid sizes its read from the longest report
        /// (usbhid/hid-core.c:1103,1157), so a report past one packet arrives
        /// whole, and a shorter one ends at its short packet.</summary>
        public int ReadLength => _readBuffer.Length;

        /// <summary>The wheel counts a mouse's notch takes, as its Resolution
        /// Multiplier read at the open.</summary>
        public int WheelMultiplier { get; }

        /// <summary>Opens the bound keyboard at a WinUSB interface path, or
        /// with <paramref name="mouse"/> the bound mouse, or null when it
        /// cannot be opened or its reports carry no keys, for a mouse no
        /// motion. The test host opens no device
        /// (<see cref="RealTouchSenseAccess"/>): a test hands the other
        /// overload an interface of its own.</summary>
        public static WinUsbKeyboardChannel Open(string path, bool mouse = false)
        {
            if (RealTouchSenseAccess.Refused) return null;
            return Open(path, WinUsbTouchSenseUsb.Open(path), mouse: mouse);
        }

        /// <summary>The open sequence over an interface that is already
        /// open. Takes the interface over: it is closed here when the open
        /// fails. The fault parameters are a test's.</summary>
        internal static WinUsbKeyboardChannel Open(string path, IKeyboardUsb usb, int maxReadFaults = MaxReadFaults,
            int readFaultPauseMs = ReadFaultPauseMs, bool mouse = false)
        {
            if (usb == null) return null;
            WinUsbKeyboardChannel opened = null;
            try
            {
                // Before the open's own transfers, so each of them is bounded.
                usb.SetTimeout(ControlPipe, ControlTimeoutMs);
                if (!usb.DescribeInterface(out byte number, out byte interfaceClass, out byte inPipe, out ushort packetSize))
                    return null;
                if (interfaceClass != HidInterfaceClass || inPipe == 0) return null;

                var configuration = new byte[MaxConfigurationLength];
                int read = usb.ReadConfiguration(configuration);
                if (read <= 0 || read > configuration.Length) return null;
                // A configuration that runs past this read names its whole
                // length in wTotalLength (USB 2.0, 9.6.3), and the interface
                // to open can sit past the cut.
                if (read >= 4 && (configuration[2] | (configuration[3] << 8)) is int total && total > read)
                {
                    var whole = new byte[total];
                    int again = usb.ReadConfiguration(whole);
                    if (again > read && again <= whole.Length)
                    {
                        configuration = whole;
                        read = again;
                    }
                }
                int length = WinUsbKeyboardProtocol.ReportDescriptorLength(configuration.AsSpan(0, read), number);
                if (length == 0) return null;
                var descriptor = new byte[length];
                // A control IN can complete short, and a short descriptor
                // is no descriptor to read a keyboard by.
                if (!usb.ControlIn(WinUsbKeyboardProtocol.ReportDescriptorSetup(number, length), descriptor, out int got)
                    || got != length)
                    return null;
                // A mouse is read by its buttons, motion and wheel, a
                // keyboard by its keys, and a descriptor with nothing of
                // the kind asked for opens nothing.
                HidKeyboardReports reports = mouse ? null : HidKeyboardReports.Parse(descriptor);
                HidMouseReports pointer = mouse ? HidMouseReports.Parse(descriptor) : null;
                if (reports == null && pointer == null) return null;

                // Its answer is not waited on for anything, as the TouchSense
                // open treats it (TouchSenseChannel.Open).
                usb.ControlOut(WinUsbKeyboardProtocol.SetIdleSetup(number), Array.Empty<byte>());
                usb.SetTimeout(inPipe, ReadTimeoutMs);
                int packet = packetSize == 0 ? DefaultReadLength : Math.Min((int)packetSize, MaxReadLength);
                int longest = reports?.LongestInputReport ?? pointer.LongestInputReport;
                int readLength = Math.Max(packet, Math.Min(longest, MaxReportLength));
                opened = new WinUsbKeyboardChannel(path, usb, number, inPipe, readLength, reports, pointer,
                    ReadWheelMultiplier(usb, number, pointer), maxReadFaults, readFaultPauseMs);
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

        /// <summary>What the wheel's Resolution Multiplier holds now, 1 for a
        /// wheel with none and for a mouse that does not answer. Windows'
        /// mouse driver can leave the feature set when the mouse moves to
        /// WinUSB, and nothing here writes it.</summary>
        private static int ReadWheelMultiplier(IKeyboardUsb usb, byte number, HidMouseReports pointer)
        {
            var feature = pointer?.WheelMultiplierFeature;
            if (feature == null) return 1;
            var report = new byte[feature.ReportLength];
            return usb.ControlIn(WinUsbKeyboardProtocol.GetFeatureSetup(number, feature.ReportId, report.Length), report, out int got)
                ? pointer.EffectiveMultiplier(report.AsSpan(0, got))
                : 1;
        }

        /// <summary>One read, as <see cref="IKeyboardChannel.TryRead"/> says.
        /// A run of faults that do not name the keyboard as gone ends the
        /// channel after about a second, the TouchSense read's rule
        /// (<see cref="TouchSenseChannel.TryRead"/>).</summary>
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
                if (TouchSenseChannel.IsGone(error) || ++_readFaults >= _maxReadFaults) return false;
            }
            // Outside the lock, so a close never waits out the pause.
            if (_readFaultPauseMs > 0) Thread.Sleep(_readFaultPauseMs);
            return IsOpen;
        }

        /// <summary>Closes the channel: no new read starts, the one a
        /// thread waits in is called off, and once it has returned the
        /// interface and its handle are freed, in that order. Every caller
        /// returns only after the handle is gone. Never called from inside
        /// <see cref="TryRead"/>, whose lock it takes.</summary>
        public void Dispose()
        {
            lock (_closeLock)
            {
                if (_freed) return;
                Volatile.Write(ref _closed, 1);
                try { _usb.CancelIo(); } catch { }
                lock (_readLock)
                {
                    try { _usb.Dispose(); } catch { }
                }
                _freed = true;
            }
        }
    }
}
