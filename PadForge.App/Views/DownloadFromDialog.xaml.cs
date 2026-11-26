using System.Windows;
using System.Windows.Controls;
using PadForge.Common.Input;

namespace PadForge.Views
{
    /// <summary>
    /// Download From's Change… (#600 item 49): the address Update Now
    /// downloads the controller database from. OK takes the text only when
    /// <see cref="ControllerDatabaseAddress.TryRead"/> does, and
    /// <see cref="Address"/> is then its one form.
    /// </summary>
    public partial class DownloadFromDialog : DialogWindow
    {
        internal DownloadFromDialog(string address)
        {
            InitializeComponent();
            AddressBox.Text = address ?? string.Empty;
        }

        /// <summary>The address taken, valid when DialogResult is true.</summary>
        internal string Address { get; private set; }

        private void AddressBox_TextChanged(object sender, TextChangedEventArgs e) => ErrorText.Visibility = Visibility.Collapsed;

        // OK is the default button, so Enter in the box comes here.
        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!ControllerDatabaseAddress.TryRead(AddressBox.Text, out string address))
            {
                ErrorText.Visibility = Visibility.Visible;
                AddressBox.Focus();
                return;
            }
            Address = address;
            DialogResult = true;
        }
    }
}
