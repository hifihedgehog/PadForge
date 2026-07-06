using System;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Battery percent for Bluetooth controllers from the device's own PnP
    /// battery property (issue #187). This is the number Windows Settings
    /// displays: DEVPKEY {104EA319-6EE2-4701-BD47-8DDBF425BBE5} pid 2 on the
    /// pad's Bluetooth devnode, verified on the reporting hardware (Series X
    /// over Bluetooth LE reads 74 here while the WinRT gaming stack's
    /// synthesized report says 10 and the XInput battery IOCTL says
    /// disconnected). The Steam Controller's Bluetooth node carries the same
    /// property, so non-Xbox pads benefit too.
    ///
    /// The lookup walks UP the queried device's OWN PnP parent chain, starting
    /// from the HID interface path SDL (with the HIDMaestro-filtered shim)
    /// handed us. That anchoring matters: there is no name or VID/PID search
    /// that a HIDMaestro virtual controller could collide with. A virtual
    /// pad's chain terminates at the HIDMaestro bus with no Bluetooth battery
    /// property anywhere, so the probe misses and its indicator stays hidden,
    /// exactly as before.
    ///
    /// The property is a cached devnode value (cheap local reads, no radio
    /// round-trip), so polling it on the 5 s battery tick cannot rate-limit
    /// or flicker the way live WinRT battery reports did.
    /// </summary>
    internal static class BluetoothBatteryService
    {
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern int CM_Locate_DevNodeW(
            out uint pdnDevInst, [MarshalAs(UnmanagedType.LPWStr)] string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_PropertyW(
            uint dnDevInst, in DEVPROPKEY propertyKey,
            out uint propertyType, byte[] propertyBuffer,
            ref uint propertyBufferSize, uint ulFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        // The Bluetooth battery devnode property Windows Settings displays.
        private static readonly DEVPROPKEY DEVPKEY_Bluetooth_Battery =
            new() { fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), pid = 2 };

        private const int CR_SUCCESS = 0;
        private const uint DEVPROP_TYPE_BYTE = 0x00000003;

        /// <summary>Returns the battery percent (0-100) for the device whose
        /// HID interface path is <paramref name="devicePath"/>, or -1 when no
        /// ancestor devnode carries the Bluetooth battery property (wired
        /// pads, virtual pads, synthetic paths).</summary>
        public static int TryGetPercent(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)
                || !devicePath.StartsWith(@"\\?\", StringComparison.Ordinal))
                return -1;

            string instanceId = HidHideController.DevicePathToInstanceId(devicePath);
            if (string.IsNullOrEmpty(instanceId)) return -1;

            if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CR_SUCCESS)
                return -1;

            // Walk the device's own ancestry. The battery property sits on
            // the BTHLE / BTHENUM function node, typically 1-3 hops above the
            // HID interface's devnode; 8 bounds the walk well past any real
            // controller stack.
            var buf = new byte[4];
            for (int hop = 0; hop < 8; hop++)
            {
                uint size = (uint)buf.Length;
                if (CM_Get_DevNode_PropertyW(devInst, in DEVPKEY_Bluetooth_Battery,
                        out uint type, buf, ref size, 0) == CR_SUCCESS
                    && type == DEVPROP_TYPE_BYTE && size >= 1)
                {
                    return Math.Clamp(buf[0], (byte)0, (byte)100);
                }
                if (CM_Get_Parent(out uint parent, devInst, 0) != CR_SUCCESS)
                    break;
                devInst = parent;
            }
            return -1;
        }
    }
}
