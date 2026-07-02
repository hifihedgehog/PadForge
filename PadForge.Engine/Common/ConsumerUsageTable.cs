namespace PadForge.Engine.Common
{
    /// <summary>
    /// The canonical Consumer Control usage table (issue #168). Index in
    /// <see cref="Fixed"/> IS the button index the mapping layer sees, so the
    /// table is APPEND-ONLY: never reorder or remove rows, or every saved
    /// "Button N" mapping on a consumer device silently retargets. Names are
    /// invariant English; MappingDisplayResolver.LocalizeObjectName carries
    /// the per-locale strings (DevObj_Consumer* keys).
    ///
    /// Usages a device reports that are not in this table get a dynamic index
    /// after the fixed block (up to <see cref="DynamicSlack"/> per session),
    /// displayed as "Consumer 0xNNNN". Dynamic indices are not stable across
    /// sessions; if one becomes common, promote it here (append-only).
    ///
    /// Usage IDs are from the HID Usage Tables, Consumer Page (0x0C). The
    /// reporter's remote (#168) carries 0x41 Menu Pick (the OK button) and
    /// 0xCF Voice Command, which Linux maps to KEY_SELECT / KEY_VOICECOMMAND
    /// exactly as the issue's evtest dump shows.
    /// </summary>
    public static class ConsumerUsageTable
    {
        public readonly struct Entry
        {
            public readonly ushort Usage;
            public readonly string Name;
            public Entry(ushort usage, string name) { Usage = usage; Name = name; }
        }

        public static readonly Entry[] Fixed =
        {
            new(0x30,  "Power"),             // 0
            new(0x40,  "Menu"),              // 1
            new(0x41,  "OK"),                // 2  Menu Pick, the #168 OK button
            new(0x42,  "Menu Up"),           // 3
            new(0x43,  "Menu Down"),         // 4
            new(0x44,  "Menu Left"),         // 5
            new(0x45,  "Menu Right"),        // 6
            new(0x46,  "Menu Escape"),       // 7
            // "Media" prefix keeps 0xB1 distinct from the keyboard's Pause
            // (VK_PAUSE, the Break key) in the shared object-name localizer.
            new(0xB0,  "Media Play"),        // 8
            new(0xB1,  "Media Pause"),       // 9
            new(0xB2,  "Record"),            // 10
            new(0xB3,  "Fast Forward"),      // 11
            new(0xB4,  "Rewind"),            // 12
            new(0xB5,  "Next Track"),        // 13
            new(0xB6,  "Previous Track"),    // 14
            new(0xB7,  "Media Stop"),        // 15
            new(0xB8,  "Eject"),             // 16
            new(0xCD,  "Play/Pause"),        // 17
            new(0xCF,  "Voice Command"),     // 18  the #168 voice button
            new(0xE2,  "Mute"),              // 19
            new(0xE9,  "Volume Up"),         // 20
            new(0xEA,  "Volume Down"),       // 21
            new(0x94,  "Quit"),              // 22
            new(0x9C,  "Channel Up"),        // 23
            new(0x9D,  "Channel Down"),      // 24
            new(0x183, "Media Player"),      // 25
            new(0x18A, "Email"),             // 26
            new(0x192, "Calculator"),        // 27
            new(0x194, "File Browser"),      // 28
            new(0x221, "Browser Search"),    // 29
            new(0x223, "Browser Home"),      // 30
            new(0x224, "Browser Back"),      // 31
            new(0x225, "Browser Forward"),   // 32
            new(0x226, "Browser Stop"),      // 33
            new(0x227, "Browser Refresh"),   // 34
            new(0x22A, "Browser Bookmarks"), // 35
        };

        /// <summary>Session-dynamic slots appended after the fixed block for
        /// usages not in the table.</summary>
        public const int DynamicSlack = 16;

        /// <summary>Total button slots a consumer device exposes.</summary>
        public static int TotalSlots => Fixed.Length + DynamicSlack;

        /// <summary>Fixed index for a usage, or -1 when it is not in the table.</summary>
        public static int IndexOf(ushort usage)
        {
            for (int i = 0; i < Fixed.Length; i++)
                if (Fixed[i].Usage == usage) return i;
            return -1;
        }

        /// <summary>Display name for a dynamic (untabled) usage.</summary>
        public static string DynamicName(ushort usage) => $"Consumer 0x{usage:X4}";
    }
}
