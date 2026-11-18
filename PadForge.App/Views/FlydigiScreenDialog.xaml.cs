using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using PadForge.Common;
using PadForge.Common.Input.Flydigi;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Engine.Flydigi;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    /// <summary>What a Flydigi Apex screen shows (#586). The values are the
    /// dialog's own and saved nowhere.</summary>
    internal enum FlydigiScreenSource
    {
        Picture = 0,
        ProfilePicture = 1,
        ProfileName = 2,
        ProfileNumber = 3,
    }

    /// <summary>
    /// Sends a picture to a Flydigi Apex screen (#586). The preview is drawn
    /// from the frames that go out, decoded back, so it shows what the pad
    /// gets. Nothing reaches the pad until Send. The picture, its fit and
    /// the profile's picture are the active profile's, as the Dreamcast and
    /// keyboard screens keep them.
    /// </summary>
    public partial class FlydigiScreenDialog : DialogWindow
    {
        private readonly FlydigiApexSession _pad;
        private readonly FlydigiScreenSender.Apex4Binding _binding;
        private readonly string _profileName;
        private readonly int _profileNumber;
        private FlydigiScreenSource _source = FlydigiScreenSource.Picture;
        private FlydigiScreenFit _fit = FlydigiScreenFit.Fill;
        private string _picturePath;
        private string _profilePicture;
        private bool _profilePictureChanged;
        private FlydigiScreenPicture _picture;
        private int _cut;
        private readonly List<System.Windows.Media.Imaging.BitmapSource> _previewFrames = new();
        private int _previewIndex;
        private readonly DispatcherTimer _previewTimer;
        private CancellationTokenSource _sending;

        private FlydigiScreenDialog(FlydigiApexSession pad, FlydigiScreenSender.Apex4Binding binding,
            string profileName, int profileNumber, string profilePicture)
        {
            InitializeComponent();
            _pad = pad;
            _binding = binding;
            _profileName = profileName ?? string.Empty;
            _profileNumber = profileNumber;
            _profilePicture = profilePicture;

            var s = Strings.Instance;
            string choose = string.Format(CultureInfo.CurrentCulture, s.DreamcastScreen_ChooseProfilePicture_Format, _profileName);
            ChooseProfilePictureButton.Content = new TextBlock { Text = choose, TextTrimming = TextTrimming.CharacterEllipsis };
            ChooseProfilePictureButton.ToolTip = choose;

            foreach (FlydigiScreenSource source in Enum.GetValues(typeof(FlydigiScreenSource)))
                SourceBox.Items.Add(new ComboBoxItem { Content = SourceName(source), Tag = source });
            foreach (FlydigiScreenFit fit in Enum.GetValues(typeof(FlydigiScreenFit)))
                FitBox.Items.Add(new ComboBoxItem { Content = FitName(fit), Tag = fit });
            SourceBox.SelectedIndex = 0;
            FitBox.SelectedIndex = 0;

            _previewTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher);
            _previewTimer.Tick += (_, _) => NextPreviewFrame();
            Closed += (_, _) => _previewTimer.Stop();
            Closing += OnClosing;
            Rebuild();
        }

        /// <summary>The profile picture the dialog leaves, and whether it
        /// changed.</summary>
        internal sealed record Result(string ProfilePicture, bool ProfilePictureChanged, bool Sent);

        private bool _sent;

        internal static Result Show(Window owner, FlydigiApexSession pad, FlydigiScreenSender.Apex4Binding binding,
            string profileName, int profileNumber, string profilePicture)
        {
            var dialog = new FlydigiScreenDialog(pad, binding, profileName, profileNumber, profilePicture);
            if (owner != null) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowDialog();
            return new Result(dialog._profilePicture, dialog._profilePictureChanged, dialog._sent);
        }

        internal static string SourceName(FlydigiScreenSource source)
        {
            var s = Strings.Instance;
            return source switch
            {
                FlydigiScreenSource.ProfilePicture => s.DreamcastScreen_Mode_ProfilePicture,
                FlydigiScreenSource.ProfileName => s.DreamcastScreen_Mode_ProfileName,
                FlydigiScreenSource.ProfileNumber => s.DreamcastScreen_Mode_ProfileNumber,
                _ => s.DreamcastScreen_Mode_Picture,
            };
        }

        private static string SourceHint(FlydigiScreenSource source)
        {
            var s = Strings.Instance;
            return source switch
            {
                FlydigiScreenSource.ProfilePicture => s.FlydigiScreen_Hint_ProfilePicture,
                FlydigiScreenSource.ProfileName => s.FlydigiScreen_Hint_ProfileName,
                FlydigiScreenSource.ProfileNumber => s.FlydigiScreen_Hint_ProfileNumber,
                _ => s.FlydigiScreen_Hint_Picture,
            };
        }

        internal static string FitName(FlydigiScreenFit fit)
        {
            var s = Strings.Instance;
            return fit switch
            {
                FlydigiScreenFit.Fit => s.FlydigiScreen_Fit_Fit,
                FlydigiScreenFit.Stretch => s.FlydigiScreen_Fit_Stretch,
                _ => s.FlydigiScreen_Fit_Fill,
            };
        }

        private FlydigiApexModel Model => _pad?.Identity.Model ?? FlydigiApexModel.None;

        private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SourceBox.SelectedItem is ComboBoxItem { Tag: FlydigiScreenSource source })
            {
                _source = source;
                Rebuild();
            }
        }

        private void FitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FitBox.SelectedItem is ComboBoxItem { Tag: FlydigiScreenFit fit })
            {
                _fit = fit;
                Rebuild();
            }
        }

        /// <summary>The frames for what is chosen now, and everything that
        /// shows them.</summary>
        private void Rebuild()
        {
            var s = Strings.Instance;
            HintText.Text = SourceHint(_source);
            bool picture = _source == FlydigiScreenSource.Picture;
            bool profilePicture = _source == FlydigiScreenSource.ProfilePicture;
            ChoosePictureButton.Visibility = picture ? Visibility.Visible : Visibility.Collapsed;
            ProfilePicturePanel.Visibility = profilePicture ? Visibility.Visible : Visibility.Collapsed;
            RemoveProfilePictureButton.Visibility = profilePicture && !string.IsNullOrEmpty(_profilePicture)
                ? Visibility.Visible : Visibility.Collapsed;
            FitRow.Visibility = picture || (profilePicture && !string.IsNullOrEmpty(_profilePicture))
                ? Visibility.Visible : Visibility.Collapsed;

            int maximum = FlydigiScreenSender.MaximumFrames(Model);
            _cut = 0;
            _picture = _source switch
            {
                FlydigiScreenSource.Picture => string.IsNullOrEmpty(_picturePath) ? null
                    : FlydigiScreenPictures.FromFile(IconPackageManager.ResolvePath(_picturePath), _fit, maximum, out _cut),
                FlydigiScreenSource.ProfilePicture => KeyboardScreenRenderer.LoadPicture(_profilePicture) is { } bitmap
                    ? FlydigiScreenPictures.FromBitmap(bitmap, _fit)
                    : FlydigiScreenPictures.FromText(_profileName),
                FlydigiScreenSource.ProfileName => FlydigiScreenPictures.FromText(_profileName),
                _ => FlydigiScreenPictures.FromText(_profileNumber.ToString(CultureInfo.CurrentCulture)),
            };

            _previewTimer.Stop();
            _previewFrames.Clear();
            _previewIndex = 0;
            if (_picture != null)
            {
                foreach (var frame in _picture.Frames) _previewFrames.Add(FlydigiScreenPictures.Preview(frame));
                PreviewImage.Source = _previewFrames[0];
                if (_previewFrames.Count > 1)
                {
                    _previewTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _picture.IntervalMs));
                    _previewTimer.Start();
                }
            }
            else
            {
                PreviewImage.Source = null;
            }

            if (_picture == null)
            {
                SizeText.Text = string.Empty;
            }
            else
            {
                var time = TimeSpan.FromSeconds(Math.Ceiling(_picture.Frames.Count * FlydigiScreenSender.SecondsPerFrame(Model)));
                string size = string.Format(CultureInfo.CurrentCulture, s.FlydigiScreen_Size_Format, _picture.Frames.Count,
                    time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.CurrentCulture));
                if (_cut > 0)
                    size += " " + string.Format(CultureInfo.CurrentCulture, s.FlydigiScreen_Cut_Format, maximum, _cut);
                SizeText.Text = size;
            }
            NoteText.Text = Model == FlydigiApexModel.Apex4 ? s.FlydigiScreen_Note_Apex4 : s.FlydigiScreen_Note_Serial;
            UpdateButtons();
        }

        private void NextPreviewFrame()
        {
            if (_previewFrames.Count == 0) return;
            _previewIndex = (_previewIndex + 1) % _previewFrames.Count;
            PreviewImage.Source = _previewFrames[_previewIndex];
        }

        private bool Sending => _sending != null;

        private void UpdateButtons()
        {
            bool idle = !Sending;
            SendButton.IsEnabled = idle && _picture != null && _pad != null && !_pad.Failed;
            SourceBox.IsEnabled = FitBox.IsEnabled = ChoosePictureButton.IsEnabled = idle;
            ChooseProfilePictureButton.IsEnabled = RemoveProfilePictureButton.IsEnabled = idle;
            CloseButton.Content = idle ? Strings.Instance.Common_Close : Strings.Instance.Common_Cancel;
        }

        private string PickPicture()
        {
            var dialog = new OpenFileDialog { Filter = Strings.Instance.FlydigiScreen_PictureFilter };
            if (dialog.ShowDialog(this) != true) return null;
            if (FlydigiScreenPictures.FromFile(dialog.FileName, _fit, 1, out _) == null)
            {
                MessageBox.Show(this, Strings.Instance.DreamcastScreen_ImportFailed, Strings.Instance.FlydigiScreen_Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            return IconPackageManager.MakeStoredPath(dialog.FileName);
        }

        private void ChoosePicture_Click(object sender, RoutedEventArgs e)
        {
            string path = PickPicture();
            if (path == null) return;
            _picturePath = path;
            Rebuild();
        }

        private void ChooseProfilePicture_Click(object sender, RoutedEventArgs e)
        {
            string path = PickPicture();
            if (path == null) return;
            _profilePicture = path;
            _profilePictureChanged = true;
            Rebuild();
        }

        private void RemoveProfilePicture_Click(object sender, RoutedEventArgs e)
        {
            _profilePicture = null;
            _profilePictureChanged = true;
            Rebuild();
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (Sending || _picture == null || _pad == null) return;
            var s = Strings.Instance;
            if (Model == FlydigiApexModel.Apex4
                && MessageBox.Show(this, s.FlydigiScreen_Apex4Confirm, s.FlydigiScreen_Title,
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            _sending = new CancellationTokenSource();
            SendProgress.Visibility = Visibility.Visible;
            SendProgress.Value = 0;
            StatusText.Text = s.FlydigiScreen_Step_Starting;
            UpdateButtons();
            var progress = new Progress<FlydigiScreenProgress>(Report);
            var picture = _picture with
            {
                IntervalMs = FlydigiScreenSender.ClampInterval(Model, _picture.IntervalMs),
            };
            try
            {
                await FlydigiScreenSender.SendAsync(_pad, picture, _binding, progress, _sending.Token);
                _sent = true;
                SendProgress.Value = 1;
                StatusText.Text = Model == FlydigiApexModel.Apex4 ? s.FlydigiScreen_Done_Apex4 : s.FlydigiScreen_Done_Serial;
            }
            catch (FlydigiScreenException ex)
            {
                StatusText.Text = FailureText(ex.Failure);
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = FailureText(FlydigiScreenFailure.Canceled);
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("FLYDIGI screen send fault: " + ex.GetType().Name);
                StatusText.Text = FailureText(FlydigiScreenFailure.NoReply);
            }
            finally
            {
                _sending.Dispose();
                _sending = null;
                UpdateButtons();
            }
        }

        private void Report(FlydigiScreenProgress progress)
        {
            var s = Strings.Instance;
            int percent = progress.Total > 0 ? (int)(100L * progress.Done / progress.Total) : 0;
            if (progress.Total > 0) SendProgress.Value = (double)progress.Done / progress.Total;
            StatusText.Text = progress.Step switch
            {
                FlydigiScreenStep.Switching => s.FlydigiScreen_Step_Switching,
                FlydigiScreenStep.WaitingForPort => s.FlydigiScreen_Step_WaitingForPort,
                FlydigiScreenStep.Binding => s.FlydigiScreen_Step_Binding,
                FlydigiScreenStep.Erasing => string.Format(CultureInfo.CurrentCulture, s.FlydigiScreen_Step_Erasing_Format, percent),
                FlydigiScreenStep.Writing => string.Format(CultureInfo.CurrentCulture, s.FlydigiScreen_Step_Writing_Format, percent),
                FlydigiScreenStep.Finishing => s.FlydigiScreen_Step_Finishing,
                FlydigiScreenStep.HandingBack => s.FlydigiScreen_Step_HandingBack,
                FlydigiScreenStep.Done => StatusText.Text,
                _ => s.FlydigiScreen_Step_Starting,
            };
        }

        internal static string FailureText(FlydigiScreenFailure failure)
        {
            var s = Strings.Instance;
            return failure switch
            {
                FlydigiScreenFailure.NotWired => s.FlydigiScreen_Fail_NotWired,
                FlydigiScreenFailure.NoPort => s.FlydigiScreen_Fail_NoPort,
                FlydigiScreenFailure.SeveralPorts => s.FlydigiScreen_Fail_SeveralPorts,
                FlydigiScreenFailure.PortDidNotOpen => s.FlydigiScreen_Fail_PortDidNotOpen,
                FlydigiScreenFailure.Refused => s.FlydigiScreen_Fail_Refused,
                FlydigiScreenFailure.NoXboxPad => s.FlydigiScreen_Fail_NoXboxPad,
                FlydigiScreenFailure.OtherXboxPads => s.FlydigiScreen_Fail_OtherXboxPads,
                FlydigiScreenFailure.NotApex4 => s.FlydigiScreen_Fail_NotApex4,
                FlydigiScreenFailure.BindFailed => s.FlydigiScreen_Fail_BindFailed,
                FlydigiScreenFailure.Canceled => s.FlydigiScreen_Fail_Canceled,
                FlydigiScreenFailure.TooManyFrames => s.FlydigiScreen_Fail_Refused,
                _ => s.FlydigiScreen_Fail_NoReply,
            };
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!Sending) return;
            // Close, Cancel while a send runs, and Escape all come here. A
            // send cut off partway leaves the screen waiting, so the dialog
            // asks first and then stops it. The send hands the pad back on
            // its own thread.
            if (MessageBox.Show(this, Strings.Instance.FlydigiScreen_LeaveWhileSending, Strings.Instance.FlydigiScreen_Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _sending.Cancel();
        }
    }
}
