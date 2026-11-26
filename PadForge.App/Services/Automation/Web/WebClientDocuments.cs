using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Services.Automation.Web
{
    /// <summary>
    /// Fetches a web assistant's client ID metadata document (#600 item 11),
    /// the one thing the web helper reaches outside this PC for, and only
    /// while the user has armed Accept a New Web Assistant. The client
    /// registration page's security notes ask an authorization server to
    /// guard against request forgery: the address must be https on its
    /// default port, the name must resolve to a public address (no loopback,
    /// private, link-local or unique-local one, nor an IPv6 address that
    /// carries such an IPv4 one) and the connection goes to the address
    /// checked, no redirect is followed, and the document is cut at 16 KB
    /// and five seconds, its body's reads included.
    /// </summary>
    internal static class WebClientDocuments
    {
        internal const int LongestDocument = 16 * 1024;
        internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

        /// <summary>The document's text, or null.</summary>
        internal static async Task<string> FetchAsync(string address, CancellationToken cancel)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort) return null;
            // The test host reaches nothing beyond this PC.
            if (PadForge.Engine.Common.OutboundNetworkGate.RefusesHost(uri.Host)) return null;
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                ConnectTimeout = Wait,
                ConnectCallback = ConnectPublicAsync,
            };
            return await FetchAsync(uri, handler, Wait, cancel).ConfigureAwait(false);
        }

        /// <summary>The fetch itself over a handler, which a test gives. One
        /// wait covers it all, the headers and every read of the body:
        /// HttpClient's own Timeout ends at the headers when the body is
        /// read as a stream. The test host reaches nothing beyond this PC
        /// through it either.</summary>
        internal static async Task<string> FetchAsync(Uri uri, HttpMessageHandler handler, TimeSpan wait, CancellationToken cancel)
        {
            if (PadForge.Engine.Common.OutboundNetworkGate.RefusesHost(uri.Host)) return null;
            using var time = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            time.CancelAfter(wait);
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/json");
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, time.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK) return null;
                if (response.Content.Headers.ContentLength is long length && length > LongestDocument) return null;
                await using var stream = await response.Content.ReadAsStreamAsync(time.Token).ConfigureAwait(false);
                var buffer = new byte[LongestDocument + 1];
                int total = 0;
                while (total < buffer.Length)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(total), time.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    total += n;
                }
                return total > LongestDocument ? null : Encoding.UTF8.GetString(buffer, 0, total);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                // The wait ran out.
                return null;
            }
        }

        /// <summary>Resolves the host and connects only to a public
        /// address, so a name that points at this PC or the home network is
        /// never reached.</summary>
        private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancel)
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancel).ConfigureAwait(false);
            foreach (var address in addresses)
            {
                if (!IsPublic(address)) continue;
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancel).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                }
            }
            throw new HttpRequestException("The client document's host has no public address.");
        }

        /// <summary>Whether an address is on the public internet.</summary>
        internal static bool IsPublic(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = address.GetAddressBytes();
                return !(b[0] == 10 || b[0] == 127 || b[0] == 0
                         || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                         || (b[0] == 192 && b[1] == 168)
                         || (b[0] == 169 && b[1] == 254)
                         || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                         || b[0] >= 224);
            }
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                byte[] b = address.GetAddressBytes();
                if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC) return false;
                // RFC 8215's local-use NAT64 prefix, 64:ff9b:1::/48, reaches
                // a gateway's own network by its definition.
                if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4] == 0x00 && b[5] == 0x01) return false;
                // An IPv4 address carried inside is judged as itself: the
                // NAT64 well-known prefix 64:ff9b::/96 (RFC 6052 2.1) and
                // the IPv4-compatible ::/96 (RFC 4291 2.5.5.1) in the last
                // four bytes, 6to4 2002::/16 (RFC 3056 2) in bytes 2 to 5.
                bool zero = true;
                for (int i = 4; i < 12; i++) zero &= b[i] == 0;
                if (zero && ((b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B) || (b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0)))
                    return IsPublic(new IPAddress(new[] { b[12], b[13], b[14], b[15] }));
                if (b[0] == 0x20 && b[1] == 0x02) return IsPublic(new IPAddress(new[] { b[2], b[3], b[4], b[5] }));
                return true;
            }
            return false;
        }
    }
}
