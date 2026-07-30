using System.Collections.Generic;

namespace PadForge.SteamWorkshop.Translation
{
    /// <summary>Inputs to one <see cref="ConfigTranslator.Translate"/> run.</summary>
    public sealed class TranslationOptions
    {
        /// <summary>Workshop published-file id. Feeds the deterministic
        /// shift-layer names (<c>Layer_{fileId}_{presetId}</c>). 0 is legal
        /// (local file / tests) and keeps names deterministic.</summary>
        public long FileId { get; init; }

        /// <summary>Language key for the config's localized title/description
        /// (<c>"english"</c>, <c>"german"</c>, ...). The root title wins when
        /// it is a real string; localization is the fallback for empty or
        /// <c>#token</c> titles (template-derived configs).</summary>
        public string PreferredLanguage { get; init; } = "english";

        /// <summary>Overrides the profile name derived from the config title.</summary>
        public string ProfileNameOverride { get; init; }

        /// <summary>Presets (action sets) to translate. Null = all.</summary>
        public IReadOnlySet<int> IncludedPresetIds { get; init; }
    }
}
