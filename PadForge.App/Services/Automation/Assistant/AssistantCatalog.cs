using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Search;
using PadForge.Services.Catalog;
using PadForge.ViewModels;
using PadForge.Views;

namespace PadForge.Services.Automation
{
    /// <summary>The window's parts the catalog reaches beyond Search's five
    /// pages (#600). Each is optional: a test leaves what it does not
    /// stand.</summary>
    internal sealed class AssistantCatalogParts
    {
        /// <summary>The root of Home, About and the main window, for their
        /// catalog entries.</summary>
        public Func<SearchPage, FrameworkElement> CatalogPageOf { get; init; }

        /// <summary>The sidebar's entry for a page's tag (Home, Devices,
        /// Pad1 and the rest).</summary>
        public Func<string, FrameworkElement> NavItem { get; init; }

        /// <summary>The tag of the page shown.</summary>
        public Func<string> NavTag { get; init; }

        /// <summary>Runs a tray menu item's work, by its key.</summary>
        public Action<string> PressTray { get; init; }

        /// <summary>The main window, which is no dialog.</summary>
        public Func<Window> MainWindow { get; init; }

        /// <summary>The guided pointer.</summary>
        public AssistantPointer Pointer { get; init; }
    }

    /// <summary>
    /// The catalog an assistant reaches (#600): every setting, action, card,
    /// section, extra and menu item of PadForge's pages (Search's index),
    /// every dialog's and the window's markup, the tray menu, and the live
    /// objects and items (LiveCatalog). A place's ID is Search's stable ID
    /// with what it is shown for (SearchIds), or a live object's ID.
    ///
    /// <para>What shows, and on which controller and device, is read the
    /// way Search reads it when it opens (SearchService.RefreshAvailability).
    /// A value is read the way Search's results read it. A write goes
    /// through the control on the page, so it is coerced, checked and saved
    /// exactly as a person's change is, and the page shows it. A press goes
    /// through the control's own automation peer, as a screen reader's
    /// press does, posted so a dialog it opens holds nothing. A read of
    /// another controller or device binds the page to it and back before
    /// anything is drawn, as Search does, so a read changes nothing on
    /// screen. Called on the interface's thread.</para>
    /// </summary>
    internal sealed class AssistantCatalog : IAssistantCatalog
    {
        private readonly SearchService _service;
        private readonly SearchNavigator _navigator;
        private readonly Func<SearchPage, FrameworkElement> _pageOf;
        private readonly MainViewModel _vm;
        private readonly Action<PadViewModel> _bindPad;
        private readonly Func<AssistantWindowPlace> _windowPlace;
        private readonly AssistantCatalogParts _parts;
        private readonly Dictionary<SearchEntry, string> _trayIds;
        private readonly IReadOnlyList<SearchEntry> _tray;

        /// <summary>The class a program reads an entry by (#600): Unset reads
        /// as the user's own.</summary>
        internal static ReachClass ReachOf(SearchEntry entry)
            => entry.Reach == ReachClass.Unset ? ReachClass.UsersOwn : entry.Reach;

        public AssistantCatalog(SearchService service, SearchNavigator navigator, Func<SearchPage, FrameworkElement> pageOf,
            MainViewModel vm, Action<PadViewModel> bindPad, Func<AssistantWindowPlace> windowPlace, AssistantCatalogParts parts = null)
        {
            _service = service;
            _navigator = navigator;
            _pageOf = pageOf;
            _vm = vm;
            _bindPad = bindPad;
            _windowPlace = windowPlace ?? (() => AssistantWindowPlace.InFront);
            _parts = parts ?? new AssistantCatalogParts();
            _tray = TrayMenu.Entries();
            _trayIds = SearchIds.Assign(_tray);
            // A mapping row's mouse settings ring their lines on this
            // catalog's controller page.
            MouseSteeringCatalog.DrawnOn = () => pageOf?.Invoke(SearchPage.Pad);
        }

        internal AssistantCatalogParts Parts => _parts;

        // ─────────────────────────────────────────────
        //  The places
        // ─────────────────────────────────────────────

        /// <summary>One place of the catalog, read now.</summary>
        internal sealed class Place
        {
            public string Id;
            public SearchEntry Entry;
            public object Item;
            public DependencyObject Label;
            public PadViewModel Pad;
            public string Section;
            public string ReadValue;
            public LiveEntry Live;
            public FrameworkElement Root;
            public bool Showing;

            public LiveItem LiveItem => Live?.Payload as LiveItem;
        }

        private void EnsureIndex()
        {
            if (_service.Index.Count == 0) _service.SetIndex(SearchIndexBuilder.Build(SearchSources.Load()));
            if (_service.Dialogs.Count == 0) _service.SetDialogs(SearchIndexBuilder.BuildCatalog(SearchSources.LoadCatalog()));
        }

        private int? NumberOf(PadViewModel pad)
        {
            if (pad == null) return null;
            int n = SettingsManager.SlotOrders.GetGlobalSlotNumber(pad.PadIndex);
            return n > 0 ? n : pad.PadIndex + 1;
        }

        private object RootOf(Place p)
            => (object)p.Pad ?? _pageOf(p.Entry.Page)?.DataContext ?? p.Root?.DataContext;

        /// <summary>The words a show or a press refuses an ID with that names
        /// no item of a menu each item has of its own.</summary>
        internal const string EachOwnWords = "Each card or tab has this item on its own menu, and this ID names none of them. find gives an ID for each one's item.";

        /// <summary>
        /// A menu each item of a list has of its own, a profile card's or a
        /// layer tab's (<see cref="SearchEntry.ItemsList"/>): a place for
        /// each item, its position after <c>@i</c>, and the entry's own
        /// place, which names no item and shows nowhere. The items are the
        /// list's data, read whether or not the list drew them. An element
        /// the list holds as it is, Profiles' drop tile, has no menu of the
        /// template's.
        /// </summary>
        private IEnumerable<Place> EachItemsMenu(Place p)
        {
            var page = _pageOf(p.Entry.Page);
            string bare = _service.IdOf(p.Entry);
            int? controller = p.Entry.Page == SearchPage.Pad ? NumberOf(p.Pad) : null;
            yield return new Place { Entry = p.Entry, Pad = p.Pad, Section = p.Section, Id = SearchIds.With(bare, controller, null, null), Showing = false };
            if (page?.FindName(p.Entry.ItemsList) is not ItemsControl list) yield break;
            bool shows = p.Showing && SearchLocator.Shows(list, page);
            int i = 0;
            foreach (object item in list.Items)
            {
                if (item is UIElement) continue;
                yield return new Place { Entry = p.Entry, Item = item, Pad = p.Pad, Section = p.Section, Id = SearchIds.With(bare, controller, i, null), Showing = shows };
                i++;
            }
        }

        /// <summary>Whether a place of a menu each item has of its own names
        /// no item: its ID carries no <c>@i</c>. An ID that names an item the
        /// page has not, past the last or on another controller, is a place
        /// that does not show.</summary>
        private static bool NamesNoItem(Place p)
            => p.Entry?.ItemsList != null && p.Item == null
               && !(SearchIds.TryRead(p.Id, out _, out _, out int? item, out _) && item != null);

        /// <summary>The menu item an entry names and the shown button whose
        /// menu holds it: for a menu each item has of its own, the one in
        /// that item's container once the list draws it.</summary>
        private (FrameworkElement Owner, MenuItem Item) MenuOf(Place p, FrameworkElement root)
        {
            if (p.Entry.ItemsList == null) return SearchNavigator.MenuItemOf(root, p.Entry);
            if (p.Item == null || root?.FindName(p.Entry.ItemsList) is not ItemsControl list) return (null, null);
            return SearchNavigator.MenuItemIn(list.ItemContainerGenerator.ContainerFromItem(p.Item), p.Entry);
        }

        /// <summary>The words a card or a tab is known by, for the place of
        /// its own menu's item: a profile's name, a layer's.</summary>
        private static string ItemName(object item) => item switch
        {
            ProfileListItem profile => profile.Name,
            ShiftLayerInfo layer => layer.LayerName,
            _ => item?.ToString(),
        };

        private int? ItemIndexOf(SearchEntry entry, object item, PadViewModel pad)
        {
            if (item == null || entry.ItemsPath == null) return null;
            object root = (object)pad ?? _pageOf(entry.Page)?.DataContext;
            if (BindingProbe.Read(root, entry.ItemsPath) is IEnumerable list)
            {
                int i = 0;
                foreach (object o in list)
                {
                    if (ReferenceEquals(o, item)) return i;
                    i++;
                }
            }
            return null;
        }

        private string DeviceOf(SearchEntry entry)
            => entry.Page == SearchPage.Devices && _vm.Devices?.SelectedDevice is { } row ? AutomationDevices.IdOf(row.InstanceGuid) : null;

        /// <summary>Every place of the catalog, the five pages as they show
        /// now first. A five-page entry that shows on no controller and no
        /// item is listed too, as not showing, so an ID always reads.</summary>
        private List<Place> Places(bool otherControllers = true)
        {
            EnsureIndex();
            _service.RefreshAvailability(otherControllers);
            var list = new List<Place>();
            var shown = new HashSet<SearchEntry>(ReferenceEqualityComparer.Instance);
            foreach (var s in _service.AvailableNow())
            {
                shown.Add(s.Entry);
                var place = new Place
                {
                    Entry = s.Entry, Item = s.Item, Label = s.Label, Pad = s.Pad, Section = s.Section, ReadValue = s.Value, Showing = true,
                    Id = SearchIds.With(_service.IdOf(s.Entry), s.Entry.Page == SearchPage.Pad ? NumberOf(s.Pad) : null,
                        ItemIndexOf(s.Entry, s.Item, s.Pad), DeviceOf(s.Entry)),
                };
                if (s.Entry.Kind == SearchKind.MenuItem && s.Entry.ItemsList != null) list.AddRange(EachItemsMenu(place));
                else list.Add(place);
            }
            foreach (var entry in _service.Index)
                if (!shown.Contains(entry)) list.Add(new Place { Entry = entry, Id = _service.IdOf(entry), Showing = false });
            AddCatalogMarkup(list);
            foreach (var entry in _tray)
                list.Add(new Place { Entry = entry, Id = _trayIds[entry], Showing = true });
            foreach (var live in LiveCatalog.Entries(_vm))
                list.Add(new Place { Live = live, Id = live.Id, Showing = true });
            return list;
        }

        /// <summary>The dialogs', Home's, About's and the window's entries:
        /// a dialog's shows while the dialog is open, located inside it.</summary>
        private void AddCatalogMarkup(List<Place> list)
        {
            var open = OpenDialogs();
            var locators = new Dictionary<FrameworkElement, SearchLocator>();
            SearchLocator LocatorOf(FrameworkElement root)
            {
                if (!locators.TryGetValue(root, out var locator)) locators[root] = locator = new SearchLocator(_ => root);
                return locator;
            }
            foreach (var entry in _service.Dialogs)
            {
                FrameworkElement root = entry.Page == SearchPage.Dialog
                    ? open.FirstOrDefault(d => d.Name == entry.Dialog)?.Element
                    : _parts.CatalogPageOf?.Invoke(entry.Page);
                DependencyObject label = null;
                if (root != null && entry.Kind != SearchKind.Dialog && entry.Kind != SearchKind.MenuItem)
                {
                    try { label = LocatorOf(root).Find(entry, _service.Dialogs); }
                    catch (Exception) { label = null; }
                }
                bool showing = entry.Kind == SearchKind.Dialog
                    ? root != null
                    : label != null && SearchLocator.Shows(label, root);
                list.Add(new Place { Entry = entry, Id = _service.IdOf(entry), Label = label, Root = root, Showing = showing });
            }
            // A window PadForge shows that no markup of the catalog
            // describes is read as it shows: its title, text and buttons.
            foreach (var d in open)
                if (!_service.Dialogs.Any(e => e.Kind == SearchKind.Dialog && e.Dialog == d.Name))
                    list.Add(new Place { Id = "open/" + d.Name, Root = d.Element, Showing = true });
        }

        /// <summary>The place an ID names, read now. Refuses an ID no place
        /// has.</summary>
        private Place Resolve(string id, List<Place> all = null)
        {
            string want = id?.Trim() ?? string.Empty;
            if (want.Length == 0) throw Unknown(id);
            all ??= Places();
            var found = all.FirstOrDefault(p => string.Equals(p.Id, want, StringComparison.Ordinal));
            if (found != null) return found;
            // An entry's own ID with nothing it is shown for: the first place
            // it shows, on the first controller, item or device that has it.
            found = all.Where(p => p.Entry != null && _service.IdOf(p.Entry) == want && !(p.Entry.ItemsList != null && p.Item != null))
                .OrderByDescending(p => p.Showing).FirstOrDefault();
            if (found != null) return found;
            // A five-page ID for a controller, item or device that does not
            // show it now: named, and not showing.
            if (SearchIds.TryRead(want, out string bare, out _, out _, out _) && bare != want)
            {
                var entry = all.FirstOrDefault(p => p.Entry != null && p.Id == bare)?.Entry
                            ?? all.FirstOrDefault(p => p.Entry != null && _service.IdOf(p.Entry) == bare)?.Entry;
                if (entry != null) return new Place { Entry = entry, Id = want, Showing = false };
            }
            throw Unknown(id);
        }

        private static AssistantRefusal Unknown(string id)
            => new AssistantRefusal("unknown-setting", "Nothing in PadForge has the ID " + id + ". find gives IDs.");

        /// <summary>Puts the device a Devices-page ID names on that page for
        /// the length of <paramref name="work"/>, and the one it showed back
        /// after, before anything is drawn.</summary>
        private T WithDevice<T>(string id, Func<T> work)
        {
            SearchIds.TryRead(id?.Trim(), out _, out _, out _, out string device);
            var devices = _vm.Devices;
            if (device == null || devices == null) return work();
            var row = devices.Devices?.FirstOrDefault(r => AutomationDevices.IdOf(r.InstanceGuid) == device)
                ?? throw new AssistantRefusal("unknown-device", "No device has the ID " + device + ". list_devices gives them.");
            var before = devices.SelectedDevice;
            if (ReferenceEquals(before, row)) return work();
            devices.SelectedDevice = row;
            try { return work(); }
            finally { devices.SelectedDevice = before; }
        }

        // ─────────────────────────────────────────────
        //  Describing a place
        // ─────────────────────────────────────────────

        private static string KindWord(SearchKind kind) => kind switch
        {
            SearchKind.Setting => "setting",
            SearchKind.Card => "card",
            SearchKind.Action => "action",
            SearchKind.Extra => "extra",
            SearchKind.Section => "section",
            SearchKind.Part => "part",
            SearchKind.Dialog => "dialog",
            _ => "menu_item",
        };

        private static string KindWord(LiveKind kind) => kind switch
        {
            LiveKind.Device => "device",
            LiveKind.Controller => "controller",
            LiveKind.Profile => "profile",
            LiveKind.Macro => "macro",
            LiveKind.Plugin => "plugin",
            LiveKind.PluginDeclaration => "plugin_setting",
            LiveKind.Action => "action",
            LiveKind.MenuItem => "menu_item",
            LiveKind.Page => "page",
            _ => "setting",
        };

        /// <summary>A place's kind word: a window no markup describes is a
        /// dialog.</summary>
        private static string KindOf(Place p)
            => p.Live != null ? KindWord(p.Live.Kind) : p.Entry != null ? KindWord(p.Entry.Kind) : "dialog";

        /// <summary>The kinds find takes.</summary>
        internal static readonly string[] Kinds =
        {
            "setting", "action", "card", "section", "part", "extra", "page", "dialog", "menu_item",
            "device", "controller", "profile", "macro", "plugin", "plugin_setting",
        };

        private static string PageName(SearchPage page)
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            return page switch
            {
                SearchPage.Pad => s.Home_Controllers,
                SearchPage.Settings => s.Settings_Title,
                SearchPage.Extras => s.Extras_Title,
                SearchPage.Devices => s.Devices_Title,
                SearchPage.Profiles => s.Profiles_Title,
                SearchPage.Home => s.Home_Title,
                SearchPage.About => s.About_Title,
                SearchPage.Window => s.Common_PadForge,
                _ => s.Search_Group_Dialogs,
            };
        }

        /// <summary>The element a place's words show in: its row for a
        /// setting, the card for a card, the button for an action.</summary>
        private FrameworkElement Target(Place p)
        {
            if (p.Entry == null)
            {
                return p.LiveItem?.Element?.Invoke();
            }
            if (p.Label == null) return null;
            return SearchLocator.TargetOf(p.Label, p.Entry);
        }

