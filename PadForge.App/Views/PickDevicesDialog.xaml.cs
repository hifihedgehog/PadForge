using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services.Automation;

namespace PadForge.Views
{
    /// <summary>
    /// Pick Devices… on an Allowed row or a network key's row (#600 DESIGN-2
    /// N3): every device on the Devices page with a tick, the ones picked
    /// before ticked, and a device picked before that is not here now kept
    /// in the list by the name it had.
    /// </summary>
    public partial class PickDevicesDialog : DialogWindow
    {
        /// <summary>One device of the list.</summary>
        public sealed class Row
        {
            public Guid Id { get; init; }
            public string Name { get; init; }
            public bool Picked { get; set; }
        }

        private readonly List<Row> _rows;

        private PickDevicesDialog(string program, ProgramReach reach, IEnumerable<(Guid Id, string Name)> devices)
        {
            InitializeComponent();
            Subtitle = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Reach_Pick_Subtitle_Format, program);
            _rows = devices.Select(d => new Row { Id = d.Id, Name = d.Name, Picked = reach?.Picked(d.Id) == true }).ToList();
            foreach (var kept in reach?.Devices ?? Array.Empty<ReachDevice>())
                if (_rows.All(r => r.Id != kept.Id)) _rows.Add(new Row { Id = kept.Id, Name = kept.Name, Picked = true });
            DeviceList.ItemsSource = _rows;
            NoneText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The devices on the Devices page, by the names the page
        /// gives them.</summary>
        internal static IReadOnlyList<(Guid Id, string Name)> DevicesNow()
        {
            var list = SettingsManager.UserDevices;
            if (list == null) return Array.Empty<(Guid, string)>();
            lock (list.SyncRoot)
                return list.Items.Where(d => d != null && d.InstanceGuid != Guid.Empty)
                    .Select(d => (d.InstanceGuid, !string.IsNullOrWhiteSpace(d.DisplayName) ? d.DisplayName
                        : !string.IsNullOrWhiteSpace(d.ProductName) ? d.ProductName : d.InstanceName))
                    .Where(d => !string.IsNullOrWhiteSpace(d.Item2)).ToList();
        }

        /// <summary>The reach picked, the program's own devices and these,
        /// or null when the user canceled.</summary>
        internal static ProgramReach Pick(Window owner, string program, ProgramReach reach)
        {
            var dialog = new PickDevicesDialog(program, reach, DevicesNow()) { Owner = owner };
            if (dialog.ShowDialog() != true) return null;
            return new ProgramReach
            {
                Devices = dialog._rows.Where(r => r.Picked).Take(ProgramReach.MostDevices).Select(r => new ReachDevice(r.Id, r.Name)).ToList(),
            };
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
