using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Services.Automation.Web
{
    /// <summary>One sign-in that waits for the user on the question card.</summary>
    /// <param name="Sid">The sign-in's own random ID, which its page carries.</param>
    /// <param name="Origin">The host its sign-in returns to: what it is named by.</param>
    /// <param name="Name">The name it gives, a claim, or null.</param>
    /// <param name="Code">Its six digits, shown on its page and on the card only.</param>
    /// <param name="Through">The host it reached PadForge through.</param>
    /// <param name="Loopback">Whether it returns to this PC, which any
    /// program here could claim.</param>
    internal sealed record WebSignIn(string Sid, string Origin, string Name, string Code, string Through, bool Loopback,
        string ClientId, string RedirectUri, string Resource);

    /// <summary>The app's side as the web helper reaches it (#600 item 11):
    /// the armed window, the allowed web assistants, the question card and
    /// the file. The helper decides nothing the user decides.</summary>
    internal interface IWebAppSide
    {
        /// <summary>Until when a new web assistant may sign in, or the
        /// minimum.</summary>
        DateTimeOffset ArmedUntil { get; }

        /// <summary>The web assistants allowed now.</summary>
        IReadOnlyCollection<WebGrant> Grants { get; }

        /// <summary>Asks the user on the question card. The new grant's ID
        /// when the user allowed it, null when not.</summary>
        Task<string> AskAsync(WebSignIn signIn, CancellationToken cancel);

        /// <summary>A grant's tokens changed: the app keeps the new hashes in
        /// PadForge.xml.</summary>
        void Tokens(WebGrant grant);

        /// <summary>A grant's token was used now.</summary>
        void Used(string grantId);

        /// <summary>A word of the sign-in pages, by its string key, in the
        /// interface language. PadForge's own strings by default, in
        /// PadForge's process. The helper's link gives the words the door
        /// sent it (WebPages).</summary>
        string Word(string key) => PadForge.Resources.Strings.Strings.Get(key);
    }

    /// <summary>
    /// The web helper's own OAuth 2.1 authorization server and the checks of
    /// its resource server (#600 item 11), as the MCP authorization
    /// specification (revision 2026-07-28, basic/authorization) asks:
    /// protected resource metadata (RFC 9728), authorization server metadata
    /// (RFC 8414), the authorization code grant with PKCE S256 only, client
    /// ID metadata documents with registration (RFC 7591) for older hosts,
    /// resource indicators (RFC 8707) so a token works only for the address it
    /// was issued for, the issuer in the authorization response (RFC 9207),
    /// and refresh tokens rotated on every use (OAuth 2.1 4.3.1, which the
    /// specification makes a MUST for public clients).
    ///
    /// <para>No password exists. A new web assistant signs in only while the
    /// user has armed Accept a New Web Assistant on the page. Each sign-in
    /// carries its own six-digit code, shown on its sign-in page and on the
    /// question card, and the user allows it there. Outside an armed window
    /// a registration and a sign-in are refused and nothing asks, and no
    /// client document is fetched.</para>
    /// </summary>
    internal sealed class WebAuth
    {
        /// <summary>How long an access token works.</summary>
        internal static readonly TimeSpan AccessLife = TimeSpan.FromHours(1);

        /// <summary>How long a refresh token keeps unused. Each use rotates
        /// it and starts the time again.</summary>
        internal static readonly TimeSpan RefreshLife = TimeSpan.FromDays(30);

        /// <summary>How long an authorization code works (OAuth 2.1 4.1.2:
        /// a maximum of 10 minutes is recommended, and a minute is plenty for
        /// a redirect).</summary>
        internal static readonly TimeSpan CodeLife = TimeSpan.FromSeconds(60);

        /// <summary>How long a sign-in's page waits for the user's answer:
        /// the card's own two minutes and a little more.</summary>
        internal static readonly TimeSpan SignInLife = TimeSpan.FromMinutes(3);

        /// <summary>The most registrations kept, and the most sign-ins that
        /// wait at once. Past them the oldest registration goes and a new
        /// sign-in is refused.</summary>
        internal const int MostRegistrations = 32, MostSignIns = 3;

        private readonly IWebAppSide _app;
        private readonly Func<DateTimeOffset> _now;
        private readonly Func<string, CancellationToken, Task<string>> _fetchDocument;
        private readonly object _sync = new object();
        private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Pending> _signIns = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IssuedCode> _codes = new(StringComparer.Ordinal);

        private sealed record Registration(string ClientId, IReadOnlyList<string> RedirectUris, string Name, DateTimeOffset Made);

        private sealed class Pending
        {
            public WebSignIn SignIn;
            public string State;
            public string Challenge;
            public DateTimeOffset Until;
            public Task<string> Answer;
            public string Issuer;
        }

        private sealed record IssuedCode(string GrantId, string ClientId, string RedirectUri, string Challenge, string Resource, DateTimeOffset Until);

        /// <param name="app">The app's side.</param>
        /// <param name="fetchDocument">Fetches a client ID metadata document
        /// by its address, its text or null. <see cref="WebClientDocuments.FetchAsync"/>
        /// when none is given.</param>
        /// <param name="now">The clock.</param>
        public WebAuth(IWebAppSide app, Func<string, CancellationToken, Task<string>> fetchDocument = null, Func<DateTimeOffset> now = null)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _fetchDocument = fetchDocument ?? WebClientDocuments.FetchAsync;
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }

        private bool Armed => _now() < _app.ArmedUntil;

        // ─────────────────────────────────────────────
        //  Metadata
        // ─────────────────────────────────────────────

        /// <summary>The protected resource metadata (RFC 9728 3.2) for a
        /// base address: the MCP endpoint as the resource, this helper as its
        /// one authorization server.</summary>
        internal WebReply ResourceMetadata(string baseUrl)
            => WebReply.JsonOf(200, new JsonObject
            {
                ["resource"] = baseUrl + WebAddress.McpPath,
                ["authorization_servers"] = new JsonArray(baseUrl),
                ["bearer_methods_supported"] = new JsonArray("header"),
                ["resource_name"] = "PadForge",
            });

        /// <summary>The authorization server metadata (RFC 8414 2) for a base
        /// address. The issuer is the base address exactly, as the clients
        /// compare it ordinally with the protected resource metadata's entry
        /// (csharp-sdk ClientOAuthProvider.GetAuthServerMetadataAsync, RFC
        /// 8414 3.3).</summary>
        internal WebReply ServerMetadata(string baseUrl)
            => WebReply.JsonOf(200, new JsonObject
            {
                ["issuer"] = baseUrl,
                ["authorization_endpoint"] = baseUrl + "/authorize",
                ["token_endpoint"] = baseUrl + "/token",
                ["registration_endpoint"] = baseUrl + "/register",
                ["response_types_supported"] = new JsonArray("code"),
                ["response_modes_supported"] = new JsonArray("query"),
                ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token"),
                ["code_challenge_methods_supported"] = new JsonArray("S256"),
                ["token_endpoint_auth_methods_supported"] = new JsonArray("none"),
                ["client_id_metadata_document_supported"] = true,
                ["authorization_response_iss_parameter_supported"] = true,
            });

        /// <summary>The 401 a request without a working token gets, with the
        /// address of the resource metadata (RFC 9728 5.1).</summary>
        internal static WebReply Unauthorized(string baseUrl, string error = null, string description = null)
        {
            string challenge = "Bearer resource_metadata=\"" + baseUrl + "/.well-known/oauth-protected-resource" + WebAddress.McpPath + "\""
                + (error == null ? string.Empty : ", error=\"" + error + "\"")
                + (description == null ? string.Empty : ", error_description=\"" + description.Replace("\"", "'") + "\"");
            return WebReply.RpcError(401, -32001, "Sign in to PadForge first.").With("WWW-Authenticate", challenge);
        }

        // ─────────────────────────────────────────────
        //  Registration (RFC 7591), for hosts without metadata documents
        // ─────────────────────────────────────────────

        internal WebReply Register(string body)
        {
            // Outside an armed window nothing new is taken in.
            if (!Armed) return WebReply.OAuthError(403, "access_denied", "PadForge is not accepting a new web assistant now.");
            JsonObject request;
            try { request = JsonNode.Parse(body ?? string.Empty) as JsonObject; }
            catch (JsonException) { request = null; }
            if (request == null) return WebReply.OAuthError(400, "invalid_client_metadata", "The body is not a JSON object.");

            var redirects = new List<string>();
            if (request["redirect_uris"] is JsonArray list)
                foreach (var item in list)
                    if (item is JsonValue v && v.TryGetValue(out string uri)) redirects.Add(uri);
            if (redirects.Count == 0 || redirects.Count > 8) return WebReply.OAuthError(400, "invalid_redirect_uri", "Give one to eight redirect addresses.");
            foreach (string uri in redirects)
                if (!WebAddress.IsRedirect(uri)) return WebReply.OAuthError(400, "invalid_redirect_uri", "A redirect address must be https, or http to this PC.");
            if (request["grant_types"] is JsonArray grants
                && grants.Any(g => g is JsonValue gv && gv.TryGetValue(out string gt) && gt is not ("authorization_code" or "refresh_token")))
                return WebReply.OAuthError(400, "invalid_client_metadata", "PadForge issues the authorization code and refresh token grants only.");
            string name = Text(request["client_name"]);

            var made = new Registration("pf-" + WebSecrets.New(16), redirects, Clip(name), _now());
            lock (_sync)
            {
                if (_registrations.Count >= MostRegistrations)
                {
                    var oldest = _registrations.Values.OrderBy(r => r.Made).First();
                    _registrations.Remove(oldest.ClientId);
                }
                _registrations[made.ClientId] = made;
            }
            // A public client: no secret is issued, and the token endpoint
            // takes the client ID alone. The C# client sends
            // client_secret_post unless the answer says none
            // (ClientOAuthProvider.PerformDynamicClientRegistrationAsync).
            var answer = new JsonObject
            {
                ["client_id"] = made.ClientId,
                ["client_id_issued_at"] = made.Made.ToUnixTimeSeconds(),
                ["redirect_uris"] = new JsonArray(redirects.Select(r => (JsonNode)r).ToArray()),
                ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
                ["response_types"] = new JsonArray("code"),
                ["token_endpoint_auth_method"] = "none",
            };
            if (name != null) answer["client_name"] = made.Name;
            return WebReply.JsonOf(201, answer).With("Cache-Control", "no-store");
        }

        // ─────────────────────────────────────────────
        //  Authorization
        // ─────────────────────────────────────────────

        /// <summary>
        /// The authorization endpoint (OAuth 2.1 4.1.1). Outside an armed
        /// window it refuses at once with a page and asks nothing. Otherwise
        /// it checks the client, its redirect address, PKCE and the resource,
        /// puts the question on the card with a fresh code, and answers with
        /// the sign-in page that shows the code and waits.
        /// </summary>
        internal async Task<WebReply> AuthorizeAsync(string baseUrl, string query, CancellationToken cancel)
        {
            var form = WebAddress.Form(query, out bool repeated);
            if (!Armed) return WebPages.Refused(_app, WebPages.NotAccepting(_app));
            if (repeated) return WebPages.Refused(_app, WebPages.CannotGoOn(_app, "a parameter was sent twice"));

            string clientId = Get(form, "client_id");
            string redirect = Get(form, "redirect_uri");
            if (string.IsNullOrEmpty(clientId)) return WebPages.Refused(_app, WebPages.CannotGoOn(_app, "no client_id"));

            // Who the client is: its metadata document, or its registration.
            // Until the redirect address is checked against them, nothing is
            // sent to it (OAuth 2.1 4.1.2.1).
            string name;
            IReadOnlyList<string> redirects;
            if (IsDocumentAddress(clientId))
            {
                string text;
                try { text = await _fetchDocument(clientId, cancel).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException) { text = null; }
                var document = ReadDocument(clientId, text);
                if (document == null) return WebPages.Refused(_app, WebPages.CannotGoOn(_app, "its client document could not be read"));
                (name, redirects) = document.Value;
            }
            else
            {
                Registration registration;
                lock (_sync) _registrations.TryGetValue(clientId, out registration);
                var granted = registration == null ? _app.Grants.FirstOrDefault(g => g.ClientId == clientId) : null;
                if (registration == null && granted == null) return WebPages.Refused(_app, WebPages.CannotGoOn(_app, "its client is not registered"));
                name = registration?.Name ?? granted.Name;
                redirects = registration?.RedirectUris ?? new[] { granted.RedirectUri };
            }
            // One registered address may be left out (OAuth 2.1 4.1.1), any
            // other must match exactly.
            if (string.IsNullOrEmpty(redirect) && redirects.Count == 1) redirect = redirects[0];
            if (string.IsNullOrEmpty(redirect) || !redirects.Contains(redirect, StringComparer.Ordinal) || !WebAddress.IsRedirect(redirect))
                return WebPages.Refused(_app, WebPages.CannotGoOn(_app, "its redirect address is not the one it registered"));

            // From here a refusal goes back to the client.
            string state = Get(form, "state");
            WebReply Back(string error, string words) => WebReply.Redirect(WebAddress.WithQuery(redirect, new[]
            {
                new KeyValuePair<string, string>("error", error),
                new KeyValuePair<string, string>("error_description", words),
                new KeyValuePair<string, string>("state", state),
                new KeyValuePair<string, string>("iss", baseUrl),
            }));
            if (Get(form, "response_type") != "code") return Back("unsupported_response_type", "PadForge issues authorization codes only.");
            if (Get(form, "code_challenge_method") != "S256" || !WebSecrets.IsChallenge(Get(form, "code_challenge")))
                return Back("invalid_request", "PKCE with S256 is required.");
            string resource = Get(form, "resource") ?? baseUrl + WebAddress.McpPath;
            if (!WebAddress.SameResource(resource, baseUrl + WebAddress.McpPath) && !WebAddress.SameResource(resource, baseUrl))
                return Back("invalid_target", "The resource is not this PadForge's address.");
            resource = baseUrl + WebAddress.McpPath;

            string origin = WebAddress.OriginOf(redirect);
            var signIn = new WebSignIn(WebSecrets.New(18), origin, name, WebSecrets.SixDigits(), WebAddress.HostOf(baseUrl),
                WebAddress.IsLoopbackHost(origin), clientId, redirect, resource);
            var pending = new Pending
            {
                SignIn = signIn,
                State = state,
                Challenge = Get(form, "code_challenge"),
                Until = _now() + SignInLife,
                Issuer = baseUrl,
            };
            lock (_sync)
            {
                Sweep();
                if (_signIns.Count >= MostSignIns) return Back("temporarily_unavailable", "Other sign-ins wait for the user. Try again in a few minutes.");
                _signIns[signIn.Sid] = pending;
            }
            pending.Answer = AskAsync(signIn);
            return WebPages.Waiting(_app, signIn, baseUrl);
        }

        private async Task<string> AskAsync(WebSignIn signIn)
        {
            using var clock = new CancellationTokenSource(SignInLife);
            try { return await _app.AskAsync(signIn, clock.Token).ConfigureAwait(false); }
            catch (Exception) { return null; }
        }

        /// <summary>What a sign-in's page shows next: the same page while the
        /// user has not answered, else the redirect back to the client with a
        /// code, or with access_denied. The code carries the issuer (RFC 9207
        /// 2), which the C# and TypeScript clients compare with the metadata's
        /// (ClientOAuthProvider.ValidateIssuerResponse,
        /// validateAuthorizationResponseIssuer).</summary>
        internal WebReply SignInNext(string sid)
        {
            Pending pending;
            lock (_sync)
            {
                Sweep();
                if (sid == null || !_signIns.TryGetValue(sid, out pending)) return WebPages.Refused(_app, WebPages.Expired(_app));
            }
            var answer = pending.Answer;
            if (answer == null || !answer.IsCompleted) return WebPages.Waiting(_app, pending.SignIn, pending.Issuer);
            lock (_sync) _signIns.Remove(sid);
            string grantId = answer.IsCompletedSuccessfully ? answer.Result : null;
            var s = pending.SignIn;
            if (grantId == null)
                return WebReply.Redirect(WebAddress.WithQuery(s.RedirectUri, new[]
                {
                    new KeyValuePair<string, string>("error", "access_denied"),
                    new KeyValuePair<string, string>("error_description", "The user did not allow it in PadForge."),
                    new KeyValuePair<string, string>("state", pending.State),
                    new KeyValuePair<string, string>("iss", pending.Issuer),
                }));
            string code = WebSecrets.New();
            lock (_sync) _codes[WebSecrets.Hash(code)] = new IssuedCode(grantId, s.ClientId, s.RedirectUri, pending.Challenge, s.Resource, _now() + CodeLife);
            return WebReply.Redirect(WebAddress.WithQuery(s.RedirectUri, new[]
            {
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("state", pending.State),
                new KeyValuePair<string, string>("iss", pending.Issuer),
            }));
        }

        /// <summary>The sign-ins that wait, for tests.</summary>
        internal int Waiting
        {
            get { lock (_sync) { Sweep(); return _signIns.Count; } }
        }

        private void Sweep()
        {
            var now = _now();
            foreach (var gone in _signIns.Where(p => p.Value.Until <= now).Select(p => p.Key).ToList()) _signIns.Remove(gone);
            foreach (var gone in _codes.Where(p => p.Value.Until <= now).Select(p => p.Key).ToList()) _codes.Remove(gone);
        }

        // ─────────────────────────────────────────────
        //  Tokens
        // ─────────────────────────────────────────────

        /// <summary>The token endpoint (OAuth 2.1 3.2): the authorization code
        /// grant with its PKCE verifier, and the refresh token grant, each
        /// with the resource it was issued for. A public client sends its
        /// client ID alone, and a client secret sent anyway is ignored.</summary>
        internal WebReply Token(string body)
        {
            var form = WebAddress.Form(body, out bool repeated);
            if (repeated) return WebReply.OAuthError(400, "invalid_request", "A parameter was sent twice.");
            return Get(form, "grant_type") switch
            {
                "authorization_code" => Exchange(form),
                "refresh_token" => Refresh(form),
                _ => WebReply.OAuthError(400, "unsupported_grant_type", "PadForge takes authorization_code and refresh_token."),
            };
        }

        private WebReply Exchange(Dictionary<string, string> form)
        {
            string code = Get(form, "code");
            IssuedCode issued = null;
            lock (_sync)
            {
                Sweep();
                // One use only (OAuth 2.1 4.1.2): taken out before it is checked.
                string key = WebSecrets.Hash(code);
                if (key != null && _codes.Remove(key, out var found)) issued = found;
            }
            if (issued == null) return WebReply.OAuthError(400, "invalid_grant", "The code is unknown, used or expired.");
            if (Get(form, "client_id") is { } clientId && clientId != issued.ClientId)
                return WebReply.OAuthError(400, "invalid_grant", "The code was issued to another client.");
            if (Get(form, "redirect_uri") is { } redirect && redirect != issued.RedirectUri)
                return WebReply.OAuthError(400, "invalid_grant", "The redirect address is not the sign-in's.");
            string verifier = Get(form, "code_verifier");
            if (!WebSecrets.IsVerifier(verifier) || WebSecrets.Challenge(verifier) != issued.Challenge)
                return WebReply.OAuthError(400, "invalid_grant", "The PKCE verifier does not match.");
            if (Get(form, "resource") is { } resource && !WebAddress.SameResource(resource, issued.Resource))
                return WebReply.OAuthError(400, "invalid_target", "The resource is not the one the code was issued for.");
            var grant = Grant(issued.GrantId);
            if (grant == null) return WebReply.OAuthError(400, "invalid_grant", "The user revoked it.");
            return Issue(grant with { ClientId = issued.ClientId, RedirectUri = issued.RedirectUri, Resource = issued.Resource });
        }

        private WebReply Refresh(Dictionary<string, string> form)
        {
            string token = Get(form, "refresh_token");
            var now = _now();
            var grant = Grants().FirstOrDefault(g => WebSecrets.Matches(token, g.RefreshHash));
            if (grant == null || grant.RefreshUntil <= now) return WebReply.OAuthError(400, "invalid_grant", "The refresh token is unknown, used or expired.");
            if (Get(form, "client_id") is { } clientId && clientId != grant.ClientId)
                return WebReply.OAuthError(400, "invalid_grant", "The refresh token was issued to another client.");
            if (Get(form, "resource") is { } resource && !WebAddress.SameResource(resource, grant.Resource))
                return WebReply.OAuthError(400, "invalid_target", "The resource is not the one the token was issued for.");
            return Issue(grant);
        }

        /// <summary>New access and refresh tokens for a grant. The old ones
        /// stop working at once, and only their hashes are kept.</summary>
        private WebReply Issue(WebGrant grant)
        {
            string access = WebSecrets.New(), refresh = WebSecrets.New();
            var now = _now();
            var changed = grant with
            {
                AccessHash = WebSecrets.Hash(access),
                AccessUntil = now + AccessLife,
                RefreshHash = WebSecrets.Hash(refresh),
                RefreshUntil = now + RefreshLife,
                LastUsed = now,
            };
            // The app keeps the hashes, and the helper's copy of the list
            // holds them from this call on.
            _app.Tokens(changed);
            return WebReply.JsonOf(200, new JsonObject
            {
                ["access_token"] = access,
                ["token_type"] = "Bearer",
                ["expires_in"] = (long)AccessLife.TotalSeconds,
                ["refresh_token"] = refresh,
            }).With("Cache-Control", "no-store").With("Pragma", "no-cache");
        }

        // ─────────────────────────────────────────────
        //  The resource server's check
        // ─────────────────────────────────────────────

        /// <summary>
        /// The grant a bearer token works for at a base address, or null
        /// with the reason. A token works only while it lives, only for the
        /// address it was issued for (RFC 8707: "MCP servers MUST validate
        /// that access tokens were issued specifically for them"), and only
        /// while the user has not revoked its grant.
        /// </summary>
        internal WebGrant Verify(string authorization, string baseUrl, out string problem)
        {
            problem = null;
            if (string.IsNullOrEmpty(authorization)) { problem = "none"; return null; }
            const string scheme = "Bearer ";
            if (!authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) { problem = "invalid_token"; return null; }
            string token = authorization.Substring(scheme.Length).Trim();
            var grant = Grants().FirstOrDefault(g => WebSecrets.Matches(token, g.AccessHash));
            if (grant == null || grant.AccessUntil <= _now()) { problem = "invalid_token"; return null; }
            if (!WebAddress.SameResource(grant.Resource, baseUrl + WebAddress.McpPath)) { problem = "invalid_token"; return null; }
            return grant;
        }

        /// <summary>The grants allowed now.</summary>
        internal IEnumerable<WebGrant> Grants() => _app.Grants;

        private WebGrant Grant(string id) => Grants().FirstOrDefault(g => g.Id == id);

        // ─────────────────────────────────────────────
        //  Client ID metadata documents
        // ─────────────────────────────────────────────

        /// <summary>A client ID that names a metadata document: https with a
        /// path (the client registration page: "The client_id URL MUST use
        /// the https scheme and contain a path component").</summary>
        internal static bool IsDocumentAddress(string clientId)
            => Uri.TryCreate(clientId, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
               && uri.AbsolutePath.Length > 1 && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo);

        /// <summary>A metadata document's name and redirect addresses, or
        /// null when it is not one for that address: valid JSON whose
        /// client_id is the address exactly, with redirect_uris (the
        /// authorization server's duties on the client registration
        /// page).</summary>
        internal static (string Name, IReadOnlyList<string> Redirects)? ReadDocument(string address, string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            JsonObject doc;
            try { doc = JsonNode.Parse(text) as JsonObject; }
            catch (JsonException) { return null; }
            if (doc == null || Text(doc["client_id"]) != address) return null;
            var redirects = new List<string>();
            if (doc["redirect_uris"] is JsonArray list)
                foreach (var item in list)
                    if (item is JsonValue v && v.TryGetValue(out string uri) && WebAddress.IsRedirect(uri)) redirects.Add(uri);
            if (redirects.Count == 0) return null;
            return (Clip(Text(doc["client_name"])), redirects);
        }

        // ─────────────────────────────────────────────

        private static string Get(Dictionary<string, string> form, string name)
            => form.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

        private static string Text(JsonNode node)
            => node is JsonValue v && v.TryGetValue(out string text) && !string.IsNullOrWhiteSpace(text) ? text : null;

        /// <summary>A name a client gives, cleaned of format characters and
        /// cut to 64 characters, as a program's name is.</summary>
        private static string Clip(string name)
        {
            if (name == null) return null;
            string clean = ProgramDeviceDeclaration.Clean(name, 64);
            return clean.Length == 0 ? null : clean;
        }
    }
}
