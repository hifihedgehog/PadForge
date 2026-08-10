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

        /// <summary>The lane the first fix missed (owner re-report, same
        /// day): OnSlotDeleted → RefreshAfterSlotReorder runs
        /// UpdatePadDeviceInfo BEFORE it rebuilds the grids, and that can
        /// fire OnSelectedDeviceChanged → PushUiExtraSourcesIntoSlotMappingSets
        /// (the documented fifth push path). At that instant the deleted
        /// pad's grid still holds the deleted VC's rows and
        /// MappingsViewLoaded is still true, so the push writes the ghost
        /// straight back into the set DeleteSlot just emptied. The writer
        /// must skip uncreated slots ITSELF (the domain-swap tattoo:
        /// guard the writer, not the callers).</summary>
        [Fact]
        public void PushMidDeleteWindow_CannotResurrectTheDeletedSlotsRows()
        {
            var (vm, ss, dev) = Arrange();
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotEnabled[0] = true;
            SettingsManager.KeyboardMouseSlotOrder.Add(0);
            var padVm = vm.Pads[0];
            padVm.OutputType = PadForge.Engine.VirtualControllerType.KeyboardMouse;
            SettingsManager.SlotMappingSets[0] = AuthoredSet();

            // Hydrate the grid the way the open Mappings tab has it: rows
            // built for the type, one carrying the Any-Device descriptor.
            padVm.RebuildMappings();
            padVm.MappingsViewLoaded = true;
            var row = padVm.Mappings.FirstOrDefault(m => !string.IsNullOrEmpty(m.TargetSettingName));
            Assert.NotNull(row);
            row.PrimarySourceDeviceGuid = "";
            row.LoadDescriptor("Touchpad 0 Finger 0 X");

            dev.DeleteSlot(0);

            // The production interleave: a push lands between DeleteSlot's
            // swap and the grid rebuild at the end of OnSlotDeleted.
            ss.PushUiExtraSourcesIntoSlotMappingSets();

            Assert.False(SettingsManager.SlotMappingSets[0].HasAuthoredContent,
                "a push in the delete window resurrected the deleted slot's rows; " +
                "the grid-to-domain writer must skip uncreated slots");
        }

        /// <summary>The writer's own gate, pinned independently of the
        /// delete flow's MappingsViewLoaded reset (which also closes the
        /// window and would otherwise mask a regression here): even with
        /// a hydrated grid claiming to be a source of truth, an uncreated
        /// slot's set must not be written.</summary>
        [Fact]
        public void Push_SkipsUncreatedSlots_EvenWithAHydratedGrid()
        {
            var (vm, ss, _) = Arrange();
            var padVm = vm.Pads[0];
            padVm.OutputType = PadForge.Engine.VirtualControllerType.KeyboardMouse;
            padVm.RebuildMappings();
            padVm.MappingsViewLoaded = true;
            var row = padVm.Mappings.FirstOrDefault(m => !string.IsNullOrEmpty(m.TargetSettingName));
            Assert.NotNull(row);
            row.PrimarySourceDeviceGuid = "";
            row.LoadDescriptor("Touchpad 0 Finger 0 X");

            SettingsManager.SlotCreated[0] = false;
            SettingsManager.SlotMappingSets[0] = new MappingSet();

            ss.PushUiExtraSourcesIntoSlotMappingSets();

            Assert.False(SettingsManager.SlotMappingSets[0].HasAuthoredContent,
                "the grid-to-domain writer wrote rows for an uncreated slot");
        }

        /// <summary>Delete resets the pad's page-navigation state: the next
        /// VC at this index must not open on the deleted VC's tab, and the
        /// dead grid must not read as hydrated (that flag is one of the two
        /// gates keeping the delete-window push from resurrecting rows).</summary>
        [Fact]
        public void DeleteSlot_ResetsTabSelectionAndGridHydration()
        {
            var (vm, _, dev) = Arrange();
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotEnabled[0] = true;
            SettingsManager.KeyboardMouseSlotOrder.Add(0);
            var padVm = vm.Pads[0];
            padVm.OutputType = PadForge.Engine.VirtualControllerType.KeyboardMouse;
            padVm.SelectedConfigTab = 2;   // Mappings
            padVm.MappingsViewLoaded = true;

            dev.DeleteSlot(0);

            Assert.Equal(0, padVm.SelectedConfigTab);
            Assert.False(padVm.MappingsViewLoaded);
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
