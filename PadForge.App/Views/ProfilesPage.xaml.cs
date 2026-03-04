using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Views
{
    public partial class ProfilesPage : UserControl
    {
        public ProfilesPage()
        {
            InitializeComponent();
        }

        private void ProfileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is SettingsViewModel vm &&
                vm.LoadProfileCommand.CanExecute(null))
            {
                vm.LoadProfileCommand.Execute(null);
            }
        }

        private void SubmitGameConfig_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SettingsViewModel;
            var selected = vm?.SelectedProfile;

            string url = "https://github.com/hifihedgehog/PadForge/issues/new?template=game_config.yml";

            if (selected != null)
            {
                url += $"&game_name={Uri.EscapeDataString(selected.Name)}";
                if (!string.IsNullOrEmpty(selected.Executables))
                    url += $"&exe_names={Uri.EscapeDataString(selected.Executables)}";

                var profile = SettingsManager.Profiles.Find(p => p.Id == selected.Id);
                if (profile != null)
                {
                    if (profile.SlotControllerTypes != null && profile.SlotCreated != null
                        && profile.SlotCreated.Length > 0 && profile.SlotCreated[0])
                    {
                        url += $"&output_type={Uri.EscapeDataString(FormatOutputType((VirtualControllerType)profile.SlotControllerTypes[0]))}";
                    }

                    // Copy settings JSON to clipboard (too large for URL params).
                    string exportJson = BuildPerSlotExport(profile);
                    if (!string.IsNullOrEmpty(exportJson))
                    {
                        try
                        {
                            Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, exportJson), true);
                        }
                        catch
                        {
                            // Clipboard locked — ignore, user will see the message and can retry.
                        }
                        MessageBox.Show(
                            "Your profile settings have been copied to the clipboard.\n\n" +
                            "Paste them into the \"Exported Settings\" field on the GitHub issue form.",
                            "Settings Copied", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        private static string BuildPerSlotExport(ProfileData profile)
        {
            var slots = new List<object>();

            if (profile.Entries != null && profile.PadSettings != null &&
                profile.SlotCreated != null && profile.SlotControllerTypes != null)
            {
                // Group entries by slot — multiple devices can share a slot.
                var grouped = profile.Entries
                    .Where(en => en.MapTo >= 0)
                    .GroupBy(en => en.MapTo);

                foreach (var group in grouped)
                {
                    int slot = group.Key;
                    var outputType = slot < profile.SlotControllerTypes.Length
                        ? (VirtualControllerType)profile.SlotControllerTypes[slot]
                        : VirtualControllerType.Xbox360;

                    // Collect each device's PadSetting for this slot.
                    var padSettings = new List<JsonElement>();
                    foreach (var entry in group)
                    {
                        var ps = profile.PadSettings
                            .FirstOrDefault(p => p.PadSettingChecksum == entry.PadSettingChecksum);
                        if (ps == null) continue;

                        var psElement = JsonDocument.Parse(ps.ToJson()).RootElement;
                        padSettings.Add(psElement);
                    }

                    if (padSettings.Count == 0) continue;

                    if (padSettings.Count == 1)
                    {
                        // Single device on this slot — use singular padSetting for simplicity.
                        slots.Add(new
                        {
                            slot,
                            outputType = FormatOutputTypeShort(outputType),
                            padSetting = padSettings[0]
                        });
                    }
                    else
                    {
                        // Multiple devices on this slot — use padSettings array.
                        slots.Add(new
                        {
                            slot,
                            outputType = FormatOutputTypeShort(outputType),
                            padSettings
                        });
                    }
                }
            }

            if (slots.Count == 0 && !profile.EnableDsuMotionServer)
                return null;

            var export = new Dictionary<string, object>();
            if (slots.Count > 0)
                export["slots"] = slots;
            if (profile.EnableDsuMotionServer)
                export["enableDsuMotionServer"] = true;

            return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
        }

        private static string FormatOutputType(VirtualControllerType type) => type switch
        {
            VirtualControllerType.DualShock4 => "DualShock 4",
            VirtualControllerType.VJoy => "vJoy",
            _ => "Xbox 360"
        };

        private static string FormatOutputTypeShort(VirtualControllerType type) => type switch
        {
            VirtualControllerType.DualShock4 => "DS4",
            VirtualControllerType.VJoy => "vJoy",
            _ => "Xbox360"
        };
    }
}
