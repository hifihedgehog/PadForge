using System;

namespace PadForge.Common.Telemetry
{
    /// <summary>
    /// Normalized engine-telemetry snapshot used to drive wheel RPM/shift LEDs.
    /// PadForge reads this from whichever racing title is running (each game
    /// exposes RPM through its own shared-memory or UDP channel) and forwards it
    /// to the wheel's LED strip, the same way SimHub and the Linux wheel drivers
    /// do. RPM is the only field the LED feature needs; the rest are kept for
    /// future dash use.
    /// </summary>
    internal struct GameTelemetrySnapshot
    {
        public float Rpm;        // current engine RPM
        public float MaxRpm;     // redline RPM
        public float IdleRpm;    // idle RPM (Forza supplies it; 0 when unknown)
        public string Source;    // producing source name (diagnostic)

        /// <summary>0..1 shift-light position as a raw fraction of redline. The
        /// LED mapper applies the shift-light thresholds (first LED well above
        /// idle, redline blink near the top), so this stays a plain ratio.</summary>
        public float RpmFraction
        {
            get
            {
                if (MaxRpm <= 1f || Rpm <= 0f) return 0f;
                float f = Rpm / MaxRpm;
                return f < 0f ? 0f : (f > 1f ? 1f : f);
            }
        }
    }

    /// <summary>
    /// One racing title's telemetry channel. Sources are cheap to keep idle: a
    /// shared-memory source returns false until the game maps its pages, a UDP
    /// source returns false until datagrams arrive. <see cref="TelemetryHub"/>
    /// owns the lifetime and polls <see cref="TryGetSnapshot"/>.
    /// </summary>
    internal interface ITelemetrySource : IDisposable
    {
        string Name { get; }
        void Start();
        void Stop();

        /// <summary>True with a fresh, in-session snapshot; false when the game
        /// isn't running or hasn't sent data recently.</summary>
        bool TryGetSnapshot(out GameTelemetrySnapshot snap);
    }
}
