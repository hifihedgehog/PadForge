using System;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ── Composite-persona self-readback guard (HIDMaestro v1.4.0, HM#39) ──
        //
        // Composite USB personas enumerate through the REAL USB stack
        // (vhci → UDE → usbccgp → hidusb). On the persona itself every
        // identifier is genuine Sony, deliberately: Windows has to see a
        // real DualSense for the UAC audio class driver to bind. So there
        // is no marker on the device, no "HM-CTL-" serial (the real pad
        // has no USB serial string and the persona matches it byte for
        // byte), and no HIDMAESTRO path fragment.
        //
        // HIDMaestro v1.4.3 (HM#42) stamps the ONE node it owns, the
        // emulated host controller, with an additive hardware id:
        //
        //   ROOT\USB\0000  ids = [ROOT\USBIP_WIN2\UDE, ROOT\HIDMAESTRO_UDE]
        //
        // Upstream's id stays at index 0 so driver matching is unchanged.
        // Measured on hardware, the token sits at depth 4 from a persona's
        // HID interface, which is why the walk below is generous.
        //
        // Prefer the token: it names HIDMaestro rather than the transport,
        // so it cannot catch a user's own usbip-win2 install or a real pad
        // legitimately forwarded over USB/IP. The usbip2_ude service check
        // stays as a fallback because the stamp is best-effort upstream (a
        // machine that refuses the write still creates controllers) and
        // because a pre-1.4.3 driver may still be resident. That fallback
        // is the user-mode mirror of the driver's own is_abobe_vhci()
        // check (usbip-win2 drivers/ude_filter/device.cpp).
        //
        // Scope: only consulted for Sony-VID devices, the only vendor
        // with composite personas today. Suppressing a real remote pad is
        // the safe failure (a log line) versus the unsafe one (PadForge
        // ingesting its own virtual pad and feeding it back to itself).

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
                if (DevNodeHasHidMaestroToken(parent))
                    return true;
                string service = GetDevNodeService(parent);
                if (string.Equals(service, "usbip2_ude", StringComparison.OrdinalIgnoreCase))
                    return true;
                current = parent;
            }
            return false;
        }

        /// <summary>True when this devnode's hardware ids carry the
        /// HIDMaestro token stamped on the emulated host controller
        /// (HM#42, v1.4.3). REG_MULTI_SZ, so the buffer is a run of
        /// NUL-terminated strings. A substring test over the whole run
        /// is sufficient and matches how the SDL fork's filter reads it.</summary>
        private static bool DevNodeHasHidMaestroToken(uint devInst)
        {
            uint size = 0;
            CM_Get_DevNode_PropertyW(devInst, DEVPKEY_Device_HardwareIds, out _, null, ref size, 0);
            if (size == 0) return false;
            byte[] buf = new byte[size];
            int rc = CM_Get_DevNode_PropertyW(devInst, DEVPKEY_Device_HardwareIds, out uint type, buf, ref size, 0);
            if (rc != CR_SUCCESS) return false;
            if (type != DEVPROP_TYPE_STRING && type != DEVPROP_TYPE_STRING_LIST) return false;
            string ids = System.Text.Encoding.Unicode.GetString(buf, 0, (int)size);
            return ids.IndexOf("HIDMAESTRO", StringComparison.OrdinalIgnoreCase) >= 0;
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
        private const uint DEVPROP_TYPE_STRING_LIST = 0x2012;

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        private static readonly DEVPROPKEY DEVPKEY_Device_Service =
            new DEVPROPKEY { fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0), pid = 6 };

        // DEVPKEY_Device_HardwareIds, same fmtid, pid 3. NOT the same
        // numbering as setupapi's SPDRP_HARDWAREID (1). HM#42 records
        // shipping a devnode-corrupting bug from exactly that mismatch.
        private static readonly DEVPROPKEY DEVPKEY_Device_HardwareIds =
            new DEVPROPKEY { fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0), pid = 3 };

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
