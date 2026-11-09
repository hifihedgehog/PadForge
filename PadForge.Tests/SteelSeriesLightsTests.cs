using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// SteelSeries devices light by light (#557): each family's lights and
    /// the frames of a color per light, byte for byte. The expected frames
    /// are written out from the references, never from the builder under
    /// test. The keys of a per-key Apex board follow its SKU, as OpenRGB's
    /// SteelSeriesApexRegions.h patches them.
    /// </summary>
    public class SteelSeriesLightsTests
    {
        private static SteelSeriesFamilyInfo Info(SteelSeriesFamily family) => SteelSeriesDirectProtocol.Info(family);

        /// <summary>A color of its own for each light: 1n 2n 3n for light n.</summary>
        private static int[] Distinct(int count)
            => Enumerable.Range(0, count).Select(i => (0x10 + i) << 16 | (0x20 + i) << 8 | (0x30 + i)).ToArray();

        private static string C(int i) => $"{0x10 + i:X2} {0x20 + i:X2} {0x30 + i:X2}";

        private static string Text(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');

        private static string[] Paint(SteelSeriesFamily family, bool gen3 = false)
        {
            var info = Info(family);
            var colors = Distinct(info.Lights.Count);
            return info.Paint(info.Report, gen3, info.Lights, colors, 0x000000).Select(f => Text(f.Buffer)).ToArray();
        }

        // ── The mice ──

        /// <summary>rivalcfg devices/rival300.py:105-156: 08 01 the logo and
        /// 08 02 the wheel, each with its own color.</summary>
        [Fact]
        public void ARival300_TakesItsLogoAndWheelColors()
        {
            var info = Info(SteelSeriesFamily.Rival300);
            Assert.Equal(new[] { LightKind.Logo, LightKind.Zone }, info.Lights.Select(l => l.Kind));
            Assert.Equal(new[] { "00 08 01 " + C(0), "00 08 02 " + C(1) }, Paint(SteelSeriesFamily.Rival300));
        }

        /// <summary>The Engine's 0A 00 0F frame (OpenRGB issue 1167): the
        /// top, middle and bottom LEDs of the strip, then the logo
        /// (rivalcfg devices/rival3.py:57-96).</summary>
        [Fact]
        public void ARival3_TakesItsFourZonesInOneReport()
        {
            var info = Info(SteelSeriesFamily.Rival3);
            Assert.Equal(new[] { LightKind.Zone, LightKind.Zone, LightKind.Zone, LightKind.Logo }, info.Lights.Select(l => l.Kind));
            Assert.Equal(new[] { 1, 2, 3, 0 }, info.Lights.Select(l => l.Zone));
            Assert.Equal(new[] { $"00 0A 00 0F {C(0)} {C(1)} {C(2)} {C(3)}" }, Paint(SteelSeriesFamily.Rival3));
        }

        /// <summary>The 5B cycle of rival310-re 5B.md:6-19: the LED in d[2]
        /// and the color from d[28] and again from d[31], the logo 00 and the
        /// wheel 01.</summary>
        [Fact]
        public void ARival310_TakesAColorPerLed()
        {
            var info = Info(SteelSeriesFamily.Rival310);
            var frames = info.Paint(info.Report, false, info.Lights, Distinct(2), 0);
            Assert.Equal(new[] { LightKind.Logo, LightKind.Zone }, info.Lights.Select(l => l.Kind));
            for (int led = 0; led < 2; led++)
            {
                var data = frames[led].Buffer.AsSpan(1);
                Assert.Equal(0x5B, data[0]);
                Assert.Equal(led, data[2]);
                Assert.Equal(C(led), Text(data.Slice(28, 3).ToArray()));
                Assert.Equal(C(led), Text(data.Slice(31, 3).ToArray()));
            }
        }

        /// <summary>The Engine's 05 cycle per LED (OpenRGB issue 833): the LED
        /// in d[2] and d[7], the color from d[30] and as the last point from
        /// d[45]. The wheel, the logo, then six LEDs of the side strips
        /// (rivalcfg devices/rival600.py:76-163).</summary>
        [Fact]
        public void ARival600_TakesAColorPerLed()
        {
            var info = Info(SteelSeriesFamily.Rival600);
            Assert.Equal(new[] { LightKind.Zone, LightKind.Logo }.Concat(Enumerable.Repeat(LightKind.Edge, 6)),
                info.Lights.Select(l => l.Kind));
            var frames = info.Paint(info.Report, false, info.Lights, Distinct(8), 0);
            Assert.Equal(8, frames.Length);
            for (int led = 0; led < 8; led++)
            {
                var data = frames[led].Buffer.AsSpan(1);
                Assert.Equal(0x05, data[0]);
                Assert.Equal(led, data[2]);
                Assert.Equal(led, data[7]);
                Assert.Equal(C(led), Text(data.Slice(30, 3).ToArray()));
                Assert.Equal(C(led), Text(data.Slice(45, 3).ToArray()));
            }
        }

        /// <summary>rice src/command/commands.rs:22-38: 05 00 LL, the color,
        /// FF 32 C8 C8 00, the LED again and 01, the logo 00 and the wheel
        /// 01.</summary>
        [Fact]
        public void ARival700_TakesItsLogoAndWheelColors()
        {
            Assert.Equal(new[]
            {
                $"00 05 00 00 {C(0)} FF 32 C8 C8 00 00 01",
                $"00 05 00 01 {C(1)} FF 32 C8 C8 00 01 01",
            }, Paint(SteelSeriesFamily.Rival700));
        }

        /// <summary>signal-plugins Steelseries_Rival_650_Mouse.js:84-131: each
        /// LED's program with its color from d[31] and as its last point,
        /// then the bind 05 00 1L FF 00 00 00 00 5C.</summary>
        [Fact]
        public void ARival650_TakesAColorPerLed()
        {
            var info = Info(SteelSeriesFamily.Rival650);
            var frames = info.Paint(info.Report, false, info.Lights, Distinct(8), 0);
            Assert.Equal(16, frames.Length);
            for (int led = 0; led < 8; led++)
            {
                var program = frames[led * 2].Buffer.AsSpan(1);
                Assert.Equal(0x03, program[0]);
                Assert.Equal(C(led), Text(program.Slice(31, 3).ToArray()));
                Assert.Equal(C(led), Text(program.Slice(program.Length - 3, 3).ToArray()));
                Assert.Equal($"00 05 00 {0x10 + led:X2} FF 00 00 00 00 5C", Text(frames[led * 2 + 1].Buffer));
            }
        }

        /// <summary>The 21 command of rival_re input/decomp.c:3606-3628 with the
        /// mask 07 and a slot per zone, the strip's top, middle and bottom
        /// LEDs (rivalcfg devices/rival3_gen2.py:50-76).</summary>
        [Fact]
        public void ARival3Gen2_TakesItsThreeZonesInOneReport()
            => Assert.Equal(new[] { $"00 21 07 {C(0)} {C(1)} {C(2)}" }, Paint(SteelSeriesFamily.Rival3Gen2));

        /// <summary>rivalcfg devices/aerox3.py:44-70: the zone bit 01, 02 or 04
        /// and the color in that zone's slot.</summary>
        [Fact]
        public void AnAerox3_TakesAReportPerZone()
            => Assert.Equal(new[]
            {
                $"00 21 01 {C(0)}",
                $"00 21 02 00 00 00 {C(1)}",
                $"00 21 04 00 00 00 00 00 00 {C(2)}",
            }, Paint(SteelSeriesFamily.Aerox3));

        /// <summary>rivalcfg devices/rival5.py:50-64 and 185-199: 21 FF 03 and
        /// ten colors, the wheel first, eight side LEDs, the logo last.</summary>
        [Fact]
        public void ARival5_TakesItsTenLedsInOneReport()
        {
            var info = Info(SteelSeriesFamily.Rival5);
            Assert.Equal(LightKind.Zone, info.Lights[0].Kind);
            Assert.All(info.Lights.Skip(1).Take(8), l => Assert.Equal(LightKind.Edge, l.Kind));
            Assert.Equal(LightKind.Logo, info.Lights[9].Kind);
            Assert.Equal(new[] { "00 21 FF 03 " + string.Join(" ", Enumerable.Range(0, 10).Select(C)) },
                Paint(SteelSeriesFamily.Rival5));
        }

        /// <summary>rivalcfg devices/aerox3_wireless_wired.py:53-79 and the
        /// dongle's devices/aerox3_wireless_wireless.py:3-16: 21 01 ZZ, or 61
        /// 01 ZZ through the dongle, and the zone's color.</summary>
        [Theory]
        [InlineData((int)SteelSeriesFamily.AeroxWireless, "21")]
        [InlineData((int)SteelSeriesFamily.AeroxWirelessDongle, "61")]
        public void AWirelessAerox_TakesAReportPerZone(int family, string command)
            => Assert.Equal(Enumerable.Range(0, 3).Select(z => $"00 {command} 01 {z:X2} {C(z)}"), Paint((SteelSeriesFamily)family));

        // ── The keyboards with zones ──

        /// <summary>OpenRGB SteelSeriesApexTZoneController.cpp:56-80: 0B 00 and
        /// ten zone colors.</summary>
        [Fact]
        public void AnApex3_TakesItsTenZonesInOneReport()
            => Assert.Equal(new[] { "00 0B 00 " + string.Join(" ", Enumerable.Range(0, 10).Select(C)) },
                Paint(SteelSeriesFamily.Apex3));

        /// <summary>OpenRGB SteelSeriesApex8ZoneController.cpp:78-93: 21 FF and
        /// eight zone colors.</summary>
        [Fact]
        public void AnApex3Tkl_TakesItsEightZonesInOneReport()
            => Assert.Equal(new[] { "00 21 FF " + string.Join(" ", Enumerable.Range(0, 8).Select(C)) },
                Paint(SteelSeriesFamily.Apex3Tkl));

        /// <summary>OpenRGB SteelSeriesOldApexController.cpp:85-124: 07 00, then
        /// each zone's color and the level 08, the main keys, the numpad, the
        /// function row, the macro keys and the logo.</summary>
        [Fact]
        public void TheApexOf2013_TakesItsFiveZonesInOneReport()
        {
            var info = Info(SteelSeriesFamily.Apex2013);
            Assert.Equal(new[] { 1, 2, 3, 4, 0 }, info.Lights.Select(l => l.Zone));
            Assert.Equal(LightKind.Logo, info.Lights[4].Kind);
            Assert.Equal(new[] { "00 07 00 " + string.Join(" ", Enumerable.Range(0, 5).Select(i => C(i) + " 08")) },
                Paint(SteelSeriesFamily.Apex2013));
        }

        /// <summary>The M750's grid takes each key's color in the slot OpenRGB
        /// puts it (SteelSeriesApexMController.cpp:18-37), and FF 32 00 in the
        /// 28 slots with no key.</summary>
        [Fact]
        public void AnApexM750_TakesEachKeysColorInItsSlot()
        {
            var info = Info(SteelSeriesFamily.ApexM750);
            Assert.Equal(104, info.Lights.Count);
            // Slot 0 is the left Ctrl, slot 4 the space bar, slot 110 Escape.
            Assert.Equal(new[] { 0xE0, 0x2C, 0x29 }, new[] { 0, 4, 110 }.Select(s => (int)info.Lights.Single(l => l.Address == s).Usage));
            var colors = info.Lights.Select(l => l.Usage == 0x29 ? 0xAA0000 : 0x000011).ToArray();
            var data = info.Paint(info.Report, false, info.Lights, colors, 0).Single().Buffer.AsSpan(1);
            Assert.Equal("00 00 01 8E 01 03 06 16", Text(data.Slice(0, 8).ToArray()));
            Assert.Equal("AA 00 00", Text(data.Slice(8 + 110 * 3, 3).ToArray()));
            Assert.Equal("00 00 11", Text(data.Slice(8 + 0, 3).ToArray()));
            foreach (byte slot in SteelSeriesDirectProtocol.M750NoKey)
                Assert.Equal("FF 32 00", Text(data.Slice(8 + slot * 3, 3).ToArray()));
        }

        // ── The per-key boards ──

        /// <summary>The per-key frame (SteelSeriesApexController.cpp:128-200)
        /// carries every key code with its light's color, and the background
        /// for a code the board's lights leave out.</summary>
        [Fact]
        public void APerKeyBoard_TakesEachKeysColor_AndTheBackgroundForAKeyItsSkuLacks()
        {
            var info = Info(SteelSeriesFamily.ApexPro);
            Assert.True(info.PerKey);
            Assert.Null(info.Lights);
            var lights = SteelSeriesApexLayouts.Lights(SteelSeriesApexLayouts.Grid("64734", SteelSeriesApexLayouts.Form.Tkl));
            var colors = lights.Select(l => l.Address == 0x04 ? 0xFF0000 : 0x0000FF).ToArray();
            byte[] data = info.Paint(info.Report, false, lights, colors, 0x00FF00).Single().Buffer.Skip(1).ToArray();
            Assert.Equal(0x3A, data[0]);
            Assert.Equal(112, data[1]);
            string KeyOf(byte code)
            {
                int i = Array.IndexOf(SteelSeriesDirectProtocol.ApexKeys, code);
                return Text(data.Skip(2 + i * 4).Take(4).ToArray());
            }
            Assert.Equal("04 FF 00 00", KeyOf(0x04));   // A, its own color
            Assert.Equal("05 00 00 FF", KeyOf(0x05));   // B, the background of the keys
            Assert.Equal("59 00 FF 00", KeyOf(0x59));   // Keypad 1, which a TKL lacks
        }

        // ── The layouts ──

        /// <summary>The tables are OpenRGB's at the pinned commit: 112 key
        /// codes, the A key first and play and pause last, and the ANSI grid
        /// of 6 rows of 22.</summary>
        [Fact]
        public void TheTables_AreOpenRgbsAtThePinnedCommit()
        {
            Assert.StartsWith("b9d903b5", SteelSeriesApexLayouts.OpenRgbCommit);
            Assert.Equal(112, SteelSeriesApexLayouts.Keys.Length);
            Assert.Equal(0x04, SteelSeriesApexLayouts.Keys[0]);
            Assert.Equal(0xFB, SteelSeriesApexLayouts.Keys[111]);
            Assert.Equal(6 * 22, SteelSeriesApexLayouts.Ansi.Length);
            Assert.Equal(6 * 22, SteelSeriesApexLayouts.M750.Length);
            // Each index names a key once.
            var used = SteelSeriesApexLayouts.Ansi.Where(i => i >= 0).ToList();
            Assert.Equal(used.Count, used.Distinct().Count());
        }

        private static HashSet<int> Codes(string sku, SteelSeriesApexLayouts.Form form)
            => SteelSeriesApexLayouts.Lights(SteelSeriesApexLayouts.Grid(sku, form)).Select(l => l.Address).ToHashSet();

        private static readonly int[] Keypad = { 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F, 0x60, 0x61, 0x62, 0x63 };

        /// <summary>A full ANSI board has the keypad and the ANSI backslash,
        /// and no ISO key. A TKL SKU loses the keypad and gains play and
        /// pause where Pause was (apex_tkl_us_region_patch). An ISO SKU moves
        /// Enter and has the two ISO keys in place of the ANSI backslash
        /// (apex_iso_region_patch). A Mini loses the F row too.</summary>
        [Fact]
        public void ASku_PicksTheFormAndTheRegion()
        {
            var full = Codes(null, SteelSeriesApexLayouts.Form.Full);
            Assert.Superset(Keypad.ToHashSet(), full);
            Assert.Contains(0x31, full);
            Assert.DoesNotContain(0x64, full);
            Assert.DoesNotContain(0x32, full);

            var tkl = Codes("64734", SteelSeriesApexLayouts.Form.Full);
            Assert.Empty(tkl.Intersect(Keypad));
            Assert.Contains(0xFB, tkl);
            Assert.DoesNotContain(0x48, tkl);

            var tklIso = Codes("64739", SteelSeriesApexLayouts.Form.Full);
            Assert.Contains(0x64, tklIso);
            Assert.Contains(0x32, tklIso);
            Assert.DoesNotContain(0x31, tklIso);
            Assert.Empty(tklIso.Intersect(Keypad));

            var fullJp = Codes("64629", SteelSeriesApexLayouts.Form.Tkl);
            Assert.Contains(0x89, fullJp);   // Yen
            Assert.Superset(Keypad.ToHashSet(), fullJp);

            var mini = Codes("64837", SteelSeriesApexLayouts.Form.Full);
            Assert.DoesNotContain(0x3A, mini);   // F1
            Assert.Empty(mini.Intersect(Keypad));
        }

        /// <summary>A SKU the table lacks, or none, takes the patch of the
        /// product's form, where OpenRGB takes none. 64856 appears twice in
        /// OpenRGB's table, and std::map keeps the first entry, the TKL US
        /// one.</summary>
        [Fact]
        public void AnUnknownSku_TakesItsProductsForm_AndATwiceListedSkuItsFirstEntry()
        {
            Assert.False(SteelSeriesApexLayouts.Knows("64741"));
            Assert.Empty(Codes("64741", SteelSeriesApexLayouts.Form.Tkl).Intersect(Keypad));
            Assert.Superset(Keypad.ToHashSet(), Codes("64741", SteelSeriesApexLayouts.Form.Full));
            var twice = Codes("64856", SteelSeriesApexLayouts.Form.Full);
            Assert.Empty(twice.Intersect(Keypad));
            Assert.DoesNotContain(0x64, twice);
        }

        /// <summary>A key is named by its code, its keyboard-page usage, but
        /// for the right Fn key (F0) and play and pause (FB), which are lights
        /// of their own with a label.</summary>
        [Fact]
        public void AKeyCode_IsItsUsage_SaveFnAndPlayPause()
        {
            Assert.Equal(new LightInfo(0x04, LightKind.Key, 0x04, LightKeySource.Vendor), SteelSeriesApexLayouts.Light(0x04));
            var fn = SteelSeriesApexLayouts.Light(0xF0);
            Assert.Equal((LightKind.Other, (ushort)0, LightLabel.Fn), (fn.Kind, fn.Usage, fn.Label));
            var play = SteelSeriesApexLayouts.Light(0xFB);
            Assert.Equal((LightKind.Other, (ushort)0, LightLabel.PlayPause), (play.Kind, play.Usage, play.Label));
        }

        /// <summary>The serial's first five characters are the SKU
        /// (RGBController_SteelSeriesApex.cpp:105). The region byte follows F5
        /// and a byte (SteelSeriesApexController.cpp:384-408).</summary>
        [Fact]
        public void TheSkuIsRead_FromTheSerial_OrFromTheRegion()
        {
            Assert.Equal("64739", SteelSeriesApexLayouts.SkuOfSerial(System.Text.Encoding.ASCII.GetBytes("64739A123456789")));
            Assert.Null(SteelSeriesApexLayouts.SkuOfSerial(System.Text.Encoding.ASCII.GetBytes("1.19.7")));
            Assert.Null(SteelSeriesApexLayouts.SkuOfSerial(new byte[] { 0x36, 0x34 }));
            Assert.Equal("64740", SteelSeriesApexLayouts.SkuOfRegion(new byte[] { 0xF5, 0x00, 0x01 }));
            Assert.Equal("64743", SteelSeriesApexLayouts.SkuOfRegion(new byte[] { 0xF5, 0x00, 0x04 }));
            Assert.Equal("64745", SteelSeriesApexLayouts.SkuOfRegion(new byte[] { 0xF5, 0x00, 0x0D }));
            Assert.Null(SteelSeriesApexLayouts.SkuOfRegion(new byte[] { 0xF5, 0x00, 0x02 }));
            Assert.Null(SteelSeriesApexLayouts.SkuOfRegion(new byte[] { 0x90, 0x00, 0x01 }));
        }

        /// <summary>Which board asks what, by OpenRGB's product IDs
        /// (SteelSeriesDevices.h:90-108, SteelSeriesApexController.cpp:52-73
        /// and 351-419).</summary>
        [Fact]
        public void EachBoard_AsksForItsSku_OrHasOne()
        {
            foreach (ushort pid in new ushort[] { 0x1610, 0x1612, 0x1618, 0x161C, 0x1614, 0x1640 })
                Assert.True(SteelSeriesApexLayouts.AsksSerial(pid), pid.ToString("X4"));
            Assert.True(SteelSeriesApexLayouts.AsksRegion(0x1642));
            Assert.Equal("64847", SteelSeriesApexLayouts.FixedSku(0x1634));
            Assert.Equal("64837", SteelSeriesApexLayouts.FixedSku(0x1620));
            foreach (ushort pid in new ushort[] { 0x1628, 0x1630, 0x1632, 0x1644, 0x1646 })
                Assert.Equal("64865", SteelSeriesApexLayouts.FixedSku(pid));
            Assert.Null(SteelSeriesApexLayouts.FixedSku(0x1610));
            Assert.Equal(SteelSeriesApexLayouts.Form.Full, SteelSeriesApexLayouts.FormOf(0x1610));
            Assert.Equal(SteelSeriesApexLayouts.Form.Mini, SteelSeriesApexLayouts.FormOf(0x1620));
            Assert.Equal(SteelSeriesApexLayouts.Form.Tkl, SteelSeriesApexLayouts.FormOf(0x1618));
        }

        /// <summary>Every family with more than one LED has its lights, each
        /// address once, zones numbered from 1, and a paint whose frames
        /// change when one light's color does.</summary>
        [Fact]
        public void EveryFamilyWithLights_PaintsEachLight()
        {
            foreach (var info in SteelSeriesDirectProtocol.Families.Where(f => f.Paint != null && !f.PerKey))
            {
                var lights = info.Lights;
                Assert.True(lights.Count > 1, info.Family.ToString());
                Assert.Equal(lights.Count, lights.Select(l => l.Address).Distinct().Count());
                var zones = lights.Where(l => l.Kind == LightKind.Zone).Select(l => l.Zone).ToList();
                Assert.Equal(Enumerable.Range(1, zones.Count), zones);
                var flat = new int[lights.Count];
                var before = string.Join("|", info.Paint(info.Report, false, lights, flat, 0).Select(f => Text(f.Buffer)));
                for (int i = 0; i < lights.Count; i++)
                {
                    var one = (int[])flat.Clone();
                    one[i] = 0x7F7F7F;
                    var after = string.Join("|", info.Paint(info.Report, false, lights, one, 0).Select(f => Text(f.Buffer)));
                    Assert.True(before != after, $"{info.Family} light {i}");
                }
            }
        }
    }
}
