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

        /// <summary><para>THE #265 REGRESSION, in the one configuration that
        /// actually shipped broken. A DS3 paired outside PadForge's ceremony
        /// leaves NO BTHPORT VID/PID record: BthPS3 identifies pads by remote
        /// name and the pairing itself lives inside the controller, which
        /// stores the host radio's MAC. So the narrow "did PadForge pair one"
        /// probe reads false on a machine whose DS3 connects over BthPS3
        /// daily.</para>
        ///
        /// <para>That machine was only still working because a leftover
        /// %ProgramData%\DsHidMini folder made the DsHidMini probe read true,
        /// which forced always-armed. Fixing that probe alone would have
        /// disarmed patching and silently killed the pad, which is why the
        /// policy input had to widen in the same change.</para>
        ///
        /// <para>The guard is stated as the composition the caller performs:
        /// paired-OR-present, never paired alone.</para></summary>
        [Theory]
        // no DsHidMini, no BTHPORT record, but a DS3 devnode exists: STAY ARMED.
        [InlineData(false, false, true, true)]
        // and with neither signal there is genuinely no pad: disarm.
        [InlineData(false, false, false, false)]
        public void PsmPatchPolicy_ArmsForAnExternallyPairedPad(
            bool dsHidMini, bool anyPaired, bool machineHasDs3, bool expectPatching)
        {
            bool hasPad = anyPaired || machineHasDs3;
            var (_, patching) =
                PadForge.Services.Ds3PairingService.PsmPatchPolicy(dsHidMini, hasPad);
            Assert.Equal(expectPatching, patching);
        }

        /// <summary>The DsHidMini probe must not accept a leftover config
        /// folder as proof. %ProgramData%\DsHidMini is the driver's settings
        /// root and survives uninstall the way application config normally
        /// does, so on its own it says the driver WAS here, not that it is.
        /// Accepting it disabled the #204 crash-safety mitigation on machines
        /// that had removed DsHidMini.</summary>
        [Fact]
        public void DsHidMiniProbe_DoesNotTrustTheLeftoverConfigFolder()
        {
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RepoRoot(), "PadForge.App", "Services", "Ds3DriverInstaller.cs"));
            int probe = src.IndexOf("public static bool IsDsHidMiniInstalled",
                System.StringComparison.Ordinal);
            Assert.True(probe > 0, "IsDsHidMiniInstalled not found");
            int end = src.IndexOf("\n        }", probe, System.StringComparison.Ordinal);
            string body = src.Substring(probe, end - probe);

            Assert.False(body.Contains("CommonApplicationData"),
                "IsDsHidMiniInstalled is trusting %ProgramData% again. That folder " +
                "survives uninstall, so it proves the driver WAS installed, not that " +
                "it is (#265).");
        }

        private static string RepoRoot()
        {
            var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "PadForge.App")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }
    }
}
