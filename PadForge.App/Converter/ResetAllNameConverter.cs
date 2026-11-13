using System;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// The name a screen reader reads for a card's Reset All button, from
    /// [0] the words the button shows, [1] its tooltip, [2] the format the
    /// tooltips of the cards are built from (Pad_ResetSection_Format, "Reset
    /// {0}") and [3] the words Reset All: the words, then the card's name, as
    /// in "Reset All, Left Thumbstick". A page holds one such button a card,
    /// all showing the same words, so by the words alone a screen reader
    /// read them alike.
    ///
    /// <para>The card's name is what the tooltip holds where the format holds
    /// {0}. A tooltip written on its own, which the format does not fit,
    /// follows the words whole when it is a phrase. A tooltip that is a
    /// sentence is help, and UI Automation reads it as the help text. The
    /// result is empty, and the button reads the words it shows, when the
    /// tooltip is a sentence or missing, and for a button of this look with
    /// other words: a Reset Calibration button read its whole tooltip after
    /// its name.</para>
    /// </summary>
    public sealed class ResetAllNameConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            string words = values.Length > 0 ? values[0] as string : null;
            string tip = values.Length > 1 ? values[1] as string : null;
            string format = values.Length > 2 ? values[2] as string : null;
            string resetAll = values.Length > 3 ? values[3] as string : null;
            if (string.IsNullOrWhiteSpace(words) || string.IsNullOrWhiteSpace(tip) || words != resetAll)
                return string.Empty;
            string section = Section(tip, format);
            if (ReferenceEquals(section, tip) && !PadForge.Common.AccessibleNames.RowNameConverter.IsPhrase(tip))
                return string.Empty;
            return words + ", " + section;
        }

        /// <summary>What a tooltip holds in the place of the format's {0}, or
        /// the tooltip whole when the format does not fit it.</summary>
        internal static string Section(string tip, string format)
        {
            int at = format?.IndexOf("{0}", StringComparison.Ordinal) ?? -1;
            if (at < 0)
                return tip;
            string lead = format.Substring(0, at), tail = format.Substring(at + 3);
            if (tip.Length > lead.Length + tail.Length
                && tip.StartsWith(lead, StringComparison.Ordinal) && tip.EndsWith(tail, StringComparison.Ordinal))
                return tip.Substring(lead.Length, tip.Length - lead.Length - tail.Length);
            return tip;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
