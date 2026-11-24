using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The one request Settings, Updates, Controller Database sends (#597):
    /// what it asks GitHub, which answers it takes, and where it stops
    /// reading. Every test hands the fetch a transport of its own, so no
    /// request leaves the PC, and the last tests pin that the real
    /// transport refuses in the test host.
    /// </summary>
    public class ControllerDatabaseUpdaterTests
    {
        private static readonly TimeSpan Stall = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

        /// <summary>A transport that answers from a script and keeps what it
        /// was asked.</summary>
        private sealed class Transport
        {
            public readonly List<(Uri Address, string IfNoneMatch, string AcceptEncoding, string Method)> Asked = new();
            private readonly Queue<Func<HttpResponseMessage>> _answers = new();

            public Transport Then(Func<HttpResponseMessage> answer)
            {
                _answers.Enqueue(answer);
                return this;
            }

            public Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                Asked.Add((request.RequestUri, request.Headers.IfNoneMatch.ToString(), request.Headers.AcceptEncoding.ToString(),
                    request.Method.Method));
                Assert.True(_answers.Count > 0, "a request the test did not expect: " + request.RequestUri);
                return Task.FromResult(_answers.Dequeue()());
            }
        }

        private static HttpResponseMessage Ok(byte[] body, string etag = null, bool gzip = false, DateTimeOffset? date = null)
        {
            byte[] sent = body;
            if (gzip)
            {
                using var packed = new MemoryStream();
                using (var zip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true)) zip.Write(body, 0, body.Length);
                sent = packed.ToArray();
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(sent) };
            if (gzip) response.Content.Headers.ContentEncoding.Add("gzip");
            if (etag != null) response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
            if (date != null) response.Headers.Date = date;
            return response;
        }

        private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
        {
            var response = new HttpResponseMessage(status);
            if (location != null) response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return response;
        }

        private static Task<ControllerDatabaseFetch> Fetch(Transport transport, string etag = null, CancellationToken ct = default) =>
            ControllerDatabaseUpdater.FetchAsync(etag, transport.Send, Stall, Deadline, ct);

        private static readonly byte[] Body = Encoding.UTF8.GetBytes("# Game Controller DB\n030000004c050000b20b000000000000,Pad,a:b0,platform:Windows,\n");

        // ── What is asked ──

        [Fact]
        public async Task TheRequest_GoesToTheOneAddress_AsksForGzip_AndNamesNoTagTheFirstTime()
        {
            var transport = new Transport().Then(() => Ok(Body));
            var fetch = await Fetch(transport);
            var asked = Assert.Single(transport.Asked);
            Assert.Equal("https://raw.githubusercontent.com/mdqinc/SDL_GameControllerDB/master/gamecontrollerdb.txt", asked.Address.AbsoluteUri);
            Assert.Equal("GET", asked.Method);
            Assert.Equal("gzip", asked.AcceptEncoding);
            Assert.Equal(string.Empty, asked.IfNoneMatch);
            Assert.Equal(ControllerDatabaseFetchOutcome.Fetched, fetch.Outcome);
            Assert.Equal(Body, fetch.Content);
        }

        [Theory]
        [InlineData("W/\"0a1b2c3d\"")]
        [InlineData("\"0a1b2c3d\"")]
        public async Task TheStoredTag_IsSentBack(string etag)
        {
            var transport = new Transport().Then(() => new HttpResponseMessage(HttpStatusCode.NotModified));
            var fetch = await Fetch(transport, etag);
            Assert.Equal(etag, Assert.Single(transport.Asked).IfNoneMatch);
            Assert.Equal(ControllerDatabaseFetchOutcome.NotModified, fetch.Outcome);
            Assert.Null(fetch.Content);
        }

        /// <summary>Text that is no tag is not put in a header.</summary>
        [Theory]
        [InlineData("not a tag")]
        [InlineData("")]
        public async Task ATagThatIsNoTag_IsNotSent(string etag)
        {
            var transport = new Transport().Then(() => Ok(Body));
            await Fetch(transport, etag);
            Assert.Equal(string.Empty, Assert.Single(transport.Asked).IfNoneMatch);
        }

        // ── What is taken ──

        [Fact]
        public async Task AnAnswerOf200_GivesTheBodyItsTagAndGitHubsTime()
        {
            var when = new DateTimeOffset(2026, 11, 23, 14, 17, 35, TimeSpan.Zero);
            var transport = new Transport().Then(() => Ok(Body, "W/\"new-tag\"", date: when));
            var fetch = await Fetch(transport, "W/\"old-tag\"");
            Assert.Equal(ControllerDatabaseFetchOutcome.Fetched, fetch.Outcome);
            Assert.Equal(Body, fetch.Content);
            Assert.Equal("W/\"new-tag\"", fetch.ETag);
            Assert.Equal(when.UtcDateTime, fetch.ServerTimeUtc);
            Assert.Equal(DateTimeKind.Utc, fetch.ServerTimeUtc.Value.Kind);
        }

        [Fact]
        public async Task AGzipBody_IsDecodedHere()
        {
            byte[] large = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("030000004c050000b20b000000000000,Pad,a:b0,platform:Windows,\n", 5000)));
            var transport = new Transport().Then(() => Ok(large, gzip: true));
            var fetch = await Fetch(transport);
            Assert.Equal(ControllerDatabaseFetchOutcome.Fetched, fetch.Outcome);
            Assert.Equal(large, fetch.Content);
            Assert.Null(fetch.ETag);
            Assert.Null(fetch.ServerTimeUtc);
        }

        [Fact]
        public async Task AnEncodingThatWasNotAskedFor_IsRefused()
        {
            var transport = new Transport().Then(() =>
            {
                var response = Ok(Body);
                response.Content.Headers.ContentEncoding.Add("br");
                return response;
            });
            var fetch = await Fetch(transport);
            Assert.Equal(ControllerDatabaseFetchOutcome.Refused, fetch.Outcome);
            Assert.Null(fetch.Content);
        }

        [Fact]
        public async Task ABodyMarkedGzipThatIsNot_IsRefused()
        {
            var transport = new Transport().Then(() =>
            {
                var response = Ok(Body);
                response.Content.Headers.ContentEncoding.Add("gzip");
                return response;
            });
            Assert.Equal(ControllerDatabaseFetchOutcome.Refused, (await Fetch(transport)).Outcome);
        }

        // ── Where the reading stops ──

        /// <summary>A stream that says how long it is only when asked to,
        /// and counts what was read of it.</summary>
        private sealed class CountedContent : HttpContent
        {
            private readonly long _length;
            private readonly long? _declared;
            public long Read;

            public CountedContent(long length, long? declared)
            {
                _length = length;
                _declared = declared;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) => throw new NotSupportedException();

            protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new Zeros(this));

            protected override bool TryComputeLength(out long length)
            {
                length = _declared ?? 0;
                return _declared != null;
            }

            private sealed class Zeros : Stream
            {
                private readonly CountedContent _owner;
                public Zeros(CountedContent owner) => _owner = owner;
                public override bool CanRead => true;
                public override bool CanSeek => false;
                public override bool CanWrite => false;
                public override long Length => throw new NotSupportedException();
                public override long Position { get => _owner.Read; set => throw new NotSupportedException(); }
                public override void Flush() { }
                public override int Read(byte[] buffer, int offset, int count)
                {
                    int n = (int)Math.Min(count, _owner._length - _owner.Read);
                    Array.Fill(buffer, (byte)'#', offset, n);
                    _owner.Read += n;
                    return n;
                }
                public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
                public override void SetLength(long value) => throw new NotSupportedException();
                public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            }
        }

        /// <summary>A Content-Length over the cap is refused before a byte
        /// of the body is read.</summary>
        [Fact]
        public async Task AContentLengthOverTheCap_IsRefusedUnread()
        {
            var content = new CountedContent(ControllerDatabaseDownload.MaxBytes + 1L, ControllerDatabaseDownload.MaxBytes + 1L);
            var transport = new Transport().Then(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            var fetch = await Fetch(transport);
            Assert.Equal(ControllerDatabaseFetchOutcome.Refused, fetch.Outcome);
            Assert.Equal(0, content.Read);
        }

        /// <summary>A body that names no length, or a shorter one than it
        /// sends, is read up to the cap and no further.</summary>
        [Fact]
        public async Task ABodyOverTheCap_IsReadToTheCapAndRefused()
        {
            var content = new CountedContent(64L * 1024 * 1024, declared: null);
            var transport = new Transport().Then(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            var fetch = await Fetch(transport);
            Assert.Equal(ControllerDatabaseFetchOutcome.Refused, fetch.Outcome);
            Assert.Null(fetch.Content);
            Assert.InRange(content.Read, ControllerDatabaseDownload.MaxBytes, ControllerDatabaseDownload.MaxBytes + 2 * 81920);
        }

        [Fact]
        public async Task ABodyAtTheCap_IsRead()
        {
            var content = new CountedContent(ControllerDatabaseDownload.MaxBytes, declared: null);
            var transport = new Transport().Then(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            var fetch = await Fetch(transport);
            Assert.Equal(ControllerDatabaseFetchOutcome.Fetched, fetch.Outcome);
            Assert.Equal(ControllerDatabaseDownload.MaxBytes, fetch.Content.Length);
        }

        /// <summary>The cap counts what comes out of gzip: a few kilobytes
        /// on the wire that unpack to more than 4 MB are refused.</summary>
        [Fact]
        public async Task AGzipBodyThatUnpacksPastTheCap_IsRefused()
        {
            byte[] zeros = new byte[ControllerDatabaseDownload.MaxBytes + 1];
            HttpResponseMessage answer = Ok(zeros, gzip: true);
            Assert.True(answer.Content.Headers.ContentLength < 100 * 1024, "the test body is not small on the wire");
            var transport = new Transport().Then(() => answer);
            var fetch = await Fetch(transport);
            Assert.Equal(ControllerDatabaseFetchOutcome.Refused, fetch.Outcome);
            Assert.Null(fetch.Content);
        }

        // ── Redirects ──

        [Theory]
        [InlineData(HttpStatusCode.MovedPermanently, "https://raw.githubusercontent.com/mdqinc/SDL_GameControllerDB/main/gamecontrollerdb.txt")]
        [InlineData(HttpStatusCode.Found, "/mdqinc/SDL_GameControllerDB/main/gamecontrollerdb.txt")]
        [InlineData(HttpStatusCode.TemporaryRedirect, "https://RAW.githubusercontent.com/other/path.txt")]
        [InlineData(HttpStatusCode.PermanentRedirect, "../../main/gamecontrollerdb.txt")]
        public async Task ARedirectOnTheHost_IsFollowed_WithTheSameQuestion(HttpStatusCode status, string location)
        {
            var transport = new Transport().Then(() => Redirect(status, location)).Then(() => Ok(Body, "\"moved\""));
            var fetch = await Fetch(transport, "W/\"old-tag\"");
            Assert.Equal(ControllerDatabaseFetchOutcome.Fetched, fetch.Outcome);
            Assert.Equal(2, transport.Asked.Count);
            Assert.Equal("raw.githubusercontent.com", transport.Asked[1].Address.Host);
            Assert.Equal(Uri.UriSchemeHttps, transport.Asked[1].Address.Scheme);
            Assert.Equal("W/\"old-tag\"", transport.Asked[1].IfNoneMatch);
            Assert.Equal("gzip", transport.Asked[1].AcceptEncoding);
        }

        /// <summary>A redirect off the host, to plain HTTP or to another
        /// port is refused, and the second request is never sent.</summary>
        [Theory]
        [InlineData("https://example.com/gamecontrollerdb.txt")]
        [InlineData("https://raw.githubusercontent.com.example.com/gamecontrollerdb.txt")]
        [InlineData("https://github.com/mdqinc/SDL_GameControllerDB/raw/master/gamecontrollerdb.txt")]
        [InlineData("http://raw.githubusercontent.com/mdqinc/SDL_GameControllerDB/master/gamecontrollerdb.txt")]
        [InlineData("https://raw.githubusercontent.com:8443/mdqinc/SDL_GameControllerDB/master/gamecontrollerdb.txt")]
        [InlineData("//example.com/gamecontrollerdb.txt")]
        [InlineData("ftp://raw.githubusercontent.com/gamecontrollerdb.txt")]
        public async Task ARedirectOffTheHost_IsRefused_AndNotFollowed(string location)
        {
            var transport = new Transport().Then(() => Redirect(HttpStatusCode.Found, location));
            var fetch = await Fetch(transport);
            Assert.Equal(ControllerDatabaseFetchOutcome.Refused, fetch.Outcome);
            Assert.Single(transport.Asked);
            Assert.False(ControllerDatabaseUpdater.IsTrusted(new Uri(new Uri(ControllerDatabaseSection.SourceUrl), location)));
        }

        [Fact]
        public async Task ARedirectWithNoAddress_AndAFourthRedirect_AreRefused()
        {
            var bare = new Transport().Then(() => Redirect(HttpStatusCode.Found, null));
            Assert.Equal(ControllerDatabaseFetchOutcome.Refused, (await Fetch(bare)).Outcome);

            const string again = "https://raw.githubusercontent.com/mdqinc/SDL_GameControllerDB/master/gamecontrollerdb.txt";
            var loop = new Transport();
            for (int i = 0; i <= ControllerDatabaseUpdater.MaxRedirects; i++) loop.Then(() => Redirect(HttpStatusCode.Found, again));
            Assert.Equal(ControllerDatabaseFetchOutcome.Refused, (await Fetch(loop)).Outcome);
            Assert.Equal(ControllerDatabaseUpdater.MaxRedirects + 1, loop.Asked.Count);
        }

        [Theory]
        [InlineData("https://raw.githubusercontent.com/a", true)]
        [InlineData("https://RAW.GitHubUserContent.com/a", true)]
        [InlineData("https://raw.githubusercontent.com:443/a", true)]
        [InlineData("http://raw.githubusercontent.com/a", false)]
        [InlineData("https://raw.githubusercontent.com:444/a", false)]
        [InlineData("https://githubusercontent.com/a", false)]
        [InlineData("https://xraw.githubusercontent.com/a", false)]
        [InlineData("https://raw.githubusercontent.com@example.com/a", false)]
        public void OnlyTheDatabasesHostOverHttps_IsTrusted(string address, bool trusted)
        {
            Assert.Equal(trusted, ControllerDatabaseUpdater.IsTrusted(new Uri(address)));
            Assert.False(ControllerDatabaseUpdater.IsTrusted(null));
            Assert.False(ControllerDatabaseUpdater.IsTrusted(new Uri("/a", UriKind.Relative)));
        }

        // ── No answer ──

        [Theory]
        [InlineData(HttpStatusCode.NotFound, "HTTP 404")]
        [InlineData(HttpStatusCode.TooManyRequests, "HTTP 429")]
        [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
        [InlineData(HttpStatusCode.NoContent, "HTTP 204")]
        [InlineData(HttpStatusCode.PartialContent, "HTTP 206")]
        public async Task AnyOtherStatus_IsNoAnswer(HttpStatusCode status, string reason)
        {
            var transport = new Transport().Then(() => new HttpResponseMessage(status) { Content = new ByteArrayContent(Body) });
            var fetch = await Fetch(transport);
            Assert.Equal(ControllerDatabaseFetchOutcome.Unreachable, fetch.Outcome);
            Assert.Equal(reason, fetch.Reason);
            Assert.False(fetch.Stalled);
            Assert.Null(fetch.Content);
        }

        [Fact]
        public async Task ARequestThatReachesNoServer_IsNoAnswer_WithTheNetworksWord()
        {
            Task<HttpResponseMessage> Fails(HttpRequestMessage r, CancellationToken c) =>
                Task.FromException<HttpResponseMessage>(new HttpRequestException("No such host is known."));
            var fetch = await ControllerDatabaseUpdater.FetchAsync(null, Fails, Stall, Deadline, CancellationToken.None);
            Assert.Equal(ControllerDatabaseFetchOutcome.Unreachable, fetch.Outcome);
            Assert.Equal("No such host is known.", fetch.Reason);
            Assert.False(fetch.Stalled);
        }

        /// <summary>A body that stops arriving ends the fetch at the read's
        /// deadline.</summary>
        [Fact]
        public async Task ABodyThatStopsArriving_IsAStall()
        {
            var transport = new Transport().Then(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverArrives()) });
            var fetch = await ControllerDatabaseUpdater.FetchAsync(null, transport.Send, TimeSpan.FromMilliseconds(100), Deadline, CancellationToken.None);
            Assert.Equal(ControllerDatabaseFetchOutcome.Unreachable, fetch.Outcome);
            Assert.True(fetch.Stalled);
        }

        /// <summary>A body that keeps arriving a byte at a time never meets
        /// a read's deadline. The whole fetch has its own, and ends
        /// there.</summary>
        [Fact]
        public async Task ABodyThatDrips_EndsAtTheFetchsOwnDeadline()
        {
            var transport = new Transport().Then(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Drips()) });
            var fetch = await ControllerDatabaseUpdater.FetchAsync(null, transport.Send, Stall, TimeSpan.FromMilliseconds(300), CancellationToken.None);
            Assert.Equal(ControllerDatabaseFetchOutcome.Unreachable, fetch.Outcome);
            Assert.True(fetch.Stalled);
            Assert.Null(fetch.Content);
        }

        /// <summary>One byte every 20 ms, for as long as it is read.</summary>
        private sealed class Drips : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(20, cancellationToken);
                buffer.Span[0] = (byte)'#';
                return 1;
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>HttpClient's own timeout, while it waits for GitHub's
        /// answer, reads as the same thing.</summary>
        [Fact]
        public async Task ATimeoutBeforeTheAnswer_IsAStall()
        {
            Task<HttpResponseMessage> TimesOut(HttpRequestMessage r, CancellationToken c) =>
                Task.FromException<HttpResponseMessage>(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."));
            var fetch = await ControllerDatabaseUpdater.FetchAsync(null, TimesOut, Stall, Deadline, CancellationToken.None);
            Assert.Equal(ControllerDatabaseFetchOutcome.Unreachable, fetch.Outcome);
            Assert.True(fetch.Stalled);
        }

        /// <summary>Closing PadForge cancels the fetch, and that is no
        /// answer to report.</summary>
        [Fact]
        public async Task ACancel_EndsTheFetch_AsACancel()
        {
            using var cts = new CancellationTokenSource();
            var transport = new Transport().Then(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverArrives()) });
            var running = ControllerDatabaseUpdater.FetchAsync(null, transport.Send, Stall, Deadline, cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

            using var before = new CancellationTokenSource();
            before.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fetch(new Transport(), ct: before.Token));
        }

        private sealed class NeverArrives : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // ── The real transport, in the test host ──

        /// <summary>The client is made for one member, which asks the
        /// outbound gate before it sends. Read from the source first, so a
        /// build with the guard taken out fails here and the call below is
        /// never made.</summary>
        [Fact]
        public async Task TheRealRequest_IsRefusedInTheTestHost_BeforeItIsSent()
        {
            FenceSource.AsksFirst(TestHostFenceTests.DatabaseUpdater, TestHostFenceTests.DatabaseSend, TestHostFenceTests.DatabaseGuard);
            string source = OffLimitsSource.Read(TestHostFenceTests.DatabaseUpdater);
            string send = FenceSource.Body(source, TestHostFenceTests.DatabaseSend);
            Assert.Single(Regex.Matches(source, @"\bLazyClient\.Value\b"));
            Assert.Contains("LazyClient.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)", send, StringComparison.Ordinal);
            // The handler follows no redirect and unpacks nothing itself.
            Assert.Contains("AllowAutoRedirect = false,", source, StringComparison.Ordinal);
            Assert.Contains("AutomaticDecompression = DecompressionMethods.None,", source, StringComparison.Ordinal);
            // And it keeps no cookie: the request carries the version and the tag alone.
            Assert.Contains("UseCookies = false,", source, StringComparison.Ordinal);
            // The updater's client conventions: its User-Agent and its wait.
            Assert.Contains("new ProductInfoHeaderValue(\"PadForge\", BuildIdentity.VersionText(BuildIdentity.Version))", source, StringComparison.Ordinal);
            Assert.Contains("Timeout = TimeSpan.FromSeconds(30)", source, StringComparison.Ordinal);
            Assert.Contains("FetchAsync(etag, SendAsync, UpdateService.DownloadStallTimeout, FetchDeadline, ct)", source, StringComparison.Ordinal);

            Assert.True(PadForge.Engine.Common.OutboundNetworkGate.RefusesHost(ControllerDatabaseUpdater.Host));
            var fetch = await ControllerDatabaseUpdater.FetchAsync(null, CancellationToken.None);
            Assert.Equal(ControllerDatabaseFetchOutcome.Unreachable, fetch.Outcome);
            Assert.StartsWith("The test host sends no request beyond this PC", fetch.Reason, StringComparison.Ordinal);
        }

        /// <summary>The updater writes nothing: no file, no folder, no temp
        /// path. What it fetches is held in memory until the settings are
        /// saved.</summary>
        [Theory]
        [InlineData("PadForge.App/Services/ControllerDatabaseUpdater.cs")]
        [InlineData("PadForge.App/Services/ControllerDatabaseController.cs")]
        [InlineData("PadForge.App/Common/Input/ControllerDatabaseDownload.cs")]
        [InlineData("PadForge.App/Common/Input/ControllerDatabaseSection.cs")]
        [InlineData("PadForge.App/Common/Input/ControllerDatabaseRow.cs")]
        [InlineData("PadForge.App/Common/Input/ControllerDatabaseUse.cs")]
        [InlineData("PadForge.App/Common/Input/ControllerDatabaseStatus.cs")]
        [InlineData("PadForge.App/Common/Input/ControllerDatabaseBaseline.cs")]
        [InlineData("PadForge.App/Common/Input/InputManager.ControllerDatabase.cs")]
        public void NoPartOfTheFeature_WritesAFile(string file)
        {
            string code = string.Join("\n", OffLimitsSource.Read(file).Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            Assert.DoesNotMatch(@"\bFile\.|\bFileStream\b|\bDirectory\.|\bPath\.GetTemp|\bStreamWriter\b|\bFileInfo\b|\bIsolatedStorage", code);
        }
    }
}
