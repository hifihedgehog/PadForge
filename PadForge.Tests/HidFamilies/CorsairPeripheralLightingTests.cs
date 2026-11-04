using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A scripted Corsair device: every output report, input read,
    /// feature report and wait is logged in order, and an input read is
    /// answered once per output report by <see cref="Answer"/>.</summary>
    internal sealed class FakeCorsairChannel : IHidReportChannel
    {
        public string Path { get; init; } = @"\\?\hid#vid_1b1c&pid_1b2d&mi_01#fake";
        public bool FeatureOnly => false;
        public int InputReportLength { get; init; } = 65;
        public int OutputReportLength { get; init; } = 65;
        public int FeatureReportLength { get; init; } = 65;

        public readonly List<string> Log = new();
        public readonly List<byte[]> Outputs = new();
        public readonly List<byte[]> Features = new();
        public Func<byte[], byte[]> Answer = _ => null;
        public Func<byte[]> FeatureAnswer = () => null;
        public bool FailWrites;
        private byte[] _unanswered;

        public Action<int> Wait => ms => Log.Add("wait " + ms);

        public bool SetFeature(byte[] report)
        {
            Log.Add("set " + CorsairPeripheralLightingTests.Hex(report, 3));
            Features.Add((byte[])report.Clone());
            return !FailWrites;
        }

        public bool GetFeature(byte reportId, byte[] buffer)
        {
            Log.Add("get");
            var answer = FeatureAnswer();
            if (answer == null) return false;
            Array.Clear(buffer);
            Array.Copy(answer, buffer, Math.Min(answer.Length, buffer.Length));
            return true;
        }

        public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs)
        {
            if (FailWrites) return false;
            Log.Add("out " + CorsairPeripheralLightingTests.Hex(report, 6));
            Outputs.Add((byte[])report.Clone());
            _unanswered = report;
            return true;
        }

        public int ReadInput(byte[] buffer, int timeoutMs)
        {
            Log.Add("in " + timeoutMs);
            var asked = _unanswered;
            _unanswered = null;
            var answer = asked == null ? null : Answer(asked);
            if (answer == null) return 0;
            Array.Clear(buffer);
            Array.Copy(answer, buffer, Math.Min(answer.Length, buffer.Length));
            return Math.Min(answer.Length, buffer.Length);
        }

        public void Dispose() { }
    }

    /// <summary>
    /// OpenRGB's CorsairPeripheralController family (#557): the older
    /// protocol's keyboards, mice, mousepad and stand, the K65 Mini and the
    /// K55 RGB PRO XT. Every expected byte is written out from OpenRGB or
    /// ckb-next, never from the builder under test.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class CorsairPeripheralLightingTests : IDisposable
    {
        internal static string Hex(byte[] bytes, int count) => BitConverter.ToString(bytes, 0, Math.Min(count, bytes.Length)).Replace('-', ' ');

        private static string Hex(byte[] bytes, int from, int count) => BitConverter.ToString(bytes, from, count).Replace('-', ' ');

        public CorsairPeripheralLightingTests() => KeyboardProfileBackups.ResetForTest(persistAtOnce: true);

        public void Dispose() => KeyboardProfileBackups.ResetForTest();

        private static CorsairPeripheralLighting Family() => new();

        internal static HidCollectionInfo Collection(ushort pid, ushort page = 0xFFC2, int iface = 1, ushort usage = 0x0004)
            => new()
            {
                Path = $@"\\?\hid#vid_1b1c&pid_{pid:x4}&mi_{iface:x2}#{page:x4}",
                VendorId = 0x1B1C, ProductId = pid, UsagePage = page, Usage = usage, InterfaceNumber = iface,
                InputReportLength = 65, OutputReportLength = 65, FeatureReportLength = 65,
            };

        /// <summary>An identification answer as Windows reads it: report ID
        /// 0, then data byte 0x14 the type and 0x17 the layout
        /// (CorsairPeripheralController.cpp:596-678).</summary>
        private static byte[] Identity(byte type, byte layout = 0)
        {
            var answer = new byte[65];
            answer[1] = 0x0E;
            answer[2] = 0x01;
            answer[1 + 0x14] = type;
            answer[1 + 0x17] = layout;
            return answer;
        }

        // ─────────────────────────────────────────────
        //  The generated tables
        // ─────────────────────────────────────────────

        [Fact]
        public void TheTables_AreOpenRgbsAtThePinnedCommit()
        {
            Assert.Equal("c6fc8016d215cca17d660c47746a08d7ea6a9175", CorsairLights.OpenRgbCommit);
            // CorsairPeripheralController.cpp:19-26: 113 numbers, Escape 0x00
            // first, then 0x10 and 114 for the two ISO keys.
            Assert.Equal(113, CorsairLights.Keys.Length);
            Assert.Equal(new[] { 0x10, 114 }, CorsairLights.Keys.Skip(111));
            // keys_k70_mk2 (lines 28-35) ends 16, 114, 47, 59, 125.
            Assert.Equal(new[] { 16, 114, 47, 59, 125 }, CorsairLights.KeysK70Mk2.Skip(111));
            // keys_k95_plat (lines 38-48): 111 keys, 9 more, 19 bar lights.
            Assert.Equal(139, CorsairLights.KeysK95Plat.Length);
            Assert.Equal(157, CorsairLights.KeysK95Plat[^1]);
            Assert.Equal(135, CorsairLights.KeysK95.Length);
            Assert.Equal(new[] { 0x00, 0x01, 0x02, 0x03, 0x05, 0x06, 0x07, 0x08, 0x04 }, CorsairLights.St100);
            Assert.Equal(new[] { 0x31, 0x3f, 0x41, 0x42, 0x51, 0x53, 0x55, 0x6f, 0x7e, 0x7f, 0x80, 0x81 },
                CorsairLights.KeyMappingK95PlatAnsi);
            // RGBController_CorsairPeripheral.cpp:179-294 and 546-687.
            Assert.Equal(113, CorsairLights.LedNames.Length);
            Assert.Equal("Key: Escape", CorsairLights.LedNames[0]);
            Assert.Equal("Key: / (ISO)", CorsairLights.LedNames[111]);
            Assert.Equal(139, CorsairLights.LedNamesK95Plat.Length);
            Assert.Equal("Light Bar 19", CorsairLights.LedNamesK95Plat[^1]);
            Assert.Equal(new[] { "", "", "Logo", "DPI", "Scroll Wheel" }, CorsairLights.CorsairM65EliteLeds.Take(5));
            Assert.Equal((6, 23), (CorsairLights.MatrixMap.Rows, CorsairLights.MatrixMap.Cols));
            Assert.Equal(-1, CorsairLights.MatrixMap.Cells[1]);
            // RGBController_CorsairK65Mini.cpp:20-124 and the K55 RGB PRO XT's
            // tables (CorsairK55RGBPROXTController.cpp:19-25,
            // RGBController_CorsairK55RGBPROXT.cpp:20-188).
            Assert.Equal(64, CorsairLights.K65MiniKeys.Length);
            Assert.Equal((41, "Key: Escape"), CorsairLights.K65MiniKeys[0]);
            Assert.Equal(111, CorsairLights.K55ProXtKeys.Length);
            Assert.Equal(111, CorsairLights.K55ProXtKeyNames.Length);
            Assert.Equal(127, CorsairLights.K55ProXtKeys[0]);
            Assert.Equal("Key: G1", CorsairLights.K55ProXtKeyNames[0]);
        }

        [Fact]
        public void TheDeviceTypes_AreTheDetectorsSections()
        {
            // CorsairPeripheralControllerDetect.cpp:179-229.
            Assert.Equal(CorsairDeviceType.Keyboard, CorsairPeripheralLighting.TypeOf(0x1B2D));
            Assert.Equal(CorsairDeviceType.Mouse, CorsairPeripheralLighting.TypeOf(0x1B5D));
            Assert.Equal(CorsairDeviceType.Mousemat, CorsairPeripheralLighting.TypeOf(0x1B3B));
            Assert.Equal(CorsairDeviceType.HeadsetStand, CorsairPeripheralLighting.TypeOf(0x0A34));
            Assert.All(CorsairLights.V1Sections.Where(s => s.Detector == CorsairPeripheralLighting.DetectPeripheral),
                s => Assert.Contains(s.Section, new[] { "Keyboards", "Mice", "Mousemats", "Headset Stands" }));
            Assert.Equal(21, CorsairLights.V1Sections.Count(s => s.Section == "Keyboards"));
            Assert.Equal(14, CorsairLights.V1Sections.Count(s => s.Section == "Mice"));
        }

        [Fact]
        public void TheKeyboardTables_AreOpenRgbsByProductId()
        {
            // CorsairPeripheralController.cpp:621-653.
            Assert.Equal(CorsairPeripheralLighting.Logical.K95Platinum, CorsairPeripheralLighting.LogicalOf(0x1B2D));
            Assert.Equal(CorsairPeripheralLighting.Logical.K95, CorsairPeripheralLighting.LogicalOf(0x1B11));
            Assert.Equal(CorsairPeripheralLighting.Logical.K55, CorsairPeripheralLighting.LogicalOf(0x1B3D));
            Assert.Equal(CorsairPeripheralLighting.Logical.K70LuxRgb, CorsairPeripheralLighting.LogicalOf(0x1B33));
            foreach (ushort pid in new ushort[] { 0x1B38, 0x1B49, 0x1B6B, 0x1B55, 0x1B48 })
                Assert.Equal(CorsairPeripheralLighting.Logical.K70Mk2, CorsairPeripheralLighting.LogicalOf(pid));
            Assert.Equal(CorsairPeripheralLighting.Logical.K68, CorsairPeripheralLighting.LogicalOf(0x1B4F));
            // The K95 RGB PLATINUM SE is no case of the switch.
            Assert.Equal(CorsairPeripheralLighting.Logical.Normal, CorsairPeripheralLighting.LogicalOf(0x1B82));
            // The tables that take the key-input setup (lines 80-90).
            Assert.Equal(new[] { CorsairPeripheralLighting.Logical.K95Platinum, CorsairPeripheralLighting.Logical.K55,
                CorsairPeripheralLighting.Logical.K70Mk2, CorsairPeripheralLighting.Logical.K68 },
                Enum.GetValues<CorsairPeripheralLighting.Logical>().Where(CorsairPeripheralLighting.TakesKeyInput));
        }

        // ─────────────────────────────────────────────
        //  Matching
        // ─────────────────────────────────────────────

        [Fact]
        public void EveryRegistration_HasItsModel_OnItsOwnCollection()
        {
            var family = Family();
            var registrations = OpenRgbRegistrations.Of(CorsairPeripheralLighting.OpenRgbFamily).ToList();
            Assert.Equal(39, registrations.Count);
            foreach (var r in registrations)
            {
                ushort page = r.UsagePage == OpenRgbRegistration.Any ? (ushort)0xFFC2 : (ushort)r.UsagePage;
                var model = family.Match(Collection((ushort)r.ProductId, page));
                Assert.NotNull(model);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(family, model.Family);
                Assert.Same(model, family.Match(Collection((ushort)r.ProductId, page)));
                Assert.Equal(new[] { VendorPrograms.CorsairIcue }, model.Programs);
                Assert.Equal(CorsairLights.Product((ushort)r.ProductId), model.Map.Product);
                // The keyboard's other interfaces and pages are not its own
                // (REGISTER_HID_DETECTOR_IP, interface 1 and the page).
                if (r.Interface != OpenRgbRegistration.Any)
                    Assert.Null(family.Match(Collection((ushort)r.ProductId, page, iface: 0)));
                if (r.UsagePage != OpenRgbRegistration.Any)
                    Assert.Null(family.Match(Collection((ushort)r.ProductId, 0x0001)));
            }
            // The V2 family's devices are not this family's.
            Assert.Null(family.Match(Collection(0x1BC4, 0xFF42)));
            Assert.Null(family.Match(Collection(0x1B2D) with { VendorId = 0x1B1D }));
        }

        [Fact]
        public void TheSharedTable_HoldsTheFamily()
        {
            Assert.Contains(CorsairPeripheralLighting.Instance, HidLightingFamilies.All);
            Assert.Contains(VendorPrograms.CorsairIcue, VendorPrograms.All);
            // iCUE.exe, the process Aurora looks for (CorsairRgbNetDevice.cs:46).
            Assert.True(VendorPrograms.CorsairIcue.Owns("iCUE"));
        }

        // ─────────────────────────────────────────────
        //  The lights
        // ─────────────────────────────────────────────

        [Fact]
        public void AFullKeyboard_LightsEachKeyAtItsCorsairNumber()
        {
            var lights = CorsairPeripheralLighting.Lights(CorsairDeviceType.Keyboard, CorsairPeripheralLighting.Logical.Normal, "Corsair K70 RGB");
            Assert.Equal(113, lights.Count);
            // Escape is number 0 at row 0, column 0; F1 number 0x0C (keys[10],
            // led_names[10]); the brightness key 137 a labeled light.
            Assert.Equal(new LightInfo(0x00, LightKind.Key, 0x29, LightKeySource.Generated)
            { X = 0, Y = 0, Width = 1, Height = 1, HasPosition = true }, lights[0]);
            Assert.Equal((0x0C, (ushort)0x3A), (lights[10].Address, lights[10].Usage));
            Assert.Equal(LightLabel.Brightness, lights.Single(l => l.Address == 137).Label);
            // ckb-next's bslash_iso (0x10) and hash (0x72): usages 0x64 and 0x32.
            Assert.Equal((ushort)0x64, lights.Single(l => l.Address == 0x10).Usage);
            Assert.Equal((ushort)0x32, lights.Single(l => l.Address == 0x72).Usage);
            // The Windows lock key has no usage.
            Assert.Equal(LightKind.Other, lights.Single(l => l.Address == 0x08).Kind);
        }

        [Fact]
        public void TheK95Platinum_HasItsGKeysAndLightBar()
        {
            var lights = CorsairPeripheralLighting.Lights(CorsairDeviceType.Keyboard, CorsairPeripheralLighting.Logical.K95Platinum, "x");
            Assert.Equal(139, lights.Count);
            // Macro G1 to G6 at numbers 0x0A, 0x16, 0x22, 0x2E, 0x3A, 0x46
            // (keys_k95_plat after 0x10 and 114).
            var g = lights.Where(l => l.Label == LightLabel.GKey).ToList();
            Assert.Equal(new[] { 0x0A, 0x16, 0x22, 0x2E, 0x3A, 0x46 }, g.Select(l => l.Address));
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, g.Select(l => l.LabelNumber));
            var bar = lights.Where(l => l.Kind == LightKind.Edge).ToList();
            Assert.Equal(19, bar.Count);
            Assert.Equal(new[] { 144, 145, 146, 158, 160 }, bar.Take(5).Select(l => l.Address));
            Assert.All(bar, l => Assert.False(l.HasPosition));
        }

        [Fact]
        public void TheK70Mk2_HasItsLogoLights()
        {
            var lights = CorsairPeripheralLighting.Lights(CorsairDeviceType.Keyboard, CorsairPeripheralLighting.Logical.K70Mk2, "x");
            Assert.Equal(116, lights.Count);
            // "Key: Logo Left" 47 and "Key: Logo Right" 59 (lines 541-542).
            Assert.Equal(new[] { 47, 59 }, lights.Where(l => l.Kind == LightKind.Logo).Select(l => l.Address));
            // The K70 LUX RGB keeps its own names on the same numbers.
            var lux = CorsairPeripheralLighting.Lights(CorsairDeviceType.Keyboard, CorsairPeripheralLighting.Logical.K70LuxRgb, "x");
            Assert.Equal(lights.Select(l => (l.Address, l.Kind, l.Usage)), lux.Select(l => (l.Address, l.Kind, l.Usage)));
        }

        [Fact]
        public void TheK55_HasThreeZones()
        {
            var lights = CorsairPeripheralLighting.Lights(CorsairDeviceType.Keyboard, CorsairPeripheralLighting.Logical.K55, "x");
            Assert.Equal(new[] { (0, 1), (1, 2), (2, 3) }, lights.Select(l => (l.Address, l.Zone)));
            Assert.All(lights, l => Assert.Equal(LightKind.Zone, l.Kind));
        }

        [Fact]
        public void AMouse_HasFifteenZones_NamedAsOpenRgbsCodeEndsUp()
        {
            // The Ironclaw takes the M65 Elite's names (lines 1292-1295).
            var ironclaw = CorsairPeripheralLighting.Lights(CorsairDeviceType.Mouse, 0, "Corsair Ironclaw RGB");
            Assert.Equal(15, ironclaw.Count);
            Assert.Equal(LightKind.Logo, ironclaw[2].Kind);
            Assert.Equal(LightKind.Status, ironclaw[3].Kind);
            Assert.Equal(LightKind.Zone, ironclaw[4].Kind);
            // The Sabre RGB's underglow (lines 873-890).
            Assert.Equal(LightKind.Edge, CorsairPeripheralLighting.Lights(CorsairDeviceType.Mouse, 0, "Corsair Sabre RGB")[1].Kind);
            // The M65 RGB Elite's own names are replaced by the second test.
            var elite = CorsairPeripheralLighting.Lights(CorsairDeviceType.Mouse, 0, "Corsair M65 RGB Elite");
            Assert.All(elite, l => Assert.Equal(LightKind.Zone, l.Kind));
            Assert.Equal(Enumerable.Range(1, 15), elite.Select(l => l.Zone));
            Assert.Equal(Enumerable.Range(0, 15), elite.Select(l => l.Address));
        }

        [Fact]
        public void TheStand_PutsItsLogoFifth()
        {
            var lights = CorsairPeripheralLighting.Lights(CorsairDeviceType.HeadsetStand, 0, "Corsair ST100 RGB");
            Assert.Equal(new[] { 0, 1, 2, 3, 5, 6, 7, 8, 4 }, lights.Select(l => l.Address));
            Assert.Equal(LightKind.Logo, lights[8].Kind);
            Assert.All(lights.Take(8), l => Assert.Equal(LightKind.Edge, l.Kind));
            Assert.All(CorsairPeripheralLighting.Lights(CorsairDeviceType.Mousemat, 0, "x"), l => Assert.Equal(LightKind.Edge, l.Kind));
        }

        [Fact]
        public void TheK65Mini_LightsColumnByColumn_AndItsSpaceBarThrice()
        {
            var lights = CorsairPeripheralLighting.K65MiniLights();
            Assert.Equal(64, lights.Count);
            // Column 0: Escape (41), Tab (43), Caps (57), LShift (106), LCtrl (105).
            Assert.Equal(new[] { 41, 43, 57, 106, 105 }, lights.Take(5).Select(l => l.Address));
            Assert.Equal((1f, 0f), (lights[5].X, lights[5].Y));
            // The space bar's lights: 0 and 1 unnamed, 44 Space (lines 61, 74, 87).
            Assert.Equal(new[] { 0, 44, 1 }, lights.Where(l => l.Usage == 0x2C).Select(l => l.Address));
        }

        [Fact]
        public void TheK55ProXt_LightsEachKeyAtItsBankPlace()
        {
            var lights = CorsairPeripheralLighting.K55ProXtLights();
            Assert.Equal(111, lights.Count);
            Assert.Equal(new[] { 127, 128, 129, 130, 131, 132 }, lights.Take(6).Select(l => l.Address));
            Assert.All(lights.Take(6), l => Assert.Equal(LightLabel.GKey, l.Label));
            Assert.Equal((37, (ushort)0x29), (lights[6].Address, lights[6].Usage));
            // Index 6 sits at row 0, column 1 of the grid (line 21).
            Assert.Equal((1f, 0f), (lights[6].X, lights[6].Y));
        }

        // ─────────────────────────────────────────────
        //  The frames
        // ─────────────────────────────────────────────

        [Fact]
        public void TheFixedReports_AreOpenRgbs()
        {
            Assert.Equal("00 0E 01 00", Hex(CorsairPeripheralLighting.Query(), 4));
            Assert.Equal("00 07 04 02 00", Hex(CorsairPeripheralLighting.SpecialFunction(0x02), 5));
            Assert.Equal("00 07 04 01 00", Hex(CorsairPeripheralLighting.SpecialFunction(0x01), 5));
            Assert.Equal("00 07 05 02 00 03 00", Hex(CorsairPeripheralLighting.LightingControl(CorsairDeviceType.Keyboard), 7));
            Assert.Equal("00 07 05 02 00 01 00", Hex(CorsairPeripheralLighting.LightingControl(CorsairDeviceType.Mouse), 7));
            Assert.Equal("00 07 05 02 00 04 00", Hex(CorsairPeripheralLighting.LightingControl(CorsairDeviceType.Mousemat), 7));
            Assert.Equal("00 07 05 02 00 03 00", Hex(CorsairPeripheralLighting.LightingControl(CorsairDeviceType.HeadsetStand), 7));
            Assert.Equal(65, CorsairPeripheralLighting.Query().Length);
        }

        [Fact]
        public void TheOpen_IsOpenRgbsByTypeAndTable()
        {
            static string[] Heads(List<byte[]> frames) => frames.Select(f => Hex(f, 6)).ToArray();
            // A K70 RGB: the lighting control alone.
            Assert.Equal(new[] { "00 07 05 02 00 03" },
                Heads(CorsairPeripheralLighting.OpenFrames(CorsairDeviceType.Keyboard, CorsairPeripheralLighting.Logical.Normal, 0)));
            // A mouse: ReadFirmwareInfo's special function, then the control.
            Assert.Equal(new[] { "00 07 04 02 00 00", "00 07 05 02 00 01" },
                Heads(CorsairPeripheralLighting.OpenFrames(CorsairDeviceType.Mouse, 0, 0)));
            // A K95 Platinum: special function, control, then the key-input
            // setup (lines 80-90).
            var k95 = CorsairPeripheralLighting.OpenFrames(CorsairDeviceType.Keyboard, CorsairPeripheralLighting.Logical.K95Platinum,
                CorsairPeripheralLighting.Physical.Ansi);
            Assert.Equal(new[] { "00 07 04 02 00 00", "00 07 05 02 00 03", "00 07 05 08 00 01", "00 07 40 1E 00 00",
                "00 07 40 1E 00 1E", "00 07 40 1E 00 3D", "00 07 40 1E 00 61" }, Heads(k95));
        }

        [Fact]
        public void TheKeyInputSetup_CountsPastTheSkipList()
        {
            // SetupK55AndK95LightingControl (lines 421-497) on an ANSI board:
            // 0 to 29, then 30 to 48 and 50 to 60 (0x31 skipped).
            var frames = CorsairPeripheralLighting.KeyInputFrames(CorsairPeripheralLighting.Logical.K95Platinum,
                CorsairPeripheralLighting.Physical.Ansi);
            static int[] Ids(byte[] f) => Enumerable.Range(0, 30).Select(j => (int)f[5 + 2 * j]).ToArray();
            Assert.Equal(Enumerable.Range(0, 30), Ids(frames[1]));
            Assert.Equal(Enumerable.Range(30, 19).Concat(Enumerable.Range(50, 11)), Ids(frames[2]));
            Assert.All(Enumerable.Range(0, 30), j => Assert.Equal(0xC0, frames[2][6 + 2 * j]));
            // From 61, less 63, 65, 66, 81, 83 and 85: the third ends at 96.
            Assert.Equal(new[] { 61, 62, 64, 67, 68 }, Ids(frames[3]).Take(5));
            Assert.Equal(96, Ids(frames[3])[^1]);
            // An ISO K70 MK.2 skips 0x78 to 0x7D too (line 65).
            var iso = CorsairPeripheralLighting.KeyInputFrames(CorsairPeripheralLighting.Logical.K70Mk2, CorsairPeripheralLighting.Physical.Iso);
            Assert.DoesNotContain(0x79, iso.Skip(1).SelectMany(Ids));
            Assert.Contains(0x31, iso.Skip(1).SelectMany(Ids));
        }

        [Fact]
        public void AKeyboardPicture_IsThreeStreamedBanks()
        {
            var lights = new[]
            {
                new LightInfo(0x00, LightKind.Key, 0x29, LightKeySource.Generated),
                new LightInfo(0x89, LightKind.Other, 0, LightKeySource.None),
            };
            var frames = CorsairPeripheralLighting.PictureFrames(CorsairDeviceType.Keyboard, CorsairPeripheralLighting.Logical.Normal,
                lights, new[] { 0x112233, 0xAABBCC });
            // StreamPacket 1, 2 and 3 of 60, 60 and 24 bytes, then 07 28
            // channel 03 finish, 1 for red and green, 2 for blue (lines 237-256).
            Assert.Equal(new[]
            {
                "00 7F 01 3C 00 11", "00 7F 02 3C 00 00", "00 7F 03 18 00 00", "00 07 28 01 03 01",
                "00 7F 01 3C 00 22", "00 7F 02 3C 00 00", "00 7F 03 18 00 00", "00 07 28 02 03 01",
                "00 7F 01 3C 00 33", "00 7F 02 3C 00 00", "00 7F 03 18 00 00", "00 07 28 03 03 02",
            }, frames.Select(f => Hex(f, 6)));
            // 0x89 = 137 is byte 17 of the third packet's data, from byte 5.
            Assert.Equal(0xAA, frames[2][5 + 17]);
            Assert.Equal(0xBB, frames[6][5 + 17]);
            Assert.Equal(0xCC, frames[10][5 + 17]);
            // 24 bytes: byte 5 + 24 stays clear even when the bank has more.
            Assert.All(frames, f => Assert.Equal(65, f.Length));
            var k95 = CorsairPeripheralLighting.PictureFrames(CorsairDeviceType.Keyboard, CorsairPeripheralLighting.Logical.K95Platinum,
                new[] { new LightInfo(157, LightKind.Edge, 0, LightKeySource.None) }, new[] { 0x010203 });
            Assert.Equal("00 7F 03 30 00", Hex(k95[2], 5));
            // The K95 RGB streams 48 too (line 218), the K70 MK.2 24.
            Assert.Equal("00 7F 03 30 00", Hex(CorsairPeripheralLighting.PictureFrames(CorsairDeviceType.Keyboard,
                CorsairPeripheralLighting.Logical.K95, lights, new[] { 0, 0 })[2], 5));
            Assert.Equal("00 7F 03 18 00", Hex(CorsairPeripheralLighting.PictureFrames(CorsairDeviceType.Keyboard,
                CorsairPeripheralLighting.Logical.K70Mk2, lights, new[] { 0, 0 })[2], 5));
            Assert.Equal(0x01, k95[2][5 + 37]);
        }

        [Fact]
        public void TheK55Picture_IsOneZoneReport()
        {
            var lights = CorsairPeripheralLighting.Lights(CorsairDeviceType.Keyboard, CorsairPeripheralLighting.Logical.K55, "x");
            var frame = Assert.Single(CorsairPeripheralLighting.PictureFrames(CorsairDeviceType.Keyboard,
                CorsairPeripheralLighting.Logical.K55, lights, new[] { 0x010203, 0x040506, 0x070809 }));
            Assert.Equal("00 07 25 00 00 01 02 03 04 05 06 07 08 09 00", Hex(frame, 15));
        }

        [Fact]
        public void AMousePicture_NumbersItsZones()
        {
            var lights = CorsairPeripheralLighting.Lights(CorsairDeviceType.Mouse, 0, "Corsair M65");
            var colors = Enumerable.Range(0, 15).Select(i => 0x100000 * i + 0x0A0B0C).ToArray();
            var frame = Assert.Single(CorsairPeripheralLighting.PictureFrames(CorsairDeviceType.Mouse, 0, lights, colors));
            Assert.Equal("00 07 22 0F 00 00 0A 0B 0C 01 1A 0B 0C 02 2A", Hex(frame, 15));
            Assert.Equal("0E EA 0B 0C", Hex(frame, 61, 4));
        }

        [Fact]
        public void TheStandPicture_IsTheMousepadsWithItsLogoFifth()
        {
            var lights = CorsairPeripheralLighting.Lights(CorsairDeviceType.HeadsetStand, 0, "x");
            var colors = Enumerable.Range(1, 9).Select(i => i * 0x010101).ToArray();
            var frame = Assert.Single(CorsairPeripheralLighting.PictureFrames(CorsairDeviceType.HeadsetStand, 0, lights, colors));
            // remap_colors[st100[i]] = colors[i] (lines 170-181): the logo,
            // OpenRGB's ninth, sits fifth.
            Assert.Equal("00 07 22 09 00 01 01 01 02 02 02 03 03 03 04 04 04 09 09 09 05 05 05", Hex(frame, 23));
            var mat = Assert.Single(CorsairPeripheralLighting.PictureFrames(CorsairDeviceType.Mousemat, 0,
                CorsairPeripheralLighting.Lights(CorsairDeviceType.Mousemat, 0, "x"), Enumerable.Repeat(0x123456, 15).ToArray()));
            Assert.Equal("00 07 22 0F 00 12 34 56", Hex(mat, 8));
            Assert.Equal("12 34 56 00", Hex(mat, 47, 4));
        }

        [Fact]
        public void TheK65MiniFrames_AreOpenRgbs()
        {
            var open = CorsairPeripheralLighting.K65MiniOpenFrames();
            Assert.Equal(new[] { "00 08 01 03 00 02", "00 08 02 6E 00 00", "00 08 0D 01 22 00" }, open.Select(f => Hex(f, 6)));
            Assert.All(open, f => Assert.Equal(1025, f.Length));
            var picture = CorsairPeripheralLighting.K65MiniPicture(new[]
            {
                new LightInfo(41, LightKind.Key, 0x29, LightKeySource.Generated),
                new LightInfo(0, LightKind.Key, 0x2C, LightKeySource.Generated),
            }, new[] { 0x112233, 0x445566 });
            Assert.Equal("00 08 06 01 73 01 00 00 12 00 44 55 66", Hex(picture, 13));
            Assert.Equal("11 22 33", Hex(picture, 0x0A + 41 * 3, 3));
            Assert.Equal(1025, picture.Length);
        }

        [Fact]
        public void TheK55ProXtFrames_AreOpenRgbs()
        {
            Assert.Equal(new[] { "00 08 01 03 00 02", "00 08 02 5F 00 00", "00 08 0D 00 01 00" },
                CorsairPeripheralLighting.K55ProXtOpenFrames().Select(f => Hex(f, 6)));
            var frames = CorsairPeripheralLighting.K55ProXtPicture(new[]
            {
                new LightInfo(0, LightKind.Key, 0, LightKeySource.None),
                new LightInfo(136, LightKind.Key, 0, LightKeySource.None),
            }, new[] { 0x112233, 0x445566 });
            Assert.Equal(7, frames.Count);
            // 08 06 00 9B 01, 53 bytes from byte 12 (lines 114-123).
            Assert.Equal("00 08 06 00 9B 01 00 00 00 00 00 00 11", Hex(frames[0], 13));
            Assert.All(frames.Skip(1), f => Assert.Equal("00 08 07 00", Hex(f, 4)));
            // Red of 136 is stream byte 136: frame 2 (53 + 61 = 114), byte 4 + 22.
            Assert.Equal(0x44, frames[2][4 + 22]);
            // Green of 0 is stream byte 137: frame 2, byte 4 + 23.
            Assert.Equal(0x22, frames[2][4 + 23]);
            // Blue of 136 is stream byte 410: frame 6 (53 + 5 * 61 = 358), byte 4 + 52.
            Assert.Equal(0x66, frames[6][4 + 52]);
            Assert.Equal(0, frames[6][4 + 53]);
            Assert.Equal("00 08 01 03 00 01 00", Hex(CorsairPeripheralLighting.BoardHandBack(), 7));
        }

        // ─────────────────────────────────────────────
        //  Sessions
        // ─────────────────────────────────────────────

        [Fact]
        public void AKeyboardSession_AsksWhatItIs_ThenOpens()
        {
            var family = Family();
            var model = family.Match(Collection(0x1B2D));
            var io = new FakeCorsairChannel { Answer = q => q[1] == 0x0E ? Identity(0xC0, 1) : null };
            var session = Assert.IsType<CorsairPeripheralLighting.PeripheralSession>(model.Begin(io, io.Wait));
            Assert.Equal(CorsairDeviceType.Keyboard, session.Type);
            Assert.Equal(CorsairPeripheralLighting.Logical.K95Platinum, session.Logical);
            Assert.Equal(CorsairPeripheralLighting.Physical.Iso, session.Physical);
            Assert.Equal(new[] { "out 00 0E 01 00 00 00", "in 1000", "out 00 07 04 02 00 00", "out 00 07 05 02 00 03",
                "out 00 07 05 08 00 01" }, io.Log.Take(5));
            Assert.Equal(139, session.Map.Lights.Count);
            Assert.False(session.Feature);
        }

        [Fact]
        public void ADeviceThatDoesNotAnswerARead_IsAskedByFeatureReport()
        {
            var model = Family().Match(Collection(0x1B5D));
            var io = new FakeCorsairChannel { FeatureAnswer = () => Identity(0xC1) };
            var session = Assert.IsType<CorsairPeripheralLighting.PeripheralSession>(model.Begin(io, io.Wait));
            Assert.Equal(CorsairDeviceType.Mouse, session.Type);
            Assert.Equal(new[] { "out 00 0E 01 00 00 00", "in 1000", "set 00 0E 01", "get", "out 00 07 04 02 00 00",
                "out 00 07 05 02 00 01" }, io.Log);
        }

        [Fact]
        public void ADeviceThatNeverNamesItsType_IsAskedTenTimes_AndLeftAlone()
        {
            var model = Family().Match(Collection(0x1B2D));
            var io = new FakeCorsairChannel { Answer = _ => Identity(0x00) };
            Assert.Null(model.Begin(io, io.Wait));
            Assert.Equal(10, io.Outputs.Count);
            Assert.All(io.Outputs, f => Assert.Equal("00 0E 01", Hex(f, 3)));
            // A C2 answer from the ST100's product ID is the stand.
            Assert.Equal(CorsairDeviceType.HeadsetStand, CorsairPeripheralLighting.TypeOfAnswer(0xC2, 0x0A34));
            Assert.Equal(CorsairDeviceType.Mousemat, CorsairPeripheralLighting.TypeOfAnswer(0xC2, 0x1B3B));
        }

        [Fact]
        public void AnAnswerOfAnotherType_PicksTheLightsByIt()
        {
            // OpenRGB builds by the answer, not the registration's section.
            var model = Family().Match(Collection(0x1B13));
            Assert.Equal(113, model.Map.Lights.Count);
            var io = new FakeCorsairChannel { Answer = q => q[1] == 0x0E ? Identity(0xC1) : null };
            var session = model.Begin(io, io.Wait);
            Assert.Equal(15, session.Map.Lights.Count);
        }

        [Fact]
        public void APicture_WaitsForTheRecord_ThenGoesOut()
        {
            KeyboardProfileBackups.ResetForTest(persistAtOnce: false);
            var model = Family().Match(Collection(0x1B13));
            var io = new FakeCorsairChannel { Answer = q => q[1] == 0x0E ? Identity(0xC0) : null };
            var session = model.Begin(io, io.Wait);
            int before = io.Outputs.Count;
            var lights = session.Map.Lights;
            var colors = Enumerable.Repeat(0x102030, lights.Count).ToArray();
            Assert.Equal(HidLightingPaint.Later, session.Paint(io, io.Wait, lights, colors));
            Assert.Equal(before, io.Outputs.Count);
            // The record holds the hand-back report and its count.
            var backup = KeyboardProfileBackups.Get("corsair:1B1C:1B13");
            Assert.Equal("00 07 04 01 00", Hex(backup.Value.Contents, 5));
            Assert.Equal(2, backup.Value.Profile);
            KeyboardProfileBackups.Persisted(int.MaxValue);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, colors));
            Assert.Equal(before + 12, io.Outputs.Count);
            Assert.True(model.Owed());
        }

        [Fact]
        public void AKeyboardHandBack_IsCkbNextsHardwareModeTwice()
        {
            var model = Family().Match(Collection(0x1B13));
            var io = new FakeCorsairChannel { Answer = q => q[1] == 0x0E ? Identity(0xC0) : null };
            var session = model.Begin(io, io.Wait);
            session.Paint(io, io.Wait, session.Map.Lights, new int[session.Map.Lights.Count]);
            io.Log.Clear();
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Equal(new[] { "out 00 07 04 01 00 00", "wait 30", "out 00 07 04 01 00 00" }, io.Log);
            Assert.Null(KeyboardProfileBackups.Get("corsair:1B1C:1B13"));
            Assert.Equal(2, session.CrashFrames.Length);
            Assert.All(session.CrashFrames, f => Assert.Equal("00 07 04 01 00", Hex(f, 5)));
        }

        [Fact]
        public void AMouseHandBack_IsHardwareModeOnce()
        {
            var model = Family().Match(Collection(0x1B5D));
            var io = new FakeCorsairChannel { Answer = q => q[1] == 0x0E ? Identity(0xC1) : null };
            var session = model.Begin(io, io.Wait);
            io.Log.Clear();
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Equal(new[] { "out 00 07 04 01 00 00" }, io.Log);
            Assert.Single(session.CrashFrames);
            io.FailWrites = true;
            Assert.False(session.HandBack(io, io.Wait));
        }

        [Fact]
        public void ADeviceAnEarlierRunLeft_IsHandedBackFromItsRecord()
        {
            var model = Family().Match(Collection(0x1B13));
            Assert.False(model.Owed());
            KeyboardProfileBackups.Take("corsair:1B1C:1B13", 2, CorsairPeripheralLighting.SpecialFunction(0x01));
            Assert.True(model.Owed());
            var io = new FakeCorsairChannel();
            Assert.True(model.Repair(io, io.Wait));
            Assert.Equal(new[] { "out 00 07 04 01 00 00", "wait 30", "out 00 07 04 01 00 00" }, io.Log);
            Assert.False(model.Owed());
            // A failed one stays owed until it has failed three times.
            KeyboardProfileBackups.Take("corsair:1B1C:1B13", 2, CorsairPeripheralLighting.SpecialFunction(0x01));
            io.FailWrites = true;
            for (int i = 0; i < 3; i++)
            {
                Assert.True(model.Owed());
                Assert.False(model.Repair(io, io.Wait));
            }
            Assert.False(model.Owed());
        }

        [Fact]
        public void TheK65Mini_OpensWithItsWaits_AndKeepsItsPictureAlive()
        {
            var model = Family().Match(Collection(0x1BAF, page: 0xFF42));
            // 50 s with nothing sent, a resend included
            // (RGBController_CorsairK65Mini.cpp:219 and 238-250).
            Assert.Equal(50000, model.KeepAliveMs);
            Assert.Equal(50000, model.KeepAliveIdleMs);
            Assert.Equal(0, model.RecheckMs);
            var io = new FakeCorsairChannel { OutputReportLength = 1025 };
            var session = model.Begin(io, io.Wait);
            Assert.Equal(new[] { "out 00 08 01 03 00 02", "wait 10", "out 00 08 02 6E 00 00", "wait 10",
                "out 00 08 0D 01 22 00", "wait 10" }, io.Log);
            io.Log.Clear();
            var lights = model.Map.Lights;
            Assert.Null(session.Map);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, new int[lights.Count]));
            Assert.Single(io.Outputs.Skip(3));
            // The keep-alive is the picture again (RGBController_CorsairK65Mini.cpp:242-245).
            Assert.True(session.KeepAlive(io, io.Wait, lights, new int[lights.Count]));
            Assert.Equal(5, io.Outputs.Count);
            Assert.Equal(io.Outputs[3], io.Outputs[4]);
            io.Log.Clear();
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Equal(new[] { "out 00 08 01 03 00 01" }, io.Log);
        }

        [Fact]
        public void TheK55ProXt_OpensAndPaints()
        {
            var model = Family().Match(Collection(0x1BA1, page: 0xFF42));
            Assert.Equal("Corsair K55 RGB PRO XT", model.Name);
            var io = new FakeCorsairChannel();
            var session = model.Begin(io, io.Wait);
            Assert.Equal(3, io.Outputs.Count);
            session.Paint(io, io.Wait, model.Map.Lights, new int[model.Map.Lights.Count]);
            Assert.Equal(10, io.Outputs.Count);
            Assert.Equal(50000, model.KeepAliveMs);
            Assert.Equal(50000, model.KeepAliveIdleMs);
        }

        [Fact]
        public void AFailedWrite_FailsThePicture()
        {
            var model = Family().Match(Collection(0x1B13));
            var io = new FakeCorsairChannel { Answer = q => q[1] == 0x0E ? Identity(0xC0) : null };
            var session = model.Begin(io, io.Wait);
            io.FailWrites = true;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, io.Wait, session.Map.Lights, new int[session.Map.Lights.Count]));
            Assert.Null(model.Begin(io, io.Wait));
        }
    }
}
