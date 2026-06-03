using System;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace PadForge.Common.Telemetry
{
    /// <summary>
    /// iRacing telemetry via the irsdk shared memory map (<c>Local\IRSDKMemMapFileName</c>).
    /// Current RPM is the live float telemetry var named "RPM" — it has no fixed
    /// offset, so the var-header table is scanned once per connection to find its
    /// in-buffer offset, then read from the most-recent data buffer each poll.
    /// Redline comes from the session-info YAML string (<c>DriverCarSLLastRPM</c>,
    /// falling back to <c>DriverCarRedLine</c>), re-parsed only when the session
    /// update counter changes.
    ///
    /// <para>Layout verified against mherbold/IRSDKSharper, kutu/pyirsdk, and
    /// irsdkSharp (2026-06-02): header ints version@0, status@4, sessUpd@12,
    /// sessLen@16, sessOff@20, numVars@24, varHdrOff@28, numBuf@32; varBuf[] at 48
    /// stride 16 {tickCount, bufOffset}; var header stride 144 {type, offset@4,
    /// count, name[32]@16}.</para>
    /// </summary>
    internal sealed class IRacingTelemetrySource : ITelemetrySource
    {
        public string Name => "iRacing";

        private const string MapName = @"Local\IRSDKMemMapFileName";
        private const int H_Status = 4, H_SessUpd = 12, H_SessLen = 16, H_SessOff = 20;
        private const int H_NumVars = 24, H_VarHdrOff = 28, H_NumBuf = 32;
        private const int VarBufBase = 48, VarBufStride = 16;
        private const int VarHdrStride = 144, VarHdrOffField = 4, VarHdrName = 16, VarNameLen = 32;

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _acc;
        private int _rpmVarOffset = -1;
        private int _lastSessUpd = int.MinValue;
        private float _maxRpm, _idleRpm;
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
                _rpmVarOffset = -1;
                _lastSessUpd = int.MinValue;
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
                if ((_acc.ReadInt32(H_Status) & 1) == 0) return false; // irsdk_stConnected
                if (_rpmVarOffset < 0)
                {
                    _rpmVarOffset = FindVarOffset("RPM");
                    if (_rpmVarOffset < 0) return false;
                }

                // Most-recent data buffer = highest tickCount.
                int nbuf = _acc.ReadInt32(H_NumBuf);
                if (nbuf <= 0 || nbuf > 8) return false;
                int bestTick = int.MinValue, bestOff = 0;
                for (int i = 0; i < nbuf; i++)
                {
                    int t = _acc.ReadInt32(VarBufBase + i * VarBufStride);
                    if (t > bestTick) { bestTick = t; bestOff = _acc.ReadInt32(VarBufBase + i * VarBufStride + 4); }
                }

                float rpm = _acc.ReadSingle(bestOff + _rpmVarOffset);
                ParseSessionRpms();
                if (rpm < 0f) rpm = 0f;
                snap = new GameTelemetrySnapshot
                {
                    Rpm = rpm,
                    MaxRpm = _maxRpm > 0f ? _maxRpm : 8000f,
                    IdleRpm = _idleRpm,
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

        // Scans the var-header table for a var by name; returns its in-buffer offset.
        private int FindVarOffset(string want)
        {
            int n = _acc.ReadInt32(H_NumVars);
            int hbase = _acc.ReadInt32(H_VarHdrOff);
            if (n <= 0 || n > 4096 || hbase <= 0) return -1;
            var nm = new byte[VarNameLen];
            for (int i = 0; i < n; i++)
            {
                int b = hbase + i * VarHdrStride;
                _acc.ReadArray(b + VarHdrName, nm, 0, VarNameLen);
                int z = Array.IndexOf(nm, (byte)0);
                if (z < 0) z = VarNameLen;
                if (Encoding.ASCII.GetString(nm, 0, z) == want)
                    return _acc.ReadInt32(b + VarHdrOffField);
            }
            return -1;
        }

        // Re-parses redline / idle from the session YAML only when it changes.
        private void ParseSessionRpms()
        {
            int upd = _acc.ReadInt32(H_SessUpd);
            if (upd == _lastSessUpd && _maxRpm > 0f) return;
            int len = _acc.ReadInt32(H_SessLen);
            int off = _acc.ReadInt32(H_SessOff);
            if (len <= 0 || len > 4 * 1024 * 1024 || off <= 0) return;
            var buf = new byte[len];
            _acc.ReadArray(off, buf, 0, len);
            // ASCII/Latin1 is sufficient: the keys and numeric values are ASCII;
            // only driver-name fields are cp1252, which we never read.
            string y = Encoding.Latin1.GetString(buf);
            float last = ScanFloat(y, "DriverCarSLLastRPM:");
            float red = ScanFloat(y, "DriverCarRedLine:");
            _maxRpm = last > 0f ? last : red;
            _idleRpm = ScanFloat(y, "DriverCarIdleRPM:");
            _lastSessUpd = upd;
        }

        private static float ScanFloat(string y, string key)
        {
            int i = y.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return 0f;
            i += key.Length;
            int e = y.IndexOf('\n', i);
            if (e < 0) e = y.Length;
            return float.TryParse(y.Substring(i, e - i).Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out float v) ? v : 0f;
        }

        private void Close()
        {
            try { _acc?.Dispose(); } catch { }
            try { _mmf?.Dispose(); } catch { }
            _acc = null; _mmf = null; _rpmVarOffset = -1;
        }

        public void Stop() => Close();
        public void Dispose() => Close();
    }
}
