using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The light maps of the three vendor SDK paths (#557) against the
    /// references, each table written out here from a source other than the
    /// one the generator reads, so a slip in either shows.
    /// </summary>
    public class SdkLightMapsTests
    {
        /// <summary>A keyboard with the per-key feature gets the keys of the LED
        /// SDK's map, and one with only zones gets the zones.</summary>
        [Fact]
        public void AKeyboardsRow_ShowsWhatItsDeviceHas()
        {
            var perKey = LogiLedKeys.KeyboardOf(new HidppUnit { RgbFeatureId = 0x8081, PerKeyFeatureId = 0x8081 });
            var zones = LogiLedKeys.KeyboardOf(new HidppUnit { RgbFeatureId = 0x8070 });
            Assert.True(perKey.KeyCount > 100);
            Assert.Equal(0, zones.KeyCount);
            Assert.NotEmpty(zones.Lights);
            Assert.Same(LogiLedKeys.MapOf("keyboard"), LogiLedKeys.KeyboardOf(null));
        }

        /// <summary>Each 6 by 22 cell RGB.NET's Razer keyboard mapping names
        /// with a key (RGB.NET.Devices.Razer Generic/LedMappings.cs, its
        /// 8 by 24 grid one row and one column in), as cell:usage, the usage
        /// from the LedId's name. RZKEY_FN's cell is left out: RGB.NET calls
        /// it RightGui there, the Chroma header Fn.</summary>
        internal const string RgbNetRazerCells =
            "0001:29 0003:3A 0004:3B 0005:3C 0006:3D 0007:3E 0008:3F 0009:40 000A:41 000B:42 000C:43 000D:44 "
            + "000E:45 000F:46 0010:47 0011:48 0101:35 0102:1E 0103:1F 0104:20 0105:21 0106:22 0107:23 0108:24 "
            + "0109:25 010A:26 010B:27 010C:2D 010D:2E 010E:2A 010F:49 0110:4A 0111:4B 0112:53 0113:54 0114:55 "
            + "0115:56 0201:2B 0202:14 0203:1A 0204:08 0205:15 0206:17 0207:1C 0208:18 0209:0C 020A:12 020B:13 "
            + "020C:2F 020D:30 020E:31 020F:4C 0210:4D 0211:4E 0212:5F 0213:60 0214:61 0215:57 0301:39 0302:04 "
            + "0303:16 0304:07 0305:09 0306:0A 0307:0B 0308:0D 0309:0E 030A:0F 030B:33 030C:34 030D:32 030E:28 "
            + "0312:5C 0313:5D 0314:5E 0401:E1 0402:64 0403:1D 0404:1B 0405:06 0406:19 0407:05 0408:11 0409:10 "
            + "040A:36 040B:37 040C:38 040E:E5 0410:52 0412:59 0413:5A 0414:5B 0415:58 0501:E0 0502:E3 0503:E2 "
            + "0507:2C 050B:E6 050D:65 050E:E4 050F:50 0510:51 0511:4F 0513:62 0514:63";

        /// <summary>The header's keys RGB.NET does not name: the Japanese
        /// keys (RzChromaSDKTypes.h:307-311), each with its Keyboard page
        /// usage (HID Usage Tables 1.6, International1 to 5).</summary>
        internal const string ChromaJapaneseCells = "0015:89 040D:87 0504:8B 0509:8A 050A:88";

        internal static Dictionary<int, int> Pairs(string text)
            => text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split(':'))
                .ToDictionary(p => int.Parse(p[0], NumberStyles.HexNumber), p => int.Parse(p[1], NumberStyles.HexNumber));

        private static int Cell(int code) => (code >> 8) * 22 + (code & 0xFF);

        [Fact]
        public void TheChromaKeyboard_NamesEveryRzkeyCell_ByTheReferences()
        {
            var map = ChromaLayouts.MapOf("keyboard");
            Assert.Equal((6, 22), (ChromaLayouts.KeyboardRows, ChromaLayouts.KeyboardColumns));
            var byCell = map.Lights.ToDictionary(l => l.Address);

            var keys = Pairs(RgbNetRazerCells);
            foreach (var pair in Pairs(ChromaJapaneseCells)) keys.Add(pair.Key, pair.Value);
            foreach (var (code, usage) in keys)
            {
                var light = byCell[Cell(code)];
                Assert.Equal(LightKind.Key, light.Kind);
                Assert.Equal(usage, light.Usage);
                Assert.Equal(LightKeySource.Vendor, light.KeySource);
            }

            // The macro keys M1 to M5 in column 0 (RZKEY_MACRO1-5,
            // RzChromaSDKTypes.h:289-293), Fn at 0x050C (line 284), the logo at
            // RZLED_LOGO 0x0014 (line 325), and the two media keys RGB.NET and
            // Aurora both place at row 0, columns 18 and 19.
            for (int m = 1; m <= 5; m++)
            {
                var macro = byCell[Cell(m << 8)];
                // Printed M1 to M5 on the board, as OpenRGB names them
                // (RazerDevices.cpp:119-123), never G.
                Assert.Equal((LightKind.Other, LightLabel.MacroKey, m), (macro.Kind, macro.Label, macro.LabelNumber));
            }
            Assert.Equal((LightKind.Other, LightLabel.Fn), (byCell[Cell(0x050C)].Kind, byCell[Cell(0x050C)].Label));
            Assert.Equal(LightKind.Logo, byCell[Cell(0x0014)].Kind);
            Assert.Equal(LightLabel.Previous, byCell[Cell(0x0012)].Label);
            Assert.Equal(LightLabel.PlayPause, byCell[Cell(0x0013)].Label);

            // Nothing else: 105 + 5 keys, 5 macro keys, Fn, the logo and the
            // two media keys.
            Assert.Equal(keys.Count + 5 + 1 + 1 + 2, map.Lights.Count);
            Assert.Equal(map.Lights.OrderBy(l => l.Address).Select(l => l.Address), map.Lights.Select(l => l.Address));
            Assert.Equal(0, map.UnnamedKeyCount);
            Assert.Null(map.Product);
        }

        [Fact]
        public void TheChromaMouse_HasTheRzled2Cells()
        {
            // RZLED2, RzChromaSDKTypes.h:491-515: the wheel, logo and
            // backlight, then seven LEDs down each side and five along the
            // bottom.
            var map = ChromaLayouts.MapOf("mouse");
            Assert.Equal((9, 7), (ChromaLayouts.MouseRows, ChromaLayouts.MouseColumns));
            int MouseCell(int code) => (code >> 8) * 7 + (code & 0xFF);
            var byCell = map.Lights.ToDictionary(l => l.Address);
            Assert.Equal((LightKind.Zone, 1), (byCell[MouseCell(0x0203)].Kind, byCell[MouseCell(0x0203)].Zone));
            Assert.Equal((LightKind.Zone, 2), (byCell[MouseCell(0x0403)].Kind, byCell[MouseCell(0x0403)].Zone));
            Assert.Equal(LightKind.Logo, byCell[MouseCell(0x0703)].Kind);
            var edges = new List<int>();
            for (int i = 1; i <= 7; i++) { edges.Add((i << 8) | 0); edges.Add((i << 8) | 6); }
            for (int i = 1; i <= 5; i++) edges.Add(0x0800 | i);
            foreach (int code in edges) Assert.Equal(LightKind.Edge, byCell[MouseCell(code)].Kind);
            Assert.Equal(3 + edges.Count, map.Lights.Count);
            Assert.Equal(19, map.EdgeCount);
        }

        /// <summary>The one-row categories, numbered as their CHROMA_CUSTOM
        /// arrays: the headset's 5, the mousepad's 15, the keypad's 4 by 5
        /// and Chroma Link's 5 (RzChromaSDKTypes.h:622, 658, 718-721, 801;
        /// WyvrnChroma ChromaGrid.cs).</summary>
        [Theory]
        [InlineData("headset", 5)]
        [InlineData("mousepad", 15)]
        [InlineData("keypad", 20)]
        [InlineData("chromalink", 5)]
        public void TheOtherChromaCategories_AreNumberedZones(string category, int count)
        {
            var map = ChromaLayouts.MapOf(category);
            Assert.Equal(count, map.Lights.Count);
            for (int i = 0; i < count; i++)
                Assert.Equal((i, LightKind.Zone, i + 1), (map.Lights[i].Address, map.Lights[i].Kind, map.Lights[i].Zone));
            Assert.Equal(count, map.ZoneCount);
        }

        /// <summary>Each LogiLed::KeyName value (LogitechLEDLib.h:33-147) as
        /// value:usage, the usage from the value read as a set 1 scan code,
        /// 0x100 standing for the E0 prefix, through the scan code column of
        /// Microsoft's Keyboard Scan Code Specification (appendix C).</summary>
        private const string LogiLedScanCodes =
            "1:29 3B:3A 3C:3B 3D:3C 3E:3D 3F:3E 40:3F 41:40 42:41 43:42 44:43 57:44 58:45 137:46 46:47 145:48 29:35 "
            + "2:1E 3:1F 4:20 5:21 6:22 7:23 8:24 9:25 A:26 B:27 C:2D D:2E E:2A 152:49 147:4A 149:4B 45:53 135:54 "
            + "37:55 4A:56 F:2B 10:14 11:1A 12:08 13:15 14:17 15:1C 16:18 17:0C 18:12 19:13 1A:2F 1B:30 2B:31 153:4C "
            + "14F:4D 151:4E 47:5F 48:60 49:61 4E:57 3A:39 1E:04 1F:16 20:07 21:09 22:0A 23:0B 24:0D 25:0E 26:0F "
            + "27:33 28:34 1C:28 4B:5C 4C:5D 4D:5E 2A:E1 2C:1D 2D:1B 2E:06 2F:19 30:05 31:11 32:10 33:36 34:37 35:38 "
            + "36:E5 148:52 4F:59 50:5A 51:5B 11C:58 1D:E0 15B:E3 38:E2 39:2C 138:E6 15C:E7 15D:65 11D:E4 14B:50 "
            + "150:51 14D:4F 52:62 53:63";

        [Fact]
        public void TheLedSdkKeyboard_HasEveryKeyName_AndTheFiveZones()
        {
            var map = LogiLedKeys.MapOf("keyboard");
            var byAddress = map.Lights.ToDictionary(l => l.Address);
            var keys = Pairs(LogiLedScanCodes);
            foreach (var (code, usage) in keys)
                Assert.Equal((LightKind.Key, usage), (byAddress[code].Kind, (int)byAddress[code].Usage));
            // G_1 to G_9 (0xFFF1-0xFFF9), G_LOGO and G_BADGE (0xFFFF1, 0xFFFF2).
            for (int g = 1; g <= 9; g++)
                Assert.Equal((LightKind.Other, LightLabel.GKey, g),
                    (byAddress[0xFFF0 + g].Kind, byAddress[0xFFF0 + g].Label, byAddress[0xFFF0 + g].LabelNumber));
            Assert.Equal(LightKind.Logo, byAddress[0xFFFF1].Kind);
            Assert.Equal(LightKind.Logo, byAddress[0xFFFF2].Kind);
            // Zones 0 to 4 of LogiLed::Keyboard, the G213's five.
            for (int zone = 0; zone < 5; zone++)
                Assert.Equal((LightKind.Zone, zone + 1), (byAddress[LogiLedKeys.ZoneBase + zone].Kind, byAddress[LogiLedKeys.ZoneBase + zone].Zone));
            Assert.Equal(keys.Count + 9 + 2 + 5, map.Lights.Count);
            Assert.All(map.Lights.Where(l => l.Kind != LightKind.Zone), l => Assert.True(LogiLedKeys.IsKey(l)));
            Assert.All(map.Lights.Where(l => l.Kind == LightKind.Zone), l => Assert.False(LogiLedKeys.IsKey(l)));
        }

        /// <summary>Every other LED SDK type with zones gets them numbered
        /// from 1 (LedSdkBackend.TypeOf), and the whole devices and the
        /// one-zone mousemat get no map.</summary>
        [Fact]
        public void TheOtherLedSdkTypes_AreTheirZones()
        {
            foreach (var (type, zones) in new[] { ("mouse", 3), ("headset", 4), ("speaker", 4) })
            {
                var map = LogiLedKeys.MapOf(type);
                Assert.Equal(Enumerable.Range(0, zones).Select(z => LogiLedKeys.ZoneBase + z), map.Lights.Select(l => l.Address));
                Assert.Equal(Enumerable.Range(1, zones), map.Lights.Select(l => l.Zone));
            }
            Assert.Null(LogiLedKeys.MapOf("mousemat"));
            Assert.Null(LogiLedKeys.MapOf(PeripheralLinker.LedSdkWholeDevices));
        }

        /// <summary>The GameSense keyboard: a key per Apex key code but play
        /// and pause, by its code (OpenRGB SteelSeriesApexController.cpp
        /// keys[], the codes RGB.NET's custom zones send GameSense too), the
        /// rgb-per-key-zones logo and the five rgb-5-zone zones. The mouse:
        /// wheel, logo and base (standard-zones.md:82-86). The headset's one
        /// zone gets no map.</summary>
        [Fact]
        public void TheGameSenseMaps_AreTheApexKeys_TheLogoAndTheZones()
        {
            var keyboard = GameSenseLights.MapOf("keyboard");
            var handlers = GameSenseLights.HandlersOf("keyboard");
            Assert.Equal(keyboard.Lights.Count, handlers.Count);
            var codes = SteelSeriesApexLayouts.Keys.Where(c => c != 0xFB).ToList();
            Assert.Equal(111, codes.Count);
            for (int i = 0; i < keyboard.Lights.Count; i++)
            {
                var light = keyboard.Lights[i];
                var h = handlers[i];
                if (light.Address < GameSenseLights.LogoAddress)
                {
                    Assert.Contains((byte)light.Address, codes);
                    Assert.Equal(("rgb-per-key-zones", null, light.Address), (h.DeviceType, h.Zone, h.CustomKey));
                    if (light.Address == 0xF0) Assert.Equal(LightLabel.Fn, light.Label);
                    else Assert.Equal((LightKind.Key, light.Address), (light.Kind, (int)light.Usage));
                }
            }
            Assert.Equal(codes.Count + 1 + 5, keyboard.Lights.Count);
            int logo = keyboard.Lights.ToList().FindIndex(l => l.Kind == LightKind.Logo);
            Assert.Equal(("rgb-per-key-zones", "logo"), (handlers[logo].DeviceType, handlers[logo].Zone));
            var zones = Enumerable.Range(0, keyboard.Lights.Count).Where(i => keyboard.Lights[i].Kind == LightKind.Zone).ToList();
            Assert.Equal(new[] { "one", "two", "three", "four", "five" }, zones.Select(i => handlers[i].Zone));
            Assert.All(zones, i => Assert.Equal("rgb-5-zone", handlers[i].DeviceType));
            Assert.Equal(handlers.Count, handlers.Select(h => h.FrameKey).Distinct().Count());

            var mouse = GameSenseLights.HandlersOf("mouse");
            Assert.Equal(new[] { ("mouse", "wheel"), ("mouse", "logo"), ("mouse", "base") }, mouse.Select(h => (h.DeviceType, h.Zone)));
            Assert.Equal(new[] { LightKind.Zone, LightKind.Logo, LightKind.Zone }, GameSenseLights.MapOf("mouse").Lights.Select(l => l.Kind));
            Assert.Null(GameSenseLights.MapOf("headset"));
        }
    }

    /// <summary>
    /// Each Razer accessory reaches the Chroma SDK under what it is (#557):
    /// a mat as a mousepad, a headset as a headset, and anything else as a
    /// Chroma Link device, the SDK's category for every other lit thing
    /// (RzChromaSDKDefines.h at CSDK_ChromaModSample fe641c4). The names are
    /// OpenRGB's (RazerDevices.h:236-285) and openrazer's.
    /// </summary>
    public class RazerAccessoryCategoryTests
    {
        private static string Category(ushort pid, int capType = PadForge.Engine.InputDeviceType.Mouse)
            => PeripheralLinker.RazerCategory(new LinkRow(Guid.NewGuid(), capType, PeripheralLinker.RazerVid, pid, Guid.NewGuid(), null));

        [Theory]
        [InlineData(0x0068, "mousepad")] // Firefly HyperFlux
        [InlineData(0x0C00, "mousepad")] // Firefly
        [InlineData(0x0C01, "mousepad")] // Goliathus Chroma
        [InlineData(0x0C02, "mousepad")] // Goliathus Chroma Extended
        [InlineData(0x0C04, "mousepad")] // Firefly V2
        [InlineData(0x0C05, "mousepad")] // Strider Chroma
        [InlineData(0x0C06, "mousepad")] // Goliathus Chroma 3XL
        [InlineData(0x0C08, "mousepad")] // Firefly V2 Pro
        [InlineData(0x0F03, "headset")] // Tiamat 7.1 V2
        [InlineData(0x0F19, "headset")] // Kraken Kitty Edition
        [InlineData(0x007E, "chromalink")] // Mouse Dock Chroma
        [InlineData(0x0215, "chromalink")] // Core
        [InlineData(0x0517, "chromalink")] // Nommo Chroma
        [InlineData(0x0518, "chromalink")] // Nommo Pro
        [InlineData(0x0532, "chromalink")] // Leviathan V2
        [InlineData(0x054A, "chromalink")] // Leviathan V2 X
        [InlineData(0x0F07, "chromalink")] // Chroma Mug
        [InlineData(0x0F08, "chromalink")] // Base Station Chroma
        [InlineData(0x0F09, "chromalink")] // Chroma HDK
        [InlineData(0x0F0D, "chromalink")] // Laptop Stand Chroma
        [InlineData(0x0F0E, "chromalink")] // Chroma PC Case Lighting Kit
        [InlineData(0x0F13, "chromalink")] // O11 Dynamic
        [InlineData(0x0F1A, "chromalink")] // Core X
        [InlineData(0x0F1B, "chromalink")] // Seiren Emote
        [InlineData(0x0F1D, "chromalink")] // Mouse Bungee V3 Chroma
        [InlineData(0x0F20, "chromalink")] // Base Station V2 Chroma
        [InlineData(0x0F21, "chromalink")] // Thunderbolt 4 Dock Chroma
        [InlineData(0x0F2B, "chromalink")] // Laptop Stand Chroma V2
        [InlineData(0x0F52, "chromalink")] // Thunderbolt 5 Dock Chroma
        public void AnAccessory_ReachesTheCategoryOfWhatItIs(int pid, string category)
        {
            Assert.Equal(category, Category((ushort)pid));
            // Whatever kind of collection the row is.
            Assert.Equal(category, Category((ushort)pid, PadForge.Engine.InputDeviceType.Keyboard));
        }

        /// <summary>The theory names every accessory, and the two narrower
        /// lists stay inside the list of accessories.</summary>
        [Fact]
        public void EveryAccessoryIsNamed_AndTheListsNest()
        {
            var named = typeof(RazerAccessoryCategoryTests).GetMethod(nameof(AnAccessory_ReachesTheCategoryOfWhatItIs))
                .GetCustomAttributes(typeof(InlineDataAttribute), false).Cast<InlineDataAttribute>()
                .Select(a => (ushort)(int)a.GetData(null).First()[0]).OrderBy(p => p);
            Assert.Equal(PeripheralProductIds.RazerAccessories.OrderBy(p => p), named);
            Assert.All(PeripheralProductIds.RazerMousepads, p => Assert.Contains(p, PeripheralProductIds.RazerAccessories));
            Assert.All(PeripheralProductIds.RazerHeadsets, p => Assert.Contains(p, PeripheralProductIds.RazerAccessories));
        }

        /// <summary>A mouse, a keyboard and a keypad keep their categories.</summary>
        [Fact]
        public void AMouseAKeyboardAndAKeypad_KeepTheirs()
        {
            Assert.Equal("mouse", Category(PeripheralProductIds.RazerMice[0], PadForge.Engine.InputDeviceType.Keyboard));
            Assert.Equal("keyboard", Category(PeripheralProductIds.RazerKeyboards[0]));
            Assert.Equal("keypad", Category(PeripheralLinker.RazerKeypadPids[0]));
        }
    }

    /// <summary>
    /// A Logitech keyboard lit through the LED SDK's keyboard path shows the
    /// lights its own board has (#557): the keys on a per-key board, the
    /// five zones on a zone board such as the G213, never both. The path
    /// serves every Logitech keyboard at once, so its published map holds
    /// both kinds, and the row's HID++ unit tells which is the row's.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class LedSdkKeyboardRowMapTests : IDisposable
    {
        private static readonly Guid Row = new("00000000-0000-0000-0000-0000000000c7");
        private static readonly OutputPath Keyboard = new(OutputFamily.LedSdkType, "keyboard");

        public LedSdkKeyboardRowMapTests()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
            PeripheralLightMaps.Publish(Keyboard, LogiLedKeys.MapOf("keyboard"));
        }

        public void Dispose()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
        }

        private static DeviceLinks Links(params HidppUnit[] units)
        {
            PeripheralOutputs.Hidpp = new HidppSnapshot(units, Array.Empty<Guid>());
            return new DeviceLinks { Device = Row, Lighting = new[] { Keyboard }, HidppUnits = units.Select(u => u.Key).ToArray() };
        }

        [Fact]
        public void APerKeyBoard_ShowsItsKeys_AndAZoneBoard_ItsFiveZones()
        {
            var whole = LogiLedKeys.MapOf("keyboard");
            // A G915 X: 0x8071 for its zones and 0x8081 for its keys.
            var perKey = new HidppUnit
            {
                ChannelPath = "rx", DeviceIndex = 1, DeviceType = 0, RgbFeatureId = 0x8071, RgbIndex = 9,
                PerKeyFeatureId = 0x8081, PerKeyIndex = 10,
            };
            var map = PeripheralLightMaps.ForRow(Links(perKey), out var found);
            Assert.Equal(Keyboard, found);
            Assert.All(map.Lights, l => Assert.True(LogiLedKeys.IsKey(l)));
            Assert.Equal(whole.Lights.Count(l => LogiLedKeys.IsKey(l)), map.Lights.Count);
            Assert.True(map.HasKeys);
            Assert.Equal(0, map.ZoneCount);

            // A G610 answers 0x8080.
            var perKeyV1 = perKey with { PerKeyFeatureId = 0x8080 };
            Assert.Same(map, PeripheralLightMaps.ForRow(Links(perKeyV1), out _));

            // A G213: 0x8070 and no per-key feature.
            var zones = new HidppUnit { ChannelPath = "usb", DeviceIndex = 0xFF, DeviceType = 0, RgbFeatureId = 0x8070, RgbIndex = 7 };
            map = PeripheralLightMaps.ForRow(Links(zones), out found);
            Assert.Equal(Keyboard, found);
            Assert.Equal(Enumerable.Range(1, 5), map.Lights.Select(l => l.Zone));
            Assert.All(map.Lights, l => Assert.Equal(LightKind.Zone, l.Kind));
            Assert.False(map.HasKeys);
        }

        /// <summary>With no lit HID++ unit known for the row, the card shows
        /// the path's whole map, as the path paints it. A row on another
        /// path, or the mouse type, is never narrowed.</summary>
        [Fact]
        public void WithNoUnitKnown_TheWholeMapShows_AndOtherPathsAreUntouched()
        {
            var whole = LogiLedKeys.MapOf("keyboard");
            Assert.Same(whole, PeripheralLightMaps.ForRow(Links(), out _));
            var dark = new HidppUnit { ChannelPath = "usb", DeviceIndex = 0xFF, DeviceType = 0, PerKeyFeatureId = 0x8081 };
            Assert.Same(whole, PeripheralLightMaps.ForRow(Links(dark), out _));
            Assert.Null(PeripheralLightMaps.ForRow(null, out _));

            var mouse = new OutputPath(OutputFamily.LedSdkType, "mouse");
            var mouseMap = LogiLedKeys.MapOf("mouse");
            PeripheralLightMaps.Publish(mouse, mouseMap);
            var zones = new HidppUnit { ChannelPath = "usb", DeviceIndex = 0xFF, DeviceType = 3, RgbFeatureId = 0x8070, RgbIndex = 7 };
            PeripheralOutputs.Hidpp = new HidppSnapshot(new[] { zones }, Array.Empty<Guid>());
            var links = new DeviceLinks { Device = Row, Lighting = new[] { mouse }, HidppUnits = new[] { zones.Key } };
            Assert.Same(mouseMap, PeripheralLightMaps.ForRow(links, out var found));
            Assert.Equal(mouse, found);
        }

        /// <summary>A keyboard row on a receiver with a mouse beside it takes
        /// the keyboard unit's kind, whichever unit is listed first.</summary>
        [Fact]
        public void TheKeyboardUnit_DecidesOverAMouseOnTheSameRow()
        {
            var mouse = new HidppUnit { ChannelPath = "rx", DeviceIndex = 2, DeviceType = 3, RgbFeatureId = 0x8070, RgbIndex = 7 };
            var board = new HidppUnit
            {
                ChannelPath = "rx", DeviceIndex = 1, DeviceType = 0, RgbFeatureId = 0x8071, RgbIndex = 9,
                PerKeyFeatureId = 0x8081, PerKeyIndex = 10,
            };
            var map = PeripheralLightMaps.ForRow(Links(mouse, board), out _);
            Assert.True(map.HasKeys);
            Assert.Equal(0, map.ZoneCount);
        }
    }
}
