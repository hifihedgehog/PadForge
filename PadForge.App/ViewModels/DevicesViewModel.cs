using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common;
using PadForge.Common.Input;

namespace PadForge.ViewModels
{
    /// <summary>
    /// ViewModel for the Devices page. Shows a list of all detected input
    /// devices (online and offline) and raw input state for the selected device.
    /// </summary>
    public partial class DevicesViewModel : ViewModelBase
    {
        public DevicesViewModel()
        {
            Title = "Devices";
        }

        // ─────────────────────────────────────────────
        //  Device list
        // ─────────────────────────────────────────────

        /// <summary>
        /// Collection of all known devices. Updated by InputService when
        /// the device list changes.
        /// </summary>
        public ObservableCollection<DeviceRowViewModel> Devices { get; } =
            new ObservableCollection<DeviceRowViewModel>();

        private DeviceRowViewModel _selectedDevice;

        /// <summary>
        /// The currently selected device in the device list.
        /// When selected, its raw input state is shown in the detail panel.
        /// </summary>
        public DeviceRowViewModel SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (SetProperty(ref _selectedDevice, value))
                {
                    OnPropertyChanged(nameof(HasSelectedDevice));
                    _assignToSlotCommand?.NotifyCanExecuteChanged();
                    _removeDeviceCommand?.NotifyCanExecuteChanged();
                    RefreshSlotButtons();
                }
            }
        }

        /// <summary>Whether a device is currently selected.</summary>
        public bool HasSelectedDevice => _selectedDevice != null;

        // ─────────────────────────────────────────────
        //  Device counts
        // ─────────────────────────────────────────────

        private int _totalCount;

        /// <summary>Total number of detected devices.</summary>
        public int TotalCount
        {
            get => _totalCount;
            set => SetProperty(ref _totalCount, value);
        }

        private int _onlineCount;

        /// <summary>Number of currently connected devices.</summary>
        public int OnlineCount
        {
            get => _onlineCount;
            set => SetProperty(ref _onlineCount, value);
        }

        // ─────────────────────────────────────────────
        //  Raw state display (structured, for selected device)
        // ─────────────────────────────────────────────

        /// <summary>Structured axis values for visual display (progress bars).</summary>
        public ObservableCollection<AxisDisplayItem> RawAxes { get; } = new();

        /// <summary>Structured button states for visual display (circles).</summary>
        public ObservableCollection<ButtonDisplayItem> RawButtons { get; } = new();

        /// <summary>Structured POV hat values for visual display (compass).</summary>
        public ObservableCollection<PovDisplayItem> RawPovs { get; } = new();

        /// <summary>Keyboard key layout items for visual keyboard display.</summary>
        public ObservableCollection<KeyboardKeyItem> KeyboardKeys { get; } = new();

        private bool _isKeyboardDevice;
        /// <summary>Whether the currently selected device is a keyboard.</summary>
        public bool IsKeyboardDevice
        {
            get => _isKeyboardDevice;
            set => SetProperty(ref _isKeyboardDevice, value);
        }

        private int _selectedButtonTotal;

        /// <summary>Total number of buttons on the selected device.</summary>
        public int SelectedButtonTotal
        {
            get => _selectedButtonTotal;
            set => SetProperty(ref _selectedButtonTotal, value);
        }

        private bool _hasRawData;

        /// <summary>Whether raw state data is available for the selected device.</summary>
        public bool HasRawData
        {
            get => _hasRawData;
            set => SetProperty(ref _hasRawData, value);
        }

        // Gyroscope / Accelerometer individual values

        private bool _hasGyroData;
        public bool HasGyroData { get => _hasGyroData; set => SetProperty(ref _hasGyroData, value); }

        private bool _hasAccelData;
        public bool HasAccelData { get => _hasAccelData; set => SetProperty(ref _hasAccelData, value); }

        private double _gyroX, _gyroY, _gyroZ;
        public double GyroX { get => _gyroX; set => SetProperty(ref _gyroX, value); }
        public double GyroY { get => _gyroY; set => SetProperty(ref _gyroY, value); }
        public double GyroZ { get => _gyroZ; set => SetProperty(ref _gyroZ, value); }

        private double _accelX, _accelY, _accelZ;
        public double AccelX { get => _accelX; set => SetProperty(ref _accelX, value); }
        public double AccelY { get => _accelY; set => SetProperty(ref _accelY, value); }
        public double AccelZ { get => _accelZ; set => SetProperty(ref _accelZ, value); }

        /// <summary>Tracks which device's collections are currently populated.</summary>
        internal Guid LastRawStateDeviceGuid { get; set; }

        /// <summary>
        /// Rebuilds the raw state collections for a new device with the given capabilities.
        /// </summary>
        internal void RebuildRawStateCollections(int axisCount, int buttonCount, int povCount, bool isKeyboard = false)
        {
            RawAxes.Clear();
            for (int i = 0; i < axisCount; i++)
                RawAxes.Add(new AxisDisplayItem { Index = i, Name = $"Axis {i}" });

            RawButtons.Clear();
            KeyboardKeys.Clear();
            IsKeyboardDevice = isKeyboard;

            if (isKeyboard)
            {
                // Build positioned keyboard layout instead of flat button list.
                foreach (var key in KeyboardKeyItem.BuildLayout())
                    KeyboardKeys.Add(key);
            }
            else
            {
                for (int i = 0; i < buttonCount; i++)
                    RawButtons.Add(new ButtonDisplayItem { Index = i });
            }

            RawPovs.Clear();
            for (int i = 0; i < povCount; i++)
                RawPovs.Add(new PovDisplayItem { Index = i });

            SelectedButtonTotal = buttonCount;
        }

        /// <summary>Clears all raw state display data.</summary>
        internal void ClearRawState()
        {
            RawAxes.Clear();
            RawButtons.Clear();
            RawPovs.Clear();
            KeyboardKeys.Clear();
            IsKeyboardDevice = false;
            HasRawData = false;
            HasGyroData = false;
            HasAccelData = false;
            LastRawStateDeviceGuid = Guid.Empty;
        }

        // ─────────────────────────────────────────────
        //  Commands
        // ─────────────────────────────────────────────

        private RelayCommand _refreshCommand;

        /// <summary>Command to force-refresh the device list.</summary>
        public RelayCommand RefreshCommand =>
            _refreshCommand ??= new RelayCommand(
                () => RefreshRequested?.Invoke(this, EventArgs.Empty));

        private RelayCommand<int> _assignToSlotCommand;

        /// <summary>
        /// Command to assign the selected device to a pad slot.
        /// Parameter is the slot index (0–3).
        /// </summary>
        public RelayCommand<int> AssignToSlotCommand =>
            _assignToSlotCommand ??= new RelayCommand<int>(
                slotIndex => AssignToSlotRequested?.Invoke(this, slotIndex),
                _ => HasSelectedDevice);

        private RelayCommand _hideDeviceCommand;

        /// <summary>Command to hide the selected device from the list.</summary>
        public RelayCommand HideDeviceCommand =>
            _hideDeviceCommand ??= new RelayCommand(
                () =>
                {
                    if (_selectedDevice != null)
                    {
                        _selectedDevice.IsHidden = true;
                        HideDeviceRequested?.Invoke(this, _selectedDevice.InstanceGuid);
                    }
                },
                () => HasSelectedDevice);

        private RelayCommand _removeDeviceCommand;

        /// <summary>
        /// Command to remove the selected device from the device list entirely.
        /// Removes the device record, any associated user settings, and the UI row.
        /// Works for both online and offline devices — removing an online device
        /// will also unassign it from any slot and stop reading its input.
        /// </summary>
        public RelayCommand RemoveDeviceCommand =>
            _removeDeviceCommand ??= new RelayCommand(
                () =>
                {
                    if (_selectedDevice != null)
                    {
                        Guid guid = _selectedDevice.InstanceGuid;
                        Devices.Remove(_selectedDevice);
                        SelectedDevice = null;
                        RemoveDeviceRequested?.Invoke(this, guid);
                        RefreshCounts();
                    }
                },
                () => HasSelectedDevice && _selectedDevice != null);

        /// <summary>Raised when a refresh is requested.</summary>
        public event EventHandler RefreshRequested;

        /// <summary>Raised when the user assigns a device to a slot. Arg = slot index.</summary>
        public event EventHandler<int> AssignToSlotRequested;

        /// <summary>Raised when the user hides a device. Arg = instance GUID.</summary>
        public event EventHandler<Guid> HideDeviceRequested;

        /// <summary>Raised when the user removes a device. Arg = instance GUID.</summary>
        public event EventHandler<Guid> RemoveDeviceRequested;

        /// <summary>Raised when the user toggles a slot assignment. Arg = slot index.</summary>
        public event EventHandler<int> ToggleSlotRequested;

        // ─────────────────────────────────────────────
        //  Dynamic slot buttons
        // ─────────────────────────────────────────────

        /// <summary>
        /// Dynamic list of virtual controller slot buttons for device assignment.
        /// Only includes created/active slots.
        /// </summary>
        public ObservableCollection<SlotButtonItem> ActiveSlotItems { get; } = new();

        private RelayCommand<int> _toggleSlotCommand;

        /// <summary>Command to toggle the selected device's assignment to a slot.</summary>
        public RelayCommand<int> ToggleSlotCommand =>
            _toggleSlotCommand ??= new RelayCommand<int>(
                slotIndex =>
                {
                    ToggleSlotRequested?.Invoke(this, slotIndex);
                    RefreshSlotButtons();
                },
                _ => HasSelectedDevice);

        /// <summary>
        /// Rebuilds <see cref="ActiveSlotItems"/> based on which virtual controller
        /// slots are created and whether the selected device is assigned to each.
        /// </summary>
        public void RefreshSlotButtons()
        {
            var activeSlots = new System.Collections.Generic.List<int>();
            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (SettingsManager.SlotCreated[i])
                    activeSlots.Add(i);
            }

            // Get the selected device's current assignments.
            var assignedSlots = _selectedDevice != null
                ? SettingsManager.GetAssignedSlots(_selectedDevice.InstanceGuid)
                : new System.Collections.Generic.List<int>();

            // Compute 1-based slot numbers.
            bool structureChanged = activeSlots.Count != ActiveSlotItems.Count;
            if (!structureChanged)
            {
                for (int i = 0; i < activeSlots.Count; i++)
                {
                    if (ActiveSlotItems[i].PadIndex != activeSlots[i])
                    {
                        structureChanged = true;
                        break;
                    }
                }
            }

            if (structureChanged)
            {
                ActiveSlotItems.Clear();
                int num = 0;
                foreach (int slot in activeSlots)
                {
                    num++;
                    ActiveSlotItems.Add(new SlotButtonItem
                    {
                        PadIndex = slot,
                        SlotNumber = num,
                        IsAssigned = assignedSlots.Contains(slot)
                    });
                }
            }
            else
            {
                foreach (var item in ActiveSlotItems)
                    item.IsAssigned = assignedSlots.Contains(item.PadIndex);
            }

            _toggleSlotCommand?.NotifyCanExecuteChanged();
        }

        // ─────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Finds a device row by instance GUID.
        /// </summary>
        public DeviceRowViewModel FindByGuid(Guid instanceGuid)
        {
            foreach (var d in Devices)
            {
                if (d.InstanceGuid == instanceGuid)
                    return d;
            }
            return null;
        }

        /// <summary>
        /// Updates the device counts from the Devices collection.
        /// </summary>
        public void RefreshCounts()
        {
            TotalCount = Devices.Count;
            int online = 0;
            foreach (var d in Devices)
            {
                if (d.IsOnline)
                    online++;
            }
            OnlineCount = online;
        }
    }

    /// <summary>
    /// Represents a single virtual controller slot button on the Devices page.
    /// </summary>
    public class SlotButtonItem : ObservableObject
    {
        private int _padIndex;
        /// <summary>Zero-based pad slot index (0–3).</summary>
        public int PadIndex
        {
            get => _padIndex;
            set => SetProperty(ref _padIndex, value);
        }

        private int _slotNumber;
        /// <summary>1-based slot number among active slots.</summary>
        public int SlotNumber
        {
            get => _slotNumber;
            set => SetProperty(ref _slotNumber, value);
        }

        private bool _isAssigned;
        /// <summary>Whether the currently selected device is assigned to this slot.</summary>
        public bool IsAssigned
        {
            get => _isAssigned;
            set => SetProperty(ref _isAssigned, value);
        }

    }

    /// <summary>Visual display item for a single axis value.</summary>
    public class AxisDisplayItem : ObservableObject
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;

        private double _normalizedValue;
        /// <summary>Axis value normalized to 0.0–1.0 range.</summary>
        public double NormalizedValue
        {
            get => _normalizedValue;
            set => SetProperty(ref _normalizedValue, value);
        }

        private int _rawValue;
        /// <summary>Raw axis value (0–65535).</summary>
        public int RawValue
        {
            get => _rawValue;
            set => SetProperty(ref _rawValue, value);
        }
    }

    /// <summary>Visual display item for a single button state.</summary>
    public class ButtonDisplayItem : ObservableObject
    {
        public int Index { get; set; }

        private bool _isPressed;
        /// <summary>Whether the button is currently pressed.</summary>
        public bool IsPressed
        {
            get => _isPressed;
            set => SetProperty(ref _isPressed, value);
        }
    }

    /// <summary>Visual display item for a single keyboard key with position data.</summary>
    public class KeyboardKeyItem : ObservableObject
    {
        public int VKeyIndex { get; set; }
        public string Label { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public double KeyWidth { get; set; }
        public double KeyHeight { get; set; }

        private bool _isPressed;
        public bool IsPressed
        {
            get => _isPressed;
            set => SetProperty(ref _isPressed, value);
        }

        /// <summary>Canvas width for the keyboard layout (for XAML binding).</summary>
        public const double LayoutWidth = 556;
        /// <summary>Canvas height for the keyboard layout (for XAML binding).</summary>
        public const double LayoutHeight = 136;

        /// <summary>
        /// Builds a full ANSI QWERTY keyboard layout with numpad as positioned key items.
        /// Each key is mapped to its Windows Virtual Key code.
        /// Wrapped in a Viewbox in XAML so it auto-scales to the available width.
        /// </summary>
        public static ObservableCollection<KeyboardKeyItem> BuildLayout()
        {
            const double u = 24;  // unit size in pixels (1 standard key width)
            const double g = 2;   // gap between keys
            const double kh = 20; // key height
            const double rh = 22; // row height (key + gap)

            var keys = new ObservableCollection<KeyboardKeyItem>();

            void Add(int vk, string label, double xU, double y, double wU = 1, double hU = 1)
            {
                keys.Add(new KeyboardKeyItem
                {
                    VKeyIndex = vk,
                    Label = label,
                    X = xU * u,
                    Y = y,
                    KeyWidth = wU * u - g,
                    KeyHeight = hU * kh + (hU > 1 ? g : 0) // tall keys span the gap
                });
            }

            // ── Row 0: Esc, F1–F12 ──
            double y0 = 0;
            Add(0x1B, "Esc", 0, y0);
            for (int i = 0; i < 4; i++) Add(0x70 + i, $"F{i + 1}", 2 + i, y0);
            for (int i = 0; i < 4; i++) Add(0x74 + i, $"F{i + 5}", 6.5 + i, y0);
            for (int i = 0; i < 4; i++) Add(0x78 + i, $"F{i + 9}", 11 + i, y0);

            // ── Row 1: ` 1–0 - = Bksp ──
            double y1 = rh + 4; // extra gap after Fn row
            Add(0xC0, "`", 0, y1);
            for (int i = 1; i <= 9; i++) Add(0x30 + i, i.ToString(), i, y1);
            Add(0x30, "0", 10, y1);
            Add(0xBD, "-", 11, y1);
            Add(0xBB, "=", 12, y1);
            Add(0x08, "\u2190", 13, y1, 2); // ← arrow for Backspace

            // ── Row 2: Tab Q–P [ ] \ ──
            double y2 = y1 + rh;
            Add(0x09, "Tab", 0, y2, 1.5);
            int[] qRow = { 0x51, 0x57, 0x45, 0x52, 0x54, 0x59, 0x55, 0x49, 0x4F, 0x50 };
            string[] qLbl = { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P" };
            for (int i = 0; i < 10; i++) Add(qRow[i], qLbl[i], 1.5 + i, y2);
            Add(0xDB, "[", 11.5, y2);
            Add(0xDD, "]", 12.5, y2);
            Add(0xDC, "\\", 13.5, y2, 1.5);

            // ── Row 3: Caps A–L ; ' Enter ──
            double y3 = y2 + rh;
            Add(0x14, "Caps", 0, y3, 1.75);
            int[] aRow = { 0x41, 0x53, 0x44, 0x46, 0x47, 0x48, 0x4A, 0x4B, 0x4C };
            string[] aLbl = { "A", "S", "D", "F", "G", "H", "J", "K", "L" };
            for (int i = 0; i < 9; i++) Add(aRow[i], aLbl[i], 1.75 + i, y3);
            Add(0xBA, ";", 10.75, y3);
            Add(0xDE, "'", 11.75, y3);
            Add(0x0D, "\u21B5", 12.75, y3, 2.25); // ↵ arrow for Enter

            // ── Row 4: Shift Z–M , . / Shift ──
            double y4 = y3 + rh;
            Add(0xA0, "Shift", 0, y4, 2.25);
            int[] zRow = { 0x5A, 0x58, 0x43, 0x56, 0x42, 0x4E, 0x4D };
            string[] zLbl = { "Z", "X", "C", "V", "B", "N", "M" };
            for (int i = 0; i < 7; i++) Add(zRow[i], zLbl[i], 2.25 + i, y4);
            Add(0xBC, ",", 9.25, y4);
            Add(0xBE, ".", 10.25, y4);
            Add(0xBF, "/", 11.25, y4);
            Add(0xA1, "Shift", 12.25, y4, 2.75);

            // ── Row 5: Ctrl Win Alt Space Alt Win Menu Ctrl ──
            double y5 = y4 + rh;
            Add(0xA2, "Ctrl", 0, y5, 1.25);
            Add(0x5B, "Win", 1.25, y5, 1.25);
            Add(0xA4, "Alt", 2.5, y5, 1.25);
            Add(0x20, "", 3.75, y5, 6.25);
            Add(0xA5, "Alt", 10, y5, 1.25);
            Add(0x5C, "Win", 11.25, y5, 1.25);
            Add(0x5D, "Fn", 12.5, y5, 1.25);
            Add(0xA3, "Ctrl", 13.75, y5, 1.25);

            // ── Navigation cluster ──
            double nx = 15.5;
            Add(0x2D, "Ins", nx, y1);       Add(0x24, "Hm", nx + 1, y1);   Add(0x21, "PU", nx + 2, y1);
            Add(0x2E, "Del", nx, y2);       Add(0x23, "End", nx + 1, y2);  Add(0x22, "PD", nx + 2, y2);

            // ── Arrow keys ──
            Add(0x26, "\u25B2", nx + 1, y4);                                         // Up
            Add(0x25, "\u25C4", nx, y5);  Add(0x28, "\u25BC", nx + 1, y5);  Add(0x27, "\u25BA", nx + 2, y5); // Left Down Right

            // ── Numpad ──
            double np = 19;
            Add(0x90, "Num", np, y1);       Add(0x6F, "/", np + 1, y1);    Add(0x6A, "*", np + 2, y1);   Add(0x6D, "-", np + 3, y1);
            Add(0x67, "7", np, y2);         Add(0x68, "8", np + 1, y2);    Add(0x69, "9", np + 2, y2);
            Add(0x6B, "+", np + 3, y2, 1, 2); // tall key spanning 2 rows
            Add(0x64, "4", np, y3);         Add(0x65, "5", np + 1, y3);    Add(0x66, "6", np + 2, y3);
            Add(0x61, "1", np, y4);         Add(0x62, "2", np + 1, y4);    Add(0x63, "3", np + 2, y4);
            Add(0x88, "\u21B5", np + 3, y4, 1, 2); // NumEnter (custom VKey 0x88 via RawInput E0 translation)
            Add(0x60, "0", np, y5, 2);      Add(0x6E, ".", np + 2, y5);

            return keys;
        }

        /// <summary>
        /// Checks if a VKey is pressed. The Raw Input handler sets both
        /// generic (0x10/0x11/0x12) and specific L/R codes (0xA0–0xA5),
        /// so a direct array lookup is sufficient.
        /// </summary>
        public static bool IsVKeyPressed(bool[] buttons, int vk)
        {
            return vk < buttons.Length && buttons[vk];
        }
    }

    /// <summary>Visual display item for a single POV hat switch.</summary>
    public class PovDisplayItem : ObservableObject
    {
        public int Index { get; set; }

        private int _centidegrees = -1;
        /// <summary>POV value in centidegrees (0–35900), or -1 for centered.</summary>
        public int Centidegrees
        {
            get => _centidegrees;
            set
            {
                if (SetProperty(ref _centidegrees, value))
                {
                    OnPropertyChanged(nameof(IsCentered));
                    OnPropertyChanged(nameof(AngleDegrees));
                }
            }
        }

        /// <summary>Whether the POV is centered (no direction).</summary>
        public bool IsCentered => _centidegrees < 0;

        /// <summary>Direction in degrees (0–359) for rotation transforms.</summary>
        public double AngleDegrees => _centidegrees >= 0 ? _centidegrees / 100.0 : 0;
    }
}
