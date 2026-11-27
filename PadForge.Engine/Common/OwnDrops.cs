using System;
using System.Collections.Concurrent;
using System.Threading;

namespace PadForge.Engine
{
    /// <summary>
    /// When PadForge's own work last took a device row offline, or could
    /// have (#600 item 42). The connection notes say nothing of such a drop
    /// and count it toward nothing (DeviceNoteDirector). Each cause stamps
    /// before it can take a row away, on <see cref="Environment.TickCount64"/>,
    /// the clock the notes' walk reads, so a walk that reads the row away has
    /// already seen the stamp. Work that lasts stamps again when it ends.
    ///
    /// <para>Three kinds. A row's own stamp: PadForge closed that row (a
    /// MIDI backend change, a feature switched off), and the row's absence
    /// is PadForge's until it returns. Every row's: the work can
    /// take a row PadForge cannot name offline (a virtual controller's
    /// creation, whose xinputhid pass can briefly drop a physical Xbox pad,
    /// an SDL driver's hint turned off and on, a HID collection restarted),
    /// and an absence that begins within five seconds of it is the work's.
    /// A settle: the work drops devices for longer than a stamp covers (a
    /// Bluetooth radio restarted, a device's driver changed), so the notes
    /// believe no row until the devices have settled, as after a USB port
    /// PadForge cycled.</para>
    /// </summary>
    public static class OwnDrops
    {
        /// <summary>No stamp.</summary>
        public const long Never = -1;

        private static readonly ConcurrentDictionary<Guid, long> s_rows = new();
        private static long s_every = Never;
        private static long s_settle = Never;
        private static int s_running;

        /// <summary>PadForge's own work takes this row offline now, and
        /// its absence from here on is that work's until the row returns.
        /// A later absence is not: the row's return ends the stamp
        /// (<see cref="Back"/>).</summary>
        public static void Row(Guid device)
        {
            if (device != Guid.Empty) s_rows[device] = Environment.TickCount64;
        }

        /// <summary>The row is online again: its own stamp, if it has one,
        /// ends here, even when the notes never read the row away. Called
        /// when a row turns online (UserDevice.IsOnline).</summary>
        public static void Back(Guid device)
        {
            if (s_rows.ContainsKey(device)) s_rows.TryRemove(device, out _);
        }

        /// <summary>PadForge's own work can take any row offline now.</summary>
        public static void Every() => Volatile.Write(ref s_every, Environment.TickCount64);

        /// <summary>Work that can take any row offline for as long as it
        /// runs, such as a virtual controller's bring-up, which can take
        /// seconds: every row counts as stamped now until the matching
        /// <see cref="End"/>, which stamps every row again.</summary>
        public static void Begin() => Interlocked.Increment(ref s_running);

        /// <summary>The end of work begun with <see cref="Begin"/>.</summary>
        public static void End()
        {
            Every();
            Interlocked.Decrement(ref s_running);
        }

        /// <summary>PadForge's own work drops devices for a while: no row is
        /// believed until they have settled.</summary>
        public static void Settle() => Volatile.Write(ref s_settle, Environment.TickCount64);

        /// <summary>The row's own latest stamp until the row comes back, or
        /// <see cref="Never"/>.</summary>
        public static long RowAt(Guid device) => s_rows.TryGetValue(device, out long at) ? at : Never;

        /// <summary>The latest stamp that covers every row: now while work
        /// begun with <see cref="Begin"/> runs, or <see cref="Never"/>.</summary>
        public static long EveryAt => Volatile.Read(ref s_running) > 0 ? Environment.TickCount64 : Volatile.Read(ref s_every);

        /// <summary>The latest settle, or <see cref="Never"/>.</summary>
        public static long SettleAt => Volatile.Read(ref s_settle);
    }
}
