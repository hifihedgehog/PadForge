using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Shared raw-HID output-report write, used by the per-vendor wheel/pedal
    /// FFB writers (Logitech / Fanatec / Thrustmaster). Overlapped
    /// <c>CreateFileW</c> + <c>WriteFile</c>, matching <see cref="PlayStationEffectWriter"/>'s
    /// proven plumbing (hidapi-equivalent flags). Bypasses SDL3 so the vendor
    /// custom HID protocols reach the device.
    /// </summary>
    internal static class RawHidOutput
    {
        /// <summary>Per-path cached device handle + manual-reset event
        /// (the PlayStationEffectWriter CachedIo shape). Fanatec pedal rumble
        /// writes at up to poll rate, and the old open-per-write shape
        /// paid CreateFile + CreateEvent + two CloseHandle per frame.
        /// FALLBACK-SAFE: any cached-handle failure closes the cache and
        /// re-runs the exact legacy open-write for the SAME write. The
        /// per-entry gate serializes same-path writers.</summary>
        private sealed class CachedIo
        {
            public IntPtr Handle;
            public IntPtr Event;
            public byte[] Sized;   // per-path resize scratch (serialized by Gate)
            public readonly object Gate = new();
        }
        private static readonly ConcurrentDictionary<string, CachedIo> s_ioCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Writes a raw output report (caller includes the leading
        /// report-ID byte). Returns false on open or write failure.</summary>
        public static bool Write(string devicePath, byte[] buf)
        {
            if (string.IsNullOrEmpty(devicePath) || buf == null || buf.Length == 0)
                return false;

            var io = s_ioCache.GetOrAdd(devicePath, _ => new CachedIo());
            lock (io.Gate)
            {
                if (io.Handle != IntPtr.Zero)
                {
                    byte[] cachedBuf = SizeForCached(io, devicePath, buf);
                    if (WriteOnHandle(io.Handle, io.Event, cachedBuf)) return true;
                    // Stale handle (sleep, replug): drop the cache and fall
                    // through to the legacy path for THIS write.
                    CloseHandle(io.Event);
                    CloseHandle(io.Handle);
                    io.Handle = IntPtr.Zero;
                    io.Event = IntPtr.Zero;
                }

                IntPtr handle = CreateFileW(
                    devicePath,
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_FLAG_OVERLAPPED,
                    IntPtr.Zero);

                if (handle == IntPtr.Zero || handle == INVALID_HANDLE_VALUE) return false;

                // Windows HID requires the write buffer to be exactly the collection's
                // OutputReportByteLength. The per-vendor writers build the logical
                // report (report-ID byte + command bytes); pad/clamp it to the device's
                // actual length here. Without this, a wheel whose output reports are
                // longer than the command rejects every write with ERROR_INVALID_PARAMETER
                // (e.g. the G29 joystick collection wants 17 bytes; the lg4ff command is 8).
                byte[] outBuf = ResizeForDevice(devicePath, handle, buf);

                IntPtr ev = CreateEventW(IntPtr.Zero, true, false, null);
                if (ev == IntPtr.Zero) { CloseHandle(handle); return false; }

                bool ok = WriteOnHandle(handle, ev, outBuf);
                if (ok)
                {
                    io.Handle = handle;
                    io.Event = ev;
                    return true;
                }
                CloseHandle(ev);
                CloseHandle(handle);
                return false;
            }
        }

        /// <summary>Pads <paramref name="buf"/> to the cached path's known
        /// OutputReportByteLength using the per-path scratch, zeroing the
        /// pad tail each call (same zero-pad contract as ResizeForDevice).
        /// Falls back to buf as-is when no length is cached, matching the
        /// legacy path's behavior for caps-query-unavailable devices.</summary>
        private static byte[] SizeForCached(CachedIo io, string devicePath, byte[] buf)
        {
            if (!_outLen.TryGetValue(devicePath, out int need) || need <= 0 || need <= buf.Length)
                return buf;
            if (io.Sized == null || io.Sized.Length != need)
                io.Sized = new byte[need];
            Array.Copy(buf, 0, io.Sized, 0, buf.Length);
            Array.Clear(io.Sized, buf.Length, need - buf.Length);
            return io.Sized;
        }

        private static bool WriteOnHandle(IntPtr handle, IntPtr ev, byte[] outBuf)
        {
            // Manual-reset event: clear before reuse.
            ResetEvent(ev);
            // Pin held until every path below has passed its unbounded
            // GetOverlappedResult (or established no I/O is pending), so
            // the kernel never reads a moved buffer mid-write
            // (PlayStationEffectWriter.WriteRaw pattern).
            var pin = GCHandle.Alloc(outBuf, GCHandleType.Pinned);
            try
            {
                var ol = new OVERLAPPED { hEvent = ev };
                bool ok = WriteFile(handle, pin.AddrOfPinnedObject(), (uint)outBuf.Length, IntPtr.Zero, ref ol);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ERROR_IO_PENDING) return false;
                    if (WaitForSingleObject(ev, 1000) != WAIT_OBJECT_0)
                    {
                        // CancelIo only REQUESTS abort; `ol` is a stack local
                        // and `outBuf` unpins in the finally, so block until
                        // the cancelled I/O actually completes before
                        // unwinding (PlayStationEffectWriter drain).
                        CancelIo(handle);
                        GetOverlappedResult(handle, ref ol, out _, true);
                        return false;
                    }
                }
                return GetOverlappedResult(handle, ref ol, out _, true);
            }
            finally { pin.Free(); }
        }

        /// <summary>Sends a feature report (caller includes the leading
        /// report-ID byte). The buffer is zero-padded to the collection's
        /// FeatureReportByteLength — HidD_SetFeature rejects other sizes the
        /// same way WriteFile rejects wrong OutputReportByteLength. When
        /// <paramref name="stampSonyBtCrc"/> is set, the last four bytes of
        /// the sized buffer receive the Sony BT feature-report CRC32 (seed
        /// byte 0x53 + report bytes, little-endian) — per dualsense-tester's
        /// crc32.util.ts fillFeatureReportChecksum.</summary>
        public static bool SetFeature(string devicePath, byte[] buf, bool stampSonyBtCrc)
        {
            if (string.IsNullOrEmpty(devicePath) || buf == null || buf.Length == 0)
                return false;

            IntPtr handle = CreateFileW(
                devicePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (handle == IntPtr.Zero || handle == INVALID_HANDLE_VALUE) return false;

            try
            {
                // Sony BT firmware checks the CRC32 at the LOGICAL report's
                // last four bytes — i.e. at the end of the report as the
                // descriptor declares it, BEFORE any padding to the
                // collection's FeatureReportByteLength. dualsense-tester's
                // fillFeatureReportChecksum proves the offset: it stamps at
                // reportData[len-4] of the descriptor-sized buffer and lets
                // the HID stack pad afterwards. Stamping after padding put
                // the CRC in the pad bytes whenever caps exceed the report
                // size, and the pad silently dropped every forwarded
                // command (the wired-virtual ds.daidr.me symptom).
                if (stampSonyBtCrc && buf.Length >= 5)
                {
                    uint crc = 0xFFFFFFFFu;
                    crc = Crc32Step(crc, 0x53);
                    for (int i = 0; i < buf.Length - 4; i++)
                        crc = Crc32Step(crc, buf[i]);
                    crc = ~crc;
                    buf[buf.Length - 4] = (byte)crc;
                    buf[buf.Length - 3] = (byte)(crc >> 8);
                    buf[buf.Length - 2] = (byte)(crc >> 16);
                    buf[buf.Length - 1] = (byte)(crc >> 24);
                }

                int need = QueryFeatureLen(devicePath, handle);
                byte[] outBuf = buf;
                if (need > buf.Length)
                {
                    outBuf = new byte[need];
                    Array.Copy(buf, 0, outBuf, 0, buf.Length);
                }

                return HidD_SetFeature(handle, outBuf, (uint)outBuf.Length);
            }
            finally { CloseHandle(handle); }
        }

        private static uint Crc32Step(uint crc, byte b)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(crc & 1)));
            return crc;
        }

        // Cached per-device OutputReportByteLength so the caps lookup runs once per
        // path rather than on every FFB frame.
        private static readonly ConcurrentDictionary<string, int> _outLen = new();
        private static readonly ConcurrentDictionary<string, int> _featLen = new();

        /// <summary>Forgets the cached report lengths for a device. Called on
        /// unplug so a different device later on the same path re-queries its
        /// caps instead of reusing stale ones.</summary>
        public static void ResetDevice(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return;
            _outLen.TryRemove(devicePath, out _);
            _featLen.TryRemove(devicePath, out _);
            if (s_ioCache.TryRemove(devicePath, out var io))
            {
                lock (io.Gate)
                {
                    if (io.Event != IntPtr.Zero) CloseHandle(io.Event);
                    if (io.Handle != IntPtr.Zero) CloseHandle(io.Handle);
                    io.Handle = IntPtr.Zero;
                    io.Event = IntPtr.Zero;
                }
            }
        }

        private static int QueryFeatureLen(string devicePath, IntPtr handle)
        {
            if (_featLen.TryGetValue(devicePath, out int n)) return n;
            if (!HidD_GetPreparsedData(handle, out IntPtr pp) || pp == IntPtr.Zero) return 0;
            try
            {
                if (HidP_GetCaps(pp, out HIDP_CAPS caps) < 0) return 0;
                if (caps.FeatureReportByteLength > 0)
                    _featLen[devicePath] = caps.FeatureReportByteLength;
                return caps.FeatureReportByteLength;
            }
            finally { HidD_FreePreparsedData(pp); }
        }

        // Returns buf sized to the device's OutputReportByteLength (zero-padded, or
        // clamped if the command is somehow longer). Falls back to buf unchanged
        // when the caps query is unavailable.
        private static byte[] ResizeForDevice(string devicePath, IntPtr handle, byte[] buf)
        {
            if (!_outLen.TryGetValue(devicePath, out int need))
            {
                need = QueryOutputLen(handle);
                if (need > 0) _outLen[devicePath] = need;
            }
            // Pad up only, never truncate - mirrors hidapi hid_write (windows/hid.c):
            // a buffer already >= the device report length is sent as-is.
            if (need <= 0 || need <= buf.Length) return buf;
            byte[] sized = new byte[need];
            Array.Copy(buf, 0, sized, 0, buf.Length);
            return sized;
        }

        private static int QueryOutputLen(IntPtr handle)
        {
            if (!HidD_GetPreparsedData(handle, out IntPtr pp) || pp == IntPtr.Zero) return 0;
            try
            {
                if (HidP_GetCaps(pp, out HIDP_CAPS caps) < 0) return 0;
                return caps.OutputReportByteLength;
            }
            finally { HidD_FreePreparsedData(pp); }
        }

        private const uint GENERIC_WRITE        = 0x40000000u;
        private const uint GENERIC_READ         = 0x80000000u;
        private const uint FILE_SHARE_READ      = 0x00000001u;
        private const uint FILE_SHARE_WRITE     = 0x00000002u;
        private const uint OPEN_EXISTING        = 3u;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000u;
        private const uint WAIT_OBJECT_0        = 0u;
        private const int  ERROR_IO_PENDING     = 997;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct OVERLAPPED
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public uint OffsetLow;
            public uint OffsetHigh;
            public IntPtr hEvent;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        // IntPtr buffer, not byte[]: the marshaler's automatic pin ends when
        // WriteFile returns ERROR_IO_PENDING, leaving the kernel reading a
        // movable managed array for the pending window (PlayStationEffectWriter has
        // the same declaration for the same reason).
        private static extern bool WriteFile(
            IntPtr hFile, IntPtr lpBuffer, uint nNumberOfBytesToWrite,
            IntPtr lpNumberOfBytesWritten, ref OVERLAPPED lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(
            IntPtr hFile, ref OVERLAPPED lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIo(IntPtr hFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

        [DllImport("kernel32.dll")]
        private static extern bool ResetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("hid.dll")]
        private static extern bool HidD_SetFeature(IntPtr hidDeviceObject, byte[] reportBuffer, uint reportBufferLength);

        [DllImport("hid.dll")]
        private static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll")]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

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
    }
}
