using System.Windows.Controls;

namespace PadForge.Views
{
    /// <summary>
    /// The single home for Remote Link (issue #138): on/off + port + status, identity
    /// protection mode, the paired-PC manager (online state + editable names + revoke),
    /// and nearby unpaired PCs. DataContext is the main view model, so it binds the
    /// Dashboard's connection state and the Settings peer/identity state in one place
    /// instead of splitting Remote Link across two pages.
    /// </summary>
    public partial class RemoteLinkPage : UserControl
    {
        public RemoteLinkPage()
        {
            InitializeComponent();
        }
    }
}
