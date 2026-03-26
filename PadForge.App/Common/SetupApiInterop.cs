using System;
using System.Runtime.InteropServices;

namespace PadForge.Common
{
    /// <summary>
    /// Shared SetupAPI P/Invoke declarations and structs used by
    /// HidHideController, VJoyVirtualController, and DriverInstaller.
    /// </summary>
    internal static class SetupApiInterop
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr SetupDiGetClassDevsW(
            ref Guid ClassGuid, string Enumerator, IntPtr hwndParent, int Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiEnumDeviceInfo(
            IntPtr DeviceInfoSet, int MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool SetupDiGetDeviceInstanceIdW(
            IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
            char[] DeviceInstanceId, int DeviceInstanceIdSize, out int RequiredSize);

        [DllImport("setupapi.dll")]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        /// <summary>
        /// Inline C# type definition for PowerShell Add-Type scripts that need
        /// SetupAPI device node creation. Shared between DriverInstaller and
        /// VJoyVirtualController to avoid duplication. Contains the raw C# source
        /// with proper braces/quotes — use <see cref="GetPsSetupApiSnippet"/> to
        /// get a version safe for C# interpolated strings.
        /// </summary>
        internal static string GetPsSetupApiSnippet()
        {
            return @"    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PF_SetupApi {
    public const int DIF_REGISTERDEVICE = 0x19;
    public const int SPDRP_HARDWAREID = 0x01;
    public const int DICD_GENERATE_ID = 0x01;
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
    [DllImport(""setupapi.dll"", SetLastError = true)]
    public static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);
    [DllImport(""setupapi.dll"", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiCreateDeviceInfoW(IntPtr DeviceInfoSet, string DeviceName, ref Guid ClassGuid, string DeviceDescription, IntPtr hwndParent, int CreationFlags, ref SP_DEVINFO_DATA DeviceInfoData);
    [DllImport(""setupapi.dll"", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, int Property, byte[] PropertyBuffer, int PropertyBufferSize);
    [DllImport(""setupapi.dll"", SetLastError = true)]
    public static extern bool SetupDiCallClassInstaller(int InstallFunction, IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData);
    [DllImport(""setupapi.dll"", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
    [DllImport(""newdev.dll"", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string HardwareId, string FullInfPath, int InstallFlags, out bool bRebootRequired);
}
'@";
        }
    }
}
