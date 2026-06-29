using System;
using System.Windows;
using System.Windows.Input;
using PadForge.Common.Input;
using PadForge.Services;

namespace PadForge.Views
{
    /// <summary>
    /// Capture-and-name flow for issue #150 per-tag binding: tap a tag, the
    /// reader's UID is captured, the user names it, and it is added to
    /// <see cref="NfcTagRegistry"/> (which surfaces it as a bindable button on
    /// the NFC device). Also lists registered tags with a Remove action. Listens
    /// to <see cref="NfcReaderService.TagDetected"/> directly while open and
    /// marshals to the UI thread; the event fires on the monitor thread.
    /// </summary>
    public partial class RegisterNfcTagDialog : Wpf.Ui.Controls.FluentWindow
    {
        private readonly NfcReaderService _svc;
        private Action<string, string> _handler;
        private string _capturedUid;

        public RegisterNfcTagDialog()
        {
            InitializeComponent();
            _svc = NfcReaderService.Active;
            RefreshList();

            if (_svc == null)
            {
                StatusText.Text = "No NFC reader detected. Connect a PC/SC reader and reopen.";
                return;
            }
            _handler = OnTagDetected;
            _svc.TagDetected += _handler;
            Closed += (s, e) => Unsubscribe();
        }

        private void Unsubscribe()
        {
            if (_svc != null && _handler != null)
            {
                try { _svc.TagDetected -= _handler; } catch { }
            }
            _handler = null;
        }

        private void OnTagDetected(string reader, string uid)
        {
            // Fired on the monitor thread; hop to the UI thread before touching
            // controls.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _capturedUid = NfcTagRegistry.NormalizeUid(uid);
                UidText.Text = _capturedUid;
                RegisterBtn.IsEnabled = !string.IsNullOrEmpty(_capturedUid);
                StatusText.Text = "Tag captured. Enter a name and click Register. Tap another tag to replace it.";
                if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Focus();
            }));
        }

        private void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_capturedUid)) return;
            string name = NfcTagRegistry.Register(_capturedUid, NameBox.Text);
            StatusText.Text = $"Registered \"{name}\". Tap another tag to add more.";
            NameBox.Text = string.Empty;
            UidText.Text = "(waiting)";
            _capturedUid = null;
            RegisterBtn.IsEnabled = false;
            RefreshList();
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string uid && !string.IsNullOrEmpty(uid))
            {
                NfcTagRegistry.Remove(uid);
                RefreshList();
            }
        }

        private void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && RegisterBtn.IsEnabled)
            {
                RegisterButton_Click(sender, e);
                e.Handled = true;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Unsubscribe();
            Close();
        }

        private void RefreshList() => TagListBox.ItemsSource = NfcTagRegistry.Tags;
    }
}
