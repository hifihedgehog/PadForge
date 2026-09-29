using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Engine.Common.BlissBox;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Bliss-Box API's reports and parsers (issue #469), built byte for
    /// byte against the API Tool (BBAPI.cs, main-original.c), DeviceBuddy
    /// (bliss_box_api.js, automation.js) and the two firmware images' rules.
    /// </summary>
    public class BlissBoxProtocolTests
    {
        [Fact]
        public void OnlyThePlayerPortsMatch_NeverAnUpdater()
        {
            for (ushort pid = 0x0D04; pid <= 0x0D07; pid++)
                Assert.True(BlissBoxProtocol.IsPort(0x16D0, pid));
            Assert.False(BlissBoxProtocol.IsPort(0x16D0, 0x0D03));
            Assert.False(BlissBoxProtocol.IsPort(0x16D0, 0x0D08));
            Assert.False(BlissBoxProtocol.IsPort(0x16D0, 0x0A5F)); // 3.x bootloader
            Assert.False(BlissBoxProtocol.IsPort(0x16D0, 0x04FB)); // GPA bootloader
            Assert.False(BlissBoxProtocol.IsPort(0x16D0, 0x0A60)); // 1.x
            Assert.False(BlissBoxProtocol.IsPort(0x16D0, 0x0A61)); // 4-Play Fix
            Assert.False(BlissBoxProtocol.IsPort(0x16D1, 0x0D04));
            Assert.Equal(1, BlissBoxProtocol.PlayerOf(0x0D04));
            Assert.Equal(4, BlissBoxProtocol.PlayerOf(0x0D07));
            Assert.Equal(7, BlissBoxProtocol.PlayerByte(4));
        }

        [Fact]
        public void RumbleIsBbapiSendRumble_TypeOneWithLoopOff()
        {
            // sendRumble(type, state, amount, loop): [18][type][0][0][state][amount][loop].
            Assert.Equal(new byte[] { 18, 4, 0, 0, 1, 200, 0xFF, 0, 0 }, BlissBoxProtocol.Rumble(true, 200));
            Assert.Equal(new byte[] { 18, 5, 0, 0, 1, 1, 0xFF, 0, 0 }, BlissBoxProtocol.Rumble(false, 1));
            Assert.Equal(new byte[] { 18, 4, 0, 0, 0, 0, 0, 0, 0 }, BlissBoxProtocol.Rumble(true, 0));
            Assert.Equal(BlissBoxProtocol.CommandReportLength, BlissBoxProtocol.Rumble(false, 0).Length);
        }

        [Fact]
        public void PlayerIsBbapiSetPlayer_WithResetAsTheWizardSendsIt()
        {
            // setPlayer(player, reset): player + 3 at [4], reset at [5].
            Assert.Equal(new byte[] { 18, 8, 0, 0, 6, 1, 0, 0, 0 }, BlissBoxProtocol.SetPlayer(3));
            Assert.Throws<ArgumentOutOfRangeException>(() => BlissBoxProtocol.SetPlayer(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => BlissBoxProtocol.SetPlayer(5));
        }

        [Fact]
        public void ScreenPutsThePictureWhereBbapiAndDeviceBuddyPutIt()
        {
            var wire = Enumerable.Range(0, 192).Select(i => (byte)i).ToArray();
            var report = BlissBoxProtocol.Screen(wire);
            Assert.Equal(BlissBoxProtocol.ScreenReportLength, report.Length);
            // BBAPI.cs sendLCD: [20][0x24][0][0] then the 192 bytes.
            Assert.Equal(new byte[] { 20, 0x24, 0, 0 }, report[..4]);
            Assert.Equal(wire, report[4..196]);
            // DeviceBuddy's API_WRITE fills its buffer from index 2 with the
            // parameters, whose first is a header, so the picture lands at
            // index 3 after the report ID: byte 4 of the report, as here.
            var parms = new byte[193];
            Array.Copy(wire, 0, parms, 1, 192);
            var data = new byte[200];
            data[0] = parms[0];
            for (int i = 0; i < parms.Length && i < 192; i++) data[2 + i] = parms[i];
            Assert.Equal(wire[..191], data[3..194]);
        }

        [Fact]
        public void InfoParsesTheFiveBytesAndChecksThePlayer()
        {
            var info = BlissBoxProtocol.ParseInfo(new byte[] { 121, 0x10, 4, 7, 86 }, 4);
            Assert.NotNull(info);
            Assert.Equal(121, info.Type);
            Assert.Equal(0x10, info.Flags);
            Assert.Equal(4, info.Major);
            Assert.Equal(86, info.Minor);
            Assert.False(info.Searching);
            Assert.True(info.IsAdvanced);
            Assert.Null(BlissBoxProtocol.ParseInfo(new byte[] { 121, 0x10, 4, 7, 86 }, 3));
            Assert.Null(BlissBoxProtocol.ParseInfo(new byte[] { 121, 0x10, 4, 7 }, 4));
            Assert.True(BlissBoxProtocol.ParseInfo(new byte[] { 0, 0x01, 3, 4, 24 }, 1).Searching);
        }

        [Fact]
        public void PressureAndScreenReadsCheckThePlayerByte()
        {
            var data = new byte[13];
            data[0] = 5; // player 2
            for (int i = 1; i < 13; i++) data[i] = (byte)(i * 10);
            var pressure = new byte[12];
            Assert.True(BlissBoxProtocol.TryParsePressure(data, 2, pressure));
            Assert.Equal(data[1..], pressure);
            Assert.False(BlissBoxProtocol.TryParsePressure(data, 1, pressure));

            var screen = new byte[193];
            screen[0] = 4;
            screen[192] = 0xAB;
            var wire = new byte[192];
            Assert.True(BlissBoxProtocol.TryParseScreen(screen, 1, wire));
            Assert.Equal(0xAB, wire[191]);
            Assert.False(BlissBoxProtocol.TryParseScreen(screen, 2, wire));
        }

        [Fact]
        public void NativeRepliesFollowBbapiGetData()
        {
            // [player + 3][use][size][bytes]
            var state = BlissBoxProtocol.ParseNativeReply(new byte[] { 4, 1, 3, 5, 0, 1, 99 }, 1, 1, out var reply);
            Assert.Equal(BlissBoxProtocol.NativeReplyState.Ready, state);
            Assert.Equal(new byte[] { 5, 0, 1 }, reply);
            Assert.Equal(BlissBoxProtocol.NativeReplyState.NoReply,
                BlissBoxProtocol.ParseNativeReply(new byte[] { 4, 0, 0 }, 1, 1, out _));
            Assert.Equal(BlissBoxProtocol.NativeReplyState.NotReady,
                BlissBoxProtocol.ParseNativeReply(new byte[] { 5, 1, 3, 1, 2, 3 }, 1, 1, out _));
            Assert.Equal(BlissBoxProtocol.NativeReplyState.NotReady,
                BlissBoxProtocol.ParseNativeReply(new byte[] { 4, 1, 0 }, 1, 1, out _));
        }

        [Fact]
        public void ShortMessagesAreTheHeaderAlone()
        {
            var reports = BlissBoxProtocol.NativeReports(new byte[] { 0x00 });
            var header = Assert.Single(reports);
            // [18][0x25][0][size hi][size lo][use][m0][m1][0]
            Assert.Equal(new byte[] { 18, 0x25, 0, 0, 1, 1, 0x00, 0, 0 }, header);
            Assert.Single(BlissBoxProtocol.NativeReports(new byte[] { 7, 8 }));
        }

        /// <summary>The 3.0 firmware places a final chunk five bytes past the
        /// last positioned one, from RAM a header never resets (handler at
        /// 0x0902). GPA 4.86 places a lone final chunk at position 2 (0x2D9F).
        /// Every message must come through both whole, including right after
        /// another message left the 3.0 position somewhere else.</summary>
        [Fact]
        public void EveryLengthAssemblesWholeUnderBothFirmwareRules()
        {
            var rng = new Random(469);
            var threeZero = new FirmwareModel(gpa: false);
            var gpa = new FirmwareModel(gpa: true);
            for (int length = 1; length <= 255; length++)
            {
                var message = new byte[length];
                rng.NextBytes(message);
                var reports = BlissBoxProtocol.NativeReports(message);
                Assert.Equal(message, threeZero.Feed(reports));
                Assert.Equal(message, gpa.Feed(reports));
            }
        }

        /// <summary>BBAPI.cs sendData's chunk count, transcribed, leaves the
        /// last byte of a 28-byte message off the wire. The count here does
        /// not.</summary>
        [Fact]
        public void TheChunkCountCoversWhatBbapiDrops()
        {
            var message = Enumerable.Range(1, 28).Select(i => (byte)i).ToArray();
            var model = new FirmwareModel(gpa: true);
            Assert.NotEqual(message, model.Feed(BbapiSendData(message)));
            Assert.Equal(message, model.Feed(BlissBoxProtocol.NativeReports(message)));
        }

        private static List<byte[]> BbapiSendData(byte[] message)
        {
            var reports = new List<byte[]>();
            var header = new byte[9];
            header[0] = 18; header[1] = 0x25; header[3] = 0; header[4] = (byte)message.Length; header[5] = 1;
            if (message.Length > 0) header[6] = message[0];
            if (message.Length > 1) header[7] = message[1];
            reports.Add(header);
            if (message.Length > 2)
            {
                int c = 2, pos = 2, size = message.Length;
                size -= 2; size += 1; size /= 5;
                if (size == 0) size = 1;
                if (size % 5 > 0) size++;
                for (int s = 0; s < size; s++)
                {
                    var chunk = new byte[9];
                    chunk[0] = 18; chunk[1] = 0x25;
                    if (s == size - 1) chunk[2] = 0xFF;
                    else { chunk[2] = (byte)pos; pos += 5; }
                    for (int k = 0; k < 5; k++, c++) if (c < message.Length) chunk[3 + k] = message[c];
                    reports.Add(chunk);
                }
            }
            return reports;
        }

        /// <summary>The native channel's assembly as the firmware listings
        /// show it.</summary>
        private sealed class FirmwareModel
        {
            private readonly bool _gpa;
            private int _lastPosition = 40; // stale RAM from an earlier message
            private byte[] _buffer;
            private int _size;
            private bool _positioned;

            public FirmwareModel(bool gpa) => _gpa = gpa;

            public byte[] Feed(IEnumerable<byte[]> reports)
            {
                byte[] done = null;
                foreach (var r in reports)
                {
                    Assert.Equal(18, r[0]);
                    Assert.Equal(0x25, r[1]);
                    if (r[2] == 0)
                    {
                        _size = (r[3] << 8) | r[4];
                        _buffer = new byte[Math.Max(_size, 2) + 300];
                        _buffer[0] = r[6];
                        _buffer[1] = r[7];
                        _positioned = false;
                        if (_size <= 2) done = _buffer[.._size];
                        continue;
                    }
                    int at;
                    if (r[2] == 0xFF)
                        at = _gpa && !_positioned ? 2 : _lastPosition + 5;
                    else
                    {
                        at = r[2];
                        _lastPosition = at;
                        _positioned = true;
                    }
                    Array.Copy(r, 3, _buffer, at, 5);
                    if (r[2] == 0xFF) done = _buffer[.._size];
                }
                return done;
            }
        }

        // ── The VMU picture ──

        /// <summary>DeviceBuddy's README example: 193 values, a header then
        /// the 192 wire bytes, read the way DeviceBuddy draws report 24.</summary>
        private static readonly byte[] DeviceBuddyExample =
        {
            0,240,1,227,255,129,254,240,3,231,207,199,254,60,15,15,1,231,14,30,30,14,0,231,14,7,120,30,0,247,158,7,240,24,0,113,156,15,240,24,0,113,252,15,240,24,0,113,252,15,224,24,0,113,252,14,248,30,0,115,220,30,30,31,0,243,158,60,15,15,129,195,142,240,3,231,231,195,238,240,1,225,255,129,255,0,0,0,0,0,0,0,0,0,0,0,0,63,131,241,247,241,254,121,199,185,227,243,254,97,230,57,224,227,14,97,230,56,240,227,14,97,198,32,240,243,158,96,6,0,248,115,156,120,7,128,120,115,252,62,3,224,120,112,252,15,129,248,248,115,252,3,192,124,240,115,220,0,192,28,240,115,158,0,192,28,240,115,142,48,195,28,0,123,238,113,199,60,0,57,254,127,135,249,240,60,255,63,3,241,240,28,127,
        };

        private static readonly string[] BlissBoxArt =
        {
            "#######...###.......#####...######......######..",
            "########..####......#####..########....########.",
            ".########..###............####..###...###...###.",
            ".###.#####.####...........###...##....##....##..",
            ".###...###..###.....####..###.........##........",
            ".####..###..###.....####..###.........##........",
            "..###.####..###.....####..#####.......####......",
            "..########..###....#####...######......#####....",
            "..######....###....####......#####.......#####..",
            "..########..###....####........####........####.",
            "..###..###..###....#####.........##..........##.",
            ".####..###..####....####.....#...##...###....##.",
            ".###....##...###....####...###...##..####....##.",
            ".###....##...###.....####..###...##..####....##.",
            ".#########..######...####..###.####...###..####.",
            ".########...#######.#####...######.....#######..",
            "................................................",
            "................................................",
            "#########......##########....####...........####",
            ".###.#####....#####..######..#####..........####",
            ".###...###....###......#####....####......####..",
            ".####..###..####........#####....####....####...",
            "..###.####..###..........####......#####.###....",
            "..#######...###............##........#######....",
            "..#######...###............##.......########....",
            "..#######...###............##.......########....",
            "..###..##...###............##.......#######.....",
            ".####..####.####.........####......####.###.....",
            ".###....###..###.........###.....####....####...",
            ".###....###..####.......####....####......####..",
            ".##########...######..#####..#####..........####",
            ".########......###########...####...........####",
        };

        [Fact]
        public void TheDeviceBuddyExampleReadsBlissBoxUpright()
        {
            Assert.Equal(193, DeviceBuddyExample.Length);
            var image = BlissBoxScreen.FromWire(DeviceBuddyExample.AsSpan(1, 192));
            for (int y = 0; y < BlissBoxScreen.Height; y++)
                for (int x = 0; x < BlissBoxScreen.Width; x++)
                    Assert.True(BlissBoxArt[y][x] == '#' == BlissBoxScreen.IsDark(image, x, y), $"pixel ({x}, {y})");
        }

        [Fact]
        public void TheRotationIsItsOwnInverse_AndMatchesDlcd()
        {
            var rng = new Random(460);
            var image = new byte[192];
            rng.NextBytes(image);
            var wire = BlissBoxScreen.ToWire(image);
            Assert.Equal(image, BlissBoxScreen.FromWire(wire));
            // dLCD.cs writeToLCD: reverse each byte's bits, then the order.
            for (int k = 0; k < 192; k++)
                Assert.Equal(BlissBoxScreen.ReverseBits(image[k]), wire[191 - k]);
            Assert.Equal(0x01, BlissBoxScreen.ReverseBits(0x80));
            Assert.Equal(0xB1, BlissBoxScreen.ReverseBits(0x8D));
        }

        [Fact]
        public void AnLcdFileGivesItsFirstFrameInImageOrder()
        {
            var file = new byte[BlissBoxScreen.LcdPixelOffset + 48 * 32 + 10];
            file[BlissBoxScreen.LcdPixelOffset + 0] = BlissBoxScreen.LcdDark;           // (0, 0)
            file[BlissBoxScreen.LcdPixelOffset + 47] = BlissBoxScreen.LcdDark;          // (47, 0)
            file[BlissBoxScreen.LcdPixelOffset + 48 * 31 + 5] = BlissBoxScreen.LcdDark; // (5, 31)
            file[BlissBoxScreen.LcdPixelOffset + 1] = 0x07;                             // not 0x08: light
            var image = BlissBoxScreen.FromLcd(file);
            Assert.True(BlissBoxScreen.IsDark(image, 0, 0));
            Assert.True(BlissBoxScreen.IsDark(image, 47, 0));
            Assert.True(BlissBoxScreen.IsDark(image, 5, 31));
            Assert.False(BlissBoxScreen.IsDark(image, 1, 0));
            Assert.Equal(3, Enumerable.Range(0, 48 * 32).Count(p => BlissBoxScreen.IsDark(image, p % 48, p / 48)));
            Assert.Null(BlissBoxScreen.FromLcd(new byte[100]));
        }

        [Fact]
        public void AVmsIconIsCenteredWithEightBlankPixelsEitherSide()
        {
            var file = new byte[0x80 + 128 + 64];
            BitConverter.GetBytes(0x80u).CopyTo(file, 16);
            for (int i = 0; i < 128; i++) file[0x80 + i] = 0xFF; // every icon pixel dark
            var image = BlissBoxScreen.FromVms(file);
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 48; x++)
                    Assert.Equal(x >= 8 && x < 40, BlissBoxScreen.IsDark(image, x, y));

            var bad = new byte[200];
            BitConverter.GetBytes(0u).CopyTo(bad, 16);
            Assert.Null(BlissBoxScreen.FromVms(bad));
            BitConverter.GetBytes(150u).CopyTo(bad, 16); // icon would run past the end
            Assert.Null(BlissBoxScreen.FromVms(bad));
        }

        // ── The Controller Pak ──

        /// <summary>libdragon's joybus_accessory_calculate_addr_checksum
        /// table.</summary>
        private static int LibdragonAddressChecksum(int address)
        {
            int[] xorTable = { 0x00, 0x00, 0x00, 0x00, 0x00, 0x15, 0x1F, 0x0B, 0x16, 0x19, 0x07, 0x0E, 0x1C, 0x0D, 0x1A, 0x01 };
            int checksum = 0;
            for (int i = 15; i >= 5; i--)
                if (((address >> i) & 1) != 0) checksum ^= xorTable[i];
            return checksum & 0x1F;
        }

        /// <summary>libdragon's joybus_accessory_calculate_data_crc.</summary>
        private static byte LibdragonDataCrc(byte[] data)
        {
            int crc = 0;
            foreach (byte b in data)
            {
                int x = crc ^ b;
                crc = 0;
                if ((x & 0x80) != 0) crc ^= 0x89;
                if ((x & 0x40) != 0) crc ^= 0x86;
                if ((x & 0x20) != 0) crc ^= 0x43;
                if ((x & 0x10) != 0) crc ^= 0xE3;
                if ((x & 0x08) != 0) crc ^= 0xB3;
                if ((x & 0x04) != 0) crc ^= 0x9B;
                if ((x & 0x02) != 0) crc ^= 0x8F;
                if ((x & 0x01) != 0) crc ^= 0x85;
            }
            return (byte)crc;
        }

        [Fact]
        public void TheAddressChecksumMatchesLibdragonForEveryBlock()
        {
            for (int block = 0; block < BlissBoxControllerPak.Blocks; block++)
            {
                int address = block * 32;
                Assert.Equal(LibdragonAddressChecksum(address), BlissBoxControllerPak.AddressChecksum(address));
                Assert.Equal(address | LibdragonAddressChecksum(address), BlissBoxControllerPak.AddressField(block));
            }
            // The Rumble Pak's probe page, above the pak's own blocks.
            Assert.Equal(LibdragonAddressChecksum(0x8000), BlissBoxControllerPak.AddressChecksum(0x8000));
        }

        [Fact]
        public void TheDataCrcMatchesLibdragon_AndARumblePakBlockIsB8()
        {
            var rng = new Random(64);
            for (int i = 0; i < 200; i++)
            {
                var data = new byte[32];
                rng.NextBytes(data);
                Assert.Equal(LibdragonDataCrc(data), BlissBoxControllerPak.DataCrc(data));
            }
            Assert.Equal(0xB8, BlissBoxControllerPak.DataCrc(Enumerable.Repeat((byte)0x80, 32).ToArray()));
        }

        [Fact]
        public void ReadAndWriteMessagesCarryTheCheckedAddress()
        {
            ushort field = BlissBoxControllerPak.AddressField(3);
            Assert.Equal(new byte[] { 0x02, (byte)(field >> 8), (byte)field }, BlissBoxControllerPak.ReadMessage(3));
            var data = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            var write = BlissBoxControllerPak.WriteMessage(3, data);
            Assert.Equal(35, write.Length);
            Assert.Equal(0x03, write[0]);
            Assert.Equal(data, write[3..]);
            Assert.Equal(new byte[] { 0x00 }, BlissBoxControllerPak.StatusMessage());
        }

        [Fact]
        public void BlockRepliesTellOkBadAndMissingApart()
        {
            var data = Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray();
            byte crc = BlissBoxControllerPak.DataCrc(data);
            var copy = new byte[32];
            Assert.Equal(BlissBoxControllerPak.BlockResult.Ok, BlissBoxControllerPak.ParseRead(data.Append(crc).ToArray(), copy));
            Assert.Equal(data, copy);
            Assert.Equal(BlissBoxControllerPak.BlockResult.NoPak, BlissBoxControllerPak.ParseRead(data.Append((byte)~crc).ToArray(), copy));
            Assert.Equal(BlissBoxControllerPak.BlockResult.BadCrc, BlissBoxControllerPak.ParseRead(data.Append((byte)(crc ^ 1)).ToArray(), copy));
            Assert.Equal(BlissBoxControllerPak.BlockResult.Short, BlissBoxControllerPak.ParseRead(data, copy));
            Assert.Equal(BlissBoxControllerPak.BlockResult.Ok, BlissBoxControllerPak.ParseWrite(new[] { crc }, data));
            Assert.Equal(BlissBoxControllerPak.BlockResult.NoPak, BlissBoxControllerPak.ParseWrite(new[] { (byte)~crc }, data));
        }

        [Fact]
        public void APakIsPresentAtStatusOneOrThree()
        {
            Assert.True(BlissBoxControllerPak.IsPakPresent(new byte[] { 5, 0, 1 }));
            Assert.True(BlissBoxControllerPak.IsPakPresent(new byte[] { 5, 0, 3 }));
            Assert.False(BlissBoxControllerPak.IsPakPresent(new byte[] { 5, 0, 2 }));
            Assert.False(BlissBoxControllerPak.IsPakPresent(new byte[] { 5, 0, 0 }));
            Assert.False(BlissBoxControllerPak.IsPakPresent(new byte[] { 5, 0 }));
        }

        // ── The PlayStation poll ──

        [Fact]
        public void ThePollIsTheReadCommand_AndArrowsDecodeActiveLow()
        {
            Assert.Equal(new byte[] { 0x01, 0x42, 0x00, 0x00, 0x00 }, BlissBoxPsx.PollMessage());
            // Up (bit 4) and left (bit 7) held, the rest released.
            byte first = unchecked((byte)~(0x10 | 0x80));
            Assert.True(BlissBoxPsx.TryDecodeArrows(new byte[] { 0xFF, 0x41, 0x5A, first, 0xFF }, out byte arrows));
            Assert.Equal(0x01 | 0x04, arrows);
            // Without the leading byte, the same.
            Assert.True(BlissBoxPsx.TryDecodeArrows(new byte[] { 0x41, 0x5A, first, 0xFF }, out arrows));
            Assert.Equal(0x01 | 0x04, arrows);
            // All four at once, a jump.
            Assert.True(BlissBoxPsx.TryDecodeArrows(new byte[] { 0xFF, 0x41, 0x5A, 0x0F, 0xFF }, out arrows));
            Assert.Equal(0x0F, arrows);
            Assert.False(BlissBoxPsx.TryDecodeArrows(new byte[] { 0xFF, 0x73, 0x5A, 0x0F, 0xFF }, out _));
            Assert.False(BlissBoxPsx.TryDecodeArrows(new byte[] { 0xFF, 0x41 }, out _));
        }
    }
}
