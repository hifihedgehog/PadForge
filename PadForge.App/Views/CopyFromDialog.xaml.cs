using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Views
{
    /// <summary>Copy From (#574): pick the controller whose mappings and
    /// tuning, or whose macros, another controller takes. The caller words
    /// the line under the title and builds the rows.</summary>
    public partial class CopyFromDialog : DialogWindow
    {
        /// <summary>
        /// Represents a device entry in the "Copy From" list.
        /// </summary>
        public class DeviceEntry
        {
            /// <summary>The row's first line: the source controller as its
            /// page names it ("Xbox #1"), or the device's name for a device
            /// on no controller.</summary>
            public string Name { get; set; }

            /// <summary>The row's second line: the device that drives the
            /// controller, or that the device is on none.</summary>
            public string Detail { get; set; }

            public Guid InstanceGuid { get; set; }
            public PadSetting PadSetting { get; set; }
            public VirtualControllerType OutputType { get; set; }
            public bool IsExtended { get; set; }
            /// <summary>Source slot index. Caller fills this so the
            /// Copy From apply path can pull this device's slice of
            /// the source slot's MappingSet (multi-source ExtraSources
            /// + CombineMode + Custom formula). Issue #61.</summary>
            public int SourceSlot { get; set; } = -1;

            /// <summary>What a screen reader says for the row.</summary>
            public string ReadName => string.IsNullOrEmpty(Detail) ? Name : Name + ", " + Detail;
        }

        /// <summary>
        /// The PadSetting selected by the user, or null if canceled.
        /// </summary>
        public PadSetting SelectedPadSetting { get; private set; }

        /// <summary>
        /// The selected device entry (includes OutputType and layout info).
        /// </summary>
        public DeviceEntry SelectedEntry { get; private set; }

        public CopyFromDialog(IEnumerable<DeviceEntry> devices, string subtitle)
        {
            InitializeComponent();
            Subtitle = subtitle;
            var rows = devices.ToList();
            DeviceList.ItemsSource = rows;
            // The first row is picked on open, so Copy is live at once.
            if (rows.Count > 0) DeviceList.SelectedIndex = 0;
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
