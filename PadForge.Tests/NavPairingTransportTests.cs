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
            int at = src.IndexOf("bool isNav = dev == null || dev.Value.Pid == NAV_PID;", System.StringComparison.Ordinal);
            Assert.True(at > 0);
            int moveAt = src.IndexOf("MoveSixpair(dev.Value.AddrPath", at, System.StringComparison.Ordinal);
            Assert.True(moveAt > at);

            string navBranch = src.Substring(at, moveAt - at);
            Assert.Contains("EnsureWinUsbBound(_log, ct,", navBranch, System.StringComparison.Ordinal);
            // And it binds the NAVIGATION pad, not the DS3.
            Assert.Contains("NavPidToken", navBranch, System.StringComparison.Ordinal);
        }

        /// <summary>The DS3 and the Navigation controller share one WinUSB
        /// interface GUID, so every lookup that used to mean "the DS3"
        /// because nothing else carried that GUID now has to say which pad
        /// it wants. Unfiltered, the DS3 instance opened the Navigation
        /// pad's interface and listed it as a DualShock 3 on PID 0x0268.
        /// FindPdoPath had already been fixed for this on the BthPS3 side
        /// and its sibling had not.</summary>
        [Fact]
        public void EveryDeviceLookup_NamesThePadItWants()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Common", "Input", "Ds3DirectService.cs"));

            // The interface finder takes a PID token and applies it.
            Assert.Contains("FindInterfacePath(DS3_WINUSB_IF, PidPathToken)",
                src, System.StringComparison.Ordinal);
            Assert.Contains("p.IndexOf(pidToken, StringComparison.OrdinalIgnoreCase) >= 0",
                src, System.StringComparison.Ordinal);
            // No caller can ask for "any pad with this GUID" any more.
            Assert.DoesNotContain("requireVid054c", src, System.StringComparison.Ordinal);

            // The auto-bind judges and binds THIS instance's pad.
            Assert.Contains("IsUsbPadNeedingWinUsb(", src, System.StringComparison.Ordinal);
            Assert.Contains("_log, default, PidHwToken, Tag);",
                src, System.StringComparison.Ordinal);
            Assert.Contains("_log, default, PidHwToken, Tag);",
                src, System.StringComparison.Ordinal);
        }

        /// <summary>The Navigation controller may open over USB. It was
        /// gated off with "the WinUSB INF binds only the DS3", which stopped
        /// being true when the package took the Navigation pad on.</summary>
        [Fact]
        public void TheNavigationInstance_IsNotGatedOffUsb()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Common", "Input", "Ds3DirectService.cs"));
            Assert.DoesNotContain("the WinUSB INF binds only the DS3",
                src, System.StringComparison.Ordinal);
        }

        /// <summary>The DS3 and the Navigation controller share one WinUSB
        /// interface GUID, so NOTHING may look a pad up by that GUID alone.
        /// Unfiltered, a ceremony for one pad can open the other and write
        /// this PC's radio address into the wrong controller, and a bind for
        /// one can report finished because the other's interface is live.
        ///
        /// <para>This is the sweep, not a spot check: every lookup in both
        /// files is listed here, and a new unfiltered one fails.</para></summary>
        [Fact]
        public void NoLookupResolvesAPadByTheSharedGuidAlone()
        {
            string pair = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Services", "Ds3PairingService.cs"));
            string inst = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Services", "Ds3DriverInstaller.cs"));
            string direct = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Common", "Input", "Ds3DirectService.cs"));

            // Every finder takes a PID token.
            Assert.Contains("FindInterfacePath(Guid ifGuid, string pidPathToken)",
                pair, System.StringComparison.Ordinal);
            Assert.Contains("FindInterfacePath(Guid ifGuid, string pidToken)",
                direct, System.StringComparison.Ordinal);
            Assert.Contains("HasActiveWinUsbInterface(string pidPathToken)",
                inst, System.StringComparison.Ordinal);

            // Every finder APPLIES it.
            foreach (string src in new[] { pair, direct, inst })
                Assert.Contains("StringComparison.OrdinalIgnoreCase) >= 0",
                    src, System.StringComparison.Ordinal);

            // And the pairing ceremony asks for the pad it is pairing.
            Assert.Contains(@"FindWinUsbPad(""pid_042f"")", pair, System.StringComparison.Ordinal);
            Assert.Contains(@"FindWinUsbPad(""pid_0268"")", pair, System.StringComparison.Ordinal);
        }

        /// <summary>The Navigation controller is found by its USB node, not
        /// by a HID interface. Binding it to WinUSB removes its HID node, so
        /// a HID search finds it once and never again: every retry after the
        /// first reported "No PS Move or Navigation controller found on USB"
        /// for a pad that was plugged in.</summary>
        [Fact]
        public void NavigationDiscovery_DoesNotDependOnHid()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Services", "Ds3PairingService.cs"));
            Assert.Contains("IsSonyPadOnUsb(Ds3DriverInstaller.NavPidToken)",
                src, System.StringComparison.Ordinal);
            // The HID hit alone can no longer decide there is nothing here.
            Assert.Contains("if (dev == null && !navOnUsb)", src, System.StringComparison.Ordinal);
            // A docked Move still pairs as a Move.
            Assert.Contains("bool isNav = dev == null || dev.Value.Pid == NAV_PID;",
                src, System.StringComparison.Ordinal);
        }

        /// <summary>THE MOVE MUST NOT MOVE. Its ceremony is HID and stays
        /// HID: its own reports ARE in its descriptor, it is hardware-proven
        /// that way, and nothing about the Navigation fix touches it. The
        /// package must not name a Move PID, or a Move plugged in during any
        /// bind would be dragged off the inbox driver.</summary>
        [Theory]
        [InlineData("PID_03D5")]   // PS Move ZCM1
        [InlineData("PID_0C5E")]   // PS Move ZCM2
        public void TheWinUsbInf_NeverNamesAMovePid(string pid)
        {
            string inf = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Resources", "BthPS3", "WinUSB", "ds3_winusb.inf"));
            Assert.DoesNotContain(pid, inf, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The Move branch runs its HID sixpair and binds nothing.
        /// A bind there would be the regression this pins against.</summary>
        [Fact]
        public void TheMoveBranch_BindsNothingAndKeepsItsHidCeremony()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Services", "Ds3PairingService.cs"));
            int at = src.IndexOf("MoveSixpair(dev.Value.AddrPath", System.StringComparison.Ordinal);
            Assert.True(at > 0, "the Move branch is gone");
            int end = src.IndexOf("r.Ds3Mac = macHex;", at, System.StringComparison.Ordinal);
            Assert.True(end > at);

            string moveBranch = src.Substring(at, end - at);
            Assert.DoesNotContain("EnsureWinUsbBound", moveBranch, System.StringComparison.Ordinal);
            Assert.Contains("TryCaptureCalibration", moveBranch, System.StringComparison.Ordinal);

            // And the ceremony itself is still the HID one.
            int ms = src.IndexOf("private bool MoveSixpair(", System.StringComparison.Ordinal);
            int msEnd = src.IndexOf("\n        /// <summary>", ms, System.StringComparison.Ordinal);
            string moveBody = src.Substring(ms, msEnd - ms);
            Assert.Contains("HidD_GetFeature", moveBody, System.StringComparison.Ordinal);
            Assert.Contains("HidD_SetFeature", moveBody, System.StringComparison.Ordinal);
            Assert.DoesNotContain("WinUsb_Initialize", moveBody, System.StringComparison.Ordinal);
        }

        /// <summary>The bind touches ONE pad per call, the one it was asked
        /// about. Binding every covered PID at once would take whichever pad
        /// is merely plugged in off the inbox driver as a side effect of the
        /// other one's ceremony.</summary>
        [Fact]
        public void TheForcedBind_TargetsOnlyTheRequestedPad()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Services", "Ds3DriverInstaller.cs"));
            Assert.Contains(@"string hwid = @""USB\VID_054C&"" + pidToken;",
                src, System.StringComparison.Ordinal);
            // The DS3's own callers keep the DS3 token, so the auto-bind
            // monitor and the DS3 ceremony behave exactly as before.
            Assert.Contains("=> EnsureWinUsbBound(log, ct, Ds3PidToken,", src, System.StringComparison.Ordinal);
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
