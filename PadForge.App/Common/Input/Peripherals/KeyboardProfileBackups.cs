using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// What a keyboard's stored lighting profile held before PadForge wrote
    /// its colors over it (#557). A Lenovo Legion Gen 10 keyboard shows
    /// PadForge's colors only from a profile it keeps in its own memory
    /// (<see cref="LenovoLegionLighting"/>), so the hand-back has to write
    /// the profile's old contents back, and a run that ends without a
    /// hand-back, as a forced kill does, leaves that owed to the next run.
    /// One backup per keyboard model, by its product key
    /// (<see cref="LightMap.Product"/>), since PadForge writes one profile
    /// of a keyboard at a time. Process-wide and saved with the app
    /// settings, like the learned key maps, and saved at once: a backup is
    /// worth nothing if it is not on disk before the first write it
    /// guards.
    /// </summary>
    internal static class KeyboardProfileBackups
    {
        /// <summary>A profile's number and its contents as the keyboard
        /// gave them: the two length bytes of its answer, then the profile
        /// byte and the effects.</summary>
        internal readonly record struct Backup(byte Profile, byte[] Contents);

        private sealed record Entry(byte Profile, byte[] Contents, int Version);

        private static readonly object s_lock = new();
        private static Dictionary<string, Entry> s_entries = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> s_failures = new(StringComparer.OrdinalIgnoreCase);
        private static int s_version;
        private static int s_persisted;
        private static bool s_persistAtOnce;

        /// <summary>Raised after a backup was taken or dropped. Any thread.
        /// The service saves the settings on it.</summary>
        public static event EventHandler Changed;

        /// <summary>The longest contents a backup holds: an answer's 960
        /// bytes less the report ID and the opcode.</summary>
        internal const int MaxContents = 958;

        /// <summary>The backup on record for <paramref name="product"/>, or
        /// null.</summary>
        public static Backup? Get(string product)
        {
            if (string.IsNullOrEmpty(product)) return null;
            lock (s_lock)
                return s_entries.TryGetValue(product, out var entry) ? new Backup(entry.Profile, entry.Contents) : null;
        }

        /// <summary>Records what <paramref name="profile"/> of
        /// <paramref name="product"/> holds, in place of any backup of the
        /// model.</summary>
        public static void Take(string product, byte profile, byte[] contents)
        {
            if (string.IsNullOrEmpty(product) || contents == null || contents.Length < 3 || contents.Length > MaxContents)
                return;
            lock (s_lock)
            {
                var entries = new Dictionary<string, Entry>(s_entries, StringComparer.OrdinalIgnoreCase);
                int version = Interlocked.Increment(ref s_version);
                entries[product] = new Entry(profile, (byte[])contents.Clone(), version);
                s_entries = entries;
                s_failures.Remove(product);
                if (s_persistAtOnce) Volatile.Write(ref s_persisted, version);
            }
            Raise();
        }

        /// <summary>Forgets the backup of <paramref name="product"/>: the
        /// profile holds its own contents again, or someone else's.</summary>
        public static void Drop(string product)
        {
            if (string.IsNullOrEmpty(product)) return;
            lock (s_lock)
            {
                if (!s_entries.ContainsKey(product)) return;
                var entries = new Dictionary<string, Entry>(s_entries, StringComparer.OrdinalIgnoreCase);
                entries.Remove(product);
                s_entries = entries;
                s_failures.Remove(product);
                int version = Interlocked.Increment(ref s_version);
                if (s_persistAtOnce) Volatile.Write(ref s_persisted, version);
            }
            Raise();
        }

        /// <summary>How many times this run failed to put the backup of
        /// <paramref name="product"/> back.</summary>
        public static int Failures(string product)
        {
            if (string.IsNullOrEmpty(product)) return 0;
            lock (s_lock)
                return s_failures.TryGetValue(product, out int failures) ? failures : 0;
        }

        /// <summary>Counts a try that did not put the backup of
        /// <paramref name="product"/> back. Not saved: the next run tries
        /// again.</summary>
        public static void NoteFailure(string product)
        {
            if (string.IsNullOrEmpty(product)) return;
            lock (s_lock)
                s_failures[product] = (s_failures.TryGetValue(product, out int failures) ? failures : 0) + 1;
        }

        /// <summary>Whether the backup of <paramref name="product"/> on
        /// record has reached the settings file.</summary>
        public static bool IsPersisted(string product)
        {
            if (string.IsNullOrEmpty(product)) return false;
            lock (s_lock)
                return s_entries.TryGetValue(product, out var entry) && entry.Version <= Volatile.Read(ref s_persisted);
        }

        /// <summary>Every backup, for the settings file, with the version
        /// they stand for: the save that writes them hands it back to
        /// <see cref="Persisted"/>.</summary>
        public static IReadOnlyList<(string Product, int Profile, string Contents)> SaveRegistry(out int version)
        {
            lock (s_lock)
            {
                version = Volatile.Read(ref s_version);
                return s_entries.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(p => (p.Key, (int)p.Value.Profile, Convert.ToHexString(p.Value.Contents))).ToList();
            }
        }

        /// <summary>The settings file now holds the backups as they stood
        /// at <paramref name="version"/>.</summary>
        public static void Persisted(int version)
        {
            int seen;
            do
            {
                seen = Volatile.Read(ref s_persisted);
                if (version <= seen) return;
            }
            while (Interlocked.CompareExchange(ref s_persisted, version, seen) != seen);
        }

        /// <summary>Loads the settings file's backups in place of what is
        /// held. They came from the file, so they are on it. A file that names
        /// none leaves the backups held as they are: they are the devices'
        /// state, not settings, each the only copy of a profile PadForge
        /// wrote over, so a reset to defaults, which loads a fresh
        /// AppSettingsData, keeps them, as it keeps a Bliss-Box adapter's own
        /// pictures (SettingsService.ResetToDefaults).</summary>
        public static void LoadRegistry(IEnumerable<(string Product, int Profile, string Contents)> saved)
        {
            if (saved == null) return;
            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (product, profile, text) in saved)
            {
                if (string.IsNullOrWhiteSpace(product) || profile < 0 || profile > byte.MaxValue
                    || string.IsNullOrWhiteSpace(text))
                    continue;
                byte[] contents;
                try { contents = Convert.FromHexString(text.Trim()); }
                catch (FormatException) { continue; }
                if (contents.Length < 3 || contents.Length > MaxContents) continue;
                entries[product.Trim()] = new Entry((byte)profile, contents, 0);
            }
            lock (s_lock)
            {
                s_entries = entries;
                s_failures.Clear();
                Interlocked.Increment(ref s_version);
            }
        }

        private static void Raise()
        {
            try { Changed?.Invoke(null, EventArgs.Empty); } catch { }
        }

        /// <summary>Clears every backup. <paramref name="persistAtOnce"/>
        /// counts each change as saved the moment it is made, for a test
        /// with no settings service.</summary>
        internal static void ResetForTest(bool persistAtOnce = false)
        {
            lock (s_lock)
            {
                s_entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                s_failures.Clear();
                int version = Interlocked.Increment(ref s_version);
                Volatile.Write(ref s_persisted, version);
                s_persistAtOnce = persistAtOnce;
            }
        }
    }
}
