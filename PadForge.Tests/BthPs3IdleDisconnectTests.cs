using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The BthPS3 pads (DualShock 3, PS Move, PlayStation Move Navigation)
    /// never offered idle disconnect. They reach the host through a profile
    /// driver whose PDO path carries none of the markers the wireless
    /// predicate looks for, and nothing else identifies them as wireless.
    ///
    /// <para>Opening the gate on the path alone would have been worse than
    /// the gap: the disconnect targets a pad by the Bluetooth address kept
    /// in SerialNumber, and these three are the only pads without one,
    /// because they reach SDL as virtual joysticks and
    /// SDL_VirtualJoystickDesc has no serial field. Without an address the
    /// control would appear and do nothing. The gate therefore requires the
    /// address, and the address is stamped from the pad's own USB dock.</para>
    /// </summary>
    public class BthPs3IdleDisconnectTests
    {
        private const string Ds3Pdo =
            @"\?\bthps3bus#{53f88889-1aaf-4353-a047-556b69ec6da6}&dev&vid_054c&pid_0268#a&12248277&1&bthps3_device_01#{968e1849-73b1-4876-b80a-ed6dd171489b}";

        /// <summary>THE GAP. With an address known, the pad is a disconnect
        /// target like any other wireless pad.</summary>
        [Fact]
        public void BthPs3PadWithAnAddress_IsADisconnectTarget()
        {
            Assert.True(BluetoothLinkHelper.IsDisconnectTarget(
                Ds3Pdo, 0x054C, 0x0268, "0007040a1b7a"));
        }

        /// <summary>THE SAFETY PROPERTY. With no address there is nothing to
        /// target, so the control must not appear. This is exactly today's
        /// behaviour, which is why opening the gate cannot regress a pad
        /// whose address never arrives.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void BthPs3PadWithoutAnAddress_IsNotATarget(string serial)
        {
            Assert.False(BluetoothLinkHelper.IsDisconnectTarget(
                Ds3Pdo, 0x054C, 0x0268, serial));
        }

        /// <summary>An address alone does not make something wireless. A USB
        /// path stays out whatever the row happens to carry, so the pad on
        /// the cable never offers to disconnect itself.</summary>
        [Fact]
        public void AUsbPath_IsNeverATargetEvenWithAnAddress()
        {
            Assert.False(BluetoothLinkHelper.IsDisconnectTarget(
                @"\?\usb#vid_054c&pid_042f#6&6e5fe31&0&1#{b35924d6-3e16-4a9e-9782-5524a4b79bac}",
                0x054C, 0x042F, "0007040a1b7a"));
        }

        /// <summary>Every pad that was a target before still is, address or
        /// not. The BthPS3 clause is additive.</summary>
        [Theory]
        // Inbox Bluetooth HID (DualSense over BT).
        [InlineData(@"\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6#a&5d634de&0&0000", 0x054C, 0x0CE6)]
        // Switch 2, which the path cannot see and the VID/PID clause admits.
        [InlineData("", 0x057E, 0x2069)]
        // Combined Joy-Con pair, likewise.
        [InlineData("", 0x057E, 0x2008)]
        public void PreviouslyRecognisedPads_StillQualifyWithNoAddress(
            string path, ushort vid, ushort pid)
        {
            Assert.True(BluetoothLinkHelper.IsDisconnectTarget(path, vid, pid, null));
        }

        /// <summary>A Remote Link peer relays a real VID and PID, and this
        /// machine has no radio link to it. It stays excluded ahead of every
        /// other clause, including the new one.</summary>
        [Fact]
        public void ARemoteLinkPeer_StaysExcluded()
        {
            Assert.False(BluetoothLinkHelper.IsDisconnectTarget(
                "peer://desktop/bthps3bus", 0x054C, 0x0268, "0007040a1b7a"));
        }

        /// <summary>The path test names the profile driver, not a pad. Any
        /// device behind BthPS3 is covered, which is what makes this one
        /// change serve the DualShock 3, the Move and the Navigation pad
        /// rather than three separate patches.</summary>
        [Theory]
        [InlineData(@"\?\bthps3bus#{84957238-d867-421f-89c1-67847a3b55b5}&dev&vid_054c&pid_03d5#a&12248277&1&bthps3_device_02#{968e1849-73b1-4876-b80a-ed6dd171489b}", true)]
        [InlineData(@"\?\hid#vid_054c&pid_0ce6", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void TheBthPs3PathTest_NamesTheDriver(string path, bool expected)
        {
            Assert.Equal(expected, BluetoothLinkHelper.IsBthPs3Path(path));
        }
    }
}
