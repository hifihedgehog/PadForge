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
    /// <para>Offsets by Marshal.OffsetOf on vAzhure's PC2SharedMemory.cs, a C#
    /// port of the game's SMS_MemMapStructs header (2026-11-19):
    /// mVersion(u32)@0, mSpeed(f32, m/s)@6848, mRpm(f32)@6852 (0x1AC4),
    /// mMaxRPM(f32)@6856, mTerrain(u32 a tire, front left first)@7008,
    /// mRainDensity(f32, 0 to 1)@7300, mSequenceNumber(u32)@7320. mRpm@6852
    /// matches the community-documented AMS2 offset.</para>
    ///
    /// <para>The sequence number was read at 7316 until 2026-11-19, counted
    /// from chris-ldgk/rust_ams2_sharedmem's SMS_MemMap.rs. That port has no
    /// mLastOpponentCollisionMagnitude, a float the header keeps between
    /// mLastOpponentCollisionIndex and mBoostActive, so every offset it gives
    /// from there on is 4 short. vAzhure's port and CrewChiefV4's
    /// PCars2Struct.cs both have the field, and Marshal.OffsetOf on
    /// CrewChiefV4's struct gives every offset above, 7320 among them
    /// (2026-11-20). 7316 is mCloudBrightness, the last of the seven
    /// weather floats ahead of the sequence number, and a sky that held
    /// its brightness for a second read as a game that had stopped
    /// writing.</para>
    /// </summary>
    internal sealed class MadnessTelemetrySource : ITelemetrySource
    {
        public string Name => "AMS2/PCARS2";

        private const string MapName = "$pcars2$";
        private const int OffVersion = 0;       // u32 SHARED_MEMORY_VERSION (PCARS2=8, AMS2=13+)
        private const int OffRpm = 6852;        // f32 mRpm
        private const int OffMaxRpm = 6856;     // f32 mMaxRPM
        private const int OffSpeed = 6848;      // f32 mSpeed, m/s
        private const int OffTerrain = 7008;    // u32[4] mTerrain, front left first
        private const int OffRainDensity = 7300; // f32 mRainDensity, 0 to 1
        internal const int OffSequence = 7320;  // u32 mSequenceNumber (advances each write)
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
                float rain = _acc.ReadSingle(OffRainDensity);
                snap = new GameTelemetrySnapshot
                {
                    Rpm = rpm, MaxRpm = max, IdleRpm = 0f, Source = Name,
                    Grip = (float)PadForge.Engine.Steering.RoadGrip.FrontAxle(
                        SurfaceGrips.Madness(_acc.ReadInt32(OffTerrain), rain), SurfaceGrips.Madness(_acc.ReadInt32(OffTerrain + 4), rain)),
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
