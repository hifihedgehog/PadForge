using System;

namespace PadForge.Common
{
    /// <summary>
    /// A battery's level as a picture: the Segoe MDL2 Assets battery glyph
    /// for the nearest tenth, from the charging set while the device
    /// charges. It stands before the percentage wherever a battery shows.
    /// Battery0 to Battery9 are U+E850 to U+E859 and Battery10 is U+E83F.
    /// BatteryCharging0 to BatteryCharging8 are U+E85A to U+E862,
    /// BatteryCharging9 is U+E83E and BatteryCharging10 is U+EA93 (the names
    /// of the Segoe MDL2 documentation table, each codepoint checked against
    /// the installed font).
    /// </summary>
    internal static class BatteryGlyphs
    {
        /// <summary>The font the glyphs are in, for an element that holds
        /// a glyph alone.</summary>
        internal const string FontFamily = "Segoe MDL2 Assets";

        /// <summary>The fonts of a run that holds a glyph among a text's
        /// other runs: a text font, then the glyphs' font. A run with no
        /// text still takes its font for what WPF sets at its edges, and the
        /// glyphs' font draws those as two boxes: a row with no battery read
        /// "Mouse" and two boxes. A text font draws nothing for them, and
        /// has no glyph at the battery's codepoints, which fall through to
        /// the glyphs' font.</summary>
        internal const string RunFontFamily = "Segoe UI, Segoe MDL2 Assets";

        /// <summary>The glyph for a level from 0 to 100, or nothing for a
        /// device that reports no battery (a negative level).</summary>
        internal static string For(int percent, bool charging)
        {
            if (percent < 0) return string.Empty;
            int tenth = Math.Clamp((percent + 5) / 10, 0, 10);
            int code = charging
                ? (tenth == 10 ? 0xEA93 : tenth == 9 ? 0xE83E : 0xE85A + tenth)
                : (tenth == 10 ? 0xE83F : 0xE850 + tenth);
            return char.ConvertFromUtf32(code);
        }
    }
}
