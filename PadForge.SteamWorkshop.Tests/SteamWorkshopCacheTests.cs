using System;
using System.IO;
using System.Linq;
using PadForge.SteamWorkshop.Cache;

namespace PadForge.SteamWorkshop.Tests
{
    public class SteamWorkshopCacheTests : IDisposable
    {
        private readonly string _root;
        private DateTimeOffset _now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public SteamWorkshopCacheTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "pfsw-cache-tests", Guid.NewGuid().ToString("N"));
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

        private SteamWorkshopCache NewCache(
            long general = SteamWorkshopCache.DefaultGeneralBudgetBytes,
            long art = SteamWorkshopCache.DefaultArtBudgetBytes)
            => new SteamWorkshopCache(_root, general, art, () => _now);

        private sealed class Sample
        {
            public string Name { get; set; }
            public int Id { get; set; }
        }

        [Fact]
        public void String_round_trips()
        {
            var cache = NewCache();
            cache.PutString(CacheCategory.Search, "123", "hello");
            Assert.True(cache.TryGetString(CacheCategory.Search, "123", CacheTtls.Search, out var value));
            Assert.Equal("hello", value);
        }

        [Fact]
        public void Bytes_round_trip()
        {
            var cache = NewCache();
            var data = new byte[] { 1, 2, 3, 4, 5 };
            cache.PutBytes(CacheCategory.Vdf, "793611331_1478305293", data);
            Assert.True(cache.TryGetBytes(CacheCategory.Vdf, "793611331_1478305293", null, out var read));
            Assert.Equal(data, read);
        }

        [Fact]
        public void Json_round_trips()
        {
            var cache = NewCache();
            cache.PutJson(CacheCategory.Personas, "440", new Sample { Name = "TF2", Id = 440 });
            Assert.True(cache.TryGetJson<Sample>(CacheCategory.Personas, "440", CacheTtls.Personas, out var sample));
            Assert.Equal("TF2", sample.Name);
            Assert.Equal(440, sample.Id);
        }

        [Fact]
        public void Absent_key_is_a_miss()
        {
            var cache = NewCache();
            Assert.False(cache.TryGetString(CacheCategory.Search, "nope", CacheTtls.Search, out var value));
            Assert.Null(value);
        }

        [Fact]
        public void Put_swallows_a_sharing_violation_and_keeps_the_old_entry()
        {
            // The cache is an optimization: a concurrent reader holding the
            // destination (same-key read racing a rewrite) makes the atomic
            // replace fail with a sharing violation, and that must never
            // propagate into the operation that produced the data.
            var cache = NewCache();
            cache.PutBytes(CacheCategory.Games, "locked", new byte[] { 1 });

            var path = Path.Combine(_root, "games", "locked.json");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                cache.PutBytes(CacheCategory.Games, "locked", new byte[] { 2 }); // must not throw
            }

