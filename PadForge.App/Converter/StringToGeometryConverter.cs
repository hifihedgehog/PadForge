using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PadForge.Converters
{
    /// <summary>
    /// Converts an SVG path data string to a <see cref="Geometry"/> for binding to Path.Data.
    /// </summary>
    public sealed class StringToGeometryConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string pathData && !string.IsNullOrWhiteSpace(pathData))
                return Geometry.Parse(pathData);
            return Geometry.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
