using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PadForge.Common.Telemetry
{
    /// <summary>
    /// OutGauge UDP telemetry — used natively by Live for Speed and emitted in the
    /// same format by BeamNG.drive. One source serves both. The packet has no
    /// max-RPM field, so the redline is derived from a running session peak (the
    /// strip self-calibrates after the engine is revved out once).
    ///
    /// <para>Layout verified against the LFS OutGauge spec via
    /// alexmcbride/insimdotnet (OutGaugePack.cs) + fuelsoft/out-gauge-cluster,
    /// 2026-06-02: Time(u32)@0, Car[4]@4, Flags(u16)@8, Gear@10, PLID@11,
    /// Speed(f32)@12, RPM(f32)@16, ... packet is 92 bytes (no OutGaugeID) or 96
    /// (with ID). RPM is already rev/min.</para>
    ///
    /// <para>Default port 4444 (user-configurable in each game's OutGauge config).</para>
    /// </summary>
    internal sealed class OutGaugeTelemetrySource : ITelemetrySource
    {
        public string Name => "OutGauge";

        private const int RpmOffset = 16;

        private readonly int _port;
        private UdpClient _udp;
        private Thread _thread;
        private volatile bool _run;
        private volatile float _rpm, _peak;
        private volatile int _lastTick;

        public OutGaugeTelemetrySource(int port = 4444) { _port = port; }

        public void Start()
        {
            if (_run) return;
            try { _udp = new UdpClient(new IPEndPoint(IPAddress.Any, _port)); }
            catch { _udp = null; return; } // port already bound — stay idle
            _run = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "OutGaugeTelemetry" };
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
                    if (b.Length != 92 && b.Length != 96) continue; // OutGauge with/without ID
                    float rpm = BitConverter.ToSingle(b, RpmOffset);
                    if (float.IsNaN(rpm) || rpm < 0f || rpm > 30000f) continue;
                    _rpm = rpm;
                    if (rpm > _peak) _peak = rpm;
                    _lastTick = Environment.TickCount;
                }
                catch (SocketException) { if (!_run) break; }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        }

        public bool TryGetSnapshot(out GameTelemetrySnapshot snap)
        {
            snap = default;
            if (_udp == null) return false;
            if (unchecked(Environment.TickCount - _lastTick) > 1000) return false; // stale
            // No max-RPM in OutGauge: redline ~= the highest RPM seen this session,
            // a touch above the current value so a fresh engine still lights LEDs.
            float max = Math.Max(_peak, _rpm * 1.05f);
            if (max <= 0f) return false;
            snap = new GameTelemetrySnapshot { Rpm = _rpm, MaxRpm = max, IdleRpm = 0f, Source = Name };
            return true;
        }

        public void Stop()
        {
            _run = false;
            try { _udp?.Close(); } catch { }
            try { _thread?.Join(200); } catch { }
            _udp = null; _thread = null;
            _peak = 0f;
        }

        public void Dispose() => Stop();
    }
}
