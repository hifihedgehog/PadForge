using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using PadForge.Services.Plugins;
using Xunit;
using WebRequest = PadForge.Services.Automation.Web.WebRequest;

namespace PadForge.Tests
{
    /// <summary>The app's side as the web helper's sign-in sees it, with a
    /// clock and the user's answers of the test's own.</summary>
    internal sealed class FakeWebApp : IWebAppSide
    {
        public DateTimeOffset ArmedUntil { get; set; } = DateTimeOffset.MinValue;
        public readonly List<WebGrant> List = new();
        public readonly List<WebSignIn> Asked = new();
        public Func<WebSignIn, string> Answer = _ => null;
        public readonly List<WebGrant> Issued = new();
        public readonly List<string> UsedIds = new();

        public IReadOnlyCollection<WebGrant> Grants { get { lock (List) return List.ToList(); } }

        public Task<string> AskAsync(WebSignIn signIn, CancellationToken cancel)
        {
            lock (Asked) Asked.Add(signIn);
            string id = Answer(signIn);
            if (id != null)
                lock (List) List.Add(new WebGrant { Id = id, Origin = signIn.Origin, Name = signIn.Name, ClientId = signIn.ClientId,
                    RedirectUri = signIn.RedirectUri, Resource = signIn.Resource, Allowed = DateTimeOffset.UtcNow });
            return Task.FromResult(id);
        }

        public void Tokens(WebGrant grant)
        {
            lock (List)
            {
                List.RemoveAll(g => g.Id == grant.Id);
                List.Add(grant);
            }
            Issued.Add(grant);
        }

        public void Used(string grantId) => UsedIds.Add(grantId);
    }

    /// <summary>
    /// The web helper's own OAuth 2.1 sign-in (#600 item 11), against the
    /// MCP authorization specification's rules: metadata, registration only
    /// while armed, PKCE S256, client ID metadata documents fetched only
    /// while armed, the code on the page and the card, resource indicators,
    /// single-use codes, rotated refresh tokens and tokens kept as hashes.
    /// </summary>
    public class WebAuthTests
    {
        private const string Base = "https://quiet-lake-4b2c.ngrok-free.app";
        private const string Redirect = "https://claude.ai/api/mcp/auth_callback";
        private DateTimeOffset _now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        private readonly FakeWebApp _app = new();
        private int _fetched;
        private string _document;
        private readonly WebAuth _auth;

        public WebAuthTests()
        {
            _auth = new WebAuth(_app, (url, ct) => { _fetched++; return Task.FromResult(_document); }, () => _now);
        }

        private void ArmFor(int minutes = 5) => _app.ArmedUntil = _now.AddMinutes(minutes);

        private WebReply Authorize(string query) => _auth.AuthorizeAsync(Base, query, default).GetAwaiter().GetResult();

        private static string Fetch(string address) => WebClientDocuments.FetchAsync(address, default).GetAwaiter().GetResult();

        private static JsonObject Json(WebReply reply) => JsonNode.Parse(reply.Body).AsObject();

        private string RegisterClient(string name = "Claude", string redirect = Redirect)
        {
            var reply = _auth.Register(new JsonObject { ["redirect_uris"] = new JsonArray(redirect), ["client_name"] = name }.ToJsonString());
            Assert.Equal(201, reply.Status);
            return Json(reply)["client_id"].GetValue<string>();
        }

        private static (string Verifier, string Challenge) Pkce()
        {
            string verifier = WebSecrets.New(40);
            return (verifier, WebSecrets.Challenge(verifier));
        }

        private static string Query(params (string, string)[] pairs)
            => "?" + string.Join("&", pairs.Select(p => Uri.EscapeDataString(p.Item1) + "=" + Uri.EscapeDataString(p.Item2)));

        private static Dictionary<string, string> QueryOf(string location) => WebAddress.Form(new Uri(location).Query, out _);

        private static string Sid(WebReply page)
        {
            var m = Regex.Match(page.Body, "sid=([A-Za-z0-9_\\-]+)");
            Assert.True(m.Success, "the waiting page names its sign-in");
            return m.Groups[1].Value;
        }

        /// <summary>A whole sign-in: registration, authorize, the user's
        /// answer, and the token exchange. The token answer.</summary>
        private JsonObject SignIn(out string clientId, out string code6)
        {
            ArmFor();
            clientId = RegisterClient();
            var (verifier, challenge) = Pkce();
            _app.Answer = s => "grant-1";
            var page = Authorize(Query(("response_type", "code"), ("client_id", clientId), ("redirect_uri", Redirect),
                ("code_challenge", challenge), ("code_challenge_method", "S256"), ("state", "st8"), ("resource", Base + "/mcp")));
            Assert.Equal(200, page.Status);
            code6 = _app.Asked.Single().Code;
            var next = _auth.SignInNext(Sid(page));
            Assert.Equal(302, next.Status);
            var back = QueryOf(next.Location);
            var token = _auth.Token("grant_type=authorization_code&code=" + Uri.EscapeDataString(back["code"]) + "&redirect_uri=" + Uri.EscapeDataString(Redirect)
                + "&client_id=" + Uri.EscapeDataString(clientId) + "&code_verifier=" + verifier + "&resource=" + Uri.EscapeDataString(Base + "/mcp"));
            Assert.Equal(200, token.Status);
            return Json(token);
        }

