using System.Windows;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    /// <summary>
    /// Set or enter the Remote Link portable-identity password (issue #138). In "set"
    /// mode it asks for a new password twice (must match); in "enter" mode it asks once
    /// to unlock a password-protected identity. DialogResult is true on OK with a valid
    /// entry; the password is exposed only to the caller and never persisted in clear.
    /// </summary>
    public partial class RemoteLinkPasswordDialog : DialogWindow
    {
        private readonly bool _setMode;

        public RemoteLinkPasswordDialog(bool setMode, string prompt)
        {
            InitializeComponent();

            _setMode = setMode;
            Title = setMode ? Strings.Instance.RemoteLink_PasswordSetTitle : Strings.Instance.RemoteLink_PasswordUnlockTitle;
            Subtitle = prompt;
            ConfirmRow.Visibility = setMode ? Visibility.Visible : Visibility.Collapsed;
            Loaded += (_, _) => PwBox.Focus();
        }

        /// <summary>The entered password (valid only when DialogResult is true).</summary>
        public string Password { get; private set; } = "";

        // OK is the default button, so Enter in either box comes here.
        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string pw = PwBox.Password ?? "";
            if (string.IsNullOrEmpty(pw))
            {
                Show(Strings.Instance.RemoteLink_PasswordEmpty);
                PwBox.Focus();
                return;
            }
            if (_setMode && pw != (ConfirmBox.Password ?? ""))
            {
                Show(Strings.Instance.RemoteLink_PasswordMismatch);
                ConfirmBox.Focus();
                return;
            }
            Password = pw;
            DialogResult = true;
        }

        /// <summary>The line under the boxes: why the password was not taken.</summary>
        private void Show(string message)
        {
            ErrorText.Text = message ?? string.Empty;
            ErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
