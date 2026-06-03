using System;
using System.IO.MemoryMappedFiles;

namespace PadForge.Common.Telemetry
{
    /// <summary>
    /// Euro Truck Simulator 2 / American Truck Simulator telemetry via the
    /// RenCloud scs-sdk-plugin shared memory map <c>Local\SCSTelemetry</c> (one
    /// plugin + one map serves both games). Reads truck engine RPM + governed max
    /// RPM by fixed offset, with an update-counter freshness gate.
    ///
    /// <para>Offsets verified against RenCloud/scs-sdk-plugin
    /// scs-telemetry-common.hpp (PLUGIN_REVID 12), 2026-06-02: sdkActive(bool)@0,
    /// time(u64 update counter)@8, config_f.engineRpmMax(float)@740,
    /// truck_f.engineRpm(float)@952. (The legacy Funbit map
    /// <c>Local\Ets2TelemetryServer</c> has a different layout and is NOT used.)</para>
    ///
    /// <para>PREREQUISITE: the RenCloud plugin DLL must be in
    /// <c>&lt;game&gt;\bin\win_x64\plugins\</c>. No plugin = no map = the source
    /// stays idle via the open backoff.</para>
    /// </summary>
    internal sealed class ScsTruckTelemetrySource : ITelemetrySource
    {
        public string Name => "ETS2/ATS (SCS)";

        private const string MapName = @"Local\SCSTelemetry";
        private const int OffSdkActive = 0;   // bool
        private const int OffTime = 8;        // u64 update counter
        private const int OffRpmMax = 740;    // float config_f.engineRpmMax
        private const int OffRpm = 952;       // float truck_f.engineRpm

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _acc;
        private int _nextOpenTick;
        private ulong _lastTime;
        private int _staleCount;
        private bool _haveTime;

        public void Start() { /* lazy open */ }

        private bool EnsureOpen()
        {
            if (_acc != null) return true;
            if (unchecked(Environment.TickCount - _nextOpenTick) < 0) return false;
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                _acc = _mmf.CreateViewAccessor(0, 1024, MemoryMappedFileAccess.Read); // RPM@952 well within 1KB
                _haveTime = false;
                _staleCount = 0;
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
                bool active = _acc.ReadByte(OffSdkActive) != 0;
                ulong t = _acc.ReadUInt64(OffTime);
                float rpm = _acc.ReadSingle(OffRpm);
                float max = _acc.ReadSingle(OffRpmMax);

                if (_haveTime && t == _lastTime)
                {
                    if (++_staleCount > 120) return false; // ~2s frozen @60Hz
                }
                else { _staleCount = 0; _lastTime = t; _haveTime = true; }

                if (!active || max <= 0f) return false; // engineRpmMax is 0 in menus
                snap = new GameTelemetrySnapshot { Rpm = rpm, MaxRpm = max, IdleRpm = 0f, Source = Name };
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
            _acc = null; _mmf = null; _haveTime = false;
        }

        public void Stop() => Close();
        public void Dispose() => Close();
    }
}
