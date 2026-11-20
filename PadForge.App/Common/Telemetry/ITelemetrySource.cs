using System;

namespace PadForge.Common.Telemetry
{
    /// <summary>
    /// Normalized telemetry snapshot. PadForge reads this from whichever
    /// racing title is running (each game exposes it through its own
    /// shared-memory or UDP channel). The RPM fields drive a wheel's RPM
    /// and shift LEDs, the same way SimHub and the Linux wheel drivers do.
    /// The speed and the grip drive a wheel's centering in Vehicle mode
    /// (#588).
    /// </summary>
    internal struct GameTelemetrySnapshot
    {
        public float Rpm;        // current engine RPM
        public float MaxRpm;     // redline RPM
        public float IdleRpm;    // idle RPM (Forza supplies it; 0 when unknown)
        public string Source;    // producing source name (diagnostic)

        /// <summary>The source sent a speed.</summary>
        public bool HasSpeed;

        /// <summary>The car's speed in meters a second, never negative.</summary>
        public float Speed;

        /// <summary>The road's grip under the front wheels against dry
        /// asphalt's (PadForge.Engine.Steering.RoadGrip), or 0 where the
        /// source sends no word on the road.</summary>
        public float Grip;

        /// <summary>Sets the speed from a reading in meters a second. A
        /// reading that is no number leaves the snapshot with none.</summary>
        public void SetSpeed(float metersPerSecond)
        {
            if (!float.IsFinite(metersPerSecond)) return;
            Speed = Math.Abs(metersPerSecond);
            HasSpeed = true;
        }

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

    /// <summary>
    /// A source that binds a UDP port. While the Game Telemetry row (issue
    /// #531) listens on the same port, the hub keeps this source closed and
    /// the lights read the game from the row (<see cref="TelemetryHub.YieldPort"/>).
    /// </summary>
    internal interface IUdpTelemetrySource : ITelemetrySource
    {
        int Port { get; }
    }
}
