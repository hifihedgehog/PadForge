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

        // Resend the output report at least this often while streaming so the DS3 keeps
        // rumble alive (ScpToolkit resends the whole report every 500 ms, BthDs3.cs:158).
        // 500 ms is under the ~1 s motor cutout and well above the 150 ms floor.
        private const int RUMBLE_KEEPALIVE_MS = 500;

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

        // Which transport the current session is streaming over. The DS3 works over
        // Bluetooth (BthPS3 raw PDO, IOCTL I/O) OR USB (inbox WinUSB, control-transfer +
        // interrupt-pipe I/O). The report is the SAME raw DS3 layout on both; USB just
        // lacks Bluetooth's leading 0xA1 HID-transport byte, so the USB reader prepends
        // one and the shared PushState parser handles both verbatim.
        private enum Ds3Transport { None, Bluetooth, Usb }
        private volatile Ds3Transport _transport = Ds3Transport.None;

        // Bluetooth handles. _readPdo is owned by the read loop; _writePdo by the writer
        // thread. Guarded by _outLock (writer state shares it).
        private IntPtr _readPdo = IntPtr.Zero;
        private IntPtr _writePdo = IntPtr.Zero;

        // USB (WinUSB) handles: _usbDev is the CreateFile handle, _usbIfh the WinUSB
        // interface handle used for control transfers and pipe reads, _usbInPipe the
        // interrupt-IN endpoint id. Same lock discipline as the BT handles.
        private IntPtr _usbDev = IntPtr.Zero;
        private IntPtr _usbIfh = IntPtr.Zero;
        private byte _usbInPipe;
        private long _lastUsbBindAttempt;

        // The real device-interface path of the current transport (BthPS3 PDO over BT,
        // WinUSB interface over USB). The SDL virtual joystick has no path of its own, so
        // this is surfaced for the Device Dossier (path display + BT/USB classification)
        // via SdlDeviceWrapper.ExternalDevicePathProvider. NOT used for device identity
        // (the two transports' paths differ; identity stays on the stable SDL GUID).
        private volatile string _transportPath;

        // Per-connection writer generation: Teardown flips it false so the writer
        // exits on THIS pad's disconnect even though the service keeps _running.
        // Without it, every disconnect leaked a live writer thread (the loop's only
        // exit was _running) and reconnect stacked another one on top.
        private volatile bool _writerRun;

        // Held across every DeviceIoControl on the write handle AND across that
        // handle's close in Teardown, so a write can never land on a closed (and
        // possibly recycled) handle value. The SDL callbacks never take this lock;
        // they only store state under _outLock, so the hot path stays I/O-free.
        // Lock order where both are held: _ioLock outer, _outLock inner.
        private readonly object _ioLock = new object();

        // Serializes Teardown between Stop() and MonitorLoop (both call it) and
        // makes it idempotent, so the SDL close/detach pair can never run twice
        // for the same handles.
        private readonly object _teardownLock = new object();

        // Writer state: SDL callbacks store here and signal; the writer flushes.
        private readonly object _outLock = new object();
        private readonly AutoResetEvent _writeSignal = new AutoResetEvent(false);
        private byte _ledMask = 0x02;            // player 1 LED by default
        private byte _rumbleLarge, _rumbleSmall;
        private bool _outDirty;
        private volatile bool _everGotInput;

        // ── unpair coordination ──────────────────────────────────────────────────
        // While true, the monitor loop tears down any live pad and does not re-grab
        // it. The App-layer unpair flow sets this (and cancels the current read)
        // before deleting a DS3's Bluetooth records + cycling the radio, so a still-
        // connected pad can't re-attach a ghost virtual joystick mid-unpair.
        // REFCOUNTED, not a bool. Two independent flows suppress this: the
        // pairing ceremony (RunPairing's finally) and the remove-device
        // unpair (UnpairAllDs3's finally, armed even earlier by the Devices
        // page). They overlap, since the ceremony holds the radio gate only
        // for its sixpair steps and both are launched fire-and-forget. A
        // plain bool let whichever finished FIRST re-enable the monitor
        // under the other, which then re-grabbed the pad mid-ceremony.
        private static int _suppressDepth;
        private static bool _suppressReconnect => System.Threading.Volatile.Read(ref _suppressDepth) > 0;
        private static volatile Ds3DirectService _current;

        /// <summary>Detach any live DS3 now and block reconnect until the
        /// matching <see cref="AllowReconnect"/>. Nests: every call must be
        /// paired, and the monitor resumes only when the last one releases.
        /// Call before removing the pad's BT records.</summary>
        public static void SuppressAndRelease()
        {
            System.Threading.Interlocked.Increment(ref _suppressDepth);
            _current?.CancelCurrentRead();
        }

        /// <summary>Test seam (InternalsVisibleTo PadForge.Tests): whether
        /// the monitor is currently suppressed. The regression this locks:
        /// an acquire/release imbalance across the unpair flow left the
        /// depth stuck above zero, and no DS3 could attach again until the
        /// app restarted (owner repro 2026-08-08).</summary>
        internal static bool IsReconnectSuppressedForTest => _suppressReconnect;

        /// <summary>Test seam: hard-reset the suppression depth so tests
        /// cannot leak state into each other.</summary>
        internal static void ResetSuppressionForTest()
            => System.Threading.Interlocked.Exchange(ref _suppressDepth, 0);

        /// <summary>Release one suppression claim (see
        /// <see cref="SuppressAndRelease"/>). Never drops below zero, so an
        /// unbalanced extra release cannot strand the monitor suppressed or
        /// un-suppress a claim it does not own.</summary>
        public static void AllowReconnect()
        {
            while (true)
            {
                int cur = System.Threading.Volatile.Read(ref _suppressDepth);
                if (cur <= 0) return;
                if (System.Threading.Interlocked.CompareExchange(ref _suppressDepth, cur - 1, cur) == cur) return;
            }
        }

        private void CancelCurrentRead()
        {
            lock (_outLock)
            {
                if (_readPdo != IntPtr.Zero && _readPdo != INVALID_HANDLE) CancelIoEx(_readPdo, IntPtr.Zero);
                if (_usbDev != IntPtr.Zero && _usbDev != INVALID_HANDLE) CancelIoEx(_usbDev, IntPtr.Zero);
            }
        }

        public Ds3DirectService(Action<string> log = null) => _log = log ?? (_ => { });

        public bool IsConnected => _sdlJoystick != IntPtr.Zero;

        /// <summary>SDL instance id of the attached virtual joystick (0 when absent).</summary>
        public uint InstanceId => _instanceId;

        // ─── battery (raw[30] -> BT buf[31], DS_BATTERY_STATUS in DsCommon.h:61-70) ─
        //
        // SDL's PS3 driver has no battery path and the virtual-joystick API has no
        // power setter, so battery reaches the #167 lane through
        // SdlDeviceWrapper.ExternalPowerInfoProvider, keyed by SDL instance id.
        // Discharge levels 0x01..0x05 map to quintiles (level * 20%). 0xEE/0xEF are
        // USB charge states that cannot appear on a Bluetooth link; handled defensively.

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (int Percent, bool Charging)>
            PowerByInstance = new();

        /// <summary>Battery for a given SDL instance id, or null if it isn't a DS3
        /// this service is driving. Wired into SdlDeviceWrapper.ExternalPowerInfoProvider.</summary>
        public static (int Percent, bool Charging)? GetPowerInfo(uint instanceId)
            => PowerByInstance.TryGetValue(instanceId, out var p) ? p : null;

        /// <summary>The real transport interface path for a given SDL instance id (the
        /// BthPS3 PDO path over Bluetooth, the WinUSB interface path over USB), or null
        /// if it isn't the DS3 this service is driving. Wired into
        /// SdlDeviceWrapper.ExternalDevicePathProvider so the Dossier can show a path and
        /// classify the transport for a device whose SDL path is empty.</summary>
        public static string GetDevicePath(uint sdlInstanceId)
        {
            var svc = _current;
            return (svc != null && svc.IsConnected && svc.InstanceId == sdlInstanceId)
                ? svc._transportPath : null;
        }

        /// <summary>Set the player LED for a relayed player-index frame (#191 over Remote
        /// Link), but only when this instance id is the DS3 this service is driving.
        /// SetPlayerNumber is change-detected, so a repeated value is a no-op.</summary>
        public static bool TrySetPlayerNumber(uint sdlInstanceId, int oneBasedNumber)
        {
            var svc = _current;
            if (svc == null || !svc.IsConnected || svc.InstanceId != sdlInstanceId) return false;
            svc.SetPlayerNumber(oneBasedNumber);
            return true;
        }

        private byte _lastBattery = 0xFF;

        private void UpdateBattery(byte status)
        {
            // 0x00 = DsBatteryStatusNone (DsCommon.h:62): the pad has no reading yet
            // (transient right after connect). Publishing it would show a hard "0%";
            // leave the entry absent/unchanged so the indicator stays hidden instead.
            if (status == 0x00) return;
            if (status == _lastBattery || _instanceId == 0) return;
            _lastBattery = status;
            (int Percent, bool Charging) p = status switch
            {
                0xEF => (100, true),                        // charged (USB-only state)
                0xEE => (-1, true),                         // charging (USB-only state)
                _ => (Math.Min((int)status, 5) * 20, false) // 0x01..0x05 discharge quintiles
            };
            PowerByInstance[_instanceId] = p;
        }

        /// <summary>Player LED idle floor (#191): the virtual controller's 1-based
        /// display number lights LED 1-4, wrapping past 4 exactly like SDL's PS3
        /// driver (SDL_hidapi_ps3.c:244, 0x01 &lt;&lt; (1 + index % 4)). 0 = unmapped,
        /// keeps LED 1.</summary>
        public void SetPlayerNumber(int oneBasedNumber)
        {
            int zeroBased = oneBasedNumber <= 0 ? 0 : (oneBasedNumber - 1) % 4;
            byte mask = (byte)(0x01 << (1 + zeroBased));
            bool changed;
            lock (_outLock)
            {
                changed = _ledMask != mask;
                if (changed) { _ledMask = mask; _outDirty = true; }
            }
            if (changed) _writeSignal.Set();
        }

        /// <summary>Begin watching for a DS3 (USB or Bluetooth) and stream it as a virtual
        /// joystick. Call after SDL has been initialised (SDL_INIT_JOYSTICK).</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
            _current = this;
            _readThread = new Thread(MonitorLoop) { IsBackground = true, Name = "Ds3DirectRead" };
            _readThread.Start();
        }

        public void Stop()
        {
            _running = false;
            if (_current == this) _current = null;
            _writeSignal.Set();
            lock (_outLock)
            {
                if (_readPdo != IntPtr.Zero) CancelIoEx(_readPdo, IntPtr.Zero);
                if (_usbDev != IntPtr.Zero && _usbDev != INVALID_HANDLE) CancelIoEx(_usbDev, IntPtr.Zero);
            }
            try { _readThread?.Join(1500); } catch { }
            Teardown();
        }

        private void MonitorLoop()
        {
            while (_running)
            {
                // Unpair in progress: drop any live pad and don't re-grab it.
                if (_suppressReconnect) { Teardown(); Thread.Sleep(250); continue; }

                // USB takes priority: a present WinUSB interface means the pad is
                // physically wired (it can't be on both transports at once), whereas the
                // BthPS3 PDO node lingers present even after the pad leaves Bluetooth, so
                // trying BT first could grab a dead node and never reach a live USB pad.
                if (!OpenUsb() && !OpenBluetooth()) { Thread.Sleep(500); continue; }

                lock (_outLock) { _everGotInput = false; _outDirty = true; }

                // Re-check the unpair gate now that the handles are published: a
                // SuppressAndRelease that fired between the loop-top check and the
                // opens above would otherwise attach a ghost joystick mid-unpair.
                if (_suppressReconnect) { Teardown(); Thread.Sleep(250); continue; }

                string tag = _transport == Ds3Transport.Usb ? "USB" : "BT";
                _log($"DS3({tag}): device opened, kicking + attaching virtual joystick...");
                if (!AttachVirtual()) { Teardown(); Thread.Sleep(1000); continue; }

                _writerRun = true;
                _writeThread = new Thread(WriterLoop) { IsBackground = true, Name = "Ds3DirectWrite" };
                _writeThread.Start();

                _log($"DS3({tag}): virtual joystick attached; streaming.");
                if (_transport == Ds3Transport.Usb) UsbReadLoop();
                else ReadLoop(_readPdo);   // blocks until the pad disconnects or Stop()

                Teardown();
                _log($"DS3({tag}): disconnected; watching for reconnect.");
            }
        }

        /// <summary>Open the Bluetooth BthPS3 raw PDO if a wireless DS3 is connected.</summary>
        private bool OpenBluetooth()
        {
            string path = FindPdoPath();
            if (path == null) return false;

            IntPtr rh = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (rh == INVALID_HANDLE) return false;
            IntPtr wh = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (wh == INVALID_HANDLE) { CloseHandle(rh); return false; }

            lock (_outLock) { _readPdo = rh; _writePdo = wh; }
            _transportPath = path;
            _transport = Ds3Transport.Bluetooth;
            return true;
        }

        /// <summary>Open the WinUSB-bound DS3 if one is on USB. Binds WinUSB first if a
        /// raw USB DS3 is present but unbound (throttled), so a plug-in works without the
        /// user having gone through the pairing ceremony.</summary>
        private bool OpenUsb()
        {
            string path = FindWinUsbDs3();
            if (path == null)
            {
                // Not WinUSB-bound. Bind it only if a USB DS3 is plugged in AND still on
                // the inbox driver (no working function driver). If DsHidMini or anything
                // else owns it, defer: don't fight over the device. Throttled so a repeated
                // bind failure doesn't spin pnputil.
                long now = Environment.TickCount64;
                if (now - _lastUsbBindAttempt >= 15000
                    && PadForge.Services.Ds3DriverInstaller.IsUsbDs3NeedingWinUsb(m => _log("DS3(USB): " + m)))
                {
                    _lastUsbBindAttempt = now;
                    _log("DS3(USB): unclaimed DS3 on USB, binding WinUSB...");
                    try { PadForge.Services.Ds3DriverInstaller.EnsureWinUsbBound(_log, default); } catch { }
                    path = FindWinUsbDs3();
                }
                if (path == null) return false;
            }

            IntPtr dev = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (dev == INVALID_HANDLE) return false;
            if (!WinUsb_Initialize(dev, out IntPtr ifh)) { CloseHandle(dev); return false; }

            // Find the interrupt-IN endpoint (the input-report pipe).
            byte inPipe = 0;
            if (WinUsb_QueryInterfaceSettings(ifh, 0, out var idesc))
            {
                for (byte i = 0; i < idesc.bNumEndpoints; i++)
                {
                    if (WinUsb_QueryPipe(ifh, 0, i, out var pipe)
                        && (pipe.PipeId & 0x80) != 0 && pipe.PipeType == 3 /*interrupt*/)
                    { inPipe = pipe.PipeId; break; }
                }
            }
            if (inPipe == 0) { WinUsb_Free(ifh); CloseHandle(dev); return false; }

            // A short read timeout so UsbReadLoop wakes to re-check _running/_writerRun
            // (WinUsb_ReadPipe otherwise blocks until a report arrives).
            uint timeout = 100;
            WinUsb_SetPipePolicy(ifh, inPipe, PIPE_TRANSFER_TIMEOUT, 4, ref timeout);

            lock (_outLock) { _usbDev = dev; _usbIfh = ifh; _usbInPipe = inPipe; }
            _transportPath = path;
            _transport = Ds3Transport.Usb;
            return true;
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

            while (_running && _writerRun)
            {
                _writeSignal.WaitOne(50);
                if (!_running || !_writerRun) break;

                long now = Environment.TickCount64;

                // Re-kick while silent (DsHidMini re-sends the enable after 1 s of no input).
                if (!_everGotInput && kicks < 5 && now - lastKick >= 1000)
                {
                    _log($"DS3({(_transport == Ds3Transport.Usb ? "USB" : "BT")}): no input yet - re-kick #{kicks + 1}");
                    Kick(); kicks++; lastKick = now;
                    continue;
                }

                // The five kicks ran and the pad never spoke. Detach so the
                // monitor can rebuild the whole transport instead of
                // sitting attached-but-silent forever: WinUsb_ReadPipe
                // answers ERROR_SEM_TIMEOUT indefinitely on a pad that
                // never enabled, which is not an error path, so nothing
                // else in this object ever reconsiders. Reported
                // 2026-08-08 (discussion #285) as "wired stops working
                // after a pairing attempt until PadForge is restarted":
                // the ceremony's cycle re-opens the device, the F4 enable
                // lands on a pad still settling, and the lane wedges with
                // a virtual joystick attached and no input behind it.
                if (!_everGotInput && kicks >= 5 && now - lastKick >= 3000)
                {
                    _log($"DS3({(_transport == Ds3Transport.Usb ? "USB" : "BT")}): no input after {kicks} kicks - detaching for a clean re-open");
                    // Cancel the read, NOT the service. The read loop then
                    // returns, the monitor tears down and re-opens on its
                    // next pass, and the enable is retried on a fresh
                    // handle. Setting _running here would kill DS3 support
                    // for the whole session.
                    CancelCurrentRead();
                    break;
                }

                bool doWrite;
                lock (_outLock)
                {
                    // Keepalive: resend the current state every RUMBLE_KEEPALIVE_MS while
                    // streaming, even with nothing changed, so held rumble is refreshed
                    // before the DS3 stops its motors (~1 s after the last output report).
                    // PadForge change-detects rumble at the SDL layer, so a sustained
                    // rumble fires OnRumble once and _outDirty then clears; without this
                    // the motor dies at ~1 s. Mirrors ScpToolkit's 500 ms full-report
                    // resend (BthDs3.cs:158-164). Unconditional-while-streaming also
                    // self-heals a dropped OFF (the next resend re-sends rumble=0). The
                    // 150 ms floor still gates every write, so no BT flooding.
                    bool keepaliveDue = _everGotInput && now - lastWrite >= RUMBLE_KEEPALIVE_MS;
                    doWrite = (_outDirty || keepaliveDue) && now - lastWrite >= OUTPUT_MIN_INTERVAL_MS;
                }
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
            lock (_ioLock)
            {
                if (_transport == Ds3Transport.Usb)
                {
                    // USB enable: SET_REPORT(FEATURE, 0xF4) {42,0C,00,00} flips the DS3
                    // operational and starts the interrupt-IN stream (ScpToolkit
                    // UsbDs3.cs Start(); proven prototype ds3winusb). Note 0x0C (USB),
                    // not the 0x03 the BT enable uses.
                    IntPtr ifh; lock (_outLock) ifh = _usbIfh;
                    if (ifh != IntPtr.Zero)
                        UsbSetReport(ifh, 0x03, 0xF4, new byte[] { 0x42, 0x0C, 0x00, 0x00 });
                }
                else
                {
                    // BT enable: 0x53 (SET_REPORT | FEATURE, report id 0xF4)
                    // then F4 42 03 00 00. In the Bluetooth HID transport
                    // header SET_REPORT is 0x50 and the low nibble is the
                    // report type, 1 input / 2 output / 3 feature, so the 3
                    // here is FEATURE. The bytes were always right; the
                    // comment named the wrong type.
                    byte[] en = { 0x53, 0xF4, 0x42, 0x03, 0x00, 0x00 };
                    IntPtr h; lock (_outLock) h = _writePdo;
                    if (h != IntPtr.Zero && h != INVALID_HANDLE)
                        DeviceIoControl(h, IOCTL_HID_CONTROL_WRITE, en, en.Length, null, 0, out _, IntPtr.Zero);
                }
            }
        }

        // 50-byte DS3 Bluetooth output report (DsHidMini G_Ds3BthHidOutputReport):
        // [0]=0x52 (SET_REPORT|OUTPUT), [1]=0x01 report id, then the raw 48-byte output
        // payload, so raw offset N lands at [N+2]: smallDur raw[1]->[3], smallOn
        // raw[2]->[4], largeDur raw[3]->[5], largeForce raw[4]->[6], LED raw[9]->[11].
        // Rumble durations are 0xFE, not the 0xFF of the raw template: DsHidMini
        // overrides both to 0xFE at BT startup (DsBth.Timers.c:50-51) and USB Host
        // Shield's setRumbleOn uses 0xFE, the value that holds the motor until the
        // next OFF. 0xFF with no periodic resend is what let the motor time out.
        private void WriteOutputReport()
        {
            byte[] o = {
                0x52,0x01, 0x00,0xFE,0x00,0xFE,0x00, 0x00,0x00,0x00,0x00,0x00,
                0xFF,0x27,0x10,0x00,0x32, 0xFF,0x27,0x10,0x00,0x32, 0xFF,0x27,0x10,0x00,0x32, 0xFF,0x27,0x10,0x00,0x32,
                0x00,0x00,0x00,0x00,0x00, 0x00,0x00,0x00,0x00,0x00, 0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00 };
            lock (_ioLock)
            {
                IntPtr h, ifh;
                Ds3Transport tr = _transport;
                lock (_outLock)
                {
                    o[4] = _rumbleSmall > 0 ? (byte)0x01 : (byte)0x00; // small motor on/off
                    o[6] = _rumbleLarge;                               // large motor strength
                    o[11] = _ledMask;                                  // player LED bitmask
                    _outDirty = false;
                    h = _writePdo;
                    ifh = _usbIfh;
                }
                if (tr == Ds3Transport.Usb)
                {
                    // USB output: SET_REPORT(OUTPUT, 0x01) with just the 48-byte payload
                    // (o[2..], dropping the BT 0x52/0x01 framing). Same rumble/LED bytes.
                    if (ifh != IntPtr.Zero)
                        UsbSetReport(ifh, 0x02, 0x01, o.AsSpan(2).ToArray());
                }
                else if (h != IntPtr.Zero && h != INVALID_HANDLE)
                {
                    DeviceIoControl(h, IOCTL_HID_CONTROL_WRITE, o, o.Length, null, 0, out _, IntPtr.Zero);
                }
            }
        }

        /// <summary>WinUSB HID SET_REPORT class control transfer (reportType 0x02 OUTPUT
        /// / 0x03 FEATURE). Caller holds _ioLock.</summary>
        private static bool UsbSetReport(IntPtr ifh, byte reportType, byte reportId, byte[] data)
        {
            var s = new WINUSB_SETUP_PACKET
            {
                RequestType = 0x21,                                 // Host->Device | Class | Interface
                Request = 0x09,                                     // HID SET_REPORT
                Value = (ushort)((reportType << 8) | reportId),
                Index = 0,
                Length = (ushort)data.Length,
            };
            return WinUsb_ControlTransfer(ifh, s, data, (uint)data.Length, out _, IntPtr.Zero);
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
                    // Full 50-byte frames only (BthPS3 completes reads at exactly the
                    // report size; a shorter completion would leave stale bytes from
                    // the previous frame in the reused buffer past rd). raw[1]=0xFF is
                    // the DS3's invalid/wake frame; both references drop it
                    // (SDL_hidapi_ps3.c:566 data[1]==0xFF, ScpToolkit BthDs3.cs:42).
                    if (rd >= DS3_BT_INPUT_REPORT_SIZE && buf[0] == 0xA1 && buf[1] == 0x01 && buf[2] != 0xFF)
                    {
                        _everGotInput = true;
                        PushState(buf, rd);
                        UpdateBattery(buf[31]);   // raw[30] BatteryStatus
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

        /// <summary>USB read loop: WinUsb_ReadPipe on the interrupt-IN endpoint. The 49-byte
        /// USB report is the raw DS3 report; prepend the 0xA1 byte Bluetooth carries so the
        /// shared PushState parser (BT offsets) handles it verbatim.</summary>
        private void UsbReadLoop()
        {
            byte[] buf = new byte[DS3_BT_INPUT_REPORT_SIZE];   // normalized (0xA1 + raw)
            byte[] raw = new byte[64];
            buf[0] = 0xA1;
            long lastProbe = 0;
            while (_running && _transport == Ds3Transport.Usb)
            {
                IntPtr ifh; lock (_outLock) ifh = _usbIfh;
                if (ifh == IntPtr.Zero) break;

                if (WinUsb_ReadPipe(ifh, _usbInPipe, raw, (uint)raw.Length, out uint got, IntPtr.Zero))
                {
                    // Standard DS3 report id 0x01. Shift raw[0..] to buf[1..] (buf[0]=0xA1),
                    // giving the same layout the BT parser expects: report id at buf[1],
                    // buttons at buf[3], sticks buf[7..10], pressures/motion at BT offsets.
                    if (got >= 10 && raw[0] == 0x01)
                    {
                        int n = (int)Math.Min(got, (uint)(buf.Length - 1));
                        Array.Copy(raw, 0, buf, 1, n);
                        int rd = n + 1;
                        if (rd >= DS3_BT_INPUT_REPORT_SIZE && buf[2] != 0xFF)
                        {
                            _everGotInput = true;
                            PushState(buf, rd);
                            UpdateBattery(buf[31]);
                        }
                    }
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_SEM_TIMEOUT) continue;   // pipe timeout, no report yet
                    if (err == ERROR_DEVICE_NOT_CONNECTED || err == ERROR_FILE_NOT_FOUND ||
                        err == ERROR_INVALID_HANDLE || err == ERROR_OPERATION_ABORTED ||
                        err == ERROR_GEN_FAILURE || err == ERROR_NO_SUCH_DEVICE)
                        break;
                    Thread.Sleep(2);
                    long now = Environment.TickCount64;
                    if (now - lastProbe >= 1000)
                    {
                        lastProbe = now;
                        if (FindWinUsbDs3() == null) break;
                    }
                }
            }
        }

        // ─── SDL virtual joystick ───────────────────────────────────────────────

        private bool AttachVirtual()
        {
            // Standard gamepad shape so SDL treats it as a gamepad and PadForge auto-maps.
            _rumbleCb = OnRumble; _setLedCb = OnSetLed; _setPlayerCb = OnSetPlayerIndex; _setSensorsCb = OnSetSensors;

            // Transport-independent name: SDL folds the name into the joystick GUID, and
            // PadForge derives the device identity from that GUID (the virtual joystick
            // has no path/serial). A per-transport name would give USB and Bluetooth
            // different identities, so a slot mapping made on one wouldn't survive a
            // switch to the other. One name = one identity for the physical pad.
            var namePtr = Marshal.StringToHGlobalAnsi("DualShock 3");
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
                if (_instanceId == 0)
                {
                    _log($"DS3({(_transport == Ds3Transport.Usb ? "USB" : "BT")}): SDL_AttachVirtualJoystick failed.");
                    return false;
                }
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
            // Serialized + idempotent: Stop() and MonitorLoop can both arrive here
            // (e.g. a wedged read thread outliving Stop's join), and the SDL
            // close/detach pair must never run twice for the same handles.
            lock (_teardownLock)
            {
                _writerRun = false;
                _writeSignal.Set();
                // Unblock a writer/reader stuck inside I/O so the joins can succeed.
                lock (_outLock)
                {
                    if (_writePdo != IntPtr.Zero && _writePdo != INVALID_HANDLE) CancelIoEx(_writePdo, IntPtr.Zero);
                    if (_usbDev != IntPtr.Zero && _usbDev != INVALID_HANDLE) CancelIoEx(_usbDev, IntPtr.Zero);
                }
                try { if (_writeThread != null && _writeThread != Thread.CurrentThread) _writeThread.Join(1000); } catch { }
                _writeThread = null;

                if (_instanceId != 0) { PowerByInstance.TryRemove(_instanceId, out _); _lastBattery = 0xFF; }
                if (_sdlJoystick != IntPtr.Zero) { SDL.SDL_CloseJoystick(_sdlJoystick); _sdlJoystick = IntPtr.Zero; }
                if (_instanceId != 0) { SDL.SDL_DetachVirtualJoystick(_instanceId); _instanceId = 0; }

                // _ioLock: no write can be in flight while the handles close, so a late
                // writer can never hit a closed/recycled handle value.
                lock (_ioLock)
                lock (_outLock)
                {
                    if (_readPdo != IntPtr.Zero && _readPdo != INVALID_HANDLE) CloseHandle(_readPdo);
                    if (_writePdo != IntPtr.Zero && _writePdo != INVALID_HANDLE) CloseHandle(_writePdo);
                    _readPdo = IntPtr.Zero;
                    _writePdo = IntPtr.Zero;
                    if (_usbIfh != IntPtr.Zero) { WinUsb_Free(_usbIfh); _usbIfh = IntPtr.Zero; }
                    if (_usbDev != IntPtr.Zero && _usbDev != INVALID_HANDLE) CloseHandle(_usbDev);
                    _usbDev = IntPtr.Zero;
                    _usbInPipe = 0;
                }
                _transportPath = null;
                _transport = Ds3Transport.None;
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

        /// <summary>0..255 stick byte to the full SDL axis range with 0x80 = exactly 0.
        /// SDL's own PS3 driver uses v*257-32768 (center = +128); keeping a true zero
        /// center matters for downstream deadzones, so scale each half to its full
        /// extent instead: 128..255 -> 0..32767, 0..128 -> -32768..0.</summary>
        private static short AxisFromByte(byte v) =>
            v >= 128 ? (short)((v - 128) * 32767 / 127) : (short)((v - 128) * 32768 / 128);

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
            // Same convention as SDL's PS3 driver: 0x01 << (1 + index % 4).
            SetPlayerNumber(playerIndex < 0 ? 0 : playerIndex + 1);
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

        // WinUSB interface GUID from the shipped ds3_winusb.inf (the USB DS3 binding).
        private static readonly Guid DS3_WINUSB_IF = new Guid("B35924D6-3E16-4A9E-9782-5524A4B79BAC");

        private string FindWinUsbDs3() => FindInterfacePath(DS3_WINUSB_IF, requireVid054c: false);

        // Generalized SetupDi interface-path lookup (the BthPS3 PDO variant filters on
        // the 054c substring; the WinUSB interface GUID is DS3-specific already).
        private string FindInterfacePath(Guid ifGuid, bool requireVid054c)
        {
            IntPtr set = SetupDiGetClassDevs(ref ifGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == INVALID_HANDLE) return null;
            var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            try
            {
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref ifGuid, i, ref did); i++)
                {
                    // ACTIVE only (SPINT_ACTIVE): registrations persist in
                    // the registry after the driver changes, and a stale
                    // path here short-circuited the auto-bind (path came
                    // back non-null, so the rebind never ran) while the
                    // live pad sat on HidUsb with no input (#285).
                    if ((did.Flags & SPINT_ACTIVE) == 0) continue;
                    int req = 0;
                    SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, ref req, IntPtr.Zero);
                    IntPtr det = Marshal.AllocHGlobal(req);
                    try
                    {
                        Marshal.WriteInt32(det, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref did, det, req, ref req, IntPtr.Zero))
                        {
                            string p = Marshal.PtrToStringUni(det + 4);
                            if (p != null && (!requireVid054c || p.IndexOf("054c", StringComparison.OrdinalIgnoreCase) >= 0))
                                return p;
                        }
                    }
                    finally { Marshal.FreeHGlobal(det); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return null;
        }

        private const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10, SPINT_ACTIVE = 0x1;
        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const int ERROR_FILE_NOT_FOUND = 2, ERROR_INVALID_HANDLE = 6,
                          ERROR_GEN_FAILURE = 31, ERROR_SEM_TIMEOUT = 121,
                          ERROR_NO_SUCH_DEVICE = 433,
                          ERROR_OPERATION_ABORTED = 995, ERROR_DEVICE_NOT_CONNECTED = 1167;
        private static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);

        // ── WinUSB interop (USB DS3, inbox winusb.sys) ──────────────────────────
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WINUSB_SETUP_PACKET { public byte RequestType; public byte Request; public ushort Value; public ushort Index; public ushort Length; }
        [StructLayout(LayoutKind.Sequential)]
        private struct USB_INTERFACE_DESCRIPTOR { public byte bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints, bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WINUSB_PIPE_INFORMATION { public int PipeType; public byte PipeId; public ushort MaximumPacketSize; public byte Interval; }
        private const uint PIPE_TRANSFER_TIMEOUT = 0x03;

        [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_Initialize(IntPtr dev, out IntPtr ifh);
        [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_Free(IntPtr ifh);
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ControlTransfer(IntPtr ifh, WINUSB_SETUP_PACKET setup, byte[] buf, uint len, out uint transferred, IntPtr overlapped);
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ReadPipe(IntPtr ifh, byte pipeId, byte[] buf, uint len, out uint transferred, IntPtr overlapped);
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_QueryInterfaceSettings(IntPtr ifh, byte alt, out USB_INTERFACE_DESCRIPTOR desc);
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_QueryPipe(IntPtr ifh, byte alt, byte pipeIndex, out WINUSB_PIPE_INFORMATION pipe);
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_SetPipePolicy(IntPtr ifh, byte pipeId, uint policyType, uint valueLen, ref uint value);

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
