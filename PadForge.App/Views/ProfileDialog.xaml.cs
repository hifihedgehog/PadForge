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

        /// <summary>The polling override choices, by interval (#365).
        /// Index 0 is the "follow the global setting" sentinel (0 ms).</summary>
        private static readonly int[] PollingChoicesMs = { 0, 1, 2, 4, 8, 16 };

        /// <summary>Chosen polling override in milliseconds, 0 = follow the
        /// global setting (the ProfileData sentinel).</summary>
        public int PollingOverrideMs
            => _pollingMs[System.Math.Clamp(PollingRateBox.SelectedIndex, 0, _pollingMs.Count - 1)];

        /// <summary>The intervals the box holds, entry for entry: the six
        /// choices, and a profile's own interval when it is none of them.</summary>
        private readonly System.Collections.Generic.List<int> _pollingMs = new(PollingChoicesMs);

        public ProfileDialog()
        {
            InitializeComponent();

            ExecutablePaths.CollectionChanged += (_, _) => RebuildGames();
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
        public void LoadForEdit(string name, IEnumerable<string> exePaths, int pollingOverrideMs = 0)
        {
            NameBox.Text = name;
            ExecutablePaths.Clear();
            foreach (var p in exePaths)
                ExecutablePaths.Add(p);
            // The engine holds an override to 1..16 ms, so that is what a
            // stored one means. An interval the six choices do not hold (a
            // hand-edited file) gets an entry of its own: shown as "follow
            // the global setting", a save of this dialog wrote 0 over it.
            int ms = pollingOverrideMs <= 0 ? 0 : System.Math.Min(pollingOverrideMs, 16);
            int idx = _pollingMs.IndexOf(ms);
            if (idx < 0)
            {
                idx = _pollingMs.FindIndex(held => held > ms);
                if (idx < 0) idx = _pollingMs.Count;
                _pollingMs.Insert(idx, ms);
                var entries = new System.Collections.Generic.List<string>((string[])PollingRateBox.ItemsSource);
                entries.Insert(idx, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:0.#} Hz ({1} ms)", 1000.0 / ms, ms));
                PollingRateBox.ItemsSource = entries.ToArray();
            }
            PollingRateBox.SelectedIndex = idx;
            Title = Strings.Instance.ProfileDialog_Edit;
            Subtitle = Strings.Instance.ProfileDialog_EditDescription;
            // A profile with a rate of its own shows it: More opens on it.
            ProfileMore.IsExpanded = PollingRateBox.SelectedIndex != 0;
        }

        /// <summary>A game's file name, as its chip shows it. A stored entry
        /// with no folder is its own name.</summary>
        internal static string GameName(string path)
        {
            string name = Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? path ?? string.Empty : name;
        }

        /// <summary>One chip per game, ahead of the two buttons that add
        /// one: the file name in the code face with the whole path as its
        /// tooltip, and a remove button named for the game.</summary>
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
                text.SetResourceReference(TextBlock.FontFamilyProperty, "TelemetryFontFamily");

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
