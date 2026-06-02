using System;
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

            try
            {
                IntPtr ev = CreateEventW(IntPtr.Zero, true, false, null);
                if (ev == IntPtr.Zero) return false;
                try
                {
                    var ol = new OVERLAPPED { hEvent = ev };
                    bool ok = WriteFile(handle, buf, (uint)buf.Length, IntPtr.Zero, ref ol);
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
    }
}
