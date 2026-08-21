using System;
using System.Collections.Generic;
using System.Threading;
using PadForge;
using Wpf.Ui.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #340 (Xaklse): after running the Welcome Tour from the
    /// Settings page, BOTH "Dashboard" and "Settings" wore the orange
    /// active bar while only the Dashboard was rendered.
    ///
    /// <para>The rail is two collections. Dashboard, Profiles, Devices and
    /// the slots are MenuItems; Settings and About are FooterMenuItems. The
    /// tour restyled the rail with a hand-rolled loop that set its target
    /// active without clearing anything and swept only MenuItems, so the
    /// footer's Settings kept the bar it already had. The reporter's own
    /// diagnosis, that the tour leaves stale active-state styling on the
    /// previous section without clearing it, was exactly right.</para>
    ///
    /// <para>The same shape sat in the rail rebuild, where it is reachable
    /// far more often: any slot create, delete or reorder while Settings or
    /// About is open searched MenuItems only, found no match, lit the
    /// Dashboard fallback, and left the footer item lit as well.</para>
    ///
    /// <para>WPF elements demand STA, so each body runs on its own STA
    /// thread and rethrows.</para>
    /// </summary>
    public class NavActiveStateTests
    {
        private static void RunSta(Action body)
        {
            Exception failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(15000), "STA test body timed out");
            if (failure != null) throw failure;
        }

        private static NavigationViewItem Item(string tag, bool active = false)
            => new NavigationViewItem { Tag = tag, IsActive = active };

        /// <summary>The rail as MainWindow.xaml declares it.</summary>
        private static (List<object> menu, List<object> footer) Rail(
            string activeTag = null)
        {
            var menu = new List<object>
            {
                Item("Dashboard"), Item("Profiles"), Item("Devices"), Item("Pad1"),
            };
            var footer = new List<object>
            {
                new NavigationViewItemSeparator(),
                Item("Settings"), Item("About"),
            };
            foreach (var o in menu)
                if (o is NavigationViewItem n && n.Tag?.ToString() == activeTag) n.IsActive = true;
            foreach (var o in footer)
                if (o is NavigationViewItem n && n.Tag?.ToString() == activeTag) n.IsActive = true;
            return (menu, footer);
        }

        private static List<string> ActiveTags(List<object> menu, List<object> footer)
        {
            var lit = new List<string>();
            foreach (var o in menu)
                if (o is NavigationViewItem n && n.IsActive) lit.Add(n.Tag?.ToString());
            foreach (var o in footer)
                if (o is NavigationViewItem n && n.IsActive) lit.Add(n.Tag?.ToString());
            return lit;
        }

        /// <summary>THE REPORTED BUG. Settings is lit (the user was on that
        /// page), the tour activates Dashboard, and exactly one bar must
        /// remain. The old code left two.</summary>
        [Fact]
        public void TourFromSettings_LeavesOnlyTheDashboardLit()
        {
            RunSta(() =>
            {
                var (menu, footer) = Rail(activeTag: "Settings");
                Assert.True(MainWindow.ApplyNavActive(menu, footer, "Dashboard"));
                Assert.Equal(new[] { "Dashboard" }, ActiveTags(menu, footer));
            });
        }

        /// <summary>A footer item can be the one selected, and selecting it
        /// clears the menu side. The sweep runs in both directions.</summary>
        [Fact]
        public void SelectingAFooterItem_ClearsTheMenuSide()
        {
            RunSta(() =>
            {
                var (menu, footer) = Rail(activeTag: "Dashboard");
                Assert.True(MainWindow.ApplyNavActive(menu, footer, "Settings"));
                Assert.Equal(new[] { "Settings" }, ActiveTags(menu, footer));
            });
        }

        /// <summary>THE SECOND INSTANCE. The rail rebuild restores the
        /// previously selected tag, and a deleted slot leaves no match. The
        /// caller falls back to the Dashboard, and the footer item that was
        /// open must not stay lit alongside it.</summary>
        [Fact]
        public void RebuildFallback_AfterTheOpenSlotIsDeleted_LightsOnlyTheFallback()
        {
            RunSta(() =>
            {
                var (menu, footer) = Rail(activeTag: "Settings");
                // The rebuild asks for the vanished tag first.
                Assert.False(MainWindow.ApplyNavActive(menu, footer, "Pad9"));
                // A miss must leave nothing lit, or the fallback would be
                // a second bar rather than the only one.
                Assert.Empty(ActiveTags(menu, footer));
                Assert.True(MainWindow.ApplyNavActive(menu, footer, "Dashboard"));
                Assert.Equal(new[] { "Dashboard" }, ActiveTags(menu, footer));
            });
        }

        /// <summary>Idempotent: re-selecting the already-lit item leaves it
        /// lit and alone, so the rebuild can run on every topology change.</summary>
        [Fact]
        public void ReselectingTheSameItem_IsANoOp()
        {
            RunSta(() =>
            {
                var (menu, footer) = Rail(activeTag: "Devices");
                Assert.True(MainWindow.ApplyNavActive(menu, footer, "Devices"));
                Assert.Equal(new[] { "Devices" }, ActiveTags(menu, footer));
            });
        }

        /// <summary>Non-item entries (the footer separator) are skipped
        /// rather than throwing, and a null tag never matches.</summary>
        [Fact]
        public void SeparatorsAndUntaggedItems_AreSkipped()
        {
            RunSta(() =>
            {
                var (menu, footer) = Rail(activeTag: "About");
                menu.Add(new NavigationViewItem());  // no tag
                Assert.False(MainWindow.ApplyNavActive(menu, footer, null));
                Assert.Empty(ActiveTags(menu, footer));
            });
        }
    }
}
