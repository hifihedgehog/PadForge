using System;
using System.Collections.Generic;
using PadForge.Common.Input;
using PadForge.Services.Plugins;

namespace PadForge.Services
{
    /// <summary>
    /// The rows in the user's own SDL_GAMECONTROLLERCONFIG variable (#600
    /// item 49), offered to Add Rows. SDL2 Gamepad Tool's Set Mapping As
    /// Environment Variable writes it to the user's profile
    /// (HKEY_CURRENT_USER\Environment), and other SDL programs take rows
    /// from it.
    ///
    /// <para>SDL in PadForge's process reads the variable PadForge started
    /// with. The text here is the value Windows builds for the desktop
    /// shell's token from the
    /// signed-in user's profile (<see cref="UserVariables.OfTheUser"/>,
    /// CreateEnvironmentBlock): the person at the desktop, even when PadForge
    /// was started with another account's password. With no shell, nothing
    /// is read. Each row passes the paste's own check
    /// (<see cref="SdlMappingRow.ReadAll"/>) and is kept only when the person
    /// presses Add.</para>
    /// </summary>
    internal static class ControllerDatabaseProfileRows
    {
        internal const string Variable = "SDL_GAMECONTROLLERCONFIG";

        /// <summary>The variable's text in the signed-in user's profile, or
        /// null.</summary>
        internal static string Read()
        {
            try
            {
                return ValueOf(UserVariables.OfTheUser(), Variable);
            }
            catch (Exception)
            {
                // A shell that went away between the two calls, or a token
                // Windows would not read: no rows to offer.
                return null;
            }
        }

        /// <summary>The value of <paramref name="name"/> among
        /// <c>name=value</c> lines, the name in any case, or null.</summary>
        internal static string ValueOf(IReadOnlyList<string> lines, string name)
        {
            if (lines == null) return null;
            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line) || line.Length <= name.Length || line[name.Length] != '=') continue;
                if (string.Compare(line, 0, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    return line.Substring(name.Length + 1);
            }
            return null;
        }

        /// <summary>The rows of <paramref name="text"/> that pass the paste's
        /// check and that the user does not keep as written. Of rows that
        /// share an ID the last is the one, as Add Rows keeps it.</summary>
        /// <summary>The variable's value in PadForge's own process, which SDL
        /// read at start (SDL_LoadGamepadHints). A test gives its own.</summary>
        internal static Func<string> Applied = () => Environment.GetEnvironmentVariable(Variable);

        /// <summary>The rows of <paramref name="text"/> that PadForge does not
        /// keep (<see cref="NotKept"/>), less each row for a controller that
        /// <paramref name="applied"/> maps. SDL loaded those rows at start at
        /// USER priority, and a row PadForge adds is API priority, which never
        /// replaces a USER row (SDL_gamepad.c, SDL_LoadGamepadHints and
        /// SDL_PrivateAddMappingForGUID).</summary>
        internal static List<SdlMappingRow> NotApplied(string text, ControllerDatabaseMine mine, string applied)
        {
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(applied))
                foreach (var row in SdlMappingRow.ReadAll(applied, out _, out _, out _)) covered.Add(row.Guid);
            var found = new List<SdlMappingRow>();
            foreach (var row in NotKept(text, mine))
                if (!covered.Contains(row.Guid)) found.Add(row);
            return found;
        }

        internal static List<SdlMappingRow> NotKept(string text, ControllerDatabaseMine mine)
        {
            var found = new List<SdlMappingRow>();
            if (string.IsNullOrWhiteSpace(text)) return found;
            var last = new Dictionary<string, SdlMappingRow>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var row in SdlMappingRow.ReadAll(text, out _, out _, out _))
            {
                if (!last.ContainsKey(row.Guid)) order.Add(row.Guid);
                last[row.Guid] = row;
            }
            foreach (string id in order)
            {
                var row = last[id];
                bool kept = false;
                foreach (var m in mine?.Rows ?? Array.Empty<ControllerDatabaseMineRow>())
                    if (!m.ForPadForgeDevice && string.Equals(m.Id, row.Guid, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(m.Text, row.Text, StringComparison.Ordinal))
                        kept = true;
                if (!kept) found.Add(row);
            }
            return found;
        }
    }
}
