using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PadForge.Common;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Resources.Strings;
using PadForge.Services;

namespace PadForge.Views
{
    /// <summary>
    /// What one keyboard screen shows (issue #519), the Dreamcast Screen
    /// dialog's shape, opened from the screen's row on the Devices page.
    /// Works on a copy: nothing reaches the screen or the profile until Save.
    /// The preview is drawn by the same composition the screen service
    /// writes, at the screen's own pixels.
    /// </summary>
    public partial class KeyboardScreenDialog : Wpf.Ui.Controls.FluentWindow
    {
        private readonly KeyboardScreenService _service;
        private readonly KeyboardScreenInfo _screen;
        private readonly KeyboardScreenKind _kind;
        private readonly bool _overlay;
        private readonly string _profileName;
        private KeyboardScreenMode _mode;
        private string _picture;
        private int _panX, _panY;
        private string _profilePicture;
        private bool _saved;

        private KeyboardScreenDialog(KeyboardScreenService service, KeyboardScreenInfo screen, KeyboardScreenData current,
            string profileName, string profilePicture, string detail, Size? overlaySize)
        {
            InitializeComponent();
            MouseLeftButtonDown += (_, __) => { try { DragMove(); } catch { } };
            _service = service;
            _screen = screen;
            var kind = _kind = screen.Kind;
            _profileName = profileName ?? string.Empty;
            _mode = current?.Mode ?? KeyboardScreenData.DefaultMode;
            _picture = current?.Picture;
            _panX = current?.PanX ?? 0;
            _panY = current?.PanY ?? 0;
            _profilePicture = profilePicture;

            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            Title = screen.Name;
            TitleText.Text = Title;
            DetailText.Text = detail ?? string.Empty;
            DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            string choose = string.Format(culture, s.DreamcastScreen_ChooseProfilePicture_Format, _profileName);
            ChooseProfilePictureButton.Content = new TextBlock { Text = choose, TextTrimming = TextTrimming.CharacterEllipsis };
            ChooseProfilePictureButton.ToolTip = choose;

            _overlay = kind == KeyboardScreenKind.Overlay;
            if (_overlay)
            {
                // The overlay has no size of its own, so the preview takes the
                // shape of its window, in a box the dialog holds.
                var window = overlaySize ?? new Size(320, 240);
                double fit = Math.Min(480.0 / window.Width, 270.0 / window.Height);
                OverlayPreview.Width = window.Width * fit;
                OverlayPreview.Height = window.Height * fit;
                OverlayPreview.Visibility = Visibility.Visible;
                PreviewImage.Visibility = Visibility.Collapsed;
            }
            else
            {
                // The screen at a size the dialog holds: the monochrome screen
                // doubled, the larger screens shrunk to the dialog's width.
                var (width, height) = KeyboardScreenFormats.SizeOf(kind);
                double scale = KeyboardScreenFormats.IsMonochrome(kind) ? 2.0 : Math.Min(1.0, 480.0 / width);
                PreviewImage.Width = width * scale;
                PreviewImage.Height = height * scale;
                RenderOptions.SetBitmapScalingMode(PreviewImage, KeyboardScreenFormats.IsMonochrome(kind)
                    ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
                if (KeyboardScreenFormats.IsMonochrome(kind))
                    PreviewFrame.Background = new SolidColorBrush(Color.FromRgb(0xBF, 0xD0, 0xB4));
            }

            foreach (KeyboardScreenMode mode in Enum.GetValues(typeof(KeyboardScreenMode)))
                ModeBox.Items.Add(new ComboBoxItem { Content = KeyboardScreenService.ModeName(mode), Tag = mode });
            ModeBox.SelectedIndex = (int)_mode;
            Refresh();
        }

        /// <summary>The choices the user saved, or null when canceled.</summary>
        public sealed record Result(KeyboardScreenMode Mode, string Picture, int PanX, int PanY, string ProfilePicture);

        internal static Result Show(Window owner, KeyboardScreenService service, KeyboardScreenInfo screen,
            KeyboardScreenData current, string profileName, string profilePicture, string detail, Size? overlaySize = null)
        {
            var dialog = new KeyboardScreenDialog(service, screen, current, profileName, profilePicture, detail, overlaySize);
            if (owner != null) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowDialog();
            return dialog._saved ? new Result(dialog._mode, dialog._picture, dialog._panX, dialog._panY, dialog._profilePicture) : null;
        }

        private static string ModeHint(KeyboardScreenMode mode)
        {
            var s = Strings.Instance;
            return mode switch
            {
                KeyboardScreenMode.ProfileName => s.KeyboardScreen_Hint_ProfileName,
                KeyboardScreenMode.ProfileNumber => s.KeyboardScreen_Hint_ProfileNumber,
                KeyboardScreenMode.ProfilePicture => s.KeyboardScreen_Hint_ProfilePicture,
                KeyboardScreenMode.Clock => s.KeyboardScreen_Hint_Clock,
                KeyboardScreenMode.PlayTime => s.KeyboardScreen_Hint_PlayTime,
                KeyboardScreenMode.Picture => s.KeyboardScreen_Hint_Picture,
                KeyboardScreenMode.Stopwatch => s.KeyboardScreen_Hint_Stopwatch,
                KeyboardScreenMode.Lightbar => s.KeyboardScreen_Hint_Lightbar,
                _ => s.KeyboardScreen_Hint_Off,
            };
        }

        private static string OverlayHint(KeyboardScreenMode mode)
        {
            var s = Strings.Instance;
            return mode switch
            {
                KeyboardScreenMode.ProfileName => s.KeyboardScreen_Hint_ProfileName_Overlay,
                KeyboardScreenMode.ProfileNumber => s.KeyboardScreen_Hint_ProfileNumber_Overlay,
                KeyboardScreenMode.ProfilePicture => s.KeyboardScreen_Hint_ProfilePicture_Overlay,
                KeyboardScreenMode.Clock => s.KeyboardScreen_Hint_Clock_Overlay,
                KeyboardScreenMode.PlayTime => s.KeyboardScreen_Hint_PlayTime_Overlay,
                KeyboardScreenMode.Picture => s.KeyboardScreen_Hint_Picture_Overlay,
                KeyboardScreenMode.Stopwatch => s.KeyboardScreen_Hint_Stopwatch_Overlay,
                KeyboardScreenMode.Lightbar => s.KeyboardScreen_Hint_Lightbar_Overlay,
                _ => s.KeyboardScreen_Hint_Off_Overlay,
            };
        }

        private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ModeBox.SelectedItem is ComboBoxItem { Tag: KeyboardScreenMode mode })
            {
                _mode = mode;
                Refresh();
            }
        }

