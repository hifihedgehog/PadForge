using System;
using CommunityToolkit.Mvvm.Input;

namespace PadForge.ViewModels
{
    public partial class DevicesViewModel
    {
        private RelayCommand<string> _resetSelectedDeviceSettingCommand;
        public RelayCommand<string> ResetSelectedDeviceSettingCommand => _resetSelectedDeviceSettingCommand ??=
            new RelayCommand<string>(ResetSelectedDeviceSetting, CanResetSelectedDeviceSetting);

        private bool CanResetSelectedDeviceSetting(string name) => SelectedDevice != null
            && DeviceRowViewModel.CanResetSetting(name);

        private void ResetSelectedDeviceSetting(string name)
        {
            if (!CanResetSelectedDeviceSetting(name)) return;
            var device = SelectedDevice;
            if (name == nameof(DeviceRowViewModel.HidHideEnabled) && !device.IsHidHideAvailable) return;
            device.ResetSettingCommand.Execute(name);
            LastRawStateDeviceGuid = Guid.Empty;
            // Device options persist through this explicit notification, just
            // as the checkbox Click and idle-disconnect LostFocus handlers do.
            NotifyDeviceHidingChanged(device.InstanceGuid);
        }
    }
}
