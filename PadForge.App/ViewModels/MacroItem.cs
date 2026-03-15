using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;

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

        private string _name = "New Macro";

        /// <summary>User-facing name for this macro.</summary>
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

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
        /// For custom vJoy OutputController triggers: wide button bitmask (128 buttons).
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
        /// or from the combined Xbox-mapped virtual controller output.
        /// </summary>
        public MacroTriggerSource TriggerSource
        {
            get => _triggerSource;
            set => SetProperty(ref _triggerSource, value);
        }

        /// <summary>Human-readable display of the trigger combo.</summary>
        public string TriggerDisplayText
        {
            get
            {
                var parts = new List<string>();

                // Button part.
                if (UsesRawTrigger)
                {
                    var objects = ResolveDeviceObjects(_triggerDeviceGuid);
                    foreach (int b in _triggerRawButtons)
                    {
                        var obj = objects?.FirstOrDefault(o => o.IsButton && o.InputIndex == b);
                        parts.Add(obj != null && !string.IsNullOrEmpty(obj.Name) ? obj.Name : $"Button {b}");
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

                // Axis part(s).
                foreach (var axis in _triggerAxisTargets)
                    parts.Add($"{axis.DisplayName()} > {_triggerAxisThreshold}%");

                if (parts.Count == 0) return "Not set \u2014 click Record";

                string result = string.Join(" + ", parts);

                // Append source device name at end.
                if (UsesRawTrigger || UsesPovTrigger || UsesAxisTrigger)
                {
                    string deviceName = ResolveDeviceName(_triggerDeviceGuid);
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
            return $"POV {idx} {CentidegreesToDirection(cd)}";
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
            if (centidegrees < 0) return "Centered";
            centidegrees %= 36000;
            if (centidegrees >= 33750 || centidegrees < 2250) return "Up";
            if (centidegrees < 6750) return "Up-Right";
            if (centidegrees < 11250) return "Right";
            if (centidegrees < 15750) return "Down-Right";
            if (centidegrees < 20250) return "Down";
            if (centidegrees < 24750) return "Down-Left";
            if (centidegrees < 29250) return "Left";
            if (centidegrees < 33750) return "Up-Left";
            return "Up";
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

        /// <summary>True if this macro uses the raw device button trigger path.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool UsesRawTrigger => _triggerDeviceGuid != Guid.Empty && _triggerRawButtons.Length > 0;

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

        /// <summary>True if this macro uses POV hat triggers.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool UsesPovTrigger => _triggerPovs.Length > 0;

        private bool _isRecordingTrigger;

        /// <summary>Whether the macro is currently recording its trigger combo.</summary>
        public bool IsRecordingTrigger
        {
            get => _isRecordingTrigger;
            set
            {
                if (SetProperty(ref _isRecordingTrigger, value))
                    OnPropertyChanged(nameof(RecordTriggerButtonText));
            }
        }

        public string RecordTriggerButtonText =>
            IsRecordingTrigger ? "Stop" : "Record Trigger";

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
        /// Set by PadViewModel when OutputType/VJoyPreset changes.
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
                    foreach (var action in Actions)
                        action.ButtonStyle = value;
                }
            }
        }

        private int _customButtonCount = 11;

        /// <summary>
        /// Number of buttons for custom vJoy (from VJoyConfig.ButtonCount).
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

        private MacroTriggerMode _triggerMode = MacroTriggerMode.OnPress;

        /// <summary>When to fire: on press, on release, while held, or always.</summary>
        public MacroTriggerMode TriggerMode
        {
            get => _triggerMode;
            set
            {
                if (SetProperty(ref _triggerMode, value))
                    OnPropertyChanged(nameof(IsNotAlwaysMode));
            }
        }

        /// <summary>True when TriggerMode is not Always (used to show/hide trigger recording UI).</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsNotAlwaysMode => _triggerMode != MacroTriggerMode.Always;

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
                OnPropertyChanged(nameof(TriggerDisplayText));
            }
        }

        /// <summary>Backward-compatible single-axis property. Sets/gets first element.</summary>
        public MacroAxisTarget TriggerAxisTarget
        {
            get => _triggerAxisTargets.Length > 0 ? _triggerAxisTargets[0] : MacroAxisTarget.None;
            set
            {
                // Legacy setter — convert single value to array.
                if (value == MacroAxisTarget.None)
                    TriggerAxisTargets = Array.Empty<MacroAxisTarget>();
                else
                    TriggerAxisTargets = new[] { value };
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
        public bool UsesAxisTrigger => _triggerAxisTargets.Length > 0;

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
                }
            }
        }

        /// <summary>True when Type is ButtonPress or ButtonRelease.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsButtonType => _type == MacroActionType.ButtonPress || _type == MacroActionType.ButtonRelease;

        /// <summary>True when Type is KeyPress or KeyRelease.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsKeyType => _type == MacroActionType.KeyPress || _type == MacroActionType.KeyRelease;

        /// <summary>True when Type is ButtonPress, KeyPress, or Delay.</summary>
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
        /// Number of buttons to show for Numbered style (from VJoyConfig.ButtonCount).
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

        // ── Custom vJoy button storage (128 buttons max) ──

        private uint[] _customButtonWords = new uint[4];

        /// <summary>
        /// For ButtonPress/ButtonRelease with custom vJoy: wide button bitmask (4 × 32-bit = 128 buttons).
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

        /// <summary>Sets/clears a custom vJoy button (0-based index).</summary>
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

        /// <summary>Returns true if the specified custom vJoy button is pressed.</summary>
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
                        // Dynamic list for custom vJoy — N buttons from config.
                        var list = new List<GamepadButtonOption>();
                        for (int i = 0; i < _customButtonCount; i++)
                            list.Add(new GamepadButtonOption(this, $"Btn {i + 1}", customIndex: i));
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
        /// Provides the list of VirtualKey values for ComboBox binding in the UI.
        /// </summary>
        public static Array VirtualKeyValues { get; } = Enum.GetValues(typeof(VirtualKey));

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
                    ? $"Device Axis {_sourceDeviceAxisIndex}"
                    : _axisTarget.DisplayName();
                return _type switch
                {
                    MacroActionType.ButtonPress => $"Press {btnText} for {_durationMs}ms",
                    MacroActionType.ButtonRelease => $"Release {btnText}",
                    MacroActionType.KeyPress => $"Keys {keyDisplay} for {_durationMs}ms",
                    MacroActionType.KeyRelease => $"Release keys {keyDisplay}",
                    MacroActionType.Delay => $"Wait {_durationMs}ms",
                    MacroActionType.AxisSet => $"Set {_axisTarget} = {_axisValue}",
                    MacroActionType.SystemVolume => _volumeLimit < 100
                        ? $"System Volume \u2190 {axisLabel} (max {_volumeLimit}%)"
                        : $"System Volume \u2190 {axisLabel}",
                    MacroActionType.AppVolume => string.IsNullOrEmpty(_processName)
                        ? (_volumeLimit < 100 ? $"App Volume \u2190 {axisLabel} (max {_volumeLimit}%)" : $"App Volume \u2190 {axisLabel}")
                        : (_volumeLimit < 100 ? $"App Volume ({_processName}) \u2190 {axisLabel} (max {_volumeLimit}%)" : $"App Volume ({_processName}) \u2190 {axisLabel}"),
                    MacroActionType.MouseMove => $"Mouse Move \u2190 {axisLabel} (speed {_mouseSensitivity:F0})",
                    MacroActionType.MouseButtonPress => $"Mouse Press {_mouseButton}",
                    MacroActionType.MouseButtonRelease => $"Mouse Release {_mouseButton}",
                    MacroActionType.MouseScroll => $"Mouse Scroll \u2190 {axisLabel} (speed {_mouseSensitivity:F0})",
                    _ => "Unknown action"
                };
            }
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
        Always
    }

    public enum MacroTriggerSource
    {
        /// <summary>Record from the physical input device's raw/native buttons.</summary>
        InputDevice,

        /// <summary>Record from the combined Xbox-mapped virtual controller output.</summary>
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
        MouseScroll
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
            MacroAxisTarget.LeftStickX => "X Axis",
            MacroAxisTarget.LeftStickY => "Y Axis",
            MacroAxisTarget.RightStickX => "X Rotation",
            MacroAxisTarget.RightStickY => "Y Rotation",
            MacroAxisTarget.LeftTrigger => "Z Axis",
            MacroAxisTarget.RightTrigger => "Z Rotation",
            _ => target.ToString()
        };
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

        /// <summary>Xbox/DS4 bitmask flag (0 for custom mode).</summary>
        public ushort Flag { get; }

        /// <summary>Custom vJoy button index (0-based). -1 = use Flag on ushort.</summary>
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

        /// <summary>Xbox/DS4 bitmask mode.</summary>
        public GamepadButtonOption(MacroAction parent, string label, ushort flag)
        {
            _parent = parent;
            _label = label;
            Flag = flag;
            CustomIndex = -1;
        }

        /// <summary>Custom vJoy button index mode (0-based).</summary>
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
        Numbered  // vJoy Custom: "Btn 1", "Btn 2", etc.
    }

    public static class MacroButtonNames
    {
        /// <summary>
        /// Returns the button label/flag pairs for the given style.
        /// Flags are always the same Xbox-standard bitmask; only labels differ.
        /// </summary>
        public static (string Label, ushort Flag)[] GetButtonDefs(MacroButtonStyle style) => style switch
        {
            MacroButtonStyle.DualShock4 => _ds4Defs,
            MacroButtonStyle.Numbered => _numberedDefs,
            _ => _xboxDefs
        };

        /// <summary>Formats a button bitmask into a human-readable string.</summary>
        public static string FormatButtons(ushort flags, MacroButtonStyle style)
        {
            if (flags == 0) return "(none)";
            var defs = GetButtonDefs(style);
            return string.Join(" + ", defs.Where(d => (flags & d.Flag) != 0).Select(d => d.Label));
        }

        /// <summary>Formats custom vJoy button words into a human-readable string.</summary>
        public static string FormatCustomButtons(uint[] words)
        {
            if (words == null || words.All(w => w == 0)) return "(none)";
            var parts = new List<string>();
            for (int i = 0; i < 128; i++)
            {
                int word = i / 32;
                int bit = i % 32;
                if (word < words.Length && (words[word] & (uint)(1 << bit)) != 0)
                    parts.Add($"Btn {i + 1}");
            }
            return parts.Count > 0 ? string.Join(" + ", parts) : "(none)";
        }

        /// <summary>
        /// Derives the button style from the output controller type and vJoy preset.
        /// </summary>
        public static MacroButtonStyle DeriveStyle(
            VirtualControllerType outputType, VJoyPreset vJoyPreset = VJoyPreset.Xbox360) => outputType switch
        {
            VirtualControllerType.DualShock4 => MacroButtonStyle.DualShock4,
            VirtualControllerType.VJoy => vJoyPreset switch
            {
                VJoyPreset.DualShock4 => MacroButtonStyle.DualShock4,
                VJoyPreset.Custom => MacroButtonStyle.Numbered,
                _ => MacroButtonStyle.Xbox360
            },
            _ => MacroButtonStyle.Xbox360
        };

        private static readonly (string Label, ushort Flag)[] _xboxDefs =
        {
            ("A", 0x1000), ("B", 0x2000), ("X", 0x4000), ("Y", 0x8000),
            ("LB", 0x0100), ("RB", 0x0200), ("Back", 0x0020), ("Start", 0x0010),
            ("LS", 0x0040), ("RS", 0x0080), ("Guide", 0x0400),
            ("Up", 0x0001), ("Down", 0x0002), ("Left", 0x0004), ("Right", 0x0008),
        };

        private static readonly (string Label, ushort Flag)[] _ds4Defs =
        {
            ("Cross", 0x1000), ("Circle", 0x2000), ("Square", 0x4000), ("Triangle", 0x8000),
            ("L1", 0x0100), ("R1", 0x0200), ("Share", 0x0020), ("Options", 0x0010),
            ("L3", 0x0040), ("R3", 0x0080), ("PS", 0x0400), ("Touchpad", 0x0800),
            ("Up", 0x0001), ("Down", 0x0002), ("Left", 0x0004), ("Right", 0x0008),
        };

        // vJoy Custom: Xbox bitmask bits → vJoy button numbers (see SubmitGamepadState mapping).
        // D-pad still shows direction names (they map to POV, not buttons).
        private static readonly (string Label, ushort Flag)[] _numberedDefs =
        {
            ("Btn 1", 0x1000), ("Btn 2", 0x2000), ("Btn 3", 0x4000), ("Btn 4", 0x8000),
            ("Btn 5", 0x0100), ("Btn 6", 0x0200), ("Btn 7", 0x0020), ("Btn 8", 0x0010),
            ("Btn 9", 0x0040), ("Btn 10", 0x0080), ("Btn 11", 0x0400),
            ("Up", 0x0001), ("Down", 0x0002), ("Left", 0x0004), ("Right", 0x0008),
        };
    }
}
