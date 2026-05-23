using System;

namespace PadForge.Engine
{
    /// <summary>
    /// Virtual input device representing the touchpad overlay window.
    /// Reads touchpad state from a callback and exposes it through the
    /// standard <see cref="ISdlInputDevice"/> pipeline so it appears
    /// in the Devices page and can be assigned to PlayStation slots.
    /// </summary>
    public class TouchpadOverlayDevice : ISdlInputDevice
    {
        private const ushort OverlayVendorId = 0xBEEF;
        private const ushort OverlayProductId = 0xCA7F;

        public static readonly Guid OverlayProductGuid =
            new Guid("BEBC0000-0000-0000-0000-CAFEFACE0002");

        public static readonly Guid OverlayInstanceGuid =
            new Guid("BEBC0001-0000-0000-0000-CAFEFACE0002");

        // The lock-protected write + Volatile.Read on the get path
        // already provide full memory ordering; the `volatile` keyword
        // here was redundant and triggered CS0420 because Volatile.Read
        // takes the field by `ref`, which strips the volatile contract
        // at the call site anyway.
        private CustomInputState _currentState = new CustomInputState();
        private readonly object _stateLock = new object();

        public uint SdlInstanceId => 0xFFFFFFFE;
        public string Name => "Touchpad Overlay";
        public int NumAxes => 0;
        // The touchpad click rides Buttons[16] (SDL_GAMEPAD_BUTTON_TOUCHPAD's
        // canonical PadForge slot). NumButtons describes the slot range —
        // dense-iter consumers (macro-trigger recorder, mapping picker's
        // raw-button list) walk 0..NumButtons-1, so 17 is the right value
        // here even though only one slot is populated. The sparse
        // SupportedButtonIndices below tells the Devices preview which
        // slots are real, so the grid still shows exactly 1 circle.
        public int NumButtons => 17;
        public int RawButtonCount => 17;
        public int NumHats => 0;
        public int[] SupportedButtonIndices => _supportedButtons;
        private static readonly int[] _supportedButtons = { 16 };
        public IntPtr GamepadHandle => IntPtr.Zero;
        public bool HasRumble => false;
        public bool HasRumbleTriggers => false;
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
        /// Updates the touchpad state. Called from UI thread. The overlay
        /// surface is a single virtual pad with up to 2 fingers (matching
        /// the DS4 touchpad reference). The new <see cref="TouchpadInputState"/>
        /// per-pad model carries the same data with explicit per-slot
        /// arrays and contact-ID synthesis on rising/falling edges so the
        /// gesture recognizer sees a stable input shape regardless of
        /// touchpad source (overlay vs SDL vs PTP).
        /// </summary>
        public void UpdateState(TouchpadState tp)
        {
            lock (_stateLock)
            {
                var s = new CustomInputState();
                s.Touchpads = new[] { new TouchpadInputState(2) };
                var pad = s.Touchpads[0];
                pad.FingerX[0] = tp.X0;
                pad.FingerY[0] = tp.Y0;
                pad.FingerPressure[0] = tp.Down0 ? 1f : 0f;
                pad.FingerDown[0] = tp.Down0;
                pad.FingerX[1] = tp.X1;
                pad.FingerY[1] = tp.Y1;
                pad.FingerPressure[1] = tp.Down1 ? 1f : 0f;
                pad.FingerDown[1] = tp.Down1;
                // Contact-ID synthesis for the overlay: each fresh
                // UpdateState replaces the snapshot wholesale, so we
                // synthesize IDs against the prior snapshot held on this
                // device wrapper. On rising edge allocate from the
                // monotonic counter; on falling edge clear to -1; else
                // carry the prior ID.
                var prev = _currentState?.Touchpads?[0];
                bool prev0 = prev?.FingerDown != null && prev.FingerDown.Length > 0 && prev.FingerDown[0];
                bool prev1 = prev?.FingerDown != null && prev.FingerDown.Length > 1 && prev.FingerDown[1];
                int prevId0 = prev?.FingerContactId != null && prev.FingerContactId.Length > 0 ? prev.FingerContactId[0] : -1;
                int prevId1 = prev?.FingerContactId != null && prev.FingerContactId.Length > 1 ? prev.FingerContactId[1] : -1;
                pad.FingerContactId[0] = tp.Down0
                    ? (prev0 ? prevId0 : _overlayContactIdNext++)
                    : -1;
                pad.FingerContactId[1] = tp.Down1
                    ? (prev1 ? prevId1 : _overlayContactIdNext++)
                    : -1;
                pad.Clicked = tp.Click;
                // Touchpad click rides Buttons[16] (SDL_GAMEPAD_BUTTON_TOUCHPAD's
                // canonical PadForge slot). The "Touchpad 0 Click" descriptor
                // reads from this index — same path as a physical DS4/DualSense.
                s.Buttons[16] = tp.Click;
                _currentState = s;
            }
        }

        // Monotonic contact-ID source for the overlay's two finger slots.
        private int _overlayContactIdNext = 1;

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
