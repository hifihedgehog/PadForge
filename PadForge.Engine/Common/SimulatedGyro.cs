using System;

namespace PadForge.Engine
{
    /// <summary>
    /// Which gyro axes a device lacks that its accelerometer can supply
    /// (#472). The DualShock 3's gyro measures yaw only, and both of its
    /// paths publish pitch and roll as zero, so those two come from the
    /// accelerometer when the option is on. Every other device with a gyro
    /// reports all three axes and never sees the option. A device with an
    /// accelerometer and no gyro is not served: with no gyro, rotation about
    /// gravity has no source, so a level controller would get no yaw.
    /// </summary>
    public static class SimulatedGyro
    {
        [Flags]
        public enum Axes : byte
        {
            None = 0,
            Pitch = 1,
            Yaw = 2,
            Roll = 4,
        }

        public const int DefaultSmoothingMs = 100;
        public const int MinSmoothingMs = 20;
        public const int MaxSmoothingMs = 250;

        public static Axes MissingAxes(ushort vendorId, ushort productId, bool hasGyro, bool hasAccel)
            => hasGyro && hasAccel && DualShock3Motion.Is(vendorId, productId)
                ? Axes.Pitch | Axes.Roll
                : Axes.None;

        public static bool Covers(Axes axes, int gyroAxis) => gyroAxis switch
        {
            0 => (axes & Axes.Pitch) != 0,
            1 => (axes & Axes.Yaw) != 0,
            2 => (axes & Axes.Roll) != 0,
            _ => false,
        };

        /// <summary>The stored smoothing time in seconds, clamped to the
        /// setting's range. An unreadable value reads as the default.</summary>
        public static float SmoothingSeconds(float milliseconds)
        {
            if (!float.IsFinite(milliseconds)) milliseconds = DefaultSmoothingMs;
            return Math.Clamp(milliseconds, MinSmoothingMs, MaxSmoothingMs) / 1000f;
        }
    }

    /// <summary>A copied simulated rate for one (device, slot), rad/s in
    /// SDL's frame, and the axes it supplies.</summary>
    public readonly record struct SimulatedGyroSample(float Pitch, float Yaw, float Roll, SimulatedGyro.Axes Axes)
    {
        public bool Covers(int gyroAxis) => SimulatedGyro.Covers(Axes, gyroAxis);

        public float Get(int gyroAxis) => gyroAxis switch
        {
            0 => Pitch,
            1 => Yaw,
            2 => Roll,
            _ => 0f,
        };
    }
}
