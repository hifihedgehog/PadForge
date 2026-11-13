using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.ViewModels
{
    /// <summary>Which of the Devices page's three lists a row stands in (#574).</summary>
    public enum DeviceListGroup { Connected, NotConnected, Other }

    /// <summary>The Devices page's Show segments (#574).</summary>
    public enum DeviceFilter { All, Controllers, KeyboardsMice, Other }

    /// <summary>
    /// The Devices page's lists (#574): the devices connected now, the ones
    /// PadForge remembers and cannot see, and the other inputs (merged rows,
    /// consumer controls and the rows PadForge and the extras feed), each
    /// through the Show segment and the search, with their counts and the
    /// names the folded lines list.
    /// </summary>
    public partial class DevicesViewModel
    {
        private DeviceFilter _filter = DeviceFilter.All;

        /// <summary>The Show segment: which kinds of device the lists show.</summary>
        public DeviceFilter Filter
        {
            get => _filter;
            set
            {
                if (!SetProperty(ref _filter, value)) return;
                RefreshCounts();
            }
        }

        private string _searchText = string.Empty;

        /// <summary>What the search box holds. It finds a device by its name,
        /// its product's name, its type in the language and its VID:PID.</summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (!SetProperty(ref _searchText, value ?? string.Empty)) return;
                RefreshCounts();
            }
        }

        /// <summary>The Connected list's view of <see cref="Devices"/>.</summary>
        public ICollectionView ConnectedView { get; private set; }

        /// <summary>The Not Connected list's view.</summary>
        public ICollectionView NotConnectedView { get; private set; }

        /// <summary>The Other Inputs list's view.</summary>
        public ICollectionView OtherInputsView { get; private set; }

        /// <summary>Made on the UI thread by the page (DevicesPage
        /// OnDataContextChanged): a ListCollectionView belongs to the thread
        /// that makes it, and view models built in tests need none.</summary>
        internal void EnsureListViews()
        {
            if (ConnectedView != null) return;
            ConnectedView = MakeView(DeviceListGroup.Connected);
            NotConnectedView = MakeView(DeviceListGroup.NotConnected);
            OtherInputsView = MakeView(DeviceListGroup.Other);
            OnPropertyChanged(nameof(ConnectedView));
            OnPropertyChanged(nameof(NotConnectedView));
            OnPropertyChanged(nameof(OtherInputsView));
        }

        /// <summary>One list's view: the rows of its group that pass the
        /// segment and the search, in the collection's order (name, then
        /// VID, PID, merged rows last). A row that goes offline or comes back
        /// moves between the lists at the next <see cref="RefreshCounts"/>,
        /// which every place that changes a row calls.
        ///
        /// <para>The views do not filter live. InputService's sync sets a
        /// row online and then moves rows to sort them, in one turn of the
        /// dispatcher. A view that filtered live took the move as the row's
        /// arrival, took the row again when it caught up with the change,
        /// and showed every connected device twice (2026-11-12). A row it
        /// took in late also stood at the list's end, out of order.</para>
        ///
        /// <para>The Connected list stands in parts, Controllers, Keyboards
        /// &amp; Mice and Other, in that order whatever part a device first
        /// turned up in, and its rows keep the collection's order within
        /// each part. A part with no row is not there.</para></summary>
        private ListCollectionView MakeView(DeviceListGroup group)
        {
            var view = new ListCollectionView(Devices)
            {
                Filter = o => o is DeviceRowViewModel d && d.ListGroup == group && Matches(d),
            };
            if (group == DeviceListGroup.Connected)
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DeviceRowViewModel.Section))
                {
                    CustomSort = SectionOrder.Instance,
                });
            return view;
        }

        /// <summary>Puts each list's view in step with the rows: a view is
        /// read again when the rows it shows are not exactly its group's
        /// rows that pass the segment and the search, in the collection's
        /// order, and for the Connected list each under its own part. A
        /// view that is already right is left alone, so its list keeps its
        /// rows and its selection.</summary>
        private void SyncViews()
        {
            SyncView(ConnectedView, DeviceListGroup.Connected);
            SyncView(NotConnectedView, DeviceListGroup.NotConnected);
            SyncView(OtherInputsView, DeviceListGroup.Other);
        }

        private void SyncView(ICollectionView view, DeviceListGroup group)
        {
            if (view == null) return;
            if (!IsInStep(view, group)) view.Refresh();
        }

        /// <summary>The rows a list should show, in the order it shows
        /// them: the collection's, and for a list in parts each part's rows
        /// together, the parts in the Show segments' order.</summary>
        internal IEnumerable<DeviceRowViewModel> RowsOf(DeviceListGroup group, bool inParts)
        {
            if (!inParts)
            {
                foreach (var d in Devices)
                    if (d != null && d.ListGroup == group && Matches(d)) yield return d;
                yield break;
            }
            foreach (DeviceFilter part in new[] { DeviceFilter.Controllers, DeviceFilter.KeyboardsMice, DeviceFilter.Other })
                foreach (var d in Devices)
                    if (d != null && d.ListGroup == group && d.Section == part && Matches(d)) yield return d;
        }

        private bool IsInStep(ICollectionView view, DeviceListGroup group)
        {
            var parts = view.Groups;
            if (parts == null)
            {
                var shown = view.GetEnumerator();
                try
                {
                    foreach (var d in RowsOf(group, inParts: false))
                        if (!shown.MoveNext() || !ReferenceEquals(shown.Current, d)) return false;
                    return !shown.MoveNext();
                }
                finally
                {
                    (shown as IDisposable)?.Dispose();
                }
            }

            // A list in parts: each row under the part its type names now,
            // the parts in order and none of them empty.
            var want = RowsOf(group, inParts: true).GetEnumerator();
            int last = -1;
            foreach (object item in parts)
            {
                if (item is not CollectionViewGroup { Name: DeviceFilter name } part || part.ItemCount == 0) return false;
                if ((int)name <= last) return false;
                last = (int)name;
                foreach (object row in part.Items)
                    if (!want.MoveNext() || !ReferenceEquals(want.Current, row) || want.Current.Section != name) return false;
            }
            return !want.MoveNext();
        }

        /// <summary>The segment a type falls in. Controllers are the game
        /// controllers, a Falcon and a VR controller. Keyboards &amp; Mice
        /// are keyboards, analog keyboards, mice and touchpads. Every other
        /// type is Other.</summary>
        internal static DeviceFilter FilterOf(string typeKey) => typeKey switch
        {
            "Gamepad" or "Joystick" or "Wheel" or "FlightStick" or "FirstPerson" or "Supplemental"
                or "NovintFalcon" or "VrController" => DeviceFilter.Controllers,
            "Keyboard" or "AnalogKeyboard" or "Mouse" or "Touchpad" => DeviceFilter.KeyboardsMice,
            _ => DeviceFilter.Other,
        };

        /// <summary>Whether a row passes the segment and the search. The
        /// search reads the name, the product's name, the type in the
        /// language and VID:PID, ignoring case in the current
        /// culture.</summary>
        internal bool Matches(DeviceRowViewModel d)
        {
            if (d == null) return false;
            if (_filter != DeviceFilter.All && FilterOf(d.DeviceTypeKey) != _filter) return false;
            string query = _searchText.Trim();
            if (query.Length == 0) return true;
            var compare = CultureInfo.CurrentCulture.CompareInfo;
            bool Has(string text) => !string.IsNullOrEmpty(text) && compare.IndexOf(text, query, CompareOptions.IgnoreCase) >= 0;
            return Has(d.DeviceName) || Has(d.ProductName) || Has(d.DeviceType)
                || (d.HasVidPid && Has(d.VendorIdHex + ":" + d.ProductIdHex));
        }

        private int _connectedTotal;

        /// <summary>The devices connected now, whatever the segment and the
        /// search: the "N connected" under the page's title.</summary>
        public int ConnectedTotal
        {
            get => _connectedTotal;
            private set => SetProperty(ref _connectedTotal, value);
        }

        private int _connectedShown;

        /// <summary>The Connected list's count, after the segment and the search.</summary>
        public int ConnectedShown
        {
            get => _connectedShown;
            private set => SetProperty(ref _connectedShown, value);
        }

        private int _notConnectedCount;

        /// <summary>The Not Connected line's count.</summary>
        public int NotConnectedCount
        {
            get => _notConnectedCount;
            private set
            {
                if (SetProperty(ref _notConnectedCount, value))
                    OnPropertyChanged(nameof(HasNotConnected));
            }
        }

        /// <summary>Whether the Not Connected line shows: it has a row to open.</summary>
        public bool HasNotConnected => _notConnectedCount > 0;

        private int _otherInputsCount;

        /// <summary>The Other Inputs line's count.</summary>
        public int OtherInputsCount
        {
            get => _otherInputsCount;
            private set
            {
                if (SetProperty(ref _otherInputsCount, value))
                    OnPropertyChanged(nameof(HasOtherInputs));
            }
        }

        /// <summary>Whether the Other Inputs line shows.</summary>
        public bool HasOtherInputs => _otherInputsCount > 0;

        private string _notConnectedHint = string.Empty;

        /// <summary>The Not Connected line's hint: the names it holds.</summary>
        public string NotConnectedHint
        {
            get => _notConnectedHint;
            private set => SetProperty(ref _notConnectedHint, value);
        }

        private string _otherInputsHint = string.Empty;

        /// <summary>The Other Inputs line's hint: the names it holds.</summary>
        public string OtherInputsHint
        {
            get => _otherInputsHint;
            private set => SetProperty(ref _otherInputsHint, value);
        }

        private bool _showNoMatch;

        /// <summary>Whether the list says no device matches: the segment or
        /// the search leaves no row in any of the three lists.</summary>
        public bool ShowNoMatch
        {
            get => _showNoMatch;
            private set => SetProperty(ref _showNoMatch, value);
        }

        private bool _canAddController = true;

        /// <summary>Whether a controller can be made for a device (Use It,
        /// New Controller): a slot is free. Set by RefreshSlotButtons.</summary>
        public bool CanAddController
        {
            get => _canAddController;
            private set => SetProperty(ref _canAddController, value);
        }

        /// <summary>The counts and the folded lines' names, from
        /// <see cref="Devices"/>, and the three lists' views put in step
        /// with the rows. It needs no view, so a test needs none. It runs
        /// where the rows change (SyncDevicesList, the engine's stop, Remove
        /// Device) and when the segment or the search does.</summary>
        public void RefreshCounts()
        {
            int total = 0, shown = 0, notConnected = 0, other = 0;
            var notConnectedNames = new List<string>();
            var otherNames = new List<string>();
            foreach (var d in Devices)
            {
                if (d == null) continue;
                var group = d.ListGroup;
                if (group == DeviceListGroup.Connected) total++;
                if (!Matches(d)) continue;
                switch (group)
                {
                    case DeviceListGroup.Connected: shown++; break;
                    case DeviceListGroup.NotConnected: notConnected++; notConnectedNames.Add(d.DeviceName); break;
                    default: other++; otherNames.Add(d.DeviceName); break;
                }
            }
            ConnectedTotal = total;
            ConnectedShown = shown;
            NotConnectedCount = notConnected;
            OtherInputsCount = other;
            NotConnectedHint = string.Join(", ", notConnectedNames);
            OtherInputsHint = string.Join(", ", otherNames);
            ShowNoMatch = shown + notConnected + other == 0
                && (_filter != DeviceFilter.All || !string.IsNullOrWhiteSpace(_searchText));
            RefreshColumns();
            SyncViews();
        }
    }
}
