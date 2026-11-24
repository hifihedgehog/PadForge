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
        private readonly Action<PadViewModel> _bindPad;
        private readonly SearchLocator _locator;
        private IReadOnlyList<SearchEntry> _index = Array.Empty<SearchEntry>();
        private readonly List<Available> _available = new();

        /// <summary>An entry that shows now. A controller page's entry names
        /// the controller it shows on, the name of its section there, and,
        /// for a controller other than the one the page is bound to, the
        /// value it read while the page showed that controller (the page's
        /// elements show the bound one's again by the time a query runs).
        /// <see cref="Value"/> is null when the value is read at the
        /// query.</summary>
        private sealed record Available(SearchEntry Entry, object Item, DependencyObject Label,
            PadViewModel Pad = null, string Section = null, string Value = null);

        /// <summary>The Settings group each available entry of that page
        /// sits in, by its name, for the entry's second line.</summary>
        private readonly Dictionary<SearchEntry, string> _settingsGroup = new();

        /// <param name="bindPad">Binds the controller page to a controller as
        /// a visit to its page does (MainWindow.BindPadPage: the stored
        /// mapping rows mirrored into the view model, the selected device's
        /// inputs refreshed, then the page's data). Null sets the page's data
        /// alone.</param>
        internal SearchService(MainViewModel vm, Func<SearchPage, FrameworkElement> pageOf, Action<PadViewModel> bindPad = null)
        {
            _vm = vm;
            _pageOf = pageOf;
            _bindPad = bindPad;
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
                    SetIndex(done.Result);
                    IndexBuilt?.Invoke(this, EventArgs.Empty);
                }));
            }, TaskScheduler.Default);
        }

        /// <summary>Takes an index made elsewhere (the tests, and the
        /// assistants' catalog when it is asked before the build is
        /// done).</summary>
        internal void SetIndex(IReadOnlyList<SearchEntry> index)
        {
            _index = index ?? Array.Empty<SearchEntry>();
            _ids = SearchIds.Assign(_index);
        }

        private Dictionary<SearchEntry, string> _ids = new(ReferenceEqualityComparer.Instance);

        /// <summary>An entry's stable ID (#597), the same in every
        /// language.</summary>
        internal string IdOf(SearchEntry entry) => entry != null && _ids.TryGetValue(entry, out string id) ? id : null;

        /// <summary>One entry that shows now, as the settings catalog reads
        /// it (#597): the entry, its item, its label's element, the
        /// controller it stands on and that controller's section name, and
        /// the value read for a controller the page is no longer bound
        /// to.</summary>
        internal readonly record struct Shown(SearchEntry Entry, object Item, DependencyObject Label, PadViewModel Pad, string Section, string Value);

        /// <summary>Every entry that showed at the last read
        /// (<see cref="RefreshAvailability"/>).</summary>
        internal IReadOnlyList<Shown> AvailableNow()
        {
            var list = new List<Shown>(_available.Count);
            foreach (var a in _available) list.Add(new Shown(a.Entry, a.Item, a.Label, a.Pad, a.Section, a.Value));
            return list;
        }

        /// <summary>The second line Search writes for an entry.</summary>
        internal string WhereFor(Shown shown)
            => WhereOf(shown.Entry, shown.Pad, shown.Section, shown.Item ?? (object)shown.Pad ?? _pageOf(shown.Entry.Page)?.DataContext);

        /// <summary>The value Search shows for an entry now.</summary>
        internal string ValueFor(Shown shown) => shown.Value ?? ValueOf(shown.Entry, shown.Item ?? shown.Label);

        /// <summary>How well an entry's words match a query: Search's own
        /// score, 0 for no match.</summary>
        internal int ScoreFor(Shown shown, string query)
        {
            var entry = shown.Entry;
            object data = shown.Item ?? (object)shown.Pad ?? _pageOf(entry.Page)?.DataContext;
            string title = SearchLocator.LabelText(entry.Label, data) ?? string.Empty;
            if (title.Length == 0) return 0;
            string qualifier = SearchLocator.LabelText(entry.Qualifier, data) ?? string.Empty;
            string english = SearchLocator.EnglishText(entry.Label);
            string englishQualifier = SearchLocator.EnglishText(entry.Qualifier);
            if (english != null && englishQualifier != null) english += " " + englishQualifier;
            int score = SearchMatcher.Score(query, qualifier.Length > 0 ? title + " " + qualifier : title, WhereFor(shown), english);
            return score == 0 ? 0 : score + Weight(entry);
        }

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

        /// <summary>Reads which entries show now: on each controller made
        /// (its section and tab shown), on the selected device, and for each
        /// item of a card per item, by the item's own gates.
        ///
        /// <para>The controller page shows one controller at a time, and
        /// what it shows depends on the controller's kind and its devices.
        /// So the page is read as it shows the controller it is bound to,
        /// and then bound to each other controller made in turn, in sidebar
        /// order, and read as it shows each (<see cref="ReadOtherControllers"/>).
        /// An entry that shows on more than one controller is kept for the
        /// first of them: a setting the bound controller has stays that
        /// controller's, and a setting it lacks is offered on the first
        /// controller that has it.</para>
        ///
        /// <para>With <paramref name="otherControllers"/> false the other
        /// controllers are left for <see cref="ReadOtherControllers"/>, so
        /// Search can show before they are read.</para></summary>
        internal void RefreshAvailability(bool otherControllers = true)
        {
            _available.Clear();
            _settingsGroup.Clear();
            _locator.Reset();
            _read++;
            _othersToRead = false;
            _taken = new HashSet<(SearchEntry, string, string)>();
            ReadEntries(e => e.Page != SearchPage.Pad, null, null, false);

            if (Pad == null) return;
            _readBound = BoundPad;
            if (IsMade(_readBound))
                ReadEntries(e => e.Page == SearchPage.Pad, _readBound, _taken, readValues: false);
            _othersToRead = true;
            if (otherControllers) ReadOtherControllers();
        }

        /// <summary>How many reads have begun, so a late read knows a newer
        /// read has replaced the one it belongs to.</summary>
        private int _read;

        /// <summary>The controller the page was bound to when the read
        /// began, the entries taken so far, and whether the other
        /// controllers are still to be read.</summary>
        private PadViewModel _readBound;
        private HashSet<(SearchEntry, string, string)> _taken = new();
        private bool _othersToRead;

        /// <summary>Reads every controller made but the bound one, which
        /// <see cref="RefreshAvailability"/> left to read. Each is bound the
        /// way a visit to its page binds it (the bind step the window
        /// passes). The bound controller was not refreshed by Search, and is
        /// bound back by its data alone, before anything is drawn. Does
        /// nothing when there is nothing left to read, or when the page has
        /// been bound to another controller since the read began.</summary>
        internal void ReadOtherControllers()
        {
            if (!_othersToRead) return;
            _othersToRead = false;
            var page = Pad;
            var bound = _readBound;
            if (page == null || !ReferenceEquals(page.DataContext, bound)) return;
            var order = new List<PadViewModel>();
            foreach (int index in _vm.NavControllerItems.Select(n => n.PadIndex).Concat(Enumerable.Range(0, InputManager.MaxPads)))
            {
                var other = index >= 0 && index < _vm.Pads.Count ? _vm.Pads[index] : null;
                if (IsMade(other) && !ReferenceEquals(other, bound) && !order.Contains(other)) order.Add(other);
            }
            if (order.Count == 0) return;

            page.ReadingForSearch = true;
            try
            {
                foreach (var pad in order)
                {
                    if (_bindPad != null) _bindPad(pad);
                    else page.DataContext = pad;
                    _locator.Reset();
                    ReadEntries(e => e.Page == SearchPage.Pad, pad, _taken, readValues: true);
                }
            }
            finally
            {
                if (!ReferenceEquals(page.DataContext, bound)) page.DataContext = bound;
                page.ReadingForSearch = false;
                _locator.Reset();
            }
        }

        /// <summary>Reads the other controllers at Background priority, once
        /// Search has drawn, then calls <paramref name="whenRead"/>. Skipped
        /// when Search has closed by then (<paramref name="isOpen"/>), or a
        /// newer read has begun, which posts its own.</summary>
        internal void ReadOtherControllersLater(Dispatcher dispatcher, Func<bool> isOpen, Action whenRead)
        {
            int read = _read;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (read != _read || !_othersToRead || !isOpen()) return;
                ReadOtherControllers();
                whenRead?.Invoke();
            }));
        }

        /// <summary>Reads the entries <paramref name="which"/> picks as their
        /// pages show now. For the controller page, <paramref name="pad"/>
        /// is the controller it shows, and an entry already in
        /// <paramref name="taken"/> (read on an earlier controller) is
        /// left out. <paramref name="readValues"/> reads each entry's value
        /// now, for a controller the page will not show once the read is
        /// done.</summary>
        private void ReadEntries(Func<SearchEntry, bool> which, PadViewModel pad, HashSet<(SearchEntry, string, string)> taken, bool readValues)
        {
            var padPage = Pad;
            // How many entries before each one show the same words in the
            // same scope, counted over every entry, shown or not: the Nth
            // such entry is the Nth such element of the scope.
            var seen = new Dictionary<(SearchPage, string, SearchLabelSource, string), int>();
            // A row's gates, each read once: the options of a row share them.
            List<MappingItem> rows = null;
            var passes = new Dictionary<(object, SearchGate), bool>();
            bool RowPasses(object item, SearchGate gate)
            {
                if (!passes.TryGetValue((item, gate), out bool pass))
                    passes[(item, gate)] = pass = BindingProbe.Passes(item, gate, k => Resource(SearchPage.Pad, k));
                return pass;
            }

            void Add(SearchEntry entry, object item, DependencyObject label)
            {
                if (pad == null)
                {
                    _available.Add(new Available(entry, item, label));
                    return;
                }
                object data = item ?? pad;
                var key = (entry, SearchLocator.LabelText(entry.Label, data) ?? string.Empty, SearchLocator.LabelText(entry.Qualifier, data) ?? string.Empty);
                if (!taken.Add(key)) return;
                string section = entry.Section >= 0 ? padPage.SectionName(entry.Section) : null;
                // Another controller's value is read now, while the page's
                // elements show that controller.
                string value = readValues ? ValueOf(entry, item ?? label) : null;
                _available.Add(new Available(entry, item, label, pad, section, value));
            }

            foreach (var entry in _index)
            {
                if (!which(entry)) continue;
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
                        if (pad == null || padPage == null) continue;
                        if (entry.Section >= 0 && !padPage.IsSectionShown(entry.Section)) continue;
                        if (entry.Tab >= 0 && !padPage.IsTabAvailable(entry.Tab)) continue;
                    }
                    else if (entry.Page == SearchPage.Devices && _vm.Devices?.SelectedDevice == null)
                        continue;

                    if (entry.Kind is SearchKind.Section or SearchKind.Part or SearchKind.Extra)
                    {
                        Add(entry, null, null);
                        continue;
                    }
                    if (entry.RowOptions)
                    {
                        // The controller's rows, not the table's: the table
                        // under the picture holds only the picked control's.
                        rows ??= pad.RowsOfSlot.ToList();
                        if (rows.Any(item => entry.ItemGates.All(g => RowPasses(item, g))))
                            Add(entry, null, null);
                        continue;
                    }
                    if (entry.ItemsPath != null)
                    {
                        var list = _locator.ListOf(entry);
                        if (list == null || !Shows(entry, list)) continue;
                        foreach (object item in list.Items)
                            if (entry.ItemGates.All(g => BindingProbe.Passes(item, g, k => Resource(entry.Page, k))))
                                Add(entry, item, null);
                        continue;
                    }
                    var label = _locator.Find(entry, n, text);
                    if (label != null && Shows(entry, label))
                        Add(entry, null, label);
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

            foreach (var (entry, item, label, pad, section, value) in _available)
            {
                object data = item ?? pad ?? _pageOf(entry.Page)?.DataContext;
                string title = SearchLocator.LabelText(entry.Label, data) ?? string.Empty;
                if (title.Length == 0) continue;
                string qualifier = SearchLocator.LabelText(entry.Qualifier, data) ?? string.Empty;
                string where = WhereOf(entry, pad, section, data);
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
                    Pad = pad,
                    ReadValue = value,
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
            foreach (var r in results) r.Value = r.ReadValue ?? (r.Entry == null ? string.Empty : ValueOf(r.Entry, r.Payload));
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
        private string WhereOf(SearchEntry entry, PadViewModel pad, string section, object data)
        {
            var parts = new List<string>();
            switch (entry.Page)
            {
                case SearchPage.Pad:
                    parts.Add(SlotName(pad));
                    if (entry.Kind == SearchKind.Section) break;
                    if (!string.IsNullOrEmpty(section)) parts.Add(section);
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

        /// <summary>The value an entry's element reads now: its value box
        /// and unit, its switch's On or Off, its picker's text, or a fold
        /// card's summary. <paramref name="payload"/> is the entry's item or
        /// its label's element.</summary>
        private string ValueOf(SearchEntry entry, object payload)
        {
            if (entry == null) return string.Empty;
            try
            {
                if (entry.Kind == SearchKind.Card && payload is PadForge.Controls.FoldCard fold)
                    return fold.Summary ?? string.Empty;
                if (entry.Value == null || entry.RowOptions) return string.Empty;
                object data;
                FrameworkElement row = null;
                if (entry.ItemsPath != null) data = payload;
                else
                {
                    row = SearchLocator.Up(payload as DependencyObject) as FrameworkElement;
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
