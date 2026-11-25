using System;
using System.Collections.Generic;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;

namespace PadForge.ViewModels
{
    public partial class MappingSourceItem : IMouseDpiListener
    {
        // ── Mouse steering (#597, #600) ──
        // The settings of a mouse source, on the source's line where Mouse
        // Position has its sensitivity: the travel family's (Mouse Travel X
        // and Y, Wheel Travel, Wheel Travel Sideways and an axis marked as
        // moving relatively), Mouse Speed's and Mouse Position's. They are
        // MappingSource's ParamTravel* and ParamMouse* fields and
        // ParamAxisRelative, read by SourceKindRuntime. The Steer input is
        // the source's own gate (GateDescriptor), which the engine reads on
        // the source's device. MappingItem carries the same members for a
        // row's first source, so one template draws either.

        /// <summary>True when the source reads the travel family through its
        /// own input, the engine's rule (SourceEvaluator.IsMouseTravel): a
        /// travel input, or an axis marked as moving relatively. A mode that
        /// reads keys leaves the input unread. Gates the travel
        /// settings.</summary>
        public bool IsMouseTravelSource => !IsKindDescriptorless
            && (SourceCoercion.IsMouseTravelDescriptor(_descriptor)
                || (_paramAxisRelative && SourceCoercion.TryParseRelativeAxis(_descriptor, out _, out _)));

        /// <summary>True when the source reads a mouse's Axis 0 or 1, Mouse
        /// Speed X or Y, through its own input. Gates Mouse Speed's
        /// settings.</summary>
        public bool IsMouseSpeedSource => !IsKindDescriptorless
            && MouseTravelLine.IsMouseSpeedDescriptor(_descriptor) && MouseTravelLine.IsMouseDevice(_deviceGuid);

        /// <summary>True when the source reads Mouse Position X or Y
        /// through its own input.</summary>
        public bool IsMousePositionSource => !IsKindDescriptorless && IsMouseCursorSource;

        /// <summary>True for any mouse source that steers: the travel
        /// family, Mouse Speed and Mouse Position. Gates Steer, the Steer
        /// Input and Smoothing.</summary>
        public bool IsMouseSteerSource => IsMouseTravelSource || IsMouseSpeedSource || IsMousePositionSource;

        /// <summary>True when the source reads a numbered axis or slider of
        /// a device that is no mouse and no keyboard, which "This axis moves
        /// relatively" can mark, and an axis marked so while its device is
        /// away, so the switch that marked it stays. A mode that reads keys
        /// leaves the input unread.</summary>
        public bool IsAxisRelativeApplicable => !IsKindDescriptorless
            && SourceCoercion.TryParseRelativeAxis(_descriptor, out _, out _)
            && (_paramAxisRelative || MouseTravelLine.IsAxisDevice(_deviceGuid));

        private bool _paramAxisRelative;
        /// <summary>This axis moves relatively: the axis holds a position
        /// from how far it turned, as Mouse Travel does.</summary>
        public bool ParamAxisRelative
        {
            get => _paramAxisRelative;
            set
            {
                if (SetProperty(ref _paramAxisRelative, value)) RaiseMouseTravel();
            }
        }

        private double _paramTravelCounts = MappingSource.DefaultTravelCounts;
        /// <summary>Travel to Full Lock: the counts the mouse moves from
        /// center to a lock, 100 to 50,000.</summary>
        public double ParamTravelCounts
        {
            get => _paramTravelCounts;
            set
            {
                if (SetProperty(ref _paramTravelCounts, MouseTravelLine.Within(value,
                        MappingSource.MinTravelCounts, MappingSource.MaxTravelCounts, 0, _paramTravelCounts)))
                {
                    OnPropertyChanged(nameof(ParamTravelCountsDial));
                    OnPropertyChanged(nameof(ParamTravelCountsText));
                    OnPropertyChanged(nameof(TravelCountsUnit));
                }
            }
        }

        /// <summary><see cref="ParamTravelCounts"/> as its slider reads it,
        /// 0 to 100 (<see cref="MouseTravelLine.DialOf"/>).</summary>
        public double ParamTravelCountsDial
        {
            get => MouseTravelLine.DialOf(_paramTravelCounts);
            set => ParamTravelCounts = MouseTravelLine.CountsAt(value);
        }

        /// <summary>Travel to Full Lock in its box: the counts with the user's
        /// own digit grouping. A typed number is read in that format and
        /// lands inside the range, and text that is no number leaves the
        /// counts as they were and shows them again.</summary>
        public string ParamTravelCountsText
        {
            get => MouseTravelLine.FormatCounts(_paramTravelCounts);
            set
            {
                if (MouseTravelLine.ParseCounts(value) is double counts) ParamTravelCounts = counts;
                OnPropertyChanged(nameof(ParamTravelCountsText));
            }
        }

