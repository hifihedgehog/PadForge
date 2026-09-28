using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Common.AnalogKeyboard;
using PadForge.Engine.Common.AnalogKeyboard.Routes;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The NuPhy HE and Madlions Nano 68 Pro routes (issue #468). No such
    /// keyboard is on the bench, so every rule is pinned against the sources:
    /// NuPhy's configurator NuPhyIO (main.23dc78ef.js, cited by raw character
    /// offset), the NuPhyIO traffic an Air75 HE owner captured in
    /// calamity-inc/Soup#156, the two 0xA0 captures in
    /// AnalogSense/universal-analog-plugin#31, and nisayera/AnalogKeys at
    /// abaf1e4 (cited by file and line). Buffers are what Windows hands over:
    /// byte 0 is the report ID, 0 for this collection.
    /// </summary>
    public class AnalogKeyboardNuPhyTests
    {
        // ── Helpers ──

        private static AnalogKeyboardDeviceInfo Info(ushort vid, ushort pid, ushort usagePage = 1, ushort usage = 0,
            ushort inLength = 65, ushort outLength = 65, bool numbered = false, string product = "")
            => new()
            {
                VendorId = vid,
                ProductId = pid,
                UsagePage = usagePage,
                Usage = usage,
                InputReportLength = inLength,
                OutputReportLength = outLength,
                HasInputReport = id => numbered ? id == 1 : id == 0,
                HasOutputReport = id => numbered ? id == 1 : id == 0,
                ProductString = product,
            };

        /// <summary>A received report: report ID 0, then the frame, padded to
        /// the 64-byte report.</summary>
        private static byte[] Report(string hex)
        {
            var data = Convert.FromHexString(hex);
            var report = new byte[65];
            data.CopyTo(report, 1);
            return report;
        }

        private static byte[] Frame(byte[] report) => report.Skip(1).ToArray();

        /// <summary>A received event: header 0xA0, bytes 1 to 3, the travel
        /// at bytes 6 and 7 and the full-travel field at 14 and 15, both
        /// big-endian, padded to the 64-byte report.</summary>
        private static byte[] Event(int b1, int b2, int b3, int travel, int full = 0)
        {
            var report = new byte[65];
            report[1] = 0xA0;
            report[2] = (byte)b1;
            report[3] = (byte)b2;
            report[4] = (byte)b3;
            report[7] = (byte)(travel >> 8);
            report[8] = (byte)travel;
            report[15] = (byte)(full >> 8);
            report[16] = (byte)full;
            return report;
        }

        private static NuPhyHeModel NuPhy(ushort pid) => NuPhyHeModel.Find(pid);
        private static MadlionsA0Model Nano68Pro => MadlionsA0Model.Find(0x106B);

        /// <summary>
        /// A keyboard that speaks the command protocol over the scripted
        /// transport. It keeps function data for each mode, answers GetFunc
        /// with it, stores SetFunc data, and echoes the command header in each
        /// 0xAA reply, as NuPhyIO's own simulated HE keyboard does (module
        /// 65843, raw offsets 1977544 and 1977805) and AnalogKeys documents
        /// (docs/protocol.md:34-48). Hooks script the failures.
        /// </summary>
        private sealed class FakeKeyboard
        {
            public readonly byte[] Func;
            public readonly AnalogKeyboardTestTransport Io = new();

            /// <summary>No reply at all for a request (it still lands).</summary>
            public Func<byte[], bool> Silent = _ => false;

            /// <summary>Answer with 0xAB, the checksum-error reply.</summary>
            public Func<byte[], bool> ChecksumError = _ => false;

            /// <summary>The offset the reply claims, by default the request's.</summary>
            public Func<int, int, int> ReplyOffset = (command, offset) => offset;

            /// <summary>Reports the keyboard sends after the reply.</summary>
            public Func<byte[], IEnumerable<byte[]>> After = _ => Array.Empty<byte[]>();

            public FakeKeyboard(int length)
            {
                Func = new byte[length];
                Io.OnSend = Respond;
            }

            private IEnumerable<byte[]> Respond(byte[] report)
            {
                Assert.Equal(65, report.Length);
                Assert.Equal(0x00, report[0]);
                var frame = report.AsSpan(1);
                Assert.Equal(NuPhyProtocol.CommandHeader, frame[0]);
                Assert.Equal(NuPhyProtocol.Checksum(frame), frame[3]);
                byte command = frame[1];
                int length = frame[4];
                int offset = frame[5] | (frame[6] << 8);
                if (command == NuPhyProtocol.SetFunc)
                    Array.Copy(report, 1 + NuPhyProtocol.DataIndex, Func, offset, length);

                var replies = new List<byte[]>();
                if (!Silent(report))
                {
                    var reply = new byte[65];
                    Array.Copy(report, reply, 1 + NuPhyProtocol.DataIndex);
                    reply[1] = ChecksumError(report) ? NuPhyProtocol.ChecksumErrorHeader : NuPhyProtocol.ReplyHeader;
                    int claimed = ReplyOffset(command, offset);
                    reply[6] = (byte)claimed;
                    reply[7] = (byte)(claimed >> 8);
                    if (command == NuPhyProtocol.GetFunc)
                        Array.Copy(Func, claimed, reply, 1 + NuPhyProtocol.DataIndex, length);
                    reply[4] = NuPhyProtocol.Checksum(reply.AsSpan(1));
                    replies.Add(reply);
                }
                replies.AddRange(After(report));
                return replies;
            }

            public List<byte[]> Writes => Io.Writes("out");
        }

        /// <summary>Function data for three NuPhy modes: bytes 4 to 7 as the
        /// Air75 HE in calamity-inc/Soup#156 held them (01 86 00 03, debugMode
        /// clear), and other bytes set so any stray write would show.</summary>
        private static FakeKeyboard NuPhyKeyboard()
        {
            var kb = new FakeKeyboard(NuPhyProtocol.ModeCount * NuPhyProtocol.ModeStride);
            for (int i = 0; i < kb.Func.Length; i++) kb.Func[i] = (byte)(0x40 + i % 29);
            for (int mode = 0; mode < 3; mode++)
            {
                int at = mode * 64 + 4;
                kb.Func[at] = 0x01;
                kb.Func[at + 1] = 0x86;
                kb.Func[at + 2] = 0x00;
                kb.Func[at + 3] = 0x03;
            }
            return kb;
        }

        /// <summary>A Nano 68 Pro settings block at address 0 with debugMode
        /// clear in frame byte 15 (AnalogKeys protocol.py:80-84).</summary>
        private static FakeKeyboard NanoKeyboard()
        {
            var kb = new FakeKeyboard(64);
            for (int i = 0; i < kb.Func.Length; i++) kb.Func[i] = (byte)(0x11 + i * 7);
            kb.Func[7] = 0x22; // bit 1 bottom trigger, bit 3 debugMode clear, bits 5-7 anti-shake
            return kb;
        }

        private static bool DebugBit(byte[] func, int mode) => (func[mode * 64 + 7] & 0x08) != 0;

        // ── Routes and catalogs ──

        [Fact]
        public void All_IsNuPhyHeThenMadlionsA0()
        {
            // The integrator's order: NuPhyHe replaces the Soup NuPhy route,
            // MadlionsA0 follows HallJoy's Madlions routes.
            Assert.Equal(new[] { "nuphy-he", "madlions-a0" }, NuPhyRoutes.All.Select(r => r.Id).ToArray());
            foreach (var route in NuPhyRoutes.All)
            {
                Assert.Equal(AnalogKeyboardProtocol.NuPhy, route.Protocol);
                Assert.True(route.Writable);
                // Shared, as NuPhyIO (WebHID) and AnalogKeys (hidapi) open it.
                Assert.False(route.Exclusive);
                // HallJoy's A0 backend: HidD_SetNumInputBuffers 256 (mad68pr_backend.cpp:716).
                Assert.Equal(256, route.InputBuffers);
                Assert.Null(route.Companion);
            }
        }

        [Theory]
        // NuPhyIO's device catalog, keyboard entries on usage 0 / usage page 1
        // (raw offsets 173948-180666).
        [InlineData(0xFEE0, "NuPhy Air60 HE", 10)]
        [InlineData(0x6120, "NuPhy Air75 HE", 10)]
        [InlineData(0xFE70, "NuPhy Field75 HE", 20)]
        [InlineData(0x6132, "NuPhy Field75 HE V2", 10)]
        [InlineData(0x6112, "NuPhy Halo65 HE", 20)]
        [InlineData(0x6113, "NuPhy Halo65 HE Pro", 10)]
        [InlineData(0x6130, "NuPhy BH65 HE", 10)]
        [InlineData(0x6100, "NuPhy Gem80 HE", 10)]
        [InlineData(0x6131, null, 10)]
        [InlineData(0xA011, "NuPhy WH80", 10)]
        [InlineData(0x8F01, "NuPhy WH80 (dongle)", 10)]
        public void NuPhyHe_MatchesEveryHeModel_WithItsPrecision(int pid, string name, int microns)
        {
            // defaultPrecision per device-data module: 0.02 mm on the Field75 HE
            // (raw offset 145206) and Halo65 HE (149299), 0.01 mm elsewhere.
            var info = Info(0x19F5, (ushort)pid, product: "anything");
            Assert.True(NuPhyRoutes.NuPhyHe.Matches(info));
            Assert.False(NuPhyRoutes.MadlionsA0.Matches(info));
            var model = NuPhy((ushort)pid);
            Assert.Equal(name, model.Name);
            Assert.Equal(microns, model.MicronsPerCount);
            Assert.Equal(name, NuPhyRoutes.NuPhyHe.Name(info));
            Assert.Null(NuPhyRoutes.NuPhyHe.Keys);
            Assert.IsType<NuPhyHeSession>(NuPhyRoutes.NuPhyHe.CreateSession(info));
        }

        [Fact]
        public void NuPhyHe_CatalogHasElevenModels()
        {
            // Ten HE keyboards plus the WH80 dongle, NuPhyIO's catalog entries
            // with the keyboard capability that are not mechanical boards.
            Assert.Equal(11, NuPhyHeModel.All.Count);
        }

        [Fact]
        public void NuPhyHe_RejectsTheMechanicalBoardsAndOtherNuPhyIds()
        {
            // NuPhyIO's mechanical keyboards (another protocol class, no 0xA0
            // parser), the U1 dongle 0x2620, and the HE bootloader IDs.
            ushort[] others =
            {
                0x1026, 0x1027, 0x1028, 0x1029, 0x102A, 0x102B, 0x102C, 0x102D, 0x102E, 0x102F,
                0x1030, 0x1031, 0x1032, 0x1033, 0x1034, 0x1035, 0x1036, 0x1037, 0x1038, 0x1039,
                0x103A, 0x103B, 0x103C, 0x103F, 0x1040, 0x1042, 0x1043, 0x1044,
                0x2620,
                0xFF20, 0x6F10, 0xFF02, 0xAF0F, 0x6F03, 0x6F04, 0xFF5A, 0x6F11, 0x8A04, 0xFE00,
            };
            foreach (ushort pid in others)
                Assert.False(NuPhyRoutes.NuPhyHe.Matches(Info(0x19F5, pid, product: "NuPhy HE")), $"{pid:X4}");
        }

        [Fact]
        public void NuPhyHe_RejectsNearMisses()
        {
            // Right keyboard, wrong collection or shape, and the right shape
            // on another vendor.
            Assert.False(NuPhyRoutes.NuPhyHe.Matches(Info(0x19F5, 0x6120, usage: 6)));
            Assert.False(NuPhyRoutes.NuPhyHe.Matches(Info(0x19F5, 0x6120, usagePage: 0xFF60, usage: 0x61)));
            Assert.False(NuPhyRoutes.NuPhyHe.Matches(Info(0x19F5, 0x6120, inLength: 33, outLength: 33)));
            Assert.False(NuPhyRoutes.NuPhyHe.Matches(Info(0x19F5, 0x6120, outLength: 0)));
            Assert.False(NuPhyRoutes.NuPhyHe.Matches(Info(0x19F5, 0x6120, numbered: true)));
            Assert.False(NuPhyRoutes.NuPhyHe.Matches(Info(0x19F4, 0x6120)));
            Assert.False(NuPhyRoutes.NuPhyHe.Matches(Info(0x373B, 0x6120)));
            Assert.False(NuPhyRoutes.NuPhyHe.Matches(null));
            Assert.Null(NuPhyRoutes.NuPhyHe.CreateSession(Info(0x19F5, 0x1028)));
        }

        [Theory]
        // The seven product IDs AnalogKeys calls Nano 68 Pro (protocol.py:30-36).
        [InlineData(0x106B)]
        [InlineData(0x106D)]
        [InlineData(0x10FF)]
        [InlineData(0x1100)]
        [InlineData(0x110C)]
        [InlineData(0x113A)]
        [InlineData(0x1143)]
        public void MadlionsA0_MatchesTheNano68Pro(int pid)
        {
            var info = Info(0x373B, (ushort)pid);
            Assert.True(NuPhyRoutes.MadlionsA0.Matches(info));
            Assert.False(NuPhyRoutes.NuPhyHe.Matches(info));
            Assert.Equal("Madlions Nano 68 Pro", NuPhyRoutes.MadlionsA0.Name(info));
            Assert.Equal(Nano68Pro.KeyOrder, NuPhyRoutes.MadlionsA0.Keys(info));
            Assert.IsType<MadlionsA0Session>(NuPhyRoutes.MadlionsA0.CreateSession(info));
        }

        [Fact]
        public void MadlionsA0_AdmitsEveryAnalogKeysPid_ButThoseOtherRoutesOwn()
        {
            // AnalogKeys' 32 product IDs (protocol.py:28-61). The 24 without a
            // recorded key table are read too, keys by index, 373B:1109 is
            // HallJoy's MAD 68 Pro R, and the Soup family owns the VIA boards.
            ushort[] noTable =
            {
                0x1064, 0x1101, 0x11A1, 0x106E, 0x10A8, 0x10D3, 0x10D4, 0x1102, 0x1131,
                0x1038, 0x1047, 0x104E, 0x1052, 0x1050, 0x103B, 0x1051, 0x1061, 0x10C1, 0x10EB,
                0x10D5, 0x10F2, 0x10F3, 0x10F4, 0x10F5,
            };
            foreach (ushort pid in noTable)
            {
                Assert.True(NuPhyRoutes.MadlionsA0.Matches(Info(0x373B, pid)), $"{pid:X4}");
                Assert.Null(MadlionsA0Model.Find(pid).Table);
            }
            Assert.Equal("Madlions Fire 68 Ultra Limit", MadlionsA0Model.Find(0x10D5).Name);
            Assert.False(NuPhyRoutes.MadlionsA0.Matches(Info(0x373B, 0x1109)));
            foreach (ushort pid in NuPhyRoutes.MadlionsPidsOwnedElsewhere)
                Assert.False(NuPhyRoutes.MadlionsA0.Matches(Info(0x373B, pid)), $"{pid:X4}");
            Assert.Contains((ushort)0x1109, NuPhyRoutes.MadlionsPidsOwnedElsewhere);
            Assert.Equal(31, MadlionsA0Model.All.Count);
        }

        [Fact]
        public void MadlionsA0_BoardWithoutATable_PublishesKeysByIndex()
        {
            // AnalogKeys learns these boards' keys by having them pressed. A
            // key index becomes a position code, and the modifier mask in byte
            // 2 still names its key.
            Assert.Equal(AnalogKeyCodes.PositionBase + 0x1A, NuPhyProtocol.MadlionsKeyCode(0, 0x1A, null));
            Assert.Equal(AnalogKeyCodes.None, NuPhyProtocol.MadlionsKeyCode(0, 0, null));
            Assert.Equal(AnalogKeyCodes.LShift, NuPhyProtocol.MadlionsKeyCode(0x02, 0x00, null));
            Assert.Contains(AnalogKeyCodes.LCtrl, MadlionsA0Model.Find(0x1064).KeyOrder);
        }

        [Fact]
        public void MadlionsA0_RejectsNearMisses()
        {
            // AnalogKeys picks usage page 1, usage 0 (device.py:27-40). The
            // VIA collection at 0xFF60/0x61 is the Soup family's.
            Assert.False(NuPhyRoutes.MadlionsA0.Matches(Info(0x373B, 0x106B, usagePage: 0xFF60, usage: 0x61)));
            Assert.False(NuPhyRoutes.MadlionsA0.Matches(Info(0x373B, 0x106B, usage: 6)));
            Assert.False(NuPhyRoutes.MadlionsA0.Matches(Info(0x373B, 0x106B, inLength: 33, outLength: 33)));
            Assert.False(NuPhyRoutes.MadlionsA0.Matches(Info(0x373B, 0x106B, numbered: true)));
            Assert.False(NuPhyRoutes.MadlionsA0.Matches(Info(0x19F5, 0x106B)));
        }

        [Fact]
        public void Nano68ProTable_IsAnalogKeysKeymap()
        {
            // examples/keymap.example.json: every ordinary key's firmware index
            // equals its keyboard-page usage, and Fn is index 1.
            var table = Nano68Pro.Table;
            Assert.Equal(83, table.Length);
            Assert.Equal(AnalogKeyCodes.Fn, table[1]);
            Assert.Equal(AnalogKeyCodes.A, table[4]);
            Assert.Equal(AnalogKeyCodes.W, table[26]);
            Assert.Equal(AnalogKeyCodes.Space, table[44]);
            Assert.Equal(AnalogKeyCodes.Backslash, table[49]);
            Assert.Equal(AnalogKeyCodes.Home, table[74]);
            Assert.Equal(AnalogKeyCodes.Delete, table[76]);
            Assert.Equal(AnalogKeyCodes.ArrowUp, table[82]);
            Assert.Equal(AnalogKeyCodes.None, table[0]);
            Assert.Equal(AnalogKeyCodes.None, table[50]);
            int ordinary = 0;
            for (int i = 0; i < table.Length; i++)
            {
                if (table[i] == 0 || table[i] == AnalogKeyCodes.Fn) continue;
                Assert.Equal(i, table[i]);
                ordinary++;
            }
            Assert.Equal(60, ordinary);
        }

        [Fact]
        public void Nano68ProKeyOrder_IsAnalogKeysLayoutWithModifiers()
        {
            // analogkeys/layout.py:28-57, 68 keys in rows, modifiers included
            // (layout.py:63-66), no right GUI.
            var order = Nano68Pro.KeyOrder;
            Assert.Equal(68, order.Length);
            Assert.Equal(order.Length, order.Distinct().Count());
            Assert.Equal(AnalogKeyCodes.Escape, order[0]);
            Assert.Equal(AnalogKeyCodes.Delete, order[14]);
            Assert.Equal(AnalogKeyCodes.ArrowRight, order[67]);
            Assert.Contains(AnalogKeyCodes.LShift, order);
            Assert.Contains(AnalogKeyCodes.RAlt, order);
            Assert.Contains(AnalogKeyCodes.Fn, order);
            Assert.DoesNotContain(AnalogKeyCodes.RMeta, order);
            // Every key the table reports is listed.
            foreach (int code in AnalogKeyboardData.KeysOf(Nano68Pro.Table)) Assert.Contains(code, order);
        }

        // ── Frames ──

        [Fact]
        public void NuPhyGetFunc_MatchesTheCapturedNuPhyIoFrames()
        {
            // calamity-inc/Soup#156, NuPhyIO on an Air75 HE: 55 05 00 36 36 00,
            // 55 05 00 76 36 40, 55 05 00 B6 36 80, zeros after.
            byte[][] captured =
            {
                new byte[] { 0x55, 0x05, 0x00, 0x36, 0x36, 0x00, 0x00, 0x00 },
                new byte[] { 0x55, 0x05, 0x00, 0x76, 0x36, 0x40, 0x00, 0x00 },
                new byte[] { 0x55, 0x05, 0x00, 0xB6, 0x36, 0x80, 0x00, 0x00 },
            };
            for (int mode = 0; mode < 3; mode++)
            {
                var request = NuPhyHeSession.ReadFuncRequest(mode);
                Assert.Equal(65, request.Length);
                Assert.Equal(0x00, request[0]);
                Assert.Equal(captured[mode], request.Skip(1).Take(8).ToArray());
                Assert.All(request.Skip(9), b => Assert.Equal(0, b));
            }
        }

        [Fact]
        public void NuPhySetFunc_MatchesTheCapturedNuPhyIoFrames()
        {
            // calamity-inc/Soup#156: on, byte 7 03 becomes 0B (checksums 9A, DA,
            // 1A), off, back to 03 (92, D2, 12). Four data bytes at 64 times
            // the mode plus 4.
            byte[] on = { 0x01, 0x86, 0x00, 0x0B };
            byte[] off = { 0x01, 0x86, 0x00, 0x03 };
            byte[] onSums = { 0x9A, 0xDA, 0x1A };
            byte[] offSums = { 0x92, 0xD2, 0x12 };
            for (int mode = 0; mode < 3; mode++)
            {
                byte at = (byte)(mode * 64 + 4);
                Assert.Equal(new byte[] { 0x00, 0x55, 0x06, 0x00, onSums[mode], 0x04, at, 0x00, 0x00, 0x01, 0x86, 0x00, 0x0B },
                    NuPhyHeSession.WriteFlagsRequest(mode, on).Take(13).ToArray());
                Assert.Equal(new byte[] { 0x00, 0x55, 0x06, 0x00, offSums[mode], 0x04, at, 0x00, 0x00, 0x01, 0x86, 0x00, 0x03 },
                    NuPhyHeSession.WriteFlagsRequest(mode, off).Take(13).ToArray());
                Assert.All(NuPhyHeSession.WriteFlagsRequest(mode, on).Skip(13), b => Assert.Equal(0, b));
            }
        }

        [Fact]
        public void MadlionsFrames_MatchAnalogKeys()
        {
            // build_get_func: 55 05, length 0x38, address 0, checksum 0x38
            // (protocol.py:98-104). build_set_func: 55 06, length 0x38, the
            // block from byte 8, checksum over bytes 4 to 63 (protocol.py:111-129).
            Assert.Equal(new byte[] { 0x00, 0x55, 0x05, 0x00, 0x38, 0x38, 0x00, 0x00, 0x00 },
                MadlionsA0Session.ReadBlockRequest().Take(9).ToArray());
            var block = Enumerable.Range(0, 56).Select(i => (byte)(i * 3)).ToArray();
            var request = MadlionsA0Session.WriteBlockRequest(block);
            Assert.Equal(new byte[] { 0x00, 0x55, 0x06, 0x00 }, request.Take(4).ToArray());
            Assert.Equal(0x38, request[5]);
            Assert.Equal(block, request.Skip(9).ToArray());
            int sum = 0x38 + block.Sum(b => b);
            Assert.Equal((byte)sum, request[4]);
        }

        [Fact]
        public void Checksum_IsTheLowByteOfBytes4To63()
        {
            // NuPhyIO createCommand (raw offset 1882211), AnalogKeys protocol.py:87-89.
            var frame = new byte[64];
            for (int i = 0; i < 64; i++) frame[i] = 0xFF;
            Assert.Equal(unchecked((byte)(60 * 0xFF)), NuPhyProtocol.Checksum(frame));
            Assert.Throws<ArgumentOutOfRangeException>(() => NuPhyProtocol.Command(0x06, 57, 0, new byte[57]));
        }

        [Fact]
        public void ClassifyReply_WantsTheHeaderCommandAndOffsetEcho()
        {
            // NuPhyIO takes 0xAA reports with the command's byte 1 (raw offsets
            // 590153 and 594011). The offset echo keeps one mode's reply from
            // answering another's request. 0xAB is the checksum-error reply
            // (HallJoy mad68pr_protocol.h:22-23).
            var reply = Frame(NuPhyHeSession.ReadFuncRequest(1));
            reply[0] = 0xAA;
            Assert.Equal(NuPhyProtocol.ReplyKind.Reply, NuPhyProtocol.ClassifyReply(reply, 0x05, 64));
            Assert.Equal(NuPhyProtocol.ReplyKind.Other, NuPhyProtocol.ClassifyReply(reply, 0x05, 0));
            Assert.Equal(NuPhyProtocol.ReplyKind.Other, NuPhyProtocol.ClassifyReply(reply, 0x06, 64));
            Assert.Equal(NuPhyProtocol.ReplyKind.Other, NuPhyProtocol.ClassifyReply(reply.Take(63).ToArray(), 0x05, 64));
            reply[0] = 0xAB;
            Assert.Equal(NuPhyProtocol.ReplyKind.ChecksumError, NuPhyProtocol.ClassifyReply(reply, 0x05, 64));
            reply[0] = 0x55;
            Assert.Equal(NuPhyProtocol.ReplyKind.Other, NuPhyProtocol.ClassifyReply(reply, 0x05, 64));
        }

        // ── NuPhy start and stop ──

        [Fact]
        public void NuPhyStart_SetsOnlyTheDebugBit_InEachMode_InNuPhyIosOrder()
        {
            // setKeyboardDebug (raw offset 1733856): for modes 0 to 2, GetFunc
            // then SetFunc of bytes 4 to 7 with bit 3 of byte 7 set.
            var kb = NuPhyKeyboard();
            var before = (byte[])kb.Func.Clone();
            var session = new NuPhyHeSession(NuPhy(0x6120));
            Assert.True(session.Start(kb.Io));

            var writes = kb.Writes;
            Assert.Equal(6, writes.Count);
            for (int mode = 0; mode < 3; mode++)
            {
                Assert.Equal(NuPhyHeSession.ReadFuncRequest(mode), writes[2 * mode]);
                Assert.Equal(NuPhyHeSession.WriteFlagsRequest(mode, new byte[] { 0x01, 0x86, 0x00, 0x0B }), writes[2 * mode + 1]);
                Assert.True(session.ModeChanged(mode));
            }
            for (int i = 0; i < kb.Func.Length; i++)
            {
                byte expected = i % 64 == 7 ? (byte)(before[i] | 0x08) : before[i];
                Assert.Equal(expected, kb.Func[i]);
            }
            Assert.Equal("NuPhy Air75 HE", session.ModelName);
            Assert.Null(session.KeyOrder);
        }

        [Fact]
        public void NuPhyStop_PutsEveryBitBack_AndTheDataIsAsFound()
        {
            // endKeyStatusUpload: GetFunc, then SetFunc with the bit clear, per
            // mode. calamity-inc/Soup#156's shutdown frames.
            var kb = NuPhyKeyboard();
            var before = (byte[])kb.Func.Clone();
            var session = new NuPhyHeSession(NuPhy(0x6120));
            Assert.True(session.Start(kb.Io));
            int started = kb.Writes.Count;
            session.Stop(kb.Io);

            var stop = kb.Writes.Skip(started).ToList();
            Assert.Equal(6, stop.Count);
            for (int mode = 0; mode < 3; mode++)
            {
                Assert.Equal(NuPhyHeSession.ReadFuncRequest(mode), stop[2 * mode]);
                Assert.Equal(NuPhyHeSession.WriteFlagsRequest(mode, new byte[] { 0x01, 0x86, 0x00, 0x03 }), stop[2 * mode + 1]);
                Assert.False(session.ModeChanged(mode));
            }
            Assert.Equal(before, kb.Func);

            // A second stop writes nothing.
            session.Stop(kb.Io);
            Assert.Equal(started + 6, kb.Writes.Count);
        }

        [Fact]
        public void NuPhyStart_LeavesAModeWhoseBitIsAlreadySet_AndStopDoesToo()
        {
            // setPartialKeyboardFunc returns success without writing when the
            // value already matches (raw offset 1636647).
            var kb = NuPhyKeyboard();
            kb.Func[64 + 7] |= 0x08;
            var before = (byte[])kb.Func.Clone();
            var session = new NuPhyHeSession(NuPhy(0xFE70));
            Assert.True(session.Start(kb.Io));
            Assert.Equal(5, kb.Writes.Count); // no SetFunc for mode 1
            Assert.False(session.ModeChanged(1));
            Assert.True(DebugBit(kb.Func, 0) && DebugBit(kb.Func, 1) && DebugBit(kb.Func, 2));

            session.Stop(kb.Io);
            Assert.Equal(before, kb.Func);
            Assert.True(DebugBit(kb.Func, 1));
        }

        [Fact]
        public void NuPhyStop_ReadsAgain_SoAChangeMadeMeanwhileSurvives()
        {
            // NuPhyIO always reads before it writes (setPartialKeyboardFunc):
            // a report rate changed during the session keeps its new value.
            var kb = NuPhyKeyboard();
            var session = new NuPhyHeSession(NuPhy(0x6120));
            Assert.True(session.Start(kb.Io));
            kb.Func[64 + 4] = 0x04;
            session.Stop(kb.Io);
            Assert.Equal(0x04, kb.Func[64 + 4]);
            Assert.False(DebugBit(kb.Func, 0) || DebugBit(kb.Func, 1) || DebugBit(kb.Func, 2));
        }

        [Fact]
        public void NuPhyStop_WritesTheStartBytes_WhenTheReadGoesUnanswered()
        {
            // The literal undo when the keyboard stops answering reads.
            var kb = NuPhyKeyboard();
            var before = (byte[])kb.Func.Clone();
            var session = new NuPhyHeSession(NuPhy(0x6120));
            Assert.True(session.Start(kb.Io));
            kb.Silent = r => r[2] == NuPhyProtocol.GetFunc;
            session.Stop(kb.Io);
            Assert.Equal(before, kb.Func);
        }

        [Fact]
        public void NuPhyStart_NoAnswer_WritesNothing()
        {
            // Four tries of the first GetFunc (NuPhyIO maxRetries 3, raw offset
            // 593887), then give up without a single SetFunc.
            var kb = NuPhyKeyboard();
            var before = (byte[])kb.Func.Clone();
            kb.Silent = _ => true;
            var session = new NuPhyHeSession(NuPhy(0x6120));
            Assert.False(session.Start(kb.Io));
            Assert.Equal(4, kb.Writes.Count);
            Assert.All(kb.Writes, w => Assert.Equal(NuPhyHeSession.ReadFuncRequest(0), w));
            Assert.Equal(before, kb.Func);
        }

        [Fact]
        public void NuPhyStart_ReplyForAnotherMode_IsRefused_AndModeZeroIsPutBack()
        {
            // Mode 1's request answered with mode 0's offset: never trusted.
            // Start fails and undoes mode 0.
            var kb = NuPhyKeyboard();
            var before = (byte[])kb.Func.Clone();
            kb.ReplyOffset = (command, offset) => command == NuPhyProtocol.GetFunc && offset == 64 ? 0 : offset;
            var session = new NuPhyHeSession(NuPhy(0x6120));
            Assert.False(session.Start(kb.Io));
            Assert.Equal(before, kb.Func);
            // GetFunc 0, SetFunc 0, four GetFunc 1, then GetFunc 0 and SetFunc 0 to undo.
            var w = kb.Writes;
            Assert.Equal(8, w.Count);
            Assert.Equal(NuPhyHeSession.ReadFuncRequest(1), w[2]);
            Assert.Equal(NuPhyHeSession.ReadFuncRequest(1), w[5]);
            Assert.Equal(NuPhyHeSession.WriteFlagsRequest(0, new byte[] { 0x01, 0x86, 0x00, 0x03 }), w[7]);
        }

        [Fact]
        public void NuPhyStart_UnansweredWrite_IsUndoneToo()
        {
            // The mode 2 SetFunc lands but no reply comes: an uncertain enable.
            // Start fails and every mode, mode 2 included, ends as it began.
            var kb = NuPhyKeyboard();
            var before = (byte[])kb.Func.Clone();
            kb.Silent = r => r[2] == NuPhyProtocol.SetFunc && r[6] == 132;
            var session = new NuPhyHeSession(NuPhy(0x6120));
            Assert.False(session.Start(kb.Io));
            Assert.Equal(before, kb.Func);
            for (int mode = 0; mode < 3; mode++) Assert.False(session.ModeChanged(mode));
        }

        [Fact]
        public void NuPhyStart_ChecksumErrorReply_IsRetried()
        {
            // 0xAB answers a frame the keyboard rejected (HallJoy
            // MAD68_PRO_R_FIRMWARE_FINAL_AUDIT.md:118-119): send it again.
            var kb = NuPhyKeyboard();
            int errors = 1;
            kb.ChecksumError = r => r[2] == NuPhyProtocol.GetFunc && errors-- > 0;
            var session = new NuPhyHeSession(NuPhy(0x6120));
            Assert.True(session.Start(kb.Io));
            Assert.Equal(7, kb.Writes.Count);
            Assert.Equal(kb.Writes[0], kb.Writes[1]);
        }

        [Fact]
        public void NuPhyStart_DeviceGone_Fails()
        {
            // A read that says the device is gone ends the handshake before a
            // single write (IAnalogKeyboardTransport.Receive returns -1).
            var kb = NuPhyKeyboard();
            kb.Io.Gone = true;
            Assert.False(new NuPhyHeSession(NuPhy(0x6120)).Start(kb.Io));
            Assert.Empty(kb.Writes);
        }

        [Fact]
        public void NuPhyStart_StaleRepliesAndEventsBeforeACommand_AreNotItsAnswer()
        {
            // Reports queued before a command are read off first: an old reply
            // to the same command cannot answer it, and events are kept.
            var kb = NuPhyKeyboard();
            var stale = new byte[65];
            Array.Copy(NuPhyHeSession.ReadFuncRequest(0), stale, 65);
            stale[1] = 0xAA; // a reply with data bytes all zero
            kb.Io.QueueInput(stale, Report("a010002c0640014aee01ff000704014a086e0d17"));
            var session = new NuPhyHeSession(NuPhy(0x6120));
            Assert.True(session.Start(kb.Io));
            Assert.Equal(NuPhyHeSession.WriteFlagsRequest(0, new byte[] { 0x01, 0x86, 0x00, 0x0B }), kb.Writes[1]);

            var output = new AnalogKeyInputState();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(kb.Io, output, null));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.Space));
        }

        [Fact]
        public void NuPhyStart_EventsDuringTheHandshake_ArePublishedByTheFirstPass()
        {
            // The keyboard starts streaming after the first SetFunc, while the
            // other modes are still being set (NuPhyIO parses events during
            // commands, raw offset 590153).
            var kb = NuPhyKeyboard();
            kb.After = r => r[2] == NuPhyProtocol.SetFunc && r[6] == 4
                ? new[] { Report("a010001a1e95014f0a01ff01040501554b35050f"), Report("a01000041ec501553c01ff0103080155496d04e4") }
                : Array.Empty<byte[]>();
            var session = new NuPhyHeSession(NuPhy(0xA011));
            Assert.True(session.Start(kb.Io));
            var output = new AnalogKeyInputState();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(kb.Io, output, null));
            Assert.Equal(335f / 341f, output.Get(AnalogKeyCodes.W), 5);
            Assert.Equal(1f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(AnalogPollResult.Idle, session.Pass(kb.Io, output, null));
            Assert.Equal(2, output.Count);
        }

        // ── NuPhy events ──

        [Fact]
        public void Air75HeCapture_RestIsZero_BottomIsOne()
        {
            // universal-analog-plugin#31, tvvoty's Air75 HE space bar log: bytes
            // 6..7 reach 330 and bytes 14..15 read 330 in every row.
            var session = new NuPhyHeSession(NuPhy(0x6120));
            var output = new AnalogKeyInputState();
            Assert.True(session.Apply(Report("a010002c0640014aee01ff000704014a086e0d170000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"), output));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.Space));
            Assert.True(session.Apply(Report("a010002c0007000000ffff000801014a0d110d170000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"), output));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.Space));
            Assert.Equal(0, output.Count);
        }

        [Fact]
        public void Wh80Capture_RestIsZero_BottomIsOne_AndModifiersDecode()
        {
            // universal-analog-plugin#31, foodpolicy's WH80 log: bytes 14..15
            // read 341 in every row, A bottoms at 341, W stops at 335, and
            // Left Ctrl arrives as modifier bit 0x01 in byte 2.
            var session = new NuPhyHeSession(NuPhy(0xA011));
            var output = new AnalogKeyInputState();
            session.Apply(Report("a01000041ec501553c01ff0103080155496d04e41eb901500000015000ba00010000000000000000000000000000000000000000000000000000000000000000"), output);
            session.Apply(Report("a010001a1e95014f0a01ff01040501554b35050f1e63014d0000014d00ba00030000000000000000000000000000000000000000000000000000000000000000"), output);
            session.Apply(Report("a01001001ec701551401ff010407015549a905101e6e014d0000014d00cd00010000000000000000000000000000000000000000000000000000000000000000"), output);
            Assert.Equal(1f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(335f / 341f, output.Get(AnalogKeyCodes.W), 5);
            Assert.Equal(1f, output.Get(AnalogKeyCodes.LCtrl));

            session.Apply(Report("a01000040000000000ffff01030801559e4804e40000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"), output);
            session.Apply(Report("a010001a0001000000ffff0104050155a293050f0001000000000000003e00000000000000000000000000000000000000000000000000000000000000000000"), output);
            session.Apply(Report("a01001000000000000ffff0104070155a22c05100000000000000000002000000000000000000000000000000000000000000000000000000000000000000000"), output);
            Assert.Equal(0, output.Count);
        }

        [Fact]
        public void BothCaptures_Replayed_EveryRowIsAKey_NothingPastOne_AllReleasedAtTheEnd()
        {
            foreach (var (pid, rows, peaks) in new[]
            {
                ((ushort)0x6120, Air75HeRows, new Dictionary<int, float> { [AnalogKeyCodes.Space] = 1f }),
                ((ushort)0xA011, Wh80Rows, new Dictionary<int, float>
                {
                    [AnalogKeyCodes.W] = 335f / 341f, [AnalogKeyCodes.A] = 1f, [AnalogKeyCodes.S] = 1f,
                    [AnalogKeyCodes.D] = 1f, [AnalogKeyCodes.LCtrl] = 1f, [AnalogKeyCodes.C] = 1f,
                }),
            })
            {
                var session = new NuPhyHeSession(NuPhy(pid));
                var io = new AnalogKeyboardTestTransport();
                foreach (var row in rows) io.QueueInput(Report(row));
                var output = new AnalogKeyInputState();
                var seen = new Dictionary<int, float>();
                for (int i = 0; i < rows.Length; i++)
                {
                    Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
                    int code = NuPhyProtocol.NuPhyKeyCode(Convert.FromHexString(rows[i])[1],
                        Convert.FromHexString(rows[i])[2], Convert.FromHexString(rows[i])[3]);
                    Assert.NotEqual(0, code);
                    float depth = output.Get(code);
                    Assert.InRange(depth, 0f, 1f);
                    seen[code] = Math.Max(seen.TryGetValue(code, out float p) ? p : 0f, depth);
                }
                Assert.Equal(0, output.Count);
                Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
                Assert.Equal(peaks.Count, seen.Count);
                foreach (var (code, peak) in peaks) Assert.Equal(peak, seen[code], 5);
            }
        }

        [Fact]
        public void NuPhyDepth_FallsBackToFourMillimeters_WhenBytes14And15AreZero()
        {
            // travel times defaultPrecision over 4.0 mm: 200 counts are 4.0 mm
            // at 0.02 mm (Field75 HE) and 2.0 mm at 0.01 mm (Air60 HE).
            var field = new NuPhyHeSession(NuPhy(0xFE70));
            var air = new NuPhyHeSession(NuPhy(0xFEE0));
            var output = new AnalogKeyInputState();
            Assert.True(field.Apply(Event(0x10, 0x00, 0x1A, 200), output));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
            Assert.True(air.Apply(Event(0x10, 0x00, 0x1A, 200), output));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W), 5);
            // With bytes 14..15 set, they win over the precision: 165 of 330
            // is half, where 1.65 mm over 4.0 mm would read 0.4125.
            Assert.True(air.Apply(Event(0x10, 0x00, 0x1A, 165, 330), output));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W), 5);
            Assert.Equal(0f, NuPhyProtocol.NuPhyDepth(0, 330, 10));
            Assert.Equal(1f, NuPhyProtocol.NuPhyDepth(400, 330, 10));
            Assert.Equal(1f, NuPhyProtocol.NuPhyDepth(250, 0, 20));
        }

        [Theory]
        // NuPhyIO's key decoder (module 88352): type 0x10 with code0 0 is a
        // usage, with code1 0 a modifier bit. Soup reads FF 05 as Fn
        // (AnalogueKeyboard.cpp:1087), NuPhyIO's KC_FN5 is 0xF0FF05.
        [InlineData(0x10, 0x00, 0x2C, AnalogKeyCodes.Space)]
        [InlineData(0x10, 0x00, 0x04, AnalogKeyCodes.A)]
        [InlineData(0x10, 0x00, 0xE0, AnalogKeyCodes.LCtrl)]
        [InlineData(0x10, 0x01, 0x00, AnalogKeyCodes.LCtrl)]
        [InlineData(0x10, 0x02, 0x00, AnalogKeyCodes.LShift)]
        [InlineData(0x10, 0x04, 0x00, AnalogKeyCodes.LAlt)]
        [InlineData(0x10, 0x08, 0x00, AnalogKeyCodes.LMeta)]
        [InlineData(0x10, 0x10, 0x00, AnalogKeyCodes.RCtrl)]
        [InlineData(0x10, 0x20, 0x00, AnalogKeyCodes.RShift)]
        [InlineData(0x10, 0x40, 0x00, AnalogKeyCodes.RAlt)]
        [InlineData(0x10, 0x80, 0x00, AnalogKeyCodes.RMeta)]
        [InlineData(0xF0, 0xFF, 0x05, AnalogKeyCodes.Fn)]
        [InlineData(0x10, 0xFF, 0x05, AnalogKeyCodes.Fn)]
        [InlineData(0x10, 0x03, 0x00, AnalogKeyCodes.None)]
        [InlineData(0x10, 0x01, 0x04, AnalogKeyCodes.None)]
        [InlineData(0x10, 0x00, 0x00, AnalogKeyCodes.None)]
        [InlineData(0x10, 0x00, 0x01, AnalogKeyCodes.None)]
        [InlineData(0x10, 0x00, 0xE8, AnalogKeyCodes.None)]
        [InlineData(0xF0, 0xFF, 0x01, AnalogKeyCodes.None)]
        [InlineData(0x30, 0x00, 0xE2, AnalogKeyCodes.None)]
        [InlineData(0x20, 0x00, 0x04, AnalogKeyCodes.None)]
        public void NuPhyKeyCode_FollowsNuPhyIoAndSoup(int type, int code0, int code1, int expected)
        {
            Assert.Equal(expected, NuPhyProtocol.NuPhyKeyCode((byte)type, (byte)code0, (byte)code1));
        }

        [Fact]
        public void NuPhyPass_OtherReportsAreNotKeys()
        {
            // StateChangeReport A1, KeyboardReset A2, KeyboardFuncCfgReport A3,
            // DongleConnection FA (raw offset 1541394), a late reply, and an A0
            // shorter than NuPhyIO's 20-byte minimum: none touches the state.
            var session = new NuPhyHeSession(NuPhy(0x6120));
            var io = new AnalogKeyboardTestTransport();
            var output = new AnalogKeyInputState();
            output.Set(AnalogKeyCodes.W, 0.5f);
            var late = NuPhyHeSession.ReadFuncRequest(0);
            late[1] = 0xAA;
            io.QueueInput(Report("a1000102"), Report("a2"), Report("a3000000000000000001"), Report("fafb0601"), late,
                new byte[] { 0x00, 0xA0, 0x10, 0x00, 0x2C, 0x06, 0x40, 0x01, 0x4A });
            for (int i = 0; i < 6; i++) Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            Assert.Equal(1, output.Count);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
        }

        [Fact]
        public void NuPhyPass_UnknownKeyEvent_CountsAsAReportButChangesNothing()
        {
            // A mode-switch function (0xF0FA00, KC_FN_SWITCH_M1 in NuPhyIO's
            // key table) is an event, but not a key this route reports.
            var session = new NuPhyHeSession(NuPhy(0x6120));
            var io = new AnalogKeyboardTestTransport();
            io.QueueInput(Report("a0f0fa000640014aee01ff000704014a086e0d17"));
            var output = new AnalogKeyInputState();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0, output.Count);
        }

        [Fact]
        public void NuPhyPass_QuietIsIdle_GoneIsFailed()
        {
            // PushedReportSession's rules for a pushed family.
            var session = new NuPhyHeSession(NuPhy(0x6120));
            var io = new AnalogKeyboardTestTransport();
            var output = new AnalogKeyInputState();
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            io.Gone = true;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
        }

        // ── Madlions ──

        [Fact]
        public void MadlionsStart_ReadsTheBlock_WritesItBackWithTheBit()
        {
            // device.py:98-111: GetFunc, then SetFunc of the same block with
            // only debugMode set (protocol.py:111-129).
            var kb = NanoKeyboard();
            var before = (byte[])kb.Func.Clone();
            var session = new MadlionsA0Session(Nano68Pro);
            Assert.True(session.Start(kb.Io));
            Assert.Equal(2, kb.Writes.Count);
            Assert.Equal(MadlionsA0Session.ReadBlockRequest(), kb.Writes[0]);
            var enabled = before.Take(56).ToArray();
            enabled[7] |= 0x08;
            Assert.Equal(MadlionsA0Session.WriteBlockRequest(enabled), kb.Writes[1]);
            for (int i = 0; i < 64; i++)
                Assert.Equal(i == 7 ? (byte)(before[i] | 0x08) : before[i], kb.Func[i]);
            Assert.True(session.ClearOnStop);
            Assert.Equal("Madlions Nano 68 Pro", session.ModelName);
            Assert.Equal(Nano68Pro.KeyOrder, session.KeyOrder);
        }

        [Fact]
        public void MadlionsStop_ReadsAgain_AndClearsTheBit()
        {
            // device.py:113-122 clears the bit on exit. The block is read again
            // first, so a brightness change made on the keyboard meanwhile
            // (frame byte 17, protocol.py:83) is kept.
            var kb = NanoKeyboard();
            var before = (byte[])kb.Func.Clone();
            var session = new MadlionsA0Session(Nano68Pro);
            Assert.True(session.Start(kb.Io));
            kb.Func[9] = 0x7F;
            session.Stop(kb.Io);
            var stop = kb.Writes.Skip(2).ToList();
            Assert.Equal(2, stop.Count);
            Assert.Equal(MadlionsA0Session.ReadBlockRequest(), stop[0]);
            Assert.Equal(0x7F, kb.Func[9]);
            Assert.False((kb.Func[7] & 0x08) != 0);
            before[9] = 0x7F;
            Assert.Equal(before, kb.Func);
            Assert.False(session.ClearOnStop);
        }

        [Fact]
        public void MadlionsStart_BitAlreadySet_WritesNothing_ButStopStillClearsIt()
        {
            // AnalogKeys clears the bit on exit whatever it was (device.py:117).
            var kb = NanoKeyboard();
            kb.Func[7] |= 0x08;
            var session = new MadlionsA0Session(Nano68Pro);
            Assert.True(session.Start(kb.Io));
            Assert.Single(kb.Writes);
            session.Stop(kb.Io);
            Assert.Equal(0, kb.Func[7] & 0x08);
        }

        [Fact]
        public void MadlionsStart_Unreadable_WritesNothing()
        {
            // "If the block cannot be read we refuse to write at all"
            // (device.py:3-7 and 143-147): three tries, no SetFunc.
            var kb = NanoKeyboard();
            var before = (byte[])kb.Func.Clone();
            kb.Silent = _ => true;
            var session = new MadlionsA0Session(Nano68Pro);
            Assert.False(session.Start(kb.Io));
            Assert.Equal(3, kb.Writes.Count);
            Assert.All(kb.Writes, w => Assert.Equal(MadlionsA0Session.ReadBlockRequest(), w));
            Assert.Equal(before, kb.Func);
            Assert.False(session.ClearOnStop);
        }

        [Fact]
        public void MadlionsStart_UnansweredWrite_IsUndone()
        {
            // The SetFunc lands but its reply never comes: Start fails and the
            // bit is cleared again, as device.py:113-122 would on exit.
            var kb = NanoKeyboard();
            var before = (byte[])kb.Func.Clone();
            kb.Silent = r => r[2] == NuPhyProtocol.SetFunc;
            var session = new MadlionsA0Session(Nano68Pro);
            Assert.False(session.Start(kb.Io));
            Assert.Equal(before, kb.Func);
        }

        [Fact]
        public void MadlionsStop_Unreadable_WritesTheStartBlockWithTheBitClear()
        {
            // AnalogKeys' own exit write, the block read at start (device.py:117).
            var kb = NanoKeyboard();
            var before = (byte[])kb.Func.Clone();
            var session = new MadlionsA0Session(Nano68Pro);
            Assert.True(session.Start(kb.Io));
            kb.Silent = r => r[2] == NuPhyProtocol.GetFunc;
            session.Stop(kb.Io);
            Assert.Equal(before, kb.Func);
        }

        [Fact]
        public void MadlionsEvents_UseTheKeyTable_TheModifierMask_AndAnalogKeysDepth()
        {
            // parse_event (protocol.py:173-194): byte 2 a modifier bit, byte 3
            // the firmware index, travel BE16 at 6..7 in 0.01 mm. Depth with
            // calibration.py's fallback: (travel - 4) / (345 - 8 - 4), clamped.
            var session = new MadlionsA0Session(Nano68Pro);
            var output = new AnalogKeyInputState();
            Assert.True(session.Apply(Event(0x10, 0x00, 0x04, 170), output));
            Assert.Equal((170 - 4) / 333f, output.Get(AnalogKeyCodes.A), 5);
            // Index 1 is Fn, whatever byte 1 holds, and bytes 14..15 play no part.
            Assert.True(session.Apply(Event(0xF0, 0x00, 0x01, 337, 120), output));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.Fn));
            Assert.True(session.Apply(Event(0x10, 0x02, 0x00, 200), output));
            Assert.Equal((200 - 4) / 333f, output.Get(AnalogKeyCodes.LShift), 5);
            // Several mask bits: AnalogKeys' status frame, not a key.
            Assert.True(session.Apply(Event(0xF0, 0xFF, 0x00, 200), output));
            Assert.Equal(3, output.Count);
            // Travel inside the 4-count top dead zone releases the key.
            Assert.True(session.Apply(Event(0x10, 0x00, 0x04, 4), output));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(2, output.Count);
            Assert.Equal(1f, NuPhyProtocol.MadlionsDepth(337));
            Assert.Equal(1f, NuPhyProtocol.MadlionsDepth(354));
            Assert.Equal(0f, NuPhyProtocol.MadlionsDepth(0));
        }

        [Fact]
        public void MadlionsKeyCode_OutsideTheTable_IsNoKey()
        {
            // "There is no real key with index 0" (AnalogKeys
            // docs/protocol.md:119-121), and indices past the keymap name no
            // key. A modifier bit wins over byte 3 (protocol.py:177-185).
            var table = Nano68Pro.Table;
            Assert.Equal(AnalogKeyCodes.None, NuPhyProtocol.MadlionsKeyCode(0, 83, table));
            Assert.Equal(AnalogKeyCodes.None, NuPhyProtocol.MadlionsKeyCode(0, 255, table));
            Assert.Equal(AnalogKeyCodes.None, NuPhyProtocol.MadlionsKeyCode(0, 0, table));
            Assert.Equal(AnalogKeyCodes.RAlt, NuPhyProtocol.MadlionsKeyCode(0x40, 0x10, table));
        }

        // ── Captures ──

        // universal-analog-plugin#31, comment 4100284940 (tvvoty, Air75 HE,
        // space bar), every row, bytes 0 to 19: the span every reference
        // parser reads.
        private static readonly string[] Air75HeRows =
        {
            "a010002c002500171801ff010002014a0cf10d17", "a010002c0029001a1a01ff010002014a0ced0d17", "a010002c003a00232301ff010002014a0cdb0d17",
            "a010002c0049002a2a01ff010002014a0ccc0d17", "a010002c005900313101ff010002014a0cbb0d17", "a010002c006b003a3a01ff010002014a0ca90d17",
            "a010002c008900474701ff010002014a0c8a0d17", "a010002c008600464901ff010002014a0c8d0d17", "a010002c0092004b4b01ff010002014a0c810d17",
            "a010002c009b004f4f01ff010002014a0c780d17", "a010002c00a100505101ff010002014a0c720d17", "a010002c00ab00545401ff010002014a0c670d17",
            "a010002c00b600575701ff010002014a0c5c0d17", "a010002c00d600636301ff010002014a0c3b0d17", "a010002c00ee006a6a01ff010002014a0c220d17",
            "a010002c00ff00707001ff010002014a0c110d17", "a010002c010300727201ff010002014a0c0d0d17", "a010002c011700770001ff010002014a0bf80d17",
            "a010002c0139007f0001ff010002014a0bd50d17", "a010002c015800878701ff010002014a0bb60d17", "a010002c019300950001ff010002014a0b790d17",
            "a010002c01c2009f0001ff010002014a0b490d17", "a010002c01f100a90101ff010002014a0b190d17", "a010002c022700b30001ff010002014a0ae10d17",
            "a010002c027700c00001ff010002014a0a8f0d17", "a010002c028b00c30001ff010002014a0a7a0d17", "a010002c02a700c70001ff010002014a0a5e0d17",
            "a010002c02ec00d10001ff010002014a0a170d17", "a010002c032000d80101ff010002014a09e10d17", "a010002c036700e20101ff010002014a09980d17",
            "a010002c039b00e70001ff010002014a09630d17", "a010002c03a200e80001ff010002014a095c0d17", "a010002c040b00f30101ff010002014a08f00d17",
            "a010002c046b00fc0001ff010002014a088d0d17", "a010002c0640014aee01ff000704014a086e0d17", "a010002c0640014aee01ff000704014a08050d17",
            "a010002c0640014aee01ff000704014a068d0d17", "a010002c0640014aee01ff000704014a068c0d17", "a010002c0640014aee01ff000704014a068c0d17",
            "a010002c0640014aee01ff000704014a068b0d17", "a010002c0640014aee01ff000704014a068b0d17", "a010002c0640014aee01ff000704014a068b0d17",
            "a010002c0640014aee01ff000704014a068a0d17", "a010002c0640014aee01ff000704014a06880d17", "a010002c0640014aee01ff000704014a06870d17",
            "a010002c0640014aee01ff000704014a06850d17", "a010002c0640014aee01ff000704014a06850d17", "a010002c0640014aee01ff000704014a06850d17",
            "a010002c0640014aee01ff000704014a06850d17", "a010002c0640014aee01ff000704014a06860d17", "a010002c0640014aee01ff000704014a06890d17",
            "a010002c0640014aee01ff000704014a068a0d17", "a010002c0640014aee01ff000704014a068a0d17", "a010002c0640014aee01ff000704014a06890d17",
            "a010002c0640014aee01ff000704014a068a0d17", "a010002c0640014aee01ff000704014a068a0d17", "a010002c0640014aee01ff000704014a068a0d17",
            "a010002c0640014aee01ff000704014a068b0d17", "a010002c0640014aee01ff000704014a068c0d17", "a010002c0640014aee01ff000704014a068c0d17",
            "a010002c0640014aee01ff000704014a068c0d17", "a010002c0640014aee01ff000704014a07840d17", "a010002c0517010c3effff000704014a094d0d17",
            "a010002c0513010c42ffff000704014a09500d17", "a010002c063801430201ff000704014a08760d17", "a010002c0640014aee01ff000704014a07d10d17",
            "a010002c0640014aee01ff000704014a080b0d17", "a010002c0640014aee01ff000704014a08050d17", "a010002c0640014aee01ff000801014a07e30d17",
            "a010002c0640014aee01ff000801014a07e30d17", "a010002c05d5012500ffff000801014a08590d17", "a010002c05b6012000ffff000801014a08720d17",
            "a010002c059a011b00ffff000801014a08890d17", "a010002c0592011a00ffff000801014a088f0d17", "a010002c0503010a00ffff000801014a09040d17",
            "a010002c04d5010545ffff000801014a09290d17", "a010002c04df010645ffff000801014a09210d17", "a010002c047d00fe00ffff000801014a09710d17",
            "a010002c042800f555ffff000801014a09b60d17", "a010002c041300f357ffff000801014a09c70d17", "a010002c035e00e100ffff000801014a0a5a0d17",
            "a010002c02e200d000ffff000801014a0abf0d17", "a010002c02ad00c882ffff000801014a0aea0d17", "a010002c02ac00c883ffff000801014a0aeb0d17",
            "a010002c026e00bf00ffff000801014a0b1d0d17", "a010002c023400b595ffff000801014a0b4c0d17", "a010002c023400b596ffff000801014a0b4c0d17",
            "a010002c018e0094b6ffff000801014a0bd30d17", "a010002c011b007801ffff000801014a0c310d17", "a010002c012c007c01ffff000801014a0c230d17",
            "a010002c0131007d0801ff000801014a0c1f0d17", "a010002c0101007101ffff000801014a0c460d17", "a010002c00ed006a01ffff000801014a0c560d17",
            "a010002c00ba005a01ffff000801014a0c800d17", "a010002c0082004501ffff000801014a0cad0d17", "a010002c0055003002ffff000801014a0cd20d17",
            "a010002c0036002202ffff000801014a0ceb0d17", "a010002c0031001e02ffff000801014a0cef0d17", "a010002c0007000000ffff000801014a0d110d17",
        };

        // universal-analog-plugin#31, comment 3970755817 (foodpolicy, WH80:
        // W, A, S, D, Left Ctrl, C), every row, bytes 0 to 19.
        private static readonly string[] Wh80Rows =
        {
            "a010001a00a400157801ff0104050155a065050f", "a010001a00b500188201ff0104050155a010050f", "a010001a00f3001fdc01ff01040501559f66050f",
            "a010001a01040021f001ff01040501559f2a050f", "a010001a012500251801ff01040501559ed0050f", "a010001a012b00261801ff01040501559ec1050f",
            "a010001a013600272c01ff01040501559eaf050f", "a010001a014200293601ff01040501559e70050f", "a010001a015e002b5401ff01040501559e3a050f",
            "a010001a01ce0037c201ff01040501559d36050f", "a010001a023400413001ff01040501559c16050f", "a010001a02dd0051d001ff01040501559a34050f",
            "a010001a031500560201ff0104050155997d050f", "a010001a037a005f5201ff01040501559857050f", "a010001a03a200627a01ff010405015597ea050f",
            "a010001a0440006d5a01ff0104050155965e050f", "a010001a0526007efa01ff010405015593e6050f", "a010001a073e009f4e01ff01040501558dfd050f",
            "a010001a089b00b03c01ff010405015589c9050f", "a010001a140001134001ff01040501556b5e050f", "a010001a1e84014e0a01ff01040501554b9f050f",
            "a010001a1e95014f0a01ff01040501554b35050f", "a010001a1d0501441401ff01040501554e82050f", "a010001a1845012c5effff01040501555a5d050f",
            "a010001a01f6003b00ffff01040501559c31050f", "a010001a0001000000ffff0104050155a293050f", "a010000400ac00168c01ff01030801559afc04e4",
            "a0100004011600230e01ff010308015599ee04e4", "a01000040151002a5401ff0103080155993c04e4", "a01000040180002f7c01ff010308015598a304e4",
            "a0100004018c00309001ff0103080155986104e4", "a010000401aa0033ae01ff0103080155982e04e4", "a0100004024300424401ff010308015596bb04e4",
            "a010000402b9004db201ff0103080155957604e4", "a010000403000054f801ff0103080155948c04e4", "a0100004032400571601ff0103080155943504e4",
            "a01000040353005c4801ff010308015593d104e4", "a01000040457006f6e01ff0103080155915b04e4", "a010000404fc007be601ff01030801558f8304e4",
            "a010000406820094e001ff01030801558b3e04e4", "a010000407b500a58001ff0103080155882b04e4", "a01000040b3600ce6801ff01030801557df704e4",
            "a01000040f1c00f10a01ff010308015573ff04e4", "a01000041ec501553c01ff0103080155496d04e4", "a01000041ed101553c01ff0103080155491804e4",
            "a0100004196b01322cffff0103080155548e04e4", "a01000040703009b0affff0103080155888b04e4", "a01000040000000000ffff01030801559e4804e4",
            "a0100016009d00147801ff0104090155a3010525", "a010001601050022f001ff0104090155a2220525", "a0100016014c002a5401ff0104090155a1330525",
            "a0100016017e002f8601ff0104090155a0720525", "a0100016018900308601ff0104090155a0510525", "a010001601b40034b801ff01040901559ffd0525",
            "a010001601c50036cc01ff01040901559fae0525", "a010001601db0038d601ff01040901559f630525", "a0100016021d003f2601ff01040901559ec10525",
            "a0100016025900455801ff01040901559e350525", "a010001603ad00649801ff01040901559a2a0525", "a01000160512007cdc01ff010409015596700525",
            "a010001607f100a89e01ff01040901558ddf0525", "a010001609c300bebe01ff010409015588350525", "a01000160d5800e22601ff01040901557d8b0525",
            "a01000160fda00f73201ff010409015576960525", "a01000161b25013a1e01ff010409015556c20525", "a01000161e62014d1401ff01040901554c000525",
            "a01000161eb401551401ff01040901554b3f0525", "a01000161eca01553201ff01040901554afa0525", "a01000161911013040ffff010409015557af0525",
            "a010001601150023a0ffff0104090155a0fd0525", "a01000160000000000ffff0104090155a5640525", "a010000700e2001dc801ff0104070155a362052d",
            "a010000700f80020e601ff0104070155a319052d", "a0100007011400230e01ff0104070155a2b3052d", "a0100007012400252201ff0104070155a27d052d",
            "a0100007013500273601ff0104070155a259052d", "a01000070156002b5e01ff0104070155a1e1052d", "a0100007018800308601ff0104070155a189052d",
            "a010000701c00035c201ff0104070155a0dc052d", "a010000701ec003af401ff0104070155a06b052d", "a010000702ca004fb201ff01040701559e1d052d",
            "a0100007032900571601ff01040701559cfb052d", "a010000703b400649801ff01040701559b6c052d", "a010000704eb0079d201ff010407015597cb052d",
            "a01000070be800d49a01ff010407015584e5052d", "a010000714de01197201ff01040701556be8052d", "a01000071ec401551401ff01040701554d2d052d",
            "a01000071ec901551401ff01040701554d24052d", "a01000071ee501551e01ff01040701554cd3052d", "a01000071f0101551e01ff01040701554c8d052d",
            "a01000071ab501381e01ff010407015553c4052d", "a01000070000000000ffff0104070155a98b052d", "a0100100019700317c01ff01040701559d590510",
            "a01001000a1100c26e01ff0104070155862c0510", "a0100100151b011a2201ff0104070155699b0510", "a01001001ec701551401ff010407015549a90510",
            "a01001001ecc01551401ff0104070155499f0510", "a010000603b20064a201ff0104030155961e04fc", "a01000061ec901550001ff01040301554a3304fc",
            "a01000061e57014c32ffff01040301554b2104fc", "a01000060ddd00e66effff0104030155767604fc", "a0100006058d008564ffff01040301558e3604fc",
            "a01000060000000000ffff0104030155a01504fc", "a01001001dd701482801ff01040701554b870510", "a01001000711009c50ffff01040701558b960510",
            "a01001000000000000ffff0104070155a22c0510", "a01001000433006cde01ff0104070155971c0510", "a01001000e9000ec0401ff01040701557b630510",
            "a01001001ec701551401ff010407015549a60510", "a01001001ecc01551401ff0104070155499b0510", "a01001001d6801461e01ff01040701554c040510",
            "a010010018ff01304affff010407015554e60510", "a01001000000000000ffff0104070155a2b90510", "a0100100026a00465801ff01040701559c1e0510",
            "a010010003da0067ac01ff010407015598a50510", "a01001001ec701550a01ff010407015549ae0510", "a01001001ecc01550a01ff010407015549990510",
            "a010000603c30065a201ff0104030155965d04fc", "a01000061d050144d601ff010403015559ca04fc", "a01000061ecf01550a01ff0104030155497704fc",
            "a01000061e8a014e1effff01040301554a8f04fc", "a0100006190b01304affff0104030155547e04fc", "a0100006000d000046ffff0104030155a01404fc",
            "a0100100187f012d54ffff010407015554fe0510", "a01001000017000000ffff0104070155a16a0510",
        };
    }
}
