using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace PadForge.Common.Telemetry
{
    /// <summary>
    /// Assetto Corsa / Competizione shared-memory telemetry. Both titles map the
    /// same <c>Local\acpmf_physics</c> and <c>Local\acpmf_static</c> pages while
    /// running. Current RPM is <c>Physics.Rpms</c> (int) at byte 20 of the physics
    /// page (PacketId, Gas, Brake, Fuel, Gear precede it, all 4 bytes). Redline is
    /// <c>StaticInfo.MaxRpm</c>, read by marshaling the static page's head — the
    /// Unicode string fields ahead of MaxRpm make a hand-offset fragile, so the
    /// layout up to MaxRpm is described faithfully and let the marshaler place it.
    /// Struct verified against mdjarv/assettocorsasharedmemory (2026-06-02).
    ///
    /// <para>Speed is <c>Physics.SpeedKmh</c> (float) at byte 28. The track's
    /// grip is <c>Graphics.SurfaceGrip</c> (float) on the
    /// <c>Local\acpmf_graphics</c> page, at byte 280 in Assetto Corsa and at
    /// 1240 in Competizione, whose page holds sixty cars' coordinates and ids
    /// ahead of it. Both by Marshal.OffsetOf on assettocorsasharedmemory's
    /// structs and by count of tm-bt-led's SharedFileOut.h, which has the two
    /// layouts side by side (2026-11-19). Competizione is told by the static
    /// page's version, 1.8 and later (tm-bt-led assetto.js 89). The graphics
    /// page is read when it opens and is not needed for the rest.</para>
    /// </summary>
    internal sealed class AssettoCorsaTelemetrySource : ITelemetrySource
    {
        public string Name => "Assetto Corsa";

        private const string PhysicsName = "Local\\acpmf_physics";
        private const string StaticName = "Local\\acpmf_static";
        private const string GraphicsName = "Local\\acpmf_graphics";
        private const int RpmsOffset = 20;
        private const int SpeedKmhOffset = 28;
        internal const int SurfaceGripOffset = 280, CompetizioneSurfaceGripOffset = 1240;

        private MemoryMappedFile _physMmf, _statMmf, _graphMmf;
        private MemoryMappedViewAccessor _physAcc, _statAcc, _graphAcc;
        private int _maxRpm;
        private bool _competizione;
        private int _staticCountdown;
        private int _nextOpenTick;   // Environment.TickCount before which open is skipped
        private int _lastPacketId;   // physics PacketId (@0); advances each physics tick
        private int _lastAdvanceTick; // Environment.TickCount of the last PacketId change
        private bool _havePacket;

        // Faithful prefix of SPageFileStatic up to MaxRpm (Pack = 4, Unicode
        // strings). Marshaling the head lets the runtime account for the string
        // padding instead of trusting a computed byte offset.
        [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
        private struct AcStaticHead
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string SMVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string ACVersion;
            public int NumberOfSessions;
            public int NumCars;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string CarModel;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string Track;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string PlayerName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string PlayerSurname;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string PlayerNick;
            public int SectorCount;
            public float MaxTorque;
            public float MaxPower;
            public int MaxRpm;
        }

        public void Start() { /* lazy: pages exist only while the game runs */ }

        private bool EnsureOpen()
        {
            if (_physAcc != null) return true;
            // Back off between open attempts: when the game isn't running,
            // OpenExisting throws, and retrying every poll would be an exception
            // storm. Retry at most once per second.
            if (unchecked(Environment.TickCount - _nextOpenTick) < 0) return false;
            try
            {
                _physMmf = MemoryMappedFile.OpenExisting(PhysicsName, MemoryMappedFileRights.Read);
                _statMmf = MemoryMappedFile.OpenExisting(StaticName, MemoryMappedFileRights.Read);
                _physAcc = _physMmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                _statAcc = _statMmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                _staticCountdown = 0;
                // The graphics page gives the track's grip alone, so a failure to open it costs only that.
                try
                {
                    _graphMmf = MemoryMappedFile.OpenExisting(GraphicsName, MemoryMappedFileRights.Read);
                    _graphAcc = _graphMmf.CreateViewAccessor(0, CompetizioneSurfaceGripOffset + 4, MemoryMappedFileAccess.Read);
                }
                catch
                {
                    try { _graphMmf?.Dispose(); } catch { }
                    _graphMmf = null;
                    _graphAcc = null;
                }
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
                if (_staticCountdown <= 0)
                {
                    _maxRpm = ReadStatic(out _competizione);
                    _staticCountdown = 200; // re-read redline ~every 200 polls (car change)
                }
                else _staticCountdown--;

                if (_maxRpm <= 0) return false;

                // Freshness gate. The physics page persists with frozen values in
                // menus / pause / replay / after the session ends, so a nonzero RPM
                // could otherwise stick on the strip. PacketId (@0) advances every
                // physics tick; if it hasn't changed for ~1s the data is stale and
                // we report idle so the LEDs clear (mirrors Forza's stale timeout).
                int packetId = _physAcc.ReadInt32(0);
                int now = Environment.TickCount;
                if (!_havePacket || packetId != _lastPacketId)
                {
                    _lastPacketId = packetId;
                    _lastAdvanceTick = now;
                    _havePacket = true;
                }
                else if (unchecked(now - _lastAdvanceTick) > 1000)
                {
                    return false;
                }

                int rpms = _physAcc.ReadInt32(RpmsOffset);
                if (rpms < 0) rpms = 0;
                snap = new GameTelemetrySnapshot { Rpm = rpms, MaxRpm = _maxRpm, IdleRpm = 0f, Source = Name };
                snap.SetSpeed(_physAcc.ReadSingle(SpeedKmhOffset) / 3.6f);
                if (_graphAcc != null)
                    snap.Grip = (float)SurfaceGrips.AssettoCorsa(
                        _graphAcc.ReadSingle(_competizione ? CompetizioneSurfaceGripOffset : SurfaceGripOffset));
                return true;
            }
            catch
            {
                // Game closed mid-read — drop the handles and report idle.
                Close();
                return false;
            }
        }

        /// <summary>The static page's max RPM, and whether its version is
        /// Competizione's.</summary>
        private int ReadStatic(out bool competizione)
        {
            int size = Marshal.SizeOf<AcStaticHead>();
            byte[] buf = new byte[size];
            _statAcc.ReadArray(0, buf, 0, size);
            GCHandle h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                AcStaticHead head = Marshal.PtrToStructure<AcStaticHead>(h.AddrOfPinnedObject());
                competizione = IsCompetizione(head.SMVersion);
                return head.MaxRpm;
            }
            finally { h.Free(); }
        }

        /// <summary>Competizione's pages report version 1.8 or later.</summary>
        internal static bool IsCompetizione(string smVersion)
            => double.TryParse(smVersion, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) && v >= 1.8;

        private void Close()
        {
            try { _physAcc?.Dispose(); } catch { }
            try { _statAcc?.Dispose(); } catch { }
            try { _graphAcc?.Dispose(); } catch { }
            try { _physMmf?.Dispose(); } catch { }
            try { _statMmf?.Dispose(); } catch { }
            try { _graphMmf?.Dispose(); } catch { }
            _physAcc = null; _statAcc = null; _graphAcc = null; _physMmf = null; _statMmf = null; _graphMmf = null;
            _maxRpm = 0;
            _havePacket = false;
        }

        public void Stop() => Close();
        public void Dispose() => Close();
    }
}
