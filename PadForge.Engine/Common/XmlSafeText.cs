using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// Text the settings file can hold. XML 1.0 has no way to write a control
    /// character, U+FFFE, U+FFFF or half a surrogate pair, and XmlSerializer
    /// throws on one, so a single such character in a single name fails every
    /// save until the name is gone.
    /// </summary>
    public static class XmlSafeText
    {
        /// <summary>The text with every character XML 1.0 cannot hold taken
        /// out, as <see cref="XmlConvert.IsXmlChar"/> and
        /// <see cref="XmlConvert.IsXmlSurrogatePair"/> judge them. The same
        /// string when there are none.</summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            StringBuilder kept = null;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (XmlConvert.IsXmlChar(c))
                {
                    kept?.Append(c);
                }
                else if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], c))
                {
                    kept?.Append(c).Append(text[i + 1]);
                    i++;
                }
                else
                {
                    kept ??= new StringBuilder(text.Length).Append(text, 0, i);
                }
            }
            return kept?.ToString() ?? text;
        }

        /// <summary>What <see cref="XmlSerializer.Serialize(Stream, object)"/>
        /// writes, with every string passed through <see cref="Clean"/>, so a
        /// name from any source costs at most its bad characters, never the
        /// save.</summary>
        public static byte[] Serialize(XmlSerializer serializer, object data)
        {
            using var stream = new MemoryStream();
            using (var writer = new CleaningWriter(stream))
                serializer.Serialize(writer, data);
            return stream.ToArray();
        }

        // Writes what XmlSerializer writes to a stream, byte for byte, with one
        // difference. Element text and attribute values both arrive through
        // WriteString. Left alone, this writer does not refuse a bad character:
        // it writes one that the settings file then cannot load.
        private sealed class CleaningWriter : XmlTextWriter
        {
            public CleaningWriter(Stream stream) : base(stream, new UTF8Encoding(false))
            {
                Formatting = Formatting.Indented;
                Indentation = 2;
            }

            public override void WriteString(string text) => base.WriteString(Clean(text));
        }
    }
}
