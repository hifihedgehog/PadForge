using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// A status line as Home's tile draws it (#574): the same words, with
    /// two places where an address in it may break across lines, after
    /// "://" and before a port's colon. A wrapped TextBlock breaks a word
    /// that does not fit at the last character that does, so in a tile one
    /// character too narrow "https://10.19.90.40:8080" read
    /// "https://10.19.90.40:808" over "0". Each break place is a zero-width
    /// space, which draws nothing and takes no room. Only the drawn text
    /// carries them: the status a screen reader gets stays the string as
    /// written. A line with no address comes back unchanged.
    /// </summary>
    public sealed class SoftBreakAddressConverter : IValueConverter
    {
        private const string BreakPlace = "\u200B";

        // The colon between a host and its port: a host character before
        // it, one to five digits after it, and then the address ends, at a
        // slash, a space, a comma, a semicolon, a closing bracket, a period
        // that ends the sentence, or the line's end.
        private static readonly Regex PortColon = new(
            @"(?<=[\w\]]):(?=\d{1,5}(?:$|[/\s,;)]|\.(?:\s|$)))", RegexOptions.CultureInvariant);

        /// <summary>The line with a break place after each "://" and before
        /// each port's colon.</summary>
        internal static string Apply(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("://", StringComparison.Ordinal) < 0) return text;
            return PortColon.Replace(text.Replace("://", "://" + BreakPlace), BreakPlace + ":");
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is string text ? Apply(text) : value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
