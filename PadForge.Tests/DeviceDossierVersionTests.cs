using PadForge.ViewModels;

namespace PadForge.Tests
{
    /// <summary>The Device Dossier's FIRMWARE and REV lines.</summary>
    public class DeviceDossierVersionTests
    {
        /// <summary>Sony firmware reads as dualsense-tester prints the update
        /// version (high byte, dot, low byte, in hex). Microsoft and Nintendo
        /// values read as decimal major.minor (hifihedgehog/SDL#42: Xbox
        /// 5.5.2641.0 is 0x0505, a GIP hello for 5.17 is 0x0511, Switch 3.72
        /// is 0x0348). Other makers' values print as raw hex, and 0 hides the
        /// line.</summary>
        [Theory]
        [InlineData(0x0224, 0x054C, "2.24")]
        [InlineData(0x0110, 0x054C, "1.10")]
        [InlineData(0x0505, 0x045E, "5.5")]
        [InlineData(0x0511, 0x045E, "5.17")]
        [InlineData(0x0348, 0x057E, "3.72")]
        [InlineData(0x1234, 0x20D6, "0x1234")]
        [InlineData(0, 0x045E, "")]
        [InlineData(0, 0x054C, "")]
        public void Firmware_ReadsTheWayItsMakerPrintsIt(int version, int vendor, string expected)
            => Assert.Equal(expected, DeviceRowViewModel.FormatFirmwareVersion((ushort)version, (ushort)vendor));

        [Theory]
        [InlineData(0x0100, "0x0100")]
        [InlineData(0, "")]
        public void Revision_PrintsAsUsbToolsPrintIt(int version, string expected)
            => Assert.Equal(expected, DeviceRowViewModel.FormatProductRevision((ushort)version));
    }
}
