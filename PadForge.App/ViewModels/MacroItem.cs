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
using PadForge.Services;

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
            OnPropertyChanged(nameof(TriggerPressWindowToolTip));
            OnPropertyChanged(nameof(HoldTimeToolTip));
            OnPropertyChanged(nameof(TriggerInputItems)); // C24: chip labels are localized too
            OnPropertyChanged(nameof(InlineIntervalToolTip));
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
            get => _outputChannelOptions ??= MacroOutputChannelNames.GetOptions(_buttonStyle, _extendedProfileId);
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
        /// to resolve the target <c>DeviceSlotConfig</c> at fire
        /// time. Not serialized — the parent <c>MacroData.PadIndex</c> is
        /// the persisted source of truth and gets reapplied on load.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public int PadIndex { get; set; } = -1;

        /// <summary>Nonzero links the two legs of a materialized hold pair
        /// (audit #2 M4/M6): the press leg SETs the ToggleKey /
        /// ToggleMouseButton latch, the OnRelease twin CLEARs it, and the
        /// shared id is how they reach each other at runtime. A starting
        /// leg cancels its executing twin, so a re-press kills the twin's
        /// pending delayed release before the stale Clear can cut the new
        /// hold short, and an Off fire clears the twin's latches (each
        /// leg's latch state lives on its own action instance). 0 =
        /// unpaired, every macro the editor creates. Persisted via
        /// <c>MacroData.PairId</c>, like <see cref="PadIndex"/>.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public int PairId { get; set; }

        /// <summary>Release linger for UntilRelease macros (translator v22,
        /// Steam's activator <c>delay_end</c> on autofire: "wait for this
        /// period of time after the button has been released before
        /// deactivating"). When nonzero, the trigger's release does not stop
        /// the executing macro immediately; the pulse train keeps running
        /// this many milliseconds past the release, and a re-press inside
        /// the window cancels the pending stop (the M6 cancel-on-re-press
        /// shape applied to the pulse stop leg). 0 = stop at release, every
        /// macro the editor creates. Persisted via
        /// <c>MacroData.ReleaseLingerMs</c>, like <see cref="PairId"/>.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public int ReleaseLingerMs { get; set; }

        /// <summary>Runtime deadline of the pending linger stop (UTC).
        /// MinValue = no pending stop. Owned by the Step4b evaluators.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public System.DateTime ReleaseLingerStartUtc { get; set; } = System.DateTime.MinValue;

        private bool _isEnabled = true;

        /// <summary>Whether this macro is active. Disabling clears every
        /// action's volatile Toggle latch (issue #9 wave 1b): the evaluator
        /// skips disabled macros, so the per-frame latch application stops
        /// immediately, and clearing the bits here keeps a later re-enable
        /// from silently resurrecting a stale latched button or key. The
        /// engine's per-frame key reconcile sends the matching KeyUp on the
        /// next tick once the desired set no longer contains the key.</summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (SetProperty(ref _isEnabled, value) && !value)
                {
                    foreach (var action in Actions)
                    {
                        if (action == null) continue;
                        action.VcToggleLatched = false;
                        action.KeyToggleLatched = false;
                        action.MouseToggleLatched = false;
                        action.VcAxisToggleLatched = false;
                        action.WheelToggleLatched = false;
                    }

                    // End the RUN too, not just the latches. Clearing the five
                    // latch bits stopped the held outputs but left the sequence
                    // mid-flight, so re-enabling resumed at CurrentActionIndex
                    // and injected the remaining actions with no trigger press.
                    // Mirrors the ViewModel-reachable half of the engine's
                    // EndMacroRun (Step4b.EvaluateMacros.cs:1014).
                    IsExecuting = false;
                    CurrentActionIndex = 0;
                    ComboResumeIndex = 0;
                    RunReleasedFireToCompletion = false;
                }
            }
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
                {
                    OnPropertyChanged(nameof(TriggerDisplayText));
                    OnPropertyChanged(nameof(TriggerInputItems));
                    OnPropertyChanged(nameof(HasTriggerInputItems));
                }
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
                OnPropertyChanged(nameof(TriggerInputItems));
                OnPropertyChanged(nameof(HasTriggerInputItems));
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
                            else if (!string.IsNullOrEmpty(entry.GestureDescriptor))
                            {
                                inputs.Add(MappingDisplayResolver.ResolveDescriptorText(
                                    entry.GestureDescriptor, null, padPrefixAlways: grp.Key == Guid.Empty) ?? entry.GestureDescriptor);
                            }
                            else if (!string.IsNullOrEmpty(entry.SourceDescriptor))
                            {
                                inputs.Add(MappingDisplayResolver.ResolveDescriptorText(
                                    entry.SourceDescriptor, null, padPrefixAlways: grp.Key == Guid.Empty) ?? entry.SourceDescriptor);
                            }
                        }
                        // Guid.Empty is a real group here (#9 B-9): the
                        // device-free entries render under the same
                        // "(Any device)" sentinel the mapping picker uses.
                        string deviceName = grp.Key == Guid.Empty
                            ? Strings.Instance.Mapping_AnyDevice
                            : ResolveDeviceName(grp.Key);
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
                        else if (!string.IsNullOrEmpty(entry.GestureDescriptor))
                        {
                            parts.Add(MappingDisplayResolver.ResolveDescriptorText(
                                entry.GestureDescriptor, null, padPrefixAlways: grp.Key == Guid.Empty) ?? entry.GestureDescriptor);
                        }
                        else if (!string.IsNullOrEmpty(entry.SourceDescriptor))
                        {
                            parts.Add(MappingDisplayResolver.ResolveDescriptorText(
                                entry.SourceDescriptor, null, padPrefixAlways: grp.Key == Guid.Empty) ?? entry.SourceDescriptor);
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
                        parts.Add(MacroButtonNames.FormatCustomButtons(_triggerCustomButtonWords, _extendedProfileId));
                    }
                    else if (_triggerButtons != 0)
                    {
                        parts.Add(MacroButtonNames.FormatButtons(_triggerButtons, _buttonStyle, _extendedProfileId));
                    }

                    // POV part(s).
                    foreach (var pov in _triggerPovs)
                        parts.Add(FormatPovTrigger(pov));
                }

                // Legacy slot-combined button parts render even when the
                // entry list is populated: the OutputController finalize
                // keeps dropdown-picked gesture entries alongside a
                // recorded bitmask / custom-word trigger, and the
                // evaluator ANDs both, so hiding either half would
                // under-report what the macro requires. Legacy POVs stay
                // inside the entries-empty branch because the
                // single-device back-compat mirror duplicates entry POVs
                // into TriggerPovs.
                if (entries.Count > 0)
                {
                    if (_buttonStyle == MacroButtonStyle.Numbered && UsesCustomTrigger)
                        parts.Add(MacroButtonNames.FormatCustomButtons(_triggerCustomButtonWords, _extendedProfileId));
                    else if (_triggerButtons != 0)
                        parts.Add(MacroButtonNames.FormatButtons(_triggerButtons, _buttonStyle, _extendedProfileId));
                }

                // Axis part(s) — always Xbox-output, no per-device split.
                foreach (var axis in _triggerAxisTargets)
                    parts.Add($"{axis.DisplayName()} > {_triggerAxisThreshold}%");

                if (parts.Count == 0) return Strings.Instance.Macro_NotSet;

                string result = string.Join(" + ", parts);

                // Append source device name at end ONLY for single-device legacy /
                // single-device new-list cases. Multi-device already shows names
                // inline.
                if (!multiDevice && (UsesRawTrigger || UsesPovTrigger || UsesAxisTrigger
                    || UsesGestureTrigger || UsesDescriptorTrigger))
                {
                    Guid deviceGuid = entries.Count > 0 ? entries[0].DeviceGuid : _triggerDeviceGuid;
                    if (deviceGuid == Guid.Empty && entries.Count > 0)
                    {
                        // Device-free entries (#9 B-9). The sentinel already
                        // carries its own parentheses, so it appends bare.
                        result = $"{result} {Strings.Instance.Mapping_AnyDevice}";
                    }
                    else
                    {
                        string deviceName = ResolveDeviceName(deviceGuid);
                        if (!string.IsNullOrEmpty(deviceName))
                            result = $"{result} ({deviceName})";
                    }
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

        /// <summary>True if this macro uses POV hat triggers, either via the
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

        /// <summary>True if this macro uses touchpad-gesture trigger
        /// entries (#177). Gesture entries only exist in the
        /// multi-device <see cref="TriggerInputs"/> spec; there is no
        /// legacy form.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool UsesGestureTrigger
        {
            get
            {
                var entries = GetTriggerInputEntries();
                for (int i = 0; i < entries.Count; i++)
                    if (!string.IsNullOrEmpty(entries[i].GestureDescriptor)) return true;
                return false;
            }
        }

        /// <summary>True if this macro uses descriptor trigger entries
        /// (#9 B-9): engine-read descriptors ("Gyro Pitch",
        /// "Touchpad 0 Finger 0 Down") evaluated through SourceCoercion's
        /// button read. Entry-list only, like the gesture trigger.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool UsesDescriptorTrigger
        {
            get
            {
                var entries = GetTriggerInputEntries();
                for (int i = 0; i < entries.Count; i++)
                    if (!string.IsNullOrEmpty(entries[i].SourceDescriptor)) return true;
                return false;
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
                set { if (SetProperty(ref _deviceGuid, value)) _deviceGuidStr = null; }
            }

            private string _deviceGuidStr;
            /// <summary>Cached <c>DeviceGuid.ToString()</c>. The macro
            /// evaluator runs per polling tick on the 1 kHz thread, and
            /// the gesture provider is keyed by guid STRING; caching
            /// keeps the gesture checker allocation-free like its button
            /// and POV siblings.</summary>
            [System.Xml.Serialization.XmlIgnore]
            public string DeviceGuidString => _deviceGuidStr ??= _deviceGuid.ToString();

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

            /// <summary>Touchpad gesture descriptor ("Touchpad 0 TouchLeft",
            /// "Touchpad 0 SwipeUp", "Touchpad 0 Custom_MyShape"), or null
            /// if this entry isn't a gesture. Evaluated per frame through
            /// the same SourceCoercion.TouchpadGestureFiredProvider the
            /// mapping grid uses, so the enable gates on the Touchpad tab
            /// govern macros and mappings identically (#177). Picked from
            /// the trigger dropdown, never recorded. Recording a gesture
            /// by accident is the exact complaint the issue raises.</summary>
            private string _gestureDescriptor;
            public string GestureDescriptor
            {
                get => _gestureDescriptor;
                set { if (SetProperty(ref _gestureDescriptor, value)) _gestureParsed = false; }
            }

            private bool _gestureParsed;
            private int _gesturePadIdx = -1;
            private string _gestureName;

            /// <summary>Parses "Touchpad {padIdx} {gestureName}" out of
            /// <see cref="GestureDescriptor"/> once and caches the parts,
            /// so the per-tick trigger evaluation doesn't re-Split on the
            /// polling thread. Returns false for a null / malformed
            /// descriptor.</summary>
            public bool TryGetGestureParts(out int padIdx, out string gestureName)
            {
                if (!_gestureParsed)
                {
                    _gestureParsed = true;
                    _gesturePadIdx = -1;
                    _gestureName = null;
                    var d = _gestureDescriptor;
                    if (!string.IsNullOrEmpty(d))
                    {
                        var parts = d.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3 && int.TryParse(parts[1], out int p) && p >= 0)
                        {
                            _gesturePadIdx = p;
                            _gestureName = parts[2];
                        }
                    }
                }
                padIdx = _gesturePadIdx;
                gestureName = _gestureName;
                return _gestureName != null;
            }

            /// <summary>Engine input descriptor evaluated through
            /// SourceCoercion's button read ("Gyro Pitch",
            /// "Touchpad 0 Finger 0 Down"), or null if this entry isn't a
            /// descriptor entry (#9 B-9). Carries the trigger shapes that
            /// have no raw-entry form: the readers canonicalize abstract
            /// "Gamepad ..." spellings and evaluate the gyro / touchpad
            /// families with the same per-(device, slot) tuning a mapping
            /// row gets. Picked from the trigger dropdown, never recorded.</summary>
            private string _sourceDescriptor;
            public string SourceDescriptor
            {
                get => _sourceDescriptor;
                set { if (SetProperty(ref _sourceDescriptor, value)) _descriptorSource = null; }
            }

            private PadForge.Engine.Data.MappingSource _descriptorSource;

            /// <summary>Deadzone / threshold percent stamped onto the cached
            /// <see cref="DescriptorSource"/> (v15). 0 (default) leaves the
            /// source's DeadZone unset so axis-class descriptors read the
            /// evaluator's default threshold and gyro keeps its engine-default
            /// 30°/s rate. Distinct from <see cref="DeadZone"/>, whose 1..100
            /// clamp cannot express "engine default" and whose 50 default
            /// would silently retune every existing descriptor entry.</summary>
            private int _descriptorDeadZone;
            public int DescriptorDeadZone
            {
                get => _descriptorDeadZone;
                set { if (SetProperty(ref _descriptorDeadZone, Math.Clamp(value, 0, 100))) _descriptorSource = null; }
            }

            /// <summary>Cached <see cref="PadForge.Engine.Data.MappingSource"/>
            /// wrapper for <see cref="SourceDescriptor"/>, so the 1 kHz trigger
            /// evaluation never allocates (mirrors <see cref="TryGetGestureParts"/>).
            /// DeadZone is left at 0 (unset) unless a
            /// <see cref="DescriptorDeadZone"/> was stamped, so axis-class
            /// descriptors read the evaluator's default threshold and gyro
            /// keeps its engine default. The entry's HalfAxis / Invert /
            /// Bidirectional flags ride onto the source (v15): all default
            /// false, so plain entries evaluate exactly as before, and a
            /// stamped gyro entry reads ONE signed rotation direction through
            /// the engine's half-aware gyro bool read. Null when this isn't a
            /// descriptor entry.</summary>
            [System.Xml.Serialization.XmlIgnore]
            public PadForge.Engine.Data.MappingSource DescriptorSource
                => string.IsNullOrEmpty(_sourceDescriptor)
                    ? null
                    : _descriptorSource ??= new PadForge.Engine.Data.MappingSource
                    {
                        Descriptor = _sourceDescriptor,
                        DeadZone = _descriptorDeadZone,
                        HalfAxis = _halfAxis,
                        Invert = _invert,
                        Bidirectional = _bidirectional,
                    };

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
                set
                {
                    if (SetProperty(ref _halfAxis, value))
                    {
                        _descriptorSource = null;
                        OnPropertyChanged(nameof(IsInvertApplicable));
                    }
                }
            }

            /// <summary>Whether Invert does anything in the current
            /// combination (round nine, R9): with Half + Either both on
            /// the evaluator mirrors around center and Invert is inert,
            /// exactly as in the two mapping editors. This third editor
            /// was left ungated when those two were fixed, so the commit
            /// that claimed "both mapping editors" was one short.</summary>
            [System.Xml.Serialization.XmlIgnore]
            public bool IsInvertApplicable => !(_halfAxis && _bidirectional);

            /// <summary>When true the axis reading is flipped (val → 1−val)
            /// before the deadzone test. Same semantics as
            /// <c>MappingSource.Invert</c>.</summary>
            private bool _invert;
            public bool Invert
            {
                get => _invert;
                set { if (SetProperty(ref _invert, value)) _descriptorSource = null; }
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
                set
                {
                    if (SetProperty(ref _bidirectional, value))
                    {
                        _descriptorSource = null;
                        OnPropertyChanged(nameof(IsInvertApplicable));
                    }
                }
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
            /// Bidirectional field is optional. The parser defaults it to 0
            /// when reading older XML written before the flag existed.
            /// Descriptor entries write <c>sd:{descriptor}</c> when
            /// unstamped and the v15 <c>sdh:h:i:b:dz:{descriptor}</c> form
            /// when a half selector or explicit threshold rides along.
            /// The GUID may be all-zero (#9 B-9): that is the persisted
            /// "the device on the macro's slot" form, so an empty guid no
            /// longer voids the spec. A payload-less entry still yields
            /// "" and gets filtered by the TriggerInputs join.</summary>
            public string Spec
            {
                get
                {
                    if (AxisTarget != MacroAxisTarget.None)
                        return $"in:{DeviceGuid}:ax:{AxisTarget}:{(HalfAxis ? 1 : 0)}:{(Invert ? 1 : 0)}:{DeadZone}:{(Bidirectional ? 1 : 0)}";
                    if (!string.IsNullOrEmpty(Pov)) return $"in:{DeviceGuid}:pov:{Pov}";
                    if (RawButton >= 0) return $"in:{DeviceGuid}:btn:{RawButton}";
                    // Gesture descriptors ride as the spec tail so colons in
                    // custom-gesture names survive (the parser re-joins the
                    // tail). '|' would split the pipe-joined TriggerInputs
                    // element, so it's escaped as "&P" (collision-free:
                    // '&' is rejected by the gesture-name validator).
                    if (!string.IsNullOrEmpty(GestureDescriptor))
                        return $"in:{DeviceGuid}:tg:{GestureDescriptor.Replace("|", "&P")}";
                    // Descriptor entries (#9 B-9) ride the same
                    // tail-escaping shape as gestures. A stamped entry
                    // (v15: half selector and/or explicit threshold) takes
                    // the extended "sdh" tag with the flags BEFORE the
                    // descriptor tail so the tail re-join keeps working;
                    // plain entries keep the byte-identical "sd" form.
                    if (!string.IsNullOrEmpty(SourceDescriptor))
                    {
                        // v19 (G3): the extended form writes whenever ANY
                        // stamp is non-default. The old HalfAxis-or-deadzone
                        // gate dropped an Invert-only or Bidirectional-only
                        // stamp back to the plain "sd" spelling, silently
                        // shedding the flag on round-trip.
                        if (HalfAxis || Invert || Bidirectional || DescriptorDeadZone > 0)
                            return $"in:{DeviceGuid}:sdh:{(HalfAxis ? 1 : 0)}:{(Invert ? 1 : 0)}:{(Bidirectional ? 1 : 0)}:{DescriptorDeadZone}:{SourceDescriptor.Replace("|", "&P")}";
                        return $"in:{DeviceGuid}:sd:{SourceDescriptor.Replace("|", "&P")}";
                    }
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
                    case "tg":
                        // Re-join the tail: gesture descriptors (and the
                        // custom-gesture names inside them) may contain
                        // ':'. Unescape the pipe token Spec wrote.
                        string desc = string.Join(":", parts, 3, parts.Length - 3)
                            .Replace("&P", "|");
                        if (string.IsNullOrWhiteSpace(desc)
                            || !desc.StartsWith("Touchpad ", StringComparison.Ordinal))
                            return null;
                        entry.GestureDescriptor = desc;
                        return entry;
                    case "sd":
                        // Descriptor entry (#9 B-9): same tail re-join and
                        // unescape as "tg". Accepted as-is (no family gate)
                        // so the spec stays forward-compatible; the engine
                        // read simply evaluates false for descriptors it
                        // doesn't recognize.
                        string sd = string.Join(":", parts, 3, parts.Length - 3)
                            .Replace("&P", "|");
                        if (string.IsNullOrWhiteSpace(sd)) return null;
                        entry.SourceDescriptor = sd;
                        return entry;
                    case "sdh":
                        // Stamped descriptor entry (v15): HalfAxis, Invert,
                        // Bidirectional, and the descriptor threshold ride
                        // in front of the descriptor tail
                        // (in:GUID:sdh:h:i:b:dz:{descriptor}).
                        if (parts.Length < 8) return null;
                        string sdh = string.Join(":", parts, 7, parts.Length - 7)
                            .Replace("&P", "|");
                        if (string.IsNullOrWhiteSpace(sdh)) return null;
                        entry.SourceDescriptor = sdh;
                        entry.HalfAxis = parts[3] == "1";
                        entry.Invert = parts[4] == "1";
                        entry.Bidirectional = parts[5] == "1";
                        if (int.TryParse(parts[6], out int sdz))
                            entry.DescriptorDeadZone = sdz;
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

        /// <summary>Converts a picker <see cref="InputChoice"/> into a
        /// <see cref="TriggerInputEntry"/> for the macro trigger dropdown
        /// (#177). Returns false for descriptors the trigger engine has
        /// no entry shape for (finger position axes, sliders, continuous
        /// gesture axes). Buttons, POV directions, gamepad-layout axes 0-5,
        /// the touchpad click (raw button 16), bool-valued touchpad
        /// gestures, gyro axes, and "Finger M Down" all convert.
        /// An empty <see cref="InputChoice.DeviceGuid"/> converts too
        /// (#9 B-9): it is the picker's "(Any device)" group and stores
        /// <see cref="Guid.Empty"/>, the persisted "the device on the
        /// macro's slot" form the evaluator resolves per slot device.</summary>
        public static bool TryBuildTriggerEntry(InputChoice choice, out TriggerInputEntry entry)
        {
            entry = null;
            string d = choice?.Descriptor;
            if (string.IsNullOrEmpty(d)) return false;
            Guid g = Guid.Empty;
            if (!string.IsNullOrEmpty(choice.DeviceGuid)
                && !Guid.TryParse(choice.DeviceGuid, out g))
                return false;

            // Abstract Gamepad aliases (#9) fold to their canonical
            // "Button N" / "POV 0 Dir" / "Axis N" form so the family's
            // picker entries convert the same as the raw ones.
            d = PadForge.Engine.Common.Mapping.SourceCoercion.ResolveGamepadAlias(d) ?? d;

            if (d.StartsWith("Button ", StringComparison.Ordinal)
                && int.TryParse(d.Substring(7), out int btn) && btn >= 0)
            {
                entry = new TriggerInputEntry { DeviceGuid = g, RawButton = btn };
                return true;
            }

            if (d.StartsWith("POV ", StringComparison.Ordinal))
            {
                var pp = d.Split(' ');
                if (pp.Length == 3 && int.TryParse(pp[1], out int povIdx))
                {
                    int cd = pp[2] switch
                    {
                        "Up" => 0, "Right" => 9000, "Down" => 18000, "Left" => 27000,
                        _ => -1
                    };
                    if (cd >= 0)
                    {
                        entry = new TriggerInputEntry { DeviceGuid = g, Pov = $"{povIdx}:{cd}" };
                        return true;
                    }
                }
                return false;
            }

            if (d.StartsWith("Axis ", StringComparison.Ordinal)
                && int.TryParse(d.Substring(5), out int ax) && ax >= 0 && ax <= 5)
            {
                entry = new TriggerInputEntry
                {
                    DeviceGuid = g,
                    AxisTarget = ax switch
                    {
                        0 => MacroAxisTarget.LeftStickX,
                        1 => MacroAxisTarget.LeftStickY,
                        2 => MacroAxisTarget.LeftTrigger,
                        3 => MacroAxisTarget.RightStickX,
                        4 => MacroAxisTarget.RightStickY,
                        _ => MacroAxisTarget.RightTrigger,
                    },
                };
                return true;
            }

            if (d.StartsWith("Touchpad ", StringComparison.Ordinal))
            {
                var tp = d.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                if (tp.Length < 3 || !int.TryParse(tp[1], out int tpPad)) return false;
                if (tp[2].Equals("Click", StringComparison.OrdinalIgnoreCase))
                {
                    // Canonical touchpad click rides Buttons[16]
                    // (SDL_GAMEPAD_BUTTON_TOUCHPAD), same slot the
                    // mapping recorder resolves it to. Pads past the
                    // first have NO Buttons[16] backing (a multi-pad
                    // device's second click surfaces as its own gamepad
                    // button), so mapping them to 16 would fire on the
                    // WRONG pad; they ride a descriptor entry instead,
                    // which evaluates through the same touchpad bool
                    // read a mapping row gets (quiet today, live when
                    // the multi-touchpad click extension lands there).
                    if (tpPad == 0)
                    {
                        entry = new TriggerInputEntry { DeviceGuid = g, RawButton = 16 };
                        return true;
                    }
                    entry = new TriggerInputEntry { DeviceGuid = g, SourceDescriptor = d };
                    return true;
                }
                if (tp[2].StartsWith("Click ", StringComparison.OrdinalIgnoreCase))
                {
                    // Windowed clicks (v19, M8): "Click {window}" is the
                    // pad's click AND finger 0 inside the window, a bool
                    // SourceCoercion.ReadTouchpadBool answers for every
                    // pad index (pad 0 reads Buttons[16] inside it), so
                    // the whole family rides a descriptor entry. The old
                    // Equals("Click") test let these fall through to the
                    // gesture catch-all, building a trigger the gesture
                    // parser rejects: dead forever. Unknown window tokens
                    // stay unconvertible.
                    var ct = tp[2].Split(' ');
                    if (ct.Length == 2 && IsTouchpadWindowToken(ct[1]))
                    {
                        entry = new TriggerInputEntry { DeviceGuid = g, SourceDescriptor = d };
                        return true;
                    }
                    return false;
                }
                if (tp[2].StartsWith("Finger", StringComparison.OrdinalIgnoreCase))
                {
                    // "Finger M Down" is a bool the engine's touchpad read
                    // already answers, so it rides a descriptor entry
                    // (#9 B-9). The windowed forms are the same contact
                    // bool gated to one region of the pad and convert the
                    // same way; the window grammar mirrors
                    // SourceCoercion.ReadTouchpadBool (v19, M9): one
                    // window token (halves #9 B-1, the v18 vertical
                    // halves, or a diamond quadrant), or the v18 7-token
                    // quadrant-in-half compose (quadrant first, then
                    // Left / Right). Finger position axes (X / Y, whole
                    // or half-windowed) stay unconvertible: no bool read
                    // exists for them. PRESSURE converts since #239: the
                    // bool branch reads it against the per-source
                    // threshold, whole-pad or zone-windowed (Center
                    // included).
                    var ft = tp[2].Split(' ');
                    if (ft.Length is 3 or 4
                        && ft[0].Equals("Finger", StringComparison.OrdinalIgnoreCase)
                        && ft[2].Equals("Pressure", StringComparison.Ordinal)
                        && (ft.Length == 3 || IsTouchpadWindowToken(ft[3])
                            || ft[3].Equals("Center", StringComparison.Ordinal)))
                    {
                        entry = new TriggerInputEntry { DeviceGuid = g, SourceDescriptor = d };
                        return true;
                    }
                    // Finger ring (v26): a bool the engine's ring read
                    // answers (radius on DeadZone, Invert = inner), whole
                    // pad or half-windowed, so it rides a descriptor entry
                    // like the Down forms below.
                    if (ft.Length is 3 or 4
                        && ft[0].Equals("Finger", StringComparison.OrdinalIgnoreCase)
                        && ft[2].Equals("Ring", StringComparison.Ordinal)
                        && (ft.Length == 3 || IsTouchpadWindowToken(ft[3])))
                    {
                        entry = new TriggerInputEntry { DeviceGuid = g, SourceDescriptor = d };
                        return true;
                    }
                    if (ft.Length >= 3 && ft.Length <= 5
                        && ft[0].Equals("Finger", StringComparison.OrdinalIgnoreCase)
                        && ft[2].Equals("Down", StringComparison.Ordinal))
                    {
                        bool windowOk = ft.Length == 3
                            || (ft.Length == 4 && IsTouchpadWindowToken(ft[3]))
                            || (ft.Length == 5 && IsTouchpadQuadrantToken(ft[3])
                                && (ft[4] == "Left" || ft[4] == "Right"));
                        if (windowOk)
                        {
                            entry = new TriggerInputEntry { DeviceGuid = g, SourceDescriptor = d };
                            return true;
                        }
                    }
                    return false;
                }
                // Absolute pointer axes (#9 B-15): positional analogs like
                // the finger X/Y reads above, and NOT gesture names, so the
                // gesture-fire catch-all below must not claim them (it
                // would build a trigger the gesture parser rejects, dead
                // forever). They stay mapping-only.
                if (tp[2].StartsWith("Pointer", StringComparison.Ordinal)) return false;
                // Continuous gesture axes have no bool entry in the
                // fired set; they stay recording/mapping-only.
                if (tp[2] is "PinchAxis" or "RotateAxis" or "StickX" or "StickY") return false;
                entry = new TriggerInputEntry { DeviceGuid = g, GestureDescriptor = d };
                return true;
            }

            // Bare gyro axes (#9 B-9): evaluated through SourceCoercion's
            // button read (rate past the engine's default threshold), with
            // the same per-(device, slot) Gyro-tab tuning a mapping row
            // gets. Covers workshop configs whose bindings live on a gyro
            // group.
            if (d.StartsWith("Gyro ", StringComparison.Ordinal))
            {
                entry = new TriggerInputEntry { DeviceGuid = g, SourceDescriptor = d };
                return true;
            }

            // Stick deflection rings (translator v17): a bool the engine's
            // ring read answers (magnitude vs the DeadZone radius, Invert =
            // inner), so the family rides a descriptor entry exactly like
            // the gyro axes. The check must come after the alias fold: the
            // ring spelling is in the "Gamepad " namespace but is not an
            // alias-table member, so the fold leaves it intact.
            if (PadForge.Engine.Common.Mapping.SourceCoercion.IsStickRingDescriptor(d))
            {
                entry = new TriggerInputEntry { DeviceGuid = g, SourceDescriptor = d };
                return true;
            }

            // Capsense touch channels (translator v26): plain hardware
            // bools the engine's capsense read answers, same non-alias
            // "Gamepad " namespace rule as the rings.
            if (PadForge.Engine.Common.Mapping.SourceCoercion.IsCapSenseDescriptor(d))
            {
                entry = new TriggerInputEntry { DeviceGuid = g, SourceDescriptor = d };
                return true;
            }

            // NFC tags (#241): "Any NFC Tag" / "NFC Tag N" are plain bools the
            // engine's NFC read answers, so a tap-to-macro trigger rides a
            // descriptor entry exactly like the capsense family. Without this
            // branch the tag showed in the mapping picker but not the macro
            // "add trigger from list" list (Codex #2).
            if (PadForge.Engine.Common.Mapping.SourceCoercion.IsNfcTagDescriptor(d))
            {
                entry = new TriggerInputEntry { DeviceGuid = g, SourceDescriptor = d };
                return true;
            }

            // Mouse gestures (issue #200): every family member is a one-shot
            // bool in the recognizer's fired set, so the whole family rides
            // GestureDescriptor. Evaluated by CheckGestureTrigger's mouse
            // branch through SourceCoercion.MouseGestureFiredProvider.
            if (d.StartsWith("Mouse Gesture ", StringComparison.Ordinal))
            {
                entry = new TriggerInputEntry { DeviceGuid = g, GestureDescriptor = d };
                return true;
            }

            // Menu items (#9 B-17): "Menu {id} Item {k}" is a bool the
            // engine's coercion read answers through the menu runtime's
            // fired set (asserted / commit-pulsed), so it rides a
            // descriptor entry exactly like the gyro family. Carries
            // imported Workshop menu cells whose bindings are macro-shaped
            // (cursor warps, latches).
            if (PadForge.Engine.Common.Mapping.SourceCoercion.IsMenuItemDescriptor(d))
            {
                entry = new TriggerInputEntry { DeviceGuid = g, SourceDescriptor = d };
                return true;
            }

            return false;
        }

        /// <summary>The touchpad window-token vocabulary (v19, M8/M9),
        /// mirroring <c>SourceCoercion.ParseTouchpadHalf</c>: horizontal
        /// halves, the v18 vertical halves, and the v18 diamond
        /// quadrants. Ordinal on purpose: the engine's parser is exact,
        /// so a case-mangled token would build a dead trigger.</summary>
        private static bool IsTouchpadWindowToken(string t)
            => t is "Left" or "Right" or "Upper" or "Lower"
                or "North" or "South" or "East" or "West";

        /// <summary>The quadrant subset of the window vocabulary,
        /// mirroring <c>SourceCoercion.ComposeTouchpadWindow</c>: only a
        /// quadrant composes with a horizontal half in the 7-token
        /// form.</summary>
        private static bool IsTouchpadQuadrantToken(string t)
            => t is "North" or "South" or "East" or "West";

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
                // Leave the field NULL when there is nothing to parse, which
                // is the normal shape for a legacy macro on the XML load path.
                // Allocating an empty list here made
                // EnsureTriggerInputEntries' `if (_triggerInputEntries != null)
                // return;` guard see a populated field and skip the legacy
                // migration permanently, so a macro whose trigger lived in the
                // old TriggerDeviceGuid + TriggerRawButtons / TriggerPovs
                // fields still FIRED (the engine reads those) while the editor
                // showed "Not set".
                if (string.IsNullOrEmpty(value))
                {
                    _triggerInputEntries = null;
                    OnPropertyChanged(nameof(TriggerInputs));
                    OnPropertyChanged(nameof(UsesRawTrigger));
                    OnPropertyChanged(nameof(UsesPovTrigger));
                    OnPropertyChanged(nameof(UsesGestureTrigger));
                    OnPropertyChanged(nameof(UsesDescriptorTrigger));
                    OnPropertyChanged(nameof(TriggerDisplayText));
                    return;
                }

                _triggerInputEntries = new List<TriggerInputEntry>();
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
                OnPropertyChanged(nameof(UsesGestureTrigger));
                OnPropertyChanged(nameof(UsesDescriptorTrigger));
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
            ClearArmedTriggerWindows();

            _triggerInputEntries = entries ?? new List<TriggerInputEntry>();
            WireTriggerInputEntries();
            OnPropertyChanged(nameof(TriggerInputs));
            OnPropertyChanged(nameof(UsesRawTrigger));
            OnPropertyChanged(nameof(UsesPovTrigger));
            OnPropertyChanged(nameof(UsesAxisTrigger));
            OnPropertyChanged(nameof(UsesGestureTrigger));
            OnPropertyChanged(nameof(UsesDescriptorTrigger));
            OnPropertyChanged(nameof(TriggerDisplayText));
            OnPropertyChanged(nameof(TriggerAxisEntries));
            OnPropertyChanged(nameof(HasTriggerAxisEntries));
            OnPropertyChanged(nameof(TriggerInputItems));
            OnPropertyChanged(nameof(HasTriggerInputItems));
        }

        /// <summary>Every individual input that makes up the trigger, one
        /// removable row: the modern per-device entries plus any legacy
        /// slot-combined bitmask buttons and axis targets, mirroring what
        /// <see cref="TriggerDisplayText"/> renders. UI-only (the evaluator
        /// reads the underlying fields directly and never touches this), and
        /// regenerated on each bind like <see cref="TriggerAxisEntries"/>.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public IEnumerable<MacroTriggerInputItem> TriggerInputItems
        {
            get
            {
                foreach (var entry in GetTriggerInputEntries())
                {
                    var captured = entry;
                    yield return new MacroTriggerInputItem(
                        FormatTriggerEntryLabel(captured),
                        new RelayCommand(() => RemoveTriggerEntry(captured)),
                        captured.AxisTarget != MacroAxisTarget.None ? captured : null);
                }

                // Legacy slot-combined buttons (OutputController source). One row
                // per set bit so each is removable on its own. Custom (Extended)
                // words and the Xbox/DS4 bitmask are mutually exclusive by style,
                // matching the TriggerDisplayText branch.
                if (_buttonStyle == MacroButtonStyle.Numbered && UsesCustomTrigger)
                {
                    for (int i = 0; i < 128; i++)
                    {
                        int word = i / 32, bit = i % 32;
                        if (word < _triggerCustomButtonWords.Length
                            && (_triggerCustomButtonWords[word] & (uint)(1 << bit)) != 0)
                        {
                            int idx = i;
                            yield return new MacroTriggerInputItem(
                                MacroButtonNames.RawButtonShortLabel(_extendedProfileId, idx + 1),
                                new RelayCommand(() => RemoveLegacyCustomButton(idx)));
                        }
                    }
                }
                else if (_triggerButtons != 0)
                {
                    foreach (var def in MacroButtonNames.GetButtonDefs(_buttonStyle, _extendedProfileId))
                    {
                        if ((_triggerButtons & def.Flag) != 0)
                        {
                            ushort flag = def.Flag;
                            yield return new MacroTriggerInputItem(
                                def.Label,
                                new RelayCommand(() => RemoveLegacyTriggerButton(flag)));
                        }
                    }
                }

                // Legacy slot-combined axis targets (all share one threshold).
                for (int i = 0; i < _triggerAxisTargets.Length; i++)
                {
                    int idx = i;
                    yield return new MacroTriggerInputItem(
                        $"{_triggerAxisTargets[idx].DisplayName()} > {_triggerAxisThreshold}%",
                        new RelayCommand(() => RemoveLegacyAxisTarget(idx)));
                }
            }
        }

        /// <summary>True when the trigger has at least one input to list. Cheap
        /// (no item/command allocation) so the empty-state placeholder can bind
        /// to it without walking <see cref="TriggerInputItems"/>.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool HasTriggerInputItems =>
            GetTriggerInputEntries().Count > 0
            || _triggerButtons != 0
            || (_triggerCustomButtonWords != null && _triggerCustomButtonWords.Any(w => w != 0))
            || _triggerAxisTargets.Length > 0;

        /// <summary>Display label for one trigger input entry: the input's own
        /// name (button / POV / axis / gesture) prefixed with the device name
        /// when it resolves. Mirrors the per-entry formatting in
        /// <see cref="TriggerDisplayText"/>.</summary>
        private string FormatTriggerEntryLabel(TriggerInputEntry entry)
        {
            var objects = ResolveDeviceObjects(entry.DeviceGuid);
            string input;
            if (entry.RawButton >= 0)
            {
                var obj = objects?.FirstOrDefault(o => o.IsButton && o.InputIndex == entry.RawButton);
                input = obj != null && !string.IsNullOrEmpty(obj.Name)
                    ? obj.Name
                    : string.Format(Strings.Instance.Macro_Button_Format, entry.RawButton);
            }
            else if (!string.IsNullOrEmpty(entry.Pov))
            {
                input = FormatPovTrigger(entry.Pov);
            }
            else if (entry.AxisTarget != MacroAxisTarget.None)
            {
                // Just the axis name. The deadzone / invert / half read off the
                // inline controls on this same row, so repeating them here would
                // only clutter the label.
                input = entry.AxisTarget.DisplayName();
            }
            else if (!string.IsNullOrEmpty(entry.GestureDescriptor))
            {
                input = MappingDisplayResolver.ResolveDescriptorText(
                    entry.GestureDescriptor, null, padPrefixAlways: entry.DeviceGuid == Guid.Empty) ?? entry.GestureDescriptor;
            }
            else if (!string.IsNullOrEmpty(entry.SourceDescriptor))
            {
                input = MappingDisplayResolver.ResolveDescriptorText(
                    entry.SourceDescriptor, null, padPrefixAlways: entry.DeviceGuid == Guid.Empty) ?? entry.SourceDescriptor;
            }
            else
            {
                input = "";
            }
            // Device-free entries (#9 B-9) carry the same "(Any device)"
            // sentinel the mapping picker's leading group uses, so the
            // chip round-trips readably when the editor reopens.
            string deviceName = entry.DeviceGuid == Guid.Empty
                ? Strings.Instance.Mapping_AnyDevice
                : ResolveDeviceName(entry.DeviceGuid);
            return !string.IsNullOrEmpty(deviceName) ? $"{deviceName}: {input}" : input;
        }

        /// <summary>Stops an active trigger recording so a removal isn't
        /// immediately rewritten by the recorder on the next polling tick
        /// (the same guard <see cref="ClearTriggerCommand"/> uses).</summary>
        private void StopRecordingBeforeTriggerEdit()
        {
            if (IsRecordingTrigger)
            {
                IsRecordingTrigger = false;
                RecordTriggerRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Removes a single per-device entry from the trigger combo,
        /// symmetric to the add-from-list append path.</summary>
        private void RemoveTriggerEntry(TriggerInputEntry entry)
        {
            StopRecordingBeforeTriggerEdit();
            var list = new List<TriggerInputEntry>(GetTriggerInputEntries());
            list.Remove(entry);
            SetTriggerInputEntries(list);   // raises TriggerDisplayText / TriggerInputItems / ...
        }

        /// <summary>Drops every armed trigger window. Editing the trigger combo
        /// mid-hold invalidates all of them: the OLD combo's state must not be
        /// credited to the new one (audit 2026-07-25 round four, R15), and the
        /// previous sample belonged to the old trigger, so swapping to one
        /// already held would otherwise read as a fresh observed edge (round
        /// five, X15). Mirrors the TriggerMode setter's clears.
        ///
        /// <para>Extracted because the three legacy removal paths below change
        /// the trigger combo exactly as SetTriggerInputEntries does, and none of
        /// them cleared any of this. Removing a legacy trigger button mid-hold
        /// left the armed window credited to the smaller combo.</para></summary>
        private void ClearArmedTriggerWindows()
        {
            TriggerHoldStartUtc = DateTime.MinValue;
            TriggerHoldFired = false;
            TriggerPressStreak = 0;
            TriggerLastPressUtc = DateTime.MinValue;
            LastEvaluatedUtc = DateTime.MinValue;
            WasTriggerActive = false;
        }

        private void RemoveLegacyTriggerButton(ushort flag)
        {
            StopRecordingBeforeTriggerEdit();
            ClearArmedTriggerWindows();
            TriggerButtons = (ushort)(_triggerButtons & ~flag);
        }

        private void RemoveLegacyCustomButton(int index)
        {
            StopRecordingBeforeTriggerEdit();
            ClearArmedTriggerWindows();
            var words = (uint[])_triggerCustomButtonWords.Clone();
            int word = index / 32, bit = index % 32;
            if (word < words.Length) words[word] &= ~(uint)(1 << bit);
            TriggerCustomButtonWords = words;
        }

        private void RemoveLegacyAxisTarget(int index)
        {
            if (index < 0 || index >= _triggerAxisTargets.Length) return;
            StopRecordingBeforeTriggerEdit();
            ClearArmedTriggerWindows();
            var targets = _triggerAxisTargets.ToList();
            targets.RemoveAt(index);
            var dirs = _triggerAxisDirections.ToList();
            if (index < dirs.Count) dirs.RemoveAt(index);
            TriggerAxisTargets = targets.ToArray();
            TriggerAxisDirections = dirs.ToArray();
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

        private string _extendedProfileId;

        /// <summary>
        /// The Extended slot's HIDMaestro profile slug, stamped by
        /// PadViewModel beside ButtonStyle (#215). Null on non-Extended
        /// slots. Re-letters the Numbered labels on Switch Pro profiles;
        /// the mask / index value spaces are untouched.
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public string RawProfileId
        {
            get => _extendedProfileId;
            set
            {
                if (SetProperty(ref _extendedProfileId, value))
                {
                    OnPropertyChanged(nameof(TriggerDisplayText));
                    _outputChannelOptions = null;
                    OnPropertyChanged(nameof(OutputChannelOptions));
                    foreach (var action in Actions)
                        action.RawProfileId = value;
                }
            }
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

        /// <summary>When to fire: on press, on release, while held, always, custom
        /// expression, or after a continuous hold (<see cref="MacroTriggerMode.HoldForMs"/>).</summary>
        public MacroTriggerMode TriggerMode
        {
            get => _triggerMode;
            set
            {
                if (SetProperty(ref _triggerMode, value))
                {
                    // Audit 2026-07-18: the evaluator reads this SAME
                    // object live, so a mode switch must void every
                    // transient the old mode armed. An armed SinglePress
                    // timestamp otherwise fires DoublePress on its first
                    // post-switch press, and a mid-hold switch keeps the
                    // HoldForMs timer crediting the old mode's hold.
                    TriggerLastPressUtc = DateTime.MinValue;
                    TriggerPressStreak = 0;
                    TriggerHoldStartUtc = DateTime.MinValue;
                    TriggerHoldFired = false;
                    RunReleasedFireToCompletion = false;
                    // #238 Toggle: a mode switch drops the latch, same
                    // transient-voiding contract as the lines above.
                    ToggleTriggerLatched = false;
                    ToggleRawWasActive = false;
                    // ...and drops the RUN as well (audit 2026-07-24). The
                    // lines above void what the old mode ARMED; without
                    // this, what it STARTED outlived the switch. A latched
                    // Toggle running an all-continuous sequence kept
                    // IsExecuting through the change, and the new mode's
                    // stop conditions never matched a run it did not begin
                    // (a released OnPress never sees its own release edge),
                    // so the actions asserted forever and no later press
                    // could restart the macro. Ending the run is the same
                    // contract the disable lane already applies.
                    IsExecuting = false;
                    CurrentActionIndex = 0;
                    ComboResumeIndex = 0;
                    AwaitReleaseAfterBreak = false;
                    OnPropertyChanged(nameof(IsNotAlwaysMode));
                    OnPropertyChanged(nameof(IsCustomExpressionMode));
                    OnPropertyChanged(nameof(ShowsHoldTimeRow));
                    OnPropertyChanged(nameof(HoldTimeToolTip));
                    OnPropertyChanged(nameof(IsDoublePressMode));
                    OnPropertyChanged(nameof(ShowsInlineIntervalRow));
                    OnPropertyChanged(nameof(ShowsRepeatSection));
                    OnPropertyChanged(nameof(InlineIntervalToolTip));
                    OnPropertyChanged(nameof(TriggerPressWindowToolTip));
                    OnPropertyChanged(nameof(ShowsTriggerComboEditor));
                }
            }
        }

        /// <summary>True when TriggerMode is not Always (legacy callers: used to
        /// gate UI that should hide in Always mode).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsNotAlwaysMode => _triggerMode != MacroTriggerMode.Always;

        /// <summary>True when the shared hold-time ms row shows (#253):
        /// HoldForMs fires AT the threshold, ShortPress fires at release
        /// UNDER it. One row, one stored value, two directions (the
        /// ShowsInlineIntervalRow precedent).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool ShowsHoldTimeRow =>
            _triggerMode == MacroTriggerMode.HoldForMs ||
            _triggerMode == MacroTriggerMode.ShortPress;

        /// <summary>Tooltip for the shared hold-time row, following the
        /// active mode (the TriggerPressWindowToolTip idiom). Re-raised on
        /// mode and culture changes.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public string HoldTimeToolTip =>
            _triggerMode == MacroTriggerMode.ShortPress
                ? Strings.Instance.Macro_ShortPress_Tooltip
                : Strings.Instance.Macro_HoldForMs_Tooltip;

        /// <summary>True when the inline repeat-interval ms row shows
        /// beside the Fire picker (#238): Turbo (the interval is the turbo
        /// rate) and Toggle (while latched, the sequence re-runs at this
        /// interval). Both modes force until-release repeats in the
        /// engine, so the interval is their only live repeat setting and
        /// it sits where the mode was chosen.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool ShowsInlineIntervalRow =>
            _triggerMode == MacroTriggerMode.Turbo ||
            _triggerMode == MacroTriggerMode.Toggle;

        /// <summary>False when the Repeat section (Mode / Count / Interval)
        /// hides (#238): Turbo and Toggle override RepeatMode and
        /// RepeatCount in the engine, so showing dead controls would let
        /// the user author settings that silently do nothing. Their one
        /// live knob, the interval, moves to the inline row beside the
        /// Fire picker instead.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool ShowsRepeatSection =>
            _triggerMode != MacroTriggerMode.Turbo &&
            _triggerMode != MacroTriggerMode.Toggle &&
            // ShortPress starts with the trigger ALREADY released, so the
            // deferred-completion flag is always set and the until-release
            // repeat branch can never run (audit 2026-07-25, C17). Showing
            // Repeat there let a user author a setting the engine provably
            // ignores, the same dead-control class this gate exists for.
            _triggerMode != MacroTriggerMode.ShortPress;

        /// <summary>Tooltip for the inline interval row, following the
        /// active mode (the TriggerPressWindowToolTip idiom): the turbo
        /// rate for Turbo, the while-latched pacing for Toggle. Re-raised
        /// on mode and culture changes.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public string InlineIntervalToolTip =>
            _triggerMode == MacroTriggerMode.Turbo
                ? Strings.Instance.Macro_Turbo_Tooltip
                : Strings.Instance.Macro_ToggleInterval_Tooltip;

        /// <summary>True when TriggerMode is DoublePress (translator v17),
        /// TriplePress, or SinglePress (#238, all three consume the same
        /// press window: the chains chain through it, the single defers
        /// by it). Gates the press-window ms row in the trigger editor.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsDoublePressMode =>
            _triggerMode == MacroTriggerMode.DoublePress ||
            _triggerMode == MacroTriggerMode.TriplePress ||
            _triggerMode == MacroTriggerMode.SinglePress;

        /// <summary>Tooltip for the shared press-window ms row (#238):
        /// the double- or triple-press explanation, following the active
        /// trigger mode. Re-raised on mode and culture changes.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public string TriggerPressWindowToolTip =>
            _triggerMode == MacroTriggerMode.TriplePress
                ? Strings.Instance.Macro_TriplePress_Tooltip
                : _triggerMode == MacroTriggerMode.SinglePress
                    ? Strings.Instance.Macro_SinglePress_Tooltip
                    : Strings.Instance.Macro_DoublePress_Tooltip;

        /// <summary>True when the standard trigger-combo recording UI should show
        /// (i.e. one of OnPress / OnRelease / WhileHeld / HoldForMs /
        /// DoublePress / TriplePress / SinglePress / Toggle / Turbo).
        /// Always mode has no trigger; CustomExpression mode uses the
        /// formula editor instead.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool ShowsTriggerComboEditor =>
            _triggerMode == MacroTriggerMode.OnPress ||
            _triggerMode == MacroTriggerMode.OnRelease ||
            _triggerMode == MacroTriggerMode.WhileHeld ||
            _triggerMode == MacroTriggerMode.HoldForMs ||
            _triggerMode == MacroTriggerMode.DoublePress ||
            _triggerMode == MacroTriggerMode.TriplePress ||
            _triggerMode == MacroTriggerMode.SinglePress ||
            _triggerMode == MacroTriggerMode.Toggle ||
            _triggerMode == MacroTriggerMode.Turbo ||
            _triggerMode == MacroTriggerMode.ShortPress;

        private int _triggerHoldMs = 500;

        /// <summary>Continuous-hold threshold in milliseconds for
        /// <see cref="MacroTriggerMode.HoldForMs"/> (issue #9 wave 1b).
        /// The macro fires once when the trigger combo has been held this
        /// long; a shorter tap does nothing. Clamped to 50..10000;
        /// default 500.</summary>
        public int TriggerHoldMs
        {
            get => _triggerHoldMs;
            set => SetProperty(ref _triggerHoldMs, Math.Clamp(value, 50, 10000));
        }

        private RelayCommand _resetTriggerHoldMsCommand;
        /// <summary>Resets the hold-threshold to the 500 ms default (issue #9
        /// wave 1b), pairing the ms box with the standard reset glyph.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public RelayCommand ResetTriggerHoldMsCommand =>
            _resetTriggerHoldMsCommand ??= new RelayCommand(() => TriggerHoldMs = 500);

        private RelayCommand _resetRepeatDelayMsCommand;
        /// <summary>Resets the repeat interval to the 100 ms default, for
        /// the Turbo mode's inline interval row (#238).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public RelayCommand ResetRepeatDelayMsCommand =>
            _resetRepeatDelayMsCommand ??= new RelayCommand(() => RepeatDelayMs = 100);

        private int _triggerDoublePressMs = 442;

        /// <summary>Double-press window in milliseconds for
        /// <see cref="MacroTriggerMode.DoublePress"/> (translator v17). The
        /// macro fires on the second rising edge when it lands within this
        /// window of the first. A slower second press re-arms as a fresh
        /// first press. Clamped to 50..5000; default 442, the double-tap
        /// window Valve's own shipped controller_base templates author
        /// (basicui.vdf / basicui_neptune.vdf, "double_tap_time" "442").</summary>
        public int TriggerDoublePressMs
        {
            get => _triggerDoublePressMs;
            set => SetProperty(ref _triggerDoublePressMs, Math.Clamp(value, 50, 5000));
        }

        private RelayCommand _resetTriggerDoublePressMsCommand;
        /// <summary>Resets the double-press window to the 442 ms default,
        /// pairing the ms box with the standard reset glyph.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public RelayCommand ResetTriggerDoublePressMsCommand =>
            _resetTriggerDoublePressMsCommand ??= new RelayCommand(() => TriggerDoublePressMs = 442);

        private string _layerMask = "";

        /// <summary>Shift-layer gate (translator v25, Steam's
        /// always_on_action): when non-empty and not "Base", the macro's
        /// trigger only counts as active while this layer is the slot's
        /// engaged layer, so a set-scoped always-on command fires at set
        /// entry and stops at set exit. Empty (default) = ungated. A
        /// translator-stamped field like ShiftActivator.AxisHalf: not
        /// surfaced in the editor, carried through the DTO round-trip.</summary>
        public string LayerMask
        {
            get => _layerMask;
            set
            {
                // A NULL write is a picker artifact, never a user choice
                // (audit 2026-07-25, C1). The editor's Layer ComboBox binds
                // SelectedValue two-way over MacroLayerChoices; when the
                // choice this macro selects disappears (a layer delete, or
                // a reconcile tail-removal), WPF's Selector coerces
                // SelectedValue to null and the binding pushes it here,
                // silently downgrading a scoped macro to "" (Any layer)
                // and persisting the loss through the dirty gate.
                // Ignoring null is the same defense MappingItem.SelectedInput
                // uses against its own picker rebuilds; the persisted mask
                // stays the truth.
                if (value == null) return;
                if (SetProperty(ref _layerMask, value))
                {
                    OnPropertyChanged(nameof(HasLayerScope));
                    OnPropertyChanged(nameof(ShowsLayerRow));
                }
            }
        }

        /// <summary>True when the macro is scoped to a layer or to Base
        /// (#254 A-1), i.e. anything but the ungated "" default. Drives the
        /// scope dot on the macro list.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool HasLayerScope => !string.IsNullOrEmpty(_layerMask);

        /// <summary>True when the macro carries ANY scope (same predicate
        /// as <see cref="HasLayerScope"/>; kept as a separate name because
        /// the XAML visibility trigger binds a row-display contract, not
        /// the scope-dot contract). The editor shows the Layer row whenever
        /// this OR the slot's HasShiftLayers is true, so a scope that
        /// arrived from an import or a cross-slot copy is always visible
        /// and clearable rather than silently gating a macro the user
        /// cannot inspect (audit 2026-07-25, C3; doc corrected round four,
        /// R22: the earlier summary described a not-representable predicate
        /// the code never implemented).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool ShowsLayerRow => HasLayerScope;

        /// <summary>UTC time of the last evaluator tick that OBSERVED this
        /// macro's trigger (audit 2026-07-25 round four, replacing the
        /// round-three sticky bool). <see cref="WasTriggerActive"/> is only
        /// trustworthy when observation was CONTINUOUS: after any gap (engine
        /// stop, the macro disabled, its slot idle-skipped, a profile
        /// hot-retain), the previous sample is stale and an apparent edge on
        /// the first tick back may have happened entirely unwatched. The
        /// sticky bool modeled "sampled sometime", so every one of those gap
        /// sources re-created the held-at-start bug it was added to fix. A
        /// stamp self-heals them all: a rising edge only arms On Short Press
        /// when the previous tick is recent. Poll-thread transient.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public DateTime LastEvaluatedUtc { get; set; } = DateTime.MinValue;

        /// <summary>Transient timing state for
        /// <see cref="MacroTriggerMode.DoublePress"/>: the UTC time of the
        /// previous rising edge, or <see cref="DateTime.MinValue"/> when no
        /// first press is armed. Never serialized.</summary>
        [System.Xml.Serialization.XmlIgnore]
        internal DateTime TriggerLastPressUtc { get; set; } = DateTime.MinValue;

        /// <summary>TriplePress chain length so far (#238): how many
        /// rising edges the current fast-press chain holds. Volatile
        /// runtime state, reset when a press lands outside the window
        /// and consumed on fire.</summary>
        [System.Xml.Serialization.XmlIgnore]
        internal int TriggerPressStreak { get; set; }

        /// <summary>Transient timing state for <see cref="MacroTriggerMode.HoldForMs"/>:
        /// the UTC time the trigger combo last went active (rising edge). The
        /// evaluator arms it on each press and fires once the hold crosses
        /// <see cref="TriggerHoldMs"/>. Never serialized.</summary>
        [System.Xml.Serialization.XmlIgnore]
        internal DateTime TriggerHoldStartUtc { get; set; } = DateTime.MinValue;

        /// <summary>True once the current hold has fired, so a single hold
        /// never double-fires. Re-armed (cleared) by the evaluator on the
        /// next rising edge. Never serialized.</summary>
        [System.Xml.Serialization.XmlIgnore]
        internal bool TriggerHoldFired { get; set; }

        /// <summary>Set when a deferred SinglePress fired with the button
        /// already released (audit 2026-07-18): the activation runs its
        /// sequence ONE full pass, with the UntilRelease release-stop
        /// suppressed until the pass completes (the release already
        /// happened before the fire). Cleared on completion, disable,
        /// mode switch, and pair-cancel.</summary>
        [System.Xml.Serialization.XmlIgnore]
        internal bool RunReleasedFireToCompletion { get; set; }

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
                OnPropertyChanged(nameof(TriggerInputItems));
                OnPropertyChanged(nameof(HasTriggerInputItems));
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

        /// <summary>Variable ceiling for a custom-expression macro. The
        /// editor labels each row from the ItemsControl's AlternationIndex,
        /// which WPF assigns cyclically, so past this count two rows would
        /// carry the SAME letter while the evaluator still binds them
        /// positionally: the user would edit one variable and the formula
        /// would read the other (round 34). Keep in sync with
        /// AlternationCount on the TriggerExpressionVariables ItemsControl
        /// in PadPage.xaml.</summary>
        public const int MaxExpressionVariables = 32;

        private RelayCommand _addExpressionVariableCommand;
        public RelayCommand AddExpressionVariableCommand =>
            _addExpressionVariableCommand ??= new RelayCommand(() =>
            {
                if (TriggerExpressionVariables.Count >= MaxExpressionVariables) return;
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
                {
                    _removeActionCommand?.NotifyCanExecuteChanged();
                    // Same requery gap as the macro toolbar: Duplicate stays disabled
                    // without an explicit notify (#112).
                    _duplicateActionCommand?.NotifyCanExecuteChanged();
                }
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

        /// <summary>Raw trigger state on the previous frame for the Toggle
        /// mode's edge detector (#238). Separate from
        /// <see cref="WasTriggerActive"/>, which stores the LATCH the
        /// evaluator presents downstream. Runtime only.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool ToggleRawWasActive { get; set; }

        /// <summary>The Toggle mode's latch (#238): flipped on each raw
        /// rising edge, presented downstream as the trigger state, cleared
        /// on disable, mode switch, and layer close. Runtime only.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool ToggleTriggerLatched { get; set; }

        /// <summary>Combo-break park position (discussion #237): the action
        /// index the NEXT start resumes from. 0 = start from the top. Set
        /// by a <see cref="MacroActionType.ComboBreak"/> action, cleared on
        /// normal sequence completion, UntilRelease stop, disable, profile
        /// switch, engine stop, and app restart (volatile).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public int ComboResumeIndex { get; set; }

        /// <summary>Set when a combo break parks the sequence while the
        /// trigger is still held: hold-shaped trigger modes (WhileHeld,
        /// Always) must not auto-resume through the break, so the start
        /// gate stays closed until the trigger reads inactive once.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool AwaitReleaseAfterBreak { get; set; }

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
                var action = new MacroAction { Type = MacroActionType.ButtonPress, ButtonStyle = _buttonStyle, CustomButtonCount = _customButtonCount, RawProfileId = _extendedProfileId };
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

        private RelayCommand _duplicateActionCommand;
        /// <summary>Duplicates the selected action and inserts the clone right after it
        /// (#112). Round-trips through the ActionData DTO for a clean deep copy, then
        /// re-applies this macro's button style and count (display-only, not in the DTO).</summary>
        public RelayCommand DuplicateActionCommand =>
            _duplicateActionCommand ??= new RelayCommand(() =>
            {
                if (_selectedAction == null) return;
                var clone = SettingsService.BuildMacroAction(SettingsService.BuildActionData(_selectedAction));
                clone.ButtonStyle = _buttonStyle;
                clone.CustomButtonCount = _customButtonCount;
                clone.RawProfileId = _extendedProfileId;
                int idx = Actions.IndexOf(_selectedAction);
                if (idx < 0) Actions.Add(clone); else Actions.Insert(idx + 1, clone);
                SelectedAction = clone;
            }, () => _selectedAction != null);

        public event EventHandler RecordTriggerRequested;

        public override string ToString() => $"{_name} ({TriggerDisplayText})";
    }

    /// <summary>One removable input in a macro's trigger, rendered as a row in
    /// the trigger input list. <see cref="Label"/> is the display text;
    /// <see cref="RemoveCommand"/> drops just this one input from the trigger.
    /// <see cref="AxisEntry"/> is set only for a per-device axis input, so its
    /// row can show the Invert / Half / Either / Deadzone controls inline
    /// instead of in a second, duplicated list.</summary>
    public sealed class MacroTriggerInputItem
    {
        public MacroTriggerInputItem(string label, System.Windows.Input.ICommand removeCommand,
            MacroItem.TriggerInputEntry axisEntry = null)
        {
            Label = label;
            RemoveCommand = removeCommand;
            AxisEntry = axisEntry;
        }

        public string Label { get; }
        public System.Windows.Input.ICommand RemoveCommand { get; }

        /// <summary>The underlying axis entry when this row is a per-device axis
        /// input, else null. Drives the inline axis controls.</summary>
        public MacroItem.TriggerInputEntry AxisEntry { get; }

        /// <summary>True when this row carries an axis input and should show the
        /// Invert / Half / Either / Deadzone controls.</summary>
        public bool IsAxis => AxisEntry != null;
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
            // The static list is rebuilt by RefreshVirtualKeyValues, but an
            // x:Static binding never re-reads it; the instance accessor +
            // this raise are what reach open views.
            OnPropertyChanged(nameof(VirtualKeyChoices));
        }

        /// <summary>Instance accessor over <see cref="VirtualKeyValues"/>.
        /// x:Static ItemsSource bindings evaluate once and kept the old
        /// language after a live switch (owner report 2026-07-16).</summary>
        public List<KeyDisplayItem> VirtualKeyChoices => VirtualKeyValues;

        private MacroActionType _type = MacroActionType.ButtonPress;

        /// <summary>Type of action to perform.</summary>
        public MacroActionType Type
        {
            get => _type;
            set
            {
                if (SetProperty(ref _type, value))
                {
                    // A hidden pulse must not survive a retype (audit
                    // 2026-07-25, C40): the latch pass consults
                    // PulseWhileLatched for every latched axis action, but
                    // the editor offers the checkbox only for
                    // IsPulseCapableType. Switching ToggleVcAxis(pulse on)
                    // to Set Axis (Latched) left the flag set with no UI to
                    // see or clear it, so the "persistent" ladder value
                    // oscillated on an invisible square wave.
                    if (!IsPulseCapableType && _pulseWhileLatched)
                    {
                        _pulseWhileLatched = false;
                        OnPropertyChanged(nameof(PulseWhileLatched));
                    }
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(IsButtonType));
                    OnPropertyChanged(nameof(IsKeyType));
                    OnPropertyChanged(nameof(IsAnyKeyType));
                    OnPropertyChanged(nameof(IsDurationType));
                    OnPropertyChanged(nameof(IsAxisType));
                    OnPropertyChanged(nameof(IsMouseWheelTapType));
                    OnPropertyChanged(nameof(IsMouseNudgeType));
                    OnPropertyChanged(nameof(IsCycleTapListType));
                    OnPropertyChanged(nameof(IsSystemVolumeType));
                    OnPropertyChanged(nameof(IsAppVolumeType));
                    OnPropertyChanged(nameof(IsMouseMoveType));
                    OnPropertyChanged(nameof(IsMouseButtonType));
                    OnPropertyChanged(nameof(IsContinuousAxisType));
                    OnPropertyChanged(nameof(IsLightbarType));
                    OnPropertyChanged(nameof(IsLightbarColorClearType));
                    OnPropertyChanged(nameof(IsLightbarModeSetType));
                    OnPropertyChanged(nameof(IsLightbarModeCycleType));
                    OnPropertyChanged(nameof(IsPointerModeCycleType));
                    OnPropertyChanged(nameof(IsPointerModeSetType));
                    OnPropertyChanged(nameof(IsGuideLedBrightnessType));
                    OnPropertyChanged(nameof(IsAnyLightbarType));
                    OnPropertyChanged(nameof(IsLightbarReactiveHold));
                    OnPropertyChanged(nameof(IsLightbarStickyHold));
                    OnPropertyChanged(nameof(IsLightbarFixedColorVisible));
                    OnPropertyChanged(nameof(IsLightbarPaletteVisible));
                    OnPropertyChanged(nameof(IsRumbleType));
                    OnPropertyChanged(nameof(IsRumbleStopType));
                    OnPropertyChanged(nameof(IsRumbleTriggerType));
                    OnPropertyChanged(nameof(IsRumbleTriggerStopType));
                    OnPropertyChanged(nameof(IsAnyRumbleSetType));
                    OnPropertyChanged(nameof(IsAnyRumbleStopType));
                    OnPropertyChanged(nameof(RumbleEditorTitle));
                    OnPropertyChanged(nameof(RumbleStopEditorTitle));
                    OnPropertyChanged(nameof(RumbleStopEditorBody));
                    OnPropertyChanged(nameof(IsPlaySoundType));
                    OnPropertyChanged(nameof(IsSoundStopType));
                    OnPropertyChanged(nameof(IsAnyRumbleType));
                    OnPropertyChanged(nameof(IsSetGyroEngagedType));
                    OnPropertyChanged(nameof(IsMouseRecenterType));
                    OnPropertyChanged(nameof(IsMouseFixPositionType));
                    OnPropertyChanged(nameof(IsMouseLimitRegionType));
                    OnPropertyChanged(nameof(IsMoveMouseToScreenPositionType));
                    OnPropertyChanged(nameof(IsRepeatKeyWhileHeldType));
                    OnPropertyChanged(nameof(IsRepeatVcButtonWhileHeldType));
                    OnPropertyChanged(nameof(IsToggleVcButtonType));
                    OnPropertyChanged(nameof(IsToggleKeyType));
                    OnPropertyChanged(nameof(IsGyroRecenterType));
                    OnPropertyChanged(nameof(IsToggleMouseButtonType));
                    OnPropertyChanged(nameof(IsToggleVcAxisType));
                    OnPropertyChanged(nameof(IsRepeatVcAxisWhileHeldType));
                    OnPropertyChanged(nameof(IsToggleWheelType));
                    OnPropertyChanged(nameof(IsAxisAddType));
                    OnPropertyChanged(nameof(IsAxisSetLatchedType));
                    OnPropertyChanged(nameof(IsAxisScaleType));
                    OnPropertyChanged(nameof(IsAxisLatchReleaseType));
                    OnPropertyChanged(nameof(IsComboBreakType));
                    OnPropertyChanged(nameof(IsAxisYieldCapableType));
                    OnPropertyChanged(nameof(IsAnyMouseButtonType));
                    OnPropertyChanged(nameof(IsAnyWheelTapType));
                    OnPropertyChanged(nameof(IsAnyAxisValueType));
                    OnPropertyChanged(nameof(IsPulseCapableType));
                    OnPropertyChanged(nameof(IsAnyVcButtonType));
                    OnPropertyChanged(nameof(IsRepeatIntervalType));
                    OnPropertyChanged(nameof(IsDisconnectControllerType));
                    OnPropertyChanged(nameof(IsDisconnectSpecificDevice));
                    OnPropertyChanged(nameof(DisconnectDeviceOptions));
                    OnPropertyChanged(nameof(IsRunProgramType));
                    OnPropertyChanged(nameof(IsTextBlockType));
                    OnPropertyChanged(nameof(IsRumbleReactiveHold));
                    OnPropertyChanged(nameof(IsRumbleStickyHold));

                    // Seed a freshly-typed pin action with the primary monitor's
                    // current center (issue #109). XML defaults cannot run code, so
                    // a (0,0) coord would sit the pin at the screen corner. Treat
                    // (0,0) as "unconfigured" and resolve it once on type switch.
                    // A loaded action keeps its saved coord (deserialized before
                    // this path ever runs).
                    if (_type == MacroActionType.MouseFixPosition
                        && _cursorPinX == 0 && _cursorPinY == 0
                        && CursorControlService.TryGetPrimaryCenter(out int pcx, out int pcy))
                    {
                        CursorPinX = pcx;
                        CursorPinY = pcy;
                    }

                    // Same rationale for a freshly-typed move-cursor action (#9):
                    // seed the target to the primary-monitor center so an
                    // unconfigured (0,0) doesn't warp the cursor to the corner.
                    if (_type == MacroActionType.MoveMouseToScreenPosition
                        && _mouseX == 0 && _mouseY == 0
                        && CursorControlService.TryGetPrimaryCenter(out int mcx, out int mcy))
                    {
                        MouseX = mcx;
                        MouseY = mcy;
                    }
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
        /// field for its hold time: ButtonPress / KeyPress / Delay /
        /// MouseButtonPress / AxisHold / AxisAdd (the relative add rides
        /// the AxisHold duration shape, discussion #237). LightbarColor
        /// uses its own <c>LightbarHoldMs</c>/<c>LightbarFadeMs</c> pair
        /// instead so the hold and fade sliders can be scaled and labeled
        /// separately from the generic ms field.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsDurationType => _type == MacroActionType.ButtonPress || _type == MacroActionType.KeyPress || _type == MacroActionType.Delay || _type == MacroActionType.MouseButtonPress || _type == MacroActionType.AxisHold || _type == MacroActionType.AxisAdd || _type == MacroActionType.AxisScale;

        /// <summary>True when Type is AxisSet or AxisHold (both edit the
        /// axis target + value pair; AxisHold adds the duration knob via
        /// <see cref="IsDurationType"/>).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAxisType => _type == MacroActionType.AxisSet || _type == MacroActionType.AxisHold
            || _type == MacroActionType.AxisSetLatched || _type == MacroActionType.AxisScale;

        /// <summary>True when Type is MouseWheelTap (v15).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsMouseWheelTapType => _type == MacroActionType.MouseWheelTap;

        /// <summary>True when Type is MouseNudge (v16).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsMouseNudgeType => _type == MacroActionType.MouseNudge;

        /// <summary>True when Type is CycleTapList (v16).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsCycleTapListType => _type == MacroActionType.CycleTapList;

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

        /// <summary>True when Type is RumbleTrigger (issue #102).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsRumbleTriggerType => _type == MacroActionType.RumbleTrigger;

        /// <summary>True when Type is RumbleTriggerStop (issue #102).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsRumbleTriggerStopType => _type == MacroActionType.RumbleTriggerStop;

        /// <summary>True for either rumble-set action (main motors or trigger).
        /// Both share the strength / hold-mode / duration param editor.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAnyRumbleSetType => IsRumbleType || IsRumbleTriggerType;

        /// <summary>True for either rumble-stop action (main motors or trigger).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAnyRumbleStopType => IsRumbleStopType || IsRumbleTriggerStopType;

        /// <summary>Header for the shared rumble-set param card: the trigger title
        /// for a RumbleTrigger action, otherwise the main-motor Rumble title (#102).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public string RumbleEditorTitle => IsRumbleTriggerType
            ? Strings.Instance.MacroAction_Type_RumbleTrigger
            : Strings.Instance.MacroAction_Type_Rumble;

        /// <summary>Header for the shared rumble-stop card (#102).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public string RumbleStopEditorTitle => IsRumbleTriggerStopType
            ? Strings.Instance.MacroAction_Type_RumbleTriggerStop
            : Strings.Instance.MacroAction_Type_RumbleStop;

        /// <summary>Body text for the shared rumble-stop card (#102).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public string RumbleStopEditorBody => IsRumbleTriggerStopType
            ? Strings.Instance.MacroAction_RumbleTriggerStop_Tooltip
            : Strings.Instance.MacroAction_RumbleStop_Tooltip;

        public bool IsPlaySoundType => _type == MacroActionType.PlaySound;

        public bool IsSoundStopType => _type == MacroActionType.SoundStop;

        /// <summary>True when Type is SetGyroEngaged. Surfaces the
        /// Mode dropdown editor in the macro action UI.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsSetGyroEngagedType => _type == MacroActionType.SetGyroEngaged;

        /// <summary>True when Type is MouseRecenter (issue #108). Surfaces the
        /// Cursor Recenter Mode dropdown in the macro action UI.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsMouseRecenterType => _type == MacroActionType.MouseRecenter;

        /// <summary>True when Type is MouseFixPosition (issue #109). Surfaces the
        /// Cursor Pin Mode dropdown plus the Pin X / Pin Y spinboxes.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsMouseFixPositionType => _type == MacroActionType.MouseFixPosition;

        /// <summary>True when Type is MouseLimitRegion (issue #110). Surfaces the
        /// Cursor Clamp Mode dropdown plus the Inset X / Inset Y spinboxes.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsMouseLimitRegionType => _type == MacroActionType.MouseLimitRegion;

        /// <summary>True when Type is MoveMouseToScreenPosition (issue #9). Surfaces
        /// the Mouse X / Mouse Y spinboxes plus the "Pick on screen" capture button.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsMoveMouseToScreenPositionType => _type == MacroActionType.MoveMouseToScreenPosition;

        /// <summary>True when Type is RepeatKeyWhileHeld (issue #9). Surfaces the
        /// key picker (shared with Key Press) plus the Interval spinbox.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsRepeatKeyWhileHeldType => _type == MacroActionType.RepeatKeyWhileHeld;

        /// <summary>True when Type is RepeatVcButtonWhileHeld (issue #9 wave 1b).
        /// Surfaces the button-target grid (shared with Button Press) plus the
        /// Interval spinbox.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsRepeatVcButtonWhileHeldType => _type == MacroActionType.RepeatVcButtonWhileHeld;

        /// <summary>True when Type is ToggleVcButton (issue #9 wave 1b).
        /// Surfaces the button-target grid (shared with Button Press).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsToggleVcButtonType => _type == MacroActionType.ToggleVcButton;

        /// <summary>True when Type is ToggleKey (issue #9 wave 1b). Surfaces the
        /// key picker (shared with Key Press).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsToggleKeyType => _type == MacroActionType.ToggleKey;

        /// <summary>True when Type is GyroRecenter (issue #9 wave 1b, B-18).
        /// Gates the parameter-less info card in the macro editor.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsGyroRecenterType => _type == MacroActionType.GyroRecenter;

        /// <summary>True when Type is ToggleMouseButton (v19, M1). Surfaces
        /// the mouse-button picker (shared with Mouse Button Press) plus the
        /// pulse-while-latched row.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsToggleMouseButtonType => _type == MacroActionType.ToggleMouseButton;

        /// <summary>True when Type is ToggleVcAxis (v19, M1). Surfaces the
        /// axis target / value pair (shared with Set Axis) plus the
        /// pulse-while-latched row.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsToggleVcAxisType => _type == MacroActionType.ToggleVcAxis;

        /// <summary>True when Type is RepeatVcAxisWhileHeld (v19, M1).
        /// Surfaces the axis target / value pair plus the Interval
        /// spinbox.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsRepeatVcAxisWhileHeldType => _type == MacroActionType.RepeatVcAxisWhileHeld;

        /// <summary>True when Type is ToggleWheel (v19, M1). Surfaces the
        /// wheel tick / horizontal pair (shared with Mouse Wheel Tick) plus
        /// the Interval spinbox.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsToggleWheelType => _type == MacroActionType.ToggleWheel;

        /// <summary>True when Type is AxisAdd (discussion #237). Surfaces
        /// the signed-value hint under the shared axis target / value pair
        /// (the value box already accepts the full -32768..32767 range, so
        /// the relative add reuses it as is).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAxisAddType => _type == MacroActionType.AxisAdd;

        /// <summary>True when Type is AxisSetLatched (#251). Gates the
        /// ladder hint under the shared axis editor.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAxisSetLatchedType => _type == MacroActionType.AxisSetLatched;

        /// <summary>True when Type is AxisScale (#251). Surfaces the
        /// signed-percent hint under the shared axis target / value pair:
        /// the value is the scale delta, -50% halves the current
        /// deflection, +50% amplifies half again.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAxisScaleType => _type == MacroActionType.AxisScale;

        /// <summary>True when Type is AxisLatchRelease (#251). Surfaces the
        /// axis-target-only row (None reads as "all axes").</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAxisLatchReleaseType => _type == MacroActionType.AxisLatchRelease;

        /// <summary>True when Type is ComboBreak (discussion #237). Gates
        /// the parameter-less info card in the macro editor, the
        /// GyroRecenter card shape.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsComboBreakType => _type == MacroActionType.ComboBreak;

        /// <summary>True for the axis holds that honor
        /// <see cref="AxisYieldToPhysical"/> (discussion #237): AxisHold,
        /// ToggleVcAxis, and RepeatVcAxisWhileHeld. Gates the yield
        /// checkbox row.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAxisYieldCapableType
            => _type is MacroActionType.AxisHold or MacroActionType.ToggleVcAxis
                or MacroActionType.RepeatVcAxisWhileHeld or MacroActionType.AxisSetLatched;

        /// <summary>True when the mouse-button picker applies:
        /// MouseButtonPress / MouseButtonRelease plus the v18
        /// ToggleMouseButton latch, which addresses its target through the
        /// same <see cref="MouseButton"/> knob (the IsAnyKeyType pattern
        /// applied to the mouse-button picker, v19 M1).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAnyMouseButtonType => IsMouseButtonType || IsToggleMouseButtonType;

        /// <summary>True when the wheel tick / horizontal editor applies:
        /// MouseWheelTap plus the v18 ToggleWheel latch, both reading
        /// <see cref="AxisValue"/> as the signed tick count and
        /// <see cref="WheelHorizontal"/> as the lane (v19, M1).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAnyWheelTapType => IsMouseWheelTapType || IsToggleWheelType;

        /// <summary>True when the axis target / value editor applies:
        /// AxisSet / AxisHold plus the v18 ToggleVcAxis latch and
        /// RepeatVcAxisWhileHeld turbo, which write the same
        /// <see cref="AxisTarget"/> / <see cref="AxisValue"/> pair
        /// (v19, M1). The AxisAdd relative deflection (discussion #237)
        /// rides the same pair, with AxisValue read as the signed
        /// per-frame add.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAnyAxisValueType => IsAxisType
            || _type == MacroActionType.ToggleVcAxis
            || _type == MacroActionType.RepeatVcAxisWhileHeld
            || _type == MacroActionType.AxisAdd;

        /// <summary>True for the latch actions whose held contribution can
        /// pulse on the turbo square wave (<see cref="PulseWhileLatched"/>):
        /// ToggleVcButton / ToggleKey plus the v18 ToggleMouseButton and
        /// ToggleVcAxis. Gates the pulse checkbox row (v19, M1).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsPulseCapableType
            => _type is MacroActionType.ToggleVcButton or MacroActionType.ToggleKey
                or MacroActionType.ToggleMouseButton or MacroActionType.ToggleVcAxis;

        /// <summary>True when the VC button-target checkbox grid applies:
        /// ButtonPress / ButtonRelease plus the wave-1b RepeatVcButtonWhileHeld
        /// turbo and ToggleVcButton latch, which reuse the same
        /// <see cref="ButtonFlags"/> / <see cref="CustomButtonWords"/> target
        /// pair (the IsAnyKeyType pattern applied to the button grid).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAnyVcButtonType => IsButtonType
            || _type == MacroActionType.RepeatVcButtonWhileHeld
            || _type == MacroActionType.ToggleVcButton;

        /// <summary>True when the Interval ms row applies: the turbo
        /// actions (RepeatKeyWhileHeld, RepeatVcButtonWhileHeld, and the
        /// v18 RepeatVcAxisWhileHeld), the ToggleWheel latch (one detent
        /// per interval), and any pulse-capable latch whose
        /// <see cref="PulseWhileLatched"/> is on. All share the
        /// <see cref="IntervalMs"/> knob (v19, M1).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsRepeatIntervalType => _type == MacroActionType.RepeatKeyWhileHeld
            || _type == MacroActionType.RepeatVcButtonWhileHeld
            || _type == MacroActionType.RepeatVcAxisWhileHeld
            || _type == MacroActionType.ToggleWheel
            || (IsPulseCapableType && _pulseWhileLatched);

        /// <summary>True when the key-combo picker applies: KeyPress / KeyRelease,
        /// the RepeatKeyWhileHeld autofire (issue #9), or the ToggleKey latch
        /// (issue #9 wave 1b). All read <see cref="ParsedKeyCodes"/>.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAnyKeyType => IsKeyType
            || _type == MacroActionType.RepeatKeyWhileHeld
            || _type == MacroActionType.ToggleKey;

        /// <summary>True when Type is DisconnectController (issue #162). Surfaces
        /// the target-mode dropdown.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsDisconnectControllerType => _type == MacroActionType.DisconnectController;

        /// <summary>True when Type is RunProgram (user request). Surfaces the program
        /// path / arguments / working-folder editor.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsRunProgramType => _type == MacroActionType.RunProgram;

        /// <summary>True when Type is TextBlock (#201). Gates the multiline
        /// text editor panel.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsTextBlockType => _type == MacroActionType.TextBlock;

        /// <summary>True when the Disconnect action is in Specific-device mode.
        /// Surfaces the device picker.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsDisconnectSpecificDevice =>
            IsDisconnectControllerType && _disconnectTarget == MacroDisconnectTarget.SpecificDevice;

        /// <summary>True when Type is any rumble-related action — drives
        /// the macro editor's grouping into a single CardBorder.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAnyRumbleType
            => _type == MacroActionType.Rumble
            || _type == MacroActionType.RumbleStop
            || _type == MacroActionType.RumbleTrigger
            || _type == MacroActionType.RumbleTriggerStop;

        /// <summary>True when the Rumble action is in Reactive hold mode —
        /// drives visibility of the hold/fade sliders in the editor.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsRumbleReactiveHold
            => (_type == MacroActionType.Rumble || _type == MacroActionType.RumbleTrigger)
               && _rumbleHoldMode == MacroRumbleHoldMode.Reactive;

        /// <summary>True when the Rumble action is in Sticky hold mode.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsRumbleStickyHold
            => (_type == MacroActionType.Rumble || _type == MacroActionType.RumbleTrigger)
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

        /// <summary>True when Type is PointerModeCycle (issue #203).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsPointerModeCycleType => _type == MacroActionType.PointerModeCycle;

        /// <summary>True when Type is PointerModeSet (issue #203 follow-up).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsPointerModeSetType => _type == MacroActionType.PointerModeSet;

        /// <summary>True when Type is GuideLedBrightness (discussion #209).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsGuideLedBrightnessType => _type == MacroActionType.GuideLedBrightness;

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

        private string _extendedProfileId;

        /// <summary>
        /// The Extended slot's HIDMaestro profile slug, cascaded from the
        /// owning MacroItem (#215). Re-letters the Numbered button labels
        /// on Switch Pro profiles; display-only.
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public string RawProfileId
        {
            get => _extendedProfileId;
            set
            {
                if (SetProperty(ref _extendedProfileId, value))
                {
                    _buttonOptions = null;
                    OnPropertyChanged(nameof(ButtonOptions));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        private ushort _buttonFlags;

        /// <summary>
        /// For ButtonPress/ButtonRelease with gamepad presets: Xbox bitmask
        /// flags. The wave-1b RepeatVcButtonWhileHeld / ToggleVcButton
        /// actions address their target button through this same field
        /// (and <see cref="CustomButtonWords"/> on Extended slots).
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
                    // Mirrors the Lighting tab's dropdown order (simple
                    // time-based, multi-color, battery, audio cluster),
                    // led by the PlayerNumber default and the deliberate
                    // Off, plus the legacy InputReactive* compat values.
                    var modes = new[] {
                        LightbarMode.PlayerNumber, LightbarMode.Off,
                        LightbarMode.Static, LightbarMode.Breathing, LightbarMode.Strobe,
                        LightbarMode.Rainbow, LightbarMode.ColorCycle, LightbarMode.Battery,
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
                        // Dynamic list for custom Extended. N buttons from
                        // config, lettered per profile on Switch Pro (#215).
                        var list = new List<GamepadButtonOption>();
                        for (int i = 0; i < _customButtonCount; i++)
                            list.Add(new GamepadButtonOption(this, MacroButtonNames.RawButtonShortLabel(_extendedProfileId, i + 1), customIndex: i));
                        _buttonOptions = list.AsReadOnly();
                    }
                    else
                    {
                        var defs = MacroButtonNames.GetButtonDefs(_buttonStyle, _extendedProfileId);
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

        /// <summary>Returns a user-friendly localized display name for a
        /// virtual key. Internal: MappingDisplayResolver reuses this
        /// vocabulary for keyboard-device keys the engine's invariant
        /// table leaves as "Key 0xNN" hex.</summary>
        internal static string VirtualKeyDisplayName(VirtualKey vk) => vk switch
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

        private int[] _parsedKeyCodesCache;
        private string _parsedKeyCodesCacheKeyString;
        private int _parsedKeyCodesCacheKeyCode;

        /// <summary>
        /// Parses KeyString into an array of VK codes. Falls back to legacy KeyCode
        /// if KeyString is empty but KeyCode is set. Memoized against the inputs it
        /// was computed from because the macro executor reads this on the ~1000Hz
        /// poll thread every held frame, where a per-call regex parse and array
        /// allocation are not affordable.
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public int[] ParsedKeyCodes
        {
            get
            {
                string keyString = _keyString;
                int keyCode = _keyCode;
                var cached = _parsedKeyCodesCache;
                if (cached != null
                    && _parsedKeyCodesCacheKeyCode == keyCode
                    && string.Equals(_parsedKeyCodesCacheKeyString, keyString, StringComparison.Ordinal))
                    return cached;
                cached = !string.IsNullOrWhiteSpace(keyString)
                    ? ParseKeyString(keyString)
                    : (keyCode != 0 ? new[] { keyCode } : Array.Empty<int>());
                _parsedKeyCodesCacheKeyString = keyString;
                _parsedKeyCodesCacheKeyCode = keyCode;
                _parsedKeyCodesCache = cached;
                return cached;
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
                {
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(AxisValuePercent));
                }
            }
        }

        /// <summary>The editor-facing form of <see cref="AxisValue"/>: a
        /// signed percent of full deflection (-100..100), the unit every
        /// summary and tooltip already speaks. The raw -32768..32767 field
        /// stays the persisted truth, so profiles and the clipboard are
        /// untouched. Typing 75 means 75% of full scale, which is what a
        /// human means by 75 (the raw box demanded 24575 for the same
        /// thing, which nobody could know).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public double AxisValuePercent
        {
            get => Math.Round(_axisValue * 100.0 / 32767.0);
            set
            {
                double clamped = Math.Clamp(value, -100.0, 100.0);
                AxisValue = (short)Math.Round(clamped * 32767.0 / 100.0);
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

        private bool _wheelHorizontal;

        /// <summary>For MouseWheelTap (v15): tick the horizontal
        /// (MOUSEEVENTF_HWHEEL) lane instead of the vertical wheel.</summary>
        public bool WheelHorizontal
        {
            get => _wheelHorizontal;
            set
            {
                if (SetProperty(ref _wheelHorizontal, value))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private string _cycleStepsCsv = "";

        /// <summary>For CycleTapList (v16): the ordered step list. Steps
        /// separated by ','; parts of one step joined '+' (they fire
        /// together); part = kind ':' value. Kinds: K:{vk} key tap,
        /// M:{0..4} mouse-button click, W:{ticks} vertical wheel,
        /// H:{ticks} horizontal wheel, B:{mask} VC button flags,
        /// A:{axisTarget}:{value} VC axis assert (MacroAxisTarget ordinal,
        /// trigger targets on the AxisHold pull scale). Example:
        /// "K:49,K:50,W:1+M:0".</summary>
        public string CycleStepsCsv
        {
            get => _cycleStepsCsv;
            set
            {
                if (SetProperty(ref _cycleStepsCsv, value ?? ""))
                {
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(ParsedCycleSteps));
                }
            }
        }

        private bool _cycleWrap = true;

        /// <summary>For CycleTapList (v16): wrap past the last step back
        /// to the first. Off = further fires produce nothing once the end
        /// is reached (Steam's Wrap List - Off).</summary>
        public bool CycleWrap
        {
            get => _cycleWrap;
            set
            {
                if (SetProperty(ref _cycleWrap, value))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        /// <summary>CycleTapList runtime position (v16): index of the NEXT
        /// step to fire. Per-action volatile like the ToggleVcButton
        /// latch: lives with the loaded action, resets on profile switch
        /// and app restart, never persisted.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public int CycleIndex { get; set; }

        /// <summary>Transient CycleTapList latch (v16): true once the
        /// current step's injection parts (key / mouse / wheel) have
        /// fired, cleared when the step completes. A latch instead of the
        /// executor's usual actionElapsed &lt; 1 convention because a
        /// loaded frame can arrive later than 1 ms after the trigger
        /// stamp, which would silently swallow the one-shot parts. Never
        /// serialized.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool CycleInjectionFired { get; set; }

        private CycleStepPart[][] _parsedCycleStepsCache;
        private string _parsedCycleStepsCacheCsv;

        /// <summary>Parses <see cref="CycleStepsCsv"/> into step parts,
        /// memoized against the CSV (the executor reads this per fire on
        /// the poll thread). Unparseable parts are dropped, and a step whose
        /// parts all drop is dropped whole.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public CycleStepPart[][] ParsedCycleSteps
        {
            get
            {
                string csv = _cycleStepsCsv;
                var cached = _parsedCycleStepsCache;
                if (cached != null && ReferenceEquals(_parsedCycleStepsCacheCsv, csv))
                    return cached;
                var steps = new List<CycleStepPart[]>();
                foreach (var stepText in (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = new List<CycleStepPart>();
                    foreach (var partText in stepText.Split('+', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (CycleStepPart.TryParse(partText, out var part))
                            parts.Add(part);
                    }
                    if (parts.Count > 0) steps.Add(parts.ToArray());
                }
                var result = steps.ToArray();
                _parsedCycleStepsCache = result;
                _parsedCycleStepsCacheCsv = csv;
                return result;
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

        // ── Run program (for MacroActionType.RunProgram, user request) ──

        private string _programPath = "";
        /// <summary>Path to the program or file to launch. ShellExecute, so a document
        /// or URL with a file association works too.</summary>
        public string ProgramPath
        {
            get => _programPath;
            set { if (SetProperty(ref _programPath, value ?? "")) OnPropertyChanged(nameof(DisplayText)); }
        }

        private string _programArgs = "";
        /// <summary>Command-line arguments passed to the program (one string).</summary>
        public string ProgramArgs
        {
            get => _programArgs;
            set => SetProperty(ref _programArgs, value ?? "");
        }

        private string _programWorkingDir = "";
        /// <summary>Working folder for the launched program. Empty leaves it to the
        /// shell default.</summary>
        public string ProgramWorkingDir
        {
            get => _programWorkingDir;
            set => SetProperty(ref _programWorkingDir, value ?? "");
        }

        // ── Text Block (for MacroActionType.TextBlock, issue #201) ──

        private string _textContent = "";
        /// <summary>The plain text a TextBlock action types out. There is no token
        /// grammar (Key Press owns combos). Newlines press Enter, tabs press Tab.
        /// The setter normalizes line endings to LF and strips the control
        /// characters the settings XML cannot carry, so the stored value
        /// round-trips byte-identically on every persistence leg.</summary>
        public string TextContent
        {
            get => _textContent;
            set { if (SetProperty(ref _textContent, SanitizeTextContent(value))) OnPropertyChanged(nameof(DisplayText)); }
        }

        /// <summary>Normalizes a Text Block's content for storage: CRLF and lone CR
        /// become LF, and C0 control characters other than tab and LF are stripped.
        /// Two forcing facts, both verified against XmlSerializer: a C0 character
        /// (e.g. ESC pasted from ANSI-colored terminal text) makes the WHOLE
        /// settings save throw on every autosave until removed, and a CR survives
        /// the save but the XML parser normalizes it to LF on load, so persisting
        /// CR would leave the XML and clipboard-JSON legs byte-divergent. The
        /// emitter treats \n, \r, and \r\n as one Enter each, so normalizing here
        /// changes no typed output and keeps per-character pacing one-slot-per-Enter.</summary>
        internal static string SanitizeTextContent(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? "";
            bool dirty = false;
            foreach (char c in value)
            {
                if (c < ' ' && c != '\t' && c != '\n') { dirty = true; break; }
            }
            if (!dirty) return value;

            var sb = new System.Text.StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\r')
                {
                    if (i + 1 < value.Length && value[i + 1] == '\n') continue; // CRLF folds to the LF
                    sb.Append('\n'); // lone CR (classic Mac text) keeps its line break
                }
                else if (c < ' ' && c != '\t' && c != '\n')
                {
                    // C0 control character the XML infoset forbids: drop it.
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private int _textPerCharDelayMs;
        /// <summary>Milliseconds between typed characters. 0 (default) emits the
        /// whole text as one batched SendInput call.</summary>
        public int TextPerCharDelayMs
        {
            get => _textPerCharDelayMs;
            set => SetProperty(ref _textPerCharDelayMs, Math.Clamp(value, 0, 1000));
        }

        /// <summary>Runtime emission cursor for TextBlock pacing: how many UTF-16
        /// code units of <see cref="TextContent"/> the current run has typed. The
        /// executor re-arms it to 0 when the action completes, and macro start
        /// resets it alongside the mouse accumulators, so an interrupted run can't
        /// resume mid-string.</summary>
        [System.Xml.Serialization.XmlIgnore]
        internal int TextEmitCursor;

        /// <summary>Pure pacing math for TextBlock emission: how many UTF-16 code
        /// units of <paramref name="text"/> should have been typed once
        /// <paramref name="elapsedMs"/> has passed. Delay 0 (or less) is the whole
        /// string at once; otherwise code unit k emits when elapsed reaches
        /// k * delay, so the first character goes out on the action's first tick.
        /// Never splits a surrogate pair: a boundary landing between the halves
        /// pulls the low half forward into the same emission.</summary>
        internal static int ComputeTextEmitTarget(string text, int perCharDelayMs, double elapsedMs)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int target = perCharDelayMs <= 0
                ? text.Length
                : Math.Min(text.Length, (int)(elapsedMs / perCharDelayMs) + 1);
            if (target > 0 && target < text.Length && char.IsHighSurrogate(text[target - 1]))
                target++;
            return target;
        }

        // ── Bluetooth disconnect (for MacroActionType.DisconnectController, issue #162) ──

        private MacroDisconnectTarget _disconnectTarget = MacroDisconnectTarget.TriggeringDevice;
        /// <summary>Which device(s) the disconnect targets. Explicit on the
        /// action: the trigger alone cannot express "turn off device X from
        /// device Y".</summary>
        public MacroDisconnectTarget DisconnectTarget
        {
            get => _disconnectTarget;
            set
            {
                if (SetProperty(ref _disconnectTarget, value))
                {
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(IsDisconnectSpecificDevice));
                }
            }
        }

        private Guid _disconnectDeviceGuid = Guid.Empty;
        /// <summary>The victim device for
        /// <see cref="MacroDisconnectTarget.SpecificDevice"/> mode.</summary>
        public Guid DisconnectDeviceGuid
        {
            get => _disconnectDeviceGuid;
            set
            {
                if (SetProperty(ref _disconnectDeviceGuid, value))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        /// <summary>Devices offered by the Specific-device picker: every known
        /// device on a Bluetooth path. Computed on read so the list is fresh
        /// each time the editor shows it.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public List<MacroDisconnectDeviceOption> DisconnectDeviceOptions
        {
            get
            {
                var options = new List<MacroDisconnectDeviceOption>();
                lock (SettingsManager.UserDevices.SyncRoot)
                {
                    foreach (var ud in SettingsManager.UserDevices.Items)
                    {
                        if (ud == null || ud.InstanceGuid == Guid.Empty) continue;
                        if (!PadForge.Common.Input.BluetoothLinkHelper.IsDisconnectTarget(ud.DevicePath, ud.VendorId, ud.ProdId)) continue;
                        options.Add(new MacroDisconnectDeviceOption
                        {
                            Guid = ud.InstanceGuid,
                            Name = ud.ResolvedName ?? ud.InstanceGuid.ToString(),
                        });
                    }
                }
                // The saved victim may be offline/forgotten: keep it pickable
                // (and its selection visible) rather than silently blanking.
                if (_disconnectDeviceGuid != Guid.Empty
                    && !options.Exists(o => o.Guid == _disconnectDeviceGuid))
                {
                    options.Add(new MacroDisconnectDeviceOption
                    {
                        Guid = _disconnectDeviceGuid,
                        Name = SettingsManager.FindDeviceByInstanceGuid(_disconnectDeviceGuid)?.ResolvedName
                               ?? _disconnectDeviceGuid.ToString(),
                    });
                }
                return options;
            }
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
                    // Drop the parsed cache FIRST. The getter returns
                    // _lightbarPaletteCache whenever it is non-null, so
                    // raising LightbarPalette without clearing it re-handed
                    // the UI the palette parsed from the PREVIOUS csv, and a
                    // macro switch or paste showed the old colors
                    // (round 34).
                    _lightbarPaletteCache = null;
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

        private CursorRecenterMode _cursorRecenterMode = CursorRecenterMode.XAndY;
        /// <summary>Which axes a <see cref="MacroActionType.MouseRecenter"/> action
        /// snaps to the primary-monitor center (issue #108).</summary>
        public CursorRecenterMode CursorRecenterMode
        {
            get => _cursorRecenterMode;
            set
            {
                if (SetProperty(ref _cursorRecenterMode, value))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private CursorPinMode _cursorPinMode = CursorPinMode.XAndY;
        /// <summary>Which axes a <see cref="MacroActionType.MouseFixPosition"/>
        /// action pins while engaged (issue #109). The other axis stays free.</summary>
        public CursorPinMode CursorPinMode
        {
            get => _cursorPinMode;
            set
            {
                if (SetProperty(ref _cursorPinMode, value))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private int _cursorPinX;
        /// <summary>Target X coordinate (primary-monitor physical pixels) a pin
        /// action holds while engaged (issue #109). Defaults to the monitor center
        /// on first type switch. Clamped to the primary monitor's width.</summary>
        public int CursorPinX
        {
            get => _cursorPinX;
            set => SetProperty(ref _cursorPinX, ClampToPrimaryWidth(value));
        }

        private int _cursorPinY;
        /// <summary>Target Y coordinate (primary-monitor physical pixels) a pin
        /// action holds while engaged (issue #109). Defaults to the monitor center
        /// on first type switch. Clamped to the primary monitor's height.</summary>
        public int CursorPinY
        {
            get => _cursorPinY;
            set => SetProperty(ref _cursorPinY, ClampToPrimaryHeight(value));
        }

        private int _mouseX;
        /// <summary>Target X coordinate (primary-monitor physical pixels) a
        /// <see cref="MacroActionType.MoveMouseToScreenPosition"/> action warps the
        /// cursor to on press (issue #9). Seeded to the primary-monitor center on
        /// first type switch and pickable on screen. Clamped to the primary
        /// monitor's width, mirroring <see cref="CursorPinX"/>.</summary>
        public int MouseX
        {
            get => _mouseX;
            set
            {
                if (SetProperty(ref _mouseX, ClampToPrimaryWidth(value)))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private int _mouseY;
        /// <summary>Target Y coordinate (primary-monitor physical pixels) a
        /// <see cref="MacroActionType.MoveMouseToScreenPosition"/> action warps the
        /// cursor to on press (issue #9). Seeded to the primary-monitor center on
        /// first type switch and pickable on screen. Clamped to the primary
        /// monitor's height, mirroring <see cref="CursorPinY"/>.</summary>
        public int MouseY
        {
            get => _mouseY;
            set
            {
                if (SetProperty(ref _mouseY, ClampToPrimaryHeight(value)))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private int _nudgeDx;

        /// <summary>Signed X pixel delta a
        /// <see cref="MacroActionType.MouseNudge"/> action moves the cursor
        /// by per fire (v16). Screen frame: positive = right. Deliberately
        /// NOT the clamped <see cref="MouseX"/> warp coordinate: a nudge is
        /// a relative offset and negative values are the point.</summary>
        public int NudgeDx
        {
            get => _nudgeDx;
            set
            {
                if (SetProperty(ref _nudgeDx, value))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private int _nudgeDy;

        /// <summary>Signed Y pixel delta for
        /// <see cref="MacroActionType.MouseNudge"/> (v16). Screen frame:
        /// positive = down (the SendInput MOUSEEVENTF_MOVE convention).</summary>
        public int NudgeDy
        {
            get => _nudgeDy;
            set
            {
                if (SetProperty(ref _nudgeDy, value))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private bool _axisYieldToPhysical;
        /// <summary>Yield-to-physical for the absolute axis holds
        /// (discussion #237, reWASD's "Absolute deflection" contract:
        /// "once you move your physical stick, the combo will go back to
        /// zero, and now your stick will have a higher priority"). While
        /// set on an <see cref="MacroActionType.AxisHold"/>,
        /// <see cref="MacroActionType.ToggleVcAxis"/>, or
        /// <see cref="MacroActionType.RepeatVcAxisWhileHeld"/> action,
        /// the engine checks the target's already-mapped value each
        /// frame BEFORE overwriting: the first frame the physical input
        /// exceeds the yield threshold, the macro write is suppressed
        /// and STAYS suppressed for the remainder of that activation
        /// (latched, matching reWASD's "goes back to zero"), re-arming
        /// when the action re-fires. Off by default so existing macros
        /// keep their macro-wins semantics.</summary>
        public bool AxisYieldToPhysical
        {
            get => _axisYieldToPhysical;
            set
            {
                if (SetProperty(ref _axisYieldToPhysical, value))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private int _intervalMs = 100;
        /// <summary>Repeat interval in milliseconds for
        /// <see cref="MacroActionType.RepeatKeyWhileHeld"/> (issue #9): the pad
        /// fires one KeyDown+KeyUp pulse each interval while the trigger is held.
        /// Also the square-wave period for
        /// <see cref="MacroActionType.RepeatVcButtonWhileHeld"/> (issue #9 wave
        /// 1b), which holds its target button for half the interval and
        /// releases it for the other half.
        /// Clamped to 10..1000; default 100 (10 taps/second).</summary>
        public int IntervalMs
        {
            get => _intervalMs;
            set
            {
                if (SetProperty(ref _intervalMs, Math.Clamp(value, 10, 1000)))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        /// <summary>Transient timing state for
        /// <see cref="MacroActionType.RepeatKeyWhileHeld"/>: the UTC time the last
        /// autofire pulse was sent. Reset to <see cref="DateTime.MinValue"/> so the
        /// first held frame fires immediately. Never serialized.</summary>
        [System.Xml.Serialization.XmlIgnore]
        internal DateTime RepeatKeyLastFireUtc { get; set; } = DateTime.MinValue;

        /// <summary>Transient timing state for
        /// <see cref="MacroActionType.RepeatVcButtonWhileHeld"/> (issue #9 wave
        /// 1b): the UTC time the pulse phase last flipped. MinValue makes the
        /// first held frame flip to ON immediately, mirroring
        /// <see cref="RepeatKeyLastFireUtc"/>. Reset on macro (re)start via
        /// InputManager.ResetMouseAccumulators. Never serialized.</summary>
        [System.Xml.Serialization.XmlIgnore]
        internal DateTime RepeatVcLastToggleUtc { get; set; } = DateTime.MinValue;

        /// <summary>Current phase of the RepeatVcButtonWhileHeld square wave:
        /// true = the target button is written this half-period. Runtime
        /// state beside <see cref="RepeatVcLastToggleUtc"/>. Never serialized.</summary>
        [System.Xml.Serialization.XmlIgnore]
        internal bool RepeatVcPulseOn { get; set; }

        /// <summary>Volatile latch bit for <see cref="MacroActionType.ToggleVcButton"/>
        /// (issue #9 wave 1b). While set, the engine ORs the action's target
        /// button(s) into the slot's combined output every frame. Cleared when
        /// the owning macro is disabled; fresh instances (profile switch / app
        /// restart) start unlatched. Never serialized.</summary>
        [System.Xml.Serialization.XmlIgnore]
        internal bool VcToggleLatched { get; set; }

        /// <summary>Volatile latch bit for <see cref="MacroActionType.ToggleKey"/>
        /// (issue #9 wave 1b). While set, the engine's per-frame key reconcile
        /// holds every parsed key logically down; clearing it (second fire,
        /// macro disable, or instance discard) releases the key on the next
        /// tick. Never serialized.</summary>
        [System.Xml.Serialization.XmlIgnore]
        internal bool KeyToggleLatched { get; set; }

        /// <summary>Volatile latch bit for
        /// <see cref="MacroActionType.ToggleMouseButton"/> (v18). While set,
        /// the engine's per-frame mouse-button reconcile holds
        /// <see cref="MouseButton"/> logically down. Never serialized.</summary>
        [System.Xml.Serialization.XmlIgnore]
        internal bool MouseToggleLatched { get; set; }

        /// <summary>Volatile latch bit for
        /// <see cref="MacroActionType.ToggleVcAxis"/> (v18). While set, the
        /// engine re-writes the axis assert every frame. Never
        /// serialized.</summary>
        [System.Xml.Serialization.XmlIgnore]
        internal bool VcAxisToggleLatched { get; set; }

        /// <summary>Volatile latch bit for
        /// <see cref="MacroActionType.ToggleWheel"/> (v18). While set, the
        /// engine sends one wheel detent per interval. Never
        /// serialized.</summary>
        [System.Xml.Serialization.XmlIgnore]
        internal bool WheelToggleLatched { get; set; }

        private bool _pulseWhileLatched;
        /// <summary>Composes Steam's toggle + hold_repeats (v18): while the
        /// action's latch is engaged, the contribution pulses on the
        /// <see cref="MacroActionType.RepeatVcButtonWhileHeld"/> square
        /// wave (period <see cref="IntervalMs"/>) instead of holding
        /// solid. Serialized; read by ToggleVcButton / ToggleKey /
        /// ToggleVcAxis latch application.</summary>
        public bool PulseWhileLatched
        {
            get => _pulseWhileLatched;
            set
            {
                if (SetProperty(ref _pulseWhileLatched, value))
                {
                    // The Interval row shows for a pulsing latch (v19, M1).
                    OnPropertyChanged(nameof(IsRepeatIntervalType));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        private MacroLatchDirection _latchDirection;
        /// <summary>How a ToggleKey / ToggleMouseButton fire writes the
        /// volatile latch (audit #2 M4): Toggle flips (the classic
        /// behavior and the serialized default), On sets, Off clears.
        /// The Workshop materializer lowers HoldKey / HoldMouseButton
        /// pairs to On on the press leg and Off on the release leg, so
        /// the held key rides the per-frame reconcile and its
        /// engine-stop / profile-switch release paths instead of a raw
        /// KeyPress Down those paths cannot see. Serialized.</summary>
        public MacroLatchDirection LatchDirection
        {
            get => _latchDirection;
            set => SetProperty(ref _latchDirection, value);
        }

        private RelayCommand _resetIntervalMsCommand;
        /// <summary>Resets the turbo interval to the 100 ms default (issue #9
        /// wave 1b), pairing the shared Interval row with the standard reset
        /// glyph.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public RelayCommand ResetIntervalMsCommand =>
            _resetIntervalMsCommand ??= new RelayCommand(() => IntervalMs = 100);

        private System.Windows.Threading.DispatcherTimer _mousePickTimer;
        private int _mousePickCountdown;

        /// <summary>True while a MoveMouseToScreenPosition "Pick on screen" capture
        /// countdown is running (issue #9).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsPickingMousePosition => _mousePickTimer != null;

        /// <summary>Label for the "Pick on screen" button: the live countdown while
        /// capturing, the idle prompt otherwise (issue #9).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public string MousePickButtonText => _mousePickTimer != null
            ? string.Format(Strings.Instance.Macro_PickOnScreen_Countdown_Format, _mousePickCountdown)
            : Strings.Instance.Macro_PickOnScreen;

        private RelayCommand _pickMousePositionCommand;
        /// <summary>Starts a 3-second "pick on screen" countdown (issue #9); when it
        /// elapses, the current desktop cursor position is captured into
        /// <see cref="MouseX"/> / <see cref="MouseY"/>. The delay lets the user move
        /// the cursor onto the target after clicking the button.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public RelayCommand PickMousePositionCommand =>
            _pickMousePositionCommand ??= new RelayCommand(StartMousePositionPick);

        private void StartMousePositionPick()
        {
            if (_mousePickTimer != null) return; // already picking
            _mousePickCountdown = 3;
            _mousePickTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _mousePickTimer.Tick += (_, _) =>
            {
                _mousePickCountdown--;
                if (_mousePickCountdown > 0)
                {
                    OnPropertyChanged(nameof(MousePickButtonText));
                    return;
                }
                _mousePickTimer.Stop();
                _mousePickTimer = null;
                if (CursorControlService.TryGetCursorPosition(out int cx, out int cy))
                {
                    MouseX = cx;
                    MouseY = cy;
                }
                OnPropertyChanged(nameof(IsPickingMousePosition));
                OnPropertyChanged(nameof(MousePickButtonText));
            };
            OnPropertyChanged(nameof(IsPickingMousePosition));
            OnPropertyChanged(nameof(MousePickButtonText));
            _mousePickTimer.Start();
        }

        private CursorClampMode _cursorClampMode = CursorClampMode.XAndY;
        /// <summary>Which axes a <see cref="MacroActionType.MouseLimitRegion"/>
        /// action clamps inside the inset region while engaged (issue #110).</summary>
        public CursorClampMode CursorClampMode
        {
            get => _cursorClampMode;
            set
            {
                if (SetProperty(ref _cursorClampMode, value))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private int _cursorClampInsetX = 50;
        /// <summary>Pixels held back from each left/right edge for a region clamp
        /// (issue #110). The cursor is kept inside [inset, width - inset] on X.
        /// Clamped to 0..half the primary monitor's width.</summary>
        public int CursorClampInsetX
        {
            get => _cursorClampInsetX;
            set => SetProperty(ref _cursorClampInsetX, ClampInsetToHalfWidth(value));
        }

        private int _cursorClampInsetY = 50;
        /// <summary>Pixels held back from each top/bottom edge for a region clamp
        /// (issue #110). The cursor is kept inside [inset, height - inset] on Y.
        /// Clamped to 0..half the primary monitor's height.</summary>
        public int CursorClampInsetY
        {
            get => _cursorClampInsetY;
            set => SetProperty(ref _cursorClampInsetY, ClampInsetToHalfHeight(value));
        }

        private RelayCommand _resetCursorClampInsetXCommand;
        /// <summary>Resets the region-clamp X inset to the 50 px default (#110).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public RelayCommand ResetCursorClampInsetXCommand =>
            _resetCursorClampInsetXCommand ??= new RelayCommand(() => CursorClampInsetX = 50);

        private RelayCommand _resetCursorClampInsetYCommand;
        /// <summary>Resets the region-clamp Y inset to the 50 px default (#110).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public RelayCommand ResetCursorClampInsetYCommand =>
            _resetCursorClampInsetYCommand ??= new RelayCommand(() => CursorClampInsetY = 50);

        // CSV of LightbarMode int values for ModeCycle. Default skips
        // Off and the audio modes. Most users want a quick visual
        // toggle, not silent output. Must match the DTO default in the
        // profile converter so hydration and fresh creation agree.
        private string _lightbarCycleModesCsv = "1,2,3,4,11,12,13";
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

        // CSV of PointerMode names for PointerModeCycle (issue #203).
        // Default cycles all four modes. Names, not ints: the mode is a
        // string family on PadSetting, and names keep the CSV readable.
        private string _pointerCycleModesCsv = "Mouse,FpsMouse,Mouse43,Mouse169";
        /// <summary>CSV of <c>PadSetting.PointerMode</c> names to cycle
        /// through. Each fire advances to the next listed mode. Editor
        /// surfaces this as a four-item checkbox row.</summary>
        public string PointerCycleModesCsv
        {
            get => _pointerCycleModesCsv;
            set
            {
                if (SetProperty(ref _pointerCycleModesCsv, value ?? string.Empty))
                {
                    _pointerCycleIndex = 0;
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        private int _pointerCycleIndex;
        /// <summary>Per-action volatile cycle position for PointerModeCycle.
        /// Resets on action edit and on app restart.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public int PointerCycleIndex
        {
            get => _pointerCycleIndex;
            set => _pointerCycleIndex = value;
        }

        // Target mode for PointerModeSet (issue #203 follow-up), the
        // pointer sibling of LightbarTargetMode. A mode NAME like the
        // cycle CSV, so the settings XML stays readable.
        private string _pointerSetMode = "Mouse";
        /// <summary>The <c>PadSetting.PointerMode</c> name PointerModeSet
        /// applies. Unknown names normalize to Mouse at execution.</summary>
        public string PointerSetMode
        {
            get => _pointerSetMode;
            set
            {
                if (SetProperty(ref _pointerSetMode, value ?? "Mouse"))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        /// <summary>Resolves <see cref="PointerSetMode"/> to a recognized
        /// mode name, defaulting to Mouse for anything unknown, so a hand-
        /// edited XML value can never write garbage into PadSetting.</summary>
        internal string NormalizedPointerSetMode()
        {
            foreach (var known in PointerModeNames)
                if (string.Equals(_pointerSetMode, known, StringComparison.OrdinalIgnoreCase))
                    return known;
            return "Mouse";
        }

        private static readonly string[] PointerModeNames =
            { "Mouse", "FpsMouse", "Mouse43", "Mouse169" };

        // Brightness percent for GuideLedBrightness (discussion #209),
        // the Guide/Home LED sibling of the fixed lightbar parameters.
        private int _guideLedPercent = 100;
        /// <summary>The brightness percent (0-100) the GuideLedBrightness
        /// action applies. 0 turns the LED off.</summary>
        public int GuideLedPercent
        {
            get => _guideLedPercent;
            set
            {
                if (SetProperty(ref _guideLedPercent, Math.Clamp(value, 0, 100)))
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        /// <summary>Parses <see cref="PointerCycleModesCsv"/> into the
        /// recognized mode names, preserving CSV order.</summary>
        public string[] ParsedPointerCycleModes()
        {
            var list = new List<string>(4);
            foreach (var part in (_pointerCycleModesCsv ?? "").Split(','))
            {
                var p = part.Trim();
                if (p.Length == 0) continue;
                foreach (var known in PointerModeNames)
                    if (string.Equals(p, known, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!list.Contains(known)) list.Add(known);
                        break;
                    }
            }
            return list.ToArray();
        }

        internal int CountSelectedPointerCycleModes() => ParsedPointerCycleModes().Length;

        internal void WritePointerCycleCsv(IEnumerable<string> selected)
        {
            // Keep the canonical mode order regardless of click order.
            var set = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
            PointerCycleModesCsv = string.Join(",", PointerModeNames.Where(set.Contains));
        }

        /// <summary>Localized display name for a PointerMode value, shared
        /// by the Pointer tab combo and the cycle checkboxes.</summary>
        internal static string PointerModeDisplayName(string mode) => mode switch
        {
            "FpsMouse" => Strings.Instance.Pad_Pointer_Mode_FpsMouse,
            "Mouse43" => Strings.Instance.Pad_Pointer_Mode_43,
            "Mouse169" => Strings.Instance.Pad_Pointer_Mode_169,
            _ => Strings.Instance.Pad_Pointer_Mode_Mouse,
        };

        private IReadOnlyList<PointerModeCycleOption> _pointerCycleModeOptions;
        /// <summary>Checkbox-bindable projection of the four pointer modes
        /// for the PointerModeCycle editor, mirroring
        /// <see cref="CycleModeOptions"/>: the CSV is canonical, labels
        /// resolve live for culture changes.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public IReadOnlyList<PointerModeCycleOption> PointerCycleModeOptions
        {
            get
            {
                _pointerCycleModeOptions ??= PointerModeNames
                    .Select(m => new PointerModeCycleOption(this, m))
                    .ToList()
                    .AsReadOnly();
                return _pointerCycleModeOptions;
            }
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
                    ? MacroButtonNames.FormatCustomButtons(_customButtonWords, _extendedProfileId)
                    : MacroButtonNames.FormatButtons(_buttonFlags, _buttonStyle, _extendedProfileId);
                string axisLabel = _axisSource == MacroAxisSource.InputDevice
                    ? string.Format(Strings.Instance.Macro_DeviceAxis_Format, _sourceDeviceAxisIndex)
                    : _axisTarget.DisplayName();
                // Yield marker for the axis holds (discussion #237). Only
                // the three yield-capable arms append it.
                string yieldMark = _axisYieldToPhysical
                    ? " " + Strings.Instance.MacroAction_YieldSuffix
                    : string.Empty;
                return _type switch
                {
                    MacroActionType.ButtonPress => string.Format(Strings.Instance.MacroAction_Press_Format, btnText, _durationMs),
                    MacroActionType.ButtonRelease => string.Format(Strings.Instance.MacroAction_Release_Format, btnText),
                    MacroActionType.KeyPress => string.Format(Strings.Instance.MacroAction_KeyPress_Format, keyDisplay, _durationMs),
                    MacroActionType.KeyRelease => string.Format(Strings.Instance.MacroAction_KeyRelease_Format, keyDisplay),
                    MacroActionType.Delay => string.Format(Strings.Instance.MacroAction_Wait_Format, _durationMs),
                    MacroActionType.AxisSet => string.Format(Strings.Instance.MacroAction_SetAxis_Format, _axisTarget.DisplayName(), _axisValue),
                    MacroActionType.AxisHold => string.Format(Strings.Instance.MacroAction_HoldAxis_Format, _axisTarget.DisplayName(), _axisValue, _durationMs) + yieldMark,
                    MacroActionType.MouseWheelTap => Strings.Instance.MacroAction_Type_MouseWheelTap,
                    MacroActionType.MouseNudge => string.Format(
                        Strings.Instance.MacroAction_MouseNudge_Format, _nudgeDx, _nudgeDy),
                    MacroActionType.CycleTapList => string.Format(
                        Strings.Instance.MacroAction_CycleTapList_Format, ParsedCycleSteps.Length),
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
                    MacroActionType.PointerModeCycle => string.Format(
                        Strings.Instance.MacroAction_PointerModeCycle_Format,
                        CountSelectedPointerCycleModes()),
                    MacroActionType.PointerModeSet => string.Format(
                        Strings.Instance.MacroAction_PointerModeSet_Format,
                        PointerModeDisplayName(NormalizedPointerSetMode())),
                    MacroActionType.GuideLedBrightness => string.Format(
                        Strings.Instance.MacroAction_GuideLedBrightness_Format,
                        _guideLedPercent),
                    MacroActionType.Rumble => FormatRumbleSummary(),
                    MacroActionType.RumbleStop => Strings.Instance.MacroAction_RumbleStop,
                    MacroActionType.RumbleTrigger => FormatRumbleTriggerSummary(),
                    MacroActionType.RumbleTriggerStop => Strings.Instance.MacroAction_RumbleTriggerStop,
                    MacroActionType.PlaySound => string.IsNullOrEmpty(_soundFilePath)
                        ? Strings.Instance.MacroAction_Type_PlaySound
                        : string.Format(
                            _soundLoop ? Strings.Instance.MacroAction_PlaySoundLoop_Format
                                       : Strings.Instance.MacroAction_PlaySound_Format,
                            SoundFileName, _soundVolume),
                    MacroActionType.SoundStop => Strings.Instance.MacroAction_Type_SoundStop,
                    MacroActionType.HeadphoneVolumeUp => Strings.Instance.MacroAction_Type_HeadphoneVolumeUp,
                    MacroActionType.HeadphoneVolumeDown => Strings.Instance.MacroAction_Type_HeadphoneVolumeDown,
                    MacroActionType.SetGyroEngaged => string.Format(
                        Strings.Instance.MacroAction_SetGyroEngaged_Format,
                        SetGyroEngagedModeDisplayName(_setGyroEngagedMode)),
                    MacroActionType.MouseRecenter => string.Format(
                        Strings.Instance.MacroAction_MouseRecenter_Format,
                        CursorRecenterModeDisplayName(_cursorRecenterMode)),
                    MacroActionType.MouseFixPosition => string.Format(
                        Strings.Instance.MacroAction_MouseFixPosition_Format,
                        CursorPinModeDisplayName(_cursorPinMode)),
                    MacroActionType.MouseLimitRegion => string.Format(
                        Strings.Instance.MacroAction_MouseLimitRegion_Format,
                        CursorClampModeDisplayName(_cursorClampMode)),
                    MacroActionType.MoveMouseToScreenPosition => string.Format(
                        Strings.Instance.MacroAction_MoveMouseToScreenPosition_Format,
                        _mouseX, _mouseY),
                    MacroActionType.RepeatKeyWhileHeld => string.Format(
                        Strings.Instance.MacroAction_RepeatKeyWhileHeld_Format,
                        keyDisplay, _intervalMs),
                    MacroActionType.RepeatVcButtonWhileHeld => string.Format(
                        Strings.Instance.MacroAction_RepeatVcButtonWhileHeld_Format,
                        btnText, _intervalMs),
                    MacroActionType.ToggleVcButton => string.Format(
                        Strings.Instance.MacroAction_ToggleVcButton_Format,
                        btnText),
                    MacroActionType.ToggleKey => string.Format(
                        Strings.Instance.MacroAction_ToggleKey_Format,
                        keyDisplay),
                    MacroActionType.GyroRecenter =>
                        Strings.Instance.MacroAction_Type_GyroRecenter,
                    MacroActionType.DisconnectController => string.Format(
                        Strings.Instance.MacroAction_DisconnectController_Format,
                        DisconnectTargetDisplayName()),
                    MacroActionType.RunProgram => string.Format(
                        Strings.Instance.MacroAction_RunProgram_Format,
                        ProgramDisplayName()),
                    MacroActionType.TextBlock => string.Format(
                        Strings.Instance.MacroAction_TextBlock_Format,
                        TextBlockDisplayName()),
                    // v19 (M1): the v18 latch / turbo family renders its
                    // target instead of falling to the unknown label.
                    MacroActionType.ToggleMouseButton => string.Format(
                        Strings.Instance.MacroAction_ToggleMouseButton_Format,
                        MacroMouseButtonDisplayName(_mouseButton)),
                    MacroActionType.ToggleVcAxis => string.Format(
                        Strings.Instance.MacroAction_ToggleVcAxis_Format,
                        _axisTarget.DisplayName(), _axisValue) + yieldMark,
                    MacroActionType.RepeatVcAxisWhileHeld => string.Format(
                        Strings.Instance.MacroAction_RepeatVcAxisWhileHeld_Format,
                        _axisTarget.DisplayName(), _axisValue, _intervalMs) + yieldMark,
                    MacroActionType.ToggleWheel => string.Format(
                        Strings.Instance.MacroAction_ToggleWheel_Format,
                        _axisValue, _intervalMs),
                    // Discussion #237: the relative add renders its signed
                    // percent of the pull scale, the combo break its type
                    // label (it carries no parameters).
                    MacroActionType.AxisAdd => string.Format(
                        Strings.Instance.MacroAction_AxisAdd_Format,
                        _axisTarget.DisplayName(), FormatSignedPercent(_axisValue)),
                    MacroActionType.AxisSetLatched => string.Format(
                        Strings.Instance.MacroAction_AxisSetLatched_Format,
                        _axisTarget.DisplayName(), FormatSignedPercent(_axisValue)),
                    MacroActionType.AxisLatchRelease => string.Format(
                        Strings.Instance.MacroAction_AxisLatchRelease_Format,
                        _axisTarget == MacroAxisTarget.None
                            ? Strings.Instance.MacroAction_AllAxes
                            : _axisTarget.DisplayName()),
                    MacroActionType.AxisScale => string.Format(
                        Strings.Instance.MacroAction_AxisScale_Format,
                        _axisTarget.DisplayName(), FormatSignedPercent(_axisValue)),
                    MacroActionType.ComboBreak =>
                        Strings.Instance.MacroAction_Type_ComboBreak,
                    _ => Strings.Instance.Macro_UnknownAction
                };
            }
        }

        /// <summary>Signed percent of the pull scale for the Axis Add
        /// summary (discussion #237): +32767 renders "+100" and -16384
        /// renders "-50". The plus sign is explicit because the add
        /// direction is the point.</summary>
        private static string FormatSignedPercent(short value)
        {
            int pct = (int)Math.Round(value * 100.0 / 32767.0);
            return pct >= 0
                ? "+" + pct.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : pct.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Short label for the Run Program summary: the program's file name
        /// when a path is set, otherwise a neutral placeholder.</summary>
        private string ProgramDisplayName()
        {
            if (string.IsNullOrWhiteSpace(_programPath))
                return Strings.Instance.MacroAction_RunProgram_NoProgram;
            try { return System.IO.Path.GetFileName(_programPath.Trim()); }
            catch { return _programPath.Trim(); }
        }

        /// <summary>Short label for the Text Block summary: the text's first line,
        /// capped at 24 characters with an ellipsis, or a neutral placeholder when
        /// the action holds no text yet.</summary>
        private string TextBlockDisplayName()
        {
            if (string.IsNullOrWhiteSpace(_textContent))
                return Strings.Instance.MacroAction_TextBlock_NoText;
            string s = _textContent;
            bool truncated = false;
            int nl = s.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) { s = s.Substring(0, nl); truncated = true; }
            if (s.Length > 24)
            {
                // Never cut a surrogate pair in half mid-label.
                int cut = char.IsHighSurrogate(s[23]) ? 23 : 24;
                s = s.Substring(0, cut);
                truncated = true;
            }
            s = s.TrimEnd();
            return truncated ? s + "…" : s;
        }

        /// <summary>Display label for the Disconnect action's current target:
        /// the picked device's name in Specific mode, the mode label otherwise.</summary>
        private string DisconnectTargetDisplayName()
        {
            switch (_disconnectTarget)
            {
                case MacroDisconnectTarget.SpecificDevice:
                    return SettingsManager.FindDeviceByInstanceGuid(_disconnectDeviceGuid)?.ResolvedName
                           ?? Strings.Instance.MacroDisconnect_SpecificDevice;
                case MacroDisconnectTarget.SlotDevices:
                    return Strings.Instance.MacroDisconnect_SlotDevices;
                case MacroDisconnectTarget.AllDevices:
                    return Strings.Instance.MacroDisconnect_AllDevices;
                case MacroDisconnectTarget.TriggeringDevice:
                default:
                    return Strings.Instance.MacroDisconnect_TriggeringDevice;
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

        /// <summary>Trigger-channel sibling of <see cref="FormatRumbleSummary"/>
        /// (issue #102). Uses LT / RT trigger codes so the list row reads
        /// distinctly from a main-motor Rumble action, reusing the same hold-mode
        /// format strings (no separate translation needed).</summary>
        private string FormatRumbleTriggerSummary()
        {
            string triggers = $"LT{_rumbleStrengthLeft}/RT{_rumbleStrengthRight}";
            return _rumbleHoldMode == MacroRumbleHoldMode.Sticky
                ? string.Format(Strings.Instance.MacroAction_Rumble_Sticky_Format, triggers)
                : string.Format(Strings.Instance.MacroAction_Rumble_Reactive_Format, triggers, _rumbleHoldMs + _rumbleFadeMs);
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
                LightbarMode.PlayerNumber       => s.Pad_Lighting_Mode_PlayerNumber,
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
                LightbarMode.Battery            => s.Pad_Lighting_Mode_Battery,
                LightbarMode.Strobe             => s.Pad_Lighting_Mode_Strobe,
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

        /// <summary>Axis symbol for the cursor recenter mode (#108). Universal
        /// symbols, not localized.</summary>
        private static string CursorRecenterModeDisplayName(CursorRecenterMode mode) => mode switch
        {
            CursorRecenterMode.XOnly => "X",
            CursorRecenterMode.YOnly => "Y",
            CursorRecenterMode.XAndY => "X+Y",
            _ => mode.ToString()
        };

        /// <summary>Axis symbol for the cursor pin mode (#109). Universal
        /// symbols, not localized.</summary>
        private static string CursorPinModeDisplayName(CursorPinMode mode) => mode switch
        {
            CursorPinMode.XOnly => "X",
            CursorPinMode.YOnly => "Y",
            CursorPinMode.XAndY => "X+Y",
            _ => mode.ToString()
        };

        /// <summary>Axis symbol for the cursor clamp mode (#110). Universal
        /// symbols, not localized.</summary>
        private static string CursorClampModeDisplayName(CursorClampMode mode) => mode switch
        {
            CursorClampMode.XOnly => "X",
            CursorClampMode.YOnly => "Y",
            CursorClampMode.XAndY => "X+Y",
            _ => mode.ToString()
        };

        // Coordinate / inset clamps for the pin (#109) and region (#110) actions.
        // Resolve the primary monitor on demand so an off-screen coord can never be
        // saved. If the monitor can't be read, the raw value passes through (a later
        // tick re-clamps at write time anyway).
        private static int ClampToPrimaryWidth(int value)
        {
            if (value < 0) return 0;
            if (CursorControlService.TryGetPrimarySize(out int w, out _) && value > w) return w;
            return value;
        }

        private static int ClampToPrimaryHeight(int value)
        {
            if (value < 0) return 0;
            if (CursorControlService.TryGetPrimarySize(out _, out int h) && value > h) return h;
            return value;
        }

        private static int ClampInsetToHalfWidth(int value)
        {
            if (value < 0) return 0;
            if (CursorControlService.TryGetPrimarySize(out int w, out _) && value > w / 2) return w / 2;
            return value;
        }

        private static int ClampInsetToHalfHeight(int value)
        {
            if (value < 0) return 0;
            if (CursorControlService.TryGetPrimarySize(out _, out int h) && value > h / 2) return h / 2;
            return value;
        }
    }

    // ─────────────────────────────────────────────
    //  Enums
    // ─────────────────────────────────────────────

    // APPEND-ONLY: like MacroActionType below, this enum rides the macro
    // clipboard (MacroData.TriggerMode inside SerializeMacrosToClipboard's
    // System.Text.Json envelope), which serializes it NUMERICALLY. Inserting
    // a member re-meanings previously copied payloads. New members go at the
    // end with pinned ordinals.
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
        CustomExpression,

        /// <summary>Fire once when the trigger combo has been held continuously
        /// for <see cref="MacroItem.TriggerHoldMs"/> milliseconds (issue #9
        /// wave 1b, B-8b: the Steam Input Long_Press activator). A shorter
        /// tap does nothing. Release re-arms, so the next qualifying hold
        /// fires again. At the tail per the APPEND-ONLY rule above; ordinal
        /// pinned.</summary>
        HoldForMs = 5,

        /// <summary>Fire once when the trigger combo sees press, release,
        /// press within <see cref="MacroItem.TriggerDoublePressMs"/>
        /// milliseconds (translator v17: the Steam Input Double_Press
        /// activator, whose window key is double_tap_time in the
        /// serializer's own token table). A single press, or a second
        /// press outside the window, only re-arms as a fresh first press.
        /// The trigger stays active through the second press's hold, so
        /// UntilRelease shapes stop on its release ("If held on the second
        /// press, it will remain pressed", Valve's shipped Double Press
        /// string). At the tail per the APPEND-ONLY rule above; ordinal
        /// pinned.</summary>
        DoublePress = 6,

        /// <summary>Fire once on the THIRD rising edge of a press chain
        /// whose successive presses each land within
        /// <see cref="MacroItem.TriggerDoublePressMs"/> of the previous
        /// one (discussion #238, the reWASD / Steam "Triple Press"
        /// activation mode). A slower press re-arms as a fresh first
        /// press, and the chain is consumed on fire so six fast taps fire
        /// twice, never four times. The trigger reads active through the
        /// third press's hold, the DoublePress UntilRelease contract. At
        /// the tail per the APPEND-ONLY rule above; ordinal pinned.</summary>
        TriplePress = 7,

        /// <summary>Fire once when a press is NOT followed by a second
        /// press within <see cref="MacroItem.TriggerDoublePressMs"/>
        /// (discussion #238, reWASD's "Single Press" as distinct from
        /// "Start Press"). The DEFERRED counterpart of
        /// <see cref="OnPress"/>: OnPress fires the instant the button
        /// goes down (Start Press), SinglePress waits out the press
        /// window so it can share a button with a DoublePress or
        /// TriplePress macro without firing on their chains. A chain of
        /// two or more fast presses fires nothing here. The fire lands
        /// at window expiry whether the button is still held (hold
        /// shapes keep working) or already released. At the tail per
        /// the APPEND-ONLY rule above; ordinal pinned.</summary>
        SinglePress = 8,

        /// <summary>Latching trigger (discussion #238, the reWASD / Steam
        /// "Toggle" activation mode): the first press latches the trigger
        /// ACTIVE, the next press releases it. Downstream the macro
        /// evaluates exactly like <see cref="WhileHeld"/> against the
        /// latch, and the release-stop applies on unlatch regardless of
        /// RepeatMode, so holds and repeats stay active until the button
        /// is pressed again. Disabling the macro or closing its shift
        /// layer clears the latch. At the tail per the APPEND-ONLY rule
        /// above; ordinal pinned.</summary>
        Toggle = 9,

        /// <summary>Repeat-while-held (discussion #238, the classic
        /// "Turbo" activation mode): the sequence re-runs at
        /// <see cref="MacroItem.RepeatDelayMs"/> for as long as the
        /// trigger is held, and stops on release regardless of
        /// RepeatMode. The WhileHeld + Until Release composition as one
        /// first-class mode, with the interval surfaced beside the Fire
        /// picker. At the tail per the APPEND-ONLY rule above; ordinal
        /// pinned.</summary>
        Turbo = 10,

        /// <summary>#253 "On Short Press": fires once at RELEASE, only when
        /// the continuous hold stayed UNDER <see cref="MacroItem.TriggerHoldMs"/>.
        /// The exact twin of <see cref="HoldForMs"/> ("On Long Press"),
        /// sharing its threshold, so the pair composes tap-vs-hold on one
        /// button. At the tail per the APPEND-ONLY rule above; ordinal
        /// pinned.</summary>
        ShortPress = 11
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

    // APPEND-ONLY: the macro clipboard leg (SettingsService.SerializeMacrosToClipboard)
    // writes this enum NUMERICALLY via System.Text.Json defaults, so inserting a member
    // re-meanings every previously copied clipboard payload. The settings XML writes
    // names and is insertion-safe; the clipboard is not. New members go at the end.
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

        /// <summary>Drives the slot's macro TRIGGER override (issue #102),
        /// the sibling of <see cref="Rumble"/> for the trigger channel.
        /// Reuses the same <c>RumbleStrengthLeft</c> / <c>RumbleStrengthRight</c>
        /// / hold-mode / <c>RumbleHoldMs</c> / <c>RumbleFadeMs</c> fields, but
        /// its scalar output max-combines into the trigger channel (Xbox impulse
        /// triggers and DualSense AT Vibration) alongside the XInput/FFB → trigger
        /// routing pass instead of the grip motors.</summary>
        RumbleTrigger,

        /// <summary>Releases any active macro trigger override on the slot
        /// (issue #102). Sibling of <see cref="RumbleStop"/> for the trigger
        /// channel; pair with a Sticky <see cref="RumbleTrigger"/>.</summary>
        RumbleTriggerStop,

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
        SoundStop,

        /// <summary>Recenters the desktop cursor on press (issue #108). The
        /// <see cref="MacroItem.CursorRecenterMode"/> picks which axes snap to the
        /// primary-monitor center (X only / Y only / both). One press, one cursor
        /// write, no continuous evaluation or release behavior. Pairs with the #107
        /// "Mouse Position X/Y" sources so a button can re-zero the mapped stick.</summary>
        MouseRecenter,

        /// <summary>Toggles a sticky cursor pin on press (issue #109). While
        /// engaged, the <see cref="CursorControlService"/> writes the cursor to
        /// <see cref="MacroItem.CursorPinX"/> / <c>CursorPinY</c> on every 200 Hz
        /// tick before sampling, so the mapped "Mouse Position" source reads the
        /// pinned coord. <see cref="MacroItem.CursorPinMode"/> picks which axes are
        /// held. A second press releases the pin.</summary>
        MouseFixPosition,

        /// <summary>Toggles a cursor region clamp on press (issue #110). While
        /// engaged, the <see cref="CursorControlService"/> keeps the cursor inside
        /// the inset rectangle every 200 Hz tick, writing only when a clamped axis
        /// is outside. <see cref="MacroItem.CursorClampMode"/> picks which axes are
        /// clamped, and <c>CursorClampInsetX</c> / <c>CursorClampInsetY</c> set the
        /// margin held back from each edge. A second press releases the clamp.</summary>
        MouseLimitRegion,

        /// <summary>Disconnects a Bluetooth controller so it sleeps (issue #162),
        /// the DS4Windows "Disconnect BT" special action. The host radio drops
        /// the link via IOCTL_BTH_DISCONNECT_DEVICE; there is no per-family
        /// power-off command. <see cref="MacroAction.DisconnectTarget"/> picks
        /// the victim: the device(s) whose inputs form this macro's trigger,
        /// one specific device (<see cref="MacroAction.DisconnectDeviceGuid"/>),
        /// every Bluetooth device on this pad's slot, or every Bluetooth device
        /// PadForge knows. Skips devices that are charging or not on a
        /// Bluetooth path.</summary>
        DisconnectController,

        /// <summary>Launches an external program or file (user request) with optional
        /// command-line arguments and a working folder. Fire-and-forget via
        /// ShellExecute on a background thread, so the macro sequence never blocks on
        /// the launch. Running arbitrary programs is the user's responsibility.</summary>
        RunProgram,

        /// <summary>Types out plain text (issue #201, the LGS "Text Block"). Delivered
        /// as Unicode keyboard injection (SendInput with KEYEVENTF_UNICODE, the
        /// AutoHotkey SendText mechanism), so it is layout-independent and types any
        /// text including accents, CJK, and emoji. Newlines press Enter and tabs press
        /// Tab. <see cref="MacroAction.TextPerCharDelayMs"/> paces the emission; 0
        /// types the whole text in one batched call. Key Press remains the tool for
        /// key combos.</summary>
        TextBlock,

        /// <summary>Cycles the Wii pointer mode (issue #203) through a
        /// user-selected subset of modes for every IR-capable device on
        /// the slot. Each fire advances to the next checked mode. Cycle
        /// position is per-action and volatile, resetting on app
        /// restart. At the tail per the APPEND-ONLY rule above.</summary>
        PointerModeCycle,

        /// <summary>Sets the Wii pointer mode (issue #203 follow-up) to one
        /// fixed mode for every IR-capable device on the slot, the direct
        /// sibling of <see cref="LightbarModeSet"/> beside its cycle. At
        /// the tail per the APPEND-ONLY rule above.</summary>
        PointerModeSet,

        /// <summary>Sets the Guide/Home button LED brightness (discussion
        /// #209) for every capable controller on the slot: Xbox One and
        /// later pads over USB (the \\.\XboxGIP lane) and the 2015 Steam
        /// Controller (SDL home-LED hint). One transient write per fire,
        /// nothing persisted, so a pair of macros can flash the LED when a
        /// mode engages and restore it after. The Lighting tab's Battery
        /// mode reasserts on its next cadence. At the tail per the
        /// APPEND-ONLY rule above.</summary>
        GuideLedBrightness,

        /// <summary>Warps the desktop cursor to a fixed primary-monitor pixel
        /// (issue #9). One press, one <c>SetCursorPos</c> write via
        /// <see cref="CursorControlService"/>. <see cref="MacroAction.MouseX"/> /
        /// <c>MouseY</c> hold the target, seeded to the primary-monitor center
        /// on first type switch and pickable on screen from the editor. At the
        /// tail per the APPEND-ONLY rule above.</summary>
        MoveMouseToScreenPosition,

        /// <summary>Turbo / autofire for a keyboard key (issue #9). While the
        /// macro trigger is held, fires a full KeyDown+KeyUp pulse for
        /// <see cref="MacroAction.KeyCode"/> every
        /// <see cref="MacroAction.IntervalMs"/> milliseconds (10..1000, default
        /// 100). A continuous action, so a macro whose only action is this one
        /// keeps pulsing until release. At the tail per the APPEND-ONLY rule
        /// above.</summary>
        RepeatKeyWhileHeld = 34,

        /// <summary>Turbo / autofire for a virtual controller button (issue
        /// #9 wave 1b). While the macro trigger is held, pulses the target
        /// button(s) on and off as a 50 % duty-cycle square wave with period
        /// <see cref="MacroAction.IntervalMs"/> (the same 10..1000 ms knob
        /// <see cref="RepeatKeyWhileHeld"/> uses). The target rides the same
        /// <see cref="MacroAction.ButtonFlags"/> (Xbox bitmask) /
        /// <see cref="MacroAction.CustomButtonWords"/> (Extended) pair
        /// <see cref="ButtonPress"/> addresses a slot button with, and the
        /// pulse ORs into the slot's combined output the same way ButtonPress
        /// does. A continuous action. At the tail per the APPEND-ONLY rule
        /// above; ordinal pinned.</summary>
        RepeatVcButtonWhileHeld = 35,

        /// <summary>Latch / unlatch a virtual controller button (issue #9
        /// wave 1b). Each fire flips the action's volatile latch bit; while
        /// latched, the target button(s) (<see cref="MacroAction.ButtonFlags"/>
        /// / <see cref="MacroAction.CustomButtonWords"/>, the ButtonPress
        /// addressing pair) are OR-written into the slot's combined output
        /// every frame, independent of the macro's execution state. Latch is
        /// per-action volatile: cleared when the macro is disabled and on
        /// profile switch / app restart. At the tail per the APPEND-ONLY rule
        /// above; ordinal pinned.</summary>
        ToggleVcButton = 36,

        /// <summary>Latch / unlatch a keyboard key (issue #9 wave 1b). Each
        /// fire flips the action's volatile latch bit; while latched, every
        /// key in <see cref="MacroAction.ParsedKeyCodes"/> is held logically
        /// down via SendInput (the engine reconciles the desired set per
        /// frame, sending one KeyDown when the latch engages and one KeyUp
        /// when it releases, the macro disables, or the macro is removed).
        /// Latch is per-action volatile like <see cref="ToggleVcButton"/>.
        /// At the tail per the APPEND-ONLY rule above; ordinal pinned.</summary>
        ToggleKey = 37,

        /// <summary>Re-references the slot's gyro-aim state (issue #9 wave
        /// 1b, B-18). One fire zeroes every accumulated gyro reference the
        /// aim path holds for the slot: the dual-threshold smoothing window
        /// and EMA rate history (SourceCoercion), the captured MotionLean
        /// neutral orientation (SourceKindRuntime, re-captured from the next
        /// real gravity sample), and the per-device gravity estimator
        /// (re-seeded from the instantaneous accelerometer reading via
        /// <c>InputManager.GyroRecenterApply</c>), so Player/World-space
        /// projection and lean steering re-reference the controller's
        /// CURRENT pose. At the tail per the APPEND-ONLY rule above;
        /// ordinal pinned.</summary>
        GyroRecenter = 38,

        /// <summary>Timed axis assert (translator v15). Writes
        /// <see cref="MacroAction.AxisValue"/> to
        /// <see cref="MacroAction.AxisTarget"/> EVERY frame while the action
        /// is current, advancing only when <see cref="MacroAction.DurationMs"/>
        /// elapses, mirroring how ButtonPress keeps its flags asserted per
        /// frame. The hold-until-release form rides
        /// RepeatMode=UntilRelease + RepeatDelayMs=0, the HoldVcButton
        /// lowering shape. Trigger targets read AxisValue on the pull scale
        /// (0..32767 = 0..100%, doubled onto the 0..65535 output), so a
        /// full pull IS reachable; sticks keep the signed short frame
        /// AxisSet uses. At the tail per the APPEND-ONLY rule above;
        /// ordinal pinned.</summary>
        AxisHold = 39,

        /// <summary>One discrete mouse-wheel detent per fire (translator
        /// v15): a single WHEEL_DELTA SendInput tick.
        /// <see cref="MacroAction.AxisValue"/> is the signed tick count
        /// (positive = up / right, 0 reads as +1);
        /// <see cref="MacroAction.WheelHorizontal"/> selects the
        /// MOUSEEVENTF_HWHEEL lane. At the tail per the APPEND-ONLY rule
        /// above; ordinal pinned.</summary>
        MouseWheelTap = 40,

        /// <summary>One fixed-pixel cursor nudge per fire (translator v16,
        /// Steam's <c>mouse_delta</c> "Move by Amount").
        /// <see cref="MacroAction.NudgeDx"/> / <see cref="MacroAction.NudgeDy"/>
        /// are signed screen-frame pixels (+x right, +y down), enqueued
        /// ONCE into the same accumulate-and-flush mouse lane the
        /// continuous MouseMove action feeds, so the injector thread
        /// batches it off the poll thread. At the tail per the APPEND-ONLY
        /// rule above; ordinal pinned.</summary>
        MouseNudge = 41,

        /// <summary>Cycle through a list of one-shot taps (translator v16,
        /// Steam's Scroll Wheel List): each fire executes the NEXT step of
        /// <see cref="MacroAction.CycleStepsCsv"/> and advances the
        /// per-action volatile <see cref="MacroAction.CycleIndex"/>,
        /// wrapping when <see cref="MacroAction.CycleWrap"/> is set and
        /// dead-ending past the last step otherwise. Step vocabulary in
        /// the CSV doc. At the tail per the APPEND-ONLY rule above;
        /// ordinal pinned.</summary>
        CycleTapList = 42,

        /// <summary>Latch / unlatch a mouse button (translator v18, Steam's
        /// activator toggle on a mouse_button binding). Each fire flips the
        /// action's volatile latch; the engine reconciles a desired
        /// mouse-button set per frame exactly like <see cref="ToggleKey"/>
        /// (one down on engage, one up on release / disable / removal /
        /// engine stop). <see cref="MacroAction.MouseButton"/> picks the
        /// button. At the tail per the APPEND-ONLY rule above; ordinal
        /// pinned.</summary>
        ToggleMouseButton = 43,

        /// <summary>Latch / unlatch an axis-natured VC target (translator
        /// v18, Steam's toggle on a trigger-pull / stick-direction
        /// binding). While latched, <see cref="MacroAction.AxisValue"/> is
        /// re-written to <see cref="MacroAction.AxisTarget"/> every frame,
        /// the <see cref="AxisHold"/> shape driven by a latch instead of a
        /// duration. At the tail per the APPEND-ONLY rule above; ordinal
        /// pinned.</summary>
        ToggleVcAxis = 44,

        /// <summary>Turbo / autofire for an axis-natured VC target
        /// (translator v18, Steam's hold_repeats on a trigger-pull /
        /// stick-direction binding). While the macro trigger is held,
        /// asserts <see cref="MacroAction.AxisTarget"/> on the ON half of
        /// the <see cref="RepeatVcButtonWhileHeld"/> square wave. A
        /// continuous action. At the tail per the APPEND-ONLY rule above;
        /// ordinal pinned.</summary>
        RepeatVcAxisWhileHeld = 45,

        /// <summary>Latch / unlatch a continuous wheel scroll (translator
        /// v18, Steam's toggle on a mouse_wheel binding). While latched,
        /// sends one <see cref="MouseWheelTap"/>-shaped detent every
        /// <see cref="MacroAction.IntervalMs"/> ms, reproducing the held
        /// KbmScroll row's continuous scroll. At the tail per the
        /// APPEND-ONLY rule above; ordinal pinned.</summary>
        ToggleWheel = 46,

        /// <summary>Relative axis deflection (discussion #237, reWASD's
        /// "Axis Control: Relative deflection"). ADDS
        /// <see cref="MacroAction.AxisValue"/> to whatever the mapping
        /// pipeline already wrote to <see cref="MacroAction.AxisTarget"/>
        /// this frame, clamped to the target's range, re-applied every
        /// frame while the action is current (the <see cref="AxisHold"/>
        /// duration shape). Negative values subtract, so a held -50%
        /// on a stick axis turns a run into a walk while the physical
        /// stick keeps steering. Trigger targets add on the pull scale
        /// (0..32767 = 0..100%); sticks add in the signed short frame.
        /// At the tail per the APPEND-ONLY rule above; ordinal pinned.</summary>
        AxisAdd = 47,

        /// <summary>Combo break (discussion #237, reWASD's "Combo break"
        /// block). Divides the action list into parts: reaching the break
        /// ends this fire and parks the sequence, and the NEXT trigger
        /// press resumes from the action after the break. Completing the
        /// final action (or an UntilRelease stop) re-arms from the top.
        /// Hold-shaped triggers must be RELEASED and pressed again to
        /// continue (a WhileHeld trigger never auto-resumes through a
        /// break). Park position is volatile: it resets on app restart,
        /// profile switch, and macro disable (idle and engine restarts
        /// within a session deliberately preserve it, so a combo does
        /// not lose its place because the engine napped). At the tail
        /// per the APPEND-ONLY rule above; ordinal pinned.</summary>
        ComboBreak = 48,

        /// <summary>Set Axis (Latched) (discussion #237 use case 1, issue
        /// #251): latches the axis at the value PERSISTENTLY, surviving
        /// combo-break parks (the latch pass runs independent of
        /// execution). Firing any Set Axis (Latched) step clears its
        /// sibling steps on the same axis first, so a ladder of steps
        /// separated by Combo Breaks REPLACES the value press by press
        /// instead of flipping like Toggle Axis (whose second lap
        /// unlatches). Honors Yield to physical. At the tail per the
        /// APPEND-ONLY rule above; ordinal pinned.</summary>
        AxisSetLatched = 49,

        /// <summary>Release Axis Latches (discussion #237's "other key can
        /// nullify", issue #251): clears every axis latch (Set Axis
        /// (Latched) steps and Toggle Axis latches) for the chosen axis,
        /// or ALL axes when the target is None, across all the slot's
        /// macros, returning the axis to physical control. Latching zero
        /// is not the same thing: that would force zero over the physical
        /// input. At the tail; ordinal pinned.</summary>
        AxisLatchRelease = 50,

        /// <summary>Scale Axis (discussion #237 use case 2, issue #251):
        /// proportional deflection. Multiplies the axis's current combined
        /// value by (1 + value/32767) every frame while current, the
        /// AxisHold duration shape: -50% halves the stick (run becomes
        /// walk), +50% amplifies half again, clamped to full scale. No
        /// yield gate, because proportional composes with the physical
        /// input by construction. At the tail; ordinal pinned.</summary>
        AxisScale = 51,

        /// <summary>Raises the slot's headphone jack hardware volume
        /// (DeviceSlotConfig.HeadphoneVolume) by 10%, clamped at 100.
        /// Persists like any other Audio-tab edit.</summary>
        HeadphoneVolumeUp = 52,

        /// <summary>Lowers the slot's headphone jack hardware volume
        /// by 10%, clamped at 0.</summary>
        HeadphoneVolumeDown = 53
    }

    /// <summary>One parsed part of a <see cref="MacroActionType.CycleTapList"/>
    /// step (v16). Kinds: 'K' key tap (Value = VK), 'M' mouse-button click
    /// (Value = 0..4), 'W' vertical wheel ticks, 'H' horizontal wheel
    /// ticks, 'B' VC button flags (Value = Xbox bitmask), 'A' VC axis
    /// assert (Value = MacroAxisTarget ordinal, Value2 = axis value on the
    /// AxisHold scale).</summary>
    public readonly struct CycleStepPart
    {
        public CycleStepPart(char kind, int value, short value2 = 0)
        {
            Kind = kind;
            Value = value;
            Value2 = value2;
        }

        public char Kind { get; }

        public int Value { get; }

        public short Value2 { get; }

        /// <summary>Parses "K:49", "B:4096", "A:2:32767" forms. Unknown
        /// kinds and junk numbers fail rather than guessing.</summary>
        public static bool TryParse(string text, out CycleStepPart part)
        {
            part = default;
            var tokens = (text ?? "").Trim().Split(':');
            if (tokens.Length < 2 || tokens[0].Length != 1) return false;
            char kind = char.ToUpperInvariant(tokens[0][0]);
            if (!int.TryParse(tokens[1], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int value))
            {
                return false;
            }
            switch (kind)
            {
                case 'K':
                case 'M':
                case 'W':
                case 'H':
                case 'B':
                    if (tokens.Length != 2) return false;
                    part = new CycleStepPart(kind, value);
                    return true;
                case 'A':
                    if (tokens.Length != 3
                        || !short.TryParse(tokens[2], System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out short axisValue))
                    {
                        return false;
                    }
                    part = new CycleStepPart(kind, value, axisValue);
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>Target selector for <see cref="MacroActionType.DisconnectController"/>
    /// (issue #162). Explicit on the action because the trigger alone cannot
    /// express "turn off device X from device Y".</summary>
    public enum MacroDisconnectTarget
    {
        /// <summary>The device(s) named by this macro's trigger entries. No-ops
        /// when the trigger names no device (Always / expression triggers).</summary>
        TriggeringDevice = 0,
        /// <summary>The device picked in <see cref="MacroAction.DisconnectDeviceGuid"/>.</summary>
        SpecificDevice = 1,
        /// <summary>Every Bluetooth-pathed device mapped to this macro's pad slot.</summary>
        SlotDevices = 2,
        /// <summary>Every Bluetooth-pathed device PadForge knows.</summary>
        AllDevices = 3
    }

    /// <summary>One row in the Disconnect-action device picker (issue #162):
    /// a known Bluetooth-pathed device by GUID and display name.</summary>
    public class MacroDisconnectDeviceOption
    {
        public Guid Guid { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>Which axes a <see cref="MacroActionType.MouseRecenter"/> action
    /// snaps to the primary-monitor center (issue #108).</summary>
    public enum CursorRecenterMode
    {
        /// <summary>Snap only the X coordinate to center; leave Y where it is.</summary>
        XOnly = 0,
        /// <summary>Snap only the Y coordinate to center; leave X where it is.</summary>
        YOnly = 1,
        /// <summary>Snap both X and Y to center.</summary>
        XAndY = 2
    }

    /// <summary>Which axes a <see cref="MacroActionType.MouseFixPosition"/> action
    /// pins to its target coordinate (issue #109).</summary>
    public enum CursorPinMode
    {
        /// <summary>Pin only the X coordinate; leave Y free.</summary>
        XOnly = 0,
        /// <summary>Pin only the Y coordinate; leave X free.</summary>
        YOnly = 1,
        /// <summary>Pin both X and Y.</summary>
        XAndY = 2
    }

    /// <summary>Which axes a <see cref="MacroActionType.MouseLimitRegion"/> action
    /// clamps inside the inset region (issue #110).</summary>
    public enum CursorClampMode
    {
        /// <summary>Clamp only the X coordinate; leave Y free.</summary>
        XOnly = 0,
        /// <summary>Clamp only the Y coordinate; leave X free.</summary>
        YOnly = 1,
        /// <summary>Clamp both X and Y.</summary>
        XAndY = 2
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

    /// <summary>Write mode for the volatile latch on
    /// <see cref="MacroActionType.ToggleKey"/> and
    /// <see cref="MacroActionType.ToggleMouseButton"/> (audit #2 M4).
    /// Toggle keeps the classic flip. On and Off make the fire
    /// idempotent, so a hold pair can SET the latch on its press leg and
    /// CLEAR it on its release leg: a two-macro flip decomposition
    /// alternates or sticks, because each fire inverts whatever state
    /// the other leg left behind.</summary>
    public enum MacroLatchDirection
    {
        /// <summary>Flip the latch. Each fire toggles.</summary>
        Toggle = 0,
        /// <summary>Force the latch on.</summary>
        On = 1,
        /// <summary>Force the latch off. On a hold-pair leg
        /// (<see cref="MacroItem.PairId"/> nonzero) the clear also
        /// reaches the twin's latches, because each leg's latch state
        /// lives on its own action instance.</summary>
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
        /// <summary>The optional profile id re-letters the Numbered button
        /// labels on Switch Pro profiles (#215); other styles ignore it.</summary>
        public static string DisplayName(MacroOutputChannel channel, MacroButtonStyle style, string profileId = null)
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
                        MacroOutputChannel.A     => MacroButtonNames.RawButtonLabel(profileId, 1),
                        MacroOutputChannel.B     => MacroButtonNames.RawButtonLabel(profileId, 2),
                        MacroOutputChannel.X     => MacroButtonNames.RawButtonLabel(profileId, 3),
                        MacroOutputChannel.Y     => MacroButtonNames.RawButtonLabel(profileId, 4),
                        MacroOutputChannel.LB    => MacroButtonNames.RawButtonLabel(profileId, 5),
                        MacroOutputChannel.RB    => MacroButtonNames.RawButtonLabel(profileId, 6),
                        MacroOutputChannel.Back  => MacroButtonNames.RawButtonLabel(profileId, 7),
                        MacroOutputChannel.Start => MacroButtonNames.RawButtonLabel(profileId, 8),
                        MacroOutputChannel.LS    => MacroButtonNames.RawButtonLabel(profileId, 9),
                        MacroOutputChannel.RS    => MacroButtonNames.RawButtonLabel(profileId, 10),
                        MacroOutputChannel.Guide => MacroButtonNames.RawButtonLabel(profileId, 11),
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
        public static List<MacroOutputChannelOption> GetOptions(MacroButtonStyle style, string profileId = null)
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
                list.Add(new MacroOutputChannelOption { Value = ch, Name = DisplayName(ch, style, profileId) });
            return list;
        }
    }

    public static class MacroButtonNames
    {
        /// <summary>The Numbered convention's mask order: numbered button N
        /// (1-based) corresponds to NumberedMaskOrder[N-1]. This is exactly
        /// the mapping BuildNumberedDefs labels (A = Button 1 ... Guide =
        /// Button 11). Shared by the menu editor and delivery so a slot's
        /// output-type switch translates an authored button instead of
        /// stranding it.</summary>
        public static readonly ushort[] NumberedMaskOrder =
        {
            0x1000, 0x2000, 0x4000, 0x8000, // A B X Y
            0x0100, 0x0200,                 // LB RB
            0x0020, 0x0010,                 // Back Start
            0x0040, 0x0080,                 // LS RS
            0x0400,                         // Guide
        };

        /// <summary>1-based numbered-button equivalent of the LOWEST set
        /// mask bit, or 0 when none maps.</summary>
        public static int NumberFromMask(int mask)
        {
            for (int i = 0; i < NumberedMaskOrder.Length; i++)
                if ((mask & NumberedMaskOrder[i]) != 0) return i + 1;
            return 0;
        }

        /// <summary>Mask equivalent of a 1-based numbered button, or 0 when
        /// the number is outside the shared 1..11 range.</summary>
        public static ushort MaskFromNumber(int number)
            => number >= 1 && number <= NumberedMaskOrder.Length
                ? NumberedMaskOrder[number - 1] : (ushort)0;

        /// <summary>
        /// Returns the button label/flag pairs for the given style.
        /// Flags are always the same Xbox-standard bitmask; only labels differ.
        /// The optional profile id re-letters the Numbered labels on Switch
        /// Pro profiles (#215); it is ignored for the other styles.
        /// </summary>
        public static (string Label, ushort Flag)[] GetButtonDefs(MacroButtonStyle style, string profileId = null) => style switch
        {
            MacroButtonStyle.DualShock4 => BuildDS4Defs(),
            MacroButtonStyle.Numbered => BuildNumberedDefs(profileId),
            _ => BuildXboxDefs()
        };

        /// <summary>Formats a button bitmask into a human-readable string.
        /// The optional profile id re-letters Numbered labels on Switch Pro
        /// profiles (#215).</summary>
        public static string FormatButtons(ushort flags, MacroButtonStyle style, string profileId = null)
        {
            if (flags == 0) return Strings.Instance.Macro_None;
            var defs = GetButtonDefs(style, profileId);
            return string.Join(" + ", defs.Where(d => (flags & d.Flag) != 0).Select(d => d.Label));
        }

        /// <summary>Formats custom Extended button words into a human-readable
        /// string. The optional profile id re-letters Switch Pro buttons (#215).</summary>
        public static string FormatCustomButtons(uint[] words, string profileId = null)
        {
            if (words == null || words.All(w => w == 0)) return Strings.Instance.Macro_None;
            var parts = new List<string>();
            for (int i = 0; i < 128; i++)
            {
                int word = i / 32;
                int bit = i % 32;
                if (word < words.Length && (words[word] & (uint)(1 << bit)) != 0)
                    parts.Add(RawButtonShortLabel(profileId, i + 1));
            }
            return parts.Count > 0 ? string.Join(" + ", parts) : Strings.Instance.Macro_None;
        }

        /// <summary>
        /// Derives the button style from the output controller type. Extended
        /// slots show numbered labels (Btn1, Btn2, ...) since the active
        /// HIDMaestro profile drives the layout. Xbox-style "A B X Y" labels
        /// belong on Xbox slots, DualShock labels on PlayStation slots.
        /// Switch Pro profiles keep the Numbered value space and re-letter
        /// the labels per raw index (see <see cref="RawButtonLabel"/>).
        /// </summary>
        public static MacroButtonStyle DeriveStyle(VirtualControllerType outputType) => outputType switch
        {
            VirtualControllerType.PlayStation => MacroButtonStyle.DualShock4,
            VirtualControllerType.Extended    => MacroButtonStyle.Numbered,
            // Nintendo rides the Numbered value space and re-letters per
            // raw index through the switch-pro profile (#215), exactly
            // like an Extended slot holding that profile.
            VirtualControllerType.Nintendo    => MacroButtonStyle.Numbered,
            _                                 => MacroButtonStyle.Xbox360
        };

        /// <summary>Nintendo lettering gate (#215): true when the Extended
        /// slot's HIDMaestro profile is a Switch Pro family pad whose
        /// descriptor button order is the Nintendo standard (B A Y X, L R,
        /// ZL ZR, Minus Plus, stick clicks, Home, Capture). Grounded in the
        /// switch-pro profile's layout roles. Covers "switch-pro" and the
        /// "switch2-pro" id family ("switch2-pro-controller" in the current
        /// catalog).</summary>
        public static bool IsNintendoLetteredProfile(string profileId) =>
            !string.IsNullOrEmpty(profileId)
            && (string.Equals(profileId, "switch-pro", StringComparison.OrdinalIgnoreCase)
                || profileId.StartsWith("switch2-pro", StringComparison.OrdinalIgnoreCase));

        /// <summary>True for the Switch 2 Pro family specifically. Its wire
        /// order is NOT the original Pro Controller's: the D-pad rides four
        /// buttons instead of a hat, the left-hand controls sit above the
        /// right-hand ones, and it carries five extra buttons the original
        /// has no wire for.</summary>
        public static bool IsSwitch2LetteredProfile(string profileId) =>
            !string.IsNullOrEmpty(profileId)
            && profileId.StartsWith("switch2-pro", StringComparison.OrdinalIgnoreCase);

        /// <summary>Count of role-mapped (lettered) buttons on the ORIGINAL
        /// switch-pro profile: indices 0-13. The descriptor declares 18 (the
        /// last four are the Joy-Con rail SL/SR bits), but the SDK packer
        /// only emits role-mapped buttons, so anything past this count is
        /// dead wire on the virtual pad.</summary>
        public const int NintendoLetteredButtonCount = 14;

        /// <summary>Same count for the Switch 2 Pro: indices 0-20, every one
        /// role-mapped in the profile's report 0x09 field list (three
        /// button-mask bytes of 8 + 8 + 5). Clamping this family at 14 hid
        /// Home, Capture, GR, GL and C behind the end of the grid, so they
        /// could not be mapped at all.</summary>
        public const int Switch2LetteredButtonCount = 21;

        /// <summary>Lettered button count for whichever Nintendo profile is
        /// active. Callers that size a raw-index surface must use this, not
        /// either constant directly.</summary>
        public static int NintendoLetteredCountFor(string profileId) =>
            IsSwitch2LetteredProfile(profileId)
                ? Switch2LetteredButtonCount
                : NintendoLetteredButtonCount;

        /// <summary>Nintendo name for a 0-based raw button index on the
        /// ORIGINAL Switch Pro profile, or null past the lettered range
        /// (callers fall back to the numbered format). Index order matches
        /// the switch-pro HID descriptor: face 0-3, bumpers 4-5, ZL/ZR 6-7,
        /// Minus/Plus 8-9, stick clicks 10-11, Home 12, Capture 13.</summary>
        public static string NintendoExtendedLabel(int index) => index switch
        {
            0 => "B",
            1 => "A",
            2 => "Y",
            3 => "X",
            4 => Strings.Instance.Btn_L,
            5 => Strings.Instance.Btn_R,
            6 => Strings.Instance.Btn_ZL,
            7 => Strings.Instance.Btn_ZR,
            8 => Strings.Instance.Btn_Minus,
            9 => Strings.Instance.Btn_Plus,
            10 => Strings.Instance.Btn_LeftStickButton,
            11 => Strings.Instance.Btn_RightStickButton,
            12 => Strings.Instance.Btn_Home,
            13 => Strings.Instance.Btn_Capture,
            _ => null,
        };

        /// <summary>Same, for the Switch 2 Pro. Order is the field list of
        /// the profile's report 0x09 button masks, byte 3 then 4 then 5:
        ///   3: B A Y X, R, ZR, Plus, RS
        ///   4: Down Right Left Up, L, ZL, Minus, LS
        ///   5: Home, Capture, GR, GL, C
        /// It shares almost nothing with the original's order beyond the
        /// four face buttons, which is why the label table has to fork by
        /// profile rather than extend.</summary>
        public static string Switch2ExtendedLabel(int index) => index switch
        {
            0 => "B",
            1 => "A",
            2 => "Y",
            3 => "X",
            4 => Strings.Instance.Btn_R,
            5 => Strings.Instance.Btn_ZR,
            6 => Strings.Instance.Btn_Plus,
            7 => Strings.Instance.Btn_RightStickButton,
            8 => Strings.Instance.Btn_Down,
            9 => Strings.Instance.Btn_Right,
            10 => Strings.Instance.Btn_Left,
            11 => Strings.Instance.Btn_Up,
            12 => Strings.Instance.Btn_L,
            13 => Strings.Instance.Btn_ZL,
            14 => Strings.Instance.Btn_Minus,
            15 => Strings.Instance.Btn_LeftStickButton,
            16 => Strings.Instance.Btn_Home,
            17 => Strings.Instance.Btn_Capture,
            18 => Strings.Instance.Btn_GR,
            19 => Strings.Instance.Btn_GL,
            20 => Strings.Instance.Btn_C,
            _ => null,
        };

        /// <summary>Lettered label for whichever Nintendo profile is active.</summary>
        public static string NintendoLetteredLabel(string profileId, int index) =>
            IsSwitch2LetteredProfile(profileId)
                ? Switch2ExtendedLabel(index)
                : NintendoExtendedLabel(index);

        /// <summary>Label for the 1-based Extended button N under the given
        /// profile: Nintendo lettering on Switch Pro profiles, the "Button
        /// {N}" format otherwise. The long-form twin of
        /// <see cref="RawButtonShortLabel"/> (mapping grid, menu cell
        /// picker, output-channel dropdown).</summary>
        public static string RawButtonLabel(string profileId, int number) =>
            (IsNintendoLetteredProfile(profileId) ? NintendoLetteredLabel(profileId, number - 1) : null)
            ?? string.Format(Strings.Instance.Extended_Button_Format, number);

        /// <summary>Compact-label twin of <see cref="RawButtonLabel"/>
        /// ("Btn {N}" fallback) for the macro trigger chips and button
        /// checkbox grid.</summary>
        public static string RawButtonShortLabel(string profileId, int number) =>
            (IsNintendoLetteredProfile(profileId) ? NintendoLetteredLabel(profileId, number - 1) : null)
            ?? string.Format(Strings.Instance.Macro_Btn_Format, number);

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
        // Switch Pro profiles letter each number per the descriptor's raw
        // index (#215); the mask/number value space is unchanged.
        private static (string Label, ushort Flag)[] BuildNumberedDefs(string profileId)
        {
            var s = Strings.Instance;
            string L(int n) => RawButtonShortLabel(profileId, n);
            return new (string, ushort)[]
            {
                (L(1), 0x1000), (L(2), 0x2000),
                (L(3), 0x4000), (L(4), 0x8000),
                (L(5), 0x0100), (L(6), 0x0200),
                (L(7), 0x0020), (L(8), 0x0010),
                (L(9), 0x0040), (L(10), 0x0080),
                (L(11), 0x0400),
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
    /// <summary>One checkbox in the PointerModeCycle editor (issue #203),
    /// mirroring <see cref="LightbarModeCycleOption"/>: IsChecked reads and
    /// rewrites the parent action's CSV, and Label resolves live so a
    /// culture change reflows without a rebuild.</summary>
    public class PointerModeCycleOption : ObservableObject
    {
        private readonly MacroAction _parent;
        public string ModeName { get; }

        public string Label => MacroAction.PointerModeDisplayName(ModeName);

        public PointerModeCycleOption(MacroAction parent, string modeName)
        {
            _parent = parent;
            ModeName = modeName;
            Strings.CultureChanged += OnCultureChanged;
        }

        private void OnCultureChanged()
            => OnPropertyChanged(nameof(Label));

        public bool IsChecked
        {
            get => _parent.ParsedPointerCycleModes()
                .Contains(ModeName, StringComparer.OrdinalIgnoreCase);
            set
            {
                var current = _parent.ParsedPointerCycleModes().ToList();
                bool has = current.Contains(ModeName, StringComparer.OrdinalIgnoreCase);
                if (value && !has) current.Add(ModeName);
                else if (!value && has) current.RemoveAll(
                    m => string.Equals(m, ModeName, StringComparison.OrdinalIgnoreCase));
                else return;
                _parent.WritePointerCycleCsv(current);
                OnPropertyChanged(nameof(IsChecked));
            }
        }
    }

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
