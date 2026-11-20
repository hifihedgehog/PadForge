using System;
using System.Collections.Generic;
using System.Text;

namespace PadForge.Engine.Data
{
    /// <summary>One layer's gyro activation: the buttons that turn the gyro
    /// on and how. <see cref="Mode"/> is "Hold" (on while held), "Release"
    /// (on while not held) or "Toggle" (flips on each press). With
    /// <see cref="All"/> every button must be held, else any one. A layer
    /// with no buttons keeps the gyro on.</summary>
    public sealed class GyroActivationEntry
    {
        public string Mode = "Hold";
        public bool All;
        public string[] Buttons = Array.Empty<string>();
    }

    /// <summary>The text form of <see cref="PadSetting.GyroActivation"/>:
    /// "layer=Mode,Any|All,button|button" entries joined by ";". The engaged
    /// layer's entry applies, else Base's, else none. Buttons are
    /// device-free descriptors, held on any device of the slot.</summary>
    public static class GyroActivation
    {
        /// <summary>The entries by layer mask, or null for empty text.
        /// Malformed entries are skipped.</summary>
        public static Dictionary<string, GyroActivationEntry> Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var map = new Dictionary<string, GyroActivationEntry>(StringComparer.Ordinal);
            foreach (string entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = entry.IndexOf('=');
                if (eq <= 0) continue;
                string[] parts = entry.Substring(eq + 1).Split(',');
                if (parts.Length < 3) continue;
                map[entry.Substring(0, eq)] = new GyroActivationEntry
                {
                    Mode = parts[0] is "Release" or "Toggle" ? parts[0] : "Hold",
                    All = parts[1] == "All",
                    // A descriptor holds no comma, but rejoin the tail so a
                    // stray one cannot drop the buttons after it.
                    Buttons = string.Join(",", parts, 2, parts.Length - 2)
                        .Split('|', StringSplitOptions.RemoveEmptyEntries),
                };
            }
            return map.Count == 0 ? null : map;
        }

        /// <summary>The text for <paramref name="entries"/>, in the order
        /// given. Null or empty gives "".</summary>
        public static string Encode(IEnumerable<KeyValuePair<string, GyroActivationEntry>> entries)
        {
            if (entries == null) return "";
            var sb = new StringBuilder();
            foreach (var kv in entries)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(kv.Key).Append('=')
                  .Append(kv.Value.Mode is "Release" or "Toggle" ? kv.Value.Mode : "Hold").Append(',')
                  .Append(kv.Value.All ? "All" : "Any").Append(',')
                  .Append(string.Join("|", kv.Value.Buttons ?? Array.Empty<string>()));
            }
            return sb.ToString();
        }
    }
}
