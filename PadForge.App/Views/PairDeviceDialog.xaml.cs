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
            // FluentWindow sets ExtendsContentIntoTitleBar, which zeroes
            // WindowChrome.CaptionHeight, and this dialog declares no
            // <ui:TitleBar>, so no point in the window was non-client and it
            // could not be moved at all. Same remedy MainWindow uses on its
            // branding bar. Controls that need the click (Button, TextBox,
            // ListBoxItem) mark this bubbling event handled, so the drag only
            // starts on inert chrome.
            MouseLeftButtonDown += (_, __) => { try { DragMove(); } catch { } };

            Closing += OnClosing;
        }

        /// <summary>0 = Wii (inquiry scan), 1 = DualShock 3 (guided USB ceremony).</summary>
        private bool IsDs3Family => FamilyCombo.SelectedIndex == 1;

        private void Family_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (InstructionsText == null) return; // fires once during InitializeComponent
            bool ds3 = IsDs3Family;
            InstructionsText.Text = ds3 ? Strings.Instance.Ds3Pair_Instructions
                                        : Strings.Instance.WiiPair_Instructions;
            // The "temporary pairing" and live found-list are Wii-only concepts.
            TemporaryCheck.Visibility = ds3 ? Visibility.Collapsed : Visibility.Visible;
            FoundPanel.Visibility = Visibility.Collapsed;
            SetStatus(string.Empty);
        }

        private void Pair_Click(object sender, RoutedEventArgs e)
        {
            if (_scanning) return;
            if (IsDs3Family) { _ = PairDs3(); return; }
            _ = PairWii();
        }

        /// <summary>
        /// DualShock 3: run the USB pairing ceremony (sixpair + registry identity +
        /// radio cycle) on a background thread, streaming each step to the status line.
        /// </summary>
        private async Task PairDs3()
        {
            _scanning = true;
            _pairedAny = false;
            PairButton.IsEnabled = false;
            FamilyCombo.IsEnabled = false;
            ScanRing.Visibility = Visibility.Visible;
            SetStatus(Strings.Instance.Ds3Pair_Working, secondary: true);

            var svc = new Ds3PairingService(msg =>
                Dispatcher.BeginInvoke(new Action(() => SetStatus(msg, secondary: true))));

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Ds3PairingService.PairResult result = null;
            try
            {
                result = await Task.Run(() => svc.RunPairing(token));
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, error: true);
            }
            finally
            {
                ScanRing.Visibility = Visibility.Collapsed;
                _scanning = false;
            }

            if (result != null && result.Success)
            {
                _pairedAny = true;
                SetStatus(Strings.Instance.Ds3Pair_Success, success: true);
                PairButton.Visibility = Visibility.Collapsed;
                DismissButton.Content = Strings.Instance.WiiPair_Done;
            }
            else
            {
                // Every code that has something actionable to say gets its own
                // message. The catch-all used to send people to a log file
                // PadForge does not write (#265): pairing narration goes to the
                // in-memory diagnostics ring, which only reaches disk when
                // PADFORGE_DIAG is set. "no-radio" in particular is just
                // Bluetooth being switched off, and it is checked BEFORE the
                // WinUSB bind, so the user never reaches the USB step.
                string msg = result?.Error switch
                {
                    "no-ds3-usb" or "winusb-bind-failed" => Strings.Instance.Ds3Pair_NoUsb,
                    "install-failed" => Strings.Instance.Ds3Pair_InstallFailed,
                    "no-radio" => Strings.Instance.Ds3Pair_NoRadio,
                    _ => Strings.Instance.Ds3Pair_Failed,
                };
                SetStatus(msg, error: true);
                PairButton.IsEnabled = true;
                FamilyCombo.IsEnabled = true;
            }
        }

        private async Task PairWii()
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
