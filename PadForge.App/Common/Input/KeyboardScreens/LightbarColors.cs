using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.KeyboardScreens
{
    /// <summary>
    /// The lightbar color each DualShock 4 and DualSense was last sent, by
    /// device and slot (issue #519, the Lightbar mode @eVenent asked for in
    /// discussion #507). The effects dispatcher records a color where it
    /// builds the final bytes for the pad, and only when the frame asserts
    /// the bar: a DualShock 4 frame always does, and a DualSense frame does
    /// with its lightbar enable bit, so a color the pad never took is never
    /// shown. The screen service reads it on the UI thread.
    /// </summary>
    internal static class LightbarColors
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<Guid, (int Slot, byte R, byte G, byte B)> _byDevice = new();

        public static void Record(int slot, Guid device, byte r, byte g, byte b)
        {
            lock (_lock) _byDevice[device] = (slot, r, g, b);
        }

        public static void Clear()
        {
            lock (_lock) _byDevice.Clear();
        }

        /// <summary>The color of the lowest slot with one, among the devices
        /// <paramref name="current"/> says are still on the slot their color
        /// came from. A device that left the slot is forgotten.</summary>
        public static bool TryGetLowest(Func<Guid, int, bool> current, out (byte R, byte G, byte B) color)
            => TryGetLowest(current, null, out color);

        /// <summary>The same, among the slots <paramref name="slots"/>
        /// allows, or every slot when it is null: a keyboard screen follows
        /// the slots its row feeds.
        ///
        /// <para><paramref name="current"/> and <paramref name="slots"/> run
        /// outside the lock. The screen service's checks take the device and
        /// settings locks, and the effects dispatcher calls
        /// <see cref="Record"/> while it holds the device lock, so asking
        /// them under this lock would take the two locks in the opposite
        /// order and freeze the UI and the poll thread. The entries are
        /// copied under the lock, judged outside it, and a device that left
        /// is removed only while its entry is still the one judged, so a
        /// color recorded meanwhile stays.</para></summary>
        public static bool TryGetLowest(Func<Guid, int, bool> current, Func<int, bool> slots, out (byte R, byte G, byte B) color)
        {
            color = default;
            int best = int.MaxValue;
            List<KeyValuePair<Guid, (int Slot, byte R, byte G, byte B)>> entries, gone = null;
            lock (_lock) entries = new(_byDevice);
            foreach (var pair in entries)
            {
                var entry = pair.Value;
                if (current != null && !current(pair.Key, entry.Slot))
                {
                    (gone ??= new()).Add(pair);
                    continue;
                }
                if (entry.Slot >= best || (slots != null && !slots(entry.Slot))) continue;
                best = entry.Slot;
                color = (entry.R, entry.G, entry.B);
            }
            if (gone != null)
            {
                lock (_lock)
                {
                    foreach (var pair in gone)
                        if (_byDevice.TryGetValue(pair.Key, out var now) && now == pair.Value) _byDevice.Remove(pair.Key);
                }
            }
            return best != int.MaxValue;
        }
    }
}
