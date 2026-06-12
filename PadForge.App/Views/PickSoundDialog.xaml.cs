using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace PadForge.Views
{
    /// <summary>List picker for a sound inside a sound package
    /// (issue #83). Same FluentWindow chrome as the other dialogs.</summary>
    public partial class PickSoundDialog : Wpf.Ui.Controls.FluentWindow
    {
        /// <summary>The chosen entry, or null when the dialog was
        /// cancelled.</summary>
        public string SelectedSound => SoundList.SelectedItem as string;

        public PickSoundDialog(string description, List<string> items)
        {
            InitializeComponent();
            Title = description;
            DescriptionText.Text = description;
            SoundList.ItemsSource = items;
            if (items != null && items.Count > 0)
                SoundList.SelectedIndex = 0;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (SoundList.SelectedItem != null)
                DialogResult = true;
        }

        private void SoundList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SoundList.SelectedItem != null)
                DialogResult = true;
        }
    }
}