        /// <summary>What follows the counts' box: "counts", and "counts,
        /// 3.2 cm" once the mouse's DPI is set (#600).</summary>
        public string TravelCountsUnit => MouseTravelLine.CountsUnit(_paramTravelCounts,
            IsMouseTravelSource ? _descriptor : string.Empty, _deviceGuid, this);

        private string _paramTravelCentering = MappingSource.TravelCenteringOff;
        /// <summary>Centering: what brings the wheel back, one of
        /// MappingSource's TravelCentering values.</summary>
        public string ParamTravelCentering
        {
            get => _paramTravelCentering;
            set
            {
                // A picker whose list is being rebuilt writes null. That is
                // no choice (the lesson of #160).
                if (value == null) return;
                if (SetProperty(ref _paramTravelCentering, MappingSource.EffectiveTravelCentering(value)))
                    OnPropertyChanged(nameof(IsTravelReturnTimeShown));
            }
        }

        public IReadOnlyList<MouseTravelChoice> ParamTravelCenteringChoices => MouseTravelLine.CenteringChoices();

        private double _paramTravelReturnTime = MappingSource.DefaultTravelReturnTime;
        /// <summary>Return Time in seconds, 0 to 5. 0 centers at once.</summary>
        public double ParamTravelReturnTime
        {
            get => _paramTravelReturnTime;
            set => SetProperty(ref _paramTravelReturnTime, MouseTravelLine.Within(value,
                MappingSource.MinTravelReturnTime, MappingSource.MaxTravelReturnTime, 2, _paramTravelReturnTime));
        }

        /// <summary>Return Time shows for the two kinds of centering that
        /// take the wheel back over it.</summary>
        public bool IsTravelReturnTimeShown => IsMouseTravelSource
            && _paramTravelCentering is MappingSource.TravelCenteringSpring or MappingSource.TravelCenteringTelemetry;

        private string _paramTravelSteer = MappingSource.TravelSteerAlways;
        // The Steer input a change to Always took out of the gate, for a
        // change back to a mode that reads one. Kept for the session only.
        private string _travelSteerInputPutBy = string.Empty;

        /// <summary>Steer: when the mouse moves the source, one of Always,
        /// While Held and Center, While Held and Stay, and Toggle. Always has
        /// no input, so a change to it empties the gate, and the engine never
        /// holds the mouse on an input the line does not show. The input
        /// comes back with a change to a mode that reads one.</summary>
        public string ParamTravelSteer
        {
            get => _paramTravelSteer;
            set
            {
                if (value == null) return;
                string steer = MouseTravelLine.SteerOf(value, _paramTravelCentering, IsMouseTravelSource);
                if (!SetProperty(ref _paramTravelSteer, steer)) return;
                if (IsMouseSteerSource)
                {
                    if (steer == MappingSource.TravelSteerAlways)
                    {
                        if (HasGate)
                        {
                            _travelSteerInputPutBy = _gateDescriptor;
                            GateDescriptor = string.Empty;
                        }
                    }
                    else if (!HasGate && _travelSteerInputPutBy.Length > 0)
                    {
                        GateDescriptor = _travelSteerInputPutBy;
                        _travelSteerInputPutBy = string.Empty;
                    }
                }
                OnPropertyChanged(nameof(IsTravelSteerInputShown));
                OnPropertyChanged(nameof(IsMousePositionAnchorShown));
            }
        }

        public IReadOnlyList<MouseTravelChoice> ParamTravelSteerChoices => MouseTravelLine.SteerChoices();

        /// <summary>The Steer Input picker shows for the three modes that
        /// read an input.</summary>
        public bool IsTravelSteerInputShown => IsMouseSteerSource
            && _paramTravelSteer != MappingSource.TravelSteerAlways;

        private IReadOnlyList<InputChoice> _travelSteerInputs;
        /// <summary>The Steer Input picker's list
        /// (<see cref="MouseTravelLine.SteerInputs"/>), built when the
        /// picker first asks and again after what it is built from
        /// changes.</summary>
        public IReadOnlyList<InputChoice> TravelSteerInputs => _travelSteerInputs ??= MouseTravelLine.SteerInputs(
            ParentMappingItem?.KeyInputs, _deviceGuid, LineDeviceLabel, _gateDescriptor, GateText);

        /// <summary>The Steer input as its picker holds it: the entry for
        /// the gate on the source's device. A pick writes the gate.</summary>
        public InputChoice TravelSteerInputChoice
        {
            get => PadForge.Common.MappingDisplayResolver.FindChoiceOnDevice(
                TravelSteerInputs, _gateDescriptor, _deviceGuid);
            set
            {
                // A null write is a picker that lost its entry to a rebuilt
                // list, never a choice: the input stays (#160).
                if (value == null || string.Equals(value.Descriptor ?? string.Empty, _gateDescriptor, StringComparison.Ordinal)) return;
                GateDescriptor = value.Descriptor ?? string.Empty;
            }
        }

