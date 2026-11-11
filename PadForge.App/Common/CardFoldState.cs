using System;
using System.Collections.Generic;
using System.Linq;

namespace PadForge.Common
{
    /// <summary>
    /// Which cards the user has opened further (#574). A card opens on the
    /// settings most people use and keeps the rest behind a More row, and a
    /// card few people use folds to one line. Each such part names itself
    /// with a key, and this set holds the keys that are open. It saves with
    /// the app's own settings, apart from any profile, so a card opened once
    /// is open at the next launch and after a profile switch.
    ///
    /// <para><see cref="Changed"/> fires on a real change from
    /// <see cref="Set"/> and never from <see cref="Load"/>, so reading the
    /// settings file does not mark it dirty. <see cref="Replaced"/> fires
    /// after <see cref="Load"/>, and the parts on screen read their keys
    /// again: a reset to defaults folds every card back.</para>
    /// </summary>
    public static class CardFoldState
    {
        private static readonly object Gate = new();
        private static readonly HashSet<string> OpenKeys = new(StringComparer.Ordinal);
        private static readonly WeakHandlers<EventHandler> ReplacedHandlers = new();

        /// <summary>Raised after <see cref="Set"/> changed the set. The
        /// settings need saving.</summary>
        public static event EventHandler Changed;

        /// <summary>Raised after <see cref="Load"/> replaced the set. A
        /// subscriber is held weakly, so a part that left the screen is
        /// collected as before.</summary>
        public static event EventHandler Replaced
        {
            add => ReplacedHandlers.Add(value);
            remove => ReplacedHandlers.Remove(value);
        }

        /// <summary>Whether the part with this key is open. A part with no
        /// key is never stored and reads closed.</summary>
        public static bool IsOpen(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (Gate) return OpenKeys.Contains(key);
        }

        /// <summary>Records that the part with this key was opened or
        /// closed.</summary>
        public static void Set(string key, bool open)
        {
            if (string.IsNullOrEmpty(key)) return;
            bool changed;
            lock (Gate) changed = open ? OpenKeys.Add(key) : OpenKeys.Remove(key);
            if (changed) Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>The open keys in ordinal order, so the settings file
        /// lists them the same way from one save to the next.</summary>
        public static List<string> Snapshot()
        {
            lock (Gate) return OpenKeys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        }

        /// <summary>Replaces the set with what a settings file holds. A
        /// null list is an empty one, as in a file from before 6.0.</summary>
        public static void Load(IEnumerable<string> keys)
        {
            lock (Gate)
            {
                OpenKeys.Clear();
                if (keys != null)
                    foreach (string key in keys)
                        if (!string.IsNullOrEmpty(key))
                            OpenKeys.Add(key);
            }
            foreach (EventHandler handler in ReplacedHandlers.Live())
                handler(null, EventArgs.Empty);
        }
    }
}
