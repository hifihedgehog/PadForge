using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>What one light of a device is (#557). Saved by name, so the
    /// order is free, and new kinds go at the end.</summary>
    internal enum LightKind : byte
    {
        /// <summary>Under one key. <see cref="LightInfo.Usage"/> names it.</summary>
        Key = 0,
        /// <summary>One of a device's lighting zones, a stretch of keys or
        /// a part of a mouse lit as one.</summary>
        Zone = 1,
        /// <summary>Edge, side or underglow lighting, LampPurposes Accent
        /// (HID Usage Tables 1.6, 26.3.1).</summary>
        Edge = 2,
        /// <summary>A logo or other branding, LampPurposes Branding.</summary>
        Logo = 3,
        /// <summary>A status light such as Caps Lock or a battery light,
        /// LampPurposes Status.</summary>
        Status = 4,
        /// <summary>Any other light: room or display lighting, or a light a
        /// device does not describe.</summary>
        Other = 5,
    }

    /// <summary>Where a light's key came from, best first (#557).</summary>
    internal enum LightKeySource : byte
    {
        /// <summary>No key: a zone, edge, logo, status or other light, or a
        /// key light no source has named.</summary>
        None = 0,
        /// <summary>The device said so: a LampArray InputBinding, a Lenovo
        /// Spectrum key page, a QMK keycode.</summary>
        Device = 1,
        /// <summary>The vendor's fixed numbering: a Logitech zone ID, a
        /// SteelSeries key code, the Chroma SDK grid.</summary>
        Vendor = 2,
        /// <summary>A table generated from the references at a pinned
        /// commit.</summary>
        Generated = 3,
        /// <summary>The user's own map from Map Key Lights.</summary>
        Learned = 4,
    }

    /// <summary>What a light that is not a keyboard-page key is called on
    /// its chip (#557): a G-key or a media key a device lights. Put into
    /// words when the chip is drawn, so the name follows the interface's
    /// language. Saved nowhere, so the order is free.</summary>
    internal enum LightLabel : byte
    {
        None = 0,
        /// <summary>G and <see cref="LightInfo.LabelNumber"/>, as Logitech
        /// prints it on the key.</summary>
        GKey = 1,
        Brightness = 2,
        PlayPause = 3,
        Mute = 4,
        Next = 5,
        Previous = 6,
        Stop = 7,
        GameMode = 8,
        CapsLock = 9,
        ScrollLock = 10,
        NumLock = 11,
        /// <summary>The Fn key, which has no usage: the keyboard handles it
        /// and never reports it.</summary>
        Fn = 12,
        /// <summary>M and <see cref="LightInfo.LabelNumber"/>, a macro key
        /// as Razer and Cooler Master print it (OpenRGB RazerDevices.cpp:119
        /// and CMKeyboardDevices.cpp:330-334 name them "Key: M1" on).</summary>
        MacroKey = 13,
        /// <summary>P and <see cref="LightInfo.LabelNumber"/>, a profile
        /// key (OpenRGB CMKeyboardDevices.cpp:302-305, the MK Pro L's "Key: P1"
        /// to "Key: P4").</summary>
        ProfileKey = 14,
        VolumeUp = 15,
        VolumeDown = 16,
        /// <summary>A volume knob or dial, one light.</summary>
        Volume = 17,
        /// <summary>The fan key of an ASUS ROG laptop (OpenRGB
        /// AsusAuraCoreLaptopDevices.cpp:201).</summary>
        Fan = 18,
        /// <summary>The ROG key of an ASUS ROG laptop, printed with ROG's
        /// name, which no locale translates (AsusAuraCoreLaptopDevices.cpp:202).</summary>
        Rog = 19,
        /// <summary>A dial that is no volume knob, such as the BlackWidow V4
        /// Pro's Command Dial (OpenRGB RazerDevices.cpp:354).</summary>
        Dial = 20,
        ScrollWheel = 21,
        /// <summary>The Aimpad key that turns Aimpad on and off, and the two
        /// that lower and raise its sensitivity, as the MK850 has them
        /// (OpenRGB CMKeyboardDevices.cpp:327-329).</summary>
        Aimpad = 22,
        AimpadDown = 23,
        AimpadUp = 24,
        /// <summary>A key printed with its number alone, drawn as two digits
        /// (<see cref="LightInfo.LabelNumber"/>), as the Tartarus V2's 01 to
        /// 20 (OpenRGB RazerDevices.cpp:1256-1276).</summary>
        Numbered = 25,
    }

    /// <summary>One light of a unit. <see cref="Address"/> is the index the
    /// family's frames use for it: a lamp ID, a zone ID, a grid cell as
    /// row times columns plus column. <see cref="Usage"/> is the key's HID
    /// usage on the Keyboard/Keypad page (0x07), 0 for no key.
    /// <see cref="X"/>, <see cref="Y"/>, <see cref="Width"/> and
    /// <see cref="Height"/> place it in the unit's own units when
    /// <see cref="HasPosition"/>: micrometers for a LampArray, key widths
    /// for a layout, grid cells for a grid. A light with a width and a
    /// height spans them from its top left corner at X and Y, as a layout's
    /// key does. A light without them is a point at X and Y, as a
    /// LampArray's lamp is.</summary>
    internal readonly record struct LightInfo(int Address, LightKind Kind, ushort Usage, LightKeySource KeySource)
    {
        public float X { get; init; }
        public float Y { get; init; }
        public float Width { get; init; }
        public float Height { get; init; }
        public bool HasPosition { get; init; }

        /// <summary>For a zone, its number among the unit's zones, from 1.
        /// 0 for any other kind.</summary>
        public int Zone { get; init; }

        /// <summary>What names a light that no keyboard-page usage names,
        /// such as a G-key or a media key, and its number for a G-key.</summary>
        public LightLabel Label { get; init; }
        public int LabelNumber { get; init; }

        /// <summary>The group a whole-group color reaches it by
        /// (<see cref="KeyFrame.Groups"/>): "zone" and the zone's number for
        /// a zone, the kind's name in lower case for an edge, logo, status
        /// or other light, and null for a key, which is reached by its
        /// usage.</summary>
        public string Group => Kind switch
        {
            LightKind.Key => null,
            LightKind.Zone => LightGroups.Zone(Zone),
            LightKind.Edge => LightGroups.Edge,
            LightKind.Logo => LightGroups.Logo,
            LightKind.Status => LightGroups.Status,
            _ => LightGroups.Other,
        };
    }

    /// <summary>A strip of addressable LEDs a unit drives, whose length
    /// OpenRGB leaves to its user, its zones flagged
    /// ZONE_FLAG_MANUALLY_CONFIGURABLE_SIZE (#557): its number among the
    /// unit's strips, from 1, the most LEDs it takes, and the count set for
    /// it, 0 for none.</summary>
    internal readonly record struct LightStrip(int Number, int MaxLeds, int Leds);

    /// <summary>The LED counts a device's config holds for its strips
    /// (#557), in the order of the strips' numbers, in the text form
    /// "30,0,12", with no trailing zeros. A count the text lacks is 0, so
    /// a config that has never held any reads as none.</summary>
    internal static class StripCounts
    {
        public static readonly IReadOnlyList<int> None = Array.Empty<int>();

        /// <summary>The counts in <paramref name="text"/>, a part that is no
        /// count reading as 0.</summary>
        public static IReadOnlyList<int> Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return None;
            var parts = text.Split(',');
            var counts = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                counts[i] = int.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int count) ? count : 0;
            return Trimmed(counts);
        }

        public static string Format(IReadOnlyList<int> counts)
        {
            var trimmed = Trimmed(counts);
            return trimmed.Count == 0 ? string.Empty
                : string.Join(",", System.Linq.Enumerable.Select(trimmed, c => c.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        /// <summary>The count of strip <paramref name="number"/>, from 1, or 0
        /// where none is set.</summary>
        public static int At(IReadOnlyList<int> counts, int number)
            => counts != null && number >= 1 && number <= counts.Count ? Math.Max(0, counts[number - 1]) : 0;

        /// <summary>The counts with strip <paramref name="number"/>'s set to
        /// <paramref name="count"/>.</summary>
        public static IReadOnlyList<int> With(IReadOnlyList<int> counts, int number, int count)
        {
            if (number < 1) return Trimmed(counts);
            var next = new int[Math.Max(counts?.Count ?? 0, number)];
            for (int i = 0; i < next.Length; i++) next[i] = At(counts, i + 1);
            next[number - 1] = Math.Max(0, count);
            return Trimmed(next);
        }

        /// <summary>Whether two lists set the same counts, with null and
        /// trailing zeros standing for none.</summary>
        public static bool Same(IReadOnlyList<int> a, IReadOnlyList<int> b)
        {
            int longest = Math.Max(a?.Count ?? 0, b?.Count ?? 0);
            for (int number = 1; number <= longest; number++)
                if (At(a, number) != At(b, number)) return false;
            return true;
        }

        private static IReadOnlyList<int> Trimmed(IReadOnlyList<int> counts)
        {
            int length = counts?.Count ?? 0;
            while (length > 0 && At(counts, length) == 0) length--;
            if (length == 0) return None;
            var trimmed = new int[length];
            for (int i = 0; i < length; i++) trimmed[i] = At(counts, i + 1);
            return trimmed;
        }
    }

    /// <summary>The group names a key frame and the saved key colors use
    /// for lights that are not keys (#557).</summary>
    internal static class LightGroups
    {
        public const string Edge = "edge";
        public const string Logo = "logo";
        public const string Status = "status";
        public const string Other = "other";
        public const string ZonePrefix = "zone";

        public static string Zone(int number) => ZonePrefix + Math.Max(1, number).ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Whether <paramref name="group"/> is a name this class
        /// makes, so a saved setting with anything else is dropped on
        /// load.</summary>
        public static bool IsValid(string group)
        {
            if (string.IsNullOrEmpty(group)) return false;
            if (group is Edge or Logo or Status or Other) return true;
            if (!group.StartsWith(ZonePrefix, StringComparison.Ordinal) || group.Length == ZonePrefix.Length) return false;
            for (int i = ZonePrefix.Length; i < group.Length; i++)
                if (group[i] < '0' || group[i] > '9') return false;
            return group[ZonePrefix.Length] != '0';
        }
    }

    /// <summary>
    /// The lights of one unit, in the order its frames address them
    /// (#557). Made once per unit, from the best source the unit has
    /// (<see cref="LightKeySource"/>), and never changed after: a backend
    /// swaps in a new map when it learns more.
    /// </summary>
    internal sealed class LightMap
    {
        private readonly Dictionary<ushort, int[]> _byUsage = new();

        public static readonly LightMap Empty = new(Array.Empty<LightInfo>(), "none");

        /// <summary>Every light, in address order.</summary>
        public IReadOnlyList<LightInfo> Lights { get; }

        /// <summary>Where the map came from, for the log and the tab: the
        /// reference or the device report it was read from.</summary>
        public string Source { get; }

        /// <summary>The device model the map belongs to, the key Map Key
        /// Lights stores its names under (<see cref="LearnedKeyMaps"/>), or
        /// null for a map no names can be learned for.</summary>
        public string Product { get; init; }

        /// <summary>The strips of addressable LEDs the unit drives that its
        /// user can size, the Lighting tab's LED Strips card, or none.</summary>
        public IReadOnlyList<LightStrip> Strips { get; init; } = Array.Empty<LightStrip>();

        /// <summary>True when every light has a position.</summary>
        public bool Positioned { get; }

        /// <summary>The number of keys with a known usage, each counted
        /// once however many lights it has.</summary>
        public int KeyCount { get; }

        /// <summary>The number of zones.</summary>
        public int ZoneCount { get; }

        /// <summary>The number of edge lights.</summary>
        public int EdgeCount { get; }

        private readonly int[] _edgeNumbers;

        /// <summary>An edge light's number among the unit's edge lights, from
        /// 1, in map order, where the unit has more than one, so their chips
        /// can be told apart as zones are. 0 for any other light.</summary>
        public int EdgeNumberOf(int index)
            => _edgeNumbers != null && (uint)index < (uint)_edgeNumbers.Length ? _edgeNumbers[index] : 0;

        /// <summary>The number of key lights whose key no source named, the
        /// ones Map Key Lights asks about.</summary>
        public int UnnamedKeyCount { get; }

        public LightMap(IReadOnlyList<LightInfo> lights, string source)
        {
            Lights = lights ?? Array.Empty<LightInfo>();
            Source = source ?? string.Empty;
            bool positioned = Lights.Count > 0;
            var byUsage = new Dictionary<ushort, List<int>>();
            for (int i = 0; i < Lights.Count; i++)
            {
                var light = Lights[i];
                if (!light.HasPosition) positioned = false;
                if (light.Kind == LightKind.Zone) ZoneCount = Math.Max(ZoneCount, light.Zone);
                if (light.Kind == LightKind.Edge) EdgeCount++;
                if (light.Kind != LightKind.Key) continue;
                if (light.Usage == 0)
                {
                    UnnamedKeyCount++;
                    continue;
                }
                if (!byUsage.TryGetValue(light.Usage, out var list)) byUsage[light.Usage] = list = new List<int>(1);
                list.Add(i);
            }
            foreach (var pair in byUsage) _byUsage[pair.Key] = pair.Value.ToArray();
            KeyCount = _byUsage.Count;
            Positioned = positioned;
            if (EdgeCount > 1)
            {
                _edgeNumbers = new int[Lights.Count];
                int number = 0;
                for (int i = 0; i < Lights.Count; i++)
                    if (Lights[i].Kind == LightKind.Edge) _edgeNumbers[i] = ++number;
            }
        }

        /// <summary>The indexes into <see cref="Lights"/> of the lights
        /// under <paramref name="usage"/>, or none.</summary>
        public IReadOnlyList<int> IndexesOf(ushort usage)
            => _byUsage.TryGetValue(usage, out var indexes) ? indexes : Array.Empty<int>();

        /// <summary>The usages of every key the unit lights.</summary>
        public IEnumerable<ushort> Usages => _byUsage.Keys;

        /// <summary>Whether the unit has more than one light to color, the
        /// test for a Key Colors or Zone Colors card.</summary>
        public bool PerLight => Lights.Count > 1;

        /// <summary>Whether any light is a key, which names the card Key
        /// Colors rather than Zone Colors.</summary>
        public bool HasKeys => KeyCount > 0 || UnnamedKeyCount > 0;
    }
}
