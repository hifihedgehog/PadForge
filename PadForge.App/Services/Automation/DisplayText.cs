using System.Globalization;
using System.Linq;
using System.Text;

namespace PadForge.Services.Automation
{
    /// <summary>Text from an outside program as PadForge shows it (#597).</summary>
    internal static class DisplayText
    {
        /// <summary>The text with every Unicode format character taken out:
        /// the bidirectional controls, the zero-width characters and the
        /// tag characters, so a name a program gives can neither reverse nor
        /// hide the text around it.</summary>
        internal static string StripFormatCharacters(string text) => string.IsNullOrEmpty(text) ? text ?? string.Empty : string.Concat(text.EnumerateRunes().Where(r => Rune.GetUnicodeCategory(r) != UnicodeCategory.Format).Select(r => r.ToString()));
    }
}
