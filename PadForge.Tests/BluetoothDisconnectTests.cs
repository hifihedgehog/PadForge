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

        // ── Steam power-off report (SC2026 and 2015 Gordon) ──

        [Fact]
        public void SteamPowerOffReport_MagicForm_MatchesHandheldCompanionBytes()
        {
            // HandheldCompanion GordonController.cs TurnOff (2015 Gordon) sends
            // [0x9F, 0x04, 0x6f, 0x66, 0x66, 0x21] ("off!"), and the on-wire
            // feature buffer prepends report id 0x00 (SDL_hidapi_steam.c's
            // 0x00 + blob framing, mirrored by HapticToneService).
            byte[] buf = BluetoothLinkHelper.BuildSteamPowerOffReport(65, withOffMagic: true);
            Assert.Equal(65, buf.Length);
            Assert.Equal(0x00, buf[0]);
            Assert.Equal(0x9F, buf[1]);
            Assert.Equal(0x04, buf[2]);
            Assert.Equal((byte)'o', buf[3]);
            Assert.Equal((byte)'f', buf[4]);
            Assert.Equal((byte)'f', buf[5]);
            Assert.Equal((byte)'!', buf[6]);
            for (int i = 7; i < buf.Length; i++)
                Assert.Equal(0x00, buf[i]);
        }

        [Fact]
        public void SteamPowerOffReport_BareForm_MatchesSteamControllerTools()
        {
            // steam_controller_tools controller.ts turnOff (2026 controller)
            // sends the bare protocol id with a zero payload.
            byte[] buf = BluetoothLinkHelper.BuildSteamPowerOffReport(65, withOffMagic: false);
            Assert.Equal(65, buf.Length);
            Assert.Equal(0x00, buf[0]);
            Assert.Equal(0x9F, buf[1]);
            for (int i = 2; i < buf.Length; i++)
                Assert.Equal(0x00, buf[i]);
        }

        // ── SDL XInput-backend path parsing ──

        [Theory]
        [InlineData("XInput#0", 0u)]
        [InlineData("XInput#1", 1u)]
        [InlineData("XInput#3", 3u)]
        public void XInputSlot_ParsesSdlBackendPath(string path, uint expected)
        {
            // SDL_xinputjoystick.c:211: path = "XInput#%u" with the XInput
            // user index. The user's Xbox Series pad persists as "XInput#1".
            Assert.True(BluetoothLinkHelper.TryParseXInputSlot(path, out uint slot));
            Assert.Equal(expected, slot);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("XInput#4")]   // beyond XUSER_MAX_COUNT
        [InlineData("XInput#x")]
        [InlineData(@"\\?\HID#VID_054C&PID_0CE6#...")]
        public void XInputSlot_RejectsNonBackendPaths(string path)
        {
            Assert.False(BluetoothLinkHelper.TryParseXInputSlot(path, out _));
        }

        // ── DTO round-trip (the #112 persistence lane; a field missing here
        //    silently resets the action on every save/load) ──

        [Fact]
        public void DisconnectAction_SurvivesTheMacroDataRoundTrip()
        {
            var deviceGuid = Guid.NewGuid();
            var m = new PadForge.ViewModels.MacroItem { Name = "Chord" };
            m.Actions.Add(new PadForge.ViewModels.MacroAction
            {
                Type = PadForge.ViewModels.MacroActionType.DisconnectController,
                DisconnectTarget = PadForge.ViewModels.MacroDisconnectTarget.SpecificDevice,
                DisconnectDeviceGuid = deviceGuid,
            });

            var data = PadForge.Services.SettingsService.BuildMacroDataForMacro(m, 0);
            var clone = PadForge.Services.SettingsService.LoadMacroFromData(
                data, PadForge.Engine.VirtualControllerType.Xbox, null);

            Assert.Single(clone.Actions);
            Assert.Equal(PadForge.ViewModels.MacroActionType.DisconnectController, clone.Actions[0].Type);
            Assert.Equal(PadForge.ViewModels.MacroDisconnectTarget.SpecificDevice, clone.Actions[0].DisconnectTarget);
            Assert.Equal(deviceGuid, clone.Actions[0].DisconnectDeviceGuid);
        }

        [Fact]
        public void XInputCapabilitiesEx_AbiSizeIsPinned()
        {
            // 20-byte XINPUT_CAPABILITIES + WORD VID/PID/version/pad + DWORD,
            // per Special K include/SpecialK/input/xinput.h:162-169.
            Assert.Equal(32, BluetoothLinkHelper.CapabilitiesExSize);
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
