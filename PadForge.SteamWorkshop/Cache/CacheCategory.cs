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

        /// <summary>Workshop QueryFiles results keyed by app/rank/page (24 h).</summary>
        Search,

        /// <summary>Raw VDF blobs keyed by file id + time_updated (immutable, evict by budget).</summary>
        Vdf,

        /// <summary>Creator persona names keyed by Steam id (7 d).</summary>
        Personas,

        /// <summary>
        /// Artwork bitmaps keyed by app id + CDN filename (separate budget). Entries
        /// carry a 7 d freshness window (weekly hero re-fetch), and stale entries are
        /// kept as the offline fallback rather than deleted.
        /// </summary>
        Art,
    }
}
