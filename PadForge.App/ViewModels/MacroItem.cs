using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// Represents a single macro: a trigger combination of controller inputs
    /// that produces a sequence of output actions (button presses, key presses,
    /// delays, or repeated inputs).
    ///
    /// Macros are evaluated in the pipeline between Step 3 (mapping) and
    /// Step 4 (combining). When the trigger condition is met, the macro's
    /// actions are injected into the Gamepad state.
    /// </summary>
    public class MacroItem : ObservableObject
    {
        // ─────────────────────────────────────────────
        //  Identity
        // ─────────────────────────────────────────────

        public MacroItem()
        {
            Strings.CultureChanged += OnCultureChanged;
        }

        private void OnCultureChanged()
        {
            OnPropertyChanged(nameof(RecordTriggerButtonText));
            OnPropertyChanged(nameof(RecordTriggerIcon));
            OnPropertyChanged(nameof(TriggerDisplayText));
            _outputChannelOptions = null;
            OnPropertyChanged(nameof(OutputChannelOptions));
        }

        // Cached OutputController-dropdown items for the variable rows.
        // Invalidated when ButtonStyle changes or the active culture flips.
        private List<MacroOutputChannelOption> _outputChannelOptions;

        /// <summary>Style- and culture-aware list of channels for the
        /// per-variable OutputController dropdown. Labels mirror the
        /// mapping table for the same VC family (e.g. PlayStation slot
        /// → ✕/○/◻/△ + L1/R1/L2/R2/Share/Options/PS; Extended slot →
        /// Button 1 / Button 2 / …; Xbox slot → A/B/X/Y + Left Shoulder
        /// / Right Shoulder / etc.).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public IList<MacroOutputChannelOption> OutputChannelOptions
        {
            get => _outputChannelOptions ??= MacroOutputChannelNames.GetOptions(_buttonStyle);
        }

        private string _name = Strings.Instance.Macro_NewMacro;

        /// <summary>User-facing name for this macro.</summary>
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        /// <summary>Pad index (0-based) of the slot that owns this macro.
        /// Set when the macro is added to a slot's collection or loaded
        /// from XML. Used by <see cref="MacroActionType.LightbarColor"/>
        /// to resolve the target <c>PlayStationSlotConfig</c> at fire
        /// time. Not serialized — the parent <c>MacroData.PadIndex</c> is
        /// the persisted source of truth and gets reapplied on load.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public int PadIndex { get; set; } = -1;

        private bool _isEnabled = true;

        /// <summary>Whether this macro is active.</summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        // ─────────────────────────────────────────────
        //  Trigger condition
        //  A combination of buttons that must ALL be pressed
        //  simultaneously to fire the macro.
        // ─────────────────────────────────────────────

        /// <summary>
        /// Trigger buttons — all must be pressed simultaneously.
        /// Uses Gamepad button flag constants (e.g., Gamepad.A | Gamepad.B).
        /// </summary>
        private ushort _triggerButtons;

        public ushort TriggerButtons
        {
            get => _triggerButtons;
            set
            {
                if (SetProperty(ref _triggerButtons, value))
                    OnPropertyChanged(nameof(TriggerDisplayText));
            }
        }

        private uint[] _triggerCustomButtonWords = new uint[4];

        /// <summary>
        /// For custom Extended OutputController triggers: wide button bitmask (128 buttons).
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public uint[] TriggerCustomButtonWords
        {
            get => _triggerCustomButtonWords;
            set
            {
                _triggerCustomButtonWords = value ?? new uint[4];
                OnPropertyChanged(nameof(TriggerDisplayText));
            }
        }

        /// <summary>Serializable hex form of TriggerCustomButtonWords.</summary>
        public string TriggerCustomButtons
        {
            get
            {
                if (_triggerCustomButtonWords.All(w => w == 0)) return null;
                return string.Join(",", _triggerCustomButtonWords.Select(w => w.ToString("X8")));
            }
            set
            {
                _triggerCustomButtonWords = new uint[4];
                if (string.IsNullOrEmpty(value)) return;
                var parts = value.Split(',');
                for (int i = 0; i < 4 && i < parts.Length; i++)
                    if (uint.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out var w))
                        _triggerCustomButtonWords[i] = w;
            }
        }

        /// <summary>True if any custom trigger button is set.</summary>
        public bool UsesCustomTrigger => _triggerCustomButtonWords.Any(w => w != 0);

        private MacroTriggerSource _triggerSource = MacroTriggerSource.InputDevice;

        /// <summary>
        /// Whether the trigger records from the physical input device (raw buttons)
        /// or from the slot's combined virtual controller output.
        /// </summary>
        public MacroTriggerSource TriggerSource
        {
            get => _triggerSource;
            set => SetProperty(ref _triggerSource, value);
        }

        /// <summary>Human-readable display of the trigger combo. For
        /// multi-device combos (multi-device <see cref="TriggerInputs"/>
        /// list) inputs are grouped by device and rendered as
        /// "Device A [Btn X + Btn Y] + Device B [Key A]". For legacy
        /// single-device triggers the format is the historical one
        /// "Btn X + Btn Y (DeviceName)".</summary>
        public string TriggerDisplayText
        {
            get
            {
                var entries = GetTriggerInputEntries();
                bool multiDevice = entries.Count > 0
                    && entries.Select(e => e.DeviceGuid).Distinct().Count() > 1;

                var parts = new List<string>();

                if (multiDevice)
                {
                    // Group entries by device, render each group with the
                    // device name as a prefix so the user can see at a
                    // glance which input came from where.
                    var byDevice = entries.GroupBy(e => e.DeviceGuid);
                    foreach (var grp in byDevice)
                    {
                        var objects = ResolveDeviceObjects(grp.Key);
                        var inputs = new List<string>();
                        foreach (var entry in grp)
                        {
                            if (entry.RawButton >= 0)
                            {
                                var obj = objects?.FirstOrDefault(o => o.IsButton && o.InputIndex == entry.RawButton);
                                inputs.Add(obj != null && !string.IsNullOrEmpty(obj.Name)
                                    ? obj.Name
                                    : string.Format(Strings.Instance.Macro_Button_Format, entry.RawButton));
                            }
                            else if (!string.IsNullOrEmpty(entry.Pov))
                            {
                                inputs.Add(FormatPovTrigger(entry.Pov));
                            }
                            else if (entry.AxisTarget != MacroAxisTarget.None)
                            {
                                var tags = new List<string>();
                                if (entry.HalfAxis) tags.Add(Strings.Instance.Macro_Axis_Half);
                                if (entry.HalfAxis && entry.Bidirectional) tags.Add(Strings.Instance.Pad_Either.ToLowerInvariant());
                                if (entry.Invert && !(entry.HalfAxis && entry.Bidirectional)) tags.Add(Strings.Instance.Macro_Axis_Inverted);
                                string tagText = tags.Count > 0 ? $" ({string.Join(", ", tags)})" : "";
                                inputs.Add($"{entry.AxisTarget.DisplayName()} > {entry.DeadZone}%{tagText}");
                            }
                        }
                        string deviceName = ResolveDeviceName(grp.Key);
                        if (!string.IsNullOrEmpty(deviceName))
                            parts.Add(deviceName + " [" + string.Join(" + ", inputs) + "]");
                        else
                            parts.Add(string.Join(" + ", inputs));
                    }
                }
                else if (entries.Count > 0)
                {
                    // Single-device multi-input case via the new list.
                    var grp = entries.GroupBy(e => e.DeviceGuid).First();
                    var objects = ResolveDeviceObjects(grp.Key);
                    foreach (var entry in grp)
                    {
                        if (entry.RawButton >= 0)
                        {
                            var obj = objects?.FirstOrDefault(o => o.IsButton && o.InputIndex == entry.RawButton);
                            parts.Add(obj != null && !string.IsNullOrEmpty(obj.Name)
                                ? obj.Name
                                : string.Format(Strings.Instance.Macro_Button_Format, entry.RawButton));
                        }
                        else if (!string.IsNullOrEmpty(entry.Pov))
                        {
                            parts.Add(FormatPovTrigger(entry.Pov));
                        }
                        else if (entry.AxisTarget != MacroAxisTarget.None)
                        {
                            var tags = new List<string>();
                            if (entry.HalfAxis) tags.Add(Strings.Instance.Macro_Axis_Half);
                            if (entry.Invert)   tags.Add(Strings.Instance.Macro_Axis_Inverted);
                            string tagText = tags.Count > 0 ? $" ({string.Join(", ", tags)})" : "";
                            parts.Add($"{entry.AxisTarget.DisplayName()} > {entry.DeadZone}%{tagText}");
                        }
                    }
                }
                else
                {
                    // Legacy single-device path (no multi-device entries).
                    if (UsesRawTrigger)
                    {
                        var objects = ResolveDeviceObjects(_triggerDeviceGuid);
                        foreach (int b in _triggerRawButtons)
                        {
                            var obj = objects?.FirstOrDefault(o => o.IsButton && o.InputIndex == b);
                            parts.Add(obj != null && !string.IsNullOrEmpty(obj.Name) ? obj.Name : string.Format(Strings.Instance.Macro_Button_Format, b));
                        }
                    }
                    else if (_buttonStyle == MacroButtonStyle.Numbered && UsesCustomTrigger)
                    {
                        parts.Add(MacroButtonNames.FormatCustomButtons(_triggerCustomButtonWords));
                    }
                    else if (_triggerButtons != 0)
                    {
                        parts.Add(MacroButtonNames.FormatButtons(_triggerButtons, _buttonStyle));
                    }

                    // POV part(s).
                    foreach (var pov in _triggerPovs)
                        parts.Add(FormatPovTrigger(pov));
                }

                // Axis part(s) — always Xbox-output, no per-device split.
                foreach (var axis in _triggerAxisTargets)
                    parts.Add($"{axis.DisplayName()} > {_triggerAxisThreshold}%");

                if (parts.Count == 0) return Strings.Instance.Macro_NotSet;

                string result = string.Join(" + ", parts);

                // Append source device name at end ONLY for single-device legacy /
                // single-device new-list cases. Multi-device already shows names
                // inline.
                if (!multiDevice && (UsesRawTrigger || UsesPovTrigger || UsesAxisTrigger))
                {
                    Guid deviceGuid = entries.Count > 0 ? entries[0].DeviceGuid : _triggerDeviceGuid;
                    string deviceName = ResolveDeviceName(deviceGuid);
                    if (!string.IsNullOrEmpty(deviceName))
                        result = $"{result} ({deviceName})";
                }

                return result;
            }
        }

        /// <summary>
        /// Resolves a device GUID to a human-readable name via SettingsManager.
        /// Returns null if the device is not found.
        /// </summary>
        private static string ResolveDeviceName(Guid deviceGuid)
        {
            if (deviceGuid == Guid.Empty) return null;
            var ud = SettingsManager.FindDeviceByInstanceGuid(deviceGuid);
            return ud?.ResolvedName;
        }

        private static DeviceObjectItem[] ResolveDeviceObjects(Guid deviceGuid)
        {
            if (deviceGuid == Guid.Empty) return null;
            var ud = SettingsManager.FindDeviceByInstanceGuid(deviceGuid);
            return ud?.DeviceObjects;
        }

        /// <summary>
        /// Formats a stored POV trigger ("povIndex:centidegrees") to display text ("POV 0 Up").
        /// </summary>
        internal static string FormatPovTrigger(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            var split = stored.Split(':');
            if (split.Length != 2 || !int.TryParse(split[0], out int idx) || !int.TryParse(split[1], out int cd))
                return stored;
            return string.Format(Strings.Instance.Macro_POV_Format, idx, CentidegreesToDirection(cd));
        }

        /// <summary>
        /// Parses a stored POV trigger ("povIndex:centidegrees") into its components.
        /// </summary>
        internal static bool ParsePovTrigger(string stored, out int povIndex, out int centidegrees)
        {
            povIndex = -1; centidegrees = -1;
            if (string.IsNullOrEmpty(stored)) return false;
            var split = stored.Split(':');
            return split.Length == 2
                && int.TryParse(split[0], out povIndex)
                && int.TryParse(split[1], out centidegrees);
        }

        private static string CentidegreesToDirection(int centidegrees)
        {
            if (centidegrees < 0) return Strings.Instance.POV_Centered;
            centidegrees %= 36000;
            if (centidegrees >= 33750 || centidegrees < 2250) return Strings.Instance.POV_Up;
            if (centidegrees < 6750) return Strings.Instance.POV_UpRight;
            if (centidegrees < 11250) return Strings.Instance.POV_Right;
            if (centidegrees < 15750) return Strings.Instance.POV_DownRight;
            if (centidegrees < 20250) return Strings.Instance.POV_Down;
            if (centidegrees < 24750) return Strings.Instance.POV_DownLeft;
            if (centidegrees < 29250) return Strings.Instance.POV_Left;
            if (centidegrees < 33750) return Strings.Instance.POV_UpLeft;
            return Strings.Instance.POV_Up;
        }

        // ─────────────────────────────────────────────
        //  Raw device button trigger (alternative path)
        //  When set, the macro fires based on raw device-specific buttons
        //  rather than the Xbox-mapped bitmask above.
        // ─────────────────────────────────────────────

        private Guid _triggerDeviceGuid;

        /// <summary>
        /// GUID of the device whose raw buttons are the trigger source.
        /// <see cref="Guid.Empty"/> = use legacy Xbox bitmask path.
        /// </summary>
        public Guid TriggerDeviceGuid
        {
            get => _triggerDeviceGuid;
            set
            {
                if (SetProperty(ref _triggerDeviceGuid, value))
                    OnPropertyChanged(nameof(TriggerDisplayText));
            }
        }

        private int[] _triggerRawButtons = Array.Empty<int>();

        /// <summary>
        /// Raw button indices that must all be pressed simultaneously.
        /// E.g. [13, 14] for DualSense touchpad + mic buttons.
        /// </summary>
        public int[] TriggerRawButtons
        {
            get => _triggerRawButtons;
            set
            {
                if (SetProperty(ref _triggerRawButtons, value ?? Array.Empty<int>()))
                    OnPropertyChanged(nameof(TriggerDisplayText));
            }
        }

        /// <summary>True if this macro uses the raw device button trigger path —
        /// either via the multi-device <see cref="TriggerInputs"/> spec or the
        /// legacy single-device <see cref="TriggerDeviceGuid"/> /
        /// <see cref="TriggerRawButtons"/> pair.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool UsesRawTrigger
        {
            get
            {
                var entries = GetTriggerInputEntries();
                for (int i = 0; i < entries.Count; i++)
                    if (entries[i].RawButton >= 0) return true;
                return _triggerDeviceGuid != Guid.Empty && _triggerRawButtons.Length > 0;
            }
        }

        private string[] _triggerPovs = Array.Empty<string>();

        /// <summary>
        /// POV hat triggers stored as "povIndex:centidegrees" (e.g. "0:0" for POV 0 Up).
        /// All must be active simultaneously.
        /// </summary>
        public string[] TriggerPovs
        {
            get => _triggerPovs;
            set
            {
                _triggerPovs = value ?? Array.Empty<string>();
                OnPropertyChanged(nameof(TriggerPovs));
                OnPropertyChanged(nameof(UsesPovTrigger));
                OnPropertyChanged(nameof(TriggerDisplayText));
            }
        }

        /// <summary>True if this macro uses POV hat triggers — either via the
        /// multi-device <see cref="TriggerInputs"/> spec or the legacy
        /// <see cref="TriggerPovs"/> array.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool UsesPovTrigger
        {
            get
            {
                var entries = GetTriggerInputEntries();
                for (int i = 0; i < entries.Count; i++)
                    if (!string.IsNullOrEmpty(entries[i].Pov)) return true;
                return _triggerPovs.Length > 0;
            }
        }

        // ───────────────────────────────────────────────
        //  Multi-device trigger inputs
        //  Authoritative storage for cross-device button + POV combos
        //  (e.g. "hold controller X + keyboard A + mouse Left").
        //  Legacy single-device fields (TriggerDeviceGuid /
        //  TriggerRawButtons / TriggerPovs) remain valid for
        //  back-compat — when both are populated, the multi-device
        //  list wins.
        // ───────────────────────────────────────────────

        /// <summary>One entry in the multi-device trigger combo. Exactly one
        /// of <see cref="RawButton"/>, <see cref="Pov"/>, or
        /// <see cref="AxisTarget"/> is populated per entry. Axis entries
        /// carry the same Invert / HalfAxis / DeadZone fields the merge-
        /// mapping system uses on its axis-to-button sources — no per-axis
        /// classification (the engine treats any axis index uniformly with
        /// these three knobs).</summary>
        public sealed class TriggerInputEntry : ObservableObject
        {
            private Guid _deviceGuid;
            public Guid DeviceGuid
            {
                get => _deviceGuid;
                set => SetProperty(ref _deviceGuid, value);
            }

            /// <summary>Raw button index, or -1 if this entry isn't a button.</summary>
            private int _rawButton = -1;
            public int RawButton
            {
                get => _rawButton;
                set => SetProperty(ref _rawButton, value);
            }

            /// <summary>"povIndex:centidegrees" form, or null if this entry isn't a POV.</summary>
            private string _pov;
            public string Pov
            {
                get => _pov;
                set => SetProperty(ref _pov, value);
            }

            /// <summary>Axis target on the device's standard SDL gamepad layout
            /// (0=LX, 1=LY, 2=LT, 3=RX, 4=RY, 5=RT). <c>None</c> if not an axis entry.</summary>
            private MacroAxisTarget _axisTarget = MacroAxisTarget.None;
            public MacroAxisTarget AxisTarget
            {
                get => _axisTarget;
                set => SetProperty(ref _axisTarget, value);
            }

            /// <summary>When true the axis reads as a half-axis: only the
            /// half away from the centered rest position contributes. Same
            /// semantics as <c>MappingSource.HalfAxis</c> — combined with
            /// <see cref="Invert"/> it picks which half is active (false =
            /// upper half / positive direction; true = lower half / negative
            /// direction).</summary>
            private bool _halfAxis;
            public bool HalfAxis
            {
                get => _halfAxis;
                set => SetProperty(ref _halfAxis, value);
            }

            /// <summary>When true the axis reading is flipped (val → 1−val)
            /// before the deadzone test. Same semantics as
            /// <c>MappingSource.Invert</c>.</summary>
            private bool _invert;
            public bool Invert
            {
                get => _invert;
                set => SetProperty(ref _invert, value);
            }

            /// <summary>When true (and <see cref="HalfAxis"/> is also on) the
            /// entry fires on absolute deflection past the deadzone — i.e.
            /// either side of center counts. Lets a single half-axis entry
            /// stand in for "stick deflected anywhere past N %", which the
            /// merge-mapping system can only express by adding two opposite-
            /// Invert sources to the same row. Ignored when HalfAxis is off
            /// since a full-axis read has no center to mirror across.</summary>
            private bool _bidirectional;
            public bool Bidirectional
            {
                get => _bidirectional;
                set => SetProperty(ref _bidirectional, value);
            }

            /// <summary>Axis-to-button deadzone in percent (1..100). Default
            /// matches the merge-mapping <c>MappingSource.DeadZone</c>
            /// default. The axis fires past this percentage of the full
            /// range (HalfAxis off) or past this percentage of the half
            /// range past center (HalfAxis on).</summary>
            private int _deadZone = 50;
            public int DeadZone
            {
                get => _deadZone;
                set => SetProperty(ref _deadZone, Math.Clamp(value, 1, 100));
            }

            private CommunityToolkit.Mvvm.Input.RelayCommand _resetDeadZoneCommand;
            /// <summary>Reset the per-entry deadzone to the default 50 %. Pairs
            /// with the mapping table's <c>ResetDeadZoneCommand</c> so the
            /// macros editor's axis row carries the same reset glyph as the
            /// mapping grid.</summary>
            [System.Xml.Serialization.XmlIgnore]
            public CommunityToolkit.Mvvm.Input.RelayCommand ResetDeadZoneCommand =>
                _resetDeadZoneCommand ??= new CommunityToolkit.Mvvm.Input.RelayCommand(() => DeadZone = 50);

            /// <summary>Device name resolved from <see cref="DeviceGuid"/> for the
            /// per-entry editor UI.</summary>
            [System.Xml.Serialization.XmlIgnore]
            public string DeviceLabel
            {
                get
                {
                    if (_deviceGuid == Guid.Empty) return "";
                    var ud = SettingsManager.FindDeviceByInstanceGuid(_deviceGuid);
                    return ud?.ResolvedName ?? "";
                }
            }

            /// <summary>Localized axis name (Left Stick X / Left Trigger / …)
            /// for the per-entry editor UI.</summary>
            [System.Xml.Serialization.XmlIgnore]
            public string AxisLabel => _axisTarget == MacroAxisTarget.None ? "" : _axisTarget.DisplayName();

            /// <summary>Compact tagged form for XML round-trip.
            /// Format: <c>in:GUID:ax:Target:HalfAxis:Invert:DeadZone:Bidirectional</c>
            /// (e.g. <c>in:GUID:ax:LeftStickX:1:0:50:1</c>). The trailing
            /// Bidirectional field is optional — parser defaults to 0 when
            /// reading older XML written before the flag existed.</summary>
            public string Spec
            {
                get
                {
                    if (DeviceGuid == Guid.Empty) return "";
                    if (AxisTarget != MacroAxisTarget.None)
                        return $"in:{DeviceGuid}:ax:{AxisTarget}:{(HalfAxis ? 1 : 0)}:{(Invert ? 1 : 0)}:{DeadZone}:{(Bidirectional ? 1 : 0)}";
                    if (!string.IsNullOrEmpty(Pov)) return $"in:{DeviceGuid}:pov:{Pov}";
                    if (RawButton >= 0) return $"in:{DeviceGuid}:btn:{RawButton}";
                    return "";
                }
            }

            public static TriggerInputEntry Parse(string spec)
            {
                if (string.IsNullOrEmpty(spec)) return null;
                var parts = spec.Split(':');
                if (parts.Length < 4 || parts[0] != "in" || !Guid.TryParse(parts[1], out var g))
                    return null;
                var entry = new TriggerInputEntry { DeviceGuid = g };
                switch (parts[2])
                {
                    case "btn":
                        if (!int.TryParse(parts[3], out int b)) return null;
                        entry.RawButton = b;
                        return entry;
                    case "pov":
                        if (parts.Length < 5) return null;
                        entry.Pov = $"{parts[3]}:{parts[4]}";
                        return entry;
                    case "ax":
                        if (!Enum.TryParse<MacroAxisTarget>(parts[3], out var at) || at == MacroAxisTarget.None) return null;
                        entry.AxisTarget = at;
                        // New format: parts[4]=HalfAxis(0/1), parts[5]=Invert(0/1), parts[6]=DeadZone(1-100).
                        // Legacy migration: if parts[4] is "Positive"/"Negative"/"Any" (older direction-based
                        // spec), map it to HalfAxis + Invert and use parts[5] as deadzone, parts[6] as invert.
                        if (parts.Length >= 5)
                        {
                            if (parts[4] == "0" || parts[4] == "1")
                            {
                                entry.HalfAxis = parts[4] == "1";
                                if (parts.Length >= 6 && (parts[5] == "0" || parts[5] == "1"))
                                    entry.Invert = parts[5] == "1";
                                if (parts.Length >= 7 && int.TryParse(parts[6], out int dz))
                                    entry.DeadZone = dz;
                                if (parts.Length >= 8 && (parts[7] == "0" || parts[7] == "1"))
                                    entry.Bidirectional = parts[7] == "1";
                            }
                            else
                            {
                                // Legacy "Positive"/"Negative"/"Any" form. Translate:
                                //   Positive → HalfAxis=true,  Invert=false
                                //   Negative → HalfAxis=true,  Invert=true
                                //   Any      → HalfAxis=false, Invert=false
                                if (parts[4] == "Positive") { entry.HalfAxis = true;  entry.Invert = false; }
                                else if (parts[4] == "Negative") { entry.HalfAxis = true; entry.Invert = true; }
                                else { entry.HalfAxis = false; entry.Invert = false; }
                                if (parts.Length >= 6 && int.TryParse(parts[5], out int legacyDz))
                                    entry.DeadZone = legacyDz;
                                // Legacy parts[6] was an invert flag; ignore in favor of the
                                // direction-derived value above.
                            }
                        }
                        return entry;
                    default: return null;
                }
            }
        }

        private List<TriggerInputEntry> _triggerInputEntries;

        /// <summary>Pipe-separated <see cref="TriggerInputEntry.Spec"/> entries
        /// for the multi-device combo. Empty/null means "use legacy
        /// single-device fields" if those are populated. Persisted to XML
        /// as a single element so the format stays compact and append-only.</summary>
        public string TriggerInputs
        {
            get
            {
                EnsureTriggerInputEntries();
                if (_triggerInputEntries == null || _triggerInputEntries.Count == 0) return null;
                return string.Join("|", _triggerInputEntries.Select(e => e.Spec).Where(s => s.Length > 0));
            }
            set
            {
                _triggerInputEntries = new List<TriggerInputEntry>();
                if (!string.IsNullOrEmpty(value))
                {
                    foreach (var s in value.Split('|'))
                    {
                        var entry = TriggerInputEntry.Parse(s);
                        if (entry != null) _triggerInputEntries.Add(entry);
                    }
                }
                // Wire the loaded entries so post-load per-entry edits (Invert /
                // HalfAxis / Bidirectional / DeadZone) bubble up and autosave —
                // the XML-load path previously left them unwired.
                WireTriggerInputEntries();
                OnPropertyChanged(nameof(TriggerInputs));
                OnPropertyChanged(nameof(UsesRawTrigger));
                OnPropertyChanged(nameof(UsesPovTrigger));
                OnPropertyChanged(nameof(TriggerDisplayText));
            }
        }

        /// <summary>Returns the current parsed entries. Migrates from legacy
        /// single-device fields on first access. Hot path: called every macro
        /// evaluation frame via <c>UsesRawTrigger</c> /
        /// <c>CheckRawButtonTrigger</c>; the migrated cache is reused.</summary>
        public IReadOnlyList<TriggerInputEntry> GetTriggerInputEntries()
        {
            EnsureTriggerInputEntries();
            return _triggerInputEntries ?? (IReadOnlyList<TriggerInputEntry>)Array.Empty<TriggerInputEntry>();
        }

        /// <summary>Subset of <see cref="GetTriggerInputEntries"/> containing
        /// just the axis-bearing entries — used by the per-entry editor in
        /// the macro trigger panel so the user can toggle Invert / HalfAxis
        /// and adjust DeadZone the same way the merge-mapping editor exposes
        /// them on axis-to-button sources.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public IEnumerable<TriggerInputEntry> TriggerAxisEntries
        {
            get
            {
                var entries = GetTriggerInputEntries();
                for (int i = 0; i < entries.Count; i++)
                    if (entries[i].AxisTarget != MacroAxisTarget.None)
                        yield return entries[i];
            }
        }

        /// <summary>True when the macro has legacy slot-combined axis targets
        /// (the OutputController-source path) — drives the visibility of the
        /// legacy single Threshold + Direction sliders. Mutually exclusive in
        /// practice with <see cref="HasTriggerAxisEntries"/> since the
        /// recorder writes to one path or the other based on TriggerSource.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool HasLegacyAxisTrigger => _triggerAxisTargets != null && _triggerAxisTargets.Length > 0;

        /// <summary>True when the macro has at least one per-device axis
        /// entry. Drives the visibility of the per-entry axis editor panel.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool HasTriggerAxisEntries
        {
            get
            {
                var entries = GetTriggerInputEntries();
                for (int i = 0; i < entries.Count; i++)
                    if (entries[i].AxisTarget != MacroAxisTarget.None) return true;
                return false;
            }
        }

        /// <summary>Replaces the entry list. Used by the recorder when
        /// finalizing a multi-device combo.</summary>
        public void SetTriggerInputEntries(List<TriggerInputEntry> entries)
        {
            _triggerInputEntries = entries ?? new List<TriggerInputEntry>();
            WireTriggerInputEntries();
            OnPropertyChanged(nameof(TriggerInputs));
            OnPropertyChanged(nameof(UsesRawTrigger));
            OnPropertyChanged(nameof(UsesPovTrigger));
            OnPropertyChanged(nameof(UsesAxisTrigger));
            OnPropertyChanged(nameof(TriggerDisplayText));
            OnPropertyChanged(nameof(TriggerAxisEntries));
            OnPropertyChanged(nameof(HasTriggerAxisEntries));
        }

        private void EnsureTriggerInputEntries()
        {
            if (_triggerInputEntries != null) return;
            _triggerInputEntries = new List<TriggerInputEntry>();
            // Migrate from legacy single-device fields on first access.
            if (_triggerDeviceGuid != Guid.Empty)
            {
                if (_triggerRawButtons != null)
                {
                    foreach (var btn in _triggerRawButtons)
                        _triggerInputEntries.Add(new TriggerInputEntry { DeviceGuid = _triggerDeviceGuid, RawButton = btn });
                }
                if (_triggerPovs != null)
                {
                    foreach (var pov in _triggerPovs)
                        _triggerInputEntries.Add(new TriggerInputEntry { DeviceGuid = _triggerDeviceGuid, Pov = pov });
                }
            }
            WireTriggerInputEntries();
        }

        /// <summary>Subscribes each <see cref="TriggerInputEntry"/>'s
        /// PropertyChanged event so per-entry Invert / HalfAxis / Bidirectional /
        /// DeadZone edits bubble up through the macro as a
        /// <see cref="TriggerInputs"/> change. Without this the macro's
        /// own PropertyChanged never fires for per-entry edits, the
        /// settings autosave never runs, and the new values are lost on
        /// the next reload.</summary>
        private void WireTriggerInputEntries()
        {
            if (_triggerInputEntries == null) return;
            foreach (var entry in _triggerInputEntries)
            {
                if (entry == null) continue;
                entry.PropertyChanged -= OnTriggerInputEntryPropertyChanged;
                entry.PropertyChanged += OnTriggerInputEntryPropertyChanged;
            }
        }

        private void OnTriggerInputEntryPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // The serialized TriggerInputs spec changes whenever any entry
            // field changes. Re-fire TriggerInputs so the MainWindow
            // macro.PropertyChanged → MarkDirty chain catches the edit.
            OnPropertyChanged(nameof(TriggerInputs));
            OnPropertyChanged(nameof(TriggerAxisEntries));
            OnPropertyChanged(nameof(TriggerDisplayText));
        }

        private bool _isRecordingTrigger;

        /// <summary>Whether the macro is currently recording its trigger combo.</summary>
        public bool IsRecordingTrigger
        {
            get => _isRecordingTrigger;
            set
            {
                if (SetProperty(ref _isRecordingTrigger, value))
                {
                    OnPropertyChanged(nameof(RecordTriggerButtonText));
                    OnPropertyChanged(nameof(RecordTriggerIcon));
                }
            }
        }

        public string RecordTriggerButtonText =>
            IsRecordingTrigger ? Strings.Instance.Common_Stop : Strings.Instance.Macro_RecordTrigger;

        public string RecordTriggerIcon =>
            IsRecordingTrigger ? "\uE71A" : "\uE7C8"; // Stop : Record

        private string _recordingLiveText = "";

        /// <summary>Live display of buttons being pressed during recording.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public string RecordingLiveText
        {
            get => _recordingLiveText;
            set => SetProperty(ref _recordingLiveText, value ?? "");
        }

        private MacroButtonStyle _buttonStyle = MacroButtonStyle.Xbox360;

        /// <summary>
        /// Determines button display names based on output controller type.
        /// Set by PadViewModel when OutputType changes.
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public MacroButtonStyle ButtonStyle
        {
            get => _buttonStyle;
            set
            {
                if (SetProperty(ref _buttonStyle, value))
                {
                    OnPropertyChanged(nameof(TriggerDisplayText));
                    _outputChannelOptions = null;
                    OnPropertyChanged(nameof(OutputChannelOptions));
                    foreach (var action in Actions)
                        action.ButtonStyle = value;
                }
            }
        }

        private int _customButtonCount = 11;

        /// <summary>
        /// Number of buttons for custom Extended (from ExtendedConfig.ButtonCount).
        /// Propagated to actions for ButtonOptions generation.
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public int CustomButtonCount
        {
            get => _customButtonCount;
            set => SetProperty(ref _customButtonCount, Math.Max(1, value));
        }

        // ─────────────────────────────────────────────
        //  Trigger options
        // ─────────────────────────────────────────────

        /// <summary>Compact "file1, file2" summary of this macro's Play
        /// Sound actions, for the Audio tab's sound-macro hub list.</summary>
        public string SoundFilesSummary
        {
            get
            {
                var names = Actions
                    .Where(a => a.Type == MacroActionType.PlaySound && !string.IsNullOrEmpty(a.SoundFileName))
                    .Select(a => a.SoundFileName)
                    .Distinct()
                    .ToList();
                return names.Count == 0 ? string.Empty : string.Join(", ", names);
            }
        }

        private MacroTriggerMode _triggerMode = MacroTriggerMode.OnPress;

        /// <summary>When to fire: on press, on release, while held, always, or custom expression.</summary>
        public MacroTriggerMode TriggerMode
        {
            get => _triggerMode;
            set
            {
                if (SetProperty(ref _triggerMode, value))
                {
                    OnPropertyChanged(nameof(IsNotAlwaysMode));
                    OnPropertyChanged(nameof(IsCustomExpressionMode));
                    OnPropertyChanged(nameof(ShowsTriggerComboEditor));
                }
            }
        }

        /// <summary>True when TriggerMode is not Always (legacy callers: used to
        /// gate UI that should hide in Always mode).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsNotAlwaysMode => _triggerMode != MacroTriggerMode.Always;

        /// <summary>True when the standard trigger-combo recording UI should show
        /// (i.e. one of OnPress / OnRelease / WhileHeld). Always mode has no
        /// trigger; CustomExpression mode uses the formula editor instead.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool ShowsTriggerComboEditor =>
            _triggerMode == MacroTriggerMode.OnPress ||
            _triggerMode == MacroTriggerMode.OnRelease ||
            _triggerMode == MacroTriggerMode.WhileHeld;

        private bool _consumeTriggerButtons = true;

        /// <summary>
        /// If true, the trigger buttons are removed from the Gamepad state
        /// when the macro fires (so the game doesn't also see them).
        /// </summary>
        public bool ConsumeTriggerButtons
        {
            get => _consumeTriggerButtons;
            set => SetProperty(ref _consumeTriggerButtons, value);
        }

        // ─────────────────────────────────────────────
        //  Axis trigger (fire when an axis exceeds a threshold)
        // ─────────────────────────────────────────────

        private MacroAxisTarget[] _triggerAxisTargets = Array.Empty<MacroAxisTarget>();

        /// <summary>Axes that must all exceed the threshold for the trigger to fire.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public MacroAxisTarget[] TriggerAxisTargets
        {
            get => _triggerAxisTargets;
            set
            {
                _triggerAxisTargets = value ?? Array.Empty<MacroAxisTarget>();
                OnPropertyChanged(nameof(TriggerAxisTargets));
                OnPropertyChanged(nameof(UsesAxisTrigger));
                OnPropertyChanged(nameof(HasLegacyAxisTrigger));
                OnPropertyChanged(nameof(TriggerDisplayText));
            }
        }

        /// <summary>Serializable comma-separated form of TriggerAxisTargets.</summary>
        public string TriggerAxisTargetList
        {
            get
            {
                if (_triggerAxisTargets.Length == 0) return null;
                return string.Join(",", _triggerAxisTargets.Select(a => a.ToString()));
            }
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    TriggerAxisTargets = Array.Empty<MacroAxisTarget>();
                    return;
                }
                TriggerAxisTargets = value.Split(',')
                    .Select(s => Enum.TryParse<MacroAxisTarget>(s.Trim(), out var v) ? v : MacroAxisTarget.None)
                    .Where(v => v != MacroAxisTarget.None)
                    .ToArray();
            }
        }

        private int _triggerAxisThreshold = 50;

        /// <summary>Threshold percentage (0-100). All trigger axes must exceed this.</summary>
        public int TriggerAxisThreshold
        {
            get => _triggerAxisThreshold;
            set
            {
                if (SetProperty(ref _triggerAxisThreshold, Math.Clamp(value, 1, 100)))
                    OnPropertyChanged(nameof(TriggerDisplayText));
            }
        }

        /// <summary>True if this macro uses one or more axes as part of its trigger.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool UsesAxisTrigger
        {
            get
            {
                if (_triggerAxisTargets.Length > 0) return true;
                var entries = GetTriggerInputEntries();
                for (int i = 0; i < entries.Count; i++)
                    if (entries[i].AxisTarget != MacroAxisTarget.None) return true;
                return false;
            }
        }

        // ── Axis direction filter (per-axis, parallel to TriggerAxisTargets) ──

        private MacroAxisDirection[] _triggerAxisDirections = Array.Empty<MacroAxisDirection>();

        /// <summary>Direction filter for each trigger axis. Parallel array to TriggerAxisTargets.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public MacroAxisDirection[] TriggerAxisDirections
        {
            get => _triggerAxisDirections;
            set
            {
                _triggerAxisDirections = value ?? Array.Empty<MacroAxisDirection>();
                OnPropertyChanged(nameof(TriggerAxisDirections));
                OnPropertyChanged(nameof(TriggerDisplayText));
            }
        }

        /// <summary>Serializable comma-separated form of TriggerAxisDirections.</summary>
        public string TriggerAxisDirectionList
        {
            get
            {
                if (_triggerAxisDirections.Length == 0) return null;
                return string.Join(",", _triggerAxisDirections.Select(d => d.ToString()));
            }
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    TriggerAxisDirections = Array.Empty<MacroAxisDirection>();
                    return;
                }
                TriggerAxisDirections = value.Split(',')
                    .Select(s => Enum.TryParse<MacroAxisDirection>(s.Trim(), out var v) ? v : MacroAxisDirection.Any)
                    .ToArray();
            }
        }

        /// <summary>Gets the direction for a trigger axis at the given index, defaulting to Any.</summary>
        public MacroAxisDirection GetAxisDirection(int index)
            => index >= 0 && index < _triggerAxisDirections.Length ? _triggerAxisDirections[index] : MacroAxisDirection.Any;

        /// <summary>
        /// UI-facing index for the first trigger axis direction (0=Any, 1=Positive, 2=Negative).
        /// Sets all trigger axis directions uniformly for simplicity.
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public int TriggerAxisDirectionIndex
        {
            get => _triggerAxisDirections.Length > 0 ? (int)_triggerAxisDirections[0] : 0;
            set
            {
                var dir = (MacroAxisDirection)Math.Clamp(value, 0, 2);
                var dirs = new MacroAxisDirection[_triggerAxisTargets.Length];
                Array.Fill(dirs, dir);
                TriggerAxisDirections = dirs;
            }
        }

        // ─────────────────────────────────────────────
        //  Custom-expression trigger (TriggerMode = CustomExpression)
        //  A formula over a/b/c/... variables, each bound to either an
        //  input-device input or a virtual controller channel. Compiled lazily
        //  via PadForge.Engine.Common.Mapping.MappingExpression. The
        //  trigger is "active" on a given frame when the evaluated result
        //  is >= 0.5; OnPress-equivalent rising-edge semantics fire the
        //  macro on the 0 → 1 transition.
        // ─────────────────────────────────────────────

        private string _triggerExpression = "";
        private PadForge.Engine.Common.Mapping.MappingExpression.Compiled _triggerExpressionCompiled;

        /// <summary>User-typed formula. Empty / whitespace compiles to literal 0
        /// (trigger never active). Changes invalidate the cached compile.</summary>
        public string TriggerExpression
        {
            get => _triggerExpression;
            set
            {
                if (SetProperty(ref _triggerExpression, value ?? ""))
                {
                    _triggerExpressionCompiled = null;
                    OnPropertyChanged(nameof(CustomExpressionStatus));
                    OnPropertyChanged(nameof(IsCustomExpressionInvalid));
                    OnPropertyChanged(nameof(IsCustomExpressionWarning));
                }
            }
        }

        /// <summary>Returns the cached compile, recomputing if dirty.
        /// Hot path: called every macro evaluation frame in CustomExpression mode.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public PadForge.Engine.Common.Mapping.MappingExpression.Compiled TriggerExpressionCompiled
        {
            get
            {
                if (_triggerExpressionCompiled == null)
                    _triggerExpressionCompiled = PadForge.Engine.Common.Mapping.MappingExpression.Compile(_triggerExpression ?? "");
                return _triggerExpressionCompiled;
            }
        }

        private ObservableCollection<MacroExpressionVariable> _triggerExpressionVariables;

        /// <summary>Ordered list of variables for the custom-expression trigger.
        /// Variable at index 0 binds to <c>a</c>, index 1 to <c>b</c>, etc.
        /// Also addressable as <c>s[0]</c>, <c>s[1]</c>, ... in the formula.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public ObservableCollection<MacroExpressionVariable> TriggerExpressionVariables
        {
            get
            {
                if (_triggerExpressionVariables == null)
                {
                    _triggerExpressionVariables = new ObservableCollection<MacroExpressionVariable>();
                    _triggerExpressionVariables.CollectionChanged += OnTriggerExpressionVariablesChanged;
                }
                return _triggerExpressionVariables;
            }
            set
            {
                if (_triggerExpressionVariables != null)
                    _triggerExpressionVariables.CollectionChanged -= OnTriggerExpressionVariablesChanged;
                _triggerExpressionVariables = value ?? new ObservableCollection<MacroExpressionVariable>();
                _triggerExpressionVariables.CollectionChanged += OnTriggerExpressionVariablesChanged;
                OnPropertyChanged(nameof(TriggerExpressionVariables));
                OnPropertyChanged(nameof(TriggerExpressionVariableSpecs));
                OnPropertyChanged(nameof(CustomExpressionStatus));
                OnPropertyChanged(nameof(IsCustomExpressionWarning));
                OnPropertyChanged(nameof(VariableCount));
            }
        }

        private void OnTriggerExpressionVariablesChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(VariableCount));
            OnPropertyChanged(nameof(CustomExpressionStatus));
            OnPropertyChanged(nameof(IsCustomExpressionWarning));
        }

        /// <summary>Number of variables defined for the custom-expression
        /// trigger. Drives the chip-palette visibility so users only see
        /// letters that map to a real variable.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public int VariableCount => _triggerExpressionVariables?.Count ?? 0;

        /// <summary>Serializable comma-separated list of the variables' Spec
        /// strings. Empty entries are preserved so a/b/c/... indexing is
        /// stable across a load even if some variables are still unbound.</summary>
        public string TriggerExpressionVariableSpecs
        {
            get
            {
                if (_triggerExpressionVariables == null || _triggerExpressionVariables.Count == 0) return null;
                return string.Join("|", _triggerExpressionVariables.Select(v => v?.Spec ?? ""));
            }
            set
            {
                // Use the property to lazy-init the collection — direct field
                // access here throws NRE when the setter runs during MacroItem
                // construction in SettingsService.LoadMacros (object-initializer
                // path), which silently dropped any macro with a CustomExpression
                // trigger from the load.
                var coll = TriggerExpressionVariables;
                coll.Clear();
                if (string.IsNullOrEmpty(value))
                {
                    OnPropertyChanged(nameof(TriggerExpressionVariables));
                    OnPropertyChanged(nameof(VariableCount));
                    return;
                }
                foreach (var spec in value.Split('|'))
                {
                    var v = new MacroExpressionVariable();
                    if (!string.IsNullOrEmpty(spec)) v.Spec = spec;
                    coll.Add(v);
                }
                OnPropertyChanged(nameof(TriggerExpressionVariables));
                OnPropertyChanged(nameof(CustomExpressionStatus));
                OnPropertyChanged(nameof(IsCustomExpressionWarning));
                OnPropertyChanged(nameof(VariableCount));
            }
        }

        /// <summary>True when this macro's trigger uses the custom-expression path.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsCustomExpressionMode => _triggerMode == MacroTriggerMode.CustomExpression;

        /// <summary>Localized "valid" / parse-error string for the editor footer.
        /// Mirrors the merge-mapping <c>CombineExpressionStatus</c> shape.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public string CustomExpressionStatus
        {
            get
            {
                var s = Strings.Instance;
                if (string.IsNullOrWhiteSpace(_triggerExpression))
                    return s.Pad_Formula_Status_Empty;
                var c = TriggerExpressionCompiled;
                if (!c.IsValid)
                    return "✗ " + (c.Error ?? s.Pad_Formula_Status_ParseError);

                int defined = _triggerExpressionVariables?.Count ?? 0;
                var refs = c.ReferencedSingleLetterVars ?? "";
                var outOfRange = new List<string>();
                foreach (char letter in refs)
                {
                    int idx = letter - 'a';
                    if (idx >= defined) outOfRange.Add(letter.ToString());
                }
                bool indexedOutOfRange = c.MaxIndexedRef >= defined;
                if (outOfRange.Count == 0 && !indexedOutOfRange)
                    return s.Pad_Formula_Status_Valid;

                string warn;
                if (outOfRange.Count > 0 && indexedOutOfRange)
                    warn = string.Join(",", outOfRange) + " " + s.Pad_Formula_Status_And + " s[" + c.MaxIndexedRef + "] " + s.Pad_Formula_Status_NoSourcePlural;
                else if (outOfRange.Count > 0)
                    warn = outOfRange.Count == 1
                        ? outOfRange[0] + " " + s.Pad_Formula_Status_NoSourceSingular
                        : string.Join(",", outOfRange) + " " + s.Pad_Formula_Status_NoSourcePlural;
                else
                    warn = "s[" + c.MaxIndexedRef + "] " + s.Pad_Formula_Status_NoSourceSingular;
                return "⚠  " + s.Pad_Formula_Status_Valid.TrimStart('✓', ' ') + " — " + warn + " (" + s.Pad_Formula_Status_TreatedAsZero + ")";
            }
        }

        /// <summary>True when the expression failed to parse.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsCustomExpressionInvalid => !TriggerExpressionCompiled.IsValid;

        /// <summary>True when the expression parses but references more variables
        /// than the macro has defined.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsCustomExpressionWarning
        {
            get
            {
                var c = TriggerExpressionCompiled;
                if (!c.IsValid) return false;
                int refCount = c.ReferencedSingleLetterVars?.Length ?? 0;
                int maxIdx = c.MaxIndexedRef;
                int defined = _triggerExpressionVariables?.Count ?? 0;
                return Math.Max(refCount, maxIdx + 1) > defined;
            }
        }

        /// <summary>Whether a recording session is in progress for the
        /// most recently added custom-expression variable. Used by the
        /// editor's recording-button glyph + live-feedback text.</summary>
        private bool _isRecordingExpressionVariable;
        [System.Xml.Serialization.XmlIgnore]
        public bool IsRecordingExpressionVariable
        {
            get => _isRecordingExpressionVariable;
            set => SetProperty(ref _isRecordingExpressionVariable, value);
        }

        private RelayCommand _addExpressionVariableCommand;
        public RelayCommand AddExpressionVariableCommand =>
            _addExpressionVariableCommand ??= new RelayCommand(() =>
            {
                TriggerExpressionVariables.Add(new MacroExpressionVariable());
                OnPropertyChanged(nameof(CustomExpressionStatus));
                OnPropertyChanged(nameof(IsCustomExpressionWarning));
            });

        private RelayCommand<MacroExpressionVariable> _removeExpressionVariableCommand;
        /// <summary>Removes one variable from the list. Subsequent variables
        /// shift letters (b becomes a, etc.) — that matches how the merge-
        /// mapping editor handles ExtraSources deletion, so the formula's
        /// references stay positionally meaningful.</summary>
        public RelayCommand<MacroExpressionVariable> RemoveExpressionVariableCommand =>
            _removeExpressionVariableCommand ??= new RelayCommand<MacroExpressionVariable>(v =>
            {
                if (v == null) return;
                TriggerExpressionVariables.Remove(v);
                OnPropertyChanged(nameof(CustomExpressionStatus));
                OnPropertyChanged(nameof(IsCustomExpressionWarning));
            });

        // ─────────────────────────────────────────────
        //  Actions
        //  Sequence of outputs produced when the trigger fires.
        // ─────────────────────────────────────────────

        /// <summary>Ordered sequence of actions to execute.</summary>
        public ObservableCollection<MacroAction> Actions { get; } = new();

        private MacroAction _selectedAction;

        public MacroAction SelectedAction
        {
            get => _selectedAction;
            set
            {
                if (SetProperty(ref _selectedAction, value))
                    _removeActionCommand?.NotifyCanExecuteChanged();
            }
        }

        // ─────────────────────────────────────────────
        //  Repeat settings
        // ─────────────────────────────────────────────

        private MacroRepeatMode _repeatMode = MacroRepeatMode.Once;

        /// <summary>How the action sequence repeats.</summary>
        public MacroRepeatMode RepeatMode
        {
            get => _repeatMode;
            set => SetProperty(ref _repeatMode, value);
        }

        private int _repeatCount = 1;

        /// <summary>Number of times to repeat (for FixedCount mode).</summary>
        public int RepeatCount
        {
            get => _repeatCount;
            set => SetProperty(ref _repeatCount, Math.Max(1, value));
        }

        private int _repeatDelayMs = 100;

        /// <summary>Delay between repeats in milliseconds.</summary>
        public int RepeatDelayMs
        {
            get => _repeatDelayMs;
            set => SetProperty(ref _repeatDelayMs, Math.Max(0, value));
        }

        // ─────────────────────────────────────────────
        //  Runtime state (not serialized)
        // ─────────────────────────────────────────────

        /// <summary>Whether the macro is currently executing its action sequence.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsExecuting { get; set; }

        /// <summary>Current position in the action sequence during execution.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public int CurrentActionIndex { get; set; }

        /// <summary>Remaining repeats.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public int RemainingRepeats { get; set; }

        /// <summary>Timestamp when the current action/delay started.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public DateTime ActionStartTime { get; set; }

        /// <summary>Whether the trigger was active on the previous frame.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool WasTriggerActive { get; set; }

        // ─────────────────────────────────────────────
        //  Commands
        // ─────────────────────────────────────────────

        private RelayCommand _recordTriggerCommand;
        public RelayCommand RecordTriggerCommand =>
            _recordTriggerCommand ??= new RelayCommand(() =>
            {
                IsRecordingTrigger = !IsRecordingTrigger;
                RecordTriggerRequested?.Invoke(this, EventArgs.Empty);
            });

        private RelayCommand _clearTriggerCommand;
        public RelayCommand ClearTriggerCommand =>
            _clearTriggerCommand ??= new RelayCommand(() =>
            {
                // If a recording is active, stop it first. Otherwise the
                // InputService recorder keeps writing _recordedInputEntries
                // back to the macro every polling tick and the cleared
                // axis entries reappear on the next frame — that's the
                // "had to hit Clear twice" bug.
                if (IsRecordingTrigger)
                {
                    IsRecordingTrigger = false;
                    RecordTriggerRequested?.Invoke(this, EventArgs.Empty);
                }

                TriggerButtons = 0;
                TriggerCustomButtonWords = new uint[4];
                TriggerRawButtons = Array.Empty<int>();
                TriggerDeviceGuid = Guid.Empty;
                TriggerAxisTargets = Array.Empty<MacroAxisTarget>();
                TriggerAxisDirections = Array.Empty<MacroAxisDirection>();
                TriggerPovs = Array.Empty<string>();
                SetTriggerInputEntries(new List<TriggerInputEntry>());
                OnPropertyChanged(nameof(TriggerDisplayText));
                OnPropertyChanged(nameof(TriggerAxisEntries));
                OnPropertyChanged(nameof(HasTriggerAxisEntries));
                OnPropertyChanged(nameof(HasLegacyAxisTrigger));
            });

        private RelayCommand _addActionCommand;
        public RelayCommand AddActionCommand =>
            _addActionCommand ??= new RelayCommand(() =>
            {
                var action = new MacroAction { Type = MacroActionType.ButtonPress, ButtonStyle = _buttonStyle, CustomButtonCount = _customButtonCount };
                Actions.Add(action);
                SelectedAction = action;
            });

        private RelayCommand _removeActionCommand;
        public RelayCommand RemoveActionCommand =>
            _removeActionCommand ??= new RelayCommand(() =>
            {
                if (_selectedAction != null)
                {
                    Actions.Remove(_selectedAction);
                    SelectedAction = Actions.LastOrDefault();
                }
            }, () => _selectedAction != null);

        public event EventHandler RecordTriggerRequested;

        public override string ToString() => $"{_name} ({TriggerDisplayText})";
    }

    /// <summary>
    /// A single action within a macro's action sequence.
    /// </summary>
    public class MacroAction : ObservableObject
    {
        static MacroAction()
        {
            Strings.CultureChanged += RefreshVirtualKeyValues;
        }

        public MacroAction()
        {
            Strings.CultureChanged += OnCultureChanged;
        }

        private void OnCultureChanged()
        {
            OnPropertyChanged(nameof(DisplayText));
        }

        private MacroActionType _type = MacroActionType.ButtonPress;

        /// <summary>Type of action to perform.</summary>
        public MacroActionType Type
        {
            get => _type;
            set
            {
                if (SetProperty(ref _type, value))
                {
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(IsButtonType));
                    OnPropertyChanged(nameof(IsKeyType));
                    OnPropertyChanged(nameof(IsDurationType));
                    OnPropertyChanged(nameof(IsAxisType));
                    OnPropertyChanged(nameof(IsSystemVolumeType));
                    OnPropertyChanged(nameof(IsAppVolumeType));
                    OnPropertyChanged(nameof(IsMouseMoveType));
                    OnPropertyChanged(nameof(IsMouseButtonType));
                    OnPropertyChanged(nameof(IsContinuousAxisType));
                    OnPropertyChanged(nameof(IsLightbarType));
                    OnPropertyChanged(nameof(IsLightbarColorClearType));
                    OnPropertyChanged(nameof(IsLightbarModeSetType));
                    OnPropertyChanged(nameof(IsLightbarModeCycleType));
                    OnPropertyChanged(nameof(IsAnyLightbarType));
                    OnPropertyChanged(nameof(IsLightbarReactiveHold));
                    OnPropertyChanged(nameof(IsLightbarStickyHold));
                    OnPropertyChanged(nameof(IsLightbarFixedColorVisible));
                    OnPropertyChanged(nameof(IsLightbarPaletteVisible));
                    OnPropertyChanged(nameof(IsRumbleType));
                    OnPropertyChanged(nameof(IsRumbleStopType));
                    OnPropertyChanged(nameof(IsPlaySoundType));
                    OnPropertyChanged(nameof(IsSoundStopType));
                    OnPropertyChanged(nameof(IsAnyRumbleType));
                    OnPropertyChanged(nameof(IsSetGyroEngagedType));
                    OnPropertyChanged(nameof(IsRumbleReactiveHold));
                    OnPropertyChanged(nameof(IsRumbleStickyHold));
                }
            }
        }

        /// <summary>True when Type is ButtonPress or ButtonRelease.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsButtonType => _type == MacroActionType.ButtonPress || _type == MacroActionType.ButtonRelease;

        /// <summary>True when Type is KeyPress or KeyRelease.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsKeyType => _type == MacroActionType.KeyPress || _type == MacroActionType.KeyRelease;

        /// <summary>True when Type uses the generic <c>DurationMs</c>
        /// field for its hold time — ButtonPress / KeyPress / Delay /
        /// MouseButtonPress. LightbarColor uses its own
        /// <c>LightbarHoldMs</c>/<c>LightbarFadeMs</c> pair instead so
        /// the hold and fade sliders can be scaled and labeled
        /// separately from the generic ms field.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsDurationType => _type == MacroActionType.ButtonPress || _type == MacroActionType.KeyPress || _type == MacroActionType.Delay || _type == MacroActionType.MouseButtonPress;

        /// <summary>True when Type is AxisSet.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAxisType => _type == MacroActionType.AxisSet;

        /// <summary>True when Type is SystemVolume.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsSystemVolumeType => _type == MacroActionType.SystemVolume;

        /// <summary>True when Type is AppVolume.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAppVolumeType => _type == MacroActionType.AppVolume;

        /// <summary>True when Type is MouseMove or MouseScroll (continuous axis-to-mouse).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsMouseMoveType => _type == MacroActionType.MouseMove || _type == MacroActionType.MouseScroll;

        /// <summary>True when Type is MouseButtonPress or MouseButtonRelease.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsMouseButtonType => _type == MacroActionType.MouseButtonPress || _type == MacroActionType.MouseButtonRelease;

        /// <summary>True when Type is LightbarColor (PlayStation slot RGB override).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsLightbarType => _type == MacroActionType.LightbarColor;

        /// <summary>True when Type is Rumble.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsRumbleType => _type == MacroActionType.Rumble;

        /// <summary>True when Type is RumbleStop.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsRumbleStopType => _type == MacroActionType.RumbleStop;

        public bool IsPlaySoundType => _type == MacroActionType.PlaySound;

        public bool IsSoundStopType => _type == MacroActionType.SoundStop;

        /// <summary>True when Type is SetGyroEngaged. Surfaces the
        /// Mode dropdown editor in the macro action UI.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsSetGyroEngagedType => _type == MacroActionType.SetGyroEngaged;

        /// <summary>True when Type is any rumble-related action — drives
        /// the macro editor's grouping into a single CardBorder.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAnyRumbleType
            => _type == MacroActionType.Rumble
            || _type == MacroActionType.RumbleStop;

        /// <summary>True when the Rumble action is in Reactive hold mode —
        /// drives visibility of the hold/fade sliders in the editor.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsRumbleReactiveHold
            => _type == MacroActionType.Rumble
               && _rumbleHoldMode == MacroRumbleHoldMode.Reactive;

        /// <summary>True when the Rumble action is in Sticky hold mode.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsRumbleStickyHold
            => _type == MacroActionType.Rumble
               && _rumbleHoldMode == MacroRumbleHoldMode.Sticky;

        /// <summary>True when Type is LightbarColorClear.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsLightbarColorClearType => _type == MacroActionType.LightbarColorClear;

        /// <summary>True when Type is LightbarModeSet.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsLightbarModeSetType => _type == MacroActionType.LightbarModeSet;

        /// <summary>True when Type is LightbarModeCycle.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsLightbarModeCycleType => _type == MacroActionType.LightbarModeCycle;

        /// <summary>True when Type is any of the lightbar-related action
        /// types — drives the macro editor's grouping into a single
        /// CardBorder.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAnyLightbarType
            => _type == MacroActionType.LightbarColor
            || _type == MacroActionType.LightbarColorClear
            || _type == MacroActionType.LightbarModeSet
            || _type == MacroActionType.LightbarModeCycle;

        /// <summary>True when the LightbarColor action is in Reactive
        /// hold mode — drives the visibility of the ColorSource picker
        /// and Decay slider in the editor.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsLightbarReactiveHold
            => _type == MacroActionType.LightbarColor
               && _lightbarHoldMode == MacroLightbarHoldMode.Reactive;

        /// <summary>True when the LightbarColor action is in Sticky hold
        /// mode.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsLightbarStickyHold
            => _type == MacroActionType.LightbarColor
               && _lightbarHoldMode == MacroLightbarHoldMode.Sticky;

        /// <summary>True when the color picker should be visible —
        /// LightbarColor with ColorSource = Fixed (Reactive or Sticky).
        /// Random and Palette sources don't read the action's RGB so
        /// the picker is hidden in those cases.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsLightbarFixedColorVisible
            => _type == MacroActionType.LightbarColor
               && (_lightbarHoldMode == MacroLightbarHoldMode.Sticky
                   || _lightbarColorSource == MacroLightbarColorSource.Fixed);

        /// <summary>True when Type uses a continuous axis source (SystemVolume, AppVolume, MouseMove, MouseScroll).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsContinuousAxisType => _type is MacroActionType.SystemVolume or MacroActionType.AppVolume
            or MacroActionType.MouseMove or MacroActionType.MouseScroll;

        /// <summary>True when AxisSource is InputDevice.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsDeviceAxisSource => _axisSource == MacroAxisSource.InputDevice;

        /// <summary>True when AxisSource is OutputController (default).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsOutputAxisSource => _axisSource == MacroAxisSource.OutputController;

        private MacroButtonStyle _buttonStyle = MacroButtonStyle.Xbox360;

        /// <summary>
        /// Determines button display names. Synced from parent MacroItem.
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public MacroButtonStyle ButtonStyle
        {
            get => _buttonStyle;
            set
            {
                if (SetProperty(ref _buttonStyle, value))
                {
                    // Force full rebuild when switching to/from Numbered (different option count).
                    _buttonOptions = null;
                    OnPropertyChanged(nameof(ButtonOptions));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        private int _customButtonCount = 11;

        /// <summary>
        /// Number of buttons to show for Numbered style (from ExtendedConfig.ButtonCount).
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public int CustomButtonCount
        {
            get => _customButtonCount;
            set
            {
                if (SetProperty(ref _customButtonCount, Math.Max(1, value)) && _buttonStyle == MacroButtonStyle.Numbered)
                {
                    _buttonOptions = null;
                    OnPropertyChanged(nameof(ButtonOptions));
                }
            }
        }

        private ushort _buttonFlags;

        /// <summary>
        /// For ButtonPress/ButtonRelease with gamepad presets: Xbox bitmask flags.
        /// </summary>
        public ushort ButtonFlags
        {
            get => _buttonFlags;
            set
            {
                if (SetProperty(ref _buttonFlags, value))
                {
                    OnPropertyChanged(nameof(DisplayText));
                    if (_buttonOptions != null && _buttonStyle != MacroButtonStyle.Numbered)
                        foreach (var opt in _buttonOptions)
                            opt.Refresh();
                }
            }
        }

        // ── Custom Extended button storage (128 buttons max) ──

        private uint[] _customButtonWords = new uint[4];

        /// <summary>
        /// For ButtonPress/ButtonRelease with custom Extended: wide button bitmask (4 × 32-bit = 128 buttons).
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public uint[] CustomButtonWords
        {
            get => _customButtonWords;
            set => _customButtonWords = value ?? new uint[4];
        }

        /// <summary>Serializable hex form of CustomButtonWords (e.g. "00000003,00000000,00000000,00000000").</summary>
        public string CustomButtons
        {
            get
            {
                if (_customButtonWords.All(w => w == 0)) return null; // Omit from XML when empty.
                return string.Join(",", _customButtonWords.Select(w => w.ToString("X8")));
            }
            set
            {
                _customButtonWords = new uint[4];
                if (string.IsNullOrEmpty(value)) return;
                var parts = value.Split(',');
                for (int i = 0; i < 4 && i < parts.Length; i++)
                    if (uint.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out var w))
                        _customButtonWords[i] = w;
            }
        }

        /// <summary>Sets/clears a custom Extended button (0-based index).</summary>
        public void SetCustomButton(int index, bool pressed)
        {
            int word = index / 32;
            int bit = index % 32;
            if (word < 0 || word >= _customButtonWords.Length) return;
            if (pressed) _customButtonWords[word] |= (uint)(1 << bit);
            else _customButtonWords[word] &= ~(uint)(1 << bit);
            OnPropertyChanged(nameof(DisplayText));
            RefreshCustomButtonOptions();
        }

        /// <summary>Returns true if the specified custom Extended button is pressed.</summary>
        public bool IsCustomButtonPressed(int index)
        {
            int word = index / 32;
            int bit = index % 32;
            if (word < 0 || word >= _customButtonWords.Length) return false;
            return (_customButtonWords[word] & (uint)(1 << bit)) != 0;
        }

        /// <summary>Returns true if any custom button is set.</summary>
        public bool HasCustomButtons => _customButtonWords.Any(w => w != 0);

        private void RefreshCustomButtonOptions()
        {
            if (_buttonOptions == null || _buttonStyle != MacroButtonStyle.Numbered) return;
            foreach (var opt in _buttonOptions)
                opt.Refresh();
        }

        // ── LightbarMode cycle checkbox options ──

        private IReadOnlyList<LightbarModeCycleOption> _cycleModeOptions;

        /// <summary>Checkbox-bindable list of every selectable
        /// LightbarMode value for the LightbarModeCycle action's editor.
        /// Toggling any option's IsChecked rewrites
        /// <see cref="LightbarCycleModesCsv"/> — the CSV is the
        /// canonical storage; the option list is a UI-side projection.
        /// Each option's <c>Label</c> is a live getter that resolves
        /// the localized mode name on access, so a culture change
        /// reflows the labels without rebuilding the list.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public IReadOnlyList<LightbarModeCycleOption> CycleModeOptions
        {
            get
            {
                if (_cycleModeOptions == null)
                {
                    var modes = new[] {
                        LightbarMode.Off, LightbarMode.Static, LightbarMode.Breathing,
                        LightbarMode.Rainbow, LightbarMode.ColorCycle,
                        LightbarMode.AudioPulse, LightbarMode.AudioPulseRandom, LightbarMode.AudioPulseRainbow,
                        LightbarMode.AudioThresholds, LightbarMode.AudioGradient, LightbarMode.AudioCrossFade,
                        LightbarMode.InputReactive, LightbarMode.InputReactiveCycle, LightbarMode.InputReactiveFixed,
                    };
                    _cycleModeOptions = modes
                        .Select(m => new LightbarModeCycleOption(this, m))
                        .ToList()
                        .AsReadOnly();
                }
                return _cycleModeOptions;
            }
        }

        /// <summary>Sets the cycle CSV from an enumerable of selected
        /// modes. Sorts by enum value for stable ordering.</summary>
        internal void WriteCycleCsv(IEnumerable<LightbarMode> selected)
        {
            var ordered = selected.Distinct().OrderBy(m => (int)m).Select(m => ((int)m).ToString());
            LightbarCycleModesCsv = string.Join(",", ordered);
        }

        // ── Button checkbox options ──

        private IReadOnlyList<GamepadButtonOption> _buttonOptions;

        /// <summary>Checkbox-bindable options for each gamepad button.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public IReadOnlyList<GamepadButtonOption> ButtonOptions
        {
            get
            {
                if (_buttonOptions == null)
                {
                    if (_buttonStyle == MacroButtonStyle.Numbered)
                    {
                        // Dynamic list for custom Extended — N buttons from config.
                        var list = new List<GamepadButtonOption>();
                        for (int i = 0; i < _customButtonCount; i++)
                            list.Add(new GamepadButtonOption(this, string.Format(Strings.Instance.Macro_Btn_Format, i + 1), customIndex: i));
                        _buttonOptions = list.AsReadOnly();
                    }
                    else
                    {
                        var defs = MacroButtonNames.GetButtonDefs(_buttonStyle);
                        _buttonOptions = defs
                            .Select(d => new GamepadButtonOption(this, d.Label, d.Flag))
                            .ToList().AsReadOnly();
                    }
                }
                return _buttonOptions;
            }
        }

        private int _keyCode;

        /// <summary>
        /// For KeyPress/KeyRelease: virtual key code (Win32 VK_ constant).
        /// </summary>
        public int KeyCode
        {
            get => _keyCode;
            set
            {
                if (SetProperty(ref _keyCode, value))
                {
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(SelectedVirtualKey));
                }
            }
        }

        /// <summary>
        /// Gets or sets the key code as a VirtualKey enum value.
        /// Provides typed ComboBox binding while keeping KeyCode as the serialized int.
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public VirtualKey SelectedVirtualKey
        {
            get => (VirtualKey)_keyCode;
            set => KeyCode = (int)value;
        }

        /// <summary>
        /// Provides the list of VirtualKey values with localized display names for ComboBox binding.
        /// Rebuilt on culture change so display names track the current language.
        /// </summary>
        public static List<KeyDisplayItem> VirtualKeyValues { get; private set; } = BuildKeyDisplayItems();

        internal static void RefreshVirtualKeyValues() => VirtualKeyValues = BuildKeyDisplayItems();

        private static List<KeyDisplayItem> BuildKeyDisplayItems()
        {
            var items = new List<KeyDisplayItem>();
            foreach (VirtualKey vk in Enum.GetValues(typeof(VirtualKey)))
                items.Add(new KeyDisplayItem(vk, VirtualKeyDisplayName(vk)));
            return items;
        }

        /// <summary>Returns a user-friendly localized display name for a virtual key.</summary>
        private static string VirtualKeyDisplayName(VirtualKey vk) => vk switch
        {
            VirtualKey.None => Strings.Instance.Macro_None,
            // Mouse buttons
            VirtualKey.LButton => Strings.Instance.Key_LButton,
            VirtualKey.RButton => Strings.Instance.Key_RButton,
            VirtualKey.Cancel => Strings.Instance.Key_Cancel,
            VirtualKey.MButton => Strings.Instance.Key_MButton,
            VirtualKey.XButton1 => Strings.Instance.Key_XButton1,
            VirtualKey.XButton2 => Strings.Instance.Key_XButton2,
            // Common keys
            VirtualKey.Backspace => Strings.Instance.Key_Backspace,
            VirtualKey.Tab => Strings.Instance.Key_Tab,
            VirtualKey.Clear => Strings.Instance.Key_Clear,
            VirtualKey.Enter => Strings.Instance.Key_Enter,
            VirtualKey.Shift => Strings.Instance.Key_Shift,
            VirtualKey.Control => Strings.Instance.Key_Control,
            VirtualKey.Alt => Strings.Instance.Key_Alt,
            VirtualKey.Pause => Strings.Instance.Key_Pause,
            VirtualKey.CapsLock => Strings.Instance.Key_CapsLock,
            VirtualKey.Escape => Strings.Instance.Key_Escape,
            VirtualKey.Space => Strings.Instance.Key_Space,
            // Navigation
            VirtualKey.PageUp => Strings.Instance.Key_PageUp,
            VirtualKey.PageDown => Strings.Instance.Key_PageDown,
            VirtualKey.End => Strings.Instance.Key_End,
            VirtualKey.Home => Strings.Instance.Key_Home,
            VirtualKey.Left => Strings.Instance.Key_Left,
            VirtualKey.Up => Strings.Instance.Key_Up,
            VirtualKey.Right => Strings.Instance.Key_Right,
            VirtualKey.Down => Strings.Instance.Key_Down,
            VirtualKey.Select => Strings.Instance.Key_Select,
            VirtualKey.Print => Strings.Instance.Key_Print,
            VirtualKey.Execute => Strings.Instance.Key_Execute,
            VirtualKey.PrintScreen => Strings.Instance.Key_PrintScreen,
            VirtualKey.Insert => Strings.Instance.Key_Insert,
            VirtualKey.Delete => Strings.Instance.Key_Delete,
            VirtualKey.Help => Strings.Instance.Key_Help,
            // Numbers
            VirtualKey.D0 => "0", VirtualKey.D1 => "1", VirtualKey.D2 => "2",
            VirtualKey.D3 => "3", VirtualKey.D4 => "4", VirtualKey.D5 => "5",
            VirtualKey.D6 => "6", VirtualKey.D7 => "7", VirtualKey.D8 => "8",
            VirtualKey.D9 => "9",
            // Windows keys
            VirtualKey.LWin => Strings.Instance.Key_LWin,
            VirtualKey.RWin => Strings.Instance.Key_RWin,
            VirtualKey.Apps => Strings.Instance.Key_Apps,
            VirtualKey.Sleep => Strings.Instance.Key_Sleep,
            // Numpad — Key_Numpad is a format string ("Numpad {0}"),
            // substitute the digit or symbol via string.Format rather than
            // concatenating (which would leave the literal "{0}" in place).
            VirtualKey.NumPad0 => string.Format(Strings.Instance.Key_Numpad, 0),
            VirtualKey.NumPad1 => string.Format(Strings.Instance.Key_Numpad, 1),
            VirtualKey.NumPad2 => string.Format(Strings.Instance.Key_Numpad, 2),
            VirtualKey.NumPad3 => string.Format(Strings.Instance.Key_Numpad, 3),
            VirtualKey.NumPad4 => string.Format(Strings.Instance.Key_Numpad, 4),
            VirtualKey.NumPad5 => string.Format(Strings.Instance.Key_Numpad, 5),
            VirtualKey.NumPad6 => string.Format(Strings.Instance.Key_Numpad, 6),
            VirtualKey.NumPad7 => string.Format(Strings.Instance.Key_Numpad, 7),
            VirtualKey.NumPad8 => string.Format(Strings.Instance.Key_Numpad, 8),
            VirtualKey.NumPad9 => string.Format(Strings.Instance.Key_Numpad, 9),
            VirtualKey.Multiply => string.Format(Strings.Instance.Key_Numpad, "*"),
            VirtualKey.Add => string.Format(Strings.Instance.Key_Numpad, "+"),
            VirtualKey.Separator => Strings.Instance.Key_Separator,
            VirtualKey.Subtract => string.Format(Strings.Instance.Key_Numpad, "-"),
            VirtualKey.Decimal => string.Format(Strings.Instance.Key_Numpad, "."),
            VirtualKey.Divide => string.Format(Strings.Instance.Key_Numpad, "/"),
            // Lock keys
            VirtualKey.NumLock => Strings.Instance.Key_NumLock,
            VirtualKey.ScrollLock => Strings.Instance.Key_ScrollLock,
            // Left/Right modifiers
            VirtualKey.LShift => Strings.Instance.Key_LeftShift,
            VirtualKey.RShift => Strings.Instance.Key_RightShift,
            VirtualKey.LControl => Strings.Instance.Key_LeftCtrl,
            VirtualKey.RControl => Strings.Instance.Key_RightCtrl,
            VirtualKey.LAlt => Strings.Instance.Key_LeftAlt,
            VirtualKey.RAlt => Strings.Instance.Key_RightAlt,
            // Browser keys
            VirtualKey.BrowserBack => Strings.Instance.Key_BrowserBack,
            VirtualKey.BrowserForward => Strings.Instance.Key_BrowserForward,
            VirtualKey.BrowserRefresh => Strings.Instance.Key_BrowserRefresh,
            VirtualKey.BrowserStop => Strings.Instance.Key_BrowserStop,
            VirtualKey.BrowserSearch => Strings.Instance.Key_BrowserSearch,
            VirtualKey.BrowserFavorites => Strings.Instance.Key_BrowserFavorites,
            VirtualKey.BrowserHome => Strings.Instance.Key_BrowserHome,
            // Media keys
            VirtualKey.VolumeMute => Strings.Instance.Key_VolumeMute,
            VirtualKey.VolumeDown => Strings.Instance.Key_VolumeDown,
            VirtualKey.VolumeUp => Strings.Instance.Key_VolumeUp,
            VirtualKey.MediaNextTrack => Strings.Instance.Key_MediaNext,
            VirtualKey.MediaPrevTrack => Strings.Instance.Key_MediaPrev,
            VirtualKey.MediaStop => Strings.Instance.Key_MediaStop,
            VirtualKey.MediaPlayPause => Strings.Instance.Key_MediaPlayPause,
            VirtualKey.LaunchMail => Strings.Instance.Key_LaunchMail,
            VirtualKey.LaunchMediaSelect => Strings.Instance.Key_LaunchMediaSelect,
            VirtualKey.LaunchApp1 => Strings.Instance.Key_LaunchApp1,
            VirtualKey.LaunchApp2 => Strings.Instance.Key_LaunchApp2,
            // OEM keys (symbol pairs, universal)
            VirtualKey.OemSemicolon => "; :",
            VirtualKey.OemPlus => "= +",
            VirtualKey.OemComma => ", <",
            VirtualKey.OemMinus => "- _",
            VirtualKey.OemPeriod => ". >",
            VirtualKey.OemSlash => "/ ?",
            VirtualKey.OemTilde => "` ~",
            VirtualKey.OemOpenBracket => "[ {",
            VirtualKey.OemBackslash => "\\ |",
            VirtualKey.OemCloseBracket => "] }",
            VirtualKey.OemQuote => "' \"",
            // F-keys and letters fall through to ToString()
            _ => vk.ToString()
        };

        // ── Multi-key string support ──

        private string _keyString = "";

        /// <summary>
        /// Multi-key combo string in x360ce format, e.g., "{Control}{Alt}{Delete}".
        /// </summary>
        public string KeyString
        {
            get => _keyString;
            set
            {
                if (SetProperty(ref _keyString, value ?? ""))
                {
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(ParsedKeyCodes));
                }
            }
        }

        /// <summary>
        /// Parses KeyString into an array of VK codes. Falls back to legacy KeyCode
        /// if KeyString is empty but KeyCode is set.
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public int[] ParsedKeyCodes
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_keyString))
                    return ParseKeyString(_keyString);
                return _keyCode != 0 ? new[] { _keyCode } : Array.Empty<int>();
            }
        }

        /// <summary>Parses "{Key1}{Key2}..." format into int[] of VK codes.</summary>
        public static int[] ParseKeyString(string keyString)
        {
            if (string.IsNullOrWhiteSpace(keyString))
                return Array.Empty<int>();
            var codes = new List<int>();
            foreach (Match m in Regex.Matches(keyString, @"\{(\w+)\}"))
            {
                if (Enum.TryParse<VirtualKey>(m.Groups[1].Value, true, out var vk))
                    codes.Add((int)vk);
            }
            return codes.ToArray();
        }

        /// <summary>Formats VK codes into "{Key1}{Key2}..." string.</summary>
        public static string FormatKeyString(int[] keyCodes)
        {
            if (keyCodes == null || keyCodes.Length == 0) return "";
            var sb = new StringBuilder();
            foreach (var code in keyCodes)
            {
                if (Enum.IsDefined(typeof(VirtualKey), code))
                    sb.Append($"{{{(VirtualKey)code}}}");
                else
                    sb.Append($"{{0x{code:X2}}}");
            }
            return sb.ToString();
        }

        private VirtualKey _selectedKeyToAdd;

        /// <summary>
        /// Bound to the key picker ComboBox. On selection, auto-appends {KeyName}
        /// to KeyString and resets to None.
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public VirtualKey SelectedKeyToAdd
        {
            get => _selectedKeyToAdd;
            set
            {
                if (SetProperty(ref _selectedKeyToAdd, value) && value != VirtualKey.None)
                {
                    KeyString += $"{{{value}}}";
                    // Reset selection after appending so the same key can be added again.
                    SetProperty(ref _selectedKeyToAdd, VirtualKey.None);
                    OnPropertyChanged(nameof(SelectedKeyToAdd));
                }
            }
        }

        private RelayCommand _clearKeyStringCommand;

        /// <summary>Clears the KeyString.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public RelayCommand ClearKeyStringCommand =>
            _clearKeyStringCommand ??= new RelayCommand(() => KeyString = "");

        private int _durationMs = 50;

        /// <summary>
        /// For ButtonPress/KeyPress: how long to hold (ms).
        /// For Delay: pause duration (ms).
        /// </summary>
        public int DurationMs
        {
            get => _durationMs;
            set
            {
                if (SetProperty(ref _durationMs, Math.Max(0, value)))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private short _axisValue;

        /// <summary>
        /// For AxisSet: the signed axis value to inject (-32768..32767).
        /// </summary>
        public short AxisValue
        {
            get => _axisValue;
            set
            {
                if (SetProperty(ref _axisValue, value))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private MacroAxisTarget _axisTarget = MacroAxisTarget.None;

        /// <summary>For AxisSet/SystemVolume/AppVolume: which axis to use.</summary>
        public MacroAxisTarget AxisTarget
        {
            get => _axisTarget;
            set
            {
                if (SetProperty(ref _axisTarget, value))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private bool _invertAxis;

        /// <summary>When true, invert the axis value (0→1 becomes 1→0, or negate for mouse delta).</summary>
        public bool InvertAxis
        {
            get => _invertAxis;
            set
            {
                if (SetProperty(ref _invertAxis, value))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private bool _showVolumeOsd = true;

        /// <summary>When true, trigger the Windows volume flyout OSD. Only relevant for SystemVolume/AppVolume.</summary>
        public bool ShowVolumeOsd
        {
            get => _showVolumeOsd;
            set => SetProperty(ref _showVolumeOsd, value);
        }

        private string _processName = "";

        /// <summary>
        /// For AppVolume: the process name (e.g., "firefox", "spotify") whose
        /// volume in the Windows mixer should be controlled.
        /// </summary>
        public string ProcessName
        {
            get => _processName;
            set
            {
                if (SetProperty(ref _processName, value ?? ""))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        /// <summary>
        /// Process names with active audio sessions, populated on demand.
        /// Used as suggestion items in the editable ComboBox.
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public ObservableCollection<string> AudioProcessNames { get; } = new();

        private RelayCommand _refreshAudioProcessesCommand;

        /// <summary>Refreshes the list of processes with active audio sessions.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public RelayCommand RefreshAudioProcessesCommand =>
            _refreshAudioProcessesCommand ??= new RelayCommand(() =>
            {
                AudioProcessNames.Clear();
                foreach (var name in AudioSessionHelper.GetActiveAudioProcessNames())
                    AudioProcessNames.Add(name);
            });

        // ── Lightbar (for MacroActionType.LightbarColor / Clear / ModeSet / ModeCycle) ──
        // RGB used by LightbarColor when ColorSource = Fixed. Default to
        // white so a freshly-added Color action produces a visible flash
        // on first test fire.

        private byte _lightbarR = 0xFF;
        public byte LightbarR
        {
            get => _lightbarR;
            set
            {
                if (SetProperty(ref _lightbarR, value))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private byte _lightbarG = 0xFF;
        public byte LightbarG
        {
            get => _lightbarG;
            set
            {
                if (SetProperty(ref _lightbarG, value))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private byte _lightbarB = 0xFF;
        public byte LightbarB
        {
            get => _lightbarB;
            set
            {
                if (SetProperty(ref _lightbarB, value))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private MacroLightbarHoldMode _lightbarHoldMode = MacroLightbarHoldMode.Reactive;
        /// <summary>Reactive (default, decay-fade) or Sticky (held until
        /// a <see cref="MacroActionType.LightbarColorClear"/> runs).</summary>
        public MacroLightbarHoldMode LightbarHoldMode
        {
            get => _lightbarHoldMode;
            set
            {
                if (SetProperty(ref _lightbarHoldMode, value))
                {
                    OnPropertyChanged(nameof(IsLightbarReactiveHold));
                    OnPropertyChanged(nameof(IsLightbarStickyHold));
                    OnPropertyChanged(nameof(IsLightbarFixedColorVisible));
                    OnPropertyChanged(nameof(IsLightbarPaletteVisible));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        private MacroLightbarColorSource _lightbarColorSource = MacroLightbarColorSource.Fixed;
        /// <summary>For Reactive holds: Fixed RGB (the action's color),
        /// RandomHue (rolled per fire), or PaletteStep (advance through
        /// the slot's <c>LightbarPalette</c>). Ignored for Sticky holds
        /// (always Fixed).</summary>
        public MacroLightbarColorSource LightbarColorSource
        {
            get => _lightbarColorSource;
            set
            {
                if (SetProperty(ref _lightbarColorSource, value))
                {
                    OnPropertyChanged(nameof(IsLightbarFixedColorVisible));
                    OnPropertyChanged(nameof(IsLightbarPaletteVisible));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        private int _lightbarHoldMs = 0;
        /// <summary>Hold window for Reactive holds (ms). The override
        /// stays at full intensity for this many ms before the fade
        /// begins. 0 means start fading immediately. Clamped 0..5000.</summary>
        public int LightbarHoldMs
        {
            get => _lightbarHoldMs;
            set
            {
                int clamped = Math.Clamp(value, 0, 5000);
                if (SetProperty(ref _lightbarHoldMs, clamped))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private int _lightbarFadeMs = 600;
        /// <summary>Fade window for Reactive holds (ms). After the hold
        /// period elapses, the override linearly fades to 0 over this
        /// duration. 0 means cut directly to off after the hold.
        /// Clamped 0..5000.</summary>
        public int LightbarFadeMs
        {
            get => _lightbarFadeMs;
            set
            {
                int clamped = Math.Clamp(value, 0, 5000);
                if (SetProperty(ref _lightbarFadeMs, clamped))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        // ── Rumble action fields ──
        // Mirrors the LightbarColor block above. Reactive holds run at
        // full strength for RumbleHoldMs then linearly fade to 0 across
        // RumbleFadeMs; Sticky holds at full strength until a RumbleStop
        // action releases.

        private MacroRumbleHoldMode _rumbleHoldMode = MacroRumbleHoldMode.Reactive;
        /// <summary>Reactive (decay-fade pulse) or Sticky (held until a
        /// <see cref="MacroActionType.RumbleStop"/> runs).</summary>
        public MacroRumbleHoldMode RumbleHoldMode
        {
            get => _rumbleHoldMode;
            set
            {
                if (SetProperty(ref _rumbleHoldMode, value))
                {
                    OnPropertyChanged(nameof(IsRumbleReactiveHold));
                    OnPropertyChanged(nameof(IsRumbleStickyHold));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        private int _rumbleStrengthLeft = 100;
        /// <summary>Left (heavy / low-frequency) motor strength as a
        /// percentage 0..100 of the device's full output. Combined with
        /// the FFB-tab per-motor gain at injection time.</summary>
        public int RumbleStrengthLeft
        {
            get => _rumbleStrengthLeft;
            set
            {
                int clamped = Math.Clamp(value, 0, 100);
                if (SetProperty(ref _rumbleStrengthLeft, clamped))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private int _rumbleStrengthRight = 100;
        /// <summary>Right (light / high-frequency) motor strength as a
        /// percentage 0..100. Set 0 with <c>RumbleStrengthLeft</c> > 0
        /// (or vice versa) to fire one motor in isolation.</summary>
        public int RumbleStrengthRight
        {
            get => _rumbleStrengthRight;
            set
            {
                int clamped = Math.Clamp(value, 0, 100);
                if (SetProperty(ref _rumbleStrengthRight, clamped))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private int _rumbleHoldMs = 100;
        /// <summary>Full-strength hold window for Reactive rumble (ms).
        /// Default 100 — short enough that a button tap feels punchy
        /// without bleeding into the next press. Clamped 0..5000.</summary>
        public int RumbleHoldMs
        {
            get => _rumbleHoldMs;
            set
            {
                int clamped = Math.Clamp(value, 0, 5000);
                if (SetProperty(ref _rumbleHoldMs, clamped))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private int _rumbleFadeMs = 200;
        /// <summary>Fade-out window for Reactive rumble (ms). After
        /// <c>RumbleHoldMs</c> elapses, both motors ramp linearly to 0
        /// over this duration. 0 means cut directly to off. Clamped
        /// 0..5000.</summary>
        public int RumbleFadeMs
        {
            get => _rumbleFadeMs;
            set
            {
                int clamped = Math.Clamp(value, 0, 5000);
                if (SetProperty(ref _rumbleFadeMs, clamped))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        // ── Sound (for MacroActionType.PlaySound / SoundStop, issue #83) ──

        private string _soundFilePath = string.Empty;
        /// <summary>Absolute path of the sound file to play. Decoded via
        /// Media Foundation (wav / mp3 / m4a / aac / wma / flac) and cached
        /// after first use so repeat fires start instantly.</summary>
        public string SoundFilePath
        {
            get => _soundFilePath;
            set
            {
                if (SetProperty(ref _soundFilePath, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(SoundFileName));
                }
            }
        }

        /// <summary>File name only, for compact display on the action row
        /// and the Audio tab's sound-macro list. Package references render
        /// as "entry — package".</summary>
        public string SoundFileName
        {
            get
            {
                if (PadForge.Common.SoundPackageManager.IsPackageRef(_soundFilePath))
                    return PadForge.Common.SoundPackageManager.DisplayName(_soundFilePath);
                try { return string.IsNullOrEmpty(_soundFilePath) ? string.Empty : System.IO.Path.GetFileName(_soundFilePath); }
                catch { return _soundFilePath; }
            }
        }

        private int _soundVolume = 100;
        /// <summary>Per-action volume percentage 0-100, multiplied with the
        /// slot's master sound volume (Audio tab).</summary>
        public int SoundVolume
        {
            get => _soundVolume;
            set => SetProperty(ref _soundVolume, Math.Clamp(value, 0, 100));
        }

        private bool _soundLoop;
        /// <summary>Loop until a SoundStop action (or, for While-Held /
        /// Until-Release macros, the trigger's release). Off = play once.</summary>
        public bool SoundLoop
        {
            get => _soundLoop;
            set => SetProperty(ref _soundLoop, value);
        }

        private string _lightbarPaletteCsv = string.Empty;
        /// <summary>CSV of "R,G,B" hex triplets defining the per-macro
        /// palette for <see cref="MacroLightbarColorSource.PaletteStep"/>.
        /// Empty falls back to the slot's own
        /// <c>LightbarPalette</c>.</summary>
        public string LightbarPaletteCsv
        {
            get => _lightbarPaletteCsv;
            set
            {
                if (SetProperty(ref _lightbarPaletteCsv, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(LightbarPalette));
                    OnPropertyChanged(nameof(IsLightbarPaletteVisible));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        /// <summary>Parsed view of <see cref="LightbarPaletteCsv"/> as a
        /// list of LightbarPaletteEntry rows. The macro editor binds an
        /// ItemsControl to this and writes back via the helper commands.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public System.Collections.ObjectModel.ObservableCollection<LightbarPaletteEntry> LightbarPalette
        {
            get
            {
                if (_lightbarPaletteCache != null) return _lightbarPaletteCache;
                _lightbarPaletteCache = new System.Collections.ObjectModel.ObservableCollection<LightbarPaletteEntry>();
                foreach (var (r, g, b) in ParsePaletteCsv(_lightbarPaletteCsv))
                {
                    var entry = new LightbarPaletteEntry { R = r, G = g, B = b };
                    entry.PropertyChanged += OnPaletteEntryPropertyChanged;
                    _lightbarPaletteCache.Add(entry);
                }
                _lightbarPaletteCache.CollectionChanged += (_, e) =>
                {
                    if (e.NewItems != null)
                        foreach (LightbarPaletteEntry n in e.NewItems)
                            n.PropertyChanged += OnPaletteEntryPropertyChanged;
                    if (e.OldItems != null)
                        foreach (LightbarPaletteEntry o in e.OldItems)
                            o.PropertyChanged -= OnPaletteEntryPropertyChanged;
                    SyncPaletteCsvFromCollection();
                };
                return _lightbarPaletteCache;
            }
        }
        private System.Collections.ObjectModel.ObservableCollection<LightbarPaletteEntry> _lightbarPaletteCache;

        private void OnPaletteEntryPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Re-emit only on R/G/B; ignore other ObservableObject churn.
            if (e.PropertyName is nameof(LightbarPaletteEntry.R)
                                or nameof(LightbarPaletteEntry.G)
                                or nameof(LightbarPaletteEntry.B))
            {
                SyncPaletteCsvFromCollection();
            }
        }

        /// <summary>True when the palette editor should be visible —
        /// LightbarColor + Reactive + ColorSource = PaletteStep.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsLightbarPaletteVisible
            => _type == MacroActionType.LightbarColor
               && _lightbarHoldMode == MacroLightbarHoldMode.Reactive
               && _lightbarColorSource == MacroLightbarColorSource.PaletteStep;

        private static System.Collections.Generic.IEnumerable<(byte r, byte g, byte b)> ParsePaletteCsv(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) yield break;
            // Format: "RRGGBB,RRGGBB,..." (hex, 6 chars each).
            foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (raw.Length != 6) continue;
                if (byte.TryParse(raw.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
                 && byte.TryParse(raw.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
                 && byte.TryParse(raw.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                {
                    yield return (r, g, b);
                }
            }
        }

        private void SyncPaletteCsvFromCollection()
        {
            if (_lightbarPaletteCache == null) return;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _lightbarPaletteCache.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = _lightbarPaletteCache[i];
                sb.Append($"{e.R:X2}{e.G:X2}{e.B:X2}");
            }
            // Skip the round-trip through the property setter so we
            // don't rebuild the cache we just authored.
            string newCsv = sb.ToString();
            if (_lightbarPaletteCsv != newCsv)
            {
                _lightbarPaletteCsv = newCsv;
                OnPropertyChanged(nameof(LightbarPaletteCsv));
                OnPropertyChanged(nameof(DisplayText));
            }
        }

        public RelayCommand AddLightbarPaletteColorCommand
            => _addLightbarPaletteColorCommand ??= new RelayCommand(() =>
            {
                LightbarPalette.Add(new LightbarPaletteEntry { R = 0xFF, G = 0xFF, B = 0xFF });
            });
        private RelayCommand _addLightbarPaletteColorCommand;

        public RelayCommand<LightbarPaletteEntry> RemoveLightbarPaletteColorCommand
            => _removeLightbarPaletteColorCommand ??= new RelayCommand<LightbarPaletteEntry>(entry =>
            {
                if (entry == null) return;
                LightbarPalette.Remove(entry);
            });
        private RelayCommand<LightbarPaletteEntry> _removeLightbarPaletteColorCommand;

        private LightbarMode _lightbarTargetMode = LightbarMode.Static;
        /// <summary>Target <c>LightbarMode</c> for
        /// <see cref="MacroActionType.LightbarModeSet"/>.</summary>
        public LightbarMode LightbarTargetMode
        {
            get => _lightbarTargetMode;
            set
            {
                if (SetProperty(ref _lightbarTargetMode, value))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private MacroSetGyroEngagedMode _setGyroEngagedMode = MacroSetGyroEngagedMode.Toggle;
        /// <summary>Write mode for
        /// <see cref="MacroActionType.SetGyroEngaged"/>.
        /// <see cref="MacroSetGyroEngagedMode.Toggle"/> flips the per-slot
        /// macro-engaged bit; <see cref="MacroSetGyroEngagedMode.On"/>
        /// forces it true; <see cref="MacroSetGyroEngagedMode.Off"/>
        /// forces it false. OR-combined with the dedicated engage
        /// button's bit at the gyro evaluator.</summary>
        public MacroSetGyroEngagedMode SetGyroEngagedMode
        {
            get => _setGyroEngagedMode;
            set
            {
                if (SetProperty(ref _setGyroEngagedMode, value))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        // CSV of LightbarMode int values for ModeCycle. Default skips
        // Off and the audio modes — most users want a quick visual
        // toggle, not silent output.
        private string _lightbarCycleModesCsv = "1,2,3,4,11,12";
        /// <summary>CSV of <c>LightbarMode</c> int values to cycle
        /// through. Each fire advances to the next listed mode. Editor
        /// surfaces this as a 13-item checkbox grid.</summary>
        public string LightbarCycleModesCsv
        {
            get => _lightbarCycleModesCsv;
            set
            {
                if (SetProperty(ref _lightbarCycleModesCsv, value ?? string.Empty))
                {
                    _lightbarCycleIndex = 0;
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        private int _lightbarCycleIndex;
        /// <summary>Per-action volatile cycle position. Resets on action
        /// edit and on app restart.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public int LightbarCycleIndex
        {
            get => _lightbarCycleIndex;
            set => _lightbarCycleIndex = value;
        }

        // ── Volume limit ──

        private int _volumeLimit = 100;

        /// <summary>For SystemVolume/AppVolume: maximum volume percentage (1-100). Axis output is scaled to this limit.</summary>
        public int VolumeLimit
        {
            get => _volumeLimit;
            set
            {
                if (SetProperty(ref _volumeLimit, Math.Clamp(value, 1, 100)))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        // ── Mouse properties ──

        private float _mouseSensitivity = 10f;

        /// <summary>For MouseMove/MouseScroll: pixels (or scroll units) per frame at full deflection. Range 1-100.</summary>
        public float MouseSensitivity
        {
            get => _mouseSensitivity;
            set
            {
                if (SetProperty(ref _mouseSensitivity, Math.Clamp(value, 1f, 100f)))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        /// <summary>Fractional pixel/scroll accumulator for sub-pixel precision.</summary>
        [System.Xml.Serialization.XmlIgnore]
        internal float MouseAccumulator;

        private MacroMouseButton _mouseButton = MacroMouseButton.Left;

        /// <summary>For MouseButtonPress/MouseButtonRelease: which mouse button.</summary>
        public MacroMouseButton MouseButton
        {
            get => _mouseButton;
            set
            {
                if (SetProperty(ref _mouseButton, value))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        // ── Input device axis source ──

        private MacroAxisSource _axisSource = MacroAxisSource.OutputController;

        /// <summary>Where to read axis values: from the virtual controller output or a physical input device.</summary>
        public MacroAxisSource AxisSource
        {
            get => _axisSource;
            set
            {
                if (SetProperty(ref _axisSource, value))
                {
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(IsDeviceAxisSource));
                    OnPropertyChanged(nameof(IsOutputAxisSource));
                }
            }
        }

        private Guid _sourceDeviceGuid = Guid.Empty;

        /// <summary>For InputDevice axis source: the InstanceGuid of the physical device to read from.</summary>
        public Guid SourceDeviceGuid
        {
            get => _sourceDeviceGuid;
            set => SetProperty(ref _sourceDeviceGuid, value);
        }

        private int _sourceDeviceAxisIndex = -1;

        /// <summary>For InputDevice axis source: which axis index to read from the device's InputState.Axis[].</summary>
        public int SourceDeviceAxisIndex
        {
            get => _sourceDeviceAxisIndex;
            set => SetProperty(ref _sourceDeviceAxisIndex, value);
        }

        /// <summary>Human-readable display text for the action list.</summary>
        public string DisplayText
        {
            get
            {
                var keyDisplay = !string.IsNullOrEmpty(_keyString) ? _keyString : ResolveKeyName(_keyCode);
                string btnText = _buttonStyle == MacroButtonStyle.Numbered
                    ? MacroButtonNames.FormatCustomButtons(_customButtonWords)
                    : MacroButtonNames.FormatButtons(_buttonFlags, _buttonStyle);
                string axisLabel = _axisSource == MacroAxisSource.InputDevice
                    ? string.Format(Strings.Instance.Macro_DeviceAxis_Format, _sourceDeviceAxisIndex)
                    : _axisTarget.DisplayName();
                return _type switch
                {
                    MacroActionType.ButtonPress => string.Format(Strings.Instance.MacroAction_Press_Format, btnText, _durationMs),
                    MacroActionType.ButtonRelease => string.Format(Strings.Instance.MacroAction_Release_Format, btnText),
                    MacroActionType.KeyPress => string.Format(Strings.Instance.MacroAction_KeyPress_Format, keyDisplay, _durationMs),
                    MacroActionType.KeyRelease => string.Format(Strings.Instance.MacroAction_KeyRelease_Format, keyDisplay),
                    MacroActionType.Delay => string.Format(Strings.Instance.MacroAction_Wait_Format, _durationMs),
                    MacroActionType.AxisSet => string.Format(Strings.Instance.MacroAction_SetAxis_Format, _axisTarget, _axisValue),
                    MacroActionType.SystemVolume => _volumeLimit < 100
                        ? string.Format(Strings.Instance.MacroAction_SysVolLimit_Format, axisLabel, _volumeLimit)
                        : string.Format(Strings.Instance.MacroAction_SysVol_Format, axisLabel),
                    MacroActionType.AppVolume => string.IsNullOrEmpty(_processName)
                        ? (_volumeLimit < 100 ? string.Format(Strings.Instance.MacroAction_AppVolLimit_Format, axisLabel, _volumeLimit) : string.Format(Strings.Instance.MacroAction_AppVol_Format, axisLabel))
                        : (_volumeLimit < 100 ? string.Format(Strings.Instance.MacroAction_AppVolLimit_Format, $"{axisLabel} ({_processName})", _volumeLimit) : string.Format(Strings.Instance.MacroAction_AppVol_Format, $"{axisLabel} ({_processName})")),
                    MacroActionType.MouseMove => string.Format(Strings.Instance.MacroAction_MouseMove_Format, axisLabel, _mouseSensitivity),
                    MacroActionType.MouseButtonPress => string.Format(Strings.Instance.MacroAction_MousePress_Format, MacroMouseButtonDisplayName(_mouseButton)),
                    MacroActionType.MouseButtonRelease => string.Format(Strings.Instance.MacroAction_MouseRelease_Format, MacroMouseButtonDisplayName(_mouseButton)),
                    MacroActionType.MouseScroll => string.Format(Strings.Instance.MacroAction_Scroll_Format, axisLabel, _mouseSensitivity),
                    MacroActionType.ToggleTouchpadOverlay => Strings.Instance.MacroAction_ToggleTouchpadOverlay,
                    MacroActionType.LightbarColor => FormatLightbarColorSummary(),
                    MacroActionType.LightbarColorClear => Strings.Instance.MacroAction_LightbarColorClear,
                    MacroActionType.LightbarModeSet => string.Format(
                        Strings.Instance.MacroAction_LightbarModeSet_Format,
                        LightbarModeDisplayName(_lightbarTargetMode)),
                    MacroActionType.LightbarModeCycle => string.Format(
                        Strings.Instance.MacroAction_LightbarModeCycle_Format,
                        CountSelectedCycleModes()),
                    MacroActionType.Rumble => FormatRumbleSummary(),
                    MacroActionType.RumbleStop => Strings.Instance.MacroAction_RumbleStop,
                    MacroActionType.PlaySound => string.IsNullOrEmpty(_soundFilePath)
                        ? Strings.Instance.MacroAction_Type_PlaySound
                        : string.Format(
                            _soundLoop ? Strings.Instance.MacroAction_PlaySoundLoop_Format
                                       : Strings.Instance.MacroAction_PlaySound_Format,
                            SoundFileName, _soundVolume),
                    MacroActionType.SoundStop => Strings.Instance.MacroAction_Type_SoundStop,
                    MacroActionType.SetGyroEngaged => string.Format(
                        Strings.Instance.MacroAction_SetGyroEngaged_Format,
                        SetGyroEngagedModeDisplayName(_setGyroEngagedMode)),
                    _ => Strings.Instance.Macro_UnknownAction
                };
            }
        }

        // ── Lightbar action display helpers ──

        private string FormatLightbarColorSummary()
        {
            string colorPart = _lightbarColorSource switch
            {
                MacroLightbarColorSource.RandomHue   => Strings.Instance.Macro_LightbarColorSource_RandomHue,
                MacroLightbarColorSource.PaletteStep => Strings.Instance.Macro_LightbarColorSource_PaletteStep,
                _                                    => $"#{_lightbarR:X2}{_lightbarG:X2}{_lightbarB:X2}"
            };
            // Sticky always uses Fixed (the picker's RGB).
            if (_lightbarHoldMode == MacroLightbarHoldMode.Sticky)
                colorPart = $"#{_lightbarR:X2}{_lightbarG:X2}{_lightbarB:X2}";
            return _lightbarHoldMode == MacroLightbarHoldMode.Sticky
                ? string.Format(Strings.Instance.MacroAction_LightbarColor_Sticky_Format, colorPart)
                : string.Format(Strings.Instance.MacroAction_LightbarColor_Reactive_Format, colorPart, _lightbarHoldMs + _lightbarFadeMs);
        }

        // ── Rumble action display helper ──

        private string FormatRumbleSummary()
        {
            // "L100/R100" style motor descriptor — concise and reads
            // identically across locales without needing translation.
            string motors = $"L{_rumbleStrengthLeft}/R{_rumbleStrengthRight}";
            return _rumbleHoldMode == MacroRumbleHoldMode.Sticky
                ? string.Format(Strings.Instance.MacroAction_Rumble_Sticky_Format, motors)
                : string.Format(Strings.Instance.MacroAction_Rumble_Reactive_Format, motors, _rumbleHoldMs + _rumbleFadeMs);
        }

        /// <summary>Counts the modes selected in
        /// <see cref="LightbarCycleModesCsv"/>. Used for the cycle
        /// action's display string.</summary>
        public int CountSelectedCycleModes()
        {
            if (string.IsNullOrEmpty(_lightbarCycleModesCsv)) return 0;
            int count = 0;
            foreach (var token in _lightbarCycleModesCsv.Split(','))
                if (int.TryParse(token, out _)) count++;
            return count;
        }

        /// <summary>Parses <see cref="LightbarCycleModesCsv"/> into an
        /// array of <see cref="LightbarMode"/> values (skipping invalid
        /// tokens). Empty array if the CSV is empty or unparseable.</summary>
        public LightbarMode[] ParsedCycleModes()
        {
            if (string.IsNullOrEmpty(_lightbarCycleModesCsv)) return Array.Empty<LightbarMode>();
            var list = new System.Collections.Generic.List<LightbarMode>();
            foreach (var token in _lightbarCycleModesCsv.Split(','))
            {
                if (int.TryParse(token.Trim(), out int v) && Enum.IsDefined(typeof(LightbarMode), v))
                    list.Add((LightbarMode)v);
            }
            return list.ToArray();
        }

        internal static string LightbarModeDisplayName(LightbarMode mode)
        {
            var s = Strings.Instance;
            return mode switch
            {
                LightbarMode.Off                => s.Pad_Lighting_Mode_Off,
                LightbarMode.Static             => s.Pad_Lighting_Mode_Static,
                LightbarMode.Breathing          => s.Pad_Lighting_Mode_Breathing,
                LightbarMode.Rainbow            => s.Pad_Lighting_Mode_Rainbow,
                LightbarMode.ColorCycle         => s.Pad_Lighting_Mode_ColorCycle,
                LightbarMode.AudioPulse         => s.Pad_Lighting_Mode_AudioPulse,
                LightbarMode.AudioPulseRandom   => s.Pad_Lighting_Mode_AudioPulseRandom,
                LightbarMode.AudioPulseRainbow  => s.Pad_Lighting_Mode_AudioPulseRainbow,
                LightbarMode.AudioThresholds    => s.Pad_Lighting_Mode_AudioThresholds,
                LightbarMode.AudioGradient      => s.Pad_Lighting_Mode_AudioGradient,
                LightbarMode.AudioCrossFade     => s.Pad_Lighting_Mode_AudioCrossFade,
                LightbarMode.InputReactive      => s.Pad_Lighting_Mode_InputReactive,
                LightbarMode.InputReactiveCycle => s.Pad_Lighting_Mode_InputReactiveCycle,
                LightbarMode.InputReactiveFixed => s.Pad_Lighting_Mode_InputReactiveFixed,
                _ => mode.ToString()
            };
        }

        /// <summary>
        /// Resolves a virtual key code to a human-readable name using the VirtualKey enum.
        /// Falls back to hex notation if the code is not a known enum member.
        /// </summary>
        private static string ResolveKeyName(int keyCode)
        {
            if (Enum.IsDefined(typeof(VirtualKey), keyCode))
                return ((VirtualKey)keyCode).ToString();
            return $"0x{keyCode:X2}";
        }

        /// <summary>Returns the localized display name for a mouse button.</summary>
        private static string MacroMouseButtonDisplayName(MacroMouseButton btn) => btn switch
        {
            MacroMouseButton.Left => Strings.Instance.Macro_MouseLeft,
            MacroMouseButton.Right => Strings.Instance.Macro_MouseRight,
            MacroMouseButton.Middle => Strings.Instance.Macro_MouseMiddle,
            MacroMouseButton.X1 => Strings.Instance.Macro_MouseX1,
            MacroMouseButton.X2 => Strings.Instance.Macro_MouseX2,
            _ => btn.ToString()
        };

        /// <summary>Localized display name for the SetGyroEngaged
        /// write mode.</summary>
        private static string SetGyroEngagedModeDisplayName(MacroSetGyroEngagedMode mode) => mode switch
        {
            MacroSetGyroEngagedMode.On     => Strings.Instance.Macro_SetGyroEngaged_On,
            MacroSetGyroEngagedMode.Off    => Strings.Instance.Macro_SetGyroEngaged_Off,
            MacroSetGyroEngagedMode.Toggle => Strings.Instance.Macro_SetGyroEngaged_Toggle,
            _ => mode.ToString()
        };
    }

    // ─────────────────────────────────────────────
    //  Enums
    // ─────────────────────────────────────────────

    public enum MacroTriggerMode
    {
        /// <summary>Fire once when the trigger combo is first pressed.</summary>
        OnPress,

        /// <summary>Fire once when the trigger combo is released.</summary>
        OnRelease,

        /// <summary>Fire repeatedly while the trigger combo is held.</summary>
        WhileHeld,

        /// <summary>Runs continuously without any trigger button requirement.</summary>
        Always,

        /// <summary>Fire on rising edge of a user-defined formula over a/b/c/...
        /// variables. Each variable binds to an input-device input or an
        /// virtual-controller-channel value; the compiled formula evaluates to a
        /// float per frame and "trigger active" is <c>result &gt;= 0.5</c>.</summary>
        CustomExpression
    }

    public enum MacroTriggerSource
    {
        /// <summary>Record from the physical input device's raw/native buttons.</summary>
        InputDevice,

        /// <summary>Record from the slot's combined virtual controller output.</summary>
        OutputController
    }

    public enum MacroRepeatMode
    {
        /// <summary>Execute the action sequence once.</summary>
        Once,

        /// <summary>Repeat a fixed number of times.</summary>
        FixedCount,

        /// <summary>Repeat until the trigger is released (WhileHeld mode only).</summary>
        UntilRelease
    }

    public enum MacroActionType
    {
        /// <summary>Press controller button(s) for a duration.</summary>
        ButtonPress,

        /// <summary>Release controller button(s).</summary>
        ButtonRelease,

        /// <summary>Press a keyboard key via SendInput.</summary>
        KeyPress,

        /// <summary>Release a keyboard key.</summary>
        KeyRelease,

        /// <summary>Pause for a duration before the next action.</summary>
        Delay,

        /// <summary>Set an axis to a specific value.</summary>
        AxisSet,

        /// <summary>Continuously map a source axis value to the Windows system volume.</summary>
        SystemVolume,

        /// <summary>Continuously map a source axis value to a specific application's volume in the Windows mixer.</summary>
        AppVolume,

        /// <summary>Continuously map a source axis to mouse cursor movement.</summary>
        MouseMove,

        /// <summary>Press a mouse button via SendInput.</summary>
        MouseButtonPress,

        /// <summary>Release a mouse button via SendInput.</summary>
        MouseButtonRelease,

        /// <summary>Continuously map a source axis to mouse scroll wheel.</summary>
        MouseScroll,

        /// <summary>Toggle the touchpad overlay visibility.</summary>
        ToggleTouchpadOverlay,

        /// <summary>Override the assigned PlayStation slot's lightbar.
        /// Two hold modes: <see cref="MacroLightbarHoldMode.Reactive"/>
        /// fires a decay-fade flash like the InputReactive lightbar mode
        /// (configurable color source and decay length);
        /// <see cref="MacroLightbarHoldMode.Sticky"/> holds the chosen
        /// RGB until a <see cref="LightbarColorClear"/> action releases
        /// it. Game-driven writes still win over the override at the
        /// packet level.</summary>
        LightbarColor,

        /// <summary>Releases any active lightbar override on the
        /// assigned slot. Pair with <see cref="LightbarColor"/> Sticky
        /// to give the user a deliberate way to undo the hold via
        /// another macro.</summary>
        LightbarColorClear,

        /// <summary>Sets the slot's <c>LightbarMode</c> to a specific
        /// value. Persists like any other Lighting-tab edit until
        /// another action or the user changes it.</summary>
        LightbarModeSet,

        /// <summary>Cycles the slot's <c>LightbarMode</c> through a
        /// user-selected subset of modes. Each fire advances to the
        /// next checked mode. Cycle position is per-action and
        /// volatile — resets on app restart.</summary>
        LightbarModeCycle,

        /// <summary>Sets the slot's gyro engage state. <c>Mode</c>
        /// controls the write: <c>Toggle</c> flips the per-slot
        /// macro-engaged bit, <c>On</c> forces it true, <c>Off</c>
        /// forces it false. The bit OR-combines with the
        /// <c>GyroAimEngageButton</c>'s per-slot bit at the gyro
        /// evaluator. Bit is per-slot volatile and resets on profile
        /// switch and app restart.</summary>
        SetGyroEngaged,

        /// <summary>Drives the slot's macro rumble override. Two hold
        /// modes parallel <see cref="LightbarColor"/>:
        /// <see cref="MacroRumbleHoldMode.Reactive"/> fires a one-shot
        /// pulse with full-intensity hold + decay-fade tail (configurable
        /// via <c>RumbleHoldMs</c> / <c>RumbleFadeMs</c>);
        /// <see cref="MacroRumbleHoldMode.Sticky"/> holds at full
        /// intensity until a <see cref="RumbleStop"/> action releases
        /// it. Per-motor strength via <c>RumbleStrengthLeft</c> /
        /// <c>RumbleStrengthRight</c>. Combines with game-driven rumble
        /// via max() so user-driven feedback is always felt.</summary>
        Rumble,

        /// <summary>Releases any active rumble override on the slot.
        /// Pair with <see cref="Rumble"/> Sticky to give the user a
        /// deliberate way to undo the hold via another macro.</summary>
        RumbleStop,

        /// <summary>Plays a sound file (issue #83). Routed through
        /// <c>SoundMacroService</c> to the slot's configured output device
        /// (Audio tab) — the system default or a specific endpoint such as
        /// a USB DualSense's controller speaker. <c>SoundFilePath</c> picks
        /// the file (wav / mp3 / m4a / aac / wma / flac via Media
        /// Foundation), <c>SoundVolume</c> scales it (multiplied with the
        /// slot's master volume), and <c>SoundLoop</c> loops it until a
        /// <see cref="SoundStop"/> action or — for While-Held /
        /// Until-Release macros — the trigger's release stops it. One-shots
        /// always play to completion.</summary>
        PlaySound,

        /// <summary>Stops every macro sound on the slot. Pair with
        /// <see cref="PlaySound"/> Loop to give the user a deliberate way
        /// to end a looping sound via another macro.</summary>
        SoundStop
    }

    /// <summary>Write mode for the
    /// <see cref="MacroActionType.SetGyroEngaged"/> action.</summary>
    public enum MacroSetGyroEngagedMode
    {
        /// <summary>Flip the slot's macro-engaged bit. Each fire toggles.</summary>
        Toggle = 0,
        /// <summary>Force the slot's macro-engaged bit true.</summary>
        On = 1,
        /// <summary>Force the slot's macro-engaged bit false.</summary>
        Off = 2
    }

    /// <summary>Hold mode for <see cref="MacroActionType.Rumble"/>.
    /// Parallel to <see cref="MacroLightbarHoldMode"/>; intensity is
    /// applied to both motors equally over the same hold/fade window.</summary>
    public enum MacroRumbleHoldMode
    {
        /// <summary>Decay-fade pulse. Motors run at full configured
        /// strength across the hold window, then ramp to zero across
        /// the fade window. Mirrors the lightbar Reactive hold.</summary>
        Reactive = 0,
        /// <summary>Held at full strength until a
        /// <see cref="MacroActionType.RumbleStop"/> action runs.</summary>
        Sticky = 1
    }

    /// <summary>Hold mode for <see cref="MacroActionType.LightbarColor"/>.</summary>
    public enum MacroLightbarHoldMode
    {
        /// <summary>Decay-fade flash. Intensity ramps from 1.0 to 0.0
        /// across the configured decay window, then expires and the
        /// configured Lighting-tab mode takes back over. Mirrors the
        /// existing InputReactive lightbar mode.</summary>
        Reactive = 0,
        /// <summary>Held at full intensity until a
        /// <see cref="MacroActionType.LightbarColorClear"/> action
        /// runs (or the slot's device unbinds).</summary>
        Sticky = 1
    }

    /// <summary>Color source for a Reactive
    /// <see cref="MacroActionType.LightbarColor"/>. Sticky always
    /// uses Fixed.</summary>
    public enum MacroLightbarColorSource
    {
        /// <summary>The action's configured RGB.</summary>
        Fixed = 0,
        /// <summary>A fresh random hue rolled at fire time.</summary>
        RandomHue = 1,
        /// <summary>The next entry in the slot's <c>LightbarPalette</c>,
        /// advancing per fire.</summary>
        PaletteStep = 2
    }

    public enum MacroMouseButton
    {
        Left,
        Right,
        Middle,
        X1,
        X2
    }

    public enum MacroAxisTarget
    {
        None,
        LeftStickX,
        LeftStickY,
        RightStickX,
        RightStickY,
        LeftTrigger,
        RightTrigger
    }

    public static class MacroAxisTargetNames
    {
        /// <summary>
        /// Returns a user-friendly display name matching the mapping target labels.
        /// </summary>
        public static string DisplayName(this MacroAxisTarget target) => target switch
        {
            MacroAxisTarget.LeftStickX => Strings.Instance.MacroAxis_XAxis,
            MacroAxisTarget.LeftStickY => Strings.Instance.MacroAxis_YAxis,
            MacroAxisTarget.RightStickX => Strings.Instance.MacroAxis_XRotation,
            MacroAxisTarget.RightStickY => Strings.Instance.MacroAxis_YRotation,
            MacroAxisTarget.LeftTrigger => Strings.Instance.MacroAxis_ZAxis,
            MacroAxisTarget.RightTrigger => Strings.Instance.MacroAxis_ZRotation,
            _ => target.ToString()
        };
    }

    /// <summary>Direction filter for axis-based macro triggers.</summary>
    public enum MacroAxisDirection
    {
        /// <summary>Fire regardless of axis direction (existing behavior).</summary>
        Any,

        /// <summary>Fire only when the axis value is positive (e.g., stick right, trigger pressed).</summary>
        Positive,

        /// <summary>Fire only when the axis value is negative (e.g., stick left).</summary>
        Negative
    }

    /// <summary>Channels on the slot's combined virtual controller output
    /// that a <see cref="MacroExpressionVariable"/> can sample when its
    /// <c>Source</c> is <see cref="MacroTriggerSource.OutputController"/>.
    /// The engine normalizes every slot to an XInput-shaped <c>Gamepad</c>
    /// struct regardless of the slot's configured VC family (Xbox /
    /// PlayStation / Switch / Extended), so the channel set below is the
    /// stable union. Buttons read 0.0/1.0, triggers read 0..1, sticks read
    /// 0..1 with 0.5 as the rest position (so the expression sees a uniform
    /// 0..1 domain like the merge-mapping evaluator does).</summary>
    public enum MacroOutputChannel
    {
        None,
        A, B, X, Y,
        LB, RB,
        LS, RS,
        Back, Start, Guide,
        DpadUp, DpadDown, DpadLeft, DpadRight,
        LT, RT,
        LX, LY, RX, RY
    }

    /// <summary>One a/b/c/... variable that a macro's custom-expression
    /// trigger can reference. Binds either to an input-device input
    /// (raw button / POV / axis) or to a channel on the slot's combined
    /// virtual controller output. Serialized as a compact tagged string so
    /// the XML stays one element per variable rather than a nested block.</summary>
    public sealed class MacroExpressionVariable : ObservableObject
    {
        private MacroTriggerSource _source = MacroTriggerSource.InputDevice;
        public MacroTriggerSource Source
        {
            get => _source;
            set
            {
                if (SetProperty(ref _source, value))
                {
                    // Picking OutputController without choosing a channel
                    // would leave the row unbound — seed a sensible default
                    // so the variable becomes immediately useful.
                    if (_source == MacroTriggerSource.OutputController && _outputChannel == MacroOutputChannel.None)
                        OutputChannel = MacroOutputChannel.A;
                    OnPropertyChanged(nameof(DisplaySummary));
                }
            }
        }

        private Guid _deviceGuid;
        public Guid DeviceGuid
        {
            get => _deviceGuid;
            set { if (SetProperty(ref _deviceGuid, value)) OnPropertyChanged(nameof(DisplaySummary)); }
        }

        private int _rawButton = -1;
        /// <summary>Raw device button index (0-based) when Source=InputDevice;
        /// -1 when not used.</summary>
        public int RawButton
        {
            get => _rawButton;
            set { if (SetProperty(ref _rawButton, value)) OnPropertyChanged(nameof(DisplaySummary)); }
        }

        private string _pov;
        /// <summary>"povIndex:centidegrees" form when Source=InputDevice and
        /// the variable samples a POV direction; null when not used.</summary>
        public string Pov
        {
            get => _pov;
            set { if (SetProperty(ref _pov, string.IsNullOrEmpty(value) ? null : value)) OnPropertyChanged(nameof(DisplaySummary)); }
        }

        private MacroAxisTarget _axisTarget = MacroAxisTarget.None;
        public MacroAxisTarget AxisTarget
        {
            get => _axisTarget;
            set { if (SetProperty(ref _axisTarget, value)) OnPropertyChanged(nameof(DisplaySummary)); }
        }

        private MacroOutputChannel _outputChannel = MacroOutputChannel.None;
        public MacroOutputChannel OutputChannel
        {
            get => _outputChannel;
            set { if (SetProperty(ref _outputChannel, value)) OnPropertyChanged(nameof(DisplaySummary)); }
        }

        /// <summary>True if this variable has a usable binding (one of
        /// raw-button / POV / axis / output-channel populated). An unbound
        /// variable evaluates to 0.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsBound
        {
            get
            {
                if (_source == MacroTriggerSource.OutputController) return _outputChannel != MacroOutputChannel.None;
                return _rawButton >= 0 || !string.IsNullOrEmpty(_pov) || _axisTarget != MacroAxisTarget.None;
            }
        }

        /// <summary>Compact tagged form used for XML round-tripping.
        /// "in:GUID:btn:N", "in:GUID:pov:idx:cd", "in:GUID:ax:Target",
        /// "out:Channel". Empty for unbound.</summary>
        public string Spec
        {
            get
            {
                if (_source == MacroTriggerSource.OutputController)
                    return _outputChannel == MacroOutputChannel.None ? "" : $"out:{_outputChannel}";
                if (_deviceGuid == Guid.Empty) return "";
                if (_rawButton >= 0) return $"in:{_deviceGuid}:btn:{_rawButton}";
                if (!string.IsNullOrEmpty(_pov)) return $"in:{_deviceGuid}:pov:{_pov}";
                if (_axisTarget != MacroAxisTarget.None) return $"in:{_deviceGuid}:ax:{_axisTarget}";
                return "";
            }
            set
            {
                _source = MacroTriggerSource.InputDevice;
                _deviceGuid = Guid.Empty;
                _rawButton = -1;
                _pov = null;
                _axisTarget = MacroAxisTarget.None;
                _outputChannel = MacroOutputChannel.None;
                if (!string.IsNullOrEmpty(value))
                {
                    var parts = value.Split(':');
                    if (parts.Length >= 2 && parts[0] == "out")
                    {
                        _source = MacroTriggerSource.OutputController;
                        if (Enum.TryParse<MacroOutputChannel>(parts[1], out var ch)) _outputChannel = ch;
                    }
                    else if (parts.Length >= 4 && parts[0] == "in" && Guid.TryParse(parts[1], out var g))
                    {
                        _deviceGuid = g;
                        switch (parts[2])
                        {
                            case "btn":
                                if (int.TryParse(parts[3], out var bi)) _rawButton = bi;
                                break;
                            case "pov":
                                if (parts.Length >= 5) _pov = $"{parts[3]}:{parts[4]}";
                                break;
                            case "ax":
                                if (Enum.TryParse<MacroAxisTarget>(parts[3], out var at)) _axisTarget = at;
                                break;
                        }
                    }
                }
                OnPropertyChanged(nameof(Source));
                OnPropertyChanged(nameof(DeviceGuid));
                OnPropertyChanged(nameof(RawButton));
                OnPropertyChanged(nameof(Pov));
                OnPropertyChanged(nameof(AxisTarget));
                OnPropertyChanged(nameof(OutputChannel));
                OnPropertyChanged(nameof(DisplaySummary));
            }
        }

        /// <summary>Short human-readable summary for the variable row UI.
        /// Resolves device-aware names (e.g. real button label "X" instead
        /// of "Button 13") when the device is online.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public string DisplaySummary
        {
            get
            {
                if (_isRecording) return string.IsNullOrEmpty(_liveText) ? Strings.Instance.Macro_RecordHint : _liveText;
                if (!IsBound) return Strings.Instance.Macro_NotSet;
                if (_source == MacroTriggerSource.OutputController)
                    return _outputChannel.ToString();

                string devName = ResolveDeviceName();
                string prefix = string.IsNullOrEmpty(devName) ? "" : devName + " · ";

                if (_rawButton >= 0)
                {
                    var objects = ResolveDeviceObjects();
                    var obj = objects?.FirstOrDefault(o => o.IsButton && o.InputIndex == _rawButton);
                    string label = obj != null && !string.IsNullOrEmpty(obj.Name)
                        ? obj.Name
                        : string.Format(Strings.Instance.Macro_Button_Format, _rawButton);
                    return prefix + label;
                }
                if (!string.IsNullOrEmpty(_pov)) return prefix + MacroItem.FormatPovTrigger(_pov);
                if (_axisTarget != MacroAxisTarget.None) return prefix + _axisTarget.DisplayName();
                return "";
            }
        }

        private string ResolveDeviceName()
        {
            if (_deviceGuid == Guid.Empty) return null;
            var ud = SettingsManager.FindDeviceByInstanceGuid(_deviceGuid);
            return ud?.ResolvedName;
        }

        private DeviceObjectItem[] ResolveDeviceObjects()
        {
            if (_deviceGuid == Guid.Empty) return null;
            var ud = SettingsManager.FindDeviceByInstanceGuid(_deviceGuid);
            return ud?.DeviceObjects;
        }

        // ── Recording state for the per-row Record button ──

        private bool _isRecording;
        [System.Xml.Serialization.XmlIgnore]
        public bool IsRecording
        {
            get => _isRecording;
            set
            {
                if (SetProperty(ref _isRecording, value))
                {
                    OnPropertyChanged(nameof(RecordIcon));
                    OnPropertyChanged(nameof(RecordTooltip));
                    OnPropertyChanged(nameof(DisplaySummary));
                }
            }
        }

        private string _liveText = "";
        /// <summary>Live in-progress feedback shown while a record session is
        /// running for this variable (e.g. "Press any input…").</summary>
        [System.Xml.Serialization.XmlIgnore]
        public string LiveText
        {
            get => _liveText;
            set
            {
                if (SetProperty(ref _liveText, value ?? ""))
                    OnPropertyChanged(nameof(DisplaySummary));
            }
        }

        [System.Xml.Serialization.XmlIgnore]
        public string RecordIcon => _isRecording ? "" : ""; // Stop : Record

        [System.Xml.Serialization.XmlIgnore]
        public string RecordTooltip => _isRecording ? Strings.Instance.Common_Stop : Strings.Instance.Macro_RecordTrigger;

        private RelayCommand _recordCommand;
        public RelayCommand RecordCommand =>
            _recordCommand ??= new RelayCommand(() =>
            {
                IsRecording = !IsRecording;
                RecordRequested?.Invoke(this, EventArgs.Empty);
            });

        private RelayCommand _clearBindingCommand;
        public RelayCommand ClearBindingCommand =>
            _clearBindingCommand ??= new RelayCommand(() =>
            {
                DeviceGuid = Guid.Empty;
                RawButton = -1;
                Pov = null;
                AxisTarget = MacroAxisTarget.None;
                OutputChannel = MacroOutputChannel.None;
                LiveText = "";
                OnPropertyChanged(nameof(DisplaySummary));
                OnPropertyChanged(nameof(Spec));
            });

        /// <summary>Raised when the user toggles the per-row Record button. The
        /// host wires this to <see cref="Services.InputService.StartExpressionVariableRecording"/>
        /// / Stop, similar to the macro trigger flow.</summary>
        public event EventHandler RecordRequested;
    }

    /// <summary>Where to read axis values from for continuous actions.</summary>
    public enum MacroAxisSource
    {
        /// <summary>Read from the combined virtual controller output (existing behavior).</summary>
        OutputController,

        /// <summary>Read from a physical input device's raw InputState.Axis[].</summary>
        InputDevice
    }

    /// <summary>
    /// Represents a single gamepad button as a toggleable checkbox option.
    /// Reads/writes individual bits from the parent MacroAction's ButtonFlags.
    /// </summary>
    public class GamepadButtonOption : ObservableObject
    {
        private readonly MacroAction _parent;

        private string _label;
        public string Label
        {
            get => _label;
            internal set => SetProperty(ref _label, value);
        }

        /// <summary>Xbox / PlayStation bitmask flag (0 for custom mode).</summary>
        public ushort Flag { get; }

        /// <summary>Custom Extended button index (0-based). -1 = use Flag on ushort.</summary>
        public int CustomIndex { get; }

        public bool IsChecked
        {
            get => CustomIndex >= 0
                ? _parent.IsCustomButtonPressed(CustomIndex)
                : (_parent.ButtonFlags & Flag) != 0;
            set
            {
                if (CustomIndex >= 0)
                {
                    _parent.SetCustomButton(CustomIndex, value);
                }
                else if (value)
                    _parent.ButtonFlags |= Flag;
                else
                    _parent.ButtonFlags = (ushort)(_parent.ButtonFlags & ~Flag);
                OnPropertyChanged();
            }
        }

        /// <summary>Xbox / PlayStation bitmask mode.</summary>
        public GamepadButtonOption(MacroAction parent, string label, ushort flag)
        {
            _parent = parent;
            _label = label;
            Flag = flag;
            CustomIndex = -1;
        }

        /// <summary>Custom Extended button index mode (0-based).</summary>
        public GamepadButtonOption(MacroAction parent, string label, int customIndex)
        {
            _parent = parent;
            _label = label;
            Flag = 0;
            CustomIndex = customIndex;
        }

        /// <summary>Re-evaluates IsChecked when button state is changed externally.</summary>
        public void Refresh() => OnPropertyChanged(nameof(IsChecked));
    }

    /// <summary>
    /// Determines which set of button labels to display in macros.
    /// </summary>
    public enum MacroButtonStyle
    {
        Xbox360,
        DualShock4,
        Numbered  // Extended Custom: "Btn 1", "Btn 2", etc.
    }

    /// <summary>One <see cref="MacroOutputChannel"/> + its localized display
    /// label, sized for the macro's current <see cref="MacroButtonStyle"/>.
    /// Used as ItemsSource for the variable-row OutputChannel dropdown so
    /// the labels match the mapping table for the same VC family
    /// (e.g. PlayStation slot shows ✕/○/□/△ and L1/R1, Extended shows
    /// Button 1 / Button 2 / ..., Xbox shows A/B/X/Y and Left Shoulder).</summary>
    public sealed class MacroOutputChannelOption
    {
        public MacroOutputChannel Value { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>Display-name resolver for <see cref="MacroOutputChannel"/> values.
    /// Mirrors the mapping table's labels in
    /// <c>PadViewModel.InitializeGamepadMappings</c> so the macro's OutputController
    /// dropdown reads the same way as the row labels users already know.</summary>
    public static class MacroOutputChannelNames
    {
        public static string DisplayName(MacroOutputChannel channel, MacroButtonStyle style)
        {
            var s = Strings.Instance;
            // Stick axes + D-Pad: same label across all styles (matches the
            // mapping table — D-Pad rows always use Btn_DPadUp etc.).
            switch (channel)
            {
                case MacroOutputChannel.LX: return s.Btn_LeftStickX;
                case MacroOutputChannel.LY: return s.Btn_LeftStickY;
                case MacroOutputChannel.RX: return s.Btn_RightStickX;
                case MacroOutputChannel.RY: return s.Btn_RightStickY;
                case MacroOutputChannel.DpadUp:    return s.Btn_DPadUp;
                case MacroOutputChannel.DpadDown:  return s.Btn_DPadDown;
                case MacroOutputChannel.DpadLeft:  return s.Btn_DPadLeft;
                case MacroOutputChannel.DpadRight: return s.Btn_DPadRight;
            }
            switch (style)
            {
                case MacroButtonStyle.DualShock4:
                    return channel switch
                    {
                        MacroOutputChannel.A => "✕",  // ✕ (matches PS mapping row)
                        MacroOutputChannel.B => "○",  // ○ (matches PS mapping row)
                        MacroOutputChannel.X => "◻",  // ◻ (matches PS mapping row)
                        MacroOutputChannel.Y => "△",  // △ (matches PS mapping row)
                        MacroOutputChannel.LB => "L1",
                        MacroOutputChannel.RB => "R1",
                        MacroOutputChannel.LS => "L3",
                        MacroOutputChannel.RS => "R3",
                        MacroOutputChannel.Back => s.Btn_Share,
                        MacroOutputChannel.Start => s.Btn_Options,
                        MacroOutputChannel.Guide => s.Btn_PS,
                        MacroOutputChannel.LT => "L2",
                        MacroOutputChannel.RT => "R2",
                        _ => channel.ToString()
                    };
                case MacroButtonStyle.Numbered:
                    return channel switch
                    {
                        MacroOutputChannel.A     => string.Format(s.Extended_Button_Format, 1),
                        MacroOutputChannel.B     => string.Format(s.Extended_Button_Format, 2),
                        MacroOutputChannel.X     => string.Format(s.Extended_Button_Format, 3),
                        MacroOutputChannel.Y     => string.Format(s.Extended_Button_Format, 4),
                        MacroOutputChannel.LB    => string.Format(s.Extended_Button_Format, 5),
                        MacroOutputChannel.RB    => string.Format(s.Extended_Button_Format, 6),
                        MacroOutputChannel.Back  => string.Format(s.Extended_Button_Format, 7),
                        MacroOutputChannel.Start => string.Format(s.Extended_Button_Format, 8),
                        MacroOutputChannel.LS    => string.Format(s.Extended_Button_Format, 9),
                        MacroOutputChannel.RS    => string.Format(s.Extended_Button_Format, 10),
                        MacroOutputChannel.Guide => string.Format(s.Extended_Button_Format, 11),
                        MacroOutputChannel.LT    => s.Btn_LeftTrigger,
                        MacroOutputChannel.RT    => s.Btn_RightTrigger,
                        _ => channel.ToString()
                    };
                default: // Xbox360
                    return channel switch
                    {
                        MacroOutputChannel.A     => "A",
                        MacroOutputChannel.B     => "B",
                        MacroOutputChannel.X     => "X",
                        MacroOutputChannel.Y     => "Y",
                        MacroOutputChannel.LB    => s.Btn_LeftShoulder,
                        MacroOutputChannel.RB    => s.Btn_RightShoulder,
                        MacroOutputChannel.LS    => s.Btn_LeftStickButton,
                        MacroOutputChannel.RS    => s.Btn_RightStickButton,
                        MacroOutputChannel.Back  => s.Btn_Back,
                        MacroOutputChannel.Start => s.Btn_Start,
                        MacroOutputChannel.Guide => s.Btn_Guide,
                        MacroOutputChannel.LT    => s.Btn_LeftTrigger,
                        MacroOutputChannel.RT    => s.Btn_RightTrigger,
                        _ => channel.ToString()
                    };
            }
        }

        /// <summary>Builds the full option list for the given style in stable
        /// presentation order (face → shoulders → start/back/guide → sticks →
        /// d-pad → triggers → axes). The order mirrors the mapping table so
        /// users find what they're looking for in roughly the same place.</summary>
        public static List<MacroOutputChannelOption> GetOptions(MacroButtonStyle style)
        {
            var order = new[]
            {
                MacroOutputChannel.A, MacroOutputChannel.B, MacroOutputChannel.X, MacroOutputChannel.Y,
                MacroOutputChannel.LB, MacroOutputChannel.RB,
                MacroOutputChannel.Back, MacroOutputChannel.Start, MacroOutputChannel.Guide,
                MacroOutputChannel.LS, MacroOutputChannel.RS,
                MacroOutputChannel.DpadUp, MacroOutputChannel.DpadDown, MacroOutputChannel.DpadLeft, MacroOutputChannel.DpadRight,
                MacroOutputChannel.LT, MacroOutputChannel.RT,
                MacroOutputChannel.LX, MacroOutputChannel.LY, MacroOutputChannel.RX, MacroOutputChannel.RY,
            };
            var list = new List<MacroOutputChannelOption>(order.Length);
            foreach (var ch in order)
                list.Add(new MacroOutputChannelOption { Value = ch, Name = DisplayName(ch, style) });
            return list;
        }
    }

    public static class MacroButtonNames
    {
        /// <summary>
        /// Returns the button label/flag pairs for the given style.
        /// Flags are always the same Xbox-standard bitmask; only labels differ.
        /// </summary>
        public static (string Label, ushort Flag)[] GetButtonDefs(MacroButtonStyle style) => style switch
        {
            MacroButtonStyle.DualShock4 => BuildDS4Defs(),
            MacroButtonStyle.Numbered => BuildNumberedDefs(),
            _ => BuildXboxDefs()
        };

        /// <summary>Formats a button bitmask into a human-readable string.</summary>
        public static string FormatButtons(ushort flags, MacroButtonStyle style)
        {
            if (flags == 0) return Strings.Instance.Macro_None;
            var defs = GetButtonDefs(style);
            return string.Join(" + ", defs.Where(d => (flags & d.Flag) != 0).Select(d => d.Label));
        }

        /// <summary>Formats custom Extended button words into a human-readable string.</summary>
        public static string FormatCustomButtons(uint[] words)
        {
            if (words == null || words.All(w => w == 0)) return Strings.Instance.Macro_None;
            var parts = new List<string>();
            for (int i = 0; i < 128; i++)
            {
                int word = i / 32;
                int bit = i % 32;
                if (word < words.Length && (words[word] & (uint)(1 << bit)) != 0)
                    parts.Add(string.Format(Strings.Instance.Macro_Btn_Format, i + 1));
            }
            return parts.Count > 0 ? string.Join(" + ", parts) : Strings.Instance.Macro_None;
        }

        /// <summary>
        /// Derives the button style from the output controller type. Extended
        /// slots show numbered labels (Btn1, Btn2, ...) since the active
        /// HIDMaestro profile drives the layout — Xbox-style "A B X Y" labels
        /// belong on Xbox slots, DualShock labels on PlayStation slots.
        /// </summary>
        public static MacroButtonStyle DeriveStyle(VirtualControllerType outputType) => outputType switch
        {
            VirtualControllerType.PlayStation => MacroButtonStyle.DualShock4,
            VirtualControllerType.Extended    => MacroButtonStyle.Numbered,
            _                                 => MacroButtonStyle.Xbox360
        };

        private static (string Label, ushort Flag)[] BuildXboxDefs() => new (string, ushort)[]
        {
            ("A", 0x1000), ("B", 0x2000), ("X", 0x4000), ("Y", 0x8000),
            (Strings.Instance.Btn_LeftShoulder, 0x0100), (Strings.Instance.Btn_RightShoulder, 0x0200),
            (Strings.Instance.Btn_Back, 0x0020), (Strings.Instance.Btn_Start, 0x0010),
            (Strings.Instance.Btn_LeftStickButton, 0x0040), (Strings.Instance.Btn_RightStickButton, 0x0080),
            (Strings.Instance.Btn_Guide, 0x0400),
            (Strings.Instance.Btn_Up, 0x0001), (Strings.Instance.Btn_Down, 0x0002),
            (Strings.Instance.Btn_Left, 0x0004), (Strings.Instance.Btn_Right, 0x0008),
        };

        private static (string Label, ushort Flag)[] BuildDS4Defs() => new (string, ushort)[]
        {
            (Strings.Instance.Btn_Cross, 0x1000), (Strings.Instance.Btn_Circle, 0x2000),
            (Strings.Instance.Btn_Square, 0x4000), (Strings.Instance.Btn_Triangle, 0x8000),
            (Strings.Instance.Btn_L1, 0x0100), (Strings.Instance.Btn_R1, 0x0200),
            (Strings.Instance.Btn_Share, 0x0020), (Strings.Instance.Btn_Options, 0x0010),
            (Strings.Instance.Btn_L3, 0x0040), (Strings.Instance.Btn_R3, 0x0080),
            (Strings.Instance.Btn_PS, 0x0400), (Strings.Instance.Btn_Touchpad, 0x0800),
            (Strings.Instance.Btn_Up, 0x0001), (Strings.Instance.Btn_Down, 0x0002),
            (Strings.Instance.Btn_Left, 0x0004), (Strings.Instance.Btn_Right, 0x0008),
        };

        // Extended Custom: Xbox bitmask bits → Extended button numbers (see SubmitGamepadState mapping).
        // D-pad still shows direction names (they map to POV, not buttons).
        private static (string Label, ushort Flag)[] BuildNumberedDefs()
        {
            var s = Strings.Instance;
            return new (string, ushort)[]
            {
                (string.Format(s.Macro_Btn_Format, 1), 0x1000), (string.Format(s.Macro_Btn_Format, 2), 0x2000),
                (string.Format(s.Macro_Btn_Format, 3), 0x4000), (string.Format(s.Macro_Btn_Format, 4), 0x8000),
                (string.Format(s.Macro_Btn_Format, 5), 0x0100), (string.Format(s.Macro_Btn_Format, 6), 0x0200),
                (string.Format(s.Macro_Btn_Format, 7), 0x0020), (string.Format(s.Macro_Btn_Format, 8), 0x0010),
                (string.Format(s.Macro_Btn_Format, 9), 0x0040), (string.Format(s.Macro_Btn_Format, 10), 0x0080),
                (string.Format(s.Macro_Btn_Format, 11), 0x0400),
                (s.Btn_Up, 0x0001), (s.Btn_Down, 0x0002),
                (s.Btn_Left, 0x0004), (s.Btn_Right, 0x0008),
            };
        }
    }

    /// <summary>
    /// Wraps a VirtualKey with a localized display name for ComboBox binding.
    /// </summary>
    public class KeyDisplayItem
    {
        public KeyDisplayItem(VirtualKey key, string displayName)
        {
            Key = key;
            DisplayName = displayName;
        }

        public VirtualKey Key { get; }
        public string DisplayName { get; }

        public override string ToString() => DisplayName;
    }

    /// <summary>Checkbox-bindable option representing one
    /// <see cref="LightbarMode"/> value in the LightbarModeCycle editor's
    /// grid. Reads / writes the parent action's
    /// <c>LightbarCycleModesCsv</c>.</summary>
    public class LightbarModeCycleOption : ObservableObject
    {
        private readonly MacroAction _parent;
        public LightbarMode Mode { get; }

        /// <summary>Live-resolved localized mode name. Reads
        /// <see cref="Strings.Instance"/> on each access so the cached
        /// option list reflows after a culture change without being
        /// rebuilt — paired with the CultureChanged subscription that
        /// raises PropertyChanged below.</summary>
        public string Label => MacroAction.LightbarModeDisplayName(Mode);

        public LightbarModeCycleOption(MacroAction parent, LightbarMode mode)
        {
            _parent = parent;
            Mode = mode;
            // Refresh the label when the user switches UI culture.
            // Strings.CultureChanged uses weak-handler tracking
            // (instance methods are held via WeakReference), so the
            // option doesn't need to unsubscribe explicitly.
            Strings.CultureChanged += OnCultureChanged;
        }

        private void OnCultureChanged() => OnPropertyChanged(nameof(Label));

        public bool IsChecked
        {
            get
            {
                var csv = _parent.LightbarCycleModesCsv;
                if (string.IsNullOrEmpty(csv)) return false;
                int target = (int)Mode;
                foreach (var token in csv.Split(','))
                    if (int.TryParse(token.Trim(), out int v) && v == target) return true;
                return false;
            }
            set
            {
                var csv = _parent.LightbarCycleModesCsv ?? string.Empty;
                var current = new System.Collections.Generic.HashSet<LightbarMode>();
                foreach (var token in csv.Split(','))
                    if (int.TryParse(token.Trim(), out int v) && Enum.IsDefined(typeof(LightbarMode), v))
                        current.Add((LightbarMode)v);
                if (value) current.Add(Mode);
                else current.Remove(Mode);
                _parent.WriteCycleCsv(current);
                OnPropertyChanged();
            }
        }
    }
}
