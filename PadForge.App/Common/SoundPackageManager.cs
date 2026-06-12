using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PadForge.Common
{
    /// <summary>
    /// Registry and reader for sound packages (issue #83 follow-up).
    ///
    /// A sound package is ONE file — a zip (conventional extension
    /// <c>.pfsounds</c>) containing audio entries and, optionally, a
    /// <c>manifest.json</c> with a display name and credits. PadForge
    /// never extracts a package: sounds are read straight out of the zip
    /// at decode time, so importing a package means registering its path,
    /// not copying anything. The app itself stays exactly two files;
    /// packages are user content living wherever the user keeps them.
    ///
    /// Portability: paths under the application directory are stored
    /// relative, so an exe + xml + *.pfsounds set travels as a flat
    /// directory (USB stick, cloud folder) and resolves anywhere.
    ///
    /// Macros reference package sounds as
    /// <c>pfsound://PackageName/entryName.ext</c> — alongside ordinary
    /// absolute file paths, which keep working unchanged.
    /// </summary>
    public static class SoundPackageManager
    {
        public const string Scheme = "pfsound://";
        public const string FileExtension = ".pfsounds";

        private static readonly string[] AudioExtensions =
            { ".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac", ".ogg" };

        public sealed class PackageRef
        {
            // Properties (not fields) so WPF bindings can read them.
            public string Name { get; set; }
            /// <summary>Stored path — relative to the exe directory when
            /// the package lives under it, absolute otherwise.</summary>
            public string Path { get; set; }
        }

        private static readonly object _lock = new();
        private static readonly List<PackageRef> _packages = new();

        /// <summary>Raised when the registry changes (UI refresh).</summary>
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

        /// <summary>Registers a package file. Returns the registered name
        /// (deduped against existing names) or null when the file isn't a
        /// readable package with at least one audio entry.</summary>
        public static string Register(string filePath) => Register(filePath, out _);

        /// <summary>Registers a package file and also reports the package's
        /// own (probed) name, which can differ from the registered name when
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
                // refreshed name still dedups against OTHER entries —
                // otherwise a re-register could hand two packages the same
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
        //  Sound references
        // ─────────────────────────────────────────────

        public static bool IsPackageRef(string soundRef) =>
            soundRef != null && soundRef.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

        public static string MakeRef(string packageName, string entryName) =>
            $"{Scheme}{packageName}/{entryName}";

        public static bool TryParseRef(string soundRef, out string packageName, out string entryName)
        {
            packageName = null; entryName = null;
            if (!IsPackageRef(soundRef)) return false;
            string rest = soundRef.Substring(Scheme.Length);
            int slash = rest.IndexOf('/');
            if (slash <= 0 || slash == rest.Length - 1) return false;
            packageName = rest.Substring(0, slash);
            entryName = rest.Substring(slash + 1);
            return true;
        }

        /// <summary>Human-readable display for a package sound reference:
        /// "entry — package".</summary>
        public static string DisplayName(string soundRef) =>
            TryParseRef(soundRef, out string pkg, out string entry) ? $"{entry} — {pkg}" : soundRef;

        /// <summary>Reads a package sound's bytes, or null (package
        /// missing / entry missing / unreadable).</summary>
        public static byte[] TryReadSound(string soundRef)
        {
            if (!TryParseRef(soundRef, out string pkg, out string entry)) return null;
            string file = ResolvePackageFile(pkg);
            if (file == null || !File.Exists(file)) return null;
            try
            {
                using var zip = ZipFile.OpenRead(file);
                var e = zip.Entries.FirstOrDefault(x =>
                    string.Equals(x.FullName, entry, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.Name, entry, StringComparison.OrdinalIgnoreCase));
                if (e == null) return null;
                using var ms = new MemoryStream((int)Math.Min(e.Length, int.MaxValue));
                using (var s = e.Open()) s.CopyTo(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }

        /// <summary>Lists the audio entry names inside a registered
        /// package (empty when missing/unreadable).</summary>
        public static List<string> ListSounds(string packageName)
        {
            var result = new List<string>();
            string file = ResolvePackageFile(packageName);
            if (file == null) return result;
            result.AddRange(ListSoundsInFile(file));
            return result;
        }

        /// <summary>Lists audio entries inside any package file on disk.</summary>
        public static List<string> ListSoundsInFile(string filePath)
        {
            var result = new List<string>();
            try
            {
                using var zip = ZipFile.OpenRead(filePath);
                foreach (var e in zip.Entries)
                    if (AudioExtensions.Contains(System.IO.Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase))
                        result.Add(e.FullName);
            }
            catch { }
            return result;
        }

        // ─────────────────────────────────────────────
        //  Import / export
        // ─────────────────────────────────────────────

        /// <summary>Builds a package file from loose sound files. Entry
        /// names are the source file names.</summary>
        public static bool ExportPackage(string destFilePath, string displayName, IEnumerable<string> soundFiles)
        {
            try
            {
                using var fs = File.Create(destFilePath);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                var manifest = zip.CreateEntry("manifest.json");
                using (var w = new StreamWriter(manifest.Open()))
                    w.Write("{\"name\":\"" + (displayName ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
                            + "\",\"generator\":\"PadForge\"}");
                foreach (var f in soundFiles.Where(File.Exists))
                    zip.CreateEntryFromFile(f, System.IO.Path.GetFileName(f), CompressionLevel.Optimal);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Display name from manifest.json when present, else the
        /// file name; null when the zip has no audio entries.</summary>
        private static string ProbePackageName(string filePath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(filePath);
                bool hasAudio = zip.Entries.Any(e =>
                    AudioExtensions.Contains(System.IO.Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase));
                if (!hasAudio) return null;

                var man = zip.GetEntry("manifest.json");
                if (man != null)
                {
                    using var r = new StreamReader(man.Open());
                    string json = r.ReadToEnd();
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("name", out var nameProp)
                            && nameProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            // Slashes would break the pfsound://Package/entry
                            // ref grammar (the first '/' is the delimiter).
                            string n = nameProp.GetString()
                                .Replace("/", " ").Replace("\\", " ").Trim();
                            if (!string.IsNullOrWhiteSpace(n))
                                return n;
                        }
                    }
                    catch (System.Text.Json.JsonException) { /* malformed manifest — fall back to file name */ }
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
        /// absolute otherwise.</summary>
        private static string MakeStoredPath(string filePath)
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
