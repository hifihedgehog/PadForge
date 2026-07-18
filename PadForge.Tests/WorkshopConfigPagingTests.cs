using System.Collections.Generic;
using System.Linq;
using PadForge.Views;
using SteamKit2.Internal;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Browse Community Configs paging (#9 follow-up): QueryFiles is
    /// page-based (page/numperpage, total on the response), and the dialog
    /// streams pages as the scroll nears the bottom instead of stopping at
    /// the top 30. The pure decisions live in WorkshopConfigListPager;
    /// these tests pin the page cursor, the cross-page dedupe on
    /// publishedfileid (rank order can shift between fetches), the
    /// ban/legacy filters, the short-page end-of-results signal, and the
    /// silent-page bound that stops a fill when filters eat page after
    /// page.
    /// </summary>
    public class WorkshopConfigPagingTests
    {
        private const int PageSize = 3;
        private const int MaxSilentPages = 4;

        private static WorkshopConfigListPager NewPager() => new(PageSize, MaxSilentPages);

        private static PublishedFileDetails Item(ulong id, bool banned = false,
            string fileUrl = "https://cdn.example/config.vdf")
            => new PublishedFileDetails { publishedfileid = id, banned = banned, file_url = fileUrl };

        /// <summary>A full page of distinct banned items: consumes a fetch
        /// without producing a visible row and without reading as a short
        /// (end-of-results) page.</summary>
        private static List<PublishedFileDetails> SilentPage(ref ulong nextId)
        {
            var page = new List<PublishedFileDetails>();
            for (int i = 0; i < PageSize; i++)
                page.Add(Item(nextId++, banned: true));
            return page;
        }

        [Fact]
        public void FullPage_AdvancesCursor_AndStaysOpen()
        {
            var pager = NewPager();

            var visible = pager.Accept(new List<PublishedFileDetails> { Item(1), Item(2), Item(3) }, showLegacy: false);

            Assert.Equal(new ulong[] { 1, 2, 3 }, visible.Select(d => d.publishedfileid));
            Assert.Equal(2, pager.NextPage);
            Assert.False(pager.Exhausted);
        }

        [Fact]
        public void ShortPage_IsTheEndOfResults()
        {
            var pager = NewPager();

            var visible = pager.Accept(new List<PublishedFileDetails> { Item(1), Item(2) }, showLegacy: false);

            Assert.Equal(2, visible.Count);
            Assert.True(pager.Exhausted);
        }

        [Fact]
        public void EmptyOrNullPage_IsTheEndOfResults()
        {
            var pager = NewPager();
            pager.Accept(null, showLegacy: false);
            Assert.True(pager.Exhausted);

            var pager2 = NewPager();
            pager2.Accept(new List<PublishedFileDetails>(), showLegacy: false);
            Assert.True(pager2.Exhausted);
        }

        [Fact]
        public void RankShiftBetweenPages_LandsAnItemOnce()
        {
            var pager = NewPager();
            pager.Accept(new List<PublishedFileDetails> { Item(1), Item(2), Item(3) }, showLegacy: false);

            // Item 3 slid down a rank between fetches and reappears on page 2.
            var visible = pager.Accept(new List<PublishedFileDetails> { Item(3), Item(4), Item(5) }, showLegacy: false);

            Assert.Equal(new ulong[] { 4, 5 }, visible.Select(d => d.publishedfileid));
        }

        [Fact]
        public void BannedItems_NeverLand()
        {
            var pager = NewPager();

            var visible = pager.Accept(new List<PublishedFileDetails>
            {
                Item(1, banned: true), Item(2), Item(3, banned: true),
            }, showLegacy: false);

            Assert.Equal(new ulong[] { 2 }, visible.Select(d => d.publishedfileid));
        }

        [Fact]
        public void LegacyItems_FollowTheSettingsToggle()
        {
            // Legacy = no file_url (null or empty).
            var hidden = NewPager().Accept(new List<PublishedFileDetails>
            {
                Item(1, fileUrl: null), Item(2, fileUrl: string.Empty), Item(3),
            }, showLegacy: false);
            Assert.Equal(new ulong[] { 3 }, hidden.Select(d => d.publishedfileid));

            var shown = NewPager().Accept(new List<PublishedFileDetails>
            {
                Item(1, fileUrl: null), Item(2, fileUrl: string.Empty), Item(3),
            }, showLegacy: true);
            Assert.Equal(new ulong[] { 1, 2, 3 }, shown.Select(d => d.publishedfileid));
        }

        [Fact]
        public void SilentPageRun_TripsTheBound()
        {
            var pager = NewPager();
            ulong id = 100;

            pager.BeginFill();
            for (int i = 0; i < MaxSilentPages - 1; i++)
            {
                pager.Accept(SilentPage(ref id), showLegacy: false);
                Assert.False(pager.Exhausted);
            }

            pager.Accept(SilentPage(ref id), showLegacy: false);
            Assert.True(pager.Exhausted);
        }

        [Fact]
        public void ProductivePage_ResetsTheSilentCount()
        {
            var pager = NewPager();
            ulong id = 100;

            pager.BeginFill();
            for (int i = 0; i < MaxSilentPages - 1; i++)
                pager.Accept(SilentPage(ref id), showLegacy: false);

            // A page with one visible row resets the run.
            pager.Accept(new List<PublishedFileDetails> { Item(1), Item(id++, banned: true), Item(id++, banned: true) },
                showLegacy: false);
            Assert.False(pager.Exhausted);

            for (int i = 0; i < MaxSilentPages - 1; i++)
            {
                pager.Accept(SilentPage(ref id), showLegacy: false);
                Assert.False(pager.Exhausted);
            }
            pager.Accept(SilentPage(ref id), showLegacy: false);
            Assert.True(pager.Exhausted);
        }

        [Fact]
        public void BeginFill_ScopesTheBoundToOneFill()
        {
            var pager = NewPager();
            ulong id = 100;

            pager.BeginFill();
            for (int i = 0; i < MaxSilentPages - 1; i++)
                pager.Accept(SilentPage(ref id), showLegacy: false);
            Assert.False(pager.Exhausted);

            // The next scroll gesture opens a new fill; the old run's count
            // must not spill into it.
            pager.BeginFill();
            for (int i = 0; i < MaxSilentPages - 1; i++)
            {
                pager.Accept(SilentPage(ref id), showLegacy: false);
                Assert.False(pager.Exhausted);
            }
        }

        [Fact]
        public void RepeatedPage_CountsSilent_SoAStuckCursorTerminates()
        {
            // A page of nothing but already-seen ids (a cached or rank-froze
            // response) produces zero rows; the bound must end the fill
            // rather than let it spin forever.
            var pager = NewPager();
            var samePage = new List<PublishedFileDetails> { Item(1), Item(2), Item(3) };
            pager.BeginFill();
            pager.Accept(samePage, showLegacy: false);

            for (int i = 0; i < MaxSilentPages - 1; i++)
            {
                pager.Accept(samePage, showLegacy: false);
                Assert.False(pager.Exhausted);
            }
            pager.Accept(samePage, showLegacy: false);
            Assert.True(pager.Exhausted);
        }

        [Fact]
        public void Reset_StartsAFreshList()
        {
            var pager = NewPager();
            pager.Accept(new List<PublishedFileDetails> { Item(1), Item(2) }, showLegacy: false);
            Assert.True(pager.Exhausted);
            Assert.Equal(2, pager.NextPage);

            pager.Reset();

            Assert.Equal(1, pager.NextPage);
            Assert.False(pager.Exhausted);
            // A game or tag switch clears the seen set: the same id lands
            // again on the fresh list.
            var visible = pager.Accept(new List<PublishedFileDetails> { Item(1), Item(2), Item(3) }, showLegacy: false);
            Assert.Equal(new ulong[] { 1, 2, 3 }, visible.Select(d => d.publishedfileid));
        }
    }
}
