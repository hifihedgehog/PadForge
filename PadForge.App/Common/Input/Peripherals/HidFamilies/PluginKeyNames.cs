using System;
using System.Collections.Generic;
using System.Linq;

namespace PadForge.Common.Input.Peripherals.HidFamilies
{
    /// <summary>The key names SignalRGB's plugins use, as HID usages of the
    /// keyboard page by the key's place (HID Usage Tables 1.6, section 10), or
    /// as labels for a key no usage names.</summary>
    internal static class PluginKeyNames
    {
        private static readonly Dictionary<string, ushort> s_usages = Build();

        private static Dictionary<string, ushort> Build()
        {
            var usages = new Dictionary<string, ushort>(StringComparer.Ordinal)
            {
                ["Esc"] = 0x29, ["`"] = 0x35, ["-_"] = 0x2D, ["=+"] = 0x2E, ["+="] = 0x2E, ["Backspace"] = 0x2A,
                ["Tab"] = 0x2B, ["["] = 0x2F, ["]"] = 0x30, ["\\"] = 0x31, ["CapsLock"] = 0x39,
                [";"] = 0x33, ["'"] = 0x34, ["Enter"] = 0x28, ["Left Shift"] = 0xE1, [","] = 0x36, ["."] = 0x37,
                ["/"] = 0x38, ["Right Shift"] = 0xE5, ["Left Ctrl"] = 0xE0, ["Left Win"] = 0xE3, ["Left Alt"] = 0xE2,
                ["Space"] = 0x2C, ["Right Alt"] = 0xE6, ["Menu"] = 0x65, ["Right Ctrl"] = 0xE4,
                ["Print Screen"] = 0x46, ["Scroll Lock"] = 0x47, ["Pause Break"] = 0x48, ["Insert"] = 0x49,
                ["Home"] = 0x4A, ["Page Up"] = 0x4B, ["Del"] = 0x4C, ["End"] = 0x4D, ["Page Down"] = 0x4E,
                ["Page DoWN"] = 0x4E, ["Right Arrow"] = 0x4F, ["Left Arrow"] = 0x50, ["Down Arrow"] = 0x51,
                ["Up Arrow"] = 0x52, ["NumLock"] = 0x53, ["Num /"] = 0x54, ["Num *"] = 0x55, ["Num -"] = 0x56,
                ["Num +"] = 0x57, ["Num Enter"] = 0x58, ["Num 0"] = 0x62, ["Num ."] = 0x63, ["Num ,"] = 0x63,
                ["ISO #"] = 0x32, ["ISO /"] = 0x32, ["ISO <"] = 0x64, ["# ISO"] = 0x32, ["< ISO"] = 0x64,
                // The Speedlink ORIOS keyboard names its legends in German.
                ["^"] = 0x35, ["ß"] = 0x2D, ["´"] = 0x2E, ["Ü"] = 0x2F, ["Ö"] = 0x33, ["Ä"] = 0x34,
                ["#"] = 0x32, ["<"] = 0x64, ["AltGr"] = 0xE6,
                // Other spellings in the QMK plugins.
                ["Delete"] = 0x4C, ["Ins"] = 0x49, ["PgUp"] = 0x4B, ["PgDn"] = 0x4E, ["PrtSc"] = 0x46, ["Screenshot"] = 0x46,
                ["L Alt"] = 0xE2, ["L Ctrl"] = 0xE0, ["R Alt"] = 0xE6, ["R Ctrl"] = 0xE4, ["R Shift"] = 0xE5,
                ["Right Win"] = 0xE7, ["Win"] = 0xE3, ["Numlock"] = 0x53, ["Num"] = 0x53, ["ISO_#"] = 0x32, ["ISO_<"] = 0x64,
                ["Upp Arrow"] = 0x52, ["|"] = 0x31, ["\\|"] = 0x31, ["~"] = 0x35, ["="] = 0x2E, ["-"] = 0x2D, ["+"] = 0x2E, ["F23"] = 0x72, ["F24"] = 0x73,
                ["Enm"] = 0x4A, [">"] = 0x37, ["Space 1"] = 0x2C, ["Space 2"] = 0x2C, ["\\\\"] = 0x31,
                // The Nordic legends of the GMMK and Keychron ISO boards.
                ["§"] = 0x35, ["¨"] = 0x30, ["Å"] = 0x2F, ["Æ"] = 0x34, ["Ø"] = 0x33,
            };
            for (int i = 0; i < 26; i++) usages[((char)('A' + i)).ToString()] = (ushort)(0x04 + i);
            for (int i = 1; i <= 9; i++) usages[i.ToString()] = (ushort)(0x1D + i);
            usages["0"] = 0x27;
            for (int i = 1; i <= 12; i++) usages["F" + i] = (ushort)(0x39 + i);
            for (int i = 1; i <= 9; i++) usages["Num " + i] = (ushort)(0x58 + i);
            return usages;
        }

