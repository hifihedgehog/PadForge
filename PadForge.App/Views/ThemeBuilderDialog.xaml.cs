using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PadForge.Common;
using PadForge.Common.Themes;
using PadForge.Controls;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>
    /// New Theme and Edit Theme (#591): a theme's name, its base, its
    /// picture and its four colors, with PadForge wearing the theme while
    /// the dialog is open.
    ///
    /// The picture comes from a Steam game (its wide library picture and
    /// its logo, through <see cref="SteamGameArt"/>) or from a file, and
    /// <see cref="ThemePicture"/> makes it fit. A theme opened for editing
    /// keeps the picture its file holds until another is chosen.
    /// </summary>
    public partial class ThemeBuilderDialog : DialogWindow
    {
        /// <summary>One of the four colors: its row's parts and its value.</summary>
        private sealed class Shade
        {
            internal string Label, Tip;
            internal Color Value;
            internal ToggleButton Swatch;
            internal Border Chip;
            internal TextBox Hex;
        }

        /// <summary>A game the search found. Its ToString is what a list reads it as.</summary>
        private sealed record GameHit(int AppId, string Name)
        {
            public override string ToString() => Name;
        }

        private readonly SettingsViewModel _settings;
        private readonly ThemeDefinition _existing;
        private readonly SteamGameArt _steam;
        private readonly Shade[] _shades;
        private readonly DispatcherTimer _previewTimer, _searchTimer;
        private Shade _picking;
        // True until the dialog holds its starting values: the slider and
        // the switch report the values their markup gives them while the
        // dialog is still being built.
        private bool _loading = true;
        private bool _colorsTouched;
        private CancellationTokenSource _searchCancel, _fetchCancel;

        // The picture: a source this dialog makes it from, or the one the edited file holds.
        private BitmapSource _photo, _logo;
        private byte[] _keptPicture;
        private string _keptPictureName;
        private double _keptTile = ThemeDefinition.DefaultTileAt;
        private string _credits;
        private BitmapSource _composed;
        private (BitmapSource Photo, BitmapSource Logo, Color Ground, bool Dark, double Focus) _composedFor;

        /// <summary>The theme as it was saved and listed, or null when the dialog was canceled.</summary>
        internal ThemeDefinition Saved { get; private set; }

        /// <summary>Opens the builder over a window. A theme file to edit,
        /// or null for a new theme that starts from the look in force.</summary>
        internal static ThemeDefinition Show(Window owner, ThemeDefinition existing, SettingsViewModel settings)
        {
            var dialog = new ThemeBuilderDialog(existing, settings) { Owner = owner };
            dialog.ShowDialog();
            return dialog.Saved;
        }

        internal ThemeBuilderDialog(ThemeDefinition existing, SettingsViewModel settings)
        {
            InitializeComponent();
            _existing = existing;
            _settings = settings;
            _steam = new SteamGameArt(() => _settings != null && _settings.EnableCommunityConfigLookup);
            var s = Strings.Instance;
            _shades = new[]
            {
                new Shade { Label = s.ThemeBuilder_Background, Tip = s.ThemeBuilder_BackgroundTip },
                new Shade { Label = s.ThemeBuilder_Accent, Tip = s.ThemeBuilder_AccentTip },
                new Shade { Label = s.ThemeBuilder_Secondary, Tip = s.ThemeBuilder_SecondaryTip },
                new Shade { Label = s.ThemeBuilder_Attention, Tip = s.ThemeBuilder_AttentionTip },
            };
            GameList.DisplayMemberPath = nameof(GameHit.Name);

            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _previewTimer.Tick += (_, _) =>
            {
                _previewTimer.Stop();
                ShowPreview();
            };
            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _searchTimer.Tick += (_, _) =>
            {
                _searchTimer.Stop();
                _ = RunSearchAsync();
            };

            try
            {
                Start();
                BuildColorRows();
            }
            finally
            {
                _loading = false;
            }

            foreach (var property in new[] { ColorPickerControl.RedProperty, ColorPickerControl.GreenProperty, ColorPickerControl.BlueProperty })
                DependencyPropertyDescriptor.FromProperty(property, typeof(ColorPickerControl)).AddValueChanged(Picker, OnPickerChanged);

            Closed += (_, _) =>
            {
                _previewTimer.Stop();
                _searchTimer.Stop();
                _searchCancel?.Cancel();
                _fetchCancel?.Cancel();
                // The look that was on before the dialog, or the saved theme once it is picked.
                ThemeManager.SetPreview(null);
            };
            Loaded += (_, _) =>
            {
                NameBox.Focus();
                NameBox.SelectAll();
                ShowPicture();
                ShowPreview();
            };
        }

        /// <summary>The values the dialog opens with: the edited theme's, or the look in force.</summary>
        private void Start()
        {
            var s = Strings.Instance;
            var from = _existing ?? ThemeManager.Current;
            bool dark = from?.Dark ?? Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() != Wpf.Ui.Appearance.ApplicationTheme.Light;
            _shades[0].Value = from?.Background ?? ThemeEngine.StockGround(dark);
            _shades[1].Value = from?.Accent ?? ThemeEngine.StockAccent;
            _shades[2].Value = from?.Secondary ?? ThemeEngine.StockSecondary(dark);
            _shades[3].Value = from?.Attention ?? ThemeEngine.StockAttention(dark);
            BaseBox.SelectedIndex = dark ? 0 : 1;
            if (_existing != null)
            {
                Title = s.Settings_ThemeEdit;
                NameBox.Text = _existing.Name;
                _keptPicture = _existing.PictureBytes;
                _keptPictureName = _existing.PictureName;
                _keptTile = _existing.TileAt;
                _credits = _existing.Credits;
                _colorsTouched = true;
            }
            else
            {
                NameBox.Text = s.ThemeBuilder_DefaultName;
            }
        }

        private bool Dark => BaseBox.SelectedIndex != 1;

        // ─────────────────────────────────────────────
        //  The four colors
        // ─────────────────────────────────────────────

        private void BuildColorRows()
        {
            var s = Strings.Instance;
            foreach (var shade in _shades)
            {
                var row = new SettingRow { MinHeight = 44 };
                row.Children.Add(new TextBlock { Style = (Style)FindResource("DialogLabel"), Text = shade.Label, ToolTip = shade.Tip });
                shade.Chip = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5) };
                shade.Chip.SetResourceReference(Border.BorderBrushProperty, "SteelLineBrush");
                shade.Chip.BorderThickness = new Thickness(1);
                shade.Swatch = new ToggleButton
                {
                    Content = shade.Chip, Padding = new Thickness(4), MinWidth = 0, MinHeight = 0, Margin = new Thickness(0, 0, 8, 0),
                    ToolTip = s.ThemeBuilder_PickColor,
                };
                AutomationProperties.SetName(shade.Swatch, shade.Label + ", " + s.ThemeBuilder_PickColor);
                shade.Hex = new Wpf.Ui.Controls.TextBox { Style = (Style)FindResource("DialogTextBox"), Width = 116, MaxLength = 7 };
                AutomationProperties.SetName(shade.Hex, shade.Label);
                var local = shade;
                shade.Swatch.Checked += (_, _) => StartPicking(local);
                shade.Swatch.Unchecked += (_, _) => { if (_picking == local) StopPicking(); };
                shade.Hex.TextChanged += (_, _) => OnHexChanged(local);
                shade.Hex.LostFocus += (_, _) => WriteHex(local);
                var end = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                end.Children.Add(shade.Swatch);
                end.Children.Add(shade.Hex);
                row.Children.Add(end);
                ColorRows.Children.Add(row);
                ShowShade(shade);
            }
        }

        private void ShowShade(Shade shade)
        {
            var fill = new SolidColorBrush(shade.Value);
            fill.Freeze();
            shade.Chip.Background = fill;
            WriteHex(shade);
        }

        private void WriteHex(Shade shade)
        {
            string text = ThemeColor.Format(shade.Value);
            if (shade.Hex.Text == text) return;
            bool was = _loading;
            _loading = true;
            try { shade.Hex.Text = text; }
            finally { _loading = was; }
        }

        private void OnHexChanged(Shade shade)
        {
            if (_loading || !ThemeColor.TryParse(shade.Hex.Text, out Color typed) || typed == shade.Value) return;
            SetShade(shade, typed, fromHex: true);
        }

        /// <summary>One color changed by the user: its chip, its box, the
        /// picker when it is this color's, and the look.</summary>
        private void SetShade(Shade shade, Color value, bool fromHex = false, bool fromPicker = false)
        {
            shade.Value = value;
            _colorsTouched = true;
            var fill = new SolidColorBrush(value);
            fill.Freeze();
            shade.Chip.Background = fill;
            if (!fromHex) WriteHex(shade);
            if (!fromPicker && _picking == shade) LoadPicker(value);
            if (shade == _shades[0]) ShowPicture();
            QueuePreview();
        }

        private void StartPicking(Shade shade)
        {
            foreach (var other in _shades)
                if (other != shade && other.Swatch.IsChecked == true) other.Swatch.IsChecked = false;
            _picking = shade;
            LoadPicker(shade.Value);
            Picker.Visibility = Visibility.Visible;
        }

        private void StopPicking()
        {
            _picking = null;
            Picker.Visibility = Visibility.Collapsed;
        }

        private void LoadPicker(Color value)
        {
            bool was = _loading;
            _loading = true;
            try
            {
                Picker.Red = value.R;
                Picker.Green = value.G;
                Picker.Blue = value.B;
            }
            finally { _loading = was; }
        }

        private void OnPickerChanged(object sender, EventArgs e)
        {
            if (_loading || _picking == null) return;
            var picked = Color.FromRgb(Picker.Red, Picker.Green, Picker.Blue);
            if (picked != _picking.Value) SetShade(_picking, picked, fromPicker: true);
        }

        private void Match_Click(object sender, RoutedEventArgs e) => Match();

        /// <summary>Takes the four colors from the picture.</summary>
        private void Match()
        {
            BitmapSource source = _photo ?? (_keptPicture != null ? ThemePicture.DecodeSource(_keptPicture) : null);
            if (source == null) return;
            var suggested = ThemePalette.Suggest(source, Dark);
            SetShade(_shades[0], suggested.Background);
            SetShade(_shades[1], suggested.Accent);
            SetShade(_shades[2], suggested.Secondary);
            SetShade(_shades[3], suggested.Attention);
        }

        private void BaseBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            // A theme that still has the look's own colors takes the other ground's.
            if (!_colorsTouched)
            {
                bool dark = Dark;
                _shades[0].Value = ThemeEngine.StockGround(dark);
                _shades[2].Value = ThemeEngine.StockSecondary(dark);
                _shades[3].Value = ThemeEngine.StockAttention(dark);
                foreach (var shade in _shades) ShowShade(shade);
            }
            ShowPicture();
            QueuePreview();
        }

        private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SaveButton != null) SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);
        }

        // ─────────────────────────────────────────────
        //  The picture
        // ─────────────────────────────────────────────

        private void FromFile_Click(object sender, RoutedEventArgs e)
        {
            var s = Strings.Instance;
            var dialog = new OpenFileDialog { Filter = s.ThemeBuilder_PictureFilter, CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            BitmapSource photo = null;
            try
            {
                var info = new FileInfo(dialog.FileName);
                if (info.Length <= ThemeFile.MaxPictureBytes) photo = ThemePicture.DecodeSource(File.ReadAllBytes(dialog.FileName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                photo = null;
            }
            if (photo == null)
            {
                ConfirmDialog.Notice(this, Title, s.ThemeBuilder_PictureFailed);
                return;
            }
            GamePanel.Visibility = Visibility.Collapsed;
            TakePicture(photo, null, null);
        }

        private void NoPicture_Click(object sender, RoutedEventArgs e)
        {
            GamePanel.Visibility = Visibility.Collapsed;
            _photo = null;
            _logo = null;
            _keptPicture = null;
            _keptPictureName = null;
            _credits = null;
            ShowPicture();
            QueuePreview();
        }

        /// <summary>A new source for the picture. The first one also gives
        /// the theme its colors, unless the user already set some.</summary>
        internal void TakePicture(BitmapSource photo, BitmapSource logo, string credits)
        {
            _photo = photo;
            _logo = logo;
            _credits = credits;
            _keptPicture = null;
            _keptPictureName = null;
            bool was = _loading;
            _loading = true;
            try
            {
                PositionSlider.Value = 50;
                LogoSwitch.IsChecked = true;
            }
            finally { _loading = was; }
            if (!_colorsTouched)
            {
                Match();
                _colorsTouched = false;
            }
            ShowPicture();
            QueuePreview();
        }

        /// <summary>The picture as the theme will draw it, or null for
        /// none. Made again only when what it is made from changed.</summary>
        private BitmapSource ComposedPicture()
        {
            if (_photo == null) return null;
            var wanted = (_photo, LogoSwitch.IsChecked == true ? _logo : null, _shades[0].Value, Dark, PositionSlider.Value / 100.0);
            if (_composed == null || !wanted.Equals(_composedFor))
            {
                _composed = ThemePicture.Compose(wanted.Item1, wanted.Item2, wanted.Item3, wanted.Item4, wanted.Item5);
                _composedFor = wanted;
            }
            return _composed;
        }

        /// <summary>The dialog's own view of the picture and the rows that go with it.</summary>
        private void ShowPicture()
        {
            BitmapSource picture = ComposedPicture() ?? (_keptPicture != null ? BoundedBitmap.FromBytes(_keptPicture, ThemePicture.Width, ThemePicture.Height, scaleUp: false) : null);
            PictureView.Source = picture;
            var ground = new SolidColorBrush(new ThemeEngine(Seeds()).Ground);
            ground.Freeze();
            PictureGround.Fill = ground;
            PictureFrame.Visibility = picture != null ? Visibility.Visible : Visibility.Collapsed;
            PositionRow.Visibility = _photo != null ? Visibility.Visible : Visibility.Collapsed;
            LogoRow.Visibility = _photo != null && _logo != null ? Visibility.Visible : Visibility.Collapsed;
            NoPictureButton.IsEnabled = picture != null;
            MatchButton.IsEnabled = picture != null;
        }

        private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            ShowPicture();
            QueuePreview();
        }

        private void LogoSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            ShowPicture();
            QueuePreview();
        }

        // ─────────────────────────────────────────────
        //  A Steam game's picture
        // ─────────────────────────────────────────────

        private void FromGame_Click(object sender, RoutedEventArgs e)
        {
            GamePanel.Visibility = Visibility.Visible;
            ShowLookupState();
        }

        private void ShowLookupState()
        {
            bool on = _steam.IsOn;
            LookupOffPanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            SearchPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on) GameSearchBox.Focus();
        }

        // The same setting Get Profiles' Community tab turns on, which MainWindow saves.
        private void EnableLookup_Click(object sender, RoutedEventArgs e)
        {
            if (_settings != null) _settings.EnableCommunityConfigLookup = true;
            ShowLookupState();
        }

        private void GameSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void Status(string text)
        {
            GameStatus.Text = text ?? string.Empty;
            GameStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task RunSearchAsync()
        {
            var s = Strings.Instance;
            _searchCancel?.Cancel();
            var cancel = _searchCancel = new CancellationTokenSource();
            string query = GameSearchBox.Text?.Trim();
            if (string.IsNullOrEmpty(query))
            {
                GameList.ItemsSource = null;
                GameList.Visibility = Visibility.Collapsed;
                Status(null);
                return;
            }
            Status(s.Workshop_SearchingSteam);
            try
            {
                var found = await _steam.SearchAsync(query, cancel.Token);
                if (cancel.IsCancellationRequested) return;
                GameList.ItemsSource = found.Select(g => new GameHit(g.AppId, g.Name)).ToList();
                GameList.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                Status(found.Count > 0 ? null : s.Workshop_NoGamesFound);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or System.Text.Json.JsonException
                                           or InvalidOperationException or OperationCanceledException
                                           or PadForge.SteamWorkshop.SteamWorkshopException)
            {
                if (!cancel.IsCancellationRequested) Status(s.ThemeBuilder_SearchFailed);
            }
        }

        private async void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GameList.SelectedItem is not GameHit game) return;
            var s = Strings.Instance;
            _fetchCancel?.Cancel();
            var cancel = _fetchCancel = new CancellationTokenSource();
            Status(s.ThemeBuilder_Fetching);
            try
            {
                var (hero, logo) = await _steam.FetchAsync(game.AppId, cancel.Token);
                if (cancel.IsCancellationRequested) return;
                BitmapSource photo = hero != null ? ThemePicture.DecodeSource(hero) : null;
                if (photo == null)
                {
                    Status(s.ThemeBuilder_NoArt);
                    return;
                }
                Status(null);
                // A name the user has not changed becomes the game's.
                if (_existing == null && (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == s.ThemeBuilder_DefaultName || _nameFromGame))
                {
                    NameBox.Text = game.Name.Length > 80 ? game.Name.Substring(0, 80) : game.Name;
                    _nameFromGame = true;
                }
                TakePicture(photo, logo != null ? BoundedBitmap.FromBytes(logo, 1280, 720, scaleUp: false) : null,
                    string.Format(s.ThemeBuilder_GameCredits_Format, game.Name));
            }
            catch (OperationCanceledException)
            {
            }
        }

        private bool _nameFromGame;

        // ─────────────────────────────────────────────
        //  The look, live, and the file
        // ─────────────────────────────────────────────

        private ThemeSeeds Seeds() => new ThemeSeeds
        {
            Dark = Dark, Background = _shades[0].Value, Accent = _shades[1].Value, Secondary = _shades[2].Value, Attention = _shades[3].Value,
        };

        private void QueuePreview()
        {
            if (_loading) return;
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        /// <summary>The theme as the dialog holds it. For the look, the
        /// picture is the one just drawn. For the file, it is encoded.</summary>
        private ThemeDefinition BuildTheme(bool forFile)
        {
            var theme = new ThemeDefinition
            {
                Kind = ThemeKind.File,
                Id = _existing?.Id,
                Name = NameBox.Text?.Trim(),
                Look = _existing?.Look,
                Author = _existing?.Author,
                Credits = _credits,
                Dark = Dark,
                Background = _shades[0].Value, Accent = _shades[1].Value, Secondary = _shades[2].Value, Attention = _shades[3].Value,
            };
            BitmapSource composed = ComposedPicture();
            if (composed != null)
            {
                // A picture this dialog made: the starters' own layout, logo then picture.
                theme.TileAt = 0.36;
                if (forFile)
                {
                    theme.PictureBytes = ThemePicture.EncodePng(composed);
                    theme.PictureName = "picture.png";
                }
                else theme.SetPicture(composed);
            }
            else if (_keptPicture != null)
            {
                theme.PictureBytes = _keptPicture;
                theme.PictureName = _keptPictureName;
                theme.TileAt = _keptTile;
            }
            return theme;
        }

        private void ShowPreview() => ThemeManager.SetPreview(BuildTheme(forFile: false));

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (TrySave()) DialogResult = true;
        }

        /// <summary>Writes the theme to its file and lists it: the file a
        /// theme was opened from, or one the user names for a new theme.
        /// False when the user backs out of naming it or the write fails,
        /// which a notice says.</summary>
        internal bool TrySave()
        {
            var s = Strings.Instance;
            if (string.IsNullOrWhiteSpace(NameBox.Text)) return false;
            string path = _existing?.Path;
            if (path == null)
            {
                var dialog = new SaveFileDialog
                {
                    Filter = s.Settings_ThemeFileFilter,
                    FileName = Controls.ThemeGallery.SafeFileName(NameBox.Text.Trim()) + ThemeFile.Extension,
                    DefaultExt = ThemeFile.Extension,
                    AddExtension = true,
                    OverwritePrompt = true,
                    InitialDirectory = AppDomain.CurrentDomain.BaseDirectory,
                };
                if (dialog.ShowDialog(this) != true) return false;
                path = dialog.FileName;
            }
            try
            {
                ThemeFile.Write(path, BuildTheme(forFile: true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                ConfirmDialog.Notice(this, Title, string.Format(s.Settings_ThemeSaveFailed_Format, ex.Message));
                return false;
            }
            Saved = ThemeManager.Register(path);
            return Saved != null;
        }
    }
}
