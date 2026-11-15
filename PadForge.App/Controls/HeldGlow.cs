using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using PadForge.Common;

namespace PadForge.Controls
{
    /// <summary>The light on a Home card's picture (the approved "keep the
    /// frame" canvas): an ember glow over each control that is held on the
    /// controller now, as far lit as the control is held. It lies over the
    /// picture and takes the picture's own box, so a glow stands on its
    /// control at any size the card shows the picture at. It takes no
    /// click and no screen reader reads it: the picture says nothing the
    /// controller's page does not.</summary>
    public sealed class HeldGlow : FrameworkElement
    {
        public static readonly DependencyProperty MarksProperty = DependencyProperty.Register(
            nameof(Marks), typeof(IReadOnlyList<StillMark>), typeof(HeldGlow),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LevelsProperty = DependencyProperty.Register(
            nameof(Levels), typeof(byte[]), typeof(HeldGlow),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PictureProperty = DependencyProperty.Register(
            nameof(Picture), typeof(ImageSource), typeof(HeldGlow),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public HeldGlow()
        {
            IsHitTestVisible = false;
            Focusable = false;
        }

        /// <summary>Where each control is drawn, in parts of the picture's
        /// width and height.</summary>
        public IReadOnlyList<StillMark> Marks
        {
            get => (IReadOnlyList<StillMark>)GetValue(MarksProperty);
            set => SetValue(MarksProperty, value);
        }

        /// <summary>How far each control is held, in the marks' order and
        /// in steps (StillMarks.Steps).</summary>
        public byte[] Levels
        {
            get => (byte[])GetValue(LevelsProperty);
            set => SetValue(LevelsProperty, value);
        }

        /// <summary>The picture this lies over, for its shape.</summary>
        public ImageSource Picture
        {
            get => (ImageSource)GetValue(PictureProperty);
            set => SetValue(PictureProperty, value);
        }

        /// <summary>The box a picture takes in a room it is fit into whole
        /// and centered, as an Image with Stretch Uniform draws it. Empty
        /// when either has no size.</summary>
        internal static Rect PictureBox(Size room, double pictureWidth, double pictureHeight)
        {
            if (!(room.Width > 0) || !(room.Height > 0) || !(pictureWidth > 0) || !(pictureHeight > 0)) return Rect.Empty;
            double scale = Math.Min(room.Width / pictureWidth, room.Height / pictureHeight);
            double width = pictureWidth * scale, height = pictureHeight * scale;
            return new Rect((room.Width - width) / 2, (room.Height - height) / 2, width, height);
        }

        /// <summary>How far a glow reaches past its control on each side,
        /// as a part of the picture's width: 8 on a detailed card's
        /// picture, the reach the canvas drew.</summary>
        internal const double Spill = 0.044;

        /// <summary>The oval a mark's glow fills in a picture's box: the
        /// control's own box and the spill around it.</summary>
        internal static Rect GlowBox(Rect picture, Rect mark)
        {
            double spill = picture.Width * Spill;
            return new Rect(
                picture.X + mark.X * picture.Width - spill,
                picture.Y + mark.Y * picture.Height - spill,
                Math.Max(0, mark.Width * picture.Width) + 2 * spill,
                Math.Max(0, mark.Height * picture.Height) + 2 * spill);
        }

        protected override void OnRender(DrawingContext drawing)
        {
            var marks = Marks;
            var levels = Levels;
            var picture = Picture;
            if (marks == null || levels == null || picture == null || levels.Length != marks.Count) return;
            Rect box = PictureBox(RenderSize, picture.Width, picture.Height);
            if (box.IsEmpty) return;
            for (int i = 0; i < levels.Length; i++)
            {
                if (levels[i] == 0) continue;
                Rect glow = GlowBox(box, marks[i].Box);
                drawing.PushOpacity(Math.Min(1.0, levels[i] / (double)StillMarks.Steps));
                drawing.DrawEllipse(s_glow, null,
                    new Point(glow.X + glow.Width / 2, glow.Y + glow.Height / 2), glow.Width / 2, glow.Height / 2);
                drawing.Pop();
            }
        }

        /// <summary>The glow: a pale ember heart, ember around it, and
        /// nothing at its edge.</summary>
        private static readonly Brush s_glow = MakeGlow();

        private static Brush MakeGlow()
        {
            var brush = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xF2, 0xFF, 0xD2, 0xB8), 0),
                    new GradientStop(Color.FromArgb(0xBF, 0xFF, 0x6B, 0x2C), 0.53),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0x6B, 0x2C), 1),
                },
            };
            brush.Freeze();
            return brush;
        }
    }
}
