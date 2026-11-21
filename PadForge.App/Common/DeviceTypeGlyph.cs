using System.Windows.Media;
using PadForge.Engine;

namespace PadForge.Common
{
    /// <summary>
    /// Maps a device's CapType (<see cref="InputDeviceType"/>) to the Segoe
    /// MDL2 Assets glyph for that device class, so roster surfaces draw the
    /// icon that matches the device instead of a blanket gamepad (#175).
    /// Reuses the characters the app already established elsewhere: E962
    /// (Mouse Output card), EFA5 (Touchpad group and capability chips), E8D6
    /// (MIDI badges), E7FC (controller). A wheel and a joystick, which no
    /// icon font draws, take a line drawing instead (<see cref="ShapeFor"/>).
    /// </summary>
    internal static class DeviceTypeGlyph
    {
        /// <summary>
        /// Glyph for a CapType. A gamepad, a first-person controller and a
        /// supplemental one share E7FC, the app's one controller character,
        /// and unknown types fall back to it as well. A wheel, a joystick
        /// and a flight stick have no glyph: they draw their shape
        /// (<see cref="ShapeFor"/>).
        /// </summary>
        internal static string For(int capType) => capType switch
        {
            InputDeviceType.Driving or InputDeviceType.Joystick or InputDeviceType.Flight => "",
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
            InputDeviceType.PeripheralLighting => "\uE781", // the Lighting group's glyph (#494)
            InputDeviceType.PeripheralHaptics => "\uE877",  // the Force Feedback group's glyph (#494)
            InputDeviceType.KeyboardScreenKeys => "\uE765", // KeyboardClassic: the keys sit on a keyboard (#519)
            InputDeviceType.NovintFalcon => "\uE7FC",       // Game: the Falcon is a game controller (#521)
            InputDeviceType.GameTelemetry => "\uEC4A",      // SpeedHigh, a speedometer (#531; verified in live segmdl2.ttf)
            InputDeviceType.TwitchChat => "\uE8F2",         // ChatBubbles (#555; verified in live segmdl2.ttf and SegoeIcons.ttf)
            _ => "\uE7FC"                                // Game
        };

        /// <summary>The line drawing a kind of device takes in place of a
        /// glyph (Controls.KindIcon): a steering wheel for a wheel, and a
        /// joystick for a joystick and a flight stick. Neither Segoe MDL2
        /// Assets nor Segoe Fluent Icons has a glyph for either, so both
        /// drew as a gamepad (owner, 2026-11-17). Null for every other kind,
        /// which keeps its glyph.</summary>
        internal static Geometry ShapeFor(int capType) => capType switch
        {
            InputDeviceType.Driving => Shapes.SteeringWheel,
            InputDeviceType.Joystick or InputDeviceType.Flight => Shapes.Joystick,
            _ => null
        };

        /// <summary>The steering wheel's drawing on a grid of 24: the rim,
        /// the hub and three spokes. SteeringWheelGeometry in
        /// ControllerIcons.xaml is the same drawing, the mark of the
        /// steering cards.</summary>
        internal const string SteeringWheelPath = "M5,12 A7,7 0 1 1 19,12 A7,7 0 1 1 5,12 Z M10.1,12 A1.9,1.9 0 1 1 13.9,12 A1.9,1.9 0 1 1 10.1,12 Z M10.1,12 L5,12 M13.9,12 L19,12 M12,13.9 L12,19";

        /// <summary>The joystick's drawing on the same grid, in the same
        /// line: the ball, the shaft and the base.</summary>
        internal const string JoystickPath = "M9,7 A3,3 0 1 1 15,7 A3,3 0 1 1 9,7 Z M12,10 L12,15.5 M7,15.5 L17,15.5 A2,2 0 0 1 19,17.5 A2,2 0 0 1 17,19.5 L7,19.5 A2,2 0 0 1 5,17.5 A2,2 0 0 1 7,15.5 Z";

        // The drawings, made the first time a surface asks for one, on the
        // thread that draws it, and frozen so every thread can read them.
        private static class Shapes
        {
            internal static readonly Geometry SteeringWheel = Frozen(SteeringWheelPath);
            internal static readonly Geometry Joystick = Frozen(JoystickPath);

            private static Geometry Frozen(string path)
            {
                var shape = Geometry.Parse(path);
                shape.Freeze();
                return shape;
            }
        }

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
