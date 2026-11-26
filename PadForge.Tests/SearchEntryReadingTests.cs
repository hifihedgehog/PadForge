using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PadForge.Search;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// What Search's index carries for an entry and how a result is ranked
    /// (#600): a menu item weighs as an action, a dialog whose title is set
    /// in code is named by its class, the Macros and Menus parts carry their
    /// own buttons' words, and a failed catalog leaves the pages' index.
    /// </summary>
    public class SearchEntryReadingTests
    {
        private static SearchIndex Index() => SearchIndexBuilder.Read(
            SearchSources.FileNames.ToDictionary(p => p.Key, p => ExtrasPageTests.RepoText("PadForge.App", "Views", p.Value)),
            PadPage.TabOfGroupName);

        /// <summary>A menu item sits in the Actions group with the page
        /// buttons and weighs as they do, so a menu item never outranks a
        /// button its words match as well.</summary>
        [Fact]
        public void AMenuItem_WeighsAsAnAction()
        {
            var action = new SearchEntry(SearchKind.Action, SearchPage.Settings, new SearchLabel("Common_Delete", null), null, null,
                -1, -1, null, false, false, null, null, Array.Empty<SearchGate>(), null, null, SearchLabelSource.Button, 0);
            var item = action with { Kind = SearchKind.MenuItem };
            Assert.Equal(SearchService.Weight(action), SearchService.Weight(item));
            Assert.True(SearchService.Weight(item) < SearchService.Weight(action with { Kind = SearchKind.Setting }));
        }

        /// <summary>A dialog whose title is set in code is named by its
        /// class, the words its place in the catalog reads.</summary>
        [Fact]
        public void ADialogWhoseTitleIsSetInCode_IsNamedByItsClass()
        {
            var catalog = SearchIndexBuilder.BuildCatalog(SearchSources.LoadCatalog());
            foreach (string dialog in new[] { "ConfirmDialog", "KeepDriverDialog", "KeyboardScreenDialog", "NetworkAssistantDialog", "RemoteAssignmentsDialog" })
            {
                var entry = Assert.Single(catalog, e => e.Kind == SearchKind.Dialog && e.Dialog == dialog);
                Assert.Null(entry.Label.Key);
                Assert.Equal(dialog, SearchLocator.LabelText(entry.Label, null));
                Assert.Equal(dialog, SearchLocator.LabelText(entry.Label, new object()));
            }
        }

        /// <summary>The Macros and Menus part buttons are entries of their
        /// own buttons: their AutomationIds and their click handler.</summary>
        [Fact]
        public void TheMacrosAndMenusParts_CarryTheirOwnButtons()
        {
            var entries = Index().Entries;
            var macros = Assert.Single(entries, e => e.Page == SearchPage.Pad && e.Kind == SearchKind.Part && e.Tab == 1);
            Assert.Equal("MacrosTab", macros.AutomationId);
            Assert.Equal("TabBtn_Click", macros.Handler);
            var menus = Assert.Single(entries, e => e.Page == SearchPage.Pad && e.Kind == SearchKind.Part && e.Tab == 15);
            Assert.Equal("MenusTab", menus.AutomationId);
            Assert.Equal("TabBtn_Click", menus.Handler);
        }

        /// <summary>The pages' index and the dialogs' are built apart: a
        /// catalog that fails leaves the pages' index, and pages that fail
        /// leave the catalog.</summary>
        [Fact]
        public void ACatalogThatFails_LeavesThePagesIndex()
        {
            IReadOnlyList<SearchEntry> pages = Index().Entries;
            var (built, dialogs) = SearchService.BuildApart(() => pages, () => throw new InvalidOperationException("a dialog's markup"));
            Assert.Same(pages, built);
            Assert.Null(dialogs);

            IReadOnlyList<SearchEntry> catalog = Array.Empty<SearchEntry>();
            var (none, kept) = SearchService.BuildApart(() => throw new InvalidOperationException("a page's markup"), () => catalog);
            Assert.Null(none);
            Assert.Same(catalog, kept);
        }
    }

    /// <summary>
    /// A menu item's result (#600) is offered only where its landing finds
    /// the shown button whose menu holds it: a profile card's own menu needs
    /// a card, and every menu item that shows has its button.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class SearchMenuItemTests
    {
        [Fact]
        public void AMenuItem_IsOfferedOnlyWhereItsLandingFindsItsButton()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                string export = Strings.Instance.Profiles_Export;
                bool CardMenu(SearchResult r) => r.Entry is { Kind: SearchKind.MenuItem, Page: SearchPage.Profiles, ItemsList: not null };

                // No profile card: no card's menu.
                p.Service.RefreshAvailability();
                Assert.DoesNotContain(p.Service.Query(export), CardMenu);

                // One card, drawn: its menu is offered, and the landing finds
                // the card's own More.
                p.Vm.Settings.ProfileItems.Add(new ProfileListItem { Id = "p1", Name = "Racing" });
                p.Profiles.UpdateLayout();
                PageRenderDump.Settle(p.Profiles.Dispatcher);
                p.Service.RefreshAvailability();
                var result = Assert.Single(p.Service.Query(export), CardMenu);
                var owner = SearchNavigator.MenuOwnerOf(p.Profiles, result.Entry, e => SearchLocator.Shows(e, p.Profiles));
                Assert.NotNull(owner);
                Assert.Equal("Profiles_More", System.Windows.Automation.AutomationProperties.GetAutomationId(owner));

                // Every menu of a list's items that shows has a button its
                // landing finds in an item's container.
                var offered = p.Service.AvailableNow().Where(s => s.Entry.Kind == SearchKind.MenuItem && s.Entry.ItemsList != null).ToList();
                Assert.NotEmpty(offered);
                foreach (var s in offered)
                {
                    var page = p.PageOf(s.Entry.Page);
                    Assert.True(SearchNavigator.MenuOwnerOf(page, s.Entry, e => SearchLocator.Shows(e, page)) != null,
                        p.Service.IdOf(s.Entry) + " is offered and its landing finds no button");
                }
            });
        }
    }
}
