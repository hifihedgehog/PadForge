using System;

namespace PadForge.Engine
{
    /// <summary>
    /// API-agnostic snapshot of a device's complete input state at a single point in time.
    /// All values use unsigned conventions:
    ///   Stick axes:   0–65535  (center = 32768; the producer emits (ushort)(raw - short.MinValue),
    ///                 so a centered raw 0 yields 32768 — see SdlDeviceWrapper. Any receiver doing
    ///                 neutral/deadzone math must use 32768, not the 32767 arithmetic midpoint.)
    ///   Trigger axes: 0–65535  (0-based: 0 = released, 65535 = full; not centered)
    ///   Sliders:      0–65535
    ///   POVs:         centidegrees 0–35900, or -1 for centered
    ///   Buttons:      true = pressed, false = released
    ///
    /// Button index reference (PadForge-internal, matches SdlDeviceWrapper's population).
    /// Indices 11-21 follow SDL3's SDL_GamepadButton enum order, skipping the four
    /// DPad slots that PadForge synthesizes into POV[0]:
    ///   0-10  Standard XInput-shape (A/B/X/Y/LB/RB/Back/Start/LS/RS/Guide)
    ///   11    Misc1 (Capture / Share / Mute)
    ///   12    Right Paddle 1
    ///   13    Left Paddle 1
    ///   14    Right Paddle 2
    ///   15    Left Paddle 2
    ///   16    TouchpadClick — maps to SDL_GAMEPAD_BUTTON_TOUCHPAD
    ///   17-21 Misc2-Misc6 (driver-specific extras)
    ///   22+   Raw joystick buttons beyond the standardized gamepad range
    ///
    /// This replaces the former CustomDiState class. The field layout is intentionally
    /// compatible with the mapping pipeline (Steps 3–5) which indexes into these arrays
    /// by ordinal position.
    /// </summary>
    public class CustomInputState
    {
        /// <summary>Maximum number of axes stored in the <see cref="Axis"/> array.</summary>
        public const int MaxAxis = 24;

        /// <summary>Maximum number of sliders stored in the <see cref="Sliders"/> array.</summary>
        public const int MaxSliders = 8;

        /// <summary>Maximum number of POV hat switches.</summary>
        public const int MaxPovs = 4;

        /// <summary>Maximum number of buttons (256 covers full Windows VK code range).</summary>
        public const int MaxButtons = 256;

        /// <summary>
        /// Axis values (unsigned, 0–65535). Indices 0–5 correspond to standard axes
        /// (X, Y, Z, Rx, Ry, Rz). Indices 6–23 are additional axes.
        /// </summary>
        public int[] Axis;

        /// <summary>
        /// Slider values (unsigned, 0–65535). Used for overflow axes beyond <see cref="MaxAxis"/>
        /// or for devices that report dedicated slider controls.
        /// </summary>
        public int[] Sliders;

        /// <summary>
        /// POV hat switch values in centidegrees (0 = North, 9000 = East, etc.).
        /// A value of -1 indicates the hat is centered (no direction pressed).
        /// </summary>
        public int[] Povs;

        /// <summary>
        /// Button pressed states. true = currently pressed, false = released.
        /// </summary>
        public bool[] Buttons;

        /// <summary>
        /// Gyroscope data: [X, Y, Z] in radians per second.
        /// Only populated for devices with a gyro sensor.
        /// </summary>
        public float[] Gyro;

        /// <summary>
        /// Accelerometer data: [X, Y, Z] in meters per second squared.
        /// Only populated for devices with an accelerometer sensor.
        /// </summary>
        public float[] Accel;

        /// <summary>
        /// Auxiliary (left-side) accelerometer data: [X, Y, Z] in meters per
        /// second squared, SDL native frame (issue #199). SDL delivers this as
        /// SDL_SENSOR_ACCEL_L: the Wii Nunchuk's own accelerometer on a
        /// Nunchuk-attached remote, or the LEFT Joy-Con's accelerometer on a
        /// combined Joy-Con pair (whose primary Accel is the right half).
        /// Zeroed for devices without the sensor.
        /// </summary>
        public float[] AccelAux;

