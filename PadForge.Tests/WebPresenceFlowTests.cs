using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jint;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// What the web helper finds and says, and the answer page's address
    /// (#600 items 11 and 53): the helper looks for tunnel programs again
    /// when PadForge asks, every hello names its protocol, a taken network
    /// port is tried again, the answer page's QR code names an address on a
    /// private network, and the page tells a closed door from a typed code
    /// it waits for.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WebPresenceFlowTests
    {
        private const string Exe = @"C:\PadForge\PadForge.exe";

        [Fact]
        public async Task TheHelper_LooksForTunnelProgramsAgain_WhenPadForgeAsks()
        {
            try
            {
                WebTunnels.ExistsSeam = (_, _) => false;
                var stream = new RecordingLink();
                await using (var link = new WebLink(stream))
                {
                    link.SayFound();
                    await AssistantRig.Until(() => stream.Lines.Count == 1);
                    Assert.Empty(stream.Messages[0]["tunnels"].AsArray());
                    // cloudflared installed while the helper runs.
                    WebTunnels.ExistsSeam = (_, exe) => exe == "cloudflared.exe";
                    link.Take(new JsonObject { ["t"] = "find" });
                    await AssistantRig.Until(() => stream.Lines.Count == 2, 2000);
                    var again = stream.Messages[1];
                    Assert.Equal("found", again["t"].GetValue<string>());
                    Assert.Contains(again["tunnels"].AsArray(), t => t["key"].GetValue<string>() == "cloudflared");
                }
            }
            finally
            {
                WebTunnels.ExistsSeam = null;
            }

            // The door asks when the page shows.
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => Exe, rig.PipeName);
            door.TurnOn();
            using var helper = new RecordingLink();
            _ = door.ServeHelperAsync(helper, default);
            await AssistantRig.Until(() => helper.Lines.Count > 0);
            door.LookForTunnels();
            await AssistantRig.Until(() => helper.Messages.Any(m => m["t"]?.GetValue<string>() == "find"), 2000);
        }

        [Fact]
        public void EveryHelloOfTheWebHelperAndOfAnAssistantsCopy_NamesItsProtocol()
        {
            string Protocol = " protocol=" + AutomationLine.Protocol;
            Assert.Contains(Protocol, WebHelper.HelperHello("k"));
            Assert.Contains(Protocol, WebHelper.MirrorHello(new WebGrant { Id = "g" }, "k"));
            Assert.Contains(Protocol, WebHelper.MirrorHello(new WebGrant { Id = "n", Network = true, From = "192.168.1.30" }, "k"));
            Assert.Contains(Protocol, AssistantMirror.McpHello("Claude", "claude.exe"));
            Assert.Contains(Protocol, AssistantCommandLine.HelloLine(AutomationLevel.Read));
        }

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

        /// <summary>A port another program held is served once it lets go,
        /// with no word from PadForge.</summary>
        [Fact]
        public async Task ATakenNetworkPort_IsTriedAgain_AndServedOnceItIsFree()
        {
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings);
            var store = new WebAccessStore();
            var launcher = new WebTestLauncher();
            using var door = new WebDoor(rig.Host, store, launcher, () => Exe, rig.PipeName, network: new NetworkDoor(_ => string.Empty))
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
                    network: new WebNetworkSide { Names = () => new[] { "127.0.0.1" } }, networkRetry: TimeSpan.FromMilliseconds(200)));
                await AssistantRig.Until(() => door.NetworkState?.State == "taken", 20000);
                holder.Stop();
                await AssistantRig.Until(() => door.NetworkState?.State == "listening", 5000);
                Assert.Equal(port, door.NetworkState.Port);
            }
            finally
            {
                holder.Stop();
                stop.Cancel();
                if (helper != null) { try { await helper.WaitAsync(TimeSpan.FromSeconds(10)); } catch { } }
            }
        }

        /// <summary>The web controller's own host can be an address outside
        /// the private ranges (a VPN's, Tailscale's 100.64/10): the answer
        /// page's address names this PC's private one, which the page's own
        /// checks take.</summary>
        [Fact]
        public void TheAnswerPagesAddress_NamesThisPcsPrivateAddress()
        {
            try
            {
                NetworkDoor.AddressesSeam = () => new[] { "192.168.1.24" };
                var answers = new WebAnswer();
                answers.SetAddress("https://100.101.102.103:8080", null);
                Assert.Equal("https://192.168.1.24:8080/answer", answers.Address);
                answers.SetAddress("https://localhost:8080", null);
                Assert.Equal("https://192.168.1.24:8080/answer", answers.Address);
                answers.SetAddress("https://10.0.0.7:8080", null);
                Assert.Equal("https://10.0.0.7:8080/answer", answers.Address);
                // With none, the dialog says why.
                NetworkDoor.AddressesSeam = () => Array.Empty<string>();
                answers.SetAddress("https://100.101.102.103:8080", null);
                Assert.Null(answers.Address);
                Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Answer_NoPrivateAddress, answers.AddressProblem);
            }
            finally
            {
                NetworkDoor.AddressesSeam = null;
            }
        }

        /// <summary>The answer page's script run in Jint against a fetch of
        /// the test's own: its first refresh, and what the pairing line says.</summary>
        private static string PairLineAfter(int status, string body)
        {
            string page = File.ReadAllText(ExtrasPageTests.RepoText("PadForge.App", "WebAssets", "answer.html") is { } text && text.Length > 0
                ? Path.Combine(Root(), "PadForge.App", "WebAssets", "answer.html") : throw new InvalidOperationException());
            int start = page.IndexOf("<script>", StringComparison.Ordinal) + "<script>".Length;
            string script = page.Substring(start, page.IndexOf("</script>", start, StringComparison.Ordinal) - start);
            var engine = new Jint.Engine(o => { o.TimeoutInterval(TimeSpan.FromSeconds(5)); o.MaxStatements(200_000); });
            engine.Execute(@"
                var elements = {};
                function el(id) {
                    if (!elements[id]) elements[id] = { id: id, hidden: false, textContent: '', value: '', disabled: false, firstChild: null, dataset: {},
                        addEventListener: function () {}, appendChild: function () {}, removeChild: function () {},
                        querySelectorAll: function () { return { forEach: function () {} }; } };
                    return elements[id];
                }
                var made = 0;
                var document = { hidden: false, getElementById: el, addEventListener: function () {},
                    createElement: function () { made++; return el('made' + made); },
                    querySelectorAll: function () { return { forEach: function () {} }; } };
                var location = { hash: '', pathname: '/answer' };
                var history = { replaceState: function () {} };
                var navigator = { userAgent: '' };
                function setInterval() {}
                var answer = null;
                function fetch() {
                    return Promise.resolve({ status: answer.status,
                        json: function () { return answer.body === null ? Promise.reject(new Error('not json')) : Promise.resolve(JSON.parse(answer.body)); } });
                }");
            engine.Execute("answer = { status: " + status + ", body: " + (body == null ? "null" : "'" + body + "'") + " };");
            engine.Execute(script);
            engine.Advanced.ProcessTasks();
            return engine.Evaluate("el('pairLine').textContent").ToString();
        }

        private static string Root()
        {
            string root = AppContext.BaseDirectory;
            while (!Directory.Exists(Path.Combine(root, "PadForge.App"))) root = Path.GetDirectoryName(root);
            return root;
        }

        [Fact]
        public void TheAnswerPage_AsksForTheCode_OnlyWhileAPairingIsOpen()
        {
            const string closed = "No pairing is open.";
            const string code = "Type the six digits PadForge shows.";
            // No page served: no device, no pairing, or a stopped route.
            Assert.StartsWith(closed, PairLineAfter(404, null));
            // A Host or a peer the page refuses, whether a pairing is open
            // or not (WebAnswer.Handle answers 403 before it reads either).
            Assert.StartsWith("PadForge answers this page only on its own network and at its own address.", PairLineAfter(403, null));
            Assert.StartsWith(closed, PairLineAfter(401, "{\"paired\":false,\"pairing\":false}"));
            // A pairing waits for its code.
            Assert.StartsWith(code, PairLineAfter(401, "{\"paired\":false,\"pairing\":true}"));
        }
    }
}
