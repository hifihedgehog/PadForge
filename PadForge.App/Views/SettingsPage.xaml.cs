using System.Windows.Controls;

namespace PadForge.Views
{
    public partial class SettingsPage : UserControl
    {
        /// <summary>Static InputService reference wired by MainWindow at
        /// startup, same shape as the other pages.</summary>
        public static PadForge.Services.InputService InputService { get; set; }

        public SettingsPage()
        {
            InitializeComponent();
        }

        // Battery Alerts test (#293): fire the notification pipeline with a
        // synthetic low-battery event.
        private void TestBatteryNotify_Click(object sender, System.Windows.RoutedEventArgs e)
            => InputService?.TestBatteryNotification();

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