        /// <summary>
        /// Per-touchpad finger state. One <see cref="TouchpadInputState"/>
        /// per physical touchpad surface the device exposes (1 for DS4 /
        /// DualSense / Shield / PTP, 2 for Steam Controller 2026 / Steam
        /// Deck, 3 for the original Steam Controller). Supports up to N
        /// fingers per pad (PTP max 5, SDL gamepads typically 1-2) and
        /// tracks contact identity across slot up/down transitions so
        /// the gesture engine can distinguish "same finger continuing"
        /// from "new finger landed in the same slot." Null when the
        /// device has no touchpad. Replaced the legacy
        /// <c>TouchpadFingers[6]</c> / <c>TouchpadDown[2]</c> single-pad
        /// shape in v3.3 to support multi-touchpad devices natively.
        /// </summary>
        public TouchpadInputState[] Touchpads;

        /// <summary>
        /// Full MIDI input namespace (all 128 notes + 128 CCs + pitch bend),
        /// channel-merged. Null when the device is not a MIDI input. Same
        /// lazily-allocated, nullable cost model as <see cref="Touchpads"/>.
        /// </summary>
        public MidiInputState Midi;

        /// <summary>Wii Remote IR-camera pointer (issue #146), normalized to the
        /// [-1..+1] stick range per screen axis from the two sensor-bar dots, plus
        /// a <see cref="WiiIrState.Detected"/> flag. A value type, so it is always
        /// present and costs no allocation; <c>Detected == false</c> means "no IR
        /// or no dots this frame". Populated by SdlDeviceWrapper from the raw
        /// joystick axes 6-9, where the SDL hidapi_wii driver posts dot0/dot1 X/Y
        /// for a bare Wii Remote with the camera powered.</summary>
        public WiiIrState Ir;

        /// <summary>Right Joy-Con NIR camera average intensity, 0..1 (issue #151).
        /// The SDL fork's hidapi_switch posts the MCU's per-frame average-intensity
        /// byte on dedicated joystick axis 6 for a standalone right Joy-Con with the
        /// camera powered (SDL#7). A covered sensor reads bright (high), an
        /// uncovered one dark (low), so this is a cover/proximity scalar. 0 while
        /// the camera is off. Populated by SdlDeviceWrapper.</summary>
        public float JoyConIrIntensity;

        /// <summary>Joy-Con 2 optical mouse sensor motion, in raw sensor counts
        /// accumulated since the previous poll (issue #154). The SDL fork's BLE
        /// Switch 2 driver posts the sensor's absolute 16-bit X/Y counters on
        /// dedicated joystick axes 6/7 (SDL#8); SdlDeviceWrapper turns them into
        /// per-poll deltas with 16-bit wraparound (the jc2mouse delta_u16 idiom).
        /// Screen convention: +X = right, +Y = toward the user (down), matching
        /// how joycon2mouse/jc2mouse feed the OS cursor. 0 when idle or absent.</summary>
        public float JoyCon2MouseDX;
        public float JoyCon2MouseDY;

        /// <summary>Battery percentage from SDL3 (0..100, or -1 if unknown).
        /// Refreshed periodically by SdlDeviceWrapper, not every frame.</summary>
        public int BatteryPercent;

        /// <summary>True if the device reports as charging or fully charged.</summary>
        public bool BatteryCharging;

        /// <summary>
        /// Creates a new zeroed input state with default array sizes.
        /// All axes and sliders default to 0, all POVs default to -1 (centered),
        /// all buttons default to false (released).
        /// </summary>
        public CustomInputState()
        {
            Axis = new int[MaxAxis];
            Sliders = new int[MaxSliders];
            Povs = new int[MaxPovs];
            Buttons = new bool[MaxButtons];
            Gyro = new float[3];
            Accel = new float[3];
            AccelAux = new float[3];
            // Touchpads starts null. Device wrappers allocate the per-pad
            // TouchpadState[] at device-open time with the right pad count
            // and per-pad finger slot count for the actual hardware. Null
            // here means "no touchpad surface on this device."
            Touchpads = null;
            BatteryPercent = -1;

            // Initialize POVs to centered.
            for (int i = 0; i < Povs.Length; i++)
                Povs[i] = -1;
        }

