using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.ViewModels
{
    /// <summary>
    /// ViewModel for a single virtual controller slot (one of 16 pads).
    /// Features:
    ///   #1 — Multi-device selection: SelectedMappedDevice picks which device to configure
    ///   #2 — Expanded dead zones: per-axis X/Y, trigger dead zones, anti-dead zone, linear
    ///   #4 — Macro foundation: trigger combos → action sequences
    /// </summary>
    public partial class PadViewModel : ViewModelBase
    {
        public PadViewModel(int padIndex)
        {
            PadIndex = padIndex;
            _slotNumber = padIndex + 1;
            Title = $"Virtual Controller {padIndex + 1}";
            SlotLabel = $"Virtual Controller {padIndex + 1}";
            _vJoyConfig.PropertyChanged += OnVJoyConfigPropertyChanged;
            RebuildMappings();
            RebuildStickConfigs();
            RebuildTriggerConfigs();
        }

        /// <summary>Zero-based pad slot index (0–15).</summary>
        public int PadIndex { get; }

        /// <summary>
        /// Callback invoked when a config item property changes that needs to be persisted.
        /// Wired by MainWindow to call SettingsService.MarkDirty().
        /// </summary>
        public Action ConfigItemDirtyCallback { get; set; }

        private int _slotNumber;
        /// <summary>One-based sequential number among active slots, for display.</summary>
        public int SlotNumber
        {
            get => _slotNumber;
            set => SetProperty(ref _slotNumber, value);
        }

        private string _slotLabel;
        /// <summary>Display label (e.g., "Virtual Controller 1").</summary>
        public string SlotLabel
        {
            get => _slotLabel;
            set => SetProperty(ref _slotLabel, value);
        }

        // ═══════════════════════════════════════════════
        //  Output type (Xbox 360 / DualShock 4)
        // ═══════════════════════════════════════════════

        private VirtualControllerType _outputType;

        /// <summary>Virtual controller output type for this slot.</summary>
        public VirtualControllerType OutputType
        {
            get => _outputType;
            set
            {
                if (SetProperty(ref _outputType, value))
                {
                    ResetDeadZoneSettings();
                    RebuildMappings();
                    RebuildStickConfigs();
                    RebuildTriggerConfigs();
                    SyncMacroButtonStyle();
                }
            }
        }

        private string _typeInstanceLabel = "1";
        /// <summary>Per-type instance number label (e.g., "1", "2"). Set by RefreshNavControllerItems.</summary>
        public string TypeInstanceLabel
        {
            get => _typeInstanceLabel;
            set => SetProperty(ref _typeInstanceLabel, value);
        }

        /// <summary>Int binding for ComboBox SelectedIndex (0=Xbox 360, 1=DualShock 4).</summary>
        public int OutputTypeIndex
        {
            get => (int)_outputType;
            set
            {
                if (Enum.IsDefined(typeof(VirtualControllerType), value))
                    OutputType = (VirtualControllerType)value;
            }
        }

        // ═══════════════════════════════════════════════
        //  vJoy per-slot configuration
        // ═══════════════════════════════════════════════

        private VJoySlotConfig _vJoyConfig = new();

        /// <summary>
        /// Per-slot vJoy configuration (preset, axis/button counts).
        /// Always present — only meaningful when OutputType == VJoy.
        /// </summary>
        public VJoySlotConfig VJoyConfig
        {
            get => _vJoyConfig;
            set
            {
                if (_vJoyConfig != null)
                    _vJoyConfig.PropertyChanged -= OnVJoyConfigPropertyChanged;
                if (SetProperty(ref _vJoyConfig, value) && value != null)
                    value.PropertyChanged += OnVJoyConfigPropertyChanged;
            }
        }

        private void OnVJoyConfigPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // When vJoy config changes (preset, counts), rebuild dynamic collections
            if (OutputType == VirtualControllerType.VJoy)
            {
                switch (e.PropertyName)
                {
                    case nameof(VJoySlotConfig.Preset):
                        ResetDeadZoneSettings();
                        RebuildMappings();
                        RebuildStickConfigs();
                        RebuildTriggerConfigs();
                        SyncMacroButtonStyle();
                        break;
                    case nameof(VJoySlotConfig.ThumbstickCount):
                    case nameof(VJoySlotConfig.TriggerCount):
                        ResetDeadZoneSettings();
                        RebuildMappings();
                        RebuildStickConfigs();
                        RebuildTriggerConfigs();
                        break;
                    case nameof(VJoySlotConfig.PovCount):
                        RebuildMappings();
                        break;
                    case nameof(VJoySlotConfig.ButtonCount):
                        RebuildMappings();
                        SyncMacroButtonStyle();
                        break;
                }
            }
        }

        // ═══════════════════════════════════════════════
        //  MIDI per-slot configuration
        // ═══════════════════════════════════════════════

        private MidiSlotConfig _midiConfig = new();

        /// <summary>
        /// Per-slot MIDI configuration (port, channel, CC/note mappings).
        /// Always present — only meaningful when OutputType == Midi.
        /// </summary>
        public MidiSlotConfig MidiConfig
        {
            get => _midiConfig;
            set => SetProperty(ref _midiConfig, value ?? new());
        }

        // ═══════════════════════════════════════════════
        //  #1: Multi-device selection within a slot
        // ═══════════════════════════════════════════════

        /// <summary>
        /// Info about a single physical device mapped to this virtual controller slot.
        /// </summary>
        public class MappedDeviceInfo : ObservableObject
        {
            private string _name = "Unknown";
            private Guid _instanceGuid;
            private bool _isOnline;

            public string Name
            {
                get => _name;
                set => SetProperty(ref _name, value);
            }

            public Guid InstanceGuid
            {
                get => _instanceGuid;
                set => SetProperty(ref _instanceGuid, value);
            }

            public bool IsOnline
            {
                get => _isOnline;
                set => SetProperty(ref _isOnline, value);
            }

            public override string ToString() => Name;
        }

        /// <summary>All physical devices currently mapped to this slot.</summary>
        public ObservableCollection<MappedDeviceInfo> MappedDevices { get; } = new();

        private MappedDeviceInfo _selectedMappedDevice;

        /// <summary>
        /// The currently selected device within this slot for configuration.
        /// When changed, the mapping grid and dead zone settings should update
        /// to reflect THIS device's PadSetting.
        /// </summary>
        public MappedDeviceInfo SelectedMappedDevice
        {
            get => _selectedMappedDevice;
            set
            {
                var old = _selectedMappedDevice;
                if (SetProperty(ref _selectedMappedDevice, value))
                {
                    if (old != null) old.PropertyChanged -= OnSelectedDevicePropertyChanged;
                    if (value != null) value.PropertyChanged += OnSelectedDevicePropertyChanged;
                    OnPropertyChanged(nameof(HasSelectedDevice));
                    SelectedDeviceChanged?.Invoke(this, value);
                }
            }
        }

        /// <summary>Whether a device is selected for configuration.</summary>
        public bool HasSelectedDevice => _selectedMappedDevice != null;

        private void OnSelectedDevicePropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MappedDeviceInfo.IsOnline))
                _mapAllCommand?.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// Raised when the user selects a different device within this slot.
        /// InputService should reload the PadSetting for the newly selected device.
        /// </summary>
        public event EventHandler<MappedDeviceInfo> SelectedDeviceChanged;

        private string _mappedDeviceName = "No device mapped";

        public string MappedDeviceName
        {
            get => _mappedDeviceName;
            set => SetProperty(ref _mappedDeviceName, value);
        }

        private Guid _mappedDeviceGuid;

        public Guid MappedDeviceGuid
        {
            get => _mappedDeviceGuid;
            set => SetProperty(ref _mappedDeviceGuid, value);
        }

        private bool _isDeviceOnline;

        public bool IsDeviceOnline
        {
            get => _isDeviceOnline;
            set => SetProperty(ref _isDeviceOnline, value);
        }

        // ═══════════════════════════════════════════════
        //  XInput output state (for visualizer) — unchanged
        // ═══════════════════════════════════════════════

        private bool _buttonA;
        public bool ButtonA { get => _buttonA; set => SetProperty(ref _buttonA, value); }

        private bool _buttonB;
        public bool ButtonB { get => _buttonB; set => SetProperty(ref _buttonB, value); }

        private bool _buttonX;
        public bool ButtonX { get => _buttonX; set => SetProperty(ref _buttonX, value); }

        private bool _buttonY;
        public bool ButtonY { get => _buttonY; set => SetProperty(ref _buttonY, value); }

        private bool _leftShoulder;
        public bool LeftShoulder { get => _leftShoulder; set => SetProperty(ref _leftShoulder, value); }

        private bool _rightShoulder;
        public bool RightShoulder { get => _rightShoulder; set => SetProperty(ref _rightShoulder, value); }

        private bool _buttonBack;
        public bool ButtonBack { get => _buttonBack; set => SetProperty(ref _buttonBack, value); }

        private bool _buttonStart;
        public bool ButtonStart { get => _buttonStart; set => SetProperty(ref _buttonStart, value); }

        private bool _leftThumbButton;
        public bool LeftThumbButton { get => _leftThumbButton; set => SetProperty(ref _leftThumbButton, value); }

        private bool _rightThumbButton;
        public bool RightThumbButton { get => _rightThumbButton; set => SetProperty(ref _rightThumbButton, value); }

        private bool _buttonGuide;
        public bool ButtonGuide { get => _buttonGuide; set => SetProperty(ref _buttonGuide, value); }

        private bool _dpadUp;
        public bool DPadUp { get => _dpadUp; set => SetProperty(ref _dpadUp, value); }

        private bool _dpadDown;
        public bool DPadDown { get => _dpadDown; set => SetProperty(ref _dpadDown, value); }

        private bool _dpadLeft;
        public bool DPadLeft { get => _dpadLeft; set => SetProperty(ref _dpadLeft, value); }

        private bool _dpadRight;
        public bool DPadRight { get => _dpadRight; set => SetProperty(ref _dpadRight, value); }

        private double _leftTrigger;
        public double LeftTrigger { get => _leftTrigger; set => SetProperty(ref _leftTrigger, value); }

        private double _rightTrigger;
        public double RightTrigger { get => _rightTrigger; set => SetProperty(ref _rightTrigger, value); }

        private double _thumbLX = 0.5;
        public double ThumbLX { get => _thumbLX; set => SetProperty(ref _thumbLX, value); }

        private double _thumbLY = 0.5;
        public double ThumbLY { get => _thumbLY; set => SetProperty(ref _thumbLY, value); }

        private double _thumbRX = 0.5;
        public double ThumbRX { get => _thumbRX; set => SetProperty(ref _thumbRX, value); }

        private double _thumbRY = 0.5;
        public double ThumbRY { get => _thumbRY; set => SetProperty(ref _thumbRY, value); }

        private short _rawThumbLX;
        public short RawThumbLX { get => _rawThumbLX; set => SetProperty(ref _rawThumbLX, value); }

        private short _rawThumbLY;
        public short RawThumbLY { get => _rawThumbLY; set => SetProperty(ref _rawThumbLY, value); }

        private short _rawThumbRX;
        public short RawThumbRX { get => _rawThumbRX; set => SetProperty(ref _rawThumbRX, value); }

        private short _rawThumbRY;
        public short RawThumbRY { get => _rawThumbRY; set => SetProperty(ref _rawThumbRY, value); }

        private ushort _rawLeftTrigger;
        public ushort RawLeftTrigger { get => _rawLeftTrigger; set => SetProperty(ref _rawLeftTrigger, value); }

        private ushort _rawRightTrigger;
        public ushort RawRightTrigger { get => _rawRightTrigger; set => SetProperty(ref _rawRightTrigger, value); }

        // ── Per-device values for stick/trigger tab previews ──
        // These show the selected device only, not the combined slot.

        private double _deviceThumbLX = 0.5;
        public double DeviceThumbLX { get => _deviceThumbLX; set => SetProperty(ref _deviceThumbLX, value); }

        private double _deviceThumbLY = 0.5;
        public double DeviceThumbLY { get => _deviceThumbLY; set => SetProperty(ref _deviceThumbLY, value); }

        private double _deviceThumbRX = 0.5;
        public double DeviceThumbRX { get => _deviceThumbRX; set => SetProperty(ref _deviceThumbRX, value); }

        private double _deviceThumbRY = 0.5;
        public double DeviceThumbRY { get => _deviceThumbRY; set => SetProperty(ref _deviceThumbRY, value); }

        private short _deviceRawThumbLX;
        public short DeviceRawThumbLX { get => _deviceRawThumbLX; set => SetProperty(ref _deviceRawThumbLX, value); }

        private short _deviceRawThumbLY;
        public short DeviceRawThumbLY { get => _deviceRawThumbLY; set => SetProperty(ref _deviceRawThumbLY, value); }

        private short _deviceRawThumbRX;
        public short DeviceRawThumbRX { get => _deviceRawThumbRX; set => SetProperty(ref _deviceRawThumbRX, value); }

        private short _deviceRawThumbRY;
        public short DeviceRawThumbRY { get => _deviceRawThumbRY; set => SetProperty(ref _deviceRawThumbRY, value); }

        private double _deviceLeftTrigger;
        public double DeviceLeftTrigger { get => _deviceLeftTrigger; set => SetProperty(ref _deviceLeftTrigger, value); }

        private double _deviceRightTrigger;
        public double DeviceRightTrigger { get => _deviceRightTrigger; set => SetProperty(ref _deviceRightTrigger, value); }

        private ushort _deviceRawLeftTrigger;
        public ushort DeviceRawLeftTrigger { get => _deviceRawLeftTrigger; set => SetProperty(ref _deviceRawLeftTrigger, value); }

        private ushort _deviceRawRightTrigger;
        public ushort DeviceRawRightTrigger { get => _deviceRawRightTrigger; set => SetProperty(ref _deviceRawRightTrigger, value); }

        // ═══════════════════════════════════════════════
        //  Mapping rows — unchanged
        // ═══════════════════════════════════════════════

        public ObservableCollection<MappingItem> Mappings { get; } =
            new ObservableCollection<MappingItem>();

        /// <summary>
        /// Raised after RebuildMappings completes so listeners (e.g. InputService) can
        /// reload mapping descriptors from the active PadSetting into the new MappingItems.
        /// </summary>
        public event EventHandler MappingsRebuilt;

        /// <summary>
        /// Rebuilds the Mappings collection based on the current OutputType and vJoy config.
        /// Labels follow the output type's convention (Xbox 360/DS4/vJoy numbered).
        /// </summary>
        public void RebuildMappings()
        {
            Mappings.Clear();

            bool isCustomVJoy = OutputType == VirtualControllerType.VJoy && !VJoyConfig.IsGamepadPreset;
            if (OutputType == VirtualControllerType.KeyboardMouse)
                InitializeKeyboardMouseMappings();
            else if (OutputType == VirtualControllerType.Midi)
                InitializeMidiMappings();
            else if (isCustomVJoy)
                InitializeVJoyCustomMappings();
            else
                InitializeGamepadMappings();

            MappingsRebuilt?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Standard gamepad mappings (21 items). Labels depend on the effective output type:
        /// Xbox 360 naming for Xbox360 / vJoy-Xbox360, DS4 naming for DualShock4 / vJoy-DS4.
        /// </summary>
        private void InitializeGamepadMappings()
        {
            bool isDS4 = OutputType == VirtualControllerType.DualShock4
                || (OutputType == VirtualControllerType.VJoy && VJoyConfig.Preset == VJoyPreset.DualShock4);

            // Buttons
            if (isDS4)
            {
                Mappings.Add(new MappingItem("\u2715", "ButtonA", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("\u25CB", "ButtonB", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("\u25FB", "ButtonX", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("\u25B3", "ButtonY", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("L1", "LeftShoulder", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("R1", "RightShoulder", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("Share", "ButtonBack", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("Options", "ButtonStart", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("PS", "ButtonGuide", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("L3", "LeftThumbButton", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("R3", "RightThumbButton", MappingCategory.Buttons));
            }
            else
            {
                Mappings.Add(new MappingItem("A", "ButtonA", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("B", "ButtonB", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("X", "ButtonX", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("Y", "ButtonY", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("Left Bumper", "LeftShoulder", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("Right Bumper", "RightShoulder", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("Back", "ButtonBack", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("Start", "ButtonStart", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("Guide", "ButtonGuide", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("Left Stick Click", "LeftThumbButton", MappingCategory.Buttons));
                Mappings.Add(new MappingItem("Right Stick Click", "RightThumbButton", MappingCategory.Buttons));
            }

            // D-Pad
            Mappings.Add(new MappingItem("D-Pad Up", "DPadUp", MappingCategory.DPad));
            Mappings.Add(new MappingItem("D-Pad Down", "DPadDown", MappingCategory.DPad));
            Mappings.Add(new MappingItem("D-Pad Left", "DPadLeft", MappingCategory.DPad));
            Mappings.Add(new MappingItem("D-Pad Right", "DPadRight", MappingCategory.DPad));

            // Triggers
            Mappings.Add(new MappingItem(isDS4 ? "L2" : "Left Trigger", "LeftTrigger", MappingCategory.Triggers));
            Mappings.Add(new MappingItem(isDS4 ? "R2" : "Right Trigger", "RightTrigger", MappingCategory.Triggers));

            // Stick axes
            Mappings.Add(new MappingItem("Left Stick X", "LeftThumbAxisX", MappingCategory.LeftStick, "LeftThumbAxisXNeg"));
            Mappings.Add(new MappingItem("Left Stick Y", "LeftThumbAxisY", MappingCategory.LeftStick, "LeftThumbAxisYNeg"));
            Mappings.Add(new MappingItem("Right Stick X", "RightThumbAxisX", MappingCategory.RightStick, "RightThumbAxisXNeg"));
            Mappings.Add(new MappingItem("Right Stick Y", "RightThumbAxisY", MappingCategory.RightStick, "RightThumbAxisYNeg"));
        }

        /// <summary>
        /// MIDI mappings — labels use CC numbers for axes and Note numbers for buttons.
        /// Uses the same PadSetting property keys as gamepad so the pipeline works unchanged.
        /// </summary>
        private void InitializeMidiMappings()
        {
            var mc = MidiConfig;
            var ccNumbers = mc.GetCcNumbers();
            var noteNumbers = mc.GetNoteNumbers();

            // CC outputs — each CC is a bipolar axis with positive and negative mapping keys.
            for (int i = 0; i < mc.CcCount; i++)
                Mappings.Add(new MappingItem($"CC {ccNumbers[i]}", $"MidiCC{i}", MappingCategory.Triggers, $"MidiCC{i}Neg"));

            // Note outputs — each note is a button (Note On/Off).
            for (int i = 0; i < mc.NoteCount; i++)
                Mappings.Add(new MappingItem($"Note {noteNumbers[i]}", $"MidiNote{i}", MappingCategory.Buttons));
        }

        /// <summary>
        /// Keyboard + Mouse mappings — full keyboard keys, mouse buttons, and mouse axes.
        /// Targets use "Kbm" prefix for dictionary-based PadSetting storage.
        /// Key targets: "KbmKey{vk}" where vk is the Windows virtual-key code (hex).
        /// Mouse buttons: "KbmMBtn{0-4}" (LMB, RMB, MMB, X1, X2).
        /// Mouse axes: "KbmMouseX"/"KbmMouseY" (bidirectional), "KbmScroll" (bidirectional).
        /// </summary>
        private void InitializeKeyboardMouseMappings()
        {
            // Helper to add a keyboard key mapping target
            void AddKey(string label, byte vk)
                => Mappings.Add(new MappingItem(label, $"KbmKey{vk:X2}", MappingCategory.Buttons));

            // ── Letters ──
            for (int i = 0; i < 26; i++)
                AddKey(((char)('A' + i)).ToString(), (byte)(0x41 + i));

            // ── Numbers ──
            for (int i = 0; i <= 9; i++)
                AddKey(i.ToString(), (byte)(0x30 + i));

            // ── Function keys ──
            for (int i = 1; i <= 12; i++)
                AddKey($"F{i}", (byte)(0x6F + i)); // VK_F1=0x70 .. VK_F12=0x7B

            // ── Modifiers ──
            AddKey("Left Shift", 0xA0);
            AddKey("Right Shift", 0xA1);
            AddKey("Left Ctrl", 0xA2);
            AddKey("Right Ctrl", 0xA3);
            AddKey("Left Alt", 0xA4);
            AddKey("Right Alt", 0xA5);

            // ── Special keys ──
            AddKey("Space", 0x20);
            AddKey("Enter", 0x0D);
            AddKey("Escape", 0x1B);
            AddKey("Tab", 0x09);
            AddKey("Backspace", 0x08);
            AddKey("Caps Lock", 0x14);

            // ── Navigation ──
            AddKey("Up", 0x26);
            AddKey("Down", 0x28);
            AddKey("Left", 0x25);
            AddKey("Right", 0x27);
            AddKey("Home", 0x24);
            AddKey("End", 0x23);
            AddKey("Page Up", 0x21);
            AddKey("Page Down", 0x22);
            AddKey("Insert", 0x2D);
            AddKey("Delete", 0x2E);

            // ── Punctuation ──
            AddKey(";", 0xBA);
            AddKey("=", 0xBB);
            AddKey(",", 0xBC);
            AddKey("-", 0xBD);
            AddKey(".", 0xBE);
            AddKey("/", 0xBF);
            AddKey("`", 0xC0);
            AddKey("[", 0xDB);
            AddKey("\\", 0xDC);
            AddKey("]", 0xDD);
            AddKey("'", 0xDE);

            // ── Numpad ──
            for (int i = 0; i <= 9; i++)
                AddKey($"Num {i}", (byte)(0x60 + i));
            AddKey("Num *", 0x6A);
            AddKey("Num +", 0x6B);
            AddKey("Num -", 0x6D);
            AddKey("Num .", 0x6E);
            AddKey("Num /", 0x6F);

            // ── Mouse buttons ──
            Mappings.Add(new MappingItem("Left Click", "KbmMBtn0", MappingCategory.Buttons));
            Mappings.Add(new MappingItem("Right Click", "KbmMBtn1", MappingCategory.Buttons));
            Mappings.Add(new MappingItem("Middle Click", "KbmMBtn2", MappingCategory.Buttons));
            Mappings.Add(new MappingItem("Mouse 4", "KbmMBtn3", MappingCategory.Buttons));
            Mappings.Add(new MappingItem("Mouse 5", "KbmMBtn4", MappingCategory.Buttons));

            // ── Mouse movement axes (bidirectional) ──
            Mappings.Add(new MappingItem("Mouse X", "KbmMouseX", MappingCategory.LeftStick, negSettingName: "KbmMouseXNeg"));
            Mappings.Add(new MappingItem("Mouse Y", "KbmMouseY", MappingCategory.LeftStick, negSettingName: "KbmMouseYNeg"));

            // ── Mouse scroll (bidirectional, visualized as Right Stick Y) ──
            Mappings.Add(new MappingItem("Scroll", "KbmScroll", MappingCategory.RightStick, negSettingName: "KbmScrollNeg"));
        }

        /// <summary>
        /// Dynamic vJoy Custom mappings — numbered buttons, sticks, triggers, POVs.
        /// Axis layout interleaves sticks and triggers: [Stick0 X,Y | Trig0 | Stick1 X,Y | Trig1 | ...].
        /// </summary>
        private void InitializeVJoyCustomMappings()
        {
            var cfg = VJoyConfig;
            int stickCount = cfg.ThumbstickCount;
            int triggerCount = cfg.TriggerCount;

            cfg.ComputeAxisLayout(out var stickAxisX, out var stickAxisY, out var triggerAxis);

            // Stick axes (paired)
            for (int i = 0; i < stickCount; i++)
            {
                var cat = i == 0 ? MappingCategory.LeftStick : MappingCategory.RightStick;
                Mappings.Add(new MappingItem($"Stick {i + 1} X", $"VJoyAxis{stickAxisX[i]}", cat, $"VJoyAxis{stickAxisX[i]}Neg"));
                Mappings.Add(new MappingItem($"Stick {i + 1} Y", $"VJoyAxis{stickAxisY[i]}", cat, $"VJoyAxis{stickAxisY[i]}Neg"));
            }

            // Trigger axes (unpaired)
            for (int i = 0; i < triggerCount; i++)
                Mappings.Add(new MappingItem($"Trigger {i + 1}", $"VJoyAxis{triggerAxis[i]}", MappingCategory.Triggers));

            // Buttons
            for (int i = 0; i < cfg.ButtonCount; i++)
                Mappings.Add(new MappingItem($"Button {i + 1}", $"VJoyBtn{i}", MappingCategory.Buttons));

            // POVs
            for (int i = 0; i < cfg.PovCount; i++)
            {
                string label = cfg.PovCount == 1 ? "D-Pad" : $"POV {i + 1}";
                Mappings.Add(new MappingItem($"{label} Up", $"VJoyPov{i}Up", MappingCategory.DPad));
                Mappings.Add(new MappingItem($"{label} Down", $"VJoyPov{i}Down", MappingCategory.DPad));
                Mappings.Add(new MappingItem($"{label} Left", $"VJoyPov{i}Left", MappingCategory.DPad));
                Mappings.Add(new MappingItem($"{label} Right", $"VJoyPov{i}Right", MappingCategory.DPad));
            }
        }

        // ═══════════════════════════════════════════════
        //  Force feedback — unchanged
        // ═══════════════════════════════════════════════

        private int _forceOverallGain = 100;
        public int ForceOverallGain { get => _forceOverallGain; set => SetProperty(ref _forceOverallGain, Math.Clamp(value, 0, 100)); }

        private int _leftMotorStrength = 100;
        public int LeftMotorStrength { get => _leftMotorStrength; set => SetProperty(ref _leftMotorStrength, Math.Clamp(value, 0, 100)); }

        private int _rightMotorStrength = 100;
        public int RightMotorStrength { get => _rightMotorStrength; set => SetProperty(ref _rightMotorStrength, Math.Clamp(value, 0, 100)); }

        private bool _swapMotors;
        public bool SwapMotors { get => _swapMotors; set => SetProperty(ref _swapMotors, value); }

        private ICommand _resetForceAllCommand;
        public ICommand ResetForceAllCommand => _resetForceAllCommand ??= new RelayCommand(() =>
        {
            ForceOverallGain = 100;
            LeftMotorStrength = 100;
            RightMotorStrength = 100;
            SwapMotors = false;
        });

        private ICommand _resetOverallGainCommand;
        public ICommand ResetOverallGainCommand => _resetOverallGainCommand ??= new RelayCommand(() => ForceOverallGain = 100);
        private ICommand _resetLeftMotorCommand;
        public ICommand ResetLeftMotorCommand => _resetLeftMotorCommand ??= new RelayCommand(() => LeftMotorStrength = 100);
        private ICommand _resetRightMotorCommand;
        public ICommand ResetRightMotorCommand => _resetRightMotorCommand ??= new RelayCommand(() => RightMotorStrength = 100);

        private double _leftMotorDisplay;
        public double LeftMotorDisplay { get => _leftMotorDisplay; set => SetProperty(ref _leftMotorDisplay, value); }

        private double _rightMotorDisplay;
        public double RightMotorDisplay { get => _rightMotorDisplay; set => SetProperty(ref _rightMotorDisplay, value); }

        // ═══════════════════════════════════════════════
        //  #2: Expanded dead zone settings
        //  Per-axis X/Y, anti-dead zone, linear, trigger dead zones
        // ═══════════════════════════════════════════════

        /// <summary>
        /// Resets all per-slot settings to defaults. Called when a slot is deleted
        /// so the next controller created in the same slot starts clean.
        /// </summary>
        public void ResetAllSettings()
        {
            ResetDeadZoneSettings();
            LeftSensitivityCurveX = "0,0;1,1";
            LeftSensitivityCurveY = "0,0;1,1";
            RightSensitivityCurveX = "0,0;1,1";
            RightSensitivityCurveY = "0,0;1,1";
            LeftTriggerSensitivityCurve = "0,0;1,1";
            RightTriggerSensitivityCurve = "0,0;1,1";
            ForceOverallGain = 100;
            LeftMotorStrength = 100;
            RightMotorStrength = 100;
        }

        /// <summary>Resets all dead zone, anti-dead zone, linear, and trigger settings to defaults.</summary>
        private void ResetDeadZoneSettings()
        {
            LeftDeadZoneShape = (int)DeadZoneShape.ScaledRadial;
            LeftDeadZoneX = 0; LeftDeadZoneY = 0;
            LeftAntiDeadZoneX = 0; LeftAntiDeadZoneY = 0;
            LeftLinear = 0;
            LeftCenterOffsetX = 0; LeftCenterOffsetY = 0;
            LeftMaxRangeX = 100; LeftMaxRangeY = 100;
            RightDeadZoneShape = (int)DeadZoneShape.ScaledRadial;
            RightDeadZoneX = 0; RightDeadZoneY = 0;
            RightAntiDeadZoneX = 0; RightAntiDeadZoneY = 0;
            RightLinear = 0;
            RightCenterOffsetX = 0; RightCenterOffsetY = 0;
            RightMaxRangeX = 100; RightMaxRangeY = 100;
            LeftTriggerDeadZone = 0; LeftTriggerAntiDeadZone = 0; LeftTriggerMaxRange = 100;
            RightTriggerDeadZone = 0; RightTriggerAntiDeadZone = 0; RightTriggerMaxRange = 100;
        }

        // ── Left Stick ──
        private int _leftDeadZoneShape = (int)DeadZoneShape.ScaledRadial;
        private static readonly int MaxDeadZoneShape = Enum.GetValues(typeof(DeadZoneShape)).Length - 1;
        public int LeftDeadZoneShape { get => _leftDeadZoneShape; set => SetProperty(ref _leftDeadZoneShape, Math.Clamp(value, 0, MaxDeadZoneShape)); }

        private double _leftDeadZoneX;
        public double LeftDeadZoneX { get => _leftDeadZoneX; set => SetProperty(ref _leftDeadZoneX, Math.Clamp(value, 0, 100)); }

        private double _leftDeadZoneY;
        public double LeftDeadZoneY { get => _leftDeadZoneY; set => SetProperty(ref _leftDeadZoneY, Math.Clamp(value, 0, 100)); }

        private double _leftAntiDeadZoneX;
        public double LeftAntiDeadZoneX { get => _leftAntiDeadZoneX; set => SetProperty(ref _leftAntiDeadZoneX, Math.Clamp(value, 0, 100)); }

        private double _leftAntiDeadZoneY;
        public double LeftAntiDeadZoneY { get => _leftAntiDeadZoneY; set => SetProperty(ref _leftAntiDeadZoneY, Math.Clamp(value, 0, 100)); }

        private double _leftLinear;
        public double LeftLinear { get => _leftLinear; set => SetProperty(ref _leftLinear, Math.Clamp(value, 0, 100)); }

        // ── Right Stick ──
        private int _rightDeadZoneShape = (int)DeadZoneShape.ScaledRadial;
        public int RightDeadZoneShape { get => _rightDeadZoneShape; set => SetProperty(ref _rightDeadZoneShape, Math.Clamp(value, 0, MaxDeadZoneShape)); }

        private double _rightDeadZoneX;
        public double RightDeadZoneX { get => _rightDeadZoneX; set => SetProperty(ref _rightDeadZoneX, Math.Clamp(value, 0, 100)); }

        private double _rightDeadZoneY;
        public double RightDeadZoneY { get => _rightDeadZoneY; set => SetProperty(ref _rightDeadZoneY, Math.Clamp(value, 0, 100)); }

        private double _rightAntiDeadZoneX;
        public double RightAntiDeadZoneX { get => _rightAntiDeadZoneX; set => SetProperty(ref _rightAntiDeadZoneX, Math.Clamp(value, 0, 100)); }

        private double _rightAntiDeadZoneY;
        public double RightAntiDeadZoneY { get => _rightAntiDeadZoneY; set => SetProperty(ref _rightAntiDeadZoneY, Math.Clamp(value, 0, 100)); }

        private double _rightLinear;
        public double RightLinear { get => _rightLinear; set => SetProperty(ref _rightLinear, Math.Clamp(value, 0, 100)); }

        // ── Sensitivity Curves (per-axis for sticks, serialized control point strings) ──
        private string _leftSensitivityCurveX = "0,0;1,1";
        public string LeftSensitivityCurveX { get => _leftSensitivityCurveX; set => SetProperty(ref _leftSensitivityCurveX, value ?? "0,0;1,1"); }
        private string _leftSensitivityCurveY = "0,0;1,1";
        public string LeftSensitivityCurveY { get => _leftSensitivityCurveY; set => SetProperty(ref _leftSensitivityCurveY, value ?? "0,0;1,1"); }

        private string _rightSensitivityCurveX = "0,0;1,1";
        public string RightSensitivityCurveX { get => _rightSensitivityCurveX; set => SetProperty(ref _rightSensitivityCurveX, value ?? "0,0;1,1"); }
        private string _rightSensitivityCurveY = "0,0;1,1";
        public string RightSensitivityCurveY { get => _rightSensitivityCurveY; set => SetProperty(ref _rightSensitivityCurveY, value ?? "0,0;1,1"); }

        private string _leftTriggerSensitivityCurve = "0,0;1,1";
        public string LeftTriggerSensitivityCurve { get => _leftTriggerSensitivityCurve; set => SetProperty(ref _leftTriggerSensitivityCurve, value ?? "0,0;1,1"); }

        private string _rightTriggerSensitivityCurve = "0,0;1,1";
        public string RightTriggerSensitivityCurve { get => _rightTriggerSensitivityCurve; set => SetProperty(ref _rightTriggerSensitivityCurve, value ?? "0,0;1,1"); }

        // ── Max Range ──
        private double _leftMaxRangeX = 100;
        public double LeftMaxRangeX { get => _leftMaxRangeX; set => SetProperty(ref _leftMaxRangeX, Math.Clamp(value, 1, 100)); }

        private double _leftMaxRangeY = 100;
        public double LeftMaxRangeY { get => _leftMaxRangeY; set => SetProperty(ref _leftMaxRangeY, Math.Clamp(value, 1, 100)); }

        private double _rightMaxRangeX = 100;
        public double RightMaxRangeX { get => _rightMaxRangeX; set => SetProperty(ref _rightMaxRangeX, Math.Clamp(value, 1, 100)); }

        private double _rightMaxRangeY = 100;
        public double RightMaxRangeY { get => _rightMaxRangeY; set => SetProperty(ref _rightMaxRangeY, Math.Clamp(value, 1, 100)); }

        // ── Max Range (negative direction) ──
        private double _leftMaxRangeXNeg = 100;
        public double LeftMaxRangeXNeg { get => _leftMaxRangeXNeg; set => SetProperty(ref _leftMaxRangeXNeg, Math.Clamp(value, 1, 100)); }

        private double _leftMaxRangeYNeg = 100;
        public double LeftMaxRangeYNeg { get => _leftMaxRangeYNeg; set => SetProperty(ref _leftMaxRangeYNeg, Math.Clamp(value, 1, 100)); }

        private double _rightMaxRangeXNeg = 100;
        public double RightMaxRangeXNeg { get => _rightMaxRangeXNeg; set => SetProperty(ref _rightMaxRangeXNeg, Math.Clamp(value, 1, 100)); }

        private double _rightMaxRangeYNeg = 100;
        public double RightMaxRangeYNeg { get => _rightMaxRangeYNeg; set => SetProperty(ref _rightMaxRangeYNeg, Math.Clamp(value, 1, 100)); }

        // ── Center Offsets ──
        private double _leftCenterOffsetX;
        public double LeftCenterOffsetX { get => _leftCenterOffsetX; set => SetProperty(ref _leftCenterOffsetX, Math.Clamp(value, -100, 100)); }

        private double _leftCenterOffsetY;
        public double LeftCenterOffsetY { get => _leftCenterOffsetY; set => SetProperty(ref _leftCenterOffsetY, Math.Clamp(value, -100, 100)); }

        private double _rightCenterOffsetX;
        public double RightCenterOffsetX { get => _rightCenterOffsetX; set => SetProperty(ref _rightCenterOffsetX, Math.Clamp(value, -100, 100)); }

        private double _rightCenterOffsetY;
        public double RightCenterOffsetY { get => _rightCenterOffsetY; set => SetProperty(ref _rightCenterOffsetY, Math.Clamp(value, -100, 100)); }

        // ── Triggers ──
        private double _leftTriggerDeadZone;
        public double LeftTriggerDeadZone { get => _leftTriggerDeadZone; set => SetProperty(ref _leftTriggerDeadZone, Math.Clamp(value, 0, 100)); }

        private double _rightTriggerDeadZone;
        public double RightTriggerDeadZone { get => _rightTriggerDeadZone; set => SetProperty(ref _rightTriggerDeadZone, Math.Clamp(value, 0, 100)); }

        private double _leftTriggerAntiDeadZone;
        public double LeftTriggerAntiDeadZone { get => _leftTriggerAntiDeadZone; set => SetProperty(ref _leftTriggerAntiDeadZone, Math.Clamp(value, 0, 100)); }

        private double _rightTriggerAntiDeadZone;
        public double RightTriggerAntiDeadZone { get => _rightTriggerAntiDeadZone; set => SetProperty(ref _rightTriggerAntiDeadZone, Math.Clamp(value, 0, 100)); }

        private double _leftTriggerMaxRange = 100;
        public double LeftTriggerMaxRange { get => _leftTriggerMaxRange; set => SetProperty(ref _leftTriggerMaxRange, Math.Clamp(value, 1, 100)); }

        private double _rightTriggerMaxRange = 100;
        public double RightTriggerMaxRange { get => _rightTriggerMaxRange; set => SetProperty(ref _rightTriggerMaxRange, Math.Clamp(value, 1, 100)); }

        // ── Backward compatibility shims ──
        // SettingsService and existing PadPage.xaml use LeftDeadZone/RightDeadZone.
        // Route to both X and Y axes so old code works transparently.
        public double LeftDeadZone
        {
            get => _leftDeadZoneX;
            set { LeftDeadZoneX = value; LeftDeadZoneY = value; }
        }

        public double RightDeadZone
        {
            get => _rightDeadZoneX;
            set { RightDeadZoneX = value; RightDeadZoneY = value; }
        }

        // ═══════════════════════════════════════════════
        //  Dynamic stick/trigger config items for the Sticks and Triggers tabs.
        //  These collections drive the ItemsControl-based dynamic UI.
        //  For gamepad presets: 2 sticks, 2 triggers.
        //  For custom vJoy: N sticks, M triggers.
        // ═══════════════════════════════════════════════

        public ObservableCollection<StickConfigItem> StickConfigs { get; } = new();
        public ObservableCollection<TriggerConfigItem> TriggerConfigs { get; } = new();

        private bool _syncingConfigItems;

        /// <summary>
        /// Rebuilds the StickConfigs collection based on the current output type.
        /// For Xbox 360/DS4 (or vJoy with gamepad preset): always 2 sticks (Left, Right).
        /// For vJoy Custom: N sticks based on ThumbstickCount.
        /// </summary>
        public void RebuildStickConfigs()
        {
            foreach (var item in StickConfigs)
                item.PropertyChanged -= OnStickConfigPropertyChanged;
            StickConfigs.Clear();

            bool isKbm = OutputType == VirtualControllerType.KeyboardMouse;
            if (isKbm)
            {
                // KBM: stick 0 = Mouse X/Y, stick 1 = Scroll Wheel (Y-axis only)
                var mouse = new StickConfigItem(0, "Mouse Movement", -1, -1);
                SyncStickItemFromVm(mouse);
                mouse.PropertyChanged += OnStickConfigPropertyChanged;
                StickConfigs.Add(mouse);

                var scroll = new StickConfigItem(1, "Scroll Wheel", -1, -1);
                SyncStickItemFromVm(scroll);
                scroll.PropertyChanged += OnStickConfigPropertyChanged;
                StickConfigs.Add(scroll);
                return;
            }

            int count = 2; // Default for Xbox 360, DS4, vJoy gamepad presets
            bool isCustomVJoy = OutputType == VirtualControllerType.VJoy && !VJoyConfig.IsGamepadPreset;
            if (isCustomVJoy)
                count = VJoyConfig.ThumbstickCount;

            int[] axX = null, axY = null, trAx = null;
            if (isCustomVJoy && count > 0)
                VJoyConfig.ComputeAxisLayout(out axX, out axY, out trAx);

            for (int i = 0; i < count; i++)
            {
                string title = isCustomVJoy
                    ? $"Stick {i + 1}"
                    : i == 0 ? "Left Thumbstick" : "Right Thumbstick";
                int xiIdx = axX != null ? axX[i] : -1;
                int yiIdx = axY != null ? axY[i] : -1;
                var item = new StickConfigItem(i, title, xiIdx, yiIdx);
                SyncStickItemFromVm(item);
                item.PropertyChanged += OnStickConfigPropertyChanged;
                StickConfigs.Add(item);
            }
        }

        /// <summary>
        /// Rebuilds the TriggerConfigs collection based on the current output type.
        /// For Xbox 360/DS4 (or vJoy with gamepad preset): always 2 triggers (Left, Right).
        /// For vJoy Custom: N triggers based on TriggerCount.
        /// </summary>
        public void RebuildTriggerConfigs()
        {
            foreach (var item in TriggerConfigs)
                item.PropertyChanged -= OnTriggerConfigPropertyChanged;
            TriggerConfigs.Clear();

            // KBM has no triggers — scroll is on Right Stick Y.
            if (OutputType == VirtualControllerType.KeyboardMouse)
                return;

            int count = 2; // Default for Xbox 360, DS4, vJoy gamepad presets
            bool isCustomVJoy = OutputType == VirtualControllerType.VJoy && !VJoyConfig.IsGamepadPreset;
            if (isCustomVJoy)
                count = VJoyConfig.TriggerCount;

            int[] axX = null, axY = null, trAx = null;
            if (isCustomVJoy && count > 0)
                VJoyConfig.ComputeAxisLayout(out axX, out axY, out trAx);

            for (int i = 0; i < count; i++)
            {
                string title = isCustomVJoy
                    ? $"Trigger {i + 1}"
                    : i == 0 ? "Left Trigger" : "Right Trigger";
                int ai = trAx != null ? trAx[i] : -1;
                var item = new TriggerConfigItem(i, title, ai);
                SyncTriggerItemFromVm(item);
                item.PropertyChanged += OnTriggerConfigPropertyChanged;
                TriggerConfigs.Add(item);
            }
        }

        /// <summary>
        /// Pushes current VM dead zone properties into a StickConfigItem.
        /// Called on rebuild and when settings are loaded.
        /// </summary>
        public void SyncStickItemFromVm(StickConfigItem item)
        {
            _syncingConfigItems = true;
            try
            {
                switch (item.Index)
                {
                    case 0:
                        item.DeadZoneShape = (DeadZoneShape)LeftDeadZoneShape;
                        item.DeadZoneX = LeftDeadZoneX;
                        item.DeadZoneY = LeftDeadZoneY;
                        item.AntiDeadZoneX = LeftAntiDeadZoneX;
                        item.AntiDeadZoneY = LeftAntiDeadZoneY;
                        item.Linear = LeftLinear;
                        item.SensitivityCurveX = LeftSensitivityCurveX;
                        item.SensitivityCurveY = LeftSensitivityCurveY;
                        item.MaxRangeX = LeftMaxRangeX;
                        item.MaxRangeY = LeftMaxRangeY;
                        item.MaxRangeXNeg = LeftMaxRangeXNeg;
                        item.MaxRangeYNeg = LeftMaxRangeYNeg;
                        item.CenterOffsetX = LeftCenterOffsetX;
                        item.CenterOffsetY = LeftCenterOffsetY;
                        break;
                    case 1:
                        item.DeadZoneShape = (DeadZoneShape)RightDeadZoneShape;
                        item.DeadZoneX = RightDeadZoneX;
                        item.DeadZoneY = RightDeadZoneY;
                        item.AntiDeadZoneX = RightAntiDeadZoneX;
                        item.AntiDeadZoneY = RightAntiDeadZoneY;
                        item.Linear = RightLinear;
                        item.SensitivityCurveX = RightSensitivityCurveX;
                        item.SensitivityCurveY = RightSensitivityCurveY;
                        item.MaxRangeX = RightMaxRangeX;
                        item.MaxRangeY = RightMaxRangeY;
                        item.MaxRangeXNeg = RightMaxRangeXNeg;
                        item.MaxRangeYNeg = RightMaxRangeYNeg;
                        item.CenterOffsetX = RightCenterOffsetX;
                        item.CenterOffsetY = RightCenterOffsetY;
                        break;
                }
            }
            finally { _syncingConfigItems = false; }
        }

        /// <summary>
        /// Pushes current VM trigger properties into a TriggerConfigItem.
        /// </summary>
        public void SyncTriggerItemFromVm(TriggerConfigItem item)
        {
            _syncingConfigItems = true;
            try
            {
                switch (item.Index)
                {
                    case 0:
                        item.DeadZone = LeftTriggerDeadZone;
                        item.MaxRange = LeftTriggerMaxRange;
                        item.AntiDeadZone = LeftTriggerAntiDeadZone;
                        item.SensitivityCurve = LeftTriggerSensitivityCurve;
                        break;
                    case 1:
                        item.DeadZone = RightTriggerDeadZone;
                        item.MaxRange = RightTriggerMaxRange;
                        item.AntiDeadZone = RightTriggerAntiDeadZone;
                        item.SensitivityCurve = RightTriggerSensitivityCurve;
                        break;
                }
            }
            finally { _syncingConfigItems = false; }
        }

        /// <summary>
        /// Syncs all StickConfigItem values back from current VM properties.
        /// Called after settings are loaded/pasted.
        /// </summary>
        public void SyncAllConfigItemsFromVm()
        {
            foreach (var item in StickConfigs)
                SyncStickItemFromVm(item);
            foreach (var item in TriggerConfigs)
                SyncTriggerItemFromVm(item);
        }

        private void OnStickConfigPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_syncingConfigItems) return;
            if (sender is not StickConfigItem item) return;

            // Sync changed property back to VM
            switch (item.Index)
            {
                case 0:
                    switch (e.PropertyName)
                    {
                        case nameof(StickConfigItem.DeadZoneShape): LeftDeadZoneShape = (int)item.DeadZoneShape; break;
                        case nameof(StickConfigItem.DeadZoneX): LeftDeadZoneX = item.DeadZoneX; break;
                        case nameof(StickConfigItem.DeadZoneY): LeftDeadZoneY = item.DeadZoneY; break;
                        case nameof(StickConfigItem.AntiDeadZoneX): LeftAntiDeadZoneX = item.AntiDeadZoneX; break;
                        case nameof(StickConfigItem.AntiDeadZoneY): LeftAntiDeadZoneY = item.AntiDeadZoneY; break;
                        case nameof(StickConfigItem.Linear): LeftLinear = item.Linear; break;
                        case nameof(StickConfigItem.SensitivityCurveX): LeftSensitivityCurveX = item.SensitivityCurveX; break;
                        case nameof(StickConfigItem.SensitivityCurveY): LeftSensitivityCurveY = item.SensitivityCurveY; break;
                        case nameof(StickConfigItem.MaxRangeX): LeftMaxRangeX = item.MaxRangeX; break;
                        case nameof(StickConfigItem.MaxRangeY): LeftMaxRangeY = item.MaxRangeY; break;
                        case nameof(StickConfigItem.MaxRangeXNeg): LeftMaxRangeXNeg = item.MaxRangeXNeg; break;
                        case nameof(StickConfigItem.MaxRangeYNeg): LeftMaxRangeYNeg = item.MaxRangeYNeg; break;
                        case nameof(StickConfigItem.CenterOffsetX): LeftCenterOffsetX = item.CenterOffsetX; break;
                        case nameof(StickConfigItem.CenterOffsetY): LeftCenterOffsetY = item.CenterOffsetY; break;
                    }
                    ConfigItemDirtyCallback?.Invoke();
                    break;
                case 1:
                    switch (e.PropertyName)
                    {
                        case nameof(StickConfigItem.DeadZoneShape): RightDeadZoneShape = (int)item.DeadZoneShape; break;
                        case nameof(StickConfigItem.DeadZoneX): RightDeadZoneX = item.DeadZoneX; break;
                        case nameof(StickConfigItem.DeadZoneY): RightDeadZoneY = item.DeadZoneY; break;
                        case nameof(StickConfigItem.AntiDeadZoneX): RightAntiDeadZoneX = item.AntiDeadZoneX; break;
                        case nameof(StickConfigItem.AntiDeadZoneY): RightAntiDeadZoneY = item.AntiDeadZoneY; break;
                        case nameof(StickConfigItem.Linear): RightLinear = item.Linear; break;
                        case nameof(StickConfigItem.SensitivityCurveX): RightSensitivityCurveX = item.SensitivityCurveX; break;
                        case nameof(StickConfigItem.SensitivityCurveY): RightSensitivityCurveY = item.SensitivityCurveY; break;
                        case nameof(StickConfigItem.MaxRangeX): RightMaxRangeX = item.MaxRangeX; break;
                        case nameof(StickConfigItem.MaxRangeY): RightMaxRangeY = item.MaxRangeY; break;
                        case nameof(StickConfigItem.MaxRangeXNeg): RightMaxRangeXNeg = item.MaxRangeXNeg; break;
                        case nameof(StickConfigItem.MaxRangeYNeg): RightMaxRangeYNeg = item.MaxRangeYNeg; break;
                        case nameof(StickConfigItem.CenterOffsetX): RightCenterOffsetX = item.CenterOffsetX; break;
                        case nameof(StickConfigItem.CenterOffsetY): RightCenterOffsetY = item.CenterOffsetY; break;
                    }
                    ConfigItemDirtyCallback?.Invoke();
                    break;
                default:
                    // vJoy custom sticks 2+: values stored directly on ConfigItem,
                    // persisted via SettingsService.UpdatePadSettingsFromViewModels.
                    ConfigItemDirtyCallback?.Invoke();
                    break;
            }
        }

        private void OnTriggerConfigPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_syncingConfigItems) return;
            if (sender is not TriggerConfigItem item) return;

            switch (item.Index)
            {
                case 0:
                    switch (e.PropertyName)
                    {
                        case nameof(TriggerConfigItem.DeadZone): LeftTriggerDeadZone = item.DeadZone; break;
                        case nameof(TriggerConfigItem.MaxRange): LeftTriggerMaxRange = item.MaxRange; break;
                        case nameof(TriggerConfigItem.AntiDeadZone): LeftTriggerAntiDeadZone = item.AntiDeadZone; break;
                        case nameof(TriggerConfigItem.SensitivityCurve): LeftTriggerSensitivityCurve = item.SensitivityCurve; break;
                    }
                    ConfigItemDirtyCallback?.Invoke();
                    break;
                case 1:
                    switch (e.PropertyName)
                    {
                        case nameof(TriggerConfigItem.DeadZone): RightTriggerDeadZone = item.DeadZone; break;
                        case nameof(TriggerConfigItem.MaxRange): RightTriggerMaxRange = item.MaxRange; break;
                        case nameof(TriggerConfigItem.AntiDeadZone): RightTriggerAntiDeadZone = item.AntiDeadZone; break;
                        case nameof(TriggerConfigItem.SensitivityCurve): RightTriggerSensitivityCurve = item.SensitivityCurve; break;
                    }
                    ConfigItemDirtyCallback?.Invoke();
                    break;
                default:
                    // vJoy custom triggers 2+: values stored directly on ConfigItem,
                    // persisted via SettingsService.UpdatePadSettingsFromViewModels.
                    ConfigItemDirtyCallback?.Invoke();
                    break;
            }
        }

        // ═══════════════════════════════════════════════
        //  #4: Macro system — foundation
        // ═══════════════════════════════════════════════

        /// <summary>Macros configured for this pad slot.</summary>
        public ObservableCollection<MacroItem> Macros { get; } = new();

        private MacroItem _selectedMacro;

        public MacroItem SelectedMacro
        {
            get => _selectedMacro;
            set
            {
                if (SetProperty(ref _selectedMacro, value))
                {
                    OnPropertyChanged(nameof(HasSelectedMacro));
                    _removeMacroCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        public bool HasSelectedMacro => _selectedMacro != null;

        private RelayCommand _addMacroCommand;
        public RelayCommand AddMacroCommand =>
            _addMacroCommand ??= new RelayCommand(() =>
            {
                var macro = new MacroItem
                {
                    Name = $"Macro {Macros.Count + 1}",
                    ButtonStyle = MacroButtonNames.DeriveStyle(_outputType, _vJoyConfig?.Preset ?? VJoyPreset.Xbox360)
                };
                Macros.Add(macro);
                SelectedMacro = macro;
            });

        private RelayCommand _removeMacroCommand;
        public RelayCommand RemoveMacroCommand =>
            _removeMacroCommand ??= new RelayCommand(() =>
            {
                if (_selectedMacro != null)
                {
                    Macros.Remove(_selectedMacro);
                    SelectedMacro = Macros.LastOrDefault();
                }
            }, () => HasSelectedMacro);

        /// <summary>
        /// Syncs macro button display style to all macros when the output
        /// controller type or vJoy preset changes.
        /// </summary>
        private void SyncMacroButtonStyle()
        {
            var style = MacroButtonNames.DeriveStyle(_outputType, _vJoyConfig?.Preset ?? VJoyPreset.Xbox360);
            int btnCount = (_outputType == VirtualControllerType.VJoy ? _vJoyConfig?.ButtonCount : null) ?? 11;
            foreach (var macro in Macros)
            {
                macro.ButtonStyle = style;
                macro.CustomButtonCount = btnCount;
                foreach (var action in macro.Actions)
                    action.CustomButtonCount = btnCount;
            }
        }

        // ═══════════════════════════════════════════════
        //  Active config tab
        // ═══════════════════════════════════════════════

        private int _selectedConfigTab;

        /// <summary>
        /// 0=Mappings, 1=Left Stick, 2=Right Stick, 3=Triggers, 4=Force Feedback, 5=Macros
        /// </summary>
        public int SelectedConfigTab
        {
            get => _selectedConfigTab;
            set => SetProperty(ref _selectedConfigTab, value);
        }

        // ═══════════════════════════════════════════════
        //  Commands
        // ═══════════════════════════════════════════════

        private RelayCommand _testRumbleCommand;
        public RelayCommand TestRumbleCommand =>
            _testRumbleCommand ??= new RelayCommand(
                () => TestRumbleRequested?.Invoke(this, EventArgs.Empty),
                () => IsDeviceOnline);

        public event EventHandler TestRumbleRequested;

        /// <summary>Raised to test only the left motor.</summary>
        public event EventHandler TestLeftMotorRequested;
        public void FireTestLeftMotor() => TestLeftMotorRequested?.Invoke(this, EventArgs.Empty);

        /// <summary>Raised to test only the right motor.</summary>
        public event EventHandler TestRightMotorRequested;
        public void FireTestRightMotor() => TestRightMotorRequested?.Invoke(this, EventArgs.Empty);

        /// <summary>
        /// The TargetSettingName of the mapping currently being recorded
        /// (single-click or Map All). Null when idle. Used to drive
        /// controller-tab element flashing.
        /// </summary>
        private string _currentRecordingTarget;
        public string CurrentRecordingTarget
        {
            get => _currentRecordingTarget;
            set => SetProperty(ref _currentRecordingTarget, value);
        }

        private RelayCommand _clearMappingsCommand;
        public RelayCommand ClearMappingsCommand =>
            _clearMappingsCommand ??= new RelayCommand(ClearAllMappings);

        private void ClearAllMappings()
        {
            foreach (var m in Mappings)
            {
                m.SourceDescriptor = string.Empty;
                m.NegSourceDescriptor = string.Empty;
                m.IsInverted = false;
                m.IsHalfAxis = false;
            }
        }

        // ── Copy / Paste / Copy From ──

        /// <summary>
        /// Raised when the user wants to copy the current device's settings to the clipboard.
        /// MainWindow/InputService handles reading the PadSetting and calling ToJson().
        /// </summary>
        public event EventHandler CopySettingsRequested;

        private RelayCommand _copySettingsCommand;
        public RelayCommand CopySettingsCommand =>
            _copySettingsCommand ??= new RelayCommand(
                () => CopySettingsRequested?.Invoke(this, EventArgs.Empty),
                () => HasSelectedDevice);

        /// <summary>
        /// Raised when the user wants to paste settings from the clipboard.
        /// MainWindow/InputService handles parsing and applying.
        /// </summary>
        public event EventHandler PasteSettingsRequested;

        private RelayCommand _pasteSettingsCommand;
        public RelayCommand PasteSettingsCommand =>
            _pasteSettingsCommand ??= new RelayCommand(
                () => PasteSettingsRequested?.Invoke(this, EventArgs.Empty),
                () => HasSelectedDevice);

        /// <summary>
        /// Raised when the user wants to copy settings from another device.
        /// MainWindow handles showing the picker dialog.
        /// </summary>
        public event EventHandler CopyFromRequested;

        private RelayCommand _copyFromCommand;
        public RelayCommand CopyFromCommand =>
            _copyFromCommand ??= new RelayCommand(
                () => CopyFromRequested?.Invoke(this, EventArgs.Empty),
                () => HasSelectedDevice);

        // ── Map All ──

        /// <summary>Raised to request recording for the current Map All item.</summary>
        public event EventHandler<MappingItem> MapAllRecordRequested;

        /// <summary>Raised to cancel an in-progress Map All recording.</summary>
        public event EventHandler MapAllCancelRequested;

        private bool _isMapAllActive;
        public bool IsMapAllActive
        {
            get => _isMapAllActive;
            set => SetProperty(ref _isMapAllActive, value);
        }

        /// <summary>When true, the current Map All step is recording the negative direction of an axis.</summary>
        internal bool MapAllRecordingNeg { get; set; }

        private int _mapAllCurrentIndex;
        public int MapAllCurrentIndex
        {
            get => _mapAllCurrentIndex;
            set => SetProperty(ref _mapAllCurrentIndex, value);
        }

        private string _mapAllCurrentTarget;
        public string MapAllCurrentTarget
        {
            get => _mapAllCurrentTarget;
            set => SetProperty(ref _mapAllCurrentTarget, value);
        }

        private string _mapAllPromptText;
        /// <summary>Descriptive text shown on the Controller tab during Map All (e.g., "Press: A").</summary>
        public string MapAllPromptText
        {
            get => _mapAllPromptText;
            set => SetProperty(ref _mapAllPromptText, value);
        }

        /// <summary>Timer used to add a short delay between Map All entries.</summary>
        private DispatcherTimer _mapAllDelayTimer;

        private RelayCommand _mapAllCommand;
        public RelayCommand MapAllCommand =>
            _mapAllCommand ??= new RelayCommand(StartMapAll, () => HasSelectedDevice && !IsMapAllActive && SelectedMappedDevice?.IsOnline == true);

        private void StartMapAll()
        {
            if (Mappings.Count == 0) return;
            IsMapAllActive = true;
            MapAllCurrentIndex = 0;
            _mapAllCommand?.NotifyCanExecuteChanged();
            AdvanceMapAll();
        }

        private void AdvanceMapAll()
        {
            if (!IsMapAllActive) return;

            if (MapAllCurrentIndex >= Mappings.Count)
            {
                StopMapAll();
                return;
            }

            var mapping = Mappings[MapAllCurrentIndex];

            // Switch to Controller tab (index 0) for stick axes so the 3D arrow is visible.
            // The 3D model is only on the Controller tab; if Map All was started from the
            // Mappings tab, the user wouldn't see the directional arrows otherwise.
            if (mapping.HasNegDirection)
                SelectedConfigTab = 0;

            // Detect Y axis: standard controllers use "AxisY" in the setting name,
            // custom vJoy uses "Stick N Y" in the label (setting name is "VJoyAxisN").
            bool isYAxis = mapping.TargetSettingName.Contains("AxisY")
                        || mapping.TargetLabel.EndsWith(" Y", StringComparison.Ordinal);

            if (MapAllRecordingNeg)
            {
                // Second phase: opposite direction from the first.
                // X: second=left (neg). Y: second=down (pos, because NegateAxis inverts).
                // Keep MapAllRecordingNeg=true until MapAllRecordRequested fires, so the
                // handler can distinguish Y second phase from Y first phase.
                string dirHint = isYAxis ? "(\u2193)" : "(\u2190)";
                // Y: second phase targets pos descriptor (down in game).
                // X: second phase targets neg descriptor (left).
                string target = isYAxis ? mapping.TargetSettingName : mapping.NegSettingName;
                MapAllCurrentTarget = target;
                CurrentRecordingTarget = target;
                MapAllPromptText = $"Map: {mapping.TargetLabel} {dirHint}  ({MapAllCurrentIndex + 1}/{Mappings.Count})";
            }
            else
            {
                string suffix = "";
                if (mapping.HasNegDirection)
                {
                    // First phase: natural primary direction.
                    // X: first=right (pos). Y: first=up (neg, because NegateAxis inverts).
                    suffix = isYAxis ? " (\u2191)" : " (\u2192)";
                }
                // Y: first phase targets neg descriptor (up in game).
                // X: first phase targets pos descriptor (right).
                string target = (mapping.HasNegDirection && isYAxis) ? mapping.NegSettingName : mapping.TargetSettingName;
                MapAllCurrentTarget = target;
                CurrentRecordingTarget = target;
                MapAllPromptText = $"Map: {mapping.TargetLabel}{suffix}  ({MapAllCurrentIndex + 1}/{Mappings.Count})";
            }
            MapAllRecordRequested?.Invoke(this, mapping);

            // Clear after firing so OnMapAllItemCompleted will advance the index
            // when the second-phase recording finishes.
            if (MapAllRecordingNeg)
                MapAllRecordingNeg = false;
        }

        /// <summary>Called when a Map All recording completes (success or timeout). Advances to next after a short delay.</summary>
        public void OnMapAllItemCompleted()
        {
            if (!IsMapAllActive) return;

            // Short delay so analog input (axis return to center) doesn't
            // accidentally trigger the next recording.
            _mapAllDelayTimer?.Stop();
            _mapAllDelayTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _mapAllDelayTimer.Tick += (s, e) =>
            {
                _mapAllDelayTimer.Stop();
                _mapAllDelayTimer = null;
                if (!IsMapAllActive) return;

                // MapAllRecordingNeg=true means the first phase just finished and
                // a second phase is needed at the same index.  Stay on the same
                // mapping and let AdvanceMapAll show the opposite-direction prompt.
                if (!MapAllRecordingNeg)
                {
                    MapAllCurrentIndex++;
                }
                AdvanceMapAll();
            };
            _mapAllDelayTimer.Start();
        }

        public void StopMapAll()
        {
            _mapAllDelayTimer?.Stop();
            _mapAllDelayTimer = null;
            IsMapAllActive = false;
            MapAllRecordingNeg = false;
            MapAllCurrentTarget = null;
            CurrentRecordingTarget = null;
            MapAllPromptText = null;
            MapAllCancelRequested?.Invoke(this, EventArgs.Empty);
            _mapAllCommand?.NotifyCanExecuteChanged();
        }

        // ═══════════════════════════════════════════════
        //  State update (30Hz from InputService)
        // ═══════════════════════════════════════════════

        public void UpdateFromEngineState(Gamepad gp, Engine.Vibration vibration)
        {
            ButtonA = gp.IsButtonPressed(Gamepad.A);
            ButtonB = gp.IsButtonPressed(Gamepad.B);
            ButtonX = gp.IsButtonPressed(Gamepad.X);
            ButtonY = gp.IsButtonPressed(Gamepad.Y);
            LeftShoulder = gp.IsButtonPressed(Gamepad.LEFT_SHOULDER);
            RightShoulder = gp.IsButtonPressed(Gamepad.RIGHT_SHOULDER);
            ButtonBack = gp.IsButtonPressed(Gamepad.BACK);
            ButtonStart = gp.IsButtonPressed(Gamepad.START);
            LeftThumbButton = gp.IsButtonPressed(Gamepad.LEFT_THUMB);
            RightThumbButton = gp.IsButtonPressed(Gamepad.RIGHT_THUMB);
            ButtonGuide = gp.IsButtonPressed(Gamepad.GUIDE);
            DPadUp = gp.IsButtonPressed(Gamepad.DPAD_UP);
            DPadDown = gp.IsButtonPressed(Gamepad.DPAD_DOWN);
            DPadLeft = gp.IsButtonPressed(Gamepad.DPAD_LEFT);
            DPadRight = gp.IsButtonPressed(Gamepad.DPAD_RIGHT);

            RawLeftTrigger = gp.LeftTrigger;
            RawRightTrigger = gp.RightTrigger;
            LeftTrigger = gp.LeftTrigger / 65535.0;
            RightTrigger = gp.RightTrigger / 65535.0;

            RawThumbLX = gp.ThumbLX;
            RawThumbLY = gp.ThumbLY;
            RawThumbRX = gp.ThumbRX;
            RawThumbRY = gp.ThumbRY;
            ThumbLX = (gp.ThumbLX - (double)short.MinValue) / 65535.0;
            ThumbLY = 1.0 - ((gp.ThumbLY - (double)short.MinValue) / 65535.0);
            ThumbRX = (gp.ThumbRX - (double)short.MinValue) / 65535.0;
            ThumbRY = 1.0 - ((gp.ThumbRY - (double)short.MinValue) / 65535.0);

            if (vibration != null)
            {
                // Apply FFB scaling so the motor bars reflect what the physical controller receives.
                double overallFactor = ForceOverallGain / 100.0;
                double leftFactor = LeftMotorStrength / 100.0;
                double rightFactor = RightMotorStrength / 100.0;
                double rawL = vibration.LeftMotorSpeed / 65535.0 * leftFactor * overallFactor;
                double rawR = vibration.RightMotorSpeed / 65535.0 * rightFactor * overallFactor;
                if (SwapMotors)
                    (rawL, rawR) = (rawR, rawL);
                LeftMotorDisplay = rawL;
                RightMotorDisplay = rawR;
            }
        }

        /// <summary>
        /// Updates per-device stick/trigger values for the stick and trigger tab previews.
        /// Shows only the selected device's input, not the combined slot.
        /// Also syncs live values to StickConfigs/TriggerConfigs items.
        /// </summary>
        public void UpdateDeviceState(Gamepad gp)
        {
            DeviceRawLeftTrigger = gp.LeftTrigger;
            DeviceRawRightTrigger = gp.RightTrigger;
            DeviceLeftTrigger = gp.LeftTrigger / 65535.0;
            DeviceRightTrigger = gp.RightTrigger / 65535.0;

            DeviceRawThumbLX = gp.ThumbLX;
            DeviceRawThumbLY = gp.ThumbLY;
            DeviceRawThumbRX = gp.ThumbRX;
            DeviceRawThumbRY = gp.ThumbRY;
            DeviceThumbLX = (gp.ThumbLX - (double)short.MinValue) / 65535.0;
            DeviceThumbLY = 1.0 - ((gp.ThumbLY - (double)short.MinValue) / 65535.0);
            DeviceThumbRX = (gp.ThumbRX - (double)short.MinValue) / 65535.0;
            DeviceThumbRY = 1.0 - ((gp.ThumbRY - (double)short.MinValue) / 65535.0);

            // Sync live values to dynamic config items (full processing pipeline for preview)
            if (StickConfigs.Count > 0)
            {
                var (lvx, lox, lvy, loy) = ProcessStickForPreview(
                    DeviceThumbLX + LeftCenterOffsetX / 200.0,
                    DeviceThumbLY - LeftCenterOffsetY / 200.0,
                    LeftDeadZoneX, LeftDeadZoneY,
                    LeftAntiDeadZoneX, LeftAntiDeadZoneY,
                    LeftLinear, LeftMaxRangeX, LeftMaxRangeY,
                    LeftMaxRangeXNeg, LeftMaxRangeYNeg,
                    LeftSensitivityCurveX, LeftSensitivityCurveY,
                    (DeadZoneShape)LeftDeadZoneShape);
                StickConfigs[0].LiveX = lvx;
                StickConfigs[0].LiveY = lvy;
                StickConfigs[0].RawX = (short)Math.Clamp((lox - 0.5) * 2.0 * 32767, short.MinValue, short.MaxValue);
                StickConfigs[0].RawY = (short)Math.Clamp((0.5 - loy) * 2.0 * 32767, short.MinValue, short.MaxValue);
                StickConfigs[0].HardwareRawX = gp.ThumbLX;
                StickConfigs[0].HardwareRawY = gp.ThumbLY;
                UpdateStickCurveDots(StickConfigs[0], DeviceThumbLX, DeviceThumbLY);
            }
            if (StickConfigs.Count > 1)
            {
                var (rvx, rox, rvy, roy) = ProcessStickForPreview(
                    DeviceThumbRX + RightCenterOffsetX / 200.0,
                    DeviceThumbRY - RightCenterOffsetY / 200.0,
                    RightDeadZoneX, RightDeadZoneY,
                    RightAntiDeadZoneX, RightAntiDeadZoneY,
                    RightLinear, RightMaxRangeX, RightMaxRangeY,
                    RightMaxRangeXNeg, RightMaxRangeYNeg,
                    RightSensitivityCurveX, RightSensitivityCurveY,
                    (DeadZoneShape)RightDeadZoneShape);
                StickConfigs[1].LiveX = rvx;
                StickConfigs[1].LiveY = rvy;
                StickConfigs[1].RawX = (short)Math.Clamp((rox - 0.5) * 2.0 * 32767, short.MinValue, short.MaxValue);
                StickConfigs[1].RawY = (short)Math.Clamp((0.5 - roy) * 2.0 * 32767, short.MinValue, short.MaxValue);
                StickConfigs[1].HardwareRawX = gp.ThumbRX;
                StickConfigs[1].HardwareRawY = gp.ThumbRY;
                UpdateStickCurveDots(StickConfigs[1], DeviceThumbRX, DeviceThumbRY);
            }
            if (TriggerConfigs.Count > 0)
            {
                var processed = ProcessTriggerForPreview(DeviceLeftTrigger, TriggerConfigs[0]);
                TriggerConfigs[0].LiveValue = processed;
                TriggerConfigs[0].RawValue = (ushort)Math.Clamp((int)(processed * 65535), 0, 65535);
                UpdateTriggerCurveDot(TriggerConfigs[0], DeviceLeftTrigger);
            }
            if (TriggerConfigs.Count > 1)
            {
                var processed = ProcessTriggerForPreview(DeviceRightTrigger, TriggerConfigs[1]);
                TriggerConfigs[1].LiveValue = processed;
                TriggerConfigs[1].RawValue = (ushort)Math.Clamp((int)(processed * 65535), 0, 65535);
                UpdateTriggerCurveDot(TriggerConfigs[1], DeviceRightTrigger);
            }
        }

        /// <summary>
        /// Processes both stick axes together through the shape-aware dead zone pipeline.
        /// Uses the same algorithms as Step3's ApplyDeadZone for preview consistency.
        /// </summary>
        private static (double visualX, double outputX, double visualY, double outputY)
            ProcessStickForPreview(
                double adjNormX, double adjNormY,
                double deadZoneX, double deadZoneY,
                double antiDeadZoneX, double antiDeadZoneY,
                double linear, double maxRangeX, double maxRangeY,
                double maxRangeXNeg, double maxRangeYNeg,
                string curveX, string curveY,
                DeadZoneShape shape)
        {
            // Convert to signed [-1, 1]
            double sx = (adjNormX - 0.5) * 2.0;
            double sy = (adjNormY - 0.5) * 2.0;
            double signX = Math.Sign(sx), signY = Math.Sign(sy);
            double magX = Math.Abs(sx), magY = Math.Abs(sy);
            double dzXn = deadZoneX / 100.0, dzYn = deadZoneY / 100.0;
            // Pick max range based on direction of input (mirrors Step3 pipeline).
            double mrXn = (sx >= 0 ? maxRangeX : maxRangeXNeg) / 100.0;
            double mrYn = (sy >= 0 ? maxRangeY : maxRangeYNeg) / 100.0;
            if (mrXn <= dzXn) mrXn = Math.Min(dzXn + 0.01, 1.0);
            if (mrYn <= dzYn) mrYn = Math.Min(dzYn + 0.01, 1.0);

            // ── Axial: cross-shaped DZ visualization ──
            if (shape == DeadZoneShape.Axial)
            {
                bool xInDz = magX < dzXn, yInDz = magY < dzYn;

                // Center rectangle (both in DZ) → dot at center.
                if (xInDz && yInDz)
                    return (0.5, 0.5, 0.5, 0.5);

                // Per-axis DZ gate + rescale (mirrors ApplySingleDeadZone).
                double remAx = xInDz ? 0 : Math.Min((magX - dzXn) / (mrXn - dzXn), 1.0);
                double remAy = yInDz ? 0 : Math.Min((magY - dzYn) / (mrYn - dzYn), 1.0);
                double oAx = PostDzForPreview(remAx, curveX, antiDeadZoneX, linear);
                double oAy = PostDzForPreview(remAy, curveY, antiDeadZoneY, linear);

                double outPosX = Math.Clamp(0.5 + signX * oAx * 0.5, 0.0, 1.0);
                double outPosY = Math.Clamp(0.5 + signY * oAy * 0.5, 0.0, 1.0);

                // Visual: each axis jumps to its DZ boundary, scales outward.
                // In the cross arms, the zeroed axis stays at center (snapped to axis).
                // In the corners, both jump to boundary.
                double visAx = xInDz ? 0.0 : dzXn + oAx * (1.0 - dzXn);
                double visAy = yInDz ? 0.0 : dzYn + oAy * (1.0 - dzYn);
                double visPosX = xInDz ? 0.5 : Math.Clamp(0.5 + signX * visAx * 0.5, 0.0, 1.0);
                double visPosY = yInDz ? 0.5 : Math.Clamp(0.5 + signY * visAy * 0.5, 0.0, 1.0);

                return (visPosX, outPosX, visPosY, outPosY);
            }

            // ── 2D shapes (Radial, Sloped, Hybrid) ──
            double remX, remY;
            switch (shape)
            {
                case DeadZoneShape.Radial:
                    Common.Input.InputManager.ComputeRadial(sx, sy, magX, magY, dzXn, dzYn, mrXn, mrYn, false, out remX, out remY);
                    break;
                case DeadZoneShape.ScaledRadial:
                    Common.Input.InputManager.ComputeRadial(sx, sy, magX, magY, dzXn, dzYn, mrXn, mrYn, true, out remX, out remY);
                    break;
                case DeadZoneShape.SlopedAxial:
                    Common.Input.InputManager.ComputeSloped(magX, magY, dzXn, dzYn, mrXn, mrYn, false, out remX, out remY);
                    break;
                case DeadZoneShape.SlopedScaledAxial:
                    Common.Input.InputManager.ComputeSloped(magX, magY, dzXn, dzYn, mrXn, mrYn, true, out remX, out remY);
                    break;
                case DeadZoneShape.Hybrid:
                    Common.Input.InputManager.ComputeHybrid(sx, sy, magX, magY, dzXn, dzYn, mrXn, mrYn, out remX, out remY, out signX, out signY);
                    break;
                default:
                    remX = magX; remY = magY;
                    break;
            }

            // Post-DZ per axis: curve → ADZ → linear (mirrors ApplyPostDeadZone)
            double outX = PostDzForPreview(remX, curveX, antiDeadZoneX, linear);
            double outY = PostDzForPreview(remY, curveY, antiDeadZoneY, linear);

            double outputPosX = Math.Clamp(0.5 + signX * outX * 0.5, 0.0, 1.0);
            double outputPosY = Math.Clamp(0.5 + signY * outY * 0.5, 0.0, 1.0);

            // ── Shape-specific visual mapping ──
            // Principle: dot at center inside red zones, axis-constrained in yellow zones,
            // and jumps to zone boundary when exiting (never appears inside a colored zone).

            const double visEps = 1e-10;

            // ── Sloped Axial (non-scaled): output position directly ──
            // Natural boundary at wedge edge (raw magnitude ≈ effDz at boundary).
            if (shape == DeadZoneShape.SlopedAxial)
            {
                bool xZeroed = magX < dzXn * magY;
                bool yZeroed = magY < dzYn * magX;
                double visX = xZeroed ? 0.5 : outputPosX;
                double visY = yZeroed ? 0.5 : outputPosY;
                return (visX, outputPosX, visY, outputPosY);
            }

            // ── Sloped Scaled Axial: wedge boundary jump ──
            // Rescaled output starts from 0 — jump to wedge edge like Scaled Radial
            // jumps to circle edge.
            if (shape == DeadZoneShape.SlopedScaledAxial)
            {
                bool xZeroed = magX < dzXn * magY;
                bool yZeroed = magY < dzYn * magX;
                double visX, visY;
                if (xZeroed)
                    visX = 0.5;
                else
                {
                    double effDz = dzXn * magY;
                    double vis = effDz + outX * (1.0 - effDz);
                    visX = Math.Clamp(0.5 + signX * vis * 0.5, 0.0, 1.0);
                }
                if (yZeroed)
                    visY = 0.5;
                else
                {
                    double effDz = dzYn * magX;
                    double vis = effDz + outY * (1.0 - effDz);
                    visY = Math.Clamp(0.5 + signY * vis * 0.5, 0.0, 1.0);
                }
                return (visX, outputPosX, visY, outputPosY);
            }

            // ── Scaled Radial: radial boundary jump ──
            if (shape == DeadZoneShape.ScaledRadial)
            {
                double eDzX = Math.Max(dzXn, visEps), eDzY = Math.Max(dzYn, visEps);
                double edx = sx / eDzX, edy = sy / eDzY;
                if (edx * edx + edy * edy < 1.0)
                    return (0.5, outputPosX, 0.5, outputPosY);

                // DZ boundary radius in the direction of the stick.
                double rawMag = Math.Sqrt(magX * magX + magY * magY);
                if (rawMag < visEps)
                    return (0.5, outputPosX, 0.5, outputPosY);
                double ux = magX / rawMag, uy = magY / rawMag;
                double dxu = ux / eDzX, dyu = uy / eDzY;
                double dzR = 1.0 / Math.Sqrt(dxu * dxu + dyu * dyu);

                // Map output magnitude [0,max] → visual [dzR, 1] so dot starts at circle edge.
                double outMag = Math.Sqrt(outX * outX + outY * outY);
                double visMag = dzR + outMag * (1.0 - dzR);

                double visX = Math.Clamp(0.5 + signX * ux * visMag * 0.5, 0.0, 1.0);
                double visY = Math.Clamp(0.5 + signY * uy * visMag * 0.5, 0.0, 1.0);
                return (visX, outputPosX, visY, outputPosY);
            }

            // ── Hybrid: circle (red center) + wedge (yellow axis-snap) ──
            if (shape == DeadZoneShape.Hybrid)
            {
                double eDzX = Math.Max(dzXn, visEps), eDzY = Math.Max(dzYn, visEps);
                double edx = sx / eDzX, edy = sy / eDzY;
                if (edx * edx + edy * edy < 1.0)
                    return (0.5, outputPosX, 0.5, outputPosY);

                // DZ boundary radius in the direction of the stick.
                double rawMag = Math.Sqrt(magX * magX + magY * magY);
                if (rawMag < visEps)
                    return (0.5, outputPosX, 0.5, outputPosY);
                double ux = magX / rawMag, uy = magY / rawMag;
                double dxu = ux / eDzX, dyu = uy / eDzY;
                double dzR = 1.0 / Math.Sqrt(dxu * dxu + dyu * dyu);

                // Check wedge conditions from the sloped stage.
                Common.Input.InputManager.ComputeRadial(sx, sy, magX, magY, dzXn, dzYn, mrXn, mrYn,
                    true, out double srX, out double srY);
                bool xZeroed = srX < dzXn * srY;
                bool yZeroed = srY < dzYn * srX;

                if (xZeroed || yZeroed)
                {
                    // Wedge zone: zeroed axis at center, alive axis jumps to circle edge.
                    double visX = xZeroed ? 0.5
                        : Math.Clamp(0.5 + signX * (dzXn + outX * (1.0 - dzXn)) * 0.5, 0.0, 1.0);
                    double visY = yZeroed ? 0.5
                        : Math.Clamp(0.5 + signY * (dzYn + outY * (1.0 - dzYn)) * 0.5, 0.0, 1.0);
                    return (visX, outputPosX, visY, outputPosY);
                }

                // Free zone: radial boundary jump in stick direction.
                double outMag = Math.Sqrt(outX * outX + outY * outY);
                double visMag = dzR + outMag * (1.0 - dzR);
                double vfX = Math.Clamp(0.5 + signX * ux * visMag * 0.5, 0.0, 1.0);
                double vfY = Math.Clamp(0.5 + signY * uy * visMag * 0.5, 0.0, 1.0);
                return (vfX, outputPosX, vfY, outputPosY);
            }

            // ── Radial (non-scaled): output position directly ──
            // Natural boundary jump: raw magnitude at DZ edge ≈ DZ radius.
            return (outputPosX, outputPosX, outputPosY, outputPosY);
        }

        private static double PostDzForPreview(double remapped, string curveString, double antiDeadZone, double linear)
        {
            if (remapped <= 0 && antiDeadZone <= 0) return 0;
            remapped = StickConfigItem.ApplyCurve(remapped, curveString);
            double adzNorm = antiDeadZone / 100.0;
            double output = adzNorm + remapped * (1.0 - adzNorm);
            if (linear > 0)
            {
                double lf = linear / 100.0;
                output = remapped * lf + output * (1.0 - lf);
            }
            return output;
        }

        /// <summary>
        /// <summary>
        /// Updates the CurveEditor live input values for a stick config item.
        /// normX/normY are 0-1 normalized where 0.5 = center.
        /// CurveEditor handles the dot rendering internally.
        /// </summary>
        private static void UpdateStickCurveDots(StickConfigItem stick, double normX, double normY)
        {
            // Signed input for the CurveEditor LiveInput property
            double signedX = (normX - 0.5) * 2.0;
            double signedY = -((normY - 0.5) * 2.0);
            stick.LiveInputX = signedX;
            stick.LiveInputY = signedY;
        }

        private static void UpdateTriggerCurveDot(TriggerConfigItem trig, double inputNorm)
        {
            trig.LiveInputForCurve = Math.Clamp(inputNorm, 0, 1);
        }

        /// <summary>
        /// Applies the trigger processing pipeline (dead zone, max range, curve, anti-dead zone)
        /// to a raw 0–1 trigger value for preview display. Mirrors Step3's ApplyTriggerDeadZone.
        /// </summary>
        private static double ProcessTriggerForPreview(double rawNorm, TriggerConfigItem trig)
        {
            double t = Math.Clamp(rawNorm, 0, 1);
            double dz = trig.DeadZone / 100.0;
            double mr = trig.MaxRange / 100.0;
            if (mr <= dz) mr = dz + 0.01;

            if (t < dz) return 0;

            double remapped = Math.Min((t - dz) / (mr - dz), 1.0);
            double output = StickConfigItem.ApplyCurve(remapped, trig.SensitivityCurve);

            // Anti-dead zone: offset the output minimum
            double adz = trig.AntiDeadZone / 100.0;
            if (adz > 0)
                output = adz + output * (1.0 - adz);

            return Math.Clamp(output, 0, 1);
        }

        // ═══════════════════════════════════════════════
        //  VJoy raw state snapshot (for custom vJoy schematic view)
        // ═══════════════════════════════════════════════

        /// <summary>
        /// Latest VJoyRawState snapshot for custom vJoy display.
        /// Updated at 30Hz alongside UpdateFromEngineState.
        /// </summary>
        public VJoyRawState VJoyOutputSnapshot { get; private set; }

        /// <summary>
        /// Latest KbmRawState snapshot for KBM preview display.
        /// Updated at 30Hz alongside UpdateFromEngineState.
        /// </summary>
        public KbmRawState KbmOutputSnapshot { get; set; }

        /// <summary>
        /// Updates the combined output display from a VJoyRawState (custom vJoy slots).
        /// Syncs live values to StickConfigs/TriggerConfigs and stores the raw snapshot.
        /// </summary>
        public void UpdateFromVJoyRawState(VJoyRawState raw)
        {
            VJoyOutputSnapshot = raw;

            // Sync stick config items from raw axes
            foreach (var stick in StickConfigs)
            {
                bool hasX = stick.AxisXIndex >= 0 && raw.Axes != null && stick.AxisXIndex < raw.Axes.Length;
                bool hasY = stick.AxisYIndex >= 0 && raw.Axes != null && stick.AxisYIndex < raw.Axes.Length;

                if (hasX) stick.HardwareRawX = raw.Axes[stick.AxisXIndex];
                if (hasY) stick.HardwareRawY = raw.Axes[stick.AxisYIndex];

                double normX = hasX ? (raw.Axes[stick.AxisXIndex] - (double)short.MinValue) / 65535.0 : 0.5;
                double normY = hasY ? (raw.Axes[stick.AxisYIndex] - (double)short.MinValue) / 65535.0 : 0.5;

                var (vx, ox, vy, oy) = ProcessStickForPreview(
                    normX + stick.CenterOffsetX / 200.0,
                    normY - stick.CenterOffsetY / 200.0,
                    stick.DeadZoneX, stick.DeadZoneY,
                    stick.AntiDeadZoneX, stick.AntiDeadZoneY,
                    stick.Linear, stick.MaxRangeX, stick.MaxRangeY,
                    stick.MaxRangeXNeg, stick.MaxRangeYNeg,
                    stick.SensitivityCurveX, stick.SensitivityCurveY,
                    stick.DeadZoneShape);

                if (hasX) { stick.LiveX = vx; stick.RawX = (short)Math.Clamp((ox - 0.5) * 2.0 * 32767, short.MinValue, short.MaxValue); }
                if (hasY) { stick.LiveY = vy; stick.RawY = (short)Math.Clamp((0.5 - oy) * 2.0 * 32767, short.MinValue, short.MaxValue); }

                UpdateStickCurveDots(stick, stick.LiveX, stick.LiveY);
            }

            // Sync trigger config items from raw axes
            foreach (var trig in TriggerConfigs)
            {
                if (trig.AxisIndex >= 0 && raw.Axes != null && trig.AxisIndex < raw.Axes.Length)
                {
                    // Trigger axes are signed short (-32768..32767), normalize to 0.0-1.0
                    double rawNorm = (raw.Axes[trig.AxisIndex] - (double)short.MinValue) / 65535.0;
                    var processed = ProcessTriggerForPreview(rawNorm, trig);
                    trig.LiveValue = processed;
                    trig.RawValue = (ushort)Math.Clamp((int)(processed * 65535), 0, 65535);
                    UpdateTriggerCurveDot(trig, rawNorm);
                }
            }

            OnPropertyChanged(nameof(VJoyOutputSnapshot));
        }

        // ═══════════════════════════════════════════════
        //  MIDI raw state snapshot (for MIDI preview view)
        // ═══════════════════════════════════════════════

        public MidiRawState MidiOutputSnapshot { get; private set; }

        public void UpdateFromMidiRawState(MidiRawState raw)
        {
            MidiOutputSnapshot = raw;
            OnPropertyChanged(nameof(MidiOutputSnapshot));
        }

        public void RefreshCommands()
        {
            _testRumbleCommand?.NotifyCanExecuteChanged();
            _removeMacroCommand?.NotifyCanExecuteChanged();
            _copySettingsCommand?.NotifyCanExecuteChanged();
            _pasteSettingsCommand?.NotifyCanExecuteChanged();
            _copyFromCommand?.NotifyCanExecuteChanged();
            _mapAllCommand?.NotifyCanExecuteChanged();
        }
    }
}
