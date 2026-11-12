using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PadForge.Views
{
    /// <summary>A picture of a slot's controls that can say where a control
    /// is drawn, so the page can ring the picked one (#574). Every picture
    /// of the Buttons section is one: the 3D and 2D models, the schematic,
    /// the keyboard and mouse, the MIDI board, the two VR hands, and the
    /// touch sensors' capsules.</summary>
    internal interface IPictureWithPick
    {
        /// <summary>Where the control a mapping target names is drawn, in
        /// this picture's own coordinates, and whether its outline is
        /// round. False when the picture draws no such control, or is not
        /// built yet.</summary>
        bool TryGetPickBounds(string target, out Rect bounds, out bool round);

        /// <summary>Raised when a control may be drawn elsewhere now while
        /// the picture kept its size: its drawing was built again, or its
        /// camera moved.</summary>
        event EventHandler PickBoundsChanged;

        /// <summary>Raised with the control under the pointer, by the name
        /// a click on it records, and with null when the pointer leaves it,
        /// so the page can ring it as the Buttons board draws a hover. A
        /// picture may say the same control again as the pointer moves over
        /// it.</summary>
        event EventHandler<string> HoverTargetChanged;
    }

    /// <summary>Where the ring on a picked control goes: a line around the
    /// control with a soft band outside it, as the boards draw it.</summary>
    internal readonly record struct PickRingPlacement(Rect Line, double LineRadius, Rect Band, double BandRadius);

    internal static class PicturePick
    {
        /// <summary>The space between a control and its ring's line.</summary>
        internal const double Gap = 3;

        /// <summary>The ring's line.</summary>
        internal const double LineWidth = 2;

        /// <summary>The soft band outside the line.</summary>
        internal const double BandWidth = 4;

        /// <summary>The ring for a control drawn at <paramref name="control"/>.
        /// The line stands off the control by <see cref="Gap"/>. A round
        /// control's ring is as round as its box allows. Any other is a
        /// box with corners a fifth of its shorter side, no less than 6 and
        /// no more than 14.
        ///
        /// <para>The line stays inside a picture of the size
        /// <paramref name="within"/>: a control at the picture's edge, as a
        /// trigger at the top of the 2D model, would have its ring cut open
        /// there. A control that lies outside the picture has no ring, and
        /// the placement's line is then empty.</para></summary>
        internal static PickRingPlacement Place(Rect control, bool round, Size within = default)
        {
            // Whole pixels, grown outward, so the line is sharp and never
            // cuts into the control.
            double left = Math.Floor(control.Left), top = Math.Floor(control.Top);
            Rect line = new Rect(left, top, Math.Ceiling(control.Right) - left, Math.Ceiling(control.Bottom) - top);
            line.Inflate(Gap + LineWidth, Gap + LineWidth);
            if (within.Width > 0 && within.Height > 0)
            {
                line.Intersect(new Rect(0, 0, within.Width, within.Height));
                if (line.IsEmpty || line.Width <= 2 * LineWidth || line.Height <= 2 * LineWidth) return default;
            }
            double shorter = Math.Min(line.Width, line.Height);
            double radius = round ? shorter / 2 : Math.Clamp(shorter * 0.2, 6, 14);
            Rect band = line;
            band.Inflate(BandWidth, BandWidth);
            return new PickRingPlacement(line, radius, band, radius + BandWidth);
        }

        /// <summary>Maps a rect of a part's own coordinates to the
        /// picture's. False while the part is not in the picture's tree, or
        /// covers nothing.</summary>
        internal static bool TryMap(Visual part, Visual picture, Rect own, out Rect bounds)
        {
            bounds = default;
            if (part == null || picture == null || own.IsEmpty || own.Width <= 0 || own.Height <= 0) return false;
            if (!ReferenceEquals(part, picture) && !picture.IsAncestorOf(part)) return false;
            bounds = ReferenceEquals(part, picture) ? own : part.TransformToAncestor(picture).TransformBounds(own);
            return bounds.Width > 0 && bounds.Height > 0;
        }

        /// <summary>The rect a drawn part covers in its own coordinates: a
        /// path's outline, or the box of anything else. Empty for a part
        /// with no size yet.</summary>
        internal static Rect OwnRect(FrameworkElement part)
        {
            if (part == null) return Rect.Empty;
            if (part is Path { Data: { } outline }) return outline.Bounds;
            double width = double.IsNaN(part.Width) ? part.ActualWidth : part.Width;
            double height = double.IsNaN(part.Height) ? part.ActualHeight : part.Height;
            return width > 0 && height > 0 ? new Rect(0, 0, width, height) : Rect.Empty;
        }

        /// <summary>Whether a part and everything above it up to the
        /// picture is set to show. Read from Visibility, never IsVisible,
        /// which is false for everything in a page that is in no window.</summary>
        internal static bool Shows(DependencyObject part, DependencyObject picture)
        {
            for (var at = part; at != null; at = VisualTreeHelper.GetParent(at))
            {
                if (at is UIElement { Visibility: not Visibility.Visible }) return false;
                if (ReferenceEquals(at, picture)) return true;
            }
            return false;
        }
    }
}
