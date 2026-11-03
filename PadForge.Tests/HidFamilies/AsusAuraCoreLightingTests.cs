using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.AsusAuraUsbLightingTests;

namespace PadForge.Tests
{
    /// <summary>The Aura Core family never reads the PC's mainboard name in
    /// the test host (#557).</summary>
    internal static class AsusAuraCoreOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => AsusAuraCoreLighting.OffLimitsForTest = true;
    }

    /// <summary>The ASUS Aura Core family against OpenRGB's
    /// AsusAuraCoreController at the pinned commit (#557).</summary>
    public class AsusAuraCoreLightingTests
    {
        private static List<byte[]> Sets(FakeReportChannel io) => io.Calls.Where(c => c.Kind == "set").Select(c => c.Bytes).ToList();

        private static List<OpenRgbRegistration> Rows() => OpenRgbRegistrations.Of(AsusAuraCoreLighting.OpenRgbFamily).ToList();

        [Fact]
        public void TheFamily_IsOnTheWorkersList()
            => Assert.Contains(AsusAuraCoreLighting.Instance, HidLightingFamilies.All);

        [Fact]
        public void TheMainboardRead_IsFencedInTheTestHost()
        {
            Assert.True(AsusAuraCoreLighting.OffLimitsForTest);
            Assert.Null(AsusAuraCoreLighting.MainboardName());
        }

        /// <summary>Every registration is matched on a collection with
        /// feature reports, the laptops' by a listed mainboard name, and none
        /// without feature reports (IdentifyDevice, AsusAuraCoreController.cpp:290-344).</summary>
        [Fact]
        public void EveryRegistration_IsMatched()
        {
            var family = new AsusAuraCoreLighting(() => "G533ZM");
            var rows = Rows();
            Assert.Equal(5, rows.Count);
            foreach (var r in rows)
            {
                var model = family.Match(CollectionOf(r));
                Assert.True(model != null, r.Name);
                Assert.Equal(OpenRgbRegistrations.Match(AsusAuraCoreLighting.OpenRgbFamily, CollectionOf(r))[0].Name, model.Name);
                Assert.Equal(new[] { VendorPrograms.ArmouryCrate }, model.Programs);
                Assert.Null(family.Match(CollectionOf(r, feature: 0)));
            }
            var scar15 = rows.Single(r => r.Name == "ASUS ROG Strix SCAR 15");
            Assert.Null(new AsusAuraCoreLighting(() => "SOME-BOARD").Match(CollectionOf(scar15)));
            Assert.Null(new AsusAuraCoreLighting(() => null).Match(CollectionOf(scar15)));
            var laptop = family.Match(CollectionOf(scar15));
            Assert.Equal(0, laptop.HoldMs);
            Assert.Equal(HidppBackend.PaintMs, laptop.RepaintMs);
            var core = family.Match(CollectionOf(rows.First(r => r.ProductId == 0x1854)));
            Assert.Equal(LenovoLegionLighting.HoldMs, core.HoldMs);
            Assert.Equal(LenovoLegionLighting.RepaintMs, core.RepaintMs);
            Assert.Equal(4, core.Map.Lights.Count);
        }

        /// <summary>The board name is read once per family.</summary>
        [Fact]
        public void TheBoard_IsReadOnce()
        {
            int reads = 0;
            var family = new AsusAuraCoreLighting(() => { reads++; return "G533ZM"; });
            var scar = Rows().Single(r => r.Name == "ASUS ROG Strix SCAR 15");
            family.Match(CollectionOf(scar));
            family.Match(CollectionOf(scar));
            Assert.Equal(1, reads);
        }

        /// <summary>The four-zone keyboard per zone: B3, the zone from 1, mode
        /// 0, the color, speed FF and direction 0, then B5 and B4, seventeen
        /// bytes each (AsusAuraCoreController.cpp:86-177,
        /// RGBController_AsusAuraCore.cpp:261-309).</summary>
        [Fact]
        public void TheFourZoneKeyboard_IsStaticPerZone()
        {
            var frames = AsusAuraCoreLighting.KeyboardFrames(new[] { 0x112233, 0x445566, 0x778899, 0xAABBCC });
            Assert.Equal(12, frames.Count);
            Starts("5D B3 01 00 11 22 33 FF", frames[0], 17);
            Starts("5D B5", frames[1], 17);
            Starts("5D B4", frames[2], 17);
            Starts("5D B3 04 00 AA BB CC FF", frames[9], 17);
            var io = Channel();
            io.Feature = id => id == 0x5D ? new byte[] { 0x5D, 1 } : null;
            var session = AsusAuraCoreLighting.BeginCore(io, _ => { }, 0x1854, null);
            Assert.IsType<AsusAuraCoreLighting.KeyboardSession>(session);
            Assert.Empty(Sets(io));
            Assert.Equal(4, session.Map.Lights.Count);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Empty(session.CrashFrames);
        }

        /// <summary>The GA15DH: 5D unanswered and 5E answered, then the handshake
        /// with a read after each (AsusAuraCoreController.cpp:346-410), then
        /// InitDirectMode (lines 179-224), and BC D0 frames of sixteen LEDs
        /// (lines 226-288).</summary>
        [Fact]
        public void TheGa15dh_IsHandshakenAndLitDirect()
        {
            var io = Channel();
            io.Feature = id => id == 0x5E ? new byte[] { 0x5E } : null;
            var session = AsusAuraCoreLighting.BeginCore(io, _ => { }, 0x1866, null);
            Assert.IsType<AsusAuraCoreLighting.Ga15dhSession>(session);
            var sets = Sets(io);
            Assert.Equal(5, sets.Count);
            Starts("5E 41 53 55 53 20 54 65 63 68 2E 49 6E 63 2E", sets[0], 64);
            Starts("5E 05 20 31 00 10", sets[1], 64);
            Starts("5E BC D0", sets[2], 64);
            Starts("5E BC D0 01 02 00 00 10", sets[3], 64);
            Starts("5E BC D0 01 02 00 01 04", sets[4], 64);
            Assert.Equal(20, session.Map.Lights.Count);
            var frames = AsusAuraCoreLighting.Ga15dhFrames(Enumerable.Range(1, 20).ToList());
            Assert.Equal(2, frames.Count);
            Assert.Equal("5E BC D0 01 02 00 00 10 00 00 00 01 00 00 02", Hex(frames[0].Take(15).ToArray()));
            Starts("5E BC D0 01 02 00 01 04 00 00 00 11 00 00 12 00 00 13 00 00 14", frames[1], 64);
            Assert.Null(AsusAuraCoreLighting.BeginCore(Channel(), _ => { }, 0x1854, null));
        }

        /// <summary>The laptop's opening: BC 00, the layout read, BA C5 C4 03
        /// and BC 00 again (AsusAuraCoreLaptopController.cpp:71, 99-139,
        /// 183-209 and 304-313), and ISO where byte 12 of the answer is 2.</summary>
        [Fact]
        public void ALaptop_ReadsItsLayout_AndOpensDirect()
        {
            var rows = AsusAuraCoreLighting.LaptopRows("G533ZM");
            var io = Channel();
            var iso = new byte[64];
            iso[0] = 0x5D;
            iso[12] = 2;
            io.Feature = id => id == 0x5D ? iso : null;
            var session = AsusAuraCoreLighting.BeginLaptop(io, rows, 0x19B6);
            var sets = Sets(io);
            Assert.Equal(4, sets.Count);
            Starts("5D BC", sets[0], 64);
            Starts("5D 05 20 31 00 10", sets[1], 64);
            Starts("5D BA C5 C4 03", sets[2], 64);
            Starts("5D BC", sets[3], 64);
            var isoMap = AsusAuraCoreLighting.LaptopMap(0x19B6, rows, KeyboardLayout.IsoQwerty, out _);
            Assert.Equal(isoMap.Lights.Count, session.Map.Lights.Count);
            Assert.EndsWith(":" + KeyboardLayout.IsoQwerty, session.Map.Product);
            // The same laptop reached through 1866, which answers neither
            // Aura Core report.
            io = Channel();
            Assert.IsType<AsusAuraCoreLighting.LaptopSession>(AsusAuraCoreLighting.BeginCore(io, _ => { }, 0x1866, rows));
        }

        /// <summary>The GU603VV's one zone is one light, BC 01, and its
        /// picture BC 01 01 with the color at byte 9
        /// (AsusAuraCoreLaptopController.cpp:199-204 and 225-236).</summary>
        [Fact]
        public void AOneZoneLaptop_TakesOneLight()
        {
            var rows = AsusAuraCoreLighting.LaptopRows("GU603VV");
            Assert.True(AsusAuraCoreLighting.SingleZone(rows));
            Assert.False(AsusAuraCoreLighting.SingleZone(AsusAuraCoreLighting.LaptopRows("G533ZM")));
            Starts("5D BC 01", AsusAuraCoreLighting.LaptopOpening(true)[0], 64);
            var map = AsusAuraCoreLighting.LaptopMap(0x19B6, rows, KeyboardLayout.AnsiQwerty, out int length);
            Assert.Equal(1, length);
            var light = Assert.Single(map.Lights);
            Assert.Equal(LightKind.Zone, light.Kind);
            Starts("5D BC 01 01 00 00 00 00 00 12 34 56",
                Assert.Single(AsusAuraCoreLighting.LaptopFrames(map.Lights, new[] { 0x123456 }, length)), 64);
        }

        /// <summary>OpenRGB's buffer counts one past the highest LED value
        /// for each zone with a layout, zone by zone
        /// (RGBController_AsusAuraCoreLaptop.cpp:356-371): the G533ZW's lid
        /// reaches 177 and its lightbar no higher, so 179.</summary>
        [Fact]
        public void TheBuffer_IsOpenRgbs()
        {
            var rows = AsusAuraCoreLighting.LaptopRows("G533ZW");
            Assert.Equal(new[] { "Keyboard", "Lid", "Lightbar" }, rows.Select(r => r.Zone));
            var map = AsusAuraCoreLighting.LaptopMap(0x19B6, rows, KeyboardLayout.AnsiQwerty, out int length);
            Assert.Equal(179, length);
            Assert.Equal(LightKind.Logo, map.Lights.Single(l => l.Address == 167).Kind);
            Assert.Equal(LightKind.Edge, map.Lights.Single(l => l.Address == 174).Kind);
            Assert.True(map.KeyCount > 60);
        }

        /// <summary>SetLedsDirect (AsusAuraCoreLaptopController.cpp:238-301):
        /// BC 00 01 01 01, the offset and sixteen colors per frame up to 167,
        /// the last of seven, then 04 with the LEDs past 167.</summary>
        [Fact]
        public void LaptopFrames_AreSetLedsDirect()
        {
            var lights = Enumerable.Range(0, 170).Select(i => new LightInfo(i, LightKind.Key, 4, LightKeySource.Generated)).ToList();
            var colors = Enumerable.Range(0, 170).Select(i => i).ToList();
            var frames = AsusAuraCoreLighting.LaptopFrames(lights, colors, 170);
            Assert.Equal(12, frames.Count);
            Assert.Equal("5D BC 00 01 01 01 00 10 00 00 00 00 00 00 01", Hex(frames[0].Take(15).ToArray()));
            Assert.Equal("5D BC 00 01 01 01 10 10 00 00 00 10", Hex(frames[1].Take(12).ToArray()));
            Starts("5D BC 00 01 01 01 A0 07 00 00 00 A0 00 00 A1 00 00 A2 00 00 A3 00 00 A4 00 00 A5 00 00 A6", frames[10], 64);
            Starts("5D BC 00 01 04 00 00 00 00 00 00 A7 00 00 A8 00 00 A9", frames[11], 64);
        }
    }
}
