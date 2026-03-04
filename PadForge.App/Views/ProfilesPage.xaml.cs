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

                // Determine output type label for the template field
                var profile = SettingsManager.Profiles.Find(p => p.Id == selected.Id);
                if (profile != null)
                {
                    // Use slot 0's output type for the "Output Type" template field
                    if (profile.SlotControllerTypes != null && profile.SlotCreated != null
                        && profile.SlotCreated.Length > 0 && profile.SlotCreated[0])
                    {
                        url += $"&output_type={Uri.EscapeDataString(FormatOutputType((VirtualControllerType)profile.SlotControllerTypes[0]))}";
                    }

                    // Build per-slot structured export
                    string exportJson = BuildPerSlotExport(profile);
                    if (!string.IsNullOrEmpty(exportJson))
                        url += $"&notes={Uri.EscapeDataString(exportJson)}";
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
                // Group entries by slot, export each slot's PadSetting
                var seenSlots = new HashSet<int>();
                foreach (var entry in profile.Entries)
                {
                    if (entry.MapTo < 0) continue;
                    if (!seenSlots.Add(entry.MapTo)) continue; // one export per slot

                    var ps = profile.PadSettings
                        .FirstOrDefault(p => p.PadSettingChecksum == entry.PadSettingChecksum);
                    if (ps == null) continue;

                    var outputType = entry.MapTo < profile.SlotControllerTypes.Length
                        ? (VirtualControllerType)profile.SlotControllerTypes[entry.MapTo]
                        : VirtualControllerType.Xbox360;

                    // Parse the PadSetting JSON back to a JsonElement so it nests properly
                    var psJsonStr = ps.ToJson();
                    var psElement = JsonDocument.Parse(psJsonStr).RootElement;

                    slots.Add(new
                    {
                        slot = entry.MapTo,
                        outputType = FormatOutputTypeShort(outputType),
                        padSetting = psElement
                    });
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
