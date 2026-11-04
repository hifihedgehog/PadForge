using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using P = PadForge.Common.Input.Peripherals.RazerDirectProtocol;

namespace PadForge.Tests
{
    /// <summary>
    /// Razer devices light by light (#557): the reports of a color per LED
    /// and per cell, a receiver's list of paired devices and its replies,
    /// and the light maps against OpenRGB's RazerDevices.cpp and openrazer's
    /// LED IDs. Every expected report is laid out here from the reference,
    /// as OpenRGB's razer_report is (RazerController.h at c6fc8016) with the
    /// checksum of razer_calculate_crc (RazerController.cpp:602-623), never
    /// from the builder under test.
    /// </summary>
    public class RazerLightsTests
    {
        /// <summary>A color of its own for each light: 1n 2n 3n for light n.</summary>
        private static int[] Distinct(int count)
            => Enumerable.Range(0, count).Select(i => (0x10 + i) << 16 | (0x20 + i) << 8 | (0x30 + i)).ToArray();

        private static byte[] Rgb(int i) => new[] { (byte)(0x10 + i), (byte)(0x20 + i), (byte)(0x30 + i) };

        private const int Fill = 0xA1B2C3;
        private static readonly byte[] FillRgb = { 0xA1, 0xB2, 0xC3 };

        /// <summary>One report as OpenRGB lays it out with the hidapi report
        /// ID in front: report ID 0, status 0, the transaction ID, two bytes
        /// of remaining packets and the protocol type at 0, the data size,
        /// the class, the command, 80 arguments, the checksum and a reserved
        /// byte. The checksum is the XOR of bytes 3 to 88.</summary>
        private static byte[] Expected(byte transactionId, byte commandClass, byte command, int size, params byte[] arguments)
        {
            var report = new byte[91];
            report[2] = transactionId;
            report[6] = (byte)size;
            report[7] = commandClass;
            report[8] = command;
            arguments.CopyTo(report, 9);
            byte crc = 0;
            for (int i = 3; i < 89; i++) crc ^= report[i];
            report[89] = crc;
            return report;
        }

        private static byte[] Cat(params IEnumerable<byte>[] parts) => parts.SelectMany(p => p).ToArray();

        private static string Text(byte[] report) => BitConverter.ToString(report).Replace('-', ' ');

        private static string[] Texts(IEnumerable<byte[]> reports) => reports.Select(Text).ToArray();

        private static RazerDirectRow Row(ushort productId) => RazerDirectTable.Find(productId);

        private static RazerLightRow Lights(ushort productId)
        {
            var lights = RazerLightTable.Find(productId);
            Assert.NotNull(lights);
            return lights;
        }

        // ─────────────────────────────────────────────
        //  The reports
        // ─────────────────────────────────────────────

        /// <summary>OpenRGB's extended matrix frame row: 0F/03, size the
        /// row's colors plus 5, arguments 2 to 4 the row, the start and the
        /// stop column, the colors from argument 5
        /// (RazerController.cpp:793-810). The custom frame effect is 0F/02
        /// at size 0C with 00 00 08 (931-940).</summary>
        [Fact]
        public void TheExtendedMatrixRowAndItsEffect_AreOpenRgbsBytes()
        {
            var colors = Distinct(3);
            Assert.Equal(Text(Expected(0x1F, 0x0F, 0x03, 14, Cat(new byte[] { 0, 0, 2, 0, 2 }, Rgb(0), Rgb(1), Rgb(2)))),
                Text(P.ExtendedMatrixRow(0x1F, 2, colors)));
            Assert.Equal(Text(Expected(0xFF, 0x0F, 0x02, 0x0C, 0, 0, 8)), Text(P.ExtendedCustom(0xFF)));
        }

        /// <summary>OpenRGB's standard matrix row with a color per column,
        /// 03/0B [FF row start stop colors] at the colors plus 4
        /// (RazerController.cpp:812-830), and its linear frame, 03/0C [start
        /// stop colors] at size 32 (777-791).</summary>
        [Fact]
        public void TheStandardRowAndLine_CarryAColorPerColumn()
        {
            var colors = Distinct(2);
            Assert.Equal(Text(Expected(0xFF, 0x03, 0x0B, 10, Cat(new byte[] { 0xFF, 4, 0, 1 }, Rgb(0), Rgb(1)))),
                Text(P.StandardRow(0xFF, 4, colors)));
            Assert.Equal(Text(Expected(0x3F, 0x03, 0x0C, 0x32, Cat(new byte[] { 0, 1 }, Rgb(0), Rgb(1)))),
                Text(P.StandardLine(0x3F, colors)));
        }

        /// <summary>The receiver's list read: class 00, command BF, size 50,
        /// no argument (RazerController.cpp:1283 and RazerController.h:62).</summary>
        [Fact]
        public void TheReceiversListRead_IsOpenRgbsBytes()
        {
            Assert.Equal(Text(Expected(0xFF, 0x00, 0xBF, 0x50)), Text(P.PairedDevicesRead(0xFF)));
            var dock = Row(0x00A4);
            Assert.Equal(RazerProbe.PairedDevices, dock.Probe);
            Assert.Equal(Text(Expected(0xFF, 0x00, 0xBF, 0x50)), Text(P.ProbeRead(dock, 0xFF)));
            // The Basilisk V4 Pro is read as OpenRGB first reads a product
            // painted through LED 00: its region list (1207-1224).
            var v4 = Row(0x00E2);
            Assert.Equal(RazerProbe.RegionList, v4.Probe);
            Assert.Equal(Text(Expected(0x1F, 0x0F, 0x80, 0x50)), Text(P.ProbeRead(v4, 0x1F)));
        }

