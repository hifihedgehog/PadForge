using System;
using System.Collections.Generic;
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
            // A Bliss-Box port's choices persist through the port's own
            // settings, which the action request writes (#469).
            if (name == nameof(DeviceRowViewModel.BlissBoxNativeArrows))
            {
                RequestBlissBox(BlissBoxAction.NativeArrows);
                return;
            }
            LastRawStateDeviceGuid = Guid.Empty;
            // Device options persist through this explicit notification, just
            // as the checkbox Click and idle-disconnect LostFocus handlers do.
            NotifyDeviceHidingChanged(device.InstanceGuid);
        }

        // ─────────────────────────────────────────────
        //  The panel's cards (#574)
        // ─────────────────────────────────────────────

        /// <summary>Whether the Settings card shows the device's tools: a
        /// driver switch, or one of the buttons a row of its kind offers (the
        /// iCade and USIO readings, NFC tags, voice and chat words, hidden
        /// buttons, key lights, a Bliss-Box port's actions, a keyboard's
        /// screen, the Falcon's reach).</summary>
        public bool ShowDeviceTools => ShowDriverBind || ShowDriverRestore
            || (SelectedDevice is { } d && (d.ShowReadAsICade || d.ShowReadAsKeyboard || d.ShowUsioTekken || d.ShowUsioTaiko
                || d.ShowRegisterNfcTag || d.ShowManageVoicePhrases || d.ShowManageChatCommands || d.ShowLearnHandheldButton
                || d.ShowMapKeyLights || d.ShowBlissBoxPlayer || d.ShowDreamcastScreen || d.ShowControllerPak
                || d.ShowKeyboardScreen || d.ShowFalconCalibrate));

        /// <summary>Whether the Settings card shows at all: the device has a
        /// setting of its own or a tool.</summary>
        public bool ShowSettingsCard => SelectedDevice is { } d
            && (d.ShowInputHidingSection || d.ShowPowerSection || d.ShowNativeArrows || d.ShowGunCalibration || ShowDeviceTools);

        /// <summary>The settings the Settings card shows for a device, in the
        /// card's order, which its Reset All puts back. Force Raw Joystick
        /// Mode is the Details card's and stays.</summary>
        internal static IReadOnlyList<string> SettingsCardResets(DeviceRowViewModel d)
        {
            var names = new List<string>();
            if (d == null) return names;
            if (d.ShowInputHidingSection && d.ShowHidHideToggle && d.IsHidHideAvailable) names.Add(nameof(DeviceRowViewModel.HidHideEnabled));
            if (d.ShowInputHidingSection && d.ShowConsumeToggle) names.Add(nameof(DeviceRowViewModel.ConsumeInputEnabled));
            if (d.ShowPowerSection && d.ShowIdleDisconnect) names.Add(nameof(DeviceRowViewModel.IdleDisconnectMinutes));
            if (d.ShowPowerSection && d.ShowQuickCharge) names.Add(nameof(DeviceRowViewModel.QuickChargeEnabled));
            if (d.ShowNativeArrows) names.Add(nameof(DeviceRowViewModel.BlissBoxNativeArrows));
            if (d.ShowGunCalibration) names.Add(nameof(DeviceRowViewModel.GunCalibration));
            return names;
        }

        /// <summary>Whether the Settings card's Reset All shows: the card has
        /// a setting to put back.</summary>
        public bool HasResettableSettings => SelectedDevice != null && SettingsCardResets(SelectedDevice).Count > 0;

        /// <summary>Whether the Test Inputs card shows: the device reads input
        /// and its state is read.</summary>
        public bool ShowTestCard => HasRawData && SelectedDevice?.ShowRawInputState == true;

        private RelayCommand _resetSettingsCardCommand;

        /// <summary>The Settings card's Reset All: each of the card's
        /// settings back to its default, as each one's own reset puts it, and
        /// one notification for them all.</summary>
        public RelayCommand ResetSettingsCardCommand => _resetSettingsCardCommand ??=
            new RelayCommand(ResetSettingsCard, () => HasResettableSettings);

        private void ResetSettingsCard()
        {
            var device = SelectedDevice;
            if (device == null) return;
            var names = SettingsCardResets(device);
            if (names.Count == 0) return;
            bool arrows = false;
            foreach (string name in names)
            {
                if (!DeviceRowViewModel.CanResetSetting(name)) continue;
                device.ResetSettingCommand.Execute(name);
                arrows |= name == nameof(DeviceRowViewModel.BlissBoxNativeArrows);
            }
            // A Bliss-Box port's choices persist through the port's own
            // settings, which the action request writes (#469).
            if (arrows) RequestBlissBox(BlissBoxAction.NativeArrows);
            LastRawStateDeviceGuid = Guid.Empty;
            NotifyDeviceHidingChanged(device.InstanceGuid);
        }

        /// <summary>The panel's cards follow the selected row and the driver
        /// offer.</summary>
        private void RaisePanelFacts()
        {
            OnPropertyChanged(nameof(ShowDeviceTools));
            OnPropertyChanged(nameof(ShowSettingsCard));
            OnPropertyChanged(nameof(HasResettableSettings));
            OnPropertyChanged(nameof(ShowTestCard));
            _resetSettingsCardCommand?.NotifyCanExecuteChanged();
        }
    }
}