        /// <summary>The class an element of the place takes now: the
        /// markup's, made stricter by what the element sits in.</summary>
        private (ReachClass Class, ReachReason Why) ClassOf(Place p, FrameworkElement element = null)
        {
            ReachClass cls;
            ReachReason why;
            if (p.Entry != null) { cls = ReachOf(p.Entry); why = p.Entry.Why; }
            else if (p.LiveItem is { } item) { cls = item.Reach; why = item.Why; }
            else { cls = ReachClass.Free; why = ReachReason.None; }
            element ??= Target(p);
            if (element != null)
            {
                var (live, liveWhy) = Reach.Of(element);
                if (live == ReachClass.Unset) live = ReachClass.UsersOwn;
                if (live > cls) { cls = live; why = liveWhy; }
            }
            if (cls == ReachClass.Unset) cls = ReachClass.UsersOwn;
            return (cls, why);
        }

        private string WordsOf(Place p)
        {
            if (p.Live != null) return p.Live.Name;
            if (p.Entry == null) return p.Root == null ? p.Id : DialogRegistry.Describe(p.Root)?.Title;
            object data = p.Item ?? RootOf(p);
            string title = SearchLocator.LabelText(p.Entry.Label, data) ?? string.Empty;
            string qualifier = SearchLocator.LabelText(p.Entry.Qualifier, data);
            if (string.IsNullOrEmpty(title) && p.Entry.Name != null) title = SearchLocator.LabelText(p.Entry.Name, data) ?? string.Empty;
            if (string.IsNullOrEmpty(title) && p.Entry.AutomationId != null) title = p.Entry.AutomationId;
            return string.IsNullOrEmpty(qualifier) ? title : title + " " + qualifier;
        }

        private string WhereOf(Place p)
        {
            if (p.Live != null) return p.LiveItem?.Where ?? p.Live.Where ?? string.Empty;
            if (p.Entry == null) return PageName(SearchPage.Dialog);
            if (p.Entry.Page is SearchPage.Pad or SearchPage.Settings or SearchPage.Extras or SearchPage.Devices or SearchPage.Profiles)
            {
                if (!p.Showing) return PageName(p.Entry.Page);
                string where = _service.WhereFor(new SearchService.Shown(p.Entry, p.Item, p.Label, p.Pad, p.Section, p.ReadValue));
                // An item's own menu: the card or tab it is.
                if (p.Entry.ItemsList != null && ItemName(p.Item) is { Length: > 0 } item)
                    where = string.IsNullOrEmpty(where) ? item : where + PadForge.Resources.Strings.Strings.Instance.Search_WhereSeparator + item;
                return where;
            }
            if (p.Entry.Page == SearchPage.Dialog)
            {
                var dialog = _service.Dialogs.FirstOrDefault(d => d.Kind == SearchKind.Dialog && d.Dialog == p.Entry.Dialog);
                string title = dialog == null ? p.Entry.Dialog : SearchLocator.LabelText(dialog.Label, null) ?? p.Entry.Dialog;
                return p.Entry.Kind == SearchKind.Dialog ? PageName(SearchPage.Dialog) : title;
            }
            return PageName(p.Entry.Page);
        }

        /// <summary>The dialog an action opens, by its ID, through the
        /// catalog's table of openers.</summary>
        private string OpensOf(SearchEntry entry)
        {
            if (entry?.Handler == null) return null;
            foreach (var (dialog, opener) in DialogCatalog.Openers)
            {
                if (opener.Page != entry.Page || opener.Handler != entry.Handler) continue;
                var d = _service.Dialogs.FirstOrDefault(e => e.Kind == SearchKind.Dialog && e.Dialog == dialog);
                return d == null ? null : _service.IdOf(d);
            }
            return null;
        }

        private AssistantPlace Describe(Place p)
        {
            var target = Target(p);
            var (cls, _) = ClassOf(p, target);
            var place = new AssistantPlace
            {
                Id = p.Id,
                Kind = KindOf(p),
                Name = AutomationReadScrub.Text(WordsOf(p)),
                Where = WhereOf(p),
                Showing = p.Showing,
                UnderMore = p.Entry?.UnderMore ?? false,
                Reach = AutomationReadScrub.ReachWord(cls),
            };
            if (p.Entry != null)
            {
                object data = p.Item ?? RootOf(p);
                place.Help = AutomationReadScrub.Text(SearchLocator.LabelText(p.Entry.Help, data));
                place.Opens = OpensOf(p.Entry);
            }
            if (p.Live?.Payload is DeviceRowViewModel deviceRow && SettingsManager.FindDeviceByInstanceGuid(deviceRow.InstanceGuid) is { } ud)
                place.Maker = OutsideText.Of(AssistantDeviceMaker.Of(ud));
            if (p.LiveItem?.Help is { Length: > 0 } help) place.Help = AutomationReadScrub.Text(help);
            if (cls == ReachClass.UsersOwn) return place;

            // A plugin as its card's line shows it: its state, why it stopped,
            // the devices it added and its signer (#600 item 18).
            if (p.Live?.Kind == LiveKind.Plugin)
            {
                place.ValueText = AutomationReadScrub.ForReach(cls, PluginLine(p.Live.Payload));
                return place;
            }
            if (p.LiveItem is { } item)
            {
                if (target != null) place.Enabled = target.IsEnabled;
                DescribeItem(item, place, cls);
                return place;
            }
            if (p.Entry == null) return place;
            if (target != null) place.Enabled = target.IsEnabled;
            if (p.Entry.Kind == SearchKind.Card)
            {
                if (p.Showing) place.ValueText = AutomationReadScrub.Text(_service.ValueFor(new SearchService.Shown(p.Entry, p.Item, p.Label, p.Pad, p.Section, p.ReadValue)));
                return place;
            }
            if (p.Entry.Kind is SearchKind.Section or SearchKind.Part or SearchKind.Extra)
            {
                place.Takes = "section";
                return place;
            }
            if (p.Entry.Kind != SearchKind.Setting || !p.Showing) return place;
            DescribeValue(p, place, cls);
            return place;
        }

        private static string SafeRead(Func<string> read)
        {
            try { return read?.Invoke(); }
            catch (Exception) { return null; }
        }

