using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PadForge.Controls;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Views
{
    public partial class SettingsPage : UserControl
    {
        /// <summary>Static InputService reference wired by MainWindow at
        /// startup, same shape as the other pages.</summary>
        public static PadForge.Services.InputService InputService { get; set; }

        /// <summary>The line icon of a group's button, drawn by the
        /// button's template (#574, the world-class pass).</summary>
        public static readonly DependencyProperty GroupIconProperty = DependencyProperty.RegisterAttached(
            "GroupIcon", typeof(System.Windows.Media.Geometry), typeof(SettingsPage), new PropertyMetadata(null));

        public static System.Windows.Media.Geometry GetGroupIcon(DependencyObject element)
            => (System.Windows.Media.Geometry)element.GetValue(GroupIconProperty);

        public static void SetGroupIcon(DependencyObject element, System.Windows.Media.Geometry value)
            => element.SetValue(GroupIconProperty, value);

        private SettingsViewModel _vm;

        /// <summary>The folds and More rows the search opened, closed again
        /// when it moves on or ends.</summary>
        private readonly List<FoldPart> _openedForSearch = new();

        /// <summary>Set while the code checks the group buttons, so their
        /// Checked does not read as a click.</summary>
        private bool _syncing;

        private bool _applyQueued;

        public SettingsPage()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            // Leaving the page empties the search box.
            IsVisibleChanged += OnIsVisibleChanged;
            // Held weakly by Strings, so no unsubscribe is owed.
            Strings.CultureChanged += OnCultureChanged;
        }

        /// <summary>The five groups in their order: each group's panel, its
        /// heading for the search and its button.</summary>
        private (SettingsGroup Group, StackPanel Panel, TextBlock Heading, RadioButton Button)[] Groups => new[]
        {
            (SettingsGroup.General, GeneralGroup, GeneralHeading, GroupGeneral),
            (SettingsGroup.Input, InputGroup, InputHeading, GroupInput),
            (SettingsGroup.Hardware, HardwareGroup, HardwareHeading, GroupHardware),
            (SettingsGroup.Drivers, DriversGroup, DriversHeading, GroupDrivers),
            (SettingsGroup.Data, DataGroup, DataHeading, GroupData),
        };

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_vm != null) _vm.PropertyChanged -= Vm_PropertyChanged;
            _vm = e.NewValue as SettingsViewModel;
            if (_vm != null) _vm.PropertyChanged += Vm_PropertyChanged;
            ApplyView();
        }

        private void Vm_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                QueueApply();
                return;
            }
            if (e.PropertyName is nameof(SettingsViewModel.SelectedSettingsGroup) or nameof(SettingsViewModel.SettingsSearchText))
            {
                ApplyView();
                SettingsScroll.ScrollToTop();
            }
            else if (SettingSearch.Words(_vm?.SettingsSearchText).Length > 0)
            {
                // A row whose gate opens mid-search, such as Show Legacy
                // Workshop Configs when its switch turns on, is filtered too.
                QueueApply();
            }
        }

        /// <summary>The words changed, and so did the line for no match.</summary>
        private void OnCultureChanged() => QueueApply();

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible && _vm != null) _vm.SettingsSearchText = string.Empty;
        }

        /// <summary>One view at a time on the dispatcher's quiet time.</summary>
        private void QueueApply()
        {
            if (_applyQueued) return;
            _applyQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ApplyView));
        }

        /// <summary>Shows the selected group while the box is empty, and the
        /// matching rows of every group under their headings while it holds a
        /// word. A fold the last search opened is closed first.</summary>
        private void ApplyView()
        {
            _applyQueued = false;
            foreach (var part in _openedForSearch) part.EndSearch();
            _openedForSearch.Clear();

            string query = _vm?.SettingsSearchText ?? string.Empty;
            string[] words = SettingSearch.Words(query);
            bool searching = words.Length > 0;
            var selected = _vm?.SelectedSettingsGroup ?? SettingsGroup.General;
            int total = 0;
            bool first = true;
            foreach (var (group, panel, heading, _) in Groups)
            {
                if (!searching)
                {
                    SettingSearch.Clear(panel);
                    heading.Visibility = Visibility.Collapsed;
                    panel.Visibility = group == selected ? Visibility.Visible : Visibility.Collapsed;
                    continue;
                }
                int shown = SettingSearch.Filter(panel, words, CultureInfo.CurrentUICulture, _openedForSearch);
                total += shown;
                panel.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
                heading.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (shown == 0) continue;
                // 12 from the card above makes the 28 of the Home board's
                // headings. The first has nothing above it.
                heading.Margin = first ? new Thickness(0, 0, 0, 12) : new Thickness(0, 16, 0, 12);
                first = false;
            }

            if (searching && total == 0)
            {
                SearchNoneLine.Text = string.Format(CultureInfo.CurrentUICulture,
                    Strings.Instance.Settings_SearchNone_Format, query.Trim());
                SearchNoneLine.Visibility = Visibility.Visible;
            }
            else
            {
                SearchNoneLine.Visibility = Visibility.Collapsed;
            }
            SyncGroupButtons(selected, searching);
        }

        /// <summary>The selected group's button reads checked while the box is
        /// empty. While it holds a word, none does.</summary>
        private void SyncGroupButtons(SettingsGroup selected, bool searching)
        {
            _syncing = true;
            try
            {
                foreach (var (group, _, _, button) in Groups)
                    button.IsChecked = !searching && group == selected;
            }
            finally
            {
                _syncing = false;
            }
        }

        /// <summary>A group button empties the box and opens its group. A UI
        /// Automation Select raises Checked too, so a screen reader and the
        /// capture script open a group the same way.</summary>
        private void Group_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncing || _vm == null || sender is not RadioButton { Tag: SettingsGroup group }) return;
            _vm.SettingsSearchText = string.Empty;
            _vm.SelectedSettingsGroup = group;
        }

        /// <summary>The group whose panel holds a part of the page, or null
        /// for a part outside the five groups.</summary>
        internal SettingsGroup? GroupOf(DependencyObject part)
        {
            for (var at = part; at != null; at = PadForge.Search.SearchLocator.Up(at))
                foreach (var (group, panel, _, _) in Groups)
                    if (ReferenceEquals(at, panel)) return group;
            return null;
        }

        /// <summary>A group's name, as its button and its heading read.</summary>
        internal string GroupName(SettingsGroup group)
        {
            foreach (var (candidate, panel, _, _) in Groups)
                if (candidate == group) return SettingSearch.GetGroup(panel);
            return null;
        }

        /// <summary>Whether a part shows by the page's own rules in its
        /// group. Which group is open is not counted, and neither is what the
        /// page's search box leaves out: Search (#574) finds a setting of any
        /// group and opens the group on the way to it. A row its own rule
        /// hides, such as one that shows only while a switch is on, does not
        /// show.</summary>
        internal bool ShowsByItsRules(DependencyObject part)
        {
            if (GroupOf(part) == null) return PadForge.Search.SearchLocator.Shows(part, this);
            for (var at = part; at != null; at = PadForge.Search.SearchLocator.Up(at))
            {
                foreach (var (_, panel, _, _) in Groups)
                    if (ReferenceEquals(at, panel)) return true;
                // An item's own rule, whatever the search box says. A card
                // has no rule of its own: only the search box hides one.
                if (at is SettingItem item)
                {
                    if (item.OwnVisibility != Visibility.Visible) return false;
                }
                else if (SettingSearch.GetCard(at) == null && at is UIElement { Visibility: Visibility.Collapsed })
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Opens the group that holds a part and empties the page's
        /// search box, so the part shows. A landing from Search (#574) calls
        /// it before it scrolls to the part.</summary>
        internal void Reveal(DependencyObject part)
        {
            if (_vm == null || GroupOf(part) is not { } group) return;
            _vm.SettingsSearchText = string.Empty;
            _vm.SelectedSettingsGroup = group;
        }

        /// <summary>Ctrl+F puts the caret in the search box.</summary>
        private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.F || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            SettingsSearchBox.Focus();
            SettingsSearchBox.SelectAll();
            e.Handled = true;
        }

        /// <summary>Escape in the search box empties it.</summary>
        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || string.IsNullOrEmpty(SettingsSearchBox.Text)) return;
            SettingsSearchBox.Clear();
            e.Handled = true;
        }

        // Battery Alerts test (#293): fire the notification pipeline with a
        // synthetic low-battery event.
        private void TestBatteryNotify_Click(object sender, RoutedEventArgs e)
            => InputService?.TestBatteryNotification();

        // Diagnostics (#303): dump the in-memory engine event ring to a
        // timestamped file and reveal it in Explorer. Works whether or not
        // continuous logging is on, so a user can capture the moments
        // around a glitch after the fact.
        private void SaveDiagSnapshot_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = PadForge.Common.DiagnosticsLogControl.SaveSnapshot();
                PadForge.Common.ShellLaunch.Start("explorer.exe", $"/select,\"{path}\"");
            }
            catch
            {
                // A full disk or a broken shell association must not take
                // the settings page down.
            }
        }

        private void OpenDiagFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(PadForge.Common.DiagnosticsLogControl.Folder);
                PadForge.Common.ShellLaunch.Start("explorer.exe",
                    PadForge.Common.DiagnosticsLogControl.Folder);
            }
            catch
            {
                // A broken shell association must not take the settings
                // page down. The folder is also the button's tooltip.
            }
        }

        // Pick the Steam-free SteamVR install directory (#49). Same
        // OpenFolderDialog shape the macro working-dir browse uses.
        private void SteamVrBrowse_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not SettingsViewModel vm) return;
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = Strings.Instance.Settings_SteamVRInstallDir,
            };
            try
            {
                if (!string.IsNullOrEmpty(vm.SteamVrInstallDir)
                    && System.IO.Directory.Exists(vm.SteamVrInstallDir))
                    dlg.InitialDirectory = vm.SteamVrInstallDir;
            }
            catch { }
            if (dlg.ShowDialog() == true)
                vm.SteamVrInstallDir = dlg.FolderName;
        }
    }
}
