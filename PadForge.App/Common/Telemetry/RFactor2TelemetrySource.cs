using System;
using System.IO.MemoryMappedFiles;

namespace PadForge.Common.Telemetry
{
    /// <summary>
    /// rFactor 2 / Le Mans Ultimate telemetry via the de-facto-standard
    /// rF2SharedMemoryMapPlugin (TheIronWolfModding). Reads the LOCAL PLAYER's
    /// vehicle: the Telemetry buffer's <c>mVehicles[]</c> is slot-indexed, not
    /// player-indexed, so index 0 is the player only when alone on track. Racing
    /// against AI / in MP, the player can be at any slot, so the player's
    /// <c>mID</c> is resolved from the Scoring map (which has an <c>mIsPlayer</c>
    /// flag) and matched against the Telemetry vehicles.
    ///
    /// <para>Telemetry layout verified against the plugin's rF2Data.cs (2026-06-02):
    /// header mVersionUpdateBegin@0 / End@4, mNumVehicles@12, mVehicles[]@16,
    /// stride 1888; within a vehicle mID(i32)@0, mElapsedTime(f64)@12,
    /// mEngineRPM(f64)@356, mEngineMaxRPM(f64)@532. Scoring layout computed by
    /// hand from the plugin's rF2State.h (#pragma pack(4), 64-bit, 4-byte long):
    /// every mapped buffer opens with the 8-byte version block, then the 4-byte
    /// mBytesUpdatedHint, so rF2ScoringInfo starts at 12. It is 548 bytes, which
    /// puts mVehicles[] at 560 with a 584-byte stride. Within a vehicle,
    /// mID(i32) is at 0 and mIsPlayer(bool) at 196.</para>
    ///
    /// <para>Speed is the length of <c>mLocalVel</c>, three doubles at 184 of
    /// the vehicle, in m/s. The road under the front wheels is each wheel's
    /// <c>mSurfaceType</c> (byte) at 176 of its <c>rF2Wheel</c>, the wheels
    /// starting at 848 of the vehicle and 260 bytes each, front left first.
    /// The road's wetness is <c>mAvgPathWetness</c> (f64) at 332 of
    /// rF2ScoringInfo, so at 344 of the Scoring map. All by Marshal.OffsetOf
    /// on the plugin's rF2Data.cs, which also gives back the offsets above
    /// (2026-11-19).</para>
    ///
    /// <para>PREREQUISITE: rF2SharedMemoryMapPlugin64.dll installed + enabled. No
    /// plugin = no map = the source stays idle (open backoff).</para>
    /// </summary>
    internal sealed class RFactor2TelemetrySource : ITelemetrySource
    {
        public string Name => "rFactor2/LMU";

        private const string TelemMap = "$rFactor2SMMP_Telemetry$";
        private const string ScoringMap = "$rFactor2SMMP_Scoring$";
        private const int VehBase = 16, VehStride = 1888;
        private const int OffMID = 0, OffRpm = 356, OffMax = 532, OffElapsed = 12;
        private const int OffLocalVel = 184, OffWheels = 848, WheelStride = 260, OffSurfaceType = 176;
        private const int ScoringVehBase = 560, ScoringVehStride = 584, OffIsPlayer = 196;
        private const int ScoringAvgPathWetness = 344;

        private MemoryMappedFile _telMmf, _scMmf;
        private MemoryMappedViewAccessor _telAcc, _scAcc;
        private int _nextOpenTick;
        private double _lastElapsed;
        private int _lastChangeTick;
        private bool _haveElapsed;

        public void Start() { /* lazy open */ }

