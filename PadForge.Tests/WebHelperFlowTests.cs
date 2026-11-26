using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Services.Automation.Web;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The web helper's own parts the audit of 2026-11-26 found wrong (#600
    /// item 11): a client document's host that names the home network
    /// through NAT64 or 6to4, a refresh token presented twice at once, lines
    /// to PadForge that leave out of order, and cloudflared's own error
    /// line read as an address.
    /// </summary>
    public class WebHelperFlowTests
    {
        private const string Base = "https://quiet-lake-4b2c.ngrok-free.app";

        [Fact]
        public void AnIpv6AddressThatCarriesAPrivateIpv4One_IsNotPublic()
        {
            // RFC 6052 NAT64, the well-known prefix: 192.168.1.1 and 10.0.0.1.
            Assert.False(WebClientDocuments.IsPublic(IPAddress.Parse("64:ff9b::c0a8:101")));
            Assert.False(WebClientDocuments.IsPublic(IPAddress.Parse("64:ff9b::a00:1")));
            // RFC 8215, the local-use prefix.
            Assert.False(WebClientDocuments.IsPublic(IPAddress.Parse("64:ff9b:1::c0a8:101")));
            // RFC 3056 6to4: 192.168.1.1.
            Assert.False(WebClientDocuments.IsPublic(IPAddress.Parse("2002:c0a8:101::")));
            // RFC 4291 2.5.5.1, IPv4-compatible: 127.0.0.1.
            Assert.False(WebClientDocuments.IsPublic(IPAddress.Parse("::7f00:1")));
            // A public IPv4 address behind either prefix stays public.
            Assert.True(WebClientDocuments.IsPublic(IPAddress.Parse("64:ff9b::a04f:680a")));
            Assert.True(WebClientDocuments.IsPublic(IPAddress.Parse("2002:a04f:680a::1")));
            Assert.True(WebClientDocuments.IsPublic(IPAddress.Parse("2607:6bc0::10")));
        }

        /// <summary>The app's side with a slow file, so two refreshes of one
        /// token overlap.</summary>
        private sealed class SlowApp : IWebAppSide
        {
            private readonly object _sync = new();
            private readonly List<WebGrant> _list = new();
            public SlowApp(WebGrant grant) => _list.Add(grant);
            public DateTimeOffset ArmedUntil => DateTimeOffset.MinValue;
            public IReadOnlyCollection<WebGrant> Grants { get { lock (_sync) return _list.ToList(); } }
            public Task<string> AskAsync(WebSignIn signIn, CancellationToken cancel) => Task.FromResult<string>(null);
            public void Tokens(WebGrant grant)
            {
                Thread.Sleep(100);
                lock (_sync)
                {
                    _list.RemoveAll(g => g.Id == grant.Id);
                    _list.Add(grant);
                }
            }
            public void Used(string grantId) { }
        }

        [Fact]
        public async Task ARefreshTokenPresentedTwiceAtOnce_WorksOnce()
        {
            string token = WebSecrets.New();
            var app = new SlowApp(new WebGrant
            {
                Id = "g", Origin = "claude.ai", ClientId = "c", Resource = Base + "/mcp",
                RefreshHash = WebSecrets.Hash(token), RefreshUntil = DateTimeOffset.UtcNow.AddDays(1),
            });
            var auth = new WebAuth(app, (_, _) => Task.FromResult<string>(null));
            using var go = new Barrier(2);
            int Refresh() { go.SignalAndWait(); return auth.Token("grant_type=refresh_token&refresh_token=" + token).Status; }
            var answers = await Task.WhenAll(Task.Run(Refresh), Task.Run(Refresh));
            Assert.Equal(1, answers.Count(a => a == 200));
            Assert.Equal(1, answers.Count(a => a == 400));
        }

        [Fact]
        public async Task LinesToPadForge_LeaveInTheOrderTheyWereSaid()
        {
            var stream = new RecordingLink();
            await using (var link = new WebLink(stream))
            {
                // Said from a pool thread, as the helper's reads and
                // callbacks say them.
                await Task.Run(() => { for (int i = 0; i < 4000; i++) link.Say(new JsonObject { ["n"] = i }); });
                await AssistantRig.Until(() => stream.Lines.Count == 4000, 10000);
            }
            Assert.Equal(Enumerable.Range(0, 4000), stream.Messages.Select(m => m["n"].GetValue<int>()));
        }

        [Fact]
        public void CloudflaredsOwnErrorLine_IsNoAddress()
        {
            var cloudflared = WebTunnels.Of("cloudflared");
            // cloudflared cmd/cloudflared/tunnel/quick_tunnel.go RunQuickTunnel:
            // errors.Wrap(err, "failed to request quick Tunnel"), printed by
            // urfave/cli's HandleExitCoder on stderr.
            Assert.Null(cloudflared.AddressIn(
                "failed to request quick Tunnel: Post \"https://api.trycloudflare.com/tunnel\": dial tcp: lookup api.trycloudflare.com: no such host"));
            Assert.Equal("https://seasonal-deck-organisms-sf.trycloudflare.com",
                cloudflared.AddressIn("2026-11-24T12:00:00Z INF |  https://seasonal-deck-organisms-sf.trycloudflare.com                                     |"));
            // A process's lines are read for the address only after the
            // banner quickTunnelStartupLines prints before it.
            var read = WebTunnels.AddressReader(cloudflared);
            Assert.Null(read("2026-11-24T12:00:00Z INF |  https://early-one.trycloudflare.com  |"));
            Assert.Null(read("2026-11-24T12:00:00Z INF |  Your quick Tunnel has been created! Visit it at (it may take some time to be reachable):  |"));
            Assert.Equal("https://seasonal-deck-organisms-sf.trycloudflare.com",
                read("2026-11-24T12:00:00Z INF |  https://seasonal-deck-organisms-sf.trycloudflare.com                                     |"));
            // A kind with no banner reads every line.
            Assert.Equal("https://box.tail1234.ts.net", WebTunnels.AddressReader(WebTunnels.Of("tailscale"))("https://box.tail1234.ts.net/"));
        }
    }
}
