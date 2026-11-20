using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using PadForge.Models3D;
using PadForge.Views;

namespace PadForge.Controls
{
    /// <summary>What is held on a controller now, as a Home card's model shows
    /// it: the two sticks' raw axes, the two triggers' pull from 0 to 1, the
    /// held buttons, one bit for each role of ControllerModelView.ButtonRoles,
    /// and a finger on either stick's top. The card's view model hands over a
    /// new one only when the model would move or light.</summary>
    public readonly record struct StagePose(short LeftX, short LeftY, short RightX, short RightY,
        double LeftPull, double RightPull, ulong Held = 0, bool LeftTouch = false, bool RightTouch = false)
    {
        public static readonly StagePose Rest = default;
    }

    /// <summary>A Home card's controller, moving: the 3D model its still was
    /// drawn from, shown through the same piece of the drawing the still kept,
    /// so at rest it stands where the still stands. Its sticks tilt and its
    /// triggers turn as they do on the controller's page
    /// (<see cref="ControllerPose"/>), and what is held lights as it does
    /// there (<see cref="ModelGlow"/>). It takes no click and no screen
    /// reader reads it: the stage's own click opens the controller's page,
    /// and nothing here can start a recording.
    ///
    /// <para>It draws nothing on a computer that draws 3D in software, where
    /// the card keeps its still.</para></summary>
    public sealed class LiveStage : FrameworkElement
    {
        public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
            nameof(Model), typeof(LiveModel), typeof(LiveStage),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsArrange, (d, _) => ((LiveStage)d).ModelChanged()));

        public static readonly DependencyProperty PoseProperty = DependencyProperty.Register(
            nameof(Pose), typeof(StagePose), typeof(LiveStage),
            new FrameworkPropertyMetadata(StagePose.Rest, (d, _) => ((LiveStage)d).ApplyPose()));

        public static readonly DependencyProperty PictureProperty = DependencyProperty.Register(
            nameof(Picture), typeof(ImageSource), typeof(LiveStage),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsArrange));

        private readonly Viewport3D _viewport;
        private readonly ModelVisual3D _shown = new();
        private LiveModelInstance _instance;

        public LiveStage()
        {
            IsHitTestVisible = false;
            Focusable = false;
            _viewport = new Viewport3D
            {
                // A controller looks the same in every language.
                FlowDirection = FlowDirection.LeftToRight,
                IsHitTestVisible = false,
                Focusable = false,
                ClipToBounds = false,
            };
            // The still's lights (ControllerStill), which are the page's.
            _viewport.Children.Add(new HelixToolkit.Wpf.SunLight());
            var lights = new Model3DGroup();
            lights.Children.Add(new DirectionalLight(ControllerStill.HeadLight, ControllerModelFraming.Look));
            lights.Children.Add(new DirectionalLight(ControllerStill.RimLight, new Vector3D(0, -0.7, 0.7)));
            _viewport.Children.Add(new ModelVisual3D { Content = lights });
            _viewport.Children.Add(_shown);
            AddVisualChild(_viewport);
            // The model is built only where the computer draws 3D with its
            // graphics card, and a remote desktop session changes that while
            // the program runs.
            Loaded += (_, _) =>
            {
                RenderCapability.TierChanged -= OnTierChanged;
                RenderCapability.TierChanged += OnTierChanged;
                OnTierChanged(this, EventArgs.Empty);
            };
            Unloaded += (_, _) => RenderCapability.TierChanged -= OnTierChanged;
        }

        /// <summary>Builds the model, or drops it, when the tier no longer
        /// agrees with what the stage holds.</summary>
        private void OnTierChanged(object sender, EventArgs e)
        {
            if ((_instance != null) != (Model != null && DrawsIn3D)) ModelChanged();
        }

        /// <summary>Whether this computer draws 3D with its graphics card. A
        /// model drawn in software would cost the processor every move.</summary>
        internal static bool DrawsIn3D => (RenderCapability.Tier >> 16) > 0;

        /// <summary>The model the card's still was drawn from.</summary>
        public LiveModel Model
        {
            get => (LiveModel)GetValue(ModelProperty);
            set => SetValue(ModelProperty, value);
        }

        /// <summary>What is held on the controller now: the model turns and
        /// lights it.</summary>
        public StagePose Pose
        {
            get => (StagePose)GetValue(PoseProperty);
            set => SetValue(PoseProperty, value);
        }

        /// <summary>The still this stands over, for its box: the model is
        /// shown where the still is drawn.</summary>
        public ImageSource Picture
        {
            get => (ImageSource)GetValue(PictureProperty);
            set => SetValue(PictureProperty, value);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index)
            => index == 0 ? _viewport : throw new ArgumentOutOfRangeException(nameof(index));

        private void ModelChanged()
        {
            var model = Model;
            _instance = model != null && DrawsIn3D ? model.NewInstance() : null;
            if (_instance == null)
            {
                _shown.Content = null;
                _shown.Transform = null;
                return;
            }
            _viewport.Camera = new PerspectiveCamera
            {
                Position = model.Camera,
                LookDirection = ControllerModelFraming.Look,
                UpDirection = ControllerModelFraming.Up,
                FieldOfView = ControllerModelFraming.FieldOfView,
            };
            _shown.Transform = new MatrixTransform3D(model.Placement);
            _shown.Content = _instance.Root;
            ApplyPose();
        }

        private void ApplyPose() => _instance?.Pose(Pose);

        protected override Size MeasureOverride(Size availableSize)
        {
            _viewport.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return new Size(0, 0);
        }

        /// <summary>The drawing the still was cut from, shown at the size that
        /// puts the cut piece on the still's box. The model stands where the
        /// still's pixels say, and the stage shows only that box.</summary>
        protected override Size ArrangeOverride(Size finalSize)
        {
            var model = Model;
            var picture = Picture;
            Rect box = picture == null ? Rect.Empty : HeldGlow.PictureBox(finalSize, picture.Width, picture.Height);
            if (_instance == null || model == null || box.IsEmpty || model.Cut.Width <= 0 || model.Cut.Height <= 0)
            {
                _viewport.Arrange(new Rect(0, 0, 0, 0));
                Clip = null;
                return finalSize;
            }
            double scale = box.Width / model.Cut.Width;
            _viewport.Arrange(new Rect(
                box.X - model.Cut.X * scale, box.Y - model.Cut.Y * scale,
                ControllerStill.DrawWidth * scale, ControllerStill.DrawHeight * scale));
            Clip = new RectangleGeometry(box);
            return finalSize;
        }
    }
}