        private bool EnsureOpen()
        {
            if (_telAcc != null) return true;
            if (unchecked(Environment.TickCount - _nextOpenTick) < 0) return false;
            try
            {
                _telMmf = MemoryMappedFile.OpenExisting(TelemMap, MemoryMappedFileRights.Read);
                _telAcc = _telMmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                _haveElapsed = false;
                // Scoring is best-effort: without it we fall back to vehicle[0].
                try
                {
                    _scMmf = MemoryMappedFile.OpenExisting(ScoringMap, MemoryMappedFileRights.Read);
                    _scAcc = _scMmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                }
                catch { _scAcc = null; }
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
                uint begin = _telAcc.ReadUInt32(0);
                int numVeh = _telAcc.ReadInt32(12);
                if (numVeh <= 0) return false;       // menus / no session
                if (numVeh > 128) numVeh = 128;

                int idx = ResolvePlayerIndex(numVeh); // player slot, or 0 fallback
                int o = VehBase + idx * VehStride;
                double rpm = _telAcc.ReadDouble(o + OffRpm);
                double max = _telAcc.ReadDouble(o + OffMax);
                double elapsed = _telAcc.ReadDouble(o + OffElapsed);
                double vx = _telAcc.ReadDouble(o + OffLocalVel), vy = _telAcc.ReadDouble(o + OffLocalVel + 8),
                    vz = _telAcc.ReadDouble(o + OffLocalVel + 16);
                int surfaceLeft = _telAcc.ReadByte(o + OffWheels + OffSurfaceType);
                int surfaceRight = _telAcc.ReadByte(o + OffWheels + WheelStride + OffSurfaceType);
                uint end = _telAcc.ReadUInt32(4);

                if (begin != end) return false;      // torn write — retry next poll
                if (max <= 0.0) return false;         // engine data not valid yet

                int now = Environment.TickCount;
                if (!_haveElapsed || elapsed != _lastElapsed) { _lastElapsed = elapsed; _lastChangeTick = now; _haveElapsed = true; }
                else if (unchecked(now - _lastChangeTick) > 2000) return false; // paused / frozen

                double wetness = ReadWetness();
                snap = new GameTelemetrySnapshot
                {
                    Rpm = (float)rpm, MaxRpm = (float)max, IdleRpm = 0f, Source = Name,
                    Grip = (float)PadForge.Engine.Steering.RoadGrip.FrontAxle(
                        SurfaceGrips.RFactor(surfaceLeft, wetness), SurfaceGrips.RFactor(surfaceRight, wetness)),
                };
                snap.SetSpeed((float)Math.Sqrt(vx * vx + vy * vy + vz * vz));
                return true;
            }
            catch
            {
                Close();
                _nextOpenTick = Environment.TickCount + 1000;
                return false;
            }
        }

        /// <summary>The main path's average wetness from the Scoring map,
        /// or 0 where the map is not open or the figure is no number.</summary>
        private double ReadWetness()
        {
            if (_scAcc == null) return 0;
            try
            {
                double wetness = _scAcc.ReadDouble(ScoringAvgPathWetness);
                return double.IsFinite(wetness) ? Math.Clamp(wetness, 0.0, 1.0) : 0;
            }
            catch { return 0; }
        }

        // Finds the player's Telemetry slot. Reads the player's mID from Scoring
        // (mIsPlayer==1), then matches it against the Telemetry vehicles' mID.
        // Falls back to slot 0 when Scoring is unavailable or no match is found.
        private int ResolvePlayerIndex(int numVeh)
        {
            if (_scAcc == null) return 0;
            try
            {
                int playerMID = int.MinValue;
                for (int i = 0; i < numVeh; i++)
                {
                    int vo = ScoringVehBase + i * ScoringVehStride;
                    if (_scAcc.ReadByte(vo + OffIsPlayer) == 1) { playerMID = _scAcc.ReadInt32(vo + OffMID); break; }
                }
                if (playerMID == int.MinValue) return 0;
                for (int j = 0; j < numVeh; j++)
                    if (_telAcc.ReadInt32(VehBase + j * VehStride + OffMID) == playerMID) return j;
            }
            catch { /* scoring read hiccup — fall back */ }
            return 0;
        }

        private void Close()
        {
            try { _telAcc?.Dispose(); } catch { }
            try { _scAcc?.Dispose(); } catch { }
            try { _telMmf?.Dispose(); } catch { }
            try { _scMmf?.Dispose(); } catch { }
            _telAcc = null; _scAcc = null; _telMmf = null; _scMmf = null; _haveElapsed = false;
        }

        public void Stop() => Close();
        public void Dispose() => Close();
    }
}
