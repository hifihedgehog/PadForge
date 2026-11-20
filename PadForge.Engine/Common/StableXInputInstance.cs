using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PadForge.Engine
{
    /// <summary>
    /// Looks up a stable physical-device PnP instance ID for an XInput-backed
    /// Xbox controller given its VID/PID. Used by <see cref="SdlDeviceWrapper.BuildInstanceGuid"/>
    /// when SDL reports a synthetic "XInput#N" path. The slot number in
    /// those paths is not stable (xinputhid can reshuffle slots when a
    /// second Xbox-VID device appears), so mapping persistent device
    /// identity to SDL's slot number breaks on reshuffle.
    ///
    /// A real Xbox controller's underlying HID instance ID is stable per
    /// physical device (it contains the BT MAC for Bluetooth devices and a
    /// USB hub/port address for wired devices). HIDMaestro's devices are
    /// left out by the rule Step 1 and the hiding side read
    /// (<see cref="HidMaestroAncestry.IsHidMaestroWalk"/>), and so is a node
    /// OpenXInput gives no slot (<see cref="HidMaestroAncestry.ReadersDrop"/>),
    /// since no XInput#N path can name it.
    /// </summary>
    public static class StableXInputInstance
    {
        /// <summary>Whether a composite persona of this vendor and product
        /// id can be PadForge's own (InputManager.ChecksUsbipAncestry, the
        /// scope Step 1 and the hiding side ask for a device under the
        /// usbip-win2 host controller). The app sets it as the engine
        /// starts. Null answers no.</summary>
        public static Func<ushort, ushort, bool> PersonaInScope;

        /// <summary>
        /// Returns every HID-class device instance ID whose PnP tree
        /// contains the given VID/PID and that an XInput#N path can name:
        /// not one of HIDMaestro's devices, and given a slot by OpenXInput.
        /// Sorted lexicographically so the order is deterministic across
        /// SetupDi enumeration variations (e.g. between reboots or after a
        /// USB hub re-plug).
        ///
        /// <para>Lets <see cref="SdlDeviceWrapper.BuildInstanceGuid"/>
        /// disambiguate two same-model controllers that would otherwise
        /// collapse to the same identity, such as two Xbox Series Bluetooth
        /// pads paired at the same time. Both report the same VID/PID, and
        /// SDL gives them synthetic XInput#N paths with no serial.</para>
        /// </summary>
        public static IReadOnlyList<string> FindAll(ushort vid, ushort pid)
        {
            // USB HID format: VID_045E&PID_0B13
            string vidPidUsb = $"VID_{vid:X4}&PID_{pid:X4}";
            // BLE HID-over-GATT format: VID&02045E_PID&0B13 (02 = USB-assigned source)
            //                            VID&01045E_PID&0B13 (01 = Bluetooth SIG)
            string vidBle02 = $"VID&02{vid:X4}";
            string vidBle01 = $"VID&01{vid:X4}";
            string pidBle = $"PID&{pid:X4}";

            var matches = new List<string>();

            var guid = GUID_DEVCLASS_HIDCLASS;
            IntPtr devInfoSet = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
            if (devInfoSet == (IntPtr)(-1)) return matches;

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

                    bool usbIds = instanceId.Contains(vidPidUsb, StringComparison.OrdinalIgnoreCase);
                    bool match = usbIds
                        || (instanceId.Contains(pidBle, StringComparison.OrdinalIgnoreCase)
                            && (instanceId.Contains(vidBle02, StringComparison.OrdinalIgnoreCase)
                                || instanceId.Contains(vidBle01, StringComparison.OrdinalIgnoreCase)));

                    if (!match) continue;
                    // A persona is a USB device, so a node without the
                    // VID_xxxx&PID_yyyy pair is none whatever is in scope,
                    // as the hiding side reads it.
                    if (IsLeftOut(instanceId, () => usbIds && PersonaInScope?.Invoke(vid, pid) == true,
                            LiveWalk, HidMaestroAncestry.CarriesXusbInterface))
                        continue;

                    matches.Add(instanceId);
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }

            matches.Sort(StringComparer.OrdinalIgnoreCase);
            return matches;
        }

        /// <summary>Whether <see cref="FindAll"/> leaves a node out: one of
        /// HIDMaestro's devices (<see cref="IsHidMaestroInstance"/>), or a
        /// node OpenXInput gives no slot
        /// (<see cref="HidMaestroAncestry.ReadersDrop"/>). A real pad
        /// attached over usbip-win2 under a host controller HIDMaestro has
        /// stamped is the second kind, whichever of Windows' Xbox drivers
        /// serves it: xusb22 puts its XUSB interface two nodes below the
        /// stamp and xboxgip four, and OpenXInput's walk reaches both.
        /// <paramref name="walk"/> gives the hops up from a node, or null
        /// for a node that is not present, and
        /// <paramref name="carriesXusb"/> says whether a node carries an
        /// XUSB interface: the device tree's answers in
        /// <see cref="FindAll"/>.</summary>
        internal static bool IsLeftOut(string instanceId, Func<bool> personaInScope,
            Func<string, IReadOnlyList<AncestryHop>> walk, Func<string, bool> carriesXusb)
        {
            if (HidMaestroAncestry.NamesHidMaestro(instanceId)) return true;
            var hops = walk(instanceId);
            return hops != null
                && (HidMaestroAncestry.IsHidMaestroWalk(hops, personaInScope)
                    || HidMaestroAncestry.ReadersDrop(hops, carriesXusb));
        }

        /// <summary>Whether a node is one of HIDMaestro's devices, by the
        /// rule Step 1 and the hiding side read
        /// (<see cref="HidMaestroAncestry.IsHidMaestroWalk"/>): its instance
        /// id names HIDMaestro, or the walk up from its node meets a node of
        /// HIDMaestro's own driver, a persona, or, while
        /// <paramref name="personaInScope"/> holds, the usbip-win2 host
        /// controller. That host controller marks nothing by itself. A node
        /// that is not present is judged by its own id alone.</summary>
        internal static bool IsHidMaestroInstance(string instanceId, Func<bool> personaInScope,
            Func<string, IReadOnlyList<AncestryHop>> walk)
        {
            if (HidMaestroAncestry.NamesHidMaestro(instanceId)) return true;
            var hops = walk(instanceId);
            return hops != null && HidMaestroAncestry.IsHidMaestroWalk(hops, personaInScope);
        }

        /// <summary>The walk from a node up through its parents, the node
        /// itself first, or null for a node that is not present.</summary>
        private static IReadOnlyList<AncestryHop> LiveWalk(string instanceId)
            => CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != 0 ? null : ReadWalk(devInst);

        /// <summary>The live walk: the node and up to fifteen of its
        /// parents, each with its instance id, hardware ids and
        /// service.</summary>
        private static List<AncestryHop> ReadWalk(uint devInst)
        {
            var walk = new List<AncestryHop>();
            var idBuf = new StringBuilder(512);
            for (int depth = 0; depth < 16; depth++)
            {
                idBuf.Clear();
                idBuf.EnsureCapacity(512);
                string id = CM_Get_Device_IDW(devInst, idBuf, idBuf.Capacity, 0) == 0 ? idBuf.ToString() : null;
                walk.Add(new AncestryHop(id, ReadHardwareIds(devInst), ReadService(devInst)));

                if (CM_Get_Parent(out uint parent, devInst, 0) != 0) break;
                if (parent == 0 || parent == devInst) break;
                devInst = parent;
            }
            return walk;
        }

        private const uint CM_DRP_HARDWAREID = 0x02;
        private const uint CM_DRP_SERVICE = 0x05;

        /// <summary>A devnode's hardware ids (CM_DRP_HARDWAREID,
        /// REG_MULTI_SZ), each whole, none when they do not read. The
        /// host controller's stamp is told from the other HIDMaestro ids by
        /// the full string.</summary>
        private static IReadOnlyList<string> ReadHardwareIds(uint devInst)
        {
            var buf = new char[1024];
            int lenBytes = buf.Length * 2;
            if (CM_Get_DevNode_Registry_PropertyW(devInst, CM_DRP_HARDWAREID, out _, buf, ref lenBytes, 0) != 0)
                return Array.Empty<string>();

            var ids = new List<string>();
            int charCount = Math.Min(lenBytes / 2, buf.Length);
            int start = 0;
            for (int i = 0; i < charCount; i++)
            {
                if (buf[i] != '\0') continue;
                if (i > start) ids.Add(new string(buf, start, i - start));
                start = i + 1;
                // Double-null terminates the REG_MULTI_SZ.
                if (i + 1 < charCount && buf[i + 1] == '\0') break;
            }
            return ids;
        }

        /// <summary>A devnode's driver service (CM_DRP_SERVICE), or null.
        /// The usbip-win2 host controller's is usbip2_ude.</summary>
        private static string ReadService(uint devInst)
        {
            var buf = new char[256];
            int lenBytes = buf.Length * 2;
            if (CM_Get_DevNode_Registry_PropertyW(devInst, CM_DRP_SERVICE, out _, buf, ref lenBytes, 0) != 0)
                return null;
            int nul = Array.IndexOf(buf, '\0');
            string service = nul >= 0 ? new string(buf, 0, nul) : new string(buf);
            return service.Length == 0 ? null : service;
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

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_Registry_PropertyW(
            uint devInst, uint ulProperty, out uint pulRegDataType,
            [Out] char[] buffer, ref int pulLength, int ulFlags);
    }
}
