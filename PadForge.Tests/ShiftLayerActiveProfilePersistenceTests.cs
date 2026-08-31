using System;
using System.Collections.Generic;
using System.IO;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #375 (brawler14801): a shift layer authored on a NAMED
    /// profile is wiped across close and reopen, but only when that profile
    /// is still the ACTIVE profile at close. Switching to another profile
    /// first preserves it. These tests replay the reporter's flow through
    /// the real save and load pipeline: live mapping set with a
    /// ShiftActivator, a stored active profile carrying the same activator,
    /// SaveToFile, statics torn down like a process exit, LoadFromFile.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ShiftLayerActiveProfilePersistenceTests : IDisposable
    {
        private static readonly Guid Pad = new("32462c1a-3538-3ea7-4ed1-692056f86c4b");

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly List<ProfileData> _savedProfiles;
        private readonly string _savedActiveProfileId;
        private readonly ProfileData _savedPendingDefault;
        private readonly bool[] _savedCreated;
        private readonly bool[] _savedEnabled;
        private readonly MappingSet[] _savedMappingSets;
        private readonly List<int> _savedXboxOrder;

        public ShiftLayerActiveProfilePersistenceTests()
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

        private static MappingSet SetWithShiftLayer()
        {
            var ms = new MappingSet();
            ms.ShiftActivators.Add(new ShiftActivator
            {
                LayerName = "Shift 1",
                LayerMask = "Shift1",
                DeviceGuid = Pad.ToString(),
                Descriptor = "PadA",
            });
            return ms;
        }

        /// <summary>The reporter's exact failing flow: author the layer, save
        /// while the profile is ACTIVE, die, reload. The layer must be live
        /// after the restart.</summary>
        [Fact]
        public void ShiftLayer_SurvivesRestart_WithProfileActive()
        {
            string path = Path.Combine(Path.GetTempPath(), "padforge-shift-active-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                var (vm, svc, ss) = Arrange();

                // The reporter's minimal path through the real lanes: the
                // default profile EMPTY (no slots), create an empty named
                // profile, LOAD it, then author the slot, the pad, and the
                // shift layer while it is active, then save (their step 12).
                Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
                Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
                var profile = svc.CreateEmptyProfile("New Profile", "");
                svc.LoadProfile(profile.Id);
                SettingsManager.SlotCreated[0] = true;
                SettingsManager.SlotEnabled[0] = true;
                AddAssignedPad();
                SettingsManager.SlotMappingSets[0] = SetWithShiftLayer();

                ss.SaveToFile(path);

                string xml = File.ReadAllText(path);
                int storedActivators = xml.Split(new[] { "ShiftActivator" }, StringSplitOptions.None).Length - 1;
                Assert.True(storedActivators > 0,
                    "the saved XML carries no ShiftActivator element at all");

                // Process death and restart.
                var (vm2, svc2, ss2) = Arrange();
                ss2.LoadFromFile(path);

                var live = SettingsManager.SlotMappingSets[0];
                Assert.True(live != null && live.ShiftActivators != null && live.ShiftActivators.Count == 1,
                    $"live slot 0 after restart-with-active: set={(live == null ? "null" : "present")} "
                    + $"activators={(live?.ShiftActivators?.Count ?? -1)} "
                    + $"(XML held {storedActivators} ShiftActivator elements)");

                // The stored profile keeps its copy too.
                var reloaded = SettingsManager.Profiles.Find(p => p.Id == profile.Id);
                Assert.NotNull(reloaded);
                Assert.True(reloaded.SlotMappingSets != null
                    && reloaded.SlotMappingSets.Length > 0
                    && reloaded.SlotMappingSets[0] != null
                    && reloaded.SlotMappingSets[0].ShiftActivators.Count == 1,
                    "the STORED profile lost its ShiftActivator across the round trip");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        /// <summary>The ghost guard still guards after the reorder: a set
        /// authored for a slot that NO topology owns (neither the default's
        /// nor the active profile's) is still masked on load, with an
        /// active profile present. This is the hazard
        /// MaskMappingSetsForUncreatedSlots exists for, and moving it after
        /// LoadProfiles must not weaken it.</summary>
        [Fact]
        public void GhostSetOnUnownedSlot_IsStillMasked()
        {
            string path = Path.Combine(Path.GetTempPath(), "padforge-shift-ghost-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                var (vm, svc, ss) = Arrange();
                AddAssignedPad();
                SettingsManager.SlotMappingSets[0] = SetWithShiftLayer();
                // A ghost: authored content on a slot nobody owns.
                SettingsManager.SlotMappingSets[5] = SetWithShiftLayer();
                SettingsManager.SlotCreated[5] = false;

                var profile = svc.CreateSnapshotProfile("New Profile", "");
                // The snapshot cloned the ghost too; a real pre-DeleteSlot
                // save carries it only at the top level.
                profile.SlotMappingSets[5] = null;
                SettingsManager.ActiveProfileId = profile.Id;

                ss.SaveToFile(path);

                var (vm2, svc2, ss2) = Arrange();
                ss2.LoadFromFile(path);

                Assert.True(SettingsManager.SlotMappingSets[0]?.ShiftActivators?.Count == 1,
                    "the owned slot's layer must survive");
                var ghost = SettingsManager.SlotMappingSets[5];
                Assert.True(ghost == null || !ghost.HasAuthoredContent,
                    "the unowned slot's ghost set must stay masked");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        /// <summary>Env-gated fixture generator for live-bench reproduction:
        /// writes the post-step-12 state (active named profile carrying a
        /// shift layer) to the path in PADFORGE_SHIFT_FIXTURE. Skipped in
        /// normal runs.</summary>
        [Fact]
        public void ExportFixtureForLiveBench()
        {
            string outPath = Environment.GetEnvironmentVariable("PADFORGE_SHIFT_FIXTURE");
            if (string.IsNullOrEmpty(outPath)) return;

            var (vm, svc, ss) = Arrange();

            // The reporter's minimal path, through the real lanes: default
            // profile EMPTY (no slots), create an empty named profile, LOAD
            // it (captures the default snapshot the real flow captures),
            // then author the slot, the pad, and the shift layer while the
            // profile is active, then save.
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);

            var profile = svc.CreateEmptyProfile("New Profile", "");
            svc.LoadProfile(profile.Id);

            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotEnabled[0] = true;
            AddAssignedPad();
            SettingsManager.SlotMappingSets[0] = SetWithShiftLayer();

            ss.SaveToFile(outPath);
        }

        /// <summary>The reporter's working control: same authoring, but the
        /// DEFAULT profile is active at close. The layer must survive inside
        /// the stored named profile.</summary>
        [Fact]
        public void ShiftLayer_SurvivesRestart_WithDefaultActive()
        {
            string path = Path.Combine(Path.GetTempPath(), "padforge-shift-default-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                var (vm, svc, ss) = Arrange();
                AddAssignedPad();
                SettingsManager.SlotMappingSets[0] = SetWithShiftLayer();

                var profile = svc.CreateSnapshotProfile("New Profile", "");
                SettingsManager.ActiveProfileId = null; // default active at close

                ss.SaveToFile(path);

                var (vm2, svc2, ss2) = Arrange();
                ss2.LoadFromFile(path);

                var reloaded = SettingsManager.Profiles.Find(p => p.Id == profile.Id);
                Assert.NotNull(reloaded);
                Assert.True(reloaded.SlotMappingSets != null
                    && reloaded.SlotMappingSets.Length > 0
                    && reloaded.SlotMappingSets[0] != null
                    && reloaded.SlotMappingSets[0].ShiftActivators.Count == 1,
                    "the stored profile lost its ShiftActivator even on the reporter's WORKING path");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}
