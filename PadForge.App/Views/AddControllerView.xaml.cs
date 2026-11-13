using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PadForge.Engine;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>Add a Controller (#574): the type tiles and the device that
    /// drives the new controller. A UserControl, not a dialog window: the
    /// ShellDialog that hosts it carries the dialog's role, so a screen reader
    /// meets one dialog. MainWindow makes the controller a tile asks for and
    /// closes the host.</summary>
    public partial class AddControllerView : UserControl
    {
        public AddControllerView()
        {
            InitializeComponent();
        }

        /// <summary>Raised by a tile whose type can be made, with its type.</summary>
        public event EventHandler<VirtualControllerType> TypeChosen;

        /// <summary>Raised by the X.</summary>
        public event EventHandler CloseRequested;

        private void TypeTile_Click(object sender, RoutedEventArgs e)
        {
            // An unavailable tile stays clickable and does nothing, as the
            // popup's buttons did.
            if (sender is FrameworkElement { DataContext: ControllerTypeChoice choice } && choice.IsAvailable)
                TypeChosen?.Invoke(this, choice.Type);
        }

        private void DrivenBy_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: DrivenByChoice choice } && DataContext is AddControllerViewModel vm)
                vm.SelectedDevice = choice.Device;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

        /// <summary>Puts the focus on the first tile whose type can be made,
        /// and on the X when none can.</summary>
        public void FocusFirstChoice()
        {
            foreach (var list in new[] { CommonTiles, MoreTiles })
            {
                for (int i = 0; i < list.Items.Count; i++)
                {
                    if (list.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject container
                        && FindTile(container) is PadForge.Controls.AvailabilityButton tile && tile.IsAvailable
                        && tile.Focus())
                        return;
                }
            }
            CloseButton.Focus();
        }

        private static PadForge.Controls.AvailabilityButton FindTile(DependencyObject root)
        {
            if (root is PadForge.Controls.AvailabilityButton tile) return tile;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (FindTile(VisualTreeHelper.GetChild(root, i)) is PadForge.Controls.AvailabilityButton found)
                    return found;
            return null;
        }
    }
}
