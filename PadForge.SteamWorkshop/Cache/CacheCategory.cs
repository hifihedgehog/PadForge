namespace PadForge.SteamWorkshop.Cache
{
    /// <summary>
    /// The kinds of data cached under <c>%LOCALAPPDATA%\PadForge\SteamWorkshopCache</c>.
    /// Each category is a subdirectory with its own default TTL. Categories share one of
    /// two eviction budgets: everything except <see cref="Art"/> draws on the general
    /// budget; artwork has its own separate budget.
    /// </summary>
    public enum CacheCategory
    {
        /// <summary>Store-search results keyed by query (24 h).</summary>
        Games,

        /// <summary>App metadata keyed by app id (7 d).</summary>
        Apps,

        /// <summary>Workshop QueryFiles results keyed by app/rank/page (24 h).</summary>
        Search,

        /// <summary>Per-file published-file metadata keyed by file id (24 h).</summary>
        Details,

        /// <summary>Raw VDF blobs keyed by file id + time_updated (immutable, evict by budget).</summary>
        Vdf,

        /// <summary>Creator persona names keyed by Steam id (7 d).</summary>
        Personas,

        /// <summary>Artwork bitmaps keyed by app id + CDN filename (separate budget).</summary>
        Art,
    }
}
