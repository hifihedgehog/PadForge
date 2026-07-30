using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PadForge.Converters
{
    /// <summary>
    /// Multi-binding converter: takes (Red, Green, Blue) byte values
    /// and produces a <see cref="SolidColorBrush"/> for the lightbar
    /// preview swatch on the Lighting tab. Falls back to black on
    /// invalid input.
    /// </summary>
    public sealed class RgbToBrushConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            byte r = ToByte(values, 0);
            byte g = ToByte(values, 1);
            byte b = ToByte(values, 2);
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze(); // render-thread-friendly, no dispatcher affinity
            return brush;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => null;

        private static byte ToByte(object[] values, int idx)
        {
            if (values == null || idx >= values.Length) return 0;
            try { return System.Convert.ToByte(values[idx]); }
            catch { return 0; }
        }
    }
}
