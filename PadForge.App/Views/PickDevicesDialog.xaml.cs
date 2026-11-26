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

        private PickDevicesDialog(string program, ProgramReach reach, IEnumerable<(Guid Id, string Name)> devices, ISet<Guid> owned)
        {
            InitializeComponent();
            Subtitle = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Reach_Pick_Subtitle_Format, program);
            _rows = RowsFor(reach, devices, owned);
            DeviceList.ItemsSource = _rows;
            NoneText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The dialog's rows: each device listed, ticked when the
        /// reach picked it, then each device the reach kept that is not
        /// listed now and the program does not own, ticked, by the name it
        /// had. A device it owns is its own while it is connected, so a pick
        /// that kept it while it was away does not list it.</summary>
        internal static List<Row> RowsFor(ProgramReach reach, IEnumerable<(Guid Id, string Name)> devices, ISet<Guid> owned)
        {
            var rows = devices.Select(d => new Row { Id = d.Id, Name = d.Name, Picked = reach?.Picked(d.Id) == true }).ToList();
            foreach (var kept in reach?.Devices ?? Array.Empty<ReachDevice>())
                if ((owned == null || !owned.Contains(kept.Id)) && rows.All(r => r.Id != kept.Id)) rows.Add(new Row { Id = kept.Id, Name = kept.Name, Picked = true });
            return rows;
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

        /// <summary>The list Pick Devices… shows for the program whose consent
        /// key is <paramref name="key"/>: every device but the ones an open
        /// session of it added, which its reach always takes.</summary>
        internal static IReadOnlyList<(Guid Id, string Name)> DevicesFor(ProgramDevices devices, string key)
        {
            var own = devices?.OwnedByKey(key);
            var now = DevicesNow();
            return own == null || own.Count == 0 ? now : now.Where(d => !own.Contains(d.Id)).ToList();
        }

        /// <summary>The reach picked, the program's own devices and these,
        /// or null when the user canceled.</summary>
        internal static ProgramReach Pick(Window owner, ProgramDevices devices, string key, string program, ProgramReach reach)
        {
            var dialog = new PickDevicesDialog(program, reach, DevicesFor(devices, key), devices?.OwnedByKey(key)) { Owner = owner };
            if (dialog.ShowDialog() != true) return null;
            return new ProgramReach
            {
                Devices = dialog._rows.Where(r => r.Picked).Take(ProgramReach.MostDevices).Select(r => new ReachDevice(r.Id, r.Name)).ToList(),
            };
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
