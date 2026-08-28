using System.Linq;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A profile whose HID descriptor leads with a mouse or a keyboard
    /// report cannot carry a hand-packed frame through HIDMaestro's raw
    /// path, and the failure is loud: the frame comes back out as cursor
    /// motion.
    ///
    /// <para>HMController.SubmitRawReport takes data bytes only, its own XML
    /// says so, and the driver prepends the descriptor's FIRST report id.
    /// Point that at a mouse and every frame we submit drives the pointer.
    /// Valve's 2026 Steam Controller is exactly that shape: its lizard-mode
    /// mouse (report 0x40) and keyboard (0x41) share one interface with its
    /// controller state (0x42), and the mouse is first. A slot on that
    /// profile drove the cursor with the rolling sequence number out of our
    /// own frame, 250 times a second.</para>
    ///
    /// <para>HIDMaestro owns the fix (its raw path must honor
    /// extendedReport.reportId, HIDMaestro issue 58). These
    /// tests are the tripwire: they name the one profile in that condition
    /// today, so a SECOND one cannot arrive unnoticed, and the named one
    /// stops matching the moment HIDMaestro lands the fix.</para>
    /// </summary>
    public class PointingReportProfileGuardTests
    {
        // Verbatim from HIDMaestro profiles/valve/steam-controller-2.json,
        // which took it from OpenPuck's ReversePuckFirmware CTRL_HID_DESC.
        // Report 0x40 mouse, 0x41 keyboard, 0x42 the controller state.
        private const string Triton =
            "05010902a10185400901a100050919012902150025017501950281027506950181010501093009311581"
            + "257f7508950281069501093881060501";

        private static byte[] Hex(string s)
        {
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = System.Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }

        [Fact]
        public void TheTritonDescriptorLeadsWithAMouse()
            => Assert.True(HMaestroProfileCatalog.LeadsWithAPointingReport(Hex(Triton)));

        /// <summary>The 2026 Steam Controller is the ONLY profile that hand
        /// packs a frame while its descriptor leads with a pointing report.
        /// A second one arriving means a second controller would take over
        /// the pointer, and this is where that gets caught.</summary>
        [Fact]
        public void OnlyTheKnownProfilePacksAgainstAPointingLedDescriptor()
        {
            var offenders = HMaestroProfileCatalog.AllProfiles
                .Where(p => ValveReportPackers.ForProfile(p.Id) != null)
                .Where(HMaestroProfileCatalog.LeadsWithAPointingReport)
                .Select(p => p.Id)
                .OrderBy(id => id)
                .ToArray();

            Assert.Equal(new[] { "steam-controller-2" }, offenders);
        }

        /// <summary>A keyboard first is the same defect: HID keyboard
        /// reports are what the frame would become.</summary>
        [Fact]
        public void AKeyboardLeadingDescriptorIsDetected()
        {
            byte[] kbd =
            {
                0x05, 0x01,             // Usage Page (Generic Desktop)
                0x09, 0x06,             // Usage (Keyboard)
                0xA1, 0x01,             // Collection (Application)
                0x85, 0x01,             //   Report ID (1)
                0x05, 0x07,             //   Usage Page (Keyboard)
                0x19, 0xE0, 0x29, 0xE7, //   Usage Min/Max (modifiers)
                0x15, 0x00, 0x25, 0x01, //   Logical 0..1
                0x75, 0x01, 0x95, 0x08, //   8 x 1 bit
                0x81, 0x02,             //   Input
                0xC0,                   // End Collection
            };
            Assert.True(HMaestroProfileCatalog.LeadsWithAPointingReport(kbd));
        }

        /// <summary>A gamepad that happens to use report ids is fine: the
        /// id the driver prepends is its own gamepad report.</summary>
        [Fact]
        public void AGamepadWithReportIdsIsClean()
        {
            byte[] pad =
            {
                0x05, 0x01,             // Usage Page (Generic Desktop)
                0x09, 0x05,             // Usage (Gamepad)
                0xA1, 0x01,             // Collection (Application)
                0x85, 0x01,             //   Report ID (1)
                0x09, 0x30, 0x09, 0x31, //   Usage X, Y
                0x15, 0x00, 0x26, 0xFF, 0x00,
                0x75, 0x08, 0x95, 0x02, //   2 x 8 bits
                0x81, 0x02,             //   Input
                0xC0,                   // End Collection
            };
            Assert.False(HMaestroProfileCatalog.LeadsWithAPointingReport(pad));
        }

        /// <summary>No report ids at all means one input report and no
        /// prepend, so there is nothing to collide with. That is the shape
        /// the 2015 Steam Controller and the Steam Deck ship.</summary>
        [Fact]
        public void ADescriptorWithNoReportIdsIsClean()
        {
            byte[] vendor =
            {
                0x06, 0x00, 0xFF,       // Usage Page (Vendor FF00)
                0x09, 0x01,             // Usage (1)
                0xA1, 0x01,             // Collection (Application)
                0x09, 0x01,             //   Usage (1)
                0x15, 0x00, 0x26, 0xFF, 0x00,
                0x75, 0x08, 0x95, 0x40, //   64 x 8 bits
                0x81, 0x02,             //   Input
                0xC0,                   // End Collection
            };
            Assert.False(HMaestroProfileCatalog.LeadsWithAPointingReport(vendor));
        }

        [Fact]
        public void AnEmptyDescriptorIsClean()
        {
            Assert.False(HMaestroProfileCatalog.LeadsWithAPointingReport((byte[])null));
            Assert.False(HMaestroProfileCatalog.LeadsWithAPointingReport(System.Array.Empty<byte>()));
        }
    }
}
