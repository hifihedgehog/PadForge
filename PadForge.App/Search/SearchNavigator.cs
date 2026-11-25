using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PadForge.Controls;
using PadForge.ViewModels;
using PadForge.Views;

namespace PadForge.Search
{
    /// <summary>
    /// Takes a result to its place (#574) and never runs it: the page opens
    /// on it, the More row or fold card it sits in opens, it is scrolled to
    /// 96 under the top of its column, ringed for a moment, and its input
    /// takes the keyboard focus. Add Controller opens Add a Controller, as
    /// its sidebar entry does, and the documentation result opens the
    /// documentation site's search.
    /// </summary>
    internal sealed class SearchNavigator
    {
        private readonly SearchService _service;
        private readonly Func<SearchPage, FrameworkElement> _pageOf;
        private readonly MainViewModel _vm;
        private readonly Action<string> _selectNav;
        private readonly Action<int> _openSlot;
        private readonly Action _openAddController;

        internal SearchNavigator(SearchService service, Func<SearchPage, FrameworkElement> pageOf, MainViewModel vm,
            Action<string> selectNav, Action<int> openSlot, Action openAddController)
        {
            _service = service;
            _pageOf = pageOf;
            _vm = vm;
            _selectNav = selectNav;
            _openSlot = openSlot;
            _openAddController = openAddController;
        }

        /// <summary>The documentation site's search for a query.</summary>
        internal static string DocsAddress(string query) => "https://padforge.org/docs/?q=" + Uri.EscapeDataString((query ?? string.Empty).Trim());

