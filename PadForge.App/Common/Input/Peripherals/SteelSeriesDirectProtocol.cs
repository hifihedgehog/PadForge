using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>The two report types a SteelSeries control collection takes
    /// a command as.</summary>
    internal enum SteelSeriesReport
    {
        Output,
        Feature,
    }

    /// <summary>One report for a SteelSeries device: its type, and its
    /// bytes as a Windows HID call takes them. Byte 0 is the report ID
    /// position, always 0x00, since the control collection declares no
    /// report IDs (libratbag driver-steelseries.c:44), and the command
    /// follows it. The channel pads the buffer with zeros to the
    /// collection's report length.</summary>
    internal readonly record struct SteelSeriesFrame(SteelSeriesReport Report, byte[] Buffer)
    {
        /// <summary>The command byte, the first the device reads.</summary>
        public byte Command => Buffer[1];
    }

    /// <summary>The command families of SteelSeries mice and keyboards, each
    /// named for the device its frames were first read from. A family is a
    /// firmware line: the same command number means something else in
    /// another one, so a frame only ever goes to a product the table lists
    /// under its family (<see cref="SteelSeriesDirectProtocol.Models"/>).</summary>
    internal enum SteelSeriesFamily
    {
        /// <summary>No color to set: a one-color LED, or no lighting
        /// command in any reference. Such a product gets no unit.</summary>
        None,
        Rival100,
        Rival300,
        Rival3,
        Rival310,
        Rival600,
        Rival700,
        Rival3Wireless,
        Rival650,
        Rival3Gen2,
        Aerox3,
        Rival5,
        PrimeMini,
        /// <summary>An Aerox 3, 5 or 9 Wireless on its cable.</summary>
        AeroxWireless,
        /// <summary>The same mice through their 2.4 GHz dongle.</summary>
        AeroxWirelessDongle,
        /// <summary>A Prime Wireless or Prime Mini Wireless on its cable.</summary>
        PrimeWireless,
        PrimeWirelessDongle,
        PrimePlus,
        Prime,
        /// <summary>The 2019 per-key boards: Apex Pro, Apex 7, Apex 7 TKL,
        /// Apex 5.</summary>
        ApexPro,
        /// <summary>The Apex Pro TKL, whose frames depend on its firmware.</summary>
        ApexProTkl,
        /// <summary>The Apex 9 TKL and Mini, whose claim and hand-back
        /// depend on their firmware.</summary>
        Apex9,
        /// <summary>The wired Apex Pro TKL of 2023, the same.</summary>
        ApexProTkl2023,
        ApexProTklGen3,
        /// <summary>The wireless Apex Pro TKL boards of 2023 and Gen 3.</summary>
        ApexProWireless,
        ApexProGen3,
        Apex3,
        Apex3Tkl,
        ApexM750,
        /// <summary>The Apex of 2013 and the Apex 350.</summary>
        Apex2013,
    }

    /// <summary>One row of the family table: where a family's control
    /// collection sits, the report type its color travels as, its frames
    /// and how fast it takes them.</summary>
    internal sealed class SteelSeriesFamilyInfo
    {
        public SteelSeriesFamily Family { get; init; }

        /// <summary>The USB interface the control collection is alone on.</summary>
        public int Interface { get; init; }

        /// <summary>The report type of the color frames.</summary>
        public SteelSeriesReport Report { get; init; }

        /// <summary>The other report type the same color bytes travel as,
        /// where shipped references differ on the type. It is used for a
        /// collection that lacks <see cref="Report"/> at the length the
        /// frames need. Null for a family with one type.</summary>
        public SteelSeriesReport? AltReport { get; init; }

        /// <summary>The least time between two reports of one paint, the
        /// claim frames included.</summary>
        public int GapMs { get; init; }

        /// <summary>The least time between the first reports of two
        /// colors.</summary>
        public int RepaintMs { get; init; }

        /// <summary>How long the claim and an unchanged color stand before
        /// they are sent again. Every family has one.</summary>
        public int ReassertMs { get; init; }

        /// <summary>A 2.4 GHz dongle: each report is answered by an input
        /// report that has to be read before the next one goes.</summary>
        public bool Echoes { get; init; }

        /// <summary>An Apex board whose claim, packet and hand-back depend
        /// on its firmware version, which is asked for once after the
        /// open (<see cref="SteelSeriesDirectProtocol.VersionQuery"/>).</summary>
        public bool Gated { get; init; }

        /// <summary>The frames that take the lighting before the first
        /// color, by whether the firmware is of the third generation. Sent
        /// in front of the first color of an open, again on the re-assert
        /// interval, and once when Windows resumes from sleep.</summary>
        public Func<bool, SteelSeriesFrame[]> Claim { get; init; } = _ => SteelSeriesDirectProtocol.NoFrames;

        /// <summary>The frames of one color on every LED or zone: the
        /// report type they travel as, the firmware generation, then red,
        /// green and blue.</summary>
        public Func<SteelSeriesReport, bool, byte, byte, byte, SteelSeriesFrame[]> Color { get; init; }

        /// <summary>The frames that return the device to its own
        /// lighting, or none for a family no reference has such a frame
        /// for. Every one is an output report.</summary>
        public Func<bool, SteelSeriesFrame[]> HandBack { get; init; } = _ => SteelSeriesDirectProtocol.NoFrames;
    }

    /// <summary>What the scan decided for one present collection: its
    /// family, and the report type its color frames travel as there.</summary>
    internal readonly record struct SteelSeriesPlan(SteelSeriesFamilyInfo Info, SteelSeriesReport ColorReport);

    /// <summary>
    /// The frames that light a SteelSeries mouse or keyboard with one color
    /// and no SteelSeries GG running (#494). Pure: a family and a color in,
    /// bytes out, and nothing here touches a device.
    ///
    /// <para>Every lit product keeps its control channel on a vendor
    /// collection of its own: usage page 0xFFC0, usage 0x0001, no report
    /// IDs, alone on one USB interface. It is the collection rivalcfg
    /// addresses by interface number (usbhid.py:83-86), the one OpenRGB
    /// registers its detectors on (SteelSeriesControllerDetect.cpp:469-483
    /// and 537-555), and the one Aurora picks by its usage page on Windows
    /// (Aurora UnifiedHID/RivalMouses.cs:25, 60, 100 and 148). Windows
    /// opens such a collection for read and write, so output reports reach
    /// it. The mouse or keyboard collection of the same device is another
    /// interface, takes feature reports alone, and is never written to
    /// here.</para>
    ///
    /// <para>A frame is chosen by product ID from <see cref="Models"/> and
    /// never from a descriptor, because the families reuse command numbers.
    /// 0x08 is the color on a Rival 300 (rivalcfg devices/rival300.py:105-122)
    /// and erases a flash page on the Rival 3 Gen 2 firmware (rival_re
    /// input/decomp.c:3868-3885, command 8 of the table at
    /// input/disasm.asm:15080). 0x09 saves on the older mice
    /// (devices/rival100.py:131-134) and on a Rival 310 "will break the
    /// LEDs" (rival310-re README.md:27). 0x61 is a color through a mouse
    /// dongle, the sensitivity presets of a Prime (devices/prime.py:26-32)
    /// and the screen image of an Apex Pro Gen 3 (apex-oled
    /// README.md:18-26).</para>
    ///
    /// <para>No builder here produces a command that saves to the device's
    /// memory. Every family has one apart from its color command: 0x09 on
    /// the older mice, 0x59 on the Rival 310 and the Prime
    /// (devices/rival310.py:131-134, devices/prime.py:114-117), 0x11 on the
    /// newer mice and the Apex 3 TKL (devices/aerox3.py:155-158, apex-3-tkl
    /// docs/internals.md:40), with 0x51 through a dongle, and the pair
    /// 06 00 08 then 09 on the Apex 3
    /// (OpenRGB SteelSeriesApexTZoneController.cpp:45-54). A color that
    /// follows a game changes many times a second, and flash wears. The
    /// brightness commands stay out as well (0x23, 0x0A on the Apex 3):
    /// they are the user's setting, and on the wireless Aerox mice 0x23
    /// carries the dim timer in the same frame
    /// (devices/aerox3_wireless_wired.py:101-112).</para>
    ///
    /// <para>Pacing. A family's gap and its fastest repaint come from what
    /// the vendor's own program does where a USB capture of it exists, then
    /// from a program that streams to that product on Windows, and from the
    /// reference tools' waits where neither exists. rivalcfg waits 50 ms
    /// after every command and warns that a shorter wait "can hang the
    /// device" (mouse.py:85, 96-100 and 312-314), libratbag 10 ms in front
    /// of every report (driver-steelseries.c:712-713 and 741-742), and where
    /// those two differ the longer wait is kept. Every family gets its claim
    /// and its color again on its re-assert interval, 5000 ms for most: a
    /// device that lost power while its collection stayed listed, as over a
    /// PC's sleep or a fast replug, shows its stored lighting again and says
    /// nothing.</para>
    /// </summary>
    internal static class SteelSeriesDirectProtocol
    {
        internal const ushort VendorId = PeripheralLinker.SteelSeriesVid;

        /// <summary>The control collection's usage page and usage. Its
        /// report descriptor starts 06 C0 FF 09 01 on a Rival 3 Gen 2
        /// (rival_re tools/rival/__init__.py:4-14), and the Apex Pro
        /// Gen 3's control collection sits on the same page (apex-oled
        /// README.md:45-52).</summary>
        internal const ushort ControlUsagePage = 0xFFC0;
        internal const ushort ControlUsage = 0x0001;

        internal static readonly SteelSeriesFrame[] NoFrames = Array.Empty<SteelSeriesFrame>();

        /// <summary>The first firmware of the third Apex generation.
        /// OpenRGB: "Assume Gen 3 protocol from 1.19.7 or newer"
        /// (SteelSeriesApexController.cpp:219-253).</summary>
        internal const int Gen3Major = 1, Gen3Minor = 19, Gen3Patch = 7;

        /// <summary>How long the 5000 ms families wait before the claim and
        /// an unchanged color go out again, the interval the HID++ worker
        /// re-asserts on.</summary>
        internal const int ReassertMs = 5000;

        /// <summary>The re-assert of the Rival 650, whose color is an LED
        /// program in sixteen reports. It sleeps behind a dongle like the
        /// others, and its frames are the slowest and the least proven, so
        /// they are repeated least.</summary>
        internal const int SlowReassertMs = 30000;

        private static SteelSeriesFrame Out(params byte[] data) => Make(SteelSeriesReport.Output, data);
        private static SteelSeriesFrame Feat(params byte[] data) => Make(SteelSeriesReport.Feature, data);

        /// <summary>A frame from its data bytes, with the report ID
        /// position in front.</summary>
        private static SteelSeriesFrame Make(SteelSeriesReport report, byte[] data)
        {
            var buffer = new byte[data.Length + 1];
            data.CopyTo(buffer, 1);
            return new SteelSeriesFrame(report, buffer);
        }

        // ─────────────────────────────────────────────
        //  The families
        // ─────────────────────────────────────────────

        /// <summary>One row per command family.</summary>
        internal static readonly SteelSeriesFamilyInfo[] Families =
        {
            // Rival 100, 105, 106, 110 and 300S. One LED. 07 00 01 sets the
            // steady effect and 05 00 RR GG BB the color (rivalcfg
            // devices/rival100.py:91-116, devices/rival110.py:66-93,
            // libratbag driver-steelseries.c:685-744 with the Rival 100
            // quirk, OpenRGB SteelSeriesRivalController.cpp:105-134 and
            // 279-323, Aurora RivalMouses.cs:28-38). The effect goes out in
            // front of the first color and again on the re-assert interval,
            // because a mouse whose stored effect is breathing would
            // breathe in the new color. libratbag sends it before every
            // color and waits 10 ms in front of each report, rivalcfg 50 ms
            // after each, and the longer wait is the gap. No reference has
            // a frame that returns the stored lighting.
            new()
            {
                Family = SteelSeriesFamily.Rival100, Interface = 0, Report = SteelSeriesReport.Output,
                GapMs = 50, RepaintMs = 50, ReassertMs = ReassertMs,
                Claim = _ => new[] { Out(0x07, 0x00, 0x01) },
                Color = (_, _, r, g, b) => new[] { Out(0x05, 0x00, r, g, b) },
            },

            // Rival and Rival 300. Two LEDs, 01 the logo and 02 the wheel:
            // 07 LL 01 the steady effect, 08 LL RR GG BB the color
            // (rivalcfg devices/rival300.py:105-156, libratbag
            // driver-steelseries.c:685-744, Aurora RivalMouses.cs:103-125).
            // 0x08 is this family's alone. The gap is the Rival 100's, and
            // a color is two reports, so the next one starts no sooner than
            // 100 ms after it.
            new()
            {
                Family = SteelSeriesFamily.Rival300, Interface = 0, Report = SteelSeriesReport.Output,
                GapMs = 50, RepaintMs = 100, ReassertMs = ReassertMs,
                Claim = _ => new[] { Out(0x07, 0x01, 0x01), Out(0x07, 0x02, 0x01) },
                Color = (_, _, r, g, b) => new[] { Out(0x08, 0x01, r, g, b), Out(0x08, 0x02, r, g, b) },
            },

            // Rival 3, both product IDs. Four zones in one report: 0A 00 0F
            // and the colors of the front, middle, rear and logo zones.
            // SteelSeries Engine sends this frame for a static color (the
            // captures attached to OpenRGB issue 1167: rival_3_SolidRed
            // carries 0A 00 0F FF 00 00 four times, and rival_3_MidWhite
            // the same head with white in the second slot alone), and
            // SignalRGB streams it on Windows (signal-plugins
            // Steelseries_Rival_3_Mouse.js:64-96). rivalcfg and OpenRGB
            // set one zone per report with 05 00 ZZ and a brightness
            // (rivalcfg devices/rival3.py:50-96, OpenRGB
            // SteelSeriesRival3Controller.cpp:54-77), four reports where
            // the vendor's frame is one.
            new()
            {
                Family = SteelSeriesFamily.Rival3, Interface = 3, Report = SteelSeriesReport.Output,
                RepaintMs = 33, ReassertMs = ReassertMs,
                Color = (_, _, r, g, b) => new[] { Out(Fill(new byte[] { 0x0A, 0x00, 0x0F }, 4, r, g, b)) },
            },

            // Rival 310, Sensei 310, Sensei TEN. Two LEDs, 00 the logo and
            // 01 the wheel, each a color cycle of one point that does not
            // repeat (Cycle310). The Engine, rivalcfg and SignalRGB send it
            // as a feature report (rivalcfg devices/rival310.py:82-103,
            // devices/sensei310.py:70-91, devices/sensei_ten.py:67-88,
            // signal-plugins Steelseries_Rival_310_Mouse.js:77-92),
            // OpenRGB and libratbag as an output report (OpenRGB
            // SteelSeriesSenseiController.cpp:247-286, libratbag
            // driver-steelseries.c:896-926). The vendor's type comes
            // first. At connect the Engine sends this mouse a command
            // every 8 ms and its two LED frames 23.5 ms apart (the
            // captures of OpenRGB issue 1103).
            new()
            {
                Family = SteelSeriesFamily.Rival310, Interface = 0, Report = SteelSeriesReport.Feature,
                AltReport = SteelSeriesReport.Output, GapMs = 24, RepaintMs = 66, ReassertMs = ReassertMs,
                Color = (report, _, r, g, b) => new[]
                {
                    Make(report, Cycle310(0x00, r, g, b)), Make(report, Cycle310(0x01, r, g, b)),
                },
            },

            // Rival 600. Eight LEDs, each a feature report (Cycle600). The
            // Engine spaces them 22 ms apart at the median, and each takes
            // about 11 ms on the wire (the captures of OpenRGB issue 833).
            new()
            {
                Family = SteelSeriesFamily.Rival600, Interface = 0, Report = SteelSeriesReport.Feature,
                GapMs = 22, RepaintMs = 200, ReassertMs = ReassertMs,
                Color = (_, _, r, g, b) =>
                {
                    var frames = new SteelSeriesFrame[Rival600Leds.Length];
                    for (int led = 0; led < frames.Length; led++) frames[led] = Feat(Cycle600((byte)led, r, g, b));
                    return frames;
                },
            },

            // Rival 500, 700 and 710. Two LEDs, 00 the logo and 01 the
            // wheel: 05 00 LL RR GG BB FF 32 C8 C8 00 LL 01, where the last
            // byte is the steady mode and FF 32 C8 with C8 00 are a second
            // color and a time that only the trigger mode uses. The bytes
            // are rice's for the Rival 700 (rice
            // src/command/commands.rs:22-38), Aurora's for the Rival 500
            // (RivalMouses.cs:151-189), and an owner's decode of the
            // Engine's own feature report to a Rival 500 on Windows
            // (rivalcfg issue 138, comment 5380515397). OpenRGB zeroes the
            // middle and keeps the LED and the mode where they are (OpenRGB
            // SteelSeriesRivalController.cpp:243-277), and SignalRGB alone
            // puts the LED behind the mode (signal-plugins
            // Steelseries_Rival_700_Mouse.js:121-153). It is a feature
            // report in rivalcfg, OpenRGB and SignalRGB too
            // (devices/rival700.py:75-76). The same decode gives the pace,
            // "Delay of 50ms between sending packets or they may get
            // dropped", and names 08 as what the Engine sends after each
            // color to keep it. 08 is never sent here.
            new()
            {
                Family = SteelSeriesFamily.Rival700, Interface = 0, Report = SteelSeriesReport.Feature,
                GapMs = 50, RepaintMs = 100, ReassertMs = ReassertMs,
                Color = (_, _, r, g, b) => new[]
                {
                    Feat(0x05, 0x00, 0x00, r, g, b, 0xFF, 0x32, 0xC8, 0xC8, 0x00, 0x00, 0x01),
                    Feat(0x05, 0x00, 0x01, r, g, b, 0xFF, 0x32, 0xC8, 0xC8, 0x00, 0x01, 0x01),
                },
            },

            // Rival 3 Wireless, through its dongle, on interface 3 (OpenRGB
            // SteelSeriesControllerDetect.cpp:513). One LED, in the wheel:
            // 13, the LED's number 01 and the color, one output report that
            // OpenRGB writes with no reply read on this mouse
            // (SteelSeriesAeroxWirelessController.cpp:187-194, 81-103 and
            // 254-268, from commit 95154754). OpenRGB sends it no effect
            // and no save (158-174 and 235-252), and neither is sent here.
            // 13 is the Rival 3 Gen 2's factory reset (rival_re
            // tools/rival/protocol.py:46), so no other family may send it.
            // SignalRGB uploads an LED program instead, four reports 60 ms
            // apart for one color (signal-plugins
            // Steelseries_Rival_3_Mouse_Wireless.js:64-136). OpenRGB sets
            // this mouse no pace, so the gap is rivalcfg's 50 ms wait
            // (mouse.py:85 and 312-314).
            new()
            {
                Family = SteelSeriesFamily.Rival3Wireless, Interface = 3, Report = SteelSeriesReport.Output,
                GapMs = 50, RepaintMs = 50, ReassertMs = ReassertMs,
                Color = (_, _, r, g, b) => new[] { Out(0x13, 0x01, r, g, b) },
            },

            // Rival 650 Wireless, on its cable and through its dongle.
            // Eight LEDs, 0x10 to 0x17, each an LED program and its bind
            // (signal-plugins Steelseries_Rival_650_Mouse.js:84-131).
            // OpenRGB sends four reports per LED, with the second program
            // part and a 1C (OpenRGB SteelSeriesRivalController.cpp:164-218),
            // and gives the program's first three points red, green and
            // blue at distances 00, 54 and 54 ahead of the color. The shorter
            // form is the one a Windows program streams. SignalRGB waits
            // 60 ms after each program report by default and says a lower
            // delay "increases chance of mouse locking up"
            // (Steelseries_Rival_650_Mouse.js:17-31), so one color takes
            // about a second, and at most one a second is sent.
            new()
            {
                Family = SteelSeriesFamily.Rival650, Interface = 0, Report = SteelSeriesReport.Output,
                GapMs = 60, RepaintMs = 1000, ReassertMs = SlowReassertMs,
                Color = (_, _, r, g, b) =>
                {
                    var frames = new SteelSeriesFrame[16];
                    for (int led = 0; led < 8; led++)
                    {
                        frames[led * 2] = Out(LedProgram(r, g, b));
                        frames[led * 2 + 1] = Out(0x05, 0x00, (byte)(0x10 + led), 0xFF, 0x00, 0x00, 0x00, 0x00, 0x5C);
                    }
                    return frames;
                },
            },

            // Rival 3 Gen 2. Three zones in one report: 21, a mask of the
            // zones the frame sets, then one color slot per zone. The
            // firmware stores each masked zone's three bytes and a static
            // flag in RAM (rival_re input/decomp.c:3606-3628), and 22 with
            // the same mask clears the flag, after which the zone shows
            // the mouse's own animation again (decomp.c:3632-3651,
            // "clear static override (resume animation)" in
            // tools/rival/protocol.py:42-66). GG sends 21 07 and three
            // colors, 65 ms apart at the median and 33 ms at the least
            // (the captures of OpenRGB issue 4917). 22 07 is rivalcfg's
            // rainbow effect on every zone
            // (devices/rival3_gen2.py:97-116).
            new()
            {
                Family = SteelSeriesFamily.Rival3Gen2, Interface = 3, Report = SteelSeriesReport.Output,
                RepaintMs = 33, ReassertMs = ReassertMs,
                Color = (_, _, r, g, b) => new[] { Out(Fill(new byte[] { 0x21, 0x07 }, 3, r, g, b)) },
                HandBack = _ => new[] { Out(0x22, 0x07) },
            },

            // Aerox 3 and Aerox 5. The Rival 3 Gen 2's command, one zone
            // per report: 21 01 and the color, 21 02 and the color in the
            // second slot, 21 04 and the color in the third (rivalcfg
            // devices/aerox3.py:44-70 and devices/aerox5.py:44-70, OpenRGB
            // SteelSeriesAerox3Controller.cpp:85-103 and
            // SteelSeriesAerox5Controller.cpp:85-124). GG sends an Aerox 3
            // these three, 15.5 ms apart (the captures of OpenRGB issue
            // 2492). 22 07 is rivalcfg's rainbow effect on every zone
            // (devices/aerox3.py:91-110).
            new()
            {
                Family = SteelSeriesFamily.Aerox3, Interface = 3, Report = SteelSeriesReport.Output,
                GapMs = 16, RepaintMs = 50, ReassertMs = ReassertMs,
                Color = (_, _, r, g, b) => new[]
                {
                    Out(0x21, 0x01, r, g, b),
                    Out(0x21, 0x02, 0x00, 0x00, 0x00, r, g, b),
                    Out(0x21, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, r, g, b),
                },
                HandBack = _ => new[] { Out(0x22, 0x07) },
            },

            // Rival 5. Ten LEDs in one report: 21, the mask FF 03 of bits 0
            // to 9, then ten colors, the wheel first and the logo last
            // (rivalcfg devices/rival5.py:50-64 and 185-199). GG streams
            // this report every 15.6 ms (the captures of OpenRGB issues
            // 1783 and 2437). OpenRGB sends the same bytes as a feature
            // report (SteelSeriesRival5Controller.cpp:103-120), the one
            // reference that does. 22 FF 03 is rivalcfg's rainbow effect
            // (devices/rival5.py:256-263).
            new()
            {
                Family = SteelSeriesFamily.Rival5, Interface = 0, Report = SteelSeriesReport.Output,
                RepaintMs = 33, ReassertMs = ReassertMs,
                Color = (_, _, r, g, b) => new[] { Out(Fill(new byte[] { 0x21, 0xFF, 0x03 }, 10, r, g, b)) },
                HandBack = _ => new[] { Out(0x22, 0xFF, 0x03) },
            },

            // Prime Mini. One LED: 21 00 RR GG BB (rivalcfg
            // devices/prime_mini.py:44-52), the one reference for it, which
            // has no rainbow command for this mouse.
            new()
            {
                Family = SteelSeriesFamily.PrimeMini, Interface = 3, Report = SteelSeriesReport.Output,
                RepaintMs = 50, ReassertMs = ReassertMs,
                Color = (_, _, r, g, b) => new[] { Out(0x21, 0x00, r, g, b) },
            },

            // Aerox 3, 5 and 9 Wireless on the cable. Three zones, one
            // report each: 21 01 ZZ RR GG BB (rivalcfg
            // devices/aerox3_wireless_wired.py:53-79,
            // devices/aerox5_wireless_wired.py:64-82,
            // devices/aerox9_wireless_wired.py:58-76, OpenRGB
            // SteelSeriesAeroxWirelessController.cpp:176-226). 22 FF is the
            // rainbow effect (devices/aerox3_wireless_wired.py:141-148,
            // OpenRGB SteelSeriesAeroxWirelessController.cpp:158-175).
            // OpenRGB's fourth zone is 26, the reactive color, and is not
            // painted. The firmware is the Aerox 3's, so the gap is too.
            new()
            {
                Family = SteelSeriesFamily.AeroxWireless, Interface = 3, Report = SteelSeriesReport.Output,
                GapMs = 16, RepaintMs = 50, ReassertMs = ReassertMs,
                Color = (_, _, r, g, b) => Zones(0x21, 3, r, g, b),
                HandBack = _ => new[] { Out(0x22, 0xFF) },
            },

            // The same mice through the 2.4 GHz dongle: bit 6 set in the
            // command, 61 and 62, and a reply to read after each report
            // (rivalcfg devices/aerox3_wireless_wireless.py:3-16, OpenRGB
            // SteelSeriesAeroxWirelessController.cpp:254-268). GG sends a
            // dongle 61 01 ZZ and the color, one zone per report. The reply
            // comes 8 to 22 ms after the report, and GG's next report
            // follows the reply by 6.4 ms at the median (the captures of
            // OpenRGB issue 4048).
            new()
            {
                Family = SteelSeriesFamily.AeroxWirelessDongle, Interface = 3, Report = SteelSeriesReport.Output,
                GapMs = 6, RepaintMs = 50, ReassertMs = ReassertMs, Echoes = true,
                Color = (_, _, r, g, b) => Zones(0x61, 3, r, g, b),
                HandBack = _ => new[] { Out(0x62, 0xFF) },
            },

            // Prime Wireless and Prime Mini Wireless on the cable. One LED:
            // 21 01 00 RR GG BB (rivalcfg
            // devices/prime_wireless_wired.py:53-61), the one reference,
            // which has no rainbow command for these mice.
            new()
            {
                Family = SteelSeriesFamily.PrimeWireless, Interface = 3, Report = SteelSeriesReport.Output,
                RepaintMs = 50, ReassertMs = ReassertMs,
                Color = (_, _, r, g, b) => Zones(0x21, 1, r, g, b),
            },

            // And through the dongle, with bit 6 and the reply (rivalcfg
            // devices/prime_wireless_wireless.py:3-16).
            new()
            {
                Family = SteelSeriesFamily.PrimeWirelessDongle, Interface = 3, Report = SteelSeriesReport.Output,
                RepaintMs = 50, ReassertMs = ReassertMs, Echoes = true,
                Color = (_, _, r, g, b) => Zones(0x61, 1, r, g, b),
            },

            // Prime+. One LED: 62 01, the color, twelve zeros, the color
            // again as the one point of its cycle, and FF. GG sends this
            // once per change, as a feature report (the captures of OpenRGB
            // issue 4354: 62 01 FF 00 00, zeros, FF 00 00 FF). rivalcfg
            // sends zeros where the second color is, as an output report
            // (devices/prime_plus.py:44-58), and SignalRGB the second color
            // with no FF (signal-plugins
            // Steelseries_Rival_Prime_Mouse.js:68-100). The vendor's frame
            // holds both, and the capture is of this product ID, so its
            // report type comes first.
            new()
            {
                Family = SteelSeriesFamily.PrimePlus, Interface = 0, Report = SteelSeriesReport.Feature,
                AltReport = SteelSeriesReport.Output, RepaintMs = 50, ReassertMs = ReassertMs,
                Color = (report, _, r, g, b) => new[] { Make(report, PrimeColor(r, g, b)) },
            },

            // Prime and its editions. The same bytes, as the output report
            // rivalcfg and SignalRGB send these product IDs
            // (devices/prime.py:56-74, Steelseries_Rival_Prime_Mouse.js:97),
            // with the feature report behind it.
            new()
            {
                Family = SteelSeriesFamily.Prime, Interface = 0, Report = SteelSeriesReport.Output,
                AltReport = SteelSeriesReport.Feature, RepaintMs = 33, ReassertMs = ReassertMs,
                Color = (report, _, r, g, b) => new[] { Make(report, PrimeColor(r, g, b)) },
            },

            // The per-key Apex boards. One feature report: a packet number,
            // the key count, then four bytes per key, its HID usage and its
            // color (OpenRGB SteelSeriesApexController.cpp:128-200). The
            // packet is 3A on the 2019 boards, 40 on the 2023 and Apex 9
            // boards and 61 on the wireless ones
            // (SteelSeriesApexBaseController.h:48-64,
            // SteelSeriesApexController.cpp:148-166). GG streams it every
            // 64.7 ms at the median, a steady color included (the captures
            // of OpenRGB issues 4376 and 3253). A board of the third
            // firmware generation is claimed with the feature report 4B
            // and handed back with the output report 41, an older one
            // takes no claim and is handed back with 3B
            // (SteelSeriesApexController.cpp:96-110 and 255-275): "OpenRGB
            // must clear initialized keyboards during shutdown as hardware
            // lighting profiles won't apply otherwise".
            Apex(SteelSeriesFamily.ApexPro, 1, gated: false, gen3 => 0x3A, claim: null, gen3HandBack: false),

            // Apex Pro TKL: the 2019 frames until firmware 1.19.7, the
            // third generation's from it on, the packet included
            // (SteelSeriesApexController.cpp:219-253).
            Apex(SteelSeriesFamily.ApexProTkl, 1, gated: true, gen3 => gen3 ? (byte)0x40 : (byte)0x3A, claim: null, gen3HandBack: false),

            // Apex 9 TKL and Mini: packet 40 on either firmware, gated the
            // same way.
            Apex(SteelSeriesFamily.Apex9, 1, gated: true, gen3 => 0x40, claim: null, gen3HandBack: false),

            // Apex Pro TKL of 2023, wired. OpenRGB fixes it at the second
            // generation with no version test, and SignalRGB sends it 4B on
            // every start (signal-plugins
            // Steelseries_Apex_Pro_TKL_2023_Keyboard.js:78-84). OpenRGB's
            // own rule counts a second-generation board on the newer
            // firmware as third generation
            // (SteelSeriesApexBaseController.h:24-33), and a 2023 board on
            // 1.19.7 exists (apex-web PROTOCOL.md:182-190), so the board is
            // gated like its siblings: on an older firmware the frames are
            // OpenRGB's, on 1.19.7 they are SignalRGB's claim and the
            // third generation's hand-back.
            Apex(SteelSeriesFamily.ApexProTkl2023, 1, gated: true, gen3 => 0x40, claim: null, gen3HandBack: false),

            // Apex Pro TKL Gen 3, wired: third generation from the start.
            Apex(SteelSeriesFamily.ApexProTklGen3, 1, gated: false, gen3 => 0x40, claim: Feat(0x4B), gen3HandBack: true),

            // The wireless Apex Pro TKL boards: third generation, with
            // packet 61 (SteelSeriesApexController.cpp:148-166, signal-plugins
            // Steelseries_Apex_Pro_TKL_2023_Wireless_Keyboard.js:78-129).
            Apex(SteelSeriesFamily.ApexProWireless, 3, gated: false, gen3 => 0x61, claim: Feat(0x4B), gen3HandBack: true),

            // Apex Pro Gen 3. GG claims it with the output report 41 01,
            // once, some 90 ms before its first 3A frame, and never sends
            // 4B (the captures of OpenRGB issue 4376). 41 is the third
            // generation's onboard-mode command, so 41 00 hands the board
            // back. OpenRGB has no generation entry for this product and
            // sends 3B. 61 is never sent to it: there it is the image of
            // the screen (apex-oled README.md:18-26 and 54-56).
            Apex(SteelSeriesFamily.ApexProGen3, 1, gated: false, gen3 => 0x3A, claim: Out(0x41, 0x01), gen3HandBack: true),

            // Apex 3. Ten zones in one report that the frame fills
            // exactly: 0B 00 and ten colors. GG streams it at 46 to 47 ms
            // (the captures of OpenRGB issue 1644), and OpenRGB sends it
            // behind a brightness report, 0A, that is left out here
            // (SteelSeriesApexTZoneController.cpp:56-80). No reference has
            // a frame that returns the stored lighting.
            new()
            {
                Family = SteelSeriesFamily.Apex3, Interface = 3, Report = SteelSeriesReport.Output,
                RepaintMs = 50, ReassertMs = ReassertMs,
                Color = (_, _, r, g, b) => new[] { Out(Fill(new byte[] { 0x0B, 0x00 }, 10, r, g, b)) },
            },

            // Apex 3 TKL. Eight zones: 21, the mask FF, eight colors, and
            // 22 FF for the rainbow wave it shows when plugged in (OpenRGB
            // SteelSeriesApex8ZoneController.h:65-79 and
            // SteelSeriesApex8ZoneController.cpp:78-93). GG streams the
            // color frame every 15.8 ms (the captures of OpenRGB issue
            // 1902).
            new()
            {
                Family = SteelSeriesFamily.Apex3Tkl, Interface = 1, Report = SteelSeriesReport.Output,
                RepaintMs = 33, ReassertMs = ReassertMs,
                Color = (_, _, r, g, b) => new[] { Out(Fill(new byte[] { 0x21, 0xFF }, 8, r, g, b)) },
                HandBack = _ => new[] { Out(0x22, 0xFF) },
            },

            // Apex M750. Feature reports alone: three that take the
            // lighting, then 00 00 01 8E 01 03 06 16 and the colors of a
            // grid of 6 by 22 slots (OpenRGB
            // SteelSeriesApexMController.cpp:50-128, signal-plugins
            // Steelseries_Apex_M750_Keyboard.js:50-77 and 103-136). Both
            // build the three claims in one buffer and leave the FF of the
            // second in the third, so the third ends 85 FF on the wire,
            // and that is what is sent. The 104 slots with a key get the
            // color, and the 28 with none the filler the Engine puts there
            // (M750Grid). The Engine relays GameSense colors in this frame
            // every 54 ms at the median, and never under 52 (the captures
            // of OpenRGB issue 641). Its own profile lives in the Engine,
            // so no frame brings it back.
            new()
            {
                Family = SteelSeriesFamily.ApexM750, Interface = 2, Report = SteelSeriesReport.Feature,
                GapMs = 10, RepaintMs = 55, ReassertMs = ReassertMs,
                Claim = _ => new[]
                {
                    Feat(0x00, 0x00, 0x00, 0x01, 0x00, 0x85),
                    Feat(0x00, 0x00, 0x00, 0x03, 0x01, 0x00, 0xFF),
                    Feat(0x00, 0x00, 0x00, 0x01, 0x00, 0x85, 0xFF),
                },
                Color = (_, _, r, g, b) => new[] { Feat(M750Grid(r, g, b)) },
            },

            // Apex of 2013 and Apex 350. Five zones in one report: 07 00,
            // then a color and a level per zone, 08 the brightest, for the
            // main keys, the numpad, the function row, the macro keys and
            // the logo (OpenRGB SteelSeriesOldApexController.cpp:85-124
            // with the level from SteelSeriesOldApexController.h:24-34,
            // ApexCtl src/Main.hs:50-58 and 70-91). It is an output report
            // to interface 0 in both (Main.hs:222-229), and ApexCtl waits
            // 10 ms after a command (Main.hs:236-239).
            new()
            {
                Family = SteelSeriesFamily.Apex2013, Interface = 0, Report = SteelSeriesReport.Output,
                RepaintMs = 50, ReassertMs = ReassertMs,
                Color = (_, _, r, g, b) =>
                {
                    var data = new byte[2 + 5 * 4];
                    data[0] = 0x07;
                    for (int zone = 0; zone < 5; zone++)
                    {
                        int at = 2 + zone * 4;
                        data[at] = r;
                        data[at + 1] = g;
                        data[at + 2] = b;
                        data[at + 3] = 0x08;
                    }
                    return new[] { Out(data) };
                },
            },
        };

        /// <summary>One per-key Apex row. <paramref name="packet"/> answers
        /// the packet number for a firmware generation.
        /// <paramref name="claim"/> is the claim of a board that is of the
        /// third generation whatever its firmware, or null for one whose
        /// claim follows the version it reports, and
        /// <paramref name="gen3HandBack"/> says the same of the hand-back.
        /// GG leaves 65 ms between two of these reports, and each takes 11
        /// to 26 ms on the wire, so the claim and the first color are that
        /// far apart too.</summary>
        private static SteelSeriesFamilyInfo Apex(SteelSeriesFamily family, int iface, bool gated,
            Func<bool, byte> packet, SteelSeriesFrame? claim, bool gen3HandBack)
            => new()
            {
                Family = family, Interface = iface, Report = SteelSeriesReport.Feature,
                GapMs = 66, RepaintMs = 66, ReassertMs = ReassertMs, Gated = gated,
                Claim = gen3 => claim is SteelSeriesFrame fixedClaim ? new[] { Copy(fixedClaim) }
                    : gated && gen3 ? new[] { Feat(0x4B) }
                    : NoFrames,
                Color = (_, gen3, r, g, b) => new[] { Feat(PerKey(packet(gen3), r, g, b)) },
                HandBack = gen3 => gen3HandBack || (gated && gen3) ? new[] { Out(0x41, 0x00) } : new[] { Out(0x3B) },
            };

        private static SteelSeriesFrame Copy(SteelSeriesFrame frame)
            => new(frame.Report, (byte[])frame.Buffer.Clone());

        private static readonly SteelSeriesFamilyInfo[] s_byFamily = Index();

        private static SteelSeriesFamilyInfo[] Index()
        {
            var byFamily = new SteelSeriesFamilyInfo[Enum.GetValues(typeof(SteelSeriesFamily)).Length];
            foreach (var info in Families) byFamily[(int)info.Family] = info;
            return byFamily;
        }

        /// <summary>A family's row, or null for
        /// <see cref="SteelSeriesFamily.None"/>.</summary>
        internal static SteelSeriesFamilyInfo Info(SteelSeriesFamily family)
            => (uint)family < (uint)s_byFamily.Length ? s_byFamily[(int)family] : null;

        internal static SteelSeriesFrame[] Claim(SteelSeriesFamily family, bool gen3)
            => Info(family)?.Claim(gen3) ?? NoFrames;

        /// <summary>The frames of one color. <paramref name="report"/> is
        /// the type the scan chose for the collection
        /// (<see cref="SteelSeriesPlan.ColorReport"/>), which only a family
        /// with two types reads.</summary>
        internal static SteelSeriesFrame[] Color(SteelSeriesFamily family, SteelSeriesReport report, bool gen3,
            byte r, byte g, byte b)
            => Info(family)?.Color(report, gen3, r, g, b) ?? NoFrames;

        internal static SteelSeriesFrame[] HandBack(SteelSeriesFamily family, bool gen3)
            => Info(family)?.HandBack(gen3) ?? NoFrames;

        // ─────────────────────────────────────────────
        //  The frames that need more than a list of bytes
        // ─────────────────────────────────────────────

        /// <summary>A head followed by the same color <paramref name="count"/>
        /// times.</summary>
        private static byte[] Fill(byte[] head, int count, byte r, byte g, byte b)
        {
            var data = new byte[head.Length + count * 3];
            head.CopyTo(data, 0);
            for (int i = 0; i < count; i++)
            {
                int at = head.Length + i * 3;
                data[at] = r;
                data[at + 1] = g;
                data[at + 2] = b;
            }
            return data;
        }

        /// <summary>One report per zone of the wireless Aerox and Prime
        /// mice: the command, 01, the zone and its color.</summary>
        private static SteelSeriesFrame[] Zones(byte command, int count, byte r, byte g, byte b)
        {
            var frames = new SteelSeriesFrame[count];
            for (int zone = 0; zone < count; zone++) frames[zone] = Out(command, 0x01, (byte)zone, r, g, b);
            return frames;
        }

        /// <summary>
        /// The 5B color cycle of a Rival 310, Sensei 310 or Sensei TEN LED
        /// holding one color. d[2] the LED, d[3..4] the cycle's duration in
        /// milliseconds, little endian, d[19] 01 for a cycle that does not
        /// repeat, d[23] the mask of buttons that start it, d[27] the
        /// number of points, d[28..30] the start color, then each point as
        /// a color and its distance from the point before (rival310-re
        /// 5B.md:6-19, rivalcfg devices/rival310.py:4-12 with
        /// handlers/rgbgradient.py:236-278, libratbag
        /// driver-steelseries.c:761-811 and 910-926, where OpenRGB calls
        /// d[19] the "Static color flag",
        /// SteelSeriesSenseiController.cpp:279). A cycle that does not
        /// repeat and that no button starts stays on its start color. The
        /// duration is the Engine's, 10000: with it the frame is byte for
        /// byte the Engine's steady-color frame on a Sensei 310 (the
        /// captures of OpenRGB issue 1103), where rivalcfg sends 1000 and
        /// OpenRGB 0.
        /// </summary>
        private static byte[] Cycle310(byte led, byte r, byte g, byte b)
        {
            var data = new byte[35];
            data[0] = 0x5B;
            data[2] = led;
            data[3] = 0x10;
            data[4] = 0x27;
            data[19] = 0x01;
            data[27] = 0x01;
            data[28] = r;
            data[29] = g;
            data[30] = b;
            data[31] = r;
            data[32] = g;
            data[33] = b;
            return data;
        }

        /// <summary>Where the Engine places each Rival 600 LED: the wheel,
        /// the logo, then the left and right LED of the strip's top, middle
        /// and bottom (the LED numbers in rivalcfg
        /// devices/rival600.py:76-163, the positions from the Engine's
        /// frames in the captures of OpenRGB issue 833).</summary>
        private static readonly (ushort X, ushort Y)[] Rival600Leds =
        {
            (0x0055, 0x0046), (0x0055, 0x010E),
            (0x0032, 0x0091), (0x0078, 0x0091),
            (0x002D, 0x00B4), (0x007D, 0x00B4),
            (0x0019, 0x00D2), (0x0091, 0x00D2),
        };

        /// <summary>
        /// The 05 color cycle of one Rival 600 LED, as the Engine sends it
        /// for a static color. The layout is the Rival 310's with the LED
        /// given twice and four more bytes in front of the duration: d[2]
        /// the LED, d[3..4] and d[5..6] its x and y, d[7] the LED again,
        /// d[8..9] the duration, d[24] 01 for a cycle that does not repeat,
        /// d[25] the button mask, d[29] the number of points, d[30..32] the
        /// start color and the points after it (rivalcfg
        /// devices/rival600.py:4-12, libratbag driver-steelseries.c:928-945).
        ///
        /// <para>rivalcfg and libratbag send zeros for x and y and one
        /// point. The Engine's frame carries the LED's position and four
        /// points: red, green and blue, which are the template of its
        /// color-shift effect and the same in every static frame it sends,
        /// black included, and the color last. Two references send the
        /// vendor's shape and each has hardware behind it: the Engine's
        /// own capture (OpenRGB issue 833, the static frames for green, red
        /// and blue), and SignalRGB, which streams it on Windows on every
        /// render (signal-plugins Steelseries_Rival_600_Mouse.js:82-125).
        /// So the vendor's frame is the one built, byte for byte, the
        /// duration included: BE 14 on seven LEDs and 10 27 on the eighth
        /// in that capture. SignalRGB sends 10 27 on every LED. The three
        /// template points are never shown: the cycle does not repeat and
        /// no button starts it, so the LED stays on the start color.</para>
        /// </summary>
        private static byte[] Cycle600(byte led, byte r, byte g, byte b)
        {
            var (x, y) = Rival600Leds[led];
            var data = new byte[49];
            data[0] = 0x05;
            data[2] = led;
            data[3] = (byte)x;
            data[4] = (byte)(x >> 8);
            data[5] = (byte)y;
            data[6] = (byte)(y >> 8);
            data[7] = led;
            // The duration as the Engine sends it: BE 14 on seven LEDs and
            // 10 27 on the eighth.
            data[8] = led == 7 ? (byte)0x10 : (byte)0xBE;
            data[9] = led == 7 ? (byte)0x27 : (byte)0x14;
            data[24] = 0x01;
            data[29] = 0x04;
            data[30] = r;
            data[31] = g;
            data[32] = b;
            data[33] = 0xFF;
            data[38] = 0xFF;
            data[40] = 0x54;
            data[43] = 0xFF;
            data[44] = 0x54;
            data[45] = r;
            data[46] = g;
            data[47] = b;
            data[48] = 0x56;
            return data;
        }

        /// <summary>
        /// The first part of the Rival 650's LED program: 03 00 00 00 30
        /// 00, an offset of 0 and a length of 0x30, then the program itself
        /// from d[6], which has the layout of the Rival 310's cycle from
        /// its duration on. d[6..7] the duration, 10 27, d[22] 01 for a
        /// cycle that does not repeat, d[30] the number of points, four,
        /// d[31..33] the start color. The first three points are left
        /// black at distance 0 and the fourth is the color
        /// (signal-plugins Steelseries_Rival_650_Mouse.js:98-118).
        /// </summary>
        private static byte[] LedProgram(byte r, byte g, byte b)
        {
            const int points = 4, last = 34 + (points - 1) * 4;
            var data = new byte[last + 3];
            data[0] = 0x03;
            data[4] = 0x30;
            data[6] = 0x10;
            data[7] = 0x27;
            data[22] = 0x01;
            data[30] = (byte)points;
            data[31] = r;
            data[32] = g;
            data[33] = b;
            data[last] = r;
            data[last + 1] = g;
            data[last + 2] = b;
            return data;
        }

        /// <summary>The Prime's 62: d[1] the number of points, d[2..4] the
        /// start color, d[5..6] the cycle's duration, d[17..19] the color
        /// of the one point and d[20] its place, FF. The fields are read
        /// from the Engine's own frames to a Prime+ (the captures of
        /// OpenRGB issue 4354), where a color shift carries 62 04, the
        /// duration EC 13 and four points of a color and a place each from
        /// d[17] on, and a steady color 62 01 with a duration of 00 00.</summary>
        private static byte[] PrimeColor(byte r, byte g, byte b)
        {
            var data = new byte[21];
            data[0] = 0x62;
            data[1] = 0x01;
            data[2] = r;
            data[3] = g;
            data[4] = b;
            data[17] = r;
            data[18] = g;
            data[19] = b;
            data[20] = 0xFF;
            return data;
        }

        /// <summary>The HID usages of the per-key Apex frame, in the order
        /// OpenRGB sends them (SteelSeriesApexController.cpp:23-37). "No
        /// keyboard has every key fitted but the same format packet can be
        /// sent regardless of model and the firmware will pick the keys
        /// applicable to it, ignoring the rest" (SteelSeriesApexRegions.h:22-26),
        /// so every board gets all of them.</summary>
        internal static readonly byte[] ApexKeys =
        {
            0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D,
            0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
            0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x21,
            0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B,
            0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x32, 0x33, 0x34, 0x35, 0x36,
            0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40,
            0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A,
            0x4B, 0x4C, 0x4D, 0x4E, 0x4F, 0x50, 0x51, 0x52, 0x64, 0xE0,
            0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xF0, 0x31, 0x87,
            0x88, 0x89, 0x8A, 0x8B, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
            0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F, 0x60, 0x61, 0x62,
            0x63, 0xFB,
        };

        private static byte[] PerKey(byte packet, byte r, byte g, byte b)
        {
            var data = new byte[2 + ApexKeys.Length * 4];
            data[0] = packet;
            data[1] = (byte)ApexKeys.Length;
            for (int i = 0; i < ApexKeys.Length; i++)
            {
                int at = 2 + i * 4;
                data[at] = ApexKeys[i];
                data[at + 1] = r;
                data[at + 2] = g;
                data[at + 3] = b;
            }
            return data;
        }

        /// <summary>The slots of the Apex M750's frame, 6 rows of 22
        /// (OpenRGB SteelSeriesApexMController.cpp:18-37).</summary>
        internal const int M750Slots = 6 * 22;

        /// <summary>The 28 slots of that grid with no key behind them, the
        /// NA entries of OpenRGB's table, counted row by row from the
        /// frame's first slot. SignalRGB's key map names the other 104
        /// (signal-plugins Steelseries_Apex_M750_Keyboard.js:41-48).</summary>
        internal static readonly byte[] M750NoKey =
        {
            3, 5, 6, 7, 8, 13, 14, 18, 20,
            33, 35, 36, 37, 39,
            56, 58, 59, 60, 61, 65,
            79,
            101,
            115, 120, 128, 129, 130, 131,
        };

        /// <summary>
        /// The Apex M750's color frame: the head, then three bytes per
        /// slot. A slot with a key gets the color. One with none gets
        /// FF 32 00, as the Engine fills it: in the 72 frames it relays in
        /// the captures of OpenRGB issue 641, those 28 slots hold FF 32 00
        /// every time and every other slot the color of its key. OpenRGB
        /// sends the same filler (SteelSeriesApexMController.cpp:108-122).
        /// SignalRGB writes the 104 key slots alone and leaves these 28 to
        /// its engine (signal-plugins Steelseries_Apex_M750_Keyboard.js:116-133).
        /// The vendor's bytes are the ones sent.
        /// </summary>
        private static byte[] M750Grid(byte r, byte g, byte b)
        {
            var data = Fill(new byte[] { 0x00, 0x00, 0x01, 0x8E, 0x01, 0x03, 0x06, 0x16 }, M750Slots, r, g, b);
            foreach (byte slot in M750NoKey)
            {
                int at = 8 + slot * 3;
                data[at] = 0xFF;
                data[at + 1] = 0x32;
                data[at + 2] = 0x00;
            }
            return data;
        }

        // ─────────────────────────────────────────────
        //  The Apex firmware gate
        // ─────────────────────────────────────────────

        /// <summary>The firmware query of an Apex board: the output report
        /// 90, answered by an input report that starts 90 and carries the
        /// version as text, 90 00 31 2E 31 39 2E 37 for 1.19.7 (OpenRGB
        /// SteelSeriesApexBaseController.cpp:93-128, apex-web
        /// PROTOCOL.md:182-190). It is asked once, after the open and
        /// before the claim, and never between a claim and its hand-back in
        /// one open: on a 2023 board it also puts the stored configuration
        /// back over anything sent and not stored (apex-web
        /// PROTOCOL.md:83-85).</summary>
        internal static SteelSeriesFrame VersionQuery() => Out(0x90);

        internal const byte VersionCommand = 0x90;

        /// <summary>
        /// Whether a reply to <see cref="VersionQuery"/> names firmware
        /// 1.19.7 or later. <paramref name="reply"/> is the report's data,
        /// with no report ID in front. OpenRGB drops an echoed 90 where the
        /// reply starts with one, "Apex Pro Gen 2+ needs the first char
        /// dropped", drops every zero byte, and reads the rest as
        /// major.minor.patch (SteelSeriesApexBaseController.cpp:114-127 and
        /// SteelSeriesApexController.cpp:228-252), and this reads it the
        /// same way. A reply that does not read as a version is an older
        /// firmware's: the frames of the board's first reading go out,
        /// which is what OpenRGB sends every board it does not gate.
        /// </summary>
        internal static bool IsGen3(ReadOnlySpan<byte> reply)
        {
            if (!TryReadVersion(reply, out int major, out int minor, out int patch)) return false;
            if (major != Gen3Major) return major > Gen3Major;
            if (minor != Gen3Minor) return minor > Gen3Minor;
            return patch >= Gen3Patch;
        }

        /// <summary>Whether an input report is the reply to
        /// <see cref="VersionQuery"/>: one that starts with the echoed 90,
        /// or, for a firmware that does not echo, one that reads as a
        /// version from its first byte. The control collection carries
        /// other reports too, and one of those is not taken for the
        /// reply.</summary>
        internal static bool IsVersionReply(ReadOnlySpan<byte> reply)
            => reply.Length > 0 && (reply[0] == VersionCommand || TryReadVersion(reply, out _, out _, out _));

        private static bool TryReadVersion(ReadOnlySpan<byte> reply, out int major, out int minor, out int patch)
        {
            major = minor = patch = 0;
            Span<int> parts = stackalloc int[3];
            int part = 0, digits = 0;
            for (int i = reply.Length > 0 && reply[0] == VersionCommand ? 1 : 0; i < reply.Length; i++)
            {
                byte c = reply[i];
                if (c == 0 || (c == (byte)' ' && part == 0 && digits == 0)) continue;
                if (c >= (byte)'0' && c <= (byte)'9')
                {
                    // A version part is a few digits. More is no version,
                    // and would run past an int.
                    if (++digits > 6) return false;
                    parts[part] = parts[part] * 10 + (c - (byte)'0');
                }
                else if (c == (byte)'.' && digits > 0 && part < 2)
                {
                    part++;
                    digits = 0;
                }
                else
                {
                    break;
                }
            }
            if (part != 2 || digits == 0) return false;
            (major, minor, patch) = (parts[0], parts[1], parts[2]);
            return true;
        }

        // ─────────────────────────────────────────────
        //  The products
        // ─────────────────────────────────────────────

        /// <summary>
        /// One row per product ID in
        /// <see cref="PeripheralProductIds.SteelSeriesMice"/> and
        /// <see cref="PeripheralProductIds.SteelSeriesKeyboards"/>: the
        /// product IDs of rivalcfg's device profiles and of OpenRGB's
        /// detectors (rivalcfg devices/*.py, OpenRGB SteelSeriesDevices.h
        /// and SteelSeriesControllerDetect.cpp:469-555). A product whose
        /// family is <see cref="SteelSeriesFamily.None"/> gets no unit, and
        /// neither does one the lists lack.
        /// </summary>
        internal static readonly (ushort ProductId, SteelSeriesFamily Family)[] Models =
        {
            (0x0616, SteelSeriesFamily.ApexM750),
            (0x1202, SteelSeriesFamily.Apex2013),            // Apex and Apex Fnatic
            (0x1206, SteelSeriesFamily.Apex2013),            // Apex 350

            // An LED of one color, with brightness levels and no color to
            // set (rivalcfg devices/sensei_raw.py:81-113,
            // devices/kanav2.py:67-90): the Sensei [RAW] and its editions,
            // and the Kana v2.
            (0x1362, SteelSeriesFamily.None),
            (0x1369, SteelSeriesFamily.None),
            (0x136D, SteelSeriesFamily.None),
            (0x136F, SteelSeriesFamily.None),
            (0x137A, SteelSeriesFamily.None),
            (0x1380, SteelSeriesFamily.None),
            (0x1390, SteelSeriesFamily.None),

            // No lighting command in any reference: the Kinzu v2, the
            // Rival 95 and its editions, the Rival 100 PC Bang, and the
            // Rival 3 Wireless Gen 2, whose lighting the vendor says cannot
            // be customized (SteelSeries help center article
            // 37028766350349).
            (0x1366, SteelSeriesFamily.None),
            (0x1378, SteelSeriesFamily.None),
            (0x1704, SteelSeriesFamily.None),
            (0x1706, SteelSeriesFamily.None),
            (0x1707, SteelSeriesFamily.None),
            (0x1708, SteelSeriesFamily.None),
            (0x1872, SteelSeriesFamily.None),

            (0x1384, SteelSeriesFamily.Rival300),            // Rival
            (0x1392, SteelSeriesFamily.Rival300),            // Rival Dota 2 Edition
            (0x1394, SteelSeriesFamily.Rival300),            // Rival 300 CS:GO Fade Edition
            (0x1710, SteelSeriesFamily.Rival300),            // Rival 300
            (0x1712, SteelSeriesFamily.Rival300),            // Rival 300 Fallout 4 Edition
            (0x1714, SteelSeriesFamily.Rival300),            // Rival 300 Acer Predator Edition
            (0x1716, SteelSeriesFamily.Rival300),            // Rival 300 CS:GO Fade Edition (stm32)
            (0x1718, SteelSeriesFamily.Rival300),            // Rival 300 HP OMEN Edition
            (0x171A, SteelSeriesFamily.Rival300),            // Rival 300 CS:GO Hyper Beast Edition
            (0x171C, SteelSeriesFamily.Rival300),            // Rival 300 Evil Geniuses Edition

            (0x1702, SteelSeriesFamily.Rival100),            // Rival 100
            (0x170A, SteelSeriesFamily.Rival100),            // Rival 100 (Dell China)
            (0x170B, SteelSeriesFamily.Rival100),            // Rival 100 Dota 2 Edition (retail)
            (0x170C, SteelSeriesFamily.Rival100),            // Rival 100 Dota 2 Edition (Lenovo)
            (0x1729, SteelSeriesFamily.Rival100),            // Rival 110
            (0x1810, SteelSeriesFamily.Rival100),            // Rival 300S, which "works like a Rival 110"
            (0x1814, SteelSeriesFamily.Rival100),            // Rival 105
            (0x1816, SteelSeriesFamily.Rival100),            // Rival 106

            (0x1700, SteelSeriesFamily.Rival700),            // Rival 700
            (0x170E, SteelSeriesFamily.Rival700),            // Rival 500
            (0x1730, SteelSeriesFamily.Rival700),            // Rival 710

            (0x171E, SteelSeriesFamily.Rival310),            // Rival 310 CS:GO Howl Edition
            (0x1720, SteelSeriesFamily.Rival310),            // Rival 310
            (0x1722, SteelSeriesFamily.Rival310),            // Sensei 310
            (0x1736, SteelSeriesFamily.Rival310),            // Rival 310 PUBG Edition
            (0x1832, SteelSeriesFamily.Rival310),            // Sensei TEN
            (0x1834, SteelSeriesFamily.Rival310),            // Sensei TEN CS:GO Neon Rider Edition

            (0x1724, SteelSeriesFamily.Rival600),            // Rival 600
            (0x172E, SteelSeriesFamily.Rival600),            // Rival 600 Dota 2 Edition

            (0x1726, SteelSeriesFamily.Rival650),            // Rival 650 Wireless, through its dongle
            (0x172B, SteelSeriesFamily.Rival650),            // Rival 650 Wireless, on its cable
            (0x1830, SteelSeriesFamily.Rival3Wireless),      // Rival 3 Wireless, through its dongle

            (0x1824, SteelSeriesFamily.Rival3),              // Rival 3, before the vendor's firmware update
            (0x184C, SteelSeriesFamily.Rival3),              // Rival 3
            (0x1870, SteelSeriesFamily.Rival3Gen2),          // Rival 3 Gen 2

            (0x182A, SteelSeriesFamily.Prime),               // Prime Rainbow 6 Siege Black Ice Edition
            (0x182C, SteelSeriesFamily.PrimePlus),           // Prime+
            (0x182E, SteelSeriesFamily.Prime),               // Prime
            (0x1856, SteelSeriesFamily.Prime),               // Prime CS:GO Neo Noir Edition
            (0x184D, SteelSeriesFamily.PrimeMini),           // Prime Mini
            (0x1840, SteelSeriesFamily.PrimeWirelessDongle), // Prime Wireless
            (0x1842, SteelSeriesFamily.PrimeWireless),
            (0x1848, SteelSeriesFamily.PrimeWirelessDongle), // Prime Mini Wireless
            (0x184A, SteelSeriesFamily.PrimeWireless),

            (0x1836, SteelSeriesFamily.Aerox3),              // Aerox 3
            (0x1850, SteelSeriesFamily.Aerox3),              // Aerox 5
            (0x183C, SteelSeriesFamily.Rival5),              // Rival 5
            (0x183E, SteelSeriesFamily.Rival5),              // Rival 5 Destiny Edition

            (0x1838, SteelSeriesFamily.AeroxWirelessDongle), // Aerox 3 Wireless
            (0x183A, SteelSeriesFamily.AeroxWireless),
            (0x1878, SteelSeriesFamily.AeroxWirelessDongle), // Aerox 3 Wireless CS2 Dragon Lore Edition
            (0x187A, SteelSeriesFamily.AeroxWireless),
            (0x1890, SteelSeriesFamily.AeroxWirelessDongle), // Aerox 3 Wireless Gen 2
            (0x1852, SteelSeriesFamily.AeroxWirelessDongle), // Aerox 5 Wireless
            (0x1854, SteelSeriesFamily.AeroxWireless),
            (0x185C, SteelSeriesFamily.AeroxWirelessDongle), // Aerox 5 Wireless Destiny 2 Edition
            (0x185E, SteelSeriesFamily.AeroxWireless),
            (0x1860, SteelSeriesFamily.AeroxWirelessDongle), // Aerox 5 Wireless Diablo IV Edition
            (0x1862, SteelSeriesFamily.AeroxWireless),
            (0x1858, SteelSeriesFamily.AeroxWirelessDongle), // Aerox 9 Wireless
            (0x185A, SteelSeriesFamily.AeroxWireless),
            (0x1874, SteelSeriesFamily.AeroxWirelessDongle), // Aerox 9 Wireless WoW Edition
            (0x1876, SteelSeriesFamily.AeroxWireless),

            (0x1610, SteelSeriesFamily.ApexPro),             // Apex Pro
            (0x1612, SteelSeriesFamily.ApexPro),             // Apex 7
            (0x1618, SteelSeriesFamily.ApexPro),             // Apex 7 TKL
            (0x161C, SteelSeriesFamily.ApexPro),             // Apex 5
            (0x1614, SteelSeriesFamily.ApexProTkl),          // Apex Pro TKL
            (0x1620, SteelSeriesFamily.Apex9),               // Apex 9 Mini
            (0x1634, SteelSeriesFamily.Apex9),               // Apex 9 TKL
            (0x1628, SteelSeriesFamily.ApexProTkl2023),      // Apex Pro TKL 2023, wired
            (0x1630, SteelSeriesFamily.ApexProWireless),     // Apex Pro TKL 2023 Wireless
            (0x1632, SteelSeriesFamily.ApexProWireless),
            (0x1640, SteelSeriesFamily.ApexProGen3),         // Apex Pro Gen 3
            (0x1642, SteelSeriesFamily.ApexProTklGen3),      // Apex Pro TKL Gen 3, wired
            (0x1644, SteelSeriesFamily.ApexProWireless),     // Apex Pro TKL Gen 3 Wireless
            (0x1646, SteelSeriesFamily.ApexProWireless),
            (0x161A, SteelSeriesFamily.Apex3),               // Apex 3
            (0x1622, SteelSeriesFamily.Apex3Tkl),            // Apex 3 TKL
        };

        private static readonly Dictionary<ushort, SteelSeriesFamily> s_byProduct = ByProduct();

        private static Dictionary<ushort, SteelSeriesFamily> ByProduct()
        {
            var byProduct = new Dictionary<ushort, SteelSeriesFamily>(Models.Length);
            foreach (var (productId, family) in Models) byProduct.TryAdd(productId, family);
            return byProduct;
        }

        /// <summary>The family of a product ID,
        /// <see cref="SteelSeriesFamily.None"/> for one with no color and
        /// for one the table lacks.</summary>
        internal static SteelSeriesFamily FamilyOf(ushort productId)
            => s_byProduct.TryGetValue(productId, out var family) ? family : SteelSeriesFamily.None;

        // ─────────────────────────────────────────────
        //  The scan's gate
        // ─────────────────────────────────────────────

        /// <summary>The longest output and feature buffer a family sends
        /// with its color as <paramref name="colorReport"/>, each with the
        /// report ID position counted, on either firmware generation. Zero
        /// for a type it sends nothing as.</summary>
        internal static (int Output, int Feature) Longest(SteelSeriesFamilyInfo info, SteelSeriesReport colorReport)
        {
            int output = 0, feature = 0;
            void Count(SteelSeriesFrame frame)
            {
                if (frame.Report == SteelSeriesReport.Output) output = Math.Max(output, frame.Buffer.Length);
                else feature = Math.Max(feature, frame.Buffer.Length);
            }
            foreach (bool gen3 in new[] { false, true })
            {
                foreach (var frame in info.Claim(gen3)) Count(frame);
                foreach (var frame in info.Color(colorReport, gen3, 0, 0, 0)) Count(frame);
                foreach (var frame in info.HandBack(gen3)) Count(frame);
            }
            if (info.Gated) Count(VersionQuery());
            return (output, feature);
        }

        /// <summary>
        /// Whether a present HID collection is the control collection of a
        /// product with a color, and what to send it. Five conditions, all
        /// read from what Windows already parsed, with nothing asked of
        /// the device:
        ///
        /// <para>The vendor is SteelSeries and the product has a family.
        /// The collection sits on the family's interface, where a path with
        /// no interface part is interface 0, as hidapi reports a device
        /// with one interface (hidapi windows/hid.c:515-521 and 568-570).
        /// Its usage page is 0xFFC0 and its usage 0x0001: a Rival 3 has a
        /// second vendor collection, on page 0xFF00, one interface lower,
        /// and OpenRGB's Rival 3 detector changed no color until it was
        /// moved to interface 3, "Usage Page 0xFFC0" (OpenRGB issue 1167).
        /// Its reports are long enough for every frame of the family:
        /// Windows fails a buffer shorter than the report, and a frame is
        /// never cut. And a dongle's collection has the input report its
        /// replies come in.</para>
        ///
        /// <para>A family whose color travels as either report type takes
        /// the first of the two that the collection has at length. A
        /// collection that fails any of this gives no unit, and its row
        /// keeps the path through SteelSeries GG.</para>
        /// </summary>
        internal static bool TryPlan(HidCollectionInfo collection, out SteelSeriesPlan plan)
        {
            plan = default;
            if (collection == null || collection.VendorId != VendorId) return false;
            var info = Info(FamilyOf(collection.ProductId));
            if (info == null) return false;
            int iface = collection.InterfaceNumber < 0 ? 0 : collection.InterfaceNumber;
            if (iface != info.Interface) return false;
            if (collection.UsagePage != ControlUsagePage || collection.Usage != ControlUsage) return false;
            // The echoed command and its status, behind the report ID.
            if (info.Echoes && collection.InputReportLength < 3) return false;
            if (Fits(info, info.Report, collection))
            {
                plan = new SteelSeriesPlan(info, info.Report);
                return true;
            }
            if (info.AltReport is SteelSeriesReport alt && Fits(info, alt, collection))
            {
                plan = new SteelSeriesPlan(info, alt);
                return true;
            }
            return false;
        }

        private static bool Fits(SteelSeriesFamilyInfo info, SteelSeriesReport colorReport, HidCollectionInfo collection)
        {
            var (output, feature) = Longest(info, colorReport);
            return collection.OutputReportLength >= output && collection.FeatureReportLength >= feature;
        }
    }
}