        /// <summary>
        /// A receiver's answer lists a slot count, then each slot's status
        /// and big-endian product ID. Empty slots (0000 and FFFF) and the
        /// receiver's own ID are left out, the first eight devices are read,
        /// and only a success is a list (RazerController.cpp:1287-1303,
        /// RazerControllerDetect.cpp:54-60).
        /// </summary>
        [Fact]
        public void AReceiversAnswer_NamesItsPairedDevices()
        {
            byte[] Answer(byte status, params byte[] arguments)
            {
                var reply = Expected(0xFF, 0x00, 0xBF, 0x50, arguments);
                reply[1] = status;
                return reply;
            }
            // Four slots: a Basilisk V3 Pro connected, an empty slot, the
            // dock itself, and a sleeping DeathAdder V3 Pro.
            var reply = Answer(0x02, 4, 0x01, 0x00, 0xAB, 0x00, 0xFF, 0xFF, 0x01, 0x00, 0xA4, 0x00, 0x00, 0xB7);
            Assert.Equal(new ushort[] { 0x00AB, 0x00B7 }, P.PairedProductIds(reply, 0x00A4));
            // A slot count beyond the slots that hold a device reads zeros.
            Assert.Equal(new ushort[] { 0x00AB }, P.PairedProductIds(Answer(0x02, 2, 0x01, 0x00, 0xAB), 0x00A4));
            // Not a success: no list. Busy too, which carries no answer yet.
            Assert.Empty(P.PairedProductIds(Answer(0x05, 1, 0x01, 0x00, 0xAB), 0x00A4));
            Assert.Empty(P.PairedProductIds(Answer(0x01, 1, 0x01, 0x00, 0xAB), 0x00A4));
            // Nine devices: the first eight are read, the dock among them,
            // so seven are left.
            var nine = new List<byte> { 9 };
            for (int i = 0; i < 9; i++) nine.AddRange(new byte[] { 0x01, 0x00, (byte)(i == 3 ? 0xA4 : 0xB0 + i) });
            Assert.Equal(new ushort[] { 0x00B0, 0x00B1, 0x00B2, 0x00B4, 0x00B5, 0x00B6, 0x00B7 },
                P.PairedProductIds(Answer(0x02, nine.ToArray()), 0x00A4));
        }

        /// <summary>A relayed report's reply is final once its status is
        /// neither 00 nor 01 (RazerController.cpp:2114-2121): busy is read
        /// again, where a device of its own takes busy as done.</summary>
        [Fact]
        public void ARelayedReply_IsFinalOnlyPastBusy()
        {
            var request = P.ExtendedStatic(0x1F, 0x00, 0x123456);
            byte[] Reply(byte status)
            {
                var reply = (byte[])request.Clone();
                reply[P.StatusAt] = status;
                return reply;
            }
            Assert.Equal(RazerReply.Early, P.CheckRelayed(request, Reply(0x01)));
            Assert.Equal(RazerReply.Early, P.CheckRelayed(request, Reply(0x00)));
            Assert.Equal(RazerReply.Accepted, P.CheckRelayed(request, Reply(0x02)));
            Assert.Equal(RazerReply.Timeout, P.CheckRelayed(request, Reply(0x04)));
            Assert.Equal(RazerReply.NotSupported, P.CheckRelayed(request, Reply(0x05)));
            Assert.Equal(RazerReply.Failure, P.CheckRelayed(request, Reply(0x03)));
            Assert.Equal(RazerReply.Accepted, P.Check(request, Reply(0x01)));
            // Another request's reply is not this one's, busy or not.
            var other = Reply(0x02);
            other[P.TransactionAt] = 0xFF;
            Assert.Equal(RazerReply.Early, P.CheckRelayed(request, other));
        }

        // ─────────────────────────────────────────────
        //  A color per light
        // ─────────────────────────────────────────────

        /// <summary>A DeathAdder V2 is painted one 0F/02 static report per
        /// LED, as openrazer paints its logo (04) and its wheel (01)
        /// (razermouse_driver.c:5185-5190): each LED in its own color, and
        /// an LED the device named that the map leaves out in the fill.</summary>
        [Fact]
        public void AMousePaintedByLed_TakesEachLedsColor()
        {
            var row = Row(0x0084);
            var lights = Lights(0x0084);
            Assert.Equal(RazerLightPlan.PerLed, lights.Plan);
            var colors = Distinct(2);
            Assert.Equal(new[]
            {
                Text(Expected(0x3F, 0x0F, 0x02, 9, Cat(new byte[] { 0, 0x04, 1, 0, 0, 1 }, Rgb(0)))),
                Text(Expected(0x3F, 0x0F, 0x02, 9, Cat(new byte[] { 0, 0x01, 1, 0, 0, 1 }, Rgb(1)))),
                Text(Expected(0x3F, 0x0F, 0x02, 9, Cat(new byte[] { 0, 0x0A, 1, 0, 0, 1 }, FillRgb))),
            }, Texts(P.PaintLights(row, lights, lights.Lights, 0x3F, new byte[] { 0x04, 0x01, 0x0A }, colors, Fill)));
        }

        /// <summary>A DeathAdder Chroma's classic LEDs: a color and the
        /// static effect for each (razerchromacommon.c:119-129 and 148-156),
        /// the wheel's color on 01 and the logo's on 04, which openrazer
        /// names by LED.</summary>
        [Fact]
        public void AClassicMouse_TakesAColorAndAnEffectPerLed()
        {
            var row = Row(0x0043);
            var lights = Lights(0x0043);
            Assert.Equal(new[] { (0x01, LightKind.Zone), (0x04, LightKind.Logo) }, lights.Lights.Select(l => (l.Address, l.Kind)));
            var colors = Distinct(2);
            Assert.Equal(new[]
            {
                Text(Expected(0x3F, 0x03, 0x01, 5, Cat(new byte[] { 0, 0x01 }, Rgb(0)))),
                Text(Expected(0x3F, 0x03, 0x02, 3, 0, 0x01, 0x00)),
                Text(Expected(0x3F, 0x03, 0x01, 5, Cat(new byte[] { 0, 0x04 }, Rgb(1)))),
                Text(Expected(0x3F, 0x03, 0x02, 3, 0, 0x04, 0x00)),
            }, Texts(P.PaintLights(row, lights, lights.Lights, 0x3F, row.Leds, colors, Fill)));
        }

