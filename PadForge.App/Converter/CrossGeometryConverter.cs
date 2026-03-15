using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PadForge.Converters
{
    /// <summary>
    /// MultiValueConverter: [DeadZoneX, DeadZoneY] (0-100 each) → cross-shaped Geometry.
    /// The cross is the union of a vertical band (DZ_X wide, full height) and a
    /// horizontal band (full width, DZ_Y tall), centered on a 200×200 canvas.
    /// </summary>
    public sealed class CrossGeometryConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2) return Geometry.Empty;

            double dzX = 0, dzY = 0;
            if (values[0] is double dx) dzX = dx;
            else if (values[0] is int ix) dzX = ix;
            if (values[1] is double dy) dzY = dy;
            else if (values[1] is int iy) dzY = iy;

            double w = dzX / 100.0 * 200.0; // vertical band width
            double h = dzY / 100.0 * 200.0; // horizontal band height

            var vertical = new RectangleGeometry(new Rect(100 - w / 2, 0, w, 200));
            var horizontal = new RectangleGeometry(new Rect(0, 100 - h / 2, 200, h));

            return new CombinedGeometry(GeometryCombineMode.Union, vertical, horizontal);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
