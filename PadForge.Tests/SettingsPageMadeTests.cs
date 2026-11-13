using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PadForge.Controls;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Settings page made under the app's own resources (#574, the
    /// world-class pass), where SettingsPageTests reads its markup: the page
    /// reads in its board's column, a row's label stands at the card's
    /// start and its control and reset at its end, the controls of a card
    /// keep one column, and every row has its hairline, once.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class SettingsPageMadeTests
    {
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var below in Descendants<T>(child)) yield return below;
            }
        }

        private static void Lay(FrameworkElement page, double width)
        {
            page.Measure(new Size(width, 3000));
            page.Arrange(new Rect(0, 0, width, 3000));
            page.UpdateLayout();
        }

        private static Rect On(FrameworkElement page, FrameworkElement part)
            => part.TransformToAncestor(page).TransformBounds(new Rect(part.RenderSize));

        /// <summary>Whether a part shows: neither it nor anything that
        /// holds it is collapsed. A page in no window has no IsVisible.</summary>
        private static bool Shows(DependencyObject part)
        {
            for (var at = part; at != null; at = VisualTreeHelper.GetParent(at))
                if (at is UIElement { Visibility: not Visibility.Visible }) return false;
            return true;
        }

        [Fact]
        public void ThePageReadsInItsColumn_AndARowsControlsStandAtItsEnd()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var vm = new SettingsViewModel();
                var page = new SettingsPage { DataContext = vm };

                // The board's page: 1176 wide. Inside gutters of 36 the
                // column is 1040, the group list 190 of it and the cards
                // the 822 that a gap of 28 leaves, so the cards end where
                // the search box over them does.
                Lay(page, 1176);
                var list = (FrameworkElement)page.FindName("GroupList");
                var host = (FrameworkElement)page.FindName("GroupsHost");
                Assert.Equal(36, On(page, list).X, 3);
                Assert.Equal(190, list.ActualWidth, 3);
                Assert.Equal(36 + 190 + 28, On(page, host).X, 3);
                Assert.Equal(822, host.ActualWidth, 3);
                Assert.Equal(36 + 1040, On(page, host).Right, 3);
                Assert.Equal(36 + 1040, On(page, (FrameworkElement)page.FindName("SettingsSearchBox")).Right, 3);
                Assert.True(SettingRow.GetEnds(host));

                var general = (FrameworkElement)page.FindName("GeneralGroup");
                var rows = Descendants<SettingRow>(general).Where(Shows).ToList();
                Assert.True(rows.Count >= 9, rows.Count + " rows show in General");
                double left = On(page, host).X, right = On(page, host).Right;
                foreach (var row in rows)
                {
                    Assert.True(SettingRow.GetEnds(row));
                    var parts = row.Children.Cast<FrameworkElement>().Where(Shows).ToList();
                    // The label starts 21 in from the card's outer edge: its
                    // hairline of 1 and the row's 20.
                    Assert.Equal(left + 21, On(page, parts[0]).X, 3);
                    if (parts[^1] is SettingResetButton reset)
                    {
                        // The reset ends 11 from the card's outer edge, and
                        // the control before it ends 12 before the reset.
                        Assert.Equal(right - 11, On(page, reset).Right, 3);
                        Assert.Equal(On(page, reset).X - SettingRow.EndGap, On(page, parts[^2]).Right, 3);
                    }
                }

                // The switches of a card stand in one column, and so do its
                // resets: the five rows of the Window card.
                var switches = Descendants<CheckBox>(general).Where(Shows).Select(c => Math.Round(On(page, c).Right, 3)).Distinct().ToList();
                Assert.Single(switches);

                // Every row's item draws its hairline, the card's whole
                // width, and each row is the board's 48 tall.
                var items = Descendants<SettingItem>(general).Where(Shows).ToList();
                Assert.All(items, i => Assert.NotNull(i.Rule));
                Assert.All(items, i => Assert.Equal(820, i.ActualWidth, 3));
                var window = rows.Where(r => r.Children.OfType<CheckBox>().Any()).Take(5).ToList();
                Assert.All(window, r => Assert.Equal(48, ((FrameworkElement)r.Parent).ActualHeight, 3));

                // The drivers' list draws its own hairlines between its
                // parts, so a part of its own draws none, and the rows in
                // HidHide's fold draw theirs again.
                vm.IsHidHideInstalled = true;
                vm.SelectedSettingsGroup = SettingsGroup.Drivers;
                var fold = (Expander)page.FindName("HidHideOptions");
                fold.IsExpanded = true;
                try
                {
                    Lay(page, 1176);
                    var drivers = (FrameworkElement)page.FindName("DriversGroup");
                    var folded = Descendants<SettingItem>(fold).ToList();
                    Assert.True(folded.Count >= 3, folded.Count + " items in the fold");
                    Assert.All(folded, i => Assert.NotNull(i.Rule));
                    var own = Descendants<SettingItem>(drivers).Where(i => !folded.Contains(i)).ToList();
                    Assert.True(own.Count >= 5, own.Count + " parts of the drivers' list");
                    Assert.All(own, i => Assert.Null(i.Rule));
                    // A row in the fold is laid out as a card's row is.
                    var inFold = Descendants<SettingRow>(fold).First(Shows);
                    Assert.Equal(left + 21, On(page, (FrameworkElement)inFold.Children[0]).X, 3);
                    Assert.Equal(right - 11, On(page, inFold.Children.OfType<SettingResetButton>().Single()).Right, 3);
                }
                finally
                {
                    // The fold's state is the app's and outlives the page.
                    fold.IsExpanded = false;
                }

                // The window's least width: the group list stands over the
                // cards, which take the page's width inside its gutters.
                vm.SelectedSettingsGroup = SettingsGroup.General;
                Lay(page, 636);
                Assert.Equal(36, On(page, list).X, 3);
                Assert.Equal(36, On(page, host).X, 3);
                Assert.Equal(636 - 72, host.ActualWidth, 3);
                Assert.True(On(page, host).Y > On(page, list).Bottom);

                // The window rounds its layout to device pixels, and a page
                // in no window does not unless it is told to. Rounded, the
                // cards still end where the search box does and start where
                // the title does, whatever the display's scale: the column
                // caps itself, where a grid's star column that stops at a
                // MaxWidth takes one device pixel more than it.
                page.UseLayoutRounding = true;
                Lay(page, 1176);
                var search = (FrameworkElement)page.FindName("SettingsSearchBox");
                Assert.Equal(On(page, search).Right, On(page, host).Right, 3);
                Assert.Equal(On(page, (FrameworkElement)page.FindName("SettingsTitle")).X, On(page, list).X, 3);
                Assert.True(host.ActualWidth > 821 && host.ActualWidth < 823, "the cards are " + host.ActualWidth + " wide");
            }, TimeSpan.FromMinutes(2), "the Settings layout test timed out");
        }
    }
}
