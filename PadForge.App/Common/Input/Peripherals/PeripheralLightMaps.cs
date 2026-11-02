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
