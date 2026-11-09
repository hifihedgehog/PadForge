using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The SteelSeries headsets and mousemats OpenRGB lights (#557): the
    /// Arctis 5, the Arctis Nova 3, the Siberia 350 and the QCK Prism Cloth
    /// mats. Each expected report is written out here from OpenRGB at
    /// b9d903b5 (Controllers/SteelSeriesController), never from the builder
    /// under test, as a Windows HID call takes it: the report ID first and
    /// the buffer at the length OpenRGB sends.
    /// </summary>
    public class SteelSeriesHeadsetAndMatTests
    {
        private const int Red = 0x123456, Blue = 0xABCDEF;

        private static byte[] Hex(string text)
            => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();

        private static string Text(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');

        /// <summary>A buffer of <paramref name="length"/> bytes that starts
        /// with <paramref name="head"/> and is zero after it.</summary>
        private static string Padded(string head, int length)
        {
            var bytes = new byte[length];
            Hex(head).CopyTo(bytes, 0);
            return Text(bytes);
        }

        private static SteelSeriesFrame[] Paint(SteelSeriesFamily family, params int[] colors)
        {
            var info = SteelSeriesDirectProtocol.Info(family);
            return info.Paint(info.Report, false, info.Lights, colors, 0);
        }

        private static void Same(IEnumerable<(SteelSeriesReport Report, string Bytes)> expected, IEnumerable<SteelSeriesFrame> frames)
            => Assert.Equal(expected.Select(e => (e.Report, e.Bytes)), frames.Select(f => (f.Report, Text(f.Buffer))));

        // ── The frames ──

        /// <summary>SetColor once per earcup, zone 0 the left and 1 the
        /// right (SteelSeriesArctis5Controller.cpp:51-140,
        /// RGBController_SteelSeriesArctis5.cpp:54-90), and once per microphone
        /// LED, zones 2 and 3 (signal-plugins Steelseries_Arctis_5_Headset.js:25-35,
        /// 118-166): six 37-byte output reports with the report ID 06 each.</summary>
        [Fact]
        public void AnArctis5Picture_IsSetColorForEachEarcup()
        {
            const SteelSeriesReport o = SteelSeriesReport.Output;
            var expected = new List<(SteelSeriesReport, string)>();
            foreach (var (zone, rgb) in new[] { ("00", "12 34 56"), ("01", "AB CD EF"), ("02", "01 02 03"), ("03", "04 05 06") })
            {
                expected.Add((o, Padded("06 81 43 01 22", 37)));
                expected.Add((o, Padded("06 81 43 01 23", 37)));
                expected.Add((o, Padded("06 8A 42 00 20 41 00 " + rgb + " FF 32 C8 C8", 37)));
                expected.Add((o, Padded("06 8A 42 00 20 41 08 " + zone + " 01", 37)));
                expected.Add((o, Padded("06 8A 42 00 20 60 " + zone, 37)));
                expected.Add((o, Padded("06 8A 42 00 20 05", 37)));
            }
            Same(expected, Paint(SteelSeriesFamily.Arctis5, Red, Blue, 0x010203, 0x040506));
        }

        /// <summary>The QcK Prism of 0x1507: one 525-byte feature report, 0E 00
        /// 0C, each of twelve LEDs twelve bytes on from the last, its color, then
        /// 01 at its byte 14 and its index at its byte 16, and the apply report
        /// (signal-plugins Steelseries_Qck_Prism_Mousepad.js:68-98).</summary>
        [Fact]
        public void AQckPrism12Picture_IsItsTwelveLeds_ThenTheApply()
        {
            var colors = Enumerable.Range(1, 12).Select(i => i << 16 | i << 8 | i).ToArray();
            var frames = Paint(SteelSeriesFamily.QckPrism12, colors);
            Assert.Equal(2, frames.Length);
            Assert.Equal(SteelSeriesReport.Feature, frames[0].Report);
            var b = frames[0].Buffer;
            Assert.Equal(525, b.Length);
            Assert.Equal("00 0E 00 0C 00", Text(b.Take(5).ToArray()));
            Assert.Equal(new byte[] { 1, 1, 1 }, b.Skip(5).Take(3).ToArray());
            Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 1, 0, 0 }, b.Skip(8).Take(9).ToArray());
            Assert.Equal(new byte[] { 12, 12, 12 }, b.Skip(11 * 12 + 5).Take(3).ToArray());
            Assert.Equal(1, b[11 * 12 + 14]);
            Assert.Equal(11, b[11 * 12 + 16]);
            Assert.Equal(SteelSeriesReport.Output, frames[1].Report);
            Assert.Equal(Padded("00 0D", 65), Text(frames[1].Buffer));
        }

        /// <summary>The Arctis Pro GameDAC: each zone's 14-byte report, then the
        /// three A5 reports and A3 (signal-plugins
        /// Steelseries_Arctis_Pro_GameDAC_Headset.js:477-520), the lighting taken
        /// with 2C 01 and given back with 2C 00 (lines 191-207).</summary>
        [Fact]
        public void AGameDacPicture_IsEachZone_ThenTheApplyReports()
        {
            const SteelSeriesReport o = SteelSeriesReport.Output;
            Same(new[]
            {
                (o, Padded("00 AA 00 12 34 56 FF 32 C8 C8 00 00 01", 14)),
                (o, Padded("00 AA 01 AB CD EF FF 32 C8 C8 00 01 01", 14)),
                (o, Padded("00 AA 02 01 02 03 FF 32 C8 C8 00 02 01", 14)),
                (o, Padded("00 AA 03 04 05 06 FF 32 C8 C8 00 03 01", 14)),
                (o, "00 A5 08 0A 00"),
                (o, "00 A5 03 0A 00"),
                (o, "00 A5 04 0A 00"),
                (o, "00 A3 00"),
            }, Paint(SteelSeriesFamily.ArctisProGameDac, Red, Blue, 0x010203, 0x040506));
            Same(new[] { (o, "00 2C 01 00") }, SteelSeriesDirectProtocol.Claim(SteelSeriesFamily.ArctisProGameDac, false));
            Same(new[] { (o, "00 2C 00 00") }, SteelSeriesDirectProtocol.HandBack(SteelSeriesFamily.ArctisProGameDac, false));
            var info = SteelSeriesDirectProtocol.Info(SteelSeriesFamily.ArctisProGameDac);
            Assert.Equal(20, info.GapMs);
            Assert.True(info.RepaintMs >= 150);
        }

        /// <summary>The Arena speakers: one 64-byte output report 06 A7 0F with
        /// each speaker's two lights, left first (signal-plugins
        /// Steelseries_Arena_7_Speakers.js, SendColorPacket).</summary>
        [Fact]
        public void AnArenaPicture_IsOneReportOfFourLights()
        {
            Same(new[]
            {
                (SteelSeriesReport.Output, Padded("06 A7 0F 12 34 56 AB CD EF 01 02 03 04 05 06", 64)),
            }, Paint(SteelSeriesFamily.ArenaSpeakers, Red, Blue, 0x010203, 0x040506));
        }

        /// <summary>SetDirect once per LED, LED 0 the right and LED 1 the
        /// left (SteelSeriesArctisNova3Controller.cpp:68-96 and 120-148,
        /// RGBController_SteelSeriesArctisNova3.cpp:98-145): the 521-byte
        /// effect report, then the 64-byte select and apply reports.</summary>
        [Fact]
        public void AnArctisNova3Picture_IsSetDirectForEachLed()
        {
            static string Effect(string side, string rgb)
            {
                var bytes = new byte[521];
                Hex("06 AA " + side).CopyTo(bytes, 0);
                bytes[0x0D] = 0x98;
                bytes[0x0E] = 0x3A;
                bytes[0x15] = 0x55;
                bytes[0x17] = 0x96;
                bytes[0x1D] = 0x01;
                bytes[0x25] = 0x02;
                Hex(rgb).CopyTo(bytes, 0x2D);
                bytes[0x30] = 0xFF;
                return Text(bytes);
            }
            const SteelSeriesReport o = SteelSeriesReport.Output;
            Same(new[]
            {
                (o, Effect("01", "12 34 56")), (o, Padded("06 A5 02 0A", 64)), (o, Padded("06 09", 64)),
                (o, Effect("00", "AB CD EF")), (o, Padded("06 A5 01 0A", 64)), (o, Padded("06 A3", 64)),
            }, Paint(SteelSeriesFamily.ArctisNova3, Red, Blue));
        }

        /// <summary>SetColor's four commands, each through send_usb_msg: 01,
        /// 00, the command, its payload's length and the payload, 16 bytes
        /// (SteelSeriesSiberiaController.cpp:16-36 and 73-105).</summary>
        [Fact]
        public void ASiberia350Color_IsSetColorsFourCommands()
        {
            const SteelSeriesReport o = SteelSeriesReport.Output;
            Same(new[]
            {
                (o, Padded("01 00 95 02 80 BF", 16)),
                (o, Padded("01 00 80 02 52 20", 16)),
                (o, Padded("01 00 83 03 12 34 56", 16)),
                (o, Padded("01 00 93 02 03 80", 16)),
            }, SteelSeriesDirectProtocol.Color(SteelSeriesFamily.Siberia350, SteelSeriesReport.Output, false, 0x12, 0x34, 0x56));
            Assert.Null(SteelSeriesDirectProtocol.Info(SteelSeriesFamily.Siberia350).Lights);
        }

        /// <summary>SetColors: the 525-byte feature report with both lights
        /// and the 65-byte output report 0D (SteelSeriesQCKMatController.cpp:51-100).
        /// SignalRGB's frame for the same mats, read from the vendor's own
        /// traffic, is the same bytes (signal-plugins
        /// Steelseries_Qck_Mousepad.js:76-114).</summary>
        [Fact]
        public void AQckPicture_IsSetColors()
        {
            var color = new byte[525];
            Hex("00 0E 00 02 00 12 34 56 FF 32 C8 00 00 00 01 00 00 AB CD EF FF 32 C8 00 00 01 01 00 01").CopyTo(color, 0);
            Same(new[]
            {
                (SteelSeriesReport.Feature, Text(color)),
                (SteelSeriesReport.Output, Padded("00 0D", 65)),
            }, Paint(SteelSeriesFamily.QckPrismCloth, Red, Blue));
        }

        /// <summary>The headsets', mats' and speakers' lights: zones in the
        /// order the frames take them, the Siberia 350 one color.</summary>
        [Theory]
        [InlineData(SteelSeriesFamily.Arctis5, 4)]
        [InlineData(SteelSeriesFamily.ArctisNova3, 2)]
        [InlineData(SteelSeriesFamily.QckPrismCloth, 2)]
        [InlineData(SteelSeriesFamily.QckPrism12, 12)]
        [InlineData(SteelSeriesFamily.ArctisProGameDac, 4)]
        [InlineData(SteelSeriesFamily.ArenaSpeakers, 4)]
        internal void TheLights_AreZones(SteelSeriesFamily family, int count)
        {
            var lights = SteelSeriesDirectProtocol.Info(family).Lights;
            Assert.Equal(Enumerable.Range(0, count), lights.Select(l => l.Address));
            Assert.Equal(Enumerable.Range(1, count), lights.Select(l => l.Zone));
            Assert.All(lights, l => Assert.Equal(LightKind.Zone, l.Kind));
        }

        /// <summary>No family here has a frame that returns the stored
        /// lighting, and none takes a claim.</summary>
        [Fact]
        public void NoHeadsetOrMat_ClaimsOrHandsBack()
        {
            foreach (var family in new[] { SteelSeriesFamily.Arctis5, SteelSeriesFamily.ArctisNova3,
                         SteelSeriesFamily.Siberia350, SteelSeriesFamily.QckPrismCloth, SteelSeriesFamily.QckPrism12,
                         SteelSeriesFamily.ArenaSpeakers })
            {
                Assert.Empty(SteelSeriesDirectProtocol.Claim(family, false));
                Assert.Empty(SteelSeriesDirectProtocol.HandBack(family, false));
                Assert.False(SteelSeriesDirectProtocol.Info(family).Gated);
                Assert.False(SteelSeriesDirectProtocol.Info(family).Echoes);
            }
        }

        // ── The gate ──

        private static HidCollectionInfo Collection(ushort productId, int iface, ushort usagePage, ushort usage,
            ushort output, ushort feature = 0)
            => new()
            {
                Path = $@"\\?\hid#vid_1038&pid_{productId:x4}&mi_{iface:x2}#gate",
                VendorId = 0x1038, ProductId = productId, UsagePage = usagePage, Usage = usage, InterfaceNumber = iface,
                InputReportLength = 65, OutputReportLength = output, FeatureReportLength = feature,
            };

        private static SteelSeriesFamily? Family(HidCollectionInfo collection)
            => SteelSeriesDirectProtocol.TryPlan(collection, out var plan) ? plan.Info.Family : null;

        /// <summary>The Arctis 5 on interface 5 (SteelSeriesControllerDetect.cpp:524-525),
        /// on the page and usage SignalRGB names (signal-plugins
        /// Steelseries_Arctis_5_Headset.js:76-79), with 37-byte output
        /// reports. The Arctis Nova 3 on interface 4, page 0xFFC0, usage 1
        /// (line 523), with 521-byte output reports.</summary>
        [Fact]
        public void TheArctisHeadsets_AreTakenOnTheirInterfaceAndPage()
        {
            foreach (ushort pid in new ushort[] { 0x1250, 0x12AA, 0x1252 })
            {
                Assert.Equal(SteelSeriesFamily.Arctis5, Family(Collection(pid, 5, 0xFFC0, 1, 37)));
                Assert.Null(Family(Collection(pid, 5, 0xFFC0, 1, 36)));
                Assert.Null(Family(Collection(pid, 5, 0xFF00, 1, 37)));
                Assert.Null(Family(Collection(pid, 5, 0xFFC0, 2, 37)));
                Assert.Null(Family(Collection(pid, 3, 0xFFC0, 1, 37)));
            }
            Assert.Equal(SteelSeriesFamily.ArctisNova3, Family(Collection(0x12EC, 4, 0xFFC0, 1, 521)));
            Assert.Null(Family(Collection(0x12EC, 4, 0xFFC0, 1, 520)));
            Assert.Null(Family(Collection(0x12EC, 4, 0x000C, 1, 521)));
            Assert.Null(Family(Collection(0x12EC, 5, 0xFFC0, 1, 521)));
        }

        /// <summary>The products SignalRGB alone lights, where its plugins take
        /// them: the GameDAC and the QcK Prism by interface 0 alone, the Arena
        /// speakers on interface 4 and page 0xFFC0 with any usage.</summary>
        [Fact]
        public void TheProductsOnlySignalRgbLights_AreTakenWhereItsPluginsTakeThem()
        {
            Assert.Equal(SteelSeriesFamily.ArctisProGameDac, Family(Collection(0x1280, 0, 0xFF00, 7, 14)));
            Assert.Null(Family(Collection(0x1280, 0, 0xFF00, 7, 13)));
            Assert.Null(Family(Collection(0x1280, 1, 0xFF00, 7, 14)));
            Assert.Equal(SteelSeriesFamily.QckPrism12, Family(Collection(0x1507, 0, 0xFF00, 7, 65, 525)));
            Assert.Null(Family(Collection(0x1507, 0, 0xFF00, 7, 65, 524)));
            Assert.Null(Family(Collection(0x1507, 1, 0xFF00, 7, 65, 525)));
            foreach (ushort pid in PeripheralProductIds.SteelSeriesSpeakers)
            {
                Assert.Equal(SteelSeriesFamily.ArenaSpeakers, Family(Collection(pid, 4, 0xFFC0, 9, 64)));
                Assert.Null(Family(Collection(pid, 4, 0xFFC0, 9, 63)));
                Assert.Null(Family(Collection(pid, 4, 0xFF00, 9, 64)));
                Assert.Null(Family(Collection(pid, 3, 0xFFC0, 9, 64)));
            }
        }

        /// <summary>The Siberia 350 and the mats, which OpenRGB registers by
        /// interface alone (SteelSeriesControllerDetect.cpp:522 and 530-538),
        /// are taken on any page there, by the lengths of the reports their
        /// frames need.</summary>
        [Fact]
        public void TheSiberiaAndTheMats_AreTakenOnTheirInterface_OnAnyPage()
        {
            foreach (ushort page in new ushort[] { 0xFFC0, 0xFF00, 0x000C })
            {
                Assert.Equal(SteelSeriesFamily.Siberia350, Family(Collection(0x1229, 3, page, 1, 16)));
                foreach (ushort pid in PeripheralProductIds.SteelSeriesMousemats.Where(p => p != 0x1507))
                    Assert.Equal(SteelSeriesFamily.QckPrismCloth, Family(Collection(pid, 0, page, 7, 65, 525)));
            }
            Assert.Null(Family(Collection(0x1229, 3, 0xFFC0, 1, 15)));
            Assert.Null(Family(Collection(0x1229, 2, 0xFFC0, 1, 16)));
            Assert.Null(Family(Collection(0x150A, 0, 0xFFC0, 1, 65, 524)));
            Assert.Null(Family(Collection(0x150A, 0, 0xFFC0, 1, 64, 525)));
            Assert.Null(Family(Collection(0x150A, 1, 0xFFC0, 1, 65, 525)));
        }

        // ── The names ──

        /// <summary>Every product of the table has a name, the one OpenRGB
        /// registers it under where it does, else rivalcfg's, else SignalRGB's,
        /// read by tools/gen_steelseries_names.py at OpenRGB's pin.</summary>
        [Fact]
        public void EveryProduct_HasItsReferencesName()
        {
            foreach (var (productId, _) in SteelSeriesDirectProtocol.Models)
            {
                string name = SteelSeriesDirectProtocol.NameOf(productId);
                Assert.False(string.IsNullOrWhiteSpace(name), productId.ToString("X4"));
                Assert.All(name, c => Assert.InRange(c, ' ', '~'));
            }
            Assert.Equal(SteelSeriesApexLayouts.OpenRgbCommit, SteelSeriesNames.OpenRgbCommit);
            var ids = SteelSeriesNames.All.Select(n => n.ProductId).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.Null(SteelSeriesDirectProtocol.NameOf(0x0000));
        }

        /// <summary>Names read from the lines they come from.</summary>
        [Theory]
        // OpenRGB SteelSeriesControllerDetect.cpp:522-525, 530 and 543.
        [InlineData(0x1229, "SteelSeries Siberia 350")]
        [InlineData(0x12EC, "SteelSeries Arctis Nova 3")]
        [InlineData(0x1250, "SteelSeries Arctis 5")]
        [InlineData(0x12AA, "SteelSeries Arctis 5")]
        [InlineData(0x150A, "SteelSeries QCK Prism Cloth Medium")]
        [InlineData(0x161A, "SteelSeries Apex 3")]
        // OpenRGB :495, whose name is Acer's, and :507, which rivalcfg
        // calls "SteelSeries Rival 650 Wireless (wired mode)" (rival650.py:7).
        [InlineData(0x1714, "Acer Predator Gaming Mouse (Rival 300)")]
        [InlineData(0x172B, "SteelSeries Rival 650")]
        // rivalcfg alone: rival3_gen2.py:8, sensei_raw.py:7, and
        // prime_wireless_wireless.py:23, whose no-break space is a space.
        [InlineData(0x1870, "SteelSeries Rival 3 Gen 2")]
        [InlineData(0x1369, "SteelSeries Sensei [RAW]")]
        [InlineData(0x1840, "SteelSeries Prime Wireless (2.4 GHz wireless mode)")]
        // SignalRGB alone, each plugin's Name() and PIDLibrary.
        [InlineData(0x1252, "SteelSeries Arctis Pro")]
        [InlineData(0x1280, "SteelSeries Arctis Pro GameDAC")]
        [InlineData(0x1507, "SteelSeries QcK Prism")]
        [InlineData(0x1A00, "SteelSeries Arena 7")]
        [InlineData(0x1A05, "SteelSeries Arena 9")]
        public void AProduct_IsNamedAsItsReferenceNamesIt(int productId, string name)
            => Assert.Equal(name, SteelSeriesDirectProtocol.NameOf((ushort)productId));
    }
}
