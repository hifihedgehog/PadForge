using System;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>Formats a pair of 0..1 normalized axis values as the Ember
    /// instrument readout: signed, two decimals, space-separated
    /// ("+0.07 +0.98"), matching the artifact radar readouts (#175).</summary>
    public sealed class NormPairToSignedTextConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not double x || values[1] is not double y)
                return "+0.00 +0.00";
            return Fmt(x * 2.0 - 1.0) + " " + Fmt(y * 2.0 - 1.0);
        }

        private static string Fmt(double v) =>
            (v < 0 ? "−" : "+") + Math.Abs(v).ToString("0.00", CultureInfo.InvariantCulture);

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
