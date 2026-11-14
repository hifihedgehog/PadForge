using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using PadForge.Views;

namespace PadForge.Models3D
{
    /// <summary>A still picture of a controller's 3D model (#574, the
    /// world-class pass), for a Home card's stage: the model the
    /// controller's page shows, at rest, from where that page's camera
    /// stands by default and under that page's lights. The page's view
    /// frames the model with ControllerModelFraming, and so does this, so
    /// the two show the same controller the same way.
    ///
    /// <para>The model is made, drawn once to a bitmap with no window and
    /// let go. The bitmap is drawn large, with clear ground: the one who
    /// asks cuts it to the controller and makes it the size it needs,
    /// which also smooths the edges a 3D drawing to a bitmap leaves
    /// rough.</para></summary>
    internal static class ControllerStill
    {
        /// <summary>The size the model is drawn at, three times the room a
        /// stage's picture has.</summary>
        internal const int DrawWidth = 1056, DrawHeight = 696;

        /// <summary>The finish a family's model takes for a choice: the
        /// choice when the family has it, the family's first when it does
        /// not, and null for a family that lists no finish.</summary>
        internal static string FinishFor(string family, string chosen)
        {
            var (ids, _) = ControllerModelBase.Appearances(family);
            if (ids == null || ids.Length == 0) return null;
            return chosen != null && System.Array.IndexOf(ids, chosen) >= 0 ? chosen : ids[0];
        }

        /// <summary>Draws a family's model in a finish. The bitmap is
        /// frozen. Call it on a thread with a dispatcher: StagePictures
        /// draws it on a thread of its own (MakeStillOnAWorker).</summary>
        internal static BitmapSource Render(string family, string finish)
        {
            using var model = ControllerModelBase.Create(family, finish, extraControls: false);
            Rect3D bounds = model.model3DGroup.Bounds;
            Matrix3D placement = ControllerModelFraming.Placement(bounds, model.ModelScale);
            var outline = ControllerModelFraming.Measure(model.model3DGroup, placement);
            var (distance, lift) = ControllerModelFraming.Fit(outline, DrawWidth, DrawHeight);

            // A controller looks the same in every language. The viewport
            // stands in no window, so nothing above it could mirror it, and
            // it says so itself all the same.
            var viewport = new Viewport3D
            {
                FlowDirection = FlowDirection.LeftToRight,
                Width = DrawWidth,
                Height = DrawHeight,
                Camera = new PerspectiveCamera
                {
                    Position = ControllerModelFraming.CameraAt(distance, lift),
                    LookDirection = ControllerModelFraming.Look,
                    UpDirection = ControllerModelFraming.Up,
                    FieldOfView = ControllerModelFraming.FieldOfView,
                },
            };
            // The page's lights (ControllerModelView.xaml): the sun, a
            // head light at 0.35 that shines the way the camera looks, and
            // the ember light from behind and below.
            viewport.Children.Add(new HelixToolkit.Wpf.SunLight());
            var lights = new Model3DGroup();
            lights.Children.Add(new DirectionalLight(HeadLight, ControllerModelFraming.Look));
            lights.Children.Add(new DirectionalLight(RimLight, new Vector3D(0, -0.7, 0.7)));
            viewport.Children.Add(new ModelVisual3D { Content = lights });
            viewport.Children.Add(new ModelVisual3D { Content = model.model3DGroup, Transform = new MatrixTransform3D(placement) });

            viewport.Measure(new Size(DrawWidth, DrawHeight));
            viewport.Arrange(new Rect(0, 0, DrawWidth, DrawHeight));
            viewport.UpdateLayout();
            var bitmap = new RenderTargetBitmap(DrawWidth, DrawHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(viewport);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>The head light's gray: 0.35 of white, as the page's
        /// DirectionalHeadLight makes it.</summary>
        internal static readonly Color HeadLight = Color.FromRgb(89, 89, 89);

        /// <summary>The ember light's color, the page's own.</summary>
        internal static readonly Color RimLight = Color.FromRgb(0x5A, 0x32, 0x1C);
    }
}
