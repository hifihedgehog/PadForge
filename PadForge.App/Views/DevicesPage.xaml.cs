using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
        /// Propagates the change back through DevicesViewModel → DeviceService → InputService.
        /// </summary>
        private void HidingToggle_Changed(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as ViewModels.DevicesViewModel;
            if (vm?.SelectedDevice != null)
                vm.NotifyDeviceHidingChanged(vm.SelectedDevice.InstanceGuid);
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
