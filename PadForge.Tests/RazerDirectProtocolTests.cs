using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Razer feature report byte for byte (#494): every command family
    /// against the bytes the references carry, the reply rules, and the
    /// product table against the research table it was written from. Nothing
    /// here opens a device.
    /// </summary>
    public class RazerDirectProtocolTests
    {
        private const int Color = 0x123456;

        private static byte[] Hex(string text)
            => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();

        /// <summary>One report as a line of text: the transaction ID, the
        /// command, the declared size, and the arguments through that size,
        /// or through the last one that is not zero where a report carries
        /// more than it declares. tools/gen_razer_direct_table.py writes the
        /// research table's reports the same way.</summary>
        internal static string Line(byte[] report)
        {
            int size = report[RazerDirectProtocol.SizeAt];
            int last = RazerDirectProtocol.MaxArguments;
            while (last > 0 && report[RazerDirectProtocol.ArgumentsAt + last - 1] == 0) last--;
            var arguments = report.Skip(RazerDirectProtocol.ArgumentsAt).Take(Math.Max(size, last));
            return $"{report[RazerDirectProtocol.TransactionAt]:X2} {report[RazerDirectProtocol.ClassAt]:X2}/"
                + $"{report[RazerDirectProtocol.CommandAt]:X2} size {size:X2} [{string.Join(" ", arguments.Select(a => a.ToString("X2")))}]";
        }

        private static RazerDirectRow Row(ushort productId)
        {
            var row = RazerDirectTable.Find(productId);
            Assert.NotNull(row);
            return row;
        }

        private static string[] Lines(IEnumerable<byte[]> reports) => reports.Select(Line).ToArray();

        // ─────────────────────────────────────────────
        //  The report and its checksum
        // ─────────────────────────────────────────────

        /// <summary>A name, the report a builder made, the bytes it opens
        /// with and its checksum. Every byte between the two is zero.</summary>
        public static IEnumerable<object[]> Vectors()
        {
            object[] V(string name, byte[] report, string opens, byte crc) => new object[] { name, report, opens, crc };
            const byte led00 = 0x00, led01 = 0x01, led04 = 0x04, led05 = 0x05;

            yield return V("extended static red, LED 05, ID 1F", RazerDirectProtocol.ExtendedStatic(0x1F, led05, 0xFF0000),
                "00 00 1F 00 00 00 09 0F 02 00 05 01 00 00 01 FF 00 00", 0xFE);
            yield return V("the same, ID 9F", RazerDirectProtocol.ExtendedStatic(0x9F, led05, 0xFF0000),
                "00 00 9F 00 00 00 09 0F 02 00 05 01 00 00 01 FF 00 00", 0xFE);
            // openrazer razerchromacommon.c:507, which stores: the layout is
            // the capture's, and only the storage byte differs from ours.
            yield return V("openrazer's capture with storage 01, ID 3F",
                RazerDirectProtocol.Report(0x3F, 0x0F, 0x02, 9, Hex("01 05 01 00 00 01 FF 00 00")),
                "00 00 3F 00 00 00 09 0F 02 01 05 01 00 00 01 FF 00 00", 0xFF);
            yield return V("extended static, LED 00, ID 1F", RazerDirectProtocol.ExtendedStatic(0x1F, led00, Color),
                "00 00 1F 00 00 00 09 0F 02 00 00 01 00 00 01 12 34 56", 0x74);
            yield return V("extended static, LED 04, ID 3F", RazerDirectProtocol.ExtendedStatic(0x3F, led04, Color),
                "00 00 3F 00 00 00 09 0F 02 00 04 01 00 00 01 12 34 56", 0x70);
            yield return V("extended static, LED 01, ID 3F", RazerDirectProtocol.ExtendedStatic(0x3F, led01, Color),
                "00 00 3F 00 00 00 09 0F 02 00 01 01 00 00 01 12 34 56", 0x75);
            yield return V("extended spectrum, LED 05", RazerDirectProtocol.ExtendedSpectrum(0x1F, led05),
                "00 00 1F 00 00 00 06 0F 02 00 05 03 00 00 00", 0x0D);
            yield return V("extended claim read, LED 05, ID 1F", RazerDirectProtocol.ExtendedBrightnessRead(0x1F, led05),
                "00 00 1F 00 00 00 03 0F 84 01 05 00", 0x8C);
            yield return V("extended claim read, LED 04, ID 3F", RazerDirectProtocol.ExtendedBrightnessRead(0x3F, led04),
                "00 00 3F 00 00 00 03 0F 84 01 04 00", 0x8D);
            yield return V("region list read", RazerDirectProtocol.RegionListRead(0x1F),
                "00 00 1F 00 00 00 50 0F 80", 0xDF);
            yield return V("stored effect read, LED 05", RazerDirectProtocol.StoredEffectRead(0x1F, led05),
                "00 00 1F 00 00 00 0C 0F 82 01 05", 0x85);
            yield return V("one-row frame claim", RazerDirectProtocol.ExtendedFrameMode(0x1F),
                "00 00 1F 00 00 00 06 0F 02 00 00 08 00 00 00", 0x03);
            yield return V("one-row frame, three LEDs", RazerDirectProtocol.ExtendedFrameRow(0x1F, 3, Color),
                "00 00 1F 00 00 00 0E 0F 03 00 00 00 00 02 12 34 56 12 34 56 12 34 56", 0x70);
            yield return V("one-row frame, two LEDs", RazerDirectProtocol.ExtendedFrameRow(0x1F, 2, Color),
                "00 00 1F 00 00 00 0B 0F 03 00 00 00 00 01 12 34 56 12 34 56", 0x06);
            yield return V("standard frame row 0, 22 columns", RazerDirectProtocol.StandardRow(0xFF, 0, 22, Color),
                "00 00 FF 00 00 00 46 03 0B FF 00 00 15" + string.Concat(Enumerable.Repeat(" 12 34 56", 22)), 0xA4);
            yield return V("standard frame row 0, 17 columns", RazerDirectProtocol.StandardRow(0xFF, 0, 17, Color),
                "00 00 FF 00 00 00 37 03 0B FF 00 00 10" + string.Concat(Enumerable.Repeat(" 12 34 56", 17)), 0xA0);
            yield return V("standard frame row 5, 16 columns", RazerDirectProtocol.StandardRow(0xFF, 5, 16, Color),
                "00 00 FF 00 00 00 34 03 0B FF 05 00 0F" + string.Concat(Enumerable.Repeat(" 12 34 56", 16)), 0xC9);
            yield return V("a line of 15 LEDs, ID 80", RazerDirectProtocol.StandardLine(0x80, 15, Color),
                "00 00 80 00 00 00 32 03 0C 00 0E" + string.Concat(Enumerable.Repeat(" 12 34 56", 15)), 0x43);
            yield return V("standard show frame", RazerDirectProtocol.StandardShow(0xFF),
                "00 00 FF 00 00 00 02 03 0A 05 00", 0x0E);
            yield return V("standard static", RazerDirectProtocol.StandardStatic(0xFF, Color),
                "00 00 FF 00 00 00 04 03 0A 06 12 34 56", 0x7B);
            // The capture in the openrazer wiki, Reverse-Engineering-USB-Protocol.md:101-107.
            yield return V("the wiki's capture: standard static green", RazerDirectProtocol.StandardStatic(0xFF, 0x00FF00),
                "00 00 FF 00 00 00 04 03 0A 06 00 FF 00", 0xF4);
            yield return V("standard spectrum", RazerDirectProtocol.StandardSpectrum(0xFF),
                "00 00 FF 00 00 00 01 03 0A 04", 0x0C);
            yield return V("standard claim read", RazerDirectProtocol.StandardBrightnessRead(0xFF, led05),
                "00 00 FF 00 00 00 03 03 83 01 05 00", 0x87);
            yield return V("laptop claim read", RazerDirectProtocol.LaptopBrightnessRead(0xFF),
                "00 00 FF 00 00 00 02 0E 84 01 00", 0x89);
            yield return V("mouse extended static, LED 01", RazerDirectProtocol.MouseExtendedStatic(0x3F, led01, Color),
                "00 00 3F 00 00 00 06 03 0D 00 01 06 12 34 56", 0x7F);
            yield return V("mouse extended spectrum, LED 01", RazerDirectProtocol.MouseExtendedSpectrum(0x3F, led01),
                "00 00 3F 00 00 00 03 03 0D 00 01 04", 0x08);
            yield return V("classic LED on, LED 04", RazerDirectProtocol.ClassicState(0x3F, led04, on: true),
                "00 00 3F 00 00 00 03 03 00 00 04 01", 0x05);
            yield return V("classic color, LED 04", RazerDirectProtocol.ClassicColor(0x3F, led04, Color),
                "00 00 3F 00 00 00 05 03 01 00 04 12 34 56", 0x73);
            yield return V("classic effect static, LED 04",
                RazerDirectProtocol.ClassicEffect(0x3F, led04, RazerDirectProtocol.ClassicEffectStatic),
                "00 00 3F 00 00 00 03 03 02 00 04 00", 0x06);
            yield return V("classic read state, LED 04", RazerDirectProtocol.ClassicStateRead(0x3F, led04),
                "00 00 3F 00 00 00 03 03 80 01 04 00", 0x85);
            yield return V("classic read color, LED 04", RazerDirectProtocol.ClassicColorRead(0x3F, led04),
                "00 00 3F 00 00 00 05 03 81 01 04 00 00 00", 0x82);
            yield return V("classic read effect, LED 04", RazerDirectProtocol.ClassicEffectRead(0x3F, led04),
                "00 00 3F 00 00 00 03 03 82 01 04 00", 0x87);
            // A report Synapse 3 sent, from the razer-ctl notes
            // (data/README.md:11): a checksum from outside both projects.
            yield return V("a Synapse 3 capture, ID 1C", RazerDirectProtocol.Report(0x1C, 0x0D, 0x02, 4, Hex("01 01 00 00")),
                "00 00 1C 00 00 00 04 0D 02 01 01 00 00", 0x0B);
        }

        [Theory]
        [MemberData(nameof(Vectors))]
        public void EveryFamilysReport_IsTheReferencesBytes(string name, byte[] report, string opens, byte crc)
        {
            byte[] head = Hex(opens);
            Assert.Equal(RazerDirectProtocol.BufferLength, report.Length);
            Assert.True(report.AsSpan(0, head.Length).SequenceEqual(head),
                $"{name}: {BitConverter.ToString(report, 0, head.Length)} is not {opens}");
            for (int i = head.Length; i < RazerDirectProtocol.CrcAt; i++)
                Assert.True(report[i] == 0, $"{name}: byte {i} is {report[i]:X2}, past the report's last argument");
            Assert.Equal(crc, report[RazerDirectProtocol.CrcAt]);
            Assert.Equal(0, report[90]);
        }

        /// <summary>The checksum covers buffer bytes 3 to 88 and nothing
        /// else: the report ID, the status, the transaction ID, the
        /// checksum itself and the reserved byte leave it alone, and every
        /// byte inside the range moves it.</summary>
        [Fact]
        public void TheChecksum_IsTheXorOfBytes3To88()
        {
            var report = RazerDirectProtocol.ExtendedStatic(0x1F, 0x05, Color);
            byte crc = RazerDirectProtocol.Crc(report);
            Assert.Equal(report[RazerDirectProtocol.CrcAt], crc);
            foreach (int outside in new[] { 0, 1, 2, 89, 90 })
            {
                var changed = (byte[])report.Clone();
                changed[outside] ^= 0xA5;
                Assert.Equal(crc, RazerDirectProtocol.Crc(changed));
            }
            for (int inside = 3; inside <= 88; inside++)
            {
                var changed = (byte[])report.Clone();
                changed[inside] ^= 0xA5;
                Assert.Equal((byte)(crc ^ 0xA5), RazerDirectProtocol.Crc(changed));
            }

            // The same request under another ID keeps its checksum.
            var other = RazerDirectProtocol.WithTransactionId(report, 0x3F);
            Assert.Equal(0x3F, other[RazerDirectProtocol.TransactionAt]);
            Assert.Equal(0x1F, report[RazerDirectProtocol.TransactionAt]);
            Assert.Equal(crc, other[RazerDirectProtocol.CrcAt]);
            Assert.Equal(crc, RazerDirectProtocol.Crc(other));
        }

        [Fact]
        public void AReportTakesAtMostEightyArguments()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => RazerDirectProtocol.Report(0x1F, 0x03, 0x0B, 80, new byte[81]));
            Assert.Throws<ArgumentOutOfRangeException>(() => RazerDirectProtocol.Report(0x1F, 0x03, 0x0B, 81, new byte[4]));
            // The widest frame row a product has, 25 columns, fits.
            Assert.Equal(79, RazerDirectProtocol.StandardRow(0xFF, 0, 25, Color)[RazerDirectProtocol.SizeAt]);
            Assert.Throws<ArgumentOutOfRangeException>(() => RazerDirectProtocol.StandardRow(0xFF, 0, 26, Color));
        }

        // ─────────────────────────────────────────────
        //  Replies
        // ─────────────────────────────────────────────

        private static byte[] Reply(byte[] request, byte status, params byte[] arguments)
        {
            var reply = new byte[RazerDirectProtocol.BufferLength];
            reply[RazerDirectProtocol.StatusAt] = status;
            reply[RazerDirectProtocol.TransactionAt] = request[RazerDirectProtocol.TransactionAt];
            reply[RazerDirectProtocol.SizeAt] = request[RazerDirectProtocol.SizeAt];
            reply[RazerDirectProtocol.ClassAt] = request[RazerDirectProtocol.ClassAt];
            reply[RazerDirectProtocol.CommandAt] = request[RazerDirectProtocol.CommandAt];
            arguments.CopyTo(reply, RazerDirectProtocol.ArgumentsAt);
            return reply;
        }

        /// <summary>A reply is the request's when it echoes the transaction
        /// ID, the class and the command. 02 and 01 are accepted, 00 and a
        /// reply that is another request's are read again, 03, 04 and 05
        /// each have their own outcome, and the reply's checksum is never
        /// read.</summary>
        [Fact]
        public void AReplyIsReadByItsEchoAndItsStatus()
        {
            var request = RazerDirectProtocol.ExtendedStatic(0x1F, 0x05, Color);
            Assert.Equal(RazerReply.Accepted, RazerDirectProtocol.Check(request, Reply(request, 0x02)));
            Assert.Equal(RazerReply.Accepted, RazerDirectProtocol.Check(request, Reply(request, 0x01)));
            Assert.Equal(RazerReply.Early, RazerDirectProtocol.Check(request, Reply(request, 0x00)));
            Assert.Equal(RazerReply.Failure, RazerDirectProtocol.Check(request, Reply(request, 0x03)));
            Assert.Equal(RazerReply.Timeout, RazerDirectProtocol.Check(request, Reply(request, 0x04)));
            Assert.Equal(RazerReply.NotSupported, RazerDirectProtocol.Check(request, Reply(request, 0x05)));

            // The checksum of every reply above is 00, and none was refused for it.
            Assert.Equal(0, Reply(request, 0x02)[RazerDirectProtocol.CrcAt]);

            // Another ID, class or command: not this request's reply yet,
            // whatever its status says.
            foreach (int at in new[] { RazerDirectProtocol.TransactionAt, RazerDirectProtocol.ClassAt, RazerDirectProtocol.CommandAt })
                foreach (byte status in new byte[] { 0x02, 0x03, 0x04, 0x05 })
                {
                    var foreign = Reply(request, status);
                    foreign[at] ^= 0x40;
                    Assert.Equal(RazerReply.Early, RazerDirectProtocol.Check(request, foreign));
                }

            // A buffer that never was a reply.
            Assert.Equal(RazerReply.Early, RazerDirectProtocol.Check(request, new byte[RazerDirectProtocol.BufferLength]));
            Assert.Equal(RazerReply.Early, RazerDirectProtocol.Check(request, new byte[10]));
        }

        /// <summary>The stored effect read is believed only when its reply
        /// is a success for the stored profile and the LED asked for, names
        /// an effect of the family other than the custom frame, and carries
        /// at most two colors. Each condition refuses on its own, and a
        /// reply that passes is written back argument for argument with
        /// storage 00.</summary>
        [Fact]
        public void TheStoredEffectReply_IsBelievedOnlyWhenEveryConditionHolds()
        {
            const byte led = 0x05;
            var request = RazerDirectProtocol.StoredEffectRead(0x1F, led);
            // Breathing between two colors: effect 02, flags 02, rate 00.
            byte[] stored = { 0x01, led, 0x02, 0x02, 0x00, 0x02, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0x00 };

            Assert.True(RazerDirectProtocol.TryStoredEffect(0x1F, led, Reply(request, 0x02, stored), out var back));
            Assert.Equal("1F 0F/02 size 0C [00 05 02 02 00 02 00 FF 00 FF 00 00]", Line(back));

            // A static effect with one color, and spectrum with none.
            Assert.True(RazerDirectProtocol.TryStoredEffect(0x3F, led,
                Reply(request, 0x02, 0x01, led, 0x01, 0x00, 0x00, 0x01, 0xAA, 0xBB, 0xCC), out back));
            Assert.Equal("3F 0F/02 size 09 [00 05 01 00 00 01 AA BB CC]", Line(back));
            Assert.True(RazerDirectProtocol.TryStoredEffect(0x1F, led, Reply(request, 0x02, 0x01, led, 0x03), out back));
            Assert.Equal(Line(RazerDirectProtocol.ExtendedSpectrum(0x1F, led)), Line(back));
            Assert.True(back.AsSpan().SequenceEqual(RazerDirectProtocol.ExtendedSpectrum(0x1F, led)));

            byte[] With(int index, byte value)
            {
                var copy = (byte[])stored.Clone();
                copy[index] = value;
                return copy;
            }
            // Status 01 is accepted for a write and is no answer to a read.
            Assert.False(RazerDirectProtocol.TryStoredEffect(0x1F, led, Reply(request, 0x01, stored), out back));
            Assert.Null(back);
            Assert.False(RazerDirectProtocol.TryStoredEffect(0x1F, led, Reply(request, 0x03, stored), out _));
            Assert.False(RazerDirectProtocol.TryStoredEffect(0x1F, led, Reply(request, 0x02, With(0, 0x00)), out _));
            Assert.False(RazerDirectProtocol.TryStoredEffect(0x1F, led, Reply(request, 0x02, With(1, 0x04)), out _));
            Assert.False(RazerDirectProtocol.TryStoredEffect(0x1F, led, Reply(request, 0x02, With(2, 0x08)), out _));
            Assert.False(RazerDirectProtocol.TryStoredEffect(0x1F, led, Reply(request, 0x02, With(2, 0x0B)), out _));
            Assert.True(RazerDirectProtocol.TryStoredEffect(0x1F, led, Reply(request, 0x02, With(2, 0x0A)), out _));
            Assert.False(RazerDirectProtocol.TryStoredEffect(0x1F, led, Reply(request, 0x02, With(5, 0x03)), out _));
            Assert.False(RazerDirectProtocol.TryStoredEffect(0x1F, led, new byte[20], out _));
        }

        /// <summary>The region list is five-byte records that open with an
        /// LED ID and end at an ID of 00. A lit region the device names and
        /// the row does not paint gets a report of its own, a row that
        /// paints LED 00 gains nothing, and a list that is empty, refused
        /// or without its end changes nothing.</summary>
        [Fact]
        public void TheRegionList_AddsTheLitRegionsARowDoesNotPaint()
        {
            var request = RazerDirectProtocol.RegionListRead(0x1F);
            byte[] keyboard = Hex("05 19 03 06 12");
            byte[] mouse = Hex("01 19 03 01 01 04 19 03 01 01 0A 19 03 01 0B");

            Assert.Equal(new byte[] { 0x05 }, RazerDirectProtocol.Regions(Reply(request, 0x02, keyboard)));
            Assert.Equal(new byte[] { 0x01, 0x04, 0x0A }, RazerDirectProtocol.Regions(Reply(request, 0x02, mouse)));

            // A row that paints logo and scroll wheel gains the underglow.
            var deathAdder = Row(0x0084);
            Assert.Equal(new byte[] { 0x04, 0x01 }, deathAdder.Leds);
            byte[] leds = RazerDirectProtocol.PaintLeds(deathAdder, RazerDirectProtocol.Regions(Reply(request, 0x02, mouse)));
            Assert.Equal(new byte[] { 0x04, 0x01, 0x0A }, leds);
            Assert.Equal(new[]
            {
                "3F 0F/02 size 09 [00 04 01 00 00 01 12 34 56]",
                "3F 0F/02 size 09 [00 01 01 00 00 01 12 34 56]",
                "3F 0F/02 size 09 [00 0A 01 00 00 01 12 34 56]",
            }, Lines(RazerDirectProtocol.Paint(deathAdder, 0x3F, leds, Color)));

            // LED 00 is the whole device, so nothing is added to it.
            var basilisk = Row(0x0099);
            Assert.Same(basilisk.Leds, RazerDirectProtocol.PaintLeds(basilisk, RazerDirectProtocol.Regions(Reply(request, 0x02, mouse))));
            // A row's LED that the list lacks is still painted.
            Assert.Equal(new byte[] { 0x04, 0x01, 0x05 },
                RazerDirectProtocol.PaintLeds(deathAdder, RazerDirectProtocol.Regions(Reply(request, 0x02, keyboard))));

            // Nothing changes for an empty list, a reply that is no
            // success, a list that fills the report with no end, or a
            // region that is no zone to color (03 is the battery LED).
            Assert.Empty(RazerDirectProtocol.Regions(Reply(request, 0x02)));
            Assert.Empty(RazerDirectProtocol.Regions(Reply(request, 0x01, mouse)));
            Assert.Empty(RazerDirectProtocol.Regions(Reply(request, 0x05, mouse)));
            byte[] endless = Enumerable.Repeat(Hex("04 19 03 01 01"), 16).SelectMany(r => r).ToArray();
            Assert.Equal(80, endless.Length);
            Assert.Empty(RazerDirectProtocol.Regions(Reply(request, 0x02, endless)));
            Assert.Same(deathAdder.Leds, RazerDirectProtocol.PaintLeds(deathAdder, Array.Empty<byte>()));
            Assert.Same(deathAdder.Leds, RazerDirectProtocol.PaintLeds(deathAdder, new byte[] { 0x03, 0x04, 0x01 }));
            // Fifteen records and the closing zero are a list.
            byte[] fifteen = endless.Take(75).ToArray();
            Assert.Equal(15, RazerDirectProtocol.Regions(Reply(request, 0x02, fifteen)).Length);

            // Only the extended matrix family reads the list.
            var naga = Row(0x0050);
            Assert.Same(naga.Leds, RazerDirectProtocol.PaintLeds(naga, new byte[] { 0x0A }));
        }

        /// <summary>A classic LED's own state, color and effect are read
        /// before the claim and written back at release, color first, then
        /// effect, then state, with storage 00.</summary>
        [Fact]
        public void AClassicLedsOwnValues_AreWrittenBackAsTheyWereRead()
        {
            const byte led = 0x04;
            var state = Reply(RazerDirectProtocol.ClassicStateRead(0x3F, led), 0x02, 0x01, led, 0x01);
            var color = Reply(RazerDirectProtocol.ClassicColorRead(0x3F, led), 0x02, 0x01, led, 0xAA, 0xBB, 0xCC);
            var effect = Reply(RazerDirectProtocol.ClassicEffectRead(0x3F, led), 0x02, 0x01, led, 0x02);

            Assert.True(RazerDirectProtocol.TryClassicStored(0x3F, led, state, color, effect, true, out var back));
            Assert.Equal(new[]
            {
                "3F 03/01 size 05 [00 04 AA BB CC]",
                "3F 03/02 size 03 [00 04 02]",
                "3F 03/00 size 03 [00 04 01]",
            }, Lines(back));

            // An LED that was off goes back off.
            var off = Reply(RazerDirectProtocol.ClassicStateRead(0x3F, led), 0x02, 0x01, led, 0x00);
            Assert.True(RazerDirectProtocol.TryClassicStored(0x3F, led, off, color, effect, true, out back));
            Assert.Equal("3F 03/00 size 03 [00 04 00]", Line(back[2]));

            // A read that failed, a reply for another LED, and a value the
            // writes do not take each leave the fallback.
            var refused = Reply(RazerDirectProtocol.ClassicColorRead(0x3F, led), 0x03, 0x01, led, 0xAA, 0xBB, 0xCC);
            Assert.False(RazerDirectProtocol.TryClassicStored(0x3F, led, state, refused, effect, true, out back));
            Assert.Null(back);
            var otherLed = Reply(RazerDirectProtocol.ClassicEffectRead(0x3F, led), 0x02, 0x01, 0x01, 0x02);
            Assert.False(RazerDirectProtocol.TryClassicStored(0x3F, led, state, color, otherLed, true, out _));
            var wild = Reply(RazerDirectProtocol.ClassicEffectRead(0x3F, led), 0x02, 0x01, led, 0x06);
            Assert.False(RazerDirectProtocol.TryClassicStored(0x3F, led, state, color, wild, true, out _));
            var halfOn = Reply(RazerDirectProtocol.ClassicStateRead(0x3F, led), 0x02, 0x01, led, 0x02);
            Assert.False(RazerDirectProtocol.TryClassicStored(0x3F, led, halfOn, color, effect, true, out _));

            // A row whose claim sets no state gets none written back, and
            // its state reply decides nothing: with none at all, with one
            // that says off, and with one the writes would not take, the
            // color and the effect go back alone.
            Assert.True(RazerDirectProtocol.TryClassicStored(0x3F, led, default, color, effect, false, out back));
            Assert.Equal(new[] { "3F 03/01 size 05 [00 04 AA BB CC]", "3F 03/02 size 03 [00 04 02]" }, Lines(back));
            Assert.True(RazerDirectProtocol.TryClassicStored(0x3F, led, off, color, effect, false, out back));
            Assert.Equal(2, back.Length);
            Assert.True(RazerDirectProtocol.TryClassicStored(0x3F, led, halfOn, color, effect, false, out back));
            Assert.Equal(2, back.Length);
            // Its color and its effect are held to the same checks.
            Assert.False(RazerDirectProtocol.TryClassicStored(0x3F, led, state, refused, effect, false, out _));
            Assert.False(RazerDirectProtocol.TryClassicStored(0x3F, led, state, color, wild, false, out _));
        }

        /// <summary>Nine mice and one keyboard are read under FF, the ID
        /// openrazer sends their brightness read under, and painted under
        /// 3F, the ID it paints them under, and so are seven accessories,
        /// painted under 3F or 1F (openrazer razeraccessory_driver.c:1625-1642
        /// and 665-702). Their claim read is asked under FF first and under
        /// their own IDs after it. Every other row is asked under the IDs it
        /// is painted under and no other.</summary>
        [Fact]
        public void SeventeenProducts_AreReadUnderOpenrazersOwnIdForTheRead_First()
        {
            var own = RazerDirectTable.Rows.Where(r => r.ProbeTransactionId != 0).ToArray();
            Assert.Equal(new ushort[] { 0x001F, 0x0024, 0x0025, 0x003E, 0x003F, 0x0043, 0x0053, 0x0054, 0x005B, 0x0221,
                    0x0517, 0x0518, 0x0F08, 0x0F09, 0x0F0D, 0x0F13, 0x0F1A },
                own.Select(r => r.ProductId));
            Assert.All(own, row =>
            {
                Assert.Equal(0xFF, row.ProbeTransactionId);
                Assert.Contains(row.TransactionId, new byte[] { 0x3F, 0x1F });
                Assert.DoesNotContain(row.ProbeTransactionId, RazerDirectProtocol.TransactionIds(row));
                Assert.Equal(new byte[] { 0xFF }.Concat(RazerDirectProtocol.TransactionIds(row)), RazerDirectProtocol.ProbeIds(row));
                Assert.Equal(RazerProbe.StandardBrightness, row.Probe);
            });
            Assert.Equal(new byte[] { 0xFF, 0x3F, 0x1F }, RazerDirectProtocol.ProbeIds(Row(0x0043)));
            Assert.All(RazerDirectTable.Rows.Where(r => r.HasColorPath && r.ProbeTransactionId == 0),
                row => Assert.Equal(RazerDirectProtocol.TransactionIds(row), RazerDirectProtocol.ProbeIds(row)));
            // An ID a row lists for its paint is never asked twice.
            var listed = new RazerDirectRow { TransactionId = 0x3F, OtherTransactionIds = new byte[] { 0xFF }, ProbeTransactionId = 0xFF };
            Assert.Equal(new byte[] { 0x3F, 0xFF }, RazerDirectProtocol.ProbeIds(listed));
        }

        /// <summary>A row is named for its product. Where the daemon's text
        /// for a product is a class's docstring or lacks the maker, the
        /// name is OpenRGB's.</summary>
        [Fact]
        public void EveryRowIsNamedForItsProduct()
        {
            Assert.Equal("Razer BlackWidow Chroma V2", Row(0x0221).Name);
            Assert.Equal("Razer Tartarus V2", Row(0x022B).Name);
            Assert.Equal("Razer Tartarus Pro", Row(0x0244).Name);
            Assert.All(RazerDirectTable.Rows, row => Assert.DoesNotContain("Class for", row.Name));
        }

        // ─────────────────────────────────────────────
        //  The product table
        // ─────────────────────────────────────────────

        /// <summary>The table has one row for each of the 273 product IDs
        /// PadForge lists, 221 of them with a color path: the research's
        /// 98, 62, 11, 7 and 2, and #557's 41, the two Basilisk V4 Pro rows,
        /// the Mouse Dock Pro on the extended matrix frame, and 38 mice,
        /// keyboards and accessories OpenRGB lights, 35 on the extended
        /// matrix and 3 on the standard matrix.</summary>
        [Fact]
        public void TheTableHasARowForEveryListedProduct_AndTwoHundredTwentyOneColorPaths()
        {
            var rows = RazerDirectTable.Rows;
            Assert.Equal(273, rows.Length);
            Assert.Equal(273, rows.Select(r => r.ProductId).Distinct().Count());
            Assert.Equal(221, rows.Count(r => r.HasColorPath));
            int Of(RazerFamily family) => rows.Count(r => r.Family == family);
            Assert.Equal((135, 65, 11, 7, 2, 1),
                (Of(RazerFamily.Extended), Of(RazerFamily.Standard), Of(RazerFamily.Classic),
                 Of(RazerFamily.ExtendedFrame), Of(RazerFamily.MouseExtended), Of(RazerFamily.ExtendedMatrix)));
            Assert.Equal((21, 29, 2), (Of(RazerFamily.Mono), Of(RazerFamily.None), Of(RazerFamily.Legacy)));

            // The rows are the lists the linker tells a mouse from a
            // keyboard by and the accessories, no more and no fewer, each
            // product on one list.
            var listed = PeripheralProductIds.RazerMice.Concat(PeripheralProductIds.RazerKeyboards)
                .Concat(PeripheralProductIds.RazerAccessories).OrderBy(p => p).ToArray();
            Assert.Equal(listed.Length, listed.Distinct().Count());
            Assert.Equal(listed, rows.Select(r => r.ProductId).OrderBy(p => p).ToArray());
            Assert.Equal(77, rows.Count(r => r.HasColorPath && PeripheralProductIds.RazerMice.Contains(r.ProductId)));
            Assert.Equal(115, rows.Count(r => r.HasColorPath && PeripheralProductIds.RazerKeyboards.Contains(r.ProductId)));
            Assert.Equal(29, rows.Count(r => r.HasColorPath && PeripheralProductIds.RazerAccessories.Contains(r.ProductId)));

            Assert.Null(RazerDirectTable.Find(0xFFFF));
            Assert.All(rows, row => Assert.Same(row, RazerDirectTable.Find(row.ProductId)));

            // The reply waits are openrazer's four classes for a product
            // that takes a color, and 47 laptops are kept awake.
            Assert.Equal(new[] { (600, 178), (4900, 8), (31000, 31), (59900, 4) },
                rows.Where(r => r.HasColorPath).GroupBy(r => r.ReplyWaitUs).OrderBy(g => g.Key).Select(g => (g.Key, g.Count())));
            Assert.Equal(35, rows.Count(r => r.HasColorPath && r.SlowReply));
            Assert.Equal(47, rows.Count(r => r.Has(RazerRowFlags.Laptop)));

            // The first transaction ID of the 221: 1F, 3F, FF and 9F.
            int First(byte id) => rows.Count(r => r.HasColorPath && r.TransactionId == id);
            Assert.Equal((74, 74, 66, 7), (First(0x1F), First(0x3F), First(0xFF), First(0x9F)));
            Assert.Equal(80, rows.Count(r => r.HasColorPath && r.OtherTransactionIds.Length > 0));
        }

        /// <summary>A row with a color path has everything a claim needs: an
        /// ID that is not among its own alternates, a claim read, a
        /// collection to open, and LEDs or a frame by its family. A row
        /// without one is never asked for a report.</summary>
        [Fact]
        public void EveryColorRowIsComplete_AndEveryOtherRowBuildsNothing()
        {
            foreach (var row in RazerDirectTable.Rows)
            {
                string id = row.ProductId.ToString("X4");
                Assert.False(string.IsNullOrWhiteSpace(row.Name), id);
                Assert.NotEmpty(row.Collections);
                if (!row.HasColorPath)
                {
                    Assert.True(row.Family is RazerFamily.None or RazerFamily.Legacy or RazerFamily.Mono, id);
                    Assert.Equal(RazerProbe.None, row.Probe);
                    Assert.Null(RazerDirectProtocol.ProbeRead(row, 0x1F));
                    Assert.Empty(RazerDirectProtocol.Paint(row, 0x1F, row.Leds, Color));
                    Assert.Empty(RazerDirectProtocol.Claim(row, 0x1F));
                    Assert.Empty(RazerDirectProtocol.SpectrumBack(row, 0x1F));
                    Assert.Empty(row.Fallbacks);
                    continue;
                }
                Assert.NotEqual(0, row.TransactionId);
                byte[] ids = RazerDirectProtocol.TransactionIds(row);
                Assert.Equal(row.TransactionId, ids[0]);
                Assert.Equal(ids.Length, ids.Distinct().Count());
                Assert.DoesNotContain((byte)0, ids);
                Assert.NotNull(RazerDirectProtocol.ProbeRead(row, row.TransactionId));
                Assert.NotEmpty(RazerDirectProtocol.Paint(row, row.TransactionId, row.Leds, Color));
                Assert.True(row.ReplyWaitUs > 0, id);
                bool byLed = row.Family is RazerFamily.Extended or RazerFamily.MouseExtended or RazerFamily.Classic;
                Assert.Equal(byLed || row.Family is RazerFamily.ExtendedFrame or RazerFamily.ExtendedMatrix, row.Leds.Length > 0);
                if (row.Family == RazerFamily.ExtendedMatrix) Assert.True(row.Rows > 0 && row.Columns > 0, id);
                if (row.Family == RazerFamily.Standard)
                    Assert.Equal(row.Has(RazerRowFlags.HeldEffect), row.Rows == 0 && row.Columns == 0);
                if (row.Family == RazerFamily.ExtendedFrame) Assert.InRange(row.Columns, 2, 3);
            }
        }

        /// <summary>The rows the research names, each by the reports it
        /// builds.</summary>
        [Fact]
        public void TheRowsTheResearchNames_BuildTheReportsItSpells()
        {
            // 0084, a DeathAdder V2: logo and scroll wheel by their own
            // reports, ID 3F first and 1F after it.
            var row = Row(0x0084);
            Assert.Equal(new byte[] { 0x3F, 0x1F }, RazerDirectProtocol.TransactionIds(row));
            Assert.Equal(new[]
            {
                "3F 0F/02 size 09 [00 04 01 00 00 01 12 34 56]",
                "3F 0F/02 size 09 [00 01 01 00 00 01 12 34 56]",
            }, Lines(RazerDirectProtocol.Paint(row, 0x3F, row.Leds, Color)));
            Assert.Equal("3F 0F/84 size 03 [01 04 00]", Line(RazerDirectProtocol.ProbeRead(row, 0x3F)));
            Assert.Empty(RazerDirectProtocol.Claim(row, 0x3F));
            Assert.Equal(new[] { "3F 0F/02 size 06 [00 04 03 00 00 00]", "3F 0F/02 size 06 [00 01 03 00 00 00]" },
                Lines(RazerDirectProtocol.SpectrumBack(row, 0x3F)));
            Assert.Equal(new[]
            {
                "3F 0F/82 size 0C [01 04 00 00 00 00 00 00 00 00 00 00]",
                "3F 0F/82 size 0C [01 01 00 00 00 00 00 00 00 00 00 00]",
            }, Lines(RazerDirectProtocol.StoredReads(row, 0x3F, row.Leds)));
            // Its LED 00 form is another form of the same family, which
            // nobody has seen light every zone: it is no fallback.
            Assert.Empty(row.Fallbacks);
            Assert.Equal(new RazerCollection(0, 0x01, 0x02), Assert.Single(row.Collections));

            // 0203, a BlackWidow Chroma: six rows of 22 and the show report.
            row = Row(0x0203);
            var frame = Lines(RazerDirectProtocol.Paint(row, 0xFF, row.Leds, Color));
            Assert.Equal(7, frame.Length);
            for (int index = 0; index < 6; index++)
                Assert.Equal($"FF 03/0B size 46 [FF {index:X2} 00 15{string.Concat(Enumerable.Repeat(" 12 34 56", 22))}]", frame[index]);
            Assert.Equal("FF 03/0A size 02 [05 00]", frame[6]);
            Assert.Equal("FF 03/83 size 03 [01 05 00]", Line(RazerDirectProtocol.ProbeRead(row, 0xFF)));
            Assert.Empty(RazerDirectProtocol.SpectrumBack(row, 0xFF));
            Assert.Empty(RazerDirectProtocol.StoredReads(row, 0xFF, row.Leds));

            // 0099, a Basilisk V3: one report, LED 00, ID 1F, on interface 3.
            row = Row(0x0099);
            Assert.Equal("1F 0F/02 size 09 [00 00 01 00 00 01 12 34 56]",
                Assert.Single(Lines(RazerDirectProtocol.Paint(row, 0x1F, row.Leds, Color))));
            Assert.Equal(new byte[] { 0x1F }, RazerDirectProtocol.TransactionIds(row));
            Assert.Equal(new RazerCollection(3, 0x0C, 0x01), Assert.Single(row.Collections));

            // 0043, a DeathAdder Chroma: the claim turns LEDs 01 and 04 on,
            // and each update is a color and an effect per LED.
            row = Row(0x0043);
            Assert.Equal(new[] { "3F 03/00 size 03 [00 01 01]", "3F 03/00 size 03 [00 04 01]" },
                Lines(RazerDirectProtocol.Claim(row, 0x3F)));
            Assert.Equal(new[]
            {
                "3F 03/01 size 05 [00 01 12 34 56]", "3F 03/02 size 03 [00 01 00]",
                "3F 03/01 size 05 [00 04 12 34 56]", "3F 03/02 size 03 [00 04 00]",
            }, Lines(RazerDirectProtocol.Paint(row, 0x3F, row.Leds, Color)));
            Assert.Equal(new[] { "3F 03/02 size 03 [00 01 04]", "3F 03/02 size 03 [00 04 04]" },
                Lines(RazerDirectProtocol.SpectrumBack(row, 0x3F)));
            Assert.Equal(new[]
            {
                "3F 03/80 size 03 [01 01 00]", "3F 03/81 size 05 [01 01 00 00 00]", "3F 03/82 size 03 [01 01 00]",
                "3F 03/80 size 03 [01 04 00]", "3F 03/81 size 05 [01 04 00 00 00]", "3F 03/82 size 03 [01 04 00]",
            }, Lines(RazerDirectProtocol.StoredReads(row, 0x3F, row.Leds)));

            // 010F, an Anansi: the effect report goes out ahead of the color.
            row = Row(0x010F);
            Assert.Equal(new[] { "FF 03/02 size 03 [00 05 00]", "FF 03/01 size 05 [00 05 12 34 56]" },
                Lines(RazerDirectProtocol.Paint(row, 0xFF, row.Leds, Color)));

            // 0239, a Blade Stealth 2019: six rows of 16 from OpenRGB's
            // table, its effect paint as the fallback, and a laptop's read.
            row = Row(0x0239);
            frame = Lines(RazerDirectProtocol.Paint(row, 0xFF, row.Leds, Color));
            Assert.Equal(7, frame.Length);
            Assert.Equal($"FF 03/0B size 34 [FF 05 00 0F{string.Concat(Enumerable.Repeat(" 12 34 56", 16))}]", frame[5]);
            var fallback = Assert.Single(row.Fallbacks);
            Assert.Equal(RazerFallbackKind.Effect, fallback.Kind);
            Assert.Equal("FF 03/0A size 04 [06 12 34 56]", Assert.Single(Lines(RazerDirectProtocol.Paint(fallback, 0xFF, Color))));
            Assert.Equal("FF 0E/84 size 02 [01 00]", Line(RazerDirectProtocol.ProbeRead(row, 0xFF)));
            Assert.True(row.Has(RazerRowFlags.Laptop));

            // 0208, a Tartarus Chroma: OpenRGB's LED reports, no state
            // report at claim, and the effect paint if they are refused.
            row = Row(0x0208);
            Assert.Equal(new[] { "FF 03/01 size 05 [00 05 12 34 56]", "FF 03/02 size 03 [00 05 00]" },
                Lines(RazerDirectProtocol.Paint(row, 0xFF, row.Leds, Color)));
            Assert.Empty(RazerDirectProtocol.Claim(row, 0xFF));
            Assert.Equal(RazerFallbackKind.Effect, Assert.Single(row.Fallbacks).Kind);

            // 02C6, a Blade 16 2025: six rows of 17, kept awake.
            row = Row(0x02C6);
            Assert.True(row.Has(RazerRowFlags.Laptop));
            Assert.Equal((6, 17), (row.Rows, row.Columns));

            // The one-row frames: three LEDs on a Naga, two on a Basilisk
            // Mobile, each behind the mode report. A Naga Pro's spectrum is
            // per zone, since openrazer marks its whole-device one doubtful.
            row = Row(0x008F);
            Assert.Equal("1F 0F/02 size 06 [00 00 08 00 00 00]", Assert.Single(Lines(RazerDirectProtocol.Claim(row, 0x1F))));
            Assert.Equal("1F 0F/03 size 0E [00 00 00 00 02 12 34 56 12 34 56 12 34 56]",
                Assert.Single(Lines(RazerDirectProtocol.Paint(row, 0x1F, row.Leds, Color))));
            Assert.Equal(new[] { "1F 0F/02 size 06 [00 04 03 00 00 00]", "1F 0F/02 size 06 [00 01 03 00 00 00]" },
                Lines(RazerDirectProtocol.SpectrumBack(row, 0x1F)));
            Assert.Equal("1F 0F/82 size 0C [01 00 00 00 00 00 00 00 00 00 00 00]",
                Assert.Single(Lines(RazerDirectProtocol.StoredReads(row, 0x1F, row.Leds))));
            row = Row(0x00D3);
            Assert.Equal("1F 0F/03 size 0B [00 00 00 00 01 12 34 56 12 34 56]",
                Assert.Single(Lines(RazerDirectProtocol.Paint(row, 0x1F, row.Leds, Color))));
            Assert.Empty(RazerDirectProtocol.SpectrumBack(row, 0x1F));

            // The mouse extended matrix, with OpenRGB's frame and then its
            // effect paint behind it.
            row = Row(0x0053);
            Assert.Equal(new[]
            {
                "3F 03/0D size 06 [00 05 06 12 34 56]", "3F 03/0D size 06 [00 04 06 12 34 56]",
                "3F 03/0D size 06 [00 01 06 12 34 56]",
            }, Lines(RazerDirectProtocol.Paint(row, 0x3F, row.Leds, Color)));
            Assert.Equal(new[] { "3F 03/0D size 03 [00 05 04]", "3F 03/0D size 03 [00 04 04]", "3F 03/0D size 03 [00 01 04]" },
                Lines(RazerDirectProtocol.SpectrumBack(row, 0x3F)));
            Assert.Equal(new[] { RazerFallbackKind.LineRow, RazerFallbackKind.Effect }, row.Fallbacks.Select(f => f.Kind));
            Assert.Equal(new[] { "3F 03/0C size 32 [00 02 12 34 56 12 34 56 12 34 56 " + string.Join(" ", Enumerable.Repeat("00", 39)) + "]",
                "3F 03/0A size 02 [05 00]" }, Lines(RazerDirectProtocol.Paint(row.Fallbacks[0], 0x3F, Color)));
            Assert.Equal(new[] { "3F 03/0B size 0D [FF 00 00 02 12 34 56 12 34 56 12 34 56]", "3F 03/0A size 02 [05 00]" },
                Lines(RazerDirectProtocol.Paint(Row(0x0050).Fallbacks[0], 0x3F, Color)));

            // The two products with only the effect report: held, and
            // spectrum at hand-back on the one that has it.
            row = Row(0x0048);
            Assert.True(row.Has(RazerRowFlags.HeldEffect));
            Assert.Equal("FF 03/0A size 04 [06 12 34 56]", Assert.Single(Lines(RazerDirectProtocol.Paint(row, 0xFF, row.Leds, Color))));
            Assert.Equal("FF 03/0A size 01 [04]", Assert.Single(Lines(RazerDirectProtocol.SpectrumBack(row, 0xFF))));
            row = Row(0x0201);
            Assert.True(row.Has(RazerRowFlags.HeldEffect));
            Assert.Empty(RazerDirectProtocol.SpectrumBack(row, 0xFF));

            // A BlackWidow V4 moved its collection in a firmware update, so
            // its row names both.
            Assert.Equal(new[] { new RazerCollection(3, 0x01, 0x00), new RazerCollection(3, 0x0C, 0x01) }, Row(0x0287).Collections);
        }

        /// <summary>A frame's rows and its show report keep the IDs
        /// openrazer gives them while the product answered the row's first
        /// ID. A product that answered another of its IDs gets that one on
        /// every report.</summary>
        [Fact]
        public void AFramesReports_GoOutUnderOpenrazersIds_WhileTheFirstIdAnswered()
        {
            (int Rows, int Show) Ids(ushort productId, byte settled)
            {
                var row = Row(productId);
                var frame = RazerDirectProtocol.Paint(row, settled, row.Leds, Color);
                Assert.Single(frame.Take(frame.Count - 1).Select(r => r[RazerDirectProtocol.TransactionAt]).Distinct());
                return (frame[0][RazerDirectProtocol.TransactionAt], frame[^1][RazerDirectProtocol.TransactionAt]);
            }
            // Mamba Chroma, wired: rows 80, show FF. Wireless: both 80.
            Assert.Equal((0x80, 0xFF), Ids(0x0044, 0xFF));
            Assert.Equal((0x80, 0x80), Ids(0x0045, 0xFF));
            // Blade Late 2016: rows 3F, show FF.
            Assert.Equal((0x3F, 0xFF), Ids(0x0224, 0xFF));
            // Every other frame goes out under the one ID.
            Assert.Equal((0xFF, 0xFF), Ids(0x0203, 0xFF));
            Assert.Equal((0x3F, 0x3F), Ids(0x0207, 0x3F));

            // The product answered OpenRGB's ID and not openrazer's.
            Assert.Equal((0x3F, 0x3F), Ids(0x0044, 0x3F));
            Assert.Equal((0x3F, 0x3F), Ids(0x0224, 0x3F));
            Assert.Equal((0x80, 0x80), Ids(0x0044, 0x80));
            Assert.Equal((0x3F, 0x3F), Ids(0x0203, 0x3F));

            // Only three rows carry frame IDs of their own.
            Assert.Equal(new ushort[] { 0x0044, 0x0045, 0x0224 },
                RazerDirectTable.Rows.Where(r => r.RowTransactionId != 0 || r.ShowTransactionId != 0).Select(r => r.ProductId));
        }

        /// <summary>The reports of every row, as the digest reads them.</summary>
        private static string Listing(RazerDirectRow row)
        {
            byte id = row.TransactionId;
            var text = new StringBuilder();
            text.Append(row.ProductId.ToString("X4")).Append(" tx ").Append(id.ToString("X2"));
            foreach (byte other in row.OtherTransactionIds) text.Append(' ').Append(other.ToString("X2"));
            text.Append('\n');
            void Add(string what, IEnumerable<byte[]> reports)
            {
                foreach (var report in reports) text.Append("  ").Append(what).Append(' ').Append(Line(report)).Append('\n');
            }
            Add("probe", new[] { RazerDirectProtocol.ProbeRead(row, RazerDirectProtocol.ProbeIds(row)[0]) });
            Add("read", RazerDirectProtocol.StoredReads(row, id, row.Leds));
            Add("claim", RazerDirectProtocol.Claim(row, id));
            Add("paint", RazerDirectProtocol.Paint(row, id, row.Leds, Color));
            Add("back", RazerDirectProtocol.SpectrumBack(row, id));
            for (int i = 0; i < row.Fallbacks.Length; i++)
                Add("fallback " + (i + 1), RazerDirectProtocol.Paint(row.Fallbacks[i], id, Color));
            return text.ToString();
        }

        /// <summary>
        /// Every row builds, through the protocol class, exactly the reports
        /// the research table spells for its product: the claim read, the
        /// reads before a claim, the claim, the paint, the hand-back and
        /// each fallback. tools/gen_razer_direct_table.py prints one SHA-256
        /// per family over those reports as the research table has them,
        /// and these are its six. A digest that differs names its family.
        /// Run the tool with --frames to list the reports it hashed.
        /// </summary>
        [Theory]
        [InlineData("Classic", 11, "7fdb3060a5f4bebf958bce1e2ff2140093abccf33c988da1965fc404c073bffb")]
        [InlineData("Extended", 135, "a0f443a19170d12f6f03a43daacfb59dd9877e75bdf2f30e3767a4cc4d0c566b")]
        [InlineData("ExtendedFrame", 7, "c92b94a865eadbef1b9b0cb3e138470b25ce3f1beb8e7cf23e28ba79f036250d")]
        [InlineData("Standard", 65, "b09ae24f168374833f62f1cf3c797d5de7ae496749f5c637c672ae2dcaac8d93")]
        [InlineData("MouseExtended", 2, "486f3bb0a135a3b124e890b7a9ad1220a21117632d6ea44410e8b451226fa231")]
        [InlineData("ExtendedMatrix", 1, "679714ecbac9fa38d5206c3997048e70594f11143a441a044318e6119c1734d8")]
        public void EveryRowOfAFamily_BuildsTheReportsTheResearchTableSpells(string name, int count, string digest)
        {
            var family = Enum.Parse<RazerFamily>(name);
            var rows = RazerDirectTable.Rows.Where(r => r.Family == family).OrderBy(r => r.ProductId).ToArray();
            Assert.Equal(count, rows.Length);
            string text = string.Concat(rows.Select(Listing));
            string actual = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(text))).ToLowerInvariant();
            Assert.True(digest == actual,
                $"the reports of the {family} rows differ from the research table's: {actual}, expected {digest}");
        }

        /// <summary>Every report any row builds is 91 bytes with a true
        /// checksum, carries storage 00 where its command has a storage
        /// byte, and is never a device-mode report. A read carries 01, but
        /// for the two reads with no argument, the region list and a
        /// receiver's paired devices. The one class 00 report is that last
        /// read.</summary>
        [Fact]
        public void EveryReportARowBuilds_StoresNothing_AndNoneSetsTheDeviceMode()
        {
            int checkedReports = 0;
            foreach (var row in RazerDirectTable.Rows.Where(r => r.HasColorPath))
            {
                var reports = new List<byte[]>();
                foreach (byte id in RazerDirectProtocol.TransactionIds(row))
                {
                    reports.Add(RazerDirectProtocol.ProbeRead(row, id));
                    reports.AddRange(RazerDirectProtocol.StoredReads(row, id, row.Leds));
                    reports.AddRange(RazerDirectProtocol.Claim(row, id));
                    reports.AddRange(RazerDirectProtocol.Paint(row, id, row.Leds, Color));
                    reports.AddRange(RazerDirectProtocol.SpectrumBack(row, id));
                    foreach (var fallback in row.Fallbacks) reports.AddRange(RazerDirectProtocol.Paint(fallback, id, Color));
                }
                if (row.ProbeTransactionId != 0) reports.Add(RazerDirectProtocol.ProbeRead(row, row.ProbeTransactionId));
                foreach (var report in reports)
                {
                    string line = row.ProductId.ToString("X4") + " " + Line(report);
                    Assert.Equal(RazerDirectProtocol.BufferLength, report.Length);
                    Assert.Equal(RazerDirectProtocol.Crc(report), report[RazerDirectProtocol.CrcAt]);
                    Assert.Equal(0, report[0]);
                    Assert.Equal(0, report[RazerDirectProtocol.StatusAt]);
                    Assert.Equal(0, report[90]);
                    Assert.True(report[RazerDirectProtocol.SizeAt] <= RazerDirectProtocol.MaxArguments, line);
                    byte commandClass = report[RazerDirectProtocol.ClassAt], command = report[RazerDirectProtocol.CommandAt];
                    byte first = report[RazerDirectProtocol.ArgumentsAt];
                    Assert.True(commandClass != 0x00 || command == 0xBF, line);
                    bool read = (command & 0x80) != 0;
                    // The commands whose first argument is the storage byte.
                    bool stores = (commandClass == 0x0F && command == 0x02)
                        || (commandClass == 0x03 && command is 0x00 or 0x01 or 0x02 or 0x0D);
                    if (stores) Assert.True(first == 0x00, line);
                    if (commandClass == 0x03 && command == 0x0A && first == 0x05)
                        Assert.True(report[RazerDirectProtocol.ArgumentsAt + 1] == 0x00, line);
                    bool listRead = (commandClass, command) is (0x0F, 0x80) or (0x00, 0xBF);
                    bool effectsRead = (commandClass, command) == (0x0F, 0x81);
                    if (read && !listRead && !effectsRead) Assert.True(first == 0x01, line);
                    if (listRead) Assert.True(report.Skip(RazerDirectProtocol.ArgumentsAt).Take(RazerDirectProtocol.MaxArguments).All(b => b == 0), line);
                    // The region effects read names its region alone.
                    if (effectsRead) Assert.True(first == row.ProbeLed && report.Skip(RazerDirectProtocol.ArgumentsAt + 1).Take(RazerDirectProtocol.MaxArguments - 1).All(b => b == 0), line);
                    checkedReports++;
                }
            }
            Assert.True(checkedReports > 1500, $"{checkedReports} reports were checked");

            // Neither source file can build one: class 00 command 04 has no builder.
            foreach (string file in new[] { "RazerDirectProtocol.cs", "RazerDirectBackend.cs" })
            {
                string source = RepoText("PadForge.App", "Common", "Input", "Peripherals", file);
                Assert.DoesNotContain("0x00, 0x04", source);
                Assert.DoesNotContain("DeviceMode(", source);
            }
        }

        // ─────────────────────────────────────────────
        //  The control collection
        // ─────────────────────────────────────────────

        private static HidCollectionInfo Collection(ushort productId, int mi, int col, ushort page, ushort usage, ushort feature = 91)
            => new()
            {
                Path = $@"\\?\hid#vid_1532&pid_{productId:x4}&mi_{mi:x2}" + (col > 0 ? $"&col{col:x2}" : "") + "#7&1a2b3c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
                VendorId = 0x1532, ProductId = productId, UsagePage = page, Usage = usage, InterfaceNumber = mi,
                FeatureReportLength = feature,
            };

        /// <summary>Among a product's collections the one on the interface
        /// the table names comes first, then the lower interface and the
        /// lower collection number, and never one picked by its usage
        /// alone.</summary>
        [Fact]
        public void TheControlCollection_IsTheTablesInterfaceFirst_ThenTheLowerOne()
        {
            var keyboard = Row(0x0203);     // interface 2, page 01, usage 02
            var onTwo = Collection(0x0203, 2, 1, 0x01, 0x02);
            var onTwoConsumer = Collection(0x0203, 2, 2, 0x0C, 0x01);
            var onOne = Collection(0x0203, 1, 1, 0x01, 0x06);
            var onZero = Collection(0x0203, 0, 0, 0x01, 0x06);
            var onThree = Collection(0x0203, 3, 0, 0x01, 0x02);
            var ordered = new[] { onThree, onZero, onTwoConsumer, onOne, onTwo }
                .OrderBy(c => RazerDirectProtocol.CollectionRank(keyboard, c)).ToArray();
            Assert.Equal(new[] { onTwo, onTwoConsumer, onZero, onOne, onThree }, ordered);
            // The same usage on another interface does not outrank the
            // table's interface with another usage.
            Assert.True(RazerDirectProtocol.CollectionRank(keyboard, onTwoConsumer) < RazerDirectProtocol.CollectionRank(keyboard, onThree));

            // A BlackWidow V4 names two usages on interface 3: the first
            // listed wins where both are there.
            var v4 = Row(0x0287);
            var generic = Collection(0x0287, 3, 2, 0x01, 0x00);
            var consumer = Collection(0x0287, 3, 1, 0x0C, 0x01);
            Assert.True(RazerDirectProtocol.CollectionRank(v4, generic) < RazerDirectProtocol.CollectionRank(v4, consumer));
            Assert.True(RazerDirectProtocol.CollectionRank(v4, consumer) < RazerDirectProtocol.CollectionRank(v4, Collection(0x0287, 2, 0, 0x01, 0x02)));

            // A path with no interface number, a device with one interface,
            // sorts after every numbered one.
            var bare = new HidCollectionInfo { Path = @"\\?\hid#vid_1532&pid_0203#8&1&0&0000#{guid}", VendorId = 0x1532, ProductId = 0x0203 };
            Assert.Equal(-1, bare.InterfaceNumber);
            Assert.True(RazerDirectProtocol.CollectionRank(keyboard, onThree) < RazerDirectProtocol.CollectionRank(keyboard, bare));

            Assert.Equal(1, RazerDirectProtocol.CollectionNumberOf(onTwo.Path));
            Assert.Equal(0x0A, RazerDirectProtocol.CollectionNumberOf(@"\\?\hid#vid_1532&pid_0203&mi_02&col0a#7&1&0&0009#{guid}"));
            Assert.Equal(0, RazerDirectProtocol.CollectionNumberOf(onZero.Path));
            // Only the device segment is read, so an instance ID that
            // holds the token is no collection number.
            Assert.Equal(0, RazerDirectProtocol.CollectionNumberOf(@"\\?\hid#vid_1532&pid_0203&mi_02#7&col05&0&0000#{guid}"));
            Assert.Equal(0, RazerDirectProtocol.CollectionNumberOf(null));
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
