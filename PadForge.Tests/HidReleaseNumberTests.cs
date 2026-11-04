using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The release number of a USB device whose HIDD_ATTRIBUTES give
    /// 0, from its hardware IDs, as hidapi reads it (hid_internal_get_usb_info,
    /// windows/hid.c at c3509c1, lines 480-516).</summary>
    public class HidReleaseNumberTests
    {
        [Theory]
        [InlineData("USB\\VID_0B05&PID_184D&REV_0102&MI_00", "REV_", 0x0102)]
        [InlineData("usb\\vid_0b05&pid_184d&rev_0a0f&mi_00", "REV_", 0x0A0F)]
        [InlineData("USB\\VID_0B05&PID_184D&MI_00", "REV_", -1)]
        [InlineData("USB\\VID_0B05&PID_184D&REV_&MI_00", "REV_", -1)]
        [InlineData("USB\\VID_0B05&PID_184D&REV_XYZ", "REV_", -1)]
        [InlineData("USB\\VID_045E&PID_02FF&IG_00\\7&1", "IG_", 0)]
        [InlineData("USB\\VID_045E&PID_02FF&MI_00\\7&1", "IG_", -1)]
        public void AToken_ReadsItsHexDigits_OrNone(string id, string token, int value)
            => Assert.Equal(value, HidCollectionRuntime.TokenValue(id, token));

        [Fact]
        public void TheRelease_IsTheFirstHardwareIdThatNamesOne()
        {
            Assert.Equal(0x0102, HidCollectionRuntime.ReleaseFromHardwareIds(new[]
            {
                "USB\\VID_0B05&PID_184D&REV_0102&MI_00", "USB\\VID_0B05&PID_184D&MI_00",
            }));
            Assert.Equal(0x0200, HidCollectionRuntime.ReleaseFromHardwareIds(new[]
            {
                "USB\\VID_0B05&PID_184D&MI_00", "USB\\VID_0B05&PID_184D&REV_0000", "USB\\VID_0B05&PID_184D&REV_0200",
            }));
            Assert.Equal(0, HidCollectionRuntime.ReleaseFromHardwareIds(new[] { "USB\\VID_0B05&PID_184D&MI_00" }));
            Assert.Equal(0, HidCollectionRuntime.ReleaseFromHardwareIds(System.Array.Empty<string>()));
        }

        /// <summary>A parent's compatible IDs tell the bus: hidapi looks for USB
        /// anywhere in one, in upper case.</summary>
        [Fact]
        public void TheBus_IsUsbWhereACompatibleIdHoldsIt()
        {
            Assert.True(HidCollectionRuntime.IsUsb(new[] { "USB\\Class_03&SubClass_00&Prot_00", "USB\\Class_03" }));
            Assert.True(HidCollectionRuntime.IsUsb(new[] { "Other", "usb\\class_03" }));
            Assert.False(HidCollectionRuntime.IsUsb(new[] { "BTHENUM\\{00001124-0000-1000-8000-00805f9b34fb}" }));
            Assert.False(HidCollectionRuntime.IsUsb(System.Array.Empty<string>()));
        }
    }
}
