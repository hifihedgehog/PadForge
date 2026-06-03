using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PadForge.Common.Telemetry
{
    /// <summary>
    /// EA/Codemasters UDP telemetry on the shared default port 20777, covering two
    /// different wire formats from one socket (they can't both bind 20777, so a
    /// single listener disambiguates per datagram):
    /// <list type="bullet">
    ///   <item><b>F1 23 / F1 24</b> — structured packets, 29-byte header, first u16
    ///   = 2023/2024. Current RPM is <c>CarTelemetryData.m_engineRPM</c> (u16) in
    ///   packet id 6 at <c>29 + player*60 + 16</c>; redline is
    ///   <c>CarStatusData.m_maxRPM</c> (u16) in packet id 7 at <c>29 + player*55 +
    ///   17</c> (idle at +19). The two values arrive in separate packets, so both
    ///   are cached.</item>
    ///   <item><b>DiRT Rally / DiRT Rally 2.0</b> — classic flat-float datagram,
    ///   exactly 264 bytes (<c>extradata=3</c>): rpm float @148, max @252, idle @256
    ///   (each ×10 for absolute RPM; the ×10 cancels in the fraction).</item>
    /// </list>
    /// Offsets verified against the EA F1 24 UDP spec + accepted parsers and the
    /// Codemasters EGO/D-BOX format (2026-06-02). F1 22 (format 2022) has a
    /// different layout and is rejected. EA WRC UDP was not confirmed and is not
    /// claimed here.
    /// </summary>
    internal sealed class CodemastersUdpTelemetrySource : ITelemetrySource
    {
        public string Name => "F1 / Codemasters";

        private const int Port = 20777;
        private const int F1Header = 29;

        private UdpClient _udp;
        private Thread _thread;
        private volatile bool _run;
        private volatile float _rpm, _maxRpm, _idleRpm;
        private volatile int _lastTick;
        private volatile string _label = "Codemasters UDP";

        public void Start()
        {
            if (_run) return;
            try { _udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port)); }
            catch { _udp = null; return; } // 20777 already bound (e.g. SimHub) — stay idle
            _run = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "CodemastersTelemetry" };
            _thread.Start();
        }

        private void Loop()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (_run)
            {
                try
                {
                    byte[] b = _udp.Receive(ref any);
                    if (b.Length == 264) ParseDirt(b);
                    else if (b.Length >= F1Header) ParseF1(b);
                }
                catch (SocketException) { if (!_run) break; }
                catch (ObjectDisposedException) { break; }
                catch { /* malformed datagram — keep listening */ }
            }
        }

        // F1 23/24: reject other formats; cache RPM (id 6) + maxRPM/idle (id 7).
        private void ParseF1(byte[] b)
        {
            ushort fmt = BitConverter.ToUInt16(b, 0);
            if (fmt != 2023 && fmt != 2024) return;
            byte packetId = b[6];
            byte player = b[27];
            if (player == 255) return; // no player car

            if (packetId == 6)
            {
                int off = F1Header + player * 60 + 16;
                if (off + 2 > b.Length) return;
                _rpm = BitConverter.ToUInt16(b, off);
                _label = "F1 " + fmt + " UDP";
                _lastTick = Environment.TickCount;
            }
            else if (packetId == 7)
            {
                int s = F1Header + player * 55;
                if (s + 21 > b.Length) return;
                _maxRpm = BitConverter.ToUInt16(b, s + 17);
                _idleRpm = BitConverter.ToUInt16(b, s + 19);
                _label = "F1 " + fmt + " UDP";
                _lastTick = Environment.TickCount;
            }
        }

        // DiRT Rally (2.0) classic flat-float, extradata=3 (264 bytes).
        private void ParseDirt(byte[] b)
        {
            float rpm = BitConverter.ToSingle(b, 148) * 10f;
            float max = BitConverter.ToSingle(b, 252) * 10f;
            float idle = BitConverter.ToSingle(b, 256) * 10f;
            if (max <= 0f || float.IsNaN(max) || float.IsNaN(rpm)) return;
            _rpm = rpm; _maxRpm = max; _idleRpm = idle;
            _label = "DiRT Rally 2.0 UDP";
            _lastTick = Environment.TickCount;
        }

        public bool TryGetSnapshot(out GameTelemetrySnapshot snap)
        {
            snap = default;
            if (_udp == null) return false;
            if (unchecked(Environment.TickCount - _lastTick) > 1000) return false; // stale
            if (_maxRpm <= 0f) return false;
            snap = new GameTelemetrySnapshot { Rpm = _rpm, MaxRpm = _maxRpm, IdleRpm = _idleRpm, Source = _label };
            return true;
        }

        public void Stop()
        {
            _run = false;
            try { _udp?.Close(); } catch { }
            try { _thread?.Join(200); } catch { }
            _udp = null; _thread = null;
        }

        public void Dispose() => Stop();
    }
}
