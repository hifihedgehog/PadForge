using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HIDMaestro;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Writes Sony effect packets (DualSense / DualShock 4) directly to a
    /// physical HID device. Pairs the v1.3.5 <see cref="HMOutputEncoder"/>
    /// data-driven encoder with a raw Win32 WriteFile path that bypasses
    /// SDL3 entirely. Mirrors what OpenRGB's SonyDualSenseController.cpp
    /// does via hidapi.
    ///
    /// <para>Why bypass SDL? SDL3's PS5 driver runs an internal state
    /// machine that fires its own UpdateEffects packets (player-index
    /// default color on SetDevicePlayerIndex, BT LED reset at ~10.2s
    /// post-connect, etc.). These race against SDL_SendGamepadEffect
    /// calls and can override user-supplied colors after a hot-plug or
    /// reconnect. Raw HID writes through a separate file handle cut SDL
    /// out of the loop — the firmware applies whichever WriteFile lands
    /// most recently, regardless of which process opened the handle.</para>
    ///
    /// <para>Open + write + close per call. ~1 ms overhead per write,
    /// acceptable for slider-drag cadence. Avoids handle staleness on
    /// device disconnect — if the device path is gone, CreateFile fails
    /// cleanly and we report failure rather than writing into a dead
    /// handle.</para>
    /// </summary>
    internal static class SonyEffectWriter
    {
        private const uint GENERIC_WRITE         = 0x40000000u;
        private const uint GENERIC_READ          = 0x80000000u;
        private const uint FILE_SHARE_READ       = 0x00000001u;
        private const uint FILE_SHARE_WRITE      = 0x00000002u;
        private const uint OPEN_EXISTING         = 3u;
        private const uint FILE_FLAG_OVERLAPPED  = 0x40000000u;
        private const uint WAIT_OBJECT_0         = 0u;
        private const int  ERROR_IO_PENDING      = 997;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [StructLayout(LayoutKind.Sequential)]
        private struct OVERLAPPED
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public uint OffsetLow;
            public uint OffsetHigh;
            public IntPtr hEvent;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(
            IntPtr hFile,
            byte[] lpBuffer,
            uint nNumberOfBytesToWrite,
            IntPtr lpNumberOfBytesWritten,
            ref OVERLAPPED lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(
            IntPtr hFile,
            ref OVERLAPPED lpOverlapped,
            out uint lpNumberOfBytesTransferred,
            bool bWait);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIo(IntPtr hFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>Heuristic for USB vs Bluetooth from a HID device path.
        /// USB: <c>\\?\HID#VID_054C&amp;PID_0CE6&amp;...</c>.
        /// Bluetooth: <c>\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&amp;0002054c_PID&amp;0ce6...</c>.
        /// The BT GATT HID-over-BT service UUID <c>0x1124</c> appears in
        /// every BT-paired HID's path; USB paths use the unbracketed
        /// <c>VID_</c>/<c>PID_</c> form.</summary>
        public static bool IsBluetoothPath(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            return devicePath.IndexOf("{00001124", StringComparison.OrdinalIgnoreCase) >= 0
                || devicePath.IndexOf("BTHENUM",   StringComparison.OrdinalIgnoreCase) >= 0
                || devicePath.IndexOf("_VID&",     StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Encodes <paramref name="fields"/> through
        /// <paramref name="profile"/>'s <c>extendedOutputReport</c> spec
        /// and writes the resulting bytes to the device at
        /// <paramref name="devicePath"/>. Returns true on success.
        /// CRC32 footers (BT) are computed by the encoder; the caller
        /// supplies semantic fields, never byte offsets.</summary>
        public static bool Write(
            string devicePath,
            HMProfile profile,
            IReadOnlyDictionary<string, object> fields)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            if (profile == null || fields == null) return false;
            if (!profile.HasExtendedOutput)
            {
                LastWriteDiag = $"profile '{profile.Id}' has no extendedOutputReport spec";
                return false;
            }

            byte[] packet;
            try
            {
                packet = HMOutputEncoder.Encode(profile, fields);
            }
            catch (Exception ex)
            {
                LastWriteDiag = $"Encode threw: {ex.GetType().Name} {ex.Message}";
                return false;
            }

            return WriteRaw(devicePath, packet);
        }

        /// <summary>Last write outcome — exposed for the dispatcher's
        /// per-write log line. Updated on every <see cref="Write"/> call.</summary>
        public static string LastWriteDiag { get; private set; } = "";

        private static bool WriteRaw(string devicePath, byte[] buf)
        {
            // hidapi (which OpenRGB uses) opens with FILE_FLAG_OVERLAPPED,
            // GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE.
            // Match that exactly.
            IntPtr handle = CreateFileW(
                devicePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (handle == IntPtr.Zero || handle == INVALID_HANDLE_VALUE)
            {
                LastWriteDiag = $"CreateFile failed err={Marshal.GetLastWin32Error()}";
                return false;
            }

            try
            {
                IntPtr ev = CreateEventW(IntPtr.Zero, true, false, null);
                if (ev == IntPtr.Zero)
                {
                    LastWriteDiag = $"CreateEvent failed err={Marshal.GetLastWin32Error()}";
                    return false;
                }

                try
                {
                    var ol = new OVERLAPPED { hEvent = ev };
                    bool ok = WriteFile(handle, buf, (uint)buf.Length, IntPtr.Zero, ref ol);
                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != ERROR_IO_PENDING)
                        {
                            LastWriteDiag = $"WriteFile failed err={err}";
                            return false;
                        }
                        if (WaitForSingleObject(ev, 1000) != WAIT_OBJECT_0)
                        {
                            CancelIo(handle);
                            LastWriteDiag = "WriteFile timed out 1s";
                            return false;
                        }
                    }
                    bool gor = GetOverlappedResult(handle, ref ol, out uint bytes, true);
                    LastWriteDiag = gor
                        ? $"WriteFile ok bytes={bytes}"
                        : $"GetOverlappedResult failed err={Marshal.GetLastWin32Error()}";
                    return gor;
                }
                finally
                {
                    CloseHandle(ev);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }
}
