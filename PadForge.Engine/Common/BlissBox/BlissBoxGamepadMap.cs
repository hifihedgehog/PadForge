using System.Collections.Generic;

namespace PadForge.Engine.Common.BlissBox
{
    /// <summary>
    /// Where the controller in a Bliss-Box port puts its inputs in SDL's
    /// gamepad layout (<see cref="BlissBoxControllers.GamepadMap"/>): the
    /// port's descriptor for each standardized gamepad position the
    /// controller fills, so the default mapping binds the port as it binds an
    /// SDL gamepad.
    /// </summary>
    public sealed class BlissBoxGamepadMap
    {
        /// <summary>The standardized button positions, 0 to 21
        /// (<see cref="GamepadObjectNames.Button"/>).</summary>
        public const int ButtonPositions = 22;

        /// <summary>The standardized axis positions, 0 to 5
        /// (<see cref="GamepadObjectNames.Axis"/>).</summary>
        public const int AxisPositions = 6;

        private readonly string[] _buttons;
        private readonly string[] _axes;

        internal BlissBoxGamepadMap(string[] buttons, string[] axes, bool dpad, int[] pressureAxes)
        {
            _buttons = buttons;
            _axes = axes;
            DPad = dpad;
            PressureAxes = pressureAxes;
        }

        /// <summary>The port's descriptor ("Button 5") for gamepad button
        /// position <paramref name="position"/>, or null when the controller
        /// has nothing there.</summary>
        public string Button(int position)
            => (uint)position < (uint)_buttons.Length ? _buttons[position] : null;

        /// <summary>The port's descriptor for gamepad axis position
        /// <paramref name="position"/>: an axis ("Axis 2"), or a button on a
        /// trigger the controller presses digitally ("Button 8"), or
        /// null.</summary>
        public string Axis(int position)
            => (uint)position < (uint)_axes.Length ? _axes[position] : null;

        /// <summary>True when the port's hat is the controller's
        /// D-pad.</summary>
        public bool DPad { get; }

        /// <summary>The axes carrying a DualShock 2's pressures, in the order
        /// of the button pressure targets (cross, circle, square, triangle,
        /// L1, R1, then the D-pad's up, down, left and right), or null for
        /// any other controller.</summary>
        public IReadOnlyList<int> PressureAxes { get; }
    }
}