        /// <summary>
        /// Creates a deep copy of this input state.
        /// </summary>
        public CustomInputState Clone()
        {
            var clone = new CustomInputState();
            Array.Copy(Axis, clone.Axis, MaxAxis);
            Array.Copy(Sliders, clone.Sliders, MaxSliders);
            Array.Copy(Povs, clone.Povs, MaxPovs);
            Array.Copy(Buttons, clone.Buttons, MaxButtons);
            Array.Copy(Gyro, clone.Gyro, 3);
            Array.Copy(Accel, clone.Accel, 3);
            Array.Copy(AccelAux, clone.AccelAux, 3);
            if (Touchpads != null)
            {
                clone.Touchpads = new TouchpadInputState[Touchpads.Length];
                for (int i = 0; i < Touchpads.Length; i++)
                    clone.Touchpads[i] = Touchpads[i]?.Clone();
            }
            clone.Midi = Midi?.Clone();
            clone.Ir = Ir; // value type copy (X/Y/Detected)
            clone.JoyConIrIntensity = JoyConIrIntensity;
            clone.JoyCon2MouseDX = JoyCon2MouseDX;
            clone.JoyCon2MouseDY = JoyCon2MouseDY;
            clone.BatteryPercent = BatteryPercent;
            clone.BatteryCharging = BatteryCharging;
            return clone;
        }

        // ─────────────────────────────────────────────
        //  Mask helpers — used by the mapping pipeline
        //  to know which axes/sliders are present
        // ─────────────────────────────────────────────

        /// <summary>
        /// Scans device object items to build axis and actuator bitmasks.
        /// An axis bit is set if a <see cref="DeviceObjectItem"/> with
        /// <see cref="DeviceObjectTypeFlags.AbsoluteAxis"/> or
        /// <see cref="DeviceObjectTypeFlags.RelativeAxis"/> exists at that index.
        /// An actuator bit is set if the object also has
        /// <see cref="DeviceObjectTypeFlags.ForceFeedbackActuator"/>.
        /// </summary>
        /// <param name="items">Device object metadata array.</param>
        /// <param name="numAxes">Number of axes on the device.</param>
        /// <param name="axisMask">Output: bitmask of present axes (bit N = axis N exists).</param>
        /// <param name="actuatorMask">Output: bitmask of force-feedback actuator axes.</param>
        /// <param name="actuatorCount">Output: total number of actuator axes.</param>
        public static void GetAxisMask(DeviceObjectItem[] items, int numAxes,
            out int axisMask, out int actuatorMask, out int actuatorCount)
        {
            axisMask = 0;
            actuatorMask = 0;
            actuatorCount = 0;

            if (items == null)
                return;

            foreach (var item in items)
            {
                bool isAxis = (item.ObjectType & DeviceObjectTypeFlags.Axis) != 0;
                if (!isAxis)
                    continue;

                int idx = item.InputIndex;
                if (idx < 0 || idx >= 32)
                    continue;

                axisMask |= (1 << idx);

                if ((item.ObjectType & DeviceObjectTypeFlags.ForceFeedbackActuator) != 0)
                {
                    actuatorMask |= (1 << idx);
                    actuatorCount++;
                }
            }
        }

    }

    /// <summary>Wii Remote IR-camera pointer for one frame (issue #146). A value
    /// type so <see cref="CustomInputState.Ir"/> needs no per-frame allocation.
    /// <see cref="X"/> / <see cref="Y"/> are the normalized screen position in the
    /// [-1..+1] stick range (the two-dot midpoint), valid only when
    /// <see cref="Detected"/> is true. When no dot is seen this frame the producer
    /// clears <see cref="Detected"/> and every consumer reads the source as centered
    /// for that frame (X/Y are not carried over; the state is rebuilt each tick).</summary>
    public struct WiiIrState
    {
        public float X;
        public float Y;
        public bool Detected;
    }
}
