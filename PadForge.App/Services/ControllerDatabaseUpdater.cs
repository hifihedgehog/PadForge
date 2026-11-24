using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;

namespace PadForge.Services
{
    internal enum ControllerDatabaseFetchOutcome
    {
        /// <summary>GitHub sent the file.</summary>
        Fetched,
        /// <summary>GitHub answered 304: the file is the one the stored tag
        /// names.</summary>
        NotModified,
        /// <summary>No answer, a failed connection, a stalled download or an
        /// HTTP error.</summary>
        Unreachable,
        /// <summary>An answer PadForge does not take: a redirect off the
        /// host, a body over the cap, or an encoding it did not ask for.</summary>
        Refused,
    }

    /// <summary>What one request for the community database came to.
    /// <see cref="Reason"/> is the network's own word for a failure, null
    /// for a stall, and for a refusal what was refused, for the log.</summary>
    internal sealed record ControllerDatabaseFetch(ControllerDatabaseFetchOutcome Outcome, byte[] Content = null,
        string ETag = null, DateTime? ServerTimeUtc = null, string Reason = null, bool Stalled = false);

    /// <summary>
    /// Fetches the community controller database for Settings, Updates,
    /// Controller Database (#597). One fixed HTTPS address on
    /// raw.githubusercontent.com, asked only when the user presses Update
    /// Now. Nothing is written to disk here: the bytes go to
    /// <see cref="ControllerDatabaseDownload"/>, and what is kept goes into
    /// PadForge.xml at the next settings save.
    ///
    /// <para>The client follows <see cref="UpdateService"/>'s: the same
    /// User-Agent, 30 seconds for GitHub's answer, and a deadline on every
    /// read of the body. Three things differ. No redirect is followed by
    /// the handler, so each is read here and taken only to the same host
    /// over HTTPS. The body is decoded here, so the 4 MB cap counts the
    /// bytes that come out of gzip and not the ones on the wire. And the
    /// whole fetch has a deadline of its own.</para>
    /// </summary>
    internal static class ControllerDatabaseUpdater
    {
        internal const string Host = "raw.githubusercontent.com";

        /// <summary>How many redirects on the host are followed.</summary>
        internal const int MaxRedirects = 3;

        /// <summary>How long one fetch may take in all. The deadline on each
        /// read ends a connection that stops. This one ends a connection
        /// that sends a byte at a time, which would keep the button on
        /// Updating for as long as it cared to. The file is about 0.6 MB,
        /// and 50 KB as gzip.</summary>
        internal static readonly TimeSpan FetchDeadline = TimeSpan.FromMinutes(5);

