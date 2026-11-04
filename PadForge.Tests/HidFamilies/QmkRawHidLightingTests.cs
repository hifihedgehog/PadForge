using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>QMK's raw HID collection with no list of IDs, against qmk_firmware's via.c,
    /// vial-qmk's and OpenRGB's QMKOpenRGBController (#557).</summary>
    public class QmkRawHidLightingTests
    {
        private static string Hex(byte[] bytes, int from, int count) => BitConverter.ToString(bytes, from, count).Replace('-', ' ');

        private static HidCollectionInfo Raw(ushort output = 33, ushort page = 0xFF60, ushort usage = 0x61) => new()
        {
            Path = "fake", VendorId = 0x1234, ProductId = 0x5678, UsagePage = page, Usage = usage, OutputReportLength = output, InputReportLength = output,
        };

        /// <summary>A firmware: the answer to each write, the write's own bytes where it
        /// changes nothing, or the id_unhandled it gives a command it lacks.</summary>
        private sealed class Board : IHidReportChannel
        {
            public int ReportLength = 33;
            public Func<byte[], byte[]> Reply = packet => packet;
            public readonly List<byte[]> Written = new();
            private byte[] _next;

            public string Path => "fake";
            public bool FeatureOnly => false;
            public int InputReportLength => ReportLength;
            public int OutputReportLength => ReportLength;
            public int FeatureReportLength => 0;
            public bool SetFeature(byte[] report) => false;
            public bool GetFeature(byte reportId, byte[] buffer) => false;

            public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs)
            {
                Written.Add((byte[])report.Clone());
                _next = Reply(report);
                return true;
            }

            public int ReadInput(byte[] buffer, int timeoutMs)
            {
                if (_next == null) return 0;
                Array.Copy(_next, buffer, Math.Min(_next.Length, buffer.Length));
                int length = _next.Length;
                _next = null;
                return length;
            }

            public void Dispose() { }
        }

        /// <summary>A VIA firmware of a protocol, with the channels given.</summary>
        private static Board Via(int protocol, params byte[] channels)
        {
            var board = new Board();
            board.Reply = packet =>
            {
                var answer = (byte[])packet.Clone();
                switch (packet[1])
                {
                    case 0x01:
                        answer[2] = (byte)(protocol >> 8);
                        answer[3] = (byte)protocol;
                        break;
                    case 0xFE:
                        // Not Vial: the command is unknown.
                        answer[1] = 0xFF;
                        break;
                    case 0x07:
                    case 0x08:
                        if (protocol >= 0x0C && !channels.Contains(packet[2]))
                        {
                            answer[1] = 0xFF;
                        }
                        else if (packet[1] == 0x08)
                        {
                            int at = protocol >= 0x0C ? 4 : 3;
                            byte id = packet[at - 1];
                            answer[at] = id switch { 0x01 or 0x80 => 200, 0x02 or 0x81 => 7, _ => 11 };
                            if (id is 0x04 or 0x83) answer[at + 1] = 22;
                        }
                        break;
                }
                return answer;
            };
            return board;
        }

        [Fact]
        public void TheFamily_TakesTheRawCollection_WhateverItsIds()
        {
            var family = new QmkRawHidLighting();
            var model = family.Match(Raw());
            Assert.Equal("QMK Keyboard", model.Name);
            Assert.Single(model.Map.Lights);
            Assert.Same(model, family.Match(Raw()));
            Assert.Null(family.Match(Raw(page: 0xFF00)));
            Assert.Null(family.Match(Raw(usage: 0x60)));
            Assert.Null(family.Match(Raw(output: 31)));
            Assert.Null(family.Match(null));
            Assert.Contains(QmkRawHidLighting.Instance, HidLightingFamilies.All);
        }

        /// <summary>It comes after every family that knows a device by name.</summary>
        [Fact]
        public void TheFamily_ComesAfterTheRowsThatNameADevice()
        {
            var families = HidLightingFamilies.All.ToList();
            Assert.True(families.IndexOf(QmkRawHidLighting.Instance) > families.IndexOf(PluginDeviceLighting.Instance));
            Assert.True(families.IndexOf(QmkRawHidLighting.Instance) > families.IndexOf(QmkLighting.Instance));
        }

        /// <summary>Protocol 12 on: channel 3, then the effect, color and brightness read back,
        /// the effect set to solid once, the color each picture, and all three back at the end.</summary>
        [Fact]
        public void AViaBoard_TakesItsColorThroughChannelValues()
        {
            var board = Via(0x000D, 0x03);
            var session = Assert.IsType<QmkRawHidLighting.ViaSession>(QmkRawHidLighting.Begin(board, _ => { }, "p"));
            Assert.Single(session.Map.Lights);
            var target = Assert.Single(session.Targets);
            Assert.Equal((200, 7, 11, 22), ((int)target.OldBrightness, (int)target.OldEffect, (int)target.OldHue, (int)target.OldSaturation));
            Assert.Equal("00 01", Hex(board.Written[0], 0, 2));
            Assert.Contains(board.Written, w => Hex(w, 0, 4) == "00 08 03 02");
            Assert.Contains(board.Written, w => Hex(w, 0, 4) == "00 08 03 04");
            Assert.Contains(board.Written, w => Hex(w, 0, 4) == "00 08 03 01");
            Assert.Empty(session.CrashFrames);

            board.Written.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(board, _ => { }, session.Map.Lights, new[] { 0xFF0000 }));
            Assert.Equal(new[] { "00 07 03 02 01", "00 07 03 04 00 FF", "00 07 03 01 FF" }, board.Written.Select(w => Hex(w, 0, w[2] == 3 && w[3] == 4 ? 6 : 5)));
            board.Written.Clear();
            session.Paint(board, _ => { }, session.Map.Lights, new[] { 0x00FF00 });
            Assert.Equal(2, board.Written.Count);
            Assert.Equal(3, session.CrashFrames.Length);
            board.Written.Clear();
            Assert.True(session.HandBack(board, _ => { }));
            Assert.Equal(new[] { "00 07 03 02 07", "00 07 03 04 0B 16", "00 07 03 01 C8" }, board.Written.Select(w => Hex(w, 0, w[3] == 4 ? 6 : 5)));
            Assert.Empty(session.CrashFrames);
        }

        /// <summary>A channel the firmware lacks answers FF (via.c:683-686). A board with rgblight
        /// alone takes channel 2, and one with neither stays dark.</summary>
        [Fact]
        public void TheChannels_GoByWhatTheFirmwareAnswers()
        {
            var rgblight = Assert.IsType<QmkRawHidLighting.ViaSession>(QmkRawHidLighting.Begin(Via(0x000D, 0x02), _ => { }, "p"));
            Assert.Equal((byte)2, Assert.Single(rgblight.Targets).Head[0]);
            var both = Assert.IsType<QmkRawHidLighting.ViaSession>(QmkRawHidLighting.Begin(Via(0x000C, 0x02, 0x03), _ => { }, "p"));
            Assert.Equal(2, both.Targets.Count);
            Assert.Null(QmkRawHidLighting.Begin(Via(0x000D), _ => { }, "p"));
        }

        /// <summary>Before protocol 12: no channels, the RGB matrix's IDs and rgblight's.</summary>
        [Fact]
        public void AnOlderProtocol_HasNoChannels()
        {
            var board = Via(0x0009);
            var session = Assert.IsType<QmkRawHidLighting.ViaSession>(QmkRawHidLighting.Begin(board, _ => { }, "p"));
            Assert.Equal(2, session.Targets.Count);
            board.Written.Clear();
            session.Paint(board, _ => { }, session.Map.Lights, new[] { 0xFF0000 });
            Assert.Contains(board.Written, w => Hex(w, 0, 4) == "00 07 02 01");
            Assert.Contains(board.Written, w => Hex(w, 0, 5) == "00 07 04 00 FF");
            Assert.Contains(board.Written, w => Hex(w, 0, 3) == "00 07 01");
            Assert.Contains(board.Written, w => Hex(w, 0, 4) == "00 07 81 01");
            Assert.Contains(board.Written, w => Hex(w, 0, 5) == "00 07 83 00 FF");
            Assert.Contains(board.Written, w => Hex(w, 0, 3) == "00 07 80");
        }

        [Fact]
        public void AFirmwareThatIsNoVia_StaysDark()
        {
            Assert.Null(QmkRawHidLighting.Begin(null, _ => { }, "p"));
            Assert.Null(QmkRawHidLighting.Begin(new Board { Reply = _ => null }, _ => { }, "p"));
            Assert.Null(QmkRawHidLighting.Begin(Via(0x0008), _ => { }, "p"));
            var other = new Board();
            other.Reply = packet =>
            {
                var answer = (byte[])packet.Clone();
                answer[1] = 0xFF;
                return answer;
            };
            Assert.Null(QmkRawHidLighting.Begin(other, _ => { }, "p"));
        }

        // ── The QMK-OpenRGB firmware ──

        private static Board OpenRgbBoard(byte revision, params byte[] modes)
        {
            var board = new Board { ReportLength = 65 };
            board.Reply = packet =>
            {
                var answer = new byte[65];
                answer[1] = packet[1];
                switch (packet[1])
                {
                    case 1:
                        answer[2] = revision;
                        break;
                    case 3:
                        answer[2] = 2;
                        answer[3] = 2;
                        break;
                    case 4:
                        answer[2] = 9;
                        answer[3] = 8;
                        answer[4] = 7;
                        answer[5] = 6;
                        answer[6] = 5;
                        break;
                    case 5:
                        // Two LEDs: A with the key flag and Left Shift's neighbor underglow.
                        answer[2] = 10;
                        answer[3] = 20;
                        answer[4] = 0x04;
                        answer[8] = 0x04;
                        answer[9] = 30;
                        answer[10] = 40;
                        answer[11] = 0x02;
                        break;
                    case 6:
                        if (revision == 0x09) answer[2] = (byte)(modes.Contains(packet[2]) ? 50 : 25);
                        else modes.CopyTo(answer, 2);
                        break;
                }
                return answer;
            };
            return board;
        }

        [Theory]
        [InlineData(0x09)]
        [InlineData(0x0B)]
        [InlineData(0x0C)]
        [InlineData(0x0D)]
        [InlineData(0x0E)]
        public void TheQmkOpenRgbFirmware_IsAskedWhatItHas(int revision)
        {
            var board = OpenRgbBoard((byte)revision, 2, 3, 1);
            var session = Assert.IsType<QmkRawHidLighting.OpenRgbProtocolSession>(QmkRawHidLighting.Begin(board, _ => { }, "p"));
            Assert.Equal("00 01", Hex(board.Written[0], 0, 2));
            Assert.Equal(65, board.Written[0].Length);
            Assert.True(session.Map.Lights.Count >= 1);
            var key = session.Map.Lights[0];
            Assert.Equal(0x04, key.Usage);
            Assert.Equal(10, key.X);
            Assert.Equal(20, key.Y);
            Assert.Empty(session.CrashFrames);
            board.Written.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(board, _ => { }, session.Map.Lights, session.Map.Lights.Select(_ => 0x112233).ToList()));
            // Direct: mode 1 on revision 9, the last enabled mode from B, no save.
            string direct = revision == 0x09 ? "00 07 00 FF FF 01 7F 00" : "00 07 00 FF FF 03 7F 00";
            Assert.Equal(direct, Hex(board.Written[0], 0, 8));
            Assert.Equal(0x09, board.Written[1][1]);
            // What it read comes back at the end.
            Assert.Equal("00 07 07 06 05 09 08 00", Hex(Assert.Single(session.CrashFrames), 0, 8));
            board.Written.Clear();
            Assert.True(session.HandBack(board, _ => { }));
            Assert.Equal("00 07 07 06 05 09 08 00", Hex(Assert.Single(board.Written), 0, 8));
            Assert.Empty(session.CrashFrames);
        }

        /// <summary>Revision D and E: four bytes per LED and fifteen to a frame. Earlier: the
        /// first LED, the count, three bytes each and twenty to a frame.</summary>
        [Fact]
        public void TheFrames_GoByRevision()
        {
            var lights = Enumerable.Range(0, 21).Select(i => new LightInfo(i, LightKind.Key, 0, LightKeySource.None)).ToList();
            var colors = Enumerable.Range(0, 21).Select(i => 0x010203 * (i + 1)).ToList();
            var newer = QmkRawHidLighting.OpenRgbProtocolSession.Frames(0x0D, lights, colors);
            Assert.Equal(2, newer.Count);
            Assert.Equal("00 09 0F 00 01 02 03 01 02 04 06", Hex(newer[0], 0, 11));
            Assert.Equal("00 09 06 0F", Hex(newer[1], 0, 4));
            var older = QmkRawHidLighting.OpenRgbProtocolSession.Frames(0x0B, lights, colors);
            Assert.Equal(2, older.Count);
            Assert.Equal("00 09 00 14 01 02 03 02 04 06", Hex(older[0], 0, 10));
            Assert.Equal("00 09 14 01", Hex(older[1], 0, 4));
            Assert.Equal(65, older[1].Length);
        }

        [Fact]
        public void AFirmwareWithNoDirectMode_OrAnUnknownRevision_StaysDark()
        {
            Assert.Null(QmkRawHidLighting.Begin(OpenRgbBoard(0x0D, 2, 3), _ => { }, "p"));
            Assert.Null(QmkRawHidLighting.Begin(OpenRgbBoard(0x09, 2, 3), _ => { }, "p"));
            Assert.Null(QmkRawHidLighting.Begin(OpenRgbBoard(0x0A, 1), _ => { }, "p"));
            Assert.Null(QmkRawHidLighting.Begin(OpenRgbBoard(0x00, 1), _ => { }, "p"));
        }

        // ── The rows that fall back ──

        /// <summary>A keyboard SignalRGB's plugins name, on the firmware it shipped with: its
        /// row's firmware does not answer, and VIA's does.</summary>
        [Fact]
        public void ARowOfThePlugins_FallsBackToVia()
        {
            var row = PluginDeviceLighting.Devices.First(d => d.Otherwise != null);
            Assert.Equal(QmkLighting.Product(row.VendorId, row.ProductId), QmkLighting.Product(row.VendorId, row.ProductId));
            var board = Via(0x000D, 0x03);
            var model = PluginDeviceLighting.Instance.Match(new HidCollectionInfo
            {
                Path = @"\\?\hid#vid_0000&pid_0000&mi_01#fake", VendorId = row.VendorId, ProductId = row.ProductId, InterfaceNumber = 1,
                UsagePage = 0xFF60, Usage = 0x61, OutputReportLength = 33,
            });
            Assert.NotNull(model);
            Assert.IsType<QmkRawHidLighting.ViaSession>(model.Begin(board, _ => { }));
        }
    }
}
