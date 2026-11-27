using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PadForge.Services
{
    /// <summary>
    /// The variables of a process <see cref="Plugins.PluginLauncher"/> starts
    /// (#600): a plugin, the install and check copy and the web helper. The
    /// block is built from the lines it is given, PadForge's own variables
    /// for the launcher's starts.
    ///
    /// <para><c>__COMPAT_LAYER</c> is dropped: a compatibility layer that asks
    /// for administrator rights would raise the start. The variables a caller
    /// gives replace any of the same name.</para>
    /// </summary>
    internal static class UserVariables
    {
        /// <summary>The one variable never handed on.</summary>
        internal const string CompatLayer = "__COMPAT_LAYER";

        /// <summary>
        /// The user's variables as they stand now, one <c>name=value</c> line
        /// each: PadForge's own, with each value of the user's
        /// HKEY_CURRENT_USER\Environment, expanded, in place of the one of the
        /// same name. The user's Path follows the machine's, as Windows joins
        /// them for a new sign-in.
        /// </summary>
        internal static string[] OfTheUser()
        {
            var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                if (entry.Key is string name && name.Length > 0) map[name] = entry.Value as string ?? string.Empty;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey("Environment");
                if (key != null)
                    foreach (string name in key.GetValueNames())
                    {
                        if (string.IsNullOrEmpty(name) || key.GetValue(name) is not string value) continue;
                        if (string.Equals(name, "Path", StringComparison.OrdinalIgnoreCase))
                        {
                            string machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
                            value = string.IsNullOrEmpty(machine) ? value : machine.TrimEnd(';') + ";" + value;
                        }
                        map[name] = value;
                    }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
            {
                // PadForge's own variables stand.
            }
            var lines = new string[map.Count];
            int i = 0;
            foreach (var pair in map) lines[i++] = pair.Key + "=" + pair.Value;
            return lines;
        }

        /// <summary>
        /// The lines a started process gets: each of <paramref name="user"/>
        /// but <see cref="CompatLayer"/> and the drive lines Windows keeps
        /// (<c>=C:=C:\</c>), then each of <paramref name="added"/> in place of
        /// any of the same name. A name that is empty or holds '=' or a null
        /// character is left out, and null characters are taken out of
        /// values, so no line can split the block. Each name is kept once,
        /// the first line of a name in <paramref name="user"/>, and the lines
        /// are sorted by name, upper and lower case alike, as Windows keeps a
        /// block (Microsoft, CreateProcessW).
        /// </summary>
        internal static List<string> Build(IReadOnlyList<string> user, IEnumerable<KeyValuePair<string, string>> added)
        {
            var kept = new List<string>();
            if (user != null)
                foreach (string line in user)
                {
                    if (string.IsNullOrEmpty(line) || line[0] == '=' || line.IndexOf('=') < 0) continue;
                    string name = NameOf(line);
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
            kept.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(NameOf(a), NameOf(b)));
            return kept;
        }

        /// <summary>The block CreateProcess takes for those lines (each and a
        /// null character, one more at the end, two for no lines), or null
        /// when <paramref name="user"/> is null.</summary>
        internal static string Block(IReadOnlyList<string> user, IEnumerable<KeyValuePair<string, string>> added)
        {
            if (user == null) return null;
            var lines = Build(user, added);
            if (lines.Count == 0) return "\0\0";
            var block = new System.Text.StringBuilder();
            foreach (string line in lines) block.Append(line).Append('\0');
            return block.Append('\0').ToString();
        }

        private static string NameOf(string line)
        {
            int equals = line.IndexOf('=', line.StartsWith('=') ? 1 : 0);
            return equals < 0 ? line : line.Substring(0, equals);
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
                if (SameName(NameOf(lines[i]), name)) return i;
            return -1;
        }

        private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
