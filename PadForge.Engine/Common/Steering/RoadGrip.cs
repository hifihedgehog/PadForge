using System;

namespace PadForge.Engine.Steering
{
    /// <summary>
    /// A road's grip against dry asphalt's (#588), which is what the
    /// steering model takes as the tire's friction coefficient.
    ///
    /// <para>The figures are the peak coefficients of road adhesion in
    /// J. Y. Wong, Theory of Ground Vehicles, 2nd ed. (1993), p. 26, as
    /// reproduced at hpwizard.com/tire-friction-coefficient.html: asphalt
    /// and concrete dry 0.80 to 0.90, asphalt wet 0.50 to 0.70, gravel
    /// 0.60, earth road dry 0.68 and wet 0.55, hard-packed snow 0.20 and
    /// ice 0.10. Each is taken over dry asphalt's 0.85, the middle of its
    /// range, with wet asphalt at the middle of its own, and kept to two
    /// places.</para>
    /// </summary>
    public static class RoadGrip
    {
        public const double Dry = 1.0;
        public const double Wet = 0.71;
        public const double Gravel = 0.71;

        /// <summary>A dry earth road.</summary>
        public const double Dirt = 0.80;

        /// <summary>A wet earth road.</summary>
        public const double Mud = 0.65;

        public const double Snow = 0.24;
        public const double Ice = 0.12;

        /// <summary>Grass and sand are not in the table. They read as
        /// its wet earth road, the loosest surface it gives that is
        /// neither snow nor ice.</summary>
        public const double Grass = Mud, Sand = Mud;

        /// <summary>The grip of asphalt between dry and wet, for a
        /// wetness from 0 to 1.</summary>
        public static double FromWetness(double wetness)
            => double.IsFinite(wetness) ? Dry - (Dry - Wet) * Math.Clamp(wetness, 0.0, 1.0) : Dry;

        /// <summary>The front axle's grip from its two wheels': their
        /// mean, since each wheel carries half the axle's load. A wheel
        /// with no reading, 0, takes the other's.</summary>
        public static double FrontAxle(double left, double right)
        {
            if (left <= 0) return Math.Max(0, right);
            if (right <= 0) return left;
            return (left + right) / 2.0;
        }
    }
}
