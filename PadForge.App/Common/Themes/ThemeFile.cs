using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PadForge.Common.Themes
{
    /// <summary>Where a theme comes from.</summary>
    internal enum ThemeKind
    {
        /// <summary>System, Light or Dark: PadForge's own looks.</summary>
        Stock,
        /// <summary>One of the themes inside PadForge.</summary>
        Starter,
        /// <summary>A theme file the user listed.</summary>
        File,
        /// <summary>A Steam profile's game, worn while that profile is active.</summary>
        GameLook,
    }

    /// <summary>Why a listed theme file cannot be shown.</summary>
    internal enum ThemeFault { None, Missing, Unreadable }

    /// <summary>
    /// One theme (#591): a name, a base, four colors and an optional
    /// picture. The picture is 1176 by 240 units, drawn behind the top of
    /// the page.
    /// </summary>
    internal sealed class ThemeDefinition
    {
        /// <summary>The widest and tallest a theme's picture is decoded:
        /// twice the 1176 by 240 units it is drawn at.</summary>
        internal const int PictureWidth = 2352, PictureHeight = 480;

        internal string Id;
        internal ThemeKind Kind;
        internal string Name;
        /// <summary>A second line under the name: a starter's look.</summary>
        internal string Look;
        internal string Author;
        /// <summary>Where the picture came from, in the author's words.</summary>
        internal string Credits;
        internal bool Dark = true;
        internal Color? Background, Accent, Secondary, Attention;
        /// <summary>The picture as the file holds it, PNG or JPEG.</summary>
        internal byte[] PictureBytes;
        /// <summary>The picture's entry name inside a package.</summary>
        internal string PictureName;
        /// <summary>Where the gallery tile's window into the picture
        /// starts, as a share of the picture's width. The window is
        /// <see cref="TileShare"/> of the width, so it shows the subject at
        /// a size that can be told, where the whole picture would be a
        /// thin line.</summary>
        internal double TileAt = DefaultTileAt;
        internal const double DefaultTileAt = 0.33, TileShare = 0.425;
        /// <summary>A listed file's full path.</summary>
        internal string Path;
        internal ThemeFault Fault;

        private BitmapSource _picture, _thumbnail;
        private bool _pictureRead, _thumbnailRead;

        internal ThemeSeeds Seeds => new ThemeSeeds
        {
            Dark = Dark, Background = Background, Accent = Accent, Secondary = Secondary, Attention = Attention,
        };

        /// <summary>Whether the theme is anything but PadForge's own look.</summary>
        internal bool IsCustom => Kind != ThemeKind.Stock;

        /// <summary>The picture, decoded once and frozen. Null when the
        /// theme has none or its bytes hold no picture.</summary>
        internal BitmapSource Picture
        {
            get
            {
                if (!_pictureRead)
                {
                    _picture = BoundedBitmap.FromBytes(PictureBytes, PictureWidth, PictureHeight, scaleUp: false);
                    _pictureRead = true;
                }
                return _picture;
            }
        }

        /// <summary>Gives the theme a picture that is already drawn: one
        /// the builder or a game's look just made, which has no file yet.</summary>
        internal void SetPicture(BitmapSource picture)
        {
            _picture = picture;
            _thumbnail = picture;
            _pictureRead = true;
            _thumbnailRead = true;
        }

        /// <summary>The picture at a gallery tile's size.</summary>
        internal BitmapSource Thumbnail
        {
            get
            {
                if (!_thumbnailRead)
                {
                    _thumbnail = BoundedBitmap.FromBytes(PictureBytes, 588, 120, scaleUp: false);
                    _thumbnailRead = true;
                }
                return _thumbnail;
            }
        }

        /// <summary>A copy that shares nothing a change could reach but
        /// the picture's bytes, which no one changes in place.</summary>
        internal ThemeDefinition Clone() => new ThemeDefinition
        {
            Id = Id, Kind = Kind, Name = Name, Look = Look, Author = Author, Credits = Credits, Dark = Dark,
            Background = Background, Accent = Accent, Secondary = Secondary, Attention = Attention,
            PictureBytes = PictureBytes, PictureName = PictureName, TileAt = TileAt, Path = Path, Fault = Fault,
        };
    }

    /// <summary>
    /// Reads and writes a theme file, <c>.pftheme</c> (#591). With colors
    /// only it is plain JSON, which a text editor can change. With a picture
    /// it is a zip that holds <c>theme.json</c> and the picture, as a sound
    /// package and an icon package are zips. The two are told apart by the
    /// file's first bytes.
    /// </summary>
    internal static class ThemeFile
    {
        internal const string Extension = ".pftheme";
        internal const string ManifestName = "theme.json";

        /// <summary>The most of a theme file PadForge reads, of its
        /// settings and of its picture.</summary>
        internal const long MaxFileBytes = 24L * 1024 * 1024, MaxPictureBytes = 16L * 1024 * 1024;
        internal const int MaxManifestBytes = 64 * 1024;

        private const int MaxName = 80, MaxLook = 60, MaxAuthor = 80, MaxCredits = 400;

        /// <summary>The theme in a file. Never throws: a file that is not
        /// there or is no theme comes back with its fault set and the name
        /// it was listed under.</summary>
        internal static ThemeDefinition Read(string path, string listedName = null)
        {
            string fallback = string.IsNullOrWhiteSpace(listedName) ? System.IO.Path.GetFileNameWithoutExtension(path) : listedName;
            var broken = new ThemeDefinition { Kind = ThemeKind.File, Name = fallback, Path = path, Fault = ThemeFault.Unreadable };
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    broken.Fault = ThemeFault.Missing;
                    return broken;
                }
                if (info.Length > MaxFileBytes) return broken;
                // Another program may hold the file while it saves it.
                byte[] data;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    data = new byte[stream.Length];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int got = stream.Read(data, read, data.Length - read);
                        if (got <= 0) break;
                        read += got;
                    }
                    if (read != data.Length) return broken;
                }
                var theme = Parse(data, System.IO.Path.GetFileNameWithoutExtension(path));
                if (theme == null) return broken;
                theme.Kind = ThemeKind.File;
                theme.Path = path;
                return theme;
            }
            // ArgumentException: a path from a settings file edited by hand that
            // is no path, such as one holding a null character.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                           or System.Security.SecurityException or ArgumentException)
            {
                broken.Fault = File.Exists(path) ? ThemeFault.Unreadable : ThemeFault.Missing;
                return broken;
            }
        }

        /// <summary>The theme in a file's bytes, or null when they hold no theme.</summary>
        internal static ThemeDefinition Parse(byte[] data, string fallbackName)
        {
            if (data == null || data.Length == 0 || data.Length > MaxFileBytes) return null;
            try
            {
                bool zip = data.Length >= 4 && data[0] == 0x50 && data[1] == 0x4B && data[2] == 0x03 && data[3] == 0x04;
                return zip ? ParsePackage(data, fallbackName) : ParseManifest(data, fallbackName);
            }
            // InvalidOperationException is what a string or a name that holds
            // half of a surrogate pair raises when it is read.
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or FormatException
                                           or ArgumentException or NotSupportedException or DecoderFallbackException
                                           or InvalidOperationException)
            {
                return null;
            }
        }

        private static ThemeDefinition ParsePackage(byte[] data, string fallbackName)
        {
            using var archive = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
            // The settings: the entry named theme.json, the shallowest when several are.
            var manifest = archive.Entries
                .Where(e => string.Equals(e.Name, ManifestName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName.Length).FirstOrDefault();
            if (manifest == null) return null;
            byte[] json = ReadBounded(manifest, MaxManifestBytes);
            if (json == null) return null;
            var theme = ParseManifest(json, fallbackName);
            if (theme == null) return null;

            // The picture: the entry the settings name, else the first picture in the package.
            ZipArchiveEntry picture = null;
            if (!string.IsNullOrEmpty(theme.PictureName))
                picture = archive.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), theme.PictureName.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                          ?? archive.Entries.FirstOrDefault(e => string.Equals(e.Name, System.IO.Path.GetFileName(theme.PictureName), StringComparison.OrdinalIgnoreCase));
            picture ??= archive.Entries.FirstOrDefault(e => IsPictureName(e.Name));
            if (picture != null)
            {
                theme.PictureBytes = ReadBounded(picture, MaxPictureBytes);
                theme.PictureName = picture.Name;
            }
            return theme;
        }

        private static bool IsPictureName(string name)
        {
            string ext = System.IO.Path.GetExtension(name);
            return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>An entry's bytes, or null when it is larger than the
        /// limit. The entry's stated length is not trusted: the bytes are
        /// counted as they are read.</summary>
        private static byte[] ReadBounded(ZipArchiveEntry entry, long limit)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > limit) return null;
            }
            return buffer.ToArray();
        }

        private static ThemeDefinition ParseManifest(byte[] json, string fallbackName)
        {
            if (json.Length > MaxManifestBytes) return null;
            // A byte order mark is not JSON, and Notepad writes one.
            int start = json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF ? 3 : 0;
            using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(json, start, json.Length - start),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var theme = new ThemeDefinition
            {
                Name = Text(root, "name", MaxName),
                Look = Text(root, "look", MaxLook),
                Author = Text(root, "author", MaxAuthor),
                Credits = Text(root, "credits", MaxCredits),
                PictureName = Text(root, "picture", 260),
            };
            if (string.IsNullOrEmpty(theme.Name)) theme.Name = fallbackName;
            if (string.IsNullOrEmpty(theme.Name)) return null;

            if (TryProperty(root, "base", out var baseValue))
            {
                if (baseValue.ValueKind != JsonValueKind.String) return null;
                string named = baseValue.GetString()?.Trim();
                if (string.Equals(named, "light", StringComparison.OrdinalIgnoreCase)) theme.Dark = false;
                else if (!string.Equals(named, "dark", StringComparison.OrdinalIgnoreCase)) return null;
            }

            if (TryProperty(root, "tile", out var tile) && tile.ValueKind == JsonValueKind.Number && tile.TryGetDouble(out double at)
                && !double.IsNaN(at))
                theme.TileAt = Math.Clamp(at, 0, 1 - ThemeDefinition.TileShare);

            if (TryProperty(root, "colors", out var colors))
            {
                if (colors.ValueKind != JsonValueKind.Object) return null;
                if (!Shade(colors, "background", out theme.Background) || !Shade(colors, "accent", out theme.Accent)
                    || !Shade(colors, "secondary", out theme.Secondary) || !Shade(colors, "attention", out theme.Attention))
                    return null;
            }
            return theme;
        }

        // A file written by hand may capitalize a name.
        private static bool TryProperty(JsonElement owner, string name, out JsonElement value)
        {
            foreach (var property in owner.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            value = default;
            return false;
        }

        private static string Text(JsonElement owner, string name, int max)
        {
            if (!TryProperty(owner, name, out var value) || value.ValueKind != JsonValueKind.String) return null;
            string text = value.GetString()?.Trim();
            if (string.IsNullOrEmpty(text)) return null;
            // One line, no control characters: the text goes into labels.
            var clean = new StringBuilder(text.Length);
            foreach (char c in text) clean.Append(char.IsControl(c) ? ' ' : c);
            text = clean.ToString().Trim();
            return text.Length > max ? text.Substring(0, max).TrimEnd() : text;
        }

        /// <summary>A color left out is fine. A color that is there and is
        /// no color makes the file no theme.</summary>
        private static bool Shade(JsonElement colors, string name, out Color? color)
        {
            color = null;
            if (!TryProperty(colors, name, out var value) || value.ValueKind == JsonValueKind.Null) return true;
            if (value.ValueKind != JsonValueKind.String || !ThemeColor.TryParse(value.GetString(), out Color parsed)) return false;
            color = parsed;
            return true;
        }

        /// <summary>The theme's settings as the file's JSON.</summary>
        internal static byte[] ManifestBytes(ThemeDefinition theme)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Indented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                writer.WriteStartObject();
                writer.WriteString("name", theme.Name ?? string.Empty);
                if (!string.IsNullOrEmpty(theme.Look)) writer.WriteString("look", theme.Look);
                if (!string.IsNullOrEmpty(theme.Author)) writer.WriteString("author", theme.Author);
                writer.WriteString("base", theme.Dark ? "dark" : "light");
                writer.WriteStartObject("colors");
                if (theme.Background.HasValue) writer.WriteString("background", ThemeColor.Format(theme.Background.Value));
                if (theme.Accent.HasValue) writer.WriteString("accent", ThemeColor.Format(theme.Accent.Value));
                if (theme.Secondary.HasValue) writer.WriteString("secondary", ThemeColor.Format(theme.Secondary.Value));
                if (theme.Attention.HasValue) writer.WriteString("attention", ThemeColor.Format(theme.Attention.Value));
                writer.WriteEndObject();
                if (theme.PictureBytes != null)
                {
                    writer.WriteString("picture", PictureEntryName(theme));
                    if (Math.Abs(theme.TileAt - ThemeDefinition.DefaultTileAt) > 0.0005) writer.WriteNumber("tile", Math.Round(theme.TileAt, 3));
                }
                if (!string.IsNullOrEmpty(theme.Credits)) writer.WriteString("credits", theme.Credits);
                writer.WriteEndObject();
            }
            return buffer.ToArray();
        }

        private static string PictureEntryName(ThemeDefinition theme)
        {
            if (!string.IsNullOrEmpty(theme.PictureName) && IsPictureName(theme.PictureName))
                return System.IO.Path.GetFileName(theme.PictureName);
            // PNG's first bytes, else JPEG.
            byte[] b = theme.PictureBytes;
            bool png = b != null && b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;
            return png ? "picture.png" : "picture.jpg";
        }

        /// <summary>The whole file: JSON alone, or a zip with the picture.</summary>
        internal static byte[] Serialize(ThemeDefinition theme)
        {
            byte[] manifest = ManifestBytes(theme);
            if (theme.PictureBytes == null) return manifest;
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var entry = archive.CreateEntry(ManifestName, CompressionLevel.Optimal).Open())
                    entry.Write(manifest, 0, manifest.Length);
                // A PNG or a JPEG is packed already.
                using (var entry = archive.CreateEntry(PictureEntryName(theme), CompressionLevel.NoCompression).Open())
                    entry.Write(theme.PictureBytes, 0, theme.PictureBytes.Length);
            }
            return buffer.ToArray();
        }

        /// <summary>Writes the theme to a file, whole or not at all.</summary>
        internal static void Write(string path, ThemeDefinition theme) => WriteBytes(path, Serialize(theme));

        /// <summary>Writes a theme file's bytes, whole or not at all: the
        /// bytes go to a file beside it first, which then takes its place.</summary>
        internal static void WriteBytes(string path, byte[] data)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                File.WriteAllBytes(temp, data);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
