using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Key names learned with Map Key Lights (#557): for each device model,
    /// the key each light sits under, by the light's own number. A model is
    /// its product key (<see cref="LightMap.Product"/>), so one owner's
    /// mapping names the keys of every unit of that model, on this PC and,
    /// exported, on any other. A backend applies the names when it
    /// publishes a unit's lights (<see cref="Apply"/>), over the names the
    /// device and the tables give: a learned name is the user's own word on
    /// their own keyboard. Process-wide and saved with the app settings,
    /// like the learned handheld buttons.
    /// </summary>
    internal static class LearnedKeyMaps
    {
        private static readonly object s_lock = new();
        private static Dictionary<string, Dictionary<int, ushort>> s_maps = new(StringComparer.OrdinalIgnoreCase);
        private static int s_version;

        /// <summary>Raised after a name was learned, removed or loaded. Any
        /// thread. The service saves the settings on it.</summary>
        public static event EventHandler Changed;

        /// <summary>Counts every change, so a backend can tell its maps are
        /// owed the names again.</summary>
        public static int Version => Volatile.Read(ref s_version);

        /// <summary>The names learned for <paramref name="product"/>, light
        /// number to HID usage on the Keyboard/Keypad page.</summary>
        public static IReadOnlyDictionary<int, ushort> For(string product)
        {
            if (string.IsNullOrEmpty(product)) return Empty;
            lock (s_lock)
                return s_maps.TryGetValue(product, out var names) ? new Dictionary<int, ushort>(names) : Empty;
        }

        private static readonly IReadOnlyDictionary<int, ushort> Empty = new Dictionary<int, ushort>();

        /// <summary>
        /// <paramref name="map"/> with the names learned for its model: a
        /// key light named by the user takes that key, and a light the user
        /// named that the device calls a zone or another kind stays as it
        /// is, since only a key has a key to name. The same map when nothing
        /// is learned for it.
        /// </summary>
        public static LightMap Apply(LightMap map)
        {
            if (map == null || string.IsNullOrEmpty(map.Product)) return map;
            var names = For(map.Product);
            if (names.Count == 0) return map;
            bool changed = false;
            var lights = new LightInfo[map.Lights.Count];
            for (int i = 0; i < lights.Length; i++)
            {
                var light = map.Lights[i];
                if (light.Kind == LightKind.Key && names.TryGetValue(light.Address, out ushort usage)
                    && (usage != light.Usage || light.KeySource != LightKeySource.Learned))
                {
                    light = light with { Usage = usage, KeySource = LightKeySource.Learned };
                    changed = true;
                }
                lights[i] = light;
            }
            return changed ? new LightMap(lights, map.Source) { Product = map.Product } : map;
        }

        /// <summary>Names light <paramref name="address"/> of
        /// <paramref name="product"/> the key with <paramref name="usage"/>,
        /// or forgets its name when the usage is 0. A usage another light
        /// of the model had moves here: a key sits over the lights it
        /// names, and naming one again is a correction.</summary>
        public static void Set(string product, int address, ushort usage, bool moveFromOthers = false)
        {
            if (string.IsNullOrEmpty(product) || address < 0) return;
            lock (s_lock)
            {
                var maps = Copy();
                if (!maps.TryGetValue(product, out var names)) maps[product] = names = new Dictionary<int, ushort>();
                if (usage == 0) names.Remove(address);
                else
                {
                    if (moveFromOthers)
                        foreach (var other in names.Where(p => p.Value == usage && p.Key != address).Select(p => p.Key).ToList())
                            names.Remove(other);
                    names[address] = usage;
                }
                if (names.Count == 0) maps.Remove(product);
                s_maps = maps;
                Interlocked.Increment(ref s_version);
            }
            Raise();
        }

        /// <summary>Puts <paramref name="names"/> in place of what is
        /// learned for <paramref name="product"/>, as an import does.</summary>
        public static void Replace(string product, IReadOnlyDictionary<int, ushort> names)
        {
            if (string.IsNullOrEmpty(product)) return;
            lock (s_lock)
            {
                var maps = Copy();
                var kept = names?.Where(p => p.Key >= 0 && p.Value != 0).ToDictionary(p => p.Key, p => p.Value)
                           ?? new Dictionary<int, ushort>();
                if (kept.Count == 0) maps.Remove(product);
                else maps[product] = kept;
                s_maps = maps;
                Interlocked.Increment(ref s_version);
            }
            Raise();
        }

        /// <summary>Every model's names, for the settings file.</summary>
        public static IReadOnlyList<(string Product, string Names)> SaveRegistry()
        {
            lock (s_lock)
                return s_maps.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(p => (p.Key, Format(p.Value))).ToList();
        }

        /// <summary>Loads the settings file's names in place of what is
        /// held.</summary>
        public static void LoadRegistry(IEnumerable<(string Product, string Names)> saved)
        {
            var maps = new Dictionary<string, Dictionary<int, ushort>>(StringComparer.OrdinalIgnoreCase);
            if (saved != null)
                foreach (var (product, text) in saved)
                {
                    if (string.IsNullOrWhiteSpace(product)) continue;
                    var names = Parse(text);
                    if (names.Count > 0) maps[product.Trim()] = names;
                }
            lock (s_lock)
            {
                s_maps = maps;
                Interlocked.Increment(ref s_version);
            }
            Raise();
        }

        /// <summary>"light=UU" pairs split by ",": a light's number in
        /// decimal and its key's usage in hex. A pair the reader does not
        /// know is dropped.</summary>
        public static string Format(IReadOnlyDictionary<int, ushort> names)
            => string.Join(",", (names ?? Empty).OrderBy(p => p.Key)
                .Select(p => p.Key.ToString(CultureInfo.InvariantCulture) + "=" + p.Value.ToString("X2", CultureInfo.InvariantCulture)));

        public static Dictionary<int, ushort> Parse(string text)
        {
            var names = new Dictionary<int, ushort>();
            if (string.IsNullOrWhiteSpace(text)) return names;
            foreach (var raw in text.Split(','))
            {
                var pair = raw.Trim();
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                if (int.TryParse(pair.AsSpan(0, eq), NumberStyles.None, CultureInfo.InvariantCulture, out int address)
                    && ushort.TryParse(pair.AsSpan(eq + 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort usage)
                    && usage != 0)
                    names[address] = usage;
            }
            return names;
        }

        // Caller holds the lock. The dictionary is swapped whole, so a
        // reader holding the old one never sees it change.
        private static Dictionary<string, Dictionary<int, ushort>> Copy()
            => s_maps.ToDictionary(p => p.Key, p => new Dictionary<int, ushort>(p.Value), StringComparer.OrdinalIgnoreCase);

        private static void Raise()
        {
            try { Changed?.Invoke(null, EventArgs.Empty); } catch { }
        }

        internal static void ResetForTest()
        {
            lock (s_lock)
            {
                s_maps = new Dictionary<string, Dictionary<int, ushort>>(StringComparer.OrdinalIgnoreCase);
                Interlocked.Increment(ref s_version);
            }
        }
    }
}
