using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PadForge.SteamWorkshop.Cache
{
    /// <summary>Default TTLs for the cache categories, per the recipe.</summary>
    public static class CacheTtls
    {
        public static readonly TimeSpan Games = TimeSpan.FromHours(24);
        public static readonly TimeSpan Search = TimeSpan.FromHours(24);
        public static readonly TimeSpan Personas = TimeSpan.FromDays(7);

        /// <summary>
        /// Freshness window for store artwork (the design's weekly hero re-fetch).
        /// Art past this age is re-fetched rather than dropped: read it through
        /// <see cref="SteamWorkshopCache.TryGetBytesStaleOk"/> so the stale copy
        /// survives as the offline fallback.
        /// </summary>
        public static readonly TimeSpan Art = TimeSpan.FromDays(7);

        // Vdf is immutable per key (keyed by file id + time_updated) and carries
        // no TTL; it ages out by LRU only.
    }

    /// <summary>
    /// File-system cache for the Steam Workshop feature under
    /// <c>%LOCALAPPDATA%\PadForge\SteamWorkshopCache</c>. Categories live in keyed
    /// subdirectories with per-category TTLs. Two independent LRU budgets bound the size:
    /// a general budget (50 MB) across metadata and VDF blobs, and a separate art budget
    /// (60 MB). Writes are atomic (temp file then replace). Freshness (TTL) is tracked via
    /// each entry's last-write time; recency (LRU) via its last-access time, both stamped
    /// from an injectable clock so the policy is deterministic under test.
    /// </summary>
    public sealed class SteamWorkshopCache
    {
        public const long DefaultGeneralBudgetBytes = 50L * 1024 * 1024;
        public const long DefaultArtBudgetBytes = 60L * 1024 * 1024;

        private enum BudgetGroup { General, Art }

        private readonly struct CategoryInfo
        {
            public CategoryInfo(string dir, BudgetGroup group, string extension)
            {
                Dir = dir;
                Group = group;
                Extension = extension;
            }

            public string Dir { get; }
            public BudgetGroup Group { get; }
            public string Extension { get; }
        }

        private static readonly IReadOnlyDictionary<CacheCategory, CategoryInfo> Categories =
            new Dictionary<CacheCategory, CategoryInfo>
            {
                [CacheCategory.Games] = new CategoryInfo("games", BudgetGroup.General, ".json"),
                [CacheCategory.Search] = new CategoryInfo("search", BudgetGroup.General, ".json"),
                [CacheCategory.Vdf] = new CategoryInfo("vdf", BudgetGroup.General, ".vdf"),
                [CacheCategory.Personas] = new CategoryInfo("personas", BudgetGroup.General, ".json"),
                [CacheCategory.Art] = new CategoryInfo("art", BudgetGroup.Art, ".img"),
            };

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly object _writeLock = new object();
        private readonly long _generalBudgetBytes;
        private readonly long _artBudgetBytes;
        private readonly Func<DateTimeOffset> _clock;

        public string RootDirectory { get; }

        public SteamWorkshopCache(
            string rootDirectory = null,
            long generalBudgetBytes = DefaultGeneralBudgetBytes,
            long artBudgetBytes = DefaultArtBudgetBytes,
            Func<DateTimeOffset> clock = null)
        {
            if (generalBudgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(generalBudgetBytes));
            if (artBudgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(artBudgetBytes));

            RootDirectory = rootDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PadForge", "SteamWorkshopCache");
            _generalBudgetBytes = generalBudgetBytes;
            _artBudgetBytes = artBudgetBytes;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);

            Directory.CreateDirectory(RootDirectory);
        }

        // ---- byte entries ----------------------------------------------------

        public bool TryGetBytes(CacheCategory category, string key, TimeSpan? ttl, out byte[] value)
        {
            value = null;
            var path = ResolvePath(category, key);
            if (!File.Exists(path)) return false;

            if (ttl.HasValue)
            {
                var age = _clock().UtcDateTime - File.GetLastWriteTimeUtc(path);
                if (age > ttl.Value)
                {
                    // Under _writeLock, and re-checked inside it. Every write
                    // takes that lock; this delete did not, so a writer that
                    // refreshed the entry between the age read above and the
                    // delete had its FRESH file erased (round 34). Still
                    // reports a miss when the re-check says fresh, which
                    // costs one refetch and never costs data.
                    lock (_writeLock)
                    {
                        var ageNow = _clock().UtcDateTime - File.GetLastWriteTimeUtc(path);
                        if (ageNow > ttl.Value) TryDelete(path);
                    }
                    return false;
                }
            }

            try
            {
                value = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                value = null;
                return false;
            }

            TouchAccess(path);
            return true;
        }

        /// <summary>
        /// TTL-aware read that keeps expired entries. A hit older than
        /// <paramref name="freshFor"/> comes back with <paramref name="stale"/> set instead
        /// of being deleted, so the caller can re-fetch and still fall back to the old copy
        /// when the network is down (offline browsing keeps art).
        /// </summary>
        public bool TryGetBytesStaleOk(CacheCategory category, string key, TimeSpan freshFor,
            out byte[] value, out bool stale)
        {
            value = null;
            stale = false;
            var path = ResolvePath(category, key);
            if (!File.Exists(path)) return false;

            var age = _clock().UtcDateTime - File.GetLastWriteTimeUtc(path);
            stale = age > freshFor;

            try
            {
                value = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                value = null;
                stale = false;
                return false;
            }

            TouchAccess(path);
            return true;
        }

        public void PutBytes(CacheCategory category, string key, byte[] value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            WriteAtomic(category, key, value);
        }

        // ---- string entries --------------------------------------------------

        public bool TryGetString(CacheCategory category, string key, TimeSpan? ttl, out string value)
        {
            if (TryGetBytes(category, key, ttl, out var bytes))
            {
                value = Encoding.UTF8.GetString(bytes);
                return true;
            }
            value = null;
            return false;
        }

        public void PutString(CacheCategory category, string key, string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            WriteAtomic(category, key, Encoding.UTF8.GetBytes(value));
        }

        // ---- typed JSON entries ---------------------------------------------

        public bool TryGetJson<T>(CacheCategory category, string key, TimeSpan? ttl, out T value)
        {
            value = default;
            if (!TryGetBytes(category, key, ttl, out var bytes)) return false;
            try
            {
                value = JsonSerializer.Deserialize<T>(bytes, JsonOptions);
                return true;
            }
            catch (JsonException)
            {
                // Corrupt entry: drop it and report a miss.
                TryDelete(ResolvePath(category, key));
                value = default;
                return false;
            }
        }

        public void PutJson<T>(CacheCategory category, string key, T value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            WriteAtomic(category, key, bytes);
        }

        // ---- maintenance -----------------------------------------------------

        /// <summary>Removes every cached entry across all categories.</summary>
        public void Clear()
        {
            lock (_writeLock)
            {
                foreach (var info in Categories.Values)
                {
                    var dir = Path.Combine(RootDirectory, info.Dir);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var file in Directory.EnumerateFiles(dir))
                        TryDelete(file);
                }
            }
        }

        /// <summary>Total bytes currently used by the budget group that <paramref name="category"/> belongs to.</summary>
        public long BudgetUsedBytes(CacheCategory category)
        {
            var group = Categories[category].Group;
            long total = 0;
            foreach (var file in EnumerateGroupFiles(group))
                total += file.Length;
            return total;
        }

        // ---- internals -------------------------------------------------------

        private void WriteAtomic(CacheCategory category, string key, byte[] data)
        {
            var info = Categories[category];
            var dir = Path.Combine(RootDirectory, info.Dir);
            var finalPath = Path.Combine(dir, SafeFileName(key) + info.Extension);
            var tmpPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");

            lock (_writeLock)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(tmpPath, data);
                    File.Move(tmpPath, finalPath, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The cache is an optimization: a write that loses a sharing
                    // race (a reader holding the destination open) or hits a
                    // full/locked disk must never fail the operation that produced
                    // the data. Same principle as the serialize guard at the
                    // Workshop client's put site.
                    TryDelete(tmpPath);
                    return;
                }
                catch
                {
                    TryDelete(tmpPath);
                    throw;
                }

                var stamp = _clock().UtcDateTime;
                try
                {
                    File.SetLastWriteTimeUtc(finalPath, stamp);
                    File.SetLastAccessTimeUtc(finalPath, stamp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Timestamp stamping is best-effort; a failure only affects eviction ordering.
                }

                EnforceBudget(info.Group);
            }
        }

        private void EnforceBudget(BudgetGroup group)
        {
            var budget = group == BudgetGroup.Art ? _artBudgetBytes : _generalBudgetBytes;

            var files = EnumerateGroupFiles(group).ToList();
            long total = files.Sum(f => f.Length);
            if (total <= budget) return;

            // Evict least-recently-accessed first.
            foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc))
            {
                if (total <= budget) break;
                var size = file.Length;
                if (TryDelete(file.FullName))
                    total -= size;
            }
        }

        private IEnumerable<FileInfo> EnumerateGroupFiles(BudgetGroup group)
        {
            foreach (var kv in Categories)
            {
                if (kv.Value.Group != group) continue;
                var dir = Path.Combine(RootDirectory, kv.Value.Dir);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    FileInfo fi;
                    try
                    {
                        fi = new FileInfo(file);
                        if (!fi.Exists) continue;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }
                    // Skip in-flight temp files from a concurrent write. An
                    // ORPHAN (process killed between WriteAllBytes and Move)
                    // would otherwise be skipped forever and never counted
                    // against the cache budget, so disk use grew without
                    // bound. Anything older than an hour cannot still be
                    // in flight; reap it (round 34).
                    if (fi.Name.Contains(".tmp-"))
                    {
                        try
                        {
                            if (_clock().UtcDateTime - fi.LastWriteTimeUtc > TimeSpan.FromHours(1))
                                fi.Delete();
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                        continue;
                    }
                    yield return fi;
                }
            }
        }

        private void TouchAccess(string path)
        {
            try
            {
                File.SetLastAccessTimeUtc(path, _clock().UtcDateTime);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort recency update.
            }
        }

        private string ResolvePath(CacheCategory category, string key)
        {
            var info = Categories[category];
            return Path.Combine(RootDirectory, info.Dir, SafeFileName(key) + info.Extension);
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Maps a cache key to a safe file name: simple ASCII keys pass through for
        /// debuggability; anything else (spaces, slashes, non-ASCII, over-long) is hashed.
        /// </summary>
        private static string SafeFileName(string key)
        {
            if (string.IsNullOrEmpty(key)) return "_";

            var simple = key.Length <= 96;
            if (simple)
            {
                foreach (var c in key)
                {
                    var ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') ||
                             (c >= 'a' && c <= 'z') || c == '.' || c == '_' || c == '-';
                    if (!ok)
                    {
                        simple = false;
                        break;
                    }
                }
            }

            if (simple) return key;

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
