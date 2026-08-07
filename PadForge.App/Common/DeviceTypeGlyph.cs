using PadForge.Engine;

namespace PadForge.Common
{
    /// <summary>
    /// Maps a device's CapType (<see cref="InputDeviceType"/>) to the Segoe
    /// MDL2 Assets glyph for that device class, so roster surfaces draw the
    /// icon that matches the device instead of a blanket gamepad (#175).
    /// Reuses the characters the app already established elsewhere: E962
    /// (Mouse Output card), EFA5 (touchpad tab and capability chips), E8D6
    /// (MIDI badges), E7FC (controller).
    /// </summary>
    internal static class DeviceTypeGlyph
    {
        /// <summary>
        /// Glyph for a CapType. All game-controller classes (gamepad,
        /// joystick, wheel, flight stick, first person, supplemental) share
        /// E7FC, the app's one controller character. Unknown types fall back
        /// to it as well.
        /// </summary>
        internal static string For(int capType) => capType switch
        {
            InputDeviceType.Keyboard => "\uE765",        // KeyboardClassic
            InputDeviceType.Mouse => "\uE962",           // Mouse
            InputDeviceType.Touchpad => "\uEFA5",        // Touchpad
            InputDeviceType.Midi => "\uE8D6",            // MIDI badge glyph
            InputDeviceType.Nfc => "\uE9A1",             // TapAndSend
            InputDeviceType.ConsumerControl => "\uEA69", // Media (media-key strips)
            InputDeviceType.HeadsetMotion => "\uE7F6",   // Headphone (verified in live segmdl2.ttf)
            _ => "\uE7FC"                                // Game
        };
    }
}
