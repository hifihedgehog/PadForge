using System;

namespace PadForge.Common.Input
{
    /// <summary>
    /// How rumble strength becomes pulses on a device that plays fixed
    /// haptic waveforms instead of running a motor (#494). Three levels with
    /// hysteresis, so rumble that hovers on a boundary does not flip the level
    /// every tick. A Logitech device plays one waveform per pulse, chosen by
    /// level from the mask it reports, at an interval that shortens as rumble
    /// grows: 250 ms at the faintest, 80 ms at full strength, the cooldown
    /// mxhaptics ships for its impact events.
    /// </summary>
    internal static class HapticRumbleShaper
    {
        internal const float LightOn = 0.05f;
        internal const float LightOff = 0.03f;
        internal const float MediumOn = 0.33f;
        internal const float MediumOff = 0.30f;
        internal const float StrongOn = 0.66f;
        internal const float StrongOff = 0.63f;

        internal const int SlowestMs = 250;
        internal const int FastestMs = 80;

        internal static int Level(float amplitude, int current)
        {
            int up = amplitude >= StrongOn ? 3 : amplitude >= MediumOn ? 2 : amplitude >= LightOn ? 1 : 0;
            int down = amplitude >= StrongOff ? 3 : amplitude >= MediumOff ? 2 : amplitude >= LightOff ? 1 : 0;
            if (up > current) return up;
            if (down < current) return down;
            return current;
        }

        internal static int IntervalMs(float amplitude)
        {
            float t = (amplitude - LightOn) / (1f - LightOn);
            t = t < 0f ? 0f : (t > 1f ? 1f : t);
            return (int)MathF.Round(SlowestMs - t * (SlowestMs - FastestMs));
        }

        /// <summary>The collision waveform for a level, falling back to the
        /// nearest one the device lists. Null when it lists none of the
        /// three.</summary>
        internal static byte? Waveform(int level, uint mask)
        {
            if (level <= 0) return null;
            byte[] order = level switch
            {
                1 => new[] { HidppHapticProtocol.SubtleCollision, HidppHapticProtocol.DampCollision, HidppHapticProtocol.SharpCollision },
                2 => new[] { HidppHapticProtocol.DampCollision, HidppHapticProtocol.SubtleCollision, HidppHapticProtocol.SharpCollision },
                _ => new[] { HidppHapticProtocol.SharpCollision, HidppHapticProtocol.DampCollision, HidppHapticProtocol.SubtleCollision },
            };
            foreach (byte waveform in order)
                if ((mask & (1u << waveform)) != 0) return waveform;
            return null;
        }
    }
}
