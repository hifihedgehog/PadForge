using System.Collections.Generic;
using SteamKit2.Internal;

namespace PadForge.Views
{
    /// <summary>
    /// Paging state for the Browse Community Configs list. QueryFiles is
    /// page-based (page/numperpage on the request, total on the response),
    /// and a big game's catalog runs six figures (Skyrim SE: 155k+), so the
    /// list streams page by page instead of stopping at the first response.
    /// This class owns the pure decisions: which page to ask for next,
    /// which returned items are visible (ban and legacy filters, cross-page
    /// dedupe on publishedfileid, because rank order can shift between
    /// fetches), when a short page ends the results, and when a run of
    /// filtered-out pages should stop a fill. The dialog keeps the async
    /// fetch loop and every UI concern.
    /// </summary>
    internal sealed class WorkshopConfigListPager
    {
        private readonly int _pageSize;
        private readonly int _maxSilentPages;
        private readonly HashSet<ulong> _seen = new HashSet<ulong>();
        private int _silentPages;

        /// <param name="pageSize">QueryFiles numperpage; a response shorter
        /// than this is Steam's end-of-results signal.</param>
        /// <param name="maxSilentPages">Bound on consecutive pages the
        /// ban/legacy filters ate whole within a single fill. A run this
        /// long reads as the end of the importable results; without it a
        /// mostly-legacy catalog could keep a fill fetching indefinitely.</param>
        public WorkshopConfigListPager(int pageSize, int maxSilentPages)
        {
            _pageSize = pageSize;
            _maxSilentPages = maxSilentPages;
        }

        /// <summary>Next QueryFiles page to request (1-based).</summary>
        public int NextPage { get; private set; } = 1;

        /// <summary>True once a short page (or a silent-page run) marked the
        /// end of the results; no further fetches should run.</summary>
        public bool Exhausted { get; private set; }

        /// <summary>Back to page 1 with nothing seen: a game or tag switch
        /// starts a fresh list.</summary>
        public void Reset()
        {
            NextPage = 1;
            Exhausted = false;
            _silentPages = 0;
            _seen.Clear();
        }

        /// <summary>Opens a fill (one bounded run of page fetches): the
        /// silent-page bound counts within a single fill only.</summary>
        public void BeginFill() => _silentPages = 0;

        /// <summary>
        /// Consumes one fetched page and returns the items the list should
        /// show: banned items drop, legacy items drop unless shown, and an
        /// id already seen on an earlier page drops (a rank shift between
        /// fetches must not land a row twice). Advances the page cursor,
        /// marks exhaustion on a short page, and trips the silent-page
        /// bound when filters ate the whole page.
        /// </summary>
        public List<PublishedFileDetails> Accept(IReadOnlyList<PublishedFileDetails> details, bool showLegacy)
        {
            details ??= new List<PublishedFileDetails>();
            var visible = new List<PublishedFileDetails>();
            foreach (var d in details)
            {
                if (d.banned) continue;
                if (!showLegacy && string.IsNullOrEmpty(d.file_url)) continue;
                if (!_seen.Add(d.publishedfileid)) continue;
                visible.Add(d);
            }

            NextPage++;
            if (details.Count < _pageSize)
                Exhausted = true;

            if (visible.Count > 0)
                _silentPages = 0;
            else if (++_silentPages >= _maxSilentPages)
                Exhausted = true;

            return visible;
        }
    }
}
