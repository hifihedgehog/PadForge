using System;
using System.Globalization;

namespace PadForge.Common.Input
{
    /// <summary>The polling rate as a setting: how the global rate and a
    /// profile's override resolve to the rate the loop runs at, what an
    /// interval stored by an older PadForge becomes, and how a rate is
    /// written for a person.</summary>
    internal static class PollingRates
    {
        /// <summary>The rate the loop runs at: the profile's override when it
        /// has one (0 is none), the global rate otherwise, and either held
        /// to the range the loop runs in.</summary>
        internal static int Resolve(int overrideHz, int globalHz)
            => Math.Clamp(overrideHz > 0 ? overrideHz : globalHz,
                InputManager.MinPollingRateHz, InputManager.MaxPollingRateHz);

        /// <summary>The rate of an interval in whole milliseconds, as
        /// PadForge stored the setting before version 6: 1 to 16 ms, so
        /// 1000 Hz down to 63. To the nearest hertz, a half going up.</summary>
        internal static int FromIntervalMs(int intervalMs)
            => (int)Math.Round(1000.0 / Math.Clamp(intervalMs, 1, 16), MidpointRounding.AwayFromZero);

        /// <summary>The rates a profile's dialog offers, fastest first.</summary>
        internal static readonly int[] Choices = { 10000, 8000, 4000, 2000, 1000, 500, 250, 125, 60 };

        /// <summary>A rate with the time between two of its passes, as a
        /// list shows it: "4000 Hz (0.25 ms)". The interval has no more
        /// than three decimals, in the culture's own number form, and the
        /// two units are the interface language's (Common_Hz and
        /// Common_Ms).</summary>
        internal static string Label(int hz, CultureInfo culture, string hertz, string milliseconds)
            => string.Format(culture, "{0} {2} ({1:0.###} {3})", hz, 1000.0 / Math.Max(1, hz), hertz, milliseconds);
    }
}
