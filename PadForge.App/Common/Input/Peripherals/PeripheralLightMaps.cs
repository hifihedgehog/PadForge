using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The light map of every unit whose backend knows one (#557), by the
    /// output path that reaches it, for the Lighting tab's Key Colors and
    /// Zone Colors card and for Map Key Lights. A backend publishes a map
    /// when it learns it, from the device or from a table, and takes it
    /// down when the unit leaves its list. Process-wide, like the claims
    /// and links of <see cref="PeripheralOutputs"/>.
    /// </summary>
    internal static class PeripheralLightMaps
    {
        private static readonly ConcurrentDictionary<OutputPath, LightMap> s_maps = new();

        /// <summary>Raised after a map came, changed or went. Any thread.
        /// The owner marshals, and subscribers must not block.</summary>
        public static event Action Changed;

        /// <summary>The map of the unit <paramref name="path"/> reaches, or
        /// null.</summary>
        public static LightMap For(OutputPath path) => s_maps.TryGetValue(path, out var map) ? map : null;

        /// <summary>Every map known now.</summary>
        public static IEnumerable<LightMap> All => s_maps.Values;

        /// <summary>The first map among <paramref name="paths"/> with more
        /// than one light, the order a row's paths are tried in, or null.</summary>
        public static LightMap FirstPerLight(IEnumerable<OutputPath> paths, out OutputPath found)
        {
            found = default;
            if (paths == null) return null;
            foreach (var path in paths)
            {
                var map = For(path);
                if (map == null || !map.PerLight) continue;
                found = path;
                return map;
            }
            return null;
        }

        /// <summary>The first map among <paramref name="paths"/> that lists
        /// strips its user can size, the row's LED Strips card, or null. A
        /// strip left unsized can be one light, so this does not ask for
        /// more than one.</summary>
        public static LightMap WithStrips(IEnumerable<OutputPath> paths)
        {
            if (paths == null) return null;
            foreach (var path in paths)
            {
                var map = For(path);
                if (map != null && map.Strips.Count > 0) return map;
            }
            return null;
        }

        /// <summary>The lights a row's Key Colors card shows: the first map
        /// among its paths with more than one light
        /// (<see cref="FirstPerLight"/>), narrowed to the lights the row's
        /// own device has where that map serves many devices at once. The
        /// LED SDK's keyboard path is the one such map: it holds the per-key
        /// boards' keys and the zone boards' zones, and a row shows the kind
        /// its own HID++ unit has (<see cref="LogiLedKeys.KeyboardOf"/>).
        /// <paramref name="found"/> is the path the map belongs to.</summary>
        public static LightMap ForRow(DeviceLinks links, out OutputPath found)
        {
            var map = FirstPerLight(links?.Lighting, out found);
            if (map == null || found.Family != OutputFamily.LedSdkType || found.Key != "keyboard") return map;
            HidppUnit unit = null;
            var units = PeripheralOutputs.Hidpp.Units;
            foreach (string key in links.HidppUnits)
            {
                foreach (var candidate in units)
                {
                    if (candidate.Key != key || !candidate.HasRgb) continue;
                    if (unit == null || (candidate.IsKeyboardKind && !unit.IsKeyboardKind)) unit = candidate;
                }
            }
            var own = LogiLedKeys.KeyboardOf(unit);
            return own is { PerLight: true } ? own : map;
        }

        public static void Publish(OutputPath path, LightMap map)
        {
            if (map == null)
            {
                Remove(path);
                return;
            }
            bool changed = true;
            s_maps.AddOrUpdate(path, map, (_, old) =>
            {
                changed = !ReferenceEquals(old, map);
                return map;
            });
            if (changed) Raise();
        }

        public static void Remove(OutputPath path)
        {
            if (s_maps.TryRemove(path, out _)) Raise();
        }

        private static void Raise()
        {
            try { Changed?.Invoke(); } catch { }
        }

        internal static void ResetForTest() => s_maps.Clear();
    }
}
