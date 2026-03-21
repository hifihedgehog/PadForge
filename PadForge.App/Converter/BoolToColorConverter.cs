using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PadForge.Converters
{
    /// <summary>
    /// Converts a boolean value to a <see cref="SolidColorBrush"/>.
    /// true → Green (#FF4CAF50), false → Red (#FFF44336).
    /// </summary>
    public sealed class BoolToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush TrueBrush =
            new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)); // Material Green 500

        private static readonly SolidColorBrush FalseRedBrush =
            new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)); // Material Red 500

        private static readonly SolidColorBrush FalseGrayBrush =
            new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)); // Material Grey 500

        static BoolToColorConverter()
        {
            TrueBrush.Freeze();
            FalseRedBrush.Freeze();
            FalseGrayBrush.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b)
                return TrueBrush;

            return parameter is string s && s.Equals("gray", StringComparison.OrdinalIgnoreCase)
                ? FalseGrayBrush
                : FalseRedBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
