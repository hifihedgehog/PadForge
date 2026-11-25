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
                list.Add(new Place
                {
                    Entry = s.Entry, Item = s.Item, Label = s.Label, Pad = s.Pad, Section = s.Section, ReadValue = s.Value, Showing = true,
                    Id = SearchIds.With(_service.IdOf(s.Entry), s.Entry.Page == SearchPage.Pad ? NumberOf(s.Pad) : null,
                        ItemIndexOf(s.Entry, s.Item, s.Pad), DeviceOf(s.Entry)),
                });
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
            found = all.Where(p => p.Entry != null && _service.IdOf(p.Entry) == want).OrderByDescending(p => p.Showing).FirstOrDefault();
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
                if (p.Showing)
                    return _service.WhereFor(new SearchService.Shown(p.Entry, p.Item, p.Label, p.Pad, p.Section, p.ReadValue));
                return PageName(p.Entry.Page);
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
            if (cls == ReachClass.UsersOwn) return place;

            if (p.LiveItem is { } item)
            {
                place.ValueText = AutomationReadScrub.ForReach(cls, SafeRead(item.Read));
                if (item.Write != null)
                {
                    place.Takes = "text";
                    if (place.ValueText != null) place.Value = JsonSerializer.SerializeToElement(place.ValueText);
                }
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
                        children = all.Where(c => c.Entry != null && c.Entry.Page == SearchPage.Dialog && c.Entry.Dialog == e.Dialog && c.Entry.Kind != SearchKind.Dialog);
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
                            read.Children = DeviceChildren(all, row, prefix);
                            return Finish(read, null);
                        }
                        read.Children = DeviceChildren(all, row, prefix);
                        return Finish(read, null);
                    }
                    default:
                        var own = p.LiveItem;
                        children = all.Where(c => c.Live?.Payload is LiveItem li
                                                  && (li.Id.StartsWith(prefix, StringComparison.Ordinal) || (own != null && own.Menu.Any(m => ReferenceEquals(m, li)))));
                        element = p.LiveItem?.Element?.Invoke();
                        break;
                }
            }
            read.Children = children.Take(MostChildren).Select(Describe).ToList();
            return Finish(read, element);
        }

        private List<AssistantPlace> DeviceChildren(List<Place> all, DeviceRowViewModel row, string prefix)
            => all.Where(c => (c.Entry?.Page == SearchPage.Devices && c.Showing && c.Entry.Kind is SearchKind.Setting or SearchKind.Card or SearchKind.Action)
                              || (c.Live?.Payload is LiveItem li && li.Id.StartsWith(prefix, StringComparison.Ordinal)))
                .Take(MostChildren).Select(Describe).ToList();

        private AssistantRead Finish(AssistantRead read, FrameworkElement element)
        {
            if (element != null && element.IsVisible) ReadLines(element, read);
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
        private static void ReadLines(FrameworkElement root, AssistantRead read)
        {
            var listParts = new HashSet<DependencyObject>();
            foreach (var d in SearchLocator.VisualDescendants(root))
            {
                if (d is ItemsControl list && list is not MenuBase && list.IsVisible && list.Items.Count > 0 && !listParts.Contains(list))
                {
                    if (Reach.Effective(list) == ReachClass.UsersOwn) continue;
                    var rows = new AssistantListRead
                    {
                        Name = AutomationReadScrub.Text(AutomationProperties.GetName(list)),
                        Selected = list is Selector s && s.SelectedIndex >= 0 ? s.SelectedIndex : null,
                    };
                    foreach (object item in list.Items)
                    {
                        string words = list.ItemContainerGenerator.ContainerFromItem(item) is DependencyObject container
                            ? string.Join(" ", SearchLocator.VisualDescendants(container).OfType<TextBlock>().Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text)).Select(t => t.Text))
                            : ItemText(list, item);
                        if (rows.Rows.Count < MostChildren) rows.Rows.Add(AutomationReadScrub.Text(words) ?? string.Empty);
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
            // An extra that listens on the network asks when it is turned on.
            if (why == ReachReason.Network && value.ValueKind != JsonValueKind.True) return null;
            string name = WordsOf(p);
            string valueText = value.ValueKind switch
            {
                JsonValueKind.True => PadForge.Resources.Strings.Strings.Instance.Common_On,
                JsonValueKind.False => PadForge.Resources.Strings.Strings.Instance.Common_Off,
                JsonValueKind.String => value.GetString(),
                _ => value.ToString(),
            };
            return new AssistantAsk(AssistantToolWords.AskSetting(name), new[] { AssistantToolWords.SetsTo(name, valueText), AssistantToolWords.Reason(why) });
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
                item.Write(TextOf(value));
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

        private AssistantPressPlan Plan(Place p, (ReachClass Class, ReachReason Why) reach, Action press, FrameworkElement target)
        {
            RefuseUsersOwn(reach.Class);
            if (target != null && !target.IsEnabled)
                throw new AssistantRefusal("not-available", "The page has this turned off for now. show takes the user to it, where the reason shows.");
            var place = Describe(p);
            AssistantAsk ask = reach.Class == ReachClass.Asks
                ? new AssistantAsk(AssistantToolWords.AskPress(place.Name), new[] { AssistantToolWords.Reason(reach.Why) })
                : null;
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
            string header = SearchLocator.LabelText(p.Entry.Label, null);
            MenuItem found = null;
            FrameworkElement owner = null;
            if (root != null)
            {
                foreach (var element in SearchLocator.LogicalDescendants(root).OfType<FrameworkElement>())
                {
                    if (element.ContextMenu is not { } menu || !element.IsVisible) continue;
                    found = menu.Items.OfType<MenuItem>().FirstOrDefault(m => string.Equals(m.Header as string, header, StringComparison.CurrentCulture));
                    if (found != null) { owner = element; break; }
                }
            }
            if (found == null) throw new AssistantRefusal("not-available", "The page does not show the button whose menu holds this now.");
            var reach = ClassOf(p, owner);
            var (itemClass, itemWhy) = Reach.Of(found);
            if (itemClass > reach.Class) reach = (itemClass, itemWhy);
            if (!found.IsEnabled) throw new AssistantRefusal("not-available", "The menu has this item turned off for now.");
            var item = found;
            return Plan(p, reach, () => Invoke(item), owner);
        }

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
                return new AssistantPressPlan
                {
                    Place = place,
                    Ask = cls == ReachClass.Asks ? new AssistantAsk(AssistantToolWords.AskPress(place.Name), new[] { AssistantToolWords.Reason(button.Why) }) : null,
                    Press = () => Invoke(element),
                };
            }
            throw new AssistantRefusal("unknown-setting", "That dialog has no button " + buttonKey + ". read of the dialog gives its buttons.");
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

        public AssistantShown Show(AssistantShowRequest request)
        {
            var pointer = _parts.Pointer ?? throw new AssistantRefusal("not-available", "PadForge cannot point here.");
            if (request.Steps.Count == 0)
            {
                bool had = pointer.Clear();
                return new AssistantShown { Shown = false, Window = _windowPlace(), Message = had ? "The callout is gone." : "Nothing was pointed at." };
            }
            var all = Places();
            var steps = new List<AssistantPointer.Step>();
            for (int i = 0; i < request.Steps.Count; i++)
            {
                var s = request.Steps[i];
                var p = Resolve(s.Place, all);
                var (cls, _) = ClassOf(p);
                if (cls == ReachClass.UsersOwn)
                    throw new AssistantRefusal("users-own", "A place that is the user's alone is not pointed at by a program. Tell the user where it is in words.");
                if (request.Walk && i == request.Steps.Count - 1)
                    steps.AddRange(WalkTo(p));
                steps.Add(StepOf(p, s.Words, s.Advance, s.Seconds));
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
        /// element once it is up.</summary>
        private AssistantPointer.Step StepOf(Place p, string words, AssistantAdvance advance, int seconds)
        {
            string id = p.Id;
            return new AssistantPointer.Step
            {
                Words = words,
                Advance = advance,
                Seconds = seconds,
                Open = () => OpenPlace(id),
                Find = () => ElementOf(id),
            };
        }

        /// <summary>Takes the page to a place, as Search's landing does,
        /// without the keyboard's focus and without the ring, which the
        /// pointer draws and holds itself.</summary>
        private void OpenPlace(string id)
        {
            Place p;
            try { p = Resolve(id); }
            catch (AssistantRefusal) { return; }
            if (p.Entry != null && p.Entry.Page is SearchPage.Pad or SearchPage.Settings or SearchPage.Extras or SearchPage.Devices or SearchPage.Profiles)
            {
                _navigator.Open(new SearchResult { Target = SearchTarget.Entry, Entry = p.Entry, Payload = p.Item ?? p.Label, Pad = p.Pad }, takeFocus: false, ring: false);
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
        /// button, a device's row, a profile's row, a controller's or page's
        /// sidebar entry, a dialog, or a menu item's button.</summary>
        internal FrameworkElement ElementOf(string id)
        {
            Place p;
            try { p = Resolve(id, Places(otherControllers: false)); }
            catch (AssistantRefusal) { return null; }
            if (p.LiveItem is { } item) return item.Element?.Invoke();
            if (p.Entry == null && p.Live == null) return p.Root;
            if (p.Live != null)
            {
                switch (p.Live.Kind)
                {
                    case LiveKind.Page: return _parts.NavItem?.Invoke(p.Live.Payload as string);
                    case LiveKind.Controller: return _parts.NavItem?.Invoke("Pad" + ((int)p.Live.Payload + 1).ToString(CultureInfo.InvariantCulture));
                    case LiveKind.Device: return RowElement(_pageOf(SearchPage.Devices), p.Live.Payload);
                    case LiveKind.Profile: return RowElement(_pageOf(SearchPage.Profiles), p.Live.Payload);
                    default: return null;
                }
            }
            if (p.Entry.Kind == SearchKind.Dialog) return p.Root;
            if (p.Entry.Kind == SearchKind.Section && _pageOf(SearchPage.Pad) is PadPage pad) return pad.SectionButton(p.Entry.Section);
            if (p.Entry.Kind == SearchKind.Extra && _pageOf(SearchPage.Extras) is ExtrasPage extras)
                return extras.FindName(p.Entry.ExtraKey + "Tile") as FrameworkElement;
            if (p.Entry.Kind == SearchKind.MenuItem)
            {
                var root = _pageOf(p.Entry.Page) ?? p.Root;
                string header = SearchLocator.LabelText(p.Entry.Label, null);
                return root == null ? null : SearchLocator.LogicalDescendants(root).OfType<FrameworkElement>()
                    .FirstOrDefault(e => e.IsVisible && e.ContextMenu is { } m && m.Items.OfType<MenuItem>().Any(i => string.Equals(i.Header as string, header, StringComparison.CurrentCulture)));
            }
            if (p.Entry.Page is SearchPage.Pad or SearchPage.Settings or SearchPage.Extras or SearchPage.Devices or SearchPage.Profiles)
            {
                _service.Locator.Reset();
                var label = p.Entry.ItemsPath != null ? _service.Locator.FindInItem(p.Entry, p.Item, _service.Index) : _service.Locator.Find(p.Entry, _service.Index);
                return label == null ? null : SearchLocator.TargetOf(label, p.Entry);
            }
            return Target(p);
        }

        private static FrameworkElement RowElement(FrameworkElement page, object item)
        {
            if (page == null || item == null) return null;
            foreach (var list in SearchLocator.VisualDescendants(page).OfType<ItemsControl>())
                if (list.IsVisible && list.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement row && row.IsVisible) return row;
            return null;
        }

        /// <summary>The clicks on the way to a place, outermost first: the
        /// sidebar entry, the controller's section or the extra's tile, and
        /// each More that folds it. A click already made is skipped when the
        /// walk comes to it.</summary>
        private IEnumerable<AssistantPointer.Step> WalkTo(Place p)
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            string tag = null;
            if (p.Entry != null)
            {
                tag = p.Entry.Page switch
                {
                    SearchPage.Pad => p.Pad == null ? null : "Pad" + (p.Pad.PadIndex + 1).ToString(CultureInfo.InvariantCulture),
                    SearchPage.Settings => "Settings",
                    SearchPage.Extras => "Extras",
                    SearchPage.Devices => "Devices",
                    SearchPage.Profiles => "Profiles",
                    SearchPage.Home => "Home",
                    SearchPage.About => "About",
                    _ => null,
                };
            }
            else if (p.Live?.Kind is LiveKind.Device) tag = "Devices";
            else if (p.Live?.Kind is LiveKind.Profile) tag = "Profiles";
            else if (p.Live?.Kind is LiveKind.Plugin) tag = "Extras";
            var steps = new List<AssistantPointer.Step>();
            if (tag != null)
            {
                string nav = tag;
                string name = tag.StartsWith("Pad", StringComparison.Ordinal) ? SearchService.SlotName(p.Pad) : PageName(PageOfTag(tag));
                steps.Add(new AssistantPointer.Step
                {
                    Words = string.Format(CultureInfo.CurrentCulture, s.Automation_Pointer_Open_Format, name),
                    Advance = AssistantAdvance.Click,
                    Done = () => (_parts.NavTag?.Invoke() ?? _vm.SelectedNavTag) == nav,
                    Find = () => _parts.NavItem?.Invoke(nav),
                });
            }
            if (p.Entry?.Page == SearchPage.Pad && p.Entry.Section >= 0 && p.Pad != null)
            {
                int section = p.Entry.Section;
                var pad = p.Pad;
                steps.Add(new AssistantPointer.Step
                {
                    Words = string.Format(CultureInfo.CurrentCulture, s.Automation_Pointer_Open_Format,
                        (_pageOf(SearchPage.Pad) as PadPage)?.SectionName(section) ?? string.Empty),
                    Advance = AssistantAdvance.Click,
                    Done = () => PadViewModel.SectionOfTab(pad.SelectedConfigTab) == section,
                    Find = () => (_pageOf(SearchPage.Pad) as PadPage)?.SectionButton(section),
                });
            }
            if (p.Entry?.Page == SearchPage.Extras && p.Entry.ExtraKey != null && p.Entry.Kind != SearchKind.Extra)
            {
                string key = p.Entry.ExtraKey;
                var tile = _service.Index.FirstOrDefault(e => e.Kind == SearchKind.Extra && e.ExtraKey == key);
                steps.Add(new AssistantPointer.Step
                {
                    Words = string.Format(CultureInfo.CurrentCulture, s.Automation_Pointer_Open_Format, tile == null ? key : SearchLocator.LabelText(tile.Label, null)),
                    Advance = AssistantAdvance.Click,
                    Done = () => (_pageOf(SearchPage.Extras) as ExtrasPage)?.CurrentExtra == key,
                    Find = () => _pageOf(SearchPage.Extras)?.FindName(key + "Tile") as FrameworkElement,
                });
            }
            if (p.Entry?.Page == SearchPage.Settings && _pageOf(SearchPage.Settings) is SettingsPage settingsPage)
            {
                // The Settings page shows one group at a time: its group's
                // button on the page's left.
                _service.Locator.Reset();
                var label = p.Entry.ItemsPath == null && !p.Entry.RowOptions ? _service.Locator.Find(p.Entry, _service.Index) : null;
                if (label != null && settingsPage.GroupOf(label) is { } group && settingsPage.DataContext is SettingsViewModel settingsVm)
                {
                    steps.Add(new AssistantPointer.Step
                    {
                        Words = string.Format(CultureInfo.CurrentCulture, s.Automation_Pointer_Open_Format, settingsPage.GroupName(group)),
                        Advance = AssistantAdvance.Click,
                        Done = () => settingsVm.SelectedSettingsGroup == group && string.IsNullOrEmpty(settingsVm.SettingsSearchText),
                        Find = () => settingsPage.GroupButton(group),
                    });
                }
            }
            if (p.Entry != null && p.Entry.UnderMore)
            {
                string id = p.Id;
                steps.Add(new AssistantPointer.Step
                {
                    Words = s.Automation_Pointer_OpenMore,
                    Advance = AssistantAdvance.Click,
                    Done = () => ClosedFold(ElementOf(id)) == null,
                    Find = () => ClosedFold(ElementOf(id)),
                });
            }
            return steps;
        }

        /// <summary>The outermost More or fold card that folds an element
        /// away, or null when none does.</summary>
        private static FrameworkElement ClosedFold(FrameworkElement element)
        {
            FrameworkElement outer = null;
            for (var at = element == null ? null : SearchLocator.Up(element); at != null; at = SearchLocator.Up(at))
                if (at is PadForge.Controls.FoldPart fold && !fold.IsExpanded) outer = fold;
            return outer;
        }
    }
}
