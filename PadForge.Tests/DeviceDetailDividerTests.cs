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
    }
}
