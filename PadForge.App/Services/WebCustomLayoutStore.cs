using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PadForge.Services
{
    /// <summary>
    /// The custom-controller layouts built in the browser (#296 phase 4),
    /// machine-scoped. The JSON blob rides AppSettingsData in PadForge.xml
    /// (the only file beside the exe), deliberately NOT ProfileData: a custom
    /// pad is this machine's input hardware definition, not profile content,
    /// and keeping it out of the profile lanes sidesteps the five-way mirror.
    ///
    /// Shape: a JSON array of layouts,
    ///   { "id": "c1a2...", "name": "My Pad", "widgets": [
    ///       { "kind": "button"|"stick"|"slider"|"dpad"|"touch",
    ///         "x": 0.10, "y": 0.20, "w": 0.12, "h": 0.18,   // fractions of the canvas
    ///         "code": 0,          // button slot / axis index, per kind
    ///         "label": "A" } ] }
    /// The server validates on save and never trusts the browser's ids.
    /// </summary>
    public static class WebCustomLayoutStore
    {
        private static readonly object _lock = new();
        private static string _json = "[]";

        /// <summary>Raised after a mutation, so the settings layer can persist.</summary>
        public static event Action Changed;

        /// <summary>The raw JSON array, for persistence.</summary>
        public static string Json
        {
            get { lock (_lock) return _json; }
        }

        /// <summary>Loads from PadForge.xml at settings load. Invalid or empty
        /// input resets to the empty list rather than throwing.</summary>
        public static void LoadFrom(string json)
        {
            lock (_lock)
            {
                _json = ValidateArray(json) ? json : "[]";
            }
        }

        /// <summary>Adds or replaces one layout (matched by id). Returns the
        /// stored layout's id, or null when the payload is invalid.</summary>
        public static string Upsert(string layoutJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(layoutJson);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (!root.TryGetProperty("widgets", out var widgets) || widgets.ValueKind != JsonValueKind.Array)
                    return null;
                if (widgets.GetArrayLength() == 0 || widgets.GetArrayLength() > 64) return null;

                string id = root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                    && IsSafeId(idProp.GetString())
                    ? idProp.GetString()
                    : Guid.NewGuid().ToString("N").Substring(0, 12);
                string name = root.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                    ? nameProp.GetString().Trim()
                    : "";
                if (string.IsNullOrEmpty(name)) name = "Custom Pad";
                if (name.Length > 40) name = name.Substring(0, 40);

                // Re-serialize through a whitelist so nothing beyond the schema
                // is stored (the browser is untrusted input).
                var clean = new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["name"] = name,
                    ["widgets"] = CleanWidgets(widgets),
                };

                lock (_lock)
                {
                    var list = ParseList(_json);
                    list.RemoveAll(l => IdOf(l) == id);
                    list.Add(clean);
                    _json = JsonSerializer.Serialize(list);
                }
                Changed?.Invoke();
                return id;
            }
            catch { return null; }
        }

        /// <summary>Removes a layout by id. True when something was removed.</summary>
        public static bool Delete(string id)
        {
            if (!IsSafeId(id)) return false;
            bool removed;
            lock (_lock)
            {
                var list = ParseList(_json);
                removed = list.RemoveAll(l => IdOf(l) == id) > 0;
                if (removed) _json = JsonSerializer.Serialize(list);
            }
            if (removed) Changed?.Invoke();
            return removed;
        }

        /// <summary>The layout object for an id, as JSON, or null.</summary>
        public static string Find(string id)
        {
            if (!IsSafeId(id)) return null;
            lock (_lock)
            {
                var list = ParseList(_json);
                foreach (var l in list)
                    if (IdOf(l) == id)
                        return JsonSerializer.Serialize(l);
            }
            return null;
        }

        /// <summary>Extracts a layout's id. Values deserialized into a
        /// Dictionary&lt;string, object&gt; are JsonElement, not string, so a
        /// plain cast returns null (the bug that made Find/Delete miss).</summary>
        private static string IdOf(Dictionary<string, object> l)
        {
            if (l == null || !l.TryGetValue("id", out var v) || v == null) return null;
            if (v is string str) return str;
            if (v is JsonElement je) return je.ValueKind == JsonValueKind.String ? je.GetString() : null;
            return v.ToString();
        }

        private static bool IsSafeId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 32) return false;
            foreach (char c in id)
                if (!char.IsAsciiLetterOrDigit(c)) return false;
            return true;
        }

        private static bool ValidateArray(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.ValueKind == JsonValueKind.Array;
            }
            catch { return false; }
        }

        private static List<Dictionary<string, object>> ParseList(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<List<Dictionary<string, object>>>(json) ?? new();
            }
            catch { return new(); }
        }

        private static List<Dictionary<string, object>> CleanWidgets(JsonElement widgets)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (var w in widgets.EnumerateArray())
            {
                if (w.ValueKind != JsonValueKind.Object) continue;
                string kind = w.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                    ? k.GetString() : null;
                if (kind is not ("button" or "stick" or "slider" or "dpad" or "touch")) continue;

                double Get(string name, double def, double min, double max)
                {
                    if (w.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number)
                        return Math.Clamp(p.GetDouble(), min, max);
                    return def;
                }
                int code = 0;
                if (w.TryGetProperty("code", out var cp) && cp.ValueKind == JsonValueKind.Number)
                {
                    // TryGetInt32, not GetInt32: a non-integral number throws,
                    // and the throw escaped all the way out of Upsert, so ONE
                    // odd widget failed the entire save with "Save failed" and
                    // no way for the user to tell which control was at fault.
                    if (!cp.TryGetInt32(out code))
                        code = (int)Math.Clamp(cp.GetDouble(), 0, 127);
                    code = Math.Clamp(code, 0, 127);
                }
                string label = w.TryGetProperty("label", out var lp) && lp.ValueKind == JsonValueKind.String
                    ? lp.GetString() : "";
                if (label?.Length > 12) label = label.Substring(0, 12);

                result.Add(new Dictionary<string, object>
                {
                    ["kind"] = kind,
                    ["x"] = Math.Round(Get("x", 0.1, 0, 1), 4),
                    ["y"] = Math.Round(Get("y", 0.1, 0, 1), 4),
                    ["w"] = Math.Round(Get("w", 0.1, 0.02, 1), 4),
                    ["h"] = Math.Round(Get("h", 0.1, 0.02, 1), 4),
                    ["code"] = code,
                    ["label"] = label ?? "",
                });
                if (result.Count >= 64) break;
            }
            return result;
        }
    }
}
