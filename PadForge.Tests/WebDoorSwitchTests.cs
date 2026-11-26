using System;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The web door after each switch and after a reload (#600 items 11 and
    /// 53): the helper hears the web row's switch at once, the row's off
    /// closes every web assistant's session, a reload that turns both rows
    /// off stops the helper, a key the helper could not encrypt says so, and
    /// a client that signs in again keeps the reach the user gave it.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WebDoorSwitchTests
    {
        private const string Exe = @"C:\PadForge\PadForge.exe";

        private static bool IsState(JsonObject m) => m["t"]?.GetValue<string>() == "state";

        [Fact]
        public async Task TheWebRowOn_WhileTheNetworkRowKeepsTheHelper_IsSaidToTheHelper()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => Exe, rig.PipeName, network: new NetworkDoor(_ => string.Empty));
            door.TurnNetworkOn();
            // The port's pick says a state of its own when it ends.
            await door.NetshDone();
            using var link = new RecordingLink();
            _ = door.ServeHelperAsync(link, default);
            await AssistantRig.Until(() => link.Messages.Any(IsState));
            int before = link.Lines.Count;
            door.TurnOn();
            await AssistantRig.Until(() => link.Messages.Skip(before).Any(m => IsState(m) && m["web"].GetValue<bool>()), 2000);
        }

        [Fact]
        public async Task TheWebRowOff_ClosesEveryWebAssistantsSession()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            var launcher = new WebTestLauncher();
            using var door = new WebDoor(rig.Host, store, launcher, () => Exe, rig.PipeName, network: new NetworkDoor(_ => string.Empty));
            door.TurnOn();
            door.TurnNetworkOn();
            string key = launcher.Started.Single().Variables[WebHelper.KeyVariable];
            Assert.True(store.Add(new WebGrant { Id = "g1", Origin = "claude.ai", Allowed = DateTimeOffset.UtcNow }));
            using var pipe = new NamedPipeClientStream(".", rig.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes("hello kind=web token=" + key + " grant=g1\n"));
            await pipe.FlushAsync();
            Assert.StartsWith("ok padforge=", await WebLink.ReadLineAsync(pipe, default));
            await AssistantRig.Until(() => rig.Host.Clients.Snapshot().Any(c => c.Kind == AutomationClientKind.Web));
            door.TurnOff();
            // The network row keeps the helper, and the web assistant's
            // session ends with its row.
            Assert.False(launcher.Last.Stopped);
            await AssistantRig.Until(() => !rig.Host.Clients.Snapshot().Any(c => c.Kind == AutomationClientKind.Web), 3000);
        }

        [Fact]
        public void AReloadWithBothRowsOff_StopsTheHelper()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            var launcher = new WebTestLauncher();
            using var door = new WebDoor(rig.Host, store, launcher, () => Exe, rig.PipeName, network: new NetworkDoor(_ => string.Empty));
            door.TurnOn();
            Assert.Single(launcher.Started);
            store.Load(new WebAccessData { On = false, Network = false });
            Assert.True(launcher.Last.Stopped);
            Assert.Equal(WebHelperState.Off, door.HelperState);
        }

        /// <summary>A reload that turns the network row off takes its
        /// reservation and rule away, as the row's own switch does.</summary>
        [Fact]
        public async Task AReloadThatTurnsTheNetworkRowOff_TakesItsReservationAway()
        {
            const string sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            var ran = new System.Collections.Generic.List<string>();
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => Exe, rig.PipeName,
                network: new NetworkDoor(a => { lock (ran) ran.Add(a); return string.Empty; }))
            {
                HelperUserSeam = () => sid,
            };
            store.SetNetworkPort(47321);
            door.TurnNetworkOn();
            door.Take(new JsonObject { ["t"] = "who", ["sid"] = sid });
            await door.NetshDone();
            int before;
            lock (ran) before = ran.Count;
            store.Load(new WebAccessData { On = true, Network = false, NetworkPort = 47321 });
            await door.NetshDone();
            lock (ran) Assert.Contains("http delete urlacl url=http://+:47321/", ran.Skip(before));
        }

        [Fact]
        public void AKeyTheHelperCouldNotEncrypt_IsSaidNotKept()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => Exe, rig.PipeName);
            door.Take(new JsonObject { ["t"] = "protected", ["blob"] = null });
            Assert.NotNull(door.RuntimeKeyWords);
            Assert.Null(store.RuntimeKeyBlob);
        }

        [Fact]
        public async Task AClientThatSignsInAgain_KeepsTheReachTheUserGaveIt()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => Exe, rig.PipeName);
            const string client = "https://claude.ai/oauth/doc", redirect = "https://claude.ai/api/mcp/auth_callback";
            Assert.True(store.Add(new WebGrant { Id = "old", Origin = "claude.ai", ClientId = client, RedirectUri = redirect, Allowed = DateTimeOffset.UtcNow }));
            var reach = ProgramReach.OwnDevices;
            door.SetGrantReach("old", reach);
            door.Arm();
            var asking = door.AskAsync(new WebSignIn("sid", "claude.ai", "Claude", "123456", "quiet-lake.ngrok-free.app", false, client, redirect,
                "https://quiet-lake.ngrok-free.app/mcp"));
            await AssistantRig.Until(() => rig.Host.Approvals.Pending.Count == 1);
            rig.Host.Approvals.Answer(rig.Host.Approvals.Pending.Single().Id, true);
            string id = await asking;
            Assert.NotEqual("old", id);
            Assert.Equal(reach, store.Grants.Single().Reach);
        }
    }
}
