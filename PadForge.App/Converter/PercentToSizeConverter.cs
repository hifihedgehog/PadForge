using System;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// Converts a percentage value (0–100) to a pixel size.
    /// ConverterParameter = max size in pixels (e.g., "200" or "-100" for offset).
    /// Output = value / 100.0 * |parameter|, with sign preserved for offsets.
    /// Used for dead zone ring visualization on stick previews.
    /// </summary>
    public sealed class PercentToSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double percent = 0;

            if (value is int i)
                percent = i;
            else if (value is double d)
                percent = d;
            else
                return 0.0;

            double maxSize = 200.0;
            if (parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed))
                maxSize = parsed;

            return percent / 100.0 * maxSize;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
