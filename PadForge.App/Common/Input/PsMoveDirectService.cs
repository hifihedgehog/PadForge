using System;
using System.Runtime.InteropServices;
using System.Threading;
using SDL3;

namespace PadForge.Common.Input
{
    /// <summary>Per-pad PS Move calibration blobs (#277), keyed by pad MAC
    /// (lowercase hex, no separators). The pairing ceremony stores the blob it
    /// reads over USB; the Bluetooth lane scales its sensors from it
    /// (psmoveapi's read-over-USB-cache-for-BT architecture,
    /// psmove_calibration.c). Persisted in settings as "mac=hex" entries.</summary>
    public static class PsMoveCalibrationRegistry
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _blobs = new();

        public static void LoadRegistry(System.Collections.Generic.IEnumerable<string> entries)
        {
            _blobs.Clear();
            if (entries != null)
            {
                foreach (string e in entries)
                {
                    int eq = e?.IndexOf('=') ?? -1;
                    if (eq <= 0 || eq >= e.Length - 1) continue;
                    try { _blobs[e.Substring(0, eq).ToLowerInvariant()] = Convert.FromHexString(e.Substring(eq + 1)); }
                    catch { /* malformed entry: skip */ }
                }
            }
            PsMoveDirectService.CalibrationProvider = Get;
        }

