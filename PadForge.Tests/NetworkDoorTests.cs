using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using Xunit;
using WebRequest = PadForge.Services.Automation.Web.WebRequest;

namespace PadForge.Tests
{
    /// <summary>
    /// What PadForge sets up with its rights for Reach PadForge From This
    /// Network (#600 item 53): the URL reservation for the signed-in user's
    /// SID alone, the firewall rule on the Private profile alone, both taken
    /// away when the port changes, and the port picked below 49152 outside
    /// the ranges Windows excludes. Every netsh line is recorded by the
    /// test's own runner: the test host runs none.
    /// </summary>
    public class NetworkDoorTests
    {
        private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";

        /// <summary>netsh's table as this PC printed it on 2026-11-25
        /// (lanes2/N1-src/netsh_excludedportrange_this_pc.txt).</summary>
        internal const string ExcludedSample = "\r\nProtocol tcp Port Exclusion Ranges\r\n\r\nStart Port    End Port      \r\n----------    --------      \r\n"
            + "      2869        2869      \r\n      5357        5357      \r\n     49713       49812      \r\n     50000       50059     *\r\n"
            + "     63407       63506      \r\n\r\n* - Administered port exclusions.\r\n\r\n";

        [Fact]
        public void TheReservationAndTheRule_NameTheUsersSidAlone_AndThePrivateProfile()
        {
            var run = new List<string>();
            new NetworkDoor(a => { run.Add(a); return string.Empty; }).Apply(47321, Sid);
            Assert.Equal(new[]
            {
                "http delete urlacl url=http://+:47321/",
                "http add urlacl url=http://+:47321/ sddl=D:(A;;GX;;;" + Sid + ")",
                "advfirewall firewall delete rule name=\"PadForge Assistants on This Network\"",
                "advfirewall firewall add rule name=\"PadForge Assistants on This Network\" dir=in action=allow protocol=TCP localport=47321 profile=private",
            }, run);
            // Never every user, never another profile.
            Assert.DoesNotContain(run, line => line.Contains("WD)") || line.Contains("S-1-1-0") || line.Contains("profile=public") || line.Contains("profile=any"));

            // A SID that is not one, or a port outside the range, runs nothing.
            run.Clear();
            new NetworkDoor(a => { run.Add(a); return string.Empty; }).Apply(47321, "S-1-5-21 & calc.exe");
            new NetworkDoor(a => { run.Add(a); return string.Empty; }).Apply(49152, Sid);
            new NetworkDoor(a => { run.Add(a); return string.Empty; }).Apply(80, Sid);
            Assert.Empty(run);
        }

        [Fact]
        public void APortChange_TakesTheOldPairAway_ThenMakesTheNewOne()
        {
            var run = new List<string>();
            new NetworkDoor(a => { run.Add(a); return string.Empty; }).Apply(47400, Sid, before: 47321);
            Assert.Equal("http delete urlacl url=http://+:47321/", run[0]);
            Assert.Equal("advfirewall firewall delete rule name=\"PadForge Assistants on This Network\"", run[1]);
            Assert.Equal("http add urlacl url=http://+:47400/ sddl=D:(A;;GX;;;" + Sid + ")", run[3]);
            Assert.EndsWith("localport=47400 profile=private", run.Last());
            Assert.Equal(6, run.Count);
        }

        [Fact]
        public void TheWebControllersOwnRule_KeepsEveryProfile()
        {
            var run = new List<string>();
            WebControllerServer.EnsureInboundFirewallRule("PadForge Web Controller", "TCP", 8443, netsh: a => { run.Add(a); return string.Empty; });
            Assert.Equal("advfirewall firewall add rule name=\"PadForge Web Controller\" dir=in action=allow protocol=TCP localport=8443", run.Last());
        }

        [Fact]
        public void TheTestHost_RunsNoNetsh()
        {
            Assert.Throws<System.ComponentModel.Win32Exception>(() => WebControllerServer.RunNetsh(NetworkDoor.ExcludedRangesCommand));
            // The door's own runner is the fenced one, and nothing it does
            // throws past it.
            var door = new NetworkDoor();
            door.Apply(47321, Sid);
            door.Remove(47321);
        }