        [Fact]
        public void TheMetadata_NamesTheEndpointAsTheResource_AndThisHelperAsItsAuthorizationServer_WithS256AndNoSecret()
        {
            var prm = Json(_auth.ResourceMetadata(Base));
            Assert.Equal(Base + "/mcp", prm["resource"].GetValue<string>());
            Assert.Equal(Base, prm["authorization_servers"][0].GetValue<string>());
            var meta = Json(_auth.ServerMetadata(Base));
            // RFC 8414 3.3: the issuer is the address the metadata was read
            // under, compared ordinally by the C# client.
            Assert.Equal(Base, meta["issuer"].GetValue<string>());
            Assert.Equal(Base + "/authorize", meta["authorization_endpoint"].GetValue<string>());
            Assert.Equal(Base + "/token", meta["token_endpoint"].GetValue<string>());
            Assert.Equal(Base + "/register", meta["registration_endpoint"].GetValue<string>());
            Assert.Equal(new[] { "S256" }, meta["code_challenge_methods_supported"].AsArray().Select(n => n.GetValue<string>()));
            Assert.Equal(new[] { "code" }, meta["response_types_supported"].AsArray().Select(n => n.GetValue<string>()));
            Assert.Equal(new[] { "none" }, meta["token_endpoint_auth_methods_supported"].AsArray().Select(n => n.GetValue<string>()));
            Assert.True(meta["client_id_metadata_document_supported"].GetValue<bool>());
            Assert.True(meta["authorization_response_iss_parameter_supported"].GetValue<bool>());
            var challenge = WebAuth.Unauthorized(Base).Headers.Single(h => h.Key == "WWW-Authenticate").Value;
            Assert.Equal("Bearer resource_metadata=\"" + Base + "/.well-known/oauth-protected-resource/mcp\"", challenge);
        }

        [Fact]
        public async Task OutsideAnArmedWindow_ASignInIsRefused_AndNothingAsks_AndNoDocumentIsFetched()
        {
            var (_, challenge) = Pkce();
            Assert.Equal(403, _auth.Register(new JsonObject { ["redirect_uris"] = new JsonArray(Redirect) }.ToJsonString()).Status);
            var page = await _auth.AuthorizeAsync(Base, Query(("response_type", "code"), ("client_id", "https://claude.ai/oauth/client.json"),
                ("redirect_uri", Redirect), ("code_challenge", challenge), ("code_challenge_method", "S256")), default);
            Assert.Equal(403, page.Status);
            Assert.Null(page.Location);
            Assert.Contains(WebUtility.HtmlEncode(PadForge.Resources.Strings.Strings.Instance.Web_SignIn_NotAccepting), page.Body);
            Assert.Empty(_app.Asked);
            Assert.Equal(0, _fetched);
            // The window closes on time: five minutes, then refused again.
            ArmFor();
            string clientId = RegisterClient();
            _now = _now.AddMinutes(5);
            var late = await _auth.AuthorizeAsync(Base, Query(("response_type", "code"), ("client_id", clientId),
                ("redirect_uri", Redirect), ("code_challenge", challenge), ("code_challenge_method", "S256")), default);
            Assert.Equal(403, late.Status);
            Assert.Empty(_app.Asked);
        }

        [Fact]
        public void ASignIn_CarriesItsOwnSixDigits_OnItsPageAndToTheCard_AndIsNamedByWhereItReturns()
        {
            ArmFor();
            string clientId = RegisterClient("Claude <script>");
            var (_, challenge) = Pkce();
            var codes = new HashSet<string>();
            for (int i = 0; i < 3; i++)
            {
                var page = Authorize(Query(("response_type", "code"), ("client_id", clientId), ("redirect_uri", Redirect),
                    ("code_challenge", challenge), ("code_challenge_method", "S256"), ("state", "s" + i)));
                var asked = _app.Asked[i];
                Assert.Matches("^[0-9]{6}$", asked.Code);
                codes.Add(asked.Code);
                Assert.Contains(WebSecrets.Spaced(asked.Code), page.Body);
                Assert.Equal("claude.ai", asked.Origin);
                Assert.Equal("Claude <script>", asked.Name);
                // A name the client gives is escaped on the page.
                Assert.DoesNotContain("<script>", page.Body);
                Assert.Contains("Claude &lt;script&gt;", page.Body);
                Assert.False(asked.Loopback);
                Assert.Equal("quiet-lake-4b2c.ngrok-free.app", asked.Through);
                Assert.Contains("default-src 'none'", page.Headers.Single(h => h.Key == "Content-Security-Policy").Value);
                Assert.Contains("frame-ancestors 'none'", page.Headers.Single(h => h.Key == "Content-Security-Policy").Value);
            }
            Assert.True(codes.Count >= 2, "each sign-in draws its own code");
            // At most three wait at once.
            var fourth = Authorize(Query(("response_type", "code"), ("client_id", clientId), ("redirect_uri", Redirect),
                ("code_challenge", challenge), ("code_challenge_method", "S256"), ("state", "s4")));
            Assert.Equal(302, fourth.Status);
            Assert.Equal("temporarily_unavailable", QueryOf(fourth.Location)["error"]);
        }

