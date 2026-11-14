using System;
using System.Collections.Generic;
using System.ComponentModel;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// Home's first page (#574), which shows while no virtual controller
    /// exists: the controller PadForge sees, the three common types, and a way
    /// to the other four. It takes the Welcome Tour's place.
    /// </summary>
    public partial class DashboardViewModel
    {
        // What IsFirstRun last raised, and whether the summaries are being
        // made again. RefreshActiveSlots clears the list before it fills it,
        // so the page would flash to its first page and back on every
        // change of the set.
        private bool _firstRunRaised = true;
        private bool _refreshingSlots;

        /// <summary>Whether Home shows its first page: no virtual controller
        /// exists.</summary>
        public bool IsFirstRun => SlotSummaries.Count == 0;

        private void FollowFirstRun()
        {
            if (_refreshingSlots) return;
            bool now = IsFirstRun;
            if (now == _firstRunRaised) return;
            _firstRunRaised = now;
            OnPropertyChanged(nameof(IsFirstRun));
        }

        private Guid _firstRunDeviceGuid;
        private string _firstRunDeviceName = string.Empty;
        private DeviceRowViewModel _firstRunRow;

        /// <summary>The controller the first page names, or Empty.</summary>
        public Guid FirstRunDeviceGuid => _firstRunDeviceGuid;

        public bool HasFirstRunDevice => _firstRunDeviceGuid != Guid.Empty;

        /// <summary>The controller's name, as the Devices page shows it.</summary>
        public string FirstRunDeviceName => _firstRunDeviceName;

        /// <summary>The engine is stopped, so nothing reads which devices are
        /// connected: the card says so and offers Start.</summary>
        public bool FirstRunShowsStopped => EngineStateKey == "Stopped";

        public bool FirstRunShowsDevice => !FirstRunShowsStopped && HasFirstRunDevice;

        public bool FirstRunShowsNoDevice => !FirstRunShowsStopped && !HasFirstRunDevice;

        /// <summary>The card's title: the engine's state while it is stopped,
        /// the controller's name, or that none is connected.</summary>
        public string FirstRunCardTitle => FirstRunShowsStopped ? Strings.Instance.Main_EngineStopped
            : HasFirstRunDevice ? _firstRunDeviceName
            : Strings.Instance.Home_FirstRun_NoDevice;

        /// <summary>The card's line in three parts, so the battery takes the
        /// telemetry face: what stands before the battery, the battery, and
        /// what follows it.</summary>
        public string FirstRunLineLead => FirstRunLine().Lead;

        public string FirstRunLineBattery => FirstRunLine().Battery;

        public string FirstRunLineTail => FirstRunLine().Tail;

        /// <summary>The line as it is drawn, up to the battery: Bluetooth
        /// with a comma when a battery follows, or the whole lead when there
        /// is none. The battery is then a picture of its level and the
        /// percentage. FirstRunLineText has the battery's word, for a screen
        /// reader.</summary>
        public string FirstRunLineFacts
        {
            get
            {
                var row = _firstRunRow;
                if (row == null) return string.Empty;
                if (!row.HasBattery) return FirstRunLine().Lead;
                // The language's own separator, as the whole line's format has.
                return row.IsBluetoothLink
                    ? Strings.Instance.Pad_Transport_Bluetooth + Strings.Instance.Search_WhereSeparator
                    : string.Empty;
            }
        }

        /// <summary>A picture of the battery's level, or nothing.</summary>
        public string FirstRunLineGlyph => _firstRunRow?.BatteryGlyph ?? string.Empty;

        /// <summary>The space between that picture and the percentage.</summary>
        public string FirstRunLineGap => _firstRunRow?.BatteryGap ?? string.Empty;

        /// <summary>The whole line in words.</summary>
        public string FirstRunLineText
        {
            get
            {
                var (lead, battery, tail) = FirstRunLine();
                return lead + battery + tail;
            }
        }

        public bool HasFirstRunLine
        {
            get
            {
                if (!FirstRunShowsDevice) return false;
                var (lead, battery, tail) = FirstRunLine();
                return lead.Length + battery.Length + tail.Length > 0;
            }
        }

        private (string Lead, string Battery, string Tail) FirstRunLine()
        {
            var row = _firstRunRow;
            if (row == null) return (string.Empty, string.Empty, string.Empty);
            return DeviceLine.Parts(row.IsBluetoothLink, row.HasBattery ? row.BatteryText : null,
                Strings.Instance.Home_FirstRun_DeviceLine_Format, Strings.Instance.Home_FirstRun_Battery_Format);
        }

        /// <summary>The words under the heading: "this controller" while the
        /// card names one, "your controller" while it names none.</summary>
        public string FirstRunBody => FirstRunShowsDevice ? Strings.Instance.Home_FirstRun_Body
            : Strings.Instance.Home_FirstRun_BodyNoDevice;

        private IReadOnlyList<ControllerTypeChoice> _firstRunTypes;

        /// <summary>The three tiles: Xbox, PlayStation and Keyboard +
        /// Mouse.</summary>
        public IReadOnlyList<ControllerTypeChoice> FirstRunTypes => _firstRunTypes ??= ControllerTypeChoice.FirstRunSet();

        /// <summary>The line under the tiles in three parts, so the four other
        /// types' names make a link that opens Add a Controller.</summary>
        public string MoreTypesLead => MoreTypesPart(0);

        public string MoreTypesLink => MoreTypesPart(1);

        public string MoreTypesTail => MoreTypesPart(2);

        private static string MoreTypesPart(int part)
            => BracketPart(string.Format(Strings.Instance.Home_FirstRun_MoreTypes_Format,
                Strings.Instance.ControllerType_Nintendo, Strings.Instance.ControllerType_Extended,
                Strings.Instance.ControllerType_MIDI, Strings.Instance.ControllerType_VR), part);

        /// <summary>Names the controller the first page shows, and follows its
        /// row for the battery and the link. InputService calls it on every
        /// device walk. The row may be null (a device the Devices page has not
        /// listed yet), and the line is then empty.</summary>
        public void SetFirstRunDevice(Guid instanceGuid, string deviceName, DeviceRowViewModel row)
        {
            if (instanceGuid == Guid.Empty) { ClearFirstRunDevice(); return; }
            // Every device walk names the device again. The same device on
            // the same row changes nothing: the row's own changes reach the
            // line through its subscription.
            if (instanceGuid == _firstRunDeviceGuid && ReferenceEquals(row, _firstRunRow)
                && string.Equals(deviceName ?? string.Empty, _firstRunDeviceName, StringComparison.Ordinal))
                return;
            _firstRunDeviceGuid = instanceGuid;
            _firstRunDeviceName = deviceName ?? string.Empty;
            FollowFirstRunRow(row);
            RaiseFirstRunCard();
        }

        /// <summary>The first page shows no controller.</summary>
        public void ClearFirstRunDevice()
        {
            if (!HasFirstRunDevice && _firstRunRow == null) return;
            _firstRunDeviceGuid = Guid.Empty;
            _firstRunDeviceName = string.Empty;
            FollowFirstRunRow(null);
            RaiseFirstRunCard();
        }

        private void FollowFirstRunRow(DeviceRowViewModel row)
        {
            if (ReferenceEquals(row, _firstRunRow)) return;
            if (_firstRunRow != null) _firstRunRow.PropertyChanged -= OnFirstRunRowChanged;
            _firstRunRow = row;
            if (row != null) row.PropertyChanged += OnFirstRunRowChanged;
        }

        private void OnFirstRunRowChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(DeviceRowViewModel.BatteryPercent):
                case nameof(DeviceRowViewModel.BatteryText):
                case nameof(DeviceRowViewModel.BatteryGlyph):
                case nameof(DeviceRowViewModel.HasBattery):
                case nameof(DeviceRowViewModel.DevicePath):
                case nameof(DeviceRowViewModel.VendorId):
                case nameof(DeviceRowViewModel.ProductId):
                case nameof(DeviceRowViewModel.IsBluetoothLink):
                case null:
                case "":
                    RaiseFirstRunLine();
                    break;
            }
        }

        private void RaiseFirstRunLine()
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(FirstRunLineLead)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(FirstRunLineFacts)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(FirstRunLineGlyph)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(FirstRunLineGap)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(FirstRunLineBattery)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(FirstRunLineTail)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(FirstRunLineText)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasFirstRunLine)));
        }

        /// <summary>Everything the card and the words under it read: called
        /// when the device or the engine's state changes, and with a new
        /// language.</summary>
        private void RaiseFirstRunCard()
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(FirstRunDeviceGuid)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasFirstRunDevice)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(FirstRunDeviceName)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(FirstRunShowsStopped)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(FirstRunShowsDevice)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(FirstRunShowsNoDevice)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(FirstRunCardTitle)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(FirstRunBody)));
            RaiseFirstRunLine();
        }

        /// <summary>The first page's words in the new language. Called from
        /// RefreshHomeText.</summary>
        private void RefreshFirstRunText()
        {
            _firstRunTypes = ControllerTypeChoice.FirstRunSet();
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(FirstRunTypes)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(MoreTypesLead)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(MoreTypesLink)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(MoreTypesTail)));
            RaiseFirstRunCard();
        }
    }
}
