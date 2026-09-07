using System;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    public partial class DeviceUnassignConfigLifecycleTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void BothLegacyWritersUseBaseWhileAShiftLayerIsSelected(bool deviceWriter)
        {
            var oldHook = SettingsService.AfterMappingSetsRefreshed;
            try
            {
                var (vm, input, settings, _, _, ps) = ArrangePendingTopologyEdit();
                PrepareLegacyLayerRows(vm, ps);
                vm.Pads[1].ActiveLayerMask = "Shift1";
                Assert.Equal("Button 8", vm.Pads[1].Mappings.Single(m => m.TargetSettingName == "ButtonA").SourceDescriptor);
                ps.ButtonA = "Button 99";
                if (deviceWriter) input.GetCurrentPadSetting(1);
                else settings.UpdatePadSettingsFromViewModels();
                Assert.Equal("IButton 2", ps.ButtonA);
                Assert.Equal("65", ps.GetMappingDeadZone("ButtonA"));
                Assert.Equal("1", ps.GetMappingBidirectional("ButtonA"));
                Assert.Equal("Button 8", LayerSource("Shift1").Descriptor);
                Assert.Equal("Button 2", LayerSource("Base").Descriptor);
            }
            finally { SettingsService.AfterMappingSetsRefreshed = oldHook; }
        }

        [Theory]
        [InlineData("", false)]
        [InlineData("Button 3", false)]
        [InlineData("", true)]
        [InlineData("Button 3", true)]
        public void BaseEditsSurviveAnImmediateLayerChangeAndAnotherSlotsUnassignment(string descriptor, bool deviceWriter)
        {
            var oldHook = SettingsService.AfterMappingSetsRefreshed;
            try
            {
                var (vm, input, settings, devices, remote, ps) = ArrangePendingTopologyEdit();
                PrepareLegacyLayerRows(vm, ps);
                var row = vm.Pads[1].Mappings.Single(m => m.TargetSettingName == "ButtonA");
                row.SourceDescriptor = descriptor;
                Assert.Equal("IButton 2", ps.ButtonA);
                // No timer or explicit save separates the edit from the layer switch.
                vm.Pads[1].ActiveLayerMask = "Shift1";
                Assert.Equal(descriptor, ps.ButtonA);
                if (deviceWriter) input.GetCurrentPadSetting(1);
                else settings.UpdatePadSettingsFromViewModels();
                Assert.Equal(descriptor, ps.ButtonA);
                devices.DeviceAssignmentChanged += (_, _) => input.RefreshAfterDeviceAssignmentChange();
                Assert.True(devices.SetDeviceSlotAssignment(remote.InstanceGuid, 0, false));
                Assert.Equal(descriptor, ps.ButtonA);
                var baseRow = SettingsManager.SlotMappingSets[1].Rows.FirstOrDefault(r => r.Target == "ButtonA" && r.LayerMask == "Base");
                if (descriptor.Length == 0)
                    Assert.True(baseRow == null || baseRow.Sources.Count == 0);
                else
                    Assert.Equal(descriptor, Assert.Single(baseRow.Sources).Descriptor);
                Assert.Equal("Button 8", LayerSource("Shift1").Descriptor);

                // A fresh device on a fresh slot must still receive the ordinary Base automap.
                var web = new WebControllerDevice("base-control-" + Guid.NewGuid().ToString("N"), "Control Pad");
                web.SetConnected(true);
                var control = new UserDevice(); control.LoadFromWebDevice(web); control.IsOnline = true;
                SettingsManager.UserDevices.Items.Add(control);
                input.RefreshDeviceList();
                devices.AssignDeviceToSlot(control.InstanceGuid, 2);
                var controlPs = SettingsManager.FindSettingByInstanceGuidAndSlot(control.InstanceGuid, 2).GetPadSetting();
                Assert.False(string.IsNullOrEmpty(controlPs.ButtonA));
                var controlRow = SettingsManager.SlotMappingSets[2].Rows.Single(r => r.Target == "ButtonA" && r.LayerMask == "Base");
                Assert.Contains(controlRow.Sources, s => s.DeviceGuid == control.InstanceGuid.ToString()
                    && s.Descriptor == controlPs.ButtonA);
            }
            finally { SettingsService.AfterMappingSetsRefreshed = oldHook; }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LegacyProjectionDoesNotWriteAnOutgoingGridDuringAProfileSwap(bool deviceWriter)
        {
            var oldHook = SettingsService.AfterMappingSetsRefreshed;
            bool oldStale = InputService.VmMappingsStale;
            try
            {
                InputService.VmMappingsStale = false;
                var (vm, input, settings, _, _, ps) = ArrangePendingTopologyEdit();
                PrepareLegacyLayerRows(vm, ps);
                var incoming = new MappingSet();
                incoming.Rows.Add(new MappingRow
                {
                    Target = "ButtonA", LayerMask = "Base",
                    Sources = new() { new MappingSource { DeviceGuid = OtherGuid.ToString(), Descriptor = "Button 6" } }
                });
                SettingsManager.SlotMappingSets[1] = incoming;
                ps.ButtonA = "Button 6";
                ps.ForceOverall = "23";
                vm.Pads[1].ForceOverallGain = 75;
                InputService.VmMappingsStale = true;
                vm.Pads[1].ActiveLayerMask = "Shift1";
                if (deviceWriter) input.GetCurrentPadSetting(1);
                else settings.UpdatePadSettingsFromViewModels();
                Assert.Equal("Button 6", ps.ButtonA);
                Assert.Equal("23", ps.ForceOverall);
                Assert.Single(incoming.Rows);
                Assert.Equal("Button 6", incoming.Rows[0].Sources[0].Descriptor);

                InputService.VmMappingsStale = false;
                vm.Pads[1].ActiveLayerMask = "Base";
                vm.Pads[1].Mappings.Single(m => m.TargetSettingName == "ButtonA").SourceDescriptor = "Button 7";
                vm.Pads[1].ForceOverallGain = 42;
                if (deviceWriter) input.GetCurrentPadSetting(1);
                else settings.UpdatePadSettingsFromViewModels();
                Assert.Equal("Button 7", ps.ButtonA);
                Assert.Equal("42", ps.ForceOverall);
            }
            finally
            {
                InputService.VmMappingsStale = oldStale;
                SettingsService.AfterMappingSetsRefreshed = oldHook;
            }
        }

        [Fact]
        public void BaseMappingsPersistWithoutASelectedTuningDevice()
        {
            var oldHook = SettingsService.AfterMappingSetsRefreshed;
            try
            {
                var (vm, _, settings, _, _, ps) = ArrangePendingTopologyEdit();
                PrepareLegacyLayerRows(vm, ps);
                vm.Pads[1].ActiveLayerMask = "Shift1";
                vm.Pads[1].SelectedMappedDevice = null;
                ps.ButtonA = "Button 99";
                Assert.Null(vm.Pads[1].SelectedMappedDevice);
                settings.UpdatePadSettingsFromViewModels();
                Assert.Equal("IButton 2", ps.ButtonA);
                Assert.Equal("Button 8", LayerSource("Shift1").Descriptor);
            }
            finally { SettingsService.AfterMappingSetsRefreshed = oldHook; }
        }

        [Fact]
        public void BaseProjectionUsesStoredOwnershipAndDoesNotPromoteASecondarySource()
        {
            var oldHook = SettingsService.AfterMappingSetsRefreshed;
            try
            {
                var (vm, _, _, _, remote, ps) = ArrangePendingTopologyEdit();
                var remotePs = Assign(remote.InstanceGuid, 1).GetPadSetting();
                var ms = SettingsManager.SlotMappingSets[1];
                ms.Rows.Clear();
                ms.Rows.Add(new MappingRow
                {
                    Target = "ButtonA", LayerMask = "Base", CombineMode = "Custom", CombineExpression = "s[1]",
                    Sources = new() { new MappingSource { Descriptor = "" },
                        new MappingSource { DeviceGuid = OtherGuid.ToString(), Descriptor = "Button 4" } }
                });
                ms.Rows.Add(new MappingRow
                {
                    Target = "ButtonB", LayerMask = "Base",
                    Sources = new() { new MappingSource { DeviceGuid = remote.InstanceGuid.ToString(), Descriptor = "Button 5" } }
                });
                ps.ButtonA = "Button 99";
                LegacyBaseMappingProjection.Write(vm.Pads[1], OtherGuid);
                Assert.Equal("", ps.ButtonA);
                Assert.Equal("", ps.ButtonB);
                Assert.Equal("Button 5", remotePs.ButtonB);
                Assert.Equal(2, ms.Rows[0].Sources.Count);
                Assert.Equal("", ms.Rows[0].Sources[0].Descriptor);
                Assert.Equal("Button 4", ms.Rows[0].Sources[1].Descriptor);
            }
            finally { SettingsService.AfterMappingSetsRefreshed = oldHook; }
        }

        [Theory]
        [InlineData("IR Pointer X", false)]
        [InlineData("IR Pointer Y", false)]
        [InlineData("IR Brightness", false)]
        [InlineData("IR Offscreen", false)]
        [InlineData("IR Pointer X", true)]
        [InlineData("IR Pointer Y", true)]
        [InlineData("IR Brightness", true)]
        [InlineData("IR Offscreen", true)]
        public void LegacyWritersKeepPrefixExemptNamesAndTheirStoredFlags(string descriptor, bool deviceWriter)
        {
            var oldHook = SettingsService.AfterMappingSetsRefreshed;
            try
            {
                var (vm, input, settings, _, _, ps) = ArrangePendingTopologyEdit();
                PrepareLegacyLayerRows(vm, ps);
                var primary = LayerSource("Base");
                primary.Descriptor = descriptor;
                primary.Invert = true;
                primary.HalfAxis = true;
                SettingsManager.SlotMappingSets[1].Rows.Add(new MappingRow
                {
                    Target = "ButtonB", LayerMask = "Base",
                    Sources = new() { new MappingSource { DeviceGuid = OtherGuid.ToString(),
                        Descriptor = "Axis 0", Invert = true, HalfAxis = true } }
                });
                InputService.RefreshMappingsToViewModel(vm.Pads[1]);
                vm.Pads[1].ActiveLayerMask = "Shift1";
                ps.ButtonA = "IIR Pointer X";
                if (deviceWriter) input.GetCurrentPadSetting(1);
                else settings.UpdatePadSettingsFromViewModels();
                Assert.Equal(descriptor, ps.ButtonA);
                Assert.Equal("IHAxis 0", ps.ButtonB);
                var stored = LayerSource("Base");
                Assert.Equal(descriptor, stored.Descriptor);
                Assert.True(stored.Invert);
                Assert.True(stored.HalfAxis);
            }
            finally { SettingsService.AfterMappingSetsRefreshed = oldHook; }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LegacyWritersHonorCustomBipolarPairSuppression(bool deviceWriter)
        {
            var oldHook = SettingsService.AfterMappingSetsRefreshed;
            try
            {
                var (vm, input, settings, _, _, ps) = ArrangePendingTopologyEdit();
                var row = new MappingRow
                {
                    Target = "LeftThumbAxisX", LayerMask = "Base", CombineMode = "Custom",
                    CombineExpression = "a+b", SuppressBipolarPair = true,
                    Sources = new()
                    {
                        new MappingSource { DeviceGuid = OtherGuid.ToString(), Descriptor = "Button 4" },
                        new MappingSource { DeviceGuid = OtherGuid.ToString(), Descriptor = "Button 5", Invert = true }
                    }
                };
                SettingsManager.SlotMappingSets[1].Rows.Add(row);
                InputService.RefreshMappingsToViewModel(vm.Pads[1]);
                if (deviceWriter) input.GetCurrentPadSetting(1);
                else settings.UpdatePadSettingsFromViewModels();
                Assert.Equal("Button 4", ps.LeftThumbAxisX);
                Assert.Equal("", ps.LeftThumbAxisXNeg);
                Assert.True(row.SuppressBipolarPair);
                Assert.Equal(2, row.Sources.Count);

                row.SuppressBipolarPair = false;
                InputService.RefreshMappingsToViewModel(vm.Pads[1]);
                if (deviceWriter) input.GetCurrentPadSetting(1);
                else settings.UpdatePadSettingsFromViewModels();
                Assert.Equal("Button 5", ps.LeftThumbAxisXNeg);
                Assert.False(row.SuppressBipolarPair);
            }
            finally { SettingsService.AfterMappingSetsRefreshed = oldHook; }
        }

        [Theory]
        [InlineData(0, "", false)]
        [InlineData(50, "", false)]
        [InlineData(0, "", true)]
        [InlineData(50, "", true)]
        public void LegacyWritersPreserveUnsetAndDefaultDeadzones(int deadzone, string expected, bool deviceWriter)
        {
            var oldHook = SettingsService.AfterMappingSetsRefreshed;
            try
            {
                var (vm, input, settings, _, _, ps) = ArrangePendingTopologyEdit();
                PrepareLegacyLayerRows(vm, ps);
                vm.Pads[1].ActiveLayerMask = "Shift1";
                LayerSource("Base").DeadZone = deadzone;
                ps.SetMappingDeadZone("ButtonA", "99");
                if (deviceWriter) input.GetCurrentPadSetting(1);
                else settings.UpdatePadSettingsFromViewModels();
                Assert.Equal(expected, ps.GetMappingDeadZone("ButtonA") ?? "");
                Assert.Equal(deadzone, LayerSource("Base").DeadZone);
                Assert.Equal("IButton 2", ps.ButtonA);
            }
            finally { SettingsService.AfterMappingSetsRefreshed = oldHook; }
        }

        [Fact]
        public void ExplicitOwnersOutsideTheSlotStayInTheDomainWithoutBleedingIntoLegacyFields()
        {
            var oldHook = SettingsService.AfterMappingSetsRefreshed;
            try
            {
                var (vm, _, _, _, remote, ps) = ArrangePendingTopologyEdit();
                var rows = SettingsManager.SlotMappingSets[1].Rows;
                rows.Clear();
                rows.Add(new MappingRow
                {
                    Target = "ButtonA", LayerMask = "Base",
                    Sources = new() { new MappingSource { DeviceGuid = remote.InstanceGuid.ToString(), Descriptor = "Button 4" } }
                });
                rows.Add(new MappingRow
                {
                    Target = "ButtonB", LayerMask = "Base",
                    Sources = new() { new MappingSource { DeviceGuid = "", Descriptor = "Button 3" } }
                });
                Assert.Equal(new[] { 0 }, SettingsManager.GetAssignedSlots(remote.InstanceGuid));
                ps.ButtonA = "Button 99";
                LegacyBaseMappingProjection.Write(vm.Pads[1], OtherGuid);
                Assert.Equal("", ps.ButtonA);
                Assert.Equal("Button 3", ps.ButtonB);
                Assert.Equal(remote.InstanceGuid.ToString(), rows[0].Sources[0].DeviceGuid);
                Assert.Equal("Button 4", rows[0].Sources[0].Descriptor);
            }
            finally { SettingsService.AfterMappingSetsRefreshed = oldHook; }
        }

        [Fact]
        public void ActualProfileApplyDoesNotFlushTheOutgoingShiftIntoTheIncomingProfile()
        {
            var oldHook = SettingsService.AfterMappingSetsRefreshed;
            try
            {
                var (vm, input, _, _, _, ps) = ArrangePendingTopologyEdit();
                PrepareLegacyLayerRows(vm, ps);
                vm.Pads[1].ActiveLayerMask = "Shift1";
                Assert.Equal("Button 8", vm.Pads[1].Mappings.Single(m => m.TargetSettingName == "ButtonA").SourceDescriptor);
                var incoming = input.SnapshotCurrentProfile();
                var replacement = ps.CloneDeep();
                replacement.ButtonA = "Button 6";
                replacement.ForceOverall = "23";
                replacement.UpdateChecksum();
                var entry = incoming.Entries.Single(e => e.MapTo == 1 && e.InstanceGuid == OtherGuid);
                entry.PadSettingChecksum = replacement.PadSettingChecksum;
                incoming.PadSettings = incoming.PadSettings.Concat(new[] { replacement }).ToArray();
                incoming.SlotMappingSets[1] = new MappingSet();
                incoming.SlotMappingSets[1].Rows.Add(new MappingRow
                {
                    Target = "ButtonA", LayerMask = "Base",
                    Sources = new() { new MappingSource { DeviceGuid = OtherGuid.ToString(), Descriptor = "Button 6" } }
                });
                input.ApplyProfile(incoming);
                Assert.Equal("Base", vm.Pads[1].ActiveLayerMask);
                Assert.False(InputService.VmMappingsStale);
                Assert.Equal("Button 6", LayerSource("Base").Descriptor);
                Assert.Equal("Button 6", SettingsManager.FindSettingByInstanceGuidAndSlot(OtherGuid, 1).GetPadSetting().ButtonA);
                Assert.Equal(23, vm.Pads[1].ForceOverallGain);
            }
            finally { SettingsService.AfterMappingSetsRefreshed = oldHook; }
        }

        private static void PrepareLegacyLayerRows(MainViewModel vm, PadSetting ps)
        {
            var ms = SettingsManager.SlotMappingSets[1];
            var primary = ms.Rows.Single(r => r.Target == "ButtonA").Sources[0];
            primary.Descriptor = "Button 2"; primary.Invert = true;
            primary.DeadZone = 65; primary.Bidirectional = true;
            ms.ShiftActivators.Add(new ShiftActivator { LayerMask = "Shift1", LayerName = "Shift 1" });
            ms.Rows.Add(new MappingRow
            {
                Target = "ButtonA", LayerMask = "Shift1",
                Sources = new() { new MappingSource { DeviceGuid = OtherGuid.ToString(), Descriptor = "Button 8" } }
            });
            vm.Pads[1].RebuildLayerTabs(ms.ShiftActivators);
            InputService.RefreshMappingsToViewModel(vm.Pads[1]);
            ps.ButtonA = "IButton 2";
            Assert.False(ms.Authoritative);
            Assert.Contains(vm.Pads[1].LayerTabs, t => t.LayerMask == "Shift1");
        }

        private static MappingSource LayerSource(string mask)
            => SettingsManager.SlotMappingSets[1].Rows.Single(r => r.Target == "ButtonA" && r.LayerMask == mask).Sources[0];
    }
}