            Assert.True(cache.TryGetBytes(CacheCategory.Games, "locked", null, out var read));
            Assert.Equal(new byte[] { 1 }, read);
            // The failed write's temp file was cleaned up.
            Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(_root, "games")),
                f => f.Contains(".tmp-", StringComparison.Ordinal));
        }

        [Fact]
        public void Entry_expires_after_ttl()
        {
            var cache = NewCache();
            cache.PutString(CacheCategory.Search, "k", "v");

            _now = _now.AddHours(1);
            Assert.True(cache.TryGetString(CacheCategory.Search, "k", TimeSpan.FromHours(24), out _));

            _now = _now.AddHours(48); // 49 h total, past the 24 h TTL
            Assert.False(cache.TryGetString(CacheCategory.Search, "k", TimeSpan.FromHours(24), out _));
        }

        [Fact]
        public void Null_ttl_never_expires()
        {
            var cache = NewCache();
            cache.PutBytes(CacheCategory.Vdf, "immutable", new byte[] { 9 });
            _now = _now.AddDays(3650);
            Assert.True(cache.TryGetBytes(CacheCategory.Vdf, "immutable", null, out _));
        }

        [Fact]
        public void Evicts_least_recently_accessed_when_general_budget_exceeded()
        {
            var cache = NewCache(general: 320);
            var payload = new string('x', 150); // 150 bytes each

            var t0 = _now;
            _now = t0; cache.PutString(CacheCategory.Search, "a", payload);
            _now = t0.AddMinutes(1); cache.PutString(CacheCategory.Search, "b", payload);
            _now = t0.AddMinutes(2); Assert.True(cache.TryGetString(CacheCategory.Search, "a", null, out _)); // touch a
            _now = t0.AddMinutes(3); cache.PutString(CacheCategory.Search, "c", payload); // 450 > 320 -> evict

            Assert.False(cache.TryGetString(CacheCategory.Search, "b", null, out _)); // b was least recently used
            Assert.True(cache.TryGetString(CacheCategory.Search, "a", null, out _));
            Assert.True(cache.TryGetString(CacheCategory.Search, "c", null, out _));
            Assert.True(cache.BudgetUsedBytes(CacheCategory.Search) <= 320);
        }

        [Fact]
        public void Art_budget_is_independent_of_general_budget()
        {
            var cache = NewCache(general: 300, art: 10 * 1024);
            cache.PutBytes(CacheCategory.Art, "hero", new byte[500]);

            // Flood the general budget; art must not be touched.
            for (var i = 0; i < 6; i++)
            {
                _now = _now.AddMinutes(1);
                cache.PutString(CacheCategory.Search, "g" + i, new string('x', 150));
            }

            Assert.True(cache.TryGetBytes(CacheCategory.Art, "hero", null, out _));
            Assert.True(cache.BudgetUsedBytes(CacheCategory.Search) <= 300);
        }

        [Fact]
        public void Overwrite_is_atomic_and_leaves_no_temp_files()
        {
            var cache = NewCache();
            cache.PutString(CacheCategory.Search, "k", "first");
            cache.PutString(CacheCategory.Search, "k", "second");

            Assert.True(cache.TryGetString(CacheCategory.Search, "k", null, out var value));
            Assert.Equal("second", value);

            var searchDir = Path.Combine(_root, "search");
            Assert.DoesNotContain(Directory.EnumerateFiles(searchDir), f => f.Contains(".tmp-"));
        }

        [Fact]
        public void Clear_removes_all_entries()
        {
            var cache = NewCache();
            cache.PutString(CacheCategory.Search, "k", "v");
            cache.PutBytes(CacheCategory.Art, "a", new byte[] { 1, 2, 3 });

            cache.Clear();

            Assert.False(cache.TryGetString(CacheCategory.Search, "k", null, out _));
            Assert.False(cache.TryGetBytes(CacheCategory.Art, "a", null, out _));
            Assert.Equal(0, cache.BudgetUsedBytes(CacheCategory.Search));
            Assert.Equal(0, cache.BudgetUsedBytes(CacheCategory.Art));
        }

        [Fact]
        public void Corrupt_json_is_treated_as_a_miss()
        {
            var cache = NewCache();
            cache.PutString(CacheCategory.Personas, "bad", "{ not valid json ");
            Assert.False(cache.TryGetJson<Sample>(CacheCategory.Personas, "bad", null, out _));
        }

        [Fact]
        public void Unsafe_keys_are_hashed_and_still_round_trip()
        {
            var cache = NewCache();
            const string query = "the elder scrolls v: skyrim / special edition";
            cache.PutString(CacheCategory.Games, query, "appids");
            Assert.True(cache.TryGetString(CacheCategory.Games, query, CacheTtls.Games, out var value));
            Assert.Equal("appids", value);
        }

        [Fact]
        public void Stale_ok_read_is_fresh_within_the_window()
        {
            var cache = NewCache();
            cache.PutBytes(CacheCategory.Art, "440_header.jpg", new byte[] { 1, 2, 3 });

            _now = _now.AddDays(6);
            Assert.True(cache.TryGetBytesStaleOk(CacheCategory.Art, "440_header.jpg", CacheTtls.Art, out var value, out var stale));
            Assert.False(stale);
            Assert.Equal(new byte[] { 1, 2, 3 }, value);
        }

        [Fact]
        public void Stale_ok_read_flags_but_keeps_an_expired_entry()
        {
            var cache = NewCache();
            cache.PutBytes(CacheCategory.Art, "440_header.jpg", new byte[] { 1, 2, 3 });

            _now = _now.AddDays(8);
            Assert.True(cache.TryGetBytesStaleOk(CacheCategory.Art, "440_header.jpg", CacheTtls.Art, out var value, out var stale));
            Assert.True(stale);
            Assert.Equal(new byte[] { 1, 2, 3 }, value);

            // Unlike the TTL read, the expired entry survives as the offline fallback.
            Assert.True(cache.TryGetBytes(CacheCategory.Art, "440_header.jpg", null, out _));
        }

        [Fact]
        public void Stale_ok_read_misses_on_an_absent_key()
        {
            var cache = NewCache();
            Assert.False(cache.TryGetBytesStaleOk(CacheCategory.Art, "nope", CacheTtls.Art, out var value, out var stale));
            Assert.Null(value);
            Assert.False(stale);
        }

        [Fact]
        public void Rewrite_resets_the_freshness_window()
        {
            var cache = NewCache();
            cache.PutBytes(CacheCategory.Art, "440_header.jpg", new byte[] { 1 });

            _now = _now.AddDays(8);
            cache.PutBytes(CacheCategory.Art, "440_header.jpg", new byte[] { 2 });

            _now = _now.AddDays(6);
            Assert.True(cache.TryGetBytesStaleOk(CacheCategory.Art, "440_header.jpg", CacheTtls.Art, out var value, out var stale));
            Assert.False(stale);
            Assert.Equal(new byte[] { 2 }, value);
        }
    }
}
