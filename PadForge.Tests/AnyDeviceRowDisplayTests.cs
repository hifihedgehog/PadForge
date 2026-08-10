using System;
using System.Reflection;
using PadForge.Common;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Owner report 2026-08-02, second half: after picking an "(Any
    /// device)" touchpad entry from a mapping row's source dropdown, the
    /// row line showed the raw 0-based descriptor ("Touchpad 0 Finger 0
    /// X") while the dropdown itself showed the localized 1-based entry.
    /// The descriptor setter nulls the row's resolved text "until
    /// re-resolved", and the dropdown-pick handler bailed before
    /// re-resolving whenever the Device dropdown had no concrete
    /// selection. It also resolved against the Device dropdown's device,
    /// a different axis from the entry the user actually picked.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AnyDeviceRowDisplayTests
    {
        [Fact]
        public void ResolveDisplayText_WithNoDevice_NamesTouchpadFamilyOneBased()
        {
            var m = new MappingItem("Mouse X", "MouseX", MappingCategory.Buttons);
            m.LoadDescriptor("Touchpad 0 Finger 0 X");

            MappingDisplayResolver.ResolveDisplayText(m, null);

            string expected = string.Format(
                Strings.Instance.Mapping_TouchpadFingerX_Format, 1, 1);
            Assert.Equal(expected, m.SourceDisplayText);
        }

        [Fact]
        public void DropdownPickHandler_ResolvesAnAnyDevicePick_WithNoSelectedDevice()
        {
            // The production handler, invoked exactly as the SelectedInput
            // setter's event does. The row is device-free (Any Device) and
            // no Device-dropdown selection exists anywhere: the pre-fix
            // shape bailed here and left the raw descriptor showing.
            var vm = new MainViewModel();
            var ss = new SettingsService(vm);
            var svc = new InputService(vm) { SettingsService = ss };

            var m = new MappingItem("Mouse X", "MouseX", MappingCategory.Buttons);
            m.PrimarySourceDeviceGuid = "";
            m.LoadDescriptor("Touchpad 0 Finger 0 X");   // nulls resolved text

            var handler = typeof(InputService).GetMethod(
                "OnInputSelectedFromDropdown",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handler);
            handler.Invoke(svc, new object[] { m, EventArgs.Empty });

            string expected = string.Format(
                Strings.Instance.Mapping_TouchpadFingerX_Format, 1, 1);
            Assert.Equal(expected, m.SourceDisplayText);
            Assert.NotEqual("Touchpad 0 Finger 0 X", m.SourceDisplayText);
        }
    }
}
