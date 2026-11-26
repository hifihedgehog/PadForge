using System;
using System.Collections.Generic;

namespace PadForge.Common.Input
{
    /// <summary>Where the mapping SDL reads a device through came from
    /// (#600 item 45). Held in memory only, for the card.</summary>
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
        /// <summary>A device SDL does not read, which PadForge maps through
        /// the user's row.</summary>
        YoursPadForge,
    }

    /// <summary>
    /// Says where a device's mapping came from (#600 item 45), from what SDL
    /// answered and what PadForge gave it, as plain logic. SDL holds one
    /// mapping per ID (SDL_PrivateAddMappingForGUID), so the source is the
    /// one whose bindings SDL holds now, tried from the strongest down: the
    /// user's row, a downloaded row, PadForge's own, then the
    /// release's community copy by the baseline's verdict, and SDL for any
    /// other mapping.
    /// </summary>
    internal static class ControllerDatabaseSource
    {
        /// <summary>What the text SDL hands back for a mapping it holds says,
        /// read from its third field on: the layout
        /// <see cref="SdlMappingRow.Layout"/> gives, or null for text that is
        /// no row. The text is read as it is. A row SDL was given is stored
        /// converted already, and a mapping SDL builds itself, a driver's or
        /// an automatic one, goes into its table unconverted
        /// (SDL_PrivateAddMappingForGUID's callers other than
        /// SDL_PrivateAddGamepadMapping). A row PadForge or the user gives SDL
        /// is read through <see cref="StoredLayoutOf"/>.</summary>
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
        internal static SdlMappingSource Of(string deviceGuid, string mapping, ControllerDatabaseMine mine,
            ControllerDatabaseSection section, IReadOnlyList<string> ownRows, ControllerDatabaseBaseline baseline)
        {
            if (string.IsNullOrEmpty(mapping)) return SdlMappingSource.None;
            string layout = LayoutOf(mapping);
            string id = ControllerDatabaseRow.LookupId(deviceGuid);

            var yours = mine?.ForSdlDevice(deviceGuid);
            if (yours != null && ReadsThrough(mapping, yours)) return SdlMappingSource.Yours;

            if (section != null)
                foreach (var stored in section.Rows)
                    if (string.Equals(stored.Row.Guid, id, StringComparison.Ordinal)
                        && Same(StoredLayoutOf(stored.Row.Text), layout))
                        return SdlMappingSource.Downloaded;

            foreach (string own in ownRows ?? Array.Empty<string>())
            {
                int comma = own.IndexOf(',');
                if (comma == 32 && SdlMappingRow.Maps(own.Substring(0, 32), deviceGuid) && Same(StoredLayoutOf(own), layout))
                    return SdlMappingSource.PadForge;
            }

            if (baseline != null && baseline.TryFind(id, out var entry) && entry.Verdict == ControllerDatabaseVerdict.Kept)
                return SdlMappingSource.Community;
            return SdlMappingSource.Sdl;
        }

        private static bool Same(string a, string b) => a != null && string.Equals(a, b, StringComparison.Ordinal);

        /// <summary>
        /// Whether SDL's text for the mapping it reads a device through
        /// (<paramref name="sdlMapping"/>) is a row of the user's: the same
        /// layout, once the paddle fields the fork appends for an XInput
        /// controller are set aside. For an XInput controller with paddles,
        /// SDL_GetGamepadMappingForID's text ends with "paddleN:bR," for each
        /// paddle the mapping neither names nor covers by binding its raw
        /// button: paddle1 on 12, paddle2 on 14, paddle3 on 13, paddle4 on 15
        /// (SDL_XInputPaddleMappingMask, SDL_AppendXInputPaddleBindings and
        /// CreateMappingStringForJoystick in SDL_gamepad.c).
        /// </summary>
        internal static bool ReadsThrough(string sdlMapping, ControllerDatabaseMineRow row)
        {
            string stored = StoredLayout(row);
            if (stored == null || string.IsNullOrEmpty(sdlMapping)) return false;
            if (Same(stored, LayoutOf(sdlMapping))) return true;
            string bare = WithoutAppendedPaddles(sdlMapping, row.Row);
            return !ReferenceEquals(bare, sdlMapping) && Same(stored, LayoutOf(bare));
        }

        /// <summary>The fork's XInput paddles (xinput_paddles in
        /// SDL_gamepad.c): each field and the raw button it is appended on.</summary>
        private static readonly (string Field, string Raw)[] XInputPaddles =
            { ("paddle1", "b12"), ("paddle2", "b14"), ("paddle3", "b13"), ("paddle4", "b15") };

        /// <summary>SDL's text without the paddle fields the fork appends for
        /// <paramref name="row"/>, or the text itself when it holds none.</summary>
        private static string WithoutAppendedPaddles(string mapping, SdlMappingRow row)
        {
            string[] parts = mapping.Split(',');
            var kept = new List<string>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
                if (i < 2 || !IsAppendedPaddle(parts[i], row)) kept.Add(parts[i]);
            return kept.Count == parts.Length ? mapping : string.Join(",", kept);
        }

        private static bool IsAppendedPaddle(string part, SdlMappingRow row)
        {
            foreach (var (field, raw) in XInputPaddles)
            {
                if (!string.Equals(part, field + ":" + raw, StringComparison.Ordinal)) continue;
                // A field the row names, or a raw button it binds, gets none.
                if (row.InputOf(field) != null) return false;
                foreach (var b in row.Bindings)
                    if (string.Equals(b.Input.TrimStart('+', '-'), raw, StringComparison.Ordinal)) return false;
                return true;
            }
            return false;
        }

        /// <summary>The layout of a row of the user's as SDL stores it,
        /// to hold against SDL's own text for the mapping it reads a device
        /// through.</summary>
        internal static string StoredLayout(ControllerDatabaseMineRow row) => row == null ? null : StoredLayoutOf(row.Text);

        /// <summary>The layout of a row SDL is given, as SDL stores it
        /// (<see cref="AsSdlStores"/>), to hold against SDL's own text, which
        /// <see cref="LayoutOf"/> reads.</summary>
        internal static string StoredLayoutOf(string row) => LayoutOf(AsSdlStores(row));

        /// <summary>The controllers SDL gives the GameCube label hint
        /// (initial_gamecube_devices in SDL_joystick.c), as vendor and
        /// product.</summary>
        private static readonly (ushort Vendor, ushort Product)[] GameCube =
        {
            (0x0079, 0x1843), (0x0079, 0x1844), (0x0079, 0x1846), (0x057e, 0x0337), (0x057e, 0x2073), (0x05e3, 0x0681),
            (0x0926, 0x8888), (0x0e6f, 0x0185), (0x1a34, 0xf705), (0x20d6, 0xa711),
        };

        private const string Hint = "hint:";
        private const string GameCubeLabels = "SDL_GAMECONTROLLER_USE_GAMECUBE_LABELS";
        private const string ButtonLabels = "SDL_GAMECONTROLLER_USE_BUTTON_LABELS";

        /// <summary>
        /// A row's text as SDL stores it once it is added
        /// (SDL_PrivateAddGamepadMapping). A row for a controller on SDL's
        /// GameCube list that names no GameCube hint gets it, appended as SDL
        /// appends it. Then the first hint field decides: the GameCube label
        /// hint swaps the first b and x fields, the button-label hint swaps
        /// the first a and b fields and the first x and y fields, and either
        /// is marked "!" (SDL_ConvertMappingToPositionalAXBY,
        /// SDL_ConvertMappingToPositionalBAXY). A hint already marked "!",
        /// another hint, or none leaves the row as it is. Only a row SDL is
        /// given passes here: SDL's own text for a mapping it holds is the
        /// stored form already, or a mapping SDL built without the conversion.
        /// </summary>
        internal static string AsSdlStores(string mapping)
        {
            if (string.IsNullOrEmpty(mapping)) return mapping;
            int comma = mapping.IndexOf(',');
            if (comma == 32 && IsGameCube(mapping.Substring(0, 32)) && mapping.IndexOf(GameCubeLabels, StringComparison.Ordinal) < 0)
                mapping += Hint + GameCubeLabels + ":=1,";

            int at = mapping.IndexOf(Hint, StringComparison.Ordinal);
            if (at < 0) return mapping;
            at += Hint.Length;
            if (at < mapping.Length && mapping[at] == '!') return mapping;
            int end = at;
            while (end < mapping.Length && mapping[end] != ',' && mapping[end] != ':' && end - at < 127) end++;
            string name = mapping.Substring(at, end - at);
            if (string.Equals(name, GameCubeLabels, StringComparison.Ordinal))
                return Positional(mapping, new[] { (",b:", 'x'), (",x:", 'b') }, GameCubeLabels);
            if (string.Equals(name, ButtonLabels, StringComparison.Ordinal))
                return Positional(mapping, new[] { (",a:", 'b'), (",b:", 'a'), (",x:", 'y'), (",y:", 'x') }, ButtonLabels);
            return mapping;
        }

        /// <summary>
        /// SDL's own text for a mapping, made ready to give SDL again so it
        /// stores the same bindings. SDL takes a given text through the turn
        /// (<see cref="AsSdlStores"/>), and for a controller on the GameCube
        /// list a text with no hint field would get the GameCube hint
        /// unmarked and have b and x swapped. Such a text gets the hint marked
        /// "!", which SDL keeps as it is. SDL stores that hint for the
        /// controller whatever it is given, so only the swap is avoided. Any
        /// other text is returned as it is.
        /// </summary>
        internal static string ForSdlAgain(string mapping)
        {
            if (string.IsNullOrEmpty(mapping) || mapping.IndexOf(',') != 32 || !IsGameCube(mapping.Substring(0, 32))
                || mapping.IndexOf(Hint, StringComparison.Ordinal) >= 0)
                return mapping;
            return (mapping.EndsWith(",", StringComparison.Ordinal) ? mapping : mapping + ",") + Hint + "!" + GameCubeLabels + ":=1,";
        }

        /// <summary>SDL's turn of a row to positional buttons: every field is
        /// found before any is changed, the first of each takes its new
        /// letter, and "!" goes in front of the hint's name.</summary>
        private static string Positional(string mapping, (string Field, char Letter)[] swaps, string hintName)
        {
            var chars = mapping.ToCharArray();
            foreach (var (field, letter) in swaps)
            {
                int at = mapping.IndexOf(field, StringComparison.Ordinal);
                if (at >= 0) chars[at + 1] = letter;
            }
            string swapped = new string(chars);
            int hint = swapped.IndexOf(Hint + hintName, StringComparison.Ordinal);
            return hint < 0 ? swapped : swapped.Insert(hint + Hint.Length, "!");
        }

        /// <summary>Whether SDL reads an ID as a controller on its GameCube
        /// list: an ID of its standard form (<see cref="SdlMappingRow.IsStandardForm"/>),
        /// then its vendor and product (SDL_GetJoystickGUIDInfo).</summary>
        private static bool IsGameCube(string guid)
        {
            if (!SdlMappingRow.IsStandardForm(guid) || !SdlMappingRow.TryReadIds(guid, out ushort vendor, out ushort product)) return false;
            foreach (var (v, p) in GameCube)
                if (v == vendor && p == product) return true;
            return false;
        }

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