        /// <summary>A Naga Chroma's mouse extended matrix: 03/0D [00 LED 06
        /// R G B] for each LED (razerchromacommon.c:810-820).</summary>
        [Fact]
        public void ANagaChroma_TakesAColorPerLed()
        {
            var row = Row(0x0053);
            var lights = Lights(0x0053);
            var colors = Distinct(3);
            Assert.Equal(new[]
            {
                Text(Expected(0x3F, 0x03, 0x0D, 6, Cat(new byte[] { 0, 0x05, 0x06 }, Rgb(0)))),
                Text(Expected(0x3F, 0x03, 0x0D, 6, Cat(new byte[] { 0, 0x04, 0x06 }, Rgb(1)))),
                Text(Expected(0x3F, 0x03, 0x0D, 6, Cat(new byte[] { 0, 0x01, 0x06 }, Rgb(2)))),
            }, Texts(P.PaintLights(row, lights, lights.Lights, 0x3F, row.Leds, colors, Fill)));
        }

        /// <summary>A Basilisk V3 Pro, painted one color by an effect on LED
        /// 00, is painted light by light the way OpenRGB's Direct mode does
        /// it: its 13 LEDs as one row of the extended matrix frame, LED n in
        /// cell n, then the custom frame effect (RazerController.cpp:207-258).</summary>
        [Fact]
        public void ABasiliskV3Pro_TakesTheExtendedMatrixFrame()
        {
            var row = Row(0x00AA);
            var lights = Lights(0x00AA);
            Assert.Equal((RazerLightPlan.Frame, 1, 13), (lights.Plan, (int)lights.Rows, (int)lights.Columns));
            var colors = Distinct(13);
            Assert.Equal(new[]
            {
                Text(Expected(0x1F, 0x0F, 0x03, 5 + 39, Cat(new byte[] { 0, 0, 0, 0, 12 }, Enumerable.Range(0, 13).SelectMany(Rgb)))),
                Text(Expected(0x1F, 0x0F, 0x02, 0x0C, 0, 0, 8)),
            }, Texts(P.PaintLights(row, lights, lights.Lights, 0x1F, row.Leds, colors, Fill)));
        }

        /// <summary>A Basilisk V4 Pro has seven lights in a frame of 13
        /// (RazerDevices.cpp:5481-5500): cells 7 to 12 take the fill.</summary>
        [Fact]
        public void ABasiliskV4Pro_FillsTheCellsPastItsSevenLights()
        {
            var row = Row(0x00E3);
            var lights = Lights(0x00E3);
            var colors = Distinct(7);
            var cells = Enumerable.Range(0, 7).SelectMany(Rgb).Concat(Enumerable.Repeat(FillRgb, 6).SelectMany(c => c));
            Assert.Equal(new[]
            {
                Text(Expected(0x1F, 0x0F, 0x03, 5 + 39, Cat(new byte[] { 0, 0, 0, 0, 12 }, cells))),
                Text(Expected(0x1F, 0x0F, 0x02, 0x0C, 0, 0, 8)),
            }, Texts(P.PaintLights(row, lights, lights.Lights, 0x1F, row.Leds, colors, Fill)));
        }

        /// <summary>A Mamba Tournament Edition's one line of 16 LEDs: 03/0C
        /// at size 32, then the show report (RazerController.cpp:777-791 and
        /// 942-950).</summary>
        [Fact]
        public void AMambaTournamentEdition_TakesItsLineAColorPerLed()
        {
            var row = Row(0x0046);
            var lights = Lights(0x0046);
            var colors = Distinct(16);
            Assert.Equal(new[]
            {
                Text(Expected(0xFF, 0x03, 0x0C, 0x32, Cat(new byte[] { 0, 15 }, Enumerable.Range(0, 16).SelectMany(Rgb)))),
                Text(Expected(0xFF, 0x03, 0x0A, 2, 0x05, 0x00)),
            }, Texts(P.PaintLights(row, lights, lights.Lights, 0xFF, row.Leds, colors, Fill)));
        }

        /// <summary>
        /// An Orbweaver Chroma's 20 keys on its 5 by 22 standard frame, as
        /// OpenRGB places them: each of its first three rows of five one row
        /// down and one column right, and the fourth row's first key in
        /// column 1, the other four in columns 3 to 6
        /// (RazerController.cpp:1536-1566). Every other cell takes the fill.
        /// </summary>
        [Fact]
        public void AnOrbweaver_TakesItsKeysWhereOpenRgbPutsThem()
        {
            var row = Row(0x0207);
            var lights = Lights(0x0207);
            var colors = Distinct(20);
            var grid = new byte[5][];
            for (int r = 0; r < 5; r++) grid[r] = Enumerable.Repeat(FillRgb, 22).SelectMany(c => c).ToArray();
            void Put(int key, int r, int c) => Rgb(key).CopyTo(grid[r], 3 * c);
            for (int key = 0; key < 15; key++) Put(key, key / 5 + 1, key % 5 + 1);
            Put(15, 4, 1);
            for (int key = 16; key < 20; key++) Put(key, 4, key - 13);
            var expected = new List<string>();
            for (int r = 0; r < 5; r++)
                expected.Add(Text(Expected(0x3F, 0x03, 0x0B, 4 + 66, Cat(new byte[] { 0xFF, (byte)r, 0, 21 }, grid[r]))));
            expected.Add(Text(Expected(0x3F, 0x03, 0x0A, 2, 0x05, 0x00)));
            Assert.Equal(expected, Texts(P.PaintLights(row, lights, lights.Lights, 0x3F, row.Leds, colors, Fill)));
        }

        /// <summary>A Naga Pro's one-row frame keeps the mode report in its
        /// claim and gets a color per LED in its row, openrazer's
        /// [00 00 00 00 stop colors] at the exact size
        /// (razerchromacommon.c:1444-1464).</summary>
        [Fact]
        public void ANagaPro_TakesItsOneRowFrameAColorPerLed()
        {
            var row = Row(0x008F);
            var lights = Lights(0x008F);
            var colors = Distinct(3);
            Assert.Equal(new[] { Text(Expected(0x1F, 0x0F, 0x03, 14, Cat(new byte[] { 0, 0, 0, 0, 2 }, Rgb(0), Rgb(1), Rgb(2)))) },
                Texts(P.PaintLights(row, lights, lights.Lights, 0x1F, row.Leds, colors, Fill)));
        }

