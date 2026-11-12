using System;
using System.Windows.Media.Media3D;

namespace PadForge.Views
{
    /// <summary>
    /// Where the 3D controller view's camera stands by default.
    ///
    /// <para>The camera looks down at the controller from in front of it,
    /// along one fixed direction. It stood at one fixed point, where a
    /// controller is about two thirds of the view's width whatever the
    /// view's shape, with the model's center in the middle of the view.
    /// PadForge 6 shows the controller in a wide, short picture (#574).
    /// There the picture's top cut the triggers off, and the controller sat
    /// high: its triggers reach farther above its center than its grips
    /// reach below.</para>
    ///
    /// <para>The camera now backs off from that distance until the whole
    /// controller is in the view with room around it, and it is raised or
    /// lowered, still looking the same way, until the controller stands in
    /// the middle of that room. It comes no closer than it used to.</para>
    ///
    /// <para>The fit is measured from the model's own points, not from its
    /// bounding box: a controller fills little of its box (the grips hang
    /// low at the front, the triggers stand high at the back), so a box
    /// would leave a different margin around each model.</para>
    /// </summary>
    internal static class ControllerModelFraming
    {
        /// <summary>The camera's field of view across the view's width, in
        /// degrees. WPF's perspective camera measures it that way.</summary>
        public const double FieldOfView = 55;

        /// <summary>The direction the camera looks in: toward the model's
        /// face, 37.5 degrees down.</summary>
        public static readonly Vector3D Look = Unit(new Vector3D(0, 0.793, -0.609));

        /// <summary>Which way is up for the camera.</summary>
        public static readonly Vector3D Up = new Vector3D(0, 0, 1);

        // The camera's own right and up, at right angles to where it looks.
        private static readonly Vector3D CameraRight = Unit(Vector3D.CrossProduct(Look, Up));
        private static readonly Vector3D CameraUp = Vector3D.CrossProduct(CameraRight, Look);

        /// <summary>How far the camera stood from the model's center before
        /// it followed the view's shape: the fixed position (0, -172, 132).
        /// It is still the nearest the default comes.</summary>
        public static readonly double ClassicDistance = Math.Sqrt(172.0 * 172.0 + 132.0 * 132.0);

        /// <summary>The share of the view's height kept clear above the
        /// controller, and below it.</summary>
        public const double ClearShare = 0.08;

        /// <summary>The least room kept clear above the controller, in the
        /// view's own units: the row of buttons that lies over the view's
        /// top, which ends 40 down, and 6 under it.</summary>
        public const double TopClear = 46;

        /// <summary>The least room kept clear below the controller: the bar
        /// that lies over the picture's foot while a recording waits for an
        /// input, 45 tall, and 5 over it.</summary>
        public const double BottomClear = 50;

        /// <summary>The share of the view's width kept clear at each side.</summary>
        public const double SideShare = 0.05;

        /// <summary>The camera never backs off farther than this, however
        /// short the view.</summary>
        public static readonly double FarthestDistance = ClassicDistance * 6;

        private const double SliceDepth = 4;
        private const int SliceCount = 128;

        /// <summary>
        /// A model's outline as the camera's direction sees it. The model is
        /// cut into slices by depth, and each slice keeps how far its points
        /// reach to either side, its highest and its lowest point, and the
        /// depth of its nearest point. A point nearer the camera looks
        /// larger, so the reach and the depth are kept together. A slice
        /// stands for all of its points by its nearest depth and its
        /// farthest reach, which is never tighter than the points
        /// themselves and is off by one slice's depth at most.
        /// </summary>
        public sealed class Outline
        {
            internal readonly double[] Near = NewArray(double.PositiveInfinity);
            internal readonly double[] Side = NewArray(0);
            internal readonly double[] Highest = NewArray(double.NegativeInfinity);
            internal readonly double[] Lowest = NewArray(double.PositiveInfinity);

            /// <summary>How many of the model's points were measured.</summary>
            public int PointCount { get; private set; }

            private static double[] NewArray(double value)
            {
                var array = new double[SliceCount];
                Array.Fill(array, value);
                return array;
            }

            internal void Take(Point3D point)
            {
                var (across, up, depth) = ToCamera(point);
                int slice = Math.Clamp((int)Math.Floor(depth / SliceDepth) + SliceCount / 2, 0, SliceCount - 1);
                if (depth < Near[slice]) Near[slice] = depth;
                if (Math.Abs(across) > Side[slice]) Side[slice] = Math.Abs(across);
                if (up > Highest[slice]) Highest[slice] = up;
                if (up < Lowest[slice]) Lowest[slice] = up;
                PointCount++;
            }
        }

        /// <summary>A point as the camera sees it from the classic line of
        /// sight: how far to the right of the model's center, how far above
        /// it, and how far beyond it. A point nearer the camera than the
        /// center has a depth under zero.</summary>
        internal static (double Across, double Up, double Depth) ToCamera(Point3D point)
        {
            var p = (Vector3D)point;
            return (Vector3D.DotProduct(p, CameraRight), Vector3D.DotProduct(p, CameraUp), Vector3D.DotProduct(p, Look));
        }

