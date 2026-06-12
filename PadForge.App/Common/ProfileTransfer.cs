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
            using var fs = File.Create(destPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

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
                profile.Id = Guid.NewGuid().ToString("N");

                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var e in zip.Entries.Where(x =>
                             x.FullName.StartsWith(PackagesPrefix, StringComparison.OrdinalIgnoreCase)
                             && x.Name.EndsWith(SoundPackageManager.FileExtension, StringComparison.OrdinalIgnoreCase)))
                {
                    string target = Path.Combine(appDir, e.Name);
                    if (File.Exists(target) && new FileInfo(target).Length == e.Length)
                    {
                        // Same name + size: assume the same package and reuse.
                    }
                    else
                    {
                        if (File.Exists(target))
                        {
                            string stem = Path.GetFileNameWithoutExtension(e.Name);
                            int n = 2;
                            do
                            {
                                target = Path.Combine(appDir, $"{stem} ({n++}){SoundPackageManager.FileExtension}");
                            } while (File.Exists(target));
                        }
                        e.ExtractToFile(target);
                    }
                    string name = SoundPackageManager.Register(target);
                    if (name != null) registeredPackages.Add(name);
                }
                return profile;
            }
            catch
            {
                return null;
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
