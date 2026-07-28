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
            public bool IsExtended { get; set; }
            /// <summary>Source slot index. Caller fills this so the
            /// Copy From apply path can pull this device's slice of
            /// the source slot's MappingSet (multi-source ExtraSources
            /// + CombineMode + Custom formula) — Issue #61.</summary>
            public int SourceSlot { get; set; } = -1;
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
            // FluentWindow sets ExtendsContentIntoTitleBar, which zeroes
            // WindowChrome.CaptionHeight, and this dialog declares no
            // <ui:TitleBar>, so no point in the window was non-client and it
            // could not be moved at all. Same remedy MainWindow uses on its
            // branding bar. Controls that need the click (Button, TextBox,
            // ListBoxItem) mark this bubbling event handled, so the drag only
            // starts on inert chrome.
            MouseLeftButtonDown += (_, __) => { try { DragMove(); } catch { } };

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