        /// <summary>Takes a result to its place. With
        /// <paramref name="takeFocus"/> false the place is opened, scrolled to
        /// and ringed, and the keyboard focus stays where it is: an
        /// assistant's show (#597), which must never activate the window,
        /// lands that way. With <paramref name="ring"/> false nothing is
        /// ringed: the guided pointer (#600) rings and holds the place
        /// itself. A walk of the guided pointer never comes here: its user
        /// makes each click on the way (#600 item 7).</summary>
        internal void Open(SearchResult result, bool takeFocus = true, bool ring = true)
        {
            if (result == null) return;
            switch (result.Target)
            {
                case SearchTarget.Page:
                    _selectNav((string)result.Payload);
                    return;
                case SearchTarget.Controller:
                    _openSlot((int)result.Payload);
                    return;
                case SearchTarget.AddController:
                    _openAddController();
                    return;
                case SearchTarget.Device:
                    _selectNav("Devices");
                    ShowDevice(result.Payload as DeviceRowViewModel);
                    return;
                case SearchTarget.Profile:
                    _selectNav("Profiles");
                    ShowProfile(result.Payload as ProfileListItem);
                    return;
                case SearchTarget.Macro:
                {
                    // A controller's macro (#600): its page, Macros & Menus,
                    // with the macro picked.
                    if (result.Payload is not ValueTuple<int, MacroItem> macro) return;
                    _openSlot(macro.Item1);
                    if (_pageOf(SearchPage.Pad) is PadPage pad && pad.DataContext is PadViewModel padVm && padVm.PadIndex == macro.Item1)
                    {
                        pad.OpenSection(PadViewModel.MacrosSection);
                        padVm.SelectedMacro = macro.Item2;
                    }
                    return;
                }
                case SearchTarget.Plugin:
                    _selectNav("Extras");
                    if (_pageOf(SearchPage.Extras) is ExtrasPage plugins) plugins.Show("Plugins");
                    return;
                case SearchTarget.Docs:
                    try
                    {
                        PadForge.Common.ShellLaunch.Start(new ProcessStartInfo(DocsAddress(result.Payload as string)) { UseShellExecute = true });
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        // No program is registered for web pages, and the
                        // result does nothing, as a link on the About page
                        // does there.
                    }
                    return;
            }

            var entry = result.Entry;
            if (entry == null) return;
            switch (entry.Page)
            {
                case SearchPage.Pad:
                    // The controller the result stands on, which can be
                    // another than the page shows: opening its page binds
                    // the page to it.
                    var pad = _pageOf(SearchPage.Pad) as PadPage;
                    if (pad == null || (result.Pad ?? pad.DataContext as PadViewModel) is not PadViewModel padVm) return;
                    _selectNav("Pad" + (padVm.PadIndex + 1));
                    if (!ReferenceEquals(pad.DataContext, padVm)) return;
                    if (entry.Kind == SearchKind.Section) { pad.OpenSection(entry.Section); return; }
                    if (entry.Kind == SearchKind.Part) { pad.OpenTab(entry.Tab); return; }
                    if (entry.Tab >= 0) pad.OpenTab(entry.Tab);
                    break;
                case SearchPage.Settings:
                    _selectNav("Settings");
                    // The page shows one group at a time: the entry's group
                    // opens, and the page's own search box empties, before
                    // the landing looks for the entry's row.
                    if (_pageOf(SearchPage.Settings) is SettingsPage settings)
                    {
                        _service.Locator.Reset();
                        settings.Reveal(entry.ItemsPath != null ? _service.Locator.ListOf(entry) : _service.Locator.Find(entry, _service.Index));
                    }
                    break;
                case SearchPage.Extras:
                    _selectNav("Extras");
                    if (entry.ExtraKey != null && _pageOf(SearchPage.Extras) is ExtrasPage extras) extras.Show(entry.ExtraKey);
                    if (entry.Kind == SearchKind.Extra) return;
                    break;
                case SearchPage.Devices:
                    _selectNav("Devices");
                    break;
                case SearchPage.Profiles:
                    _selectNav("Profiles");
                    break;
            }
            var page = _pageOf(entry.Page);
            page?.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Land(result, page, takeFocus, ring)));
        }

        /// <summary>Opens a controller's mapping row for a program's plain
        /// show of one of the row's mouse settings (#600 item 7), as a
        /// landing on a row's option opens a row: the controller's page and
        /// its list, the row picked, which opens its details and its folded
        /// group (PadPage.SyncPickedRowSelection), and brought into view. A
        /// row the list's box or scope hides empties them, as the Settings
        /// page's box empties for a landing there.</summary>
        internal void OpenMappingRow(int padIndex, MappingItem row)
        {
            if (row == null) return;
            _openSlot(padIndex);
            if (_pageOf(SearchPage.Pad) is not PadPage pad || pad.DataContext is not PadViewModel vm || vm.PadIndex != padIndex) return;
            pad.OpenTab(PadViewModel.MappingsTabIndex);
            pad.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (pad.FindName("MappingDataGrid") is not DataGrid grid || !vm.Mappings.Contains(row)) return;
                if (!grid.Items.Contains(row))
                {
                    vm.MappingInputSearch = string.Empty;
                    vm.MappingSurfaceScope = "All";
                    if (!grid.Items.Contains(row)) return;
                }
                grid.SelectedItem = row;
                grid.ScrollIntoView(row);
            }));
        }

        /// <summary>The page shows: find the result's element, open what
        /// folds it, then scroll, ring and focus once that is laid out.</summary>
        private void Land(SearchResult result, FrameworkElement page, bool takeFocus, bool ring)
        {
            var entry = result.Entry;
            DependencyObject label;
            if (entry.RowOptions) label = RowOption(entry, page);
            else if (entry.Kind == SearchKind.MenuItem) label = MenuOwner(page, entry);
            else if (entry.ItemsPath != null) label = _service.Locator.FindInItem(entry, result.Payload, _service.Index);
            else
            {
                _service.Locator.Reset();
                label = _service.Locator.Find(entry, _service.Index);
            }
            if (label == null) return;
            var target = SearchLocator.TargetOf(label, entry);
            if (target == null) return;
            OpenFolds(target, page);
            page.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!target.IsVisible) return;
                ScrollTo(target);
                if (ring) SearchLandingAdorner.Ring(target);
                if (takeFocus) FocusIn(target, entry);
            }));
        }

        /// <summary>The button whose context menu holds a menu item's
        /// entry (#600): the menu's own element is not in the page until it
        /// opens, so the landing rings what opens it.</summary>
        private static DependencyObject MenuOwner(FrameworkElement page, SearchEntry entry) => MenuItemOf(page, entry).Owner;

        /// <summary>
        /// A menu item's entry as the page shows it now: the item, and the
        /// shown element whose menu holds it. Several menus hold items of
        /// the same words: each Paste of the controller page, and the Paste
        /// of a text box's own menu. An item bound to another command than
        /// the entry's is another item. Of the rest, the first whose button
        /// sits in the entry's scope is taken, else the first.
        /// </summary>
        internal static (FrameworkElement Owner, MenuItem Item) MenuItemOf(FrameworkElement root, SearchEntry entry)
            => root == null ? (null, null) : MenuItemAmong(SearchLocator.LogicalDescendants(root).OfType<FrameworkElement>(), entry);

        /// <summary>A menu item's entry among the elements given: in the
        /// container a list drew for one item, for a menu each item has of its
        /// own (<see cref="SearchEntry.ItemsList"/>), whose elements a
        /// template made and the logical tree does not hold.</summary>
        internal static (FrameworkElement Owner, MenuItem Item) MenuItemIn(DependencyObject container, SearchEntry entry)
            => container == null ? (null, null) : MenuItemAmong(SearchLocator.VisualDescendants(container).OfType<FrameworkElement>(), entry);

        private static (FrameworkElement Owner, MenuItem Item) MenuItemAmong(System.Collections.Generic.IEnumerable<FrameworkElement> elements, SearchEntry entry)
        {
            string header = SearchLocator.LabelText(entry.Label, null);
            (FrameworkElement Owner, MenuItem Item) first = (null, null);
            foreach (var element in elements)
            {
                if (element.ContextMenu is not { } menu || !element.IsVisible) continue;
                foreach (var item in menu.Items.OfType<MenuItem>())
                {
                    if (!string.Equals(item.Header as string, header, StringComparison.CurrentCulture)) continue;
                    var command = System.Windows.Data.BindingOperations.GetBinding(item, MenuItem.CommandProperty);
                    if ((command != null || item.Command != null) && command?.Path?.Path != entry.Handler) continue;
                    if (entry.ScopeName == null || InScope(element, entry.ScopeName)) return (element, item);
                    if (first.Item == null) first = (element, item);
                }
            }
            return first;
        }

        /// <summary>Whether an element is the one of that name, or sits in
        /// it.</summary>
        private static bool InScope(FrameworkElement element, string name)
        {
            for (DependencyObject at = element; at != null; at = SearchLocator.Up(at))
                if (at is FrameworkElement { Name: { } own } && own == name) return true;
            return false;
        }

        /// <summary>Opens every More row and fold card between an element
        /// and <paramref name="stop"/>, as a click on each would, and gives
        /// back the ones it opened.</summary>
        internal static System.Collections.Generic.List<FoldPart> OpenFolds(DependencyObject target, DependencyObject stop)
        {
            var opened = new System.Collections.Generic.List<FoldPart>();
            for (var at = SearchLocator.Up(target); at != null && at != stop; at = SearchLocator.Up(at))
            {
                if (at is FoldPart fold && !fold.IsExpanded)
                {
                    fold.IsExpanded = true;
                    opened.Add(fold);
                }
            }
            return opened;
        }

        /// <summary>A row option's element: the first of the controller's
        /// rows whose item passes the option's gates is picked and shown, and
        /// its details hold the option. A row the table does not show opens
        /// the list, which holds every row, and the list's own box and scope
        /// empty, as the Settings page's box does for a landing there.</summary>
        private DependencyObject RowOption(SearchEntry entry, FrameworkElement page)
        {
            if (page.FindName("MappingDataGrid") is not DataGrid grid || page.DataContext is not PadViewModel vm) return null;
            var item = vm.RowsOfSlot.FirstOrDefault(i => entry.ItemGates.All(g => BindingProbe.Passes(i, g, k => page.TryFindResource(k))));
            if (item == null) return null;
            if (!grid.Items.Contains(item))
            {
                if (vm.IsButtonsPictureView && page is PadPage pad) pad.OpenTab(PadViewModel.MappingsTabIndex);
                vm.MappingInputSearch = string.Empty;
                vm.MappingSurfaceScope = "All";
                if (!grid.Items.Contains(item)) return null;
            }
            grid.SelectedItem = item;
            grid.ScrollIntoView(item);
            grid.UpdateLayout();
            if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row) return null;
            row.DetailsVisibility = Visibility.Visible;
            row.UpdateLayout();
            var parts = RowOptionParts(row).ToList();
            return SearchLocator.FindAmong(parts, entry, item, _service.Index, e => e.RowOptions);
        }

        /// <summary>The parts of an open row an option's label is looked for
        /// among: what the index reads, in the same order. That is the row's
        /// body with its first source and its output. The row's line over
        /// the body holds no option, and its tags can say an option's word
        /// ("Up", "Down"). The added sources' lines repeat the first
        /// source's words, so the Nth entry of a word would land on one of
        /// them.</summary>
        internal static System.Collections.Generic.IEnumerable<DependencyObject> RowOptionParts(DependencyObject root)
        {
            int count = root is Visual || root is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(root) : 0;
            for (int i = 0; i < count; i++)
            {
                var part = VisualTreeHelper.GetChild(root, i);
                if (part is FrameworkElement { Name: RowLineName or AddedSourcesName }) continue;
                yield return part;
                foreach (var below in RowOptionParts(part)) yield return below;
            }
        }

        /// <summary>The names the controller page gives a row's line and
        /// the list of a row's added sources.</summary>
        internal const string RowLineName = "RowLine";
        internal const string AddedSourcesName = "AddedSources";

        /// <summary>Scrolls the nearest scroll viewer so the element's top is
        /// 96 under the viewport's top, as far as the viewer goes.</summary>
        internal static void ScrollTo(FrameworkElement target)
        {
            ScrollViewer scroll = null;
            for (DependencyObject at = VisualTreeHelper.GetParent(target); at != null; at = VisualTreeHelper.GetParent(at))
                if (at is ScrollViewer sv && sv.ScrollableHeight > 0) { scroll = sv; break; }
            if (scroll == null) return;
            double y = target.TranslatePoint(new Point(0, 0), scroll).Y;
            scroll.ScrollToVerticalOffset(Math.Max(0, Math.Min(scroll.ScrollableHeight, scroll.VerticalOffset + y - 96)));
        }

        /// <summary>The keyboard focus on the row's input: the one the index
        /// names, else the first that takes focus. An action's button takes
        /// it itself. A card takes none.</summary>
        private static void FocusIn(FrameworkElement target, SearchEntry entry)
        {
            if (entry.Kind == SearchKind.Card) return;
            if (entry.Kind == SearchKind.Action)
            {
                if (target.Focusable && target.IsEnabled) target.Focus();
                return;
            }
            var parts = SearchLocator.VisualDescendants(target).OfType<FrameworkElement>().ToList();
            FrameworkElement input = entry.TargetAutomationId == null ? null
                : parts.FirstOrDefault(p => AutomationProperties.GetAutomationId(p) == entry.TargetAutomationId);
            input ??= parts.FirstOrDefault(p => p is RangeBase or Selector or TextBoxBase or ToggleButton && p.Focusable && p.IsEnabled && p.IsVisible
                                                 && p is not SettingResetButton);
            if (input != null && input.Focusable && input.IsEnabled) input.Focus();
        }

        /// <summary>Opens a device's panel on the Devices page. The page
        /// knows its three lists, its segment and its search box, so it
        /// does the showing (DevicesPage.ShowDevice).</summary>
        private void ShowDevice(DeviceRowViewModel row)
        {
            if (row == null) return;
            if (_pageOf(SearchPage.Devices) is DevicesPage page) page.ShowDevice(row);
            else if (_vm.Devices != null) _vm.Devices.SelectedDevice = row;
        }

        /// <summary>Picks a profile on the Profiles page and brings it into
        /// view.</summary>
        private void ShowProfile(ProfileListItem profile)
        {
            if (profile == null || _vm.Settings == null) return;
            _vm.Settings.SelectedProfile = profile;
            if (_pageOf(SearchPage.Profiles)?.FindName("ProfileListBox") is ListBox list)
                list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => list.ScrollIntoView(profile)));
        }
    }

    /// <summary>The ring a landing draws around its row or card (#574): 2 px
    /// of ember, radius 8, 4 outside the element. It holds 1.2 s and fades
    /// over 0.4 s, or shows 1.6 s and goes when Windows asks for no
    /// animation.</summary>
    internal sealed class SearchLandingAdorner : Adorner
    {
        private readonly Pen _pen;

        private SearchLandingAdorner(UIElement adorned, Brush brush) : base(adorned)
        {
            IsHitTestVisible = false;
            _pen = new Pen(brush, 2);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var size = AdornedElement.RenderSize;
            dc.DrawRoundedRectangle(null, _pen, new Rect(-4, -4, size.Width + 8, size.Height + 8), 8, 8);
        }

        /// <summary>Search's ring held on an element until
        /// <see cref="Release"/> (#600): the guided pointer's. Null when the
        /// element sits in no adorner layer.</summary>
        internal static SearchLandingAdorner Hold(FrameworkElement target)
        {
            var layer = AdornerLayer.GetAdornerLayer(target);
            if (layer == null) return null;
            var ring = new SearchLandingAdorner(target, target.TryFindResource("EmberBrush") as Brush ?? Brushes.OrangeRed);
            layer.Add(ring);
            return ring;
        }

        /// <summary>Takes a held ring away.</summary>
        internal void Release() => AdornerLayer.GetAdornerLayer(AdornedElement)?.Remove(this);

        internal static void Ring(FrameworkElement target)
        {
            var layer = AdornerLayer.GetAdornerLayer(target);
            if (layer == null) return;
            var brush = target.TryFindResource("EmberBrush") as Brush ?? Brushes.OrangeRed;
            var ring = new SearchLandingAdorner(target, brush);
            layer.Add(ring);
            void Remove() => layer.Remove(ring);
            if (SystemParameters.ClientAreaAnimation)
            {
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400)) { BeginTime = TimeSpan.FromMilliseconds(1200) };
                fade.Completed += (_, _) => Remove();
                ring.BeginAnimation(OpacityProperty, fade);
            }
            else
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
                timer.Tick += (_, _) => { timer.Stop(); Remove(); };
                timer.Start();
            }
        }
    }
}
