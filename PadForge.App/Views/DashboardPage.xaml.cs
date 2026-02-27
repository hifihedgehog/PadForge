using System;
using System.Windows;
using System.Windows.Controls;
using ModernWpf.Controls;
using PadForge.Engine;

namespace PadForge.Views
{
    public partial class DashboardPage : UserControl
    {
        public DashboardPage()
        {
            InitializeComponent();
        }

        /// <summary>Exposes the "Add Controller" card Border for popup placement.</summary>
        public Border AddControllerCardElement => AddControllerCard;

        /// <summary>Raised when the user clicks the "Add Controller" card.</summary>
        public event EventHandler AddControllerRequested;

        /// <summary>Raised when the user clicks delete on a slot card, carrying the slot index.</summary>
        public event EventHandler<int> DeleteSlotRequested;

        /// <summary>Raised when the user clicks the power button to toggle enabled state.</summary>
        public event EventHandler<(int SlotIndex, bool IsEnabled)> SlotEnabledToggled;

        /// <summary>Raised when the user clicks a type icon to change controller type.</summary>
        public event EventHandler<(int SlotIndex, VirtualControllerType Type)> SlotTypeChangeRequested;

        private void AddControllerCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            AddControllerRequested?.Invoke(this, EventArgs.Empty);
        }

        private void DeleteSlot_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int slotIndex)
                DeleteSlotRequested?.Invoke(this, slotIndex);
        }

        private void PowerToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int slotIndex)
            {
                // Toggle: read current state from the SlotSummary DataContext.
                if (btn.DataContext is ViewModels.SlotSummary summary)
                    SlotEnabledToggled?.Invoke(this, (slotIndex, !summary.IsEnabled));
            }
        }

        private void XboxType_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int slotIndex)
                SlotTypeChangeRequested?.Invoke(this, (slotIndex, VirtualControllerType.Xbox360));
        }

        private void DS4Type_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int slotIndex)
                SlotTypeChangeRequested?.Invoke(this, (slotIndex, VirtualControllerType.DualShock4));
        }

        private void VJoyType_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int slotIndex)
                SlotTypeChangeRequested?.Invoke(this, (slotIndex, VirtualControllerType.VJoy));
        }
    }
}
