using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A virtual controller must never be built on a profile whose HID
    /// descriptor leads with a mouse or a keyboard report.
    ///
    /// <para>HIDMaestro frames every input we submit into the descriptor's
    /// FIRST input report and prepends that report's id. SubmitRawReport's
    /// own XML says "Pass data bytes only, the driver prepends the Report ID
    /// automatically", and DeviceOrchestrator.ComputeInputReportByteLength
    /// takes that id from the first Report ID in the descriptor. Point it at
    /// a mouse and every frame becomes cursor motion.</para>
    ///
    /// <para>Valve's 2026 Steam Controller is exactly that shape: its
    /// lizard-mode mouse (0x40) and keyboard (0x41) share the interface with
    /// its controller state (0x42), and the mouse is first. A slot on that
    /// profile drove the pointer with the rolling sequence number in byte 1
    /// of our own frame, 250 times a second, and the owner had to delete the
    /// virtual controller to stop it.</para>
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
        public void AMouseLeadingDescriptorIsRefused()
            => Assert.True(HMaestroProfileCatalog.LeadsWithAPointingReport(Hex(Triton)));

        /// <summary>A keyboard first is the same defect: HID keyboard
        /// reports are what the frame would become.</summary>
        [Fact]
        public void AKeyboardLeadingDescriptorIsRefused()
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
        public void AGamepadWithReportIdsIsAllowed()
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
        public void ADescriptorWithNoReportIdsIsAllowed()
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
        public void AnEmptyDescriptorIsAllowed()
        {
            Assert.False(HMaestroProfileCatalog.LeadsWithAPointingReport((byte[])null));
            Assert.False(HMaestroProfileCatalog.LeadsWithAPointingReport(System.Array.Empty<byte>()));
        }
    }
}
