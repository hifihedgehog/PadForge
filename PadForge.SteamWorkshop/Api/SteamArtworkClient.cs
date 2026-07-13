using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PadForge.SteamWorkshop.Cache;

namespace PadForge.SteamWorkshop.Api
{
    /// <summary>The bytes and provenance of a successfully fetched piece of store artwork.</summary>
    public sealed class ArtworkResult
    {
        public ArtworkResult(int appId, string file, string url, byte[] data)
        {
            AppId = appId;
            File = file;
            Url = url;
            Data = data;
        }

        public int AppId { get; }

        /// <summary>The CDN file that satisfied the request (after any fallback), e.g. <c>header.jpg</c>.</summary>
        public string File { get; }

        public string Url { get; }

        public byte[] Data { get; }
    }

    /// <summary>
    /// Fetches store artwork from the Steam CDN
    /// (<c>cdn.cloudflare.steamstatic.com/steam/apps/{appid}/{file}</c>), walking the plan's
    /// fallback chains so a missing asset degrades to a coarser one that exists on every
    /// title. Runtime hotlink only, cache-first; nothing is rehosted. The constructor throws
    /// if the opt-in gate is off.
    /// </summary>
    public sealed class SteamArtworkClient
    {
        private const string CdnBase = "https://cdn.cloudflare.steamstatic.com/steam/apps";
        private const long MaxArtBytes = 16L * 1024 * 1024;

        // Verified universal on every tested title; the *_2x and library_capsule variants 404.
        private static readonly string[] PortraitChain = { "library_600x900.jpg", "capsule_616x353.jpg", "header.jpg" };
        private static readonly string[] HeroChain = { "library_hero.jpg", "header.jpg" };

        private readonly SteamWorkshopCache _cache;

        public SteamArtworkClient(ISteamWorkshopGate gate, SteamWorkshopCache cache = null)
        {
            SteamWorkshopGuard.EnsureEnabled(gate);
            _cache = cache;
        }

        /// <summary>Portrait art with fallback: 600x900 to capsule_616x353 to header. Null if none resolve.</summary>
        public Task<ArtworkResult> GetPortraitAsync(int appId, CancellationToken ct = default) =>
            FetchChainAsync(appId, PortraitChain, ct);

        /// <summary>Hero art with fallback: library_hero to header. Null if none resolve.</summary>
        public Task<ArtworkResult> GetHeroAsync(int appId, CancellationToken ct = default) =>
            FetchChainAsync(appId, HeroChain, ct);

        /// <summary>Fetches one specific CDN file with no fallback. Null on 404 or a non-image body.</summary>
        public async Task<ArtworkResult> GetFileAsync(int appId, string file, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(file)) throw new ArgumentException("file is empty.", nameof(file));

            var key = $"{appId}_{file}";
            if (_cache != null && _cache.TryGetBytes(CacheCategory.Art, key, null, out var cached))
                return new ArtworkResult(appId, file, BuildUrl(appId, file), cached);

            var bytes = await GetRawAsync(appId, file, ct).ConfigureAwait(false);
            if (bytes == null) return null;

            _cache?.PutBytes(CacheCategory.Art, key, bytes);
            return new ArtworkResult(appId, file, BuildUrl(appId, file), bytes);
        }

        private async Task<ArtworkResult> FetchChainAsync(int appId, string[] files, CancellationToken ct)
        {
            foreach (var file in files)
            {
                var result = await GetFileAsync(appId, file, ct).ConfigureAwait(false);
                if (result != null) return result;
            }
            return null;
        }

        private static string BuildUrl(int appId, string file) => $"{CdnBase}/{appId}/{file}";

        private async Task<byte[]> GetRawAsync(int appId, string file, CancellationToken ct)
        {
            using var response = await SteamHttp.Client
                .GetAsync(BuildUrl(appId, file), HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var declared = response.Content.Headers.ContentLength;
            if (declared.HasValue && declared.Value > MaxArtBytes)
                return null;

            var bytes = await ReadCappedAsync(response.Content, MaxArtBytes, ct).ConfigureAwait(false);
            return bytes != null && LooksLikeImage(bytes) ? bytes : null;
        }

        private static async Task<byte[]> ReadCappedAsync(HttpContent content, long cap, CancellationToken ct)
        {
            await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > cap)
                    return null; // oversized: treat as unusable
            }
            return buffer.ToArray();
        }

        private static bool LooksLikeImage(byte[] b)
        {
            if (b.Length < 4) return false;

            // JPEG
            if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;
            // PNG
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;
            // GIF
            if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return true;
            // WEBP (RIFF....WEBP)
            if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
                b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return true;

            return false;
        }
    }
}
