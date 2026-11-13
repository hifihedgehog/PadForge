using System.ComponentModel;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// What the Devices page's table and panel read of a row since the
    /// world-class pass (#574): the part of the connected list it stands
    /// in, its link in words, whether its battery is low, and whether it is
    /// sending input.
    /// </summary>
    public partial class DeviceRowViewModel
    {
        /// <summary>The part of the connected list the row stands in:
        /// Controllers, Keyboards &amp; Mice or Other, by its type, as the
        /// Show segments sort the types.</summary>
        public DeviceFilter Section => DevicesViewModel.FilterOf(DeviceTypeKey);

        /// <summary>Whether the row is a controller's: a taller row, with
        /// its type under its name.</summary>
        public bool IsControllerRow => Section == DeviceFilter.Controllers;

        /// <summary>The link in words where it is a known fact, which is
        /// Bluetooth alone (Common.DeviceTransport): a path with no mark of
        /// its transport may be USB, a wireless receiver or a virtual
        /// source, so nothing is said of it.</summary>
        public string LinkText => IsBluetoothLink ? Strings.Instance.Pad_Transport_Bluetooth : string.Empty;

        /// <summary>Whether the row has a link to name.</summary>
        public bool HasLink => IsBluetoothLink;

        /// <summary>Whether the panel has a Link or a Battery tile to show.</summary>
        public bool HasLinkOrBattery => HasLink || HasBattery;

        private int _batteryLowAt = 15;

        /// <summary>The level Settings names for a low battery. The battery
        /// walk hands it to every row with each reading.</summary>
        public int BatteryLowAt
        {
            get => _batteryLowAt;
            set
            {
                if (_batteryLowAt == value) return;
                bool was = BatteryLow;
                _batteryLowAt = value;
                if (was != BatteryLow) RaiseBatteryWords();
            }
        }

        /// <summary>Whether the charge is at or under that level, and not
        /// charging.</summary>
        public bool BatteryLow => BatteryPercent >= 0 && !BatteryCharging && BatteryPercent <= _batteryLowAt;

        /// <summary>The battery for a screen reader: its level, and that it
        /// is low or charging when it is.</summary>
        public string BatterySpoken => BatteryPercent < 0 ? string.Empty
            : string.Format(BatteryLow ? Strings.Instance.Home_Battery_Low_Format
                : BatteryCharging ? Strings.Instance.Home_Battery_Charging_Format
                : Strings.Instance.Home_FirstRun_Battery_Format, BatteryText);

        private void RaiseBatteryWords()
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(BatteryLow)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(BatterySpoken)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasLinkOrBattery)));
        }

        private bool _isSending;

        /// <summary>Whether the device is sending input now: the light on
        /// its row's icon (Common.Input.InputActivity).</summary>
        public bool IsSending
        {
            get => _isSending;
            set => SetProperty(ref _isSending, value);
        }

        /// <summary>What follows from a value that changed: the section
        /// from the type, the link's words from the path and the ids, and
        /// the battery's words from its reading.</summary>
        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            switch (e.PropertyName)
            {
                case nameof(DeviceTypeKey):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Section)));
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsControllerRow)));
                    break;
                case nameof(IsBluetoothLink):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasLink)));
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(LinkText)));
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasLinkOrBattery)));
                    break;
                case nameof(BatteryPercent):
                case nameof(BatteryCharging):
                    RaiseBatteryWords();
                    break;
            }
        }

        /// <summary>The language changed: the words made of it read again.</summary>
        private void RaiseBoardWords()
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(LinkText)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(BatterySpoken)));
        }
    }
}
