using System;
using System.Collections.Generic;

namespace PadForge.Common.Input
{
    /// <summary>Where the mapping SDL reads a device through came from
    /// (#600 item 45). Append-only.</summary>
    internal enum SdlMappingSource
    {
        /// <summary>No mapping: SDL reads the device as a joystick.</summary>
        None,
        /// <summary>SDL itself: its own table, XInput, or one of its
        /// drivers.</summary>
        Sdl,
        /// <summary>The community copy built into this release's SDL.</summary>
        Community,
        /// <summary>A row Update Now downloaded.</summary>
        Downloaded,
        /// <summary>A row of PadForge's own file.</summary>
        PadForge,
        /// <summary>The user's own row.</summary>
        Yours,
        /// <summary>A row of the SDL_GAMECONTROLLERCONFIG variable, which SDL
        /// holds above every row a program adds.</summary>
        Variable,
        /// <summary>A device SDL does not read, which PadForge maps through
        /// the user's row.</summary>
        YoursPadForge,
    }

    /// <summary>
    /// Says where a device's mapping came from (#600 item 45), from what SDL
    /// answered and what PadForge gave it, as plain logic. SDL holds one
    /// mapping per ID (SDL_PrivateAddMappingForGUID), so the source is the
    /// one whose bindings SDL holds now, tried from the strongest down: the
    /// variable, the user's row, a downloaded row, PadForge's own, then the
    /// release's community copy by the baseline's verdict, and SDL for any
    /// other mapping.
    /// </summary>
    internal static class ControllerDatabaseSource
    {
        /// <summary>What a mapping's text says, read from its third field
        /// on: the layout <see cref="SdlMappingRow.Layout"/> gives, or null
        /// for text that is no row.</summary>
        internal static string LayoutOf(string mapping)
        {
            if (string.IsNullOrEmpty(mapping)) return null;
            // SDL's own text can carry a field the user grammar refuses (a
            // hint of another kind, an SDK bound): only the bindings and the
            // type and face fields are compared.
            string[] parts = mapping.Split(',');
            if (parts.Length < 3) return null;
            var kept = new List<string>(parts.Length);
            kept.Add(parts[0]);
            kept.Add("x");
            for (int i = 2; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p.StartsWith("hint:", StringComparison.Ordinal) || p.StartsWith("sdk", StringComparison.Ordinal)
                    || p.StartsWith("platform:", StringComparison.Ordinal))
                    continue;
                kept.Add(p);
            }
            string guid = parts[0].Length == 32 ? parts[0] : "00000000000000000000000000000001";
            kept[0] = guid;
            return SdlMappingRow.TryParse(string.Join(",", kept), out var row, out _) ? row.Layout : null;
        }

        /// <summary>
        /// The source of the mapping SDL reads a device through.
        /// </summary>
        /// <param name="deviceGuid">The device's ID as SDL reports it.</param>
        /// <param name="mapping">The mapping SDL holds for the device
        /// (SDL_GetGamepadMappingForID), or null when it reads the device as
        /// a joystick.</param>
        /// <param name="variable">The SDL_GAMECONTROLLERCONFIG variable's
        /// text, or null.</param>
        internal static SdlMappingSource Of(string deviceGuid, string mapping, ControllerDatabaseMine mine,
            ControllerDatabaseSection section, IReadOnlyList<string> ownRows, ControllerDatabaseBaseline baseline, string variable)
        {
            if (string.IsNullOrEmpty(mapping)) return SdlMappingSource.None;
            string layout = LayoutOf(mapping);
            string id = ControllerDatabaseRow.LookupId(deviceGuid);

            if (!string.IsNullOrEmpty(variable))
                foreach (string line in variable.Split('\n'))
                {
                    int comma = line.IndexOf(',');
                    if (comma == 32 && SdlMappingRow.Maps(line.Substring(0, 32), deviceGuid) && Same(LayoutOf(line), layout))
                        return SdlMappingSource.Variable;
                }

            var yours = mine?.ForSdlDevice(deviceGuid);
            if (yours != null && Same(yours.Row.Layout, layout)) return SdlMappingSource.Yours;

            if (section != null)
                foreach (var stored in section.Rows)
                    if (string.Equals(stored.Row.Guid, id, StringComparison.Ordinal)
                        && Same(LayoutOf(stored.Row.Text), layout))
                        return SdlMappingSource.Downloaded;

            foreach (string own in ownRows ?? Array.Empty<string>())
            {
                int comma = own.IndexOf(',');
                if (comma == 32 && SdlMappingRow.Maps(own.Substring(0, 32), deviceGuid) && Same(LayoutOf(own), layout))
                    return SdlMappingSource.PadForge;
            }

            if (baseline != null && baseline.TryFind(id, out var entry) && entry.Verdict == ControllerDatabaseVerdict.Kept)
                return SdlMappingSource.Community;
            return SdlMappingSource.Sdl;
        }

        private static bool Same(string a, string b) => a != null && string.Equals(a, b, StringComparison.Ordinal);

        /// <summary>The source of a row of every mapping PadForge carries,
        /// by its ID, for Find a Mapping (#600 item 48).</summary>
        internal static SdlMappingSource OfRow(string rowGuid, ControllerDatabaseMine mine, ControllerDatabaseSection section,
            IReadOnlyList<string> ownRows, ControllerDatabaseBaseline baseline)
        {
            if (mine != null)
                foreach (var row in mine.Rows)
                    if (string.Equals(row.Id, rowGuid, StringComparison.OrdinalIgnoreCase)) return SdlMappingSource.Yours;
            string id = ControllerDatabaseRow.LookupId(rowGuid);
            if (section != null)
                foreach (var stored in section.Rows)
                    if (string.Equals(stored.Row.Guid, id, StringComparison.Ordinal)) return SdlMappingSource.Downloaded;
            foreach (string own in ownRows ?? Array.Empty<string>())
                if (own.Length > 32 && string.Equals(ControllerDatabaseRow.LookupId(own.Substring(0, 32)), id, StringComparison.Ordinal))
                    return SdlMappingSource.PadForge;
            if (baseline != null && baseline.TryFind(id, out var entry) && entry.Verdict == ControllerDatabaseVerdict.Kept)
                return SdlMappingSource.Community;
            return SdlMappingSource.Sdl;
        }
    }
}
