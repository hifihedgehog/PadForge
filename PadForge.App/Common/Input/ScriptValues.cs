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

        /// <summary>How many names one PadForge run keeps.</summary>
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

        private static readonly ConcurrentDictionary<string, Held> Values = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised when a name is set for the first time this run,
        /// on the thread that set it. The expression editor offers it from
        /// then on.</summary>
        internal static event Action<string> NameAdded;

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

        /// <summary>Sets a value for program <paramref name="owner"/>. False
        /// for a name that cannot be used, or a new name past
        /// <see cref="MostNames"/>.</summary>
        internal static bool Set(string name, float value, int owner)
        {
            if (!IsName(name) || !float.IsFinite(value)) return false;
            bool added = false;
            if (!Known.ContainsKey(name))
            {
                if (Known.Count >= MostNames) return false;
                added = Known.TryAdd(name, name);
            }
            Values[name] = new Held(value, owner);
            if (added) NameAdded?.Invoke(name);
            return true;
        }

        /// <summary>Lets go of every value program <paramref name="owner"/>
        /// set last. They read 0 again.</summary>
        internal static void ReleaseOwner(int owner)
        {
            foreach (var pair in Values)
                if (pair.Value.Owner == owner)
                    ((ICollection<KeyValuePair<string, Held>>)Values).Remove(pair);
        }

        /// <summary>Lets go of every value.</summary>
        internal static void ReleaseAll() => Values.Clear();

        /// <summary>The names set this run, in no order.</summary>
        internal static IReadOnlyList<string> Names() => new List<string>(Known.Values);

        /// <summary>Test seam: nothing set and no name known.</summary>
        internal static void ResetForTest()
        {
            Values.Clear();
            Known.Clear();
        }
    }
}
