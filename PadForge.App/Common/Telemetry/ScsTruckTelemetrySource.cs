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
    /// <para>Speed is truck_f.speed (float, m/s, negative in reverse) at 948,
    /// the field before the RPM. The ground under a wheel is
    /// truck_ui.truck_wheelSubstance, sixteen unsigned ints from 120 in the
    /// header's second zone (it starts at 40, with sixteen ints and the four
    /// that open truck_ui ahead of the array), each a place in the list of
    /// substance names, twenty-five of 64 bytes from 4400, the header's 13th
    /// zone. Wheels 0 and 1 are the front axle's (2026-11-19).</para>
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
        private const int OffSpeed = 948;     // float truck_f.speed
        private const int OffWheelSubstance = 120; // uint truck_ui.truck_wheelSubstance[16]
        private const int OffSubstances = 4400, SubstanceCount = 25, SubstanceBytes = 64;
        private const int MapView = OffSubstances + SubstanceCount * SubstanceBytes;
        private readonly byte[] _substance = new byte[SubstanceBytes];

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
                _acc = _mmf.CreateViewAccessor(0, MapView, MemoryMappedFileAccess.Read); // through the substance names
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
                snap = new GameTelemetrySnapshot
                {
                    Rpm = rpm, MaxRpm = max, IdleRpm = 0f, Source = Name,
                    Grip = (float)PadForge.Engine.Steering.RoadGrip.FrontAxle(WheelGrip(0), WheelGrip(1)),
                };
                snap.SetSpeed(_acc.ReadSingle(OffSpeed));
                return true;
            }
            catch
            {
                Close();
                _nextOpenTick = Environment.TickCount + 1000;
                return false;
            }
        }

        /// <summary>The grip of the ground under one wheel, by the name of
        /// its substance, or 0 where the wheel names none on the list.</summary>
        private double WheelGrip(int wheel)
        {
            uint index = _acc.ReadUInt32(OffWheelSubstance + wheel * 4);
            if (index >= SubstanceCount) return 0;
            _acc.ReadArray(OffSubstances + (int)index * SubstanceBytes, _substance, 0, SubstanceBytes);
            return SurfaceGrips.Scs(_substance);
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
