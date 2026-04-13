using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PadForge.Engine
{
    /// <summary>
    /// Looks up a stable physical-device PnP instance ID for an XInput-backed
    /// Xbox controller given its VID/PID. Used by <see cref="SdlDeviceWrapper.BuildInstanceGuid"/>
    /// when SDL reports a synthetic "XInput#N" path — the slot number in
    /// those paths is not stable (xinputhid can reshuffle slots when a
    /// second Xbox-VID device appears), so mapping persistent device
    /// identity to SDL's slot number breaks on reshuffle.
    ///
    /// A real Xbox controller's underlying HID instance ID is stable per
    /// physical device (it contains the BT MAC for Bluetooth devices and a
    /// USB hub/port address for wired devices). HIDMaestro virtual devices
    /// are filtered by checking for the "HIDMAESTRO" / "HMCOMPANION" /
    /// "HMXINPUT" substring in any ancestor's instance ID, or for the
    /// ROOT\VID_*&amp;IG_* root-enumerator pattern that HIDMaestro's xinputhid
    /// profiles use.
    /// </summary>
    public static class StableXInputInstance
    {
        /// <summary>
        /// Returns the first non-HIDMaestro HID-class device instance ID whose
        /// PnP tree contains the given VID/PID, or null if none found.
        /// </summary>
        public static string Find(ushort vid, ushort pid)
        {
            // USB HID format: VID_045E&PID_0B13
            string vidPidUsb = $"VID_{vid:X4}&PID_{pid:X4}";
            // BLE HID-over-GATT format: VID&02045E_PID&0B13 (02 = USB-assigned source)
            //                            VID&01045E_PID&0B13 (01 = Bluetooth SIG)
            string vidBle02 = $"VID&02{vid:X4}";
            string vidBle01 = $"VID&01{vid:X4}";
            string pidBle = $"PID&{pid:X4}";

            var guid = GUID_DEVCLASS_HIDCLASS;
            IntPtr devInfoSet = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
            if (devInfoSet == (IntPtr)(-1)) return null;

            try
            {
                var devInfoData = new SP_DEVINFO_DATA();
                devInfoData.cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>();
                char[] buffer = new char[512];

                for (uint i = 0; SetupDiEnumDeviceInfo(devInfoSet, i, ref devInfoData); i++)
                {
                    if (!SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, buffer, (uint)buffer.Length, out _))
                        continue;

                    int nullIdx = Array.IndexOf(buffer, '\0');
                    string instanceId = nullIdx >= 0 ? new string(buffer, 0, nullIdx) : new string(buffer);

                    bool match = instanceId.Contains(vidPidUsb, StringComparison.OrdinalIgnoreCase)
                        || (instanceId.Contains(pidBle, StringComparison.OrdinalIgnoreCase)
                            && (instanceId.Contains(vidBle02, StringComparison.OrdinalIgnoreCase)
                                || instanceId.Contains(vidBle01, StringComparison.OrdinalIgnoreCase)));

                    if (!match) continue;
                    if (IsHidMaestroInstance(instanceId)) continue;

                    return instanceId;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }

            return null;
        }

        private static bool IsHidMaestroInstance(string instanceId)
        {
            if (MatchesHmPattern(instanceId)) return true;

            if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != 0)
                return false;

            var idBuf = new StringBuilder(512);
            for (int depth = 0; depth < 16; depth++)
            {
                idBuf.Clear();
                idBuf.EnsureCapacity(512);
                if (CM_Get_Device_IDW(devInst, idBuf, idBuf.Capacity, 0) == 0)
                {
                    if (MatchesHmPattern(idBuf.ToString())) return true;
                }

                if (CM_Get_Parent(out uint parent, devInst, 0) != 0) break;
                if (parent == 0 || parent == devInst) break;
                devInst = parent;
            }
            return false;
        }

        private static bool MatchesHmPattern(string id)
        {
            if (id == null) return false;
            if (id.IndexOf("HIDMAESTRO", StringComparison.OrdinalIgnoreCase) >= 0
                || id.IndexOf("HMCOMPANION", StringComparison.OrdinalIgnoreCase) >= 0
                || id.IndexOf("HMXINPUT", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (id.StartsWith(@"ROOT\VID_", StringComparison.OrdinalIgnoreCase)
                && (id.IndexOf("&IG_", StringComparison.OrdinalIgnoreCase) >= 0
                    || id.IndexOf("&XI_", StringComparison.OrdinalIgnoreCase) >= 0))
                return true;
            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        private const uint DIGCF_PRESENT = 0x00000002;
        private static Guid GUID_DEVCLASS_HIDCLASS = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, [Out] char[] DeviceInstanceId, uint DeviceInstanceIdSize, out uint RequiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint parent, uint devInst, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int len, int flags);
    }
}
