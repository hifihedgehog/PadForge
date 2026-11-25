using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using PadForge.Common.Input;
using PadForge.Common.Themes;
using PadForge.Resources.Strings;
using PadForge.Services;

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
        /// controllers. With the window's data, a program's Save names what
        /// it would let the foreground watcher turn on (#600). Without it, a
        /// program's Save asks by its reason.</summary>
        public void ForNew(PadForge.ViewModels.MainViewModel now = null)
        {
            Title = Strings.Instance.ProfileDialog_NewTitle;
            Subtitle = Strings.Instance.ProfileDialog_NewDescription;
            _new = true;
            Reads(now);
        }

        /// <summary>The Save As look, which the constructor gives, with the
        /// window's data a program's Save reads (#600).</summary>
        internal void ForSaveAs(PadForge.ViewModels.MainViewModel now) => Reads(now);

        /// <summary>Gives Save the reader a program's question card reads
        /// (#600, Reach.Starts), once the window's data is at hand.</summary>
        private void Reads(PadForge.ViewModels.MainViewModel now)
        {
            _now = now;
            PadForge.Common.Reach.SetStarts(SaveButton, now == null ? null : SaveStarts);
        }

        /// <summary>Whether the dialog shows the New look.</summary>
        private bool _new;

        /// <summary>The stored profile the Edit look edits, or null for New
        /// and Save As.</summary>
        internal ProfileData EditedProfile { get; private set; }

        /// <summary>The window's data, which a program's Save reads what is
        /// on now from (#600).</summary>
        private PadForge.ViewModels.MainViewModel _now;

        /// <summary>The Edit look for a stored profile: its name, its games
        /// and its polling rate.</summary>
        internal void LoadForEdit(ProfileData profile, PadForge.ViewModels.MainViewModel now)
        {
            LoadForEdit(profile.Name, string.IsNullOrEmpty(profile.ExecutableNames)
                ? Array.Empty<string>()
                : profile.ExecutableNames.Split('|', StringSplitOptions.RemoveEmptyEntries), profile.PollingRateOverrideHz);
            EditedProfile = profile;
            Reads(now);
        }

        /// <summary>Whether the dialog shows the Edit look.</summary>
        private bool _editing;

        /// <summary>The games the Edit look opened with.</summary>
        private string[] _gamesBefore = Array.Empty<string>();

        /// <summary>What a program's Save would let the foreground watcher
        /// turn on that listens on the network (#600), as the question card's
        /// lines. A game added while Switch Profiles by Game is on puts the
        /// profile in force when the game is in front, and the default when
        /// it leaves, so the card names what each holds, on or off now
        /// (AssistantScan.WatcherLines): in the Edit look what the stored
        /// profile holds, in Save As what is on now, which the new profile
        /// holds, and in New nothing of its own. The switch counts as on
        /// while the page or the watcher's own flag, which follows the page
        /// at the next save, says so.</summary>
        internal IReadOnlyList<string> SaveStarts()
        {
            if (_now == null || (_editing && EditedProfile == null)) return Array.Empty<string>();
            bool switching = _now.Settings.EnableAutoProfileSwitching || SettingsManager.EnableAutoProfileSwitching;
            if (!switching || !PadForge.Services.Automation.AssistantScan.AddsAGame(_gamesBefore, ExecutablePaths)) return Array.Empty<string>();
            var held = _editing ? PadForge.Services.Automation.AssistantScan.HeldExtras(EditedProfile, _now.Dashboard)
                : _new ? new List<string>()
                : PadForge.Services.Automation.AssistantScan.LiveExtras(_now.Dashboard, headTracking: false);
            return PadForge.Services.Automation.AssistantScan.WatcherLines(_editing ? EditedProfile.Name : ProfileName, held, _now.Dashboard);
        }

        /// <summary>
        /// Pre-populates the dialog for editing an existing profile.
        /// </summary>
        public void LoadForEdit(string name, IEnumerable<string> exePaths, int pollingOverrideHz = 0)
        {
            _editing = true;
            // With no stored profile there is nothing to read, so a
            // program's Save asks by its reason.
            PadForge.Common.Reach.SetStarts(SaveButton, null);
            NameBox.Text = name;
            ExecutablePaths.Clear();
            foreach (var p in exePaths)
                ExecutablePaths.Add(p);
            _gamesBefore = ExecutablePaths.ToArray();
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

        // ─────────────────────────────────────────────
        //  The game's look (#591)
        // ─────────────────────────────────────────────

        private SteamGameArt _lookArt;
        private int _lookAppId;
        private string _lookGame;
        private ProfileGameLook _look;
        private GameLooks.Made _madeLook;
        private bool _lookQuiet, _lookBusy;
        private CancellationTokenSource _lookCancel;

        /// <summary>Whether the dialog showed the game look's switch, so
        /// <see cref="GameLook"/> is the user's answer.</summary>
        internal bool GameLookOffered { get; private set; }

        /// <summary>The look the profile wears after Save, or null for none.</summary>
        internal ProfileGameLook GameLook => _look;

        /// <summary>A look made in this dialog, whose picture is kept when
        /// the profile takes it, or null when the dialog made none.</summary>
        internal GameLooks.Made MadeGameLook => _madeLook;

        /// <summary>Shows the game look's switch for a profile that knows
        /// its Steam game, set to what the profile has now.</summary>
        internal void OfferGameLook(int appId, string game, ProfileGameLook current, SteamGameArt art)
        {
            if (appId <= 0 || art == null) return;
            GameLookOffered = true;
            _lookAppId = appId;
            _lookGame = game;
            _lookArt = art;
            _look = current;
            GameLookPanel.Visibility = Visibility.Visible;
            SetLookSwitch(current != null);
            ShowLookState(null);
            Closed += (_, _) => _lookCancel?.Cancel();
        }

        private void SetLookSwitch(bool on)
        {
            _lookQuiet = true;
            try { GameLookSwitch.IsChecked = on; }
            finally { _lookQuiet = false; }
        }

        /// <summary>The line under the switch: what is going on, or that
        /// the look's picture is not on this PC, with the button that gets
        /// it. Nothing while all is well.</summary>
        private void ShowLookState(string status)
        {
            bool missing = _look != null && _madeLook == null && !GameLooks.HasPicture(_look);
            string text = status ?? (missing ? Strings.Instance.Profile_GameLook_NoPicture : null);
            GameLookStatus.Text = text ?? string.Empty;
            GameLookGetButton.Visibility = missing && !_lookBusy ? Visibility.Visible : Visibility.Collapsed;
            GameLookLine.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void GameLookSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_lookQuiet) return;
            if (GameLookSwitch.IsChecked == true)
            {
                await MakeLookAsync();
                return;
            }
            _lookCancel?.Cancel();
            _look = null;
            _madeLook = null;
            ShowLookState(null);
        }

        private async void GameLookGet_Click(object sender, RoutedEventArgs e) => await MakeLookAsync();

        /// <summary>Makes the game's look. Save waits for it. With no
        /// picture to be had, a look the profile already wears keeps its
        /// colors and a new one is not made.</summary>
        private async Task MakeLookAsync()
        {
            _lookCancel?.Cancel();
            var cancel = _lookCancel = new CancellationTokenSource();
            var s = Strings.Instance;
            _lookBusy = true;
            SaveButton.IsEnabled = false;
            ShowLookState(s.ThemeBuilder_Fetching);
            try
            {
                var made = await GameLooks.MakeAsync(_lookArt, _lookAppId, _lookGame, cancel.Token);
                if (cancel.IsCancellationRequested) return;
                _lookBusy = false;
                if (made.Look != null)
                {
                    _look = made.Look;
                    _madeLook = made;
                    ShowLookState(null);
                    return;
                }
                if (_look == null) SetLookSwitch(false);
                ShowLookState(GameLooks.FaultText(made.Fault));
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (ReferenceEquals(_lookCancel, cancel))
                {
                    _lookBusy = false;
                    SaveButton.IsEnabled = true;
                }
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
    }
}
