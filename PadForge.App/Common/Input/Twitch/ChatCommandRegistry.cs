using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PadForge.Common.Input.Twitch
{
    /// <summary>
    /// The chat commands the Twitch Chat row offers as buttons (issue #555,
    /// discussion #544). Built like <see cref="VoicePhraseRegistry"/>: a
    /// static list, a stable button per word that never changes, so removing
    /// a word leaves a gap rather than repointing every mapping, a
    /// <see cref="RegistryChanged"/> event raised outside the lock, and a
    /// Load/Save pair SettingsService round-trips through PadForge.xml.
    /// Button 0 is "Any Command", held while a command plays. A new word takes
    /// the lowest free button, so <see cref="ButtonFreed"/> has everything
    /// mapped to a removed word's button cleared first.
    ///
    /// <para>The usual Twitch Plays words come preset
    /// (<see cref="UsualWords"/>). The default mapping places each on the
    /// gamepad position it names (<see cref="ChatGamepadPlacement"/>), and
    /// the streamer can map any word to anything.</para>
    /// </summary>
    public static class ChatCommandRegistry
    {
        public sealed class CommandEntry
        {
            /// <summary>The normalized word, without the "!".</summary>
            public string Word { get; set; }
            /// <summary>The STABLE 1-based button this word occupies.</summary>
            public int Button { get; set; }
        }

        /// <summary>How many words the row carries, past Any Command.</summary>
        public const int MaxWords = 128;

        /// <summary>The longest word a streamer can add.</summary>
        public const int MaxWordLength = 20;

        /// <summary>The usual Twitch Plays words, registered on first use in
        /// this order. Home and the touchpad stay out, so a viewer cannot open
        /// a system overlay. The streamer can add them.</summary>
        public static readonly IReadOnlyList<string> UsualWords = new[]
        {
            "a", "b", "x", "y", "lb", "rb", "lt", "rt", "select", "start", "ls", "rs",
            "up", "down", "left", "right",
        };

        /// <summary>The words taking turns uses (ForgePlay's CommandHandler),
        /// which no command can take.</summary>
        public static readonly IReadOnlyList<string> ReservedWords = new[]
        {
            "claim", "accept", "release", "skip", "skipall",
        };

        // A word ending in a number and a unit would read as a hold time in
        // the command language ("jump2s" is "jump" for 2 s), so no word ends
        // that way. ChatCommandParser reads times with the same pattern.
        private static readonly Regex s_endsInTime = new(@"\d+(?:\.\d+)?(?:ms|s)$", RegexOptions.CultureInvariant);

        private static readonly object _lock = new();
        private static readonly List<CommandEntry> _words = new();
        private static int _version;

        /// <summary>Raised when the list changes, outside the lock.</summary>
        public static event EventHandler RegistryChanged;

        /// <summary>Raised with a removed word's button, outside the lock and
        /// before <see cref="RegistryChanged"/>.</summary>
        public static event Action<int> ButtonFreed;

        /// <summary>Bumps on every change, so a reader can tell its copy is
        /// stale without comparing lists.</summary>
        public static int Version { get { lock (_lock) return _version; } }

        /// <summary>The words ordered by their stable button, each copied so a
        /// caller cannot change the list.</summary>
        public static IReadOnlyList<CommandEntry> Words
        {
            get
            {
                lock (_lock)
                    return _words.OrderBy(w => w.Button)
                                 .Select(w => new CommandEntry { Word = w.Word, Button = w.Button })
                                 .ToList();
            }
        }

        public static int Count { get { lock (_lock) return _words.Count; } }

        /// <summary>The highest button in use, 0 when the list is empty, so the
        /// row reports a button span that covers every word.</summary>
        public static int MaxButtonInUse { get { lock (_lock) return _words.Count == 0 ? 0 : _words.Max(w => w.Button); } }

        /// <summary>A word as stored: trimmed, without a leading "!",
        /// lower-cased with the invariant culture.</summary>
        public static string Normalize(string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return string.Empty;
            string trimmed = word.Trim();
            if (trimmed.StartsWith('!')) trimmed = trimmed.Substring(1).Trim();
            return trimmed.ToLowerInvariant();
        }

        /// <summary>Why a word cannot be added, or <see cref="WordProblem.None"/>.
        /// A word is 1 to <see cref="MaxWordLength"/> letters and digits,
        /// starts with a letter, does not end in a time, and is not
        /// reserved.</summary>
        public static WordProblem Check(string word)
        {
            string norm = Normalize(word);
            if (norm.Length == 0) return WordProblem.Empty;
            if (norm.Length > MaxWordLength) return WordProblem.TooLong;
            if (!char.IsLetter(norm[0])) return WordProblem.Characters;
            foreach (char c in norm)
                if (!char.IsLetterOrDigit(c)) return WordProblem.Characters;
            if (s_endsInTime.IsMatch(norm)) return WordProblem.EndsInTime;
            if (ReservedWords.Contains(norm)) return WordProblem.Reserved;
            return WordProblem.None;
        }

        public enum WordProblem { None, Empty, TooLong, Characters, EndsInTime, Reserved }

        /// <summary>The STABLE button a word occupies, or -1 when the list
        /// does not have it.</summary>
        public static int ButtonFor(string word)
        {
            string norm = Normalize(word);
            if (norm.Length == 0) return -1;
            lock (_lock)
                return _words.FirstOrDefault(w => string.Equals(w.Word, norm, StringComparison.Ordinal))?.Button ?? -1;
        }

        // Lowest 1..MaxWords not taken, or -1 when full. Caller holds _lock.
        private static int LowestFreeButton()
        {
            for (int b = 1; b <= MaxWords; b++)
                if (!_words.Any(w => w.Button == b))
                    return b;
            return -1;
        }

        /// <summary>Adds a word at the lowest free button, which never changes
        /// afterward. A word already in the list keeps its button. Returns the
        /// button, or -1 when the word fails <see cref="Check"/> or the list is
        /// full.</summary>
        public static int Add(string word)
        {
            if (Check(word) != WordProblem.None) return -1;
            string norm = Normalize(word);
            int button;
            lock (_lock)
            {
                var existing = _words.FirstOrDefault(w => string.Equals(w.Word, norm, StringComparison.Ordinal));
                if (existing != null) return existing.Button;
                button = LowestFreeButton();
                if (button < 0) return -1;
                _words.Add(new CommandEntry { Word = norm, Button = button });
                _version++;
            }
            RegistryChanged?.Invoke(null, EventArgs.Empty);
            return button;
        }

        public static void Remove(string word)
        {
            string norm = Normalize(word);
            bool removed;
            int freed;
            lock (_lock)
            {
                freed = _words.FirstOrDefault(w => string.Equals(w.Word, norm, StringComparison.Ordinal))?.Button ?? -1;
                removed = _words.RemoveAll(w => string.Equals(w.Word, norm, StringComparison.Ordinal)) > 0;
                if (removed) _version++;
            }
            if (!removed) return;
            ButtonFreed?.Invoke(freed);
            RegistryChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Adds back every usual word the list is missing. Words
        /// already in it keep their buttons.</summary>
        public static void RestoreUsualWords()
        {
            bool changed = false;
            lock (_lock)
            {
                foreach (string word in UsualWords)
                {
                    if (_words.Any(w => string.Equals(w.Word, word, StringComparison.Ordinal))) continue;
                    int button = LowestFreeButton();
                    if (button < 0) break;
                    _words.Add(new CommandEntry { Word = word, Button = button });
                    changed = true;
                }
                if (changed) _version++;
            }
            if (changed) RegistryChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Replaces the list from saved settings. Null, meaning the
        /// settings have never held a list, registers the usual words. An empty
        /// list stays empty: the streamer removed them. A saved button is kept
        /// so mappings survive restarts, and a missing, out-of-range or taken
        /// one gets the lowest free button, as the voice phrase loader
        /// does.</summary>
        public static void LoadRegistry(IEnumerable<(string Word, int Button)> entries)
        {
            lock (_lock)
            {
                _words.Clear();
                if (entries == null)
                {
                    for (int i = 0; i < UsualWords.Count; i++)
                        _words.Add(new CommandEntry { Word = UsualWords[i], Button = i + 1 });
                }
                else
                {
                    foreach (var (word, button) in entries)
                    {
                        if (Check(word) != WordProblem.None) continue;
                        string norm = Normalize(word);
                        if (_words.Any(w => string.Equals(w.Word, norm, StringComparison.Ordinal))) continue;
                        int b = button;
                        if (b < 1 || b > MaxWords || _words.Any(w => w.Button == b))
                        {
                            b = LowestFreeButton();
                            if (b < 0) break;
                        }
                        _words.Add(new CommandEntry { Word = norm, Button = b });
                    }
                }
                _version++;
            }
            RegistryChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>The list for the settings round trip.</summary>
        public static List<(string Word, int Button)> SaveRegistry()
        {
            lock (_lock) return _words.Select(w => (w.Word, w.Button)).ToList();
        }
    }
}
