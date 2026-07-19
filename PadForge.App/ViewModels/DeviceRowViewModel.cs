using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// ViewModel for a single device row in the Devices page grid.
    /// Displays device identification, status, and basic capability info.
    /// </summary>
    public class DeviceRowViewModel : ObservableObject
    {
        public DeviceRowViewModel()
        {
            Strings.CultureChanged += OnCultureChanged;
        }

        private void OnCultureChanged()
        {
            OnPropertyChanged(nameof(DeviceType));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CapabilitiesSummary));
        }

        // ─────────────────────────────────────────────
        //  Identity
        // ─────────────────────────────────────────────

        private Guid _instanceGuid;

        /// <summary>Unique instance GUID for this device.</summary>
        public Guid InstanceGuid
        {
            get => _instanceGuid;
            set => SetProperty(ref _instanceGuid, value);
        }

        private string _sdlGuid = string.Empty;

        /// <summary>SDL joystick GUID (32 hex chars) for gamecontrollerdb matching.</summary>
        public string SdlGuid
        {
            get => _sdlGuid;
            set => SetProperty(ref _sdlGuid, value);
        }

        private string _serialNumber = string.Empty;

        /// <summary>Device serial number as SDL reports it (the Bluetooth MAC
        /// on most wireless pads). Empty when the device surfaces none. The
        /// dossier SERIAL line hides itself instead of showing a placeholder
        /// (#175 item 7).</summary>
        public string SerialNumber
        {
            get => _serialNumber;
            set => SetProperty(ref _serialNumber, value);
        }

        private string _deviceName = string.Empty;

        /// <summary>Display name of the device.</summary>
        public string DeviceName
        {
            get => _deviceName;
            set => SetProperty(ref _deviceName, value);
        }

        private string _productName = string.Empty;

        /// <summary>Product name of the device.</summary>
        public string ProductName
        {
            get => _productName;
            set => SetProperty(ref _productName, value);
        }

        private Guid _productGuid;

        /// <summary>Product GUID in PIDVID format.</summary>
        public Guid ProductGuid
        {
            get => _productGuid;
            set => SetProperty(ref _productGuid, value);
        }

        // ─────────────────────────────────────────────
        //  USB identification
        // ─────────────────────────────────────────────

        private ushort _vendorId;

        /// <summary>USB Vendor ID.</summary>
        public ushort VendorId
        {
            get => _vendorId;
            set
            {
                if (SetProperty(ref _vendorId, value))
                {
                    OnPropertyChanged(nameof(VendorIdHex));
                    OnPropertyChanged(nameof(HasVidPid));
                    // Transport depends on VID/PID for the fork BLE Switch 2
                    // case (empty DevicePath never fires its own notify).
                    OnPropertyChanged(nameof(IsBluetoothLink));
                }
            }
        }

        private ushort _productId;

        /// <summary>USB Product ID.</summary>
        public ushort ProductId
        {
            get => _productId;
            set
            {
                if (SetProperty(ref _productId, value))
                {
                    OnPropertyChanged(nameof(ProductIdHex));
                    OnPropertyChanged(nameof(HasVidPid));
                    OnPropertyChanged(nameof(IsBluetoothLink));
                }
            }
        }

        /// <summary>Vendor ID as a hex string (e.g., "045E").</summary>
        public string VendorIdHex => _vendorId.ToString("X4");

        /// <summary>Product ID as a hex string (e.g., "028E").</summary>
        public string ProductIdHex => _productId.ToString("X4");

        /// <summary>Whether the device reports a USB identity at all. Virtual
        /// and merged sources surface 0000:0000, which is noise, not identity.
        /// The list row hides its VID:PID token when this is false (#175
        /// phase 2 item 17), the same absent-fact gating the dossier uses.</summary>
        public bool HasVidPid => _vendorId != 0 || _productId != 0;

        // ─────────────────────────────────────────────
        //  Status
        // ─────────────────────────────────────────────

        private bool _isOnline;

        /// <summary>Whether the device is currently connected.</summary>
        public bool IsOnline
        {
            get => _isOnline;
            set
            {
                if (SetProperty(ref _isOnline, value))
                    OnPropertyChanged(nameof(StatusText));
            }
        }

        private bool _isEnabled = true;

        /// <summary>Whether the device is enabled for mapping.</summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (SetProperty(ref _isEnabled, value))
                    OnPropertyChanged(nameof(StatusText));
            }
        }

        private bool _isHidden;

        /// <summary>Whether the device is hidden from the UI.</summary>
        public bool IsHidden
        {
            get => _isHidden;
            set => SetProperty(ref _isHidden, value);
        }

        /// <summary>Status text for display.</summary>
        public string StatusText
        {
            get
            {
                if (!_isEnabled) return Strings.Instance.Common_Disabled;
                if (_isOnline) return Strings.Instance.Common_Online;
                return Strings.Instance.Common_Offline;
            }
        }

        // ─────────────────────────────────────────────
        //  Capabilities
        // ─────────────────────────────────────────────

        private int _axisCount;

        /// <summary>Number of axes on the device.</summary>
        public int AxisCount
        {
            get => _axisCount;
            set => SetProperty(ref _axisCount, value);
        }

        private int _buttonCount;

        /// <summary>Number of buttons on the device.</summary>
        public int ButtonCount
        {
            get => _buttonCount;
            set => SetProperty(ref _buttonCount, value);
        }

        private int _povCount;

        /// <summary>Number of POV hat switches on the device.</summary>
        public int PovCount
        {
            get => _povCount;
            set => SetProperty(ref _povCount, value);
        }

        /// <summary>Internal English device type key for comparison logic (e.g., "Gamepad", "Mouse").</summary>
        private string _deviceTypeKey = string.Empty;
        public string DeviceTypeKey
        {
            get => _deviceTypeKey;
            set
            {
                if (SetProperty(ref _deviceTypeKey, value))
                {
                    OnPropertyChanged(nameof(DeviceType));
                    OnPropertyChanged(nameof(IsGamepad));
                    OnPropertyChanged(nameof(ShowInputModeSection));
                    OnPropertyChanged(nameof(ShowInputModeOrHidingSection));
                    OnPropertyChanged(nameof(IsMidiDevice));
                    OnPropertyChanged(nameof(ShowTouchpadCapability));
                    OnPropertyChanged(nameof(HasCapabilityIcons));
                }
            }
        }

        /// <summary>Localized device type description for display, derived from DeviceTypeKey.</summary>
        public string DeviceType => DeviceTypeKey switch
        {
            "Gamepad" => Strings.Instance.DeviceType_Gamepad,
            "Joystick" => Strings.Instance.DeviceType_Joystick,
            "Wheel" => Strings.Instance.DeviceType_Wheel,
            "FlightStick" => Strings.Instance.DeviceType_FlightStick,
            "FirstPerson" => Strings.Instance.DeviceType_FirstPerson,
            "Supplemental" => Strings.Instance.DeviceType_Supplemental,
            "Mouse" => Strings.Instance.DeviceType_Mouse,
            "Keyboard" => Strings.Instance.DeviceType_Keyboard,
            "Touchpad" => Strings.Instance.DeviceType_Touchpad,
            "Midi" => Strings.Instance.DeviceType_Midi,
            "Nfc" => Strings.Instance.DeviceType_Nfc,
            "ConsumerControl" => Strings.Instance.DeviceType_ConsumerControl,
            _ => Strings.Instance.DeviceType_Device
        };

        private bool _hasRumble;

        /// <summary>Whether the device supports rumble vibration.</summary>
        public bool HasRumble
        {
            get => _hasRumble;
            set
            {
                if (SetProperty(ref _hasRumble, value))
                    OnPropertyChanged(nameof(HasCapabilityIcons));
            }
        }

        private bool _hasGyro;

        /// <summary>Whether the device has a gyroscope sensor.</summary>
        public bool HasGyro
        {
            get => _hasGyro;
            set
            {
                if (SetProperty(ref _hasGyro, value))
                    OnPropertyChanged(nameof(HasCapabilityIcons));
            }
        }

        private bool _hasAccel;

        /// <summary>Whether the device has an accelerometer sensor.</summary>
        public bool HasAccel
        {
            get => _hasAccel;
            set => SetProperty(ref _hasAccel, value);
        }

        private bool _hasTouchpad;

        /// <summary>Whether the device has a touchpad.</summary>
        public bool HasTouchpad
        {
            get => _hasTouchpad;
            set
            {
                if (SetProperty(ref _hasTouchpad, value))
                {
                    OnPropertyChanged(nameof(ShowTouchpadCapability));
                    OnPropertyChanged(nameof(HasCapabilityIcons));
                }
            }
        }

        /// <summary>Touchpad chip gate for the dossier capability row,
        /// mirroring CapabilitiesSummary: shown only when the touchpad is a
        /// capability of a larger device, not the device's own type.</summary>
        public bool ShowTouchpadCapability => _hasTouchpad && DeviceTypeKey != "Touchpad";

        /// <summary>Whether the dossier capability row renders at all
        /// (#175 item 7). Counts only capabilities with an established glyph
        /// (rumble, gyro, touchpad). Accel has no established glyph anywhere
        /// in the app and stays text-only in CapabilitiesSummary.</summary>
        public bool HasCapabilityIcons => _hasRumble || _hasGyro || ShowTouchpadCapability;

        // ─────────────────────────────────────────────
        //  Slot assignment
        // ─────────────────────────────────────────────

        private List<int> _assignedSlots = new();

        /// <summary>
        /// The pad slot indices this device is assigned to (0–7). Empty if unassigned.
        /// A device can be assigned to multiple slots simultaneously.
        /// </summary>
        public List<int> AssignedSlots => _assignedSlots;

        /// <summary>
        /// Observable collection of slot badges for XAML binding (icon + number).
        /// </summary>
        public ObservableCollection<SlotBadge> SlotBadges { get; } = new();

        // The IsUnassigned flag and its gray fallback pill are gone (#175
        // phase 2 item 9): absence of badges encodes unassigned.

        /// <summary>
        /// Replaces the assigned slots list, rebuilds SlotBadges, and notifies the UI.
        /// </summary>
        public void SetAssignedSlots(List<int> slots)
        {
            _assignedSlots = slots ?? new();
            SlotBadges.Clear();
            // Walk SlotOrders in type-group order (Xbox → PlayStation →
            // Extended → KbM → MIDI) to compute the global slot number,
            // matching the dashboard cards and the Pad page header.
            // Iterating raw padIndex here mismatches the visual ordering
            // when an Xbox slot is added after a PlayStation slot — the
            // cards renumber to put Xbox at #1, but the badge would still
            // show the PlayStation as #1 because it was created first.
            var slotToGlobal = BuildSlotNumberLookup();
            foreach (int slot in _assignedSlots)
            {
                slotToGlobal.TryGetValue(slot, out var info);
                SlotBadges.Add(new SlotBadge { SlotIndex = slot, SlotNumber = info.Number, VcType = info.Type });
            }
            OnPropertyChanged(nameof(AssignedSlots));
        }

        /// <summary>Builds a slotIndex → (1-based global number, VC family)
        /// map walking type-group order to match the dashboard / sidebar /
        /// Pad page numbering. Created/active slots only; uncreated slots are
        /// excluded so the global numbers don't skip. The family rides along
        /// because the walk already knows it, and the badge's branded icon
        /// needs it (#175 phase 2 item 12).</summary>
        private static Dictionary<int, (int Number, Engine.VirtualControllerType Type)> BuildSlotNumberLookup()
        {
            var map = new Dictionary<int, (int Number, Engine.VirtualControllerType Type)>();
            int globalCount = 0;
            foreach (var groupType in Engine.VirtualControllerGroups.InOrder)
            {
                foreach (int padIndex in Common.Input.SettingsManager.SlotOrders.GetOrderFor(groupType))
                {
                    if (padIndex < 0 || padIndex >= Common.Input.SettingsManager.SlotCreated.Length)
                        continue;
                    if (!Common.Input.SettingsManager.SlotCreated[padIndex])
                        continue;
                    globalCount++;
                    map[padIndex] = (globalCount, groupType);
                }
            }
            return map;
        }

        // ─────────────────────────────────────────────
        //  Input hiding toggles
        // ─────────────────────────────────────────────

        private bool _hidHideEnabled;

        /// <summary>Whether this device is hidden from games via HidHide (driver-level).</summary>
        public bool HidHideEnabled
        {
            get => _hidHideEnabled;
            set => SetProperty(ref _hidHideEnabled, value);
        }

        private bool _consumeInputEnabled;

        /// <summary>Whether this device's mapped inputs are consumed via low-level hooks.</summary>
        public bool ConsumeInputEnabled
        {
            get => _consumeInputEnabled;
            set => SetProperty(ref _consumeInputEnabled, value);
        }

        private bool _forceRawJoystickMode;

        /// <summary>Whether to bypass SDL's gamepad remapping and read raw joystick indices.</summary>
        public bool ForceRawJoystickMode
        {
            get => _forceRawJoystickMode;
            set => SetProperty(ref _forceRawJoystickMode, value);
        }

        private int _idleDisconnectMinutes;

        /// <summary>Idle disconnect countdown in minutes, 0 = off (issue #162).
        /// Shown only for Bluetooth-pathed devices; persisted on UserDevice in
        /// seconds through the same channel as the hiding toggles.</summary>
        public int IdleDisconnectMinutes
        {
            get => _idleDisconnectMinutes;
            set => SetProperty(ref _idleDisconnectMinutes, Math.Max(0, value));
        }

        private bool _showIdleDisconnect;

        /// <summary>Whether the idle-disconnect control applies to this device
        /// (Bluetooth path detected).</summary>
        public bool ShowIdleDisconnect
        {
            get => _showIdleDisconnect;
            set => SetProperty(ref _showIdleDisconnect, value);
        }

        private int _batteryPercent = -1;

        /// <summary>Battery percentage from SDL, -1 when the device reports
        /// none (wired, unknown, or offline). Drives the row's battery
        /// indicator (issue #167).</summary>
        public int BatteryPercent
        {
            get => _batteryPercent;
            set
            {
                if (SetProperty(ref _batteryPercent, value))
                {
                    OnPropertyChanged(nameof(HasBattery));
                    OnPropertyChanged(nameof(BatteryText));
                    OnPropertyChanged(nameof(BatteryGlyph));
                }
            }
        }

        private bool _batteryCharging;

        /// <summary>True while the device reports charging or charged.</summary>
        public bool BatteryCharging
        {
            get => _batteryCharging;
            set
            {
                if (SetProperty(ref _batteryCharging, value))
                    OnPropertyChanged(nameof(BatteryGlyph));
            }
        }

        /// <summary>Whether the battery indicator renders at all.</summary>
        public bool HasBattery => _batteryPercent >= 0;

        /// <summary>"78%" for the row's metadata line.</summary>
        public string BatteryText => _batteryPercent >= 0 ? $"{_batteryPercent}%" : string.Empty;

        /// <summary>Segoe MDL2 Assets battery glyph bucketed to the nearest
        /// tenth: Battery0-Battery9 are U+E850-U+E859 with Battery10 at
        /// U+E83F, and the charging variants are BatteryCharging0-8 at
        /// U+E85A-U+E862, BatteryCharging9 at U+E83E, BatteryCharging10 at
        /// U+EA93. Names per the Segoe MDL2 documentation table
        /// (segoe-ui-symbol-font.md rows for E83E/E83F/EA93), all codepoints
        /// verified against the installed font.</summary>
        public string BatteryGlyph
        {
            get
            {
                if (_batteryPercent < 0) return string.Empty;
                int tenth = Math.Clamp((_batteryPercent + 5) / 10, 0, 10);
                int code;
                if (_batteryCharging)
                    code = tenth == 10 ? 0xEA93 : tenth == 9 ? 0xE83E : 0xE85A + tenth;
                else
                    code = tenth == 10 ? 0xE83F : 0xE850 + tenth;
                return char.ConvertFromUtf32(code);
            }
        }

        private bool _isHidHideAvailable;

        /// <summary>Whether HidHide is installed and available (controls IsEnabled on the toggle).</summary>
        public bool IsHidHideAvailable
        {
            get => _isHidHideAvailable;
            set => SetProperty(ref _isHidHideAvailable, value);
        }

        /// <summary>True for MIDI input devices — drives the live piano /
        /// CC preview on the Devices page (issue #128).</summary>
        public bool IsMidiDevice => DeviceTypeKey == "Midi";

        /// <summary>Whether to show the "Consume mapped inputs" toggle (keyboards and mice only).</summary>
        public bool ShowConsumeToggle => DeviceTypeKey == "Keyboard" || DeviceTypeKey == "Mouse";

        /// <summary>True for PadForge-internal virtual sources (web controllers, web
        /// touchpads, the on-screen touchpad overlay). Identified by a URI-scheme
        /// DevicePath (web://, overlay://) instead of a real Windows HID path.
        /// HidHide can't blacklist what isn't a Windows HID device, so the
        /// "Hide from games" toggle hides itself for these sources.</summary>
        public bool IsInternalVirtual =>
            !string.IsNullOrEmpty(_devicePath)
            && (_devicePath.StartsWith("web://", StringComparison.Ordinal)
             || _devicePath.StartsWith("overlay://", StringComparison.Ordinal)
             || _devicePath.StartsWith("midi://", StringComparison.Ordinal)
             || _devicePath.StartsWith("peer://", StringComparison.Ordinal)
             // PC/SC readers aren't HID devices either: the checkbox was a
             // silent no-op (fake VID/PID resolves no instance, audit M3).
             || _devicePath.StartsWith("nfc://", StringComparison.Ordinal));

        /// <summary>True when at least one input-hiding toggle would be shown,
        /// so the "Input Hiding" section can hide its heading along with its
        /// (now-empty) body when nothing applies.</summary>
        public bool ShowInputHidingSection => !IsInternalVirtual;

        /// <summary>True when the "Input Mode" section (Force raw joystick mode)
        /// should be shown. Only real gamepads — virtual web/overlay sources are
        /// WYSIWYG and have no SDL gamepad-mapping layer to bypass.</summary>
        public bool ShowInputModeSection => IsGamepad && !IsInternalVirtual;

        /// <summary>True when either the Input Mode or Input Hiding section will
        /// render. Gates the Separator that sits between Slot Assignment and those
        /// sections so a virtual device doesn't leave a dangling divider.</summary>
        public bool ShowInputModeOrHidingSection => ShowInputModeSection || ShowInputHidingSection;

        // ─────────────────────────────────────────────
        //  Device path
        // ─────────────────────────────────────────────

        private string _devicePath = string.Empty;

        /// <summary>File system device path (for diagnostics).</summary>
        public string DevicePath
        {
            get => _devicePath;
            set
            {
                if (SetProperty(ref _devicePath, value))
                {
                    OnPropertyChanged(nameof(IsInternalVirtual));
                    OnPropertyChanged(nameof(ShowInputHidingSection));
                    OnPropertyChanged(nameof(ShowInputModeSection));
                    OnPropertyChanged(nameof(ShowInputModeOrHidingSection));
                    OnPropertyChanged(nameof(IsBluetoothLink));
                    OnPropertyChanged(nameof(DossierConnectionPath));
                }
            }
        }

        /// <summary>The connection path the dossier shows when there is no HidHide
        /// instance path to display (the existing PATH row). Covers bridged devices like
        /// the DS3, whose real transport path (BthPS3 PDO / WinUSB interface) is surfaced
        /// through <see cref="Common.SdlDeviceWrapper.ExternalDevicePathProvider"/> even
        /// though SDL reports no path. Empty for internal/virtual sources and when a
        /// HidHide path already occupies the PATH row.</summary>
        public string DossierConnectionPath =>
            (!string.IsNullOrEmpty(_devicePath) && string.IsNullOrEmpty(_hidHideInstancePath) && !IsInternalVirtual)
                ? _devicePath : string.Empty;

        /// <summary>True when the device is known to be linked over
        /// Bluetooth (classic {00001124}/BTHENUM, BLE {00001812}, the SDL
        /// fork's BLE Switch 2 driver identified by VID/PID plus empty path,
        /// or a Microsoft Xbox pad wearing a Bluetooth-mode PID), the same
        /// classifier the slot-card transport glyph uses. Drives the dossier
        /// LINK line, which renders only when Bluetooth is a positive fact:
        /// a non-BT path may be USB, a wireless dongle, or a virtual source,
        /// so no transport is claimed for those (#175 item 7). Xbox pads
        /// over BT (XInput#N paths) answer by PID, see
        /// <see cref="Common.DeviceTransport"/>.</summary>
        public bool IsBluetoothLink => Common.DeviceTransport.IsBluetooth(_devicePath, _vendorId, _productId);

        private string _hidHideInstancePath = string.Empty;

        /// <summary>Resolved HID instance path used for HidHide blacklisting.</summary>
        public string HidHideInstancePath
        {
            get => _hidHideInstancePath;
            set
            {
                if (SetProperty(ref _hidHideInstancePath, value))
                    OnPropertyChanged(nameof(DossierConnectionPath));
            }
        }

        // ─────────────────────────────────────────────
        //  Display
        // ─────────────────────────────────────────────

        /// <summary>True if this device is recognized as a gamepad (SDL or custom mapping).</summary>
        public bool IsGamepad => DeviceTypeKey == "Gamepad";

        /// <summary>True if this device can have community mappings submitted (joysticks only, not gamepads/mice/keyboards).</summary>
        public bool ShowSubmitMapping => DeviceTypeKey != "Gamepad" && DeviceTypeKey != "Mouse" && DeviceTypeKey != "Keyboard" && DeviceTypeKey != "Touchpad" && DeviceTypeKey != "Midi" && DeviceTypeKey != "Nfc";

        /// <summary>True for an NFC reader (issue #150): shows the "Register/Manage
        /// NFC Tags" button, which opens the tap-to-name registration flow.</summary>
        public bool ShowRegisterNfcTag => DeviceTypeKey == "Nfc";

        /// <summary>Capabilities summary string for display.</summary>
        public string CapabilitiesSummary
        {
            get
            {
                bool isTouchpadType = DeviceTypeKey == "Touchpad";

                // Touchpad-only devices: skip "0 axes, 0 buttons, 0 POV" noise.
                var sb = new System.Text.StringBuilder();
                if (!isTouchpadType)
                    sb.Append(string.Format(Strings.Instance.Devices_CapsSummary_Format, _axisCount, _buttonCount, _povCount));

                void Append(string cap) { if (sb.Length > 0) sb.Append(", "); sb.Append(cap); }

                if (_hasRumble) Append(Strings.Instance.Devices_Rumble);
                if (_hasGyro) Append(Strings.Instance.Devices_Gyro);
                if (_hasAccel) Append(Strings.Instance.Devices_Accel);
                // Only show "Touchpad" capability on non-touchpad-type devices (e.g. DualSense gamepad).
                if (_hasTouchpad && !isTouchpadType) Append(Strings.Instance.Btn_Touchpad);

                return sb.Length > 0 ? sb.ToString() : Strings.Instance.Btn_Touchpad;
            }
        }

        /// <summary>
        /// Refreshes computed display properties.
        /// </summary>
        public void NotifyDisplayChanged()
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CapabilitiesSummary));
            OnPropertyChanged(nameof(VendorIdHex));
            OnPropertyChanged(nameof(ProductIdHex));
            OnPropertyChanged(nameof(HasVidPid));
        }

        public override string ToString()
        {
            return $"{_deviceName} [{StatusText}]";
        }
    }

    /// <summary>
    /// Display item for a single slot assignment badge (icon + number).
    /// </summary>
    public class SlotBadge
    {
        public int SlotIndex { get; set; }
        public int SlotNumber { get; set; }

        /// <summary>VC family of the slot, captured from the type-group walk
        /// that numbers the badge. Drives the branded icon (#175 item 12).</summary>
        public Engine.VirtualControllerType VcType { get; set; }

        /// <summary>Branded path geometry for the pill, null for glyph types.</summary>
        public System.Windows.Media.Geometry TypeGeometry => SlotTypeIconMap.GeometryFor(VcType);

        /// <summary>MDL2 glyph for the non-vector types, empty otherwise.</summary>
        public string TypeGlyph => SlotTypeIconMap.GlyphFor(VcType);
    }

    /// <summary>Branded VC-type icon lookup for the slot pills (#175 phase 2
    /// item 12). The three vector families reuse the exact geometries the
    /// sidebar cards and Dashboard wear (Common.ControllerIcons); MIDI and
    /// KbM have no vector logo anywhere in the app and keep their MDL2
    /// glyphs (E8D6 piano, E961 input), mirroring
    /// MainWindow.UpdateControllerNavItemContent's type row.</summary>
    internal static class SlotTypeIconMap
    {
        private static readonly System.Windows.Media.Geometry XboxGeometry = ParseFrozen(Common.ControllerIcons.XboxSvgPath);
        private static readonly System.Windows.Media.Geometry PlayStationGeometry = ParseFrozen(Common.ControllerIcons.DS4SvgPath);
        private static readonly System.Windows.Media.Geometry NintendoGeometry = ParseFrozen(Common.ControllerIcons.SwitchSvgPath);
        private static readonly System.Windows.Media.Geometry ExtendedGeometry = ParseFrozen(Common.ControllerIcons.ExtendedSvgPath);

        private static System.Windows.Media.Geometry ParseFrozen(string pathData)
        {
            var g = System.Windows.Media.Geometry.Parse(pathData);
            g.Freeze();
            return g;
        }

        /// <summary>Branded path geometry, or null for the glyph-only types.</summary>
        public static System.Windows.Media.Geometry GeometryFor(Engine.VirtualControllerType type) => type switch
        {
            Engine.VirtualControllerType.PlayStation => PlayStationGeometry,
            Engine.VirtualControllerType.Nintendo => NintendoGeometry,
            Engine.VirtualControllerType.Extended => ExtendedGeometry,
            Engine.VirtualControllerType.Midi => null,
            Engine.VirtualControllerType.KeyboardMouse => null,
            _ => XboxGeometry
        };

        /// <summary>MDL2 glyph for the types without a vector logo; empty otherwise.</summary>
        public static string GlyphFor(Engine.VirtualControllerType type) => type switch
        {
            Engine.VirtualControllerType.Midi => "\uE8D6",
            Engine.VirtualControllerType.KeyboardMouse => "\uE961",
            _ => string.Empty
        };
    }
}
