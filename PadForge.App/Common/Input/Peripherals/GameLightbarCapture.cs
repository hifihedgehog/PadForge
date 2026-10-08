using System;
using System.Threading;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The lightbar color a game writes to one virtual PlayStation controller
    /// (#494), owned by that controller so the color moves with it when a
    /// slot reorder retargets it to another pad index, the way its inbound
    /// rumble pack does. Written from the controller's output reader thread
    /// only, read lock-free by the effects dispatchers and the phase watcher.
    ///
    /// <para>Every Sony virtual feeds it, the DualShock 4 included. The
    /// DualSense's per-subsystem mirror in <see cref="UserEffectsDispatcher"/>
    /// parses the DualSense report alone, so a game on a DualShock 4 slot
    /// reached the old Chroma and LIGHTSYNC mirrors and nothing else. Here
    /// the profile codec's decoded <c>lightbar</c> field is read, which
    /// carries the same three bytes for both families. Only frames the trust
    /// gate accepts reach it: the declared report length and a valid
    /// checksum.</para>
    /// </summary>
    internal sealed class GameLightbar
    {
        /// <summary>The last lightbar write: its tick shifted left 24 bits,
        /// then the color, so one atomic read gives a matching pair. Zero
        /// before any write.</summary>
        private long _lightbar;

        /// <summary>The tick of the last trusted frame of any kind.</summary>
        private long _frameTick;

        /// <summary>The phase the watcher last reported. Watcher thread only.</summary>
        internal GameLightbarPhase ReportedPhase;

        /// <summary>A trusted frame carrying a lightbar write. True when
        /// what a device shows changes: a new color, or a write after the
        /// grace window had closed.</summary>
        public bool Capture(byte r, byte g, byte b, long now)
        {
            int rgb = (r << 16) | (g << 8) | b;
            long previous = Interlocked.Exchange(ref _lightbar, Pack(now, rgb));
            Volatile.Write(ref _frameTick, now);
            if (previous == 0) return true;
            Unpack(previous, out long tick, out int old);
            return old != rgb || now - tick >= GameLightbarCapture.GraceMs;
        }

        /// <summary>A trusted frame without a lightbar write. The game is
        /// still there, so the color it set earlier keeps its claim. A
        /// claim that already lapsed is dropped rather than revived, so a
        /// second game that never writes the bar does not bring back the
        /// first game's color.</summary>
        public void NoteFrame(long now)
        {
            long packed = Volatile.Read(ref _lightbar);
            if (packed != 0 && Lapsed(packed, Volatile.Read(ref _frameTick), now))
            {
                Clear();
                return;
            }
            Volatile.Write(ref _frameTick, now);
        }

        /// <summary>Drops the record: the slot's devices changed, or the
        /// game went quiet past the lapse.</summary>
        public void Clear()
        {
            Interlocked.Exchange(ref _lightbar, 0);
            Volatile.Write(ref _frameTick, 0);
        }

        public GameLightbarPhase Phase(long now)
        {
            long packed = Volatile.Read(ref _lightbar);
            if (packed == 0) return GameLightbarPhase.None;
            if (Lapsed(packed, Volatile.Read(ref _frameTick), now)) return GameLightbarPhase.None;
            Unpack(packed, out long tick, out _);
            return now - tick < GameLightbarCapture.GraceMs ? GameLightbarPhase.Fresh : GameLightbarPhase.Held;
        }

        /// <summary>The game's color right now, if it wrote within the grace
        /// window, and the last color it wrote, while it is still there.
        /// Colors are 0x00RRGGBB.</summary>
        public void Read(long now, out int? fresh, out int? last)
        {
            fresh = null;
            last = null;
            long packed = Volatile.Read(ref _lightbar);
            if (packed == 0 || Lapsed(packed, Volatile.Read(ref _frameTick), now)) return;
            Unpack(packed, out long tick, out int rgb);
            last = rgb;
            if (now - tick < GameLightbarCapture.GraceMs) fresh = rgb;
        }

        private static bool Lapsed(long packed, long frameTick, long now)
        {
            Unpack(packed, out long tick, out _);
            return now - Math.Max(tick, frameTick) >= GameLightbarCapture.LapseMs;
        }

        private static long Pack(long tick, int rgb) => (Math.Max(tick, 1) << 24) | (long)(rgb & 0xFFFFFF);

        private static void Unpack(long packed, out long tick, out int rgb)
        {
            tick = packed >> 24;
            rgb = (int)(packed & 0xFFFFFF);
        }
    }

    internal enum GameLightbarPhase
    {
        /// <summary>No game color: never written, cleared, or lapsed.</summary>
        None,
        /// <summary>Written within the grace window: the game's color wins.</summary>
        Fresh,
        /// <summary>Past the grace window with the game still there.</summary>
        Held,
    }

    /// <summary>
    /// Which virtual controller's game color each slot shows (#494), for the
    /// mice, keyboards and lighting rows assigned to it.
    ///
    /// <para>The DualSense's rules, applied per slot: a write counts as the
    /// game's for <see cref="GraceMs"/>, after which a device left at the
    /// player-number default keeps the game's last color, and fifteen
    /// seconds without any valid frame from the game ends its claim
    /// (<see cref="LapseMs"/>, the dispatcher's <c>ExternalClaimLapseMs</c>).</para>
    ///
    /// <para>A dispatcher computes colors only when it runs, and a slot whose
    /// devices are not animated may not run again for a long time. So a new
    /// color, the end of the grace window and the lapse each ask that slot's
    /// dispatcher for a pass (<see cref="UserEffectsDispatcher.RequestPeripheralRefresh"/>):
    /// the color on the output reader thread, the two timeouts from a
    /// watcher that runs while any controller is registered.</para>
    /// </summary>
    internal static class GameLightbarCapture
    {
        /// <summary>How long a write counts as the game's: the DualSense
        /// mirror's <c>ExternalSubsystemGraceMs</c>.</summary>
        internal const long GraceMs = 1500;

        /// <summary>How long a slot can go without a valid frame before the
        /// game is treated as gone.</summary>
        internal const long LapseMs = 15_000;

        /// <summary>How often the watcher looks for a phase change.</summary>
        internal const int WatchMs = 250;

        private static readonly GameLightbar[] s_bySlot = new GameLightbar[InputManager.MaxPads];
        private static readonly object s_watchLock = new();
        private static Timer s_watch;
        private static int s_watchBusy;

        /// <summary>A controller took this slot: created there, or moved
        /// there by a reorder.</summary>
        public static void Publish(int padIndex, GameLightbar source)
        {
            if ((uint)padIndex >= (uint)s_bySlot.Length || source == null) return;
            Volatile.Write(ref s_bySlot[padIndex], source);
            lock (s_watchLock)
                s_watch ??= new Timer(_ => Watch(), null, WatchMs, WatchMs);
        }

        /// <summary>A controller left this slot. A controller that already
        /// took its place in a swap keeps it.</summary>
        public static void Withdraw(int padIndex, GameLightbar source)
        {
            if ((uint)padIndex >= (uint)s_bySlot.Length || source == null) return;
            if (Interlocked.CompareExchange(ref s_bySlot[padIndex], null, source) != source) return;
            UserEffectsDispatcher.RequestPeripheralRefresh(padIndex);
            lock (s_watchLock)
            {
                for (int i = 0; i < s_bySlot.Length; i++)
                    if (Volatile.Read(ref s_bySlot[i]) != null) return;
                s_watch?.Dispose();
                s_watch = null;
            }
        }

        /// <summary>A controller went away: every slot it still fed lets it
        /// go.</summary>
        public static void WithdrawEverywhere(GameLightbar source)
        {
            if (source == null) return;
            for (int pad = 0; pad < s_bySlot.Length; pad++)
                if (Volatile.Read(ref s_bySlot[pad]) == source) Withdraw(pad, source);
        }

        /// <summary>The slot's devices changed, so the next color is a fresh
        /// claim, the DualSense mirror's rule for an assignment change.</summary>
        public static void Forget(int padIndex)
        {
            if ((uint)padIndex >= (uint)s_bySlot.Length) return;
            Volatile.Read(ref s_bySlot[padIndex])?.Clear();
        }

        public static void Read(int padIndex, long nowMs, out int? fresh, out int? last)
        {
            fresh = null;
            last = null;
            if ((uint)padIndex >= (uint)s_bySlot.Length) return;
            Volatile.Read(ref s_bySlot[padIndex])?.Read(nowMs, out fresh, out last);
        }

        /// <summary>A new game color on this slot. Called from the output
        /// reader thread, which only queues the dispatcher's pass.</summary>
        public static void NotifyChanged(int padIndex) => UserEffectsDispatcher.RequestPeripheralRefresh(padIndex);

        private static void Watch()
        {
            // Timer callbacks overlap when one runs long. The phases each
            // controller last reported belong to one pass at a time.
            if (Interlocked.Exchange(ref s_watchBusy, 1) != 0) return;
            try
            {
                long now = Environment.TickCount64;
                for (int pad = 0; pad < s_bySlot.Length; pad++)
                {
                    var source = Volatile.Read(ref s_bySlot[pad]);
                    if (source == null) continue;
                    var phase = source.Phase(now);
                    if (phase == source.ReportedPhase) continue;
                    // A lapsed record is left to the reader thread. NoteFrame
                    // clears it before a later frame could revive it, and
                    // Phase and Read already treat it as gone. A clear from
                    // here could erase a color the game wrote between this
                    // read and the clear.
                    source.ReportedPhase = phase;
                    // Fresh is reported by the capture itself.
                    if (phase != GameLightbarPhase.Fresh) UserEffectsDispatcher.RequestPeripheralRefresh(pad);
                }
            }
            catch
            {
                // A timer callback must never take the process down.
            }
            finally
            {
                Volatile.Write(ref s_watchBusy, 0);
            }
        }

        /// <summary>Test seam: forgets every slot and stops the watcher.</summary>
        internal static void ResetForTest()
        {
            for (int i = 0; i < s_bySlot.Length; i++) Volatile.Write(ref s_bySlot[i], null);
            lock (s_watchLock)
            {
                s_watch?.Dispose();
                s_watch = null;
            }
        }
    }
}
