using System;
using System.IO.MemoryMappedFiles;

namespace PadForge.Common.Telemetry
{
    /// <summary>
    /// Automobilista 2 / Project CARS 2 / Project CARS 3 (Slightly Mad Studios
    /// "Madness" engine) telemetry via the shared memory map <c>$pcars2$</c>. One
    /// map serves the whole family. Engine RPM and max RPM sit in the single-car
    /// block near the struct's middle, before any version-specific additions, so
    /// the offsets are stable across PCARS2 (mVersion 8) and the AMS2 revisions.
    ///
    /// <para>Offsets computed from chris-ldgk/rust_ams2_sharedmem (SMS_MemMap.rs,
    /// the official SMS_MemMapStructs layout), 2026-06-02: mVersion(u32)@0,
    /// mRpm(f32)@6852 (0x1AC4), mMaxRPM(f32)@6856, mSequenceNumber(u32)@7316.
    /// mRpm@6852 matches the community-documented AMS2 offset.</para>
    /// </summary>
    internal sealed class MadnessTelemetrySource : ITelemetrySource
    {
        public string Name => "AMS2/PCARS2";

        private const string MapName = "$pcars2$";
        private const int OffVersion = 0;       // u32 SHARED_MEMORY_VERSION (PCARS2=8, AMS2=13+)
        private const int OffRpm = 6852;        // f32 mRpm
        private const int OffMaxRpm = 6856;     // f32 mMaxRPM
        private const int OffSequence = 7316;   // u32 mSequenceNumber (advances each write)
        private const int MapView = 8192;       // covers through mSequenceNumber

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _acc;
        private int _nextOpenTick;
        private uint _lastSeq;
        private int _lastChangeTick;
        private bool _haveSeq;

        public void Start() { /* lazy open */ }

        private bool EnsureOpen()
        {
            if (_acc != null) return true;
            if (unchecked(Environment.TickCount - _nextOpenTick) < 0) return false;
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                _acc = _mmf.CreateViewAccessor(0, MapView, MemoryMappedFileAccess.Read);
                _haveSeq = false;
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
                uint version = _acc.ReadUInt32(OffVersion);
                if (version < 8 || version > 64) return false; // sane PCARS2+/AMS2 range

                // Freshness: mSequenceNumber advances each write; frozen for ~1s = stale.
                uint seq = _acc.ReadUInt32(OffSequence);
                int now = Environment.TickCount;
                if (!_haveSeq || seq != _lastSeq) { _lastSeq = seq; _lastChangeTick = now; _haveSeq = true; }
                else if (unchecked(now - _lastChangeTick) > 1000) return false;

                float rpm = _acc.ReadSingle(OffRpm);
                float max = _acc.ReadSingle(OffMaxRpm);
                if (max <= 0f || float.IsNaN(max) || float.IsNaN(rpm)) return false; // menus / no car
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
            _acc = null; _mmf = null; _haveSeq = false;
        }

        public void Stop() => Close();
        public void Dispose() => Close();
    }
}
