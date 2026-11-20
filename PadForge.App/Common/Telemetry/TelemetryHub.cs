using System;
using System.Collections.Generic;
using System.Threading;

namespace PadForge.Common.Telemetry
{
    /// <summary>
    /// Owns the racing-telemetry sources and publishes a single current snapshot
    /// for the FFB dispatch to drive wheel RPM LEDs and a wheel's centering
    /// from. Demand-driven: the FFB loop calls <see cref="RequestActive"/> each
    /// frame a wheel wants either, which starts the sources. After a few seconds
    /// with no request the hub stops them, so no socket is bound and no shared
    /// memory is held when both are idle.
    /// </summary>
    internal static class TelemetryHub
    {
        public const int DefaultForzaPort = 5300;

        private static readonly object _lock = new();
        private static ITelemetrySource[] _sources;
        private static Thread _poll;
        private static volatile bool _running;
        private static volatile int _lastRequestTick;
        private static int _forzaPort = DefaultForzaPort;

        // Guards the published snapshot. Separate from _lock so a consumer read
        // never blocks behind source creation/teardown in Start()/Stop().
        private static readonly object _snapLock = new();
        private static GameTelemetrySnapshot _current;
        private static bool _hasCurrent;

        // Ports the Game Telemetry row holds (issue #531), under _lock. Two
        // sockets cannot share a port, and the row decodes every format these
        // UDP sources read, so a source on a held port stays closed while
        // the GameTelemetryRowSource pair reads the row.
        private static readonly HashSet<int> _yieldedPorts = new();

        /// <summary>Forza Data Out listen port. Takes effect on the next start.</summary>
        public static void SetForzaPort(int port)
        {
            if (port > 0 && port <= 65535) _forzaPort = port;
        }

        /// <summary>Closes the source on <paramref name="port"/> if one is open,
        /// and keeps it closed until <see cref="ReclaimPort"/>. The Game
        /// Telemetry row calls this before it binds.</summary>
        public static void YieldPort(int port)
        {
            lock (_lock)
            {
                if (!_yieldedPorts.Add(port)) return;
                var sources = _sources;
                if (sources == null) return;
                foreach (var s in sources)
                    if (s is IUdpTelemetrySource udp && udp.Port == port) { try { s.Stop(); } catch { } }
            }
        }

        /// <summary>Gives <paramref name="port"/> back after the row closed its
        /// socket. A running hub opens its source there again.</summary>
        public static void ReclaimPort(int port)
        {
            lock (_lock)
            {
                if (!_yieldedPorts.Remove(port)) return;
                var sources = _sources;
                if (!_running || sources == null) return;
                foreach (var s in sources)
                    if (s is IUdpTelemetrySource udp && udp.Port == port) { try { s.Start(); } catch { } }
            }
        }

        /// <summary>Test seam: whether the row holds <paramref name="port"/>.</summary>
        internal static bool IsPortYielded(int port)
        {
            lock (_lock) return _yieldedPorts.Contains(port);
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
            lock (_snapLock)
            {
                if (_hasCurrent) { snap = _current; return true; }
                snap = default;
                return false;
            }
        }

        // All registered sources, polled in array order. The first with a fresh
        // in-session snapshot wins. Only one racing title runs at a time in
        // practice, so order is mostly a tie-breaker. Add a new game by dropping
        // its ITelemetrySource into this array. The Game Telemetry row comes
        // first when its game sent a max RPM: while it is live, the game it
        // reads is the one the user set it up for, on a port no source below
        // can hold. A max the row learned from the highest RPM seen comes
        // last, behind any source with the game's own.
        private static ITelemetrySource[] BuildSources() => new ITelemetrySource[]
        {
            new GameTelemetryRowSource(learnedMax: false), // the Game Telemetry row (#531), the game's max RPM
            new ForzaUdpTelemetrySource(_forzaPort),     // UDP 5300
            new AssettoCorsaTelemetrySource(),           // acpmf shared memory (AC / ACC)
            new IRacingTelemetrySource(),                // irsdk shared memory
            new RFactor2TelemetrySource(),               // rF2/LMU shared memory (plugin)
            new RaceRoomTelemetrySource(),               // R3E shared memory
            new ScsTruckTelemetrySource(),               // ETS2/ATS shared memory (plugin)
            new MadnessTelemetrySource(),                // AMS2 / Project CARS 2-3 shared memory
            new RFactor1TelemetrySource(),               // rFactor 1 / AMS1 / GSCE shared memory (plugin)
            new CodemastersUdpTelemetrySource(),         // UDP 20777: F1 22 to 25 + DiRT/GRID
            new OutGaugeTelemetrySource(),               // UDP 4444: BeamNG + Live for Speed
            new GameTelemetryRowSource(learnedMax: true),  // the row again, a max RPM it learned
        };

        private static void Start()
        {
            lock (_lock)
            {
                if (_running) return;
                _sources = BuildSources();
                foreach (var s in _sources)
                {
                    if (s is IUdpTelemetrySource udp && _yieldedPorts.Contains(udp.Port)) continue;
                    try { s.Start(); } catch { }
                }
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
                // The Game Telemetry row with no max RPM yet lights no LEDs, but its speed and its road
                // still serve a wheel's centering (#588).
                if (!have && PadForge.Common.Input.Telemetry.GameTelemetryRuntime.TryGetMotion(
                        Environment.TickCount64, out float rowSpeed, out float rowGrip))
                {
                    best = new GameTelemetrySnapshot { Source = "Game Telemetry", Grip = rowGrip };
                    best.SetSpeed(rowSpeed);
                    have = true;
                }
                lock (_snapLock)
                {
                    _current = best;
                    _hasCurrent = have;
                }

                Thread.Sleep(16); // ~60 Hz
            }
        }

        private static void Stop()
        {
            lock (_lock)
            {
                _running = false;
                lock (_snapLock) { _hasCurrent = false; }
                var sources = _sources;
                if (sources != null)
                    foreach (var s in sources) { try { s.Dispose(); } catch { } }
                _sources = null;
            }
        }
    }
}
