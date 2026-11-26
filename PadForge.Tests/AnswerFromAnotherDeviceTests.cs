using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Engine.Privacy;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using PadForge.ViewModels;
using PadForge.Views;

namespace PadForge.Tests
{
    /// <summary>
    /// Answering from another device (#600): a question named by its request
    /// and its nonce is taken once, the page and its pairing on the answer
    /// route, the row's rule, the listener that serves the route on the web
    /// controller's HTTPS address alone, the pairing window kept out of
    /// screen capture, and the secrets kept out of every read and
    /// recording.
    /// </summary>
    public class AnswerFromAnotherDeviceTests
    {
        private const string Host = "192.168.1.24:8080";
        private static readonly IPAddress Phone = IPAddress.Parse("192.168.1.30");

        private static ApprovalRequest Change(string title = "Turn on Use Only in PadForge for Xbox Wireless Controller?")
            => new(new AutomationProgram("Office PC", 1, "office.exe", @"C:\Tools\office.exe", "Contoso"), title,
                new[] { "Other programs and games stop seeing this controller." });

        /// <summary>A page and its questions with a clock the test turns.</summary>
        private sealed class Rig
        {
            public DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
            public readonly AutomationApprovals Approvals;
            public readonly WebAnswer Answers;
            public bool Row = true;

            public Rig()
            {
                Approvals = new AutomationApprovals(TimeSpan.FromMinutes(2), () => Now);
                Answers = new WebAnswer(() => Now) { RowShows = () => Row };
                Answers.Attach(Approvals);
            }

            public AnswerReply Send(string method, string path, string body = null, string cookie = null,
                string origin = "https://" + Host, string host = Host, IPAddress from = null)
                => Answers.Handle(new AnswerRequest(method, path, host, origin, cookie, body, from ?? Phone));

            /// <summary>Pairs a device with the code and gives its cookie
            /// header.</summary>
            public string Pair(string name = "Pixel 8")
            {
                var pairing = Answers.StartPairing();
                var reply = Send("POST", "/answer/pair", JsonSerializer.Serialize(new { code = pairing.Code, name }));
                Assert.Equal(200, reply.Status);
                Assert.NotNull(reply.SetCookie);
                return reply.SetCookie.Split(';')[0];
            }

            public JsonArray Questions(string cookie)
            {
                var reply = Send("GET", "/answer/state", cookie: cookie);
                Assert.Equal(200, reply.Status);
                return JsonNode.Parse(reply.Json)["questions"].AsArray();
            }

            public AnswerReply Answer(string cookie, int id, string nonce, bool allow)
                => Send("POST", "/answer/answer", JsonSerializer.Serialize(new { id, nonce, allow }), cookie);
        }

        // ─────────────────────────────────────────────
        //  An answer names its request and is taken once
        // ─────────────────────────────────────────────

        [Fact]
        public async Task AnAnswerNamedByItsRequestAndNonce_IsTakenOnce_AndNoOther()
        {
            var approvals = new AutomationApprovals(TimeSpan.FromMinutes(2));
            var first = approvals.AskAsync(Change("first"));
            var second = approvals.AskAsync(Change("second"));
            var pending = approvals.Pending;
            Assert.Equal(32, pending[0].Nonce.Length);
            Assert.NotEqual(pending[0].Nonce, pending[1].Nonce);

            // Another request's nonce, and none, answer nothing.
            Assert.False(approvals.AnswerNamed(pending[0].Id, pending[1].Nonce, true, null));
            Assert.False(approvals.AnswerNamed(pending[0].Id, null, true, null));
            // A question a device may not answer stays.
            Assert.False(approvals.AnswerNamed(pending[0].Id, pending[0].Nonce, true, _ => false));
            Assert.False(first.IsCompleted);

            Assert.True(approvals.AnswerNamed(pending[0].Id, pending[0].Nonce, true, null));
            Assert.Equal(ApprovalAnswer.Allowed, await first);
            // A replay is refused, and the other question still waits.
            Assert.False(approvals.AnswerNamed(pending[0].Id, pending[0].Nonce, false, null));
            Assert.False(second.IsCompleted);
            Assert.True(approvals.AnswerNamed(pending[1].Id, pending[1].Nonce, false, null));
            Assert.Equal(ApprovalAnswer.Denied, await second);
        }