        [Fact]
        public void ThePort_IsPickedBelow49152_OutsideTheExcludedRanges_AndNotOneAProgramListensOn()
        {
            var ranges = NetworkDoor.ExcludedRanges(ExcludedSample);
            Assert.Equal(new[] { (2869, 2869), (5357, 5357), (49713, 49812), (50000, 50059), (63407, 63506) }, ranges);
            var random = new Random(600);
            var listening = new HashSet<int>(Enumerable.Range(20000, 20000));
            for (int i = 0; i < 500; i++)
            {
                int port = NetworkDoor.PickPort(ranges, listening, random);
                Assert.InRange(port, 1024, 49151);
                Assert.DoesNotContain(ranges, r => port >= r.Start && port <= r.End);
                Assert.DoesNotContain(port, listening);
            }
            // Every port excluded: none is picked.
            Assert.Equal(0, NetworkDoor.PickPort(new[] { (1024, 49151) }, null, random));
            // The door reads the table through its runner.
            var asked = new List<string>();
            int picked = new NetworkDoor(a => { asked.Add(a); return ExcludedSample; }).PickPort(random, new HashSet<int>());
            Assert.Equal(new[] { "interface ipv4 show excludedportrange protocol=tcp" }, asked);
            Assert.InRange(picked, 1024, 49151);
        }

        [Fact]
        public void OnlyPrivateIPv4Addresses_NameThisPc()
        {
            Assert.True(NetworkDoor.IsPrivate(IPAddress.Parse("192.168.1.24")));
            Assert.True(NetworkDoor.IsPrivate(IPAddress.Parse("10.0.0.7")));
            Assert.True(NetworkDoor.IsPrivate(IPAddress.Parse("172.31.255.1")));
            Assert.False(NetworkDoor.IsPrivate(IPAddress.Parse("172.32.0.1")));
            Assert.False(NetworkDoor.IsPrivate(IPAddress.Parse("8.8.8.8")));
            Assert.False(NetworkDoor.IsPrivate(IPAddress.Parse("127.0.0.1")));
            Assert.False(NetworkDoor.IsPrivate(IPAddress.Parse("fe80::1")));
            Assert.Equal("http://192.168.1.24:47321/mcp", NetworkDoor.Address("192.168.1.24", 47321));
            string key = NetworkDoor.NewKey();
            Assert.StartsWith("pfn_", key);
            Assert.Equal(36, key.Length);
        }
    }

    /// <summary>
    /// The network door's front (#600 item 53): a key on every call, a Host
    /// that names this PC with the door's port, Origin checked as the web
    /// door checks it, the MCP endpoint alone, and the web door's tokens
    /// and the network keys each refused at the other's door.
    /// </summary>
    public class NetworkFrontTests
    {
        private const string Public = "https://quiet-lake.ngrok-free.app";
        private readonly FakeWebApp _app = new();
        private readonly WebNetworkSide _net = new() { Names = () => new[] { "192.168.1.24", "10.0.0.7", "OFFICE-PC", "OFFICE-PC.local" }, Port = 47321, On = true };
        private readonly WebFront _front;
        private readonly string _key = NetworkDoor.NewKey();

        public NetworkFrontTests()
        {
            var auth = new WebAuth(_app, (u, c) => Task.FromResult<string>(null));
            var mcp = new WebMcp(_ => throw new InvalidOperationException("no request reaches PadForge here"));
            _front = new WebFront(41234, auth, mcp, _app, () => Public, network: _net);
            _net.Keys = new[] { ("k1", WebSecrets.Hash(_key)) };
        }

        private static WebRequest Post(string host, string key = null, string origin = null, string path = "/mcp", string method = "POST", bool network = true)
        {
            var headers = new List<(string, string)> { ("Host", host) };
            if (key != null) headers.Add(("Authorization", "Bearer " + key));
            if (origin != null) headers.Add(("Origin", origin));
            return new WebRequest(method, path, null, WebRequest.HeadersOf(headers.ToArray()), "{}") { Network = network, Remote = "192.168.1.30" };
        }

        private WebReply Handle(WebRequest request) => _front.HandleAsync(request, default).GetAwaiter().GetResult();

