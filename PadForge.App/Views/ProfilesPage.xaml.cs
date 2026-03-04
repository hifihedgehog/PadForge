using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            }

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }
}
