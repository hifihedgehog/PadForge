using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PadForge.Common
{
    /// <summary>
    /// Registry and reader for menu icon packs (#390), the
    /// <see cref="SoundPackageManager"/> shape leg for leg.
    ///
    /// An icon pack is ONE file: a zip (conventional extension
    /// <c>.pficons</c>) containing image entries and, optionally, a
    /// <c>manifest.json</c> with a display name. PadForge never extracts
    /// a pack: icons are read straight out of the zip at display time,
    /// so importing a pack means registering its path, not copying
    /// anything. The app itself stays exactly two files; packs are user
    /// content living wherever the user keeps them.
    ///
    /// Portability: paths under the application directory are stored
    /// relative, so an exe + xml + *.pficons set travels as a flat
    /// directory and resolves anywhere.
    ///
    /// Menu cells reference pack icons as
    /// <c>pficon://PackageName/entryName.ext</c>, alongside loose image
    /// paths and Steam binding-icon names, which keep working unchanged.
    /// </summary>
    public static class IconPackageManager
    {
        public const string Scheme = "pficon://";
        public const string FileExtension = ".pficons";

        /// <summary>Image formats WPF's stock decoder handles. One list
        /// for the pack probe, the entry lister, and the editor's loose
        /// file gate, so every surface agrees on what an icon is.</summary>
        public static readonly string[] ImageExtensions =
            { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

        public sealed class PackageRef
        {
            // Properties (not fields) so WPF bindings can read them.
            public string Name { get; set; }
            /// <summary>Stored path: relative to the exe directory when
            /// the pack lives under it, absolute otherwise.</summary>
            public string Path { get; set; }
        }

        private static readonly object _lock = new();
        private static readonly List<PackageRef> _packages = new();

        /// <summary>Raised when the registry changes (UI refresh, and the
        /// icon resolver's cache invalidation).</summary>
        public static event EventHandler RegistryChanged;

        // ─────────────────────────────────────────────
        //  Registry
        // ─────────────────────────────────────────────

        public static IReadOnlyList<PackageRef> Packages
        {
            get { lock (_lock) return _packages.ToList(); }
        }

        /// <summary>Replaces the registry from persisted settings.</summary>
        public static void LoadRegistry(IEnumerable<(string Name, string Path)> entries)
        {
            lock (_lock)
            {
                _packages.Clear();
                if (entries != null)
                    foreach (var (name, path) in entries)
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path))
                            _packages.Add(new PackageRef { Name = name, Path = path });
            }
            RegistryChanged?.Invoke(null, EventArgs.Empty);
        }

        public static List<(string Name, string Path)> SaveRegistry()
        {
            lock (_lock) return _packages.Select(p => (p.Name, p.Path)).ToList();
        }

        /// <summary>Registers a pack file. Returns the registered name
        /// (deduped against existing names) or null when the file isn't a
        /// readable pack with at least one image entry.</summary>
        public static string Register(string filePath) => Register(filePath, out _);

        /// <summary>Registers a pack file and also reports the pack's own
        /// (probed) name, which can differ from the registered name when
        /// a name collision forced a dedup suffix. Callers holding
        /// references built against the probed name rewrite them to the
        /// returned name.</summary>
        public static string Register(string filePath, out string probedName)
        {
            probedName = ProbePackageName(filePath);
            if (probedName == null) return null;

            string name = probedName;
            string stored = MakeStoredPath(filePath);
            lock (_lock)
            {
                // Re-registering the same file refreshes its entry. The
                // refreshed name still dedups against OTHER entries, since
                // a re-register could otherwise hand two packs the same
                // display name and make name lookups ambiguous.
                var existing = _packages.FirstOrDefault(p =>
                    string.Equals(ResolvePath(p.Path), System.IO.Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));
                string final = name;
                int n = 2;
                while (_packages.Any(p => !ReferenceEquals(p, existing)
                        && string.Equals(p.Name, final, StringComparison.OrdinalIgnoreCase)))
                    final = $"{name} ({n++})";
                name = final;

                if (existing != null)
                {
                    existing.Name = name;
                    existing.Path = stored;
                }
                else
                {
                    _packages.Add(new PackageRef { Name = name, Path = stored });
                }
            }
            RegistryChanged?.Invoke(null, EventArgs.Empty);
            return name;
        }

        public static void Unregister(string name)
        {
            lock (_lock)
                _packages.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            RegistryChanged?.Invoke(null, EventArgs.Empty);
        }

        public static string ResolvePackageFile(string name)
        {
            lock (_lock)
            {
                var p = _packages.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                return p == null ? null : ResolvePath(p.Path);
            }
        }

        // ─────────────────────────────────────────────
        //  Icon references
        // ─────────────────────────────────────────────

        public static bool IsPackageRef(string iconRef) =>
            iconRef != null && iconRef.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

        public static string MakeRef(string packageName, string entryName) =>
            $"{Scheme}{packageName}/{entryName}";

        public static bool TryParseRef(string iconRef, out string packageName, out string entryName)
        {
            packageName = null; entryName = null;
            if (!IsPackageRef(iconRef)) return false;
            string rest = iconRef.Substring(Scheme.Length);
            int slash = rest.IndexOf('/');
            if (slash <= 0 || slash == rest.Length - 1) return false;
            packageName = rest.Substring(0, slash);
            entryName = rest.Substring(slash + 1);
            return true;
        }

        /// <summary>Human-readable display for a pack icon reference:
        /// "entry, then pack", the sound picker separator.</summary>
        public static string DisplayName(string iconRef) =>
            TryParseRef(iconRef, out string pkg, out string entry) ? $"{entry} — {pkg}" : iconRef;

        /// <summary>Reads a pack icon's bytes, or null (pack missing /
        /// entry missing / unreadable).</summary>
        public static byte[] TryReadIcon(string iconRef)
        {
            if (!TryParseRef(iconRef, out string pkg, out string entry)) return null;
            string file = ResolvePackageFile(pkg);
            if (file == null || !File.Exists(file)) return null;
            try
            {
                using var zip = ZipFile.OpenRead(file);
                var e = zip.Entries.FirstOrDefault(x =>
                    string.Equals(x.FullName, entry, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.Name, entry, StringComparison.OrdinalIgnoreCase));
                if (e == null) return null;
                // Pre-size from the DECLARED length only up to a sane icon
                // size, and bound the actual copy: both numbers are archive
                // metadata a crafted pack controls (the sound layer's
                // audit G2 bound, sized for images).
                const long MaxIconBytes = 16L * 1024 * 1024;
                using var ms = new MemoryStream((int)Math.Clamp(e.Length, 0, 1024 * 1024));
                using (var s = e.Open())
                {
                    var chunk = new byte[81920];
                    long total = 0;
                    int got;
                    while ((got = s.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        total += got;
                        if (total > MaxIconBytes) return null;
                        ms.Write(chunk, 0, got);
                    }
                }
                return ms.ToArray();
            }
            catch { return null; }
        }

        /// <summary>Lists the image entry names inside a registered pack
        /// (empty when missing/unreadable).</summary>
        public static List<string> ListIcons(string packageName)
        {
            var result = new List<string>();
            string file = ResolvePackageFile(packageName);
            if (file == null) return result;
            result.AddRange(ListIconsInFile(file));
            return result;
        }

        /// <summary>Lists image entries inside any pack file on disk.</summary>
        public static List<string> ListIconsInFile(string filePath)
        {
            var result = new List<string>();
            try
            {
                using var zip = ZipFile.OpenRead(filePath);
                foreach (var e in zip.Entries)
                    if (ImageExtensions.Contains(System.IO.Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase))
                        result.Add(e.FullName);
            }
            catch { }
            return result;
        }

        // ─────────────────────────────────────────────
        //  Import / export
        // ─────────────────────────────────────────────

        /// <summary>Builds a pack file from loose image files. Entry
        /// names are the source file names (same-named files from
        /// different folders get a " (2)" suffix).</summary>
        public static bool ExportPackage(string destFilePath, string displayName, IEnumerable<string> imageFiles)
        {
            // Build in a temp file and move into place, so a mid-write
            // failure never leaves a truncated pack at the destination.
            string tmpPath = destFilePath + ".tmp";
            try
            {
                using (var fs = File.Create(tmpPath))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    var manifest = zip.CreateEntry("manifest.json");
                    using (var w = new StreamWriter(manifest.Open()))
                        w.Write("{\"name\":\"" + (displayName ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
                                + "\",\"generator\":\"PadForge\"}");

                    // ZipArchive happily writes duplicate entry names and the
                    // readers resolve first-match, so dedup here.
                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var f in imageFiles.Where(File.Exists))
                    {
                        string entryName = System.IO.Path.GetFileName(f);
                        string stem = System.IO.Path.GetFileNameWithoutExtension(entryName);
                        string ext = System.IO.Path.GetExtension(entryName);
                        int n = 2;
                        while (!used.Add(entryName))
                            entryName = $"{stem} ({n++}){ext}";
                        zip.CreateEntryFromFile(f, entryName, CompressionLevel.Optimal);
                    }
                }
                File.Move(tmpPath, destFilePath, overwrite: true);
                return true;
            }
            catch
            {
                try { File.Delete(tmpPath); } catch { }
                return false;
            }
        }

        /// <summary>The pack's manifest entry: any entry NAMED
        /// manifest.json, compared case-insensitively, the shallowest
        /// (shortest full path) when several exist. Null when none.</summary>
        private static ZipArchiveEntry FindManifestEntry(ZipArchive zip)
        {
            ZipArchiveEntry best = null;
            foreach (var e in zip.Entries)
            {
                if (!string.Equals(e.Name, "manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || e.FullName.Length < best.FullName.Length) best = e;
            }
            return best;
        }

        /// <summary>Display name from manifest.json when present, else the
        /// file name; null when the zip has no image entries.</summary>
        private static string ProbePackageName(string filePath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(filePath);
                bool hasImage = zip.Entries.Any(e =>
                    ImageExtensions.Contains(System.IO.Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase));
                if (!hasImage) return null;

                // GetEntry is an ordinal, root-only lookup: a pack zipped
                // from a folder ("Pack/manifest.json") or authored on a
                // case-preserving tool ("Manifest.json") never found its
                // manifest and registered under the file name. Match the
                // entry NAME case-insensitively anywhere in the zip and
                // prefer the shallowest one, so a root manifest still
                // wins over a nested sample's.
                var man = FindManifestEntry(zip);
                if (man != null)
                {
                    // A manifest only carries a name: cap the read so a
                    // crafted pack whose manifest decompresses to
                    // gigabytes can't force the allocation.
                    const int MaxManifestChars = 64 * 1024;
                    using var r = new StreamReader(man.Open());
                    var chars = new char[MaxManifestChars];
                    int read = r.ReadBlock(chars, 0, MaxManifestChars);
                    string json = new string(chars, 0, read);
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("name", out var nameProp)
                            && nameProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            // Slashes would break the pficon://Package/entry
                            // ref grammar (the first '/' is the delimiter).
                            string n = nameProp.GetString()
                                .Replace("/", " ").Replace("\\", " ").Trim();
                            if (!string.IsNullOrWhiteSpace(n))
                                return n;
                        }
                    }
                    catch (System.Text.Json.JsonException) { /* malformed manifest: fall back to file name */ }
                }
                return System.IO.Path.GetFileNameWithoutExtension(filePath);
            }
            catch { return null; }
        }

        // ─────────────────────────────────────────────
        //  Path portability
        // ─────────────────────────────────────────────

        private static string AppDir => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

        /// <summary>Relative when under the exe directory (portable kit),
        /// absolute otherwise. Public: the editor's loose-image pick
        /// stores through the same rule.</summary>
        public static string MakeStoredPath(string filePath)
        {
            string full = System.IO.Path.GetFullPath(filePath);
            string app = AppDir + System.IO.Path.DirectorySeparatorChar;
            return full.StartsWith(app, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(app.Length)
                : full;
        }

        public static string ResolvePath(string storedPath)
        {
            if (string.IsNullOrEmpty(storedPath)) return storedPath;
            return System.IO.Path.IsPathRooted(storedPath)
                ? storedPath
                : System.IO.Path.Combine(AppDir, storedPath);
        }
    }
}
