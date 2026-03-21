using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Views
{
    public partial class CopyFromDialog : Wpf.Ui.Controls.FluentWindow
    {
        /// <summary>
        /// Represents a device entry in the "Copy From" list.
        /// </summary>
        public class DeviceEntry
        {
            public string Name { get; set; }
            public string SlotLabel { get; set; }
            public string LayoutLabel { get; set; }
            public Guid InstanceGuid { get; set; }
            public PadSetting PadSetting { get; set; }
            public VirtualControllerType OutputType { get; set; }
            public bool IsCustomVJoy { get; set; }
        }

        /// <summary>
        /// The PadSetting selected by the user, or null if cancelled.
        /// </summary>
        public PadSetting SelectedPadSetting { get; private set; }

        /// <summary>
        /// The selected device entry (includes OutputType and layout info).
        /// </summary>
        public DeviceEntry SelectedEntry { get; private set; }

        public CopyFromDialog(IEnumerable<DeviceEntry> devices)
        {
            InitializeComponent();
            DeviceList.ItemsSource = devices;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (DeviceList.SelectedItem is DeviceEntry entry)
            {
                SelectedEntry = entry;
                SelectedPadSetting = entry.PadSetting;
                DialogResult = true;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void DeviceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DeviceList.SelectedItem is DeviceEntry entry)
            {
                SelectedEntry = entry;
                SelectedPadSetting = entry.PadSetting;
                DialogResult = true;
            }
        }
    }
}
