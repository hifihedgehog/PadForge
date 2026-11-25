namespace PadForge.Engine.Recording
{
    /// <summary>What a problem recording hears of force effects (#600). The
    /// engine's force writer calls it on the poll thread.</summary>
    public interface IForceTrace
    {
        /// <summary>An effect was updated, made, or made and started.</summary>
        void Update(ISdlInputDevice device, bool made, bool started);

        /// <summary>The device's effects were stopped and let go.</summary>
        void Stopped(ISdlInputDevice device);

        /// <summary>Making or starting an effect failed: what failed and the
        /// effect's kind (an SDL_HAPTIC_ bit). SDL's error is still the
        /// thread's to read when this is called.</summary>
        void Failed(ISdlInputDevice device, string what, uint effectType);
    }

    /// <summary>
    /// The force writer's tap for a problem recording (#600): force effects
    /// made, stopped, refused and failed, and the updates of each second.
    /// <see cref="Sink"/> is null while nothing records, so each tap costs one
    /// volatile read.
    /// </summary>
    public static class ForceTrace
    {
        /// <summary>The recording's ear, or null.</summary>
        public static volatile IForceTrace Sink;

        /// <summary>An SDL effect kind as its word.</summary>
        public static string KindWord(uint type) => type switch
        {
            1u << 0 => "constant",
            1u << 1 => "sine",
            1u << 2 => "square",
            1u << 3 => "triangle",
            1u << 4 => "sawtooth up",
            1u << 5 => "sawtooth down",
            1u << 6 => "ramp",
            1u << 7 => "spring",
            1u << 8 => "damper",
            1u << 9 => "inertia",
            1u << 10 => "friction",
            1u << 11 => "left and right motors",
            1u << 15 => "custom",
            _ => "kind 0x" + type.ToString("X", System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