        // ─────────────────────────────────────────────
        //  The page
        // ─────────────────────────────────────────────

        [Fact]
        public async Task APairedDevicesAllow_ReachesTheQuestion_AndTheCallGoesOn()
        {
            var rig = new Rig();
            string cookie = rig.Pair();
            var asked = rig.Approvals.AskAsync(Change());
            var list = rig.Questions(cookie);
            var q = Assert.Single(list);
            Assert.Equal("Turn on Use Only in PadForge for Xbox Wireless Controller?", q["title"].GetValue<string>());
            Assert.Equal("Office PC", q["program"].GetValue<string>());
            Assert.Equal(@"C:\Tools\office.exe", q["path"].GetValue<string>());
            Assert.Equal("Contoso", q["signer"].GetValue<string>());
            Assert.Equal("Other programs and games stop seeing this controller.", q["detail"][0].GetValue<string>());
            Assert.Equal(120, q["waits"].GetValue<int>());
            Assert.Equal("change", q["kind"].GetValue<string>());

            var reply = rig.Answer(cookie, q["id"].GetValue<int>(), q["nonce"].GetValue<string>(), true);
            Assert.Equal(200, reply.Status);
            Assert.Equal(ApprovalAnswer.Allowed, await asked);
            Assert.Equal(rig.Now, rig.Answers.Devices.Single().LastAnswered);
            Assert.Equal("Pixel 8", rig.Answers.Devices.Single().Name);
        }

        [Fact]
        public async Task AReplay_AndAnAnswerNamingAnotherRequest_AreRefused()
        {
            var rig = new Rig();
            string cookie = rig.Pair();
            var first = rig.Approvals.AskAsync(Change("first"));
            var second = rig.Approvals.AskAsync(Change("second"));
            var list = rig.Questions(cookie);
            int id1 = list[0]["id"].GetValue<int>(), id2 = list[1]["id"].GetValue<int>();
            string n1 = list[0]["nonce"].GetValue<string>(), n2 = list[1]["nonce"].GetValue<string>();

            Assert.Equal(409, rig.Answer(cookie, id1, n2, true).Status);
            Assert.Equal(409, rig.Answer(cookie, id1 + 100, n1, true).Status);
            Assert.False(first.IsCompleted);
            Assert.Equal(200, rig.Answer(cookie, id1, n1, false).Status);
            Assert.Equal(ApprovalAnswer.Denied, await first);
            Assert.Equal(409, rig.Answer(cookie, id1, n1, true).Status);
            Assert.False(second.IsCompleted);
        }

        [Fact]
        public void AnUnpairedOrRemovedDevice_GetsUnauthorized()
        {
            var rig = new Rig();
            var asked = rig.Approvals.AskAsync(Change());
            var p = rig.Approvals.Pending.Single();
            // With nothing paired and no pairing open the route is off.
            Assert.Equal(404, rig.Send("GET", "/answer/state").Status);

            string kept = rig.Pair("Office laptop");
            string cookie = rig.Pair();
            Assert.Equal(401, rig.Send("GET", "/answer/state").Status);
            Assert.Equal(401, rig.Answer(null, p.Id, p.Nonce, true).Status);
            Assert.Equal(401, rig.Answer(WebAnswer.CookieName + "=" + WebSecrets.New(), p.Id, p.Nonce, true).Status);

            Assert.Single(rig.Questions(cookie));
            Assert.True(rig.Answers.Remove(rig.Answers.Devices.Single(d => d.Name == "Pixel 8").Id));
            Assert.Equal(401, rig.Send("GET", "/answer/state", cookie: cookie).Status);
            Assert.Equal(401, rig.Answer(cookie, p.Id, p.Nonce, true).Status);
            Assert.False(asked.IsCompleted);
            Assert.Single(rig.Questions(kept));
        }