        /// <summary>The lights of a keyboard's table, each at its LED index
        /// less <paramref name="baseIndex"/>. <paramref name="places"/> gives
        /// a name another usage where a keyboard's legends differ, the
        /// Speedlink's QWERTZ Y and Z, or the Fnatic's ISO keys.</summary>
        internal static IReadOnlyList<LightInfo> Lights(PluginKeyTable table, int baseIndex = 0,
            IReadOnlyDictionary<string, ushort> places = null)
        {
            var lights = new LightInfo[table.Names.Length];
            for (int i = 0; i < lights.Length; i++)
            {
                string name = (table.Names[i] ?? "").Trim();
                int address = table.Indexes[i] - baseIndex;
                // A second LED under a key lights with its key.
                string key = name.StartsWith("2nd ", StringComparison.Ordinal) ? name.Substring(4) : name;
                LightInfo light;
                if (table.Usages != null && table.Usages[i] != 0)
                    light = new LightInfo(address, LightKind.Key, table.Usages[i], LightKeySource.Generated);
                else if (table.Usages != null)
                    light = Other(address, key);
                else if (places != null && places.TryGetValue(key, out ushort place))
                    light = new LightInfo(address, LightKind.Key, place, LightKeySource.Generated);
                else if (s_usages.TryGetValue(key, out ushort usage))
                    light = new LightInfo(address, LightKind.Key, usage, LightKeySource.Generated);
                else
                    light = Other(address, key);
                // A plugin whose places are not the board's gives none, and the key picker
                // draws the keys from their usages.
                lights[i] = table.X == null ? light : light with { X = table.X[i], Y = table.Y[i], Width = 1, Height = 1, HasPosition = true };
            }
            return lights;
        }

        /// <summary>A table's names with the given entries set and the rest null:
        /// a generated table names only the lights no usage names.</summary>
        internal static string[] Sparse(int count, params (int At, string Name)[] named)
        {
            var names = new string[count];
            foreach (var (at, name) in named) names[at] = name;
            return names;
        }

