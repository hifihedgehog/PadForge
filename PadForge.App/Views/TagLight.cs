using System;
using System.Windows.Controls;
using System.Windows.Media;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>How brightly a legend tag of the 3D and 2D pictures is lit
    /// (#574): by its control's output. A button's tag is dark or lit. A
    /// trigger's and a stick axis's tag takes ember by how far the output
    /// is from rest, so the tag says how hard the control is driven. The
    /// buttons had ember on their tags and the axes none, and the triggers
    /// had two level bars of their own beside them, raw in blue and output
    /// in ember (owner, 2026-11-14). The tag is the one light now.
    ///
    /// <para>A level is cut into steps, so a tag's brushes change only
    /// when its step does.</para></summary>
    internal static class TagLight
    {
        /// <summary>The steps of ember between dark and fully lit.</summary>
        internal const int Steps = 16;

        /// <summary>A level under this is rest, so the idle reading of a
        /// worn trigger or stick lights nothing.</summary>
        internal const double Rest = 0.02;

        /// <summary>The share of ember a tag takes as its control leaves
        /// rest, so the least movement shows. The rest of the way to full
        /// follows the level.</summary>
        internal const double FirstShare = 0.35;

        /// <summary>The output level of an analog control, by the name the
        /// pictures draw it under: a trigger's pull, and a stick axis's
        /// deflection to either side, each from 0 at rest to 1. Null for a
        /// control that is on or off.</summary>
        internal static double? AnalogLevel(PadViewModel vm, string control) => control switch
        {
            "LeftTrigger" => vm.LeftTrigger,
            "RightTrigger" => vm.RightTrigger,
            "LeftThumbAxisX" => Math.Abs(vm.ThumbLX - 0.5) * 2,
            "LeftThumbAxisY" => Math.Abs(vm.ThumbLY - 0.5) * 2,
            "RightThumbAxisX" => Math.Abs(vm.ThumbRX - 0.5) * 2,
            "RightThumbAxisY" => Math.Abs(vm.ThumbRY - 0.5) * 2,
            _ => null,
        };

        /// <summary>The step a level lights a tag at: 0 at rest, and from
        /// the first share to <see cref="Steps"/> as the level goes to 1.</summary>
        internal static int StepOf(double level)
        {
            if (!(level >= Rest)) return 0;
            double share = FirstShare + (1 - FirstShare) * Math.Min(1.0, level);
            return Math.Clamp((int)Math.Round(share * Steps), 1, Steps);
        }

        /// <summary>The color a step of the way from one color to another.</summary>
        internal static Color Mix(Color rest, Color lit, int step)
        {
            double share = Math.Clamp(step, 0, Steps) / (double)Steps;
            return Color.FromArgb(Part(rest.A, lit.A, share), Part(rest.R, lit.R, share),
                Part(rest.G, lit.G, share), Part(rest.B, lit.B, share));
        }

        private static byte Part(byte rest, byte lit, double share)
            => (byte)Math.Round(rest + (lit - rest) * share);

        /// <summary>Sets a tag's edge and name for a step. At rest and fully
        /// lit they take the theme's own brushes, which follow a change of
        /// theme. Between, each is a mix of its rest color and ember.</summary>
        internal static void Paint(Border edge, TextBlock name, int step)
        {
            if (step <= 0)
            {
                edge.SetResourceReference(Border.BorderBrushProperty, "SteelLineSoftBrush");
                name.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                return;
            }
            if (step >= Steps
                || edge.TryFindResource("EmberBrush") is not SolidColorBrush ember
                || edge.TryFindResource("SteelLineSoftBrush") is not SolidColorBrush line
                || name.TryFindResource("TextFillColorSecondaryBrush") is not SolidColorBrush text)
            {
                edge.SetResourceReference(Border.BorderBrushProperty, "EmberBrush");
                name.SetResourceReference(TextBlock.ForegroundProperty, "EmberBrush");
                return;
            }
            edge.BorderBrush = Frozen(Mix(line.Color, ember.Color, step));
            name.Foreground = Frozen(Mix(text.Color, ember.Color, step));
        }

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
