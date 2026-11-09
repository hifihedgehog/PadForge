using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Every Lenovo model OpenRGB lights (#557), beyond the Gen 10 board
    /// HidLightingTests covers: the Gen 7 to 9 stream, the Legion 5 Gen 10's
    /// 24 zones, the key map and the ISO and JIS boards, the Y740 to Y760
    /// controller, the 4-zone keyboards, the K500, the K510 and the M300.
    /// Every expected byte is written out from OpenRGB, Lenovo Legion
    /// Toolkit or L5P-Keyboard-RGB, never from the builder under test.
    /// </summary>
    public class LenovoLightingTests
    {
        private static string Hex(byte[] bytes, int count) => LenovoLegionLightingTests.Hex(bytes, count);

        private static string Hex(byte[] bytes, int from, int count) => BitConverter.ToString(bytes, from, count).Replace('-', ' ');

        private static HidCollectionInfo Collection(ushort vid, ushort pid, ushort page, ushort usage,
            ushort feature = 0, ushort output = 0, int iface = -1)
            => new()
            {
                Path = $@"\\?\hid#vid_{vid:x4}&pid_{pid:x4}#{page:x4}{usage:x4}",
                VendorId = vid, ProductId = pid, UsagePage = page, Usage = usage,
                FeatureReportLength = feature, OutputReportLength = output, InterfaceNumber = iface,
            };

        // ─────────────────────────────────────────────
        //  The generated tables
        // ─────────────────────────────────────────────

        [Fact]
        public void TheTables_AreOpenRgbsAtThePinnedCommit()
        {
            Assert.Equal("b9d903b544a40486709c4ca097d0b234b617db9c", LenovoLights.OpenRgbCommit);
            // LenovoDevices.h:1739-1855: 101 keys, Escape first, Right Arrow last.
            Assert.Equal(101, LenovoLights.Legion7Gen7and8AnsiLeds.Length);
            Assert.Equal(new LenovoLed(0x01, "Key: Escape"), LenovoLights.Legion7Gen7and8AnsiLeds[0]);
            Assert.Equal(new LenovoLed(0xA1, "Key: Right Arrow"), LenovoLights.Legion7Gen7and8AnsiLeds[100]);
            // LenovoDevices.h:163-325 and 1409-1420: the Y760 ANSI board,
            // indexes 0 to 144 of its table, zone 1.
            Assert.Equal(145, LenovoLights.LegionY760AnsiLeds.Length);
            Assert.Equal(1, LenovoLights.LenovoLegionY760KbdAnsi.Id);
            Assert.Equal(144, LenovoLights.LenovoLegionY760KbdAnsi.End);
            // The right vent, indexes 27 to 53 of the vent table (lines 1480-1491).
            Assert.Equal((27, 53), (LenovoLights.LenovoLegionY760VentRight.Start, LenovoLights.LenovoLegionY760VentRight.End));
            // RGBController_Lenovo_Gen7_8.cpp:31-78: 24 zones, zone ID 0.
            Assert.Equal(24, LenovoLights.Legion5gen1024zoneLeds.Length);
            Assert.Equal(new LenovoLed(0x18, "Zone R4C6"), LenovoLights.Legion5gen1024zoneLeds[23]);
            // LenovoDevices4Zone.h:101-107.
            Assert.Equal(new[] { "Left side", "Left center", "Right center", "Right side" },
                LenovoLights.Lenovo4ZoneLeds.Select(l => l.Name));
            // RGBController_LenovoK500.cpp:38-144 and LenovoK500Controller.cpp:25-131.
            Assert.Equal(104, LenovoLights.K500LedNames.Length);
            Assert.Equal("Key: Number Pad .", LenovoLights.K500LedNames[103]);
            Assert.Equal(((byte)13, (byte)5), LenovoLights.K500Mask[103]);
            Assert.Equal(((byte)7, (byte)6), LenovoLights.K500Mask[32]);   // PgUp
        }

        // ─────────────────────────────────────────────
        //  Gen 7 to 9 frames
        // ─────────────────────────────────────────────

        [Fact]
        public void TheGen7Queries_AreOpenRgbsAndTheToolkits()
        {
            var gen7 = LenovoLegionLighting.Format.Gen7;
            // {REPORT_ID, GET_ACTIVE_PROFILE, 0xC0, 0x03} (LenovoUSBController_Gen7_8.cpp:232).
            Assert.Equal("07 CA C0 03 00", Hex(LenovoLegionLighting.ActiveProfileQuery(gen7), 5));
            // {REPORT_ID, SWITCH_PROFILE, 0xC0, 0x03, profile} (line 261).
            Assert.Equal("07 C8 C0 03 03 00", Hex(LenovoLegionLighting.ProfileSwitch(3, gen7), 6));
            // {REPORT_ID, GET_PROFILE, 0xC0, 0x03, profile} (line 269).
            Assert.Equal("07 CC C0 03 05 00", Hex(LenovoLegionLighting.ContentsQuery(5, gen7), 6));
            // setLedsDirectOn and setLedsDirectOff (lines 134-166).
            Assert.Equal("07 D0 C0 03 01 05 00", Hex(LenovoLegionLighting.StreamOn(5), 7));
            Assert.Equal("07 D0 C0 03 02 05 00", Hex(LenovoLegionLighting.StreamOff(5, gen7), 7));
            // Lenovo Legion Toolkit Native.cs:217-242, header 07 type C0 03.
            Assert.Equal("07 C4 C0 03 07 00", Hex(LenovoLegionLighting.KeyCountQuery(), 6));
            Assert.Equal("07 C5 C0 03 07 02", Hex(LenovoLegionLighting.KeyPageQuery(2), 6));
            Assert.Equal("07 C5 C0 03 08 00", Hex(LenovoLegionLighting.KeyPageQuery(0, outside: true), 6));
            Assert.All(new[]
            {
                LenovoLegionLighting.ActiveProfileQuery(gen7), LenovoLegionLighting.StreamOn(5),
                LenovoLegionLighting.KeyCountQuery(),
            }, f => Assert.Equal(960, f.Length));
        }

        [Fact]
        public void TheGen7StreamGroup_IsOpenRgbsDirectEntry()
        {
            var lights = new[]
            {
                new LightInfo(0x0001, LightKind.Key, 0x29, LightKeySource.Generated),
                new LightInfo(0x03E9, LightKind.Edge, 0, LightKeySource.None),
            };
            var frame = LenovoLegionLighting.StreamGroupFrame(5, lights, LenovoLegionLighting.Format.Gen7);
            // setLedsByGroup: 07 CB, byte 2 the whole length (uint8_t)i and
            // byte 3 0x03 (LenovoUSBController_Gen7_8.cpp:83-84 and 128),
            // profile 01 01, group 1 with mode 0x0D, speed 0, spin 0,
            // direction 0x03, color mode 0x02, one black color, two lights
            // (RGBController_Lenovo_Gen7_8.cpp:692-803). 7 + 19 + 4 = 30.
            Assert.Equal("07 CB 1E 03 05 01 01 01 06 01 0D 02 00 03 00 04 03 05 02 06 00 01 00 00 00 02 01 00 E9 03 00",
                Hex(frame, 31));
        }

        [Fact]
        public void TheGen7DirectFrame_IsOpenRgbsSetLedsDirect()
        {
            var lights = new[]
            {
                new LightInfo(0x0001, LightKind.Key, 0x29, LightKeySource.Generated),
                new LightInfo(0x03E9, LightKind.Edge, 0, LightKeySource.None),
            };
            var frame = LenovoLegionLighting.DirectFrame(lights, new[] { 0x112233, 0xAABBCC });
            // 07 A1 C0 03, then address low, high, R, G, B (lines 201-219).
            Assert.Equal("07 A1 C0 03 01 00 11 22 33 E9 03 AA BB CC 00", Hex(frame, 15));
            Assert.Equal(960, frame.Length);
            // A frame holds 191 lights: the 192nd would run past it.
            var many = Enumerable.Range(0, 200).Select(n => new LightInfo(n, LightKind.Key, 0, LightKeySource.None)).ToArray();
            var full = LenovoLegionLighting.DirectFrame(many, Enumerable.Repeat(0xFFFFFF, 200).ToArray());
            Assert.Equal(191, LenovoLegionLighting.MaxDirectLights);
            Assert.Equal(0xBE, full[4 + 190 * 5]);
        }

        [Fact]
        public void TheGen7Restore_CarriesItsLengthInByteTwo()
        {
            // An answer's contents, its two length bytes first, as the Gen 10
            // answer holds them: before Gen 10 the frame carries its whole
            // length in byte 2 and 0x03 in byte 3 (setLedsByGroup, line
            // 128), here 4 + 5 = 9.
            var contents = new byte[] { 0xC0, 0x03, 0x05, 0x01, 0x01, 0x01, 0x06 };
            Assert.Equal("07 CB 09 03 05 01 01 01 06 00", Hex(LenovoLegionLighting.RestoreFrame(contents, LenovoLegionLighting.Format.Gen7), 10));
            // Gen 10 sends the answer back as it came.
            Assert.Equal("07 CB C0 03 05 01 01 01 06 00", Hex(LenovoLegionLighting.RestoreFrame(contents), 10));
        }

        [Fact]
        public void AGen7Picture_IsToldFromOthers_WithoutItsLengthBytes()
        {
            var lights = LenovoLegionLighting.Gen7Lights(LenovoLegionLighting.Legion7Gen8);
            var addresses = lights.Select(l => l.Address).ToHashSet();
            var group = LenovoLegionLighting.StreamGroupFrame(5, lights, LenovoLegionLighting.Format.Gen7);
            group[1] = LenovoLegionLighting.GetProfile;
            var contents = LenovoLegionLighting.ContentsOf(group, 5);
            Assert.NotNull(contents);
            // Byte 2 holds the whole length, not the payload's, so the Gen 10
            // check of the length bytes would refuse it.
            Assert.False(LenovoLegionLighting.IsOwnPicture(contents, 5, addresses));
            Assert.True(LenovoLegionLighting.IsOwnPicture(contents, 5, addresses, LenovoLegionLighting.Format.Gen7));
            Assert.True(LenovoLegionLighting.HoldsStream(contents, 5, addresses, LenovoLegionLighting.Format.Gen7));
        }

        // ─────────────────────────────────────────────
        //  The models
        // ─────────────────────────────────────────────

        private static HidCollectionInfo Spectrum(ushort pid) => Collection(0x048D, pid, 0xFF89, 0x07, feature: 960);

        [Theory]
        [InlineData(0xC977, 111)]
        [InlineData(0xC978, 120)]
        [InlineData(0xC988, 111)]
        [InlineData(0xC987, 111)]
        [InlineData(0xC997, 111)]
        [InlineData(0xC998, 111)]
        public void AGen7Keyboard_Streams_WithOpenRgbsZones(int pid, int lights)
        {
            var model = HidLightingFamilies.Match(Spectrum((ushort)pid));
            Assert.Same(LenovoLegionLighting.Instance, model.Family);
            // The keyboard and the neon, and on the Legion 7 Gen 7 the logo
            // and eight vents (RGBController_Lenovo_Gen7_8.cpp:401-422),
            // then the five region keys.
            Assert.Equal(lights, LenovoLegionLighting.Gen7Lights((ushort)pid).Count);
            Assert.Equal(lights + 5, model.Map.Lights.Count);
            // Lenovo Legion Toolkit's Aurora Sync interval, no hold.
            Assert.Equal(0, model.HoldMs);
            Assert.Equal(60, model.RepaintMs);
            Assert.False(model.LiveThroughLampArray);
            Assert.True(model.LitBesideWindows);
            Assert.Equal($"lenovo:048D:{pid:X4}", model.Map.Product);
        }

        [Fact]
        public void TheLegion7Gen7_HasItsLogoAndEightVents()
        {
            var lights = LenovoLegionLighting.Gen7Lights(LenovoLegionLighting.Legion7Gen7);
            Assert.Equal(Enumerable.Range(0xF5, 10).Select(n => 0x100 | n), lights.Skip(101).Take(10).Select(l => l.Address));
            Assert.Equal(0x05DD, lights[111].Address);
            Assert.Equal(LightKind.Logo, lights[111].Kind);
            Assert.Equal(Enumerable.Range(0xE9, 8).Select(n => 0x300 | n), lights.Skip(112).Select(l => l.Address));
        }

        [Fact]
        public void TheLegion5Gen10_HasTwentyFourZones_AndTakesStillPictures()
        {
            var model = HidLightingFamilies.Match(Spectrum(0xC195));
            Assert.Same(LenovoLegionLighting.Instance, model.Family);
            Assert.Equal(24, model.Map.Lights.Count);
            Assert.Equal(24, model.Map.ZoneCount);
            Assert.Equal(Enumerable.Range(1, 24), model.Map.Lights.Select(l => l.Address));
            Assert.Equal(Enumerable.Range(1, 24), model.Map.Lights.Select(l => l.Zone));
            Assert.Equal(150, model.HoldMs);
            Assert.Equal(500, model.RepaintMs);
        }

        [Fact]
        public void OnlyTheGen10Keyboards_NeverStream()
        {
            Assert.Equal(LenovoLegionLighting.RepaintMs, HidLightingFamilies.Match(Spectrum(0xC197)).RepaintMs);
            Assert.Equal(LenovoLegionLighting.RepaintMs, HidLightingFamilies.Match(Spectrum(0xC195)).RepaintMs);
            foreach (ushort pid in new ushort[] { 0xC977, 0xC978, 0xC988, 0xC987, 0xC997, 0xC998 })
                Assert.Equal(LenovoLegionLighting.StreamRepaintMs, HidLightingFamilies.Match(Spectrum(pid)).RepaintMs);
        }

        // ─────────────────────────────────────────────
        //  The key map and the boards
        // ─────────────────────────────────────────────

        [Fact]
        public void TheLayout_IsTheToolkitsRule()
        {
            // GetKeyboardLayoutAsync (SpectrumKeyboardBacklightController.cs:113-118).
            Assert.Equal(LenovoLegionLighting.Layout.Jis, LenovoLegionLighting.LayoutOf(new HashSet<int> { 0xA8, 0xA9 }));
            Assert.Equal(LenovoLegionLighting.Layout.Iso, LenovoLegionLighting.LayoutOf(new HashSet<int> { 0xA8 }));
            Assert.Equal(LenovoLegionLighting.Layout.Ansi, LenovoLegionLighting.LayoutOf(new HashSet<int> { 0x4E }));
        }

        /// <summary>The key codes of Lenovo Legion Toolkit's ISO drawing
        /// (SpectrumKeyboardISOControl.xaml), row by row.</summary>
        internal static readonly int[] IsoCodes =
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
            0x11, 0x12, 0x13, 0x14,
            0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x38,
            0x40, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D,
            0x55, 0x6D, 0x6E, 0x58, 0x59, 0x5A, 0x71, 0x72, 0x5B, 0x5C, 0x5D, 0x5F, 0xA8,
            0x77,
            0x6A, 0x4E, 0x82, 0x83, 0x6F, 0x70, 0x87, 0x88, 0x73, 0x74, 0x75, 0x76, 0x8D,
            0x7F, 0x80, 0x96, 0x97, 0x98, 0x9A, 0x9B, 0x9D,
            0x9C, 0x9F, 0xA1,
        };

        /// <summary>The key codes of Lenovo Legion Toolkit's JIS drawing
        /// (SpectrumKeyboardJisControl.xaml), row by row.</summary>
        internal static readonly int[] JisCodes =
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
            0x11, 0x12, 0x13, 0x14,
            0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0xA8, 0x38,
            0x40, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x60, 0x4C,
            0x55, 0x6D, 0x6E, 0x58, 0x59, 0x5A, 0x71, 0x72, 0x5B, 0x5C, 0x5D, 0x5F, 0x4D,
            0x77,
            0x6A, 0x4E, 0x82, 0x83, 0x6F, 0x70, 0x87, 0x88, 0x73, 0x74, 0x75, 0x76, 0x8D,
            0x7F, 0x80, 0x96, 0x97, 0xA9, 0x98, 0xAA, 0xAB, 0x9B, 0x9D,
            0x9C, 0x9F, 0xA1,
        };

        private static ushort UsageAt(LightMap map, int address) => map.Lights.Single(l => l.Address == address).Usage;

        [Fact]
        public void AnIsoBoard_NamesItsKeysByTheirPlaces()
        {
            var lights = LenovoLegionLighting.WithRegionKeys(LenovoLegionLighting.Gen10Lights());
            var map = LenovoLegionLighting.RegionMap(lights, IsoCodes.ToHashSet(), "p");
            // Left of Z: the ISO \ key. Right of ': the ISO # key.
            Assert.Equal(0x64, UsageAt(map, 0x4E));
            Assert.Equal(0x32, UsageAt(map, 0xA8));
            Assert.Equal(0x28, UsageAt(map, 0x77));
            Assert.Equal(0x2F, UsageAt(map, 0x4C));
            // The drawing has no number pad: those keys are left out, the
            // edge lights kept.
            Assert.DoesNotContain(map.Lights, l => l.Address == 0x26);
            Assert.Contains(map.Lights, l => l.Address == 0x01F5);
            Assert.Equal(IsoCodes.Length + 29, map.Lights.Count);
            Assert.All(map.Lights.Where(l => l.Kind == LightKind.Key && l.Usage != 0),
                l => Assert.Equal(LightKeySource.Device, l.KeySource));
            Assert.Equal("p", map.Product);
        }

        [Fact]
        public void AJisBoard_NamesItsKeysByTheirPlaces()
        {
            var lights = LenovoLegionLighting.WithRegionKeys(LenovoLegionLighting.Gen10Lights());
            var map = LenovoLegionLighting.RegionMap(lights, JisCodes.ToHashSet(), "p");
            Assert.Equal(0x2F, UsageAt(map, 0x60));   // @
            Assert.Equal(0x30, UsageAt(map, 0x4C));   // [
            Assert.Equal(0x32, UsageAt(map, 0x4D));   // ]
            Assert.Equal(0x89, UsageAt(map, 0xA8));   // ¥
            Assert.Equal(0x8B, UsageAt(map, 0xA9));   // 無変換
            Assert.Equal(0x8A, UsageAt(map, 0xAA));   // 変換
            Assert.Equal(0x88, UsageAt(map, 0xAB));   // かな
            Assert.Equal(0x35, UsageAt(map, 0x16));   // 半角/全角 where ` sits
            // Where the drawing and a JIS board disagree, Map Key Lights names it.
            Assert.Equal(0, UsageAt(map, 0x4E));
            // No right Alt on the drawing.
            Assert.DoesNotContain(map.Lights, l => l.Address == 0x9A);
        }

        [Fact]
        public void AKeyPageAnswer_CountsOnlyForItsOwnRequest()
        {
            var answer = new byte[960];
            answer[0] = 0x07;
            answer[1] = 0xC5;
            answer[4] = 0x07;
            answer[5] = 0x02;
            // Index 0, code 0x0016; index 1, code 0x01F5.
            answer[6] = 0;
            answer[7] = 0x16;
            answer[9] = 1;
            answer[10] = 0xF5;
            answer[11] = 0x01;
            var keys = new HashSet<int>();
            Assert.True(LenovoLegionLighting.KeysOf(answer, 0x07, 2, 20, keys));
            Assert.Equal(new[] { 0x16, 0x1F5 }, keys.OrderBy(k => k));
            // Another row, the other page, another opcode.
            Assert.False(LenovoLegionLighting.KeysOf(answer, 0x07, 3, 20, new HashSet<int>()));
            Assert.False(LenovoLegionLighting.KeysOf(answer, 0x08, 2, 20, new HashSet<int>()));
            answer[1] = 0xC4;
            Assert.False(LenovoLegionLighting.KeysOf(answer, 0x07, 2, 20, new HashSet<int>()));
        }

        [Fact]
        public void AKeyNoTableHolds_IsAddedUnnamed()
        {
            var lights = LenovoLegionLighting.Gen10Lights();
            var keys = LenovoLegionLighting.Keys.Select(k => (int)k.Number).ToHashSet();
            keys.Add(0xB0);
            keys.Add(0x01F5);   // outside the keyboard zone: not a key
            var map = LenovoLegionLighting.RegionMap(lights, keys, "p");
            var added = map.Lights.Single(l => l.Address == 0xB0);
            Assert.Equal(LightKind.Key, added.Kind);
            Assert.Equal(0, added.Usage);
            Assert.Equal(lights.Count + 1, map.Lights.Count);
        }

        // ─────────────────────────────────────────────
        //  The Y740 to Y760 controller
        // ─────────────────────────────────────────────

        private static HidCollectionInfo Y7(ushort pid) => Collection(0x048D, pid, 0xFF89, 0x07, feature: 192);

        [Fact]
        public void TheY7Family_AnswersOnItsFiveProducts()
        {
            foreach (ushort pid in new ushort[] { 0xC935, 0xC956, 0xC957, 0xC968, 0xC967 })
            {
                var model = HidLightingFamilies.Match(Y7(pid));
                Assert.Same(LenovoLegionUsbLighting.Instance, model.Family);
            }
            // A 960-byte collection of these products is not refused, and a
            // shorter one is.
            Assert.NotNull(HidLightingFamilies.Match(Collection(0x048D, 0xC968, 0xFF89, 0x07, feature: 960)));
            Assert.Null(HidLightingFamilies.Match(Collection(0x048D, 0xC968, 0xFF89, 0x07, feature: 64)));
        }

        [Fact]
        public void TheY7Instructions_AreOpenRgbs()
        {
            // sendBasicInstruction: 0x07 and the code, 192 bytes
            // (LenovoUSBController.cpp:87-125).
            var on = LenovoLegionUsbLighting.Instruction(0xB2);
            Assert.Equal(192, on.Length);
            Assert.Equal("07 B2 00", Hex(on, 3));
            Assert.Equal("07 B1 00", Hex(LenovoLegionUsbLighting.Instruction(0xB1), 3));
        }

        [Fact]
        public void TheY7Frames_GoZoneByZone_FortySevenAReport()
        {
            var lights = LenovoLegionUsbLighting.Lights(LenovoLegionUsbLighting.LegionY760, LenovoLegionUsbLighting.Board.Ansi);
            // 145 keys, 13 logo, 106 vents in four zones of the same ID, 99 neon.
            Assert.Equal(363, lights.Count);
            var colors = Enumerable.Range(0, lights.Count).Select(n => 0x010203 * (n % 50)).ToArray();
            var frames = LenovoLegionUsbLighting.ZoneFrames(lights, colors);
            // Zone 1: 47 + 47 + 47 + 4, zone 2: 13, zone 3: 47 + 47 + 12,
            // zone 4: 47 + 47 + 5 (setZoneLeds, LenovoUSBController.cpp:48-79).
            Assert.Equal(new[] { 47, 47, 47, 4, 13, 47, 47, 12, 47, 47, 5 }, frames.Select(f => (int)f[2]));
            Assert.Equal(new byte[] { 0xA1, 0xA1, 0xA1, 0xA1, 0xA2, 0xA3, 0xA3, 0xA3, 0xA4, 0xA4, 0xA4 }, frames.Select(f => f[1]));
            // 07 A1 count 00, then number R G B: Escape 0x01 at color 0, F1
            // 0x02 at color 0x010203.
            Assert.Equal("07 A1 2F 00 01 00 00 00 02 01 02 03", Hex(frames[0], 12));
            Assert.All(frames, f => Assert.Equal(192, f.Length));
        }

        [Theory]
        [InlineData(0xC935, 100, 15, -1, "Ansi15")]
        [InlineData(0xC935, 101, 20, -1, "Iso")]
        [InlineData(0xC935, 101, 49, -1, "Ansi")]
        [InlineData(0xC935, 50, 48, -1, "Iso15")]
        [InlineData(0xC956, -1, 41, -1, "Japan")]
        [InlineData(0xC956, -1, 40, -1, "Iso")]
        [InlineData(0xC956, -1, 15, -1, "Ansi")]
        [InlineData(0xC957, 0x97, -1, -1, "Japan")]
        [InlineData(0xC957, 0x91, -1, -1, "Iso")]
        [InlineData(0xC968, -1, -1, 41, "Japan")]
        [InlineData(0xC968, -1, -1, 16, "Iso")]
        [InlineData(0xC967, 0x8F, -1, -1, "Iso")]
        [InlineData(0xC967, 0x91, -1, -1, "Ansi")]
        public void TheBoard_IsOpenRgbsRulePerModel(int pid, int info1, int info4, int info7, string board)
        {
            // RGBController_LenovoUSB.cpp:89-184.
            byte? B(int v) => v < 0 ? null : (byte)v;
            Assert.Equal(board, LenovoLegionUsbLighting.BoardOf((ushort)pid, B(info1), B(info4), B(info7)).ToString());
        }

        [Fact]
        public void NoAnswer_LeavesOpenRgbsDefault()
        {
            Assert.Equal(LenovoLegionUsbLighting.Board.Ansi, LenovoLegionUsbLighting.BoardOf(0xC935, null, null, null));
            Assert.Equal(LenovoLegionUsbLighting.Board.Ansi, LenovoLegionUsbLighting.BoardOf(0xC968, null, null, null));
            Assert.Equal(new byte[] { 0x01, 0x04 }, LenovoLegionUsbLighting.InfoIds(0xC935));
            Assert.Equal(new byte[] { 0x04 }, LenovoLegionUsbLighting.InfoIds(0xC956));
            Assert.Equal(new byte[] { 0x07 }, LenovoLegionUsbLighting.InfoIds(0xC968));
            Assert.Equal(new byte[] { 0x01 }, LenovoLegionUsbLighting.InfoIds(0xC957));
            Assert.Equal(new byte[] { 0x01 }, LenovoLegionUsbLighting.InfoIds(0xC967));
        }

        [Fact]
        public void TheY7Boards_HaveOpenRgbsZones()
        {
            ushort Y740 = LenovoLegionUsbLighting.LegionY740;
            // 104 keys and four single zones of ID 1 (LenovoDevices.h:1600-1688).
            var y740 = LenovoLegionUsbLighting.Lights(Y740, LenovoLegionUsbLighting.Board.Ansi);
            Assert.Equal(108, y740.Count);
            Assert.Equal(new[] { 0x196, 0x197, 0x199, 0x198 }, y740.Skip(104).Select(l => l.Address));
            Assert.Equal(94, LenovoLegionUsbLighting.Lights(Y740, LenovoLegionUsbLighting.Board.Ansi15).Count);
            Assert.Equal(93, LenovoLegionUsbLighting.Lights(Y740, LenovoLegionUsbLighting.Board.Iso15).Count);
            Assert.Equal(107, LenovoLegionUsbLighting.Lights(Y740, LenovoLegionUsbLighting.Board.Iso).Count);
            // The Y750: the keyboard and logo, vents and neon (lines 1548-1591).
            var y750 = LenovoLegionUsbLighting.Lights(LenovoLegionUsbLighting.LegionY750, LenovoLegionUsbLighting.Board.Japan);
            Assert.Equal(149 + 3, y750.Count);
            Assert.Equal(new[] { 0x1AA, 0x1AD, 0x1AC }, y750.Skip(149).Select(l => l.Address));
            // The S models: the keyboard alone.
            Assert.Equal(143, LenovoLegionUsbLighting.Lights(LenovoLegionUsbLighting.LegionY760S, LenovoLegionUsbLighting.Board.Iso).Count);
        }

        [Fact]
        public void TheY760Tables_NameTheirKeysByTheirPlaces()
        {
            var ansi = new LightMap(LenovoLegionUsbLighting.Lights(LenovoLegionUsbLighting.LegionY760, LenovoLegionUsbLighting.Board.Ansi), "t");
            // The # legend over 3 is KEY_EN_POUND in OpenRGB's table
            // (LenovoDevices.h:191), and is not the ISO # key.
            Assert.Equal(0, UsageAt(ansi, 0x119));
            Assert.Equal(0x20, UsageAt(ansi, 0x12E));   // 3
            // Three lights under Backspace (lines 220-222).
            Assert.Equal(3, ansi.IndexesOf(0x2A).Count);
            var jp = new LightMap(LenovoLegionUsbLighting.Lights(LenovoLegionUsbLighting.LegionY760, LenovoLegionUsbLighting.Board.Japan), "t");
            Assert.Equal(0x2F, UsageAt(jp, 0x161));   // KEY_JP_AT
            Assert.Equal(0x30, UsageAt(jp, 0x162));   // [
            Assert.Equal(0x32, UsageAt(jp, 0x178));   // ]
            Assert.Equal(0x34, UsageAt(jp, 0x160));   // KEY_JP_COLON
            Assert.Equal(0x2E, UsageAt(jp, 0x137));   // KEY_JP_CHEVRON
            Assert.Equal(0x87, UsageAt(jp, 0x18C));   // KEY_JP_RO
            Assert.Equal(0x89, UsageAt(jp, 0x138));   // KEY_JP_YEN
            Assert.Equal(0x35, UsageAt(jp, 0x12B));   // "kanji"
            Assert.Equal(0x35, UsageAt(jp, 0x116));   // KEY_JP_ZENKAKU
            Assert.Equal(0, UsageAt(jp, 0x121));      // the = legend
            Assert.Equal(0, UsageAt(jp, 0x14C));      // the ` legend
            var iso = new LightMap(LenovoLegionUsbLighting.Lights(LenovoLegionUsbLighting.LegionY760, LenovoLegionUsbLighting.Board.Iso), "t");
            Assert.Equal(0x32, UsageAt(iso, 0x178));  // KEY_EN_POUND, the ISO # key
            Assert.Equal(0x64, UsageAt(iso, 0x181));  // KEY_EN_ISO_BACK_SLASH
            // Legend lights no table names wait for Map Key Lights.
            Assert.Equal(0, UsageAt(iso, 0x177));     // "Key: ~"
            Assert.Equal(LightKind.Key, iso.Lights.Single(l => l.Address == 0x177).Kind);
            // Lights outside the keyboard take their zone's kind.
            Assert.Equal(LightKind.Logo, ansi.Lights.Single(l => l.Address == 0x201).Kind);
            Assert.Equal(LightKind.Edge, ansi.Lights.Single(l => l.Address == 0x401).Kind);
        }

        // ─────────────────────────────────────────────
        //  The 4-zone keyboards
        // ─────────────────────────────────────────────

        [Fact]
        public void TheFourZoneFrame_IsOpenRgbsStaticState_AtTheHigherBrightness()
        {
            var lights = Lenovo4ZoneLighting.Lights();
            Assert.Equal(new[] { 0, 1, 2, 3 }, lights.Select(l => l.Address));
            Assert.Equal(new[] { 1, 2, 3, 4 }, lights.Select(l => l.Zone));
            var frame = Lenovo4ZoneLighting.Frame(lights, new[] { 0x112233, 0x445566, 0x778899, 0xAABBCC });
            // CC 16, effect 1, speed 1, brightness 2, four colors, 00, wave
            // 00 00 (Lenovo4ZoneUSBController.cpp:30-42, LenovoDevices4Zone.h:55-64,
            // brightness 2 per RGBKeyboardBacklightController.cs:336-341).
            Assert.Equal("CC 16 01 01 02 11 22 33 44 55 66 77 88 99 AA BB CC 00 00 00", Hex(frame, 20));
            Assert.Equal(33, frame.Length);
            Assert.All(frame.Skip(20), b => Assert.Equal(0, b));
        }

        [Theory]
        [InlineData(0xC963)]
        [InlineData(0xC985)]
        [InlineData(0xC984)]
        [InlineData(0xC975)]
        [InlineData(0xC973)]
        [InlineData(0xC965)]
        [InlineData(0xC955)]
        [InlineData(0xC995)]
        [InlineData(0xC994)]
        [InlineData(0xC993)]
        [InlineData(0xC983)]
        public void TheFourZoneFamily_AnswersOnEveryProductTheReferencesList(int pid)
        {
            var model = HidLightingFamilies.Match(Collection(0x048D, (ushort)pid, 0xFF89, 0xCC, feature: 33));
            Assert.Same(Lenovo4ZoneLighting.Instance, model.Family);
            Assert.Equal(4, model.Map.ZoneCount);
            Assert.Equal(20, model.RepaintMs);
            Assert.NotNull(model.Owed);
            // The Spectrum usage on the same product is not this family's.
            Assert.Null(Lenovo4ZoneLighting.Instance.Match(Collection(0x048D, (ushort)pid, 0xFF89, 0x07, feature: 960)));
        }

        [Fact]
        public void EveryLenovoModel_NamesTheProgramsThatLightIt()
        {
            foreach (ushort pid in new ushort[] { 0xC977, 0xC978, 0xC988, 0xC987, 0xC997, 0xC998, 0xC197, 0xC195 })
                Assert.Same(VendorPrograms.LenovoSpectrum, HidLightingFamilies.Match(Spectrum(pid)).Programs);
            foreach (ushort pid in new ushort[] { 0xC935, 0xC956, 0xC957, 0xC968, 0xC967 })
                Assert.Equal(new[] { VendorPrograms.LenovoVantage, VendorPrograms.CorsairIcue },
                    HidLightingFamilies.Match(Y7(pid)).Programs);
            foreach (ushort pid in Lenovo4ZoneLighting.Products.Keys)
                Assert.Equal(new[] { VendorPrograms.LenovoVantage },
                    HidLightingFamilies.Match(Collection(0x048D, pid, 0xFF89, 0xCC, feature: 33)).Programs);
            // Legion Accessory Central lights the K500 and the M300, by its
            // installer's modules. It carries none for the K510.
            Assert.Empty(HidLightingFamilies.Match(Collection(0x17EF, 0x619A, 0xFF1C, 0x92, output: 64, iface: 1)).Programs);
            Assert.Equal(new[] { VendorPrograms.LegionAccessoryCentral },
                HidLightingFamilies.Match(Collection(0x17EF, 0x60E4, 0xFF01, 0x01, output: 0x41)).Programs);
            Assert.Equal(new[] { VendorPrograms.LegionAccessoryCentral },
                HidLightingFamilies.Match(Collection(0x17EF, 0x60D5, 0xFF00, 0x01, output: 65, iface: 3)).Programs);
            // Every program a model names is in the one list the presence reads.
            Assert.Contains(VendorPrograms.CorsairIcue, VendorPrograms.All);
            Assert.Contains(VendorPrograms.LegionAccessoryCentral, VendorPrograms.All);
            Assert.True(VendorPrograms.CorsairIcue.Owns("iCUE"));
            // The installer's three programs (app\legion_hid.exe,
            // legion_haw.exe, legion_api.exe).
            Assert.Equal(new[] { "legion_hid", "legion_haw", "legion_api" }, VendorPrograms.LegionAccessoryCentral.Processes);
            Assert.True(VendorPrograms.LegionAccessoryCentral.Owns("LEGION_HID"));
            Assert.False(VendorPrograms.LegionAccessoryCentral.Owns("LegionSpace"));
        }

        // ─────────────────────────────────────────────
        //  The accessories
        // ─────────────────────────────────────────────

        [Fact]
        public void TheK510Reports_AreOpenRgbs()
        {
            // 04, 06 38 in bytes 3 and 4, mode 06, brightness 2, speed 0,
            // direction byte 1 for MODE_DIRECTION_LEFT, color flag 0, color
            // (LenovoK510Controller.cpp:40-75).
            var report = LenovoK510Lighting.StaticReport(0x112233);
            Assert.Equal("04 00 00 06 38 00 00 00 00 06 02 00 01 00 11 22 33 00", Hex(report, 18));
            Assert.Equal(64, report.Length);
            Assert.Equal("04 00 00 05 38 00", Hex(LenovoK510Lighting.StateRequest(), 6));
            var restore = LenovoK510Lighting.RestoreReport(new byte[] { 0x0D, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00 });
            Assert.Equal("04 00 00 06 38 00 00 00 00 0D 01 03 00 01 00 00 00 00", Hex(restore, 18));
            Assert.True(LenovoK510Lighting.IsOwnPicture(report.AsSpan(9, 8).ToArray()));
            Assert.False(LenovoK510Lighting.IsOwnPicture(restore.AsSpan(9, 8).ToArray()));
        }

        [Fact]
        public void TheK510_AnswersOnItsInterfaceAlone()
        {
            Assert.Same(LenovoK510Lighting.Instance,
                HidLightingFamilies.Match(Collection(0x17EF, 0x619A, 0xFF1C, 0x92, output: 64, iface: 1)).Family);
            Assert.Null(HidLightingFamilies.Match(Collection(0x17EF, 0x619A, 0xFF1C, 0x92, output: 64, iface: 2)));
            Assert.Null(HidLightingFamilies.Match(Collection(0x17EF, 0x619A, 0x0001, 0x06, output: 64, iface: 1)));
        }

        [Fact]
        public void TheM300Report_IsOpenRgbs_WithItsChecksum()
        {
            var report = LenovoM300Lighting.StaticReport(0x112233);
            // 00 25 00 01 01 0C 03 64 00 R G B, checksum of bytes 0 to 0x0B
            // in 0x40: 0x25 + 1 + 1 + 0x0C + 3 + 0x64 + 0x11 + 0x22 + 0x33 =
            // 0x100, so 00 (LenovoM300Controller.cpp:39-94).
            Assert.Equal("00 25 00 01 01 0C 03 64 00 11 22 33 00", Hex(report, 13));
            Assert.Equal(0x41, report.Length);
            Assert.Equal(0x00, report[0x40]);
            Assert.Equal(0x01, LenovoM300Lighting.StaticReport(0x112234)[0x40]);
            Assert.Same(LenovoM300Lighting.Instance,
                HidLightingFamilies.Match(Collection(0x17EF, 0x60E4, 0xFF01, 0x01, output: 0x41)).Family);
        }

        [Fact]
        public void TheK500_LightsOneColorAsStatic_AndOthersAsItsKeyMask()
        {
            var lights = LenovoK500Lighting.Lights();
            Assert.Equal(104, lights.Count);
            Assert.Equal(0x29, lights[0].Usage);
            Assert.Equal("00 14 00 5A 00 00 00 00 00", Hex(LenovoK500Lighting.Prelude(), 9));

            // One color: the static mode 01, brightness 2, speed 3, the color
            // over the default palette (LenovoK500Controller.cpp:133-139 and
            // 163-239).
            var same = LenovoK500Lighting.ModeReport(lights, Enumerable.Repeat(0x112233, 104).ToArray());
            Assert.Equal("00 01 01 02 03", Hex(same, 5));
            Assert.Equal("11 22 33 00 FF FF FF 00 00 FF 00", Hex(same, 0x11, 11));
            Assert.All(same.Skip(0x31), b => Assert.Equal(0, b));

            // Escape red, F8 green, the rest black: custom mode 06, Escape's
            // bit 0 of byte 0 and F8's bit 0 of byte 1, in the first color.
            var colors = new int[104];
            colors[0] = 0xFF0000;
            colors[8] = 0x00FF00;
            var custom = LenovoK500Lighting.ModeReport(lights, colors);
            Assert.Equal("00 01 06 02 03", Hex(custom, 5));
            Assert.Equal("FF 00 00", Hex(custom, 0x11, 3));
            Assert.Equal("01 01 00", Hex(custom, 0x31, 3));
            Assert.Equal(65, custom.Length);
        }

        [Fact]
        public void TheK500_AnswersOnItsCollectionThatTakesTheModeReport()
        {
            Assert.Same(LenovoK500Lighting.Instance,
                HidLightingFamilies.Match(Collection(0x17EF, 0x60D5, 0xFF00, 0x01, output: 65, iface: 3)).Family);
            Assert.Null(HidLightingFamilies.Match(Collection(0x17EF, 0x60D5, 0x0001, 0x06, output: 0, iface: 3)));
            Assert.Null(HidLightingFamilies.Match(Collection(0x17EF, 0x60D5, 0xFF00, 0x01, output: 65, iface: 1)));
        }
    }

    /// <summary>A Lenovo lighting device the accessory and Y740 to Y760
    /// tests script: it logs every report it is sent and answers a feature
    /// read or an input read from what the test gives it.</summary>
    internal sealed class FakeLenovoDevice
    {
        private readonly object _gate = new();
        private byte[] _lastFeature;
        private byte[] _lastOutput;
        private readonly List<string> _calls = new();
        public readonly List<byte[]> Features = new();
        public readonly List<byte[]> Outputs = new();

        /// <summary>The answer to a feature read after a feature report.</summary>
        public Func<byte[], byte[]> FeatureAnswer = _ => null;

        /// <summary>The input report after an output report, or null.</summary>
        public Func<byte[], byte[]> InputAnswer = _ => null;

        public void SetFeature(byte[] report)
        {
            lock (_gate)
            {
                _lastFeature = (byte[])report.Clone();
                Features.Add(_lastFeature);
                _calls.Add("F " + LenovoLegionLightingTests.Hex(report, 2));
            }
        }

        public void WriteOutput(byte[] report)
        {
            lock (_gate)
            {
                _lastOutput = (byte[])report.Clone();
                Outputs.Add(_lastOutput);
                _calls.Add("O " + LenovoLegionLightingTests.Hex(report, 5));
            }
        }

        public byte[] Feature()
        {
            lock (_gate) return _lastFeature == null ? null : FeatureAnswer(_lastFeature);
        }

        public byte[] Input()
        {
            lock (_gate)
            {
                var answer = _lastOutput == null ? null : InputAnswer(_lastOutput);
                _lastOutput = null;
                return answer;
            }
        }

        public List<string> Log()
        {
            lock (_gate) return _calls.ToList();
        }
    }

    internal sealed class FakeLenovoChannel : IHidReportChannel
    {
        public FakeLenovoDevice Device { get; init; }
        public string Path { get; init; }
        public bool FeatureOnly => false;
        public int InputReportLength => 64;
        public int OutputReportLength => 65;
        public int FeatureReportLength => 192;
        public volatile bool Disposed;

        public bool SetFeature(byte[] report)
        {
            if (Disposed) return false;
            Device.SetFeature(report);
            return true;
        }

        public bool GetFeature(byte reportId, byte[] buffer)
        {
            if (Disposed) return false;
            var answer = Device.Feature();
            if (answer == null) return false;
            Array.Clear(buffer);
            Array.Copy(answer, buffer, Math.Min(answer.Length, buffer.Length));
            return true;
        }

        public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs)
        {
            if (Disposed) return false;
            Device.WriteOutput(report);
            return true;
        }

        public int ReadInput(byte[] buffer, int timeoutMs)
        {
            if (Disposed) return -1;
            var answer = Device.Input();
            if (answer == null) return 0;
            Array.Clear(buffer);
            Array.Copy(answer, buffer, Math.Min(answer.Length, buffer.Length));
            return Math.Min(answer.Length, buffer.Length);
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// The shared HID worker against the scripted Lenovo devices other than
    /// the Spectrum keyboards (#557): what each session reads first, what it
    /// writes, what it records for the crash path and the next run, and
    /// what its hand-back sends.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class LenovoLightingBackendTests : IDisposable
    {
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");
        private static readonly Guid Device = new("00000000-0000-0000-0000-0000000000a1");

        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly FakeLenovoDevice _device = new();
        private readonly List<FakeLenovoChannel> _channels = new();
        private long _tick = 1_000_000;

        /// <summary>Every call of the 4-zone hand-back, its owner value, and
        /// what the fake answers. No WMI call is made in the test host.</summary>
        private readonly List<int> _giveBacks = new();
        private volatile bool _giveBackFails;
        private readonly Func<int, bool> _realGiveBack = Lenovo4ZoneLighting.GiveBack;

        public LenovoLightingBackendTests()
        {
            Lenovo4ZoneLighting.GiveBack = owner =>
            {
                lock (_giveBacks) _giveBacks.Add(owner);
                return !_giveBackFails;
            };
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
            KeyboardProfileBackups.ResetForTest(persistAtOnce: true);
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
        }

        public void Dispose()
        {
            SettingsManager.UserDevices = _devices;
            SettingsManager.UserSettings = _settings;
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
            KeyboardProfileBackups.ResetForTest();
            Lenovo4ZoneLighting.GiveBack = _realGiveBack;
        }

        private List<int> GiveBacks()
        {
            lock (_giveBacks) return _giveBacks.ToList();
        }

        private long Tick() => Interlocked.Read(ref _tick);
        private void Advance(long ms) => Interlocked.Add(ref _tick, ms);

        private IHidReportChannel Open(string path)
        {
            var channel = new FakeLenovoChannel { Path = path, Device = _device };
            lock (_channels) _channels.Add(channel);
            return channel;
        }

        private HidLightingBackend Started(HidCollectionInfo present)
        {
            var backend = new HidLightingBackend(null, Open, _ => Box, Tick,
                (_, _, _) => true, (_, _, _) => true, ms => Advance(ms), idleTickMs: 5);
            backend.Start();
            backend.Rescan(new[] { present });
            Passes(backend);
            return backend;
        }

        private static void Passes(HidLightingBackend backend, int count = 2)
        {
            for (int i = 0; i < count; i++)
            {
                int before = backend.PassCount;
                backend.Wake();
                Assert.True(SpinWait.SpinUntil(() => backend.PassCount > before, 30000), "the worker made a pass");
            }
        }

        private static OutputPath PathOf(HidCollectionInfo collection) => new(OutputFamily.HidLightingUnit, collection.Path);

        private static void Link(params OutputPath[] paths)
            => PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Device, Lighting = paths } }));

        private static void Light(int rgb)
            => PeripheralOutputs.SetLighting(Device, slot: 0, player: 1, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        private static void Unlight() => PeripheralOutputs.ReleaseLighting(Device, 0);

        private HidLightingBackend Lit(HidCollectionInfo collection, int rgb = 0x112233, int holdMs = 0)
        {
            var backend = Started(collection);
            Link(PathOf(collection));
            Light(rgb);
            Passes(backend);
            Advance(holdMs);
            Passes(backend);
            return backend;
        }

        private static string Hex(byte[] bytes, int count) => LenovoLegionLightingTests.Hex(bytes, count);

        private static HidCollectionInfo Y760 => new()
        {
            Path = @"\\?\hid#vid_048d&pid_c968&col02#1",
            VendorId = 0x048D, ProductId = 0xC968, UsagePage = 0xFF89, Usage = 0x07, FeatureReportLength = 192,
        };

        private static byte[] Info(byte value)
        {
            var answer = new byte[192];
            answer[0] = 0x07;
            answer[4] = value;
            return answer;
        }

        [Fact]
        public void AY760Claim_EntersSoftwareMode_ReadsTheBoard_AndSendsItsZones()
        {
            _device.FeatureAnswer = f => f[1] == 0x07 ? Info(16) : null;
            using var backend = Lit(Y760);
            var calls = _device.Log();
            // Software mode, the board query, then the zones
            // (LenovoUSBController.cpp:25 and 113, RGBController_LenovoUSB.cpp:151-167).
            Assert.Equal(new[] { "F 07 B2", "F 07 07" }, calls.Take(2));
            Assert.All(calls.Skip(2), c => Assert.Matches("^F 07 A[1-4]$", c));
            Assert.Equal("07 A1 2F 00 01 11 22 33", Hex(_device.Features[2], 8));
            // The ISO board has one Backspace light, 0x3A, where the ANSI
            // board has 0x38 to 0x3A (LenovoDevices.h:220-222 and 388), so
            // the ANSI map's 0x38 goes into no report.
            var keyboard = _device.Features.Skip(2).Where(f => f[1] == 0xA1).ToList();
            var numbers = keyboard.SelectMany(f => Enumerable.Range(0, f[2]).Select(i => (int)f[4 + 4 * i])).ToList();
            Assert.DoesNotContain(0x38, numbers);
            Assert.Contains(0x3A, numbers);
            Assert.Equal(LenovoLegionUsbLighting.SoftwareModeRecord, KeyboardProfileBackups.Get("lenovo:048D:C968")?.Contents);
            // The crash path releases software mode.
            var record = Assert.Single(backend.Recorded);
            Assert.Equal("07 B1", Hex(Assert.Single(record.Frames), 2));
            // The published map is the ISO board's, its # key named, and it
            // stays after the hand-back.
            var map = PeripheralLightMaps.For(PathOf(Y760));
            Assert.Equal(143 + 13 + 106 + 99, map.Lights.Count);
            Assert.Equal((ushort)0x32, map.Lights.Single(l => l.Address == 0x178).Usage);
            Unlight();
            Passes(backend);
            Assert.Same(map, PeripheralLightMaps.For(PathOf(Y760)));
        }

        [Fact]
        public void AY760HandBack_ReleasesSoftwareMode_AndLetsTheRecordGo()
        {
            _device.FeatureAnswer = f => f[1] == 0x07 ? Info(0) : null;
            using var backend = Lit(Y760);
            int before = _device.Log().Count;
            Unlight();
            Passes(backend);
            Assert.Equal(new[] { "F 07 B1" }, _device.Log().Skip(before));
            Assert.Null(KeyboardProfileBackups.Get("lenovo:048D:C968"));
            Assert.Empty(backend.Recorded);
        }

        [Fact]
        public void AY760AKilledRunLeftInSoftwareMode_IsReleased_WithNoClaim()
        {
            KeyboardProfileBackups.LoadRegistry(new[] { ("lenovo:048D:C968", 0, "07B100") });
            using var backend = Started(Y760);
            Assert.Empty(_device.Log());
            Advance(HidLightingBackend.RepairGraceMs);
            Passes(backend);
            Assert.Equal(new[] { "F 07 B1" }, _device.Log());
            Assert.Null(KeyboardProfileBackups.Get("lenovo:048D:C968"));
        }

        private static HidCollectionInfo K510 => new()
        {
            Path = @"\\?\hid#vid_17ef&pid_619a&mi_01#1",
            VendorId = 0x17EF, ProductId = 0x619A, UsagePage = 0xFF1C, Usage = 0x92, OutputReportLength = 64,
            InterfaceNumber = 1,
        };

        private static string K510Key => KeyboardProfileBackups.DeviceKey("lenovo:17EF:619A", K510.Path);

        private static byte[] State(params byte[] state)
        {
            var answer = new byte[64];
            answer[0] = 0x04;
            state.CopyTo(answer, 9);
            return answer;
        }

        private static readonly byte[] Stored = { 0x0D, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00 };

        [Fact]
        public void AK510Claim_SavesTheStoredMode_ThenSetsItsColor_AndTheHandBackWritesItBack()
        {
            _device.InputAnswer = o => o[3] == 0x05 ? State(Stored) : null;
            using var backend = Lit(K510, holdMs: LenovoLegionLighting.HoldMs);
            // The state request, then the static mode in the claim's color.
            Assert.Equal(new[] { "O 04 00 00 05 38", "O 04 00 00 06 38" }, _device.Log());
            Assert.Equal("06 02 00 01 00 11 22 33", Hex(_device.Outputs[1].AsSpan(9, 8).ToArray(), 8));
            Assert.Equal(Stored, KeyboardProfileBackups.Get(K510Key)?.Contents);
            Assert.False(Assert.Single(backend.Recorded).Feature);

            Unlight();
            Passes(backend);
            Assert.Equal(Stored, _device.Outputs[^1].AsSpan(9, 8).ToArray());
            Assert.Equal(0x06, _device.Outputs[^1][3]);
            Assert.Null(KeyboardProfileBackups.Get(K510Key));
        }

        [Fact]
        public void AK510ThatDoesNotAnswer_IsNotWritten()
        {
            using var backend = Lit(K510, holdMs: LenovoLegionLighting.HoldMs);
            Assert.All(_device.Log(), c => Assert.Equal("O 04 00 00 05 38", c));
            Assert.True(Assert.Single(backend.Snapshot.Units).Asleep);
            Assert.Null(KeyboardProfileBackups.Get(K510Key));
        }

        [Fact]
        public void AK510PictureAKilledRunLeft_IsWrittenBack_WithNoClaim()
        {
            KeyboardProfileBackups.LoadRegistry(new[] { (K510Key, 0, Convert.ToHexString(Stored)) });
            _device.InputAnswer = o => o[3] == 0x05 ? State(0x06, 0x02, 0x00, 0x01, 0x00, 0x11, 0x22, 0x33) : null;
            using var backend = Started(K510);
            Advance(HidLightingBackend.RepairGraceMs);
            Passes(backend);
            Assert.Equal(new[] { "O 04 00 00 05 38", "O 04 00 00 06 38" }, _device.Log());
            Assert.Equal(Stored, _device.Outputs[1].AsSpan(9, 8).ToArray());
            Assert.Null(KeyboardProfileBackups.Get(K510Key));
        }

        [Fact]
        public void AK510TheUserSetSince_IsLeftAsItIs()
        {
            KeyboardProfileBackups.LoadRegistry(new[] { (K510Key, 0, Convert.ToHexString(Stored)) });
            _device.InputAnswer = o => o[3] == 0x05 ? State(0x04, 0x02, 0x02, 0x00, 0x01, 0x00, 0x00, 0x00) : null;
            using var backend = Started(K510);
            Advance(HidLightingBackend.RepairGraceMs);
            Passes(backend);
            Assert.Equal(new[] { "O 04 00 00 05 38" }, _device.Log());
            Assert.Null(KeyboardProfileBackups.Get(K510Key));
        }

        [Fact]
        public void AK510BackupOfAnotherLength_IsDropped_AndNothingWritten()
        {
            KeyboardProfileBackups.LoadRegistry(new[] { (K510Key, 0, "0D0103") });
            _device.InputAnswer = o => o[3] == 0x05 ? State(0x06, 0x02, 0x00, 0x01, 0x00, 0x11, 0x22, 0x33) : null;
            using var backend = Started(K510);
            Advance(HidLightingBackend.RepairGraceMs);
            Passes(backend);
            Assert.Equal(new[] { "O 04 00 00 05 38" }, _device.Log());
            Assert.Null(KeyboardProfileBackups.Get(K510Key));

            // A claim then takes the stored mode as the keyboard holds it.
            Link(PathOf(K510));
            Light(0x445566);
            Passes(backend);
            Advance(LenovoLegionLighting.HoldMs);
            Passes(backend);
            Assert.Equal(8, KeyboardProfileBackups.Get(K510Key)?.Contents.Length);
        }

        [Fact]
        public void AFourZoneClaim_RecordsTheHandBackOwed_ThenSendsItsStaticState()
        {
            using var backend = Lit(FourZone);
            Assert.Equal(new[] { "F CC 16" }, _device.Log());
            Assert.Equal("CC 16 01 01 02 11 22 33 11 22 33 11 22 33 11 22 33 00", Hex(_device.Features[0], 18));
            Assert.Equal(Lenovo4ZoneLighting.OwedRecord, KeyboardProfileBackups.Get(FourZoneProduct)?.Contents);
            // The crash path sends HID reports only.
            Assert.Empty(backend.Recorded);
            Assert.Empty(GiveBacks());
        }

        private static HidCollectionInfo FourZone => new()
        {
            Path = @"\\?\hid#vid_048d&pid_c965#1",
            VendorId = 0x048D, ProductId = 0xC965, UsagePage = 0xFF89, Usage = 0xCC, FeatureReportLength = 33,
        };

        private const string FourZoneProduct = "lenovo:048D:C965";

        [Fact]
        public void AFourZoneHandBack_GivesTheKeyboardBackToTheFirmware_OverWmi()
        {
            using var backend = Lit(FourZone);
            Unlight();
            Passes(backend);
            // SetLightControlOwner(0), as Lenovo Legion Toolkit does when it
            // exits (App.xaml.cs:208-215), and no HID report.
            Assert.Equal(new[] { 0 }, GiveBacks());
            Assert.Single(_device.Log());
            Assert.Null(KeyboardProfileBackups.Get(FourZoneProduct));
        }

        [Fact]
        public void AFourZoneHandBackThatFails_KeepsItOwed()
        {
            using var backend = Lit(FourZone);
            _giveBackFails = true;
            Unlight();
            Passes(backend);
            Assert.Equal(new[] { 0 }, GiveBacks());
            Assert.NotNull(KeyboardProfileBackups.Get(FourZoneProduct));
            Assert.True(Assert.Single(backend.Snapshot.Units).Asleep);
        }

        [Fact]
        public void AFourZoneHandBackAKilledRunOwes_IsMadeByTheNextRun_WithNoClaim()
        {
            KeyboardProfileBackups.LoadRegistry(new[] { (FourZoneProduct, 0, "CC1600") });
            using var backend = Started(FourZone);
            Assert.Empty(GiveBacks());
            Advance(HidLightingBackend.RepairGraceMs);
            Passes(backend);
            Assert.Equal(new[] { 0 }, GiveBacks());
            Assert.Empty(_device.Log());
            Assert.Null(KeyboardProfileBackups.Get(FourZoneProduct));
        }

        [Fact]
        public void AFourZoneClaimOnAKeyboardAnEarlierRunLeft_HandsItBackOnce()
        {
            KeyboardProfileBackups.LoadRegistry(new[] { (FourZoneProduct, 0, "CC1600") });
            using var backend = Started(FourZone);
            Link(PathOf(FourZone));
            Light(0x112233);
            Passes(backend);
            Unlight();
            Passes(backend);
            Assert.Equal(new[] { 0 }, GiveBacks());
            Assert.Null(KeyboardProfileBackups.Get(FourZoneProduct));
        }

        [Fact]
        public void AnM300Claim_SendsItsStaticMode()
        {
            var collection = new HidCollectionInfo
            {
                Path = @"\\?\hid#vid_17ef&pid_60e4#1",
                VendorId = 0x17EF, ProductId = 0x60E4, UsagePage = 0xFF01, Usage = 0x01, OutputReportLength = 0x41,
            };
            using var backend = Lit(collection, holdMs: LenovoLegionLighting.HoldMs);
            Assert.Equal(new[] { "O 00 25 00 01 01" }, _device.Log());
            Assert.Equal(LenovoM300Lighting.StaticReport(0x112233), _device.Outputs[0]);
        }

        [Fact]
        public void AK500Claim_SendsThePreludeAndTheMode()
        {
            var collection = new HidCollectionInfo
            {
                Path = @"\\?\hid#vid_17ef&pid_60d5&mi_03#1",
                VendorId = 0x17EF, ProductId = 0x60D5, UsagePage = 0xFF00, Usage = 0x01, OutputReportLength = 65,
                InterfaceNumber = 3,
            };
            using var backend = Lit(collection, holdMs: LenovoLegionLighting.HoldMs);
            Assert.Equal(new[] { "F 00 14", "O 00 01 01 02 03" }, _device.Log());
        }
    }
}
