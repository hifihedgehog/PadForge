using System;
using System.Collections.Generic;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;

namespace PadForge.ViewModels
{
    public partial class MappingSourceItem
    {
        // ── Mouse Travel (#597) ──
        // The settings of a "Mouse Travel X" or "Mouse Travel Y" input, on
        // the source's line where Mouse Position has its sensitivity. They
        // are MappingSource's ParamTravelCounts and the five beside it,
        // read by SourceKindRuntime.TickMouseTravel. The Steer input is the
        // source's own gate (GateDescriptor), which the engine reads on the
        // source's device. MappingItem carries the same members for a row's
        // first source, so one template draws either.

        /// <summary>True when the source reads "Mouse Travel X" or "Mouse
        /// Travel Y" through its own input, the engine's rule
        /// (SourceEvaluator.IsMouseTravel). A mode that reads keys leaves
        /// the input unread. Gates the settings below.</summary>
        public bool IsMouseTravelSource => !IsKindDescriptorless
            && SourceCoercion.IsMouseTravelDescriptor(_descriptor);

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
                    OnPropertyChanged(nameof(ParamTravelCountsDial));
            }
        }

        /// <summary><see cref="ParamTravelCounts"/> as its slider reads it,
        /// 0 to 100 (<see cref="MouseTravelLine.DialOf"/>).</summary>
        public double ParamTravelCountsDial
        {
            get => MouseTravelLine.DialOf(_paramTravelCounts);
            set => ParamTravelCounts = MouseTravelLine.CountsAt(value);
        }

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
        /// <summary>Return Time in seconds, 0.05 to 5.</summary>
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

        /// <summary>Steer: when the mouse turns the wheel, one of
        /// MappingSource's TravelSteer values. Always has no input, so a
        /// change to it empties the gate, and the engine never holds the
        /// mouse on an input the line does not show. The input comes back
        /// with a change to a mode that reads one.</summary>
        public string ParamTravelSteer
        {
            get => _paramTravelSteer;
            set
            {
                if (value == null) return;
                string steer = MappingSource.EffectiveTravelSteer(value);
                if (!SetProperty(ref _paramTravelSteer, steer)) return;
                if (IsMouseTravelSource)
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
            }
        }

        public IReadOnlyList<MouseTravelChoice> ParamTravelSteerChoices => MouseTravelLine.SteerChoices();

        /// <summary>The Steer Input picker shows for the two modes that
        /// read an input.</summary>
        public bool IsTravelSteerInputShown => IsMouseTravelSource
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

        /// <summary>The gate shows as the input the source waits on, but on
        /// a "Mouse Travel" source, where it is the Steer input and the
        /// Steer Input picker shows it.</summary>
        public bool IsGateShown => HasGate && !IsMouseTravelSource;

        /// <summary>A "Mouse Travel" source that holds an input beside
        /// Always shows as While Held: that is how the engine reads a gate
        /// on it, the way a gate holds any source. A load and a change of
        /// the input to this family both come here, so the line never hides
        /// an input the mouse waits on.</summary>
        private void SettleTravelSteer()
        {
            if (!IsMouseTravelSource || !HasGate || _paramTravelSteer != MappingSource.TravelSteerAlways) return;
            _paramTravelSteer = MappingSource.TravelSteerHeld;
            OnPropertyChanged(nameof(ParamTravelSteer));
        }

        /// <summary>The source's input, its mode, its device or the row's
        /// list of inputs changed: what the settings show is read again.</summary>
        internal void RaiseMouseTravel()
        {
            SettleTravelSteer();
            OnPropertyChanged(nameof(IsMouseTravelSource));
            OnPropertyChanged(nameof(IsTravelReturnTimeShown));
            OnPropertyChanged(nameof(IsTravelSteerInputShown));
            OnPropertyChanged(nameof(IsTravelLockAtSpeedShown));
            OnPropertyChanged(nameof(IsShapingShown));
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

        /// <summary>Takes the six settings of <paramref name="src"/>, or
        /// their defaults for none. Called after the source's input and its
        /// gates are in (<see cref="LoadShapingAndGates"/>).</summary>
        internal void LoadMouseTravel(MappingSource src)
        {
            ParamTravelCounts = src != null
                ? MappingSource.EffectiveTravelCounts(src.ParamTravelCounts) : MappingSource.DefaultTravelCounts;
            ParamTravelCentering = MappingSource.EffectiveTravelCentering(src?.ParamTravelCentering);
            ParamTravelReturnTime = src != null
                ? MappingSource.EffectiveTravelReturnTime(src.ParamTravelReturnTime) : MappingSource.DefaultTravelReturnTime;
            // Past the setter: a load puts no input by and takes none out
            // of the gate it has just read.
            _travelSteerInputPutBy = string.Empty;
            if (SetProperty(ref _paramTravelSteer, MappingSource.EffectiveTravelSteer(src?.ParamTravelSteer), nameof(ParamTravelSteer)))
                OnPropertyChanged(nameof(IsTravelSteerInputShown));
            ParamTravelSmoothingMs = src != null ? MappingSource.EffectiveTravelSmoothingMs(src.ParamTravelSmoothingMs) : 0.0;
            ParamTravelLockAtSpeed = src != null
                ? MappingSource.EffectiveTravelLockAtSpeed(src.ParamTravelLockAtSpeed) : MappingSource.DefaultTravelLockAtSpeed;
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
        }
    }
}
