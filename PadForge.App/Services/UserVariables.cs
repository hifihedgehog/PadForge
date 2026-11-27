using System.Collections.Generic;
using PadForge.Common;

namespace PadForge.Services
{
    /// <summary>
    /// The variables of a process PadForge starts as the user without rights
    /// (#600): every process <see cref="Plugins.PluginLauncher"/> starts (a
    /// plugin, the install and check copy, a recording's
    /// save and the web helper), and the failed start's copy. They
    /// are the variables Windows builds for that user's own token from the
    /// user's profile and the machine's
    /// (<see cref="System32Calls.VariablesOfProcess"/>), so the process gets
    /// the user's own Path entries and every other variable the user set, as
    /// any program the user starts does. None comes from PadForge's own
    /// process, whose variables after a start with rights are the allow list
    /// <see cref="RightsVariables"/> builds.
    ///
    /// <para><c>__COMPAT_LAYER</c> is dropped: a compatibility layer that asks
    /// for administrator rights would raise the start. The variables a caller
    /// gives replace any of the same name.</para>
    ///
    /// <para>The failed start's copy is started from the protected start,
    /// before the process may trust the folder its libraries were unpacked
    /// into, so this class calls no DllImport and compares names by their
    /// characters alone, reading no culture.</para>
    /// </summary>
    internal static class UserVariables
    {
        /// <summary>The one variable never handed on.</summary>
        internal const string CompatLayer = "__COMPAT_LAYER";

        /// <summary>
        /// The lines a process started as the user gets: each of
        /// <paramref name="user"/> but <see cref="CompatLayer"/> and the drive
        /// lines Windows keeps (<c>=C:=C:\</c>), then each of
        /// <paramref name="added"/> in place of any of the same name. A name
        /// that is empty or holds '=' or a null character is left out, and
        /// null characters are taken out of values, so no line can split the
        /// block. Each name is kept once, the first line of a name in
        /// <paramref name="user"/>, and the lines are sorted by name, upper
        /// and lower case alike, as Windows keeps a block (Microsoft,
        /// CreateProcessW).
        /// </summary>
        internal static List<string> Build(IReadOnlyList<string> user, IEnumerable<KeyValuePair<string, string>> added)
        {
            var kept = new List<string>();
            if (user != null)
                foreach (string line in user)
                {
                    if (string.IsNullOrEmpty(line) || line[0] == '=' || line.IndexOf('=') < 0) continue;
                    string name = ProtectedStart.NameOf(line);
                    if (SameName(name, CompatLayer) || IndexOf(kept, name) >= 0) continue;
                    kept.Add(name + "=" + Clean(line.Substring(name.Length + 1)));
                }
            if (added != null)
                foreach (var pair in added)
                {
                    if (!IsName(pair.Key) || SameName(pair.Key, CompatLayer)) continue;
                    string line = pair.Key + "=" + Clean(pair.Value);
                    int at = IndexOf(kept, pair.Key);
                    if (at >= 0) kept[at] = line;
                    else kept.Add(line);
                }
            kept.Sort((a, b) => ProtectedStart.CompareNames(ProtectedStart.NameOf(a), ProtectedStart.NameOf(b)));
            return kept;
        }

        /// <summary>The block CreateProcess takes for those lines (each and a
        /// null character, one more at the end, two for no lines), or null
        /// when <paramref name="user"/> is null: a start whose user's
        /// variables could not be read does not go ahead.</summary>
        internal static string Block(IReadOnlyList<string> user, IEnumerable<KeyValuePair<string, string>> added)
        {
            if (user == null) return null;
            var lines = Build(user, added);
            return lines.Count == 0 ? "\0\0" : RightsVariables.ToBlock(lines);
        }

        private static bool IsName(string name)
            => !string.IsNullOrEmpty(name) && name.IndexOf('=') < 0 && name.IndexOf('\0') < 0;

        private static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.IndexOf('\0') < 0) return value;
            var kept = new char[value.Length];
            int n = 0;
            foreach (char c in value)
                if (c != '\0') kept[n++] = c;
            return new string(kept, 0, n);
        }

        private static int IndexOf(List<string> lines, string name)
        {
            for (int i = 0; i < lines.Count; i++)
                if (SameName(ProtectedStart.NameOf(lines[i]), name)) return i;
            return -1;
        }

        private static bool SameName(string a, string b) => ProtectedStart.CompareNames(a, b) == 0;
    }
}
