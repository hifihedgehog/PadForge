using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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

        /// <summary>One collection's facts through a query-only open, or
        /// null when it could not be opened or read, which is a probe to
        /// make again and never a collection kept with a vendor ID and
        /// lengths of zero.</summary>
        private static HidCollectionInfo Probe(string path)
        {
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
                    ReleaseNumber = attributes.VersionNumber,
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
