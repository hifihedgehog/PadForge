using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Model
{
    /// <summary>
    /// A typed, validated Steam Input controller configuration, built from a parsed VDF
    /// document (the <c>controller_mappings</c> root). Configurations older than schema
    /// version 3 (pre-2017) are rejected.
    /// </summary>
    public sealed class SteamInputConfig
    {
        public string Title { get; }

        public string Description { get; }

        public string CreatorSteamId { get; }

        public int Version { get; }

        public string ControllerType { get; }

        /// <summary>Per-language localized <c>title</c>/<c>description</c> strings, keyed by language then field.</summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Localization { get; }

        public IReadOnlyList<SteamInputGroup> Groups { get; }

        public IReadOnlyList<SteamInputPreset> Presets { get; }

        private SteamInputConfig(string title, string description, string creatorSteamId, int version,
            string controllerType, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> localization,
            IReadOnlyList<SteamInputGroup> groups, IReadOnlyList<SteamInputPreset> presets)
        {
            Title = title;
            Description = description;
            CreatorSteamId = creatorSteamId;
            Version = version;
            ControllerType = controllerType;
            Localization = localization;
            Groups = groups;
            Presets = presets;
        }

        /// <summary>
        /// Builds a config from a parsed VDF root. Accepts either the document root (whose
        /// single child is <c>controller_mappings</c>) or the <c>controller_mappings</c> node
        /// directly. Throws <see cref="SteamInputConfigException"/> on a missing mappings
        /// object, a missing / non-numeric version, or a version below 3.
        /// </summary>
        public static SteamInputConfig FromVdf(VdfNode root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));

            var mappings = root["controller_mappings"];
            if (mappings.IsMissing) mappings = root; // tolerate being handed the mappings node
            if (!mappings.IsObject)
                throw new SteamInputConfigException("Not a Steam Input config: 'controller_mappings' object is missing.");

            var version = mappings["version"].AsInt;
            if (version == null)
                throw new SteamInputConfigException("Steam Input config is missing a numeric 'version'.");
            if (version.Value < 3)
                throw new SteamInputConfigException(
                    $"Steam Input config version {version.Value} (pre-2017 schema). Translator targets version 3 only.");

            var localization = ParseLocalization(mappings["localization"]);
            var groups = mappings.Multi("group").Select(SteamInputGroup.FromVdf).ToList();
            var presets = mappings.Multi("preset").Select(SteamInputPreset.FromVdf).ToList();

            return new SteamInputConfig(
                mappings["title"].AsString,
                mappings["description"].AsString,
                mappings["creator"].AsString,
                (int)version.Value,
                mappings["controller_type"].AsString,
                localization,
                groups,
                presets);
        }

        private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ParseLocalization(VdfNode node)
        {
            Dictionary<string, IReadOnlyDictionary<string, string>> byLanguage = null;
            foreach (var lang in node.Children)
            {
                if (!lang.Value.IsObject) continue;
                byLanguage ??= new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                byLanguage[lang.Key] = VdfModelHelpers.ScalarSettings(lang.Value);
            }
            return (IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>)byLanguage
                ?? EmptyLocalization;
        }

        private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> EmptyLocalization =
            new Dictionary<string, IReadOnlyDictionary<string, string>>(0);
    }
}