        /// <summary>An assistant's key, a web token or a network key, never
        /// answers: the route reads no Authorization header, and such a key
        /// in the device cookie matches no device.</summary>
        [Fact]
        public async Task AnAssistantsKey_NeverAnswers()
        {
            var rig = new Rig();
            rig.Pair();
            var asked = rig.Approvals.AskAsync(Change());
            var p = rig.Approvals.Pending.Single();
            // A web assistant's own token, as its grant keeps it.
            var store = new WebAccessStore();
            string token = WebSecrets.New();
            store.Add(new WebGrant { Id = "g1", Origin = "claude.ai", AccessHash = WebSecrets.Hash(token), Allowed = DateTimeOffset.Now });
            Assert.True(WebSecrets.Matches(token, store.Grants.Single().AccessHash));
            string body = JsonSerializer.Serialize(new { id = p.Id, nonce = p.Nonce, allow = true });

            // On the listener: as a Bearer token, and in the device cookie.
            using (var run = Run(rig.Answers, https: true, controls: false))
            {
                string host = "127.0.0.1:" + run.Port;
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                foreach (var (header, value) in new[] { ("Authorization", "Bearer " + token), ("Cookie", WebAnswer.CookieName + "=" + token) })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "http://" + host + "/answer/answer")
                    {
                        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                    };
                    request.Headers.TryAddWithoutValidation("Origin", "https://" + host);
                    request.Headers.TryAddWithoutValidation(header, value);
                    using var reply = await http.SendAsync(request);
                    Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
                }
            }
            Assert.Equal(401, rig.Answer(WebAnswer.CookieName + "=" + token, p.Id, p.Nonce, true).Status);
            Assert.False(asked.IsCompleted);
        }

        [Fact]
        public void FiveWrongCodes_CloseThePairing_AndTheRightCodeIsThenRefused()
        {
            var rig = new Rig();
            // A device paired before keeps the route on, so the refusals
            // below are the pairing's own.
            rig.Pair("Office laptop");
            var ended = new List<AnswerPairingEnd>();
            rig.Answers.PairingEnded += (_, how) => ended.Add(how);
            var pairing = rig.Answers.StartPairing();
            string wrong = pairing.Code == "000000" ? "111111" : "000000";
            for (int i = 1; i <= 4; i++)
            {
                var reply = rig.Send("POST", "/answer/pair", JsonSerializer.Serialize(new { code = wrong }));
                Assert.Equal(401, reply.Status);
                Assert.Equal(5 - i, JsonNode.Parse(reply.Json)["left"].GetValue<int>());
            }
            Assert.Empty(ended);
            Assert.Equal(410, rig.Send("POST", "/answer/pair", JsonSerializer.Serialize(new { code = wrong })).Status);
            Assert.Equal(new[] { AnswerPairingEnd.TooManyTries }, ended);
            Assert.Null(rig.Answers.Pairing);
            Assert.Equal(410, rig.Send("POST", "/answer/pair", JsonSerializer.Serialize(new { code = pairing.Code })).Status);
            Assert.Single(rig.Answers.Devices);
        }

        [Fact]
        public void TheQrCodesSecret_Pairs_Once_AndAPairingEndsAfterFiveMinutes()
        {
            var rig = new Rig();
            var pairing = rig.Answers.StartPairing();
            Assert.True(pairing.Secret.Length >= 43);
            Assert.Equal("https://192.168.1.24:8080/answer#pair=" + pairing.Secret,
                WebAnswer.PairingLink("https://192.168.1.24:8080/answer", pairing));
            var reply = rig.Send("POST", "/answer/pair", JsonSerializer.Serialize(new { secret = pairing.Secret }));
            Assert.Equal(200, reply.Status);
            Assert.StartsWith(WebAnswer.CookieName + "=", reply.SetCookie);
            foreach (string part in new[] { "Path=/answer", "HttpOnly", "Secure", "SameSite=Strict" })
                Assert.Contains(part, reply.SetCookie);
            Assert.Equal(410, rig.Send("POST", "/answer/pair", JsonSerializer.Serialize(new { secret = pairing.Secret })).Status);
            // The device was given no name: it is listed as unnamed.
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Answer_Device_Unnamed, rig.Answers.Devices.Single().Name);
            // Its key is kept as its hash only.
            string key = reply.SetCookie.Split(';')[0].Split('=', 2)[1];
            Assert.Equal(WebSecrets.Hash(key), rig.Answers.Save().Single().KeyHash);

            var late = rig.Answers.StartPairing();
            rig.Now += WebAnswer.PairingLife;
            Assert.Equal(410, rig.Send("POST", "/answer/pair", JsonSerializer.Serialize(new { code = late.Code })).Status);
            Assert.Single(rig.Answers.Devices);
        }

        [Fact]
        public void TheRoute_AdmitsThisNetwork_ThisPcsHost_AndThePagesOwnOrigin()
        {
            var rig = new Rig();
            string cookie = rig.Pair();
            Assert.Equal(403, rig.Send("GET", "/answer/state", cookie: cookie, from: IPAddress.Parse("8.8.8.8")).Status);
            Assert.Equal(403, rig.Send("GET", "/answer/state", cookie: cookie, host: "evil.example:8080").Status);
            Assert.Equal(200, rig.Send("GET", "/answer/state", cookie: cookie, from: IPAddress.Loopback, host: "localhost:8080").Status);
            Assert.Equal(200, rig.Send("GET", "/answer/state", cookie: cookie, from: IPAddress.Parse("fe80::1"), host: "[fe80::2]:8080").Status);
            var p = rig.Approvals.AskAsync(Change());
            var q = rig.Approvals.Pending.Single();
            string body = JsonSerializer.Serialize(new { id = q.Id, nonce = q.Nonce, allow = true });
            Assert.Equal(403, rig.Send("POST", "/answer/answer", body, cookie, origin: null).Status);
            Assert.Equal(403, rig.Send("POST", "/answer/answer", body, cookie, origin: "https://evil.example").Status);
            Assert.Equal(403, rig.Send("POST", "/answer/answer", body, cookie, origin: "http://" + Host).Status);
            Assert.Equal(405, rig.Send("GET", "/answer/answer", body, cookie).Status);
            Assert.False(p.IsCompleted);
            Assert.True(rig.Send("GET", "/answer").Page);
        }

        /// <summary>A web sign-in's code is for the card on this PC, and
        /// Show brings this PC's window forward: neither is listed or
        /// answered from a device.</summary>
        [Fact]
        public void ASignInAndAShow_AreNeitherListedNorAnswered()
        {
            var rig = new Rig();
            string cookie = rig.Pair();
            var signIn = rig.Approvals.AskAsync(Change("sign in") with { Kind = ApprovalKind.WebSignIn, Code = "123456" });
            var show = rig.Approvals.AskAsync(Change("show") with { Kind = ApprovalKind.Show });
            Assert.Empty(rig.Questions(cookie));
            foreach (var p in rig.Approvals.Pending)
                Assert.Equal(409, rig.Answer(cookie, p.Id, p.Nonce, true).Status);
            Assert.False(signIn.IsCompleted);
            Assert.False(show.IsCompleted);
            // A program's first connection is listed, as a consent.
            var consent = rig.Approvals.AskAsync(Change("connect") with { Kind = ApprovalKind.Consent });
            Assert.Equal("consent", Assert.Single(rig.Questions(cookie))["kind"].GetValue<string>());
            Assert.False(consent.IsCompleted);
        }

        // ─────────────────────────────────────────────
        //  The row
        // ─────────────────────────────────────────────

        /// <summary>The row shows only while a web assistant is allowed or a
        /// network key exists, and turning its route on takes a paired
        /// device or an open pairing.</summary>
        [Fact]
        public void TheRow_ShowsWhileAWebGrantOrANetworkKeyExists()
        {
            Assert.False(WebAnswer.ShowsRow(0, false));
            Assert.True(WebAnswer.ShowsRow(1, false));
            Assert.True(WebAnswer.ShowsRow(0, true));
            // The network door keeps no keys yet.
            Assert.False(WebAnswer.NetworkKeyExists());

            var store = new WebAccessStore();
            var answers = new WebAnswer { RowShows = () => WebAnswer.ShowsRow(store.Grants.Count, WebAnswer.NetworkKeyExists()) };
            var view = new AnswerAccessViewModel(answers);
            Assert.False(view.ShowsRow);
            Assert.Empty(view.CatalogItems());
            store.Add(new WebGrant { Id = "g1", Origin = "claude.ai", Allowed = DateTimeOffset.Now });
            view.Refresh();
            Assert.True(view.ShowsRow);
            Assert.False(answers.Wanted);
            answers.StartPairing();
            Assert.True(answers.Wanted);
            store.Revoke("g1");
            view.Refresh();
            Assert.False(view.ShowsRow);
            Assert.False(answers.Wanted);
            Assert.Equal(404, answers.Handle(new AnswerRequest("GET", "/answer", Host, null, null, null, Phone)).Status);
        }

        [Fact]
        public void TheRowsDevices_ReadWhenPaired_AndRemoveTakesOne()
        {
            var rig = new Rig();
            var now = new DateTimeOffset(2026, 11, 25, 11, 40, 0, TimeSpan.Zero).ToLocalTime();
            rig.Now = now;
            rig.Pair();
            var view = new AnswerAccessViewModel(rig.Answers, () => now);
            var device = Assert.Single(view.Devices);
            var s = PadForge.Resources.Strings.Strings.Instance;
            string time = now.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
            Assert.Equal(string.Format(s.Answer_Device_Paired_Format, string.Format(s.Answer_When_Today_Format, time)), device.Line);
            // Every entry is the user's alone, so a program reads none of it.
            var items = view.CatalogItems();
            Assert.All(items, i => Assert.Equal(PadForge.Common.ReachClass.UsersOwn, i.Reach));
            device.RemoveCommand.Execute(null);
            Assert.Empty(rig.Answers.Devices);
            Assert.Empty(view.Devices);
        }

        // ─────────────────────────────────────────────
        //  Secrets
        // ─────────────────────────────────────────────

        /// <summary>The pairing's secret and code, both ways it is written,
        /// and the paired devices' key hashes are on the list every read and
        /// every recording is scrubbed by (ProblemRecorderHost.LiveValues),
        /// beside the values that list always held.</summary>
        [Fact]
        public void TheSecret_TheCode_AndTheDeviceKeys_AreScrubbedFromReadsAndRecordings()
        {
            var shared = WebAnswer.Shared;
            var before = shared.Save();
            var pairing = shared.StartPairing();
            try
            {
                var device = new AnswerDeviceData { Id = "d1", Name = "Pixel 8", KeyHash = WebSecrets.Hash("device-key"), Paired = DateTime.UtcNow };
                shared.Load(new[] { device });
                var environment = new PrivacyEnvironment { UserName = "pfwatcher", PcName = "PFBOX", ProfileFolder = @"C:\Users\pfwatcher" };
                var scrubber = AutomationReadScrub.Make(environment);
                string text = "user pfwatcher secret " + pairing.Secret + " code " + pairing.Code + " spaced "
                              + WebSecrets.Spaced(pairing.Code) + " hash " + device.KeyHash + " name Pixel 8";
                string scrubbed = scrubber.Text(text);
                // The positive control, in the same pass.
                Assert.DoesNotContain("pfwatcher", scrubbed);
                Assert.DoesNotContain(pairing.Secret, scrubbed);
                Assert.DoesNotContain(pairing.Code, scrubbed);
                Assert.DoesNotContain(WebSecrets.Spaced(pairing.Code), scrubbed);
                Assert.DoesNotContain(device.KeyHash, scrubbed);
                Assert.DoesNotContain("Pixel 8", scrubbed);
            }
            finally
            {
                shared.CancelPairing();
                shared.Load(before);
            }
        }

        // ─────────────────────────────────────────────
        //  The listener
        // ─────────────────────────────────────────────

        /// <summary>No answer path exists through the web helper: its front
        /// door has no answer route, nothing in the web door's code reaches
        /// the page or a named answer, and AutomationApprovals is answered
        /// from the card's view model and the page alone.</summary>
        [Fact]
        public async Task NoAnswerPathExists_ThroughTheWebHelper()
        {
            var app = new FakeWebApp();
            var front = new WebFront(41234, new WebAuth(app, (u, c) => Task.FromResult<string>(null)),
                new WebMcp(_ => throw new InvalidOperationException("no request reaches PadForge here")), app, () => null);
            foreach (var (method, path) in new[] { ("GET", "/answer"), ("GET", "/answer/state"), ("POST", "/answer/answer"), ("POST", "/answer/pair") })
            {
                var request = new PadForge.Services.Automation.Web.WebRequest(method, path, null, PadForge.Services.Automation.Web.WebRequest.HeadersOf(("Host", "127.0.0.1:41234")), "{}");
                Assert.Equal(404, (await front.HandleAsync(request, default)).Status);
            }

            string app_ = Path.Combine(OffLimitsSource.RepoRoot(), "PadForge.App");
            var callers = Directory.EnumerateFiles(app_, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                            && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"Approvals\.Answer\(|\.AnswerNamed\("))
                .Select(f => Path.GetRelativePath(app_, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "Services/WebAnswer.cs", "ViewModels/DashboardViewModel.AutomationPage.cs" }, callers);
            foreach (string f in Directory.EnumerateFiles(Path.Combine(app_, "Services", "Automation", "Web"), "*.cs"))
            {
                string text = File.ReadAllText(f);
                Assert.DoesNotContain("WebAnswer", text);
                Assert.DoesNotContain("AnswerNamed", text);
            }
        }

        /// <summary>On a real http.sys listener on loopback: the main address
        /// that serves HTTPS serves the page and its state, a run for the
        /// page alone serves nothing of the controller, and a main address
        /// that fell back to plain HTTP serves no page.</summary>
        [Fact]
        public async Task TheListener_ServesThePage_OnTheHttpsMainAddressAlone()
        {
            var rig = new Rig();
            string cookie = rig.Pair();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            using (var run = Run(rig.Answers, https: true, controls: false))
            {
                string main = "http://127.0.0.1:" + run.Port;
                using (var page = await http.GetAsync(main + "/answer"))
                {
                    Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                    Assert.Equal(ExtrasPageTests.RepoText("PadForge.App", "WebAssets", "answer.html"), await page.Content.ReadAsStringAsync());
                    Assert.Equal("no-store", page.Headers.CacheControl.ToString());
                    Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
                }
                using (var request = new HttpRequestMessage(HttpMethod.Get, main + "/answer/state"))
                {
                    request.Headers.Add("Cookie", cookie);
                    using var state = await http.SendAsync(request);
                    Assert.Equal(HttpStatusCode.OK, state.StatusCode);
                }
                using (var sheet = await http.GetAsync(main + "/css/padforge.css"))
                    Assert.Equal(HttpStatusCode.OK, sheet.StatusCode);
                foreach (string path in new[] { "/", "/index.html", "/controller.html", "/api/info", "/display.html" })
                    using (var refused = await http.GetAsync(main + path))
                        Assert.True(refused.StatusCode == HttpStatusCode.NotFound, path + " answered " + (int)refused.StatusCode);
                Assert.False(run.Server.ServesControls);
            }

            using (var run = Run(rig.Answers, https: false, controls: true))
            {
                string main = "http://127.0.0.1:" + run.Port;
                using (var page = await http.GetAsync(main + "/answer"))
                    Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
                using (var home = await http.GetAsync(main + "/"))
                    Assert.Equal(HttpStatusCode.OK, home.StatusCode);
            }
        }

        private sealed class LoopbackRun : IDisposable
        {
            public WebControllerServer Server;
            public int Port;
            public void Dispose() => Server.Dispose();
        }

        /// <summary>A server on a free loopback port. With
        /// <paramref name="https"/> the endpoint preparation hands back a
        /// lease, as a bound certificate does, and the listener takes the
        /// https prefix on 127.0.0.1 over plain HTTP.</summary>
        private static LoopbackRun Run(WebAnswer answers, bool https, bool controls)
        {
            var rng = new Random();
            for (int attempt = 0; attempt < 10; attempt++)
            {
                int port = 20000 + rng.Next(20000);
                var server = new WebControllerServer(_ => https ? new Lease() : null, () => new LoopbackListener()) { Answers = answers };
                if (server.Start(port, null, controls))
                {
                    Assert.Equal(https, server.IsHttps);
                    return new LoopbackRun { Server = server, Port = port };
                }
                server.Dispose();
            }
            throw new InvalidOperationException("no free loopback port");
        }

        private sealed class Lease : IDisposable
        {
            public void Dispose() { }
        }

        /// <summary>The production listener's behavior on 127.0.0.1 in place
        /// of the strong wildcard, https as http.</summary>
        private sealed class LoopbackListener : IWebControllerListener
        {
            private readonly HttpListener _listener = new();
            private static string Loopback(string prefix) => prefix.Replace("https://+:", "http://127.0.0.1:").Replace("http://+:", "http://127.0.0.1:");
            public bool IsListening => _listener.IsListening;
            public void Start(string prefix) { _listener.Prefixes.Add(Loopback(prefix)); _listener.Start(); }
            public void AddPrefix(string prefix) => _listener.Prefixes.Add(Loopback(prefix));
            public HttpListenerContext GetContext() => _listener.GetContext();
            public void Stop() => _listener.Stop();
            public void Close() => _listener.Close();
        }

        /// <summary>With the web controller off, the page wanted starts the
        /// listener for the page alone, which leaves the web controller's
        /// card off. The web controller's switch takes the listener over, and
        /// the page no longer wanted stops it.</summary>
        [Fact]
        public void ThePageWanted_StartsTheListenerForItAlone_WhileTheWebControllerIsOff()
        {
            RunSta(dispatcher =>
            {
                var vm = new MainViewModel();
                using var service = new InputService(vm);
                var answers = new WebAnswer { RowShows = () => true };
                service.Answers = answers;
                var made = new List<WebControllerServer>();
                service.WebServerFactory = () =>
                {
                    var server = new WebControllerServer(_ => null, () => new IdleListener());
                    made.Add(server);
                    return server;
                };
                Set(service, "_inputManager", RuntimeHelpers.GetUninitializedObject(typeof(InputManager)));
                try
                {
                    vm.Dashboard.WebControllerPort = 18092;
                    service.ApplyAnswerRoute();
                    Assert.Empty(made);

                    answers.StartPairing();
                    service.ApplyAnswerRoute();
                    PumpUntil(dispatcher, () => Get<WebControllerServer>(service, "_webServer")?.IsRunning == true);
                    var alone = Get<WebControllerServer>(service, "_webServer");
                    Assert.False(alone.ServesControls);
                    Assert.Same(answers, alone.Answers);
                    Pump(dispatcher);
                    Assert.False(vm.Dashboard.IsWebControllerRunning);

                    vm.Dashboard.EnableWebController = true;
                    Call(service, "OnDashboardPropertyChanged", vm.Dashboard, new PropertyChangedEventArgs(nameof(DashboardViewModel.EnableWebController)));
                    PumpUntil(dispatcher, () => Get<WebControllerServer>(service, "_webServer")?.IsRunning == true
                                                && Get<WebControllerServer>(service, "_webServer").ServesControls);
                    Assert.False(alone.IsRunning);
                    PumpUntil(dispatcher, () => vm.Dashboard.IsWebControllerRunning);

                    vm.Dashboard.EnableWebController = false;
                    Call(service, "OnDashboardPropertyChanged", vm.Dashboard, new PropertyChangedEventArgs(nameof(DashboardViewModel.EnableWebController)));
                    PumpUntil(dispatcher, () => Get<WebControllerServer>(service, "_webServer")?.IsRunning == true
                                                && !Get<WebControllerServer>(service, "_webServer").ServesControls);

                    answers.CancelPairing();
                    service.ApplyAnswerRoute();
                    Assert.Null(Get<WebControllerServer>(service, "_webServer"));
                    Assert.Equal(3, made.Count);
                }
                finally
                {
                    Call(service, "StopWebServer");
                    Set(service, "_inputManager", null);
                }
            });
        }

        /// <summary>A web assistant can ask while the engine is stopped, so
        /// the page does not need it: with the engine stopped the listener
        /// runs for the page alone, even with the web controller's switch on,
        /// the engine's start hands the port to the web controller's run, and
        /// its stop hands it back. A disposed service starts nothing.</summary>
        [Fact]
        public void ThePage_IsServedWithTheEngineStopped_AndTheEnginesStartAndStopTakeTheListenerOver()
        {
            RunSta(dispatcher =>
            {
                var vm = new MainViewModel();
                using var service = new InputService(vm);
                var answers = new WebAnswer { RowShows = () => true };
                service.Answers = answers;
                service.WebServerFactory = () => new WebControllerServer(_ => null, () => new IdleListener());
                WebControllerServer Server() => Get<WebControllerServer>(service, "_webServer");
                try
                {
                    vm.Dashboard.WebControllerPort = 18093;
                    vm.Dashboard.EnableWebController = true;
                    answers.StartPairing();
                    // The engine is stopped: the page alone.
                    service.ApplyAnswerRoute();
                    PumpUntil(dispatcher, () => Server()?.IsRunning == true);
                    Assert.False(Server().ServesControls);

                    // The engine starts: the web controller's run takes over.
                    Set(service, "_inputManager", RuntimeHelpers.GetUninitializedObject(typeof(InputManager)));
                    Call(service, "StartWebServerIfEnabled");
                    PumpUntil(dispatcher, () => Server()?.IsRunning == true && Server().ServesControls);

                    // The engine stops: the page alone again.
                    Call(service, "StopWebServer");
                    Set(service, "_inputManager", null);
                    service.ApplyAnswerRoute();
                    PumpUntil(dispatcher, () => Server()?.IsRunning == true && !Server().ServesControls);

                    // Disposed: nothing starts.
                    Call(service, "StopWebServer");
                    typeof(InputService).GetField("_disposed", Fields).SetValue(service, 1);
                    service.ApplyAnswerRoute();
                    Assert.Null(Server());
                }
                finally
                {
                    Call(service, "StopWebServer");
                    Set(service, "_inputManager", null);
                }
            });
        }

        // ─────────────────────────────────────────────
        //  The pairing window
        // ─────────────────────────────────────────────

        /// <summary>The pairing window asks Windows to leave it out of screen
        /// capture before it opens a pairing, and closing it closes the
        /// pairing. Its handle is made and never shown.</summary>
        [Fact]
        public void ThePairingWindow_IsHiddenFromCapture_BeforeItOpensAPairing()
        {
            DialogTestHost.Run(() =>
            {
                var answers = new WebAnswer { RowShows = () => true };
                var dialog = new AnswerPairDialog(answers);
                try
                {
                    Assert.Null(answers.Pairing);
                    IntPtr hwnd = new WindowInteropHelper(dialog).EnsureHandle();
                    Assert.True(AnswerPairDialog.GetWindowDisplayAffinity(hwnd, out uint affinity));
                    Assert.Equal(AnswerPairDialog.WDA_EXCLUDEFROMCAPTURE, affinity);
                    Assert.True(dialog.HiddenFromCapture);
                    Assert.NotNull(answers.Pairing);
                    Assert.Same(answers.Pairing, dialog.Pairing);
                }
                finally
                {
                    dialog.Close();
                }
                Assert.Null(answers.Pairing);
            });
        }

        // ── Helpers ──

        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        private static void Set(InputService service, string field, object value)
            => (typeof(InputService).GetField(field, Fields) ?? throw new MissingFieldException(field)).SetValue(service, value);

        private static T Get<T>(InputService service, string field)
            => (T)(typeof(InputService).GetField(field, Fields) ?? throw new MissingFieldException(field)).GetValue(service);

        private static void Call(InputService service, string name, params object[] args)
            => (typeof(InputService).GetMethod(name, Fields) ?? throw new MissingMethodException(name)).Invoke(service, args.Length == 0 ? null : args);

        private static void Pump(Dispatcher dispatcher)
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        private static void PumpUntil(Dispatcher dispatcher, Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!done())
            {
                Assert.True(DateTime.UtcNow < until, "timed out");
                Pump(dispatcher);
                Thread.Sleep(5);
            }
        }

        private static void RunSta(Action<Dispatcher> body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                try { body(dispatcher); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The dispatcher test did not finish.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>A listener that takes every prefix and waits for its
        /// stop.</summary>
        private sealed class IdleListener : IWebControllerListener
        {
            private readonly ManualResetEventSlim _stopped = new();
            public bool IsListening { get; private set; }
            public void Start(string prefix) => IsListening = true;
            public void AddPrefix(string prefix) { }
            public HttpListenerContext GetContext()
            {
                _stopped.Wait(TimeSpan.FromSeconds(10));
                throw new HttpListenerException();
            }
            public void Stop() { IsListening = false; _stopped.Set(); }
            public void Close() => Stop();
        }
    }
}
