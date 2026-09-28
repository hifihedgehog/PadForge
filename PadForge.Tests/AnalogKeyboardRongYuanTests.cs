using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Common.AnalogKeyboard;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// HallJoy's RY5088 feature-report routes (issue #468): ATTACK SHARK, the
    /// MonsGeek and EPOMAKER snapshot boards, and the RongYuan event stream,
    /// pinned against byte fixtures from HallJoy's source (AGPL-3.0, commit
    /// 378f9fe) and its own test vectors. No such keyboard is on the bench.
    /// Buffers are what Windows hands over: byte 0 is the report ID, 0 for
    /// these unnumbered reports. Each test names the HallJoy lines it pins.
    /// </summary>
    public class AnalogKeyboardRongYuanTests
    {
        private const ushort Vid = 0x3151;

        // ── Fixtures ──

        /// <summary>A deterministic clock whose waits advance it, so a query
        /// that runs out of time ends without real sleeping.</summary>
        private sealed class FakeTime
        {
            public long Now = 1000;
            public readonly List<int> Waits = new();
            public long Clock() => Now;
            public void Wait(int ms)
            {
                Waits.Add(ms);
                Now += ms;
            }
        }

        private static byte[] Bytes(string hex)
            => hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray();

        private static byte[] Frame(string head, byte tail)
        {
            var r = new byte[65];
            var h = Bytes(head);
            Array.Copy(h, r, h.Length);
            for (int i = h.Length; i < 65; i++) r[i] = tail;
            return r;
        }

        private static void PutLe16(byte[] r, int at, int value)
        {
            r[at] = (byte)value;
            r[at + 1] = (byte)(value >> 8);
        }

        private static void PutLe32(byte[] r, int at, uint value)
        {
            r[at] = (byte)value;
            r[at + 1] = (byte)(value >> 8);
            r[at + 2] = (byte)(value >> 16);
            r[at + 3] = (byte)(value >> 24);
        }

        /// <summary>A scripted ATTACK SHARK: 8F answers the board ID and the
        /// USB version at bytes 8 and 9, 80 the RF version, E5 the page asked.</summary>
        private sealed class SharkBoard
        {
            public uint Id = 2308;
            public int Usb = 0x0314;
            public int Rf;
            public readonly int[][] Pages = { new int[32], new int[32], new int[32], new int[32] };
            /// <summary>Answers the next request in place of the board when set.</summary>
            public Func<byte[], byte[]> Next;

            public AnalogKeyboardTestTransport Transport()
            {
                var io = new AnalogKeyboardTestTransport();
                io.OnSetFeature = req =>
                {
                    var answer = Next != null ? Next(req) : Answer(req);
                    return answer == null ? Array.Empty<byte[]>() : new[] { answer };
                };
                return io;
            }

            public byte[] Answer(byte[] req)
            {
                var r = new byte[65];
                switch (req[1])
                {
                    case 0x8F:
                        r[1] = 0x8F;
                        PutLe32(r, 2, Id);
                        PutLe16(r, 8, Usb);
                        return r;
                    case 0x80:
                        r[1] = 0x80;
                        PutLe16(r, 2, Rf);
                        return r;
                    case 0xE5:
                        var page = Pages[req[4]];
                        for (int i = 0; i < 32; i++) PutLe16(r, 1 + 2 * i, page[i]);
                        return r;
                }
                return null;
            }
        }

        private static AttackSharkSession StartedShark(SharkBoard board, FakeTime time, out AnalogKeyboardTestTransport io,
            ushort pid = 0x502F)
        {
            io = board.Transport();
            var session = new AttackSharkSession(pid, time.Clock, time.Wait);
            Assert.True(session.Start(io));
            return session;
        }

        /// <summary>A scripted RY5088 control collection. The firmware answers
        /// in the buffer the request arrived in, so a read with no answer
        /// queued returns the request itself (rongyuan_snapshot_protocol.h:133-137).
        /// Windows reports 64 bytes for a full unnumbered report.</summary>
        private sealed class RyBoard
        {
            public uint Board = 2819;
            public int Version = 0x0410;
            /// <summary>The 80 answer's version, or null for no answer.</summary>
            public int? Radio;
            /// <summary>The E6 precision byte, or null for firmware without E6.</summary>
            public int? Precision = 0;
            public byte Profile;
            public int[] Codes;
            public readonly int[][] Pages = { new int[32], new int[32], new int[32], new int[32] };
            public Func<byte[], byte[]> Next;
            private byte[] _last = new byte[65];

            public AnalogKeyboardTestTransport Transport()
            {
                var io = new AnalogKeyboardTestTransport { FeatureTransferred = a => a.Length - 1 };
                io.OnSetFeature = req =>
                {
                    _last = (byte[])req.Clone();
                    var answer = Next != null ? Next(req) : Answer(req);
                    return answer == null ? Array.Empty<byte[]>() : new[] { answer };
                };
                io.OnGetFeature = _ => (byte[])_last.Clone();
                return io;
            }

            public byte[] Answer(byte[] req)
            {
                var r = new byte[65];
                switch (req[1])
                {
                    case 0x8F:
                        r[1] = 0x8F;
                        PutLe32(r, 2, Board);
                        PutLe16(r, 8, Version);
                        return r;
                    case 0x80:
                        if (Radio == null) return null;
                        r[1] = 0x80;
                        PutLe16(r, 2, Radio.Value);
                        return r;
                    case 0xE6:
                        if (Precision == null) return null;
                        r[1] = 0xE6;
                        r[2] = 0xAA;
                        r[3] = (byte)Precision.Value;
                        return r;
                    case 0x84:
                        r[1] = 0x84;
                        r[2] = Profile;
                        return r;
                    case 0x8A:
                        // Sixteen 4-byte codes: 00 00 usage 00, or 0A 01 00 00 for Fn.
                        for (int k = 0; k < 16; k++)
                        {
                            int code = Codes == null ? 0 : Codes[req[4] * 16 + k];
                            if (code == AnalogKeyCodes.Fn)
                            {
                                r[1 + 4 * k] = 10;
                                r[2 + 4 * k] = 1;
                            }
                            else
                            {
                                r[3 + 4 * k] = (byte)code;
                            }
                        }
                        return r;
                    case 0xE5:
                        var page = Pages[req[4]];
                        for (int i = 0; i < 32; i++) PutLe16(r, 1 + 2 * i, page[i]);
                        return r;
                }
                return null;
            }
        }

        /// <summary>Forwards to a scripted transport but can fail chosen
        /// feature writes, logging every attempt.</summary>
        private sealed class FailingWrites : IAnalogKeyboardTransport
        {
            private readonly AnalogKeyboardTestTransport _inner;
            public Func<byte[], bool> Fail = _ => false;
            public readonly List<byte[]> Attempts = new();

            public FailingWrites(AnalogKeyboardTestTransport inner) => _inner = inner;

            public bool SetFeature(byte[] report)
            {
                Attempts.Add((byte[])report.Clone());
                return !Fail(report) && _inner.SetFeature(report);
            }

            public bool Send(byte[] report) => _inner.Send(report);
            public bool SendOutputReport(byte[] report) => _inner.SendOutputReport(report);
            public int Receive(byte[] buffer, int timeoutMs) => _inner.Receive(buffer, timeoutMs);
            public void DiscardStale() => _inner.DiscardStale();
            public int GetFeature(byte[] buffer) => _inner.GetFeature(buffer);
            public int InputLength => _inner.InputLength;
            public int OutputLength => _inner.OutputLength;
            public int FeatureLength => _inner.FeatureLength;
        }

        private static byte[] StreamEvent(int slot, int raw, byte reportId = 5, byte type = 0x1B, int length = 32)
        {
            var r = new byte[length];
            if (length > 0) r[0] = reportId;
            if (length > 1) r[1] = type;
            if (length > 3) PutLe16(r, 2, raw);
            if (length > 4) r[4] = (byte)slot;
            return r;
        }

        private static RongYuanStreamSession StartedStream(RyBoard board, FakeTime time, ushort vid, ushort pid,
            out AnalogKeyboardTestTransport io)
        {
            board.Codes ??= RongYuanCatalog.FindStream(board.Board, vid, pid).Codes;
            io = board.Transport();
            var session = new RongYuanStreamSession(vid, pid, time.Clock, time.Wait);
            Assert.True(session.Start(io));
            return session;
        }

        private static AnalogKeyboardDeviceInfo Collection(ushort vid, ushort pid, ushort usagePage, ushort usage,
            string container = "5a1d0b35-60c1-4bd4-8ad5-3e5c5c2f2a10", int mi = 2)
            => new()
            {
                Path = $@"\\?\hid#vid_{vid:x4}&pid_{pid:x4}&mi_{mi:x2}#8&2f1d7a3b&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
                VendorId = vid,
                ProductId = pid,
                UsagePage = usagePage,
                Usage = usage,
                InterfaceNumber = mi,
                FeatureReportLength = 65,
                ProductString = "USB Keyboard",
                ContainerId = container,
            };

        private static AnalogKeyboardDeviceInfo StreamInputCollection(ushort vid, ushort pid,
            string container = "5a1d0b35-60c1-4bd4-8ad5-3e5c5c2f2a10", byte reportId = 5, ushort bitSize = 8,
            ushort count = 31, ushort inputLength = 32, ushort usage = 1)
        {
            var info = Collection(vid, pid, 0xFFFF, usage, container, mi: 3);
            info.FeatureReportLength = 0;
            info.InputReportLength = inputLength;
            info.InputValueCaps = new[] { new AnalogKeyboardValueCap(reportId, 0xFFFF, bitSize, count) };
            return info;
        }

        private static void Link(params AnalogKeyboardDeviceInfo[] infos)
        {
            foreach (var info in infos) info.Siblings = infos.Where(o => !ReferenceEquals(o, info)).ToArray();
        }

        // ── Routes and catalogs ──

        [Fact]
        public void All_IsAttackSharkThenSnapshotThenStream()
        {
            // native_analog_backends.def:14, 43, 44: ATTACK SHARK leads the
            // catalog, the snapshot route precedes the stream route. Handles:
            // attackshark_pro_diagnostic.cpp:228, rongyuan_snapshot_backend.cpp:166-169,
            // rongyuan_stream_backend.cpp:189-192 and 228-231.
            var all = RongYuanRoutes.All;
            Assert.Equal(new[] { "attackshark-pro", "rongyuan-snapshot", "rongyuan-stream" }, all.Select(r => r.Id));
            Assert.Equal(new[]
            {
                AnalogKeyboardProtocol.AttackShark, AnalogKeyboardProtocol.RongYuanSnapshot,
                AnalogKeyboardProtocol.RongYuanStream,
            }, all.Select(r => r.Protocol));
            Assert.All(all, r => Assert.True(r.Writable && r.Exclusive));
            Assert.Null(all[0].Companion);
            Assert.Null(all[1].Companion);
            Assert.NotNull(all[2].Companion);
            Assert.Equal(128, all[2].InputBuffers);
            Assert.Equal(0, all[0].InputBuffers);
        }

        [Fact]
        public void AttackSharkCatalog_ThirtySevenProfiles_OneFnSlotAndWasd()
        {
            // attackshark_pro_diagnostic_model_test.cpp:8, 19-20 and
            // generate_attackshark_family.py:41-42 via the spec: 37 profiles,
            // Fn (0x409) at the Fn slot, slot 127 empty, W A S D at 14 9 15 21.
            var profiles = RongYuanCatalog.AttackSharkProfiles;
            Assert.Equal(37, profiles.Count);
            Assert.Equal(37, profiles.Select(p => p.Id).Distinct().Count());
            foreach (var p in profiles)
            {
                Assert.Equal(128, p.Factory.Length);
                Assert.Equal(128, p.Fn.Length);
                Assert.Equal(AnalogKeyCodes.Fn, p.Factory[p.FnSlot]);
                Assert.Single(p.Factory, c => c == AnalogKeyCodes.Fn);
                Assert.Equal(0, p.Factory[127]);
                Assert.Equal(new[] { AnalogKeyCodes.W, AnalogKeyCodes.A, AnalogKeyCodes.S, AnalogKeyCodes.D },
                    new[] { p.Factory[14], p.Factory[9], p.Factory[15], p.Factory[21] });
            }
            Assert.Equal(new ushort[] { 0x5029, 0x502D, 0x502F, 0x5030 },
                profiles.Select(p => p.ProductId).Distinct().OrderBy(x => x));
            // attackshark_pro_native_model.h:180-183, the spec's "page 3 every
            // sweep" column.
            Assert.Equal(new uint[] { 2660, 2792, 2964, 3737, 3743, 3748, 3754 },
                profiles.Where(p => p.FourthPage).Select(p => p.Id).OrderBy(x => x));
        }

        [Fact]
        public void AttackSharkTables_SpotChecks()
        {
            // attackshark_pro_native_model.h:13-15: X65 Pro HE, 67 factory
            // keys with Fn, 18 Fn-layer keys, 1 over F1 and Grave over Esc.
            var x65 = RongYuanCatalog.FindAttackShark(2308);
            Assert.Equal("ATTACK SHARK X65 Pro HE", x65.Name);
            Assert.Equal(0x502F, x65.ProductId);
            Assert.Equal(65, x65.FnSlot);
            Assert.Equal(67, x65.Factory.Count(c => c != 0));
            Assert.Equal(18, x65.Fn.Count(c => c != 0));
            Assert.Equal(AnalogKeyCodes.D1, x65.Factory[7]);
            Assert.Equal(AnalogKeyCodes.F1, x65.Fn[7]);
            Assert.Equal(AnalogKeyCodes.Backquote, x65.Fn[1]);
            Assert.Equal(AnalogKeyCodes.PrintScreen, x65.Fn[44]);
            Assert.Equal(AnalogKeyCodes.IntlBackslash, x65.Factory[10]);
            // attackshark_pro_native_model.h:141: the R68 HE's catalog PID.
            var r68 = RongYuanCatalog.FindAttackShark(3650);
            Assert.Equal(0x502D, r68.ProductId);
            Assert.Equal("ATTACK SHARK R68HE", r68.Name);
            // attackshark_pro_native_model.h:129: the hardware-tested R85 HE.
            Assert.Equal(0x5029, RongYuanCatalog.FindAttackShark(3123).ProductId);
            Assert.Null(RongYuanCatalog.FindAttackShark(1466));
        }

        [Fact]
        public void SnapshotCatalog_ThreeBoards_KeyCountsAndFnSlots()
        {
            // three_keyboard_protocol_test.cpp:41-44 and rongyuan_snapshot_protocol.h:106-109.
            var boards = RongYuanCatalog.SnapshotModels;
            Assert.Equal(new uint[] { 2819, 2642, 2959 }, boards.Select(b => b.Board));
            Assert.Equal(new[] { 82, 84, 84 }, boards.Select(b => b.Codes.Count(c => c != 0)));
            Assert.Equal(new[] { 65, 71, 71 }, boards.Select(b => Array.IndexOf(b.Codes, AnalogKeyCodes.Fn)));
            Assert.Equal(new[] { 3600, 3500, 3500 }, boards.Select(b => b.RangeUm));
            Assert.Equal(new[] { "MonsGeek M1 V5 HE", "EPOMAKER G84 HE", "EPOMAKER G84 HE" }, boards.Select(b => b.Name));
            // Esc, Grave, Tab, Caps Lock, Left Shift, Left Ctrl in slots 0 to 5.
            Assert.Equal(new[] { 41, 53, 43, 57, 225, 224 }, boards[0].Codes.Take(6));
            // three_keyboard_protocol_test.cpp:40: neither the TMR nor an ATTACK SHARK board.
            Assert.Null(RongYuanCatalog.FindSnapshot(2949));
            Assert.Null(RongYuanCatalog.FindSnapshot(2308));
        }

        [Fact]
        public void StreamCatalog_Rows_Aliases_AndTableAnomalies()
        {
            // rongyuan_stream_protocol_test.cpp:36-45 and 78: 253 rows, WASD in each.
            var rows = RongYuanCatalog.StreamModels;
            Assert.Equal(253, rows.Count);
            Assert.Equal(253, rows.Select(r => r.Board).Distinct().Count());
            Assert.All(rows, r => Assert.Subset(r.Codes.ToHashSet(),
                new HashSet<int> { AnalogKeyCodes.W, AnalogKeyCodes.A, AnalogKeyCodes.S, AnalogKeyCodes.D }));
            // rongyuan_stream_protocol.h:265.
            Assert.Equal(3, RongYuanCatalog.StreamAliases.Count);
            // The spec's range and precision counts (section 4.3 and 4.7).
            Assert.Equal(193, rows.Count(r => r.RangeUm == 4000));
            Assert.Equal(30, rows.Count(r => r.RangeUm == 3300));
            Assert.Equal(16, rows.Count(r => r.RangeUm == 3400));
            Assert.Equal(14, rows.Count(r => r.RangeUm == 3500));
            Assert.Equal(14, rows.Count(r => r.PrecisionEnum));
            // Spec section 4.4: board 3026 has Space at slots 41 and 47, board
            // 3714 binds HID 2 and 3 as HallJoy's Decode yields them, and the
            // Cybrix29 binds 31 positions.
            var matataki = RongYuanCatalog.FindStream(3026, Vid, 0x5029);
            Assert.Equal(new[] { 41, 47 }, Enumerable.Range(0, 128).Where(i => matataki.Codes[i] == AnalogKeyCodes.Space));
            var magic80 = RongYuanCatalog.FindStream(3714, Vid, 0x5030);
            Assert.Equal(2, magic80.Codes[78]);
            Assert.Equal(3, magic80.Codes[84]);
            Assert.Equal(31, RongYuanCatalog.FindStream(2886, Vid, 0x5029).Codes.Count(c => c != 0));
            // Board 2642 is also a stream row: ANTGAMER AGK75 U2, Fn at 59, E6 required.
            var antgamer = RongYuanCatalog.FindStream(2642, Vid, 0x5030);
            Assert.Equal("ANTGAMER AGK75 U2", antgamer.Name);
            Assert.True(antgamer.PrecisionEnum);
            Assert.Equal(59, Array.IndexOf(antgamer.Codes, AnalogKeyCodes.Fn));
            Assert.Equal(83, antgamer.Codes.Count(c => c != 0));
        }

        // ── Matches ──

        [Fact]
        public void AttackSharkMatches_EachProductId_AndRejectsNearMisses()
        {
            // attackshark_pro_diagnostic.cpp:188-209: path "vid_3151&pid_",
            // VID 3151, no receiver or dongle, a profile PID, FFFF:0002, feature 65.
            foreach (ushort pid in new ushort[] { 0x5029, 0x502D, 0x502F, 0x5030 })
                Assert.True(RongYuanRoutes.AttackSharkMatches(Collection(Vid, pid, 0xFFFF, 2)));

            var wrongPage = Collection(Vid, 0x502F, 0xFF00, 2);
            var wrongUsage = Collection(Vid, 0x502F, 0xFFFF, 1);
            var shortFeature = Collection(Vid, 0x502F, 0xFFFF, 2);
            shortFeature.FeatureReportLength = 64;
            var receiver = Collection(Vid, 0x5030, 0xFFFF, 2);
            receiver.ProductString = "2.4G Wireless Receiver";
            var dongle = Collection(Vid, 0x5030, 0xFFFF, 2);
            dongle.ProductString = "USB DONGLE";
            var bluetooth = Collection(Vid, 0x5030, 0xFFFF, 2);
            bluetooth.Path = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&00023151_pid&5030&col02#9&1a2b3c4d&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            var otherVendor = Collection(0x3152, 0x5030, 0xFFFF, 2);
            Assert.False(RongYuanRoutes.AttackSharkMatches(wrongPage));
            Assert.False(RongYuanRoutes.AttackSharkMatches(wrongUsage));
            Assert.False(RongYuanRoutes.AttackSharkMatches(shortFeature));
            Assert.False(RongYuanRoutes.AttackSharkMatches(Collection(Vid, 0x5031, 0xFFFF, 2)));
            Assert.False(RongYuanRoutes.AttackSharkMatches(Collection(Vid, 0x5054, 0xFFFF, 2)));
            Assert.False(RongYuanRoutes.AttackSharkMatches(receiver));
            Assert.False(RongYuanRoutes.AttackSharkMatches(dongle));
            Assert.False(RongYuanRoutes.AttackSharkMatches(bluetooth));
            Assert.False(RongYuanRoutes.AttackSharkMatches(otherVendor));
            Assert.False(RongYuanRoutes.AttackSharkMatches(null));
            // An unreadable product string rejects nothing (line 206).
            var unnamed = Collection(Vid, 0x5030, 0xFFFF, 2);
            unnamed.ProductString = string.Empty;
            Assert.True(RongYuanRoutes.AttackSharkMatches(unnamed));
        }

        [Fact]
        public void SnapshotMatches_TheControlCollectionOf5030Only()
        {
            // rongyuan_snapshot_backend.cpp:131-142: 3151:5030, usage page
            // FFFF or FF00, usage 2, feature 65. No strings.
            Assert.True(RongYuanRoutes.SnapshotMatches(Collection(Vid, 0x5030, 0xFFFF, 2)));
            Assert.True(RongYuanRoutes.SnapshotMatches(Collection(Vid, 0x5030, 0xFF00, 2)));
            var shortFeature = Collection(Vid, 0x5030, 0xFFFF, 2);
            shortFeature.FeatureReportLength = 64;
            Assert.False(RongYuanRoutes.SnapshotMatches(shortFeature));
            Assert.False(RongYuanRoutes.SnapshotMatches(Collection(Vid, 0x5029, 0xFFFF, 2)));
            Assert.False(RongYuanRoutes.SnapshotMatches(Collection(Vid, 0x5030, 0xFFFF, 1)));
            Assert.False(RongYuanRoutes.SnapshotMatches(Collection(Vid, 0x5030, 0xFF60, 2)));
            Assert.False(RongYuanRoutes.SnapshotMatches(Collection(0x374A, 0x5030, 0xFFFF, 2)));
        }

        [Fact]
        public void StreamMatches_NeedsExactlyOnePairedInputCollection()
        {
            // rongyuan_stream_backend.cpp:142-175: a candidate USB identity,
            // the control collection, a container, and exactly one input
            // collection FFFF:0001 of 32 bytes with one value cap (report 5,
            // 8 bits, 31 values) in the same container.
            foreach (var (vid, pid) in new[] { (Vid, (ushort)0x5029), ((ushort)0x046A, (ushort)0x0141),
                         ((ushort)0x374A, (ushort)0xA222), ((ushort)0x379A, (ushort)0x1803) })
            {
                var control = Collection(vid, pid, 0xFFFF, 2);
                var input = StreamInputCollection(vid, pid);
                Link(control, input);
                Assert.True(RongYuanRoutes.StreamMatches(control));
                Assert.Same(input, RongYuanRoutes.Stream.Companion(control));
                // The input collection itself is not a control collection.
                Assert.False(RongYuanRoutes.StreamMatches(input));
            }

            var ff00 = Collection(Vid, 0x5030, 0xFF00, 2);
            Link(ff00, StreamInputCollection(Vid, 0x5030));
            Assert.True(RongYuanRoutes.StreamMatches(ff00));

            bool With(AnalogKeyboardDeviceInfo control, params AnalogKeyboardDeviceInfo[] siblings)
            {
                Link(new[] { control }.Concat(siblings).ToArray());
                return RongYuanRoutes.StreamMatches(control);
            }
            Assert.False(With(Collection(Vid, 0x5030, 0xFFFF, 2)));
            Assert.False(With(Collection(Vid, 0x5030, 0xFFFF, 2), StreamInputCollection(Vid, 0x5030),
                StreamInputCollection(Vid, 0x5030)));
            Assert.False(With(Collection(Vid, 0x5030, 0xFFFF, 2), StreamInputCollection(Vid, 0x5030, reportId: 4)));
            Assert.False(With(Collection(Vid, 0x5030, 0xFFFF, 2), StreamInputCollection(Vid, 0x5030, bitSize: 16)));
            Assert.False(With(Collection(Vid, 0x5030, 0xFFFF, 2), StreamInputCollection(Vid, 0x5030, count: 30)));
            Assert.False(With(Collection(Vid, 0x5030, 0xFFFF, 2), StreamInputCollection(Vid, 0x5030, inputLength: 33)));
            Assert.False(With(Collection(Vid, 0x5030, 0xFFFF, 2), StreamInputCollection(Vid, 0x5030, usage: 2)));
            var twoCaps = StreamInputCollection(Vid, 0x5030);
            twoCaps.InputValueCaps = new[]
            {
                new AnalogKeyboardValueCap(5, 0xFFFF, 8, 31), new AnalogKeyboardValueCap(5, 0xFFFF, 8, 31),
            };
            Assert.False(With(Collection(Vid, 0x5030, 0xFFFF, 2), twoCaps));
            // Other container, no container, unknown USB identity.
            var control2 = Collection(Vid, 0x5030, 0xFFFF, 2);
            control2.Siblings = new[] { StreamInputCollection(Vid, 0x5030, container: "00000000-1111-2222-3333-444444444444") };
            Assert.False(RongYuanRoutes.StreamMatches(control2));
            Assert.False(With(Collection(Vid, 0x5030, 0xFFFF, 2, container: string.Empty),
                StreamInputCollection(Vid, 0x5030, container: string.Empty)));
            Assert.False(With(Collection(Vid, 0x5031, 0xFFFF, 2), StreamInputCollection(Vid, 0x5031)));
            var shortFeature = Collection(Vid, 0x5030, 0xFFFF, 2);
            shortFeature.FeatureReportLength = 33;
            Assert.False(With(shortFeature, StreamInputCollection(Vid, 0x5030)));
        }

        [Fact]
        public void Board2642Keyboard_MatchesAllThreeRoutes_SnapshotBeforeStream()
        {
            // native_analog_backends.def:14, 43-44 and the spec's section 9
            // item 1: a 3151:5030 control collection with a stream input
            // sibling is offered to ATTACK SHARK, then the snapshot route,
            // then the stream route.
            var control = Collection(Vid, 0x5030, 0xFFFF, 2);
            Link(control, StreamInputCollection(Vid, 0x5030));
            Assert.Equal(new[] { "attackshark-pro", "rongyuan-snapshot", "rongyuan-stream" },
                RongYuanRoutes.All.Where(r => r.Matches(control)).Select(r => r.Id));

            // The snapshot route proves board 2642 as the EPOMAKER G84 HE.
            var time = new FakeTime();
            var board = new RyBoard { Board = 2642, Codes = RongYuanCatalog.FindSnapshot(2642).Codes };
            var snapshot = new RongYuanSnapshotSession(time.Clock, time.Wait);
            Assert.True(snapshot.Start(board.Transport()));
            Assert.Equal("EPOMAKER G84 HE", snapshot.ModelName);
            // The ANTGAMER row applies only through the stream route, and needs E6.
            var stream = StartedStream(new RyBoard { Board = 2642, Precision = 1 }, new FakeTime(), Vid, 0x5030, out _);
            Assert.Equal("ANTGAMER AGK75 U2", stream.ModelName);
        }

        // ── Request frames ──

        [Fact]
        public void AttackSharkRequests_ByteForByte()
        {
            // attackshark_pro_diagnostic_model.h:18-25, compiled frames in the
            // spec's section 2.3, and attackshark_pro_diagnostic_model_test.cpp:48-50.
            Assert.Equal(Frame("00 8F 00 00 00 00 00 00 70", 0), AttackSharkSession.Request(0x8F));
            Assert.Equal(Frame("00 80 00 00 00 00 00 00 7F", 0), AttackSharkSession.Request(0x80));
            Assert.Equal(Frame("00 E5 FE 01 00 00 00 00 1B", 0), AttackSharkSession.Request(0xE5, 0));
            Assert.Equal(Frame("00 E5 FE 01 01 00 00 00 1A", 0), AttackSharkSession.Request(0xE5, 1));
            Assert.Equal(Frame("00 E5 FE 01 02 00 00 00 19", 0), AttackSharkSession.Request(0xE5, 2));
            Assert.Equal(Frame("00 E5 FE 01 03 00 00 00 18", 0), AttackSharkSession.Request(0xE5, 3));
            for (int command = 0; command < 256; command++)
                Assert.Equal(command is 0x8F or 0x80 or 0xE5, AttackSharkSession.Request(command)[1] != 0);
            for (int page = 0; page < 4; page++)
            {
                var r = AttackSharkSession.Request(0xE5, page);
                Assert.Equal(255, r.Skip(1).Take(8).Sum(b => b) & 255);
            }
            // The stream and calibration commands and page 4 cannot be built
            // (attackshark_pro_diagnostic.cpp:623).
            Assert.All(new[] { AttackSharkSession.Request(0x1B), AttackSharkSession.Request(0x1C),
                AttackSharkSession.Request(0x1E), AttackSharkSession.Request(0xE5, 4) },
                r => Assert.All(r, b => Assert.Equal(0, b)));
        }

        [Fact]
        public void RongYuanRequests_ByteForByte()
        {
            // rongyuan_snapshot_protocol.h:122-139 and the spec's section 2.5
            // compiled frames. E5 and 8A carry an FF tail, the rest zeros.
            Assert.Equal(Frame("00 8F 00 00 00 00 00 00 70", 0), RongYuanProtocol.Request(0x8F));
            Assert.Equal(Frame("00 80 00 00 00 00 00 00 7F", 0), RongYuanProtocol.Request(0x80));
            Assert.Equal(Frame("00 E6 00 00 00 00 00 00 19", 0), RongYuanProtocol.Request(0xE6));
            Assert.Equal(Frame("00 84 FF 00 00 00 00 00 7C", 0), RongYuanProtocol.Request(0x84, 0xFF));
            byte[] mapChecksums = { 0x76, 0x75, 0x74, 0x73, 0x72, 0x71, 0x70, 0x6F };
            for (int page = 0; page < 8; page++)
                Assert.Equal(Frame($"00 8A 00 FF {page:X2} 00 00 00 {mapChecksums[page]:X2}", 0xFF),
                    RongYuanProtocol.Request(0x8A, 0, 0xFF, (byte)page));
            byte[] travelChecksums = { 0x1B, 0x1A, 0x19, 0x18 };
            for (int page = 0; page < 4; page++)
                Assert.Equal(Frame($"00 E5 FE 01 {page:X2} 00 00 00 {travelChecksums[page]:X2}", 0xFF),
                    RongYuanProtocol.Request(0xE5, 0xFE, 1, (byte)page));
            // rongyuan_stream_protocol_test.cpp:12-13.
            Assert.Equal(Frame("00 1B 01 00 00 00 00 00 E3", 0), RongYuanProtocol.Request(0x1B, 1));
            Assert.Equal(Frame("00 1B 00 00 00 00 00 00 E4", 0), RongYuanProtocol.Request(0x1B, 0));
            // three_keyboard_protocol_test.cpp:13-14.
            Assert.Equal(0x18, RongYuanProtocol.Request(0xE5, 0xFE, 1, 3)[8]);
        }

        // ── ATTACK SHARK protocol ──

        [Fact]
        public void AttackShark_IdentityDecodeAndKnown()
        {
            // attackshark_pro_diagnostic_model_test.cpp:51: 8F 04 09 is board 2308.
            var r = new byte[65];
            r[1] = 0x8F;
            r[2] = 4;
            r[3] = 9;
            Assert.Equal(2308u, AttackSharkSession.Identity(r));
            // attackshark_pro_diagnostic.cpp:677-678: 8F 33 0C is the R85 HE's 3123.
            r[2] = 0x33;
            r[3] = 0x0C;
            Assert.Equal(3123u, AttackSharkSession.Identity(r));
            r[0] = 1;
            Assert.Equal(0u, AttackSharkSession.Identity(r));

            // attackshark_pro_diagnostic_model_test.cpp:11, 15, 51 and
            // attackshark_pro_diagnostic.cpp:625-627.
            Assert.True(AttackSharkSession.Known(2308, 0x502F));
            Assert.False(AttackSharkSession.Known(2308, 0x5030));
            Assert.False(AttackSharkSession.Known(2268, 0x502F));
            Assert.True(AttackSharkSession.Known(2938, 0x5030));
            Assert.True(AttackSharkSession.Known(3650, 0x502D));
            Assert.True(AttackSharkSession.Known(3650, 0x5029));
            Assert.False(AttackSharkSession.Known(3650, 0x5030));
            Assert.False(AttackSharkSession.Known(3650, 0x502F));
            Assert.False(AttackSharkSession.Known(9999, 0x5029));
            Assert.False(AttackSharkSession.Known(1466, 0x502D));
            foreach (var p in RongYuanCatalog.AttackSharkProfiles)
                foreach (ushort pid in new ushort[] { 0x5029, 0x502D, 0x502F, 0x5030 })
                    Assert.Equal(p.ProductId == pid || (p.Id == 3650 && pid == 0x5029), AttackSharkSession.Known(p.Id, pid));

            // attackshark_pro_diagnostic_model_test.cpp:60-61: the echo, an
            // unwritten FF buffer, report ID 5 and a value past 4096 are not pages.
            var values = new int[32];
            Assert.False(AttackSharkSession.Decode(AttackSharkSession.Request(0xE5, 2), values));
            Assert.False(AttackSharkSession.Decode(Enumerable.Repeat((byte)0xFF, 65).ToArray(), values));
            var five = new byte[65];
            five[0] = 5;
            Assert.False(AttackSharkSession.Decode(five, values));
            var page = new byte[65];
            PutLe16(page, 1, 4097);
            Assert.False(AttackSharkSession.Decode(page, values));
            PutLe16(page, 1, 4096);
            PutLe16(page, 1 + 2 * 31, 620);
            Assert.True(AttackSharkSession.Decode(page, values));
            Assert.Equal(4096, values[0]);
            Assert.Equal(620, values[31]);
        }

        [Fact]
        public void AttackShark_UnitsMilliAndFreshness()
        {
            // attackshark_pro_native_model.h:169-172 with the vectors of
            // attackshark_pro_diagnostic_model_test.cpp:16, 27, 43 and the
            // spec's compiled checks in section 2.6.
            Assert.Equal(100, AttackSharkSession.Units(0x0314));
            Assert.Equal(200, AttackSharkSession.Units(0x0511));
            Assert.Equal(10, AttackSharkSession.Units(0x0299));
            Assert.Equal(100, AttackSharkSession.Units(0x0300));
            Assert.Equal(200, AttackSharkSession.Units(0x0500));
            Assert.Equal(500, AttackSharkSession.Milli(350, 200));
            Assert.Equal(1000, AttackSharkSession.Milli(700, 200));
            Assert.Equal(1000, AttackSharkSession.Milli(730, 200));
            Assert.Equal(1, AttackSharkSession.Milli(1, 200));
            Assert.Equal(1000, AttackSharkSession.Milli(35, 10));
            Assert.Equal(0, AttackSharkSession.Milli(0, 100));
            Assert.Equal(500, AttackSharkSession.Milli(175, 100));
            Assert.Equal(1000, AttackSharkSession.Milli(350, 100));
            Assert.Equal(1000, AttackSharkSession.Milli(400, 100));
            Assert.Equal(0, AttackSharkSession.Milli(350, 0));
            Assert.Equal(150, AttackSharkSession.FreshBudget(1));
            Assert.Equal(200, AttackSharkSession.FreshBudget(5));
            Assert.Equal(300, AttackSharkSession.FreshBudget(10));
        }

        [Fact]
        public void AttackShark_PageSchedule_MatchesTheCompiledOutput()
        {
            // attackshark_pro_native_model.h:174-179, first 20 cycles and
            // per-1024 counts as the spec's section 2.4 printed them.
            int[] First(int fnPage, bool fourth) =>
                Enumerable.Range(0, 20).Select(c => AttackSharkSession.Page((uint)c, fnPage, fourth)).ToArray();
            int[] Counts(int fnPage, bool fourth)
            {
                var counts = new int[4];
                for (uint c = 0; c < 1024; c++) counts[AttackSharkSession.Page(c, fnPage, fourth)]++;
                return counts;
            }
            Assert.Equal(new[] { 3, 2, 0, 0, 0, 0, 2, 1, 1, 2, 0, 0, 0, 0, 2, 1, 1, 2, 0, 0 }, First(2, false));
            Assert.Equal(new[] { 512, 248, 256, 8 }, Counts(2, false));
            Assert.Equal(new[] { 3, 1, 0, 0, 0, 0, 1, 2, 2, 1, 0, 0, 0, 0, 1, 2, 2, 1, 0, 0 }, First(1, false));
            Assert.Equal(new[] { 512, 256, 248, 8 }, Counts(1, false));
            Assert.Equal(new[] { 2, 0, 1, 3, 2, 0, 1, 3 }, First(2, true).Take(8));
            Assert.Equal(new[] { 1, 0, 2, 3, 1, 0, 2, 3 }, First(1, true).Take(8));
            Assert.Equal(new[] { 256, 256, 256, 256 }, Counts(2, true));
            // attackshark_pro_diagnostic_model_test.cpp:12-13 and 45-46: every
            // page holding a key gets at least 30 of 128 cycles.
            foreach (var p in RongYuanCatalog.AttackSharkProfiles)
            {
                var counts = new int[4];
                for (uint c = 0; c < 128; c++) counts[AttackSharkSession.Page(c, p.FnSlot / 32, p.FourthPage)]++;
                for (int slot = 0; slot < 128; slot++)
                    if (p.Factory[slot] != 0 || p.Fn[slot] != 0) Assert.True(counts[slot / 32] >= 30);
            }
        }

        [Fact]
        public void AttackShark_LayerLatch_HallJoyVectors()
        {
            // attackshark_pro_diagnostic_model_test.cpp:29-39 on the X65 Pro
            // HE: slots 7, 13, ... carry 1 to = with F1 to F12 on the Fn layer.
            var p = RongYuanCatalog.FindAttackShark(2308);
            for (int n = 0; n < 12; n++)
            {
                var latch = new AttackSharkLayerLatch();
                int slot = 7 + 6 * n, baseKey = p.Factory[slot], f = AnalogKeyCodes.F1 + n;
                Assert.Equal(f, latch.Route(slot, 175, true, p));
                Assert.Equal(f, latch.Route(slot, 175, false, p));
                Assert.Equal(baseKey, latch.Route(slot, 0, false, p));
                Assert.Equal(baseKey, latch.Route(slot, 175, false, p));
                Assert.Equal(baseKey, latch.Route(slot, 175, true, p));
                Assert.Equal(baseKey, latch.Route(slot, 0, true, p));
                Assert.Equal(f, latch.Route(slot, 175, true, p));
                Assert.Equal(baseKey, latch.Route(slot, 0, true, p));
            }
            // Lines 21-25: every Fn-layer slot of every profile.
            foreach (var profile in RongYuanCatalog.AttackSharkProfiles)
                for (int slot = 0; slot < 128; slot++)
                {
                    if (profile.Fn[slot] == 0) continue;
                    var latch = new AttackSharkLayerLatch();
                    Assert.Equal(profile.Fn[slot], latch.Route(slot, 175, true, profile));
                    Assert.Equal(profile.Fn[slot], latch.Route(slot, 175, false, profile));
                    latch.Route(slot, 0, false, profile);
                    Assert.Equal(profile.Factory[slot], latch.Route(slot, 175, false, profile));
                }
        }

        [Fact]
        public void AttackShark_FnLayer_FollowsHallJoysSelfTest()
        {
            // attackshark_pro_diagnostic.cpp:606-618 on the X65 Pro HE (Fn at
            // slot 65, page 2 value 1) at 100 units per millimeter.
            var p = RongYuanCatalog.FindAttackShark(2308);
            var pages = new AttackSharkPages();
            var output = new AnalogKeyInputState();
            var sample = new int[32];
            var fn = new int[32];
            long t = 1000;
            void Step(int page, int[] values)
            {
                pages.Publish(page, values, ++t);
                pages.Compose(p, 100, 150, t, output);
            }
            fn[1] = 100;
            Step(2, fn);
            sample[7] = 175;
            Step(0, sample);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.F1));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.D1));
            // The Fn key reports its own depth: 100 raw is 285 thousandths.
            Assert.Equal(0.285f, output.Get(AnalogKeyCodes.Fn), 3);
            // Releasing Fn first keeps F1 until the key releases.
            fn[1] = 0;
            Step(2, fn);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.F1));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.D1));
            sample[7] = 0;
            Step(0, sample);
            Assert.Equal(0f, output.Get(AnalogKeyCodes.F1));
            // Without Fn it is the number.
            sample[7] = 175;
            Step(0, sample);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.D1));
            // A number held before Fn stays a number.
            fn[1] = 100;
            Step(2, fn);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.D1));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.F1));
            sample[7] = 0;
            Step(0, sample);
            sample[7] = 175;
            Step(0, sample);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.F1));
            sample[7] = 0;
            Step(0, sample);
            Assert.Equal(0f, output.Get(AnalogKeyCodes.F1));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.D1));
        }

        [Fact]
        public void AttackShark_PagesExpire_AndTheFnPageGatesEveryKey()
        {
            // attackshark_pro_diagnostic.cpp:113-130 and 598-602: a page older
            // than the budget reads 0, stays expired until it arrives again,
            // and nothing publishes while the Fn page is not fresh.
            var p = RongYuanCatalog.FindAttackShark(2308);
            var pages = new AttackSharkPages();
            var output = new AnalogKeyInputState();
            var keys = new int[32];
            keys[9] = 350; // A
            keys[14] = 175; // W
            pages.Publish(0, keys, 1000);
            pages.Compose(p, 100, 150, 1000, output);
            Assert.Equal(0, output.Count); // Fn page not seen yet
            pages.Publish(2, new int[32], 1000);
            pages.Compose(p, 100, 150, 1000, output);
            Assert.Equal(1f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
            pages.Compose(p, 100, 150, 1150, output);
            Assert.Equal(1f, output.Get(AnalogKeyCodes.A));
            pages.Compose(p, 100, 150, 1151, output);
            Assert.Equal(0, output.Count);
            // A longer budget later does not revive an expired page.
            pages.Compose(p, 100, 200, 1160, output);
            Assert.Equal(0, output.Count);
            // Fresh keys on a stale Fn page still read 0.
            pages.Publish(0, keys, 1200);
            pages.Compose(p, 100, 150, 1200, output);
            Assert.Equal(0, output.Count);
            pages.Publish(2, new int[32], 1201);
            pages.Compose(p, 100, 150, 1201, output);
            Assert.Equal(1f, output.Get(AnalogKeyCodes.A));
        }

        // ── ATTACK SHARK session ──

        [Fact]
        public void AttackShark_Start_ProvesTheBoard_ThenReadsTheRfVersion()
        {
            // attackshark_pro_diagnostic.cpp:269-281 and 321-326: two 8F
            // exchanges at 10 ms, then two 80 exchanges, each waiting before
            // the write and before the read (lines 242, 252). Answers are read
            // into an FF-filled buffer with report ID 0 (line 254).
            var time = new FakeTime();
            var board = new SharkBoard { Id = 2308, Usb = 0x0314 };
            var session = StartedShark(board, time, out var io);
            var sets = io.Writes("setf");
            Assert.Equal(new[]
            {
                AttackSharkSession.Request(0x8F), AttackSharkSession.Request(0x8F),
                AttackSharkSession.Request(0x80), AttackSharkSession.Request(0x80),
            }, sets);
            var gets = io.Writes("getf");
            Assert.Equal(4, gets.Count);
            Assert.All(gets, g =>
            {
                Assert.Equal(65, g.Length);
                Assert.Equal(0, g[0]);
                Assert.All(g.Skip(1), b => Assert.Equal(0xFF, b));
            });
            Assert.Equal(Enumerable.Repeat(10, 8), time.Waits);
            Assert.Equal("ATTACK SHARK X65 Pro HE", session.ModelName);
            Assert.Equal(2308u, session.BoardId);
            Assert.Equal(0x0314, session.UsbVersion);
            Assert.Equal(0, session.RfVersion);
            Assert.Equal(100, session.UnitsPerMillimeter);
            Assert.Equal(1, session.DelayMs);
            Assert.Equal(150, session.FreshBudgetMs);
            // The picker lists the factory keys, then the Fn-layer keys.
            Assert.Contains(AnalogKeyCodes.W, session.KeyOrder);
            Assert.Contains(AnalogKeyCodes.Fn, session.KeyOrder);
            Assert.Contains(AnalogKeyCodes.F1, session.KeyOrder);
            Assert.True(Array.IndexOf(session.KeyOrder, AnalogKeyCodes.F1) > Array.IndexOf(session.KeyOrder, AnalogKeyCodes.W));
            Assert.Equal(session.KeyOrder.Length, session.KeyOrder.Distinct().Count());
        }

        [Fact]
        public void AttackShark_Start_RejectsMismatchedOrUnknownBoards()
        {
            // attackshark_pro_diagnostic.cpp:270-280: three attempts, each two
            // 8F answers with the same known ID for this PID.
            var time = new FakeTime();
            // One request late: a stale page answers first, then the board.
            var late = new SharkBoard { Id = 2308 };
            int asked = 0;
            late.Next = req => asked++ == 0 ? new byte[65] : late.Answer(req);
            var io = late.Transport();
            Assert.True(new AttackSharkSession(0x502F, time.Clock, time.Wait).Start(io));
            Assert.Equal(4, io.Writes("setf").Count(r => r[1] == 0x8F));

            // A board with no profile: three attempts, then nothing else.
            var unknown = new SharkBoard { Id = 2819 }.Transport();
            Assert.False(new AttackSharkSession(0x5030, time.Clock, time.Wait).Start(unknown));
            Assert.Equal(6, unknown.Writes("setf").Count);
            Assert.All(unknown.Writes("setf"), r => Assert.Equal(0x8F, r[1]));

            // A known board on another board's PID.
            Assert.False(new AttackSharkSession(0x5030, time.Clock, time.Wait).Start(new SharkBoard { Id = 2308 }.Transport()));

            // The R68 HE also answers on 3151:5029 (attackshark_pro_diagnostic_model.h:10-12).
            var r68 = new AttackSharkSession(0x5029, time.Clock, time.Wait);
            Assert.True(r68.Start(new SharkBoard { Id = 3650, Usb = 0x0504 }.Transport()));
            Assert.Equal("ATTACK SHARK R68HE", r68.ModelName);

            // A zero USB version is an unsupported revision: no depth or RF
            // command follows (attackshark_pro_native_model.h:168,
            // attackshark_pro_diagnostic.cpp:317-320).
            var zero = new SharkBoard { Id = 2308, Usb = 0 }.Transport();
            Assert.False(new AttackSharkSession(0x502F, time.Clock, time.Wait).Start(zero));
            Assert.All(zero.Writes("setf"), r => Assert.Equal(0x8F, r[1]));

            // A board that never answers: three attempts of two exchanges.
            var silent = new AnalogKeyboardTestTransport();
            Assert.False(new AttackSharkSession(0x502F, time.Clock, time.Wait).Start(silent));
            Assert.Equal(3, silent.Writes("setf").Count);
        }

        [Fact]
        public void AttackShark_Start_TheRfVersionPicksTheScale()
        {
            // attackshark_pro_diagnostic.cpp:323-324: the RF version when
            // nonzero, else the USB version. R85 HE log 30: USB 0x0511, RF 0,
            // 200 units per millimeter.
            var time = new FakeTime();
            Assert.Equal(200, StartedShark(new SharkBoard { Id = 3123, Usb = 0x0511 }, time, out _, 0x5029).UnitsPerMillimeter);
            var rf = StartedShark(new SharkBoard { Id = 2308, Usb = 0x0299, Rf = 0x0300 }, time, out _);
            Assert.Equal(0x0300, rf.RfVersion);
            Assert.Equal(100, rf.UnitsPerMillimeter);
            Assert.Equal(10, StartedShark(new SharkBoard { Id = 2308, Usb = 0x0299 }, time, out _).UnitsPerMillimeter);
            // An 80 answer that is not an 80 answer leaves the RF version 0.
            var board = new SharkBoard { Id = 2308, Usb = 0x0500, Rf = 0x0100 };
            board.Next = req => req[1] == 0x80 ? new byte[65] : board.Answer(req);
            var session = StartedShark(board, time, out _);
            Assert.Equal(0, session.RfVersion);
            Assert.Equal(200, session.UnitsPerMillimeter);
        }

        [Fact]
        public void AttackShark_Pass_FlushesOnPageChange_AndPublishesDepth()
        {
            // attackshark_pro_diagnostic.cpp:330-338: pages 3, 2, 0, 0, 0, 0,
            // 2, 1, 1 for the X65 Pro HE, one discarded answer after every
            // page change, 1 ms before each write and each read.
            var time = new FakeTime();
            var board = new SharkBoard { Id = 2308, Usb = 0x0314 };
            board.Pages[0][14] = 175; // W
            board.Pages[0][9] = 350; // A
            board.Pages[2][1] = 0; // Fn up
            var session = StartedShark(board, time, out var io);
            int before = io.Writes("setf").Count;
            time.Waits.Clear();
            var output = new AnalogKeyInputState();

            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0, output.Count); // Fn page not read yet
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0, output.Count);
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.A));
            for (int i = 0; i < 6; i++) Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));

            var pages = io.Writes("setf").Skip(before).Select(r =>
            {
                Assert.Equal(0xE5, r[1]);
                return (int)r[4];
            });
            Assert.Equal(new[] { 3, 3, 2, 2, 0, 0, 0, 0, 0, 2, 2, 1, 1, 1 }, pages);
            Assert.All(time.Waits, w => Assert.Equal(1, w));
            Assert.Equal(9u, session.Cycle);

            // Fn down routes a key pressed after it to its Fn-layer key.
            board.Pages[2][1] = 100;
            board.Pages[0][14] = 0;
            board.Pages[0][7] = 175; // 1, or F1 with Fn
            for (int i = 0; i < 10; i++) session.Pass(io, output, null);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.F1));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.D1));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.W));
        }

        [Fact]
        public void AttackShark_Pass_InvalidAnswersRaiseTheDelay_ThenEndTheSession()
        {
            // attackshark_pro_diagnostic.cpp:339-348: three invalid answers
            // in a row raise the delay 1 to 5 to 10 ms (budgets 150, 200,
            // 300), a valid page clears the count, and three more at 10 ms end it.
            var time = new FakeTime();
            var board = new SharkBoard();
            var session = StartedShark(board, time, out var io);
            var output = new AnalogKeyInputState();
            var bad = new byte[65];
            PutLe16(bad, 1, 0xFFFF);
            board.Next = req => req[1] == 0xE5 ? bad : board.Answer(req);

            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            board.Next = null;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null)); // valid: count cleared
            board.Next = req => req[1] == 0xE5 ? bad : board.Answer(req);
            session.Pass(io, output, null);
            session.Pass(io, output, null);
            Assert.Equal(1, session.DelayMs);
            session.Pass(io, output, null);
            Assert.Equal(5, session.DelayMs);
            Assert.Equal(200, session.FreshBudgetMs);
            time.Waits.Clear();
            session.Pass(io, output, null);
            Assert.All(time.Waits, w => Assert.Equal(5, w));
            session.Pass(io, output, null);
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(10, session.DelayMs);
            Assert.Equal(300, session.FreshBudgetMs);
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
        }

        [Fact]
        public void AttackShark_Pass_ALateIdentityAnswerIsNotAPage()
        {
            // attackshark_pro_diagnostic.cpp:336-337: an 8F answer carrying a
            // known board ID is counted invalid and never published.
            var time = new FakeTime();
            var board = new SharkBoard();
            board.Pages[3][0] = 100;
            var session = StartedShark(board, time, out var io);
            board.Next = req => req[1] == 0xE5 ? board.Answer(AttackSharkSession.Request(0x8F)) : board.Answer(req);
            var output = new AnalogKeyInputState();
            session.Pass(io, output, null);
            session.Pass(io, output, null);
            Assert.Equal(0, output.Count);
            session.Pass(io, output, null);
            Assert.Equal(5, session.DelayMs);
        }

        [Fact]
        public void AttackShark_Pass_RechecksTheIdentityEvery1024Cycles()
        {
            // attackshark_pro_diagnostic.cpp:357: at cycle 1024 two 8F
            // exchanges at 10 ms must return the session's board ID, the
            // depth delay comes back, and the next page read flushes.
            var time = new FakeTime();
            var board = new SharkBoard();
            var session = StartedShark(board, time, out var io);
            var output = new AnalogKeyInputState();
            for (int i = 0; i < 1023; i++) Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            int before = io.Writes("setf").Count;
            time.Waits.Clear();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            // Cycle 1023 reads page 1 after cycle 1022's page 2, so the page
            // read flushes, then the recheck follows.
            Assert.Equal(1, AttackSharkSession.Page(1023, 2, false));
            Assert.Equal(2, AttackSharkSession.Page(1022, 2, false));
            var sent = io.Writes("setf").Skip(before).ToList();
            Assert.Equal(new byte[] { 0xE5, 0xE5, 0x8F, 0x8F }, sent.Select(r => r[1]).ToArray());
            Assert.Equal(new[] { 1, 1 }, sent.Take(2).Select(r => (int)r[4]));
            Assert.Equal(new[] { 1, 1, 1, 1, 10, 10, 10, 10 }, time.Waits);
            Assert.Equal(1, session.DelayMs);
            // The next pass reads page 3 with a flush.
            before = io.Writes("setf").Count;
            session.Pass(io, output, null);
            var next = io.Writes("setf").Skip(before).ToList();
            Assert.Equal(new[] { 3, 3 }, next.Select(r => (int)r[4]));
            Assert.All(next, r => Assert.Equal(0xE5, r[1]));

            // Another board answering at the next recheck ends the session (exit 8).
            board.Id = 2268;
            AnalogPollResult last = AnalogPollResult.Ok;
            for (int i = 0; i < 1023 && last == AnalogPollResult.Ok; i++) last = session.Pass(io, output, null);
            Assert.Equal(AnalogPollResult.Failed, last);
            Assert.Equal(2048u, session.Cycle);
        }

        [Fact]
        public void AttackShark_Pass_AFailedExchangeEndsTheSession()
        {
            // attackshark_pro_diagnostic.cpp:334: a failed feature write or
            // read ends the worker (exit 6).
            var time = new FakeTime();
            var board = new SharkBoard();
            var session = StartedShark(board, time, out var io);
            board.Next = _ => null; // the read finds nothing and fails
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, new AnalogKeyInputState(), null));

            var gone = StartedShark(new SharkBoard(), time, out var io2);
            io2.Gone = true;
            Assert.Equal(AnalogPollResult.Failed, gone.Pass(io2, new AnalogKeyInputState(), null));
            Assert.Equal(AnalogPollResult.Failed, new AttackSharkSession(0x502F, time.Clock, time.Wait)
                .Pass(io2, new AnalogKeyInputState(), null)); // never started
        }

        [Fact]
        public void AttackShark_ShortRead_LeavesTheUnwrittenBytesAtFF()
        {
            // attackshark_pro_diagnostic.cpp:254-256 fills the answer with FF
            // before HidD_GetFeature, so bytes the driver did not write fail
            // the 4096 check (attackshark_pro_diagnostic_model.h:34). Windows
            // counts 64 bytes for a full unnumbered report.
            var time = new FakeTime();
            var board = new SharkBoard();
            board.Pages[3][0] = 100;
            board.Pages[2][31] = 7;
            var session = StartedShark(board, time, out var io);
            var output = new AnalogKeyInputState();
            io.FeatureTransferred = _ => 10;
            session.Pass(io, output, null);
            session.Pass(io, output, null);
            session.Pass(io, output, null);
            Assert.Equal(5, session.DelayMs); // three rejected pages
            io.FeatureTransferred = _ => 64;
            session.Pass(io, output, null);
            session.Pass(io, output, null);
            Assert.Equal(0, output.Count);
            Assert.Equal(5, session.DelayMs);
        }

        [Fact]
        public void AttackShark_Stop_SendsNothing()
        {
            // attackshark_pro_diagnostic.cpp:361 and the spec's section 2.4:
            // no command changes the keyboard, so nothing is restored.
            var time = new FakeTime();
            var session = StartedShark(new SharkBoard(), time, out var io);
            session.Pass(io, new AnalogKeyInputState(), null);
            int writes = io.Log.Count;
            session.Stop(io);
            Assert.Equal(writes, io.Log.Count);
        }

        [Fact]
        public void PreciseDelay_WaitsOnAWaitableTimer_AndFallsBackAfterDispose()
        {
            // attackshark_pro_diagnostic.cpp:61-78: a high-resolution waitable
            // timer paces the exchanges, and a missing timer falls back to a
            // plain sleep instead of stopping the protocol.
            var delay = new RongYuanPreciseDelay();
            Assert.False(delay.TimerActive);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            delay.Wait(2);
            watch.Stop();
            Assert.True(delay.TimerActive);
            Assert.True(watch.Elapsed.TotalMilliseconds >= 1.0, $"waited {watch.Elapsed.TotalMilliseconds} ms");
            delay.Wait(0);
            delay.Dispose();
            Assert.False(delay.TimerActive);
            delay.Wait(1);
            delay.Dispose();
        }

        [Fact]
        public void AttackShark_OnARongYuanBoard_SendsOnlyIdentityRequests()
        {
            // attackshark_pro_diagnostic.cpp:316: a board that fails the
            // identity proof gets no depth command, so a MonsGeek on
            // 3151:5030 sees only 8F before the snapshot route's turn.
            var time = new FakeTime();
            var io = new RyBoard { Board = 2819 }.Transport();
            Assert.False(new AttackSharkSession(0x5030, time.Clock, time.Wait).Start(io));
            Assert.All(io.Writes("setf"), r => Assert.Equal(0x8F, r[1]));
        }

        // ── RongYuan protocol ──

        [Fact]
        public void RongYuan_DecodeActionCodes()
        {
            // rongyuan_snapshot_protocol.h:11-15 and the spec's section 2.7.
            Assert.Equal(AnalogKeyCodes.W, RongYuanProtocol.Decode(Bytes("00 00 1A 00")));
            Assert.Equal(AnalogKeyCodes.Fn, RongYuanProtocol.Decode(Bytes("0A 01 00 00")));
            Assert.Equal(0, RongYuanProtocol.Decode(Bytes("00 00 00 00")));
            Assert.Equal(0, RongYuanProtocol.Decode(Bytes("03 00 E9 00"))); // consumer volume up
            Assert.Equal(0, RongYuanProtocol.Decode(Bytes("00 00 E0 04"))); // a Ctrl combination
            Assert.Equal(0, RongYuanProtocol.Decode(Bytes("0D 02 01 00")));
            Assert.Equal(0, RongYuanProtocol.Decode(Bytes("0A 0E 00 00")));
            Assert.Equal(2, RongYuanProtocol.Decode(Bytes("00 00 02 00"))); // board 3714, as-is
        }

        [Fact]
        public void RongYuan_NormalizeAndUnits_HallJoyVectors()
        {
            // three_keyboard_protocol_test.cpp:33-39 and the spec's section
            // 2.6 and 3.3 compiled values.
            Assert.Equal(500, RongYuanProtocol.Normalize(360, 200, 3600));
            Assert.Equal(1000, RongYuanProtocol.Normalize(350, 100, 3500));
            Assert.Equal(1000, RongYuanProtocol.Normalize(3500, 1000, 3500));
            Assert.Equal(1000, RongYuanProtocol.Normalize(360, 100, 3600));
            Assert.Equal(500, RongYuanProtocol.Normalize(180, 100, 3600));
            Assert.Equal(963, RongYuanProtocol.Normalize(385, 100, 4000));
            Assert.Equal(0, RongYuanProtocol.Normalize(385, 0, 4000));
            var feature = new byte[65];
            feature[1] = 0xE6;
            feature[2] = 0xAA;
            Assert.Equal(100, RongYuanProtocol.Units(0x500, feature));
            feature[3] = 1;
            Assert.Equal(200, RongYuanProtocol.Units(0x300, feature));
            feature[3] = 2;
            Assert.Equal(1000, RongYuanProtocol.Units(0x300, feature));
            feature[3] = 3;
            Assert.Equal(0, RongYuanProtocol.Units(0x500, feature));
            Assert.Equal(200, RongYuanProtocol.Units(0x500, new byte[65]));
            Assert.Equal(100, RongYuanProtocol.Units(0x300, new byte[65]));
            Assert.Equal(100, RongYuanProtocol.Units(0x4FF, new byte[65]));
            Assert.Equal(10, RongYuanProtocol.Units(0x299, new byte[65]));
            // rongyuan_stream_protocol_test.cpp:20-23.
            var none = new byte[65];
            Assert.Equal(0, RongYuanProtocol.StreamUnits(0, none, false));
            Assert.Equal(0, RongYuanProtocol.StreamUnits(0x300, none, true));
            Assert.Equal(0, RongYuanProtocol.StreamUnits(0xFFFF, none, false));
            Assert.Equal(100, RongYuanProtocol.StreamUnits(0x300, none, false));
            Assert.Equal(200, RongYuanProtocol.StreamUnits(0x500, none, false));
            feature[3] = 2;
            Assert.Equal(1000, RongYuanProtocol.StreamUnits(0x300, feature, true));
            feature[3] = 3;
            Assert.Equal(0, RongYuanProtocol.StreamUnits(0x300, feature, true));
        }

        [Fact]
        public void RongYuan_ParseTravelAndAssignments_RejectEveryPartialCopy()
        {
            // three_keyboard_protocol_test.cpp:13-31: a read that returns the
            // request with part of the answer copied over it never parses.
            var request = RongYuanProtocol.Request(0xE5, 0xFE, 1, 3);
            var values = new int[32];
            Assert.False(RongYuanProtocol.ParseTravel(request, 200, values));
            var reply = new byte[65];
            for (int i = 0; i < 32; i++) PutLe16(reply, 1 + 2 * i, i * 20);
            for (int n = 0; n < 64; n++)
            {
                var partial = (byte[])request.Clone();
                Array.Copy(reply, 1, partial, 1, n);
                Assert.False(RongYuanProtocol.ParseTravel(partial, 200, values));
            }
            Assert.True(RongYuanProtocol.ParseTravel(reply, 200, values));
            Assert.Equal(620, values[31]);
            // 1200 is six millimeters at 200 units: the limit is inclusive.
            PutLe16(reply, 1, 1200);
            Assert.True(RongYuanProtocol.ParseTravel(reply, 200, values));
            PutLe16(reply, 1, 1201);
            Assert.False(RongYuanProtocol.ParseTravel(reply, 200, values));
            Assert.Equal(1200, values[0]); // a rejected page leaves the values alone
            Assert.False(RongYuanProtocol.ParseTravel(new byte[65], 0, values));

            // The first 64 bytes of kMatrix2819 (rongyuan_snapshot_protocol.h:19-22).
            var mapRequest = RongYuanProtocol.Request(0x8A, 0, 0xFF, 0);
            var mapReply = new byte[65];
            int[] first = { 41, 53, 43, 57, 225, 224, 58, 30, 20, 4, 0, 227, 59, 31, 26, 22 };
            for (int k = 0; k < 16; k++) mapReply[3 + 4 * k] = (byte)first[k];
            Assert.True(RongYuanProtocol.ValidAssignments(mapReply));
            for (int n = 0; n < 64; n++)
            {
                var partial = (byte[])mapRequest.Clone();
                Array.Copy(mapReply, 1, partial, 1, n);
                Assert.False(RongYuanProtocol.ValidAssignments(partial));
            }
            // rongyuan_snapshot_protocol.h:159-166: type past 0x20, a last
            // byte of FF, a type-0 code with a last byte.
            var bad = (byte[])mapReply.Clone();
            bad[1] = 0x21;
            Assert.False(RongYuanProtocol.ValidAssignments(bad));
            bad = (byte[])mapReply.Clone();
            bad[1] = 3;
            bad[4] = 0xFF;
            Assert.False(RongYuanProtocol.ValidAssignments(bad));
            bad = (byte[])mapReply.Clone();
            bad[4] = 1;
            Assert.False(RongYuanProtocol.ValidAssignments(bad));
            bad = (byte[])mapReply.Clone();
            bad[1] = 3;
            bad[4] = 1;
            Assert.True(RongYuanProtocol.ValidAssignments(bad));
        }

        [Fact]
        public void RongYuan_ParseStreamEvent_HallJoyVectors()
        {
            // rongyuan_stream_protocol_test.cpp:14-19: 05 1B 81 01 0E is slot
            // 14 at 385. Byte 31 is padding. Report 4, type 1C, 31 bytes and
            // slot 128 are not events.
            var packet = StreamEvent(14, 0x0181);
            Assert.Equal(Bytes("05 1B 81 01 0E"), packet.Take(5));
            Assert.True(RongYuanProtocol.ParseStreamEvent(packet, out int slot, out int raw));
            Assert.Equal(14, slot);
            Assert.Equal(385, raw);
            packet[31] = 1;
            Assert.True(RongYuanProtocol.ParseStreamEvent(packet, out _, out _));
            Assert.False(RongYuanProtocol.ParseStreamEvent(StreamEvent(14, 385, reportId: 4), out _, out _));
            Assert.False(RongYuanProtocol.ParseStreamEvent(StreamEvent(14, 385, type: 0x1C), out _, out _));
            Assert.False(RongYuanProtocol.ParseStreamEvent(StreamEvent(14, 385, length: 31), out _, out _));
            Assert.False(RongYuanProtocol.ParseStreamEvent(StreamEvent(14, 385, length: 33), out _, out _));
            Assert.False(RongYuanProtocol.ParseStreamEvent(StreamEvent(128, 385), out _, out _));
            Assert.True(RongYuanProtocol.ParseStreamEvent(StreamEvent(127, 0xFFFF), out slot, out raw));
            Assert.Equal(127, slot);
            Assert.Equal(0xFFFF, raw);
        }

        [Fact]
        public void RongYuan_FindStream_AliasesAndCollisions()
        {
            // rongyuan_stream_protocol_test.cpp:24-34.
            foreach (var alias in RongYuanCatalog.StreamAliases)
            {
                Assert.True(RongYuanCatalog.IsStreamCandidate(alias.VendorId, alias.ProductId));
                var viaAlias = RongYuanCatalog.FindStream(alias.Board, alias.VendorId, alias.ProductId);
                Assert.NotNull(viaAlias);
                Assert.Same(RongYuanCatalog.FindStream(alias.Board, alias.CanonicalVendorId, alias.CanonicalProductId), viaAlias);
                Assert.Null(RongYuanCatalog.FindStream(alias.Board, (ushort)(alias.VendorId ^ 1), alias.ProductId));
            }
            Assert.Equal("Akko MOD007S V3 HE", RongYuanCatalog.FindStream(2683, Vid, 0x5030).Name);
            Assert.Equal("Valkyrie VK Mag75 Max", RongYuanCatalog.FindStream(2398, 0x374A, 0xA222).Name);
            Assert.Null(RongYuanCatalog.FindStream(2609, 12625, 20513)); // the YC3123 collision
            Assert.Null(RongYuanCatalog.FindStream(2368, 12625, 20528)); // another factory map, not an alias
            foreach (var m in RongYuanCatalog.StreamModels)
            {
                Assert.Same(m, RongYuanCatalog.FindStream(m.Board, m.VendorId, m.ProductId));
                Assert.Null(RongYuanCatalog.FindStream(m.Board, m.VendorId, (ushort)(m.ProductId ^ 1)));
                Assert.Null(RongYuanCatalog.FindStream(m.Board + 100000, m.VendorId, m.ProductId));
            }
            Assert.False(RongYuanCatalog.IsStreamCandidate(Vid, 0x5031));
            Assert.True(RongYuanCatalog.IsStreamCandidate(0x0DB0, 0xEBBE));
        }

        // ── Snapshot session ──

        [Fact]
        public void Snapshot_Start_ProofThenMap_ByteForByte()
        {
            // rongyuan_snapshot_backend.cpp:217-275: 8F, E6, E5 pages 0 to 3,
            // 84 FF, then 8A profile FF pages 0 to 7, each read into a zeroed
            // buffer with report ID 0 (lines 186-187).
            var time = new FakeTime();
            var model = RongYuanCatalog.FindSnapshot(2819);
            var board = new RyBoard { Board = 2819, Version = 0x0410, Precision = 0, Codes = model.Codes };
            var io = board.Transport();
            var session = new RongYuanSnapshotSession(time.Clock, time.Wait);
            Assert.True(session.Start(io));
            var expected = new List<byte[]>
            {
                Frame("00 8F 00 00 00 00 00 00 70", 0),
                Frame("00 E6 00 00 00 00 00 00 19", 0),
            };
            for (int page = 0; page < 4; page++)
                expected.Add(Frame($"00 E5 FE 01 {page:X2} 00 00 00 {0x1B - page:X2}", 0xFF));
            expected.Add(Frame("00 84 FF 00 00 00 00 00 7C", 0));
            for (int page = 0; page < 8; page++)
                expected.Add(Frame($"00 8A 00 FF {page:X2} 00 00 00 {0x76 - page:X2}", 0xFF));
            Assert.Equal(expected, io.Writes("setf"));
            var gets = io.Writes("getf");
            Assert.Equal(expected.Count, gets.Count);
            Assert.All(gets, g => Assert.All(g, b => Assert.Equal(0, b)));
            Assert.Empty(time.Waits);
            Assert.Equal("MonsGeek M1 V5 HE", session.ModelName);
            Assert.Equal(100, session.UnitsPerMillimeter);
            Assert.Equal(0x0410, session.FirmwareVersion);
            Assert.Equal(AnalogKeyboardData.KeysOf(model.Codes), session.KeyOrder);
            Assert.Equal(82, session.KeyOrder.Length);
            Assert.Equal(model.Codes, session.AssignedCodes);
        }

        [Fact]
        public void Snapshot_Start_AssignmentPagesFollowTheActiveProfile()
        {
            // rongyuan_snapshot_backend.cpp:241-251: 84 answers profile 3,
            // so 8A asks profile 3 (checksums 73 down to 6C).
            var time = new FakeTime();
            var board = new RyBoard { Board = 2959, Profile = 3, Codes = RongYuanCatalog.FindSnapshot(2959).Codes };
            var io = board.Transport();
            Assert.True(new RongYuanSnapshotSession(time.Clock, time.Wait).Start(io));
            var maps = io.Writes("setf").Where(r => r[1] == 0x8A).ToList();
            Assert.Equal(8, maps.Count);
            for (int page = 0; page < 8; page++)
                Assert.Equal(Frame($"00 8A 03 FF {page:X2} 00 00 00 {0x73 - page:X2}", 0xFF), maps[page]);
        }

        [Fact]
        public void Snapshot_Query_ReadsAgainUntilTheAnswerReplacesTheRequest()
        {
            // rongyuan_snapshot_backend.cpp:184-193 and
            // rongyuan_snapshot_protocol.h:133-137: a read before the firmware
            // answered returns the request, whose FF tail fails the page, so
            // the query reads again 1 ms later.
            var time = new FakeTime();
            var board = new RyBoard { Board = 2819, Codes = RongYuanCatalog.FindSnapshot(2819).Codes };
            var io = board.Transport();
            var scripted = io.OnSetFeature;
            int pageZero = 0;
            io.OnSetFeature = req => req[1] == 0xE5 && req[4] == 0 && pageZero++ == 0
                ? new[] { (byte[])req.Clone(), board.Answer(req) }
                : scripted(req);
            Assert.True(new RongYuanSnapshotSession(time.Clock, time.Wait).Start(io));
            Assert.Equal(new[] { 1 }, time.Waits);
            Assert.Equal(io.Writes("setf").Count + 1, io.Writes("getf").Count);
        }

        [Fact]
        public void Snapshot_Start_Rejections()
        {
            // rongyuan_snapshot_backend.cpp:217-275 and 80-95: each failed
            // step fails admission, and a failed read poisons the channel.
            bool Start(Action<RyBoard> setup, Action<AnalogKeyboardTestTransport> io = null,
                Func<RongYuanSnapshotSession, bool> check = null)
            {
                var time = new FakeTime();
                var board = new RyBoard { Board = 2819, Codes = RongYuanCatalog.FindSnapshot(2819).Codes };
                setup(board);
                var transport = board.Transport();
                io?.Invoke(transport);
                var session = new RongYuanSnapshotSession(time.Clock, time.Wait);
                bool started = session.Start(transport);
                if (check != null) Assert.True(check(session));
                return started;
            }

            Assert.True(Start(_ => { }));
            // A stream-route board: the 8F query never validates and times out.
            Assert.False(Start(b => b.Board = 2949, check: s => s.Poisoned));
            // Precision 3 has no unit scale (rongyuan_snapshot_protocol.h:143).
            Assert.False(Start(b => b.Precision = 3));
            // A travel value past six millimeters never parses.
            Assert.False(Start(b => b.Pages[2][5] = 601));
            // The active profile must be under 8.
            Assert.False(Start(b => b.Profile = 8));
            // An invalid assignment page times out.
            Assert.False(Start(b => b.Next = req =>
            {
                if (req[1] != 0x8A || req[4] != 5) return b.Answer(req);
                var r = b.Answer(req);
                r[1] = 3;
                r[4] = 0xFF;
                return r;
            }));
            // Windows' count must be exactly 64 (rongyuan_snapshot_backend.cpp:94).
            Assert.False(Start(_ => { }, io => io.FeatureTransferred = a => a.Length, s => s.Poisoned));
            // A nonzero report ID fails the read.
            Assert.False(Start(b => b.Next = req =>
            {
                var r = b.Answer(req);
                r[0] = 1;
                return r;
            }, check: s => s.Poisoned));
            // A failed write poisons too.
            Assert.False(Start(_ => { }, io => io.Gone = true));
        }

        [Fact]
        public void Snapshot_LegacyUnits_WhenE6NeverAnswers()
        {
            // rongyuan_snapshot_backend.cpp:225-229: old firmware without E6
            // uses its version's scale, after the optional query's 50 ms.
            var time = new FakeTime();
            var board = new RyBoard
            {
                Board = 2642, Version = 0x0500, Precision = null,
                Codes = RongYuanCatalog.FindSnapshot(2642).Codes,
            };
            var session = new RongYuanSnapshotSession(time.Clock, time.Wait);
            Assert.True(session.Start(board.Transport()));
            Assert.Equal(200, session.UnitsPerMillimeter);
            Assert.False(session.Poisoned);
            Assert.Equal(50, time.Waits.Count);
            Assert.Equal("EPOMAKER G84 HE", session.ModelName);
        }

        [Fact]
        public void Snapshot_Pass_RoundRobin_NormalizesAndExpiresAfter150Ms()
        {
            // rongyuan_snapshot_backend.cpp:276-291 and 319-346: pages 0, 1,
            // 2, 3 in turn, 1 ms apart, normalized against 3.6 mm on the
            // M1 V5 HE, and a key reads 0 once its page is older than 150 ms
            // (lines 32, 460-465).
            var time = new FakeTime();
            var board = new RyBoard { Board = 2819, Codes = RongYuanCatalog.FindSnapshot(2819).Codes };
            board.Pages[0][14] = 180; // W: 1.8 mm of 3.6 at 100 units per mm
            board.Pages[0][9] = 360; // A: the bottom
            var io = board.Transport();
            var session = new RongYuanSnapshotSession(time.Clock, time.Wait);
            Assert.True(session.Start(io));
            int before = io.Writes("setf").Count;
            time.Waits.Clear();
            var output = new AnalogKeyInputState();

            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            long pageZeroAt = time.Now;
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
            time.Now = pageZeroAt + 149; // the next pass waits 1 ms first
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null)); // 151 ms
            Assert.Equal(0f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(0, output.Count);
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null)); // page 0 again
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(new[] { 0, 1, 2, 3, 0 }, io.Writes("setf").Skip(before).Select(r => (int)r[4]));
            Assert.Equal(new[] { 1, 1, 1, 1 }, time.Waits);
        }

        [Fact]
        public void Snapshot_Pass_AFailedQueryEndsTheSession()
        {
            // rongyuan_snapshot_backend.cpp:322-325: the loop ends on the
            // first failed travel query.
            var time = new FakeTime();
            var board = new RyBoard { Board = 2819, Codes = RongYuanCatalog.FindSnapshot(2819).Codes };
            var io = board.Transport();
            var session = new RongYuanSnapshotSession(time.Clock, time.Wait);
            Assert.True(session.Start(io));
            board.Pages[0][0] = 601;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, new AnalogKeyInputState(), null));
            Assert.True(session.Poisoned);
            board.Pages[0][0] = 0;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, new AnalogKeyInputState(), null));
            int writes = io.Log.Count;
            session.Stop(io);
            Assert.Equal(writes, io.Log.Count); // nothing to restore
        }

        [Fact]
        public void Snapshot_KeysPublish_UnderTheKeyboardsOwnAssignments()
        {
            // HallJoy binds each slot to its factory key and its assignment and
            // reads the assignments while its automatic layout remaps, its
            // default (rongyuan_snapshot_backend.cpp:41-43, 253-265, 457-465):
            // a remapped key moves the key it now types, and a slot assigned
            // nothing decodable publishes nothing.
            var time = new FakeTime();
            var codes = (int[])RongYuanCatalog.FindSnapshot(2819).Codes.Clone();
            int w = Array.IndexOf(codes, AnalogKeyCodes.W), space = Array.IndexOf(codes, AnalogKeyCodes.Space);
            Assert.True(w >= 0 && space >= 0);
            codes[w] = AnalogKeyCodes.Q;
            codes[space] = 0;
            var board = new RyBoard { Board = 2819, Codes = codes };
            board.Pages[w / 32][w % 32] = 150;
            board.Pages[space / 32][space % 32] = 150;
            var io = board.Transport();
            var session = new RongYuanSnapshotSession(time.Clock, time.Wait);
            Assert.True(session.Start(io));
            Assert.Equal(AnalogKeyCodes.Q, session.PublishedCodes[w]);
            Assert.Equal(0, session.PublishedCodes[space]);
            Assert.DoesNotContain(AnalogKeyCodes.Space, session.KeyOrder);
            var output = new AnalogKeyInputState();
            for (int page = 0; page < 4; page++)
                Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.True(output.Get(AnalogKeyCodes.Q) > 0f);
            Assert.Equal(0f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.Space));
        }

        // ── Stream session ──

        [Fact]
        public void Stream_KeysPublish_UnderTheKeyboardsOwnAssignments()
        {
            // The stream route reads the same assignment pages and publishes
            // them the same way (rongyuan_stream_backend.cpp:302-319, 511-519).
            var time = new FakeTime();
            var codes = (int[])RongYuanCatalog.FindStream(3590, Vid, 0x5030).Codes.Clone();
            int w = Array.IndexOf(codes, AnalogKeyCodes.W), space = Array.IndexOf(codes, AnalogKeyCodes.Space);
            Assert.True(w >= 0 && space >= 0);
            codes[w] = AnalogKeyCodes.Q;
            codes[space] = 0;
            var board = new RyBoard { Board = 3590, Version = 0x0409, Radio = 0, Precision = 0, Codes = codes };
            var session = StartedStream(board, time, Vid, 0x5030, out var io);
            Assert.Equal(AnalogKeyCodes.Q, session.PublishedCodes[w]);
            Assert.Equal(0, session.PublishedCodes[space]);
            var output = new AnalogKeyInputState();
            io.QueueInput(StreamEvent(w, 150));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.True(output.Get(AnalogKeyCodes.Q) > 0f);
            Assert.Equal(0f, output.Get(AnalogKeyCodes.W));
            io.QueueInput(StreamEvent(space, 150));
            session.Pass(io, output, null);
            Assert.Equal(0f, output.Get(AnalogKeyCodes.Space));
        }

        [Fact]
        public void Stream_Start_ProofMapFlushThenEnable_ByteForByte()
        {
            // rongyuan_stream_backend.cpp:264-325 and 334-361: 8F, 80, E6,
            // 84 FF, 8A pages 0 to 7, the input queue flushed, then 1B 01
            // with no read after it.
            var time = new FakeTime();
            var board = new RyBoard { Board = 3590, Version = 0x0409, Radio = 0, Precision = 0 };
            var session = StartedStream(board, time, Vid, 0x5030, out var io);
            var expected = new List<byte[]>
            {
                Frame("00 8F 00 00 00 00 00 00 70", 0),
                Frame("00 80 00 00 00 00 00 00 7F", 0),
                Frame("00 E6 00 00 00 00 00 00 19", 0),
                Frame("00 84 FF 00 00 00 00 00 7C", 0),
            };
            for (int page = 0; page < 8; page++)
                expected.Add(Frame($"00 8A 00 FF {page:X2} 00 00 00 {0x76 - page:X2}", 0xFF));
            expected.Add(Frame("00 1B 01 00 00 00 00 00 E3", 0));
            Assert.Equal(expected, io.Writes("setf"));
            Assert.Equal(expected.Count - 1, io.Writes("getf").Count);
            Assert.Equal(1, io.Discards);
            Assert.True(session.StreamEnabled);
            Assert.Equal("GamaKay TK75 TMR", session.ModelName);
            Assert.Equal(100, session.UnitsPerMillimeter);
            Assert.Equal(0, session.RadioVersion);
            Assert.Equal(81, session.KeyOrder.Length);
        }

        [Fact]
        public void Stream_TheRadioVersionPicksTheScale()
        {
            // rongyuan_stream_backend.cpp:270-277: a radio version that is
            // neither 0 nor FFFF replaces the USB version before the scale.
            var time = new FakeTime();
            var radio = StartedStream(new RyBoard { Board = 3590, Version = 0x0299, Radio = 0x0500, Precision = null },
                time, Vid, 0x5030, out _);
            Assert.Equal(0x0500, radio.RadioVersion);
            Assert.Equal(200, radio.UnitsPerMillimeter);
            var absent = StartedStream(new RyBoard { Board = 3590, Version = 0x0299, Radio = 0xFFFF, Precision = null },
                time, Vid, 0x5030, out _);
            Assert.Equal(0, absent.RadioVersion);
            Assert.Equal(10, absent.UnitsPerMillimeter);
            // No 80 answer: the read returns the request, which passes the
            // 80 check with version 0 and changes nothing.
            var silent = StartedStream(new RyBoard { Board = 3590, Version = 0x0300, Radio = null, Precision = null },
                time, Vid, 0x5030, out _);
            Assert.Equal(0, silent.RadioVersion);
            Assert.Equal(100, silent.UnitsPerMillimeter);
            // A valid E6 answer wins over every version.
            var e6 = StartedStream(new RyBoard { Board = 3590, Version = 0x0299, Radio = 0x0500, Precision = 2 },
                time, Vid, 0x5030, out _);
            Assert.Equal(1000, e6.UnitsPerMillimeter);
        }

        [Fact]
        public void Stream_PrecisionRows_RequireTheE6Answer()
        {
            // rongyuan_stream_protocol.h:277-281: board 2642's row needs E6,
            // so without it Start fails before the map and never enables.
            var time = new FakeTime();
            var board = new RyBoard { Board = 2642, Version = 0x0500, Precision = null };
            board.Codes = RongYuanCatalog.FindStream(2642, Vid, 0x5030).Codes;
            var io = board.Transport();
            var session = new RongYuanStreamSession(Vid, 0x5030, time.Clock, time.Wait);
            Assert.False(session.Start(io));
            Assert.DoesNotContain(io.Writes("setf"), r => r[1] == 0x84 || r[1] == 0x1B);
            Assert.False(session.StreamEnabled);
            var withE6 = StartedStream(new RyBoard { Board = 2642, Precision = 2 }, new FakeTime(), Vid, 0x5030, out _);
            Assert.Equal(1000, withE6.UnitsPerMillimeter);
        }

        [Fact]
        public void Stream_Start_OnlyBoardsListedForTheUsbIdentity()
        {
            // rongyuan_stream_backend.cpp:240-248 and rongyuan_stream_protocol.h:266-269.
            var time = new FakeTime();
            var io = new RyBoard { Board = 2368 }.Transport(); // a 3151:502F row seen on 5030
            var session = new RongYuanStreamSession(Vid, 0x5030, time.Clock, time.Wait);
            Assert.False(session.Start(io));
            Assert.All(io.Writes("setf"), r => Assert.Equal(0x8F, r[1]));
            Assert.True(session.Poisoned);
            Assert.Equal("Akko MOD007S V3 HE",
                StartedStream(new RyBoard { Board = 2683 }, new FakeTime(), Vid, 0x5030, out _).ModelName);
            Assert.Equal("Valkyrie VK Mag75 Max",
                StartedStream(new RyBoard { Board = 2398 }, new FakeTime(), 0x374A, 0xA222, out _).ModelName);
            Assert.Equal("MSI STRIKE 700 HE",
                StartedStream(new RyBoard { Board = 3595, Precision = 0 }, new FakeTime(), 0x0DB0, 0xEBBE, out _).ModelName);
        }

        [Fact]
        public void Stream_AFailedEnable_StillSendsTheDisable()
        {
            // rongyuan_stream_backend.cpp:232-236 and 183-188: the enable is
            // marked before the write, so a failed or timed-out 1B 01 is
            // still followed by 1B 00.
            var time = new FakeTime();
            var board = new RyBoard { Board = 3590 };
            board.Codes = RongYuanCatalog.FindStream(3590, Vid, 0x5030).Codes;
            var io = new FailingWrites(board.Transport()) { Fail = r => r[1] == 0x1B && r[2] == 1 };
            var session = new RongYuanStreamSession(Vid, 0x5030, time.Clock, time.Wait);
            Assert.False(session.Start(io));
            Assert.Equal(new[] { RongYuanProtocol.Request(0x1B, 1), RongYuanProtocol.Request(0x1B, 0) },
                io.Attempts.Skip(io.Attempts.Count - 2));
            Assert.False(session.StreamEnabled);
            int attempts = io.Attempts.Count;
            session.Stop(io);
            Assert.Equal(attempts, io.Attempts.Count);
        }

        [Fact]
        public void Stream_Pass_DeltasHoldUntilTheNextEvent()
        {
            // rongyuan_stream_backend.cpp:37 and 326-333,
            // rongyuan_stream_protocol_test.cpp:14-15 and 67-74: an event sets
            // one slot, silence keeps every key, a 0 event releases.
            var time = new FakeTime();
            var session = StartedStream(new RyBoard { Board = 3590 }, time, Vid, 0x5030, out var io);
            var output = new AnalogKeyInputState();
            io.QueueInput(Bytes("05 1B 81 01 0E").Concat(new byte[27]).ToArray());
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.963f, output.Get(AnalogKeyCodes.W), 3);
            io.QueueInput(StreamEvent(9, 200)); // A at 2 mm of 4
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null)); // a held key sends nothing
            Assert.Equal(0.963f, output.Get(AnalogKeyCodes.W), 3);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.A));
            io.QueueInput(StreamEvent(14, 0));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(1, output.Count);
            // The output is rebuilt from the session's own state each event,
            // so a reset between passes does not lose a held key.
            output.ResetForReuse();
            io.QueueInput(StreamEvent(14, 400));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.A));
        }

        [Fact]
        public void Stream_Pass_SkipsNotificationsAndNonKeySlots()
        {
            // rongyuan_stream_backend.cpp:387-388: report 5 with another event
            // type is a vendor notification. Line 328: a slot with no key.
            var time = new FakeTime();
            var session = StartedStream(new RyBoard { Board = 3590 }, time, Vid, 0x5030, out var io);
            var output = new AnalogKeyInputState();
            output.Set(AnalogKeyCodes.W, 0.25f);
            io.QueueInput(StreamEvent(14, 400, type: 0x1C));
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            io.QueueInput(StreamEvent(10, 400)); // slot 10 has no key on the TK75 TMR
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            Assert.Equal(0.25f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(1, output.Count);
        }

        [Fact]
        public void Stream_Pass_MalformedEventsEndTheSession()
        {
            // rongyuan_stream_backend.cpp:327 and 389-398: a report that does
            // not parse, or a depth past six millimeters, ends the session,
            // as does a failed read.
            var time = new FakeTime();
            var session = StartedStream(new RyBoard { Board = 3590 }, time, Vid, 0x5030, out var io);
            var output = new AnalogKeyInputState();
            AnalogPollResult With(byte[] report)
            {
                io.QueueInput(report);
                return session.Pass(io, output, null);
            }
            Assert.Equal(AnalogPollResult.Failed, With(StreamEvent(14, 100, length: 31)));
            Assert.Equal(AnalogPollResult.Failed, With(StreamEvent(14, 100, reportId: 4)));
            Assert.Equal(AnalogPollResult.Failed, With(StreamEvent(128, 100)));
            Assert.Equal(AnalogPollResult.Failed, With(StreamEvent(14, 601)));
            Assert.Equal(AnalogPollResult.Ok, With(StreamEvent(14, 600)));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
            io.Gone = true;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
        }

        [Fact]
        public void Stream_SplitKeys_ReadTheLargerDepth()
        {
            // rongyuan_stream_protocol_test.cpp:57-65 and 79-81: two positions
            // of one key stay independent and the key reads the larger.
            // Board 3026 has Space at slots 41 and 47.
            var time = new FakeTime();
            var session = StartedStream(new RyBoard { Board = 3026 }, time, Vid, 0x5029, out var io);
            var output = new AnalogKeyInputState();
            io.QueueInput(StreamEvent(41, 200), StreamEvent(47, 280), StreamEvent(47, 0), StreamEvent(41, 0));
            session.Pass(io, output, null);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.Space));
            session.Pass(io, output, null);
            Assert.Equal(0.7f, output.Get(AnalogKeyCodes.Space));
            session.Pass(io, output, null);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.Space));
            session.Pass(io, output, null);
            Assert.Equal(0f, output.Get(AnalogKeyCodes.Space));
        }

        [Fact]
        public void Stream_Stop_DisablesOnce()
        {
            // rongyuan_stream_backend.cpp:183-188: 1B 00 at the session's end,
            // once.
            var time = new FakeTime();
            var session = StartedStream(new RyBoard { Board = 3590 }, time, Vid, 0x5030, out var io);
            int before = io.Writes("setf").Count;
            session.Stop(io);
            var after = io.Writes("setf").Skip(before).ToList();
            Assert.Equal(new[] { RongYuanProtocol.Request(0x1B, 0) }, after);
            Assert.False(session.StreamEnabled);
            session.Stop(io);
            Assert.Equal(before + 1, io.Writes("setf").Count);
            // A keyboard already gone: the disable is attempted and fails quietly.
            var gone = StartedStream(new RyBoard { Board = 3590 }, new FakeTime(), Vid, 0x5030, out var io2);
            io2.Gone = true;
            gone.Stop(io2);
            Assert.False(gone.StreamEnabled);
        }
    }
}
