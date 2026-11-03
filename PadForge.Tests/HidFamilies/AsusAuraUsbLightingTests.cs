using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The ASUS Aura USB family against OpenRGB's
    /// AsusAuraUSBController at the pinned commit (#557). Every expected byte
    /// is written out from the reference's own code, cited at each test.</summary>
    public class AsusAuraUsbLightingTests
    {
        internal static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');

        /// <summary>The first bytes of a frame as hex, and every byte after
        /// them zero.</summary>
        internal static void Starts(string expected, byte[] frame, int length)
        {
            Assert.Equal(length, frame.Length);
            int n = expected.Split(' ').Length;
            Assert.Equal(expected, Hex(frame.Take(n).ToArray()));
            Assert.All(frame.Skip(n), b => Assert.Equal(0, b));
        }

        /// <summary>A collection a registration answers: its IDs, interface,
        /// usage page and usage, on USB, with the report lengths the
        /// family's frames take.</summary>
        internal static HidCollectionInfo CollectionOf(OpenRgbRegistration r, ushort output = 65, ushort feature = 64)
        {
            int iface = r.Interface == OpenRgbRegistration.Any ? 0 : r.Interface;
            return new HidCollectionInfo
            {
                Path = $@"\\?\hid#vid_{r.VendorId:x4}&pid_{r.ProductId:x4}&mi_{iface:x2}&col01#fake",
                VendorId = (ushort)r.VendorId,
                ProductId = (ushort)r.ProductId,
                InterfaceNumber = iface,
                UsagePage = (ushort)(r.UsagePage == OpenRgbRegistration.Any ? 0xFF00 : r.UsagePage),
                Usage = (ushort)(r.Usage == OpenRgbRegistration.Any ? 0x01 : r.Usage),
                InputReportLength = 65,
                OutputReportLength = output,
                FeatureReportLength = feature,
            };
        }

        /// <summary>A channel that answers like the device: the next of
        /// <paramref name="answers"/> on the first read after each output
        /// report, and nothing on any other read, so a drain before a
        /// request takes nothing.</summary>
        internal static FakeReportChannel Channel(params byte[][] answers)
        {
            var queue = new ConcurrentQueue<byte[]>(answers);
            var io = new FakeReportChannel { InputReportLength = 65, OutputReportLength = 65, FeatureReportLength = 64 };
            int seen = 0;
            io.Input = () =>
            {
                int outs = io.Calls.Count(c => c.Kind == "out");
                if (outs == seen) return null;
                seen = outs;
                return queue.TryDequeue(out var a) ? a : null;
            };
            return io;
        }

        private static List<byte[]> Outs(FakeReportChannel io) => io.Calls.Where(c => c.Kind == "out").Select(c => c.Bytes).ToList();

        private static List<byte[]> Sets(FakeReportChannel io) => io.Calls.Where(c => c.Kind == "set").Select(c => c.Bytes).ToList();

        // ── Registrations ──

        /// <summary>Every registration of the family is matched, by the model
        /// of OpenRGB's first candidate for its collection, which is the one
        /// RunHIDDetector keeps (DetectionManager.cpp:1602-1624).</summary>
        [Fact]
        public void EveryRegistration_IsMatched_ByOpenRgbsFirstCandidate()
        {
            var family = new AsusAuraUsbLighting();
            var rows = OpenRgbRegistrations.Of(AsusAuraUsbLighting.OpenRgbFamily).ToList();
            Assert.Equal(101, rows.Count);
            foreach (var r in rows)
            {
                var collection = CollectionOf(r);
                var model = family.Match(collection);
                Assert.True(model != null, r.Name + " " + r.Source);
                var first = OpenRgbRegistrations.Match(AsusAuraUsbLighting.OpenRgbFamily, collection)[0];
                Assert.Equal(first.Name, model.Name);
                Assert.Same(family, model.Family);
                Assert.Equal(new[] { VendorPrograms.ArmouryCrate }, model.Programs);
                Assert.Equal(HidppBackend.PaintMs, model.RepaintMs);
                Assert.Same(model, family.Match(collection));
            }
        }

        /// <summary>The Spatha X dock shares the 2.4 GHz dongle's collection,
        /// whose detector comes first (AsusAuraMouseControllerDetect.cpp:62-63).</summary>
        [Fact]
        public void TheSpathaXDock_IsTheDonglesCollection()
        {
            var dock = OpenRgbRegistrations.Of(AsusAuraUsbLighting.OpenRgbFamily).Single(r => r.Name == "Asus ROG Spatha X Dock");
            Assert.Equal("Asus ROG Spatha X 2.4GHz", new AsusAuraUsbLighting().Match(CollectionOf(dock)).Name);
        }

        [Fact]
        public void ACollection_WithNoFullOutputReport_IsNoMainboard()
        {
            var board = OpenRgbRegistrations.Of(AsusAuraUsbLighting.OpenRgbFamily).First(r => r.Name == "ASUS Aura Motherboard" && r.UsagePage == -1);
            Assert.Null(new AsusAuraUsbLighting().Match(CollectionOf(board, output: 9)));
            Assert.NotNull(new AsusAuraUsbLighting().Match(CollectionOf(board, output: 65)));
            Assert.NotNull(new AsusAuraUsbLighting().Match(CollectionOf(board, output: 0)));
            Assert.Null(new AsusAuraUsbLighting().Match(new HidCollectionInfo { Path = "x", VendorId = 0x1234, ProductId = 0x18F3 }));
        }

        [Fact]
        public void TheFamily_IsOnTheWorkersList()
            => Assert.Contains(AsusAuraUsbLighting.Instance, HidLightingFamilies.All);

        [Fact]
        public void TheTables_AreReadAtTheRegistrationsCommit()
            => Assert.Equal(OpenRgbRegistrations.OpenRgbCommit, AsusAuraTables.OpenRgbCommit);

        // ── Keyboards ──

        /// <summary>UpdateLeds: 00 C0 81, the count, 00, then value, red,
        /// green, blue per LED, fifteen to a frame
        /// (AsusAuraKeyboardController.cpp:277-311).</summary>
        [Fact]
        public void KeyboardFrames_AreUpdateLeds()
        {
            var lights = Enumerable.Range(0, 17).Select(i => new LightInfo(0x40 + i, LightKind.Key, 4, LightKeySource.Generated)).ToList();
            var colors = Enumerable.Range(0, 17).Select(i => 0x010203 * (i + 1)).ToList();
            var frames = AsusAuraUsbLighting.KeyboardFrames(lights, colors, perLed: true);
            Assert.Equal(2, frames.Count);
            Assert.Equal("00 C0 81 0F 00 40 01 02 03 41 02 04 06", Hex(frames[0].Take(13).ToArray()));
            Assert.Equal("4E 0F 1E 2D", Hex(frames[0].Skip(5 + 14 * 4).Take(4).ToArray()));
            Starts("00 C0 81 02 00 4F 10 20 30 50 11 22 33", frames[1], 65);
            // The TUF K1 and K5 take 00 in place of each value (line 301).
            var k1 = AsusAuraUsbLighting.KeyboardFrames(lights.Take(5).ToList(), colors.Take(5).ToList(), perLed: false);
            Starts("00 C0 81 05 00 00 01 02 03 00 02 04 06 00 03 06 09 00 04 08 0C 00 05 0A 0F", Assert.Single(k1), 65);
            Assert.False(AsusAuraUsbLighting.PerLedKeyboard(0x1945));
            Assert.False(AsusAuraUsbLighting.PerLedKeyboard(0x1899));
            Assert.True(AsusAuraUsbLighting.PerLedKeyboard(0x18F8));
        }

        /// <summary>RGBController_AsusAuraKeyboard.cpp:505-517: the answer as
        /// a byte, its last two digits where the map has them, else UK for a
        /// hundreds digit of 2 and US otherwise.</summary>
        [Fact]
        public void TheRegion_IsOpenRgbsPick()
        {
            var scope = AsusAuraTables.KeyboardSet("AsusROGStrixScopeLayouts");
            var no = AsusAuraTables.KeyboardSet("AsusROGStrixScopeII96WirelessLayouts");
            Assert.Equal(17, AsusAuraUsbLighting.RegionOf(scope, 117));
            Assert.Equal(4, AsusAuraUsbLighting.RegionOf(scope, 204));
            Assert.Equal(4, AsusAuraUsbLighting.RegionOf(scope, 211));
            Assert.Equal(11, AsusAuraUsbLighting.RegionOf(no, 211));
            Assert.Equal(17, AsusAuraUsbLighting.RegionOf(scope, 105));
            // 317 is 61 as a byte: no 61, hundreds digit 0, so US.
            Assert.Equal(17, AsusAuraUsbLighting.RegionOf(scope, 317));
            // 4 as a byte of 260 is UK's key itself.
            Assert.Equal(4, AsusAuraUsbLighting.RegionOf(scope, 260));
            Assert.Equal(117, AsusAuraUsbLighting.ClaymoreRegion(0x1000));
            Assert.Equal(204, AsusAuraUsbLighting.ClaymoreRegion(0x2000));
            Assert.Equal(221, AsusAuraUsbLighting.ClaymoreRegion(0x3000));
            Assert.Equal(117, AsusAuraUsbLighting.ClaymoreRegion(0x4000));
        }

        /// <summary>The map of every keyboard PID is the set
        /// RGBController_AsusAuraKeyboard.cpp:425-501 names, with the
        /// Claymore's by its numpad.</summary>
        [Fact]
        public void EveryKeyboard_HasItsSet()
        {
            var expected = new Dictionary<int, string>
            {
                [0x1875] = "AsusROGStrixFlareLayouts", [0x18CF] = "AsusROGStrixFlareLayouts", [0x18AF] = "AsusROGStrixFlareLayouts",
                [0x194B] = "AsusTUFK7Layouts", [0x18AA] = "AsusTUFK7Layouts",
                [0x18F8] = "AsusROGStrixScopeLayouts", [0x1951] = "AsusROGStrixScopeLayouts", [0x1B12] = "AsusROGStrixScopeLayouts",
                [0x19F6] = "AsusROGStrixScopeLayouts", [0x19F8] = "AsusROGStrixScopeLayouts",
                [0x190C] = "AsusROGStrixScopeTKLLayouts", [0x1954] = "AsusROGStrixScopeTKLLayouts", [0x1A05] = "AsusROGStrixScopeTKLLayouts",
                [0x19D0] = "AsusROGStrixScopeTKLLayouts",
                [0x1AB3] = "AsusROGStrixScopeIILayouts", [0x1AB5] = "AsusROGStrixScopeIILayouts",
                [0x1AAE] = "AsusROGStrixScopeII96WirelessLayouts", [0x1B78] = "AsusROGStrixScopeII96RxWirelessLayouts",
                [0x19FE] = "AsusROGStrixFlareIILayouts", [0x19FC] = "AsusROGStrixFlareIIAnimateLayouts",
                [0x1A83] = "AsusROGAzothLayouts", [0x1A85] = "AsusROGAzothLayouts",
                [0x193C] = "AsusFalchionLayouts", [0x193E] = "AsusFalchionLayouts",
                [0x184D] = "AsusClaymoreNoNumpadLayouts",
                [0x1945] = "AsusTufK1Layouts", [0x1899] = "AsusTufK1Layouts",
                [0x1B30] = "AsusTUFK3GamingGen2Layouts", [0x1C5E] = "AsusTUFK3GamingGen2Layouts",
            };
            var keyboards = OpenRgbRegistrations.Of(AsusAuraUsbLighting.OpenRgbFamily).Where(r => r.Detector == "DetectAsusAuraUSBKeyboard").ToList();
            Assert.Equal(29, keyboards.Count);
            foreach (var r in keyboards)
            {
                Assert.Equal(expected[r.ProductId], AsusAuraUsbLighting.KeyboardSetOf(r.ProductId));
                Assert.NotNull(AsusAuraTables.KeyboardSet(expected[r.ProductId]));
            }
            Assert.Equal("AsusClaymoreNumpadRightLayouts", AsusAuraUsbLighting.KeyboardSetOf(0x184D, 2));
            Assert.Equal("AsusClaymoreNumpadLeftLayouts", AsusAuraUsbLighting.KeyboardSetOf(0x184D, 3));
            Assert.Equal("AsusClaymoreNoNumpadLayouts", AsusAuraUsbLighting.KeyboardSetOf(0x184D, 1));
        }

        /// <summary>Each keyboard layout's lights: OpenRGB's LEDs in order,
        /// each at its LED value and matrix cell, and every key name a key
        /// the HID usage names. AsusAuraKeyboardLayouts.h:103-110 puts
        /// Escape, LED 0, at row 0 column 0 and the US Strix Scope's Logo, LED
        /// 104, at row 0 column 22.</summary>
        [Fact]
        public void KeyboardMaps_AreTheLayouts()
        {
            var map = AsusAuraUsbLighting.KeyboardMap(0x18F8, "AsusROGStrixScopeLayouts", 17);
            var layout = AsusAuraTables.KeyboardSet("AsusROGStrixScopeLayouts").Find(17);
            Assert.Equal(layout.Size, map.Lights.Count);
            Assert.Equal(new LightInfo(0x00, LightKind.Key, 0x29, LightKeySource.Generated) { X = 0, Y = 0, Width = 1, Height = 1, HasPosition = true }, map.Lights[0]);
            var logo = map.Lights[104];
            Assert.Equal(LightKind.Logo, logo.Kind);
            Assert.Equal((22f, 0f), (logo.X, logo.Y));
            for (int i = 0; i < layout.Leds.Length; i++) Assert.Equal(layout.Leds[i].Id, map.Lights[i].Address);
            Assert.Equal("asus:0B05:18F8:AsusROGStrixScopeLayouts:17", map.Product);
            // Every LED of every layout is a key with a usage, a logo, an
            // edge light, a zone, or a labeled light.
            foreach (var set in AsusAuraTables.KeyboardSets)
                foreach (var l in set.Layouts)
                {
                    var m = AsusAuraUsbLighting.KeyboardMap(0x1234, set.Name, l.Region);
                    Assert.Equal(l.Size, m.Lights.Count);
                    Assert.Equal(0, m.UnnamedKeyCount);
                }
            // The K1's five zones.
            var k1 = AsusAuraUsbLighting.KeyboardMap(0x1945, "AsusTufK1Layouts", 17);
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, k1.Lights.Select(x => x.Zone));
            Assert.All(k1.Lights, x => Assert.Equal(LightKind.Zone, x.Kind));
        }

        /// <summary>A Nordic legend takes the key of the UK layout's LED of
        /// the same value: the Scope II 96's "§ ½", LED 1, is UK's back tick
        /// (AsusAuraKeyboardLayouts.h, the NO and UK maps of
        /// AsusROGStrixScopeII96WirelessLayouts).</summary>
        [Fact]
        public void ANordicLegend_IsTheUkKeyOfItsLed()
        {
            var map = AsusAuraUsbLighting.KeyboardMap(0x1AAE, "AsusROGStrixScopeII96WirelessLayouts", 11);
            var light = map.Lights.Single(l => l.Address == 0x01);
            Assert.Equal(LightKind.Key, light.Kind);
            Assert.Equal(0x35, light.Usage);
        }

        /// <summary>A map without the region gives no lights, as operator[]
        /// does in RGBController_AsusAuraKeyboard.cpp:519-534: the TUF K3 Gen
        /// II keeps US alone, and a UK answer falls back to UK.</summary>
        [Fact]
        public void AMissingLayout_GivesNoLights()
        {
            var set = AsusAuraTables.KeyboardSet("AsusTUFK3GamingGen2Layouts");
            int region = AsusAuraUsbLighting.RegionOf(set, 204);
            Assert.Equal(4, region);
            Assert.Empty(AsusAuraUsbLighting.KeyboardMap(0x1B30, "AsusTUFK3GamingGen2Layouts", region).Lights);
        }

        /// <summary>A keyboard's session asks 12 12 after draining the input
        /// and takes its map from the answer, byte 4 times 100 plus byte 5
        /// (AsusAuraKeyboardController.cpp:155-173). Its pictures drain before
        /// each frame and await one answer after it, and its hand-back sends
        /// nothing.</summary>
        [Fact]
        public void AKeyboardSession_ReadsItsRegion_AndHandsBackNothing()
        {
            var answer = new byte[65];
            answer[4] = 2;
            answer[5] = 4;
            var io = Channel(answer);
            var session = AsusAuraUsbLighting.BeginKeyboard(io, 0x18F8);
            Assert.NotNull(session);
            var outs = Outs(io);
            Starts("00 12 12", Assert.Single(outs), 65);
            Assert.Equal(AsusAuraTables.KeyboardSet("AsusROGStrixScopeLayouts").Find(4).Size, session.Map.Lights.Count);
            Assert.Equal("in", io.Calls.First().Kind);
            Assert.Empty(session.CrashFrames);
            Assert.False(session.Feature);

            io.Calls.Clear();
            var lights = session.Map.Lights;
            var colors = lights.Select(_ => 0x112233).ToList();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, colors));
            Assert.Equal((lights.Count + 14) / 15, Outs(io).Count);
            Assert.Equal("in", io.Calls.Last().Kind);
            io.Calls.Clear();
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Empty(io.Calls);
        }

        [Fact]
        public void AKeyboardThatDoesNotAnswer_GetsNoSession()
            => Assert.Null(AsusAuraUsbLighting.BeginKeyboard(Channel(), 0x18F8));

        /// <summary>The Claymore: 40 60 for its numpad, byte 5 times 2 plus
        /// byte 4 (AsusAuraKeyboardController.cpp:195-211), then
        /// AllowRemoteControl 1 and 3, and 0 to hand back
        /// (RGBController_AsusAuraKeyboard.cpp:583-596 and 657-662).</summary>
        [Fact]
        public void TheClaymore_TakesRemoteControl_AndGivesItBack()
        {
            var numpad = new byte[65];
            numpad[5] = 1;
            var io = Channel(numpad);
            var session = AsusAuraUsbLighting.BeginKeyboard(io, 0x184D);
            var outs = Outs(io);
            Assert.Equal(3, outs.Count);
            Starts("00 40 60", outs[0], 65);
            Starts("00 41 01", outs[1], 65);
            Starts("00 41 03", outs[2], 65);
            var right = AsusAuraTables.KeyboardSet("AsusClaymoreNumpadRightLayouts").Find(17);
            Assert.Equal(right.Size, session.Map.Lights.Count);
            Starts("00 41 00", Assert.Single(session.CrashFrames), 65);
            io.Calls.Clear();
            Assert.True(session.HandBack(io, _ => { }));
            Starts("00 41 00", Assert.Single(Outs(io)), 65);
        }

        // ── Mice ──

        /// <summary>aura_mouse_devices keeps the first entry of 0x189E, the
        /// Gladius II Wireless (AsusAuraMouseDevices.h:179-191), over the
        /// Strix Impact II Gundam (lines 473-485).</summary>
        [Fact]
        public void TheMouseTable_KeepsTheFirstOfAKeyListedTwice()
        {
            var mouse = AsusAuraTables.Mouse(0x189E);
            Assert.True(mouse.Wireless);
            Assert.Equal(new byte[] { 0, 1 }, mouse.Zones);
            Assert.Equal(new byte[] { 0, 1, 2, 255, 4, 255, 6 }, mouse.Modes);
            Assert.Equal(new[] { "Logo", "Scroll Wheel", "Underglow", "All", "Dock" }, AsusAuraTables.MouseZoneNames);
        }

        /// <summary>SendUpdate (AsusAuraMouseController.cpp:156-224): 00 51 28,
        /// the zone, 00, the mode, the brightness, then red, green, blue,
        /// direction, random and speed, and on the Gladius II Origin PNK LTD
        /// a 00 before the color.</summary>
        [Fact]
        public void AMouseWithoutDirect_IsLitPerZoneInStatic()
        {
            Starts("00 51 28 02 00 00 04 11 22 33", AsusAuraUsbLighting.MouseUpdateFrame(0x1845, 2, 0, 0x112233, 4), 65);
            Starts("00 51 28 01 00 00 04 00 11 22 33", AsusAuraUsbLighting.MouseUpdateFrame(0x18CD, 1, 0, 0x112233, 4), 65);

            var model = new AsusAuraUsbLighting().ModelFor(
                OpenRgbRegistrations.Of(AsusAuraUsbLighting.OpenRgbFamily).Single(r => r.Name == "ASUS ROG Gladius II"), 0x1845);
            Assert.Equal(new[] { LightKind.Logo, LightKind.Zone, LightKind.Edge }, model.Map.Lights.Select(l => l.Kind));
            var io = Channel();
            var session = model.Begin(io, _ => { });
            Assert.Empty(io.Calls);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x010101, 0x020202, 0x030303 }));
            var outs = Outs(io);
            Assert.Equal(3, outs.Count);
            Starts("00 51 28 00 00 00 04 01 01 01", outs[0], 65);
            Starts("00 51 28 01 00 00 04 02 02 02", outs[1], 65);
            Starts("00 51 28 02 00 00 04 03 03 03", outs[2], 65);
            // Each frame after a drain and before an awaited answer.
            Assert.Equal("in", io.Calls.First().Kind);
            Assert.Equal("in", io.Calls.Last().Kind);
            Assert.True(session.HandBack(io, _ => { }));
        }

        /// <summary>SendDirect (AsusAuraMouseController.cpp:231-264) of the
        /// Chakram X: nine LEDs, the logo at 7, the wheel at 8 and the
        /// underglow at 0 to 6 (AsusAuraMouseDevices.h:772-781), five to a
        /// frame from an offset.</summary>
        [Fact]
        public void AMouseWithDirect_TakesDirectFrames()
        {
            var frames = AsusAuraUsbLighting.MouseDirectFrames(0x1A18, new[] { 0xAA0000, 0x00BB00, 0x0000CC });
            Assert.Equal(2, frames.Count);
            Starts("00 51 29 05 00 00 00 00 CC 00 00 CC 00 00 CC 00 00 CC 00 00 CC", frames[0], 65);
            Starts("00 51 29 04 00 05 00 00 CC 00 00 CC AA 00 00 00 BB 00", frames[1], 65);
            // A product ID with no LED map sends nothing.
            Assert.Empty(AsusAuraUsbLighting.MouseDirectFrames(0x1845, new[] { 1 }));
        }

        /// <summary>The Strix Evolve's Direct mode and brightness, then its
        /// color, each by SendUpdate, 0C C4 0F 00 with the key and value, and a
        /// read after it (AsusAuraMouseGen1Controller.cpp:116-138,
        /// RGBController_AsusROGStrixEvolve.cpp:119-130). ResetToSavedLighting,
        /// 0C C4, hands back (lines 172-181).</summary>
        [Fact]
        public void TheStrixEvolve_IsLitThroughItsKeys()
        {
            var model = new AsusAuraUsbLighting().ModelFor(
                OpenRgbRegistrations.Of(AsusAuraUsbLighting.OpenRgbFamily).Single(r => r.Name == "ASUS ROG Strix Evolve"), 0x185B);
            var io = Channel();
            var waits = new List<int>();
            var session = model.Begin(io, waits.Add);
            var sets = Sets(io);
            Assert.Equal(2, sets.Count);
            Starts("0C C4 0F 00 19 01", sets[0], 9);
            Starts("0C C4 0F 00 1A FF", sets[1], 9);
            Assert.Equal(new[] { 15, 15 }, waits);
            Assert.Equal(2, io.Calls.Count(c => c.Kind == "get"));
            io.Calls.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x102030 }));
            sets = Sets(io);
            Starts("0C C4 0F 00 1C 10", sets[0], 9);
            Starts("0C C4 0F 00 1D 20", sets[1], 9);
            Starts("0C C4 0F 00 1E 30", sets[2], 9);
            Assert.True(session.Feature);
            Starts("0C C4", Assert.Single(session.CrashFrames), 9);
            io.Calls.Clear();
            Assert.True(session.HandBack(io, _ => { }));
            Starts("0C C4", Assert.Single(Sets(io)), 9);
        }

        /// <summary>SendDirectSpatha: report 10 of 33 bytes, 00 F0 00, then
        /// the side, wheel and logo colors (AsusAuraMouseGen1Controller.cpp:152-170).
        /// Its Direct mode sends nothing (RGBController_AsusROGSpatha.cpp:192-197).</summary>
        [Fact]
        public void TheSpatha_TakesOneFrame()
        {
            Starts("10 00 F0 00 01 02 03 04 05 06 07 08 09", AsusAuraUsbLighting.SpathaDirectFrame(new[] { 0x010203, 0x040506, 0x070809 }), 33);
            var model = new AsusAuraUsbLighting().ModelFor(
                OpenRgbRegistrations.Of(AsusAuraUsbLighting.OpenRgbFamily).Single(r => r.Name == "ASUS ROG Spatha Wired"), 0x181C);
            var io = Channel();
            Assert.NotNull(model.Begin(io, _ => { }));
            Assert.Empty(io.Calls);
            Assert.Equal(new[] { LightKind.Edge, LightKind.Zone, LightKind.Logo }, model.Map.Lights.Select(l => l.Kind));
        }

        // ── Mousemats, stands, monitors ──

        /// <summary>UpdateLeds of a mousemat, report EE (AsusAuraMousematController.cpp:85-107),
        /// and of a headset stand in two frames (AsusAuraHeadsetStandController.cpp:83-125).</summary>
        [Fact]
        public void MousematAndStandFrames_AreUpdateLeds()
        {
            var colors = Enumerable.Range(1, 18).Select(i => i << 16 | i << 8 | i).ToList();
            var mat = Assert.Single(AsusAuraUsbLighting.MousematFrames(colors));
            Assert.Equal("EE C0 81 00 00 00 01 01 01 00 02 02 02", Hex(mat.Take(13).ToArray()));
            Assert.Equal("00 0F 0F 0F", Hex(mat.Skip(5 + 14 * 4).Take(4).ToArray()));
            Assert.Equal(65, mat.Length);
            var stand = AsusAuraUsbLighting.HeadsetStandFrames(colors);
            Assert.Equal(2, stand.Count);
            Assert.Equal("00 C0 81 00 00 00 01 01 01", Hex(stand[0].Take(9).ToArray()));
            Starts("00 C0 81 01 00 00 10 10 10 00 11 11 11 00 12 12 12", stand[1], 65);
        }

        /// <summary>The monitors: 03 02 A1 80, the register and the value, red,
        /// blue and green at three registers from 16 (0 on the XG279Q), the
        /// PG32UQ and XG32VC opening with 20 and 30, and A0 01 to apply
        /// (AsusAuraMonitorController.cpp:64-123).</summary>
        [Fact]
        public void MonitorFrames_AreItsRegisters()
        {
            var colors = new[] { 0x112233, 0x445566, 0x778899 };
            var pg = AsusAuraUsbLighting.MonitorFrames(0x19B9, colors);
            Assert.Equal(12, pg.Count);
            Assert.Equal("03 02 A1 80 20 00 00 00", Hex(pg[0]));
            Assert.Equal("03 02 A1 80 30 00 00 00", Hex(pg[1]));
            Assert.Equal("03 02 A1 80 10 11 00 00", Hex(pg[2]));
            Assert.Equal("03 02 A1 80 11 33 00 00", Hex(pg[3]));
            Assert.Equal("03 02 A1 80 12 22 00 00", Hex(pg[4]));
            Assert.Equal("03 02 A1 80 16 77 00 00", Hex(pg[8]));
            Assert.Equal("03 02 A1 80 A0 01 00 00", Hex(pg[11]));
            var xg = AsusAuraUsbLighting.MonitorFrames(0x1919, colors);
            Assert.Equal(10, xg.Count);
            Assert.Equal("03 02 A1 80 00 11 00 00", Hex(xg[0]));
            Assert.Equal(10, AsusAuraUsbLighting.MonitorFrames(0x198C, colors).Count);
            Assert.Equal("03 02 A1 80 10 11 00 00", Hex(AsusAuraUsbLighting.MonitorFrames(0x198C, colors)[0]));
        }

        // ── Mainboards and addressable controllers ──

        private static byte[] Answer(byte id, params (int At, byte Value)[] bytes)
        {
            var a = new byte[65];
            a[0] = 0xEC;
            a[1] = id;
            foreach (var (at, value) in bytes) a[at] = value;
            return a;
        }

        /// <summary>A mainboard's channels from its config table
        /// (AsusAuraMainboardController.cpp:13-56): its own LEDs on effect
        /// channel 0 and direct channel 4, then each header on the next effect
        /// channel, and on AULA3-AR32 firmware the board's effect channel
        /// after the headers.</summary>
        [Fact]
        public void AMainboardsChannels_AreItsConfigTable()
        {
            var config = new byte[60];
            config[0x02] = 3;
            config[0x1B] = 6;
            config[0x1D] = 2;
            var channels = AsusAuraUsbLighting.AuraChannels(config, "AULA3-6K75", mainboard: true);
            Assert.Equal(new AsusAuraUsbLighting.AuraChannel(0, 4, 6, 2, false), channels[0]);
            Assert.Equal(new AsusAuraUsbLighting.AuraChannel(1, 0, 1, 0, true), channels[1]);
            Assert.Equal(new AsusAuraUsbLighting.AuraChannel(3, 2, 1, 0, true), channels[3]);
            var ar32 = AsusAuraUsbLighting.AuraChannels(config, "AULA3-AR32-0207", mainboard: true);
            Assert.Equal(new AsusAuraUsbLighting.AuraChannel(3, 4, 6, 2, false), ar32[0]);
            Assert.Equal(new AsusAuraUsbLighting.AuraChannel(0, 0, 1, 0, true), ar32[1]);
            // More headers than LEDs: no 12 V headers (lines 19-22).
            config[0x1D] = 9;
            Assert.Equal(0, AsusAuraUsbLighting.AuraChannels(config, null, true)[0].Headers);
            var addressable = AsusAuraUsbLighting.AuraChannels(config, null, mainboard: false);
            Assert.Equal(3, addressable.Count);
            Assert.All(addressable, c => Assert.True(c.Addressable));
            var map = AsusAuraUsbLighting.AuraUsbMap(0x18F3, channels);
            Assert.Equal(9, map.Lights.Count);
            Assert.Equal(Enumerable.Range(1, 9), map.Lights.Select(l => l.Zone));
        }

        /// <summary>SendDirect (AsusAuraUSBController.cpp:271-335): EC 40, the
        /// channel with 80 on the last frame, the offset, the count, then
        /// twenty colors to a frame.</summary>
        [Fact]
        public void AuraDirectFrames_AreSendDirect()
        {
            var colors = Enumerable.Range(1, 25).Select(i => i).ToList();
            var frames = AsusAuraUsbLighting.AuraDirectFrames(0x04, colors);
            Assert.Equal(2, frames.Count);
            Assert.Equal("EC 40 04 00 14 00 00 01 00 00 02", Hex(frames[0].Take(11).ToArray()));
            Starts("EC 40 84 14 05 00 00 15 00 00 16 00 00 17 00 00 18 00 00 19", frames[1], 65);
            Starts("EC 40 82 00 01 00 00 07", Assert.Single(AsusAuraUsbLighting.AuraDirectFrames(0x02, new[] { 7 })), 65);
        }

        /// <summary>The session reads EC 82 and EC B0, and a mainboard then
        /// takes SetGen1 and Direct mode on its own LEDs
        /// (AsusAuraMainboardController.cpp:58-80 and 154-182). A header
        /// takes OpenRGB's Static effect once per color: EC 35 then EC 36 with
        /// its bit after the board's LEDs (lines 107-153 and 184-219).</summary>
        [Fact]
        public void AMainboardSession_ReadsItsTable_AndLightsItsHeadersStatic()
        {
            var io = Channel(Answer(0x02, (2, (byte)'A'), (3, (byte)'U')), Answer(0x30, (4 + 0x02, 1), (4 + 0x1B, 2)));
            var session = AsusAuraUsbLighting.BeginAuraUsb(io, 0x18F3, mainboard: true);
            var outs = Outs(io);
            Assert.Equal(4, outs.Count);
            Starts("EC 82", outs[0], 65);
            Starts("EC B0", outs[1], 65);
            Starts("EC 52 53 00 01", outs[2], 65);
            Starts("EC 35 00 00 00 FF", outs[3], 65);
            Assert.Equal(3, session.Map.Lights.Count);
            io.Calls.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, session.Map.Lights, new[] { 0x010203, 0x040506, 0x0A0B0C }));
            outs = Outs(io);
            Assert.Equal(3, outs.Count);
            Starts("EC 40 84 00 02 01 02 03 04 05 06", outs[0], 65);
            Starts("EC 35 01 00 00 01", outs[1], 65);
            Starts("EC 36 00 04 00 00 00 00 00 00 00 0A 0B 0C", outs[2], 65);
            io.Calls.Clear();
            session.Paint(io, _ => { }, session.Map.Lights, new[] { 0x010203, 0x040506, 0x0A0B0C });
            Assert.Single(Outs(io));
            Assert.True(session.HandBack(io, _ => { }));
        }

        /// <summary>An addressable controller's headers take EC 3B with the
        /// channel, 00, the mode and the color
        /// (AsusAuraAddressableController.cpp:79-112), and no SetGen1.</summary>
        [Fact]
        public void AnAddressableSession_LightsEachHeaderStatic()
        {
            var io = Channel(Answer(0x02), Answer(0x30, (4 + 0x02, 2)));
            var session = AsusAuraUsbLighting.BeginAuraUsb(io, 0x1889, mainboard: false);
            Assert.Equal(2, Outs(io).Count);
            io.Calls.Clear();
            session.Paint(io, _ => { }, session.Map.Lights, new[] { 0x112233, 0x445566 });
            var outs = Outs(io);
            Starts("EC 3B 00 00 01 11 22 33", outs[0], 65);
            Starts("EC 3B 01 00 01 44 55 66", outs[1], 65);
            Assert.Null(AsusAuraUsbLighting.BeginAuraUsb(Channel(Answer(0x02), Answer(0x31)), 0x1889, false));
        }

        // ── Coolers and the Ally ──

        /// <summary>The Ryuo's Direct mode, EC 3B 00 22 FF, and EC 40 80 00
        /// with the count and colors (AsusAuraRyuoAIOController.cpp:89-139);
        /// the Strix LC's EC 3B 00 FF and EC 40 00 FF 04 with 77 10 F3 after
        /// the colors (AsusROGStrixLCController.cpp:82-127).</summary>
        [Fact]
        public void CoolerFrames_AreTheirDirectModes()
        {
            var rows = OpenRgbRegistrations.Of(AsusAuraUsbLighting.OpenRgbFamily).ToList();
            var family = new AsusAuraUsbLighting();
            var ryuo = family.ModelFor(rows.Single(r => r.Name == "ASUS ROG Ryuo AIO"), 0x1887);
            var ryujin = family.ModelFor(rows.Single(r => r.Name == "ASUS ROG Ryujin AIO"), 0x18AE);
            Assert.Equal(12, ryuo.Map.Lights.Count);
            Assert.Equal(5, ryujin.Map.Lights.Count);
            var io = Channel();
            var session = ryujin.Begin(io, _ => { });
            Starts("EC 3B 00 22 FF", Assert.Single(Outs(io)), 65);
            io.Calls.Clear();
            session.Paint(io, _ => { }, ryujin.Map.Lights, new[] { 1, 2, 3, 4, 5 });
            Starts("EC 40 80 00 05 00 00 01 00 00 02 00 00 03 00 00 04 00 00 05", Assert.Single(Outs(io)), 65);

            var lc = family.ModelFor(rows.Single(r => r.Name == "ASUS ROG Strix LC"), 0x879E);
            io = Channel();
            session = lc.Begin(io, _ => { });
            Starts("EC 3B 00 FF", Assert.Single(Outs(io)), 65);
            io.Calls.Clear();
            session.Paint(io, _ => { }, lc.Map.Lights, new[] { 0x010203, 0x040506, 0x070809, 0x0A0B0C });
            Starts("EC 40 00 FF 04 01 02 03 04 05 06 07 08 09 0A 0B 0C 77 10 F3", Assert.Single(Outs(io)), 65);
        }

        /// <summary>The Ally's opening, SendInitialization and
        /// UpdateBrightness(3), and its four LEDs, 5A D1 08 0C
        /// (AsusROGAllyController.cpp:58-124).</summary>
        [Fact]
        public void TheAlly_IsOpenedAndLitThroughFeatureReports()
        {
            var opening = AsusAuraUsbLighting.AllyOpening();
            Starts("5D B9", opening[0], 64);
            Starts("5D 41 53 55 53 20 54 65 63 68 2E 49 6E 63 2E", opening[1], 64);
            Starts("5A BA C5 C4 03", opening[2], 64);
            Starts("5A D1 08 0C 01 02 03 04 05 06 07 08 09 0A 0B 0C",
                AsusAuraUsbLighting.AllyFrame(new[] { 0x010203, 0x040506, 0x070809, 0x0A0B0C }), 64);
            var model = new AsusAuraUsbLighting().ModelFor(
                OpenRgbRegistrations.Of(AsusAuraUsbLighting.OpenRgbFamily).Single(r => r.Name == "ASUS ROG Ally"), 0x1ABE);
            Assert.Equal(new[] { 1, 1, 2, 2 }, model.Map.Lights.Select(l => l.Zone));
            var io = Channel();
            Assert.NotNull(model.Begin(io, _ => { }));
            Assert.Equal(3, Sets(io).Count);
        }
    }
}
