using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Enumerates Bluetooth-paired Wii Remotes present as HID device interfaces
    /// (issue #116). PadForge reads them directly rather than through SDL, so it
    /// needs to discover the raw HID interface path, product string, and serial
    /// itself. Walks the HID device-interface class with SetupAPI, keeps the
    /// Nintendo (VID 0x057E) gamepad PIDs, and confirms VID/PID with
    /// HidD_GetAttributes rather than trusting the path string.
    /// </summary>
    internal static class WiiHidEnumerator
    {
        private const ushort NintendoVendorId = 0x057E;
        private const ushort PidWiiRemote = 0x0306;     // Nintendo RVL-CNT-01
        private const ushort PidWiiRemotePlus = 0x0330; // Nintendo RVL-CNT-01-TR

        public readonly struct WiiHidInfo
        {
            public WiiHidInfo(string path, ushort productId, string name, string serial)
            {
                Path = path; ProductId = productId; Name = name; Serial = serial;
            }
            public string Path { get; }
            public ushort ProductId { get; }
            public string Name { get; }
            public string Serial { get; }
        }

        public static List<WiiHidInfo> Enumerate()
        {
            var result = new List<WiiHidInfo>();
            HidD_GetHidGuid(out Guid hidGuid);

            IntPtr devInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                DIGCF_DEVICEINTERFACE | DIGCF_PRESENT);
            if (devInfo == InvalidHandle) return result;

            try
            {
                var ifaceData = new SP_DEVICE_INTERFACE_DATA
                { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };

                for (uint i = 0; SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, i, ref ifaceData); i++)
                {
                    string path = GetInterfacePath(devInfo, ref ifaceData);
                    if (string.IsNullOrEmpty(path)) continue;

                    // Cheap pre-filter so we only open Nintendo candidates.
                    if (path.IndexOf("057e", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    IntPtr h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (h == IntPtr.Zero || h == InvalidHandle) continue;
                    try
                    {
                        var attr = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                        if (!HidD_GetAttributes(h, ref attr)) continue;
                        if (attr.VendorID != NintendoVendorId) continue;
                        if (attr.ProductID != PidWiiRemote && attr.ProductID != PidWiiRemotePlus) continue;

                        string name = GetHidString(h, HidD_GetProductString);
                        string serial = GetHidString(h, HidD_GetSerialNumberString);
                        result.Add(new WiiHidInfo(path, attr.ProductID, name, serial));
                    }
                    finally { CloseHandle(h); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(devInfo); }

            return result;
        }

        private static string GetInterfacePath(IntPtr devInfo, ref SP_DEVICE_INTERFACE_DATA ifaceData)
        {
            SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
            if (required == 0) return null;

            IntPtr detail = Marshal.AllocHGlobal((int)required);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: 8 on 64-bit, 6 on 32-bit.
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, detail, required, out _, IntPtr.Zero))
                    return null;
                // The DevicePath WCHAR array begins at offset 4 (after cbSize).
                return Marshal.PtrToStringUni(detail + 4);
            }
            finally { Marshal.FreeHGlobal(detail); }
        }

        private delegate bool HidStringFn(IntPtr h, byte[] buffer, uint bufferLen);

        private static string GetHidString(IntPtr h, HidStringFn fn)
        {
            var buf = new byte[256];
            if (!fn(h, buf, (uint)buf.Length)) return string.Empty;
            string s = Encoding.Unicode.GetString(buf);
            int nul = s.IndexOf('\0');
            return nul >= 0 ? s.Substring(0, nul) : s;
        }

        // ─────────────────────────────────────────────
        //  Interop
        // ─────────────────────────────────────────────

        private const uint DIGCF_PRESENT = 0x02;
        private const uint DIGCF_DEVICEINTERFACE = 0x10;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private static readonly IntPtr InvalidHandle = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public uint Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll")]
        private static extern bool HidD_GetAttributes(IntPtr hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        private static extern bool HidD_GetProductString(IntPtr hidDeviceObject, byte[] buffer, uint bufferLength);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        private static extern bool HidD_GetSerialNumberString(IntPtr hidDeviceObject, byte[] buffer, uint bufferLength);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator,
            IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
            ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
