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
    ///
    /// <para>A request to the network door (#600 item 53), the listener on
    /// this PC's network addresses, takes its own way in
    /// (<see cref="HandleNetworkAsync"/>): the MCP endpoint alone, with a key
    /// the user made at this PC, a Host that names this PC and its port,
    /// and an Origin, when it carries one, of that same address. It has no
    /// sign-in, metadata or token endpoint, since Claude Code refuses an
    /// OAuth sign-in over plain HTTP outside localhost, and a web
    /// assistant's token is not taken there, nor a key on the web
    /// door.</para>
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
        private readonly WebNetworkSide _network;
        private readonly Func<bool> _webOn;

        /// <param name="port">The loopback port the helper listens on.</param>
        /// <param name="publicBase">The public address now, or null.</param>
        /// <param name="network">The network door's keys, port and names,
        /// or null with no network door.</param>
        /// <param name="webOn">Whether Reach PadForge From the Web is on.
        /// While it is off the loopback door answers nothing. On when
        /// null.</param>
        public WebFront(int port, WebAuth auth, WebMcp mcp, IWebAppSide app, Func<string> publicBase, Func<DateTimeOffset> now = null,
            WebNetworkSide network = null, Func<bool> webOn = null)
        {
            Port = port;
            _auth = auth;
            _mcp = mcp;
            _app = app;
            _publicBase = publicBase ?? (() => null);
            _now = now ?? (() => DateTimeOffset.UtcNow);
            _network = network;
            _webOn = webOn ?? (() => true);
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
            if (request.Network) return await HandleNetworkAsync(request, cancel).ConfigureAwait(false);
            if (!_webOn()) return WebReply.RpcError(404, -32000, "Reach PadForge From the Web is off.");
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

        /// <summary>
        /// One request to the network door (#600 item 53). Host must name
        /// this PC (one of its private addresses, its computer name or its
        /// .local name) with the door's port, and Origin, when a browser
        /// sends one, must be that same address, as the web door checks it:
        /// a page on another site is refused. Every request needs a key the
        /// user made, sent as a Bearer header. A key the user revoked is
        /// refused at its next call, since the keys come from PadForge's
        /// state alone.
        /// </summary>
        internal async Task<WebReply> HandleNetworkAsync(WebRequest request, CancellationToken cancel)
        {
            var net = _network;
            if (net == null || !net.On) return WebReply.RpcError(404, -32000, "Reach PadForge From This Network is off.");
            string host = request.Header("Host");
            if (!net.NamesThisPc(host)) return WebReply.RpcError(403, -32000, "Forbidden: this Host is not this PC's address on the network.");
            string origin = request.Header("Origin");
            if (origin != null && !string.Equals(origin.TrimEnd('/'), "http://" + host, StringComparison.OrdinalIgnoreCase))
                return WebReply.RpcError(403, -32000, "Forbidden: this Origin is not PadForge's network address.");
            if ((request.Path ?? "/") != WebAddress.McpPath) return WebReply.RpcError(404, -32000, "Not found.");
            if (request.Method != "POST") return NotAllowed("POST");
            string presented = Bearer(request.Header("Authorization"));
            string id = presented == null ? null : net.Find(presented);
            if (id == null)
                return WebReply.RpcError(401, -32001, "A key made in PadForge is needed: Authorization: Bearer <key>.")
                    .With("WWW-Authenticate", presented == null ? "Bearer realm=\"PadForge\"" : "Bearer realm=\"PadForge\", error=\"invalid_token\"");
            SayUsed(WebNetworkSide.UsedId(id));
            string from = System.Net.IPAddress.TryParse(request.Remote ?? string.Empty, out var remote) ? remote.ToString() : "?";
            return await _mcp.PostAsync(request, new WebGrant { Id = id, Origin = from, Network = true, From = from }, cancel).ConfigureAwait(false);
        }

        /// <summary>The token of a Bearer header, or null.</summary>
        private static string Bearer(string header)
        {
            if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
            string token = header.Substring(7).Trim();
            return token.Length == 0 ? null : token;
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

    /// <summary>
    /// The network door as the web helper holds it (#600 item 53): whether
    /// it is on, its port, and the hashes of the keys the user made, as
    /// PadForge's last state said them. A key PadForge no longer lists is
    /// refused at its next call.
    /// </summary>
    internal sealed class WebNetworkSide
    {
        private volatile IReadOnlyList<(string Id, string Hash)> _keys = Array.Empty<(string, string)>();

        public volatile bool On;
        public volatile int Port;

        /// <summary>This PC's names, as a request's Host may give them.</summary>
        internal Func<IReadOnlyCollection<string>> Names { get; init; } = NetworkDoor.ThisPcNames;

        internal IReadOnlyList<(string Id, string Hash)> Keys
        {
            get => _keys;
            set => _keys = value ?? Array.Empty<(string, string)>();
        }

        /// <summary>The ID of the key a presented text is, or null. Every
        /// key is compared, each in fixed time.</summary>
        internal string Find(string presented)
        {
            string found = null;
            foreach (var (id, hash) in _keys)
                if (WebSecrets.Matches(presented, hash) && found == null) found = id;
            return found;
        }

        /// <summary>Whether a Host names this PC with the door's port.</summary>
        internal bool NamesThisPc(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            int colon = host.LastIndexOf(':');
            if (colon <= 0 || host.IndexOf(']') > colon) return false;
            if (!int.TryParse(host.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port != Port) return false;
            string name = host.Substring(0, colon);
            return Names().Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>How the app hears a key was used: its ID with a word of
        /// its own, so no web grant's ID is taken for it.</summary>
        internal static string UsedId(string keyId) => "net:" + keyId;
    }
}
