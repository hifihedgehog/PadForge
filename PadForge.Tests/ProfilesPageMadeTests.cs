using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PadForge.Controls;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Profiles page made under the app's own resources (#574, the
    /// world-class pass), where ProfilesPageTests reads its markup: the
    /// starter tiles add their profile, once for a double click, the active
    /// card alone is lit, and the cards, the drop tile and the starter tiles
    /// stand where the board and the narrower windows put them.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class ProfilesPageMadeTests
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
            page.Measure(new Size(width, 2400));
            page.Arrange(new Rect(0, 0, width, 2400));
            page.UpdateLayout();
        }

        /// <summary>An element's place on the page.</summary>
        private static Rect On(FrameworkElement page, FrameworkElement part)
            => part.TransformToAncestor(page).TransformBounds(new Rect(part.RenderSize));

        private static ProfileListItem Profile(string id, string name, bool active = false)
        {
            var item = new ProfileListItem { Id = id, Name = name, IsActive = active };
            item.SetTopology(new[] { true }, new[] { 0 });
            return item;
        }

        [Fact]
        public void TheStarterTiles_AddTheirProfile_OnceForADoubleClick()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var vm = new SettingsViewModel();
                vm.ProfileItems.Add(Profile(ProfileListItem.DefaultProfileId, "Default", active: true));
                var page = new ProfilesPage { DataContext = vm };
                var added = new List<string>();
                page.AddStarter = info => added.Add(info.Key);
                Lay(page, 1176);

                var list = (ItemsControl)page.FindName("StarterTiles");
                var first = StarterProfileCatalog.All.Take(ProfilesPage.StarterTileCount).ToList();
                Assert.Equal(first, list.Items.Cast<StarterProfileInfo>());
                var tiles = Descendants<Button>(list).Where(b => AutomationProperties.GetAutomationId(b) == "Profiles_Starter").ToList();
                Assert.Equal(4, tiles.Count);
                // A screen reader hears each starter's name, and its line as help.
                Assert.Equal(first.Select(s => s.Name), tiles.Select(AutomationProperties.GetName));
                Assert.Equal(first.Select(s => s.Description), tiles.Select(AutomationProperties.GetHelpText));

                static void Click(Button tile) => tile.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Click(tiles[0]);
                Assert.Equal(new[] { "desktop" }, added);
                // The second click of a double click, and a third on its heels.
                Click(tiles[0]);
                Click(tiles[0]);
                Assert.Equal(new[] { "desktop" }, added);
                // Another tile adds at once.
                Click(tiles[2]);
                Assert.Equal(new[] { "desktop", "hotbar" }, added);

                // With no one to hand the profile to, a click does nothing.
                page.AddStarter = null;
                Click(tiles[1]);
                Assert.Equal(2, added.Count);
            }, TimeSpan.FromMinutes(2), "the starter tiles test timed out");
        }

        [Fact]
        public void TheActiveCardAlone_IsLit_AndTheTilesStandWhereTheBoardPutsThem()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var vm = new SettingsViewModel();
                var standard = Profile(ProfileListItem.DefaultProfileId, "Default", active: true);
                var wheel = Profile("forza-wheel", "Forza Wheel");
                vm.ProfileItems.Add(standard);
                vm.ProfileItems.Add(wheel);
                var page = new ProfilesPage { DataContext = vm };

                // The board's page: 1176 wide with gutters of 36, so three
                // columns of the cards' grid and four starter tiles.
                Lay(page, 1176);
                double inner = 1176 - 72, column = (inner - 32) / 3;
                var cards = Descendants<Border>(page).Where(b => b.Name == "ProfileCard").ToList();
                Assert.Equal(2, cards.Count);
                var drop = (FrameworkElement)page.FindName("DropTile");
                Assert.Equal(36, On(page, cards[0]).X, 3);
                Assert.Equal(column, cards[0].ActualWidth, 3);
                Assert.Equal(36 + column + 16, On(page, cards[1]).X, 3);
                Assert.Equal(36 + 2 * (column + 16), On(page, drop).X, 3);
                Assert.Equal(column, drop.ActualWidth, 3);
                // The drop tile is as tall as the cards beside it.
                Assert.Equal(cards[0].ActualHeight, drop.ActualHeight, 3);
                Assert.Equal(On(page, cards[0]).Y, On(page, drop).Y, 3);

                // The active card's light is on and its wash shows. The other
                // card has neither.
                var lights = cards.Select(c => Descendants<Halo>(c).Single(h => h.Name == "ActiveLight")).ToList();
                var washes = cards.Select(c => Descendants<Border>(c).Single(b => b.Name == "ActiveWash")).ToList();
                Assert.True(lights[0].IsLit);
                Assert.False(lights[1].IsLit);
                Assert.Equal(Visibility.Visible, washes[0].Visibility);
                Assert.Equal(Visibility.Collapsed, washes[1].Visibility);
                wheel.IsActive = true;
                standard.IsActive = false;
                Assert.False(lights[0].IsLit);
                Assert.True(lights[1].IsLit);
                wheel.IsActive = false;
                standard.IsActive = true;

                var list = (ItemsControl)page.FindName("StarterTiles");
                List<Rect> Tiles() => Descendants<Button>(list).Where(b => AutomationProperties.GetAutomationId(b) == "Profiles_Starter")
                    .Select(b => On(page, b)).ToList();
                var four = Tiles();
                double tile = (inner - 36) / 4;
                Assert.All(four, r => Assert.Equal(tile, r.Width, 3));
                Assert.All(four, r => Assert.Equal(four[0].Y, r.Y, 3));
                Assert.Equal(new[] { 36.0, 36 + tile + 12, 36 + 2 * (tile + 12), 36 + 3 * (tile + 12) }, four.Select(r => Math.Round(r.X, 3)));

                // One profile: the drop tile runs on to the row's end.
                vm.ProfileItems.Remove(wheel);
                Lay(page, 1176);
                Assert.Equal(36 + column + 16, On(page, drop).X, 3);
                Assert.Equal(2 * column + 16, drop.ActualWidth, 3);
                vm.ProfileItems.Add(wheel);

                // The window's own 1100: the page is 836 wide, two cards fill
                // their line, the drop tile is a band under them as tall as
                // its words, and the starter tiles stand two and two.
                Lay(page, 836);
                inner = 836 - 72;
                cards = Descendants<Border>(page).Where(b => b.Name == "ProfileCard").ToList();
                Assert.Equal((inner - 16) / 2, cards[0].ActualWidth, 3);
                Assert.Equal(On(page, cards[0]).Y, On(page, cards[1]).Y, 3);
                Assert.Equal(36, On(page, drop).X, 3);
                Assert.Equal(inner, drop.ActualWidth, 3);
                Assert.Equal(On(page, cards[0]).Bottom + 16, On(page, drop).Y, 3);
                Assert.True(drop.ActualHeight < cards[0].ActualHeight - 30,
                    "the band is " + drop.ActualHeight + " tall beside cards of " + cards[0].ActualHeight);
                var two = Tiles();
                Assert.All(two, r => Assert.Equal((inner - 12) / 2, r.Width, 3));
                Assert.Equal(two[0].Y, two[1].Y, 3);
                Assert.Equal(two[2].Y, two[3].Y, 3);
                Assert.True(two[2].Y > two[0].Y);
                Assert.Equal(two[0].X, two[2].X, 3);
            }, TimeSpan.FromMinutes(2), "the Profiles layout test timed out");
        }
    }
}
