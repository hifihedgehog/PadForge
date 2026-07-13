using System.Collections.Generic;
using System.Linq;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Owner report 2026-07-13: an imported Workshop profile displayed a concrete
    /// controller on the SECONDARY sources of a multi-source mapping row instead
    /// of "(Any device)". The stored DeviceGuid was correctly empty (see the
    /// translator guard in PadForge.SteamWorkshop.Tests); the regression was
    /// display-only. <see cref="MappingSourceItem.SyncSelectedInputFromState"/>
    /// stamped the slot's first concrete device onto an empty-guid ("any device")
    /// source via a descriptor-only picker fallback, and the annotation / tooltip
    /// surfaces read that same fallback. These pin the fix: a genuine GUID match
    /// still stamps the device; a descriptor-only fallback never does, so the
    /// source's own "(Any device)" identity stands.
    /// </summary>
    public class WorkshopSecondarySourceDisplayTests
    {
        private const string ConcreteGuid = "11111111-1111-1111-1111-111111111111";
        private const string ConcreteLabel = "DualSense Edge";

        // A concrete-only choice list. Since the picker-bleed fix the live
        // list also carries a leading empty-guid "(Any device)" group
        // (InputService.PopulateAvailableInputs prepends
        // MappingDisplayResolver.BuildDeviceAgnosticChoices), which an
        // empty-guid source now matches on GUID. This hand-built list pins
        // the remaining fallback path: when no empty-guid entry covers the
        // descriptor, a descriptor-only fallback onto a concrete entry must
        // still not adopt that device's identity.
        private static List<InputChoice> ConcreteSlotChoices() => new()
        {
            new InputChoice
            {
                Descriptor = "Gamepad LeftStick",
                DisplayName = "Left Stick (Click)",
                DeviceGuid = ConcreteGuid,
                DeviceLabel = ConcreteLabel,
            },
        };

        [Fact]
        public void EmptyGuidSecondary_DoesNotAdoptConcreteDevice()
        {
            // Mirrors the InputService load path: FromDomain leaves the label
            // empty, the load path resolves the source's own (empty) guid to
            // the localized "(Any device)" sentinel (Strings.Mapping_AnyDevice
            // since the wave-1b l10n pass; English value "(Any device)").
            string anyDevice = PadForge.Resources.Strings.Strings.Instance.Mapping_AnyDevice;
            var src = MappingSourceItem.FromDomain(new MappingSource
            {
                Descriptor = "Gamepad LeftStick",
                DeviceGuid = "",
            });
            src.DeviceLabel = anyDevice;

            src.SyncSelectedInputFromState(ConcreteSlotChoices());

            // The picker still resolves a representative so the ComboBox renders
            // the descriptor.
            Assert.NotNull(src.SelectedInput);
            Assert.Equal("Gamepad LeftStick", src.SelectedInput.Descriptor);

            // But the source's displayed device identity stays "any device", on
            // both the per-source subtitle field and the shared display accessor
            // the annotation / tooltip surfaces read.
            Assert.NotEqual(ConcreteLabel, src.DeviceLabel);
            Assert.Equal(anyDevice, src.DisplayDeviceLabel);
        }

        [Fact]
        public void ConcreteGuidSecondary_StillAdoptsItsDevice()
        {
            // Positive control: a source genuinely bound to the slot device must
            // keep showing that device (the 2026-07-05 multi-device wiring fix).
            var src = MappingSourceItem.FromDomain(new MappingSource
            {
                Descriptor = "Gamepad LeftStick",
                DeviceGuid = ConcreteGuid,
            });

            src.SyncSelectedInputFromState(ConcreteSlotChoices());

            Assert.NotNull(src.SelectedInput);
            Assert.Equal(ConcreteLabel, src.DeviceLabel);
            Assert.Equal(ConcreteLabel, src.DisplayDeviceLabel);
        }

        [Fact]
        public void Materializer_PreservesEmptyGuidOnMultiSourceRow()
        {
            // The materializer is a pass-through for sources; an empty guid must
            // survive translate -> materialize so the display contract holds end
            // to end.
            var t = new TranslatedProfile { Name = "MS", NeedsKbmSlot = true };
            t.KbmMappingSet.Rows.Add(new MappingRow
            {
                Target = "KbmKey45",
                CombineMode = "OR",
                Sources =
                {
                    new MappingSource { Descriptor = "Gamepad DPadUp" },
                    new MappingSource { Descriptor = "Gamepad ButtonStart" },
                },
            });

            var p = WorkshopProfileMaterializer.Materialize(t);
            var row = p.SlotMappingSets[0].Rows.Single(r => r.Target == "KbmKey45");

            Assert.Equal(2, row.Sources.Count);
            Assert.All(row.Sources, s => Assert.True(string.IsNullOrEmpty(s.DeviceGuid)));
        }
    }
}