        /// <summary>The Mouse Dock Pro is painted one color by the frame
        /// OpenRGB paints it with: one row of nine and the custom frame
        /// effect, under ID FF (RazerDevices.cpp:9455-9473), and light by
        /// light in the same reports.</summary>
        [Fact]
        public void TheMouseDockPro_IsPaintedByItsFrame()
        {
            var row = Row(0x00A4);
            Assert.Equal((RazerFamily.ExtendedMatrix, 0xFF, 1, 9), (row.Family, (int)row.TransactionId, (int)row.Rows, (int)row.Columns));
            Assert.Equal(new[]
            {
                Text(Expected(0xFF, 0x0F, 0x03, 5 + 27, Cat(new byte[] { 0, 0, 0, 0, 8 }, Enumerable.Repeat(new byte[] { 0x12, 0x34, 0x56 }, 9).SelectMany(c => c)))),
                Text(Expected(0xFF, 0x0F, 0x02, 0x0C, 0, 0, 8)),
            }, Texts(P.Paint(row, 0xFF, row.Leds, 0x123456)));
            Assert.Empty(P.Claim(row, 0xFF));
            Assert.Equal(new[] { Text(Expected(0xFF, 0x0F, 0x02, 6, 0, 0, 3)) }, Texts(P.SpectrumBack(row, 0xFF)));
            var colors = Distinct(9);
            Assert.Equal(Text(Expected(0xFF, 0x0F, 0x03, 5 + 27, Cat(new byte[] { 0, 0, 0, 0, 8 }, Enumerable.Range(0, 9).SelectMany(Rgb)))),
                Texts(P.PaintLights(row, Lights(0x00A4), Lights(0x00A4).Lights, 0xFF, row.Leds, colors, Fill))[0]);
        }

        // ─────────────────────────────────────────────
        //  The light maps
        // ─────────────────────────────────────────────

        private static (int, LightKind)[] Kinds(params (int Count, LightKind Kind)[] runs)
        {
            var list = new List<(int, LightKind)>();
            foreach (var (count, kind) in runs)
                for (int i = 0; i < count; i++) list.Add((list.Count, kind));
            return list.ToArray();
        }

        /// <summary>Each product's lights follow OpenRGB's zones in the
        /// order its RazerDevices.cpp lists them, LED n in cell n, at
        /// c6fc8016: a logo, a scroll wheel or a side panel as zones, strips
        /// and underglow as edge lighting, and a keypad's keys as keys no
        /// source has named.</summary>
        [Fact]
        public void TheLightsFollowOpenRgbsZones()
        {
            (int, LightKind)[] Of(ushort productId) => Lights(productId).Lights.Select(l => (l.Address, l.Kind)).ToArray();
            // Basilisk V3 Pro (5046-5065): Scroll Wheel, Logo, LED Strip of 11.
            Assert.Equal(Kinds((1, LightKind.Zone), (1, LightKind.Logo), (11, LightKind.Edge)), Of(0x00AA));
            // Lancehead (6193-6244): Right LED Strip 7, Left LED Strip 7, Logo, Scroll Wheel.
            Assert.Equal(Kinds((14, LightKind.Edge), (1, LightKind.Logo), (1, LightKind.Zone)), Of(0x0059));
            // Mamba Tournament Edition (6939-6990): Left 7, Right 7, Logo, Scroll Wheel.
            Assert.Equal(Kinds((14, LightKind.Edge), (1, LightKind.Logo), (1, LightKind.Zone)), Of(0x0046));
            // Basilisk V4 Pro (5481-5500 and 72262b83): Logo, Scroll Wheel, LED Strip of 5.
            Assert.Equal(Kinds((1, LightKind.Logo), (1, LightKind.Zone), (5, LightKind.Edge)), Of(0x00E2));
            // Mouse Dock Pro (9447-9473): Base of 9.
            Assert.Equal(Kinds((9, LightKind.Edge)), Of(0x00A4));
            // Tartarus Pro (7879-7914): Keypad of 4 by 5 and Keypad LED 20.
            Assert.Equal(Kinds((21, LightKind.Key)), Of(0x0244));
            Assert.All(Lights(0x0244).Lights, l => Assert.Equal((0, LightKeySource.None), (l.Usage, l.KeySource)));
            // Ornata V3: its keyboard as ten zones, numbered 1 to 10.
            Assert.Equal(Enumerable.Range(1, 10), Lights(0x02A1).Lights.Select(l => l.Zone));
            // Naga Trinity: Scroll Wheel, Logo, Numpad, two numbered zones.
            Assert.Equal(new[] { (LightKind.Zone, 1), (LightKind.Logo, 0), (LightKind.Zone, 2) },
                Lights(0x0067).Lights.Select(l => (l.Kind, l.Zone)));
            // A DeathAdder V2 by its LEDs: 04 the logo, 01 the wheel
            // (openrazer razercommon.h:37-48).
            Assert.Equal(new[] { (0x04, LightKind.Logo), (0x01, LightKind.Zone) }, Of(0x0084));
            // A Naga Left-Handed 2020: 10 is the right side, edge lighting.
            Assert.Equal(new[] { (0x04, LightKind.Logo), (0x01, LightKind.Zone), (0x10, LightKind.Edge) }, Of(0x008D));
        }

        // ─────────────────────────────────────────────
        //  Keyboards
        // ─────────────────────────────────────────────

        /// <summary>A keyboard is asked its layout with 00/86 at size 00
        /// under its own ID, no argument (OpenRGB RazerController.cpp:1308-1320).</summary>
        [Fact]
        public void TheKeyboardInfoRead_IsOpenRgbsBytes()
            => Assert.Equal(Text(Expected(0x1F, 0x00, 0x86, 0x00)), Text(P.KeyboardInfoRead(0x1F)));

