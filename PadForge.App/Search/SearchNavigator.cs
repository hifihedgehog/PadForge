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

        internal void Open(SearchResult result)
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
                case SearchTarget.Docs:
                    PadForge.Common.ShellLaunch.Start(new ProcessStartInfo(DocsAddress(result.Payload as string)) { UseShellExecute = true });
                    return;
            }

            var entry = result.Entry;
            if (entry == null) return;
            switch (entry.Page)
            {
                case SearchPage.Pad:
                    var pad = _pageOf(SearchPage.Pad) as PadPage;
                    if (pad?.DataContext is not PadViewModel padVm) return;
                    _selectNav("Pad" + (padVm.PadIndex + 1));
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
            page?.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Land(result, page)));
        }

        /// <summary>The page shows: find the result's element, open what
        /// folds it, then scroll, ring and focus once that is laid out.</summary>
        private void Land(SearchResult result, FrameworkElement page)
        {
            var entry = result.Entry;
            DependencyObject label;
            if (entry.RowOptions) label = RowOption(entry, page);
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
                SearchLandingAdorner.Ring(target);
                FocusIn(target, entry);
            }));
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

        /// <summary>A row option's element: the first mapping row whose item
        /// passes the option's gates is picked and shown, and its details
        /// hold the option.</summary>
        private DependencyObject RowOption(SearchEntry entry, FrameworkElement page)
        {
            if (page.FindName("MappingDataGrid") is not DataGrid grid) return null;
            var item = grid.Items.Cast<object>().FirstOrDefault(i => entry.ItemGates.All(g => BindingProbe.Passes(i, g, k => page.TryFindResource(k))));
            if (item == null) return null;
            grid.SelectedItem = item;
            grid.ScrollIntoView(item);
            grid.UpdateLayout();
            if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row) return null;
            row.DetailsVisibility = Visibility.Visible;
            row.UpdateLayout();
            var parts = SearchLocator.VisualDescendants(row).ToList();
            return SearchLocator.FindAmong(parts, entry, item, _service.Index, e => e.RowOptions);
        }

        /// <summary>Scrolls the nearest scroll viewer so the element's top is
        /// 96 under the viewport's top, as far as the viewer goes.</summary>
        private static void ScrollTo(FrameworkElement target)
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
