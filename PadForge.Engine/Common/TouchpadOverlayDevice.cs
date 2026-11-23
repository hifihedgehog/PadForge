using System;

namespace PadForge.Engine
{
    /// <summary>
    /// Virtual input device representing the touchpad overlay window.
    /// State is pushed in through <see cref="UpdateStateMulti"/> and read
    /// out through the standard <see cref="ISdlInputDevice"/> pipeline, so
    /// the device appears on the Devices page and can be assigned to a
    /// controller.
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

        /// <summary>Maximum simultaneous fingers the overlay surface
        /// exposes. Matches the Windows Precision Touchpad spec ceiling
        /// so a touch-capable display drawing through the overlay can
        /// drive multi-finger gestures (pinch, rotate, 3/4/5-finger
        /// swipes / taps) end-to-end.</summary>
        public const int OverlayMaxFingers = 5;

        /// <summary>
        /// Multi-finger touch path. InputService's UI timer passes the
        /// overlay window's snapshot (TouchpadOverlay.GetMultiFingerState),
        /// a <see cref="TouchpadInputState"/> with up to
        /// <see cref="OverlayMaxFingers"/> contacts. The window gives every
        /// finger that is down a new contact id when it lands, so the
        /// gesture engine can tell a finger that stays from a new touch in
        /// the same slot. The <paramref name="click"/> bit feeds
        /// <c>Buttons[16]</c> for parity with physical-pad behavior.
        /// </summary>
        public void UpdateStateMulti(TouchpadInputState snapshot, bool click)
        {
            if (snapshot == null) return;
            lock (_stateLock)
            {
                var s = new CustomInputState();
                int slots = System.Math.Min(snapshot.MaxFingers, OverlayMaxFingers);
                var pad = new TouchpadInputState(OverlayMaxFingers);
                for (int i = 0; i < slots; i++)
                {
                    pad.FingerX[i] = snapshot.FingerX[i];
                    pad.FingerY[i] = snapshot.FingerY[i];
                    pad.FingerPressure[i] = snapshot.FingerPressure[i];
                    pad.FingerDown[i] = snapshot.FingerDown[i];
                    pad.FingerContactId[i] = snapshot.FingerContactId[i];
                }
                pad.Clicked = click;
                s.Touchpads = new[] { pad };
                if (s.Buttons.Length > 16) s.Buttons[16] = click;
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
        public void Dispose() => GC.SuppressFinalize(this);
    }
}
