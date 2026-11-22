using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PadForge.Common.Themes;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Views.Controls
{
    /// <summary>One face of a gallery tile: the few colors a small
    /// PadForge is drawn in, and the window into the theme's picture.</summary>
    public sealed class ThemeTileFace
    {
        public Brush Ground { get; init; }
        public Brush Card { get; init; }
        public Brush Line { get; init; }
        public Brush Accent { get; init; }
        public Brush Secondary { get; init; }
        public Brush Attention { get; init; }
        public Brush TextLine { get; init; }
        public Brush Picture { get; init; }

        internal static ThemeTileFace Of(ThemeSwatch swatch, ThemeDefinition theme = null)
        {
            static Brush Solid(Color c)
            {
                var brush = new SolidColorBrush(c);
                brush.Freeze();
                return brush;
            }
            Brush picture = null;
            var thumbnail = theme?.Thumbnail;
            if (thumbnail != null)
            {
                // The window into the picture: a share of its width, all of its
                // height. A starter's window has the tile's own shape. One into
                // a picture of another shape fills the tile and is cut, never
                // stretched.
                var window = new ImageBrush(thumbnail)
                {
                    Viewbox = new Rect(theme.TileAt, 0, ThemeDefinition.TileShare, 1),
                    ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                    Stretch = Stretch.UniformToFill,
                };
                window.Freeze();
                picture = window;
            }
            Color card = swatch.Card, text = swatch.Text;
            return new ThemeTileFace
            {
                Ground = Solid(swatch.Ground),
                // Over a picture the card lets it show through, as Home's engine card does.
                Card = Solid(picture != null ? Color.FromArgb(0x99, card.R, card.G, card.B) : card),
                Line = Solid(swatch.Line),
                Accent = Solid(swatch.Accent),
                Secondary = Solid(swatch.Secondary),
                Attention = Solid(swatch.Attention),
                TextLine = Solid(Color.FromArgb(0x99, text.R, text.G, text.B)),
                Picture = picture,
            };
        }
    }

    /// <summary>One tile of the gallery.</summary>
    public sealed class ThemeTile
    {
        /// <summary>The theme's id, or empty for PadForge's own looks.</summary>
        public string Id { get; init; } = string.Empty;
        /// <summary>0 System, 1 Light, 2 Dark, or -1 for a theme.</summary>
        public int StockIndex { get; init; } = -1;
        public string Name { get; init; }
        public string Look { get; init; }
        public string FaultText { get; init; }
        public bool IsFile { get; init; }
        public ThemeTileFace Face { get; init; }
        public ThemeTileFace OtherFace { get; init; }
        public string SpokenName { get; init; }
        public string Tip { get; init; }

        // A list item is read by its ToString when it has no name of its own.
        public override string ToString() => SpokenName;
    }

    /// <summary>The theme gallery (#591). Its DataContext is the
    /// SettingsViewModel, whose SetTheme a picked tile calls.</summary>
    public partial class ThemeGallery : UserControl
    {
        private bool _building;

        public ThemeGallery()
        {
            InitializeComponent();
            // Loaded can come again with no Unloaded between.
            Loaded += (_, _) =>
            {
                Unhook();
                ThemeManager.ListChanged += OnListChanged;
                ThemeManager.Applied += OnLookApplied;
                Strings.CultureChanged += OnCultureChanged;
                if (DataContext is SettingsViewModel vm) vm.PropertyChanged += OnSettingChanged;
                Rebuild();
            };
            Unloaded += (_, _) => Unhook();
        }

        private void Unhook()
        {
            ThemeManager.ListChanged -= OnListChanged;
            ThemeManager.Applied -= OnLookApplied;
            Strings.CultureChanged -= OnCultureChanged;
            if (DataContext is SettingsViewModel vm) vm.PropertyChanged -= OnSettingChanged;
        }

        private SettingsViewModel Settings => DataContext as SettingsViewModel;

        private void OnListChanged(object sender, EventArgs e)
        {
            if (OnOwnThread()) Rebuild();
        }

        private void OnLookApplied(object sender, EventArgs e)
        {
            if (OnOwnThread()) ShowNote();
        }

        /// <summary>A look is put on and the list changed by the
        /// application's own thread. A gallery of another thread belongs
        /// to a window that closed without unloading it: it lets go of the
        /// theme manager, which is all it can reach from here.</summary>
        private bool OnOwnThread()
        {
            if (CheckAccess()) return true;
            ThemeManager.ListChanged -= OnListChanged;
            ThemeManager.Applied -= OnLookApplied;
            Strings.CultureChanged -= OnCultureChanged;
            return false;
        }
        private void OnCultureChanged() => Dispatcher.BeginInvoke(new Action(Rebuild));

        private void OnSettingChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SettingsViewModel.SelectedThemeIndex) or nameof(SettingsViewModel.SelectedThemeId))
                SelectPicked();
        }

        /// <summary>The tiles, in the gallery's order.</summary>
        internal static List<ThemeTile> BuildTiles()
        {
            var s = Strings.Instance;
            var light = ThemeTileFace.Of(ThemeEngine.StockSwatch(false));
            var dark = ThemeTileFace.Of(ThemeEngine.StockSwatch(true));
            var tiles = new List<ThemeTile>
            {
                new ThemeTile { StockIndex = 0, Name = s.Settings_ThemeSystem, SpokenName = s.Settings_ThemeSystem, Face = light, OtherFace = dark },
                new ThemeTile { StockIndex = 1, Name = s.Settings_ThemeLight, SpokenName = s.Settings_ThemeLight, Face = light },
                new ThemeTile { StockIndex = 2, Name = s.Settings_ThemeDark, SpokenName = s.Settings_ThemeDark, Face = dark },
            };
            foreach (var theme in ThemeManager.Starters.Concat(ThemeManager.Files))
            {
                string fault = theme.Fault switch
                {
                    ThemeFault.Missing => s.Settings_ThemeMissing,
                    ThemeFault.Unreadable => s.Settings_ThemeUnreadable,
                    _ => null,
                };
                string spoken = theme.Name;
                if (!string.IsNullOrEmpty(theme.Look)) spoken += ", " + theme.Look;
                if (fault != null) spoken += ", " + fault;
                tiles.Add(new ThemeTile
                {
                    Id = theme.Id,
                    Name = theme.Name,
                    Look = string.IsNullOrEmpty(theme.Look) ? null : theme.Look,
                    FaultText = fault,
                    IsFile = theme.Kind == ThemeKind.File,
                    Face = ThemeTileFace.Of(ThemeManager.SwatchOf(theme), theme.Fault == ThemeFault.None ? theme : null),
                    SpokenName = spoken,
                    Tip = TipOf(theme),
                });
            }
            return tiles;
        }

        /// <summary>A tile's tip: where a starter's picture came from, in the
        /// words About uses, which every language has. A theme file's own
        /// credits are its author's words, and a file without them shows
        /// where it is.</summary>
        private static string TipOf(ThemeDefinition theme)
        {
            var s = Strings.Instance;
            if (theme.Kind == ThemeKind.Starter)
                return theme.Id switch
                {
                    ThemeManager.StarterPrefix + "sonic-blue-blur" => s.About_ThemeArtSonicKronosDesc,
                    ThemeManager.StarterPrefix + "sonic-night-run" => s.About_ThemeArtSonicRunningDesc,
                    ThemeManager.StarterPrefix + "rings-the-one-ring" => s.About_ThemeArtHobbitonDesc,
                    ThemeManager.StarterPrefix + "rings-map-of-middle-earth" => s.About_ThemeArtMiddleEarthMapDesc,
                    ThemeManager.StarterPrefix + "potter-gryffindor" => s.About_ThemeArtHogwartsDesc,
                    ThemeManager.StarterPrefix + "potter-four-houses" => s.About_ThemeArtHouseCrestsDesc,
                    ThemeManager.StarterPrefix + "starwars-opening-crawl" => s.About_ThemeArtFalconDesc,
                    ThemeManager.StarterPrefix + "starwars-dark-side" => s.About_ThemeArtVaderHelmetDesc,
                    ThemeManager.StarterPrefix + "startrek-lcars" => s.About_ThemeArtEnterpriseModelDesc,
                    ThemeManager.StarterPrefix + "startrek-starfleet" => s.About_ThemeArtEnterpriseDDesc,
                    _ => theme.Credits,
                };
            if (!string.IsNullOrEmpty(theme.Credits)) return theme.Credits;
            return theme.Kind == ThemeKind.File ? theme.Path : null;
        }

        private void Rebuild()
        {
            _building = true;
            try
            {
                Tiles.ItemsSource = BuildTiles();
            }
            finally
            {
                _building = false;
            }
            SelectPicked();
            ShowNote();
        }

        private void SelectPicked()
        {
            var vm = Settings;
            if (vm == null || Tiles.ItemsSource is not List<ThemeTile> tiles) return;
            ThemeTile picked = string.IsNullOrEmpty(vm.SelectedThemeId)
                ? tiles.FirstOrDefault(t => t.StockIndex == vm.SelectedThemeIndex)
                : tiles.FirstOrDefault(t => string.Equals(t.Id, vm.SelectedThemeId, StringComparison.OrdinalIgnoreCase));
            // A picked file that was taken off the list in another place: PadForge's own look stands.
            picked ??= tiles.FirstOrDefault(t => t.StockIndex == vm.SelectedThemeIndex);
            _building = true;
            try
            {
                Tiles.SelectedItem = picked;
            }
            finally
            {
                _building = false;
            }
        }

        private void Tiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_building || Settings == null || Tiles.SelectedItem is not ThemeTile tile) return;
            if (tile.StockIndex >= 0) Settings.SetTheme(tile.StockIndex, null);
            else Settings.SetTheme(Settings.SelectedThemeIndex, tile.Id);
        }

        /// <summary>Says why the picked theme is not the one showing.</summary>
        private void ShowNote()
        {
            var s = Strings.Instance;
            string text = null;
            var picked = ThemeManager.Find(ThemeManager.SelectedId);
            if (ThemeManager.GameLook != null && ReferenceEquals(ThemeManager.Current, ThemeManager.GameLook))
                text = string.Format(s.Settings_ThemeProfileNote_Format, Settings?.ActiveProfileInfo ?? string.Empty);
            else if (picked != null && picked.Fault != ThemeFault.None)
                text = string.Format(s.Settings_ThemeFaultNote_Format, picked.Name);
            Note.Text = text ?? string.Empty;
            Note.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Tiles_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Tiles.SelectedItem is not ThemeTile tile || !tile.IsFile) return;
            if (e.Key == Key.Delete)
            {
                Remove(tile.Id);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Edit(tile.Id);
                e.Handled = true;
            }
        }

        private void EditTile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string id) Edit(id);
            e.Handled = true;
        }

        private void RemoveTile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string id) Remove(id);
            e.Handled = true;
        }

        /// <summary>Takes a theme file off the list. The file stays. When
        /// it was the picked one, PadForge's own look returns.</summary>
        private void Remove(string id)
        {
            bool wasPicked = Settings != null && string.Equals(Settings.SelectedThemeId, id, StringComparison.OrdinalIgnoreCase);
            if (wasPicked) Settings.SetTheme(Settings.SelectedThemeIndex, null);
            ThemeManager.Unregister(id);
        }

        private void Edit(string id)
        {
            var theme = ThemeManager.Find(id);
            if (theme == null || theme.Kind != ThemeKind.File || theme.Fault != ThemeFault.None) return;
            OpenBuilder(theme);
        }

        /// <summary>New Theme: the builder, starting from the look in force.</summary>
        internal void NewTheme() => OpenBuilder(null);

        private void OpenBuilder(ThemeDefinition existing)
        {
            var saved = ThemeBuilderDialog.Show(Window.GetWindow(this), existing, Settings);
            if (saved != null && Settings != null) Settings.SetTheme(Settings.SelectedThemeIndex, saved.Id);
        }

        /// <summary>Add Theme: lists a theme file where it is and picks it.</summary>
        internal void AddTheme()
        {
            var s = Strings.Instance;
            var dialog = new OpenFileDialog { Filter = s.Settings_ThemeFileFilter, CheckFileExists = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            var theme = ThemeManager.Register(dialog.FileName);
            if (theme == null)
            {
                ConfirmDialog.Notice(Window.GetWindow(this), s.Settings_Theme, s.Settings_ThemeAddFailed);
                return;
            }
            if (Settings != null) Settings.SetTheme(Settings.SelectedThemeIndex, theme.Id);
        }

        /// <summary>Save a Copy: writes the picked theme as a file to change
        /// or share. A starter is written as it is inside PadForge. Under
        /// PadForge's own look the copy holds that look's four colors.</summary>
        internal void SaveCopy()
        {
            var s = Strings.Instance;
            var picked = ThemeManager.Find(ThemeManager.SelectedId);
            string name = picked != null && picked.Fault == ThemeFault.None
                ? (string.IsNullOrEmpty(picked.Look) ? picked.Name : picked.Name + " " + picked.Look)
                : StockName();
            var dialog = new SaveFileDialog
            {
                Filter = s.Settings_ThemeFileFilter,
                FileName = SafeFileName(name) + ThemeFile.Extension,
                DefaultExt = ThemeFile.Extension,
                AddExtension = true,
                OverwritePrompt = true,
                InitialDirectory = AppDomain.CurrentDomain.BaseDirectory,
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                ThemeFile.WriteBytes(dialog.FileName, CopyBytes(picked, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                ConfirmDialog.Notice(Window.GetWindow(this), s.Settings_Theme, string.Format(s.Settings_ThemeSaveFailed_Format, ex.Message));
            }
        }

        /// <summary>What Save a Copy writes: a starter's file as it is inside
        /// PadForge, a theme file's theme, or, for PadForge's own look or a
        /// picked file that is gone, that look's four colors under the name.</summary>
        internal static byte[] CopyBytes(ThemeDefinition picked, string stockName)
        {
            if (picked == null || picked.Fault != ThemeFault.None) return ThemeFile.Serialize(StockCopy(stockName));
            if (picked.Kind == ThemeKind.Starter && picked.Id != null && picked.Id.StartsWith(ThemeManager.StarterPrefix, StringComparison.Ordinal))
            {
                byte[] own = ThemeManager.StarterBytes(picked.Id.Substring(ThemeManager.StarterPrefix.Length));
                if (own != null) return own;
            }
            return ThemeFile.Serialize(picked);
        }

        private string StockName()
        {
            var s = Strings.Instance;
            return (Settings?.SelectedThemeIndex ?? 0) switch
            {
                1 => s.Settings_ThemeLight,
                2 => s.Settings_ThemeDark,
                _ => s.Settings_ThemeSystem,
            };
        }

        /// <summary>PadForge's own look as a theme: its four colors on the ground in force.</summary>
        private static ThemeDefinition StockCopy(string name)
        {
            bool dark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() != Wpf.Ui.Appearance.ApplicationTheme.Light;
            return new ThemeDefinition
            {
                Kind = ThemeKind.File,
                Name = name,
                Dark = dark,
                Background = ThemeEngine.StockGround(dark),
                Accent = ThemeEngine.StockAccent,
                Secondary = ThemeEngine.StockSecondary(dark),
                Attention = ThemeEngine.StockAttention(dark),
            };
        }

        internal static string SafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Theme";
            var invalid = Path.GetInvalidFileNameChars();
            string safe = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? ' ' : c).ToArray()).Trim().TrimEnd('.');
            return safe.Length == 0 ? "Theme" : safe;
        }
    }
}
