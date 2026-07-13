using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Owner report 2026-07-13: after importing a Workshop profile, the
    /// mapping-row input pickers on the imported (device-less) slots listed
    /// the previous profile's concrete controller ("Xbox Series X
    /// controller") even though the slot card showed no assigned device.
    /// The UserSettings-level transition in ApplyProfile is correct (a
    /// profile with no Entries orphans every assignment), so the bleed is
    /// ViewModel display state: UpdatePadDeviceInfo's empty branch cleared
    /// MappedDevices but left SelectedMappedDevice pointing at the outgoing
    /// profile's device, and ApplyProfile's picker rebuild both (a) gated
    /// PopulateAvailableInputs on that stale selection and (b) skipped the
    /// rebuild entirely for slots with a null selection, leaving the
    /// outgoing profile's choices in AvailableInputs.
    ///
    /// These tests drive the REAL apply path (LoadProfile → ApplyProfile →
    /// UpdatePadDeviceInfo) with a materialized Workshop profile, exactly
    /// like MainWindow.AddWorkshopProfile(applyAfter: true).
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WorkshopImportPickerBleedTests : IDisposable
    {
        private static readonly Guid XboxGuid = new("22222222-2222-2222-2222-222222222222");

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly List<ProfileData> _savedProfiles;
        private readonly string _savedActiveProfileId;
        private readonly ProfileData _savedPendingDefault;
        private readonly bool[] _savedCreated;
        private readonly bool[] _savedEnabled;
        private readonly MappingSet[] _savedMappingSets;
        private readonly List<int> _savedXboxOrder;
        private readonly List<int> _savedPsOrder;
        private readonly List<int> _savedExtOrder;
        private readonly List<int> _savedKbmOrder;
        private readonly List<int> _savedMidiOrder;

        public WorkshopImportPickerBleedTests()
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
            SettingsManager.PendingDefaultSnapshot = _savedPendingDefault;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
            Array.Copy(_savedEnabled, SettingsManager.SlotEnabled, _savedEnabled.Length);
            SettingsManager.SlotMappingSets = _savedMappingSets;
            SettingsManager.XboxSlotOrder = _savedXboxOrder;
            SettingsManager.PlayStationSlotOrder = _savedPsOrder;
            SettingsManager.ExtendedSlotOrder = _savedExtOrder;
            SettingsManager.KeyboardMouseSlotOrder = _savedKbmOrder;
            SettingsManager.MidiSlotOrder = _savedMidiOrder;
        }

        /// <summary>Seeds the "owner's default profile" shape: one created
        /// Xbox slot at index 0 with a concrete gamepad assigned, then a
        /// MainViewModel + InputService pair with the pad state mirrored via
        /// UpdatePadDeviceInfo (the same call every live assignment path
        /// funnels through).</summary>
        private static (MainViewModel mainVm, InputService svc) ArrangeDefaultProfileWithXboxPad()
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

            var ud = new UserDevice
            {
                InstanceGuid = XboxGuid,
                ProductName = "Xbox Series X controller",
                CapType = InputDeviceType.Gamepad,
                CapAxeCount = 6,
                CapButtonCount = 11,
                CapPovCount = 1,
                IsOnline = true,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);

            var us = new UserSetting { InstanceGuid = XboxGuid, MapTo = 0 };
            us.SetPadSetting(new PadSetting());
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(us);

            var mainVm = new MainViewModel();
            var svc = new InputService(mainVm);
            svc.UpdatePadDeviceInfo();
            return (mainVm, svc);
        }

        /// <summary>Materializes a two-slot Workshop profile (Xbox slot 0,
        /// KbM slot 1) whose rows use abstract Gamepad descriptors and whose
        /// device assignments are deliberately empty, then registers and
        /// applies it through the same LoadProfile path
        /// MainWindow.AddWorkshopProfile(applyAfter: true) uses.</summary>
        private static ProfileData ImportAndApplyWorkshopProfile(InputService svc)
        {
            var translated = new TranslatedProfile
            {
                Name = "Community Config",
                NeedsXboxSlot = true,
                NeedsKbmSlot = true,
            };
            translated.XboxMappingSet.Rows.Add(new MappingRow
            {
                Target = "ButtonA",
                Sources = { new MappingSource { Descriptor = "Gamepad ButtonA" } },
            });
            translated.KbmMappingSet.Rows.Add(new MappingRow
            {
                Target = "KbmKey45",
                Sources = { new MappingSource { Descriptor = "Gamepad DPadUp" } },
            });

            var profile = WorkshopProfileMaterializer.Materialize(translated);
            SettingsManager.Profiles.Add(profile);
            svc.LoadProfile(profile.Id);
            return profile;
        }

        private static IEnumerable<InputChoice> AllChoices(PadViewModel pad)
            => pad.Mappings.SelectMany(m => m.AvailableInputs)
               .Concat(pad.SlotAvailableInputs);

        [Fact]
        public void WorkshopApply_DevicelessSlots_ShowNoConcreteDeviceGroups()
        {
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            var pad0 = mainVm.Pads[0];
            var pad1 = mainVm.Pads[1];

            // Same-window positive control: before the switch, the picker
            // machinery demonstrably lists the concrete pad on slot 0.
            Assert.NotNull(pad0.SelectedMappedDevice);
            Assert.Equal(XboxGuid, pad0.SelectedMappedDevice.InstanceGuid);
            Assert.Contains(AllChoices(pad0), c => string.Equals(
                c.DeviceGuid, XboxGuid.ToString(), StringComparison.OrdinalIgnoreCase));

            ImportAndApplyWorkshopProfile(svc);

            // The settings-level transition is correct: a profile with no
            // Entries fully owns assignments, so nothing stays mapped.
            Assert.Empty(pad0.MappedDevices);
            Assert.Empty(pad1.MappedDevices);

            // The slot card shows no device. The VM selection must agree.
            Assert.Null(pad0.SelectedMappedDevice);

            // The phantom itself: no mapping-row picker on either imported
            // slot may offer a concrete-device group when no device is
            // assigned to the slot.
            Assert.DoesNotContain(AllChoices(pad0), c => !string.IsNullOrEmpty(c.DeviceGuid));
            Assert.DoesNotContain(AllChoices(pad1), c => !string.IsNullOrEmpty(c.DeviceGuid));
        }

        [Fact]
        public void WorkshopApply_DevicelessSlots_OfferAbstractGamepadFamily()
        {
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            var pad0 = mainVm.Pads[0];
            var pad1 = mainVm.Pads[1];

            ImportAndApplyWorkshopProfile(svc);

            // The imported rows carry abstract "Gamepad ..." descriptors
            // with an empty DeviceGuid ("resolves on whichever pad the user
            // maps into the slot"). The device-less picker must keep those
            // rows editable: the abstract family under the "(Any device)"
            // group, never a concrete device.
            var buttonARow = pad0.Mappings.First(m => m.TargetSettingName == "ButtonA");
            var abstractChoice = buttonARow.AvailableInputs.FirstOrDefault(c =>
                c.Descriptor == "Gamepad ButtonA");
            Assert.NotNull(abstractChoice);
            Assert.True(string.IsNullOrEmpty(abstractChoice.DeviceGuid));

            // The row's saved descriptor resolves against the rebuilt list,
            // so the ComboBox renders the imported mapping. It resolves
            // into the "(Any device)" entry (empty guid), never a borrowed
            // concrete-device entry.
            Assert.NotNull(buttonARow.SelectedInput);
            Assert.Equal("Gamepad ButtonA", buttonARow.SelectedInput.Descriptor);
            Assert.True(string.IsNullOrEmpty(buttonARow.SelectedInput.DeviceGuid));

            // The KbM slot's rows are gamepad-sourced too and need the same
            // family (KbmKey45 <- Gamepad DPadUp).
            Assert.Contains(AllChoices(pad1), c =>
                c.Descriptor == "Gamepad DPadUp" && string.IsNullOrEmpty(c.DeviceGuid));
        }

        [Fact]
        public void RevertToDefault_RestoresAssignmentsAndConcretePicker()
        {
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            var pad0 = mainVm.Pads[0];

            ImportAndApplyWorkshopProfile(svc);
            svc.RevertToDefaultProfile();

            // Ordinary profile switching still owns and restores device
            // assignments: the default snapshot saved on the way out brings
            // the concrete pad back, selection and picker included.
            Assert.Single(pad0.MappedDevices);
            Assert.Equal(XboxGuid, pad0.MappedDevices[0].InstanceGuid);
            Assert.NotNull(pad0.SelectedMappedDevice);
            Assert.Equal(XboxGuid, pad0.SelectedMappedDevice.InstanceGuid);
            Assert.Contains(AllChoices(pad0), c => string.Equals(
                c.DeviceGuid, XboxGuid.ToString(), StringComparison.OrdinalIgnoreCase));

            // The "(Any device)" group is always present and leads the
            // list, concrete device groups follow.
            var buttonARow = pad0.Mappings.First(m => m.TargetSettingName == "ButtonA");
            Assert.True(buttonARow.AvailableInputs.Count > 0);
            Assert.True(string.IsNullOrEmpty(buttonARow.AvailableInputs[0].DeviceGuid));
        }

        [Fact]
        public void EmptyGuidRow_WithConcreteDeviceAssigned_ResolvesIntoAnyDeviceGroup()
        {
            // Owner symptom refinement: with the default profile's device
            // still assigned, an imported empty-guid "Gamepad ..." row
            // rendered under the concrete controller's group header because
            // the abstract family only existed as per-device entries. The
            // device-agnostic row must resolve into the "(Any device)"
            // entry even when a concrete device offers the same descriptor.
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            var pad0 = mainVm.Pads[0];

            var ms = new MappingSet();
            ms.Rows.Add(new MappingRow
            {
                Target = "ButtonA",
                Sources = { new MappingSource { Descriptor = "Gamepad ButtonA", DeviceGuid = "" } },
            });
            SettingsManager.SlotMappingSets[0] = ms;

            InputService.RefreshMappingsToViewModel(pad0);
            svc.RefreshAvailableInputsForSlot(pad0);

            var buttonARow = pad0.Mappings.First(m => m.TargetSettingName == "ButtonA");

            // The concrete device still contributes its own abstract entry
            // (its group is unchanged) ...
            Assert.Contains(buttonARow.AvailableInputs, c =>
                c.Descriptor == "Gamepad ButtonA" && string.Equals(
                    c.DeviceGuid, XboxGuid.ToString(), StringComparison.OrdinalIgnoreCase));

            // ... but the empty-guid source selects the "(Any device)"
            // entry, not the concrete one.
            Assert.NotNull(buttonARow.SelectedInput);
            Assert.Equal("Gamepad ButtonA", buttonARow.SelectedInput.Descriptor);
            Assert.True(string.IsNullOrEmpty(buttonARow.SelectedInput.DeviceGuid));
        }
    }
}
