using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// Raw Input's copy of the reports of the HID collections a problem
    /// recording compares with PadForge's own reading (#600 item 32): the
    /// newest report of each report ID, copied on RawInputListener's pump
    /// thread as each WM_INPUT comes, read by the recorder's thread.
    ///
    /// <para>The pump also carries every keyboard and mouse, so a report
    /// costs a table lookup and a copy into a buffer made when the watch
    /// began, and nothing at all when no recording watches. A copy is
    /// bounded by its slot's size and by the bytes Windows handed over,
    /// never by the size the report's own header claims. A watch the
    /// recorder does not renew lapses after <see cref="Lapse"/>, so a
    /// recording that ends leaves the pump nothing to copy.</para>
    ///
    /// <para>A slot is written under a sequence count, odd while it is
    /// written, and read again when the count moved, so the reader never
    /// takes a lock the pump waits on.</para>
    /// </summary>
    public static class RawReportTap
    {
        /// <summary>The newest report of one report ID.</summary>
        internal sealed class Slot
        {
            internal readonly byte[] Bytes;
            internal int Length;
            internal long Ticks;
            internal int Sequence;

            internal Slot(int size) => Bytes = new byte[size];
        }

        /// <summary>A watched collection: a slot for each report ID asked
        /// for, and when a report of an ID with no slot last came and how
        /// many have come.</summary>
        internal sealed class Watched
        {
            internal readonly Slot[] ByReportId = new Slot[256];
            internal long OtherTicks;
            internal int OtherCount;
        }

        private sealed class Table
        {
            internal readonly Dictionary<IntPtr, Watched> ByHandle = new();
            internal long Renewed;
        }

        private static Table s_table;

        /// <summary>How long a watch lasts without being renewed.</summary>
        internal static readonly long Lapse = Stopwatch.Frequency * 2;

        /// <summary>
        /// Watches exactly these collections from now: each by its Raw Input
        /// handle, with the report IDs whose reports are kept and the most
        /// bytes a report of it holds. A collection watched before keeps its
        /// slots and the reports in them.
        /// </summary>
        public static void Watch(IReadOnlyList<(IntPtr Handle, byte[] ReportIds, int SlotSize)> collections)
        {
            var old = Volatile.Read(ref s_table);
            var table = new Table();
            if (collections != null)
                foreach (var (handle, ids, size) in collections)
                {
                    if (handle == IntPtr.Zero || ids == null || size <= 0) continue;
                    Watched watched = null;
                    if (old != null && old.ByHandle.TryGetValue(handle, out var kept)) watched = kept;
                    watched ??= new Watched();
                    foreach (byte id in ids)
                        if (watched.ByReportId[id] == null || watched.ByReportId[id].Bytes.Length < size)
                            watched.ByReportId[id] = new Slot(size);
                    table.ByHandle[handle] = watched;
                }
            table.Renewed = Stopwatch.GetTimestamp();
            Volatile.Write(ref s_table, table.ByHandle.Count == 0 ? null : table);
        }

        /// <summary>Keeps the watch from lapsing.</summary>
        public static void Renew()
        {
            var table = Volatile.Read(ref s_table);
            if (table != null) Volatile.Write(ref table.Renewed, Stopwatch.GetTimestamp());
        }

        /// <summary>Ends every watch.</summary>
        public static void Clear() => Volatile.Write(ref s_table, null);

        /// <summary>Whether any collection is watched now.</summary>
        public static bool Watching => Volatile.Read(ref s_table) != null;

        /// <summary>
        /// One WM_INPUT record of a HID device: when its collection is
        /// watched, each report of the RAWHID batch at <paramref name="rawHid"/>
        /// goes into its report ID's slot. <paramref name="available"/> is how
        /// many bytes Windows handed over from <paramref name="rawHid"/>. Pump
        /// thread only. Allocates nothing.
        /// </summary>
        internal static void Take(IntPtr hDevice, IntPtr rawHid, int available)
        {
            var table = Volatile.Read(ref s_table);
            if (table == null || !table.ByHandle.TryGetValue(hDevice, out var watched)) return;
            long now = Stopwatch.GetTimestamp();
            if (now - Volatile.Read(ref table.Renewed) > Lapse)
            {
                Interlocked.CompareExchange(ref s_table, null, table);
                return;
            }
            // RAWHID: dwSizeHid, dwCount, then dwCount reports of dwSizeHid
            // bytes, each beginning with its report ID.
            if (available < 8) return;
            int size = Marshal.ReadInt32(rawHid);
            int count = Marshal.ReadInt32(rawHid, 4);
            if (size <= 0 || count <= 0) return;
            for (int r = 0; r < count; r++)
            {
                long start = 8L + (long)r * size;
                if (start + size > available) return;
                IntPtr report = IntPtr.Add(rawHid, (int)start);
                var slot = watched.ByReportId[Marshal.ReadByte(report)];
                if (slot == null)
                {
                    Volatile.Write(ref watched.OtherTicks, now);
                    Volatile.Write(ref watched.OtherCount, watched.OtherCount + 1);
                    continue;
                }
                int length = Math.Min(size, slot.Bytes.Length);
                int sequence = slot.Sequence;
                Volatile.Write(ref slot.Sequence, sequence + 1);
                // A release store keeps earlier writes before it, not later
                // ones: without the fence an ARM64 core may show the new
                // bytes before the odd count, and a reader would take a torn
                // report as whole.
                Interlocked.MemoryBarrier();
                Marshal.Copy(report, slot.Bytes, 0, length);
                slot.Length = length;
                slot.Ticks = now;
                Volatile.Write(ref slot.Sequence, sequence + 2);
            }
        }

        /// <summary>
        /// The newest report of a report ID of a watched collection, copied
        /// into <paramref name="into"/>, with its length and the Stopwatch time
        /// it came. False when none came yet, or the pump wrote the slot all
        /// through four tries.
        /// </summary>
        public static bool TryCopy(IntPtr handle, byte reportId, byte[] into, out int length, out long ticks)
        {
            length = 0;
            ticks = 0;
            var table = Volatile.Read(ref s_table);
            if (table == null || into == null || !table.ByHandle.TryGetValue(handle, out var watched)) return false;
            var slot = watched.ByReportId[reportId];
            if (slot == null) return false;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int before = Volatile.Read(ref slot.Sequence);
                if (before == 0) return false;
                if ((before & 1) != 0) continue;
                int n = Math.Min(slot.Length, into.Length);
                long t = slot.Ticks;
                Buffer.BlockCopy(slot.Bytes, 0, into, 0, n);
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref slot.Sequence) != before) continue;
                length = n;
                ticks = t;
                return true;
            }
            return false;
        }

        /// <summary>When a report of a watched collection came whose report
        /// ID has no slot, the report naming none of the controls compared,
        /// or 0.</summary>
        public static long OtherReportTicks(IntPtr handle)
        {
            var table = Volatile.Read(ref s_table);
            return table != null && table.ByHandle.TryGetValue(handle, out var watched) ? Volatile.Read(ref watched.OtherTicks) : 0;
        }

        /// <summary>How many reports of a watched collection have come whose
        /// report ID has no slot, counting on past int.MaxValue by wrapping,
        /// or 0.</summary>
        public static int OtherReportCount(IntPtr handle)
        {
            var table = Volatile.Read(ref s_table);
            return table != null && table.ByHandle.TryGetValue(handle, out var watched) ? Volatile.Read(ref watched.OtherCount) : 0;
        }

        /// <summary>Test seam: makes the watch as old as one that lapsed.</summary>
        internal static void LapseForTest()
        {
            var table = Volatile.Read(ref s_table);
            if (table != null) Volatile.Write(ref table.Renewed, Stopwatch.GetTimestamp() - Lapse - 1);
        }
    }
}
