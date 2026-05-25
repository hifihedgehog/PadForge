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

        /// <summary>Maximum simultaneous fingers the overlay surface
        /// exposes. Matches the Windows Precision Touchpad spec ceiling
        /// so a touch-capable display drawing through the overlay can
        /// drive multi-finger gestures (pinch, rotate, 3/4/5-finger
        /// swipes / taps) end-to-end. The mouse-drag fallback uses
        /// slot 0 only.</summary>
        public const int OverlayMaxFingers = 5;

        /// <summary>
        /// Mouse-drag fallback path. Two-finger snapshot from the legacy
        /// <see cref="TouchpadState"/> struct (DS4-shape). Used by the
        /// overlay window when a touch device isn't available; mouse-
        /// drag drives slot 0 only and slot 1's fields here are vestigial
        /// since a mouse cursor has no second contact. Higher slots
        /// (2..4) stay inert. For real multi-touch from a touch-capable
        /// display, use the <see cref="UpdateStateMulti"/> overload.
        /// </summary>
        public void UpdateState(TouchpadState tp)
        {
            lock (_stateLock)
            {
                var s = new CustomInputState();
                s.Touchpads = new[] { new TouchpadInputState(OverlayMaxFingers) };
                var pad = s.Touchpads[0];
                pad.FingerX[0] = tp.X0;
                pad.FingerY[0] = tp.Y0;
                pad.FingerPressure[0] = tp.Down0 ? 1f : 0f;
                pad.FingerDown[0] = tp.Down0;
                pad.FingerX[1] = tp.X1;
                pad.FingerY[1] = tp.Y1;
                pad.FingerPressure[1] = tp.Down1 ? 1f : 0f;
                pad.FingerDown[1] = tp.Down1;
                // Slots 2..4 default-init to 0 / false / -1.

                // Contact-ID synthesis: walk both slots, allocate on
                // rising edges, clear on falling, carry the prior ID
                // when steady. Generalized form so adding higher slots
                // doesn't repeat the pattern.
                CarryContactIds(pad, prevPad: _currentState?.Touchpads?.Length > 0 ? _currentState.Touchpads[0] : null);

                pad.Clicked = tp.Click;
                // Touchpad click rides Buttons[16] (SDL_GAMEPAD_BUTTON_TOUCHPAD's
                // canonical PadForge slot). The "Touchpad 0 Click" descriptor
                // reads from this index — same path as a physical DS4/DualSense.
                s.Buttons[16] = tp.Click;
                _currentState = s;
            }
        }

        /// <summary>
        /// Multi-finger touch path. Caller (overlay window's WPF
        /// TouchDown / TouchMove / TouchUp handlers) populates a
        /// <see cref="TouchpadInputState"/> with up to
        /// <see cref="OverlayMaxFingers"/> active contacts and passes it
        /// here. Contact IDs are caller-managed (typically a stable map
        /// from WPF <c>TouchDevice.Id</c> → slot) so the gesture engine
        /// can distinguish same-finger continuation from re-touches in
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
                // Caller-managed contact IDs are authoritative; do not
                // re-synthesize. If the caller left them at -1 while
                // still reporting FingerDown=true, fall back to the
                // monotonic synth so the gesture engine sees a stable
                // identifier.
                for (int i = 0; i < slots; i++)
                {
                    if (pad.FingerDown[i] && pad.FingerContactId[i] < 0)
                        pad.FingerContactId[i] = _overlayContactIdNext++;
                }
                pad.Clicked = click;
                s.Touchpads = new[] { pad };
                if (s.Buttons.Length > 16) s.Buttons[16] = click;
                _currentState = s;
            }
        }

        /// <summary>Walks every slot on the newly-built pad, carries
        /// the prior frame's contact ID forward on a steady down,
        /// allocates a fresh ID from <see cref="_overlayContactIdNext"/>
        /// on a rising edge, and clears to -1 on a falling edge.
        /// Shared by both UpdateState overloads.</summary>
        private void CarryContactIds(TouchpadInputState pad, TouchpadInputState prevPad)
        {
            int n = pad.MaxFingers;
            for (int i = 0; i < n; i++)
            {
                bool prevDown = prevPad != null
                    && i < prevPad.MaxFingers
                    && prevPad.FingerDown[i];
                int prevId = prevPad != null && i < prevPad.MaxFingers
                    ? prevPad.FingerContactId[i] : -1;
                if (pad.FingerDown[i])
                    pad.FingerContactId[i] = prevDown ? prevId : _overlayContactIdNext++;
                else
                    pad.FingerContactId[i] = -1;
            }
        }

        // Monotonic contact-ID source for the overlay's finger slots.
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
        public void Dispose() => GC.SuppressFinalize(this);
    }
}
