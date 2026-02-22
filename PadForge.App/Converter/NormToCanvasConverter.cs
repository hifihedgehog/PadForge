using System;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// Converts a normalized value (0.0–1.0) to a Canvas position,
    /// centered by subtracting half the dot size (6px).
    /// 
    /// ConverterParameter = canvas dimension (e.g. "200").
    /// Output = value * dimension - 6.
    /// </summary>
    public class NormToCanvasConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double normalized && parameter is string dimStr
                && double.TryParse(dimStr, out double dimension))
            {
                // Map normalized 0..1 to canvas position, offset by half dot size (7)
                return Math.Clamp(normalized * dimension - 7.0, 0, dimension - 14);
            }
            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
