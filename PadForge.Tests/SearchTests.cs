using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Search;
using PadForge.ViewModels;
using PadForge.Views;
using Wpf.Ui.Appearance;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>Search's matching (#574): case, accents and width are
    /// ignored, a hyphen starts a word, every word must be found, the title
    /// outranks the where line, and the English title is a fallback under
    /// another language.</summary>
    public class SearchMatcherTests
    {
        [Fact]
        public void CaseAccentsAndWidthAreIgnored()
        {
            Assert.True(SearchMatcher.Find("Deadzone", "déadzone", out _, out bool start));
            Assert.True(start);
            Assert.True(SearchMatcher.Find("Deadzone", "DEADZONE", out _, out _));
            Assert.True(SearchMatcher.Find("Deadzone", "ｄｅａｄ", out _, out _));
            Assert.False(SearchMatcher.Find("Deadzone", "lean", out _, out _));
        }

        [Fact]
        public void AHyphenStartsAWord()
        {
            Assert.True(SearchMatcher.Find("Anti-Deadzone", "dead", out bool word, out bool start));
            Assert.True(word);
            Assert.False(start);
            Assert.True(SearchMatcher.Find("Overdeadzone", "dead", out word, out _));
            Assert.False(word);
        }

        [Fact]
        public void EveryWordMustBeFound()
        {
            Assert.Equal(0, SearchMatcher.Score("deadzone gyro", "Deadzone", "Xbox #1, Sticks & Triggers", null));
            Assert.True(SearchMatcher.Score("deadzone gyro", "Lean Inner Deadzone", "Xbox #1, Motion & Touch, Gyro", null) > 0);
            Assert.Equal(0, SearchMatcher.Score("   ", "Deadzone", "", null));
        }

        [Fact]
        public void TheTitleOutranksTheWhereLine_AndAWholeTitleRanksFirst()
        {
            int inTitle = SearchMatcher.Score("left", "Left Stick", "Xbox #1", null);
            int inWhere = SearchMatcher.Score("left", "Deadzone", "Xbox #1, Left Stick", null);
            Assert.True(inTitle > inWhere);
            Assert.True(SearchMatcher.Score("deadzone", "Deadzone", "", null) > SearchMatcher.Score("deadzone", "Deadzone Shape", "", null));
        }

        [Fact]
        public void TheEnglishTitle_IsAFallbackUnderAnotherLanguage()
        {
            int english = SearchMatcher.Score("deadzone", "Zona muerta", "", "Deadzone");
            Assert.True(english > 0);
            Assert.True(english < SearchMatcher.Score("zona", "Zona muerta", "", "Deadzone"));
            Assert.Equal(0, SearchMatcher.Score("deadzone", "Zona muerta", "", null));
        }
    }

    /// <summary>Search's results as the panel shows them (#574): groups by
    /// their best score with Documentation last, five to a group, the first
    /// picked, the pick stopping at the ends, Enter and Escape, and the line
    /// that says nothing matches.</summary>
    [Collection("SettingsManagerStatics")]
    public class SearchViewModelTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        private static (MainViewModel Vm, SearchViewModel Search) Arrange()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var vm = new MainViewModel();
            var service = new SearchService(vm, _ => null);
            service.SetIndex(Array.Empty<SearchEntry>());
            return (vm, new SearchViewModel(service.Query));
        }

        [Fact]
        public void TheGroupsRunByTheirBestScore_DocumentationLast_FiveToAGroup()
        {
            var (vm, search) = Arrange();
            vm.Devices.Devices.Add(new DeviceRowViewModel { DeviceName = "Racing Wheel" });
            for (int i = 1; i <= 7; i++)
                vm.Settings.ProfileItems.Add(new ProfileListItem { Id = "p" + i, Name = "Racing " + i });

            search.Query = "racing wheel";
            var groups = search.Results.Select(r => r.Group).Distinct().ToList();
            Assert.Equal(new[] { SearchGroup.Devices, SearchGroup.Docs }, groups);

            search.Query = "racing";
            groups = search.Results.Select(r => r.Group).Distinct().ToList();
            // Devices and Profiles tie at their best, and fall in group order.
            Assert.Equal(new[] { SearchGroup.Devices, SearchGroup.Profiles, SearchGroup.Docs }, groups);
            Assert.Equal(5, search.Results.Count(r => r.Group == SearchGroup.Profiles));
            // A tie inside a group goes by order.
            Assert.Equal(new[] { "Racing 1", "Racing 2", "Racing 3", "Racing 4", "Racing 5" },
                search.Results.Where(r => r.Group == SearchGroup.Profiles).Select(r => r.Title).ToArray());
            Assert.Equal(SearchGroup.Docs, search.Results.Last().Group);
            Assert.Equal(string.Format(Strings.Instance.Search_Docs_Format, "racing"), search.Results.Last().Title);
            Assert.Same(search.Results[0], search.SelectedResult);
            Assert.Equal(string.Empty, search.NoResultsText);
            Assert.True(search.HasResults);
        }

        [Fact]
        public void ThePickMoves_AndStopsAtTheEnds_AndEnterOpensIt_AndEscapeCloses()
        {
            var (vm, search) = Arrange();
            for (int i = 1; i <= 3; i++)
                vm.Settings.ProfileItems.Add(new ProfileListItem { Id = "p" + i, Name = "Lane " + i });
            search.Query = "lane";
            Assert.Equal(4, search.Results.Count);
            search.MoveSelection(-1);
            Assert.Same(search.Results[0], search.SelectedResult);
            search.MoveSelection(2);
            Assert.Same(search.Results[2], search.SelectedResult);
            search.MoveSelection(5);
            Assert.Same(search.Results[3], search.SelectedResult);
            Assert.Equal(search.SelectedResult.AutomationName, search.SelectedAnnouncement);

            SearchResult opened = null;
            int closed = 0;
            search.OpenRequested += (s, r) => opened = r;
            search.CloseRequested += (s, e) => closed++;
            search.Open();
            Assert.Same(search.SelectedResult, opened);
            search.Close();
            Assert.Equal(1, closed);
        }

        [Fact]
        public void NoMatch_SaysSo_AndPicksTheDocumentation()
        {
            var (_, search) = Arrange();
            search.Query = "zzqx";
            var only = Assert.Single(search.Results);
            Assert.Equal(SearchGroup.Docs, only.Group);
            Assert.Same(only, search.SelectedResult);
            Assert.Equal(string.Format(Strings.Instance.Search_NoResults_Format, "zzqx"), search.NoResultsText);
            Assert.True(search.HasNoResultsText);

            search.Query = "";
            Assert.Empty(search.Results);
            Assert.False(search.HasResults);
            Assert.False(search.HasNoResultsText);
            Assert.Null(search.SelectedResult);
        }

        [Fact]
        public void TheDocumentationAddress_EscapesTheQuery()
        {
            Assert.Equal("https://padforge.org/docs/?q=dead%20zone%20%26%20co", SearchNavigator.DocsAddress("  dead zone & co "));
            Assert.Equal(Strings.Instance.Key_Control + " K", Arrange().Search.ShortcutText);
        }
    }

    /// <summary>Search in the shell (#574), read from the markup and the
    /// window's code: the box in the title bar's middle, the dialog in the
    /// page area, Ctrl+K, and the words it uses.</summary>
    public class SearchShellTests
    {
        private static int At(string text, string needle)
        {
            int at = text.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(at >= 0, needle + " is not there");
            Assert.True(at == text.LastIndexOf(needle, StringComparison.Ordinal), needle + " is there twice");
            return at;
        }

        [Fact]
        public void TheBoxSitsInTheTitleBarsMiddle_AndOnlyItTakesThePointer()
        {
            string xaml = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml");
            At(xaml, "<ui:TitleBar x:Name=\"AppTitleBar\" Height=\"52\" HorizontalContentAlignment=\"Stretch\"");
            int center = At(xaml, "<ui:TitleBar.CenterContent>");
            int centerEnd = At(xaml, "</ui:TitleBar.CenterContent>");
            string box = xaml.Substring(center, centerEnd - center);
            At(box, "<controls:ClickBorder x:Name=\"TitleSearchBox\" Style=\"{StaticResource TitleSearchBox}\"");
            At(box, "Height=\"34\" MaxWidth=\"560\"");
            At(box, "WindowChrome.IsHitTestVisibleInChrome=\"True\"");
            At(box, "AutomationProperties.AutomationId=\"TitleSearchBox\"");
            At(box, "Data=\"{StaticResource SearchLineGeometry}\" Size=\"16\" StrokeWidth=\"1.8\"");
            At(box, "<TextBlock x:Name=\"TitleSearchShortcut\" Style=\"{StaticResource KeyChipText}\"/>");
        }

        [Fact]
        public void TheDialogStandsInThePageArea_AfterThePages()
        {
            string xaml = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml");
            int about = At(xaml, "<views:AboutPage x:Name=\"AboutPageView\"");
            int add = At(xaml, "<controls:ShellDialog x:Name=\"AddControllerHost\"");
            int host = At(xaml, "<controls:ShellDialog x:Name=\"SearchHost\" Panel.ZIndex=\"5\" FrameMaxWidth=\"660\" ClosesOnGroundPress=\"True\"");
            Assert.True(about < add && add < host);
            At(xaml, "<views:SearchPanel x:Name=\"SearchPanelView\"/>");
            At(xaml, "AutomationProperties.AutomationId=\"SearchPanel\"");
        }

        [Fact]
        public void CtrlK_OpensSearch_ExceptWhileRecording()
        {
            string code = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            At(code, "PreviewKeyDown += Window_SearchKey;");
            int handler = At(code, "private void Window_SearchKey(object sender, System.Windows.Input.KeyEventArgs e)");
            int k = code.IndexOf("System.Windows.Input.Key.K", handler, StringComparison.Ordinal);
            Assert.True(k > handler && k - handler < 300);
            int open = At(code, "internal void OpenSearch()");
            int refuse = code.IndexOf("if (_recorderService?.IsRecording == true) return;", open, StringComparison.Ordinal);
            Assert.True(refuse > open && refuse - open < 200, "Search opens while an input is recorded");
            // What shows is read as Search opens, and again when the index
            // arrives while it is open.
            int refresh = code.IndexOf("_searchService.RefreshAvailability();", open, StringComparison.Ordinal);
            Assert.True(refresh > open && refresh - open < 2500, "Search does not read what shows as it opens");
            At(code, "_searchService.IndexBuilt += (s, e) =>");
            At(code, "TitleSearchBox.Click += (s, e) => OpenSearch();");
        }

        [Fact]
        public void ThePanel_IsDrawnAsItsBoard()
        {
            string panel = ExtrasPageTests.RepoText("PadForge.App", "Views", "SearchPanel.xaml");
            int query = At(panel, "<Border x:Name=\"QueryRow\" DockPanel.Dock=\"Top\" Padding=\"16,14\" BorderThickness=\"0,0,0,1\"");
            int box = At(panel, "<TextBox x:Name=\"SearchQueryBox\" Height=\"32\" FontSize=\"17\"");
            int foot = At(panel, "<Border x:Name=\"SearchFoot\" DockPanel.Dock=\"Bottom\" Padding=\"16,12\"");
            int list = At(panel, "<ListBox x:Name=\"SearchResultsList\" Padding=\"8,4,8,10\"");
            Assert.True(query < box && box < foot && foot < list);
            At(panel, "<Border x:Name=\"Bd\" MinHeight=\"48\" Padding=\"12,6\" CornerRadius=\"8\" BorderThickness=\"1\"");
            At(panel, "<Setter TargetName=\"Bd\" Property=\"Background\" Value=\"{DynamicResource EmberCardPickBrush}\"/>");
            At(panel, "<TextBlock Text=\"{Binding Name}\" Margin=\"12,14,12,6\" FontSize=\"12\" FontWeight=\"SemiBold\"");
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(panel, "<controls:KeyHint ").Count);
            At(panel, "AutomationProperties.AutomationId=\"SearchCloseChip\"");
        }

        [Fact]
        public void TheWordsSearchUses_AreStrings()
        {
            var s = Strings.Instance;
            foreach (string text in new[]
            {
                s.Search_Placeholder, s.Search_Title, s.Search_Results, s.Search_Key_Up, s.Search_Key_Down,
                s.Search_Hint_Move_Format, s.Search_Hint_Open_Format, s.Search_Hint_Anywhere_Format, s.Search_Group_Pages,
                s.Search_Group_Actions, s.Search_Group_Docs, s.Search_Where_UnderMore, s.Search_Where_RowOptions,
                s.Search_WhereSeparator, s.Search_NoResults_Format, s.Search_Docs_Format,
            })
                Assert.False(string.IsNullOrEmpty(text));
            Assert.Equal(", ", s.Search_WhereSeparator);
        }
    }

    /// <summary>Search finds a setting on a live page by its words (#574):
    /// on the Web Controller's page a switch under More is found, and the More
    /// row that holds it opens. A label that a scope shows twice resolves to
    /// the right one.</summary>
    [Collection("WpfApplicationSingleton")]
    public class SearchLocatorTests
    {
        /// <summary>The Settings page shows one of its five groups at a time
        /// and has a search box of its own (#574). Neither hides a setting
        /// from Search: a setting of a group that is not open is found, its
        /// second line names the group, and opening the result opens the
        /// group and empties the page's box. A row the page's own rule hides
        /// stays out.</summary>
        [Fact]
        public void ASettingOfAnotherSettingsGroup_IsFound_AndItsGroupOpens()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var vm = new SettingsViewModel();
                var page = new SettingsPage { DataContext = vm };
                page.Measure(new Size(1000, 800));
                page.Arrange(new Rect(0, 0, 1000, 800));
                page.UpdateLayout();
                Assert.Equal(SettingsGroup.General, vm.SelectedSettingsGroup);

                var markup = SearchSources.FileNames.ToDictionary(p => p.Key, p => ExtrasPageTests.RepoText("PadForge.App", "Views", p.Value));
                var index = SearchIndexBuilder.Read(markup, PadPage.TabOfGroupName).Entries;
                FrameworkElement PageOf(SearchPage p) => p == SearchPage.Settings ? page : null;
                var service = new SearchService(new MainViewModel(), PageOf);
                service.SetIndex(index);
                service.RefreshAvailability();

                // The page's settings, each with the element that shows its
                // label. Every group has some.
                var located = index.Where(e => e.Page == SearchPage.Settings && e.Kind == SearchKind.Setting && e.ItemsPath == null)
                    .Select(e => (Entry: e, Label: service.Locator.Find(e, index)))
                    .Where(x => x.Label != null && page.GroupOf(x.Label) != null).ToList();
                foreach (SettingsGroup group in Enum.GetValues(typeof(SettingsGroup)))
                    Assert.Contains(located, x => page.GroupOf(x.Label) == group);

                SearchResult Found(SearchEntry entry)
                    => service.Query(SearchLocator.LabelText(entry.Label, vm)).FirstOrDefault(r => ReferenceEquals(r.Entry, entry));

                // A setting of the last group, which is not the open one.
                var inData = located.Where(x => page.GroupOf(x.Label) == SettingsGroup.Data && page.ShowsByItsRules(x.Label)).ToList();
                Assert.NotEmpty(inData);
                Assert.All(inData, x => Assert.False(SearchLocator.Shows(x.Label, page), "the Data group is open, so this proves nothing"));
                var (entry, label) = inData.FirstOrDefault(x => Found(x.Entry) != null);
                Assert.True(entry != null, "Search found no setting of the Data group while General is open");
                // Its second line names the page and the group.
                Assert.StartsWith(Strings.Instance.Settings_Title + Strings.Instance.Search_WhereSeparator + Strings.Instance.Settings_Group_Data,
                    Found(entry).Where);

                // The page's own search box does not hide it from Search.
                vm.SettingsSearchText = "zzzzzz";
                page.UpdateLayout();
                service.RefreshAvailability();
                var result = Found(entry);
                Assert.NotNull(result);

                // Opening it opens its group and empties the page's box.
                string selected = null;
                var navigator = new SearchNavigator(service, PageOf, new MainViewModel(), tag => selected = tag, _ => { }, () => { });
                navigator.Open(result);
                Assert.Equal("Settings", selected);
                Assert.Equal(SettingsGroup.Data, vm.SelectedSettingsGroup);
                Assert.Equal(string.Empty, vm.SettingsSearchText);
                page.UpdateLayout();
                Assert.True(SearchLocator.Shows(label, page));

                // A row its own rule hides (HidHide's options with no HidHide)
                // stays out of Search, whichever group is open.
                bool HiddenByItsRule(DependencyObject part)
                {
                    for (var at = part; at != null && page.GroupOf(at) != null; at = SearchLocator.Up(at))
                        if (at is PadForge.Controls.SettingItem { OwnVisibility: not Visibility.Visible }) return true;
                    return false;
                }
                service.RefreshAvailability();
                var hidden = located.Where(x => HiddenByItsRule(x.Label)).ToList();
                Assert.NotEmpty(hidden);
                Assert.All(hidden, x => Assert.Null(Found(x.Entry)));
            }, TimeSpan.FromMinutes(3), "the Settings search run timed out");
        }

        [Fact]
        public void ASettingUnderMore_IsFound_AndItsMoreRowOpens()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var folds = PadForge.Common.CardFoldState.Snapshot();
                try
                {
                    PageRenderDump.SetResourceAssembly(typeof(App).Assembly);
                    var app = PageRenderDump.MakeAppThatNeverStarts(dispatcher);
                    app.InitializeComponent();
                    PageRenderDump.ApplyTheme(ApplicationTheme.Dark);

                    var page = new ExtrasPage { DataContext = new DashboardViewModel() };
                    page.Measure(new Size(1000, 800));
                    page.Arrange(new Rect(0, 0, 1000, 800));
                    page.UpdateLayout();
                    var markup = SearchSources.FileNames.ToDictionary(p => p.Key, p => ExtrasPageTests.RepoText("PadForge.App", "Views", p.Value));
                    var index = SearchIndexBuilder.Read(markup, PadPage.TabOfGroupName).Entries;
                    var locator = new SearchLocator(p => p == SearchPage.Extras ? page : null);

                    var entry = index.Single(e => e.Page == SearchPage.Extras && e.Kind == SearchKind.Setting && e.Label.Key == "Dashboard_WebPlainEnable");
                    Assert.Equal("WebController", entry.ExtraKey);
                    Assert.True(entry.UnderMore);
                    // The row's label is its own text, with the switch beside it,
                    // and a landing rings the row that holds both.
                    var label = locator.Find(entry, index);
                    var text = Assert.IsAssignableFrom<TextBlock>(label);
                    Assert.Equal(Strings.Instance.Dashboard_WebPlainEnable, text.Text);
                    var row = Assert.IsType<PadForge.Controls.SettingRow>(SearchLocator.TargetOf(label, entry));
                    Assert.Equal("EnableWebControllerPlainHttpCheckBox", entry.TargetAutomationId);
                    Assert.Contains(SearchLocator.LogicalDescendants(row).OfType<CheckBox>(),
                        box => System.Windows.Automation.AutomationProperties.GetAutomationId(box) == entry.TargetAutomationId);

                    var more = (PadForge.Controls.MoreRow)page.FindName("WebControllerMore");
                    more.IsExpanded = false;
                    var opened = SearchNavigator.OpenFolds(row, page);
                    Assert.Contains(more, opened);
                    Assert.True(more.IsExpanded);

                    // Two entries with the same words in one scope: the second
                    // resolves to the second element.
                    var twins = index.Where(e => e.Page == SearchPage.Extras && e.ItemsPath == null && !e.RowOptions && e.Kind == SearchKind.Setting)
                        .GroupBy(e => (e.ScopeName, e.LabelSource, e.Label.Key)).FirstOrDefault(g => g.Count() > 1 && g.Key.Item3 != null)?.ToList();
                    if (twins != null)
                    {
                        var first = locator.Find(twins[0], index);
                        var second = locator.Find(twins[1], index);
                        Assert.NotNull(first);
                        Assert.NotNull(second);
                        Assert.NotSame(first, second);
                    }
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    try
                    {
                        PadForge.Common.CardFoldState.Load(folds);
                        PairDeviceDialogSerialTests.ReleaseApplication();
                        PageRenderDump.SetResourceAssembly(null);
                    }
                    catch (Exception error) { failure ??= error; }
                    dispatcher.InvokeShutdown();
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromMinutes(3)), "the locator run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
