using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #487 (4.5.3): from a fresh PadForge.xml, a macro made on a
    /// new blank profile's virtual controller is gone after close and reopen,
    /// and an exported and re-imported copy loses it at the next restart too.
    /// These tests replay those steps through the real lanes: CreateEmptyProfile,
    /// LoadProfile, DeviceService.CreateSlot for an Xbox or PlayStation
    /// controller with its default model and no device, the Macros page's Add
    /// command, SaveToFile, statics torn down like a process exit, LoadFromFile.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MacroNewProfileRestartTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly List<ProfileData> _savedProfiles;
        private readonly string _savedActiveProfileId;
        private readonly ProfileData _savedPendingDefault;
        private readonly bool[] _savedCreatedArray;
        private readonly bool[] _savedCreated;
        private readonly bool[] _savedEnabledArray;
        private readonly bool[] _savedEnabled;
        private readonly MappingSet[] _savedMappingSets;
        private readonly Dictionary<VirtualControllerType, int[]> _savedOrders;
        private readonly string[] _savedStamps;

        public MacroNewProfileRestartTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
            _savedProfiles = SettingsManager.Profiles;
            _savedActiveProfileId = SettingsManager.ActiveProfileId;
            _savedPendingDefault = SettingsManager.PendingDefaultSnapshot;
            _savedCreatedArray = SettingsManager.SlotCreated;
            _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            _savedEnabledArray = SettingsManager.SlotEnabled;
            _savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            _savedMappingSets = SettingsManager.SlotMappingSets;
            _savedOrders = Enum.GetValues<VirtualControllerType>()
                .ToDictionary(t => t, t => SettingsManager.SlotOrders.GetOrderFor(t).ToArray());
            _savedStamps = Enumerable.Range(0, InputManager.MaxPads).Select(SettingsManager.GetWireStamp).ToArray();
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.Profiles = _savedProfiles;
            SettingsManager.ActiveProfileId = _savedActiveProfileId;
            SettingsManager.PendingDefaultSnapshot = _savedPendingDefault;
            SettingsManager.SlotCreated = _savedCreatedArray;
            Array.Copy(_savedCreated, _savedCreatedArray, _savedCreated.Length);
            SettingsManager.SlotEnabled = _savedEnabledArray;
            Array.Copy(_savedEnabled, _savedEnabledArray, _savedEnabled.Length);
            SettingsManager.SlotMappingSets = _savedMappingSets;
            foreach (var pair in _savedOrders)
            {
                var order = SettingsManager.SlotOrders.GetOrderFor(pair.Key);
                order.Clear();
                order.AddRange(pair.Value);
            }
            for (int i = 0; i < InputManager.MaxPads; i++) SettingsManager.StampNintendoWire(i, _savedStamps[i]);
        }

        /// <summary>A fresh PadForge.xml: no profiles, no controllers.</summary>
        private static (MainViewModel vm, InputService svc, SettingsService ss, DeviceService ds) Arrange()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.ActiveProfileId = null;
            SettingsManager.PendingDefaultSnapshot = null;
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            foreach (var type in Enum.GetValues<VirtualControllerType>())
                SettingsManager.SlotOrders.GetOrderFor(type).Clear();
            for (int i = 0; i < InputManager.MaxPads; i++) SettingsManager.StampNintendoWire(i, null);

            var vm = new MainViewModel();
            var ss = new SettingsService(vm);
            var svc = new InputService(vm) { SettingsService = ss };
            var ds = new DeviceService(vm, ss);
            return (vm, svc, ss, ds);
        }

        /// <summary>The reporter's steps 1 to 6: a blank new profile, switched
        /// to, a virtual controller with its default model, and a new macro on
        /// the Macros tab.</summary>
        private static (ProfileData Profile, int Slot) AuthorMacro(
            MainViewModel vm, InputService svc, DeviceService ds, VirtualControllerType type)
        {
            var profile = svc.CreateEmptyProfile("Blank", "");
            svc.LoadProfile(profile.Id);
            int slot = ds.CreateSlot(type);
            Assert.True(slot >= 0, "no slot was created");
            vm.Pads[slot].AddMacroCommand.Execute(null);
            Assert.Single(vm.Pads[slot].Macros);
            return (profile, slot);
        }

        private static string TempPath(string tag, string extension = ".xml")
            => Path.Combine(Path.GetTempPath(), $"padforge-487-{tag}-{Guid.NewGuid():N}{extension}");

        /// <summary>Step 7 and 8: close and reopen with the new profile still
        /// active. The macro is on the controller after the first reopen and
        /// after the save the next close makes.</summary>
        [Theory]
        [InlineData(VirtualControllerType.Xbox)]
        [InlineData(VirtualControllerType.PlayStation)]
        public void ANewProfilesMacro_SurvivesRestarts(VirtualControllerType type)
        {
            string path = TempPath(type.ToString());
            try
            {
                var (vm, svc, ss, ds) = Arrange();
                var (profile, slot) = AuthorMacro(vm, svc, ds, type);
                ss.SaveToFile(path);

                var (vm2, _, ss2, _) = Arrange();
                ss2.LoadFromFile(path);
                Assert.Equal(profile.Id, SettingsManager.ActiveProfileId);
                Assert.True(vm2.Pads[slot].Macros.Count == 1,
                    $"slot {slot} holds {vm2.Pads[slot].Macros.Count} macros after the first reopen");

                ss2.SaveToFile(path);
                var (vm3, _, ss3, _) = Arrange();
                ss3.LoadFromFile(path);
                Assert.True(vm3.Pads[slot].Macros.Count == 1,
                    $"slot {slot} holds {vm3.Pads[slot].Macros.Count} macros after the second reopen");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        /// <summary>The export and re-import the reporter tried: the imported
        /// profile, switched to, carries the macro, and keeps it through a
        /// close and reopen.</summary>
        [Fact]
        public void AnImportedProfilesMacro_SurvivesARestart()
        {
            string path = TempPath("import");
            string export = TempPath("export", ProfileTransfer.FileExtension);
            try
            {
                var (vm, svc, ss, ds) = Arrange();
                var (profile, slot) = AuthorMacro(vm, svc, ds, VirtualControllerType.Xbox);
                // What the Export button runs before it reads the stored profile.
                svc.SaveActiveProfileState();
                ProfileTransfer.Export(SettingsManager.Profiles.Find(p => p.Id == profile.Id), export);

                var imported = ProfileTransfer.Import(export, out _);
                Assert.NotNull(imported);
                imported.Name = "Blank (2)";
                SettingsManager.Profiles.Add(imported);
                svc.LoadProfile(imported.Id);
                Assert.Single(vm.Pads[slot].Macros);
                ss.SaveToFile(path);

                var (vm2, _, ss2, _) = Arrange();
                ss2.LoadFromFile(path);
                Assert.Equal(imported.Id, SettingsManager.ActiveProfileId);
                Assert.True(vm2.Pads[slot].Macros.Count == 1,
                    $"slot {slot} holds {vm2.Pads[slot].Macros.Count} macros after the reopen");
            }
            finally
            {
                try { File.Delete(path); } catch { }
                try { File.Delete(export); } catch { }
            }
        }
    }
}