        private static readonly Lazy<HttpClient> LazyClient = new(CreateClient);

        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                // The request carries the version and the stored tag, and
                // nothing a server set earlier.
                UseCookies = false,
            };
            // HttpClient's timeout ends once the headers arrive when the body
            // is streamed, so it bounds the wait for GitHub's answer alone.
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("PadForge", BuildIdentity.VersionText(BuildIdentity.Version)));
            return http;
        }

        /// <summary>The one request this class sends beyond the PC. In the
        /// test host it fails before it is sent, as a request that reached
        /// no server does.</summary>
        private static Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (PadForge.Engine.Common.OutboundNetworkGate.RefusesHost(request.RequestUri.IdnHost))
                throw new HttpRequestException("The test host sends no request beyond this PC: " + request.RequestUri.Host);
            return LazyClient.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        /// <summary>Asks GitHub for the file. <paramref name="etag"/> is the
        /// tag of the copy the settings hold, or null.</summary>
        internal static Task<ControllerDatabaseFetch> FetchAsync(string etag, CancellationToken ct) =>
            FetchAsync(etag, SendAsync, UpdateService.DownloadStallTimeout, FetchDeadline, ct);

        /// <summary>Only the database's host, over HTTPS, on the default
        /// port.</summary>
        internal static bool IsTrusted(Uri address) =>
            address != null && address.IsAbsoluteUri && address.Scheme == Uri.UriSchemeHttps
            && address.IsDefaultPort && string.Equals(address.IdnHost, Host, StringComparison.OrdinalIgnoreCase);

        internal static async Task<ControllerDatabaseFetch> FetchAsync(string etag,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, TimeSpan stallTimeout, TimeSpan deadline,
            CancellationToken caller)
        {
            var address = new Uri(ControllerDatabaseSection.SourceUrl);
            // Ends with the caller's token or at the deadline, whichever
            // comes first. Only the caller's own cancel leaves as a cancel.
            using var whole = CancellationTokenSource.CreateLinkedTokenSource(caller);
            whole.CancelAfter(deadline);
            var ct = whole.Token;
            try
            {
                for (int hop = 0; ; hop++)
                {
                    if (!IsTrusted(address))
                        return Refused("an address off " + Host + ": " + address.GetLeftPart(UriPartial.Authority));
                    using var request = new HttpRequestMessage(HttpMethod.Get, address);
                    request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
                    if (etag != null && EntityTagHeaderValue.TryParse(etag, out var tag))
                        request.Headers.IfNoneMatch.Add(tag);
                    using var response = await send(request, ct).ConfigureAwait(false);

                    int status = (int)response.StatusCode;
                    if (status == 304) return new ControllerDatabaseFetch(ControllerDatabaseFetchOutcome.NotModified);
                    if (status is 301 or 302 or 303 or 307 or 308)
                    {
                        var location = response.Headers.Location;
                        if (location == null) return Refused("a redirect with no address");
                        if (hop == MaxRedirects) return Refused("more than " + MaxRedirects.ToString(CultureInfo.InvariantCulture) + " redirects");
                        address = location.IsAbsoluteUri ? location : new Uri(address, location);
                        continue;
                    }
                    if (status != 200)
                        return new ControllerDatabaseFetch(ControllerDatabaseFetchOutcome.Unreachable,
                            Reason: "HTTP " + status.ToString(CultureInfo.InvariantCulture));

                    long? length = response.Content.Headers.ContentLength;
                    if (length > ControllerDatabaseDownload.MaxBytes)
                        return Refused("a Content-Length of " + length.Value.ToString(CultureInfo.InvariantCulture));
                    var encodings = response.Content.Headers.ContentEncoding;
                    bool gzip = encodings.Count == 1 && encodings.Contains("gzip");
                    if (!gzip && encodings.Count != 0 && !(encodings.Count == 1 && encodings.Contains("identity")))
                        return Refused("the encoding " + string.Join(", ", encodings));

                    await using var raw = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var body = gzip ? new GZipStream(raw, CompressionMode.Decompress) : raw;
                    byte[] content = await ReadCappedAsync(body, ControllerDatabaseDownload.MaxBytes, stallTimeout, ct).ConfigureAwait(false);
                    if (content == null)
                        return Refused("more than " + ControllerDatabaseDownload.MaxBytes.ToString(CultureInfo.InvariantCulture) + " bytes");
                    return new ControllerDatabaseFetch(ControllerDatabaseFetchOutcome.Fetched, content,
                        response.Headers.ETag?.ToString(), response.Headers.Date?.UtcDateTime);
                }
            }
            catch (OperationCanceledException) when (caller.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or UpdateDownloadStalledException)
            {
                // HttpClient's own timeout while it waited for GitHub's
                // answer, a read of the body past its deadline, or the
                // whole fetch past its own.
                return new ControllerDatabaseFetch(ControllerDatabaseFetchOutcome.Unreachable, Stalled: true);
            }
            catch (InvalidDataException ex)
            {
                return Refused("a body that is not gzip: " + ex.Message);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                return new ControllerDatabaseFetch(ControllerDatabaseFetchOutcome.Unreachable, Reason: ex.Message);
            }
        }

        private static ControllerDatabaseFetch Refused(string what) =>
            new(ControllerDatabaseFetchOutcome.Refused, Reason: what);

        /// <summary>
        /// Reads a body into memory and stops at <paramref name="cap"/>: null
        /// when the body holds more. Every read has its own deadline, as
        /// <see cref="UpdateService.CopyAndHashAsync"/> gives each of its
        /// reads, so a connection that stops without a reset ends the fetch.
        /// </summary>
        internal static async Task<byte[]> ReadCappedAsync(Stream source, int cap, TimeSpan stallTimeout, CancellationToken ct)
        {
            using var held = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                int read;
                using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    stall.CancelAfter(stallTimeout);
                    try
                    {
                        read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), stall.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new UpdateDownloadStalledException();
                    }
                }
                if (read == 0) return held.ToArray();
                if (held.Length + read > cap) return null;
                held.Write(buffer, 0, read);
            }
        }
    }
}
