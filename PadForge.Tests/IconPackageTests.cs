using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Icon packs (#390): the manager (the SoundPackageManager shape),
    /// the resolver's pack and loose-path reference forms, and the
    /// profile-transfer bundling. These also establish the package-layer
    /// test harness the sound twin never had: every test builds real
    /// .pficons zips in a temp directory and reads them back through the
    /// production code.
    /// </summary>
    [Collection("IconPackageRegistry")]
    public class IconPackageTests : IDisposable
    {
        private readonly string _dir;

        public IconPackageTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "pficons-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            IconPackageManager.LoadRegistry(null); // clean registry per test
        }

        public void Dispose()
        {
            IconPackageManager.LoadRegistry(null);
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        // A 1x1 transparent PNG, the smallest well-formed image WPF decodes.
        private static readonly byte[] OnePxPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        private string MakePack(string fileName, string manifestName = null, params string[] entries)
        {
            string path = Path.Combine(_dir, fileName);
            using (var fs = File.Create(path))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                if (manifestName != null)
                {
                    var man = zip.CreateEntry("manifest.json");
                    using var w = new StreamWriter(man.Open());
                    w.Write("{\"name\":\"" + manifestName + "\",\"generator\":\"PadForge\"}");
                }
                foreach (var e in entries.Length > 0 ? entries : new[] { "icon.png" })
                {
                    var entry = zip.CreateEntry(e);
                    using var s = entry.Open();
                    s.Write(OnePxPng, 0, OnePxPng.Length);
                }
            }
            return path;
        }

        // ── The manager ──

        [Fact]
        public void Register_ProbesManifestName_AndDedupsCollisions()
        {
            string a = MakePack("a.pficons", "MyIcons");
            string b = MakePack("b.pficons", "MyIcons");
            Assert.Equal("MyIcons", IconPackageManager.Register(a));
            Assert.Equal("MyIcons (2)", IconPackageManager.Register(b));
            // Re-registering the same file refreshes, not duplicates.
            Assert.Equal("MyIcons", IconPackageManager.Register(a));
            Assert.Equal(2, IconPackageManager.Packages.Count);
        }

        [Fact]
        public void Register_RejectsAZipWithNoImages()
        {
            string p = Path.Combine(_dir, "empty.pficons");
            using (var fs = File.Create(p))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("readme.txt");
                using var s = new StreamWriter(e.Open());
                s.Write("no images here");
            }
            Assert.Null(IconPackageManager.Register(p));
        }

        [Fact]
        public void TryReadIcon_ReturnsTheBytes_AndRefGrammarRoundTrips()
        {
            string a = MakePack("pack.pficons", "Pack", "art/glyph.png");
            IconPackageManager.Register(a);

            string iconRef = IconPackageManager.MakeRef("Pack", "art/glyph.png");
            Assert.True(IconPackageManager.TryParseRef(iconRef, out string pkg, out string entry));
            Assert.Equal("Pack", pkg);
            Assert.Equal("art/glyph.png", entry);

            byte[] bytes = IconPackageManager.TryReadIcon(iconRef);
            Assert.NotNull(bytes);
            Assert.Equal(OnePxPng, bytes);

            // Bare entry name matches too, the sound reader's contract.
            Assert.NotNull(IconPackageManager.TryReadIcon(IconPackageManager.MakeRef("Pack", "glyph.png")));
            // Missing entry and missing pack are null, never a throw.
            Assert.Null(IconPackageManager.TryReadIcon(IconPackageManager.MakeRef("Pack", "nope.png")));
            Assert.Null(IconPackageManager.TryReadIcon(IconPackageManager.MakeRef("Ghost", "glyph.png")));
        }

        [Fact]
        public void ExportPackage_RoundTrips_WithEntryDedup()
        {
            string img1 = Path.Combine(_dir, "one.png");
            File.WriteAllBytes(img1, OnePxPng);
            string sub = Directory.CreateDirectory(Path.Combine(_dir, "sub")).FullName;
            string img2 = Path.Combine(sub, "one.png");
            File.WriteAllBytes(img2, OnePxPng);

            string dest = Path.Combine(_dir, "made.pficons");
            Assert.True(IconPackageManager.ExportPackage(dest, "Made", new[] { img1, img2 }));
            string name = IconPackageManager.Register(dest);
            Assert.Equal("Made", name);
            var icons = IconPackageManager.ListIcons(name);
            Assert.Contains("one.png", icons);
            Assert.Contains("one (2).png", icons);
        }

        // ── The resolver's new forms ──

        [Fact]
        public void Resolver_ResolvesPackRefs_AndInvalidatesOnRegistryChange()
        {
            string a = MakePack("res.pficons", "Res");
            IconPackageManager.Register(a);
            string iconRef = IconPackageManager.MakeRef("Res", "icon.png");

            var img = PadForge.Common.MenuIconResolver.Resolve(iconRef);
            Assert.NotNull(img);
            Assert.True(img.IsFrozen);
            Assert.Same(img, PadForge.Common.MenuIconResolver.Resolve(iconRef));

            // Unregister: the cache invalidates and the ref now misses.
            IconPackageManager.Unregister("Res");
            Assert.Null(PadForge.Common.MenuIconResolver.Resolve(iconRef));
        }

        [Fact]
        public void Resolver_ResolvesLooseImagePaths()
        {
            string img = Path.Combine(_dir, "loose.png");
            File.WriteAllBytes(img, OnePxPng);
            var loaded = PadForge.Common.MenuIconResolver.Resolve(img);
            Assert.NotNull(loaded);
            Assert.True(loaded.IsFrozen);
            // A path to a non-image extension is not a loose-path form.
            Assert.False(PadForge.Common.MenuIconResolver.IsLooseImagePath(Path.Combine(_dir, "x.txt")));
            // A bare Steam name is not a loose path (no separators).
            Assert.False(PadForge.Common.MenuIconResolver.IsLooseImagePath("ghost_050_menu_0030.png"));
        }

        // ── Profile transfer ──

        /// <summary>The full pack round trip: a profile whose menu cell
        /// references a pack exports with the pack bundled under icons/,
        /// and importing on a machine where that name is TAKEN by a
        /// different pack lands the file suffixed, registers it, and
        /// rewrites the menu ref to the new name via the alias map.</summary>
        [Fact]
        public void ProfileTransfer_BundlesAndRewritesIconPacks()
        {
            string packPath = MakePack("share.pficons", "Share");
            string registered = IconPackageManager.Register(packPath);
            Assert.Equal("Share", registered);

            var profile = new PadForge.Services.ProfileData
            {
                Name = "IconProfile",
                SlotMappingSets = new[]
                {
                    new PadForge.Engine.Data.MappingSet
                    {
                        Menus =
                        {
                            new PadForge.Engine.Menus.MenuDefinitionEntry
                            {
                                MenuId = 1,
                                Items =
                                {
                                    new PadForge.Engine.Menus.MenuItemDefinition
                                    {
                                        Index = 1,
                                        Label = "cell",
                                        Icon = IconPackageManager.MakeRef("Share", "icon.png"),
                                    },
                                },
                            },
                        },
                    },
                },
            };

            string dest = Path.Combine(_dir, "share.pfprofile");
            var bundled = PadForge.Common.ProfileTransfer.Export(profile, dest);
            Assert.Contains("Share", bundled);
            using (var zip = ZipFile.OpenRead(dest))
            {
                Assert.NotNull(zip.GetEntry("icons/share.pficons"));
                Assert.NotNull(zip.GetEntry("icons/_aliases.txt"));
            }

            // Simulate the importing machine: the name "Share" is taken by
            // a DIFFERENT pack (different content), so the bundled file
            // must land, register suffixed, and the ref must follow.
            IconPackageManager.LoadRegistry(null);
            string other = MakePack("other.pficons", "Share", "different.png");
            Assert.Equal("Share", IconPackageManager.Register(other));

            var imported = PadForge.Common.ProfileTransfer.Import(dest, out var landed);
            Assert.NotNull(imported);
            string newName = landed.LastOrDefault(n => n.StartsWith("Share", StringComparison.Ordinal));
            Assert.NotNull(newName);
            Assert.NotEqual("Share", newName);

            string icon = imported.SlotMappingSets[0].Menus[0].Items[0].Icon;
            Assert.True(IconPackageManager.TryParseRef(icon, out string pkg, out string entry));
            Assert.Equal(newName, pkg);
            Assert.Equal("icon.png", entry);
            // The rewritten ref reads back the original bytes.
            Assert.Equal(OnePxPng, IconPackageManager.TryReadIcon(icon));

            // Cleanup the landed file beside the exe.
            foreach (var n in landed)
            {
                string f = IconPackageManager.ResolvePackageFile(n);
                IconPackageManager.Unregister(n);
                try { if (f != null && File.Exists(f)) File.Delete(f); } catch { }
            }
        }

        /// <summary>The manifest lookup was an ordinal, root-only
        /// GetEntry: a pack zipped from a folder or authored with a
        /// capitalized file name registered under the file name instead
        /// of its manifest name. The lookup now matches the entry NAME
        /// case-insensitively anywhere in the zip, on both package
        /// managers.</summary>
        [Fact]
        public void Register_FindsANestedOrRecasedManifest_OnBothPackageKinds()
        {
            string icons = Path.Combine(_dir, "nested.pficons");
            using (var fs = File.Create(icons))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var man = zip.CreateEntry("Pack/Manifest.json");
                using (var w = new StreamWriter(man.Open())) w.Write("{\"name\":\"Named\"}");
                var img = zip.CreateEntry("Pack/icon.png");
                using var s = img.Open();
                s.Write(OnePxPng, 0, OnePxPng.Length);
            }
            Assert.Equal("Named", IconPackageManager.Register(icons));

            SoundPackageManager.LoadRegistry(null);
            try
            {
                string sounds = Path.Combine(_dir, "nested.pfsounds");
                using (var fs = File.Create(sounds))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    var man = zip.CreateEntry("Pack/MANIFEST.JSON");
                    using (var w = new StreamWriter(man.Open())) w.Write("{\"name\":\"Named Sounds\"}");
                    var wav = zip.CreateEntry("Pack/click.wav");
                    using var s = wav.Open();
                    s.Write(OnePxPng, 0, OnePxPng.Length); // only the extension is probed
                }
                Assert.Equal("Named Sounds", SoundPackageManager.Register(sounds));
            }
            finally { SoundPackageManager.LoadRegistry(null); }
        }

        /// <summary>The settings legs: IconPackages persists beside
        /// SoundPackages with the same DTO shape, load and save.</summary>
        [Fact]
        public void SettingsLegs_MirrorTheSoundPackageShape()
        {
            string ss = RepoText("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("[XmlArray(\"IconPackages\")]", ss);
            Assert.Contains("public IconPackageData[] IconPackages { get; set; }", ss);
            Assert.Contains("PadForge.Common.IconPackageManager.LoadRegistry(", ss);
            Assert.Contains("PadForge.Common.IconPackageManager.SaveRegistry()", ss);
            Assert.Contains("IconPackages = iconPackages,", ss);
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
