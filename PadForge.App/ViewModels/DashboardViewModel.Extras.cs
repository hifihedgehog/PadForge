using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PadForge.ViewModels
{
    /// <summary>
    /// The Extras catalog's switches for the three extras that have several
    /// switches and no single one (#574): Head Tracking's three inputs, the
    /// three overlays and the Screen Overlay list.
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

        /// <summary>The overlays as bits: menu, shift layer, profile switch.</summary>
        internal const int MenuOverlayBit = 1, ShiftLayerFlyoutBit = 2, ProfileOverlayBit = 4;

        /// <summary>What the Overlays switch turns on the first time: all
        /// three, which is how a new settings file starts.</summary>
        internal const int OverlaysStartingSet = MenuOverlayBit | ShiftLayerFlyoutBit | ProfileOverlayBit;

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

        // ─────────────────────────────────────────────
        //  Overlays
        // ─────────────────────────────────────────────

        private int OverlayMask =>
            (_enableMenuOverlay ? MenuOverlayBit : 0)
            | (_enableShiftLayerFlyout ? ShiftLayerFlyoutBit : 0)
            | (_enableProfileOverlay ? ProfileOverlayBit : 0);

        private int _overlaysLastShown;

        /// <summary>The overlays the Overlays switch turned off last, as bits.
        /// Zero until it has turned any off.</summary>
        public int OverlaysLastShown
        {
            get => _overlaysLastShown;
            set => SetProperty(ref _overlaysLastShown, value & OverlaysStartingSet);
        }

        /// <summary>The Extras switch of Overlays: on while any of the three
        /// overlays is on.</summary>
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
            }
        }

        private RelayCommand _resetOverlaysCommand;

        /// <summary>The reset beside the Overlays switch: each overlay back to
        /// its default, which is on.</summary>
        public RelayCommand ResetOverlaysCommand =>
            _resetOverlaysCommand ??= new RelayCommand(() =>
            {
                ResetSettingCommand.Execute(nameof(EnableMenuOverlay));
                ResetSettingCommand.Execute(nameof(EnableShiftLayerFlyout));
                ResetSettingCommand.Execute(nameof(EnableProfileOverlay));
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
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(OverlaysAnyEnabled)));
                    break;
                case nameof(ScreenOverlays):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ScreenOverlaysAnyEnabled)));
                    break;
            }
            // Home's own followers: its engine button and its count of the
            // extras in use (the Home part).
            HomeFollows(e.PropertyName);
        }
    }
}
