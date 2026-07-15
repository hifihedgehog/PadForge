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

                    foreach (string pkg in ReferencedPackages(profile))
                    {
                        string file = SoundPackageManager.ResolvePackageFile(pkg);
                        if (file == null || !File.Exists(file)) continue;
                        zip.CreateEntryFromFile(file, PackagesPrefix + Path.GetFileName(file), CompressionLevel.Optimal);
                        bundled.Add(pkg);
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

                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var e in zip.Entries.Where(x =>
                             x.FullName.StartsWith(PackagesPrefix, StringComparison.OrdinalIgnoreCase)
                             && x.Name.EndsWith(SoundPackageManager.FileExtension, StringComparison.OrdinalIgnoreCase)))
                {
                    // Entry names come from the archive: strip every path
                    // component (both separator styles) and bound the result
                    // to the app directory, so a crafted archive can't write
                    // outside it (ZipSlip).
                    string slashed = e.Name.Replace('\\', '/');
                    string fileName = slashed.Substring(slashed.LastIndexOf('/') + 1);
                    if (string.IsNullOrWhiteSpace(fileName)) continue;
                    string target = Path.GetFullPath(Path.Combine(appDir, fileName));
                    if (!target.StartsWith(Path.GetFullPath(appDir), StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (File.Exists(target) && EntryMatchesFile(e, target))
                    {
                        // Byte-identical package already here: reuse it.
                        // Name + length alone is NOT identity. Two different
                        // packages that happen to share a name and a size
                        // (same tool, same layout, different audio) silently
                        // resolved to whichever landed first, so the imported
                        // macro played the wrong sounds. Compare content.
                    }
                    else
                    {
                        if (File.Exists(target))
                        {
                            string stem = Path.GetFileNameWithoutExtension(fileName);
                            int n = 2;
                            do
                            {
                                target = Path.Combine(appDir, $"{stem} ({n++}){SoundPackageManager.FileExtension}");
                            } while (File.Exists(target));
                        }
                        // Bounded copy instead of ExtractToFile: the entry's
                        // DECLARED length is attacker metadata and the real
                        // stream is unbounded, so a highly-compressed entry
                        // could fill the disk (zip bomb). Cap the bytes
                        // actually written and delete the partial file on
                        // any failure or overrun.
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
                            continue; // skip this package, keep importing the profile
                        }
                    }

                    string name = SoundPackageManager.Register(target, out string probedName);
                    if (name == null) continue;
                    registeredPackages.Add(name);

                    // A name collision on this machine dedup-renamed the
                    // package; the profile's macro refs still carry the
                    // package's own name. Rewrite them to the registered
                    // name so the sounds resolve to the bundled package.
                    if (!string.Equals(name, probedName, StringComparison.OrdinalIgnoreCase))
                        RewritePackageRefs(profile, probedName, name);
                }
                return profile;
            }
            catch
            {
                return null;
            }
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
        private static void RewritePackageRefs(ProfileData profile, string fromPackage, string toPackage)
        {
            if (profile.Macros == null) return;
            foreach (var m in profile.Macros)
            {
                if (m?.Actions == null) continue;
                foreach (var a in m.Actions)
                {
                    if (a == null) continue;
                    if (SoundPackageManager.TryParseRef(a.SoundFilePath, out string pkg, out string entry)
                        && string.Equals(pkg, fromPackage, StringComparison.OrdinalIgnoreCase))
                        a.SoundFilePath = SoundPackageManager.MakeRef(toPackage, entry);
                }
            }
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
