using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
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

                // Find the full ProfileData to export PadSettings
                var profile = SettingsManager.Profiles.Find(p => p.Id == selected.Id);
                if (profile != null)
                {
                    // Determine output type from slot 0
                    if (profile.SlotControllerTypes != null && profile.SlotCreated != null
                        && profile.SlotCreated.Length > 0 && profile.SlotCreated[0])
                    {
                        var outputType = (VirtualControllerType)profile.SlotControllerTypes[0];
                        string outputLabel = outputType switch
                        {
                            VirtualControllerType.DualShock4 => "DualShock 4",
                            VirtualControllerType.VJoy => "vJoy",
                            _ => "Xbox 360"
                        };
                        url += $"&output_type={Uri.EscapeDataString(outputLabel)}";
                    }

                    // Export first PadSetting as structured JSON
                    var padSetting = profile.PadSettings?.FirstOrDefault();
                    if (padSetting != null)
                    {
                        string settingsJson = padSetting.ToJson();
                        string dsuNote = profile.EnableDsuMotionServer ? "DSU motion server: enabled\n\n" : "";
                        url += $"&notes={Uri.EscapeDataString(dsuNote + "```json\n" + settingsJson + "\n```")}";
                    }
                    else if (profile.EnableDsuMotionServer)
                    {
                        url += $"&notes={Uri.EscapeDataString("DSU motion server: enabled")}";
                    }
                }
            }

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }
}