        /// <summary>The layout byte a keyboard answers picks its region the
        /// way OpenRGB's controller does: the values of RazerController.h:117-137,
        /// their type by GetKeyboardLayoutType (RazerController.cpp:1329-1363),
        /// and the layout built for each type (RGBController_Razer.cpp:238-258),
        /// JIS and unknown ones laid out ANSI.</summary>
        [Theory]
        [InlineData(1, (int)KeyboardLayout.AnsiQwerty)]   // US
        [InlineData(2, (int)KeyboardLayout.IsoQwerty)]    // Greek
        [InlineData(3, (int)KeyboardLayout.IsoQwertz)]    // German
        [InlineData(4, (int)KeyboardLayout.IsoAzerty)]    // French
        [InlineData(5, (int)KeyboardLayout.AnsiQwerty)]   // Russian
        [InlineData(6, (int)KeyboardLayout.IsoQwerty)]    // UK
        [InlineData(7, (int)KeyboardLayout.IsoQwerty)]    // Nordic
        [InlineData(8, (int)KeyboardLayout.AnsiQwerty)]   // Chinese Traditional
        [InlineData(9, (int)KeyboardLayout.IsoQwerty)]    // Korean
        [InlineData(10, (int)KeyboardLayout.AnsiQwerty)]  // Turkish
        [InlineData(11, (int)KeyboardLayout.AnsiQwerty)]  // Thai
        [InlineData(12, (int)KeyboardLayout.AnsiQwerty)]  // Japanese: JIS, built ANSI
        [InlineData(13, (int)KeyboardLayout.IsoQwerty)]   // Portuguese (Brazil)
        [InlineData(14, (int)KeyboardLayout.IsoQwerty)]   // Spanish (Latin America)
        [InlineData(15, (int)KeyboardLayout.IsoQwerty)]   // Swiss
        [InlineData(16, (int)KeyboardLayout.IsoQwerty)]   // Spanish (Europe)
        [InlineData(17, (int)KeyboardLayout.IsoQwerty)]   // Italian
        [InlineData(18, (int)KeyboardLayout.IsoQwerty)]   // Portuguese (Portugal)
        [InlineData(19, (int)KeyboardLayout.IsoQwerty)]   // Hebrew
        [InlineData(20, (int)KeyboardLayout.AnsiQwerty)]  // Arabic
        [InlineData(0, (int)KeyboardLayout.AnsiQwerty)]   // none
        [InlineData(99, (int)KeyboardLayout.AnsiQwerty)]  // unknown
        public void AKeyboardsLayoutByte_PicksItsRegionAsOpenRgbDoes(byte layout, int region)
        {
            var reply = Expected(0x1F, 0x00, 0x86, 0x00, layout, 0x01);
            reply[1] = 0x02;
            Assert.Equal((KeyboardLayout)region, P.RegionOf(reply));
            reply[1] = 0x05;
            Assert.Null(P.RegionOf(reply));
        }

        private static LightInfo At(LightMap map, int address) => map.Lights.Single(l => l.Address == address);

        /// <summary>
        /// A BlackWidow Chroma's keys in each region, as OpenRGB's own
        /// KeyboardLayoutManager lays razer_blackwidow_chroma_layout out
        /// (its output dumped in tools/openrgb_klm/klm_dump.tsv): Escape at
        /// row 0 column 1, Q at row 2 column 2, the logo at row 0 column 20,
        /// the ANSI backslash at row 2 column 14, and on an ISO board the #
        /// key at row 3 column 13 and the ISO backslash at row 4 column 2,
        /// each at its cell of the 6 by 22 frame. The keys carry their
        /// keyboard-page usages: Escape 29, Q 14, backslash 31, non-US # 32,
        /// non-US backslash 64 (HID Usage Tables 1.6, 10).
        /// </summary>
        [Fact]
        public void ABlackWidowChroma_TakesItsKeysFromOpenRgbsLayout_InItsRegion()
        {
            var ansi = RazerLightTable.Map(0x0203, KeyboardLayout.AnsiQwerty);
            Assert.Equal((LightKind.Key, (ushort)0x29), (At(ansi, 1).Kind, At(ansi, 1).Usage));
            Assert.Equal((ushort)0x14, At(ansi, 2 * 22 + 2).Usage);
            Assert.Equal(LightKind.Logo, At(ansi, 20).Kind);
            Assert.Equal((ushort)0x31, At(ansi, 2 * 22 + 14).Usage);
            Assert.DoesNotContain(ansi.Lights, l => l.Address == 3 * 22 + 13);
            Assert.Equal(110, ansi.Lights.Count);
            Assert.Equal((2f, 2f, true), (At(ansi, 46).X, At(ansi, 46).Y, At(ansi, 46).HasPosition));
            Assert.All(ansi.Lights.Where(l => l.Kind == LightKind.Key), l => Assert.Equal(LightKeySource.Generated, l.KeySource));

            var iso = RazerLightTable.Map(0x0203, KeyboardLayout.IsoQwerty);
            Assert.Equal((ushort)0x32, At(iso, 3 * 22 + 13).Usage);
            Assert.Equal((ushort)0x64, At(iso, 4 * 22 + 2).Usage);
            Assert.DoesNotContain(iso.Lights, l => l.Address == 2 * 22 + 14);
            Assert.Equal(111, iso.Lights.Count);
            Assert.Equal(112, RazerLightTable.Map(0x0203, KeyboardLayout.IsoQwertz).Lights.Count);
            Assert.Same(iso, RazerLightTable.Map(0x0203, KeyboardLayout.IsoQwerty));
        }

        /// <summary>A Blade Stealth's layout is 16 columns wide in OpenRGB
        /// (razer_blade_stealth_2016_layout, RazerDevices.cpp:4262) and
        /// openrazer paints it 22 wide: each key keeps its row and column,
        /// Delete at row 0 column 15 lands at cell 15 and the backtick at row
        /// 1 column 1 at cell 23.</summary>
        [Fact]
        public void ALaptopLaidOutNarrowerThanItsFrame_KeepsEachKeysRowAndColumn()
        {
            var map = RazerLightTable.Map(0x0205);
            Assert.Equal((6, 22), ((int)Lights(0x0205).Rows, (int)Lights(0x0205).Columns));
            Assert.Equal((ushort)0x4C, At(map, 15).Usage);
            Assert.Equal((ushort)0x35, At(map, 22 + 1).Usage);
            Assert.Equal(87, map.Lights.Count);
        }

