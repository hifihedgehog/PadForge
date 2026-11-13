using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NavigationView = Wpf.Ui.Controls.NavigationView;
using NavigationViewItem = Wpf.Ui.Controls.NavigationViewItem;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    /// <summary>The Devices page (#574): a title with the count of devices
    /// connected, a search and the Show segments, one list card (a table of
    /// the devices connected now, then the other inputs and the ones
    /// PadForge remembers, each folded to a line), and the selected device's
    /// panel of four cards: the device's picture, facts and the controllers
    /// that use it, its test, its settings and tools, and its details. Wide, the list and the panel scroll
    /// on their own. Narrower than the two need, the panel stands under the
    /// list and the page scrolls whole.</summary>
    public partial class DevicesPage : UserControl
    {
        /// <summary>Static InputService reference wired by MainWindow at
        /// startup so the per-device calibrate-gyro button can reach the
        /// shared <see cref="PadForge.Services.GyroCalibratorService"/>
        /// instance and resolve the selected UserDevice. Same pattern as
        /// PadPage.Recorder.</summary>
        public static PadForge.Services.InputService InputService { get; set; }

        public DevicesPage()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            SizeChanged += (_, e) => ApplyLayout(IsStacked(e.NewSize.Width));
            IsVisibleChanged += OnIsVisibleChanged;
            // A list's rows change when its view arrives through the
            // binding, when the segment or the search filters it, and when
            // a row moves to another list. The lists live as long as the
            // page.
            foreach (var list in new[] { ConnectedList, NotConnectedList, OtherInputsList })
                ((INotifyCollectionChanged)list.Items).CollectionChanged += OnListViewChanged;
            // The Used By chips are numbered each time their containers are made.
            UsedByChips.ItemContainerGenerator.StatusChanged += (_, _) =>
            {
                if (UsedByChips.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                    NumberUsedBy();
            };
        }

        /// <summary>Numbers the Used By chips for a screen reader: a chip's
        /// place among the chips and how many there are, the New Controller
        /// button among them while it shows. The label that leads the chips
        /// is one of the list's items, so that it wraps with them, and is
        /// not counted.</summary>
        internal void NumberUsedBy()
        {
            bool canAdd = (DataContext as ViewModels.DevicesViewModel)?.CanAddController == true;
            PadForge.Common.ListItemPeers.Number(UsedByChips,
                item => !ReferenceEquals(item, UsedByLabel) && (canAdd || !ReferenceEquals(item, NewControllerChip.Parent)));
        }

        /// <summary>Raised by Use It on a row and by New Controller under Used
        /// By, with the device and the button that asked. MainWindow answers
        /// by opening Add a Controller with the device picked to drive the
        /// new controller, as for Home's offer.</summary>
        public event EventHandler<(Guid Device, FrameworkElement Anchor)> NewControllerRequested;

        // ── Layout ──

        internal const double PageSide = 36, PageFoot = 36, ListMin = 520, BodyGap = 16, PanelWidth = 372;

        /// <summary>The board's wrap: the list (basis 520) and the panel (372)
        /// share a line while the content, the page less 36 a side, holds both
        /// and the 16 between.</summary>
        internal static bool IsStacked(double pageWidth) => pageWidth - 2 * PageSide < ListMin + BodyGap + PanelWidth;

        /// <summary>The search box's width: basis 220, at most 320. Beside the
        /// segments it takes what the line leaves, and alone on its line it
        /// takes the line, up to 320.</summary>
        internal static double SearchWidth(double rowWidth, double segmentWidth)
            => rowWidth >= 220 + 16 + segmentWidth ? Math.Min(320, rowWidth - 16 - segmentWidth) : Math.Min(320, Math.Max(0, rowWidth));

        /// <summary>The idle row's unit width. The label with the box (the
        /// label's text, 12 and the box's 60) and the unit start at the widths
        /// their text asks, beside 10 either side of the unit and the reset's
        /// 32. What the row lacks comes off the unit first, down to two lines
        /// of its text, so the label stays on one line: in the panel's 338 the
        /// row lacks 14, and sharing that between the two wrapped both
        /// (seen 2026-11-13). What it still lacks then comes off the two in
        /// proportion to their widths. The unit is never wider than its
        /// text.</summary>
        internal static double IdleUnitWidth(double rowWidth, double labelText, double unitText)
        {
            double lead = labelText + 12 + 60;
            double over = lead + unitText - (rowWidth - 10 - 10 - 32);
            if (over <= 0) return unitText;
            // Two lines of the unit's text, with a little for a word that
            // ends past the middle.
            double twoLines = unitText / 2 + 8;
            double first = Math.Min(over, Math.Max(0, unitText - twoLines));
            double unit = unitText - first;
            double still = over - first;
            return still <= 0 ? unit : Math.Max(0, unit - still * unit / (lead + unit));
        }

        /// <summary>A text's width on one line in its block's face and size.</summary>
        private static double TextWidth(TextBlock block)
        {
            var text = new FormattedText(block.Text ?? string.Empty, System.Globalization.CultureInfo.CurrentUICulture,
                block.FlowDirection, new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
                block.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(block).PixelsPerDip);
            return Math.Ceiling(text.WidthIncludingTrailingWhitespace);
        }

        /// <summary>The least width of the list card that holds Link and
        /// Battery as columns of the table: the columns and the row's
        /// insets take 446, which leaves a name 214.</summary>
        internal const double TableMin = 660;

        /// <summary>Whether a list card of this width shows Link and Battery
        /// as columns. Narrower, each row shows the two after its type.</summary>
        internal static bool IsWide(double listWidth) => listWidth >= TableMin;

        /// <summary>Whether the list is wide enough for the table's Link and
        /// Battery columns. The list card sets it by its own width, and
        /// every row and the table's head read it.</summary>
        public static readonly DependencyProperty IsWideTableProperty = DependencyProperty.RegisterAttached(
            "IsWideTable", typeof(bool), typeof(DevicesPage),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

        public static bool GetIsWideTable(DependencyObject element) => (bool)element.GetValue(IsWideTableProperty);

        public static void SetIsWideTable(DependencyObject element, bool value) => element.SetValue(IsWideTableProperty, value);

        /// <summary>The list card's width decides the table's form. The
        /// card's width is the page's, whatever its rows hold, so the form
        /// cannot change the width that chose it.</summary>
        private void ListCard_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.WidthChanged) return;
            bool wide = IsWide(e.NewSize.Width);
            if (GetIsWideTable(DeviceListCard) != wide) SetIsWideTable(DeviceListCard, wide);
        }

        private bool? _stacked;

        /// <summary>The two layouts. Wide, the list and the panel each scroll in
        /// their own column under the fixed title and filter row, and each
        /// scroll bar lies in a padding, never on a card. Stacked, the panel
        /// stands under the list and the body scrolls whole.</summary>
        private void ApplyLayout(bool stacked)
        {
            if (_stacked == stacked) return;
            _stacked = stacked;
            BodyScroll.VerticalScrollBarVisibility = stacked ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            BodyScroll.Padding = stacked ? new Thickness(PageSide, 0, PageSide, PageFoot) : new Thickness(0);
            PanelColumn.Width = new GridLength(stacked ? 0 : PanelWidth + PageSide);
            ListRow.Height = stacked ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            StackGapRow.Height = new GridLength(stacked ? BodyGap : 0);
            PanelRow.Height = stacked ? GridLength.Auto : new GridLength(0);
            ListScroll.VerticalScrollBarVisibility = stacked ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            ListScroll.Padding = stacked ? new Thickness(0) : new Thickness(PageSide, 0, BodyGap, PageFoot);
            Grid.SetRow(PanelScroll, stacked ? 2 : 0);
            Grid.SetColumn(PanelScroll, stacked ? 0 : 1);
            PanelScroll.VerticalScrollBarVisibility = stacked ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            PanelScroll.Padding = stacked ? new Thickness(0) : new Thickness(0, 0, PageSide, PageFoot);
        }

        private void FilterRow_SizeChanged(object sender, SizeChangedEventArgs e)
            => DeviceSearchBox.Width = SearchWidth(FilterRow.ActualWidth, ShowSegments.ActualWidth);

        private void IdleRow_SizeChanged(object sender, SizeChangedEventArgs e) => ShareIdleRow(e.NewSize.Width);

        /// <summary>A new language changes the texts and not the row's width.</summary>
        private void IdleRow_TextUpdated(object sender, DataTransferEventArgs e) => ShareIdleRow(IdleRow.ActualWidth);

        private void ShareIdleRow(double rowWidth)
        {
            if (rowWidth <= 0) return;
            double unit = TextWidth(IdleUnitText);
            double share = IdleUnitWidth(rowWidth, TextWidth(IdleLabel), unit);
            IdleUnitText.Width = share >= unit ? double.NaN : share;
        }

        // ── The lists ──

        /// <summary>A Show segment's Tag is the filter it picks.</summary>
        private void Filter_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement segment && segment.Tag is string tag
                && Enum.TryParse(tag, out ViewModels.DeviceFilter filter)
                && DataContext is ViewModels.DevicesViewModel vm)
                vm.Filter = filter;
        }

        private bool _syncingSelection;

        /// <summary>A pick in any of the three lists is the selected device.
        /// In the stacked layout the panel's first card comes into view.</summary>
        private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingSelection || DataContext is not ViewModels.DevicesViewModel vm) return;
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is ViewModels.DeviceRowViewModel row)
            {
                vm.SelectedDevice = row;
                if (_stacked == true) IdentityCard.BringIntoView();
            }
        }

        /// <summary>The three lists share one selection, the view model's: the
        /// list that holds the selected device shows it picked, and the others
        /// show nothing picked. A list's SelectedItem is set here, never bound
        /// two ways.</summary>
        private void SyncListSelection()
        {
            if (DataContext is not ViewModels.DevicesViewModel vm) return;
            _syncingSelection = true;
            try
            {
                foreach (var list in new[] { ConnectedList, NotConnectedList, OtherInputsList })
                {
                    object want = vm.SelectedDevice != null && list.Items.Contains(vm.SelectedDevice) ? vm.SelectedDevice : null;
                    if (!ReferenceEquals(list.SelectedItem, want)) list.SelectedItem = want;
                }
            }
            finally
            {
                _syncingSelection = false;
            }
        }

        /// <summary>Brings a device to the page, for a search result that
        /// names it (#574): the segment and the search box stop hiding it,
        /// it becomes the selected device, the folded list that holds it
        /// opens, and its row comes into view.</summary>
        internal void ShowDevice(ViewModels.DeviceRowViewModel row)
        {
            if (row == null || DataContext is not ViewModels.DevicesViewModel vm) return;
            vm.EnsureListViews();
            if (ListOf(row) == null)
            {
                vm.SearchText = string.Empty;
                vm.Filter = ViewModels.DeviceFilter.All;
            }
            vm.SelectedDevice = row;
            // The lists take a changed filter at the next layout, so the
            // row is found then.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var list = ListOf(row);
                if (list == null) return;
                if (ReferenceEquals(list, NotConnectedList)) NotConnectedFold.IsExpanded = true;
                else if (ReferenceEquals(list, OtherInputsList)) OtherInputsFold.IsExpanded = true;
                SyncListSelection();
                list.UpdateLayout();
                list.ScrollIntoView(row);
            }));
        }

        /// <summary>The list that holds a device's row now, or null while the
        /// segment or the search hides it.</summary>
        private ListBox ListOf(ViewModels.DeviceRowViewModel row)
        {
            foreach (var list in new[] { ConnectedList, NotConnectedList, OtherInputsList })
                if (list.Items.Contains(row)) return list;
            return null;
        }

        /// <summary>A list's rows changed: its view arrived, the segment or the
        /// search filtered it, or a row moved to another list and left its old
        /// list's pick behind. The picks are set again once the lists have
        /// their rows.</summary>
        private void OnListViewChanged(object sender, NotifyCollectionChangedEventArgs e)
            => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(SyncListSelection));

        /// <summary>The page shown with no device selected selects the first
        /// connected one, as the board draws a selected device.</summary>
        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is not true || DataContext is not ViewModels.DevicesViewModel vm
                || vm.SelectedDevice != null || vm.ConnectedView == null)
                return;
            foreach (object item in vm.ConnectedView)
            {
                if (item is ViewModels.DeviceRowViewModel row)
                {
                    vm.SelectedDevice = row;
                    return;
                }
            }
        }

        /// <summary>Use It on a row: the type bar under the button, and the
        /// controller made there takes the row's device.</summary>
        private void UseIt_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement button && button.DataContext is ViewModels.DeviceRowViewModel row)
                NewControllerRequested?.Invoke(this, (row.InstanceGuid, button));
        }

        /// <summary>New Controller under Used By, for the selected device.</summary>
        private void NewController_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement button && DataContext is ViewModels.DevicesViewModel { SelectedDevice: { } row })
                NewControllerRequested?.Invoke(this, (row.InstanceGuid, button));
        }

        /// <summary>The full readout is the Test Inputs card's body, or for a
        /// device in the gamepad layout it stands under More, below the
        /// compact test. Its parts keep their names, so the dot updaters and
        /// the MIDI previews reach them wherever it stands.</summary>
        private void PlaceRawView(bool gamepad)
        {
            Border host = gamepad ? RawMoreHost : RawPlainHost;
            if (ReferenceEquals(RawFullView.Parent, host)) return;
            if (RawFullView.Parent is Border old) old.Child = null;
            host.Child = RawFullView;
        }

        // v3.3 — gyro UI moved to the Pad page's Gyro tab. The Calibrate
        // / Reset buttons + tuning sliders + live rate readout live there
        // now, gated per-(device, slot) like FFB / Adaptive Triggers /
        // Lighting. The Devices page intentionally has no gyro UI: it's
        // the device-discovery surface, not the binding-config surface.

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ViewModels.DevicesViewModel oldVm)
                oldVm.PropertyChanged -= OnVmPropertyChanged;
            if (e.NewValue is ViewModels.DevicesViewModel newVm)
            {
                newVm.PropertyChanged += OnVmPropertyChanged;
                // The views are made here, on the page's thread.
                newVm.EnsureListViews();
                // A CollectionContainer is outside the tree, so a binding could
                // not reach the chips: they are handed over here, as Home hands
                // its controller cards.
                UsedByContainer.Collection = newVm.ActiveSlotItems;
                PlaceRawView(newVm.ShowGamepadTest);
                SyncListSelection();
            }
        }

        private void OnVmPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.DevicesViewModel.SelectedDevice))
            {
                SyncListSelection();
                return;
            }
            if (e.PropertyName == nameof(ViewModels.DevicesViewModel.ShowGamepadTest))
            {
                if (DataContext is ViewModels.DevicesViewModel shown)
                    PlaceRawView(shown.ShowGamepadTest);
                return;
            }
            if (e.PropertyName == nameof(ViewModels.DevicesViewModel.CanAddController))
            {
                NumberUsedBy();
                return;
            }
            if (e.PropertyName == nameof(ViewModels.DevicesViewModel.IsMidiDevice))
            {
                var vm = DataContext as ViewModels.DevicesViewModel;
                if (vm != null && vm.IsMidiDevice)
                {
                    MidiNotesPreview.BindInput(() => vm.LiveMidi, Views.MidiPreviewView.InputSection.Notes);
                    MidiCcPreview.BindInput(() => vm.LiveMidi, Views.MidiPreviewView.InputSection.Ccs);
                }
                else
                {
                    MidiNotesPreview.UnbindInput();
                    MidiCcPreview.UnbindInput();
                }
                return;
            }

            if (e.PropertyName is nameof(ViewModels.DevicesViewModel.TouchpadX0)
                              or nameof(ViewModels.DevicesViewModel.TouchpadY0)
                              or nameof(ViewModels.DevicesViewModel.TouchpadX1)
                              or nameof(ViewModels.DevicesViewModel.TouchpadY1)
                              or nameof(ViewModels.DevicesViewModel.TouchpadX2)
                              or nameof(ViewModels.DevicesViewModel.TouchpadY2)
                              or nameof(ViewModels.DevicesViewModel.TouchpadX3)
                              or nameof(ViewModels.DevicesViewModel.TouchpadY3)
                              or nameof(ViewModels.DevicesViewModel.TouchpadX4)
                              or nameof(ViewModels.DevicesViewModel.TouchpadY4)
                              or nameof(ViewModels.DevicesViewModel.TouchpadDown0)
                              or nameof(ViewModels.DevicesViewModel.TouchpadDown1)
                              or nameof(ViewModels.DevicesViewModel.TouchpadDown2)
                              or nameof(ViewModels.DevicesViewModel.TouchpadDown3)
                              or nameof(ViewModels.DevicesViewModel.TouchpadDown4))
            {
                UpdateTouchpadDots();
            }
            else if (e.PropertyName is nameof(ViewModels.DevicesViewModel.FalconDotX)
                                   or nameof(ViewModels.DevicesViewModel.FalconDotY)
                                   or nameof(ViewModels.DevicesViewModel.IsFalconDevice))
            {
                UpdateFalconDot();
            }
            else if (e.PropertyName is nameof(ViewModels.DevicesViewModel.Pad2X0)
                              or nameof(ViewModels.DevicesViewModel.Pad2Y0)
                              or nameof(ViewModels.DevicesViewModel.Pad2X1)
                              or nameof(ViewModels.DevicesViewModel.Pad2Y1)
                              or nameof(ViewModels.DevicesViewModel.Pad2X2)
                              or nameof(ViewModels.DevicesViewModel.Pad2Y2)
                              or nameof(ViewModels.DevicesViewModel.Pad2X3)
                              or nameof(ViewModels.DevicesViewModel.Pad2Y3)
                              or nameof(ViewModels.DevicesViewModel.Pad2X4)
                              or nameof(ViewModels.DevicesViewModel.Pad2Y4)
                              or nameof(ViewModels.DevicesViewModel.Pad2Down0)
                              or nameof(ViewModels.DevicesViewModel.Pad2Down1)
                              or nameof(ViewModels.DevicesViewModel.Pad2Down2)
                              or nameof(ViewModels.DevicesViewModel.Pad2Down3)
                              or nameof(ViewModels.DevicesViewModel.Pad2Down4))
            {
                UpdateTouchpad2Dots();
            }
        }

        private void UpdateTouchpadDots()
        {
            if (DataContext is not ViewModels.DevicesViewModel vm) return;
            if (TouchpadPreviewBorder.Visibility != Visibility.Visible) return;

            double w = TouchpadPreviewBorder.ActualWidth;
            double h = TouchpadPreviewBorder.ActualHeight;
            if (w <= 0 || h <= 0) return;

            // Up to 5 simultaneous contacts (Windows PTP max). SDL
            // gamepad touchpads typically expose 1-2 so slots 2-4
            // stay invisible there; the bridge in InputService writes
            // Down=false for missing slots which hides their dots
            // via the XAML BoolToVisibility binding.
            Canvas.SetLeft(TouchpadDot0, vm.TouchpadX0 * w - 7);
            Canvas.SetTop(TouchpadDot0, vm.TouchpadY0 * h - 7);
            Canvas.SetLeft(TouchpadDot1, vm.TouchpadX1 * w - 7);
            Canvas.SetTop(TouchpadDot1, vm.TouchpadY1 * h - 7);
            if (TouchpadDot2 != null)
            {
                Canvas.SetLeft(TouchpadDot2, vm.TouchpadX2 * w - 7);
                Canvas.SetTop(TouchpadDot2, vm.TouchpadY2 * h - 7);
            }
            if (TouchpadDot3 != null)
            {
                Canvas.SetLeft(TouchpadDot3, vm.TouchpadX3 * w - 7);
                Canvas.SetTop(TouchpadDot3, vm.TouchpadY3 * h - 7);
            }
            if (TouchpadDot4 != null)
            {
                Canvas.SetLeft(TouchpadDot4, vm.TouchpadX4 * w - 7);
                Canvas.SetTop(TouchpadDot4, vm.TouchpadY4 * h - 7);
            }
        }

        /// <summary>The Falcon handle's dot (#521), placed as the touchpad
        /// dots are. The square lays out after IsFalconDevice shows it, so
        /// its size change places the dot too.</summary>
        private void UpdateFalconDot()
        {
            if (DataContext is not ViewModels.DevicesViewModel vm || !vm.IsFalconDevice) return;
            double w = FalconPreviewBorder.ActualWidth;
            double h = FalconPreviewBorder.ActualHeight;
            if (w <= 0 || h <= 0) return;
            Canvas.SetLeft(FalconDot, vm.FalconDotX * w - 7);
            Canvas.SetTop(FalconDot, vm.FalconDotY * h - 7);
        }

        private void FalconPreviewBorder_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateFalconDot();

        // Second touchpad surface (multi-pad devices). Mirrors UpdateTouchpadDots
        // for the Pad2* finger slots inside Touchpad2PreviewBorder.
        private void UpdateTouchpad2Dots()
        {
            if (DataContext is not ViewModels.DevicesViewModel vm) return;
            if (Touchpad2PreviewBorder.Visibility != Visibility.Visible) return;

            double w = Touchpad2PreviewBorder.ActualWidth;
            double h = Touchpad2PreviewBorder.ActualHeight;
            if (w <= 0 || h <= 0) return;

            Canvas.SetLeft(Pad2Dot0, vm.Pad2X0 * w - 7);
            Canvas.SetTop(Pad2Dot0, vm.Pad2Y0 * h - 7);
            Canvas.SetLeft(Pad2Dot1, vm.Pad2X1 * w - 7);
            Canvas.SetTop(Pad2Dot1, vm.Pad2Y1 * h - 7);
            if (Pad2Dot2 != null) { Canvas.SetLeft(Pad2Dot2, vm.Pad2X2 * w - 7); Canvas.SetTop(Pad2Dot2, vm.Pad2Y2 * h - 7); }
            if (Pad2Dot3 != null) { Canvas.SetLeft(Pad2Dot3, vm.Pad2X3 * w - 7); Canvas.SetTop(Pad2Dot3, vm.Pad2Y3 * h - 7); }
            if (Pad2Dot4 != null) { Canvas.SetLeft(Pad2Dot4, vm.Pad2X4 * w - 7); Canvas.SetTop(Pad2Dot4, vm.Pad2Y4 * h - 7); }
        }

        /// <summary>Test Rumble, the identify buzz (#293): route through
        /// InputService, which picks the sole-writer lane for mapped devices
        /// and the direct train for unmapped ones.</summary>
        private void IdentifyDevice_Click(object sender, RoutedEventArgs e)
        {
            // The button is on the panel's first card, so the target is the
            // SELECTED device (the panel's whole context), not a row.
            if (DataContext is ViewModels.DevicesViewModel vm && vm.SelectedDevice != null)
                InputService?.IdentifyDevice(vm.SelectedDevice.InstanceGuid);
        }

        /// <summary>Remove Device, in the Details card: the selected device. The
        /// device is selected again before the command runs, so the one the
        /// confirm named is the one removed.</summary>
        private void RemoveDevice_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.DevicesViewModel vm || vm.SelectedDevice is not { } device)
                return;

            // Destructive-verb guard (#175 phase 2 item 1b): one click
            // deleted the device record plus its settings. Same shared
            // confirm as the Dashboard slot X. The body names the device
            // the row holds and states the settings are forgotten.
            bool confirmed = ConfirmDialog.Show(
                Window.GetWindow(this),
                Strings.Instance.Devices_RemoveConfirmTitle,
                string.Format(Strings.Instance.Devices_RemoveConfirm_Format, device.DeviceName),
                Strings.Instance.Common_Remove);
            if (!confirmed) return;

            bool isDs3 =
                device.VendorId == PadForge.Services.Ds3PairingService.DS3_VID &&
                device.ProductId == PadForge.Services.Ds3PairingService.DS3_PID;
            // PS Move family (#277): the Move and Navigation controllers
            // live in the Bluetooth stack the same way the DS3 does, so
            // "forget" must clear their pairing too.
            bool isMoveFamily =
                device.VendorId == PadForge.Services.Ds3PairingService.DS3_VID &&
                (device.ProductId == PadForge.Services.Ds3PairingService.MOVE_PID ||
                 device.ProductId == PadForge.Services.Ds3PairingService.NAV_PID);

            // Detach a live pad before pulling its row, so the still-connected
            // pad can't blip back into the list while its pairing is cleared.
            if (isDs3) PadForge.Common.Input.Ds3DirectService.SuppressAndRelease();
            if (isMoveFamily)
            {
                PadForge.Common.Input.PsMoveDirectService.SuppressAndRelease();
                PadForge.Common.Input.Ds3DirectService.SuppressAndRelease();
            }

            try
            {
                vm.SelectedDevice = device;
                if (vm.RemoveDeviceCommand.CanExecute(null))
                    vm.RemoveDeviceCommand.Execute(null);
            }
            finally
            {
                // A DualShock 3 lives on in the Bluetooth stack after its row is
                // removed (BthPS3 remembered-device record + link key), so it
                // would silently reconnect. "Forget" should mean forgotten:
                // clear its pairing too, on a background thread, since the
                // clear cycles the radio. Runs even if the row removal threw,
                // because UnpairAllDs3's finally is the ONLY thing that
                // releases the suppress gate set above. Skipping it would
                // strand every DS3, unable to reconnect for the process
                // lifetime.
                if (isDs3)
                {
                    System.Threading.Tasks.Task.Run(
                        () => new PadForge.Services.Ds3PairingService().UnpairAllDs3());
                }
                else if (isMoveFamily)
                {
                    // Adopts and releases BOTH suppress claims taken above
                    // (UnpairAllMoveFamily's finally), mirroring the DS3 contract.
                    System.Threading.Tasks.Task.Run(
                        () => new PadForge.Services.Ds3PairingService().UnpairAllMoveFamily());
                }
            }
        }

        /// <summary>
        /// Handles the HidHide / ConsumeInput / ForceRaw toggle clicks. Uses the
        /// Click event, not Checked/Unchecked, so it fires only on real user
        /// interaction. The Checked event also fired when the TwoWay binding set
        /// IsChecked as the selected device changed, which reverted the box and
        /// popped the warning on every device selection (issue #161).
        /// Shows a warning flyout for mice and keyboards before enabling.
        /// Propagates the change back through DevicesViewModel → DeviceService → InputService.
        /// </summary>
        private void HidingToggle_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as ViewModels.DevicesViewModel;
            var dev = vm?.SelectedDevice;
            if (dev == null) return;

            var cb = sender as CheckBox;

            // Say what turning on input blocking does to a mouse or keyboard
            // before it lands. After a click the box has already toggled, so
            // IsChecked == true means the user just turned it on. Consumer
            // Control rows get the keyboard's message, since HidHide's
            // base-container sibling expansion cloaks the whole physical
            // keyboard the consumer collection belongs to.
            bool cloaksKeyboardClass = dev.ShowConsumeToggle || dev.DeviceTypeKey == "ConsumerControl";
            if (cb?.IsChecked == true && cloaksKeyboardClass)
            {
                bool isHidHide = cb.Content?.ToString()?.Contains("HidHide") == true;
                bool isMerged = dev.DevicePath?.StartsWith("aggregate://") == true;

                // Immediately revert. Only re-check if the user confirms.
                if (cb != null)
                    cb.IsChecked = false;

                ShowHidingWarningFlyout(cb, vm, dev, HidingWarning(isHidHide, dev.DeviceTypeKey == "Mouse", isMerged), isHidHide);
                return;
            }

            // Force raw mode toggle changes how many buttons/axes are displayed —
            // clear the cached GUID so the raw state collections get rebuilt.
            vm.LastRawStateDeviceGuid = Guid.Empty;

            vm.NotifyDeviceHidingChanged(dev.InstanceGuid);
        }

        /// <summary>What the confirm flyout says before input blocking turns on
        /// for a keyboard, mouse or Consumer Control row. Hide from Games cannot
        /// take a keyboard or mouse away from Windows: HidHide filters the
        /// HIDClass stack only (HidHide Watchdog/App.cpp:114-131), while typing
        /// runs kbdclass > kbdhid > HidUsb and the pointer mouclass > mouhid >
        /// HidUsb. Measured on hardware on 2026-10-31 for a keyboard (discussion
        /// #537) and for a mouse (5809b088): Windows kept both, games included.
        /// So the message says what does happen and names Use Only in PadForge,
        /// which does take a USB keyboard or mouse from Windows. The Consume
        /// message stays a warning, since the hooks really swallow mapped
        /// keys.</summary>
        internal static string HidingWarning(bool isHidHide, bool isMouse, bool isMerged)
        {
            var s = Strings.Instance;
            string deviceKind = isMouse ? s.Devices_DeviceKind_Mouse : s.Devices_DeviceKind_Keyboard;
            string scope = isMerged ? string.Format(s.Devices_WarnScope_Format, deviceKind) : "";
            string action = isHidHide ? s.Devices_HideHidAction : s.Devices_ConsumeAction;
            string consequence = isHidHide
                ? string.Format(isMouse ? s.Devices_WarnHideHid_Mouse : s.Devices_WarnHideHid_Keyboard, s.Devices_DriverBind_OnlyInPadForge)
                : isMouse ? s.Devices_WarnConsumeMouse : s.Devices_WarnConsumeKeyboard;
            return string.Format(s.Devices_WarnAction_Format, action, scope, consequence);
        }

        /// <summary>Quick Charge checkbox (#372): the TwoWay binding has
        /// already written the row when Click fires, so this only flushes
        /// the row into the persisted UserDevice, the same lane as the
        /// idle-disconnect box below.</summary>
        private void QuickCharge_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as ViewModels.DevicesViewModel;
            var dev = vm?.SelectedDevice;
            if (dev == null) return;
            vm.NotifyDeviceHidingChanged(dev.InstanceGuid);
        }

        /// <summary>Light-gun calibration, the GunCon 2's (hifihedgehog/SDL#33
        /// Part 9) and a Wii Remote's (#485): runs the four-target screen
        /// against the gun's live wrapper, then stores the fitted window on the
        /// row and flushes it through the same lane as the checkboxes above,
        /// which also hands it to the wrapper. A GunCon window equal to the
        /// PC tools' starting one is stored as empty, which means the same.</summary>
        private async void GunCalibrate_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as ViewModels.DevicesViewModel;
            var dev = vm?.SelectedDevice;
            if (dev == null || !dev.CanCalibrateGun) return;
            var ud = PadForge.Common.Input.SettingsManager.FindDeviceByInstanceGuid(dev.InstanceGuid);
            if (ud?.Device is not PadForge.Engine.SdlDeviceWrapper wrapper) return;
            CalibrationGun gun;
            if (wrapper.IsGunCon2) gun = CalibrationGun.ForGunCon2(ud, wrapper);
            else if (wrapper.HasIrCamera) gun = CalibrationGun.ForWiiRemote(wrapper);
            else return;

            var calibration = await GunCalibrationScreen.RunAsync(ud, gun, Window.GetWindow(this));
            if (calibration == null) return;
            dev.GunCalibration = wrapper.IsGunCon2 && calibration.IsDefault ? string.Empty : calibration.ToString();
            vm.NotifyDeviceHidingChanged(dev.InstanceGuid);
        }

        /// <summary>
        /// Persists the idle-disconnect minutes (#162) when the box loses focus,
        /// through the same DevicesViewModel → DeviceService channel as the
        /// hiding toggles. The binding is force-committed first: WPF does not
        /// guarantee the LostFocus-triggered source update runs before this
        /// instance handler, and when the handler won the race it persisted the
        /// STALE value, which the next device-list sync then wrote back over
        /// the user's edit (observed: 1 → 0 reverted to 1).
        /// </summary>
        private void IdleDisconnect_LostFocus(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as ViewModels.DevicesViewModel;
            var dev = vm?.SelectedDevice;
            if (dev == null) return;
            if (sender is System.Windows.Controls.TextBox tb)
                tb.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            vm.NotifyDeviceHidingChanged(dev.InstanceGuid);
        }

        /// <summary>
        /// Shows a WPF UI Flyout with a warning and Proceed/Cancel buttons.
        /// Re-checks the toggle and notifies only if the user clicks Proceed.
        /// </summary>
        private void ShowHidingWarningFlyout(CheckBox cb, ViewModels.DevicesViewModel vm,
            ViewModels.DeviceRowViewModel dev, string message, bool isHidHide)
        {
            var warningIcon = new PadForge.Controls.GlyphText
            {
                Text = "\uE7BA",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 20,
                Foreground = System.Windows.Media.Brushes.Orange,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var messageText = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var proceedBtn = new Button
            {
                Content = Strings.Instance.Common_Proceed,
                Margin = new Thickness(0, 0, 8, 0),
                MinWidth = 80
            };
            // Proceed is the flyout's primary affirmative and wears the shared
            // ember accent (#175 dialog language). The old TextOnAccent-on-accent
            // pair went dark text on ember (user report 2026-07-05).
            proceedBtn.SetResourceReference(FrameworkElement.StyleProperty, "EmberAccentButton");

            var cancelBtn = new Button
            {
                Content = Strings.Instance.Common_Cancel,
                MinWidth = 80
            };

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttonPanel.Children.Add(proceedBtn);
            buttonPanel.Children.Add(cancelBtn);

            var content = new StackPanel();
            content.Children.Add(warningIcon);
            content.Children.Add(messageText);
            content.Children.Add(buttonPanel);

            // The flyout's style sets 20 by 20, and inside a setting's row
            // those 20 would narrow the label while the flyout is open.
            var flyout = new Wpf.Ui.Controls.Flyout
            {
                Content = content,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
                MinWidth = 0,
                MinHeight = 0,
            };

            // Add flyout to the visual tree near the target, then open it.
            var target = cb ?? (FrameworkElement)this;
            if (target.Parent is System.Windows.Controls.Panel panel)
            {
                panel.Children.Add(flyout);
            }
            flyout.IsOpen = true;

            // Remove the flyout from the panel once it closes — otherwise
            // every re-toggle on a mouse/keyboard leaks a closed Flyout plus
            // its captured handler closures into the singleton page's panel.
            void RemoveFromPanel()
            {
                if (target.Parent is System.Windows.Controls.Panel p && p.Children.Contains(flyout))
                    p.Children.Remove(flyout);
            }

            proceedBtn.Click += (s, ev) =>
            {
                flyout.IsOpen = false;
                // Set the model value; the TwoWay binding re-checks the box.
                // Programmatic IsChecked changes do not raise Click, so there
                // is no handler to unhook and no re-entry.
                if (isHidHide)
                    dev.HidHideEnabled = true;
                else
                    dev.ConsumeInputEnabled = true;
                vm.NotifyDeviceHidingChanged(dev.InstanceGuid);
                RemoveFromPanel();
            };

            cancelBtn.Click += (s, ev) => { flyout.IsOpen = false; RemoveFromPanel(); };
        }

        private void SubmitMapping_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.DevicesViewModel vm || vm.SelectedDevice is not { } dev)
                return;

            var sb = new System.Text.StringBuilder();
            sb.Append("https://github.com/hifihedgehog/PadForge/issues/new?template=device_mapping.yml");
            sb.Append("&title=");
            sb.Append(Uri.EscapeDataString($"[Device Mapping] {dev.DeviceName}"));
            sb.Append("&device_name=");
            sb.Append(Uri.EscapeDataString(dev.DeviceName));
            sb.Append("&vid=");
            sb.Append(Uri.EscapeDataString(dev.VendorIdHex));
            sb.Append("&pid=");
            sb.Append(Uri.EscapeDataString(dev.ProductIdHex));
            sb.Append("&axes=");
            sb.Append(dev.AxisCount);
            sb.Append("&buttons=");
            sb.Append(dev.ButtonCount);
            sb.Append("&hats=");
            sb.Append(dev.PovCount);
            if (!string.IsNullOrEmpty(dev.SdlGuid))
            {
                sb.Append("&sdl_guid=");
                sb.Append(Uri.EscapeDataString(dev.SdlGuid));
            }

            PadForge.Common.ShellLaunch.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = sb.ToString(),
                UseShellExecute = true
            });
        }

        // ── NFC tag registration (issue #150) ──

        private void RegisterNfcTag_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new RegisterNfcTagDialog { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
        }


        // ── Opt-in driver switch (hifihedgehog/SDL#33 Part 15) ──

        private void DriverBind_Click(object sender, RoutedEventArgs e)
            => (DataContext as ViewModels.DevicesViewModel)?.RequestDriverBind();

        private void DriverRestore_Click(object sender, RoutedEventArgs e)
            => (DataContext as ViewModels.DevicesViewModel)?.RequestDriverRestore();

        private void ReadAsICade_Click(object sender, RoutedEventArgs e)
            => (DataContext as ViewModels.DevicesViewModel)?.RequestICadeMode(true);

        private void ReadAsKeyboard_Click(object sender, RoutedEventArgs e)
            => (DataContext as ViewModels.DevicesViewModel)?.RequestICadeMode(false);

        private void UsioTekken_Click(object sender, RoutedEventArgs e)
            => (DataContext as ViewModels.DevicesViewModel)?.RequestUsioLayout(PadForge.Common.Input.InputManager.UsioTekken);

        private void UsioTaiko_Click(object sender, RoutedEventArgs e)
            => (DataContext as ViewModels.DevicesViewModel)?.RequestUsioLayout(PadForge.Common.Input.InputManager.UsioTaiko);

        // Bliss-Box port actions (issue #469). MainWindow owns the dialogs
        // and the transfers.
        private void BlissBoxPlayer_Click(object sender, RoutedEventArgs e)
            => (DataContext as ViewModels.DevicesViewModel)?.RequestBlissBox(ViewModels.BlissBoxAction.Player);

        private void DreamcastScreen_Click(object sender, RoutedEventArgs e)
            => (DataContext as ViewModels.DevicesViewModel)?.RequestBlissBox(ViewModels.BlissBoxAction.DreamcastScreen);

        private void PakBackup_Click(object sender, RoutedEventArgs e)
            => (DataContext as ViewModels.DevicesViewModel)?.RequestBlissBox(ViewModels.BlissBoxAction.PakBackup);

        private void PakRestore_Click(object sender, RoutedEventArgs e)
            => (DataContext as ViewModels.DevicesViewModel)?.RequestBlissBox(ViewModels.BlissBoxAction.PakRestore);

        private void NativeArrows_Click(object sender, RoutedEventArgs e)
            => (DataContext as ViewModels.DevicesViewModel)?.RequestBlissBox(ViewModels.BlissBoxAction.NativeArrows);

        // ── Keyboard screen (issue #519) and Novint Falcon (issue #521) ──

        private void KeyboardScreen_Click(object sender, RoutedEventArgs e)
            => (DataContext as ViewModels.DevicesViewModel)?.RequestKeyboardScreen();

        private void FalconCalibrate_Click(object sender, RoutedEventArgs e)
            => (DataContext as ViewModels.DevicesViewModel)?.RequestFalconCalibrate();

        private void ClearModEffects_Click(object sender, RoutedEventArgs e)
            => (DataContext as ViewModels.DevicesViewModel)?.ClearModEffects();

        // ── Handheld hidden buttons (issue #343) ──

        private void LearnHandheldButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new LearnHandheldButtonDialog { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
        }

        // ── Map Key Lights (#557) ──

        private void MapKeyLights_Click(object sender, RoutedEventArgs e)
        {
            var row = (DataContext as PadForge.ViewModels.DevicesViewModel)?.SelectedDevice;
            if (row == null) return;
            var map = PadForge.Common.Input.Peripherals.PeripheralLightMaps.ForRow(
                PadForge.Common.Input.Peripherals.PeripheralOutputs.Links.For(row.InstanceGuid), out var path);
            if (map == null) return;
            var dlg = new MapKeyLightsDialog(path, row.DeviceName) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
        }

        // ── Voice macros (issue #315) ──

        private void ManageVoicePhrases_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new RegisterVoicePhraseDialog { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
        }

        private void ManageChatCommands_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ChatCommandsDialog { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
        }

        // ── Copy Details (#175 competitor item 7) ──

        /// <summary>
        /// Copies the device's details as plain mono text, in locale-neutral
        /// token lines (PRODUCT, TYPE, CAPS, APP GUID, SDL GUID, PATH,
        /// VID:PID, LINK, SERIAL, FIRMWARE, REV, BATT). Fields the engine
        /// holds only, absent facts omitted, no placeholders. CAPS is the
        /// capabilities line, which already names rumble, gyro, accel and
        /// touchpad. Same try/catch and status-bar confirmation shape as the
        /// settings and macro copy handlers.
        /// </summary>
        private void CopyDossier_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.DevicesViewModel vm || vm.SelectedDevice is not { } dev)
                return;

            var mainVm = Application.Current.MainWindow?.DataContext as ViewModels.MainViewModel;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"PRODUCT   {dev.ProductName}");
                sb.AppendLine($"TYPE      {dev.DeviceType}");
                sb.AppendLine($"CAPS      {dev.CapabilitiesSummary}");
                sb.AppendLine($"APP GUID  {dev.InstanceGuid}");
                if (!string.IsNullOrEmpty(dev.SdlGuid))
                    sb.AppendLine($"SDL GUID  {dev.SdlGuid}");
                if (!string.IsNullOrEmpty(dev.HidHideInstancePath))
                    sb.AppendLine($"PATH      {dev.HidHideInstancePath}");
                else if (!string.IsNullOrEmpty(dev.DossierConnectionPath))
                    sb.AppendLine($"PATH      {dev.DossierConnectionPath}");
                sb.AppendLine($"VID:PID   {dev.VendorIdHex}:{dev.ProductIdHex}");
                if (dev.IsBluetoothLink)
                    sb.AppendLine("LINK      BT");
                if (!string.IsNullOrEmpty(dev.SerialNumber))
                    sb.AppendLine($"SERIAL    {dev.SerialNumber}");
                if (!string.IsNullOrEmpty(dev.FirmwareVersion))
                    sb.AppendLine($"FIRMWARE  {dev.FirmwareVersion}");
                if (!string.IsNullOrEmpty(dev.ProductRevision))
                    sb.AppendLine($"REV       {dev.ProductRevision}");
                if (dev.HasBattery)
                    sb.AppendLine($"BATT      {dev.BatteryText}" + (dev.BatteryCharging ? " CHG" : string.Empty));

                PadForge.Common.ShellLaunch.SetClipboardText(sb.ToString());
                if (mainVm != null)
                    mainVm.StatusText = Strings.Instance.Status_DossierCopied;
            }
            catch (Exception ex)
            {
                if (mainVm != null)
                    mainVm.SetStatus(string.Format(Strings.Instance.Status_CopyFailed_Format, ex.Message), persist: true);
            }
        }

        // ── A row's drag (to a controller in the sidebar) ──

        private Point _deviceDragStart;
        private bool _deviceDragStarted;

        private void DeviceCard_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border card) return;
            if (card.DataContext is not ViewModels.DeviceRowViewModel) return;
            if (IsInsideButton(e.OriginalSource as DependencyObject, card)) return;
            _deviceDragStart = e.GetPosition(this);
            _deviceDragStarted = true;
        }

        private void DeviceCard_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_deviceDragStarted || e.LeftButton != MouseButtonState.Pressed) return;
            if (sender is not Border card) return;
            if (card.DataContext is not ViewModels.DeviceRowViewModel device) return;

            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _deviceDragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(pos.Y - _deviceDragStart.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _deviceDragStarted = false;
                var data = new DataObject("DeviceInstanceGuid", device.InstanceGuid);
                DragDrop.DoDragDrop(card, data, DragDropEffects.Link);
            }
        }

        private void DeviceCard_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _deviceDragStarted = false;
        }

        private static bool IsInsideButton(DependencyObject source, DependencyObject boundary)
        {
            var current = source;
            while (current != null && current != boundary)
            {
                if (current is Button) return true;
                current = current is System.Windows.Media.Visual || current is System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return false;
        }
    }
}
