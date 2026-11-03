using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The lights of each Logitech LED SDK device type (#557): the keys a
    /// per-key keyboard takes by LogiLed::KeyName (LogitechLEDLib.h:31-149,
    /// generated into SdkKeyTables.g.cs) and the zones
    /// LogiLedSetLightingForTargetZone reaches on each type.
    ///
    /// <para>A key light's address is its KeyName value, the integer
    /// LogiLedSetLightingForKeyWithKeyName takes. A zone light's address is
    /// <see cref="ZoneBase"/> plus its zone index, so the keyboard's keys and
    /// its five zones share one map without meeting. G1 to G9 carry the
    /// G-key label, and G_LOGO and G_BADGE are logo lights, as RGB.NET maps
    /// them to Programmable1-9, Logo and Custom1
    /// (RGB.NET.Devices.Logitech/Generic/LedMappings.cs:127-137).</para>
    /// </summary>
    internal static partial class LogiLedKeys
    {
        /// <summary>The first zone light's address.</summary>
        internal const int ZoneBase = 0x100000;

        // Built on first use: a static initializer here could run before the
        // generated part's tables, since C# orders initializers across
        // partial files in no set way.
        private static readonly Lazy<Dictionary<string, LightMap>> s_maps = new(BuildMaps);

        /// <summary>The lights of an LED SDK path, or null for a path that
        /// lights one color (the whole devices and the mousemat).</summary>
        internal static LightMap MapOf(string type)
            => type != null && s_maps.Value.TryGetValue(type, out var map) ? map : null;

        /// <summary>Every path with its lights, for the worker to publish.</summary>
        internal static IEnumerable<KeyValuePair<string, LightMap>> Maps => s_maps.Value;

        private static Dictionary<string, LightMap> BuildMaps()
        {
            var maps = new Dictionary<string, LightMap>(StringComparer.Ordinal);
            foreach (var type in PeripheralLinker.LedSdkTypes)
            {
                var (code, zones) = LedSdkBackend.TypeOf(type);
                if (code < 0) continue;
                var lights = new List<LightInfo>();
                if (type == "keyboard")
                    foreach (var key in Keys) lights.Add(KeyLight(key.Code, key.Usage, key.Name));
                for (int zone = 0; zone < zones; zone++)
                    lights.Add(new LightInfo(ZoneBase + zone, LightKind.Zone, 0, LightKeySource.None) { Zone = zone + 1 });
                if (lights.Count < 2) continue;
                lights.Sort((a, b) => a.Address.CompareTo(b.Address));
                maps[type] = new LightMap(lights, type == "keyboard"
                    ? "Logitech LED SDK 9.00 LogitechLEDLib.h, KeyName and keyboard zones"
                    : "Logitech LED SDK 9.00, " + type + " zones");
            }
            return maps;
        }

        /// <summary>Whether a light of an LED SDK map is a key the per-key
        /// call reaches, rather than a zone.</summary>
        internal static bool IsKey(in LightInfo light) => light.Address < ZoneBase;

        private static LightInfo KeyLight(int code, ushort usage, string name)
        {
            if (usage != 0) return new LightInfo(code, LightKind.Key, usage, LightKeySource.Vendor);
            if (name is "G_LOGO" or "G_BADGE") return new LightInfo(code, LightKind.Logo, 0, LightKeySource.None);
            if (name.StartsWith("G_", StringComparison.Ordinal) && name.Length == 3 && char.IsDigit(name[2]))
                return new LightInfo(code, LightKind.Other, 0, LightKeySource.Vendor)
                {
                    Label = LightLabel.GKey,
                    LabelNumber = name[2] - '0',
                };
            throw new InvalidOperationException("KeyName without a light: " + name);
        }
    }
}
