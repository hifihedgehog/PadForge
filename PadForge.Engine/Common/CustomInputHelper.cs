using System;

namespace PadForge.Engine
{
    /// <summary>
    /// Helper class providing constants and utility methods for working with
    /// <see cref="CustomInputState"/> values. Replaces the former CustomDiHelper class.
    /// </summary>
    public static class CustomInputHelper
    {
        // ─────────────────────────────────────────────
        //  Array size constants (mirrored from CustomInputState for convenience)
        // ─────────────────────────────────────────────

        /// <summary>Maximum number of axes in a <see cref="CustomInputState"/>.</summary>
        public const int MaxAxis = CustomInputState.MaxAxis;

        /// <summary>Maximum number of sliders in a <see cref="CustomInputState"/>.</summary>
        public const int MaxSliders = CustomInputState.MaxSliders;

        /// <summary>Maximum number of POV hat switches in a <see cref="CustomInputState"/>.</summary>
        public const int MaxPovs = CustomInputState.MaxPovs;

        /// <summary>Maximum number of buttons in a <see cref="CustomInputState"/>.</summary>
        public const int MaxButtons = CustomInputState.MaxButtons;

        /// <summary>Unsigned axis center value (32767).</summary>
        public const int AxisCenter = 32767;

        /// <summary>Unsigned axis minimum value (0).</summary>
        public const int AxisMin = 0;

        /// <summary>Unsigned axis maximum value (65535).</summary>
        public const int AxisMax = 65535;

        /// <summary>POV centered value (-1 means no direction pressed).</summary>
        public const int PovCentered = -1;

        // ─────────────────────────────────────────────
        //  State comparison
        // ─────────────────────────────────────────────

        /// <summary>
        /// Generates a list of <see cref="CustomInputUpdate"/> items describing every
        /// difference between two input states. Used for buffered update notifications
        /// and the input recorder.
        /// </summary>
        /// <param name="oldState">Previous state snapshot (may be null, treated as all-zero).</param>
        /// <param name="newState">Current state snapshot (may be null, treated as all-zero).</param>
        /// <returns>Array of update items. Empty array if no changes.</returns>
        public static CustomInputUpdate[] GetUpdates(CustomInputState oldState, CustomInputState newState)
        {
            if (oldState == null)
                oldState = new CustomInputState();
            if (newState == null)
                newState = new CustomInputState();

            var updates = new System.Collections.Generic.List<CustomInputUpdate>();

            // Axes
            for (int i = 0; i < MaxAxis; i++)
            {
                if (oldState.Axis[i] != newState.Axis[i])
                {
                    updates.Add(new CustomInputUpdate
                    {
                        Type = MapType.Axis,
                        Index = i,
                        Value = newState.Axis[i]
                    });
                }
            }

            // Sliders
            for (int i = 0; i < MaxSliders; i++)
            {
                if (oldState.Sliders[i] != newState.Sliders[i])
                {
                    updates.Add(new CustomInputUpdate
                    {
                        Type = MapType.Slider,
                        Index = i,
                        Value = newState.Sliders[i]
                    });
                }
            }

            // POVs
            for (int i = 0; i < MaxPovs; i++)
            {
                if (oldState.Povs[i] != newState.Povs[i])
                {
                    updates.Add(new CustomInputUpdate
                    {
                        Type = MapType.POV,
                        Index = i,
                        Value = newState.Povs[i]
                    });
                }
            }

            // Buttons
            for (int i = 0; i < MaxButtons; i++)
            {
                if (oldState.Buttons[i] != newState.Buttons[i])
                {
                    updates.Add(new CustomInputUpdate
                    {
                        Type = MapType.Button,
                        Index = i,
                        Value = newState.Buttons[i] ? 1 : 0
                    });
                }
            }

            return updates.ToArray();
        }

    }
}
