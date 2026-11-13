using System;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// One of the three parts of a string around its first pair of square
    /// brackets (#574), so a sentence of the string table can draw its
    /// bracketed words as a link: ConverterParameter 0 is what stands before
    /// the pair, 1 what the pair holds, 2 what stands after. A string with no
    /// closed pair is all part 0. The string table raises a change for every
    /// string when the language changes, so the parts follow it.
    /// </summary>
    public sealed class BracketPartConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int part = parameter is string s
                && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) ? p : 0;
            return ViewModels.DashboardViewModel.BracketPart(value as string, part);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
