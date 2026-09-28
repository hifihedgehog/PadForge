using System;
using System.Collections.Generic;
using System.Threading;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// SparkLink V2, the JingTai V2 row protocol (issue #468), as HallJoy runs
    /// it (backend_sparklink.inc, sparklink_model_profiles.h,
    /// sparklink_key_codes.h, AGPL-3.0, commit 378f9fe8). Three read-only
    /// requests of 64 bytes, sent with report ID 0 on the vendor collection
    /// FFB0:1: device info <c>01 02</c>, layout row <c>03 01 00 row</c> (layer
    /// 0, the base layer) and route row <c>04 03 01 row</c>, the travel of one
    /// matrix row. Each answer echoes its request's header and carries 21
    /// little-endian values from byte 4. Nothing else is ever sent: no stream
    /// enable, no calibration, no save (backend_sparklink.inc:737-788).
    ///
    /// <para>Only this V2 path runs in HallJoy. The legacy FFA0 row protocol
    /// scores 0 and is never admitted (:13, :446-449), and the pipelined burst
    /// poll sits behind <c>if (false)</c> (:1240, :790-875). Neither is
    /// ported.</para>
    /// </summary>
    public static class SparkLinkProtocol
    {
        public const ushort VendorId = 0x1CA6;

        /// <summary>IROK MG75 Max, the model HallJoy tested on hardware. The
        /// other 28 PIDs are its experimental list (sparklink_model_profiles.h:6-44).</summary>
        public const ushort ConfirmedProductId = 0x0529;

        public const ushort UsagePage = 0xFFB0;
        public const ushort Usage = 0x0001;

        /// <summary>Every request and answer is 64 bytes (backend_sparklink.inc:15).</summary>
        public const int PayloadSize = 64;

        public const int MaxRows = 8;
        public const int ColumnsPerRow = 21;

        /// <summary>HidD_SetNumInputBuffers(h, 64) (backend_sparklink.inc:1086).</summary>
        public const int InputBuffers = 64;

        /// <summary>An answer must arrive within 250 ms of the write
        /// (backend_sparklink.inc:20, :696), read in slices of at most 80 ms (:714).</summary>
        public const int TransactionTimeoutMs = 250;
        public const int ReadSliceMs = 80;

        /// <summary>A row's values count for 8 x (250 + 20) = 2160 ms from the
        /// start of its last answered query (backend_sparklink.inc:21-24, :1295).</summary>
        public const long RowFreshnessMs = MaxRows * (TransactionTimeoutMs + 20L);

        /// <summary>No answered row for longer than this ends the session
        /// (backend_sparklink.inc:19, :1821-1834).</summary>
        public const long NoPacketRestartMs = 1800;

        /// <summary>Six failed passes in a row end the worker, with a 15 ms
        /// pause after each (backend_sparklink.inc:1320-1327).</summary>
        public const int FailStreakLimit = 6;
        public const int FailBackoffMs = 15;

        /// <summary>HallJoy's default pacing, MaxBurst (settings.cpp:87):
        /// no sleep, and a yield every 16 route queries (backend_sparklink.inc:1366-1370).</summary>
        public const int YieldEveryQueries = 16;

        /// <summary>The full-travel denominator every key starts a session
        /// with, in micrometers, and the bounds it may move within
        /// (backend_sparklink.inc:25, :877-892).</summary>
        public const ushort ObservedRawDefault = 3500;
        public const ushort ObservedRawMin = 3000;
        public const ushort ObservedRawMax = 5000;

        /// <summary>Size of HallJoy's per-key arrays, room for Fn at 0x409
        /// (sparklink_key_codes.h:7).</summary>
        public const int KeyCount = 0x410;

        /// <summary>The vendor code of Fn1 in a layout row (sparklink_key_codes.h:5-6).</summary>
        public const int FnVendorCode = 0xF101;

        /// <summary>Device-info byte 2 of a keyboard (backend_sparklink.inc:1205, :1692).</summary>
        public const byte KeyboardType = 0x01;

        // ── Identity ───────────────────────────────────────────────────────

        private sealed class Catalog
        {
            public readonly Dictionary<ushort, string> Models = new();
            public readonly List<ushort> Order = new();
            public readonly HashSet<uint> Dedicated6x21 = new();
        }

        private static readonly Lazy<Catalog> _catalog = new(LoadCatalog);

        private static Catalog LoadCatalog()
        {
            var catalog = new Catalog();
            var root = AnalogKeyboardData.File(SparkSayoRoutes.DataFile);
            foreach (var model in root.GetProperty("sparklink_models").EnumerateArray())
            {
                ushort pid = (ushort)model.GetProperty("pid").GetInt32();
                catalog.Models[pid] = model.GetProperty("name").GetString();
                catalog.Order.Add(pid);
            }
            foreach (var identity in root.GetProperty("sparkplayjoy_6x21_identities").EnumerateArray())
            {
                uint vid = (uint)identity.GetProperty("vid").GetInt32();
                uint pid = (uint)identity.GetProperty("pid").GetInt32();
                catalog.Dedicated6x21.Add((vid << 16) | pid);
            }
            return catalog;
        }

        /// <summary>The 29 admitted product IDs on VID 1CA6, the MG75 Max
        /// first, then HallJoy's experimental list in source order.</summary>
        public static IReadOnlyList<ushort> AdmittedProductIds => _catalog.Value.Order;

        /// <summary>HallJoy's ProbeIdentity (sparklink_model_profiles.h:42-44):
        /// the exact VID and one of the 29 PIDs. A vendor usage page is not
        /// identity. Device info <c>01 02</c> is a reset command on a
        /// SteelSeries Apex Pro, and an older HallJoy build that admitted any
        /// long enough vendor collection sent it one and reset the keyboard in
        /// a loop (backend_sparklink.inc:1016-1017,
        /// docs/current/STEELSERIES_APEX_PRO_IMPLEMENTATION_2026-09-25.md:90-111).</summary>
        public static bool ProbeIdentity(ushort vendorId, ushort productId)
            => vendorId == VendorId && _catalog.Value.Models.ContainsKey(productId);

        /// <summary>HallJoy's ProbeInterface (sparklink_model_profiles.h:45-47).</summary>
        public static bool ProbeInterface(ushort vendorId, ushort productId, ushort usagePage, ushort usage)
            => ProbeIdentity(vendorId, productId) && usagePage == UsagePage && usage == Usage;

        /// <summary>
        /// The metadata test, HallJoy's SparkTryOpenDevice gates in its order
        /// (backend_sparklink.inc:1010-1072): a path naming one of the six
        /// SparkPlayJoy 6x21 identities is skipped before anything else (the
        /// AULA RM route owns them), then the exact VID and PID, then the
        /// vendor collection FFB0:1 with input and output reports of at least
        /// 64 bytes. Product and serial strings and the interface number are
        /// not checked, as HallJoy checks none of them. The VID test runs first
        /// here only because it is the cheapest, which changes no result.
        /// </summary>
        public static bool Matches(AnalogKeyboardDeviceInfo info)
        {
            if (info == null || info.VendorId != VendorId) return false;
            if (PathIsDedicated6x21(info.Path)) return false;
            return ProbeInterface(info.VendorId, info.ProductId, info.UsagePage, info.Usage)
                && info.InputReportLength >= PayloadSize
                && info.OutputReportLength >= PayloadSize;
        }

        /// <summary>The model a product ID names, as HallJoy's support
        /// catalogs name it (keyboard_support_notices.json, SUPPORTED_HARDWARE.md),
        /// or null for a PID outside the list. A PID two retail models share
        /// carries both names, since the protocol cannot tell them apart.</summary>
        public static string ModelName(ushort productId)
            => _catalog.Value.Models.TryGetValue(productId, out string name) ? name : null;

        /// <summary>HallJoy's SparkPathIsDedicated6x21 (backend_sparklink.inc:456-461):
        /// true when the path's own "vid_XXXX&amp;pid_XXXX" text names one of the
        /// SparkPlayJoy 6x21 keyboards (aula_win60he_protocol.h:29-36, :144-151).</summary>
        public static bool PathIsDedicated6x21(string path)
            => TryReadUsbIdentityFromPath(path, out ushort vid, out ushort pid)
               && _catalog.Value.Dedicated6x21.Contains(((uint)vid << 16) | pid);

        /// <summary>The first "vid_XXXX&amp;pid_XXXX" in a device path, letters
        /// matched without regard to case (aula_win60he_protocol.h:64-142).</summary>
        public static bool TryReadUsbIdentityFromPath(string path, out ushort vendorId, out ushort productId)
        {
            vendorId = 0;
            productId = 0;
            const int tokenLength = 17;
            if (path == null || path.Length < tokenLength) return false;
            for (int offset = 0; offset <= path.Length - tokenLength; offset++)
            {
                if (!TokenAt(path, offset, "vid_") || !TokenAt(path, offset + 8, "&pid_")) continue;
                if (!Hex4(path, offset + 4, out ushort vid) || !Hex4(path, offset + 13, out ushort pid)) continue;
                vendorId = vid;
                productId = pid;
                return true;
            }
            return false;
        }

        private static char Fold(char c) => c >= 'A' && c <= 'Z' ? (char)(c - 'A' + 'a') : c;

        private static bool TokenAt(string path, int offset, string token)
        {
            if (offset > path.Length || token.Length > path.Length - offset) return false;
            for (int i = 0; i < token.Length; i++)
                if (Fold(path[offset + i]) != token[i]) return false;
            return true;
        }

        private static bool Hex4(string path, int offset, out ushort value)
        {
            value = 0;
            if (offset > path.Length || 4 > path.Length - offset) return false;
            int v = 0;
            for (int i = 0; i < 4; i++)
            {
                char c = Fold(path[offset + i]);
                int nibble = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1;
                if (nibble < 0) return false;
                v = (v << 4) | nibble;
            }
            value = (ushort)v;
            return true;
        }

        // ── Requests ───────────────────────────────────────────────────────

        /// <summary>SparkFillPacket: 64 zero bytes with the given bytes in
        /// front (backend_sparklink.inc:737-747).</summary>
        public static byte[] Packet(params byte[] head)
        {
            var p = new byte[PayloadSize];
            Array.Copy(head, p, Math.Min(head.Length, PayloadSize));
            return p;
        }

        /// <summary><c>01 02</c> (backend_sparklink.inc:752).</summary>
        public static byte[] DeviceInfoRequest() => Packet(0x01, 0x02);

        /// <summary><c>03 01 00 row</c>: GetKeyLayout of layer 0 (backend_sparklink.inc:761).</summary>
        public static byte[] LayoutRowRequest(int row) => Packet(0x03, 0x01, 0x00, (byte)row);

        /// <summary><c>04 03 01 row</c>: the route (travel) of one row
        /// (backend_sparklink.inc:778).</summary>
        public static byte[] RouteRowRequest(int row) => Packet(0x04, 0x03, 0x01, (byte)row);

        /// <summary>
        /// SparkWritePacket (backend_sparklink.inc:630-680): the payload goes
        /// out in up to six ways, stopping at the first that succeeds. Buffer A
        /// is the output report length (at least 64) with the payload at byte
        /// 1, or at byte 0 when the report is exactly 64 bytes. Buffer B is 65
        /// bytes, 00 and the payload. The order is WriteFile A, WriteFile B,
        /// HidD_SetOutputReport A and B, then HidD_SetFeature A and B.
        ///
        /// <para>The transport pads a short buffer to the report length and
        /// refuses a long one. Windows fails HallJoy's call when the buffer is
        /// shorter than the report (HidD_SetOutputReport and HidD_SetFeature
        /// take a buffer that holds the whole report), so a way whose buffer
        /// is shorter is skipped as the call that would have failed, and a
        /// longer one is skipped because the transport cannot send it. With the
        /// 65-byte reports the protocol's 64-byte packets imply (UNKNOWN U1 in
        /// the spec), A and B are the same bytes and HallJoy sends them twice
        /// before falling back, as here.</para>
        /// </summary>
        public static bool WritePacket(IAnalogKeyboardTransport io, byte[] payload)
        {
            int outSize = Math.Max(io.OutputLength, PayloadSize);
            var a = new byte[outSize];
            Array.Copy(payload, 0, a, outSize > PayloadSize ? 1 : 0, PayloadSize);
            var b = new byte[PayloadSize + 1];
            Array.Copy(payload, 0, b, 1, PayloadSize);

            if (a.Length == io.OutputLength && io.Send(a)) return true;
            if (b.Length == io.OutputLength && io.Send(b)) return true;
            if (a.Length == io.OutputLength && io.SendOutputReport(a)) return true;
            if (b.Length == io.OutputLength && io.SendOutputReport(b)) return true;
            if (a.Length == io.FeatureLength && io.SetFeature(a)) return true;
            if (b.Length == io.FeatureLength && io.SetFeature(b)) return true;
            return false;
        }

        // ── Answers ────────────────────────────────────────────────────────

        /// <summary>SparkFindPacket (backend_sparklink.inc:610-628): the offset
        /// of a 64-byte answer whose leading bytes match, tried at 0 and then at
        /// 1 (past the 00 report ID Windows puts in front), or -1. A negative
        /// header byte is not compared.</summary>
        public static int FindPacket(ReadOnlySpan<byte> bytes, int b0, int b1, int b2 = -1, int b3 = -1)
        {
            if (bytes.Length < PayloadSize) return -1;
            for (int off = 0; off <= 1; off++)
            {
                if (off + PayloadSize > bytes.Length) continue;
                var p = bytes.Slice(off);
                if ((b0 >= 0 && p[0] != (byte)b0) || (b1 >= 0 && p[1] != (byte)b1)
                    || (b2 >= 0 && p[2] != (byte)b2) || (b3 >= 0 && p[3] != (byte)b3))
                    continue;
                return off;
            }
            return -1;
        }

        /// <summary>The 21 little-endian values of a layout or route answer,
        /// column c at byte 4 + 2c (backend_sparklink.inc:765-769, :782-786).
        /// Bytes 46 to 63 are not read.</summary>
        public static void ReadRow(ReadOnlySpan<byte> payload, Span<ushort> values)
        {
            for (int col = 0; col < ColumnsPerRow; col++)
            {
                int off = 4 + col * 2;
                values[col] = (ushort)(payload[off] | (payload[off + 1] << 8));
            }
        }

        /// <summary>DecodeKey (sparklink_key_codes.h:8-10): 0xF101 is Fn,
        /// a code up to 0xFF is the keyboard usage as it stands (0 an empty
        /// cell), and any other vendor action is no key. Code 1 is not Fn in
        /// this protocol (docs/current/MG75_FN_REVIEW_2026-09-19.md:17).</summary>
        public static int DecodeKey(int code)
            => code == FnVendorCode ? AnalogKeyCodes.Fn : code >= 0 && code <= 0xFF ? code : 0;

        /// <summary>The device-info fields HallJoy logs (backend_sparklink.inc:351-368).</summary>
        public static SparkLinkDeviceInfo ParseDeviceInfo(ReadOnlySpan<byte> payload)
            => new(payload[2], payload[3],
                ((uint)payload[4] << 24) | ((uint)payload[5] << 16) | ((uint)payload[6] << 8) | payload[7],
                $"{payload[8]}.{payload[9]}.{payload[10]}.{payload[11]}",
                $"{payload[12]}-{payload[13]}-{payload[14]}-{payload[15]}",
                payload[16]);

        // ── Time ───────────────────────────────────────────────────────────

        /// <summary>FreshnessAgeMs (sparklink_hotplug_age.h:7-14): 0 before
        /// the first stamp, and 0 rather than negative when the stamp is newer
        /// than the clock reading.</summary>
        public static long FreshnessAgeMs(long nowMs, long lastMs)
            => lastMs == 0 || nowMs < lastMs ? 0 : nowMs - lastMs;

        /// <summary>IsPacketStale (sparklink_hotplug_age.h:16-23).</summary>
        public static bool IsPacketStale(long nowMs, long lastMs, long staleAfterMs)
            => lastMs != 0 && FreshnessAgeMs(nowMs, lastMs) > staleAfterMs;

        /// <summary>IsRowFresh (sparklink_row_freshness.h:10-14): answered at
        /// least once and not past the deadline.</summary>
        public static bool IsRowFresh(long nowMs, long lastOkMs, long deadlineMs)
            => lastOkMs != 0 && !IsPacketStale(nowMs, lastOkMs, deadlineMs);
    }

    /// <summary>The device-info answer's fields (backend_sparklink.inc:351-368).
    /// HallJoy requires Type 1 and only logs the rest.</summary>
    public readonly struct SparkLinkDeviceInfo
    {
        public SparkLinkDeviceInfo(byte type, byte subtype, uint boardId, string app, string pcb, byte runMode)
        {
            Type = type;
            Subtype = subtype;
            BoardId = boardId;
            App = app;
            Pcb = pcb;
            RunMode = runMode;
        }

        public byte Type { get; }
        public byte Subtype { get; }
        public uint BoardId { get; }
        public string App { get; }
        public string Pcb { get; }
        public byte RunMode { get; }
    }

    /// <summary>
    /// The key map and the published depths of one SparkLink keyboard, the
    /// state HallJoy keeps in its g_spark arrays (backend_sparklink.inc:38-44,
    /// :99-135, :238-327, :877-944). Values are HallJoy's milli, 0 to 1000.
    ///
    /// <para>A route answer commits its row: each key takes the largest
    /// value its cells in the row report, and the row turns fresh. A key's
    /// published value is the largest over the rows that are active, within
    /// the row count and fresh, so a key two rows share keeps its value while
    /// either still holds it, and a row that stops answering drops its keys
    /// 2160 ms after its last answered query started, while the other rows
    /// go on.</para>
    /// </summary>
    public sealed class SparkLinkMatrix
    {
        private const int Rows = SparkLinkProtocol.MaxRows;
        private const int Columns = SparkLinkProtocol.ColumnsPerRow;
        private const int KeyCount = SparkLinkProtocol.KeyCount;

        private readonly int[] _rowColToHid = new int[Rows * Columns];
        private readonly bool[] _rowActive = new bool[Rows];
        private readonly ushort[] _observedMax = new ushort[KeyCount];
        private readonly ushort[,] _rowHidMilli = new ushort[Rows, KeyCount];
        private readonly bool[] _rowFresh = new bool[Rows];
        private readonly long[] _rowLastOkMs = new long[Rows];
        private readonly ushort[] _published = new ushort[KeyCount];
        // The distinct keys of the row being committed and their values. A
        // row maps at most 21 keys, so these stand in for HallJoy's
        // key-indexed rowValues and touched arrays with the same results.
        private readonly int[] _rowKeys = new int[Columns];
        private readonly ushort[] _rowValues = new ushort[Columns];
        private int _rowCount;

        public SparkLinkMatrix() => Reset();

        /// <summary>Rows discovered: the highest row with any nonzero code, plus one.</summary>
        public int RowCount => _rowCount;

        /// <summary>Spark_ResetKeyState (backend_sparklink.inc:99-135): every
        /// published value 0, every key's full-travel denominator back to
        /// 3500, the row map and the row freshness cleared.</summary>
        public void Reset()
        {
            Array.Clear(_published);
            Array.Fill(_observedMax, SparkLinkProtocol.ObservedRawDefault);
            Array.Clear(_rowHidMilli);
            ClearLayout();
            Array.Clear(_rowFresh);
            Array.Clear(_rowLastOkMs);
        }

        /// <summary>The start of SparkDiscoverLayout (backend_sparklink.inc:897-901).</summary>
        public void ClearLayout()
        {
            Array.Clear(_rowColToHid);
            Array.Clear(_rowActive);
            _rowCount = 0;
        }

        /// <summary>Stores one layout row's decoded keys (backend_sparklink.inc:911-925).
        /// The row is active, and polled, when at least one cell decodes to a
        /// key. Returns whether any cell held a nonzero code, which is what
        /// extends the row count.</summary>
        public bool SetLayoutRow(int row, ReadOnlySpan<ushort> codes)
        {
            bool any = false, mappable = false;
            for (int col = 0; col < Columns; col++)
            {
                int code = codes[col];
                if (code != 0) any = true;
                int hid = SparkLinkProtocol.DecodeKey(code);
                _rowColToHid[row * Columns + col] = hid;
                if (hid != 0) mappable = true;
            }
            _rowActive[row] = mappable;
            return any;
        }

        public void SetRowCount(int rows) => _rowCount = Math.Clamp(rows, 0, Rows);

        public bool IsRowActive(int row) => row >= 0 && row < Rows && _rowActive[row];

        public int HidAt(int row, int col) => _rowColToHid[row * Columns + col];

        /// <summary>The key at every (row, column), row-major over 8 rows of
        /// 21, 0 where no key sits.</summary>
        public int[] Table => (int[])_rowColToHid.Clone();

        /// <summary>BackendNative_SparkOwnsHid (backend.cpp:4345-4355): any
        /// cell maps to the key.</summary>
        public bool OwnsHid(int hid)
        {
            if (hid <= 0 || hid >= KeyCount) return false;
            foreach (int mapped in _rowColToHid)
                if (mapped == hid) return true;
            return false;
        }

        /// <summary>BackendNative_SparkGetMilli (backend.cpp:4357-4362).</summary>
        public int GetMilli(int hid) => hid > 0 && hid < KeyCount ? _published[hid] : 0;

        /// <summary>SparkRecordRouteResult's freshness stamp
        /// (backend_sparklink.inc:196-211): an answered row records the time
        /// its query started.</summary>
        public void RecordRouteResult(int row, bool ok, long nowMs)
        {
            if (ok && row >= 0 && row < Rows) _rowLastOkMs[row] = nowMs;
        }

        /// <summary>SparkRowFresh (backend_sparklink.inc:238-246).</summary>
        public bool IsRowFresh(int row, long nowMs, int effectiveRows)
            => row >= 0 && row < Rows && row < effectiveRows && _rowActive[row] && _rowFresh[row]
               && SparkLinkProtocol.IsRowFresh(nowMs, _rowLastOkMs[row], SparkLinkProtocol.RowFreshnessMs);

        /// <summary>SparkNormalizeRouteToMilli (backend_sparklink.inc:877-892):
        /// travel in micrometers over the key's full-travel denominator,
        /// which starts at 3500 and follows the largest value up to 5000 the
        /// key reports this session. No rest threshold: HallJoy applies
        /// deadzones later, outside the protocol.</summary>
        public int NormalizeRouteToMilli(int hid, ushort raw)
        {
            if (hid <= 0 || hid >= KeyCount || raw == 0) return 0;
            ushort observed = Math.Clamp(_observedMax[hid], SparkLinkProtocol.ObservedRawMin,
                SparkLinkProtocol.ObservedRawMax);
            if (raw > observed && raw <= SparkLinkProtocol.ObservedRawMax)
            {
                observed = raw;
                _observedMax[hid] = observed;
            }
            float v = (float)raw / Math.Max(observed, (ushort)1);
            int milli = (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 1000f, MidpointRounding.AwayFromZero);
            return Math.Clamp(milli, 0, 1000);
        }

        /// <summary>SparkCommitRouteRow (backend_sparklink.inc:300-327).
        /// Returns whether any published value changed.</summary>
        public bool CommitRouteRow(int row, ReadOnlySpan<ushort> route, long nowMs, int effectiveRows)
        {
            if (row < 0 || row >= Rows) return false;
            // Cells in column order, as HallJoy normalizes them: a key's
            // denominator can rise at one cell and apply to its next cell.
            int keys = 0;
            for (int col = 0; col < Columns; col++)
            {
                int hid = _rowColToHid[row * Columns + col];
                if (hid == 0) continue;
                ushort milli = (ushort)NormalizeRouteToMilli(hid, route[col]);
                int k = IndexOfRowKey(hid, keys);
                if (k < 0)
                {
                    _rowKeys[keys] = hid;
                    _rowValues[keys++] = milli;
                }
                else if (milli > _rowValues[k])
                {
                    _rowValues[k] = milli;
                }
            }
            for (int k = 0; k < keys; k++) _rowHidMilli[row, _rowKeys[k]] = _rowValues[k];
            _rowFresh[row] = true;
            bool changed = false;
            for (int k = 0; k < keys; k++)
                changed = RecomputePublishedHid(_rowKeys[k], nowMs, effectiveRows) || changed;
            return changed;
        }

        private int IndexOfRowKey(int hid, int count)
        {
            for (int k = 0; k < count; k++)
                if (_rowKeys[k] == hid) return k;
            return -1;
        }

        /// <summary>SparkReconcileRowFreshness (backend_sparklink.inc:280-298):
        /// a row that is inactive, past the row count or expired stops being
        /// fresh, and the keys it maps are recomputed without it. Returns
        /// whether any published value changed.</summary>
        public bool ReconcileRowFreshness(long nowMs, int effectiveRows)
        {
            bool changed = false;
            for (int row = 0; row < Rows; row++)
            {
                bool eligible = row < effectiveRows && _rowActive[row];
                if (eligible && IsRowFresh(row, nowMs, effectiveRows)) continue;
                if (!_rowFresh[row]) continue;
                _rowFresh[row] = false;
                _rowLastOkMs[row] = 0;
                changed = RecomputeRowHids(row, nowMs, effectiveRows) || changed;
            }
            return changed;
        }

        /// <summary>SparkRecomputeRowHids (backend_sparklink.inc:262-278).</summary>
        private bool RecomputeRowHids(int row, long nowMs, int effectiveRows)
        {
            bool changed = false;
            int keys = 0;
            for (int col = 0; col < Columns; col++)
            {
                int hid = _rowColToHid[row * Columns + col];
                if (hid == 0 || IndexOfRowKey(hid, keys) >= 0) continue;
                _rowKeys[keys++] = hid;
                changed = RecomputePublishedHid(hid, nowMs, effectiveRows) || changed;
            }
            return changed;
        }

        /// <summary>SparkRecomputePublishedHid (backend_sparklink.inc:248-260).</summary>
        private bool RecomputePublishedHid(int hid, long nowMs, int effectiveRows)
        {
            if (hid == 0) return false;
            ushort aggregate = 0;
            for (int row = 0; row < effectiveRows; row++)
            {
                if (!IsRowFresh(row, nowMs, effectiveRows)) continue;
                aggregate = Math.Max(aggregate, _rowHidMilli[row, hid]);
            }
            bool changed = _published[hid] != aggregate;
            _published[hid] = aggregate;
            return changed;
        }

        /// <summary>Writes the published set into <paramref name="output"/>,
        /// replacing it: every key of <paramref name="keys"/> above 0, depth
        /// milli / 1000.</summary>
        public void Publish(AnalogKeyInputState output, int[] keys)
        {
            output.ResetForReuse();
            if (keys == null) return;
            foreach (int hid in keys)
            {
                int milli = GetMilli(hid);
                if (milli > 0) output.Set(hid, milli / 1000f);
            }
        }
    }

    /// <summary>
    /// One SparkLink keyboard (issue #468), HallJoy's SparkStart and
    /// SparkPollThreadProcImpl (backend_sparklink.inc:1178-1395, :1587-1795).
    ///
    /// <para>Start proves the protocol with device info (byte 2 must be 1,
    /// a keyboard), asks it again as HallJoy's worker does, then reads the
    /// base layer's key map a row at a time (row, column to key), since
    /// HallJoy keeps no per-model table for this family. Each pass then asks
    /// one active row for its travel, round robin, the way HallJoy's worker
    /// loop does.</para>
    ///
    /// <para>A failed row query does not release the other rows' keys: they
    /// keep their values until their own 2160 ms freshness runs out, which is
    /// how HallJoy isolates a failing row (backend_sparklink.inc:1320-1327,
    /// docs/v1.4/SPARKLINK_ROW_FRESHNESS_REVIEW_2026-09-06.md). So a failed
    /// pass returns Idle, or Ok when the freshness check changed what is
    /// published, and never NoAnswer, which would release every key on each
    /// transient failure. The session ends with Failed at HallJoy's sixth
    /// failed pass in a row or after 1800 ms without an answered row
    /// (SparkTickHotplug, :1817-1834), and the reader then releases every
    /// key, as HallJoy's worker exit does (:137-158).</para>
    ///
    /// <para>Nothing is sent at Stop. HallJoy's three requests change nothing
    /// on the keyboard, so there is nothing to undo (:1465-1563).</para>
    /// </summary>
    public sealed class SparkLinkSession : AnalogKeyboardSession
    {
        private const int Answered = 1;
        private const int NotAnswered = 0;
        private const int DeviceGone = -1;

        private readonly ushort _productId;
        private readonly Func<long> _clock;
        private readonly SparkLinkMatrix _matrix = new();
        private readonly byte[] _reply = new byte[SparkLinkProtocol.PayloadSize];
        private readonly ushort[] _row = new ushort[SparkLinkProtocol.ColumnsPerRow];
        private int[] _keys;
        private bool _started;
        private int _nextRouteRow;
        private int _failStreak;
        private long _lastPacketMs;
        private uint _routeQueries;

        /// <param name="productId">The keyboard's PID, which names the model.</param>
        /// <param name="clock">Milliseconds, GetTickCount64 by default. Tests pass their own.</param>
        public SparkLinkSession(ushort productId, Func<long> clock = null)
        {
            _productId = productId;
            _clock = clock ?? (() => Environment.TickCount64);
        }

        /// <summary>The key map and published depths, for tests and diagnostics.</summary>
        public SparkLinkMatrix Matrix => _matrix;

        /// <summary>The device-info fields of the worker's query, which HallJoy logs.</summary>
        public SparkLinkDeviceInfo DeviceInfo { get; private set; }

        public override string ModelName => _started ? SparkLinkProtocol.ModelName(_productId) : null;

        /// <summary>The keys the base layer maps, in row and column order.</summary>
        public override int[] KeyOrder => _keys;

        public override bool Start(IAnalogKeyboardTransport io)
        {
            // SparkStart's protocol proof before anything is claimed
            // (backend_sparklink.inc:1688-1701).
            if (QueryDeviceInfo(io) != Answered || _reply[2] != SparkLinkProtocol.KeyboardType) return false;
            _matrix.Reset();

            // The worker asks again and logs the fields (:1204-1211).
            if (QueryDeviceInfo(io) != Answered || _reply[2] != SparkLinkProtocol.KeyboardType) return false;
            DeviceInfo = SparkLinkProtocol.ParseDeviceInfo(_reply);

            // The worker needs at least one layout row (:1213-1220).
            if (DiscoverLayout(io) == DeviceGone || _matrix.RowCount <= 0) return false;

            // HallJoy would start a worker that finds no row to poll and quits
            // after six passes, then try again every 2 s (:1283-1327, :1837).
            // A map with no key cannot be read, so the start fails instead.
            bool anyActive = false;
            for (int row = 0; row < _matrix.RowCount; row++)
                anyActive |= _matrix.IsRowActive(row);
            if (!anyActive) return false;

            _keys = AnalogKeyboardData.KeysOf(_matrix.Table);
            _nextRouteRow = 0;
            _failStreak = 0;
            _lastPacketMs = 0;
            _started = true;
            return true;
        }

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            if (!_started) return AnalogPollResult.Failed;
            long now = _clock();

            // SparkTickHotplug (:1821-1834): a session whose rows answered
            // before and have not for 1800 ms is stopped.
            if (SparkLinkProtocol.IsPacketStale(now, _lastPacketMs, SparkLinkProtocol.NoPacketRestartMs))
                return AnalogPollResult.Failed;

            // SparkRowLimit is 0 by default, no cap (settings.cpp:88).
            int rows = _matrix.RowCount;
            bool changed = _matrix.ReconcileRowFreshness(now, rows);
            bool attempted = false, rowOk = false;
            int span = Math.Max(rows, 1);

            // One active row per pass, round robin from after the last one
            // queried (:1283-1317).
            for (int attempt = 0; attempt < rows; attempt++)
            {
                int row = _nextRouteRow % span;
                _nextRouteRow = (row + 1) % span;
                if (!_matrix.IsRowActive(row)) continue;

                attempted = true;
                long routeNow = _clock();
                int result = QueryRouteRow(io, row);
                _routeQueries++;
                if (result == DeviceGone) return AnalogPollResult.Failed;
                if (result != Answered) break;

                rowOk = true;
                _matrix.RecordRouteResult(row, true, routeNow);
                changed = _matrix.CommitRouteRow(row, _row, routeNow, rows) || changed;
                break;
            }

            if (!attempted || !rowOk)
            {
                if (++_failStreak >= SparkLinkProtocol.FailStreakLimit) return AnalogPollResult.Failed;
                Thread.Sleep(SparkLinkProtocol.FailBackoffMs);
                if (!changed) return AnalogPollResult.Idle;
                _matrix.Publish(output, _keys);
                return AnalogPollResult.Ok;
            }

            _failStreak = 0;
            _lastPacketMs = _clock();
            _matrix.Publish(output, _keys);
            if (_routeQueries % SparkLinkProtocol.YieldEveryQueries == 0) Thread.Yield();
            return AnalogPollResult.Ok;
        }

        /// <summary>SparkDiscoverLayout (backend_sparklink.inc:894-944): rows 0
        /// to 7 until a query fails, or until two rows in a row hold no code
        /// once a row with codes was seen. The row count is the highest row
        /// with any code, plus one.</summary>
        private int DiscoverLayout(IAnalogKeyboardTransport io)
        {
            _matrix.ClearLayout();
            int highest = -1, emptyRun = 0;
            for (int row = 0; row < SparkLinkProtocol.MaxRows; row++)
            {
                int result = Transact(io, SparkLinkProtocol.LayoutRowRequest(row), 0x03, 0x01, 0x00, row);
                if (result == DeviceGone) return DeviceGone;
                if (result != Answered) break;
                SparkLinkProtocol.ReadRow(_reply, _row);
                if (_matrix.SetLayoutRow(row, _row))
                {
                    highest = row;
                    emptyRun = 0;
                }
                else if (++emptyRun >= 2 && highest >= 0)
                {
                    break;
                }
            }
            _matrix.SetRowCount(highest + 1);
            return Answered;
        }

        private int QueryDeviceInfo(IAnalogKeyboardTransport io)
            => Transact(io, SparkLinkProtocol.DeviceInfoRequest(), 0x01, 0x02);

        private int QueryRouteRow(IAnalogKeyboardTransport io, int row)
        {
            Array.Clear(_row);
            int result = Transact(io, SparkLinkProtocol.RouteRowRequest(row), 0x04, 0x03, 0x01, row);
            if (result == Answered) SparkLinkProtocol.ReadRow(_reply, _row);
            return result;
        }

        /// <summary>
        /// SparkTransact (backend_sparklink.inc:682-735): write, then read for
        /// 250 ms from the end of the write in slices of at most 80 ms, and
        /// take the first report whose header matches at offset 0 or 1. Other
        /// reports are dropped. HallJoy flushes nothing before a request, so a
        /// late answer to an earlier request for the same row can satisfy this
        /// one, as here.
        ///
        /// <para>A read error is a gone device here, where HallJoy ends only
        /// the transaction and lets six failed passes end the worker: the
        /// transport reports every read error as gone, and ending the session
        /// at once releases the keys sooner, never later.</para>
        /// </summary>
        private int Transact(IAnalogKeyboardTransport io, byte[] payload, int b0, int b1, int b2 = -1, int b3 = -1)
        {
            if (!SparkLinkProtocol.WritePacket(io, payload)) return NotAnswered;
            long deadline = _clock() + SparkLinkProtocol.TransactionTimeoutMs;
            while (true)
            {
                long left = deadline - _clock();
                if (left <= 0) return NotAnswered;
                int n = io.Receive(Buffer, (int)Math.Min(left, SparkLinkProtocol.ReadSliceMs));
                if (n < 0) return DeviceGone;
                if (n == 0) continue;
                int at = SparkLinkProtocol.FindPacket(Buffer.AsSpan(0, n), b0, b1, b2, b3);
                if (at < 0) continue;
                Buffer.AsSpan(at, SparkLinkProtocol.PayloadSize).CopyTo(_reply);
                return Answered;
            }
        }
    }
}
