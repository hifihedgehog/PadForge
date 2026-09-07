using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    public partial class DeviceUnassignConfigLifecycleTests
    {
        [Theory]
        [InlineData("assign")]
        [InlineData("assign-button")]
        [InlineData("unassign-slot")]
        [InlineData("unassign-all")]
        [InlineData("remove")]
        [InlineData("create")]
        [InlineData("delete")]
        [InlineData("remote-unassign")]
        public async Task TopologyChangesPreserveAnotherSlotsPendingMappingAndTuning(string operation)
        {
            var oldHook = SettingsService.AfterMappingSetsRefreshed;
            bool oldStale = InputService.VmMappingsStale;
            bool oldReload = InputService.SuppressMappingEditPush;
            LinkAssignmentChannel client = null, server = null;
            try
            {
                InputService.VmMappingsStale = false;
                InputService.SuppressMappingEditPush = false;
                var (vm, input, settings, devices, remote, bSetting) = ArrangePendingTopologyEdit();
                var b = vm.Pads[1];
                var row = b.Mappings.Single(m => m.TargetSettingName == "ButtonA");
                row.MappingDeadZone = 70;
                b.ForceOverallGain = 75;
                settings.MarkDirty();
                Assert.Equal(50, SettingsManager.SlotMappingSets[1].Rows.Single(r => r.Target == "ButtonA").Sources[0].DeadZone);
                Assert.Equal("50", bSetting.ForceOverall);
                int refreshed = 0;
                devices.DeviceAssignmentChanged += (_, _) =>
                {
                    input.RefreshAfterDeviceAssignmentChange();
                    refreshed++;
                };
                vm.Devices.SelectedDevice = vm.Devices.FindByGuid(remote.InstanceGuid);

                switch (operation)
                {
                    case "assign": devices.AssignDeviceToSlot(remote.InstanceGuid, 2); break;
                    case "assign-button": InvokeTopologyHandler(devices, "OnAssignToSlot", 2); break;
                    case "unassign-slot": Assert.True(devices.SetDeviceSlotAssignment(remote.InstanceGuid, 0, false)); break;
                    case "unassign-all": devices.UnassignDevice(remote.InstanceGuid); break;
                    case "remove": InvokeTopologyHandler(devices, "OnRemoveDevice", remote.InstanceGuid); break;
                    case "create": Assert.Equal(2, devices.CreateSlot()); break;
                    case "delete": devices.DeleteSlot(0); break;
                    case "remote-unassign":
                        var service = new RemoteAssignmentService(vm, settings, devices);
                        server = new LinkAssignmentChannel(remote.Info.PeerFingerprintHex, () => true, () => true,
                            id => id == remote.Info.PeerLocalDeviceId ? remote : null,
                            bytes => { client.Receive(bytes); return true; },
                            context => Task.FromResult(service.Handle(context)));
                        client = new LinkAssignmentChannel(remote.Info.PeerFingerprintHex, () => true, () => true,
                            _ => null, bytes => { server.Receive(bytes); return true; }, null);
                        // The query sees the revision after the local edit. It must not need to reject
                        // an otherwise current request to preserve the pending value on another slot.
                        var snapshot = await client.QueryAsync(remote.Info.PeerLocalDeviceId);
                        Assert.Equal(LinkAssignmentStatus.Ok, snapshot.Status);
                        var reply = await client.SetAsync(remote.Info.PeerLocalDeviceId, snapshot.Revision, 0, false);
                        Assert.Equal(LinkAssignmentStatus.Ok, reply.Status);
                        Assert.Empty(SettingsManager.GetAssignedSlots(remote.InstanceGuid));
                        break;
                }

                Assert.Equal(1, refreshed);
                Assert.Equal(70, b.Mappings.Single(m => m.TargetSettingName == "ButtonA").MappingDeadZone);
                Assert.Equal(70, SettingsManager.SlotMappingSets[1].Rows.Single(r => r.Target == "ButtonA").Sources[0].DeadZone);
                Assert.Equal("70", bSetting.GetMappingDeadZone("ButtonA"));
                Assert.Equal("75", bSetting.ForceOverall);
                Assert.Equal(75, b.ForceOverallGain);
                Assert.Equal(new[] { 1 }, SettingsManager.GetAssignedSlots(OtherGuid));
            }
            finally
            {
                client?.Close(); server?.Close();
                SettingsService.AfterMappingSetsRefreshed = oldHook;
                InputService.VmMappingsStale = oldStale;
                InputService.SuppressMappingEditPush = oldReload;
            }
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void PendingFlushDoesNotWriteDuringAProfileSwapOrMappingReload(bool stale, bool reloading)
        {
            var oldHook = SettingsService.AfterMappingSetsRefreshed;
            bool oldStale = InputService.VmMappingsStale;
            bool oldReload = InputService.SuppressMappingEditPush;
            try
            {
                InputService.VmMappingsStale = false;
                InputService.SuppressMappingEditPush = false;
                var (vm, _, settings, _, _, ps) = ArrangePendingTopologyEdit();
                var row = vm.Pads[1].Mappings.Single(m => m.TargetSettingName == "ButtonA");
                row.MappingDeadZone = 70;
                vm.Pads[1].ForceOverallGain = 75;
                InputService.VmMappingsStale = stale;
                InputService.SuppressMappingEditPush = reloading;
                settings.FlushPendingDeviceEdits();
                Assert.Equal("50", ps.ForceOverall);
                Assert.Equal(50, SettingsManager.SlotMappingSets[1].Rows.Single(r => r.Target == "ButtonA").Sources[0].DeadZone);

                InputService.VmMappingsStale = false;
                InputService.SuppressMappingEditPush = false;
                settings.FlushPendingDeviceEdits();
                Assert.Equal("75", ps.ForceOverall);
                Assert.Equal(70, SettingsManager.SlotMappingSets[1].Rows.Single(r => r.Target == "ButtonA").Sources[0].DeadZone);
            }
            finally
            {
                SettingsService.AfterMappingSetsRefreshed = oldHook;
                InputService.VmMappingsStale = oldStale;
                InputService.SuppressMappingEditPush = oldReload;
            }
        }

        [Fact]
        public void PendingFlushSkipsAnUnhydratedSlotWhileFlushingAHydratedSlot()
        {
            var oldHook = SettingsService.AfterMappingSetsRefreshed;
            bool oldStale = InputService.VmMappingsStale;
            bool oldReload = InputService.SuppressMappingEditPush;
            try
            {
                InputService.VmMappingsStale = false;
                InputService.SuppressMappingEditPush = false;
                var (vm, _, settings, _, _, ps) = ArrangePendingTopologyEdit();
                var first = SettingsManager.FindSettingByInstanceGuidAndSlot(PadGuid, 0).GetPadSetting();
                vm.Pads[0].ForceOverallGain = 61;
                vm.Pads[1].ForceOverallGain = 75;
                vm.Pads[1].Mappings.Single(m => m.TargetSettingName == "ButtonA").MappingDeadZone = 70;
                vm.Pads[1].MappingsViewLoaded = false;
                settings.FlushPendingDeviceEdits();
                Assert.Equal("61", first.ForceOverall);
                Assert.Equal("50", ps.ForceOverall);
                Assert.Equal(50, SettingsManager.SlotMappingSets[1].Rows.Single(r => r.Target == "ButtonA").Sources[0].DeadZone);
            }
            finally
            {
                SettingsService.AfterMappingSetsRefreshed = oldHook;
                InputService.VmMappingsStale = oldStale;
                InputService.SuppressMappingEditPush = oldReload;
            }
        }

        private static (MainViewModel Vm, InputService Input, SettingsService Settings,
            DeviceService Devices, RemotePeerDevice Remote, PadSetting BSetting) ArrangePendingTopologyEdit()
        {
            var (vm, input, _) = Arrange();
            var settings = new SettingsService(vm);
            input.SettingsService = settings;
            var devices = new DeviceService(vm, settings);
            var peer = settings.RemoteLink.Trust.Grant(new byte[32], "Source PC", "today", true, true, true);
            var remote = new RemotePeerDevice(new RemotePeerDeviceInfo
            {
                PeerFingerprintHex = peer.FingerprintHex,
                PeerLocalDeviceId = "source-a",
                Name = "Source A",
                NumAxes = 6, NumButtons = 11, NumHats = 1,
                InputDeviceType = InputDeviceType.Gamepad
            });
            var a = new UserDevice();
            a.LoadFromExternalDevice(remote); a.IsOnline = true;
            SettingsManager.UserDevices.Items.Add(a);
            Assign(a.InstanceGuid, 0);
            AddDevice(OtherGuid, "Source B");
            SettingsManager.SlotCreated[1] = true;
            SettingsManager.SlotEnabled[1] = true;
            SettingsManager.XboxSlotOrder.Add(1);
            var bSetting = Assign(OtherGuid, 1).GetPadSetting();
            bSetting.ForceOverall = "50";
            bSetting.ButtonA = "Axis 0";
            bSetting.SetMappingDeadZone("ButtonA", "50");
            SettingsManager.SlotMappingSets[0] = new MappingSet();
            SettingsManager.SlotMappingSets[1] = new MappingSet();
            SettingsManager.SlotMappingSets[1].Rows.Add(new MappingRow
            {
                Target = "ButtonA", LayerMask = "Base",
                Sources = new() { new MappingSource { DeviceGuid = OtherGuid.ToString(), Descriptor = "Axis 0", DeadZone = 50 } }
            });
            input.RefreshDeviceList();
            InputService.RefreshMappingsToViewModel(vm.Pads[0]);
            InputService.RefreshMappingsToViewModel(vm.Pads[1]);
            Assert.True(vm.Pads[0].MappingsViewLoaded);
            Assert.True(vm.Pads[1].MappingsViewLoaded);
            Assert.Equal(OtherGuid, vm.Pads[1].SelectedMappedDevice.InstanceGuid);
            return (vm, input, settings, devices, remote, bSetting);
        }

        private static void InvokeTopologyHandler(DeviceService service, string name, object argument)
        {
            var method = typeof(DeviceService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(service, new[] { (object)null, argument });
        }
    }
}
