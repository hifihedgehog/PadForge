using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// Returns Visible when the value is non-null, Collapsed when null.
    /// </summary>
    public sealed class NullToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value != null ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
