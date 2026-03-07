using System;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// Converts a normalized value (0.0–1.0) to a Canvas position,
    /// centered by subtracting half the dot size.
    ///
    /// ConverterParameter formats:
    ///   "canvasDim"           — dot size defaults to 14.
    ///   "canvasDim,dotSize"   — explicit dot size.
    /// Output = value * (canvasDim - dotSize), clamped to [0, canvasDim - dotSize].
    /// </summary>
    public sealed class NormToCanvasConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double normalized && parameter is string paramStr)
            {
                var parts = paramStr.Split(',');
                if (parts.Length >= 1 && double.TryParse(parts[0].Trim(), out double dimension))
                {
                    double dotSize = 14;
                    if (parts.Length >= 2 && double.TryParse(parts[1].Trim(),
                        NumberStyles.Any, CultureInfo.InvariantCulture, out double ds))
                        dotSize = ds;

                    double halfDot = dotSize / 2.0;
                    return Math.Clamp(normalized * dimension - halfDot, 0, dimension - dotSize);
                }
            }
            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
