using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HIDMaestro;
using Microsoft.Win32;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    public partial class ManageProfilesDialog : Wpf.Ui.Controls.FluentWindow
    {
        /// <summary>Row model for the imported-profiles list.</summary>
        public sealed class ImportedProfileRow
        {
            public string Id { get; init; }
            public string Name { get; init; }
        }

        /// <summary>Row model for the connected-devices list.</summary>
        public sealed class DeviceRow
        {
            public HMHidDeviceInfo Info { get; init; }

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
            // FluentWindow sets ExtendsContentIntoTitleBar, which zeroes
            // WindowChrome.CaptionHeight, and this dialog declares no
            // <ui:TitleBar>, so no point in the window was non-client and it
            // could not be moved at all. Same remedy MainWindow uses on its
            // branding bar. Controls that need the click (Button, TextBox,
            // ListBoxItem) mark this bubbling event handled, so the drag only
            // starts on inert chrome.
            MouseLeftButtonDown += (_, __) => { try { DragMove(); } catch { } };

            Loaded += (_, _) =>
            {
                RefreshImportedList();
                PopulateDevices();
            };
        }

        // ─────────────────────────────────────────────
        //  Imported profiles (top list)
        // ─────────────────────────────────────────────

        private void RefreshImportedList()
        {
            var rows = _settingsService.GetUserProfileRows();
            ImportedList.ItemsSource = rows;
            ImportedHeaderText.Text = string.Format(
                Strings.Instance.ManageProfiles_YourProfiles_Format, rows.Count);
        }

        private void ImportedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool hasSelection = ImportedList.SelectedItem is ImportedProfileRow;
            ExportButton.IsEnabled = hasSelection;
            DeleteButton.IsEnabled = hasSelection;
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (ImportedList.SelectedItem is not ImportedProfileRow row) return;

            var dlg = new SaveFileDialog
            {
                FileName = $"{row.Id}.json",
                Filter = "HIDMaestro profile (*.json)|*.json|All files (*.*)|*.*",
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

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (ImportedList.SelectedItem is not ImportedProfileRow row) return;

            var confirm = MessageBox.Show(this,
                string.Format(Strings.Instance.ManageProfiles_DeleteConfirm_Format, row.Name),
                Strings.Instance.ManageProfiles_DeleteTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _settingsService.RemoveUserProfile(row.Id);
            RefreshImportedList();
            StatusText.Text = string.Format(
                Strings.Instance.ManageProfiles_StatusDeleted_Format, row.Name);
        }

        private void ImportFromFileButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "HIDMaestro profile (*.json)|*.json|All files (*.*)|*.*",
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

            string id = _settingsService.AddUserProfile(json);
            if (string.IsNullOrWhiteSpace(id))
            {
                StatusText.Text = Strings.Instance.ImportFromDevice_StatusPersistFailed;
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

        private void PopulateDevices()
        {
            try
            {
                var devices = HMDeviceExtractor.ListDevices()
                    .OrderBy(d => d.VendorId)
                    .ThenBy(d => d.ProductId)
                    .ThenBy(d => d.TopLevelUsage)
                    .Select(d => new DeviceRow { Info = d })
                    .ToList();
                DeviceList.ItemsSource = devices;

                var preferred = devices.FirstOrDefault(r =>
                    r.Info.TopLevelUsagePage == 0x01 &&
                    (r.Info.TopLevelUsage == 0x04 ||
                     r.Info.TopLevelUsage == 0x05 ||
                     r.Info.TopLevelUsage == 0x08));
                DeviceList.SelectedItem = preferred ?? devices.FirstOrDefault();

                StatusText.Text = string.Format(
                    Strings.Instance.ImportFromDevice_StatusFound_Format, devices.Count);
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Format(
                    Strings.Instance.ImportFromDevice_StatusEnumFailed_Format, ex.Message);
            }
        }

        private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ImportButton.IsEnabled = DeviceList.SelectedItem is DeviceRow;
        }

        private void DeviceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DeviceList.SelectedItem is DeviceRow) TryImport();
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e) => TryImport();

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => PopulateDevices();

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
                string id = _settingsService.AddUserProfile(rawJson);
                if (string.IsNullOrWhiteSpace(id))
                {
                    StatusText.Text = Strings.Instance.ImportFromDevice_StatusPersistFailed;
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
    }
}
