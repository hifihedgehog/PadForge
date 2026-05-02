using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// Returns <see cref="Visibility.Visible"/> when the bound enum
    /// value's name matches the converter parameter, else
    /// <see cref="Visibility.Collapsed"/>. Used to switch between
    /// per-mode UI sections without adding wrapper properties to the
    /// view-model. Parameter is case-insensitive on the enum's name —
    /// e.g. <c>ConverterParameter=Thresholds</c> shows the panel only
    /// when the bound enum value is <c>SomeEnum.Thresholds</c>.
    /// </summary>
    public sealed class EnumEqualityVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) return Visibility.Collapsed;
            return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // One-way binding only.
            return Binding.DoNothing;
        }
    }
}
