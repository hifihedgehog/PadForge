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

        /// <summary>A VIA firmware of a protocol. It sends back the packet it got, with a value
        /// written in where it has that value: from protocol 11 on the channels given, and before
        /// that the value IDs given.</summary>
        private static Board Via(int protocol, params byte[] has) => ViaBoard(protocol, has, vial: false);

        private static Board ViaBoard(int protocol, byte[] has, bool vial)
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
                        // Vial: its protocol, low byte first. Any other: the command is unknown.
                        answer[1] = vial ? (byte)0x06 : (byte)0xFF;
                        break;
                    case 0x07:
                    case 0x08:
                        bool channels = protocol >= 0x0B;
                        if (channels && !has.Contains(packet[2]))
                        {
                            answer[1] = 0xFF;
                            break;
                        }
                        int at = channels ? 4 : 3;
                        byte id = packet[at - 1];
                        bool known = !channels ? has.Contains(id)
                            : packet[2] == 0 ? id is 0x09 or 0x0A or 0x0C
                            : id is 0x01 or 0x02 || (id == 0x04 && packet[2] != 0x01);
                        if (packet[1] == 0x08 && known)
                        {
                            answer[at] = id switch { 0x01 or 0x80 or 0x09 => 200, 0x02 or 0x81 or 0x0A => 7, _ => 11 };
                            if (id is 0x04 or 0x83 or 0x0C) answer[at + 1] = 22;
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

        /// <summary>Channel 3, then the effect, color and brightness read back, the effect set to
        /// solid once, the color each picture, and all three back at the end.</summary>
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

        /// <summary>A channel the firmware lacks answers FF (via.c:220-225). A board with rgblight
        /// alone takes channel 2, and one with none of them stays dark.</summary>
        [Fact]
        public void TheChannels_GoByWhatTheFirmwareAnswers()
        {
            var rgblight = Assert.IsType<QmkRawHidLighting.ViaSession>(QmkRawHidLighting.Begin(Via(0x000D, 0x02), _ => { }, "p"));
            Assert.Equal((byte)2, Assert.Single(rgblight.Targets).Head[0]);
            var both = Assert.IsType<QmkRawHidLighting.ViaSession>(QmkRawHidLighting.Begin(Via(0x000C, 0x02, 0x03), _ => { }, "p"));
            Assert.Equal(2, both.Targets.Count);
            Assert.Null(QmkRawHidLighting.Begin(Via(0x000D), _ => { }, "p"));
        }

        /// <summary>QMK 0.19.0's via.h is the first with channels, at protocol 11. 0.18.0's is 10.</summary>
        [Fact]
        public void TheChannels_BeginAtProtocol11()
        {
            var eleven = Assert.IsType<QmkRawHidLighting.ViaSession>(QmkRawHidLighting.Begin(Via(0x000B, 0x03), _ => { }, "p"));
            Assert.Equal((byte)3, Assert.Single(eleven.Targets).Head[0]);
            var ten = Assert.IsType<QmkRawHidLighting.ViaSession>(QmkRawHidLighting.Begin(Via(0x000A, 0x80, 0x81, 0x83), _ => { }, "p"));
            Assert.Empty(Assert.Single(ten.Targets).Head);
        }

        /// <summary>Before protocol 11: rgblight's values, which QMK's RGB matrix shares. A firmware
        /// that is not Vial is never asked about 1, 2 and 4, which on Wilba's boards are layout
        /// switches (wt_rgb_backlight_api.h).</summary>
        [Fact]
        public void AnOlderProtocol_HasNoChannels()
        {
            var board = Via(0x0009, 0x80, 0x81, 0x83);
            var session = Assert.IsType<QmkRawHidLighting.ViaSession>(QmkRawHidLighting.Begin(board, _ => { }, "p"));
            var target = Assert.Single(session.Targets);
            Assert.Equal((200, 7, 11, 22), ((int)target.OldBrightness, (int)target.OldEffect, (int)target.OldHue, (int)target.OldSaturation));
            Assert.DoesNotContain(board.Written, w => w[1] == 0x08 && w[2] is 0x01 or 0x02 or 0x04);
            board.Written.Clear();
            session.Paint(board, _ => { }, session.Map.Lights, new[] { 0xFF0000 });
            Assert.Equal(new[] { "00 07 81 01", "00 07 83 00 FF", "00 07 80 FF" }, board.Written.Select(w => Hex(w, 0, w[2] == 0x83 ? 5 : 4)));
            board.Written.Clear();
            session.HandBack(board, _ => { });
            Assert.Equal(new[] { "00 07 81 07", "00 07 83 0B 16", "00 07 80 C8" }, board.Written.Select(w => Hex(w, 0, w[2] == 0x83 ? 5 : 4)));
            var other = Via(0x0009, 0x01, 0x02, 0x04);
            Assert.Null(QmkRawHidLighting.Begin(other, _ => { }, "p"));
            Assert.DoesNotContain(other.Written, w => w[1] == 0x07);
        }

        /// <summary>Vial stays at protocol 9 with its RGB matrix on 1, 2 and 4 (vial-qmk via.h:104-114),
        /// and its rgblight on 80, 81 and 83.</summary>
        [Fact]
        public void AVialBoard_HasItsMatrixAtItsOwnValues()
        {
            var board = ViaBoard(0x0009, new byte[] { 0x01, 0x02, 0x04 }, vial: true);
            var session = Assert.IsType<QmkRawHidLighting.ViaSession>(QmkRawHidLighting.Begin(board, _ => { }, "p"));
            var target = Assert.Single(session.Targets);
            Assert.Equal((byte)0x01, target.Brightness);
            Assert.Equal((byte)0x04, target.Color);
            var both = ViaBoard(0x0009, new byte[] { 0x01, 0x02, 0x04, 0x80, 0x81, 0x83 }, vial: true);
            Assert.Equal(2, Assert.IsType<QmkRawHidLighting.ViaSession>(QmkRawHidLighting.Begin(both, _ => { }, "p")).Targets.Count);
        }

        /// <summary>A firmware that sends back what it got, or says nothing to Vial's question, has
        /// none of these values, and nothing is written to it.</summary>
        [Fact]
        public void AValueTheFirmwareLacks_IsNeverWritten()
        {
            var echo = Via(0x0009);
            Assert.Null(QmkRawHidLighting.Begin(echo, _ => { }, "p"));
            Assert.DoesNotContain(echo.Written, w => w[1] == 0x07);
            var channels = new Board();
            channels.Reply = packet =>
            {
                var answer = (byte[])packet.Clone();
                if (packet[1] == 0x01) answer[3] = 0x0D;
                return answer;
            };
            Assert.Null(QmkRawHidLighting.Begin(channels, _ => { }, "qmk:5241:065B"));
            Assert.DoesNotContain(channels.Written, w => w[1] == 0x07);
            // A firmware that sends Vial's question back: not Vial.
            var back = Via(0x0009, 0x01, 0x02, 0x04);
            var through = back.Reply;
            back.Reply = packet => packet[1] == 0xFE ? (byte[])packet.Clone() : through(packet);
            Assert.Null(QmkRawHidLighting.Begin(back, _ => { }, "p"));
            // A firmware that answers the version and nothing else: not Vial.
            var quiet = Via(0x0009, 0x01, 0x02, 0x04);
            var inner = quiet.Reply;
            quiet.Reply = packet => packet[1] == 0xFE ? null : inner(packet);
            Assert.Null(QmkRawHidLighting.Begin(quiet, _ => { }, "p"));
        }

        /// <summary>A firmware that answers its version and then goes silent, or answers FF over
        /// a blank report, has no values either.</summary>
        [Fact]
        public void AFirmwareWithNoAnswerToAValue_StaysDark()
        {
            var silent = new Board();
            silent.Reply = packet =>
            {
                if (packet[1] != 0x01) return null;
                var answer = (byte[])packet.Clone();
                answer[3] = 0x0D;
                return answer;
            };
            Assert.Null(QmkRawHidLighting.Begin(silent, _ => { }, "p"));
            Assert.DoesNotContain(silent.Written, w => w[1] == 0x07);
            var blank = new Board();
            blank.Reply = packet =>
            {
                var answer = new byte[packet.Length];
                answer[1] = packet[1] == 0x01 ? (byte)0x01 : (byte)0xFF;
                if (packet[1] == 0x01) answer[3] = 0x0D;
                return answer;
            };
            Assert.Null(QmkRawHidLighting.Begin(blank, _ => { }, "p"));
            Assert.DoesNotContain(blank.Written, w => w[1] == 0x07);
        }

        /// <summary>A value that holds the byte asked with is told by the second byte.</summary>
        [Fact]
        public void AValueThatHoldsTheProbe_IsStillSeen()
        {
            var board = Via(0x000D, 0x03);
            var inner = board.Reply;
            board.Reply = packet =>
            {
                var answer = inner(packet);
                if (packet[1] == 0x08 && packet[3] == 0x01) answer[4] = 0x5A;
                return answer;
            };
            var session = Assert.IsType<QmkRawHidLighting.ViaSession>(QmkRawHidLighting.Begin(board, _ => { }, "p"));
            Assert.Equal((byte)0x5A, Assert.Single(session.Targets).OldBrightness);
            var asked = board.Written.Where(w => w[1] == 0x08 && w[2] == 0x03 && w[3] == 0x01).Select(w => w[4]).ToList();
            Assert.Equal(new byte[] { 0x5A, 0xA5 }, asked);
        }

        /// <summary>A one-color backlight, channel 1 or 9 and 0A: no color, and the effect is whether
        /// it breathes (via.c:523-562).</summary>
        [Fact]
        public void AOneColorBacklight_TakesItsBrightnessAlone()
        {
            var board = Via(0x000D, 0x01);
            var session = Assert.IsType<QmkRawHidLighting.ViaSession>(QmkRawHidLighting.Begin(board, _ => { }, "p"));
            var target = Assert.Single(session.Targets);
            Assert.Null(target.Color);
            board.Written.Clear();
            session.Paint(board, _ => { }, session.Map.Lights, new[] { 0x4080C0 });
            Assert.Equal(new[] { "00 07 01 02 00", "00 07 01 01 C0" }, board.Written.Select(w => Hex(w, 0, 5)));
            board.Written.Clear();
            session.Paint(board, _ => { }, session.Map.Lights, new[] { 0x000020 });
            Assert.Equal("00 07 01 01 20", Hex(Assert.Single(board.Written), 0, 5));
            board.Written.Clear();
            session.HandBack(board, _ => { });
            Assert.Equal(new[] { "00 07 01 02 07", "00 07 01 01 C8" }, board.Written.Select(w => Hex(w, 0, 5)));

            var old = Via(0x0009, 0x09, 0x0A);
            var older = Assert.IsType<QmkRawHidLighting.ViaSession>(QmkRawHidLighting.Begin(old, _ => { }, "p"));
            Assert.Null(Assert.Single(older.Targets).Color);
            old.Written.Clear();
            older.Paint(old, _ => { }, older.Map.Lights, new[] { 0x4080C0 });
            Assert.Equal(new[] { "00 07 0A 00", "00 07 09 C0" }, old.Written.Select(w => Hex(w, 0, 4)));
        }

        /// <summary>Wilba's, Rama's and Zeal's firmware on channel 0 (wt_main.c), with effect 1 solid
        /// color (wt_rgb_backlight.c:1705-1711). No other vendor's channel 0 is touched.</summary>
        [Theory]
        [InlineData("qmk:6582:060A")]
        [InlineData("qmk:5241:065B")]
        [InlineData("qmk:5A45:0060")]
        public void WilbasBoards_TakeTheirOwnValues(string product)
        {
            var board = Via(0x000D, 0x00);
            var session = Assert.IsType<QmkRawHidLighting.ViaSession>(QmkRawHidLighting.Begin(board, _ => { }, product));
            var target = Assert.Single(session.Targets);
            Assert.Equal((byte)0x00, target.Head[0]);
            board.Written.Clear();
            session.Paint(board, _ => { }, session.Map.Lights, new[] { 0xFF0000 });
            Assert.Equal(new[] { "00 07 00 0A 01", "00 07 00 0C 00 FF", "00 07 00 09 FF" }, board.Written.Select(w => Hex(w, 0, w[3] == 0x0C ? 6 : 5)));
            board.Written.Clear();
            session.HandBack(board, _ => { });
            Assert.Equal(new[] { "00 07 00 0A 07", "00 07 00 0C 0B 16", "00 07 00 09 C8" }, board.Written.Select(w => Hex(w, 0, w[3] == 0x0C ? 6 : 5)));
        }

        [Fact]
        public void AnotherVendorsChannelZero_IsNeverAsked()
        {
            var board = Via(0x000D, 0x00);
            Assert.Null(QmkRawHidLighting.Begin(board, _ => { }, "qmk:1234:5678"));
            Assert.DoesNotContain(board.Written, w => w[1] is 0x07 or 0x08 && w[2] == 0x00);
            Assert.Null(QmkRawHidLighting.Begin(Via(0x000D, 0x00), _ => { }, null));
        }

        /// <summary>Before channels Wilba's values share the list, and color tells them from a
        /// backlight's 9 and 0A.</summary>
        [Fact]
        public void AnOlderWilbaBoard_TakesItsValuesWithoutAChannel()
        {
            var board = Via(0x0009, 0x09, 0x0A, 0x0C);
            var session = Assert.IsType<QmkRawHidLighting.ViaSession>(QmkRawHidLighting.Begin(board, _ => { }, "p"));
            Assert.Equal((byte)0x0C, Assert.Single(session.Targets).Color);
            board.Written.Clear();
            session.Paint(board, _ => { }, session.Map.Lights, new[] { 0xFF0000 });
            Assert.Equal(new[] { "00 07 0A 01", "00 07 0C 00 FF", "00 07 09 FF" }, board.Written.Select(w => Hex(w, 0, w[2] == 0x0C ? 5 : 4)));
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

        private static Board OpenRgbBoard(byte revision, params byte[] modes) => OpenRgbBoard(revision, 2, 2, modes);

        /// <summary>The firmware with two LEDs at most: A with the key flag and an underglow LED.
        /// An LED past the count is a failure, at byte 62 on revision 9 and in the flags after it.</summary>
        private static Board OpenRgbBoard(byte revision, byte total, byte withEmpty, byte[] modes)
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
                        answer[2] = total;
                        answer[3] = withEmpty;
                        break;
                    case 4:
                        answer[2] = 9;
                        answer[3] = 8;
                        answer[4] = 7;
                        answer[5] = 6;
                        answer[6] = 5;
                        break;
                    case 5:
                        int count = revision == 0x09 ? 1 : packet[3];
                        for (int i = 0; i < count; i++)
                        {
                            int led = packet[2] + i, at = 2 + i * 7;
                            if (led >= total)
                            {
                                if (revision == 0x09) answer[1 + 62] = 25;
                                else answer[at + 2] = 25;
                                continue;
                            }
                            answer[at] = (byte)(led == 0 ? 10 : 30);
                            answer[at + 1] = (byte)(led == 0 ? 20 : 40);
                            answer[at + 2] = (byte)(led == 0 ? 0x04 : 0x02);
                            answer[at + 6] = (byte)(led == 0 ? 0x04 : 0);
                        }
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
            Assert.Equal(2, session.Map.Lights.Count);
            Assert.Equal(LightKind.Edge, session.Map.Lights[1].Kind);
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

        /// <summary>OpenRGB asks about the larger of the two counts on every revision
        /// (RGBController_QMKOpenRGBRev9.cpp:278-281, RevB:280, RevD:283), and drops what the firmware
        /// says it does not have: byte 62 on revision 9, the LED's flags on the others.</summary>
        [Theory]
        [InlineData(0x09)]
        [InlineData(0x0B)]
        [InlineData(0x0C)]
        [InlineData(0x0D)]
        [InlineData(0x0E)]
        public void TheLargerCount_IsAsked_AndWhatFailsIsDropped(int revision)
        {
            var board = OpenRgbBoard((byte)revision, 2, 3, new byte[] { 2, 1 });
            var session = Assert.IsType<QmkRawHidLighting.OpenRgbProtocolSession>(QmkRawHidLighting.Begin(board, _ => { }, "p"));
            Assert.Equal(2, session.Map.Lights.Count);
            Assert.Contains(board.Written, w => revision == 0x09 ? Hex(w, 0, 3) == "00 05 02" : Hex(w, 0, 4) == "00 05 00 03");
            var larger = OpenRgbBoard((byte)revision, 2, 0, new byte[] { 2, 1 });
            Assert.Equal(2, Assert.IsType<QmkRawHidLighting.OpenRgbProtocolSession>(QmkRawHidLighting.Begin(larger, _ => { }, "p")).Map.Lights.Count);
            Assert.Null(QmkRawHidLighting.Begin(OpenRgbBoard((byte)revision, 0, 0, new byte[] { 2, 1 }), _ => { }, "p"));
            Assert.Null(QmkRawHidLighting.Begin(OpenRgbBoard((byte)revision, 0, 2, new byte[] { 2, 1 }), _ => { }, "p"));
        }

        /// <summary>Before revision D a frame goes by place, so a color goes to its LED's place.</summary>
        [Fact]
        public void AnOlderFrame_PutsEachColorAtItsLedsPlace()
        {
            var lights = new[]
            {
                new LightInfo(1, LightKind.Key, 0, LightKeySource.None),
                new LightInfo(0, LightKind.Key, 0, LightKeySource.None),
            };
            var frame = Assert.Single(QmkRawHidLighting.OpenRgbProtocolSession.Frames(0x0B, lights, new[] { 0x111111, 0x222222 }));
            Assert.Equal("00 09 00 02 22 22 22 11 11 11", Hex(frame, 0, 10));
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

        // ── VialRGB without Direct ──

        /// <summary>A VialRGB firmware with these effects: its protocol, ID and flag, info, effect
        /// list and current mode, and the packet back for anything else.</summary>
        private static Board VialRgb(params byte[] effects)
        {
            var board = new Board();
            board.Reply = packet =>
            {
                var answer = (byte[])packet.Clone();
                switch (packet[1])
                {
                    case 0x01:
                        answer[3] = 0x09;
                        break;
                    case 0xFE:
                        Array.Clear(answer);
                        answer[1] = 0x06;
                        answer[13] = 0x01;
                        break;
                    case 0x08 when packet[2] == 0x41:
                        answer[3] = 0x05;
                        answer[5] = 0x80;
                        answer[6] = 0x01;
                        answer[7] = 0x02;
                        answer[8] = 0x03;
                        break;
                    case 0x08 when packet[2] == 0x42:
                        Array.Fill(answer, (byte)0xFF, 3, answer.Length - 3);
                        if (packet[3] == 0)
                            for (int i = 0; i < effects.Length; i++)
                            {
                                answer[3 + i * 2] = effects[i];
                                answer[4 + i * 2] = 0;
                            }
                        break;
                }
                return answer;
            };
            return board;
        }

        /// <summary>VIALRGB_NO_DIRECT: the solid color effect, 2, with the color, at the speed it
        /// read (RGBController_QMKVialRGB.cpp:256-272), and the mode read comes back.</summary>
        [Fact]
        public void AVialBoardWithoutDirect_TakesOneColorThroughItsSolidEffect()
        {
            var board = VialRgb(2, 3);
            var session = Assert.IsType<QmkLighting.VialSolidSession>(QmkRawHidLighting.Begin(board, _ => { }, "p"));
            Assert.Single(session.Map.Lights);
            Assert.Empty(session.CrashFrames);
            Assert.True(session.HandBack(board, _ => { }));
            board.Written.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(board, _ => { }, session.Map.Lights, new[] { 0xFF0000 }));
            Assert.Equal("00 07 41 02 00 80 00 FF FF", Hex(Assert.Single(board.Written), 0, 9));
            Assert.Equal("00 07 41 05 00 80 01 02 03", Hex(Assert.Single(session.CrashFrames), 0, 9));
            board.Written.Clear();
            Assert.True(session.HandBack(board, _ => { }));
            Assert.Equal("00 07 41 05 00 80 01 02 03", Hex(Assert.Single(board.Written), 0, 9));
            Assert.Empty(session.CrashFrames);
        }

        /// <summary>With Direct it lights per LED, and with neither VIA's values do not answer.</summary>
        [Fact]
        public void AVialBoardsEffects_ChooseItsSession()
        {
            Assert.Null(QmkRawHidLighting.Begin(VialRgb(3, 4), _ => { }, "p"));
            Assert.IsType<QmkLighting.VialSession>(QmkRawHidLighting.Begin(VialRgb(1, 2), _ => { }, "p"));
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
