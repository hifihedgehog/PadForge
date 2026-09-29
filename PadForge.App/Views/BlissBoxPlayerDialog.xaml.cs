using System.Windows;
using System.Windows.Controls;

namespace PadForge.Views
{
    /// <summary>Sets a Bliss-Box port's player number (issue #469). The
    /// adapter stores the number and reconnects under that player's product
    /// ID, a new device to Windows, so the dialog wears the confirm dialog's
    /// chrome: Cancel holds the default and the focus.</summary>
    public partial class BlissBoxPlayerDialog : Wpf.Ui.Controls.FluentWindow
    {
        private int _player;

        private BlissBoxPlayerDialog(int current, string detail)
        {
            InitializeComponent();
            MouseLeftButtonDown += (_, __) => { try { DragMove(); } catch { } };
            Title = PadForge.Resources.Strings.Strings.Instance.BlissBoxPlayer_Title;
            DetailText.Text = detail ?? string.Empty;
            DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            PlayerBox.SelectedIndex = System.Math.Clamp(current, 1, 4) - 1;
            Loaded += (_, _) => CancelButton.Focus();
        }

        /// <summary>The player the user chose, 1 to 4, or 0 when canceled.</summary>
        public static int Show(Window owner, int current, string detail)
        {
            var dialog = new BlissBoxPlayerDialog(current, detail);
            if (owner != null) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowDialog();
            return dialog._player;
        }

        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (PlayerBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int player))
                _player = player;
            DialogResult = true;
        }
    }
}
