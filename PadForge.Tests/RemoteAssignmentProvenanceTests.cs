using System;
using System.Linq;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using PadForge.Services;

namespace PadForge.Tests
{
    public partial class DeviceUnassignConfigLifecycleTests
    {
        [Theory]
        [InlineData("Virtual Gamepad 1")]
        [InlineData("ViGEm practice pad")]
        [InlineData("My pad")]
        public async Task SharedWebProvenanceAllowsQueryAndAssignmentDespiteItsDisplayName(string name)
        {
            var previousRefreshHook = SettingsService.AfterMappingSetsRefreshed;
            LinkAssignmentChannel client = null, server = null;
            try
            {
                var (vm, svc, _) = Arrange();
                var settings = new SettingsService(vm);
                svc.SettingsService = settings;
                var devices = new DeviceService(vm, settings);
                var peer = settings.RemoteLink.Trust.Grant(new byte[32], "Streaming PC", "today", true, true, true);
                // The path also contains a heuristic marker. Only the wrapper's provenance is trusted.
                var web = new WebControllerDevice("custom:virtual-" + Guid.NewGuid().ToString("N"),
                    name, false, "custom:provenance");
                var local = AddProvenanceRow(web);
                var remote = new RemotePeerDevice(new RemotePeerDeviceInfo
                {
                    PeerFingerprintHex = peer.FingerprintHex,
                    PeerLocalDeviceId = web.InstanceGuid.ToString("N"),
                    Name = name + " (Streaming PC)",
                    VendorId = web.VendorId,
                    ProductId = web.ProductId,
                    NumAxes = web.NumAxes,
                    NumButtons = web.NumButtons,
                    NumHats = web.NumHats,
                    SupportedButtonIndices = web.SupportedButtonIndices,
                    InputDeviceType = InputDeviceType.Gamepad,
                    HasRumble = true
                });
                var received = AddProvenanceRow(remote);
                var excluded = AddDevice(Guid.NewGuid(), "Virtual Gamepad 1");
                excluded.DevicePath = "native://virtual-output";
                svc.RefreshDeviceList();
                vm.Devices.SelectedDevice = vm.Devices.FindByGuid(PadGuid);
                var selection = vm.Devices.SelectedDevice;
                Assert.NotNull(selection);

                var service = new RemoteAssignmentService(vm, settings, devices);
                server = new LinkAssignmentChannel(peer.FingerprintHex, () => true,
                    () => settings.RemoteLink.Trust.AllowsRemoteAssignments(peer.FingerprintHex),
                    id => id == remote.Info.PeerLocalDeviceId ? remote : null,
                    bytes => { client.Receive(bytes); return true; },
                    context => Task.FromResult(service.Handle(context)));
                client = new LinkAssignmentChannel(peer.FingerprintHex, () => true, () => true, _ => null,
                    bytes => { server.Receive(bytes); return true; }, null);

                var snapshot = await client.QueryAsync(remote.Info.PeerLocalDeviceId);
                Assert.Equal(LinkAssignmentStatus.Ok, snapshot.Status);
                Assert.Empty(SettingsManager.GetAssignedSlots(received.InstanceGuid));
                Assert.Contains(snapshot.Slots, s => s.Index == 0 && s.CanAssign);
                var assigned = await client.SetAsync(remote.Info.PeerLocalDeviceId, snapshot.Revision, 0, true);
                Assert.Equal(LinkAssignmentStatus.Ok, assigned.Status);
                Assert.Equal(new[] { 0 }, SettingsManager.GetAssignedSlots(received.InstanceGuid));
                Assert.NotNull(vm.Devices.FindByGuid(local.InstanceGuid));
                Assert.NotNull(vm.Devices.FindByGuid(received.InstanceGuid));
                Assert.True(InputService.IsAssignOfferEligible(local));
                Assert.True(InputService.IsAssignOfferEligible(received));
                Assert.Null(vm.Devices.FindByGuid(excluded.InstanceGuid));
                Assert.False(InputService.IsAssignOfferEligible(excluded));
                Assert.Same(selection, vm.Devices.SelectedDevice);

                snapshot = await client.QueryAsync(remote.Info.PeerLocalDeviceId);
                var removed = await client.SetAsync(remote.Info.PeerLocalDeviceId, snapshot.Revision, 0, false);
                Assert.Equal(LinkAssignmentStatus.Ok, removed.Status);
                Assert.Empty(SettingsManager.GetAssignedSlots(received.InstanceGuid));
                Assert.Equal(new[] { 0 }, SettingsManager.GetAssignedSlots(PadGuid));
                Assert.Same(selection, vm.Devices.SelectedDevice);
            }
            finally
            {
                client?.Close();
                server?.Close();
                SettingsService.AfterMappingSetsRefreshed = previousRefreshHook;
            }
        }

