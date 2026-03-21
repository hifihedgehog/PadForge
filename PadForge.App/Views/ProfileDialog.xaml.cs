using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    public partial class ProfileDialog : Wpf.Ui.Controls.FluentWindow
    {
        public string ProfileName => NameBox.Text?.Trim();

        public ObservableCollection<string> ExecutablePaths { get; } = new();

        public ProfileDialog()
        {
            InitializeComponent();
            ExeListBox.ItemsSource = ExecutablePaths;
            NameBox.Text = Strings.Instance.ProfileDialog_DefaultName;
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
            Title = Strings.Instance.ProfileDialog_Edit;
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title = Strings.Instance.FileDialog_SelectGameExe,
                Filter = Strings.Instance.FileDialog_ExeFilter,
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
