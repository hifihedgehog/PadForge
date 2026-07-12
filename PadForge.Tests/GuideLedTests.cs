using PadForge.Common.Input;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    /// <summary>Covers the #209 Guide button LED lane: the \\.\XboxGIP
    /// 23-byte LED packet against xbledctl's proven layout (GipHeader per
    /// docs/RESEARCH.md, payload per xone gip_pkt_led), the percent to
    /// 0-47 intensity scaling (MS-GIPUSB ceiling), the announce parser
    /// (gip_pkt_announce identity prefix), the Battery-mode brightness
    /// floor, the SDL Steam home-LED hint value form, and the macro
    /// action's DTO round-trip + enum-tail pin.</summary>
    public class GuideLedTests
    {
        // ── GIP LED packet, byte-exact vs the reference layout ──

        [Fact]
        public void LedPacket_Matches_XboxGip_Framing()
        {
            // GipHeader {u64 deviceId, u8 commandId 0x0A, u8 clientFlags
            // 0x20, u8 sequence, u8 0, u32 length 3, u32 0} + payload
            // {0x00 sub-command, mode, intensity} = 23 bytes
            // (xbledctl xbox_led.c xbox_set_led / docs/RESEARCH.md).
            var pkt = XboxGipGuideLedWriter.BuildLedPacket(
                0x1122334455667788UL, sequence: 5, mode: 0x01, intensity: 47);

            Assert.Equal(23, pkt.Length);
            // deviceId, little-endian at offset 0.
            Assert.Equal(new byte[] { 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11 },
                pkt[..8]);
            Assert.Equal(0x0A, pkt[8]);            // commandId = GIP_CMD_LED
            Assert.Equal(0x20, pkt[9]);            // clientFlags = GIP_OPT_INTERNAL
            Assert.Equal(5, pkt[10]);              // sequence
            Assert.Equal(0, pkt[11]);              // unknown1, always 0
            Assert.Equal(3u, System.BitConverter.ToUInt32(pkt, 12)); // payload length
            Assert.Equal(0u, System.BitConverter.ToUInt32(pkt, 16)); // unknown2, always 0
            Assert.Equal(0x00, pkt[20]);           // guide-LED sub-command
            Assert.Equal(0x01, pkt[21]);           // mode = on
            Assert.Equal(47, pkt[22]);             // intensity
        }

        // ── Percent to intensity (0-47 per MS-GIPUSB) ──

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 0)]      // rounds down, still distinct from mode-off
        [InlineData(50, 24)]    // 23.5 rounds half-up
        [InlineData(100, 47)]
        [InlineData(-5, 0)]     // clamps
        [InlineData(150, 47)]   // clamps
        public void Intensity_Scaling_Endpoints_And_Rounding(int percent, int expected)
        {
            Assert.Equal(expected, XboxGipGuideLedWriter.ScaleToIntensity(percent));
        }

        [Fact]
        public void Intensity_Scaling_Is_Monotone()
        {
            int prev = 0;
            for (int p = 0; p <= 100; p++)
            {
                int i = XboxGipGuideLedWriter.ScaleToIntensity(p);
                Assert.True(i >= prev, $"intensity fell at {p}%: {i} < {prev}");
                Assert.InRange(i, 0, 47);
                prev = i;
            }
        }

        [Fact]
        public void Percent_Zero_Turns_The_Led_Off()
        {
            // Mirrors xbledctl xbox_set_brightness: 0 sends LED_MODE_OFF.
            Assert.Equal(((byte)0x00, (byte)0), XboxGipGuideLedWriter.FromPercent(0));
            Assert.Equal(((byte)0x01, (byte)47), XboxGipGuideLedWriter.FromPercent(100));
        }

        // ── Announce parser (gip_pkt_announce, xone bus/protocol.c) ──

        private static byte[] BuildAnnounce(byte commandId, ulong deviceId,
            ushort vid, ushort pid, int payloadLen = 28)
        {
            // 20-byte GipHeader + payload. The announce payload identity
            // prefix is address[6] + le16 unknown + le16 vendor_id +
            // le16 product_id, so VID sits at payload offset 8 and PID
            // at offset 10.
            var buf = new byte[20 + payloadLen];
            for (int i = 0; i < 8; i++) buf[i] = (byte)(deviceId >> (8 * i));
            buf[8] = commandId;
            buf[12] = (byte)payloadLen;
            for (int i = 0; i < 6 && i < payloadLen; i++) buf[20 + i] = (byte)(0xA0 + i);
            if (payloadLen >= 12)
            {
                buf[28] = (byte)vid; buf[29] = (byte)(vid >> 8);
                buf[30] = (byte)pid; buf[31] = (byte)(pid >> 8);
            }
            return buf;
        }

        [Fact]
        public void Announce_Parser_Extracts_DeviceId_Vid_Pid_Address()
        {
            var buf = BuildAnnounce(0x02, 0xDEADBEEF12345678UL, 0x045E, 0x0B12);
            Assert.True(XboxGipGuideLedWriter.TryParseAnnounce(buf, buf.Length,
                out ulong deviceId, out ushort vid, out ushort pid, out ulong address));
            Assert.Equal(0xDEADBEEF12345678UL, deviceId);
            Assert.Equal(0x045E, vid);
            Assert.Equal(0x0B12, pid);
            Assert.Equal(0xA5A4A3A2A1A0UL, address); // address[6], little-endian
        }

        [Fact]
        public void Announce_Parser_Accepts_Acknowledge_By_Header_DeviceId()
        {
            // xbledctl parity (xbox_led.c:71): a 0x01 acknowledge carries a
            // usable deviceId in its header and MUST be accepted. It has no
            // identity payload, so VID/PID come back 0 and ProcessPending
            // falls back to every discovered deviceId. Rejecting it was the
            // bug that shipped the feature dead.
            var ack = BuildAnnounce(0x01, 0xAABBCCDD11223344UL, 0x045E, 0x0B12);
            Assert.True(XboxGipGuideLedWriter.TryParseAnnounce(ack, ack.Length,
                out ulong deviceId, out ushort vid, out ushort pid, out _));
            Assert.Equal(0xAABBCCDD11223344UL, deviceId);
            Assert.Equal(0, vid);
            Assert.Equal(0, pid);
        }

        [Fact]
        public void Announce_Parser_Accepts_Announce_Without_Identity_Payload()
        {
            // A 0x02 announce framed without the assumed VID/PID payload is
            // still a valid device announce: take the header deviceId, leave
            // VID/PID 0.
            var shortMsg = BuildAnnounce(0x02, 0x1122334455667788UL, 0, 0, payloadLen: 4);
            Assert.True(XboxGipGuideLedWriter.TryParseAnnounce(shortMsg, shortMsg.Length,
                out ulong deviceId, out ushort vid, out ushort pid, out _));
            Assert.Equal(0x1122334455667788UL, deviceId);
            Assert.Equal(0, vid);
            Assert.Equal(0, pid);
        }

        // ── Write-target selection (which announced pads a request hits) ──

        private static KeyValuePair<ulong, (ushort Vid, ushort Pid, ulong Address, long LastSeen)>
            Announced(ulong deviceId, ushort vid, ushort pid)
            => new(deviceId, (vid, pid, 0UL, 0L));

        [Fact]
        public void Targets_Direct_Identity_Match_Wins()
        {
            var targets = XboxGipGuideLedWriter.SelectWriteTargets(
                (0x045E, 0x0B12),
                new[] { Announced(1, 0x045E, 0x0B12), Announced(2, 0x045E, 0x02EA) },
                out bool fellBack);
            Assert.Equal(new ulong[] { 1 }, targets);
            Assert.False(fellBack);
        }

        [Fact]
        public void Targets_Fall_Back_To_Every_Announce_On_Identity_Mismatch()
        {
            // The LOAD-BEARING path, bench-proven 2026-07-12: SDL's XInput
            // lane synthesizes a generic PID (0x02FF observed) while the GIP
            // announce carries the real one (0x0B12), so a direct match can
            // never be required. A request that matches nothing writes to
            // every announced pad, identified or not.
            var targets = XboxGipGuideLedWriter.SelectWriteTargets(
                (0x045E, 0x02FF),
                new[] { Announced(1, 0x045E, 0x0B12), Announced(7, 0, 0) },
                out bool fellBack);
            Assert.Equal(new ulong[] { 1, 7 }, targets);
            Assert.True(fellBack);
        }

        [Fact]
        public void Targets_Empty_When_Nothing_Announced()
        {
            var targets = XboxGipGuideLedWriter.SelectWriteTargets(
                (0x045E, 0x0B12),
                System.Array.Empty<KeyValuePair<ulong, (ushort, ushort, ulong, long)>>(),
                out bool fellBack);
            Assert.Empty(targets);
            Assert.False(fellBack);
        }

        [Fact]
        public void Announce_Parser_Rejects_Unknown_Command_And_Short_Header()
        {
            // A non-announce command (not 0x01 or 0x02) is not a device
            // message.
            var other = BuildAnnounce(0x09, 1, 0x045E, 0x0B12);
            Assert.False(XboxGipGuideLedWriter.TryParseAnnounce(other, other.Length,
                out _, out _, out _, out _));

            // Header alone (shorter than the 20-byte GipHeader).
            Assert.False(XboxGipGuideLedWriter.TryParseAnnounce(new byte[10], 10,
                out _, out _, out _, out _));
        }

        // ── Battery mode mapping (fuller is brighter, floor 10) ──

        [Theory]
        [InlineData(-1, -1)]   // unknown battery: skip the write
        [InlineData(0, 10)]    // floor keeps a dead battery visible
        [InlineData(9, 10)]
        [InlineData(10, 10)]
        [InlineData(55, 55)]
        [InlineData(100, 100)]
        public void Battery_Maps_To_Brightness_With_Floor(int battery, int expected)
        {
            Assert.Equal(expected,
                XboxGipGuideLedWriter.BatteryToBrightnessPercent(battery));
        }

        // ── Steam home-LED hint (SDL_HomeLEDHintChanged value form) ──

        [Theory]
        [InlineData(0, "0.00")]
        [InlineData(50, "0.50")]
        [InlineData(100, "1.00")]
        [InlineData(7, "0.07")]
        public void Steam_Hint_Value_Is_Always_Dotted(int percent, string expected)
        {
            // A dotless "50" would take SDL_HomeLEDHintChanged's boolean
            // branch and read as 100, so the setter always writes the
            // float 0..1 form.
            Assert.Equal(expected, SteamHomeLedSetter.FormatHintValue(percent));
        }

        [Theory]
        [InlineData(0x28DE, 0x1101, true)]  // legacy (CHELL)
        [InlineData(0x28DE, 0x1102, true)]  // wired (D0G)
        [InlineData(0x28DE, 0x1105, true)]  // Bluetooth (D0G)
        [InlineData(0x28DE, 0x1106, true)]  // Bluetooth (D0G)
        [InlineData(0x28DE, 0x1142, true)]  // wireless dongle
        [InlineData(0x28DE, 0x1205, false)] // not the 2015 family
        [InlineData(0x045E, 0x1102, false)] // wrong vendor
        public void Steam_2015_Pid_Gate_Matches_Sdl_Controller_List(
            int vid, int pid, bool expected)
        {
            Assert.Equal(expected,
                SteamHomeLedSetter.IsSteamController2015((ushort)vid, (ushort)pid));
        }

        // ── Macro action: DTO round-trip + enum-tail pin ──

        [Fact]
        public void GuideLed_Action_Rides_The_Macro_Dto_RoundTrip()
        {
            // Same trap as PointerModeSet: settings XML, the macro
            // clipboard, and Duplicate all funnel through the ActionData
            // DTO, and a field missing there silently resets on reload.
            var m = new MacroItem { Name = "GuideLed" };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.GuideLedBrightness,
                GuideLedPercent = 35,
            });
            var data = PadForge.Services.SettingsService.BuildMacroDataForMacro(m, 0);
            var clone = PadForge.Services.SettingsService.LoadMacroFromData(
                data, PadForge.Engine.VirtualControllerType.Xbox, null);

            Assert.Equal(MacroActionType.GuideLedBrightness, clone.Actions[0].Type);
            Assert.Equal(35, clone.Actions[0].GuideLedPercent);
        }

        [Fact]
        public void GuideLed_Action_Sits_At_The_Enum_Tail()
        {
            // APPEND-ONLY enum: the clipboard serializes ints.
            var values = (MacroActionType[])System.Enum.GetValues(typeof(MacroActionType));
            int max = 0;
            foreach (var v in values) max = System.Math.Max(max, (int)v);
            Assert.Equal((int)MacroActionType.GuideLedBrightness, max);
        }

        [Fact]
        public void GuideLed_Percent_Clamps_On_The_Vm()
        {
            var a = new MacroAction { GuideLedPercent = 250 };
            Assert.Equal(100, a.GuideLedPercent);
            a.GuideLedPercent = -3;
            Assert.Equal(0, a.GuideLedPercent);
        }

        // ── Slot config defaults (#209): DeviceDefault writes nothing ──

        [Fact]
        public void SlotConfig_Defaults_To_DeviceDefault_100()
        {
            var cfg = new DeviceSlotConfig();
            Assert.Equal(GuideLedMode.DeviceDefault, cfg.GuideLedMode);
            Assert.Equal(100, cfg.GuideLedBrightness);
            Assert.False(cfg.IsGuideLedFixed);
        }

        [Fact]
        public void SlotConfig_GuideLed_Rides_The_Dto_RoundTrip()
        {
            var cfg = new DeviceSlotConfig
            {
                GuideLedMode = GuideLedMode.Battery,
                GuideLedBrightness = 40,
            };
            var data = new DeviceSlotConfigData
            {
                GuideLedMode = cfg.GuideLedMode,
                GuideLedBrightness = cfg.GuideLedBrightness,
            };
            var restored = new DeviceSlotConfig();
            PadForge.Services.SettingsService.ApplyDeviceSlotConfigData(restored, data);
            Assert.Equal(GuideLedMode.Battery, restored.GuideLedMode);
            Assert.Equal(40, restored.GuideLedBrightness);
        }
    }
}
