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
    ///   { "id": "c1a2...", "name": "My Pad",
    ///     "aspect": 2.16,         // the screen the pad was arranged on, its width over its height
    ///     "aspect2": 0.46,        // the screen of its second arrangement, when it has one
    ///     "widgets": [
    ///       { "kind": "button"|"stick"|"slider"|"dpad"|"touch",
    ///         "x": 0.10, "y": 0.20, "w": 0.12, "h": 0.18,   // shares of that screen's width and height
    ///         "x2": 0.1, "y2": 0.7, "w2": 0.2, "h2": 0.09,  // the same on the second screen
    ///         "code": 0,          // button slot / axis index, per kind
    ///         "label": "A" } ] }
    /// A pad has an arrangement for a screen held sideways, one for a screen
    /// held upright, or both (js/pad_fit.js draws them). The shape of each
    /// arrangement's screen is what lets a control keep its shape on any
    /// other. A layout saved before 6 has no aspect: its numbers were shares
    /// of whatever screen it was built on, and the page works that screen's
    /// shape out from its round controls.
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
                    ? PadForge.Engine.Common.XmlSafeText.Clean(nameProp.GetString()).Trim()
                    : "";
                if (string.IsNullOrEmpty(name)) name = "Custom Pad";
                if (name.Length > 40) name = name.Substring(0, 40);

                // The shape of the screen each arrangement was made on. The
                // second counts only beside a first and only when it is for
                // the other way of holding the screen: one arrangement is for
                // a screen held sideways and the other for one held upright.
                double aspect = Aspect(root, "aspect");
                double aspect2 = aspect > 0 ? Aspect(root, "aspect2") : 0;
                if (aspect2 > 0 && (aspect >= 1) == (aspect2 >= 1)) aspect2 = 0;

                // Re-serialize through a whitelist so nothing beyond the schema
                // is stored (the browser is untrusted input).
                var clean = new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["name"] = name,
                };
                if (aspect > 0) clean["aspect"] = aspect;
                if (aspect2 > 0) clean["aspect2"] = aspect2;
                clean["widgets"] = CleanWidgets(widgets, aspect2 > 0);

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

        /// <summary>The narrowest and the widest screen an arrangement can be
        /// kept for, as width over height. js/pad_fit.js holds the same two.</summary>
        internal const double AspectLeast = 0.2, AspectMost = 5;

        /// <summary>A screen's shape from a layout, or 0 when it has none
        /// or one no screen has.</summary>
        private static double Aspect(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number) return 0;
            double value = p.GetDouble();
            if (double.IsNaN(value) || value < AspectLeast || value > AspectMost) return 0;
            return Math.Round(value, 4);
        }

        private static List<Dictionary<string, object>> CleanWidgets(JsonElement widgets, bool second)
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

                var clean = new Dictionary<string, object>
                {
                    ["kind"] = kind,
                    ["x"] = Math.Round(Get("x", 0.1, 0, 1), 4),
                    ["y"] = Math.Round(Get("y", 0.1, 0, 1), 4),
                    ["w"] = Math.Round(Get("w", 0.1, 0.02, 1), 4),
                    ["h"] = Math.Round(Get("h", 0.1, 0.02, 1), 4),
                };
                // Its place in the pad's second arrangement. A control that
                // came without one is given a place by the page.
                if (second && w.TryGetProperty("w2", out _) && w.TryGetProperty("h2", out _))
                {
                    clean["x2"] = Math.Round(Get("x2", 0.1, 0, 1), 4);
                    clean["y2"] = Math.Round(Get("y2", 0.1, 0, 1), 4);
                    clean["w2"] = Math.Round(Get("w2", 0.1, 0.02, 1), 4);
                    clean["h2"] = Math.Round(Get("h2", 0.1, 0.02, 1), 4);
                }
                clean["code"] = code;
                clean["label"] = label ?? "";
                result.Add(clean);
                if (result.Count >= 64) break;
            }
            return result;
        }
    }
}
