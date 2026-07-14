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
        private bool _bidirectional;
        private int _deadZone = 50;
        private string _paramUp = "";
        private string _paramDown = "";
        private double _paramRate = 0.5;
        private bool _paramSticky = true;
        private double _paramMin;
        private double _paramMax = 1;
        private string _paramModifier = "";
        private double _gyroSensitivity = 1.0;
        private double _mouseCursorSensitivity = 1.0;
        private double _paramAttackTime = 0.30;
        private double _paramReleaseTime = 0.30;
        private bool _paramAutocenter = true;
        private double _paramReverseMultiplier = 4.0;

        public string Kind
        {
            get => _kind;
            set
            {
                if (SetProperty(ref _kind, value ?? "Direct"))
                {
                    OnPropertyChanged(nameof(IsIncrementalKind));
                    OnPropertyChanged(nameof(IsInvertOnHoldKind));
                    OnPropertyChanged(nameof(IsRampedKind));
                    OnPropertyChanged(nameof(UsesUpDownKeys));
                    OnPropertyChanged(nameof(IsKindDescriptorless));
                    OnPropertyChanged(nameof(ParamUpInputChoice));
                    OnPropertyChanged(nameof(ParamDownInputChoice));
                    OnPropertyChanged(nameof(ParamModifierInputChoice));
                }
            }
        }

        public bool IsIncrementalKind => string.Equals(_kind, "Incremental", StringComparison.Ordinal);
        public bool IsInvertOnHoldKind => string.Equals(_kind, "InvertOnHold", StringComparison.Ordinal);
        public bool IsRampedKind => string.Equals(_kind, "Ramped", StringComparison.Ordinal);

        /// <summary>True for the kinds authored via a positive (Up) and negative
        /// (Down) key pair: Incremental and Ramped. Drives the inline Up/Down
        /// pickers' visibility in the primary cell and the secondary rows.</summary>
        public bool UsesUpDownKeys => IsIncrementalKind || IsRampedKind;

        /// <summary>True for kinds where the source's main Descriptor +
        /// Invert / HalfAxis / DeadZone fields are unused (Incremental
        /// authors via Up/Down + Param*; InvertOnHold acts as a row-level
        /// modifier that only uses ParamModifier). Used by the XAML to
        /// collapse the redundant primary controls so the user only sees
        /// the kind-specific row below.</summary>
        public bool IsKindDescriptorless => IsIncrementalKind || IsInvertOnHoldKind || IsRampedKind;

        /// <summary>Whether any input actually feeds this source, per kind.
        /// Mirrors the engine's SourceEvaluator dispatch (the
        /// collect-descriptors switch in InputService): Incremental and
        /// Ramped read the Up/Down keys, InvertOnHold reads its Descriptor
        /// plus the modifier key, every other kind reads the Descriptor.</summary>
        public bool HasAnyBoundFeed
        {
            get
            {
                if (UsesUpDownKeys)
                    return !string.IsNullOrEmpty(ParamUp) || !string.IsNullOrEmpty(ParamDown);
                if (IsInvertOnHoldKind)
                    return !string.IsNullOrEmpty(Descriptor) || !string.IsNullOrEmpty(ParamModifier);
                return !string.IsNullOrEmpty(Descriptor);
            }
        }

        /// <summary>One entry in the user-facing Kind dropdown. <see cref="Value"/>
        /// is the schema/engine identifier ("Direct" / "Incremental" / "InvertOnHold")
        /// that round-trips through XML; <see cref="Name"/> is the space-separated
        /// label the user actually sees. Keeps backend identifiers out of the UI.</summary>
        public sealed class KindChoice
        {
            public string Value { get; init; }
            public string Name  { get; init; }
        }

        // Cached once per culture so the WPF Kind ComboBox bindings
        // don't reallocate three KindChoice objects + run three
        // ResourceManager lookups every time the list is re-read by
        // per-row binding refresh / virtualization. Invalidates on
        // culture change so language switches still take effect.
        private static KindChoice[] _kindOptionsCache;
        private static int _kindOptionsCacheCulture;
        public static System.Collections.Generic.IReadOnlyList<KindChoice> KindOptions
        {
            get
            {
                int currentCulture = System.Globalization.CultureInfo.CurrentUICulture.LCID;
                var cached = _kindOptionsCache;
                if (cached != null && _kindOptionsCacheCulture == currentCulture)
                    return cached;
                var arr = new[]
                {
                    new KindChoice { Value = "Direct",       Name = Strings.Instance.Pad_Mapping_Kind_Direct },
                    new KindChoice { Value = "Incremental",  Name = Strings.Instance.Pad_Mapping_Kind_Incremental },
                    new KindChoice { Value = "InvertOnHold", Name = Strings.Instance.Pad_Mapping_Kind_InvertOnHold },
                    new KindChoice { Value = "Ramped",       Name = Strings.Instance.Pad_Mapping_Kind_Ramped },
                };
                _kindOptionsCache = arr;
                _kindOptionsCacheCulture = currentCulture;
                return arr;
            }
        }

        internal MappingItem ParentMappingItem { get; set; }

        private InputChoice ResolveParamChoice(string descriptor)
        {
            if (ParentMappingItem == null || string.IsNullOrEmpty(descriptor)) return null;
            foreach (var c in ParentMappingItem.AvailableInputs)
                if (c != null && string.Equals(c.Descriptor, descriptor, StringComparison.Ordinal))
                    return c;
            return null;
        }

        // #111 audit fix A. A stateful kind (Ramp / Incremental) is keyed only by
        // (slot, target, srcIdx), so it needs a concrete DeviceGuid. With none, the
        // per-device eval treats it as "any device" and ticks the accumulator once
        // per assigned device (N-times-too-fast on a multi-device slot). Stamp the
        // device from the picked input when the source has none yet, mirroring the
        // SelectedInput setter. Recording already stamps it.
        private void StampDeviceFromParamChoice(InputChoice value)
        {
            if (string.IsNullOrEmpty(_deviceGuid) && !string.IsNullOrEmpty(value?.DeviceGuid))
            {
                DeviceGuid = value.DeviceGuid;
                DeviceLabel = value.DeviceLabel ?? "";
            }
        }

        public InputChoice ParamUpInputChoice
        {
            get => ResolveParamChoice(_paramUp);
            set
            {
                // A null write means the picker's current InputChoice could not be
                // resolved against the row's AvailableInputs (e.g. a keyboard key on
                // an axis row, or a transient list rebuild on navigation). WPF's
                // TwoWay ComboBox writes null back when its selected item leaves the
                // ItemsSource -- treating that as a clear silently wiped the stored
                // Up/Down key on every refresh (#160). Preserve the descriptor; only
                // a real choice (incl. an explicit empty-descriptor "none") changes it.
                if (value == null) return;
                var d = value.Descriptor ?? "";
                if (!string.Equals(_paramUp, d, StringComparison.Ordinal))
                {
                    _paramUp = d;
                    StampDeviceFromParamChoice(value);
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
                if (value == null) return; // see ParamUpInputChoice: don't let an unresolved picker wipe the key (#160)
                var d = value.Descriptor ?? "";
                if (!string.Equals(_paramDown, d, StringComparison.Ordinal))
                {
                    _paramDown = d;
                    StampDeviceFromParamChoice(value);
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
                if (value == null) return; // see ParamUpInputChoice: don't let an unresolved picker wipe the key (#160)
                var d = value.Descriptor ?? "";
                if (!string.Equals(_paramModifier, d, StringComparison.Ordinal))
                {
                    _paramModifier = d;
                    StampDeviceFromParamChoice(value);
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
            OnPropertyChanged(nameof(ParamUpDeviceLabel));
            OnPropertyChanged(nameof(ParamDownDeviceLabel));
            OnPropertyChanged(nameof(ParamModifierDeviceLabel));
        }

        /// <summary>Device label of the device whose button is currently
        /// stored in the corresponding Param field. Resolved against the
        /// parent MappingItem's AvailableInputs via the InputChoice picker
        /// bridge. Returns empty when no matching InputChoice is found
        /// (typical for empty Param fields).</summary>
        public string ParamUpDeviceLabel       => ParamUpInputChoice?.DeviceLabel ?? "";
        public string ParamDownDeviceLabel     => ParamDownInputChoice?.DeviceLabel ?? "";
        public string ParamModifierDeviceLabel => ParamModifierInputChoice?.DeviceLabel ?? "";
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

        /// <summary>Device label to DISPLAY for this source, honoring the
        /// "(Any device)" contract. For a concrete-guid source the picker's
        /// resolved choice wins (recovers the friendly name when the stored
        /// <see cref="DeviceLabel"/> was not hydrated). For an empty-guid
        /// ("any device") source the source's own label is used, never a
        /// concrete device borrowed from a descriptor-only picker fallback.
        /// Mirrors the primary row's PrimarySourceDeviceLabel, which resolves
        /// straight from its own guid. Read by the annotation overlays and the
        /// pipeline-chip tooltip so every device readout stays consistent with
        /// the per-source picker subtitle.</summary>
        public string DisplayDeviceLabel =>
            string.IsNullOrEmpty(_deviceGuid)
                ? _deviceLabel
                : (SelectedInput?.DeviceLabel ?? _deviceLabel);
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
                    OnPropertyChanged(nameof(IsHalfAxisApplicable));
                    OnPropertyChanged(nameof(IsGyroSource));
                    OnPropertyChanged(nameof(IsMouseCursorSource));
                    OnPropertyChanged(nameof(IsIrPointerSource));
                    OnPropertyChanged(nameof(IsMouseMotionSource));
                    OnPropertyChanged(nameof(IsGenericSensitivitySource));
                }
            }
        }

        /// <summary>True when this source's descriptor names a gyro axis
        /// ("Gyro Pitch" / "Gyro Yaw" / "Gyro Roll"). Drives the per-row
        /// gyro-sensitivity slider's visibility so it only renders when
        /// it's actually meaningful.</summary>
        public bool IsGyroSource => _descriptor != null
            && _descriptor.StartsWith("Gyro ", StringComparison.Ordinal);

        /// <summary>True when this source's descriptor is an absolute cursor axis
        /// ("Mouse Position X" / "Mouse Position Y", issue #107). Drives the
        /// per-source Mouse Cursor Sensitivity slider's visibility.</summary>
        public bool IsMouseCursorSource => _descriptor != null
            && _descriptor.StartsWith("Mouse Position ", StringComparison.Ordinal);

        /// <summary>True when this source's descriptor is a Wii IR pointer axis
        /// ("IR Pointer X" / "IR Pointer Y", issue #146). Drives the per-source IR
        /// Pointer Sensitivity slider's visibility.</summary>
        public bool IsIrPointerSource => _descriptor != null
            && _descriptor.StartsWith("IR Pointer ", StringComparison.Ordinal);

        /// <summary>True for "Mouse Motion X/Y" (issue #154). Drives the
        /// per-source sensitivity slider's visibility, which shares the
        /// stored field with the IR pointer's slider (both scale their
        /// family's read in SourceCoercion).</summary>
        public bool IsMouseMotionSource => _descriptor != null
            && _descriptor.StartsWith("Mouse Motion ", StringComparison.Ordinal);

        /// <summary>True when this source carries the generic per-source
        /// Sensitivity knob (issue #9): a plain "Axis N" / "Slider N" read, or
        /// an abstract Gamepad stick / trigger that canonicalizes to one.
        /// Drives that slider's visibility, mutually exclusive with the gyro /
        /// mouse / IR predicates above.</summary>
        public bool IsGenericSensitivitySource =>
            PadForge.Engine.Common.Mapping.SourceCoercion.IsGenericSensitivityDescriptor(_descriptor);

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
                // Abstract Gamepad button/D-pad aliases (#9) fold to their
                // canonical "Button N" / "POV 0 Dir" form first.
                d = PadForge.Engine.Common.Mapping.SourceCoercion.ResolveGamepadAlias(d) ?? d;
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

        /// <summary>True when the Half checkbox (and the dependent Either)
        /// is meaningful for this source. Half-axis only applies to
        /// continuous-range sources: Axis, Slider, Touchpad X/Y/Pressure,
        /// and Gyro Pitch/Yaw/Roll. Discrete sources (Button, POV
        /// direction, Touchpad Click / Finger Down) have no upper or
        /// lower half to pick.</summary>
        public bool IsHalfAxisApplicable
        {
            get
            {
                var d = _descriptor?.Trim() ?? "";
                if (d.Length == 0) return false;

                // Gyro is always axis-like.
                if (d.StartsWith("Gyro ", System.StringComparison.Ordinal))
                    return true;

                // Joy-Con 2 mouse motion is a signed velocity: Half picks ONE
                // direction and Invert chooses which (right/down vs left/up),
                // which is how issue #154's four-way "weapon wheel" is built.
                if (d.StartsWith("Mouse Motion ", System.StringComparison.Ordinal))
                    return true;

                // Touchpad: X / Y / Pressure (whole-pad or the #9 B-1
                // half-windowed X/Y variants) are continuous; Click and
                // Finger Down (windowed included) are discrete. The finger
                // predicate covers the axis spellings, halves included,
                // and never matches the bool forms.
                if (d.StartsWith("Touchpad ", System.StringComparison.Ordinal))
                {
                    return PadForge.Engine.Common.Mapping.SourceCoercion
                            .IsTouchpadFingerAxisDescriptor(d)
                        || d.EndsWith(" Pressure", System.StringComparison.Ordinal);
                }

                // Strip leading I / H prefix flags before checking the type
                // token. Both cases, matching IsDeadZoneApplicable below and
                // the engine's OrdinalIgnoreCase prefix parse
                // (TryGetEngineOwnedSource), so a hand-edited lowercase
                // prefix that evaluates fine doesn't hide the checkbox.
                int start = 0;
                if (start < d.Length && (d[start] == 'I' || d[start] == 'i')) start++;
                if (start < d.Length && (d[start] == 'H' || d[start] == 'h')) start++;
                // "Axis N" / "Slider N" plus the abstract Gamepad sticks /
                // triggers that canonicalize to one (#9).
                return PadForge.Engine.Common.Mapping.SourceCoercion
                    .IsGenericSensitivityDescriptor(d.Substring(start));
            }
        }

        /// <summary>When <c>true</c> AND <see cref="HalfAxis"/> is also on,
        /// the axis-to-button check fires on absolute deflection past the
        /// deadzone — either side of center counts. <see cref="Invert"/>
        /// has no effect in this mode.</summary>
        public bool Bidirectional { get => _bidirectional; set => SetProperty(ref _bidirectional, value); }
        public int DeadZone
        {
            // Minimum 1: a 0% axis-to-button deadzone is disallowed. The engine
            // and load/save paths treat DeadZone == 0 as "unset" and fall back to
            // the default, so a user-set 0 silently reverted to 50%. Clamping at 1
            // keeps every value meaningful.
            get => _deadZone;
            set => SetProperty(ref _deadZone, System.Math.Clamp(value, 1, 100));
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

                // Engine-owned continuous families whose button-thresholding
                // reads the per-source DeadZone (SourceCoercion's button
                // branches use "src.DeadZone > 0 ? src.DeadZone : global").
                // Without this the column was hidden and the per-row
                // threshold those branches honor was never user-settable
                // (issue #154's "small deadzone" wheel, and retroactively
                // the IR / Balance rows shipped with #146/#151).
                if (desc.StartsWith("Mouse Motion ", System.StringComparison.Ordinal)
                    || desc.StartsWith("IR Pointer ", System.StringComparison.Ordinal)
                    || desc.StartsWith("IR Brightness", System.StringComparison.Ordinal)
                    || desc.StartsWith("Balance ", System.StringComparison.Ordinal)
                    || desc.StartsWith("Gyro ", System.StringComparison.Ordinal)
                    || desc.StartsWith("Mouse Position ", System.StringComparison.Ordinal))
                    return _parentTargetIsDiscrete;

                // MIDI CC as a button thresholds on the per-source DeadZone
                // ("Midi CC N", absolute value only; the " Up"/" Down"
                // encoder pulses are edge-fired and read no threshold).
                if (desc.StartsWith("Midi CC ", System.StringComparison.Ordinal)
                    && !desc.EndsWith(" Up", System.StringComparison.Ordinal)
                    && !desc.EndsWith(" Down", System.StringComparison.Ordinal))
                    return _parentTargetIsDiscrete;

                // Touchpad-gesture CONTINUOUS axes (PinchAxis / RotateAxis /
                // StickX / StickY) threshold on the per-source DeadZone when
                // driving a button target; one-shot gesture fires ignore it.
                if (PadForge.Engine.Common.Mapping.SourceCoercion.IsTouchpadGestureDescriptor(desc)
                    && PadForge.Engine.Common.Mapping.SourceCoercion.TryParseTouchpadGesture(desc, out _, out string gestureName)
                    && PadForge.Engine.Common.Mapping.SourceCoercion.IsTouchpadGestureAxis(gestureName))
                    return _parentTargetIsDiscrete;

                int start = 0;
                if (start < desc.Length && (desc[start] == 'I' || desc[start] == 'i')) start++;
                if (start < desc.Length && (desc[start] == 'H' || desc[start] == 'h')) start++;
                // Touchpad finger X/Y joined the generic Sensitivity family
                // (#9 B-13) but have no axis-to-button threshold read
                // (ReadAsBool's touchpad branch reads Click / "Finger M
                // Down" only), so every remaining touchpad descriptor keeps
                // the pre-widening hidden deadzone. The gesture CONTINUOUS
                // axes above already opted in explicitly. Tested on the
                // same stripped body the predicate below receives.
                if (desc.AsSpan(start).StartsWith("Touchpad ", System.StringComparison.Ordinal))
                    return false;
                // "Axis N" / "Slider N" plus the abstract Gamepad sticks /
                // triggers that canonicalize to one (#9).
                if (!PadForge.Engine.Common.Mapping.SourceCoercion
                        .IsGenericSensitivityDescriptor(desc.Substring(start)))
                    return false;
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
                {
                    OnPropertyChanged(nameof(ParamUpInputChoice));
                    OnPropertyChanged(nameof(ParamUpDeviceLabel));
                }
            }
        }
        public string ParamDown
        {
            get => _paramDown;
            set
            {
                if (SetProperty(ref _paramDown, value ?? ""))
                {
                    OnPropertyChanged(nameof(ParamDownInputChoice));
                    OnPropertyChanged(nameof(ParamDownDeviceLabel));
                }
            }
        }
        public double ParamRate { get => _paramRate; set => SetProperty(ref _paramRate, value); }

        /// <summary>Per-source gyro sensitivity multiplier. Only applied
        /// for Gyro descriptors (see <see cref="IsGyroSource"/>). UI shows
        /// the slider gated on that predicate. Default 1.0 = the engine's
        /// 500°/s → ±1 deflection scale.</summary>
        public double GyroSensitivity
        {
            get => _gyroSensitivity;
            set => SetProperty(ref _gyroSensitivity, System.Math.Clamp(value, 0.1, 10.0));
        }

        /// <summary>Per-source mouse-cursor sensitivity (issue #107). Only applied
        /// for "Mouse Position X/Y" descriptors (see <see cref="IsMouseCursorSource"/>).
        /// Default 1.0 = full deflection at 10% of screen width from center.</summary>
        public double MouseCursorSensitivity
        {
            get => _mouseCursorSensitivity;
            set => SetProperty(ref _mouseCursorSensitivity, System.Math.Clamp(value, 0.1, 5.0));
        }

        private double _irPointerSensitivity = 1.0;
        /// <summary>Per-source Wii IR-pointer sensitivity (issue #146). Only applied
        /// for "IR Pointer X/Y" descriptors. Default 1.0 = full deflection at the
        /// edge of the camera's field of view.</summary>
        public double IrPointerSensitivity
        {
            get => _irPointerSensitivity;
            set => SetProperty(ref _irPointerSensitivity, System.Math.Clamp(value, 0.1, 5.0));
        }

        private double _sensitivity = 1.0;
        /// <summary>Generic per-source sensitivity (issue #9). Only applied for
        /// plain analog sources (see <see cref="IsGenericSensitivitySource"/>):
        /// "Axis N" / "Slider N" and the abstract Gamepad sticks / triggers that
        /// canonicalize to them. Default 1.0 = unchanged.</summary>
        public double Sensitivity
        {
            get => _sensitivity;
            set => SetProperty(ref _sensitivity, System.Math.Clamp(value, 0.1, 5.0));
        }
        public bool ParamSticky { get => _paramSticky; set => SetProperty(ref _paramSticky, value); }
        public double ParamMin { get => _paramMin; set => SetProperty(ref _paramMin, value); }
        public double ParamMax { get => _paramMax; set => SetProperty(ref _paramMax, value); }

        /// <summary>Ramped attack time in seconds (issue #111). 0 to 5; the UI slider
        /// runs 0 to 2. Time for the axis to travel 0 to ±1 while the matching key is
        /// held. Only meaningful when <see cref="IsRampedKind"/>.</summary>
        public double ParamAttackTime
        {
            get => _paramAttackTime;
            set => SetProperty(ref _paramAttackTime, System.Math.Clamp(value, 0, 5));
        }

        /// <summary>Ramped release time in seconds (issue #111). 0 to 5. Time for the
        /// axis to travel ±1 back to 0 after release.</summary>
        public double ParamReleaseTime
        {
            get => _paramReleaseTime;
            set => SetProperty(ref _paramReleaseTime, System.Math.Clamp(value, 0, 5));
        }

        /// <summary>Ramped autocenter (issue #111). True = release ramps back to zero;
        /// false = the axis cruises (holds its last value). Gates the reverse speed-up.</summary>
        public bool ParamAutocenter
        {
            get => _paramAutocenter;
            set => SetProperty(ref _paramAutocenter, value);
        }

        /// <summary>Ramped reverse speed-up multiplier (issue #111). 1 to 10. Applied
        /// to the toward-zero step when switching directions while still on the
        /// original side. 1 disables the speed-up.</summary>
        public double ParamReverseMultiplier
        {
            get => _paramReverseMultiplier;
            set => SetProperty(ref _paramReverseMultiplier, System.Math.Clamp(value, 1, 10));
        }
        public string ParamModifier
        {
            get => _paramModifier;
            set
            {
                if (SetProperty(ref _paramModifier, value ?? ""))
                {
                    OnPropertyChanged(nameof(ParamModifierInputChoice));
                    OnPropertyChanged(nameof(ParamModifierDeviceLabel));
                }
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
                    // Straight guid equality, INCLUDING the empty guid: an
                    // empty-guid ("any device") source genuinely matches
                    // the picker's empty-guid "(Any device)" entry (whose
                    // label is the same "(Any device)" sentinel the load
                    // path stamps), never a concrete device's entry.
                    if (string.Equals(choice.DeviceGuid ?? "", wantGuid, StringComparison.OrdinalIgnoreCase))
                    {
                        match = choice;
                        break;
                    }
                }
                var picked = match ?? descriptorOnlyMatch;
                _selectedInput = picked;
                // Only adopt a device label when the picked choice matched on
                // GUID. A descriptor-only fallback (empty guid = "any device",
                // or a concrete guid whose device is offline) must NOT borrow
                // the fallback device's name. Doing so made empty-guid secondary
                // sources from a Workshop import display the slot's first
                // concrete controller instead of "(Any device)". The primary row
                // resolves its label straight from its own guid and never did
                // this, so gating here unifies the two paths.
                if (match != null && !string.IsNullOrEmpty(match.DeviceLabel))
                    DeviceLabel = match.DeviceLabel;
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
                    if (!value) _activeParamTarget = null; // recording stopped → no param is active
                    OnPropertyChanged(nameof(RecordButtonText));
                    OnPropertyChanged(nameof(RecordButtonIcon));
                    OnPropertyChanged(nameof(IsRecordingParamUp));
                    OnPropertyChanged(nameof(IsRecordingParamDown));
                    OnPropertyChanged(nameof(IsRecordingParamModifier));
                    OnPropertyChanged(nameof(RecordIconParamUp));
                    OnPropertyChanged(nameof(RecordIconParamDown));
                    OnPropertyChanged(nameof(RecordIconParamModifier));
                    OnPropertyChanged(nameof(RecordTextParamUp));
                    OnPropertyChanged(nameof(RecordTextParamDown));
                    OnPropertyChanged(nameof(RecordTextParamModifier));
                }
            }
        }

        // Per-Param recording state. Set by the Param Record commands so each
        // button only swaps to its "stop" icon while ITS recording is armed —
        // not when a sibling button (e.g. Up vs Down on an Incremental row) is
        // the one listening. Cleared automatically when IsRecording goes false.
        private ParamRecordTarget? _activeParamTarget;
        public bool IsRecordingParamUp       => IsRecording && _activeParamTarget == ParamRecordTarget.Up;
        public bool IsRecordingParamDown     => IsRecording && _activeParamTarget == ParamRecordTarget.Down;
        public bool IsRecordingParamModifier => IsRecording && _activeParamTarget == ParamRecordTarget.Modifier;
        // Segoe MDL2 glyphs — match the literal codepoints the existing
        // RecordButtonIcon already uses: U+E71A = Stop, U+E7C8 = Record.
        public string RecordIconParamUp       => IsRecordingParamUp       ? "" : "";
        public string RecordIconParamDown     => IsRecordingParamDown     ? "" : "";
        public string RecordIconParamModifier => IsRecordingParamModifier ? "" : "";
        public string RecordTextParamUp       => IsRecordingParamUp       ? Strings.Instance.Common_Recording : Strings.Instance.Common_Record;
        public string RecordTextParamDown     => IsRecordingParamDown     ? Strings.Instance.Common_Recording : Strings.Instance.Common_Record;
        public string RecordTextParamModifier => IsRecordingParamModifier ? Strings.Instance.Common_Recording : Strings.Instance.Common_Record;

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

        /// <summary>Param record commands toggle on a second click — matches
        /// the main ToggleRecordCommand pattern. Each command also stamps
        /// <see cref="_activeParamTarget"/> so the icon swap only happens
        /// on the button that's actually listening (Up vs Down record
        /// buttons no longer both light up when only one is armed).</summary>
        private RelayCommand _recordParamUpCommand;
        public RelayCommand RecordParamUpCommand =>
            _recordParamUpCommand ??= new RelayCommand(() => StartParamRecord(ParamRecordTarget.Up));

        private RelayCommand _recordParamDownCommand;
        public RelayCommand RecordParamDownCommand =>
            _recordParamDownCommand ??= new RelayCommand(() => StartParamRecord(ParamRecordTarget.Down));

        private RelayCommand _recordParamModifierCommand;
        public RelayCommand RecordParamModifierCommand =>
            _recordParamModifierCommand ??= new RelayCommand(() => StartParamRecord(ParamRecordTarget.Modifier));

        private void StartParamRecord(ParamRecordTarget target)
        {
            if (IsRecording) { StopRecordingRequested?.Invoke(this, EventArgs.Empty); return; }
            _activeParamTarget = target;
            // No SetProperty here — IsRecording isn't true yet; the handler
            // will set it. Fire OnPropertyChanged for the per-Param props
            // anyway so the icon updates synchronously with the click for
            // immediate feedback (handlers route through the recorder which
            // sets IsRecording=true a moment later).
            OnPropertyChanged(nameof(IsRecordingParamUp));
            OnPropertyChanged(nameof(IsRecordingParamDown));
            OnPropertyChanged(nameof(IsRecordingParamModifier));
            OnPropertyChanged(nameof(RecordIconParamUp));
            OnPropertyChanged(nameof(RecordIconParamDown));
            OnPropertyChanged(nameof(RecordIconParamModifier));
            OnPropertyChanged(nameof(RecordTextParamUp));
            OnPropertyChanged(nameof(RecordTextParamDown));
            OnPropertyChanged(nameof(RecordTextParamModifier));
            StartParamRecordingRequested?.Invoke(this, new ParamRecordEventArgs(target));
        }

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
                Bidirectional = false;
                DeadZone = 50;
                _selectedInput = null;
                OnPropertyChanged(nameof(SelectedInput));
            });

        private RelayCommand _resetDeadZoneCommand;
        public RelayCommand ResetDeadZoneCommand =>
            _resetDeadZoneCommand ??= new RelayCommand(() => DeadZone = 50);

        private RelayCommand _resetMouseCursorSensitivityCommand;
        public RelayCommand ResetMouseCursorSensitivityCommand =>
            _resetMouseCursorSensitivityCommand ??= new RelayCommand(() => MouseCursorSensitivity = 1.0);
        private RelayCommand _resetIrPointerSensitivityCommand;
        public RelayCommand ResetIrPointerSensitivityCommand =>
            _resetIrPointerSensitivityCommand ??= new RelayCommand(() => IrPointerSensitivity = 1.0);

        private RelayCommand _resetGyroSensitivityCommand;
        public RelayCommand ResetGyroSensitivityCommand =>
            _resetGyroSensitivityCommand ??= new RelayCommand(() => GyroSensitivity = 1.0);

        private RelayCommand _resetSensitivityCommand;
        public RelayCommand ResetSensitivityCommand =>
            _resetSensitivityCommand ??= new RelayCommand(() => Sensitivity = 1.0);

        /// <summary>Builds a domain <see cref="Engine.Data.MappingSource"/>
        /// from this VM's current values. Used by the Save pipeline.
        /// N/A by design: the steering Param* set (ParamYDescriptor,
        /// ParamStickDeadzone, ParamWind*, ParamAngle*, ParamMotion*,
        /// ParamControllerOrientation) and NoInherit are NOT round-tripped
        /// here. KindOptions offers only Direct/Incremental/InvertOnHold/
        /// Ramped, so an ExtraSource can never author a steering kind; those
        /// kinds are set on StickConfigItem via ApplySteeringKindToRow, and
        /// NoInherit lives on MappingRow. If a steering kind ever becomes
        /// selectable here, add its params to BOTH ToDomain and FromDomain
        /// or they drop silently through the VM round-trip.</summary>
        public Engine.Data.MappingSource ToDomain() => new()
        {
            Kind = _kind ?? "Direct",
            DeviceGuid = _deviceGuid ?? "",
            Descriptor = _descriptor ?? "",
            Invert = _invert,
            HalfAxis = _halfAxis,
            Bidirectional = _bidirectional,
            DeadZone = _deadZone,
            ParamUp = _paramUp ?? "",
            ParamDown = _paramDown ?? "",
            ParamRate = _paramRate,
            ParamSticky = _paramSticky,
            ParamMin = _paramMin,
            ParamMax = _paramMax,
            ParamModifier = _paramModifier ?? "",
            GyroSensitivity = _gyroSensitivity,
            MouseCursorSensitivity = _mouseCursorSensitivity,
            IrPointerSensitivity = _irPointerSensitivity,
            Sensitivity = _sensitivity,
            ParamAttackTime = _paramAttackTime,
            ParamReleaseTime = _paramReleaseTime,
            ParamAutocenter = _paramAutocenter,
            ParamReverseMultiplier = _paramReverseMultiplier,
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
                Bidirectional = src.Bidirectional,
                DeadZone = src.DeadZone,
                ParamUp = src.ParamUp ?? "",
                ParamDown = src.ParamDown ?? "",
                ParamRate = src.ParamRate,
                ParamSticky = src.ParamSticky,
                ParamMin = src.ParamMin,
                ParamMax = src.ParamMax,
                ParamModifier = src.ParamModifier ?? "",
                GyroSensitivity = src.GyroSensitivity > 0 ? src.GyroSensitivity : 1.0,
                MouseCursorSensitivity = src.MouseCursorSensitivity > 0 ? src.MouseCursorSensitivity : 1.0,
                IrPointerSensitivity = src.IrPointerSensitivity > 0 ? src.IrPointerSensitivity : 1.0,
                Sensitivity = src.Sensitivity > 0 ? src.Sensitivity : 1.0,
                ParamAttackTime = src.ParamAttackTime,
                ParamReleaseTime = src.ParamReleaseTime,
                ParamAutocenter = src.ParamAutocenter,
                ParamReverseMultiplier = src.ParamReverseMultiplier >= 1 ? src.ParamReverseMultiplier : 4.0,
            };
        }
    }
}
