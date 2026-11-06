using System;
using System.IO;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace PadForge.Common.ConfigImport
{
    /// <summary>The kinds of another tool's config file PadForge reads.</summary>
    public enum ForeignConfigKind
    {
        Unknown = 0,
        Rewasd = 1,
        Ds4Windows = 2,
    }

    /// <summary>
    /// Opens another tool's config file as data. The file came from somewhere
    /// else, so the reader takes no chances with it: the size is capped before
    /// a byte is parsed, XML is read with DTDs prohibited and no resolver, so
    /// an entity can neither expand nor fetch anything, and JSON goes through
    /// <see cref="JsonDocument"/> with a depth limit. The kind is decided from
    /// the content, never from the extension.
    /// </summary>
    internal static class ForeignConfigReader
    {
        /// <summary>The largest public sample is under 400 KB. Sixteen MB
        /// leaves room for any real config and refuses a hostile one.</summary>
        public const long MaxFileBytes = 16L * 1024 * 1024;

        /// <summary>True when a file's name says it might be a config this
        /// reader takes, for the drop zone. The reader still decides by
        /// content.</summary>
        public static bool HasForeignExtension(string path)
        {
            string ext = Path.GetExtension(path ?? string.Empty);
            return ext.Equals(".rewasd", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".xml", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Reads a file off disk, refusing one over the cap before
        /// reading it. Returns null with a reason key when it cannot.</summary>
        public static byte[] ReadCapped(string path, out string errorKey)
        {
            errorKey = null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    errorKey = ImportReasons.FileUnreadable;
                    return null;
                }
                if (info.Length > MaxFileBytes)
                {
                    errorKey = ImportReasons.FileTooLarge;
                    return null;
                }
                return File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                errorKey = ImportReasons.FileUnreadable;
                return null;
            }
        }

        /// <summary>Decides what a file holds from what it starts with: a
        /// <c>{</c> is JSON, and XML has to name its root.</summary>
        public static ForeignConfigKind Detect(byte[] bytes)
        {
            int i = SkipPreamble(bytes);
            if (i >= bytes.Length) return ForeignConfigKind.Unknown;
            if (bytes[i] == (byte)'{') return ForeignConfigKind.Rewasd;
            // Anything else may be XML in any encoding its preamble names.
            var root = TryParseXml(bytes, out _);
            return root?.Root?.Name.LocalName == "DS4Windows" ? ForeignConfigKind.Ds4Windows : ForeignConfigKind.Unknown;
        }

        /// <summary>Parses JSON with the comments and trailing commas hand
        /// editors leave behind. Returns null on anything malformed.</summary>
        public static JsonDocument TryParseJson(byte[] bytes)
        {
            try
            {
                int start = SkipPreamble(bytes);
                return JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, start, bytes.Length - start),
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                        MaxDepth = 64,
                    });
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Parses XML with DTDs prohibited, so a document that
        /// declares one is refused outright. Returns null on anything
        /// malformed.</summary>
        public static XDocument TryParseXml(byte[] bytes, out bool refusedDtd)
        {
            refusedDtd = false;
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 1024,
                MaxCharactersInDocument = MaxFileBytes,
                CloseInput = true,
            };
            try
            {
                using var stream = new MemoryStream(bytes);
                using var reader = XmlReader.Create(stream, settings);
                return XDocument.Load(reader);
            }
            catch (XmlException ex)
            {
                refusedDtd = ex.Message.IndexOf("DTD", StringComparison.OrdinalIgnoreCase) >= 0;
                return null;
            }
        }

        /// <summary>A file beside <paramref name="path"/>'s parent folder,
        /// such as DS4Windows' <c>Actions.xml</c> one level above its
        /// <c>Profiles</c> folder, parsed the same safe way. Null when it is
        /// absent or malformed, which only means the extra data is not
        /// there.</summary>
        public static XDocument TryReadSibling(string path, string siblingName)
        {
            try
            {
                string profilesDir = Path.GetDirectoryName(Path.GetFullPath(path));
                string parent = string.IsNullOrEmpty(profilesDir) ? null : Path.GetDirectoryName(profilesDir);
                if (string.IsNullOrEmpty(parent)) return null;
                string candidate = Path.Combine(parent, siblingName);
                if (!File.Exists(candidate)) return null;
                byte[] bytes = ReadCapped(candidate, out _);
                return bytes == null ? null : TryParseXml(bytes, out _);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        private static int SkipPreamble(byte[] bytes)
        {
            int i = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) i = 3;
            while (i < bytes.Length && (bytes[i] == ' ' || bytes[i] == '\t' || bytes[i] == '\r' || bytes[i] == '\n'))
                i++;
            return i;
        }
    }
}
