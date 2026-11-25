using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PadForge.Services.Automation;

namespace PadForge.Services.Plugins
{
    /// <summary>What a plugin's declared setting is (#600 item 17).
    /// Append-only: the kit's README and the catalog name them by their
    /// words.</summary>
    internal enum PluginSettingKind
    {
        /// <summary>On or off.</summary>
        Switch,
        /// <summary>A number in a range, with a step and a unit.</summary>
        Number,
        /// <summary>One of a list, fixed or kept current by the plugin.</summary>
        Choice,
        /// <summary>A line of text.</summary>
        Text,
        /// <summary>Read only, updated by the plugin.</summary>
        Status,
        /// <summary>An action the plugin handles.</summary>
        Button,
        /// <summary>A position on two axes the user drags.</summary>
        Pad,
        /// <summary>A device's input, picked by pressing it.</summary>
        Input,
        /// <summary>A paragraph.</summary>
        Help,
    }

    /// <summary>One entry of a choice: the value the plugin hears, and the
    /// words the user reads.</summary>
    internal sealed record PluginChoice(string Value, string Words);

    /// <summary>
    /// A setting or control a plugin declared (#600 item 17), as PadForge
    /// keeps it: its words cleaned and capped, its range and its default
    /// read and checked once. A device's own setting carries the key of
    /// the device it belongs to.
    /// </summary>
    internal sealed class PluginDeclaration
    {
        public string Key { get; init; }
        public PluginSettingKind Kind { get; init; }

        /// <summary>The key of the plugin's device it belongs to, or null for
        /// the plugin's own.</summary>
        public string Device { get; init; }

        public string Name { get; init; } = string.Empty;
        public string Help { get; init; } = string.Empty;
        public string Group { get; init; } = string.Empty;
        public string Unit { get; init; } = string.Empty;
        public double Min { get; init; }
        public double Max { get; init; } = 100;
        public double Step { get; init; } = 1;

        /// <summary>The value a new plugin or device starts with, as
        /// <see cref="PluginValues.Normalize"/> writes it.</summary>
        public string Default { get; init; } = string.Empty;

        /// <summary>The entries of a choice. A plugin that keeps the list
        /// current replaces it with a later line.</summary>
        public IReadOnlyList<PluginChoice> Choices { get; set; } = Array.Empty<PluginChoice>();

        /// <summary>A pad that stays where the user lets go of it. A pad
        /// goes back to its center otherwise.</summary>
        public bool Stay { get; init; }

        /// <summary>The key of a switch of the same plugin and device: the
        /// declaration shows only while that switch is on.</summary>
        public string When { get; init; }

        /// <summary>Drawn beside the other compact declarations of its group
        /// in one row, its name inside it: a pad, a short slider, a
        /// toggle.</summary>
        public bool Compact { get; init; }

        /// <summary>The declaration's place in the order it was given.</summary>
        public int Order { get; set; }

        /// <summary>Whether it holds a value the user sets and PadForge
        /// keeps.</summary>
        public bool HoldsValue => Kind is PluginSettingKind.Switch or PluginSettingKind.Number or PluginSettingKind.Choice
            or PluginSettingKind.Text or PluginSettingKind.Pad or PluginSettingKind.Input;
    }

    /// <summary>
    /// Reads a <c>setting</c> line's declaration (#600 item 17) and checks
    /// a value against it. The words may stand on the line
    /// (<c>name="Dead Zone" min=0 max=20</c>) or in the JSON object at its
    /// end, or both: the object's wins.
    /// </summary>
    internal static class PluginDeclarations
    {
        internal const int MostSettings = 64;
        internal const int LongestValue = 1024;
        internal const int MostValueBytes = 16 * 1024;
        internal const int MostChoices = 64;
        internal const int LongestName = 64;
        internal const int LongestHelp = 300;
        internal const int LongestGroup = 64;
        internal const int LongestUnit = 12;
        internal const int LongestChoice = 64;

