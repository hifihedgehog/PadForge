namespace PadForge.SteamWorkshop.Translation
{
    /// <summary>Per-entry outcome of translating one Steam Input binding
    /// (or one aggregate, such as a preset's passthrough recognition).</summary>
    public enum TranslationStatus
    {
        /// <summary>Fully expressed in PadForge terms with matching semantics.</summary>
        Clean = 0,

        /// <summary>Expressed, but with a documented behavioral difference or a
        /// user step required before it fires (reason key says which).</summary>
        Partial = 1,

        /// <summary>Not expressed. The reason key says why.</summary>
        Skipped = 2,

        /// <summary>Config data was malformed or hit a hard limit.</summary>
        Error = 3,
    }
}
