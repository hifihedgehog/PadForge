using System;
using System.ComponentModel;
using System.Xml.Serialization;

namespace PadForge.Engine.Data
{
    /// <summary>
    /// Data model for a single physical input device. Contains both serializable
    /// (settings-persisted) properties and runtime-only fields used during the
    /// input pipeline.
    /// 
    /// This replaces the former UserDevice that depended on SharpDX.DirectInput types.
    /// Key changes:
    ///   - LoadInstance() takes discrete values instead of a DeviceInstance
    ///   - LoadCapabilities() takes discrete values instead of Capabilities
    ///   - Runtime device reference is ISdlInputDevice (was Joystick)
    ///   - State fields renamed: InputState/InputUpdates/etc. (were Di* prefixed)
    ///   - JoState/JoUpdate fields removed entirely
    ///   - IsExclusiveMode field removed (SDL has no acquisition model)
    /// </summary>
    public partial class UserDevice : INotifyPropertyChanged
    {
        public UserDevice()
        {
            DateCreated = DateTime.Now;
            DateUpdated = DateTime.Now;
        }

        // ─────────────────────────────────────────────────────────────
        //  Serializable identity properties (persisted to XML)
        // ─────────────────────────────────────────────────────────────

        /// <summary>Unique GUID identifying this device instance (deterministic from device path).</summary>
        [XmlElement]
        public Guid InstanceGuid { get; set; }

        /// <summary>Human-readable instance name (e.g., "Xbox Controller").</summary>
        [XmlElement]
        public string InstanceName { get; set; } = string.Empty;

        /// <summary>Product GUID in PIDVID format for device family identification.</summary>
        [XmlElement]
        public Guid ProductGuid { get; set; }

        /// <summary>Human-readable product name.</summary>
        [XmlElement]
        public string ProductName { get; set; } = string.Empty;

        /// <summary>USB Vendor ID.</summary>
        [XmlElement]
        public ushort VendorId { get; set; }

        /// <summary>USB Product ID.</summary>
        [XmlElement]
        public ushort ProdId { get; set; }

        /// <summary>USB Product Version / Revision.</summary>
        [XmlElement]
        public ushort DevRevision { get; set; }

        /// <summary>Device file system path (used for instance GUID generation).</summary>
        [XmlElement]
        public string DevicePath { get; set; } = string.Empty;

        // ─────────────────────────────────────────────────────────────
        //  Serializable capability properties
        // ─────────────────────────────────────────────────────────────

        /// <summary>Number of axes on the device.</summary>
        [XmlElement]
        public int CapAxeCount { get; set; }

        /// <summary>Number of buttons on the device (gamepad-mapped count for gamepad devices).</summary>
        [XmlElement]
        public int CapButtonCount { get; set; }

        /// <summary>
        /// Total number of raw joystick buttons (before gamepad remapping).
        /// For gamepad devices, this is higher than <see cref="CapButtonCount"/> (11),
        /// exposing extra native buttons like DualSense touchpad or mic.
        /// For non-gamepad devices, this equals <see cref="CapButtonCount"/>.
        /// </summary>
        [XmlElement]
        public int RawButtonCount { get; set; }

        /// <summary>Number of POV hat switches on the device.</summary>
        [XmlElement]
        public int CapPovCount { get; set; }

        /// <summary>Device type constant from <see cref="InputDeviceType"/>.</summary>
        [XmlElement]
        public int CapType { get; set; }

        /// <summary>Device subtype (device-specific classification).</summary>
        [XmlElement]
        public int CapSubType { get; set; }

        /// <summary>Device capability flags.</summary>
        [XmlElement]
        public int CapFlags { get; set; }

        /// <summary>Whether the device has a gyroscope sensor.</summary>
        [XmlElement]
        public bool HasGyro { get; set; }

        /// <summary>Whether the device has an accelerometer sensor.</summary>
        [XmlElement]
        public bool HasAccel { get; set; }

