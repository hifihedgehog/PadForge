using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The HID LampArray frames, parsers and rules, byte for byte (#494).
    /// No LampArray device was written to: every expected byte is one a
    /// reference sends or reads, named beside it, or comes from the bench
    /// keyboard (048D:C193, 318 lamps): its attributes report as one read
    /// returned it, and the layout Windows' own HID parser gives its
    /// descriptor, which takes no device I/O.
    /// </summary>
    public class LampArrayProtocolTests
    {
        private static byte[] Bytes(string hex)
            => hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();

        private static string Hex(byte[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));

        private static string Hex(byte[][] frames) => string.Join(" | ", frames.Select(Hex));

        /// <summary>A unit with the report IDs counted up from
        /// <paramref name="firstId"/> in the order of the example
        /// descriptor: attributes, request, response, multi update, range
        /// update, control.</summary>
        private static LampArrayUnit Unit(int lamps, IReadOnlyList<LampRun> runs, byte firstId = 1, bool control = true)
            => new(@"\\?\hid#vid_048d&pid_c193&mi_01#bench", 0x048D, 0xC193)
            {
                LampCount = lamps,
                AttributesId = firstId,
                RequestId = (byte)(firstId + 1),
                ResponseId = (byte)(firstId + 2),
                RangeId = (byte)(firstId + 4),
                ControlId = control ? (byte)(firstId + 5) : (byte)0,
                HasControl = control,
                FeatureLength = 51,
                Runs = runs,
            };

        /// <summary>The slots of a multi update report in the page's
        /// example descriptor and on the bench board.</summary>
        private const int Slots = 8;

        private static LampRun[] Uniform(int lamps, LampLevels levels)
            => LampArrayProtocol.BuildRuns(Enumerable.Repeat(levels, lamps).ToArray(), Slots);

        /// <summary>What Microsoft's samples and the Lenovo ITE boards
        /// declare: a full byte per color and one intensity level.</summary>
        private static readonly LampLevels OneLevel = new(true, 255, 255, 255, 1);

        // ── The frames (spec rows 1 to 11, 24 and 25) ──

        /// <summary>The control report is the ID and AutonomousMode: 0
        /// takes the lamps from the device, the claim, and 1 gives them
        /// back. The IDs are the device's choice: 6 on the bench board and
        /// in Microsoft's sample, 0x46 on ASUS laptops (lampctl
        /// crates/lamparray/src/lib.rs:68), 0x35 on a Mistel keyboard, whose
        /// host tests the same two frames (mistel-lights
        /// Tests/BaroccoHIDTests/LampArrayReportTests.swift:9-12).</summary>
        [Theory]
        [InlineData(0x06, false, "06 00")]
        [InlineData(0x06, true, "06 01")]
        [InlineData(0x46, false, "46 00")]
        [InlineData(0x35, false, "35 00")]
        [InlineData(0x35, true, "35 01")]
        public void TheControlReport_IsItsIdAndAutonomousMode(byte id, bool autonomous, string expected)
            => Assert.Equal(expected, Hex(LampArrayProtocol.Control(id, autonomous)));

        /// <summary>The request names a lamp in 16 bits, low byte first
        /// (mistel-lights Tests/BaroccoHIDTests/LampArrayReportTests.swift:14-16).</summary>
        [Theory]
        [InlineData(0x02, 0, "02 00 00")]
        [InlineData(0x82, 0x0102, "82 02 01")]
        [InlineData(0x31, 0x0102, "31 02 01")]
        public void TheLampRequest_NamesItsLampLowByteFirst(byte id, int lamp, string expected)
            => Assert.Equal(expected, Hex(LampArrayProtocol.AttributesRequest(id, lamp)));

        /// <summary>The range update: ID, flags, first and last lamp in 16
        /// bits each, then red, green, blue and intensity. Each row is a
        /// frame a reference sends: the bench board's own IDs and lamp
        /// count, the HP OMEN frame "0b0100008f00ff000040" (hp-omen-35l-rgb
        /// README.md:102), the Acer frame "05 01 00 00 0D 00 R G B FF"
        /// (acer-predator-helios-rgb protocol/PROTOCOL.md:204), lampctl's
        /// layout with ID 0x45 (crates/lamparray/src/lib.rs:178-189), an
        /// Acer keyboard's 103 lamps on ID 0x85, a report that is not the
        /// last of its update, and the Mistel host's own test vector
        /// (mistel-lights Tests/BaroccoHIDTests/LampArrayReportTests.swift:18-21).</summary>
        [Theory]
        [InlineData(0x05, true, 0, 317, 0x1E, 0x90, 0xFF, 0x01, "05 01 00 00 3D 01 1E 90 FF 01")]
        [InlineData(0x0B, true, 0, 143, 0xFF, 0x00, 0x00, 0x40, "0B 01 00 00 8F 00 FF 00 00 40")]
        [InlineData(0x05, true, 0, 13, 0x11, 0x22, 0x33, 0xFF, "05 01 00 00 0D 00 11 22 33 FF")]
        [InlineData(0x45, true, 0, 0, 0x00, 0xE5, 0xFF, 0xFF, "45 01 00 00 00 00 00 E5 FF FF")]
        [InlineData(0x85, true, 0, 102, 0xFF, 0x00, 0x00, 0xFF, "85 01 00 00 66 00 FF 00 00 FF")]
        [InlineData(0x05, false, 0, 9, 0x0A, 0x14, 0x1E, 0x01, "05 00 00 00 09 00 0A 14 1E 01")]
        [InlineData(0x34, true, 0, 64, 0xFF, 0xFF, 0x00, 0x01, "34 01 00 00 40 00 FF FF 00 01")]
        public void TheRangeUpdate_IsTheFrameTheReferencesSend(byte id, bool complete, int start, int end,
            byte red, byte green, byte blue, byte intensity, string expected)
        {
            byte[] frame = LampArrayProtocol.RangeUpdate(id, complete, start, end, red, green, blue, intensity);
            Assert.Equal(expected, Hex(frame));
            Assert.Equal(LampArrayProtocol.RangeUpdateLength, frame.Length);
        }

        /// <summary>Windows fails a feature report shorter than the
        /// collection's longest, so the channel pads each frame with zeros
        /// (hidapi windows/hid.c:1279-1300). The bench board's feature
        /// length is 51: its claim, its color and its hand-back each go out
        /// as their own bytes and zeros after.</summary>
        [Fact]
        public void EachFrame_GoesOutPaddedToTheCollectionsLength()
        {
            byte[] scratch = null;
            byte[] paint = LampArrayProtocol.RangeUpdate(0x05, true, 0, 317, 0x1E, 0x90, 0xFF, 0x01);
            byte[] padded = HidReportChannel.Pad(paint, 51, ref scratch);
            Assert.Equal(51, padded.Length);
            Assert.Equal(paint, padded.Take(10));
            Assert.All(padded.Skip(10), b => Assert.Equal(0, b));

            foreach (var frame in new[]
                     {
                         LampArrayProtocol.Control(0x06, false),
                         LampArrayProtocol.RangeUpdate(0x05, true, 0, 317, 0x12, 0x34, 0x56, 0x01),
                         LampArrayProtocol.Control(0x06, true),
                     })
            {
                byte[] sized = HidReportChannel.Pad(frame, 51, ref scratch);
                Assert.Equal(51, sized.Length);
                Assert.Equal(frame, sized.Take(frame.Length));
                Assert.All(sized.Skip(frame.Length), b => Assert.Equal(0, b));
            }
            Assert.Equal("05 01 00 00 3D 01 12 34 56 01",
                Hex(LampArrayProtocol.RangeUpdate(0x05, true, 0, 317, 0x12, 0x34, 0x56, 0x01)));
        }

        /// <summary>Every lamp off in one report, which is within any
        /// lamp's level counts: the hand-back of a device with no control
        /// report.</summary>
        [Fact]
        public void AllOff_IsOneRangeOverEveryLamp_WithEveryChannelAtZero()
        {
            Assert.Equal("05 01 00 00 03 00 00 00 00 00", Hex(LampArrayProtocol.AllOff(Unit(4, null, control: false))));
            Assert.Equal("05 01 00 00 3D 01 00 00 00 00", Hex(LampArrayProtocol.AllOff(Unit(318, Uniform(318, OneLevel)))));
            // The same single report for a unit of several runs.
            var mixed = LampArrayProtocol.BuildRuns(Enumerable.Repeat(OneLevel, 10)
                .Concat(Enumerable.Repeat(new LampLevels(false, 0, 0, 0, 1), 6)).ToArray(), Slots);
            Assert.Equal(2, mixed.Length);
            Assert.Equal("45 01 00 00 0F 00 00 00 00 00", Hex(LampArrayProtocol.AllOff(Unit(16, mixed, firstId: 0x41))));
        }

        // ── The two reports that are read (spec rows 12 to 16) ──

        /// <summary>The attributes report of the bench board, as its one
        /// read returned it, and the Mistel keyboard's, as its host's test
        /// has it (mistel-lights Tests/BaroccoHIDTests/LampArrayReportTests.swift:52-61).</summary>
        [Fact]
        public void TheAttributesReport_GivesTheLampCountTheBoxTheKindAndTheInterval()
        {
            byte[] bench = Bytes("01 3E 01 C8 91 05 00 38 32 04 00 60 6D 00 00 01 00 00 00 C8 7D 00 00");
            Assert.True(LampArrayProtocol.TryParseAttributes(bench, out var attributes));
            Assert.Equal(new LampArrayAttributes(318, 365000, 275000, 28000, 1, 32200), attributes);

            // In a buffer of the collection's own length, as Windows hands
            // it back.
            var sized = new byte[51];
            bench.CopyTo(sized, 0);
            Assert.True(LampArrayProtocol.TryParseAttributes(sized, out var again));
            Assert.Equal(attributes, again);

            byte[] mistel = Bytes("30 41 00 00 E2 04 00 20 C5 01 00 10 27 00 00 01 00 00 00 B8 0B 00 00");
            Assert.True(LampArrayProtocol.TryParseAttributes(mistel, out var keyboard));
            Assert.Equal(new LampArrayAttributes(65, 320000, 116000, 10000, 1, 3000), keyboard);

            // Too short to hold the fields.
            Assert.False(LampArrayProtocol.TryParseAttributes(bench.Take(22).ToArray(), out _));
            Assert.False(LampArrayProtocol.TryParseAttributes(Array.Empty<byte>(), out _));
            Assert.Equal(23, LampArrayProtocol.AttributesLength);
        }

        /// <summary>The response report names its lamp and gives the four
        /// level counts and IsProgrammable at bytes 23 to 27. A device
        /// with a 16-bit InputBinding sends one byte more
        /// (Apex3TklDynamicLighting Driver.c:300-301), which changes
        /// nothing that is read.</summary>
        [Fact]
        public void TheLampResponse_GivesItsLampAndItsLevelCounts_WhateverFollowsThem()
        {
            byte[] response = Bytes("03 05 00 10 27 00 00 20 4E 00 00 00 00 00 00 A0 0F 00 00 01 00 00 00 FF FF FF 01 01 04");
            Assert.Equal(LampArrayProtocol.ResponseLength, response.Length);
            Assert.True(LampArrayProtocol.TryParseLampResponse(response, out int lamp, out var levels));
            Assert.Equal(5, lamp);
            Assert.Equal(new LampLevels(true, 255, 255, 255, 1), levels);

            Assert.True(LampArrayProtocol.TryParseLampResponse(response.Append((byte)0x00).ToArray(), out int wide, out var same));
            Assert.Equal((5, levels), (wide, same));

            // A fixed-color lamp, and a lamp ID past one byte.
            byte[] fixedLamp = (byte[])response.Clone();
            fixedLamp[1] = 0x3D;
            fixedLamp[2] = 0x01;
            fixedLamp[26] = 100;
            fixedLamp[27] = 0;
            Assert.True(LampArrayProtocol.TryParseLampResponse(fixedLamp, out int last, out var backlight));
            Assert.Equal(317, last);
            Assert.Equal(new LampLevels(false, 255, 255, 255, 100), backlight);

            Assert.False(LampArrayProtocol.TryParseLampResponse(response.Take(27).ToArray(), out _, out _));
            Assert.False(LampArrayProtocol.TryParseLampResponse(response.Take(28).ToArray(), out _, out _));
        }

        // ── From a color to what a device takes (spec rows 17 to 22, 26 and 27) ──

        /// <summary>hid-rgb-ctl's rule (src/device.rs:33-41): a channel
        /// with a full byte of steps takes the value as it is, any other
        /// the nearest of its steps, and one with no steps takes 0.</summary>
        [Theory]
        [InlineData(255, 255, 255)]
        [InlineData(128, 100, 50)]
        [InlineData(255, 100, 100)]
        [InlineData(255, 1, 1)]
        [InlineData(128, 1, 1)]
        [InlineData(127, 1, 0)]
        [InlineData(200, 0, 0)]
        [InlineData(0, 100, 0)]
        [InlineData(1, 100, 0)]
        [InlineData(3, 100, 1)]
        [InlineData(254, 254, 253)]
        [InlineData(200, 300, 200)]
        public void AValueIsScaledToTheChannelsLevelCount(int value, int levels, int expected)
            => Assert.Equal(expected, LampArrayProtocol.Scale(value, levels));

        /// <summary>No scaled value is ever above its channel's count, the
        /// one thing a device rejects a whole report for (HID Usage Tables
        /// 1.6, 26.11.2).</summary>
        [Fact]
        public void AScaledValue_IsNeverAboveTheLevelCount()
        {
            for (int levels = 0; levels <= 255; levels++)
                for (int value = 0; value <= 255; value++)
                {
                    int scaled = LampArrayProtocol.Scale(value, levels);
                    Assert.InRange(scaled, 0, levels);
                    if (value == 255) Assert.Equal(levels, scaled);
                    if (value == 0) Assert.Equal(0, scaled);
                }
        }

        /// <summary>The bench board: 318 lamps that all declare a full
        /// byte per color and one intensity level, so one report paints
        /// them, with intensity 1 for a lit color and 0 for black.</summary>
        [Fact]
        public void AUniformUnit_IsPaintedByOneReport_AtItsLampsFullIntensity()
        {
            var runs = Uniform(318, OneLevel);
            Assert.Equal(new[] { new LampRun(0, 317, OneLevel) }, runs);
            var bench = Unit(318, runs);
            Assert.Equal("05 01 00 00 3D 01 1E 90 FF 01", Hex(LampArrayProtocol.Frames(bench, 0x1E90FF)));
            Assert.Equal("05 01 00 00 3D 01 00 00 00 00", Hex(LampArrayProtocol.Frames(bench, 0x000000)));
            Assert.Equal("05 01 00 00 3D 01 01 00 00 01", Hex(LampArrayProtocol.Frames(bench, 0x010000)));

            // Counts below a full byte: each channel at its nearest step,
            // and the intensity at its count, which is full brightness.
            var coarse = Unit(4, Uniform(4, new LampLevels(true, 100, 100, 100, 255)));
            Assert.Equal("05 01 00 00 03 00 64 32 00 FF", Hex(LampArrayProtocol.Frames(coarse, 0xFF8000)));
            Assert.Equal("05 01 00 00 03 00 00 00 00 00", Hex(LampArrayProtocol.Frames(coarse, 0x000000)));
            // A color too dim for the nearest step of a coarse channel
            // rounds to none. It is not black, so the intensity stays.
            Assert.Equal("05 01 00 00 03 00 00 00 00 FF", Hex(LampArrayProtocol.Frames(coarse, 0x010000)));
        }

        /// <summary>Lamps that differ are painted run by run, each at its
        /// own counts, and only the last report closes the update. A
        /// fixed-color lamp gets no color, and its intensity is the
        /// color's brightest channel, lit at its lowest level when the
        /// scaling would round it off.</summary>
        [Fact]
        public void LampsThatDiffer_ArePaintedRunByRun_AndTheLastReportClosesTheUpdate()
        {
            var fixedLamp = new LampLevels(false, 255, 255, 255, 1);
            var lamps = Enumerable.Repeat(OneLevel, 10).Concat(Enumerable.Repeat(fixedLamp, 2)).ToArray();
            var runs = LampArrayProtocol.BuildRuns(lamps, Slots);
            Assert.Equal(new[] { new LampRun(0, 9, OneLevel), new LampRun(10, 11, fixedLamp) }, runs);

            var unit = Unit(12, runs);
            Assert.Equal("05 00 00 00 09 00 0A 14 1E 01 | 05 01 0A 00 0B 00 00 00 00 01",
                Hex(LampArrayProtocol.Frames(unit, 0x0A141E)));
            Assert.Equal("05 00 00 00 09 00 00 00 00 00 | 05 01 0A 00 0B 00 00 00 00 00",
                Hex(LampArrayProtocol.Frames(unit, 0x000000)));

            // The line the diagnostics log gets for them.
            Assert.Equal("0-9 R255/G255/B255/I1, 10-11 fixed I1", LampArrayProtocol.DescribeRuns(runs));
            Assert.Equal("0-317 R255/G255/B255/I1", LampArrayProtocol.DescribeRuns(Uniform(318, OneLevel)));
            Assert.Equal("no runs", LampArrayProtocol.DescribeRuns(null));
            Assert.Equal("no runs", LampArrayProtocol.DescribeRuns(Array.Empty<LampRun>()));
        }

        /// <summary>One fixed-color lamp with 100 intensity levels, a
        /// single-color keyboard backlight (crosrgbkblight
        /// croskblight/croskblight.cpp:428-429): the color's brightest
        /// channel sets how bright it is.</summary>
        [Theory]
        [InlineData(0xFFFFFF, "05 01 00 00 00 00 00 00 00 64")]
        [InlineData(0x808080, "05 01 00 00 00 00 00 00 00 32")]
        [InlineData(0x010101, "05 01 00 00 00 00 00 00 00 01")]
        [InlineData(0x000000, "05 01 00 00 00 00 00 00 00 00")]
        [InlineData(0x0000FF, "05 01 00 00 00 00 00 00 00 64")]
        [InlineData(0x004000, "05 01 00 00 00 00 00 00 00 19")]
        public void AFixedColorLamp_TakesTheColorsBrightnessAsItsIntensity(int rgb, string expected)
        {
            var unit = Unit(1, Uniform(1, new LampLevels(false, 255, 255, 255, 100)));
            Assert.Equal(expected, Hex(LampArrayProtocol.Frames(unit, rgb)));
        }

        /// <summary>A lamp with no intensity level at all is never sent
        /// one: 1 would be above its count.</summary>
        [Fact]
        public void ALampWithNoIntensityLevel_IsNeverSentOne()
        {
            var noLevels = Unit(2, Uniform(2, new LampLevels(false, 0, 0, 0, 0)));
            Assert.Equal("05 01 00 00 01 00 00 00 00 00", Hex(LampArrayProtocol.Frames(noLevels, 0xFFFFFF)));
            var noGain = Unit(2, Uniform(2, new LampLevels(true, 255, 255, 255, 0)));
            Assert.Equal("05 01 00 00 01 00 FF FF FF 00", Hex(LampArrayProtocol.Frames(noGain, 0xFFFFFF)));
            // A color channel the lamp does not have stays 0.
            var redGreen = Unit(2, Uniform(2, new LampLevels(true, 16, 16, 0, 1)));
            Assert.Equal("05 01 00 00 01 00 10 08 00 01", Hex(LampArrayProtocol.Frames(redGreen, 0xFF80FF)));
        }

        /// <summary>An array cut into more runs than a device must take
        /// back to back is painted as one run at the smallest counts,
        /// hid-rgb-ctl's rule for a uniform color (src/device.rs:572-590):
        /// each color at the smallest count among the programmable lamps,
        /// the intensity at the smallest among all.</summary>
        [Fact]
        public void AnArrayOfTooManyRuns_IsPaintedAsOne_AtTheSmallestCounts()
        {
            // Nine blocks of eight lamps, each block unlike its neighbors.
            var lamps = new List<LampLevels>();
            for (int block = 0; block < 9; block++)
                for (int i = 0; i < 8; i++)
                    lamps.Add(new LampLevels(true, (byte)(255 - block * 10), (byte)(200 - block), (byte)(100 + block),
                        block % 2 == 0 ? (byte)255 : (byte)100));
            var runs = LampArrayProtocol.BuildRuns(lamps, Slots);
            Assert.Equal(new[] { new LampRun(0, 71, new LampLevels(true, 175, 192, 100, 100)) }, runs);
            Assert.Equal("05 01 00 00 47 00 AF C0 64 64", Hex(LampArrayProtocol.Frames(Unit(72, runs), 0xFFFFFF)));
            Assert.Equal("05 01 00 00 47 00 00 00 00 00", Hex(LampArrayProtocol.Frames(Unit(72, runs), 0x000000)));
            // Nine runs are too many whatever the device would take: with
            // one lamp to a multi update it takes 72 reports in a row.
            Assert.Single(LampArrayProtocol.BuildRuns(lamps, 1));
            Assert.Equal(8, LampArrayProtocol.MaxRuns);

            // Eight such blocks are eight runs, each painted at its own.
            var eight = LampArrayProtocol.BuildRuns(lamps.Take(64).ToArray(), Slots);
            Assert.Equal(8, eight.Length);
            var frames = LampArrayProtocol.Frames(Unit(64, eight), 0xFFFFFF);
            Assert.Equal(8, frames.Length);
            Assert.All(frames.Take(7), frame => Assert.Equal(0x00, frame[1]));
            Assert.Equal(0x01, frames[7][1]);
            Assert.Equal("05 00 00 00 07 00 FF C8 64 FF", Hex(frames[0]));
            Assert.Equal("05 01 38 00 3F 00 B9 C1 6B 64", Hex(frames[7]));

            // The reports a device must take back to back are as many as
            // set every lamp through its multi update, eight lamps at a
            // time here, so a small array has fewer.
            var four = new[]
            {
                new LampLevels(true, 255, 255, 255, 1), new LampLevels(true, 100, 255, 255, 1),
                new LampLevels(true, 255, 50, 255, 1), new LampLevels(false, 0, 0, 0, 1),
            };
            Assert.Equal(new[] { new LampRun(0, 3, new LampLevels(true, 100, 50, 255, 1)) }, LampArrayProtocol.BuildRuns(four, Slots));
            var sixteen = Enumerable.Repeat(OneLevel, 8).Concat(Enumerable.Repeat(new LampLevels(true, 100, 100, 100, 1), 8)).ToArray();
            Assert.Equal(2, LampArrayProtocol.BuildRuns(sixteen, Slots).Length);
            var three = sixteen.Take(15).Append(new LampLevels(true, 50, 50, 50, 1)).ToArray();
            Assert.Equal(new[] { new LampRun(0, 15, new LampLevels(true, 50, 50, 50, 1)) }, LampArrayProtocol.BuildRuns(three, Slots));

            // No programmable lamp among them: fixed-color lamps at their
            // smallest intensity count.
            var backlights = Enumerable.Range(0, 4).Select(i => new LampLevels(false, 0, 0, 0, (byte)(100 - i * 10))).ToArray();
            Assert.Equal(new[] { new LampRun(0, 3, new LampLevels(false, 255, 255, 255, 70)) }, LampArrayProtocol.BuildRuns(backlights, Slots));

            Assert.Empty(LampArrayProtocol.BuildRuns(Array.Empty<LampLevels>(), Slots));
            Assert.Empty(LampArrayProtocol.BuildRuns(null, Slots));
        }

        /// <summary>How many reports of one update a device is sure to
        /// take is its own to say: as many multi updates as set every lamp
        /// (HID Usage Tables 1.6, 26.7), so the count depends on how many
        /// lamps one of its multi updates sets. A device with no multi
        /// update report promises one report and no more.</summary>
        [Fact]
        public void TheRunsADeviceTakes_FollowFromItsOwnMultiUpdate()
        {
            var sixteen = Enumerable.Repeat(OneLevel, 8).Concat(Enumerable.Repeat(new LampLevels(true, 100, 100, 100, 1), 8)).ToArray();
            var three = sixteen.Take(15).Append(new LampLevels(true, 50, 50, 50, 1)).ToArray();
            var whole = new[] { new LampRun(0, 15, new LampLevels(true, 100, 100, 100, 1)) };

            // Sixteen lamps to a multi update: one report sets them all.
            Assert.Equal(whole, LampArrayProtocol.BuildRuns(sixteen, 16));
            Assert.Equal(whole, LampArrayProtocol.BuildRuns(sixteen, 255));
            // Four to a multi update: four reports in a row, so three runs
            // go out as they are.
            Assert.Equal(3, LampArrayProtocol.BuildRuns(three, 4).Length);
            Assert.Equal(2, LampArrayProtocol.BuildRuns(sixteen, 4).Length);
            // Fifteen lamps at eight to a multi update still make two.
            Assert.Equal(2, LampArrayProtocol.BuildRuns(sixteen.Skip(1).ToArray(), Slots).Length);

            // No multi update report at all.
            Assert.Equal(whole, LampArrayProtocol.BuildRuns(sixteen, 0));
            Assert.Equal(whole, LampArrayProtocol.BuildRuns(sixteen, -1));
            // Lamps that are all alike are one run whatever the device takes.
            Assert.Equal(new[] { new LampRun(0, 15, OneLevel) },
                LampArrayProtocol.BuildRuns(Enumerable.Repeat(OneLevel, 16).ToArray(), 0));
        }

        /// <summary>A unit whose lamps could not be read is painted by the
        /// frame that needs no level count: the color as it is at
        /// intensity 255, what lampctl sends (crates/lamparray/src/lib.rs:178-196),
        /// and all zeros for black.</summary>
        [Fact]
        public void AUnitWhoseLampsCouldNotBeRead_IsPaintedBlind()
        {
            var blind = Unit(4, null);
            Assert.Equal("05 01 00 00 03 00 11 22 33 FF", Hex(LampArrayProtocol.Frames(blind, 0x112233)));
            Assert.Equal("05 01 00 00 03 00 00 00 00 00", Hex(LampArrayProtocol.Frames(blind, 0x000000)));
            Assert.Equal("45 01 00 00 00 00 00 E5 FF FF", Hex(LampArrayProtocol.Frames(Unit(1, null, firstId: 0x41), 0x00E5FF)));
            // A unit with no runs at all is painted the same way.
            Assert.Equal("05 01 00 00 03 00 11 22 33 FF", Hex(LampArrayProtocol.Frames(Unit(4, Array.Empty<LampRun>()), 0x112233)));
        }

        // ── The timing a device declares ──

        /// <summary>The first color waits the device's own interval after
        /// the claim, a millisecond at least, and two updates are never
        /// closer than that interval or than the dispatcher's tick. The
        /// intervals are the ones devices declare: 2.4 ms and 3 ms on two
        /// keyboards, 20 ms on a desktop and a monitor, 32.2 ms on the
        /// bench board.</summary>
        [Theory]
        [InlineData(0u, 1, 33)]
        [InlineData(1u, 1, 33)]
        [InlineData(2400u, 3, 33)]
        [InlineData(3000u, 3, 33)]
        [InlineData(20000u, 20, 33)]
        [InlineData(32200u, 33, 33)]
        [InlineData(33000u, 33, 33)]
        [InlineData(33001u, 34, 34)]
        [InlineData(250000u, 250, 250)]
        [InlineData(1000000u, 1000, 1000)]
        [InlineData(5000000u, 1000, 1000)]
        [InlineData(uint.MaxValue, 1000, 1000)]
        public void TheWaits_ComeFromTheDevicesOwnInterval(uint intervalUs, int settleMs, int paintMs)
        {
            Assert.Equal(settleMs, LampArrayProtocol.SettleMs(intervalUs));
            Assert.Equal(paintMs, LampArrayProtocol.PaintIntervalMs(intervalUs));
            var unit = Unit(4, null) with { MinUpdateIntervalUs = intervalUs };
            Assert.Equal((settleMs, paintMs), (unit.SettleMs, unit.PaintIntervalMs));
        }

        // ── The descriptor: which reports, and whether they are ours ──

        private static LampValueCap Cap(byte id, ushort link, ushort linkUsage, ushort usage, ushort bits,
            ushort count = 1, bool output = false, ushort? usageMax = null, ushort page = 0x59, ushort linkPage = 0x59)
            => new(output, id, link, linkPage, linkUsage, page, usage, usageMax ?? usage, bits, count);

        /// <summary>The feature value fields of the bench board,
        /// 048D:C193, as Windows' parser lists them
        /// (HidP_GetValueCaps), in its order.</summary>
        private static List<LampValueCap> BenchCaps()
        {
            var caps = new List<LampValueCap>
            {
                Cap(1, 1, 0x02, 0x03, 16), Cap(1, 1, 0x02, 0x08, 32), Cap(1, 1, 0x02, 0x07, 32),
                Cap(1, 1, 0x02, 0x06, 32), Cap(1, 1, 0x02, 0x05, 32), Cap(1, 1, 0x02, 0x04, 32),
                Cap(2, 2, 0x20, 0x21, 16),
                Cap(3, 3, 0x22, 0x21, 16), Cap(3, 3, 0x22, 0x26, 32), Cap(3, 3, 0x22, 0x27, 32),
                Cap(3, 3, 0x22, 0x25, 32), Cap(3, 3, 0x22, 0x24, 32), Cap(3, 3, 0x22, 0x23, 32),
                Cap(3, 3, 0x22, 0x2D, 8), Cap(3, 3, 0x22, 0x2C, 8), Cap(3, 3, 0x22, 0x2B, 8),
                Cap(3, 3, 0x22, 0x2A, 8), Cap(3, 3, 0x22, 0x29, 8), Cap(3, 3, 0x22, 0x28, 8),
                Cap(4, 4, 0x50, 0x55, 8), Cap(4, 4, 0x50, 0x03, 8), Cap(4, 4, 0x50, 0x21, 16, count: 8),
            };
            for (int slot = 0; slot < 8; slot++)
                for (ushort usage = 0x54; usage >= 0x51; usage--)
                    caps.Add(Cap(4, 4, 0x50, usage, 8));
            caps.AddRange(new[]
            {
                Cap(5, 5, 0x60, 0x55, 8), Cap(5, 5, 0x60, 0x62, 16), Cap(5, 5, 0x60, 0x61, 16),
                Cap(5, 5, 0x60, 0x54, 8), Cap(5, 5, 0x60, 0x53, 8), Cap(5, 5, 0x60, 0x52, 8), Cap(5, 5, 0x60, 0x51, 8),
                Cap(6, 6, 0x70, 0x71, 8),
            });
            return caps;
        }

        private static List<LampValueCap> Without(List<LampValueCap> caps, Func<LampValueCap, bool> drop)
            => caps.Where(cap => !drop(cap)).ToList();

        private static List<LampValueCap> Replace(List<LampValueCap> caps, Func<LampValueCap, bool> which,
            Func<LampValueCap, LampValueCap> with)
            => caps.Select(cap => which(cap) ? with(cap) : cap).ToList();

        /// <summary>The bench board's descriptor passes, and gives the
        /// report IDs and collections its probe read: 1, 2, 3, 5 and 6,
        /// each in the collection of the same number as its ID but for the
        /// range update, the fifth.</summary>
        [Fact]
        public void TheBenchBoardsFields_AreTheReportsPadForgeDrives()
        {
            var caps = BenchCaps();
            Assert.Equal(62, caps.Count);
            var layout = LampArrayProtocol.CheckShape(caps, 51, out string rejection);
            Assert.Null(rejection);
            Assert.Equal(new LampArrayLayout(1, 2, 3, 5, 6, true, 51)
            {
                AttributesLink = 1, RequestLink = 2, ResponseLink = 3, RangeLink = 5, ControlLink = 6,
                MultiUpdateSlots = 8,
                // #557: its multi update and lamp descriptions are the ones
                // PadForge sends and reads.
                MultiId = 4, MultiLink = 4, InputBindingBits = 8, DescribesLamps = true,
            }, layout);

            // The IDs are the device's choice, and so is their order.
            var asus = caps.Select(cap => cap with { ReportId = (byte)(0x46 - cap.ReportId + 1) }).ToList();
            var other = LampArrayProtocol.CheckShape(asus, 51, out rejection);
            Assert.Null(rejection);
            Assert.Equal((0x46, 0x45, 0x44, 0x42, 0x41),
                (other.AttributesId, other.RequestId, other.ResponseId, other.RangeId, other.ControlId));
        }

        /// <summary>The lamps one multi update sets are the LampId slots
        /// of that report, eight on the bench board and in the page's
        /// example descriptor (hutrr84_-_lighting_and_illumination_page.txt:704-714),
        /// counted however the descriptor declares them. The lamp ID of
        /// any other report is none of them.</summary>
        [Fact]
        public void TheSlotsOfTheMultiUpdate_AreCountedFromItsLampIdFields()
        {
            Assert.Equal(8, LampArrayProtocol.CheckShape(BenchCaps(), 51, out _).MultiUpdateSlots);

            var wide = Replace(BenchCaps(), cap => cap.LinkUsage == 0x50 && cap.UsageMin == 0x21, cap => cap with { ReportCount = 16 });
            Assert.Equal(16, LampArrayProtocol.CheckShape(wide, 99, out _).MultiUpdateSlots);

            // Four slots, each a field of its own.
            var apart = Without(BenchCaps(), cap => cap.LinkUsage == 0x50 && cap.UsageMin == 0x21);
            for (int slot = 0; slot < 4; slot++) apart.Add(Cap(4, 4, 0x50, 0x21, 16));
            Assert.Equal(4, LampArrayProtocol.CheckShape(apart, 51, out _).MultiUpdateSlots);

            // No multi update report, and one typed as Output, which is
            // still what the device says it takes.
            Assert.Equal(0, LampArrayProtocol.CheckShape(Without(BenchCaps(), cap => cap.LinkUsage == 0x50), 51, out _).MultiUpdateSlots);
            var output = Replace(BenchCaps(), cap => cap.LinkUsage == 0x50, cap => cap with { Output = true });
            Assert.Equal(8, LampArrayProtocol.CheckShape(output, 51, out _).MultiUpdateSlots);

            // A lamp ID field of another page's collection is not counted.
            var foreign = BenchCaps();
            foreign.Add(Cap(9, 9, 0x50, 0x21, 16, count: 8, linkPage: 0xFF89));
            foreign.Add(Cap(9, 9, 0x50, 0x21, 16, count: 8, page: 0xFF89));
            Assert.Equal(8, LampArrayProtocol.CheckShape(foreign, 51, out _).MultiUpdateSlots);
        }

        /// <summary>The control report is optional (HID Usage Tables 1.6,
        /// 26.10.1), so is the multi update to PadForge, which paints a
        /// device without one in ranges (#557), and a field on another
        /// page, or in a collection of another page, is none of these
        /// reports'.</summary>
        [Fact]
        public void ADeviceWithNoControlReport_IsDriven_AndWhatIsNotUsedIsNotChecked()
        {
            var bare = Without(BenchCaps(), cap => cap.LinkUsage == 0x70);
            var layout = LampArrayProtocol.CheckShape(bare, 51, out string rejection);
            Assert.Null(rejection);
            Assert.False(layout.HasControl);
            Assert.Equal((0, 0), (layout.ControlId, layout.ControlLink));

            Assert.NotNull(LampArrayProtocol.CheckShape(Without(BenchCaps(), cap => cap.LinkUsage == 0x50), 51, out _));
            // The unused fields of a report may be anything.
            var wideBinding = Replace(BenchCaps(), cap => cap.LinkUsage == 0x22 && cap.UsageMin == 0x2D, cap => cap with { BitSize = 16 });
            Assert.NotNull(LampArrayProtocol.CheckShape(wideBinding, 52, out _));

            var foreign = BenchCaps();
            foreign.Add(Cap(9, 9, 0x60, 0x55, 16, page: 0xFF89));
            foreign.Add(Cap(9, 9, 0x60, 0x61, 8, linkPage: 0xFF89));
            Assert.NotNull(LampArrayProtocol.CheckShape(foreign, 51, out _));

            // A descriptor that declares the four channels as one usage
            // range lists them as one field that covers all four. Its
            // report count is taken both ways, as the four values the
            // field describes and as 1.
            foreach (ushort count in new ushort[] { 1, 4 })
            {
                var ranged = Without(BenchCaps(), cap => cap.LinkUsage == 0x60 && cap.UsageMin is >= 0x51 and <= 0x54);
                ranged.Add(Cap(5, 5, 0x60, 0x51, 8, count: count, usageMax: 0x54));
                Assert.NotNull(LampArrayProtocol.CheckShape(ranged, 51, out rejection));
                Assert.Null(rejection);
            }

            // More values than the range has usages go to its last usage,
            // which is then an array and no single channel.
            var overlong = Without(BenchCaps(), cap => cap.LinkUsage == 0x60 && cap.UsageMin is >= 0x51 and <= 0x54);
            overlong.Add(Cap(5, 5, 0x60, 0x51, 8, count: 6, usageMax: 0x54));
            Assert.Null(LampArrayProtocol.CheckShape(overlong, 51, out rejection));
            Assert.Equal("usage 0x54 of its range update report is an array of 3", rejection);
        }

        /// <summary>How many values a usage has in a field as Windows'
        /// parser lists it: the report count for a field of one usage, one
        /// for each usage of a range, and the surplus for the range's last
        /// usage where the count is above the range's length (Microsoft's
        /// value-capability-arrays.md, and Device Class Definition for HID
        /// 1.11, 6.2.2.8).</summary>
        [Theory]
        [InlineData(0x21, 0x21, 1, 0x21, 1)]
        [InlineData(0x21, 0x21, 8, 0x21, 8)]
        [InlineData(0x21, 0x21, 0, 0x21, 1)]
        [InlineData(0x51, 0x54, 4, 0x51, 1)]
        [InlineData(0x51, 0x54, 4, 0x54, 1)]
        [InlineData(0x51, 0x54, 1, 0x52, 1)]
        [InlineData(0x51, 0x54, 6, 0x51, 1)]
        [InlineData(0x51, 0x54, 6, 0x53, 1)]
        [InlineData(0x51, 0x54, 6, 0x54, 3)]
        [InlineData(0x51, 0x54, 5, 0x54, 2)]
        public void TheValuesOfAUsage_AreCountedByItsFieldsRangeAndReportCount(int usageMin, int usageMax, int count, int usage, int expected)
            => Assert.Equal(expected, LampArrayProtocol.ValuesOf(
                Cap(5, 5, 0x60, (ushort)usageMin, 8, count: (ushort)count, usageMax: (ushort)usageMax), (ushort)usage));

        /// <summary>Only Feature reports are driven. A device that types
        /// its range update, or its control report, as Output gets no path,
        /// and the reason says which report.</summary>
        [Fact]
        public void AReportTypedAsOutput_IsTurnedDown()
        {
            var output = Replace(BenchCaps(), cap => cap.LinkUsage == 0x60, cap => cap with { Output = true });
            Assert.Null(LampArrayProtocol.CheckShape(output, 51, out string rejection));
            Assert.Contains("range update report", rejection);
            Assert.Contains("Output report", rejection);

            var control = Replace(BenchCaps(), cap => cap.LinkUsage == 0x70, cap => cap with { Output = true });
            Assert.Null(LampArrayProtocol.CheckShape(control, 51, out rejection));
            Assert.Contains("control report", rejection);
            Assert.Contains("Output report", rejection);

            // Typed both ways, the Output half is still there to be sent
            // to by a host that reads the descriptor otherwise.
            var both = BenchCaps();
            both.Add(Cap(7, 7, 0x60, 0x55, 8, output: true));
            Assert.Null(LampArrayProtocol.CheckShape(both, 51, out rejection));
            Assert.Contains("Output report", rejection);
        }

        /// <summary>Each field PadForge uses must be there once, as one
        /// value of the width the layout gives it. Any other descriptor is
        /// turned down with the report and the usage named.</summary>
        [Theory]
        [InlineData(0x60, 0x55, 16, 1, "usage 0x55 of its range update report is 16 bits where 8 are due")]
        [InlineData(0x60, 0x61, 8, 1, "usage 0x61 of its range update report is 8 bits where 16 are due")]
        [InlineData(0x60, 0x54, 16, 1, "usage 0x54 of its range update report is 16 bits where 8 are due")]
        [InlineData(0x02, 0x03, 8, 1, "usage 0x03 of its attributes report is 8 bits where 16 are due")]
        [InlineData(0x02, 0x08, 16, 1, "usage 0x08 of its attributes report is 16 bits where 32 are due")]
        [InlineData(0x20, 0x21, 16, 2, "usage 0x21 of its lamp request report is an array of 2")]
        [InlineData(0x20, 0x21, 8, 1, "usage 0x21 of its lamp request report is 8 bits where 16 are due")]
        [InlineData(0x22, 0x2C, 1, 1, "usage 0x2C of its lamp response report is 1 bits where 8 are due")]
        [InlineData(0x70, 0x71, 1, 1, "usage 0x71 of its control report is 1 bits where 8 are due")]
        [InlineData(0x60, 0x51, 4, 1, "usage 0x51 of its range update report is 4 bits where 8 are due")]
        [InlineData(0x60, 0x62, 8, 1, "usage 0x62 of its range update report is 8 bits where 16 are due")]
        [InlineData(0x02, 0x07, 16, 1, "usage 0x07 of its attributes report is 16 bits where 32 are due")]
        [InlineData(0x22, 0x21, 8, 1, "usage 0x21 of its lamp response report is 8 bits where 16 are due")]
        [InlineData(0x22, 0x28, 16, 1, "usage 0x28 of its lamp response report is 16 bits where 8 are due")]
        public void AFieldOfAnotherWidth_IsTurnedDown(ushort report, ushort usage, ushort bits, ushort count, string expected)
        {
            var caps = Replace(BenchCaps(), cap => cap.LinkUsage == report && cap.UsageMin == usage,
                cap => cap with { BitSize = bits, ReportCount = count });
            Assert.Null(LampArrayProtocol.CheckShape(caps, 51, out string rejection));
            Assert.Equal(expected, rejection);
        }

        [Fact]
        public void AMissingOrAmbiguousReport_IsTurnedDown()
        {
            string rejection;
            // No response report at all, and none of the other three.
            foreach (var (report, name) in new[]
                     {
                         ((ushort)0x22, "lamp response report"), ((ushort)0x02, "attributes report"),
                         ((ushort)0x20, "lamp request report"), ((ushort)0x60, "range update report"),
                     })
            {
                Assert.Null(LampArrayProtocol.CheckShape(Without(BenchCaps(), cap => cap.LinkUsage == report), 51, out rejection));
                Assert.Equal($"it has no {name} (collection 0x{report:X2}) among its feature reports", rejection);
            }
            Assert.Null(LampArrayProtocol.CheckShape(new List<LampValueCap>(), 51, out rejection));
            Assert.Contains("attributes report", rejection);
            Assert.Null(LampArrayProtocol.CheckShape(null, 51, out rejection));
            Assert.NotNull(rejection);

            // A field that is missing, and one that is there twice.
            Assert.Null(LampArrayProtocol.CheckShape(
                Without(BenchCaps(), cap => cap.LinkUsage == 0x60 && cap.UsageMin == 0x62), 51, out rejection));
            Assert.Equal("its range update report has no usage 0x62", rejection);
            var twice = BenchCaps();
            twice.Add(Cap(5, 5, 0x60, 0x51, 8));
            Assert.Null(LampArrayProtocol.CheckShape(twice, 51, out rejection));
            Assert.Equal("its range update report declares usage 0x51 2 times", rejection);

            // One report's fields under two report IDs, two reports under
            // one, and a report with no ID.
            var spread = Replace(BenchCaps(), cap => cap.LinkUsage == 0x60 && cap.UsageMin == 0x54, cap => cap with { ReportId = 7 });
            Assert.Null(LampArrayProtocol.CheckShape(spread, 51, out rejection));
            Assert.Contains("spread over report IDs 0x05 and 0x07", rejection);
            // One report ID, and the report's collection there twice: the
            // parser takes one collection to build a report in.
            var twoCollections = Replace(BenchCaps(), cap => cap.LinkUsage == 0x60 && cap.UsageMin == 0x54,
                cap => cap with { LinkCollection = 7 });
            Assert.Null(LampArrayProtocol.CheckShape(twoCollections, 51, out rejection));
            Assert.Equal("its range update report (collection 0x60) is declared twice", rejection);
            var shared = Replace(BenchCaps(), cap => cap.LinkUsage == 0x70, cap => cap with { ReportId = 5 });
            Assert.Null(LampArrayProtocol.CheckShape(shared, 51, out rejection));
            Assert.Equal("two of its reports share report ID 0x05", rejection);
            var noId = Replace(BenchCaps(), cap => cap.LinkUsage == 0x20, cap => cap with { ReportId = 0 });
            Assert.Null(LampArrayProtocol.CheckShape(noId, 51, out rejection));
            Assert.Equal("one of its reports has no report ID", rejection);

            // Feature reports too short to hold a lamp response.
            Assert.Null(LampArrayProtocol.CheckShape(BenchCaps(), 28, out rejection));
            Assert.Contains("28 bytes", rejection);
            Assert.NotNull(LampArrayProtocol.CheckShape(BenchCaps(), 29, out _));
        }

        /// <summary>The line a turned-down device gets lists its lighting
        /// fields, the multi update's left out.</summary>
        [Fact]
        public void TheFieldsOfATurnedDownDevice_AreListedForItsLine()
        {
            string listed = LampArrayProtocol.DescribeCaps(BenchCaps());
            Assert.StartsWith("01/02/03 16x1, 01/02/08 32x1", listed);
            Assert.EndsWith("05/60/51 8x1, 06/70/71 8x1", listed);
            Assert.DoesNotContain("/50/", listed);
            Assert.Equal("05/60/51-54 8x1 out", LampArrayProtocol.DescribeCaps(new[] { Cap(5, 5, 0x60, 0x51, 8, output: true, usageMax: 0x54) }));
            Assert.Equal("no lighting fields", LampArrayProtocol.DescribeCaps(new[] { Cap(1, 1, 0x01, 0x30, 8, page: 0x01) }));
            Assert.Equal("no lighting fields", LampArrayProtocol.DescribeCaps(null));
        }

        // ── The frames against the parser's own layout ──

        /// <summary>A stand-in for Windows' parser on a device with the
        /// example descriptor's layout: each field at the byte the layout
        /// gives it, low byte first. <paramref name="move"/> puts one
        /// usage of one report somewhere else, as another descriptor
        /// would.</summary>
        private static LampArrayProtocol.ParserBuild Parser(LampArrayLayout layout, int length = 51,
            (byte Report, ushort Usage, int Offset)? move = null, byte? refuse = null, List<byte> asked = null)
            => (id, link, fields) =>
            {
                asked?.Add(id);
                if (refuse == id) return null;
                var report = new byte[length];
                report[0] = id;
                foreach (var (usage, value) in fields)
                {
                    (int offset, int size) = (id == layout.RangeId, usage) switch
                    {
                        (true, 0x55) => (1, 1),
                        (true, 0x61) => (2, 2),
                        (true, 0x62) => (4, 2),
                        (true, >= 0x51 and <= 0x54) => (6 + usage - 0x51, 1),
                        (false, 0x71) => (1, 1),
                        (false, 0x21) => (1, 2),
                        (false, >= 0x28 and <= 0x2C) => (23 + usage - 0x28, 1),
                        _ => throw new InvalidOperationException($"usage 0x{usage:X2} in report 0x{id:X2}"),
                    };
                    if (move is { } moved && moved.Report == id && moved.Usage == usage) offset = moved.Offset;
                    for (int i = 0; i < size; i++) report[offset + i] = (byte)(value >> (8 * i));
                }
                return report;
            };

        private static readonly LampArrayLayout BenchLayout = new(1, 2, 3, 5, 6, true, 51)
        {
            AttributesLink = 1, RequestLink = 2, ResponseLink = 3, RangeLink = 5, ControlLink = 6,
        };

        /// <summary>The parser builds each report from known values, with
        /// no device I/O, and the bytes must be the builders' own. The
        /// bench board's parser gave "05 01 34 12 78 56 A1 B2 C3 D4" and
        /// "06 01" for these values, which is what the stand-in gives.</summary>
        [Fact]
        public void TheFrames_AgreeWithTheParsersLayout_ForADeviceOfTheExampleDescriptor()
        {
            var asked = new List<byte>();
            Assert.Null(LampArrayProtocol.CrossCheck(BenchLayout, Parser(BenchLayout, asked: asked)));
            Assert.Equal(new byte[] { 5, 6, 2, 3 }, asked);
            Assert.Equal("05 01 34 12 78 56 A1 B2 C3 D4",
                Hex(LampArrayProtocol.RangeUpdate(5, true, 0x1234, 0x5678, 0xA1, 0xB2, 0xC3, 0xD4)));
            Assert.Equal("06 01", Hex(LampArrayProtocol.Control(6, true)));

            // Other IDs, a longer report, and the links handed through.
            var asus = new LampArrayLayout(0x41, 0x42, 0x43, 0x45, 0x46, true, 65)
            {
                AttributesLink = 7, RequestLink = 8, ResponseLink = 9, RangeLink = 11, ControlLink = 12,
            };
            var links = new List<(byte, ushort)>();
            var parser = Parser(asus, 65);
            Assert.Null(LampArrayProtocol.CrossCheck(asus, (id, link, fields) =>
            {
                links.Add((id, link));
                return parser(id, link, fields);
            }));
            Assert.Equal(new[] { ((byte)0x45, (ushort)11), ((byte)0x46, (ushort)12), ((byte)0x42, (ushort)8), ((byte)0x43, (ushort)9) }, links);

            // No control report: the parser is asked for none.
            var bare = BenchLayout with { HasControl = false, ControlId = 0, ControlLink = 0 };
            asked.Clear();
            Assert.Null(LampArrayProtocol.CrossCheck(bare, Parser(bare, asked: asked)));
            Assert.Equal(new byte[] { 5, 2, 3 }, asked);
        }

        /// <summary>A device that lays a report out any other way would
        /// take PadForge's frames for something else. Each such report is
        /// named with both layouts.</summary>
        [Fact]
        public void ADeviceThatLaysAReportOutOtherwise_IsTurnedDown()
        {
            // The last lamp ahead of the first.
            string swapped = LampArrayProtocol.CrossCheck(BenchLayout, (id, link, fields) =>
            {
                var report = Parser(BenchLayout)(id, link, fields);
                if (id == 5) (report[2], report[3], report[4], report[5]) = (report[4], report[5], report[2], report[3]);
                return report;
            });
            Assert.Equal("Windows lays its range update report out as 05 01 78 56 34 12 A1 B2 C3 D4"
                         + " where the LampArray layout is 05 01 34 12 78 56 A1 B2 C3 D4", swapped);

            // The intensity ahead of the colors.
            Assert.StartsWith("Windows lays its range update report out as",
                LampArrayProtocol.CrossCheck(BenchLayout, Parser(BenchLayout, move: (5, 0x54, 10))));
            // AutonomousMode behind a byte of something else.
            Assert.Equal("Windows lays its control report out as 06 00 where the LampArray layout is 06 01",
                LampArrayProtocol.CrossCheck(BenchLayout, Parser(BenchLayout, move: (6, 0x71, 2))));
            // A lamp ID one byte on.
            Assert.StartsWith("Windows lays its lamp request report out as 02 00 34",
                LampArrayProtocol.CrossCheck(BenchLayout, Parser(BenchLayout, move: (2, 0x21, 2))));
            // The level counts one byte on, behind a field of their own.
            string response = LampArrayProtocol.CrossCheck(BenchLayout, Parser(BenchLayout, move: (3, 0x28, 30)));
            Assert.StartsWith("Windows lays its lamp response report out as 03 34 12", response);
            Assert.Contains("bytes 1 to 2 and 23 to 27", response);
            Assert.NotNull(LampArrayProtocol.CrossCheck(BenchLayout, Parser(BenchLayout, move: (3, 0x21, 3))));
            Assert.NotNull(LampArrayProtocol.CrossCheck(BenchLayout, Parser(BenchLayout, move: (3, 0x2C, 28))));

            // A report the parser refuses to build at all.
            Assert.Equal("Windows' parser could not build its range update report",
                LampArrayProtocol.CrossCheck(BenchLayout, Parser(BenchLayout, refuse: 5)));
            Assert.Equal("Windows' parser could not build its control report",
                LampArrayProtocol.CrossCheck(BenchLayout, Parser(BenchLayout, refuse: 6)));
            Assert.Equal("Windows' parser could not build its lamp request report",
                LampArrayProtocol.CrossCheck(BenchLayout, Parser(BenchLayout, refuse: 2)));
            Assert.Equal("Windows' parser could not build its lamp response report",
                LampArrayProtocol.CrossCheck(BenchLayout, Parser(BenchLayout, refuse: 3)));
            // And one it builds too short to hold the frame.
            Assert.NotNull(LampArrayProtocol.CrossCheck(BenchLayout, (id, link, fields) => new[] { id }));
        }

        // ── Lamp by lamp (#557) ──

        private static byte[] Response(int lamp, uint x, uint y, uint purposes, byte binding, LampLevels levels, int length = 29)
        {
            var report = new byte[length];
            report[0] = 0x03;
            BitConverter.TryWriteBytes(report.AsSpan(1), (ushort)lamp);
            BitConverter.TryWriteBytes(report.AsSpan(3), x);
            BitConverter.TryWriteBytes(report.AsSpan(7), y);
            BitConverter.TryWriteBytes(report.AsSpan(11), 9000u);
            BitConverter.TryWriteBytes(report.AsSpan(15), 4000u);
            BitConverter.TryWriteBytes(report.AsSpan(19), purposes);
            report[23] = levels.Red;
            report[24] = levels.Green;
            report[25] = levels.Blue;
            report[26] = levels.Intensity;
            report[27] = levels.Programmable ? (byte)1 : (byte)0;
            if (length > 28) report[28] = binding;
            return report;
        }

        /// <summary>The bench board's own answer for its Esc key, read from
        /// the device: lamp 0 at 21.8 by 67 mm, a control lamp bound to
        /// usage 0x29.</summary>
        [Fact]
        public void ALampDescriptionReadsPositionPurposesAndItsKey()
        {
            var levels = new LampLevels(true, 255, 255, 255, 1);
            var report = Response(0, 21800, 67000, 0x01, 0x29, levels);
            Assert.True(LampArrayProtocol.TryParseLampDescription(report, 8, out var lamp));
            Assert.Equal(new LampDescription(0, 21800, 67000, 9000, 4000, 0x01, levels, 0x29), lamp);

            Assert.True(LampArrayProtocol.TryParseLampDescription(report, 0, out lamp));
            Assert.Equal(0, lamp.InputBinding);

            var wide = Response(7, 1, 2, 0x01, 0x34, levels, length: 30);
            wide[29] = 0x12;
            Assert.True(LampArrayProtocol.TryParseLampDescription(wide, 16, out lamp));
            Assert.Equal(0x1234, lamp.InputBinding);

            Assert.False(LampArrayProtocol.TryParseLampDescription(report.AsSpan(0, 28), 8, out _));
        }

        [Fact]
        public void AKeyboardsControlLampsAreKeys_AndItsOtherLampsGoByPurpose()
        {
            var l = new LampLevels(true, 255, 255, 255, 1);
            var lamps = new[]
            {
                new LampDescription(0, 100, 200, 0, 0, 0x01, l, 0x29),
                new LampDescription(1, 110, 200, 0, 0, 0x01, l, 0),
                new LampDescription(2, 120, 200, 0, 0, 0x02, l, 0),
                new LampDescription(3, 130, 200, 0, 0, 0x04 | 0x02, l, 0),
                new LampDescription(4, 140, 200, 0, 0, 0x08, l, 0),
                new LampDescription(5, 150, 200, 0, 0, 0x10, l, 0),
                new LampDescription(6, 160, 200, 0, 0, 0x01 | 0x04, l, 0x04),
            };
            var map = LampArrayProtocol.LightMapOf(lamps, kind: 1);
            Assert.Equal(new[] { LightKind.Key, LightKind.Key, LightKind.Edge, LightKind.Logo, LightKind.Status, LightKind.Other, LightKind.Key },
                map.Lights.Select(light => light.Kind));
            Assert.Equal(new ushort[] { 0x29, 0, 0, 0, 0, 0, 0x04 }, map.Lights.Select(light => light.Usage));
            Assert.Equal(LightKeySource.Device, map.Lights[0].KeySource);
            Assert.Equal(LightKeySource.None, map.Lights[1].KeySource);
            Assert.Equal(1, map.UnnamedKeyCount);
            Assert.True(map.Positioned);
            Assert.Equal(130f, map.Lights[3].X);

            // On a mouse a control lamp is a zone, numbered in lamp order.
            var mouse = LampArrayProtocol.LightMapOf(lamps, kind: 2);
            Assert.Equal(new[] { 1, 2, 0, 0, 0, 0, 3 }, mouse.Lights.Select(light => light.Zone));
            Assert.False(mouse.HasKeys);
        }

        /// <summary>The page's own example (HID Usage Tables 1.6, 26.11.1):
        /// eight slots, five lamps, the unused slots left zero.</summary>
        [Fact]
        public void AMultiUpdateIsLaidOutAsThePagesExample()
        {
            var lamps = new[]
            {
                new LampValues(0x19, 0xFF, 0x00, 0xFF, 0x80), new LampValues(0x23, 0x80, 0x80, 0xFF, 0xFF),
                new LampValues(0x72, 0x00, 0x00, 0x80, 0xFF), new LampValues(0x56, 0xFF, 0x80, 0x00, 0x80),
                new LampValues(0x64, 0xFF, 0xFF, 0x00, 0xFF),
            };
            var report = LampArrayProtocol.MultiUpdate(0x04, 8, complete: true, lamps);
            Assert.Equal("04 05 01 19 00 23 00 72 00 56 00 64 00 00 00 00 00 00 00 "
                         + "FF 00 FF 80 80 80 FF FF 00 00 80 FF FF 80 00 80 FF FF 00 FF "
                         + "00 00 00 00 00 00 00 00 00 00 00 00",
                LampArrayProtocol.Hex(report, report.Length));
            Assert.Throws<ArgumentException>(() => LampArrayProtocol.MultiUpdate(0x04, 2, false, lamps));
        }

        [Fact]
        public void ALampsChannelValuesFollowTheOneColorRules()
        {
            var full = new LampLevels(true, 255, 255, 255, 1);
            Assert.Equal(((byte)0x11, (byte)0x22, (byte)0x33, (byte)1), LampArrayProtocol.ChannelValues(full, 0x112233));
            Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)0), LampArrayProtocol.ChannelValues(full, 0));
            var fixedLamp = new LampLevels(false, 0, 0, 0, 3);
            Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)1), LampArrayProtocol.ChannelValues(fixedLamp, 0x010000));
            Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)3), LampArrayProtocol.ChannelValues(fixedLamp, 0xFF0000));
        }

        private static LampArrayUnit Unit(int lamps, byte multiId, int slots)
            => new("p", 1, 2)
            {
                LampCount = lamps, RangeId = 5, MultiId = multiId, MultiUpdateSlots = slots,
                LampLevels = Enumerable.Repeat(new LampLevels(true, 255, 255, 255, 1), lamps).ToArray(),
            };

        [Fact]
        public void LampReportsSendEveryDueLampByMultiUpdate_InSlotSizedReports_NoneClosed()
        {
            var unit = Unit(10, 0x04, 4);
            var colors = Enumerable.Range(0, 10).Select(i => i * 0x010101).ToArray();
            var due = new[] { 0, 1, 2, 3, 4, 9 };
            var reports = LampArrayProtocol.LampReports(unit, colors, due);
            Assert.Equal(new[] { 4, 2 }, reports.Select(r => r.Lamps));
            Assert.Equal("04 04 00 00 00 01 00 02 00 03 00", LampArrayProtocol.Hex(reports[0].Frame, 11));
            Assert.Equal("04 02 00 04 00 09 00 00 00 00 00", LampArrayProtocol.Hex(reports[1].Frame, 11));
            Assert.Equal("04 04 04 01 09 09 09 01", LampArrayProtocol.Hex(reports[1].Frame[11..], 8));
        }

        [Fact]
        public void WithoutAMultiUpdate_EveryRunIsOneReport_NoneClosed()
        {
            var unit = Unit(6, 0, 0);
            var colors = new[] { 0xFF0000, 0xFF0000, 0x00FF00, 0x00FF00, 0x00FF00, 0x0000FF };
            var reports = LampArrayProtocol.LampReports(unit, colors, new[] { 0, 1, 2, 3, 4, 5 });
            Assert.Equal(new[] { 2, 3, 1 }, reports.Select(r => r.Lamps));
            Assert.Equal(new[]
            {
                "05 00 00 00 01 00 FF 00 00 01",
                "05 00 02 00 04 00 00 FF 00 01",
                "05 00 05 00 05 00 00 00 FF 01",
            }, reports.Select(r => LampArrayProtocol.Hex(r.Frame, r.Frame.Length)));

            // A gap in the due lamps ends a run.
            reports = LampArrayProtocol.LampReports(unit, colors, new[] { 0, 2, 3 });
            Assert.Equal(new[] { 1, 2 }, reports.Select(r => r.Lamps));
            Assert.Empty(LampArrayProtocol.LampReports(unit, colors, Array.Empty<int>()));
        }

        /// <summary>A whole keyboard in one color takes one report where it took 40,
        /// which is what a dark or plain part of a screen is.</summary>
        [Fact]
        public void LampsThatShareAColor_ShareAReport_WhenThatSavesOne()
        {
            var unit = Unit(318, 0x04, 8);
            var one = Enumerable.Repeat(0x102030, 318).ToArray();
            var all = Enumerable.Range(0, 318).ToArray();
            var report = Assert.Single(LampArrayProtocol.LampReports(unit, one, all));
            Assert.Equal(318, report.Lamps);
            Assert.Equal("05 00 00 00 3D 01 10 20 30 01", LampArrayProtocol.Hex(report.Frame, report.Frame.Length));

            // A run no longer than a report stays with its neighbors in the multi
            // update. A longer one gets its own range, in place.
            var mixed = new int[20];
            for (int i = 0; i < 20; i++) mixed[i] = i is >= 3 and < 12 ? 0xAA0000 : i + 1;
            var reports = LampArrayProtocol.LampReports(Unit(20, 0x04, 8), mixed, Enumerable.Range(0, 20).ToArray());
            Assert.Equal(new[] { 3, 9, 8 }, reports.Select(r => r.Lamps));
            Assert.Equal(new byte[] { 0x04, 0x05, 0x04 }, reports.Select(r => r.Frame[0]));
            Assert.Equal(20, reports.Sum(r => r.Lamps));
        }

        [Fact]
        public void MarkComplete_FlagsEachReportKindInItsOwnPlace()
        {
            var unit = Unit(10, 0x04, 4);
            var multi = LampArrayProtocol.MultiUpdate(0x04, 4, complete: false, new[] { new LampValues(0, 1, 2, 3, 1) });
            LampArrayProtocol.MarkComplete(unit, multi);
            Assert.Equal(1, multi[2]);
            Assert.Equal(1, multi[1]);
            var range = LampArrayProtocol.RangeUpdate(0x05, complete: false, 0, 9, 1, 2, 3, 1);
            LampArrayProtocol.MarkComplete(unit, range);
            Assert.Equal(1, range[1]);
            Assert.Equal(0, range[2]);
        }

        [Fact]
        public void TheMultiUpdateIsKeptOnlyWhenItIsAFeatureReportOfItsOwn_LaidOutAsPadForgeBuildsIt()
        {
            Assert.Equal(4, LampArrayProtocol.CheckShape(BenchCaps(), 51, out _).MultiId);

            var output = Replace(BenchCaps(), cap => cap.LinkUsage == 0x50, cap => cap with { Output = true });
            Assert.Equal(0, LampArrayProtocol.CheckShape(output, 51, out _).MultiId);

            // A channel short of a slot.
            var shortRed = BenchCaps();
            shortRed.Remove(shortRed.Last(cap => cap.LinkUsage == 0x50 && cap.UsageMin == 0x51));
            Assert.Equal(0, LampArrayProtocol.CheckShape(shortRed, 51, out _).MultiId);

            // Its ID shared with another report.
            var shared = Replace(BenchCaps(), cap => cap.LinkUsage == 0x50, cap => cap with { ReportId = 5 });
            Assert.Equal(0, LampArrayProtocol.CheckShape(shared, 51, out _).MultiId);
        }

        /// <summary>A parser that lays a multi update out as Microsoft's
        /// struct does: the lamp IDs as one array, then each slot's red,
        /// green, blue and intensity as fields of their own, in the order
        /// their data indexes give.</summary>
        private static byte[] ParserLike(byte id, IReadOnlyList<LampArrayProtocol.ParserField> fields, bool swapGreenBlue = false)
        {
            var report = new byte[51];
            report[0] = id;
            foreach (var field in fields)
            {
                if (field.Usage == 0x03) report[1] = (byte)field.Value;
                else if (field.Usage == 0x55) report[2] = (byte)field.Value;
                else if (field.Usage == 0x21 && field.Array != null) field.Array.CopyTo(report, 3);
                else if (field.DataIndex >= 100)
                {
                    int slot = (field.DataIndex - 100) / 4, channel = (field.DataIndex - 100) % 4;
                    if (swapGreenBlue && channel is 1 or 2) channel = 3 - channel;
                    report[19 + slot * 4 + channel] = (byte)field.Value;
                }
                else return null;
            }
            return report;
        }

        private static List<LampValueCap> IndexedBenchCaps()
        {
            var caps = BenchCaps();
            // The bench board declares each slot's channels red to
            // intensity, and the parser numbers them in that order.
            int slotOf = 0;
            var result = new List<LampValueCap>();
            var counts = new Dictionary<ushort, int>();
            foreach (var cap in caps)
            {
                if (cap.LinkUsage == 0x50 && cap.UsageMin is >= 0x51 and <= 0x54)
                {
                    counts.TryGetValue(cap.UsageMin, out slotOf);
                    counts[cap.UsageMin] = slotOf + 1;
                    result.Add(cap with { DataIndex = (ushort)(100 + slotOf * 4 + (cap.UsageMin - 0x51)) });
                }
                else result.Add(cap);
            }
            return result;
        }

        [Fact]
        public void TheMultiUpdateIsCheckedAgainstTheParser_ArrayAndSeparateFieldsAlike()
        {
            var caps = IndexedBenchCaps();
            var layout = LampArrayProtocol.CheckShape(caps, 51, out _);
            Assert.True(LampArrayProtocol.CheckMulti(layout, caps, (id, link, fields) => ParserLike(id, fields)));
            Assert.False(LampArrayProtocol.CheckMulti(layout, caps, (id, link, fields) => ParserLike(id, fields, swapGreenBlue: true)));
            Assert.False(LampArrayProtocol.CheckMulti(layout, caps, (id, link, fields) => null));
            Assert.False(LampArrayProtocol.CheckMulti(layout with { MultiId = 0 }, caps, (id, link, fields) => ParserLike(id, fields)));
        }

        /// <summary>The numbering Windows gives a Lenovo Legion keyboard (048D:C193),
        /// read from its parser: every channel sits under one main item, and the
        /// indexes run from the last value, so the first slot's red is the highest.
        /// The check turned that keyboard down and left it to range updates, about 50
        /// of its 318 lamps per paint.</summary>
        private static List<LampValueCap> BackwardIndexedCaps()
        {
            var result = new List<LampValueCap>();
            var counts = new Dictionary<ushort, int>();
            foreach (var cap in BenchCaps())
            {
                if (cap.LinkUsage == 0x50 && cap.UsageMin is >= 0x51 and <= 0x54)
                {
                    counts.TryGetValue(cap.UsageMin, out int slot);
                    counts[cap.UsageMin] = slot + 1;
                    result.Add(cap with { DataIndex = (ushort)(153 - slot * 4 - (cap.UsageMin - 0x51)) });
                }
                else result.Add(cap);
            }
            return result;
        }

        private static byte[] BackwardParserLike(byte id, IReadOnlyList<LampArrayProtocol.ParserField> fields)
        {
            var report = new byte[51];
            report[0] = id;
            foreach (var field in fields)
            {
                if (field.Usage == 0x03) report[1] = (byte)field.Value;
                else if (field.Usage == 0x55) report[2] = (byte)field.Value;
                else if (field.Usage == 0x21 && field.Array != null) field.Array.CopyTo(report, 3);
                else if (field.DataIndex >= 100) report[19 + 153 - field.DataIndex] = (byte)field.Value;
                else return null;
            }
            return report;
        }

        [Fact]
        public void TheMultiUpdateCheck_TakesDataIndexesThatRunBackward()
        {
            var caps = BackwardIndexedCaps();
            var layout = LampArrayProtocol.CheckShape(caps, 51, out _);
            Assert.True(LampArrayProtocol.CheckMulti(layout, caps, (id, link, fields) => BackwardParserLike(id, fields)));
        }

        /// <summary>Either direction still has to land every byte where the report
        /// is built: a parser that puts the fields anywhere else fails both.</summary>
        [Fact]
        public void TheMultiUpdateCheck_StillRefusesAReportLaidOutOtherwise_InBothDirections()
        {
            var caps = BackwardIndexedCaps();
            var layout = LampArrayProtocol.CheckShape(caps, 51, out _);
            Assert.False(LampArrayProtocol.CheckMulti(layout, caps, (id, link, fields) =>
            {
                var built = BackwardParserLike(id, fields);
                if (built != null) (built[19], built[20]) = (built[20], built[19]);
                return built;
            }));
        }

        [Fact]
        public void TheLampDescriptionsAreCheckedAgainstTheParser()
        {
            var layout = LampArrayProtocol.CheckShape(BenchCaps(), 51, out _);
            byte[] Build(byte id, ushort link, IReadOnlyList<(ushort Usage, uint Value)> values, bool swap)
            {
                var report = new byte[51];
                report[0] = id;
                foreach (var (usage, value) in values)
                {
                    int at = usage switch
                    {
                        0x21 => 1, 0x23 => 3, 0x24 => 7, 0x25 => 11, 0x27 => swap ? 19 : 15, 0x26 => swap ? 15 : 19,
                        0x28 => 23, 0x29 => 24, 0x2A => 25, 0x2B => 26, 0x2C => 27, 0x2D => 28, _ => -1,
                    };
                    if (at < 0) return null;
                    int width = usage is 0x21 ? 2 : usage is >= 0x23 and <= 0x27 ? 4 : 1;
                    for (int i = 0; i < width; i++) report[at + i] = (byte)(value >> (8 * i));
                }
                return report;
            }
            Assert.True(LampArrayProtocol.CheckDescriptions(layout, (id, link, values) => Build(id, link, values, false)));
            Assert.False(LampArrayProtocol.CheckDescriptions(layout, (id, link, values) => Build(id, link, values, true)));
            Assert.False(LampArrayProtocol.CheckDescriptions(layout with { DescribesLamps = false },
                (id, link, values) => Build(id, link, values, false)));
        }
    }
}
