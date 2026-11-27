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
        /// <summary>No name, a name over 128 characters, or one with a field
        /// marker of SDL's or a character other than letters, marks, digits,
        /// punctuation, symbols and the plain space in it
        /// (ControllerDatabaseRow.IsPlainName).</summary>
        BadName,
        /// <summary>A hint field other than the two label hints, or an SDK
        /// bound.</summary>
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
    /// SDL's own rows carry. A checksum field is read into the row's ID, as
    /// SDL reads it, so a row of SDL's for one name of a model stays that
    /// name's. Paste, Find and Guess keep a row under the device's own ID,
    /// with the device's own checksum.
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
        /// and a checksum field, which goes into an ID that carries no
        /// checksum (<see cref="WithCrc"/>). False, with the reason, for
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
            string type = null, face = null, hint = null, crc = null;
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
                        // SDL finds the first crc field (strstr), and reads
                        // any value (CrcOf).
                        crc ??= value;
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
                        if (!LabelHint.IsMatch(value)) { why = SdlRowRefusal.Hint; return false; }
                        if (hint != null) { why = SdlRowRefusal.RepeatedField; return false; }
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
            row = new SdlMappingRow(WithCrc(guid, crc), name, bindings, type, face, hint);
            why = SdlRowRefusal.None;
            return true;
        }

        /// <summary>
        /// A crc field's value as SDL reads it: SDL_strtol in base 16, which is
        /// the C library's strtol on Windows (HAVE_STRTOL), cast to 16 bits
        /// (SDL_PrivateAddMappingForGUID). Leading white space, a sign and a
        /// 0x go first, the hex digits are read up to the first other
        /// character, a value past a 32-bit long is the long's limit, and text
        /// with no digit reads 0, which sets no checksum.
        /// </summary>
        internal static ushort CrcOf(string value)
        {
            int i = 0;
            while (i < value.Length && value[i] is ' ' or '\t' or '\n' or '\v' or '\f' or '\r') i++;
            bool negative = false;
            if (i < value.Length && value[i] is '+' or '-') negative = value[i++] == '-';
            if (i + 2 < value.Length && value[i] == '0' && value[i + 1] is 'x' or 'X' && Uri.IsHexDigit(value[i + 2])) i += 2;
            long n = 0;
            bool over = false;
            for (; i < value.Length && Uri.IsHexDigit(value[i]); i++)
            {
                if (over) continue;
                n = n * 16 + Convert.ToInt32(value[i].ToString(), 16);
                if (n > (negative ? -(long)int.MinValue : int.MaxValue)) over = true;
            }
            long read = over ? (negative ? int.MinValue : int.MaxValue) : negative ? -n : n;
            return (ushort)(read & 0xFFFF);
        }

        /// <summary>An ID with a crc field's value written into its checksum,
        /// as SDL does for an ID that carries none: an ID that carries one
        /// keeps it, and SDL writes it over the field
        /// (SDL_PrivateAddMappingForGUID). The checksum is the ID's third and
        /// fourth bytes (hex digits 4 to 7), little-endian
        /// (SDL_SetJoystickGUIDCRC).</summary>
        private static string WithCrc(string guid, string crc)
        {
            if (crc == null || ChecksumOf(guid) != 0) return guid;
            ushort value = CrcOf(crc);
            if (value == 0) return guid;
            return guid.Substring(0, 4) + (value & 0xFF).ToString("x2", CultureInfo.InvariantCulture)
                + (value >> 8).ToString("x2", CultureInfo.InvariantCulture) + guid.Substring(8);
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

        /// <summary>Whether SDL reads a device through a row of this ID at some
        /// step of its lookup (<see cref="Reach"/>).</summary>
        internal static bool Maps(string rowGuid, string deviceGuid) => Reach(rowGuid, deviceGuid) > 0;

        /// <summary>
        /// The step of SDL's lookup at which it reads a device through a row
        /// of this ID, or 0 when no step does
        /// (SDL_PrivateGetGamepadMappingForGUID,
        /// SDL_PrivateMatchGamepadMappingForGUID). The two IDs agree once both
        /// checksums and versions are set aside. Then 1: the device's version,
        /// with the device's checksum, or a row naming none for a device with
        /// none. 2: the device's version, a row naming no checksum for a
        /// device with one. 3 and 4: another version in the same two ways, for
        /// a device whose ID is of SDL's standard form with a vendor and a
        /// product and is no MFi controller's (SDL_JoystickGUIDUsesVersion). A
        /// row whose ID names a checksum other than the device's is never read:
        /// SDL writes the ID's checksum into the row as it adds it
        /// (SDL_PrivateAddMappingForGUID) and passes over a row whose checksum
        /// is not the device's. SDL reads the row of the nearest step, and of
        /// rows at one step the first in its list. 5 to 8: steps 1 to 4 for the
        /// device's GUID on the USB bus (ControllerDatabaseRow.UsbBusFallback),
        /// which SDL tries only after every step for its own GUID
        /// (SDL_gamepad.c:1652-1667).
        /// </summary>
        internal static int Reach(string rowGuid, string deviceGuid)
        {
            int step = ReachAsGiven(rowGuid, deviceGuid);
            if (step > 0) return step;
            string usb = ControllerDatabaseRow.UsbBusFallback(deviceGuid);
            step = usb == null ? 0 : ReachAsGiven(rowGuid, usb);
            return step > 0 ? step + 4 : 0;
        }

        private static int ReachAsGiven(string rowGuid, string deviceGuid)
        {
            if (rowGuid == null || deviceGuid == null || rowGuid.Length != 32 || deviceGuid.Length != 32) return 0;
            for (int i = 0; i < 32; i++)
            {
                if ((i >= 4 && i < 8) || (i >= 24 && i < 28)) continue;
                if (char.ToLowerInvariant(rowGuid[i]) != char.ToLowerInvariant(deviceGuid[i])) return 0;
            }
            ushort rowChecksum = ChecksumOf(rowGuid), deviceChecksum = ChecksumOf(deviceGuid);
            if (rowChecksum != 0 && rowChecksum != deviceChecksum) return 0;
            int step = 1;
            if (string.Compare(rowGuid, 24, deviceGuid, 24, 4, StringComparison.OrdinalIgnoreCase) != 0)
            {
                if (!UsesVersion(deviceGuid)) return 0;
                step = 3;
            }
            return rowChecksum == 0 && deviceChecksum != 0 ? step + 1 : step;
        }

        /// <summary>Whether SDL's lookup sets a device's version aside on its
        /// last tries (SDL_JoystickGUIDUsesVersion): its ID is of the standard
        /// form with a vendor and a product, and is no MFi controller's, whose
        /// version holds its buttons (driver mark 'm').</summary>
        private static bool UsesVersion(string guid)
            => IsStandardForm(guid) && TryReadIds(guid, out ushort vendor, out ushort product) && vendor != 0 && product != 0
               && !string.Equals(guid.Substring(28, 2), "6d", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether an ID of 32 hex digits is of SDL's standard form,
        /// from which SDL reads a vendor, a product, a version and a checksum
        /// (SDL_GetJoystickGUIDInfo): a bus below 0x20 or the virtual bus,
        /// 0xFF (SDL_HARDWARE_BUS_VIRTUAL), and zero words after the vendor and
        /// after the product.</summary>
        internal static bool IsStandardForm(string guid)
        {
            if (guid == null || guid.Length != 32 || !HexByte(guid, 0, out int low) || !HexByte(guid, 2, out int high)) return false;
            int bus = low | (high << 8);
            if (bus >= 0x20 && bus != 0xFF) return false;
            return string.CompareOrdinal(guid, 12, "0000", 0, 4) == 0 && string.CompareOrdinal(guid, 20, "0000", 0, 4) == 0;
        }

        /// <summary>The name checksum an ID of 32 hex digits carries: its
        /// third and fourth bytes (hex digits 4 to 7), little-endian, as SDL_GetJoystickGUIDInfo
        /// reads them. Zero for any other text.</summary>
        internal static ushort ChecksumOf(string id)
        {
            if (id == null || id.Length != 32) return 0;
            return HexByte(id, 4, out int low) && HexByte(id, 6, out int high) ? (ushort)(low | (high << 8)) : (ushort)0;
        }

        private static bool HexByte(string id, int at, out int value) =>
            int.TryParse(id.AsSpan(at, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);

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
            /// <summary>The same vendor and product under an ID SDL would not
            /// read the device through: another bus, or another version of a
            /// device SDL keeps to its own version for.</summary>
            SameProduct,
            /// <summary>Another device's row.</summary>
            Other,
        }

        /// <summary>
        /// The row for a device among the rows of a text: the one SDL would
        /// read it through, at the nearest step of its lookup
        /// (<see cref="Reach"/>), the first of them at one step, then one of
        /// its vendor and product, then the first.
        /// </summary>
        internal static SdlMappingRow PickFor(IReadOnlyList<SdlMappingRow> rows, string deviceGuid, out Fit fit)
        {
            fit = Fit.Other;
            if (rows == null || rows.Count == 0) return null;
            SdlMappingRow nearest = null;
            int nearestStep = int.MaxValue;
            foreach (var row in rows)
            {
                int step = Reach(row.Guid, deviceGuid);
                if (step > 0 && step < nearestStep)
                {
                    nearest = row;
                    nearestStep = step;
                }
            }
            if (nearest != null)
            {
                fit = Fit.ItsId;
                return nearest;
            }
            if (TryReadIds(deviceGuid, out ushort v, out ushort p))
                foreach (var row in rows)
                    if (TryReadIds(row.Guid, out ushort rv, out ushort rp) && rv == v && rp == p) { fit = Fit.SameProduct; return row; }
            return rows[0];
        }
    }
}
