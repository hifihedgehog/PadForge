using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Common
{
    /// <summary>
    /// Provides direct access to the HidHide control device via P/Invoke IOCTLs.
    /// Manages device blacklisting, application whitelisting, and cloaking state.
    ///
    /// Buffer format for GET/SET operations: Multi-SZ (null-separated UTF-16 strings,
    /// double-null terminated). SET operations replace the entire list.
    /// </summary>
    public static class HidHideController
    {
        // ─────────────────────────────────────────────
        //  IOCTL codes
        // ─────────────────────────────────────────────

        private const uint IOCTL_GET_WHITELIST = 0x80016000;
        private const uint IOCTL_SET_WHITELIST = 0x80016004;
        private const uint IOCTL_GET_BLACKLIST = 0x80016008;
        private const uint IOCTL_SET_BLACKLIST = 0x8001600C;
        private const uint IOCTL_GET_ACTIVE    = 0x80016010;
        private const uint IOCTL_SET_ACTIVE    = 0x80016014;

        private const string DevicePath = @"\\.\HidHide";

        // ─────────────────────────────────────────────
        //  P/Invoke
        // ─────────────────────────────────────────────

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            byte[] lpInBuffer,
            int nInBufferSize,
            byte[] lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint QueryDosDeviceW(
            string lpDeviceName,
            [Out] char[] lpTargetPath,
            uint ucchMax);

        // SetupAPI for enumerating HID devices by VID/PID.
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(
            ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInfo(
            IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInstanceIdW(
            IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
            char[] DeviceInstanceId, uint DeviceInstanceIdSize, out uint RequiredSize);

        [DllImport("setupapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        private static readonly Guid GUID_DEVCLASS_HIDCLASS = new("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
        private const uint DIGCF_PRESENT = 0x02;

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        // ─────────────────────────────────────────────
        //  Tracking: device IDs managed by PadForge
        // ─────────────────────────────────────────────

        /// <summary>
        /// Set of device instance IDs that PadForge has added to the HidHide blacklist.
        /// Used during cleanup to remove only our entries, not those added by other tools.
        /// </summary>
        private static readonly HashSet<string> _managedDeviceIds = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new();

        // ─────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────

        /// <summary>
        /// Returns true if the HidHide control device can be opened.
        /// </summary>
        public static bool IsAvailable()
        {
            try
            {
                using var handle = OpenDevice();
                return handle != null && !handle.IsInvalid;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the current device blacklist (device instance IDs).
        /// </summary>
        public static List<string> GetBlacklist()
        {
            return GetMultiSzList(IOCTL_GET_BLACKLIST);
        }

        /// <summary>
        /// Replaces the entire device blacklist.
        /// </summary>
        public static void SetBlacklist(List<string> instanceIds)
        {
            SetMultiSzList(IOCTL_SET_BLACKLIST, instanceIds);
        }

        /// <summary>
        /// Adds a device instance ID to the blacklist if not already present.
        /// </summary>
        public static void AddToBlacklist(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return;

            lock (_lock)
            {
                var list = GetBlacklist();
                if (!list.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(instanceId);
                    SetBlacklist(list);
                }
                _managedDeviceIds.Add(instanceId);
            }
        }

        /// <summary>
        /// Removes a device instance ID from the blacklist.
        /// </summary>
        public static void RemoveFromBlacklist(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return;

            lock (_lock)
            {
                var list = GetBlacklist();
                list.RemoveAll(id => string.Equals(id, instanceId, StringComparison.OrdinalIgnoreCase));
                SetBlacklist(list);
                _managedDeviceIds.Remove(instanceId);
            }
        }

        /// <summary>
        /// Gets the current application whitelist (DOS device paths).
        /// </summary>
        public static List<string> GetWhitelist()
        {
            return GetMultiSzList(IOCTL_GET_WHITELIST);
        }

        /// <summary>
        /// Replaces the entire application whitelist.
        /// </summary>
        public static void SetWhitelist(List<string> paths)
        {
            SetMultiSzList(IOCTL_SET_WHITELIST, paths);
        }

        /// <summary>
        /// Ensures the given application path is in the whitelist.
        /// Converts the path to DOS device format (\Device\HarddiskVolumeN\...) first.
        /// </summary>
        public static void EnsureWhitelisted(string appPath)
        {
            if (string.IsNullOrEmpty(appPath)) return;

            string dosPath = ToDosDevicePath(appPath);
            if (dosPath == null) return;

            var list = GetWhitelist();
            if (!list.Contains(dosPath, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(dosPath);
                SetWhitelist(list);
            }
        }

        /// <summary>
        /// Gets whether cloaking (device hiding) is currently active.
        /// </summary>
        public static bool GetActive()
        {
            using var handle = OpenDevice();
            if (handle == null || handle.IsInvalid) return false;

            byte[] outBuffer = new byte[1];
            if (!DeviceIoControl(handle, IOCTL_GET_ACTIVE, null, 0,
                outBuffer, outBuffer.Length, out _, IntPtr.Zero))
                return false;

            return outBuffer[0] != 0;
        }

        /// <summary>
        /// Enables or disables cloaking (device hiding).
        /// </summary>
        public static void SetActive(bool active)
        {
            using var handle = OpenDevice();
            if (handle == null || handle.IsInvalid) return;

            byte[] inBuffer = new byte[] { active ? (byte)1 : (byte)0 };
            DeviceIoControl(handle, IOCTL_SET_ACTIVE, inBuffer, inBuffer.Length,
                null, 0, out _, IntPtr.Zero);
        }

        /// <summary>
        /// Removes all device IDs that PadForge previously added to the blacklist.
        /// Leaves entries added by other tools untouched.
        /// </summary>
        public static void RemoveManagedDevices()
        {
            lock (_lock)
            {
                if (_managedDeviceIds.Count == 0) return;

                var list = GetBlacklist();
                list.RemoveAll(id => _managedDeviceIds.Contains(id));
                SetBlacklist(list);
                _managedDeviceIds.Clear();
            }
        }

        /// <summary>
        /// Finds all present HID device instance IDs matching the given VID/PID.
        /// Used as a fallback when a device has a synthetic path (e.g., XInput#0)
        /// that can't be converted to a valid instance ID.
        /// </summary>
        public static List<string> FindInstanceIdsByVidPid(ushort vendorId, ushort productId)
        {
            var result = new List<string>();
            string vidPid = $"VID_{vendorId:X4}&PID_{productId:X4}";

            var guid = GUID_DEVCLASS_HIDCLASS;
            IntPtr devInfoSet = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
            if (devInfoSet == (IntPtr)(-1)) return result;

            try
            {
                var devInfoData = new SP_DEVINFO_DATA();
                devInfoData.cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>();

                for (uint i = 0; SetupDiEnumDeviceInfo(devInfoSet, i, ref devInfoData); i++)
                {
                    char[] buffer = new char[512];
                    if (SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, buffer, (uint)buffer.Length, out _))
                    {
                        int nullIdx = Array.IndexOf(buffer, '\0');
                        string instanceId = nullIdx >= 0 ? new string(buffer, 0, nullIdx) : new string(buffer);
                        if (instanceId.Contains(vidPid, StringComparison.OrdinalIgnoreCase))
                            result.Add(instanceId);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }

            return result;
        }

        /// <summary>
        /// Converts a device path (\\?\HID#VID_...) to a PnP device instance ID
        /// (HID\VID_...\...) suitable for the HidHide blacklist.
        /// </summary>
        public static string DevicePathToInstanceId(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath))
                return null;

            string path = devicePath;

            // Strip \\?\ prefix.
            if (path.StartsWith(@"\\?\"))
                path = path.Substring(4);

            // Remove device interface GUID suffix ({...}).
            int guidIdx = path.LastIndexOf('{');
            if (guidIdx > 0)
                path = path.Substring(0, guidIdx);

            // Replace # with \ (device path uses # as separator).
            path = path.Replace('#', '\\');
            path = path.TrimEnd('\\');

            return string.IsNullOrEmpty(path) ? null : path;
        }

        // ─────────────────────────────────────────────
        //  Private helpers
        // ─────────────────────────────────────────────

        private static SafeFileHandle OpenDevice()
        {
            var handle = CreateFileW(
                DevicePath,
                GENERIC_READ | GENERIC_WRITE,
                0, // No sharing
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }

            return handle;
        }

        /// <summary>
        /// Reads a multi-SZ string list from HidHide via a GET IOCTL.
        /// </summary>
        private static List<string> GetMultiSzList(uint ioctl)
        {
            var result = new List<string>();

            using var handle = OpenDevice();
            if (handle == null || handle.IsInvalid) return result;

            // First call with small buffer to get required size.
            byte[] outBuffer = new byte[4096];
            if (!DeviceIoControl(handle, ioctl, null, 0,
                outBuffer, outBuffer.Length, out int bytesReturned, IntPtr.Zero))
            {
                // Try larger buffer on failure.
                outBuffer = new byte[65536];
                if (!DeviceIoControl(handle, ioctl, null, 0,
                    outBuffer, outBuffer.Length, out bytesReturned, IntPtr.Zero))
                    return result;
            }

            if (bytesReturned < 4) // At minimum double-null terminator (4 bytes in UTF-16)
                return result;

            // Parse multi-SZ: null-separated UTF-16 strings, double-null terminated.
            string fullString = Encoding.Unicode.GetString(outBuffer, 0, bytesReturned);

            // Split on null characters, filter empty entries.
            foreach (string entry in fullString.Split('\0'))
            {
                if (!string.IsNullOrEmpty(entry))
                    result.Add(entry);
            }

            return result;
        }

        /// <summary>
        /// Writes a multi-SZ string list to HidHide via a SET IOCTL.
        /// </summary>
        private static void SetMultiSzList(uint ioctl, List<string> entries)
        {
            using var handle = OpenDevice();
            if (handle == null || handle.IsInvalid) return;

            // Build multi-SZ buffer: each string null-terminated, plus final null.
            var sb = new StringBuilder();
            foreach (string entry in entries)
            {
                if (!string.IsNullOrEmpty(entry))
                {
                    sb.Append(entry);
                    sb.Append('\0');
                }
            }
            sb.Append('\0'); // Double-null terminator.

            byte[] inBuffer = Encoding.Unicode.GetBytes(sb.ToString());
            DeviceIoControl(handle, ioctl, inBuffer, inBuffer.Length,
                null, 0, out _, IntPtr.Zero);
        }

        /// <summary>
        /// Converts a regular file path (C:\PadForge\PadForge.exe) to DOS device
        /// path format (\Device\HarddiskVolumeN\PadForge\PadForge.exe) for the
        /// HidHide whitelist.
        /// </summary>
        private static string ToDosDevicePath(string filePath)
        {
            try
            {
                string fullPath = Path.GetFullPath(filePath);
                string drive = Path.GetPathRoot(fullPath);
                if (string.IsNullOrEmpty(drive)) return null;

                // Get the drive letter without trailing backslash (e.g., "C:")
                string driveLetter = drive.TrimEnd('\\');

                // Query the DOS device name for this drive letter.
                char[] buffer = new char[512];
                uint result = QueryDosDeviceW(driveLetter, buffer, (uint)buffer.Length);
                if (result == 0) return null;

                // QueryDosDevice returns a multi-SZ; take the first entry.
                string dosDevice = new string(buffer, 0, Array.IndexOf(buffer, '\0'));

                // Build full DOS path: \Device\HarddiskVolumeN + \rest\of\path
                string relativePath = fullPath.Substring(drive.Length);
                return dosDevice + @"\" + relativePath;
            }
            catch
            {
                return null;
            }
        }
    }
}
