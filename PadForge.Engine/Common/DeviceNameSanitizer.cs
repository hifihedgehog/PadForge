using System;
using System.Text;

namespace PadForge.Engine
{
    /// <summary>
    /// Sanitizes device names from HID product strings.
    ///
    /// Cheap USB devices return fixed-size buffers from
    /// <c>HidD_GetProductString</c>. Some fill only part of the buffer and
    /// leave embedded null characters or non-printable garbage. Names with
    /// embedded <c>\0</c> characters pass .NET string handling but are
    /// invalid in XML 1.0 and crash <see cref="System.Xml.Serialization.XmlSerializer"/>
    /// mid-write — truncating the settings file at whatever byte was last
    /// flushed. (Fix for issue #53: Ugee tablet product string
    /// <c>"ugee device책책책책\0\0\0\0\0"</c>.)
    ///
    /// Run every raw HID product string through <see cref="Clean"/> at
    /// ingress. The companion fix in <c>SettingsService.Save</c> writes to a
    /// <see cref="System.IO.MemoryStream"/> first so a serializer crash can
    /// never truncate the on-disk file, but that's belt-and-suspenders; the
    /// real fix is not letting these bytes reach serialization in the first
    /// place.
    /// </summary>
    internal static class DeviceNameSanitizer
    {
        /// <summary>
        /// Cleans a raw HID product string for safe storage and display.
        /// Truncates at the first embedded null (HID strings are C-style),
        /// strips characters invalid in XML 1.0, collapses whitespace
        /// runs, and trims. Returns null if nothing meaningful is left.
        /// Valid Unicode (CJK, emoji, accented Latin, etc.) is preserved.
        /// </summary>
        public static string Clean(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return null;

            // HID product strings are null-terminated. Anything after the
            // first \0 is buffer padding or uninitialized bytes, never
            // part of the device name.
            int nullIndex = raw.IndexOf('\0');
            if (nullIndex >= 0)
                raw = raw.Substring(0, nullIndex);

            if (string.IsNullOrWhiteSpace(raw))
                return null;

            // XML 1.0 valid characters:
            //   #x9 | #xA | #xD | [#x20-#xD7FF] | [#xE000-#xFFFD] |
            //   [#x10000-#x10FFFF]
            // Drop C0 controls (except tab/LF/CR which we normalize to
            // space for display), 0xFFFE, 0xFFFF, and lone surrogates.
            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];

                if (c == '\t' || c == '\n' || c == '\r')
                {
                    sb.Append(' ');
                    continue;
                }

                if (c < 0x20)
                    continue;

                if (c == 0xFFFE || c == 0xFFFF)
                    continue;

                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1]))
                    {
                        sb.Append(c);
                        sb.Append(raw[++i]);
                    }
                    // Lone high surrogate — drop.
                    continue;
                }
                if (char.IsLowSurrogate(c))
                    continue; // Lone low surrogate — drop.

                sb.Append(c);
            }

            string result = sb.ToString().Trim();

            // Collapse runs of internal whitespace to a single space.
            while (result.Contains("  "))
                result = result.Replace("  ", " ");

            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
    }
}
