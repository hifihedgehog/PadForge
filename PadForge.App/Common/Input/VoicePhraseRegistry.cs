using System;
using System.Collections.Generic;
using System.Linq;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Persisted registry of named voice phrases for issue #315 (voice macros).
    ///
    /// Each entry maps a spoken phrase to a user-chosen name and a STABLE
    /// 1-based raw-button index. A registered phrase is exposed by
    /// <see cref="VoiceRecognizerDevice"/> as its own momentary button: button
    /// 0 stays "Any Phrase", and each phrase keeps the button it was assigned
    /// at registration forever, so removing a middle phrase leaves a gap
    /// rather than renumbering the others (renumbering would silently repoint
    /// every existing mapping). Mirrors <see cref="NfcTagRegistry"/>: a static
    /// registry, a <see cref="RegistryChanged"/> event for UI refresh, and a
    /// Load/Save pair that SettingsService round-trips through PadForge.xml.
    ///
    /// Phrase identity is the NORMALIZED text (trimmed, inner whitespace
    /// collapsed, case-folded), so "Reload Weapon" and "reload  weapon" are
    /// one phrase. GAVPI dispatches on the exact recognized string and lets
    /// near-duplicates collide silently; normalizing at the door is the fix.
    /// </summary>
    public static class VoicePhraseRegistry
    {
        public sealed class PhraseEntry
        {
            // Properties (not fields) so WPF list bindings can read them.
            /// <summary>Normalized phrase text: the identity, and the string
            /// the grammar is built from.</summary>
            public string Phrase { get; set; }
            /// <summary>Display name in the picker (defaults to the phrase).</summary>
            public string Name { get; set; }
            /// <summary>STABLE 1-based raw-button index this phrase occupies.
            /// Assigned once at registration and never changed. Button 0 is
            /// the recognizer's "Any Phrase" button, so phrases start at 1.</summary>
            public int Button { get; set; }
        }

        // Buttons live in CustomInputState.Buttons[256], so 0 = Any and 1..255
        // are phrases. Registration beyond that is rejected, not overflowed.
        private const int MaxButton = 255;

        private static readonly object _lock = new();
        private static readonly List<PhraseEntry> _phrases = new();

        /// <summary>Raised when the registry changes (input-picker refresh and
        /// the recognizer's grammar rebuild both ride this).</summary>
        public static event EventHandler RegistryChanged;

        /// <summary>Snapshot of the registered phrases ordered by their stable
        /// button, each copied so a caller cannot mutate the registry.</summary>
        public static IReadOnlyList<PhraseEntry> Phrases
        {
            get
            {
                lock (_lock)
                    return _phrases.OrderBy(p => p.Button)
                                   .Select(p => new PhraseEntry { Phrase = p.Phrase, Name = p.Name, Button = p.Button })
                                   .ToList();
            }
        }

        public static int Count { get { lock (_lock) return _phrases.Count; } }

        /// <summary>Highest button index in use (0 if none), so the device can
        /// report a raw-button span covering every assigned phrase.</summary>
        public static int MaxButtonInUse { get { lock (_lock) return _phrases.Count == 0 ? 0 : _phrases.Max(p => p.Button); } }

        // Lowest 1..MaxButton not currently occupied, or -1 when full.
        // Caller holds _lock.
        private static int LowestFreeButton()
        {
            for (int b = 1; b <= MaxButton; b++)
                if (!_phrases.Any(p => p.Button == b))
                    return b;
            return -1;
        }

        /// <summary>Normalizes a phrase to its stored identity: trimmed, runs
        /// of internal whitespace collapsed to one space, lower-cased with the
        /// invariant culture. Recognition results and registrations both pass
        /// through here, so a match compares equal regardless of the source's
        /// spacing or capitalization.</summary>
        public static string NormalizePhrase(string phrase)
        {
            if (string.IsNullOrWhiteSpace(phrase)) return string.Empty;
            Span<char> buf = phrase.Length <= 256 ? stackalloc char[phrase.Length] : new char[phrase.Length];
            int n = 0;
            bool pendingSpace = false;
            foreach (char c in phrase)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (n > 0) pendingSpace = true;
                    continue;
                }
                if (pendingSpace) { buf[n++] = ' '; pendingSpace = false; }
                buf[n++] = char.ToLowerInvariant(c);
            }
            return new string(buf[..n]);
        }

        /// <summary>The STABLE button a phrase occupies, or -1 when it is not
        /// registered. Button 0 is the device's "Any Phrase" button.</summary>
        public static int ButtonForPhrase(string phrase)
        {
            string norm = NormalizePhrase(phrase);
            if (norm.Length == 0) return -1;
            lock (_lock)
            {
                var p = _phrases.FirstOrDefault(x => string.Equals(x.Phrase, norm, StringComparison.Ordinal));
                return p?.Button ?? -1;
            }
        }

        /// <summary>Registers (or renames) a phrase. The normalized text is the
        /// identity; re-registering the same phrase updates its name and KEEPS
        /// its button. A new phrase gets the lowest free button, which never
        /// changes afterward. Names are deduped so two phrases never share a
        /// picker label. Returns the final stored name, or null when the phrase
        /// is empty or the registry is full.</summary>
        public static string Register(string phrase, string name)
        {
            string norm = NormalizePhrase(phrase);
            if (norm.Length == 0) return null;
            string baseName = string.IsNullOrWhiteSpace(name) ? norm : name.Trim();
            string final;
            lock (_lock)
            {
                var existing = _phrases.FirstOrDefault(p => string.Equals(p.Phrase, norm, StringComparison.Ordinal));
                if (existing == null)
                {
                    int button = LowestFreeButton();
                    if (button < 0) return null; // registry full
                    existing = new PhraseEntry { Phrase = norm, Button = button };
                    _phrases.Add(existing);
                }
                final = baseName;
                int k = 2;
                while (_phrases.Any(p => !ReferenceEquals(p, existing)
                        && string.Equals(p.Name, final, StringComparison.OrdinalIgnoreCase)))
                    final = $"{baseName} ({k++})";
                existing.Name = final;
            }
            // Raise OUTSIDE the lock (mirrors NfcTagRegistry): a handler must
            // not run while _lock is held.
            RegistryChanged?.Invoke(null, EventArgs.Empty);
            return final;
        }

        public static void Remove(string phrase)
        {
            string norm = NormalizePhrase(phrase);
            bool removed;
            lock (_lock) removed = _phrases.RemoveAll(p => string.Equals(p.Phrase, norm, StringComparison.Ordinal)) > 0;
            if (removed) RegistryChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Replaces the registry from persisted settings (load time).
        /// The stored button is honored so existing macro bindings stay valid
        /// across restarts; an absent / out-of-range / colliding button is
        /// reassigned the lowest free one, mirroring the NFC loader.</summary>
        public static void LoadRegistry(IEnumerable<(string Phrase, string Name, int Button)> entries)
        {
            lock (_lock)
            {
                _phrases.Clear();
                if (entries != null)
                    foreach (var (phrase, name, button) in entries)
                    {
                        string norm = NormalizePhrase(phrase);
                        if (norm.Length == 0 || string.IsNullOrWhiteSpace(name)) continue;
                        if (_phrases.Any(p => string.Equals(p.Phrase, norm, StringComparison.Ordinal))) continue;
                        int b = button;
                        if (b < 1 || b > MaxButton || _phrases.Any(p => p.Button == b))
                        {
                            b = LowestFreeButton();
                            if (b < 0) break; // full
                        }
                        _phrases.Add(new PhraseEntry { Phrase = norm, Name = name.Trim(), Button = b });
                    }
            }
            RegistryChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Serializable view for the settings round-trip.</summary>
        public static List<(string Phrase, string Name, int Button)> SaveRegistry()
        {
            lock (_lock) return _phrases.Select(p => (p.Phrase, p.Name, p.Button)).ToList();
        }
    }
}
