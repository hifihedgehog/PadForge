using System;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Input.Falcon;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge
{
    public partial class MainWindow
    {
        /// <summary>Force Dimension's SDK download page, where the Settings
        /// card sends a user with no SDK (issue #521).</summary>
        internal const string FalconSdkDownloadUrl = "https://www.forcedimension.com/software/sdk";

        /// <summary>The SDK's download page in the browser, and Windows'
        /// reason on the status line when it did not open.</summary>
        internal static void OpenFalconDownload(Action<string> setStatus)
        {
            if (ShellLaunch.TryStart(new ProcessStartInfo { FileName = FalconSdkDownloadUrl, UseShellExecute = true }, out string why)) return;
            setStatus(string.Format(CultureInfo.CurrentCulture, Strings.Instance.Falcon_DownloadNotOpened_Format, why));
        }

        /// <summary>The Novint Falcon card's buttons and a Falcon row's
        /// Calibrate Reach on the Devices page (issue #521), the Keyboard
        /// Screens card's shape: a dhd64.dll the user has, Force Dimension's
        /// driver for a Falcon with none, the SDK's download page, and a
        /// reach sweep on the selected Falcon.</summary>
        private void WireFalcon()
        {
            var settings = _viewModel.Settings;
            settings.PickFalconLibraryRequested += (_, _) =>
            {
                var dialog = new OpenFileDialog { Filter = Strings.Instance.Falcon_LibraryFilter };
                if (dialog.ShowDialog(this) != true) return;
                settings.FalconLibraryPath = IconPackageManager.MakeStoredPath(dialog.FileName);
                _settingsService.MarkDirty();
                _viewModel.SetStatus(string.Format(CultureInfo.CurrentCulture,
                    Strings.Instance.Status_FalconLibraryPicked_Format, System.IO.Path.GetFileName(dialog.FileName)));
            };

            settings.InstallFalconDriverRequested += async (_, _) =>
            {
                // The package the last sweep found, read on its worker.
                string package = FalconRuntime.Status.DriverPackage;
                await RunDriverOperationAsync(
                    Strings.Instance.Status_InstallingFalconDriver,
                    () => DriverInstaller.InstallFalconDriver(package),
                    () => { });
            };

            settings.OpenFalconDownloadRequested += (_, _) => OpenFalconDownload(text => _viewModel.SetStatus(text));

            _viewModel.Devices.FalconCalibrateRequested += (_, _) =>
            {
                if (_viewModel.Devices.SelectedDevice is { } row) CalibrateFalcon(row);
            };
        }

        /// <summary>Starts a reach sweep on the selected Falcon and keeps the
        /// result on the Falcon's record, where the row's reach comes from,
        /// for every slot it feeds.</summary>
        private void CalibrateFalcon(DeviceRowViewModel row)
        {
            var s = Strings.Instance;
            Guid guid = row.InstanceGuid;
            if (guid == Guid.Empty) return;
            var falcon = _inputService?.FindFalcon(guid);
            if (falcon == null || !falcon.IsAttached)
            {
                row.FalconCalibrationStatus = s.Falcon_Calibrate_NotOpen;
                return;
            }
            if (!falcon.BeginCalibration(result => Dispatcher.BeginInvoke(() => FalconCalibrated(row, guid, falcon, result))))
                return;
            row.FalconCalibrationStatus = s.Falcon_Calibrate_Running;
        }

        private void FalconCalibrated(DeviceRowViewModel row, Guid guid, FalconDevice falcon, FalconCalibration result)
        {
            var s = Strings.Instance;
            bool all = result.MeasuredX && result.MeasuredY && result.MeasuredZ;
            bool any = result.MeasuredX || result.MeasuredY || result.MeasuredZ;
            string line = InputService.FalconReachLine(result.Reach);
            if (any)
            {
                var ud = SettingsManager.FindDeviceByInstanceGuid(guid);
                if (ud != null)
                {
                    ud.FalconCalibration = FalconDevice.FormatReach(result.Reach);
                    _settingsService.MarkDirty();
                }
            }
            // The line is the row's own, so it waits there when the selection
            // moved during the sweep.
            row.FalconCalibrationStatus = all
                ? string.Format(CultureInfo.CurrentCulture, s.Falcon_Calibrate_Done_Format, line)
                : any
                    ? string.Format(CultureInfo.CurrentCulture, s.Falcon_Calibrate_Partial_Format, line)
                    : falcon.IsAttached ? s.Falcon_Calibrate_TooSmall : s.Falcon_Calibrate_Closed;
        }
    }
}
