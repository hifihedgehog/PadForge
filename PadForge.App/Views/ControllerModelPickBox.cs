using System;
using System.Buffers;
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
    ///
    /// <para>The box is taken again for every frame that turns the model.
    /// Each point of a mesh near the control is put on the screen once, and
    /// the control's box is cut into cells that list the triangles over
    /// them, so a point is held against the triangles of its own cell.
    /// Measured 2026-11-13 over every control of the ten models, each from
    /// seven sides: 0.33 ms a call on average and 4.6 ms at worst. Holding
    /// every point against every triangle took 2.8 ms and 118 ms, and gave
    /// the same boxes.</para>
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

            /// <summary>The corners of the triangles of the rest of the
            /// model near the control. A point that several triangles of
            /// its mesh name is kept once, so it is put on the screen once
            /// for a frame and not once for each of them.</summary>
            internal Point3D[] CoverPoints = Array.Empty<Point3D>();

            /// <summary>The triangles: three places in
            /// <see cref="CoverPoints"/> each.</summary>
            internal int[] CoverCorners = Array.Empty<int>();

            /// <summary>The triangles as three points each, as a test reads
            /// them.</summary>
            internal Point3D[] Cover => Array.ConvertAll(CoverCorners, corner => CoverPoints[corner]);

            // The points and the corners on the screen, the triangles that
            // reach into the control's box and the cells of the box they lie
            // over, kept between calls: the box is taken again for every
            // frame that turns the model.
            internal double[] At = Array.Empty<double>();
            internal double[] CoverAt = Array.Empty<double>();
            internal int[] Kept = Array.Empty<int>();
            internal int[] CellStart = Array.Empty<int>();
            internal int[] CellItems = Array.Empty<int>();
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

            var corners = new List<Point3D>();
            var cornerOf = new List<int>();
            GatherCover(model, parts, atRest, null, near, corners, cornerOf);

            shape.Points = Thin(own);
            shape.CoverPoints = corners.ToArray();
            shape.CoverCorners = cornerOf.ToArray();
            shape.At = new double[shape.Points.Length * 3];
            shape.CoverAt = new double[shape.CoverPoints.Length * 3];
            shape.Kept = new int[shape.CoverCorners.Length / 3];
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
            IReadOnlyDictionary<Model3DGroup, Material> atRest, Material worn, Rect3D near,
            List<Point3D> corners, List<int> cornerOf)
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
                    GatherCover(child, parts, atRest, worn, near, corners, cornerOf);
                return;
            }
            if (model is not GeometryModel3D { Geometry: MeshGeometry3D mesh } drawn) return;
            // A mesh with no material is not drawn at all.
            Material wears = worn ?? drawn.Material;
            if (wears == null || IsSeeThrough(wears)) return;

            Point3DCollection positions = mesh.Positions;
            Int32Collection numbers = mesh.TriangleIndices;
            int points = positions.Count, count = numbers.Count > 0 ? numbers.Count : points;
            if (count < 3) return;

            // The mesh's lists are copied once and read from the copies. A
            // read of one entry of a mesh's own list checks the calling
            // thread, and a model has several hundred thousand entries.
            Point3D[] at = ArrayPool<Point3D>.Shared.Rent(points);
            positions.CopyTo(at, 0);
            int[] number = null;
            if (numbers.Count > 0)
            {
                number = ArrayPool<int>.Shared.Rent(count);
                numbers.CopyTo(number, 0);
            }
            // Where each of the mesh's points stands among the corners, plus
            // one. Zero is a point no triangle near the control has named.
            int[] place = null;
            for (int i = 0; i + 2 < count; i += 3)
            {
                int ia = i, ib = i + 1, ic = i + 2;
                if (number != null)
                {
                    ia = number[i]; ib = number[i + 1]; ic = number[i + 2];
                    if ((uint)ia >= (uint)points || (uint)ib >= (uint)points || (uint)ic >= (uint)points)
                        continue;
                }
                Point3D a = at[ia], b = at[ib], c = at[ic];
                if (Math.Max(a.X, Math.Max(b.X, c.X)) < near.X || Math.Min(a.X, Math.Min(b.X, c.X)) > near.X + near.SizeX
                    || Math.Max(a.Y, Math.Max(b.Y, c.Y)) < near.Y || Math.Min(a.Y, Math.Min(b.Y, c.Y)) > near.Y + near.SizeY
                    || Math.Max(a.Z, Math.Max(b.Z, c.Z)) < near.Z || Math.Min(a.Z, Math.Min(b.Z, c.Z)) > near.Z + near.SizeZ)
                    continue;
                if (place == null)
                {
                    place = ArrayPool<int>.Shared.Rent(points);
                    Array.Clear(place, 0, points);
                }
                cornerOf.Add(PlaceOf(ia, at, place, corners));
                cornerOf.Add(PlaceOf(ib, at, place, corners));
                cornerOf.Add(PlaceOf(ic, at, place, corners));
            }
            ArrayPool<Point3D>.Shared.Return(at);
            if (number != null) ArrayPool<int>.Shared.Return(number);
            if (place != null) ArrayPool<int>.Shared.Return(place);
        }

        private static int PlaceOf(int point, Point3D[] at, int[] place, List<Point3D> corners)
        {
            if (place[point] == 0)
            {
                corners.Add(at[point]);
                place[point] = corners.Count;
            }
            return place[point] - 1;
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

            // Each corner of the cover on the screen, once. One at or behind
            // the eye has no place there, and a distance of zero marks it.
            Point3D[] corners = shape.CoverPoints;
            double[] cornerAt = shape.CoverAt;
            for (int i = 0; i < corners.Length; i++)
            {
                Point4D p = toScreen.Transform(new Point4D(corners[i].X, corners[i].Y, corners[i].Z, 1));
                if (p.W > 0)
                {
                    cornerAt[i * 3] = p.X / p.W;
                    cornerAt[i * 3 + 1] = p.Y / p.W;
                    cornerAt[i * 3 + 2] = p.W;
                }
                else cornerAt[i * 3 + 2] = 0;
            }

            // The triangles that reach into the control's box. One with a
            // corner behind the eye is left out: it has no place on the
            // screen.
            int[] cornerOf = shape.CoverCorners;
            int[] kept = shape.Kept;
            int count = 0;
            for (int t = 0; t + 2 < cornerOf.Length; t += 3)
            {
                int a = cornerOf[t] * 3, b = cornerOf[t + 1] * 3, c = cornerOf[t + 2] * 3;
                if (!(cornerAt[a + 2] > 0) || !(cornerAt[b + 2] > 0) || !(cornerAt[c + 2] > 0)) continue;
                double ax = cornerAt[a], ay = cornerAt[a + 1], bx = cornerAt[b], by = cornerAt[b + 1], cx = cornerAt[c], cy = cornerAt[c + 1];
                if (Math.Max(ax, Math.Max(bx, cx)) < whole.Left || Math.Min(ax, Math.Min(bx, cx)) > whole.Right
                    || Math.Max(ay, Math.Max(by, cy)) < whole.Top || Math.Min(ay, Math.Min(by, cy)) > whole.Bottom)
                    continue;
                kept[count++] = t;
            }

            // The box is cut into cells, and each kept triangle is listed
            // under the cells its own box lies over, so a point is held
            // against the triangles of its own cell and no others. A triangle
            // that can hide a point lies over the point, so it is listed
            // under the point's cell. Few triangles need no cells.
            int cells = CellsFor(count, whole);
            int[] start = shape.CellStart, items = shape.CellItems;
            if (cells > 0)
            {
                int total = cells * cells;
                if (start.Length < total) shape.CellStart = start = new int[total];
                Array.Clear(start, 0, total);
                for (int k = 0; k < count; k++)
                {
                    CellsOf(cornerAt, cornerOf, kept[k], whole, cells, out int x0, out int x1, out int y0, out int y1);
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                            start[y * cells + x]++;
                }
                // A count becomes where the cell's list begins, and the fill
                // moves it to where the list ends, which is where the next
                // cell's begins.
                int listed = 0;
                for (int i = 0; i < total; i++)
                {
                    int n = start[i];
                    start[i] = listed;
                    listed += n;
                }
                if (items.Length < listed) shape.CellItems = items = new int[listed + (listed >> 1)];
                for (int k = 0; k < count; k++)
                {
                    CellsOf(cornerAt, cornerOf, kept[k], whole, cells, out int x0, out int x1, out int y0, out int y1);
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                            items[start[y * cells + x]++] = kept[k];
                }
            }

            Rect seen = Rect.Empty;
            int inView = 0;
            for (int i = 0; i < points.Length; i++)
            {
                double x = at[i * 3], y = at[i * 3 + 1], far = at[i * 3 + 2];
                bool hidden;
                if (cells == 0)
                    hidden = IsHidden(cornerAt, cornerOf, kept, 0, count, x, y, far * (1 - DepthSlack));
                else
                {
                    int cell = Cell(y, whole.Top, whole.Height, cells) * cells + Cell(x, whole.Left, whole.Width, cells);
                    hidden = IsHidden(cornerAt, cornerOf, items, cell == 0 ? 0 : start[cell - 1], start[cell],
                        x, y, far * (1 - DepthSlack));
                }
                if (hidden) continue;
                inView++;
                if (seen.IsEmpty) seen = new Rect(x, y, 0, 0);
                else seen.Union(new Point(x, y));
            }

            box = inView >= 3 && seen.Width > 0 && seen.Height > 0 ? seen : whole;
            return box.Width > 0 && box.Height > 0;
        }

        /// <summary>How many cells across and down the control's box is cut
        /// into for this many triangles: none for a few, or for a box with no
        /// width or no height, and otherwise about four triangles to a cell,
        /// from 4 to 32 a side.</summary>
        internal static int CellsFor(int triangles, Rect whole)
            => triangles < 64 || !(whole.Width > 0) || !(whole.Height > 0)
                ? 0
                : Math.Clamp((int)Math.Sqrt(triangles / 4.0), 4, 32);

        /// <summary>The cell a place along the box falls in. The same rule
        /// gives a point its cell and a triangle the cells from its least
        /// place to its greatest, and it never goes down as the place goes
        /// up, so a triangle that spans a point is listed under the point's
        /// cell. A place is held to the cells before it is made a whole
        /// number: a corner just in front of the eye lands far off the
        /// screen, past what a whole number holds.</summary>
        private static int Cell(double place, double from, double size, int cells)
        {
            double cell = (place - from) * cells / size;
            return cell >= cells ? cells - 1 : cell > 0 ? (int)cell : 0;
        }

        private static void CellsOf(double[] cornerAt, int[] cornerOf, int triangle, Rect whole, int cells,
            out int x0, out int x1, out int y0, out int y1)
        {
            int a = cornerOf[triangle] * 3, b = cornerOf[triangle + 1] * 3, c = cornerOf[triangle + 2] * 3;
            double ax = cornerAt[a], ay = cornerAt[a + 1], bx = cornerAt[b], by = cornerAt[b + 1], cx = cornerAt[c], cy = cornerAt[c + 1];
            x0 = Cell(Math.Min(ax, Math.Min(bx, cx)), whole.Left, whole.Width, cells);
            x1 = Cell(Math.Max(ax, Math.Max(bx, cx)), whole.Left, whole.Width, cells);
            y0 = Cell(Math.Min(ay, Math.Min(by, cy)), whole.Top, whole.Height, cells);
            y1 = Cell(Math.Max(ay, Math.Max(by, cy)), whole.Top, whole.Height, cells);
            // A corner that is no number gives no order to its box. Such a
            // triangle hides nothing, and one cell holds it.
            if (x1 < x0) x1 = x0;
            if (y1 < y0) y1 = y0;
        }

        /// <summary>Whether one of these triangles lies over the point and
        /// nearer than <paramref name="nearest"/>. <paramref name="list"/>
        /// names the triangles from <paramref name="from"/> up to
        /// <paramref name="to"/>, each by the place of its first corner.</summary>
        private static bool IsHidden(double[] cornerAt, int[] cornerOf, int[] list, int from, int to,
            double x, double y, double nearest)
        {
            for (int k = from; k < to; k++)
            {
                int t = list[k];
                int a = cornerOf[t] * 3, b = cornerOf[t + 1] * 3, c = cornerOf[t + 2] * 3;
                double ax = cornerAt[a], ay = cornerAt[a + 1], bx = cornerAt[b], by = cornerAt[b + 1], cx = cornerAt[c], cy = cornerAt[c + 1];
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
                double inverse = wa / cornerAt[a + 2] + wb / cornerAt[b + 2] + wc / cornerAt[c + 2];
                if (inverse > 0 && 1 / inverse < nearest) return true;
            }
            return false;
        }
    }
}
