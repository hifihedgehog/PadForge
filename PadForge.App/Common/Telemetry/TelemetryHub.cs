using System;
using System.Threading;

namespace PadForge.Common.Telemetry
{
    /// <summary>
    /// Owns the racing-telemetry sources and publishes a single current snapshot
    /// for the FFB dispatch to drive wheel RPM LEDs from. Demand-driven: the FFB
    /// loop calls <see cref="RequestActive"/> each frame a wheel wants LEDs, which
    /// starts the sources; after a few seconds with no request the hub stops them,
    /// so no socket is bound and no shared memory is held when the feature is idle.
    /// </summary>
    internal static class TelemetryHub
    {
        public const int DefaultForzaPort = 5300;

        private static readonly object _lock = new();
        private static ITelemetrySource[] _sources;
        private static Thread _poll;
        private static volatile bool _running;
        private static int _lastRequestTick;
        private static int _forzaPort = DefaultForzaPort;

        private static GameTelemetrySnapshot _current;
        private static volatile bool _hasCurrent;

        /// <summary>Forza Data Out listen port. Takes effect on the next start.</summary>
        public static void SetForzaPort(int port)
        {
            if (port > 0 && port <= 65535) _forzaPort = port;
        }

        /// <summary>Signals that a consumer needs telemetry this frame. Starts the
        /// sources on first call and keeps them alive while calls continue.</summary>
        public static void RequestActive()
        {
            _lastRequestTick = Environment.TickCount;
            if (!_running) Start();
        }

        /// <summary>Latest in-session snapshot, if any source is producing data.</summary>
        public static bool TryGetCurrent(out GameTelemetrySnapshot snap)
        {
            if (_hasCurrent) { snap = _current; return true; }
            snap = default;
            return false;
        }

        // All registered sources, polled in array order — the first with a fresh
        // in-session snapshot wins. Only one racing title runs at a time in
        // practice, so order is mostly a tie-breaker. Add a new game by dropping
        // its ITelemetrySource into this array.
        private static ITelemetrySource[] BuildSources() => new ITelemetrySource[]
        {
            new ForzaUdpTelemetrySource(_forzaPort),     // UDP 5300
            new AssettoCorsaTelemetrySource(),           // acpmf shared memory (AC / ACC)
            new IRacingTelemetrySource(),                // irsdk shared memory
            new RFactor2TelemetrySource(),               // rF2/LMU shared memory (plugin)
            new RaceRoomTelemetrySource(),               // R3E shared memory
            new ScsTruckTelemetrySource(),               // ETS2/ATS shared memory (plugin)
            new CodemastersUdpTelemetrySource(),         // UDP 20777: F1 23/24 + DiRT Rally
        };

        private static void Start()
        {
            lock (_lock)
            {
                if (_running) return;
                _sources = BuildSources();
                foreach (var s in _sources) { try { s.Start(); } catch { } }
                _running = true;
                _poll = new Thread(Loop) { IsBackground = true, Name = "TelemetryHub" };
                _poll.Start();
            }
        }

        private static void Loop()
        {
            while (_running)
            {
                if (unchecked(Environment.TickCount - _lastRequestTick) > 3000)
                {
                    Stop();
                    break;
                }

                GameTelemetrySnapshot best = default;
                bool have = false;
                var sources = _sources;
                if (sources != null)
                {
                    foreach (var s in sources)
                    {
                        try
                        {
                            if (s.TryGetSnapshot(out var snap) && snap.MaxRpm > 0f)
                            {
                                best = snap; have = true;
                                break;
                            }
                        }
                        catch { /* a flaky source must not take down the hub */ }
                    }
                }
                _current = best;
                _hasCurrent = have;

                Thread.Sleep(16); // ~60 Hz
            }
        }

        private static void Stop()
        {
            lock (_lock)
            {
                _running = false;
                _hasCurrent = false;
                var sources = _sources;
                if (sources != null)
                    foreach (var s in sources) { try { s.Dispose(); } catch { } }
                _sources = null;
            }
        }
    }
}
