using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HIDMaestro;
using Microsoft.Win32;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    /// <summary>
    /// Controller Presets (#574): the HIDMaestro presets the user added,
    /// each with its own Export and Delete, and the two ways to add one,
    /// from a file or from a controller that is connected now.
    /// </summary>
    public partial class ManageProfilesDialog : DialogWindow
    {
        /// <summary>Row model for the imported-profiles list.</summary>
        public sealed class ImportedProfileRow
        {
            public string Id { get; init; }
            public string Name { get; init; }

            /// <summary>Why the profile is not in the pickers, or null when
            /// it is (HMaestroProfileCatalog.GetUserProfileProblem).</summary>
            public string Problem { get; init; }

            public override string ToString() => Name;
        }

        /// <summary>Row model for the connected-devices list.</summary>
        public sealed class DeviceRow
        {
            public HMHidDeviceInfo Info { get; init; }

            public override string ToString() => DisplayName;

            public string DisplayName => string.IsNullOrWhiteSpace(Info?.ProductString)
                ? Strings.Instance.ImportFromDevice_UnnamedDevice
                : Info.ProductString;

            public string DetailLine
            {
                get
                {
                    string vidpid = $"VID_{Info.VendorId:X4}:PID_{Info.ProductId:X4}";
                    string usage = UsageLabel(Info.TopLevelUsagePage, Info.TopLevelUsage);
                    string mfg = string.IsNullOrWhiteSpace(Info.ManufacturerString)
                        ? ""
                        : $" — {Info.ManufacturerString}";
                    return $"{vidpid}  {usage}{mfg}";
                }
            }

            private static string UsageLabel(ushort page, ushort usage)
            {
                if (page == 0x01)
                {
                    return usage switch
                    {
                        0x02 => "(Mouse)",
                        0x04 => "(Joystick)",
                        0x05 => "(Gamepad)",
                        0x06 => "(Keyboard)",
                        0x08 => "(Multi-axis)",
                        _ => $"(0x01:0x{usage:X2})"
                    };
                }
                return $"(0x{page:X2}:0x{usage:X2})";
            }
        }

        /// <summary>Id of the profile the caller should auto-select on the
        /// current slot, if any. Set after a successful import; null
        /// otherwise. Caller checks this after ShowDialog returns true.</summary>
        public string ImportedProfileId { get; private set; }

        private readonly Services.SettingsService _settingsService;

        public ManageProfilesDialog(Services.SettingsService settingsService)
        {
            _settingsService = settingsService;
            InitializeComponent();

            Loaded += (_, _) =>
            {
                RefreshImportedList();
                PopulateDevices(sayCount: true);
                ImportFromFileButton.Focus();
            };
        }

        // ─────────────────────────────────────────────
        //  Imported profiles (top list)
        // ─────────────────────────────────────────────

        /// <summary>The presets added so far. With none, their heading and
        /// list are left out.</summary>
        private void RefreshImportedList()
        {
            var rows = _settingsService.GetUserProfileRows();
            ImportedList.ItemsSource = rows;
            ImportedBlock.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>A row's Export writes its preset to a file.</summary>
        private void ExportRow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ImportedProfileRow row) return;

            var dlg = new SaveFileDialog
            {
                FileName = $"{row.Id}.json",
                Filter = Strings.Instance.FileDialog_HidMaestroProfileFilter + "|" + Strings.Instance.FileDialog_AllFilesFilter,
                Title = Strings.Instance.ManageProfiles_ExportTitle
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                _settingsService.ExportUserProfile(row.Id, dlg.FileName);
                StatusText.Text = string.Format(
                    Strings.Instance.ManageProfiles_StatusExported_Format, dlg.FileName);
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Format(
                    Strings.Instance.ManageProfiles_StatusExportFailed_Format, ex.Message);
            }
        }

        /// <summary>A row's Delete asks, then removes its preset. The row
        /// and its buttons go with it, so the focus they held goes to
        /// Import From File.</summary>
        private void DeleteRow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ImportedProfileRow row) return;

            if (!ConfirmDialog.Show(this, Strings.Instance.ManageProfiles_DeleteTitle,
                    string.Format(Strings.Instance.ManageProfiles_DeleteConfirm_Format, row.Name),
                    Strings.Instance.ManageProfiles_Delete))
                return;

            _settingsService.RemoveUserProfile(row.Id);
            RefreshImportedList();
            StatusText.Text = string.Format(
                Strings.Instance.ManageProfiles_StatusDeleted_Format, row.Name);
            ImportFromFileButton.Focus();
        }

        private void ImportFromFileButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = Strings.Instance.FileDialog_HidMaestroProfileFilter + "|" + Strings.Instance.FileDialog_AllFilesFilter,
                Title = Strings.Instance.ManageProfiles_ImportFromFileTitle,
                CheckFileExists = true,
                Multiselect = false
            };
            if (dlg.ShowDialog(this) != true) return;

            string json;
            try
            {
                json = System.IO.File.ReadAllText(dlg.FileName);
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Format(
                    Strings.Instance.ManageProfiles_StatusImportFileFailed_Format, ex.Message);
                return;
            }

            string id = _settingsService.AddUserProfile(json, out string problem);
            if (string.IsNullOrWhiteSpace(id))
            {
                StatusText.Text = ImportFailedText(problem);
                return;
            }

            RefreshImportedList();
            ImportedProfileId = id;
            StatusText.Text = string.Format(
                Strings.Instance.ManageProfiles_StatusImportFileSucceeded_Format,
                System.IO.Path.GetFileName(dlg.FileName));
        }

        // ─────────────────────────────────────────────
        //  Connected devices (bottom list)
        // ─────────────────────────────────────────────

        /// <summary>Fills the picker with the devices connected now. The
        /// pick stays on the device it was on when that device is still
        /// listed, and otherwise goes to the first joystick, gamepad or
        /// multi-axis device, else the first device. The count is said as
        /// the dialog opens. A refill as the picker opens says nothing
        /// unless it fails, so the line keeps what the last action said.</summary>
        private void PopulateDevices(bool sayCount)
        {
            try
            {
                var devices = HMDeviceExtractor.ListDevices()
                    .OrderBy(d => d.VendorId)
                    .ThenBy(d => d.ProductId)
                    .ThenBy(d => d.TopLevelUsage)
                    .Select(d => new DeviceRow { Info = d })
                    .ToList();
                var before = (DeviceList.SelectedItem as DeviceRow)?.Info;
                DeviceList.ItemsSource = devices;
                int pick = PickAfterRefill(devices.Select(KeyOf).ToList(), before == null ? null : KeyOf(before));
                DeviceList.SelectedItem = pick >= 0 ? devices[pick] : null;

                if (sayCount)
                    StatusText.Text = string.Format(
                        Strings.Instance.ImportFromDevice_StatusFound_Format, devices.Count);
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Format(
                    Strings.Instance.ImportFromDevice_StatusEnumFailed_Format, ex.Message);
            }
        }

        private static (ushort Vendor, ushort Product, ushort Page, ushort Usage) KeyOf(DeviceRow row) => KeyOf(row.Info);

        private static (ushort Vendor, ushort Product, ushort Page, ushort Usage) KeyOf(HMHidDeviceInfo info)
            => (info.VendorId, info.ProductId, info.TopLevelUsagePage, info.TopLevelUsage);

        /// <summary>Which device to pick after the list was read again, by
        /// its place in the list: the device picked before when it is still
        /// there (same vendor, product and top-level usage), else the first
        /// joystick, gamepad or multi-axis device, else the first device,
        /// and -1 with no device at all.</summary>
        internal static int PickAfterRefill(
            IReadOnlyList<(ushort Vendor, ushort Product, ushort Page, ushort Usage)> devices,
            (ushort Vendor, ushort Product, ushort Page, ushort Usage)? picked)
        {
            if (picked != null)
                for (int i = 0; i < devices.Count; i++)
                    if (devices[i] == picked.Value) return i;
            for (int i = 0; i < devices.Count; i++)
                if (devices[i].Page == 0x01 && (devices[i].Usage == 0x04 || devices[i].Usage == 0x05 || devices[i].Usage == 0x08))
                    return i;
            return devices.Count > 0 ? 0 : -1;
        }

        private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ImportButton.IsEnabled = DeviceList.SelectedItem is DeviceRow;
        }

        // A controller plugged in since the dialog opened is in the list.
        private void DeviceList_DropDownOpened(object sender, EventArgs e) => PopulateDevices(sayCount: false);

        private void ImportButton_Click(object sender, RoutedEventArgs e) => TryImport();

        /// <summary>Reports whether anything was imported, rather than a flat
        /// false. The two import lanes end differently on purpose: the device
        /// lane sets DialogResult = true and closes, while the FILE lane stays
        /// open (it refreshes the list and writes a status line) so several
        /// files can be imported in one visit. That left the file lane's
        /// ImportedProfileId unreachable, because the only way out was this
        /// button and it always answered false, so the caller's
        /// `ShowDialog() == true` gate discarded it and the imported profile
        /// was never auto-selected on the slot. The caller also checks
        /// ImportedProfileId itself, so returning true here cannot select
        /// nothing.</summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
            => DialogResult = !string.IsNullOrWhiteSpace(ImportedProfileId);

        private void TryImport()
        {
            if (DeviceList.SelectedItem is not DeviceRow row) return;

            try
            {
                var profile = HMDeviceExtractor.Extract(row.Info);
                string rawJson = HMDeviceExtractor.ToJson(profile);
                string id = _settingsService.AddUserProfile(rawJson, out string problem);
                if (string.IsNullOrWhiteSpace(id))
                {
                    StatusText.Text = ImportFailedText(problem);
                    return;
                }
                ImportedProfileId = id;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Format(
                    Strings.Instance.ImportFromDevice_StatusExtractFailed_Format, ex.Message);
            }
        }

        /// <summary>The status line for an import that stored nothing: the
        /// reason HIDMaestro would leave the profile out of the pickers, or
        /// the parse failure when the JSON had no id to store it by.</summary>
        internal static string ImportFailedText(string problem) =>
            string.IsNullOrEmpty(problem)
                ? Strings.Instance.ImportFromDevice_StatusPersistFailed
                : string.Format(Strings.Instance.ManageProfiles_StatusImportRefused_Format, problem);
    }
}
