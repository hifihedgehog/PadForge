using System;
using System.Windows;
using System.Windows.Media;
using PadForge.Services;

namespace PadForge.Common.Crosshairs
{
    /// <summary>
    /// A crosshair's shapes in pixels around the point it marks, which is
    /// (0, 0), and the room they take (#585). The overlay window and the
    /// preview on the crosshair's card draw the same shape.
    ///
    /// <para>A Cross is the CS:GO classic crosshair as the CS:GO crosshair
    /// generator draws it (Skarbo/CSGOCrosshair, javascript.js
    /// updateCrosshair): each arm is a thickness wide and a length long,
    /// begins half its thickness plus the gap from the center, and the dot is
    /// a thickness square at the center. A Circle is Crosshair-Overlay's
    /// polygon (Drawing.cpp STYLE_POLYGON), its sides on a radius, the first
    /// vertex straight above the center, round at the most sides. An outline is
    /// the shape grown by its thickness on every side and drawn under it, as
    /// Crosshair-Overlay's outlined styles draw their larger quads first. The
    /// rotation turns everything about the center (config_t flRotation).</para>
    /// </summary>
    internal sealed class CrosshairShape
    {
        /// <summary>The filled shape: arms, ring and dot. Null for a picture.</summary>
        public Geometry Body { get; private init; }

        /// <summary>The outline pen's width: twice the outline thickness, as the
        /// pen is centered on the shape's edge and the fill covers its inner
        /// half. Zero with no outline.</summary>
        public double OutlinePenWidth { get; private init; }

        /// <summary>The picture's rectangle about the center (Image).</summary>
        public Rect ImageRect { get; private init; }

        /// <summary>The rotation in degrees, clockwise.</summary>
        public double Rotation { get; private init; }

        /// <summary>Everything drawn, the outline and the rotation included,
        /// about the center.</summary>
        public Rect Bounds { get; private init; }

        public bool IsImage => Body == null;

        public static CrosshairShape Of(CrosshairData d)
        {
            var rotate = new RotateTransform(d.Rotation);
            rotate.Freeze();
            if (d.Style == CrosshairStyle.Image)
            {
                var rect = new Rect(-d.ImageWidth / 2, -d.ImageHeight / 2, d.ImageWidth, d.ImageHeight);
                var outer = new RectangleGeometry(rect) { Transform = rotate };
                outer.Freeze();
                return new CrosshairShape { ImageRect = rect, Rotation = d.Rotation, Bounds = outer.Bounds };
            }

            Geometry body = d.Style switch
            {
                CrosshairStyle.Circle => Ring(d),
                CrosshairStyle.Dot => new EllipseGeometry(new Point(0, 0), d.DotSize / 2, d.DotSize / 2),
                _ => Cross(d),
            };
            body.Transform = rotate;
            body.Freeze();
            double pen = d.Outline ? d.OutlineThickness * 2 : 0;
            Rect bounds = pen > 0 ? body.GetRenderBounds(OutlinePen(pen)) : body.Bounds;
            if (bounds.IsEmpty) bounds = new Rect(0, 0, 0, 0);
            return new CrosshairShape { Body = body, OutlinePenWidth = pen, Rotation = d.Rotation, Bounds = bounds };
        }

        /// <summary>The pen an outline is drawn with: square corners, as
        /// Crosshair-Overlay's larger quads have.</summary>
        public static Pen OutlinePen(double width, Brush brush = null)
        {
            var pen = new Pen(brush ?? Brushes.Black, width) { LineJoin = PenLineJoin.Miter, MiterLimit = 2 };
            pen.Freeze();
            return pen;
        }

        private static Geometry Cross(CrosshairData d)
        {
            double t = d.Thickness, length = d.Length, start = t / 2 + d.Gap;
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            if (length > 0)
            {
                group.Children.Add(new RectangleGeometry(new Rect(start, -t / 2, length, t)));
                group.Children.Add(new RectangleGeometry(new Rect(-start - length, -t / 2, length, t)));
                group.Children.Add(new RectangleGeometry(new Rect(-t / 2, start, t, length)));
                if (!d.TShape) group.Children.Add(new RectangleGeometry(new Rect(-t / 2, -start - length, t, length)));
            }
            if (d.CenterDot) group.Children.Add(new RectangleGeometry(new Rect(-t / 2, -t / 2, t, t)));
            return group;
        }

        private static Geometry Ring(CrosshairData d)
        {
            double r = d.Radius;
            Geometry line;
            if (d.Sides >= CrosshairData.MaxSides) line = new EllipseGeometry(new Point(0, 0), r, r);
            else
            {
                var figure = new PathFigure { IsClosed = true, IsFilled = false };
                for (int i = 0; i < d.Sides; i++)
                {
                    double a = (-90 + i * 360.0 / d.Sides) * Math.PI / 180;
                    var p = new Point(r * Math.Cos(a), r * Math.Sin(a));
                    if (i == 0) figure.StartPoint = p;
                    else figure.Segments.Add(new LineSegment(p, true));
                }
                line = new PathGeometry(new[] { figure });
            }
            var ring = line.GetWidenedPathGeometry(new Pen(Brushes.Black, d.Thickness) { LineJoin = PenLineJoin.Miter });
            if (!d.CenterDot) return ring;
            double dot = Math.Max(2, d.Thickness);
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            group.Children.Add(ring);
            group.Children.Add(new EllipseGeometry(new Point(0, 0), dot / 2, dot / 2));
            return group;
        }
    }
}
