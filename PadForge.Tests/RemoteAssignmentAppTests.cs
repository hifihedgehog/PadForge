using System;
using System.Collections.Generic;
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
        [Fact]
        public void SettingOneAssignmentPreservesOtherSlotMappingsAndSelection()
        {
            var (vm, svc, dev) = Arrange();
            SettingsManager.SlotCreated[1] = true;
            SettingsManager.SlotEnabled[1] = true;
            SettingsManager.XboxSlotOrder.Add(1);
            Assign(PadGuid, 1);
            AddDevice(OtherGuid, "Other Controller");
            svc.RefreshDeviceList();
            vm.Devices.SelectedDevice = vm.Devices.FindByGuid(OtherGuid);
            var selected = vm.Devices.SelectedDevice;
            var other = new MappingSet { Authoritative = true };
            other.Rows.Add(new MappingRow { Target = "Button 0", Sources = new List<MappingSource>
            {
                new MappingSource { DeviceGuid = PadGuid.ToString(), Descriptor = "Button 5" }
            }});
            SettingsManager.SlotMappingSets[1] = other;
            var config = Customize(vm.Pads[1], PadGuid);
            int changes = 0;
            dev.DeviceAssignmentChanged += (_, _) => changes++;
            Assert.True(dev.SetDeviceSlotAssignment(PadGuid, 0, false));
            Assert.True(dev.SetDeviceSlotAssignment(PadGuid, 0, false));
            Assert.Equal(new[] { 1 }, SettingsManager.GetAssignedSlots(PadGuid));
            Assert.Equal(1, changes);
            Assert.Same(selected, vm.Devices.SelectedDevice);
            Assert.Same(other, SettingsManager.SlotMappingSets[1]);
            Assert.Equal("Button 5", Assert.Single(Assert.Single(other.Rows).Sources).Descriptor);
            Assert.Same(config, vm.Pads[1].PeekDeviceConfig(PadGuid));
            Assert.True(dev.SetDeviceSlotAssignment(PadGuid, 0, true));
            Assert.True(dev.SetDeviceSlotAssignment(PadGuid, 0, true));
            Assert.Equal(2, changes);
            Assert.Equal(new[] { 0, 1 }, SettingsManager.GetAssignedSlots(PadGuid));
            Assert.Same(selected, vm.Devices.SelectedDevice);
            Assert.Contains(other.Rows.SelectMany(r => r.Sources), s => s.Descriptor == "Button 5" && s.DeviceGuid == PadGuid.ToString());
        }

        [Fact]
        public async Task RemoteAssignmentsEnforceProfileRevisionAndGamepadPermission()
        {
            var (vm, svc, dev) = Arrange();
            var settings = new SettingsService(vm);
            svc.SettingsService = settings;
            var peer = settings.RemoteLink.Trust.Grant(new byte[32], "Source", "today", true, true, true);
            var info = new RemotePeerDeviceInfo
            {
                PeerFingerprintHex = peer.FingerprintHex, PeerLocalDeviceId = "browser-id",
                Name = "Browser Gamepad 1", NumAxes = 6, NumButtons = 17, NumHats = 1
            };
            var remote = new RemotePeerDevice(info);
            var device = new UserDevice();
            device.LoadFromExternalDevice(remote);
            device.IsOnline = true;
            SettingsManager.UserDevices.Items.Add(device);
            svc.RefreshDeviceList();
            var service = new RemoteAssignmentService(vm, settings, dev);
            LinkAssignmentChannel client = null;
            var server = new LinkAssignmentChannel(peer.FingerprintHex, () => true,
                () => settings.RemoteLink.Trust.AllowsRemoteAssignments(peer.FingerprintHex),
                id => id == "browser-id" ? remote : null, bytes => { client.Receive(bytes); return true; },
                context => Task.FromResult(service.Handle(context)));
            client = new(peer.FingerprintHex, () => true, () => true, _ => null,
                bytes => { server.Receive(bytes); return true; }, null);
            var snapshot = await client.QueryAsync("browser-id");
            Assert.Equal(LinkAssignmentStatus.Ok, snapshot.Status);
            settings.MarkDirty();
            Assert.Equal(LinkAssignmentStatus.Stale, (await client.SetAsync("browser-id", snapshot.Revision, 0, true)).Status);
            Assert.Empty(SettingsManager.GetAssignedSlots(device.InstanceGuid));
            snapshot = await client.QueryAsync("browser-id");
            Assert.Equal(LinkAssignmentStatus.Ok, (await client.SetAsync("browser-id", snapshot.Revision, 0, true)).Status);
            Assert.Contains(0, SettingsManager.GetAssignedSlots(device.InstanceGuid));

            snapshot = await client.QueryAsync("browser-id");
            svc.ApplyProfile(new ProfileData { Id = "other", Name = "Other" });
            svc.ApplyProfile(new ProfileData { Id = "original", Name = "Original" });
            Assert.Equal(LinkAssignmentStatus.Stale, (await client.SetAsync("browser-id", snapshot.Revision, 0, true)).Status);
            Assert.Empty(SettingsManager.GetAssignedSlots(device.InstanceGuid));
            snapshot = await client.QueryAsync("browser-id");
            Assert.Equal(LinkAssignmentStatus.Ok, (await client.SetAsync("browser-id", snapshot.Revision, 0, true)).Status);
            Assert.Contains(0, SettingsManager.GetAssignedSlots(device.InstanceGuid));

            vm.Pads[0].OutputType = VirtualControllerType.KeyboardMouse;
            snapshot = await client.QueryAsync("browser-id");
            Assert.False(snapshot.Slots[0].CanAssign);
            Assert.Equal(LinkAssignmentStatus.Ok, (await client.SetAsync("browser-id", snapshot.Revision, 0, false)).Status);
            snapshot = await client.QueryAsync("browser-id");
            Assert.Equal(LinkAssignmentStatus.Denied, (await client.SetAsync("browser-id", snapshot.Revision, 0, true)).Status);
            client.Close(); server.Close();
        }
    }
}
