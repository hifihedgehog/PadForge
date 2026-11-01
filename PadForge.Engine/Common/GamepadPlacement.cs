using System.Collections.Generic;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// Where a device read outside SDL's gamepad layout puts its inputs in
    /// that layout: the device's descriptor for each standardized gamepad
    /// position it fills, so the default mapping binds it as it binds an SDL
    /// gamepad. The controller in a Bliss-Box port has one
    /// (<see cref="BlissBox.BlissBoxGamepadMap"/>), and so do the Twitch Chat
    /// row's usual words (issue #555).
    /// </summary>
    public class GamepadPlacement
    {
        /// <summary>The standardized button positions, 0 to 21
        /// (<see cref="GamepadObjectNames.Button"/>).</summary>
        public const int ButtonPositions = 22;

        /// <summary>The standardized axis positions, 0 to 5
        /// (<see cref="GamepadObjectNames.Axis"/>).</summary>
        public const int AxisPositions = 6;

        private readonly string[] _buttons;
        private readonly string[] _axes;
        private readonly string[] _dpadButtons;

        /// <param name="dpadButtons">The device's buttons for D-pad up, down,
        /// left and right, in that order, when its D-pad is four buttons
        /// rather than a hat. Null otherwise.</param>
        public GamepadPlacement(string[] buttons, string[] axes, bool dpad, IReadOnlyList<int> pressureAxes,
            string[] dpadButtons = null)
        {
            _buttons = buttons;
            _axes = axes;
            _dpadButtons = dpadButtons;
            DPad = dpad;
            PressureAxes = pressureAxes;
        }

        /// <summary>The device's descriptor ("Button 5") for gamepad button
        /// position <paramref name="position"/>, or null when the device has
        /// nothing there.</summary>
        public string Button(int position)
            => (uint)position < (uint)_buttons.Length ? _buttons[position] : null;

        /// <summary>The device's descriptor for gamepad axis position
        /// <paramref name="position"/>: an axis ("Axis 2"), or a button on a
        /// trigger the device presses digitally ("Button 8"), or
        /// null.</summary>
        public string Axis(int position)
            => (uint)position < (uint)_axes.Length ? _axes[position] : null;

        /// <summary>True when the device's hat is its D-pad.</summary>
        public bool DPad { get; }

        /// <summary>The device's button for D-pad up, down, left or right (0
        /// to 3) when its D-pad is four buttons, or null.</summary>
        public string DPadButton(int direction)
            => _dpadButtons != null && (uint)direction < (uint)_dpadButtons.Length ? _dpadButtons[direction] : null;

        /// <summary>The axes carrying a DualShock 2's pressures, in the order
        /// of the button pressure targets (cross, circle, square, triangle,
        /// L1, R1, then the D-pad's up, down, left and right), or null for
        /// any other controller.</summary>
        public IReadOnlyList<int> PressureAxes { get; }
    }
}
