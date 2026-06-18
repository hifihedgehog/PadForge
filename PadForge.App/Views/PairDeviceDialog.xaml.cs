using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using PadForge.Resources.Strings;
using PadForge.Services;
using Wpf.Ui.Controls;

namespace PadForge.Views
{
    /// <summary>
    /// In-app Bluetooth pairing flow for Wii controllers (issue #116). Drives
    /// <see cref="WiiPairingService"/> on a background thread, showing a live
    /// found-controller list and progress while the user holds the SYNC button.
    /// <see cref="DialogResult"/> is true when at least one controller paired,
    /// so the caller can refresh the device list.
    /// </summary>
    public partial class PairDeviceDialog : FluentWindow
    {
        private readonly WiiPairingService _service = new();
        private CancellationTokenSource _cts;
        private bool _scanning;
        private bool _pairedAny;

        public PairDeviceDialog()
        {
            InitializeComponent();
            Closing += OnClosing;
        }

        private async void Pair_Click(object sender, RoutedEventArgs e)
        {
            if (_scanning) return;

            _scanning = true;
            _pairedAny = false;
            PairButton.IsEnabled = false;
            TemporaryCheck.IsEnabled = false;
            FamilyCombo.IsEnabled = false;
            ScanRing.Visibility = Visibility.Visible;
            FoundPanel.Visibility = Visibility.Collapsed;
            FoundList.Items.Clear();
            SetStatus(Strings.Instance.WiiPair_Searching, secondary: true);

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            bool temporary = TemporaryCheck.IsChecked == true;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                while (!token.IsCancellationRequested)
                {
                    WiiPairingService.PairPassResult pass =
                        await Task.Run(() => _service.RunPairingPass(temporary, token));

                    if (token.IsCancellationRequested) break;

                    if (pass.Error == "no-radio" || pass.Error == "no-bluetooth-stack")
                    {
                        SetStatus(Strings.Instance.WiiPair_NoBluetooth, error: true);
                        break;
                    }

                    foreach (string name in pass.Found)
                    {
                        if (seen.Add(name))
                        {
                            FoundList.Items.Add(name);
                            FoundPanel.Visibility = Visibility.Visible;
                        }
                    }

                    if (pass.Paired.Count > 0)
                    {
                        _pairedAny = true;
                        SetStatus(string.Format(Strings.Instance.WiiPair_SuccessFormat,
                            string.Join(", ", pass.Paired)), success: true);
                        break;
                    }

                    if (seen.Count > 0)
                        SetStatus(Strings.Instance.WiiPair_Searching, secondary: true);
                    else
                        SetStatus(Strings.Instance.WiiPair_NothingYet, secondary: true);
                }
            }
            finally
            {
                ScanRing.Visibility = Visibility.Collapsed;
                _scanning = false;

                if (_pairedAny)
                {
                    // Pairing done. Turn the dismiss button into a confirming
                    // close and hide the redundant Pair button.
                    PairButton.Visibility = Visibility.Collapsed;
                    DismissButton.Content = Strings.Instance.WiiPair_Done;
                }
                else
                {
                    PairButton.IsEnabled = true;
                    TemporaryCheck.IsEnabled = true;
                    FamilyCombo.IsEnabled = true;
                }
            }
        }

        private void Dismiss_Click(object sender, RoutedEventArgs e)
        {
            // Stops an in-flight scan (OnClosing cancels the token) and closes.
            DialogResult = _pairedAny;
            Close();
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _cts?.Cancel();
        }

        private void SetStatus(string text, bool secondary = false, bool success = false, bool error = false)
        {
            StatusText.Text = text;
            string brushKey = success ? "SystemFillColorSuccessBrush"
                : error ? "SystemFillColorCriticalBrush"
                : secondary ? "TextFillColorSecondaryBrush"
                : "TextFillColorPrimaryBrush";
            StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brushKey);
        }
    }
}
