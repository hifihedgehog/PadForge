using System;
using System.Collections.Generic;
using System.Linq;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Persisted registry of named NFC tags for issue #150 (per-tag binding).
    ///
    /// Each entry maps a tag UID (uppercase hex, as <see cref="PadForge.Services.
    /// NfcReaderService"/> reports it) to a user-chosen name. A registered tag is
    /// exposed by <see cref="NfcReaderDevice"/> as its own momentary button: button
    /// 0 stays "Any NFC Tag", and the n-th registered tag is button n (1-based), so
    /// a tap of a specific tag fires only the macros bound to that tag through the
    /// existing raw-button trigger path. Mirrors <see cref="PadForge.Common.
    /// SoundPackageManager"/>: a static registry, a <see cref="RegistryChanged"/>
    /// event for UI refresh, and a Load/Save pair that <see cref="PadForge.Services.
    /// SettingsService"/> round-trips through the settings file. The registry is
    /// reader-agnostic (one tag set across all readers), since a UID identifies the
    /// tag, not the reader.
    /// </summary>
    public static class NfcTagRegistry
    {
        public sealed class TagEntry
        {
            // Properties (not fields) so WPF list bindings can read them.
            public string Uid { get; set; }   // uppercase hex, no separators
            public string Name { get; set; }
            /// <summary>STABLE 1-based raw-button index this tag occupies. Assigned
            /// once at registration and never changed, so a macro bound to this tag
            /// keeps firing it even after OTHER tags are added or removed. Button 0
            /// is the reader's "Any NFC Tag" button, so tag buttons start at 1.</summary>
            public int Button { get; set; }
        }

        // Buttons live in CustomInputState.Buttons[256], so 0 = Any and 1..255 are
        // tags. A registry beyond that is rejected rather than overflowing the array.
        private const int MaxButton = 255;

        private static readonly object _lock = new();
        private static readonly List<TagEntry> _tags = new();

        /// <summary>Raised when the registry changes (input-picker refresh).</summary>
        public static event EventHandler RegistryChanged;

        /// <summary>Snapshot of the registered tags (ordered by their stable
        /// button), each with the button it occupies.</summary>
        public static IReadOnlyList<TagEntry> Tags
        {
            get
            {
                lock (_lock)
                    return _tags.OrderBy(t => t.Button)
                                .Select(t => new TagEntry { Uid = t.Uid, Name = t.Name, Button = t.Button })
                                .ToList();
            }
        }

        public static int Count { get { lock (_lock) return _tags.Count; } }

        /// <summary>Highest button index in use (0 if no tags), so the device can
        /// report a raw-button count that spans every assigned tag.</summary>
        public static int MaxButtonInUse { get { lock (_lock) return _tags.Count == 0 ? 0 : _tags.Max(t => t.Button); } }

        // Lowest 1..MaxButton not currently occupied, or -1 if the registry is full.
        // Caller holds _lock.
        private static int LowestFreeButton()
        {
            for (int b = 1; b <= MaxButton; b++)
                if (!_tags.Any(t => t.Button == b))
                    return b;
            return -1;
        }

        /// <summary>Normalises a UID to the stored form: uppercase hex with any
        /// spaces / colons / dashes (some readers format the UID) stripped, so a
        /// tap compares equal regardless of the source's punctuation.</summary>
        public static string NormalizeUid(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return string.Empty;
            Span<char> buf = stackalloc char[uid.Length];
            int n = 0;
            foreach (char c in uid)
            {
                if (c == ' ' || c == ':' || c == '-') continue;
                buf[n++] = char.ToUpperInvariant(c);
            }
            return new string(buf[..n]);
        }

        /// <summary>The STABLE button index a UID occupies, or -1 if the UID is not
        /// registered. Button 0 is the reader's "Any NFC Tag" button.</summary>
        public static int ButtonForUid(string uid)
        {
            string norm = NormalizeUid(uid);
            if (norm.Length == 0) return -1;
            lock (_lock)
            {
                var t = _tags.FirstOrDefault(x => string.Equals(x.Uid, norm, StringComparison.Ordinal));
                return t?.Button ?? -1;
            }
        }

        /// <summary>Registers (or renames) a tag. The UID is the identity; a second
        /// register of the same UID just updates its name and KEEPS its button. A new
        /// UID is assigned the lowest free button, which never changes afterward, so
        /// adding or removing other tags can't rebind this one. Names are deduped so
        /// two tags never share a picker label. Returns the final stored name, or null
        /// if the UID is empty or the registry is full (255 tags).</summary>
        public static string Register(string uid, string name)
        {
            string norm = NormalizeUid(uid);
            if (norm.Length == 0) return null;
            string baseName = string.IsNullOrWhiteSpace(name) ? "Tag " + norm : name.Trim();
            string final;
            lock (_lock)
            {
                var existing = _tags.FirstOrDefault(t => string.Equals(t.Uid, norm, StringComparison.Ordinal));
                if (existing == null)
                {
                    int button = LowestFreeButton();
                    if (button < 0) return null; // registry full
                    existing = new TagEntry { Uid = norm, Button = button };
                    _tags.Add(existing);
                }
                final = baseName;
                int k = 2;
                while (_tags.Any(t => !ReferenceEquals(t, existing)
                        && string.Equals(t.Name, final, StringComparison.OrdinalIgnoreCase)))
                    final = $"{baseName} ({k++})";
                existing.Name = final;
            }
            // Raise OUTSIDE the lock (mirrors SoundPackageManager): a handler must not
            // run while _lock is held, or a synchronous re-entrant read would matter.
            RegistryChanged?.Invoke(null, EventArgs.Empty);
            return final;
        }

        public static void Remove(string uid)
        {
            string norm = NormalizeUid(uid);
            bool removed;
            lock (_lock) removed = _tags.RemoveAll(t => string.Equals(t.Uid, norm, StringComparison.Ordinal)) > 0;
            if (removed) RegistryChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Replaces the registry from persisted settings (load time). The
        /// stored button is honoured so existing macro bindings stay valid across
        /// restarts; an absent / out-of-range / colliding button (older saves, hand
        /// edits) is reassigned the lowest free one.</summary>
        public static void LoadRegistry(IEnumerable<(string Uid, string Name, int Button)> entries)
        {
            lock (_lock)
            {
                _tags.Clear();
                if (entries != null)
                    foreach (var (uid, name, button) in entries)
                    {
                        string norm = NormalizeUid(uid);
                        if (norm.Length == 0 || string.IsNullOrWhiteSpace(name)) continue;
                        if (_tags.Any(t => string.Equals(t.Uid, norm, StringComparison.Ordinal))) continue;
                        int b = button;
                        if (b < 1 || b > MaxButton || _tags.Any(t => t.Button == b))
                        {
                            b = LowestFreeButton();
                            if (b < 0) break; // full
                        }
                        _tags.Add(new TagEntry { Uid = norm, Name = name.Trim(), Button = b });
                    }
            }
            RegistryChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Serialisable view for the settings round-trip (save time).</summary>
        public static List<(string Uid, string Name, int Button)> SaveRegistry()
        {
            lock (_lock) return _tags.Select(t => (t.Uid, t.Name, t.Button)).ToList();
        }
    }
}
