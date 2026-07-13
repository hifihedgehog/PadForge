using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// Golden-file snapshots: every committed fixture VDF translates (default
    /// options + its file id) to a committed, hand-reviewed snapshot under
    /// <c>Golden/</c>. A diff here is a deliberate translator change. Review
    /// it, then regenerate by running the suite once with the environment
    /// variable <c>PADFORGE_BLESS_GOLDEN=1</c> (writes into the source tree)
    /// and commit the result.
    /// </summary>
    public class TranslationGoldenTests
    {
        private static readonly long[] AllFixtureIds =
        {
            708227783, 770509247, 789818086, 793611331, 875948877,
            930657498, 1129670518, 1223976670, 1370740828, 1723403062,
            1957995349, 2220285578, 2790927974, 2795727040, 2853328208,
            2858159083, 2948704083, 3353173512, 3354224367, 3443409487,
            3451446931, 3456927474,
        };

        public static IEnumerable<object[]> FixtureIds()
            => AllFixtureIds.Select(id => new object[] { id });

        [Fact]
        public void FixtureListMatchesDirectory()
        {
            var onDisk = TestFixtures.AllVdfPaths()
                .Select(p => long.Parse(Path.GetFileNameWithoutExtension(p)))
                .OrderBy(id => id)
                .ToArray();
            Assert.Equal(AllFixtureIds.OrderBy(id => id).ToArray(), onDisk);
        }

        [Theory]
        [MemberData(nameof(FixtureIds))]
        public void Fixture_MatchesGolden(long fileId)
        {
            string actual = Translate(fileId);

            if (Environment.GetEnvironmentVariable("PADFORGE_BLESS_GOLDEN") == "1")
            {
                Directory.CreateDirectory(SourceGoldenDir);
                File.WriteAllText(Path.Combine(SourceGoldenDir, fileId + ".golden.txt"), actual);
                return;
            }

            string goldenPath = Path.Combine(AppContext.BaseDirectory, "Golden", fileId + ".golden.txt");
            Assert.True(File.Exists(goldenPath),
                $"Missing golden snapshot {goldenPath}. Run once with PADFORGE_BLESS_GOLDEN=1, review, commit.");
            string expected = Normalize(File.ReadAllText(goldenPath));
            Assert.Equal(expected, Normalize(actual));
        }

        [Theory]
        [MemberData(nameof(FixtureIds))]
        public void Fixture_TranslationIsDeterministic(long fileId)
        {
            Assert.Equal(Translate(fileId), Translate(fileId));
        }

        private static string Translate(long fileId)
        {
            var root = VdfParser.Parse(TestFixtures.Read(fileId));
            var config = SteamInputConfig.FromVdf(root);
            var translated = new ConfigTranslator().Translate(config, new TranslationOptions
            {
                FileId = fileId,
            });
            return GoldenProjection.Render(translated);
        }

        private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');

        /// <summary>Source-tree Golden directory (bless target): the test
        /// assembly runs from bin/{cfg}/{tfm}, three levels below the project.</summary>
        private static string SourceGoldenDir
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Golden"));
    }
}
