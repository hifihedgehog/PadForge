using System;
using System.IO;
using PadForge.SteamWorkshop.Local;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// Local Steam folder fallback for legacy configs (#9 Phase D). The on-disk layout
    /// these fixtures mirror was grounded 2026-07-13 against a live Steam install (86
    /// subscribed items under steamapps\workshop\content\241100): each published file id
    /// is a folder holding either controller_configuration.vdf or {ugchandle}_legacy.bin.
    /// </summary>
    public class LocalWorkshopConfigStoreTests : IDisposable
    {
        private readonly string _root;

        public LocalWorkshopConfigStoreTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "pfsw-local-tests", Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }

        private string NewLibrary(string name)
        {
            var library = Path.Combine(_root, name);
            Directory.CreateDirectory(library);
            return library;
        }

        private static string PlantConfig(string library, ulong fileId, string fileName, string content)
        {
            var dir = Path.Combine(library, "steamapps", "workshop", "content", "241100",
                fileId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);
            File.WriteAllText(path, content);
            return path;
        }

        [Fact]
        public void Finds_the_canonical_vdf_payload()
        {
            var library = NewLibrary("lib0");
            var planted = PlantConfig(library, 579212038, "controller_configuration.vdf", "\"controller_mappings\" {}");

            var found = LocalWorkshopConfigStore.FindConfigFile(579212038, new[] { library });
            Assert.Equal(planted, found);
        }

        [Fact]
        public void Finds_the_legacy_bin_payload()
        {
            var library = NewLibrary("lib0");
            var planted = PlantConfig(library, 523551908, "429322699312489274_legacy.bin", "\"controller_mappings\" {}");

            var found = LocalWorkshopConfigStore.FindConfigFile(523551908, new[] { library });
            Assert.Equal(planted, found);
        }

        [Fact]
        public void Prefers_the_canonical_vdf_over_the_legacy_bin()
        {
            var library = NewLibrary("lib0");
            PlantConfig(library, 650764041, "111_legacy.bin", "legacy");
            var canonical = PlantConfig(library, 650764041, "controller_configuration.vdf", "canonical");

            var found = LocalWorkshopConfigStore.FindConfigFile(650764041, new[] { library });
            Assert.Equal(canonical, found);
        }

        [Fact]
        public void Misses_when_the_item_folder_is_absent()
        {
            var library = NewLibrary("lib0");
            Assert.Null(LocalWorkshopConfigStore.FindConfigFile(42, new[] { library }));
            Assert.Null(LocalWorkshopConfigStore.ReadConfigText(42, new[] { library }));
        }

        [Fact]
        public void Probes_every_library_root_in_order()
        {
            var first = NewLibrary("lib0");
            var second = NewLibrary("lib1");
            var planted = PlantConfig(second, 690652370, "269466470963446092_legacy.bin", "\"controller_mappings\" {}");

            var found = LocalWorkshopConfigStore.FindConfigFile(690652370, new[] { first, second });
            Assert.Equal(planted, found);
        }

        [Fact]
        public void Null_and_blank_roots_are_skipped()
        {
            var library = NewLibrary("lib0");
            var planted = PlantConfig(library, 7, "controller_configuration.vdf", "x");

            var found = LocalWorkshopConfigStore.FindConfigFile(7, new[] { null, "", "  ", library });
            Assert.Equal(planted, found);
            Assert.Null(LocalWorkshopConfigStore.FindConfigFile(7, null));
        }

        [Fact]
        public void Read_returns_the_payload_text()
        {
            var library = NewLibrary("lib0");
            const string vdf = "\"controller_mappings\"\n{\n\t\"version\"\t\t\"3\"\n}\n";
            PlantConfig(library, 793611331, "controller_configuration.vdf", vdf);

            Assert.Equal(vdf, LocalWorkshopConfigStore.ReadConfigText(793611331, new[] { library }));
        }

        [Fact]
        public void Read_rejects_a_payload_over_the_parser_cap()
        {
            var library = NewLibrary("lib0");
            var path = PlantConfig(library, 9, "controller_configuration.vdf", "");
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                fs.SetLength(PadForge.SteamWorkshop.Vdf.VdfParser.MaxInputBytes + 1);
            }

            Assert.Null(LocalWorkshopConfigStore.ReadConfigText(9, new[] { library }));
        }

        [Fact]
        public void Parses_the_nested_libraryfolders_shape()
        {
            const string vdf = """
                "libraryfolders"
                {
                    "0"
                    {
                        "path"		"C:\\Program Files (x86)\\Steam"
                        "label"		""
                    }
                    "1"
                    {
                        "path"		"D:\\SteamLibrary"
                    }
                }
                """;

            var paths = LocalWorkshopConfigStore.ParseLibraryFolders(vdf);
            Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, paths);
        }

        [Fact]
        public void Parses_the_flat_libraryfolders_shape_and_skips_metadata_keys()
        {
            const string vdf = """
                "LibraryFolders"
                {
                    "TimeNextStatsReport"		"1500000000"
                    "ContentStatsID"		"-8123456789012345678"
                    "1"		"D:\\Games\\SteamLibrary"
                    "2"		"E:\\Steam"
                }
                """;

            var paths = LocalWorkshopConfigStore.ParseLibraryFolders(vdf);
            Assert.Equal(new[] { @"D:\Games\SteamLibrary", @"E:\Steam" }, paths);
        }

        [Fact]
        public void Malformed_libraryfolders_yields_no_paths()
        {
            Assert.Empty(LocalWorkshopConfigStore.ParseLibraryFolders(null));
            Assert.Empty(LocalWorkshopConfigStore.ParseLibraryFolders(""));
            Assert.Empty(LocalWorkshopConfigStore.ParseLibraryFolders("\"libraryfolders\" { \"0\" {"));
        }
    }
}
