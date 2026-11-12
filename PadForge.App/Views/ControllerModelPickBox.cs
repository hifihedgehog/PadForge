using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PadForge.Views
{
    /// <summary>
    /// Where a control of the 3D model is drawn, for the ring on the picked
    /// control (#574): the box that the part of it in view covers.
    ///
    /// <para>A control's mesh is more than what shows. A face button has a
    /// stem inside the shell, a trigger a hinge, a bumper an end under the
    /// trigger. The box of the whole mesh stood well past the button, so the
    /// box is taken from the points nothing stands in front of: each of the
    /// control's points is held against the triangles of the rest of the
    /// model that lie near it.</para>
    ///
    /// <para>What light passes through hides nothing: the clear domes over
    /// a DualSense's and an Xbox Series pad's face buttons, the tinted
    /// shells over an Xbox 360's, and the empty plate around a decal. They
    /// are told by their material at rest (<see cref="IsSeeThrough"/>).</para>
    ///
    /// <para>A part's own transforms are left out. The view gives a part
    /// one only to tilt a stick or pull a trigger, so the box is the
    /// control's at rest and does not swim while the control moves.</para>
    /// </summary>
    internal static class ControllerModelPickBox
    {
        /// <summary>The most of a control's points held against the model.
        /// A mesh with more is thinned evenly: its points lie close enough
        /// together that the box loses less than a pixel.</summary>
        internal const int MostPoints = 800;

        /// <summary>How much nearer than a point a triangle has to be to
        /// hide it, as a share of the point's distance from the eye. It
        /// keeps a surface that touches the control, such as the rim of
        /// the hole a button sits in, from hiding the control's edge.</summary>
        internal const double DepthSlack = 0.004;

        /// <summary>How far from a control, in the model's units, the rest
        /// of the model is read for what can stand in front of it: as far
        /// as the control is long, no less than 6 and no more than 20.</summary>
        internal static double Reach(Rect3D own)
            => Math.Clamp(Math.Max(own.SizeX, Math.Max(own.SizeY, own.SizeZ)), 6, 20);

        /// <summary>Whether a box is a round control's: one about as wide as
        /// it is tall. The camera sees the model's face at a slant, so a
        /// round cap is up to a third wider than tall, and a trigger a
        /// little taller than wide. A bumper is twice as wide as tall.</summary>
        internal static bool IsRound(Rect box)
        {
            if (!(box.Width > 0) || !(box.Height > 0)) return false;
            double wide = box.Width / box.Height;
            return wide > 0.6 && wide < 1.67;
        }

        /// <summary>What the box is taken from, read once for each control
        /// of a model.</summary>
        internal sealed class Shape
        {
            /// <summary>The control's points at rest.</summary>
            internal Point3D[] Points = Array.Empty<Point3D>();

            /// <summary>The triangles of the rest of the model near the
            /// control, three points each.</summary>
            internal Point3D[] Cover = Array.Empty<Point3D>();

            // The points and the triangles on the screen, kept between
            // calls: the box is taken again for every frame that turns the
            // model.
            internal double[] At = Array.Empty<double>();
            internal double[] CoverAt = Array.Empty<double>();
        }

        /// <summary>Whether light passes through a material, so that a mesh
        /// wearing it hides nothing behind it. Its first diffuse layer
        /// decides: a brush drawn at less than full strength, a color that
        /// is not solid, or a texture that carries transparency. The
        /// models' opaque textures are JPEGs, which carry none, and their
        /// clear plastic and decals are PNGs that do.</summary>
        internal static bool IsSeeThrough(Material material)
        {
            switch (material)
            {
                case MaterialGroup layers:
                    foreach (Material layer in layers.Children)
                        if (layer is DiffuseMaterial or MaterialGroup) return IsSeeThrough(layer);
                    return false;
                case DiffuseMaterial { Brush: { } brush }:
                    if (brush.Opacity < 0.99) return true;
                    if (brush is SolidColorBrush solid) return solid.Color.A < 250;
                    return brush is ImageBrush { ImageSource: System.Windows.Media.Imaging.BitmapSource texture }
                        && CarriesTransparency(texture.Format);
                default:
                    return false;
            }
        }

        private static bool CarriesTransparency(PixelFormat format)
            => format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32
            || format == PixelFormats.Rgba64 || format == PixelFormats.Prgba64
            || format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float;

        /// <summary>Reads a control's shape from its model: the points of
        /// its parts, and the triangles of everything else within
        /// <see cref="Reach"/> of them that light does not pass through.
        /// <paramref name="atRest"/> is the model's own record of what each
        /// part wears while nothing lights it, so a clear part that is lit
        /// when the shape is read is still clear.</summary>
        internal static Shape Read(Model3D model, IReadOnlyList<Model3DGroup> parts,
            IReadOnlyDictionary<Model3DGroup, Material> atRest = null)
        {
            var shape = new Shape();
            var own = new List<Point3D>();
            foreach (var part in parts) GatherPoints(part, own);
            if (own.Count == 0) return shape;

            Rect3D near = new Rect3D(own[0], new Size3D(0, 0, 0));
            foreach (Point3D point in own) near.Union(point);
            double reach = Reach(near);
            near = new Rect3D(near.X - reach, near.Y - reach, near.Z - reach,
                near.SizeX + 2 * reach, near.SizeY + 2 * reach, near.SizeZ + 2 * reach);

            var cover = new List<Point3D>();
            GatherCover(model, parts, atRest, null, near, cover);

            shape.Points = Thin(own);
            shape.Cover = cover.ToArray();
            shape.At = new double[shape.Points.Length * 3];
            shape.CoverAt = new double[shape.Cover.Length * 3];
            return shape;
        }

        private static Point3D[] Thin(List<Point3D> points)
        {
            if (points.Count <= MostPoints) return points.ToArray();
            int step = (points.Count + MostPoints - 1) / MostPoints;
            var kept = new List<Point3D>(MostPoints);
            for (int i = 0; i < points.Count; i += step) kept.Add(points[i]);
            return kept.ToArray();
        }

        private static void GatherPoints(Model3D model, List<Point3D> points)
        {
            if (model is Model3DGroup group)
            {
                foreach (Model3D child in group.Children)
                    GatherPoints(child, points);
            }
            else if (model is GeometryModel3D { Geometry: MeshGeometry3D mesh })
            {
                points.AddRange(mesh.Positions);
            }
        }

        private static void GatherCover(Model3D model, IReadOnlyList<Model3DGroup> parts,
            IReadOnlyDictionary<Model3DGroup, Material> atRest, Material worn, Rect3D near, List<Point3D> cover)
        {
            if (model is Model3DGroup group)
            {
                // The control's own parts hide nothing of it that matters:
                // a point behind its own surface lies inside its outline.
                for (int i = 0; i < parts.Count; i++)
                    if (ReferenceEquals(parts[i], group)) return;
                // What the model says this part wears at rest goes for the
                // meshes under it.
                if (atRest != null && atRest.TryGetValue(group, out Material rest) && rest != null) worn = rest;
                foreach (Model3D child in group.Children)
                    GatherCover(child, parts, atRest, worn, near, cover);
                return;
            }
            if (model is not GeometryModel3D { Geometry: MeshGeometry3D mesh } drawn) return;
            // A mesh with no material is not drawn at all.
            Material wears = worn ?? drawn.Material;
            if (wears == null || IsSeeThrough(wears)) return;

            Point3DCollection positions = mesh.Positions;
            Int32Collection corners = mesh.TriangleIndices;
            int count = corners.Count > 0 ? corners.Count : positions.Count;
            for (int i = 0; i + 2 < count; i += 3)
            {
                int ia = i, ib = i + 1, ic = i + 2;
                if (corners.Count > 0)
                {
                    ia = corners[i]; ib = corners[i + 1]; ic = corners[i + 2];
                    if ((uint)ia >= (uint)positions.Count || (uint)ib >= (uint)positions.Count
                        || (uint)ic >= (uint)positions.Count)
                        continue;
                }
                Point3D a = positions[ia], b = positions[ib], c = positions[ic];
                if (Math.Max(a.X, Math.Max(b.X, c.X)) < near.X || Math.Min(a.X, Math.Min(b.X, c.X)) > near.X + near.SizeX
                    || Math.Max(a.Y, Math.Max(b.Y, c.Y)) < near.Y || Math.Min(a.Y, Math.Min(b.Y, c.Y)) > near.Y + near.SizeY
                    || Math.Max(a.Z, Math.Max(b.Z, c.Z)) < near.Z || Math.Min(a.Z, Math.Min(b.Z, c.Z)) > near.Z + near.SizeZ)
                    continue;
                cover.Add(a);
                cover.Add(b);
                cover.Add(c);
            }
        }

        /// <summary>The box the control covers on the screen once a matrix
        /// has put the model there: the box of its points in view, or of
        /// all its points when fewer than three are in view, so a control
        /// on the far side is still marked where it is. False for a control
        /// with no points, or with a point at or behind the eye.</summary>
        internal static bool TryGetBox(Shape shape, Matrix3D toScreen, out Rect box)
        {
            box = Rect.Empty;
            Point3D[] points = shape.Points;
            if (points.Length == 0) return false;

            // Each point as x, y on the screen and its distance from the eye.
            double[] at = shape.At;
            Rect whole = Rect.Empty;
            for (int i = 0; i < points.Length; i++)
            {
                Point4D p = toScreen.Transform(new Point4D(points[i].X, points[i].Y, points[i].Z, 1));
                if (!(p.W > 0)) return false;
                double x = p.X / p.W, y = p.Y / p.W;
                at[i * 3] = x;
                at[i * 3 + 1] = y;
                at[i * 3 + 2] = p.W;
                if (whole.IsEmpty) whole = new Rect(x, y, 0, 0);
                else whole.Union(new Point(x, y));
            }

            // The triangles that reach into the control's box. One with a
            // corner behind the eye is left out: it has no place on the
            // screen.
            Point3D[] cover = shape.Cover;
            double[] coverAt = shape.CoverAt;
            int kept = 0;
            for (int i = 0; i + 2 < cover.Length; i += 3)
            {
                Point4D a = toScreen.Transform(new Point4D(cover[i].X, cover[i].Y, cover[i].Z, 1));
                Point4D b = toScreen.Transform(new Point4D(cover[i + 1].X, cover[i + 1].Y, cover[i + 1].Z, 1));
                Point4D c = toScreen.Transform(new Point4D(cover[i + 2].X, cover[i + 2].Y, cover[i + 2].Z, 1));
                if (!(a.W > 0) || !(b.W > 0) || !(c.W > 0)) continue;
                double ax = a.X / a.W, ay = a.Y / a.W, bx = b.X / b.W, by = b.Y / b.W, cx = c.X / c.W, cy = c.Y / c.W;
                if (Math.Max(ax, Math.Max(bx, cx)) < whole.Left || Math.Min(ax, Math.Min(bx, cx)) > whole.Right
                    || Math.Max(ay, Math.Max(by, cy)) < whole.Top || Math.Min(ay, Math.Min(by, cy)) > whole.Bottom)
                    continue;
                coverAt[kept] = ax; coverAt[kept + 1] = ay; coverAt[kept + 2] = a.W;
                coverAt[kept + 3] = bx; coverAt[kept + 4] = by; coverAt[kept + 5] = b.W;
                coverAt[kept + 6] = cx; coverAt[kept + 7] = cy; coverAt[kept + 8] = c.W;
                kept += 9;
            }

            Rect seen = Rect.Empty;
            int inView = 0;
            for (int i = 0; i < points.Length; i++)
            {
                double x = at[i * 3], y = at[i * 3 + 1], far = at[i * 3 + 2];
                if (IsHidden(coverAt, kept, x, y, far * (1 - DepthSlack))) continue;
                inView++;
                if (seen.IsEmpty) seen = new Rect(x, y, 0, 0);
                else seen.Union(new Point(x, y));
            }

            box = inView >= 3 && seen.Width > 0 && seen.Height > 0 ? seen : whole;
            return box.Width > 0 && box.Height > 0;
        }

        /// <summary>Whether a triangle lies over the point and nearer than
        /// <paramref name="nearest"/>.</summary>
        private static bool IsHidden(double[] cover, int length, double x, double y, double nearest)
        {
            for (int i = 0; i < length; i += 9)
            {
                double ax = cover[i], ay = cover[i + 1], bx = cover[i + 3], by = cover[i + 4], cx = cover[i + 6], cy = cover[i + 7];
                if ((x < ax && x < bx && x < cx) || (x > ax && x > bx && x > cx)
                    || (y < ay && y < by && y < cy) || (y > ay && y > by && y > cy))
                    continue;
                double span = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                if (Math.Abs(span) < 1e-9) continue;
                double wa = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) / span;
                double wb = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) / span;
                double wc = 1 - wa - wb;
                if (wa < 0 || wb < 0 || wc < 0) continue;
                // Distance from the eye runs straight across a triangle on
                // the screen only as its inverse.
                double inverse = wa / cover[i + 2] + wb / cover[i + 5] + wc / cover[i + 8];
                if (inverse > 0 && 1 / inverse < nearest) return true;
            }
            return false;
        }
    }
}
