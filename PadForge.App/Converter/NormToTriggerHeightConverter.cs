using System;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// Converts a normalized value (0.0-1.0) to a pixel dimension.
    /// ConverterParameter = max size in pixels (e.g., "40").
    /// Used for trigger fill bars and motor activity indicators.
    /// </summary>
    public sealed class NormToTriggerHeightConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double norm = 0;
            if (value is double d) norm = d;
            else if (value is float f) norm = f;

            double maxSize = 40;
            if (parameter is string s && double.TryParse(s,
                NumberStyles.Any, CultureInfo.InvariantCulture, out double h))
                maxSize = h;

            return Math.Clamp(norm * maxSize, 0, maxSize);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
