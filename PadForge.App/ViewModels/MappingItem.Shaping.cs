using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>A choice of the Deadzone Shape picker on a source's line: the
    /// stored MappingSource.ParamStickDeadZoneShape and its words.</summary>
    public sealed record SourceDeadZoneShapeChoice(int Value, string Display)
    {
        public override string ToString() => Display;
    }

    public partial class MappingItem
    {
        // ── The source's own shaping and its gates ──
        // MappingSource.ParamCurveExponent, ParamRangeOuter, ParamAntiDeadzone,
        // ParamStickDeadZoneShape and ParamStickDeadZoneInner are read by
        // SourceCoercion's shaping seam (ApplyStickDeadZoneShape and
        // ApplyCurveRangeShaping), GateDescriptor and Gate2Descriptor by the
        // chord-second-leg read. The source line edits them here, so the save
        // rebuilds them from this object like every other dial.

        private double _paramCurveExponent;
        /// <summary>The reading raised to this power. 0 and 1 leave it
        /// straight.</summary>
        public double ParamCurveExponent
        {
            get => _paramCurveExponent;
            set { if (SetProperty(ref _paramCurveExponent, Math.Clamp(value, 0.0, 5.0))) RaiseShaping(); }
        }

        private double _paramRangeOuter;
        /// <summary>Full output at this fraction of the travel. 0 is off.</summary>
        public double ParamRangeOuter
        {
            get => _paramRangeOuter;
            set
            {
                if (SetProperty(ref _paramRangeOuter, Math.Clamp(value, 0.0, 1.0)))
                {
                    OnPropertyChanged(nameof(ParamRangeOuterPercent));
                    RaiseShaping();
                }
            }
        }

        /// <summary><see cref="ParamRangeOuter"/> in percent, 100 for off.</summary>
        public double ParamRangeOuterPercent
        {
            get => _paramRangeOuter > 0.0 && _paramRangeOuter < 1.0 ? Math.Round(_paramRangeOuter * 100.0, 1) : 100.0;
            set => ParamRangeOuter = value >= 100.0 ? 0.0 : Math.Clamp(value, 1.0, 100.0) / 100.0;
        }

        private double _paramAntiDeadzone;
        /// <summary>The output starts at this fraction. 0 is off.</summary>
        public double ParamAntiDeadzone
        {
            get => _paramAntiDeadzone;
            set
            {
                if (SetProperty(ref _paramAntiDeadzone, Math.Clamp(value, 0.0, 0.99)))
                {
                    OnPropertyChanged(nameof(ParamAntiDeadzonePercent));
                    RaiseShaping();
                }
            }
        }

        /// <summary><see cref="ParamAntiDeadzone"/> in percent.</summary>
        public double ParamAntiDeadzonePercent
        {
            get => Math.Round(_paramAntiDeadzone * 100.0, 1);
            set => ParamAntiDeadzone = Math.Clamp(value, 0.0, 99.0) / 100.0;
        }

        private int _paramStickDeadZoneShape;
        /// <summary>0 off, 1 axial (this axis alone), 2 radial (the stick
        /// pair together).</summary>
        public int ParamStickDeadZoneShape
        {
            get => _paramStickDeadZoneShape;
            set { if (SetProperty(ref _paramStickDeadZoneShape, value is 1 or 2 ? value : 0)) RaiseShaping(); }
        }

        /// <summary>The Deadzone Shape picker's choices.</summary>
        public IReadOnlyList<SourceDeadZoneShapeChoice> ParamStickDeadZoneShapeChoices => new[]
        {
            new SourceDeadZoneShapeChoice(0, Strings.Instance.Mapping_SourceDeadZoneShape_Off),
            new SourceDeadZoneShapeChoice(1, Strings.Instance.Mapping_SourceDeadZoneShape_Axial),
            new SourceDeadZoneShapeChoice(2, Strings.Instance.Mapping_SourceDeadZoneShape_Radial),
        };

        private double _paramStickDeadZoneInner;
        /// <summary>The inner deadzone of the shape, as a fraction.</summary>
        public double ParamStickDeadZoneInner
        {
            get => _paramStickDeadZoneInner;
            set
            {
                if (SetProperty(ref _paramStickDeadZoneInner, Math.Clamp(value, 0.0, 0.99)))
                {
                    OnPropertyChanged(nameof(ParamStickDeadZoneInnerPercent));
                    RaiseShaping();
                }
            }
        }

        /// <summary><see cref="ParamStickDeadZoneInner"/> in percent.</summary>
        public double ParamStickDeadZoneInnerPercent
        {
            get => Math.Round(_paramStickDeadZoneInner * 100.0, 1);
            set => ParamStickDeadZoneInner = Math.Clamp(value, 0.0, 99.0) / 100.0;
        }

        /// <summary>True when any of the five holds a value the engine
        /// applies.</summary>
        public bool HasShaping =>
            (_paramCurveExponent > 0.0 && _paramCurveExponent != 1.0)
            || (_paramRangeOuter > 0.0 && _paramRangeOuter < 1.0)
            || (_paramAntiDeadzone > 0.0 && _paramAntiDeadzone < 1.0)
            || _paramStickDeadZoneShape != 0
            || (_paramStickDeadZoneInner > 0.0 && _paramStickDeadZoneInner < 1.0);

        /// <summary>The five shaping dials show where the source holds a
        /// value, and on a continuous source of a row whose output has no
        /// device card (a mouse axis, a raw axis), where they are the only
        /// shaping there is. A stick or trigger output's shaping lives on
        /// the device's own card, which takes an import's at assignment.</summary>
        public bool IsShapingShown => HasShaping
            || ((IsParamAccelApplicable || IsMouseTravelSource) && !TargetHasDeviceCard);

        /// <summary>True when the row's output is one of the six a device
        /// card shapes.</summary>
        private bool TargetHasDeviceCard => PadForge.Services.WorkshopTuningApplier.IsCardTarget(TargetSettingName);

        private void RaiseShaping()
        {
            OnPropertyChanged(nameof(HasShaping));
            OnPropertyChanged(nameof(IsShapingShown));
        }

        private string _gateDescriptor = string.Empty;
        /// <summary>The input this source waits on: it counts only while this
        /// also reads as pressed on the same controller, or on a mouse source,
        /// where it is the Steer input, on the device the line names for it
        /// (<see cref="TravelSteerDeviceGuid"/>). Empty for none, which takes
        /// that device away too.</summary>
        public string GateDescriptor
        {
            get => _gateDescriptor;
            set
            {
                if (SetProperty(ref _gateDescriptor, value ?? string.Empty))
                {
                    if (_gateDescriptor.Length == 0 && _travelSteerDeviceGuid != null)
                    {
                        _travelSteerDeviceGuid = null;
                        OnPropertyChanged(nameof(TravelSteerDeviceGuid));
                    }
                    OnPropertyChanged(nameof(GateText));
                    OnPropertyChanged(nameof(HasGate));
                    RaiseTravelSteerInput();
                }
            }
        }

        private string _gate2Descriptor = string.Empty;
        /// <summary>A second input the source waits on, beside
        /// <see cref="GateDescriptor"/>. Empty for none.</summary>
        public string Gate2Descriptor
        {
            get => _gate2Descriptor;
            set
            {
                if (SetProperty(ref _gate2Descriptor, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(Gate2Text));
                    OnPropertyChanged(nameof(HasGate2));
                }
            }
        }

        public bool HasGate => _gateDescriptor.Length > 0;
        public bool HasGate2 => _gate2Descriptor.Length > 0;

        /// <summary>The gate in words. An import's zone test (a stick's
        /// sector or ring, a trigger's pull band) reads as the range it
        /// checks.</summary>
        public string GateText => GateWords(_gateDescriptor);
        public string Gate2Text => GateWords(_gate2Descriptor);

        private static string GateWords(string descriptor)
            => string.IsNullOrEmpty(descriptor) ? string.Empty
                : PadForge.Common.MappingDisplayResolver.ResolveDescriptorText(descriptor,
                    Array.Empty<global::PadForge.Engine.DeviceObjectItem>(), padPrefixAlways: true) ?? descriptor;

        /// <summary>The gate's picker: a pick replaces the gate, and the
        /// picker itself holds nothing.</summary>
        public InputChoice GatePick
        {
            get => null;
            set { if (value != null) GateDescriptor = value.Descriptor ?? string.Empty; }
        }

        public InputChoice Gate2Pick
        {
            get => null;
            set { if (value != null) Gate2Descriptor = value.Descriptor ?? string.Empty; }
        }

        /// <summary>The object whose shaping and gates the line under the
        /// source shows.</summary>
        public object ShapingHolder => IsPrimaryDescriptor ? this : PrimaryKindSource;

        private RelayCommand _resetParamCurveExponentCommand, _resetParamRangeOuterCommand,
            _resetParamAntiDeadzoneCommand, _resetParamStickDeadZoneShapeCommand,
            _resetParamStickDeadZoneInnerCommand, _resetGateDescriptorCommand, _resetGate2DescriptorCommand;
        public RelayCommand ResetParamCurveExponentCommand =>
            _resetParamCurveExponentCommand ??= new RelayCommand(() => ParamCurveExponent = 0.0);
        public RelayCommand ResetParamRangeOuterCommand =>
            _resetParamRangeOuterCommand ??= new RelayCommand(() => ParamRangeOuter = 0.0);
        public RelayCommand ResetParamAntiDeadzoneCommand =>
            _resetParamAntiDeadzoneCommand ??= new RelayCommand(() => ParamAntiDeadzone = 0.0);
        public RelayCommand ResetParamStickDeadZoneShapeCommand =>
            _resetParamStickDeadZoneShapeCommand ??= new RelayCommand(() => ParamStickDeadZoneShape = 0);
        public RelayCommand ResetParamStickDeadZoneInnerCommand =>
            _resetParamStickDeadZoneInnerCommand ??= new RelayCommand(() => ParamStickDeadZoneInner = 0.0);
        public RelayCommand ResetGateDescriptorCommand =>
            _resetGateDescriptorCommand ??= new RelayCommand(() => GateDescriptor = string.Empty);
        public RelayCommand ResetGate2DescriptorCommand =>
            _resetGate2DescriptorCommand ??= new RelayCommand(() => Gate2Descriptor = string.Empty);

        /// <summary>Takes the five shaping values and the two gates of
        /// <paramref name="src"/>, or clears them for none.</summary>
        internal void LoadShapingAndGates(Engine.Data.MappingSource src)
        {
            ParamCurveExponent = src?.ParamCurveExponent ?? 0.0;
            ParamRangeOuter = src?.ParamRangeOuter ?? 0.0;
            ParamAntiDeadzone = src?.ParamAntiDeadzone ?? 0.0;
            ParamStickDeadZoneShape = src?.ParamStickDeadZoneShape ?? 0;
            ParamStickDeadZoneInner = src?.ParamStickDeadZoneInner ?? 0.0;
            GateDescriptor = src?.GateDescriptor ?? string.Empty;
            Gate2Descriptor = src?.Gate2Descriptor ?? string.Empty;
        }

        /// <summary>Writes them onto <paramref name="dst"/>.</summary>
        internal void StoreShapingAndGates(Engine.Data.MappingSource dst, bool gates = true)
        {
            if (dst == null) return;
            dst.ParamCurveExponent = _paramCurveExponent;
            dst.ParamRangeOuter = _paramRangeOuter;
            dst.ParamAntiDeadzone = _paramAntiDeadzone;
            dst.ParamStickDeadZoneShape = _paramStickDeadZoneShape;
            dst.ParamStickDeadZoneInner = _paramStickDeadZoneInner;
            if (!gates) return;
            dst.GateDescriptor = _gateDescriptor;
            dst.Gate2Descriptor = _gate2Descriptor;
        }
    }
}
