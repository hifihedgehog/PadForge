using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Quick Charge (#372, asked in discussion #367): when a Bluetooth
    /// controller's USB twin comes online (same serial, the pad's MAC),
    /// PadForge drops the Bluetooth radio link so the pad charges without
    /// powering the radio. SDL de-duplicates only the JOYSTICK when the USB
    /// twin arrives, so the radio drop is PadForge's job.
    /// </summary>
    public class QuickChargeTests
    {
        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        /// <summary>The opt-in persists per device, and the once-per-plug
        /// latch never does: a restart must re-arm the drop for the next
        /// plug, not resurrect a stale "already handled".</summary>
        [Fact]
        public void FlagPersists_LatchDoesNot()
        {
            var ser = new XmlSerializer(typeof(UserDevice));
            var ud = new UserDevice { QuickChargeEnabled = true, QuickChargeHandled = true };
            using var w = new StringWriter();
            ser.Serialize(w, ud);
            string xml = w.ToString();
            Assert.Contains("QuickChargeEnabled", xml);
            Assert.DoesNotContain("QuickChargeHandled", xml);

            using var r = new StringReader(xml);
            var back = (UserDevice)ser.Deserialize(r);
            Assert.True(back.QuickChargeEnabled);
            Assert.False(back.QuickChargeHandled);
        }

        /// <summary>Serial matching runs through TryParseAddress on both
        /// sides, so the SDL dash format and a colon format for the same MAC
        /// compare equal, and an all-zero or empty serial never matches
        /// anything (address 0 is rejected by the scan).</summary>
        [Theory]
        [InlineData("a0-ab-51-11-22-33", "A0:AB:51:11:22:33", true)]
        [InlineData("a0-ab-51-11-22-33", "a0-ab-51-11-22-33", true)]
        [InlineData("a0-ab-51-11-22-33", "a0-ab-51-11-22-34", false)]
        [InlineData("", "a0-ab-51-11-22-33", false)]
        [InlineData("not-a-mac", "a0-ab-51-11-22-33", false)]
        public void SerialMatching_IsSeparatorAgnostic(string a, string b, bool same)
        {
            bool aOk = PadForge.Common.Input.BluetoothLinkHelper.TryParseAddress(a, out long addrA);
            bool bOk = PadForge.Common.Input.BluetoothLinkHelper.TryParseAddress(b, out long addrB);
            Assert.Equal(same, aOk && bOk && addrA == addrB);
        }

        /// <summary>The all-zero MAC parses (it is six valid octets) and the
        /// scan must therefore reject address zero explicitly, or two
        /// serial-less devices would pair with each other.</summary>
        [Fact]
        public void AllZeroMacIsRejectedByTheScan()
        {
            Assert.True(PadForge.Common.Input.BluetoothLinkHelper.TryParseAddress(
                "00-00-00-00-00-00", out long addr));
            Assert.Equal(0, addr);
            string src = RepoText("PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs");
            int at = src.IndexOf("private static void CheckQuickCharge", StringComparison.Ordinal);
            Assert.True(at > 0);
            Assert.Contains("addr == 0) return;", src.Substring(at, 1400));
        }

        /// <summary>The scan's source contract: it runs from the per-device
        /// update for every online device (before the idle countdown's own
        /// early return, so wired pads with no idle timeout still scan), the
        /// wired side is the seed (Bluetooth devices return), the twin must
        /// be Bluetooth-pathed AND opted in, the latch fires once per USB
        /// arrival, and the fresh-connection stamp re-arms it.</summary>
        [Fact]
        public void ScanContract_SeedGateTwinAndLatch()
        {
            string src = RepoText("PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs");
            int at = src.IndexOf("private static void CheckQuickCharge", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = src.Substring(at, 2600);

            Assert.Contains("if (ud.QuickChargeHandled) return;", body);
            Assert.Contains("DeviceTransport.IsBluetooth(ud.DevicePath", body);
            Assert.Contains("!d.QuickChargeEnabled) continue;", body);
            Assert.Contains("DeviceTransport.IsBluetooth(d.DevicePath", body);
            Assert.Contains("ud.QuickChargeHandled = true;", body);
            Assert.Contains("BluetoothLinkHelper.TryDisconnect(serial)", body);

            // The fresh-connection stamp re-arms the latch, and the scan is
            // reached before the idle countdown's zero-timeout early return.
            int idle = src.IndexOf("private static void UpdateIdleDisconnect(", StringComparison.Ordinal);
            Assert.True(idle > 0);
            string idleBody = src.Substring(idle, src.IndexOf("if (ud.IdleDisconnectSeconds <= 0)", idle) - idle);
            Assert.Contains("ud.QuickChargeHandled = false;", idleBody);
            Assert.Contains("CheckQuickCharge(ud, now);", idleBody);
        }

        /// <summary>The persistence sibling set: the row fill and the row
        /// flush both carry the flag, like the idle-disconnect legs they sit
        /// beside, and the Devices page checkbox uses Click (never Checked,
        /// the #161 lesson) with both localized strings bound.</summary>
        [Fact]
        public void SiblingLegs_FillFlushAndCheckbox()
        {
            string fill = RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("row.QuickChargeEnabled = ud.QuickChargeEnabled;", fill);

            string flush = RepoText("PadForge.App", "Services", "DeviceService.cs");
            Assert.Contains("ud.QuickChargeEnabled = row.QuickChargeEnabled;", flush);

            string page = RepoText("PadForge.App", "Views", "DevicesPage.xaml");
            Assert.Contains("Binding SelectedDevice.QuickChargeEnabled, Mode=TwoWay", page);
            Assert.Contains("Click=\"QuickCharge_Click\"", page);
            Assert.Contains("Devices_QuickCharge,", page);
            Assert.Contains("Devices_QuickChargeTooltip,", page);
            Assert.DoesNotContain("Checked=\"QuickCharge", page);
        }
    }
}
