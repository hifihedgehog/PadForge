using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #467 (brawler14801): a macro added on a NAMED profile is
    /// gone after close and reopen, but only when that profile is still the
    /// ACTIVE profile at close. Switching to Default first preserves it. The
    /// macro sibling of discussion #375's shift layers. These tests replay
    /// the reporter's flow through the real lanes: an empty Default,
    /// CreateEmptyProfile, LoadProfile, the slot and pad, the Macros page's
    /// Add command, SaveToFile, statics torn down like a process exit,
    /// LoadFromFile.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MacroActiveProfilePersistenceTests : IDisposable
    {
        private static readonly Guid Pad = new("5b0c7e1a-9d43-4f2e-8a61-0c3d2e7f9a14");

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly List<ProfileData> _savedProfiles;
        private readonly string _savedActiveProfileId;
        private readonly ProfileData _savedPendingDefault;
        private readonly bool[] _savedCreated;
        private readonly bool[] _savedEnabled;
        private readonly MappingSet[] _savedMappingSets;
        private readonly List<int> _savedXboxOrder;

        public MacroActiveProfilePersistenceTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
            _savedProfiles = SettingsManager.Profiles;
            _savedActiveProfileId = SettingsManager.ActiveProfileId;
            _savedPendingDefault = SettingsManager.PendingDefaultSnapshot;
            _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            _savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            _savedMappingSets = SettingsManager.SlotMappingSets;
            _savedXboxOrder = SettingsManager.XboxSlotOrder;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.Profiles = _savedProfiles;
            SettingsManager.ActiveProfileId = _savedActiveProfileId;
            SettingsManager.PendingDefaultSnapshot = _savedPendingDefault;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
            Array.Copy(_savedEnabled, SettingsManager.SlotEnabled, _savedEnabled.Length);
            SettingsManager.SlotMappingSets = _savedMappingSets;
            SettingsManager.XboxSlotOrder = _savedXboxOrder;
        }

        private static (MainViewModel vm, InputService svc, SettingsService ss) Arrange()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.ActiveProfileId = null;
            SettingsManager.PendingDefaultSnapshot = null;
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
            SettingsManager.NintendoSlotOrder = new List<int>();
            SettingsManager.VrSlotOrder = new List<int>();

            var vm = new MainViewModel();
            var ss = new SettingsService(vm);
            var svc = new InputService(vm) { SettingsService = ss };
            return (vm, svc, ss);
        }

        private static void AddAssignedPad()
        {
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(new UserDevice
                {
                    InstanceGuid = Pad,
                    ProductGuid = Pad,
                    InstanceName = "Test Pad",
                    ProductName = "Test Pad",
                    IsOnline = true,
                    CapType = PadForge.Engine.InputDeviceType.Gamepad,
                });
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(new UserSetting
                {
                    InstanceGuid = Pad,
                    ProductGuid = Pad,
                    MapTo = 0,
                });
        }

        /// <summary>The reporter's steps 1 to 11: an empty Default, a new
        /// empty profile loaded, a slot with a pad, and Add on the Macros
        /// page, all while the named profile is active.</summary>
        private static ProfileData AuthorMacroOnActiveProfile(MainViewModel vm, InputService svc)
        {
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
            var profile = svc.CreateEmptyProfile("New Profile", "");
            svc.LoadProfile(profile.Id);
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotEnabled[0] = true;
            AddAssignedPad();
            vm.Pads[0].AddMacroCommand.Execute(null);
            Assert.Single(vm.Pads[0].Macros);
            return profile;
        }

        private static string TempPath(string tag)
            => Path.Combine(Path.GetTempPath(), $"padforge-macro-{tag}-{Guid.NewGuid():N}.xml");

        /// <summary>The reporter's failing flow (steps 18 to 21): close with
        /// the named profile still active, reopen. The macro must be live on
        /// the slot, the stored profile must keep its copy, and both must
        /// survive the save a close performs and a second reopen.</summary>
        [Fact]
        public void Macro_SurvivesRestart_WithProfileActive()
        {
            string path = TempPath("active");
            try
            {
                var (vm, svc, ss) = Arrange();
                var profile = AuthorMacroOnActiveProfile(vm, svc);
                ss.SaveToFile(path);

                string xml = File.ReadAllText(path);
                Assert.Contains("Macro 1", xml);

                // Process death and restart.
                var (vm2, svc2, ss2) = Arrange();
                ss2.LoadFromFile(path);

                Assert.Equal(profile.Id, SettingsManager.ActiveProfileId);
                Assert.True(vm2.Pads[0].Macros.Count == 1,
                    $"live slot 0 after restart-with-active holds {vm2.Pads[0].Macros.Count} macros");
                Assert.Equal("Macro 1", vm2.Pads[0].Macros[0].Name);

                var stored = SettingsManager.Profiles.Find(p => p.Id == profile.Id);
                Assert.NotNull(stored);
                Assert.True(stored.Macros != null && stored.Macros.Length == 1,
                    "the STORED profile lost its macro across the round trip");

                // The save a close performs, then a second reopen.
                ss2.SaveToFile(path);
                var (vm3, svc3, ss3) = Arrange();
                ss3.LoadFromFile(path);

                Assert.True(vm3.Pads[0].Macros.Count == 1,
                    $"live slot 0 after a second restart holds {vm3.Pads[0].Macros.Count} macros");
                var storedAgain = SettingsManager.Profiles.Find(p => p.Id == profile.Id);
                Assert.True(storedAgain?.Macros != null && storedAgain.Macros.Length == 1,
                    "the close-time save wrote the lost macro over the stored profile");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        /// <summary>The reporter's working control (steps 12 to 17): switch
        /// to Default before closing, reopen, load the named profile. The
        /// macro comes back with it.</summary>
        [Fact]
        public void Macro_SurvivesRestart_WithDefaultActive()
        {
            string path = TempPath("default");
            try
            {
                var (vm, svc, ss) = Arrange();
                var profile = AuthorMacroOnActiveProfile(vm, svc);
                svc.RevertToDefaultProfile();
                ss.SaveToFile(path);

                var (vm2, svc2, ss2) = Arrange();
                ss2.LoadFromFile(path);
                Assert.Null(SettingsManager.ActiveProfileId);

                var stored = SettingsManager.Profiles.Find(p => p.Id == profile.Id);
                Assert.True(stored?.Macros != null && stored.Macros.Length == 1,
                    "the stored profile lost its macro even on the reporter's working path");

                svc2.LoadProfile(profile.Id);
                Assert.True(vm2.Pads[0].Macros.Count == 1,
                    $"loading the profile after restart shows {vm2.Pads[0].Macros.Count} macros");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        /// <summary>The macro ghost guard still guards after the reorder: a
        /// macro on a slot that NO topology owns (neither the default's nor
        /// the active profile's) is still dropped on load with an active
        /// profile present. A save from before delete-time macro clearing
        /// carries such a macro at the top level only.</summary>
        [Fact]
        public void GhostMacroOnUnownedSlot_IsStillDropped()
        {
            string path = TempPath("ghost");
            try
            {
                var (vm, svc, ss) = Arrange();
                AddAssignedPad();
                vm.Pads[0].AddMacroCommand.Execute(null);
                vm.Pads[5].Macros.Add(new MacroItem { PadIndex = 5, Name = "Ghost" });
                SettingsManager.SlotCreated[5] = false;

                var profile = svc.CreateSnapshotProfile("New Profile", "");
                profile.Macros = profile.Macros.Where(m => m.PadIndex != 5).ToArray();
                SettingsManager.ActiveProfileId = profile.Id;

                ss.SaveToFile(path);
                Assert.Contains("Ghost", File.ReadAllText(path));

                var (vm2, svc2, ss2) = Arrange();
                ss2.LoadFromFile(path);

                Assert.True(vm2.Pads[0].Macros.Count == 1, "the owned slot's macro must survive");
                Assert.True(vm2.Pads[5].Macros.Count == 0, "the unowned slot's ghost macro must stay dropped");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}
