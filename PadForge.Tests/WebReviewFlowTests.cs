using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The web door's second round (#600 items 11 and 53): a reload that
    /// turns a row off closes that row's sessions on both sides, the answer
    /// page's address is read again each time, a web hello is refused while
    /// the web row is off, and the halves of earlier fixes that no test
    /// drove: the helper's sweep while the web row is off, an address read
    /// after a tunnel program's exit, and a helper that ends while a key
    /// waits for its blob.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WebReviewFlowTests
    {
        private const string Exe = @"C:\PadForge\PadForge.exe";

        private static NetworkKeyData Key(string id) => new()
        {
            Id = id, Name = "Office PC", Level = (int)AutomationLevel.Read, Hash = WebSecrets.Hash("pfn_" + id), Made = DateTime.UtcNow,
        };

        private static WebAssistantData Grant(string id) => new() { Id = id, Origin = "claude.ai", Allowed = DateTime.UtcNow };

        /// <summary>A pipe client that says one hello as the helper's copy
        /// would, answered ok.</summary>
        private static async Task<NamedPipeClientStream> HelloAsync(AssistantRig rig, string hello)
        {
            var pipe = new NamedPipeClientStream(".", rig.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(hello + "\n"));
            await pipe.FlushAsync();
            Assert.StartsWith("ok padforge=", await WebLink.ReadLineAsync(pipe, default));
            return pipe;
        }

        [Fact]
        public async Task AReloadThatTurnsTheNetworkRowOff_ClosesEveryKeysSession()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            var launcher = new WebTestLauncher();
            using var door = new WebDoor(rig.Host, store, launcher, () => Exe, rig.PipeName, network: new NetworkDoor(_ => string.Empty));
            store.Load(new WebAccessData { On = true, Network = true, NetworkPort = 47321, Keys = new[] { Key("k1") } });
            string helperKey = launcher.Started.Last().Variables[WebHelper.KeyVariable];
            using var pipe = await HelloAsync(rig, "hello kind=web token=" + helperKey + " net=k1 from=192.168.1.30");
            await AssistantRig.Until(() => rig.Host.Clients.Snapshot().Any(c => c.Kind == AutomationClientKind.Network));
            store.Load(new WebAccessData { On = true, Network = false, NetworkPort = 47321, Keys = new[] { Key("k1") } });
            await AssistantRig.Until(() => !rig.Host.Clients.Snapshot().Any(c => c.Kind == AutomationClientKind.Network), 3000);
        }

        [Fact]
        public async Task AReloadThatTurnsTheWebRowOff_ClosesEveryWebAssistantsSession()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            var launcher = new WebTestLauncher();
            using var door = new WebDoor(rig.Host, store, launcher, () => Exe, rig.PipeName, network: new NetworkDoor(_ => string.Empty));
            store.Load(new WebAccessData { On = true, Network = true, NetworkPort = 47321, Assistants = new[] { Grant("g1") } });
            string helperKey = launcher.Started.Last().Variables[WebHelper.KeyVariable];
            using var pipe = await HelloAsync(rig, "hello kind=web token=" + helperKey + " grant=g1");
            await AssistantRig.Until(() => rig.Host.Clients.Snapshot().Any(c => c.Kind == AutomationClientKind.Web));
            store.Load(new WebAccessData { On = false, Network = true, NetworkPort = 47321, Assistants = new[] { Grant("g1") } });
            await AssistantRig.Until(() => !rig.Host.Clients.Snapshot().Any(c => c.Kind == AutomationClientKind.Web), 3000);
        }

        [Fact]
        public void AWebHello_IsRefusedWhileTheWebRowIsOff()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            var launcher = new WebTestLauncher();
            using var door = new WebDoor(rig.Host, store, launcher, () => Exe, rig.PipeName, network: new NetworkDoor(_ => string.Empty));
            store.Load(new WebAccessData { On = false, Network = true, NetworkPort = 47321, Assistants = new[] { Grant("g1") } });
            string helperKey = launcher.Started.Last().Variables[WebHelper.KeyVariable];
            Assert.Null(door.AcceptWeb(helperKey, Environment.ProcessId, "g1"));
            store.SetOn(true);
            Assert.Equal("g1", door.AcceptWeb(helperKey, Environment.ProcessId, "g1")?.Id);
        }

        private static JsonObject State(bool web, bool network)
            => new()
            {
                ["t"] = "state",
                ["web"] = web,
                ["armedUntil"] = 0L,
                ["grants"] = new JsonArray(WebWire.ToJson(new WebGrant { Id = "g1", Origin = "claude.ai" })),
                ["network"] = new JsonObject
                {
                    ["on"] = network,
                    ["port"] = 47321,
                    ["ready"] = false,
                    ["keys"] = new JsonArray(new JsonObject { ["id"] = "k1", ["hash"] = WebSecrets.Hash("pfn_k1") }),
                },
            };

        /// <summary>The helper keeps a grant's copy while the web row is
        /// on, and a key's while the network row is.</summary>
        [Fact]
        public async Task TheHelpersSweep_KeepsACopyOnlyWhileItsRowIsOn()
        {
            await using var link = new WebLink(new RecordingLink());
            link.Take(State(web: true, network: true));
            Assert.Equal(new[] { "g1", "k1" }, WebHelper.AllowedIds(link).OrderBy(i => i, StringComparer.Ordinal));
            link.Take(State(web: false, network: true));
            Assert.Equal(new[] { "k1" }, WebHelper.AllowedIds(link));
        }

        [Fact]
        public async Task TheHelpersSweep_DropsTheKeysWhileTheNetworkRowIsOff()
        {
            await using var link = new WebLink(new RecordingLink());
            link.Take(State(web: true, network: false));
            Assert.Equal(new[] { "g1" }, WebHelper.AllowedIds(link));
        }

        /// <summary>An address line read after the tunnel program ended is
        /// no address: no running report follows the stopped one.</summary>
        [Fact]
        public async Task AnAddressAfterTheTunnelProgramEnded_IsNoAddress()
        {
            Process made = null;
            try
            {
                // An object no process stands behind: nothing is started.
                WebTunnels.StartSeam = _ => made = new Process();
                var stream = new RecordingLink();
                await using var link = new WebLink(stream);
                var state = State(web: true, network: false);
                state["tunnel"] = "cloudflared";
                state["tunnelPath"] = @"C:\Tools\cloudflared.exe";
                state["tunnelRun"] = 1;
                link.Take(state);
                link.Port = 5123;
                Assert.NotNull(made);
                link.TunnelExited("cloudflared", made, null);
                link.TunnelAddress("cloudflared", made, "https://late-line.trycloudflare.com");
                await AssistantRig.Until(() => stream.Messages.Any(m => m["t"]?.GetValue<string>() == "tunnel" && m["state"]?.GetValue<string>() == "stopped"));
                await Task.Delay(200);
                Assert.DoesNotContain(stream.Messages, m => m["t"]?.GetValue<string>() == "tunnel" && m["state"]?.GetValue<string>() == "running");
                Assert.Null(link.PublicBase);
            }
            finally
            {
                WebTunnels.StartSeam = null;
                made?.Dispose();
            }
        }

        /// <summary>A key sent to a helper that ends before its blob comes
        /// back is said not kept.</summary>
        [Fact]
        public async Task AHelperThatEndsWhileAKeyWaits_SaysTheKeyIsNotKept()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            var launcher = new WebTestLauncher();
            using var door = new WebDoor(rig.Host, store, launcher, () => Exe, rig.PipeName)
            {
                DelaySeam = _ => new TaskCompletionSource<bool>().Task,
            };
            door.TurnOn();
            using var link = new RecordingLink();
            _ = door.ServeHelperAsync(link, default);
            await AssistantRig.Until(() => link.Lines.Count > 0);
            Assert.True(door.SetRuntimeKey("sk-proj-1"));
            Assert.Null(door.RuntimeKeyWords);
            launcher.Last.Exit(1);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Web_ChatGpt_KeyHelperEnded, door.RuntimeKeyWords);
        }

        /// <summary>A line that says the helper is not running leaves when a
        /// helper connects. The one that says it ended with a key waiting
        /// stays true after the restart, and stays.</summary>
        [Fact]
        public async Task TheKeysLines_StayTrueOnceTheHelperIsBack()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            var launcher = new WebTestLauncher();
            var restart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var door = new WebDoor(rig.Host, store, launcher, () => Exe, rig.PipeName) { DelaySeam = _ => restart.Task };
            door.TurnOn();
            Assert.False(door.SetRuntimeKey("sk-proj-1"));
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Web_ChatGpt_KeyNotKept, door.RuntimeKeyWords);
            using (var first = new RecordingLink())
            {
                _ = door.ServeHelperAsync(first, default);
                await AssistantRig.Until(() => first.Lines.Count > 0);
                Assert.Null(door.RuntimeKeyWords);
                Assert.True(door.SetRuntimeKey("sk-proj-1"));
                launcher.Last.Exit(1);
            }
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Web_ChatGpt_KeyHelperEnded, door.RuntimeKeyWords);
            restart.SetResult(true);
            await AssistantRig.Until(() => launcher.Started.Count == 2);
            using var second = new RecordingLink();
            _ = door.ServeHelperAsync(second, default);
            await AssistantRig.Until(() => second.Lines.Count > 0);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Web_ChatGpt_KeyHelperEnded, door.RuntimeKeyWords);
        }

        /// <summary>The network row turned off while its first port pick
        /// runs keeps no reservation and no rule for the port picked.</summary>
        [Fact]
        public async Task TheNetworkRowOffDuringItsFirstPick_KeepsNoReservation()
        {
            const string sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
            using var gate = new System.Threading.ManualResetEventSlim(false);
            var ran = new List<string>();
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => Exe, rig.PipeName,
                network: new NetworkDoor(a =>
                {
                    if (a == NetworkDoor.ExcludedRangesCommand) gate.Wait(5000);
                    lock (ran) ran.Add(a);
                    return a == NetworkDoor.ExcludedRangesCommand ? NetworkDoorTests.ExcludedSample : string.Empty;
                }))
            {
                HelperUserSeam = () => sid,
            };
            // The web row keeps a helper, whose user is known.
            door.TurnOn();
            door.Take(new JsonObject { ["t"] = "who", ["sid"] = sid });
            door.TurnNetworkOn();
            door.TurnNetworkOff();
            gate.Set();
            await door.NetshDone();
            int port = store.NetworkPort;
            Assert.InRange(port, NetworkDoor.LowestPort, NetworkDoor.HighestPort);
            lock (ran)
            {
                Assert.DoesNotContain(ran, l => l.StartsWith("http add urlacl", StringComparison.Ordinal));
                Assert.DoesNotContain(ran, l => l.StartsWith("advfirewall firewall add rule", StringComparison.Ordinal));
            }
        }

        /// <summary>The answer page's address follows this PC's addresses
        /// when it is read, with no new status from the web controller.</summary>
        [Fact]
        public void TheAnswerPagesAddress_IsReadAgainEachTime()
        {
            try
            {
                NetworkDoor.AddressesSeam = () => Array.Empty<string>();
                var answers = new WebAnswer();
                answers.SetAddress("https://100.101.102.103:8080", null);
                Assert.Null(answers.Address);
                Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Answer_NoPrivateAddress, answers.AddressProblem);
                NetworkDoor.AddressesSeam = () => new[] { "192.168.1.24" };
                Assert.Equal("https://192.168.1.24:8080/answer", answers.Address);
                Assert.Null(answers.AddressProblem);
                NetworkDoor.AddressesSeam = () => new[] { "10.0.0.7" };
                Assert.Equal("https://10.0.0.7:8080/answer", answers.Address);
                answers.SetAddress(null, "stopped");
                Assert.Null(answers.Address);
                Assert.Equal("stopped", answers.AddressProblem);
            }
            finally
            {
                NetworkDoor.AddressesSeam = null;
            }
        }
    }
}
