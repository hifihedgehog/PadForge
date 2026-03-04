using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PadForge.Common
{
    public class GameConfigEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("exeNames")]
        public string[] ExeNames { get; set; }

        [JsonPropertyName("label")]
        public string Label { get; set; }

        [JsonPropertyName("author")]
        public string Author { get; set; }

        [JsonPropertyName("notes")]
        public string Notes { get; set; }

        [JsonPropertyName("recommendedOutputType")]
        public string RecommendedOutputType { get; set; }

        [JsonPropertyName("settings")]
        public Dictionary<string, JsonElement> Settings { get; set; }

        [JsonIgnore]
        public bool EnableDsuMotionServer =>
            Settings != null &&
            Settings.TryGetValue("enableDsuMotionServer", out var val) &&
            val.ValueKind == JsonValueKind.True;
    }

    public static class GameConfigDatabase
    {
        private static List<GameConfigEntry> _entries = new();

        public static IReadOnlyList<GameConfigEntry> All => _entries;

        public static void Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "gameconfigs.json");
            if (!File.Exists(path))
                return;

            try
            {
                var json = File.ReadAllText(path);
                var doc = JsonDocument.Parse(json);
                var gamesElement = doc.RootElement.GetProperty("games");
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                _entries = JsonSerializer.Deserialize<List<GameConfigEntry>>(gamesElement.GetRawText(), options) ?? new();
            }
            catch
            {
                _entries = new();
            }
        }

        public static List<GameConfigEntry> FindByExeName(string exeName)
        {
            var results = new List<GameConfigEntry>();
            if (string.IsNullOrWhiteSpace(exeName))
                return results;

            var target = Path.GetFileNameWithoutExtension(exeName);
            foreach (var entry in _entries)
            {
                if (entry.ExeNames == null) continue;
                foreach (var exe in entry.ExeNames)
                {
                    var candidate = Path.GetFileNameWithoutExtension(exe);
                    if (string.Equals(target, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(entry);
                        break;
                    }
                }
            }
            return results;
        }
    }
}
