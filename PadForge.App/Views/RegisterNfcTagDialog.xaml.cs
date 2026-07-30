using System;
using System.Windows;
using System.Windows.Input;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
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
        private Action<string> _controllerHandler;
        private string _capturedUid;

        public RegisterNfcTagDialog()
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

            _svc = NfcReaderService.Active;
            RefreshList();

            // Capture from a Switch controller too (issue #241): the tag
            // reader on a right Joy-Con / Pro Controller raises
            // NfcTagRegistry.ControllerTagDetected. RegistrationCaptureActive
            // powers the MCU while this dialog is open, so a tap is caught
            // even before any tag is registered. Both sources normalize the
            // UID through the same registry, so a tag registered from either
            // binds on either.
            _controllerHandler = OnControllerTag;
            NfcTagRegistry.ControllerTagDetected += _controllerHandler;
            NfcTagRegistry.RegistrationCaptureActive = true;

            // A PC/SC reader, if present, raises its own event.
            if (_svc != null)
            {
                _handler = OnTagDetected;
                _svc.TagDetected += _handler;
            }

            // Status: name whichever source(s) can capture. With neither a
            // PC/SC reader nor (potentially) a controller, the old "no
            // reader" text still stands, but a controller can arrive later,
            // so the wait text is the honest default when a reader is absent.
            StatusText.Text = _svc != null ? Strings.Instance.Nfc_Waiting : Strings.Instance.Nfc_WaitingController;

            Closed += (s, e) => Unsubscribe();
        }

        private void Unsubscribe()
        {
            if (_svc != null && _handler != null)
            {
                try { _svc.TagDetected -= _handler; } catch { }
            }
            if (_controllerHandler != null)
            {
                try { NfcTagRegistry.ControllerTagDetected -= _controllerHandler; } catch { }
            }
            NfcTagRegistry.RegistrationCaptureActive = false;
            _handler = null;
            _controllerHandler = null;
        }

        private void OnControllerTag(string uid) => OnTagDetected("controller", uid);

        private void OnTagDetected(string reader, string uid)
        {
            // Fired on the monitor thread; hop to the UI thread before touching
            // controls.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _capturedUid = NfcTagRegistry.NormalizeUid(uid);
                UidText.Text = _capturedUid;
                RegisterBtn.IsEnabled = !string.IsNullOrEmpty(_capturedUid);
                StatusText.Text = Strings.Instance.Nfc_TagCaptured;
                if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Focus();
            }));
        }

        private void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_capturedUid)) return;
            string name = NfcTagRegistry.Register(_capturedUid, NameBox.Text);
            StatusText.Text = string.Format(Strings.Instance.Nfc_RegisteredFormat, name);
            NameBox.Text = string.Empty;
            UidText.Text = Strings.Instance.Nfc_Waiting;
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
