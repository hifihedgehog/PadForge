using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Shapes;
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
    public partial class RegisterNfcTagDialog : DialogWindow
    {
        private readonly NfcReaderService _svc;
        private Action<string, string> _handler;
        private Action<string> _controllerHandler;
        private string _capturedUid;

        public RegisterNfcTagDialog()
        {
            InitializeComponent();

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

            // The line names the source that can capture: the reader when
            // there is one, and a Switch controller when there is not, since
            // a controller can arrive while the dialog is open.
            StatusText.Text = _svc != null ? Strings.Instance.Nfc_TapPrompt : Strings.Instance.Nfc_WaitingController;

            Loaded += (_, _) => NameBox.Focus();
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
                // The flame is ember while a read tag waits for its name.
                LiveFlame.SetResourceReference(Shape.FillProperty, RegisterBtn.IsEnabled ? "EmberBrush" : "WaitBrush");
                if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Focus();
            }));
        }

        private void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_capturedUid)) return;
            string name = NfcTagRegistry.Register(_capturedUid, NameBox.Text);
            StatusText.Text = string.Format(Strings.Instance.Nfc_RegisteredFormat, name);
            NameBox.Text = string.Empty;
            // Waiting again: no UID shows, and the flame is the wait color.
            UidText.Text = string.Empty;
            LiveFlame.SetResourceReference(Shape.FillProperty, "WaitBrush");
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
                // The row and its button are gone, so the focus they held
                // goes to the name.
                NameBox.Focus();
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

        private void RefreshList()
        {
            var tags = NfcTagRegistry.Tags;
            TagListBox.ItemsSource = tags;
            TagsBlock.Visibility = tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
