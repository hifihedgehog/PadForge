using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace PadForge.Views
{
    /// <summary>
    /// Set or enter the Remote Link portable-identity password (issue #138). In "set"
    /// mode it asks for a new password twice (must match); in "enter" mode it asks once
    /// to unlock a password-protected identity. DialogResult is true on OK with a valid
    /// entry; the password is exposed only to the caller and never persisted in clear.
    /// </summary>
    public partial class RemoteLinkPasswordDialog : FluentWindow
    {
        private readonly bool _setMode;

        public RemoteLinkPasswordDialog(bool setMode, string prompt)
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

            _setMode = setMode;
            PromptText.Text = prompt;
            ConfirmBox.Visibility = setMode ? Visibility.Visible : Visibility.Collapsed;
            Loaded += (_, _) => PwBox.Focus();
        }

        /// <summary>The entered password (valid only when DialogResult is true).</summary>
        public string Password { get; private set; } = "";

        private void Box_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Ok_Click(sender, e);
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string pw = PwBox.Password ?? "";
            if (string.IsNullOrEmpty(pw))
            {
                Show(PadForge.Resources.Strings.Strings.Instance.RemoteLink_PasswordEmpty);
                return;
            }
            if (_setMode && pw != (ConfirmBox.Password ?? ""))
            {
                Show(PadForge.Resources.Strings.Strings.Instance.RemoteLink_PasswordMismatch);
                return;
            }
            Password = pw;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Show(string message)
        {
            ErrorBar.Message = message;
            ErrorBar.IsOpen = true;
        }
    }
}
