using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Owner report 2026-08-02: deleting a virtual controller and creating
    /// the same type at the same index resurrected the deleted VC's
    /// mappings (visibly the Any-Device rows, which need no assigned
    /// device to hydrate or fire). ResetAllSettings scrubbed the slot's
    /// MappingSet one field at a time (menus, rumble-audio, SOCD, shift
    /// activators) and never dropped the mapping ROWS, and neither the
    /// startup load nor LoadMacros gated persisted state on SlotCreated,
    /// so a pre-fix save's ghost also came back through the XML.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class SlotDeleteGhostMappingTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly List<ProfileData> _savedProfiles;
        private readonly bool[] _savedCreated;
        private readonly bool[] _savedEnabled;
        private readonly MappingSet[] _savedMappingSets;
        private readonly List<int> _savedXboxOrder;
        private readonly List<int> _savedKbmOrder;

        public SlotDeleteGhostMappingTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
            _savedProfiles = SettingsManager.Profiles;
            _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            _savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            _savedMappingSets = SettingsManager.SlotMappingSets;
            _savedXboxOrder = SettingsManager.XboxSlotOrder;
            _savedKbmOrder = SettingsManager.KeyboardMouseSlotOrder;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.Profiles = _savedProfiles;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
            Array.Copy(_savedEnabled, SettingsManager.SlotEnabled, _savedEnabled.Length);
            SettingsManager.SlotMappingSets = _savedMappingSets;
            SettingsManager.XboxSlotOrder = _savedXboxOrder;
            SettingsManager.KeyboardMouseSlotOrder = _savedKbmOrder;
        }

        private static (MainViewModel vm, SettingsService ss, DeviceService dev) Arrange()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.Profiles = new List<ProfileData>();
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SettingsManager.XboxSlotOrder = new List<int>();
            SettingsManager.PlayStationSlotOrder = new List<int>();
            SettingsManager.ExtendedSlotOrder = new List<int>();
            SettingsManager.KeyboardMouseSlotOrder = new List<int>();
            SettingsManager.MidiSlotOrder = new List<int>();

            var vm = new MainViewModel();
            var ss = new SettingsService(vm);
            var dev = new DeviceService(vm, ss);
            return (vm, ss, dev);
        }

        /// <summary>An authored set carrying one member of every family the
        /// old per-field scrub handled, plus the rows it missed.</summary>
        private static MappingSet AuthoredSet()
        {
            var ms = new MappingSet { Authoritative = true };
            ms.Rows.Add(new MappingRow
            {
                Target = "Button 0",
                Sources = new List<MappingSource>
                {
                    // The ghost the owner saw: empty guid = Any Device.
                    new MappingSource { DeviceGuid = "", Descriptor = "Touchpad 0 Finger 0 X" },
                },
            });
            ms.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "",
                Descriptor = "Button 5",
                LayerMask = "Quiet",
            });
            ms.Menus.Add(new PadForge.Engine.Menus.MenuDefinitionEntry { MenuId = 1 });
            ms.SocdMode = "Neutral";
            return ms;
        }

        [Fact]
        public void DeleteSlot_DropsTheSlotsEntireMappingSet()
        {
            var (vm, _, dev) = Arrange();
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotEnabled[0] = true;
            SettingsManager.KeyboardMouseSlotOrder.Add(0);
            vm.Pads[0].OutputType = PadForge.Engine.VirtualControllerType.KeyboardMouse;
            SettingsManager.SlotMappingSets[0] = AuthoredSet();

            dev.DeleteSlot(0);

            var after = SettingsManager.SlotMappingSets[0];
            // A fresh empty set, not the scrubbed old one: the per-field
            // scrub trailed the structure five times (menus, rumble,
            // SOCD, shift layers, and finally the rows themselves).
            Assert.NotNull(after);
            Assert.False(after.HasAuthoredContent,
                "the deleted slot's MappingSet still has authored content; " +
                "the next VC created at this index inherits it as ghost mappings");
            Assert.Empty(after.Rows);
        }

        [Fact]
        public void DeleteSlot_LeavesOtherSlotsSetsAlone()
        {
            var (vm, _, dev) = Arrange();
            for (int i = 0; i < 2; i++)
            {
                SettingsManager.SlotCreated[i] = true;
                SettingsManager.SlotEnabled[i] = true;
                SettingsManager.XboxSlotOrder.Add(i);
                SettingsManager.SlotMappingSets[i] = AuthoredSet();
            }

            dev.DeleteSlot(1);

            Assert.True(SettingsManager.SlotMappingSets[0].HasAuthoredContent,
                "deleting slot 1 must not touch slot 0's set");
            Assert.False(SettingsManager.SlotMappingSets[1].HasAuthoredContent);
        }

        [Fact]
        public void LoadMask_ReplacesAuthoredSetsOnUncreatedSlotsOnly()
        {
            Arrange();
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotMappingSets[0] = AuthoredSet();
            // Slot 3: deleted by a pre-fix build, set still in the XML.
            SettingsManager.SlotCreated[3] = false;
            SettingsManager.SlotMappingSets[3] = AuthoredSet();

            SettingsService.MaskMappingSetsForUncreatedSlots();

            Assert.True(SettingsManager.SlotMappingSets[0].HasAuthoredContent,
                "the created slot's authored set must survive the mask");
            Assert.False(SettingsManager.SlotMappingSets[3].HasAuthoredContent,
                "an uncreated slot's persisted set is a ghost and must be masked at load");
        }

        [Fact]
        public void LoadMacros_SkipsMacrosForUncreatedSlots()
        {
            var (vm, ss, _) = Arrange();
            SettingsManager.SlotCreated[0] = true;

            ss.LoadMacros(new[]
            {
                new MacroData { PadIndex = 0, Name = "kept" },
                new MacroData { PadIndex = 5, Name = "ghost" },
            });

            Assert.Single(vm.Pads[0].Macros);
            Assert.Empty(vm.Pads[5].Macros);
        }
    }
}
