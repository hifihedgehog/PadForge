using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    public partial class KeyboardScreenDialog : DialogWindow
    {
        /// <summary>The most the preview's picture takes: the board's 300 by
        /// 190 box, less 6 of the box showing and 1 of edge at each side.</summary>
        private const double PreviewWidth = 286, PreviewHeight = 176;

        /// <summary>A screen of <paramref name="width"/> by
        /// <paramref name="height"/> pixels at the size the preview draws it:
        /// as large as fits the box, its proportions kept.</summary>
        internal static Size PreviewSize(double width, double height)
        {
            if (!(width > 0) || !(height > 0)) return new Size(0, 0);
            double fit = Math.Min(PreviewWidth / width, PreviewHeight / height);
            return new Size(width * fit, height * fit);
        }

        /// <summary>One choice of the dialog's pickers: its value and its
        /// words, which are also its name for a screen reader.</summary>
        internal sealed record Choice<T>(T Value, string Name)
        {
            public override string ToString() => Name;
        }

        private readonly KeyboardScreenService _service;
        private readonly KeyboardScreenInfo _screen;
        private readonly KeyboardScreenKind _kind;
        private readonly bool _overlay;
        private readonly Size _overlaySize;
        private int _zoom;
        private int _lightbarSlot;
        private readonly string _profileName;
        private KeyboardScreenMode _mode;
        private string _picture;
        private int _panX, _panY;
        private string _profilePicture;
        private bool _saved;

        private KeyboardScreenDialog(KeyboardScreenService service, KeyboardScreenInfo screen, KeyboardScreenData current,
            string profileName, string profilePicture, Size? overlaySize)
        {
            InitializeComponent();
            _service = service;
            _screen = screen;
            var kind = _kind = screen.Kind;
            _profileName = profileName ?? string.Empty;
            _mode = current?.Mode ?? KeyboardScreenData.DefaultMode;
            _picture = current?.Picture;
            _panX = current?.PanX ?? 0;
            _panY = current?.PanY ?? 0;
            _zoom = current?.Zoom ?? KeyboardScreenData.DefaultZoom;
            _lightbarSlot = current?.LightbarSlot ?? KeyboardScreenData.LowestSlot;
            _profilePicture = profilePicture;

            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            Title = KeyboardScreenService.ScreenName(screen);
            string choose = string.Format(culture, s.DreamcastScreen_ChooseProfilePicture_Format, _profileName);
            ChooseProfilePictureButton.Content = new TextBlock { Text = choose, TextTrimming = TextTrimming.CharacterEllipsis };
            ChooseProfilePictureButton.ToolTip = choose;

            _overlay = kind == KeyboardScreenKind.Overlay;
            if (_overlay)
            {
                // The overlay has no size of its own, so the preview takes the
                // shape of its window, as large as the box holds.
                var window = _overlaySize = overlaySize ?? new Size(320, 240);
                var size = PreviewSize(window.Width, window.Height);
                OverlayPreview.Width = size.Width;
                OverlayPreview.Height = size.Height;
                OverlayPreview.Visibility = Visibility.Visible;
                PreviewImage.Visibility = Visibility.Collapsed;
                // The index is the slot, one past the lowest-slot line. The
                // picker takes a list, which its width is measured from.
                var slots = new List<Choice<int>> { new(KeyboardScreenData.LowestSlot, s.KeyboardScreen_LightbarSlot_Lowest) };
                for (int slot = 0; slot < PadForge.Common.Input.InputManager.MaxPads; slot++)
                    slots.Add(new Choice<int>(slot, (slot + 1).ToString(culture)));
                LightbarSlotBox.ItemsSource = slots;
                LightbarSlotBox.SelectedIndex = _lightbarSlot + 1;
            }
            else
            {
                // The screen at its own shape, as large as the box holds. A
                // monochrome screen's pixels stay square-edged, on the pale
                // field the picture itself carries.
                var (width, height) = KeyboardScreenFormats.SizeOf(kind);
                var size = PreviewSize(width, height);
                PreviewImage.Width = size.Width;
                PreviewImage.Height = size.Height;
                RenderOptions.SetBitmapScalingMode(PreviewImage, KeyboardScreenFormats.IsMonochrome(kind)
                    ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            }

            // The modes in the enum's own order, so a mode's index is its value.
            ModeBox.ItemsSource = Enum.GetValues<KeyboardScreenMode>()
                .Select(mode => new Choice<KeyboardScreenMode>(mode, KeyboardScreenService.ModeName(mode)))
                .ToList();
            ModeBox.SelectedIndex = (int)_mode;
            Refresh();
        }

        /// <summary>The choices the user saved, or null when canceled.</summary>
        public sealed record Result(KeyboardScreenMode Mode, string Picture, int PanX, int PanY, string ProfilePicture, int Zoom, int LightbarSlot);

        internal static Result Show(Window owner, KeyboardScreenService service, KeyboardScreenInfo screen,
            KeyboardScreenData current, string profileName, string profilePicture, Size? overlaySize = null)
        {
            var dialog = new KeyboardScreenDialog(service, screen, current, profileName, profilePicture, overlaySize);
            if (owner != null) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowDialog();
            return dialog._saved ? new Result(dialog._mode, dialog._picture, dialog._panX, dialog._panY, dialog._profilePicture, dialog._zoom, dialog._lightbarSlot) : null;
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
            if (ModeBox.SelectedItem is Choice<KeyboardScreenMode> choice)
            {
                _mode = choice.Value;
                Refresh();
            }
        }

        private void Refresh()
        {
            HintText.Text = _overlay ? OverlayHint(_mode) : ModeHint(_mode);
            bool picture = _mode == KeyboardScreenMode.Picture;
            PictureRow.Visibility = picture ? Visibility.Visible : Visibility.Collapsed;
            // At 100 percent all of the overlay's picture shows, so it has no window
            // to pan until the zoom gives it one.
            bool hasPicture = picture && !string.IsNullOrEmpty(_picture);
            ZoomRow.Visibility = _overlay && hasPicture ? Visibility.Visible : Visibility.Collapsed;
            PanRow.Visibility = hasPicture && (!_overlay || _zoom > KeyboardScreenData.DefaultZoom) ? Visibility.Visible : Visibility.Collapsed;
            ZoomBox.Text = _zoom.ToString(CultureInfo.CurrentCulture);
            LightbarSlotRow.Visibility = _overlay && _mode == KeyboardScreenMode.Lightbar ? Visibility.Visible : Visibility.Collapsed;
            bool profile = _mode == KeyboardScreenMode.ProfilePicture;
            ProfilePicturePanel.Visibility = profile ? Visibility.Visible : Visibility.Collapsed;
            RemoveProfilePictureButton.Visibility = profile && !string.IsNullOrEmpty(_profilePicture)
                ? Visibility.Visible : Visibility.Collapsed;

            // The pan stays inside the picture, as the macro holds it.
            var loaded = picture ? KeyboardScreenRenderer.LoadPicture(_picture) : null;
            if (loaded != null)
            {
                var (maxX, maxY) = _overlay
                    ? ScreenOverlayContentView.PanLimits(loaded.PixelWidth, loaded.PixelHeight, _zoom, _overlaySize)
                    : KeyboardScreenRenderer.PanLimits(loaded, _kind);
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
                Zoom = _zoom,
                LightbarSlot = _lightbarSlot,
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

        private void PanBox_LostFocus(object sender, RoutedEventArgs e) => ReadBoxes();

        /// <summary>Takes the zoom and the pan from their boxes, then redraws once.
        /// Refresh writes every box back, so a box read after it would give the old
        /// value: Enter on Save leaves the box being typed in focused, and no
        /// LostFocus has run for it.</summary>
        private void ReadBoxes()
        {
            if (int.TryParse(ZoomBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int zoom))
                _zoom = Math.Clamp(zoom, KeyboardScreenData.DefaultZoom, KeyboardScreenData.MaxZoom);
            if (int.TryParse(PanXBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int x)) _panX = Math.Max(0, x);
            if (int.TryParse(PanYBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int y)) _panY = Math.Max(0, y);
            Refresh();
        }

        private void LightbarSlotBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LightbarSlotBox.SelectedIndex < 0) return;
            _lightbarSlot = LightbarSlotBox.SelectedIndex - 1;
            Refresh();
        }

        private void ZoomBox_LostFocus(object sender, RoutedEventArgs e) => ReadBoxes();

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            ReadBoxes();
            _saved = true;
            DialogResult = true;
        }
    }
}