        /// <summary>A Huntsman Elite's keyboard zone is rows 0 to 5 of its 9
        /// by 22 frame, and its underglow of 66 LEDs the three rows below
        /// (RazerDevices.cpp, huntsman_elite: zones Keyboard 6 by 22 and
        /// Underglow 3 by 22), edge lighting at cells 132 to 197.</summary>
        [Fact]
        public void AHuntsmanElitesUnderglow_IsTheThreeRowsBelowItsKeys()
        {
            var map = RazerLightTable.Map(0x0226);
            Assert.Equal((9, 22), ((int)Lights(0x0226).Rows, (int)Lights(0x0226).Columns));
            Assert.Equal(Enumerable.Range(132, 66), map.Lights.Where(l => l.Kind == LightKind.Edge).Select(l => l.Address));
            Assert.All(map.Lights.Where(l => l.Kind == LightKind.Key), l => Assert.InRange(l.Address, 0, 131));
        }

        /// <summary>A keyboard OpenRGB lists with no layout, and one only
        /// openrazer lists, have a key at every cell of the zone, whose key no
        /// source names, for Map Key Lights: a Blade Stealth (Late 2017) its
        /// 6 by 16 (RazerDevices.cpp, zone Keyboard), a Blade 18 (2025)
        /// openrazer's 6 by 19.</summary>
        [Fact]
        public void AKeyboardNoLayoutNames_HasAnUnnamedKeyAtEveryCell()
        {
            var blade = RazerLightTable.Map(0x0232);
            Assert.Equal(96, blade.UnnamedKeyCount);
            Assert.Equal(96, blade.Lights.Count);
            var newest = RazerLightTable.Map(0x02C7);
            Assert.Equal(6 * 19, newest.UnnamedKeyCount);
            Assert.Equal("razer:1532:02C7", newest.Product);
        }

        /// <summary>A BlackWidow V4 Pro's Void zone, five LEDs that "were not
        /// connected" (RazerDevices.cpp:1820), is no light.</summary>
        [Fact]
        public void ABlackWidowV4ProsVoidZone_IsNoLight()
        {
            var map = RazerLightTable.Map(0x028D);
            // Keyboard 6 by 23 (138), underglow 9 and 9, void 5, wrist rest 20.
            int[] edges = map.Lights.Where(l => l.Kind == LightKind.Edge).Select(l => l.Address).ToArray();
            Assert.Equal(Enumerable.Range(138, 18).Concat(Enumerable.Range(161, 20)), edges);
        }

        /// <summary>
        /// Every Razer keyboard and keypad laid out by a recipe, in every
        /// region OpenRGB builds it in, has exactly the keys OpenRGB's own
        /// KeyboardLayoutManager puts in its zone, each at the cell OpenRGB
        /// sends it to and with the usage its name stands for. The keys are
        /// read from tools/openrgb_klm/klm_dump.tsv, the output of OpenRGB's
        /// code at c6fc8016 (rows "case", then row, column, value and name
        /// per key), where the first key listed at a cell names it, as
        /// GetKeyNameAt returns it (KeyboardLayoutManager.cpp:933-944).
        /// </summary>
        [Fact]
        public void EveryRazerLayout_InEveryRegion_HasOpenRgbsKeysAtOpenRgbsCells()
        {
            var cases = new Dictionary<(string Recipe, string Region), Dictionary<(int, int), string>>();
            Dictionary<(int, int), string> current = null;
            foreach (string line in RepoLines("tools", "openrgb_klm", "klm_dump.tsv"))
            {
                var f = line.Split('\t');
                if (f[0] == "case")
                {
                    current = f[1] == "Razer" ? cases[(f[2], f[3])] = new Dictionary<(int, int), string>() : null;
                    continue;
                }
                if (current == null || !int.TryParse(f[0], out int row)) continue;
                current.TryAdd((row, int.Parse(f[1])), f[3]);
            }
            Assert.Equal(156, cases.Count);
            int checkedLayouts = 0;
            foreach (var lights in RazerLightTable.Rows.Where(r => r.KeyboardRecipe != null))
            {
                foreach (var region in new[] { KeyboardLayout.AnsiQwerty, KeyboardLayout.IsoQwerty, KeyboardLayout.IsoQwertz, KeyboardLayout.IsoAzerty })
                {
                    Assert.True(cases.TryGetValue((lights.KeyboardRecipe, region.ToString()), out var cells),
                        $"{lights.ProductId:X4} {lights.KeyboardRecipe} {region}");
                    var map = RazerLightTable.Map(lights.ProductId, region);
                    int keys = 0;
                    foreach (var ((keyRow, keyCol), name) in cells)
                    {
                        if (keyRow >= lights.KeyRows || keyCol >= lights.KeyColumns || name.Length == 0) continue;
                        int index = lights.KeyBase + keyRow * lights.KeyColumns + keyCol;
                        int frameRow = index / lights.DeviceColumns, frameCol = index % lights.DeviceColumns;
                        if (frameRow >= lights.Rows || frameCol >= lights.Columns) continue;
                        int address = frameRow * lights.Columns + frameCol;
                        var light = map.Lights.Single(l => l.Address == address);
                        OpenRgbKeys.TryGet(name, out ushort usage, out _);
                        Assert.Equal(usage, light.Usage);
                        keys++;
                    }
                    Assert.Equal(keys, map.Lights.Count - lights.Lights.Length);
                    checkedLayouts++;
                }
            }
            Assert.Equal(52 * 4, checkedLayouts);
        }

