using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common.Input;
using PadForge.Engine;

namespace PadForge.ViewModels
{
    /// <summary>
    /// ViewModel for a single virtual controller slot (one of 4 pads).
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
            Title = $"Controller {padIndex + 1}";
            SlotLabel = $"Controller {padIndex + 1}";
            InitializeDefaultMappings();
        }

        /// <summary>Zero-based pad slot index (0–3).</summary>
        public int PadIndex { get; }

        /// <summary>Display label (e.g., "Player 1").</summary>
        public string SlotLabel { get; }

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
                if (SetProperty(ref _selectedMappedDevice, value))
                {
                    OnPropertyChanged(nameof(HasSelectedDevice));
                    SelectedDeviceChanged?.Invoke(this, value);
                }
            }
        }

        /// <summary>Whether a device is selected for configuration.</summary>
        public bool HasSelectedDevice => _selectedMappedDevice != null;

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

        private byte _rawLeftTrigger;
        public byte RawLeftTrigger { get => _rawLeftTrigger; set => SetProperty(ref _rawLeftTrigger, value); }

        private byte _rawRightTrigger;
        public byte RawRightTrigger { get => _rawRightTrigger; set => SetProperty(ref _rawRightTrigger, value); }

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

        private byte _deviceRawLeftTrigger;
        public byte DeviceRawLeftTrigger { get => _deviceRawLeftTrigger; set => SetProperty(ref _deviceRawLeftTrigger, value); }

        private byte _deviceRawRightTrigger;
        public byte DeviceRawRightTrigger { get => _deviceRawRightTrigger; set => SetProperty(ref _deviceRawRightTrigger, value); }

        // ═══════════════════════════════════════════════
        //  Mapping rows — unchanged
        // ═══════════════════════════════════════════════

        public ObservableCollection<MappingItem> Mappings { get; } =
            new ObservableCollection<MappingItem>();

        private void InitializeDefaultMappings()
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
            Mappings.Add(new MappingItem("D-Pad Up", "DPadUp", MappingCategory.DPad));
            Mappings.Add(new MappingItem("D-Pad Down", "DPadDown", MappingCategory.DPad));
            Mappings.Add(new MappingItem("D-Pad Left", "DPadLeft", MappingCategory.DPad));
            Mappings.Add(new MappingItem("D-Pad Right", "DPadRight", MappingCategory.DPad));
            Mappings.Add(new MappingItem("Left Trigger", "LeftTrigger", MappingCategory.Triggers));
            Mappings.Add(new MappingItem("Right Trigger", "RightTrigger", MappingCategory.Triggers));
            Mappings.Add(new MappingItem("Left Stick X", "LeftThumbAxisX", MappingCategory.LeftStick));
            Mappings.Add(new MappingItem("Left Stick Y", "LeftThumbAxisY", MappingCategory.LeftStick));
            Mappings.Add(new MappingItem("Right Stick X", "RightThumbAxisX", MappingCategory.RightStick));
            Mappings.Add(new MappingItem("Right Stick Y", "RightThumbAxisY", MappingCategory.RightStick));
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

        private double _leftMotorDisplay;
        public double LeftMotorDisplay { get => _leftMotorDisplay; set => SetProperty(ref _leftMotorDisplay, value); }

        private double _rightMotorDisplay;
        public double RightMotorDisplay { get => _rightMotorDisplay; set => SetProperty(ref _rightMotorDisplay, value); }

        // ═══════════════════════════════════════════════
        //  #2: Expanded dead zone settings
        //  Per-axis X/Y, anti-dead zone, linear, trigger dead zones
        // ═══════════════════════════════════════════════

        // ── Left Stick ──
        private int _leftDeadZoneX;
        public int LeftDeadZoneX { get => _leftDeadZoneX; set => SetProperty(ref _leftDeadZoneX, Math.Clamp(value, 0, 100)); }

        private int _leftDeadZoneY;
        public int LeftDeadZoneY { get => _leftDeadZoneY; set => SetProperty(ref _leftDeadZoneY, Math.Clamp(value, 0, 100)); }

        private int _leftAntiDeadZoneX;
        public int LeftAntiDeadZoneX { get => _leftAntiDeadZoneX; set => SetProperty(ref _leftAntiDeadZoneX, Math.Clamp(value, 0, 100)); }

        private int _leftAntiDeadZoneY;
        public int LeftAntiDeadZoneY { get => _leftAntiDeadZoneY; set => SetProperty(ref _leftAntiDeadZoneY, Math.Clamp(value, 0, 100)); }

        private int _leftLinear;
        public int LeftLinear { get => _leftLinear; set => SetProperty(ref _leftLinear, Math.Clamp(value, 0, 100)); }

        // ── Right Stick ──
        private int _rightDeadZoneX;
        public int RightDeadZoneX { get => _rightDeadZoneX; set => SetProperty(ref _rightDeadZoneX, Math.Clamp(value, 0, 100)); }

        private int _rightDeadZoneY;
        public int RightDeadZoneY { get => _rightDeadZoneY; set => SetProperty(ref _rightDeadZoneY, Math.Clamp(value, 0, 100)); }

        private int _rightAntiDeadZoneX;
        public int RightAntiDeadZoneX { get => _rightAntiDeadZoneX; set => SetProperty(ref _rightAntiDeadZoneX, Math.Clamp(value, 0, 100)); }

        private int _rightAntiDeadZoneY;
        public int RightAntiDeadZoneY { get => _rightAntiDeadZoneY; set => SetProperty(ref _rightAntiDeadZoneY, Math.Clamp(value, 0, 100)); }

        private int _rightLinear;
        public int RightLinear { get => _rightLinear; set => SetProperty(ref _rightLinear, Math.Clamp(value, 0, 100)); }

        // ── Triggers ──
        private int _leftTriggerDeadZone;
        public int LeftTriggerDeadZone { get => _leftTriggerDeadZone; set => SetProperty(ref _leftTriggerDeadZone, Math.Clamp(value, 0, 100)); }

        private int _rightTriggerDeadZone;
        public int RightTriggerDeadZone { get => _rightTriggerDeadZone; set => SetProperty(ref _rightTriggerDeadZone, Math.Clamp(value, 0, 100)); }

        private int _leftTriggerAntiDeadZone;
        public int LeftTriggerAntiDeadZone { get => _leftTriggerAntiDeadZone; set => SetProperty(ref _leftTriggerAntiDeadZone, Math.Clamp(value, 0, 100)); }

        private int _rightTriggerAntiDeadZone;
        public int RightTriggerAntiDeadZone { get => _rightTriggerAntiDeadZone; set => SetProperty(ref _rightTriggerAntiDeadZone, Math.Clamp(value, 0, 100)); }

        // ── Backward compatibility shims ──
        // SettingsService and existing PadPage.xaml use LeftDeadZone/RightDeadZone.
        // Route to both X and Y axes so old code works transparently.
        public int LeftDeadZone
        {
            get => _leftDeadZoneX;
            set { LeftDeadZoneX = value; LeftDeadZoneY = value; }
        }

        public int RightDeadZone
        {
            get => _rightDeadZoneX;
            set { RightDeadZoneX = value; RightDeadZoneY = value; }
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
                var macro = new MacroItem { Name = $"Macro {Macros.Count + 1}" };
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
                m.SourceDescriptor = string.Empty;
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
            _mapAllCommand ??= new RelayCommand(StartMapAll, () => HasSelectedDevice && !IsMapAllActive);

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
            MapAllCurrentTarget = mapping.TargetSettingName;
            CurrentRecordingTarget = mapping.TargetSettingName;
            MapAllPromptText = $"Map: {mapping.TargetLabel}  ({MapAllCurrentIndex + 1}/{Mappings.Count})";
            MapAllRecordRequested?.Invoke(this, mapping);
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
                MapAllCurrentIndex++;
                AdvanceMapAll();
            };
            _mapAllDelayTimer.Start();
        }

        public void StopMapAll()
        {
            _mapAllDelayTimer?.Stop();
            _mapAllDelayTimer = null;
            IsMapAllActive = false;
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
            LeftTrigger = gp.LeftTrigger / 255.0;
            RightTrigger = gp.RightTrigger / 255.0;

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
                LeftMotorDisplay = vibration.LeftMotorSpeed / 65535.0;
                RightMotorDisplay = vibration.RightMotorSpeed / 65535.0;
            }
        }

        /// <summary>
        /// Updates per-device stick/trigger values for the stick and trigger tab previews.
        /// Shows only the selected device's input, not the combined slot.
        /// </summary>
        public void UpdateDeviceState(Gamepad gp)
        {
            DeviceRawLeftTrigger = gp.LeftTrigger;
            DeviceRawRightTrigger = gp.RightTrigger;
            DeviceLeftTrigger = gp.LeftTrigger / 255.0;
            DeviceRightTrigger = gp.RightTrigger / 255.0;

            DeviceRawThumbLX = gp.ThumbLX;
            DeviceRawThumbLY = gp.ThumbLY;
            DeviceRawThumbRX = gp.ThumbRX;
            DeviceRawThumbRY = gp.ThumbRY;
            DeviceThumbLX = (gp.ThumbLX - (double)short.MinValue) / 65535.0;
            DeviceThumbLY = 1.0 - ((gp.ThumbLY - (double)short.MinValue) / 65535.0);
            DeviceThumbRX = (gp.ThumbRX - (double)short.MinValue) / 65535.0;
            DeviceThumbRY = 1.0 - ((gp.ThumbRY - (double)short.MinValue) / 65535.0);
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
