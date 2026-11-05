using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace PadForge.Common.Input
{
    /// <summary>One present HID top-level collection, as a peripheral
    /// backend needs it to tell its devices from every other one: who made
    /// it, what it is for, which USB interface it sits on and how long its
    /// reports are (#494). Read from the descriptor Windows already parsed,
    /// so nothing here was asked of the device.</summary>
    internal sealed record HidCollectionInfo
    {
        public string Path { get; init; }
        public ushort VendorId { get; init; }
        public ushort ProductId { get; init; }
        public ushort UsagePage { get; init; }
        public ushort Usage { get; init; }

        /// <summary>A collection Windows opens exclusively, Microsoft's table
        /// of HID clients: the mouse, the keyboard or keypad, and the pens,
        /// touch screens and touchpads. On these a program takes feature
        /// reports and no others, whichever call it makes.</summary>
        public bool WindowsHolds => (UsagePage == 0x0001 && Usage is 0x0001 or 0x0002 or 0x0006 or 0x0007)
                                    || (UsagePage == 0x000D && Usage is 0x0001 or 0x0002 or 0x0004 or 0x0005);

        /// <summary>The device's release number, HIDD_ATTRIBUTES'
        /// VersionNumber, which hidapi hands its callers as release_number
        /// (hidapi windows/hid.c:767). OpenRGB's ASUS Claymore takes its
        /// region from it (AsusAuraKeyboardControllerDetect.cpp:62,
        /// AsusAuraKeyboardController.cpp:174-189).</summary>
        public ushort ReleaseNumber { get; init; }

        /// <summary>The USB interface number, from "&amp;mi_xx" in the
        /// path, or -1 for a path without one: a device with a single
        /// interface, or one on Bluetooth.</summary>
        public int InterfaceNumber { get; init; } = -1;

        /// <summary>The byte length of the collection's longest report of
        /// each type, the report ID byte included, or 0 for a type it has
        /// none of.</summary>
        public ushort InputReportLength { get; init; }
        public ushort OutputReportLength { get; init; }
        public ushort FeatureReportLength { get; init; }
    }

    /// <summary>
    /// Enumerates every present HID collection (#494), for the peripheral
    /// backends that find their devices by descriptor: a LampArray on usage
    /// page 0x59, a Razer control collection by its 91-byte feature report, a
    /// SteelSeries vendor collection by its interface.
    /// <see cref="VendorHidRuntime.Enumerate"/> makes the same walk and
    /// cannot serve them: it drops every collection below usage page 0xFF00
    /// and every collection with no input report
    /// (VendorHidRuntime.cs:153-155). Nearly every Razer control collection
    /// sits on usage page 0x01 or 0x0C (the detector table in OpenRGB
    /// RazerControllerDetect.cpp:217-453), and a LampArray collection can
    /// have no input report at all.
    ///
    /// <para>Each path is opened with no access right, which Windows grants
    /// on a mouse or keyboard collection too, and asked only for what the
    /// HID class driver holds: the parsed descriptor and the attributes. No
    /// string is read, since a string request goes to the device, and no
    /// report is sent. A backend that wants a name asks for it once per
    /// unit. Verdicts are kept per path, so a descriptor read that succeeds
    /// runs once per appearance and one that fails runs again on the next
    /// walk, the rule <see cref="VendorHidRuntime.Lookup"/> keeps. The
    /// peripheral host makes one walk per rescan and hands the list to every
    /// backend.</para>
    /// </summary>
    internal static class HidCollectionRuntime
    {
        private static readonly Dictionary<string, HidCollectionInfo> s_verdicts =
            new(StringComparer.OrdinalIgnoreCase);

        internal static void InvalidateCache()
        {
            lock (s_verdicts) s_verdicts.Clear();
        }

        /// <summary>Every present collection that could be read, or null
        /// when the enumeration itself failed, kept apart from "none
        /// present" so one failed SetupAPI call never reads as every device
        /// unplugged.</summary>
        internal static List<HidCollectionInfo> Enumerate()
            => Collect(s_verdicts, PresentPaths(), Probe);

        /// <summary>The walk over a list of present paths, apart from the
        /// SetupAPI call that produces it and the probe that reads one, so a
        /// test drives the cache with neither. A verdict comes from
        /// <paramref name="cache"/> or from <paramref name="probe"/>, a
        /// probe that could not read its collection (null) is not kept, and
        /// a path that left the list leaves the cache, so a device that
        /// comes back on the same path is read again.</summary>
        internal static List<HidCollectionInfo> Collect(Dictionary<string, HidCollectionInfo> cache,
            IReadOnlyList<string> present, Func<string, HidCollectionInfo> probe)
        {
            if (present == null) return null;
            var result = new List<HidCollectionInfo>(present.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in present)
            {
                if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                HidCollectionInfo verdict;
                bool known;
                lock (cache) known = cache.TryGetValue(path, out verdict);
                if (!known)
                {
                    // Outside the lock: a probe opens a handle.
                    try { verdict = probe(path); }
                    catch { verdict = null; }
                    if (verdict != null)
                        lock (cache) cache[path] = verdict;
                }
                if (verdict != null) result.Add(verdict);
            }
            lock (cache)
            {
                List<string> gone = null;
                foreach (var key in cache.Keys)
                    if (!seen.Contains(key)) (gone ??= new List<string>()).Add(key);
                if (gone != null) foreach (var key in gone) cache.Remove(key);
            }
            return result;
        }

        /// <summary>The USB interface number in an interface path, or -1.
        /// hidapi reads the same "MI_" token as hexadecimal from the
        /// device's hardware ID (hidapi windows/hid.c:456-471 and 515-521),
        /// which the path's device segment repeats. Only that segment is
        /// searched, the text between the first two '#', so an instance ID
        /// that happens to hold the token is never read as an interface.</summary>
        internal static int InterfaceNumberOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return -1;
            int start = path.IndexOf('#');
            int end = start < 0 ? -1 : path.IndexOf('#', start + 1);
            string segment = start >= 0 && end > start ? path.Substring(start + 1, end - start - 1) : path;
            int at = segment.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return -1;
            int value = 0, read = 0;
            for (int i = at + 4; i < segment.Length && Uri.IsHexDigit(segment[i]); i++)
            {
                // Windows writes two digits. More than four is no interface
                // number, and would run past an int.
                if (++read > 4) return -1;
                value = (value << 4) | Uri.FromHex(segment[i]);
            }
            return read == 0 ? -1 : value;
        }

        /// <summary>The interface path of every present HID collection, or
        /// null when the device information set could not be built.</summary>
        private static List<string> PresentPaths()
        {
            var paths = new List<string>();
            SonyHeadsetHid.HidD_GetHidGuid(out Guid hidGuid);
            IntPtr set = SonyHeadsetHid.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                SonyHeadsetHid.DIGCF_PRESENT | SonyHeadsetHid.DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return null;
            try
            {
                var iface = new SonyHeadsetHid.SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = Marshal.SizeOf<SonyHeadsetHid.SP_DEVICE_INTERFACE_DATA>()
                };
                for (uint index = 0; SonyHeadsetHid.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero,
                        ref hidGuid, index, ref iface); index++)
                {
                    string path = VendorHidRuntime.GetInterfacePath(set, ref iface);
                    if (!string.IsNullOrEmpty(path)) paths.Add(path);
                }
            }
            finally
            {
                SonyHeadsetHid.SetupDiDestroyDeviceInfoList(set);
            }
            return paths;
        }

        /// <summary>The release number of a USB device, read from its hardware
        /// IDs, for a collection whose HIDD_ATTRIBUTES give 0, as hidapi does:
        /// the instance ID's device node, its parent, and that parent's parent
        /// where the instance ID names an XInput collection with IG_, when the
        /// parent's compatible IDs say USB (hid_internal_get_info and
        /// hid_internal_get_usb_info, windows/hid.c at c3509c1, lines 480-516).
        /// 0 where that finds none.</summary>
        private static ushort UsbReleaseOf(string path)
        {
            try
            {
                var instanceKey = new DEVPROPKEY { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), pid = 256 };
                var buffer = new byte[2048];
                uint size = (uint)buffer.Length;
                if (CM_Get_Device_Interface_PropertyW(path, ref instanceKey, out uint _, buffer, ref size, 0) != 0) return 0;
                string instanceId = Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
                if (CM_Locate_DevNodeW(out uint node, instanceId, 0) != 0) return 0;
                if (CM_Get_Parent(out node, node, 0) != 0) return 0;
                if (!IsUsb(StringList(node, 4))) return 0;
                string parentId = DevNodeInstanceId(node);
                if (parentId != null && TokenValue(parentId, "IG_") != -1 && CM_Get_Parent(out node, node, 0) != 0) return 0;
                return ReleaseFromHardwareIds(StringList(node, 3));
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Whether a device's compatible IDs name the USB bus, as
        /// hid_internal_detect_bus_type tells it (windows/hid.c): an ID that
        /// holds USB, as hid_internal_detect_bus_type reads them (lines 658-666).</summary>
        internal static bool IsUsb(IEnumerable<string> compatibleIds)
            => compatibleIds.Any(id => id.Contains("USB", StringComparison.OrdinalIgnoreCase));

        /// <summary>The first release number among a USB device's hardware IDs,
        /// the digits after REV_, bcdDevice. One that reads 0 names none, and
        /// hidapi goes on to the next ID. 0 for none.</summary>
        internal static ushort ReleaseFromHardwareIds(IEnumerable<string> hardwareIds)
        {
            foreach (string id in hardwareIds)
            {
                int value = TokenValue(id, "REV_");
                if (value > 0) return (ushort)value;
            }
            return 0;
        }

        /// <summary>The hexadecimal digits after a token, in either case, or -1
        /// for an ID without the token or without digits after it, as
        /// hid_internal_extract_int_token_value reads them (windows/hid.c:456-471).</summary>
        internal static int TokenValue(string id, string token)
        {
            int at = id.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return -1;
            int start = at + token.Length, end = start, value = 0;
            while (end < id.Length && Uri.IsHexDigit(id[end]) && end - start < 8)
                value = value << 4 | Convert.ToInt32(id[end++].ToString(), 16);
            return end == start ? -1 : value & 0xFFFF;
        }

        private static string DevNodeInstanceId(uint node)
        {
            var key = new DEVPROPKEY { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), pid = 256 };
            var buffer = new byte[2048];
            uint size = (uint)buffer.Length;
            return CM_Get_DevNode_PropertyW(node, ref key, out uint _, buffer, ref size, 0) != 0 ? null
                : Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
        }

        /// <summary>A device node's list of IDs: DEVPKEY_Device_HardwareIds (3)
        /// or DEVPKEY_Device_CompatibleIds (4). Empty where it has none.</summary>
        private static IReadOnlyList<string> StringList(uint node, uint pid)
        {
            var key = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = pid };
            var buffer = new byte[4096];
            uint size = (uint)buffer.Length;
            if (CM_Get_DevNode_PropertyW(node, ref key, out uint _, buffer, ref size, 0) != 0) return Array.Empty<string>();
            return Encoding.Unicode.GetString(buffer, 0, (int)size).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_PropertyW(string deviceInterface, ref DEVPROPKEY propertyKey,
            out uint propertyType, [Out] byte[] propertyBuffer, ref uint propertyBufferSize, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY propertyKey,
            out uint propertyType, [Out] byte[] propertyBuffer, ref uint propertyBufferSize, uint flags);

        /// <summary>One collection's facts through a query-only open, or
        /// null when it could not be opened or read, which is a probe to
        /// make again and never a collection kept with a vendor ID and
        /// lengths of zero.</summary>
        private static HidCollectionInfo Probe(string path)
        {
            if (RealHidAccess.Refused) return null;
            var handle = SonyHeadsetHid.CreateFile(path, 0,
                SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE,
                IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            IntPtr preparsed = IntPtr.Zero;
            try
            {
                if (!SonyHeadsetHid.HidD_GetPreparsedData(handle, out preparsed)) return null;
                if (SonyHeadsetHid.HidP_GetCaps(preparsed, out var caps) != SonyHeadsetHid.HIDP_STATUS_SUCCESS)
                    return null;
                var attributes = new SonyHeadsetHid.HIDD_ATTRIBUTES
                {
                    Size = Marshal.SizeOf<SonyHeadsetHid.HIDD_ATTRIBUTES>()
                };
                if (!SonyHeadsetHid.HidD_GetAttributes(handle, ref attributes)) return null;
                return new HidCollectionInfo
                {
                    Path = path,
                    VendorId = attributes.VendorID,
                    ProductId = attributes.ProductID,
                    UsagePage = caps.UsagePage,
                    Usage = caps.Usage,
                    ReleaseNumber = attributes.VersionNumber != 0 ? attributes.VersionNumber : UsbReleaseOf(path),
                    InterfaceNumber = InterfaceNumberOf(path),
                    InputReportLength = caps.InputReportByteLength,
                    OutputReportLength = caps.OutputReportByteLength,
                    FeatureReportLength = caps.FeatureReportByteLength,
                };
            }
            catch
            {
                return null;
            }
            finally
            {
                if (preparsed != IntPtr.Zero) SonyHeadsetHid.HidD_FreePreparsedData(preparsed);
                handle.Dispose();
            }
        }
    }
}
