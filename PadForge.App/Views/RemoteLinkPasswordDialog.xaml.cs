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
                Show("Enter a password.");
                return;
            }
            if (_setMode && pw != (ConfirmBox.Password ?? ""))
            {
                Show("The passwords don't match.");
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
