using System;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// Maps an integer 0..25 to the corresponding lowercase letter ('a'..'z').
    /// Indices &gt;= 26 fall back to "s[N]" notation, matching what the macro
    /// custom-expression evaluator accepts in formula text.
    /// </summary>
    public sealed class IndexToLetterConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int idx = value is int i ? i : 0;
            if (idx < 0) return "";
            if (idx < 26) return ((char)('a' + idx)).ToString();
            return "s[" + idx + "]";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
