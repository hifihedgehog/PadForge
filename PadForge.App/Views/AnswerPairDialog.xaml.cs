using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.Services.Automation.Web;

namespace PadForge.Views
{
    /// <summary>
    /// Pair a Device… (#600): a window of its own that Windows leaves out of
    /// every screen capture (SetWindowDisplayAffinity with
    /// WDA_EXCLUDEFROMCAPTURE), so a program that reads the screen cannot
    /// pair itself. The pairing opens only once Windows has hidden the
    /// window, and the window closes it when it closes. The window follows
    /// the pairing each second: the address once the listener serves it, the
    /// time left, and how the pairing ended.
    /// </summary>
    public partial class AnswerPairDialog : DialogWindow
    {
        /// <summary>The window shows nowhere but on the monitor (Windows 10
        /// 2004 and later): a capture of the screen shows black in its
        /// place.</summary>
        internal const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

        private readonly WebAnswer _answers;
        private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
        private AnswerPairing _pairing;
        private string _shownLink;
        private bool _hidden;
        private string _ended;

        internal AnswerPairDialog(WebAnswer answers)
        {
            _answers = answers;
            InitializeComponent();
            _clock.Tick += (_, _) => ShowState();
            _answers.PairingEnded += OnPairingEnded;
            _answers.StateChanged += OnStateChanged;
            // Cancel holds the focus as the window opens, so a key press
            // lands inside it.
            Loaded += (_, _) => CancelButton.Focus();
        }

        /// <summary>Whether Windows took the window out of screen capture.
        /// Until it has, no pairing opens and no code shows.</summary>
        internal bool HiddenFromCapture => _hidden;

        /// <summary>The pairing this window opened, or null.</summary>
        internal AnswerPairing Pairing => _pairing;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            _hidden = hwnd != IntPtr.Zero && SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
            if (_hidden)
            {
                _pairing = _answers.StartPairing();
                _clock.Start();
            }
            ShowState();
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess()) ShowState();
            else Dispatcher.BeginInvoke(new Action(ShowState));
        }

        private void OnPairingEnded(object sender, AnswerPairingEnd how)
        {
            void Ended()
            {
                if (_pairing == null || _ended != null) return;
                var s = Strings.Instance;
                switch (how)
                {
                    case AnswerPairingEnd.Paired:
                        _pairing = null;
                        Close();
                        return;
                    case AnswerPairingEnd.Expired: _ended = s.Answer_Pair_Ended_Expired; break;
                    default: return;
                }
                _pairing = null;
                _clock.Stop();
                CancelButton.Content = s.Common_Close;
                ShowState();
            }
            if (Dispatcher.CheckAccess()) Ended();
            else Dispatcher.BeginInvoke(new Action(Ended));
        }

        /// <summary>The code and its lines while the pairing is open and the
        /// page has an address, and otherwise the one line that says why not.
        /// Once five wrong typed codes have closed the typed code, the QR code
        /// stays and the line under it says until when it works.</summary>
        private void ShowState()
        {
            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            // Reading the pairing closes one past its time.
            if (_pairing != null) _ = _answers.Pairing;
            string address = _answers.Address;
            string state;
            if (!_hidden) state = s.Answer_Pair_NotHidden;
            else if (_ended != null) state = _ended;
            else if (_pairing == null) state = null;
            else if (address == null)
                state = _answers.AddressProblem is { } problem
                    ? string.Format(culture, s.Answer_Pair_NoPage_Format, problem.TrimEnd().TrimEnd('.'))
                    : s.Answer_Pair_Starting;
            else state = null;

            bool showCode = _hidden && _ended == null && _pairing != null && address != null;
            bool typedClosed = showCode && _answers.TypedCodeClosed;
            if (typedClosed)
                state = string.Format(culture, s.Answer_Pair_CodeClosed_Format, _pairing.Until.ToLocalTime().ToString("t", culture));
            CodePanel.Visibility = showCode ? Visibility.Visible : Visibility.Collapsed;
            TypedPanel.Visibility = typedClosed ? Visibility.Collapsed : Visibility.Visible;
            // Under the QR code the line keeps the gap the address line had.
            StateText.Margin = typedClosed ? new Thickness(0, 14, 0, 0) : new Thickness(0);
            if (showCode)
            {
                string link = WebAnswer.PairingLink(address, _pairing);
                if (link != _shownLink)
                {
                    QrImage.Source = WebControllerServer.RenderQr(link);
                    _shownLink = link;
                }
                OpenText.Text = string.Format(culture, s.Answer_Pair_Open_Format, address);
                string code = typedClosed ? null : WebSecrets.Spaced(_pairing.Code);
                if (!Equals(CodeDigits.ItemsSource, code))
                {
                    CodeDigits.ItemsSource = code;
                    System.Windows.Automation.AutomationProperties.SetName(CodeDigits, code ?? string.Empty);
                }
                UntilText.Text = string.Format(culture, s.Answer_Pair_Until_Format, _pairing.Until.ToLocalTime().ToString("t", culture));
            }
            else
            {
                QrImage.Source = null;
                _shownLink = null;
                CodeDigits.ItemsSource = null;
                System.Windows.Automation.AutomationProperties.SetName(CodeDigits, string.Empty);
            }
            StateText.Text = state ?? string.Empty;
            StateText.Visibility = state == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            _clock.Stop();
            _answers.PairingEnded -= OnPairingEnded;
            _answers.StateChanged -= OnStateChanged;
            // A pairing this window opened ends with it.
            if (_pairing != null)
            {
                _pairing = null;
                _answers.CancelPairing();
            }
            base.OnClosed(e);
        }
    }
}
