using System.Globalization;

namespace PadForge.Common
{
    /// <summary>
    /// Text matching as the interface's language compares text (#574): case,
    /// accents, kana type and character width are set aside, so "ecrans"
    /// finds "Écrans" and a full-width "ｍｉｔ" finds "MIT". The About page's
    /// credits filter reads it.
    /// </summary>
    internal static class TextMatch
    {
        internal const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace
            | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

        /// <summary>Where the query starts in the text, or -1.</summary>
        internal static int IndexOf(string text, string query)
            => string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)
                ? -1
                : CultureInfo.CurrentUICulture.CompareInfo.IndexOf(text, query, Loose);

        /// <summary>Whether the text holds the query.</summary>
        internal static bool Contains(string text, string query) => IndexOf(text, query) >= 0;
    }
}
