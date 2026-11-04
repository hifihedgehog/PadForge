using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The key each of OpenRGB's key names stands for (#557), so a light
    /// table copied from OpenRGB keeps OpenRGB's own names and still
    /// reaches a key by its HID usage on the Keyboard/Keypad page (0x07,
    /// HID Usage Tables 1.6, section 10). The names are the strings of
    /// OpenRGB RGBController/RGBControllerKeyNames.cpp:18-175 at the
    /// pinned commit. A key that page has no usage for, such as Fn or a
    /// media key, has a label instead (<see cref="LightLabel"/>).
    ///
    /// <para>The names of OpenRGB's national layouts other than Japanese
    /// and Korean (the Nordic, German, French, Spanish and Brazilian
    /// names, and the Japanese @, ^, : and E/J) are printed legends of keys
    /// whose usage depends on the layout they sit in, so they are not
    /// here: a table that uses one names the key it sits on.</para>
    /// </summary>
    internal static class OpenRgbKeys
    {
        /// <summary>The usage or label of an OpenRGB key name. False for
        /// a name this table does not hold.</summary>
        public static bool TryGet(string name, out ushort usage, out LightLabel label)
            => TryGet(name, out usage, out label, out _);

        /// <summary>The usage or label of an OpenRGB key name, and the
        /// number a G-key's label carries (G1 is 1). False for a name this
        /// table does not hold.</summary>
        public static bool TryGet(string name, out ushort usage, out LightLabel label, out int labelNumber)
        {
            usage = 0;
            label = LightLabel.None;
            labelNumber = 0;
            if (name == null) return false;
            if (s_usages.TryGetValue(name, out usage)) return true;
            if (s_labels.TryGetValue(name, out label)) return true;
            // The G-keys families name in their edit lists, "Key: G1" in
            // Corsair's (CorsairPeripheralV2Devices.cpp:67-72) and the G910's
            // (RGBController_LogitechHIDPP20.cpp:848-857), "G1" in the G915
            // X's (lines 443-451).
            bool prefixed = name.StartsWith("Key: ", StringComparison.Ordinal);
            string g = prefixed ? name.Substring(5) : name;
            if (g.Length >= 2 && g.Length <= 3 && g[0] == 'G' && g[1] != '0'
                && int.TryParse(g.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n))
            {
                label = LightLabel.GKey;
                labelNumber = n;
                return true;
            }
            if (!prefixed || g.Length != 2) return false;
            // The macro keys M1 to M6 and the profile keys P1 to P4 the edit
            // lists add (RazerDevices.cpp:119 and 321, CMKeyboardDevices.cpp:302-305
            // and 330-334), and the Tartarus V2's keys, which it numbers 01
            // to 20 (RazerDevices.cpp:1256-1276). The letters and digits
            // alone, "Key: M" and "Key: 1", are keys of their own above.
            if ((g[0] == 'M' || g[0] == 'P') && g[1] >= '1' && g[1] <= '9')
            {
                label = g[0] == 'M' ? LightLabel.MacroKey : LightLabel.ProfileKey;
                labelNumber = g[1] - '0';
                return true;
            }
            if (g[0] >= '0' && g[0] <= '9' && g[1] >= '0' && g[1] <= '9' && (g[0] != '0' || g[1] != '0'))
            {
                label = LightLabel.Numbered;
                labelNumber = (g[0] - '0') * 10 + (g[1] - '0');
                return true;
            }
            return false;
        }

        /// <summary>Whether a usage is a key of the typing block, whose
        /// legend changes with the region a board is made for while its
        /// usage names its place (HID Usage Tables 1.6, section 10, "Typical
        /// AT-101 Position"): the letters and digits, the symbol keys from -
        /// to /, the ISO key beside Left Shift, and the Japanese Ro and Yen
        /// keys.</summary>
        public static bool IsTypingUsage(ushort usage)
            => usage is >= 0x04 and <= 0x27 or >= 0x2D and <= 0x38 or 0x64 or 0x87 or 0x89;

        private static readonly Dictionary<string, ushort> s_usages = new(StringComparer.Ordinal)
        {
            ["Key: Escape"] = 0x29,
            ["Key: F1"] = 0x3A,
            ["Key: F2"] = 0x3B,
            ["Key: F3"] = 0x3C,
            ["Key: F4"] = 0x3D,
            ["Key: F5"] = 0x3E,
            ["Key: F6"] = 0x3F,
            ["Key: F7"] = 0x40,
            ["Key: F8"] = 0x41,
            ["Key: F9"] = 0x42,
            ["Key: F10"] = 0x43,
            ["Key: F11"] = 0x44,
            ["Key: F12"] = 0x45,
            ["Key: Print Screen"] = 0x46,
            ["Key: Scroll Lock"] = 0x47,
            ["Key: Pause/Break"] = 0x48,
            ["Key: Power"] = 0x66,
            ["Key: `"] = 0x35,
            ["Key: 1"] = 0x1E,
            ["Key: 2"] = 0x1F,
            ["Key: 3"] = 0x20,
            ["Key: 4"] = 0x21,
            ["Key: 5"] = 0x22,
            ["Key: 6"] = 0x23,
            ["Key: 7"] = 0x24,
            ["Key: 8"] = 0x25,
            ["Key: 9"] = 0x26,
            ["Key: 0"] = 0x27,
            ["Key: -"] = 0x2D,
            ["Key: ="] = 0x2E,
            ["Key: Backspace"] = 0x2A,
            ["Key: Insert"] = 0x49,
            ["Key: Home"] = 0x4A,
            ["Key: Page Up"] = 0x4B,
            ["Key: Tab"] = 0x2B,
            ["Key: Q"] = 0x14,
            ["Key: W"] = 0x1A,
            ["Key: E"] = 0x08,
            ["Key: R"] = 0x15,
            ["Key: T"] = 0x17,
            ["Key: Y"] = 0x1C,
            ["Key: U"] = 0x18,
            ["Key: I"] = 0x0C,
            ["Key: O"] = 0x12,
            ["Key: P"] = 0x13,
            ["Key: ["] = 0x2F,
            ["Key: ]"] = 0x30,
            ["Key: \\"] = 0x31,
            ["Key: \\ (ANSI)"] = 0x31,
            ["Key: Delete"] = 0x4C,
            ["Key: End"] = 0x4D,
            ["Key: Page Down"] = 0x4E,
            ["Key: Caps Lock"] = 0x39,
            ["Key: A"] = 0x04,
            ["Key: S"] = 0x16,
            ["Key: D"] = 0x07,
            ["Key: F"] = 0x09,
            ["Key: G"] = 0x0A,
            ["Key: H"] = 0x0B,
            ["Key: J"] = 0x0D,
            ["Key: K"] = 0x0E,
            ["Key: L"] = 0x0F,
            ["Key: ;"] = 0x33,
            ["Key: '"] = 0x34,
            ["Key: #"] = 0x32,
            ["Key: Enter"] = 0x28,
            ["Key: Enter (ISO)"] = 0x28,
            ["Key: Left Shift"] = 0xE1,
            ["Key: \\ (ISO)"] = 0x64,
            ["Key: Z"] = 0x1D,
            ["Key: X"] = 0x1B,
            ["Key: C"] = 0x06,
            ["Key: V"] = 0x19,
            ["Key: B"] = 0x05,
            ["Key: N"] = 0x11,
            ["Key: M"] = 0x10,
            ["Key: ,"] = 0x36,
            ["Key: ."] = 0x37,
            ["Key: /"] = 0x38,
            ["Key: Right Shift"] = 0xE5,
            ["Key: Up Arrow"] = 0x52,
            ["Key: Left Control"] = 0xE0,
            ["Key: Left Windows"] = 0xE3,
            ["Key: Left Alt"] = 0xE2,
            ["Key: Space"] = 0x2C,
            // The Skyloong GK104 Pro lights its space bar as two halves
            // (RGBController_SkyloongGK104Pro.cpp:99-100), both one key.
            ["Key: Left Space"] = 0x2C,
            ["Key: Right Space"] = 0x2C,
            ["Key: Right Alt"] = 0xE6,
            ["Key: Right Windows"] = 0xE7,
            ["Key: Menu"] = 0x65,
            ["Key: Right Control"] = 0xE4,
            ["Key: Left Arrow"] = 0x50,
            ["Key: Down Arrow"] = 0x51,
            ["Key: Right Arrow"] = 0x4F,
            ["Key: Num Lock"] = 0x53,
            ["Key: Number Pad /"] = 0x54,
            ["Key: Number Pad *"] = 0x55,
            ["Key: Number Pad -"] = 0x56,
            ["Key: Number Pad +"] = 0x57,
            ["Key: Number Pad ."] = 0x63,
            ["Key: Number Pad Enter"] = 0x58,
            ["Key: Number Pad ="] = 0x67,
            ["Key: Number Pad 0"] = 0x62,
            ["Key: Number Pad 1"] = 0x59,
            ["Key: Number Pad 2"] = 0x5A,
            ["Key: Number Pad 3"] = 0x5B,
            ["Key: Number Pad 4"] = 0x5C,
            ["Key: Number Pad 5"] = 0x5D,
            ["Key: Number Pad 6"] = 0x5E,
            ["Key: Number Pad 7"] = 0x5F,
            ["Key: Number Pad 8"] = 0x60,
            ["Key: Number Pad 9"] = 0x61,
            ["Key: F13"] = 0x68,
            ["Key: F14"] = 0x69,
            ["Key: F15"] = 0x6A,
            ["Key: F16"] = 0x6B,
            ["Key: F17"] = 0x6C,
            ["Key: F18"] = 0x6D,
            ["Key: F19"] = 0x6E,
            ["Key: F20"] = 0x6F,
            ["Key: F21"] = 0x70,
            ["Key: F22"] = 0x71,
            ["Key: F23"] = 0x72,
            ["Key: F24"] = 0x73,
            // The Japanese and Korean keys with a usage of their own
            // (HID Usage Tables 1.6, section 10, notes 15 to 20). Not the
            // Japanese Ro key: OpenRGB names it "Key: _", and its French
            // "_" key too (RGBControllerKeyNames.cpp:161 and 199), so the
            // name alone does not tell the key.
            ["Key: かな"] = 0x88,
            ["Key: ¥"] = 0x89,
            ["Key: 変換"] = 0x8A,
            ["Key: 無変換"] = 0x8B,
            ["Key: 半角/全角"] = 0x35,
            ["Key: 한/영"] = 0x90,
            ["Key: 한자"] = 0x91,
        };

        private static readonly Dictionary<string, LightLabel> s_labels = new(StringComparer.Ordinal)
        {
            ["Key: Left Fn"] = LightLabel.Fn,
            ["Key: Right Fn"] = LightLabel.Fn,
            ["Key: Media Play/Pause"] = LightLabel.PlayPause,
            ["Key: Media Previous"] = LightLabel.Previous,
            ["Key: Media Next"] = LightLabel.Next,
            ["Key: Media Stop"] = LightLabel.Stop,
            ["Key: Media Mute"] = LightLabel.Mute,
            ["Key: Brightness"] = LightLabel.Brightness,
            // Names the families' own edit lists give the lights they add,
            // which KeyboardLayoutManager carries into a layout (#557). The
            // Logitech strips (RGBController_LogitechHIDPP20.cpp:423-456 and
            // 794-830) leave off "Key: ", and their Lighting key, 0x4001, is
            // the brightness key HidppKeyMap labels the same way. Cooler
            // Master's CK550 V2 marks its Num, Caps and Scroll Lock lights
            // N, C and S (CMKeyboardDevices.cpp:675-677).
            ["Brightness"] = LightLabel.Brightness,
            ["Lighting"] = LightLabel.Brightness,
            ["Media Play/Pause"] = LightLabel.PlayPause,
            ["Media Previous"] = LightLabel.Previous,
            ["Media Next"] = LightLabel.Next,
            ["Media Mute"] = LightLabel.Mute,
            ["Game Mode"] = LightLabel.GameMode,
            ["Game Mode Indicator"] = LightLabel.GameMode,
            ["Num Lock Indicator"] = LightLabel.NumLock,
            ["Caps Lock Indicator"] = LightLabel.CapsLock,
            ["Scroll Lock Indicator"] = LightLabel.ScrollLock,
            ["Indicator: N"] = LightLabel.NumLock,
            ["Indicator: C"] = LightLabel.CapsLock,
            ["Indicator: S"] = LightLabel.ScrollLock,
            // The volume keys and knobs (RGBControllerKeyNames.cpp:143-144,
            // RazerDevices.cpp:510, 1093 and 1121), the ROG laptops' Fan and
            // ROG keys (AsusAuraCoreLaptopDevices.cpp:201-202), the
            // BlackWidow V4 Pro's Command Dial (RazerDevices.cpp:354), the
            // Tartarus V2's scroll wheel (line 1275) and the MK850's three
            // Aimpad keys (CMKeyboardDevices.cpp:327-329).
            ["Key: Media Volume +"] = LightLabel.VolumeUp,
            ["Key: Media Volume -"] = LightLabel.VolumeDown,
            ["Key: Media Volume"] = LightLabel.Volume,
            ["Media: Volume Dial"] = LightLabel.Volume,
            ["Key: Fan"] = LightLabel.Fan,
            ["Key: ROG"] = LightLabel.Rog,
            ["Key: Dial"] = LightLabel.Dial,
            ["Key: Scroll Wheel"] = LightLabel.ScrollWheel,
            ["Key: Aim <|>"] = LightLabel.Aimpad,
            ["Key: Aim -"] = LightLabel.AimpadDown,
            ["Key: Aim +"] = LightLabel.AimpadUp,
        };
    }
}