        /// <summary>A live item's value, kind, range and choices, as its row
        /// shows them: a switch on or off, a number with its unit, a choice
        /// by its words.</summary>
        private static void DescribeItem(LiveItem item, AssistantPlace place, ReachClass cls)
        {
            string raw = AutomationReadScrub.ForReach(cls, SafeRead(item.Read));
            place.Takes = item.Takes ?? (item.Write != null ? "text" : null);
            place.Minimum = item.Minimum;
            place.Maximum = item.Maximum;
            place.Unit = item.Unit;
            if (item.Choices != null)
            {
                IReadOnlyList<string> choices;
                try { choices = item.Choices(); }
                catch (Exception) { choices = null; }
                place.Choices = choices?.Take(MostChildren).Select(c => AutomationReadScrub.Text(c) ?? string.Empty).ToList();
            }
            place.ValueText = raw;
            if (raw == null) return;
            var s = PadForge.Resources.Strings.Strings.Instance;
            switch (place.Takes)
            {
                case "switch" when bool.TryParse(raw, out bool on):
                    place.Value = JsonSerializer.SerializeToElement(on);
                    place.ValueText = on ? s.Common_On : s.Common_Off;
                    break;
                case "number" when double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number):
                    place.Value = JsonSerializer.SerializeToElement(number);
                    place.ValueText = item.Unit == null ? raw : raw + " " + item.Unit;
                    break;
                default:
                    if (item.Write != null || place.Takes != null) place.Value = JsonSerializer.SerializeToElement(raw);
                    break;
            }
        }

        /// <summary>A plugin's line as its card shows it: a plugin's state,
        /// the devices it added and its signer, or the Test Controller's
        /// state.</summary>
        private static string PluginLine(object payload) => payload switch
        {
            PluginRowItem row => row.State,
            DashboardViewModel dashboard => dashboard.TestControllerLine,
            _ => null,
        };

        /// <summary>The devices a plugin added and PadForge keeps now, by
        /// their identities: a plugin's, as its card names them, or the Test
        /// Controller's controllers.</summary>
        private static HashSet<Guid> PluginDevices(object payload)
        {
            var guids = new HashSet<Guid>();
            if (payload is PluginRowItem row) guids.UnionWith(row.Devices);
            else if (payload is DashboardViewModel)
                for (int i = 0; i < PadForge.Services.Plugins.TestControllerPattern.MostControllers; i++)
                    guids.Add(PadForge.Services.Plugins.BuiltInPlugins.TestControllerGuidOf(i));
            return guids;
        }

        /// <summary>The paragraphs a plugin's help declarations give an
        /// object, in their order: the plugin's own, or a device's.</summary>
        private static List<string> Paragraphs(IEnumerable<Place> all, string prefix)
            => all.Select(c => c.LiveItem).Where(li => li != null && li.Paragraph && li.Help != null && li.Id.StartsWith(prefix, StringComparison.Ordinal))
                .Select(li => AutomationReadScrub.Text(li.Help)).Where(t => !string.IsNullOrEmpty(t)).ToList();

        /// <summary>A setting's value, kind, range or choices and whether
        /// its input takes a change now.</summary>
        private void DescribeValue(Place p, AssistantPlace place, ReachClass cls)
        {
            var entry = p.Entry;
            var row = IsBound(p) && p.Label != null ? SearchLocator.Up(p.Label) as FrameworkElement : null;
            if (entry.Value != null)
            {
                place.ValueText = AutomationReadScrub.ForReach(cls, _service.ValueFor(new SearchService.Shown(entry, p.Item, p.Label, p.Pad, p.Section, p.ReadValue)));
                switch (entry.Value.Kind)
                {
                    case SearchValueKind.Number:
                    {
                        place.Takes = "number";
                        var (min, max) = RangeOf(row, entry);
                        place.Minimum = min;
                        place.Maximum = max;
                        place.Unit = entry.Value.UnitKey != null ? PadForge.Resources.Strings.Strings.Get(entry.Value.UnitKey) : entry.Value.UnitText;
                        if (TryNumber(ReadRaw(p), out double number)) place.Value = JsonSerializer.SerializeToElement(number);
                        break;
                    }
                    case SearchValueKind.Switch:
                        place.Takes = "switch";
                        if (ReadRaw(p) is bool on) place.Value = JsonSerializer.SerializeToElement(on);
                        break;
                    case SearchValueKind.Choice:
                    {
                        place.Takes = "choice";
                        var combo = row == null ? null : SearchLocator.LogicalDescendants(row).OfType<ComboBox>().FirstOrDefault();
                        if (combo != null) place.Choices = Choices(combo);
                        if (!string.IsNullOrEmpty(place.ValueText)) place.Value = JsonSerializer.SerializeToElement(place.ValueText);
                        break;
                    }
                }
                var input = InputOf(row, entry);
                if (input != null) place.Enabled = input.IsEnabled;
                return;
            }
            // A row with no value the index reads: what its own control
            // takes, text, a list or a choice of buttons.
            if (row == null) return;
            var parts = SearchLocator.LogicalDescendants(row).OfType<FrameworkElement>().Where(e => e.IsVisible).ToList();
            if (parts.OfType<TextBox>().FirstOrDefault(t => !t.IsReadOnly) is { } box)
            {
                place.Takes = "text";
                place.Enabled = box.IsEnabled;
                place.ValueText = AutomationReadScrub.ForReach(cls, box.Text);
                if (place.ValueText != null) place.Value = JsonSerializer.SerializeToElement(place.ValueText);
            }
            else if (parts.OfType<ComboBox>().FirstOrDefault() is { } combo)
            {
                place.Takes = "choice";
                place.Enabled = combo.IsEnabled;
                place.Choices = Choices(combo);
                place.ValueText = AutomationReadScrub.ForReach(cls, combo.Text);
                if (!string.IsNullOrEmpty(place.ValueText)) place.Value = JsonSerializer.SerializeToElement(place.ValueText);
            }
            else if (parts.OfType<ListBox>().FirstOrDefault() is { } listBox)
            {
                place.Takes = "list";
                place.Enabled = listBox.IsEnabled;
                place.Choices = listBox.Items.Cast<object>().Select(i => ItemText(listBox, i)).ToList();
                if (listBox.SelectedItem != null)
                {
                    place.ValueText = AutomationReadScrub.ForReach(cls, ItemText(listBox, listBox.SelectedItem));
                    if (place.ValueText != null) place.Value = JsonSerializer.SerializeToElement(place.ValueText);
                }
            }
            else if (parts.OfType<RadioButton>().ToList() is { Count: > 1 } radios)
            {
                place.Takes = "choice";
                place.Choices = radios.Select(r => r.Content as string ?? AutomationProperties.GetName(r)).ToList();
                var on = radios.FirstOrDefault(r => r.IsChecked == true);
                place.Enabled = radios.Any(r => r.IsEnabled);
                if (on != null)
                {
                    place.ValueText = on.Content as string ?? AutomationProperties.GetName(on);
                    place.Value = JsonSerializer.SerializeToElement(place.ValueText);
                }
            }
        }

        /// <summary>The input of a setting's row the index's value binds, or
        /// its first input.</summary>
        private static Control InputOf(FrameworkElement row, SearchEntry entry)
        {
            if (row == null) return null;
            var parts = SearchLocator.LogicalDescendants(row).OfType<Control>().ToList();
            string path = entry.Value?.Path;
            if (path != null)
            {
                foreach (var c in parts)
                {
                    var dp = c switch
                    {
                        Wpf.Ui.Controls.NumberBox => Wpf.Ui.Controls.NumberBox.ValueProperty,
                        ToggleButton => ToggleButton.IsCheckedProperty,
                        TextBox => TextBox.TextProperty,
                        RangeBase => RangeBase.ValueProperty,
                        _ => null,
                    };
                    if (dp != null && BindingOperations.GetBinding(c, dp)?.Path?.Path == path) return c;
                }
            }
            return parts.FirstOrDefault(c => c is Wpf.Ui.Controls.NumberBox or TextBox or ToggleButton or ComboBox or RangeBase && c is not ScrollBar);
        }

        /// <summary>Whether the page's own elements show this place's
        /// controller now, so its row can be read and written in place.</summary>
        private bool IsBound(Place p)
        {
            if (p.Entry?.Page != SearchPage.Pad) return true;
            return ReferenceEquals(_pageOf(SearchPage.Pad)?.DataContext, p.Pad);
        }

        private object ReadRaw(Place p)
        {
            var value = p.Entry.Value;
            if (value?.Path == null) return null;
            object data = DataOf(p);
            var converter = value.ConverterKey == null ? null : _pageOf(p.Entry.Page)?.TryFindResource(value.ConverterKey) as IValueConverter;
            return BindingProbe.Read(data, value.Path, value.Kind == SearchValueKind.Number ? converter : null);
        }

        private object DataOf(Place p)
        {
            if (p.Entry.ItemsPath != null) return p.Item;
            if (p.Entry.Page == SearchPage.Pad) return p.Pad;
            var row = p.Label == null ? null : SearchLocator.Up(p.Label) as FrameworkElement;
            return row?.DataContext ?? _pageOf(p.Entry.Page)?.DataContext ?? p.Root?.DataContext;
        }

        private static bool TryNumber(object raw, out double number)
        {
            number = 0;
            switch (raw)
            {
                case null: return false;
                case string text:
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out number)
                        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
                case IConvertible convertible:
                    try { number = convertible.ToDouble(CultureInfo.InvariantCulture); return !double.IsNaN(number); }
                    catch (FormatException) { return false; }
                    catch (InvalidCastException) { return false; }
                default: return false;
            }
        }

        /// <summary>The range a row's number takes, from its slider or number
        /// box, whose bounds the markup sets, in the unit the row shows: a
        /// slider bound to the raw value while the row's box shows it through
        /// a converter has its bounds put through the same converter.</summary>
        private (double? Min, double? Max) RangeOf(FrameworkElement row, SearchEntry entry)
        {
            if (row == null) return (null, null);
            foreach (var part in SearchLocator.LogicalDescendants(row))
            {
                if (part is Wpf.Ui.Controls.NumberBox box) return (box.Minimum, box.Maximum);
                if (part is not RangeBase range || part is ScrollBar) continue;
                var own = BindingOperations.GetBinding(range, RangeBase.ValueProperty);
                string converterKey = entry?.Value?.ConverterKey;
                if (converterKey == null || own == null || own.Converter != null || own.Path?.Path != entry.Value.Path)
                    return (range.Minimum, range.Maximum);
                var converter = _pageOf(entry.Page)?.TryFindResource(converterKey) as IValueConverter;
                if (converter == null) return (range.Minimum, range.Maximum);
                double? Shown(double raw)
                    => TryNumber(converter.Convert(raw, typeof(string), null, CultureInfo.CurrentCulture), out double v) ? v : null;
                double? a = Shown(range.Minimum), b = Shown(range.Maximum);
                if (a == null || b == null) return (null, null);
                return (Math.Min(a.Value, b.Value), Math.Max(a.Value, b.Value));
            }
            return (null, null);
        }

        private static List<string> Choices(ComboBox combo)
        {
            var list = new List<string>();
            foreach (object item in combo.Items) list.Add(ItemText(combo, item));
            return list;
        }

        /// <summary>
        /// The words of a list's row it has not drawn, as it would draw them
        /// (#600 item 8): the member it names, else the texts its item
        /// template shows for the item (the list's own, its selector's, or
        /// the one its resources keep for the item's type), made apart from
        /// the list on the item, else the item's text, which the list draws
        /// as it is. A row the list has drawn is read from what it drew.
        /// </summary>
        private static string RowText(ItemsControl list, object item)
        {
            if (item is ContentControl || item == null || !string.IsNullOrEmpty(list.DisplayMemberPath)) return ItemText(list, item);
            var template = list.ItemTemplate ?? list.ItemTemplateSelector?.SelectTemplate(item, list)
                           ?? list.TryFindResource(new DataTemplateKey(item.GetType())) as DataTemplate;
            if (template != null && TemplateText(template, item) is { } drawn) return drawn;
            return ItemText(list, item);
        }

        /// <summary>The texts a template shows for an item, in order: the
        /// row made apart from any window, in a presenter of its own as the
        /// list's own row presents it, with each text's and each part's
        /// binding read now. A text the template hides is left out. Null when
        /// the template cannot be made.</summary>
        private static string TemplateText(DataTemplate template, object item)
        {
            var presenter = new ContentPresenter { Content = item, ContentTemplate = template };
            try
            {
                presenter.ApplyTemplate();
                presenter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }
            catch (Exception) { return null; }
            var texts = new List<string>();
            foreach (var text in SearchLocator.VisualDescendants(presenter).OfType<TextBlock>())
            {
                Refresh(text, TextBlock.TextProperty);
                if (string.IsNullOrWhiteSpace(text.Text)) continue;
                bool hidden = false;
                for (DependencyObject at = text; at != null && !ReferenceEquals(at, presenter) && !hidden;
                     at = at is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(at) : null)
                {
                    Refresh(at, UIElement.VisibilityProperty);
                    hidden = at is UIElement { Visibility: not Visibility.Visible };
                }
                if (!hidden) texts.Add(text.Text);
            }
            return string.Join(" ", texts);
        }

        /// <summary>Reads a property's binding now, for an element no window
        /// lays out.</summary>
        private static void Refresh(DependencyObject element, DependencyProperty property)
        {
            try { BindingOperations.GetBindingExpressionBase(element, property)?.UpdateTarget(); }
            catch (InvalidOperationException) { }
        }

        private static string ItemText(ItemsControl list, object item)
        {
            if (item is ContentControl cc) return cc.Content as string ?? cc.Content?.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(list.DisplayMemberPath)) return BindingProbe.Read(item, list.DisplayMemberPath)?.ToString() ?? string.Empty;
            return item?.ToString() ?? string.Empty;
        }

        // ─────────────────────────────────────────────
        //  find
        // ─────────────────────────────────────────────

        public AssistantFound Find(string words, string kind, int most)
        {
            string q = (words ?? string.Empty).Trim();
            string k = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim().ToLowerInvariant();
            if (k != null && Array.IndexOf(Kinds, k) < 0)
                throw new AssistantRefusal("bad-argument", "kind is one of " + string.Join(", ", Kinds) + ".");
            if (q.Length == 0 && k == null) throw new AssistantRefusal("bad-argument", "Give words from the place's name or where it is, or a kind.");
            var scored = new List<(Place Place, int Score, int Order)>();
            int order = 0;
            foreach (var p in Places())
            {
                order++;
                if (!p.Showing && p.Live == null) continue;
                string kindWord = KindOf(p);
                if (k != null && kindWord != k) continue;
                int score = q.Length == 0 ? 1 : Score(p, q);
                if (score > 0) scored.Add((p, score, order));
            }
            var picked = scored.OrderByDescending(s => s.Score).ThenBy(s => s.Order).Take(Math.Max(1, most)).Select(s => Describe(s.Place)).ToList();
            return new AssistantFound { Places = picked, LeftOut = Math.Max(0, scored.Count - picked.Count) };
        }

        private int Score(Place p, string q)
        {
            if (p.Entry != null && p.Entry.Page is SearchPage.Pad or SearchPage.Settings or SearchPage.Extras or SearchPage.Devices or SearchPage.Profiles)
                return _service.ScoreFor(new SearchService.Shown(p.Entry, p.Item, p.Label, p.Pad, p.Section, p.ReadValue), q);
            string words = WordsOf(p);
            if (string.IsNullOrEmpty(words)) return 0;
            string english = p.Entry == null ? null : SearchLocator.EnglishText(p.Entry.Label);
            return SearchMatcher.Score(q, words, WhereOf(p), english);
        }

        public List<AssistantPlace> All() => Places(true).Where(p => p.Showing && p.Entry?.Kind == SearchKind.Setting).Select(Describe).ToList();

        // ─────────────────────────────────────────────
        //  read
        // ─────────────────────────────────────────────

        /// <summary>The most places a read lists under one place.</summary>
        internal const int MostChildren = 400;

        public AssistantRead Read(string id) => WithDevice(id, () => ReadNow(id));

        private AssistantRead ReadNow(string id)
        {
            var all = Places();
            var p = Resolve(id, all);
            var read = new AssistantRead { Place = Describe(p), Docs = AssistantDocs.PageFor(p.Entry, p.Live) };
            var (cls, _) = ClassOf(p);
            if (cls == ReachClass.UsersOwn) return read;

            IEnumerable<Place> children = Enumerable.Empty<Place>();
            FrameworkElement element = null;
            if (p.Entry == null && p.Live == null)
            {
                // A window no markup describes: its text and buttons as it shows.
                ReadDialog(read, p);
                return Finish(read, p.Root);
            }
            if (p.Entry != null)
            {
                var e = p.Entry;
                switch (e.Kind)
                {
                    case SearchKind.Card:
                        children = all.Where(c => c.Entry != null && !ReferenceEquals(c.Entry, e) && c.Entry.Page == e.Page && c.Entry.CardTitle != null
                                                  && c.Entry.CardTitle.Equals(e.Label) && c.Entry.ExtraKey == e.ExtraKey
                                                  && ReferenceEquals(c.Item, p.Item) && ReferenceEquals(c.Pad, p.Pad) && c.Showing);
                        element = Target(p);
                        break;
                    case SearchKind.Section:
                        children = all.Where(c => c.Entry?.Page == SearchPage.Pad && c.Entry.Section == e.Section && ReferenceEquals(c.Pad, p.Pad)
                                                  && c.Entry.Kind is SearchKind.Card or SearchKind.Part && c.Showing);
                        break;
                    case SearchKind.Part:
                        children = all.Where(c => c.Entry?.Page == SearchPage.Pad && c.Entry.Tab == e.Tab && ReferenceEquals(c.Pad, p.Pad)
                                                  && c.Entry.Kind is SearchKind.Card or SearchKind.Setting or SearchKind.Action && c.Entry.CardTitle == null && c.Showing);
                        break;
                    case SearchKind.Extra:
                        children = all.Where(c => c.Entry != null && c.Entry.ExtraKey == e.ExtraKey && c.Entry.Kind != SearchKind.Extra
                                                  && c.Entry.Kind is SearchKind.Card or SearchKind.Setting or SearchKind.Action && c.Entry.CardTitle == null && c.Showing);
                        element = _pageOf(SearchPage.Extras)?.FindName(e.ExtraKey + "Page") as FrameworkElement;
                        break;
                    case SearchKind.Dialog:
                        // Its markup's entries, and the items the dialog
                        // gives the catalog while it is open, such as Find a
                        // Mapping's list.
                        children = all.Where(c => (c.Entry != null && c.Entry.Page == SearchPage.Dialog && c.Entry.Dialog == e.Dialog && c.Entry.Kind != SearchKind.Dialog)
                                                  || (c.LiveItem is { } li && li.Id.StartsWith("dialog/" + e.Dialog + "/", StringComparison.Ordinal))).ToList();
                        element = p.Root;
                        ReadDialog(read, p);
                        break;
                    default:
                        element = Target(p);
                        break;
                }
            }
            else
            {
                string prefix = p.Id + "/";
                switch (p.Live.Kind)
                {
                    case LiveKind.Page:
                    {
                        string tag = p.Live.Payload as string;
                        var page = PageOfTag(tag);
                        children = all.Where(c => (c.Entry != null && c.Entry.Page == page && c.Entry.Kind is SearchKind.Card or SearchKind.Extra or SearchKind.Section && c.Showing
                                                   && (page != SearchPage.Pad || ReferenceEquals(c.Pad, _pageOf(SearchPage.Pad)?.DataContext)))
                                                  || (c.Live?.Payload is LiveItem li && li.Id.StartsWith(tag?.ToLowerInvariant() + "/", StringComparison.Ordinal)));
                        element = page == SearchPage.Home ? _parts.CatalogPageOf?.Invoke(SearchPage.Home) : page == SearchPage.About ? _parts.CatalogPageOf?.Invoke(SearchPage.About) : _pageOf(page);
                        break;
                    }
                    case LiveKind.Controller:
                    {
                        int index = (int)p.Live.Payload;
                        var pad = index >= 0 && index < _vm.Pads.Count ? _vm.Pads[index] : null;
                        children = all.Where(c => (c.Entry?.Page == SearchPage.Pad && c.Entry.Kind == SearchKind.Section && ReferenceEquals(c.Pad, pad) && c.Showing)
                                                  || (c.Live?.Kind == LiveKind.Macro && c.Id.StartsWith("macro/c" + p.Id.Substring("controller/".Length) + "/", StringComparison.Ordinal))
                                                  || (c.Live?.Payload is LiveItem li && li.Id.StartsWith("row/c" + p.Id.Substring("controller/".Length) + "/", StringComparison.Ordinal)));
                        break;
                    }
                    case LiveKind.Device:
                    {
                        var row = p.Live.Payload as DeviceRowViewModel;
                        var devices = _vm.Devices;
                        var before = devices?.SelectedDevice;
                        if (devices != null && row != null && !ReferenceEquals(before, row))
                        {
                            devices.SelectedDevice = row;
                            try { all = Places(otherControllers: false); }
                            finally { devices.SelectedDevice = before; }
                            // The values were read while the device showed.
                        }
                        read.Children = DeviceChildren(all, row, prefix);
                        // The help its plugin gives it reads with it, as its
                        // panel shows it (#600 item 18).
                        var paragraphs = Paragraphs(all, prefix);
                        read.Lines.AddRange(paragraphs);
                        if (paragraphs.Count > 0) read.Place.Help = string.Join(" ", paragraphs);
                        return Finish(read, null);
                    }
                    case LiveKind.Plugin:
                    {
                        // A plugin as its card shows it (#600 item 18): its
                        // line, which holds its state, why it stopped, the
                        // devices it added and its signer, why its settings
                        // are missing, its help, then the devices by their
                        // own IDs and what it declared.
                        var payload = p.Live.Payload;
                        var added = PluginDevices(payload);
                        children = all.Where(c => (c.Live?.Kind == LiveKind.Device && c.Live.Payload is DeviceRowViewModel device && added.Contains(device.InstanceGuid))
                                                  || (c.Live?.Payload is LiveItem li && li.Id.StartsWith(prefix, StringComparison.Ordinal)));
                        if (read.Place.ValueText is { Length: > 0 } line) read.Lines.Add(line);
                        if (payload is PluginRowItem { Settings: { MissingLine: { Length: > 0 } missing } }) read.Lines.Add(AutomationReadScrub.Text(missing));
                        var paragraphs = Paragraphs(all, prefix);
                        read.Lines.AddRange(paragraphs);
                        if (paragraphs.Count > 0) read.Place.Help = string.Join(" ", paragraphs);
                        break;
                    }
                    default:
                        var own = p.LiveItem;
                        children = all.Where(c => c.Live?.Payload is LiveItem li
                                                  && (li.Id.StartsWith(prefix, StringComparison.Ordinal) || (own != null && own.Menu.Any(m => ReferenceEquals(m, li)))));
                        element = p.LiveItem?.Element?.Invoke();
                        break;
                }
            }
            var under = children.Take(MostChildren).ToList();
            read.Children = under.Select(Describe).ToList();
            // A list an item stands for reads through that item, with its
            // rows as the item words them.
            var standsFor = new HashSet<DependencyObject>(under.Select(c => c.LiveItem?.Element is { } find ? SafeElement(find) : null).Where(x => x != null));
            return Finish(read, element, standsFor);
        }

        private static FrameworkElement SafeElement(Func<FrameworkElement> find)
        {
            try { return find(); }
            catch (Exception) { return null; }
        }

        private List<AssistantPlace> DeviceChildren(List<Place> all, DeviceRowViewModel row, string prefix)
            => all.Where(c => (c.Entry?.Page == SearchPage.Devices && c.Showing && c.Entry.Kind is SearchKind.Setting or SearchKind.Card or SearchKind.Action)
                              || (c.Live?.Payload is LiveItem li && li.Id.StartsWith(prefix, StringComparison.Ordinal)))
                .Take(MostChildren).Select(Describe).ToList();

        private AssistantRead Finish(AssistantRead read, FrameworkElement element, ISet<DependencyObject> standsFor = null)
        {
            if (element != null && element.IsVisible) ReadLines(element, read, standsFor);
            if (read.WaitingForPerson == null) read.WaitingForPerson = AssistantWindowsDialogs.Waiting(_parts.MainWindow?.Invoke());
            return read;
        }

        private static SearchPage PageOfTag(string tag) => tag switch
        {
            "Home" => SearchPage.Home,
            "Devices" => SearchPage.Devices,
            "Profiles" => SearchPage.Profiles,
            "Extras" => SearchPage.Extras,
            "Settings" => SearchPage.Settings,
            "About" => SearchPage.About,
            _ => SearchPage.Pad,
        };

        /// <summary>An open dialog's buttons, each with its ID and class.</summary>
        private void ReadDialog(AssistantRead read, Place p)
        {
            if (p.Root == null) return;
            var open = DialogRegistry.Describe(p.Root);
            if (open == null) return;
            int i = 0;
            foreach (var button in open.Buttons)
            {
                var cls = button.Reach == ReachClass.Unset ? ReachClass.UsersOwn : button.Reach;
                read.Buttons.Add(new AssistantPlace
                {
                    Id = ButtonId(p.Id, button, i++),
                    Kind = "action",
                    Name = AutomationReadScrub.Text(button.Words),
                    Where = open.Title,
                    Enabled = button.Enabled,
                    Reach = AutomationReadScrub.ReachWord(cls),
                });
            }
        }

        /// <summary>A button of an open dialog by its ID: the dialog's ID,
        /// then the button's AutomationId, or its position.</summary>
        private static string ButtonId(string dialogId, OpenDialogButton button, int at)
            => dialogId + "#" + (string.IsNullOrEmpty(button.AutomationId) ? "button" + at.ToString(CultureInfo.InvariantCulture) : button.AutomationId);

        /// <summary>Every line of text an element shows, and its lists, each
        /// scrubbed by the class of where it sits: a line in a place that is
        /// the user's alone is not read.</summary>
        private static void ReadLines(FrameworkElement root, AssistantRead read, ISet<DependencyObject> standsFor = null)
        {
            var listParts = new HashSet<DependencyObject>();
            foreach (var d in SearchLocator.VisualDescendants(root))
            {
                if (d is ItemsControl list && list is not MenuBase && list.IsVisible && list.Items.Count > 0 && !listParts.Contains(list))
                {
                    if (Reach.Effective(list) == ReachClass.UsersOwn) continue;
                    if (standsFor != null && standsFor.Contains(list))
                    {
                        foreach (var inner in SearchLocator.VisualDescendants(list)) listParts.Add(inner);
                        continue;
                    }
                    var rows = new AssistantListRead
                    {
                        Name = AutomationReadScrub.Text(AutomationProperties.GetName(list)),
                        Selected = list is Selector s && s.SelectedIndex >= 0 ? s.SelectedIndex : null,
                    };
                    foreach (object item in list.Items)
                    {
                        if (rows.Rows.Count >= MostChildren) break;
                        string words = list.ItemContainerGenerator.ContainerFromItem(item) is DependencyObject container
                            ? string.Join(" ", SearchLocator.VisualDescendants(container).OfType<TextBlock>().Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text)).Select(t => t.Text))
                            : RowText(list, item);
                        rows.Rows.Add(AutomationReadScrub.Text(words) ?? string.Empty);
                    }
                    read.Lists.Add(rows);
                    foreach (var inner in SearchLocator.VisualDescendants(list)) listParts.Add(inner);
                    continue;
                }
                if (listParts.Contains(d)) continue;
                if (d is TextBlock text && text.IsVisible && !string.IsNullOrWhiteSpace(text.Text))
                {
                    if (Reach.Effective(text) == ReachClass.UsersOwn) continue;
                    if (read.Lines.Count < MostChildren) read.Lines.Add(AutomationReadScrub.Text(PadForge.Common.BidiText.GetText(text) ?? text.Text));
                }
            }
        }

        // ─────────────────────────────────────────────
        //  set
        // ─────────────────────────────────────────────

        private static void RefuseUsersOwn(ReachClass cls)
        {
            if (cls == ReachClass.UsersOwn)
                throw new AssistantRefusal("users-own", "Only the user can change this, in PadForge. show takes the user to it.");
        }

        public AssistantAsk AskFor(string id, JsonElement value) => WithDevice(id, () =>
        {
            var p = Resolve(id);
            var (cls, why) = ClassOf(p);
            RefuseUsersOwn(cls);
            if (cls != ReachClass.Asks) return null;
            // An extra that listens on the network asks when it is turned on,
            // and only when turning it on starts a listener (#600): the Head
            // Tracking switch can bring back inputs that listen on nothing.
            // A setting that lets the foreground watcher put a profile in
            // force names each profile that would turn one on, off now, and
            // asks nothing when none would (AssistantScan.NetworkLines).
            IReadOnlyList<string> lines = null;
            if (why == ReachReason.Network)
            {
                object data = p.Entry == null ? null : p.Item ?? RootOf(p);
                if (value.ValueKind != JsonValueKind.True || !AssistantScan.StartsAListener(p.Entry?.Value?.Path, data)) return null;
                lines = AssistantScan.NetworkLines(p.Entry?.Value?.Path, data, _vm);
                if (lines is { Count: 0 }) return null;
            }
            string name = WordsOf(p);
            string valueText = value.ValueKind switch
            {
                JsonValueKind.True => PadForge.Resources.Strings.Strings.Instance.Common_On,
                JsonValueKind.False => PadForge.Resources.Strings.Strings.Instance.Common_Off,
                JsonValueKind.String => value.GetString(),
                _ => value.ToString(),
            };
            var detail = new List<string> { AssistantToolWords.SetsTo(name, valueText) };
            if (lines != null) detail.AddRange(lines);
            detail.Add(AssistantToolWords.Reason(why));
            return new AssistantAsk(AssistantToolWords.AskSetting(name), detail);
        });

        public AssistantPlace Set(string id, JsonElement value) => WithDevice(id, () =>
        {
            var p = Resolve(id);
            var (cls, _) = ClassOf(p);
            RefuseUsersOwn(cls);
            if (!p.Showing)
                throw new AssistantRefusal("not-available", "The page does not show this now, for that controller or device, so it cannot be set. read says so with showing false.");
            if (p.LiveItem is { } item)
            {
                if (item.Write == null) throw new AssistantRefusal("not-available", "This takes no value from a program. press presses it.");
                if (Target(p) is { IsEnabled: false })
                    throw new AssistantRefusal("not-available", "The page has this turned off for now. show takes the user to it, where the reason shows.");
                // A value the item does not take is refused with its own
                // words, such as a plugin setting's range.
                try { item.Write(TextOf(value)); }
                catch (ArgumentException refused) { throw new AssistantRefusal("out-of-range", refused.Message); }
                return Describe(Resolve(id));
            }
            if (p.Entry == null)
                throw new AssistantRefusal("not-available", "This is read and shown, not set. Its own tool changes it.");
            switch (p.Entry.Kind)
            {
                case SearchKind.Section:
                case SearchKind.Part:
                case SearchKind.Extra:
                    if (value.ValueKind != JsonValueKind.True)
                        throw new AssistantRefusal("bad-argument", "A section, part or extra takes true, which opens it on its page.");
                    _navigator.Open(new SearchResult { Target = SearchTarget.Entry, Entry = p.Entry, Pad = p.Pad }, takeFocus: false);
                    return Describe(Resolve(id));
                case SearchKind.Setting:
                    break;
                default:
                    throw new AssistantRefusal("not-available", "This is pressed, not set. press presses it.");
            }
            WriteSetting(p, value);
            return Describe(Resolve(id));
        });

        private static string TextOf(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString(),
        };

        private void WriteSetting(Place p, JsonElement value)
        {
            var entry = p.Entry;
            var page = _pageOf(SearchPage.Pad);
            object bound = page?.DataContext;
            bool rebind = entry.Page == SearchPage.Pad && !ReferenceEquals(bound, p.Pad);
            if (rebind)
            {
                if (_bindPad != null) _bindPad(p.Pad);
                else page.DataContext = p.Pad;
            }
            try
            {
                var locator = entry.Page is SearchPage.Pad or SearchPage.Settings or SearchPage.Extras or SearchPage.Devices or SearchPage.Profiles
                    ? _service.Locator : new SearchLocator(_ => p.Root);
                locator.Reset();
                var label = entry.ItemsPath == null && !entry.RowOptions
                    ? (ReferenceEquals(locator, _service.Locator) ? locator.Find(entry, _service.Index) : p.Label)
                    : p.Label;
                var row = label == null ? null : SearchLocator.Up(label) as FrameworkElement;
                var data = entry.ItemsPath != null ? p.Item : row?.DataContext ?? DataOf(p);
                if (entry.Value?.Path != null)
                {
                    var input = InputOf(row, entry);
                    if (input != null && !input.IsEnabled)
                        throw new AssistantRefusal("not-available", "The page has this turned off for now. show takes the user to it, where the reason shows.");
                    switch (entry.Value.Kind)
                    {
                        case SearchValueKind.Number: WriteNumber(entry, row, data, value); break;
                        case SearchValueKind.Switch: WriteSwitch(entry, row, data, value); break;
                        default: WriteChoice(row, value); break;
                    }
                    return;
                }
                WriteRowControl(row, value);
            }
            finally
            {
                if (rebind && page != null && !ReferenceEquals(page.DataContext, bound))
                {
                    if (_bindPad != null && bound is PadViewModel back) _bindPad(back);
                    else page.DataContext = bound;
                }
                _service.Locator.Reset();
            }
        }

        /// <summary>A row whose value the index does not read: its text box,
        /// list or buttons of a choice, as a person types or picks.</summary>
        private static void WriteRowControl(FrameworkElement row, JsonElement value)
        {
            if (row == null) throw new AssistantRefusal("not-available", "The page does not show this now, so it cannot be set.");
            var parts = SearchLocator.LogicalDescendants(row).OfType<FrameworkElement>().Where(e => e.IsVisible).ToList();
            if (parts.OfType<TextBox>().FirstOrDefault(t => !t.IsReadOnly) is { } box)
            {
                if (!box.IsEnabled) throw new AssistantRefusal("not-available", "The page has this box turned off for now.");
                if (value.ValueKind != JsonValueKind.String) throw new AssistantRefusal("bad-argument", "This takes text.");
                string text = value.GetString() ?? string.Empty;
                if (box.MaxLength > 0 && text.Length > box.MaxLength)
                    throw new AssistantRefusal("out-of-range", "This takes at most " + box.MaxLength + " characters.");
                box.Text = text;
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                return;
            }
            if (parts.OfType<ComboBox>().FirstOrDefault() is { } combo)
            {
                WriteChoice(row, value);
                return;
            }
            if (parts.OfType<ListBox>().FirstOrDefault() is { } list)
            {
                if (!list.IsEnabled) throw new AssistantRefusal("not-available", "The page has this list turned off for now.");
                var rows = list.Items.Cast<object>().Select(i => ItemText(list, i)).ToList();
                int at = PickOf(rows, value);
                list.SelectedIndex = at;
                foreach (var property in new[] { Selector.SelectedItemProperty, Selector.SelectedValueProperty, Selector.SelectedIndexProperty })
                    list.GetBindingExpression(property)?.UpdateSource();
                return;
            }
            if (parts.OfType<RadioButton>().ToList() is { Count: > 1 } radios)
            {
                var words = radios.Select(r => r.Content as string ?? AutomationProperties.GetName(r) ?? string.Empty).ToList();
                int at = PickOf(words, value);
                if (!radios[at].IsEnabled) throw new AssistantRefusal("not-available", "The page has this choice turned off for now.");
                radios[at].IsChecked = true;
                return;
            }
            throw new AssistantRefusal("not-available", "This has no value PadForge can write for a program. show takes the user to it.");
        }

        private static int PickOf(List<string> choices, JsonElement value)
        {
            int at = -1;
            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString()?.Trim() ?? string.Empty;
                at = choices.FindIndex(c => string.Equals(c, text, StringComparison.CurrentCultureIgnoreCase));
                if (at < 0) at = choices.FindIndex(c => string.Equals(SearchMatcher.FoldQuotes(c), SearchMatcher.FoldQuotes(text), StringComparison.CurrentCultureIgnoreCase));
            }
            else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int index) && index >= 0 && index < choices.Count)
                at = index;
            if (at < 0)
                throw new AssistantRefusal("out-of-range", "This takes one of: " + string.Join(", ", choices.Select(c => "\"" + c + "\"")) + ".");
            return at;
        }

        private void WriteNumber(SearchEntry entry, FrameworkElement row, object data, JsonElement value)
        {
            double number;
            if (value.ValueKind == JsonValueKind.Number) number = value.GetDouble();
            else if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) number = parsed;
            else throw new AssistantRefusal("bad-argument", "This setting takes a number.");
            var (min, max) = RangeOf(row, entry);
            if ((min != null && number < min.Value) || (max != null && number > max.Value))
                throw new AssistantRefusal("out-of-range", "This setting takes " + Num(min) + " to " + Num(max)
                    + (entry.Value.UnitKey != null ? " " + PadForge.Resources.Strings.Strings.Get(entry.Value.UnitKey) : entry.Value.UnitText != null ? " " + entry.Value.UnitText : string.Empty)
                    + ". " + Num(number) + " is outside it.");

            var numberBox = row == null ? null : SearchLocator.LogicalDescendants(row).OfType<Wpf.Ui.Controls.NumberBox>()
                .FirstOrDefault(n => BindingOperations.GetBinding(n, Wpf.Ui.Controls.NumberBox.ValueProperty)?.Path?.Path == entry.Value.Path);
            if (numberBox != null)
            {
                numberBox.Value = number;
                numberBox.GetBindingExpression(Wpf.Ui.Controls.NumberBox.ValueProperty)?.UpdateSource();
                return;
            }
            var box = row == null ? null : SearchLocator.LogicalDescendants(row).OfType<TextBox>()
                .FirstOrDefault(t => BindingOperations.GetBinding(t, TextBox.TextProperty)?.Path?.Path == entry.Value.Path);
            if (box != null)
            {
                var culture = box.Language?.GetSpecificCulture() ?? CultureInfo.CurrentCulture;
                box.Text = number.ToString("R", culture);
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                return;
            }
            var slider = row == null ? null : SearchLocator.LogicalDescendants(row).OfType<RangeBase>()
                .FirstOrDefault(r => BindingOperations.GetBinding(r, RangeBase.ValueProperty)?.Path?.Path == entry.Value.Path);
            if (slider != null)
            {
                slider.Value = number;
                slider.GetBindingExpression(RangeBase.ValueProperty)?.UpdateSource();
                return;
            }
            Through(entry, data, number.ToString("R", CultureInfo.InvariantCulture));
        }

        private static string Num(double? n) => n == null ? "?" : n.Value.ToString("0.###", CultureInfo.InvariantCulture);

        private void WriteSwitch(SearchEntry entry, FrameworkElement row, object data, JsonElement value)
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new AssistantRefusal("bad-argument", "This setting is a switch: it takes true or false.");
            bool on = value.ValueKind == JsonValueKind.True;
            var toggle = row == null ? null : SearchLocator.LogicalDescendants(row).OfType<ToggleButton>()
                .FirstOrDefault(t => BindingOperations.GetBinding(t, ToggleButton.IsCheckedProperty)?.Path?.Path == entry.Value.Path);
            if (toggle != null)
            {
                toggle.IsChecked = on;
                toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
                return;
            }
            Through(entry, data, on);
        }

        private static void WriteChoice(FrameworkElement row, JsonElement value)
        {
            var combo = row == null ? null : SearchLocator.LogicalDescendants(row).OfType<ComboBox>().FirstOrDefault();
            if (combo == null) throw new AssistantRefusal("not-available", "The page does not show this picker now, so it cannot be set.");
            int at = PickOf(Choices(combo), value);
            if (!combo.IsEnabled)
                throw new AssistantRefusal("not-available", "The page has this picker turned off for now. show takes the user to it.");
            combo.SelectedIndex = at;
            foreach (var property in new[] { Selector.SelectedItemProperty, Selector.SelectedValueProperty, Selector.SelectedIndexProperty })
                combo.GetBindingExpression(property)?.UpdateSource();
        }

        private void Through(SearchEntry entry, object data, object value)
        {
            if (data == null) throw new AssistantRefusal("not-available", "The page does not show this setting now, so it cannot be set.");
            var converter = entry.Value.ConverterKey == null ? null : _pageOf(entry.Page)?.TryFindResource(entry.Value.ConverterKey) as IValueConverter;
            BindingProbe.Write(data, entry.Value.Path, value, converter);
        }

        // ─────────────────────────────────────────────
        //  press
        // ─────────────────────────────────────────────

        public AssistantPressPlan PlanPress(string id)
        {
            string want = id?.Trim() ?? string.Empty;
            // A button of an open dialog: the dialog's ID, # and the button.
            int hash = want.LastIndexOf('#');
            if (hash > 0 && IsDialogId(want.Substring(0, hash)))
                return PlanDialogButton(want.Substring(0, hash), want.Substring(hash + 1), want);

            var p = Resolve(want);
            if (p.LiveItem is { } item)
            {
                if (item.Press == null) throw new AssistantRefusal("not-available", "This is read or set, not pressed.");
                return Plan(p, ClassOf(p), item.Press, item.Element?.Invoke());
            }
            if (p.Entry == null)
                throw new AssistantRefusal("not-available", "This is read and shown, not pressed. Its own tool acts on it.");
            if (p.Entry.Page == SearchPage.Window && p.Entry.ScopeName == "Tray")
                return PlanTray(p);
            if (p.Entry.Kind == SearchKind.MenuItem)
                return PlanMenuItem(p);
            if (p.Entry.Kind != SearchKind.Action)
                throw new AssistantRefusal("not-available", "This is not pressed. set sets a setting, and show takes the user to a card or section.");
            if (!p.Showing) throw new AssistantRefusal("not-available", "The page does not show this button now, so it cannot be pressed.");
            var target = Target(p);
            if (target is not UIElement element)
                throw new AssistantRefusal("not-available", "The page does not show this button now, so it cannot be pressed.");
            return Plan(p, ClassOf(p, target), () => Invoke(element), target);
        }

        /// <summary>Whether an ID is a dialog's: a dialog's catalog entry, or
        /// a window no markup describes.</summary>
        private static bool IsDialogId(string id)
            => id.StartsWith("dialog.dialog/", StringComparison.Ordinal) || id.StartsWith("open/", StringComparison.Ordinal);

        /// <param name="does">What the press would make PadForge do, one line
        /// each: a press with lines asks first, whatever its class, and the
        /// card lists them after its reason, each once.</param>
        private AssistantPressPlan Plan(Place p, (ReachClass Class, ReachReason Why) reach, Action press, FrameworkElement target,
            IReadOnlyList<string> does = null)
        {
            RefuseUsersOwn(reach.Class);
            if (target != null && !target.IsEnabled)
                throw new AssistantRefusal("not-available", "The page has this turned off for now. show takes the user to it, where the reason shows.");
            var place = Describe(p);
            var detail = new List<string>();
            if (reach.Class == ReachClass.Asks) detail.Add(AssistantToolWords.Reason(reach.Why));
            foreach (string line in does ?? Array.Empty<string>())
                if (!detail.Contains(line)) detail.Add(line);
            AssistantAsk ask = detail.Count > 0 ? new AssistantAsk(AssistantToolWords.AskPress(place.Name), detail) : null;
            return new AssistantPressPlan { Place = place, Ask = ask, Press = press };
        }

        private AssistantPressPlan PlanTray(Place p)
        {
            string key = p.Entry.AutomationId?.StartsWith("Tray_", StringComparison.Ordinal) == true ? p.Entry.AutomationId.Substring(5) : null;
            // A program never brings PadForge's window forward (#438).
            if (key == "Show")
                throw new AssistantRefusal("users-own", "Only the user brings PadForge's window forward. show asks the user, with Show on PadForge's question card.");
            if (key == null || _parts.PressTray == null)
                throw new AssistantRefusal("not-available", "PadForge cannot press this tray item here.");
            return Plan(p, ClassOf(p), () => _parts.PressTray(key), null);
        }

        /// <summary>A menu item of a button's menu: the menu's own item,
        /// pressed through its peer while the menu stays shut.</summary>
        private AssistantPressPlan PlanMenuItem(Place p)
        {
            FrameworkElement root = p.Entry.Page is SearchPage.Pad or SearchPage.Settings or SearchPage.Extras or SearchPage.Devices or SearchPage.Profiles
                ? _pageOf(p.Entry.Page) : p.Root ?? _parts.CatalogPageOf?.Invoke(p.Entry.Page);
            if (NamesNoItem(p)) throw new AssistantRefusal("not-available", EachOwnWords);
            var (owner, found) = MenuOf(p, root);
            if (found == null) throw new AssistantRefusal("not-available", "The page does not show the button whose menu holds this now.");
            // A controller page's menu acts on the controller the page
            // shows, as a button's press does. A card's or a tab's own menu
            // acts on that card or tab: its button's data, as the card's
            // More hands it (ProfilesPage.CardMore_Click).
            object data = null;
            if (p.Entry.Page == SearchPage.Pad)
            {
                if (!p.Showing || (p.Pad != null && !ReferenceEquals(root.DataContext, p.Pad)))
                    throw new AssistantRefusal("not-available", "The page does not show this button now, so it cannot be pressed.");
                data = root.DataContext;
            }
            if (p.Entry.ItemsList != null) data = owner.DataContext;
            if (data != null) HandOver(owner, data);
            var reach = ClassOf(p, owner);
            var (itemClass, itemWhy) = Reach.Of(found);
            if (itemClass > reach.Class) reach = (itemClass, itemWhy);
            if (!found.IsEnabled) throw new AssistantRefusal("not-available", "The menu has this item turned off for now.");
            var item = found;
            // A layer tab's Paste replaces the layer's rows with the rows a
            // layer's Copy kept (#600). Its card names the keys they would
            // press on a keyboard and mouse controller, as a controller's
            // Paste names its own, and the press lands the rows the card
            // read (PadPage.PasteLayer).
            if (p.Entry.Handler == "ShiftLayer_Paste_Click" && root?.DataContext is PadViewModel layerPad && owner.Tag is string mask)
            {
                var rows = PadPage.LayerClipboard
                    ?? throw new AssistantRefusal("not-available", "No layer's rows are copied. A layer tab's Copy keeps them first.");
                var keys = layerPad.OutputType == PadForge.Engine.VirtualControllerType.KeyboardMouse ? RowKeyLines(rows, layerPad) : null;
                return Plan(p, reach, () => PadPage.PasteLayer(layerPad, mask, rows), owner, keys);
            }
            // A Paste lands the text its card read: another program can
            // change the clipboard while the card waits, so the press hands
            // that text to the paste, which then reads the clipboard no more
            // (MainWindow.OnPasteSettings, OnPasteMacro).
            if (IsPaste(p.Entry.Handler) && data is PadViewModel pad)
            {
                string scanned = ClipboardText();
                bool settings = p.Entry.Handler == "PasteSettingsCommand";
                return Plan(p, reach, () =>
                {
                    var command = settings ? pad.PasteSettingsCommand : pad.PasteMacroCommand;
                    if (!command.CanExecute(null)) throw new AssistantRefusal("not-available", "The menu has this item turned off for now.");
                    if (settings) pad.PasteSettingsText(scanned);
                    else pad.PasteMacroText(scanned);
                }, owner, PasteLines(p.Entry.Handler, scanned, pad));
            }
            return Plan(p, reach, () =>
            {
                if (data != null) HandOver(owner, data);
                Invoke(item);
            }, owner);
        }

        /// <summary>
        /// Gives a button's menu what a person's opening of it gives it: the
        /// controller page's More menus the page's controller
        /// (PadPage.MoreMenu_Click), a card's or a tab's own menu its
        /// button's data (ProfilesPage.CardMore_Click, and a layer tab's
        /// right-click), and each the button as where it opens, which a
        /// layer tab's items read their layer from. A menu no one opened has
        /// neither, so its items' commands were unbound and a press did
        /// nothing. One opened on another controller kept that controller.
        /// </summary>
        private static void HandOver(FrameworkElement owner, object data)
        {
            if (owner.ContextMenu is not { } menu) return;
            if (!ReferenceEquals(menu.DataContext, data)) menu.DataContext = data;
            if (!ReferenceEquals(menu.PlacementTarget, owner)) menu.PlacementTarget = owner;
        }

        /// <summary>What a Paste would make PadForge do, read from what the
        /// clipboard holds (#600 item 2's indirect route). A controller's
        /// settings are the profile import they amount to, and they carry
        /// their macros. A macro is pasted whole. Then one line for each
        /// program, key, text and mouse input, as save_macro's card says
        /// them. Onto a keyboard and mouse controller, the keys after them
        /// (<see cref="KeyLines"/>), so the card's first lines keep the
        /// macros' whatever the table's length. Empty for any other press,
        /// and for a clipboard the paste would turn down.</summary>
        private static List<string> PasteLines(string handler, string text, PadViewModel target)
        {
            var lines = new List<string>();
            if (!IsPaste(handler) || string.IsNullOrWhiteSpace(text)) return lines;
            string macros = text;
            List<string> keys = null;
            if (handler == "PasteSettingsCommand")
            {
                PadForge.Engine.Data.PadSetting settings;
                PadForge.Engine.VirtualControllerType sourceType = default;
                bool sourceIsExtended = false;
                try { settings = PadForge.Engine.Data.PadSetting.FromJson(text, out sourceType, out sourceIsExtended); }
                catch (Exception) { settings = null; }
                if (settings == null) return lines;
                lines.Add(AssistantToolWords.Reason(ReachReason.ImportsProfile));
                macros = settings.SlotMacrosJson;
                if (target?.OutputType == PadForge.Engine.VirtualControllerType.KeyboardMouse)
                    keys = KeyLines(settings, sourceType, sourceIsExtended, target);
            }
            if (!string.IsNullOrEmpty(macros) && PadForge.Services.SettingsService.TryParseMacroClipboard(macros) is { Macros: { } list })
            {
                foreach (var macro in list)
                    foreach (string line in AssistantScan.Macro(macro))
                        if (!lines.Contains(line)) lines.Add(line);
            }
            foreach (string line in keys ?? new List<string>())
                if (!lines.Contains(line)) lines.Add(line);
            return lines;
        }

        /// <summary>What a controller's settings pasted onto a keyboard and
        /// mouse controller would press: the keyboard line every other tool
        /// gives such a controller (add_controller, set_controller_type,
        /// assign_device, auto_map), whatever the settings hold, then each
        /// key a pasted row maps, as set_mappings' card names one, by the
        /// key's own name on the controller's page and the row's input. The
        /// rows are the ones the paste would land
        /// (<see cref="InputService.PastedRows"/>).</summary>
        private static List<string> KeyLines(PadForge.Engine.Data.PadSetting settings,
            PadForge.Engine.VirtualControllerType sourceType, bool sourceIsExtended, PadViewModel target)
        {
            List<PadForge.Engine.Data.MappingRow> rows;
            try { rows = InputService.PastedRows(settings, sourceType, sourceIsExtended, InputService.SlotLayoutOf(target), target.PadIndex); }
            catch (Exception) { return new List<string> { AssistantToolWords.KeyboardMouseLine() }; }
            return RowKeyLines(rows, target);
        }

        /// <summary>What rows landed on a keyboard and mouse controller would
        /// press: the keyboard line, then each key a row with a source maps,
        /// by the key's own name on the controller's page and the row's
        /// input, as set_mappings' card names one.</summary>
        private static List<string> RowKeyLines(IEnumerable<PadForge.Engine.Data.MappingRow> rows, PadViewModel target)
        {
            var lines = new List<string> { AssistantToolWords.KeyboardMouseLine() };
            foreach (var row in rows)
            {
                if (row == null || !InputService.HasSource(row)) continue;
                string key = target.Mappings.FirstOrDefault(m => m.TargetSettingName == row.Target)?.TargetLabel ?? row.Target;
                string input = row.Sources.FirstOrDefault(s => !string.IsNullOrEmpty(s?.Descriptor))?.Descriptor ?? "?";
                string line = AssistantToolWords.KeyLine(key, input);
                if (!lines.Contains(line)) lines.Add(line);
            }
            return lines;
        }

        /// <summary>Whether a menu item's command is a Paste: a controller's
        /// settings or a macro, from the clipboard.</summary>
        private static bool IsPaste(string handler) => handler is "PasteSettingsCommand" or "PasteMacroCommand";

        private static string ClipboardText()
        {
            try { return ReadClipboard?.Invoke(); }
            catch (Exception) { return null; }
        }

        /// <summary>Reads the clipboard's text for a paste's question,
        /// replaceable in tests.</summary>
        internal static Func<string> ReadClipboard = () =>
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (System.Runtime.InteropServices.COMException) { return null; }
        };

        private AssistantPressPlan PlanDialogButton(string dialogId, string buttonKey, string full)
        {
            var open = OpenDialogs();
            var all = Places();
            var dialogPlace = all.FirstOrDefault(c => c.Id == dialogId && c.Entry?.Kind == SearchKind.Dialog);
            var dialog = dialogPlace?.Root != null
                ? open.FirstOrDefault(d => ReferenceEquals(d.Element, dialogPlace.Root))
                : open.FirstOrDefault(d => "open/" + d.Name == dialogId);
            if (dialog == null) throw new AssistantRefusal("not-available", "That dialog is not open. where lists the dialogs open now.");
            int i = 0;
            foreach (var button in dialog.Buttons)
            {
                string id = ButtonId(dialogId, button, i++);
                if (id != full) continue;
                var cls = button.Reach == ReachClass.Unset ? ReachClass.UsersOwn : button.Reach;
                RefuseUsersOwn(cls);
                if (!button.Enabled) throw new AssistantRefusal("not-available", "The dialog has this button turned off for now.");
                var element = button.Element;
                var place = new AssistantPlace { Id = full, Kind = "action", Name = AutomationReadScrub.Text(button.Words), Where = dialog.Title, Reach = AutomationReadScrub.ReachWord(cls) };
                // A button that brings a profile into force names each
                // extra it would turn on that listens on the network, as the
                // profile switch's card does (AutomationCommands.SwitchAsked),
                // and goes only on what the card named. Its reader gives the
                // card's lines (Reach.Starts). A button that asks for the
                // network's sake asks only when its reader names something,
                // as a setting's switch does (AskFor), and the network's line
                // comes after what turns on.
                var starts = element == null ? null : Reach.GetStarts(element);
                var named = Starting(starts);
                bool network = cls == ReachClass.Asks && button.Why == ReachReason.Network;
                var detail = new List<string>();
                if (cls == ReachClass.Asks && !network) detail.Add(AssistantToolWords.Reason(button.Why));
                detail.AddRange(named);
                if (named.Count > 0 || (network && starts == null)) detail.Add(AssistantToolWords.ListensOnNetwork());
                Action check = starts == null ? null : () =>
                {
                    if (Starting(starts).Any(line => !named.Contains(line)))
                        throw new AssistantRefusal("not-available", "By now the press would turn on more than PadForge asked about. Press again to ask about what it would turn on now.");
                };
                return new AssistantPressPlan
                {
                    Place = place,
                    Ask = detail.Count > 0 ? new AssistantAsk(AssistantToolWords.AskPress(place.Name), detail) : null,
                    Press = () =>
                    {
                        check?.Invoke();
                        Invoke(element);
                    },
                    Check = check,
                };
            }
            throw new AssistantRefusal("unknown-setting", "That dialog has no button " + buttonKey + ". read of the dialog gives its buttons.");
        }

        /// <summary>What a press would turn on that listens on the network,
        /// as the element's <see cref="Reach.StartsProperty"/> reads it now:
        /// each of the card's lines once, none when it names nothing or
        /// cannot be read.</summary>
        private static List<string> Starting(Func<IReadOnlyList<string>> starts)
        {
            if (starts == null) return new List<string>();
            try { return (starts() ?? Array.Empty<string>()).Where(line => !string.IsNullOrEmpty(line)).Distinct().ToList(); }
            catch (Exception) { return new List<string>(); }
        }

        /// <summary>A press through the element's own automation peer, as a
        /// screen reader's press goes: a button's Click, a menu item's
        /// Click, a toggle's switch.</summary>
        internal static void Invoke(UIElement element)
        {
            var peer = UIElementAutomationPeer.CreatePeerForElement(element);
            if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) { invoke.Invoke(); return; }
            if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) { toggle.Toggle(); return; }
            if (element is ButtonBase button)
            {
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                if (button.Command is { } command && command.CanExecute(button.CommandParameter)) command.Execute(button.CommandParameter);
                return;
            }
            throw new AssistantRefusal("not-available", "This cannot be pressed by a program.");
        }

        // ─────────────────────────────────────────────
        //  where, dialogs
        // ─────────────────────────────────────────────

        /// <summary>PadForge's dialogs that show now: windows owned by the
        /// main window, in-window dialogs and open menus. The main window,
        /// the flyouts, the question card and the callout are no
        /// dialogs.</summary>
        private List<OpenDialog> OpenDialogs()
        {
            var main = _parts.MainWindow?.Invoke();
            var list = new List<OpenDialog>();
            foreach (var element in DialogRegistry.Snapshot())
            {
                if (ReferenceEquals(element, main)) continue;
                // A window with no owner is a flyout or the question card,
                // unless it holds the window as a modal dialog does.
                if (element is Window window && window.Owner == null
                    && !(System.Windows.Interop.ComponentDispatcher.IsThreadModal && window.IsActive)) continue;
                if (element is PadForge.Views.AutomationRequestCard) continue;
                var described = DialogRegistry.Describe(element);
                if (described != null) list.Add(described);
            }
            return list;
        }

        public AssistantDialogs Dialogs()
        {
            EnsureIndex();
            var result = new AssistantDialogs { WaitingForPerson = AssistantWindowsDialogs.Waiting(_parts.MainWindow?.Invoke()) };
            foreach (var d in OpenDialogs())
            {
                var entry = _service.Dialogs.FirstOrDefault(e => e.Kind == SearchKind.Dialog && e.Dialog == d.Name);
                result.Open.Add(new AssistantOpenDialog
                {
                    Id = entry != null ? _service.IdOf(entry) : "open/" + d.Name,
                    Title = AutomationReadScrub.Text(d.Title),
                    Modal = d.IsModal,
                    Reach = AutomationReadScrub.ReachWord(d.Reach == ReachClass.Unset ? ReachClass.UsersOwn : d.Reach),
                });
            }
            return result;
        }

        public AssistantWhere Where()
        {
            var dialogs = Dialogs();
            var where = new AssistantWhere
            {
                Window = _windowPlace(),
                Dialogs = dialogs.Open,
                WaitingForPerson = dialogs.WaitingForPerson,
                Device = _vm.Devices?.SelectedDevice is { } row ? AutomationDevices.IdOf(row.InstanceGuid) : null,
                Profile = SettingsManager.ActiveProfileId ?? _vm.Settings?.ProfileItems?.FirstOrDefault(p => p.IsDefault)?.Id,
                ProfileSelected = _vm.Settings?.SelectedProfile?.Id,
                Callout = _parts.Pointer?.Now(),
            };
            string tag = _parts.NavTag?.Invoke() ?? _vm.SelectedNavTag;
            if (tag != null && tag.StartsWith("Pad", StringComparison.Ordinal) && int.TryParse(tag.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int one))
            {
                int index = one - 1;
                var pad = index >= 0 && index < _vm.Pads.Count ? _vm.Pads[index] : null;
                where.Controller = NumberOf(pad);
                where.Page = "controller/" + where.Controller?.ToString(CultureInfo.InvariantCulture);
                where.PageName = SearchService.SlotName(pad);
                if (_pageOf(SearchPage.Pad) is PadPage padPage && pad != null)
                    where.Section = padPage.SectionName(PadViewModel.SectionOfTab(pad.SelectedConfigTab));
            }
            else if (tag != null)
            {
                where.Page = "page/" + tag;
                where.PageName = PageName(PageOfTag(tag));
                if (tag == "Extras" && _pageOf(SearchPage.Extras) is ExtrasPage extras && extras.CurrentExtra is { } key)
                {
                    var tile = _service.Index.FirstOrDefault(e => e.Kind == SearchKind.Extra && e.ExtraKey == key);
                    where.Section = tile == null ? key : SearchLocator.LabelText(tile.Label, null);
                }
            }
            return where;
        }

        // ─────────────────────────────────────────────
        //  show
        // ─────────────────────────────────────────────

        /// <summary>The words a show and a walk refuse a place with that the
        /// page does not show now (#600 item 7), the first build's.</summary>
        internal const string NotShownWords = "The page does not show this setting now, for that controller or device.";

        public AssistantPlace CheckShow(AssistantShowRequest request) => CheckShow(request, out _);

        /// <summary>Every step checked, read for the controller or device its
        /// ID names as read reads it, so a place on another device's panel
        /// is judged by that device. <paramref name="all"/> is the catalog
        /// read for the device the page shows now, for the show to
        /// use.</summary>
        private AssistantPlace CheckShow(AssistantShowRequest request, out List<Place> all)
        {
            List<Place> shown = null;
            AssistantPlace last = null;
            foreach (var s in request.Steps)
            {
                string id = s.Place;
                last = NamesAnotherDevice(id)
                    ? WithDevice(id, () => Checked(id, Places()))
                    : Checked(id, shown ??= Places());
            }
            all = shown ?? Places();
            return last;
        }

        /// <summary>Whether a Devices-page ID names a device other than the
        /// one the page shows now.</summary>
        private bool NamesAnotherDevice(string id)
        {
            if (!SearchIds.TryRead(id?.Trim(), out _, out _, out _, out string device) || device == null) return false;
            return _vm.Devices?.SelectedDevice is not { } row || AutomationDevices.IdOf(row.InstanceGuid) != device;
        }

        private AssistantPlace Checked(string id, List<Place> all)
        {
            var p = Resolve(id, all);
            var (cls, _) = ClassOf(p);
            if (cls == ReachClass.UsersOwn)
                throw new AssistantRefusal("users-own", "A place that is the user's alone is not pointed at by a program. Tell the user where it is in words.");
            if (NamesNoItem(p)) throw new AssistantRefusal("not-available", EachOwnWords);
            if (!p.Showing) throw new AssistantRefusal("not-available", NotShownWords);
            if (p.LiveItem is { Kind: LiveKind.PluginDeclaration } && HolderOf(p, all) == null)
                throw new AssistantRefusal("not-available", NotShownWords);
            return Describe(p);
        }

        /// <summary>The device row a Devices-page ID names after its
        /// <c>@d</c>, or null.</summary>
        private DeviceRowViewModel DeviceRowOf(string id)
        {
            if (!SearchIds.TryRead(id?.Trim(), out _, out _, out _, out string device) || device == null) return null;
            return _vm.Devices?.Devices?.FirstOrDefault(r => AutomationDevices.IdOf(r.InstanceGuid) == device);
        }

        public AssistantShown Show(AssistantShowRequest request)
        {
            var pointer = _parts.Pointer ?? throw new AssistantRefusal("not-available", "PadForge cannot point here.");
            if (request.Steps.Count == 0)
            {
                bool had = pointer.Clear();
                return new AssistantShown { Shown = false, Window = _windowPlace(), Message = had ? "The callout is gone." : "Nothing was pointed at." };
            }
            // Nothing plays until every step is checked.
            CheckShow(request, out var all);
            var steps = new List<AssistantPointer.Step>();
            for (int i = 0; i < request.Steps.Count; i++)
            {
                var s = request.Steps[i];
                var p = Resolve(s.Place, all);
                // A walk rings each click on the way to its last place, and
                // the user makes them: that step opens nothing.
                bool walked = request.Walk && i == request.Steps.Count - 1;
                if (walked) steps.AddRange(WalkTo(p));
                steps.Add(StepOf(p, s.Words, s.Advance, s.Seconds, walked));
            }
            pointer.Play(request.Program, steps, request.Owner);
            var first = Resolve(request.Steps[0].Place, all);
            return new AssistantShown
            {
                Shown = true,
                Where = WhereOf(first),
                Window = _windowPlace(),
                Steps = steps.Count,
                Message = steps.Count > 1
                    ? "PadForge shows the first of " + steps.Count + " steps. The user does the clicking."
                    : "PadForge shows it now, with your words beside it.",
            };
        }

        public bool NextCall(int owner) => _parts.Pointer?.NextCall(owner) ?? false;

        /// <summary>One step: how to bring its place up, and how to find its
        /// element once it is up. A walk's last step brings nothing up: the
        /// steps before it ring every click on the way, and the user makes
        /// each one.</summary>
        private AssistantPointer.Step StepOf(Place p, string words, AssistantAdvance advance, int seconds, bool walked = false)
        {
            string id = p.Id;
            return new AssistantPointer.Step
            {
                Words = words,
                Advance = advance,
                Seconds = seconds,
                Open = walked ? null : () => OpenPlace(id),
                Find = () => ElementOf(id),
            };
        }

        /// <summary>Takes the page to a place for a plain show, as Search's
        /// landing does, without the keyboard's focus and without the ring,
        /// which the pointer draws and holds itself: the page, the
        /// controller, its section and part, the More rows and fold cards on
        /// the way, a device's panel with the filters that hide its row
        /// emptied. A walk never calls it.</summary>
        private void OpenPlace(string id)
        {
            // A place on another device's panel: that device's panel opens
            // first, as Search's landing on a device opens it.
            if (NamesAnotherDevice(id) && DeviceRowOf(id) is { } row)
                _navigator.Open(new SearchResult { Target = SearchTarget.Device, Payload = row }, takeFocus: false);
            Place p;
            try { p = Resolve(id); }
            catch (AssistantRefusal) { return; }
            if (p.Entry != null && p.Entry.Page is SearchPage.Pad or SearchPage.Settings or SearchPage.Extras or SearchPage.Devices or SearchPage.Profiles)
            {
                _navigator.Open(new SearchResult { Target = SearchTarget.Entry, Entry = p.Entry, Payload = p.Item ?? p.Label, Pad = p.Pad }, takeFocus: false, ring: false);
                return;
            }
            // Home's and About's own markup: the page, then the folds on the
            // way, as a landing opens them.
            if (p.Entry?.Page is SearchPage.Home or SearchPage.About)
            {
                OpenPageOf(p.Entry.Page == SearchPage.Home ? "Home" : "About", Target(p));
                return;
            }
            if (p.LiveItem is { Kind: LiveKind.PluginDeclaration } declared)
            {
                // Its plugin's card on the Plugins page, or its device's
                // panel, then each fold on the way to it, the card's own
                // among them. A page that just came up draws its cards when
                // it lays out, so the folds open again then, as Search's
                // landing waits for the layout (SearchNavigator.Open posts
                // its Land at Loaded).
                if (HolderOf(p) is not { } holder) return;
                OpenPlace(holder.Id);
                string key = KeyOf(declared);
                void OpenOnTheWay()
                {
                    if (DeclarationSpot(HolderCard(holder), key) is { } spot) SearchNavigator.OpenFolds(spot, null);
                }
                OpenOnTheWay();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(OpenOnTheWay));
                return;
            }
            if (p.LiveItem is { } item)
            {
                // A mapping row's mouse setting: its controller's list, with
                // the row picked and open, as a landing on a row's option.
                if (MouseSteeringCatalog.SpotOf(_vm, item.Id) is { } spot)
                {
                    _navigator.OpenMappingRow(spot.Pad.PadIndex, spot.Row);
                    return;
                }
                // Any other row: the page that draws it, and the folds on
                // the way.
                var element = LiveElement(item);
                if (TagOfLiveItem(item, element) is { } tag) OpenPageOf(tag, element);
                return;
            }
            if (p.Live == null) return;
            switch (p.Live.Kind)
            {
                case LiveKind.Device:
                    _navigator.Open(new SearchResult { Target = SearchTarget.Device, Payload = p.Live.Payload }, takeFocus: false);
                    break;
                case LiveKind.Profile:
                    _navigator.Open(new SearchResult { Target = SearchTarget.Profile, Payload = p.Live.Payload }, takeFocus: false);
                    break;
                case LiveKind.Macro:
                    _navigator.Open(new SearchResult { Target = SearchTarget.Macro, Payload = p.Live.Payload }, takeFocus: false);
                    break;
                case LiveKind.Plugin:
                    _navigator.Open(new SearchResult { Target = SearchTarget.Plugin, Payload = p.Live.Payload }, takeFocus: false);
                    break;
            }
        }

        /// <summary>The element a place rings once it is up: its row, card or
        /// button, a device's row, a profile's row, a macro's row, a
        /// controller's or page's sidebar entry, a dialog's content, or a
        /// menu item's button.</summary>
        internal FrameworkElement ElementOf(string id)
        {
            var all = Places(otherControllers: false);
            Place p;
            try { p = Resolve(id, all); }
            catch (AssistantRefusal) { return null; }
            if (p.LiveItem is { } item)
                return LiveElement(item)
                       ?? (item.Kind == LiveKind.PluginDeclaration ? DeclarationIn(HolderCard(HolderOf(p, all)), KeyOf(item)) : null);
            if (p.Entry == null && p.Live == null) return Ringable(p.Root);
            if (p.Live != null)
            {
                switch (p.Live.Kind)
                {
                    case LiveKind.Page: return _parts.NavItem?.Invoke(p.Live.Payload as string);
                    case LiveKind.Controller: return _parts.NavItem?.Invoke("Pad" + ((int)p.Live.Payload + 1).ToString(CultureInfo.InvariantCulture));
                    case LiveKind.Device: return RowElement(_pageOf(SearchPage.Devices), p.Live.Payload);
                    case LiveKind.Profile: return RowElement(_pageOf(SearchPage.Profiles), p.Live.Payload);
                    case LiveKind.Macro: return p.Live.Payload is ValueTuple<int, MacroItem> macro ? MacroRow(macro.Item1, macro.Item2) : null;
                    case LiveKind.Plugin: return PluginCard(p.Live.Payload);
                    default: return null;
                }
            }
            if (p.Entry.Kind == SearchKind.Dialog) return Ringable(p.Root);
            if (p.Entry.Kind == SearchKind.Section && _pageOf(SearchPage.Pad) is PadPage pad) return pad.SectionButton(p.Entry.Section);
            if (p.Entry.Kind == SearchKind.Extra && _pageOf(SearchPage.Extras) is ExtrasPage extras)
                return extras.FindName(p.Entry.ExtraKey + "Tile") as FrameworkElement;
            if (p.Entry.Kind == SearchKind.MenuItem)
                return MenuOf(p, _pageOf(p.Entry.Page) ?? p.Root).Owner;
            if (p.Entry.RowOptions) return RowOptionElement(p.Entry);
            if (p.Entry.Page is SearchPage.Pad or SearchPage.Settings or SearchPage.Extras or SearchPage.Devices or SearchPage.Profiles)
            {
                _service.Locator.Reset();
                var label = p.Entry.ItemsPath != null ? _service.Locator.FindInItem(p.Entry, p.Item, _service.Index) : _service.Locator.Find(p.Entry, _service.Index);
                return label == null ? null : SearchLocator.TargetOf(label, p.Entry);
            }
            return Target(p);
        }

        /// <summary>The automation ID of the Test Controller's card on the
        /// Plugins page.</summary>
        internal const string TestControllerCardId = "Plugins_TestControllerCard";

        /// <summary>A plugin's card on the Plugins page, drawn now: a
        /// plugin's own by its row and its card's ID, the Test Controller's
        /// by its card's ID.</summary>
        private FrameworkElement PluginCard(object payload)
        {
            var extras = _pageOf(SearchPage.Extras);
            if (extras == null) return null;
            string id = payload switch
            {
                PluginRowItem row => row.FoldId,
                DashboardViewModel => TestControllerCardId,
                _ => null,
            };
            if (id == null) return null;
            return SearchLocator.VisualDescendants(extras).OfType<PadForge.Controls.FoldCard>()
                .FirstOrDefault(card => AutomationProperties.GetAutomationId(card) == id
                                        && (payload is not PluginRowItem || ReferenceEquals(card.DataContext, payload)));
        }

        /// <summary>The place a plugin's declaration sits under, as its ID
        /// begins: the plugin's own, the Test Controller's, or the device's
        /// whose row the Devices page has. Null for any other place.</summary>
        private Place HolderOf(Place p, List<Place> all = null)
        {
            if (p.LiveItem is not { Kind: LiveKind.PluginDeclaration } item) return null;
            int slash = item.Id.LastIndexOf('/');
            if (slash <= 0) return null;
            try
            {
                var holder = Resolve(item.Id.Substring(0, slash), all);
                return holder.Live?.Kind is LiveKind.Plugin or LiveKind.Device ? holder : null;
            }
            catch (AssistantRefusal) { return null; }
        }

        /// <summary>A declaration's key: its ID's last part, which holds no
        /// slash (ProgramDeviceDeclaration.IsKey).</summary>
        private static string KeyOf(LiveItem item) => item.Id.Substring(item.Id.LastIndexOf('/') + 1);

        /// <summary>The card a holder's declarations are drawn in: a plugin's
        /// card on the Plugins page, or the plugin card of the device's
        /// panel while the panel shows that device.</summary>
        private FrameworkElement HolderCard(Place holder)
        {
            switch (holder?.Live?.Kind)
            {
                case LiveKind.Plugin:
                    return PluginCard(holder.Live.Payload);
                case LiveKind.Device:
                    if (holder.Live.Payload is not DeviceRowViewModel row || _vm.Devices?.SelectedDevice?.InstanceGuid != row.InstanceGuid) return null;
                    return DevicePluginCard();
                default:
                    return null;
            }
        }

        /// <summary>The device panel's card for a plugin's device.</summary>
        private FrameworkElement DevicePluginCard() => _pageOf(SearchPage.Devices)?.FindName("PluginDeviceCard") as FrameworkElement;

        /// <summary>The control a plugin's settings view draws for a
        /// declaration in a card: the element of its row that carries its
        /// AutomationId, or the row itself for a paragraph. Found by the
        /// row's data, so no other element of that ID is taken. Null until
        /// the card drew its rows.</summary>
        private static FrameworkElement DeclarationIn(FrameworkElement card, string key)
        {
            if (card == null) return null;
            FrameworkElement row = null;
            foreach (var element in SearchLocator.VisualDescendants(card).OfType<FrameworkElement>())
            {
                if (element.DataContext is not PluginSettingItem item || item.Key != key) continue;
                if (AutomationProperties.GetAutomationId(element) == item.AutomationId) return element;
                row ??= element;
            }
            return row;
        }

        /// <summary>Where a declaration sits in its card now, for the folds
        /// on the way to it: its control once the card drew it, else what
        /// the card opens, so the card's own fold is on the way.</summary>
        private static FrameworkElement DeclarationSpot(FrameworkElement card, string key)
            => DeclarationIn(card, key) ?? ((card as PadForge.Controls.FoldPart)?.Content as FrameworkElement) ?? card;

        /// <summary>
        /// The clicks on the way to a plugin's declaration: the way to its
        /// plugin's card, then the card while it is shut; or the way to its
        /// device's list, the device's row while another device's panel
        /// shows, and each fold over the device panel's plugin card. A
        /// plugin's card is drawn once the Plugins page shows, so its step
        /// finds the card when the step comes.
        /// </summary>
        private IEnumerable<AssistantPointer.Step> WalkToDeclaration(Place p, LiveItem declared)
        {
            var steps = new List<AssistantPointer.Step>();
            if (HolderOf(p) is not { } holder) return steps;
            steps.AddRange(WalkTo(holder));
            if (holder.Live.Payload is DeviceRowViewModel device)
            {
                if (_vm.Devices?.SelectedDevice?.InstanceGuid != device.InstanceGuid)
                    steps.Add(DeviceRowStep(device));
                var card = DevicePluginCard();
                steps.AddRange(FoldSteps(DeclarationIn(HolderCard(holder), KeyOf(declared)) ?? card, null));
                return steps;
            }
            steps.Add(new AssistantPointer.Step
            {
                Say = () => FoldWords(HolderCard(holder) as PadForge.Controls.FoldPart),
                Advance = AssistantAdvance.Click,
                Done = () => HolderCard(holder) is PadForge.Controls.FoldPart { IsExpanded: true } or PadForge.Controls.FoldCard { CanOpen: false },
                Find = () => HolderCard(holder),
                Stays = true,
            });
            return steps;
        }

        /// <summary>A walk's step to another device's row on the Devices
        /// page. It stays until the device is picked: the user picks
        /// it.</summary>
        private AssistantPointer.Step DeviceRowStep(DeviceRowViewModel device) => new()
        {
            Words = string.Format(CultureInfo.CurrentCulture, PadForge.Resources.Strings.Strings.Instance.Automation_Pointer_Open_Format, device.DeviceName),
            Advance = AssistantAdvance.Click,
            Done = () => _vm.Devices?.SelectedDevice?.InstanceGuid == device.InstanceGuid,
            Find = () => RowElement(_pageOf(SearchPage.Devices), device),
            Stays = true,
        };

        /// <summary>The controller page's mapping list.</summary>
        private DataGrid MappingGrid() => _pageOf(SearchPage.Pad)?.FindName("MappingDataGrid") as DataGrid;

        /// <summary>Whether a mapping row's item passes an option's rules, so
        /// its details hold the option.</summary>
        private bool HoldsOption(SearchEntry entry, object item)
        {
            var page = _pageOf(SearchPage.Pad);
            return item != null && page != null && entry.ItemGates.All(g => BindingProbe.Passes(item, g, k => page.TryFindResource(k)));
        }

        /// <summary>The row a walk to an option rings: the picked row when it
        /// holds the option, else the first the list shows in an open group
        /// that does, else the first the list shows that does, else the
        /// controller's first that does, which a filter of the list keeps
        /// out until the user clears it.</summary>
        private object RowFor(SearchEntry entry)
        {
            if (MappingGrid() is not { } grid) return null;
            if (HoldsOption(entry, grid.SelectedItem)) return grid.SelectedItem;
            var listed = grid.Items.Cast<object>().Where(item => HoldsOption(entry, item)).ToList();
            return listed.FirstOrDefault(item => !GroupFolded(item as MappingItem)) ?? listed.FirstOrDefault()
                   ?? (_pageOf(SearchPage.Pad)?.DataContext as PadViewModel)?.RowsOfSlot.FirstOrDefault(item => HoldsOption(entry, item));
        }

        /// <summary>A row of the mapping list, brought into the list's view
        /// first when the list has not drawn it. None for a row the list's
        /// box hides.</summary>
        private DataGridRow RowContainer(object item)
        {
            if (item == null || MappingGrid() is not { } grid || !grid.Items.Contains(item)) return null;
            if (grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow drawn) return drawn;
            grid.ScrollIntoView(item);
            grid.UpdateLayout();
            return grid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
        }

        /// <summary>An option's element in the picked row's details, as
        /// Search's landing finds it there (SearchNavigator.RowOption): none
        /// while no row that holds the option is picked.</summary>
        private FrameworkElement RowOptionElement(SearchEntry entry)
        {
            if (MappingGrid() is not { } grid || !HoldsOption(entry, grid.SelectedItem)) return null;
            if (grid.ItemContainerGenerator.ContainerFromItem(grid.SelectedItem) is not DataGridRow row) return null;
            var label = SearchLocator.FindAmong(SearchNavigator.RowOptionParts(row).ToList(), entry, grid.SelectedItem, _service.Index, e => e.RowOptions);
            return label == null ? null : SearchLocator.TargetOf(label, entry);
        }

        /// <summary>An item's row in the list on a page that shows it. A
        /// list that holds the item and has not drawn its row brings it into
        /// view first, as the list's own scroll would.</summary>
        private static FrameworkElement RowElement(FrameworkElement page, object item)
        {
            if (page == null || item == null) return null;
            foreach (var list in SearchLocator.VisualDescendants(page).OfType<ItemsControl>())
                if (list.IsVisible && list.Items.Contains(item) && DrawnRow(list, item) is { IsVisible: true } row) return row;
            return null;
        }

        /// <summary>The row a list draws for an item, brought into view when
        /// the list has not drawn it.</summary>
        private static FrameworkElement DrawnRow(ItemsControl list, object item)
        {
            if (list.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement drawn) return drawn;
            if (list is not ListBox box) return null;
            box.ScrollIntoView(item);
            box.UpdateLayout();
            return box.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
        }

        /// <summary>The clicks on the way to a place, outermost first: the
        /// sidebar entry, the filters that keep a device's row out of the
        /// Devices page's lists and another device's row, the controller's
        /// section and its part, the mapping list's filters, the band of a
        /// folded group and the row an option or a mouse setting is on, the
        /// way back to the Extras page's tiles and the extra's tile, the
        /// Settings group, and each closed fold card and More that holds the
        /// place, wherever it sits. A click already made is skipped when the
        /// walk comes to it. A step whose click can leave its work undone (a
        /// fold's switch, a box that still holds words) stays until it is
        /// done.</summary>
        private IEnumerable<AssistantPointer.Step> WalkTo(Place p)
        {
            if (p.LiveItem is { Kind: LiveKind.PluginDeclaration } declared) return WalkToDeclaration(p, declared);
            var steps = new List<AssistantPointer.Step>();
            // A mapping row's mouse setting: the controller's entry, the way
            // to the row in the list, then the row, which the user picks. The
            // setting stands in the row's details, which open with the pick.
            if (p.LiveItem != null && MouseSteeringCatalog.SpotOf(_vm, p.LiveItem.Id) is { } spot)
            {
                var mapped = spot.Row;
                steps.Add(SidebarStep(PadTag(spot.Pad)));
                steps.AddRange(WayToMappingRow(spot.Pad, () => mapped));
                steps.Add(MappingRowStep(() => mapped, () => ReferenceEquals(MappingGrid()?.SelectedItem, mapped)));
                string line = p.Id;
                steps.AddRange(FoldSteps(null, () => ElementOf(line)));
                return steps;
            }
            var element = p.LiveItem != null ? LiveElement(p.LiveItem) : null;
            string tag = TagOf(p, element);
            if (tag != null) steps.Add(SidebarStep(tag));
            // A device: the filters and the folds that keep its row from
            // showing. The row is the place.
            if (p.Live?.Kind == LiveKind.Device && p.Live.Payload is DeviceRowViewModel shown)
                steps.AddRange(WayToDeviceRow(shown));
            // A place on another device's panel: that device's row, after the
            // filters and folds that keep it from showing, which the user
            // picks.
            if (p.Entry?.Page == SearchPage.Devices && NamesAnotherDevice(p.Id) && DeviceRowOf(p.Id) is { } device)
            {
                steps.AddRange(WayToDeviceRow(device));
                steps.Add(DeviceRowStep(device));
            }
            if (p.Entry?.Page == SearchPage.Pad && p.Entry.Section >= 0 && p.Pad != null)
            {
                var pad = p.Pad;
                // The section's button, unless the place is that button.
                if (p.Entry.Kind != SearchKind.Section) steps.Add(SectionStep(pad, p.Entry.Section));
                // Buttons and Macros & Menus show one part at a time: the
                // part's own button, unless the place is that button.
                if (p.Entry.Kind != SearchKind.Part && PartOf(p.Entry.Tab) is { } part) steps.Add(PartStep(pad, part));
                // An option of a mapping row: the way to the row in the list,
                // then the row the option is on, which the user picks. Its
                // details open with the pick.
                if (p.Entry.RowOptions)
                {
                    var entry = p.Entry;
                    steps.AddRange(WayInMappingList(() => RowFor(entry) as MappingItem));
                    steps.Add(MappingRowStep(() => RowFor(entry), () => HoldsOption(entry, MappingGrid()?.SelectedItem)));
                }
            }
            // A macro: Macros & Menus, whose list holds it whichever of the
            // two parts shows.
            if (p.Live?.Kind == LiveKind.Macro && PadOfMacro(p.Live.Payload) is { } macroPad)
                steps.Add(SectionStep(macroPad, PadViewModel.MacrosSection));
            // The Extras page: back to its tiles from another extra's page,
            // then the extra's tile, unless the place is the tile.
            if (p.Entry?.Page == SearchPage.Extras || p.Live?.Kind == LiveKind.Plugin)
            {
                string extra = p.Live?.Kind == LiveKind.Plugin ? "Plugins"
                    : p.Entry.Kind != SearchKind.Extra ? p.Entry.ExtraKey : null;
                steps.Add(ExtrasBackStep(extra));
                if (extra != null) steps.Add(ExtraTileStep(extra));
            }
            // The Settings page shows one group at a time: its group's
            // button on the page's left.
            var onSettings = p.Entry?.Page == SearchPage.Settings ? SettingsSpot(p.Entry) : p.LiveItem != null && tag == "Settings" ? element : null;
            if (SettingsGroupStep(onSettings) is { } group) steps.Add(group);
            // Each closed fold card and More that holds the place. A page's
            // or a controller's sidebar entry sits in none, and a device's
            // row is reached through its list above.
            if (p.Live?.Kind is not (LiveKind.Page or LiveKind.Controller or LiveKind.Device))
            {
                string id = p.Id;
                steps.AddRange(FoldSteps(ElementOf(id), () => ElementOf(id)));
            }
            return steps;
        }

        /// <summary>The sidebar's tag of a controller's page.</summary>
        private static string PadTag(PadViewModel pad) => "Pad" + (pad.PadIndex + 1).ToString(CultureInfo.InvariantCulture);

        /// <summary>The controller a macro's payload names, or null.</summary>
        private PadViewModel PadOfMacro(object payload)
            => payload is ValueTuple<int, MacroItem> macro && macro.Item1 >= 0 && macro.Item1 < _vm.Pads.Count ? _vm.Pads[macro.Item1] : null;

        /// <summary>The sidebar's tag of the page that holds a place: a
        /// page's entry for Search's and the catalog's markup, the page of a
        /// device, a profile, a plugin or a macro, and for a live row the
        /// page its element sits in. Null for a page's or a controller's own
        /// entry, which is the place, and for a dialog's or the window's
        /// place.</summary>
        private string TagOf(Place p, FrameworkElement element)
        {
            if (p.Entry != null)
            {
                return p.Entry.Page switch
                {
                    SearchPage.Pad => p.Pad == null ? null : PadTag(p.Pad),
                    SearchPage.Settings => "Settings",
                    SearchPage.Extras => "Extras",
                    SearchPage.Devices => "Devices",
                    SearchPage.Profiles => "Profiles",
                    SearchPage.Home => "Home",
                    SearchPage.About => "About",
                    _ => null,
                };
            }
            switch (p.Live?.Kind)
            {
                case LiveKind.Device: return "Devices";
                case LiveKind.Profile: return "Profiles";
                case LiveKind.Plugin: return "Extras";
                case LiveKind.Macro: return PadOfMacro(p.Live.Payload) is { } pad ? PadTag(pad) : null;
            }
            return p.LiveItem is { } item ? TagOfLiveItem(item, element) : null;
        }

        /// <summary>The sidebar's tag of the page that draws a live row: the
        /// page its provider names (<see cref="LiveItem.Page"/>), else the
        /// page its element sits in. Null for a row in a dialog.</summary>
        private string TagOfLiveItem(LiveItem item, FrameworkElement element)
            => item.Page ?? TagOfElement(element);

        private static readonly (SearchPage Page, string Tag)[] PageTags =
        {
            (SearchPage.Settings, "Settings"), (SearchPage.Extras, "Extras"), (SearchPage.Devices, "Devices"), (SearchPage.Profiles, "Profiles"),
        };

        /// <summary>The sidebar's tag of the page an element sits in, or
        /// null for one in a dialog or in none of the pages.</summary>
        private string TagOfElement(FrameworkElement element)
        {
            for (DependencyObject at = element; at != null; at = SearchLocator.Up(at))
            {
                if (at is not FrameworkElement here) continue;
                if (ReferenceEquals(here, _pageOf(SearchPage.Pad))) return here.DataContext is PadViewModel pad ? PadTag(pad) : null;
                foreach (var (page, tag) in PageTags)
                    if (ReferenceEquals(here, _pageOf(page))) return tag;
                if (_parts.CatalogPageOf is { } catalogPage)
                {
                    if (ReferenceEquals(here, catalogPage(SearchPage.Home))) return "Home";
                    if (ReferenceEquals(here, catalogPage(SearchPage.About))) return "About";
                }
            }
            return null;
        }

        /// <summary>A page's name as its sidebar entry says it: a
        /// controller's name for a controller's page.</summary>
        private string NameOfTag(string tag)
        {
            if (tag.StartsWith("Pad", StringComparison.Ordinal)
                && int.TryParse(tag.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int one) && one >= 1 && one <= _vm.Pads.Count)
                return SearchService.SlotName(_vm.Pads[one - 1]);
            return PageName(PageOfTag(tag));
        }

        /// <summary>A plain show's opening of a place Search's index does not
        /// hold: its page, as a click on its sidebar entry opens it, the
        /// Settings group that holds it with the page's search box emptied,
        /// and each fold on the way to its element.</summary>
        private void OpenPageOf(string tag, FrameworkElement element)
        {
            _navigator.Open(new SearchResult { Target = SearchTarget.Page, Payload = tag }, takeFocus: false);
            if (element == null) return;
            if (tag == "Settings" && _pageOf(SearchPage.Settings) is SettingsPage settings) settings.Reveal(element);
            SearchNavigator.OpenFolds(element, null);
        }

        /// <summary>A live row's element where it is drawn now: the item's
        /// own. A mapping row's mouse setting falls back to its line on this
        /// catalog's controller page, whichever page the item's own element
        /// looks on.</summary>
        private FrameworkElement LiveElement(LiveItem item)
        {
            if (item.Element is { } find && SafeElement(find) is { } own) return own;
            return MouseSteeringCatalog.SpotOf(_vm, item.Id) is { } spot
                ? SafeElement(() => MouseSteeringCatalog.LineElement(_pageOf(SearchPage.Pad), spot.Row, spot.Source, spot.Key))
                : null;
        }

        /// <summary>A macro's row in its controller's macro list, while the
        /// controller page shows that controller. The list shows in both
        /// parts of Macros &amp; Menus.</summary>
        private FrameworkElement MacroRow(int padIndex, MacroItem macro)
        {
            if (_pageOf(SearchPage.Pad) is not PadPage page || page.DataContext is not PadViewModel pad || pad.PadIndex != padIndex) return null;
            var list = SearchLocator.LogicalDescendants(page).OfType<ListBox>().FirstOrDefault(l => AutomationProperties.GetAutomationId(l) == "MacroList");
            return list == null || !list.IsVisible || !list.Items.Contains(macro) ? null : DrawnRow(list, macro);
        }

        /// <summary>What a ring goes around for a window: its content. The
        /// ring is drawn on the adorner layer inside a window, and no layer
        /// lies over a window itself.</summary>
        private static FrameworkElement Ringable(FrameworkElement root)
            => root is Window window && window.Content is FrameworkElement content ? content : root;

        /// <summary>A walk's step to a page's sidebar entry.</summary>
        private AssistantPointer.Step SidebarStep(string tag) => new()
        {
            Words = string.Format(CultureInfo.CurrentCulture, PadForge.Resources.Strings.Strings.Instance.Automation_Pointer_Open_Format, NameOfTag(tag)),
            Advance = AssistantAdvance.Click,
            Done = () => (_parts.NavTag?.Invoke() ?? _vm.SelectedNavTag) == tag,
            Find = () => _parts.NavItem?.Invoke(tag),
        };

        /// <summary>A walk's step to a section of a controller's page. Its
        /// name is read when the step comes, once the page shows that
        /// controller: the first section's name follows the controller's
        /// type.</summary>
        private AssistantPointer.Step SectionStep(PadViewModel pad, int section) => new()
        {
            Say = () => string.Format(CultureInfo.CurrentCulture, PadForge.Resources.Strings.Strings.Instance.Automation_Pointer_Open_Format,
                (_pageOf(SearchPage.Pad) as PadPage)?.SectionName(section) ?? string.Empty),
            Advance = AssistantAdvance.Click,
            Done = () => PadViewModel.SectionOfTab(pad.SelectedConfigTab) == section,
            Find = () => (_pageOf(SearchPage.Pad) as PadPage)?.SectionButton(section),
        };

        /// <summary>A walk's step to a part button of Buttons or of Macros
        /// &amp; Menus.</summary>
        private AssistantPointer.Step PartStep(PadViewModel pad, PartButton part) => new()
        {
            Say = () => string.Format(CultureInfo.CurrentCulture, PadForge.Resources.Strings.Strings.Instance.Automation_Pointer_Open_Format, PartName(part.Button)),
            Advance = AssistantAdvance.Click,
            Done = () => part.Shows(pad),
            Find = () => (_pageOf(SearchPage.Pad) as PadPage)?.FindName(part.Button) as FrameworkElement,
        };

        /// <summary>
        /// The clicks on the way to a device's row on the Devices page, after
        /// its sidebar entry: the control that keeps the row out of both
        /// lists (the Show segment, the connection segment, the search box,
        /// one after another, as DevicesPage.HiderOf names them), which the
        /// user clears or picks, then each closed fold over the list that
        /// holds the row. The filters' step stays until the row is listed,
        /// as a fold's step stays until the fold is open. The program never
        /// empties the search or changes a segment.
        /// </summary>
        private IEnumerable<AssistantPointer.Step> WayToDeviceRow(DeviceRowViewModel device)
        {
            FrameworkElement Hider() => (_pageOf(SearchPage.Devices) as DevicesPage)?.HiderOf(device);
            var steps = new List<AssistantPointer.Step>
            {
                new()
                {
                    Say = () => FilterWords(Hider()),
                    Advance = AssistantAdvance.Click,
                    Done = () => Hider() == null,
                    Find = Hider,
                    Stays = true,
                },
            };
            steps.AddRange(FoldSteps(ListHolding(_pageOf(SearchPage.Devices), device), () => ListHolding(_pageOf(SearchPage.Devices), device)));
            return steps;
        }

        /// <summary>A filter's step in words: Clear the search box for a box
        /// the user empties, or Pick and the words of the choice that shows
        /// every row, for a segment or a picker.</summary>
        private static string FilterWords(FrameworkElement hider)
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            string words;
            switch (hider)
            {
                case null:
                    return string.Empty;
                case System.Windows.Controls.Primitives.TextBoxBase:
                    return s.Automation_Pointer_ClearSearch;
                case ComboBox picker:
                    words = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == "All")?.Content as string;
                    break;
                default:
                    words = SearchLocator.LogicalDescendants(hider).OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
                            ?? AutomationProperties.GetName(hider);
                    break;
            }
            return string.Format(CultureInfo.CurrentCulture, s.Automation_Pointer_Pick_Format, words ?? string.Empty);
        }

        /// <summary>The clicks on the way to a mapping row of a controller's
        /// list, after the controller's sidebar entry: the first section,
        /// its List, and the way in the list (<see cref="WayInMappingList"/>).
        /// </summary>
        private IEnumerable<AssistantPointer.Step> WayToMappingRow(PadViewModel pad, Func<MappingItem> row)
        {
            var steps = new List<AssistantPointer.Step> { SectionStep(pad, PadViewModel.ButtonsSection) };
            if (PartOf(PadViewModel.MappingsTabIndex) is { } list) steps.Add(PartStep(pad, list));
            steps.AddRange(WayInMappingList(row));
            return steps;
        }

        /// <summary>The clicks in the controller page's list on the way to a
        /// row, once the list shows: each closed fold over the list, the
        /// control that keeps the row out of the list (its Keyboard and Mouse
        /// scope, then its search box), which the user picks or clears, and
        /// the band of the row's group while the group is folded. Each stays
        /// until it is done. <paramref name="row"/> is read as each step
        /// comes.</summary>
        private IEnumerable<AssistantPointer.Step> WayInMappingList(Func<MappingItem> row)
        {
            var steps = new List<AssistantPointer.Step>();
            if (MappingGrid() is { } grid) steps.AddRange(FoldSteps(grid, null));
            steps.Add(new AssistantPointer.Step
            {
                Say = () => FilterWords(ListHider(row())),
                Advance = AssistantAdvance.Click,
                Done = () => ListHider(row()) == null,
                Find = () => ListHider(row()),
                Stays = true,
            });
            steps.Add(new AssistantPointer.Step
            {
                Say = () => string.Format(CultureInfo.CurrentCulture, PadForge.Resources.Strings.Strings.Instance.Automation_Pointer_Open_Format,
                    FoldedBand(row()) is { } band ? AutomationProperties.GetName(band) : string.Empty),
                Advance = AssistantAdvance.Click,
                Done = () => !GroupFolded(row()),
                Find = () => FoldedBand(row()),
                Stays = true,
            });
            return steps;
        }

        /// <summary>A walk's step to a mapping row of the list, which the
        /// user picks: its details open with the pick.</summary>
        private AssistantPointer.Step MappingRowStep(Func<object> row, Func<bool> done) => new()
        {
            Say = () => string.Format(CultureInfo.CurrentCulture, PadForge.Resources.Strings.Strings.Instance.Automation_Pointer_Open_Format,
                (row() as MappingItem)?.TargetLabel ?? string.Empty),
            Advance = AssistantAdvance.Click,
            Done = done,
            Find = () => RowContainer(row()),
            Stays = true,
        };

        /// <summary>The control that keeps a mapping row out of the
        /// controller page's list now: the Keyboard and Mouse scope while it
        /// leaves the row out, then the list's search box while its words
        /// match nothing the row says. Null while the list holds the row or
        /// shows the picture, and for a row the slot's own surface mode
        /// leaves out, which no filter of the list brings back.</summary>
        private FrameworkElement ListHider(MappingItem row)
        {
            if (row == null || MappingGrid() is not { } grid || grid.Items.Contains(row)) return null;
            var page = _pageOf(SearchPage.Pad);
            if (page?.DataContext is not PadViewModel vm || !vm.IsButtonsListView || !vm.Mappings.Contains(row)) return null;
            var kbm = vm.OutputType == PadForge.Engine.VirtualControllerType.KeyboardMouse ? vm.KbmConfig : null;
            if (!PadViewModel.RowMatchesSurface(row, "All", kbm)) return null;
            if (!PadViewModel.RowMatchesSurface(row, vm.MappingSurfaceScope, kbm) && page.FindName("MappingSurfaceScopeBox") is ComboBox scope && scope.IsVisible)
                return scope;
            return PadViewModel.RowMatchesSearch(row, vm.MappingInputSearch) ? null : page.FindName("MappingFilterSearchBox") as FrameworkElement;
        }

        /// <summary>Whether the list's group that holds a row is folded: a
        /// controller's list folds Left Stick and Right Stick to start
        /// with.</summary>
        private bool GroupFolded(MappingItem row)
            => row != null && _pageOf(SearchPage.Pad)?.DataContext is PadViewModel vm && vm.ListIsGrouped
               && MappingGrid() is { } grid && grid.Items.Contains(row) && !vm.IsListGroupOpen(row.Category);

        /// <summary>The band of the folded group that holds a row, which the
        /// user clicks to open the group. A group the list has not drawn is
        /// brought into view first, as the list brings a row in: the list
        /// draws a folded group's band and none of its rows.</summary>
        private FrameworkElement FoldedBand(MappingItem row)
        {
            if (!GroupFolded(row) || MappingGrid() is not { } grid) return null;
            var group = grid.Items.Groups?.OfType<CollectionViewGroup>().FirstOrDefault(g => g.Items.Contains(row));
            if (group == null) return null;
            if (grid.ItemContainerGenerator.ContainerFromItem(group) is not GroupItem item)
            {
                grid.ScrollIntoView(row);
                grid.UpdateLayout();
                item = grid.ItemContainerGenerator.ContainerFromItem(group) as GroupItem;
            }
            var band = item == null ? null : SearchLocator.VisualDescendants(item).OfType<Expander>().FirstOrDefault();
            return band?.Template?.FindName("Band", band) as FrameworkElement ?? band;
        }

        /// <summary>A walk's step back to the Extras page's tiles while an
        /// extra's page other than <paramref name="keep"/> shows: that page's
        /// Extras crumb, which the user clicks.</summary>
        private AssistantPointer.Step ExtrasBackStep(string keep) => new()
        {
            Words = string.Format(CultureInfo.CurrentCulture, PadForge.Resources.Strings.Strings.Instance.Automation_Pointer_Open_Format, PageName(SearchPage.Extras)),
            Advance = AssistantAdvance.Click,
            Done = () => (_pageOf(SearchPage.Extras) as ExtrasPage)?.CurrentExtra is not { } now || now == keep,
            Find = () => _pageOf(SearchPage.Extras) is ExtrasPage extras && extras.CurrentExtra is { } now
                ? SearchLocator.LogicalDescendants(extras).OfType<System.Windows.Controls.Primitives.ButtonBase>()
                    .FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "Extras_Back_" + now)
                : null,
        };

        /// <summary>A walk's step to an extra's tile on the Extras
        /// page.</summary>
        private AssistantPointer.Step ExtraTileStep(string key)
        {
            var tile = _service.Index.FirstOrDefault(e => e.Kind == SearchKind.Extra && e.ExtraKey == key);
            return new AssistantPointer.Step
            {
                Words = string.Format(CultureInfo.CurrentCulture, PadForge.Resources.Strings.Strings.Instance.Automation_Pointer_Open_Format,
                    tile == null ? key : SearchLocator.LabelText(tile.Label, null)),
                Advance = AssistantAdvance.Click,
                Done = () => (_pageOf(SearchPage.Extras) as ExtrasPage)?.CurrentExtra == key,
                Find = () => _pageOf(SearchPage.Extras)?.FindName(key + "Tile") as FrameworkElement,
            };
        }

        /// <summary>Where a Settings page entry sits, for its group: its
        /// label, or for an entry of a list's items the list, as Search's
        /// landing reveals it (SearchNavigator.Open). None for a row's
        /// option.</summary>
        private DependencyObject SettingsSpot(SearchEntry entry)
        {
            if (entry.RowOptions) return null;
            _service.Locator.Reset();
            return entry.ItemsPath != null ? _service.Locator.ListOf(entry) : _service.Locator.Find(entry, _service.Index);
        }

        /// <summary>A walk's step to the Settings page's group that holds a
        /// part of the page, or null for a part in none. The group's button
        /// empties the page's search box as it opens the group.</summary>
        private AssistantPointer.Step SettingsGroupStep(DependencyObject part)
        {
            if (part == null || _pageOf(SearchPage.Settings) is not SettingsPage page || page.GroupOf(part) is not { } group
                || page.DataContext is not SettingsViewModel vm) return null;
            return new AssistantPointer.Step
            {
                Words = string.Format(CultureInfo.CurrentCulture, PadForge.Resources.Strings.Strings.Instance.Automation_Pointer_Open_Format, page.GroupName(group)),
                Advance = AssistantAdvance.Click,
                Done = () => vm.SelectedSettingsGroup == group && string.IsNullOrEmpty(vm.SettingsSearchText),
                Find = () => page.GroupButton(group),
            };
        }

        /// <summary>A step for each closed fold on the way to an element,
        /// outermost first, each ringing its own fold until the user opens
        /// it. An element not drawn yet gets one step that rings whichever
        /// fold holds it when the step comes, one after another, through
        /// <paramref name="find"/>.</summary>
        private static IEnumerable<AssistantPointer.Step> FoldSteps(FrameworkElement element, Func<FrameworkElement> find)
        {
            if (element != null)
            {
                foreach (var fold in FoldsOnTheWay(element))
                {
                    var shut = fold;
                    yield return new AssistantPointer.Step
                    {
                        Words = FoldWords(shut),
                        Advance = AssistantAdvance.Click,
                        Done = () => shut.IsExpanded,
                        Find = () => shut,
                        Stays = true,
                    };
                }
                yield break;
            }
            if (find == null) yield break;
            yield return new AssistantPointer.Step
            {
                Say = () => FoldWords(FoldsOnTheWay(find()).FirstOrDefault()),
                Advance = AssistantAdvance.Click,
                Done = () => FoldsOnTheWay(find()).Count == 0,
                Find = () => FoldsOnTheWay(find()).FirstOrDefault(),
                Stays = true,
            };
        }

        /// <summary>The closed folds a person opens to reach an element,
        /// outermost first: each More row and fold card that holds it in
        /// what it opens and can open. A card's own line, its switch and its
        /// buttons, sits outside what the card opens, so a place there needs
        /// no click on the card.</summary>
        internal static List<PadForge.Controls.FoldPart> FoldsOnTheWay(FrameworkElement element)
        {
            var folds = new List<PadForge.Controls.FoldPart>();
            if (element == null) return folds;
            DependencyObject below = element;
            for (var at = SearchLocator.Up(element); at != null; below = at, at = SearchLocator.Up(at))
            {
                if (at is not PadForge.Controls.FoldPart fold || fold.IsExpanded || fold is PadForge.Controls.FoldCard { CanOpen: false }) continue;
                if (Holds(fold, below, element)) folds.Insert(0, fold);
            }
            return folds;
        }

        /// <summary>Whether a fold holds an element in what it opens: its
        /// content, reached by the logical tree, or its content's site in
        /// its template, reached by the visual tree.</summary>
        private static bool Holds(PadForge.Controls.FoldPart fold, DependencyObject below, DependencyObject element)
        {
            if (ReferenceEquals(below, fold.Content)) return true;
            if (ReferenceEquals(below, fold.Header)) return false;
            if (fold is PadForge.Controls.FoldCard card && (ReferenceEquals(below, card.HeaderAction) || ReferenceEquals(below, card.Switch))) return false;
            return fold.Template?.FindName("ExpandSite", fold) is System.Windows.Media.Visual site
                   && element is System.Windows.Media.Visual visual && site.IsAncestorOf(visual);
        }

        /// <summary>A fold's step in words: Open More, or Open and the
        /// card's title.</summary>
        private static string FoldWords(PadForge.Controls.FoldPart fold)
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            if (fold == null) return string.Empty;
            if (fold is PadForge.Controls.MoreRow) return s.Automation_Pointer_OpenMore;
            string name = fold.Header as string;
            if (string.IsNullOrWhiteSpace(name)) name = AutomationProperties.GetName(fold);
            if (string.IsNullOrWhiteSpace(name) && fold.Header is DependencyObject header)
                name = SearchLocator.LogicalDescendants(header).OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            return string.Format(CultureInfo.CurrentCulture, s.Automation_Pointer_Open_Format, name ?? string.Empty);
        }

        /// <summary>The list on a page that holds an item now, whether or not
        /// a fold shows it.</summary>
        private static FrameworkElement ListHolding(FrameworkElement page, object item)
        {
            if (page == null || item == null) return null;
            return SearchLocator.LogicalDescendants(page).OfType<ItemsControl>().FirstOrDefault(list => list.Items.Contains(item));
        }

        /// <summary>The part button of Buttons or Macros &amp; Menus a tab
        /// shows in, and whether that part shows for a controller.</summary>
        private sealed record PartButton(string Button, Func<PadViewModel, bool> Shows);

        private static PartButton PartOf(int tab) => tab switch
        {
            PadViewModel.PictureTabIndex or PadViewModel.OutputTabIndex => new PartButton("TabController", pad => pad.IsButtonsPictureView),
            PadViewModel.MappingsTabIndex => new PartButton("TabMappings", pad => pad.IsButtonsListView),
            PadViewModel.MacrosTabIndex => new PartButton("TabMacros", pad => pad.MacrosPartIndex == 0),
            PadViewModel.MenusTabIndex => new PartButton("TabMenus", pad => pad.MacrosPartIndex == 1),
            _ => null,
        };

        /// <summary>A part button's words, as its text shows them.</summary>
        private string PartName(string button)
        {
            if ((_pageOf(SearchPage.Pad) as PadPage)?.FindName(button) is not FrameworkElement element) return string.Empty;
            return SearchLocator.LogicalDescendants(element).OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
                   ?? AutomationProperties.GetName(element) ?? string.Empty;
        }
    }
}