        [Fact]
        public void HiddenWebAndPeerSourcesRemainExcludedUntilTheFlagIsCleared()
        {
            var previousRefreshHook = SettingsService.AfterMappingSetsRefreshed;
            try
            {
                var (vm, svc, _) = Arrange();
                var web = AddProvenanceRow(new WebControllerDevice("hidden-" + Guid.NewGuid().ToString("N"),
                    "Virtual Gamepad 1", false, "gamepad"));
                var peer = AddProvenanceRow(new RemotePeerDevice(new RemotePeerDeviceInfo
                {
                    PeerFingerprintHex = "peer",
                    PeerLocalDeviceId = Guid.NewGuid().ToString("N"),
                    Name = "Virtual Gamepad 1 (Streaming PC)",
                    InputDeviceType = InputDeviceType.Gamepad
                }));
                web.IsHidden = true;
                peer.IsHidden = true;
                svc.RefreshDeviceList();
                Assert.Null(vm.Devices.FindByGuid(web.InstanceGuid));
                Assert.Null(vm.Devices.FindByGuid(peer.InstanceGuid));
                Assert.False(InputService.IsAssignOfferEligible(web));
                Assert.False(InputService.IsAssignOfferEligible(peer));
                Assert.NotNull(vm.Devices.FindByGuid(PadGuid));

                web.IsHidden = false;
                peer.IsHidden = false;
                svc.RefreshDeviceList();
                Assert.NotNull(vm.Devices.FindByGuid(web.InstanceGuid));
                Assert.NotNull(vm.Devices.FindByGuid(peer.InstanceGuid));
                Assert.True(InputService.IsAssignOfferEligible(web));
                Assert.True(InputService.IsAssignOfferEligible(peer));
            }
            finally { SettingsService.AfterMappingSetsRefreshed = previousRefreshHook; }
        }

        [Theory]
        [InlineData("Virtual Gamepad", "hid://controller")]
        [InlineData("ViGEm Controller", "hid://controller")]
        [InlineData("Controller", "hid://virtual-controller")]
        [InlineData("Controller", "hid://vigem-controller")]
        [InlineData("Virtual Gamepad", "web://spoofed")]
        [InlineData("Virtual Gamepad", "peer://spoofed")]
        public void UnprovenSourcesRetainTheVirtualFilterEvenWithAWebOrPeerPath(string name, string path)
        {
            var previousRefreshHook = SettingsService.AfterMappingSetsRefreshed;
            try
            {
                var (vm, svc, _) = Arrange();
                var device = AddDevice(Guid.NewGuid(), name);
                device.DevicePath = path;
                // This row has no trusted web or peer wrapper. A URI alone is not provenance.
                Assert.Null(device.Device);
                svc.RefreshDeviceList();
                Assert.Null(vm.Devices.FindByGuid(device.InstanceGuid));
                Assert.False(InputService.IsAssignOfferEligible(device));
                Assert.NotNull(vm.Devices.FindByGuid(PadGuid));

                device.IsOnline = false;
                svc.RefreshDeviceList();
                Assert.NotNull(vm.Devices.FindByGuid(device.InstanceGuid));
                Assert.False(vm.Devices.FindByGuid(device.InstanceGuid).IsOnline);
            }
            finally { SettingsService.AfterMappingSetsRefreshed = previousRefreshHook; }
        }

        private static UserDevice AddProvenanceRow(ISdlInputDevice source)
        {
            if (source is WebControllerDevice web) web.SetConnected(true);
            var row = new UserDevice();
            row.LoadFromExternalDevice(source);
            row.IsOnline = true;
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(row);
            return row;
        }
    }
}
