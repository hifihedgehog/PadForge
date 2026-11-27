using System;
using System.Globalization;
using System.Text;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// Text a program supplied, or a file's name, made fit to show the user
    /// (#600). Every format character goes: the bidirectional overrides,
    /// embeddings and isolates (U+202A to U+202E, U+2066 to U+2069) that turn
    /// "gpj.exe" into what reads as "exe.jpg", the marks LRM, RLM and ALM, the
    /// zero-width space, joiners and word joiner, the soft hyphen and the byte
    /// order mark, all of Unicode's category Cf. Control characters go too,
    /// and a line or paragraph separator becomes a space, so a name holds one
    /// line and cannot read backward. Every other character stays: a letter
    /// of another script drawn like a Latin one (U+0430 for a), and a letter
    /// drawn blank (U+3164 HANGUL FILLER), so a name can still look like
    /// another program's.
    ///
    /// <para>The one helper every name a program supplies and every file
    /// name shown goes through: hello names, device and control names,
    /// profile names a program sends, what Activity writes and what the
    /// request card shows.</para>
    /// </summary>
    public static class ShownText
    {
        /// <summary>The text with every format and control character out,
        /// or the text itself when it has none. Null stays null. Characters
        /// are read as whole code points, so a format character outside the
        /// Basic Multilingual Plane (the tag characters U+E0000 to U+E007F)
        /// goes as well, and a lone surrogate, which shows as no character,
        /// goes too.</summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int i = 0;
            while (i < text.Length && !char.IsSurrogate(text[i]) && Kind(new Rune(text[i])) == Keep.Same) i++;
            if (i == text.Length) return text;
            var sb = new StringBuilder(text.Length);
            sb.Append(text, 0, i);
            while (i < text.Length)
            {
                if (Rune.DecodeFromUtf16(text.AsSpan(i), out Rune rune, out int used) != System.Buffers.OperationStatus.Done)
                {
                    // A lone surrogate.
                    i += Math.Max(used, 1);
                    continue;
                }
                switch (Kind(rune))
                {
                    case Keep.Same: sb.Append(text, i, used); break;
                    case Keep.Space: sb.Append(' '); break;
                }
                i += used;
            }
            return sb.ToString();
        }

        private enum Keep { Same, Space, Out }

        /// <summary>What a character becomes: itself, a space, or
        /// nothing.</summary>
        private static Keep Kind(Rune c) => Rune.GetUnicodeCategory(c) switch
        {
            UnicodeCategory.Format or UnicodeCategory.Control => Keep.Out,
            UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator => Keep.Space,
            _ => Keep.Same,
        };
    }
}