        [Fact]
        public void TheWholeSignIn_IssuesTokens_KeptAsHashesOnly_WithTheIssuerOnTheRedirect()
        {
            ArmFor();
            string clientId = RegisterClient();
            var (verifier, challenge) = Pkce();
            _app.Answer = _ => "g1";
            var page = Authorize(Query(("response_type", "code"), ("client_id", clientId), ("redirect_uri", Redirect),
                ("code_challenge", challenge), ("code_challenge_method", "S256"), ("state", "xyz"), ("resource", Base + "/mcp")));
            var next = _auth.SignInNext(Sid(page));
            Assert.StartsWith(Redirect + "?code=", next.Location);
            var back = QueryOf(next.Location);
            Assert.Equal("xyz", back["state"]);
            Assert.Equal(Base, back["iss"]);

            var token = Json(_auth.Token("grant_type=authorization_code&code=" + Uri.EscapeDataString(back["code"]) + "&redirect_uri=" + Uri.EscapeDataString(Redirect)
                + "&client_id=" + clientId + "&code_verifier=" + verifier + "&resource=" + Uri.EscapeDataString(Base + "/mcp")));
            string access = token["access_token"].GetValue<string>(), refresh = token["refresh_token"].GetValue<string>();
            Assert.Equal("Bearer", token["token_type"].GetValue<string>());
            Assert.Equal(3600, token["expires_in"].GetValue<long>());
            var kept = _app.Issued.Single();
            Assert.Equal(WebSecrets.Hash(access), kept.AccessHash);
            Assert.Equal(WebSecrets.Hash(refresh), kept.RefreshHash);
            Assert.NotEqual(access, kept.AccessHash);
            Assert.DoesNotContain(access, WebWire.ToJson(kept).ToJsonString());
            Assert.DoesNotContain(refresh, WebWire.ToJson(kept).ToJsonString());

            // The token works for this address only.
            Assert.Equal("g1", _auth.Verify("Bearer " + access, Base, out _)?.Id);
            Assert.Null(_auth.Verify("Bearer " + access, "https://other.ngrok-free.app", out string problem));
            Assert.Equal("invalid_token", problem);
            Assert.Null(_auth.Verify("Bearer " + access, WebAddress.Loopback(5000), out _));
            Assert.Null(_auth.Verify("Bearer nope", Base, out _));
            Assert.Null(_auth.Verify(null, Base, out problem));
            Assert.Equal("none", problem);
            // And for an hour.
            _now = _now.AddHours(1);
            Assert.Null(_auth.Verify("Bearer " + access, Base, out _));
        }

        [Fact]
        public void ACode_IsSingleUse_AndNeedsItsVerifier_ItsRedirect_AndItsResource()
        {
            ArmFor();
            string clientId = RegisterClient();
            var (verifier, challenge) = Pkce();
            _app.Answer = _ => "g2";
            string NewCode()
            {
                var page = Authorize(Query(("response_type", "code"), ("client_id", clientId), ("redirect_uri", Redirect),
                    ("code_challenge", challenge), ("code_challenge_method", "S256")));
                return QueryOf(_auth.SignInNext(Sid(page)).Location)["code"];
            }
            string Exchange(string code, string v, string redirect = Redirect, string resource = Base + "/mcp")
            {
                var reply = _auth.Token("grant_type=authorization_code&code=" + Uri.EscapeDataString(code) + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                    + "&client_id=" + clientId + "&code_verifier=" + v + "&resource=" + Uri.EscapeDataString(resource));
                return reply.Status == 200 ? "ok" : Json(reply)["error"].GetValue<string>();
            }
            Assert.Equal("invalid_grant", Exchange(NewCode(), WebSecrets.New(40)));
            Assert.Equal("invalid_grant", Exchange(NewCode(), verifier, "https://evil.example/cb"));
            Assert.Equal("invalid_target", Exchange(NewCode(), verifier, Redirect, "https://other.example/mcp"));
            string code = NewCode();
            Assert.Equal("ok", Exchange(code, verifier));
            Assert.Equal("invalid_grant", Exchange(code, verifier));
            // A minute's life.
            string late = NewCode();
            _now = _now.AddSeconds(61);
            Assert.Equal("invalid_grant", Exchange(late, verifier));
        }

        [Fact]
        public void ARefreshToken_IsRotated_TheOldOneStopsWorking_AndARevokedGrantRefreshesNoMore()
        {
            var first = SignIn(out string clientId, out _);
            string refresh = first["refresh_token"].GetValue<string>();
            var again = _auth.Token("grant_type=refresh_token&refresh_token=" + refresh + "&client_id=" + clientId + "&resource=" + Uri.EscapeDataString(Base + "/mcp"));
            Assert.Equal(200, again.Status);
            string second = Json(again)["refresh_token"].GetValue<string>();
            Assert.NotEqual(refresh, second);
            Assert.Equal(400, _auth.Token("grant_type=refresh_token&refresh_token=" + refresh).Status);
            Assert.Null(_auth.Verify("Bearer " + first["access_token"].GetValue<string>(), Base, out _));
            Assert.Equal("invalid_target", Json(_auth.Token("grant_type=refresh_token&refresh_token=" + second + "&resource=https%3A%2F%2Fx.example%2Fmcp"))["error"].GetValue<string>());
            lock (_app.List) _app.List.Clear();
            Assert.Equal(400, _auth.Token("grant_type=refresh_token&refresh_token=" + second).Status);
        }

        [Fact]
        public void DontAllow_SendsAccessDeniedBack_WithTheStateAndTheIssuer()
        {
            ArmFor();
            string clientId = RegisterClient();
            var (_, challenge) = Pkce();
            _app.Answer = _ => null;
            var page = Authorize(Query(("response_type", "code"), ("client_id", clientId), ("redirect_uri", Redirect),
                ("code_challenge", challenge), ("code_challenge_method", "S256"), ("state", "q")));
            var back = QueryOf(_auth.SignInNext(Sid(page)).Location);
            Assert.Equal("access_denied", back["error"]);
            Assert.Equal("q", back["state"]);
            Assert.Equal(Base, back["iss"]);
            Assert.False(back.ContainsKey("code"));
        }

