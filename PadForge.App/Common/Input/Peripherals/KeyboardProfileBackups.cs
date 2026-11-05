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
    /// A laptop's own keyboard keeps one backup, by its product key
    /// (<see cref="LightMap.Product"/>), since a PC has one and PadForge
    /// writes one profile of it at a time. An external device keeps one of
    /// its own (<see cref="DeviceKey"/>), since a PC can hold two of one
    /// model. Process-wide and saved with the app settings, like the learned
    /// key maps, and saved at once: a backup is worth nothing if it is not on
    /// disk before the first write it guards.
    /// </summary>
    internal static class KeyboardProfileBackups
    {
        /// <summary>A profile's number and its contents as the keyboard
        /// gave them: the two length bytes of its answer, then the profile
        /// byte and the effects.</summary>
        internal readonly record struct Backup(byte Profile, byte[] Contents);

        /// <summary>What comes between a model's product key and a device's
        /// path in <see cref="DeviceKey"/>.</summary>
        private const char PathMark = '|';

        /// <summary>What comes ahead of a key for what the device reported of
        /// PadForge's last picture, kept beside that key's backup and gone with it.</summary>
        private const string ShownMark = "shown:";

        private static string ShownKey(string key, int part)
            => part < 0 ? ShownMark + key : ShownMark + key + "#" + part;

        private static bool IsShownOf(string held, string key)
            => held.Equals(ShownMark + key, StringComparison.OrdinalIgnoreCase)
               || held.StartsWith(ShownMark + key + "#", StringComparison.OrdinalIgnoreCase);

        /// <summary>Ahead of a write of a stored picture: what is on record of the
        /// last one goes, so a kill during the write leaves the file saying nothing
        /// and not naming a picture the device no longer shows. False until the
        /// settings file holds that, and no write goes out before it does.
        /// <paramref name="part"/> is the profile, on a device PadForge lights
        /// several of.</summary>
        public static bool ClearShown(string key, int part = -1)
        {
            if (string.IsNullOrEmpty(key)) return true;
            Drop(ShownKey(key, part));
            return Volatile.Read(ref s_version) <= Volatile.Read(ref s_persisted);
        }

        /// <summary>The device reported <paramref name="lighting"/> right after a
        /// picture of PadForge's.</summary>
        public static void NoteShown(string key, byte[] lighting, int part = -1)
        {
            if (!string.IsNullOrEmpty(key)) Take(ShownKey(key, part), 0, lighting);
        }

        /// <summary>Whether lighting of the shape PadForge leaves can be PadForge's
        /// own: it is what the device reported of PadForge's last picture, or
        /// nothing is on record of that. Different colors are someone else's, set
        /// since, and they stay.</summary>
        public static bool MayBePadForges(string key, ReadOnlySpan<byte> lighting, int part = -1)
            => Get(ShownKey(key, part)) is not { } shown || shown.Contents.AsSpan().SequenceEqual(lighting);

        private sealed record Entry(byte Profile, byte[] Contents, int Version);

        private static readonly object s_lock = new();
        private static Dictionary<string, Entry> s_entries = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> s_failures = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> s_failedAt = new(StringComparer.OrdinalIgnoreCase);
        private static Func<long> s_ticks = () => Environment.TickCount64;
        private static int s_version;
        private static int s_persisted;
        private static bool s_persistAtOnce;
        private static Func<string, bool> s_pathPresent;

        /// <summary>Raised after a backup was taken or dropped. Any thread.
        /// The service saves the settings on it.</summary>
        public static event EventHandler Changed;

        /// <summary>The longest contents a backup holds: an answer's 960
        /// bytes less the report ID and the opcode.</summary>
        internal const int MaxContents = 958;

        /// <summary>The key an external device's backup is kept under: its
        /// model's product key and the path of the collection PadForge wrote
        /// through. Windows gives two present devices two paths whatever
        /// serial numbers they report, and the path is at hand with no handle
        /// open, which <see cref="HidLightingModel.Owed"/> needs. A channel
        /// with no path keeps the product key.</summary>
        internal static string DeviceKey(string product, string path)
            => string.IsNullOrEmpty(path) ? product : product + PathMark + path;

        /// <summary>The key a session of the device at
        /// <paramref name="path"/> keeps its backup under. A device with no
        /// serial number takes a new path on another USB port, so with no
        /// backup of its own it takes over one a device of its model left at
        /// a path that is gone. A session checks a backup against what its
        /// device shows, so a backup that was another device's is dropped and
        /// not written. A device away on another port with no session keeps
        /// its backup, under its old path, until a claim opens it.</summary>
        internal static string SessionKey(string product, string path)
        {
            string key = DeviceKey(product, path);
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(product)) return key;
            string prefix = product + PathMark;
            List<string> others = null;
            lock (s_lock)
            {
                if (s_entries.ContainsKey(key)) return key;
                foreach (string held in s_entries.Keys)
                    if (held.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) (others ??= new()).Add(held);
            }
            if (others == null) return key;
            foreach (string other in others)
            {
                bool present;
                try { present = (s_pathPresent ?? PathPresent)(other.Substring(prefix.Length)); }
                catch { present = true; }
                if (present) continue;
                Move(other, key);
                break;
            }
            return key;
        }

        /// <summary>Whether a collection is still in the system: an open with
        /// no access right, which asks the device nothing. The test host
        /// opens no device (<see cref="RealHidAccess"/>), so to it every path
        /// is present.</summary>
        private static bool PathPresent(string path)
        {
            if (RealHidAccess.Refused) return true;
            using var channel = HidReportChannel.OpenFeatureOnly(path);
            return channel != null;
        }

        private static void Move(string from, string to)
        {
            lock (s_lock)
            {
                if (!s_entries.TryGetValue(from, out var entry) || s_entries.ContainsKey(to)) return;
                var entries = new Dictionary<string, Entry>(s_entries, StringComparer.OrdinalIgnoreCase);
                entries.Remove(from);
                int version = Interlocked.Increment(ref s_version);
                entries[to] = entry with { Version = version };
                foreach (string held in s_entries.Keys)
                {
                    if (!IsShownOf(held, from)) continue;
                    entries.Remove(held);
                    entries[ShownMark + to + held.Substring(ShownMark.Length + from.Length)] = s_entries[held] with { Version = version };
                }
                s_entries = entries;
                s_failures.Remove(from);
                if (s_persistAtOnce) Volatile.Write(ref s_persisted, version);
            }
            Raise();
        }

        /// <summary>The backup on record for <paramref name="key"/>, or
        /// null.</summary>
        public static Backup? Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (s_lock)
                return s_entries.TryGetValue(key, out var entry) ? new Backup(entry.Profile, entry.Contents) : null;
        }

        /// <summary>Records what <paramref name="profile"/> of the device or
        /// keyboard keyed <paramref name="key"/> holds, in place of any
        /// backup under that key.</summary>
        public static void Take(string key, byte profile, byte[] contents)
        {
            if (string.IsNullOrEmpty(key) || contents == null || contents.Length < 3 || contents.Length > MaxContents)
                return;
            lock (s_lock)
            {
                var entries = new Dictionary<string, Entry>(s_entries, StringComparer.OrdinalIgnoreCase);
                int version = Interlocked.Increment(ref s_version);
                entries[key] = new Entry(profile, (byte[])contents.Clone(), version);
                s_entries = entries;
                s_failures.Remove(key);
                if (s_persistAtOnce) Volatile.Write(ref s_persisted, version);
            }
            Raise();
        }

        /// <summary>Forgets the backup of <paramref name="key"/>: the
        /// profile holds its own contents again, or someone else's.</summary>
        public static void Drop(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (s_lock)
            {
                if (!s_entries.ContainsKey(key)) return;
                var entries = new Dictionary<string, Entry>(s_entries, StringComparer.OrdinalIgnoreCase);
                entries.Remove(key);
                foreach (string held in s_entries.Keys)
                    if (IsShownOf(held, key)) entries.Remove(held);
                s_entries = entries;
                s_failures.Remove(key);
                int version = Interlocked.Increment(ref s_version);
                if (s_persistAtOnce) Volatile.Write(ref s_persisted, version);
            }
            Raise();
        }

        /// <summary>How long after its last failed try a backup's count of
        /// failures clears. What fails a try can pass, another program's
        /// exchange with the keyboard or a late answer, so a device is never
        /// locked out for the run: it gets its tries again a minute on.</summary>
        internal const int FailuresClearMs = 60000;

        /// <summary>How many times in a row this run failed to put the backup
        /// of <paramref name="key"/> back, with the last of them less than
        /// <see cref="FailuresClearMs"/> ago.</summary>
        public static int Failures(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            lock (s_lock)
            {
                if (!s_failures.TryGetValue(key, out int failures)) return 0;
                if (s_ticks() - s_failedAt[key] < FailuresClearMs) return failures;
                s_failures.Remove(key);
                s_failedAt.Remove(key);
                return 0;
            }
        }

        /// <summary>Counts a try that did not put the backup of
        /// <paramref name="key"/> back. Not saved: the next run tries
        /// again.</summary>
        public static void NoteFailure(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (s_lock)
            {
                s_failures[key] = (s_failures.TryGetValue(key, out int failures) ? failures : 0) + 1;
                s_failedAt[key] = s_ticks();
            }
        }

        /// <summary>Whether the backup of <paramref name="key"/> on
        /// record has reached the settings file.</summary>
        public static bool IsPersisted(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (s_lock)
                return s_entries.TryGetValue(key, out var entry) && entry.Version <= Volatile.Read(ref s_persisted);
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
        internal static void ResetForTest(bool persistAtOnce = false, Func<string, bool> pathPresent = null,
            Func<long> ticks = null)
        {
            lock (s_lock)
            {
                s_pathPresent = pathPresent;
                s_ticks = ticks ?? (() => Environment.TickCount64);
                s_entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                s_failures.Clear();
                int version = Interlocked.Increment(ref s_version);
                Volatile.Write(ref s_persisted, version);
                s_persistAtOnce = persistAtOnce;
            }
        }
    }
}
