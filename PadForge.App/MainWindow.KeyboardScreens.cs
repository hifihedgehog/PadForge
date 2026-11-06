using System;
using System.Globalization;
using Microsoft.Win32;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;

namespace PadForge
{
    public partial class MainWindow
    {
        /// <summary>The keyboard screens' actions (issue #519): a screen row's
        /// dialog from its panel on the Devices page, and on the Settings
        /// card Mad Catz's SDK fetched at the user's click, a Strike7API.dll
        /// the user has, and the Visual C++ 2008 runtime that library
        /// links.</summary>
        private void WireKeyboardScreens()
        {
            var settings = _viewModel.Settings;
            _viewModel.Devices.KeyboardScreenRequested += (_, _) =>
            {
                if (_viewModel.Devices.SelectedDevice is { } row) ConfigureKeyboardScreen(row.InstanceGuid);
            };
            _viewModel.Dashboard.ChooseScreenOverlayContentRequested += (_, item) => ConfigureScreenOverlay(item);

            // A download that lands is the library from now on, in place of a
            // copy picked before. A failed one leaves the pick alone.
            settings.DownloadStrike7LibraryRequested += async (_, _) =>
                await KeyboardScreenService.DownloadStrike7Library(
                    operation => RunDriverOperationAsync(Strings.Instance.Status_DownloadingStrike7, operation, () => { }),
                    () => DriverInstaller.InstallStrike7Library(
                        KeyboardScreenService.DownloadedStrike7Library, KeyboardScreenService.Strike7LibrarySha256),
                    settings, () => _settingsService.MarkDirty());

            settings.PickStrike7LibraryRequested += (_, _) =>
            {
                var dialog = new OpenFileDialog { Filter = Strings.Instance.KeyboardScreens_Strike7LibraryFilter };
                if (dialog.ShowDialog(this) != true) return;
                settings.Strike7LibraryPath = IconPackageManager.MakeStoredPath(dialog.FileName);
                _settingsService.MarkDirty();
                _viewModel.SetStatus(string.Format(CultureInfo.CurrentCulture,
                    Strings.Instance.Status_Strike7LibraryPicked_Format, System.IO.Path.GetFileName(dialog.FileName)));
            };

            settings.InstallVc2008RuntimeRequested += async (_, _) =>
            {
                await RunDriverOperationAsync(
                    Strings.Instance.Status_InstallingVc2008,
                    DriverInstaller.InstallVc2008Runtime,
                    () => { });
            };
        }

        private void ConfigureKeyboardScreen(Guid row)
        {
            var service = _inputService?.KeyboardScreens;
            var screen = service?.ScreenOf(row);
            if (screen != null) ConfigureScreen(service, screen, service.StatusLine(screen));
        }

        /// <summary>A Screen Overlay's dialog (#560), from its Dashboard card, whether
        /// or not its window is up.</summary>
        private void ConfigureScreenOverlay(ScreenOverlayItem item)
        {
            var service = _inputService?.KeyboardScreens;
            if (service != null) ConfigureScreen(service, KeyboardScreenInfo.Overlay(item.Number), null);
        }

        /// <summary>Opens one screen's dialog and keeps what was saved: the
        /// screen's mode, picture and pan, kept by its row, and the active
        /// profile's picture.</summary>
        private void ConfigureScreen(KeyboardScreenService service, KeyboardScreenInfo screen, string detail)
        {
            Guid row = screen.RowGuid;
            // The overlay has no size of its own, so the preview takes its window's.
            System.Windows.Size? overlaySize = null;
            if (_viewModel.Dashboard.FindScreenOverlay(screen.OverlayNumber) is { } overlay)
                overlaySize = new System.Windows.Size(overlay.Width, overlay.Height);
            string profileId = SettingsManager.ActiveProfileId;
            var profile = string.IsNullOrEmpty(profileId) ? null : SettingsManager.Profiles.Find(p => p.Id == profileId);
            string profilePicture = profile != null ? profile.KeyboardScreenPicture : _viewModel.Settings.DefaultProfileKeyboardScreenPicture;
            var choice = KeyboardScreenDialog.Show(this, service, screen, service.Get(row),
                DreamcastScreenService.ActiveProfileName(), profilePicture, detail, overlaySize);
            if (choice == null) return;
            if (profile != null) profile.KeyboardScreenPicture = choice.ProfilePicture;
            else _viewModel.Settings.DefaultProfileKeyboardScreenPicture = choice.ProfilePicture;
            service.Update(row, d =>
            {
                d.Mode = choice.Mode;
                d.Picture = choice.Picture;
                d.PanX = choice.PanX;
                d.PanY = choice.PanY;
                d.Zoom = choice.Zoom;
                d.LightbarSlot = choice.LightbarSlot;
            });
            _viewModel.SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.Instance.Status_KeyboardScreen_Format,
                KeyboardScreenService.ScreenName(screen), KeyboardScreenService.ModeName(choice.Mode)));
        }
    }
}
