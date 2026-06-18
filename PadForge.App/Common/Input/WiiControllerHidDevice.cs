using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    /// <summary>
    /// A Bluetooth Wii Remote read directly over raw HID and exposed to the
    /// input pipeline as a standard <see cref="ISdlInputDevice"/> (issue #116).
    ///
    /// PadForge does NOT route the Wii Remote through SDL. SDL's hidapi sends
    /// output reports with WriteFile whenever the output-report length is &lt;= 512
    /// (windows/hid.c), and the Wii Remote's length is 22, so on Windows 8+ SDL
    /// always uses WriteFile. The Microsoft Bluetooth stack rejects WriteFile for
    /// the Wii Remote with ERROR_INVALID_PARAMETER; only HidD_SetOutputReport
    /// (SET_REPORT over the control channel) works. Every write SDL's Wii driver
    /// makes during init (status request, set-reporting-mode, player LED, extension
    /// identify) therefore fails, so SDL can neither make the remote stream nor
    /// drive it. A Wii Remote that nothing makes stream also drops off Bluetooth
    /// within a few seconds. That is why a freshly-paired remote never appears in
    /// SDL and disconnects on its own.
    ///
    /// This device fixes both: it opens the HID handle and holds it (which keeps
    /// the Bluetooth link up), sends the player-LED and data-reporting-mode reports
    /// via HidD_SetOutputReport (which stops the flashing and starts the stream),
    /// and reads the core-buttons report on a dedicated thread, mirroring Dolphin's
    /// WiimoteReal read loop. State lands in the standard gamepad button/POV arrays
    /// of <see cref="CustomInputState"/>, so the normal mapping picker resolves it.
    ///
    /// Scope: the Wii Remote's own buttons and D-pad, plus rumble. Nunchuk /
    /// Classic / Wii U Pro extension axes are not parsed yet (the report-mode and
    /// read loop are structured so they can be added without reshaping this class).
    /// </summary>
    internal sealed class WiiRemoteHidDevice : ISdlInputDevice
    {
        private const ushort NintendoVendorId = 0x057E;

        // Wii output report IDs.
        private const byte ReportPlayerLeds = 0x11; // data byte: LED mask in high nibble | rumble bit
        private const byte ReportDataMode   = 0x12; // [flags][mode]
        // Reporting modes.
        private const byte ModeCoreButtons  = 0x30; // buttons only (2 data bytes)
        // Flag byte bits: 0x04 = continuous reporting, 0x01 = rumble.
        private const byte FlagContinuous   = 0x04;
        private const byte FlagRumble       = 0x01;
        // Player LED 1 lit (high nibble).
        private const byte Led1             = 0x10;

        // Mappable button layout. Indices are arbitrary and remappable; names
        // come from GetDeviceObjects below.
        private const int BtnA = 0, BtnB = 1, Btn1 = 2, Btn2 = 3, BtnMinus = 4, BtnPlus = 5, BtnHome = 6;
        private const int ButtonCount = 7;

        private static readonly string[] ButtonNames =
            { "A", "B", "1", "2", "Minus", "Plus", "Home" };

        private readonly string _devicePath;
        private readonly object _stateLock = new();
        private readonly object _writeLock = new();

        private CustomInputState _state = new();
        private volatile bool _attached;
        private volatile bool _running;
        private IntPtr _handle = InvalidHandle;
        private int _outputReportLength = 22;
        private Thread _readThread;
        private volatile bool _rumbleActive;

        public WiiRemoteHidDevice(string devicePath, ushort productId, string name, string serial)
        {
            _devicePath = devicePath;
            ProductId = productId;
            Name = string.IsNullOrWhiteSpace(name) ? "Wii Remote" : name;

            // Prefer the HID serial (the remote's Bluetooth MAC on most stacks)
            // for a stable identity that survives a re-pair; fall back to the
            // interface path, which is stable for a given paired remote.
            string idBase = string.IsNullOrWhiteSpace(serial) ? devicePath : serial;
            InstanceGuid = Md5Guid("pfwii-hid:" + idBase);
            ProductGuid = Md5Guid("pfwii-product:" + productId.ToString("X4"));
            SdlInstanceId = unchecked((uint)idBase.GetHashCode());

            // POV 0 centidegrees is North (D-pad up); a fresh state must read as
            // centered (-1) until the first report, not as up-held.
            _state.Povs[0] = -1;
        }

        // ─────────────────────────────────────────────
        //  ISdlInputDevice identity / capabilities
        // ─────────────────────────────────────────────

        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => 0;
        public int NumButtons => ButtonCount;
        public int RawButtonCount => ButtonCount;
        public int NumHats => 1;
        public int[] SupportedButtonIndices { get; } = BuildDense(ButtonCount);
        public IntPtr GamepadHandle => IntPtr.Zero;
        public bool HasRumble => true;
        public bool HasRumbleTriggers => false;
        public bool HasHaptic => false;
        public bool HasGyro => false;
        public bool HasAccel => false;
        public bool HasTouchpad => false;
        public HapticEffectStrategy HapticStrategy => HapticEffectStrategy.None;
        public IntPtr HapticHandle => IntPtr.Zero;
        public uint HapticFeatures => 0;
        public int NumHapticAxes => 0;
        public bool IsAttached => _attached;
        public ushort VendorId => NintendoVendorId;
        public ushort ProductId { get; }
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath => _devicePath;
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.Gamepad;

        public DeviceObjectItem[] GetDeviceObjects()
        {
            var items = new DeviceObjectItem[ButtonCount + 1];
            int idx = 0;
            for (int i = 0; i < ButtonCount; i++)
            {
                items[idx++] = new DeviceObjectItem
                {
                    InputIndex = i,
                    ObjectTypeGuid = ObjectGuid.Button,
                    Name = ButtonNames[i],
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    Offset = i * 4
                };
            }
            items[idx] = new DeviceObjectItem
            {
                InputIndex = 0,
                ObjectTypeGuid = ObjectGuid.PovController,
                Name = "D-Pad",
                ObjectType = DeviceObjectTypeFlags.PointOfViewController,
                Offset = ButtonCount * 4
            };
            return items;
        }

        // ─────────────────────────────────────────────
        //  Connection lifecycle
        // ─────────────────────────────────────────────

        /// <summary>Opens the HID handle, kickstarts streaming, and starts the
        /// read thread. Returns false when the device cannot be opened (another
        /// owner, vanished, etc).</summary>
        public bool Open()
        {
            IntPtr h = CreateFileW(_devicePath, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h == IntPtr.Zero || h == InvalidHandle)
                return false;

            _handle = h;
            _outputReportLength = QueryOutputReportLength(h);
            if (_outputReportLength <= 0) _outputReportLength = 22;

            // Kickstart: light player LED 1 (stops the idle flashing) and put the
            // remote into continuous core-button reporting (starts the stream).
            if (!SendPlayerLed() || !SendReportMode())
            {
                Close();
                return false;
            }

            _attached = true;
            _running = true;
            _readThread = new Thread(ReadLoop)
            {
                Name = "PadForge.WiiRemote",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _readThread.Start();
            return true;
        }

        public void Dispose()
        {
            _attached = false;
            _running = false;
            var t = _readThread;
            _readThread = null;
            // Closing the handle aborts the pending overlapped ReadFile, letting
            // the read thread fall out of its loop.
            Close();
            if (t != null && t.IsAlive && t.ManagedThreadId != Environment.CurrentManagedThreadId)
                t.Join(TimeSpan.FromSeconds(1));
        }

        private void Close()
        {
            var h = Interlocked.Exchange(ref _handle, InvalidHandle);
            if (h != IntPtr.Zero && h != InvalidHandle)
                CloseHandle(h);
        }

        // ─────────────────────────────────────────────
        //  Read loop + parsing
        // ─────────────────────────────────────────────

        private void ReadLoop()
        {
            IntPtr ev = CreateEventW(IntPtr.Zero, true, false, null);
            // One overlapped structure for the whole loop. It is a stack local,
            // so its address is stable for the method's lifetime (the GC never
            // relocates the stack), which is what overlapped I/O needs.
            var ol = new WiiOverlapped { EventHandle = ev };
            var buf = new byte[32];
            try
            {
                while (_running)
                {
                    ResetEvent(ev);
                    bool ok = ReadFile(_handle, buf, (uint)buf.Length, out uint read, ref ol);
                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != ERROR_IO_PENDING)
                            break; // device gone or handle closed
                        // Continuous mode streams ~constantly; a 2s gap means the
                        // link dropped. Re-issue once on a benign timeout, bail on error.
                        uint w = WaitForSingleObject(ev, 2000);
                        if (w != WAIT_OBJECT_0)
                        {
                            // Cancel and drain so the OVERLAPPED is free before reuse.
                            CancelIo(_handle);
                            GetOverlappedResult(_handle, ref ol, out _, true);
                            if (w == WAIT_TIMEOUT) continue;
                            break;
                        }
                        if (!GetOverlappedResult(_handle, ref ol, out read, false))
                            break;
                    }
                    if (read >= 3 && buf[0] == ModeCoreButtons)
                        ParseCoreButtons(buf);
                }
            }
            catch { /* teardown races are expected; treat as disconnect */ }
            finally
            {
                CloseHandle(ev);
                _attached = false;
            }
        }

        private void ParseCoreButtons(byte[] buf)
        {
            byte b1 = buf[1], b2 = buf[2];
            bool left  = (b1 & 0x01) != 0;
            bool right = (b1 & 0x02) != 0;
            bool down  = (b1 & 0x04) != 0;
            bool up    = (b1 & 0x08) != 0;
            bool plus  = (b1 & 0x10) != 0;
            bool two   = (b2 & 0x01) != 0;
            bool one   = (b2 & 0x02) != 0;
            bool b     = (b2 & 0x04) != 0;
            bool a     = (b2 & 0x08) != 0;
            bool minus = (b2 & 0x10) != 0;
            bool home  = (b2 & 0x80) != 0;

            lock (_stateLock)
            {
                var s = _state.Clone();
                s.Buttons[BtnA] = a;
                s.Buttons[BtnB] = b;
                s.Buttons[Btn1] = one;
                s.Buttons[Btn2] = two;
                s.Buttons[BtnMinus] = minus;
                s.Buttons[BtnPlus] = plus;
                s.Buttons[BtnHome] = home;
                s.Povs[0] = DpadToPov(up, right, down, left);
                _state = s;
            }
        }

        private static int DpadToPov(bool up, bool right, bool down, bool left)
        {
            if (up && right) return 4500;
            if (right && down) return 13500;
            if (down && left) return 22500;
            if (left && up) return 31500;
            if (up) return 0;
            if (right) return 9000;
            if (down) return 18000;
            if (left) return 27000;
            return -1;
        }

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            lock (_stateLock)
                return _state.Clone();
        }

        // ─────────────────────────────────────────────
        //  Output (rumble + kickstart) via HidD_SetOutputReport
        // ─────────────────────────────────────────────

        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue)
        {
            bool active = (low | high) != 0;
            if (active == _rumbleActive) return true;
            _rumbleActive = active;
            return SendPlayerLed();
        }

        public bool StopRumble()
        {
            if (!_rumbleActive) return true;
            _rumbleActive = false;
            return SendPlayerLed();
        }

        private bool SendPlayerLed()
        {
            var buf = new byte[_outputReportLength];
            buf[0] = ReportPlayerLeds;
            buf[1] = (byte)(Led1 | (_rumbleActive ? FlagRumble : 0));
            return SetOutputReport(buf);
        }

        private bool SendReportMode()
        {
            var buf = new byte[_outputReportLength];
            buf[0] = ReportDataMode;
            buf[1] = (byte)(FlagContinuous | (_rumbleActive ? FlagRumble : 0));
            buf[2] = ModeCoreButtons;
            return SetOutputReport(buf);
        }

        private bool SetOutputReport(byte[] buf)
        {
            var h = _handle;
            if (h == IntPtr.Zero || h == InvalidHandle) return false;
            lock (_writeLock)
                return HidD_SetOutputReport(h, buf, (uint)buf.Length);
        }

        private static int QueryOutputReportLength(IntPtr handle)
        {
            if (!HidD_GetPreparsedData(handle, out IntPtr pp) || pp == IntPtr.Zero) return 0;
            try
            {
                if (HidP_GetCaps(pp, out HIDP_CAPS caps) < 0) return 0;
                return caps.OutputReportByteLength;
            }
            finally { HidD_FreePreparsedData(pp); }
        }

        private static int[] BuildDense(int n)
        {
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = i;
            return a;
        }

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }

        // ─────────────────────────────────────────────
        //  Win32 interop
        // ─────────────────────────────────────────────

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const int ERROR_IO_PENDING = 997;
        private const uint WAIT_OBJECT_0 = 0;
        private const uint WAIT_TIMEOUT = 258;
        private static readonly IntPtr InvalidHandle = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct WiiOverlapped
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public uint OffsetLow;
            public uint OffsetHigh;
            public IntPtr EventHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, ref WiiOverlapped lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(IntPtr hFile, ref WiiOverlapped lpOverlapped,
            out uint lpNumberOfBytesTransferred, bool bWait);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIo(IntPtr hFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset,
            bool bInitialState, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ResetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetOutputReport(IntPtr hidDeviceObject, byte[] reportBuffer, uint reportBufferLength);

        [DllImport("hid.dll")]
        private static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll")]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);
    }
}
