using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals.HidFamilies
{
    /// <summary>How a registered device is reached (#557): a HID collection,
    /// a USB serial port, WMI, or another way such as a detector that only
    /// registers others.</summary>
    internal enum OpenRgbTransport
    {
        Hid = 0,
        UsbSerial = 1,
        Wmi = 2,
        Other = 3,
    }

    /// <summary>
    /// One OpenRGB detector registration (#557), as the detector object its
    /// REGISTER_*_DETECTOR line makes holds it (DetectionManager.h:469-503).
    /// <see cref="Any"/> (-1) is OpenRGB's "any" for the vendor and product
    /// IDs, the interface, the usage page, the usage and the bus
    /// (DetectionManager.h:41-46). A registration with
    /// <see cref="FromSettings"/> set takes its name and IDs from the user's
    /// OpenRGB settings at run time (QMK's), so it matches nothing here.
    /// <see cref="Source"/> is the file and line of the REGISTER line.
    /// </summary>
    internal sealed record OpenRgbRegistration(string Family, string Name, string Detector, string Macro,
                                               OpenRgbTransport Transport, int VendorId, int ProductId, int Interface,
                                               int UsagePage, int Usage, int Bus, bool EnabledByDefault,
                                               bool FromSettings, string Source)
    {
        public const int Any = -1;

        /// <summary>A detector with neither vendor nor product ID, which
        /// OpenRGB tries only after the specific ones
        /// (DetectionManager.cpp:292-299).</summary>
        public bool IsGeneric => VendorId == Any && ProductId == Any;

        /// <summary>A REGISTER_HID_WRAPPED_* detector, which OpenRGB runs in a
        /// pass of its own (DetectionManager.cpp:1631-1730).</summary>
        public bool IsWrapped => Macro.StartsWith("REGISTER_HID_WRAPPED_", StringComparison.Ordinal);
    }

    /// <summary>The detector registrations of the families #557 ports, read
    /// from OpenRGB at <see cref="OpenRgbCommit"/> by
    /// tools/gen_openrgb_registrations.py into OpenRgbRegistrations.g.cs,
    /// and OpenRGB's rules for which of them a HID collection answers
    /// to.</summary>
    internal static partial class OpenRgbRegistrations
    {
        /// <summary>A family's registrations, in table order.</summary>
        public static IEnumerable<OpenRgbRegistration> Of(string family)
        {
            foreach (var registration in All)
                if (registration.Family == family)
                    yield return registration;
        }

        /// <summary>hidapi's bus types, HID_API_BUS_USB and
        /// HID_API_BUS_BLUETOOTH (hidapi.h:124-147).</summary>
        public const int BusUsb = 1;
        public const int BusBluetooth = 2;

        /// <summary>
        /// The bus hidapi gives a collection, as far as its path tells it.
        /// hidapi reads the bus from the compatible IDs of the collection's
        /// parent device (windows/hid.c:630-690), which PadForge does not
        /// read. The HID class names a collection's device after its bus
        /// driver's enumerator: "HID\VID_..." under USB, "HID\{00001124-...}"
        /// under Bluetooth and "HID\{00001812-...}" under Bluetooth LE, the
        /// HID and HID-over-GATT service UUIDs. Any other path gives -1,
        /// unknown.
        /// </summary>
        public static int BusOf(HidCollectionInfo collection)
        {
            string path = collection?.Path;
            if (string.IsNullOrEmpty(path)) return OpenRgbRegistration.Any;
            if (path.Contains("{00001124-0000-1000-8000-00805f9b34fb}", StringComparison.OrdinalIgnoreCase)
                || path.Contains("{00001812-0000-1000-8000-00805f9b34fb}", StringComparison.OrdinalIgnoreCase))
                return BusBluetooth;
            if (path.Contains("hid#vid_", StringComparison.OrdinalIgnoreCase))
                return BusUsb;
            return OpenRgbRegistration.Any;
        }

        /// <summary>The interface number hidapi gives a collection: on USB
        /// a path with no "&amp;mi_" is a single-interface device, which
        /// hidapi numbers 0 (windows/hid.c:515-521 and 568-570). On any other
        /// bus it stays -1 (line 758).</summary>
        public static int InterfaceOf(HidCollectionInfo collection, int bus)
            => collection.InterfaceNumber == -1 && bus == BusUsb ? 0 : collection.InterfaceNumber;

        /// <summary>
        /// BasicHIDBlock::compare (DetectionManager.cpp:106-121): every one of
        /// the vendor and product IDs, the usage page, the usage, the
        /// interface and the bus is "any" or equal to the collection's, the
        /// interface and the bus as hidapi gives them (<see cref="InterfaceOf"/>,
        /// <see cref="BusOf"/>). <paramref name="busType"/> overrides the bus
        /// the path tells, -1 for none. A registration that names a bus does
        /// not match an unknown one. A registration that is not a HID one,
        /// or takes its IDs from the settings, matches nothing.
        /// </summary>
        public static bool Compare(OpenRgbRegistration registration, HidCollectionInfo collection, int busType = -1)
        {
            if (registration.Transport != OpenRgbTransport.Hid || registration.FromSettings || collection == null)
                return false;
            int bus = busType != OpenRgbRegistration.Any ? busType : BusOf(collection);
            return (registration.VendorId == OpenRgbRegistration.Any || registration.VendorId == collection.VendorId)
                && (registration.ProductId == OpenRgbRegistration.Any || registration.ProductId == collection.ProductId)
                && (registration.UsagePage == OpenRgbRegistration.Any || registration.UsagePage == collection.UsagePage)
                && (registration.Usage == OpenRgbRegistration.Any || registration.Usage == collection.Usage)
                && (registration.Interface == OpenRgbRegistration.Any || registration.Interface == InterfaceOf(collection, bus))
                && (registration.Bus == OpenRgbRegistration.Any || registration.Bus == bus);
        }

        /// <summary>BasicHIDBlock::matching_id (lines 123-127): the vendor ID
        /// equal and the product ID equal or "any".</summary>
        public static bool MatchingId(OpenRgbRegistration registration, HidCollectionInfo collection)
            => registration.VendorId == collection.VendorId
            && (registration.ProductId == collection.ProductId || registration.ProductId == OpenRgbRegistration.Any);

        /// <summary>
        /// The registrations a collection answers to, in the order OpenRGB
        /// tries them (RunHIDDetector, DetectionManager.cpp:1515-1628, then
        /// RunHIDWrappedDetector, 1631-1730, the same rules in a pass of its
        /// own): the specific detectors whose IDs match and whose fields
        /// compare, then the generic ones whose fields compare, which are
        /// skipped once an enabled specific detector's IDs have matched. A
        /// detector disabled by default is not run. OpenRGB stops at the
        /// first detector that returns a controller, which needs the
        /// device, so every candidate is listed.
        /// </summary>
        public static List<OpenRgbRegistration> Match(HidCollectionInfo collection, int busType = -1,
                                                      IReadOnlyList<OpenRgbRegistration> table = null)
        {
            table ??= All;
            var result = new List<OpenRgbRegistration>();
            foreach (bool wrapped in new[] { false, true })
            {
                bool skipGeneric = false;
                foreach (var r in table)
                    if (r.IsWrapped == wrapped && !r.IsGeneric && r.EnabledByDefault
                        && r.Transport == OpenRgbTransport.Hid && !r.FromSettings && MatchingId(r, collection))
                    {
                        skipGeneric = true;
                        if (Compare(r, collection, busType)) result.Add(r);
                    }
                if (skipGeneric)
                    continue;
                foreach (var r in table)
                    if (r.IsWrapped == wrapped && r.IsGeneric && r.EnabledByDefault && Compare(r, collection, busType))
                        result.Add(r);
            }
            return result;
        }

        /// <summary>The registrations of <paramref name="family"/> a
        /// collection answers to, among every family's, in OpenRGB's
        /// order.</summary>
        public static List<OpenRgbRegistration> Match(string family, HidCollectionInfo collection, int busType = -1)
            => Match(collection, busType).FindAll(r => r.Family == family);
    }
}
