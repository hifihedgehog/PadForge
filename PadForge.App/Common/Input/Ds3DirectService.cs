using System;
using System.Runtime.InteropServices;
using System.Threading;
using SDL3;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Surfaces a DualShock 3 that is connected over Bluetooth through the BthPS3
    /// profile driver (RAW-PDO mode, no DsHidMini) as an SDL virtual joystick, so the
    /// normal PadForge/SDL pipeline consumes it exactly like any other gamepad.
    ///
    /// BthPS3's raw PDO is NOT a HID device (it exposes a custom IOCTL interface,
    /// GUID_DEVINTERFACE_BTHPS3 {968E1849}), so SDL's hidapi cannot open it directly.
    /// This service owns the native transport (open + kick + read/parse) and pushes
    /// the parsed state into an SDL virtual joystick, keeping SDL as the intermediary.
    ///
    /// Transport + kick + report layout are the exact sequence proven on hardware
    /// (see memory: ds3-winusb-userland-usb-proven, "FULLY WORKING" recipe).
    /// </summary>
    public sealed class Ds3DirectService
    {
        // GUID_DEVINTERFACE_BTHPS3 {968E1849-73B1-4876-B80A-ED6DD171489B} - the RAW PDO's IOCTL interface.
        private static readonly Guid BthPs3Interface =
            new Guid(0x968e1849, 0x73b1, 0x4876, 0xb8, 0x0a, 0xed, 0x6d, 0xd1, 0x71, 0x48, 0x9b);

        // IOCTLs on the raw PDO (common/include/BthPS3.h).
        private const uint IOCTL_HID_CONTROL_WRITE   = 0x2AA808;
        private const uint IOCTL_HID_INTERRUPT_READ  = 0x2A680C;

        private const ushort DS3_VID = 0x054C;
        private const ushort DS3_PID = 0x0268;

        private readonly Action<string> _log;
        private Thread _thread;
        private volatile bool _running;

        // SDL virtual-joystick state. _sdlJoystick is our own opened handle for pushing state.
        private uint _instanceId;
        private IntPtr _sdlJoystick = IntPtr.Zero;

        // Keep the callback delegates rooted for the lifetime of the attach.
        private SDL.VJRumble _rumbleCb;
        private SDL.VJSetLED _setLedCb;
        private SDL.VJSetPlayerIndex _setPlayerCb;
        private SDL.VJSetSensorsEnabled _setSensorsCb;

        // Current output state driven back to the DS3 (rumble + player LED bitmask).
        private IntPtr _pdo = IntPtr.Zero;   // open handle to the raw PDO (for output writes)
        private byte _ledMask = 0x02;        // player 1 LED by default
        private byte _rumbleLarge, _rumbleSmall;
        private readonly object _outLock = new object();

        public Ds3DirectService(Action<string> log = null) => _log = log ?? (_ => { });

        public bool IsConnected => _sdlJoystick != IntPtr.Zero;

        /// <summary>Begin watching for a Bluetooth DS3 and stream it as a virtual joystick.
        /// Call after SDL has been initialised (SDL_INIT_JOYSTICK).</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(MonitorLoop) { IsBackground = true, Name = "Ds3DirectService" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _thread?.Join(1500); } catch { }
            Teardown();
        }

        private void MonitorLoop()
        {
            while (_running)
            {
                string path = FindPdoPath();
                if (path == null) { Thread.Sleep(500); continue; }

                IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == INVALID_HANDLE) { Thread.Sleep(500); continue; }
                _pdo = h;

                _log($"DS3(BT): raw PDO opened, kicking + attaching virtual joystick...");
                Kick(h);
                if (!AttachVirtual()) { Teardown(); Thread.Sleep(1000); continue; }
                _log("DS3(BT): virtual joystick attached; streaming.");

                ReadLoop(h);   // blocks until the pad disconnects or Stop()

                Teardown();
                _log("DS3(BT): disconnected; watching for reconnect.");
            }
        }

        // Output report (LED/rumble) first, then the magic enable - the clone needs both.
        private void Kick(IntPtr h)
        {
            WriteOutputReport(h);
            byte[] en = { 0x53, 0xF4, 0x42, 0x03, 0x00, 0x00 };
            DeviceIoControl(h, IOCTL_HID_CONTROL_WRITE, en, en.Length, null, 0, out _, IntPtr.Zero);
        }

        // 50-byte DS3 Bluetooth output report (DsHidMini G_Ds3BthHidOutputReport):
        // [0]=0x52 (SET_REPORT|OUTPUT), [1]=0x01 report id, [2]=LED mask offset -> actually [11].
        private void WriteOutputReport(IntPtr h)
        {
            byte[] o = {
                0x52,0x01, 0x00,0x00,0x00,0x00,0x00, 0x00,0x00,0x00,0x00,0x00,
                0xFF,0x27,0x10,0x00,0x32, 0xFF,0x27,0x10,0x00,0x32, 0xFF,0x27,0x10,0x00,0x32, 0xFF,0x27,0x10,0x00,0x32,
                0x00,0x00,0x00,0x00,0x00, 0x00,0x00,0x00,0x00,0x00, 0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00 };
            lock (_outLock)
            {
                o[2] = _rumbleSmall > 0 ? (byte)0x01 : (byte)0x00; // small motor: duration/on
                o[4] = _rumbleLarge;                               // large motor: strength
                o[11] = _ledMask;                                  // player LED bitmask
            }
            DeviceIoControl(h, IOCTL_HID_CONTROL_WRITE, o, o.Length, null, 0, out _, IntPtr.Zero);
        }

        private void ReadLoop(IntPtr h)
        {
            byte[] buf = new byte[64];
            int idle = 0;
            while (_running)
            {
                if (DeviceIoControl(h, IOCTL_HID_INTERRUPT_READ, null, 0, buf, buf.Length, out int rd, IntPtr.Zero) && rd >= 11 && buf[0] == 0xA1)
                {
                    idle = 0;
                    PushState(buf);
                }
                else
                {
                    if (++idle > 200 && FindPdoPath() == null) break; // pad gone
                    Thread.Sleep(5);
                }
            }
        }

        // ─── SDL virtual joystick ───────────────────────────────────────────────

        private bool AttachVirtual()
        {
            // Standard gamepad shape so SDL treats it as CapType==Gamepad and PadForge auto-maps.
            // axis_mask bits 0-5 = LEFTX,LEFTY,RIGHTX,RIGHTY,LEFT_TRIGGER,RIGHT_TRIGGER.
            // button_mask bits 0-14 = SOUTH..DPAD_RIGHT (SDL_GamepadButton order).
            _rumbleCb = OnRumble; _setLedCb = OnSetLed; _setPlayerCb = OnSetPlayerIndex; _setSensorsCb = OnSetSensors;

            var namePtr = Marshal.StringToHGlobalAnsi("DualShock 3 (Bluetooth)");
            try
            {
                var desc = new SDL.SDL_VirtualJoystickDesc
                {
                    type = (ushort)SDL.SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD,
                    vendor_id = DS3_VID,
                    product_id = DS3_PID,
                    naxes = 6,
                    nbuttons = 15,
                    nhats = 0,
                    button_mask = 0x7FFF, // bits 0-14
                    axis_mask = 0x3F,     // bits 0-5
                    name = namePtr,
                    Rumble = Marshal.GetFunctionPointerForDelegate(_rumbleCb),
                    SetLED = Marshal.GetFunctionPointerForDelegate(_setLedCb),
                    SetPlayerIndex = Marshal.GetFunctionPointerForDelegate(_setPlayerCb),
                    SetSensorsEnabled = Marshal.GetFunctionPointerForDelegate(_setSensorsCb),
                };
                desc.version = (uint)Marshal.SizeOf<SDL.SDL_VirtualJoystickDesc>();

                _instanceId = SDL.SDL_AttachVirtualJoystick(ref desc);
                if (_instanceId == 0) { _log("DS3(BT): SDL_AttachVirtualJoystick failed."); return false; }
                _sdlJoystick = SDL.SDL_OpenJoystick(_instanceId);
                return _sdlJoystick != IntPtr.Zero;
            }
            finally { Marshal.FreeHGlobal(namePtr); }
        }

        private void Teardown()
        {
            if (_sdlJoystick != IntPtr.Zero) { SDL.SDL_CloseJoystick(_sdlJoystick); _sdlJoystick = IntPtr.Zero; }
            if (_instanceId != 0) { SDL.SDL_DetachVirtualJoystick(_instanceId); _instanceId = 0; }
            if (_pdo != INVALID_HANDLE && _pdo != IntPtr.Zero) { CloseHandle(_pdo); _pdo = IntPtr.Zero; }
        }

        // DS3 Bluetooth input report: [0]=0xA1 [1]=0x01 [2]=0x00 [3]=btn1 [4]=btn2 [5]=btnPS
        //   [7]=LX [8]=LY [9]=RX [10]=RY, pressure bytes [15..26] (USB layout +1 for the 0xA1 header).
        private void PushState(byte[] b)
        {
            IntPtr j = _sdlJoystick;
            if (j == IntPtr.Zero) return;

            byte b1 = b[3], b2 = b[4], b3 = b[5];
            // btn1: Select L3 R3 Start  Up Right Down Left
            bool select = (b1 & 0x01) != 0, l3 = (b1 & 0x02) != 0, r3 = (b1 & 0x04) != 0, start = (b1 & 0x08) != 0;
            bool up = (b1 & 0x10) != 0, right = (b1 & 0x20) != 0, down = (b1 & 0x40) != 0, left = (b1 & 0x80) != 0;
            // btn2: L2 R2 L1 R1  Tri Cir Cross Sq
            bool l1 = (b2 & 0x04) != 0, r1 = (b2 & 0x08) != 0;
            bool tri = (b2 & 0x10) != 0, cir = (b2 & 0x20) != 0, cross = (b2 & 0x40) != 0, sq = (b2 & 0x80) != 0;
            bool ps = (b3 & 0x01) != 0;

            // SDL_GamepadButton order: 0 South 1 East 2 West 3 North 4 Back 5 Guide 6 Start
            //   7 LStick 8 RStick 9 LShoulder 10 RShoulder 11 DpadUp 12 DpadDown 13 DpadLeft 14 DpadRight
            SDL.SDL_SetJoystickVirtualButton(j, 0, cross);
            SDL.SDL_SetJoystickVirtualButton(j, 1, cir);
            SDL.SDL_SetJoystickVirtualButton(j, 2, sq);
            SDL.SDL_SetJoystickVirtualButton(j, 3, tri);
            SDL.SDL_SetJoystickVirtualButton(j, 4, select);
            SDL.SDL_SetJoystickVirtualButton(j, 5, ps);
            SDL.SDL_SetJoystickVirtualButton(j, 6, start);
            SDL.SDL_SetJoystickVirtualButton(j, 7, l3);
            SDL.SDL_SetJoystickVirtualButton(j, 8, r3);
            SDL.SDL_SetJoystickVirtualButton(j, 9, l1);
            SDL.SDL_SetJoystickVirtualButton(j, 10, r1);
            SDL.SDL_SetJoystickVirtualButton(j, 11, up);
            SDL.SDL_SetJoystickVirtualButton(j, 12, down);
            SDL.SDL_SetJoystickVirtualButton(j, 13, left);
            SDL.SDL_SetJoystickVirtualButton(j, 14, right);

            SDL.SDL_SetJoystickVirtualAxis(j, 0, AxisFromByte(b[7]));   // LX
            SDL.SDL_SetJoystickVirtualAxis(j, 1, AxisFromByte(b[8]));   // LY
            SDL.SDL_SetJoystickVirtualAxis(j, 2, AxisFromByte(b[9]));   // RX
            SDL.SDL_SetJoystickVirtualAxis(j, 3, AxisFromByte(b[10]));  // RY
            SDL.SDL_SetJoystickVirtualAxis(j, 4, TriggerFromByte(b[19])); // L2 pressure
            SDL.SDL_SetJoystickVirtualAxis(j, 5, TriggerFromByte(b[20])); // R2 pressure
        }

        private static short AxisFromByte(byte v) => (short)Math.Clamp((v - 128) * 257, -32768, 32767);
        private static short TriggerFromByte(byte v) => (short)Math.Clamp(v * 129, 0, 32767);

        // ─── SDL callbacks -> DS3 output ────────────────────────────────────────

        private bool OnRumble(IntPtr userdata, ushort low, ushort high)
        {
            lock (_outLock) { _rumbleLarge = (byte)(low >> 8); _rumbleSmall = (byte)(high >> 8); }
            if (_pdo != IntPtr.Zero && _pdo != INVALID_HANDLE) WriteOutputReport(_pdo);
            return true;
        }

        private bool OnSetLed(IntPtr userdata, byte r, byte g, byte bl) => true; // DS3 has no RGB; player LED via SetPlayerIndex

        private void OnSetPlayerIndex(IntPtr userdata, int playerIndex)
        {
            // DS3 player LED bitmask: LED1..LED4 = bits 1..4 (bit0 unused).
            byte mask = playerIndex switch { 0 => 0x02, 1 => 0x04, 2 => 0x08, 3 => 0x10, _ => 0x02 };
            lock (_outLock) { _ledMask = mask; }
            if (_pdo != IntPtr.Zero && _pdo != INVALID_HANDLE) WriteOutputReport(_pdo);
        }

        private bool OnSetSensors(IntPtr userdata, bool enabled) => true; // sensors always in the report

        // ─── device enumeration + native I/O ────────────────────────────────────

        private string FindPdoPath()
        {
            Guid g = BthPs3Interface;
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == INVALID_HANDLE) return null;
            var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            try
            {
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did); i++)
                {
                    int req = 0;
                    SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, ref req, IntPtr.Zero);
                    IntPtr det = Marshal.AllocHGlobal(req);
                    try
                    {
                        Marshal.WriteInt32(det, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref did, det, req, ref req, IntPtr.Zero))
                        {
                            string p = Marshal.PtrToStringUni(det + 4);
                            // one DS3 for now; the PDO path carries VID_054C&PID_0268
                            if (p != null && p.IndexOf("054c", StringComparison.OrdinalIgnoreCase) >= 0) return p;
                        }
                    }
                    finally { Marshal.FreeHGlobal(det); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return null;
        }

        private const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3;
        private static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int detailSize, ref int required, IntPtr devInfo);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inLen, byte[] outBuf, int outLen, out int ret, IntPtr ov);
    }
}
