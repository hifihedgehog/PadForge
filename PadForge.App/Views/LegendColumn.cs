using System;
using System.Collections.Generic;

namespace PadForge.Views
{
    /// <summary>
    /// Where a column of the picture's legend puts its chips. The 3D
    /// picture and the 2D one share it, so their legends stand alike.
    ///
    /// <para>A chip stands beside its control where it can. Chips never
    /// overlap: one is pushed down by the chip over it, and a column that
    /// would run past the picture's foot is pushed up from the foot.</para>
    ///
    /// <para>The column keeps clear of the buttons in the picture's
    /// corners (the view switch at the left, the colorway picker, the
    /// legend's own switch and Reset View at the right): its first chip
    /// stands a gap under them. In a picture too short for the column at
    /// its usual gap, the gap closes up, down to the least one. A picked
    /// control's bar leaves the picture about 320 tall in a window 1040
    /// tall, where eleven chips at the usual gap ran under Reset View
    /// (seen 2026-11-15). In a picture too short even for that, the column
    /// takes the corner's room too.</para>
    /// </summary>
    internal static class LegendColumn
    {
        /// <summary>A chip's height.</summary>
        internal const double ChipHeight = 20;

        /// <summary>The room between two chips, where the picture has it.</summary>
        internal const double Gap = 6;

        /// <summary>The least room between two chips, in a short picture.</summary>
        internal const double LeastGap = 2;

        /// <summary>The room a column keeps to the picture's top and foot.</summary>
        internal const double EdgeMargin = 8;

        /// <summary>The top of each chip of a column.
        /// <paramref name="anchors"/> are the heights the column's controls
        /// are drawn at, from the top one down. <paramref name="height"/>
        /// is the picture's, and <paramref name="cornerBottom"/> is how far
        /// down the buttons in its corners reach, 0 for none.</summary>
        internal static double[] Tops(IReadOnlyList<double> anchors, double height, double cornerBottom)
        {
            int count = anchors?.Count ?? 0;
            var tops = new double[count];
            if (count == 0) return tops;

            double foot = height - EdgeMargin;
            double chips = count * ChipHeight;
            // The gap at which the column ends on the foot from a top,
            // never more than the usual one.
            double GapFrom(double from) => count > 1 ? Math.Min(Gap, (foot - from - chips) / (count - 1)) : Gap;

            double top = Math.Max(EdgeMargin, cornerBottom + Gap);
            double gap = GapFrom(top);
            if (gap < LeastGap)
            {
                top = EdgeMargin;
                gap = Math.Max(LeastGap, GapFrom(top));
            }

            // Down from the top: beside its control, or under the chip over it.
            double previousBottom = top - gap;
            for (int i = 0; i < count; i++)
            {
                tops[i] = Math.Max(anchors[i] - ChipHeight / 2, previousBottom + gap);
                previousBottom = tops[i] + ChipHeight;
            }

            // Past the foot: up from the foot.
            if (tops[count - 1] + ChipHeight > foot)
            {
                tops[count - 1] = foot - ChipHeight;
                for (int i = count - 2; i >= 0; i--)
                    tops[i] = Math.Min(tops[i], tops[i + 1] - ChipHeight - gap);
                tops[0] = Math.Max(tops[0], top);
            }
            return tops;
        }
    }
}