        [Fact]
        public void AKeylessCallFromTheNetwork_Gets401_AndAWrongKeyIsInvalid()
        {
            var none = Handle(Post("192.168.1.24:47321"));
            Assert.Equal(401, none.Status);
            Assert.Equal("Bearer realm=\"PadForge\"", none.Headers.Single(h => h.Key == "WWW-Authenticate").Value);
            var wrong = Handle(Post("192.168.1.24:47321", NetworkDoor.NewKey()));
            Assert.Equal(401, wrong.Status);
            Assert.Contains("error=\"invalid_token\"", wrong.Headers.Single(h => h.Key == "WWW-Authenticate").Value);
            Assert.Empty(_app.UsedIds);
        }

        [Fact]
        public void AKeyWorks_PastTheDoor_AndItsUseIsSaid()
        {
            // Past the door the endpoint's own checks answer: this request
            // names no Accept, so 406 comes from the MCP endpoint itself.
            var reply = Handle(Post("192.168.1.24:47321", _key));
            Assert.Equal(406, reply.Status);
            Assert.Equal(new[] { "net:k1" }, _app.UsedIds);
            // Each of this PC's names, in any case.
            Assert.Equal(406, Handle(Post("office-pc:47321", _key)).Status);
            Assert.Equal(406, Handle(Post("OFFICE-PC.local:47321", _key)).Status);
            Assert.Equal(406, Handle(Post("10.0.0.7:47321", _key)).Status);
        }

        [Fact]
        public void AWrongHost_Gets403()
        {
            foreach (string host in new[] { "evil.example:47321", "192.168.1.24:9999", "192.168.1.24", "127.0.0.1:47321", "192.168.1.99:47321", "" })
                Assert.Equal(403, Handle(Post(host, _key)).Status);
        }

        [Fact]
        public void ABrowserPageOnAnotherSite_IsRefused_ByItsOrigin()
        {
            Assert.Equal(403, Handle(Post("192.168.1.24:47321", _key, "http://evil.example")).Status);
            Assert.Equal(403, Handle(Post("192.168.1.24:47321", _key, "http://192.168.1.24:9999")).Status);
            Assert.Equal(406, Handle(Post("192.168.1.24:47321", _key, "http://192.168.1.24:47321")).Status);
        }

        [Fact]
        public void RevokeEndsIt_AtTheNextCall()
        {
            Assert.Equal(406, Handle(Post("192.168.1.24:47321", _key)).Status);
            _net.Keys = Array.Empty<(string, string)>();
            Assert.Equal(401, Handle(Post("192.168.1.24:47321", _key)).Status);
            _net.On = false;
            Assert.Equal(404, Handle(Post("192.168.1.24:47321", _key)).Status);
        }

        [Fact]
        public void TheNetworkDoor_ServesTheMcpEndpointAlone_AndTheTwoDoorsTakeNothingOfEachOther()
        {
            foreach (string path in new[] { "/.well-known/oauth-authorization-server", "/.well-known/oauth-protected-resource/mcp", "/register", "/token", "/authorize" })
                Assert.Equal(404, Handle(Post("192.168.1.24:47321", _key, path: path)).Status);
            Assert.Equal(405, Handle(Post("192.168.1.24:47321", _key, method: "GET")).Status);
            // A web assistant's access token is no key.
            string token = WebSecrets.New();
            lock (_app.List) _app.List.Add(new WebGrant { Id = "g1", Origin = "claude.ai", AccessHash = WebSecrets.Hash(token), AccessUntil = DateTimeOffset.UtcNow.AddHours(1), Resource = Public + "/mcp" });
            Assert.Equal(401, Handle(Post("192.168.1.24:47321", token)).Status);
            // A network key is no token at the web door.
            var web = Post("quiet-lake.ngrok-free.app", _key, network: false);
            Assert.Equal(401, Handle(web).Status);
            Assert.Empty(_app.UsedIds);
        }