        // ─────────────────────────────────────────────────────────────
        //  Serializable metadata
        // ─────────────────────────────────────────────────────────────

        /// <summary>Date when this device record was first created.</summary>
        [XmlElement]
        public DateTime DateCreated { get; set; }

        /// <summary>Date when this device record was last updated.</summary>
        [XmlElement]
        public DateTime DateUpdated { get; set; }

        /// <summary>Whether this device is currently enabled for mapping.</summary>
        [XmlElement]
        public bool IsEnabled { get; set; } = true;

        /// <summary>Whether this device is currently hidden from the UI.</summary>
        [XmlElement]
        public bool IsHidden { get; set; }

        /// <summary>User-assigned display name (overrides InstanceName in the UI if set).</summary>
        [XmlElement]
        public string DisplayName { get; set; } = string.Empty;

        // ─────────────────────────────────────────────────────────────
        //  Runtime-only fields (NOT serialized)
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The opened SDL device wrapper. This is the live handle used for state
        /// reading and rumble. Set during Step 1 (UpdateDevices) and cleared when
        /// the device is disconnected.
        /// </summary>
        [XmlIgnore]
        public ISdlInputDevice Device { get; set; }

        /// <summary>
        /// Whether this device is currently online (physically connected and opened).
        /// </summary>
        [XmlIgnore]
        public bool IsOnline { get; set; }

        /// <summary>
        /// Current input state snapshot. Written by the background thread (Step 2),
        /// read by the UI thread. Reference assignment is atomic.
        /// </summary>
        [XmlIgnore]
        public CustomInputState InputState { get; set; }

        /// <summary>
        /// Buffered input updates since the last poll cycle.
        /// </summary>
        [XmlIgnore]
        public CustomInputUpdate[] InputUpdates { get; set; }

        /// <summary>
        /// Timestamp of the current <see cref="InputState"/> reading.
        /// </summary>
        [XmlIgnore]
        public DateTime InputStateTime { get; set; }

        /// <summary>
        /// Previous input state (from the prior poll cycle), used for change detection.
        /// </summary>
        [XmlIgnore]
        public CustomInputState OldInputState { get; set; }

        /// <summary>
        /// Previous buffered updates (from the prior poll cycle).
        /// </summary>
        [XmlIgnore]
        public CustomInputUpdate[] OldInputUpdates { get; set; }

        /// <summary>
        /// Timestamp of the previous <see cref="OldInputState"/> reading.
        /// </summary>
        [XmlIgnore]
        public DateTime OldInputStateTime { get; set; }

        /// <summary>
        /// The "original" input state captured at the start of a recording session.
        /// Used by the recorder to detect deltas.
        /// </summary>
        [XmlIgnore]
        public CustomInputState OrgInputState { get; set; }

        /// <summary>
        /// Timestamp of the <see cref="OrgInputState"/> capture.
        /// </summary>
        [XmlIgnore]
        public DateTime OrgInputStateTime { get; set; }

        /// <summary>
        /// Bitmask of present axes (bit N set = axis N exists on the device).
        /// Computed from device objects during Step 1.
        /// </summary>
        [XmlIgnore]
        public int AxeMask { get; set; }

        /// <summary>
        /// Bitmask of force-feedback actuator axes.
        /// </summary>
        [XmlIgnore]
        public int ActuatorMask { get; set; }

        /// <summary>
        /// Total number of force-feedback actuator axes.
        /// </summary>
        [XmlIgnore]
        public int ActuatorCount { get; set; }

        /// <summary>
        /// Bitmask of present sliders.
        /// </summary>
        [XmlIgnore]
        public int SliderMask { get; set; }

        /// <summary>
        /// Array of device object metadata (axes, hats, buttons).
        /// Populated during Step 1.
        /// </summary>
        [XmlIgnore]
        public DeviceObjectItem[] DeviceObjects { get; set; }

        /// <summary>
        /// Array of device effect metadata (rumble capabilities).
        /// Populated during Step 1.
        /// </summary>
        [XmlIgnore]
        public DeviceEffectItem[] DeviceEffects { get; set; }

