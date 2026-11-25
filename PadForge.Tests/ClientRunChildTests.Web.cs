using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using PadForge.Services.Plugins;
using Xunit;

namespace PadForge.Tests
{
    public partial class ClientRunChildTests
    {
        // ─────────────────────────────────────────────
        //  --web
        // ─────────────────────────────────────────────

        /// <summary>The web door's launcher, starting the app's build itself
        /// with the door's variables: the one-time key and the test's own
        /// pipe, the form every child of these tests has. It starts one
        /// child: a later start, as the door's restart after the child
        /// ended, is refused and counted.</summary>
        private sealed class ChildLauncher : IPluginLauncher
        {
            private readonly string _exe;
            private readonly WindowWatch _watch;
            public Child Started;
            public ChildProcess Process;
            public int Refused;

            /// <summary>Whether the door's stop ends the child. A test that
            /// shows the child ends by itself turns it off.</summary>
            public bool StopEndsChild = true;

            public ChildLauncher(string exe, WindowWatch watch = null)
            {
                _exe = exe;
                _watch = watch;
            }

            public PluginLaunch Start(PluginCommand command, IReadOnlyDictionary<string, string> variables)
            {
                if (Started != null)
                {
                    Interlocked.Increment(ref Refused);
                    return PluginLaunch.Refused(PluginRefusal.Failed);
                }
                Assert.EndsWith(" --web", command.CommandLine);
                Started = new Child(_exe, "--web", variables, _watch);
                Process = new ChildProcess(Started, StopEndsChild);
                return new PluginLaunch { Process = Process };
            }
        }

        /// <summary>The door's view of the child. A stop closes the child's
        /// job, as the plugin launcher's stop closes its own
        /// (PluginLauncher.PluginProcess.Stop), unless the test said
        /// otherwise.</summary>
        private sealed class ChildProcess : IPluginProcess
        {
            private readonly Child _child;
            private readonly bool _stopEnds;
            public int Stops;
            public ChildProcess(Child child, bool stopEnds)
            {
                _child = child;
                _stopEnds = stopEnds;
                _child.Exited += code => Exited?.Invoke(code);
            }
            public int Id => _child.Id;
            public bool Owns(int processId) => processId == Id;
            public event Action<int> Exited;
            public void Stop()
            {
                Interlocked.Increment(ref Stops);
                if (_stopEnds) _child.EndJob();
            }
            public void Dispose() { }
        }

        [UnelevatedFact]
        public async Task TheWebHelper_StartedByTheDoor_ServesHttpOnLoopback_AndEndsWhenTheDoorStopsIt()
        {
            TheListingsSeeWhatTheyLookFor();
            string exe = BuiltExe();
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings);
            var store = new WebAccessStore();
            using var watch = new WindowWatch();
            var launcher = new ChildLauncher(exe, watch);
            using var door = new WebDoor(rig.Host, store, launcher, () => exe, rig.PipeName);
            try
            {
                door.TurnOn();
                Assert.NotNull(launcher.Started);
                // The door took the child by its key and its process, and the
                // child listens on the port it reported.
                await AssistantRig.Until(() => door.HelperState == WebHelperState.Running && store.Port > 0, 30000);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                string root = WebAddress.Loopback(store.Port);
                var metadata = await http.GetAsync(root + "/.well-known/oauth-protected-resource/mcp");
                Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
                var resource = JsonNode.Parse(await metadata.Content.ReadAsStringAsync());
                Assert.NotNull(resource?["authorization_servers"]);
                // A call with no token is refused, and says where to sign in.
                var anonymous = await http.PostAsync(root + "/mcp", new StringContent(
                    Request(1, "initialize", new JsonObject { ["protocolVersion"] = AssistantMirror.UpstreamVersion, ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "x", ["version"] = "1" } }).ToJsonString(),
                    Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
                Assert.Contains(anonymous.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
                // A sign-in page, with the door not armed: refused, in the
                // words PadForge sent the helper with its state.
                var refused = await http.GetAsync(root + "/authorize?response_type=code&client_id=https%3A%2F%2Fexample.com%2Fclient.json"
                    + "&redirect_uri=https%3A%2F%2Fexample.com%2Fback&code_challenge=abc&code_challenge_method=S256");
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                Assert.Contains(WebUtility.HtmlEncode(PadForge.Resources.Strings.Strings.Get("Web_SignIn_NotAccepting")), await refused.Content.ReadAsStringAsync());

                // After real work: no part of WPF.
                var mapped = MappedFiles(launcher.Started.Id);
                Assert.Contains("System.Private.CoreLib.dll", mapped);
                Assert.Contains("System.Net.HttpListener.dll", mapped);
                NoWpf(mapped, "after it answered", launcher.Started);
                Assert.Equal(Rights(Environment.ProcessId), Rights(launcher.Started.Id));

                // Turning the door off ends the helper, through its job, and
                // nothing listens.
                door.TurnOff();
                Assert.True(launcher.Started.Ends(20000), "the helper did not end when the door stopped it. Standard error: " + launcher.Started.StandardError);
                Assert.Equal(1, launcher.Process.Stops);
                Assert.Equal(WebHelperState.Off, door.HelperState);
                await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync(root + "/.well-known/oauth-protected-resource/mcp"));
                // Its protocol is HTTP: standard output held nothing.
                Assert.Empty(launcher.Started.StandardOutput);
                watch.Drain();
                Assert.True(watch.Shown(launcher.Started.Id).Count == 0, "the helper showed a window: " + Events(watch, launcher.Started.Id));
                Assert.Equal(0, launcher.Refused);
            }
            finally
            {
                launcher.Started?.Dispose();
            }
        }

