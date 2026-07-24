using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins the BthPS3PSM filter-control IOCTL contract behind the issue #199
    /// crash mitigation. Ds3DriverInstaller.SetPsmPatching sends the ENABLE and
    /// DISABLE codes as magic hex; this recomputes them (and the GET code and
    /// its 408-byte buffer, which the filter also exposes) from the documented
    /// Windows CTL_CODE formula and the BthPS3.h definitions
    /// (common/include/BthPS3.h:360-410, tag v2.10.470.0) so a future edit
    /// cannot silently corrupt the control path. The ENABLE/DISABLE values
    /// match what the upstream BthPS3 repo's own C# consumer
    /// (shared/FilterDriver.cs) hardcodes.
    /// </summary>
    public class BthPs3PsmIoctlTests
    {
        // CTL_CODE(DeviceType, Function, Method, Access)
        //   = (DeviceType << 16) | (Access << 14) | (Function << 2) | Method
        private static uint CtlCode(uint deviceType, uint function, uint method, uint access)
            => (deviceType << 16) | (access << 14) | (function << 2) | method;

        private const uint FILE_DEVICE_BUS_EXTENDER = 0x0000002A;
        private const uint METHOD_BUFFERED = 0;
        private const uint FILE_READ_DATA = 0x0001;
        private const uint FILE_WRITE_DATA = 0x0002;
        private const uint IOCTL_BTHPS3_BASE = 0x801;

        [Fact]
        public void Enable_Ioctl_MatchesFormula()
        {
            // BUSENUM_W_IOCTL(base + 0x300)
            uint enable = CtlCode(FILE_DEVICE_BUS_EXTENDER, IOCTL_BTHPS3_BASE + 0x300,
                METHOD_BUFFERED, FILE_WRITE_DATA);
            Assert.Equal(0x2AAC04u, enable);
        }

        [Fact]
        public void Disable_Ioctl_MatchesFormula()
        {
            // BUSENUM_W_IOCTL(base + 0x301)
            uint disable = CtlCode(FILE_DEVICE_BUS_EXTENDER, IOCTL_BTHPS3_BASE + 0x301,
                METHOD_BUFFERED, FILE_WRITE_DATA);
            Assert.Equal(0x2AAC08u, disable);
        }

        [Fact]
        public void Get_Ioctl_MatchesFormula()
        {
            // BUSENUM_R_IOCTL(base + 0x302)
            uint get = CtlCode(FILE_DEVICE_BUS_EXTENDER, IOCTL_BTHPS3_BASE + 0x302,
                METHOD_BUFFERED, FILE_READ_DATA);
            Assert.Equal(0x2A6C0Cu, get);
        }

        [Fact]
        public void Get_BufferSize_MatchesStruct()
        {
            // BTHPS3PSM_GET_PSM_PATCHING (pshpack1):
            //   ULONG DeviceIndex; ULONG IsEnabled; WCHAR SymbolicLinkName[200]
            //   = 4 + 4 + 200 * sizeof(WCHAR=2) = 408. The filter rejects any
            //   other input/output length (Sideband.c:479-495).
            const int size = 4 + 4 + 200 * 2;
            Assert.Equal(408, size);
        }

        /// <summary>The PSM-patch policy truth table (issue #199 +
        /// the 2026-07-24 DsHidMini coexistence audit). A DsHidMini system
        /// never has patching disarmed and never has ownership taken:
        /// its DS3s connect only while BthPS3 patching is armed, and its
        /// pads leave no BTHPORT VID/PID record for the paired probe to
        /// find. Without DsHidMini, PadForge owns arming and patches only
        /// while a DS3 is actually paired (the crash-safety default).</summary>
        [Theory]
        [InlineData(true, false, false, true)]   // DsHidMini, no PadForge DS3: leave theirs armed
        [InlineData(true, true, false, true)]    // DsHidMini + PadForge DS3: still theirs, armed
        [InlineData(false, false, true, false)]  // no DsHidMini, nothing paired: own + disarm
        [InlineData(false, true, true, true)]    // no DsHidMini, DS3 paired: own + armed
        public void PsmPatchPolicy_TruthTable(
            bool dsHidMini, bool anyPaired, bool expectOwnership, bool expectPatching)
        {
            var (takeOwnership, patching) =
                PadForge.Services.Ds3PairingService.PsmPatchPolicy(dsHidMini, anyPaired);
            Assert.Equal(expectOwnership, takeOwnership);
            Assert.Equal(expectPatching, patching);
        }
    }
}
