using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Guide button LED brightness for Xbox One / Elite / Series controllers
    /// over USB (discussion #209), via the multiplexed <c>\\.\XboxGIP</c>
    /// device interface that xboxgip.sys exposes. The lane is the one
    /// xbledctl proved out (xbledctl/docs/RESEARCH.md "The Real
    /// Breakthrough"): open the interface with overlapped I/O, send IOCTL
    /// 0x40001CD0 to register as a reenumerate caller, read announce
    /// messages to learn each controller's driver-assigned deviceId, then
    /// WriteFile a 23-byte packet (20-byte GipHeader + 3-byte LED payload).
    /// The GIP LED command itself is GIP_CMD_LED 0x0A with payload
    /// {sub-command 0x00, mode, brightness} per xone bus/protocol.c
    /// (gip_pkt_led / gip_set_led_mode) and xow controller/gip.cpp
    /// (LedModeData / setLedMode). Intensity range is 0-47 per MS-GIPUSB
    /// (xbledctl LED_BRIGHTNESS_MAX).
    ///
    /// USB only: Bluetooth Xbox pads ride xinputhid with no GIP lane, so
    /// writes for them match no announce entry and drop silently
    /// (xbledctl RESEARCH.md; the SDL HIDAPI Xbox LED path is dead on
    /// Windows because xboxgip blocks HIDAPI access).
    ///
    /// Lazy singleton. One background worker thread owns the handle, the
    /// announce listener, and every write, so callers never block on
    /// device I/O: <see cref="TrySetBrightness"/> only enqueues. When the
    /// interface is absent (xboxgip not loaded) the writer stays inert and
    /// retries the open on a cooldown. Nothing here ever throws into a
    /// caller.
    /// </summary>
    internal sealed class XboxGipGuideLedWriter
    {
        // ─────────────────────────────────────────────
        //  Inline P/Invoke (repo convention)
        // ─────────────────────────────────────────────

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize,
            IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(
            IntPtr hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(
            IntPtr hFile, IntPtr lpBuffer, uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(
            IntPtr hFile, IntPtr lpOverlapped,
            out uint lpNumberOfBytesTransferred, bool bWait);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateEventW(
            IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ResetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForMultipleObjects(
            uint nCount, IntPtr[] lpHandles, bool bWaitAll, uint dwMilliseconds);

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const int ERROR_IO_PENDING = 997;
        private const uint WAIT_OBJECT_0 = 0;
        private const uint WAIT_TIMEOUT = 258;
        private static readonly IntPtr InvalidHandle = new(-1);

        // ─────────────────────────────────────────────
        //  Wire constants. Source: xbledctl src/xbox_led.h + docs/RESEARCH.md,
        //  corroborated by xone bus/protocol.c and xow controller/gip.cpp.
        // ─────────────────────────────────────────────

        /// <summary>IOCTL that registers this handle for device announce
        /// messages (GIP_ADD_REENUMERATE_CALLER_CONTEXT, xbledctl
        /// xbox_led.c GIP_REENUMERATE).</summary>
        private const uint GipReenumerateIoctl = 0x40001CD0;

        /// <summary>GIP_CMD_LED (xone protocol.c, xbledctl xbox_led.h).</summary>
        internal const byte GipCmdLed = 0x0A;

        /// <summary>GIP_OPT_INTERNAL client flag (xone protocol.c BIT(5)).</summary>
        internal const byte GipOptInternal = 0x20;

        /// <summary>LED brightness ceiling per MS-GIPUSB
        /// (xbledctl LED_BRIGHTNESS_MAX).</summary>
        internal const int MaxIntensity = 47;

        /// <summary>Packed GipHeader size on the \\.\XboxGIP framing:
        /// u64 deviceId + u8 commandId + u8 clientFlags + u8 sequence +
        /// u8 zero + u32 length + u32 zero (xbledctl RESEARCH.md).</summary>
        internal const int HeaderSize = 20;

        private const byte LedModeOff = 0x00;
        private const byte LedModeOn = 0x01;
        private const byte GipCmdAcknowledge = 0x01;
        private const byte GipCmdAnnounce = 0x02;

        // ─────────────────────────────────────────────
        //  Pure wire-format helpers (unit-tested)
        // ─────────────────────────────────────────────

        /// <summary>Scales a UI brightness percent (0-100) onto the GIP
        /// intensity range 0-47, round-half-up, monotone, with exact
        /// endpoints (0 to 0, 100 to 47).</summary>
        internal static byte ScaleToIntensity(int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            return (byte)((percent * MaxIntensity + 50) / 100);
        }

        /// <summary>Percent to (mode, intensity): 0 percent turns the LED
        /// off (mode 0x00), anything else is steady-on (mode 0x01) at the
        /// scaled intensity. Mirrors xbledctl xbox_set_brightness.</summary>
        internal static (byte Mode, byte Intensity) FromPercent(int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            return percent == 0
                ? (LedModeOff, (byte)0)
                : (LedModeOn, ScaleToIntensity(percent));
        }

        /// <summary>Battery percent to LED brightness percent for the
        /// Battery guide-LED mode: a fuller battery is brighter, floored
        /// at 10 so a low battery stays visible. Unknown battery (negative
        /// input) returns -1, meaning skip the write.</summary>
        internal static int BatteryToBrightnessPercent(int batteryPercent)
        {
            if (batteryPercent < 0) return -1;
            return Math.Clamp(batteryPercent, 10, 100);
        }

        /// <summary>Builds the 23-byte \\.\XboxGIP LED packet: the packed
        /// 20-byte GipHeader {deviceId, commandId 0x0A, clientFlags 0x20,
        /// sequence, 0, length 3, 0} followed by the 3-byte gip_pkt_led
        /// payload {0x00 sub-command, mode, intensity}. Layout per
        /// xbledctl xbox_led.c xbox_set_led / docs/RESEARCH.md, payload
        /// per xone protocol.c gip_pkt_led.</summary>
        internal static byte[] BuildLedPacket(ulong deviceId, byte sequence, byte mode, byte intensity)
        {
            var pkt = new byte[HeaderSize + 3];
            for (int i = 0; i < 8; i++)
                pkt[i] = (byte)(deviceId >> (8 * i));
            pkt[8] = GipCmdLed;
            pkt[9] = GipOptInternal;
            pkt[10] = sequence;
            pkt[11] = 0;
            pkt[12] = 3; // u32 LE payload length, high bytes stay 0
            pkt[20] = 0x00;
            pkt[21] = mode;
            pkt[22] = intensity;
            return pkt;
        }

        /// <summary>Parses one \\.\XboxGIP message for its header deviceId,
        /// accepting BOTH 0x01 ACKNOWLEDGE and 0x02 ANNOUNCE exactly like
        /// the proven reference (xbledctl xbox_led.c:71 accepts commandId
        /// <c>0x01 || 0x02</c> and uses <c>hdr-&gt;deviceId</c> from the
        /// header with no payload parse). VID/PID are read opportunistically
        /// from a 0x02 announce payload (xone gip_pkt_announce layout: VID at
        /// payload+8, PID at +10) and left 0 otherwise.
        ///
        /// <para>The earlier version required a 0x02 message carrying a
        /// parseable VID/PID payload, so on hardware that delivered 0x01, or
        /// 0x02 framed without our assumed payload, the device map never
        /// populated and every write silently no-opped. That was the feature
        /// shipping dead. The reference deliberately keys nothing on the
        /// payload.</para></summary>
        internal static bool TryParseAnnounce(byte[] buf, int read,
            out ulong deviceId, out ushort vendorId, out ushort productId, out ulong address)
        {
            deviceId = 0; vendorId = 0; productId = 0; address = 0;
            if (buf == null || read < HeaderSize) return false;

            byte cmd = buf[8];
            if (cmd != GipCmdAnnounce && cmd != GipCmdAcknowledge) return false;

            for (int i = 0; i < 8; i++)
                deviceId |= (ulong)buf[i] << (8 * i);

            // Identity payload only exists on a 0x02 announce; read it when
            // present, leave VID/PID at 0 otherwise. ProcessPending falls
            // back to every discovered deviceId when no VID/PID is known.
            if (cmd == GipCmdAnnounce)
            {
                uint payloadLen = (uint)(buf[12] | buf[13] << 8 | buf[14] << 16 | buf[15] << 24);
                if (payloadLen >= 12 && read >= HeaderSize + 12)
                {
                    for (int i = 0; i < 6; i++)
                        address |= (ulong)buf[HeaderSize + i] << (8 * i);
                    vendorId = (ushort)(buf[HeaderSize + 8] | buf[HeaderSize + 9] << 8);
                    productId = (ushort)(buf[HeaderSize + 10] | buf[HeaderSize + 11] << 8);
                }
            }
            return true;
        }

        /// <summary>Selects the announced deviceIds a (VID, PID) brightness
        /// request writes to. Direct identity matches win; with no match the
        /// request falls back to EVERY announced deviceId. The fallback is
        /// load-bearing, not best-effort politeness: the request key comes
        /// from SDL's XInput lane, which SYNTHESIZES a generic product id
        /// when RAWINPUT correlation is off (PadForge policy), while the GIP
        /// announce carries the pad's real identity, so the two namespaces
        /// need not ever agree. Bench ground truth 2026-07-12 (diag.log
        /// 01:54): request 0x045E/0x02FF, announce 0x045E/0x0B12, LED lit
        /// via the fallback. Restricting the fallback to identity-matched or
        /// unidentified entries kills the feature on exactly that rig.
        /// xbledctl parity agrees: the reference keys nothing on VID/PID
        /// (xbox_led.c:71-74 takes hdr-&gt;deviceId only). Consequence,
        /// documented and accepted: multiple GIP pads on one rig share a
        /// brightness (last configured wins), because no field exists that
        /// ties an announce to one SDL device.</summary>
        internal static List<ulong> SelectWriteTargets(
            (ushort Vid, ushort Pid) key,
            IEnumerable<KeyValuePair<ulong, (ushort Vid, ushort Pid, ulong Address, long LastSeen)>> announced,
            out bool fellBack)
        {
            var matches = new List<ulong>();
            var all = new List<ulong>();
            foreach (var a in announced)
            {
                all.Add(a.Key);
                if (a.Value.Vid == key.Vid && a.Value.Pid == key.Pid)
                    matches.Add(a.Key);
            }
            fellBack = matches.Count == 0 && all.Count > 0;
            return fellBack ? all : matches;
        }

        /// <summary>True when the device rides Windows' XInput lane
        /// (SDL's synthetic "XInput#N" path, SDL_xinputjoystick.c) AND is
        /// a first-party GIP pad (One / Elite / Series, the Microsoft
        /// impulse-trigger PID set). The path check alone over-matched:
        /// SDL hands XInput#N to every XInput userid, so Xbox 360 and
        /// third-party XUSB pads got a fully interactive card whose
        /// writes could never match a GIP announce (xbledctl RESEARCH.md
        /// section 9: XUSB speaks only the XInput protocol). Known
        /// scope: licensed third-party GIP pads and the Adaptive
        /// Controller are outside the gate until their PIDs are curated.
        /// Bluetooth GIP pads still pass the PID gate but announce
        /// nothing, the USB-only case the card's subtitle
        /// discloses.</summary>
        internal static bool IsXboxGipPathed(UserDevice ud)
            => ud?.DevicePath != null
            && ud.DevicePath.StartsWith("XInput#", StringComparison.OrdinalIgnoreCase)
            && PadForge.Engine.XboxControllerIdentity.IsImpulseTriggerDevice(
                   (ushort)ud.VendorId, (ushort)ud.ProdId);

        // ─────────────────────────────────────────────
        //  Singleton + request queue
        // ─────────────────────────────────────────────

        private static XboxGipGuideLedWriter _instance;
        private static readonly object InstanceLock = new();

        public static XboxGipGuideLedWriter Instance
        {
            get
            {
                if (_instance == null)
                    lock (InstanceLock)
                        _instance ??= new XboxGipGuideLedWriter();
                return _instance;
            }
        }

        // Latest-wins request per (VID, PID). Value = (percent, attempts).
        // Bounded: requests beyond the cap are dropped rather than grown.
        private readonly ConcurrentDictionary<(ushort Vid, ushort Pid), (int Percent, int Attempts)> _pending = new();
        private const int MaxPendingModels = 64;
        private const int MaxAttempts = 20;

        /// <summary>Diag dedup only: last percent LOGGED per model (caller
        /// threads) and pend lines already emitted (worker thread). Both
        /// persist across the queue drain, unlike _pending, so steady-state
        /// re-enqueues stay silent. Cleared wholesale at the model cap;
        /// losing dedup state costs one duplicate log line, nothing
        /// else.</summary>
        private readonly ConcurrentDictionary<(ushort Vid, ushort Pid), int> _lastLoggedEnqueue = new();
        private readonly HashSet<(ushort Vid, ushort Pid, int Percent)> _pendLogged = new();

        private readonly AutoResetEvent _work = new(false);

        // Worker-thread-only state.
        private IntPtr _handle = InvalidHandle;
        private IntPtr _readEvent;
        private IntPtr _writeEvent;
        private IntPtr _readBuffer;
        private IntPtr _writeBuffer;
        private IntPtr _readOverlapped;
        private IntPtr _writeOverlapped;
        private bool _readPending;
        private byte _sequence = 1;
        private long _nextOpenAttemptTick;
        private long _lastReenumNudgeTick;
        private int _lastOpenFailGle;

        private const int ReadBufferSize = 4096;
        private const int OpenRetryCooldownMs = 15000;

        /// <summary>deviceId to announce identity, worker-thread-only.
        /// Bounded to <see cref="MaxAnnounceEntries"/>, oldest evicted.</summary>
        private readonly Dictionary<ulong, (ushort Vid, ushort Pid, ulong Address, long LastSeen)> _announced = new();
        private const int MaxAnnounceEntries = 32;

        /// <summary>Per-deviceId change detection: the last (mode,
        /// intensity) successfully written, so re-applies (the 30 s
        /// battery cadence, device-update reseeds) skip redundant
        /// writes.</summary>
        private readonly Dictionary<ulong, (byte Mode, byte Intensity)> _lastWritten = new();

        private XboxGipGuideLedWriter()
        {
            var thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "XboxGipGuideLed",
            };
            thread.Start();
        }

        /// <summary>Queues a Guide LED brightness write for every announced
        /// GIP controller matching the device's VID/PID. A single match is
        /// the exact controller. Multiple same-model controllers all
        /// receive the write, because the GIP announce carries no stable
        /// tie to SDL's XInput slot numbering, and same-model pads sharing
        /// one brightness is the acceptable degenerate case. Never throws
        /// and never blocks on device I/O.</summary>
        public bool TrySetBrightness(UserDevice ud, int percent0to100)
        {
            try
            {
                if (!IsXboxGipPathed(ud)) return false;
                if (ud.VendorId == 0) return false;
                if (_pending.Count >= MaxPendingModels
                    && !_pending.ContainsKey((ud.VendorId, ud.ProdId))) return false;

                int pct = Math.Clamp(percent0to100, 0, 100);
                _pending[(ud.VendorId, ud.ProdId)] = (pct, 0);
                _work.Set();
                // Diag only on a CHANGED request. The dedup ledger must be
                // separate from _pending, which ProcessPending DRAINS on
                // success: a drained key would read as "changed" on every
                // 30 s re-enqueue and the gate would log a steady-state
                // no-op line per pass, the exact churn it exists to stop.
                if (_lastLoggedEnqueue.Count > MaxPendingModels) _lastLoggedEnqueue.Clear();
                bool changed = !_lastLoggedEnqueue.TryGetValue((ud.VendorId, ud.ProdId), out int prior)
                               || prior != pct;
                if (changed)
                {
                    _lastLoggedEnqueue[(ud.VendorId, ud.ProdId)] = pct;
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"GUIDELED enqueue vid=0x{ud.VendorId:X4} pid=0x{ud.ProdId:X4} pct={pct}");
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ─────────────────────────────────────────────
        //  Worker thread. Owns the handle end to end.
        // ─────────────────────────────────────────────

        private void WorkerLoop()
        {
            try
            {
                _readEvent = CreateEventW(IntPtr.Zero, true, false, null);
                _writeEvent = CreateEventW(IntPtr.Zero, true, false, null);
                _readBuffer = Marshal.AllocHGlobal(ReadBufferSize);
                _writeBuffer = Marshal.AllocHGlobal(64);
                _readOverlapped = Marshal.AllocHGlobal(OverlappedSize);
                _writeOverlapped = Marshal.AllocHGlobal(OverlappedSize);
                if (_readEvent == IntPtr.Zero || _writeEvent == IntPtr.Zero) return;

                var waitBoth = new[] { _readEvent, _work.SafeWaitHandle.DangerousGetHandle() };

                while (true)
                {
                    if (_handle == InvalidHandle)
                    {
                        // Inert until the interface opens. Only attempt an
                        // open while somebody actually wants a write, on a
                        // cooldown, so a machine without xboxgip loaded is
                        // never polled hot.
                        if (_pending.IsEmpty)
                        {
                            _work.WaitOne(OpenRetryCooldownMs);
                            continue;
                        }
                        long now = Environment.TickCount64;
                        if (now < _nextOpenAttemptTick)
                        {
                            _work.WaitOne((int)Math.Min(_nextOpenAttemptTick - now, OpenRetryCooldownMs));
                            continue;
                        }
                        _nextOpenAttemptTick = now + OpenRetryCooldownMs;
                        if (!OpenInterface()) continue;
                    }

                    if (!_readPending && !StartAnnounceRead())
                    {
                        CloseInterface();
                        continue;
                    }

                    uint waited = WaitForMultipleObjects(2, waitBoth, false, 1000);
                    if (waited == WAIT_OBJECT_0)
                    {
                        // Announce (or acknowledge) message completed.
                        if (GetOverlappedResult(_handle, _readOverlapped, out uint rd, false))
                        {
                            _readPending = false;
                            HarvestAnnounce((int)rd);
                        }
                        else
                        {
                            _readPending = false;
                            CloseInterface();
                            continue;
                        }
                    }
                    else if (waited != WAIT_OBJECT_0 + 1 && waited != WAIT_TIMEOUT)
                    {
                        // WAIT_FAILED. Tear down rather than spin hot on a
                        // broken handle pair.
                        CloseInterface();
                        continue;
                    }
                    // WAIT_OBJECT_0 + 1 = work signaled, WAIT_TIMEOUT = tick.
                    // Both fall through to a pending pass so retries advance.

                    ProcessPending();
                }
            }
            catch
            {
                // Worker death leaves the writer permanently inert for the
                // session. LED writes are cosmetic, never crash the app.
            }
        }

        private bool OpenInterface()
        {
            // CreateFileW(\\.\XboxGIP, R/W, share R/W, OPEN_EXISTING,
            // FILE_FLAG_OVERLAPPED) then the reenumerate-caller IOCTL, the
            // exact open + registration sequence in xbledctl xbox_led.c
            // (xbox_open_device / discover_devices). The IOCTL wires this
            // handle into the announce pipeline and provokes the driver to
            // emit announces for already-connected controllers.
            _handle = CreateFileW("\\\\.\\XboxGIP",
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);
            if (_handle == InvalidHandle)
            {
                // Once per distinct error code: a machine without xboxgip
                // loaded (gle=2) retries the open on the cooldown for as
                // long as a request is pending, and identical lines per
                // retry read as an error storm for an expected condition.
                int gle = Marshal.GetLastWin32Error();
                if (gle != _lastOpenFailGle)
                {
                    _lastOpenFailGle = gle;
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"GUIDELED open unavailable gle={gle}");
                }
                return false;
            }
            _lastOpenFailGle = 0;

            bool ioctlOk = DeviceIoControl(_handle, GipReenumerateIoctl,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            _readPending = false;
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"GUIDELED open ok ioctl={ioctlOk} gle={Marshal.GetLastWin32Error()}");
            return true;
        }

        private void CloseInterface()
        {
            if (_handle != InvalidHandle)
            {
                if (_readPending)
                {
                    CancelIoEx(_handle, _readOverlapped);
                    WaitForSingleObject(_readEvent, 100);
                    _readPending = false;
                }
                CloseHandle(_handle);
                _handle = InvalidHandle;
            }
            // Announce deviceIds are handle-independent driver state, but
            // a broken handle usually means topology changed. Drop the map
            // so matches rebuild from fresh announces.
            _announced.Clear();
            _lastWritten.Clear();
        }

        private bool StartAnnounceRead()
        {
            ResetEvent(_readEvent);
            ZeroOverlapped(_readOverlapped, _readEvent);
            bool ok = ReadFile(_handle, _readBuffer, ReadBufferSize, out uint rd, _readOverlapped);
            if (ok)
            {
                // Completed synchronously.
                HarvestAnnounce((int)rd);
                return true;
            }
            if (Marshal.GetLastWin32Error() == ERROR_IO_PENDING)
            {
                _readPending = true;
                return true;
            }
            return false;
        }

        /// <summary>Last rx line logged, worker-thread-only. The interface
        /// streams periodic status heartbeats (cmd 0x03 and 0x20 observed
        /// every ~20 s on the bench), so an unconditional rx dump churns
        /// thousands of identical lines a day. Consecutive duplicates are
        /// suppressed from the LOG only; every message is still fully
        /// processed.</summary>
        private (int Read, byte Cmd, ulong DevId) _lastRxLogged;

        private void HarvestAnnounce(int read)
        {
            if (read < HeaderSize) return;
            var buf = new byte[Math.Min(read, ReadBufferSize)];
            Marshal.Copy(_readBuffer, buf, 0, buf.Length);

            // Raw reception dump BEFORE any decode: what the interface
            // actually delivered (command + deviceId), the ground-truth
            // line that resolves the whole "map stays empty" question on
            // the next hardware run. Independent of the parser.
            ulong rawId = 0;
            for (int i = 0; i < 8; i++) rawId |= (ulong)buf[i] << (8 * i);
            bool rxLogged = _lastRxLogged != (read, buf[8], rawId);
            if (rxLogged)
            {
                _lastRxLogged = (read, buf[8], rawId);
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"GUIDELED rx len={read} cmd=0x{buf[8]:X2} devId=0x{rawId:X16}");
            }

            // Non-announce traffic (status heartbeats and other GIP
            // commands) is expected and already visible in the rx line's
            // cmd byte; it needs no line of its own.
            if (!TryParseAnnounce(buf, buf.Length,
                    out ulong deviceId, out ushort vid, out ushort pid, out ulong address))
                return;

            // A 0x01 acknowledge (or a 0x02 without an identity payload)
            // carries no VID/PID; keep the value we already learned for
            // this deviceId so a heartbeat never blanks a known identity.
            bool isAnnounce = buf[8] == GipCmdAnnounce;
            if (vid == 0 && pid == 0 && _announced.TryGetValue(deviceId, out var prior))
            {
                vid = prior.Vid; pid = prior.Pid;
                if (address == 0) address = prior.Address;
            }

            if (_announced.Count >= MaxAnnounceEntries && !_announced.ContainsKey(deviceId))
                EvictOldestAnnounce();
            _announced[deviceId] = (vid, pid, address, Environment.TickCount64);

            // A 0x02 announce means the pad (re)connected at firmware
            // default brightness, so the write ledger is stale: xbledctl
            // re-applies unconditionally on every arrival because the LED
            // state resets on unplug. Gated to the announce (not every
            // 0x01 heartbeat, which would force a redundant write per
            // beat) so a replug that reuses the deviceId still reseeds.
            if (isAnnounce) _lastWritten.Remove(deviceId);
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"GUIDELED announced devId=0x{deviceId:X16} vid=0x{vid:X4} pid=0x{pid:X4} announce={isAnnounce}");
        }

        private void EvictOldestAnnounce()
        {
            ulong oldestKey = 0;
            long oldestSeen = long.MaxValue;
            foreach (var kvp in _announced)
            {
                if (kvp.Value.LastSeen < oldestSeen)
                {
                    oldestSeen = kvp.Value.LastSeen;
                    oldestKey = kvp.Key;
                }
            }
            _announced.Remove(oldestKey);
            _lastWritten.Remove(oldestKey);
        }

        private void ProcessPending()
        {
            if (_pending.IsEmpty || _handle == InvalidHandle) return;

            foreach (var kvp in _pending)
            {
                var key = kvp.Key;
                var (percent, attempts) = kvp.Value;

                var matches = SelectWriteTargets(key, _announced, out bool fellBack);

                if (matches.Count == 0)
                {
                    // Diag once per (model, percent) request generation. An
                    // attempts==0 gate alone is defeated by the 30 s apply
                    // lanes, which reset attempts on every re-enqueue: a
                    // configured pad that never announces (Bluetooth) would
                    // re-log forever.
                    if (_pendLogged.Count > MaxPendingModels) _pendLogged.Clear();
                    if (_pendLogged.Add((key.Vid, key.Pid, percent)))
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"GUIDELED pend vid=0x{key.Vid:X4} pid=0x{key.Pid:X4} announced={_announced.Count} matches=0");
                    // Nothing announced at all yet (this branch is reachable
                    // only with an empty announce map, since the fallback
                    // fills matches otherwise; the explicit gate pins that
                    // invariant against future fallback changes, because
                    // re-provoking with announces present would only storm
                    // 0x02s from connected pads, invalidating their
                    // _lastWritten and forcing a redundant write per cycle).
                    // Re-provoke announces on a cooldown, then let the 1 s
                    // ticks retry until the attempt budget runs out. The
                    // cooldown matters because the ~2 s apply lanes reset
                    // attempts on every re-enqueue, and the IOCTL ADDS a
                    // caller context driver-side (xbledctl registers it
                    // exactly once); an announce-less rig must not re-add
                    // one every 2 s for the whole session.
                    long nowNudge = Environment.TickCount64;
                    if (attempts == 0 && _announced.Count == 0
                        && nowNudge - _lastReenumNudgeTick >= OpenRetryCooldownMs)
                    {
                        _lastReenumNudgeTick = nowNudge;
                        DeviceIoControl(_handle, GipReenumerateIoctl,
                            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                    }
                    if (attempts + 1 >= MaxAttempts)
                        _pending.TryRemove(new KeyValuePair<(ushort, ushort), (int, int)>(key, kvp.Value));
                    else
                        _pending.TryUpdate(key, (percent, attempts + 1), kvp.Value);
                    continue;
                }

                var (mode, intensity) = FromPercent(percent);
                bool allOk = true;
                foreach (ulong deviceId in matches)
                {
                    if (_lastWritten.TryGetValue(deviceId, out var last)
                        && last.Mode == mode && last.Intensity == intensity)
                        continue;

                    // Change detection passed, a real write goes out; the
                    // write itself logs its result. Note the routing here
                    // so a wrong-target write is attributable.
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"GUIDELED route vid=0x{key.Vid:X4} pid=0x{key.Pid:X4} devId=0x{deviceId:X16} fallback={fellBack} pct={percent}");

                    if (WriteLedPacket(deviceId, mode, intensity))
                    {
                        _lastWritten[deviceId] = (mode, intensity);
                    }
                    else
                    {
                        // Stale deviceId (unplugged mid-flight) or a broken
                        // handle. Drop the entry so the next announce pass
                        // rebuilds it, and keep the request for retry.
                        _announced.Remove(deviceId);
                        _lastWritten.Remove(deviceId);
                        allOk = false;
                    }
                }

                if (allOk)
                    _pending.TryRemove(new KeyValuePair<(ushort, ushort), (int, int)>(key, kvp.Value));
                else if (attempts + 1 >= MaxAttempts)
                    _pending.TryRemove(new KeyValuePair<(ushort, ushort), (int, int)>(key, kvp.Value));
                else
                    _pending.TryUpdate(key, (percent, attempts + 1), kvp.Value);
            }
        }

        /// <summary>Cancels any in-flight announce read before a write.
        /// xbledctl never writes with a read pending: discover_devices
        /// completes or cancels every ReadFile (CancelIo + event wait)
        /// before xbox_set_led runs. Matching that proven state keeps the
        /// driver interaction identical to the reference. The main loop
        /// reissues the read on its next iteration, and the reenumerate
        /// IOCTL re-provokes announces, so a message lost to the cancel
        /// window is recovered.</summary>
        private void EnsureNoPendingRead()
        {
            if (!_readPending) return;
            CancelIoEx(_handle, _readOverlapped);
            WaitForSingleObject(_readEvent, 100);
            _readPending = false;
        }

        private bool WriteLedPacket(ulong deviceId, byte mode, byte intensity)
        {
            EnsureNoPendingRead();
            byte[] pkt = BuildLedPacket(deviceId, _sequence, mode, intensity);
            // Sequence wraps 1-255, never 0 (xbledctl xbox_led.c).
            _sequence = (byte)((_sequence % 255) + 1);

            Marshal.Copy(pkt, 0, _writeBuffer, pkt.Length);
            ResetEvent(_writeEvent);
            ZeroOverlapped(_writeOverlapped, _writeEvent);

            bool ok = WriteFile(_handle, _writeBuffer, (uint)pkt.Length, out uint written, _writeOverlapped);
            if (!ok)
            {
                if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return false;
                // 2 s budget mirrors xbledctl xbox_set_led. Runs on the
                // worker thread only, never a caller.
                if (WaitForSingleObject(_writeEvent, 2000) != WAIT_OBJECT_0)
                {
                    CancelIoEx(_handle, _writeOverlapped);
                    WaitForSingleObject(_writeEvent, 100);
                    return false;
                }
                if (!GetOverlappedResult(_handle, _writeOverlapped, out written, false))
                {
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"GUIDELED write devId=0x{deviceId:X16} mode={mode} int={intensity} OVERLAPPED-FAIL gle={Marshal.GetLastWin32Error()}");
                    return false;
                }
            }
            bool wrote = written == pkt.Length;
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"GUIDELED write devId=0x{deviceId:X16} mode={mode} int={intensity} ok={wrote} written={written}");
            return wrote;
        }

        // OVERLAPPED: 2 pointers + 2 u32 + event handle.
        private static readonly int OverlappedSize = 2 * IntPtr.Size + 8 + IntPtr.Size;

        private static void ZeroOverlapped(IntPtr overlapped, IntPtr eventHandle)
        {
            for (int i = 0; i < OverlappedSize; i++)
                Marshal.WriteByte(overlapped, i, 0);
            Marshal.WriteIntPtr(overlapped, 2 * IntPtr.Size + 8, eventHandle);
        }
    }
}
