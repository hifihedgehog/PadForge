using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The web door end to end (#600 item 11, item 52): PadForge's host with
    /// its assistants' server on a pipe of the test's own, the app's web door
    /// with a launcher that stands the test's own process in for the helper,
    /// the web helper itself listening on 127.0.0.1, and the SDK's own client
    /// signing in with its own OAuth flow (ClientOAuthProvider), the user's
    /// Allow given on the question card by the test after it compared the
    /// card's code with the one on the sign-in page. No browser opens and
    /// nothing reaches the desktop.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WebEndToEndTests
    {
        /// <summary>A client's token store that outlives its connection, as a
        /// web assistant's does.</summary>
        private sealed class KeptTokens : ITokenCache
        {
            private TokenContainer _tokens;
            public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken) { _tokens = tokens; return default; }
            public ValueTask<TokenContainer> GetTokensAsync(CancellationToken cancellationToken) => new(_tokens);
        }

        private sealed class Door : IAsyncDisposable
        {
            public readonly AssistantRig Rig = new();
            public readonly WebAccessStore Store = new();
            public readonly WebTestLauncher Launcher = new();
            public readonly WebDoor WebDoor;
            public readonly CancellationTokenSource Stop = new();
            public Task<int> Helper;
            public int Port;
            public int Fetched;
            public string Document;
            /// <summary>The codes the sign-in pages showed, in order.</summary>
            public readonly List<string> PageCodes = new();
            public bool AnswerAllow = true;

            public Door()
            {
                WebDoor = new WebDoor(Rig.Host, Store, Launcher, () => @"C:\PadForge\PadForge.exe", Rig.PipeName);
                // The user, on the question card: Allow only when the card's
                // code is the one the sign-in page showed.
                Rig.Host.Approvals.PendingChanged += (_, _) =>
                {
                    foreach (var pending in Rig.Host.Approvals.Pending.Where(p => p.Request.Kind == ApprovalKind.WebSignIn))
                    {
                        bool same;
                        lock (PageCodes) same = PageCodes.Count > 0 && PageCodes.Last() == pending.Request.Code;
                        if (same) Rig.Host.Approvals.Answer(pending.Id, AnswerAllow);
                    }
                };
            }

            public async Task StartAsync()
            {
                WebDoor.TurnOn();
                string key = Launcher.Started.Last().Variables[WebHelper.KeyVariable];
                var listening = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                Helper = Task.Run(() => WebHelper.RunAsync(key, Rig.PipeName, Stop.Token,
                    (url, ct) => { Interlocked.Increment(ref Fetched); return Task.FromResult(Document); }, port => listening.TrySetResult(port)));
                Port = await listening.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await AssistantRig.Until(() => WebDoor.HelperState == WebHelperState.Running && Store.Port == Port);
            }

            public string Endpoint => WebAddress.Loopback(Port) + "/mcp";

            /// <summary>The browser's part of a sign-in, done by the test: the
            /// sign-in page, its code read off the page, the next page until the
            /// redirect back.</summary>
            public async Task<AuthorizationResult> BrowserAsync(AuthorizationCallbackContext context, CancellationToken ct)
            {
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                var page = await http.GetAsync(context.AuthorizationUri, ct);
                string html = await page.Content.ReadAsStringAsync(ct);
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                var code = Regex.Match(html, "<p class=\"code\">([0-9]{3}) ([0-9]{3})</p>");
                Assert.True(code.Success, "the sign-in page shows six digits");
                lock (PageCodes) PageCodes.Add(code.Groups[1].Value + code.Groups[2].Value);
                // The card may already show: answer it now that the code is known.
                foreach (var pending in Rig.Host.Approvals.Pending.Where(p => p.Request.Kind == ApprovalKind.WebSignIn && p.Request.Code == PageCodes.Last()))
                    Rig.Host.Approvals.Answer(pending.Id, AnswerAllow);
                string next = WebUtility.HtmlDecode(Regex.Match(html, "url=([^\"]+)\"").Groups[1].Value);
                for (int i = 0; i < 100; i++)
                {
                    var answer = await http.GetAsync(next, ct);
                    if (answer.StatusCode == HttpStatusCode.Redirect)
                    {
                        var back = WebAddress.Form(answer.Headers.Location.Query, out _);
                        Assert.StartsWith(context.RedirectUri.ToString(), answer.Headers.Location.ToString());
                        return new AuthorizationResult
                        {
                            Code = back.GetValueOrDefault("code"),
                            State = back.GetValueOrDefault("state"),
                            Iss = back.GetValueOrDefault("iss"),
                        };
                    }
                    await Task.Delay(50, ct);
                }
                throw new TimeoutException("the sign-in page never moved on");
            }

            public async Task<McpClient> ConnectAsync(string version = null, Uri document = null, ITokenCache cache = null)
            {
                var transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(Endpoint),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    OAuth = new ClientOAuthOptions
                    {
                        RedirectUri = new Uri("https://claude.ai/api/mcp/auth_callback"),
                        ClientMetadataDocumentUri = document,
                        AuthorizationCallbackHandler = BrowserAsync,
                        DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "Claude" },
                        TokenCache = cache,
                    },
                });
                return await McpClient.CreateAsync(transport, new McpClientOptions
                {
                    ProtocolVersion = version,
                    ClientInfo = new Implementation { Name = "web-test", Version = "1" },
                });
            }

            public async ValueTask DisposeAsync()
            {
                Stop.Cancel();
                WebDoor.Dispose();
                if (Helper != null) { try { await Helper.WaitAsync(TimeSpan.FromSeconds(10)); } catch { } }
                Rig.Dispose();
            }
        }

        [Fact]
        public async Task TheSdksClient_SignsInByRegistration_WithTheCodeOnTheCard_AndCallsPadForgeThroughTheHelper()
        {
            await using var door = new Door();
            await door.StartAsync();

            // Not armed: the sign-in is refused and nothing asks.
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => door.ConnectAsync());
            Assert.Empty(door.Rig.Host.Approvals.Pending);
            Assert.Empty(door.Store.Grants);

            door.WebDoor.Arm();
            await WebSignInWait.UntilTaking(door.Port);
            await using var client = await door.ConnectAsync();
            // The helper hands the new hashes on as it answers the token
            // request, and the file holds them a moment later.
            await AssistantRig.Until(() => door.Store.Grants.SingleOrDefault()?.AccessHash != null);
            var grant = door.Store.Grants.Single();
            Assert.Equal("claude.ai", grant.Origin);
            Assert.Equal("Claude", grant.Name);
            Assert.StartsWith("pf-", grant.ClientId);
            Assert.Equal(door.Endpoint, grant.Resource);
            Assert.NotNull(grant.AccessHash);
            Assert.False(door.WebDoor.Armed);

            var tools = await client.ListToolsAsync();
            Assert.Contains(tools, t => t.Name == "status");
            Assert.Contains(tools, t => t.Name == "find");
            var status = await client.CallToolAsync("status");
            Assert.NotEqual(true, status.IsError);

            // A web assistant reaches no file on this PC by its path.
            foreach (string tool in new[] { "import_profile", "export_profile" })
            {
                var byPath = await client.CallToolAsync(tool, new Dictionary<string, object> { ["path"] = @"C:\Windows\win.ini" });
                Assert.True(byPath.IsError);
                Assert.Contains("reaches no file on this PC", AssistantRig.Text(byPath));
            }
            // The code the page showed is in no line of Activity.
            string shown;
            lock (door.PageCodes) shown = door.PageCodes.Last();
            Assert.DoesNotContain(shown, door.Rig.Host.Activity.ToText());

            // PadForge lists it as a web assistant named by where its sign-in
            // returned, at the user's level, with the globe's kind.
            await AssistantRig.Until(() => door.Rig.Host.Clients.Snapshot().Any(c => c.Kind == AutomationClientKind.Web));
            var listed = door.Rig.Host.Clients.Snapshot().Single(c => c.Kind == AutomationClientKind.Web);
            Assert.Equal("claude.ai", listed.Name);
            var session = door.Rig.Host.Sessions.Snapshot().Single(s => s.Kind == AutomationClientKind.Web);
            Assert.Equal(AutomationLevel.ChangeSettings, door.Rig.Host.Sessions.LevelOf(session));

            // Revoke: the token stops working at once and the session closes.
            door.WebDoor.Revoke(grant.Id);
            await AssistantRig.Until(() => !door.Rig.Host.Clients.Snapshot().Any(c => c.Kind == AutomationClientKind.Web));
            using var http = new HttpClient();
            var probe = new HttpRequestMessage(HttpMethod.Post, door.Endpoint) { Content = new StringContent("{}") };
            probe.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "anything");
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(probe)).StatusCode);
        }

        [Fact]
        public async Task TheSdksClient_SignsInByItsMetadataDocument_AtTheInitializeRevision_AndDontAllowRefusesIt()
        {
            await using var door = new Door();
            await door.StartAsync();
            var address = new Uri("https://claude.ai/oauth/mcp-oauth-client-metadata");
            door.Document = new JsonObject
            {
                ["client_id"] = address.AbsoluteUri,
                ["client_name"] = "Claude",
                ["redirect_uris"] = new JsonArray("https://claude.ai/api/mcp/auth_callback"),
            }.ToJsonString();

            door.WebDoor.Arm();
            await WebSignInWait.UntilTaking(door.Port);
            door.AnswerAllow = false;
            await Assert.ThrowsAnyAsync<Exception>(() => door.ConnectAsync("2025-11-25", address));
            Assert.Empty(door.Store.Grants);

            door.WebDoor.Arm();
            await WebSignInWait.UntilTaking(door.Port);
            door.AnswerAllow = true;
            await using var client = await door.ConnectAsync("2025-11-25", address);
            var grant = door.Store.Grants.Single();
            Assert.Equal(address.AbsoluteUri, grant.ClientId);
            Assert.True(door.Fetched >= 2);
            var status = await client.CallToolAsync("status");
            Assert.NotEqual(true, status.IsError);
        }

        [Fact]
        public async Task AnExpiredAccessToken_IsRefreshedByTheClient_AndTheRotatedHashReachesTheFile()
        {
            await using var door = new Door();
            await door.StartAsync();
            door.WebDoor.Arm();
            await WebSignInWait.UntilTaking(door.Port);
            var cache = new KeptTokens();
            await using (var client = await door.ConnectAsync(cache: cache))
                await client.ListToolsAsync();
            var first = door.Store.Grants.Single();
            // The token's hour is over: the app's file says it ended.
            door.Store.Tokens(first with { AccessUntil = DateTimeOffset.UtcNow.AddMinutes(-1) });
            await door.WebDoor.SayStateAsync();
            // The helper holds the ended token once the old one is refused.
            string old = (await cache.GetTokensAsync(default)).AccessToken;
            using (var http = new HttpClient())
                await AssistantRig.Until(() =>
                {
                    var probe = new HttpRequestMessage(HttpMethod.Post, door.Endpoint) { Content = new StringContent("{}") };
                    probe.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", old);
                    return http.Send(probe).StatusCode == HttpStatusCode.Unauthorized;
                });
            await using (var again = await door.ConnectAsync(cache: cache))
                await again.ListToolsAsync();
            await AssistantRig.Until(() => door.Store.Grants.Single().RefreshHash != first.RefreshHash);
            var second = door.Store.Grants.Single();
            Assert.Equal(first.Id, second.Id);
            Assert.NotEqual(first.RefreshHash, second.RefreshHash);
            Assert.True(second.AccessUntil > DateTimeOffset.UtcNow);
            // No second card: a refresh is no sign-in.
            Assert.Empty(door.Rig.Host.Approvals.Pending);
        }

        [Fact]
        public async Task AHelperPadForgeDidNotStart_IsRefused()
        {
            await using var door = new Door();
            door.WebDoor.TurnOn();
            int code = await WebHelper.RunAsync(WebSecrets.New(), door.Rig.PipeName, CancellationToken.None);
            Assert.Equal(WebHelper.Refused, code);
            Assert.Equal(WebHelper.Refused, await WebHelper.RunAsync(null, door.Rig.PipeName, CancellationToken.None));
            Assert.Equal(WebHelper.NotRunning, await WebHelper.RunAsync(WebSecrets.New(), "PadForge.Tests.NoSuchPipe." + Guid.NewGuid().ToString("N"), CancellationToken.None));
        }
    }
}
