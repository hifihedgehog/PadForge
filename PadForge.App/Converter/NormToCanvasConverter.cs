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
                // Parameter memo: the XAML uses a handful of literal
                // strings, and this converter sits on the 30 Hz stick-dot
                // bindings, so the per-call Split + Trim was steady churn.
                if (!s_paramCache.TryGetValue(paramStr, out var p))
                {
                    p = ParseParam(paramStr);
                    if (s_paramCache.Count < 64) s_paramCache[paramStr] = p;
                }
                if (p.valid)
                {
                    double halfDot = p.dotSize / 2.0;
                    return Math.Clamp(normalized * p.dimension - halfDot, 0, p.dimension - p.dotSize);
                }
            }
            return 0.0;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool valid, double dimension, double dotSize)> s_paramCache = new();

        private static (bool valid, double dimension, double dotSize) ParseParam(string paramStr)
        {
            var parts = paramStr.Split(',');
            if (parts.Length >= 1 && double.TryParse(parts[0].Trim(), out double dimension))
            {
                double dotSize = 14;
                if (parts.Length >= 2 && double.TryParse(parts[1].Trim(),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out double ds))
                    dotSize = ds;
                return (true, dimension, dotSize);
            }
            return (false, 0, 0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
