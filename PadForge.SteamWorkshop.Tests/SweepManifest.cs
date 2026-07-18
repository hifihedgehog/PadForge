using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// Committed record of one wild-corpus harvest run: which configs the
    /// sweep pulled from the live top of each game's Workshop list, and the
    /// content hashes that pin what was translated. Written by
    /// tools/SteamWorkshopSweep (which links this file as a shared source)
    /// and round-trip-tested here so the committed manifest stays readable
    /// by both.
    /// </summary>
    internal sealed class SweepManifest
    {
        /// <summary>ISO 8601 UTC timestamp of the harvest run.</summary>
        public string HarvestedUtc { get; set; } = "";

        public List<SweepManifestEntry> Entries { get; set; } = new();

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        public string ToJson() => JsonSerializer.Serialize(this, Options);

        public static SweepManifest FromJson(string json) =>
            JsonSerializer.Deserialize<SweepManifest>(json, Options);

        public void Save(string path) => File.WriteAllText(path, ToJson());

        public static SweepManifest Load(string path) => FromJson(File.ReadAllText(path));
    }

    /// <summary>One harvested config. The title rides as a SHA-256 hash
    /// (arbitrary user text stays out of the repo); the VDF hash pins the
    /// exact bytes in the gitignored cache that the sweep translated.</summary>
    internal sealed class SweepManifestEntry
    {
        public int AppId { get; set; }

        public string AppName { get; set; } = "";

        public long FileId { get; set; }

        /// <summary>Hex SHA-256 of the UTF-8 title string.</summary>
        public string TitleSha256 { get; set; } = "";

        /// <summary>Hex SHA-256 of the cached VDF file bytes.</summary>
        public string VdfSha256 { get; set; } = "";
    }
}
