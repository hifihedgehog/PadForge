using System;

namespace PadForge.Engine
{
    /// <summary>
    /// API-agnostic snapshot of a device's complete input state at a single point in time.
    /// All values use unsigned conventions:
    ///   Axes/Sliders: 0–65535  (center = 32767)
    ///   POVs:         centidegrees 0–35900, or -1 for centered
    ///   Buttons:      true = pressed, false = released
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
}
