using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The frames that light a SteelSeries device on its own
    /// (#494), byte for byte: each expected frame is the one a reference
    /// sends, written out here from that reference and from the vendor's
    /// own captured traffic, never from the builder under test. A frame is
    /// given as a Windows HID call takes it, the report ID position first,
    /// without the zero padding the channel adds.</summary>
    public class SteelSeriesDirectProtocolTests
    {
        private const byte R = 0x12, G = 0x34, B = 0x56;

        /// <summary>Bytes from hex text, where "00*14" stands for fourteen
        /// zero bytes.</summary>
        private static byte[] Hex(string text)
        {
            var bytes = new List<byte>();
            foreach (string token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = token.Split('*');
                byte value = Convert.ToByte(parts[0], 16);
                int count = parts.Length > 1 ? int.Parse(parts[1]) : 1;
                for (int i = 0; i < count; i++) bytes.Add(value);
            }
            return bytes.ToArray();
        }

        private static string Text(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');

        private static void Same(string expected, SteelSeriesReport report, SteelSeriesFrame frame)
        {
            Assert.Equal(report, frame.Report);
            Assert.Equal(Text(Hex(expected)), Text(frame.Buffer));
        }

        private static SteelSeriesFrame[] Color(SteelSeriesFamily family, bool gen3 = false, SteelSeriesReport? report = null)
            => SteelSeriesDirectProtocol.Color(family, report ?? SteelSeriesDirectProtocol.Info(family).Report, gen3, R, G, B);

        private static SteelSeriesFrame[] ColorOf(SteelSeriesFamily family, int rgb, bool gen3 = false)
            => SteelSeriesDirectProtocol.Color(family, SteelSeriesDirectProtocol.Info(family).Report, gen3,
                (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        private static SteelSeriesFrame[] Claim(SteelSeriesFamily family, bool gen3 = false)
            => SteelSeriesDirectProtocol.Claim(family, gen3);

        private static SteelSeriesFrame[] HandBack(SteelSeriesFamily family, bool gen3 = false)
            => SteelSeriesDirectProtocol.HandBack(family, gen3);

        /// <summary>A captured report's bytes, which the capture holds
        /// without the report ID position and padded to the report's
        /// length: the frame under test must equal its head, and the rest
        /// of the capture is zeros.</summary>
        private static void SameAsCaptured(string captured, SteelSeriesFrame frame)
        {
            byte[] wire = Hex(captured);
            Assert.Equal(0x00, frame.Buffer[0]);
            Assert.Equal(Text(wire), Text(frame.Buffer.AsSpan(1).ToArray()));
        }

        // ── The mice with a color command ──

        /// <summary>rivalcfg devices/rival100.py:91-116: the steady effect
        /// 07 00 01 and the color 05 00 RR GG BB. libratbag
        /// driver-steelseries.c:685-744 sends the same two with its Rival
        /// 100 quirk, Aurora RivalMouses.cs:28-38 the color.</summary>
        [Fact]
        public void ARival100_IsSetSteady_ThenGivenItsColor()
        {
            var claim = Assert.Single(Claim(SteelSeriesFamily.Rival100));
            Same("00 07 00 01", SteelSeriesReport.Output, claim);
            var color = Assert.Single(Color(SteelSeriesFamily.Rival100));
            Same("00 05 00 12 34 56", SteelSeriesReport.Output, color);
            Assert.Empty(HandBack(SteelSeriesFamily.Rival100));
        }

        /// <summary>rivalcfg devices/rival300.py:105-156: 07 LL 01 and
        /// 08 LL RR GG BB for the logo, 01, and the wheel, 02. Aurora
        /// RivalMouses.cs:103-125 sends the same colors.</summary>
        [Fact]
        public void ARival300_HasBothLedsSetSteady_ThenBothColored()
        {
            var claim = Claim(SteelSeriesFamily.Rival300);
            Assert.Equal(2, claim.Length);
            Same("00 07 01 01", SteelSeriesReport.Output, claim[0]);
            Same("00 07 02 01", SteelSeriesReport.Output, claim[1]);
            var color = Color(SteelSeriesFamily.Rival300);
            Assert.Equal(2, color.Length);
            Same("00 08 01 12 34 56", SteelSeriesReport.Output, color[0]);
            Same("00 08 02 12 34 56", SteelSeriesReport.Output, color[1]);
            Assert.Empty(HandBack(SteelSeriesFamily.Rival300));
        }

        /// <summary>The Engine's static frame for a Rival 3: 0A 00 0F and
        /// four colors in one report (OpenRGB issue 1167,
        /// rival_3_SolidRed.pcapng, packet 1, an output report to
        /// interface 3). signal-plugins Steelseries_Rival_3_Mouse.js:64-96
        /// builds the same frame.</summary>
        [Fact]
        public void ARival3_TakesItsFourZonesInOneReport_AsTheVendorSendsThem()
        {
            Assert.Empty(Claim(SteelSeriesFamily.Rival3));
            var color = Assert.Single(Color(SteelSeriesFamily.Rival3));
            Same("00 0A 00 0F 12 34 56 12 34 56 12 34 56 12 34 56", SteelSeriesReport.Output, color);
            Assert.Empty(HandBack(SteelSeriesFamily.Rival3));

            SameAsCaptured("0A 00 0F FF 00 00 FF 00 00 FF 00 00 FF 00 00",
                Assert.Single(ColorOf(SteelSeriesFamily.Rival3, 0xFF0000)));
        }

        /// <summary>The 5B cycle of a Rival 310, Sensei 310 or Sensei TEN,
        /// one report per LED: the LED at d[2], the duration 10 27 at
        /// d[3..4], 01 at d[19] and d[27], the color at d[28] and d[31]
        /// (rival310-re 5B.md:6-19, rivalcfg devices/rival310.py:4-12).
        /// With the Engine's duration it is the Engine's steady frame on a
        /// Sensei 310 (OpenRGB issue 1103, the logo and mousewheel Steady
        /// mode captures, packet 13 of each, a feature report to interface
        /// 0).</summary>
        [Fact]
        public void ARival310_GetsOneSteadyCyclePerLed_AsTheVendorSendsIt()
        {
            const string logo = "00 5B 00 00 10 27 00*14 01 00*7 01 12 34 56 12 34 56 00";
            const string wheel = "00 5B 00 01 10 27 00*14 01 00*7 01 12 34 56 12 34 56 00";
            var color = Color(SteelSeriesFamily.Rival310);
            Assert.Equal(2, color.Length);
            Same(logo, SteelSeriesReport.Feature, color[0]);
            Same(wheel, SteelSeriesReport.Feature, color[1]);
            Assert.Equal(36, color[0].Buffer.Length);

            // The same bytes as an output report, the type OpenRGB and
            // libratbag send (OpenRGB SteelSeriesSenseiController.cpp:247-286).
            var output = Color(SteelSeriesFamily.Rival310, report: SteelSeriesReport.Output);
            Same(logo, SteelSeriesReport.Output, output[0]);
            Same(wheel, SteelSeriesReport.Output, output[1]);
            Assert.Empty(Claim(SteelSeriesFamily.Rival310));
            Assert.Empty(HandBack(SteelSeriesFamily.Rival310));

            var captured = ColorOf(SteelSeriesFamily.Rival310, 0xFF5200);
            SameAsCaptured("5B 00 00 10 27 00*14 01 00*7 01 FF 52 00 FF 52 00 00", captured[0]);
            SameAsCaptured("5B 00 01 10 27 00*14 01 00*7 01 FF 00 00 FF 00 00 00",
                ColorOf(SteelSeriesFamily.Rival310, 0xFF0000)[1]);
        }

        /// <summary>The Engine's static frame for each of a Rival 600's
        /// eight LEDs, from its capture of static green (OpenRGB issue 833,
        /// SteelSeries600_Static_RGB_ReportDescriptor.pcapng, packets 7 to
        /// 21, feature reports of 578 bytes to interface 0): the LED, its
        /// x and y, the LED again, the duration, 01 at d[24], four points
        /// at d[29], the color, then red, green and blue at 00, 54 and 54,
        /// and the color at 56. LED 7 carried 10 27 for its duration in
        /// that capture, and gets it here too.</summary>
        [Fact]
        public void ARival600_GetsTheVendorsStaticFramePerLed_WithEachLedsPosition()
        {
            // The frame's fixed part, as captured.
            const string tail = "BE 14 00*14 01 00 00 00 00 04 00 FF 00 FF 00 00 00 00 FF 00 54 00 00 FF 54 00 FF 00 56";
            string[] heads =
            {
                "05 00 00 55 00 46 00 00",
                "05 00 01 55 00 0E 01 01",
                "05 00 02 32 00 91 00 02",
                "05 00 03 78 00 91 00 03",
                "05 00 04 2D 00 B4 00 04",
                "05 00 05 7D 00 B4 00 05",
                "05 00 06 19 00 D2 00 06",
                "05 00 07 91 00 D2 00 07",
            };
            var green = ColorOf(SteelSeriesFamily.Rival600, 0x00FF00);
            Assert.Equal(8, green.Length);
            for (int led = 0; led < 8; led++)
            {
                Assert.Equal(SteelSeriesReport.Feature, green[led].Report);
                // LED 7's duration is 10 27 in the capture, the others' BE 14.
                SameAsCaptured(heads[led] + " " + (led == 7 ? "10 27" + tail.Substring(5) : tail), green[led]);
                Assert.Equal(50, green[led].Buffer.Length);
            }

            // Another color moves the start color and the last point alone.
            var color = Color(SteelSeriesFamily.Rival600);
            Same("00 05 00 00 55 00 46 00 00 BE 14 00*14 01 00 00 00 00 04 12 34 56 FF 00 00 00 00 FF 00 54 00 00 FF 54 12 34 56 56",
                SteelSeriesReport.Feature, color[0]);
            Assert.Empty(Claim(SteelSeriesFamily.Rival600));
            Assert.Empty(HandBack(SteelSeriesFamily.Rival600));
        }

        /// <summary>rice src/command/commands.rs:22-38 for the Rival 700
        /// and Aurora RivalMouses.cs:151-189 for the Rival 500: the LED,
        /// the color, FF 32 C8 C8 00, the LED again and 01. An owner's
        /// decode of the Engine's feature report gives the same layout
        /// (rivalcfg issue 138, comment 5380515397).</summary>
        [Fact]
        public void ARival700_RepeatsTheLedInFrontOfTheSteadyMode()
        {
            var color = Color(SteelSeriesFamily.Rival700);
            Assert.Equal(2, color.Length);
            Same("00 05 00 00 12 34 56 FF 32 C8 C8 00 00 01", SteelSeriesReport.Feature, color[0]);
            Same("00 05 00 01 12 34 56 FF 32 C8 C8 00 01 01", SteelSeriesReport.Feature, color[1]);
            Assert.Empty(Claim(SteelSeriesFamily.Rival700));
            Assert.Empty(HandBack(SteelSeriesFamily.Rival700));
        }

        /// <summary>OpenRGB SteelSeriesAeroxWirelessController.cpp:187-194:
        /// 13, the wheel's LED 01 and the color, in one output report, with
        /// no claim and no hand-back, since OpenRGB sends this mouse no
        /// effect and no save (158-174 and 235-252).</summary>
        [Fact]
        public void ARival3Wireless_GetsItsColorInOneReport_AsOpenRgbSendsIt()
        {
            var color = Color(SteelSeriesFamily.Rival3Wireless);
            Same("00 13 01 12 34 56", SteelSeriesReport.Output, Assert.Single(color));
            Assert.Empty(Claim(SteelSeriesFamily.Rival3Wireless));
            Assert.Empty(HandBack(SteelSeriesFamily.Rival3Wireless));
        }

        /// <summary>signal-plugins Steelseries_Rival_650_Mouse.js:84-131: per
        /// LED, the program with four points, three of them left at zero,
        /// and the bind of that LED, 10 to 17.</summary>
        [Fact]
        public void ARival650_GetsTheProgramAndTheBind_ForEachOfItsEightLeds()
        {
            var color = Color(SteelSeriesFamily.Rival650);
            Assert.Equal(16, color.Length);
            for (int led = 0; led < 8; led++)
            {
                Same("00 03 00 00 00 30 00 10 27 00*14 01 00*7 04 12 34 56 00*12 12 34 56",
                    SteelSeriesReport.Output, color[led * 2]);
                Same($"00 05 00 {0x10 + led:X2} FF 00 00 00 00 5C", SteelSeriesReport.Output, color[led * 2 + 1]);
            }
            Assert.Equal(50, color[0].Buffer.Length);
            Assert.Empty(Claim(SteelSeriesFamily.Rival650));
            Assert.Empty(HandBack(SteelSeriesFamily.Rival650));
        }

        /// <summary>GG's frame for a Rival 3 Gen 2: 21, the mask 07 and
        /// three colors (OpenRGB issue 4917,
        /// steelseries_rival3gen2_redcapture.pcapng, an output report to
        /// interface 3). The firmware stores each masked zone's color and
        /// clears it on 22 (rival_re input/decomp.c:3606-3651), and 22 07
        /// is rivalcfg's rainbow effect on every zone
        /// (devices/rival3_gen2.py:97-116).</summary>
        [Fact]
        public void ARival3Gen2_TakesThreeZonesInOneReport_AndIsHandedBackWithTheSameMask()
        {
            Same("00 21 07 12 34 56 12 34 56 12 34 56", SteelSeriesReport.Output,
                Assert.Single(Color(SteelSeriesFamily.Rival3Gen2)));
            Same("00 22 07", SteelSeriesReport.Output, Assert.Single(HandBack(SteelSeriesFamily.Rival3Gen2)));
            Assert.Empty(Claim(SteelSeriesFamily.Rival3Gen2));

            SameAsCaptured("21 07 FF 00 00 FF 00 00 FF 00 00",
                Assert.Single(ColorOf(SteelSeriesFamily.Rival3Gen2, 0xFF0000)));
        }

        /// <summary>GG's three reports for an Aerox 3, one zone each
        /// (OpenRGB issue 2492, Steady_All_Zones__Color_ac_ef_ac_.pcapng,
        /// output reports to interface 3), which are rivalcfg's
        /// (devices/aerox3.py:44-70, devices/aerox5.py:44-70). 22 07 is its
        /// rainbow effect (devices/aerox3.py:91-110).</summary>
        [Fact]
        public void AnAerox3_TakesOneReportPerZone_AndIsHandedBackWith2207()
        {
            var color = Color(SteelSeriesFamily.Aerox3);
            Assert.Equal(3, color.Length);
            Same("00 21 01 12 34 56", SteelSeriesReport.Output, color[0]);
            Same("00 21 02 00 00 00 12 34 56", SteelSeriesReport.Output, color[1]);
            Same("00 21 04 00 00 00 00 00 00 12 34 56", SteelSeriesReport.Output, color[2]);
            Same("00 22 07", SteelSeriesReport.Output, Assert.Single(HandBack(SteelSeriesFamily.Aerox3)));
            Assert.Empty(Claim(SteelSeriesFamily.Aerox3));

            var captured = ColorOf(SteelSeriesFamily.Aerox3, 0xACEFAC);
            SameAsCaptured("21 01 AC EF AC", captured[0]);
            SameAsCaptured("21 02 00 00 00 AC EF AC", captured[1]);
            SameAsCaptured("21 04 00 00 00 00 00 00 AC EF AC", captured[2]);
        }

        /// <summary>GG's frame for a Rival 5: 21, the mask FF 03 and ten
        /// colors (OpenRGB issue 1783, rival5_White.pcapng, and issue 2437
        /// for the Destiny Edition, output reports to interface 0).
        /// 22 FF 03 is rivalcfg's rainbow effect
        /// (devices/rival5.py:256-263).</summary>
        [Fact]
        public void ARival5_TakesTenLedsInOneReport_AndIsHandedBackWith22FF03()
        {
            Same("00 21 FF 03 " + string.Join(" ", Enumerable.Repeat("12 34 56", 10)), SteelSeriesReport.Output,
                Assert.Single(Color(SteelSeriesFamily.Rival5)));
            Same("00 22 FF 03", SteelSeriesReport.Output, Assert.Single(HandBack(SteelSeriesFamily.Rival5)));
            Assert.Empty(Claim(SteelSeriesFamily.Rival5));

            SameAsCaptured("21 FF 03 FF*30", Assert.Single(ColorOf(SteelSeriesFamily.Rival5, 0xFFFFFF)));
        }

        /// <summary>rivalcfg devices/prime_mini.py:44-52.</summary>
        [Fact]
        public void APrimeMini_TakesOneColorReport_AndHasNoHandBack()
        {
            Same("00 21 00 12 34 56", SteelSeriesReport.Output, Assert.Single(Color(SteelSeriesFamily.PrimeMini)));
            Assert.Empty(Claim(SteelSeriesFamily.PrimeMini));
            Assert.Empty(HandBack(SteelSeriesFamily.PrimeMini));
        }

        /// <summary>rivalcfg devices/aerox3_wireless_wired.py:53-79 for the
        /// zones and 141-148 for the rainbow effect, and
        /// devices/aerox3_wireless_wireless.py:3-16 for the dongle, which
        /// sets bit 6 of the command. GG sends a dongle exactly these three
        /// (OpenRGB issue 4048, SteelseriesAerox5Wireless_Red.pcapng,
        /// output reports to interface 3).</summary>
        [Fact]
        public void AWirelessAerox_TakesOneIndexedReportPerZone_OnItsCableAndThroughItsDongle()
        {
            var cable = Color(SteelSeriesFamily.AeroxWireless);
            Assert.Equal(3, cable.Length);
            Same("00 21 01 00 12 34 56", SteelSeriesReport.Output, cable[0]);
            Same("00 21 01 01 12 34 56", SteelSeriesReport.Output, cable[1]);
            Same("00 21 01 02 12 34 56", SteelSeriesReport.Output, cable[2]);
            Same("00 22 FF", SteelSeriesReport.Output, Assert.Single(HandBack(SteelSeriesFamily.AeroxWireless)));

            var dongle = Color(SteelSeriesFamily.AeroxWirelessDongle);
            Assert.Equal(3, dongle.Length);
            Same("00 61 01 00 12 34 56", SteelSeriesReport.Output, dongle[0]);
            Same("00 61 01 01 12 34 56", SteelSeriesReport.Output, dongle[1]);
            Same("00 61 01 02 12 34 56", SteelSeriesReport.Output, dongle[2]);
            Same("00 62 FF", SteelSeriesReport.Output, Assert.Single(HandBack(SteelSeriesFamily.AeroxWirelessDongle)));
            Assert.Empty(Claim(SteelSeriesFamily.AeroxWireless));
            Assert.Empty(Claim(SteelSeriesFamily.AeroxWirelessDongle));

            var captured = ColorOf(SteelSeriesFamily.AeroxWirelessDongle, 0xFF0000);
            SameAsCaptured("61 01 00 FF 00 00", captured[0]);
            SameAsCaptured("61 01 01 FF 00 00", captured[1]);
            SameAsCaptured("61 01 02 FF 00 00", captured[2]);
        }

        /// <summary>rivalcfg devices/prime_wireless_wired.py:53-61 and
        /// devices/prime_wireless_wireless.py:3-16, which has no rainbow
        /// command for these mice.</summary>
        [Fact]
        public void AWirelessPrime_TakesOneReport_AndHasNoHandBack()
        {
            Same("00 21 01 00 12 34 56", SteelSeriesReport.Output, Assert.Single(Color(SteelSeriesFamily.PrimeWireless)));
            Same("00 61 01 00 12 34 56", SteelSeriesReport.Output, Assert.Single(Color(SteelSeriesFamily.PrimeWirelessDongle)));
            foreach (var family in new[] { SteelSeriesFamily.PrimeWireless, SteelSeriesFamily.PrimeWirelessDongle })
            {
                Assert.Empty(Claim(family));
                Assert.Empty(HandBack(family));
            }
        }

        /// <summary>GG's frame for a Prime+: 62 01, the color, twelve
        /// zeros, the color and FF (OpenRGB issue 4354, primered.pcap and
        /// primesteady.pcap, a feature report of 642 bytes to interface 0).
        /// The Prime takes the same bytes as an output report (rivalcfg
        /// devices/prime.py:56-74).</summary>
        [Fact]
        public void APrime_TakesTheVendorsFrame_AsAFeatureReportOnThePlusAndAnOutputReportElsewhere()
        {
            const string frame = "00 62 01 12 34 56 00*12 12 34 56 FF";
            Same(frame, SteelSeriesReport.Feature, Assert.Single(Color(SteelSeriesFamily.PrimePlus)));
            Same(frame, SteelSeriesReport.Output, Assert.Single(Color(SteelSeriesFamily.Prime)));
            // Each takes the other type where its collection lacks the first.
            Same(frame, SteelSeriesReport.Output,
                Assert.Single(Color(SteelSeriesFamily.PrimePlus, report: SteelSeriesReport.Output)));
            Same(frame, SteelSeriesReport.Feature,
                Assert.Single(Color(SteelSeriesFamily.Prime, report: SteelSeriesReport.Feature)));
            foreach (var family in new[] { SteelSeriesFamily.PrimePlus, SteelSeriesFamily.Prime })
            {
                Assert.Empty(Claim(family));
                Assert.Empty(HandBack(family));
            }

            SameAsCaptured("62 01 FF 00 00 00*12 FF 00 00 FF", Assert.Single(ColorOf(SteelSeriesFamily.PrimePlus, 0xFF0000)));
            SameAsCaptured("62 01 FF 52 00 00*12 FF 52 00 FF", Assert.Single(ColorOf(SteelSeriesFamily.PrimePlus, 0xFF5200)));
        }

        // ── The keyboards ──

        /// <summary>The 112 usages of the per-key frame in OpenRGB's order
        /// (SteelSeriesApexController.cpp:23-37), written out again here.</summary>
        private const string ApexKeys =
            "04 05 06 07 08 09 0A 0B 0C 0D 0E 0F 10 11 12 13 14 15 16 17 " +
            "18 19 1A 1B 1C 1D 1E 1F 20 21 22 23 24 25 26 27 28 29 2A 2B " +
            "2C 2D 2E 2F 30 32 33 34 35 36 37 38 39 3A 3B 3C 3D 3E 3F 40 " +
            "41 42 43 44 45 46 47 48 49 4A 4B 4C 4D 4E 4F 50 51 52 64 E0 " +
            "E1 E2 E3 E4 E5 E6 E7 F0 31 87 88 89 8A 8B 53 54 55 56 57 58 " +
            "59 5A 5B 5C 5D 5E 5F 60 61 62 63 FB";

        private static string PerKey(string packet)
            => "00 " + packet + " 70 " + string.Join(" ", ApexKeys.Split(' ').Select(key => key + " 12 34 56"));

        /// <summary>OpenRGB SteelSeriesApexController.cpp:128-200: the
        /// packet number, the key count, then usage and color per key, as a
        /// feature report. The packet is 3A on the 2019 boards, 40 on the
        /// 2023 and Apex 9 boards and 61 on the wireless ones
        /// (SteelSeriesApexBaseController.h:48-64). GG's own frames have
        /// this layout with the keys of the board alone: 3A 69 and 105
        /// keys on an Apex Pro Gen 3 (OpenRGB issue 4376), 40 54 and 84
        /// keys on an Apex 9 TKL (OpenRGB issue 3253).</summary>
        [Fact]
        public void APerKeyApex_GetsAll112KeysInOneColor_UnderItsModelsPacketNumber()
        {
            Assert.Equal(112, ApexKeys.Split(' ').Length);
            Assert.Equal(Text(Hex(ApexKeys)), Text(SteelSeriesDirectProtocol.ApexKeys));
            Assert.Equal(112, SteelSeriesDirectProtocol.ApexKeys.Distinct().Count());

            var packets = new (SteelSeriesFamily Family, bool Gen3, string Packet)[]
            {
                (SteelSeriesFamily.ApexPro, false, "3A"),
                (SteelSeriesFamily.ApexPro, true, "3A"),
                (SteelSeriesFamily.ApexProTkl, false, "3A"),
                (SteelSeriesFamily.ApexProTkl, true, "40"),
                (SteelSeriesFamily.Apex9, false, "40"),
                (SteelSeriesFamily.Apex9, true, "40"),
                (SteelSeriesFamily.ApexProTkl2023, false, "40"),
                (SteelSeriesFamily.ApexProTkl2023, true, "40"),
                (SteelSeriesFamily.ApexProTklGen3, false, "40"),
                (SteelSeriesFamily.ApexProWireless, false, "61"),
                (SteelSeriesFamily.ApexProGen3, false, "3A"),
                (SteelSeriesFamily.ApexProGen3, true, "3A"),
            };
            foreach (var (family, gen3, packet) in packets)
            {
                var frame = Assert.Single(Color(family, gen3));
                Same(PerKey(packet), SteelSeriesReport.Feature, frame);
                // 450 data bytes, which fits the shortest of these boards'
                // feature reports, the Apex 9's 512.
                Assert.Equal(451, frame.Buffer.Length);
            }

            // The head of GG's own frame on an Apex 9 TKL, with its key
            // count: 40 54 04 FF 00 00 05 FF 00 00 06 FF 00 00.
            var red = Assert.Single(ColorOf(SteelSeriesFamily.Apex9, 0xFF0000)).Buffer;
            Assert.Equal(Text(Hex("40 70 04 FF 00 00 05 FF 00 00 06 FF 00 00")), Text(red.AsSpan(1, 14).ToArray()));
        }

        /// <summary>OpenRGB SteelSeriesApexController.cpp:96-110 and
        /// 255-275: a board of the third generation is claimed with the
        /// feature report 4B and handed back with the output report 41, an
        /// older one takes no claim and is handed back with 3B.</summary>
        [Fact]
        public void AnApexsClaimAndHandBack_FollowItsGeneration()
        {
            // The 2019 boards, whatever a firmware reply said.
            foreach (bool gen3 in new[] { false, true })
            {
                Assert.Empty(Claim(SteelSeriesFamily.ApexPro, gen3));
                Same("00 3B", SteelSeriesReport.Output, Assert.Single(HandBack(SteelSeriesFamily.ApexPro, gen3)));
            }

            // The boards a firmware update moved to the third generation.
            foreach (var family in new[] { SteelSeriesFamily.ApexProTkl, SteelSeriesFamily.Apex9, SteelSeriesFamily.ApexProTkl2023 })
            {
                Assert.True(SteelSeriesDirectProtocol.Info(family).Gated);
                Assert.Empty(Claim(family, gen3: false));
                Same("00 3B", SteelSeriesReport.Output, Assert.Single(HandBack(family, gen3: false)));
                Same("00 4B", SteelSeriesReport.Feature, Assert.Single(Claim(family, gen3: true)));
                Same("00 41 00", SteelSeriesReport.Output, Assert.Single(HandBack(family, gen3: true)));
            }

            // The boards that are of the third generation from the start.
            foreach (var family in new[] { SteelSeriesFamily.ApexProTklGen3, SteelSeriesFamily.ApexProWireless })
            {
                foreach (bool gen3 in new[] { false, true })
                {
                    Same("00 4B", SteelSeriesReport.Feature, Assert.Single(Claim(family, gen3)));
                    Same("00 41 00", SteelSeriesReport.Output, Assert.Single(HandBack(family, gen3)));
                }
            }

            // The Apex Pro Gen 3: GG's own claim, the output report 41 01
            // (OpenRGB issue 4376, SteelSeriesAPEXProGen3-Red.pcapng,
            // packet 1821), and never 4B.
            foreach (bool gen3 in new[] { false, true })
            {
                Same("00 41 01", SteelSeriesReport.Output, Assert.Single(Claim(SteelSeriesFamily.ApexProGen3, gen3)));
                Same("00 41 00", SteelSeriesReport.Output, Assert.Single(HandBack(SteelSeriesFamily.ApexProGen3, gen3)));
            }

            // Only the gated boards are asked for their version.
            Assert.Equal(new[] { SteelSeriesFamily.ApexProTkl, SteelSeriesFamily.Apex9, SteelSeriesFamily.ApexProTkl2023 },
                SteelSeriesDirectProtocol.Families.Where(f => f.Gated).Select(f => f.Family));
            Assert.Equal(new ushort[] { 0x1614, 0x1620, 0x1628, 0x1634 },
                SteelSeriesDirectProtocol.Models.Where(m => SteelSeriesDirectProtocol.Info(m.Family)?.Gated == true)
                    .Select(m => m.ProductId).OrderBy(id => id));
        }

        /// <summary>GG's frame for an Apex 3: 0B 00 and ten colors, which
        /// fill its 32-byte report (OpenRGB issue 1644, saving-packet.pcapng,
        /// an output report to interface 3).</summary>
        [Fact]
        public void AnApex3_TakesTenZonesInOneReport_ThatTheFrameFills()
        {
            var color = Assert.Single(Color(SteelSeriesFamily.Apex3));
            Same("00 0B 00 " + string.Join(" ", Enumerable.Repeat("12 34 56", 10)), SteelSeriesReport.Output, color);
            Assert.Equal(33, color.Buffer.Length);
            Assert.Empty(Claim(SteelSeriesFamily.Apex3));
            Assert.Empty(HandBack(SteelSeriesFamily.Apex3));

            SameAsCaptured("0B 00 " + string.Join(" ", Enumerable.Repeat("FF 52 00", 10)),
                Assert.Single(ColorOf(SteelSeriesFamily.Apex3, 0xFF5200)));
        }

        /// <summary>GG's frame for an Apex 3 TKL: 21 FF and eight colors
        /// (OpenRGB issue 1902, apex_3_windows_gg_colors.pcapng, an output
        /// report to interface 1). 22 FF is its rainbow wave (OpenRGB
        /// SteelSeriesApex8ZoneController.h:75-79).</summary>
        [Fact]
        public void AnApex3Tkl_TakesEightZonesInOneReport_AndIsHandedBackWith22FF()
        {
            Same("00 21 FF " + string.Join(" ", Enumerable.Repeat("12 34 56", 8)), SteelSeriesReport.Output,
                Assert.Single(Color(SteelSeriesFamily.Apex3Tkl)));
            Same("00 22 FF", SteelSeriesReport.Output, Assert.Single(HandBack(SteelSeriesFamily.Apex3Tkl)));
            Assert.Empty(Claim(SteelSeriesFamily.Apex3Tkl));

            SameAsCaptured("21 FF " + string.Join(" ", Enumerable.Repeat("FF 52 00", 8)),
                Assert.Single(ColorOf(SteelSeriesFamily.Apex3Tkl, 0xFF5200)));
        }

        /// <summary>The Apex M750's grid as its frame carries it, six rows
        /// of 22 slots from the frame's first slot on: K a slot with a key,
        /// a dot one with none. Written from the Engine's own frames (the
        /// captures of OpenRGB issue 641), where the dotted slots hold
        /// FF 32 00 and the others the color of their key, and equal to
        /// the NA entries of OpenRGB's table
        /// (SteelSeriesApexMController.cpp:18-37).</summary>
        private static readonly string[] M750Grid =
        {
            "KKK.K....KKKK..KKK.K.K",
            "KKKKKKKKKKK.K...K.KKKK",
            "KKKKKKKKKKKK.K....KKK.",
            "KKKKKKKKKKKKK.KKKKKKKK",
            "KKKKKKKKKKKKK.KKKKKKKK",
            "KKKKK.KKKK.KKKKKKK....",
        };

        private static string M750Frame(string color)
            => "00 00 01 8E 01 03 06 16 " + string.Join(" ", string.Concat(M750Grid).Select(slot => slot == 'K' ? color : "FF 32 00"));

        /// <summary>OpenRGB SteelSeriesApexMController.cpp:50-128 and
        /// signal-plugins Steelseries_Apex_M750_Keyboard.js:50-77: three
        /// feature reports, the third still carrying the FF of the second,
        /// then the head 00 00 01 8E 01 03 06 16 and three bytes for each
        /// of the 132 slots: the color where the slot has a key, and
        /// FF 32 00 where it has none, which is byte for byte the frame the
        /// Engine relays (OpenRGB issue 641,
        /// steelseries_complete_illumination_aurora.pcapng, a feature
        /// report of 512 bytes to interface 2, with 40 40 40 on every key).</summary>
        [Fact]
        public void AnApexM750_IsClaimedWithThreeFeatureReports_ThenGetsTheVendorsGrid()
        {
            var claim = Claim(SteelSeriesFamily.ApexM750);
            Assert.Equal(3, claim.Length);
            Same("00 00 00 00 01 00 85", SteelSeriesReport.Feature, claim[0]);
            Same("00 00 00 00 03 01 00 FF", SteelSeriesReport.Feature, claim[1]);
            Same("00 00 00 00 01 00 85 FF", SteelSeriesReport.Feature, claim[2]);

            Assert.Equal(132, SteelSeriesDirectProtocol.M750Slots);
            Assert.Equal(132, string.Concat(M750Grid).Length);
            Assert.Equal(28, string.Concat(M750Grid).Count(slot => slot == '.'));
            Assert.Equal(Enumerable.Range(0, 132).Where(slot => string.Concat(M750Grid)[slot] == '.').Select(slot => (byte)slot),
                SteelSeriesDirectProtocol.M750NoKey);

            var color = Assert.Single(Color(SteelSeriesFamily.ApexM750));
            Same("00 " + M750Frame("12 34 56"), SteelSeriesReport.Feature, color);
            Assert.Equal(405, color.Buffer.Length);
            Assert.Empty(HandBack(SteelSeriesFamily.ApexM750));

            SameAsCaptured(M750Frame("40 40 40"), Assert.Single(ColorOf(SteelSeriesFamily.ApexM750, 0x404040)));
            // A color that is the filler's own leaves nothing to tell apart,
            // and black leaves the filler alone.
            var black = Assert.Single(ColorOf(SteelSeriesFamily.ApexM750, 0x000000)).Buffer;
            Assert.Equal(28, Enumerable.Range(0, 132).Count(slot => black[9 + slot * 3] == 0xFF && black[10 + slot * 3] == 0x32));
        }

        /// <summary>OpenRGB SteelSeriesOldApexController.cpp:85-124 and
        /// ApexCtl src/Main.hs:50-58: 07 00, then a color and the level 08
        /// for each of the five zones.</summary>
        [Fact]
        public void AnApexOf2013_GivesEachOfItsFiveZonesTheColorAtLevel08()
        {
            Same("00 07 00 " + string.Join(" ", Enumerable.Repeat("12 34 56 08", 5)), SteelSeriesReport.Output,
                Assert.Single(Color(SteelSeriesFamily.Apex2013)));
            Assert.Empty(Claim(SteelSeriesFamily.Apex2013));
            Assert.Empty(HandBack(SteelSeriesFamily.Apex2013));
        }

        // ── The firmware gate ──

        /// <summary>The reply to 90 is 90 00 and the version as text,
        /// 31 2E 31 39 2E 37 for 1.19.7 (apex-web PROTOCOL.md:182-190), and
        /// the third generation starts there (OpenRGB
        /// SteelSeriesApexController.cpp:219-253).</summary>
        [Theory]
        [InlineData("90 00 31 2E 31 39 2E 37", true)]             // 1.19.7
        [InlineData("90 00 31 2E 31 39 2E 37 00*56", true)]       // the whole 64-byte report
        [InlineData("90 00 31 2E 31 39 2E 36", false)]            // 1.19.6
        [InlineData("90 00 31 2E 31 39 2E 31 30", true)]          // 1.19.10
        [InlineData("90 00 31 2E 32 30 2E 30", true)]             // 1.20.0
        [InlineData("90 00 31 2E 31 38 2E 39", false)]            // 1.18.9
        [InlineData("90 00 32 2E 30 2E 30", true)]                // 2.0.0
        [InlineData("90 00 30 2E 33 37 2E 30", false)]            // 0.37.0
        [InlineData("90 00 31 2E 31 39 2E 37 20 2B 61", true)]    // 1.19.7 +a
        [InlineData("90 00 31 2E 31 39", false)]                  // two parts
        [InlineData("90 00 31 2E 2E 37", false)]                  // an empty part
        [InlineData("90 00", false)]                              // no text
        [InlineData("90", false)]
        [InlineData("", false)]
        [InlineData("21 00 31 2E 31 39 2E 37", false)]            // another command's reply
        [InlineData("90 00 41 42 43", false)]                     // no version
        [InlineData("90 00 39 39 39 39 39 39 39 2E 31 2E 31", false)]   // no version part is that long
        [InlineData("90 31 2E 31 39 2E 37", true)]                // no byte between the 90 and the text
        [InlineData("31 2E 31 39 2E 37", true)]                   // 1.19.7 with no 90 in front
        [InlineData("31 2E 31 39 2E 36 00*58", false)]            // 1.19.6 the same way
        [InlineData("20 20 32 2E 30 2E 30", true)]                // 2.0.0 behind spaces
        public void AFirmwareReply_IsOfTheThirdGeneration_From1_19_7(string reply, bool gen3)
            => Assert.Equal(gen3, SteelSeriesDirectProtocol.IsGen3(Hex(reply)));

        /// <summary>OpenRGB drops the first byte of the reply where it is
        /// the echoed 90 and reads the reply from its first byte where it is
        /// not (SteelSeriesApexBaseController.cpp:114-127), so either shape
        /// is a version reply. Any other input report is not, and the
        /// worker reads past it.</summary>
        [Theory]
        [InlineData("90 00 31 2E 31 39 2E 37", true)]
        [InlineData("90 00", true)]                               // the echo with no text
        [InlineData("90", true)]
        [InlineData("31 2E 31 38 2E 30", true)]                   // 1.18.0 with no 90 in front
        [InlineData("A3 00 05", false)]                           // the reply to a brightness read
        [InlineData("31 2E 31 38", false)]                        // no whole version, no 90
        [InlineData("00 00 00", false)]
        [InlineData("", false)]
        public void AnInputReport_IsAVersionReply_WhenItEchoes90OrReadsAsAVersion(string report, bool isReply)
            => Assert.Equal(isReply, SteelSeriesDirectProtocol.IsVersionReply(Hex(report)));

        [Fact]
        public void TheVersionQuery_IsTheOutputReport90()
        {
            Same("00 90", SteelSeriesReport.Output, SteelSeriesDirectProtocol.VersionQuery());
            Assert.Equal(0x90, SteelSeriesDirectProtocol.VersionCommand);
        }

        // ── What no builder may produce ──

        /// <summary>The first data byte each family may send. Anything else
        /// is another family's command, and several of those do harm on the
        /// wrong firmware.</summary>
        private static readonly Dictionary<SteelSeriesFamily, byte[]> Allowed = new()
        {
            [SteelSeriesFamily.Rival100] = new byte[] { 0x07, 0x05 },
            [SteelSeriesFamily.Rival300] = new byte[] { 0x07, 0x08 },
            [SteelSeriesFamily.Rival3] = new byte[] { 0x0A },
            [SteelSeriesFamily.Rival310] = new byte[] { 0x5B },
            [SteelSeriesFamily.Rival600] = new byte[] { 0x05 },
            [SteelSeriesFamily.Rival700] = new byte[] { 0x05 },
            [SteelSeriesFamily.Rival3Wireless] = new byte[] { 0x13 },
            [SteelSeriesFamily.Rival650] = new byte[] { 0x03, 0x05 },
            [SteelSeriesFamily.Rival3Gen2] = new byte[] { 0x21, 0x22 },
            [SteelSeriesFamily.Aerox3] = new byte[] { 0x21, 0x22 },
            [SteelSeriesFamily.Rival5] = new byte[] { 0x21, 0x22 },
            [SteelSeriesFamily.PrimeMini] = new byte[] { 0x21 },
            [SteelSeriesFamily.AeroxWireless] = new byte[] { 0x21, 0x22 },
            [SteelSeriesFamily.AeroxWirelessDongle] = new byte[] { 0x61, 0x62 },
            [SteelSeriesFamily.PrimeWireless] = new byte[] { 0x21 },
            [SteelSeriesFamily.PrimeWirelessDongle] = new byte[] { 0x61 },
            [SteelSeriesFamily.PrimePlus] = new byte[] { 0x62 },
            [SteelSeriesFamily.Prime] = new byte[] { 0x62 },
            [SteelSeriesFamily.ApexPro] = new byte[] { 0x3A, 0x3B },
            [SteelSeriesFamily.ApexProTkl] = new byte[] { 0x3A, 0x40, 0x4B, 0x3B, 0x41 },
            [SteelSeriesFamily.Apex9] = new byte[] { 0x40, 0x4B, 0x3B, 0x41 },
            [SteelSeriesFamily.ApexProTkl2023] = new byte[] { 0x40, 0x4B, 0x3B, 0x41 },
            [SteelSeriesFamily.ApexProTklGen3] = new byte[] { 0x40, 0x4B, 0x41 },
            [SteelSeriesFamily.ApexProWireless] = new byte[] { 0x61, 0x4B, 0x41 },
            [SteelSeriesFamily.ApexProGen3] = new byte[] { 0x3A, 0x41 },
            [SteelSeriesFamily.Apex3] = new byte[] { 0x0B },
            [SteelSeriesFamily.Apex3Tkl] = new byte[] { 0x21, 0x22 },
            [SteelSeriesFamily.ApexM750] = new byte[] { 0x00 },
            [SteelSeriesFamily.Apex2013] = new byte[] { 0x07 },
            [SteelSeriesFamily.Arctis5] = new byte[] { 0x81, 0x8A },
            [SteelSeriesFamily.ArctisNova3] = new byte[] { 0xAA, 0xA5, 0x09, 0xA3 },
            [SteelSeriesFamily.Siberia350] = new byte[] { 0x00 },
            [SteelSeriesFamily.QckPrismCloth] = new byte[] { 0x0E, 0x0D },
            [SteelSeriesFamily.QckPrism12] = new byte[] { 0x0E, 0x0D },
            [SteelSeriesFamily.ArctisProGameDac] = new byte[] { 0x2C, 0xAA, 0xA5, 0xA3 },
            [SteelSeriesFamily.ArenaSpeakers] = new byte[] { 0xA7 },
        };

        /// <summary>The report ID each family's frames carry: 06 on the Arctis
        /// headsets (OpenRGB SteelSeriesArctis5Controller.h:159,
        /// SteelSeriesArctisNova3Controller.h:19), 01 on the Siberia 350
        /// (SteelSeriesSiberiaController.cpp:21-22), none, 00, on every
        /// other.</summary>
        private static byte ReportIdOf(SteelSeriesFamily family) => family switch
        {
            SteelSeriesFamily.Arctis5 or SteelSeriesFamily.ArctisNova3 or SteelSeriesFamily.ArenaSpeakers => 0x06,
            SteelSeriesFamily.Siberia350 => 0x01,
            _ => 0x00,
        };

        /// <summary>Every frame every family can build, on either firmware
        /// generation and as either report type.</summary>
        private static IEnumerable<(SteelSeriesFamilyInfo Info, string Part, SteelSeriesFrame Frame)> EveryFrame()
        {
            foreach (var info in SteelSeriesDirectProtocol.Families)
            {
                foreach (bool gen3 in new[] { false, true })
                {
                    foreach (var frame in info.Claim(gen3)) yield return (info, "claim", frame);
                    foreach (var frame in info.HandBack(gen3)) yield return (info, "hand-back", frame);
                    foreach (var report in new[] { SteelSeriesReport.Output, SteelSeriesReport.Feature })
                        foreach (var frame in info.Color(report, gen3, R, G, B)) yield return (info, "color", frame);
                }
            }
        }

        /// <summary>No builder produces a command that saves to the
        /// device's memory, resets it, or changes a setting of the user's.
        /// 09, 59, 11 and 51 save (rivalcfg devices/rival100.py:131-134,
        /// devices/rival310.py:131-134, devices/aerox3.py:155-158). 06 is
        /// the first half of the Apex 3's save pair (OpenRGB
        /// SteelSeriesApexTZoneController.cpp:45-54). 01 resets a Rival 3
        /// Gen 2 and 13 is its factory reset (rival_re
        /// tools/rival/protocol.py:42-53), and 01 01 puts an Apex 3 TKL in
        /// its bootloader (apex-3-tkl docs/internals.md:63). 13 is also the
        /// Rival 3 Wireless's color (OpenRGB
        /// SteelSeriesAeroxWirelessController.cpp:187-194), so that family
        /// alone sends it.
        /// 23 and 63 are the brightness, 2D and 6D the sensitivity presets,
        /// 26 the reactive color and 27 the default lighting
        /// (devices/aerox3.py:15-21, 71-90 and 139-153). E8 is a report of
        /// unknown meaning that only SignalRGB sends a Rival 3 Wireless.
        /// 08 erases a flash page on the Rival 3 Gen 2 firmware (rival_re
        /// input/decomp.c:3868-3885) and is the Rival 300's color alone,
        /// 0A is the Apex 3's brightness and the Rival 3's color alone, and
        /// 61 is the screen image of an Apex Pro Gen 3. The headsets'
        /// reports carry a report ID of their own, so their bytes are in
        /// another numbering, and only their own lists hold them. The Arctis
        /// Nova 3's 09 is its right LED's apply report, which OpenRGB also
        /// saves with (SteelSeriesArctisNova3Controller.cpp:84-96 and
        /// 112-115), the one exception the protocol's comment names.</summary>
        [Fact]
        public void NoBuilderEverProducesASaveAResetOrASetting()
        {
            byte[] never = { 0x09, 0x59, 0x11, 0x51, 0x06, 0x01, 0x23, 0x63, 0x2D, 0x6D, 0x26, 0x27, 0xE8, 0x90 };
            int frames = 0;
            foreach (var (info, part, frame) in EveryFrame())
            {
                frames++;
                Assert.True(frame.Buffer.Length >= 2, $"{info.Family} {part}");
                Assert.Equal(ReportIdOf(info.Family), frame.Buffer[0]);
                Assert.Equal(frame.Buffer[1], frame.Command);
                if (frame.Buffer[0] == 0x00)
                    Assert.False(never.Contains(frame.Command), $"{info.Family} {part} sends {frame.Command:X2}");
                Assert.True(Allowed[info.Family].Contains(frame.Command),
                    $"{info.Family} {part} sends {frame.Command:X2}, which is not one of its commands");
                if (frame.Command == 0x08) Assert.Equal(SteelSeriesFamily.Rival300, info.Family);
                if (frame.Command == 0x0A) Assert.Equal(SteelSeriesFamily.Rival3, info.Family);
                if (frame.Command == 0x13) Assert.Equal(SteelSeriesFamily.Rival3Wireless, info.Family);
                if (frame.Command == 0x61) Assert.NotEqual(SteelSeriesFamily.ApexProGen3, info.Family);
            }
            Assert.True(frames > 100, "every family was walked");
            // Every family has its list, and every listed command is one a
            // builder can produce, so the list holds nothing stale.
            Assert.Equal(SteelSeriesDirectProtocol.Families.Select(f => f.Family).OrderBy(f => f), Allowed.Keys.OrderBy(f => f));
            foreach (var pair in Allowed)
                Assert.Equal(pair.Value.OrderBy(c => c),
                    EveryFrame().Where(f => f.Info.Family == pair.Key).Select(f => f.Frame.Command).Distinct().OrderBy(c => c));
        }

        /// <summary>The crash path writes a hand-back through a handle of
        /// its own with one bounded output write, so every hand-back frame
        /// is an output report, and every claim that is a feature report
        /// has a hand-back behind it.</summary>
        [Fact]
        public void EveryHandBackIsAnOutputReport()
        {
            foreach (var info in SteelSeriesDirectProtocol.Families)
                foreach (bool gen3 in new[] { false, true })
                    Assert.All(info.HandBack(gen3), frame => Assert.Equal(SteelSeriesReport.Output, frame.Report));
        }

        /// <summary>A builder hands out bytes of its own on every call, so
        /// a caller that keeps a frame never sees another call's color in
        /// it.</summary>
        [Fact]
        public void EveryCallBuildsItsOwnBytes()
        {
            foreach (var info in SteelSeriesDirectProtocol.Families)
            {
                foreach (bool gen3 in new[] { false, true })
                {
                    Check(() => info.Claim(gen3));
                    Check(() => info.HandBack(gen3));
                    Check(() => info.Color(info.Report, gen3, R, G, B));
                }
            }
            static void Check(Func<SteelSeriesFrame[]> build)
            {
                var first = build();
                var second = build();
                Assert.Equal(first.Length, second.Length);
                for (int i = 0; i < first.Length; i++)
                {
                    Assert.NotSame(first[i].Buffer, second[i].Buffer);
                    Assert.Equal(first[i].Buffer, second[i].Buffer);
                }
            }
        }

        // ── The family table ──

        /// <summary>The families whose references send a color's reports
        /// with no wait between them.</summary>
        private static readonly SteelSeriesFamily[] BackToBack =
        {
            SteelSeriesFamily.Arctis5, SteelSeriesFamily.ArctisNova3, SteelSeriesFamily.Siberia350,
            SteelSeriesFamily.QckPrismCloth,
            SteelSeriesFamily.QckPrism12,
            SteelSeriesFamily.ArenaSpeakers,
        };

        /// <summary>How fast each family takes its reports: the gap between
        /// two reports of one paint, the fastest repaint, the re-assert
        /// interval, and whether each report is answered. A figure is the
        /// vendor's own where a capture of it exists and a reference
        /// tool's wait where none does, the longer one where two tools
        /// differ (rivalcfg's 50 ms over libratbag's 10 on the Rival 100
        /// and 300, and on the Prime mice and the Rival 3 Wireless no
        /// capture gives a rate for). Every family has a re-assert
        /// interval but the Arctis Nova 3, whose color ends with the report
        /// OpenRGB saves it with.</summary>
        [Theory]
        [InlineData(SteelSeriesFamily.Rival100, 0, 1, 50, 50, 5000, false)]
        [InlineData(SteelSeriesFamily.Rival300, 0, 2, 50, 100, 5000, false)]
        [InlineData(SteelSeriesFamily.Rival3, 3, 1, 0, 33, 5000, false)]
        [InlineData(SteelSeriesFamily.Rival310, 0, 2, 24, 66, 5000, false)]
        [InlineData(SteelSeriesFamily.Rival600, 0, 8, 22, 200, 5000, false)]
        [InlineData(SteelSeriesFamily.Rival700, 0, 2, 50, 100, 5000, false)]
        [InlineData(SteelSeriesFamily.Rival3Wireless, 3, 1, 50, 50, 5000, false)]
        [InlineData(SteelSeriesFamily.Rival650, 0, 16, 60, 1000, 30000, false)]
        [InlineData(SteelSeriesFamily.Rival3Gen2, 3, 1, 0, 33, 5000, false)]
        [InlineData(SteelSeriesFamily.Aerox3, 3, 3, 16, 50, 5000, false)]
        [InlineData(SteelSeriesFamily.Rival5, 0, 1, 0, 33, 5000, false)]
        [InlineData(SteelSeriesFamily.PrimeMini, 3, 1, 0, 50, 5000, false)]
        [InlineData(SteelSeriesFamily.AeroxWireless, 3, 3, 16, 50, 5000, false)]
        [InlineData(SteelSeriesFamily.AeroxWirelessDongle, 3, 3, 6, 50, 5000, true)]
        [InlineData(SteelSeriesFamily.PrimeWireless, 3, 1, 0, 50, 5000, false)]
        [InlineData(SteelSeriesFamily.PrimeWirelessDongle, 3, 1, 0, 50, 5000, true)]
        [InlineData(SteelSeriesFamily.PrimePlus, 0, 1, 0, 50, 5000, false)]
        [InlineData(SteelSeriesFamily.Prime, 0, 1, 0, 33, 5000, false)]
        [InlineData(SteelSeriesFamily.ApexPro, 1, 1, 66, 66, 5000, false)]
        [InlineData(SteelSeriesFamily.ApexProTkl, 1, 1, 66, 66, 5000, false)]
        [InlineData(SteelSeriesFamily.Apex9, 1, 1, 66, 66, 5000, false)]
        [InlineData(SteelSeriesFamily.ApexProTkl2023, 1, 1, 66, 66, 5000, false)]
        [InlineData(SteelSeriesFamily.ApexProTklGen3, 1, 1, 66, 66, 5000, false)]
        [InlineData(SteelSeriesFamily.ApexProWireless, 3, 1, 66, 66, 5000, false)]
        [InlineData(SteelSeriesFamily.ApexProGen3, 1, 1, 66, 66, 5000, false)]
        [InlineData(SteelSeriesFamily.Apex3, 3, 1, 0, 50, 5000, false)]
        [InlineData(SteelSeriesFamily.Apex3Tkl, 1, 1, 0, 33, 5000, false)]
        [InlineData(SteelSeriesFamily.ApexM750, 2, 1, 10, 55, 5000, false)]
        [InlineData(SteelSeriesFamily.Apex2013, 0, 1, 0, 50, 5000, false)]
        [InlineData(SteelSeriesFamily.Arctis5, 5, 24, 0, 50, 5000, false)]
        [InlineData(SteelSeriesFamily.ArctisNova3, 4, 6, 0, 500, 0, false)]
        [InlineData(SteelSeriesFamily.Siberia350, 3, 4, 0, 500, 0, false)]
        [InlineData(SteelSeriesFamily.QckPrismCloth, 0, 2, 0, 33, 5000, false)]
        [InlineData(SteelSeriesFamily.QckPrism12, 0, 2, 0, 33, 5000, false)]
        [InlineData(SteelSeriesFamily.ArctisProGameDac, 0, 8, 20, 230, 5000, false)]
        [InlineData(SteelSeriesFamily.ArenaSpeakers, 4, 1, 0, 33, 5000, false)]
        public void EachFamily_SitsOnItsInterface_AndIsPacedAsItsReferencesPaceIt(object familyValue, int iface,
            int reports, int gapMs, int repaintMs, int reassertMs, bool echoes)
        {
            // The family comes boxed, since a public test method cannot name
            // an internal type in its signature.
            var family = (SteelSeriesFamily)familyValue;
            var info = SteelSeriesDirectProtocol.Info(family);
            Assert.NotNull(info);
            Assert.Equal(family, info.Family);
            Assert.Equal(iface, info.Interface);
            Assert.Equal(reports, info.Color(info.Report, false, R, G, B).Length);
            Assert.Equal(reports, info.Color(info.Report, true, R, G, B).Length);
            Assert.Equal(gapMs, info.GapMs);
            Assert.Equal(repaintMs, info.RepaintMs);
            Assert.Equal(reassertMs, info.ReassertMs);
            Assert.Equal(echoes, info.Echoes);
            // A family with several reports per paint, its claim counted,
            // has a gap to keep between them, but for the headsets and mats,
            // whose every reference sends the reports back to back (OpenRGB
            // SteelSeriesArctis5Controller.cpp:66-139,
            // SteelSeriesArctisNova3Controller.cpp:144-147,
            // SteelSeriesSiberiaController.cpp:82-104,
            // SteelSeriesQCKMatController.cpp:96-99, and SignalRGB the
            // Arctis 5's and the mats' on every render).
            if (BackToBack.Contains(family)) Assert.Equal(0, info.GapMs);
            else if (reports + info.Claim(true).Length > 1) Assert.True(info.GapMs > 0, "a gap between its reports");
            // One whole color, at its gaps, fits inside the fastest repaint,
            // so a color is never started before the last one is out.
            Assert.True((reports - 1) * gapMs < repaintMs, "a color finishes before the next may start");
        }

        [Fact]
        public void EveryFamilyHasOneRow_AndNoneHasNone()
        {
            var listed = SteelSeriesDirectProtocol.Families.Select(f => f.Family).ToList();
            Assert.Equal(listed.Count, listed.Distinct().Count());
            var all = Enum.GetValues(typeof(SteelSeriesFamily)).Cast<SteelSeriesFamily>()
                .Where(f => f != SteelSeriesFamily.None).OrderBy(f => f);
            Assert.Equal(all, listed.OrderBy(f => f));
            Assert.Null(SteelSeriesDirectProtocol.Info(SteelSeriesFamily.None));
            Assert.Null(SteelSeriesDirectProtocol.Info((SteelSeriesFamily)999));
            Assert.Empty(SteelSeriesDirectProtocol.Color(SteelSeriesFamily.None, SteelSeriesReport.Output, false, R, G, B));
            Assert.Empty(SteelSeriesDirectProtocol.Claim(SteelSeriesFamily.None, false));
            Assert.Empty(SteelSeriesDirectProtocol.HandBack(SteelSeriesFamily.None, false));
            // Only the families two shipped references differ on carry a
            // second report type, and it is the other of the two.
            Assert.Equal(new[] { SteelSeriesFamily.Rival310, SteelSeriesFamily.PrimePlus, SteelSeriesFamily.Prime },
                SteelSeriesDirectProtocol.Families.Where(f => f.AltReport != null).Select(f => f.Family));
            Assert.All(SteelSeriesDirectProtocol.Families.Where(f => f.AltReport != null),
                f => Assert.NotEqual(f.Report, f.AltReport.Value));
        }

        /// <summary>The longest buffer each family sends as each report
        /// type, the report ID position counted, which is the least a
        /// collection must declare for it.</summary>
        [Theory]
        [InlineData(SteelSeriesFamily.Rival100, 6, 0)]
        [InlineData(SteelSeriesFamily.Rival300, 6, 0)]
        [InlineData(SteelSeriesFamily.Rival3, 16, 0)]
        [InlineData(SteelSeriesFamily.Rival310, 0, 36)]
        [InlineData(SteelSeriesFamily.Rival600, 0, 50)]
        [InlineData(SteelSeriesFamily.Rival700, 0, 14)]
        [InlineData(SteelSeriesFamily.Rival3Wireless, 6, 0)]
        [InlineData(SteelSeriesFamily.Rival650, 50, 0)]
        [InlineData(SteelSeriesFamily.Rival3Gen2, 12, 0)]
        [InlineData(SteelSeriesFamily.Aerox3, 12, 0)]
        [InlineData(SteelSeriesFamily.Rival5, 34, 0)]
        [InlineData(SteelSeriesFamily.PrimeMini, 6, 0)]
        [InlineData(SteelSeriesFamily.AeroxWireless, 7, 0)]
        [InlineData(SteelSeriesFamily.AeroxWirelessDongle, 7, 0)]
        [InlineData(SteelSeriesFamily.PrimeWireless, 7, 0)]
        [InlineData(SteelSeriesFamily.PrimeWirelessDongle, 7, 0)]
        [InlineData(SteelSeriesFamily.PrimePlus, 0, 22)]
        [InlineData(SteelSeriesFamily.Prime, 22, 0)]
        [InlineData(SteelSeriesFamily.ApexPro, 2, 451)]
        [InlineData(SteelSeriesFamily.ApexProTkl, 3, 451)]
        [InlineData(SteelSeriesFamily.Apex9, 3, 451)]
        [InlineData(SteelSeriesFamily.ApexProTkl2023, 3, 451)]
        [InlineData(SteelSeriesFamily.ApexProTklGen3, 3, 451)]
        [InlineData(SteelSeriesFamily.ApexProWireless, 3, 451)]
        [InlineData(SteelSeriesFamily.ApexProGen3, 3, 451)]
        [InlineData(SteelSeriesFamily.Apex3, 33, 0)]
        [InlineData(SteelSeriesFamily.Apex3Tkl, 27, 0)]
        [InlineData(SteelSeriesFamily.ApexM750, 0, 405)]
        [InlineData(SteelSeriesFamily.Apex2013, 23, 0)]
        [InlineData(SteelSeriesFamily.Arctis5, 37, 0)]
        [InlineData(SteelSeriesFamily.ArctisNova3, 521, 0)]
        [InlineData(SteelSeriesFamily.Siberia350, 16, 0)]
        [InlineData(SteelSeriesFamily.QckPrismCloth, 65, 525)]
        [InlineData(SteelSeriesFamily.QckPrism12, 65, 525)]
        [InlineData(SteelSeriesFamily.ArctisProGameDac, 14, 0)]
        [InlineData(SteelSeriesFamily.ArenaSpeakers, 64, 0)]
        public void EachFamilysLongestFrames_AreWhatItsCollectionMustHold(object familyValue, int output, int feature)
        {
            var info = SteelSeriesDirectProtocol.Info((SteelSeriesFamily)familyValue);
            Assert.Equal((output, feature), SteelSeriesDirectProtocol.Longest(info, info.Report));
            if (info.AltReport is SteelSeriesReport alt)
                Assert.Equal((feature, output), SteelSeriesDirectProtocol.Longest(info, alt));
        }

        // ── The product table ──

        /// <summary>Every product ID PadForge lists as a SteelSeries mouse,
        /// keyboard, headset or mousemat has one row, no product ID has
        /// two, and the table holds no product the lists lack.</summary>
        [Fact]
        public void EveryListedProductHasOneRow_AndNoProductIdComesTwice()
        {
            var ids = SteelSeriesDirectProtocol.Models.Select(m => m.ProductId).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            var listed = PeripheralProductIds.SteelSeriesMice.Concat(PeripheralProductIds.SteelSeriesKeyboards)
                .Concat(PeripheralProductIds.SteelSeriesHeadsets).Concat(PeripheralProductIds.SteelSeriesMousemats)
                .Concat(PeripheralProductIds.SteelSeriesSpeakers).ToList();
            Assert.Equal(97 + 6 + 11 + 2, listed.Count);
            Assert.Equal(listed.Count, listed.Distinct().Count());
            Assert.Equal(listed.OrderBy(id => id), ids.OrderBy(id => id));

            // Every row names a family of the table or none at all, and
            // every family has a product.
            foreach (var (productId, family) in SteelSeriesDirectProtocol.Models)
            {
                Assert.True(Enum.IsDefined(typeof(SteelSeriesFamily), family), $"{productId:X4}");
                Assert.Equal(family, SteelSeriesDirectProtocol.FamilyOf(productId));
                Assert.Equal(family != SteelSeriesFamily.None, SteelSeriesDirectProtocol.Info(family) != null);
            }
            Assert.Equal(SteelSeriesDirectProtocol.Families.Select(f => f.Family).OrderBy(f => f),
                SteelSeriesDirectProtocol.Models.Select(m => m.Family).Where(f => f != SteelSeriesFamily.None).Distinct().OrderBy(f => f));

            // A keyboard's family is a keyboard's, a headset's a headset's,
            // a mat's a mat's, and a mouse's a mouse's.
            foreach (var (productId, family) in SteelSeriesDirectProtocol.Models)
            {
                if (family == SteelSeriesFamily.None) continue;
                bool keyboardFamily = family >= SteelSeriesFamily.ApexPro && family <= SteelSeriesFamily.Apex2013;
                bool headsetFamily = family is SteelSeriesFamily.Arctis5 or SteelSeriesFamily.ArctisNova3
                    or SteelSeriesFamily.Siberia350 or SteelSeriesFamily.ArctisProGameDac;
                Assert.Equal(PeripheralProductIds.SteelSeriesKeyboards.Contains(productId), keyboardFamily);
                Assert.Equal(PeripheralProductIds.SteelSeriesHeadsets.Contains(productId), headsetFamily);
                Assert.Equal(PeripheralProductIds.SteelSeriesMousemats.Contains(productId),
                    family is SteelSeriesFamily.QckPrismCloth or SteelSeriesFamily.QckPrism12);
                Assert.Equal(PeripheralProductIds.SteelSeriesSpeakers.Contains(productId),
                    family == SteelSeriesFamily.ArenaSpeakers);
            }
            // A product the table lacks has no family.
            Assert.Equal(SteelSeriesFamily.None, SteelSeriesDirectProtocol.FamilyOf(0x0000));
        }

        /// <summary>The products with no color to set: the one-color
        /// Sensei [RAW] family and the Kana v2, and those no reference has
        /// a lighting command for, the Kinzu v2, the Rival 95 family, the
        /// Rival 100 PC Bang and the Rival 3 Wireless Gen 2.</summary>
        [Fact]
        public void AProductWithNoColor_HasNoFamily()
        {
            ushort[] none =
            {
                0x1362, 0x1369, 0x136D, 0x136F, 0x137A, 0x1380, 0x1390,
                0x1366, 0x1378, 0x1704, 0x1706, 0x1707, 0x1708, 0x1872,
            };
            Assert.Equal(none.OrderBy(id => id),
                SteelSeriesDirectProtocol.Models.Where(m => m.Family == SteelSeriesFamily.None).Select(m => m.ProductId).OrderBy(id => id));
            Assert.All(none, id => Assert.Contains(id, PeripheralProductIds.SteelSeriesMice));
            Assert.Equal(102, SteelSeriesDirectProtocol.Models.Count(m => m.Family != SteelSeriesFamily.None));
            Assert.Equal(19, SteelSeriesDirectProtocol.Models.Count(m => PeripheralProductIds.SteelSeriesKeyboards.Contains(m.ProductId)
                                                                         && m.Family != SteelSeriesFamily.None));
        }

        /// <summary>Which product is which family, row by row, from the
        /// references each row was read in: rivalcfg's device profiles for
        /// the mice, OpenRGB's detectors for the keyboards, the Aerox 3
        /// Wireless Gen 2, the headsets and the mats
        /// (SteelSeriesControllerDetect.cpp:522-538 with
        /// SteelSeriesDevices.h:69-85).</summary>
        [Theory]
        [InlineData(SteelSeriesFamily.Rival100, new ushort[] { 0x1702, 0x170A, 0x170B, 0x170C, 0x1729, 0x1810, 0x1814, 0x1816 })]
        [InlineData(SteelSeriesFamily.Rival300, new ushort[] { 0x1384, 0x1392, 0x1394, 0x1710, 0x1712, 0x1714, 0x1716, 0x1718, 0x171A, 0x171C })]
        [InlineData(SteelSeriesFamily.Rival3, new ushort[] { 0x1824, 0x184C })]
        [InlineData(SteelSeriesFamily.Rival310, new ushort[] { 0x171E, 0x1720, 0x1722, 0x1736, 0x1832, 0x1834 })]
        [InlineData(SteelSeriesFamily.Rival600, new ushort[] { 0x1724, 0x172E })]
        [InlineData(SteelSeriesFamily.Rival700, new ushort[] { 0x1700, 0x170E, 0x1730 })]
        [InlineData(SteelSeriesFamily.Rival3Wireless, new ushort[] { 0x1830 })]
        [InlineData(SteelSeriesFamily.Rival650, new ushort[] { 0x1726, 0x172B })]
        [InlineData(SteelSeriesFamily.Rival3Gen2, new ushort[] { 0x1870 })]
        [InlineData(SteelSeriesFamily.Aerox3, new ushort[] { 0x1836, 0x1850 })]
        [InlineData(SteelSeriesFamily.Rival5, new ushort[] { 0x183C, 0x183E })]
        [InlineData(SteelSeriesFamily.PrimeMini, new ushort[] { 0x184D })]
        [InlineData(SteelSeriesFamily.AeroxWireless, new ushort[] { 0x183A, 0x1854, 0x185A, 0x185E, 0x1862, 0x1876, 0x187A })]
        [InlineData(SteelSeriesFamily.AeroxWirelessDongle, new ushort[] { 0x1838, 0x1852, 0x1858, 0x185C, 0x1860, 0x1874, 0x1878, 0x1890, 0x233A })]
        [InlineData(SteelSeriesFamily.PrimeWireless, new ushort[] { 0x1842, 0x184A })]
        [InlineData(SteelSeriesFamily.PrimeWirelessDongle, new ushort[] { 0x1840, 0x1848 })]
        [InlineData(SteelSeriesFamily.PrimePlus, new ushort[] { 0x182C })]
        [InlineData(SteelSeriesFamily.Prime, new ushort[] { 0x182A, 0x182E, 0x1856 })]
        [InlineData(SteelSeriesFamily.ApexPro, new ushort[] { 0x1610, 0x1612, 0x1618, 0x161C })]
        [InlineData(SteelSeriesFamily.ApexProTkl, new ushort[] { 0x1614 })]
        [InlineData(SteelSeriesFamily.Apex9, new ushort[] { 0x1620, 0x1634 })]
        [InlineData(SteelSeriesFamily.ApexProTkl2023, new ushort[] { 0x1628 })]
        [InlineData(SteelSeriesFamily.ApexProTklGen3, new ushort[] { 0x1642 })]
        [InlineData(SteelSeriesFamily.ApexProWireless, new ushort[] { 0x1630, 0x1632, 0x1644, 0x1646 })]
        [InlineData(SteelSeriesFamily.ApexProGen3, new ushort[] { 0x1640 })]
        [InlineData(SteelSeriesFamily.Apex3, new ushort[] { 0x161A })]
        [InlineData(SteelSeriesFamily.Apex3Tkl, new ushort[] { 0x1622 })]
        [InlineData(SteelSeriesFamily.ApexM750, new ushort[] { 0x0616 })]
        [InlineData(SteelSeriesFamily.Apex2013, new ushort[] { 0x1202, 0x1206 })]
        [InlineData(SteelSeriesFamily.Arctis5, new ushort[] { 0x1250, 0x1252, 0x12AA })]
        [InlineData(SteelSeriesFamily.ArctisProGameDac, new ushort[] { 0x1280 })]
        [InlineData(SteelSeriesFamily.QckPrism12, new ushort[] { 0x1507 })]
        [InlineData(SteelSeriesFamily.ArenaSpeakers, new ushort[] { 0x1A00, 0x1A05 })]
        [InlineData(SteelSeriesFamily.ArctisNova3, new ushort[] { 0x12EC })]
        [InlineData(SteelSeriesFamily.Siberia350, new ushort[] { 0x1229 })]
        [InlineData(SteelSeriesFamily.QckPrismCloth, new ushort[] { 0x150A, 0x150D, 0x1514, 0x1516, 0x1518, 0x151A, 0x151C, 0x151E, 0x1520, 0x3769 })]
        public void EachFamilysProducts_AreTheOnesItsReferencesList(object familyValue, ushort[] productIds)
            => Assert.Equal(productIds.OrderBy(id => id),
                SteelSeriesDirectProtocol.Models.Where(m => m.Family == (SteelSeriesFamily)familyValue)
                    .Select(m => m.ProductId).OrderBy(id => id));

        /// <summary>37 of the 102 lit products have a frame that returns
        /// them to their own lighting. The other 65, the headsets and mats
        /// among them but for the GameDAC, keep the last color until they lose
        /// power.</summary>
        [Fact]
        public void ThirtySevenProductsCanBeHandedBack_AndSixtyFiveKeepTheirLastColor()
        {
            bool HandsBack(SteelSeriesFamily family)
                => SteelSeriesDirectProtocol.HandBack(family, false).Length > 0 && SteelSeriesDirectProtocol.HandBack(family, true).Length > 0;
            var lit = SteelSeriesDirectProtocol.Models.Where(m => m.Family != SteelSeriesFamily.None).ToList();
            Assert.Equal(37, lit.Count(m => HandsBack(m.Family)));
            Assert.Equal(65, lit.Count(m => !HandsBack(m.Family)));
            // A family hands back on both firmware generations or on neither.
            Assert.All(SteelSeriesDirectProtocol.Families, f =>
                Assert.Equal(f.HandBack(false).Length > 0, f.HandBack(true).Length > 0));
            Assert.Equal(new[]
                {
                    SteelSeriesFamily.Rival3Gen2, SteelSeriesFamily.Aerox3, SteelSeriesFamily.Rival5,
                    SteelSeriesFamily.AeroxWireless, SteelSeriesFamily.AeroxWirelessDongle,
                    SteelSeriesFamily.ApexPro, SteelSeriesFamily.ApexProTkl, SteelSeriesFamily.Apex9,
                    SteelSeriesFamily.ApexProTkl2023, SteelSeriesFamily.ApexProTklGen3,
                    SteelSeriesFamily.ApexProWireless, SteelSeriesFamily.ApexProGen3, SteelSeriesFamily.Apex3Tkl,
                    SteelSeriesFamily.ArctisProGameDac,
                },
                SteelSeriesDirectProtocol.Families.Where(f => HandsBack(f.Family)).Select(f => f.Family));
        }

        // ── The scan's gate ──

        private static HidCollectionInfo Collection(ushort productId, int iface, ushort output = 65, ushort feature = 515,
            ushort input = 65, ushort usagePage = 0xFFC0, ushort usage = 0x0001, ushort vendorId = 0x1038)
            => new()
            {
                Path = $@"\\?\hid#vid_{vendorId:x4}&pid_{productId:x4}&mi_{Math.Max(iface, 0):x2}#gate",
                VendorId = vendorId, ProductId = productId, UsagePage = usagePage, Usage = usage,
                InterfaceNumber = iface, InputReportLength = input, OutputReportLength = output, FeatureReportLength = feature,
            };

        private static SteelSeriesPlan? Plan(HidCollectionInfo collection)
            => SteelSeriesDirectProtocol.TryPlan(collection, out var plan) ? plan : null;

        /// <summary>A unit is the control collection of a product with a
        /// color: SteelSeries' vendor ID, a product of the table, the
        /// family's interface, usage page 0xFFC0 with usage 1. A Rival 3
        /// Gen 2's descriptor is the model (rival_re
        /// tools/rival/__init__.py:4-14): interface 3, input and output of
        /// 64 bytes, a feature report of 514.</summary>
        [Fact]
        public void TheGate_TakesAProductsControlCollection_AndNoOtherCollection()
        {
            var plan = Plan(Collection(0x1870, 3));
            Assert.NotNull(plan);
            Assert.Equal(SteelSeriesFamily.Rival3Gen2, plan.Value.Info.Family);
            Assert.Equal(SteelSeriesReport.Output, plan.Value.ColorReport);

            // The second vendor collection of the same mouse, on page
            // 0xFF00, one interface lower, and its input-only neighbor on
            // page 0xFFC1.
            Assert.Null(Plan(Collection(0x1870, 2, usagePage: 0xFF00, feature: 0)));
            Assert.Null(Plan(Collection(0x1870, 3, usagePage: 0xFF00)));
            Assert.Null(Plan(Collection(0x1870, 3, usagePage: 0xFFC1)));
            Assert.Null(Plan(Collection(0x1870, 3, usage: 0x0002)));
            // The mouse and keyboard collections of the device.
            Assert.Null(Plan(Collection(0x1870, 0, usagePage: 0x0001, usage: 0x0002)));
            Assert.Null(Plan(Collection(0x1870, 1, usagePage: 0x0001, usage: 0x0006)));
            // The right collection on another interface.
            Assert.Null(Plan(Collection(0x1870, 0)));
            Assert.Null(Plan(Collection(0x1870, 1)));
            // Another vendor's device with the same product ID, a product
            // with no color, and a product the table lacks.
            Assert.Null(Plan(Collection(0x1870, 3, vendorId: 0x046D)));
            Assert.Null(Plan(Collection(0x1369, 0)));
            Assert.Null(Plan(Collection(0x1507, 0)));
            Assert.Null(Plan(null));
        }

        /// <summary>A path with no interface part is interface 0, as
        /// hidapi reports a device with one interface (hidapi
        /// windows/hid.c:568-570).</summary>
        [Fact]
        public void TheGate_ReadsAPathWithNoInterfacePart_AsInterface0()
        {
            Assert.Equal(SteelSeriesFamily.Rival100, Plan(Collection(0x1702, -1, output: 33)).Value.Info.Family);
            Assert.Equal(SteelSeriesFamily.Rival100, Plan(Collection(0x1702, 0, output: 33)).Value.Info.Family);
            // A product whose control collection is on interface 3 is not
            // reached through a path that names none.
            Assert.Null(Plan(Collection(0x1870, -1)));
        }

        /// <summary>The collection has to declare each report type the
        /// family sends, long enough for its longest frame. Windows fails a
        /// buffer shorter than the report, and a frame is never cut.</summary>
        [Fact]
        public void TheGate_NeedsEveryReportTheFamilySends_LongEnoughForItsLongestFrame()
        {
            // An Apex 3's frame fills its 32-byte report exactly.
            Assert.NotNull(Plan(Collection(0x161A, 3, output: 33)));
            Assert.Null(Plan(Collection(0x161A, 3, output: 32)));
            Assert.Null(Plan(Collection(0x161A, 3, output: 0)));

            // A Rival 600's eight feature reports.
            Assert.NotNull(Plan(Collection(0x1724, 0, output: 33, feature: 579)));
            Assert.NotNull(Plan(Collection(0x1724, 0, output: 0, feature: 50)));
            Assert.Null(Plan(Collection(0x1724, 0, output: 33, feature: 49)));
            Assert.Null(Plan(Collection(0x1724, 0, output: 65, feature: 0)));

            // A per-key Apex needs its feature report for the colors and
            // its output report for the hand-back: a board that could be
            // claimed and not handed back gets no unit. The Apex 9's
            // 512-byte report holds the frame.
            Assert.NotNull(Plan(Collection(0x1610, 1, output: 65, feature: 643)));
            Assert.NotNull(Plan(Collection(0x1634, 1, output: 65, feature: 513)));
            Assert.Null(Plan(Collection(0x1610, 1, output: 0, feature: 643)));
            Assert.Null(Plan(Collection(0x1610, 1, output: 65, feature: 450)));
            Assert.NotNull(Plan(Collection(0x1610, 1, output: 65, feature: 451)));

            // An Apex M750's collection has a feature report and nothing
            // else (OpenRGB issue 641, its descriptor).
            var m750 = Plan(Collection(0x0616, 2, output: 0, feature: 513, input: 0));
            Assert.Equal(SteelSeriesFamily.ApexM750, m750.Value.Info.Family);
            Assert.Equal(SteelSeriesReport.Feature, m750.Value.ColorReport);
        }

        /// <summary>A family two shipped references send as different
        /// report types takes the first of the two its collection has at
        /// length: the vendor's where a capture shows it.</summary>
        [Fact]
        public void TheGate_TakesTheFirstReportTypeTheCollectionHas_ForAFamilyWithTwo()
        {
            // Sensei 310: a feature report of 262 bytes, the Engine's type.
            Assert.Equal(SteelSeriesReport.Feature, Plan(Collection(0x1722, 0, output: 65, feature: 263)).Value.ColorReport);
            // A collection with no feature report long enough: the output
            // report carries the same bytes.
            Assert.Equal(SteelSeriesReport.Output, Plan(Collection(0x1722, 0, output: 65, feature: 0)).Value.ColorReport);
            Assert.Equal(SteelSeriesReport.Output, Plan(Collection(0x1722, 0, output: 36, feature: 35)).Value.ColorReport);
            Assert.Null(Plan(Collection(0x1722, 0, output: 35, feature: 35)));

            // Prime+: the feature report GG sends it. Prime: the output
            // report rivalcfg and SignalRGB send it.
            Assert.Equal(SteelSeriesReport.Feature, Plan(Collection(0x182C, 0, output: 65, feature: 643)).Value.ColorReport);
            Assert.Equal(SteelSeriesReport.Output, Plan(Collection(0x182C, 0, output: 65, feature: 0)).Value.ColorReport);
            Assert.Equal(SteelSeriesReport.Output, Plan(Collection(0x182E, 0, output: 65, feature: 643)).Value.ColorReport);
            Assert.Equal(SteelSeriesReport.Feature, Plan(Collection(0x182E, 0, output: 0, feature: 643)).Value.ColorReport);
            Assert.Null(Plan(Collection(0x182E, 0, output: 21, feature: 21)));
        }

        /// <summary>A dongle answers each report on the collection's input
        /// report, which has to be there to read.</summary>
        [Fact]
        public void TheGate_NeedsTheInputReportADonglesRepliesComeIn()
        {
            Assert.NotNull(Plan(Collection(0x1852, 3)));
            Assert.Null(Plan(Collection(0x1852, 3, input: 0)));
            Assert.Null(Plan(Collection(0x1852, 3, input: 2)));
            Assert.NotNull(Plan(Collection(0x1852, 3, input: 3)));
            // The same mouse on its cable is not read from.
            Assert.NotNull(Plan(Collection(0x1854, 3, input: 0)));
        }
    }
}
