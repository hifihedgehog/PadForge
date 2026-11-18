using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using PadForge.Common.Input;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    /// <summary>New, Save As and Edit Profile (#574): the profile's name and
    /// the games that load it. The constructor gives the Save As look,
    /// <see cref="ForNew"/> the New look and <see cref="LoadForEdit"/> the
    /// Edit look.</summary>
    public partial class ProfileDialog : DialogWindow
    {
        public string ProfileName => NameBox.Text?.Trim();

        public ObservableCollection<string> ExecutablePaths { get; } = new();

        /// <summary>Chosen polling override in passes a second, 0 = follow
        /// the global setting (the ProfileData sentinel).</summary>
        public int PollingOverrideHz
            => _pollingHz[System.Math.Clamp(PollingRateBox.SelectedIndex, 0, _pollingHz.Count - 1)];

        /// <summary>The rates the box holds, entry for entry and fastest
        /// first after the "follow the global setting" entry (0): the
        /// choices of <see cref="PollingRates.Choices"/> (#365), and a
        /// profile's own rate when it is none of them.</summary>
        private readonly List<int> _pollingHz = new() { 0 };

        public ProfileDialog()
        {
            InitializeComponent();

            ExecutablePaths.CollectionChanged += (_, _) => RebuildGames();
            _pollingHz.AddRange(PollingRates.Choices);
            PollingRateBox.ItemsSource = PollingEntries();
            PollingRateBox.SelectedIndex = 0;
            NameBox.Text = Strings.Instance.ProfileDialog_DefaultName;
            NameBox.Focus();
            NameBox.SelectAll();
        }

        /// <summary>The New Profile look: a profile that starts with no
        /// controllers.</summary>
        public void ForNew()
        {
            Title = Strings.Instance.ProfileDialog_NewTitle;
            Subtitle = Strings.Instance.ProfileDialog_NewDescription;
        }

        /// <summary>
        /// Pre-populates the dialog for editing an existing profile.
        /// </summary>
        public void LoadForEdit(string name, IEnumerable<string> exePaths, int pollingOverrideHz = 0)
        {
            NameBox.Text = name;
            ExecutablePaths.Clear();
            foreach (var p in exePaths)
                ExecutablePaths.Add(p);
            // The engine holds an override to the range its loop runs in,
            // so that is what a stored one means. A rate the choices do not
            // hold (a profile from before PadForge 6 at 3 ms, a hand-edited
            // file) gets an entry of its own: shown as "follow the global
            // setting", a save of this dialog wrote 0 over it.
            int hz = pollingOverrideHz <= 0 ? 0 : PollingRates.Resolve(pollingOverrideHz, pollingOverrideHz);
            int idx = _pollingHz.IndexOf(hz);
            if (idx < 0)
            {
                // After the follow entry, where the list stays fastest first.
                idx = _pollingHz.FindIndex(1, held => held < hz);
                if (idx < 0) idx = _pollingHz.Count;
                _pollingHz.Insert(idx, hz);
                PollingRateBox.ItemsSource = PollingEntries();
            }
            PollingRateBox.SelectedIndex = idx;
            Title = Strings.Instance.ProfileDialog_Edit;
            Subtitle = Strings.Instance.ProfileDialog_EditDescription;
            // A profile with a rate of its own shows it: More opens on it.
            ProfileMore.IsExpanded = PollingRateBox.SelectedIndex != 0;
        }

        /// <summary>The box's entries as it shows them: the words for
        /// following the global setting, then each rate with the time
        /// between two of its passes.</summary>
        private string[] PollingEntries()
        {
            var entries = new string[_pollingHz.Count];
            entries[0] = Strings.Instance.ProfileDialog_PollingDefault;
            for (int i = 1; i < entries.Length; i++)
                entries[i] = PollingRates.Label(_pollingHz[i], System.Globalization.CultureInfo.CurrentCulture,
                    Strings.Instance.Common_Hz, Strings.Instance.Common_Ms);
            return entries;
        }

        /// <summary>A game's file name, as its chip shows it. A stored entry
        /// with no folder is its own name.</summary>
        internal static string GameName(string path)
        {
            string name = Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? path ?? string.Empty : name;
        }

        /// <summary>One chip per game, ahead of the two buttons that add
        /// one: the file name with the whole path as its tooltip, and a
        /// remove button named for the game.</summary>
        private void RebuildGames()
        {
            for (int i = GamesLine.Children.Count - 1; i >= 0; i--)
                if (GamesLine.Children[i] is Border)
                    GamesLine.Children.RemoveAt(i);

            int at = 0;
            foreach (string path in ExecutablePaths)
            {
                string name = GameName(path);
                var text = new TextBlock
                {
                    Text = name,
                    FontSize = 13,
                    MaxWidth = 320,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                string removeName = string.Format(Strings.Instance.ProfileDialog_RemoveGame_Format, name);
                var remove = new Wpf.Ui.Controls.Button { Tag = path, ToolTip = removeName };
                remove.SetResourceReference(StyleProperty, "DialogChipRemove");
                AutomationProperties.SetName(remove, removeName);
                remove.Click += RemoveGame_Click;

                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(text);
                line.Children.Add(remove);

                var chip = new Border { Child = line, ToolTip = path };
                chip.SetResourceReference(StyleProperty, "DialogChip");
                GamesLine.Children.Insert(at++, chip);
            }
        }

        /// <summary>Adds a game unless the list holds it. Windows reads a
        /// path the same in either case, so the list does too.</summary>
        private void AddGame(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            foreach (string have in ExecutablePaths)
                if (string.Equals(have, path, StringComparison.OrdinalIgnoreCase))
                    return;
            ExecutablePaths.Add(path);
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
                    AddGame(path);
            }
        }

        /// <summary>The programs that have a window open now, in a menu
        /// under the button, each by its file name with its path as the
        /// tooltip. A pick adds it to the games.</summary>
        private void AddRunningGame_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = AddRunningGameButton,
                Placement = PlacementMode.Bottom,
            };
            IReadOnlyList<string> programs = WindowedPrograms.List();
            if (programs.Count == 0)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = Strings.Instance.ProfileDialog_NoRunningGames,
                    IsEnabled = false,
                });
            }
            foreach (string path in programs)
            {
                // A menu reads one underscore as the mark of an access key.
                var item = new MenuItem
                {
                    Header = GameName(path).Replace("_", "__"),
                    ToolTip = path,
                    Tag = path,
                };
                item.Click += (s, _) => AddGame((string)((MenuItem)s).Tag);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        /// <summary>A chip's remove takes its game off the list. The
        /// keyboard's focus goes to the chip that takes its place, to the
        /// one before it when it was the last, or to Add a Running Game
        /// when none is left.</summary>
        private void RemoveGame_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string path) return;
            int at = ExecutablePaths.IndexOf(path);
            if (at < 0) return;
            bool hadFocus = (sender as UIElement)?.IsKeyboardFocusWithin == true;
            ExecutablePaths.RemoveAt(at);
            if (!hadFocus) return;

            int next = Math.Min(at, ExecutablePaths.Count - 1);
            if (next >= 0 && GamesLine.Children[next] is Border { Child: Panel line } && line.Children.Count > 1)
                line.Children[1].Focus();
            else
                AddRunningGameButton.Focus();
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
    }
}
