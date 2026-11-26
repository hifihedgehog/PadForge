using System.Globalization;
using System.Windows;
using PadForge.Resources.Strings;
using PadForge.Services.Automation.Web;

namespace PadForge.Views
{
    /// <summary>Change… on the network door's Port row (#600 item 53): a port
    /// from 1024 to 49151, or nothing when the user cancels.</summary>
    public partial class NetworkPortDialog : DialogWindow
    {
        private NetworkPortDialog(int port)
        {
            InitializeComponent();
            PortBox.Text = port == 0 ? string.Empty : port.ToString(CultureInfo.CurrentCulture);
            Loaded += (_, _) =>
            {
                PortBox.Focus();
                PortBox.SelectAll();
            };
        }

        /// <summary>The port typed, when OK was pressed.</summary>
        internal int Port { get; private set; }

        internal static int? Ask(Window owner, int port)
        {
            var dialog = new NetworkPortDialog(port) { Owner = owner };
            return dialog.ShowDialog() == true ? dialog.Port : null;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(PortBox.Text?.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out int port) || !NetworkDoor.IsPort(port))
            {
                ErrorText.Text = Strings.Instance.Net_Port_Wrong;
                ErrorText.Visibility = Visibility.Visible;
                PortBox.Focus();
                return;
            }
            Port = port;
            DialogResult = true;
        }
    }
}
