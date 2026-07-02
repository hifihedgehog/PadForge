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
        public void SteamPowerOffReport_GordonForm_MatchesHandheldCompanionBytes()
        {
            // HandheldCompanion GordonController.cs TurnOff (2015 Gordon) sends
            // [0x9F, 0x04, 0x6f, 0x66, 0x66, 0x21] ("off!") in a 65-byte
            // report-id-0 buffer (SDL_hidapi_steamdeck.c:98 shape).
            byte[] buf = BluetoothLinkHelper.BuildSteamPowerOffReport(65, 0x00, withOffMagic: true);
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
        public void SteamPowerOffReport_TritonForm_MatchesSdlLizardFraming()
        {
            // The 2026 Triton takes a 64-byte report-id-1 buffer
            // (SDL_hidapi_steam_triton.c:130, buffer[64] = { 1 }) with the
            // bare protocol id per steam_controller_tools controller.ts:204.
            // Report id 0 on this collection fails with
            // ERROR_INVALID_PARAMETER, traced on hardware 2026-07-02.
            byte[] buf = BluetoothLinkHelper.BuildSteamPowerOffReport(64, 0x01, withOffMagic: false);
            Assert.Equal(64, buf.Length);
            Assert.Equal(0x01, buf[0]);
            Assert.Equal(0x9F, buf[1]);
            for (int i = 2; i < buf.Length; i++)
                Assert.Equal(0x00, buf[i]);
        }

        // ── SDL XInput-backend path parsing ──

        [Theory]
        [InlineData("XInput#0", 0u)]
        [InlineData("XInput#1", 1u)]
        [InlineData("XInput#3", 3u)]
        [InlineData("XInput#4", 4u)]   // SDL enumerates 16 slots (SDL_xinput.h:45)
        [InlineData("XInput#15", 15u)]
        public void XInputSlot_ParsesSdlBackendPath(string path, uint expected)
        {
            // SDL_xinputjoystick.c:211: path = "XInput#%u" with the XInput
            // user index. SDL's own XUSER_MAX_COUNT is 16 (SDL_xinput.h:45)
            // and the bundled OpenXInput fork is built to match, so the full
            // range must parse. The user's Xbox Series pad persists as
            // "XInput#1".
            Assert.True(BluetoothLinkHelper.TryParseXInputSlot(path, out uint slot));
            Assert.Equal(expected, slot);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("XInput#16")]  // beyond SDL's XUSER_MAX_COUNT of 16
        [InlineData("XInput#x")]
        [InlineData(@"\\?\HID#VID_054C&PID_0CE6#...")]
        public void XInputSlot_RejectsNonBackendPaths(string path)
        {
            Assert.False(BluetoothLinkHelper.TryParseXInputSlot(path, out _));
        }

        // ── Switch 2 BLE shutdown ──

        [Fact]
        public void Switch2Shutdown_MatchesResearchWireExample()
        {
            // switch2_controller_research commands.md, command 0x06 subcmd
            // 0x02: header 06 91 xx 02 00 0C 00 00 + 12 zero bytes, with the
            // transport byte 0x01 for Bluetooth (commands.md Command Header).
            byte[] cmd = BluetoothLinkHelper.BuildSwitch2ShutdownCommand();
            Assert.Equal(20, cmd.Length);
            Assert.Equal(0x06, cmd[0]); // command id
            Assert.Equal(0x91, cmd[1]); // host -> device
            Assert.Equal(0x01, cmd[2]); // transport: Bluetooth
            Assert.Equal(0x02, cmd[3]); // subcommand: shutdown
            Assert.Equal(0x00, cmd[4]);
            Assert.Equal(0x0C, cmd[5]); // payload length
            for (int i = 6; i < cmd.Length; i++)
                Assert.Equal(0x00, cmd[i]);
        }

        [Theory]
        [InlineData(0x2066)] // Joy-Con 2 (Right)
        [InlineData(0x2067)] // Joy-Con 2 (Left)
        [InlineData(0x2068)] // Joy-Con 2 pair
        [InlineData(0x2069)] // Switch 2 Pro
        [InlineData(0x2073)] // NSO GameCube
        public void Switch2Family_MirrorsSdlUsbIds(int pid)
        {
            // The full family from SDL usb_ids.h:126-130, never a subset.
            Assert.True(BluetoothLinkHelper.IsSwitch2(0x057E, (ushort)pid));
        }

        [Fact]
        public void Switch2Family_RejectsGen1AndOtherVendors()
        {
            Assert.False(BluetoothLinkHelper.IsSwitch2(0x057E, 0x2009)); // gen-1 Switch Pro
            Assert.False(BluetoothLinkHelper.IsSwitch2(0x054C, 0x2069)); // wrong vendor
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

        // ── Idle truth table: post-3.5.0 pointer/mouse families (#146/#154) ──

        [Fact]
        public void GamepadIdle_WiiIrPointerDefeatsIdle()
        {
            var s = NeutralGamepadState();
            s.Ir = new WiiIrState { X = 0.1f, Y = 0f, Detected = true };
            Assert.False(IdleInputDetector.IsGamepadIdle(s));
        }

        [Fact]
        public void GamepadIdle_JoyCon2MouseMotionDefeatsIdle()
        {
            var s = NeutralGamepadState();
            s.JoyCon2MouseDY = 3f;
            Assert.False(IdleInputDetector.IsGamepadIdle(s));
        }

        [Fact]
        public void GamepadIdle_NirIntensityAloneStaysIdle()
        {
            // The NIR proximity scalar is ambient (never settles to a rest
            // value), excluded like the motion sensors.
            var s = NeutralGamepadState();
            s.JoyConIrIntensity = 0.6f;
            Assert.True(IdleInputDetector.IsGamepadIdle(s));
        }

        [Fact]
        public void Unchanged_WiiIrPointerDefeatsIdle()
        {
            var a = NeutralGamepadState();
            var b = NeutralGamepadState();
            b.Ir = new WiiIrState { X = 0f, Y = 0f, Detected = true };
            Assert.False(IdleInputDetector.IsUnchanged(b, a));
        }
    }
}
