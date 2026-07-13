using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PadForge.SteamWorkshop.Api;
using PadForge.SteamWorkshop.Cache;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// Art freshness policy (#9 Phase D): cached art is served without a network
    /// touch for 7 days, re-fetched after, and a re-fetch that fails on the network
    /// falls back to the stale copy so offline browsing keeps art. All network I/O
    /// goes through a stub handler, so nothing here is live.
    /// </summary>
    public class SteamArtworkClientTests : IDisposable
    {
        private static readonly byte[] JpegA = { 0xFF, 0xD8, 0xFF, 0xE0, 0x0A };
        private static readonly byte[] JpegB = { 0xFF, 0xD8, 0xFF, 0xE1, 0x0B };

        private readonly string _root;
        private DateTimeOffset _now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public SteamArtworkClientTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "pfsw-art-tests", Guid.NewGuid().ToString("N"));
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

        private sealed class FakeGate : ISteamWorkshopGate
        {
            public bool IsCommunityConfigLookupEnabled => true;
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; }

            public int Calls { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Calls++;
                ct.ThrowIfCancellationRequested();
                if (Respond == null) throw new HttpRequestException("offline");
                return Task.FromResult(Respond(request));
            }
        }

        private static HttpResponseMessage Ok(byte[] bytes) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

        private (SteamArtworkClient Client, StubHandler Handler) NewClient()
        {
            var cache = new SteamWorkshopCache(_root, clock: () => _now);
            var handler = new StubHandler { Respond = _ => Ok(JpegA) };
            var client = new SteamArtworkClient(new FakeGate(), cache, new HttpClient(handler));
            return (client, handler);
        }

        [Fact]
        public async Task Fresh_cache_hit_skips_the_network()
        {
            var (client, handler) = NewClient();
            Assert.Equal(JpegA, (await client.GetFileAsync(440, "header.jpg")).Data);
            Assert.Equal(1, handler.Calls);

            _now = _now.AddDays(6);
            Assert.Equal(JpegA, (await client.GetFileAsync(440, "header.jpg")).Data);
            Assert.Equal(1, handler.Calls);
        }

        [Fact]
        public async Task Stale_entry_refetches_and_resets_freshness()
        {
            var (client, handler) = NewClient();
            await client.GetFileAsync(440, "header.jpg");

            _now = _now.AddDays(8);
            handler.Respond = _ => Ok(JpegB);
            Assert.Equal(JpegB, (await client.GetFileAsync(440, "header.jpg")).Data);
            Assert.Equal(2, handler.Calls);

            _now = _now.AddDays(6);
            Assert.Equal(JpegB, (await client.GetFileAsync(440, "header.jpg")).Data);
            Assert.Equal(2, handler.Calls);
        }

        [Fact]
        public async Task Offline_refetch_serves_the_stale_copy()
        {
            var (client, handler) = NewClient();
            await client.GetFileAsync(440, "header.jpg");

            _now = _now.AddDays(8);
            handler.Respond = null; // every request now fails at the socket
            var result = await client.GetFileAsync(440, "header.jpg");
            Assert.Equal(JpegA, result.Data);

            // Serving stale must not reset freshness: the next read tries again.
            await client.GetFileAsync(440, "header.jpg");
            Assert.Equal(3, handler.Calls);
        }

        [Fact]
        public async Task Http_timeout_serves_the_stale_copy()
        {
            var (client, handler) = NewClient();
            await client.GetFileAsync(440, "header.jpg");

            _now = _now.AddDays(8);
            // The HttpClient timeout surfaces as TaskCanceledException without
            // the caller's token being set.
            handler.Respond = _ => throw new TaskCanceledException("timeout");
            Assert.Equal(JpegA, (await client.GetFileAsync(440, "header.jpg")).Data);
        }

        [Fact]
        public async Task Offline_without_a_cached_copy_still_throws()
        {
            var (client, handler) = NewClient();
            handler.Respond = null;
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetFileAsync(440, "header.jpg"));
        }

        [Fact]
        public async Task Caller_cancellation_propagates_even_with_a_stale_copy()
        {
            var (client, _) = NewClient();
            await client.GetFileAsync(440, "header.jpg");

            _now = _now.AddDays(8);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.GetFileAsync(440, "header.jpg", cts.Token));
        }

        [Fact]
        public async Task Definitive_404_falls_through_to_the_chain()
        {
            var (client, handler) = NewClient();
            handler.Respond = req => req.RequestUri.AbsolutePath.EndsWith("/header.jpg", StringComparison.Ordinal)
                ? Ok(JpegB)
                : new HttpResponseMessage(HttpStatusCode.NotFound);

            var result = await client.GetHeroAsync(440);
            Assert.Equal("header.jpg", result.File);
            Assert.Equal(JpegB, result.Data);
        }
    }
}
