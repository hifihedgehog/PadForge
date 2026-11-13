using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using PadForge.Views;

namespace PadForge.Search
{
    /// <summary>
    /// Search's answers (#574): the entries of the pages' markup that show
    /// now, the pages, controllers, devices and profiles of the moment, and
    /// the documentation. Which entries show is read once each time Search
    /// opens (<see cref="RefreshAvailability"/>), never on a keystroke. The
    /// index is built on a worker once the window has loaded. Until then, and
    /// if the build fails, the dynamic results answer alone.
    /// </summary>
    internal sealed class SearchService
    {
        private readonly MainViewModel _vm;
        private readonly Func<SearchPage, FrameworkElement> _pageOf;
        private readonly SearchLocator _locator;
        private IReadOnlyList<SearchEntry> _index = Array.Empty<SearchEntry>();
        private readonly List<(SearchEntry Entry, object Item, DependencyObject Label)> _available = new();

        /// <summary>The Settings group each available entry of that page
        /// sits in, by its name, for the entry's second line.</summary>
        private readonly Dictionary<SearchEntry, string> _settingsGroup = new();

        internal SearchService(MainViewModel vm, Func<SearchPage, FrameworkElement> pageOf)
        {
            _vm = vm;
            _pageOf = pageOf;
            _locator = new SearchLocator(pageOf);
        }

        internal SearchLocator Locator => _locator;

        /// <summary>Raised on the dispatcher's thread when the index has been
        /// built, so an open Search can take in its settings and
        /// actions.</summary>
        internal event EventHandler IndexBuilt;

        internal IReadOnlyList<SearchEntry> Index => _index;

        /// <summary>Builds the index on a worker and takes it on the
        /// dispatcher's thread. A failed build is logged, and Search keeps
        /// its dynamic results.</summary>
        internal void BuildIndex(Dispatcher dispatcher)
        {
            Task.Run(() => SearchIndexBuilder.Build(SearchSources.Load())).ContinueWith(done =>
            {
                if (done.IsFaulted)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine("Search: the index could not be built: " + done.Exception?.GetBaseException().Message);
                    return;
                }
                dispatcher.BeginInvoke(new Action(() =>
                {
                    _index = done.Result;
                    IndexBuilt?.Invoke(this, EventArgs.Empty);
                }));
            }, TaskScheduler.Default);
        }

        /// <summary>Takes an index made elsewhere (the tests).</summary>
        internal void SetIndex(IReadOnlyList<SearchEntry> index) => _index = index ?? Array.Empty<SearchEntry>();

        private PadPage Pad => _pageOf(SearchPage.Pad) as PadPage;

        private PadViewModel BoundPad => Pad?.DataContext as PadViewModel;

        private static bool IsMade(PadViewModel pad)
            => pad != null && pad.PadIndex >= 0 && pad.PadIndex < InputManager.MaxPads && SettingsManager.SlotCreated[pad.PadIndex];

        /// <summary>Where an entry's place ends: an extra's own page, or the
        /// page.</summary>
        private DependencyObject PlaceRoot(SearchEntry entry)
        {
            var page = _pageOf(entry.Page);
            if (entry.Page == SearchPage.Extras && entry.ExtraKey != null && page?.FindName(entry.ExtraKey + "Page") is DependencyObject extra)
                return extra;
            return page;
        }

        private object Resource(SearchPage page, string key) => _pageOf(page)?.TryFindResource(key);

        /// <summary>Whether an entry's element shows in its place. The
        /// Settings page shows one group at a time and has a search box of
        /// its own, and neither hides a setting from Search, so that page
        /// says what its own rules show (SettingsPage.ShowsByItsRules). The
        /// landing opens the group.</summary>
        private bool Shows(SearchEntry entry, DependencyObject element)
        {
            if (entry.Page == SearchPage.Settings && _pageOf(SearchPage.Settings) is SettingsPage settings)
            {
                if (!settings.ShowsByItsRules(element)) return false;
                if (settings.GroupOf(element) is { } group) _settingsGroup[entry] = settings.GroupName(group);
                return true;
            }
            return SearchLocator.Shows(element, PlaceRoot(entry));
        }

