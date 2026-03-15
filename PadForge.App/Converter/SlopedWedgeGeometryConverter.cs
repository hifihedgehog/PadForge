using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PadForge.Converters
{
    /// <summary>
    /// MultiValueConverter: [DeadZoneX, DeadZoneY] (0-100 each) → 4 triangular wedge Geometry.
    /// Sloped DZ zeroes X when |x| &lt; dzX*|y| (wedges along Y axis) and
    /// Y when |y| &lt; dzY*|x| (wedges along X axis).
    /// Each wedge is a triangle from center to the canvas edge.
    /// </summary>
    public sealed class SlopedWedgeGeometryConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2) return Geometry.Empty;

            double dzX = 0, dzY = 0;
            if (values[0] is double dx) dzX = dx;
            else if (values[0] is int ix) dzX = ix;
            if (values[1] is double dy) dzY = dy;
            else if (values[1] is int iy) dzY = iy;

            double dzXn = dzX / 100.0; // half-width of wedge at edge
            double dzYn = dzY / 100.0; // half-height of wedge at edge

            const double c = 100.0; // center
            const double size = 200.0;

            var geo = new PathGeometry();

            // Upper wedge (X zeroed near +Y axis): center → top-left → top-right
            AddTriangle(geo, c, c,
                c - dzXn * c, 0,
                c + dzXn * c, 0);

            // Lower wedge (X zeroed near -Y axis): center → bottom-left → bottom-right
            AddTriangle(geo, c, c,
                c - dzXn * c, size,
                c + dzXn * c, size);

            // Right wedge (Y zeroed near +X axis): center → top-right → bottom-right
            AddTriangle(geo, c, c,
                size, c - dzYn * c,
                size, c + dzYn * c);

            // Left wedge (Y zeroed near -X axis): center → top-left → bottom-left
            AddTriangle(geo, c, c,
                0, c - dzYn * c,
                0, c + dzYn * c);

            return geo;
        }

        private static void AddTriangle(PathGeometry geo,
            double cx, double cy, double x1, double y1, double x2, double y2)
        {
            var fig = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = true, IsFilled = true };
            fig.Segments.Add(new LineSegment(new Point(x1, y1), true));
            fig.Segments.Add(new LineSegment(new Point(x2, y2), true));
            geo.Figures.Add(fig);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