        [Fact]
        public async Task WithTheWebSwitchOff_TheLoopbackDoorAnswersNothing()
        {
            var auth = new WebAuth(_app, (u, c) => Task.FromResult<string>(null));
            var front = new WebFront(41234, auth, new WebMcp(_ => throw new InvalidOperationException()), _app, () => Public, network: _net, webOn: () => false);
            var meta = new WebRequest("GET", "/.well-known/oauth-authorization-server", null, WebRequest.HeadersOf(("Host", "127.0.0.1:41234")), null);
            Assert.Equal(404, (await front.HandleAsync(meta, default)).Status);
            Assert.Equal(406, (await front.HandleAsync(Post("192.168.1.24:47321", _key), default)).Status);
        }
    }

    /// <summary>
    /// The app's side of the network door (#600 item 53): the port picked
    /// once and kept, the reservation and the rule made for the helper's
    /// user once it says who it is and taken away when the row goes off or
    /// the port changes, the keys kept as hashes, and the helper started
    /// while either switch is on.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class NetworkWebDoorTests
    {
        private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";

        private sealed class Rig : IDisposable
        {
            public readonly AssistantRig Assistant;
            public readonly WebAccessStore Store = new();
            public readonly WebTestLauncher Launcher = new() { Id = 4242 };
            public readonly List<string> Netsh = new();
            public readonly WebDoor Door;

            public Rig(AutomationLevel level = AutomationLevel.ChangeSettings)
            {
                Assistant = new AssistantRig(level);
                Door = new WebDoor(Assistant.Host, Store, Launcher, () => @"C:\PadForge\PadForge.exe", Assistant.PipeName,
                    network: new NetworkDoor(a => { lock (Netsh) Netsh.Add(a); return a.StartsWith("interface", StringComparison.Ordinal) ? NetworkDoorTests.ExcludedSample : string.Empty; }))
                {
                    // The desktop's user, whom the helper names.
                    HelperUserSeam = () => Sid,
                };
            }

            public string[] Ran() { lock (Netsh) return Netsh.ToArray(); }

            public void Dispose()
            {
                Door.Dispose();
                Assistant.Dispose();
            }
        }

        [Fact]
        public async Task TurningItOn_PicksThePortOnce_StartsTheHelper_AndReservesForTheHelpersUser()
        {
            using var rig = new Rig();
            Assert.False(rig.Store.On);
            rig.Door.TurnNetworkOn();
            // The pick runs on the pool, with the other netsh runs.
            await rig.Door.NetshDone();
            int port = rig.Store.NetworkPort;
            Assert.InRange(port, 1024, 49151);
            Assert.True(rig.Store.NetworkOn);
            // The helper starts for the network door alone.
            Assert.Single(rig.Launcher.Started);
            var state = rig.Door.State();
            Assert.False(state["web"].GetValue<bool>());
            Assert.True(state["network"]["on"].GetValue<bool>());
            // Nothing is reserved before the helper says whose it is.
            Assert.False(state["network"]["ready"].GetValue<bool>());
            Assert.Equal(new[] { NetworkDoor.ExcludedRangesCommand }, rig.Ran());

            rig.Door.Take(new JsonObject { ["t"] = "who", ["sid"] = Sid });
            await rig.Door.NetshDone();
            Assert.Contains("http add urlacl url=http://+:" + port + "/ sddl=D:(A;;GX;;;" + Sid + ")", rig.Ran());
            Assert.Contains(rig.Ran(), l => l.EndsWith("localport=" + port + " profile=private", StringComparison.Ordinal));
            Assert.True(rig.Door.State()["network"]["ready"].GetValue<bool>());

            // Off and on again keeps the port.
            rig.Door.TurnNetworkOff();
            await rig.Door.NetshDone();
            Assert.Equal("advfirewall firewall delete rule name=\"PadForge Assistants on This Network\"", rig.Ran().Last());
            Assert.Contains("http delete urlacl url=http://+:" + port + "/", rig.Ran());
            Assert.True(rig.Launcher.Last.Stopped);
            rig.Door.TurnNetworkOn();
            Assert.Equal(port, rig.Store.NetworkPort);
            // A SID that is not one is never handed on.
            int before = rig.Ran().Length;
            rig.Door.Take(new JsonObject { ["t"] = "who", ["sid"] = "S-1-5 & calc" });
            await rig.Door.NetshDone();
            Assert.Equal(before, rig.Ran().Length);
        }

        [Fact]
        public async Task APortChange_RemovesTheOldPair_AndATakenPortIsSaid_AndNeverMoved()
        {
            using var rig = new Rig();
            rig.Store.SetNetworkPort(47321);
            rig.Door.TurnNetworkOn();
            rig.Door.Take(new JsonObject { ["t"] = "who", ["sid"] = Sid });
            await rig.Door.NetshDone();
            rig.Door.Take(new JsonObject { ["t"] = "net", ["state"] = "taken", ["port"] = 47321 });
            Assert.Equal(new NetworkDoorState("taken", 47321), rig.Door.NetworkState);
            Assert.Equal(47321, rig.Store.NetworkPort);

            int before = rig.Ran().Length;
            Assert.False(rig.Door.ChangeNetworkPort(49152));
            Assert.False(rig.Door.ChangeNetworkPort(1023));
            Assert.True(rig.Door.ChangeNetworkPort(47400));
            await rig.Door.NetshDone();
            var after = rig.Ran().Skip(before).ToArray();
            Assert.Equal("http delete urlacl url=http://+:47321/", after[0]);
            Assert.Equal("advfirewall firewall delete rule name=\"PadForge Assistants on This Network\"", after[1]);
            Assert.Contains("http add urlacl url=http://+:47400/ sddl=D:(A;;GX;;;" + Sid + ")", after);
            Assert.Equal(47400, rig.Store.NetworkPort);
            Assert.Null(rig.Door.NetworkState);
        }

        [Fact]
        public async Task AKey_IsShownOnce_KeptAsItsHash_CappedAtTheUsersLevel_AndRevokeTakesItOut()
        {
            using var rig = new Rig(AutomationLevel.SendInputs);
            rig.Door.TurnNetworkOn();
            await rig.Door.NetshDone();
            string key = rig.Door.AddNetworkKey("Office PC", AutomationLevel.ChangeSettings);
            Assert.StartsWith("pfn_", key);
            var kept = rig.Store.Keys.Single();
            Assert.Equal("Office PC", kept.Name);
            Assert.Equal(AutomationLevel.SendInputs, kept.Level);
            Assert.True(WebSecrets.Matches(key, kept.Hash));
            Assert.Null(rig.Door.AddNetworkKey("   ", AutomationLevel.Read));

            var data = rig.Store.Save();
            var xml = new System.Xml.Serialization.XmlSerializer(typeof(WebAccessData));
            var text = new StringWriter();
            xml.Serialize(text, data);
            Assert.DoesNotContain(key, text.ToString());
            Assert.Contains(kept.Hash, text.ToString());
            var back = new WebAccessStore();
            back.Load((WebAccessData)xml.Deserialize(new StringReader(text.ToString())));
            Assert.True(back.NetworkOn);
            Assert.Equal(rig.Store.NetworkPort, back.NetworkPort);
            Assert.Equal(kept with { Made = back.Keys.Single().Made }, back.Keys.Single());

            // The helper hears the hash, never the key.
            string state = rig.Door.State().ToJsonString();
            Assert.Contains(kept.Hash, state);
            Assert.DoesNotContain(key, state);
            // Only the helper PadForge started, with the row on, names a key.
            string helperKey = rig.Launcher.Started.Last().Variables[WebHelper.KeyVariable];
            Assert.Equal(kept, rig.Door.AcceptNetwork(helperKey, 4242, kept.Id));
            Assert.Null(rig.Door.AcceptNetwork(helperKey, 4243, kept.Id));
            Assert.Null(rig.Door.AcceptNetwork(helperKey, 4242, "other"));

            rig.Door.RevokeNetworkKey(kept.Id);
            Assert.Empty(rig.Store.Keys);
            Assert.Null(rig.Door.AcceptNetwork(helperKey, 4242, kept.Id));
            Assert.Empty(rig.Door.State()["network"]["keys"].AsArray());
        }

        [Fact]
        public void TheWebSwitchOff_KeepsTheHelperForTheNetworkDoor()
        {
            using var rig = new Rig();
            rig.Door.TurnOn();
            rig.Door.TurnNetworkOn();
            Assert.Single(rig.Launcher.Started);
            rig.Door.TurnOff();
            Assert.False(rig.Launcher.Last.Stopped);
            Assert.False(rig.Door.State()["web"].GetValue<bool>());
            rig.Door.TurnNetworkOff();
            Assert.True(rig.Launcher.Last.Stopped);
        }
    }

