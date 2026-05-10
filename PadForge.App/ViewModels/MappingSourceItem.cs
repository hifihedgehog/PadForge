using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PadForge.ViewModels
{
    /// <summary>
    /// One source row within a multi-source <see cref="MappingItem"/>.
    /// Phase 2C — represents a single <c>Engine.Data.MappingSource</c> in
    /// the Mappings UI. Bound by the (forthcoming) RowDetailsTemplate
    /// inside the Mappings DataGrid.
    /// </summary>
    public class MappingSourceItem : ObservableObject
    {
        /// <summary>Per-source input picker choices. Populated by the
        /// parent <see cref="MappingItem"/> whenever
        /// <see cref="DeviceGuid"/> changes — the parent calls back into
        /// the InputService's per-device InputChoice lookup. Empty
        /// DeviceGuid means "use the slot's primary device" and the
        /// parent populates this with that device's inputs.</summary>
        public ObservableCollection<InputChoice> AvailableInputs { get; } = new();
        private string _kind = "Direct";
        private string _deviceGuid = "";
        private string _descriptor = "";
        private bool _invert;
        private bool _halfAxis;
        private int _deadZone = 50;
        private string _paramUp = "";
        private string _paramDown = "";
        private double _paramRate = 0.5;
        private bool _paramSticky = true;
        private double _paramMin;
        private double _paramMax = 1;
        private string _paramModifier = "";

        public string Kind { get => _kind; set => SetProperty(ref _kind, value ?? "Direct"); }
        public string DeviceGuid
        {
            get => _deviceGuid;
            set => SetProperty(ref _deviceGuid, value ?? "");
        }
        public string Descriptor
        {
            get => _descriptor;
            set
            {
                if (SetProperty(ref _descriptor, value ?? ""))
                {
                    OnPropertyChanged(nameof(IsButtonClassDescriptor));
                    OnPropertyChanged(nameof(DirectionBadge));
                }
            }
        }
        public bool Invert
        {
            get => _invert;
            set
            {
                if (SetProperty(ref _invert, value))
                    OnPropertyChanged(nameof(DirectionBadge));
            }
        }

        /// <summary>True when the descriptor is button-class (button,
        /// POV direction, or touchpad click) — bool-yielding sources for
        /// which a direction badge on a bipolar-axis target makes sense.
        /// Axis / Slider sources encode their own sign so they get no
        /// direction badge.</summary>
        public bool IsButtonClassDescriptor
        {
            get
            {
                var d = _descriptor?.Trim() ?? "";
                if (d.Length == 0) return false;
                if (d.StartsWith("Button ", System.StringComparison.Ordinal)) return true;
                if (d.StartsWith("POV ", System.StringComparison.Ordinal)) return true;
                if (d.StartsWith("Touchpad ", System.StringComparison.Ordinal)) return true;
                return false;
            }
        }

        /// <summary>"→ +" or "← −" for button-class sources, depending
        /// on the Invert flag. Empty for non-button-class sources. The
        /// XAML-level visibility check still gates this on the parent
        /// MappingItem.IsBipolarAxisTarget so the badge only renders on
        /// stick-axis rows.</summary>
        public string DirectionBadge
        {
            get
            {
                if (!IsButtonClassDescriptor) return "";
                return _invert ? "← −" : "→ +";
            }
        }
        public bool HalfAxis { get => _halfAxis; set => SetProperty(ref _halfAxis, value); }
        public int DeadZone { get => _deadZone; set => SetProperty(ref _deadZone, value); }

        public string ParamUp { get => _paramUp; set => SetProperty(ref _paramUp, value ?? ""); }
        public string ParamDown { get => _paramDown; set => SetProperty(ref _paramDown, value ?? ""); }
        public double ParamRate { get => _paramRate; set => SetProperty(ref _paramRate, value); }
        public bool ParamSticky { get => _paramSticky; set => SetProperty(ref _paramSticky, value); }
        public double ParamMin { get => _paramMin; set => SetProperty(ref _paramMin, value); }
        public double ParamMax { get => _paramMax; set => SetProperty(ref _paramMax, value); }
        public string ParamModifier { get => _paramModifier; set => SetProperty(ref _paramModifier, value ?? ""); }

        // ─────────────────────────────────────────────
        //  Cross-device picker bridge
        // ─────────────────────────────────────────────

        private InputChoice _selectedInput;
        private bool _suppressSelectionSync;

        /// <summary>The currently picked <see cref="InputChoice"/> from
        /// the parent MappingItem's grouped cross-device picker. Setting
        /// this writes both <see cref="DeviceGuid"/> and
        /// <see cref="Descriptor"/> in one shot — mirrors
        /// <see cref="MappingItem.SelectedInput"/>'s behavior so a single
        /// dropdown selection lands two fields.</summary>
        public InputChoice SelectedInput
        {
            get => _selectedInput;
            set
            {
                if (_suppressSelectionSync) return;
                if (SetProperty(ref _selectedInput, value) && value != null)
                {
                    DeviceGuid = value.DeviceGuid ?? "";
                    Descriptor = value.Descriptor ?? "";
                }
            }
        }

        /// <summary>Sync the dropdown selection from the current
        /// <see cref="DeviceGuid"/>+<see cref="Descriptor"/> pair against
        /// a flat cross-device choice list. Match is on
        /// (DeviceGuid, Descriptor) with a descriptor-only fallback.
        /// Called by the parent MappingItem after the row's load /
        /// AvailableInputs refresh, with the parent's cross-device
        /// picker list as the search space.</summary>
        public void SyncSelectedInputFromState(System.Collections.Generic.IEnumerable<InputChoice> choices)
        {
            _suppressSelectionSync = true;
            try
            {
                if (string.IsNullOrEmpty(_descriptor) && string.IsNullOrEmpty(_deviceGuid))
                {
                    _selectedInput = null;
                    OnPropertyChanged(nameof(SelectedInput));
                    return;
                }
                if (choices == null)
                {
                    _selectedInput = null;
                    OnPropertyChanged(nameof(SelectedInput));
                    return;
                }
                string wantGuid = (_deviceGuid ?? "").ToLowerInvariant();
                InputChoice match = null;
                InputChoice descriptorOnlyMatch = null;
                foreach (var choice in choices)
                {
                    if (!string.Equals(choice.Descriptor, _descriptor, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (descriptorOnlyMatch == null) descriptorOnlyMatch = choice;
                    if (!string.IsNullOrEmpty(wantGuid)
                        && string.Equals(choice.DeviceGuid ?? "", wantGuid, StringComparison.OrdinalIgnoreCase))
                    {
                        match = choice;
                        break;
                    }
                }
                _selectedInput = match ?? descriptorOnlyMatch;
                OnPropertyChanged(nameof(SelectedInput));
            }
            finally
            {
                _suppressSelectionSync = false;
            }
        }

        /// <summary>Builds a domain <see cref="Engine.Data.MappingSource"/>
        /// from this VM's current values. Used by the Save pipeline.</summary>
        public Engine.Data.MappingSource ToDomain() => new()
        {
            Kind = _kind ?? "Direct",
            DeviceGuid = _deviceGuid ?? "",
            Descriptor = _descriptor ?? "",
            Invert = _invert,
            HalfAxis = _halfAxis,
            DeadZone = _deadZone,
            ParamUp = _paramUp ?? "",
            ParamDown = _paramDown ?? "",
            ParamRate = _paramRate,
            ParamSticky = _paramSticky,
            ParamMin = _paramMin,
            ParamMax = _paramMax,
            ParamModifier = _paramModifier ?? "",
        };

        /// <summary>Populates this VM from a domain
        /// <see cref="Engine.Data.MappingSource"/>.</summary>
        public static MappingSourceItem FromDomain(Engine.Data.MappingSource src)
        {
            if (src == null) return new MappingSourceItem();
            return new MappingSourceItem
            {
                Kind = src.Kind ?? "Direct",
                DeviceGuid = src.DeviceGuid ?? "",
                Descriptor = src.Descriptor ?? "",
                Invert = src.Invert,
                HalfAxis = src.HalfAxis,
                DeadZone = src.DeadZone,
                ParamUp = src.ParamUp ?? "",
                ParamDown = src.ParamDown ?? "",
                ParamRate = src.ParamRate,
                ParamSticky = src.ParamSticky,
                ParamMin = src.ParamMin,
                ParamMax = src.ParamMax,
                ParamModifier = src.ParamModifier ?? "",
            };
        }
    }
}
