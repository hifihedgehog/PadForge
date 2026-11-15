using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// The mappings list's columns. Every device that drives the controller
    /// has a column, named once in the list's head, in the order of the
    /// Driven By line. A row holds a cell under each, with a tag for each
    /// input of that device that drives the row's output.
    ///
    /// <para>Sources that name no device stand under a column of their own,
    /// first. A source on a device the controller no longer has keeps a
    /// column, dimmed, after the devices.</para>
    ///
    /// <para>While the list has no room for every column at its least
    /// width, the devices that are not connected fold into one column that
    /// counts their inputs in each row. A click on it opens them again. If
    /// the columns still do not fit, the cells scroll sideways and the
    /// output's name and the row's end stay where they are.</para>
    /// </summary>
    public partial class PadViewModel
    {
        /// <summary>The list's columns, in order. The fold is the last and
        /// is always there, shown only while it holds devices.</summary>
        public ObservableCollection<ListColumn> ListColumns { get; } = new();

        private bool _listDirty = true;
        private bool _listFlushPosted;
        private bool _flushingList;
        private readonly HashSet<MappedDeviceInfo> _listDevicesHooked = new();

        /// <summary>Follows the rows and the devices from the start, before
        /// the first rows are built.</summary>
        private void InitList()
        {
            Mappings.CollectionChanged += OnMappingsChangedForList;
            MappedDevices.CollectionChanged += OnMappedDevicesChangedForList;
        }

        private void OnMappingsChangedForList(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (MappingItem row in e.OldItems)
                    if (ReferenceEquals(row.Owner, this)) row.Owner = null;
            if (e.Action == NotifyCollectionChangedAction.Reset)
                foreach (var row in Mappings) row.Owner = this;
            else if (e.NewItems != null)
                foreach (MappingItem row in e.NewItems) row.Owner = this;
            MarkListDirty();
        }

        private void OnMappedDevicesChangedForList(object sender, NotifyCollectionChangedEventArgs e)
        {
            var now = new HashSet<MappedDeviceInfo>(MappedDevices);
            foreach (var device in new List<MappedDeviceInfo>(_listDevicesHooked))
            {
                if (now.Contains(device)) continue;
                device.PropertyChanged -= OnMappedDeviceChangedForList;
                _listDevicesHooked.Remove(device);
            }
            foreach (var device in now)
            {
                if (!_listDevicesHooked.Add(device)) continue;
                device.PropertyChanged += OnMappedDeviceChangedForList;
            }
            MarkListDirty();
        }

        private void OnMappedDeviceChangedForList(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MappedDeviceInfo.Name) or nameof(MappedDeviceInfo.InstanceGuid)
                or nameof(MappedDeviceInfo.IsOnline) or nameof(MappedDeviceInfo.TypeGlyph)
                or nameof(MappedDeviceInfo.TypeName))
                MarkListDirty();
        }

        /// <summary>The columns or a row's cells may be others now. The
        /// list is built again once, before the next frame, however many
        /// changes ask for it: a load changes every row many times.
        ///
        /// <para>The build is posted only from the app's own thread, which
        /// is where the program changes rows and devices. A change made on
        /// any other thread leaves the list to its next reader
        /// (<see cref="MappingItem.Cells"/> and <see cref="FlushList"/>
        /// build it then). Posted from there, the build would read the rows
        /// on the app's thread while the other thread still changes them.</para></summary>
        internal void MarkListDirty()
        {
            _listDirty = true;
            if (_listFlushPosted) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || !dispatcher.CheckAccess()) return;
            _listFlushPosted = true;
            dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() =>
            {
                _listFlushPosted = false;
                FlushList();
            }));
        }

        /// <summary>The assigned device a source names, or null: for no
        /// device, and for one the controller does not have.</summary>
        internal MappedDeviceInfo MappedDeviceOf(string deviceGuid)
        {
            if (string.IsNullOrEmpty(deviceGuid) || !Guid.TryParse(deviceGuid, out Guid id)) return null;
            foreach (var device in MappedDevices)
                if (device.InstanceGuid == id) return device;
            return null;
        }

        /// <summary>Whether a source's device is away: not connected, or
        /// not one of the controller's. A source that names no device is
        /// never away.</summary>
        internal static bool IsAway(PadViewModel owner, string deviceGuid)
        {
            if (owner == null || string.IsNullOrEmpty(deviceGuid)) return false;
            var device = owner.MappedDeviceOf(deviceGuid);
            return device == null || !device.IsOnline;
        }

        /// <summary>Names a row's first source again from its device's own
        /// names, after the source changed without a load.</summary>
        internal void ResolveFirstSourceName(MappingItem row)
        {
            if (row == null) return;
            Guid.TryParse(row.PrimarySourceDeviceGuid, out Guid id);
            Services.InputService.ResolveDisplayText(row, id);
        }

        /// <summary>Builds the columns and every row's cells, if anything
        /// changed since they were built.</summary>
        internal void FlushList()
        {
            if (!_listDirty || _flushingList) return;
            _flushingList = true;
            bool suppress = SuppressSettingsDirty;
            SuppressSettingsDirty = true;
            try
            {
                _listDirty = false;
                var s = Strings.Instance;
                int rows = Mappings.Count;
                var tagsOf = new List<RowTag>[rows];
                bool anyDevice = false;
                var gone = new List<(string Guid, string Name)>();
                for (int i = 0; i < rows; i++)
                {
                    tagsOf[i] = Mappings[i].BuildTags();
                    foreach (var tag in tagsOf[i])
                    {
                        if (tag.DeviceGuid.Length == 0) { anyDevice = true; continue; }
                        if (MappedDeviceOf(tag.DeviceGuid) != null) continue;
                        bool listed = false;
                        foreach (var g in gone)
                            if (string.Equals(g.Guid, tag.DeviceGuid, StringComparison.Ordinal)) { listed = true; break; }
                        if (!listed) gone.Add((tag.DeviceGuid, tag.Line?.LineDeviceLabel ?? string.Empty));
                    }
                }

                var wanted = new List<(ListColumnKind Kind, string Guid, MappedDeviceInfo Device, string Name)>();
                if (anyDevice) wanted.Add((ListColumnKind.AnyDevice, string.Empty, null, s.Mapping_AnyDevice));
                foreach (var device in MappedDevices)
                    wanted.Add((ListColumnKind.Device, device.InstanceGuid.ToString("D").ToLowerInvariant(), device, device.Name));
                foreach (var g in gone)
                    wanted.Add((ListColumnKind.Gone, g.Guid, null, g.Name.Length > 0 ? g.Name : g.Guid.Substring(0, Math.Min(8, g.Guid.Length))));
                wanted.Add((ListColumnKind.Fold, string.Empty, null, string.Empty));

                bool same = wanted.Count == ListColumns.Count;
                for (int i = 0; same && i < wanted.Count; i++)
                    same = ListColumns[i].Kind == wanted[i].Kind
                        && string.Equals(ListColumns[i].Guid, wanted[i].Guid, StringComparison.Ordinal)
                        && ReferenceEquals(ListColumns[i].Device, wanted[i].Device);
                if (!same)
                {
                    ListColumns.Clear();
                    foreach (var w in wanted) ListColumns.Add(new ListColumn(w.Kind, w.Guid, w.Device));
                    _awayOpenedByUser = false;
                }
                for (int i = 0; i < wanted.Count; i++)
                {
                    var column = ListColumns[i];
                    switch (column.Kind)
                    {
                        case ListColumnKind.Device:
                            column.Name = column.Device.Name;
                            column.TypeGlyph = column.Device.TypeGlyph;
                            column.TypeName = column.Device.TypeName;
                            column.IsAway = !column.Device.IsOnline;
                            break;
                        case ListColumnKind.Gone:
                            column.Name = wanted[i].Name;
                            column.TypeGlyph = "";
                            column.IsAway = true;
                            break;
                        case ListColumnKind.AnyDevice:
                            column.Name = wanted[i].Name;
                            break;
                    }
                }
                ApplyListFold();
                for (int i = 0; i < rows; i++) Mappings[i].SetCells(ListColumns, tagsOf[i]);
                // The sources' lines name their devices too.
                foreach (var row in Mappings)
                {
                    row.RaiseLineFacts();
                    foreach (var extra in row.ExtraSources) extra?.RaiseLineFacts();
                }
            }
            finally
            {
                SuppressSettingsDirty = suppress;
                _flushingList = false;
            }
        }

        // ─────────────────────────────────────────────
        //  The fold and the sideways scroll
        // ─────────────────────────────────────────────

        private double _listCellsWidth;
        private bool _awayOpenedByUser;

        /// <summary>The page tells the list how wide its cells' part is:
        /// what a row has between the output's name and the row's end.</summary>
        internal void SetListCellsWidth(double width)
        {
            if (double.IsNaN(width) || width < 0) width = 0;
            if (Math.Abs(width - _listCellsWidth) < 0.5) return;
            _listCellsWidth = width;
            bool suppress = SuppressSettingsDirty;
            SuppressSettingsDirty = true;
            try { ApplyListFold(); }
            finally { SuppressSettingsDirty = suppress; }
        }

        private bool _isAwayFolded;

        /// <summary>The devices that are not connected are folded into one
        /// column.</summary>
        public bool IsAwayFolded
        {
            get => _isAwayFolded;
            private set => SetProperty(ref _isAwayFolded, value);
        }

        private double _listCellsOffset;

        /// <summary>How far the cells are scrolled sideways.</summary>
        public double ListCellsOffset
        {
            get => _listCellsOffset;
            set
            {
                bool suppress = SuppressSettingsDirty;
                SuppressSettingsDirty = true;
                try { SetProperty(ref _listCellsOffset, Math.Clamp(double.IsNaN(value) ? 0 : value, 0, Math.Max(0, _listCellsScrollMax))); }
                finally { SuppressSettingsDirty = suppress; }
            }
        }

        private double _listCellsScrollMax;

        /// <summary>The furthest the cells scroll: what the columns need at
        /// their least width, less what the list has.</summary>
        public double ListCellsScrollMax
        {
            get => _listCellsScrollMax;
            private set => SetProperty(ref _listCellsScrollMax, value);
        }

        /// <summary>The room the list has for its cells, the scroll bar's
        /// page.</summary>
        public double ListCellsViewport => _listCellsWidth;

        /// <summary>The cells do not fit at their least width, so the list
        /// shows a scroll bar at its foot.</summary>
        public bool ShowListCellsScroll => _listCellsScrollMax > 0.5;

        private RelayCommand _openAwayColumnsCommand;

        /// <summary>A click on the fold's head: each device that is not
        /// connected gets its column back, until the list has room for all
        /// of them or the devices change.</summary>
        public RelayCommand OpenAwayColumnsCommand => _openAwayColumnsCommand ??= new RelayCommand(() =>
        {
            _awayOpenedByUser = true;
            bool suppress = SuppressSettingsDirty;
            SuppressSettingsDirty = true;
            try { ApplyListFold(); }
            finally { SuppressSettingsDirty = suppress; }
        });

        /// <summary>Whether the columns need folding, given how many there
        /// are and the room: more of them than fit at their least width.</summary>
        internal static bool ColumnsAreTight(int columns, double width)
            => width > 0 && columns * ListColumn.MinWidth > width + 0.5;

        /// <summary>Folds or opens the away devices' columns for the room
        /// the list has, and sizes the sideways scroll.</summary>
        private void ApplyListFold()
        {
            int all = 0, away = 0;
            ListColumn fold = null;
            foreach (var column in ListColumns)
            {
                if (column.IsFold) { fold = column; continue; }
                all++;
                if (column.IsAway) away++;
            }
            bool tight = ColumnsAreTight(all, _listCellsWidth);
            if (!tight || away == 0) _awayOpenedByUser = false;
            bool folded = tight && away > 0 && !_awayOpenedByUser;
            foreach (var column in ListColumns)
                column.IsShown = column.IsFold ? folded : !(folded && column.IsAway);
            if (fold != null) fold.Name = string.Format(Strings.Instance.Pad_List_NotConnected_Format, away);
            IsAwayFolded = folded;

            int shown = all - (folded ? away : 0);
            double needs = shown * ListColumn.MinWidth + (folded ? ListColumn.FoldWidth : 0);
            double max = _listCellsWidth > 0 ? Math.Max(0, needs - _listCellsWidth) : 0;
            if (max < 0.5) max = 0;
            bool showed = ShowListCellsScroll;
            ListCellsScrollMax = max;
            OnPropertyChanged(nameof(ListCellsViewport));
            if (showed != ShowListCellsScroll) OnPropertyChanged(nameof(ShowListCellsScroll));
            if (_listCellsOffset > max) ListCellsOffset = max;
        }

        /// <summary>Whether an open row shows its live value: in the list
        /// always, and under the picture where the bar does
        /// (<see cref="ShowPickedValue"/>).</summary>
        public bool ShowRowValue => !IsButtonsPictureView || ShowPickedValue;
    }
}
