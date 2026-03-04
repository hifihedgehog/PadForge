using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using PadForge.Common;

namespace PadForge.Views
{
    public partial class ProfileDialog : Window
    {
        public string ProfileName => NameBox.Text?.Trim();

        public ObservableCollection<string> ExecutablePaths { get; } = new();

        public List<GameConfigEntry> MatchedConfigs { get; } = new();

        public bool MatchByFilenameOnly => FilenameOnlyCheckBox.IsChecked == true;

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
        public void LoadForEdit(string name, IEnumerable<string> exePaths, bool matchByFilenameOnly)
        {
            NameBox.Text = name;
            ExecutablePaths.Clear();
            foreach (var p in exePaths)
                ExecutablePaths.Add(p);
            FilenameOnlyCheckBox.IsChecked = matchByFilenameOnly;
            Title = "Edit Profile";
            UpdateConfigHint();
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
                UpdateConfigHint();
            }
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (ExeListBox.SelectedItem is string selected)
            {
                ExecutablePaths.Remove(selected);
                UpdateConfigHint();
            }
        }

        private void UpdateConfigHint()
        {
            MatchedConfigs.Clear();
            foreach (var path in ExecutablePaths)
            {
                var exeName = Path.GetFileName(path);
                var matches = GameConfigDatabase.FindByExeName(exeName);
                foreach (var m in matches)
                    if (!MatchedConfigs.Contains(m))
                        MatchedConfigs.Add(m);
            }

            if (MatchedConfigs.Count == 1)
            {
                var c = MatchedConfigs[0];
                ConfigHintText.Text = $"Config available: {c.Label} by {c.Author} \u2014 {c.Notes}";
                ConfigHintText.Visibility = Visibility.Visible;
            }
            else if (MatchedConfigs.Count > 1)
            {
                ConfigHintText.Text = $"{MatchedConfigs.Count} community configs available for this game.";
                ConfigHintText.Visibility = Visibility.Visible;
            }
            else
            {
                ConfigHintText.Visibility = Visibility.Collapsed;
            }
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
