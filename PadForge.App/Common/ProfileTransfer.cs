using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Services;

namespace PadForge.Common
{
    /// <summary>
    /// Shareable profile files (issue #83 follow-up). A profile file
    /// (conventional extension <c>.pfprofile</c>) is a zip containing
    /// <c>profile.xml</c> — one serialized <see cref="ProfileData"/>, the
    /// same unit the in-app profile system snapshots and applies — plus a
    /// <c>packages/</c> folder bundling every sound package the profile's
    /// macros reference. Import lands the bundled packages as ordinary
    /// .pfsounds files next to the exe (one file each, deduped by content
    /// length), registers them, and adds the profile to the registry; the
    /// existing activation machinery does the rest.
    /// </summary>
    public static class ProfileTransfer
    {
        public const string FileExtension = ".pfprofile";
        private const string ProfileEntry = "profile.xml";
        private const string PackagesPrefix = "packages/";

        /// <summary>Icon packs (#390) ride beside the sound packages under
        /// their own prefix, with their own alias map, the same landing
        /// rules on import.</summary>
        private const string IconsPrefix = "icons/";
        private const string IconAliasMapEntry = "icons/_aliases.txt";

        /// <summary>Maps each macro sound ref's package alias to the bundled
        /// file that supplies it, one <c>alias\tfilename</c> line per entry.
        ///
        /// <para>Why it exists: the alias a macro stores is this machine's
        /// REGISTERED name, which the registry dedup-renames on collision
        /// ("SFX" -> "SFX (2)"). The importing machine re-registers from the
        /// package's own probed name and only rewrites refs when ITS
        /// registration collided, so an alias that was suffixed on the
        /// exporting machine and not on the importing one resolved to nothing
        /// and the macro fell silent. Normalizing the refs to the probed name
        /// on export would fix that one case and break a worse one: two
        /// different packages that both probe "SFX" would collapse onto one
        /// ref. Keying the map by FILE keeps them distinct.</para>
        ///
        /// <para>Absent in archives written before this existed, which keep
        /// the previous behavior.</para></summary>
        private const string AliasMapEntry = "packages/_aliases.txt";

        private static readonly XmlSerializer Serializer = new(typeof(ProfileData));

