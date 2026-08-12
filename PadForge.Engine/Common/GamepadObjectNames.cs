namespace PadForge.Engine
{
    /// <summary>
    /// The invariant names for the standardized SDL gamepad axis and button
    /// slots, in ONE place.
    ///
    /// These strings are not cosmetic. MappingDisplayResolver.LocalizeObjectName
    /// is a literal-string switch keyed on exactly these values, so a name that
    /// drifts by one word silently loses its translation and shows raw English
    /// in every non-English UI.
    ///
    /// They used to live only inside SdlDeviceWrapper's private helpers, and
    /// RemotePeerDevice (which must synthesize the same objects for a peer's
    /// device, since gamepad DeviceObjects are deliberately not shipped over
    /// the wire) carried its own near-miss copy: "Left Bumper" for
    /// "Left Shoulder", "Left X" for "Left Stick X", "Touchpad" for
    /// "Touchpad Click". The same physical pad therefore read differently
    /// depending on whose machine it was plugged into, and the remote half was
    /// untranslatable. Both sides now call this table, so they cannot drift
    /// apart again.
    /// </summary>
    public static class GamepadObjectNames
    {
        /// <summary>Name for a standardized gamepad axis position
        /// (LX, LY, LT, RX, RY, RT), else a flat "Axis N".</summary>
        public static string Axis(int axisIndex) => axisIndex switch
        {
            0 => "Left Stick X",
            1 => "Left Stick Y",
            2 => "Left Trigger",
            3 => "Right Stick X",
            4 => "Right Stick Y",
            5 => "Right Trigger",
            _ => $"Axis {axisIndex}"
        };

        /// <summary>Name for a standardized gamepad button position (0-10
        /// standard, 11-21 extended), else a flat "Button N".</summary>
        public static string Button(int buttonIndex) => buttonIndex switch
        {
            0 => "A",
            1 => "B",
            2 => "X",
            3 => "Y",
            4 => "Left Shoulder",
            5 => "Right Shoulder",
            6 => "Back",
            7 => "Start",
            8 => "Left Stick Button",
            9 => "Right Stick Button",
            10 => "Guide",
            11 => "Misc 1",
            12 => "Right Paddle 1",
            13 => "Left Paddle 1",
            14 => "Right Paddle 2",
            15 => "Left Paddle 2",
            16 => "Touchpad Click",
            17 => "Misc 2",
            18 => "Misc 3",
            19 => "Misc 4",
            20 => "Misc 5",
            21 => "Misc 6",
            _ => $"Button {buttonIndex}"
        };
    }
}
