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

            // The concrete device lists the same read under its own name
            // (A is Button 0) and no longer repeats the abstract one ...
            Assert.DoesNotContain(buttonARow.AvailableInputs, c =>
                c.Descriptor == "Gamepad ButtonA" && !string.IsNullOrEmpty(c.DeviceGuid));
            Assert.Contains(buttonARow.AvailableInputs, c =>
                c.Descriptor == "Button 0" && string.Equals(
                    c.DeviceGuid, XboxGuid.ToString(), StringComparison.OrdinalIgnoreCase));

            // ... and the empty-guid source selects the "(Any device)" entry.
            Assert.NotNull(buttonARow.SelectedInput);
            Assert.Equal("Gamepad ButtonA", buttonARow.SelectedInput.Descriptor);
            Assert.True(string.IsNullOrEmpty(buttonARow.SelectedInput.DeviceGuid));
        }

        private static HashSet<string> AbstractGamepadNames()
            => PadForge.Engine.Common.Mapping.SourceCoercion.GamepadAliasTable
                .Select(t => "Gamepad " + t.Member)
                .ToHashSet(StringComparer.Ordinal);

        [Fact]
        public void AbstractGamepadNames_AreListedOnlyUnderAnyDevice()
        {
            // Owner report 2026-10-02: an Xbox Series controller's group
            // listed "Gamepad A" beside its own A. Each abstract name is
            // offered once, under "(Any device)", and the controller's group
            // keeps its own reads plus the compound stick sources, which
            // nothing else in that group reads.
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            var pad0 = mainVm.Pads[0];
            svc.RefreshAvailableInputsForSlot(pad0);

            var abstractNames = AbstractGamepadNames();
            var choices = pad0.Mappings.First(m => m.TargetSettingName == "ButtonA").AvailableInputs;
            foreach (string name in abstractNames)
            {
                var copies = choices.Where(c => c.Descriptor == name).ToList();
                Assert.True(copies.Count <= 1, name + " is listed " + copies.Count + " times");
                Assert.All(copies, c => Assert.True(string.IsNullOrEmpty(c.DeviceGuid), name + " is listed under a device"));
            }
            Assert.Contains(choices, c => c.Descriptor == "Gamepad ButtonA" && string.IsNullOrEmpty(c.DeviceGuid));

            string xbox = XboxGuid.ToString();
            bool OnXbox(InputChoice c) => string.Equals(c.DeviceGuid, xbox, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(choices, c => c.Descriptor == "Button 0" && OnXbox(c));
            Assert.Contains(choices, c => c.Descriptor == PadForge.Engine.Common.Mapping.SourceCoercion.FlickStickRightDescriptor && OnXbox(c));
            Assert.Contains(choices, c => c.Descriptor == PadForge.Engine.Common.Mapping.SourceCoercion.RightStickRingDescriptor && OnXbox(c));

            // The per-device builder itself emits none of them.
            var device = SettingsManager.UserDevices.Items.First(u => u.InstanceGuid == XboxGuid);
            Assert.DoesNotContain(PadForge.Common.MappingDisplayResolver.BuildInputChoices(device),
                c => abstractNames.Contains(c.Descriptor));
        }

        [Fact]
        public void StickRings_UnderAController_DropTheGamepadPrefix()
        {
            // Owner, 2026-10-02: the stick rings stay under each controller,
            // where they read that controller's stick, named without the
            // Gamepad prefix that marks the "(Any device)" family. A row and
            // the macro editor name a ring the way its context's group does.
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            var pad0 = mainVm.Pads[0];
            svc.RefreshAvailableInputsForSlot(pad0);
            var si = PadForge.Resources.Strings.Strings.Instance;
            string xbox = XboxGuid.ToString();
            var choices = pad0.Mappings.First(m => m.TargetSettingName == "ButtonA").AvailableInputs;
            var device = SettingsManager.UserDevices.Items.First(u => u.InstanceGuid == XboxGuid);

            foreach (var (desc, bare) in new[]
            {
                (PadForge.Engine.Common.Mapping.SourceCoercion.LeftStickRingDescriptor, si.Mapping_LeftStickRing),
                (PadForge.Engine.Common.Mapping.SourceCoercion.RightStickRingDescriptor, si.Mapping_RightStickRing),
            })
            {
                string prefixed = string.Format(si.Mapping_Gamepad_Format, bare);
                var onXbox = Assert.Single(choices, c => c.Descriptor == desc
                    && string.Equals(c.DeviceGuid, xbox, StringComparison.OrdinalIgnoreCase));
                var onAny = Assert.Single(choices, c => c.Descriptor == desc && string.IsNullOrEmpty(c.DeviceGuid));
                Assert.Equal(bare, onXbox.DisplayName);
                Assert.Equal(prefixed, onAny.DisplayName);

                var bound = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
                bound.LoadDescriptor(desc);
                PadForge.Common.MappingDisplayResolver.ResolveDisplayText(bound, device);
                Assert.Equal(onXbox.DisplayName, bound.SourceDisplayText);

                var free = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
                free.LoadDescriptor(desc);
                PadForge.Common.MappingDisplayResolver.ResolveDisplayText(free, null);
                Assert.Equal(onAny.DisplayName, free.SourceDisplayText);

                Assert.Equal(prefixed, PadForge.Common.MappingDisplayResolver.ResolveDescriptorText(desc, null, padPrefixAlways: true));
                Assert.Equal(bare, PadForge.Common.MappingDisplayResolver.ResolveDescriptorText(desc, null, padPrefixAlways: false));
            }

            // Every other Gamepad name reads the same in both contexts.
            Assert.Equal(PadForge.Common.MappingDisplayResolver.ResolveGamepadText("Gamepad ButtonA"),
                PadForge.Common.MappingDisplayResolver.ResolveGamepadText("Gamepad ButtonA", anyDevice: false));
            Assert.Equal(PadForge.Common.MappingDisplayResolver.ResolveGamepadText("Gamepad LeftStickTouch"),
                PadForge.Common.MappingDisplayResolver.ResolveGamepadText("Gamepad LeftStickTouch", anyDevice: false));
        }

        [Fact]
        public void APinnedAbstractName_ReadsAsTheDevicesOwnEntryOnItsRowAndItsParameterPickers()
        {
            // A source stored as "Gamepad ButtonA" with a controller selects
            // that controller's own entry, so the row's text names that entry
            // too, and a parameter picker on a pinned source names the
            // controller's own entry for an abstract key with the controller's
            // label. An unpinned source keeps the "(Any device)" entry.
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            var pad0 = mainVm.Pads[0];
            string xbox = XboxGuid.ToString();
            var ms = new MappingSet();
            ms.Rows.Add(new MappingRow
            {
                Target = "ButtonA",
                Sources =
                {
                    new MappingSource { Descriptor = "Gamepad ButtonA", DeviceGuid = xbox },
                    new MappingSource { Descriptor = "Button 2", DeviceGuid = xbox, Kind = "InvertOnHold", ParamModifier = "Gamepad ButtonB" },
                    new MappingSource { Descriptor = "Button 3", Kind = "InvertOnHold", ParamModifier = "Gamepad ButtonB" },
                },
            });
            SettingsManager.SlotMappingSets[0] = ms;
            InputService.RefreshMappingsToViewModel(pad0);
            svc.RefreshAvailableInputsForSlot(pad0);

            var row = pad0.Mappings.First(m => m.TargetSettingName == "ButtonA");
            Assert.Equal("Button 0", row.SelectedInput.Descriptor);
            Assert.Equal(row.SelectedInput.DisplayName, row.SourceDisplayText);
            Assert.NotEqual(PadForge.Common.MappingDisplayResolver.ResolveGamepadText("Gamepad ButtonA"), row.SourceDisplayText);

            var pinned = row.ExtraSources[0];
            Assert.Equal("Gamepad ButtonB", pinned.ParamModifier);
            Assert.Equal("Button 1", pinned.ParamModifierInputChoice.Descriptor);
            Assert.Equal(xbox, pinned.ParamModifierInputChoice.DeviceGuid, ignoreCase: true);
            Assert.NotEqual(PadForge.Resources.Strings.Strings.Instance.Mapping_AnyDevice, pinned.ParamModifierDeviceLabel);
            Assert.Equal(pinned.ParamModifierInputChoice.DeviceLabel, pinned.ParamModifierDeviceLabel);

            var free = row.ExtraSources[1];
            Assert.Equal("Gamepad ButtonB", free.ParamModifierInputChoice.Descriptor);
            Assert.True(string.IsNullOrEmpty(free.ParamModifierInputChoice.DeviceGuid));
        }

        [Fact]
        public void AnInvertedOrHalfGamepadSource_ReadsByItsNameNotItsStoredForm()
        {
            // The load keeps Invert and Half as an I/H prefix on the row's
            // descriptor, so an imported stick ring arrives as
            // "HGamepad LeftStickRing". Its row text names the ring with the
            // flag's label, the way every other family's prefixed row reads.
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            var pad0 = mainVm.Pads[0];
            var si = PadForge.Resources.Strings.Strings.Instance;
            var ms = new MappingSet();
            ms.Rows.Add(new MappingRow
            {
                Target = "ButtonA",
                Sources = { new MappingSource { Descriptor = "Gamepad LeftStickRing", HalfAxis = true } },
            });
            ms.Rows.Add(new MappingRow
            {
                Target = "DPadUp",
                Sources = { new MappingSource { Descriptor = "Gamepad LeftStickY", HalfAxis = true, Invert = true } },
            });
            SettingsManager.SlotMappingSets[0] = ms;
            InputService.RefreshMappingsToViewModel(pad0);

            var ring = pad0.Mappings.First(m => m.TargetSettingName == "ButtonA");
            Assert.Equal("HGamepad LeftStickRing", ring.SourceDescriptor);
            Assert.Equal(si.Mapping_Half + " " + PadForge.Common.MappingDisplayResolver.ResolveGamepadText("Gamepad LeftStickRing"),
                ring.SourceDisplayText);
            var wedge = pad0.Mappings.First(m => m.TargetSettingName == "DPadUp");
            Assert.Equal("IHGamepad LeftStickY", wedge.SourceDescriptor);
            Assert.Equal(si.Mapping_InvHalf + " " + PadForge.Common.MappingDisplayResolver.ResolveGamepadText("Gamepad LeftStickY"),
                wedge.SourceDisplayText);

            // The same ring stored with a controller drops the Gamepad prefix
            // and keeps the flag's label.
            var bound = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            bound.LoadDescriptor("HGamepad LeftStickRing");
            PadForge.Common.MappingDisplayResolver.ResolveDisplayText(bound,
                SettingsManager.UserDevices.Items.First(u => u.InstanceGuid == XboxGuid));
            Assert.Equal(si.Mapping_Half + " " + si.Mapping_LeftStickRing, bound.SourceDisplayText);
        }

        [Fact]
        public void HidingAnyDevice_HidesEveryAbstractGamepadName()
        {
            // The picker's device filter hides a group by its key, "any" for
            // the device-agnostic group. With that group hidden, no abstract
            // name may survive under a concrete controller.
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            var pad0 = mainVm.Pads[0];
            svc.RefreshAvailableInputsForSlot(pad0);

            var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "any" };
            var visible = pad0.Mappings.First(m => m.TargetSettingName == "ButtonA").AvailableInputs
                .Where(c => PadViewModel.MatchesPickerFilter(c, "", hidden)).ToList();
            var abstractNames = AbstractGamepadNames();
            Assert.DoesNotContain(visible, c => abstractNames.Contains(c.Descriptor));
            Assert.Contains(visible, c => c.Descriptor == "Button 0");
        }

        [Fact]
        public void AnAbstractSourcePinnedToADevice_SelectsThatDevicesOwnEntry()
        {
            // Before the abstract names left the device groups, a user could
            // pick "Gamepad A" under a controller, which stores that device.
            // Such a row keeps its binding and selects the controller's own
            // entry for the same read (Gamepad A is Button 0, listed as A),
            // for the primary source and for an extra one.
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            var pad0 = mainVm.Pads[0];
            string xbox = XboxGuid.ToString();
            var ms = new MappingSet();
            ms.Rows.Add(new MappingRow
            {
                Target = "ButtonA",
                Sources =
                {
                    new MappingSource { Descriptor = "Gamepad ButtonA", DeviceGuid = xbox },
                    new MappingSource { Descriptor = "Gamepad ButtonB", DeviceGuid = xbox },
                },
            });
            SettingsManager.SlotMappingSets[0] = ms;

            InputService.RefreshMappingsToViewModel(pad0);
            svc.RefreshAvailableInputsForSlot(pad0);

            var row = pad0.Mappings.First(m => m.TargetSettingName == "ButtonA");
            Assert.NotNull(row.SelectedInput);
            Assert.Equal("Button 0", row.SelectedInput.Descriptor);
            Assert.Equal(xbox, row.SelectedInput.DeviceGuid, ignoreCase: true);
            Assert.Equal("Gamepad ButtonA", row.SourceDescriptor);
            Assert.Equal(xbox, row.PrimarySourceDeviceGuid, ignoreCase: true);

            var extra = Assert.Single(row.ExtraSources);
            Assert.NotNull(extra.SelectedInput);
            Assert.Equal("Button 1", extra.SelectedInput.Descriptor);
            Assert.Equal(xbox, extra.SelectedInput.DeviceGuid, ignoreCase: true);
            Assert.Equal("Gamepad ButtonB", extra.Descriptor);
            Assert.Equal(xbox, extra.DeviceGuid, ignoreCase: true);

            // The device's own entry is a match on the device, so the source
            // takes its device label from it, as an exact match would.
            string xboxLabel = extra.SelectedInput.DeviceLabel;
            Assert.False(string.IsNullOrEmpty(xboxLabel));
            extra.DeviceLabel = "";
            svc.RefreshAvailableInputsForSlot(pad0);
            Assert.Equal(xboxLabel, Assert.Single(row.ExtraSources).DeviceLabel);
        }

        [Fact]
        public void SingleInputPickers_SelectTheDevicesOwnEntryForAPinnedAbstractName()
        {
            // The Gyro tab's aim engage, the trigger-route activators and the
            // mouse-gesture engage match their stored input by descriptor and
            // device with no "(Any device)" fallback, which could unpin a
            // binding on write-back. A pinned abstract name selects the
            // device's own entry instead, still on that device.
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            var pad0 = mainVm.Pads[0];
            svc.RefreshAvailableInputsForSlot(pad0);
            string xbox = XboxGuid.ToString();

            pad0.GyroAimEngageButton = "Gamepad LeftShoulder";
            pad0.GyroAimEngageDeviceGuid = xbox;
            pad0.LeftTriggerRouteActivator = "Gamepad ButtonX";
            pad0.LeftTriggerRouteActivatorDeviceGuid = xbox;
            pad0.RightTriggerRouteActivator = "Gamepad ButtonY";
            pad0.RightTriggerRouteActivatorDeviceGuid = xbox;
            pad0.MouseGestureCustomEngageButton = "Gamepad RightShoulder";
            pad0.MouseGestureCustomEngageDeviceGuid = xbox;
            pad0.DeviceConfig.AudioMirrorEngageButton = "Gamepad ButtonB";
            pad0.DeviceConfig.AudioMirrorEngageDeviceGuid = xbox;

            void AssertOnXbox(InputChoice c, string descriptor)
            {
                Assert.NotNull(c);
                Assert.Equal(descriptor, c.Descriptor);
                Assert.Equal(xbox, c.DeviceGuid, ignoreCase: true);
            }
            AssertOnXbox(pad0.GyroAimEngageSelectedInput, "Button 4");
            AssertOnXbox(pad0.LeftTriggerRouteActivatorSelectedInput, "Button 2");
            AssertOnXbox(pad0.RightTriggerRouteActivatorSelectedInput, "Button 3");
            AssertOnXbox(pad0.MouseGestureCustomEngageSelectedInput, "Button 5");
            AssertOnXbox(pad0.MirrorEngageSelectedInput, "Button 1");

            // An any-device abstract name still selects its "(Any device)" entry.
            pad0.GyroAimEngageDeviceGuid = "";
            Assert.Equal("Gamepad LeftShoulder", pad0.GyroAimEngageSelectedInput?.Descriptor);
            Assert.True(string.IsNullOrEmpty(pad0.GyroAimEngageSelectedInput.DeviceGuid));
        }

        [Fact]
        public void ShiftActivatorDialog_ReselectsAPinnedAbstractNameThroughTheDevicesOwnEntry()
        {
            // WPF dialogs cannot run headless, so the lookup is pinned where it
            // lives. Without it, an activator picked as "Gamepad A" under a
            // controller could not be re-selected, and Save would block on the
            // input-required validation.
            string root = new System.IO.DirectoryInfo(AppContext.BaseDirectory).FullName;
            while (!System.IO.Directory.Exists(System.IO.Path.Combine(root, "PadForge.App")))
                root = System.IO.Directory.GetParent(root).FullName;
            string dlg = System.Text.RegularExpressions.Regex.Replace(
                System.IO.File.ReadAllText(System.IO.Path.Combine(root,
                    "PadForge.App", "Views", "ShiftActivatorDialog.xaml.cs")), @"\s+", "");
            Assert.Contains("FindChoiceOnDevice(InputCombo.Items.OfType<InputChoice>(),existing.Descriptor,existing.DeviceGuid);", dlg);
            Assert.Contains("FindChoiceOnDevice(ChordSecondCombo.Items.OfType<InputChoice>(),secondDesc,secondGuid);", dlg);
        }

        [Fact]
        public void FindChoiceOnDevice_PrefersTheExactEntryAndNeverLeavesTheDevice()
        {
            const string pad = "33333333-3333-3333-3333-333333333333";
            var anyA = new InputChoice { Descriptor = "Gamepad ButtonA", DeviceGuid = "" };
            var padA = new InputChoice { Descriptor = "Button 0", DeviceGuid = pad };
            var padAliasA = new InputChoice { Descriptor = "Gamepad ButtonA", DeviceGuid = pad };
            var otherA = new InputChoice { Descriptor = "Button 0", DeviceGuid = "44444444-4444-4444-4444-444444444444" };

            // The exact entry wins over the device's own spelling.
            Assert.Same(padAliasA, PadForge.Common.MappingDisplayResolver.FindChoiceOnDevice(
                new[] { anyA, padA, padAliasA }, "Gamepad ButtonA", pad));
            // Without it, the device's own entry for the same read.
            Assert.Same(padA, PadForge.Common.MappingDisplayResolver.FindChoiceOnDevice(
                new[] { anyA, otherA, padA }, "Gamepad ButtonA", pad.ToUpperInvariant()));
            // An any-device name matches only the "(Any device)" entry.
            Assert.Same(anyA, PadForge.Common.MappingDisplayResolver.FindChoiceOnDevice(
                new[] { padA, anyA }, "Gamepad ButtonA", ""));
            // Never another device's entry, and nothing for a name that is not an alias.
            Assert.Null(PadForge.Common.MappingDisplayResolver.FindChoiceOnDevice(
                new[] { anyA, otherA }, "Gamepad ButtonA", pad));
            Assert.Null(PadForge.Common.MappingDisplayResolver.FindChoiceOnDevice(
                new[] { padA }, "Gamepad LeftStickRing", pad));
            Assert.Null(PadForge.Common.MappingDisplayResolver.FindChoiceOnDevice(
                new[] { padA }, "", pad));
        }
    
        /// <summary>The row DATA itself must be reloaded on a deviceless
        /// slot, not only the picker.
        ///
        /// <para>The apply tail called LoadPadSettingToViewModel only when a
        /// device was selected. A Workshop import carries no assignments by
        /// design, so on every imported slot the mapping refresh inside that
        /// call never ran and the grid kept the OUTGOING profile's
        /// MappingItems: the owner saw the default profile's controller on
        /// rows whose stored DeviceGuid is empty. The picker tests above all
        /// passed the whole time, because the picker WAS rebuilt. Only the
        /// rows were stale.</para></summary>
        [Fact]
        public void WorkshopApply_DevicelessSlots_ReloadTheRowsThemselves()
        {
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            var pad0 = mainVm.Pads[0];
            var buttonA = pad0.Mappings.First(m => m.TargetSettingName == "ButtonA");

            // Stamp the outgoing profile's device onto the row, the state
            // that used to survive the switch. The fixture's default profile
            // leaves this empty, so setting it explicitly is what makes the
            // stale-vs-reloaded distinction observable at all.
            buttonA.PrimarySourceDeviceGuid = XboxGuid.ToString();

            ImportAndApplyWorkshopProfile(svc);

            // Re-query rather than reuse the captured instance: if the apply
            // rebuilds the Mappings collection, the old object is orphaned
            // and would report its stale value forever, which would make this
            // test lie about the product.
            buttonA = pad0.Mappings.First(m => m.TargetSettingName == "ButtonA");

            // The imported row stores an empty GUID. If the reload was
            // skipped, this still reads the Xbox pad's GUID and the grid
            // renders that controller's name under an abstract source.
            Assert.True(string.IsNullOrEmpty(buttonA.PrimarySourceDeviceGuid),
                $"row kept the outgoing profile's device ('{buttonA.PrimarySourceDeviceGuid}'): "
                + "the deviceless slot never reloaded its mapping rows.");
        }
    
        /// <summary>The persisted-artifact invariant, checked the way the
        /// owner's PadForge.xml revealed the bug: an Authoritative (Workshop)
        /// set must keep its abstract, device-free sources.
        ///
        /// <para>The shipped file had the opposite: every row of the
        /// "sonic campaign" Authoritative set carried a concrete DeviceGuid
        /// and a raw "Button N" descriptor, with zero "Gamepad ..." sources
        /// left. ApplyProfile held its stale-ViewModel guard only around
        /// UpdatePadDeviceInfo, so an autosave landing anywhere in the ~40
        /// lines before the per-pad refresh rewrote the imported set from the
        /// OUTGOING profile's MappingItems, which is the clobber that
        /// function's own comment warns about.</para></summary>
        [Fact]
        public void WorkshopApply_AuthoritativeSet_KeepsItsDeviceFreeSources()
        {
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            ImportAndApplyWorkshopProfile(svc);

            var set = SettingsManager.SlotMappingSets[0];
            Assert.NotNull(set);
            Assert.True(set.Authoritative, "the imported set lost its Workshop ownership flag");

            var row = set.Rows.First(r => r.Target == "ButtonA");
            var src = row.Sources.First();
            Assert.Equal("Gamepad ButtonA", src.Descriptor);
            Assert.True(string.IsNullOrEmpty(src.DeviceGuid),
                $"an Authoritative row was rebound to a concrete device ('{src.DeviceGuid}'): "
                + "the imported abstract source was clobbered.");
        }
    
        /// <summary>The round-trip the owner actually performs: import,
        /// apply, then let the debounced save run. The save pushes the live
        /// grid into SlotMappingSets and SaveActiveProfileState snapshots
        /// those sets back onto the stored profile, so a stale grid does not
        /// merely LOOK wrong, it overwrites the imported profile on disk.
        ///
        /// <para>Receipt: the owner's PadForge.xml, written three seconds
        /// after ImportedAt, held an Authoritative set with zero
        /// "Gamepad ..." sources and 23 rows bound to the assigned
        /// controller, byte-identical to that profile's own legacy
        /// per-device descriptors.</para></summary>
        [Fact]
        public void WorkshopImport_SurvivesTheSaveRoundTrip()
        {
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            var profile = ImportAndApplyWorkshopProfile(svc);

            // The debounced save: grid -> live sets, then live -> profile.
            svc.PushUiIntoSlotMappingSetsForTest();
            svc.SaveActiveProfileState();

            var stored = profile.SlotMappingSets?[0];
            Assert.NotNull(stored);
            Assert.True(stored.Authoritative, "the stored set lost its Workshop ownership flag");

            var row = stored.Rows.FirstOrDefault(r => r.Target == "ButtonA");
            Assert.NotNull(row);
            var src = row.Sources.First();
            Assert.Equal("Gamepad ButtonA", src.Descriptor);
            Assert.True(string.IsNullOrEmpty(src.DeviceGuid),
                $"the save round-trip rebound an imported row to '{src.DeviceGuid}'. "
                + "This is the shape that reached the owner's XML.");
        }
    
        /// <summary>The grid push must not rebind an Authoritative set's
        /// device-free sources, even when the grid rows carry a concrete
        /// device. This is the shape that reached the owner's XML: slot 0
        /// still flagged Authoritative, all 22 sources rewritten from
        /// "Gamepad ..." with an empty GUID to the assigned pad's raw
        /// "Button N".</summary>
        [Fact]
        public void GridPush_CannotRebindAnAuthoritativeSetToADevice()
        {
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            ImportAndApplyWorkshopProfile(svc);

            // Simulate the stale grid the owner sees: the row carries the
            // assigned pad's concrete descriptor instead of the abstract one.
            var pad0 = mainVm.Pads[0];
            var row = pad0.Mappings.First(m => m.TargetSettingName == "ButtonA");
            row.PrimarySourceDeviceGuid = XboxGuid.ToString();
            row.SourceDescriptor = "Button 0";
            // The push short-circuits on !MappingsViewLoaded. RefreshMappingsCore
            // sets it in the real app, so without this the test passes
            // vacuously by never reaching the rebuild it means to exercise.
            pad0.MappingsViewLoaded = true;

            svc.PushUiIntoSlotMappingSetsForTest();

            var src = SettingsManager.SlotMappingSets[0].Rows
                .First(r => r.Target == "ButtonA").Sources.First();
            Assert.Equal("Gamepad ButtonA", src.Descriptor);
            Assert.True(string.IsNullOrEmpty(src.DeviceGuid),
                $"a grid push rebound an Authoritative row to '{src.DeviceGuid}' "
                + $"with descriptor '{src.Descriptor}'. The Workshop import owns its rows.");
        }
    
        /// <summary>The owner's second symptom, verbatim: "When I assign a
        /// device with automapping to it, it replaces the mapping in the
        /// profile."
        ///
        /// <para>Assignment writes an auto-mapped legacy PadSetting for the
        /// device, then the legacy merge runs. Commit 7cf6c17b gated both of
        /// that merge's write operations behind !current.Authoritative so an
        /// import owns its rows outright. The owner's file says both ran
        /// anyway: 20 translated rows became 22, and every source carries the
        /// assigned pad's GUID instead of the empty one.</para></summary>
        [Fact]
        public void AssigningAnAutomappableDevice_DoesNotRewriteAnImportedSet()
        {
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            ImportAndApplyWorkshopProfile(svc);

            var before = SettingsManager.SlotMappingSets[0];
            Assert.True(before.Authoritative, "precondition: the imported set owns its rows");
            int rowsBefore = before.Rows.Count;

            // Assign the pad to slot 0 the way DeviceService does: a legacy
            // PadSetting carrying the automap's concrete descriptors.
            var ps = new PadSetting();
            ps.ButtonA = "Button 0";
            ps.ButtonB = "Button 1";
            ps.ButtonX = "Button 2";
            ps.ButtonY = "Button 3";
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                foreach (var us in SettingsManager.UserSettings.Items)
                    if (us.InstanceGuid == XboxGuid) { us.MapTo = 0; us.SetPadSetting(ps); }
            }

            SettingsService.RefreshMappingSetsFromLegacy();

            var after = SettingsManager.SlotMappingSets[0];
            Assert.True(after.Authoritative, "the merge dropped Workshop ownership");
            Assert.Equal(rowsBefore, after.Rows.Count);

            var src = after.Rows.First(r => r.Target == "ButtonA").Sources.First();
            Assert.Equal("Gamepad ButtonA", src.Descriptor);
            Assert.True(string.IsNullOrEmpty(src.DeviceGuid),
                $"assignment rebound an imported row to '{src.DeviceGuid}' "
                + $"with descriptor '{src.Descriptor}'.");
        }
    
        /// <summary>Workshop ownership must survive every replacement of the
        /// live set, not only the merge's normal path.
        ///
        /// <para>The owner's XML caught the split that matters: the LIVE
        /// slot-0 set read Authoritative="false" with all 22 sources bound to
        /// the assigned pad, while the profile's stored copy still read true.
        /// Both merge guards key on current.Authoritative, so once the live
        /// flag is lost the automap is free to rebind every imported row and
        /// add its own. Any path that swaps the live set has to carry the
        /// flag.</para></summary>
        [Fact]
        public void LegacyMerge_CarriesAuthoritativeAcrossASetReplacement()
        {
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            ImportAndApplyWorkshopProfile(svc);

            // The replacement branch: a live set flagged Authoritative whose
            // Rows list is null, which hands the rebuilt legacy set over.
            var live = SettingsManager.SlotMappingSets[0];
            Assert.True(live.Authoritative);
            live.Rows = null;

            SettingsService.RefreshMappingSetsFromLegacy();

            Assert.True(SettingsManager.SlotMappingSets[0].Authoritative,
                "the replacement dropped Workshop ownership, which un-gates the "
                + "automap merge for every later device assignment.");
        }
    
        /// <summary>The picker rebuild must not be able to rebind a row.
        ///
        /// <para>A live ComboBox bound TwoWay to SelectedInput drives
        /// SelectedItem when its ItemsSource is cleared and refilled, and
        /// that write reaches the setter, which stamps the entry's DeviceGuid
        /// onto the row and reloads its descriptor. On an imported
        /// device-free row that is a silent rebind to a concrete device, and
        /// the next save persists it. No harness reproduces the provocation,
        /// so this asserts the invariant the suppression exists to hold: a
        /// write arriving mid-rebuild changes nothing.</para></summary>
        [Fact]
        public void PickerRebuild_IgnoresASelectionWriteMidFlight()
        {
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            ImportAndApplyWorkshopProfile(svc);

            var row = mainVm.Pads[0].Mappings.First(m => m.TargetSettingName == "ButtonA");
            string guidBefore = row.PrimarySourceDeviceGuid;
            string descBefore = row.SourceDescriptor;

            // What WPF does mid-rebuild: assign a concrete-device entry.
            row.SelectedInput = new InputChoice
            {
                Descriptor = "Button 0",
                DisplayName = "A",
                DeviceGuid = XboxGuid.ToString(),
                DeviceLabel = "Xbox pad",
            };

            // Outside a rebuild this IS a real user pick and must apply.
            Assert.Equal(XboxGuid.ToString(), row.PrimarySourceDeviceGuid, ignoreCase: true);

            // Restore, then prove the same write is inert during a rebuild.
            row.PrimarySourceDeviceGuid = guidBefore;
            row.LoadDescriptor(descBefore);

            var choices = new List<InputChoice>(row.AvailableInputs);
            row.ReplaceAvailableInputsForTest(choices, () =>
                row.SelectedInput = new InputChoice
                {
                    Descriptor = "Button 0",
                    DisplayName = "A",
                    DeviceGuid = XboxGuid.ToString(),
                    DeviceLabel = "Xbox pad",
                });

            Assert.Equal(descBefore, row.SourceDescriptor);
            Assert.True(string.IsNullOrEmpty(row.PrimarySourceDeviceGuid),
                $"a mid-rebuild selection write rebound the row to "
                + $"'{row.PrimarySourceDeviceGuid}'.");
        }
    
        /// <summary>XML round-trip: an Authoritative set's device-free rows
        /// must survive serialize and deserialize. Every deploy restarts the
        /// app, so this path runs constantly in practice and no prior test
        /// covered it.</summary>
        [Fact]
        public void AuthoritativeSet_SurvivesXmlRoundTrip()
        {
            var ms = new MappingSet { Authoritative = true };
            var row = new MappingRow { Target = "ButtonA", LayerMask = "Base" };
            row.Sources.Add(new MappingSource
            { Kind = "Direct", Descriptor = "Gamepad ButtonA", DeviceGuid = "" });
            ms.Rows.Add(row);

            var ser = new System.Xml.Serialization.XmlSerializer(typeof(MappingSet));
            string xml;
            using (var sw = new System.IO.StringWriter())
            {
                ser.Serialize(sw, ms);
                xml = sw.ToString();
            }
            Assert.Contains("Gamepad ButtonA", xml);
            Assert.Contains("Authoritative=\"true\"", xml);

            MappingSet back;
            using (var sr = new System.IO.StringReader(xml))
                back = (MappingSet)ser.Deserialize(sr);

            Assert.True(back.Authoritative, "the flag did not survive the round trip");
            Assert.NotNull(back.Rows);
            Assert.Single(back.Rows);
            var src = back.Rows[0].Sources.First();
            Assert.Equal("Gamepad ButtonA", src.Descriptor);
            Assert.True(string.IsNullOrEmpty(src.DeviceGuid));
        }
    
        /// <summary>The owner's action, through the real assignment entry
        /// point: import a Workshop profile, then assign an automappable
        /// device to the slot. DeviceService.AssignDeviceToSlot is the method
        /// the Devices page calls, and no test had ever run it after an
        /// import.</summary>
        [Fact]
        public void AssignDeviceToSlot_AfterImport_LeavesTheImportedRowsAlone()
        {
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            var settingsSvc = new SettingsService(mainVm);
            var devSvc = new DeviceService(mainVm, settingsSvc);

            ImportAndApplyWorkshopProfile(svc);

            // A Workshop import leaves the slot deviceless, which is the
            // state the owner assigns INTO. Make that explicit, or
            // AssignDeviceToSlot early-returns on "already assigned" and the
            // test passes without running the path it exists to exercise.
            lock (SettingsManager.UserSettings.SyncRoot)
                foreach (var u in SettingsManager.UserSettings.Items)
                    if (u.InstanceGuid == XboxGuid) u.MapTo = -1;
            svc.RefreshDeviceList();

            var devRow = mainVm.Devices.Devices.OfType<DeviceRowViewModel>()
                .FirstOrDefault(d => d.InstanceGuid == XboxGuid);
            Assert.NotNull(devRow);
            Assert.DoesNotContain(0, devRow.AssignedSlots);

            var before = SettingsManager.SlotMappingSets[0];
            Assert.True(before.Authoritative);
            var srcBefore = before.Rows.First(r => r.Target == "ButtonA").Sources.First();
            Assert.Equal("Gamepad ButtonA", srcBefore.Descriptor);

            devSvc.AssignDeviceToSlot(XboxGuid, 0);

            // Positive control: the assignment really happened.
            Assert.Contains(0, SettingsManager.GetAssignedSlots(XboxGuid));

            // Now the rest of MainWindow's DeviceAssignmentChanged handler,
            // in its real order. Units pass in isolation; this composition is
            // what the app actually runs and what no test had ever executed.
            foreach (var pv in mainVm.Pads) pv.MappingsViewLoaded = false;
            svc.RefreshDeviceList();
            SettingsService.RefreshMappingSetsFromLegacy();
            for (int i = 0; i < mainVm.Pads.Count; i++)
            {
                var pv = mainVm.Pads[i];
                InputService.RefreshMappingsToViewModel(pv);
                var sel = pv.SelectedMappedDevice;
                if (sel != null && sel.InstanceGuid != Guid.Empty)
                    InputService.LoadPadSettingToViewModel(pv, sel.InstanceGuid);
                svc.RefreshAvailableInputsForSlot(pv);
            }
            // The debounced save that follows any assignment.
            svc.PushUiIntoSlotMappingSetsForTest();

            var after = SettingsManager.SlotMappingSets[0];
            var row = after.Rows.FirstOrDefault(r => r.Target == "ButtonA");
            Assert.NotNull(row);
            var src = row.Sources.First();
            Assert.Equal("Gamepad ButtonA", src.Descriptor);
            Assert.True(string.IsNullOrEmpty(src.DeviceGuid),
                $"assignment rebound the imported row to '{src.DeviceGuid}' "
                + $"with descriptor '{src.Descriptor}'.");
        }
    
        /// <summary>Startup load: the persisted Authoritative set must come
        /// back from XML with its device-free rows, not be rebuilt from the
        /// assigned device's legacy automap.
        ///
        /// <para>Every deploy restarts the app, so LoadOrMigrateSlotMappingSets
        /// runs constantly in practice. It keeps the XML set when
        /// HasAuthoredContent is true and otherwise hands the slot to
        /// BuildOneSlotFromLegacy, which knows only the per-device
        /// descriptors and therefore emits device-bound rows in the
        /// automap's alphabetical target order. That is the exact shape in
        /// the owner's file.</para></summary>
        [Fact]
        public void StartupLoad_KeepsTheImportedSet_DoesNotRebuildFromLegacy()
        {
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            ImportAndApplyWorkshopProfile(svc);

            // The device carries an automap PadSetting, as it does after any
            // assignment. This is what BuildOneSlotFromLegacy would emit from.
            var autoPs = new PadSetting();
            autoPs.ButtonA = "Button 0";
            autoPs.ButtonB = "Button 1";
            lock (SettingsManager.UserSettings.SyncRoot)
                foreach (var u in SettingsManager.UserSettings.Items)
                    if (u.InstanceGuid == XboxGuid) { u.MapTo = 0; u.SetPadSetting(autoPs); }

            var persisted = SettingsManager.SlotMappingSets;
            Assert.True(persisted[0].Authoritative);
            Assert.Equal("Gamepad ButtonA",
                persisted[0].Rows.First(r => r.Target == "ButtonA").Sources.First().Descriptor);

            // Re-run the startup path over the persisted sets.
            SettingsService.LoadOrMigrateSlotMappingSetsForTest(persisted);

            var after = SettingsManager.SlotMappingSets[0];
            Assert.True(after.Authoritative, "startup load dropped Workshop ownership");
            var row = after.Rows.FirstOrDefault(r => r.Target == "ButtonA");
            Assert.NotNull(row);
            var src = row.Sources.First();
            Assert.Equal("Gamepad ButtonA", src.Descriptor);
            Assert.True(string.IsNullOrEmpty(src.DeviceGuid),
                $"startup load rebound the imported row to '{src.DeviceGuid}' "
                + $"with descriptor '{src.Descriptor}'.");
        }
    
        /// <summary>The owner's own discriminator: "the issue ONLY occurs if
        /// there is a virtual controller type from the default profile
        /// matching the newly loaded community profile."
        ///
        /// <para>PadViewModel.OutputType rebuilds Mappings, sticks and
        /// triggers only inside its SetProperty guard, so assigning the SAME
        /// type is a no-op and the slot keeps the OUTGOING profile's
        /// MappingItem instances: their PrimarySourceDeviceGuid, their
        /// AvailableInputs, and a SelectedInput still referencing an
        /// InputChoice built for the previous profile's device. A slot whose
        /// type DIFFERS goes through the rebuild and comes out clean, which
        /// is exactly the asymmetry the owner observed.</para></summary>
        [Fact]
        public void SameTypeSlot_StillRebuildsItsRows_OnProfileApply()
        {
            var (mainVm, svc) = ArrangeDefaultProfileWithXboxPad();
            var pad0 = mainVm.Pads[0];
            Assert.Equal(VirtualControllerType.Xbox, pad0.OutputType);

            // The outgoing profile's row, bound to the default profile's pad.
            var stale = pad0.Mappings.First(m => m.TargetSettingName == "ButtonA");
            stale.PrimarySourceDeviceGuid = XboxGuid.ToString();
            stale.LoadDescriptor("Button 0");
            stale.SelectedInput = new InputChoice
            {
                Descriptor = "Button 0",
                DisplayName = "A",
                DeviceGuid = XboxGuid.ToString(),
                DeviceLabel = "Xbox pad",
            };

            // The imported profile also wants an Xbox slot 0: types MATCH.
            ImportAndApplyWorkshopProfile(svc);
            Assert.Equal(VirtualControllerType.Xbox, pad0.OutputType);

            var row = pad0.Mappings.First(m => m.TargetSettingName == "ButtonA");

            Assert.True(string.IsNullOrEmpty(row.PrimarySourceDeviceGuid),
                $"a same-type slot kept the outgoing profile's device "
                + $"('{row.PrimarySourceDeviceGuid}') on its row.");
            Assert.True(row.SelectedInput == null
                || string.IsNullOrEmpty(row.SelectedInput.DeviceGuid),
                "the row's picker selection still points at the outgoing "
                + "profile's device.");
        }
    }
}