        /// <summary>
        /// Force feedback state tracker for this device.
        /// </summary>
        [XmlIgnore]
        public ForceFeedbackState ForceFeedbackState { get; set; }

        // ─────────────────────────────────────────────────────────────
        //  Convenience properties
        // ─────────────────────────────────────────────────────────────

        /// <summary>True if this device is a mouse (CapType == InputDeviceType.Mouse).</summary>
        [XmlIgnore]
        public bool IsMouse => CapType == InputDeviceType.Mouse;

        /// <summary>True if this device is a keyboard (CapType == InputDeviceType.Keyboard).</summary>
        [XmlIgnore]
        public bool IsKeyboard => CapType == InputDeviceType.Keyboard;

        /// <summary>True if the device has at least one force-feedback actuator, SDL rumble, or SDL haptic support.</summary>
        [XmlIgnore]
        public bool HasForceFeedback => ActuatorCount > 0 || (Device != null && (Device.HasRumble || Device.HasHaptic));

        /// <summary>
        /// Returns the display name for UI purposes: the user-assigned DisplayName if set,
        /// otherwise the InstanceName, otherwise the ProductName.
        /// </summary>
        [XmlIgnore]
        public string ResolvedName
        {
            get
            {
                if (!string.IsNullOrEmpty(DisplayName))
                    return DisplayName;
                if (!string.IsNullOrEmpty(InstanceName))
                    return InstanceName;
                return ProductName ?? "(Unknown Device)";
            }
        }

