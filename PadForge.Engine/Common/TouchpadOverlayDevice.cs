using System;

namespace PadForge.Engine
{
    /// <summary>
    /// Virtual input device representing the touchpad overlay window.
    /// Reads touchpad state from a callback and exposes it through the
    /// standard <see cref="ISdlInputDevice"/> pipeline so it appears
    /// in the Devices page and can be assigned to DS4 slots.
    /// </summary>
    public class TouchpadOverlayDevice : ISdlInputDevice
    {
        private const ushort OverlayVendorId = 0xBEEF;
        private const ushort OverlayProductId = 0xCA7F;

        public static readonly Guid OverlayProductGuid =
            new Guid("BEBC0000-0000-0000-0000-CAFEFACE0002");

        public static readonly Guid OverlayInstanceGuid =
            new Guid("BEBC0001-0000-0000-0000-CAFEFACE0002");

        private volatile CustomInputState _currentState = new CustomInputState();
        private readonly object _stateLock = new object();

        public uint SdlInstanceId => 0xFFFFFFFE;
        public string Name => "Touchpad Overlay";
        public int NumAxes => 0;
        public int NumButtons => 0;
        public int RawButtonCount => 0;
        public int NumHats => 0;
        public bool HasRumble => false;
        public bool HasHaptic => false;
        public bool HasGyro => false;
        public bool HasAccel => false;
        public bool HasTouchpad => true;
        public HapticEffectStrategy HapticStrategy => HapticEffectStrategy.None;
        public IntPtr HapticHandle => IntPtr.Zero;
        public uint HapticFeatures => 0;
        public int NumHapticAxes => 0;
        public ushort VendorId => OverlayVendorId;
        public ushort ProductId => OverlayProductId;
        public string DevicePath => "overlay://touchpad";
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;
        public Guid InstanceGuid => OverlayInstanceGuid;
        public Guid ProductGuid => OverlayProductGuid;
        public bool IsAttached => true;

        /// <summary>
        /// Updates the touchpad state. Called from UI thread.
        /// </summary>
        public void UpdateState(TouchpadState tp)
        {
            lock (_stateLock)
            {
                var s = new CustomInputState();
                s.TouchpadFingers[0] = tp.X0;
                s.TouchpadFingers[1] = tp.Y0;
                s.TouchpadFingers[2] = tp.Down0 ? 1f : 0f;
                s.TouchpadFingers[3] = tp.X1;
                s.TouchpadFingers[4] = tp.Y1;
                s.TouchpadFingers[5] = tp.Down1 ? 1f : 0f;
                s.TouchpadDown[0] = tp.Down0;
                s.TouchpadDown[1] = tp.Down1;
                // Map click to button 20 (touchpad click, same as web controller)
                if (tp.Click) s.Buttons[20] = true;
                _currentState = s;
            }
        }

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            return Volatile.Read(ref _currentState);
        }

        public DeviceObjectItem[] GetDeviceObjects()
        {
            return Array.Empty<DeviceObjectItem>();
        }

        public int GetInputDeviceType() => InputDeviceType.Touchpad;

        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;
        public void Dispose() { }
    }
}
