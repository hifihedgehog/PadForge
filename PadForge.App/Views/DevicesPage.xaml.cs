using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NavigationView = Wpf.Ui.Controls.NavigationView;
using NavigationViewItem = Wpf.Ui.Controls.NavigationViewItem;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
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
                newVm.PropertyChanged += OnVmPropertyChanged;
        }

        private void OnVmPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
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

        private void RemoveDevice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn &&
                btn.DataContext is ViewModels.DeviceRowViewModel device)
            {
                var vm = DataContext as ViewModels.DevicesViewModel;
                if (vm != null)
                {
                    vm.SelectedDevice = device;
                    if (vm.RemoveDeviceCommand.CanExecute(null))
                        vm.RemoveDeviceCommand.Execute(null);
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

            // Warn when enabling input blocking on a mouse or keyboard. After a
            // click the box has already toggled, so IsChecked == true means the
            // user just turned it on. Consumer Control rows get the same
            // warning: HidHide's base-container sibling expansion cloaks the
            // WHOLE physical keyboard the consumer collection belongs to, and
            // doing that silently locked users out of their system (audit M1).
            bool cloaksKeyboardClass = dev.ShowConsumeToggle || dev.DeviceTypeKey == "ConsumerControl";
            if (cb?.IsChecked == true && cloaksKeyboardClass)
            {
                bool isHidHide = cb.Content?.ToString()?.Contains("HidHide") == true;
                string action = isHidHide
                    ? Strings.Instance.Devices_HideAction
                    : Strings.Instance.Devices_ConsumeAction;
                string deviceKind = dev.DeviceTypeKey == "Mouse"
                    ? Strings.Instance.Devices_DeviceKind_Mouse
                    : Strings.Instance.Devices_DeviceKind_Keyboard;
                bool isMerged = dev.DevicePath?.StartsWith("aggregate://") == true;

                string scope = isMerged
                    ? string.Format(Strings.Instance.Devices_WarnScope_Format, deviceKind)
                    : "";

                string consequence = isHidHide
                    ? string.Format(Strings.Instance.Devices_WarnHide_Format, deviceKind)
                    : dev.DeviceTypeKey == "Mouse"
                        ? Strings.Instance.Devices_WarnConsumeMouse
                        : Strings.Instance.Devices_WarnConsumeKeyboard;

                // Immediately revert — only re-check if the user confirms.
                if (cb != null)
                    cb.IsChecked = false;

                ShowHidingWarningFlyout(cb, vm, dev,
                    string.Format(Strings.Instance.Devices_WarnAction_Format, action, scope, consequence),
                    isHidHide);
                return;
            }

            // Force raw mode toggle changes how many buttons/axes are displayed —
            // clear the cached GUID so the raw state collections get rebuilt.
            vm.LastRawStateDeviceGuid = Guid.Empty;

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
            var warningIcon = new TextBlock
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
            proceedBtn.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
            proceedBtn.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "AccentFillColorDefaultBrush");

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

            var flyout = new Wpf.Ui.Controls.Flyout
            {
                Content = content,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Top
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

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
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

        // ── Device dossier copy (#175 competitor item 7) ──

        /// <summary>
        /// Copies the dossier block as plain mono text. Locale-neutral token
        /// lines mirroring the on-screen card, in card order: the full
        /// identity superset now that the dossier is the single identity
        /// block (PRODUCT, TYPE, CAPS, APP GUID, SDL GUID, PATH, VID:PID,
        /// LINK, SERIAL, BATT). Fields the engine holds only, absent facts
        /// omitted, no placeholders. CAPS is the on-card capabilities
        /// summary line, which already names rumble/gyro/accel/touchpad,
        /// so the chip row adds no extra copy line. Same try/catch +
        /// status-bar confirmation shape as the settings/macro copy
        /// handlers.
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
                sb.AppendLine($"VID:PID   {dev.VendorIdHex}:{dev.ProductIdHex}");
                if (dev.IsBluetoothLink)
                    sb.AppendLine("LINK      BT");
                if (!string.IsNullOrEmpty(dev.SerialNumber))
                    sb.AppendLine($"SERIAL    {dev.SerialNumber}");
                if (dev.HasBattery)
                    sb.AppendLine($"BATT      {dev.BatteryText}" + (dev.BatteryCharging ? " CHG" : string.Empty));

                Clipboard.SetText(sb.ToString());
                if (mainVm != null)
                    mainVm.StatusText = Strings.Instance.Status_DossierCopied;
            }
            catch (Exception ex)
            {
                if (mainVm != null)
                    mainVm.StatusText = string.Format(Strings.Instance.Status_CopyFailed_Format, ex.Message);
            }
        }

        // ── Device card drag (to sidebar controller cards) ──

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
