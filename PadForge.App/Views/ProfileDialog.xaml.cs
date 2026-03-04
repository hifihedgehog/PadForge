using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;

namespace PadForge.Views
{
    public partial class ProfileDialog : Window
    {
        public string ProfileName => NameBox.Text?.Trim();

        public ObservableCollection<string> ExecutablePaths { get; } = new();

        public ProfileDialog()
        {
            InitializeComponent();
            ExeListBox.ItemsSource = ExecutablePaths;
            NameBox.Focus();
            NameBox.SelectAll();
        }

        /// <summary>
        /// Pre-populates the dialog for editing an existing profile.
        /// </summary>
        public void LoadForEdit(string name, IEnumerable<string> exePaths)
        {
            NameBox.Text = name;
            ExecutablePaths.Clear();
            foreach (var p in exePaths)
                ExecutablePaths.Add(p);
            Title = "Edit Profile";
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title = "Select Game Executable",
                Filter = "Executables (*.exe)|*.exe|All Files (*.*)|*.*",
                Multiselect = true
            };

            if (ofd.ShowDialog(this) == true)
            {
                foreach (var path in ofd.FileNames)
                {
                    if (!ExecutablePaths.Contains(path))
                        ExecutablePaths.Add(path);
                }
            }
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (ExeListBox.SelectedItem is string selected)
                ExecutablePaths.Remove(selected);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                NameBox.Focus();
                return;
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
