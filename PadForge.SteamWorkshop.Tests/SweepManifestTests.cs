using System;
using System.IO;
using System.Linq;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Guards the two contracts the wild-corpus sweep tool shares
    /// with this suite: the harvest manifest round-trips losslessly, and
    /// the approved reason-key set derives from the one lockdown multiset
    /// (single source of truth, no drift between the test and the tool).</summary>
    public class SweepManifestTests
    {
        private static SweepManifest BuildSample() => new SweepManifest
        {
            HarvestedUtc = "2026-07-18T00:00:00Z",
            Entries =
            {
                new SweepManifestEntry
                {
                    AppId = 489830,
                    AppName = "The Elder Scrolls V: Skyrim Special Edition",
                    FileId = 793611331,
                    TitleSha256 = new string('a', 64),
                    VdfSha256 = new string('b', 64),
                },
                new SweepManifestEntry
                {
                    AppId = 1361210,
                    AppName = "Warhammer 40,000: Darktide",
                    FileId = 2853328208,
                    TitleSha256 = new string('c', 64),
                    VdfSha256 = new string('d', 64),
                },
            },
        };

        private static void AssertEqual(SweepManifest expected, SweepManifest actual)
        {
            Assert.Equal(expected.HarvestedUtc, actual.HarvestedUtc);
            Assert.Equal(expected.Entries.Count, actual.Entries.Count);
            for (int i = 0; i < expected.Entries.Count; i++)
            {
                Assert.Equal(expected.Entries[i].AppId, actual.Entries[i].AppId);
                Assert.Equal(expected.Entries[i].AppName, actual.Entries[i].AppName);
                Assert.Equal(expected.Entries[i].FileId, actual.Entries[i].FileId);
                Assert.Equal(expected.Entries[i].TitleSha256, actual.Entries[i].TitleSha256);
                Assert.Equal(expected.Entries[i].VdfSha256, actual.Entries[i].VdfSha256);
            }
        }

        [Fact]
        public void Manifest_JsonRoundTrip_IsLossless()
        {
            var manifest = BuildSample();
            AssertEqual(manifest, SweepManifest.FromJson(manifest.ToJson()));
        }

        [Fact]
        public void Manifest_FileRoundTrip_IsLossless()
        {
            var manifest = BuildSample();
            var path = Path.Combine(Path.GetTempPath(), "padforge-sweep-manifest-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                manifest.Save(path);
                AssertEqual(manifest, SweepManifest.Load(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ApprovedKeys_DeriveFromTheLockdownMultiset()
        {
            var expected = ApprovedReasonLockdown.CorpusMultiset
                .Select(e => e.Substring(0, e.LastIndexOf('=')))
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(expected.Count, ApprovedReasonLockdown.ApprovedKeys.Count);
            foreach (var key in expected)
                Assert.Contains(key, ApprovedReasonLockdown.ApprovedKeys);

            // Every multiset entry carries a count and no key smuggles one in.
            foreach (var key in ApprovedReasonLockdown.ApprovedKeys)
                Assert.DoesNotContain("=", key);

            // A safety-net key outside the multiset stays unapproved.
            Assert.DoesNotContain("Workshop_Tr_ActivatorInputNotSupported",
                ApprovedReasonLockdown.ApprovedKeys);
        }
    }
}
