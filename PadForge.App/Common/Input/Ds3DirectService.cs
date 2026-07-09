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
    /// I/O model (each point traced to the drivers' source):
    ///  - Reads use EXACTLY 50-byte buffers. BthPS3 submits interrupt IN transfers
    ///    without ACL_SHORT_TRANSFER_OK (L2CAP.Transfer.c:224), so a larger buffer
    ///    only completes once it FILLS across multiple reports - misaligning the
    ///    stream and collapsing the rate. DsHidMini reads with 0x32-byte buffers for
    ///    the same reason (Device.c:1228-1249, BTHPS3_SIXAXIS_HID_INPUT_REPORT_SIZE).
    ///  - One read handle with a single always-pended synchronous read (DsHidMini
    ///    keeps exactly one outstanding read too: ContinuousRequestCount=1).
    ///  - Writes go through a dedicated writer thread on a SEPARATE handle (separate
    ///    file objects do not serialize against each other), rate-limited to one
    ///    output packet per 150 ms with newest-wins coalescing, mirroring DsHidMini's
    ///    BT output rate control (Configuration.c:1000-1001). SDL's Rumble callback
    ///    runs under SDL's global joystick lock on the 1000 Hz polling thread, so it
    ///    must NEVER touch the device - it only stores state and signals the writer.
    /// </summary>
    public sealed class Ds3DirectService
    {
        // GUID_DEVINTERFACE_BTHPS3 {968E1849-73B1-4876-B80A-ED6DD171489B} - the RAW PDO's IOCTL interface.
        private static readonly Guid BthPs3Interface =
            new Guid(0x968e1849, 0x73b1, 0x4876, 0xb8, 0x0a, 0xed, 0x6d, 0xd1, 0x71, 0x48, 0x9b);

        // IOCTLs on the raw PDO (BthPS3 common/include/BthPS3.h).
        private const uint IOCTL_HID_CONTROL_WRITE  = 0x2AA808;
        private const uint IOCTL_HID_INTERRUPT_READ = 0x2A680C;

        private const ushort DS3_VID = 0x054C;
        private const ushort DS3_PID = 0x0268;

        // BTHPS3_SIXAXIS_HID_INPUT_REPORT_SIZE (BthPS3.h:350). Must match exactly; see I/O model above.
        private const int DS3_BT_INPUT_REPORT_SIZE = 0x32;

        // Minimum interval between Bluetooth output packets (DsHidMini's proven default).
        private const int OUTPUT_MIN_INTERVAL_MS = 150;

        private readonly Action<string> _log;
        private Thread _readThread;
        private Thread _writeThread;
        private volatile bool _running;

        // SDL virtual-joystick state. _sdlJoystick is our own opened handle for pushing state.
        private uint _instanceId;
        private IntPtr _sdlJoystick = IntPtr.Zero;

        // Keep the callback delegates rooted for the lifetime of the attach.
        private SDL.VJRumble _rumbleCb;
        private SDL.VJSetLED _setLedCb;
        private SDL.VJSetPlayerIndex _setPlayerCb;
        private SDL.VJSetSensorsEnabled _setSensorsCb;

        // Device handles. _readPdo is owned by the read loop; _writePdo by the writer
        // thread. Guarded by _outLock (writer state shares it).
        private IntPtr _readPdo = IntPtr.Zero;
        private IntPtr _writePdo = IntPtr.Zero;

        // Writer state: SDL callbacks store here and signal; the writer flushes.
        private readonly object _outLock = new object();
        private readonly AutoResetEvent _writeSignal = new AutoResetEvent(false);
        private byte _ledMask = 0x02;            // player 1 LED by default
        private byte _rumbleLarge, _rumbleSmall;
        private bool _outDirty;
        private volatile bool _everGotInput;

        public Ds3DirectService(Action<string> log = null) => _log = log ?? (_ => { });

        public bool IsConnected => _sdlJoystick != IntPtr.Zero;

        /// <summary>Begin watching for a Bluetooth DS3 and stream it as a virtual joystick.
        /// Call after SDL has been initialised (SDL_INIT_JOYSTICK).</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
            _readThread = new Thread(MonitorLoop) { IsBackground = true, Name = "Ds3DirectRead" };
            _readThread.Start();
        }

        public void Stop()
        {
            _running = false;
            _writeSignal.Set();
            lock (_outLock) { if (_readPdo != IntPtr.Zero) CancelIoEx(_readPdo, IntPtr.Zero); }
            try { _readThread?.Join(1500); } catch { }
            Teardown();
        }

        private void MonitorLoop()
        {
            while (_running)
            {
                string path = FindPdoPath();
                if (path == null) { Thread.Sleep(500); continue; }

                IntPtr rh = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (rh == INVALID_HANDLE) { Thread.Sleep(500); continue; }
                IntPtr wh = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (wh == INVALID_HANDLE) { CloseHandle(rh); Thread.Sleep(500); continue; }

                lock (_outLock) { _readPdo = rh; _writePdo = wh; _everGotInput = false; _outDirty = true; }

                _log("DS3(BT): raw PDO opened, kicking + attaching virtual joystick...");
                if (!AttachVirtual()) { Teardown(); Thread.Sleep(1000); continue; }

                _writeThread = new Thread(WriterLoop) { IsBackground = true, Name = "Ds3DirectWrite" };
                _writeThread.Start();

                _log("DS3(BT): virtual joystick attached; streaming.");
                ReadLoop(rh);   // blocks until the pad disconnects or Stop()

                Teardown();
                _log("DS3(BT): disconnected; watching for reconnect.");
            }
        }

        // ─── writer thread: kick, re-kick, rate-limited output flush ────────────

        private void WriterLoop()
        {
            long attachedAt = Environment.TickCount64;
            long lastWrite = 0, lastKick = 0;
            int kicks = 0;

            // Immediate first kick: output report (LED) then the magic enable.
            // The clone needs the output report FIRST (DsHidMini G_Ds3BthHidOutputReport
            // ordering); the bare enable alone does not start it.
            Kick(); kicks = 1; lastKick = attachedAt; lastWrite = attachedAt;

            while (_running)
            {
                _writeSignal.WaitOne(50);
                if (!_running) break;

                long now = Environment.TickCount64;

                // Re-kick while silent (DsHidMini re-sends the enable after 1 s of no input).
                if (!_everGotInput && kicks < 5 && now - lastKick >= 1000)
                {
                    _log($"DS3(BT): no input yet - re-kick #{kicks + 1}");
                    Kick(); kicks++; lastKick = now;
                    continue;
                }

                bool doWrite;
                lock (_outLock) { doWrite = _outDirty && now - lastWrite >= OUTPUT_MIN_INTERVAL_MS; }
                if (doWrite)
                {
                    WriteOutputReport();
                    lastWrite = now;
                }
            }
        }

        private void Kick()
        {
            WriteOutputReport();
            byte[] en = { 0x53, 0xF4, 0x42, 0x03, 0x00, 0x00 };
            IntPtr h; lock (_outLock) h = _writePdo;
            if (h != IntPtr.Zero && h != INVALID_HANDLE)
                DeviceIoControl(h, IOCTL_HID_CONTROL_WRITE, en, en.Length, null, 0, out _, IntPtr.Zero);
        }

        // 50-byte DS3 Bluetooth output report (DsHidMini G_Ds3BthHidOutputReport):
        // [0]=0x52 (SET_REPORT|OUTPUT), [1]=0x01 report id, then the raw 48-byte output
        // payload, so raw offset N lands at [N+2]: smallDur raw[1]->[3], smallOn
        // raw[2]->[4], largeDur raw[3]->[5], largeForce raw[4]->[6], LED raw[9]->[11].
        // (Proven on hardware: the USB path drives raw[2]/raw[4]/raw[9]; the BT LED
        // at [11] lit during the prototype stream.)
        private void WriteOutputReport()
        {
            byte[] o = {
                0x52,0x01, 0x00,0xFF,0x00,0xFF,0x00, 0x00,0x00,0x00,0x00,0x00,
                0xFF,0x27,0x10,0x00,0x32, 0xFF,0x27,0x10,0x00,0x32, 0xFF,0x27,0x10,0x00,0x32, 0xFF,0x27,0x10,0x00,0x32,
                0x00,0x00,0x00,0x00,0x00, 0x00,0x00,0x00,0x00,0x00, 0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00 };
            IntPtr h;
            lock (_outLock)
            {
                o[4] = _rumbleSmall > 0 ? (byte)0x01 : (byte)0x00; // small motor on/off
                o[6] = _rumbleLarge;                               // large motor strength
                o[11] = _ledMask;                                  // player LED bitmask
                _outDirty = false;
                h = _writePdo;
            }
            if (h != IntPtr.Zero && h != INVALID_HANDLE)
                DeviceIoControl(h, IOCTL_HID_CONTROL_WRITE, o, o.Length, null, 0, out _, IntPtr.Zero);
        }

        // ─── read loop: exact-size buffer, one pended read, no hot-path churn ───

        private void ReadLoop(IntPtr h)
        {
            byte[] buf = new byte[DS3_BT_INPUT_REPORT_SIZE];
            long lastProbe = 0;
            while (_running)
            {
                if (DeviceIoControl(h, IOCTL_HID_INTERRUPT_READ, null, 0, buf, buf.Length, out int rd, IntPtr.Zero))
                {
                    if (rd >= 11 && buf[0] == 0xA1 && buf[1] == 0x01)
                    {
                        _everGotInput = true;
                        PushState(buf, rd);
                    }
                    // Non-input traffic on the interrupt channel: ignore, read again.
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_DEVICE_NOT_CONNECTED || err == ERROR_FILE_NOT_FOUND ||
                        err == ERROR_INVALID_HANDLE || err == ERROR_OPERATION_ABORTED)
                        break;
                    // Transient failure: brief pause; liveness probe at most once per second.
                    Thread.Sleep(2);
                    long now = Environment.TickCount64;
                    if (now - lastProbe >= 1000)
                    {
                        lastProbe = now;
                        if (FindPdoPath() == null) break;
                    }
                }
            }
        }

        // ─── SDL virtual joystick ───────────────────────────────────────────────

        private bool AttachVirtual()
        {
            // Standard gamepad shape so SDL treats it as a gamepad and PadForge auto-maps.
            _rumbleCb = OnRumble; _setLedCb = OnSetLed; _setPlayerCb = OnSetPlayerIndex; _setSensorsCb = OnSetSensors;

            var namePtr = Marshal.StringToHGlobalAnsi("DualShock 3 (Bluetooth)");
            // Two sensors: accel + gyro at the DS3's ~100 Hz report rate. SDL deep-copies
            // this array during attach (SDL_virtualjoystick.c attach inner), so the
            // unmanaged copy only needs to live for the duration of the call.
            var sensors = new SDL.SDL_VirtualJoystickSensorDesc[]
            {
                new SDL.SDL_VirtualJoystickSensorDesc { type = SDL_SENSOR_ACCEL, rate = 100.0f },
                new SDL.SDL_VirtualJoystickSensorDesc { type = SDL_SENSOR_GYRO,  rate = 100.0f },
            };
            int sensorSize = Marshal.SizeOf<SDL.SDL_VirtualJoystickSensorDesc>();
            IntPtr sensorsPtr = Marshal.AllocHGlobal(sensorSize * sensors.Length);
            try
            {
                for (int i = 0; i < sensors.Length; i++)
                    Marshal.StructureToPtr(sensors[i], sensorsPtr + i * sensorSize, false);

                var desc = new SDL.SDL_VirtualJoystickDesc
                {
                    type = (ushort)SDL.SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD,
                    vendor_id = DS3_VID,
                    product_id = DS3_PID,
                    // 6 gamepad axes + the 10 button-pressure axes on 6-15, mirroring
                    // upstream SDL's PS3 driver (SDL_hidapi_ps3.c:486-516) so the #193
                    // HasExtraGenericAxes seam surfaces them as "Axis 6".."Axis 15"
                    // identically on both transports.
                    naxes = 16,
                    nbuttons = 15,
                    nhats = 0,
                    nsensors = (ushort)sensors.Length,
                    sensors = sensorsPtr,
                    button_mask = 0x7FFF, // bits 0-14 (SOUTH..DPAD_RIGHT)
                    axis_mask = 0x3F,     // bits 0-5 (LX,LY,RX,RY,LT,RT)
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
            finally
            {
                Marshal.FreeHGlobal(sensorsPtr);
                Marshal.FreeHGlobal(namePtr);
            }
        }

        private void Teardown()
        {
            _writeSignal.Set();
            try { if (_writeThread != null && _writeThread != Thread.CurrentThread) _writeThread.Join(1000); } catch { }
            _writeThread = null;

            if (_sdlJoystick != IntPtr.Zero) { SDL.SDL_CloseJoystick(_sdlJoystick); _sdlJoystick = IntPtr.Zero; }
            if (_instanceId != 0) { SDL.SDL_DetachVirtualJoystick(_instanceId); _instanceId = 0; }

            lock (_outLock)
            {
                if (_readPdo != IntPtr.Zero && _readPdo != INVALID_HANDLE) CloseHandle(_readPdo);
                if (_writePdo != IntPtr.Zero && _writePdo != INVALID_HANDLE) CloseHandle(_writePdo);
                _readPdo = IntPtr.Zero;
                _writePdo = IntPtr.Zero;
            }
        }

        // ─── report parse -> SDL state ──────────────────────────────────────────
        //
        // BT input report = 0xA1 0x01 + the 48 remaining bytes of the raw 49-byte DS3
        // report, so raw offset N lands at buf[N+1] (raw layout: DsHidMini Ds3Types.h).
        //   buttons raw[2..4] -> buf[3..5]; sticks raw[6..9] -> buf[7..10];
        //   pressures raw[14..25] -> buf[15..26]; accel raw[41..46] / gyro raw[47..48]
        //   (big-endian words) -> buf[42..47] / buf[48..49].

        private void PushState(byte[] b, int len)
        {
            IntPtr j = _sdlJoystick;
            if (j == IntPtr.Zero) return;

            byte b1 = b[3], b2 = b[4], b3 = b[5];
            // raw btn1: Select L3 R3 Start  Up Right Down Left
            bool select = (b1 & 0x01) != 0, l3 = (b1 & 0x02) != 0, r3 = (b1 & 0x04) != 0, start = (b1 & 0x08) != 0;
            bool up = (b1 & 0x10) != 0, right = (b1 & 0x20) != 0, down = (b1 & 0x40) != 0, left = (b1 & 0x80) != 0;
            // raw btn2: L2 R2 L1 R1  Tri Cir Cross Sq
            bool l1 = (b2 & 0x04) != 0, r1 = (b2 & 0x08) != 0;
            bool tri = (b2 & 0x10) != 0, cir = (b2 & 0x20) != 0, cross = (b2 & 0x40) != 0, sq = (b2 & 0x80) != 0;
            bool ps = (b3 & 0x01) != 0;

            // One lock acquisition for the whole frame: the per-call locks nest
            // (recursive), the frame publishes atomically, and contention with the
            // 1000 Hz polling thread costs one wait instead of 23.
            SDL.SDL_LockJoysticks();
            try
            {
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

                SDL.SDL_SetJoystickVirtualAxis(j, 0, AxisFromByte(b[7]));      // LX
                SDL.SDL_SetJoystickVirtualAxis(j, 1, AxisFromByte(b[8]));      // LY
                SDL.SDL_SetJoystickVirtualAxis(j, 2, AxisFromByte(b[9]));      // RX
                SDL.SDL_SetJoystickVirtualAxis(j, 3, AxisFromByte(b[10]));     // RY
                SDL.SDL_SetJoystickVirtualAxis(j, 4, PressureAxis(b[19]));     // L2 pressure (raw[18])
                SDL.SDL_SetJoystickVirtualAxis(j, 5, PressureAxis(b[20]));     // R2 pressure (raw[19])

                // Button-pressure axes 6-15, same order and scale as upstream SDL's
                // PS3 driver (SDL_hidapi_ps3.c button_axis_offsets, axis_index from 6;
                // raw offset -> BT buf offset is +1). Order: South East West North,
                // LShoulder RShoulder, DpadUp DpadDown DpadLeft DpadRight.
                SDL.SDL_SetJoystickVirtualAxis(j, 6, PressureAxis(b[25]));   // Cross    (raw[24])
                SDL.SDL_SetJoystickVirtualAxis(j, 7, PressureAxis(b[24]));   // Circle   (raw[23])
                SDL.SDL_SetJoystickVirtualAxis(j, 8, PressureAxis(b[26]));   // Square   (raw[25])
                SDL.SDL_SetJoystickVirtualAxis(j, 9, PressureAxis(b[23]));   // Triangle (raw[22])
                SDL.SDL_SetJoystickVirtualAxis(j, 10, PressureAxis(b[21]));  // L1       (raw[20])
                SDL.SDL_SetJoystickVirtualAxis(j, 11, PressureAxis(b[22]));  // R1       (raw[21])
                SDL.SDL_SetJoystickVirtualAxis(j, 12, PressureAxis(b[15]));  // D-pad Up    (raw[14])
                SDL.SDL_SetJoystickVirtualAxis(j, 13, PressureAxis(b[17]));  // D-pad Down  (raw[16])
                SDL.SDL_SetJoystickVirtualAxis(j, 14, PressureAxis(b[18]));  // D-pad Left  (raw[17])
                SDL.SDL_SetJoystickVirtualAxis(j, 15, PressureAxis(b[16]));  // D-pad Right (raw[15])

                // Motion. Raw words are BIG-endian (DsHidMini byteswaps them before its
                // SXS serve; we read the raw report, so swap here). Frame mapping and
                // scale follow the fork's SonySixaxis motion patch as landed (memory:
                // sdl-patch-spec-ds3-sixaxis-motion, hardware-verified 2026-07-08),
                // composed back through DsHidMini's transforms to raw byte order:
                //   SDL accel = ( (ax-512), -(az-512), -(ay-512) ) / 113 * g
                //   SDL gyro  = ( 0, -(gz-512) * (90/123) deg/s, 0 )   [genuine-anchored negation]
                if (len >= DS3_BT_INPUT_REPORT_SIZE)
                {
                    int ax = (b[42] << 8) | b[43];
                    int ay = (b[44] << 8) | b[45];
                    int az = (b[46] << 8) | b[47];
                    int gz = (b[48] << 8) | b[49];

                    ulong ts = SDL.SDL_GetTicksNS();

                    _accelData[0] = (ax - 512) * ACCEL_SCALE;
                    _accelData[1] = -(az - 512) * ACCEL_SCALE;
                    _accelData[2] = -(ay - 512) * ACCEL_SCALE;
                    SDL.SDL_SendJoystickVirtualSensorData(j, SDL_SENSOR_ACCEL, ts, _accelData, 3);

                    _gyroData[0] = 0.0f;
                    _gyroData[1] = -(gz - 512) * GYRO_SCALE;
                    _gyroData[2] = 0.0f;
                    SDL.SDL_SendJoystickVirtualSensorData(j, SDL_SENSOR_GYRO, ts, _gyroData, 3);
                }
            }
            finally { SDL.SDL_UnlockJoysticks(); }
        }

        private const int SDL_SENSOR_ACCEL = 1;
        private const int SDL_SENSOR_GYRO = 2;
        private const float SDL_STANDARD_GRAVITY = 9.80665f;
        private const float ACCEL_SCALE = SDL_STANDARD_GRAVITY / 113.0f;                  // 113 LSB/g
        private const float GYRO_SCALE = (90.0f / 123.0f) * ((float)Math.PI / 180.0f);    // 123 LSB per 90 deg/s

        private readonly float[] _accelData = new float[3];
        private readonly float[] _gyroData = new float[3];

        private static short AxisFromByte(byte v) => (short)Math.Clamp((v - 128) * 257, -32768, 32767);

        /// <summary>0..255 pressure to the full SDL axis range, exactly as SDL's PS3
        /// driver scales it (v*257 - 32768; released = -32768). The virtual backend
        /// rests trigger axes at SDL_JOYSTICK_AXIS_MIN, confirming the convention.</summary>
        private static short PressureAxis(byte v) => (short)(v * 257 - 32768);

        // ─── SDL callbacks: store + signal ONLY (they run under SDL's joystick lock
        //     on the polling thread; device I/O here would stall the whole pipeline) ─

        private bool OnRumble(IntPtr userdata, ushort low, ushort high)
        {
            lock (_outLock)
            {
                _rumbleLarge = (byte)(low >> 8);
                _rumbleSmall = (byte)(high >> 8);
                _outDirty = true;
            }
            _writeSignal.Set();
            return true;
        }

        private bool OnSetLed(IntPtr userdata, byte r, byte g, byte bl) => true; // DS3 has no RGB; player LED via SetPlayerIndex

        private void OnSetPlayerIndex(IntPtr userdata, int playerIndex)
        {
            // DS3 player LED bitmask: LED1..LED4 = bits 1..4 (bit 0 unused).
            byte mask = playerIndex switch { 0 => 0x02, 1 => 0x04, 2 => 0x08, 3 => 0x10, _ => 0x02 };
            lock (_outLock) { _ledMask = mask; _outDirty = true; }
            _writeSignal.Set();
        }

        private bool OnSetSensors(IntPtr userdata, bool enabled) => true; // sensors are always in the report

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
        private const int ERROR_FILE_NOT_FOUND = 2, ERROR_INVALID_HANDLE = 6,
                          ERROR_OPERATION_ABORTED = 995, ERROR_DEVICE_NOT_CONNECTED = 1167;
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
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(IntPtr h, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inLen, byte[] outBuf, int outLen, out int ret, IntPtr ov);
    }
}
