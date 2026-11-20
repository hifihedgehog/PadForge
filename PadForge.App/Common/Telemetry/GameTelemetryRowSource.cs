using System;
using PadForge.Common.Input.Telemetry;

namespace PadForge.Common.Telemetry
{
    /// <summary>
    /// The Game Telemetry row's engine values (issue #531). While the row
    /// listens on a port, the hub's own source for that port stays closed
    /// (<see cref="TelemetryHub.YieldPort"/>), so the lights read that game
    /// from the row, which also decodes F1 22 and F1 25, EA SPORTS WRC,
    /// Project CARS 2 and Automobilista 2 over UDP, the ETS2 plugin and the
    /// user's custom formats. The row owns its socket, so this source opens
    /// and closes nothing.
    ///
    /// <para>The hub holds two. The one first in its order answers when the
    /// game sent its max RPM. The one last answers when the row learned the
    /// max from the highest RPM seen, so a source with the game's own max,
    /// scs-sdk-plugin's for a truck, answers before it.</para>
    /// </summary>
    internal sealed class GameTelemetryRowSource : ITelemetrySource
    {
        /// <summary>A learned max RPM is the highest RPM seen, which the
        /// engine meets each time it passes its old peak and on its first
        /// frame at idle. The max goes this far above the RPM, the headroom
        /// the hub's OutGauge source keeps, so the strip does not blink the
        /// shift cue there.</summary>
        internal const float LearnedHeadroom = 1.05f;

        private readonly bool _learnedMax;

        /// <param name="learnedMax">Answer for a max RPM the row learned,
        /// instead of one the game sent.</param>
        public GameTelemetryRowSource(bool learnedMax) { _learnedMax = learnedMax; }

        public string Name => "Game Telemetry";

        public void Start() { }
        public void Stop() { }
        public void Dispose() { }

        public bool TryGetSnapshot(out GameTelemetrySnapshot snap)
        {
            snap = default;
            if (!GameTelemetryRuntime.TryGetEngine(Environment.TickCount64, out float rpm, out float maxRpm, out bool learned)
                || learned != _learnedMax)
                return false;
            if (learned) maxRpm = Math.Max(maxRpm, rpm * LearnedHeadroom);
            snap = new GameTelemetrySnapshot { Rpm = rpm, MaxRpm = maxRpm, IdleRpm = 0f, Source = Name };
            if (GameTelemetryRuntime.TryGetMotion(Environment.TickCount64, out float speed, out bool hasSpeed, out float grip))
            {
                if (hasSpeed) snap.SetSpeed(speed);
                snap.Grip = grip;
            }
            return true;
        }
    }
}