        /// <summary>Writes <paramref name="profile"/> and its referenced
        /// sound packages to <paramref name="destPath"/>. Returns the
        /// bundled package names.</summary>
        public static List<string> Export(ProfileData profile, string destPath)
        {
            var bundled = new List<string>();

            // Build in a temp file and move into place, so a mid-export
            // failure (e.g. a locked package file) never leaves a truncated
            // .pfprofile at the destination.
            string tmpPath = destPath + ".tmp";
            try
            {
                using (var fs = File.Create(tmpPath))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    var entry = zip.CreateEntry(ProfileEntry);
                    using (var s = entry.Open())
                        Serializer.Serialize(s, profile);

                    var aliasMap = new List<string>();
                    foreach (string pkg in ReferencedPackages(profile))
                    {
                        string file = SoundPackageManager.ResolvePackageFile(pkg);
                        if (file == null || !File.Exists(file)) continue;
                        string entryFile = Path.GetFileName(file);
                        zip.CreateEntryFromFile(file, PackagesPrefix + entryFile, CompressionLevel.Optimal);
                        bundled.Add(pkg);
                        // Record which bundled file backs this alias so the
                        // importer can rewrite the ref to whatever name IT
                        // ends up registering the file under. A tab is safe:
                        // package names come from a manifest display name or a
                        // file stem, neither of which can contain one.
                        if (!pkg.Contains('\t') && !entryFile.Contains('\t'))
                            aliasMap.Add(pkg + "\t" + entryFile);
                    }
                    if (aliasMap.Count > 0)
                    {
                        var mapEntry = zip.CreateEntry(AliasMapEntry);
                        using var ms = new StreamWriter(mapEntry.Open());
                        foreach (var line in aliasMap) ms.WriteLine(line);
                    }

                    // #390: icon packs referenced by the profile's menu
                    // cells, same bundling and alias rules.
                    var iconAliasMap = new List<string>();
                    foreach (string pkg in ReferencedIconPackages(profile))
                    {
                        string file = IconPackageManager.ResolvePackageFile(pkg);
                        if (file == null || !File.Exists(file)) continue;
                        string entryFile = Path.GetFileName(file);
                        zip.CreateEntryFromFile(file, IconsPrefix + entryFile, CompressionLevel.Optimal);
                        bundled.Add(pkg);
                        if (!pkg.Contains('	') && !entryFile.Contains('	'))
                            iconAliasMap.Add(pkg + "	" + entryFile);
                    }
                    if (iconAliasMap.Count > 0)
                    {
                        var mapEntry = zip.CreateEntry(IconAliasMapEntry);
                        using var ms = new StreamWriter(mapEntry.Open());
                        foreach (var line in iconAliasMap) ms.WriteLine(line);
                    }
                }
                File.Move(tmpPath, destPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmpPath); } catch { }
                throw;
            }
            return bundled;
        }

        /// <summary>Reads a profile file: lands bundled packages next to
        /// the exe (skipping byte-identical ones already there), registers
        /// them, and returns the deserialized profile with a fresh Id.
        /// Returns null when the file isn't a readable profile.</summary>
        public static ProfileData Import(string srcPath, out List<string> registeredPackages)
        {
            registeredPackages = new List<string>();
            try
            {
                using var zip = ZipFile.OpenRead(srcPath);
                var pe = zip.GetEntry(ProfileEntry);
                if (pe == null) return null;

                ProfileData profile;
                using (var s = pe.Open())
                    profile = (ProfileData)Serializer.Deserialize(s);
                if (profile == null) return null;
                // Profiles exported before the v4.x schema rename carry the
                // per-(slot, device) configs under the legacy element name.
                profile.MigrateLegacySchema();
                profile.Id = Guid.NewGuid().ToString("N");

                // alias -> bundled file, written by Export. Absent on older
                // archives, which then keep the probed-name-only behavior.
                var aliasesByFile = ReadAliasMap(zip);

                // Actions already rewritten by THIS import. RewritePackageRefs
                // matches on a ref's CURRENT value, and the loop below rewrites
                // in place once per bundled entry, so an alias belonging to a
                // LATER entry could re-match refs an earlier entry had already
                // pointed at its final name. Registration dedups by appending
                // " (n)", which is exactly the shape that collides. Marking each
                // action stops the cascade without reordering the loop.
                var rewrittenActions = new HashSet<object>(
                    System.Collections.Generic.ReferenceEqualityComparer.Instance);

                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var e in zip.Entries.Where(x =>
                             x.FullName.StartsWith(PackagesPrefix, StringComparison.OrdinalIgnoreCase)
                             && x.Name.EndsWith(SoundPackageManager.FileExtension, StringComparison.OrdinalIgnoreCase)))
                {
                    // Entry names come from the archive: strip every path
                    // component (both separator styles); the shared landing
                    // helper bounds and copies (ZipSlip, byte-identical
                    // reuse, dedup rename, capped copy).
                    string slashed = e.Name.Replace('\\', '/');
                    string fileName = slashed.Substring(slashed.LastIndexOf('/') + 1);
                    if (string.IsNullOrWhiteSpace(fileName)) continue;
                    string target = TryLandEntry(e, appDir, fileName, SoundPackageManager.FileExtension);
                    if (target == null) continue; // skip this package, keep importing the profile

                    string name = SoundPackageManager.Register(target, out string probedName);
                    if (name == null) continue;
                    registeredPackages.Add(name);

                    // Rewrite the profile's refs onto whatever name this
                    // machine registered the file under.
                    //
                    // The alias map is authoritative when present: it says
                    // exactly which ref the EXPORTING machine used for THIS
                    // file, including a dedup-suffixed alias ("SFX (2)") that
                    // the probed name alone can never reconstruct. That case
                    // used to leave the macro pointing at a package name no
                    // machine had, and the sound silently never played.
                    bool rewrote = false;
                    if (aliasesByFile.TryGetValue(fileName, out var aliases))
                    {
                        foreach (var alias in aliases)
                        {
                            if (string.Equals(alias, name, StringComparison.OrdinalIgnoreCase)) { rewrote = true; continue; }
                            RewritePackageRefs(profile, alias, name, rewrittenActions);
                            rewrote = true;
                        }
                    }
                    // Older archives carry no map: fall back to the probed
                    // name, which is what those refs were written against.
                    if (!rewrote && !string.Equals(name, probedName, StringComparison.OrdinalIgnoreCase))
                        RewritePackageRefs(profile, probedName, name, rewrittenActions);
                }

                // #390: icon packs, the identical landing and rewrite
                // discipline against the menus' pficon:// references.
                var iconAliasesByFile = ReadAliasMap(zip, IconAliasMapEntry);
                var rewrittenItems = new HashSet<object>(
                    System.Collections.Generic.ReferenceEqualityComparer.Instance);
                foreach (var e in zip.Entries.Where(x =>
                             x.FullName.StartsWith(IconsPrefix, StringComparison.OrdinalIgnoreCase)
                             && x.Name.EndsWith(IconPackageManager.FileExtension, StringComparison.OrdinalIgnoreCase)))
                {
                    string slashed = e.Name.Replace('\\', '/');
                    string fileName = slashed.Substring(slashed.LastIndexOf('/') + 1);
                    if (string.IsNullOrWhiteSpace(fileName)) continue;
                    string target = TryLandEntry(e, appDir, fileName, IconPackageManager.FileExtension);
                    if (target == null) continue;

                    string name = IconPackageManager.Register(target, out string probedName);
                    if (name == null) continue;
                    registeredPackages.Add(name);

                    bool rewrote = false;
                    if (iconAliasesByFile.TryGetValue(fileName, out var aliases))
                    {
                        foreach (var alias in aliases)
                        {
                            if (string.Equals(alias, name, StringComparison.OrdinalIgnoreCase)) { rewrote = true; continue; }
                            RewriteIconPackageRefs(profile, alias, name, rewrittenItems);
                            rewrote = true;
                        }
                    }
                    if (!rewrote && !string.Equals(name, probedName, StringComparison.OrdinalIgnoreCase))
                        RewriteIconPackageRefs(profile, probedName, name, rewrittenItems);
                }
                return profile;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Reads the alias map written by Export, keyed by bundled
        /// file name (one file can back several aliases). Returns empty for
        /// archives that predate the map, or a malformed one: an unreadable
        /// map must degrade to the old probed-name path, never fail the
        /// import.</summary>
        private static Dictionary<string, List<string>> ReadAliasMap(ZipArchive zip, string mapEntryName = AliasMapEntry)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var e = zip.GetEntry(mapEntryName);
                if (e == null) return map;
                using var r = new StreamReader(e.Open());
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0 || tab == line.Length - 1) continue;
                    string alias = line.Substring(0, tab);
                    string file = line.Substring(tab + 1).Trim();
                    if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(file)) continue;
                    if (!map.TryGetValue(file, out var list))
                        map[file] = list = new List<string>();
                    if (!list.Contains(alias, StringComparer.OrdinalIgnoreCase))
                        list.Add(alias);
                }
            }
            catch { /* malformed map: fall back to probed-name rewriting */ }
            return map;
        }

        /// <summary>True when the archive entry and the on-disk file are
        /// byte-identical. Length is only the cheap pre-filter: two different
        /// packages authored by the same tool routinely share a name AND a
        /// size, and treating that as identity made an import silently reuse
        /// the resident package's audio instead of the bundled one. Streamed
        /// and length-capped so a hostile entry cannot be read unbounded.</summary>
        private static bool EntryMatchesFile(ZipArchiveEntry e, string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Length != e.Length) return false;

                using var a = e.Open();
                using var b = File.OpenRead(path);
                var ba = new byte[81920];
                var bb = new byte[81920];
                long remaining = fi.Length;
                while (remaining > 0)
                {
                    int want = (int)Math.Min(ba.Length, remaining);
                    int ra = ReadFully(a, ba, want);
                    int rb = ReadFully(b, bb, want);
                    if (ra != want || rb != want) return false;
                    if (!ba.AsSpan(0, want).SequenceEqual(bb.AsSpan(0, want))) return false;
                    remaining -= want;
                }
                return true;
            }
            catch
            {
                // Unreadable either side: fall through to the copy path, which
                // dedup-renames rather than silently reusing a file we could
                // not verify.
                return false;
            }
        }

        private static int ReadFully(Stream s, byte[] buf, int count)
        {
            int total = 0;
            while (total < count)
            {
                int got = s.Read(buf, total, count - total);
                if (got <= 0) break;
                total += got;
            }
            return total;
        }

        /// <summary>Rewrites every macro sound ref pointing at
        /// <paramref name="fromPackage"/> to <paramref name="toPackage"/>.</summary>
        /// <param name="alreadyRewritten">Actions this import has already
        /// repointed. Null means no tracking, for callers outside the import
        /// loop. An action is rewritten at most once per import: without that,
        /// a later entry whose alias equals an earlier entry's FINAL name
        /// re-matched refs that were already correct and moved them again.</param>
        private static void RewritePackageRefs(ProfileData profile, string fromPackage, string toPackage,
            HashSet<object> alreadyRewritten = null)
        {
            if (profile.Macros == null) return;
            foreach (var m in profile.Macros)
            {
                if (m?.Actions == null) continue;
                foreach (var a in m.Actions)
                {
                    if (a == null) continue;
                    if (alreadyRewritten != null && alreadyRewritten.Contains(a)) continue;
                    if (SoundPackageManager.TryParseRef(a.SoundFilePath, out string pkg, out string entry)
                        && string.Equals(pkg, fromPackage, StringComparison.OrdinalIgnoreCase))
                    {
                        a.SoundFilePath = SoundPackageManager.MakeRef(toPackage, entry);
                        alreadyRewritten?.Add(a);
                    }
                }
            }
        }

        /// <summary>Lands one bundled package entry beside the exe with
        /// the audit-hardened rules shared by sound packages and icon
        /// packs: the ZipSlip bound, byte-identical reuse (full content
        /// compare, never name plus size), the dedup-suffix rename, and
        /// a bounded copy that deletes the partial file on overrun.
        /// Returns the landed path, or null when the entry was
        /// skipped.</summary>
        private static string TryLandEntry(ZipArchiveEntry e, string appDir, string fileName, string fileExtension)
        {
            string target = Path.GetFullPath(Path.Combine(appDir, fileName));
            if (!target.StartsWith(Path.GetFullPath(appDir), StringComparison.OrdinalIgnoreCase))
                return null;

            if (File.Exists(target) && EntryMatchesFile(e, target))
                return target;

            if (File.Exists(target))
            {
                string stem = Path.GetFileNameWithoutExtension(fileName);
                int n = 2;
                do
                {
                    target = Path.Combine(appDir, $"{stem} ({n++}){fileExtension}");
                } while (File.Exists(target));
            }
            // Bounded copy instead of ExtractToFile: the entry's DECLARED
            // length is attacker metadata and the real stream is
            // unbounded, so a highly-compressed entry could fill the disk
            // (zip bomb). Cap the bytes actually written and delete the
            // partial file on any failure or overrun.
            try
            {
                const long MaxPackageBytes = 512L * 1024 * 1024;
                using var src = e.Open();
                using var dst = File.Create(target);
                var chunk = new byte[81920];
                long total = 0;
                int got;
                while ((got = src.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += got;
                    if (total > MaxPackageBytes)
                        throw new IOException("bundled package exceeds the size cap");
                    dst.Write(chunk, 0, got);
                }
            }
            catch
            {
                try { File.Delete(target); } catch { }
                return null;
            }
            return target;
        }

        /// <summary>Rewrites every menu cell icon ref pointing at
        /// <paramref name="fromPackage"/> to <paramref name="toPackage"/>
        /// (#390), the sound rewrite's discipline: each item moves at
        /// most once per import.</summary>
        private static void RewriteIconPackageRefs(ProfileData profile, string fromPackage, string toPackage,
            HashSet<object> alreadyRewritten = null)
        {
            if (profile.SlotMappingSets == null) return;
            foreach (var ms in profile.SlotMappingSets)
            {
                if (ms?.Menus == null) continue;
                foreach (var def in ms.Menus)
                {
                    if (def?.Items == null) continue;
                    foreach (var it in def.Items)
                    {
                        if (it == null) continue;
                        if (alreadyRewritten != null && alreadyRewritten.Contains(it)) continue;
                        if (IconPackageManager.TryParseRef(it.Icon, out string pkg, out string entry)
                            && string.Equals(pkg, fromPackage, StringComparison.OrdinalIgnoreCase))
                        {
                            it.Icon = IconPackageManager.MakeRef(toPackage, entry);
                            alreadyRewritten?.Add(it);
                        }
                    }
                }
            }
        }

        /// <summary>Distinct icon-pack names referenced by the profile's
        /// menu cells (#390).</summary>
        private static IEnumerable<string> ReferencedIconPackages(ProfileData profile)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (profile.SlotMappingSets == null) return names;
            foreach (var ms in profile.SlotMappingSets)
            {
                if (ms?.Menus == null) continue;
                foreach (var def in ms.Menus)
                {
                    if (def?.Items == null) continue;
                    foreach (var it in def.Items)
                        if (it != null && IconPackageManager.TryParseRef(it.Icon, out string pkg, out _))
                            names.Add(pkg);
                }
            }
            return names;
        }

        /// <summary>Distinct sound-package names referenced by the
        /// profile's macros.</summary>
        private static IEnumerable<string> ReferencedPackages(ProfileData profile)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (profile.Macros == null) return names;
            foreach (var m in profile.Macros)
            {
                if (m?.Actions == null) continue;
                foreach (var a in m.Actions)
                    if (SoundPackageManager.TryParseRef(a?.SoundFilePath, out string pkg, out _))
                        names.Add(pkg);
            }
            return names;
        }
    }
}
