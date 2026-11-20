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
    ///   <item><b>F1 22 to F1 25</b>: structured packets, first u16 the format,
    ///   2022 to 2025. Current RPM is <c>CarTelemetryData.m_engineRPM</c> (u16) in
    ///   packet id 6 at <c>header + player*60 + 16</c>. Redline is
    ///   <c>CarStatusData.m_maxRPM</c> (u16) in packet id 7 at <c>header +
    ///   player*entry + 17</c> (idle at +19), with a 24-byte header and 47-byte
    ///   entries in format 2022 and 29 and 55 after (<see cref="ReadF1"/>). The
    ///   two values arrive in separate packets, so both are cached.</item>
    ///   <item><b>DiRT Rally 2.0 / DiRT Rally / DiRT 4 / GRID</b> — the classic
    ///   Codemasters flat-float datagram, exactly 264 bytes (<c>extradata=3</c>):
    ///   rpm float @148, max @252, idle @256 (each ×10 for absolute RPM; the ×10
    ///   cancels in the fraction). These titles share the byte layout, so one path
    ///   covers them all when each is configured to <c>extradata=3</c>.</item>
    /// </list>
    /// Offsets verified against the EA F1 24 UDP spec + accepted parsers and the
    /// Codemasters EGO/D-BOX format (2026-06-02), and the F1 layouts against
    /// vAzhure F12022.cs to F12025.cs (2026-10-27). EA WRC UDP was not confirmed
    /// and is not claimed here.
    ///
    /// <para>Speed: F1's <c>m_speed</c> (u16, km/h) opens the player's Car
    /// Telemetry entry, and its <c>m_surfaceType[4]</c> closes it at 56, in the
    /// order rear left, rear right, front left, front right (f1-24-udp
    /// PacketCarTelemetryData.h and its README, "What is the order of the
    /// wheel arrays?"). The flat-float datagram's speed is the float at 28, in
    /// m/s (CMTelemetry codemasters.h), and it has no word on the road.</para>
    /// </summary>
    internal sealed class CodemastersUdpTelemetrySource : IUdpTelemetrySource
    {
        public string Name => "F1 / Codemasters";

        private const int ListenPort = 20777;
        private const int F1Header = 29;
        private const int F1_22Header = 24;

        public int Port => ListenPort;

        private UdpClient _udp;
        private Thread _thread;
        private volatile bool _run;
        private volatile float _rpm, _maxRpm, _idleRpm, _speed, _grip;
        private volatile bool _hasSpeed;
        private volatile int _lastTick;
        private volatile string _label = "Codemasters UDP";

        public void Start()
        {
            if (_run) return;
            try
            {
                // Every address, which the test host binds none of (NetworkListenerGate).
                PadForge.Engine.Common.NetworkListenerGate.Enter(IPAddress.Any);
                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, ListenPort));
            }
            catch { _udp = null; return; } // 20777 is already bound, by SimHub for one. Stay idle.
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
                    else if (b.Length >= F1_22Header) ParseF1(b);
                }
                catch (SocketException) { if (!_run) break; }
                catch (ObjectDisposedException) { break; }
                catch { /* malformed datagram — keep listening */ }
            }
        }

        // Cache RPM (id 6) + maxRPM/idle (id 7).
        private void ParseF1(byte[] b)
        {
            var read = ReadF1(b, out int fmt, out float rpm, out float max, out float idle);
            if (read == F1Read.None) return;
            if (read == F1Read.Rpm)
            {
                _rpm = rpm;
                ReadF1Motion(b, out float speed, out float grip);
                _speed = speed;
                _hasSpeed = true;
                _grip = grip;
            }
            else { _maxRpm = max; _idleRpm = idle; }
            _label = "F1 " + fmt + " UDP";
            _lastTick = Environment.TickCount;
        }

        /// <summary>What one F1 packet gave the lights.</summary>
        internal enum F1Read { None, Rpm, MaxAndIdle }

        /// <summary>Formats 2022 to 2025, the ones the Game Telemetry row
        /// reads. Format 2022 has a 24-byte header (packet id at 5, player
        /// car at 22) and 47-byte Car Status entries. The later formats have
        /// a 29-byte header (id at 6, player at 27) and 55-byte entries. Car
        /// Telemetry entries are 60 bytes with the RPM at 16, and Car Status
        /// has the max RPM at 17 and idle at 19, in all four (vAzhure
        /// F12022.cs to F12025.cs). A packet must have its id's size in its
        /// format (TelemetryDecoders.F1PacketSize), so no other game's
        /// datagram reads as F1.</summary>
        internal static F1Read ReadF1(byte[] b, out int format, out float rpm, out float maxRpm, out float idleRpm)
        {
            rpm = maxRpm = idleRpm = 0f;
            format = b.Length >= 2 ? BitConverter.ToUInt16(b, 0) : 0;
            bool f1_22 = format == 2022;
            int header = f1_22 ? F1_22Header : F1Header;
            if (b.Length < header) return F1Read.None;
            byte packetId = b[f1_22 ? 5 : 6];
            if (packetId is not (6 or 7)
                || b.Length != PadForge.Common.Input.Telemetry.TelemetryDecoders.F1PacketSize(format, packetId))
                return F1Read.None;
            byte player = b[f1_22 ? 22 : 27];
            if (player >= 22) return F1Read.None; // 255: no player car

            if (packetId == 6)
            {
                rpm = BitConverter.ToUInt16(b, header + player * 60 + 16);
                return F1Read.Rpm;
            }
            int s = header + player * (f1_22 ? 47 : 55);
            maxRpm = BitConverter.ToUInt16(b, s + 17);
            idleRpm = BitConverter.ToUInt16(b, s + 19);
            return F1Read.MaxAndIdle;
        }

        /// <summary>The speed in m/s and the front wheels' grip from a Car
        /// Telemetry packet <see cref="ReadF1"/> has taken the RPM from, so
        /// its format, its size and its player car are already known
        /// good.</summary>
        internal static void ReadF1Motion(byte[] b, out float speed, out float grip)
        {
            bool f1_22 = BitConverter.ToUInt16(b, 0) == 2022;
            int entry = (f1_22 ? F1_22Header : F1Header) + b[f1_22 ? 22 : 27] * 60;
            speed = BitConverter.ToUInt16(b, entry) / 3.6f;
            grip = (float)PadForge.Engine.Steering.RoadGrip.FrontAxle(SurfaceGrips.F1(b[entry + 58]), SurfaceGrips.F1(b[entry + 59]));
        }

        // DiRT Rally (2.0) classic flat-float, extradata=3 (264 bytes).
        private void ParseDirt(byte[] b)
        {
            float rpm = BitConverter.ToSingle(b, 148) * 10f;
            float max = BitConverter.ToSingle(b, 252) * 10f;
            float idle = BitConverter.ToSingle(b, 256) * 10f;
            if (max <= 0f || float.IsNaN(max) || float.IsNaN(rpm)) return;
            float speed = BitConverter.ToSingle(b, 28);
            _speed = speed;
            _hasSpeed = float.IsFinite(speed);
            _grip = 0f;
            _rpm = rpm; _maxRpm = max; _idleRpm = idle;
            _label = "DiRT/GRID UDP";
            _lastTick = Environment.TickCount;
        }

        public bool TryGetSnapshot(out GameTelemetrySnapshot snap)
        {
            snap = default;
            if (_udp == null) return false;
            if (unchecked(Environment.TickCount - _lastTick) > 1000) return false; // stale
            if (_maxRpm <= 0f) return false;
            snap = new GameTelemetrySnapshot { Rpm = _rpm, MaxRpm = _maxRpm, IdleRpm = _idleRpm, Source = _label, Grip = _grip };
            if (_hasSpeed) snap.SetSpeed(_speed);
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