        private void Refresh()
        {
            HintText.Text = _overlay ? OverlayHint(_mode) : ModeHint(_mode);
            bool picture = _mode == KeyboardScreenMode.Picture;
            ChoosePictureButton.Visibility = picture ? Visibility.Visible : Visibility.Collapsed;
            // All of the overlay's picture shows, so it has no window to pan.
            PanRow.Visibility = !_overlay && picture && !string.IsNullOrEmpty(_picture) ? Visibility.Visible : Visibility.Collapsed;
            bool profile = _mode == KeyboardScreenMode.ProfilePicture;
            ChooseProfilePictureButton.Visibility = profile ? Visibility.Visible : Visibility.Collapsed;
            RemoveProfilePictureButton.Visibility = profile && !string.IsNullOrEmpty(_profilePicture)
                ? Visibility.Visible : Visibility.Collapsed;

            // The pan stays inside the picture, as the macro holds it.
            var loaded = picture ? KeyboardScreenRenderer.LoadPicture(_picture) : null;
            if (loaded != null)
            {
                var (maxX, maxY) = KeyboardScreenRenderer.PanLimits(loaded, _kind);
                _panX = Math.Clamp(_panX, 0, maxX);
                _panY = Math.Clamp(_panY, 0, maxY);
            }
            PanXBox.Text = _panX.ToString(CultureInfo.CurrentCulture);
            PanYBox.Text = _panY.ToString(CultureInfo.CurrentCulture);

            var data = new KeyboardScreenData
            {
                Device = _screen.RowGuid.ToString("D"),
                Mode = _mode,
                Picture = _picture,
                PanX = _panX,
                PanY = _panY,
            };
            if (_overlay) OverlayPreview.Show(_service.OverlayPreview(data, _profilePicture, _profileName));
            else PreviewImage.Source = KeyboardScreenRenderer.ToBitmap(_service.Preview(_screen, data, _profilePicture, _profileName), _kind);
        }

        /// <summary>A picture file as a stored path, or null after telling
        /// the user why not.</summary>
        private string PickPicture()
        {
            var dialog = new OpenFileDialog { Filter = Strings.Instance.KeyboardScreen_PictureFilter };
            if (dialog.ShowDialog(this) != true) return null;
            string stored = IconPackageManager.MakeStoredPath(dialog.FileName);
            if (KeyboardScreenRenderer.LoadPicture(stored) != null) return stored;
            MessageBox.Show(this, Strings.Instance.DreamcastScreen_ImportFailed, Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        private void ChoosePicture_Click(object sender, RoutedEventArgs e)
        {
            string picture = PickPicture();
            if (picture == null) return;
            _picture = picture;
            _panX = _panY = 0;
            Refresh();
        }

        private void ChooseProfilePicture_Click(object sender, RoutedEventArgs e)
        {
            string picture = PickPicture();
            if (picture == null) return;
            _profilePicture = picture;
            Refresh();
        }

        private void RemoveProfilePicture_Click(object sender, RoutedEventArgs e)
        {
            _profilePicture = null;
            Refresh();
        }

        private void PanBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(PanXBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int x)) _panX = Math.Max(0, x);
            if (int.TryParse(PanYBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int y)) _panY = Math.Max(0, y);
            Refresh();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            PanBox_LostFocus(sender, e);
            _saved = true;
            DialogResult = true;
        }
    }
}
