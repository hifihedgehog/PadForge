using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// The key tables and model catalogs the routes read (issue #468), kept
    /// as JSON files embedded in the engine rather than as C# literals: the
    /// HallJoy routes alone carry several hundred tables. Each file lives in
    /// Common/AnalogKeyboard/Data and holds one object whose properties are
    /// named tables. A table is an array of key codes indexed by the position
    /// the keyboard reports, 0 where no key sits. Files load on first use,
    /// on the sweep's worker, and stay cached.
    /// </summary>
    public static class AnalogKeyboardData
    {
        private const string ResourcePrefix = "PadForge.Engine.Common.AnalogKeyboard.Data.";

        private static readonly ConcurrentDictionary<string, JsonElement> _files = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, int[]> _tables = new(StringComparer.Ordinal);

        /// <summary>The root object of <paramref name="file"/> (for example
        /// "attackshark.json"). Throws when the file is not embedded, which
        /// is a build defect, not a runtime condition.</summary>
        public static JsonElement File(string file)
            => _files.GetOrAdd(file, Load);

        /// <summary>A table of <paramref name="file"/> as key codes by
        /// position, or null when the file has no such table.</summary>
        public static int[] Table(string file, string name)
        {
            string key = file + "|" + name;
            if (_tables.TryGetValue(key, out var cached)) return cached;
            var root = File(file);
            if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
                return null;
            var table = new int[element.GetArrayLength()];
            int i = 0;
            foreach (var item in element.EnumerateArray()) table[i++] = item.GetInt32();
            return _tables.GetOrAdd(key, table);
        }

        /// <summary>The distinct nonzero codes of a table in position order,
        /// the picker's list for a keyboard read through it.</summary>
        public static int[] KeysOf(int[] table)
        {
            if (table == null) return null;
            var seen = new HashSet<int>();
            var list = new List<int>();
            foreach (int code in table)
                if (code > 0 && code < AnalogKeyInputState.CodeCount && seen.Add(code)) list.Add(code);
            return list.ToArray();
        }

        private static JsonElement Load(string file)
        {
            var assembly = typeof(AnalogKeyboardData).Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourcePrefix + file)
                ?? throw new FileNotFoundException("Analog keyboard data not embedded: " + file);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.Clone();
        }
    }
}
