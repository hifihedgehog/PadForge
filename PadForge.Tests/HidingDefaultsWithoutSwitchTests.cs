using System;
using System.Collections.Generic;
using System.IO;
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
    /// Hide from Games is never turned on for a device whose row has no
    /// switch for it. The Test Controller, a replay, a program's device and
    /// a web controller type as a gamepad, and the assign turned HidHide on
    /// for them with HidHide installed. The tick hid nothing, the row offered
    /// no way to clear it, and it held the HidHide uninstall back.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class HidingDefaultsWithoutSwitchTests : IDisposable
    {
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly List<ProfileData> _profiles = SettingsManager.Profiles;
        private readonly string _active = SettingsManager.ActiveProfileId;
        private readonly ProfileData _pendingDefault = SettingsManager.PendingDefaultSnapshot;
        private readonly bool[] _created = SettingsManager.SlotCreated;
        private readonly bool[] _enabled = SettingsManager.SlotEnabled;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly List<string> _files = new();

        public void Dispose()
        {
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.Profiles = _profiles;
            SettingsManager.ActiveProfileId = _active;
            SettingsManager.PendingDefaultSnapshot = _pendingDefault;
            SettingsManager.SlotCreated = _created;
            SettingsManager.SlotEnabled = _enabled;
            SettingsManager.SlotMappingSets = _sets;
            foreach (string file in _files)
                try { File.Delete(file); } catch { }
        }

        private const string RealPad = @"\\?\HID#VID_054C&PID_0CE6&MI_03#7&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        [Theory]
        [InlineData("program://test-controller")]
        [InlineData("program://replay/1")]
        [InlineData("web://client-1")]
        public void AnAssign_TurnsNoHidingOn_ForARowWithNoHideFromGamesSwitch(string path)
        {
            var ud = new UserDevice { InstanceGuid = Guid.NewGuid(), DevicePath = path, CapType = InputDeviceType.Gamepad };
            var row = new DeviceRowViewModel { DevicePath = path };
            Assert.False(row.ShowHidHideToggle);

            DeviceService.AutoEnableHidingDefaults(ud, row, () => true);

            Assert.False(ud.HidHideEnabled);
            Assert.False(row.HidHideEnabled);
        }

        [Fact]
        public void AnAssign_StillTurnsHidingOn_ForAGamepadWithTheSwitch()
        {
            var ud = new UserDevice { InstanceGuid = Guid.NewGuid(), DevicePath = RealPad, CapType = InputDeviceType.Gamepad };
            var row = new DeviceRowViewModel { DevicePath = RealPad };
            Assert.True(row.ShowHidHideToggle);

            DeviceService.AutoEnableHidingDefaults(ud, row, () => true);

            Assert.True(ud.HidHideEnabled);
            Assert.True(row.HidHideEnabled);
        }

        [Fact]
        public void TheLoad_ClearsHiding_OnASavedDeviceWhoseRowHasNoSwitch()
        {
            var (_, settings) = Arrange();
            var program = new UserDevice { InstanceGuid = Guid.NewGuid(), DevicePath = "program://test-controller", CapType = InputDeviceType.Gamepad, HidHideEnabled = true };
            var web = new UserDevice { InstanceGuid = Guid.NewGuid(), DevicePath = "web://client-1", CapType = InputDeviceType.Gamepad, HidHideEnabled = true };
            var real = new UserDevice { InstanceGuid = Guid.NewGuid(), DevicePath = RealPad, CapType = InputDeviceType.Gamepad, HidHideEnabled = true };
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                SettingsManager.UserDevices.Items.Add(program);
                SettingsManager.UserDevices.Items.Add(web);
                SettingsManager.UserDevices.Items.Add(real);
            }
            // Assigned, as the case needs: the load drops a program's device
            // that no mapping row names.
            lock (SettingsManager.UserSettings.SyncRoot)
                foreach (var ud in new[] { program, web, real })
                    SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = ud.InstanceGuid, ProductGuid = ud.InstanceGuid, MapTo = 0 });
            string path = TempFile();
            Assert.True(settings.SaveToFile(path));

            var (_, settings2) = Arrange();
            settings2.LoadFromFile(path);

            UserDevice[] loaded;
            lock (SettingsManager.UserDevices.SyncRoot) loaded = SettingsManager.UserDevices.Items.ToArray();
            Assert.False(loaded.Single(d => d.InstanceGuid == program.InstanceGuid).HidHideEnabled);
            Assert.False(loaded.Single(d => d.InstanceGuid == web.InstanceGuid).HidHideEnabled);
            Assert.True(loaded.Single(d => d.InstanceGuid == real.InstanceGuid).HidHideEnabled);
        }

        private static (MainViewModel Vm, SettingsService Settings) Arrange()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.ActiveProfileId = null;
            SettingsManager.PendingDefaultSnapshot = null;
            SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
            SettingsManager.SlotEnabled = new bool[InputManager.MaxPads];
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            var vm = new MainViewModel();
            var settings = new SettingsService(vm);
            _ = new InputService(vm) { SettingsService = settings };
            return (vm, settings);
        }

        private string TempFile()
        {
            string path = Path.Combine(Path.GetTempPath(), "PadForgeHidingDefaults_" + Guid.NewGuid().ToString("N") + ".xml");
            _files.Add(path);
            return path;
        }
    }
}
