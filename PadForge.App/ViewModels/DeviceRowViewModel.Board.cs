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
        /// <summary>The part of the connected list the row stands in, by
        /// its type, as the Show segments sort the types.</summary>
        public DeviceFilter Section => DevicesViewModel.FilterOf(DeviceTypeKey);

        /// <summary>The link in words where PadForge knows it
        /// (<see cref="Link"/>): Bluetooth, USB, PS/2 or I2C, for a keyboard
        /// and a mouse as for a controller (owner, 2026-11-17). A wireless
        /// receiver is the USB device it is. A virtual device, and one the
        /// device tree does not hold, has none.</summary>
        public string LinkText => Link switch
        {
            Common.DeviceLink.Bluetooth => Strings.Instance.Pad_Transport_Bluetooth,
            Common.DeviceLink.Usb => Strings.Instance.Pad_Transport_Usb,
            Common.DeviceLink.Ps2 => Strings.Instance.Pad_Transport_Ps2,
            Common.DeviceLink.I2c => Strings.Instance.Pad_Transport_I2c,
            _ => string.Empty,
        };

        /// <summary>The Bluetooth mark and a space for the Details row's Link
        /// line, which draws it before the word. Empty for any other link.</summary>
        public string LinkMarkText => Link == Common.DeviceLink.Bluetooth ? " " : string.Empty;

        /// <summary>Whether the row has a link to name.</summary>
        public bool HasLink => Link != Common.DeviceLink.None;

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
                    break;
                case nameof(IsOnline):
                case nameof(IsBluetoothLink):
                    // A device that comes online is read from the tree again:
                    // the answer kept for its path may be from while it was
                    // away, or from a walk that ended before it settled.
                    if (e.PropertyName == nameof(IsOnline) && IsOnline)
                        Common.DeviceTransport.ForgetLink(DevicePath);
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Link)));
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasLink)));
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(LinkText)));
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(LinkMarkText)));
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
