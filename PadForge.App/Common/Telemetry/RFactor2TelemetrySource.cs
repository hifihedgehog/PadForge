using System;
using System.IO.MemoryMappedFiles;

namespace PadForge.Common.Telemetry
{
    /// <summary>
    /// rFactor 2 / Le Mans Ultimate telemetry via the de-facto-standard
    /// rF2SharedMemoryMapPlugin (TheIronWolfModding) shared memory map
    /// <c>$rFactor2SMMP_Telemetry$</c> (bare, session-local name). Reads the local
    /// player's vehicle (index 0 in single-player / hotlap) engine RPM + rev limit
    /// by direct offset — the full buffer is ~236 KB (128 vehicles × 1888), so
    /// marshaling it at poll rate is avoided.
    ///
    /// <para>Layout verified against the plugin's own C# (rF2data.cs) + consumers
    /// (CrewChiefV4, pyRfactor2SharedMemory), 2026-06-02: header u32
    /// mVersionUpdateBegin@0 / mVersionUpdateEnd@4, i32 mNumVehicles@12,
    /// rF2VehicleTelemetry mVehicles[]@16, stride 1888; within a vehicle
    /// mElapsedTime(f64)@12, mEngineRPM(f64)@356, mEngineMaxRPM(f64)@532.</para>
    ///
    /// <para>PREREQUISITE: rF2SharedMemoryMapPlugin64.dll must be installed in
    /// <c>&lt;game&gt;\Bin64\Plugins</c> and enabled. No plugin = no map = the
    /// source stays idle (OpenExisting throws, handled by the open backoff).</para>
    /// </summary>
    internal sealed class RFactor2TelemetrySource : ITelemetrySource
    {
        public string Name => "rFactor2/LMU";

        private const string MapName = "$rFactor2SMMP_Telemetry$";
        private const int VehBase = 16, VehStride = 1888;
        private const int OffRpm = 356, OffMax = 532, OffElapsed = 12;
        private const int MapLen = VehBase + VehStride; // header + vehicle[0]

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _acc;
        private int _nextOpenTick;
        private double _lastElapsed;
        private int _lastChangeTick;
        private bool _haveElapsed;

        public void Start() { /* lazy open */ }

        private bool EnsureOpen()
        {
            if (_acc != null) return true;
            if (unchecked(Environment.TickCount - _nextOpenTick) < 0) return false;
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                _acc = _mmf.CreateViewAccessor(0, MapLen, MemoryMappedFileAccess.Read);
                _haveElapsed = false;
                return true;
            }
            catch
            {
                Close();
                _nextOpenTick = Environment.TickCount + 1000;
                return false;
            }
        }

        public bool TryGetSnapshot(out GameTelemetrySnapshot snap)
        {
            snap = default;
            if (!EnsureOpen()) return false;
            try
            {
                uint begin = _acc.ReadUInt32(0);
                if (_acc.ReadInt32(12) <= 0) return false; // mNumVehicles — menus / no session

                int o = VehBase; // vehicle[0] = local player (SP / hotlap)
                double rpm = _acc.ReadDouble(o + OffRpm);
                double max = _acc.ReadDouble(o + OffMax);
                double elapsed = _acc.ReadDouble(o + OffElapsed);
                uint end = _acc.ReadUInt32(4);

                if (begin != end) return false; // torn write — skip, retry next poll
                if (max <= 0.0) return false;    // engine data not valid yet

                int now = Environment.TickCount;
                if (!_haveElapsed || elapsed != _lastElapsed)
                {
                    _lastElapsed = elapsed; _lastChangeTick = now; _haveElapsed = true;
                }
                else if (unchecked(now - _lastChangeTick) > 2000)
                {
                    return false; // paused / frozen
                }

                snap = new GameTelemetrySnapshot
                {
                    Rpm = (float)rpm,
                    MaxRpm = (float)max,
                    IdleRpm = 0f,
                    Source = Name,
                };
                return true;
            }
            catch
            {
                Close();
                _nextOpenTick = Environment.TickCount + 1000;
                return false;
            }
        }

        private void Close()
        {
            try { _acc?.Dispose(); } catch { }
            try { _mmf?.Dispose(); } catch { }
            _acc = null; _mmf = null; _haveElapsed = false;
        }

        public void Stop() => Close();
        public void Dispose() => Close();
    }
}
