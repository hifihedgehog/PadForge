using System;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// Converts a boolean to an opacity value.
    /// true = 1.0 (fully visible), false = 0.2 (dimmed).
    /// Optional ConverterParameter overrides the "false" opacity (e.g., "0" for fully hidden).
    /// </summary>
    public sealed class BoolToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool pressed = value is bool b && b;
            double dimOpacity = 0.2;

            if (parameter is string s && double.TryParse(s,
                NumberStyles.Any, CultureInfo.InvariantCulture, out double custom))
            {
                dimOpacity = custom;
            }

            return pressed ? 1.0 : dimOpacity;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
