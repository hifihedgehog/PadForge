using System;
using System.Runtime.InteropServices;

namespace PadForge.Common
{
    /// <summary>
    /// Shared SetupAPI P/Invoke declarations and structs used by
    /// HidHideController. The Extended-specific PowerShell snippet generator
    /// was deleted in v3 along with the live Extended install path; the
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

        private const int DIGCF_ALLCLASSES = 0x4;
        private const int DIGCF_PRESENT = 0x2;

        /// <summary>True while any PRESENT devnode's instance ID contains
        /// HIDMAESTRO. The startup orphan sweep's ordering barrier (HM#38):
        /// RemoveAllVirtualControllers returns before PnP removal completes,
        /// so callers that must not race an in-flight removal poll this until
        /// it goes false (bounded).</summary>
        internal static bool AnyPresentHidMaestroDevice()
        {
            Guid any = Guid.Empty;
            IntPtr devs = SetupDiGetClassDevsW(ref any, null, IntPtr.Zero,
                DIGCF_ALLCLASSES | DIGCF_PRESENT);
            if (devs == IntPtr.Zero || devs == new IntPtr(-1)) return false;
            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                var buf = new char[512];
                for (int i = 0; SetupDiEnumDeviceInfo(devs, i, ref data); i++)
                {
                    if (!SetupDiGetDeviceInstanceIdW(devs, ref data, buf, buf.Length, out int len))
                        continue;
                    var id = new string(buf, 0, Math.Max(0, len - 1));
                    if (id.IndexOf("HIDMAESTRO", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            finally { SetupDiDestroyDeviceInfoList(devs); }
            return false;
        }
    }
}
