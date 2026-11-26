using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PadForge.ViewModels
{
    /// <summary>
    /// The Extras catalog's switches for the four extras that have several
    /// switches and no single one (#574): Head Tracking's three inputs, the
    /// three overlays, the two device notes (#600) and the controllers on or
    /// off note (#600 item 42), the Screen Overlay list and the crosshair
    /// list (#585).
    ///
    /// <para>Each catalog switch reads on while any of its own is on. Turning
    /// it off turns them all off and remembers which were on. Turning it on
    /// brings those back, or the starting set when it has never turned any
    /// off. The remembered sets are saved, so they hold across a restart.
    /// Only the catalog switch writes them: a profile or a settings load
    /// sets the real switches one after another, and a set remembered from
    /// the steps in between would be a set nobody chose.</para>
    /// </summary>
    public partial class DashboardViewModel
    {
        /// <summary>Head Tracking's inputs as bits: UDP, FreeTrack, OpenXR.</summary>
        internal const int HeadTrackingUdpBit = 1, HeadTrackingFreeTrackBit = 2, HeadTrackingOpenXrBit = 4;

        /// <summary>What the Head Tracking switch turns on the first time: the
        /// UDP listener and the FreeTrack reader, the pair one switch turned
        /// on before the inputs had switches of their own. The headset input
        /// opens an OpenXR session, so it waits to be chosen.</summary>
        internal const int HeadTrackingStartingSources = HeadTrackingUdpBit | HeadTrackingFreeTrackBit;

        /// <summary>The overlays as bits: menu, shift layer, profile switch,
        /// the two device notes (#600) and the controllers on or off note
        /// (#600 item 42).</summary>
        internal const int MenuOverlayBit = 1, ShiftLayerFlyoutBit = 2, ProfileOverlayBit = 4,
            DeviceDisconnectedNoteBit = 8, DeviceConnectedNoteBit = 16, ControllersNoteBit = 32;

        /// <summary>What the Overlays switch turns on the first time: the
        /// four a new settings file starts with. The device notes ship off
        /// and wait to be chosen, so they are not in it.</summary>
        internal const int OverlaysStartingSet = MenuOverlayBit | ShiftLayerFlyoutBit | ProfileOverlayBit | ControllersNoteBit;

        /// <summary>Every row the Overlays switch can turn off and bring
        /// back: the starting four and the two device notes.</summary>
        internal const int OverlaysAllBits = OverlaysStartingSet | DeviceDisconnectedNoteBit | DeviceConnectedNoteBit;

        // ─────────────────────────────────────────────
        //  Head Tracking
        // ─────────────────────────────────────────────

        private int HeadTrackingSourceMask =>
            (_headTrackingEnabled ? HeadTrackingUdpBit : 0)
            | (_headTrackingFreeTrack ? HeadTrackingFreeTrackBit : 0)
            | (_headTrackingOpenXr ? HeadTrackingOpenXrBit : 0);

        private int _headTrackingLastSources;

        /// <summary>The inputs the Head Tracking switch turned off last, as
        /// bits. Zero until it has turned any off.</summary>
        public int HeadTrackingLastSources
        {
            get => _headTrackingLastSources;
            set => SetProperty(ref _headTrackingLastSources,
                value & (HeadTrackingUdpBit | HeadTrackingFreeTrackBit | HeadTrackingOpenXrBit));
        }

        /// <summary>The Extras switch of Head Tracking: on while any of the
        /// three inputs is on.</summary>
        public bool HeadTrackingAnyEnabled
        {
            get => HeadTrackingSourceMask != 0;
            set
            {
                int now = HeadTrackingSourceMask;
                if (value == (now != 0)) return;
                if (!value) HeadTrackingLastSources = now;
                int mask = !value ? 0
                    : _headTrackingLastSources != 0 ? _headTrackingLastSources : HeadTrackingStartingSources;
                HeadTrackingEnabled = (mask & HeadTrackingUdpBit) != 0;
                HeadTrackingFreeTrack = (mask & HeadTrackingFreeTrackBit) != 0;
                HeadTrackingOpenXr = (mask & HeadTrackingOpenXrBit) != 0;
            }
        }

        /// <summary>Whether turning the Head Tracking switch on now would
        /// start the UDP input, the one that listens on the network (#600).
        /// The switch is off and the inputs it brings back hold UDP. With
        /// only OpenXR among them, it opens the headset's session and listens
        /// on nothing.</summary>
        internal bool HeadTrackingSwitchStartsUdp
            => HeadTrackingSourceMask == 0
               && ((_headTrackingLastSources != 0 ? _headTrackingLastSources : HeadTrackingStartingSources) & HeadTrackingUdpBit) != 0;

        private RelayCommand _resetHeadTrackingSourcesCommand;

        /// <summary>The reset beside the Head Tracking switch: each input back
        /// to its default, which is off.</summary>
        public RelayCommand ResetHeadTrackingSourcesCommand =>
            _resetHeadTrackingSourcesCommand ??= new RelayCommand(() =>
            {
                ResetHeadTrackingEnabledCommand.Execute(null);
                ResetHeadTrackingFreeTrackCommand.Execute(null);
                ResetHeadTrackingOpenXrCommand.Execute(null);
            });

        private RelayCommand _resetHeadTrackingRangesCommand;

        /// <summary>The Range card's Reset All (#574): the rotation and
        /// translation ranges back to their defaults. The per-axis ranges
        /// under More keep their own resets.</summary>
        public RelayCommand ResetHeadTrackingRangesCommand =>
            _resetHeadTrackingRangesCommand ??= new RelayCommand(() =>
            {
                ResetHeadTrackingRotationRangeCommand.Execute(null);
                ResetHeadTrackingTranslationRangeCommand.Execute(null);
            });

        // ─────────────────────────────────────────────
        //  Overlays
        // ─────────────────────────────────────────────

        private int OverlayMask =>
            (_enableMenuOverlay ? MenuOverlayBit : 0)
            | (_enableShiftLayerFlyout ? ShiftLayerFlyoutBit : 0)
            | (_enableProfileOverlay ? ProfileOverlayBit : 0)
            | (_enableDeviceDisconnectedNote ? DeviceDisconnectedNoteBit : 0)
            | (_enableDeviceConnectedNote ? DeviceConnectedNoteBit : 0)
            | (_enableControllersNote ? ControllersNoteBit : 0);

        private int _overlaysLastShown;

        /// <summary>The overlays the Overlays switch turned off last, as bits.
        /// Zero until it has turned any off.</summary>
        public int OverlaysLastShown
        {
            get => _overlaysLastShown;
            set => SetProperty(ref _overlaysLastShown, value & OverlaysAllBits);
        }

        /// <summary>The Extras switch of Overlays: on while any of the six
        /// rows is on.</summary>
        public bool OverlaysAnyEnabled
        {
            get => OverlayMask != 0;
            set
            {
                int now = OverlayMask;
                if (value == (now != 0)) return;
                if (!value) OverlaysLastShown = now;
                int mask = !value ? 0 : _overlaysLastShown != 0 ? _overlaysLastShown : OverlaysStartingSet;
                EnableMenuOverlay = (mask & MenuOverlayBit) != 0;
                EnableShiftLayerFlyout = (mask & ShiftLayerFlyoutBit) != 0;
                EnableProfileOverlay = (mask & ProfileOverlayBit) != 0;
                EnableDeviceDisconnectedNote = (mask & DeviceDisconnectedNoteBit) != 0;
                EnableDeviceConnectedNote = (mask & DeviceConnectedNoteBit) != 0;
                EnableControllersNote = (mask & ControllersNoteBit) != 0;
            }
        }

        private RelayCommand _resetOverlaysCommand;

        /// <summary>The reset beside the Overlays switch: each row back to
        /// its default, which is on for the three overlays and the
        /// controllers on or off note and off for the two device
        /// notes.</summary>
        public RelayCommand ResetOverlaysCommand =>
            _resetOverlaysCommand ??= new RelayCommand(() =>
            {
                ResetSettingCommand.Execute(nameof(EnableMenuOverlay));
                ResetSettingCommand.Execute(nameof(EnableShiftLayerFlyout));
                ResetSettingCommand.Execute(nameof(EnableProfileOverlay));
                ResetSettingCommand.Execute(nameof(EnableDeviceDisconnectedNote));
                ResetSettingCommand.Execute(nameof(EnableDeviceConnectedNote));
                ResetSettingCommand.Execute(nameof(EnableControllersNote));
            });

        // ─────────────────────────────────────────────
        //  Screen Overlay
        // ─────────────────────────────────────────────

        private int ScreenOverlayMask
        {
            get
            {
                int mask = 0;
                foreach (var item in ScreenOverlays)
                    if (item.Enabled) mask |= 1 << (item.Number - 1);
                return mask;
            }
        }

        private int _screenOverlaysLastOn;

        /// <summary>The overlays the Screen Overlay switch turned off last, a
        /// bit for each overlay's number. Zero until it has turned any off.</summary>
        public int ScreenOverlaysLastOn
        {
            get => _screenOverlaysLastOn;
            set => SetProperty(ref _screenOverlaysLastOn,
                value & ((1 << PadForge.Common.Input.KeyboardScreens.KeyboardScreenInfo.MaxOverlays) - 1));
        }

        /// <summary>The Extras switch of Screen Overlay: on while any overlay
        /// on the list is on. Turned on with none remembered, or with every
        /// remembered overlay since removed, it turns on the first.</summary>
        public bool ScreenOverlaysAnyEnabled
        {
            get => ScreenOverlayMask != 0;
            set
            {
                int now = ScreenOverlayMask;
                if (value == (now != 0)) return;
                if (!value) ScreenOverlaysLastOn = now;
                int mask = 0;
                if (value)
                {
                    foreach (var item in ScreenOverlays)
                        mask |= _screenOverlaysLastOn & (1 << (item.Number - 1));
                    if (mask == 0 && ScreenOverlays.Count > 0) mask = 1 << (ScreenOverlays[0].Number - 1);
                }
                foreach (var item in ScreenOverlays)
                    item.Enabled = (mask & (1 << (item.Number - 1))) != 0;
            }
        }

        // ─────────────────────────────────────────────
        //  Crosshair (#585)
        // ─────────────────────────────────────────────

        private int CrosshairMask
        {
            get
            {
                int mask = 0;
                foreach (var item in Crosshairs)
                    if (item.Enabled) mask |= 1 << (item.Number - 1);
                return mask;
            }
        }

        private int _crosshairsLastOn;

        /// <summary>The crosshairs the Crosshair switch turned off last, a bit
        /// for each crosshair's number. Zero until it has turned any
        /// off.</summary>
        public int CrosshairsLastOn
        {
            get => _crosshairsLastOn;
            set => SetProperty(ref _crosshairsLastOn, value & ((1 << PadForge.Services.CrosshairData.MaxCrosshairs) - 1));
        }

        /// <summary>The Extras switch of Crosshair: on while any crosshair on
        /// the list is on. Turned on with none remembered, or with every
        /// remembered crosshair since removed, it turns on the first, as the
        /// Screen Overlay switch does.</summary>
        public bool CrosshairsAnyEnabled
        {
            get => CrosshairMask != 0;
            set
            {
                int now = CrosshairMask;
                if (value == (now != 0)) return;
                if (!value) CrosshairsLastOn = now;
                int mask = 0;
                if (value)
                {
                    foreach (var item in Crosshairs)
                        mask |= _crosshairsLastOn & (1 << (item.Number - 1));
                    if (mask == 0 && Crosshairs.Count > 0) mask = 1 << (Crosshairs[0].Number - 1);
                }
                foreach (var item in Crosshairs)
                    item.Enabled = (mask & (1 << (item.Number - 1))) != 0;
            }
        }

        // ─────────────────────────────────────────────
        //  The catalog switches follow their own
        // ─────────────────────────────────────────────

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            switch (e.PropertyName)
            {
                case nameof(HeadTrackingEnabled):
                case nameof(HeadTrackingFreeTrack):
                case nameof(HeadTrackingOpenXr):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HeadTrackingAnyEnabled)));
                    break;
                case nameof(EnableMenuOverlay):
                case nameof(EnableShiftLayerFlyout):
                case nameof(EnableProfileOverlay):
                case nameof(EnableDeviceDisconnectedNote):
                case nameof(EnableDeviceConnectedNote):
                case nameof(EnableControllersNote):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(OverlaysAnyEnabled)));
                    break;
                case nameof(ScreenOverlays):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ScreenOverlaysAnyEnabled)));
                    break;
                case nameof(Crosshairs):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(CrosshairsAnyEnabled)));
                    break;
                case nameof(Magnifier):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(MagnifierEnabled)));
                    break;
                case nameof(AutomationLevel):
                    // The Automation page's choice and its line (#600).
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(AutomationLevelIndex)));
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(AutomationLevelLine)));
                    break;
            }
            // Home's own followers: its engine button and its count of the
            // extras in use (the Home part).
            HomeFollows(e.PropertyName);
        }
    }
}
