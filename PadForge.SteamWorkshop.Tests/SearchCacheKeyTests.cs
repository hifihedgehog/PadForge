using System;
using System.Linq;
using System.Reflection;
using PadForge.SteamWorkshop.Api;
using SteamKit2;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// <para>The search cache is keyed by hand, so every input that changes
    /// WHICH configs a page contains has to be in the key. The free-text
    /// filter is one of those, and leaving it out is a silent failure rather
    /// than a loud one: the first query typed against an (appid, sort, page)
    /// already fetched would be served the UNFILTERED page out of cache and
    /// look like a search that matched everything.</para>
    /// <para>Reached by reflection because the builder is private and this
    /// project is network-free. That is the point: the key is pure, so it can
    /// be pinned without a round trip.</para>
    /// </summary>
    public class SearchCacheKeyTests
    {
        private static readonly MethodInfo Build = typeof(SteamWorkshopClient)
            .GetMethod("BuildSearchKey", BindingFlags.NonPublic | BindingFlags.Static);

        private static string Key(int appId = 730,
            EPublishedFileQueryType q = EPublishedFileQueryType.RankedByVote,
            int page = 1, int perPage = 30, string[] tags = null, string text = null)
            => (string)Build.Invoke(null, new object[] { appId, q, page, perPage, tags, text });

        [Fact]
        public void TheBuilderIsStillThere()
        {
            // Positive control. Reflection that silently resolves to null
            // would turn every assertion below into a NullReferenceException
            // rather than a pass, but a rename could also make this file
            // quietly meaningless if it were written more leniently.
            Assert.NotNull(Build);
            Assert.False(string.IsNullOrWhiteSpace(Key()));
        }

        [Fact]
        public void SearchText_ChangesTheKey()
        {
            Assert.NotEqual(Key(), Key(text: "gyro"));
            Assert.NotEqual(Key(text: "gyro"), Key(text: "dpad"));
        }

        [Fact]
        public void SearchText_IsCaseAndWhitespaceInsensitive()
        {
            // One query typed three ways is one query, so it should hit the
            // same cached page rather than spending three round trips.
            Assert.Equal(Key(text: "gyro"), Key(text: "GYRO"));
            Assert.Equal(Key(text: "gyro"), Key(text: "  Gyro  "));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void EmptySearchText_KeepsTheUnfilteredKey(string blank)
            => Assert.Equal(Key(), Key(text: blank));

        [Fact]
        public void EveryOtherInput_StillChangesTheKey()
        {
            // Footprint check: the text key is appended, so a bug that
            // appended it in place of the rest would show up here.
            string b = Key();
            Assert.NotEqual(b, Key(appId: 440));
            Assert.NotEqual(b, Key(q: EPublishedFileQueryType.RankedByTrend));
            Assert.NotEqual(b, Key(page: 2));
            Assert.NotEqual(b, Key(perPage: 50));
            Assert.NotEqual(b, Key(tags: new[] { "controller_neptune" }));
        }

        [Fact]
        public void TagOrder_DoesNotChangeTheKey()
        {
            // Pre-existing contract, re-asserted because the text key was
            // appended right beside it.
            Assert.Equal(
                Key(tags: new[] { "a", "b" }),
                Key(tags: new[] { "b", "a" }));
        }

        [Fact]
        public void TextAndTags_AreDistinguishable()
        {
            // The two are concatenated, so a query that happens to read like
            // a tag must not collide with that tag being set.
            Assert.NotEqual(
                Key(tags: new[] { "gyro" }),
                Key(text: "gyro"));
        }

        [Fact]
        public void TheKeyIsSafeAsAFileName()
        {
            // Cache entries land on disk under this key, so a query with a
            // slash or a quote in it must not escape into a path.
            char[] bad = System.IO.Path.GetInvalidFileNameChars();
            foreach (var q in new[] { "a/b", "a\\b", "a:b", "a\"b", "a*b?", "  ..  " })
                Assert.DoesNotContain(Key(text: q), c => bad.Contains(c));
        }
    }
}
