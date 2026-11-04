using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals.HidFamilies
{
    /// <summary>The zone types of OpenRGB's razer_zone entries
    /// (RazerDevices.cpp, ZONE_TYPE_*).</summary>
    internal enum RazerHidZoneType
    {
        Single,
        Linear,
        Matrix,
    }

    /// <summary>One zone of a razer_device entry: its name, type, rows and
    /// columns (RazerDevices.h, struct razer_zone).</summary>
    internal sealed record RazerHidZone(string Name, RazerHidZoneType Type, int Rows, int Columns)
    {
        public int Count => Rows * Columns;
    }

    /// <summary>One razer_device entry of OpenRGB's RazerDevices.cpp (#557):
    /// the product's name, the transaction ID and LED ID its reports carry,
    /// its frame of rows and columns, and its zones in the order OpenRGB
    /// lists their LEDs (RazerDevices.h, struct razer_device).</summary>
    internal sealed record RazerHidDevice(ushort ProductId, string Name, byte TransactionId, byte LedId, int Rows,
        int Columns, RazerHidZone[] Zones, string Source);

    /// <summary>
    /// The Razer devices OpenRGB lights through controllers of their own,
    /// outside the 90-byte report path the Razer worker speaks (#557): the
    /// Kraken headsets of three generations, the Hanbo cooler, the ARGB
    /// controller, and the Charging Pad Chroma, which the Razer worker's
    /// table does not list. tools/gen_razer_hid_table.py reads their
    /// detector registrations and razer_device entries from OpenRGB's
    /// RazerController folder into RazerHidTable.g.cs. The registration
    /// generator leaves the RazerController folder out (RazerDirectBackend
    /// lights it its own way), so these registrations are a table of their
    /// own, matched by the same rules (<see cref="OpenRgbRegistrations.Match(HidCollectionInfo, int, IReadOnlyList{OpenRgbRegistration})"/>).
    /// </summary>
    internal static partial class RazerHidTable
    {
        internal const string FamilyName = "RazerController";
        internal const ushort VendorId = 0x1532;

        /// <summary>The device entries by product ID, made on first use: the
        /// order in which the fields of two partial files are set is not
        /// defined, so no field initializer reads <see cref="Devices"/>.</summary>
        private static readonly Lazy<Dictionary<ushort, RazerHidDevice>> s_devices = new(Index);

        private static Dictionary<ushort, RazerHidDevice> Index()
        {
            var byPid = new Dictionary<ushort, RazerHidDevice>();
            foreach (var device in Devices) byPid[device.ProductId] = device;
            return byPid;
        }

        /// <summary>The razer_device entry of a product, or null for one
        /// OpenRGB's device list lacks.</summary>
        internal static RazerHidDevice Device(ushort productId)
            => s_devices.Value.TryGetValue(productId, out var device) ? device : null;

        /// <summary>The first of the table's registrations a collection
        /// answers to, by OpenRGB's rules, if its detector is
        /// <paramref name="detector"/>, else null.</summary>
        internal static OpenRgbRegistration Match(HidCollectionInfo collection, string detector)
        {
            if (collection?.Path == null) return null;
            foreach (var registration in OpenRgbRegistrations.Match(collection, -1, Registrations))
                return registration.Detector == detector ? registration : null;
            return null;
        }

        /// <summary>The registrations of a detector, in table order.</summary>
        internal static IEnumerable<OpenRgbRegistration> Of(string detector)
        {
            foreach (var registration in Registrations)
                if (registration.Detector == detector) yield return registration;
        }

        /// <summary>The key Map Key Lights stores learned names under, the
        /// one the Razer worker's maps use (<see cref="RazerLightTable.ProductKey"/>).</summary>
        internal static string Product(ushort productId) => RazerLightTable.ProductKey(productId);

        /// <summary>
        /// The lights of a product's zones in OpenRGB's LED order, light
        /// <c>i</c> at address <c>i</c>: each zone a numbered zone, from 1, or
        /// for a zone whose name says it is a logo, a logo light, and for a
        /// strip or a channel, edge lighting with the zone's number, the
        /// kinds the Razer light table gives these names
        /// (tools/gen_razer_light_table.py zone_kind).
        /// </summary>
        internal static IReadOnlyList<LightInfo> Lights(IReadOnlyList<RazerHidZone> zones)
        {
            var lights = new List<LightInfo>();
            for (int z = 0; z < zones.Count; z++)
            {
                var kind = KindOf(zones[z].Name);
                for (int i = 0; i < zones[z].Count; i++)
                    lights.Add(new LightInfo(lights.Count, kind, 0, LightKeySource.None) { Zone = z + 1 });
            }
            return lights;
        }

        /// <summary>The kind the Razer light table gives a zone of this name
        /// (tools/gen_razer_light_table.py zone_kind).</summary>
        internal static LightKind KindOf(string name)
        {
            if (name is "Logo" or "Logo Zone" or "Left Logo" or "Right Logo") return LightKind.Logo;
            if (name.Contains("Strip", StringComparison.Ordinal) || name.Contains("Underglow", StringComparison.Ordinal)
                || name.Contains("Chroma Zone", StringComparison.Ordinal) || name.Contains("Channel", StringComparison.Ordinal)
                || name.Contains("Speaker", StringComparison.Ordinal) || name is "Base" or "Mousemat" or "Case LEDs")
                return LightKind.Edge;
            return LightKind.Zone;
        }

        /// <summary>A product's map, its razer_device entry's zones, or one
        /// zone light for a product the device list lacks.</summary>
        internal static LightMap MapOf(ushort productId)
        {
            var device = Device(productId);
            var lights = device != null
                ? Lights(device.Zones)
                : new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } };
            string source = device != null ? "OpenRGB " + device.Source : "OpenRGB RazerControllerDetect.cpp";
            return new LightMap(lights, source) { Product = Product(productId) };
        }
    }
}
