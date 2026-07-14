using PadForge.Common;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The mapping-row chip and its dropdown are twin display surfaces for
    /// the same descriptor and must render identically (owner report
    /// 2026-07-13: imported "Touchpad {p} Click" rows showed the raw
    /// 0-based descriptor in the chip while the dropdown showed the
    /// localized 1-based picker entry). The chip resolvers therefore
    /// resolve the device-independent touchpad / gyro families without
    /// device-object metadata, using the any-device naming (pad prefix
    /// everywhere) for empty-guid rows and the per-device shortening for
    /// concrete devices.
    /// </summary>
    public class MappingDisplayAgnosticTests
    {
        private static MappingItem NewItem(string descriptor)
        {
            var mi = new MappingItem("Test", "Test", MappingCategory.Buttons);
            mi.LoadDescriptor(descriptor);
            return mi;
        }

        [Theory]
        [InlineData("Touchpad 0 Click", 1)]
        [InlineData("Touchpad 1 Click", 2)]
        public void EmptyGuidClickRow_ChipShowsPadPrefixedPickerEntry(string descriptor, int displayPad)
        {
            var si = Strings.Instance;
            var mi = NewItem(descriptor);
            MappingDisplayResolver.ResolveDisplayText(mi, null);
            string expected = string.Format(
                si.Mapping_TouchpadGesture_PadPrefix_Format, displayPad, si.Mapping_TouchpadClick);
            Assert.Equal(expected, mi.SourceDisplayText);
        }

        [Fact]
        public void EmptyGuidFingerAxisRow_ChipShowsOneBasedName()
        {
            var si = Strings.Instance;
            var mi = NewItem("Touchpad 0 Finger 0 X");
            MappingDisplayResolver.ResolveDisplayText(mi, null);
            Assert.Equal(string.Format(si.Mapping_TouchpadFingerX_Format, 1, 1), mi.SourceDisplayText);
        }

        [Fact]
        public void EmptyGuidInvertedFingerAxisRow_CarriesPrefixLabel()
        {
            var si = Strings.Instance;
            var mi = NewItem("ITouchpad 0 Finger 0 Y");
            MappingDisplayResolver.ResolveDisplayText(mi, null);
            string expected = si.Mapping_Inv + " " + string.Format(si.Mapping_TouchpadFingerY_Format, 1, 1);
            Assert.Equal(expected, mi.SourceDisplayText);
        }

        [Fact]
        public void EmptyGuidGyroRow_ResolvesLocalizedName()
        {
            var mi = NewItem("Gyro Pitch");
            MappingDisplayResolver.ResolveDisplayText(mi, null);
            Assert.Equal(Strings.Instance.Mapping_GyroPitch, mi.SourceDisplayText);
        }

        [Fact]
        public void EmptyGuidNegSource_ResolvesLikeThePicker()
        {
            var si = Strings.Instance;
            var mi = new MappingItem("Test", "TestAxis", MappingCategory.LeftStick, negSettingName: "TestAxisNeg");
            mi.LoadNegDescriptor("Touchpad 1 Click");
            MappingDisplayResolver.ResolveNegDisplayText(mi, null);
            string expected = string.Format(
                si.Mapping_TouchpadGesture_PadPrefix_Format, 2, si.Mapping_TouchpadClick);
            Assert.Contains(expected, mi.SourceDisplayText);
        }

        [Fact]
        public void PerDeviceContext_PadZeroClickStaysUnnumbered()
        {
            Assert.Equal(
                Strings.Instance.Mapping_TouchpadClick,
                MappingDisplayResolver.ResolveDescriptorText("Touchpad 0 Click", null));
        }

        [Fact]
        public void PerDeviceContext_PadOneClickCarriesPadPrefix()
        {
            var si = Strings.Instance;
            string expected = string.Format(
                si.Mapping_TouchpadGesture_PadPrefix_Format, 2, si.Mapping_TouchpadClick);
            Assert.Equal(expected, MappingDisplayResolver.ResolveDescriptorText("Touchpad 1 Click", null));
        }

        /// <summary>The mirror-closure contract: EVERY entry the any-device
        /// picker group offers must chip-render to exactly the picker's
        /// display name when loaded on an empty-guid row. New device-free
        /// source families added to BuildDeviceAgnosticChoices fail here
        /// until the chip resolver learns them too.</summary>
        [Fact]
        public void AnyDevicePickerGroup_RoundTripsThroughChipResolver()
        {
            foreach (var choice in MappingDisplayResolver.BuildDeviceAgnosticChoices())
            {
                var mi = NewItem(choice.Descriptor);
                MappingDisplayResolver.ResolveDisplayText(mi, null);
                Assert.True(
                    choice.DisplayName == mi.SourceDisplayText,
                    $"Chip/picker divergence for '{choice.Descriptor}': picker '{choice.DisplayName}', chip '{mi.SourceDisplayText}'");
            }
        }
    }
}
