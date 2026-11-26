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
    /// <para>Three kinds. A row's own stamp: PadForge closed that row and
    /// will open it again (a MIDI backend change). Every row's: the work can
    /// take a row PadForge cannot name offline (a virtual controller's
    /// creation, whose xinputhid pass can briefly drop a physical Xbox pad,
    /// an SDL driver's hint turned off and on, a HID collection restarted).
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

        /// <summary>PadForge's own work takes this row offline now.</summary>
        public static void Row(Guid device)
        {
            if (device != Guid.Empty) s_rows[device] = Environment.TickCount64;
        }

        /// <summary>PadForge's own work can take any row offline now.</summary>
        public static void Every() => Volatile.Write(ref s_every, Environment.TickCount64);

        /// <summary>PadForge's own work drops devices for a while: no row is
        /// believed until they have settled.</summary>
        public static void Settle() => Volatile.Write(ref s_settle, Environment.TickCount64);

        /// <summary>The latest stamp that covers a row, its own or every
        /// row's, or <see cref="Never"/>.</summary>
        public static long At(Guid device)
        {
            long every = Volatile.Read(ref s_every);
            return s_rows.TryGetValue(device, out long own) && own > every ? own : every;
        }

        /// <summary>The latest settle, or <see cref="Never"/>.</summary>
        public static long SettleAt => Volatile.Read(ref s_settle);
    }
}
