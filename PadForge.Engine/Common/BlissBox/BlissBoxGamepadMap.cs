namespace PadForge.Engine.Common.BlissBox
{
    /// <summary>
    /// Where the controller in a Bliss-Box port puts its inputs in SDL's
    /// gamepad layout (<see cref="BlissBoxControllers.GamepadMap"/>): the
    /// port's descriptor for each standardized gamepad position the
    /// controller fills, so the default mapping binds the port as it binds an
    /// SDL gamepad. The port's D-pad is its hat or nothing.
    /// </summary>
    public sealed class BlissBoxGamepadMap : GamepadPlacement
    {
        internal BlissBoxGamepadMap(string[] buttons, string[] axes, bool dpad, int[] pressureAxes)
            : base(buttons, axes, dpad, pressureAxes)
        {
        }
    }
}