        [Fact]
        public void PkceAndTheRedirectAreRequired_AndAWrongRedirectIsNeverFollowed()
        {
            ArmFor();
            string clientId = RegisterClient();
            var (_, challenge) = Pkce();
            var noPkce = Authorize(Query(("response_type", "code"), ("client_id", clientId), ("redirect_uri", Redirect)));
            Assert.Equal("invalid_request", QueryOf(noPkce.Location)["error"]);
            var plain = Authorize(Query(("response_type", "code"), ("client_id", clientId), ("redirect_uri", Redirect),
                ("code_challenge", challenge), ("code_challenge_method", "plain")));
            Assert.Equal("invalid_request", QueryOf(plain.Location)["error"]);
            var elsewhere = Authorize(Query(("response_type", "code"), ("client_id", clientId), ("redirect_uri", "https://evil.example/cb"),
                ("code_challenge", challenge), ("code_challenge_method", "S256")));
            Assert.Equal(403, elsewhere.Status);
            Assert.Null(elsewhere.Location);
            var unknown = Authorize(Query(("response_type", "code"), ("client_id", "pf-unknown"), ("redirect_uri", Redirect),
                ("code_challenge", challenge), ("code_challenge_method", "S256")));
            Assert.Equal(403, unknown.Status);
            var otherResource = Authorize(Query(("response_type", "code"), ("client_id", clientId), ("redirect_uri", Redirect),
                ("code_challenge", challenge), ("code_challenge_method", "S256"), ("resource", "https://api.example.com")));
            Assert.Equal("invalid_target", QueryOf(otherResource.Location)["error"]);
            Assert.Empty(_app.Asked);
            // Registration takes https and this PC's http only.
            Assert.Equal(400, _auth.Register(new JsonObject { ["redirect_uris"] = new JsonArray("http://evil.example/cb") }.ToJsonString()).Status);
            Assert.Equal(201, _auth.Register(new JsonObject { ["redirect_uris"] = new JsonArray("http://127.0.0.1:33418/callback") }.ToJsonString()).Status);
        }

        [Fact]
        public void AClientDocument_IsReadOnlyWhileArmed_MustNameItsOwnAddress_AndListTheRedirect()
        {
            const string address = "https://claude.ai/oauth/mcp-oauth-client-metadata";
            var (_, challenge) = Pkce();
            string q = Query(("response_type", "code"), ("client_id", address), ("redirect_uri", Redirect), ("code_challenge", challenge), ("code_challenge_method", "S256"));
            ArmFor();
            _document = new JsonObject { ["client_id"] = "https://evil.example/doc", ["client_name"] = "Claude", ["redirect_uris"] = new JsonArray(Redirect) }.ToJsonString();
            Assert.Equal(403, Authorize(q).Status);
            _document = new JsonObject { ["client_id"] = address, ["client_name"] = "Claude", ["redirect_uris"] = new JsonArray("https://claude.ai/other") }.ToJsonString();
            Assert.Equal(403, Authorize(q).Status);
            _document = new JsonObject { ["client_id"] = address, ["client_name"] = "Claude", ["redirect_uris"] = new JsonArray(Redirect) }.ToJsonString();
            Assert.Equal(200, Authorize(q).Status);
            Assert.Equal("Claude", _app.Asked.Single().Name);
            Assert.Equal(3, _fetched);
            Assert.True(WebAuth.IsDocumentAddress(address));
            Assert.False(WebAuth.IsDocumentAddress("https://claude.ai"));
            Assert.False(WebAuth.IsDocumentAddress("http://claude.ai/doc"));
        }

        [Fact]
        public void ASignInThatReturnsToThisPC_IsSaidToBeClaimableByAnyProgramHere()
        {
            ArmFor();
            string clientId = RegisterClient("Local", "http://localhost:4417/callback");
            var (_, challenge) = Pkce();
            var page = Authorize(Query(("response_type", "code"), ("client_id", clientId), ("redirect_uri", "http://localhost:4417/callback"),
                ("code_challenge", challenge), ("code_challenge_method", "S256")));
            Assert.True(_app.Asked.Single().Loopback);
            Assert.Contains(WebUtility.HtmlEncode(PadForge.Resources.Strings.Strings.Instance.Web_SignIn_LocalWarning), page.Body);
        }

        [Fact]
        public void TheClientDocumentFetch_ReachesOnlyPublicAddresses()
        {
            Assert.False(WebClientDocuments.IsPublic(IPAddress.Loopback));
            Assert.False(WebClientDocuments.IsPublic(IPAddress.Parse("10.1.2.3")));
            Assert.False(WebClientDocuments.IsPublic(IPAddress.Parse("172.20.0.1")));
            Assert.False(WebClientDocuments.IsPublic(IPAddress.Parse("192.168.1.10")));
            Assert.False(WebClientDocuments.IsPublic(IPAddress.Parse("169.254.169.254")));
            Assert.False(WebClientDocuments.IsPublic(IPAddress.Parse("100.100.1.1")));
            Assert.False(WebClientDocuments.IsPublic(IPAddress.Parse("::1")));
            Assert.False(WebClientDocuments.IsPublic(IPAddress.Parse("fd00::1")));
            Assert.False(WebClientDocuments.IsPublic(IPAddress.Parse("fe80::1")));
            Assert.False(WebClientDocuments.IsPublic(IPAddress.Parse("::ffff:192.168.0.1")));
            Assert.True(WebClientDocuments.IsPublic(IPAddress.Parse("160.79.104.10")));
            Assert.True(WebClientDocuments.IsPublic(IPAddress.Parse("2607:6bc0::10")));
            // The test host fetches nothing beyond this PC, and an address
            // that is not https on its own port is never fetched.
            Assert.Null(Fetch("https://claude.ai/oauth/doc"));
            Assert.Null(Fetch("http://claude.ai/oauth/doc"));
            Assert.Null(Fetch("https://claude.ai:8443/oauth/doc"));
        }

