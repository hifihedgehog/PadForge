using System;
using System.Runtime.InteropServices;

namespace PadForge.Common
{
    /// <summary>
    /// Shared SetupAPI P/Invoke declarations and structs used by
    /// HidHideController. The vJoy-specific PowerShell snippet generator
    /// was deleted in v3 along with the live vJoy install path; the
    /// DriverInstaller.UninstallVJoy method retains its own bundled
    /// PowerShell uninstall script for the v3 cleanup wizard.
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
    }
}
