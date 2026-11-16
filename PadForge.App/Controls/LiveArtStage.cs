using System;
using System.Windows;
using System.Windows.Media;
using PadForge.Models2D;
using PadForge.Views;

namespace PadForge.Controls
{
    /// <summary>A Home card's controller drawn in 2D, moving: the layers its
    /// picture was made of (<see cref="LiveArt"/>), drawn where the picture
    /// stands, with each stick's art shifted, each trigger's art filled and
    /// each held button's art shown as the controller page's 2D view shows
    /// them (ControllerModel2DView.UpdateButtons, UpdateTriggers,
    /// UpdateSticks). It takes no click and no screen reader reads it: the
    /// stage's own click opens the controller's page.</summary>
    public sealed class LiveArtStage : FrameworkElement
    {
        public static readonly DependencyProperty ArtProperty = DependencyProperty.Register(
            nameof(Art), typeof(LiveArt), typeof(LiveArtStage),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PoseProperty = DependencyProperty.Register(
            nameof(Pose), typeof(StagePose), typeof(LiveArtStage),
            new FrameworkPropertyMetadata(StagePose.Rest, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PictureProperty = DependencyProperty.Register(
            nameof(Picture), typeof(ImageSource), typeof(LiveArtStage),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public LiveArtStage()
        {
            IsHitTestVisible = false;
            Focusable = false;
            // A controller looks the same in every language.
            FlowDirection = FlowDirection.LeftToRight;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        }

        /// <summary>The layers of the card's picture.</summary>
        public LiveArt Art
        {
            get => (LiveArt)GetValue(ArtProperty);
            set => SetValue(ArtProperty, value);
        }

        /// <summary>What is held on the controller now.</summary>
        public StagePose Pose
        {
            get => (StagePose)GetValue(PoseProperty);
            set => SetValue(PoseProperty, value);
        }

        /// <summary>The picture this stands over, for its box: the layers are
        /// drawn where the picture is drawn.</summary>
        public ImageSource Picture
        {
            get => (ImageSource)GetValue(PictureProperty);
            set => SetValue(PictureProperty, value);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var art = Art;
            var picture = Picture;
            if (art == null || picture == null || art.Width <= 0 || art.Height <= 0) return;
            Rect box = HeldGlow.PictureBox(RenderSize, picture.Width, picture.Height);
            if (box.IsEmpty || box.Width <= 0 || box.Height <= 0) return;
            double across = box.Width / art.Width, down = box.Height / art.Height;
            Rect On(Rect place, double x = 0, double y = 0)
                => new(box.X + (place.X + x) * across, box.Y + (place.Y + y) * down, place.Width * across, place.Height * down);

            var pose = Pose;
            bool Held(int bit) => bit >= 0 && (pose.Held & (1UL << bit)) != 0;
            dc.PushClip(new RectangleGeometry(box));
            foreach (var layer in art.Layers)
            {
                switch (layer.Kind)
                {
                    case LiveArt.Kind.Still:
                        dc.DrawImage(layer.Picture, On(layer.Place));
                        break;
                    case LiveArt.Kind.Stick:
                    {
                        // Up on the stick is up on the picture.
                        double x = (layer.Left ? pose.LeftX : pose.RightX) / 32767.0 * art.StickTravel;
                        double y = -(layer.Left ? pose.LeftY : pose.RightY) / 32767.0 * art.StickTravel;
                        dc.DrawImage(layer.Picture, On(layer.Place, x, y));
                        break;
                    }
                    case LiveArt.Kind.Trigger:
                    {
                        // Filled from the bottom by the pull.
                        double pull = Math.Clamp(layer.Left ? pose.LeftPull : pose.RightPull, 0.0, 1.0);
                        if (!(pull > 0)) break;
                        Rect at = On(layer.Place);
                        dc.PushClip(new RectangleGeometry(new Rect(at.X, at.Y + at.Height * (1 - pull), at.Width, at.Height * pull)));
                        dc.DrawImage(layer.Picture, at);
                        dc.Pop();
                        break;
                    }
                    case LiveArt.Kind.Button:
                        if (Held(layer.Bit)) dc.DrawImage(layer.Picture, On(layer.Place));
                        break;
                    case LiveArt.Kind.StickClick:
                        if (Held(layer.Bit))
                            dc.DrawImage(layer.Picture, On(layer.Place));
                        else if (layer.Left ? pose.LeftTouch : pose.RightTouch)
                        {
                            dc.PushOpacity(ControllerModel2DView.StickTouchOpacity);
                            dc.DrawImage(layer.Picture, On(layer.Place));
                            dc.Pop();
                        }
                        break;
                }
            }
            dc.Pop();
        }
    }
}
