using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PadForge.Converters
{
    /// <summary>
    /// Converts a boolean value to a <see cref="SolidColorBrush"/>.
    /// true → ok green (#46C463), false → red (#FFF44336), or steel
    /// text-3 gray (#5D6B85) with ConverterParameter=gray.
    /// </summary>
    public sealed class BoolToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush TrueBrush =
            new SolidColorBrush(Color.FromRgb(0x46, 0xC4, 0x63)); // Ember palette ok

        private static readonly SolidColorBrush FalseRedBrush =
            new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)); // Material Red 500

        private static readonly SolidColorBrush FalseGrayBrush =
            new SolidColorBrush(Color.FromRgb(0x5D, 0x6B, 0x85)); // Ember palette text-3

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
