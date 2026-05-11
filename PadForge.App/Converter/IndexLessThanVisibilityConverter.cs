using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// Returns <see cref="Visibility.Visible"/> when the bound integer is
    /// strictly greater than the converter parameter, otherwise
    /// <see cref="Visibility.Collapsed"/>. Used by the formula chip
    /// palettes so the per-letter chip is only shown when its 0-based
    /// index is less than the row's / macro's variable count.
    ///
    /// <para>Example: <c>Visibility="{Binding VariableCount,
    /// Converter={StaticResource IndexLessThanVisibilityConverter},
    /// ConverterParameter=3}"</c> reveals the chip when there are at
    /// least 4 variables defined (i.e. index 3 = 'd' is in range).</para>
    /// </summary>
    public sealed class IndexLessThanVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int count = value is int c ? c : 0;
            int index = 0;
            if (parameter is int pi) index = pi;
            else if (parameter is string ps) int.TryParse(ps, NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
            return index < count ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