        /// <summary>Reads which entries show now: on the bound controller
        /// (its section and tab shown), on the selected device, and for each
        /// item of a card per item, by the item's own gates.</summary>
        internal void RefreshAvailability()
        {
            _available.Clear();
            _settingsGroup.Clear();
            _locator.Reset();
            var pad = Pad;
            bool padMade = IsMade(BoundPad);
            // How many entries before each one show the same words in the
            // same scope, counted over every entry, shown or not: the Nth
            // such entry is the Nth such element of the scope.
            var seen = new Dictionary<(SearchPage, string, SearchLabelSource, string), int>();
            foreach (var entry in _index)
            {
                try
                {
                    int n = 0;
                    string text = null;
                    bool located = entry.ItemsPath == null && !entry.RowOptions
                                   && entry.Kind is not (SearchKind.Section or SearchKind.Part or SearchKind.Extra);
                    if (located)
                    {
                        text = SearchLocator.LabelText(entry.Label, _pageOf(entry.Page)?.DataContext);
                        var key = (entry.Page, entry.ScopeName ?? string.Empty, entry.LabelSource, text ?? string.Empty);
                        seen.TryGetValue(key, out n);
                        seen[key] = n + 1;
                    }

                    if (entry.Page == SearchPage.Pad)
                    {
                        if (!padMade || pad == null) continue;
                        if (entry.Section >= 0 && !pad.IsSectionShown(entry.Section)) continue;
                        if (entry.Tab >= 0 && !pad.IsTabAvailable(entry.Tab)) continue;
                    }
                    else if (entry.Page == SearchPage.Devices && _vm.Devices?.SelectedDevice == null)
                        continue;

                    if (entry.Kind is SearchKind.Section or SearchKind.Part or SearchKind.Extra)
                    {
                        _available.Add((entry, null, null));
                        continue;
                    }
                    if (entry.RowOptions)
                    {
                        if (pad?.FindName("MappingDataGrid") is ItemsControl grid
                            && grid.Items.Cast<object>().Any(item => entry.ItemGates.All(g => BindingProbe.Passes(item, g, k => Resource(entry.Page, k)))))
                            _available.Add((entry, null, null));
                        continue;
                    }
                    if (entry.ItemsPath != null)
                    {
                        var list = _locator.ListOf(entry);
                        if (list == null || !Shows(entry, list)) continue;
                        foreach (object item in list.Items)
                            if (entry.ItemGates.All(g => BindingProbe.Passes(item, g, k => Resource(entry.Page, k))))
                                _available.Add((entry, item, null));
                        continue;
                    }
                    var label = _locator.Find(entry, n, text);
                    if (label != null && Shows(entry, label))
                        _available.Add((entry, null, label));
                }
                catch (Exception error)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine("Search: entry " + entry.Label?.Key + entry.Label?.Path + " could not be read: " + error.Message);
                }
            }
        }

        /// <summary>The results for a query, grouped and ranked: each group's
        /// best five, groups by their best score, Documentation last. Empty
        /// for an empty query.</summary>
        internal IReadOnlyList<SearchResult> Query(string text)
        {
            string q = text?.Trim() ?? string.Empty;
            if (q.Length == 0) return Array.Empty<SearchResult>();
            var scored = new List<SearchResult>();
            var pad = BoundPad;

            foreach (var (entry, item, label) in _available)
            {
                object data = item ?? _pageOf(entry.Page)?.DataContext;
                string title = SearchLocator.LabelText(entry.Label, data) ?? string.Empty;
                if (title.Length == 0) continue;
                string qualifier = SearchLocator.LabelText(entry.Qualifier, data) ?? string.Empty;
                string where = WhereOf(entry, pad, data);
                string english = SearchLocator.EnglishText(entry.Label);
                string englishQualifier = SearchLocator.EnglishText(entry.Qualifier);
                if (english != null && englishQualifier != null) english += " " + englishQualifier;
                int score = SearchMatcher.Score(q, qualifier.Length > 0 ? title + " " + qualifier : title, where, english);
                if (score == 0) continue;
                var group = entry.Kind switch
                {
                    SearchKind.Extra or SearchKind.Section or SearchKind.Part => SearchGroup.Pages,
                    SearchKind.Action => SearchGroup.Actions,
                    _ => SearchGroup.Settings,
                };
                scored.Add(new SearchResult
                {
                    Title = title,
                    Qualifier = qualifier,
                    Where = where,
                    Group = group,
                    GroupLabel = GroupLabel(group),
                    Score = score + Weight(entry),
                    Order = entry.Order,
                    Target = SearchTarget.Entry,
                    Entry = entry,
                    Payload = item ?? label,
                });
            }

            AddDynamic(scored, q);

            var groups = scored.GroupBy(r => r.Group)
                .Select(g => g.OrderByDescending(r => r.Score).ThenBy(r => r.Order).ThenBy(r => r.Title.Length).Take(5).ToList())
                .OrderBy(g => g[0].Group == SearchGroup.Docs ? 1 : 0)
                .ThenByDescending(g => g[0].Group == SearchGroup.Docs ? 0 : g[0].Score)
                .ThenBy(g => g[0].Group)
                .ToList();
            var results = groups.SelectMany(g => g).ToList();
            foreach (var r in results) r.Value = ValueOf(r);
            return results;
        }

        private static int Weight(SearchEntry entry)
        {
            int weight = entry.Kind switch
            {
                SearchKind.Extra or SearchKind.Section or SearchKind.Part => 8,
                SearchKind.Card => 4,
                SearchKind.Action => -2,
                _ => 0,
            };
            if (entry.UnderMore) weight -= 3;
            if (entry.RowOptions) weight -= 5;
            return weight;
        }

        internal static string GroupLabel(SearchGroup group) => group switch
        {
            SearchGroup.Pages => Strings.Instance.Search_Group_Pages,
            SearchGroup.Controllers => Strings.Instance.Home_Controllers,
            SearchGroup.Devices => Strings.Instance.Devices_Title,
            SearchGroup.Profiles => Strings.Instance.Profiles_Title,
            SearchGroup.Settings => Strings.Instance.Settings_Title,
            SearchGroup.Actions => Strings.Instance.Search_Group_Actions,
            _ => Strings.Instance.Search_Group_Docs,
        };

        internal static string SlotName(PadViewModel pad)
            => pad == null ? string.Empty : pad.OutputTypeDisplayName + " #" + pad.TypeInstanceLabel;

        /// <summary>The second line of a result: where it is, its parts
        /// joined by the language's list separator.</summary>
        private string WhereOf(SearchEntry entry, PadViewModel pad, object data)
        {
            var parts = new List<string>();
            switch (entry.Page)
            {
                case SearchPage.Pad:
                    parts.Add(SlotName(pad));
                    if (entry.Kind == SearchKind.Section) break;
                    if (entry.Section >= 0 && Pad?.SectionName(entry.Section) is { Length: > 0 } section) parts.Add(section);
                    break;
                case SearchPage.Settings:
                    parts.Add(Strings.Instance.Settings_Title);
                    if (_settingsGroup.TryGetValue(entry, out string group)) parts.Add(group);
                    break;
                case SearchPage.Extras:
                    parts.Add(Strings.Instance.Extras_Title);
                    if (entry.Kind != SearchKind.Extra && entry.ExtraKey != null)
                    {
                        var tile = _index.FirstOrDefault(e => e.Kind == SearchKind.Extra && e.ExtraKey == entry.ExtraKey);
                        if (tile != null) parts.Add(SearchLocator.LabelText(tile.Label, null));
                    }
                    break;
                case SearchPage.Devices:
                    parts.Add(Strings.Instance.Devices_Title);
                    if (_vm.Devices?.SelectedDevice?.DeviceName is { Length: > 0 } device) parts.Add(device);
                    break;
                case SearchPage.Profiles:
                    parts.Add(Strings.Instance.Profiles_Title);
                    break;
            }
            if (entry.Kind is not (SearchKind.Card or SearchKind.Section or SearchKind.Part or SearchKind.Extra)
                && entry.CardTitle != null && SearchLocator.LabelText(entry.CardTitle, data) is { Length: > 0 } card)
                parts.Add(card);
            if (entry.UnderMore) parts.Add(Strings.Instance.Search_Where_UnderMore);
            if (entry.RowOptions) parts.Add(Strings.Instance.Search_Where_RowOptions);
            return string.Join(Strings.Instance.Search_WhereSeparator, parts.Where(p => !string.IsNullOrEmpty(p)));
        }

        /// <summary>The pages, controllers, devices, profiles and Add
        /// Controller of the moment, and the documentation.</summary>
        private void AddDynamic(List<SearchResult> scored, string q)
        {
            void Add(string title, SearchGroup group, SearchTarget target, object payload, int weight, int order, string where = "")
            {
                if (string.IsNullOrEmpty(title)) return;
                int score = SearchMatcher.Score(q, title, where, null);
                if (score == 0) return;
                scored.Add(new SearchResult
                {
                    Title = title, Where = where, Group = group, GroupLabel = GroupLabel(group),
                    Score = score + weight, Order = order, Target = target, Payload = payload,
                });
            }

            var s = Strings.Instance;
            int order = -1000;
            foreach (var (title, tag) in new[]
                     {
                         (s.Home_Title, "Home"), (s.Devices_Title, "Devices"), (s.Profiles_Title, "Profiles"),
                         (s.Extras_Title, "Extras"), (s.Settings_Title, "Settings"), (s.About_Title, "About"),
                     })
                Add(title, SearchGroup.Pages, SearchTarget.Page, tag, 8, order++);

            int made = 0;
            for (int i = 0; i < InputManager.MaxPads && i < _vm.Pads.Count; i++)
            {
                if (!SettingsManager.SlotCreated[i]) continue;
                made++;
                Add(SlotName(_vm.Pads[i]), SearchGroup.Controllers, SearchTarget.Controller, i, 8, order++);
            }
            if (made < InputManager.MaxPads)
                Add(s.Main_AddController, SearchGroup.Actions, SearchTarget.AddController, null, -2, order++);

            if (_vm.Devices?.Devices != null)
                foreach (var row in _vm.Devices.Devices)
                    Add(row.DeviceName, SearchGroup.Devices, SearchTarget.Device, row, 0, order++);
            if (_vm.Settings?.ProfileItems != null)
                foreach (var profile in _vm.Settings.ProfileItems)
                    Add(profile.Name, SearchGroup.Profiles, SearchTarget.Profile, profile, 0, order++);

            scored.Add(new SearchResult
            {
                Title = string.Format(s.Search_Docs_Format, q),
                Where = "padforge.org/docs",
                Group = SearchGroup.Docs,
                GroupLabel = GroupLabel(SearchGroup.Docs),
                Target = SearchTarget.Docs,
                Payload = q,
                Order = int.MaxValue,
            });
        }

        /// <summary>The value a shown result reads now: its value box and
        /// unit, its switch's On or Off, its picker's text, or a fold card's
        /// summary.</summary>
        private string ValueOf(SearchResult result)
        {
            var entry = result.Entry;
            if (entry == null) return string.Empty;
            try
            {
                if (entry.Kind == SearchKind.Card && result.Payload is PadForge.Controls.FoldCard fold)
                    return fold.Summary ?? string.Empty;
                if (entry.Value == null || entry.RowOptions) return string.Empty;
                object data;
                FrameworkElement row = null;
                if (entry.ItemsPath != null) data = result.Payload;
                else
                {
                    row = SearchLocator.Up(result.Payload as DependencyObject) as FrameworkElement;
                    data = row?.DataContext;
                }
                switch (entry.Value.Kind)
                {
                    case SearchValueKind.Number:
                        var converter = entry.Value.ConverterKey == null ? null : Resource(entry.Page, entry.Value.ConverterKey) as IValueConverter;
                        string number = BindingProbe.Format(BindingProbe.Read(data, entry.Value.Path, converter), entry.Value.StringFormat);
                        if (string.IsNullOrEmpty(number)) return string.Empty;
                        string unit = entry.Value.UnitKey != null ? Strings.Get(entry.Value.UnitKey) : entry.Value.UnitText;
                        return string.IsNullOrEmpty(unit) ? number : number + " " + unit;
                    case SearchValueKind.Switch:
                        return BindingProbe.Read(data, entry.Value.Path) is bool on
                            ? (on ? Strings.Instance.Common_On : Strings.Instance.Common_Off) : string.Empty;
                    default:
                        var combo = row == null ? null : SearchLocator.LogicalDescendants(row).OfType<ComboBox>().FirstOrDefault();
                        return combo?.Text ?? string.Empty;
                }
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