        private double _paramTravelSmoothingMs;
        /// <summary>Smoothing in milliseconds, 0 to 100.</summary>
        public double ParamTravelSmoothingMs
        {
            get => _paramTravelSmoothingMs;
            set => SetProperty(ref _paramTravelSmoothingMs, MouseTravelLine.Within(value,
                0.0, MappingSource.MaxTravelSmoothingMs, 0, _paramTravelSmoothingMs));
        }

        private double _paramTravelLockAtSpeed = MappingSource.DefaultTravelLockAtSpeed;
        /// <summary>Lock at Speed in percent, 20 to 100.</summary>
        public double ParamTravelLockAtSpeed
        {
            get => _paramTravelLockAtSpeed;
            set => SetProperty(ref _paramTravelLockAtSpeed, MouseTravelLine.Within(value,
                MappingSource.MinTravelLockAtSpeed, MappingSource.MaxTravelLockAtSpeed, 0, _paramTravelLockAtSpeed));
        }

        /// <summary>Lock at Speed shows while the Game Telemetry extra is
        /// on, since only a game's telemetry gives it a speed to act on.</summary>
        public bool IsTravelLockAtSpeedShown => IsMouseTravelSource
            && PadForge.Common.Input.Telemetry.GameTelemetryRuntime.Enabled;

        private double _paramMouseSpeedSensitivity = MappingSource.DefaultMouseSpeedSensitivity;
        /// <summary>Mouse Speed's Sensitivity, 0.1 to 10.</summary>
        public double ParamMouseSpeedSensitivity
        {
            get => _paramMouseSpeedSensitivity;
            set => SetProperty(ref _paramMouseSpeedSensitivity, MouseTravelLine.Within(value,
                MappingSource.MinMouseSpeedSensitivity, MappingSource.MaxMouseSpeedSensitivity, 2, _paramMouseSpeedSensitivity));
        }

        private double _paramMouseBackToCenter;
        /// <summary>Mouse Speed's Back to Center in seconds, 0 to 2.</summary>
        public double ParamMouseBackToCenter
        {
            get => _paramMouseBackToCenter;
            set => SetProperty(ref _paramMouseBackToCenter, MouseTravelLine.Within(value,
                0.0, MappingSource.MaxMouseBackToCenter, 2, _paramMouseBackToCenter));
        }

        private bool _paramMousePositionAnchor;
        /// <summary>Mouse Position's Start Where Steering Began.</summary>
        public bool ParamMousePositionAnchor
        {
            get => _paramMousePositionAnchor;
            set => SetProperty(ref _paramMousePositionAnchor, value);
        }

        /// <summary>Start Where Steering Began shows on Mouse Position under
        /// a mode that reads a Steer input, the only place it acts.</summary>
        public bool IsMousePositionAnchorShown => IsMousePositionSource
            && _paramTravelSteer != MappingSource.TravelSteerAlways;

        /// <summary>The gate shows as the input the source waits on, but on
        /// a mouse source, where it is the Steer input and the Steer Input
        /// picker shows it.</summary>
        public bool IsGateShown => HasGate && !IsMouseSteerSource;

        /// <summary>A mouse source that holds an input beside Always shows
        /// the mode the engine reads it in (MappingSource.EffectiveMouseSteer),
        /// so the line never hides an input the mouse waits on. A load and a
        /// change of the input to a mouse family both come here.</summary>
        private void SettleTravelSteer()
        {
            if (!IsMouseSteerSource || !HasGate || _paramTravelSteer != MappingSource.TravelSteerAlways) return;
            _paramTravelSteer = MappingSource.EffectiveMouseSteer(MappingSource.TravelSteerAlways,
                _paramTravelCentering, hasInput: true, holdsPosition: IsMouseTravelSource);
            OnPropertyChanged(nameof(ParamTravelSteer));
        }

        /// <summary>The source's input, its mode, its device or the row's
        /// list of inputs changed: what the settings show is read again.</summary>
        internal void RaiseMouseTravel()
        {
            SettleTravelSteer();
            OnPropertyChanged(nameof(IsMouseTravelSource));
            OnPropertyChanged(nameof(IsMouseSpeedSource));
            OnPropertyChanged(nameof(IsMousePositionSource));
            OnPropertyChanged(nameof(IsMouseSteerSource));
            OnPropertyChanged(nameof(IsAxisRelativeApplicable));
            OnPropertyChanged(nameof(IsTravelReturnTimeShown));
            OnPropertyChanged(nameof(IsTravelSteerInputShown));
            OnPropertyChanged(nameof(IsTravelLockAtSpeedShown));
            OnPropertyChanged(nameof(IsMousePositionAnchorShown));
            OnPropertyChanged(nameof(TravelCountsUnit));
            OnPropertyChanged(nameof(IsShapingShown));
            OnPropertyChanged(nameof(IsParamAccelApplicable));
            RaiseTravelSteerInput();
        }

