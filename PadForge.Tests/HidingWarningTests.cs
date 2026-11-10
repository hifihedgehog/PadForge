using PadForge.Resources.Strings;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The confirm flyout before Hide from Games or Consume Mapped Inputs turns
    /// on for a keyboard, mouse or Consumer Control row. Hide from Games said a
    /// keyboard or mouse would be hidden from all applications and the system
    /// lost, but HidHide cannot take either from Windows (measured 2026-10-31
    /// for a keyboard, 5809b088 for a mouse). The message now says what does
    /// happen and names Use Only in PadForge, which takes a USB keyboard or
    /// mouse from Windows. The Consume warning stays, since the hooks do
    /// swallow mapped keys.
    /// </summary>
    public sealed class HidingWarningTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void HideFromGames_SaysWindowsKeepsTheDevice_AndNamesUseOnlyInPadForge(bool mouse)
        {
            var s = Strings.Instance;
            string kind = mouse ? s.Devices_DeviceKind_Mouse : s.Devices_DeviceKind_Keyboard;
            string text = DevicesPage.HidingWarning(isHidHide: true, isMouse: mouse, isMerged: false);

            Assert.DoesNotContain(string.Format(s.Devices_WarnHide_Format, kind), text);
            Assert.StartsWith(string.Format(s.Devices_WarnAction_Format, s.Devices_HideHidAction, "", ""), text);
            Assert.Contains(string.Format(mouse ? s.Devices_WarnHideHid_Mouse : s.Devices_WarnHideHid_Keyboard,
                s.Devices_DriverBind_OnlyInPadForge), text);
            Assert.Contains(s.Devices_DriverBind_OnlyInPadForge, text);
        }

        /// <summary>The merged keyboard or mouse row offers Consume Mapped Inputs
        /// and never Hide from Games (DeviceRowViewModel.ShowHidHideToggle
        /// excludes an aggregate row), so the scope reaches only this
        /// warning. It once read "for all connected mouses".</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ConsumeMappedInputs_OnTheMergedRow_NamesEveryConnectedDevice(bool mouse)
        {
            var s = Strings.Instance;
            string kind = mouse ? s.Devices_DeviceKind_Mouse : s.Devices_DeviceKind_Keyboard;
            string text = DevicesPage.HidingWarning(isHidHide: false, isMouse: mouse, isMerged: true);
            Assert.Contains(string.Format(s.Devices_WarnScope_Format, kind), text);
            Assert.DoesNotContain(kind + "s", text);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ConsumeMappedInputs_StaysAWarning(bool mouse)
        {
            var s = Strings.Instance;
            string text = DevicesPage.HidingWarning(isHidHide: false, isMouse: mouse, isMerged: false);
            Assert.Contains(s.Devices_ConsumeAction, text);
            Assert.Contains(mouse ? s.Devices_WarnConsumeMouse : s.Devices_WarnConsumeKeyboard, text);
        }
    }
}
