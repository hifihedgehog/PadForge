using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ModernWpf.Controls;
using ModernWpf.Controls.Primitives;

namespace PadForge.Views
{
    public partial class DevicesPage : UserControl
    {
        public DevicesPage()
        {
            InitializeComponent();
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
                    ? "hide this device from all applications"
                    : "block mapped inputs from this device";
                string deviceKind = dev.DeviceType == "Mouse" ? "mouse" : "keyboard";
                bool isMerged = dev.DeviceName?.Contains("(Merged)") == true ||
                                dev.DeviceName?.Contains("All ") == true;

                string scope = isMerged
                    ? $" for all connected {deviceKind}s"
                    : "";

                string consequence;
                if (isHidHide)
                {
                    consequence = $"If this is your only {deviceKind}, you will lose the ability to " +
                        $"interact with your system. Only proceed if you have another " +
                        $"input device available.";
                }
                else
                {
                    consequence = $"If you map inputs that are critical for system interaction " +
                        (dev.DeviceType == "Mouse"
                            ? "(e.g. left/right click, X/Y movement)"
                            : "(e.g. common keys)") +
                        $", you may lose the ability to control your system. " +
                        $"Only proceed if you have another input device available.";
                }

                // Immediately revert — only re-check if the user confirms.
                if (cb != null)
                    cb.IsChecked = false;

                ShowHidingWarningFlyout(cb, vm, dev,
                    $"This will {action}{scope}.\n\n{consequence}",
                    isHidHide);
                return;
            }

            vm.NotifyDeviceHidingChanged(dev.InstanceGuid);
        }

        /// <summary>
        /// Shows a ModernWpf Flyout with a warning and Proceed/Cancel buttons.
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
                Content = "Proceed",
                Margin = new Thickness(0, 0, 8, 0),
                MinWidth = 80
            };
            proceedBtn.SetResourceReference(Control.StyleProperty, "AccentButtonStyle");

            var cancelBtn = new Button
            {
                Content = "Cancel",
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

            var flyout = new Flyout
            {
                Content = content,
                Placement = FlyoutPlacementMode.Bottom
            };

            proceedBtn.Click += (s, ev) =>
            {
                flyout.Hide();
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

            cancelBtn.Click += (s, ev) => flyout.Hide();

            flyout.ShowAt(cb ?? (FrameworkElement)this);
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
