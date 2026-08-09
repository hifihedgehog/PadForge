using System.Windows.Controls;

namespace PadForge.Views
{
    public partial class SettingsPage : UserControl
    {
        public SettingsPage()
        {
            InitializeComponent();
        }

        // Re-run the Ember welcome tour (#175).
        private void ShowTour_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (System.Windows.Application.Current.MainWindow is PadForge.MainWindow main)
                main.StartFirstRunTour();
        }

        // Pick the Steam-free SteamVR install directory (#49). Same
        // OpenFolderDialog shape the macro working-dir browse uses.
        private void SteamVrBrowse_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is not PadForge.ViewModels.SettingsViewModel vm) return;
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = PadForge.Resources.Strings.Strings.Instance.Settings_SteamVRInstallDir,
            };
            try
            {
                if (!string.IsNullOrEmpty(vm.SteamVrInstallDir)
                    && System.IO.Directory.Exists(vm.SteamVrInstallDir))
                    dlg.InitialDirectory = vm.SteamVrInstallDir;
            }
            catch { }
            if (dlg.ShowDialog() == true)
                vm.SteamVrInstallDir = dlg.FolderName;
        }
    }
}
