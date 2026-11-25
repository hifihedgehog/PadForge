using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Services.Automation.Web
{
    /// <summary>
    /// The web helper's front door (#600 item 11): every HTTP request goes
    /// through <see cref="HandleAsync"/>, which checks Host and Origin,
    /// works out the base address the request came by, and routes it to the
    /// metadata, the sign-in, the token endpoint or the MCP endpoint.
    ///
    /// <para>Host and Origin are checked against the DNS rebinding attack
    /// the transport specification names ("Servers MUST validate the Origin
    /// header on all incoming connections"). Host must be this helper's own
    /// loopback address or the public address the user picked. Origin, when a
    /// request carries one, must be the origin of the address the request
    /// came by, or the answer is 403.</para>
    ///
    /// <para>The base address is never read from the listener: a request
    /// that came by the loopback address is served as
    /// <c>http://127.0.0.1:port</c>, one that came by the tunnel as the
    /// public address. The issuer, the resource, the metadata's address and
    /// a token's audience all come from that one string.</para>
    /// </summary>
    internal sealed class WebFront
    {
        /// <summary>The most bytes of a body the door reads.</summary>
        internal const int LongestBody = 1024 * 1024, LongestFormBody = 64 * 1024;

        private readonly WebAuth _auth;
        private readonly WebMcp _mcp;
        private readonly IWebAppSide _app;
        private readonly Func<string> _publicBase;
        private readonly Func<DateTimeOffset> _now;
        private readonly Dictionary<string, DateTimeOffset> _usedSaid = new(StringComparer.Ordinal);

        /// <param name="port">The loopback port the helper listens on.</param>
        /// <param name="publicBase">The public address now, or null.</param>
        public WebFront(int port, WebAuth auth, WebMcp mcp, IWebAppSide app, Func<string> publicBase, Func<DateTimeOffset> now = null)
        {
            Port = port;
            _auth = auth;
            _mcp = mcp;
            _app = app;
            _publicBase = publicBase ?? (() => null);
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }

        public int Port { get; }

        /// <summary>The base address a request came by, or null when its Host
        /// is neither this helper's loopback address nor the public one.</summary>
        internal string BaseOf(WebRequest request)
        {
            string host = request.Header("Host");
            if (string.IsNullOrEmpty(host)) return null;
            string port = Port.ToString(CultureInfo.InvariantCulture);
            if (string.Equals(host, "127.0.0.1:" + port, StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "localhost:" + port, StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "[::1]:" + port, StringComparison.OrdinalIgnoreCase))
            {
                // A tunnel that hands on the loopback Host names the public
                // one it came by. A program on this PC that claims it gains
                // nothing, since a token works only for the address it was
                // issued for.
                string publicBase = _publicBase();
                string forwarded = request.Header("X-Forwarded-Host")?.Split(',')[0].Trim();
                if (publicBase != null && forwarded != null && string.Equals(AuthorityOf(publicBase), forwarded, StringComparison.OrdinalIgnoreCase))
                    return publicBase;
                return WebAddress.Loopback(Port);
            }
            string pub = _publicBase();
            if (pub != null && string.Equals(AuthorityOf(pub), host, StringComparison.OrdinalIgnoreCase)) return pub;
            return null;
        }

        private static string AuthorityOf(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Authority : null;

        internal async Task<WebReply> HandleAsync(WebRequest request, CancellationToken cancel)
        {
            string baseUrl = BaseOf(request);
            if (baseUrl == null) return WebReply.RpcError(403, -32000, "Forbidden: this Host is not PadForge's web address.");
            string origin = request.Header("Origin");
            if (origin != null && !string.Equals(origin.TrimEnd('/'), baseUrl, StringComparison.OrdinalIgnoreCase))
                return WebReply.RpcError(403, -32000, "Forbidden: this Origin is not PadForge's web address.");

            string path = request.Path ?? "/";
            string method = request.Method ?? "GET";
            switch (path)
            {
                case "/.well-known/oauth-protected-resource" + WebAddress.McpPath:
                case "/.well-known/oauth-protected-resource":
                    return method == "GET" ? _auth.ResourceMetadata(baseUrl) : NotAllowed("GET");
                case "/.well-known/oauth-authorization-server":
                    return method == "GET" ? _auth.ServerMetadata(baseUrl) : NotAllowed("GET");
                case "/register":
                    return method == "POST" ? _auth.Register(request.Body) : NotAllowed("POST");
                case "/authorize":
                    return method == "GET" ? await _auth.AuthorizeAsync(baseUrl, request.Query, cancel).ConfigureAwait(false) : NotAllowed("GET");
                case "/authorize/next":
                    return method == "GET" ? _auth.SignInNext(WebAddress.Form(request.Query, out _).GetValueOrDefault("sid")) : NotAllowed("GET");
                case "/token":
                    if (method != "POST") return NotAllowed("POST");
                    if (!(request.Header("Content-Type") ?? string.Empty).StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                        return WebReply.OAuthError(400, "invalid_request", "The token endpoint takes application/x-www-form-urlencoded.");
                    return _auth.Token(request.Body);
                case WebAddress.McpPath:
                    if (method != "POST") return NotAllowed("POST");
                    var grant = _auth.Verify(request.Header("Authorization"), baseUrl, out string problem);
                    if (grant == null)
                        return WebAuth.Unauthorized(baseUrl, problem == "none" ? null : problem,
                            problem == "none" ? null : "The token is unknown, expired, revoked or for another address.");
                    SayUsed(grant.Id);
                    return await _mcp.PostAsync(request, grant, cancel).ConfigureAwait(false);
                default:
                    return WebReply.RpcError(404, -32000, "Not found.");
            }
        }

        private static WebReply NotAllowed(string allow) => WebReply.RpcError(405, -32000, "Method Not Allowed.").With("Allow", allow);

        /// <summary>Tells the app a grant was used, at most once a minute.</summary>
        private void SayUsed(string id)
        {
            var now = _now();
            lock (_usedSaid)
            {
                if (_usedSaid.TryGetValue(id, out var said) && now - said < TimeSpan.FromMinutes(1)) return;
                _usedSaid[id] = now;
            }
            try { _app.Used(id); } catch { /* the request goes on */ }
        }
    }
}
