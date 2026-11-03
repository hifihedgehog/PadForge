using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>One light of an OpenRGB lenovo_led array: its number in the
    /// zone and OpenRGB's name for it (LenovoDevices.h:57-61).</summary>
    internal readonly record struct LenovoLed(byte Number, string Name);

    /// <summary>OpenRGB's zone types for a lenovo_zone.</summary>
    internal enum LenovoZoneType
    {
        Single,
        Linear,
        Matrix,
    }

    /// <summary>One OpenRGB lenovo_zone (LenovoDevices.h:63-74): its name,
    /// type and ID, its light array, and the first and last index of the
    /// array it takes.</summary>
    internal sealed record LenovoZone(string Name, LenovoZoneType Type, int Id, LenovoLed[] Leds, int Start, int End);

    /// <summary>
    /// The lights of the Lenovo families (#557), from OpenRGB's tables
    /// (<c>LenovoLights.g.cs</c>, written by tools/gen_lenovo_lights.py at
    /// the pinned commit). A light's address is its zone's ID in the high
    /// byte and its number in the low one, as both OpenRGB controllers
    /// build it (RGBController_Lenovo_Gen7_8.cpp:455 and
    /// RGBController_LenovoUSB.cpp:346). A name OpenRgbKeys knows makes a
    /// key with its usage. Any other "Key: " name is a key no source has
    /// named, which Map Key Lights can name. Any other light takes the kind
    /// its zone gives it.
    /// </summary>
    internal static partial class LenovoLights
    {
        internal const string KeyPrefix = "Key: ";

        /// <summary>The lights of <paramref name="zones"/>, each zone with
        /// the kind its lights that are not keys take, in OpenRGB's order.
        /// <paramref name="usages"/> overrides the usage of a light by its
        /// address: 0 leaves the key unnamed.</summary>
        internal static List<LightInfo> Build(IEnumerable<(LenovoZone Zone, LightKind Kind)> zones,
            IReadOnlyDictionary<int, ushort> usages = null)
        {
            var lights = new List<LightInfo>();
            int zoneNumber = 0;
            foreach (var (zone, kind) in zones)
            {
                for (int i = zone.Start; i <= zone.End; i++)
                {
                    var led = zone.Leds[i];
                    int address = zone.Id << 8 | led.Number;
                    if (kind == LightKind.Zone)
                    {
                        // Each light of a zone table is a zone of its own,
                        // numbered from 1 in OpenRGB's order.
                        lights.Add(new LightInfo(address, LightKind.Zone, 0, LightKeySource.None) { Zone = ++zoneNumber });
                        continue;
                    }
                    if (usages != null && usages.TryGetValue(address, out ushort usage))
                    {
                        lights.Add(new LightInfo(address, LightKind.Key, usage,
                            usage == 0 ? LightKeySource.None : LightKeySource.Generated));
                        continue;
                    }
                    lights.Add(Light(address, led.Name, kind));
                }
            }
            return lights;
        }

        /// <summary>The light at <paramref name="address"/> that OpenRGB calls
        /// <paramref name="name"/>, <paramref name="kind"/> when the name is
        /// not a key's.</summary>
        internal static LightInfo Light(int address, string name, LightKind kind)
        {
            if (OpenRgbKeys.TryGet(name, out ushort usage, out var label))
            {
                return usage != 0
                    ? new LightInfo(address, LightKind.Key, usage, LightKeySource.Generated)
                    : new LightInfo(address, LightKind.Other, 0, LightKeySource.None) { Label = label };
            }
            if (name != null && name.StartsWith(KeyPrefix, StringComparison.Ordinal))
                return new LightInfo(address, LightKind.Key, 0, LightKeySource.None);
            return new LightInfo(address, kind, 0, LightKeySource.None);
        }

        /// <summary>Every address of <paramref name="lights"/>.</summary>
        internal static HashSet<int> Addresses(IEnumerable<LightInfo> lights)
        {
            var set = new HashSet<int>();
            foreach (var light in lights) set.Add(light.Address);
            return set;
        }
    }
}
