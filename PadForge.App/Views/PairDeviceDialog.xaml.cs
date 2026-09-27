using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>True only when the Wii inquiry scan actually paired
        /// something. The caller's Wii driver re-scan is an eleven-second hint
        /// toggle that drops and re-opens every connected Wii Remote, so a
        /// canceled dialog and the DualShock 3 and Move ceremonies, which never
        /// touch that driver, must not pay for it.</summary>
        public bool PairedWii { get; private set; }

        /// <summary>Where the serial family's adds and removes land. The
        /// settings view model owns the list, persists it and hands it to
        /// SDL through its changed event.</summary>
        private readonly ViewModels.SettingsViewModel _settings;

        public PairDeviceDialog(ViewModels.SettingsViewModel settings)
        {
            _settings = settings;
            InitializeComponent();
            ControllerCombo.ItemsSource = Common.Input.SerialControllers.Protocols.Select(p => p.Name).ToList();
            ControllerCombo.SelectedIndex = 0;
            SerialAddedList.ItemsSource = _settings?.SerialControllers;
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

        /// <summary>0 = Wii (inquiry scan), 1 = DualShock 3 (guided USB ceremony),
        /// 2 = PS Move / Navigation (guided USB ceremony, #277), 3 = a
        /// controller on a COM port (#33).</summary>
        private bool IsDs3Family => FamilyCombo.SelectedIndex == 1;
        private bool IsMoveFamily => FamilyCombo.SelectedIndex == 2;
        private bool IsSerialFamily => FamilyCombo.SelectedIndex == 3;

        private void Family_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (InstructionsText == null) return; // fires once during InitializeComponent
            bool ds3 = IsDs3Family;
            bool move = IsMoveFamily;
            bool serial = IsSerialFamily;
            InstructionsText.Text = serial ? Strings.Instance.SerialPair_Instructions
                                  : move ? Strings.Instance.MovePair_Instructions
                                  : ds3 ? Strings.Instance.Ds3Pair_Instructions
                                        : Strings.Instance.WiiPair_Instructions;
            // The "temporary pairing" and live found-list are Wii-only concepts.
            TemporaryCheck.Visibility = (ds3 || move || serial) ? Visibility.Collapsed : Visibility.Visible;
            FoundPanel.Visibility = Visibility.Collapsed;
            SerialPickers.Visibility = serial ? Visibility.Visible : Visibility.Collapsed;
            PairButton.Content = serial ? Strings.Instance.Common_Add : Strings.Instance.WiiPair_Pair;
            SetStatus(string.Empty);
            // The serial family disables Add while no port is present, and
            // the pairing families always start enabled.
            if (serial) RefreshPorts();
            else PairButton.IsEnabled = true;
            UpdateSerialAddedPanel();
        }

        private void Pair_Click(object sender, RoutedEventArgs e)
        {
            if (_scanning) return;
            if (IsSerialFamily) { AddSerial(); return; }
            if (IsDs3Family) { _ = PairDs3(); return; }
            if (IsMoveFamily) { _ = PairDs3(moveFamily: true); return; }
            _ = PairWii();
        }

        /// <summary>
        /// DualShock 3: run the USB pairing ceremony (sixpair + registry identity +
        /// radio cycle) on a background thread, streaming each step to the status line.
        /// </summary>
        private async Task PairDs3(bool moveFamily = false)
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
            string fault = null;
            try
            {
                result = await Task.Run(() => moveFamily ? svc.RunMovePairing(token) : svc.RunPairing(token));
            }
            catch (Exception ex)
            {
                // Held, not shown: the else branch below used to overwrite
                // this with the generic verdict, so the one message that
                // named the real fault never survived to the screen.
                fault = ex.Message;
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
                string msg = fault ?? result?.Error switch
                {
                    "no-move-usb" => Strings.Instance.MovePair_NoUsb,
                    "no-ds3-usb" or "winusb-bind-failed" => Strings.Instance.Ds3Pair_NoUsb,
                    "install-failed" => Strings.Instance.Ds3Pair_InstallFailed,
                    "no-radio" => Strings.Instance.Ds3Pair_NoRadio,
                    "driver-untrusted" => Strings.Instance.Ds3Pair_DriverUntrusted,
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
            var scanCancellation = _cts;
            WiiPairingService.PairPassResult shown = null;

            void ShowPass(WiiPairingService.PairPassResult pass)
            {
                if (token.IsCancellationRequested || ReferenceEquals(shown, pass)) return;
                shown = pass;
                if (pass.Error == "no-radio" || pass.Error == "no-bluetooth-stack")
                {
                    SetStatus(Strings.Instance.WiiPair_NoBluetooth, error: true);
                    return;
                }
                if (pass.Error != null)
                {
                    SetStatus(pass.Error == WiiPairingService.PsmVerificationFailed
                        ? Strings.Instance.WiiPair_PsmUnavailable : Strings.Instance.WiiPair_Failed, error: true);
                    return;
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
                    PairedWii = true;
                    SetStatus(string.Format(Strings.Instance.WiiPair_SuccessFormat,
                        string.Join(", ", pass.Paired)), success: true);
                }
                else
                    SetStatus(seen.Count > 0 ? Strings.Instance.WiiPair_Searching
                        : Strings.Instance.WiiPair_NothingYet, secondary: true);
            }

            var progress = new Progress<WiiPairingService.PairPassResult>(pass =>
            {
                if (_scanning && ReferenceEquals(_cts, scanCancellation)) ShowPass(pass);
            });
            try
            {
                var result = await Task.Run(() => _service.RunPairingScan(temporary, token, progress));
                // A very short scan can finish before queued progress is shown.
                ShowPass(result);
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("WIIPAIR scan failed: " + ex);
                _pairedAny = false;
                if (!token.IsCancellationRequested)
                    SetStatus(Strings.Instance.WiiPair_Failed, error: true);
            }
            finally
            {
                ScanRing.Visibility = Visibility.Collapsed;
                _scanning = false;

                if (_pairedAny)
                {
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

        // ── Serial controllers (#33) ─────────────────────────────────────

        /// <summary>Lists the present COM ports that SDL's hint can name,
        /// keeping the selection when its port is still there. A port Windows
        /// numbers after the dialog opens appears when the list is opened
        /// again.</summary>
        private void RefreshPorts()
        {
            string keep = (PortCombo.SelectedItem as Common.Input.ComPort)?.InstanceId;
            var ports = Common.Input.SerialControllers.ListComPorts()
                .Where(p => Common.Input.SerialControllers.PortKey(p) != null)
                .ToList();
            PortCombo.ItemsSource = ports;
            PortCombo.SelectedItem = ports.FirstOrDefault(p => p.InstanceId == keep) ?? ports.FirstOrDefault();
            PairButton.IsEnabled = ports.Count > 0 && _settings != null;
            if (ports.Count == 0) SetStatus(Strings.Instance.SerialPair_NoPorts, secondary: true);
            else if (StatusText.Text == Strings.Instance.SerialPair_NoPorts) SetStatus(string.Empty);
        }

        private void PortCombo_DropDownOpened(object sender, EventArgs e) => RefreshPorts();

        private void AddSerial()
        {
            if (_settings == null || PortCombo.SelectedItem is not Common.Input.ComPort port) return;
            int pick = ControllerCombo.SelectedIndex;
            if (pick < 0 || pick >= Common.Input.SerialControllers.Protocols.Length) return;
            var (token, name) = Common.Input.SerialControllers.Protocols[pick];
            string key = Common.Input.SerialControllers.PortKey(port);
            if (key == null) return;

            // A port carries one controller. Naming it again replaces the
            // entry, which is also how SDL reads a port named twice: the last
            // entry wins.
            var list = _settings.SerialControllers;
            var same = list.FirstOrDefault(s => string.Equals(s.Port, key, StringComparison.OrdinalIgnoreCase));
            if (same == null && list.Count >= Common.Input.SerialControllers.MaxEntries)
            {
                SetStatus(Strings.Instance.SerialPair_Full, error: true);
                return;
            }
            var entry = new Common.Input.SerialControllerEntry { Port = key, PortName = port.PortName, Protocol = token };
            if (same != null) list[list.IndexOf(same)] = entry;
            else list.Add(entry);
            _settings.RaiseSerialControllersChanged();
            UpdateSerialAddedPanel();
            SetStatus(string.Format(Strings.Instance.SerialPair_AddedFormat, name, port.PortName), success: true);
        }

        private void SerialRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null || sender is not FrameworkElement { Tag: Common.Input.SerialControllerEntry entry }) return;
            if (_settings.SerialControllers.Remove(entry))
                _settings.RaiseSerialControllersChanged();
            UpdateSerialAddedPanel();
            SetStatus(string.Empty);
        }

        private void UpdateSerialAddedPanel()
            => SerialAddedPanel.Visibility = IsSerialFamily && _settings?.SerialControllers.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

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
            // A verdict promotes the narration line it is replacing into the
            // line above it, so "the step that failed is shown above" is
            // true. Progress lines just replace each other as before.
            if ((success || error) && !string.IsNullOrWhiteSpace(StatusText.Text)
                && !ReferenceEquals(StatusText.Text, text))
            {
                LastStepText.Text = StatusText.Text;
                LastStepText.Visibility = Visibility.Visible;
            }
            else if (!success && !error)
            {
                LastStepText.Visibility = Visibility.Collapsed;
            }
            StatusText.Text = text;
            string brushKey = success ? "SystemFillColorSuccessBrush"
                : error ? "SystemFillColorCriticalBrush"
                : secondary ? "TextFillColorSecondaryBrush"
                : "TextFillColorPrimaryBrush";
            StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brushKey);
        }
    }
}
