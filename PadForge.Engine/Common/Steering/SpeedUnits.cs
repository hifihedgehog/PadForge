using System;
using System.Globalization;

namespace PadForge.Engine.Steering
{
    /// <summary>
    /// The unit a road speed reads in on this PC (#588), and the fixed speed
    /// a wheel's centering starts with. Speeds are saved in km/h whatever the
    /// unit shown.
    /// </summary>
    public static class SpeedUnits
    {
        public const double KmhPerMph = 1.609344;

        private static readonly Lazy<bool> _miles = new(() =>
        {
            // The Windows region, which the interface's language does not change.
            try { return MilesIn(RegionInfo.CurrentRegion); }
            catch (ArgumentException) { return false; }
        });

        /// <summary>Whether speeds read in miles an hour here. Read once:
        /// a running program keeps the region it started in.</summary>
        public static bool Miles => _miles.Value;

        /// <summary>Whether a region's speeds read in miles an hour: its
        /// measures are not metric, or it is the United Kingdom, whose
        /// measures are metric and whose roads are signed in miles.</summary>
        public static bool MilesIn(RegionInfo region)
            => region != null && (!region.IsMetric
                || string.Equals(region.TwoLetterISORegionName, "GB", StringComparison.OrdinalIgnoreCase));

        /// <summary>The fixed speed a wheel starts with, in km/h: 60 mph
        /// where speeds read in miles, the higher of the two speeds the
        /// paper tests steering feel at, and 100 km/h elsewhere. Each is a
        /// round number in the unit it is shown in.</summary>
        public static int DefaultFixedSpeedKmh => FixedSpeedKmhFor(Miles);

        /// <summary>The starting fixed speed for a unit: 97 km/h, which
        /// reads 60 mph, or 100 km/h.</summary>
        public static int FixedSpeedKmhFor(bool miles) => miles ? 97 : 100;

        /// <summary>A speed in m/s in the unit shown, to a whole number.</summary>
        public static double Shown(double metersPerSecond, bool miles)
            => Math.Round(miles ? metersPerSecond * 3.6 / KmhPerMph : metersPerSecond * 3.6);
    }
}
