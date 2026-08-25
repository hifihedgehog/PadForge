using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Owner report 2026-08-25: unassigning a DualSense left its crossfeed,
    /// limiter and parametric EQ in the slot's per-device config dictionary,
    /// and assigning it again showed them as set. The dictionary is keyed by
    /// device guid and seeded by GetOrAdd from the slot's MappedDevices, and
    /// only DeleteSlot ever removed entries. The rule now: an assigned
    /// device's config is per-assignment state. Unassign drops it, reassign
    /// starts from defaults, and an assigned device that is merely offline
    /// keeps it.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class DeviceUnassignConfigLifecycleTests : IDisposable
    {
        private static readonly Guid PadGuid = new("33333333-3333-3333-3333-333333333333");
        private static readonly Guid OtherGuid = new("44444444-4444-4444-4444-444444444444");

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly List<ProfileData> _savedProfiles;
        private readonly string _savedActiveProfileId;
        private readonly bool[] _savedCreated;
        private readonly bool[] _savedEnabled;
        private readonly MappingSet[] _savedMappingSets;
        private readonly List<int> _savedXboxOrder;
        private readonly List<int> _savedPsOrder;
        private readonly List<int> _savedExtOrder;
        private readonly List<int> _savedKbmOrder;
        private readonly List<int> _savedMidiOrder;

        public DeviceUnassignConfigLifecycleTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
            _savedProfiles = SettingsManager.Profiles;
            _savedActiveProfileId = SettingsManager.ActiveProfileId;
            _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            _savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            _savedMappingSets = SettingsManager.SlotMappingSets;
            _savedXboxOrder = SettingsManager.XboxSlotOrder;
            _savedPsOrder = SettingsManager.PlayStationSlotOrder;
            _savedExtOrder = SettingsManager.ExtendedSlotOrder;
            _savedKbmOrder = SettingsManager.KeyboardMouseSlotOrder;
            _savedMidiOrder = SettingsManager.MidiSlotOrder;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.Profiles = _savedProfiles;
            SettingsManager.ActiveProfileId = _savedActiveProfileId;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
            Array.Copy(_savedEnabled, SettingsManager.SlotEnabled, _savedEnabled.Length);
            SettingsManager.SlotMappingSets = _savedMappingSets;
            SettingsManager.XboxSlotOrder = _savedXboxOrder;
            SettingsManager.PlayStationSlotOrder = _savedPsOrder;
            SettingsManager.ExtendedSlotOrder = _savedExtOrder;
            SettingsManager.KeyboardMouseSlotOrder = _savedKbmOrder;
            SettingsManager.MidiSlotOrder = _savedMidiOrder;
        }

        private static UserDevice AddDevice(Guid guid, string name)
        {
            var ud = new UserDevice
            {
                InstanceGuid = guid,
                ProductName = name,
                CapType = InputDeviceType.Gamepad,
                CapAxeCount = 6,
                CapButtonCount = 11,
                CapPovCount = 1,
                IsOnline = true,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            return ud;
        }

        private static UserSetting Assign(Guid guid, int slot)
        {
            var us = new UserSetting { InstanceGuid = guid, MapTo = slot };
            us.SetPadSetting(new PadSetting());
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(us);
            return us;
        }

        private static (MainViewModel vm, InputService svc, DeviceService dev) Arrange()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.ActiveProfileId = null;
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotEnabled[0] = true;
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SettingsManager.XboxSlotOrder = new List<int> { 0 };
            SettingsManager.PlayStationSlotOrder = new List<int>();
            SettingsManager.ExtendedSlotOrder = new List<int>();
            SettingsManager.KeyboardMouseSlotOrder = new List<int>();
            SettingsManager.MidiSlotOrder = new List<int>();

            AddDevice(PadGuid, "DualSense Wireless Controller");
            Assign(PadGuid, 0);

            var vm = new MainViewModel();
            var svc = new InputService(vm);
            var ss = new SettingsService(vm);
            var dev = new DeviceService(vm, ss);
            svc.RefreshDeviceList();
            return (vm, svc, dev);
        }

        /// <summary>The three settings from the report, all non-default.</summary>
        private static DeviceSlotConfig Customize(PadViewModel pad, Guid guid)
        {
            var cfg = pad.GetOrCreateDeviceConfig(guid);
            cfg.AudioEqEnabled = true;
            cfg.AudioCrossfeedLevel = 2;
            cfg.AudioLimiterCeiling = 50;
            return cfg;
        }

        [Fact]
        public void Unassign_DropsTheDevicesConfigAtOnce()
        {
            var (vm, svc, dev) = Arrange();
            var pad = vm.Pads[0];
            Assert.True(pad.PerDeviceSlotConfigs.ContainsKey(PadGuid), "arrange: the seam seeded the config");
            Customize(pad, PadGuid);

            dev.UnassignDevice(PadGuid);

            // Gone the moment the call returns, before any refresh pass.
            Assert.False(pad.PerDeviceSlotConfigs.ContainsKey(PadGuid),
                "unassign left the device's crossfeed / limiter / EQ config in the slot");
            svc.UpdatePadDeviceInfo();
            Assert.False(pad.PerDeviceSlotConfigs.ContainsKey(PadGuid));
        }

        [Fact]
        public void Reassign_StartsFromDefaults()
        {
            var (vm, svc, dev) = Arrange();
            var pad = vm.Pads[0];
            var old = Customize(pad, PadGuid);

            dev.UnassignDevice(PadGuid);
            svc.RefreshDeviceList();
            dev.AssignDeviceToSlot(PadGuid, 0);
            Assert.Contains(0, SettingsManager.GetAssignedSlots(PadGuid));
            svc.UpdatePadDeviceInfo();

            Assert.True(pad.PerDeviceSlotConfigs.TryGetValue(PadGuid, out var fresh));
            Assert.NotSame(old, fresh);
            Assert.False(fresh.AudioEqEnabled, "the parametric EQ came back on after a reassign");
            Assert.Equal(0, fresh.AudioCrossfeedLevel);
            Assert.Equal(98, fresh.AudioLimiterCeiling);
        }

        /// <summary>The seam covers every path that retires an assignment,
        /// including the ones DeviceService never sees (profile switch,
        /// Workshop import): a UserSetting that simply leaves the slot is
        /// enough. Same-window positive controls: a device still assigned
        /// keeps its customization through the same pass, and being
        /// offline does not count as leaving.</summary>
        [Fact]
        public void RefreshPass_PrunesToAssignedDevices_KeepsAssignedAndOffline()
        {
            var (vm, svc, _) = Arrange();
            var pad = vm.Pads[0];
            var other = AddDevice(OtherGuid, "Xbox Series X controller");
            Assign(OtherGuid, 0);
            svc.UpdatePadDeviceInfo();
            var padCfg = Customize(pad, PadGuid);
            var otherCfg = Customize(pad, OtherGuid);

            // The first device leaves the slot by data alone.
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.RemoveAll(u => u.InstanceGuid == PadGuid);
            // The second stays assigned but goes offline.
            other.IsOnline = false;

            svc.UpdatePadDeviceInfo();

            Assert.False(pad.PerDeviceSlotConfigs.ContainsKey(PadGuid), "departed device kept its config");
            Assert.True(pad.PerDeviceSlotConfigs.TryGetValue(OtherGuid, out var kept));
            Assert.Same(otherCfg, kept);
            Assert.True(kept.AudioEqEnabled, "an offline but assigned device lost its config");
            Assert.NotSame(padCfg, kept);
        }

        [Fact]
        public void LastDeviceLeaves_SlotHoldsNoConfigs()
        {
            var (vm, svc, _) = Arrange();
            var pad = vm.Pads[0];
            Customize(pad, PadGuid);

            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Clear();
            svc.UpdatePadDeviceInfo();

            Assert.DoesNotContain(pad.PerDeviceSlotConfigs.Keys, k => k != Guid.Empty);
        }

        [Fact]
        public void RemoveDeviceConfig_RebindsTheTabOffTheDroppedInstance()
        {
            var vm = new PadViewModel(0);
            var g = Guid.NewGuid();
            var cfg = vm.GetOrCreateDeviceConfig(g);
            vm.DeviceConfig = cfg;
            cfg.AudioEqEnabled = true;

            Assert.True(vm.RemoveDeviceConfig(g));

            Assert.False(vm.PerDeviceSlotConfigs.ContainsKey(g));
            Assert.NotSame(cfg, vm.DeviceConfig);
            Assert.False(vm.DeviceConfig.AudioEqEnabled, "the tab still edits the dropped config");
            Assert.False(vm.RemoveDeviceConfig(g), "a second remove reports nothing to do");
        }

        [Fact]
        public void Prune_KeepsAnchorAndAssigned_DropsTheRest()
        {
            var vm = new PadViewModel(0);
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var keep = vm.GetOrCreateDeviceConfig(a);
            vm.GetOrCreateDeviceConfig(b);

            int removed = vm.PruneDeviceSlotConfigsToAssigned(new[] { a });

            Assert.Equal(1, removed);
            Assert.Same(keep, vm.PerDeviceSlotConfigs[a]);
            Assert.False(vm.PerDeviceSlotConfigs.ContainsKey(b));
            Assert.Equal(0, vm.PruneDeviceSlotConfigsToAssigned(new[] { a }));
        }
    }
}
