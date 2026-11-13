using System.Windows;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    /// <summary>What a confirm's action is: a removal, in the danger look,
    /// or a step forward, in the ember look.</summary>
    public enum ConfirmKind { Destructive, Primary }

    /// <summary>The shared confirm (#574) on the dialog frame: the question
    /// as the title, what happens as the line under it, an optional fact in
    /// the code face, and a foot where Cancel holds the default, the cancel
    /// and the first focus. Esc cancels, and Enter never fires the action
    /// unless the user tabs onto it. <see cref="Notice"/> is the same window
    /// with nothing to decide.</summary>
    public partial class ConfirmDialog : DialogWindow
    {
        private bool _confirmed;

        /// <summary>A null <paramref name="actionText"/> makes a notice: the
        /// action is gone and the one button reads Close, or
        /// <paramref name="cancelText"/>.</summary>
        private ConfirmDialog(string title, string message, string actionText, string detail, ConfirmKind kind, string cancelText)
        {
            InitializeComponent();
            Title = title;
            Subtitle = message;
            if (!string.IsNullOrEmpty(detail))
            {
                DetailText.Text = detail;
                DetailText.Visibility = Visibility.Visible;
            }
            if (actionText == null)
            {
                ActionButton.Visibility = Visibility.Collapsed;
                CancelButton.Content = cancelText ?? Strings.Instance.Common_Close;
            }
            else
            {
                ActionButton.Content = actionText;
                if (kind == ConfirmKind.Primary)
                    ActionButton.SetResourceReference(StyleProperty, "DialogPrimaryButton");
                if (cancelText != null) CancelButton.Content = cancelText;
            }
            Loaded += (_, _) => CancelButton.Focus();
        }

        /// <summary>Modal confirm, centered on <paramref name="owner"/> (the
        /// screen when null). True only when the user chose the action.
        /// Callers pass their own words. <paramref name="detail"/> names the
        /// thing at stake in the code face, <paramref name="kind"/> picks the
        /// action's look, and <paramref name="cancelText"/> replaces Cancel's
        /// word.</summary>
        public static bool Show(Window owner, string title, string message, string actionText, string detail = null,
            ConfirmKind kind = ConfirmKind.Destructive, string cancelText = null)
        {
            var dlg = new ConfirmDialog(title, message, actionText ?? string.Empty, detail, kind, cancelText);
            Place(dlg, owner);
            dlg.ShowDialog();
            return dlg._confirmed;
        }

        /// <summary>A notice with nothing to decide: one button, Close unless
        /// the caller names another word.</summary>
        public static void Notice(Window owner, string title, string message, string detail = null, string closeText = null)
        {
            var dlg = new ConfirmDialog(title, message, null, detail, ConfirmKind.Destructive, closeText);
            Place(dlg, owner);
            dlg.ShowDialog();
        }

        private static void Place(ConfirmDialog dlg, Window owner)
        {
            if (owner != null)
                dlg.Owner = owner;
            else
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            _confirmed = true;
            DialogResult = true;
        }
    }
}
