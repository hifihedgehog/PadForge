using System;
using System.Windows;
using PadForge.ViewModels;

namespace PadForge.Common
{
    /// <summary>Where one control is drawn on a controller's picture: the
    /// control's role, and its box. A drawing gives the box in the
    /// picture's pixels. A Home card holds it in parts of the fitted
    /// picture's width and height, so it stays right at any size the card
    /// shows the picture at.</summary>
    public readonly record struct StillMark(string Role, Rect Box);

    /// <summary>What a Home card's picture lights (the approved "keep the
    /// frame" canvas): a held button, a pulled trigger and a moved stick
    /// light on the picture, as they do on the controller's own page.
    /// A button's role is its name on that page. The triggers and the
    /// sticks have the four names here.</summary>
    internal static class StillMarks
    {
        internal const string LeftTrigger = "LeftTrigger", RightTrigger = "RightTrigger";
        internal const string LeftStick = "LeftStick", RightStick = "RightStick";

        /// <summary>The steps a level is told in. A button is at none or
        /// at all of them, and a trigger or a stick moves through them, so
        /// a card is drawn again only when a step changes.</summary>
        internal const int Steps = 15;

        internal static readonly StillMark[] None = Array.Empty<StillMark>();

        /// <summary>Whether a role is one a picture can light.</summary>
        internal static bool Reads(string role)
            => role is LeftTrigger or RightTrigger or LeftStick or RightStick
                || Views.ControllerModelView.TryReadButton(null, role, out _);

        /// <summary>How far a role's control is held on a controller now,
        /// from 0 to 1: a trigger's pull, how far a stick is from its
        /// center, and 1 for a held button.</summary>
        internal static double Level(PadViewModel pad, string role)
        {
            if (pad == null) return 0;
            switch (role)
            {
                case LeftTrigger: return pad.LeftTrigger;
                case RightTrigger: return pad.RightTrigger;
                case LeftStick: return Reach(pad.RawThumbLX, pad.RawThumbLY);
                case RightStick: return Reach(pad.RawThumbRX, pad.RawThumbRY);
            }
            return Views.ControllerModelView.TryReadButton(pad, role, out bool held) && held ? 1 : 0;
        }

        /// <summary>How far a stick is from its center, from 0 to 1.</summary>
        internal static double Reach(short x, short y)
            => Math.Min(1.0, Math.Sqrt((double)x * x + (double)y * y) / short.MaxValue);

        /// <summary>A level as one of the steps.</summary>
        internal static byte Step(double level)
            => double.IsNaN(level) ? (byte)0 : (byte)Math.Round(Math.Clamp(level, 0.0, 1.0) * Steps);

        /// <summary>Marks given in a picture's pixels, as parts of the
        /// piece of the picture that was cut out and kept.</summary>
        internal static StillMark[] Place(System.Collections.Generic.IReadOnlyList<StillMark> marks, Int32Rect cut)
        {
            if (marks == null || marks.Count == 0 || cut.Width <= 0 || cut.Height <= 0) return None;
            var placed = new StillMark[marks.Count];
            for (int i = 0; i < placed.Length; i++)
            {
                Rect box = marks[i].Box;
                placed[i] = new StillMark(marks[i].Role, new Rect(
                    (box.X - cut.X) / cut.Width, (box.Y - cut.Y) / cut.Height,
                    box.Width / cut.Width, box.Height / cut.Height));
            }
            return placed;
        }
    }
}
