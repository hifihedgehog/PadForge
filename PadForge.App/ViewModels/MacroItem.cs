using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common;

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

        /// <summary>Human-readable display of the trigger combo.</summary>
        public string TriggerDisplayText
        {
            get
            {
                if (_triggerButtons == 0) return "Not set — click Record";
                var parts = new System.Collections.Generic.List<string>();
                if ((_triggerButtons & 0x1000) != 0) parts.Add("A");
                if ((_triggerButtons & 0x2000) != 0) parts.Add("B");
                if ((_triggerButtons & 0x4000) != 0) parts.Add("X");
                if ((_triggerButtons & 0x8000) != 0) parts.Add("Y");
                if ((_triggerButtons & 0x0100) != 0) parts.Add("LB");
                if ((_triggerButtons & 0x0200) != 0) parts.Add("RB");
                if ((_triggerButtons & 0x0020) != 0) parts.Add("Back");
                if ((_triggerButtons & 0x0010) != 0) parts.Add("Start");
                if ((_triggerButtons & 0x0040) != 0) parts.Add("LS");
                if ((_triggerButtons & 0x0080) != 0) parts.Add("RS");
                if ((_triggerButtons & 0x0400) != 0) parts.Add("Guide");
                if ((_triggerButtons & 0x0800) != 0) parts.Add("Share");
                if ((_triggerButtons & 0x0001) != 0) parts.Add("Up");
                if ((_triggerButtons & 0x0002) != 0) parts.Add("Down");
                if ((_triggerButtons & 0x0004) != 0) parts.Add("Left");
                if ((_triggerButtons & 0x0008) != 0) parts.Add("Right");
                return parts.Count > 0 ? string.Join(" + ", parts) : "Not set";
            }
        }

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
            IsRecordingTrigger ? "Recording..." : "Record Trigger";

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
            set => SetProperty(ref _selectedAction, value);
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
                var action = new MacroAction { Type = MacroActionType.ButtonPress };
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
                    OnPropertyChanged(nameof(DisplayText));
            }
        }

        private ushort _buttonFlags;

        /// <summary>
        /// For ButtonPress/ButtonRelease: Gamepad button flags to press/release.
        /// </summary>
        public ushort ButtonFlags
        {
            get => _buttonFlags;
            set
            {
                if (SetProperty(ref _buttonFlags, value))
                    OnPropertyChanged(nameof(DisplayText));
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
                return _type switch
                {
                    MacroActionType.ButtonPress => $"Press buttons ({_buttonFlags:X4}) for {_durationMs}ms",
                    MacroActionType.ButtonRelease => $"Release buttons ({_buttonFlags:X4})",
                    MacroActionType.KeyPress => $"Key {ResolveKeyName(_keyCode)} for {_durationMs}ms",
                    MacroActionType.KeyRelease => $"Release key {ResolveKeyName(_keyCode)}",
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
}
