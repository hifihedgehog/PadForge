using System.Windows;

namespace PadForge.Views
{
    /// <summary>Shared destructive-verb confirm (#175 phase 2). Same
    /// FluentWindow chrome as the other dialogs: ember tick + display-face
    /// title, body description with an optional mono fact line, and a footer
    /// where Cancel holds both the default and the initial focus. Esc
    /// cancels. Enter can never fire the destructive action unless the user
    /// tabs onto it deliberately.</summary>
    public partial class ConfirmDialog : Wpf.Ui.Controls.FluentWindow
    {
        private bool _confirmed;

        private ConfirmDialog(string title, string message, string actionText, string detail)
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

            Title = title;
            TitleText.Text = title;
            MessageText.Text = message;
            ActionButton.Content = actionText;
            if (!string.IsNullOrEmpty(detail))
            {
                DetailText.Text = detail;
                DetailText.Visibility = Visibility.Visible;
            }
            Loaded += (_, _) => CancelButton.Focus();
        }

        /// <summary>Modal confirm, centered on <paramref name="owner"/> (screen
        /// when null). Returns true only when the user clicked the destructive
        /// action button. Callers pass their own localized verb strings;
        /// <paramref name="detail"/> (optional) renders as the mono cold line
        /// naming the thing at stake.</summary>
        public static bool Show(Window owner, string title, string message, string actionText, string detail = null)
        {
            var dlg = new ConfirmDialog(title, message, actionText, detail);
            if (owner != null)
                dlg.Owner = owner;
            else
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.ShowDialog();
            return dlg._confirmed;
        }

        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            _confirmed = true;
            DialogResult = true;
        }
    }
}