        /// <summary>How far the view lifts a model so that its vertical
        /// center is the point the model turns about. Some families are
        /// authored off-center: the DualShock 4's meshes sit 21.9 mm
        /// low.</summary>
        public static double RecenterZ(Rect3D bounds)
            => bounds.IsEmpty ? 0 : -(bounds.Z + bounds.SizeZ / 2.0);

        /// <summary>What the view does to a model before it is turned: the
        /// recentering, in the model's own units, then the model's
        /// scale.</summary>
        public static Matrix3D Placement(Rect3D bounds, double scale)
            => new TranslateTransform3D(0, 0, RecenterZ(bounds)).Value * new ScaleTransform3D(scale, scale, scale).Value;

        /// <summary>Measures a model's outline. <paramref name="placement"/>
        /// is what the view does to the model before it is turned
        /// (<see cref="Placement"/>). The model is measured as it is when
        /// this is called, so call it on a model at rest.</summary>
        public static Outline Measure(Model3D model, Matrix3D placement)
        {
            var outline = new Outline();
            Visit(model, placement, outline);
            return outline.PointCount > 0 ? outline : null;
        }

        private static void Visit(Model3D model, Matrix3D toWorld, Outline outline)
        {
            if (model == null) return;
            // A model's own transform applies before its parent's.
            Matrix3D matrix = model.Transform != null ? model.Transform.Value * toWorld : toWorld;
            if (model is Model3DGroup group)
            {
                foreach (Model3D child in group.Children)
                    Visit(child, matrix, outline);
            }
            else if (model is GeometryModel3D { Geometry: MeshGeometry3D mesh })
            {
                Point3DCollection positions = mesh.Positions;
                int count = positions.Count;
                for (int i = 0; i < count; i++)
                    outline.Take(matrix.Transform(positions[i]));
            }
        }

        /// <summary>
        /// Where the camera stands by default in a view of this size: how
        /// far from the model, and how far above the model's center the
        /// point it looks at lies (under zero is below).
        ///
        /// <para>The distance is the nearest, never under the classic one,
        /// at which every point of the model is inside the view with the
        /// clear room kept above, below and at the sides. The lift puts the
        /// model in the middle of what room is left over above and below,
        /// so a view with room to spare shows it centered too.</para>
        ///
        /// <para>A view with no size yet, or no model, gets the classic
        /// camera: the classic distance and no lift.</para>
        /// </summary>
        public static (double Distance, double Lift) Fit(Outline outline, double viewWidth, double viewHeight)
        {
            if (outline == null || !(viewWidth > 0) || !(viewHeight > 0)) return (ClassicDistance, 0);

            // Half the view, as a share of the distance to a point: across
            // the width by the field of view, and up the height by the
            // view's shape.
            double halfWidth = Math.Tan(FieldOfView * Math.PI / 360.0);
            double halfHeight = halfWidth * viewHeight / viewWidth;

            double top = Math.Max(TopClear, ClearShare * viewHeight) / viewHeight;
            double bottom = Math.Max(BottomClear, ClearShare * viewHeight) / viewHeight;
            double sideRoom = halfWidth * (1 - 2 * SideShare);
            double topRoom = halfHeight * (1 - 2 * top);
            double bottomRoom = halfHeight * (1 - 2 * bottom);

            // A point at height h and depth z, seen from a camera that
            // stands d off and is lifted by c, is under the top of the room
            // when h - c <= (d + z) * topRoom, and over its bottom when
            // c - h <= (d + z) * bottomRoom. So the lift has to lie between
            // over - d * topRoom and under + d * bottomRoom, with:
            double over = double.NegativeInfinity;   // the most any slice asks the lift to be at least
            double under = double.PositiveInfinity;  // the least any slice lets the lift be at most
            double sides = 0;
            for (int i = 0; i < SliceCount; i++)
            {
                double near = outline.Near[i];
                if (double.IsPositiveInfinity(near)) continue;
                over = Math.Max(over, outline.Highest[i] - near * topRoom);
                under = Math.Min(under, outline.Lowest[i] + near * bottomRoom);
                sides = Math.Max(sides, outline.Side[i] / sideRoom - near);
            }

            double distance = Math.Max(ClassicDistance, sides);
            // The two limits meet at one distance, which is the nearest the
            // model fits between them. A view too short for the room above
            // and below has no such distance.
            double room = topRoom + bottomRoom;
            distance = room > 0 ? Math.Max(distance, (over - under) / room) : FarthestDistance;
            distance = Math.Min(distance, FarthestDistance);

            double lift = ((over - distance * topRoom) + (under + distance * bottomRoom)) / 2;
            return (distance, lift);
        }

        /// <summary>The camera's position for a distance and a lift: back
        /// along its line of sight from the point it looks at, which lies
        /// the lift above the model's center, the origin.</summary>
        public static Point3D CameraAt(double distance, double lift)
            => (Point3D)(CameraUp * lift - Look * distance);

        private static Vector3D Unit(Vector3D vector)
        {
            vector.Normalize();
            return vector;
        }
    }
}
