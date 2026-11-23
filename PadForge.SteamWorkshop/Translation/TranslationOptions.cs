using System;
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

        /// <summary>Language keys for the config's localized title,
        /// description and set titles (<c>"english"</c>, <c>"german"</c>,
        /// ...), best first, with English tried after them. The root title
        /// wins when it is a real string. An empty or <c>#token</c> title
        /// (template-derived configs) is read from the config's localization:
        /// a <c>#token</c> title by its token in these languages and English,
        /// then in any language the config carries in ordinal order. A title
        /// still unread, and an empty one, is read by its field name in these
        /// languages and English only.</summary>
        public IReadOnlyList<string> PreferredLanguages { get; init; } = new[] { "english" };

        /// <summary>Overrides the profile name derived from the config title.</summary>
        public string ProfileNameOverride { get; init; }

        /// <summary>Presets (action sets) to translate. Null = all.</summary>
        public IReadOnlySet<int> IncludedPresetIds { get; init; }

        /// <summary>The format for a name the translator writes, looked up by
        /// its string-table key (the keys of <see cref="TranslationNames"/>).
        /// The translator has no string table, so the app passes its own. Null,
        /// or a lookup that returns nothing or the key itself, takes
        /// <see cref="TranslationNames.English"/>.</summary>
        public Func<string, string> NameFormat { get; init; }
    }
}