        /// <summary>
        /// Returns a status string for UI display.
        /// </summary>
        [XmlIgnore]
        public string StatusText
        {
            get
            {
                if (!IsEnabled) return "Disabled";
                if (IsOnline) return "Online";
                return "Offline";
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Loading methods (replace DirectInput DeviceInstance / Capabilities)
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Populates the device identity properties from discrete values.
        /// This replaces the former LoadInstance(DeviceInstance) method.
        /// </summary>
        /// <param name="instanceGuid">Deterministic instance GUID (from SdlDeviceWrapper).</param>
        /// <param name="instanceName">Device instance name.</param>
        /// <param name="productGuid">Product GUID in PIDVID format.</param>
        /// <param name="productName">Product name.</param>
        public void LoadInstance(Guid instanceGuid, string instanceName, Guid productGuid, string productName)
        {
            InstanceGuid = instanceGuid;
            InstanceName = instanceName ?? string.Empty;
            ProductGuid = productGuid;
            ProductName = productName ?? string.Empty;
            DateUpdated = DateTime.Now;
        }

        /// <summary>
        /// Populates the device capability properties from discrete values.
        /// This replaces the former LoadCapabilities(Capabilities) method.
        /// </summary>
        /// <param name="axeCount">Number of axes.</param>
        /// <param name="buttonCount">Number of buttons.</param>
        /// <param name="povCount">Number of POV hats.</param>
        /// <param name="type">Device type (see <see cref="InputDeviceType"/>).</param>
        /// <param name="subtype">Device subtype.</param>
        /// <param name="flags">Capability flags.</param>
        public void LoadCapabilities(int axeCount, int buttonCount, int povCount,
            int type, int subtype, int flags)
        {
            CapAxeCount = axeCount;
            CapButtonCount = buttonCount;
            CapPovCount = povCount;
            CapType = type;
            CapSubType = subtype;
            CapFlags = flags;
            DateUpdated = DateTime.Now;
        }

        /// <summary>
        /// Populates the device identity and capabilities from an <see cref="SdlDeviceWrapper"/>.
        /// Convenience method that calls both <see cref="LoadInstance"/> and <see cref="LoadCapabilities"/>.
        /// </summary>
        /// <param name="wrapper">An opened SDL device wrapper.</param>
        public void LoadFromSdlDevice(SdlDeviceWrapper wrapper)
        {
            if (wrapper == null)
                throw new ArgumentNullException(nameof(wrapper));

            LoadFromDevice(wrapper);
            DevRevision = wrapper.ProductVersion;
        }

        /// <summary>
        /// Populates the device identity and capabilities from any <see cref="ISdlInputDevice"/>
        /// (joystick, keyboard, or mouse). Common logic shared by all device types.
        /// </summary>
        private void LoadFromDevice(ISdlInputDevice wrapper)
        {
            LoadInstance(
                wrapper.InstanceGuid,
                wrapper.Name,
                wrapper.ProductGuid,
                wrapper.Name);

            LoadCapabilities(
                wrapper.NumAxes,
                wrapper.NumButtons,
                wrapper.NumHats,
                wrapper.GetInputDeviceType(),
                0, // subtype not available from SDL
                0  // flags not available from SDL
            );

            // Store the raw joystick button count (may exceed NumButtons for gamepad devices).
            RawButtonCount = Math.Max(wrapper.RawButtonCount, wrapper.NumButtons);

            // Sensor capabilities.
            HasGyro = wrapper.HasGyro;
            HasAccel = wrapper.HasAccel;

            VendorId = wrapper.VendorId;
            ProdId = wrapper.ProductId;
            DevicePath = wrapper.DevicePath;

            // Populate device objects and effects.
            DeviceObjects = wrapper.GetDeviceObjects();
            DeviceEffects = (wrapper.HasRumble || wrapper.HasHaptic)
                ? new[] { DeviceEffectItem.CreateRumbleEffect() }
                : Array.Empty<DeviceEffectItem>();

            // Compute masks.
            CustomInputState.GetAxisMask(DeviceObjects, CapAxeCount,
                out int axisMask, out int actuatorMask, out int actuatorCount);
            AxeMask = axisMask;
            ActuatorMask = actuatorMask;
            ActuatorCount = actuatorCount;
            SliderMask = CustomInputState.GetSlidersMask(DeviceObjects, CustomInputState.MaxSliders);

            // Initialize force feedback state for devices with rumble or haptic FFB.
            if (wrapper.HasRumble || wrapper.HasHaptic)
                ForceFeedbackState = new ForceFeedbackState();

            Device = wrapper;
        }

        /// <summary>
        /// Populates the device identity and capabilities from a <see cref="SdlKeyboardWrapper"/>.
        /// </summary>
        public void LoadFromKeyboardDevice(SdlKeyboardWrapper wrapper)
        {
            if (wrapper == null)
                throw new ArgumentNullException(nameof(wrapper));
            LoadFromDevice(wrapper);
        }

        /// <summary>
        /// Populates the device identity and capabilities from a <see cref="SdlMouseWrapper"/>.
        /// </summary>
        public void LoadFromMouseDevice(SdlMouseWrapper wrapper)
        {
            if (wrapper == null)
                throw new ArgumentNullException(nameof(wrapper));
            LoadFromDevice(wrapper);
        }

        /// <summary>
        /// Clears all runtime state when the device is disconnected.
        /// The serializable identity and capability properties are preserved.
        /// </summary>
        public void ClearRuntimeState()
        {
            Device = null;
            IsOnline = false;
            InputState = null;
            InputUpdates = null;
            OldInputState = null;
            OldInputUpdates = null;
            OrgInputState = null;
            DeviceObjects = null;
            DeviceEffects = null;
            ForceFeedbackState = null;

            NotifyStateChanged();
        }

        // ─────────────────────────────────────────────────────────────
        //  INotifyPropertyChanged
        // ─────────────────────────────────────────────────────────────

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Raises PropertyChanged for all runtime display properties.
        /// Call after updating IsOnline, InputState, etc.
        /// </summary>
        public void NotifyStateChanged()
        {
            OnPropertyChanged(nameof(IsOnline));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(InputState));
        }

        // ─────────────────────────────────────────────────────────────
        //  Display
        // ─────────────────────────────────────────────────────────────

        public override string ToString()
        {
            return $"{ResolvedName} [{InstanceGuid:N}]";
        }
    }
}
