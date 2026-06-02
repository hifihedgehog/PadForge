namespace PadForge.Common.Telemetry
{
    /// <summary>
    /// Maps a 0..1 RPM fraction to a shift-light LED bitmask. LEDs begin filling
    /// at <see cref="Start"/> of redline and are all lit by <see cref="Full"/>;
    /// above <see cref="Redline"/> the whole strip blinks as a shift-now cue.
    /// Bit 0 is the first (leftmost) LED. Each wheel family has a different LED
    /// count, so the count is a parameter.
    /// </summary>
    internal static class RpmLedMap
    {
        private const float Start = 0.70f;   // first LED lights here
        private const float Full = 0.97f;    // all LEDs lit here
        private const float Redline = 0.985f; // blink the whole strip above this

        public const int LogitechLeds = 5;   // G25..G923 rev strip
        public const int FanatecLeds = 9;    // ftecff LEDS = 9 (rim strip)

        /// <summary>Logitech 5-LED bitmask (f8 12 payload). Bit 0 = first LED.</summary>
        public static byte Logitech(float frac, bool blinkOn)
        {
            if (frac >= Redline) return blinkOn ? (byte)0x1F : (byte)0x00;
            return (byte)((1 << Count(frac, LogitechLeds)) - 1);
        }

        /// <summary>Fanatec 9-LED bitmask (bit 0 = first LED). The Fanatec writer
        /// reshuffles for the rim wire format (first LED = highest bit).</summary>
        public static int Fanatec(float frac, bool blinkOn)
        {
            if (frac >= Redline) return blinkOn ? 0x1FF : 0x000;
            return (1 << Count(frac, FanatecLeds)) - 1;
        }

        private static int Count(float frac, int total)
        {
            if (frac <= Start) return 0;
            if (frac >= Full) return total;
            float t = (frac - Start) / (Full - Start);
            int n = (int)(t * total) + 1;
            return n > total ? total : n;
        }
    }
}
