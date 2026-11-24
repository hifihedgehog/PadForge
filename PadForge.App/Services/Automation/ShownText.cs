using System.Globalization;
using System.Text;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// Text a program supplied, or a file's name, made fit to show the user
    /// (#597). Every format character goes: the bidirectional overrides,
    /// embeddings and isolates (U+202A to U+202E, U+2066 to U+2069) that turn
    /// "gpj.exe" into what reads as "exe.jpg", the marks LRM, RLM and ALM, the
    /// zero-width space, joiners and word joiner, the soft hyphen and the byte
    /// order mark, all of Unicode's category Cf. Control characters go too,
    /// and a line or paragraph separator becomes a space, so a name holds one
    /// line. A program then cannot make a name read as another program's,
    /// nor hide part of one.
    ///
    /// <para>The one helper every name a program supplies and every file
    /// name shown goes through: hello names, device and control names,
    /// profile names a program sends, what Activity writes and what the
    /// request card shows.</para>
    /// </summary>
    internal static class ShownText
    {
        /// <summary>The text with every format and control character out,
        /// or the text itself when it has none. Null stays null.</summary>
        internal static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int i = 0;
            while (i < text.Length && Kind(text[i]) == Keep.Same) i++;
            if (i == text.Length) return text;
            var sb = new StringBuilder(text.Length);
            sb.Append(text, 0, i);
            for (; i < text.Length; i++)
            {
                switch (Kind(text[i]))
                {
                    case Keep.Same: sb.Append(text[i]); break;
                    case Keep.Space: sb.Append(' '); break;
                }
            }
            return sb.ToString();
        }

        private enum Keep { Same, Space, Out }

        /// <summary>What a character becomes: itself, a space, or
        /// nothing.</summary>
        private static Keep Kind(char c) => CharUnicodeInfo.GetUnicodeCategory(c) switch
        {
            UnicodeCategory.Format or UnicodeCategory.Control => Keep.Out,
            UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator => Keep.Space,
            _ => Keep.Same,
        };
    }
}
