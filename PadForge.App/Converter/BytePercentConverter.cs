using System;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// Shows a setting stored as a byte, 0 to 255, as a whole percent of
    /// that range, the way the Rumble board shows an adaptive trigger's
    /// range and strength (#574), and turns a percent slid or typed back
    /// into the byte. The setting keeps its 0 to 255 and its meaning. Every
    /// whole percent comes back as itself: 255 steps hold 100 with room to
    /// spare. Text that is no number leaves the setting as it was.
    /// </summary>
    public sealed class BytePercentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int percent = value is byte stored ? ToPercent(stored) : 0;
            return targetType == typeof(string) ? percent.ToString(culture) : (object)(double)percent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            switch (value)
            {
                case double slid:
                    return ToByte(slid);
                case string typed when double.TryParse(typed, NumberStyles.Float, culture, out double percent) && !double.IsNaN(percent):
                    return ToByte(percent);
                default:
                    return Binding.DoNothing;
            }
        }

        /// <summary>The byte as a whole percent of 0 to 255.</summary>
        internal static int ToPercent(byte value) => (int)Math.Round(value * 100.0 / 255);

        /// <summary>A percent, held to 0 to 100, as the nearest byte.</summary>
        internal static byte ToByte(double percent)
            => double.IsNaN(percent) ? (byte)0 : (byte)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100);
    }
}
