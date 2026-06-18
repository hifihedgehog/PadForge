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
    /// A Bluetooth Wii controller read directly over raw HID and exposed to the
    /// input pipeline as a standard <see cref="ISdlInputDevice"/> (issue #116).
    ///
    /// PadForge does NOT route Wii controllers through SDL. SDL's hidapi sends
    /// output reports with WriteFile whenever the output-report length is &lt;= 512
    /// (windows/hid.c), and a Wii Remote's length is 22, so on Windows 8+ SDL
    /// always uses WriteFile. The Microsoft Bluetooth stack rejects WriteFile for
    /// the remote with ERROR_INVALID_PARAMETER; only HidD_SetOutputReport
    /// (SET_REPORT over the control channel) works. Every write SDL's Wii driver
    /// makes during init therefore fails, so SDL can neither make the remote
    /// stream nor drive it, and an unstreaming remote drops off Bluetooth within
    /// a few seconds.
    ///
    /// This device opens the HID handle and holds it (which keeps the Bluetooth
    /// link up), sends the player-LED and data-reporting-mode reports via
    /// HidD_SetOutputReport (which stops the flashing and starts the stream), and
    /// reads input on a dedicated thread, mirroring Dolphin's WiimoteReal read
    /// loop. State lands in the standard gamepad button/axis/POV arrays of
    /// <see cref="CustomInputState"/>, so the normal mapping picker resolves it.
    ///
    /// All Wii controller forms are supported, detected at open time from the
    /// extension ID at 0xA400FE, with the protocol taken from SDL's
    /// SDL_hidapi_wii.c:
    ///   - Wii Remote / Wii Remote Plus (no extension): its 7 buttons + D-pad.
    ///   - Wii Remote + Nunchuk: adds the Nunchuk analog stick and C / Z.
    ///   - Classic Controller / Classic Controller Pro: full gamepad
    ///     (two sticks, L/R + ZL/ZR, face buttons, D-pad).
    ///   - Wii U Pro Controller: full gamepad (report 0x3D).
    /// </summary>
    internal sealed class WiiControllerHidDevice : ISdlInputDevice
    {
        private enum WiiExt { None, Nunchuk, Classic, WiiUPro }

        private const ushort NintendoVendorId = 0x057E;

        // Output report IDs.
        private const byte ReportPlayerLeds  = 0x11;
        private const byte ReportDataMode    = 0x12;
        private const byte ReportWriteMemory = 0x16;
        private const byte ReportReadMemory  = 0x17;
        // Input report IDs.
        private const byte InReadMemory    = 0x21;
        private const byte ModeCoreButtons = 0x30; // 30 BB BB
        private const byte ModeCorePlusExt = 0x32; // 32 BB BB EE*8  (Nunchuk / Classic)
        private const byte ModeWiiUPro     = 0x3D; // 3d EE*21       (Wii U Pro, no core buttons)
        // Flag byte bits and LED.
        private const byte FlagContinuous = 0x04;
        private const byte FlagRumble     = 0x01;
        private const byte RegisterSpace  = 0x04;
        private const byte Led1           = 0x10;
        // Extension identities (SDL_hidapi_wii.c).
        private const int ExtIdNunchuk = 0x0000;
        private const int ExtIdClassic = 0x0101;
        private const int ExtIdWiiUPro = 0x0120;

        // Analog-stick raw ranges (SDL InitStickCalibrationData).
        private const int NunchukMin = 40, NunchukMax = 215;
        private const int ClassicLMin = 9, ClassicLMax = 54;   // 6-bit L stick
        private const int ClassicRMin = 5, ClassicRMax = 26;   // 5-bit R stick
        private const int WiiUProMin = 1000, WiiUProMax = 3000; // 16-bit sticks

        // Button indices for the Wii Remote / Nunchuk layout.
        private const int BtnA = 0, BtnB = 1, Btn1 = 2, Btn2 = 3, BtnMinus = 4, BtnPlus = 5, BtnHome = 6, BtnC = 7, BtnZ = 8;

        private static readonly string[] WiiButtonNames =
            { "A", "B", "1", "2", "Minus", "Plus", "Home", "C", "Z" };
        // Canonical gamepad order (matches the SDL3 auto-map: A,B,X,Y,LB,RB,Back,Start,LS,RS,Guide).
        private static readonly string[] PadButtonNames =
            { "A", "B", "X", "Y", "L", "R", "Minus", "Plus", "Left Stick", "Right Stick", "Home" };
        private static readonly string[] PadAxisNames =
            { "Left X", "Left Y", "Left Trigger", "Right X", "Right Y", "Right Trigger" };
        private static readonly Guid[] AxisGuids =
            { ObjectGuid.XAxis, ObjectGuid.YAxis, ObjectGuid.ZAxis, ObjectGuid.RxAxis, ObjectGuid.RyAxis, ObjectGuid.RzAxis };

        private readonly string _devicePath;
        private readonly object _stateLock = new();
        private readonly object _writeLock = new();

        private CustomInputState _state = new();
        private volatile bool _attached;
        private volatile bool _running;
        private volatile WiiExt _ext = WiiExt.None;
        private IntPtr _handle = InvalidHandle;
        private int _outputReportLength = 22;
        private Thread _readThread;
        private volatile bool _rumbleActive;

        public WiiControllerHidDevice(string devicePath, ushort productId, string name, string serial)
        {
            _devicePath = devicePath;
            ProductId = productId;
            Name = string.IsNullOrWhiteSpace(name) ? "Wii Remote" : name;

            string idBase = string.IsNullOrWhiteSpace(serial) ? devicePath : serial;
            InstanceGuid = Md5Guid("pfwii-hid:" + idBase);
            ProductGuid = Md5Guid("pfwii-product:" + productId.ToString("X4"));
            SdlInstanceId = unchecked((uint)idBase.GetHashCode());

            // POV 0 centidegrees is North (D-pad up); a fresh state must read as
            // centered (-1) until the first report, not as up-held.
            _state.Povs[0] = -1;
        }

        private bool IsPadExtension => _ext == WiiExt.Classic || _ext == WiiExt.WiiUPro;

        // ─────────────────────────────────────────────
        //  ISdlInputDevice identity / capabilities
        // ─────────────────────────────────────────────

        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => _ext switch
        {
            WiiExt.Nunchuk => 2,
            WiiExt.Classic => 6,
            WiiExt.WiiUPro => 6,
            _ => 0
        };
        public int NumButtons => _ext switch
        {
            WiiExt.Nunchuk => 9,
            WiiExt.Classic => 11,
            WiiExt.WiiUPro => 11,
            _ => 7
        };
        public int RawButtonCount => NumButtons;
        public int NumHats => 1;
        public int[] SupportedButtonIndices => BuildDense(NumButtons);
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
            int numAxes = NumAxes;
            int numButtons = NumButtons;
            bool pad = IsPadExtension;
            var items = new List<DeviceObjectItem>(numAxes + numButtons + 1);

            for (int i = 0; i < numAxes; i++)
            {
                string name = pad ? PadAxisNames[i]
                    : i == 0 ? "Nunchuk X" : "Nunchuk Y";
                items.Add(new DeviceObjectItem
                {
                    InputIndex = i,
                    ObjectTypeGuid = AxisGuids[i],
                    Name = name,
                    ObjectType = DeviceObjectTypeFlags.AbsoluteAxis,
                    Offset = i * 4
                });
            }

            string[] btnNames = pad ? PadButtonNames : WiiButtonNames;
            for (int i = 0; i < numButtons; i++)
            {
                items.Add(new DeviceObjectItem
                {
                    InputIndex = i,
                    ObjectTypeGuid = ObjectGuid.Button,
                    Name = btnNames[i],
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    Offset = (numAxes + i) * 4
                });
            }

            items.Add(new DeviceObjectItem
            {
                InputIndex = 0,
                ObjectTypeGuid = ObjectGuid.PovController,
                Name = "D-Pad",
                ObjectType = DeviceObjectTypeFlags.PointOfViewController,
                Offset = (numAxes + numButtons) * 4
            });

            return items.ToArray();
        }

        // ─────────────────────────────────────────────
        //  Connection lifecycle
        // ─────────────────────────────────────────────

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

            // Light player LED 1 (stops the idle flashing).
            if (!SendPlayerLed())
            {
                Close();
                return false;
            }

            // Detect the extension before any continuous reporting starts, so the
            // only input reports in flight are responses to the register probes.
            _ext = DetectExtension();

            // Center any analog sticks the controller has until the first report.
            if (NumAxes > 0)
                lock (_stateLock)
                {
                    _state.Axis[0] = 32768; _state.Axis[1] = 32768;
                    if (IsPadExtension) { _state.Axis[3] = 32768; _state.Axis[4] = 32768; }
                }

            if (!SendReportMode())
            {
                Close();
                return false;
            }

            _attached = true;
            _running = true;
            _readThread = new Thread(ReadLoop)
            {
                Name = "PadForge.WiiController",
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
        //  Extension detection
        // ─────────────────────────────────────────────

        /// <summary>Initializes the extension port and reads its identity. Mirrors
        /// SDL's SendExtensionReset + ReadExtensionControllerType: write 0x55 to
        /// 0xA400F0 and 0x00 to 0xA400FB (which makes the data raw/unencrypted),
        /// then read two bytes at 0xA400FE. 0x0000 = Nunchuk, 0x0101 = Classic,
        /// 0x0120 = Wii U Pro. Anything else (including no extension) is treated
        /// as a bare Wii Remote.</summary>
        private WiiExt DetectExtension()
        {
            try
            {
                WriteMemory(0xA400F0, 0x55);
                WriteMemory(0xA400FB, 0x00);
                ReadMemory(0xA400FE, 2);

                // 0x21 response: [0]=0x21, [1..2]=buttons, [3]=size/error nibble
                // (0x10 = 2 bytes, no error), [4..5]=address low word (0x00FE),
                // [6..7]=the two ID bytes.
                var buf = new byte[32];
                if (AwaitReport(InReadMemory, 400, buf, out int len)
                    && len >= 8 && buf[3] == 0x10 && buf[4] == 0x00 && buf[5] == 0xFE)
                {
                    int ext = (buf[6] << 8) | buf[7];
                    return ext switch
                    {
                        ExtIdNunchuk => WiiExt.Nunchuk,
                        ExtIdClassic => WiiExt.Classic,
                        ExtIdWiiUPro => WiiExt.WiiUPro,
                        _ => WiiExt.None
                    };
                }
            }
            catch { /* probe failed -> bare remote */ }
            return WiiExt.None;
        }

        private bool AwaitReport(byte wantId, int timeoutMs, byte[] buf, out int len)
        {
            len = 0;
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                int remaining = (int)(deadline - Environment.TickCount64);
                if (ReadReportSync(buf, Math.Min(remaining, 120), out len) && len >= 1 && buf[0] == wantId)
                    return true;
            }
            return false;
        }

        private bool ReadReportSync(byte[] buf, int timeoutMs, out int len)
        {
            len = 0;
            var h = _handle;
            if (h == IntPtr.Zero || h == InvalidHandle) return false;
            IntPtr ev = CreateEventW(IntPtr.Zero, true, false, null);
            try
            {
                var ol = new WiiOverlapped { EventHandle = ev };
                if (ReadFile(h, buf, (uint)buf.Length, out uint read, ref ol))
                {
                    len = (int)read;
                    return true;
                }
                if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return false;
                if (WaitForSingleObject(ev, (uint)Math.Max(0, timeoutMs)) != WAIT_OBJECT_0)
                {
                    CancelIo(h);
                    GetOverlappedResult(h, ref ol, out _, true);
                    return false;
                }
                if (!GetOverlappedResult(h, ref ol, out read, false)) return false;
                len = (int)read;
                return true;
            }
            finally { CloseHandle(ev); }
        }

        // ─────────────────────────────────────────────
        //  Read loop + parsing
        // ─────────────────────────────────────────────

        private void ReadLoop()
        {
            IntPtr ev = CreateEventW(IntPtr.Zero, true, false, null);
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
                            break;
                        uint w = WaitForSingleObject(ev, 2000);
                        if (w != WAIT_OBJECT_0)
                        {
                            CancelIo(_handle);
                            GetOverlappedResult(_handle, ref ol, out _, true);
                            if (w == WAIT_TIMEOUT) continue;
                            break;
                        }
                        if (!GetOverlappedResult(_handle, ref ol, out read, false))
                            break;
                    }
                    if (buf[0] == ModeCoreButtons && read >= 3)
                        ParseCore(buf);
                    else if (buf[0] == ModeCorePlusExt && read >= 11)
                        ParseCorePlusExt(buf);
                    else if (buf[0] == ModeWiiUPro && read >= 12)
                        ParseWiiUPro(buf);
                }
            }
            catch { /* teardown races are expected; treat as disconnect */ }
            finally
            {
                CloseHandle(ev);
                _attached = false;
            }
        }

        // Wii Remote core buttons (report 0x30 / the core bytes of 0x32).
        private static void ApplyWiiRemoteButtons(CustomInputState s, byte b1, byte b2)
        {
            s.Buttons[BtnA] = (b2 & 0x08) != 0;
            s.Buttons[BtnB] = (b2 & 0x04) != 0;
            s.Buttons[Btn1] = (b2 & 0x02) != 0;
            s.Buttons[Btn2] = (b2 & 0x01) != 0;
            s.Buttons[BtnMinus] = (b2 & 0x10) != 0;
            s.Buttons[BtnPlus] = (b1 & 0x10) != 0;
            s.Buttons[BtnHome] = (b2 & 0x80) != 0;
            bool left = (b1 & 0x01) != 0, right = (b1 & 0x02) != 0;
            bool down = (b1 & 0x04) != 0, up = (b1 & 0x08) != 0;
            s.Povs[0] = DpadToPov(up, right, down, left);
        }

        private void ParseCore(byte[] buf)
        {
            lock (_stateLock)
            {
                var s = _state.Clone();
                ApplyWiiRemoteButtons(s, buf[1], buf[2]);
                _state = s;
            }
        }

        private void ParseCorePlusExt(byte[] buf)
        {
            lock (_stateLock)
            {
                var s = _state.Clone();
                if (_ext == WiiExt.Classic)
                {
                    ApplyClassic(s, buf, 3); // extension bytes begin at buf[3]
                }
                else // Nunchuk
                {
                    ApplyWiiRemoteButtons(s, buf[1], buf[2]);
                    int rawX = buf[3], rawY = buf[4];
                    byte ext5 = buf[8];
                    s.Buttons[BtnC] = (ext5 & 0x02) == 0;
                    s.Buttons[BtnZ] = (ext5 & 0x01) == 0;
                    s.Axis[0] = Calibrate(rawX, NunchukMin, NunchukMax, false);
                    s.Axis[1] = Calibrate(rawY, NunchukMin, NunchukMax, true);
                }
                _state = s;
            }
        }

        private void ParseWiiUPro(byte[] buf)
        {
            // Report 0x3D: extension bytes begin at buf[1], no core buttons.
            lock (_stateLock)
            {
                var s = _state.Clone();
                int o = 1;
                int lx = buf[o + 0] | (buf[o + 1] << 8);
                int rx = buf[o + 2] | (buf[o + 3] << 8);
                int ly = buf[o + 4] | (buf[o + 5] << 8);
                int ry = buf[o + 6] | (buf[o + 7] << 8);
                s.Axis[0] = Calibrate(lx, WiiUProMin, WiiUProMax, false);
                s.Axis[1] = Calibrate(ly, WiiUProMin, WiiUProMax, true);
                s.Axis[3] = Calibrate(rx, WiiUProMin, WiiUProMax, false);
                s.Axis[4] = Calibrate(ry, WiiUProMin, WiiUProMax, true);
                ApplyPadButtons(s, buf[o + 8], buf[o + 9]);
                s.Buttons[9] = (buf[o + 10] & 0x01) == 0; // RS
                s.Buttons[8] = (buf[o + 10] & 0x02) == 0; // LS
                _state = s;
            }
        }

        // Classic Controller (extension bytes from `off`): two sticks, ZL/ZR
        // triggers, face buttons, D-pad. Layout per SDL HandleGamepadControllerButtonData.
        private static void ApplyClassic(CustomInputState s, byte[] buf, int off)
        {
            byte e0 = buf[off + 0], e1 = buf[off + 1], e2 = buf[off + 2];
            int lx = e0 & 0x3F;
            int ly = e1 & 0x3F;
            int rx = (e2 >> 7) | ((e1 >> 5) & 0x06) | ((e0 >> 3) & 0x18);
            int ry = e2 & 0x1F;
            s.Axis[0] = Calibrate(lx, ClassicLMin, ClassicLMax, false);
            s.Axis[1] = Calibrate(ly, ClassicLMin, ClassicLMax, true);
            s.Axis[3] = Calibrate(rx, ClassicRMin, ClassicRMax, false);
            s.Axis[4] = Calibrate(ry, ClassicRMin, ClassicRMax, true);
            ApplyPadButtons(s, buf[off + 4], buf[off + 5]);
        }

        // Shared face-button / D-pad / ZL-ZR unpack for Classic and Wii U Pro.
        // byteA / byteB carry identical bit layouts on both; buttons are active-low.
        private static void ApplyPadButtons(CustomInputState s, byte byteA, byte byteB)
        {
            s.Buttons[0] = (byteB & 0x40) == 0; // A (SOUTH)
            s.Buttons[1] = (byteB & 0x10) == 0; // B (EAST)
            s.Buttons[2] = (byteB & 0x20) == 0; // X (WEST)
            s.Buttons[3] = (byteB & 0x08) == 0; // Y (NORTH)
            s.Buttons[4] = (byteA & 0x20) == 0; // L
            s.Buttons[5] = (byteA & 0x02) == 0; // R
            s.Buttons[6] = (byteA & 0x10) == 0; // Minus
            s.Buttons[7] = (byteA & 0x04) == 0; // Plus
            s.Buttons[10] = (byteA & 0x08) == 0; // Home

            bool up = (byteB & 0x01) == 0, left = (byteB & 0x02) == 0;
            bool down = (byteA & 0x40) == 0, right = (byteA & 0x80) == 0;
            s.Povs[0] = DpadToPov(up, right, down, left);

            bool zl = (byteB & 0x80) == 0, zr = (byteB & 0x04) == 0;
            s.Axis[2] = zl ? 65535 : 0; // Left Trigger
            s.Axis[5] = zr ? 65535 : 0; // Right Trigger
        }

        // Maps a raw analog value in [min..max] to the 0..65535 axis space,
        // inverting Y so up reads low (the SDL gamepad axis convention the
        // auto-map and engine expect).
        private static int Calibrate(int raw, int min, int max, bool invert)
        {
            int span = max - min;
            if (span <= 0) return 32768;
            int v = Math.Clamp((raw - min) * 65535 / span, 0, 65535);
            return invert ? 65535 - v : v;
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
        //  Output via HidD_SetOutputReport
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
            buf[2] = _ext switch
            {
                WiiExt.WiiUPro => ModeWiiUPro,
                WiiExt.Nunchuk => ModeCorePlusExt,
                WiiExt.Classic => ModeCorePlusExt,
                _ => ModeCoreButtons
            };
            return SetOutputReport(buf);
        }

        private bool WriteMemory(uint address, byte value)
        {
            var buf = new byte[_outputReportLength];
            buf[0] = ReportWriteMemory;
            buf[1] = (byte)(RegisterSpace | (_rumbleActive ? FlagRumble : 0));
            buf[2] = (byte)((address >> 16) & 0xFF);
            buf[3] = (byte)((address >> 8) & 0xFF);
            buf[4] = (byte)(address & 0xFF);
            buf[5] = 1;
            buf[6] = value;
            return SetOutputReport(buf);
        }

        private bool ReadMemory(uint address, int size)
        {
            var buf = new byte[_outputReportLength];
            buf[0] = ReportReadMemory;
            buf[1] = (byte)(RegisterSpace | (_rumbleActive ? FlagRumble : 0));
            buf[2] = (byte)((address >> 16) & 0xFF);
            buf[3] = (byte)((address >> 8) & 0xFF);
            buf[4] = (byte)(address & 0xFF);
            buf[5] = (byte)((size >> 8) & 0xFF);
            buf[6] = (byte)(size & 0xFF);
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