        [Fact]
        public void Addresses_AreReadAsTheDoorTakesThem()
        {
            Assert.Equal("https://quiet-lake.ngrok-free.app", WebAddress.Base("https://Quiet-Lake.ngrok-free.app/mcp"));
            Assert.Equal("https://a.trycloudflare.com", WebAddress.Base(" https://a.trycloudflare.com/ "));
            Assert.Equal("https://box.tail1.ts.net:8443", WebAddress.Base("https://box.tail1.ts.net:8443"));
            Assert.Null(WebAddress.Base("http://a.trycloudflare.com"));
            Assert.Null(WebAddress.Base("https://a.example/other"));
            Assert.Null(WebAddress.Base("https://a.example/mcp?x=1"));
            Assert.True(WebAddress.SameResource("HTTPS://A.example/mcp/", "https://a.example/mcp"));
            Assert.False(WebAddress.SameResource("https://a.example/MCP", "https://a.example/mcp"));
            Assert.True(WebAddress.IsRedirect("http://[::1]:9/cb"));
            Assert.False(WebAddress.IsRedirect("https://a.example/cb#frag"));
            Assert.False(WebAddress.IsRedirect("javascript:alert(1)"));
        }
    }

    /// <summary>The web helper's front door (#600 item 11): Host and Origin,
    /// the base address each request came by, and the refusals with their
    /// status codes.</summary>
    public class WebFrontTests
    {
        private const string Public = "https://quiet-lake.ngrok-free.app";
        private readonly FakeWebApp _app = new();
        private readonly WebFront _front;

        public WebFrontTests()
        {
            var auth = new WebAuth(_app, (u, c) => Task.FromResult<string>(null));
            var mcp = new WebMcp(_ => throw new InvalidOperationException("no request reaches PadForge here"));
            _front = new WebFront(41234, auth, mcp, _app, () => Public);
        }

        private static WebRequest Get(string path, params (string, string)[] headers) => new("GET", path, null, WebRequest.HeadersOf(headers), null);

        private WebReply Handle(WebRequest request) => _front.HandleAsync(request, default).GetAwaiter().GetResult();

        [Fact]
        public void HostAndOrigin_AreChecked_AgainstDnsRebinding()
        {
            Assert.Equal(WebAddress.Loopback(41234), _front.BaseOf(Get("/", ("Host", "127.0.0.1:41234"))));
            Assert.Equal(WebAddress.Loopback(41234), _front.BaseOf(Get("/", ("Host", "localhost:41234"))));
            Assert.Equal(Public, _front.BaseOf(Get("/", ("Host", "quiet-lake.ngrok-free.app"))));
            Assert.Equal(Public, _front.BaseOf(Get("/", ("Host", "127.0.0.1:41234"), ("X-Forwarded-Host", "quiet-lake.ngrok-free.app"))));
            Assert.Null(_front.BaseOf(Get("/", ("Host", "evil.example"))));
            Assert.Null(_front.BaseOf(Get("/", ("Host", "127.0.0.1:9999"))));

            Assert.Equal(403, Handle(Get("/.well-known/oauth-authorization-server", ("Host", "evil.example"))).Status);
            Assert.Equal(403, Handle(Get("/.well-known/oauth-authorization-server", ("Host", "quiet-lake.ngrok-free.app"), ("Origin", "https://evil.example"))).Status);
            Assert.Equal(200, Handle(Get("/.well-known/oauth-authorization-server", ("Host", "quiet-lake.ngrok-free.app"), ("Origin", Public))).Status);
            var meta = JsonNode.Parse(Handle(Get("/.well-known/oauth-authorization-server", ("Host", "127.0.0.1:41234"))).Body);
            Assert.Equal(WebAddress.Loopback(41234), meta["issuer"].GetValue<string>());
        }

        [Fact]
        public void TheMcpEndpoint_AsksForASignIn_Refuses_GetAndDelete_AndAnUnknownPath()
        {
            var post = new WebRequest("POST", "/mcp", null, WebRequest.HeadersOf(("Host", "quiet-lake.ngrok-free.app")), "{}");
            var none = Handle(post);
            Assert.Equal(401, none.Status);
            Assert.Equal("Bearer resource_metadata=\"" + Public + "/.well-known/oauth-protected-resource/mcp\"", none.Headers.Single(h => h.Key == "WWW-Authenticate").Value);
            var bad = Handle(post with { Headers = WebRequest.HeadersOf(("Host", "quiet-lake.ngrok-free.app"), ("Authorization", "Bearer x")) });
            Assert.Equal(401, bad.Status);
            Assert.Contains("error=\"invalid_token\"", bad.Headers.Single(h => h.Key == "WWW-Authenticate").Value);
            foreach (string method in new[] { "GET", "DELETE", "PUT" })
            {
                var reply = Handle(post with { Method = method });
                Assert.Equal(405, reply.Status);
                Assert.Equal("POST", reply.Headers.Single(h => h.Key == "Allow").Value);
            }
            Assert.Equal(404, Handle(Get("/admin", ("Host", "127.0.0.1:41234"))).Status);
            var form = new WebRequest("POST", "/token", null, WebRequest.HeadersOf(("Host", "127.0.0.1:41234"), ("Content-Type", "application/json")), "{}");
            Assert.Equal(400, Handle(form).Status);
            var prm = JsonNode.Parse(Handle(Get("/.well-known/oauth-protected-resource/mcp", ("Host", "quiet-lake.ngrok-free.app"))).Body);
            Assert.Equal(Public + "/mcp", prm["resource"].GetValue<string>());
        }
    }

    /// <summary>The MCP endpoint's checks before PadForge is reached, and
    /// the buffered reply read back as plain JSON (#600 item 11).</summary>
    public class WebMcpTests
    {
        private static readonly WebGrant Grant = new() { Id = "g", Origin = "claude.ai" };

