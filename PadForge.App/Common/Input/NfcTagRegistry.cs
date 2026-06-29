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
    internal static class NfcTagRegistry
    {
        public sealed class TagEntry
        {
            // Properties (not fields) so WPF list bindings can read them.
            public string Uid { get; set; }   // uppercase hex, no separators
            public string Name { get; set; }
        }

        private static readonly object _lock = new();
        private static readonly List<TagEntry> _tags = new();

        /// <summary>Raised when the registry changes (input-picker refresh).</summary>
        public static event EventHandler RegistryChanged;

        /// <summary>Snapshot of the registered tags, in button order
        /// (index 0 -> button 1, index 1 -> button 2, ...).</summary>
        public static IReadOnlyList<TagEntry> Tags
        {
            get { lock (_lock) return _tags.Select(t => new TagEntry { Uid = t.Uid, Name = t.Name }).ToList(); }
        }

        public static int Count { get { lock (_lock) return _tags.Count; } }

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

        /// <summary>The 1-based button index for a UID, or -1 if the UID is not
        /// registered. Button 0 is the reader's "Any NFC Tag" button.</summary>
        public static int ButtonForUid(string uid)
        {
            string norm = NormalizeUid(uid);
            if (norm.Length == 0) return -1;
            lock (_lock)
            {
                for (int i = 0; i < _tags.Count; i++)
                    if (string.Equals(_tags[i].Uid, norm, StringComparison.Ordinal))
                        return i + 1;
            }
            return -1;
        }

        /// <summary>Registers (or renames) a tag. The UID is the identity; a second
        /// register of the same UID just updates its name. Names are deduped so two
        /// tags never share a picker label. Returns the final stored name.</summary>
        public static string Register(string uid, string name)
        {
            string norm = NormalizeUid(uid);
            if (norm.Length == 0) return null;
            string baseName = string.IsNullOrWhiteSpace(name) ? "Tag " + norm : name.Trim();
            lock (_lock)
            {
                var existing = _tags.FirstOrDefault(t => string.Equals(t.Uid, norm, StringComparison.Ordinal));
                string final = baseName;
                int k = 2;
                while (_tags.Any(t => !ReferenceEquals(t, existing)
                        && string.Equals(t.Name, final, StringComparison.OrdinalIgnoreCase)))
                    final = $"{baseName} ({k++})";
                if (existing != null) existing.Name = final;
                else _tags.Add(new TagEntry { Uid = norm, Name = final });
                RegistryChanged?.Invoke(null, EventArgs.Empty);
                return final;
            }
        }

        public static void Remove(string uid)
        {
            string norm = NormalizeUid(uid);
            bool removed;
            lock (_lock) removed = _tags.RemoveAll(t => string.Equals(t.Uid, norm, StringComparison.Ordinal)) > 0;
            if (removed) RegistryChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Replaces the registry from persisted settings (load time).</summary>
        public static void LoadRegistry(IEnumerable<(string Uid, string Name)> entries)
        {
            lock (_lock)
            {
                _tags.Clear();
                if (entries != null)
                    foreach (var (uid, name) in entries)
                    {
                        string norm = NormalizeUid(uid);
                        if (norm.Length == 0 || string.IsNullOrWhiteSpace(name)) continue;
                        if (_tags.Any(t => string.Equals(t.Uid, norm, StringComparison.Ordinal))) continue;
                        _tags.Add(new TagEntry { Uid = norm, Name = name.Trim() });
                    }
            }
            RegistryChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Serialisable view for the settings round-trip (save time).</summary>
        public static List<(string Uid, string Name)> SaveRegistry()
        {
            lock (_lock) return _tags.Select(t => (t.Uid, t.Name)).ToList();
        }
    }
}
