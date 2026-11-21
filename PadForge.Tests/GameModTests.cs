using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    [CollectionDefinition("GameModStatics", DisableParallelization = true)]
    public class GameModStaticsCollection { }

    /// <summary>
    /// DSX game mods (#529): the protocol, the trigger encodings, the holds
    /// and their releases, the effects dispatcher's use of them, the server,
    /// the port files and the DSX.exe stand-in. The holds are process-wide,
    /// so these run alone.
    /// </summary>
    [Collection("GameModStatics")]
    public sealed class GameModTests : IDisposable
    {
        public GameModTests()
        {
            GameModEffects.ResetForTest();
            GameModStatusDevices.ResetForTest();
            // Controller 0 is the virtual controller at pad index 7, and
            // controller 1 the one at pad index 6. Other tests write game
            // state to pads 10 to 15.
            GameModEffects.OrderedSlotsOverrideForTest = () => new[] { 7, 6 };
        }

        public void Dispose()
        {
            GameModEffects.ResetForTest();
            GameModStatusDevices.ResetForTest();
        }

        private static readonly GameModSender Mod = new(40000, 0, "Mod.exe", null, Environment.TickCount64);
        private static readonly GameModSender Other = new(40001, 0, "Other.exe", null, Environment.TickCount64);

        private static void Send(string packet, GameModSender sender = null)
        {
            var instructions = GameModProtocol.Parse(Encoding.ASCII.GetBytes(packet));
            Assert.NotNull(instructions);
            foreach (var instruction in instructions)
                GameModEffects.Apply(instruction, sender ?? Mod);
        }

        private static byte[] Encode(int mode, params int[] parameters)
        {
            var block = new byte[11];
            Assert.True(GameModProtocol.TryEncodeTrigger(mode, parameters, block), "mode " + mode);
            return block;
        }

        private static GameModEffects.Hold Controller0 => GameModEffects.ForController(0);

        private static readonly byte[] Off = { 0x05, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        // ── The packets mods send (DSZ's per-mod packets, udp.rs tests) ──

        [Fact]
        public void TarkovDsx_ResistancePlayerLightsAndAZeroThreshold()
        {
            Send(@"{""instructions"":[{""type"":1,""parameters"":[0,2,13,0,8]},{""type"":6,""parameters"":[0,2]},{""type"":4,""parameters"":[0,2,0]}]}");
            Assert.Equal(new byte[] { 0x21, 0xFF, 0x03, 0xFF, 0xFF, 0xFF, 0x3F, 0, 0, 0, 0 }, Controller0.RightTrigger);
            Assert.Equal((byte)0x35, Controller0.PlayerLights);
            Assert.False(Controller0.HasThresholds);
        }

        [Fact]
        public void DsxFallout4_StringParameters_CustomVibrateResistanceAndRgbWithBrightness()
        {
            Send(@"{""instructions"":[{""type"":1,""parameters"":[""0"",""1"",""12"",""9"",""40"",""200"",""0"",""0"",""0"",""0"",""0""]},{""type"":2,""parameters"":[""0"",""255"",""64"",""0"",""128""]}]}");
            Assert.Equal(new byte[] { 0x06, 40, 200, 0, 0, 0, 0, 0, 0, 0, 0 }, Controller0.LeftTrigger);
            Assert.Equal(new byte[] { 128, 32, 0 }, Controller0.Lightbar);
        }

        [Fact]
        public void DsxSkyrimNg_NormalWithParametersIsOff_AndMicPulse()
        {
            Send(@"{""instructions"":[{""type"":1,""parameters"":[""0"",""1"",""0"",""0"",""0"",""0"",""0""]},{""type"":5,""parameters"":[""0"",""1""]}]}");
            Assert.Equal(Off, Controller0.LeftTrigger);
            Assert.Equal((byte)2, Controller0.MuteLight);
        }

        [Fact]
        public void DualSense4Rockstar_BowAndAThreshold()
        {
            Send(@"{""instructions"":[{""type"":1,""parameters"":[0,2,14,0,8,2,5]},{""type"":4,""parameters"":[0,2,120]}]}");
            Assert.Equal(new byte[] { 0x22, 0x01, 0x01, 0x21, 0x00, 0, 0, 0, 0, 0, 0 }, Controller0.RightTrigger);
            Assert.Equal((byte)120, Controller0.RightThreshold);
            Assert.Equal((byte)0, Controller0.LeftThreshold);
        }

        [Fact]
        public void Wf2Dsx_AutomaticGunRgbWithoutBrightnessAndBooleanPlayerLights()
        {
            Send(@"{""instructions"":[{""type"":1,""parameters"":[0,1,17,0,6,12]},{""type"":2,""parameters"":[0,0,30,70]},{""type"":3,""parameters"":[0,true,false,true,false,true]}]}");
            Assert.Equal(new byte[] { 0x26, 0xFF, 0x03, 0x6D, 0xDB, 0xB6, 0x2D, 0, 0, 12, 0 }, Controller0.LeftTrigger);
            Assert.Equal(new byte[] { 0, 30, 70 }, Controller0.Lightbar);
            Assert.Equal((byte)(0b10101 | 0x20), Controller0.PlayerLights);
        }

        [Fact]
        public void ForzaDsxLegacy_TheTestSequenceOfModes()
        {
            Assert.Equal(new byte[] { 0x26, 0xFF, 0x03, 0xFF, 0xFF, 0xFF, 0x3F, 0, 0, 10, 0 }, Encode(8, 10));
            Assert.Equal(new byte[] { 0x26, 0, 101, 255, 255, 0, 0, 0, 0, 0, 0 }, Encode(12, 8, 0, 101, 255, 255, 0, 0, 0));
            Assert.Equal(new byte[] { 0x23, 0x01, 0x02, 0x14, 10, 0, 0, 0, 0, 0, 0 }, Encode(15, 0, 9, 2, 4, 10));
            Assert.Equal(new byte[] { 0x25, 0x84, 0x00, 7, 0, 0, 0, 0, 0, 0, 0 }, Encode(16, 2, 7, 8));
            Assert.Equal(new byte[] { 0x27, 0x01, 0x02, 0x3F, 10, 0, 0, 0, 0, 0, 0 }, Encode(18, 0, 9, 7, 7, 10, 0));
            Send(@"{""instructions"":[{""type"":1,""parameters"":[0,1,8,10]},{""type"":1,""parameters"":[0,1,12,8,0,101,255,255,0,0,0]},{""type"":1,""parameters"":[0,1,15,0,9,2,4,10]},{""type"":1,""parameters"":[0,1,16,2,7,8]},{""type"":1,""parameters"":[0,1,18,0,9,7,7,10,0]}]}");
            Assert.Equal(Encode(18, 0, 9, 7, 7, 10, 0), Controller0.LeftTrigger);
        }

        [Fact]
        public void RbrAdaptiveTrigger_AResetInTheSamePacketEndsWhatItSet()
        {
            Send(@"{""instructions"": [{""type"": 1, ""parameters"": [0, 2, 17, 0, 5, 30]}, {""type"": 7, ""parameters"": [0]}]}");
            Assert.Null(Controller0);
            Assert.False(GameModEffects.AnyHeld);
            Assert.Equal(new byte[] { 0x26, 0xFF, 0x03, 0x24, 0x49, 0x92, 0x24, 0, 0, 30, 0 }, Encode(17, 0, 5, 30));
        }

        [Fact]
        public void RaceElement_StatusFeedbackVibrationAndCustomModes()
        {
            Send(@"{""instructions"":[{""type"":0,""parameters"":[]},{""type"":1,""parameters"":[0,1,21,1,5]},{""type"":1,""parameters"":[0,2,23,0,6,40]},{""type"":1,""parameters"":[0,1,12,11,60,180,0,0,0,0,0]},{""type"":1,""parameters"":[0,1,12,2,60,180,0,0,0,0,0]}]}");
            Assert.Equal(new byte[] { 0x21, 0xFE, 0x03, 0x20, 0x49, 0x92, 0x24, 0, 0, 0, 0 }, Encode(21, 1, 5));
            Assert.Equal(new byte[] { 0x06, 60, 180, 0, 0, 0, 0, 0, 0, 0, 0 }, Encode(12, 11, 60, 180, 0, 0, 0, 0, 0));
            Assert.Equal(new byte[] { 0x26, 0xFF, 0x03, 0x6D, 0xDB, 0xB6, 0x2D, 0, 0, 40, 0 }, Controller0.RightTrigger);
            Assert.Equal(new byte[] { 0x21, 60, 180, 0, 0, 0, 0, 0, 0, 0, 0 }, Controller0.LeftTrigger);
        }

        [Fact]
        public void DsxPp_ResistanceThenTheNamedPresets()
        {
            Send(@"{""instructions"":[{""type"":1,""parameters"":[0,2,13,3,6]},{""type"":1,""parameters"":[0,1,7]},{""type"":1,""parameters"":[0,1,1]}]}");
            Assert.Equal(new byte[] { 0x21, 0xF8, 0x03, 0x00, 0xDA, 0xB6, 0x2D, 0, 0, 0, 0 }, Controller0.RightTrigger);
            Assert.Equal(new byte[] { 0x02, 144, 160, 255, 0, 0, 0, 0, 0, 0, 0 }, Controller0.LeftTrigger);
            Assert.Equal(new byte[] { 0x01, 0, 255, 0, 0, 0, 0, 0, 0, 0, 0 }, Encode(7));
        }

        [Fact]
        public void GodotDualSenseXSupport_StatusWithoutParametersThenLights()
        {
            var status = GameModProtocol.Parse(Encoding.ASCII.GetBytes(@"{""instructions"":[{""type"":0}]}"));
            Assert.Equal(GameModProtocol.GetDsxStatus, Assert.Single(status).Type);
            Send(@"{""instructions"":[{""type"":2,""parameters"":[0,255,255,255,255]},{""type"":6,""parameters"":[0,5]},{""type"":5,""parameters"":[0,2]}]}");
            Assert.Equal(new byte[] { 255, 255, 255 }, Controller0.Lightbar);
            Assert.Equal((byte)0x20, Controller0.PlayerLights);
            Assert.Equal((byte)0, Controller0.MuteLight);
        }

        // ── Parsing ──

        [Fact]
        public void TheV2Example_UnusedEntriesAreStatusWithNullParameters()
        {
            // new Instruction[7] with three filled, as Newtonsoft writes it.
            string packet = @"{""instructions"":[{""type"":1,""parameters"":[0,2,2]},{""type"":4,""parameters"":[0,2,0]},{""type"":2,""parameters"":[0,255,255,255]},"
                + @"{""type"":0,""parameters"":null},{""type"":0,""parameters"":null},{""type"":0,""parameters"":null},{""type"":0,""parameters"":null}]}";
            var instructions = GameModProtocol.Parse(Encoding.ASCII.GetBytes(packet));
            Assert.Equal(7, instructions.Count);
            Assert.Empty(instructions[6].Parameters);
            foreach (var i in instructions) GameModEffects.Apply(i, Mod);
            Assert.Equal(new byte[] { 0x02, 128, 160, 255, 0, 0, 0, 0, 0, 0, 0 }, Controller0.RightTrigger);
            Assert.Equal(new byte[] { 255, 255, 255 }, Controller0.Lightbar);
        }

        [Fact]
        public void TheV31Example_ShiftedCustomPacket_ReadsAsDsxReadsIt()
        {
            // AddCustomAdaptiveTriggerToPacket writes the values from index 3,
            // over the custom mode, and leaves a null at the end.
            var instructions = GameModProtocol.Parse(Encoding.ASCII.GetBytes(
                @"{""instructions"":[{""type"":1,""parameters"":[0,1,12,0,101,255,255,0,0,0,null]}]}"));
            Assert.Equal(new[] { 0, 1, 12, 0, 101, 255, 255, 0, 0, 0, 0 }, Assert.Single(instructions).Parameters);
            GameModEffects.Apply(instructions[0], Mod);
            Assert.Equal(new byte[] { 0x00, 101, 255, 255, 0, 0, 0, 0, 0, 0, 0 }, Controller0.LeftTrigger);
        }

        [Theory]
        [InlineData(@"{""Instructions"":[{""Type"":""TriggerUpdate"",""Parameters"":[0,2,13,0,8]}]}", 1)]
        [InlineData(@"{""instructions"":[{""type"":""triggerupdate"",""parameters"":[0,2,13,0,8]}]}", 1)]
        [InlineData(@"{""instructions"":[{""type"":""1"",""parameters"":[0,2,13,0,8]}]}", 1)]
        [InlineData(@"{""instructions"":[{""type"":""Invalid""}]}", 0)]
        [InlineData(@"{""instructions"":[{""type"":7.0,""parameters"":[0]}]}", 7)]
        [InlineData("\uFEFF{\"instructions\":[{\"type\":2,\"parameters\":[0,1,2,3]},]}\0\0", 2)]
        public void TypesAreNumbersOrNames_KeysMatchAnyCase(string packet, int type)
            => Assert.Equal(type, Assert.Single(GameModProtocol.Parse(Encoding.UTF8.GetBytes(packet))).Type);

        [Fact]
        public void ParametersReadAsTheMostLenientServerReadsThem()
        {
            var i = Assert.Single(GameModProtocol.Parse(Encoding.ASCII.GetBytes(
                @"{""instructions"":[{""type"":3,""parameters"":[0,true,false,""1"",""true"",12.9,-3,null,{},[1],""Left""]}]}")));
            Assert.Equal(new[] { 0, 1, 0, 1, 1, 12, -3, 0, 0, 0, 0 }, i.Parameters);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("[1,2,3]")]
        [InlineData(@"{""commands"":[]}")]
        [InlineData(@"{""instructions"":{}}")]
        public void ANonPacket_ParsesToNull(string packet)
            => Assert.Null(GameModProtocol.Parse(Encoding.ASCII.GetBytes(packet)));

        [Fact]
        public void AnInstructionWithoutAReadableType_IsSkipped_TheRestApply()
        {
            var list = GameModProtocol.Parse(Encoding.ASCII.GetBytes(
                @"{""instructions"":[{""type"":""Bogus"",""parameters"":[0]},5,{""parameters"":[0]},{""type"":5,""parameters"":[0,0]}]}"));
            Assert.Equal(GameModProtocol.MicLed, Assert.Single(list).Type);
        }

        // ── Encodings ──

        [Fact]
        public void TheV3Modes_FollowNielk1()
        {
            Assert.Equal(Off, Encode(20));
            Assert.Equal(Off, Encode(0));
            Assert.Equal(new byte[] { 0x21, 0xFE, 0x03, 0xB8, 0xCB, 0x4D, 0x01, 0, 0, 0, 0 }, Encode(24, 1, 9, 8, 1));
            Assert.Equal(new byte[] { 0x21, 0xC7, 0x00, 0xFF, 0x01, 0xFC, 0x00, 0, 0, 0, 0 }, Encode(25, 8, 8, 8, 0, 0, 0, 8, 8, 0, 0));
            Assert.Equal(new byte[] { 0x26, 0x1F, 0x03, 0xFF, 0x7F, 0x00, 0x3F, 0, 0, 10, 0 }, Encode(26, 10, 8, 8, 8, 8, 8, 0, 0, 0, 8, 8));
            Assert.Equal(Encode(8, 10), Encode(19));
            Assert.Equal(Encode(13, 0, 8), Encode(21, 0, 8));
            Assert.Equal(Encode(16, 2, 7, 8), Encode(22, 2, 7, 8));
            Assert.Equal(Encode(17, 0, 5, 30), Encode(23, 0, 5, 30));
        }

        [Fact]
        public void ZeroStrengthOrFrequency_TurnsTheTriggerOff()
        {
            Assert.Equal(Off, Encode(13, 4, 0));
            Assert.Equal(Off, Encode(16, 2, 7, 0));
            Assert.Equal(Off, Encode(17, 0, 0, 30));
            Assert.Equal(Off, Encode(17, 0, 8, 0));
            Assert.Equal(Off, Encode(8, 0));
            Assert.Equal(Off, Encode(14, 0, 8, 0, 5));
            Assert.Equal(Off, Encode(15, 0, 9, 2, 4, 0));
            Assert.Equal(Off, Encode(18, 0, 9, 7, 7, 0, 0));
            Assert.Equal(Off, Encode(25, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
            Assert.Equal(Off, Encode(26, 0, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8));
        }

        [Theory]
        [InlineData(13, new[] { 10, 8 })]          // position past 9
        [InlineData(13, new[] { 0, 9 })]           // strength past 8
        [InlineData(13, new[] { 0 })]              // a parameter missing
        [InlineData(16, new[] { 1, 7, 8 })]        // a weapon starts at 2
        [InlineData(16, new[] { 5, 5, 8 })]        // ends after it starts
        [InlineData(14, new[] { 8, 8, 2, 5 })]     // a bow's start before its end
        [InlineData(15, new[] { 0, 9, 4, 4, 10 })] // the second foot after the first
        [InlineData(18, new[] { 0, 9, 8, 7, 10, 0 })] // strengths past 7
        [InlineData(24, new[] { 1, 9, 0, 1 })]     // slope strengths from 1
        [InlineData(25, new[] { 9, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
        [InlineData(12, new[] { 17, 1, 2, 3 })]    // custom modes end at 16
        [InlineData(27, new int[0])]               // modes end at 26
        public void ABadInstruction_ChangesNothing(int mode, int[] parameters)
        {
            var block = Enumerable.Repeat((byte)0xAA, 11).ToArray();
            Assert.False(GameModProtocol.TryEncodeTrigger(mode, parameters, block));
            Assert.All(block, b => Assert.Equal(0xAA, b));
        }

        [Fact]
        public void ABadInstruction_KeepsWhatTheModHeld()
        {
            Send(@"{""instructions"":[{""type"":1,""parameters"":[0,2,13,0,8]}]}");
            var held = Controller0.RightTrigger;
            Send(@"{""instructions"":[{""type"":1,""parameters"":[0,2,13,12,8]},{""type"":1,""parameters"":[0,3,13,0,8]}]}");
            Assert.Same(held, Controller0.RightTrigger);
        }

        [Fact]
        public void TheNamedPresets_AreDualSenseYsBytes()
        {
            Assert.Equal(new byte[] { 0x21, 69, 160, 255, 0, 0, 0, 0, 0, 0, 0 }, Encode(3));
            Assert.Equal(new byte[] { 0x21, 32, 160, 255, 255, 255, 255, 255, 0, 0, 0 }, Encode(4));
            Assert.Equal(new byte[] { 0x21, 16, 160, 255, 255, 255, 255, 255, 0, 0, 0 }, Encode(5));
            Assert.Equal(new byte[] { 0x02, 0, 255, 255, 255, 255, 255, 255, 0, 0, 0 }, Encode(6));
            Assert.Equal(new byte[] { 0x21, 2, 39, 33, 39, 38, 2, 0, 0, 0, 0 }, Encode(9));
            Assert.Equal(new byte[] { 0x22, 2, 35, 1, 6, 6, 1, 33, 0, 0, 0 }, Encode(10));
            Assert.Equal(new byte[] { 0x27, 37, 35, 6, 39, 33, 35, 34, 0, 0, 0 }, Encode(11));
        }

        [Theory]
        [InlineData(0, 0x00)] [InlineData(1, 0x01)] [InlineData(2, 0x21)] [InlineData(3, 0x05)]
        [InlineData(4, 0x25)] [InlineData(5, 0x02)] [InlineData(6, 0x22)] [InlineData(7, 0x06)]
        [InlineData(8, 0x26)] [InlineData(9, 0x06)] [InlineData(10, 0x26)] [InlineData(11, 0x06)]
        [InlineData(12, 0x26)] [InlineData(13, 0x27)] [InlineData(14, 0x27)] [InlineData(15, 0x27)]
        [InlineData(16, 0x27)]
        public void EachCustomMode_TakesItsModeByte_AndTheSeventhValueGoesToByte9(int custom, int modeByte)
        {
            var block = Encode(12, custom, 1, 2, 3, 4, 5, 6, 7);
            Assert.Equal(new byte[] { (byte)modeByte, 1, 2, 3, 4, 5, 6, 0, 0, 7, 0 }, block);
        }

        [Fact]
        public void GtaIvsVibrateResistanceAB_PutsTheWheelSpeedWhereThe0x26EffectReadsItsFrequency()
            => Assert.Equal(new byte[] { 0x26, 140, 255, 144, 90, 120, 220, 0, 0, 33, 0 },
                Encode(12, 12, 140, 255, 144, 90, 120, 220, 33));

        // ── Lights ──

        [Fact]
        public void Lights_MapAsDszMapsThem()
        {
            Assert.True(GameModProtocol.TryLightbar(new[] { 255, 128, 0, 128 }, out byte r, out byte g, out byte b));
            Assert.Equal(new byte[] { 128, 64, 0 }, new[] { r, g, b });
            Assert.True(GameModProtocol.TryLightbar(new[] { 300, -5, 7 }, out r, out g, out b));
            Assert.Equal(new byte[] { 255, 0, 7 }, new[] { r, g, b });
            Assert.False(GameModProtocol.TryLightbar(new[] { 1, 2 }, out _, out _, out _));

            Assert.True(GameModProtocol.TryPlayerLights(new[] { 1, 0, 0, 0, 1 }, out byte bits));
            Assert.Equal(0x20 | 0b10001, bits);
            Assert.True(GameModProtocol.TryPlayerLights(new[] { 1 }, out bits));
            Assert.Equal(0x21, bits);
            byte[] patterns = { 0x24, 0x2A, 0x35, 0x3B, 0x3F, 0x20 };
            for (int n = 0; n <= 5; n++)
            {
                Assert.True(GameModProtocol.TryPlayerLightsNewRevision(new[] { n }, out bits));
                Assert.Equal(patterns[n], bits);
            }
            Assert.False(GameModProtocol.TryPlayerLightsNewRevision(new[] { 6 }, out _));

            Assert.True(GameModProtocol.TryMuteLight(new[] { 0 }, out byte mute));
            Assert.Equal(1, mute);
            Assert.True(GameModProtocol.TryMuteLight(new[] { 1 }, out mute));
            Assert.Equal(2, mute);
            Assert.True(GameModProtocol.TryMuteLight(new[] { 2 }, out mute));
            Assert.Equal(0, mute);
            Assert.False(GameModProtocol.TryMuteLight(new[] { 3 }, out _));
        }

        // ── Holds ──

        [Fact]
        public void TheControllerIndex_CountsTheOrder_AndANegativeOneMeansEvery()
        {
            Send(@"{""instructions"":[{""type"":2,""parameters"":[1,1,2,3]}]}");
            Assert.Null(GameModEffects.ForSlot(7));
            Assert.Equal(new byte[] { 1, 2, 3 }, GameModEffects.ForSlot(6).Lightbar);
            Send(@"{""instructions"":[{""type"":5,""parameters"":[-1,0]}]}");
            for (int i = 0; i < GameModEffects.Controllers; i++)
                Assert.Equal((byte)1, GameModEffects.ForController(i).MuteLight);
            // An index past the last addresses nothing.
            long version = GameModEffects.Version;
            Send(@"{""instructions"":[{""type"":5,""parameters"":[16,2]}]}");
            Assert.Equal(version, GameModEffects.Version);
        }

        [Fact]
        public void TheControllerOrder_IsTheDashboardsOrder_OfLiveSlotsWithAnOnlineSonyPad()
        {
            var savedDevices = SettingsManager.UserDevices;
            var savedSettings = SettingsManager.UserSettings;
            var savedXbox = SettingsManager.XboxSlotOrder;
            var savedPlayStation = SettingsManager.PlayStationSlotOrder;
            var savedNintendo = SettingsManager.NintendoSlotOrder;
            var savedExtended = SettingsManager.ExtendedSlotOrder;
            var savedKeyboardMouse = SettingsManager.KeyboardMouseSlotOrder;
            var savedMidi = SettingsManager.MidiSlotOrder;
            var savedVr = SettingsManager.VrSlotOrder;
            var savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            var savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            try
            {
                GameModEffects.OrderedSlotsOverrideForTest = null;

                // Pad index i takes the i-th device: a wheel, a DualSense, an
                // offline DualSense, a DualSense Edge on a disabled slot, and
                // a DualShock 4.
                var devices = new DeviceCollection();
                var settings = new SettingsCollection();
                foreach (var (vendorId, productId, online) in new (ushort, ushort, bool)[]
                {
                    (0x046D, 0xC24F, true), (0x054C, 0x0CE6, true), (0x054C, 0x0CE6, false),
                    (0x054C, 0x0DF2, true), (0x054C, 0x09CC, true),
                })
                {
                    var id = Guid.NewGuid();
                    devices.Items.Add(new PadForge.Engine.Data.UserDevice
                    {
                        InstanceGuid = id, ProductGuid = id, VendorId = vendorId, ProdId = productId,
                        IsOnline = online, IsEnabled = true,
                    });
                    settings.Items.Add(new PadForge.Engine.Data.UserSetting { InstanceGuid = id, MapTo = settings.Items.Count });
                }
                SettingsManager.UserDevices = devices;
                SettingsManager.UserSettings = settings;

                Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
                for (int i = 0; i < 5; i++)
                {
                    SettingsManager.SlotCreated[i] = true;
                    SettingsManager.SlotEnabled[i] = i != 3;
                }
                // Home numbers pads 0, 4 and 1 as 1, 2 and 3 in the
                // Xbox group, then pads 2 and 3 in the PlayStation group.
                SettingsManager.XboxSlotOrder = new List<int> { 0, 4, 1 };
                SettingsManager.PlayStationSlotOrder = new List<int> { 2, 3 };
                SettingsManager.NintendoSlotOrder = new List<int>();
                SettingsManager.ExtendedSlotOrder = new List<int>();
                SettingsManager.KeyboardMouseSlotOrder = new List<int>();
                SettingsManager.MidiSlotOrder = new List<int>();
                SettingsManager.VrSlotOrder = new List<int>();

                // The DualShock 4 on pad 4, numbered 2, is the mods'
                // controller 0, ahead of the DualSense on pad 1, numbered 3,
                // though its pad index is higher. The wheel's pad, the offline
                // pad's and the disabled one are not addressable.
                Assert.Equal(new[] { 4, 1 }, GameModEffects.OrderedSlots());
            }
            finally
            {
                SettingsManager.UserDevices = savedDevices;
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.XboxSlotOrder = savedXbox;
                SettingsManager.PlayStationSlotOrder = savedPlayStation;
                SettingsManager.NintendoSlotOrder = savedNintendo;
                SettingsManager.ExtendedSlotOrder = savedExtended;
                SettingsManager.KeyboardMouseSlotOrder = savedKeyboardMouse;
                SettingsManager.MidiSlotOrder = savedMidi;
                SettingsManager.VrSlotOrder = savedVr;
                Array.Copy(savedCreated, SettingsManager.SlotCreated, savedCreated.Length);
                Array.Copy(savedEnabled, SettingsManager.SlotEnabled, savedEnabled.Length);
            }
        }

        [Fact]
        public void TheSameValueFromAnotherSender_ChangesOnlyItsOwner()
        {
            Send(@"{""instructions"":[{""type"":2,""parameters"":[0,9,9,9]}]}", Mod);
            long version = GameModEffects.Version;
            var held = Controller0.Lightbar;
            Send(@"{""instructions"":[{""type"":2,""parameters"":[0,9,9,9]}]}", Mod);
            Assert.Equal(version, GameModEffects.Version);
            Send(@"{""instructions"":[{""type"":2,""parameters"":[0,9,9,9]}]}", Other);
            Assert.Same(held, Controller0.Lightbar);
            GameModEffects.ReleaseSender(Mod);
            Assert.Equal(new byte[] { 9, 9, 9 }, Controller0.Lightbar);
            GameModEffects.ReleaseSender(Other);
            Assert.Null(Controller0);
        }

        [Fact]
        public void ASendersExit_EndsOnlyWhatItSet()
        {
            Send(@"{""instructions"":[{""type"":1,""parameters"":[0,2,13,0,8]}]}", Mod);
            Send(@"{""instructions"":[{""type"":2,""parameters"":[0,1,1,1]}]}", Other);
            GameModEffects.ReleaseSender(Mod);
            Assert.Null(Controller0.RightTrigger);
            Assert.Equal(new byte[] { 1, 1, 1 }, Controller0.Lightbar);
            Assert.Equal("Other.exe", Controller0.SenderName);
        }

        [Fact]
        public void AZeroThreshold_HoldsNothing()
        {
            Send(@"{""instructions"":[{""type"":4,""parameters"":[0,1,0]}]}");
            Assert.False(GameModEffects.AnyHeld);
            Send(@"{""instructions"":[{""type"":4,""parameters"":[0,1,90]}]}");
            Send(@"{""instructions"":[{""type"":4,""parameters"":[0,1,0]}]}");
            Assert.False(GameModEffects.AnyHeld);
        }

        [Fact]
        public void ResetAndClearAll_EndEverything()
        {
            Send(@"{""instructions"":[{""type"":1,""parameters"":[0,2,13,0,8]},{""type"":2,""parameters"":[1,1,1,1]}]}");
            GameModEffects.Reset(0);
            Assert.Null(GameModEffects.ForController(0));
            Assert.NotNull(GameModEffects.ForController(1));
            GameModEffects.ClearAll();
            Assert.False(GameModEffects.AnyHeld);
        }

        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(65535, 0, 65535)]
        [InlineData(30000, 120, 0)]
        [InlineData(120 * 257 - 1, 120, 0)]
        [InlineData(120 * 257, 120, 120 * 257)]
        [InlineData(65535, 255, 65535)]
        [InlineData(65534, 255, 0)]
        public void TheThreshold_HoldsATriggerAt0BelowItsPoint(int trigger, int point, int reported)
            => Assert.Equal((ushort)reported, GameModEffects.ApplyThreshold((ushort)trigger, (byte)point));

        [Theory]
        [InlineData(short.MinValue, 120, short.MinValue)]
        [InlineData(120 * 257 - 32768 - 1, 120, short.MinValue)]
        [InlineData(120 * 257 - 32768, 120, 120 * 257 - 32768)]
        [InlineData(short.MaxValue, 255, short.MaxValue)]
        [InlineData(short.MaxValue - 1, 255, short.MinValue)]
        [InlineData(-5, 0, -5)]
        public void ARawTrigger_RestsBelowItsPoint(int trigger, int point, int reported)
            => Assert.Equal((short)reported, GameModEffects.ApplyRawThreshold((short)trigger, (byte)point));

        [Theory]
        // Sticks, triggers, axes, then the left and right trigger axes the
        // interleaved layout gives (ComputeAxisLayout, IsTriggerSlot).
        [InlineData(2, 2, 6, 2, 5)]
        [InlineData(0, 2, 2, 0, 1)]
        [InlineData(3, 1, 7, 2, -1)]
        [InlineData(1, 3, 5, 2, 3)]
        public void RawThresholds_FindTheLayoutsTriggerPair_InACopy(int sticks, int triggers, int axisCount,
            int leftAxis, int rightAxis)
        {
            var layout = new CustomControllerLayout { Sticks = sticks, Triggers = triggers, Axes = axisCount };
            var raw = RawHidState.Create(axisCount, 16, 1);
            // The triggers sit at 60%, between the points 120 (47%) and 200
            // (78%), so each rests only under the 200. Every other axis sits
            // at 52%, which the 200 would rest if it landed there. Run once
            // each way round, a wrong point or a wrong axis on either side
            // changes the frame.
            for (int i = 0; i < axisCount; i++) raw.Axes[i] = 1234;
            raw.Axes[leftAxis] = 6553;
            if (rightAxis >= 0) raw.Axes[rightAxis] = 6553;
            var before = (short[])raw.Axes.Clone();
            short[] scratch = null;

            foreach (var (left, right, restingAxis) in new[] { (120, 200, rightAxis), (200, 120, leftAxis) })
            {
                var thresholded = GameModEffects.ApplyRawThresholds(in raw, in layout, (byte)left, (byte)right, ref scratch);

                Assert.Same(scratch, thresholded.Axes);
                Assert.Same(raw.Buttons, thresholded.Buttons);
                Assert.Equal(before, raw.Axes);
                for (int i = 0; i < axisCount; i++)
                {
                    short expected = i == restingAxis ? short.MinValue : before[i];
                    Assert.Equal(expected, thresholded.Axes[i]);
                }
            }
        }

        [Fact]
        public void RawThresholds_LeaveAFrameWithoutTriggerAxesAlone()
        {
            var layout = new CustomControllerLayout { Sticks = 2, Triggers = 0, Axes = 4 };
            var raw = RawHidState.Create(4, 16, 1);
            short[] scratch = null;
            var thresholded = GameModEffects.ApplyRawThresholds(in raw, in layout, 120, 120, ref scratch);
            Assert.Same(raw.Axes, thresholded.Axes);
            Assert.Null(scratch);
        }

        [Fact]
        public void TheThresholdRead_UsesTheCachedOrder()
        {
            Send(@"{""instructions"":[{""type"":4,""parameters"":[0,2,77]}]}");
            GameModEffects.ThresholdsForSlot(7, out byte left, out byte right);
            Assert.Equal(0, left);
            Assert.Equal(77, right);
            GameModEffects.ThresholdsForSlot(6, out left, out right);
            Assert.Equal(0, left);
            Assert.Equal(0, right);
        }

        [Fact]
        public void AMoveInTheControllerOrder_TellsEverySlotInTheOldAndTheNewOrder()
        {
            int[] order = { 7, 6 };
            GameModEffects.OrderedSlotsOverrideForTest = () => order;
            Send(@"{""instructions"":[{""type"":2,""parameters"":[0,1,2,3]}]}");
            GameModEffects.RefreshOrder();
            var told = new List<int>();
            GameModEffects.NotifiedForTest = slot => { lock (told) told.Add(slot); };

            // The same order tells no one, and a resent value publishes
            // nothing, so neither wakes a pass.
            GameModEffects.RefreshOrder();
            Send(@"{""instructions"":[{""type"":2,""parameters"":[0,1,2,3]}]}");
            Assert.Empty(told);

            // Pad 7's virtual controller turned off: the mod's controller 0
            // is pad 6 now. Pad 7 gives the light back and pad 6 takes it,
            // each told once.
            order = new[] { 6 };
            GameModEffects.RefreshOrder();
            Assert.Equal(new[] { 6, 7 }, told.OrderBy(s => s).ToArray());

            // The polling thread's cached read sees the move back as well.
            told.Clear();
            order = new[] { 7, 6 };
            System.Threading.Thread.Sleep(300);
            Assert.Same(Controller0, GameModEffects.ForSlotCached(7));
            Assert.Equal(new[] { 6, 7 }, told.OrderBy(s => s).ToArray());

            // With nothing held, a move tells no one.
            told.Clear();
            GameModEffects.Reset(-1);
            told.Clear();
            order = new[] { 6 };
            GameModEffects.RefreshOrder();
            Assert.Empty(told);
        }

        [Fact]
        public void APeripheralTheSlotLights_ShowsTheModsLightsLine()
        {
            var savedSettings = SettingsManager.UserSettings;
            var mouse = Guid.NewGuid();
            try
            {
                var settings = new SettingsCollection();
                settings.Items.Add(new PadForge.Engine.Data.UserSetting { InstanceGuid = mouse, MapTo = 7 });
                SettingsManager.UserSettings = settings;
                var ud = new PadForge.Engine.Data.UserDevice
                {
                    InstanceGuid = mouse, ProductGuid = mouse, VendorId = 0x046D, ProdId = 0xC08B, IsOnline = true,
                };
                Send(@"{""instructions"":[{""type"":2,""parameters"":[0,1,2,3]}]}");

                // Not lit from pad 7: no line.
                Assert.Equal(string.Empty, InputService.GameModDeviceLine(ud));

                // Lit from pad 7, the mod's controller 0, which holds a light.
                PadForge.Common.Input.Peripherals.PeripheralOutputs.SetLighting(mouse, 7, 1, 1, 2, 3);
                Assert.Equal(string.Format(PadForge.Resources.Strings.Strings.Instance.Devices_GameModPeripheralLights_Format, "Mod.exe"),
                    InputService.GameModDeviceLine(ud));

                // Lit from another slot only: the mod's light is not on it.
                PadForge.Common.Input.Peripherals.PeripheralOutputs.ReleaseLighting(mouse, 7);
                PadForge.Common.Input.Peripherals.PeripheralOutputs.SetLighting(mouse, 3, 1, 1, 2, 3);
                Assert.Equal(string.Empty, InputService.GameModDeviceLine(ud));

                // A hold with no lightbar recolors nothing.
                PadForge.Common.Input.Peripherals.PeripheralOutputs.SetLighting(mouse, 7, 1, 1, 2, 3);
                GameModEffects.Reset(0);
                Send(@"{""instructions"":[{""type"":5,""parameters"":[0,1]}]}");
                Assert.Equal(string.Empty, InputService.GameModDeviceLine(ud));
            }
            finally
            {
                PadForge.Common.Input.Peripherals.PeripheralOutputs.ReleaseDevice(mouse);
                SettingsManager.UserSettings = savedSettings;
            }
        }

        // ── The effects pass ──

        [Fact]
        public void AGamesWrite_BeatsTheModsHold_WhichFillsTheRest()
        {
            Send(@"{""instructions"":[{""type"":1,""parameters"":[0,2,13,0,8]},{""type"":1,""parameters"":[0,1,13,0,8]},{""type"":2,""parameters"":[0,5,6,7]},{""type"":5,""parameters"":[0,1]},{""type"":6,""parameters"":[0,0]}]}");
            var game = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                RightTriggerEffect = new byte[11],
                MuteLed = 0,
            };
            var fill = UserEffectsDispatcher.FillFromModHold(game, Controller0, isDs5: true);
            Assert.Same(game.RightTriggerEffect, fill.Overrides.RightTriggerEffect);
            Assert.False(fill.RightTrigger);
            Assert.Equal(Controller0.LeftTrigger, fill.Overrides.LeftTriggerEffect);
            Assert.NotSame(Controller0.LeftTrigger, fill.Overrides.LeftTriggerEffect);
            Assert.True(fill.LeftTrigger);
            Assert.Equal((byte)0, fill.Overrides.MuteLed);
            Assert.False(fill.MuteLight);
            Assert.Equal((byte)0x24, fill.Overrides.PlayerIndicator);
            Assert.True(fill.PlayerLights);
            Assert.Equal(new byte[] { 5, 6, 7 }, fill.Overrides.LightbarRgb);
            Assert.False(fill.Overrides.LightbarEverExternal);
        }

        [Fact]
        public void ADualShock4_TakesOnlyTheLightbar()
        {
            Send(@"{""instructions"":[{""type"":1,""parameters"":[0,2,13,0,8]},{""type"":2,""parameters"":[0,5,6,7]},{""type"":5,""parameters"":[0,1]}]}");
            var fill = UserEffectsDispatcher.FillFromModHold(default, Controller0, isDs5: false);
            Assert.Null(fill.Overrides.RightTriggerEffect);
            Assert.Null(fill.Overrides.MuteLed);
            Assert.Equal(new byte[] { 5, 6, 7 }, fill.Overrides.LightbarRgb);
            Assert.False(fill.RightTrigger || fill.LeftTrigger || fill.MuteLight || fill.PlayerLights);
            Assert.Equal(0x050607, UserEffectsDispatcher.ModColor(Controller0));
            Assert.Null(UserEffectsDispatcher.ModColor(null));
        }

        [Fact]
        public void AnEndedHold_IsWrittenOnceMore()
        {
            var previous = new System.Collections.Generic.Dictionary<Guid, bool>();
            var pad = Guid.NewGuid();
            Assert.False(UserEffectsDispatcher.ModHoldEnded(previous, pad, holdsNow: true));
            Assert.False(UserEffectsDispatcher.ModHoldEnded(previous, pad, holdsNow: true));
            Assert.True(UserEffectsDispatcher.ModHoldEnded(previous, pad, holdsNow: false));
            Assert.False(UserEffectsDispatcher.ModHoldEnded(previous, pad, holdsNow: false));
        }

        [Fact]
        public void AModChange_KeepsTheWriterAliveForTwoSeconds()
        {
            Assert.False(UserEffectsDispatcher.ModChangedRecently(0, 5000));
            Assert.True(UserEffectsDispatcher.ModChangedRecently(1000, 1000 + UserEffectsDispatcher.ModChangeTimerMs - 1));
            Assert.False(UserEffectsDispatcher.ModChangedRecently(1000, 1000 + UserEffectsDispatcher.ModChangeTimerMs));
        }

        [Fact]
        public void TheImpulseTranslation_ReadsTheModsProgramForASideNoGameWrote()
        {
            Send(@"{""instructions"":[{""type"":1,""parameters"":[0,2,17,0,5,30]}]}");
            var left = new byte[11];
            var right = new byte[11];
            Assert.True(UserEffectsDispatcher.TryGetInboundTriggerEffects(7, left, right));
            Assert.Equal(Encode(17, 0, 5, 30), right);
            Assert.Equal(new byte[11], left);
            Assert.False(UserEffectsDispatcher.TryGetInboundTriggerEffects(6, left, right));
        }

        [Fact]
        public void ThePass_FillsAfterTheLightbarDecision_AndBeforeTheInjectedEffects()
        {
            string code = Source("PadForge.App", "Common", "Input", "UserEffectsDispatcher.cs");
            int capture = code.IndexOf("var modHold = GameModEffects.ForSlot(_padIndex);", StringComparison.Ordinal);
            int overrides = code.IndexOf("var overrides = GetActiveOverrides(_padIndex);", StringComparison.Ordinal);
            int decision = code.IndexOf("bool assertLightbar = ShouldAssertLightbar(", StringComparison.Ordinal);
            int fill = code.IndexOf("var modFill = FillFromModHold(devOverrides, modHold, isDs5);", StringComparison.Ordinal);
            int impulse = code.IndexOf("SlotImpulseTriggerForDeviceProvider?.Invoke(_padIndex, ud.InstanceGuid)", StringComparison.Ordinal);
            Assert.True(overrides > 0 && capture > overrides && decision > capture && fill > decision && impulse > fill);
            // A macro's trigger effect (#531) is read beside the mod's hold
            // and fills after it, so the order is game, mod, macro, then the
            // injected effects.
            int macroCapture = code.IndexOf("MacroTriggerEffectsProvider?.Invoke(_padIndex)?.Read(", StringComparison.Ordinal);
            int macroFill = code.IndexOf("var macroFill = FillFromMacroEffects(devOverrides, macroLeftTrig, macroRightTrig, isDs5);", StringComparison.Ordinal);
            Assert.True(macroCapture > capture && decision > macroCapture && macroFill > fill && impulse > macroFill);
            Assert.Contains("| ModHoldEnded(_prevModRightTrig, ud.InstanceGuid, modFill.RightTrigger)", code);
            Assert.Contains("| ModHoldEnded(_prevMacroRightTrig, ud.InstanceGuid, macroFill.RightTrigger);", code);
            Assert.Contains("| ModHoldEnded(_prevModLeftTrig, ud.InstanceGuid, modFill.LeftTrigger)", code);
            Assert.Contains("| ModHoldEnded(_prevMacroLeftTrig, ud.InstanceGuid, macroFill.LeftTrigger);", code);
            Assert.Contains("| ModHoldEnded(_prevModMicLed, ud.InstanceGuid, modFill.MuteLight);", code);
            Assert.Contains("| ModHoldEnded(_prevModPips, ud.InstanceGuid, modFill.PlayerLights);", code);
            Assert.Equal(2, CountOf(code, "LightPeripheral(ud, perDeviceCfgs, rawAudioPeak, nowMs, playerNumber, ModColor(modHold))"));

            // Step 5 reads the thresholds twice: on the raw surface of an
            // Extended or Nintendo slot and on every other slot's gamepad
            // state, each after its button SOCD and on a copy.
            string step5 = Source("PadForge.App", "Common", "Input", "InputManager.Step5.VirtualDevices.cs");
            const string read = "GameModEffects.ThresholdsForSlot(padIndex, out byte modLeftPoint, out byte modRightPoint);";
            Assert.Equal(2, CountOf(step5, read));
            int rawSocd = step5.IndexOf("socdExt.ApplyExtended(CombinedRawHidStates[padIndex].Buttons);", StringComparison.Ordinal);
            int rawRead = step5.IndexOf(read, rawSocd, StringComparison.Ordinal);
            int socd = step5.IndexOf("gpOut.Buttons = socdGp.ApplyGamepad(gpOut.Buttons);", StringComparison.Ordinal);
            int threshold = step5.IndexOf(read, socd, StringComparison.Ordinal);
            Assert.True(rawSocd > 0 && rawRead > rawSocd && socd > rawRead && threshold > socd);
            string rawBranch = step5.Substring(rawRead, socd - rawRead);
            Assert.Matches(@"valvePacker\.Pack\(\s*rawOut,", rawBranch);
            Assert.Matches(@"ExtendedReportPacker\.Pack\(\s*rawOut, layout, _extendedReportScratch\)", rawBranch);
            Assert.Matches(@"hmExt\.SubmitRawHidState\(\s*rawOut,", rawBranch);
            Assert.DoesNotContain("CombinedRawHidStates[padIndex]", rawBranch);
        }

        // ── Server ──

        [Fact]
        public void TheDefaultPort_IsFencedInTheTestHost()
        {
            using var server = new GameModServer();
            Assert.False(server.Start(GameModServer.DefaultPort, standIn: false));
            Assert.Equal(GameModServer.ServerState.PortInUse, server.State);
        }

        [Fact]
        public void APortInUse_SaysSo()
        {
            using var holder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)holder.Client.LocalEndPoint).Port;
            using var server = new GameModServer();
            Assert.False(server.Start(port, standIn: false));
            Assert.Equal(GameModServer.ServerState.PortInUse, server.State);
            Assert.Contains(port.ToString(), server.FormatStatus());
        }

        [Fact]
        public void EveryPacketIsAnswered_AndApplied_AndStopEndsTheHolds()
        {
            int port;
            using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                port = ((IPEndPoint)probe.Client.LocalEndPoint).Port;
            using var server = new GameModServer();
            Assert.True(server.Start(port, standIn: false));
            Assert.Equal(GameModServer.ServerState.Listening, server.State);

            using var mod = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            mod.Client.ReceiveTimeout = 5000;
            var target = new IPEndPoint(IPAddress.Loopback, port);
            byte[] packet = Encoding.ASCII.GetBytes(@"{""instructions"":[{""type"":1,""parameters"":[0,2,13,0,8]}]}");
            mod.Send(packet, packet.Length, target);
            var from = new IPEndPoint(IPAddress.Loopback, 0);
            using var reply = JsonDocument.Parse(mod.Receive(ref from));
            Assert.Equal(GameModProtocol.ReplyStatus, reply.RootElement.GetProperty("Status").GetString());
            Assert.Equal(JsonValueKind.Array, reply.RootElement.GetProperty("Devices").ValueKind);
            Assert.True(reply.RootElement.TryGetProperty("isControllerConnected", out _));
            Assert.True(reply.RootElement.TryGetProperty("BatteryLevel", out _));
            Assert.True(reply.RootElement.TryGetProperty("TimeReceived", out _));

            // The reply goes before the apply, so wait for the hold.
            var watch = Stopwatch.StartNew();
            while (Controller0 == null && watch.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(10);
            Assert.Equal(Encode(13, 0, 8), Controller0.RightTrigger);
            Assert.Equal(Environment.ProcessId, Controller0.Owners[(int)GameModEffects.Part.RightTrigger].ProcessId);

            // A packet that is not one still gets an answer.
            byte[] junk = Encoding.ASCII.GetBytes("hello");
            mod.Send(junk, junk.Length, target);
            using var second = JsonDocument.Parse(mod.Receive(ref from));
            Assert.Equal(GameModProtocol.ReplyStatus, second.RootElement.GetProperty("Status").GetString());

            server.Stop();
            Assert.False(GameModEffects.AnyHeld);
            Assert.Equal(GameModServer.ServerState.Stopped, server.State);
        }

        [Fact]
        public void AModThatOpensASocketEveryTick_IsOneSender_AndEveryPacketIsAnsweredAtItsOwnPort()
        {
            // The GTA V mods make a new UdpClient on every Connect and call
            // Connect every tick (DualSense4GTAV add.cs:28-31, 47-49), so one
            // process sends from a new port each time.
            int port;
            using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                port = ((IPEndPoint)probe.Client.LocalEndPoint).Port;
            using var server = new GameModServer();
            Assert.True(server.Start(port, standIn: false));
            int changes = 0;
            server.StatusChanged += (_, _) => System.Threading.Interlocked.Increment(ref changes);

            var target = new IPEndPoint(IPAddress.Loopback, port);
            byte[] packet = Encoding.ASCII.GetBytes(@"{""instructions"":[{""type"":2,""parameters"":[0,1,2,3]}]}");
            var mods = new List<UdpClient>();
            try
            {
                for (int i = 0; i < 20; i++)
                {
                    var mod = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    mods.Add(mod);
                    mod.Client.ReceiveTimeout = 5000;
                    mod.Send(packet, packet.Length, target);
                    var from = new IPEndPoint(IPAddress.Loopback, 0);
                    using var reply = JsonDocument.Parse(mod.Receive(ref from));
                    Assert.Equal(GameModProtocol.ReplyStatus, reply.RootElement.GetProperty("Status").GetString());
                }

                // The reply goes before the apply, so wait for the last port.
                var watch = Stopwatch.StartNew();
                while (server.PortCountForTest < 20 && watch.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(10);
                Assert.Equal(20, server.PortCountForTest);
                Assert.Equal(1, server.SenderCountForTest);
                Assert.Equal(Environment.ProcessId, Controller0.Owners[(int)GameModEffects.Part.Lightbar].ProcessId);
                Assert.Equal(1, System.Threading.Volatile.Read(ref changes));
            }
            finally
            {
                foreach (var mod in mods) mod.Dispose();
            }
        }

        [Fact]
        public void TheReply_CarriesDsxsFieldsAndNames()
        {
            var devices = new[]
            {
                new GameModProtocol.ReplyDevice { Index = 0, MacAddress = "aa:bb", DeviceType = 0, ConnectionType = 1, BatteryLevel = 80, IsSupportAT = true, IsSupportLightBar = true, IsSupportPlayerLED = true, IsSupportMicLED = true },
                new GameModProtocol.ReplyDevice { Index = 1, DeviceType = 3, BatteryLevel = 40, IsSupportLightBar = true },
            };
            using var json = JsonDocument.Parse(GameModProtocol.BuildReply(devices, 1, new DateTime(2026, 10, 25, 12, 0, 0)));
            var root = json.RootElement;
            Assert.Equal("DSX Received UDP Instructions", root.GetProperty("Status").GetString());
            Assert.Equal("2026-10-25 12:00:00", root.GetProperty("TimeReceived").GetString());
            Assert.True(root.GetProperty("isControllerConnected").GetBoolean());
            Assert.Equal(40, root.GetProperty("BatteryLevel").GetInt32());
            var first = root.GetProperty("Devices")[0];
            Assert.Equal(new[] { "Index", "MacAddress", "DeviceType", "ConnectionType", "BatteryLevel", "IsSupportAT", "IsSupportLightBar", "IsSupportPlayerLED", "IsSupportMicLED" },
                first.EnumerateObject().Select(p => p.Name));

            using var none = JsonDocument.Parse(GameModProtocol.BuildReply(Array.Empty<GameModProtocol.ReplyDevice>(), 0, DateTime.Now));
            Assert.False(none.RootElement.GetProperty("isControllerConnected").GetBoolean());
            Assert.Equal(0, none.RootElement.GetProperty("Devices").GetArrayLength());
        }

        [Fact]
        public void TheStatusLine_NamesTheSenderAndTheVirtualController()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            Assert.Equal(string.Format(s.Server_ListeningOn_Format, 6969),
                GameModServer.FormatStatus(GameModServer.ServerState.Listening, 6969, null, 0));
            Assert.Equal(string.Format(s.GameMods_Sending_Format, 6969, "GTA5.exe", 2),
                GameModServer.FormatStatus(GameModServer.ServerState.Listening, 6969, "GTA5.exe", 2));
            Assert.Equal(string.Format(s.GameMods_SendingNoController_Format, 6969, "GTA5.exe"),
                GameModServer.FormatStatus(GameModServer.ServerState.Listening, 6969, "GTA5.exe", 0));
            Assert.Equal(string.Format(s.GameMods_PortInUse_Format, 7000),
                GameModServer.FormatStatus(GameModServer.ServerState.PortInUse, 7000, null, 0));
            // An index past the addressable controllers names the index,
            // and says nothing about controllers that are there.
            Assert.Equal(string.Format(s.GameMods_SendingPastControllers_Format, 6969, "GTA5.exe", 2),
                GameModServer.FormatStatus(GameModServer.ServerState.Listening, 6969, "GTA5.exe", 0, pastIndex: 2));
        }

        [Fact]
        public void AnIndexPastTheAddressableControllers_GetsItsOwnLine()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            int port;
            using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                port = ((IPEndPoint)probe.Client.LocalEndPoint).Port;
            using var server = new GameModServer();
            Assert.True(server.Start(port, standIn: false));
            using var mod = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            mod.Client.ReceiveTimeout = 5000;
            string name = Process.GetCurrentProcess().MainModule.ModuleName;

            // Two addressable controllers (pads 7 and 6): index 5 is past them.
            byte[] packet = Encoding.ASCII.GetBytes(@"{""instructions"":[{""type"":2,""parameters"":[5,1,2,3]}]}");
            mod.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
            var from = new IPEndPoint(IPAddress.Loopback, 0);
            mod.Receive(ref from);
            string expected = string.Format(s.GameMods_SendingPastControllers_Format, port, name, 5);
            var watch = Stopwatch.StartNew();
            while (server.FormatStatus() != expected && watch.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(10);
            Assert.Equal(expected, server.FormatStatus());

            // With none addressable, the line says so.
            GameModEffects.OrderedSlotsOverrideForTest = () => Array.Empty<int>();
            Assert.Equal(string.Format(s.GameMods_SendingNoController_Format, port, name), server.FormatStatus());
            Assert.Equal(s.Common_Stopped, GameModServer.FormatStatus(GameModServer.ServerState.Stopped, 6969, null, 0));
        }

        [Fact]
        public void AServerKeptFromItsPort_BindsOnceTheOtherProgramLeaves()
        {
            var vm = new PadForge.ViewModels.MainViewModel();
            using var service = new InputService(vm);
            var holder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)holder.Client.LocalEndPoint).Port;
            Service(service, "_inputManager").SetValue(service,
                System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InputManager)));
            try
            {
                vm.Dashboard.GameModDsxStandIn = false;
                vm.Dashboard.GameModServerPort = port;
                vm.Dashboard.EnableGameModServer = true;
                Call(service, "StartGameModServerIfEnabled");
                Assert.Null(Service(service, "_gameModServer").GetValue(service));

                // Inside the interval nothing is tried, and while the other
                // program holds the port a try fails again.
                Call(service, "UpdateGameModLines");
                Assert.Null(Service(service, "_gameModServer").GetValue(service));
                Service(service, "_gameModFailedMs").SetValue(service, Environment.TickCount64 - InputService.PortInUseRetryMs);
                Call(service, "UpdateGameModLines");
                Assert.Null(Service(service, "_gameModServer").GetValue(service));

                // The other program leaves: the next try past the interval
                // listens.
                holder.Dispose();
                Call(service, "UpdateGameModLines");
                Assert.Null(Service(service, "_gameModServer").GetValue(service));
                Service(service, "_gameModFailedMs").SetValue(service, Environment.TickCount64 - InputService.PortInUseRetryMs);
                Call(service, "UpdateGameModLines");
                var server = (GameModServer)Service(service, "_gameModServer").GetValue(service);
                Assert.NotNull(server);
                Assert.Equal(GameModServer.ServerState.Listening, server.State);
                Assert.Equal(port, server.Port);

                // Stopped by the switch, nothing is tried again.
                Call(service, "StopGameModServer");
                Service(service, "_gameModFailedMs").SetValue(service, 0L);
                Call(service, "UpdateGameModLines");
                Assert.Null(Service(service, "_gameModServer").GetValue(service));
            }
            finally
            {
                holder.Dispose();
                Call(service, "StopGameModServer");
                Service(service, "_inputManager").SetValue(service, null);
            }
        }

        [Fact]
        public void TheMotionServerKeptFromItsPort_BindsOnceTheOtherProgramLeaves()
        {
            var vm = new PadForge.ViewModels.MainViewModel();
            using var service = new InputService(vm);
            var holder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)holder.Client.LocalEndPoint).Port;
            Service(service, "_inputManager").SetValue(service,
                System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InputManager)));
            try
            {
                vm.Dashboard.DsuMotionServerPort = port;
                vm.Dashboard.EnableDsuMotionServer = true;
                Call(service, "StartDsuServerIfEnabled");
                Assert.Null(Service(service, "_dsuServer").GetValue(service));

                Call(service, "RetryDsuServerOnPortInUse");
                Assert.Null(Service(service, "_dsuServer").GetValue(service));
                holder.Dispose();
                Call(service, "RetryDsuServerOnPortInUse");
                Assert.Null(Service(service, "_dsuServer").GetValue(service));
                Service(service, "_dsuFailedMs").SetValue(service, Environment.TickCount64 - InputService.PortInUseRetryMs);
                Call(service, "RetryDsuServerOnPortInUse");
                var server = (DsuMotionServer)Service(service, "_dsuServer").GetValue(service);
                Assert.NotNull(server);
                Assert.False(server.PortInUse);

                Call(service, "StopDsuServer");
                Service(service, "_dsuFailedMs").SetValue(service, 0L);
                Call(service, "RetryDsuServerOnPortInUse");
                Assert.Null(Service(service, "_dsuServer").GetValue(service));

                // The dashboard tick runs it, beside the game mod lines.
                string input = Source("PadForge.App", "Services", "InputService.cs");
                Assert.Contains("            UpdateGameModLines();\n            RetryDsuServerOnPortInUse();\n", input);
            }
            finally
            {
                holder.Dispose();
                Call(service, "StopDsuServer");
                Service(service, "_inputManager").SetValue(service, null);
            }
        }

        [Fact]
        public void AFailedStartsReason_SurvivesALanguageChange()
        {
            var vm = new PadForge.ViewModels.MainViewModel();
            using var service = new InputService(vm);
            using var holder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)holder.Client.LocalEndPoint).Port;
            Service(service, "_inputManager").SetValue(service,
                System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InputManager)));
            try
            {
                var s = PadForge.Resources.Strings.Strings.Instance;
                vm.Dashboard.GameModDsxStandIn = false;
                vm.Dashboard.GameModServerPort = port;
                vm.Dashboard.EnableGameModServer = true;
                vm.Dashboard.DsuMotionServerPort = port;
                vm.Dashboard.EnableDsuMotionServer = true;
                Call(service, "StartGameModServerIfEnabled");
                Call(service, "StartDsuServerIfEnabled");

                // What a language change runs.
                Call(service, "RefreshServerStatusStrings");
                Assert.Equal(string.Format(s.GameMods_PortInUse_Format, port), vm.Dashboard.GameModServerStatus);
                Assert.Equal(string.Format(s.Server_PortInUse_Format, port), vm.Dashboard.DsuServerStatus);

                // Stopped, the reason is gone.
                Call(service, "StopGameModServer");
                Call(service, "StopDsuServer");
                Call(service, "RefreshServerStatusStrings");
                Assert.Equal(s.Common_Stopped, vm.Dashboard.GameModServerStatus);
                Assert.Equal(s.Common_Stopped, vm.Dashboard.DsuServerStatus);
            }
            finally
            {
                Call(service, "StopGameModServer");
                Call(service, "StopDsuServer");
                Service(service, "_inputManager").SetValue(service, null);
            }
        }

        private static System.Reflection.FieldInfo Service(InputService service, string name) =>
            typeof(InputService).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(InputService), name);

        private static void Call(InputService service, string name) =>
            (typeof(InputService).GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new MissingMethodException(nameof(InputService), name)).Invoke(service, null);

        // ── Senders ──

        [Fact]
        public void ASender_IsTheProcessThatOwnsItsPort()
        {
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)socket.Client.LocalEndPoint).Port;
            using var sender = GameModSender.Identify(port, Environment.TickCount64);
            Assert.Equal(Environment.ProcessId, sender.ProcessId);
            Assert.EndsWith(".exe", sender.Name, StringComparison.OrdinalIgnoreCase);
            Assert.False(sender.IsGone(Environment.TickCount64 + GameModSender.LapseMs * 10));
        }

        [Fact]
        public void ASenderWithNoProcess_LapsesAfterFifteenSeconds()
        {
            using var sender = new GameModSender(1, 0, null, null, 1000);
            Assert.False(sender.IsGone(1000 + GameModSender.LapseMs - 1));
            Assert.True(sender.IsGone(1000 + GameModSender.LapseMs));
            sender.Touch(20000);
            Assert.False(sender.IsGone(20000 + GameModSender.LapseMs - 1));
        }

        // ── Port files ──

        [Fact]
        public void ThePortFiles_FollowTheRule()
        {
            string root = Path.Combine(Path.GetTempPath(), "PadForge.Tests", "GameModPortFiles", Guid.NewGuid().ToString("N"));
            try
            {
                string legacy = Path.Combine(root, "Temp", "DualSenseX", "DualSenseX_PortNumber.txt");
                string current = Path.Combine(root, "DSX", "DSX_UDP_PortNumber.txt");

                // The default port: the old file is made, the new one is not.
                GameModPortFiles.WriteTo(legacy, current, 6969);
                Assert.Equal("6969", File.ReadAllText(legacy));
                Assert.False(File.Exists(current));

                // Another port: both carry it.
                GameModPortFiles.WriteTo(legacy, current, 7000);
                Assert.Equal("7000", File.ReadAllText(legacy));
                Assert.Equal("7000", File.ReadAllText(current));

                // The same port again: nothing is rewritten.
                var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(legacy, stamp);
                File.SetLastWriteTimeUtc(current, stamp);
                File.WriteAllText(current, " 7000\r\n");
                File.SetLastWriteTimeUtc(current, stamp);
                GameModPortFiles.WriteTo(legacy, current, 7000);
                Assert.Equal(stamp, File.GetLastWriteTimeUtc(legacy));
                Assert.Equal(stamp, File.GetLastWriteTimeUtc(current));

                // Back to the default: the new file that exists is corrected.
                GameModPortFiles.WriteTo(legacy, current, 6969);
                Assert.Equal("6969", File.ReadAllText(current));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        // ── The DSX.exe stand-in ──

        [Fact]
        public void TheStandIn_IsEmbedded_AndRunsOnWindowsOwnFramework()
        {
            byte[] exe = DsxStandIn.ReadEmbedded();
            Assert.NotNull(exe);
            Assert.True(exe.Length > 1024 && exe.Length < 64 * 1024, exe.Length.ToString());
            Assert.Equal((byte)'M', exe[0]);
            Assert.Equal((byte)'Z', exe[1]);
            Assert.Equal("4242", DsxStandIn.Arguments(4242));

            string folder = Path.Combine(Path.GetTempPath(), "PadForge.Tests", "DsxStandIn", Guid.NewGuid().ToString("N"));
            string path = Path.Combine(folder, "DSX.exe");
            try
            {
                Assert.Equal(path, DsxStandIn.Extract(path));
                Assert.Equal(exe, File.ReadAllBytes(path));
                var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(path, stamp);
                Assert.Equal(path, DsxStandIn.Extract(path));
                Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));

                // A parent that is already gone: the stand-in exits at once,
                // and with no parent named it refuses to run.
                Assert.Equal(0, Run(path, "2147483000"));
                Assert.Equal(1, Run(path, ""));
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        private static int Run(string path, string arguments)
        {
            var psi = new ProcessStartInfo(path, arguments) { UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(psi);
            Assert.True(process.WaitForExit(15000), "the stand-in did not exit");
            return process.ExitCode;
        }

        // ── Settings ──

        [Fact]
        public void TheSettings_RoundTripThroughAppSettingsAndProfiles()
        {
            var fresh = new AppSettingsData();
            Assert.False(fresh.EnableGameModServer);
            Assert.Equal(6969, fresh.GameModServerPort);
            Assert.False(fresh.GameModDsxStandIn);

            var app = RoundTrip(new AppSettingsData { EnableGameModServer = true, GameModServerPort = 7000, GameModDsxStandIn = true });
            Assert.True(app.EnableGameModServer);
            Assert.Equal(7000, app.GameModServerPort);
            Assert.True(app.GameModDsxStandIn);

            var profile = RoundTrip(new ProfileData { EnableGameModServer = true, GameModServerPort = 7001, GameModDsxStandIn = true });
            Assert.True(profile.EnableGameModServer);
            Assert.Equal(7001, profile.GameModServerPort);
            Assert.True(profile.GameModDsxStandIn);
            Assert.Equal(6969, new ProfileData().GameModServerPort);
        }

        [Fact]
        public void EverySettingsSite_CarriesAllThreeGameModSettings()
        {
            // The motion server's sibling set in SettingsService: the two
            // data classes, the load, the active profile's apply, the
            // profile save and the app settings build. Counted in lines,
            // since the game mod port's load checks its range where the
            // motion server's checks only for zero.
            string settings = Source("PadForge.App", "Services", "SettingsService.cs");
            Assert.Equal(LinesWith(settings, "DsuMotionServerPort"), LinesWith(settings, "GameModServerPort"));
            Assert.Equal(LinesWith(settings, "EnableDsuMotionServer"), LinesWith(settings, "EnableGameModServer"));
            Assert.Equal(LinesWith(settings, "EnableDsuMotionServer"), LinesWith(settings, "GameModDsxStandIn"));

            // And in InputService: the profile snapshot, the profile apply
            // and the snapshot copied into a profile.
            string input = Source("PadForge.App", "Services", "InputService.cs");
            foreach (string name in new[] { "EnableGameModServer", "GameModServerPort", "GameModDsxStandIn" })
            {
                Assert.Contains(name + " = _mainVm.Dashboard." + name + ",", input);
                Assert.Contains("profile." + name + " = snapshot." + name + ";", input);
                Assert.Contains("_mainVm.Dashboard." + name + " = profile." + name, input);
            }

            string window = Source("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("or nameof(DashboardViewModel.EnableGameModServer)", window);
            Assert.Contains("or nameof(DashboardViewModel.GameModServerPort)", window);
            Assert.Contains("or nameof(DashboardViewModel.GameModDsxStandIn)", window);
        }

        private static T RoundTrip<T>(T value)
        {
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(T));
            using var text = new StringWriter();
            serializer.Serialize(text, value);
            using var reader = new StringReader(text.ToString());
            return (T)serializer.Deserialize(reader);
        }

        // ── Helpers ──

        private static string Source(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "PadForge.App"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
        }

        private static int LinesWith(string text, string value) =>
            text.Split('\n').Count(line => line.Contains(value, StringComparison.Ordinal));

        private static int CountOf(string text, string value)
        {
            int count = 0;
            for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
                count++;
            return count;
        }
    }
}