        /// <summary>A light no keyboard-page usage names.</summary>
        internal static LightInfo Other(int address, string name)
        {
            var label = name switch
            {
                "Fn" or "FN Lock" or "FN1" or "FN2" => LightLabel.Fn,
                "Volume Up" => LightLabel.VolumeUp,
                "Volume Down" => LightLabel.VolumeDown,
                "Light" or "Cycle LED" => LightLabel.Brightness,
                "Knob" or "Knob1" or "Knob 2" or "Knob3" or "Encoder Knob" => LightLabel.Dial,
                "Brightness" => LightLabel.Brightness,
                "Mute" or "Media Mute" or "Mute Mic" or "VOLUME_MUTE" => LightLabel.Mute,
                "MediaPlayPause" => LightLabel.PlayPause,
                "MediaStop" => LightLabel.Stop,
                "MediaFastForward" => LightLabel.Next,
                "MediaRewind" => LightLabel.Previous,
                "Game Mode" or "Lock" => LightLabel.GameMode,
                "Fan" => LightLabel.Fan,
                "ROG" => LightLabel.Rog,
                _ => LightLabel.None,
            };
            if (name.StartsWith("Logo", StringComparison.Ordinal) || name is "LeftLogo" or "RightLogo")
                return new LightInfo(address, LightKind.Logo, 0, LightKeySource.None);
            if (name.StartsWith("TopBar", StringComparison.Ordinal) || name.StartsWith("BottomBar", StringComparison.Ordinal)
                || name.StartsWith("LeftBar", StringComparison.Ordinal) || name.StartsWith("RightBar", StringComparison.Ordinal)
                || name.StartsWith("Left LED", StringComparison.Ordinal) || name.StartsWith("Right LED", StringComparison.Ordinal)
                || name.StartsWith("UG", StringComparison.Ordinal) || name.StartsWith("Underglow", StringComparison.OrdinalIgnoreCase))
                return new LightInfo(address, LightKind.Edge, 0, LightKeySource.None);
            // The Q0's four buttons above its number pad carry shapes, and an
            // unnamed entry in a table is a key no plugin names.
            if (name is "Circle" or "Cross" or "Square" or "Triangle" or "")
                return new LightInfo(address, LightKind.Key, 0, LightKeySource.None);
            if (name.Length is 2 or 3 && name[0] == 'G' && int.TryParse(name.AsSpan(1), out int gKey))
                return new LightInfo(address, LightKind.Other, 0, LightKeySource.None) { Label = LightLabel.GKey, LabelNumber = gKey };
            if (name.Length == 2 && name[0] == 'M' && char.IsDigit(name[1]))
                return new LightInfo(address, LightKind.Other, 0, LightKeySource.None) { Label = LightLabel.MacroKey, LabelNumber = name[1] - '0' };
            if (name.StartsWith("Led ", StringComparison.Ordinal))
                return new LightInfo(address, LightKind.Edge, 0, LightKeySource.None);
            if (name == "Profile")
                return new LightInfo(address, LightKind.Other, 0, LightKeySource.None) { Label = LightLabel.ProfileKey, LabelNumber = 1 };
            if (name is "Space Left" or "Space Right")
                return new LightInfo(address, LightKind.Key, 0x2C, LightKeySource.Generated);
            return new LightInfo(address, LightKind.Other, 0, LightKeySource.None) { Label = label };
        }

        /// <summary>The names <see cref="Lights"/> finds neither a usage nor a
        /// label for, for the tests. A second LED's name counts as its key's.</summary>
        internal static IEnumerable<string> Unresolved(PluginKeyTable table)
            => table.Names.Select(n => (n ?? "").Trim()).Select(n => n.StartsWith("2nd ", StringComparison.Ordinal) ? n.Substring(4) : n)
                .Where(n => !s_usages.ContainsKey(n) && Other(0, n) is { Kind: LightKind.Other, Label: LightLabel.None });

        /// <summary>A light of a Corsair mouse or stand from what its plugin
        /// names it.</summary>
        internal static LightInfo Named(int address, string name)
        {
            var other = Other(address, name);
            if (other.Kind != LightKind.Other || other.Label != LightLabel.None) return other;
            if (name.Contains("Scroll", StringComparison.OrdinalIgnoreCase))
                return new LightInfo(address, LightKind.Other, 0, LightKeySource.None) { Label = LightLabel.ScrollWheel };
            if (name.Contains("logo", StringComparison.OrdinalIgnoreCase))
                return new LightInfo(address, LightKind.Logo, 0, LightKeySource.None);
            if (name.Contains("dpi", StringComparison.OrdinalIgnoreCase) || name.Contains("Indicator", StringComparison.OrdinalIgnoreCase))
                return new LightInfo(address, LightKind.Status, 0, LightKeySource.None);
            return new LightInfo(address, LightKind.Edge, 0, LightKeySource.None);
        }
    }
}
