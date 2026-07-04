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
    }
}
