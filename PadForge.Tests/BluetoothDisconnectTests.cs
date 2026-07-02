using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;

namespace PadForge.Tests
{
    /// <summary>
    /// #162 Bluetooth disconnect: MAC parse, IOCTL payload byte order, and
    /// the idle truth table. The payload construction is pinned against
    /// DsHidMini's BluetoothHelper.DisconnectRemoteDevice
    /// ({0,0} ++ MAC, reversed, read little-endian) and DS4Windows'
    /// DS4Device.DisconnectBT (btAddr[5-i] = sbytes[i]).
    /// </summary>
    public class BluetoothDisconnectTests
    {
        // ── MAC parse ──

        [Theory]
        [InlineData("aa:bb:cc:dd:ee:ff")]
        [InlineData("AA:BB:CC:DD:EE:FF")]
        [InlineData("aa-bb-cc-dd-ee-ff")]
        [InlineData("aabbccddeeff")]
        public void ParseAddress_AcceptsAllSerialForms(string serial)
        {
            Assert.True(BluetoothLinkHelper.TryParseAddress(serial, out long address));
            Assert.Equal(0x0000aabbccddeeffL, address);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("aa:bb:cc:dd:ee")]        // five octets
        [InlineData("aa:bb:cc:dd:ee:ff:11")]  // seven octets
        [InlineData("not a mac address")]
        [InlineData("aabbccddeegg")]          // non-hex digit
        public void ParseAddress_RejectsGarbage(string serial)
        {
            Assert.False(BluetoothLinkHelper.TryParseAddress(serial, out _));
        }

        [Fact]
        public void ParseAddress_PayloadBytesMatchDsHidMiniConstruction()
        {
            // DsHidMini: {0x00,0x00} ++ GetAddressBytes(), reversed, then
            // BitConverter.ToUInt64 (little-endian). For aa:bb:cc:dd:ee:ff
            // the in-memory payload is ff ee dd cc bb aa 00 00.
            Assert.True(BluetoothLinkHelper.TryParseAddress("aa:bb:cc:dd:ee:ff", out long address));
            byte[] payload = BitConverter.GetBytes(address);
            Assert.Equal(new byte[] { 0xff, 0xee, 0xdd, 0xcc, 0xbb, 0xaa, 0x00, 0x00 }, payload);
        }

        [Fact]
        public void TryDisconnect_UnparseableSerialReturnsFalseWithoutTouchingRadio()
        {
            Assert.False(BluetoothLinkHelper.TryDisconnect("not a mac"));
            Assert.False(BluetoothLinkHelper.TryDisconnect(string.Empty));
            Assert.False(BluetoothLinkHelper.TryDisconnect(null));
        }

        // ── Idle truth table (gamepad absolute test) ──

        private static CustomInputState NeutralGamepadState()
        {
            var s = new CustomInputState();
            s.Axis[0] = 32767; s.Axis[1] = 32767; // left stick centered
            s.Axis[3] = 32767; s.Axis[4] = 32767; // right stick centered
            // triggers (axes 2/5) rest at 0; POVs default -1; buttons false
            return s;
        }

        [Fact]
        public void GamepadIdle_NeutralStateIsIdle()
        {
            Assert.True(IdleInputDetector.IsGamepadIdle(NeutralGamepadState()));
        }

        [Fact]
        public void GamepadIdle_ButtonDefeatsIdle()
        {
            var s = NeutralGamepadState();
            s.Buttons[0] = true;
            Assert.False(IdleInputDetector.IsGamepadIdle(s));
        }

        [Fact]
        public void GamepadIdle_PovDefeatsIdle()
        {
            var s = NeutralGamepadState();
            s.Povs[0] = 9000;
            Assert.False(IdleInputDetector.IsGamepadIdle(s));
        }

        [Theory]
        [InlineData(0)]  // LX
        [InlineData(1)]  // LY
        [InlineData(3)]  // RX
        [InlineData(4)]  // RY
        public void GamepadIdle_StickPastSlopDefeatsIdle(int axis)
        {
            var s = NeutralGamepadState();
            s.Axis[axis] = 32767 + IdleInputDetector.StickSlop + 1;
            Assert.False(IdleInputDetector.IsGamepadIdle(s));

            s = NeutralGamepadState();
            s.Axis[axis] = 32767 - IdleInputDetector.StickSlop - 1;
            Assert.False(IdleInputDetector.IsGamepadIdle(s));
        }

        [Fact]
        public void GamepadIdle_StickInsideSlopStaysIdle()
        {
            var s = NeutralGamepadState();
            s.Axis[0] = 32767 + IdleInputDetector.StickSlop - 1;
            Assert.True(IdleInputDetector.IsGamepadIdle(s));
        }

        [Theory]
        [InlineData(2)]  // LT
        [InlineData(5)]  // RT
        public void GamepadIdle_TriggerDefeatsIdle(int axis)
        {
            var s = NeutralGamepadState();
            s.Axis[axis] = IdleInputDetector.TriggerSlop + 1;
            Assert.False(IdleInputDetector.IsGamepadIdle(s));
        }

        [Fact]
        public void GamepadIdle_TouchpadFingerDefeatsIdle()
        {
            var s = NeutralGamepadState();
            s.Touchpads = new[] { new TouchpadInputState { FingerDown = new[] { true } } };
            Assert.False(IdleInputDetector.IsGamepadIdle(s));
        }

        // ── Idle truth table (generic change detection) ──

        [Fact]
        public void Unchanged_IdenticalStatesAreIdle()
        {
            Assert.True(IdleInputDetector.IsUnchanged(NeutralGamepadState(), NeutralGamepadState()));
        }

        [Fact]
        public void Unchanged_ButtonChangeDefeatsIdle()
        {
            var a = NeutralGamepadState();
            var b = NeutralGamepadState();
            b.Buttons[42] = true;
            Assert.False(IdleInputDetector.IsUnchanged(b, a));
        }

        [Fact]
        public void Unchanged_AxisJitterInsideSlopStaysIdle()
        {
            var a = NeutralGamepadState();
            var b = NeutralGamepadState();
            b.Axis[7] = a.Axis[7] + IdleInputDetector.DeltaSlop - 1;
            Assert.True(IdleInputDetector.IsUnchanged(b, a));
        }

        [Fact]
        public void Unchanged_AxisMovePastSlopDefeatsIdle()
        {
            var a = NeutralGamepadState();
            var b = NeutralGamepadState();
            b.Axis[7] = a.Axis[7] + IdleInputDetector.DeltaSlop + 1;
            Assert.False(IdleInputDetector.IsUnchanged(b, a));
        }
    }
}
