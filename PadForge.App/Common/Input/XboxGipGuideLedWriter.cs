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

        /// <summary>Parses one \\.\XboxGIP message as a device announce.
        /// Returns true only for commandId 0x02 messages carrying at least
        /// the identity prefix of gip_pkt_announce (xone protocol.c):
        /// address[6] + le16 unknown + le16 vendor_id + le16 product_id,
        /// i.e. VID at payload offset 8 and PID at offset 10. Acknowledge
        /// messages (0x01) carry a deviceId but no VID/PID, so they cannot
        /// feed the match map and are ignored here.</summary>
        internal static bool TryParseAnnounce(byte[] buf, int read,
            out ulong deviceId, out ushort vendorId, out ushort productId, out ulong address)
        {
            deviceId = 0; vendorId = 0; productId = 0; address = 0;
            if (buf == null || read < HeaderSize) return false;

            for (int i = 0; i < 8; i++)
                deviceId |= (ulong)buf[i] << (8 * i);

            if (buf[8] != GipCmdAnnounce) return false;

            uint payloadLen = (uint)(buf[12] | buf[13] << 8 | buf[14] << 16 | buf[15] << 24);
            if (payloadLen < 12 || read < HeaderSize + 12) return false;

            for (int i = 0; i < 6; i++)
                address |= (ulong)buf[HeaderSize + i] << (8 * i);
            vendorId = (ushort)(buf[HeaderSize + 8] | buf[HeaderSize + 9] << 8);
            productId = (ushort)(buf[HeaderSize + 10] | buf[HeaderSize + 11] << 8);
            return true;
        }

        /// <summary>True when the device rides Windows' XInput lane
        /// (SDL's synthetic "XInput#N" path, SDL_xinputjoystick.c) AND is
        /// a GIP-family pad (One / Elite / Series, the impulse-trigger
        /// PID set that mirrors controller_list.h's XBoxOneController
        /// class). The path check alone over-matched: SDL hands XInput#N
        /// to every XInput userid, so Xbox 360 and third-party XUSB pads
        /// got a fully interactive card whose writes could never match a
        /// GIP announce (xbledctl RESEARCH.md section 9: XUSB speaks only
        /// the XInput protocol). Bluetooth GIP pads still pass the PID
        /// gate but announce nothing, the USB-only case the card's
        /// subtitle discloses.</summary>
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

                _pending[(ud.VendorId, ud.ProdId)] = (Math.Clamp(percent0to100, 0, 100), 0);
                _work.Set();
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
            if (_handle == InvalidHandle) return false;

            DeviceIoControl(_handle, GipReenumerateIoctl,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            _readPending = false;
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

        private void HarvestAnnounce(int read)
        {
            if (read < HeaderSize) return;
            var buf = new byte[Math.Min(read, ReadBufferSize)];
            Marshal.Copy(_readBuffer, buf, 0, buf.Length);

            if (!TryParseAnnounce(buf, buf.Length,
                    out ulong deviceId, out ushort vid, out ushort pid, out ulong address))
            {
                // Acknowledge (0x01) refreshes liveness of a known entry
                // but cannot create one, since it carries no VID/PID.
                if (buf[8] == GipCmdAcknowledge
                    && _announced.TryGetValue(GetDeviceId(buf), out var known))
                {
                    _announced[GetDeviceId(buf)] = (known.Vid, known.Pid, known.Address, Environment.TickCount64);
                }
                return;
            }

            if (_announced.Count >= MaxAnnounceEntries && !_announced.ContainsKey(deviceId))
                EvictOldestAnnounce();
            _announced[deviceId] = (vid, pid, address, Environment.TickCount64);
            // A fresh announce means the pad (re)connected at firmware
            // default brightness, so the write ledger is stale by
            // definition: xbledctl re-applies unconditionally on every
            // arrival (main.cpp WM_DEVICECHANGE -> TryAutoApply) because
            // the LED state resets on unplug. Without this, a replug that
            // reuses the deviceId skipped the reseed and the pad stayed
            // at firmware default.
            _lastWritten.Remove(deviceId);

            static ulong GetDeviceId(byte[] b)
            {
                ulong id = 0;
                for (int i = 0; i < 8; i++) id |= (ulong)b[i] << (8 * i);
                return id;
            }
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

                var matches = new List<ulong>();
                foreach (var a in _announced)
                    if (a.Value.Vid == key.Vid && a.Value.Pid == key.Pid)
                        matches.Add(a.Key);

                if (matches.Count == 0)
                {
                    // Nothing announced for this model yet. Re-provoke
                    // announces once, then let the 1 s ticks retry until
                    // the attempt budget runs out (a Bluetooth Xbox pad
                    // legitimately never announces).
                    if (attempts == 0)
                        DeviceIoControl(_handle, GipReenumerateIoctl,
                            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
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
                if (!GetOverlappedResult(_handle, _writeOverlapped, out written, false)) return false;
            }
            return written == pkt.Length;
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
