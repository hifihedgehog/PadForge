using System;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ── Composite-persona self-readback guard (HIDMaestro v1.4.0, HM#39) ──
        //
        // Composite USB personas enumerate through the REAL USB stack
        // (vhci → UDE → usbccgp → hidusb), so unlike UMDF2 virtuals they
        // carry no HIDMAESTRO marker in any hardware ID, no "HM-CTL-"
        // serial (the real pad has no USB serial string, and the persona
        // matches it byte for byte), and no HIDMAESTRO path fragment. The
        // fork-side enumeration filter and the Step 1 serial/path guard
        // both miss them by construction. The one honest discriminator
        // left is devnode ancestry: everything on the emulated controller
        // has the usbip2_ude service somewhere in its parent chain. This
        // is the user-mode mirror of the driver's own is_abobe_vhci()
        // check (usbip-win2 drivers/ude_filter/device.cpp), which walks
        // the same relationship from the other side.
        //
        // Scope: only consulted for Sony-VID devices, the only vendor
        // with composite personas today. A real remote pad attached over
        // USB/IP generally would be suppressed too; that is the safe
        // failure (a log line) versus the unsafe one (PadForge ingesting
        // its own virtual pad and feeding it back to itself).

        /// <summary>True when the HID device behind
        /// <paramref name="hidDevicePath"/> sits under the usbip-win2
        /// emulated host controller. Any failure resolves false so real
        /// devices are never suppressed by a plumbing error.</summary>
        private static bool IsOnUsbipVhci(string hidDevicePath)
        {
            if (string.IsNullOrEmpty(hidDevicePath)) return false;

            // Interface path → instance ID: "\\?\hid#vid_054c&pid_0ce6#7&x&y&z#{guid}"
            // becomes "HID\VID_054C&PID_0CE6\7&x&y&z".
            string p = hidDevicePath;
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p.Substring(4);
            int guidSep = p.IndexOf("#{", StringComparison.Ordinal);
            if (guidSep > 0) p = p.Substring(0, guidSep);
            string instanceId = p.Replace('#', '\\');

            if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CR_SUCCESS)
                return false;

            // Bounded parent walk: HID child → usbccgp function → USB
            // device → hub → host controller is 4-5 hops on real trees.
            uint current = devInst;
            for (int depth = 0; depth < 8; depth++)
            {
                if (CM_Get_Parent(out uint parent, current, 0) != CR_SUCCESS)
                    return false;
                string service = GetDevNodeService(parent);
                if (string.Equals(service, "usbip2_ude", StringComparison.OrdinalIgnoreCase))
                    return true;
                current = parent;
            }
            return false;
        }

        private static string GetDevNodeService(uint devInst)
        {
            uint size = 0;
            CM_Get_DevNode_PropertyW(devInst, DEVPKEY_Device_Service, out _, null, ref size, 0);
            if (size == 0) return null;
            byte[] buf = new byte[size];
            int rc = CM_Get_DevNode_PropertyW(devInst, DEVPKEY_Device_Service, out uint type, buf, ref size, 0);
            if (rc != CR_SUCCESS || type != DEVPROP_TYPE_STRING) return null;
            string s = System.Text.Encoding.Unicode.GetString(buf, 0, (int)size);
            int nul = s.IndexOf('\0');
            return nul >= 0 ? s.Substring(0, nul) : s;
        }

        private const int CR_SUCCESS = 0;
        private const uint DEVPROP_TYPE_STRING = 0x12;

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        private static readonly DEVPROPKEY DEVPKEY_Device_Service =
            new DEVPROPKEY { fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0), pid = 6 };

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_PropertyW(
            uint dnDevInst, in DEVPROPKEY propertyKey,
            out uint propertyType, byte[] propertyBuffer, ref uint propertyBufferSize, uint ulFlags);
    }
}
