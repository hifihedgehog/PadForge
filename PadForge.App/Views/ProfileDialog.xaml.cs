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

        /// <summary>The polling override choices, by interval (#365).
        /// Index 0 is the "follow the global setting" sentinel (0 ms).</summary>
        private static readonly int[] PollingChoicesMs = { 0, 1, 2, 4, 8, 16 };

        /// <summary>Chosen polling override in milliseconds, 0 = follow the
        /// global setting (the ProfileData sentinel).</summary>
        public int PollingOverrideMs
            => PollingChoicesMs[System.Math.Clamp(PollingRateBox.SelectedIndex, 0, PollingChoicesMs.Length - 1)];

        public ProfileDialog()
        {
            InitializeComponent();
            // FluentWindow sets ExtendsContentIntoTitleBar, which zeroes
            // WindowChrome.CaptionHeight, and this dialog declares no
            // <ui:TitleBar>, so no point in the window was non-client and it
            // could not be moved at all. Same remedy MainWindow uses on its
            // branding bar. Controls that need the click (Button, TextBox,
            // ListBoxItem) mark this bubbling event handled, so the drag only
            // starts on inert chrome.
            MouseLeftButtonDown += (_, __) => { try { DragMove(); } catch { } };

            ExeListBox.ItemsSource = ExecutablePaths;
            PollingRateBox.ItemsSource = new[]
            {
                Strings.Instance.ProfileDialog_PollingDefault,
                "1000 Hz (1 ms)",
                "500 Hz (2 ms)",
                "250 Hz (4 ms)",
                "125 Hz (8 ms)",
                "62.5 Hz (16 ms)",
            };
            PollingRateBox.SelectedIndex = 0;
            NameBox.Text = Strings.Instance.ProfileDialog_DefaultName;
            NameBox.Focus();
            NameBox.SelectAll();
        }

        /// <summary>
        /// Pre-populates the dialog for editing an existing profile.
        /// </summary>
        public void LoadForEdit(string name, IEnumerable<string> exePaths, int pollingOverrideMs = 0)
        {
            NameBox.Text = name;
            ExecutablePaths.Clear();
            foreach (var p in exePaths)
                ExecutablePaths.Add(p);
            int idx = System.Array.IndexOf(PollingChoicesMs, pollingOverrideMs);
            PollingRateBox.SelectedIndex = idx >= 0 ? idx : 0;
            Title = Strings.Instance.ProfileDialog_Edit;
            ModeDescText.Text = Strings.Instance.ProfileDialog_EditDescription;
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
