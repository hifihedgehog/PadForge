using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PadForge.Common.Input
{
    /// <summary>Why a row a person gave was not taken (#600).</summary>
    internal enum SdlRowRefusal
    {
        None,
        /// <summary>No text, or only comments and blank lines.</summary>
        Empty,
        /// <summary>Text that is no row of the database's form.</summary>
        NotARow,
        /// <summary>Rows for other systems only.</summary>
        OtherPlatform,
        /// <summary>An ID that is not 32 hex digits, or the XInput and
        /// default IDs, which name no device.</summary>
        BadId,
        /// <summary>No name, a name over 128 characters, or one with a
        /// control character or a field marker of SDL's in it.</summary>
        BadName,
        /// <summary>A hint field other than the two label hints.</summary>
        Hint,
        /// <summary>A field SDL does not know.</summary>
        UnknownField,
        /// <summary>A binding SDL's parser does not take.</summary>
        BadBinding,
        /// <summary>One field twice.</summary>
        RepeatedField,
        /// <summary>Over 1,024 characters.</summary>
        TooLong,
        /// <summary>No binding at all.</summary>
        NoBindings,
    }

    /// <summary>One binding of a row: the gamepad field (with a sign for half
    /// of an output axis) and the joystick input behind it.</summary>
    internal readonly record struct SdlBinding(string Field, string Input)
    {
        /// <summary>The field without its sign.</summary>
        public string Name => Field.Length > 0 && (Field[0] == '+' || Field[0] == '-') ? Field.Substring(1) : Field;

        public override string ToString() => Field + ":" + Input;
    }

    /// <summary>
    /// A row of SDL's controller database that a person gives PadForge for a
    /// device (#600 items 46 to 49): pasted, picked, guessed, or read from a
    /// file. Its grammar is wider than <see cref="ControllerDatabaseRow"/>'s,
    /// which holds rows from the network: any bus (a Bluetooth pad keeps its
    /// own), and SDL's type and face fields and its two label hints, which
    /// SDL's own rows carry. A checksum field is dropped, since a row kept
    /// for a device carries the device's own checksum in its ID.
    ///
    /// <para>The fields are SDL's (map_StringForGamepadButton and
    /// map_StringForGamepadAxis in SDL_gamepad.c), and a binding is held to
    /// the same form as <see cref="ControllerDatabaseRow"/>'s.</para>
    /// </summary>
    internal sealed class SdlMappingRow
    {
        internal const int MaxLength = ControllerDatabaseRow.MaxRowLength;
        internal const int MaxNameLength = ControllerDatabaseRow.MaxNameLength;

        /// <summary>SDL's button fields, in SDL's order.</summary>
        internal static readonly string[] ButtonFields =
        {
            "a", "b", "x", "y", "back", "guide", "start", "leftstick", "rightstick", "leftshoulder", "rightshoulder",
            "dpup", "dpdown", "dpleft", "dpright", "misc1", "paddle1", "paddle2", "paddle3", "paddle4", "touchpad",
            "misc2", "misc3", "misc4", "misc5", "misc6",
        };

        /// <summary>SDL's axis fields, in SDL's order.</summary>
        internal static readonly string[] AxisFields = { "leftx", "lefty", "rightx", "righty", "lefttrigger", "righttrigger" };

        /// <summary>SDL's gamepad types (map_StringForGamepadType).</summary>
        private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
        {
            "unknown", "standard", "xbox360", "xboxone", "ps3", "ps4", "ps5", "switchpro", "joyconleft", "joyconright",
            "joyconpair", "gamecube", "steam",
        };

        /// <summary>The two hints SDL's own rows carry: the face buttons
        /// named by their labels, or, with "!", by their places, as SDL
        /// keeps a row it has turned to places already.</summary>
        private static readonly Regex LabelHint = new(@"\A!?SDL_GAMECONTROLLER_USE_(BUTTON|GAMECUBE)_LABELS:=1\z",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex Input = new(@"\A[+-]?(b[0-9]{1,3}|a[0-9]{1,3}~?|h[0-9]\.[0-9]{1,2})\z",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex Face = new(@"\A[a-z]{1,8}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>The field markers SDL finds anywhere in a row
        /// (strstr in SDL_gamepad.c), so none may stand in a name.</summary>
        private static readonly string[] Markers = { "crc:", "type:", "face:", "hint:", "platform:", "sdk>=:", "sdk<=:" };

        /// <summary>32 lowercase hex digits.</summary>
        public string Guid { get; }

        public string Name { get; }

        /// <summary>The bindings, sorted by field, as the community writes
        /// them.</summary>
        public IReadOnlyList<SdlBinding> Bindings { get; }

        /// <summary>SDL's gamepad type, or null.</summary>
        public string Type { get; }

        /// <summary>SDL's face button style, or null.</summary>
        public string FaceStyle { get; }

        /// <summary>One of the two label hints, or null.</summary>
        public string Hint { get; }

        private SdlMappingRow(string guid, string name, List<SdlBinding> bindings, string type, string face, string hint)
        {
            Guid = guid;
            Name = name;
            bindings.Sort((x, y) => string.CompareOrdinal(x.Field, y.Field));
            Bindings = bindings;
            Type = type;
            FaceStyle = face;
            Hint = hint;
        }

        /// <summary>Every field the row gives a value: the bindings, then
        /// type, face and hint.</summary>
        public int FieldCount => Bindings.Count + (Type != null ? 1 : 0) + (FaceStyle != null ? 1 : 0) + (Hint != null ? 1 : 0);

        /// <summary>The row in the database's form under its own ID.</summary>
        public string Text => TextUnder(Guid);

        /// <summary>The row in the database's form under another ID: the
        /// bindings sorted, then type, face and hint, and the Windows
        /// platform field last.</summary>
        public string TextUnder(string guid)
        {
            var sb = new StringBuilder(256).Append(guid).Append(',').Append(Name).Append(',');
            foreach (var b in Bindings) sb.Append(b.Field).Append(':').Append(b.Input).Append(',');
            if (Type != null) sb.Append("type:").Append(Type).Append(',');
            if (FaceStyle != null) sb.Append("face:").Append(FaceStyle).Append(',');
            if (Hint != null) sb.Append("hint:").Append(Hint).Append(',');
            return sb.Append(ControllerDatabaseRow.WindowsField).ToString();
        }

        /// <summary>This row under another ID, or another name.</summary>
        public SdlMappingRow With(string guid = null, string name = null) =>
            new(guid ?? Guid, name ?? Name, new List<SdlBinding>(Bindings), Type, FaceStyle, Hint);

        /// <summary>The input a field is bound to, or null.</summary>
        public string InputOf(string field)
        {
            foreach (var b in Bindings)
                if (string.Equals(b.Field, field, StringComparison.Ordinal)) return b.Input;
            return null;
        }

        /// <summary>The bindings alone, as text that two rows of the same
        /// mapping share whatever their ID, name and checksum: the sorted
        /// bindings and the type and face fields.</summary>
        public string Layout
        {
            get
            {
                var sb = new StringBuilder(200);
                foreach (var b in Bindings) sb.Append(b.Field).Append(':').Append(b.Input).Append(',');
                if (Type != null) sb.Append("type:").Append(Type).Append(',');
                if (FaceStyle != null) sb.Append("face:").Append(FaceStyle).Append(',');
                return sb.ToString();
            }
        }

        /// <summary>A row built from bindings, as Copy and Guess build one.</summary>
        internal static SdlMappingRow Build(string guid, string name, IEnumerable<SdlBinding> bindings)
        {
            var list = new List<SdlBinding>(bindings);
            return new SdlMappingRow(guid, CleanName(name), list, null, null, null);
        }

        /// <summary>A name in the form a row takes: commas and line breaks
        /// become spaces, the ends are trimmed, it is cut at 128
        /// characters, and an empty one is "Controller".</summary>
        internal static string CleanName(string name)
        {
            var sb = new StringBuilder(name?.Length ?? 0);
            foreach (char c in name ?? string.Empty)
                sb.Append(c == ',' || char.IsControl(c) ? ' ' : c);
            string clean = sb.ToString().Trim();
            foreach (string marker in Markers) clean = clean.Replace(marker, marker.TrimEnd(':'), StringComparison.Ordinal);
            if (clean.Length > MaxNameLength) clean = clean.Substring(0, MaxNameLength).TrimEnd();
            return clean.Length == 0 ? "Controller" : clean;
        }

        /// <summary>
        /// Reads one row. A row may carry the Windows platform field or none,
        /// and a checksum field, which is dropped. False, with the reason, for
        /// anything else.
        /// </summary>
        public static bool TryParse(string line, out SdlMappingRow row, out SdlRowRefusal why)
        {
            row = null;
            line = line?.Trim();
            if (string.IsNullOrEmpty(line)) { why = SdlRowRefusal.Empty; return false; }
            if (line.Length > MaxLength) { why = SdlRowRefusal.TooLong; return false; }
            string[] parts = line.Split(',');
            if (parts.Length < 3) { why = SdlRowRefusal.NotARow; return false; }

            string guid = parts[0].Trim().ToLowerInvariant();
            if (!IsDeviceGuid(guid)) { why = guid.Length == 32 || guid is "xinput" or "default" ? SdlRowRefusal.BadId : SdlRowRefusal.NotARow; return false; }

            string name = parts[1];
            if (name.Length == 0 || name.Length > MaxNameLength || !ControllerDatabaseRow.IsPlainName(name) || HasMarker(name))
            {
                why = SdlRowRefusal.BadName;
                return false;
            }

            var bindings = new List<SdlBinding>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string type = null, face = null, hint = null;
            for (int i = 2; i < parts.Length; i++)
            {
                string field = parts[i].Trim();
                if (field.Length == 0) continue;
                int colon = field.IndexOf(':');
                if (colon < 1) { why = SdlRowRefusal.NotARow; return false; }
                string key = field.Substring(0, colon);
                string value = field.Substring(colon + 1);
                switch (key)
                {
                    case "platform":
                        if (!string.Equals(value, "Windows", StringComparison.Ordinal)) { why = SdlRowRefusal.OtherPlatform; return false; }
                        continue;
                    case "crc":
                        continue;
                    case "type":
                        if (!Types.Contains(value)) { why = SdlRowRefusal.UnknownField; return false; }
                        if (type != null) { why = SdlRowRefusal.RepeatedField; return false; }
                        type = value;
                        continue;
                    case "face":
                        if (!Face.IsMatch(value)) { why = SdlRowRefusal.UnknownField; return false; }
                        if (face != null) { why = SdlRowRefusal.RepeatedField; return false; }
                        face = value;
                        continue;
                    case "hint":
                        if (!LabelHint.IsMatch(value) || hint != null) { why = SdlRowRefusal.Hint; return false; }
                        hint = value;
                        continue;
                }
                if (key.StartsWith("sdk", StringComparison.Ordinal)) { why = SdlRowRefusal.Hint; return false; }
                bool signed = key[0] == '+' || key[0] == '-';
                string bare = signed ? key.Substring(1) : key;
                bool axis = Array.IndexOf(AxisFields, bare) >= 0;
                bool button = Array.IndexOf(ButtonFields, bare) >= 0;
                if (!axis && !button) { why = SdlRowRefusal.UnknownField; return false; }
                if (signed && !axis) { why = SdlRowRefusal.UnknownField; return false; }
                if (!Input.IsMatch(value)) { why = SdlRowRefusal.BadBinding; return false; }
                if (!seen.Add(key)) { why = SdlRowRefusal.RepeatedField; return false; }
                bindings.Add(new SdlBinding(key, value));
            }
            if (bindings.Count == 0) { why = SdlRowRefusal.NoBindings; return false; }
            row = new SdlMappingRow(guid, name, bindings, type, face, hint);
            why = SdlRowRefusal.None;
            return true;
        }

        private static bool HasMarker(string name)
        {
            foreach (string marker in Markers)
                if (name.Contains(marker, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Whether text is a device's ID: 32 hex digits, not all
        /// zero.</summary>
        internal static bool IsDeviceGuid(string guid)
        {
            if (guid == null || guid.Length != 32) return false;
            bool any = false;
            foreach (char c in guid)
            {
                if (!Uri.IsHexDigit(c)) return false;
                if (c != '0') any = true;
            }
            return any;
        }

        // ─────────────────────────────────────────────
        //  IDs
        // ─────────────────────────────────────────────

        /// <summary>
        /// A device's ID as the community database writes it (#600 item 46):
        /// the ID SDL reports with its checksum and version zeroed, the bus
        /// and the driver's mark kept. SDL clears a device's checksum, and on
        /// its fallback tries its version, then compares the rest, bus
        /// included (SDL_PrivateMatchGamepadMappingForGUID), so a row in this
        /// form maps every device of that bus, vendor and product.
        /// </summary>
        internal static string CommunityId(string deviceGuid) => ControllerDatabaseRow.LookupId(deviceGuid);

        /// <summary>
        /// Whether SDL reads a device through a row of this ID: the two IDs
        /// agree once both checksums are cleared, and either their versions
        /// agree or the row's is zero (SDL_PrivateFindGamepadMappingForGUID).
        /// </summary>
        internal static bool Maps(string rowGuid, string deviceGuid)
        {
            if (rowGuid == null || deviceGuid == null || rowGuid.Length != 32 || deviceGuid.Length != 32) return false;
            bool rowVersionZero = true;
            for (int i = 0; i < 32; i++)
            {
                if (i >= 4 && i < 8) continue;
                char r = char.ToLowerInvariant(rowGuid[i]), d = char.ToLowerInvariant(deviceGuid[i]);
                if (i >= 24 && i < 28)
                {
                    if (r != '0') rowVersionZero = false;
                    continue;
                }
                if (r != d) return false;
            }
            if (rowVersionZero) return true;
            return string.Compare(rowGuid, 24, deviceGuid, 24, 4, StringComparison.OrdinalIgnoreCase) == 0;
        }

        /// <summary>The vendor and product an ID of SDL's standard form
        /// carries, little-endian at hex digits 8 and 16, or false.</summary>
        internal static bool TryReadIds(string guid, out ushort vendor, out ushort product)
        {
            vendor = product = 0;
            if (guid == null || guid.Length != 32) return false;
            if (string.CompareOrdinal(guid, 12, "0000", 0, 4) != 0 || string.CompareOrdinal(guid, 20, "0000", 0, 4) != 0) return false;
            if (!ushort.TryParse(guid.AsSpan(8, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort v0)
                || !ushort.TryParse(guid.AsSpan(10, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort v1)
                || !ushort.TryParse(guid.AsSpan(16, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort p0)
                || !ushort.TryParse(guid.AsSpan(18, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort p1))
                return false;
            vendor = (ushort)((v1 << 8) | v0);
            product = (ushort)((p1 << 8) | p0);
            return vendor != 0 || product != 0;
        }

        // ─────────────────────────────────────────────
        //  Text with many rows
        // ─────────────────────────────────────────────

        /// <summary>
        /// The rows in a text: one per line, comments and blank lines left
        /// out. Rows for other systems are passed over and counted. A line
        /// that is no row gives its reason.
        /// </summary>
        internal static List<SdlMappingRow> ReadAll(string text, out int otherPlatforms, out SdlRowRefusal firstRefusal, out int refused)
        {
            var rows = new List<SdlMappingRow>();
            otherPlatforms = refused = 0;
            firstRefusal = SdlRowRefusal.None;
            if (text == null) { firstRefusal = SdlRowRefusal.Empty; return rows; }
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (TryParse(line, out var row, out var why)) { rows.Add(row); continue; }
                if (why == SdlRowRefusal.OtherPlatform) { otherPlatforms++; continue; }
                refused++;
                if (firstRefusal == SdlRowRefusal.None) firstRefusal = why;
            }
            if (rows.Count == 0 && firstRefusal == SdlRowRefusal.None)
                firstRefusal = otherPlatforms > 0 ? SdlRowRefusal.OtherPlatform : SdlRowRefusal.Empty;
            return rows;
        }

        /// <summary>How a picked row stands to the device.</summary>
        internal enum Fit
        {
            /// <summary>SDL would read the device through the row's ID.</summary>
            ItsId,
            /// <summary>Another bus or version of the same vendor and product.</summary>
            SameProduct,
            /// <summary>Another device's row.</summary>
            Other,
        }

        /// <summary>
        /// The row for a device among the rows of a text: one SDL would read
        /// it through, then one of its vendor and product, then the first.
        /// </summary>
        internal static SdlMappingRow PickFor(IReadOnlyList<SdlMappingRow> rows, string deviceGuid, out Fit fit)
        {
            fit = Fit.Other;
            if (rows == null || rows.Count == 0) return null;
            foreach (var row in rows)
                if (Maps(row.Guid, deviceGuid)) { fit = Fit.ItsId; return row; }
            if (TryReadIds(deviceGuid, out ushort v, out ushort p))
                foreach (var row in rows)
                    if (TryReadIds(row.Guid, out ushort rv, out ushort rp) && rv == v && rp == p) { fit = Fit.SameProduct; return row; }
            return rows[0];
        }
    }
}