        /// <summary>The kinds by the word a plugin gives.</summary>
        internal static readonly IReadOnlyDictionary<string, PluginSettingKind> Kinds = new Dictionary<string, PluginSettingKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["switch"] = PluginSettingKind.Switch,
            ["number"] = PluginSettingKind.Number,
            ["choice"] = PluginSettingKind.Choice,
            ["text"] = PluginSettingKind.Text,
            ["status"] = PluginSettingKind.Status,
            ["button"] = PluginSettingKind.Button,
            ["pad"] = PluginSettingKind.Pad,
            ["input"] = PluginSettingKind.Input,
            ["help"] = PluginSettingKind.Help,
        };

        /// <summary>The word of a kind, as the README and the catalog write
        /// it.</summary>
        internal static string Word(PluginSettingKind kind) => kind.ToString().ToLowerInvariant();

        /// <summary>
        /// Reads a declaration from a line's words and its JSON object. Null
        /// and the reason in words when it cannot be read.
        /// </summary>
        internal static PluginDeclaration Parse(AutomationRequest r, out string error)
        {
            var words = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in r.Words)
                if (w.IsPair) words[w.Key] = w.Value;
            JsonElement? json = null;
            JsonDocument doc = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(r.Json))
                {
                    try { doc = JsonDocument.Parse(r.Json, new JsonDocumentOptions { MaxDepth = 4 }); }
                    catch (JsonException) { error = "the object is not JSON"; return null; }
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) { error = "the object is not an object"; return null; }
                    json = doc.RootElement;
                    foreach (var p in doc.RootElement.EnumerateObject())
                        if (p.Value.ValueKind is JsonValueKind.String) words[p.Name] = p.Value.GetString();
                        else if (p.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                            words[p.Name] = p.Value.ValueKind == JsonValueKind.Number
                                ? p.Value.GetDouble().ToString("R", CultureInfo.InvariantCulture)
                                : (p.Value.ValueKind == JsonValueKind.True ? "true" : "false");
                }
                return Read(words, json, out error);
            }
            finally { doc?.Dispose(); }
        }

        /// <summary>Reads a declaration from its words, and its choices from
        /// the object's <c>choices</c>. Used for the built-ins too.</summary>
        internal static PluginDeclaration Read(IReadOnlyDictionary<string, string> words, JsonElement? json, out string error)
        {
            error = null;
            string Get(string name) => words.TryGetValue(name, out var v) ? v : null;

            string key = Get("key");
            if (!ProgramDeviceDeclaration.IsKey(key)) { error = "key is 1 to 64 letters, digits, _, . or -"; return null; }
            string device = Get("device");
            if (device != null && !ProgramDeviceDeclaration.IsKey(device)) { error = "device is a device's key"; return null; }
            string kindWord = Get("kind");
            if (kindWord == null || !Kinds.TryGetValue(kindWord, out var kind))
            {
                error = "kind is switch, number, choice, text, status, button, pad, input or help";
                return null;
            }
            string when = Get("when");
            if (when != null && !ProgramDeviceDeclaration.IsKey(when)) { error = "when names a switch's key"; return null; }

            double min = 0, max = 100, step = 1;
            if (kind == PluginSettingKind.Number)
            {
                if (!TryNumber(Get("min") ?? "0", out min) || !TryNumber(Get("max") ?? "100", out max) || min >= max)
                {
                    error = "a number's min and max are numbers, min below max";
                    return null;
                }
                if (!TryNumber(Get("step") ?? "1", out step) || step <= 0 || step > max - min)
                {
                    error = "a number's step is above 0 and no more than max minus min";
                    return null;
                }
            }

            var choices = new List<PluginChoice>();
            if (kind == PluginSettingKind.Choice)
            {
                if (json is { } o && o.TryGetProperty("choices", out var list))
                {
                    if (!ReadChoices(list, choices, out error)) return null;
                }
                else if (Get("choices") is { } flat)
                {
                    foreach (string part in flat.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        string value = ProgramDeviceDeclaration.Clean(part, LongestChoice);
                        if (value.Length > 0) choices.Add(new PluginChoice(value, value));
                    }
                }
                if (choices.Count > MostChoices) { error = "a choice has up to 64 entries"; return null; }
            }

            var shell = new PluginDeclaration
            {
                Key = key,
                Kind = kind,
                Device = device,
                Name = ProgramDeviceDeclaration.Clean(Get("name"), LongestName),
                Help = ProgramDeviceDeclaration.Clean(Get("help") ?? Get("text"), LongestHelp),
                Group = ProgramDeviceDeclaration.Clean(Get("group"), LongestGroup),
                Unit = ProgramDeviceDeclaration.Clean(Get("unit"), LongestUnit),
                Min = min,
                Max = max,
                Step = step,
                Choices = choices,
                Stay = IsTrue(Get("stay")),
                When = when,
                Compact = string.Equals(Get("look"), "compact", StringComparison.OrdinalIgnoreCase),
            };
            if (shell.Name.Length == 0 && kind != PluginSettingKind.Help) shell = With(shell, name: key);

            // The default, checked as any value is. A choice with no default
            // starts at its first entry.
            string fallback = kind switch
            {
                PluginSettingKind.Switch => "false",
                PluginSettingKind.Number => PluginValues.FormatNumber(Math.Clamp(0, min, max)),
                PluginSettingKind.Choice => choices.Count > 0 ? choices[0].Value : string.Empty,
                PluginSettingKind.Pad => "0,0",
                _ => string.Empty,
            };
            string given = Get("default");
            string normalized = fallback;
            if (given != null && shell.HoldsValue)
            {
                normalized = PluginValues.Normalize(shell, given, out string why);
                if (normalized == null) { error = "default: " + why; return null; }
            }
            return With(shell, defaultValue: normalized);
        }

        private static PluginDeclaration With(PluginDeclaration d, string name = null, string defaultValue = null) => new()
        {
            Key = d.Key, Kind = d.Kind, Device = d.Device, Name = name ?? d.Name, Help = d.Help, Group = d.Group,
            Unit = d.Unit, Min = d.Min, Max = d.Max, Step = d.Step, Default = defaultValue ?? d.Default,
            Choices = d.Choices, Stay = d.Stay, When = d.When, Compact = d.Compact, Order = d.Order,
        };

        /// <summary>Reads a list of choices: strings, or objects with
        /// <c>value</c> and <c>name</c>.</summary>
        internal static bool ReadChoices(JsonElement list, List<PluginChoice> choices, out string error)
        {
            error = null;
            if (list.ValueKind != JsonValueKind.Array) { error = "choices is a list"; return false; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in list.EnumerateArray())
            {
                string value, name;
                if (item.ValueKind == JsonValueKind.String) value = name = item.GetString();
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    value = item.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : value;
                }
                else { error = "a choice is a string or an object with value and name"; return false; }
                value = ProgramDeviceDeclaration.Clean(value, LongestChoice);
                name = ProgramDeviceDeclaration.Clean(name, LongestChoice);
                if (value.Length == 0) { error = "a choice's value is not empty"; return false; }
                if (!seen.Add(value)) { error = "the choice " + value + " is given twice"; return false; }
                choices.Add(new PluginChoice(value, name.Length > 0 ? name : value));
                if (choices.Count > MostChoices) { error = "a choice has up to 64 entries"; return false; }
            }
            return true;
        }

        internal static bool TryNumber(string text, out double value)
            => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

        internal static bool IsTrue(string text) => text is "1" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
                                                     || string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase)
                                                     || string.Equals(text, "on", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The values of declared settings as PadForge keeps them: one text
    /// form for each kind, in no language, so PadForge.xml and the wire
    /// read the same. A switch is <c>true</c> or <c>false</c>, a number its
    /// invariant digits on its step, a choice its entry's value, a pad
    /// <c>x,y</c> from -100 to 100 each, an input the device's ID and the
    /// control's name.
    /// </summary>
    internal static class PluginValues
    {
        /// <summary>The value in its kept form, or null and why when the
        /// declaration does not take it.</summary>
        internal static string Normalize(PluginDeclaration d, string value, out string error)
        {
            error = null;
            value ??= string.Empty;
            if (Encoding.UTF8.GetByteCount(value) > PluginDeclarations.LongestValue) { error = "a value is up to 1 KB"; return null; }
            switch (d.Kind)
            {
                case PluginSettingKind.Switch:
                    if (PluginDeclarations.IsTrue(value)) return "true";
                    if (value is "0" || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)) return "false";
                    error = d.Name + " is true or false";
                    return null;
                case PluginSettingKind.Number:
                {
                    if (!PluginDeclarations.TryNumber(value.Trim().TrimEnd('%'), out double n))
                    {
                        error = d.Name + " takes a number from " + FormatNumber(d.Min) + " to " + FormatNumber(d.Max);
                        return null;
                    }
                    if (n < d.Min - d.Step * 1e-9 || n > d.Max + d.Step * 1e-9)
                    {
                        error = d.Name + " takes a number from " + FormatNumber(d.Min) + " to " + FormatNumber(d.Max);
                        return null;
                    }
                    return FormatNumber(OnStep(d, n));
                }
                case PluginSettingKind.Choice:
                    foreach (var c in d.Choices)
                        if (string.Equals(c.Value, value, StringComparison.Ordinal)) return c.Value;
                    foreach (var c in d.Choices)
                        if (string.Equals(c.Words, value, StringComparison.OrdinalIgnoreCase)) return c.Value;
                    error = d.Name + " is one of its choices";
                    return null;
                case PluginSettingKind.Text:
                    return ProgramDeviceDeclaration.Clean(value, PluginDeclarations.LongestValue);
                case PluginSettingKind.Pad:
                {
                    var parts = value.Split(',');
                    if (parts.Length != 2 || !PluginDeclarations.TryNumber(parts[0].Trim(), out double x)
                        || !PluginDeclarations.TryNumber(parts[1].Trim(), out double y)
                        || x < -100 || x > 100 || y < -100 || y > 100)
                    {
                        error = d.Name + " is x,y, each from -100 to 100";
                        return null;
                    }
                    return FormatNumber(Math.Round(x, 1)) + "," + FormatNumber(Math.Round(y, 1));
                }
                case PluginSettingKind.Input:
                    return ProgramDeviceDeclaration.Clean(value, 128);
                default:
                    error = d.Name + " holds no value";
                    return null;
            }
        }

        /// <summary>A number on the declaration's step from its min, inside
        /// its range.</summary>
        internal static double OnStep(PluginDeclaration d, double n)
        {
            double steps = Math.Round((n - d.Min) / d.Step);
            double on = d.Min + steps * d.Step;
            // Keep as many decimals as the step has, so 0.1 steps read 0.3,
            // not 0.30000000000000004.
            int decimals = Decimals(d.Step);
            on = Math.Round(on, Math.Min(decimals, 10));
            return Math.Clamp(on, d.Min, d.Max);
        }

        internal static int Decimals(double step)
        {
            string text = FormatNumber(step);
            int dot = text.IndexOf('.');
            return dot < 0 ? 0 : text.Length - dot - 1;
        }

        internal static string FormatNumber(double n) => n.ToString("0.##########", CultureInfo.InvariantCulture);

        /// <summary>A pad's two axes from its kept form.</summary>
        internal static (double X, double Y) Pad(string value)
        {
            var parts = (value ?? string.Empty).Split(',');
            if (parts.Length == 2 && PluginDeclarations.TryNumber(parts[0], out double x) && PluginDeclarations.TryNumber(parts[1], out double y))
                return (Math.Clamp(x, -100, 100), Math.Clamp(y, -100, 100));
            return (0, 0);
        }
    }
}
