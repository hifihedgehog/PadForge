using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Drivers card's HIDMaestro and BthPS3 rows, read with the subject
    /// their place gives them: a number beside "Installed" in a row named
    /// for a driver reads as the installed driver's version, as the HidHide
    /// row's does. Each row shows the installed driver's own version, and no
    /// number while none is installed, whatever version was last read.
    /// </summary>
    public class DriverRowVersionTests
    {
        [Fact]
        public void TheHidMaestroRow_ShowsNoNumber_WhileNoDriverIsInstalled()
        {
            var vm = new SettingsViewModel { HIDMaestroVersion = "2.4.0", IsHIDMaestroInstalled = false };
            Assert.Equal(string.Empty, vm.HIDMaestroVersion);
            vm.IsHIDMaestroInstalled = true;
            Assert.Equal("2.4.0", vm.HIDMaestroVersion);
        }

        [Fact]
        public void TheBthPs3Row_ShowsNoNumber_WhileNoDriverIsInstalled()
        {
            var vm = new SettingsViewModel { BthPS3Version = "3.2.1", IsBthPS3Installed = false };
            Assert.Equal(string.Empty, vm.BthPS3Version);
            vm.IsBthPS3Installed = true;
            Assert.Equal("3.2.1", vm.BthPS3Version);
        }
    }
}
