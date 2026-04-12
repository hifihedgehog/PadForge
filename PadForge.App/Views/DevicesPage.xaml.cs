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
        public DevicesPage()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ViewModels.DevicesViewModel oldVm)
                oldVm.PropertyChanged -= OnVmPropertyChanged;
            if (e.NewValue is ViewModels.DevicesViewModel newVm)
                newVm.PropertyChanged += OnVmPropertyChanged;
        }

        private void OnVmPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ViewModels.DevicesViewModel.TouchpadX0)
                              or nameof(ViewModels.DevicesViewModel.TouchpadY0)
                              or nameof(ViewModels.DevicesViewModel.TouchpadX1)
                              or nameof(ViewModels.DevicesViewModel.TouchpadY1))
            {
                UpdateTouchpadDots();
            }
        }

        private void UpdateTouchpadDots()
        {
            if (DataContext is not ViewModels.DevicesViewModel vm) return;
            if (TouchpadPreviewBorder.Visibility != Visibility.Visible) return;

            double w = TouchpadPreviewBorder.ActualWidth;
            double h = TouchpadPreviewBorder.ActualHeight;
            if (w <= 0 || h <= 0) return;

            Canvas.SetLeft(TouchpadDot0, vm.TouchpadX0 * w - 7);
            Canvas.SetTop(TouchpadDot0, vm.TouchpadY0 * h - 7);
            Canvas.SetLeft(TouchpadDot1, vm.TouchpadX1 * w - 7);
            Canvas.SetTop(TouchpadDot1, vm.TouchpadY1 * h - 7);
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
        /// Handles CheckBox Checked/Unchecked for HidHide and ConsumeInput toggles.
        /// Shows a warning flyout for mice and keyboards before enabling.
        /// Propagates the change back through DevicesViewModel → DeviceService → InputService.
        /// </summary>
        private void HidingToggle_Changed(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as ViewModels.DevicesViewModel;
            var dev = vm?.SelectedDevice;
            if (dev == null) return;

            // Warn when enabling input blocking on a mouse or keyboard.
            if (e.RoutedEvent == CheckBox.CheckedEvent && dev.ShowConsumeToggle)
            {
                var cb = sender as CheckBox;
                bool isHidHide = cb?.Content?.ToString()?.Contains("HidHide") == true;
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

            proceedBtn.Click += (s, ev) =>
            {
                flyout.IsOpen = false;
                if (cb != null)
                {
                    // Temporarily unhook to avoid re-entering HidingToggle_Changed.
                    cb.Checked -= HidingToggle_Changed;
                    if (isHidHide)
                        dev.HidHideEnabled = true;
                    else
                        dev.ConsumeInputEnabled = true;
                    cb.Checked += HidingToggle_Changed;
                }
                vm.NotifyDeviceHidingChanged(dev.InstanceGuid);
            };

            cancelBtn.Click += (s, ev) => flyout.IsOpen = false;
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
