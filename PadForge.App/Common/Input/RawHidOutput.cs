using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Shared raw-HID output-report write, used by the per-vendor wheel/pedal
    /// FFB writers (Logitech / Fanatec / Thrustmaster). Overlapped
    /// <c>CreateFileW</c> + <c>WriteFile</c>, matching <see cref="SonyEffectWriter"/>'s
    /// proven plumbing (hidapi-equivalent flags). Bypasses SDL3 so the vendor
    /// custom HID protocols reach the device.
    /// </summary>
    internal static class RawHidOutput
    {
        /// <summary>Writes a raw output report (caller includes the leading
        /// report-ID byte). Returns false on open or write failure.</summary>
        public static bool Write(string devicePath, byte[] buf)
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

            // Windows HID requires the write buffer to be exactly the collection's
            // OutputReportByteLength. The per-vendor writers build the logical
            // report (report-ID byte + command bytes); pad/clamp it to the device's
            // actual length here. Without this, a wheel whose output reports are
            // longer than the command rejects every write with ERROR_INVALID_PARAMETER
            // (e.g. the G29 joystick collection wants 17 bytes; the lg4ff command is 8).
            byte[] outBuf = ResizeForDevice(devicePath, handle, buf);

            try
            {
                IntPtr ev = CreateEventW(IntPtr.Zero, true, false, null);
                if (ev == IntPtr.Zero) return false;
                try
                {
                    var ol = new OVERLAPPED { hEvent = ev };
                    bool ok = WriteFile(handle, outBuf, (uint)outBuf.Length, IntPtr.Zero, ref ol);
                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != ERROR_IO_PENDING) return false;
                        if (WaitForSingleObject(ev, 1000) != WAIT_OBJECT_0)
                        {
                            CancelIo(handle);
                            return false;
                        }
                    }
                    return GetOverlappedResult(handle, ref ol, out _, true);
                }
                finally { CloseHandle(ev); }
            }
            finally { CloseHandle(handle); }
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
                int need = QueryFeatureLen(devicePath, handle);
                byte[] outBuf = buf;
                if (need > buf.Length)
                {
                    outBuf = new byte[need];
                    Array.Copy(buf, 0, outBuf, 0, buf.Length);
                }

                if (stampSonyBtCrc && outBuf.Length >= 5)
                {
                    uint crc = 0xFFFFFFFFu;
                    crc = Crc32Step(crc, 0x53);
                    for (int i = 0; i < outBuf.Length - 4; i++)
                        crc = Crc32Step(crc, outBuf[i]);
                    crc = ~crc;
                    outBuf[outBuf.Length - 4] = (byte)crc;
                    outBuf[outBuf.Length - 3] = (byte)(crc >> 8);
                    outBuf[outBuf.Length - 2] = (byte)(crc >> 16);
                    outBuf[outBuf.Length - 1] = (byte)(crc >> 24);
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
        private static extern bool WriteFile(
            IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
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