        /// <summary>The Steer input changed, or what its picker lists did.
        /// A picker never asked for has nothing to build again.</summary>
        internal void RaiseTravelSteerInput()
        {
            OnPropertyChanged(nameof(IsGateShown));
            if (_travelSteerInputs == null) return;
            _travelSteerInputs = null;
            OnPropertyChanged(nameof(TravelSteerInputs));
            OnPropertyChanged(nameof(TravelSteerInputChoice));
        }

        /// <summary>The Game Telemetry extra went on or off.</summary>
        internal void RaiseMouseTravelRoad() => OnPropertyChanged(nameof(IsTravelLockAtSpeedShown));

        private bool _listensForMouseDpi;
        void IMouseDpiListener.ListenForMouseDpi()
        {
            if (_listensForMouseDpi) return;
            _listensForMouseDpi = true;
            MouseDpiNotice.Listen(OnMouseDpiChanged);
        }

        /// <summary>A mouse's DPI changed on its device panel.</summary>
        private void OnMouseDpiChanged(object sender, EventArgs e) => OnPropertyChanged(nameof(TravelCountsUnit));

        /// <summary>Takes the mouse settings of <paramref name="src"/>, or
        /// their defaults for none. Called after the source's input and its
        /// gates are in (<see cref="LoadShapingAndGates"/>).</summary>
        internal void LoadMouseTravel(MappingSource src)
        {
            _paramAxisRelative = src?.ParamAxisRelative ?? false;
            OnPropertyChanged(nameof(ParamAxisRelative));
            ParamTravelCounts = src != null
                ? MappingSource.EffectiveTravelCounts(src.ParamTravelCounts) : MappingSource.DefaultTravelCounts;
            ParamTravelCentering = MappingSource.EffectiveTravelCentering(src?.ParamTravelCentering);
            ParamTravelReturnTime = src != null
                ? MappingSource.EffectiveTravelReturnTime(src.ParamTravelReturnTime) : MappingSource.DefaultTravelReturnTime;
            // Past the setter: a load puts no input by and takes none out
            // of the gate it has just read.
            _travelSteerInputPutBy = string.Empty;
            if (SetProperty(ref _paramTravelSteer, MouseTravelLine.SteerOf(src?.ParamTravelSteer, _paramTravelCentering, IsMouseTravelSource),
                    nameof(ParamTravelSteer)))
                OnPropertyChanged(nameof(IsTravelSteerInputShown));
            ParamTravelSmoothingMs = src != null ? MappingSource.EffectiveTravelSmoothingMs(src.ParamTravelSmoothingMs) : 0.0;
            ParamTravelLockAtSpeed = src != null
                ? MappingSource.EffectiveTravelLockAtSpeed(src.ParamTravelLockAtSpeed) : MappingSource.DefaultTravelLockAtSpeed;
            ParamMouseSpeedSensitivity = src != null
                ? MappingSource.EffectiveMouseSpeedSensitivity(src.ParamMouseSpeedSensitivity) : MappingSource.DefaultMouseSpeedSensitivity;
            ParamMouseBackToCenter = src != null ? MappingSource.EffectiveMouseBackToCenter(src.ParamMouseBackToCenter) : 0.0;
            ParamMousePositionAnchor = src?.ParamMousePositionAnchor ?? false;
            RaiseMouseTravel();
        }

        /// <summary>Writes them onto <paramref name="dst"/>. The Steer
        /// input is the gate, which <see cref="StoreShapingAndGates"/>
        /// writes.</summary>
        internal void StoreMouseTravel(MappingSource dst)
        {
            if (dst == null) return;
            dst.ParamTravelCounts = _paramTravelCounts;
            dst.ParamTravelCentering = _paramTravelCentering;
            dst.ParamTravelReturnTime = _paramTravelReturnTime;
            dst.ParamTravelSteer = _paramTravelSteer;
            dst.ParamTravelSmoothingMs = _paramTravelSmoothingMs;
            dst.ParamTravelLockAtSpeed = _paramTravelLockAtSpeed;
            dst.ParamMouseSpeedSensitivity = _paramMouseSpeedSensitivity;
            dst.ParamMouseBackToCenter = _paramMouseBackToCenter;
            dst.ParamMousePositionAnchor = _paramMousePositionAnchor;
            // Kept while the device is away: only another input drops it.
            dst.ParamAxisRelative = _paramAxisRelative && SourceCoercion.TryParseRelativeAxis(_descriptor, out _, out _);
        }
    }
}
