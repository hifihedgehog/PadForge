using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PadForge.Common.Telemetry
{
    /// <summary>
    /// Forza "Data Out" UDP telemetry (Forza Motorsport 7 / 2023, Horizon 4 / 5).
    /// The game broadcasts a fixed binary struct to a user-set UDP port. The RPM
    /// fields live in the common "Sled" prefix, so they sit at the same offsets
    /// across every Forza variant (the per-title gap only shifts the later "Dash"
    /// block, which this source doesn't read):
    /// <list type="bullet">
    ///   <item><c>int IsRaceOn</c> @ 0 — 0 in menus / paused.</item>
    ///   <item><c>float EngineMaxRpm</c> @ 8.</item>
    ///   <item><c>float EngineIdleRpm</c> @ 12.</item>
    ///   <item><c>float CurrentEngineRpm</c> @ 16.</item>
    /// </list>
    /// Offsets verified against geeooff/forza-data-web and austinbaccus/
    /// forza-telemetry (independent C# readers, 2026-06-02). The user enables
    /// Data Out and points it at <see cref="TelemetryHub.DefaultForzaPort"/>.
    /// </summary>
    internal sealed class ForzaUdpTelemetrySource : ITelemetrySource
    {
        public string Name => "Forza";

        private readonly int _port;
        private UdpClient _udp;
        private Thread _thread;
        private volatile bool _run;

        // Latest decoded values, published from the receive thread.
        private volatile float _rpm, _maxRpm, _idleRpm;
        private volatile bool _inRace;
        private volatile int _lastTick;  // Environment.TickCount of last valid packet

        public ForzaUdpTelemetrySource(int port) { _port = port; }

        public void Start()
        {
            if (_run) return;
            try
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, _port));
            }
            catch
            {
                // Port already bound (e.g. SimHub listening) or unavailable —
                // the source just stays idle, FFB/LED falls back gracefully.
                _udp = null;
                return;
            }
            _run = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "ForzaTelemetry" };
            _thread.Start();
        }

        private void Loop()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (_run)
            {
                try
                {
                    byte[] buf = _udp.Receive(ref any); // blocks until a datagram or socket close
                    if (buf.Length < 20) continue;      // need bytes 0..19
                    int isRaceOn = BitConverter.ToInt32(buf, 0);
                    float maxRpm = BitConverter.ToSingle(buf, 8);
                    float idleRpm = BitConverter.ToSingle(buf, 12);
                    float rpm = BitConverter.ToSingle(buf, 16);
                    if (maxRpm <= 0f || float.IsNaN(maxRpm) || float.IsNaN(rpm)) continue;
                    _maxRpm = maxRpm;
                    _idleRpm = idleRpm;
                    _rpm = rpm;
                    _inRace = isRaceOn != 0;
                    _lastTick = Environment.TickCount;
                }
                catch (SocketException) { if (!_run) break; }
                catch (ObjectDisposedException) { break; }
                catch { /* malformed datagram — ignore, keep listening */ }
            }
        }

        public bool TryGetSnapshot(out GameTelemetrySnapshot snap)
        {
            snap = default;
            if (_udp == null) return false;
            if (unchecked(Environment.TickCount - _lastTick) > 1000) return false; // stale
            if (!_inRace) return false;
            snap = new GameTelemetrySnapshot
            {
                Rpm = _rpm,
                MaxRpm = _maxRpm,
                IdleRpm = _idleRpm,
                Source = Name,
            };
            return true;
        }

        public void Stop()
        {
            _run = false;
            try { _udp?.Close(); } catch { }
            try { _thread?.Join(200); } catch { }
            _udp = null;
            _thread = null;
        }

        public void Dispose() => Stop();
    }
}
