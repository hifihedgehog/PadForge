using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PadForge.Converters
{
    /// <summary>
    /// Converts a boolean value to a <see cref="Brush"/>.
    /// true → ok green (#46C463), false → fault ember-red (#E5484D), or the
    /// themed text-3 gray (TextFillColorTertiaryBrush, resolved at convert
    /// time so both themes get their own value) with ConverterParameter=gray.
    /// </summary>
    public sealed class BoolToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush TrueBrush =
            new SolidColorBrush(Color.FromRgb(0x46, 0xC4, 0x63)); // Ember palette ok

        private static readonly SolidColorBrush FalseFaultBrush =
            new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)); // Ember palette fault

        private static readonly SolidColorBrush FalseGrayFallbackBrush =
            new SolidColorBrush(Color.FromRgb(0x5D, 0x6B, 0x85)); // dark text-3 fallback

        static BoolToColorConverter()
        {
            TrueBrush.Freeze();
            FalseFaultBrush.Freeze();
            FalseGrayFallbackBrush.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b)
                return TrueBrush;

            if (parameter is string s && s.Equals("gray", StringComparison.OrdinalIgnoreCase))
            {
                return System.Windows.Application.Current?.Resources["TextFillColorTertiaryBrush"] as Brush
                    ?? FalseGrayFallbackBrush;
            }

            return FalseFaultBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