        public static string[] SaveRegistry()
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (var kv in _blobs)
                list.Add(kv.Key + "=" + Convert.ToHexString(kv.Value));
            list.Sort(StringComparer.Ordinal);
            return list.ToArray();
        }

        public static void Store(string mac, byte[] blob)
        {
            if (string.IsNullOrEmpty(mac) || blob == null || blob.Length == 0) return;
            _blobs[mac.ToLowerInvariant()] = blob;
            PsMoveDirectService.CalibrationProvider = Get;
        }

        /// <summary>Blob for the given MAC. Only when NO MAC is known (the PDO
        /// path carried no serial) does a machine with exactly ONE stored pad
        /// fall back to it; a known-but-unstored MAC returns null, because
        /// applying another pad's per-unit calibration silently corrupts the
        /// motion scaling.</summary>
        public static byte[] Get(string mac)
        {
            if (!string.IsNullOrEmpty(mac))
                return _blobs.TryGetValue(mac.ToLowerInvariant(), out var b) ? b : null;
            if (_blobs.Count == 1)
                foreach (var kv in _blobs) return kv.Value;
            return null;
        }
    }

    /// <summary>
    /// Surfaces a PlayStation Move motion controller connected over Bluetooth through
    /// the BthPS3 profile driver (raw PDO, MOTION device category) as an SDL virtual
    /// joystick, the same lane shape as <see cref="Ds3DirectService"/> (#277).
    ///
    /// Protocol, each point traced to a cloned reference:
    ///  - BthPS3 exposes the Move on its own device-interface GUID
    ///    (GUID_DEVINTERFACE_BTHPS3_MOTION, BthPS3.h:206) with the same raw-PDO
    ///    IOCTL surface the DS3 lane uses (BthPS3.h:375-390).
    ///  - Input report 0x01 streams WITHOUT any enable kick: hid-sony's
    ///    MOTION_CONTROLLER probe branch (hid-sony.c:2276) registers only the output
    ///    report and never calls sixaxis_set_operational_bt, unlike its NAVIGATION
    ///    and SIXAXIS branches (hid-sony.c:2189-2200, 2241-2252).
    ///  - The BT input frame is 0xA1 + the 49-byte ZCM1 report (hid-sony.c:1170
    ///    accepts rd[0]==0x01 &amp;&amp; size==49 for MOTION_CONTROLLER_BT; the struct is
    ///    PSMove_ZCM1_Data_Input, psmove.c:165-216). The PS4-era ZCM2 model sends a
    ///    44-byte report instead (PSMove_ZCM2_Data_Input, psmove.c:218-226), so its
    ///    BT frame is 45 bytes. Reads must be EXACTLY the frame size (BthPS3 pends
    ///    interrupt reads without short-transfer-OK, same constraint the DS3 lane
    ///    documents), so the reader starts at the ZCM1 size and drops to the ZCM2
    ///    size when the stream proves misaligned.
    ///  - EVERY ZCM1 report carries TWO sensor frames (trigger/trigger2,
    ///    aX..aZ twice, gX..gZ twice); both are decoded and sent so the motion rate
    ///    is not silently halved (psmove.c:165-205 and the #277 plan's trap note).
    ///    ZCM2 carries only one valid frame (psmove.c:1813 "Only one frame on the
    ///    ZCM2").
    ///  - Output: LED sphere + rumble share one report, id 0x02, 49 bytes,
    ///    [type, zero, r, g, b, zero2, rumble], sent on the INTERRUPT channel
    ///    (hid-sony.c motion_send_output_report + MOTION_REPORT_0x02_SIZE, sent via
    ///    hid_hw_output_report which is the interrupt DATA path; framed 0xA2 for the
    ///    raw channel). It must be refreshed periodically or the sphere times out
    ///    (psmove.c:75 PSMOVE_MAX_LED_INHIBIT_MS 4000).
    ///  - Sensor scaling comes from the per-unit calibration blob read over USB at
    ///    pair time (psmoveapi's own architecture: psmove_calibration.c reads it over
    ///    USB and caches it; there are no nominal fallback constants in any cloned
    ///    reference, and hid-sony exposes raw counts). Without a stored blob the
    ///    sensors stay muted and the log names the fix (pair over USB once).
    /// </summary>
    public sealed class PsMoveDirectService
    {
        // GUID_DEVINTERFACE_BTHPS3_MOTION {BCEC605D-233C-4BEF-9A10-F2B81B5297F6} (BthPS3.h:206-208).
        private static readonly Guid MotionInterface =
            new Guid(0xbcec605d, 0x233c, 0x4bef, 0x9a, 0x10, 0xf2, 0xb8, 0x1b, 0x52, 0x97, 0xf6);

        // Raw-PDO IOCTLs (BthPS3.h:375-390; CTL_CODE with FILE_DEVICE_BUS_EXTENDER).
        private const uint IOCTL_HID_INTERRUPT_READ  = 0x2A680C;
        private const uint IOCTL_HID_INTERRUPT_WRITE = 0x2AA810;

        private const ushort MOVE_VID = 0x054C;
        private const ushort MOVE_PID = 0x03D5;   // BTHPS3_MOTION_PID (BthPS3.h:51); the BT PDO advertises this for both models

        /// <summary>0xA1 + 49-byte ZCM1 report (psmove.c PSMove_ZCM1_Data_Input; hid-sony.c:1170).</summary>
        internal const int Zcm1BtReportSize = 50;
        /// <summary>0xA1 + 44-byte ZCM2 report (psmove.c PSMove_ZCM2_Data_Input).</summary>
        internal const int Zcm2BtReportSize = 45;

        // Output pacing: psmoveapi rate-limits LED updates to one per 120 ms
        // (PSMOVE_MIN_LED_UPDATE_WAIT_MS, psmove.c:78) and refreshes unchanged
        // LEDs every 4000 ms (PSMOVE_MAX_LED_INHIBIT_MS, psmove.c:75) because the
        // sphere times out without refresh. 2000 ms keeps well inside that
        // timeout and also serves as the rumble keepalive.
        private const int OUTPUT_MIN_INTERVAL_MS = 120;
        private const int OUTPUT_KEEPALIVE_MS = 2000;

        private readonly Action<string> _log;
        private Thread _readThread;
        private Thread _writeThread;
        private volatile bool _running;

        private uint _instanceId;
        private IntPtr _sdlJoystick = IntPtr.Zero;

        private SDL.VJRumble _rumbleCb;
        private SDL.VJSetLED _setLedCb;
        private SDL.VJSetPlayerIndex _setPlayerCb;
        private SDL.VJSetSensorsEnabled _setSensorsCb;

        // Which transport the current session streams over. USB input is real
        // on the Move: psmove_poll reads input transport-agnostically
        // (psmove.c:1401-1423) and hid-sony registers a full input device for
        // MOTION_CONTROLLER_USB, so a docked Move plays, not just charges.
        private enum MoveTransport { None, Bluetooth, Usb }
        private volatile MoveTransport _transport = MoveTransport.None;
        private string Tag => _transport == MoveTransport.Usb ? "MOVE(USB)" : "MOVE(BT)";

        // One read handle for the pended interrupt read, one write handle for the
        // writer thread (separate file objects do not serialize against each other).
        private IntPtr _readPdo = IntPtr.Zero;
        private IntPtr _writePdo = IntPtr.Zero;

        // USB (inbox HID, the transport psmoveapi itself uses): the col01 data
        // collection carries input/output, col02 answers the address reports
        // (psmove.c's _WIN32 quirk). Report lengths come from the collection's
        // HID caps: reads must use InputReportByteLength and writes must pad to
        // OutputReportByteLength (the HID class contract).
        private IntPtr _usbHandle = IntPtr.Zero;
        private int _usbInLen, _usbOutLen;
        private volatile string _transportPath;

        private volatile bool _writerRun;
        private int _writerGen;

        // Same lock discipline as the DS3 lane: _ioLock across every write AND the
        // write handle's close; _outLock guards writer state and handle fields; SDL
        // callbacks only store state under _outLock and signal. Order: _ioLock
        // outer, _outLock inner.
        private readonly object _ioLock = new object();
        private readonly object _teardownLock = new object();
        private readonly object _outLock = new object();
        private readonly AutoResetEvent _writeSignal = new AutoResetEvent(false);

        // Sphere color + rumble (one report carries both). Default color is
        // applied per player index until an explicit LED write claims the sphere.
        private byte _r, _g, _b;
        private byte _rumble;
        private bool _ledExplicit;
        private bool _outDirty;
        private volatile bool _everGotInput;

        // Session model: true once the stream proves ZCM2-sized frames. Sticky
        // across reconnects for the process lifetime: one physical pad cannot
        // change model, and the sticky bit spares every reconnect the garbage
        // window the first detection pays. Known limit, deliberate: MIXING both
        // models in one app session relies on the misalignment detector firing
        // in the shrink direction too, and BthPS3's exact completion behavior
        // for a read buffer SMALLER than the incoming frame is unproven without
        // hardware. Two Moves of different generations on one machine is the
        // rare case; the detector logs every flip, so a wrong stick is visible.
        private static volatile bool _modelZcm2;

        // ── unpair coordination (same contract as Ds3DirectService) ─────────────
        private static int _suppressDepth;
        private static bool _suppressReconnect => Volatile.Read(ref _suppressDepth) > 0;
        private static volatile PsMoveDirectService _current;

        /// <summary>Per-pad calibration blob lookup, keyed by the pad MAC in
        /// lowercase hex (no separators), wired by the App layer from settings.
        /// Returns null when no blob is stored. The ceremony stores the blob it
        /// reads over USB (psmove_calibration.c architecture).</summary>
        public static Func<string, byte[]> CalibrationProvider;

        public static void SuppressAndRelease()
        {
            Interlocked.Increment(ref _suppressDepth);
            _current?.CancelCurrentRead();
        }

        public static void AllowReconnect()
        {
            while (true)
            {
                int cur = Volatile.Read(ref _suppressDepth);
                if (cur <= 0) return;
                if (Interlocked.CompareExchange(ref _suppressDepth, cur - 1, cur) == cur) return;
            }
        }

        internal static void ResetSuppressionForTest()
            => Interlocked.Exchange(ref _suppressDepth, 0);

        private void CancelCurrentRead()
        {
            lock (_outLock)
            {
                if (_readPdo != IntPtr.Zero && _readPdo != INVALID_HANDLE) CancelIoEx(_readPdo, IntPtr.Zero);
                if (_usbHandle != IntPtr.Zero && _usbHandle != INVALID_HANDLE) CancelIoEx(_usbHandle, IntPtr.Zero);
            }
        }

        public PsMoveDirectService(Action<string> log = null) => _log = log ?? (_ => { });

        public bool IsConnected => _sdlJoystick != IntPtr.Zero;
        public uint InstanceId => _instanceId;

        // Battery reaches the #167 lane through the same external provider the DS3
        // uses. Semantics per hid-sony sixaxis_parse_report (battery at raw offset
        // 12 for MOTION, hid-sony.c:964): 0xEE charging, 0xEF full, 0x01..0x05
        // discharge quintiles; psmove.c:178 agrees (0x05 = max, 0xEE = charging).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (int Percent, bool Charging)>
            PowerByInstance = new();

        public static (int Percent, bool Charging)? GetPowerInfo(uint instanceId)
            => PowerByInstance.TryGetValue(instanceId, out var p) ? p : null;

        public static string GetDevicePath(uint sdlInstanceId)
        {
            var svc = _current;
            return (svc != null && svc.IsConnected && svc.InstanceId == sdlInstanceId)
                ? svc._transportPath : null;
        }

        /// <summary>Default sphere colors by 1-based player number, matching SDL's
        /// PS4/PS5 player lightbar palette (blue, red, green, pink). Applied only
        /// until an explicit SetLED claims the sphere.</summary>
        internal static (byte R, byte G, byte B) DefaultSphereColor(int oneBasedNumber)
        {
            int idx = oneBasedNumber <= 0 ? 0 : (oneBasedNumber - 1) % 4;
            return idx switch
            {
                0 => (0x00, 0x00, 0x40),
                1 => (0x40, 0x00, 0x00),
                2 => (0x00, 0x40, 0x00),
                _ => (0x20, 0x00, 0x20),
            };
        }

        public static bool TrySetPlayerNumber(uint sdlInstanceId, int oneBasedNumber)
        {
            var svc = _current;
            if (svc == null || !svc.IsConnected || svc.InstanceId != sdlInstanceId) return false;
            svc.SetPlayerNumber(oneBasedNumber);
            return true;
        }

        public void SetPlayerNumber(int oneBasedNumber)
        {
            var (r, g, b) = DefaultSphereColor(oneBasedNumber);
            bool changed = false;
            lock (_outLock)
            {
                if (!_ledExplicit && (_r != r || _g != g || _b != b))
                {
                    _r = r; _g = g; _b = b; _outDirty = true; changed = true;
                }
            }
            if (changed) _writeSignal.Set();
        }

        private byte _lastBattery = 0xFF;

        private void UpdateBattery(byte status)
        {
            if (status == 0x00) return;
            if (status == _lastBattery || _instanceId == 0) return;
            _lastBattery = status;
            (int Percent, bool Charging) p = status switch
            {
                0xEF => (100, true),
                0xEE => (-1, true),
                _ => (Math.Min((int)status, 5) * 20, false)
            };
            PowerByInstance[_instanceId] = p;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _current = this;
            _readThread = new Thread(MonitorLoop) { IsBackground = true, Name = "PsMoveDirectRead" };
            _readThread.Start();
        }

        public void Stop()
        {
            _running = false;
            if (_current == this) _current = null;
            _writeSignal.Set();
            lock (_outLock)
            {
                if (_readPdo != IntPtr.Zero && _readPdo != INVALID_HANDLE) CancelIoEx(_readPdo, IntPtr.Zero);
                if (_usbHandle != IntPtr.Zero && _usbHandle != INVALID_HANDLE) CancelIoEx(_usbHandle, IntPtr.Zero);
            }
            try { _readThread?.Join(1500); } catch { }
            Teardown();
        }

        // Flap discipline copied from the DS3 lane: the BthPS3 PDO lingers after
        // the pad leaves, so the open keeps succeeding while the read returns at
        // once, and with no floor that is a hot attach/teardown spin.
        private const int MinHealthySessionMs = 400;
        private const int FlapBackoffFirstMs = 125;
        private const int FlapBackoffMaxMs = 2000;

        private void MonitorLoop()
        {
            int flapBackoffMs = 0;
            while (_running)
            {
                if (_suppressReconnect) { Teardown(); Thread.Sleep(250); continue; }

                if (!OpenUsb() && !OpenBluetooth()) { Thread.Sleep(500); continue; }

                lock (_outLock) { _everGotInput = false; _outDirty = true; }
                _outWriteFailLogged = false;

                if (_suppressReconnect) { Teardown(); Thread.Sleep(250); continue; }

                _log($"{Tag}: device opened, attaching virtual joystick...");
                if (!AttachVirtual()) { Teardown(); Thread.Sleep(1000); continue; }

                _writerRun = true;
                int writerGen = Interlocked.Increment(ref _writerGen);
                _writeThread = new Thread(() => WriterLoop(writerGen)) { IsBackground = true, Name = "PsMoveDirectWrite" };
                _writeThread.Start();

                _log($"{Tag}: virtual joystick attached; streaming ({(_modelZcm2 ? "ZCM2" : "ZCM1")} frames).");
                if (_transport == MoveTransport.Bluetooth) LoadCalibrationForPath();
                long sessionStart = Environment.TickCount64;
                if (_transport == MoveTransport.Usb) UsbReadLoop();
                else ReadLoop(_readPdo);

                Teardown();
                long sessionMs = Environment.TickCount64 - sessionStart;
                _log($"{Tag}: disconnected after {sessionMs} ms; watching for reconnect.");

                if (!_running) break;

                if (sessionMs < MinHealthySessionMs)
                {
                    flapBackoffMs = flapBackoffMs == 0 ? FlapBackoffFirstMs
                                  : flapBackoffMs >= FlapBackoffMaxMs / 2 ? FlapBackoffMaxMs
                                  : flapBackoffMs * 2;
                    for (int slept = 0; slept < flapBackoffMs && _running; slept += 100)
                        Thread.Sleep(Math.Min(100, flapBackoffMs - slept));
                }
                else flapBackoffMs = 0;
            }
        }

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
            _transport = MoveTransport.Bluetooth;
            return true;
        }

        // One dock-mode capture per plug-in: the path clears when the pad
        // leaves so the next dock re-captures.
        private string _lastDockedPath;

        /// <summary>Raised once per dock event with the pad's MAC (lowercase
        /// hex, null when the col02 address read failed) and the host address
        /// currently stored IN the pad (big-endian, null when unknown). The
        /// App layer wires this to the auto-pair decision: Sony's own console
        /// pairs a Move by cable plug-in, and requiring a dialog step instead
        /// left a bench pad docked, calibrated, and paired to nothing
        /// (2026-08-18: record 48f07bed1049 carried no PadForge identity).</summary>
        public static Action<string, byte[]> DockObserved;

        /// <summary>Wired by the App layer to an immediate settings save. The
        /// minted Devices row lives only in memory until a save lands, and the
        /// deploy loop's taskkill /F means "save on exit" never runs on the
        /// bench; the 2026-08-18 log showed a minted row (DEV +/-) that was
        /// absent from the next PadForge.xml write.</summary>
        public static Action PersistRequested;

        /// <summary>True when the Devices list already carries the Move's row
        /// (by the family VID/PID, online or offline).</summary>
        public static bool DeviceRowExists()
        {
            try
            {
                var devices = SettingsManager.UserDevices;
                if (devices == null) return false;
                lock (devices.SyncRoot)
                {
                    foreach (var d in devices.Items)
                        if (d != null && d.VendorId == MOVE_VID && d.ProdId == MOVE_PID) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>USB handling per model, per the moveonpc reference ("HID
        /// reports": input report 0x01 is BT-only on the ZCM1; the ZCM2 page
        /// carries no such restriction and psmoveapi polls it over USB):
        /// a docked ZCM1 is a CHARGING/PAIRING DOCK, so no virtual joystick is
        /// attached (the first build attached one that could never produce
        /// input, the dead pad the 2026-08-18 bench caught). The dock still
        /// captures the pad's calibration blob once per plug-in. A ZCM2
        /// (USB PID 0x0C5E, psmove_private.h:58) opens a real input session.</summary>
        private bool OpenUsb()
        {
            var found = FindUsbHid();
            if (found == null) { _lastDockedPath = null; return false; }

            if (!found.Value.Zcm2)
            {
                // ZCM1 dock: capture once per plug-in, never attach.
                if (_lastDockedPath != found.Value.DataPath)
                {
                    _lastDockedPath = found.Value.DataPath;
                    string mac = null; byte[] storedHost = null;
                    IntPtr dh = CreateFile(found.Value.DataPath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (dh != INVALID_HANDLE)
                    {
                        try { (mac, storedHost) = TryUsbCalibration(dh, found.Value.AddrPath, zcm2: false); }
                        finally { CloseHandle(dh); }
                    }
                    _log("MOVE(USB): PS Move docked. This model streams input over Bluetooth only "
                        + "(moveonpc: input report 0x01 is BT-only on the ZCM1); the dock charges "
                        + "the pad and captures its motion calibration.");
                    try { DockObserved?.Invoke(mac, storedHost); } catch { }
                }
                return false;
            }

            IntPtr h = CreateFile(found.Value.DataPath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID_HANDLE) return false;

            int inLen = 0, outLen = 0;
            if (HidD_GetPreparsedData(h, out IntPtr pp) && pp != IntPtr.Zero)
            {
                try
                {
                    if (HidP_GetCaps(pp, out HIDP_CAPS caps) >= 0)
                    {
                        inLen = caps.InputReportByteLength;
                        outLen = caps.OutputReportByteLength;
                    }
                }
                finally { HidD_FreePreparsedData(pp); }
            }
            if (inLen <= 1) { CloseHandle(h); return false; }

            _modelZcm2 = true;

            lock (_outLock) { _usbHandle = h; _usbInLen = inLen; _usbOutLen = outLen; }
            _transportPath = found.Value.DataPath;
            _transport = MoveTransport.Usb;

            TryUsbCalibration(h, found.Value.AddrPath, zcm2: true);
            return true;
        }

        /// <summary>Reads the pad MAC and its stored host address (feature 0x04
        /// on col02: controller little-endian at bytes 1-6, host at 10-15,
        /// psmove.c:953-964) and the calibration blob (feature 0x10 parts,
        /// psmove.c:973-1077) directly on this dock, so a Move paired by an
        /// external tool still gets calibrated motion the first time it is
        /// plugged in. Returns what it learned for the auto-pair decision.</summary>
        private (string Mac, byte[] StoredHostBigEndian) TryUsbCalibration(IntPtr dataHandle, string addrPath, bool zcm2)
        {
            string mac = null; byte[] storedHost = null;
            try
            {
                if (addrPath != null)
                {
                    IntPtr ah = CreateFile(addrPath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (ah != INVALID_HANDLE)
                    {
                        try
                        {
                            byte[] btg = new byte[16];
                            btg[0] = 0x04;
                            if (HidD_GetFeature(ah, btg, btg.Length))
                            {
                                var macBe = new byte[6];
                                for (int i = 0; i < 6; i++) macBe[i] = btg[6 - i];
                                mac = Convert.ToHexString(macBe).ToLowerInvariant();
                                storedHost = new byte[6];
                                for (int i = 0; i < 6; i++) storedHost[i] = btg[15 - i];
                            }
                        }
                        finally { CloseHandle(ah); }
                    }
                }

                byte[] blob = (mac != null ? PsMoveCalibrationRegistry.Get(mac) : null);
                if (blob == null)
                {
                    int parts = zcm2 ? 2 : 3;
                    blob = new byte[zcm2 ? 96 : 143];
                    var seen = new System.Collections.Generic.HashSet<int>();
                    for (int attempt = 0; attempt < parts * 3 && seen.Count < parts; attempt++)
                    {
                        byte[] cal = new byte[49];
                        cal[0] = 0x10;
                        if (!HidD_GetFeature(dataHandle, cal, cal.Length)) { blob = null; break; }
                        int destOffset, srcOffset;
                        switch (cal[1])
                        {
                            case 0x00: destOffset = 0; srcOffset = 0; break;
                            case 0x01 when !zcm2: destOffset = 49; srcOffset = 2; break;
                            case 0x82 when !zcm2: destOffset = 2 * 49 - 2; srcOffset = 2; break;
                            case 0x81 when zcm2: destOffset = 49; srcOffset = 2; break;
                            default: continue;
                        }
                        Array.Copy(cal, srcOffset, blob, destOffset, 49 - srcOffset);
                        seen.Add(cal[1]);
                    }
                    if (blob != null && seen.Count < parts) blob = null;
                    if (blob != null && mac != null)
                    {
                        PsMoveCalibrationRegistry.Store(mac, blob);
                        _log($"MOVE(USB): calibration captured and stored for {mac}.");
                    }
                }
                _calibration = blob != null ? DecodeCalibrationBlob(blob, zcm2) : null;
                if (_calibration == null)
                    _log("MOVE(USB): no calibration available; motion stays muted this session.");
            }
            catch (Exception ex) { _log("MOVE(USB): calibration read failed: " + ex.Message); }
            return (mac, storedHost);
        }

        /// <summary>Attaches the Move's virtual joystick for a moment and
        /// detaches it again, so the pad exists as an (offline) row in the
        /// Devices list the instant pairing succeeds, before its first
        /// Bluetooth connection. Without this a paired-but-never-connected
        /// Move had no row at all, so nothing to Remove (the 2026-08-18 bench
        /// gap). Identity is safe: SDL folds vid/pid/name into the virtual
        /// GUID, so this mint and the later live pad are the same device to
        /// PadForge. Skipped when a live session already owns the identity.</summary>
        public static void MintIdentityRow(Action<string> log = null)
        {
            try
            {
                var svc = _current;
                if (svc != null && svc.IsConnected) return;   // a live row already exists

                var namePtr = Marshal.StringToHGlobalAnsi("PS Move Motion Controller");
                var sensors = new SDL.SDL_VirtualJoystickSensorDesc[]
                {
                    new SDL.SDL_VirtualJoystickSensorDesc { type = SDL_SENSOR_ACCEL, rate = 170.0f },
                    new SDL.SDL_VirtualJoystickSensorDesc { type = SDL_SENSOR_GYRO,  rate = 170.0f },
                };
                int sensorSize = Marshal.SizeOf<SDL.SDL_VirtualJoystickSensorDesc>();
                IntPtr sensorsPtr = Marshal.AllocHGlobal(sensorSize * sensors.Length);
                uint id = 0;
                try
                {
                    for (int i = 0; i < sensors.Length; i++)
                        Marshal.StructureToPtr(sensors[i], sensorsPtr + i * sensorSize, false);
                    var desc = new SDL.SDL_VirtualJoystickDesc
                    {
                        type = (ushort)SDL.SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD,
                        vendor_id = MOVE_VID,
                        product_id = MOVE_PID,
                        naxes = 6,
                        nbuttons = 15,
                        nhats = 0,
                        nsensors = (ushort)sensors.Length,
                        sensors = sensorsPtr,
                        button_mask = 0x027F,
                        axis_mask = 0x3F,
                        name = namePtr,
                    };
                    desc.version = (uint)Marshal.SizeOf<SDL.SDL_VirtualJoystickDesc>();
                    id = SDL.SDL_AttachVirtualJoystick(ref desc);
                }
                finally
                {
                    Marshal.FreeHGlobal(sensorsPtr);
                    Marshal.FreeHGlobal(namePtr);
                }
                if (id == 0) { log?.Invoke("Move identity mint: attach failed."); return; }
                // Long enough for the 1000 Hz device walk to ingest the row;
                // the detach then leaves it offline.
                Thread.Sleep(2500);
                SDL.SDL_DetachVirtualJoystick(id);
                Thread.Sleep(500);   // let the walk process the removal into offline state
                // Persist NOW: a row that lives only in memory is a row the
                // next process never sees.
                try { PersistRequested?.Invoke(); } catch { }
                log?.Invoke(DeviceRowExists()
                    ? "Move registered in the device list (offline until it connects)."
                    : "Move identity mint DID NOT SURVIVE ingestion: the row is already gone from the device list.");
            }
            catch (Exception ex) { log?.Invoke("Move identity mint failed: " + ex.Message); }
        }

        /// <summary>Finds a docked Move's HID collections: the col01 data path
        /// and the col02 address path. Bluetooth HID instances are excluded
        /// (this lane's BT transport is the BthPS3 PDO, and the family is
        /// blacklisted from SDL's backends, not from the HID class).</summary>
        private (string DataPath, string AddrPath, bool Zcm2)? FindUsbHid()
        {
            HidD_GetHidGuid(out Guid hidGuid);
            string dataPath = null, addrPath = null; bool zcm2 = false; bool any = false;
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == INVALID_HANDLE) return null;
            var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            try
            {
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref did); i++)
                {
                    if ((did.Flags & SPINT_ACTIVE) == 0) continue;
                    int req = 0;
                    SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, ref req, IntPtr.Zero);
                    IntPtr det = Marshal.AllocHGlobal(req);
                    try
                    {
                        Marshal.WriteInt32(det, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref did, det, req, ref req, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(det + 4);
                        if (path == null) continue;
                        if (path.IndexOf("vid_054c", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        bool isZcm1 = path.IndexOf("pid_03d5", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool isZcm2 = path.IndexOf("pid_0c5e", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!isZcm1 && !isZcm2) continue;
                        if (path.IndexOf("bthenum", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        any = true; zcm2 = isZcm2;
                        if (path.IndexOf("col02", StringComparison.OrdinalIgnoreCase) >= 0) addrPath = path;
                        else if (path.IndexOf("col01", StringComparison.OrdinalIgnoreCase) >= 0) dataPath = path;
                        else dataPath ??= path;
                    }
                    finally { Marshal.FreeHGlobal(det); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return any && dataPath != null ? (dataPath, addrPath, zcm2) : null;
        }

        // ─── writer thread: LED/rumble on the interrupt channel, keepalive ──────

        private long _lastNoInputLog;

        private void WriterLoop(int gen)
        {
            long lastWrite = 0;
            long attachedAt = Environment.TickCount64;
            _lastNoInputLog = attachedAt;

            // First output immediately: the sphere shows the default player color
            // as soon as the pad attaches (and proves the write path early).
            WriteOutputReport();
            lastWrite = attachedAt;

            while (_running && _writerRun && Volatile.Read(ref _writerGen) == gen)
            {
                _writeSignal.WaitOne(100);
                if (!_running || !_writerRun || Volatile.Read(ref _writerGen) != gen) break;

                long now = Environment.TickCount64;

                // Input watchdog: the Move needs no enable kick (hid-sony.c:2276
                // registers no operational-mode call for MOTION), so a session
                // that never streams means the lingering-PDO case. Detach for a
                // clean re-open the same way the DS3 lane's five-kick exhaustion
                // does.
                if (!_everGotInput && now - _lastNoInputLog >= 1000)
                {
                    _lastNoInputLog = now;
                    _log($"{Tag}: no input yet, {(now - attachedAt) / 1000.0:F1} s since attach");
                }

                if (!_everGotInput && now - attachedAt >= 5000)
                {
                    _log($"{Tag}: no input 5 s after attach - detaching for a clean re-open");
                    _writerRun = false;
                    CancelCurrentRead();
                    Thread.Sleep(250);
                    CancelCurrentRead();
                    break;
                }

                bool doWrite;
                lock (_outLock)
                {
                    bool keepaliveDue = _everGotInput && now - lastWrite >= OUTPUT_KEEPALIVE_MS;
                    doWrite = (_outDirty || keepaliveDue) && now - lastWrite >= OUTPUT_MIN_INTERVAL_MS;
                }
                if (doWrite)
                {
                    WriteOutputReport();
                    lastWrite = now;
                }
            }
        }

        /// <summary>Builds the 0xA2-framed interrupt-channel output frame:
        /// 0xA2 (DATA|Output) + the 49-byte report 0x02 hid-sony sends
        /// (motion_send_output_report: [0x02, 0, r, g, b, 0, rumble] zero-padded
        /// to MOTION_REPORT_0x02_SIZE 49). Pure, test-locked.</summary>
        internal static byte[] BuildOutputFrame(byte r, byte g, byte b, byte rumble)
        {
            var o = new byte[1 + 49];
            o[0] = 0xA2;   // BT HID DATA | Output
            o[1] = 0x02;   // report id (hid-sony motion_output_report_02.type)
            o[3] = r;
            o[4] = g;
            o[5] = b;
            o[7] = rumble;
            return o;
        }

        /// <summary>The USB output report: id 0x06 (PSMove_Req_SetLEDs) with
        /// [_zero, r, g, b, rumble2, rumble] and zero padding, the 9-byte
        /// PSMove_Data_LEDs psmoveapi hid_writes (psmove.c:123-132), padded to
        /// the collection's OutputReportByteLength. Pure, test-locked.</summary>
        internal static byte[] BuildUsbOutputReport(byte r, byte g, byte b, byte rumble, int outLen)
        {
            var o = new byte[Math.Max(outLen, 9)];
            o[0] = 0x06;
            o[2] = r;
            o[3] = g;
            o[4] = b;
            o[6] = rumble;
            return o;
        }

        private bool _outWriteFailLogged;

        private void WriteOutputReport()
        {
            lock (_ioLock)
            {
                byte[] o;
                IntPtr h;
                MoveTransport tr = _transport;
                lock (_outLock)
                {
                    o = tr == MoveTransport.Usb
                        ? BuildUsbOutputReport(_r, _g, _b, _rumble, _usbOutLen)
                        : BuildOutputFrame(_r, _g, _b, _rumble);
                    _outDirty = false;
                    h = tr == MoveTransport.Usb ? _usbHandle : _writePdo;
                }
                if (h != IntPtr.Zero && h != INVALID_HANDLE)
                {
                    bool ok = tr == MoveTransport.Usb
                        ? WriteFile(h, o, o.Length, out _, IntPtr.Zero)
                        : DeviceIoControl(h, IOCTL_HID_INTERRUPT_WRITE, o, o.Length, null, 0, out _, IntPtr.Zero);
                    if (!ok && !_outWriteFailLogged)
                    {
                        _outWriteFailLogged = true;
                        _log($"{Tag}: output write FAILED err={Marshal.GetLastWin32Error()} (logged once per session)");
                    }
                }
            }
        }

        // ─── read loop: exact-size buffer, model fallback on misalignment ───────

        private void ReadLoop(IntPtr h)
        {
            byte[] buf = new byte[_modelZcm2 ? Zcm2BtReportSize : Zcm1BtReportSize];
            long lastProbe = 0;
            int misaligned = 0;
            int rxFails = 0, rxLastErr = 0, rxOther = 0;
            long rxNextSummary = Environment.TickCount64 + 1000;
            while (_running && _writerRun)
            {
                if (!_everGotInput && Environment.TickCount64 >= rxNextSummary)
                {
                    rxNextSummary = Environment.TickCount64 + 1000;
                    _log($"{Tag}: rx pre-input summary other={rxOther} fails={rxFails}"
                        + (rxFails > 0 ? $" lastErr={rxLastErr}" : ""));
                }
                if (DeviceIoControl(h, IOCTL_HID_INTERRUPT_READ, null, 0, buf, buf.Length, out int rd, IntPtr.Zero))
                {
                    if (rd >= buf.Length && buf[0] == 0xA1 && buf[1] == 0x01)
                    {
                        misaligned = 0;
                        _everGotInput = true;
                        PushState(buf, rd);
                        UpdateBattery(buf[13]);   // struct offset 12 (psmove.c:178) + 0xA1
                    }
                    else
                    {
                        rxOther++;
                        // A completed read with the wrong header is the model-size
                        // misalignment signature: a ZCM2's 45-byte frames fill a
                        // 50-byte buffer across frame boundaries (and vice versa).
                        // Four in a row flips the size and rebuilds the session.
                        if (++misaligned >= 4)
                        {
                            _modelZcm2 = !_modelZcm2;
                            _log($"MOVE(BT): stream misaligned; switching to "
                                + $"{(_modelZcm2 ? "ZCM2 (45-byte)" : "ZCM1 (50-byte)")} frames and re-opening.");
                            break;
                        }
                    }
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    rxFails++; rxLastErr = err;
                    if (err == ERROR_DEVICE_NOT_CONNECTED || err == ERROR_FILE_NOT_FOUND ||
                        err == ERROR_INVALID_HANDLE || err == ERROR_OPERATION_ABORTED)
                        break;
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

        /// <summary>Normalizes a USB HID input report (report id 0x01 at byte 0,
        /// hidapi/HidD framing) into the 0xA1-prefixed frame the shared parser
        /// expects, so BT and USB decode through one code path (the DS3 lane's
        /// normalization pattern). Returns false when the report is not an
        /// input report or is too short for the model. Pure, test-locked.</summary>
        internal static bool NormalizeUsbReport(byte[] raw, int got, byte[] dest, bool zcm2)
        {
            int need = (zcm2 ? Zcm2BtReportSize : Zcm1BtReportSize) - 1;   // raw report incl. id
            if (got < need || raw[0] != 0x01) return false;
            dest[0] = 0xA1;
            Array.Copy(raw, 0, dest, 1, need);
            return true;
        }

        private void UsbReadLoop()
        {
            bool zcm2 = _modelZcm2;
            byte[] frame = new byte[zcm2 ? Zcm2BtReportSize : Zcm1BtReportSize];
            int inLen; IntPtr h;
            lock (_outLock) { inLen = _usbInLen; h = _usbHandle; }
            byte[] raw = new byte[Math.Max(inLen, frame.Length - 1)];
            int rxShort = 0, rxWrongId = 0, rxFails = 0, rxLastErr = 0, rxLastLen = 0;
            long rxNextSummary = Environment.TickCount64 + 1000;
            while (_running && _writerRun && _transport == MoveTransport.Usb)
            {
                // Until the first accepted frame, say once a second what the
                // pipe is delivering: a wrong report length (caps mismatch), a
                // different report id, and a failing read all looked identical
                // in a silent log (the #285 lesson).
                if (!_everGotInput && Environment.TickCount64 >= rxNextSummary)
                {
                    rxNextSummary = Environment.TickCount64 + 1000;
                    _log($"MOVE(USB): rx pre-input summary short={rxShort} wrongId={rxWrongId} "
                        + $"fails={rxFails} lastLen={rxLastLen} capsIn={inLen}"
                        + (rxFails > 0 ? $" lastErr={rxLastErr}" : ""));
                }
                lock (_outLock) h = _usbHandle;
                if (h == IntPtr.Zero || h == INVALID_HANDLE) break;

                if (ReadFile(h, raw, raw.Length, out int got, IntPtr.Zero))
                {
                    rxLastLen = got;
                    if (NormalizeUsbReport(raw, got, frame, zcm2))
                    {
                        _everGotInput = true;
                        PushState(frame, frame.Length);
                        UpdateBattery(frame[13]);   // 0xEE = charging on the wire (psmove.c:178)
                    }
                    else if (got > 0 && raw[0] != 0x01) rxWrongId++;
                    else rxShort++;
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    rxFails++; rxLastErr = err;
                    if (err == ERROR_DEVICE_NOT_CONNECTED || err == ERROR_FILE_NOT_FOUND ||
                        err == ERROR_INVALID_HANDLE || err == ERROR_OPERATION_ABORTED ||
                        err == ERROR_GEN_FAILURE || err == ERROR_NO_SUCH_DEVICE)
                        break;
                    Thread.Sleep(2);
                }
            }
        }

        // ─── SDL virtual joystick ───────────────────────────────────────────────

        private bool AttachVirtual()
        {
            _rumbleCb = OnRumble; _setLedCb = OnSetLed; _setPlayerCb = OnSetPlayerIndex; _setSensorsCb = OnSetSensors;

            var namePtr = Marshal.StringToHGlobalAnsi("PS Move Motion Controller");
            // ZCM1 reports arrive at ~85 Hz with two sensor frames each, so the
            // effective sensor rate is ~170 Hz; SDL deep-copies this array.
            var sensors = new SDL.SDL_VirtualJoystickSensorDesc[]
            {
                new SDL.SDL_VirtualJoystickSensorDesc { type = SDL_SENSOR_ACCEL, rate = 170.0f },
                new SDL.SDL_VirtualJoystickSensorDesc { type = SDL_SENSOR_GYRO,  rate = 170.0f },
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
                    vendor_id = MOVE_VID,
                    product_id = MOVE_PID,
                    // Standard 6-axis gamepad shape; the Move has no sticks, so
                    // axes 0-3 rest at center and only RT (the T trigger) moves.
                    naxes = 6,
                    nbuttons = 15,
                    nhats = 0,
                    nsensors = (ushort)sensors.Length,
                    sensors = sensorsPtr,
                    // South(Cross) East(Circle) West(Square) North(Triangle)
                    // Back(Select) Guide(PS) Start + LeftShoulder(Move button).
                    button_mask = 0x027F,
                    axis_mask = 0x3F,
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
                    _log($"{Tag}: SDL_AttachVirtualJoystick failed.");
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
            lock (_teardownLock)
            {
                _writerRun = false;
                _writeSignal.Set();
                lock (_outLock)
                {
                    if (_writePdo != IntPtr.Zero && _writePdo != INVALID_HANDLE) CancelIoEx(_writePdo, IntPtr.Zero);
                }
                try { if (_writeThread != null && _writeThread != Thread.CurrentThread) _writeThread.Join(1000); } catch { }
                _writeThread = null;

                if (_instanceId != 0) { PowerByInstance.TryRemove(_instanceId, out _); _lastBattery = 0xFF; }
                if (_sdlJoystick != IntPtr.Zero) { SDL.SDL_CloseJoystick(_sdlJoystick); _sdlJoystick = IntPtr.Zero; }
                if (_instanceId != 0) { SDL.SDL_DetachVirtualJoystick(_instanceId); _instanceId = 0; }

                lock (_ioLock)
                lock (_outLock)
                {
                    if (_readPdo != IntPtr.Zero && _readPdo != INVALID_HANDLE) CloseHandle(_readPdo);
                    if (_writePdo != IntPtr.Zero && _writePdo != INVALID_HANDLE) CloseHandle(_writePdo);
                    _readPdo = IntPtr.Zero;
                    _writePdo = IntPtr.Zero;
                    if (_usbHandle != IntPtr.Zero && _usbHandle != INVALID_HANDLE) CloseHandle(_usbHandle);
                    _usbHandle = IntPtr.Zero;
                    _usbInLen = _usbOutLen = 0;
                }
                _transportPath = null;
                _transport = MoveTransport.None;
                _calibration = null;
                _calibrationMissingLogged = false;
                lock (_outLock) { _ledExplicit = false; _rumble = 0; }
            }
        }

        // ─── report decode ──────────────────────────────────────────────────────
        //
        // BT frame = 0xA1 + the raw report, so struct offset N (psmove.c
        // PSMove_Data_Input_Common) lands at buf[N+1]:
        //   buttons1..4 -> buf[2..5], trigger buf[6], trigger2 buf[7],
        //   battery buf[13], accel frame1 buf[14..19], accel frame2 buf[20..25],
        //   gyro frame1 buf[26..31], gyro frame2 buf[32..37],
        //   temp/mag (ZCM1) buf[38..43].

        /// <summary>Assembles the 32-bit button word exactly as psmove_get_buttons
        /// documents it (psmove.c: byte2 at bits 0-7, byte1 at bits 8-15, byte3
        /// bit 0 at bit 16, byte4 bits 6-7 at bits 19-20). Bit meanings are the
        /// Btn_* enum (psmove.h:95-105). Pure, test-locked.</summary>
        internal static uint DecodeButtons(byte b1, byte b2, byte b3, byte b4)
            => (uint)(b2 | (b1 << 8) | ((b3 & 0x01) << 16) | ((b4 & 0xC0) << 13));

        internal const uint BtnTriangle = 1u << 4;
        internal const uint BtnCircle   = 1u << 5;
        internal const uint BtnCross    = 1u << 6;
        internal const uint BtnSquare   = 1u << 7;
        internal const uint BtnSelect   = 1u << 8;
        internal const uint BtnStart    = 1u << 11;
        internal const uint BtnPs       = 1u << 16;
        internal const uint BtnMove     = 1u << 19;
        internal const uint BtnT        = 1u << 20;

        /// <summary>ZCM1 sensor word: little-endian 16-bit minus 0x8000
        /// (psmove_decode_16bit, psmove.c:138-144). Pure.</summary>
        internal static int DecodeZcm1(byte lo, byte hi) => (lo | (hi << 8)) - 0x8000;

        /// <summary>ZCM2 sensor word: little-endian 16-bit two's complement
        /// (psmove_decode_16bit_twos_complement, psmove.c:146-153). Pure.</summary>
        internal static int DecodeZcm2(byte lo, byte hi) => (short)(lo | (hi << 8));

        /// <summary>ZCM1 magnetometer, 12-bit signed packing per
        /// psmove_get_magnetometer (psmove.c:2034-2042) with TWELVE_BIT_SIGNED
        /// (psmove.c:135). Inputs are the raw report bytes at struct offsets
        /// 38-42 (templow_mXhigh, mXlow, mYhigh, mYlow_mZhigh, mZlow). Pure.</summary>
        internal static (int X, int Y, int Z) DecodeMagnetometer(
            byte templowMxHigh, byte mXlow, byte mYhigh, byte mYlowMzHigh, byte mZlow)
        {
            static int Signed12(int v) => (v & 0x800) != 0 ? -(((~v) & 0xFFF) + 1) : v;
            int x = Signed12(((templowMxHigh & 0x0F) << 8) | mXlow);
            int y = Signed12((mYhigh << 4) | ((mYlowMzHigh & 0xF0) >> 4));
            int z = Signed12(((mYlowMzHigh & 0x0F) << 8) | mZlow);
            return (x, y, z);
        }

        private void PushState(byte[] b, int len)
        {
            IntPtr j = _sdlJoystick;
            if (j == IntPtr.Zero) return;

            uint buttons = DecodeButtons(b[2], b[3], b[4], b[5]);
            string magLine = null, calLine = null;

            SDL.SDL_LockJoysticks();
            try
            {
                // SDL_GamepadButton order: 0 South 1 East 2 West 3 North 4 Back
                // 5 Guide 6 Start 7 LStick 8 RStick 9 LShoulder 10 RShoulder.
                SDL.SDL_SetJoystickVirtualButton(j, 0, (buttons & BtnCross) != 0);
                SDL.SDL_SetJoystickVirtualButton(j, 1, (buttons & BtnCircle) != 0);
                SDL.SDL_SetJoystickVirtualButton(j, 2, (buttons & BtnSquare) != 0);
                SDL.SDL_SetJoystickVirtualButton(j, 3, (buttons & BtnTriangle) != 0);
                SDL.SDL_SetJoystickVirtualButton(j, 4, (buttons & BtnSelect) != 0);
                SDL.SDL_SetJoystickVirtualButton(j, 5, (buttons & BtnPs) != 0);
                SDL.SDL_SetJoystickVirtualButton(j, 6, (buttons & BtnStart) != 0);
                SDL.SDL_SetJoystickVirtualButton(j, 9, (buttons & BtnMove) != 0);

                // T trigger -> RT axis, SDL PS3-driver scaling (released = MIN).
                SDL.SDL_SetJoystickVirtualAxis(j, 5, (short)(b[6] * 257 - 32768));

                var cal = _calibration;
                if (cal != null)
                {
                    ulong ts = SDL.SDL_GetTicksNS();
                    if (!_modelZcm2 && len >= Zcm1BtReportSize)
                    {
                        // Both ZCM1 half-frames, each ~half a report period apart.
                        // Frame 2 is the NEWER sample (psmove_get_half_frame reads
                        // Frame_SecondHalf at base+6 and psmove.c:1846 averages the
                        // two for the "current" value); send frame 1 with an
                        // earlier timestamp.
                        ulong half = 5_800_000; // ~half of an ~85 Hz report period, ns
                        SendSensorFrame(j, cal, b, 14, 26, ts - half);
                        SendSensorFrame(j, cal, b, 20, 32, ts);
                        magLine = TraceMagnetometer(b);
                    }
                    else if (_modelZcm2 && len >= Zcm2BtReportSize)
                    {
                        // ZCM2: one valid frame (psmove.c:1813).
                        SendSensorFrame(j, cal, b, 14, 26, ts);
                    }
                }
                else
                {
                    calLine = TraceCalibrationMissing();
                }
            }
            finally { SDL.SDL_UnlockJoysticks(); }
            if (magLine != null) _log(magLine);
            if (calLine != null) _log(calLine);
        }

        /// <summary>Decodes one accel+gyro half-frame at the given BT offsets,
        /// applies the per-unit calibration, maps the Move's native axes into
        /// SDL's sensor space, and sends both sensors.
        ///
        /// Axis mapping (hypothesis pending hardware): the Move's native frame
        /// has X right, Y along the controller toward the sphere, Z out of the
        /// button face. SDL wants X right, Y up, Z toward the user. Held flat
        /// (buttons up, sphere forward), native Y points away from the user
        /// (SDL -Z) and native Z points up (SDL +Y):
        ///   sdl = (x, z, -y) for both sensors.</summary>
        private void SendSensorFrame(IntPtr j, MoveCalibration cal, byte[] b, int accelOff, int gyroOff, ulong ts)
        {
            int rax, ray, raz, rgx, rgy, rgz;
            if (_modelZcm2)
            {
                rax = DecodeZcm2(b[accelOff], b[accelOff + 1]);
                ray = DecodeZcm2(b[accelOff + 2], b[accelOff + 3]);
                raz = DecodeZcm2(b[accelOff + 4], b[accelOff + 5]);
                rgx = DecodeZcm2(b[gyroOff], b[gyroOff + 1]);
                rgy = DecodeZcm2(b[gyroOff + 2], b[gyroOff + 3]);
                rgz = DecodeZcm2(b[gyroOff + 4], b[gyroOff + 5]);
            }
            else
            {
                rax = DecodeZcm1(b[accelOff], b[accelOff + 1]);
                ray = DecodeZcm1(b[accelOff + 2], b[accelOff + 3]);
                raz = DecodeZcm1(b[accelOff + 4], b[accelOff + 5]);
                rgx = DecodeZcm1(b[gyroOff], b[gyroOff + 1]);
                rgy = DecodeZcm1(b[gyroOff + 2], b[gyroOff + 3]);
                rgz = DecodeZcm1(b[gyroOff + 4], b[gyroOff + 5]);
            }

            // Calibrated units: accel in g (f*raw + c), gyro in rad/s (f*raw),
            // per psmove_calibration.c's linear mapping.
            float ax = (cal.Fax * rax + cal.Cax) * SDL_STANDARD_GRAVITY;
            float ay = (cal.Fay * ray + cal.Cay) * SDL_STANDARD_GRAVITY;
            float az = (cal.Faz * raz + cal.Caz) * SDL_STANDARD_GRAVITY;
            float gx = cal.Fgx * (rgx - cal.Dgx);
            float gy = cal.Fgy * (rgy - cal.Dgy);
            float gz = cal.Fgz * (rgz - cal.Dgz);

            _accelData[0] = ax; _accelData[1] = az; _accelData[2] = -ay;
            SDL.SDL_SendJoystickVirtualSensorData(j, SDL_SENSOR_ACCEL, ts, _accelData, 3);
            _gyroData[0] = gx; _gyroData[1] = gz; _gyroData[2] = -gy;
            SDL.SDL_SendJoystickVirtualSensorData(j, SDL_SENSOR_GYRO, ts, _gyroData, 3);
        }

        private const int SDL_SENSOR_ACCEL = 1;
        private const int SDL_SENSOR_GYRO = 2;
        private const float SDL_STANDARD_GRAVITY = 9.80665f;

        private readonly float[] _accelData = new float[3];
        private readonly float[] _gyroData = new float[3];

        // ─── magnetometer trace (ZCM1 only; no engine consumer yet) ─────────────
        //
        // The compass is decoded and proven in the diagnostics ring (2 s cadence)
        // so the fusion work has ground truth when it starts; SDL has no
        // magnetometer sensor type to carry it further today.

        private long _magNextLogTicks;

        private string TraceMagnetometer(byte[] b)
        {
            long now = Environment.TickCount64;
            if (now < _magNextLogTicks) return null;
            _magNextLogTicks = now + 2000;
            var (mx, my, mz) = DecodeMagnetometer(b[39], b[40], b[41], b[42], b[43]);
            if (mx == 0 && my == 0 && mz == 0) return null;   // ZCM1 without EXT data still sends real values; all-zero = likely ZCM2-ish silence
            return $"MOVEMAG raw=({mx},{my},{mz})";
        }

        // ─── calibration ────────────────────────────────────────────────────────

        internal sealed class MoveCalibration
        {
            // Accel: calibrated_g = F*raw + C (psmove_calibration.c:474-500).
            public float Fax, Fay, Faz, Cax, Cay, Caz;
            // Gyro: calibrated_rad_s = F*(raw - D) (psmove_calibration.c:539-560;
            // D is zero for ZCM1, the 0x26 drift words for ZCM2).
            public float Fgx, Fgy, Fgz;
            public int Dgx, Dgy, Dgz;
        }

        private volatile MoveCalibration _calibration;
        private bool _calibrationMissingLogged;

        private string TraceCalibrationMissing()
        {
            if (_calibrationMissingLogged) return null;
            _calibrationMissingLogged = true;
            return "MOVE(BT): no stored calibration for this pad - motion sensors stay muted. "
                 + "Pair the Move over USB once (Devices page) to capture its calibration.";
        }

        private void LoadCalibrationForPath()
        {
            try
            {
                var provider = CalibrationProvider;
                if (provider == null) return;
                string mac = ExtractMacFromPath(_transportPath);
                byte[] blob = provider(mac);
                if (blob == null) return;
                _calibration = DecodeCalibrationBlob(blob, _modelZcm2);
                if (_calibration != null)
                    _log($"MOVE(BT): calibration loaded ({blob.Length} bytes"
                        + (mac != null ? $", pad {mac}" : ", single stored pad") + ").");
            }
            catch (Exception ex) { _log("MOVE(BT): calibration load failed: " + ex.Message); }
        }

        /// <summary>Pulls the 12-hex-digit remote address out of a BthPS3 raw-PDO
        /// interface path (the PDO's serial segment carries it). GUID segments
        /// also end in 12-hex runs, so everything inside braces is dropped first;
        /// of the remaining exact-12 hex runs the LAST one is the serial. Returns
        /// null when no such token exists; the provider then falls back to the
        /// single stored blob, if exactly one. Pure.</summary>
        internal static string ExtractMacFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var sb = new System.Text.StringBuilder(path.Length);
            int depth = 0;
            foreach (char ch in path)
            {
                if (ch == '{') { depth++; continue; }
                if (ch == '}') { if (depth > 0) depth--; continue; }
                if (depth == 0) sb.Append(ch);
            }
            string bare = sb.ToString();
            string best = null;
            int run = 0;
            for (int i = 0; i <= bare.Length; i++)
            {
                bool hex = i < bare.Length && Uri.IsHexDigit(bare[i]);
                if (hex) { run++; continue; }
                if (run == 12) best = bare.Substring(i - 12, 12);
                run = 0;
            }
            return best?.ToLowerInvariant();
        }

        /// <summary>Decodes the USB calibration blob into the linear mapping,
        /// byte-exact against psmove_calibration.c:
        /// ZCM1 (143 bytes = 49*3-4): accel ±1g points via 16-bit-unsigned-minus-
        /// 0x8000 words at 0x04+6*orientation (orientations per
        /// psmove_calibration_get_usb_accel_values), gyro 80 RPM point at
        /// 0x46+8*axis minus the bias words at 0x2A, factor = 80 RPM in rad/s /
        /// point (psmove_calibration.c:541-560).
        /// ZCM2 (96 bytes = 49*2-2): signed words, accel at 0x02+6*orientation,
        /// gyro ±90 RPM at 0x30+6*orientation with drift words at 0x26.
        /// Returns null when the blob length matches neither model. Pure.</summary>
        internal static MoveCalibration DecodeCalibrationBlob(byte[] blob, bool zcm2)
        {
            const float RpmToRadPerSec = 2.0f * (float)Math.PI / 60.0f;
            static int U16(byte[] d, int o) => ((d[o] | (d[o + 1] << 8)) - 0x8000);
            static int S16(byte[] d, int o) => (short)(d[o] | (d[o + 1] << 8));

            if (!zcm2 && blob.Length == 143)
            {
                int axLo = U16(blob, 0x04 + 6 * 1), axHi = U16(blob, 0x04 + 6 * 3);
                int ayLo = U16(blob, 0x04 + 6 * 5 + 2), ayHi = U16(blob, 0x04 + 6 * 4 + 2);
                int azLo = U16(blob, 0x04 + 6 * 2 + 4), azHi = U16(blob, 0x04 + 6 * 0 + 4);
                int bx = U16(blob, 0x2A), by = U16(blob, 0x2A + 2), bz = U16(blob, 0x2A + 4);
                int gx80 = U16(blob, 0x46 + 8 * 0) - bx;
                int gy80 = U16(blob, 0x46 + 8 * 1 + 2) - by;
                int gz80 = U16(blob, 0x46 + 8 * 2 + 4) - bz;
                if (axHi == axLo || ayHi == ayLo || azHi == azLo || gx80 == 0 || gy80 == 0 || gz80 == 0)
                    return null;
                const float gyroFactor = 80.0f * RpmToRadPerSec;
                var c = new MoveCalibration
                {
                    Fax = 2.0f / (axHi - axLo),
                    Fay = 2.0f / (ayHi - ayLo),
                    Faz = 2.0f / (azHi - azLo),
                    Fgx = gyroFactor / gx80,
                    Fgy = gyroFactor / gy80,
                    Fgz = gyroFactor / gz80,
                };
                c.Cax = -(c.Fax * axLo) - 1.0f;
                c.Cay = -(c.Fay * ayLo) - 1.0f;
                c.Caz = -(c.Faz * azLo) - 1.0f;
                return c;
            }
            if (zcm2 && blob.Length == 96)
            {
                int axLo = S16(blob, 0x02 + 6 * 1), axHi = S16(blob, 0x02 + 6 * 0);
                int ayLo = S16(blob, 0x02 + 6 * 3 + 2), ayHi = S16(blob, 0x02 + 6 * 2 + 2);
                int azLo = S16(blob, 0x02 + 6 * 5 + 4), azHi = S16(blob, 0x02 + 6 * 4 + 4);
                int dx = S16(blob, 0x26), dy = S16(blob, 0x26 + 2), dz = S16(blob, 0x26 + 4);
                int gxLo = S16(blob, 0x30 + 6 * 3), gxHi = S16(blob, 0x30 + 6 * 0);
                int gyLo = S16(blob, 0x30 + 6 * 4 + 2), gyHi = S16(blob, 0x30 + 6 * 1 + 2);
                int gzLo = S16(blob, 0x30 + 6 * 5 + 4), gzHi = S16(blob, 0x30 + 6 * 2 + 4);
                if (axHi == axLo || ayHi == ayLo || azHi == azLo
                    || gxHi == gxLo || gyHi == gyLo || gzHi == gzLo)
                    return null;
                const float spanFactor = 2.0f * 90.0f * RpmToRadPerSec;
                var c = new MoveCalibration
                {
                    Fax = 2.0f / (axHi - axLo),
                    Fay = 2.0f / (ayHi - ayLo),
                    Faz = 2.0f / (azHi - azLo),
                    Fgx = spanFactor / (gxHi - gxLo),
                    Fgy = spanFactor / (gyHi - gyLo),
                    Fgz = spanFactor / (gzHi - gzLo),
                    Dgx = dx, Dgy = dy, Dgz = dz,
                };
                c.Cax = -(c.Fax * axLo) - 1.0f;
                c.Cay = -(c.Fay * ayLo) - 1.0f;
                c.Caz = -(c.Faz * azLo) - 1.0f;
                return c;
            }
            return null;
        }

        // ─── SDL callbacks: store + signal only ─────────────────────────────────

        private bool OnRumble(IntPtr userdata, ushort low, ushort high)
        {
            // One rumble byte; hid-sony sends max(right, left)
            // (motion_send_output_report, CONFIG_SONY_FF branch).
            byte r = (byte)(Math.Max(low, high) >> 8);
            lock (_outLock)
            {
                if (_rumble != r) { _rumble = r; _outDirty = true; }
            }
            _writeSignal.Set();
            return true;
        }

        private bool OnSetLed(IntPtr userdata, byte r, byte g, byte bl)
        {
            lock (_outLock)
            {
                _ledExplicit = true;
                if (_r != r || _g != g || _b != bl)
                {
                    _r = r; _g = g; _b = bl; _outDirty = true;
                }
            }
            _writeSignal.Set();
            return true;
        }

        private void OnSetPlayerIndex(IntPtr userdata, int playerIndex)
            => SetPlayerNumber(playerIndex < 0 ? 0 : playerIndex + 1);

        private bool OnSetSensors(IntPtr userdata, bool enabled) => true;

        // ─── device enumeration + native I/O ────────────────────────────────────

        private string FindPdoPath()
        {
            Guid g = MotionInterface;
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == INVALID_HANDLE) return null;
            var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            try
            {
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did); i++)
                {
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
                            if (p != null) return p;
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

        private const int ERROR_GEN_FAILURE = 31, ERROR_NO_SUCH_DEVICE = 433;

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetFeature(IntPtr h, byte[] buf, int len);
        [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr preparsed);
        [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr preparsed);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr h, byte[] buf, int len, out int read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr h, byte[] buf, int len, out int written, IntPtr ov);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(IntPtr h, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inLen, byte[] outBuf, int outLen, out int ret, IntPtr ov);
    }
}
