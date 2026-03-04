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
    }
}
