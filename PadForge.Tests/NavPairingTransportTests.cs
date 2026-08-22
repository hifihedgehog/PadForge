using System.IO;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Navigation controller's pairing ran over inbox HID and could
    /// never have worked. Measured on the owner's hardware: the pad's HID
    /// collection reports FeatureReportByteLength 49 and accepts feature
    /// report ids 0x01, 0x02, 0xEE and 0xEF only. HidD_GetFeature(0xF2)
    /// returns ERROR_INVALID_PARAMETER at every buffer length, including 49.
    ///
    /// <para>This file's own header already said so: the 0xF2 and 0xF5
    /// magic reports are absent from the HID descriptor, which is why the
    /// DS3 ceremony uses WinUSB control transfers. The Navigation pad is a
    /// DS3 in a smaller shell and needs the same transport. It was written
    /// against the Move's HID ceremony instead, because the Move's own
    /// reports ARE in its descriptor.</para>
    /// </summary>
    public class NavPairingTransportTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }

        private static string NavSixpairBody()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Services", "Ds3PairingService.cs"));
            int at = src.IndexOf("private bool NavSixpair(", System.StringComparison.Ordinal);
            Assert.True(at > 0, "NavSixpair not found");
            int next = src.IndexOf("\n        private ", at + 1, System.StringComparison.Ordinal);
            return next > at ? src.Substring(at, next - at) : src.Substring(at);
        }

        /// <summary>THE BUG. The ceremony must not touch the HID feature
        /// API, which cannot carry these reports on this device.</summary>
        [Fact]
        public void NavSixpair_UsesNoHidFeatureCall()
        {
            string body = NavSixpairBody();
            Assert.DoesNotContain("HidD_GetFeature", body, System.StringComparison.Ordinal);
            Assert.DoesNotContain("HidD_SetFeature", body, System.StringComparison.Ordinal);
        }

        /// <summary>It goes over WinUSB, through the same helpers the DS3
        /// ceremony uses, on the reports the references name.</summary>
        [Fact]
        public void NavSixpair_RunsTheDs3WinUsbCeremony()
        {
            string body = NavSixpairBody();
            Assert.Contains("WinUsb_Initialize", body, System.StringComparison.Ordinal);
            Assert.Contains("GetFeature(ifh, 0xF2", body, System.StringComparison.Ordinal);
            Assert.Contains("SetFeature(ifh, 0xF5", body, System.StringComparison.Ordinal);
            // The handle is released on every path, including the failures.
            Assert.Contains("finally { WinUsb_Free(ifh); }", body, System.StringComparison.Ordinal);
            Assert.Contains("finally { CloseHandle(dev); }", body, System.StringComparison.Ordinal);
        }

        /// <summary>A write that returns true is not proof the firmware
        /// stored anything, so the master is read back and compared. The DS3
        /// does this and the Navigation pad gets the same treatment.</summary>
        [Fact]
        public void NavSixpair_ConfirmsTheCommit()
        {
            string body = NavSixpairBody();
            Assert.Contains("sixpair-not-committed", body, System.StringComparison.Ordinal);
            Assert.Contains("SequenceEqual(radioBigEndian)", body, System.StringComparison.Ordinal);
        }

        /// <summary>The pad has to be on WinUSB before the ceremony runs,
        /// and only the Navigation branch binds. The Move stays on HID
        /// because the reports its ceremony uses are in its descriptor.</summary>
        [Fact]
        public void OnlyTheNavigationBranch_BindsWinUsb()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Services", "Ds3PairingService.cs"));
            int at = src.IndexOf("bool isNav = dev.Value.Pid == NAV_PID;", System.StringComparison.Ordinal);
            Assert.True(at > 0);
            int moveAt = src.IndexOf("MoveSixpair(dev.Value.AddrPath", at, System.StringComparison.Ordinal);
            Assert.True(moveAt > at);

            string navBranch = src.Substring(at, moveAt - at);
            Assert.Contains("EnsureWinUsbBound(ct)", navBranch, System.StringComparison.Ordinal);
        }

        /// <summary>The driver package covers both pads. Binding the DS3
        /// alone left the Navigation controller on inbox HID with no way to
        /// reach its sixpair reports.</summary>
        [Fact]
        public void TheWinUsbInf_CoversBothPads()
        {
            string inf = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Resources", "BthPS3", "WinUSB", "ds3_winusb.inf"));
            Assert.Contains(@"USB\VID_054C&PID_0268", inf, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"USB\VID_054C&PID_042F", inf, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
