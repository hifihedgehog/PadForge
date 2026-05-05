using System;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// Converts a signed normalized value (-1.0..+1.0) to a Canvas
    /// position, centered by subtracting half the dot size. -1 lands
    /// at the left/top edge, 0 at the geometric center of the canvas,
    /// +1 at the right/bottom edge. Used by the Constant Force grid
    /// (signed magnitude) where <see cref="NormToCanvasConverter"/>'s
    /// 0..1 input convention doesn't fit.
    ///
    /// ConverterParameter formats:
    ///   "canvasDim"           — dot size defaults to 14.
    ///   "canvasDim,dotSize"   — explicit dot size.
    /// Output = (value + 1) / 2 * canvasDim - halfDot, clamped to
    /// [-halfDot, canvasDim - halfDot].
    /// </summary>
    public sealed class SignedNormToCanvasConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double v && parameter is string paramStr)
            {
                var parts = paramStr.Split(',');
                if (parts.Length >= 1 &&
                    double.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double dim))
                {
                    double dotSize = 14;
                    if (parts.Length >= 2 &&
                        double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double ds))
                        dotSize = ds;

                    if (v < -1) v = -1;
                    if (v > 1) v = 1;
                    double halfDot = dotSize / 2.0;
                    double pos = (v + 1.0) * 0.5 * dim - halfDot;
                    double max = dim - dotSize;
                    return Math.Clamp(pos, -halfDot, max);
                }
            }
            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
