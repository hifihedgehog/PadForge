using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using PadForge.Common;

namespace PadForge.Views
{
    public partial class ConfigSelectionDialog : Window
    {
        public GameConfigEntry SelectedConfig { get; private set; }

        public ConfigSelectionDialog(IList<GameConfigEntry> configs)
        {
            InitializeComponent();
            ConfigListBox.ItemsSource = configs;
            if (configs.Count > 0)
                ConfigListBox.SelectedIndex = 0;
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (ConfigListBox.SelectedItem is GameConfigEntry entry)
            {
                SelectedConfig = entry;
                DialogResult = true;
            }
        }

        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void ConfigList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ConfigListBox.SelectedItem is GameConfigEntry entry)
            {
                SelectedConfig = entry;
                DialogResult = true;
            }
        }
    }
}
