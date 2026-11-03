using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Roccat family against OpenRGB's RoccatController at the pinned
    /// commit (#557), with Eruption, roccat-vulcan-api-rs and SignalRGB where
    /// they bear on a byte. Every expected byte is written out from the
    /// reference line the test names, never from the builder under test.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class RoccatLightingTests : IDisposable
    {
        public RoccatLightingTests() => KeyboardProfileBackups.ResetForTest(persistAtOnce: true);

        public void Dispose() => KeyboardProfileBackups.ResetForTest();

        private static string Hex(IEnumerable<byte> bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));

        private static HidCollectionInfo CollectionOf(OpenRgbRegistration r, string path = null)
            => new()
            {
                Path = path ?? $@"\\?\hid#vid_{r.VendorId:x4}&pid_{r.ProductId:x4}&mi_{Math.Max(0, r.Interface):x2}#7&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
                VendorId = (ushort)r.VendorId,
                ProductId = (ushort)r.ProductId,
                InterfaceNumber = r.Interface,
                UsagePage = (ushort)(r.UsagePage < 0 ? 0xFF00 : r.UsagePage),
                Usage = (ushort)(r.Usage < 0 ? 1 : r.Usage),
            };

        // ── Registrations ──

        /// <summary>RoccatControllerDetect.cpp:390-434 registers 28
        /// detectors. Every one but the Vulcan keyboards' is a model of the
        /// HID family, under OpenRGB's name, and the Vulcan keyboards' are
        /// left to the own-look family, which finds each one's two
        /// collections.</summary>
        [Fact]
        public void EveryRegistration_IsLit()
        {
            var registrations = OpenRgbRegistrations.Of("RoccatController").ToList();
            Assert.Equal(28, registrations.Count);
            int vulcans = 0;
            foreach (var r in registrations)
            {
                var collection = CollectionOf(r);
                var model = RoccatLighting.Instance.Match(collection);
                if (r.Detector == RoccatVulcanLighting.Detector)
                {
                    vulcans++;
                    Assert.Null(model);
                    var (ledIf, ledPage, ctrlIf, ctrlPage) = RoccatVulcanLighting.Interfaces((ushort)r.ProductId);
                    Assert.Equal(r.Interface, ctrlIf);
                    Assert.Equal(r.UsagePage, ctrlPage);
                    var led = new HidCollectionInfo
                    {
                        Path = $@"\\?\hid#vid_{r.VendorId:x4}&pid_{r.ProductId:x4}&mi_{ledIf:x2}#7&2&0&0000#{{4d1e55b2}}",
                        VendorId = (ushort)r.VendorId, ProductId = (ushort)r.ProductId, InterfaceNumber = ledIf,
                        UsagePage = (ushort)ledPage, Usage = 1,
                    };
                    var family = new RoccatVulcanLighting(() => new[] { collection, led }, _ => Box, _ => null, (_, _, _) => false);
                    var found = Assert.Single(family.Look());
                    Assert.Equal(collection.Path, found.Key);
                    Assert.Equal(r.Name, found.Model.Name);
                    Assert.Same(VendorPrograms.Roccat, found.Model.Programs);
                    Assert.True(found.Model.Map.Lights.Count > 0, r.Name);
                    continue;
                }
                Assert.NotNull(model);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(RoccatLighting.Instance, model.Family);
                Assert.Same(VendorPrograms.Roccat, model.Programs);
                Assert.Equal($"roccat:{r.VendorId:X4}:{r.ProductId:X4}", model.Map.Product);
            }
            Assert.Equal(14, vulcans);
            Assert.Contains(RoccatLighting.Instance, HidLightingFamilies.All);
            Assert.Contains(RoccatVulcanLighting.Instance, HidLightingFamilies.All);
        }

        [Fact]
        public void ACollectionOnAnotherInterfaceOrPage_IsNotTaken()
        {
            var kone = OpenRgbRegistrations.Of("RoccatController").Single(r => r.Name == "Roccat Kone Pro");
            Assert.Null(RoccatLighting.Instance.Match(CollectionOf(kone) with { InterfaceNumber = 2 }));
            Assert.Null(RoccatLighting.Instance.Match(CollectionOf(kone) with { UsagePage = 0xFF00 }));
            Assert.Null(RoccatLighting.Instance.Match(CollectionOf(kone) with { ProductId = 0x2C89 }));
        }

        /// <summary>ROCCAT_Swarm prefixes both of Swarm's processes.</summary>
        [Fact]
        public void Swarm_IsTheProgramThatHoldsThem()
        {
            Assert.Contains(VendorPrograms.RoccatSwarm, VendorPrograms.All);
            Assert.True(VendorPrograms.RoccatSwarm.Owns("ROCCAT_Swarm"));
            Assert.True(VendorPrograms.RoccatSwarm.Owns("ROCCAT_Swarm_Monitor"));
            Assert.False(VendorPrograms.RoccatSwarm.Owns("Swarm"));
        }

        // ── Lights ──

        [Fact]
        public void MiceAndPads_HaveOpenRgbsLeds()
        {
            HidLightingModel M(string name)
                => RoccatLighting.Instance.Match(CollectionOf(OpenRgbRegistrations.Of("RoccatController").Single(r => r.Name == name)));

            // RGBController_RoccatBurst.cpp:130-141: Scroll Wheel, Logo.
            Assert.Equal(new[] { LightKind.Zone }, M("Roccat Burst Core").Map.Lights.Select(l => l.Kind));
            Assert.Equal(new[] { LightKind.Zone, LightKind.Logo }, M("Roccat Burst Pro").Map.Lights.Select(l => l.Kind));
            // RGBController_RoccatBurstProAir.cpp:105-111: wheel, logo, left, right.
            Assert.Equal(new[] { LightKind.Zone, LightKind.Logo, LightKind.Zone, LightKind.Zone },
                M("Roccat Burst Pro Air").Map.Lights.Select(l => l.Kind));
            Assert.Equal(2, M("Roccat Kone Pro").Map.ZoneCount);
            Assert.Equal(2, M("Roccat Kone Pro Air").Map.ZoneCount);
            Assert.Equal(2, M("Roccat Kone Pro Air (Wired)").Map.ZoneCount);
            // RGBController_RoccatKoneXP.cpp:177-232: 9 left, 9 right, wheel, DPI.
            var xp = M("Roccat Kone XP").Map;
            Assert.Equal(20, xp.Lights.Count);
            Assert.Equal(18, xp.EdgeCount);
            Assert.Equal(2, xp.ZoneCount);
            Assert.Equal(Enumerable.Range(0, 20), xp.Lights.Select(l => l.Address));
            // RGBController_RoccatKoneAimo.cpp:58-157: wheel, 4 + 4 strip, 2 lower.
            var aimo = M("Roccat Kone Aimo").Map;
            Assert.Equal(11, aimo.Lights.Count);
            Assert.Equal(10, aimo.EdgeCount);
            Assert.Equal(11, M("Roccat Kone Aimo 16K").Map.Lights.Count);
            // RGBController_RoccatKova.cpp:94-109: Wheel LED, Stripe LED.
            Assert.Equal(new[] { LightKind.Zone, LightKind.Edge }, M("Roccat Kova").Map.Lights.Select(l => l.Kind));
            Assert.Equal(2, M("Roccat Sense Aimo Mid").Map.EdgeCount);
            Assert.Equal(2, M("Roccat Sense Aimo XXL").Map.EdgeCount);
            Assert.Single(M("Roccat Elo 7.1").Map.Lights);
            // RGBController_RoccatHordeAimo.cpp:63-68: LED 1 to 6.
            Assert.Equal(6, M("Roccat Horde Aimo").Map.ZoneCount);
        }

        /// <summary>Each Vulcan entry's lights are its LEDs in OpenRGB's
        /// order, at their values, and every name OpenRGB gives a key
        /// reaches a usage or a label.</summary>
        [Fact]
        public void EveryVulcanLayout_LightsEveryLed()
        {
            Assert.Equal(13, RoccatVulcanLayouts.All.Length);
            foreach (var layout in RoccatVulcanLayouts.All)
            {
                var lights = RoccatVulcanLayouts.Lights(layout);
                Assert.Equal(layout.Leds.Select(l => (int)l.Id), lights.Select(l => l.Address));
                Assert.Equal(lights.Count, lights.Select(l => l.Address).Distinct().Count());
                for (int i = 0; i < lights.Count; i++)
                {
                    var name = layout.Leds[i].Name;
                    if (name.Length == 0 || name.StartsWith("Keyboard LED", StringComparison.Ordinal)
                        || name.StartsWith(RoccatVulcanLayouts.PalmRest, StringComparison.Ordinal))
                        continue;
                    Assert.True(lights[i].Usage != 0 || lights[i].Label != LightLabel.None, layout.Table + " " + name);
                }
            }
        }

        [Fact]
        public void Vulcan120_UsLayout_IsOpenRgbsGrid()
        {
            var layout = RoccatVulcanLayouts.Find("RoccatVulcan120AimoLayouts", RoccatVulcanLayouts.LayoutUs);
            var lights = RoccatVulcanLayouts.Lights(layout);
            // RoccatVulcanKeyboardLayouts.h:268-276: 104 LEDs, Escape at 0x00,
            // ` at 0x01, Tab at 0x02; the grid of line 45 puts Escape at row 0
            // column 0 and Tab at row 2 column 0.
            Assert.Equal(104, lights.Count);
            Assert.Equal((0x00, (ushort)0x29), (lights[0].Address, lights[0].Usage));
            Assert.Equal((0x01, (ushort)0x35), (lights[1].Address, lights[1].Usage));
            Assert.Equal((0x02, (ushort)0x2B), (lights[2].Address, lights[2].Usage));
            Assert.Equal((0f, 0f), (lights[0].X, lights[0].Y));
            Assert.Equal((0f, 2f), (lights[2].X, lights[2].Y));
            // Q is LED 0x07, the eleventh in the list.
            Assert.Equal((0x07, (ushort)0x14), (lights[10].Address, lights[10].Usage));
        }

        /// <summary>The layout byte picks the entry as SetupZones does
        /// (RGBController_RoccatVulcanKeyboard.cpp:148-163).</summary>
        [Fact]
        public void TheReportedLayout_PicksTheEntry()
        {
            Assert.Equal(0, RoccatVulcanLayouts.RegionOf(0));
            Assert.Equal(2, RoccatVulcanLayouts.RegionOf(1));
            Assert.Equal(2, RoccatVulcanLayouts.RegionOf(2));
            Assert.Equal(2, RoccatVulcanLayouts.RegionOf(3));
            Assert.Equal(6, RoccatVulcanLayouts.RegionOf(6));
            Assert.Equal(0, RoccatVulcanLayouts.RegionOf(4));
            Assert.Equal(105, RoccatVulcanLayouts.Find("RoccatVulcan120AimoLayouts", 1).Leds.Length);
            // The Vulcan II has a UK entry only (RoccatVulcanKeyboardLayouts.h:682-825).
            Assert.Equal(2, RoccatVulcanLayouts.Find("RoccatVulcanIILayouts", 0).Region);
            Assert.Equal(6, RoccatVulcanLayouts.Find("RoccatVulcanIIMaxLayouts", 6).Region);
        }

        [Fact]
        public void TheTables_FollowTheProduct()
        {
            // RGBController_RoccatVulcanKeyboard.cpp:110-144.
            Assert.Equal("RoccatVulcan120AimoLayouts", RoccatVulcanLayouts.TableOf(0x1E7D, 0x307A));
            Assert.Equal("RoccatVulcan120AimoLayouts", RoccatVulcanLayouts.TableOf(0x1E7D, 0x3098));
            Assert.Equal("RoccatVulcanTKLLayouts", RoccatVulcanLayouts.TableOf(0x1E7D, 0x2FEE));
            Assert.Equal("RoccatVulcanTKLLayouts", RoccatVulcanLayouts.TableOf(0x1E7D, 0x311A));
            Assert.Equal("RoccatVulcanTKLLayouts", RoccatVulcanLayouts.TableOf(0x10F5, 0x5023));
            Assert.Equal("TurtleBeachVulcanIITKLProLayouts", RoccatVulcanLayouts.TableOf(0x10F5, 0x5001));
            Assert.Equal("RoccatPyroLayouts", RoccatVulcanLayouts.TableOf(0x1E7D, 0x30F7));
            Assert.Equal("RoccatPyroLayouts", RoccatVulcanLayouts.TableOf(0x1E7D, 0x314C));
            Assert.Equal("RoccatVulcanIILayouts", RoccatVulcanLayouts.TableOf(0x1E7D, 0x2F4E));
            Assert.Equal("RoccatVulcanIILayouts", RoccatVulcanLayouts.TableOf(0x10F5, 0x501B));
            Assert.Equal("RoccatVulcanIIMaxLayouts", RoccatVulcanLayouts.TableOf(0x1E7D, 0x2EE2));
            Assert.Equal("RoccatVulcanIIMiniLayouts", RoccatVulcanLayouts.TableOf(0x1E7D, 0x2F09));
            Assert.Equal("RoccatMagmaLayouts", RoccatVulcanLayouts.TableOf(0x1E7D, 0x3124));
            Assert.Equal("RoccatMagmaLayouts", RoccatVulcanLayouts.TableOf(0x1E7D, 0x69A0));
        }

        /// <summary>The Japanese legends sit on the US keys at their cells
        /// (RoccatVulcanKeyboardLayouts.h:837-845 against 827-835): E/J on `,
        /// ^ on =, @ on [, : on '.</summary>
        [Fact]
        public void JapaneseLegends_LightTheirKeys()
        {
            var layout = RoccatVulcanLayouts.Find("RoccatVulcanIIMaxLayouts", 6);
            var lights = RoccatVulcanLayouts.Lights(layout);
            ushort UsageOf(string name) => lights[Array.FindIndex(layout.Leds, l => l.Name == name)].Usage;
            Assert.Equal(0x35, UsageOf("Key: E/J"));
            Assert.Equal(0x2E, UsageOf("Key: ^"));
            Assert.Equal(0x2F, UsageOf("Key: @"));
            Assert.Equal(0x34, UsageOf("Key: :"));
        }

        /// <summary>The Vulcan II Max's second LEDs light under their keys
        /// and its 16 palm rest LEDs are edge lighting
        /// (RoccatVulcanKeyboardLayouts.h:1016-1056).</summary>
        [Fact]
        public void VulcanIIMax_SecondLedsAndPalmRest()
        {
            var layout = RoccatVulcanLayouts.Find("RoccatVulcanIIMaxLayouts", 0);
            var map = new LightMap(RoccatVulcanLayouts.Lights(layout), "t");
            Assert.Equal(16, map.EdgeCount);
            // F1 (0x3A) and its LED 2 at 0x89.
            var f1 = map.IndexesOf(0x3A).Select(i => map.Lights[i].Address).ToList();
            Assert.Equal(2, f1.Count);
            Assert.Contains(0x89, f1);
        }

        /// <summary>The Magma's five LEDs are five zones
        /// (RoccatVulcanKeyboardLayouts.h:1774-1792).</summary>
        [Fact]
        public void Magma_IsFiveZones()
        {
            var map = RoccatVulcanLighting.MapOf("RoccatMagmaLayouts", 0, "p");
            Assert.Equal(5, map.ZoneCount);
            Assert.False(map.HasKeys);
        }

        // ── Vulcan reports ──

        /// <summary>EnableDirect (RoccatVulcanKeyboardController.cpp:166-195).</summary>
        [Fact]
        public void TheDirectSwitch_IsOpenRgbs()
        {
            Assert.Equal("15 00 01", Hex(RoccatVulcanLighting.DirectSwitch(0x3098, true)));
            Assert.Equal("15 00 00", Hex(RoccatVulcanLighting.DirectSwitch(0x3098, false)));
            Assert.Equal("15 00 01", Hex(RoccatVulcanLighting.DirectSwitch(0x5023, true)));
            Assert.Equal("0E 05 01 00 00", Hex(RoccatVulcanLighting.DirectSwitch(0x30F7, true)));
            Assert.Equal("0E 05 00 00 00", Hex(RoccatVulcanLighting.DirectSwitch(0x5001, false)));
            Assert.Equal("0E 05 01 00 00", Hex(RoccatVulcanLighting.DirectSwitch(0x69A0, true)));
            // Eruption sends the same switch on to a Vulcan 1xx
            // (roccat_vulcan_1xx.rs:179-180) and a Vulcan Pro (roccat_vulcan_pro.rs:239).
        }

        /// <summary>SendMode (lines 400-609) for a first-generation board:
        /// 0D, the length 443 in two bytes, 00, the mode, the speed 06,
        /// 00, the brightness 45, 00, zeros, and the sum. roccat-vulcan-api-rs
        /// opens its mode report the same way, 0D BB 01 (reports.rs FEATURE_REPORT_8).</summary>
        [Fact]
        public void TheModeReport_FirstGeneration()
        {
            var direct = RoccatVulcanLighting.ModeReport(0x3098, 0x0B);
            Assert.Equal(443, direct.Length);
            Assert.Equal("0D BB 01 00 0B 06 00 45 00", Hex(direct.Take(9)));
            Assert.All(direct.Skip(9).Take(432), b => Assert.Equal(0, b));
            // 0D + BB + 01 + 0B + 06 + 45 = 0x11F.
            Assert.Equal("1F 01", Hex(direct.Skip(441)));
        }

        [Fact]
        public void TheModeReport_SecondGeneration()
        {
            // Magma: 26 bytes, one length byte, 11 1A 00 02 06 45 00 00, sum 0x78.
            var magma = RoccatVulcanLighting.ModeReport(0x3124, 0x02);
            Assert.Equal(26, magma.Length);
            Assert.Equal("11 1A 00 02 06 45 00 00", Hex(magma.Take(8)));
            Assert.Equal("78 00", Hex(magma.Skip(24)));
            // Vulcan II Mini: 332 = 0x14C, its own 02 and FB, sum 0x1B1.
            var mini = RoccatVulcanLighting.ModeReport(0x2F09, 0x0B);
            Assert.Equal(332, mini.Length);
            Assert.Equal("11 4C 01 02 0B 06 45 FB 00", Hex(mini.Take(9)));
            Assert.Equal("B1 01", Hex(mini.Skip(330)));
            // Turtle Beach TKL Pro: 284 = 0x11C, its own 03, 0B and 01, sum 0x8A.
            var tkl = RoccatVulcanLighting.ModeReport(0x5001, 0x02);
            Assert.Equal(284, tkl.Length);
            Assert.Equal("11 1C 01 03 02 06 45 0B 01", Hex(tkl.Take(9)));
            Assert.Equal("8A 00", Hex(tkl.Skip(282)));
            // The other lengths (lines 409-453).
            Assert.Equal(365, RoccatVulcanLighting.ModeReport(0x314C, 2).Length);
            Assert.Equal(371, RoccatVulcanLighting.ModeReport(0x30F7, 2).Length);
            Assert.Equal(371, RoccatVulcanLighting.ModeReport(0x311A, 2).Length);
            Assert.Equal(377, RoccatVulcanLighting.ModeReport(0x2F4E, 2).Length);
            Assert.Equal(377, RoccatVulcanLighting.ModeReport(0x501B, 2).Length);
            Assert.Equal(542, RoccatVulcanLighting.ModeReport(0x2EE2, 2).Length);
            Assert.Equal(443, RoccatVulcanLighting.ModeReport(0x5023, 2).Length);
        }

        /// <summary>SendColors for a first-generation board, against
        /// Eruption's frame for the same keyboard (roccat_vulcan_1xx.rs:941-1000:
        /// A1 01 01 B4, then per key r at (key/12)*36 + key%12 + 4, g 12 on, b
        /// 24 on, cut into 64-byte packets behind a 0).</summary>
        [Fact]
        public void TheColorFrame_FirstGeneration()
        {
            var packets = RoccatVulcanLighting.ColorPackets(0x3098, true, new[] { (0, 0x112233), (13, 0x445566) });
            Assert.Equal(7, packets.Length);
            Assert.All(packets, p => Assert.Equal(65, p.Length));
            Assert.Equal("00 A1 01 01 B4", Hex(packets[0].Take(5)));
            Assert.Equal(0x11, packets[0][5]);
            Assert.Equal(0x22, packets[0][17]);
            Assert.Equal(0x33, packets[0][29]);
            // Key 13: (13/12)*36 + 1 + 4 = 41, so packet 0 byte 42; b at 65,
            // packet 1 byte 2.
            Assert.Equal(0x44, packets[0][42]);
            Assert.Equal(0x55, packets[0][54]);
            Assert.Equal(0x66, packets[1][2]);
            Assert.Equal(0, packets[1][1]);
            var little = RoccatVulcanLighting.ColorPackets(0x3098, false, Array.Empty<(int, int)>());
            Assert.Equal("00 A1 01 B4 01", Hex(little[0].Take(5)));
        }

        [Fact]
        public void TheColorFrame_SecondGeneration()
        {
            // Vulcan Pro: 384 bytes, 12 per column, every packet A1 and its
            // number, 60 data bytes after the first packet's 4-byte header.
            var pro = RoccatVulcanLighting.ColorPackets(0x30F7, true, new[] { (13, 0x445566) });
            Assert.Equal(6, pro.Length);
            Assert.Equal("00 A1 01 01 80", Hex(pro[0].Take(5)));
            Assert.Equal("00 A1 02", Hex(pro[1].Take(3)));
            Assert.Equal("00 A1 06", Hex(pro[5].Take(3)));
            Assert.Equal(0x44, pro[0][42]);
            Assert.Equal(0x55, pro[0][54]);
            Assert.Equal(0x66, pro[1][6]);
            // Vulcan II: one LED per column, so LED 5's red, green, blue at
            // 15, 16, 17 of the data.
            var two = RoccatVulcanLighting.ColorPackets(0x2F4E, true, new[] { (5, 0x0A0B0C) });
            Assert.Equal(7, two.Length);
            Assert.Equal("0A 0B 0C", Hex(two[0].Skip(20).Take(3)));
            // Vulcan II Mini: LED 17 sits at slot 18 (line 28), never
            // big-endian (line 159-160): 345 = 0x159.
            var mini = RoccatVulcanLighting.ColorPackets(0x2F09, RoccatVulcanLighting.BigEndian(0x2F09, 116), new[] { (17, 0x0A0B0C) });
            Assert.Equal("00 A1 01 59 01", Hex(mini[0].Take(5)));
            Assert.Equal("0A 0B 0C", Hex(mini[0].Skip(59).Take(3)));
            // Magma: 64 bytes, one length byte, five per column.
            var magma = RoccatVulcanLighting.ColorPackets(0x3124, true, new[] { (4, 0x0A0B0C) });
            Assert.Single(magma);
            Assert.Equal("00 A1 01 40", Hex(magma[0].Take(4)));
            Assert.Equal(0x0A, magma[0][8]);
            Assert.Equal(0x0B, magma[0][13]);
            Assert.Equal(0x0C, magma[0][18]);
        }

        /// <summary>The Turtle Beach TKL Pro holds every red, then every
        /// green, then every blue, as many of each as its highest LED value
        /// plus one (lines 295-345).</summary>
        [Fact]
        public void TheColorFrame_TurtleBeachTklPro()
        {
            var p = RoccatVulcanLighting.ColorPackets(0x5001, true, new[] { (0, 0x010203), (2, 0x0A0B0C) });
            Assert.Equal(5, p.Length);
            Assert.Equal("00 A1 01 01 40", Hex(p[0].Take(5)));
            Assert.Equal(0x01, p[0][5]);
            Assert.Equal(0x0A, p[0][7]);
            Assert.Equal(0x02, p[0][8]);
            Assert.Equal(0x0B, p[0][10]);
            Assert.Equal(0x03, p[0][11]);
            Assert.Equal(0x0C, p[0][13]);
        }

        /// <summary>IsBigEndianDirectMode (lines 155-164), its version
        /// compare included: 1.16 and 1.36 big, 1.05 and 2.05 little.</summary>
        [Fact]
        public void TheLengthsByteOrder_FollowsTheFirmware()
        {
            Assert.True(RoccatVulcanLighting.BigEndian(0x3098, 116));
            Assert.True(RoccatVulcanLighting.BigEndian(0x3098, 136));
            Assert.False(RoccatVulcanLighting.BigEndian(0x3098, 105));
            Assert.False(RoccatVulcanLighting.BigEndian(0x3098, 205));
            Assert.False(RoccatVulcanLighting.BigEndian(0x2F09, 136));
        }

        // ── Vulcan sessions ──

        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");

        private sealed class Keyboard
        {
            public readonly FakeReportChannel Control;
            public readonly FakeReportChannel Led = new() { Path = "led", OutputReportLength = 65, InputReportLength = 65 };
            public int ReadyAfter;

            public Keyboard(byte version = 136, byte layout = 0, byte infoId = 0x0F)
            {
                Control = new FakeReportChannel { Path = "ctrl" };
                int readies = 0;
                Control.Feature = id => id switch
                {
                    0x0F or 0x09 => new byte[] { infoId, 0x00, version, 0, 0, 0, layout, 0, 0 },
                    0x04 => new byte[] { 0x04, (byte)(readies++ >= ReadyAfter ? 1 : 0), 0, 0 },
                    _ => null,
                };
            }
        }

        private static (RoccatVulcanLighting Family, HidLightingModel Model) Vulcan(Keyboard keyboard, ushort pid = 0x3098,
            List<(string, byte[], int)> shots = null)
        {
            var family = new RoccatVulcanLighting(null, _ => Box,
                path => path == "ctrl" ? keyboard.Control : path == "led" ? keyboard.Led : null,
                (p, f, t) => { shots?.Add((p, f, t)); return true; });
            return (family, family.Model("Roccat Vulcan 120-Series Aimo", 0x1E7D, pid, "led"));
        }

        private static List<(string Kind, byte[] Bytes)> Calls(FakeReportChannel channel)
            => channel.Calls.Select(c => (c.Kind, c.Bytes)).ToList();

        [Fact]
        public void AVulcanSession_EntersDirectAsOpenRgbDoes_AndHandsBackTheDefaultMode()
        {
            var keyboard = new Keyboard(version: 136, layout: 2);
            var (_, model) = Vulcan(keyboard);
            var handle = model.Open("ctrl");
            var session = Assert.IsType<RoccatVulcanLighting.Session>(model.BeginOwn(handle, _ => { }));
            // InitDeviceInfo reads report 0F twice (lines 104-105).
            Assert.Equal(new[] { "get", "get" }, Calls(keyboard.Control).Select(c => c.Kind));
            Assert.Equal(105, session.Map.Lights.Count);
            Assert.True(session.BigEndian);
            Assert.Empty(session.CrashFrames);

            var lights = session.Map.Lights;
            var colors = Enumerable.Repeat(0x102030, lights.Count).ToArray();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(null, _ => { }, lights, colors));
            var control = Calls(keyboard.Control).Skip(2).ToList();
            // DeviceUpdateMode (lines 299-316 of the RGBController): mode,
            // ready, switch on, ready.
            Assert.Equal(new[] { "set", "get", "set", "get" }, control.Select(c => c.Kind));
            Assert.Equal(RoccatVulcanLighting.ModeReport(0x3098, 0x0B), control[0].Bytes);
            Assert.Equal("15 00 01", Hex(control[2].Bytes));
            var led = Calls(keyboard.Led);
            Assert.Equal(7, led.Count(c => c.Kind == "out"));
            Assert.Equal("in", led.Last().Kind);
            Assert.Equal(2, session.CrashFrames.Length);
            Assert.Equal(RoccatVulcanLighting.ModeReport(0x3098, 0x02), session.CrashFrames[0]);
            Assert.Equal("15 00 00", Hex(session.CrashFrames[1]));

            // The next picture writes the colors only.
            int before = keyboard.Control.Calls.Count;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(null, _ => { }, lights, colors));
            Assert.Equal(before, keyboard.Control.Calls.Count);

            Assert.True(session.HandBack(null, _ => { }));
            var back = Calls(keyboard.Control).Skip(before).ToList();
            Assert.Equal(new[] { "set", "get", "set", "get" }, back.Select(c => c.Kind));
            Assert.Equal("0D BB 01 00 02 06 00 45 00", Hex(back[0].Bytes.Take(9)));
            Assert.Equal("15 00 00", Hex(back[2].Bytes));
            Assert.Empty(session.CrashFrames);
        }

        /// <summary>WaitUntilReady polls 25 ms apart until byte 1 is 1
        /// (lines 633-641).</summary>
        [Fact]
        public void TheReadyWait_PollsUntilTheKeyboardIsReady()
        {
            var keyboard = new Keyboard { ReadyAfter = 3 };
            var waits = new List<int>();
            Assert.True(RoccatVulcanLighting.WaitUntilReady(keyboard.Control, waits.Add, 0x3098));
            Assert.Equal(new[] { 25, 25, 25 }, waits);
            Assert.Equal(4, keyboard.Control.Calls.Count);
            Assert.All(keyboard.Control.Calls, c => Assert.Equal(new byte[] { 0x04 }, c.Bytes));
        }

        [Fact]
        public void AJapaneseVulcanIIMax_LightsByItsJapaneseEntry()
        {
            var keyboard = new Keyboard(layout: 6, infoId: 0x09);
            var (_, model) = Vulcan(keyboard, 0x2EE2);
            var session = model.BeginOwn(model.Open("ctrl"), _ => { });
            Assert.Equal(151, session.Map.Lights.Count);
            Assert.Equal(new byte[] { 0x09 }, keyboard.Control.Calls.First().Bytes);
        }

        [Fact]
        public void AVulcanThatDoesNotAnswer_GetsNoSession()
        {
            var keyboard = new Keyboard();
            keyboard.Control.Feature = _ => null;
            var (_, model) = Vulcan(keyboard);
            Assert.Null(model.BeginOwn(model.Open("ctrl"), _ => { }));
            Assert.DoesNotContain(keyboard.Control.Calls, c => c.Kind == "set");
        }

        [Fact]
        public void AVulcanHeldByAnotherProgram_IsNotOpened()
        {
            var keyboard = new Keyboard();
            var held = new FakeReportChannel { Path = "led", FeatureOnly = true };
            var family = new RoccatVulcanLighting(null, _ => Box, p => p == "ctrl" ? keyboard.Control : held, (_, _, _) => true);
            var model = family.Model("x", 0x1E7D, 0x3098, "led");
            Assert.Null(model.Open("ctrl"));
            Assert.True(keyboard.Control.Disposed);
            Assert.True(held.Disposed);
        }

        /// <summary>The crash path writes the hand-back's two reports to the
        /// control collection.</summary>
        [Fact]
        public void TheVulcanCrashPath_WritesToTheControlCollection()
        {
            var shots = new List<(string, byte[], int)>();
            var (_, model) = Vulcan(new Keyboard(), shots: shots);
            Assert.True(model.WriteOnce("ctrl", new byte[] { 0x15, 0x00, 0x00 }, 200));
            Assert.Equal("ctrl", Assert.Single(shots).Item1);
        }

        /// <summary>The look pairs a control collection with the LED
        /// collection in its container and leaves a keyboard whose LED
        /// collection is missing or elsewhere.</summary>
        [Fact]
        public void TheLook_PairsByContainer()
        {
            HidCollectionInfo C(string path, int iface, ushort page)
                => new() { Path = path, VendorId = 0x1E7D, ProductId = 0x30F7, InterfaceNumber = iface, UsagePage = page, Usage = 1 };
            var a = new Guid("00000000-0000-0000-0000-0000000000a1");
            var b = new Guid("00000000-0000-0000-0000-0000000000b2");
            var walk = new[]
            {
                C("ctrl-a", 1, 0xFF01), C("ctrl-b", 1, 0xFF01), C("led-b", 3, 0xFF00), C("led-a", 3, 0xFF00),
                C("other", 3, 0x0001),
            };
            var containers = new Dictionary<string, Guid>
            {
                ["ctrl-a"] = a, ["led-a"] = a, ["ctrl-b"] = b, ["led-b"] = b, ["other"] = a,
            };
            var family = new RoccatVulcanLighting(() => walk, p => containers[p], _ => null, (_, _, _) => true);
            var found = family.Look();
            Assert.Equal(new[] { "ctrl-a", "ctrl-b" }, found.Select(d => d.Key));
            Assert.Equal(new[] { a, b }, found.Select(d => d.Container));
            // The same model at the next look.
            Assert.Same(found[0].Model, family.Look()[0].Model);

            var lonely = new RoccatVulcanLighting(() => walk.Take(2).ToArray(), p => containers[p], _ => null, (_, _, _) => true);
            Assert.Empty(lonely.Look());
            var unknown = new RoccatVulcanLighting(() => walk, _ => Guid.Empty, _ => null, (_, _, _) => true);
            Assert.Empty(unknown.Look());
            Assert.Null(new RoccatVulcanLighting(() => null, _ => a, _ => null, (_, _, _) => true).Look());
        }

        // ── Mouse, pad and headset reports ──

        [Fact]
        public void TheControlReport_IsOpenRgbs()
        {
            // SwitchControl (RoccatBurstController.cpp:58-70), the same bytes
            // Eruption sends on (roccat_burst_pro.rs:202).
            Assert.Equal("0E 06 01 01 00 FF", Hex(RoccatLighting.Control(true)));
            Assert.Equal("0E 06 01 00 00 FF", Hex(RoccatLighting.Control(false)));
            // SignalRGB's Kone hand-back (Roccat_Kone_Mouse.js:202).
            Assert.Equal("0E 06 00 00 00 FF", Hex(RoccatLighting.KoneAimoRelease));
        }

        [Fact]
        public void TheMouseColorReports_AreOpenRgbs()
        {
            // RoccatBurstController.cpp:72-89.
            Assert.Equal("0D 0B 11 22 33 44 55 66 00 00 00", Hex(RoccatLighting.SmallDirect(new[] { 0x112233, 0x445566 }, 2)));
            Assert.Equal("0D 0B 11 22 33 00 00 00 00 00 00", Hex(RoccatLighting.SmallDirect(new[] { 0x112233 }, 1)));

            // RoccatKoneXPController.cpp:259-290.
            var xp = RoccatLighting.KoneXpDirect(Enumerable.Range(1, 20).Select(i => i << 16 | i << 8 | i).ToArray());
            Assert.Equal(122, xp.Length);
            Assert.Equal("0D 7A 01 01 01 FF FF FF 02 02 02 FF FF FF", Hex(xp.Take(14)));
            Assert.Equal("14 14 14 FF FF FF", Hex(xp.Skip(116)));

            // RoccatKoneAimoController.cpp:85-107.
            var aimo = RoccatLighting.KoneAimoDirect(Enumerable.Range(1, 11).Select(i => i << 16 | i << 8 | i).ToArray());
            Assert.Equal(46, aimo.Length);
            Assert.Equal("0D 2E 01 01 01 00 02 02 02 00", Hex(aimo.Take(10)));
            Assert.Equal("0B 0B 0B 00", Hex(aimo.Skip(42)));

            // RoccatBurstProAirController.cpp:51-94.
            Assert.Equal("06 01 4C 06 06 01 00 FF 0F" + string.Concat(Enumerable.Repeat(" 00", 21)), Hex(RoccatLighting.BurstProAirMode()));
            var air = RoccatLighting.BurstProAirColors(new[] { 0x010203, 0x040506, 0x070809, 0x0A0B0C });
            Assert.Equal("06 01 4C 06 14 01 FF 01 02 03 02 FF 04 05 06 03 FF 07 08 09 04 FF 0A 0B 0C 00 00 00 00 00", Hex(air));

            // RoccatKoneProAirController.cpp:96-119.
            Assert.Equal("00 10 10 0B 00 09 64 64 64 0B 01 02 03 04 05 06",
                Hex(RoccatLighting.KoneProAirDirect(new[] { 0x010203, 0x040506 })));
        }

        [Fact]
        public void TheEloReports_AreOpenRgbs()
        {
            // RoccatEloController.cpp:54-97.
            var init = RoccatLighting.EloInit();
            Assert.Equal(new[]
            {
                "FF 01 00 00", "FF 02 00 00", "FF 03 00 01", "FF 04 00 00", "FF 01 00 00",
            }, init.Select(r => Hex(r.Take(4))));
            Assert.All(init, r => Assert.Equal(16, r.Length));
            Assert.Equal("FF 04 00 00 0A 0B 0C 00", Hex(RoccatLighting.EloReport(0x04, rgb: 0x0A0B0C).Take(8)));
        }

        [Fact]
        public void TheHordeReports_AreOpenRgbs()
        {
            // RoccatHordeAimoController.cpp:30-92: 18 18 FF FF, six colors,
            // and the sum 18 + 18 + FF + FF + 6 * (1 + 2 + 3) = 0x252.
            Assert.Equal("13 08 01 00 00 00 00 00", Hex(RoccatLighting.HordeInit()));
            var colors = RoccatLighting.HordeColors(Enumerable.Repeat(0x010203, 6).ToArray());
            Assert.Equal("18 18 FF FF" + string.Concat(Enumerable.Repeat(" 01 02 03", 6)) + " 52 02", Hex(colors));
        }

        /// <summary>SetColor of the Kova (RoccatKovaController.cpp:60-109):
        /// the flags 14 |= 30 | 03, color flow off, mode 01, speed 0, the
        /// stripe at 18 and the wheel at 22, the sum of bytes 0 to 25:
        /// 06 + 1C + 05 + 73 + 01 + 40 + 50 + 60 + 10 + 20 + 30 = 0x1EB.</summary>
        [Fact]
        public void TheKovaProfile_IsOpenRgbs()
        {
            var read = new byte[28];
            read[0] = 0x06;
            read[1] = 0x1C;
            read[2] = 0x05;
            read[14] = 0x40;
            read[15] = 0x01;
            read[16] = 0x03;
            read[17] = 0x02;
            var profile = RoccatLighting.KovaProfile(read, 0x102030, 0x405060);
            Assert.Equal("06 1C 05 00 00 00 00 00 00 00 00 00 00 00 73 00 01 00 00 40 50 60 00 10 20 30 EB 01", Hex(profile));
            Assert.Equal("04 00 80", Hex(RoccatLighting.KovaInit()));
        }

        /// <summary>SetMode and SendDirect of the Sense Aimo
        /// (RoccatSenseAimoController.cpp:101-151).</summary>
        [Fact]
        public void TheSenseAimoReports_AreOpenRgbs()
        {
            Assert.Equal("02 02 0B 00 00 00 0A 0B 0C FF 0B 00 00 00 0D 0E 0F FF 00",
                Hex(RoccatLighting.SenseAimoMode(2, new[] { 0x0A0B0C, 0x0D0E0F })));
            Assert.Equal("03 0A 0B 0C FF 0D 0E 0F FF", Hex(RoccatLighting.SenseAimoDirect(new[] { 0x0A0B0C, 0x0D0E0F })));
        }

        // ── Mouse sessions ──

        private static HidLightingModel ModelNamed(string name)
            => RoccatLighting.Instance.Match(CollectionOf(OpenRgbRegistrations.Of("RoccatController").Single(r => r.Name == name)));

        [Fact]
        public void ABurstSession_TakesControl_AndGivesItBack()
        {
            var io = new FakeReportChannel { FeatureReportLength = 11 };
            var model = ModelNamed("Roccat Burst Pro");
            var session = model.Begin(io, _ => { });
            Assert.Empty(io.Calls);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x112233, 0x445566 }));
            Assert.Equal(new[] { "0E 06 01 01 00 FF", "0D 0B 11 22 33 44 55 66 00 00 00" }, io.Calls.Select(c => Hex(c.Bytes)));
            Assert.Equal("0E 06 01 00 00 FF", Hex(Assert.Single(session.CrashFrames)));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal("0E 06 01 00 00 FF", Hex(io.Calls.Last().Bytes));
            Assert.Empty(session.CrashFrames);
        }

        [Fact]
        public void AKoneXpSession_WaitsForTheMouse()
        {
            int reads = 0;
            var io = new FakeReportChannel { Feature = id => id == 0x04 ? new byte[] { 0x04, (byte)(reads++ >= 1 ? 1 : 0), 0, 0 } : null };
            var model = ModelNamed("Roccat Kone XP");
            var waits = new List<int>();
            var session = model.Begin(io, waits.Add);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, waits.Add, model.Map.Lights, new int[20]));
            Assert.Equal(new[] { "set", "get", "get", "set" }, io.Calls.Select(c => c.Kind));
            Assert.Equal(new[] { 200 }, waits);
            Assert.True(session.HandBack(io, waits.Add));
            Assert.Equal("0E 06 01 00 00 FF", Hex(io.Calls.Reverse().Skip(1).First().Bytes));
        }

        [Fact]
        public void AKoneAimoSession_ReadsAsSwarmDoes_AndGivesTheMouseBack()
        {
            var io = new FakeReportChannel();
            var model = ModelNamed("Roccat Kone Aimo 16K");
            var session = model.Begin(io, _ => { });
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new int[11]));
            var calls = io.Calls.ToList();
            Assert.Equal(new[] { "get", "set", "set", "get" }, calls.Select(c => c.Kind));
            Assert.Equal(0x00, calls[0].Bytes[0]);
            Assert.Equal(0x0D, calls[3].Bytes[0]);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal("0E 06 00 00 00 FF", Hex(io.Calls.Last().Bytes));
        }

        [Fact]
        public void DevicesNoReferenceGivesBack_SendNothingAtTheHandBack()
        {
            foreach (var name in new[] { "Roccat Burst Pro Air", "Roccat Kone Pro Air", "Roccat Elo 7.1", "Roccat Horde Aimo" })
            {
                var io = new FakeReportChannel { OutputReportLength = 16 };
                var model = ModelNamed(name);
                var session = model.Begin(io, _ => { });
                Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new int[model.Map.Lights.Count]));
                int count = io.Calls.Count;
                Assert.Empty(session.CrashFrames);
                Assert.True(session.HandBack(io, _ => { }));
                Assert.Equal(count, io.Calls.Count);
            }
        }

        [Fact]
        public void AnEloSession_StartsAsOpenRgbDoes()
        {
            var io = new FakeReportChannel { OutputReportLength = 16 };
            var model = ModelNamed("Roccat Elo 7.1");
            var session = model.Begin(io, _ => { });
            Assert.False(session.Feature);
            session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x0A0B0C });
            Assert.Equal(new[] { "FF 01", "FF 02", "FF 03", "FF 04", "FF 01", "FF 04" }, io.Calls.Select(c => Hex(c.Bytes.Take(2))));
            Assert.Equal("FF 04 00 00 0A 0B 0C", Hex(io.Calls.Last().Bytes.Take(7)));
        }

        // ── Stored settings ──

        private static byte[] KovaRead()
        {
            var read = new byte[28];
            read[0] = 0x06;
            read[1] = 0x1C;
            read[14] = 0x41;
            read[16] = 0x02;
            read[26] = 0x6B;
            return read;
        }

        [Fact]
        public void AKovaSession_SavesTheProfile_BeforeItWrites_AndPutsItBack()
        {
            const string product = "roccat:1E7D:2CEE";
            var stored = KovaRead();
            var io = new FakeReportChannel { Feature = id => id == 0x06 ? stored : id == 0x09 ? new byte[8] : null };
            var model = ModelNamed("Roccat Kova");
            Assert.Equal(RoccatLighting.StoredHoldMs, model.HoldMs);
            Assert.Equal(RoccatLighting.StoredRepaintMs, model.RepaintMs);
            var session = Assert.IsType<RoccatLighting.StoredSession>(model.Begin(io, _ => { }));
            // OpenRGB's constructor: 04 00 80, the version, then the profile.
            Assert.Equal(new[] { "set", "get", "get" }, io.Calls.Select(c => c.Kind));
            Assert.Equal("04 00 80", Hex(io.Calls.First().Bytes));
            Assert.Null(KeyboardProfileBackups.Get(product));

            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x102030, 0x405060 }));
            var backup = Assert.IsType<KeyboardProfileBackups.Backup>(KeyboardProfileBackups.Get(product));
            Assert.Equal(stored, backup.Contents);
            Assert.Equal(RoccatLighting.KovaProfile(stored, 0x102030, 0x405060), io.Calls.Last().Bytes);
            Assert.Equal(stored, Assert.Single(session.CrashFrames));

            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(stored, io.Calls.Last().Bytes);
            Assert.Null(KeyboardProfileBackups.Get(product));
        }

        [Fact]
        public void AKovaPicture_WaitsForItsBackupToBeSaved()
        {
            KeyboardProfileBackups.ResetForTest();
            var stored = KovaRead();
            var io = new FakeReportChannel { Feature = id => id == 0x06 ? stored : new byte[8] };
            var model = ModelNamed("Roccat Kova");
            var session = model.Begin(io, _ => { });
            int before = io.Calls.Count;
            Assert.Equal(HidLightingPaint.Later, session.Paint(io, _ => { }, model.Map.Lights, new[] { 1, 2 }));
            Assert.Equal(RoccatLighting.SavingMs, session.LaterMs);
            Assert.Equal(before, io.Calls.Count);
            Assert.Empty(session.CrashFrames);
        }

        /// <summary>A profile an earlier run left is put back with no claim,
        /// and a session that opens over it puts back the saved one.</summary>
        [Fact]
        public void WhatAKilledRunLeft_IsPutBack()
        {
            const string product = "roccat:1E7D:2CEE";
            var original = KovaRead();
            KeyboardProfileBackups.Take(product, 0, original);
            var model = ModelNamed("Roccat Kova");
            Assert.True(model.Owed());

            var ours = RoccatLighting.KovaProfile(original, 0x112233, 0x445566);
            var io = new FakeReportChannel { Feature = id => id == 0x06 ? ours : new byte[8] };
            var session = Assert.IsType<RoccatLighting.StoredSession>(model.Begin(io, _ => { }));
            Assert.Equal(original, session.Original);

            Assert.True(model.Repair(io, _ => { }));
            Assert.Equal(original, io.Calls.Last().Bytes);
            Assert.False(model.Owed());

            KeyboardProfileBackups.Take(product, 0, original);
            var broken = new FakeReportChannel { FailWrites = true };
            Assert.False(model.Repair(broken, _ => { }));
            Assert.Equal(1, KeyboardProfileBackups.Failures(product));
        }

        [Fact]
        public void ASenseAimoSession_SetsDirectOnce_AndPutsTheModeBack()
        {
            const string product = "roccat:1E7D:343A";
            var stored = new byte[] { 0x02, 0x03, 0x0A, 0x05, 0xFF, 0x00, 0x10, 0x20, 0x30, 0xFF, 0x0A, 0x05, 0xFF, 0x00, 0x40, 0x50, 0x60, 0xFF, 0x80 };
            var io = new FakeReportChannel { Feature = id => id == 0x02 ? stored : null };
            var model = ModelNamed("Roccat Sense Aimo Mid");
            var session = model.Begin(io, _ => { });
            Assert.Equal(new[] { "get" }, io.Calls.Select(c => c.Kind));
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x0A0B0C, 0x0D0E0F }));
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x0A0B0C, 0x0D0E0F }));
            var sets = io.Calls.Where(c => c.Kind == "set").Select(c => Hex(c.Bytes)).ToList();
            // The profile 03 the pad answered goes into the mode report.
            Assert.Equal(new[]
            {
                "02 03 0B 00 00 00 0A 0B 0C FF 0B 00 00 00 0D 0E 0F FF 00",
                "03 0A 0B 0C FF 0D 0E 0F FF",
                "03 0A 0B 0C FF 0D 0E 0F FF",
            }, sets);
            Assert.Equal(stored, KeyboardProfileBackups.Get(product)?.Contents);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(stored, io.Calls.Last().Bytes);
            Assert.Null(KeyboardProfileBackups.Get(product));
        }

        [Fact]
        public void ASessionWhoseWriteFails_ReportsIt()
        {
            var io = new FakeReportChannel { FailWrites = true };
            var model = ModelNamed("Roccat Kone Pro");
            var session = model.Begin(io, _ => { });
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, _ => { }, model.Map.Lights, new int[2]));
            // The switch may have landed, so its undoing is on record.
            Assert.Single(session.CrashFrames);
            Assert.False(session.HandBack(io, _ => { }));
        }
    }
}
