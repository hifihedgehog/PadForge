using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Resources.Strings;

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

        public string Kind
        {
            get => _kind;
            set
            {
                if (SetProperty(ref _kind, value ?? "Direct"))
                {
                    OnPropertyChanged(nameof(IsDirectKind));
                    OnPropertyChanged(nameof(IsIncrementalKind));
                    OnPropertyChanged(nameof(IsInvertOnHoldKind));
                    OnPropertyChanged(nameof(IsKindDescriptorless));
                    OnPropertyChanged(nameof(ParamUpInputChoice));
                    OnPropertyChanged(nameof(ParamDownInputChoice));
                    OnPropertyChanged(nameof(ParamModifierInputChoice));
                }
            }
        }

        public bool IsDirectKind => string.Equals(_kind, "Direct", StringComparison.Ordinal);
        public bool IsIncrementalKind => string.Equals(_kind, "Incremental", StringComparison.Ordinal);
        public bool IsInvertOnHoldKind => string.Equals(_kind, "InvertOnHold", StringComparison.Ordinal);

        /// <summary>True for kinds where the source's main Descriptor +
        /// Invert / HalfAxis / DeadZone fields are unused (Incremental
        /// authors via Up/Down + Param*; InvertOnHold acts as a row-level
        /// modifier that only uses ParamModifier). Used by the XAML to
        /// collapse the redundant primary controls so the user only sees
        /// the kind-specific row below.</summary>
        public bool IsKindDescriptorless => IsIncrementalKind || IsInvertOnHoldKind;

        /// <summary>User-facing source kinds in the Mappings UI. Per-source
        /// InvertOnHold flips only this source's contribution (useful when
        /// the user wants a single contributor in a multi-source row to
        /// flip independently). The row-level "Invert while held" modifier
        /// on <see cref="MappingItem"/> is a separate, complementary tool
        /// for flipping the row's final output — the B.3 reversible-throttle
        /// shape — and is exposed in the row footer rather than here.</summary>
        public static System.Collections.Generic.IReadOnlyList<string> KindOptions { get; }
            = new[] { "Direct", "Incremental", "InvertOnHold" };

        internal MappingItem ParentMappingItem { get; set; }

        private InputChoice ResolveParamChoice(string descriptor)
        {
            if (ParentMappingItem == null || string.IsNullOrEmpty(descriptor)) return null;
            foreach (var c in ParentMappingItem.AvailableInputs)
                if (c != null && string.Equals(c.Descriptor, descriptor, StringComparison.Ordinal))
                    return c;
            return null;
        }

        public InputChoice ParamUpInputChoice
        {
            get => ResolveParamChoice(_paramUp);
            set
            {
                var d = value?.Descriptor ?? "";
                if (!string.Equals(_paramUp, d, StringComparison.Ordinal))
                {
                    _paramUp = d;
                    OnPropertyChanged(nameof(ParamUp));
                    OnPropertyChanged(nameof(ParamUpInputChoice));
                }
            }
        }

        public InputChoice ParamDownInputChoice
        {
            get => ResolveParamChoice(_paramDown);
            set
            {
                var d = value?.Descriptor ?? "";
                if (!string.Equals(_paramDown, d, StringComparison.Ordinal))
                {
                    _paramDown = d;
                    OnPropertyChanged(nameof(ParamDown));
                    OnPropertyChanged(nameof(ParamDownInputChoice));
                }
            }
        }

        public InputChoice ParamModifierInputChoice
        {
            get => ResolveParamChoice(_paramModifier);
            set
            {
                var d = value?.Descriptor ?? "";
                if (!string.Equals(_paramModifier, d, StringComparison.Ordinal))
                {
                    _paramModifier = d;
                    OnPropertyChanged(nameof(ParamModifier));
                    OnPropertyChanged(nameof(ParamModifierInputChoice));
                }
            }
        }

        public void RefreshParamPickerChoices()
        {
            OnPropertyChanged(nameof(ParamUpInputChoice));
            OnPropertyChanged(nameof(ParamDownInputChoice));
            OnPropertyChanged(nameof(ParamModifierInputChoice));
        }
        public string DeviceGuid
        {
            get => _deviceGuid;
            set => SetProperty(ref _deviceGuid, value ?? "");
        }

        private string _deviceLabel = "";
        /// <summary>Friendly name of the device this source reads from
        /// (e.g. "DualSense Edge"). Surfaced inline below the per-source
        /// picker so users can tell at a glance which device each
        /// ExtraSource is bound to. Set by the parent MappingItem when
        /// the source is hydrated / synced; setting directly via the
        /// SelectedInput picker also updates it via the InputChoice's
        /// DeviceLabel field.</summary>
        public string DeviceLabel
        {
            get => _deviceLabel;
            set => SetProperty(ref _deviceLabel, value ?? "");
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
                    OnPropertyChanged(nameof(IsDeadZoneApplicable));
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
        public int DeadZone
        {
            get => _deadZone;
            set => SetProperty(ref _deadZone, System.Math.Clamp(value, 0, 100));
        }

        /// <summary>True when the per-source deadzone column is
        /// applicable for this source: the descriptor is an axis or
        /// slider AND the parent target is a discrete (button-type)
        /// output. The parent <see cref="MappingItem"/> is the only
        /// place that knows the target type, so the parent passes it
        /// down via <see cref="ParentTargetIsDiscrete"/>.</summary>
        public bool IsDeadZoneApplicable
        {
            get
            {
                var desc = _descriptor ?? "";
                if (string.IsNullOrEmpty(desc)) return false;
                int start = 0;
                if (start < desc.Length && (desc[start] == 'I' || desc[start] == 'i')) start++;
                if (start < desc.Length && (desc[start] == 'H' || desc[start] == 'h')) start++;
                var body = desc.AsSpan(start);
                if (!body.StartsWith("Axis") && !body.StartsWith("Slider")) return false;
                return _parentTargetIsDiscrete;
            }
        }

        private bool _parentTargetIsDiscrete;
        /// <summary>Set by the parent <see cref="MappingItem"/> at
        /// hydration time so <see cref="IsDeadZoneApplicable"/> can
        /// know whether the row's target is a button-class output.
        /// Stored on the source rather than walked up the tree so the
        /// XAML can bind directly without a RelativeSource hop.</summary>
        public bool ParentTargetIsDiscrete
        {
            get => _parentTargetIsDiscrete;
            set
            {
                if (SetProperty(ref _parentTargetIsDiscrete, value))
                    OnPropertyChanged(nameof(IsDeadZoneApplicable));
            }
        }

        public string ParamUp
        {
            get => _paramUp;
            set
            {
                if (SetProperty(ref _paramUp, value ?? ""))
                    OnPropertyChanged(nameof(ParamUpInputChoice));
            }
        }
        public string ParamDown
        {
            get => _paramDown;
            set
            {
                if (SetProperty(ref _paramDown, value ?? ""))
                    OnPropertyChanged(nameof(ParamDownInputChoice));
            }
        }
        public double ParamRate { get => _paramRate; set => SetProperty(ref _paramRate, value); }
        public bool ParamSticky { get => _paramSticky; set => SetProperty(ref _paramSticky, value); }
        public double ParamMin { get => _paramMin; set => SetProperty(ref _paramMin, value); }
        public double ParamMax { get => _paramMax; set => SetProperty(ref _paramMax, value); }
        public string ParamModifier
        {
            get => _paramModifier;
            set
            {
                if (SetProperty(ref _paramModifier, value ?? ""))
                    OnPropertyChanged(nameof(ParamModifierInputChoice));
            }
        }

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
                    DeviceLabel = value.DeviceLabel ?? "";
                    Descriptor = value.Descriptor ?? "";
                }
            }
        }

        /// <summary>Sync the dropdown selection from the current
        /// <see cref="DeviceGuid"/>+<see cref="Descriptor"/> pair
        /// against the parent row's cross-device choice list. Match
        /// is on (DeviceGuid, Descriptor) with a descriptor-only
        /// fallback. Called by the parent MappingItem after the row's
        /// load or after its AvailableInputs list is rebuilt.</summary>
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
                var picked = match ?? descriptorOnlyMatch;
                _selectedInput = picked;
                if (picked != null && !string.IsNullOrEmpty(picked.DeviceLabel))
                    DeviceLabel = picked.DeviceLabel;
                OnPropertyChanged(nameof(SelectedInput));
            }
            finally
            {
                _suppressSelectionSync = false;
            }
        }

        // ─────────────────────────────────────────────
        //  Recording (per-source)
        // ─────────────────────────────────────────────

        private bool _isRecording;
        public bool IsRecording
        {
            get => _isRecording;
            set
            {
                if (SetProperty(ref _isRecording, value))
                {
                    OnPropertyChanged(nameof(RecordButtonText));
                    OnPropertyChanged(nameof(RecordButtonIcon));
                }
            }
        }
        public string RecordButtonText => IsRecording ? Strings.Instance.Common_Recording : Strings.Instance.Common_Record;
        public string RecordButtonIcon => IsRecording ? "" : ""; // Stop : Record

        private RelayCommand _toggleRecordCommand;
        public RelayCommand ToggleRecordCommand =>
            _toggleRecordCommand ??= new RelayCommand(() =>
            {
                if (IsRecording)
                    StopRecordingRequested?.Invoke(this, EventArgs.Empty);
                else
                    StartRecordingRequested?.Invoke(this, EventArgs.Empty);
            });

        public event EventHandler StartRecordingRequested;
        public event EventHandler StopRecordingRequested;

        /// <summary>Identifies which Param field a record request is for
        /// (ParamUp / ParamDown / ParamModifier). The Mappings page's
        /// handler reads this on the event payload and routes to
        /// <c>RecorderService.StartRecordingExtraSourceParam</c>.</summary>
        public enum ParamRecordTarget { Up, Down, Modifier }
        public sealed class ParamRecordEventArgs : EventArgs
        {
            public ParamRecordTarget Target { get; }
            public ParamRecordEventArgs(ParamRecordTarget t) { Target = t; }
        }
        public event EventHandler<ParamRecordEventArgs> StartParamRecordingRequested;

        private RelayCommand _recordParamUpCommand;
        public RelayCommand RecordParamUpCommand =>
            _recordParamUpCommand ??= new RelayCommand(() =>
                StartParamRecordingRequested?.Invoke(this, new ParamRecordEventArgs(ParamRecordTarget.Up)));

        private RelayCommand _recordParamDownCommand;
        public RelayCommand RecordParamDownCommand =>
            _recordParamDownCommand ??= new RelayCommand(() =>
                StartParamRecordingRequested?.Invoke(this, new ParamRecordEventArgs(ParamRecordTarget.Down)));

        private RelayCommand _recordParamModifierCommand;
        public RelayCommand RecordParamModifierCommand =>
            _recordParamModifierCommand ??= new RelayCommand(() =>
                StartParamRecordingRequested?.Invoke(this, new ParamRecordEventArgs(ParamRecordTarget.Modifier)));

        private RelayCommand _clearCommand;
        /// <summary>Mirrors <see cref="MappingItem.ClearCommand"/>:
        /// resets descriptor + flags + deadzone to defaults but keeps
        /// the row in <c>ExtraSources</c>. Use the parent's
        /// RemoveExtraSourceCommand when the row should disappear
        /// entirely.</summary>
        public RelayCommand ClearCommand =>
            _clearCommand ??= new RelayCommand(() =>
            {
                Descriptor = "";
                DeviceGuid = "";
                DeviceLabel = "";
                Invert = false;
                HalfAxis = false;
                DeadZone = 50;
                _selectedInput = null;
                OnPropertyChanged(nameof(SelectedInput));
            });

        private RelayCommand _resetDeadZoneCommand;
        public RelayCommand ResetDeadZoneCommand =>
            _resetDeadZoneCommand ??= new RelayCommand(() => DeadZone = 50);

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
