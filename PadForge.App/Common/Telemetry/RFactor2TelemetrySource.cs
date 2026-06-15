using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

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
    /// mEngineRPM(f64)@356, mEngineMaxRPM(f64)@532. Scoring layout (the player
    /// resolution) uses the plugin's own struct definitions marshalled below, so
    /// the array base and field offsets are computed by the runtime, not by hand.</para>
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
        private const int ScoringHeader = 12; // begin + end + bytesHint (3 ints) before mScoringInfo

        // The plugin's Scoring structs, copied verbatim so Marshal computes the
        // array base + field offsets (no hand arithmetic over the doubles/arrays).
        [StructLayout(LayoutKind.Sequential)]
        private struct rF2Vec3 { public double x, y, z; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 4)]
        private struct rF2ScoringInfo
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] mTrackName;
            public int mSession;
            public double mCurrentET;
            public double mEndET;
            public int mMaxLaps;
            public double mLapDist;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] pointer1;
            public int mNumVehicles;
            public byte mGamePhase;
            public sbyte mYellowFlagState;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public sbyte[] mSectorFlag;
            public byte mStartLight;
            public byte mNumRedLights;
            public byte mInRealtime;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] mPlayerName;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] mPlrFileName;
            public double mDarkCloud;
            public double mRaining;
            public double mAmbientTemp;
            public double mTrackTemp;
            public rF2Vec3 mWind;
            public double mMinPathWetness;
            public double mMaxPathWetness;
            public byte mGameMode;
            public byte mIsPasswordProtected;
            public ushort mServerPort;
            public uint mServerPublicIP;
            public int mMaxPlayers;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] mServerName;
            public float mStartET;
            public double mAvgPathWetness;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 200)] public byte[] mExpansion;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] pointer2;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 4)]
        private struct rF2VehicleScoring
        {
            public int mID;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] mDriverName;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] mVehicleName;
            public short mTotalLaps;
            public sbyte mSector;
            public sbyte mFinishStatus;
            public double mLapDist;
            public double mPathLateral;
            public double mTrackEdge;
            public double mBestSector1;
            public double mBestSector2;
            public double mBestLapTime;
            public double mLastSector1;
            public double mLastSector2;
            public double mLastLapTime;
            public double mCurSector1;
            public double mCurSector2;
            public short mNumPitstops;
            public short mNumPenalties;
            public byte mIsPlayer;
            public sbyte mControl;
            public byte mInPits;
            public byte mPlace;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] mVehicleClass;
            public double mTimeBehindNext;
            public int mLapsBehindNext;
            public double mTimeBehindLeader;
            public int mLapsBehindLeader;
            public double mLapStartET;
            public rF2Vec3 mPos;
            public rF2Vec3 mLocalVel;
            public rF2Vec3 mLocalAccel;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public rF2Vec3[] mOri;
            public rF2Vec3 mLocalRot;
            public rF2Vec3 mLocalRotAccel;
            public byte mHeadlights;
            public byte mPitState;
            public byte mServerScored;
            public byte mIndividualPhase;
            public int mQualification;
            public double mTimeIntoLap;
            public double mEstimatedLapTime;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)] public byte[] mPitGroup;
            public byte mFlag;
            public byte mUnderYellow;
            public byte mCountLapFlag;
            public byte mInGarageStall;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] mUpgradePack;
            public float mPitLapDist;
            public float mBestLapSector1;
            public float mBestLapSector2;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] mExpansion;
        }

        private static readonly int ScoringInfoSize = Marshal.SizeOf<rF2ScoringInfo>();
        private static readonly int ScoringVehStride = Marshal.SizeOf<rF2VehicleScoring>();
        private static readonly int ScoringVehBase = ScoringHeader + ScoringInfoSize;
        private static readonly int OffIsPlayer = Marshal.OffsetOf<rF2VehicleScoring>(nameof(rF2VehicleScoring.mIsPlayer)).ToInt32();

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
                uint end = _telAcc.ReadUInt32(4);

                if (begin != end) return false;      // torn write — retry next poll
                if (max <= 0.0) return false;         // engine data not valid yet

                int now = Environment.TickCount;
                if (!_haveElapsed || elapsed != _lastElapsed) { _lastElapsed = elapsed; _lastChangeTick = now; _haveElapsed = true; }
                else if (unchecked(now - _lastChangeTick) > 2000) return false; // paused / frozen

                snap = new GameTelemetrySnapshot { Rpm = (float)rpm, MaxRpm = (float)max, IdleRpm = 0f, Source = Name };
                return true;
            }
            catch
            {
                Close();
                _nextOpenTick = Environment.TickCount + 1000;
                return false;
            }
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
