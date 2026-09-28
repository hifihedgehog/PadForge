using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// HallJoy's three RY5088 routes (issue #468), ported from HallJoy's
    /// source (AGPL-3.0, commit 378f9fe). All three talk to the same vendor
    /// control collection with unnumbered 65-byte feature reports, and a
    /// 3151:5030 keyboard can match all three on metadata, so they run in
    /// HallJoy's order and the first handshake that proves the board wins.
    ///
    /// <para>Priority in HallJoy's native catalog (native_analog_backends.def):
    /// ATTACK SHARK is the first route of the whole catalog (line 14). The
    /// snapshot route comes right after Chilkey Slice75 (line 43) and the
    /// stream route right after the snapshot route (line 44). Board 2642 is
    /// both an EPOMAKER G84 HE snapshot board and an ANTGAMER AGK75 U2 stream
    /// row with a different key map: the snapshot route proves it first and
    /// keeps it, so the ANTGAMER profile applies only when the snapshot proof
    /// fails (the order native_analog_backends.def:43-44 gives, and the
    /// stream route's skip of a path another route claimed,
    /// rongyuan_stream_backend.cpp:415-418).</para>
    /// </summary>
    public static class RongYuanRoutes
    {
        public const ushort VendorId = 0x3151;

        /// <summary>The one USB identity the snapshot route serves
        /// (rongyuan_snapshot_protocol.h:9).</summary>
        public const ushort SnapshotProductId = 0x5030;

        /// <summary>Windows length of the vendor collection's unnumbered
        /// feature report: 64 payload bytes and the report ID byte.</summary>
        public const int FeatureReportLength = 65;

        /// <summary>ATTACK SHARK RY5088 boards, HallJoy's "attackshark-pro"
        /// descriptor (attackshark_pro_diagnostic.cpp:791-798). HallJoy opens
        /// the collection for reading and writing with no sharing first
        /// (attackshark_pro_diagnostic.cpp:228), then on a sharing or access
        /// error a shared handle, then one with no access rights (lines
        /// 232-235): the R85 HE needed that ladder. A page expires 150 ms
        /// after its last read at the 1 ms exchange delay and 300 ms at the
        /// 10 ms delay (attackshark_pro_native_model.h:164, 171), which the
        /// session enforces page by page, so the row's backstop is the
        /// longest of those budgets. HallJoy's supervisor starts the worker
        /// again 3 s after an identity failure or a session's end
        /// (attackshark_pro_diagnostic.cpp:363-364, 503).</summary>
        public static readonly AnalogKeyboardRoute AttackShark = new()
        {
            Id = "attackshark-pro",
            Protocol = AnalogKeyboardProtocol.AttackShark,
            Matches = AttackSharkMatches,
            CreateSession = info => new AttackSharkSession(info.ProductId),
            Writable = true,
            Exclusive = true,
            OpenFallback = true,
            StaleAfterMs = AttackSharkSession.FreshBudget(10),
            StartRetryMs = 3000,
            ReconnectMs = 3000,
        };

        /// <summary>MonsGeek M1 V5 HE and EPOMAKER G84 HE, HallJoy's
        /// "rongyuan-snapshot" descriptor (rongyuan_snapshot_backend.cpp:501-518),
        /// opened for reading and writing with no sharing
        /// (rongyuan_snapshot_backend.cpp:166-169). Its worker runs admission
        /// again every second whether or not a session ran
        /// (rongyuan_snapshot_backend.cpp:354-373).</summary>
        public static readonly AnalogKeyboardRoute Snapshot = new()
        {
            Id = "rongyuan-snapshot",
            Protocol = AnalogKeyboardProtocol.RongYuanSnapshot,
            Matches = SnapshotMatches,
            CreateSession = info => new RongYuanSnapshotSession(),
            Writable = true,
            Exclusive = true,
            // A feature transfer gives up after 50 ms (rongyuan_snapshot_backend.cpp:88-92),
            // and a page expires 150 ms after its read (line 32).
            TransferTimeoutMs = RongYuanCommandChannel.QueryWindowMs,
            StaleAfterMs = RongYuanSnapshotSession.FreshMs,
            StartRetryMs = 1000,
            ReconnectMs = 1000,
        };

        /// <summary>The RongYuan event stream, HallJoy's "rongyuan-stream"
        /// descriptor (rongyuan_stream_backend.cpp:555-572). The control
        /// collection opens for reading and writing with no sharing
        /// (rongyuan_stream_backend.cpp:189-192) and the paired input
        /// collection read-only and shared, with 128 input buffers
        /// (rongyuan_stream_backend.cpp:228-231). Its worker runs admission
        /// again every second whether or not a session ran
        /// (rongyuan_stream_backend.cpp:407-426), the stream enable included.</summary>
        public static readonly AnalogKeyboardRoute Stream = new()
        {
            Id = "rongyuan-stream",
            Protocol = AnalogKeyboardProtocol.RongYuanStream,
            Matches = StreamMatches,
            CreateSession = info => new RongYuanStreamSession(info.VendorId, info.ProductId),
            Writable = true,
            Exclusive = true,
            InputBuffers = 128,
            Companion = StreamInput,
            TransferTimeoutMs = RongYuanCommandChannel.QueryWindowMs,
            StartRetryMs = 1000,
            ReconnectMs = 1000,
        };

        /// <summary>The three routes in HallJoy's relative order: ATTACK
        /// SHARK, then the snapshot route, then the stream route.</summary>
        public static IReadOnlyList<AnalogKeyboardRoute> All => new[] { AttackShark, Snapshot, Stream };

        /// <summary>HallJoy's ATTACK SHARK collection filter, Find()
        /// (attackshark_pro_diagnostic.cpp:176-213): a USB path of vendor
        /// 3151, a product string that names no receiver or dongle, a product
        /// ID some profile uses, and the FFFF:0002 collection with a 65-byte
        /// feature report. HallJoy reads no serial and no interface number.</summary>
        public static bool AttackSharkMatches(AnalogKeyboardDeviceInfo info)
        {
            if (info == null || info.VendorId != VendorId) return false;
            // The lowercased path must carry "vid_3151&pid_" (line 189), which
            // keeps Bluetooth paths out: the route is wired USB only.
            if ((info.Path ?? string.Empty).IndexOf("vid_3151&pid_", StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            // Receivers and dongles get no feature traffic (lines 205-208).
            string product = info.ProductString ?? string.Empty;
            if (product.IndexOf("receiver", StringComparison.OrdinalIgnoreCase) >= 0
                || product.IndexOf("dongle", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return info.UsagePage == 0xFFFF && info.Usage == 2
                && info.FeatureReportLength == FeatureReportLength
                && RongYuanCatalog.IsAttackSharkProductId(info.ProductId);
        }

        /// <summary>HallJoy's snapshot filter (rongyuan_snapshot_backend.cpp:131-142):
        /// 3151:5030 and the control collection.</summary>
        public static bool SnapshotMatches(AnalogKeyboardDeviceInfo info)
            => info != null && info.VendorId == VendorId && info.ProductId == SnapshotProductId
               && IsControlCollection(info);

        /// <summary>HallJoy's stream filter (rongyuan_stream_backend.cpp:142-175):
        /// a USB identity some stream row or alias names, the control
        /// collection, a container ID, and exactly one paired input collection.
        /// Product strings, serials and interface numbers are not checked.</summary>
        public static bool StreamMatches(AnalogKeyboardDeviceInfo info)
            => info != null && IsControlCollection(info) && !string.IsNullOrEmpty(info.ContainerId)
               && RongYuanCatalog.IsStreamCandidate(info.VendorId, info.ProductId)
               && StreamInput(info) != null;

        /// <summary>The vendor control collection both RongYuan routes command:
        /// usage page FFFF or FF00, usage 2, a 65-byte feature report
        /// (rongyuan_snapshot_backend.cpp:140-142, rongyuan_stream_backend.cpp:155-157).</summary>
        public static bool IsControlCollection(AnalogKeyboardDeviceInfo info)
            => (info.UsagePage == 0xFFFF || info.UsagePage == 0xFF00) && info.Usage == 2
               && info.FeatureReportLength == FeatureReportLength;

        /// <summary>The input collection the stream arrives on
        /// (rongyuan_stream_backend.cpp:150-153): usage FFFF:0001, a 32-byte
        /// input report, and exactly one input value cap, report ID 5, eight
        /// bits, 31 of them.</summary>
        public static bool IsStreamInput(AnalogKeyboardDeviceInfo info)
        {
            if (info == null || info.UsagePage != 0xFFFF || info.Usage != 1 || info.InputReportLength != 32)
                return false;
            var caps = info.InputValueCaps;
            if (caps == null || caps.Count != 1) return false;
            var cap = caps[0];
            return cap.ReportId == 5 && cap.BitSize == 8 && cap.ReportCount == 31;
        }

        /// <summary>The stream route's companion: the one input collection of
        /// the same VID, PID and container ID. HallJoy uses a control
        /// collection only when exactly one such input collection exists
        /// (rongyuan_stream_backend.cpp:169-175), so two or none give null.</summary>
        public static AnalogKeyboardDeviceInfo StreamInput(AnalogKeyboardDeviceInfo control)
        {
            if (control == null || string.IsNullOrEmpty(control.ContainerId) || control.Siblings == null)
                return null;
            AnalogKeyboardDeviceInfo found = null;
            int matches = 0;
            foreach (var sibling in control.Siblings)
            {
                if (sibling == null || !IsStreamInput(sibling)) continue;
                if (sibling.VendorId != control.VendorId || sibling.ProductId != control.ProductId) continue;
                if (!string.Equals(sibling.ContainerId, control.ContainerId, StringComparison.OrdinalIgnoreCase))
                    continue;
                found = sibling;
                matches++;
            }
            return matches == 1 ? found : null;
        }
    }

    /// <summary>One ATTACK SHARK revision: its board ID from the 8F answer,
    /// its USB product ID, where its Fn key sits, HallJoy's name for it, and
    /// its factory and Fn-layer key tables of 128 slots each
    /// (attackshark_pro_native_model.h:8-160). A slot is page * 32 plus the
    /// value's place in its depth page.</summary>
    public sealed class AttackSharkProfile
    {
        public uint Id { get; init; }
        public ushort ProductId { get; init; }
        public int FnSlot { get; init; }
        public string Name { get; init; }
        public int[] Factory { get; init; }
        public int[] Fn { get; init; }

        /// <summary>True when a factory or Fn key sits in slots 96 to 127,
        /// which puts page 3 in every sweep (attackshark_pro_native_model.h:180-183).</summary>
        public bool FourthPage { get; init; }
    }

    /// <summary>One snapshot board (rongyuan_snapshot_protocol.h:99-109):
    /// the board ID the 8F answer carries, the vendor product token, the
    /// model name, the travel range in micrometers and the factory key code
    /// of each of the 128 slots.</summary>
    public sealed class RongYuanSnapshotModel
    {
        public uint Board { get; init; }
        public string Product { get; init; }
        public string Name { get; init; }
        public int RangeUm { get; init; }
        public int[] Codes { get; init; }
    }

    /// <summary>One stream profile row (rongyuan_stream_protocol.h:7-261).
    /// <see cref="PrecisionEnum"/> rows need a valid E6 answer for their
    /// unit scale.</summary>
    public sealed class RongYuanStreamModel
    {
        public uint Board { get; init; }
        public ushort VendorId { get; init; }
        public ushort ProductId { get; init; }
        public string Name { get; init; }
        public string Product { get; init; }
        public int RangeUm { get; init; }
        public bool PrecisionEnum { get; init; }
        public int[] Codes { get; init; }
    }

    /// <summary>An extra USB identity one stream board was seen under,
    /// read with its canonical row (rongyuan_stream_protocol.h:263-265).</summary>
    public sealed class RongYuanStreamAlias
    {
        public uint Board { get; init; }
        public ushort VendorId { get; init; }
        public ushort ProductId { get; init; }
        public ushort CanonicalVendorId { get; init; }
        public ushort CanonicalProductId { get; init; }
    }

    /// <summary>
    /// The model catalogs and key tables of the three routes, embedded as
    /// Data/rongyuan.json. A script generated the file from HallJoy's
    /// attackshark_pro_native_model.h, rongyuan_snapshot_protocol.h and
    /// rongyuan_stream_protocol.h, decoding each 4-byte action with HallJoy's
    /// Decode (rongyuan_snapshot_protocol.h:11-15), and checked every table
    /// against a second extraction. Loads on first use.
    /// </summary>
    public static class RongYuanCatalog
    {
        public const string DataFile = "rongyuan.json";

        private sealed class Catalog
        {
            public AttackSharkProfile[] AttackShark;
            public RongYuanSnapshotModel[] Snapshot;
            public RongYuanStreamModel[] Stream;
            public RongYuanStreamAlias[] Aliases;
            public HashSet<int> AttackSharkProductIds;
            public HashSet<int> StreamIdentities;
        }

        private static readonly Lazy<Catalog> _catalog = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

        public static IReadOnlyList<AttackSharkProfile> AttackSharkProfiles => _catalog.Value.AttackShark;
        public static IReadOnlyList<RongYuanSnapshotModel> SnapshotModels => _catalog.Value.Snapshot;
        public static IReadOnlyList<RongYuanStreamModel> StreamModels => _catalog.Value.Stream;
        public static IReadOnlyList<RongYuanStreamAlias> StreamAliases => _catalog.Value.Aliases;

        /// <summary>The ATTACK SHARK profile for a board ID, or null
        /// (attackshark_pro_native_model.h:162).</summary>
        public static AttackSharkProfile FindAttackShark(uint id)
        {
            foreach (var p in _catalog.Value.AttackShark)
                if (p.Id == id) return p;
            return null;
        }

        /// <summary>True for a product ID some ATTACK SHARK profile uses,
        /// CandidatePid (attackshark_pro_diagnostic_model.h:14-17).</summary>
        public static bool IsAttackSharkProductId(ushort productId)
            => _catalog.Value.AttackSharkProductIds.Contains(productId);

        /// <summary>The snapshot board for a board ID, or null
        /// (rongyuan_snapshot_protocol.h:110-115).</summary>
        public static RongYuanSnapshotModel FindSnapshot(uint board)
        {
            foreach (var m in _catalog.Value.Snapshot)
                if (m.Board == board) return m;
            return null;
        }

        /// <summary>The stream row for a board seen under a USB identity,
        /// after an alias maps the identity to its canonical row, or null
        /// (rongyuan_stream_protocol.h:266-269). The board and the identity
        /// must both match one row.</summary>
        public static RongYuanStreamModel FindStream(uint board, ushort vendorId, ushort productId)
        {
            var catalog = _catalog.Value;
            foreach (var a in catalog.Aliases)
            {
                if (a.Board != board || a.VendorId != vendorId || a.ProductId != productId) continue;
                vendorId = a.CanonicalVendorId;
                productId = a.CanonicalProductId;
                break;
            }
            foreach (var m in catalog.Stream)
                if (m.Board == board && m.VendorId == vendorId && m.ProductId == productId) return m;
            return null;
        }

        /// <summary>True for a USB identity some stream row or alias names,
        /// Candidate (rongyuan_stream_protocol.h:270).</summary>
        public static bool IsStreamCandidate(ushort vendorId, ushort productId)
            => _catalog.Value.StreamIdentities.Contains((vendorId << 16) | productId);

        private static Catalog Load()
        {
            var root = AnalogKeyboardData.File(DataFile);
            var catalog = new Catalog();

            var shark = new List<AttackSharkProfile>();
            foreach (var row in root.GetProperty("attackshark_profiles").EnumerateArray())
            {
                uint id = row.GetProperty("id").GetUInt32();
                var factory = Table($"attackshark_{id}_factory");
                var fn = Table($"attackshark_{id}_fn");
                bool fourth = false;
                for (int i = 96; i < 128; i++) fourth |= factory[i] != 0 || fn[i] != 0;
                shark.Add(new AttackSharkProfile
                {
                    Id = id,
                    ProductId = (ushort)row.GetProperty("pid").GetInt32(),
                    FnSlot = row.GetProperty("fnSlot").GetInt32(),
                    Name = row.GetProperty("name").GetString(),
                    Factory = factory,
                    Fn = fn,
                    FourthPage = fourth,
                });
            }
            catalog.AttackShark = shark.ToArray();
            catalog.AttackSharkProductIds = new HashSet<int>();
            foreach (var p in catalog.AttackShark) catalog.AttackSharkProductIds.Add(p.ProductId);

            var snapshot = new List<RongYuanSnapshotModel>();
            foreach (var row in root.GetProperty("snapshot_models").EnumerateArray())
            {
                uint board = row.GetProperty("board").GetUInt32();
                snapshot.Add(new RongYuanSnapshotModel
                {
                    Board = board,
                    Product = row.GetProperty("product").GetString(),
                    Name = row.GetProperty("name").GetString(),
                    RangeUm = row.GetProperty("rangeUm").GetInt32(),
                    Codes = Table($"snapshot_{board}"),
                });
            }
            catalog.Snapshot = snapshot.ToArray();

            var stream = new List<RongYuanStreamModel>();
            catalog.StreamIdentities = new HashSet<int>();
            foreach (var row in root.GetProperty("stream_models").EnumerateArray())
            {
                uint board = row.GetProperty("board").GetUInt32();
                var model = new RongYuanStreamModel
                {
                    Board = board,
                    VendorId = (ushort)row.GetProperty("vid").GetInt32(),
                    ProductId = (ushort)row.GetProperty("pid").GetInt32(),
                    Name = row.GetProperty("name").GetString(),
                    Product = row.GetProperty("product").GetString(),
                    RangeUm = row.GetProperty("rangeUm").GetInt32(),
                    PrecisionEnum = row.GetProperty("precisionEnum").GetBoolean(),
                    Codes = Table($"stream_{board}"),
                };
                stream.Add(model);
                catalog.StreamIdentities.Add((model.VendorId << 16) | model.ProductId);
            }
            catalog.Stream = stream.ToArray();

            var aliases = new List<RongYuanStreamAlias>();
            foreach (var row in root.GetProperty("stream_aliases").EnumerateArray())
            {
                var alias = new RongYuanStreamAlias
                {
                    Board = row.GetProperty("board").GetUInt32(),
                    VendorId = (ushort)row.GetProperty("vid").GetInt32(),
                    ProductId = (ushort)row.GetProperty("pid").GetInt32(),
                    CanonicalVendorId = (ushort)row.GetProperty("canonicalVid").GetInt32(),
                    CanonicalProductId = (ushort)row.GetProperty("canonicalPid").GetInt32(),
                };
                aliases.Add(alias);
                catalog.StreamIdentities.Add((alias.VendorId << 16) | alias.ProductId);
            }
            catalog.Aliases = aliases.ToArray();
            return catalog;
        }

        private static int[] Table(string name)
        {
            var table = AnalogKeyboardData.Table(DataFile, name);
            if (table == null || table.Length != 128)
                throw new InvalidOperationException("Analog keyboard table missing or not 128 slots: " + name);
            return table;
        }
    }

    /// <summary>
    /// A wait of whole milliseconds on a high-resolution waitable timer, the
    /// way HallJoy paces its RY5088 exchanges (attackshark_pro_diagnostic.cpp:61-78):
    /// a plain sleep would round each 1 ms wait up to the scheduler tick.
    /// Falls back to a plain timer and then to Thread.Sleep, since timer
    /// availability must not gate the protocol (the same fallback order).
    /// One owner thread at a time.
    /// </summary>
    public sealed class RongYuanPreciseDelay : IDisposable
    {
        private const uint CreateWaitableTimerHighResolution = 0x2;
        // TIMER_MODIFY_STATE | SYNCHRONIZE: the rights the wait needs. Some
        // Windows builds reject TIMER_ALL_ACCESS together with the
        // high-resolution flag (PadForge's audio pacing timer found this).
        private const uint TimerAccess = 0x0002 | 0x00100000;
        private const uint WaitObject0 = 0;
        private const int WaitSlackMs = 1000;

        private SafeWaitHandle _timer;
        private bool _created;
        private bool _disposed;

        /// <summary>True once a wait has created a waitable timer and until
        /// disposal. False means the waits fall back to Thread.Sleep.</summary>
        public bool TimerActive => !_disposed && _timer != null && !_timer.IsInvalid;

        public void Wait(int ms)
        {
            if (ms <= 0) return;
            if (!_disposed && !_created)
            {
                _created = true;
                _timer = Create();
            }
            var timer = _timer;
            if (!_disposed && timer != null && !timer.IsInvalid)
            {
                long due = -(long)ms * 10000;
                if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)
                    && WaitForSingleObject(timer, (uint)(ms + WaitSlackMs)) == WaitObject0)
                    return;
            }
            Thread.Sleep(ms);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        private static SafeWaitHandle Create()
        {
            var timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAccess);
            if (timer.IsInvalid)
            {
                timer.Dispose();
                timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAccess);
            }
            return timer;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period,
            IntPtr completion, IntPtr argument, bool resume);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    }
}
