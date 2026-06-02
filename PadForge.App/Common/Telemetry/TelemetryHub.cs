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
        private static ForzaUdpTelemetrySource _forza;
        private static AssettoCorsaTelemetrySource _ac;
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

        private static void Start()
        {
            lock (_lock)
            {
                if (_running) return;
                _forza = new ForzaUdpTelemetrySource(_forzaPort);
                _ac = new AssettoCorsaTelemetrySource();
                _forza.Start();
                _ac.Start();
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
                // Forza first (active racing titles rarely overlap); AC/ACC next.
                if (_forza != null && _forza.TryGetSnapshot(out var f) && f.MaxRpm > 0f)
                {
                    best = f; have = true;
                }
                else if (_ac != null && _ac.TryGetSnapshot(out var a) && a.MaxRpm > 0f)
                {
                    best = a; have = true;
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
                try { _forza?.Dispose(); } catch { }
                try { _ac?.Dispose(); } catch { }
                _forza = null;
                _ac = null;
            }
        }
    }
}
