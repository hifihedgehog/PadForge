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
            InputDeviceType.Tablet => "\uEFA5",
            InputDeviceType.Midi => "\uE8D6",            // MIDI badge glyph
            InputDeviceType.Nfc => "\uE9A1",             // TapAndSend
            InputDeviceType.ConsumerControl => "\uEA69", // Media (media-key strips)
            InputDeviceType.HeadsetMotion => "\uE7F6",   // Headphone (verified in live segmdl2.ttf)
            InputDeviceType.Microphone => "\uE720",     // Microphone
            InputDeviceType.HandheldButtons => "\uE7F8", // DeviceLaptopNoPic (the machine itself, #343)
            InputDeviceType.SystemMotion => "\uE7F8",    // same machine, its sensor row
            InputDeviceType.HeadTracker => "\uE77B",     // Contact, a head (#355; verified in live segmdl2.ttf)
            InputDeviceType.VrController => "\uF119",    // VR controller, the icon the slot art already uses (#403)
            InputDeviceType.LogitechGKeys => "\uE765",   // KeyboardClassic: G-keys are keyboard keys (#454)
            InputDeviceType.AnalogKeyboard => "\uE765",  // KeyboardClassic: it is a keyboard (#468)
            InputDeviceType.WebMenus => "\uF0E2",        // GridView, a deck of tiles (#471)
            InputDeviceType.PeripheralLighting => "\uE781", // the Lighting tab's glyph (#494)
            InputDeviceType.PeripheralHaptics => "\uE877",  // the Force Feedback tab's glyph (#494)
            InputDeviceType.KeyboardScreenKeys => "\uE765", // KeyboardClassic: the keys sit on a keyboard (#519)
            InputDeviceType.NovintFalcon => "\uE7FC",       // Game: the Falcon is a game controller (#521)
            InputDeviceType.GameTelemetry => "\uEC4A",      // SpeedHigh, a speedometer (#531; verified in live segmdl2.ttf)
            InputDeviceType.TwitchChat => "\uE8F2",         // ChatBubbles (#555; verified in live segmdl2.ttf and SegoeIcons.ttf)
            _ => "\uE7FC"                                // Game
        };

        /// <summary>The key of a CapType's kind of device, which the kind's
        /// name is looked up by (<see cref="NameFor"/>). "Device" for a
        /// type with no kind of its own.</summary>
        internal static string KeyFor(int capType) => capType switch
        {
            InputDeviceType.Gamepad => "Gamepad",
            InputDeviceType.Joystick => "Joystick",
            InputDeviceType.Driving => "Wheel",
            InputDeviceType.Flight => "FlightStick",
            InputDeviceType.FirstPerson => "FirstPerson",
            InputDeviceType.Supplemental => "Supplemental",
            InputDeviceType.Mouse => "Mouse",
            InputDeviceType.Keyboard => "Keyboard",
            InputDeviceType.Touchpad => "Touchpad",
            InputDeviceType.Tablet => "Tablet",
            InputDeviceType.Midi => "Midi",
            InputDeviceType.Nfc => "Nfc",
            InputDeviceType.Microphone => "Microphone",
            InputDeviceType.ConsumerControl => "ConsumerControl",
            InputDeviceType.HeadsetMotion => "HeadsetMotion",
            InputDeviceType.HandheldButtons => "HandheldButtons",
            InputDeviceType.SystemMotion => "SystemMotion",
            InputDeviceType.HeadTracker => "HeadTracker",
            InputDeviceType.VrController => "VrController",
            InputDeviceType.LogitechGKeys => "LogitechGKeys",
            InputDeviceType.AnalogKeyboard => "AnalogKeyboard",
            InputDeviceType.WebMenus => "WebMenus",
            InputDeviceType.PeripheralLighting => "PeripheralLighting",
            InputDeviceType.PeripheralHaptics => "PeripheralHaptics",
            InputDeviceType.KeyboardScreenKeys => "KeyboardScreenKeys",
            InputDeviceType.NovintFalcon => "NovintFalcon",
            InputDeviceType.GameTelemetry => "GameTelemetry",
            InputDeviceType.TwitchChat => "TwitchChat",
            _ => "Device"
        };

        /// <summary>The name of a kind of device in the language in use:
        /// "Gamepad", "Wheel", "Keyboard". "Device" for a key that names no
        /// kind.</summary>
        internal static string NameFor(string key)
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            return key switch
            {
                "Gamepad" => s.DeviceType_Gamepad,
                "Joystick" => s.DeviceType_Joystick,
                "Wheel" => s.DeviceType_Wheel,
                "FlightStick" => s.DeviceType_FlightStick,
                "FirstPerson" => s.DeviceType_FirstPerson,
                "Supplemental" => s.DeviceType_Supplemental,
                "Mouse" => s.DeviceType_Mouse,
                "Keyboard" => s.DeviceType_Keyboard,
                "Touchpad" => s.DeviceType_Touchpad,
                "Tablet" => s.DeviceType_Tablet,
                "Midi" => s.DeviceType_Midi,
                "Nfc" => s.DeviceType_Nfc,
                "Microphone" => s.DeviceType_Microphone,
                "ConsumerControl" => s.DeviceType_ConsumerControl,
                "HeadsetMotion" => s.DeviceType_HeadsetMotion,
                "HandheldButtons" => s.DeviceType_HandheldButtons,
                "SystemMotion" => s.DeviceType_SystemMotion,
                "HeadTracker" => s.DeviceType_HeadTracker,
                "VrController" => s.DeviceType_VrController,
                "LogitechGKeys" => s.DeviceType_LogitechGKeys,
                "AnalogKeyboard" => s.DeviceType_AnalogKeyboard,
                "WebMenus" => s.DeviceType_WebMenus,
                "PeripheralHaptics" => s.DeviceType_PeripheralHaptics,
                "PeripheralLighting" => s.DeviceType_PeripheralLighting,
                "KeyboardScreenKeys" => s.DeviceType_KeyboardScreenKeys,
                "NovintFalcon" => s.DeviceType_NovintFalcon,
                "GameTelemetry" => s.DeviceType_GameTelemetry,
                "TwitchChat" => s.DeviceType_TwitchChat,
                _ => s.DeviceType_Device
            };
        }
    }
}
