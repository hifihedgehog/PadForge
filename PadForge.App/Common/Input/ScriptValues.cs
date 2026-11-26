using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Named numbers programs hand to Custom Expression macro triggers (#597),
    /// set by a <c>var name=value</c> line on the Automation pipe. A Custom
    /// Expression variable whose source is a script value reads one by name,
    /// as a telemetry variable reads its channel (GameTelemetryRuntime.Read).
    ///
    /// <para>A value reads 0 until a program sets it, and again once the
    /// program that set it last is gone or Automation's level drops below
    /// Send Inputs. Names are matched without regard to case. The macro pass
    /// reads with no lock: the table is a concurrent dictionary, whose reads
    /// take none.</para>
    /// </summary>
    internal static class ScriptValues
    {
        /// <summary>The longest name.</summary>
        internal const int LongestName = 32;

        /// <summary>How many names PadForge keeps at once. A name a program
        /// set is kept until the last session that set it ends, and then
        /// its place is free again.</summary>
        internal const int MostNames = 256;

        private readonly struct Held
        {
            public Held(float value, int owner)
            {
                Value = value;
                Owner = owner;
            }

            public readonly float Value;
            public readonly int Owner;
        }

        // Writes take this lock, so a name, the sessions that set it and
        // AnySet change as one. The macro pass reads Values without it.
        private static readonly object Sync = new object();
        private static readonly ConcurrentDictionary<string, Held> Values = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, HashSet<int>> Known = new(StringComparer.OrdinalIgnoreCase);
        private static volatile bool _anySet;

        /// <summary>Raised when a name is set while no session holds it, on
        /// the thread that set it. The expression editor offers it from
        /// then on.</summary>
        internal static event Action<string> NameAdded;

        /// <summary>Raised when the last session that set a name ended, on
        /// the thread that ended it. The expression editor offers it no
        /// more.</summary>
        internal static event Action<string> NameRemoved;

        /// <summary>Whether a program has a value set now. Keyboard and
        /// mouse output to windows with rights waits while one does
        /// (ProgramInputGate).</summary>
        internal static bool AnySet => _anySet;

        /// <summary>Whether a name can be used: a letter, then letters,
        /// digits, <c>_</c>, <c>.</c> or <c>-</c>, up to 32. It never holds
        /// the <c>:</c> or <c>|</c> a saved variable is cut at.</summary>
        internal static bool IsName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > LongestName) return false;
            if (!char.IsAsciiLetter(name[0])) return false;
            foreach (char c in name)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')) return false;
            return true;
        }

        /// <summary>The value a macro reads now. Called on the poll
        /// thread.</summary>
        internal static float Read(string name)
            => !string.IsNullOrEmpty(name) && Values.TryGetValue(name, out var held) ? held.Value : 0f;

        /// <summary>The session that set a name's value last, 0 while no
        /// program's value is set. Called on the poll thread.</summary>
        internal static int SetterOf(string name)
            => !string.IsNullOrEmpty(name) && Values.TryGetValue(name, out var held) ? held.Owner : 0;

        /// <summary>Sets a value for program <paramref name="owner"/>. False
        /// for a name that cannot be used, or a new name while
        /// <see cref="MostNames"/> are held.</summary>
        internal static bool Set(string name, float value, int owner)
        {
            if (!IsName(name) || !float.IsFinite(value)) return false;
            bool added = false;
            lock (Sync)
            {
                if (!Known.TryGetValue(name, out var setters))
                {
                    if (Known.Count >= MostNames) return false;
                    Known[name] = setters = new HashSet<int>();
                    added = true;
                }
                setters.Add(owner);
                Values[name] = new Held(value, owner);
                _anySet = true;
            }
            if (added) NameAdded?.Invoke(name);
            return true;
        }

        /// <summary>Program <paramref name="owner"/> ended: every value it
        /// set last reads 0 again, and every name no session that is still
        /// open has set goes, freeing its place.</summary>
        internal static void ReleaseOwner(int owner)
        {
            List<string> gone = null;
            lock (Sync)
            {
                foreach (var pair in Values)
                    if (pair.Value.Owner == owner)
                        ((ICollection<KeyValuePair<string, Held>>)Values).Remove(pair);
                foreach (var pair in Known)
                {
                    if (!pair.Value.Remove(owner) || pair.Value.Count > 0) continue;
                    (gone ??= new List<string>()).Add(pair.Key);
                }
                if (gone != null)
                    foreach (string name in gone)
                    {
                        Known.Remove(name);
                        Values.TryRemove(name, out _);
                    }
                _anySet = !Values.IsEmpty;
            }
            if (gone != null && NameRemoved is { } removed)
                foreach (string name in gone) removed(name);
        }

        /// <summary>Lets go of every value, as a level below Send Inputs
        /// does. The names stay with the sessions that set them.</summary>
        internal static void ReleaseAll()
        {
            lock (Sync)
            {
                Values.Clear();
                _anySet = false;
            }
        }

        /// <summary>The names held now, in no order.</summary>
        internal static IReadOnlyList<string> Names()
        {
            lock (Sync) return new List<string>(Known.Keys);
        }

        /// <summary>Test seam: nothing set and no name known.</summary>
        internal static void ResetForTest()
        {
            lock (Sync)
            {
                Values.Clear();
                Known.Clear();
                _anySet = false;
            }
        }
    }
}
