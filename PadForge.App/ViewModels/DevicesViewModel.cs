using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Resources.Strings;

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
            Title = Strings.Instance.Devices_Title;
        }

        protected override void OnCultureChanged()
        {
            Title = Strings.Instance.Devices_Title;
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
        //  Type facet filter (#175 phase 2 item 13)
        // ─────────────────────────────────────────────

        private string _selectedFacet = "ALL";

        /// <summary>Active facet token: ALL / GAMEPAD / KEYBOARD / MOUSE /
        /// OTHER. Locale-neutral literals per the DZ / PEAK precedent; the
        /// chips' active-state DataTriggers key off the raw token.</summary>
        public string SelectedFacet
        {
            get => _selectedFacet;
            set => SetFacet(value);
        }

        private System.ComponentModel.ICollectionView _devicesView;

        /// <summary>Applies a facet filter to the device list. Filters the
        /// default collection view the ListBox already binds through, so the
        /// XAML keeps its plain ItemsSource="{Binding Devices}".</summary>
        public void SetFacet(string facet)
        {
            if (string.IsNullOrEmpty(facet) || _selectedFacet == facet)
                return;
            _selectedFacet = facet;
            OnPropertyChanged(nameof(SelectedFacet));

            _devicesView ??= System.Windows.Data.CollectionViewSource.GetDefaultView(Devices);
            if (facet == "ALL")
                _devicesView.Filter = null;
            else
                _devicesView.Filter = o => o is DeviceRowViewModel d && FacetOf(d) == facet;
        }

        /// <summary>Facet bucket for a row. GAMEPAD covers the stick classes
        /// (everything InputService keys off a joystick-family CapType);
        /// OTHER is whatever is left (touchpads, MIDI, NFC, consumer
        /// collections, unclassified).</summary>
        private static string FacetOf(DeviceRowViewModel d) => d.DeviceTypeKey switch
        {
            "Gamepad" or "FirstPerson" or "Supplemental" => "GAMEPAD",
            // Joysticks and wheels get their own facets (maintainer request
            // 2026-07-06): flight sticks bucket with joysticks.
            "Joystick" or "FlightStick" => "JOYSTICK",
            "Wheel" => "WHEEL",
            "Keyboard" => "KEYBOARD",
            "Mouse" => "MOUSE",
            _ => "OTHER"
        };

        private int _facetCountJoystick;
        /// <summary>Row count behind the JOYSTICK chip (joysticks + flight sticks).</summary>
        public int FacetCountJoystick
        {
            get => _facetCountJoystick;
            set => SetProperty(ref _facetCountJoystick, value);
        }

        private int _facetCountWheel;
        /// <summary>Row count behind the WHEEL chip.</summary>
        public int FacetCountWheel
        {
            get => _facetCountWheel;
            set => SetProperty(ref _facetCountWheel, value);
        }

        private int _facetCountGamepad;
        /// <summary>Row count behind the GAMEPAD chip. The ALL chip binds TotalCount.</summary>
        public int FacetCountGamepad
        {
            get => _facetCountGamepad;
            set => SetProperty(ref _facetCountGamepad, value);
        }

        private int _facetCountKeyboard;
        /// <summary>Row count behind the KEYBOARD chip.</summary>
        public int FacetCountKeyboard
        {
            get => _facetCountKeyboard;
            set => SetProperty(ref _facetCountKeyboard, value);
        }

        private int _facetCountMouse;
        /// <summary>Row count behind the MOUSE chip.</summary>
        public int FacetCountMouse
        {
            get => _facetCountMouse;
            set => SetProperty(ref _facetCountMouse, value);
        }

        private int _facetCountOther;
        /// <summary>Row count behind the OTHER chip.</summary>
        public int FacetCountOther
        {
            get => _facetCountOther;
            set => SetProperty(ref _facetCountOther, value);
        }

        // ─────────────────────────────────────────────
        //  Raw state display (structured, for selected device)
        // ─────────────────────────────────────────────

        /// <summary>Structured axis values for visual display (progress bars).</summary>
        public ObservableCollection<AxisDisplayItem> RawAxes { get; } = new();

        /// <summary>Structured button states for visual display (circles).</summary>
        public ObservableCollection<ButtonDisplayItem> RawButtons { get; } = new();

        /// <summary>Named NFC tag rows for the selected reader's live preview
        /// (issue #150): "Any NFC Tag" plus each registered tag, highlighting while
        /// tapped. Shown instead of the numbered-circle grid for NFC readers.</summary>
        public ObservableCollection<NfcTagDisplayItem> NfcTags { get; } = new();

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

        private bool _isMouseDevice;
        /// <summary>Whether the currently selected device is a mouse.</summary>
        public bool IsMouseDevice
        {
            get => _isMouseDevice;
            set => SetProperty(ref _isMouseDevice, value);
        }

        private bool _isTouchpadDevice;
        /// <summary>Whether the currently selected device is a touchpad.</summary>
        public bool IsTouchpadDevice
        {
            get => _isTouchpadDevice;
            set => SetProperty(ref _isTouchpadDevice, value);
        }

        private bool _isMidiDevice;
        /// <summary>Whether the currently selected device is a MIDI input
        /// device (drives the MidiPreviewView piano + CC preview, issue #128).</summary>
        public bool IsMidiDevice
        {
            get => _isMidiDevice;
            set => SetProperty(ref _isMidiDevice, value);
        }

        private bool _isNfcDevice;
        /// <summary>Whether the selected device is an NFC reader (issue #150):
        /// drives the named tag preview list in place of the numbered button grid.</summary>
        public bool IsNfcDevice
        {
            get => _isNfcDevice;
            set => SetProperty(ref _isNfcDevice, value);
        }

        private bool _isConsumerDevice;
        /// <summary>Whether the selected device is a Consumer Control
        /// collection (issue #168): drives the named button-chip preview in
        /// place of the numbered grid and the empty axes section.</summary>
        public bool IsConsumerDevice
        {
            get => _isConsumerDevice;
            set => SetProperty(ref _isConsumerDevice, value);
        }

        /// <summary>Named button chips for the Consumer Control preview
        /// (issue #168): the canonical table's localized names, plus any
        /// session-dynamic usages the device has reported.</summary>
        public ObservableCollection<ConsumerButtonDisplayItem> ConsumerButtons { get; } = new();

        /// <summary>Rebuilds the NFC tag preview rows from the registry: "Any NFC
        /// Tag" first, then each registered tag at its stable button index. Called on
        /// device selection and whenever the tag registry changes.</summary>
        public void RebuildNfcTags()
        {
            NfcTags.Clear();
            NfcTags.Add(new NfcTagDisplayItem { Name = Strings.Instance.Mapping_NfcAnyTag, Uid = string.Empty, Button = 0 });
            foreach (var t in PadForge.Common.Input.NfcTagRegistry.Tags)
                NfcTags.Add(new NfcTagDisplayItem { Name = t.Name, Uid = t.Uid, Button = t.Button });
        }

        /// <summary>Live MIDI input state of the selected MIDI device, set
        /// each poll tick by InputService. MidiPreviewView (input mode)
        /// polls this every render frame. Null until the first message.</summary>
        public PadForge.Engine.MidiInputState LiveMidi { get; set; }

        private bool _hasTouchpadData;
        /// <summary>Whether the selected device has touchpad data to display.</summary>
        public bool HasTouchpadData
        {
            get => _hasTouchpadData;
            set => SetProperty(ref _hasTouchpadData, value);
        }

        // Touchpad finger positions (0-1 normalized) for the preview.
        // Up to 5 simultaneous contacts (Windows PTP max; SDL gamepad
        // touchpads typically expose 1-2). Touchpad click pressed
        // state is shown in the Buttons grid at slot 16
        // (SDL_GAMEPAD_BUTTON_TOUCHPAD), not on this preview's
        // background.
        private double _touchpadX0, _touchpadY0, _touchpadX1, _touchpadY1,
                       _touchpadX2, _touchpadY2, _touchpadX3, _touchpadY3,
                       _touchpadX4, _touchpadY4;
        private bool _touchpadDown0, _touchpadDown1, _touchpadDown2,
                     _touchpadDown3, _touchpadDown4;
        public double TouchpadX0 { get => _touchpadX0; set => SetProperty(ref _touchpadX0, value); }
        public double TouchpadY0 { get => _touchpadY0; set => SetProperty(ref _touchpadY0, value); }
        public double TouchpadX1 { get => _touchpadX1; set => SetProperty(ref _touchpadX1, value); }
        public double TouchpadY1 { get => _touchpadY1; set => SetProperty(ref _touchpadY1, value); }
        public double TouchpadX2 { get => _touchpadX2; set => SetProperty(ref _touchpadX2, value); }
        public double TouchpadY2 { get => _touchpadY2; set => SetProperty(ref _touchpadY2, value); }
        public double TouchpadX3 { get => _touchpadX3; set => SetProperty(ref _touchpadX3, value); }
        public double TouchpadY3 { get => _touchpadY3; set => SetProperty(ref _touchpadY3, value); }
        public double TouchpadX4 { get => _touchpadX4; set => SetProperty(ref _touchpadX4, value); }
        public double TouchpadY4 { get => _touchpadY4; set => SetProperty(ref _touchpadY4, value); }
        public bool TouchpadDown0 { get => _touchpadDown0; set => SetProperty(ref _touchpadDown0, value); }
        public bool TouchpadDown1 { get => _touchpadDown1; set => SetProperty(ref _touchpadDown1, value); }
        public bool TouchpadDown2 { get => _touchpadDown2; set => SetProperty(ref _touchpadDown2, value); }
        public bool TouchpadDown3 { get => _touchpadDown3; set => SetProperty(ref _touchpadDown3, value); }
        public bool TouchpadDown4 { get => _touchpadDown4; set => SetProperty(ref _touchpadDown4, value); }

        // Second touchpad surface (Steam Controller 2026 / Steam Deck / original
        // Steam Controller). Same 5-finger preview shape as the first pad; shown
        // only when HasSecondTouchpadData is true (device reports 2+ pads).
        private bool _hasSecondTouchpadData;
        /// <summary>Whether the selected device exposes a second touchpad surface.</summary>
        public bool HasSecondTouchpadData
        {
            get => _hasSecondTouchpadData;
            set { if (SetProperty(ref _hasSecondTouchpadData, value)) OnPropertyChanged(nameof(TouchpadLabel)); }
        }

        /// <summary>Label for the first touchpad preview. Numbered ("Touchpad 1")
        /// only on multi-pad devices, matching the mapping picker's 1-based pad
        /// numbering so the user can tell the two surfaces apart. Single-pad
        /// devices keep the plain "Touchpad".</summary>
        public string TouchpadLabel => HasSecondTouchpadData
            ? $"{PadForge.Resources.Strings.Strings.Instance.Btn_Touchpad} 1"
            : PadForge.Resources.Strings.Strings.Instance.Btn_Touchpad;
        /// <summary>Label for the second touchpad preview ("Touchpad 2").</summary>
        public string Touchpad2Label => $"{PadForge.Resources.Strings.Strings.Instance.Btn_Touchpad} 2";
        private double _pad2X0, _pad2Y0, _pad2X1, _pad2Y1, _pad2X2, _pad2Y2,
                       _pad2X3, _pad2Y3, _pad2X4, _pad2Y4;
        private bool _pad2Down0, _pad2Down1, _pad2Down2, _pad2Down3, _pad2Down4;
        public double Pad2X0 { get => _pad2X0; set => SetProperty(ref _pad2X0, value); }
        public double Pad2Y0 { get => _pad2Y0; set => SetProperty(ref _pad2Y0, value); }
        public double Pad2X1 { get => _pad2X1; set => SetProperty(ref _pad2X1, value); }
        public double Pad2Y1 { get => _pad2Y1; set => SetProperty(ref _pad2Y1, value); }
        public double Pad2X2 { get => _pad2X2; set => SetProperty(ref _pad2X2, value); }
        public double Pad2Y2 { get => _pad2Y2; set => SetProperty(ref _pad2Y2, value); }
        public double Pad2X3 { get => _pad2X3; set => SetProperty(ref _pad2X3, value); }
        public double Pad2Y3 { get => _pad2Y3; set => SetProperty(ref _pad2Y3, value); }
        public double Pad2X4 { get => _pad2X4; set => SetProperty(ref _pad2X4, value); }
        public double Pad2Y4 { get => _pad2Y4; set => SetProperty(ref _pad2Y4, value); }
        public bool Pad2Down0 { get => _pad2Down0; set => SetProperty(ref _pad2Down0, value); }
        public bool Pad2Down1 { get => _pad2Down1; set => SetProperty(ref _pad2Down1, value); }
        public bool Pad2Down2 { get => _pad2Down2; set => SetProperty(ref _pad2Down2, value); }
        public bool Pad2Down3 { get => _pad2Down3; set => SetProperty(ref _pad2Down3, value); }
        public bool Pad2Down4 { get => _pad2Down4; set => SetProperty(ref _pad2Down4, value); }

        private double _mouseMotionX, _mouseMotionY;
        public double MouseMotionX { get => _mouseMotionX; set => SetProperty(ref _mouseMotionX, value); }
        public double MouseMotionY { get => _mouseMotionY; set => SetProperty(ref _mouseMotionY, value); }

        private double _mouseScrollIntensity;
        /// <summary>Normalized scroll intensity: positive = up, negative = down. Range -1 to 1.</summary>
        public double MouseScrollIntensity { get => _mouseScrollIntensity; set => SetProperty(ref _mouseScrollIntensity, value); }

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

        // Aux (left-side) accelerometer preview (#199): Nunchuk / left Joy-Con.

        private bool _hasAccelAuxData;
        public bool HasAccelAuxData { get => _hasAccelAuxData; set => SetProperty(ref _hasAccelAuxData, value); }

        private double _accelAuxX, _accelAuxY, _accelAuxZ;
        public double AccelAuxX { get => _accelAuxX; set => SetProperty(ref _accelAuxX, value); }
        public double AccelAuxY { get => _accelAuxY; set => SetProperty(ref _accelAuxY, value); }
        public double AccelAuxZ { get => _accelAuxZ; set => SetProperty(ref _accelAuxZ, value); }

        // Aux (left-side) gyro preview (#252): the left Joy-Con of a pair.
        // Paired with the accel readout above so a user can see WHICH half
        // a reading comes from before binding it.

        private bool _hasGyroAuxData;
        public bool HasGyroAuxData { get => _hasGyroAuxData; set => SetProperty(ref _hasGyroAuxData, value); }

        private double _gyroAuxX, _gyroAuxY, _gyroAuxZ;
        public double GyroAuxX { get => _gyroAuxX; set => SetProperty(ref _gyroAuxX, value); }
        public double GyroAuxY { get => _gyroAuxY; set => SetProperty(ref _gyroAuxY, value); }
        public double GyroAuxZ { get => _gyroAuxZ; set => SetProperty(ref _gyroAuxZ, value); }

        // v3.3 — gyro UI moved to the Pad page Gyro tab. Calibration
        // label, live rate readouts, sensitivity / deadzone / smoothing
        // / acceleration / curve / units sliders all live on PadViewModel
        // now (per-(device, slot) tuning matches FFB / Adaptive Triggers
        // / Lighting tab pattern). DevicesViewModel intentionally has
        // no gyro UI state.

        /// <summary>Tracks which device's collections are currently populated.</summary>
        internal Guid LastRawStateDeviceGuid { get; set; }

        /// <summary>
        /// Rebuilds the raw state collections for a new device.
        /// <paramref name="buttonIndices"/> is the sparse list of button
        /// positions the device actually exposes — for SDL3 gamepads this
        /// skips extended slots (paddles, Misc buttons) the device doesn't
        /// have, so the preview only shows real buttons. Item Index values
        /// are stored verbatim and used by the InputService update loop
        /// to read the matching <c>state.Buttons[Index]</c>.
        /// </summary>
        internal void RebuildRawStateCollections(int axisCount, IReadOnlyList<int> buttonIndices, int povCount, bool isKeyboard = false, bool isMouse = false, bool isTouchpad = false, bool isMidi = false, bool isNfc = false, IReadOnlyList<ConsumerButtonDisplayItem> consumerButtons = null)
        {
            IsNfcDevice = isNfc;
            if (isNfc) RebuildNfcTags(); else NfcTags.Clear();

            // Consumer Control (issue #168): named chips replace both the
            // numbered grid and the (empty) axes section, the same treatment
            // the NFC reader's tag list got in #150.
            bool isConsumer = consumerButtons != null;
            IsConsumerDevice = isConsumer;
            ConsumerButtons.Clear();
            if (isConsumer)
            {
                foreach (var b in consumerButtons)
                    ConsumerButtons.Add(b);
            }

            RawAxes.Clear();
            if (!isMouse && !isMidi && !isConsumer)
            {
                for (int i = 0; i < axisCount; i++)
                    RawAxes.Add(new AxisDisplayItem { Index = i, Name = string.Format(Strings.Instance.Devices_Axis_Format, i) });
            }

            RawButtons.Clear();
            KeyboardKeys.Clear();
            IsKeyboardDevice = isKeyboard;
            IsMouseDevice = isMouse;
            IsTouchpadDevice = isTouchpad;

            IsMidiDevice = isMidi;
            if (isMidi)
            {
                // The MidiPreviewView (input mode) renders the piano + CCs
                // directly from LiveMidi; no per-key VM collections. Clear
                // the generic lists so a previously-selected gamepad's axes /
                // buttons / POV hats don't leak into the MIDI view.
                RawPovs.Clear();
                SelectedButtonTotal = 0;
                return;
            }

            int buttonCount = buttonIndices?.Count ?? 0;

            if (isKeyboard)
            {
                // Build positioned keyboard layout instead of flat button list.
                foreach (var key in KeyboardKeyItem.BuildLayout())
                    KeyboardKeys.Add(key);
            }
            else if (!isConsumer) // consumer devices use the named-chip list
            {
                // Mouse visual handles button display, but RawButtons still
                // needs entries so InputService can update IsPressed for the
                // mouse preview's left/middle/right/X1/X2 lookups by index.
                // DisplayNumber is the REAL state index, not the grid
                // ordinal. With ordinal numbering a sparse device lied about
                // its identifiers: the Wii Remote's raw D-pad duplicates
                // (buttons 22-25, positions 11-14 in the sparse list) showed
                // as "11-14" here while the mapping picker, the recorder, and
                // every stored descriptor call them Button 22-25. The owner
                // burned a bench round on that mismatch (disc #198 follow-up,
                // 2026-07-10). Preview numbering must match the mappable
                // names, gaps and all.
                for (int i = 0; i < buttonCount; i++)
                    RawButtons.Add(new ButtonDisplayItem { Index = buttonIndices[i], DisplayNumber = buttonIndices[i] });
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
            IsMouseDevice = false;
            IsTouchpadDevice = false;
            IsMidiDevice = false;
            IsNfcDevice = false;
            NfcTags.Clear();
            IsConsumerDevice = false;
            ConsumerButtons.Clear();
            LiveMidi = null;
            HasRawData = false;
            HasGyroData = false;
            HasAccelData = false;
            HasAccelAuxData = false;
            HasGyroAuxData = false;
            HasTouchpadData = false;
            HasSecondTouchpadData = false;
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

        private RelayCommand _pairCommand;

        /// <summary>Command to open the Bluetooth pairing flow for controllers
        /// that need an in-app pairing ceremony (Wii controllers, issue #116).</summary>
        public RelayCommand PairCommand =>
            _pairCommand ??= new RelayCommand(
                () => PairRequested?.Invoke(this, EventArgs.Empty));

        private RelayCommand<int> _assignToSlotCommand;

        /// <summary>
        /// Command to assign the selected device to a pad slot.
        /// Parameter is the slot index (0–15).
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
                () => HasSelectedDevice);

        /// <summary>Raised when a refresh is requested.</summary>
        public event EventHandler RefreshRequested;

        /// <summary>Raised when the user opens the Bluetooth pairing flow.</summary>
        public event EventHandler PairRequested;

        /// <summary>Raised when the user assigns a device to a slot. Arg = slot index.</summary>
        public event EventHandler<int> AssignToSlotRequested;

        /// <summary>Raised when the user hides a device. Arg = instance GUID.</summary>
        public event EventHandler<Guid> HideDeviceRequested;

        /// <summary>Raised when the user removes a device. Arg = instance GUID.</summary>
        public event EventHandler<Guid> RemoveDeviceRequested;

        /// <summary>Raised when the user toggles a slot assignment. Arg = slot index.</summary>
        public event EventHandler<int> ToggleSlotRequested;

        /// <summary>Raised when a device's input hiding toggle changes. Arg = instance GUID.</summary>
        public event EventHandler<Guid> DeviceHidingChanged;

        /// <summary>
        /// Notifies that a device's HidHide or ConsumeInput toggle was changed.
        /// </summary>
        public void NotifyDeviceHidingChanged(Guid instanceGuid)
        {
            DeviceHidingChanged?.Invoke(this, instanceGuid);
        }

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
            // Walk SlotOrders in type-group order (Xbox → PlayStation →
            // Extended → KbM → MIDI) so the assignment buttons match the
            // dashboard cards' visual order. Iterating raw padIndex here
            // would put a creation-order-first PlayStation before an
            // Xbox added later, even though the card layout shows Xbox
            // at #1. The walk's group type rides along so the pill can
            // wear the family's branded icon (#175 phase 2 item 12).
            var activeSlots = new System.Collections.Generic.List<(int PadIndex, Engine.VirtualControllerType Type)>();
            foreach (var groupType in Engine.VirtualControllerGroups.InOrder)
            {
                foreach (int padIndex in SettingsManager.SlotOrders.GetOrderFor(groupType))
                {
                    if (padIndex >= 0
                        && padIndex < InputManager.MaxPads
                        && SettingsManager.SlotCreated[padIndex])
                    {
                        activeSlots.Add((padIndex, groupType));
                    }
                }
            }

            // Get the selected device's current assignments.
            var assignedSlots = _selectedDevice != null
                ? SettingsManager.GetAssignedSlots(_selectedDevice.InstanceGuid)
                : new System.Collections.Generic.List<int>();

            // Compute 1-based slot numbers. A type change alone (same pad
            // index, new family) also counts as a structure change so the
            // pill's icon follows.
            bool structureChanged = activeSlots.Count != ActiveSlotItems.Count;
            if (!structureChanged)
            {
                for (int i = 0; i < activeSlots.Count; i++)
                {
                    if (ActiveSlotItems[i].PadIndex != activeSlots[i].PadIndex
                        || ActiveSlotItems[i].VcType != activeSlots[i].Type)
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
                foreach (var slot in activeSlots)
                {
                    num++;
                    ActiveSlotItems.Add(new SlotButtonItem
                    {
                        PadIndex = slot.PadIndex,
                        SlotNumber = num,
                        VcType = slot.Type,
                        IsAssigned = assignedSlots.Contains(slot.PadIndex)
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
        /// Re-runs SetAssignedSlots on every device row so the slot badges
        /// recompute their global numbers from the current SlotOrders. Call
        /// this any time the type-group order changes (slot create / delete /
        /// type change / swap / move). Without it, the badge numbers drift
        /// out of sync with the dashboard cards' visual ordering even
        /// though the assignments themselves haven't changed.
        /// </summary>
        public void RefreshAllSlotBadges()
        {
            foreach (var d in Devices)
            {
                if (d == null) continue;
                d.SetAssignedSlots(SettingsManager.GetAssignedSlots(d.InstanceGuid));
            }
        }

        /// <summary>
        /// Updates the device counts from the Devices collection, including
        /// the facet chip counts (#175 phase 2 item 13). Re-runs the active
        /// facet filter so rows whose type key changed in place re-bucket.
        /// </summary>
        public void RefreshCounts()
        {
            TotalCount = Devices.Count;
            int online = 0, gamepad = 0, joystick = 0, wheel = 0, keyboard = 0, mouse = 0, other = 0;
            foreach (var d in Devices)
            {
                if (d.IsOnline)
                    online++;
                switch (FacetOf(d))
                {
                    case "GAMEPAD": gamepad++; break;
                    case "JOYSTICK": joystick++; break;
                    case "WHEEL": wheel++; break;
                    case "KEYBOARD": keyboard++; break;
                    case "MOUSE": mouse++; break;
                    default: other++; break;
                }
            }
            OnlineCount = online;
            FacetCountGamepad = gamepad;
            FacetCountJoystick = joystick;
            FacetCountWheel = wheel;
            FacetCountKeyboard = keyboard;
            FacetCountMouse = mouse;
            FacetCountOther = other;

            if (_selectedFacet != "ALL")
                _devicesView?.Refresh();
        }
    }

    /// <summary>
    /// Represents a single virtual controller slot button on the Devices page.
    /// </summary>
    public class SlotButtonItem : ObservableObject
    {
        private int _padIndex;
        /// <summary>Zero-based pad slot index (0–15).</summary>
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

        private Engine.VirtualControllerType _vcType;
        /// <summary>VC family of the slot, captured from the type-group walk.
        /// Drives the pill's branded icon (#175 phase 2 item 12).</summary>
        public Engine.VirtualControllerType VcType
        {
            get => _vcType;
            set
            {
                if (SetProperty(ref _vcType, value))
                {
                    OnPropertyChanged(nameof(TypeGeometry));
                    OnPropertyChanged(nameof(TypeGlyph));
                }
            }
        }

        /// <summary>Branded path geometry for the pill, null for glyph types.</summary>
        public System.Windows.Media.Geometry TypeGeometry => SlotTypeIconMap.GeometryFor(_vcType);

        /// <summary>MDL2 glyph for the non-vector types, empty otherwise.</summary>
        public string TypeGlyph => SlotTypeIconMap.GlyphFor(_vcType);
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
        /// <summary>
        /// Underlying slot index into <c>state.Buttons[]</c>. May be sparse
        /// (e.g., 16 for a touchpad-click when the device skips earlier
        /// extended slots) and is not shown to the user.
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Displayed button number. Equals <see cref="Index"/> (the real
        /// state slot) so the preview agrees with the mapping picker and
        /// stored "Button N" descriptors. Sparse devices therefore show
        /// gaps, which is correct: the numbers are identifiers, not
        /// positions.
        /// </summary>
        public int DisplayNumber { get; set; }

        private bool _isPressed;
        /// <summary>Whether the button is currently pressed.</summary>
        public bool IsPressed
        {
            get => _isPressed;
            set => SetProperty(ref _isPressed, value);
        }
    }

    /// <summary>One named chip in the Consumer Control live preview
    /// (issue #168): a canonical button ("Play/Pause", "Voice Command", the
    /// reporter's OK), highlighting while held. Shown instead of the numbered
    /// grid, the same replacement the NFC reader got in #150.</summary>
    public class ConsumerButtonDisplayItem : ObservableObject
    {
        /// <summary>Slot index into <c>state.Buttons[]</c> (the canonical
        /// ConsumerUsageTable index, persistence-stable).</summary>
        public int Index { get; set; }

        /// <summary>Localized button name.</summary>
        public string Name { get; set; } = string.Empty;

        private bool _isPressed;
        /// <summary>Whether the button is currently held.</summary>
        public bool IsPressed
        {
            get => _isPressed;
            set => SetProperty(ref _isPressed, value);
        }
    }

    /// <summary>One row in the NFC reader's live tag preview (issue #150): a
    /// registered tag (or "Any NFC Tag"), highlighting while its button pulses.</summary>
    public class NfcTagDisplayItem : ObservableObject
    {
        public string Name { get; set; } = string.Empty;
        public string Uid { get; set; } = string.Empty;
        /// <summary>Stable raw-button index this tag occupies (0 = Any NFC Tag).</summary>
        public int Button { get; set; }
        /// <summary>TickCount64 of the last tap. The feed holds <see cref="IsActive"/>
        /// for a visible window past the ~175 ms button pulse so it doesn't flicker.</summary>
        public long LastActiveTick { get; set; }

        private bool _isActive;
        /// <summary>True while this tag is lit in the preview (recently tapped).</summary>
        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
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

            // ── Row 0: Esc, F1–F12, PrtSc / ScrLk / Pause cluster ──
            double y0 = 0;
            Add(0x1B, "Esc", 0, y0);
            for (int i = 0; i < 4; i++) Add(0x70 + i, $"F{i + 1}", 2 + i, y0);
            for (int i = 0; i < 4; i++) Add(0x74 + i, $"F{i + 5}", 6.5 + i, y0);
            for (int i = 0; i < 4; i++) Add(0x78 + i, $"F{i + 9}", 11 + i, y0);
            // PrtSc / ScrLk / Pause sit above the Ins / Hm / PU nav cluster
            // at the same x-column (nx = 15.5).
            Add(0x2C, "PrSc", 15.5, y0);
            Add(0x91, "ScLk", 16.5, y0);
            Add(0x13, "Pse",  17.5, y0);

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
