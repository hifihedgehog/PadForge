using System;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Flydigi;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.Views;

namespace PadForge
{
    public partial class MainWindow
    {
        /// <summary>A Flydigi Apex pad's screen (#586): its dialog from its
        /// panel on the Devices page, as a keyboard screen's opens.</summary>
        private void WireFlydigiScreen()
        {
            _viewModel.Devices.FlydigiScreenRequested += (_, _) =>
            {
                if (_viewModel.Devices.SelectedDevice is { } row) ConfigureFlydigiScreen(row.InstanceGuid);
            };
        }

        private void ConfigureFlydigiScreen(Guid device)
        {
            var pad = FlydigiApexService.SessionOf(device);
            if (pad == null || !pad.Identity.HasScreen) return;
            var settings = _viewModel.Settings;
            string profileId = SettingsManager.ActiveProfileId;
            var profile = string.IsNullOrEmpty(profileId) ? null : SettingsManager.Profiles.Find(p => p.Id == profileId);
            string profilePicture = profile != null ? profile.KeyboardScreenPicture : settings.DefaultProfileKeyboardScreenPicture;

            // The Apex 4's send moves the pad to WinUSB. The node is on the
            // record in the settings file before the move and off it after the
            // hand-back, so a send a crash cut off is given back at the next
            // start (GiveBackFlydigiScreenBindings).
            var binding = new FlydigiScreenSender.Apex4Binding
            {
                Owe = id => Dispatcher.Invoke(() =>
                {
                    if (!settings.FlydigiScreenBindings.Contains(id)) settings.FlydigiScreenBindings.Add(id);
                    _settingsService?.Save();
                }),
                Settle = id => Dispatcher.Invoke(() =>
                {
                    if (settings.FlydigiScreenBindings.Remove(id)) _settingsService?.Save();
                }),
                HoldSdl = off => InputManager.HoldXbox360HidapiDriver(off),
            };

            var result = FlydigiScreenDialog.Show(this, device, pad.Identity.Model, binding, DreamcastScreenService.ActiveProfileName(),
                DreamcastScreenService.ActiveProfileNumber(), profilePicture);
            if (result.ProfilePictureChanged)
            {
                if (profile != null) profile.KeyboardScreenPicture = result.ProfilePicture;
                else settings.DefaultProfileKeyboardScreenPicture = result.ProfilePicture;
                _settingsService?.MarkDirty();
            }
            if (result.Sent) _viewModel.SetStatus(Strings.Instance.Status_FlydigiScreen_Sent);
        }

        /// <summary>Gives back to Windows' Xbox driver each Apex 4 node a
        /// screen send moved to WinUSB and never gave back (#586). A node on
        /// the record whose driver is no longer PadForge's leaves the record,
        /// whoever moved it.</summary>
        private void GiveBackFlydigiScreenBindings()
        {
            var owed = _viewModel.Settings.FlydigiScreenBindings.ToList();
            if (owed.Count == 0) return;
            Action<string> log = m => PadForge.Engine.SdlDiagLog.WriteLine("FLYDIGI screen: " + m);
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                foreach (string id in owed)
                {
                    bool restored = VendorUsbDriverInstaller.Restore(id, log, default);
                    if (!restored && VendorUsbDriverInstaller.HasActiveInterface(id)) continue;
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_viewModel.Settings.FlydigiScreenBindings.Remove(id)) _settingsService?.Save();
                    }));
                }
            });
        }
    }
}
