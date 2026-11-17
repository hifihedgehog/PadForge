using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.ViewModels
{
    /// <summary>Which of the Devices page's three lists a row stands in (#574).</summary>
    public enum DeviceListGroup { Connected, NotConnected, Other }

    /// <summary>The Devices page's Show segments, which are also the parts
    /// of its connected list, in this order (#574): the kinds the page
    /// sorted devices by before PadForge 6. That page had a chip for each,
    /// PadForge 6 first cut them to Controllers, Keyboards &amp; Mice and
    /// Other, and people with a wheel beside a flight stick and three
    /// keyboards lost the way to one kind (owner, 2026-11-17).</summary>
    public enum DeviceFilter { All, Gamepad, Joystick, Wheel, Keyboard, Mouse, Other }

    /// <summary>The Devices page's connection segments: every device, the
    /// ones connected now, or the ones PadForge remembers and cannot see.
    /// The main list shows the devices the segment picks (owner,
    /// 2026-11-17).</summary>
    public enum DeviceConnection { All, Connected, NotConnected }

    /// <summary>The Devices page's three lists: the main list, in parts by
    /// kind, the Other Inputs fold and the Not Connected fold.</summary>
    public enum DevicePageList { Main, OtherInputs, NotConnected }

    /// <summary>
    /// The Devices page's lists (#574). The main list shows the devices
    /// connected now, or under the Not Connected segment the ones PadForge
    /// remembers and cannot see. The Not Connected fold holds those under
    /// the other two segments, and the Other Inputs fold holds the consumer
    /// controls and the rows PadForge and the extras feed. Each list passes
    /// the Show segment, the connection segment and the search, with its
    /// count and the names a folded line lists. A merged row stands where a
    /// device of its type stands.
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

        private DeviceConnection _connection = DeviceConnection.All;

        /// <summary>The connection segment: whether the lists show every
        /// device, the connected ones, or the ones that are not.</summary>
        public DeviceConnection Connection
        {
            get => _connection;
            set
            {
                if (!SetProperty(ref _connection, value)) return;
                RefreshCounts();
            }
        }

        /// <summary>The rows the main list holds: the connected ones, or
        /// under the Not Connected segment the ones that are not.</summary>
        internal DeviceListGroup MainGroup => _connection == DeviceConnection.NotConnected
            ? DeviceListGroup.NotConnected : DeviceListGroup.Connected;

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

        /// <summary>The main list's view of <see cref="Devices"/>, in parts
        /// by kind.</summary>
        public ICollectionView MainView { get; private set; }

        /// <summary>The Not Connected list's view.</summary>
        public ICollectionView NotConnectedView { get; private set; }

        /// <summary>The Other Inputs list's view.</summary>
        public ICollectionView OtherInputsView { get; private set; }

        /// <summary>Made on the UI thread by the page (DevicesPage
        /// OnDataContextChanged): a ListCollectionView belongs to the thread
        /// that makes it, and view models built in tests need none.</summary>
        internal void EnsureListViews()
        {
            if (MainView != null) return;
            MainView = MakeView(DevicePageList.Main);
            NotConnectedView = MakeView(DevicePageList.NotConnected);
            OtherInputsView = MakeView(DevicePageList.OtherInputs);
            OnPropertyChanged(nameof(MainView));
            OnPropertyChanged(nameof(NotConnectedView));
            OnPropertyChanged(nameof(OtherInputsView));
        }

        /// <summary>One list's view: the rows it shows
        /// (<see cref="Shows"/>), in the collection's order (name, then
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
        /// <para>The main list stands in parts, one for each kind a
        /// Show segment names, in the segments' order whatever part a device
        /// first turned up in, and its rows keep the collection's order
        /// within each part, so a kind's merged row is its part's last. A
        /// part with no row is not there.</para></summary>
        private ListCollectionView MakeView(DevicePageList list)
        {
            var view = new ListCollectionView(Devices)
            {
                Filter = o => o is DeviceRowViewModel d && Shows(list, d),
            };
            if (list == DevicePageList.Main)
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DeviceRowViewModel.Section))
                {
                    CustomSort = SectionOrder.Instance,
                });
            return view;
        }

        /// <summary>Puts each list's view in step with the rows: a view is
        /// read again when the rows it shows are not exactly the rows it
        /// should show (<see cref="Shows"/>), in the collection's order, and
        /// for the main list each under its own part. A view that is
        /// already right is left alone, so its list keeps its rows and its
        /// selection.</summary>
        private void SyncViews()
        {
            SyncView(MainView, DevicePageList.Main);
            SyncView(NotConnectedView, DevicePageList.NotConnected);
            SyncView(OtherInputsView, DevicePageList.OtherInputs);
        }

        private void SyncView(ICollectionView view, DevicePageList list)
        {
            if (view == null) return;
            if (!IsInStep(view, list)) view.Refresh();
        }

        /// <summary>Whether a list shows a row: the row passes the segments
        /// and the search and stands in that list. The main list holds the
        /// rows of <see cref="MainGroup"/>. The Not Connected fold holds the
        /// rows that are not connected while the main list holds the
        /// connected ones, and the Other Inputs fold holds the other
        /// inputs.</summary>
        internal bool Shows(DevicePageList list, DeviceRowViewModel d)
        {
            if (d == null || !Matches(d)) return false;
            return list switch
            {
                DevicePageList.Main => d.ListGroup == MainGroup,
                DevicePageList.NotConnected => d.ListGroup == DeviceListGroup.NotConnected
                    && MainGroup == DeviceListGroup.Connected,
                _ => d.ListGroup == DeviceListGroup.Other,
            };
        }

        /// <summary>The rows a list should show, in the order it shows
        /// them: the collection's, and for a list in parts each part's rows
        /// together, the parts in the Show segments' order.</summary>
        internal IEnumerable<DeviceRowViewModel> RowsOf(DevicePageList list, bool inParts)
        {
            if (!inParts)
            {
                foreach (var d in Devices)
                    if (Shows(list, d)) yield return d;
                yield break;
            }
            foreach (DeviceFilter part in Parts)
                foreach (var d in Devices)
                    if (d != null && d.Section == part && Shows(list, d)) yield return d;
        }

        private bool IsInStep(ICollectionView view, DevicePageList list)
        {
            var parts = view.Groups;
            if (parts == null)
            {
                var shown = view.GetEnumerator();
                try
                {
                    foreach (var d in RowsOf(list, inParts: false))
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
            var want = RowsOf(list, inParts: true).GetEnumerator();
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

        /// <summary>The connected list's parts, in order: every segment
        /// but All.</summary>
        internal static readonly DeviceFilter[] Parts =
        {
            DeviceFilter.Gamepad, DeviceFilter.Joystick, DeviceFilter.Wheel,
            DeviceFilter.Keyboard, DeviceFilter.Mouse, DeviceFilter.Other,
        };

        /// <summary>The segment a type falls in, as the page's chips sorted
        /// the types before PadForge 6 (5.3.1's DevicesViewModel.FacetOf).
        /// Gamepads are the gamepad-shaped classes. Joysticks take flight
        /// sticks with them. A wheel, a keyboard and a mouse each have their
        /// own. Every other type is Other: a touchpad, a tablet, a MIDI
        /// device, a Falcon, a VR controller and the rest. One change from
        /// 5.3.1: an analog keyboard is a keyboard here, where the chips put
        /// it under Other.</summary>
        internal static DeviceFilter FilterOf(string typeKey) => typeKey switch
        {
            "Gamepad" or "FirstPerson" or "Supplemental" => DeviceFilter.Gamepad,
            "Joystick" or "FlightStick" => DeviceFilter.Joystick,
            "Wheel" => DeviceFilter.Wheel,
            "Keyboard" or "AnalogKeyboard" => DeviceFilter.Keyboard,
            "Mouse" => DeviceFilter.Mouse,
            _ => DeviceFilter.Other,
        };

        /// <summary>Whether a row passes the Show segment, the connection
        /// segment and the search. The search reads the name, the product's
        /// name, the type in the language and VID:PID, ignoring case in the
        /// current culture.</summary>
        internal bool Matches(DeviceRowViewModel d)
        {
            if (d == null) return false;
            if (_filter != DeviceFilter.All && FilterOf(d.DeviceTypeKey) != _filter) return false;
            if (_connection == DeviceConnection.Connected && !d.IsOnline) return false;
            if (_connection == DeviceConnection.NotConnected && d.IsOnline) return false;
            string query = _searchText.Trim();
            if (query.Length == 0) return true;
            var compare = CultureInfo.CurrentCulture.CompareInfo;
            bool Has(string text) => !string.IsNullOrEmpty(text) && compare.IndexOf(text, query, CompareOptions.IgnoreCase) >= 0;
            return Has(d.DeviceName) || Has(d.ProductName) || Has(d.DeviceType)
                || (d.HasVidPid && Has(d.VendorIdHex + ":" + d.ProductIdHex));
        }

        private int _connectedTotal;

        /// <summary>The devices connected now, whatever the segment and the
        /// search: the "N connected" under the page's title. A merged row
        /// stands in the connected list and is no device, so it is not
        /// counted.</summary>
        public int ConnectedTotal
        {
            get => _connectedTotal;
            private set => SetProperty(ref _connectedTotal, value);
        }

        private int _mainShown;

        /// <summary>The main list's count, after the segments and the search.</summary>
        public int MainShown
        {
            get => _mainShown;
            private set => SetProperty(ref _mainShown, value);
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

        /// <summary>Whether the list says no device matches: a segment or
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
        /// Device) and when a segment or the search does.</summary>
        public void RefreshCounts()
        {
            int total = 0, shown = 0, notConnected = 0, other = 0;
            var notConnectedNames = new List<string>();
            var otherNames = new List<string>();
            foreach (var d in Devices)
            {
                if (d == null) continue;
                if (d.ListGroup == DeviceListGroup.Connected && !d.IsAggregate) total++;
                if (Shows(DevicePageList.Main, d)) shown++;
                else if (Shows(DevicePageList.NotConnected, d)) { notConnected++; notConnectedNames.Add(d.DeviceName); }
                else if (Shows(DevicePageList.OtherInputs, d)) { other++; otherNames.Add(d.DeviceName); }
            }
            ConnectedTotal = total;
            MainShown = shown;
            NotConnectedCount = notConnected;
            OtherInputsCount = other;
            NotConnectedHint = string.Join(", ", notConnectedNames);
            OtherInputsHint = string.Join(", ", otherNames);
            ShowNoMatch = shown + notConnected + other == 0
                && (_filter != DeviceFilter.All || _connection != DeviceConnection.All
                    || !string.IsNullOrWhiteSpace(_searchText));
            RefreshColumns();
            SyncViews();
        }
    }
}
