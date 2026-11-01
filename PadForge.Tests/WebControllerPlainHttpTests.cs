using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    /// <summary>
    /// The web controller's plain HTTP address: a second port on the main
    /// address's listener, for browsers that refuse PadForge's certificate and
    /// for a tunnel or reverse proxy that brings its own. It is guarded by an
    /// access code carried as ?code= and traded for a cookie, because the
    /// pages use absolute paths that have no room for it.
    /// </summary>
    public sealed class WebControllerPlainHttpTests
    {
        // ── The access code ──

        [Fact]
        public void GeneratedCodesAreValidAndUseOnlyTheUnambiguousAlphabet()
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < 500; i++)
            {
                string code = WebControllerAccess.Generate();
                Assert.Equal(WebControllerAccess.CodeLength, code.Length);
                Assert.True(WebControllerAccess.IsValid(code), code);
                Assert.DoesNotContain(code, c => "ILOU".Contains(c));
                seen.Add(code);
            }
            // 50 bits each: a repeat in 500 draws would mean a broken generator.
            Assert.Equal(500, seen.Count);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("ABCDEFGHJ")]
        [InlineData("ABCDEFGHJKM")]
        [InlineData("ABCDEFGHJI")]
        [InlineData("ABCDEFGHJO")]
        [InlineData("ABCDEFGHJU")]
        [InlineData("ABCDEFGHJL")]
        [InlineData("ABCDE-GHJK")]
        public void InvalidCodesAreRejected(string code)
            => Assert.False(WebControllerAccess.IsValid(code));

        [Fact]
        public void CodesNormalizeCaseAndSurroundingSpace()
        {
            Assert.True(WebControllerAccess.IsValid(" abcdefghjk "));
            Assert.Equal("ABCDEFGHJK", WebControllerAccess.Normalize(" abcdefghjk "));
            Assert.True(WebControllerAccess.Matches("abcdefghjk", "ABCDEFGHJK"));
            Assert.False(WebControllerAccess.Matches("ABCDEFGHJM", "ABCDEFGHJK"));
            Assert.False(WebControllerAccess.Matches(null, "ABCDEFGHJK"));
            Assert.False(WebControllerAccess.Matches("ABCDEFGHJK", null));
        }

        [Theory]
        [InlineData("PadForgeWebCode=ABCDEFGHJK", "ABCDEFGHJK")]
        [InlineData("a=1; PadForgeWebCode=ABCDEFGHJK; b=2", "ABCDEFGHJK")]
        [InlineData("PadForgeWebCodeX=ABCDEFGHJK", null)]
        [InlineData("XPadForgeWebCode=ABCDEFGHJK", null)]
        [InlineData("a=1", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void TheCookieIsReadByExactName(string header, string expected)
            => Assert.Equal(expected, WebControllerAccess.ReadCookie(header));

        [Fact]
        public void AdmissionChecksThePeerFirstThenTheCookieThenTheQuery()
        {
            const string code = "ABCDEFGHJK";
            var lan = IPAddress.Parse("192.168.1.20");
            string cookie = "PadForgeWebCode=" + code;
            string stale = "PadForgeWebCode=ZZZZZZZZZZ";

            Assert.Equal(WebControllerAccess.Decision.Allow,
                WebControllerAccess.Evaluate(code, false, lan, null, cookie));
            Assert.Equal(WebControllerAccess.Decision.AllowAndSetCookie,
                WebControllerAccess.Evaluate(code, false, lan, code.ToLowerInvariant(), null));
            // A stale cookie from an old code gives way to the right query,
            // which replaces it.
            Assert.Equal(WebControllerAccess.Decision.AllowAndSetCookie,
                WebControllerAccess.Evaluate(code, false, lan, code, stale));
            Assert.Equal(WebControllerAccess.Decision.DenyMissingCode,
                WebControllerAccess.Evaluate(code, false, lan, null, stale));
            Assert.Equal(WebControllerAccess.Decision.DenyMissingCode,
                WebControllerAccess.Evaluate(code, false, lan, "ZZZZZZZZZZ", null));
            Assert.Equal(WebControllerAccess.Decision.DenyMissingCode,
                WebControllerAccess.Evaluate(null, false, lan, code, cookie));

            // This PC Only turns a LAN peer away even with the right code, and
            // admits loopback in both address families.
            Assert.Equal(WebControllerAccess.Decision.DenyNotLocal,
                WebControllerAccess.Evaluate(code, true, lan, code, cookie));
            Assert.Equal(WebControllerAccess.Decision.DenyNotLocal,
                WebControllerAccess.Evaluate(code, true, null, code, cookie));
            Assert.Equal(WebControllerAccess.Decision.Allow,
                WebControllerAccess.Evaluate(code, true, IPAddress.Loopback, null, cookie));
            Assert.Equal(WebControllerAccess.Decision.Allow,
                WebControllerAccess.Evaluate(code, true, IPAddress.IPv6Loopback, null, cookie));
            Assert.Equal(WebControllerAccess.Decision.Allow,
                WebControllerAccess.Evaluate(code, true, IPAddress.Loopback.MapToIPv6(), null, cookie));
        }

        [Fact]
        public void TheCookieIsHttpOnlyStrictAndEndsWithTheSession()
        {
            string value = WebControllerAccess.SetCookieValue("abcdefghjk");
            Assert.StartsWith("PadForgeWebCode=ABCDEFGHJK;", value);
            Assert.Contains("; Path=/", value);
            Assert.Contains("; HttpOnly", value);
            Assert.Contains("; SameSite=Strict", value);
            Assert.DoesNotContain("Expires", value);
            Assert.DoesNotContain("Max-Age", value);
        }

        [Fact]
        public void TheStoredCodeIsEncryptedAndRoundTrips()
        {
            string code = WebControllerAccess.Generate();
            string stored = WebControllerAccess.ProtectForStorage(code);
            Assert.NotNull(stored);
            Assert.DoesNotContain(code, stored);
            Assert.Equal(code, WebControllerAccess.UnprotectFromStorage(stored));

            Assert.Null(WebControllerAccess.ProtectForStorage("short"));
            Assert.Null(WebControllerAccess.UnprotectFromStorage(null));
            Assert.Null(WebControllerAccess.UnprotectFromStorage("not base64!"));
            Assert.Null(WebControllerAccess.UnprotectFromStorage(Convert.ToBase64String(new byte[64])));
            // A code stored in the clear, as a hand edit would leave it, is not accepted.
            Assert.Null(WebControllerAccess.UnprotectFromStorage(code));
        }

        // ── The server ──

        [Fact]
        public void ThePlainPrefixJoinsTheMainOneOnTheSameListener()
        {
            var firewall = new List<WebControllerServer.PlainHttpOptions>();
            var listener = new RecordingListener();
            using var server = new WebControllerServer(_ => null, () => listener,
                plainFirewall: firewall.Add);
            string code = WebControllerAccess.Generate();
            server.SetPlainAccessCode(code);
            var plain = new WebControllerServer.PlainHttpOptions(18081, false);

            Assert.True(server.Start(18080, plain));
            Assert.Equal(new[] { "http://+:18080/", "http://+:18081/" }, listener.Prefixes);
            Assert.True(server.IsPlainServing);
            Assert.False(server.IsPlainLocalOnly);
            Assert.StartsWith("http://", server.PlainUrl);
            Assert.EndsWith(":18081/?code=" + code, server.PlainUrl);
            Assert.Equal(string.Format(Strings.Instance.Server_RunningOn_Format,
                server.PlainUrl.Substring(0, server.PlainUrl.IndexOf("/?", StringComparison.Ordinal))), server.PlainStatus);
            Assert.Equal(new[] { plain }, firewall);

            server.Stop();
            Assert.False(server.IsPlainServing);
            Assert.Null(server.PlainUrl);
            Assert.Null(server.DisplayUrl);
            Assert.Null(server.PlainStatus);
            // Stopping closes the plain port's firewall opening.
            Assert.Equal(new WebControllerServer.PlainHttpOptions[] { plain, null }, firewall);
        }

        [Fact]
        public void ThisPcOnlyNamesLocalhost()
        {
            using var server = new WebControllerServer(_ => null, () => new RecordingListener());
            server.SetPlainAccessCode("abcdefghjk");
            Assert.True(server.Start(18080, new WebControllerServer.PlainHttpOptions(18081, true)));
            Assert.True(server.IsPlainLocalOnly);
            Assert.Equal("http://localhost:18081/?code=ABCDEFGHJK", server.PlainUrl);
            // The controller picture (#555) rides the plain address, the one
            // OBS loads: the main address's certificate is self-signed.
            Assert.Equal("http://localhost:18081/display.html?code=ABCDEFGHJK", server.DisplayUrl);
            Assert.Equal(string.Format(Strings.Instance.Server_RunningOn_Format, "http://localhost:18081"), server.PlainStatus);
        }

        [Fact]
        public void TheMainPortIsNotSharedWithThePlainAddress()
        {
            var firewall = new List<WebControllerServer.PlainHttpOptions>();
            var listener = new RecordingListener();
            using var server = new WebControllerServer(_ => null, () => listener, plainFirewall: firewall.Add);
            server.SetPlainAccessCode(WebControllerAccess.Generate());
            Assert.True(server.Start(18080, new WebControllerServer.PlainHttpOptions(18080, false)));
            Assert.True(server.IsRunning);
            Assert.False(server.IsPlainServing);
            Assert.Equal(new[] { "http://+:18080/" }, listener.Prefixes);
            Assert.Equal(Strings.Instance.Server_PlainSamePort, server.PlainStatus);
            // Nothing served, so the opening is closed rather than made.
            Assert.Equal(new WebControllerServer.PlainHttpOptions[] { null }, firewall);
        }

        [Fact]
        public void WithoutAValidCodeThePlainAddressStaysClosed()
        {
            var listener = new RecordingListener();
            using var server = new WebControllerServer(_ => null, () => listener);
            server.SetPlainAccessCode("not a code");
            Assert.True(server.Start(18080, new WebControllerServer.PlainHttpOptions(18081, false)));
            Assert.False(server.IsPlainServing);
            Assert.Null(server.PlainUrl);
            Assert.Null(server.DisplayUrl);
            Assert.Equal(new[] { "http://+:18080/" }, listener.Prefixes);
            Assert.Equal(Strings.Instance.Server_FailedToStart, server.PlainStatus);
        }

        [Theory]
        [InlineData(5, "denied")]
        [InlineData(32, "inuse")]
        [InlineData(183, "inuse")]
        [InlineData(-1, "failed")]
        public void ARefusedPlainPortLeavesTheMainAddressServing(int error, string kind)
        {
            var listener = new RecordingListener
            {
                OnAddPrefix = error < 0
                    ? () => throw new InvalidOperationException("unexpected")
                    : () => throw new HttpListenerException(error),
            };
            using var server = new WebControllerServer(_ => null, () => listener);
            server.SetPlainAccessCode(WebControllerAccess.Generate());
            Assert.True(server.Start(18080, new WebControllerServer.PlainHttpOptions(18081, false)));
            Assert.True(server.IsRunning);
            Assert.Equal(0, listener.Closes);
            Assert.False(server.IsPlainServing);
            string expected = kind switch
            {
                "denied" => string.Format(Strings.Instance.Server_AccessDenied_Format, 18081),
                "inuse" => string.Format(Strings.Instance.Server_PortInUse_Format, 18081),
                _ => Strings.Instance.Server_FailedToStart,
            };
            Assert.Equal(expected, server.PlainStatus);
        }

        [Fact]
        public void NoPlainRequestMeansNoPlainStatus()
        {
            using var server = new WebControllerServer(_ => null, () => new RecordingListener());
            Assert.True(server.Start(18080));
            Assert.Null(server.PlainStatus);
            Assert.Null(server.PlainUrl);
            Assert.Null(server.DisplayUrl);
            Assert.False(server.IsPlainServing);
        }

        // The failure kind travels as an int: the enum is internal, and a
        // public test method cannot take it.
        [Theory]
        [InlineData((int)WebControllerTls.Failure.PortTakenByOtherApp)]
        [InlineData((int)WebControllerTls.Failure.BindFailed)]
        [InlineData((int)WebControllerTls.Failure.None)]
        public void AnHttpFallbackSaysWhyPhoneMotionIsOff(int kind)
        {
            var failure = (WebControllerTls.Failure)kind;
            using var server = new WebControllerServer(_ => null, () => new RecordingListener(),
                httpsFailure: _ => failure);
            Assert.True(server.Start(18080));
            Assert.False(server.IsHttps);
            string expected = failure switch
            {
                WebControllerTls.Failure.PortTakenByOtherApp => string.Format(Strings.Instance.Server_HttpsPortTaken_Format, 18080),
                WebControllerTls.Failure.BindFailed => Strings.Instance.Server_HttpsBindFailed,
                // An injected endpoint that attempted nothing has no reason.
                _ => null,
            };
            Assert.Equal(expected, server.HttpsUnavailable);
            server.Stop();
            Assert.Null(server.HttpsUnavailable);
        }

        [Fact]
        public void ARefusedHttpsListenerSaysSo()
        {
            var pool = new WebControllerBindingPool(_ => true, _ => { }, _ => { });
            int created = 0;
            using var server = new WebControllerServer(pool.Acquire, () => ++created == 1
                ? new RecordingListener { OnStart = () => throw new HttpListenerException(5) }
                : new RecordingListener());
            Assert.True(server.Start(18080));
            Assert.False(server.IsHttps);
            Assert.Equal(string.Format(Strings.Instance.Server_HttpsListenerRejected_Format, 18080), server.HttpsUnavailable);
        }

        [Fact]
        public void ANewCodeDropsOnlyThePlainSessions()
        {
            using var server = new WebControllerServer(_ => null, () => new RecordingListener());
            server.SetPlainAccessCode("ABCDEFGHJK");
            var main = AddSession(server, "main", viaPlain: false);
            var plainA = AddSession(server, "plainA", viaPlain: true);
            var plainB = AddSession(server, "plainB", viaPlain: true);

            // The same code, differently cased, and a code that is not one change nothing.
            server.SetPlainAccessCode("abcdefghjk");
            server.SetPlainAccessCode("bad");
            Assert.False(plainA.IsCancellationRequested);

            server.SetPlainAccessCode("MNPQRSTVWX");
            Assert.True(plainA.IsCancellationRequested);
            Assert.True(plainB.IsCancellationRequested);
            Assert.False(main.IsCancellationRequested);
            ClearSessions(server);
        }

        [Fact]
        public async Task ACodeSetWhileTheServerStartsIsTheOneItEnforces()
        {
            using var gate = new ManualResetEventSlim();
            using var entered = new ManualResetEventSlim();
            using var server = new WebControllerServer(_ =>
            {
                entered.Set();
                gate.Wait(TimeSpan.FromSeconds(5));
                return null;
            }, () => new RecordingListener());
            server.SetPlainAccessCode("ABCDEFGHJK");
            var start = Task.Run(() =>
                server.Start(18080, new WebControllerServer.PlainHttpOptions(18081, true)));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            server.SetPlainAccessCode("MNPQRSTVWX");
            gate.Set();
            Assert.True(await start.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("http://localhost:18081/?code=MNPQRSTVWX", server.PlainUrl);
        }

        // ── Over the wire ──

        /// <summary>The whole admission path on a real http.sys listener:
        /// the refusal, the code traded for a cookie, the cookie admitting
        /// the page's later requests, a new code shutting the old cookie out,
        /// and the main address asking for nothing. The prefixes bind
        /// 127.0.0.1 instead of +, which needs no elevation.</summary>
        [Fact]
        public async Task ThePlainAddressAdmitsOnlyTheCodeAndItsCookie()
        {
            using var run = LoopbackRun.Start(localOnly: false);
            var server = run.Server;
            using var http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(10) };
            string code = run.Code;
            string plain = $"http://127.0.0.1:{run.PlainPort}";

            // The main address asks for nothing and sets nothing.
            using (var r = await http.GetAsync($"http://127.0.0.1:{run.MainPort}/"))
            {
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                Assert.False(r.Headers.Contains("Set-Cookie"));
            }

            // No code, a wrong code: refused with a page that says what to do.
            using (var r = await http.GetAsync(plain + "/"))
            {
                Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
                Assert.Contains("access code", await r.Content.ReadAsStringAsync());
            }
            using (var r = await http.GetAsync(plain + "/?code=ZZZZZZZZZZ"))
                Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            // A HEAD is refused with headers alone, not a reset connection.
            using (var head = new HttpRequestMessage(HttpMethod.Head, plain + "/"))
            using (var r = await http.SendAsync(head))
                Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

            // The code, in any case, opens the page and sets the cookie.
            using (var r = await http.GetAsync(plain + "/?code=" + code.ToLowerInvariant()))
            {
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                Assert.Equal(WebControllerAccess.SetCookieValue(code), Assert.Single(r.Headers.GetValues("Set-Cookie")));
            }

            // The page's later requests carry only the cookie.
            Assert.Equal(HttpStatusCode.OK, await Send(http, plain + "/controller.html?layout=xbox360", code));
            using (var request = new HttpRequestMessage(HttpMethod.Get, plain + "/api/info"))
            {
                request.Headers.Add("Cookie", "PadForgeWebCode=" + code);
                using var r = await http.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                Assert.False(r.Headers.Contains("Set-Cookie"));
                // The main address is plain HTTP here, and the peer is this PC.
                Assert.Equal("{\"secureUrl\":null}", await r.Content.ReadAsStringAsync());
            }

            // A new code shuts the old cookie out, and the new code gets in.
            string next = WebControllerAccess.Generate();
            server.SetPlainAccessCode(next);
            Assert.Equal(HttpStatusCode.Forbidden, await Send(http, plain + "/controller.html", code));
            using (var r = await http.GetAsync(plain + "/?code=" + next))
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal(HttpStatusCode.OK, await Send(http, plain + "/controller.html", next));
        }

        /// <summary>A new code ends the sockets opened through the plain
        /// address and leaves the main address's socket open, and the old
        /// cookie can no longer open one.</summary>
        [Fact]
        public async Task ANewCodeClosesPlainSocketsAndLeavesMainSocketsOpen()
        {
            using var run = LoopbackRun.Start(localOnly: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            string query = "/ws?type=xbox360&layout=xbox360&id=";

            using var plainSocket = new System.Net.WebSockets.ClientWebSocket();
            plainSocket.Options.SetRequestHeader("Cookie", "PadForgeWebCode=" + run.Code);
            await plainSocket.ConnectAsync(new Uri($"ws://127.0.0.1:{run.PlainPort}{query}plain1"), timeout.Token);
            using var mainSocket = new System.Net.WebSockets.ClientWebSocket();
            await mainSocket.ConnectAsync(new Uri($"ws://127.0.0.1:{run.MainPort}{query}main1"), timeout.Token);
            Assert.Contains("\"connected\"", await ReceiveText(plainSocket, timeout.Token));
            Assert.Contains("\"connected\"", await ReceiveText(mainSocket, timeout.Token));
            Assert.Equal(2, run.Server.ClientCount);

            run.Server.SetPlainAccessCode(WebControllerAccess.Generate());
            await ReceiveUntilClosed(plainSocket, timeout.Token);
            while (run.Server.ClientCount != 1)
                await Task.Delay(20, timeout.Token);
            Assert.Equal(System.Net.WebSockets.WebSocketState.Open, mainSocket.State);

            using var again = new System.Net.WebSockets.ClientWebSocket();
            again.Options.SetRequestHeader("Cookie", "PadForgeWebCode=" + run.Code);
            await Assert.ThrowsAnyAsync<System.Net.WebSockets.WebSocketException>(() =>
                again.ConnectAsync(new Uri($"ws://127.0.0.1:{run.PlainPort}{query}plain2"), timeout.Token));
        }

        /// <summary>The sweep that a new code runs cannot see a plain session
        /// that passed admission but has not registered yet, so registration
        /// itself refuses a code that changed in between. Both sides hold the
        /// registration lock, and the refusal comes before the session is
        /// installed. Pinned by structure: no hook exists between admission
        /// and registration to interleave a live test.</summary>
        [Fact]
        public void RegistrationRefusesAPlainSessionWhoseCodeWasReplaced()
        {
            var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root.FullName, "PadForge.sln"))) root = root.Parent;
            Assert.NotNull(root);
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(root.FullName,
                "PadForge.App", "Services", "WebControllerServer.cs"));

            int guard = source.IndexOf("if (!IsServing(generation) || lifetime.IsCancellationRequested)", StringComparison.Ordinal);
            int lockStart = source.LastIndexOf("lock (_registrationLock)", guard, StringComparison.Ordinal);
            int stale = source.IndexOf("if (viaPlain && admittedCode != _accessCode)", guard, StringComparison.Ordinal);
            int install = source.IndexOf("_clients[compositeKey] = session;", guard, StringComparison.Ordinal);
            Assert.True(lockStart >= 0 && guard > lockStart && stale > guard && install > stale);
            Assert.Equal(-1, source.IndexOf("lock (", guard, install - guard, StringComparison.Ordinal));

            int setter = source.IndexOf("public void SetPlainAccessCode(string code)", StringComparison.Ordinal);
            int setterLock = source.IndexOf("lock (_registrationLock)", setter, StringComparison.Ordinal);
            int write = source.IndexOf("_accessCode = normalized;", setter, StringComparison.Ordinal);
            int sweep = source.IndexOf("_clients.Values.Where(s => s.ViaPlain)", setter, StringComparison.Ordinal);
            int setterEnd = source.IndexOf("\n        }", setter, StringComparison.Ordinal);
            Assert.True(setterLock > setter && write > setterLock && sweep > write && sweep < setterEnd);
            // The only other writer of the code would bypass the sweep.
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"\b_accessCode = "));
        }

        /// <summary>The controller picture page (#555) watches a slot through
        /// a socket of its own: it gets the slot, the state and the chat,
        /// takes no client slot, and on the plain address closes when the
        /// streamer issues a new code, as the controllers do.</summary>
        [Fact]
        public async Task APicturePageWatchesASlot_AndANewCodeClosesItOnThePlainAddress()
        {
            using var run = LoopbackRun.Start(localOnly: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var page = new System.Net.WebSockets.ClientWebSocket();
            await page.ConnectAsync(new Uri($"ws://127.0.0.1:{run.PlainPort}/ws?view=2&code={run.Code}"), timeout.Token);
            string slot = await ReceiveText(page, timeout.Token);
            Assert.Contains("\"type\":\"slot\"", slot);
            Assert.Contains("\"slot\":2", slot);
            Assert.Contains("\"type\":\"state\"", await ReceiveText(page, timeout.Token));
            Assert.Contains("\"type\":\"chat\"", await ReceiveText(page, timeout.Token));
            Assert.Equal(0, run.Server.ClientCount);

            run.Server.SetPlainAccessCode(WebControllerAccess.Generate());
            await ReceiveUntilClosed(page, timeout.Token);
        }

        private static async Task<string> ReceiveText(System.Net.WebSockets.ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[4096];
            var result = await socket.ReceiveAsync(buffer, token);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
        }

        private static async Task ReceiveUntilClosed(System.Net.WebSockets.ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[4096];
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(buffer, token);
                    if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) return;
                }
            }
            catch (System.Net.WebSockets.WebSocketException) { /* the server aborted it */ }
        }

        /// <summary>On a real http.sys listener, a plain port another program
        /// already holds is refused and the main address keeps serving: a
        /// started HttpListener registers a new prefix before recording it,
        /// so the refusal leaves the registered one alone.</summary>
        [Fact]
        public async Task AHeldPlainPortLeavesTheMainAddressServing()
        {
            var rng = new Random();
            for (int attempt = 0; attempt < 10; attempt++)
            {
                int main = 20000 + rng.Next(20000);
                int plain = main + 1;
                using var holder = new HttpListener();
                holder.Prefixes.Add($"http://127.0.0.1:{plain}/");
                try { holder.Start(); } catch (HttpListenerException) { continue; }

                using var server = new WebControllerServer(_ => null, () => new LoopbackListener());
                server.SetPlainAccessCode(WebControllerAccess.Generate());
                if (!server.Start(main, new WebControllerServer.PlainHttpOptions(plain, false))) continue;

                Assert.False(server.IsPlainServing);
                Assert.Equal(string.Format(Strings.Instance.Server_PortInUse_Format, plain), server.PlainStatus);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var r = await http.GetAsync($"http://127.0.0.1:{main}/");
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                return;
            }
            throw new InvalidOperationException("no free loopback port pair");
        }

        [Fact]
        public async Task ThisPcOnlyStillAdmitsThisPc()
        {
            using var run = LoopbackRun.Start(localOnly: true);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var r = await http.GetAsync($"http://127.0.0.1:{run.PlainPort}/?code={run.Code}");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        private static async Task<HttpStatusCode> Send(HttpClient http, string url, string cookieCode)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", "theme=dark; PadForgeWebCode=" + cookieCode);
            using var r = await http.SendAsync(request);
            return r.StatusCode;
        }

        /// <summary>A server on two free loopback ports, retried when another
        /// process takes a probed port first.</summary>
        private sealed class LoopbackRun : IDisposable
        {
            public WebControllerServer Server;
            public int MainPort, PlainPort;
            public string Code;

            public static LoopbackRun Start(bool localOnly)
            {
                var rng = new Random();
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    int main = 20000 + rng.Next(20000);
                    int plain = main + 1;
                    var server = new WebControllerServer(_ => null, () => new LoopbackListener());
                    string code = WebControllerAccess.Generate();
                    server.SetPlainAccessCode(code);
                    if (server.Start(main, new WebControllerServer.PlainHttpOptions(plain, localOnly)) && server.IsPlainServing)
                        return new LoopbackRun { Server = server, MainPort = main, PlainPort = plain, Code = code };
                    server.Dispose();
                }
                throw new InvalidOperationException("no free loopback port pair");
            }

            public void Dispose() => Server.Dispose();
        }

        /// <summary>The production listener's behavior on 127.0.0.1 in place
        /// of the strong wildcard.</summary>
        private sealed class LoopbackListener : IWebControllerListener
        {
            private readonly HttpListener _listener = new();
            private static string Loopback(string prefix) => prefix.Replace("://+:", "://127.0.0.1:");
            public bool IsListening => _listener.IsListening;
            public void Start(string prefix) { _listener.Prefixes.Add(Loopback(prefix)); _listener.Start(); }
            public void AddPrefix(string prefix) => _listener.Prefixes.Add(Loopback(prefix));
            public HttpListenerContext GetContext() => _listener.GetContext();
            public void Stop() => _listener.Stop();
            public void Close() => _listener.Close();
        }

        private sealed class RecordingListener : IWebControllerListener
        {
            private readonly ManualResetEventSlim _stopped = new();
            public readonly List<string> Prefixes = new();
            public Action OnStart;
            public Action OnAddPrefix;
            public int Closes;
            public bool IsListening { get; private set; }
            public void Start(string prefix) { OnStart?.Invoke(); Prefixes.Add(prefix); IsListening = true; }
            public void AddPrefix(string prefix) { OnAddPrefix?.Invoke(); Prefixes.Add(prefix); }
            public HttpListenerContext GetContext()
            {
                _stopped.Wait(TimeSpan.FromSeconds(10));
                throw new HttpListenerException();
            }
            public void Stop() { IsListening = false; _stopped.Set(); }
            public void Close() { Closes++; Stop(); }
        }

        private static CancellationTokenSource AddSession(WebControllerServer server, string key, bool viaPlain)
        {
            var type = typeof(WebControllerServer).GetNestedType("ClientSession", BindingFlags.NonPublic);
            var cts = new CancellationTokenSource();
            object session = Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.Instance, null,
                new object[] { null, null, cts }, null);
            type.GetProperty("ViaPlain").SetValue(session, viaPlain);
            object clients = typeof(WebControllerServer).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(server);
            clients.GetType().GetMethod("TryAdd").Invoke(clients, new[] { key, session });
            return cts;
        }

        private static void ClearSessions(WebControllerServer server)
        {
            object clients = typeof(WebControllerServer).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(server);
            clients.GetType().GetMethod("Clear").Invoke(clients, null);
        }
    }
}
