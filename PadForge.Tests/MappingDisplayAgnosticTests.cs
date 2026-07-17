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

        // The Menu (#9 B-17) and Mouse Gesture (#216/#200) families are
        // device-free descriptors that Workshop import can write on an
        // empty-guid row, but they are not "(Any device)" PICKER entries
        // (menus author on the Menus tab; the gesture picker carries a
        // concrete mouse guid). The mirror-closure test above cannot cover
        // them, so they get their own chip-resolution guards. Before the
        // 1k audit fix the row chip fell through to the raw 0-based
        // descriptor while the editor showed the localized name.
        [Fact]
        public void EmptyGuidMenuItemRow_ChipResolvesToLocalizedName()
        {
            var si = Strings.Instance;
            var mi = NewItem("Menu 0 Item 3");
            MappingDisplayResolver.ResolveDisplayText(mi, null);
            Assert.Equal(string.Format(si.Mapping_MenuItem_Format, 0, 3), mi.SourceDisplayText);
            Assert.NotEqual("Menu 0 Item 3", mi.SourceDisplayText);
        }

        [Fact]
        public void EmptyGuidMouseGestureRow_ChipResolvesAwayFromRawDescriptor()
        {
            var mi = NewItem("Mouse Gesture 0 Up");
            MappingDisplayResolver.ResolveDisplayText(mi, null);
            Assert.False(string.IsNullOrEmpty(mi.SourceDisplayText));
            Assert.NotEqual("Mouse Gesture 0 Up", mi.SourceDisplayText);
        }

        [Fact]
        public void NonZeroPadClick_ReadsPerPadClicked()
        {
            // Workshop imports emit "Touchpad 1 Click" for Deck / SC 2026
            // right-pad clicks. The reader must consult the per-pad
            // Clicked flag instead of bailing on nonzero indices.
            var s = new PadForge.Engine.CustomInputState();
            s.Touchpads = new[]
            {
                new PadForge.Engine.TouchpadInputState { Clicked = false },
                new PadForge.Engine.TouchpadInputState { Clicked = true },
            };
            var src = new PadForge.Engine.Data.MappingSource { Descriptor = "Touchpad 1 Click" };
            Assert.True(PadForge.Engine.Common.Mapping.SourceCoercion
                .EvaluateForButtonTarget(s, src, 50, 0, null));
            src = new PadForge.Engine.Data.MappingSource { Descriptor = "Touchpad 0 Click" };
            Assert.False(PadForge.Engine.Common.Mapping.SourceCoercion
                .EvaluateForButtonTarget(s, src, 50, 0, null));
        }

        [Fact]
        public void KeyboardHexKeyNames_ResolveThroughTheMacroVkVocabulary()
        {
            var si = Strings.Instance;
            // Keys the engine's invariant table leaves as hex resolve to the
            // macro editor's names: defined VK values get real names...
            Assert.Equal("A", MappingDisplayResolver.LocalizeObjectName("Key 0x41"));
            Assert.Equal(si.Key_Delete, MappingDisplayResolver.LocalizeObjectName("Key 0x2E"));
            Assert.Equal(si.Key_LButton, MappingDisplayResolver.LocalizeObjectName("Key 0x01"));
            // ...the friendly-named engine subset keeps its existing path...
            Assert.Equal(si.Key_Backspace, MappingDisplayResolver.LocalizeObjectName("Backspace"));
            // ...and an undefined VK value keeps the hex fallback.
            Assert.Equal("Key 0x07", MappingDisplayResolver.LocalizeObjectName("Key 0x07"));
        }
    }
}
