using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The device detail pane draws two dividers in the stretch between the
    /// Input Mode / Hiding section and the Raw Input State header, with the
    /// Power section between them. Exactly one has to survive whatever the
    /// device happens to have.
    ///
    /// <para>The lower one was unconditional, so a device with an Input Mode
    /// or Hiding section and no Power section drew both back to back with
    /// nothing in between. Owner-reported on a Navigation controller: two
    /// bars under "Hide from Games".</para>
    ///
    /// <para>It cannot simply follow the Power section either, which is why
    /// it was unconditional in the first place: a device with neither Power
    /// nor the sections above it has no divider at all, and the Raw Input
    /// State header sits flush against the assignment controls.</para>
    /// </summary>
    public class DeviceDetailDividerTests
    {
        /// <summary>Both gates are derived, so the row is driven through the
        /// values that feed them: the type key decides IsGamepad, and the
        /// device path decides IsInternalVirtual.</summary>
        private static DeviceRowViewModel Row(bool gamepad, bool internalVirtual, bool idleDisconnect)
            => new DeviceRowViewModel
            {
                DeviceTypeKey = gamepad ? "Gamepad" : "Keyboard",
                DevicePath = internalVirtual
                    ? "web://controller/1"
                    : @"\?\hid#vid_054c&pid_042f",
                ShowIdleDisconnect = idleDisconnect,
            };

        /// <summary>THE BUG. A gamepad with no Power section: the section
        /// above drew its own divider, so this one must not draw a second.</summary>
        [Fact]
        public void SectionAboveButNoPower_DrawsOnlyTheUpperDivider()
        {
            var vm = Row(gamepad: true, internalVirtual: false, idleDisconnect: false);
            Assert.True(vm.ShowInputModeOrHidingSection);
            Assert.False(vm.ShowRawInputDivider);
        }

        /// <summary>With a Power section between them, both dividers are
        /// doing real work: one under the sections, one under Power.</summary>
        [Fact]
        public void SectionAboveAndPower_DrawsBoth()
        {
            var vm = Row(gamepad: true, internalVirtual: false, idleDisconnect: true);
            Assert.True(vm.ShowInputModeOrHidingSection);
            Assert.True(vm.ShowRawInputDivider);
        }

        /// <summary>Nothing above: the lower divider is the only one, and
        /// dropping it leaves the header flush against the assignment
        /// controls. This is the case the unconditional version existed
        /// for, and it must keep working.</summary>
        [Fact]
        public void NothingAbove_StillDrawsTheLowerDivider()
        {
            // An internal virtual source: no Input Mode (no SDL mapping layer
            // to bypass) and no Input Hiding (HidHide cannot blacklist what is
            // not a Windows HID device), so nothing draws above.
            var vm = Row(gamepad: true, internalVirtual: true, idleDisconnect: false);
            Assert.False(vm.ShowInputModeOrHidingSection);
            Assert.True(vm.ShowRawInputDivider);
        }

        /// <summary>Whatever the device, the stretch never draws two rules
        /// with nothing between them, and never draws none. Stated as the
        /// invariant rather than as three cases.</summary>
        [Theory]
        [InlineData(true, false, true)]
        [InlineData(true, false, false)]
        [InlineData(false, false, true)]
        [InlineData(false, false, false)]
        [InlineData(true, true, true)]
        [InlineData(true, true, false)]
        public void ExactlyOneDividerSeparatesEachAdjacentPair(
            bool gamepad, bool internalVirtual, bool idleDisconnect)
        {
            var vm = Row(gamepad, internalVirtual, idleDisconnect);
            bool upper = vm.ShowInputModeOrHidingSection;
            bool power = vm.ShowIdleDisconnect;
            bool lower = vm.ShowRawInputDivider;

            // Adjacent dividers with no section between them is the defect.
            Assert.False(upper && lower && !power);
            // No divider at all before the header is the defect it replaced.
            Assert.True(upper || lower);
        }
        /// <summary>
        /// Every synthetic row must answer IsInternalVirtual, because HidHide
        /// has no HID instance to cloak for any of them and the Input Hiding
        /// section would otherwise render toggles that can never do anything.
        ///
        /// <para>The OpenXR hand rows and the Logitech G-keys row were added in
        /// 4.5.0 and missed the list, which is the sibling-set gap this pins.</para>
        /// </summary>
        [Theory]
        [InlineData("web://controller/1")]
        [InlineData("overlay://touchpad/0")]
        [InlineData("midi://in/0")]
        [InlineData("peer://pc/1")]
        [InlineData("nfc://reader/0")]
        [InlineData("mic://capture/0")]
        [InlineData("handheld://buttons")]
        [InlineData("sensor://motion")]
        [InlineData("headtrack://opentrack")]
        [InlineData("openxr://hand/left")]
        [InlineData("openxr://hand/right")]
        [InlineData("logigkeys://local")]
        public void EverySyntheticPath_IsInternalVirtual(string devicePath)
        {
            var vm = new DeviceRowViewModel { DevicePath = devicePath };
            Assert.True(vm.IsInternalVirtual, devicePath + " must not offer HidHide toggles");
        }

        /// <summary>A real HID path is not synthetic. Without this the theory
        /// above would pass against a property hard-coded to true.</summary>
        [Fact]
        public void ARealHidPath_IsNotInternalVirtual()
        {
            var vm = new DeviceRowViewModel { DevicePath = @"\?\hid#vid_054c&pid_042f" };
            Assert.False(vm.IsInternalVirtual);
        }

        /// <summary>
        /// A merged row stands for every device of its kind and has no HID
        /// instance of its own, so HidHide has nothing to cloak for it. The
        /// hiding pass skips such a row on both of its lookups (its path names
        /// no instance and its vendor and product ids are zero), and the box
        /// was offered there anyway: it took the tick, saved it and hid
        /// nothing.
        ///
        /// <para>The four merged rows do not end up the same. Consume Input
        /// is a separate route that does work on a merged keyboard or mouse,
        /// so those two keep the section with that one box in it. Touchpads
        /// and consumer controls have neither box, so the heading goes as
        /// well, or it would sit over an empty body.</para>
        /// </summary>
        [Theory]
        [InlineData("aggregate://keyboards", "Keyboard", true)]
        [InlineData("aggregate://mice", "Mouse", true)]
        [InlineData("aggregate://touchpads", "Touchpad", false)]
        [InlineData("aggregate://consumercontrols", "ConsumerControl", false)]
        public void AMergedRow_NeverOffersHidHide_AndKeepsConsumeWhereItWorks(
            string devicePath, string typeKey, bool consumeApplies)
        {
            var vm = new DeviceRowViewModel { DeviceTypeKey = typeKey, DevicePath = devicePath };

            Assert.True(vm.IsAggregate);
            Assert.False(vm.ShowHidHideToggle, devicePath + " has no HID instance for HidHide to cloak");
            Assert.Equal(consumeApplies, vm.ShowConsumeToggle);
            Assert.Equal(consumeApplies, vm.ShowInputHidingSection);
        }

        /// <summary>The physical devices the merged rows stand for keep the
        /// box. Taking it off a real keyboard to fix the merged row would
        /// trade one defect for a worse one.</summary>
        [Theory]
        [InlineData("Keyboard")]
        [InlineData("Mouse")]
        [InlineData("Gamepad")]
        [InlineData("ConsumerControl")]
        public void APhysicalRow_StillOffersHidHide(string typeKey)
        {
            var vm = new DeviceRowViewModel { DeviceTypeKey = typeKey, DevicePath = @"\?\hid#vid_046d&pid_c33f" };

            Assert.False(vm.IsAggregate);
            Assert.True(vm.ShowHidHideToggle);
            Assert.True(vm.ShowInputHidingSection);
        }

        /// <summary>A row is built before its type and path are known, and
        /// they arrive in either order. The heading now follows the two
        /// boxes, and one of those follows the type, so setting the type has
        /// to raise the heading's change too or a binding made against the
        /// empty row keeps the stale answer.</summary>
        [Fact]
        public void SettingTheTypeAfterThePath_RaisesTheHidingSectionsChange()
        {
            var vm = new DeviceRowViewModel { DevicePath = "aggregate://keyboards" };
            Assert.False(vm.ShowInputHidingSection);

            var raised = new System.Collections.Generic.List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.DeviceTypeKey = "Keyboard";

            Assert.True(vm.ShowInputHidingSection);
            Assert.Contains(nameof(DeviceRowViewModel.ShowConsumeToggle), raised);
            Assert.Contains(nameof(DeviceRowViewModel.ShowInputHidingSection), raised);
        }

        /// <summary>
        /// Submitting a mapping opens a pre-filled GitHub issue about a piece of
        /// hardware, so a row backed by a runtime or an SDK has nothing to
        /// submit. VrController and LogitechGKeys missed this list in 4.5.0, and
        /// the list's AnalogKeyboard entry had no case here.
        /// </summary>
        [Theory]
        [InlineData("Gamepad")]
        [InlineData("Mouse")]
        [InlineData("Keyboard")]
        [InlineData("Touchpad")]
        [InlineData("Tablet")]
        [InlineData("Midi")]
        [InlineData("Nfc")]
        [InlineData("HeadsetMotion")]
        [InlineData("Microphone")]
        [InlineData("HandheldButtons")]
        [InlineData("ConsumerControl")]
        [InlineData("SystemMotion")]
        [InlineData("HeadTracker")]
        [InlineData("VrController")]
        [InlineData("LogitechGKeys")]
        [InlineData("AnalogKeyboard")]
        [InlineData("WebMenus")]
        [InlineData("PeripheralLighting")]
        [InlineData("PeripheralHaptics")]
        public void RowsWithNoHardwareToDescribe_DoNotOfferSubmitMapping(string typeKey)
        {
            var vm = new DeviceRowViewModel { DeviceTypeKey = typeKey };
            Assert.False(vm.ShowSubmitMapping, typeKey + " must not offer Submit Device Mapping");
        }

        /// <summary>A joystick still offers it, which is the whole point of the
        /// button. Without this the theory above proves nothing.</summary>
        [Fact]
        public void AJoystick_StillOffersSubmitMapping()
        {
            var vm = new DeviceRowViewModel { DeviceTypeKey = "Joystick" };
            Assert.True(vm.ShowSubmitMapping);
        }

    }
}
