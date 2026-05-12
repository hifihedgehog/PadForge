using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PadForge.Converters
{
    /// <summary>Converts an ARGB / RGB hex string (e.g. "#FF8E44AD" or
    /// "#8E44AD") to a <see cref="SolidColorBrush"/>. Empty or invalid input
    /// falls back to a muted gray so the colored-dot affordance on the
    /// shift-layer tab strip never renders as the default white indicator
    /// when a layer has no color authored.</summary>
    public class HexToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush Fallback =
            new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

        static HexToBrushConverter() { Fallback.Freeze(); }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string hex = value as string;
            if (string.IsNullOrWhiteSpace(hex)) return Fallback;
            try
            {
                if (ColorConverter.ConvertFromString(hex) is Color c)
                    return new SolidColorBrush(c);
            }
            catch { }
            return Fallback;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
