using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
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
        /// pipe. It starts one child: a later start, as the door's restart
        /// after the child ended, is refused and counted.</summary>
        private sealed class ChildLauncher : IPluginLauncher
        {
            private readonly string _exe;
            public Child Started;
            public ChildProcess Process;
            public int Refused;

            /// <summary>Whether the door's stop ends the child. A test that
            /// shows the child ends by itself turns it off.</summary>
            public bool StopEndsChild = true;

            public ChildLauncher(string exe) => _exe = exe;

            public PluginLaunch Start(PluginCommand command, IReadOnlyDictionary<string, string> variables)
            {
                if (Started != null)
                {
                    Interlocked.Increment(ref Refused);
                    return PluginLaunch.Refused(PluginRefusal.Failed);
                }
                Assert.EndsWith(" --web", command.CommandLine);
                Started = new Child(_exe, "--web", variables);
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
                _child.Process.EnableRaisingEvents = true;
                _child.Process.Exited += (_, _) => Exited?.Invoke(_child.Process.ExitCode);
            }
            public int Id => _child.Process.Id;
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
            var launcher = new ChildLauncher(exe);
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
                var mapped = MappedFiles(launcher.Started.Process.Id);
                Assert.Contains("System.Private.CoreLib.dll", mapped);
                Assert.Contains("System.Net.HttpListener.dll", mapped);
                Assert.False(Wpf.Any(mapped.Contains), "WPF in the child: " + string.Join(", ", Wpf.Where(mapped.Contains)) + ". Standard error: " + launcher.Started.StandardError);
                Assert.Equal(Rights(Environment.ProcessId), Rights(launcher.Started.Process.Id));

                // Turning the door off ends the helper, through its job, and
                // nothing listens.
                door.TurnOff();
                Assert.True(launcher.Started.Ends(20000), "the helper did not end when the door stopped it. Standard error: " + launcher.Started.StandardError);
                Assert.Equal(1, launcher.Process.Stops);
                Assert.Equal(WebHelperState.Off, door.HelperState);
                await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync(root + "/.well-known/oauth-protected-resource/mcp"));
                // Its protocol is HTTP: standard output held nothing.
                Assert.Empty(launcher.Started.StandardOutput);
                Assert.True(launcher.Started.VisibleWindows.IsEmpty, "the helper showed a window: " + string.Join(", ", launcher.Started.VisibleWindows.Distinct()));
                Assert.Equal(0, launcher.Refused);
            }
            finally
            {
                launcher.Started?.Dispose();
            }
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
                Assert.Equal(WebHelper.Done, launcher.Started.Process.ExitCode);
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
            Assert.Equal(server.Process.Id, said["pid"]);

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
                    Assert.True(DuplicateHandle(server.Process.SafeHandle, new IntPtr(said["handle"]), GetCurrentProcess(), out kept, 0, false, DuplicateSameAccess),
                        "no copy of the probe's end: " + Marshal.GetLastWin32Error());
                    // The copy is the helper's pipe, served by the probe.
                    Assert.True(GetNamedPipeServerProcessId(kept, out uint serverId));
                    Assert.Equal((uint)server.Process.Id, serverId);
                    Assert.True(GetNamedPipeClientProcessId(kept, out uint clientId));
                    Assert.Equal((uint)helper.Process.Id, clientId);
                }
                server.Process.Kill();
                Assert.True(server.Ends(10000));
                Assert.True(helper.Ends(15000), "the helper outlived the process that served its pipe. Standard error: " + helper.StandardError);
                Assert.True(helper.JobOpen);
                Assert.Equal(WebHelper.Done, helper.Process.ExitCode);
                await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync(metadata));
            }
            finally
            {
                kept?.Dispose();
            }
        }
    }
}
