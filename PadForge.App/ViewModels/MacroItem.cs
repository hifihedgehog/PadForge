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
                // Raw device button path.
                if (UsesRawTrigger)
                {
                    var btnParts = _triggerRawButtons.Select(b => $"Btn {b}");
                    string combo = string.Join(" + ", btnParts);
                    string deviceName = ResolveDeviceName(_triggerDeviceGuid);
                    return string.IsNullOrEmpty(deviceName)
                        ? combo
                        : $"{combo} ({deviceName})";
                }

                // Custom vJoy trigger path.
                if (_buttonStyle == MacroButtonStyle.Numbered)
                {
                    if (!UsesCustomTrigger) return "Not set \u2014 click Record";
                    return MacroButtonNames.FormatCustomButtons(_triggerCustomButtonWords);
                }

                // Gamepad preset output controller bitmask path.
                if (_triggerButtons == 0) return "Not set \u2014 click Record";
                return MacroButtonNames.FormatButtons(_triggerButtons, _buttonStyle);
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

        /// <summary>When to fire: on press, on release, or while held.</summary>
        public MacroTriggerMode TriggerMode
        {
            get => _triggerMode;
            set => SetProperty(ref _triggerMode, value);
        }

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
        public bool IsDurationType => _type == MacroActionType.ButtonPress || _type == MacroActionType.KeyPress || _type == MacroActionType.Delay;

        /// <summary>True when Type is AxisSet.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsAxisType => _type == MacroActionType.AxisSet;

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
            set => SetProperty(ref _axisValue, value);
        }

        private MacroAxisTarget _axisTarget = MacroAxisTarget.None;

        /// <summary>For AxisSet: which axis to modify.</summary>
        public MacroAxisTarget AxisTarget
        {
            get => _axisTarget;
            set => SetProperty(ref _axisTarget, value);
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
                return _type switch
                {
                    MacroActionType.ButtonPress => $"Press {btnText} for {_durationMs}ms",
                    MacroActionType.ButtonRelease => $"Release {btnText}",
                    MacroActionType.KeyPress => $"Keys {keyDisplay} for {_durationMs}ms",
                    MacroActionType.KeyRelease => $"Release keys {keyDisplay}",
                    MacroActionType.Delay => $"Wait {_durationMs}ms",
                    MacroActionType.AxisSet => $"Set {_axisTarget} = {_axisValue}",
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
        WhileHeld
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
        AxisSet
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
