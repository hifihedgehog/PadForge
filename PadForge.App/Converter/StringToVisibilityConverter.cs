using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// Returns Visible when the string is non-null and non-empty, Collapsed otherwise.
    /// </summary>
    public sealed class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is string s && !string.IsNullOrEmpty(s)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
