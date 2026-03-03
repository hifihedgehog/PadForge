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