        private static IEnumerable<string> RepoLines(params string[] parts)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return System.IO.File.ReadLines(System.IO.Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        // ─────────────────────────────────────────────
        //  Accessories and second sources (#557, part two)
        // ─────────────────────────────────────────────

        /// <summary>The region effects read: 0F/81 at size 50 with the
        /// region alone (OpenRGB RazerController.cpp:1244-1248), the claim
        /// read of a product OpenRGB paints through LED 05, such as a
        /// BlackWidow V4 Low Profile TKL.</summary>
        [Fact]
        public void TheRegionEffectsRead_IsOpenRgbsBytes()
        {
            Assert.Equal(Text(Expected(0x9F, 0x0F, 0x81, 0x50, 0x05)), Text(P.RegionEffectsRead(0x9F, 0x05)));
            var row = Row(0x02D2);
            Assert.Equal((RazerProbe.RegionEffects, (byte)0x05), (row.Probe, row.ProbeLed));
            Assert.Equal(Text(Expected(0x9F, 0x0F, 0x81, 0x50, 0x05)), Text(P.ProbeRead(row, 0x9F)));
        }

        /// <summary>The Leviathan V2 speakers carry report ID 07, OpenRGB's
        /// report_index for them (RazerController.cpp:50-60), and no other
        /// product does.</summary>
        [Fact]
        public void OnlyTheLeviathanSpeakers_CarryReportIdSeven()
        {
            Assert.Equal(new ushort[] { 0x0532, 0x054A },
                RazerDirectTable.Rows.Where(r => r.ReportId != 0).Select(r => r.ProductId));
            Assert.All(new ushort[] { 0x0532, 0x054A }, pid => Assert.Equal(0x07, Row(pid).ReportId));
        }

        /// <summary>A collection OpenRGB finds by usage alone, on any
        /// interface (REGISTER_HID_DETECTOR_PU, RazerControllerDetect.cpp:411),
        /// ranks first wherever it sits; one found by interface alone ranks by
        /// its interface (REGISTER_HID_DETECTOR_I, 446).</summary>
        [Fact]
        public void ACollectionFoundByUsageAlone_RanksFirstOnAnyInterface()
        {
            HidCollectionInfo At(int mi, ushort page, ushort usage) => new()
            {
                Path = $@"\\?\hid#vid_1532&pid_00e7&mi_{mi:x2}#7&1&0&0000#{{guid}}",
                VendorId = 0x1532, ProductId = 0x00E7, UsagePage = page, Usage = usage, InterfaceNumber = mi,
                FeatureReportLength = 91,
            };
            var naga = Row(0x00E7);
            Assert.True(P.CollectionRank(naga, At(2, 0x01, 0x03)) < P.CollectionRank(naga, At(0, 0x01, 0x02)));
            var tiamat = Row(0x0F03);
            Assert.True(P.CollectionRank(tiamat, At(0, 0x0C, 0x01)) < P.CollectionRank(tiamat, At(1, 0x01, 0x02)));
        }

        /// <summary>The accessories' lights follow OpenRGB's zones like any
        /// product's: a Firefly V2's 19 LEDs of edge lighting
        /// (RazerDevices.cpp:8068), a Chroma HDK's four channels of 16 on its
        /// 4 by 16 frame (9155), a Razer Core's side window as a zone and its
        /// strip of eight (8903), a Nommo Chroma's two speakers of 24 (9553).</summary>
        [Fact]
        public void TheAccessoriesLightsFollowOpenRgbsZones()
        {
            (int, LightKind)[] Of(ushort productId) => Lights(productId).Lights.Select(l => (l.Address, l.Kind)).ToArray();
            Assert.Equal(Kinds((19, LightKind.Edge)), Of(0x0C04));
            Assert.Equal((4, 16), ((int)Lights(0x0F09).Rows, (int)Lights(0x0F09).Columns));
            Assert.Equal(Kinds((64, LightKind.Edge)), Of(0x0F09));
            Assert.Equal(Kinds((1, LightKind.Zone), (8, LightKind.Edge)), Of(0x0215));
            Assert.Equal(Kinds((48, LightKind.Edge)), Of(0x0517));
            // The Chroma HDK is painted by the extended matrix frame: four
            // rows of 16, then the custom frame effect.
            var hdk = Row(0x0F09);
            var reports = P.PaintLights(hdk, Lights(0x0F09), Lights(0x0F09).Lights, 0x3F, hdk.Leds, Distinct(64), Fill);
            Assert.Equal(5, reports.Count);
            Assert.Equal(Text(Expected(0x3F, 0x0F, 0x03, 5 + 48, Cat(new byte[] { 0, 0, 3, 0, 15 }, Enumerable.Range(48, 16).SelectMany(Rgb)))),
                Text(reports[3]));
            Assert.Equal(Text(Expected(0x3F, 0x0F, 0x02, 0x0C, 0, 0, 8)), Text(reports[4]));
        }

        /// <summary>
        /// A Blade 15 (2018), which OpenRGB lists with no layout, takes its
        /// keys from uchroma's own key_mapping for it (uchroma
        /// uchroma/server/data/laptop.yaml:571-656 at ab05c7f), the one source
        /// whose 6 by 16 matrix fits OpenRGB's zone: Escape at row 0 column 1,
        /// Q at row 2 column 2, Backspace over two cells, and at row 5 column
        /// 3, where uchroma lists both, the first, left Meta. Cells uchroma
        /// names nothing stay keys for Map Key Lights.
        /// </summary>
        [Fact]
        public void ABlade15Of2018_TakesItsKeysFromUchroma()
        {
            var map = RazerLightTable.Map(0x0233);
            ushort UsageAt(int row, int col) => map.Lights.Single(l => l.Address == row * 16 + col).Usage;
            Assert.Equal((ushort)0x29, UsageAt(0, 1));
            Assert.Equal((ushort)0x14, UsageAt(2, 2));
            Assert.Equal(((ushort)0x2A, (ushort)0x2A), (UsageAt(1, 14), UsageAt(1, 15)));
            Assert.Equal((ushort)0xE3, UsageAt(5, 3));
            Assert.Equal((ushort)0x4F, UsageAt(5, 14));
            Assert.Equal(96, map.Lights.Count);
            Assert.Equal(0, UsageAt(0, 0));
            Assert.Contains("uchroma", map.Source);
        }

        /// <summary>A Blade 16 (2023), which only openrazer lists, takes its
        /// keys from Polychromatic's US map drawn for it by that name
        /// (data/devicemaps/blade_16_2023_en_US.svg at cd024b2, 6 by 16): esc
        /// at row 0 column 1, Q at row 2 column 2, the first shift of row 4
        /// the left one and the second the right one, fn a label. A key whose
        /// keycap the map draws with no label stays unnamed.</summary>
        [Fact]
        public void ABlade16Of2023_TakesItsKeysFromPolychromatic()
        {
            var map = RazerLightTable.Map(0x029F);
            LightInfo At(int row, int col) => map.Lights.Single(l => l.Address == row * 16 + col);
            Assert.Equal((ushort)0x29, At(0, 1).Usage);
            Assert.Equal((ushort)0x14, At(2, 2).Usage);
            Assert.Equal(((ushort)0xE1, (ushort)0xE5), (At(4, 1).Usage, At(4, 15).Usage));
            Assert.Equal((LightKind.Other, LightLabel.Fn), (At(5, 2).Kind, At(5, 2).Label));
            Assert.Equal((LightKind.Key, (ushort)0), (At(5, 13).Kind, At(5, 13).Usage));
            Assert.Contains("Polychromatic", map.Source);
        }

        /// <summary>A board no second source fits keeps a key at every cell
        /// that no source names: a Blade Stealth (Late 2017), which neither
        /// uchroma nor Polychromatic lists, and a Blade 15 (2018) Mercury,
        /// whose uchroma entry takes its parent's mapping, which names cells
        /// outside its 6 by 16 matrix.</summary>
        [Fact]
        public void ABoardNoSecondSourceFits_KeepsItsKeysUnnamed()
        {
            Assert.Equal(96, RazerLightTable.Map(0x0232).UnnamedKeyCount);
            Assert.Equal(96, RazerLightTable.Map(0x0240).UnnamedKeyCount);
            Assert.Empty(RazerLightTable.Find(0x0232).Lights);
            Assert.Empty(RazerLightTable.Find(0x0240).Lights);
        }

        /// <summary>Every light map's lights sit where its frames reach:
        /// cells inside the frame, which is the frame the product's own
        /// table row paints by where the family paints by frame, or LEDs of
        /// the product's own row. No two lights share an address, and every
        /// map has more than one light and its product key.</summary>
        [Fact]
        public void EveryLightSitsWhereTheProductsFramesReachIt()
        {
            Assert.Equal(184, RazerLightTable.Rows.Count);
            foreach (var lights in RazerLightTable.Rows)
            {
                string id = lights.ProductId.ToString("X4");
                var row = Row(lights.ProductId);
                Assert.True(row is { HasColorPath: true }, id);
                foreach (var region in new[] { KeyboardLayout.AnsiQwerty, KeyboardLayout.IsoQwerty, KeyboardLayout.IsoQwertz, KeyboardLayout.IsoAzerty })
                {
                    var all = RazerLightTable.Map(lights.ProductId, region).Lights;
                    Assert.True(all.Count > 1, id);
                    Assert.Equal(all.Count, all.Select(l => l.Address).Distinct().Count());
                    if (lights.Plan == RazerLightPlan.Frame)
                        Assert.All(all, l => Assert.InRange(l.Address, 0, lights.Rows * lights.Columns - 1));
                }
                Assert.False(string.IsNullOrEmpty(lights.Source), id);
                if (lights.KeyboardRecipe != null)
                {
                    Assert.NotNull(KeyboardLayouts.Recipe(lights.KeyboardRecipe));
                    // The recipe is the one L1a's model rows name for the
                    // product, where they name one.
                    foreach (var model in KeyboardLayouts.ModelsOf("Razer", lights.ProductId))
                        Assert.Equal(model.Recipe, lights.KeyboardRecipe);
                }
                if (lights.Plan == RazerLightPlan.PerLed)
                {
                    Assert.True(row.Family is RazerFamily.Extended or RazerFamily.MouseExtended or RazerFamily.Classic, id);
                    Assert.All(lights.Lights, l => Assert.Contains((byte)l.Address, row.Leds));
                }
                else
                {
                    if (row.Family is RazerFamily.Standard or RazerFamily.ExtendedMatrix)
                        Assert.Equal((row.Rows, row.Columns), (lights.Rows, lights.Columns));
                    else if (row.Family == RazerFamily.ExtendedFrame)
                        Assert.Equal(((byte)1, row.Columns), (lights.Rows, lights.Columns));
                    else
                        Assert.Equal(RazerFamily.Extended, row.Family);
                }
                var map = RazerLightTable.Map(lights.ProductId);
                Assert.Same(map, RazerLightTable.Map(lights.ProductId));
                Assert.Equal($"razer:1532:{id}", map.Product);
                Assert.True(map.PerLight, id);
            }
            Assert.Null(RazerLightTable.Map(0x007A));
        }

        /// <summary>No gaps: every product with a color path is painted
        /// light by light or has one light,
        /// which is one LED painted by LED, a frame of one cell, or the
        /// effect report alone.</summary>
        [Fact]
        public void EveryProductIsPaintedLightByLight_WaitsForItsLayout_OrHasOneLight()
        {
            foreach (var row in RazerDirectTable.Rows.Where(r => r.HasColorPath))
            {
                if (RazerLightTable.Find(row.ProductId) != null) continue;
                bool one = row.Family switch
                {
                    RazerFamily.Extended or RazerFamily.MouseExtended or RazerFamily.Classic => row.Leds.Length <= 1,
                    RazerFamily.Standard => row.Has(RazerRowFlags.HeldEffect) || row.Rows * row.Columns == 1,
                    _ => false,
                };
                Assert.True(one, $"{row.ProductId:X4} {row.Name} has no light map");
            }
            // Every Razer keyboard OpenRGB lays out has its layout here.
            foreach (var model in KeyboardLayouts.Models.Where(m => m.Family == "Razer"))
            {
                var lights = RazerLightTable.Find((ushort)model.ProductId);
                if (RazerDirectTable.Find((ushort)model.ProductId) is not { HasColorPath: true }) continue;
                Assert.True(lights?.KeyboardRecipe == model.Recipe, $"{model.ProductId:X4} {model.Model}");
            }
        }
    }
}
