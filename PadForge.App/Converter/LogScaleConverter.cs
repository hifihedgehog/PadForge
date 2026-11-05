using System;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// Puts a whole number between 1 and a maximum on a 0 to 100 slider by
    /// its logarithm, so each step of the slider changes the number by the
    /// same ratio. A linear slider over 1 to 600000 on a track a few hundred
    /// pixels wide moves the number by thousands per pixel, and no pixel
    /// lands near 3000. By ratio, the same slider moves a duration usefully
    /// at 50 ms and at five minutes. The ConverterParameter is the maximum.
    /// The exact number stays in the text box beside the slider.
    /// </summary>
    public sealed class LogScaleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double max = Maximum(parameter);
            double number = value switch { int i => i, double d => d, _ => 1 };
            return 100 * Math.Log(Math.Clamp(number, 1, max)) / Math.Log(max);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double max = Maximum(parameter);
            double position = value is double d ? Math.Clamp(d, 0, 100) : 0;
            return (int)Math.Round(Math.Pow(max, position / 100));
        }

        private static double Maximum(object parameter)
            => System.Convert.ToDouble(parameter, CultureInfo.InvariantCulture);
    }
}
