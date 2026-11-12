using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// Returns Visible when the value is non-null, Collapsed when null.
    /// With the parameter Invert it is the other way round: Visible when
    /// there is no value. A picker's item uses that to show a part of
    /// itself in the closed picker only, where no list item stands above
    /// it.
    /// </summary>
    public sealed class NullToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool shown = value != null;
            if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) shown = !shown;
            return shown ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
