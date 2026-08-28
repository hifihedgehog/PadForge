using System.Linq;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// What a hand-packed frame needs from HIDMaestro to reach the wire on
    /// the report it was built for.
    ///
    /// <para>HIDMaestro frames a raw submission into the descriptor's FIRST
    /// input report unless the profile declares its own id AND is always
    /// armed, in which case the frame goes out verbatim (HM v1.7.1,
    /// HIDMaestro#58). Before that release the 2026 Steam Controller had no
    /// verbatim path, and its descriptor leads with the lizard-mode mouse
    /// (report 0x40) ahead of the controller state (0x42), so every frame
    /// arrived as a mouse report and the rolling sequence number in byte 1
    /// drove the pointer 250 times a second.</para>
    ///
    /// <para>These pin the pairing that keeps that shut: a packer's frame
    /// carries its own report id exactly when the profile says it does, and
    /// a profile that leads with a pointing report is only ever safe on the
    /// verbatim path.</para>
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

        /// <summary>Every packer's frame length equals the input report its
        /// profile declares, so the frame carries the report id and takes
        /// HIDMaestro's verbatim path. A packer one byte short is the
        /// data-only form, and on a pointing-led descriptor that form is the
        /// mouse.</summary>
        [Fact]
        public void PackerFramesCarryTheirOwnReportId()
        {
            foreach (var p in HMaestroProfileCatalog.AllProfiles)
            {
                var packer = ValveReportPackers.ForProfile(p.Id);
                if (packer == null) continue;
                Assert.Equal(p.InputReportSize, packer.Size);
            }
        }

        /// <summary>A profile that leads with a mouse or a keyboard is safe
        /// ONLY on the verbatim path, which HIDMaestro takes when the
        /// profile declares a non-zero report id and is always armed. Any
        /// other pointing-led profile carrying a packer would be framed into
        /// that pointing report, which is the 2026 Steam Controller's
        /// original defect.</summary>
        [Fact]
        public void APointingLedPackerProfileTakesTheVerbatimPath()
        {
            foreach (var p in HMaestroProfileCatalog.AllProfiles)
            {
                if (ValveReportPackers.ForProfile(p.Id) == null) continue;
                if (!HMaestroProfileCatalog.LeadsWithAPointingReport(p)) continue;

                var spec = p.ExtendedReport;
                Assert.True(spec != null && spec.AlwaysArmed && spec.ReportIdByte != 0,
                    $"{p.Id} packs a frame while its descriptor leads with a pointing report, "
                    + "and it does not declare the always-armed report id that makes HIDMaestro "
                    + "emit the frame verbatim, so every frame would drive the pointer");
            }
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