    /// <summary>
    /// An assistant on this network end to end (#600 item 53): the app's
    /// door, the web helper run in the test's own process with its network
    /// listener on 127.0.0.1 (the test host binds nothing beyond loopback),
    /// and the SDK's own client sending the key as a Bearer header. PadForge
    /// lists it by its key's name and the address it called from, at the
    /// key's level, with the network's kind, and Revoke ends it.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class NetworkEndToEndTests
    {
        private static int FreePort()
        {
            for (int i = 0; i < 50; i++)
            {
                int port = Random.Shared.Next(20000, 40000);
                var probe = new TcpListener(IPAddress.Loopback, port);
                try { probe.Start(); return port; }
                catch (SocketException) { }
                finally { probe.Stop(); }
            }
            throw new InvalidOperationException("no free port");
        }

        [Fact]
        public async Task TheSdksClient_ConnectsWithItsKey_IsNamedByTheKeyAndItsAddress_AndRevokeEndsIt()
        {
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings);
            var store = new WebAccessStore();
            var launcher = new WebTestLauncher();
            var netsh = new List<string>();
            using var door = new WebDoor(rig.Host, store, launcher, () => @"C:\PadForge\PadForge.exe", rig.PipeName,
                network: new NetworkDoor(a => { lock (netsh) netsh.Add(a); return string.Empty; }))
            {
                // The helper runs in this process, as this process's user.
                HelperUserSeam = () => System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value,
            };
            int port = FreePort();
            store.SetNetworkPort(port);
            door.TurnNetworkOn();
            string key = door.AddNetworkKey("Office PC", AutomationLevel.Read);
            string helperKey = launcher.Started.Last().Variables[WebHelper.KeyVariable];
            using var stop = new CancellationTokenSource();
            var side = new WebNetworkSide { Names = () => new[] { "127.0.0.1" } };
            var helper = Task.Run(() => WebHelper.RunAsync(helperKey, rig.PipeName, stop.Token, networkPrefix: p => "http://127.0.0.1:" + p + "/", network: side));
            try
            {
                await AssistantRig.Until(() => door.NetworkState?.State == "listening", 20000);
                Assert.Contains(netsh, l => l.StartsWith("http add urlacl url=http://+:" + port + "/ sddl=D:(A;;GX;;;S-1-", StringComparison.Ordinal));

                string endpoint = "http://127.0.0.1:" + port + "/mcp";
                using (var http = new HttpClient())
                {
                    var bare = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent("{}") };
                    Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(bare)).StatusCode);
                }

                var transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(endpoint),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key },
                });
                await using (var client = await McpClient.CreateAsync(transport, new McpClientOptions { ClientInfo = new Implementation { Name = "net-test", Version = "1" } }))
                {
                    var status = await client.CallToolAsync("status");
                    Assert.NotEqual(true, status.IsError);

                    await AssistantRig.Until(() => rig.Host.Clients.Snapshot().Any(c => c.Kind == AutomationClientKind.Network));
                    var listed = rig.Host.Clients.Snapshot().Single(c => c.Kind == AutomationClientKind.Network);
                    Assert.Equal("Office PC on this network from 127.0.0.1", listed.Name);
                    var session = rig.Host.Sessions.Snapshot().Single(s => s.Kind == AutomationClientKind.Network);
                    // The key's level, which the user gave it, with no card.
                    Assert.Equal(AutomationLevel.Read, rig.Host.Sessions.LevelOf(session));
                    Assert.Empty(rig.Host.Approvals.Pending);
                    Assert.Equal("Office PC asks, on this network from 127.0.0.1, with the key you made today.", session.AskerLine);
                    var mark = MainWindow.ConnectedMarkState(rig.Host.Clients.Snapshot(), MainWindow.ClientIsFromWeb, "Devices", null);
                    Assert.True(mark.Network);
                    Assert.False(mark.Web);
                    await AssistantRig.Until(() => store.Keys.Single().LastUsed != null);

                    // Revoke: the session closes and the next call is refused.
                    door.RevokeNetworkKey(store.Keys.Single().Id);
                    await AssistantRig.Until(() => !rig.Host.Clients.Snapshot().Any(c => c.Kind == AutomationClientKind.Network));
                    using var again = new HttpClient();
                    var probe = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent("{}") };
                    probe.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                    await AssistantRig.Until(() => again.Send(probe.Clone()).StatusCode == HttpStatusCode.Unauthorized);
                }
            }
            finally
            {
                stop.Cancel();
                try { await helper.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            }
        }

        /// <summary>A port another program holds is said as taken, and the
        /// door stays on it: nothing moves to another port.</summary>
        [Fact]
        public async Task APortAnotherProgramHolds_IsSaidAsTaken_AndNeverMoved()
        {
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings);
            var store = new WebAccessStore();
            var launcher = new WebTestLauncher();
            using var door = new WebDoor(rig.Host, store, launcher, () => @"C:\PadForge\PadForge.exe", rig.PipeName,
                network: new NetworkDoor(_ => string.Empty))
            {
                HelperUserSeam = () => System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value,
            };
            int port = FreePort();
            var holder = new TcpListener(IPAddress.Loopback, port);
            holder.Start();
            using var stop = new CancellationTokenSource();
            Task<int> helper = null;
            try
            {
                store.SetNetworkPort(port);
                door.TurnNetworkOn();
                string helperKey = launcher.Started.Last().Variables[WebHelper.KeyVariable];
                helper = Task.Run(() => WebHelper.RunAsync(helperKey, rig.PipeName, stop.Token, networkPrefix: p => "http://127.0.0.1:" + p + "/",
                    network: new WebNetworkSide { Names = () => new[] { "127.0.0.1" } }));
                await AssistantRig.Until(() => door.NetworkState != null && door.NetworkState.State != "waiting", 20000);
                Assert.Equal(new NetworkDoorState("taken", port), door.NetworkState);
                Assert.Equal(port, store.NetworkPort);
            }
            finally
            {
                holder.Stop();
                stop.Cancel();
                if (helper != null) { try { await helper.WaitAsync(TimeSpan.FromSeconds(10)); } catch { } }
            }
        }
    }

    /// <summary>
    /// The network key while its window shows it once, and the Secure MCP
    /// Tunnel's runtime key blob, are on the read scrub and the problem
    /// recorder's list (#600 item 53, DESIGN-2 N4): a text that carries
    /// either comes out without it.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class NetworkScrubTests
    {
        [Fact]
        public void TheShownKeyAndTheBlob_AreScrubbedFromWhatAProgramReadsAndARecordingHolds()
        {
            string key = NetworkDoor.NewKey();
            const string blob = "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAAexampleblobtext==";
            var env = new PadForge.Engine.Privacy.PrivacyEnvironment { UserName = "someone", PcName = "PC", ProfileFolder = @"C:\Users\someone" };
            PadForge.ViewModels.WebAccessViewModel.ShownKey = key;
            WebAccessStore.Shared.SetRuntimeKey(blob);
            try
            {
                var live = AutomationReadScrub.LiveValues(env, new List<PadForge.Engine.Data.UserDevice>());
                Assert.Contains(live, v => v.Item2 == key);
                Assert.Contains(live, v => v.Item2 == blob);
                var scrubber = AutomationReadScrub.Make(env);
                string text = scrubber.Text("the key " + key + " and the blob " + blob + " end here");
                Assert.DoesNotContain(key, text);
                Assert.DoesNotContain(blob, text);
                Assert.Contains("end here", text);
            }
            finally
            {
                PadForge.ViewModels.WebAccessViewModel.ShownKey = null;
                WebAccessStore.Shared.SetRuntimeKey(null);
            }
            // Once the window closes, the key is on no list.
            Assert.DoesNotContain(AutomationReadScrub.LiveValues(env, new List<PadForge.Engine.Data.UserDevice>()), v => v.Item2 == key);
        }
    }

    internal static class HttpRequestClone
    {
        /// <summary>A request sent again: HttpClient sends one message once.</summary>
        internal static HttpRequestMessage Clone(this HttpRequestMessage request)
        {
            var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Content = new StringContent("{}") };
            copy.Headers.Authorization = request.Headers.Authorization;
            return copy;
        }
    }
}