        /// <summary>
        /// A web assistant's whole way in, through the <c>--web</c> child the
        /// door started: the SDK's own HTTP client signs in against the
        /// child's own port on 127.0.0.1 with its own OAuth flow, by
        /// registration, as WebEndToEndTests signs in against a helper in the
        /// test's process (WebEndToEndTests.cs:120-140), and the user allows
        /// it on the question card once its code matches the sign-in page's.
        /// Signed in, it lists the tools and reads a place. A call with no
        /// token, and one with a token PadForge never gave, are refused with
        /// the Bearer challenge. The child loads no part of WPF and shows no
        /// window.
        /// </summary>
        [UnelevatedFact]
        public async Task TheWebHelper_SignsInTheSdksClient_OnItsOwnPort_ReadsSignedIn_AndRefusesAnUnsignedCall()
        {
            string exe = BuiltExe();
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings);
            var store = new WebAccessStore();
            using var watch = new WindowWatch();
            var launcher = new ChildLauncher(exe, watch);
            using var door = new WebDoor(rig.Host, store, launcher, () => exe, rig.PipeName);
            var codes = new List<string>();
            // The user, on the question card: Allow only when the card's code
            // is the one the sign-in page showed (WebEndToEndTests.cs:59-69).
            rig.Host.Approvals.PendingChanged += (_, _) => AllowTheSignInWhoseCodeWasShown(rig, codes);
            try
            {
                door.TurnOn();
                await AssistantRig.Until(() => door.HelperState == WebHelperState.Running && store.Port > 0, 30000);
                string endpoint = WebAddress.Loopback(store.Port) + "/mcp";
                door.Arm();
                await AssistantRig.Until(() => door.HelperArmed, 30000);

                var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(endpoint),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    OAuth = new ClientOAuthOptions
                    {
                        RedirectUri = new Uri("https://claude.ai/api/mcp/auth_callback"),
                        AuthorizationCallbackHandler = (context, ct) => SignInPageAsync(context, rig, codes, ct),
                        DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "Claude" },
                    },
                }), new McpClientOptions { ClientInfo = new Implementation { Name = "web-child-test", Version = "1" } });
                try
                {
                    await AssistantRig.Until(() => store.Grants.SingleOrDefault()?.AccessHash != null, 30000);
                    var grant = store.Grants.Single();
                    Assert.Equal("claude.ai", grant.Origin);
                    Assert.Equal(endpoint, grant.Resource);
                    Assert.False(door.Armed);

                    var tools = await client.ListToolsAsync();
                    Assert.Contains(tools, t => t.Name == "status");
                    Assert.Contains(tools, t => t.Name == "find");
                    Assert.Contains(tools, t => t.Name == "read");
                    Assert.Equal(8, Value(await client.CallToolAsync("read", AssistantRig.Args(("id", "pad/Pad_Deadzone@c1")))));

                    // PadForge lists it as a web assistant, at the user's level.
                    await AssistantRig.Until(() => rig.Host.Clients.Snapshot().Any(c => c.Kind == AutomationClientKind.Web), 30000);
                    var session = rig.Host.Sessions.Snapshot().Single(s => s.Kind == AutomationClientKind.Web);
                    Assert.Equal(AutomationLevel.ChangeSettings, rig.Host.Sessions.LevelOf(session));
                }
                finally
                {
                    await client.DisposeAsync();
                }

                // A call with no token is refused and says where to sign in,
                // and so is a token PadForge never gave.
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                var unsigned = await http.PostAsync(endpoint, new StringContent(Request(1, "tools/list").ToJsonString(), Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
                Assert.Contains(unsigned.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
                var forged = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(Request(2, "tools/list").ToJsonString(), Encoding.UTF8, "application/json") };
                forged.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "pf-made-up-" + Guid.NewGuid().ToString("N"));
                Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(forged)).StatusCode);

                // After real work: no part of WPF, beside the runtime, the
                // listener and the protocol's own library.
                watch.Drain();
                var mapped = MappedFiles(launcher.Started.Id);
                Assert.Contains("System.Private.CoreLib.dll", mapped);
                Assert.Contains("System.Net.HttpListener.dll", mapped);
                Assert.Contains("ModelContextProtocol.Core.dll", mapped);
                NoWpf(mapped, "after a signed-in call", launcher.Started);

                door.TurnOff();
                Assert.True(launcher.Started.Ends(20000), "the helper did not end when the door stopped it. Standard error: " + launcher.Started.StandardError);
                Assert.Empty(launcher.Started.StandardOutput);
                watch.Drain();
                Assert.True(watch.Shown(launcher.Started.Id).Count == 0, "the helper showed a window: " + Events(watch, launcher.Started.Id));
            }
            finally
            {
                launcher.Started?.Dispose();
            }
        }

        /// <summary>Allows each sign-in card whose code is the last one a
        /// sign-in page showed.</summary>
        private static void AllowTheSignInWhoseCodeWasShown(AssistantRig rig, List<string> codes)
        {
            string shown;
            lock (codes) shown = codes.Count > 0 ? codes[^1] : null;
            if (shown == null) return;
            foreach (var pending in rig.Host.Approvals.Pending.Where(p => p.Request.Kind == ApprovalKind.WebSignIn && p.Request.Code == shown))
                rig.Host.Approvals.Answer(pending.Id, true);
        }

        /// <summary>The browser's part of a sign-in, done by the test as
        /// WebEndToEndTests.BrowserAsync does it (WebEndToEndTests.cs:88-118):
        /// the sign-in page the child serves, its code read off the page,
        /// then the next page until the redirect back.</summary>
        private static async Task<AuthorizationResult> SignInPageAsync(AuthorizationCallbackContext context, AssistantRig rig, List<string> codes, CancellationToken ct)
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            var page = await http.GetAsync(context.AuthorizationUri, ct);
            string html = await page.Content.ReadAsStringAsync(ct);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var code = Regex.Match(html, "<p class=\"code\">([0-9]{3}) ([0-9]{3})</p>");
            Assert.True(code.Success, "the sign-in page shows six digits");
            lock (codes) codes.Add(code.Groups[1].Value + code.Groups[2].Value);
            // The card may already show: answer it now that the code is known.
            AllowTheSignInWhoseCodeWasShown(rig, codes);
            string next = WebUtility.HtmlDecode(Regex.Match(html, "url=([^\"]+)\"").Groups[1].Value);
            for (int i = 0; i < 300; i++)
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

        /// <summary>PadForge shuts its Automation host down, as it does
        /// after the door when it closes (MainWindow's shutdown), here with
        /// the door left as it was. The host closes its side of the helper's
        /// pipe, and the helper ends by itself: the door never stopped it,
        /// and the test still holds its job.</summary>
        [UnelevatedFact]
        public Task TheWebHelper_EndsByItself_WhenPadForgeClosesItsSideOfThePipe()
            => EndsWhenPadForgeClosesItsSide(rig => rig.Host.Dispose(), doorStops: 0);

        /// <summary>Automation goes off: the host closes the helper's pipe
        /// with the programs' sessions. The door's stop, which ends the
        /// helper through its job, does nothing here, and the helper still
        /// ends by itself.</summary>
        [UnelevatedFact]
        public Task TheWebHelper_EndsByItself_WhenAutomationGoesOff()
            => EndsWhenPadForgeClosesItsSide(rig => rig.Host.Apply(false, AutomationLevel.ChangeSettings), doorStops: 1);

        private static async Task EndsWhenPadForgeClosesItsSide(Action<AssistantRig> close, int doorStops)
        {
            string exe = BuiltExe();
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings);
            var store = new WebAccessStore();
            var launcher = new ChildLauncher(exe) { StopEndsChild = false };
            using var door = new WebDoor(rig.Host, store, launcher, () => exe, rig.PipeName);
            try
            {
                door.TurnOn();
                await AssistantRig.Until(() => door.HelperState == WebHelperState.Running && store.Port > 0, 30000);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                string metadata = WebAddress.Loopback(store.Port) + "/.well-known/oauth-protected-resource/mcp";
                Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(metadata)).StatusCode);

                close(rig);
                Assert.True(launcher.Started.Ends(15000), "the helper outlived PadForge's side of its pipe. Standard error: " + launcher.Started.StandardError);
                Assert.Equal(doorStops, launcher.Process.Stops);
                Assert.True(launcher.Started.JobOpen);
                Assert.Equal(WebHelper.Done, launcher.Started.ExitCode);
                await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync(metadata));
            }
            finally
            {
                launcher.Started?.Dispose();
            }
        }

        /// <summary>PadForge's process ends with no word to its helper, as a
        /// crash ends it. The tests' probe stands in for PadForge, serving the
        /// helper's pipe under the test's own name, and the test ends it the
        /// way a crash does. The helper ends by itself: the test holds its
        /// job and never stops it.</summary>
        [UnelevatedFact]
        public Task TheWebHelper_EndsByItself_WhenPadForgesProcessEnds() => EndsWithItsServer(pipeStaysOpen: false);

        /// <summary>The same, with PadForge's end of the pipe held open by
        /// another process (here the test host, which takes a copy of the
        /// probe's handle before the probe ends). No read of the helper's
        /// ends, so only its watch on the process that served its pipe can
        /// end it.</summary>
        [UnelevatedFact]
        public Task TheWebHelper_EndsByItself_WhenPadForgesProcessEnds_WhileItsPipeStaysOpen() => EndsWithItsServer(pipeStaysOpen: true);

        private static async Task EndsWithItsServer(bool pipeStaysOpen)
        {
            string exe = BuiltExe();
            string pipe = "PadForge.Tests.WebHelper." + Guid.NewGuid().ToString("N");
            using var server = new Child(PluginLauncherTests.ProbeExe, "serve " + pipe, new Dictionary<string, string>());
            string serving = server.NextLine(20000);
            Assert.True(serving != null && serving.StartsWith("serving ", StringComparison.Ordinal), "the probe serves no pipe: " + serving + " " + server.StandardError);
            var said = serving.Split(' ').Skip(1).Select(f => f.Split('=')).ToDictionary(f => f[0], f => long.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(server.Id, said["pid"]);

            using var helper = new Child(exe, "--web", new Dictionary<string, string>
            {
                [WebHelper.KeyVariable] = "key" + Guid.NewGuid().ToString("N"),
                [WebHelper.PipeVariable] = pipe,
            });
            Assert.Equal("connected", server.NextLine(30000));
            // The helper took its state and listens: it says its port.
            int port = 0;
            for (string line; port == 0 && (line = server.NextLine(30000)) != null;)
                if (JsonNode.Parse(line) is JsonObject o && o["t"]?.GetValue<string>() == "port") port = o["port"].GetValue<int>();
            Assert.True(port > 0, "the helper said no port. Standard error: " + helper.StandardError);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            string metadata = WebAddress.Loopback(port) + "/.well-known/oauth-protected-resource/mcp";
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(metadata)).StatusCode);

            SafeFileHandle kept = null;
            try
            {
                if (pipeStaysOpen)
                {
                    Assert.True(DuplicateHandle(server.Handle, new IntPtr(said["handle"]), GetCurrentProcess(), out kept, 0, false, DuplicateSameAccess),
                        "no copy of the probe's end: " + Marshal.GetLastWin32Error());
                    // The copy is the helper's pipe, served by the probe.
                    Assert.True(GetNamedPipeServerProcessId(kept, out uint serverId));
                    Assert.Equal((uint)server.Id, serverId);
                    Assert.True(GetNamedPipeClientProcessId(kept, out uint clientId));
                    Assert.Equal((uint)helper.Id, clientId);
                }
                server.Kill();
                Assert.True(server.Ends(10000));
                Assert.True(helper.Ends(15000), "the helper outlived the process that served its pipe. Standard error: " + helper.StandardError);
                Assert.True(helper.JobOpen);
                Assert.Equal(WebHelper.Done, helper.ExitCode);
                await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync(metadata));
            }
            finally
            {
                kept?.Dispose();
            }
        }
    }
}
