using System;
using System.IO.MemoryMappedFiles;

namespace PadForge.Common.Telemetry
{
    /// <summary>
    /// RaceRoom Racing Experience (R3E) telemetry via the official Sector3 shared
    /// memory map <c>$R3E</c> (bare, session-local name). The engine-speed fields
    /// are in radians/sec, so they are converted to RPM. Redline prefers the
    /// upshift point, falling back to the hard rev limiter.
    ///
    /// <para>Offsets verified via Marshal.OffsetOf on the current official
    /// <c>R3E.Data.Shared</c> struct (VersionMajor 3, size 43996), 2026-06-02:
    /// VersionMajor@0, GameInMenus@24, EngineRps@1396, MaxEngineRps@1400,
    /// UpshiftRps@1404 (all rad/s, -1 = N/A). Gated on VersionMajor==3 because
    /// these offsets are version-sensitive.</para>
    ///
    /// <para>Speed is <c>CarSpeed</c> (float, m/s) at 1392. The material under
    /// each tire is <c>TireOnMtrl</c>, four ints from 1952 with the front left
    /// first, read by the struct's own MtrlType. Both by Marshal.OffsetOf on
    /// r3e-api's R3E.cs, which gives the same 43996 bytes and the offsets
    /// above (2026-11-19).</para>
    /// </summary>
    internal sealed class RaceRoomTelemetrySource : ITelemetrySource
    {
        public string Name => "RaceRoom";

        private const string MapName = "$R3E";
        private const int OffVersionMajor = 0;
        private const int OffGameInMenus = 24;
        private const int OffEngineRps = 1396;
        private const int OffMaxEngineRps = 1400;
        private const int OffUpshiftRps = 1404;
        private const int OffCarSpeed = 1392;
        private const int OffTireOnMtrl = 1952;
        private const float RpsToRpm = 60f / (2f * MathF.PI); // 9.549296586

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _acc;
        private int _nextOpenTick;

        public void Start() { /* lazy open */ }

        private bool EnsureOpen()
        {
            if (_acc != null) return true;
            if (unchecked(Environment.TickCount - _nextOpenTick) < 0) return false;
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                _acc = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
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
                if (_acc.ReadInt32(OffVersionMajor) != 3) return false; // struct-version guard
                if (_acc.ReadInt32(OffGameInMenus) != 0) return false;  // not live in menus

                float engRps = _acc.ReadSingle(OffEngineRps);
                if (engRps <= 0f) return false; // -1 sentinel / engine off
                float upRps = _acc.ReadSingle(OffUpshiftRps);
                float maxRps = _acc.ReadSingle(OffMaxEngineRps);
                float redRps = upRps > 0f ? upRps : maxRps; // upshift preferred, limiter fallback
                if (redRps <= 0f) return false;

                snap = new GameTelemetrySnapshot
                {
                    Rpm = engRps * RpsToRpm,
                    MaxRpm = redRps * RpsToRpm,
                    IdleRpm = 0f,
                    Source = Name,
                    Grip = (float)PadForge.Engine.Steering.RoadGrip.FrontAxle(
                        SurfaceGrips.RaceRoom(_acc.ReadInt32(OffTireOnMtrl)), SurfaceGrips.RaceRoom(_acc.ReadInt32(OffTireOnMtrl + 4))),
                };
                // -1 where the game has no speed to give.
                float speed = _acc.ReadSingle(OffCarSpeed);
                if (speed >= 0f) snap.SetSpeed(speed);
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
            _acc = null; _mmf = null;
        }

        public void Stop() => Close();
        public void Dispose() => Close();
    }
}
