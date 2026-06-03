using System;
using System.IO.MemoryMappedFiles;

namespace PadForge.Common.Telemetry
{
    /// <summary>
    /// rFactor 1 / Automobilista 1 / Game Stock Car Extreme (the ISImotor "rFactor
    /// 1" engine) telemetry via the dallongo shared-memory plugin map
    /// <c>$rFactorShared$</c>. The plugin packs its struct with <c>#pragma pack(1)</c>
    /// (no padding), so the offsets are plain field sums.
    ///
    /// <para>Offsets computed from dallongo/rFactorSharedMemoryMap
    /// (rfSharedStruct.hpp, pack(1)) and corroborated by Spacefreak18/simapi
    /// (rfdata.h, same struct), 2026-06-02: engineRPM(f32)@188, engineMaxRPM(f32)@228.</para>
    ///
    /// <para>FRAGMENTED LANDSCAPE: multiple rF1 plugins publish the same
    /// <c>$rFactorShared$</c> map with DIFFERENT layouts (CrewChief's variant drops
    /// the version[8] header and adds a vehicleName[64] field, shifting engineRPM
    /// to 244). These offsets are for the dallongo/simapi family. To avoid lighting
    /// LEDs from a wrong-layout plugin's garbage, the read is gated on a sane RPM
    /// range — a mismatched layout fails the gate and the source stays silent
    /// rather than wrong.</para>
    ///
    /// <para>PREREQUISITE: a dallongo-family rFactor shared-memory plugin installed
    /// in the game's Plugins folder. No plugin = no map = the source stays idle.</para>
    /// </summary>
    internal sealed class RFactor1TelemetrySource : ITelemetrySource
    {
        public string Name => "rFactor1/AMS1";

        private const string MapName = "$rFactorShared$";
        private const int OffEngineRpm = 188;     // f32 engineRPM
        private const int OffEngineMaxRpm = 228;  // f32 engineMaxRPM
        private const int MapView = 512;          // covers both fields

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _acc;
        private int _nextOpenTick;
        private float _lastRpm;
        private int _lastChangeTick;
        private bool _haveRpm;

        public void Start() { /* lazy open */ }

        private bool EnsureOpen()
        {
            if (_acc != null) return true;
            if (unchecked(Environment.TickCount - _nextOpenTick) < 0) return false;
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                _acc = _mmf.CreateViewAccessor(0, MapView, MemoryMappedFileAccess.Read);
                _haveRpm = false;
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
                float rpm = _acc.ReadSingle(OffEngineRpm);
                float max = _acc.ReadSingle(OffEngineMaxRpm);
                // Sane-range gate: rejects a wrong-layout plugin's garbage so it
                // degrades to no-LEDs instead of wrong-LEDs (see class remarks).
                if (float.IsNaN(max) || float.IsNaN(rpm)) return false;
                if (max < 1000f || max > 25000f || rpm < 0f || rpm > 25000f) return false;

                // rF1 exposes no frame counter; treat a frozen RPM as stale (paused /
                // game closed but map left mapped). A real engine always jitters.
                int now = Environment.TickCount;
                if (!_haveRpm || rpm != _lastRpm) { _lastRpm = rpm; _lastChangeTick = now; _haveRpm = true; }
                else if (unchecked(now - _lastChangeTick) > 2000) return false;

                if (rpm < 0f) rpm = 0f;
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
            _acc = null; _mmf = null; _haveRpm = false;
        }

        public void Stop() => Close();
        public void Dispose() => Close();
    }
}
