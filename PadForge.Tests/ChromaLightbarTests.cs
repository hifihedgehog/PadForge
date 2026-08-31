using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Razer Chroma lightbar mirror (#373, asked in discussion #368). No
    /// Razer hardware exists on the bench, so these tests pin the entire
    /// protocol surface against a fake in-process Chroma REST server: the
    /// init body field for field, the heartbeat cadence, the exact
    /// CHROMA_STATIC JSON with the BGR integer, change-only pushing, the
    /// session DELETE on stop, and the no-Synapse retry path. The protocol
    /// itself is triangulated from the official Razer REST docs and
    /// chroma-sdk/Colore, which agree on every field.
    ///
    /// <para>The fake binds a unique localhost port and the service takes it
    /// as its endpoint: the production port 54235 is machine-global, and a
    /// test that talked to it would reach a real Synapse (the same lesson as
    /// the external-control pipe's name seam).</para>
    /// </summary>
    [Collection("ChromaPublishedColor")]
    public class ChromaLightbarTests : IDisposable
    {
        private sealed class FakeChromaServer : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly Thread _thread;
            private volatile bool _running = true;

            public readonly ConcurrentQueue<(string Method, string Path, string Body)> Requests = new();
            public string Endpoint { get; }
            public string SessionPath => "/chromasdk";
            public bool RefuseInit { get; set; }

            public FakeChromaServer()
            {
                // Bind a free port by probing; HttpListener cannot bind port 0.
                var rng = new Random();
                for (int attempt = 0; ; attempt++)
                {
                    int port = 20000 + rng.Next(20000);
                    var l = new HttpListener();
                    l.Prefixes.Add($"http://127.0.0.1:{port}/");
                    try { l.Start(); }
                    catch (HttpListenerException) when (attempt < 10) { continue; }
                    _listener = l;
                    Endpoint = $"http://127.0.0.1:{port}";
                    break;
                }
                _thread = new Thread(Serve) { IsBackground = true };
                _thread.Start();
            }

            private void Serve()
            {
                while (_running)
                {
                    HttpListenerContext ctx;
                    try { ctx = _listener.GetContext(); }
                    catch { break; }

                    string body;
                    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                        body = reader.ReadToEnd();
                    Requests.Enqueue((ctx.Request.HttpMethod, ctx.Request.Url.AbsolutePath, body));

                    string response;
                    if (ctx.Request.Url.AbsolutePath == "/razer/chromasdk"
                        && ctx.Request.HttpMethod == "POST")
                    {
                        if (RefuseInit)
                        {
                            response = "{\"result\":1168}";
                        }
                        else
                        {
                            // The official init page's success shape verbatim.
                            response = "{\"sessionid\":777,\"uri\":\""
                                + Endpoint + SessionPath + "\"}";
                        }
                    }
                    else
                    {
                        response = "{\"result\":0}";
                    }

                    byte[] bytes = Encoding.UTF8.GetBytes(response);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    try
                    {
                        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        ctx.Response.OutputStream.Close();
                    }
                    catch { /* client gone */ }
                }
            }

            public bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
            {
                long start = Environment.TickCount64;
                while (Environment.TickCount64 - start < timeoutMs)
                {
                    if (condition()) return true;
                    Thread.Sleep(25);
                }
                return condition();
            }

            public void Dispose()
            {
                _running = false;
                try { _listener.Stop(); } catch { }
                try { _listener.Close(); } catch { }
            }
        }

        public ChromaLightbarTests()
        {
            ChromaLightbarService.ResetPublishedForTest();
        }

        public void Dispose()
        {
            ChromaLightbarService.ResetPublishedForTest();
        }

        /// <summary>BGR packing, the official docs' "Color value in BGR
        /// format" and Colore's Color constructor (R + G&lt;&lt;8 + B&lt;&lt;16):
        /// pure red is 255, pure blue is 16711680.</summary>
        [Theory]
        [InlineData(0xFF0000, 255)]        // red
        [InlineData(0x00FF00, 65280)]      // green
        [InlineData(0x0000FF, 16711680)]   // blue
        [InlineData(0xFFFFFF, 16777215)]
        [InlineData(0x000000, 0)]
        [InlineData(0x123456, 0x563412)]
        public void ToBgr_MatchesTheChromaWireFormat(int rgb, int bgr)
        {
            Assert.Equal(bgr, ChromaLightbarService.ToBgr(rgb));
        }

        /// <summary>The full session: init carries the documented app-info
        /// fields, a published color arrives as CHROMA_STATIC with the BGR
        /// integer on all six category endpoints, a repeat publish of the
        /// same color sends nothing new, heartbeats ride the session, and
        /// stopping DELETEs it.</summary>
        [Fact]
        public void FullSession_InitPushHeartbeatAndTeardown()
        {
            using var server = new FakeChromaServer();
            using var svc = new ChromaLightbarService(
                server.Endpoint, heartbeatMs: 200, retryMs: 500, pollMs: 25);
            svc.Start();

            // Init arrived with the documented body.
            Assert.True(server.WaitFor(() =>
                server.Requests.Any(r => r.Path == "/razer/chromasdk" && r.Method == "POST")));
            var init = server.Requests.First(r => r.Path == "/razer/chromasdk");
            using (var doc = JsonDocument.Parse(init.Body))
            {
                var root = doc.RootElement;
                Assert.Equal("PadForge", root.GetProperty("title").GetString());
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("description").GetString()));
                Assert.Equal("hifihedgehog", root.GetProperty("author").GetProperty("name").GetString());
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("author").GetProperty("contact").GetString()));
                var devices = root.GetProperty("device_supported").EnumerateArray()
                    .Select(e => e.GetString()).ToArray();
                Assert.Equal(
                    new[] { "keyboard", "mouse", "headset", "mousepad", "keypad", "chromalink" },
                    devices);
                Assert.Equal("application", root.GetProperty("category").GetString());
            }

            // Publish red: six CHROMA_STATIC PUTs with BGR 255.
            ChromaLightbarService.Publish(255, 0, 0);
            Assert.True(server.WaitFor(() =>
                server.Requests.Count(r => r.Path.StartsWith("/chromasdk/")
                    && r.Body.Contains("CHROMA_STATIC")) >= 6));
            var pushes = server.Requests
                .Where(r => r.Body.Contains("CHROMA_STATIC")).ToArray();
            var paths = pushes.Select(p => p.Path).OrderBy(p => p).ToArray();
            Assert.Equal(
                new[]
                {
                    "/chromasdk/chromalink", "/chromasdk/headset", "/chromasdk/keyboard",
                    "/chromasdk/keypad", "/chromasdk/mouse", "/chromasdk/mousepad",
                },
                paths);
            foreach (var push in pushes)
            {
                Assert.Equal("PUT", push.Method);
                Assert.Equal("{\"effect\":\"CHROMA_STATIC\",\"param\":{\"color\":255}}", push.Body);
            }

            // The same color again pushes nothing new; a different one does.
            int staticCount = server.Requests.Count(r => r.Body.Contains("CHROMA_STATIC"));
            ChromaLightbarService.Publish(255, 0, 0);
            Thread.Sleep(300);
            Assert.Equal(staticCount, server.Requests.Count(r => r.Body.Contains("CHROMA_STATIC")));
            ChromaLightbarService.Publish(0, 0, 255);
            Assert.True(server.WaitFor(() =>
                server.Requests.Any(r => r.Body.Contains("\"color\":16711680"))));

            // Heartbeats ride the session at the configured cadence.
            Assert.True(server.WaitFor(() =>
                server.Requests.Count(r => r.Path == "/chromasdk/heartbeat" && r.Method == "PUT") >= 2));

            // Stop DELETEs the session.
            svc.Stop();
            Assert.True(server.WaitFor(() =>
                server.Requests.Any(r => r.Method == "DELETE" && r.Path == "/chromasdk")));
        }

        /// <summary>With nothing listening at the endpoint, Start never
        /// throws, the state reports WaitingForSynapse, and the loop keeps
        /// retrying until a server appears.</summary>
        [Fact]
        public void NoSynapse_ReportsWaitingAndRetries()
        {
            // A port with no listener: bind-then-close to find a free one.
            using var probe = new FakeChromaServer();
            string deadEndpoint = probe.Endpoint;
            probe.Dispose();

            var states = new ConcurrentQueue<ChromaServiceState>();
            using var svc = new ChromaLightbarService(
                deadEndpoint, heartbeatMs: 200, retryMs: 100, pollMs: 25);
            svc.StateChanged += s => states.Enqueue(s);
            svc.Start();

            long start = Environment.TickCount64;
            while (Environment.TickCount64 - start < 5000
                && states.Count(s => s == ChromaServiceState.WaitingForSynapse) < 2)
                Thread.Sleep(25);
            Assert.True(states.Count(s => s == ChromaServiceState.WaitingForSynapse) >= 2);
            Assert.DoesNotContain(ChromaServiceState.Connected, states);
        }

        /// <summary>An init the server refuses (Synapse's documented failure
        /// shape, a result with no uri) is a retry, not a crash.</summary>
        [Fact]
        public void RefusedInit_IsRetriedNotFatal()
        {
            using var server = new FakeChromaServer { RefuseInit = true };
            var states = new ConcurrentQueue<ChromaServiceState>();
            using var svc = new ChromaLightbarService(
                server.Endpoint, heartbeatMs: 200, retryMs: 100, pollMs: 25);
            svc.StateChanged += s => states.Enqueue(s);
            svc.Start();

            Assert.True(server.WaitFor(() =>
                server.Requests.Count(r => r.Path == "/razer/chromasdk") >= 2));
            Assert.DoesNotContain(ChromaServiceState.Connected, states);
        }

        /// <summary>The feed's source contract: the OutputDecoded handler
        /// publishes the decoded lightbar field under each family's validity
        /// bit (DualSense validFlag1 bit 2, DualShock 4 validFlag0 bit 1 per
        /// SDL k_EPS4EffectLED), and the Dashboard sibling set carries the
        /// setting through every persistence leg the web-controller toggle
        /// has.</summary>
        [Fact]
        public void FeedAndSiblingContracts()
        {
            string vc = RepoText("PadForge.App", "Common", "Input", "HMaestroVirtualController.cs");
            int at = vc.IndexOf("Chroma lightbar mirror (#373)", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = vc.Substring(at, 1600);
            Assert.Contains("TryGetValue(\"lightbar\"", body);
            Assert.Contains("(lbVf1 & 0x04) != 0", body);
            Assert.Contains("(lbVf0 & 0x02) != 0", body);
            Assert.Contains("ChromaLightbarService.Publish(lbRgb[0], lbRgb[1], lbRgb[2])", body);

            string ss = RepoText("PadForge.App", "Services", "SettingsService.cs");
            int webLegs = ss.Split(new[] { "EnableWebController" }, StringSplitOptions.None).Length - 1;
            int chromaLegs = ss.Split(new[] { "EnableChromaLightbar" }, StringSplitOptions.None).Length - 1;
            Assert.Equal(webLegs, chromaLegs);

            string page = RepoText("PadForge.App", "Views", "DashboardPage.xaml");
            Assert.Contains("Binding EnableChromaLightbar", page);
            Assert.Contains("Binding ChromaStatus", page);
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
