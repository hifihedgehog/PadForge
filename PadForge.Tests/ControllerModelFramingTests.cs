using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using PadForge.Models3D;
using PadForge.Views;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// The 3D controller view's default camera (#574). It stood at one
    /// point whatever the view's shape, and the wide, short picture of
    /// PadForge 6 cut the triggers off at its top with the controller
    /// sitting high. The camera backs off until the whole controller shows
    /// and stands at the height that centers it, measured from each
    /// model's own points.
    /// </summary>
    public class ControllerModelFramingTests
    {
        private readonly ITestOutputHelper _out;
        public ControllerModelFramingTests(ITestOutputHelper o) => _out = o;

        private static readonly string[] s_families =
        {
            "XBOX360", "XboxSeries", "DS3", "DS4", "DualSense", "DualSenseEdge",
            "Switch2Pro", "SteamDeck", "SteamController", "SteamController2",
        };

        // The picture of the Buttons section in a window the size the owner
        // works in (measured 2026-11-11), a smaller window's, a picture
        // near square, a square one and a tall one.
        private static readonly (double W, double H)[] s_views =
        {
            (1393, 581), (1100, 420), (900, 700), (640, 640), (500, 900),
        };

        public static IEnumerable<object[]> FamiliesAndViews()
        {
            foreach (string family in s_families)
                foreach (var (w, h) in s_views)
                    yield return new object[] { family, w, h };
        }

        /// <summary>A model's outline as the view measures it, with every
        /// point of the model after the view's placement.</summary>
        internal static (ControllerModelFraming.Outline Outline, List<Point3D> Points) Load(string family)
        {
            using var model = ControllerModelBase.Create(family, null, false);
            Matrix3D placement = ControllerModelFraming.Placement(model.model3DGroup.Bounds, model.ModelScale);
            var points = new List<Point3D>();
            Collect(model.model3DGroup, placement, points);
            return (ControllerModelFraming.Measure(model.model3DGroup, placement), points);
        }

        private static void Collect(Model3D model, Matrix3D toWorld, List<Point3D> points)
        {
            Matrix3D matrix = model.Transform.Value * toWorld;
            if (model is Model3DGroup group)
            {
                foreach (Model3D child in group.Children) Collect(child, matrix, points);
            }
            else if (model is GeometryModel3D { Geometry: MeshGeometry3D mesh })
            {
                foreach (Point3D p in mesh.Positions) points.Add(matrix.Transform(p));
            }
        }

        /// <summary>How far the model reaches toward each edge of a view
        /// from a camera at a distance and a lift: 1 is the edge itself.
        /// Left and right count as one.</summary>
        private static (double Side, double Top, double Bottom) Reach(
            List<Point3D> points, double distance, double lift, double w, double h)
        {
            double halfWidth = Math.Tan(ControllerModelFraming.FieldOfView * Math.PI / 360.0);
            double halfHeight = halfWidth * h / w;
            double side = 0, top = double.NegativeInfinity, bottom = double.NegativeInfinity;
            foreach (Point3D p in points)
            {
                var (across, up, depth) = ControllerModelFraming.ToCamera(p);
                double away = distance + depth;
                Assert.True(away > 0, "a point of the model is behind the camera");
                side = Math.Max(side, Math.Abs(across) / (away * halfWidth));
                top = Math.Max(top, (up - lift) / (away * halfHeight));
                bottom = Math.Max(bottom, (lift - up) / (away * halfHeight));
            }
            return (side, top, bottom);
        }

        /// <summary>How far toward each edge the clear room lets the model
        /// reach in a view of a height.</summary>
        private static (double Side, double Top, double Bottom) Allowed(double h)
        {
            double top = Math.Max(ControllerModelFraming.TopClear, ControllerModelFraming.ClearShare * h) / h;
            double bottom = Math.Max(ControllerModelFraming.BottomClear, ControllerModelFraming.ClearShare * h) / h;
            return (1 - 2 * ControllerModelFraming.SideShare, 1 - 2 * top, 1 - 2 * bottom);
        }

        /// <summary>In a view of any shape, the default camera shows the
        /// whole of every model with the clear room around it, stands no
        /// farther off than that takes, and has the model in the middle of
        /// the room that is left above and below it.</summary>
        [Theory]
        [MemberData(nameof(FamiliesAndViews))]
        public void TheDefaultCameraShowsTheWholeController_Centered(string family, double w, double h)
        {
            var (outline, points) = Load(family);
            Assert.NotNull(outline);
            Assert.Equal(points.Count, outline.PointCount);

            var (distance, lift) = ControllerModelFraming.Fit(outline, w, h);
            Assert.True(distance >= ControllerModelFraming.ClassicDistance - 1e-9,
                $"{family} {w}x{h}: the camera came closer than it used to");

            var reach = Reach(points, distance, lift, w, h);
            var allowed = Allowed(h);
            Assert.True(reach.Side <= allowed.Side + 1e-6, $"{family} {w}x{h}: the sides reach {reach.Side:F3} of {allowed.Side:F3}");
            Assert.True(reach.Top <= allowed.Top + 1e-6, $"{family} {w}x{h}: the top reaches {reach.Top:F3} of {allowed.Top:F3}");
            Assert.True(reach.Bottom <= allowed.Bottom + 1e-6, $"{family} {w}x{h}: the bottom reaches {reach.Bottom:F3} of {allowed.Bottom:F3}");

            // No farther than it has to be: the classic distance, the sides
            // reached, or the top and the bottom both reached, each within
            // 3 percent.
            bool classic = Math.Abs(distance - ControllerModelFraming.ClassicDistance) < 1e-9;
            bool sides = reach.Side >= 0.97 * allowed.Side;
            bool tall = reach.Top >= 0.97 * allowed.Top && reach.Bottom >= 0.97 * allowed.Bottom;
            Assert.True(classic || sides || tall,
                $"{family} {w}x{h}: the camera stands {distance:F1} off with sides {reach.Side:F3}/{allowed.Side:F3}, "
                + $"top {reach.Top:F3}/{allowed.Top:F3}, bottom {reach.Bottom:F3}/{allowed.Bottom:F3}");

            // Centered: what room is left over above and below is the same,
            // give or take what the nearer end's larger look adds.
            double over = (allowed.Top - reach.Top) * h / 2, under = (allowed.Bottom - reach.Bottom) * h / 2;
            Assert.True(Math.Abs(over - under) <= 0.25 * Math.Max(over, under) + 4,
                $"{family} {w}x{h}: {over:F1} left over above and {under:F1} below");
            _out.WriteLine($"{family} {w}x{h}: {points.Count} points, distance {distance:F1}, lift {lift:F1}, "
                + $"sides {reach.Side:F3} top {reach.Top:F3} bottom {reach.Bottom:F3}, left over {over:F0} above {under:F0} below");
        }

        /// <summary>The owner's picture (2026-11-11): an Xbox Series
        /// controller in a 1393 by 581 picture, zoomed out by hand until the
        /// whole of it showed and moved to about the middle. The camera was
        /// about 253 away there. The fixed default stood 217 away with the
        /// controller's center in the middle, and the picture's top cut the
        /// triggers off.</summary>
        [Fact]
        public void AWideShortPicture_BacksTheCameraOffAndCentersTheController()
        {
            var (outline, points) = Load("XboxSeries");
            var (distance, lift) = ControllerModelFraming.Fit(outline, 1393, 581);
            Assert.InRange(distance, 238, 262);
            Assert.True(lift > 5, $"the camera was lifted by {lift:F1}");

            var at = Reach(points, distance, lift, 1393, 581);
            double above = (1 - at.Top) / 2 * 581, below = (1 - at.Bottom) / 2 * 581;
            Assert.InRange(above, 46, 52);
            Assert.InRange(below, 50, 56);

            // And the fixed default did cut it: its top was past the edge.
            var classic = Reach(points, ControllerModelFraming.ClassicDistance, 0, 1393, 581);
            Assert.True(classic.Top > 1, $"the classic camera's top reach is {classic.Top:F3}");
        }

        /// <summary>A picture with room for the controller at the classic
        /// distance keeps that distance, since the camera only ever backs
        /// off, and still centers the controller in it.</summary>
        [Fact]
        public void APictureWithRoom_KeepsTheClassicDistance_AndCenters()
        {
            var (outline, points) = Load("XboxSeries");
            var (distance, lift) = ControllerModelFraming.Fit(outline, 900, 700);
            Assert.Equal(ControllerModelFraming.ClassicDistance, distance, 9);

            var centered = Reach(points, distance, lift, 900, 700);
            var unlifted = Reach(points, distance, 0, 900, 700);
            Assert.True(unlifted.Top - unlifted.Bottom > 0.15, "the model's center in the middle leaves the controller high");
            Assert.True(Math.Abs(centered.Top - centered.Bottom) < 0.08,
                $"lifted, the top reaches {centered.Top:F3} and the bottom {centered.Bottom:F3}");
        }

        [Fact]
        public void TheClassicCamera_IsWhereTheFixedCameraStood()
        {
            Point3D at = ControllerModelFraming.CameraAt(ControllerModelFraming.ClassicDistance, 0);
            Assert.Equal(0, at.X, 6);
            Assert.InRange(at.Y, -172.3, -171.7);
            Assert.InRange(at.Z, 131.7, 132.3);
        }

        [Fact]
        public void NoModelOrNoSizeYet_IsTheClassicCamera()
        {
            var (outline, _) = Load("XboxSeries");
            Assert.Equal((ControllerModelFraming.ClassicDistance, 0.0), ControllerModelFraming.Fit(null, 1393, 581));
            Assert.Equal((ControllerModelFraming.ClassicDistance, 0.0), ControllerModelFraming.Fit(outline, 0, 0));
            Assert.Equal((ControllerModelFraming.ClassicDistance, 0.0), ControllerModelFraming.Fit(outline, double.NaN, 581));
            Assert.Null(ControllerModelFraming.Measure(new Model3DGroup(), Matrix3D.Identity));
        }

        /// <summary>A picture too short to keep the room above and below
        /// the controller stops at the farthest distance, and never asks
        /// for an endless one.</summary>
        [Fact]
        public void AViewTooShortForTheRoom_StopsAtTheFarthestDistance()
        {
            var (outline, _) = Load("XboxSeries");
            var none = ControllerModelFraming.Fit(outline, 1393, 60);
            Assert.Equal(ControllerModelFraming.FarthestDistance, none.Distance, 9);
            Assert.False(double.IsNaN(none.Lift) || double.IsInfinity(none.Lift));

            var little = ControllerModelFraming.Fit(outline, 1393, 110);
            Assert.True(little.Distance <= ControllerModelFraming.FarthestDistance && !double.IsNaN(little.Distance));
            Assert.False(double.IsNaN(little.Lift) || double.IsInfinity(little.Lift));
        }

    }

    /// <summary>
    /// How the 3D view uses its default camera (#574). The view's markup
    /// names styles the application holds, so the view is built under a
    /// stand-in Application, which makes these tests share the collection
    /// of the others that do.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class ControllerModelViewCameraTests
    {
        private static FrameworkElement Viewport(ControllerModelView view)
            => (FrameworkElement)view.FindName("ModelViewPort");

        private static Point3D CameraPosition(ControllerModelView view)
        {
            object camera = Viewport(view).GetType().GetProperty("Camera").GetValue(Viewport(view));
            return ((PerspectiveCamera)camera).Position;
        }

        private static void Size(ControllerModelView view, double w, double h)
        {
            view.Measure(new Size(w, h));
            view.Arrange(new Rect(0, 0, w, h));
            view.UpdateLayout();
        }

        private static void Same(Point3D want, Point3D got)
            => Assert.True((want - got).Length < 1e-3, $"the camera is at {got}, not at {want}");

        /// <summary>The camera follows the view's size while it is the
        /// default one. A wheel zoom makes it the user's, which a change of
        /// size leaves alone, and Reset View gives it back.</summary>
        [Fact]
        public void TheCameraFollowsTheViewsSize_UntilTheUserMovesIt_AndResetViewGivesItBack()
        {
            CustomTelemetryFormatsDialogTests.RunWithApp(() =>
            {
                var (outline, _) = ControllerModelFramingTests.Load("XboxSeries");
                var view = new ControllerModelView();
                typeof(ControllerModelView).GetField("_modelOutline", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(view, outline);
                FrameworkElement viewport = Viewport(view);

                Point3D Default()
                {
                    var (distance, lift) = ControllerModelFraming.Fit(outline, viewport.ActualWidth, viewport.ActualHeight);
                    return ControllerModelFraming.CameraAt(distance, lift);
                }

                Size(view, 1393, 581);
                Assert.True(viewport.ActualWidth > 1000 && viewport.ActualHeight > 400, "the viewport took no size");
                Point3D wide = Default();
                Assert.True(((Vector3D)wide).Length > ControllerModelFraming.ClassicDistance + 15);
                Same(wide, CameraPosition(view));

                Size(view, 900, 700);
                Point3D roomy = Default();
                Assert.True((roomy - wide).Length > 15, "the two sizes ask for the same camera");
                Same(roomy, CameraPosition(view));

                // One notch of the wheel toward the model: 6 closer along
                // the line of sight.
                viewport.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                });
                Point3D zoomed = roomy + ControllerModelFraming.Look * 6;
                Same(zoomed, CameraPosition(view));

                Size(view, 1393, 581);
                Same(zoomed, CameraPosition(view));

                typeof(ControllerModelView).GetMethod("ResetView_Click", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(view, new object[] { view, new RoutedEventArgs() });
                Same(Default(), CameraPosition(view));

                Size(view, 900, 700);
                Same(Default(), CameraPosition(view));
            });
        }
    }
}