        private static WebRequest Post(string body, params (string, string)[] headers)
            => new("POST", "/mcp", null, WebRequest.HeadersOf(new[] { ("Accept", "application/json, text/event-stream") }.Concat(headers).ToArray()), body);

        private static readonly WebMcp Mcp = new(_ => throw new InvalidOperationException("no request reaches PadForge here"));

        private static WebReply PostOf(WebRequest request) => Mcp.PostAsync(request, Grant, default).GetAwaiter().GetResult();

        [Fact]
        public void TheChecks_ComeInTheSdkHandlersOrder_WithItsStatusCodes()
        {
            var noAccept = new WebRequest("POST", "/mcp", null, WebRequest.HeadersOf(("Accept", "application/json")), "{}");
            Assert.Equal(406, PostOf(noAccept).Status);
            Assert.Equal(400, PostOf(Post("not json")).Status);
            var tools = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/list\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}}}}";
            var unsupported = PostOf(Post(tools, ("MCP-Protocol-Version", "2099-01-01")));
            Assert.Equal(400, unsupported.Status);
            var error = JsonNode.Parse(unsupported.Body)["error"];
            Assert.Equal(-32022, error["code"].GetValue<int>());
            Assert.Contains("2026-07-28", error["data"]["supported"].AsArray().Select(n => n.GetValue<string>()));
            Assert.Equal(7, JsonNode.Parse(unsupported.Body)["id"].GetValue<int>());
            var noMethod = PostOf(Post(tools, ("MCP-Protocol-Version", "2026-07-28")));
            Assert.Equal(400, noMethod.Status);
            Assert.Equal(-32020, JsonNode.Parse(noMethod.Body)["error"]["code"].GetValue<int>());
            var mismatch = PostOf(Post(tools, ("MCP-Protocol-Version", "2026-07-28"), ("Mcp-Method", "tools/call")));
            Assert.Equal(-32020, JsonNode.Parse(mismatch.Body)["error"]["code"].GetValue<int>());
            var call = "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/call\",\"params\":{\"name\":\"status\",\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}}}}";
            var name = PostOf(Post(call, ("MCP-Protocol-Version", "2026-07-28"), ("Mcp-Method", "tools/call"), ("Mcp-Name", "find")));
            Assert.Equal(-32020, JsonNode.Parse(name.Body)["error"]["code"].GetValue<int>());
            var init = "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"initialize\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}}}}";
            var removed = PostOf(Post(init, ("MCP-Protocol-Version", "2026-07-28"), ("Mcp-Method", "initialize")));
            Assert.Equal(404, removed.Status);
            Assert.Equal(-32601, JsonNode.Parse(removed.Body)["error"]["code"].GetValue<int>());
            var listen = "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"subscriptions/listen\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}}}}";
            var stream = PostOf(Post(listen, ("MCP-Protocol-Version", "2026-07-28"), ("Mcp-Method", "subscriptions/listen")));
            Assert.Equal(404, stream.Status);
            // A notification is taken with 202 and no body.
            var note = PostOf(Post("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", ("MCP-Protocol-Version", "2025-11-25")));
            Assert.Equal(202, note.Status);
            Assert.Null(note.Body);
        }

        [Fact]
        public void ABufferedEventStream_GivesTheResponseAlone_AsPlainJson()
        {
            string events = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"progress\":1}}\n\n"
                + "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"tools\":[]}}\n\n";
            string final = WebMcp.FinalOf(Encoding.UTF8.GetBytes(events), JsonValue.Create(3));
            Assert.Equal("{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"tools\":[]}}", final);
            Assert.Null(WebMcp.FinalOf(Encoding.UTF8.GetBytes(events), JsonValue.Create(4)));
            Assert.True(WebMcp.IsPerRequest("2026-07-28"));
            Assert.False(WebMcp.IsPerRequest("2025-11-25"));
            Assert.False(WebMcp.IsPerRequest(null));
        }
    }

    /// <summary>A launcher that starts nothing and hands back a process
    /// standing for the test's own, so a helper the test runs in process
    /// passes as the one PadForge started.</summary>
    internal sealed class WebTestLauncher : IPluginLauncher
    {
        public readonly List<(PluginCommand Command, IReadOnlyDictionary<string, string> Variables)> Started = new();
        public WebTestProcess Last;
        public int Id = Environment.ProcessId;

        public PluginLaunch Start(PluginCommand command, IReadOnlyDictionary<string, string> variables)
        {
            Started.Add((command, variables));
            Last = new WebTestProcess(Id);
            return new PluginLaunch { Process = Last };
        }
    }

    internal sealed class WebTestProcess : IPluginProcess
    {
        public WebTestProcess(int id) => Id = id;
        public int Id { get; }
        public bool Stopped;
        public bool Owns(int processId) => processId == Id;
        public event Action<int> Exited;
        public void Stop() { Stopped = true; }
        public void Exit(int code) => Exited?.Invoke(code);
        public void Dispose() { }
    }

    /// <summary>
    /// The app's web door (#600 item 11): it takes the helper's pipe only
    /// from the process it started with its key, asks on the card only
    /// inside the armed window, shows the code on the card alone, keeps
    /// what the user allowed in PadForge.xml as hashes, and Revoke takes it
    /// away.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WebDoorTests
    {
        private static string KeyOf(WebTestLauncher launcher) => launcher.Started.Last().Variables[WebHelper.KeyVariable];

        [Fact]
        public void TheHelper_IsStartedThroughTheLauncher_WithAOneTimeKey_AndOnlyThatProcessWithThatKeyIsTaken()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            var launcher = new WebTestLauncher { Id = 4242 };
            using var door = new WebDoor(rig.Host, store, launcher, () => @"C:\PadForge\PadForge.exe", rig.PipeName);
            Assert.Empty(launcher.Started);
            door.TurnOn();
            var (command, variables) = launcher.Started.Single();
            Assert.Equal(@"C:\PadForge\PadForge.exe", command.Program);
            Assert.Equal("\"C:\\PadForge\\PadForge.exe\" --web", command.CommandLine);
            Assert.Equal(rig.PipeName, variables[WebHelper.PipeVariable]);
            string key = KeyOf(launcher);
            Assert.True(key.Length >= 40);
            Assert.True(door.AcceptHelper(key, 4242));
            Assert.False(door.AcceptHelper(key, 4243));
            Assert.False(door.AcceptHelper(key.Substring(1) + "A", 4242));
            Assert.False(door.AcceptHelper(null, 4242));
            Assert.True(store.On);
            // Off ends it, and its key is taken by nothing.
            door.TurnOff();
            Assert.True(launcher.Last.Stopped);
            Assert.False(door.AcceptHelper(key, 4242));
            Assert.False(store.On);
            // A new start draws a new key.
            door.TurnOn();
            Assert.NotEqual(key, KeyOf(launcher));
        }

        [Fact]
        public async Task OutsideTheArmedWindow_NoCardShows_AndInsideIt_TheCardCarriesTheCode_WhichNoDetailLineHolds()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            var clock = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => @"C:\PadForge\PadForge.exe", rig.PipeName, () => clock);
            var signIn = new WebSignIn("sid", "claude.ai", "Claude", "482913", "quiet-lake.ngrok-free.app", false,
                "https://claude.ai/oauth/doc", "https://claude.ai/api/mcp/auth_callback", "https://quiet-lake.ngrok-free.app/mcp");

            Assert.Null(await door.AskAsync(signIn));
            Assert.Empty(rig.Host.Approvals.Pending);

            door.Arm();
            Assert.Equal(clock + WebDoor.ArmedFor, door.ArmedUntil);
            var asking = door.AskAsync(signIn);
            await AssistantRig.Until(() => rig.Host.Approvals.Pending.Count == 1);
            var pending = rig.Host.Approvals.Pending.Single();
            Assert.Equal(ApprovalKind.WebSignIn, pending.Request.Kind);
            Assert.Equal("Allow claude.ai to work with PadForge from the web?", pending.Request.Title);
            Assert.Equal("Its sign-in returns to claude.ai. It calls itself Claude.", pending.Request.From);
            Assert.Equal("482913", pending.Request.Code);
            Assert.Equal("Allow only if its sign-in page shows this code:", pending.Request.CodeLead);
            Assert.Equal("It works at Change Settings, through quiet-lake.ngrok-free.app.", pending.Request.CodeAfter);
            // Shown only on its page and on the card: never in a line the
            // Automation page lists, the title, or the From line.
            Assert.DoesNotContain(pending.Request.Detail, l => l.Contains("482") || l.Contains("913"));
            Assert.DoesNotContain("482", pending.Request.Title + pending.Request.From);
            var item = new PadForge.ViewModels.AutomationRequestItem(pending, (_, _) => true);
            Assert.Equal("482 913", item.Code);
            Assert.True(item.HasCode);
            Assert.DoesNotContain(item.Detail, l => l.Contains("482"));
            rig.Host.Approvals.Answer(pending.Id, true);
            string id = await asking;
            Assert.NotNull(id);
            var grant = store.Grants.Single();
            Assert.Equal(id, grant.Id);
            Assert.Equal("claude.ai", grant.Origin);
            Assert.Equal("Claude", grant.Name);
            // One new assistant per arming.
            Assert.False(door.Armed);
            // A code that is not six digits is never put on the card.
            door.Arm();
            Assert.Null(await door.AskAsync(signIn with { Code = "48291" }));
            Assert.Empty(rig.Host.Approvals.Pending);
            // The window ends after five minutes.
            clock += WebDoor.ArmedFor;
            Assert.False(door.Armed);
            Assert.Null(await door.AskAsync(signIn));
        }

        [Fact]
        public void TheStore_KeepsHashesOnly_RoundTripsThroughTheFile_AndRevokeTakesTheAssistantOut()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => @"C:\PadForge\PadForge.exe", rig.PipeName);
            Assert.Null(store.Save());
            string access = WebSecrets.New(), refresh = WebSecrets.New();
            var until = new DateTimeOffset(2026, 11, 24, 13, 0, 0, TimeSpan.Zero);
            store.SetOn(true);
            store.SetTunnel("ngrok", null);
            store.SetPort(51234);
            Assert.True(store.Add(new WebGrant { Id = "g1", Origin = "claude.ai", Name = "Claude", ClientId = "c", RedirectUri = "https://claude.ai/cb",
                Resource = "https://x.ngrok-free.app/mcp", Allowed = until.AddHours(-1) }));
            store.Tokens(new WebGrant { Id = "g1", AccessHash = WebSecrets.Hash(access), AccessUntil = until, RefreshHash = WebSecrets.Hash(refresh), RefreshUntil = until.AddDays(30) });
            var data = store.Save();
            var xml = new System.Xml.Serialization.XmlSerializer(typeof(WebAccessData));
            var text = new System.IO.StringWriter();
            xml.Serialize(text, data);
            string file = text.ToString();
            Assert.DoesNotContain(access, file);
            Assert.DoesNotContain(refresh, file);
            Assert.Contains(WebSecrets.Hash(access), file);
            var back = new WebAccessStore();
            back.Load((WebAccessData)xml.Deserialize(new System.IO.StringReader(file)));
            Assert.True(back.On);
            Assert.Equal("ngrok", back.Tunnel);
            Assert.Equal(51234, back.Port);
            var kept = back.Grants.Single();
            Assert.Equal(WebSecrets.Hash(access), kept.AccessHash);
            Assert.Equal(until, kept.AccessUntil);
            Assert.True(WebSecrets.Matches(refresh, kept.RefreshHash));
            Assert.False(WebSecrets.Matches(access, kept.RefreshHash));
            // A hash that is not one is not taken in.
            back.Load(new WebAccessData { Assistants = new[] { new WebAssistantData { Id = "x", Origin = "a", AccessHash = "not-a-hash" } } });
            Assert.Null(back.Grants.Single().AccessHash);

            door.Revoke("g1");
            Assert.Empty(store.Grants);
            Assert.Null(door.State()["grants"].AsArray().FirstOrDefault());
        }

        [Fact]
        public void TheStateTheHelperIsTold_CarriesThePastedAddressOrTheTunnelFound()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => @"C:\PadForge\PadForge.exe", rig.PipeName);
            door.PickTunnel("paste", "https://box.tail1.ts.net");
            var state = door.State();
            Assert.Equal("https://box.tail1.ts.net", state["pasted"].GetValue<string>());
            Assert.Null(state["tunnel"]);
            Assert.Equal("https://box.tail1.ts.net/mcp", door.PublicAddress);
            door.PickTunnel("ngrok");
            state = door.State();
            Assert.Null(state["pasted"]);
            Assert.Equal("ngrok", state["tunnel"].GetValue<string>());
            Assert.Null(door.PublicAddress);
            door.PickTunnel("unknown");
            Assert.Null(store.Tunnel);
        }
    }

    /// <summary>The tunnel programs (#600 item 11): their command lines and
    /// the address each one's own words give, from the lines their sources
    /// and documents print, and the fence that keeps the test host from
    /// starting one.</summary>
    public class WebTunnelTests
    {
        [Fact]
        public void EachTunnel_ReadsItsAddressFromItsOwnWords()
        {
            var cloudflared = WebTunnels.Of("cloudflared");
            // cloudflared quick_tunnel.go quickTunnelStartupLines: the
            // banner, then the address on a line of its own, in a log line.
            Assert.Null(cloudflared.AddressIn("2026-11-24T12:00:00Z INF |  Your quick Tunnel has been created! Visit it at (it may take some time to be reachable):  |"));
            Assert.Equal("https://seasonal-deck-organisms-sf.trycloudflare.com",
                cloudflared.AddressIn("2026-11-24T12:00:00Z INF |  https://seasonal-deck-organisms-sf.trycloudflare.com                                     |"));
            Assert.Null(cloudflared.AddressIn("2026-11-24T12:00:00Z INF Requesting new quick Tunnel on trycloudflare.com..."));
            Assert.Equal("tunnel --no-autoupdate --url http://127.0.0.1:5123", cloudflared.Arguments(5123));
            Assert.False(cloudflared.KeepsAddress);

            var ngrok = WebTunnels.Of("ngrok");
            Assert.Equal("https://quiet-lake-4b2c.ngrok-free.app",
                ngrok.AddressIn("t=2026-11-24T12:00:00-0500 lvl=info msg=\"started tunnel\" obj=tunnels name=command_line addr=http://127.0.0.1:5123 url=https://quiet-lake-4b2c.ngrok-free.app"));
            Assert.Null(ngrok.AddressIn("t=2026-11-24T12:00:00-0500 lvl=info msg=\"client session established\" obj=tunnels.session"));
            Assert.Equal("http 127.0.0.1:5123 --log stdout --log-format logfmt", ngrok.Arguments(5123));
            Assert.True(ngrok.KeepsAddress);

            var devtunnel = WebTunnels.Of("devtunnel");
            Assert.Equal("https://xyz1abcd-5123.usw2.devtunnels.ms", devtunnel.AddressIn("Connect via browser: https://xyz1abcd-5123.usw2.devtunnels.ms"));
            Assert.Null(devtunnel.AddressIn("Inspect network activity: https://xyz1abcd-5123-inspect.usw2.devtunnels.ms"));
            Assert.Equal("host -p 5123 --allow-anonymous", devtunnel.Arguments(5123));
            Assert.False(devtunnel.KeepsAddress);

            var tailscale = WebTunnels.Of("tailscale");
            // serve_v2.go messageForPort: "Available on the internet:", a blank
            // line, then scheme://host/ for each mount.
            Assert.Null(tailscale.AddressIn("Available on the internet:"));
            Assert.Equal("https://box.tail1234.ts.net", tailscale.AddressIn("https://box.tail1234.ts.net/"));
            Assert.Null(tailscale.AddressIn("|-- proxy http://127.0.0.1:5123"));
            Assert.Equal("funnel 5123", tailscale.Arguments(5123));
            Assert.True(tailscale.KeepsAddress);
        }

        [Fact]
        public void Find_LooksOnThePathAndWhereEachInstalls()
        {
            try
            {
                WebTunnels.ExistsSeam = (folder, exe) => (folder == @"C:\Tools" && exe == "ngrok.exe") || (folder.EndsWith("Tailscale", StringComparison.Ordinal) && exe == "tailscale.exe");
                var found = WebTunnels.Find(@"C:\Tools;relative\folder");
                Assert.Equal(new[] { "ngrok", "tailscale" }, found.Select(f => f.Key));
                Assert.Equal(@"C:\Tools\ngrok.exe", found[0].Path);
            }
            finally
            {
                WebTunnels.ExistsSeam = null;
            }
        }

        [Fact]
        public void TheTestHost_StartsNoTunnelProgram()
        {
            Assert.True(WebTunnels.OffLimitsForTest);
            Assert.Null(WebTunnels.Start(WebTunnels.Of("ngrok"), @"C:\Windows\System32\cmd.exe", 5123, _ => { }, _ => { }, _ => { }));
        }
    }
}
