using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PadForge.Common.Input
{
    /// <summary>
    /// One Windows row of the community controller database (#597), held to
    /// the form PadForge's own rows are held to (GamepadMappingFileTests) and
    /// to the limits a row from the network needs: a controller ID in the USB
    /// form with a vendor and a product, a name of at most 128 characters,
    /// bindings SDL's parser takes, the Windows platform field as the last
    /// one, and at most 1,024 characters in all.
    ///
    /// <para>A binding is held shorter than SDL's parser can take. That
    /// parser copies a binding's name into 20 bytes and its value into 128
    /// (SDL_PrivateParseGamepadConfigString), reads a hat as one digit, a
    /// dot and its direction, and stops reading the row at a value too long
    /// for it. A row accepted here reaches neither edge.</para>
    /// </summary>
    internal sealed class ControllerDatabaseRow
    {
        internal const int MaxRowLength = 1024;
        internal const int MaxNameLength = 128;
        /// <summary>The longest binding name SDL knows is rightshoulder, 13
        /// characters, or 14 with a half-axis sign.</summary>
        internal const int MaxBindingNameLength = 16;

        /// <summary>What marks a line as a Windows row.</summary>
        internal const string WindowsField = "platform:Windows,";
        private const string WindowsTail = ",platform:Windows,";
        private const int GuidLength = 32;

        /// <summary>A controller's ID on the USB bus with no name hash and no
        /// version, the form the SDL fork keeps community rows in
        /// (gen_community_mappings.c, ParseUSBGUID): 03000000, the vendor,
        /// 0000, the product, and twelve zeros.</summary>
        private static readonly Regex UsbGuid = new(@"\A03000000[0-9a-f]{4}0000[0-9a-f]{4}000000000000\z",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>A binding: a gamepad control, with a sign for half of an
        /// axis, and the button, axis or hat direction that drives it. Digits
        /// are ASCII, as SDL reads them (SDL_PrivateParseGamepadElement): a
        /// button or an axis by its number, a hat by one digit, a dot and its
        /// direction.</summary>
        private static readonly Regex Binding = new(@"\A[+-]?[a-z][a-z0-9]*:[+-]?(b[0-9]{1,3}|a[0-9]{1,3}~?|h[0-9]\.[0-9]{1,2})\z",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>The controller ID: 32 lowercase hex digits.</summary>
        public string Guid { get; }

        public string Name { get; }

        /// <summary>The row exactly as the community wrote it.</summary>
        public string Text { get; }

        public ushort VendorId { get; }

        public ushort ProductId { get; }

        private string _hash;

        /// <summary>The row's hash, as the baseline holds it.</summary>
        public string Hash => _hash ??= HashOf(Text);

        private ControllerDatabaseRow(string guid, string name, string text, ushort vendor, ushort product)
        {
            Guid = guid;
            Name = name;
            Text = text;
            VendorId = vendor;
            ProductId = product;
        }

        /// <summary>Reads one line. False for anything but a Windows row in
        /// the accepted form.</summary>
        public static bool TryParse(string line, out ControllerDatabaseRow row)
        {
            row = null;
            if (line == null || line.Length > MaxRowLength || line.Length <= GuidLength + WindowsTail.Length) return false;
            if (!line.EndsWith(WindowsTail, StringComparison.Ordinal)) return false;
            if (line[GuidLength] != ',') return false;

            string guid = line.Substring(0, GuidLength);
            if (!TryReadUsbGuid(guid, out ushort vendor, out ushort product)) return false;
            // vJoy's one ID, which every vJoy device shares while each user
            // sets its buttons and axes.
            if (vendor == 0x1234 && product == 0xBEAD) return false;

            int nameEnd = line.IndexOf(',', GuidLength + 1);
            int nameLength = nameEnd - GuidLength - 1;
            if (nameEnd < 0 || nameLength < 1 || nameLength > MaxNameLength) return false;
            string name = line.Substring(GuidLength + 1, nameLength);
            // SDL looks for a hint in the whole row, the name included, and
            // adds no row whose hint it does not know
            // (SDL_PrivateAddGamepadMapping).
            if (!IsPlainName(name) || name.Contains("hint:", StringComparison.Ordinal)) return false;

            // The bindings stand between the name and the platform field.
            // A row with none would make a gamepad with no control of a
            // joystick that works as it is.
            int bindingsEnd = line.Length - WindowsTail.Length;
            if (bindingsEnd < nameEnd) return false;
            string bindings = line.Substring(nameEnd, bindingsEnd - nameEnd);
            bool bound = false;
            foreach (string field in bindings.Split(','))
            {
                if (field.Length == 0) continue;
                int colon = field.IndexOf(':');
                if (colon < 1 || colon > MaxBindingNameLength || !Binding.IsMatch(field)) return false;
                if (EndsInAFieldOfSdls(field.AsSpan(0, colon))) return false;
                bound = true;
            }
            if (!bound) return false;

            row = new ControllerDatabaseRow(guid, name, line, vendor, product);
            return true;
        }

        /// <summary>SDL's own fields of a row: a name hash, a gamepad type, a
        /// face button style, a hint and the platform.</summary>
        private static readonly string[] FieldsOfSdls = { "crc", "type", "face", "hint", "platform" };

        /// <summary>
        /// Whether a control's name ends in one of SDL's own fields. SDL
        /// finds those by their text anywhere in the bindings (strstr for
        /// "crc:", "type:", "face:", "hint:" and "platform:" in
        /// SDL_gamepad.c), so a control named "xcrc" would be read as a name
        /// hash, and the row would map only controllers of that hash. A row
        /// kept here maps every controller of its ID and carries no field
        /// but its bindings and the platform at its end.
        /// </summary>
        private static bool EndsInAFieldOfSdls(ReadOnlySpan<char> control)
        {
            foreach (string field in FieldsOfSdls)
                if (control.EndsWith(field, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Whether a controller ID is in the USB form with a vendor
        /// and a product, and what they are. The IDs are little-endian.</summary>
        internal static bool TryReadUsbGuid(string guid, out ushort vendor, out ushort product)
        {
            vendor = product = 0;
            if (guid == null || !UsbGuid.IsMatch(guid)) return false;
            vendor = (ushort)((HexByte(guid, 10) << 8) | HexByte(guid, 8));
            product = (ushort)((HexByte(guid, 18) << 8) | HexByte(guid, 16));
            return vendor != 0 && product != 0;
        }

        private static int HexByte(string text, int at) =>
            int.Parse(text.AsSpan(at, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

        /// <summary>A name PadForge shows as it is: letters, marks, digits,
        /// punctuation, symbols and the plain space. No control or format
        /// character, no other kind of space, and no half of a surrogate
        /// pair.</summary>
        internal static bool IsPlainName(string name)
        {
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == ' ') continue;
                UnicodeCategory category;
                if (char.IsSurrogate(c))
                {
                    if (!char.IsHighSurrogate(c) || i + 1 >= name.Length || !char.IsLowSurrogate(name[i + 1])) return false;
                    category = CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(c, name[i + 1]));
                    i++;
                }
                else
                {
                    category = CharUnicodeInfo.GetUnicodeCategory(c);
                }
                switch (category)
                {
                    case UnicodeCategory.Control:
                    case UnicodeCategory.Format:
                    case UnicodeCategory.Surrogate:
                    case UnicodeCategory.PrivateUse:
                    case UnicodeCategory.OtherNotAssigned:
                    case UnicodeCategory.SpaceSeparator:
                    case UnicodeCategory.LineSeparator:
                    case UnicodeCategory.ParagraphSeparator:
                        return false;
                }
            }
            return true;
        }

        /// <summary>The first 16 hex digits of the SHA-256 of a row's UTF-8
        /// bytes. tools/gen_controller_database_baseline.py writes the same
        /// for each pinned row.</summary>
        internal static string HashOf(string text) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8).ToLowerInvariant();

        /// <summary>
        /// The row ID that SDL's lookup reaches from a joystick's GUID, or
        /// null when the text is no GUID. SDL tries the GUID with its name
        /// hash cleared, then with its version cleared as well
        /// (SDL_PrivateGetGamepadMappingForGUID). A row in the USB form has
        /// neither, so it maps a joystick exactly when the joystick's GUID
        /// with both cleared is the row's ID. The hash is hex digits 4 to 7
        /// and the version 24 to 27 (SDL_CreateJoystickGUID).
        /// </summary>
        internal static string LookupId(string joystickGuid)
        {
            if (joystickGuid == null || joystickGuid.Length != GuidLength) return null;
            Span<char> id = stackalloc char[GuidLength];
            for (int i = 0; i < GuidLength; i++)
            {
                char c = char.ToLowerInvariant(joystickGuid[i]);
                if (!Uri.IsHexDigit(c)) return null;
                id[i] = (i >= 4 && i < 8) || (i >= 24 && i < 28) ? '0' : c;
            }
            return new string(id);
        }
    }
}
