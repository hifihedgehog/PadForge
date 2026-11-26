using System.Linq;
using System.Text.Json.Nodes;
using PadForge.Services.Automation.Web;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Assistants on the Web card's view (#600 item 11): a Copy note
    /// leaves with the address it was about, the paste box follows a reload,
    /// and the Tunnel box is told its pick again after its list is rebuilt.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WebAccessViewFlowTests
    {
        private const string Exe = @"C:\PadForge\PadForge.exe";

        [Fact]
        public void TheCopiedNote_LeavesWhenTheAddressChanges()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => Exe, rig.PipeName);
            string copied = null;
            var view = new WebAccessViewModel(door, store) { ClipboardSeam = text => copied = text };
            door.PickTunnel("paste", "https://a.example.com");
            view.Refresh();
            view.CopyAddressCommand.Execute(null);
            Assert.Equal("https://a.example.com/mcp", copied);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Web_Address_Copied, view.AddressNote);
            view.Refresh();
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Web_Address_Copied, view.AddressNote);
            door.PickTunnel("paste", "https://b.example.com");
            view.Refresh();
            Assert.True(string.IsNullOrEmpty(view.AddressNote));
        }

        /// <summary>While the network door's port is picked, the Address row
        /// lists no address on port 0 and does not say the PC has none.</summary>
        [Fact]
        public async System.Threading.Tasks.Task WhileThePortIsPicked_TheAddressRowSaysNothingUntrue()
        {
            try
            {
                NetworkDoor.AddressesSeam = () => new[] { "192.168.1.24" };
                using var gate = new System.Threading.ManualResetEventSlim(false);
                using var rig = new AssistantRig();
                var store = new WebAccessStore();
                using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => Exe, rig.PipeName,
                    network: new NetworkDoor(a => { gate.Wait(5000); return a == NetworkDoor.ExcludedRangesCommand ? NetworkDoorTests.ExcludedSample : string.Empty; }));
                var view = new WebAccessViewModel(door, store) { AskToTurnNetworkOn = () => true };
                view.IsNetworkOn = true;
                Assert.Empty(view.NetworkAddresses);
                Assert.False(view.ShowsNoAddress);
                gate.Set();
                await door.NetshDone();
                view.Refresh();
                Assert.Equal("http://192.168.1.24:" + store.NetworkPort + "/mcp", view.NetworkAddresses.Single().Address);
                Assert.False(view.ShowsNoAddress);
            }
            finally
            {
                NetworkDoor.AddressesSeam = null;
            }
        }

        [Fact]
        public void ThePasteBox_FollowsAReload()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => Exe, rig.PipeName);
            store.Load(new WebAccessData { Tunnel = "paste", Address = "https://a.example.com" });
            var view = new WebAccessViewModel(door, store);
            Assert.Equal("https://a.example.com", view.PastedAddress);
            store.Load(new WebAccessData { Tunnel = "paste", Address = "https://b.example.com" });
            Assert.Equal("https://b.example.com", view.PastedAddress);
            view.SelectedTunnel = view.Tunnels.First(t => t.Key == null);
            view.SelectedTunnel = view.Tunnels.First(t => t.Key == "paste");
            Assert.Equal("https://b.example.com", store.Address);
        }

        [Fact]
        public void TheTunnelBox_IsToldItsPickAgain_AfterItsListIsRebuilt()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => Exe, rig.PipeName);
            store.Load(new WebAccessData { On = true, Tunnel = "ngrok" });
            var view = new WebAccessViewModel(door, store);
            var before = view.Tunnels.ToList();
            int told = 0;
            view.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(WebAccessViewModel.SelectedTunnel)) told++; };
            door.Take(new JsonObject
            {
                ["t"] = "found",
                ["tunnels"] = new JsonArray(new JsonObject { ["key"] = "ngrok", ["path"] = @"C:\Tools\ngrok.exe" }),
            });
            Assert.False(before.SequenceEqual(view.Tunnels));
            Assert.Equal("ngrok", view.SelectedTunnel.Key);
            Assert.True(told > 0);
        }
    }
}
