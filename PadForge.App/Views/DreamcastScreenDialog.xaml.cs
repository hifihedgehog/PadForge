using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.Services;

namespace PadForge.Views
{
    /// <summary>
    /// What a Dreamcast pad's VMU shows in a Bliss-Box port (issue #469).
    /// Works on a copy: nothing reaches the port or the profile until Save.
    /// The preview is drawn from the same composition the screen service
    /// writes, so it shows what the chosen mode puts on the VMU.
    /// </summary>
    public partial class DreamcastScreenDialog : Wpf.Ui.Controls.FluentWindow
    {
        private readonly DreamcastScreenService _service;
        private readonly BlissBoxPort _port;
        private readonly string _profileName;
        private DreamcastScreenMode _mode;
        private string _picture;
        private string _adapterPicture;
        private string _profilePicture;
        private bool _saved;

        private DreamcastScreenDialog(DreamcastScreenService service, BlissBoxPort port, BlissBoxPortData current,
            string profileName, string profilePicture, string detail)
        {
            InitializeComponent();
            MouseLeftButtonDown += (_, __) => { try { DragMove(); } catch { } };
            _service = service;
            _port = port;
            _profileName = profileName ?? string.Empty;
            _mode = current?.ScreenMode ?? DreamcastScreenMode.Adapter;
            _picture = current?.Picture;
            _adapterPicture = current?.AdapterPicture;
            _profilePicture = profilePicture;

            var s = Strings.Instance;
            Title = s.DreamcastScreen_Title;
            DetailText.Text = detail ?? string.Empty;
            // A long profile name is cut short in the button and read whole
            // in its tooltip.
            string choose = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                s.DreamcastScreen_ChooseProfilePicture_Format, _profileName);
            ChooseProfilePictureButton.Content = new TextBlock { Text = choose, TextTrimming = TextTrimming.CharacterEllipsis };
            ChooseProfilePictureButton.ToolTip = choose;

            foreach (DreamcastScreenMode mode in Enum.GetValues(typeof(DreamcastScreenMode)))
                ModeBox.Items.Add(new ComboBoxItem { Content = ModeName(mode), Tag = mode });
            ModeBox.SelectedIndex = (int)_mode;
            Refresh();
        }

        /// <summary>The choices the user saved, or null when canceled.</summary>
        public sealed record Result(DreamcastScreenMode Mode, string Picture, string ProfilePicture);

        internal static Result Show(Window owner, DreamcastScreenService service, BlissBoxPort port,
            BlissBoxPortData current, string profileName, string profilePicture, string detail)
        {
            var dialog = new DreamcastScreenDialog(service, port, current, profileName, profilePicture, detail);
            if (owner != null) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowDialog();
            return dialog._saved ? new Result(dialog._mode, dialog._picture, dialog._profilePicture) : null;
        }

        internal static string ModeName(DreamcastScreenMode mode)
        {
            var s = Strings.Instance;
            return mode switch
            {
                DreamcastScreenMode.ProfileName => s.DreamcastScreen_Mode_ProfileName,
                DreamcastScreenMode.ProfileNumber => s.DreamcastScreen_Mode_ProfileNumber,
                DreamcastScreenMode.ProfilePicture => s.DreamcastScreen_Mode_ProfilePicture,
                DreamcastScreenMode.Clock => s.DreamcastScreen_Mode_Clock,
                DreamcastScreenMode.PlayTime => s.DreamcastScreen_Mode_PlayTime,
                DreamcastScreenMode.Picture => s.DreamcastScreen_Mode_Picture,
                _ => s.DreamcastScreen_Mode_Adapter,
            };
        }

        private static string ModeHint(DreamcastScreenMode mode)
        {
            var s = Strings.Instance;
            return mode switch
            {
                DreamcastScreenMode.ProfileName => s.DreamcastScreen_Hint_ProfileName,
                DreamcastScreenMode.ProfileNumber => s.DreamcastScreen_Hint_ProfileNumber,
                DreamcastScreenMode.ProfilePicture => s.DreamcastScreen_Hint_ProfilePicture,
                DreamcastScreenMode.Clock => s.DreamcastScreen_Hint_Clock,
                DreamcastScreenMode.PlayTime => s.DreamcastScreen_Hint_PlayTime,
                DreamcastScreenMode.Picture => s.DreamcastScreen_Hint_Picture,
                _ => s.DreamcastScreen_Hint_Adapter,
            };
        }

        private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ModeBox.SelectedItem is ComboBoxItem { Tag: DreamcastScreenMode mode })
            {
                _mode = mode;
                Refresh();
            }
        }

        private void Refresh()
        {
            HintText.Text = ModeHint(_mode);
            ChoosePictureButton.Visibility = _mode == DreamcastScreenMode.Picture ? Visibility.Visible : Visibility.Collapsed;
            bool profile = _mode == DreamcastScreenMode.ProfilePicture;
            ChooseProfilePictureButton.Visibility = profile ? Visibility.Visible : Visibility.Collapsed;
            RemoveProfilePictureButton.Visibility = profile && !string.IsNullOrEmpty(_profilePicture)
                ? Visibility.Visible : Visibility.Collapsed;

            byte[] image;
            if (_mode == DreamcastScreenMode.ProfilePicture)
                image = DreamcastScreenService.TryDecode(_profilePicture, out var picture)
                    ? picture : DreamcastScreenService.RenderText(_profileName);
            else
                image = _service.Preview(_port, new BlissBoxPortData
                {
                    ScreenMode = _mode,
                    Picture = _picture,
                    AdapterPicture = _adapterPicture,
                });
            PreviewImage.Source = DreamcastScreenService.ToBitmap(image);
        }

        /// <summary>A picture from a file, or null after telling the user why
        /// not.</summary>
        private byte[] PickPicture()
        {
            var dialog = new OpenFileDialog { Filter = Strings.Instance.DreamcastScreen_PictureFilter };
            if (dialog.ShowDialog(this) != true) return null;
            byte[] image = null;
            try { image = DreamcastScreenService.ImportPicture(dialog.FileName); }
            catch { image = null; }
            if (image == null)
                MessageBox.Show(this, Strings.Instance.DreamcastScreen_ImportFailed, Strings.Instance.DreamcastScreen_Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return image;
        }

        private void ChoosePicture_Click(object sender, RoutedEventArgs e)
        {
            var image = PickPicture();
            if (image == null) return;
            _picture = DreamcastScreenService.Encode(image);
            Refresh();
        }

        private void ChooseProfilePicture_Click(object sender, RoutedEventArgs e)
        {
            var image = PickPicture();
            if (image == null) return;
            _profilePicture = DreamcastScreenService.Encode(image);
            Refresh();
        }

        private void RemoveProfilePicture_Click(object sender, RoutedEventArgs e)
        {
            _profilePicture = null;
            Refresh();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            _saved = true;
            DialogResult = true;
        }
    }
}
