using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PadForge.Engine;
using PadForge.Engine.Common.AnalogKeyboard;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// HallJoy's Neo65 SONIC HE+, SteelSeries Apex Pro and MCHOSE Mix 87 III
    /// routes (issue #468). None of these keyboards is on the bench, so each
    /// route is pinned against byte fixtures: the frames HallJoy's own builders
    /// print, the vectors of HallJoy's neo65_protocol_test.cpp,
    /// steelseries_apex_protocol_test.cpp, mchose_mix87_protocol_test.cpp and
    /// mchose_mix87_session_test.cpp, and scripted keyboards that answer the
    /// way HallJoy's parsers read. Each test cites the HallJoy source it pins
    /// (HallJoy commit 378f9fe, src/HallJoyProject/HallJoy unless the path
    /// says tests/).
    /// </summary>
    public class AnalogKeyboardNeoApexMixTests
    {
        // ── Shared ──

        private static byte[] Hex(string text)
            => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();

        private static byte[] Padded(int length, string head)
        {
            var r = new byte[length];
            Hex(head).CopyTo(r, 0);
            return r;
        }

        private static AnalogKeyboardDeviceInfo Info(ushort vid, ushort pid, ushort page, ushort usage,
            int input, int output, int feature = 0) => new()
        {
            Path = $@"\\?\hid#vid_{vid:x4}&pid_{pid:x4}#test",
            VendorId = vid,
            ProductId = pid,
            UsagePage = page,
            Usage = usage,
            InputReportLength = (ushort)input,
            OutputReportLength = (ushort)output,
            FeatureReportLength = (ushort)feature,
        };

        /// <summary>The scripted keyboard behind a scripted clock that moves
        /// only when a real one would: a read that waits in vain moves it by
        /// its whole wait, and each write by <see cref="WriteCost"/>. Every
        /// session test runs on it, so no deadline depends on how fast the
        /// test machine is. Chosen writes can fail without reaching the
        /// keyboard.</summary>
        private sealed class ClockedTransport : IAnalogKeyboardTransport
        {
            public readonly AnalogKeyboardTestTransport Inner;
            public long Now = 1000;
            public int WriteCost;
            public Func<byte[], bool> FailWrite = _ => false;

            public ClockedTransport(AnalogKeyboardTestTransport inner) => Inner = inner;

            // The scripted keyboard's own surface.
            public List<(string Kind, byte[] Data)> Log => Inner.Log;
            public List<byte[]> Writes(string kind) => Inner.Writes(kind);
            public int Discards => Inner.Discards;
            public void QueueInput(params byte[][] reports) => Inner.QueueInput(reports);

            public bool Gone
            {
                get => Inner.Gone;
                set => Inner.Gone = value;
            }

            public Func<byte[], IEnumerable<byte[]>> OnSend
            {
                get => Inner.OnSend;
                set => Inner.OnSend = value;
            }

            public bool Send(byte[] report)
            {
                Now += WriteCost;
                return !FailWrite(report) && Inner.Send(report);
            }

            public bool SendOutputReport(byte[] report)
            {
                Now += WriteCost;
                return !FailWrite(report) && Inner.SendOutputReport(report);
            }

            public int Receive(byte[] buffer, int timeoutMs)
            {
                int n = Inner.Receive(buffer, timeoutMs);
                if (n == 0) Now += Math.Max(timeoutMs, 1);
                return n;
            }

            public void DiscardStale() => Inner.DiscardStale();
            public bool SetFeature(byte[] report) => Inner.SetFeature(report);
            public int GetFeature(byte[] buffer) => Inner.GetFeature(buffer);
            public int InputLength => Inner.InputLength;
            public int OutputLength => Inner.OutputLength;
            public int FeatureLength => Inner.FeatureLength;
        }

        [Fact]
        public void Routes_FollowTartarusInHallJoysOrder()
        {
            // native_analog_backends.def:45-48: Tartarus, Neo65, SteelSeriesApex, MchoseMix87.
            var all = NeoApexMixRoutes.All;
            Assert.Equal(new[] { "halljoy-neo65", "halljoy-steelseries-apex", "halljoy-mchose-mix87" },
                all.Select(r => r.Id).ToArray());
            Assert.Equal(new[]
            {
                AnalogKeyboardProtocol.Neo65, AnalogKeyboardProtocol.SteelSeriesApex, AnalogKeyboardProtocol.MchoseMix87,
            }, all.Select(r => r.Protocol).ToArray());
            // neo65_backend.cpp:100 and steelseries_apex_backend.cpp:134 open
            // with share 0, mchose_mix87_backend.cpp:91 shares. All three write.
            Assert.True(all[0].Exclusive);
            Assert.True(all[1].Exclusive);
            Assert.False(all[2].Exclusive);
            Assert.All(all, r => Assert.True(r.Writable));
            Assert.All(all, r => Assert.Null(r.Companion));
        }

        // ── Neo65 SONIC HE+ ──

        private static readonly int[] Neo65Ansi = Neo65Protocol.Table(Neo65Protocol.AnsiProductId);
        private static readonly int[] Neo65Iso = Neo65Protocol.Table(Neo65Protocol.IsoProductId);

        /// <summary>A Neo65 answering as neo65_protocol.h reads it. Every slot
        /// that carries a key reports travel 33000 with dead zones of 300 and
        /// 400, HallJoy's own record (neo65_protocol_test.cpp:27-28), so its
        /// range is 300 to 32600. Empty slots report travel 0. Every depth
        /// starts at rest.</summary>
        private sealed class Neo65Keyboard
        {
            public int Stride = 9;
            public int[] Table = Neo65Ansi;
            public readonly int[] Depth = Enumerable.Repeat(300, 80).ToArray();
            public Func<int, (int Flag, int Axis, int Top, int Bottom)> Record;

            public Neo65Keyboard()
            {
                Record = slot => Table[slot] != 0 ? (1, 33000, 300, 400) : (0, 0, 0, 0);
            }

            public byte[] Answer(byte[] request)
            {
                Assert.Equal(33, request.Length);
                Assert.Equal(0x00, request[0]);
                Assert.Equal(0xD0, request[1]);
                var a = new byte[33];
                Array.Copy(request, a, 5);
                int start = request[3], count = request[4];
                switch (request[2])
                {
                    case 0xA9:
                        a[3] = (byte)Stride;
                        a[4] = 0;
                        break;
                    case 0xAA:
                        for (int i = 0; i < count; i++)
                        {
                            var (flag, axis, top, bottom) = Record(start + i);
                            int q = 5 + i * Stride;
                            a[q] = (byte)flag;
                            a[q + 1] = (byte)(axis >> 8);
                            a[q + 2] = (byte)axis;
                            a[q + 5] = (byte)(top >> 8);
                            a[q + 6] = (byte)top;
                            a[q + 7] = (byte)(bottom >> 8);
                            a[q + 8] = (byte)bottom;
                        }
                        break;
                    case 0xA6:
                        for (int i = 0; i < count; i++)
                        {
                            a[5 + 2 * i] = (byte)(Depth[start + i] >> 8);
                            a[6 + 2 * i] = (byte)Depth[start + i];
                        }
                        break;
                    default:
                        throw new InvalidOperationException("an opcode HallJoy never sends");
                }
                return a;
            }
        }

        private static ClockedTransport Neo65Transport(Func<byte[], IEnumerable<byte[]>> onSend)
            => new(new AnalogKeyboardTestTransport { InputLength = 33, OutputLength = 33, OnSend = onSend });

        private static ClockedTransport Neo65Transport(Neo65Keyboard kb)
            => Neo65Transport(r => new[] { kb.Answer(r) });

        private static Neo65Session Neo65(ClockedTransport io, ushort pid = Neo65Protocol.AnsiProductId)
            => new(pid) { Clock = () => io.Now };

        private static Neo65Session StartedNeo65(ClockedTransport io, ushort pid = Neo65Protocol.AnsiProductId)
        {
            var session = Neo65(io, pid);
            Assert.True(session.Start(io));
            return session;
        }

        [Fact]
        public void Neo65_Matches_AnsiAndIso_RejectsNearMisses()
        {
            // neo65_backend.cpp:59-63: E560:EE65/EF65, FF60:61, 33-byte in and out.
            Assert.True(Neo65Protocol.Matches(Info(0xE560, 0xEE65, 0xFF60, 0x61, 33, 33)));
            Assert.True(Neo65Protocol.Matches(Info(0xE560, 0xEF65, 0xFF60, 0x61, 33, 33)));
            Assert.False(Neo65Protocol.Matches(Info(0xE560, 0xEE66, 0xFF60, 0x61, 33, 33)));
            Assert.False(Neo65Protocol.Matches(Info(0xE561, 0xEE65, 0xFF60, 0x61, 33, 33)));
            Assert.False(Neo65Protocol.Matches(Info(0xE560, 0xEE65, 0xFF61, 0x61, 33, 33)));
            Assert.False(Neo65Protocol.Matches(Info(0xE560, 0xEE65, 0xFF60, 0x62, 33, 33)));
            Assert.False(Neo65Protocol.Matches(Info(0xE560, 0xEE65, 0x0001, 0x06, 33, 33)));
            Assert.False(Neo65Protocol.Matches(Info(0xE560, 0xEE65, 0xFF60, 0x61, 65, 65)));
            Assert.False(Neo65Protocol.Matches(Info(0xE560, 0xEE65, 0xFF60, 0x61, 33, 65)));
            // A Keychron VIA channel shares the usage but not the IDs.
            Assert.False(Neo65Protocol.Matches(Info(0x3434, 0x0B10, 0xFF60, 0x61, 33, 33)));
            Assert.False(Neo65Protocol.Matches(null));
        }

        [Fact]
        public void Neo65_Requests_AreHallJoysCompiledFrames()
        {
            // Spec 5.2 [compiled], neo65_protocol.h:17-20.
            Assert.Equal(Padded(33, "00 D0 A9 00 00"), Neo65Protocol.Request(0xA9));
            Assert.Equal(Padded(33, "00 D0 AA 00 03"), Neo65Protocol.Request(0xAA, 0, 3));
            Assert.Equal(Padded(33, "00 D0 A6 00 0E"), Neo65Protocol.Request(0xA6, 0, 14));
            Assert.Equal(Padded(33, "00 D0 A6 46 0A"), Neo65Protocol.Request(0xA6, 70, 10));
        }

        [Fact]
        public void Neo65_ParseRanges_HallJoysRecord_AndRejections()
        {
            // neo65_protocol_test.cpp:27-32: flag 1, travel 33000, top 300,
            // bottom 400 gives 300..32600. A rejected record leaves the ranges.
            var ranges = new Neo65Protocol.Range[80];
            var p = Neo65Protocol.Request(0xAA, 0, 1);
            Hex("01 80 E8 00 00 01 2C 01 90").CopyTo(p, 5);
            Assert.True(Neo65Protocol.ParseRanges(p, 0, 1, 9, ranges));
            Assert.Equal(300, ranges[0].Low);
            Assert.Equal(32600, ranges[0].High);
            Assert.Equal(0, Neo65Protocol.Normalize(300, ranges[0]));
            Assert.Equal(1000, Neo65Protocol.Normalize(32600, ranges[0]));
            int previous = 0;
            for (int raw = 0; raw <= 40000; raw++)
            {
                int n = Neo65Protocol.Normalize(raw, ranges[0]);
                Assert.InRange(n, previous, 1000);
                previous = n;
            }

            p[10] = 0xFF; // top 0xFF2C: the dead zones no longer fit the travel
            Assert.False(Neo65Protocol.ParseRanges(p, 0, 1, 9, ranges));
            Assert.Equal(300, ranges[0].Low);
            p[10] = 0x01;
            Assert.False(Neo65Protocol.ParseRanges(p, 0, 1, 29, ranges)); // stride past 28

            // neo65_protocol.h:33-36: the flag gates the dead zones, travel is
            // rounded down to 100, and must lie in 1000..40000.
            p[5] = 0;
            Assert.True(Neo65Protocol.ParseRanges(p, 0, 1, 9, ranges));
            Assert.Equal(0, ranges[0].Low);
            Assert.Equal(33000, ranges[0].High);
            Hex("01 80 FF").CopyTo(p, 5); // 0x80FF = 33023 rounds to 33000
            Assert.True(Neo65Protocol.ParseRanges(p, 0, 1, 9, ranges));
            Assert.Equal(32600, ranges[0].High);
            Hex("01 03 E7").CopyTo(p, 5); // 999 rounds to 900: too short
            Assert.False(Neo65Protocol.ParseRanges(p, 0, 1, 9, ranges));
            Hex("01 9C 41").CopyTo(p, 5); // 40001 rounds to 40000: allowed
            Assert.True(Neo65Protocol.ParseRanges(p, 0, 1, 9, ranges));
            Hex("01 9C A4").CopyTo(p, 5); // 40100
            Assert.False(Neo65Protocol.ParseRanges(p, 0, 1, 9, ranges));
            Hex("01 00 63").CopyTo(p, 5); // 99 rounds to 0: an empty slot
            Assert.True(Neo65Protocol.ParseRanges(p, 0, 1, 9, ranges));
            Assert.True(ranges[0].IsEmpty);

            // neo65_protocol.h:30: count at most 28 / stride, and the echo.
            Assert.False(Neo65Protocol.ParseRanges(Neo65Protocol.Request(0xAA, 0, 4), 0, 4, 9, ranges));
            Assert.False(Neo65Protocol.ParseRanges(Neo65Protocol.Request(0xAA, 3, 3), 0, 3, 9, ranges));
            Assert.False(Neo65Protocol.ParseRanges(Neo65Protocol.Request(0xA6, 0, 3), 0, 3, 9, ranges));
        }

        [Fact]
        public void Neo65_ParseDepth_HallJoysPages()
        {
            // neo65_protocol_test.cpp:15-22 and 32: depth (slot * 400) in six
            // pages. A wrong count or a value past 40000 rejects the page and
            // leaves the values.
            var values = new ushort[80];
            for (int start = 0; start < 80; start += 14)
            {
                int count = Math.Min(80 - start, 14);
                var p = Neo65Protocol.Request(0xA6, start, count);
                for (int i = 0; i < count; i++)
                {
                    int raw = (start + i) * 400;
                    p[5 + i * 2] = (byte)(raw >> 8);
                    p[6 + i * 2] = (byte)raw;
                }
                Assert.True(Neo65Protocol.ParseDepth(p, start, count, values));
                var saved = (ushort[])values.Clone();
                p[4]++;
                Assert.False(Neo65Protocol.ParseDepth(p, start, count, values));
                Assert.Equal(saved, values);
                p[4]--;
                p[5] = 255;
                Assert.False(Neo65Protocol.ParseDepth(p, start, count, values));
                Assert.Equal(saved, values);
            }
            for (int i = 0; i < 80; i++) Assert.Equal(i * 400, values[i]);
            Assert.False(Neo65Protocol.ParseDepth(Neo65Protocol.Request(0xA6, 79, 2), 79, 2, values));
            Assert.False(Neo65Protocol.ParseDepth(Neo65Protocol.Request(0xA6, 0, 15), 0, 15, values));
        }

        [Fact]
        public void Neo65_Tables_AreTheFactoryKeys()
        {
            // neo65_protocol_test.cpp:10-14: Fn (0x409) at 75, W A S D at 18
            // 33 34 35, 67 ANSI and 68 ISO keys, none twice. Spec 5.4: ISO
            // differs at 29 (Enter), 45 (Non-US #) and 49 (Non-US \).
            Assert.Equal(80, Neo65Ansi.Length);
            Assert.Equal(80, Neo65Iso.Length);
            foreach (var (table, keys) in new[] { (Neo65Ansi, 67), (Neo65Iso, 68) })
            {
                Assert.Equal(AnalogKeyCodes.Fn, table[75]);
                Assert.Equal(AnalogKeyCodes.W, table[18]);
                Assert.Equal(AnalogKeyCodes.A, table[33]);
                Assert.Equal(AnalogKeyCodes.S, table[34]);
                Assert.Equal(AnalogKeyCodes.D, table[35]);
                var nonzero = table.Where(c => c != 0).ToArray();
                Assert.Equal(keys, nonzero.Length);
                Assert.Equal(keys, nonzero.Distinct().Count());
                Assert.Equal(keys, AnalogKeyboardData.KeysOf(table).Length);
            }
            Assert.Equal(AnalogKeyCodes.Backslash, Neo65Ansi[29]);
            Assert.Equal(AnalogKeyCodes.Enter, Neo65Ansi[45]);
            Assert.Equal(0, Neo65Ansi[49]);
            Assert.Equal(AnalogKeyCodes.Enter, Neo65Iso[29]);
            Assert.Equal(AnalogKeyCodes.IntlHash, Neo65Iso[45]);
            Assert.Equal(AnalogKeyCodes.IntlBackslash, Neo65Iso[49]);
            for (int i = 0; i < 80; i++)
                if (i != 29 && i != 45 && i != 49) Assert.Equal(Neo65Ansi[i], Neo65Iso[i]);
        }

        [Fact]
        public void Neo65_Start_ReadsStrideRangesThenOneProofFrame()
        {
            // neo65_backend.cpp:104-135: A9, then 27 AA pages of 28 / 9 = 3
            // records, every key slot ranged, then one complete six-page A6
            // frame before the collection is claimed. Nothing else is sent.
            var kb = new Neo65Keyboard();
            var io = Neo65Transport(kb);
            var session = StartedNeo65(io);
            var writes = io.Writes("out");
            Assert.Equal(1 + 27 + 6, writes.Count);
            Assert.Equal(Neo65Protocol.Request(0xA9), writes[0]);
            for (int page = 0; page < 27; page++)
                Assert.Equal(Neo65Protocol.Request(0xAA, page * 3, page < 26 ? 3 : 2), writes[1 + page]);
            int[] starts = { 0, 14, 28, 42, 56, 70 };
            for (int page = 0; page < 6; page++)
                Assert.Equal(Neo65Protocol.Request(0xA6, starts[page], page < 5 ? 14 : 10), writes[28 + page]);
            Assert.Equal(io.Log.Count, writes.Count);
            Assert.Equal(0, io.Discards);

            Assert.Equal(9, session.Stride);
            Assert.Equal(300, session.RangeAt(18).Low);
            Assert.Equal(32600, session.RangeAt(18).High);
            Assert.True(session.RangeAt(15).IsEmpty);
            Assert.Equal("Neo65 SONIC HE+", session.ModelName);
            Assert.Equal(AnalogKeyboardData.KeysOf(Neo65Ansi), session.KeyOrder);
            Assert.Equal(67, session.KeyOrder.Length);
            Assert.Equal(AnalogKeyCodes.Escape, session.KeyOrder[0]);
            Assert.Contains(AnalogKeyCodes.Fn, session.KeyOrder);
            Assert.Equal(1, session.MissLimit);
        }

        [Fact]
        public void Neo65_Start_Iso_AndWiderRecords()
        {
            // neo65_protocol.h:12 picks the ISO table for EF65. A 10-byte
            // record fits two per page: 40 AA requests (neo65_backend.cpp:111).
            var kb = new Neo65Keyboard { Table = Neo65Iso, Stride = 10 };
            var io = Neo65Transport(kb);
            var session = StartedNeo65(io, Neo65Protocol.IsoProductId);
            var ranges = io.Writes("out").Where(w => w[2] == 0xAA).ToList();
            Assert.Equal(40, ranges.Count);
            Assert.Equal(Neo65Protocol.Request(0xAA, 78, 2), ranges[39]);
            Assert.Equal(68, session.KeyOrder.Length);
            Assert.Contains(AnalogKeyCodes.IntlBackslash, session.KeyOrder);
            Assert.Contains(AnalogKeyCodes.IntlHash, session.KeyOrder);
        }

        [Theory]
        [InlineData(8)]
        [InlineData(29)]
        [InlineData(0)]
        public void Neo65_Start_StrideOutOfRange_StopsAfterTheQuery(int stride)
        {
            // neo65_backend.cpp:107-108: the record size must be 9 to 28.
            var kb = new Neo65Keyboard { Stride = stride };
            var io = Neo65Transport(kb);
            Assert.False(Neo65(io).Start(io));
            Assert.Single(io.Writes("out"));
        }

        [Fact]
        public void Neo65_Start_KeySlotWithoutRange_IsRefused()
        {
            // neo65_backend.cpp:115-117: every slot that carries a key must
            // have a range. W reports no travel here.
            var kb = new Neo65Keyboard();
            kb.Record = slot => slot == 18 || kb.Table[slot] == 0 ? (0, 0, 0, 0) : (1, 33000, 300, 400);
            var io = Neo65Transport(kb);
            Assert.False(Neo65(io).Start(io));
            Assert.DoesNotContain(io.Writes("out"), w => w[2] == 0xA6);
        }

        [Fact]
        public void Neo65_Start_InvalidRangeRecord_IsRefused()
        {
            // neo65_protocol.h:36 via neo65_backend.cpp:112: dead zones that
            // fill the travel reject the page and the handshake.
            var kb = new Neo65Keyboard();
            kb.Record = slot => kb.Table[slot] == 0 ? (0, 0, 0, 0)
                : slot == 40 ? (1, 1000, 600, 400) : (1, 33000, 300, 400);
            var io = Neo65Transport(kb);
            Assert.False(Neo65(io).Start(io));
            // Page 13 (slots 39..41) was the last request.
            Assert.Equal(Neo65Protocol.Request(0xAA, 39, 3), io.Writes("out").Last());
        }

        [Fact]
        public void Neo65_Exchange_SkipsOtherAnswers_UpTo16()
        {
            // neo65_backend.cpp:91-93: up to 16 reads per request, answers
            // whose header does not echo the request skipped.
            var kb = new Neo65Keyboard();
            var stale = Padded(33, "00 D0 A7 00 00"); // echoes no request the route sends
            int skipped = 15;
            var io = Neo65Transport(r => Enumerable.Repeat(stale, skipped).Append(kb.Answer(r)).ToArray());
            Assert.True(Neo65(io).Start(io));

            skipped = 16;
            var refused = Neo65Transport(r => Enumerable.Repeat(stale, skipped).Append(kb.Answer(r)).ToArray());
            Assert.False(Neo65(refused).Start(refused));
            Assert.Single(refused.Writes("out"));
        }

        [Fact]
        public void Neo65_Start_ShortAnswer_Silence_OrGoneDevice_IsRefused()
        {
            // neo65_backend.cpp:90-92: every transfer must be 33 bytes, and
            // the exchange fails at its 100 ms deadline.
            var kb = new Neo65Keyboard();
            var shortAnswer = Neo65Transport(r => new[] { kb.Answer(r).Take(32).ToArray() });
            Assert.False(Neo65(shortAnswer).Start(shortAnswer));

            var silent = Neo65Transport(_ => Array.Empty<byte[]>());
            Assert.False(Neo65(silent).Start(silent));
            Assert.Single(silent.Writes("out"));

            var gone = Neo65Transport(kb);
            gone.Gone = true;
            Assert.False(Neo65(gone).Start(gone));
        }

        [Fact]
        public void Neo65_Start_BadProofFrame_IsRefused()
        {
            // neo65_backend.cpp:127-133: the collection is claimed only after
            // one complete valid frame. A depth past 40000 rejects it.
            var kb = new Neo65Keyboard();
            kb.Depth[50] = 40001;
            var io = Neo65Transport(kb);
            Assert.False(Neo65(io).Start(io));
        }

        [Fact]
        public void Neo65_Pass_PublishesNormalizedFactoryKeys()
        {
            // neo65_backend.cpp:123-137 and neo65_protocol.h:41-45: six A6
            // pages, then every key slot normalized to its own range. An empty
            // slot is never published.
            var kb = new Neo65Keyboard();
            var io = Neo65Transport(kb);
            var session = StartedNeo65(io);
            int before = io.Log.Count;
            kb.Depth[18] = 16450; // W: (16150 * 1000 + 16150) / 32300 = 500
            kb.Depth[75] = 32600; // Fn at the bottom of its range
            kb.Depth[33] = 300;   // A at the top dead zone: rest
            kb.Depth[15] = 40000; // an empty slot
            kb.Depth[76] = 32601; // Right Shift past the bottom
            var output = new AnalogKeyInputState();
            output.Set(AnalogKeyCodes.Q, 0.7f);
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(6, io.Log.Count - before);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.Fn));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.RShift));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.Q)); // a frame is a full snapshot
            Assert.Equal(3, output.Count);
        }

        [Fact]
        public void Neo65_Pass_Failures_EndTheSession()
        {
            // neo65_backend.cpp:127-130: a rejected page or a failed exchange
            // ends the session, MissLimit 1.
            var kb = new Neo65Keyboard();
            var io = Neo65Transport(kb);
            var session = StartedNeo65(io);
            var output = new AnalogKeyInputState();

            kb.Depth[20] = 40001;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
            kb.Depth[20] = 300;

            io.OnSend = _ => Array.Empty<byte[]>();
            Assert.Equal(AnalogPollResult.NoAnswer, session.Pass(io, output, null));

            io.OnSend = r => new[] { kb.Answer(r) };
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            io.Gone = true;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
        }

        [Fact]
        public void Neo65_Pass_FrameSlowerThan100Ms_EndsTheSession()
        {
            // neo65_backend.cpp:130: six pages must finish within 100 ms,
            // each exchange within its own 100 ms (line 89).
            var kb = new Neo65Keyboard();
            var clocked = Neo65Transport(kb);
            var session = Neo65(clocked);
            Assert.True(session.Start(clocked));
            var output = new AnalogKeyInputState();
            clocked.WriteCost = 16; // 6 x 16 = 96 ms
            Assert.Equal(AnalogPollResult.Ok, session.Pass(clocked, output, null));
            clocked.WriteCost = 17; // 102 ms
            Assert.Equal(AnalogPollResult.NoAnswer, session.Pass(clocked, output, null));
            clocked.WriteCost = 101; // one write past its exchange deadline
            Assert.Equal(AnalogPollResult.NoAnswer, session.Pass(clocked, output, null));
        }

        [Fact]
        public void Neo65_Stop_SendsNothing()
        {
            // neo65_backend.cpp:142 and spec 5.2: nothing is sent at shutdown.
            var kb = new Neo65Keyboard();
            var io = Neo65Transport(kb);
            var session = StartedNeo65(io);
            int before = io.Log.Count;
            session.Stop(io);
            Assert.Equal(before, io.Log.Count);
        }

        // ── SteelSeries Apex Pro ──

        private static readonly int[] ApexMap = SteelSeriesApexProtocol.Map;

        private static void Put16(byte[] p, int at, int value)
        {
            p[at] = (byte)value;
            p[at + 1] = (byte)(value >> 8);
        }

        /// <summary>An Apex Pro answering as steelseries_apex_protocol.h reads
        /// it. Firmware 4.16.8, every sensor calibrated to 1000..3000 unless
        /// <see cref="Calibration"/> says otherwise, and D7 banks carrying a
        /// raw sample of 1100 and <see cref="Filtered"/> per sensor.</summary>
        private sealed class ApexKeyboard
        {
            public string Version = "4.16.8";
            public Func<int, (int Max, int Min)> Calibration = _ => (3000, 1000);
            public readonly int[] Filtered = Enumerable.Repeat(1000, 70).ToArray();
            public Func<byte[], byte[]> Tamper = a => a;

            public byte[] Answer(byte[] request)
            {
                Assert.Equal(65, request.Length);
                Assert.True(SteelSeriesApexProtocol.ReadOnlyRequest(request));
                var a = new byte[65];
                switch (request[1])
                {
                    case 0x90:
                        Encoding.ASCII.GetBytes(Version).CopyTo(a, 1);
                        break;
                    case 0xDA:
                        for (int i = 0; i < request[2]; i++)
                        {
                            int hid = request[3 + i * 5];
                            var (max, min) = Calibration(Array.IndexOf(ApexMap, hid));
                            a[1 + i * 5] = (byte)hid;
                            Put16(a, 2 + i * 5, max);
                            Put16(a, 4 + i * 5, min);
                        }
                        break;
                    case 0xD7:
                        for (int i = 0; i < 14; i++)
                        {
                            Put16(a, 1 + 2 * i, 1100);
                            Put16(a, 29 + 2 * i, Filtered[(request[2] - 1) * 14 + i]);
                        }
                        break;
                }
                return Tamper(a);
            }
        }

        private static ClockedTransport ApexTransport(ApexKeyboard kb)
            => new(new AnalogKeyboardTestTransport { OnSend = r => new[] { kb.Answer(r) } });

        private static SteelSeriesApexSession Apex(ClockedTransport io, ushort pid = SteelSeriesApexProtocol.ApexProProductId)
            => new(pid) { Clock = () => io.Now };

        [Fact]
        public void Apex_Matches_ThreeIdentities_RejectsNearMisses()
        {
            // steelseries_apex_protocol_test.cpp:13-18 and
            // steelseries_apex_protocol.h:8, 12-14.
            foreach (ushort pid in new ushort[] { 0x1610, 0x1614, 0x1640 })
                Assert.True(SteelSeriesApexProtocol.Matches(Info(0x1038, pid, 0xFFC0, 1, 65, 65, 643)));
            // Apex Pro Mini, TKL 2023, TKL Gen 3, Mini Gen 3 and others run
            // other firmware families and are not admitted.
            foreach (ushort pid in new ushort[] { 0x1612, 0x1618, 0x161E, 0x1628, 0x1642, 0x1648 })
                Assert.False(SteelSeriesApexProtocol.Matches(Info(0x1038, pid, 0xFFC0, 1, 65, 65, 643)));
            Assert.False(SteelSeriesApexProtocol.Matches(Info(0x1CA6, 0x1614, 0xFFC0, 1, 65, 65, 643)));
            Assert.False(SteelSeriesApexProtocol.Matches(Info(0x1038, 0x1610, 0xFFB0, 1, 65, 65, 643)));
            Assert.False(SteelSeriesApexProtocol.Matches(Info(0x1038, 0x1610, 0xFFC0, 1, 65, 65, 65)));
            Assert.False(SteelSeriesApexProtocol.Matches(Info(0x1038, 0x1610, 0xFFC0, 2, 65, 65, 643)));
            Assert.False(SteelSeriesApexProtocol.Matches(Info(0x1038, 0x1610, 0xFFC0, 1, 64, 65, 643)));
            Assert.Equal("SteelSeries Apex Pro", SteelSeriesApexProtocol.ModelName(0x1610));
            Assert.Equal("SteelSeries Apex Pro TKL", SteelSeriesApexProtocol.ModelName(0x1614));
            Assert.Equal("SteelSeries Apex Pro Gen 3", SteelSeriesApexProtocol.ModelName(0x1640));
        }

        [Fact]
        public void Apex_Requests_AreHallJoysCompiledFrames()
        {
            // Spec 6.2 [compiled]: the version, the five banks and HallJoy's six
            // range requests byte for byte (steelseries_apex_protocol.h:23-31).
            Assert.Equal(Padded(65, "00 90"), SteelSeriesApexProtocol.VersionRequest());
            for (int bank = 1; bank <= 5; bank++)
                Assert.Equal(Padded(65, $"00 D7 {bank:X2}"), SteelSeriesApexProtocol.DepthRequest(bank));
            string[] ranges =
            {
                "00 DA 0C 35 00 00 00 00 1E 00 00 00 00 1F 00 00 00 00 20 00 00 00 00 21 00 00 00 00 22 00 00 00 00 23 00 00 00 00 24 00 00 00 00 25 00 00 00 00 26 00 00 00 00 27 00 00 00 00 2D 00 00 00 00 00 00",
                "00 DA 0C 2E 00 00 00 00 89 00 00 00 00 2B 00 00 00 00 14 00 00 00 00 1A 00 00 00 00 08 00 00 00 00 15 00 00 00 00 17 00 00 00 00 1C 00 00 00 00 18 00 00 00 00 0C 00 00 00 00 12 00 00 00 00 00 00",
                "00 DA 0C 13 00 00 00 00 2F 00 00 00 00 30 00 00 00 00 31 00 00 00 00 39 00 00 00 00 04 00 00 00 00 16 00 00 00 00 07 00 00 00 00 09 00 00 00 00 0A 00 00 00 00 0B 00 00 00 00 0D 00 00 00 00 00 00",
                "00 DA 0C 0E 00 00 00 00 0F 00 00 00 00 33 00 00 00 00 34 00 00 00 00 32 00 00 00 00 28 00 00 00 00 E1 00 00 00 00 64 00 00 00 00 1D 00 00 00 00 1B 00 00 00 00 06 00 00 00 00 19 00 00 00 00 00 00",
                "00 DA 0C 05 00 00 00 00 11 00 00 00 00 10 00 00 00 00 36 00 00 00 00 37 00 00 00 00 38 00 00 00 00 87 00 00 00 00 E5 00 00 00 00 E0 00 00 00 00 E3 00 00 00 00 E2 00 00 00 00 8B 00 00 00 00 00 00",
                "00 DA 08 2C 00 00 00 00 8A 00 00 00 00 88 00 00 00 00 E6 00 00 00 00 E7 00 00 00 00 F0 00 00 00 00 E4 00 00 00 00 2A 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00",
            };
            for (int i = 0; i < 6; i++)
            {
                var expected = Hex(ranges[i]);
                Assert.Equal(65, expected.Length);
                Assert.Equal(expected, SteelSeriesApexProtocol.RangeRequest(i * 12, i < 5 ? 12 : 8));
            }
        }

        [Fact]
        public void Apex_ReadOnlyAllowlist_HallJoysVectors()
        {
            // steelseries_apex_protocol_test.cpp:20, 22, 27, 30-31 and 36, and
            // steelseries_apex_protocol.h:32-39.
            Assert.True(SteelSeriesApexProtocol.ReadOnlyRequest(SteelSeriesApexProtocol.VersionRequest()));
            for (int bank = 1; bank <= 5; bank++)
                Assert.True(SteelSeriesApexProtocol.ReadOnlyRequest(SteelSeriesApexProtocol.DepthRequest(bank)));
            Assert.False(SteelSeriesApexProtocol.ReadOnlyRequest(SteelSeriesApexProtocol.DepthRequest(0)));
            Assert.False(SteelSeriesApexProtocol.ReadOnlyRequest(SteelSeriesApexProtocol.DepthRequest(6)));
            for (int start = 0; start < 68; start += 12)
            {
                var request = SteelSeriesApexProtocol.RangeRequest(start, Math.Min(12, 68 - start));
                Assert.True(SteelSeriesApexProtocol.ReadOnlyRequest(request));
                var unsafeRequest = (byte[])request.Clone();
                unsafeRequest[1] &= 0x7F; // DA becomes 5A, a write
                Assert.False(SteelSeriesApexProtocol.ReadOnlyRequest(unsafeRequest));
            }
            Assert.False(SteelSeriesApexProtocol.ReadOnlyRequest(SteelSeriesApexProtocol.RangeRequest(0, 13)));
            Assert.False(SteelSeriesApexProtocol.ReadOnlyRequest(SteelSeriesApexProtocol.RangeRequest(67, 2)));
            // Anything that is not exactly one of the three shapes.
            Assert.False(SteelSeriesApexProtocol.ReadOnlyRequest(Padded(65, "00 01 02")));
            Assert.False(SteelSeriesApexProtocol.ReadOnlyRequest(Padded(65, "00 90 00 01")));
            Assert.False(SteelSeriesApexProtocol.ReadOnlyRequest(Padded(65, "01 90")));
            Assert.False(SteelSeriesApexProtocol.ReadOnlyRequest(Padded(64, "00 90")));
            var shifted = SteelSeriesApexProtocol.RangeRequest(0, 12);
            shifted[3] = 0x1E; // not the codes of any 12 consecutive sensors
            Assert.False(SteelSeriesApexProtocol.ReadOnlyRequest(shifted));
        }

        [Fact]
        public void Apex_KnownVersion_Is4168WithItsNul()
        {
            // steelseries_apex_protocol_test.cpp:20, steelseries_apex_protocol.h:24.
            var version = new byte[65];
            Encoding.ASCII.GetBytes("4.16.8").CopyTo(version, 1);
            Assert.True(SteelSeriesApexProtocol.KnownVersion(version));
            version[6] = (byte)'9';
            Assert.False(SteelSeriesApexProtocol.KnownVersion(version));
            version[6] = (byte)'8';
            version[7] = (byte)'1'; // "4.16.81": the NUL is part of the check
            Assert.False(SteelSeriesApexProtocol.KnownVersion(version));
        }

        [Fact]
        public void Apex_ParseDepth_HallJoysBanks()
        {
            // steelseries_apex_protocol_test.cpp:21-26: raw 1100 + n, filtered
            // 1200 + n, the filtered group kept. Padding or a sample past 4095
            // rejects the bank and leaves the values.
            var values = new ushort[70];
            for (int bank = 1; bank <= 5; bank++)
            {
                var p = new byte[65];
                for (int i = 0; i < 14; i++)
                {
                    Put16(p, 1 + 2 * i, 1100 + (bank - 1) * 14 + i);
                    Put16(p, 29 + 2 * i, 1200 + (bank - 1) * 14 + i);
                }
                Assert.True(SteelSeriesApexProtocol.ParseDepth(p, bank, values));
                for (int i = 0; i < 14; i++) Assert.Equal(1200 + (bank - 1) * 14 + i, values[(bank - 1) * 14 + i]);
                var previous = (ushort[])values.Clone();
                p[64] = 1;
                Assert.False(SteelSeriesApexProtocol.ParseDepth(p, bank, values));
                Assert.Equal(previous, values);
                p[64] = 0;
                Put16(p, 1, 4096);
                Assert.False(SteelSeriesApexProtocol.ParseDepth(p, bank, values));
                Assert.Equal(previous, values);
            }
            Assert.False(SteelSeriesApexProtocol.ParseDepth(new byte[65], 0, values));
            Assert.False(SteelSeriesApexProtocol.ParseDepth(new byte[65], 6, values));
            var numbered = new byte[65];
            numbered[0] = 1;
            Assert.False(SteelSeriesApexProtocol.ParseDepth(numbered, 1, values));
        }

        [Fact]
        public void Apex_ParseRanges_HallJoysRecords()
        {
            // steelseries_apex_protocol_test.cpp:28-35: (HID, max, min)
            // records with the HID echoed, and steelseries_apex_protocol.h:47-50.
            var ranges = new SteelSeriesApexProtocol.Range[70];
            for (int start = 0; start < 68; start += 12)
            {
                int count = Math.Min(12, 68 - start);
                var p = new byte[65];
                for (int i = 0; i < count; i++)
                {
                    p[1 + i * 5] = (byte)ApexMap[start + i];
                    Put16(p, 2 + i * 5, 3000 + start + i);
                    Put16(p, 4 + i * 5, 1000 + start + i);
                }
                Assert.True(SteelSeriesApexProtocol.ParseRanges(p, start, count, ranges));
                for (int i = 0; i < count; i++)
                {
                    Assert.Equal(3000 + start + i, ranges[start + i].High);
                    Assert.Equal(1000 + start + i, ranges[start + i].Low);
                }
                p[1] ^= 1;
                Assert.False(SteelSeriesApexProtocol.ParseRanges(p, start, count, ranges));
            }

            // A maximum within 16 of the minimum, or reversed factory extrema,
            // is no calibration. Bytes after the records must be zero.
            var q = new byte[65];
            q[1] = (byte)ApexMap[0];
            Put16(q, 2, 1016);
            Put16(q, 4, 1000);
            Assert.True(SteelSeriesApexProtocol.ParseRanges(q, 0, 1, ranges));
            Assert.True(ranges[0].IsEmpty);
            Put16(q, 2, 1017);
            Assert.True(SteelSeriesApexProtocol.ParseRanges(q, 0, 1, ranges));
            Assert.False(ranges[0].IsEmpty);
            q[64] = 1;
            Assert.False(SteelSeriesApexProtocol.ParseRanges(q, 0, 1, ranges));
            q[64] = 0;
            Put16(q, 4, 4096);
            Assert.False(SteelSeriesApexProtocol.ParseRanges(q, 0, 1, ranges));
        }

        [Fact]
        public void Apex_Normalize_HallJoysVectors()
        {
            // steelseries_apex_protocol_test.cpp:37-38.
            var range = new SteelSeriesApexProtocol.Range(1000, 3000);
            Assert.Equal(0, SteelSeriesApexProtocol.Normalize(1000, range));
            Assert.Equal(500, SteelSeriesApexProtocol.Normalize(2000, range));
            Assert.Equal(1000, SteelSeriesApexProtocol.Normalize(4000, range));
            Assert.Equal(0, SteelSeriesApexProtocol.Normalize(2000, new SteelSeriesApexProtocol.Range(3000, 1000)));
            int previous = 0;
            for (int raw = 0; raw <= 4095; raw++)
            {
                int v = SteelSeriesApexProtocol.Normalize(raw, range);
                Assert.InRange(v, previous, 1000);
                previous = v;
            }
        }

        [Fact]
        public void Apex_Map_IsTheRomInverse_WithoutTheOemCode()
        {
            // steelseries_apex_protocol_test.cpp:39: W A S D at sensors 16 29
            // 30 31. steelseries_apex_protocol.h:19: OEM 240 at 65, 68 and 69
            // spare. steelseries_apex_backend.cpp:153 never binds 240.
            Assert.Equal(70, ApexMap.Length);
            Assert.Equal(AnalogKeyCodes.W, ApexMap[16]);
            Assert.Equal(AnalogKeyCodes.A, ApexMap[29]);
            Assert.Equal(AnalogKeyCodes.S, ApexMap[30]);
            Assert.Equal(AnalogKeyCodes.D, ApexMap[31]);
            Assert.Equal(240, ApexMap[65]);
            Assert.Equal(0, ApexMap[68]);
            Assert.Equal(0, ApexMap[69]);
            var keys = AnalogKeyboardData.KeysOf(SteelSeriesApexProtocol.BindableTable());
            Assert.Equal(67, keys.Length);
            Assert.DoesNotContain(240, keys);
            Assert.DoesNotContain(AnalogKeyCodes.Escape, keys); // OmniPoint section only
            Assert.Contains(137, keys); // International3 (Yen)
            Assert.Contains(AnalogKeyCodes.IntlBackslash, keys);
        }

        [Fact]
        public void Apex_Start_VersionCalibrationThenOneProofFrame()
        {
            // steelseries_apex_backend.cpp:144-184: 90, six DA, then five D7
            // banks before the collection is claimed. Nothing else is sent.
            var kb = new ApexKeyboard();
            var io = ApexTransport(kb);
            var session = Apex(io, 0x1614);
            Assert.True(session.Start(io));
            var writes = io.Writes("out");
            Assert.Equal(1 + 6 + 5, writes.Count);
            Assert.Equal(io.Log.Count, writes.Count);
            Assert.Equal(SteelSeriesApexProtocol.VersionRequest(), writes[0]);
            for (int i = 0; i < 6; i++)
                Assert.Equal(SteelSeriesApexProtocol.RangeRequest(i * 12, i < 5 ? 12 : 8), writes[1 + i]);
            for (int bank = 1; bank <= 5; bank++)
                Assert.Equal(SteelSeriesApexProtocol.DepthRequest(bank), writes[6 + bank]);
            Assert.Equal(0, io.Discards);
            Assert.Equal("SteelSeries Apex Pro TKL", session.ModelName);
            Assert.Equal(AnalogKeyboardData.KeysOf(SteelSeriesApexProtocol.BindableTable()), session.KeyOrder);
            Assert.Equal(1000, session.RangeAt(16).Low);
            Assert.Equal(3000, session.RangeAt(16).High);
            Assert.Equal(1, session.MissLimit);
        }

        [Theory]
        [InlineData("4.16.9")]
        [InlineData("1.19.7")]
        [InlineData("")]
        public void Apex_Start_OtherFirmware_GetsOnlyTheVersionQuery(string version)
        {
            // steelseries_apex_backend.cpp:145-149: no other command for any
            // firmware but 4.16.8.
            var kb = new ApexKeyboard { Version = version };
            var io = ApexTransport(kb);
            Assert.False(Apex(io).Start(io));
            Assert.Single(io.Log);
        }

        [Fact]
        public void Apex_Start_MainBlockWithoutCalibration_IsRefused()
        {
            // steelseries_apex_backend.cpp:125-129: sensors 14 to 39 must all
            // be calibrated. Sensor 20 is within 16.
            var kb = new ApexKeyboard { Calibration = s => s == 20 ? (1010, 1000) : (3000, 1000) };
            var io = ApexTransport(kb);
            Assert.False(Apex(io).Start(io));
            Assert.Equal(1 + 6, io.Log.Count);
        }

        [Fact]
        public void Apex_Start_UncalibratedRegionalSensor_IsNotBound()
        {
            // steelseries_apex_protocol.h:47 and steelseries_apex_backend.cpp:153:
            // sensor 13 (International3) keeps reversed extrema, so it is not
            // bound and never published.
            var kb = new ApexKeyboard { Calibration = s => s == 13 ? (1000, 3000) : (3000, 1000) };
            var io = ApexTransport(kb);
            var session = Apex(io, 0x1640);
            Assert.True(session.Start(io));
            Assert.Equal(66, session.KeyOrder.Length);
            Assert.DoesNotContain(137, session.KeyOrder);
            kb.Filtered[13] = 4000;
            var output = new AnalogKeyInputState();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0f, output.Get(137));
        }

        [Fact]
        public void Apex_Pass_PublishesFilteredSamples()
        {
            // steelseries_apex_backend.cpp:172-186: five banks, the filtered
            // group normalized between the calibration extrema.
            var kb = new ApexKeyboard();
            var io = ApexTransport(kb);
            var session = Apex(io);
            Assert.True(session.Start(io));
            int before = io.Log.Count;
            kb.Filtered[16] = 2000; // W halfway
            kb.Filtered[29] = 3000; // A at the calibrated maximum
            kb.Filtered[30] = 1000; // S at rest
            kb.Filtered[65] = 3000; // the OEM sensor: never bound
            var output = new AnalogKeyInputState();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(5, io.Log.Count - before);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.S));
            Assert.Equal(0f, output.Get(240));
            Assert.Equal(2, output.Count);
        }

        [Fact]
        public void Apex_Pass_RereadsCalibrationEvery2Seconds()
        {
            // steelseries_apex_backend.cpp:163-171: after 2000 ms the six DA
            // requests go out again before the banks, and the new extrema apply.
            var kb = new ApexKeyboard();
            var clocked = ApexTransport(kb);
            var session = Apex(clocked);
            Assert.True(session.Start(clocked));
            var output = new AnalogKeyInputState();
            int before = clocked.Inner.Log.Count;
            clocked.Now += 1999;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(clocked, output, null));
            Assert.Equal(5, clocked.Inner.Log.Count - before);

            kb.Calibration = _ => (3500, 1500);
            kb.Filtered[16] = 2500;
            before = clocked.Inner.Log.Count;
            clocked.Now += 1;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(clocked, output, null));
            var writes = clocked.Inner.Writes("out").Skip(before).ToList();
            Assert.Equal(11, writes.Count);
            Assert.All(writes.Take(6), w => Assert.Equal(0xDA, w[1]));
            Assert.All(writes.Skip(6), w => Assert.Equal(0xD7, w[1]));
            Assert.Equal(1500, session.RangeAt(16).Low);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));

            before = clocked.Inner.Log.Count;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(clocked, output, null));
            Assert.Equal(5, clocked.Inner.Log.Count - before);
        }

        [Fact]
        public void Apex_Pass_ChangedCalibratedSet_EndsTheSession()
        {
            // steelseries_apex_backend.cpp:168-169: a sensor that gains or
            // loses its calibration needs a new handshake.
            var kb = new ApexKeyboard();
            var clocked = ApexTransport(kb);
            var session = Apex(clocked);
            Assert.True(session.Start(clocked));
            kb.Calibration = s => s == 62 ? (1000, 3000) : (3000, 1000);
            clocked.Now += 2000;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(clocked, new AnalogKeyInputState(), null));
            Assert.DoesNotContain(clocked.Inner.Writes("out").Skip(12), w => w[1] == 0xD7);
        }

        [Fact]
        public void Apex_Pass_FrameBudgetCoversTheRefresh()
        {
            // steelseries_apex_backend.cpp:162 and 178: the 100 ms runs from
            // before the refresh, so six DA and five D7 exchanges share it.
            var kb = new ApexKeyboard();
            var clocked = ApexTransport(kb);
            clocked.WriteCost = 10;
            var session = Apex(clocked);
            Assert.True(session.Start(clocked));
            var output = new AnalogKeyInputState();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(clocked, output, null)); // 50 ms
            clocked.Now += 2000;
            Assert.Equal(AnalogPollResult.NoAnswer, session.Pass(clocked, output, null)); // 110 ms
        }

        [Fact]
        public void Apex_Pass_AnyBadAnswer_EndsTheSession()
        {
            // steelseries_apex_backend.cpp:110 and 172-178: exact 65 bytes with
            // report ID 0, valid banks, an answer in time.
            var kb = new ApexKeyboard();
            var io = ApexTransport(kb);
            var session = Apex(io);
            Assert.True(session.Start(io));
            var output = new AnalogKeyInputState();

            kb.Tamper = a => { a[0] = 1; return a; };
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
            kb.Tamper = a => { a[60] = 1; return a; };
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
            kb.Tamper = a => a.Take(64).ToArray();
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
            kb.Tamper = a => a;
            kb.Filtered[3] = 4096;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
            kb.Filtered[3] = 1000;

            // One request, one answer: the bank that failed is not retried.
            int before = io.Log.Count;
            io.OnSend = _ => Array.Empty<byte[]>();
            Assert.Equal(AnalogPollResult.NoAnswer, session.Pass(io, output, null));
            Assert.Equal(1, io.Log.Count - before);

            io.OnSend = r => new[] { kb.Answer(r) };
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            io.Gone = true;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
        }

        [Fact]
        public void Apex_Stop_SendsNothing()
        {
            // steelseries_apex_backend.cpp:190-191 and spec 6.2: nothing at shutdown.
            var kb = new ApexKeyboard();
            var io = ApexTransport(kb);
            var session = Apex(io);
            Assert.True(session.Start(io));
            int before = io.Log.Count;
            session.Stop(io);
            Assert.Equal(before, io.Log.Count);
        }

        // ── MCHOSE Mix 87 III ──

        /// <summary>
        /// A Mix 87 III on stock 1.22, modeled on HallJoy's own fake
        /// (tests/mchose_mix87_session_test.cpp:15-55): one profile in slot 0,
        /// 256 settings bytes of 0 with byte 8 = 55, the rest of the settings
        /// page erased, and a key descriptor region of 78 plain keys (codes 4
        /// to 81), 8 modifiers and 6 empty triplets. Every other flash byte
        /// carries a pattern, and the digests the session admits are that
        /// image's, since the vendor's bytes are in no repository.
        /// </summary>
        private sealed class Mix87Keyboard
        {
            private const int Settings = 0x2C000, Base = 0x2A000;
            public readonly byte[] Flash = new byte[0x80000];
            public byte VersionLow = 0x22;
            public readonly List<byte> FlagWrites = new();
            public Func<byte[], bool> IgnoreWrite = _ => false;
            public Func<byte[], byte> WriteAnswerMarker = _ => 0xAA;
            public Func<byte[], byte[]> Before = _ => null;
            public Func<byte[], byte[]> Tamper = p => p;

            public Mix87Keyboard(bool flagOn)
            {
                for (int i = 0; i < Flash.Length; i++) Flash[i] = (byte)(i * 31 + 7);
                Array.Clear(Flash, Base, 8);
                Flash[Base + 1] = 1;
                Array.Clear(Flash, Settings, 256);
                for (int i = 256; i < 8192; i++) Flash[Settings + i] = 0xFF;
                Flash[Settings + 7] = flagOn ? (byte)0x08 : (byte)0x00;
                Flash[Settings + 8] = 0x55;
                for (int key = 0; key < 92; key++)
                {
                    int at = 0x15DA6 + key * 3;
                    Flash[at] = Flash[at + 1] = Flash[at + 2] = 0;
                    if (key < 78)
                    {
                        Flash[at] = 0x10;
                        Flash[at + 2] = (byte)(key + 4);
                    }
                    else if (key < 86)
                    {
                        Flash[at] = 0x10;
                        Flash[at + 1] = (byte)(1 << (key - 78));
                    }
                }
            }

            public bool FlagOn => (Flash[Settings + 7] & 0x08) != 0;

            public IReadOnlyList<MchoseMix87Protocol.Fingerprint> Fingerprints()
                => MchoseMix87Protocol.Fingerprints
                    .Select(f => new MchoseMix87Protocol.Fingerprint(f.Address, f.Size,
                        Convert.ToHexStringLower(SHA256.HashData(Flash.AsSpan((int)f.Address, f.Size)))))
                    .ToArray();

            public byte[][] Answer(byte[] wire)
            {
                Assert.Equal(65, wire.Length);
                Assert.Equal(0x00, wire[0]);
                var request = new byte[64];
                Array.Copy(wire, 1, request, 0, 64);
                Assert.Equal(0x55, request[0]);
                Assert.Equal(MchoseMix87Protocol.Checksum(request), request[3]);
                var reply = (byte[])request.Clone();
                reply[0] = 0xAA;
                switch (request[1])
                {
                    case 0x03:
                        reply[8] = VersionLow;
                        reply[9] = 0x01;
                        break;
                    case 0xE0:
                        Array.Copy(Flash, request[5] | request[6] << 8 | request[7] << 16, reply, 8, request[4]);
                        break;
                    case 0x06:
                        Assert.Equal(1, request[4]);
                        FlagWrites.Add(request[8]);
                        if (IgnoreWrite(request)) return Array.Empty<byte[]>();
                        Flash[Settings + request[5]] = request[8];
                        reply[0] = WriteAnswerMarker(request);
                        break;
                    default:
                        throw new InvalidOperationException("an opcode HallJoy never sends");
                }
                reply[3] = MchoseMix87Protocol.Checksum(reply);
                reply = Tamper(reply);
                var answer = new byte[65];
                reply.CopyTo(answer, 1);
                var first = Before(request);
                return first == null ? new[] { answer } : new[] { first, answer };
            }
        }

        private static ClockedTransport Mix87Transport(Mix87Keyboard kb)
            => new(new AnalogKeyboardTestTransport { OnSend = kb.Answer });

        /// <summary>A session that admits <paramref name="kb"/>'s image, or
        /// the digests given, on <paramref name="io"/>'s clock.</summary>
        private static MchoseMix87Session Mix87(Mix87Keyboard kb, ClockedTransport io,
            IReadOnlyList<MchoseMix87Protocol.Fingerprint> digests = null)
            => new(digests ?? kb.Fingerprints()) { Clock = () => io.Now };

        /// <summary>An A0 event as ReadFile returns it: report ID 0, then the
        /// packet (mchose_mix87_protocol.h:50-57).</summary>
        private static byte[] Mix87Event(int code, int depth, int maximum = 341, byte type = 0x10, byte modifier = 0)
        {
            var wire = new byte[65];
            wire[1] = 0xA0;
            wire[2] = type;
            wire[3] = modifier;
            wire[4] = (byte)code;
            wire[7] = (byte)(depth >> 8);
            wire[8] = (byte)depth;
            wire[15] = (byte)(maximum >> 8);
            wire[16] = (byte)maximum;
            return wire;
        }

        private static int FlagWriteCount(ClockedTransport io) => io.Writes("out").Count(w => w[2] == 0x06);

        [Fact]
        public void Mix87_Matches_ExactShape_AndOnlyOneSuchCollection()
        {
            // mchose_mix87_backend.cpp:67-73: 3837:300D, 0001:0000, 65/65.
            // Lines 250-253: no session with two such collections.
            var info = Info(0x3837, 0x300D, 0x0001, 0x0000, 65, 65);
            Assert.True(MchoseMix87Protocol.Matches(info));
            Assert.False(MchoseMix87Protocol.Matches(Info(0x3837, 0x300D, 0x0001, 0x0006, 65, 65)));
            Assert.False(MchoseMix87Protocol.Matches(Info(0x3837, 0x300D, 0x0001, 0x0000, 64, 64)));
            Assert.False(MchoseMix87Protocol.Matches(Info(0x3837, 0x300D, 0xFF00, 0x0000, 65, 65)));
            Assert.False(MchoseMix87Protocol.Matches(Info(0x3837, 0x300C, 0x0001, 0x0000, 65, 65)));
            // Mix87 I is a different device (MCHOSE_MIX87_LOG35_2026-09-27.md:154-157).
            Assert.False(MchoseMix87Protocol.Matches(Info(0x41E4, 0x2122, 0x0001, 0x0000, 65, 65)));

            info.Siblings = new[] { Info(0x3837, 0x300D, 0x0001, 0x0006, 9, 2) };
            Assert.True(MchoseMix87Protocol.Matches(info));
            info.Siblings = new[] { Info(0x3837, 0x300D, 0x0001, 0x0000, 65, 65) };
            Assert.False(MchoseMix87Protocol.Matches(info));
            Assert.False(MchoseMix87Protocol.Matches(null));
        }

        [Fact]
        public void Mix87_Packets_AreHallJoysCompiledFrames()
        {
            // Spec 7.2 [compiled] and mchose_mix87_protocol_test.cpp:7.
            Assert.Equal(Padded(64, "55 03 00 1F 1F 00 00 00"), MchoseMix87Protocol.GetInfo());
            Assert.Equal(Padded(64, "55 E0 00 AA 08 00 A0 02"), MchoseMix87Protocol.Read(0x2A000, 8));
            Assert.Equal(Padded(64, "55 E0 00 FA 38 00 C0 02"), MchoseMix87Protocol.Read(0x2C000, 56));
            Assert.Equal(Padded(64, "55 E0 00 3C 38 A6 5D 01"), MchoseMix87Protocol.Read(0x15DA6, 56));
            Assert.Equal(Padded(64, "55 06 00 10 01 07 00 00 08"), MchoseMix87Protocol.FlagWrite(0, 0x00, true));
            // Profile 3 clearing the flag of 0x7D: offset C7, value 75, sum 13D.
            Assert.Equal(Padded(64, "55 06 00 3D 01 C7 00 00 75"), MchoseMix87Protocol.FlagWrite(3, 0x7D, false));
            Assert.Equal(new byte[64], MchoseMix87Protocol.FlagWrite(4, 0, true));
            var read = MchoseMix87Protocol.Read(0x2C007, 1);
            Assert.Equal(new byte[] { 0x55, 0xE0, 0x00, 0xCA, 0x01, 0x07, 0xC0, 0x02 }, read.Take(8).ToArray());
            // mchose_mix87_protocol_test.cpp:12: out of range reads are empty.
            Assert.Equal(0, MchoseMix87Protocol.Read(0x80000, 1)[0]);
            Assert.Equal(0, MchoseMix87Protocol.Read(0, 57)[0]);
            Assert.Equal(0, MchoseMix87Protocol.Read(0x7FFFF, 2)[0]);
            Assert.Equal(0x55, MchoseMix87Protocol.Read(0x7FFFF, 1)[0]);
        }

        [Fact]
        public void Mix87_Reply_HallJoysVectors()
        {
            // mchose_mix87_protocol_test.cpp:7-11.
            var read = MchoseMix87Protocol.Read(0x2C007, 1);
            var reply = (byte[])read.Clone();
            reply[0] = 0xAA;
            reply[8] = 0xAF;
            reply[3] = MchoseMix87Protocol.Checksum(reply);
            Assert.True(MchoseMix87Protocol.Reply(read, reply));
            reply[6] ^= 1;
            reply[3] = MchoseMix87Protocol.Checksum(reply);
            Assert.False(MchoseMix87Protocol.Reply(read, reply));
            reply = (byte[])read.Clone();
            reply[0] = 0xAA;
            reply[3] ^= 1;
            Assert.False(MchoseMix87Protocol.Reply(read, reply));
            reply = (byte[])read.Clone();
            reply[0] = 0xAA;
            reply[4] = 255;
            Assert.False(MchoseMix87Protocol.Reply(read, reply));
            reply = (byte[])read.Clone();
            reply[0] = 0xAB;
            Assert.False(MchoseMix87Protocol.Reply(read, reply));
        }

        [Fact]
        public void Mix87_Hid_HallJoysVectors()
        {
            // mchose_mix87_protocol_test.cpp:19 and spec 7.4 [compiled].
            Assert.Equal(0xE1, MchoseMix87Protocol.Hid(0x10, 0x02, 0x00));
            Assert.Equal(0xE7, MchoseMix87Protocol.Hid(0x10, 0x80, 0x00));
            Assert.Equal(0xE0, MchoseMix87Protocol.Hid(0x10, 0x01, 0x00));
            Assert.Equal(0, MchoseMix87Protocol.Hid(0x10, 0x00, 0x74));
            Assert.Equal(0, MchoseMix87Protocol.Hid(0x10, 0x03, 0x00));
            Assert.Equal(0, MchoseMix87Protocol.Hid(0xF0, 0xFF, 0x01));
            Assert.Equal(0, MchoseMix87Protocol.Hid(0x10, 0x00, 0x03));
            Assert.Equal(0x04, MchoseMix87Protocol.Hid(0x10, 0x00, 0x04));
            Assert.Equal(0x73, MchoseMix87Protocol.Hid(0x10, 0x00, 0x73));
            Assert.Equal(0, MchoseMix87Protocol.Hid(0x10, 0x02, 0x04));
            Assert.Equal(0, MchoseMix87Protocol.Hid(0x20, 0x00, 0x1A));
        }

        [Fact]
        public void Mix87_Decode_HallJoysVectors()
        {
            // mchose_mix87_protocol_test.cpp:13-18: depth 341, 10 and 0 of
            // maximum 341 give 1000, 29 and 0. Any other maximum is rejected.
            var allowed = new bool[256];
            allowed[26] = true;
            var a = new byte[64];
            a[0] = 0xA0;
            a[1] = 0x10;
            a[3] = 26;
            a[14] = 0x01;
            a[15] = 0x55;
            a[6] = 0x01;
            a[7] = 0x55;
            Assert.True(MchoseMix87Protocol.Decode(a, allowed, out int hid, out int milli));
            Assert.Equal(26, hid);
            Assert.Equal(1000, milli);
            a[6] = 0;
            a[7] = 10;
            Assert.True(MchoseMix87Protocol.Decode(a, allowed, out _, out milli));
            Assert.Equal(29, milli);
            a[7] = 0;
            Assert.True(MchoseMix87Protocol.Decode(a, allowed, out _, out milli));
            Assert.Equal(0, milli);
            a[15] = 0;
            Assert.False(MchoseMix87Protocol.Decode(a, allowed, out _, out _));
            // mchose_mix87_protocol.h:55: a depth past the maximum, or a key
            // outside the allowed set.
            a[15] = 0x55;
            a[6] = 0x01;
            a[7] = 0x56;
            Assert.False(MchoseMix87Protocol.Decode(a, allowed, out _, out _));
            a[7] = 0x00;
            a[3] = 27;
            Assert.False(MchoseMix87Protocol.Decode(a, allowed, out _, out _));
        }

        [Fact]
        public void Mix87_ChangeFlag_PreservesEverySettingAcross2048Transactions()
        {
            // mchose_mix87_protocol_test.cpp:20-33: every profile, every prior
            // byte, both directions. One write when the flag changes, none when
            // it already is as asked, and only bit 3 of one byte ever moves.
            for (int profile = 0; profile < 4; profile++)
                for (int value = 0; value < 256; value++)
                    foreach (bool enable in new[] { false, true })
                    {
                        var config = Enumerable.Repeat((byte)0xFF, 8192).ToArray();
                        for (int i = 0; i < 256; i++) config[i] = (byte)(i * 17 + 5);
                        int offset = profile * 64 + 7;
                        config[offset] = (byte)value;
                        var original = (byte[])config.Clone();
                        var block = new byte[] { 0, 1, (byte)profile, 0, 0, 0, 0, 0 };
                        int writes = 0;
                        var result = MchoseMix87Protocol.ChangeFlag(block, enable,
                            (address, target) =>
                            {
                                if (address == MchoseMix87Protocol.BaseAddress) block.CopyTo(target, 0);
                                else
                                {
                                    Assert.Equal(MchoseMix87Protocol.SettingsAddress, address);
                                    Array.Copy(config, target, target.Length);
                                }
                                return true;
                            },
                            (q, r) =>
                            {
                                writes++;
                                Assert.Equal(0x06, q[1]);
                                Assert.Equal(1, q[4]);
                                Assert.Equal(offset, q[5]);
                                Assert.Equal(MchoseMix87Protocol.Checksum(q), q[3]);
                                config[q[5]] = q[8];
                                q.CopyTo(r, 0);
                                r[0] = 0xAA;
                                return MchoseMix87Protocol.Reply(q, r);
                            });
                        bool same = ((value & 8) != 0) == enable;
                        Assert.Equal(same ? MchoseMix87Protocol.ChangeResult.Unchanged : MchoseMix87Protocol.ChangeResult.Verified, result);
                        Assert.Equal(same ? 0 : 1, writes);
                        for (int i = 0; i < 256; i++)
                            if (i != offset) Assert.Equal(original[i], config[i]);
                        Assert.Equal(enable ? value | 8 : value & ~8, config[offset]);
                    }
        }

        [Fact]
        public void Mix87_ChangeFlag_FailureScenarios()
        {
            // mchose_mix87_protocol_test.cpp:34-47: a failed first read, a
            // stale profile, a lost acknowledgement, an ignored write and a
            // failed read-back. Only a clean round trip is Verified.
            for (int scenario = 0; scenario < 5; scenario++)
            {
                var consent = new byte[] { 0, 1, 2, 0, 0, 0, 0, 0 };
                var settings = Enumerable.Repeat((byte)0xFF, 8192).ToArray();
                settings[135] = 0;
                int writes = 0;
                var result = MchoseMix87Protocol.ChangeFlag(consent, true,
                    (address, target) =>
                    {
                        if (scenario == 0) return false;
                        if (address == MchoseMix87Protocol.BaseAddress)
                        {
                            var block = (byte[])consent.Clone();
                            if (scenario == 1) block[2] = 1;
                            block.CopyTo(target, 0);
                        }
                        else Array.Copy(settings, target, target.Length);
                        return !(scenario == 4 && writes > 0);
                    },
                    (q, r) =>
                    {
                        writes++;
                        if (scenario != 3) settings[q[5]] = q[8];
                        return scenario != 2;
                    });
                var expected = scenario == 0 ? MchoseMix87Protocol.ChangeResult.ReadFailed
                    : scenario == 1 ? MchoseMix87Protocol.ChangeResult.StaleProfile
                    : MchoseMix87Protocol.ChangeResult.Uncertain;
                Assert.Equal(expected, result);
                Assert.Equal(scenario < 2 ? 0 : 1, writes);
            }
        }

        [Fact]
        public void Mix87_ChangeFlag_ReservedDataBlocksTheWrite_FactoryPaddingDoesNot()
        {
            // mchose_mix87_protocol_test.cpp:48-62 and mchose_mix87_protocol.h:67-73:
            // the firmware erases the whole 8 KiB page, so unknown data past the
            // settings blocks the write. Factory zero padding erased to FF by
            // the write verifies.
            var block = new byte[] { 0, 1, 0, 0, 0, 0, 0, 0 };
            var page = Enumerable.Repeat((byte)0xFF, 8192).ToArray();
            page[7] = 0;
            page[4096] = 0x42;
            int writes = 0;
            Assert.Equal(MchoseMix87Protocol.ChangeResult.ReservedData, MchoseMix87Protocol.ChangeFlag(block, true,
                (address, target) =>
                {
                    if (address == MchoseMix87Protocol.BaseAddress) block.CopyTo(target, 0);
                    else Array.Copy(page, target, target.Length);
                    return true;
                },
                (q, r) => { writes++; return true; }));
            Assert.Equal(0, writes);

            var zero = new byte[8192];
            Assert.Equal(MchoseMix87Protocol.ChangeResult.Verified, MchoseMix87Protocol.ChangeFlag(block, true,
                (address, target) =>
                {
                    if (address == MchoseMix87Protocol.BaseAddress) block.CopyTo(target, 0);
                    else Array.Copy(zero, target, target.Length);
                    return true;
                },
                (q, r) =>
                {
                    zero[q[5]] = q[8];
                    for (int i = 256; i < 8192; i++) zero[i] = 0xFF;
                    return true;
                }));
        }

        [Fact]
        public void Mix87_Fingerprints_AreHallJoysSevenRegions()
        {
            // mchose_mix87_backend.cpp:128-136, in HallJoy's order.
            var f = MchoseMix87Protocol.Fingerprints;
            Assert.Equal(7, f.Count);
            Assert.Equal(new uint[] { 0x131F0, 0x0E868, 0x0F406, 0x08754, 0x08F08, 0x13B85, 0x15DA6 },
                f.Select(x => x.Address).ToArray());
            Assert.Equal(new[] { 1952, 320, 464, 96, 256, 93, 276 }, f.Select(x => x.Size).ToArray());
            Assert.Equal("afcfb5972f2d2d51eb5930c7c143a588dc0d0be6de8645878bdf9ae4daeaaf9e", f[0].Sha256);
            Assert.Equal("4a6bcfa47be09b1ae0342008d3c44a6ea2727fa8dbbef94bc9fe36c81cc36533", f[6].Sha256);
            Assert.True(MchoseMix87Protocol.MatchesDigest(Encoding.ASCII.GetBytes("abc"),
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
            Assert.False(MchoseMix87Protocol.MatchesDigest(Encoding.ASCII.GetBytes("abd"),
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
        }

        [Fact]
        public void Mix87_KeySet_NeedsEightySixDistinctKeys()
        {
            // mchose_mix87_backend.cpp:154-161: a key twice, or a count other
            // than 86, fails admission.
            var kb = new Mix87Keyboard(flagOn: false);
            var region = kb.Flash.AsSpan(0x15DA6, 276).ToArray();
            var allowed = new bool[256];
            var table = new int[92];
            Assert.True(MchoseMix87Protocol.DecodeKeySet(region, allowed, table));
            Assert.Equal(86, allowed.Count(x => x));
            Assert.Equal(0x04, table[0]);
            Assert.Equal(0xE7, table[85]);
            Assert.Equal(0, table[86]);

            region[3 * 5 + 2] = 4; // key 5 repeats key 0
            Assert.False(MchoseMix87Protocol.DecodeKeySet(region, new bool[256], new int[92]));

            MchoseMix87Session.ResetGeneration();
            kb.Flash[0x15DA6 + 3 * 90] = 0x10;
            kb.Flash[0x15DA6 + 3 * 90 + 2] = 0x60; // an 87th key
            var io = Mix87Transport(kb);
            Assert.False(Mix87(kb, io).Start(io));
            Assert.Equal(0, FlagWriteCount(io));
        }

        [Fact]
        public void Mix87_FlagOff_StartEnables_EventsRead_StopDisables()
        {
            // mchose_mix87_backend.cpp:194-213, 216-245 and 177-187, the
            // session test's normal run (tests/mchose_mix87_session_test.cpp:
            // 75-84 and 96-100): one write sets the flag, events update their
            // own keys, the stop's write clears it, and settings byte 8
            // survives both.
            MchoseMix87Session.ResetGeneration();
            var kb = new Mix87Keyboard(flagOn: false);
            var io = Mix87Transport(kb);
            var session = Mix87(kb, io);
            Assert.True(session.Start(io));
            Assert.Equal(new byte[] { 0x08 }, kb.FlagWrites.ToArray());
            Assert.True(kb.FlagOn);
            Assert.True(session.DisableOwed);
            Assert.Equal(MchoseMix87Protocol.ChangeResult.Verified, session.EnableResult);
            Assert.Equal(0, session.Profile);
            Assert.Equal("MCHOSE Mix87 III", session.ModelName);
            Assert.Equal(86, session.KeyOrder.Length);
            Assert.Equal(Enumerable.Range(4, 78).Concat(Enumerable.Range(0xE0, 8)).ToArray(), session.KeyOrder);

            // Reads only until the write: 03, the seven regions in 64 reads of
            // up to 56 bytes, the mode (block, settings, block: 7), then the
            // transaction's block, 8 KiB page (147) and block again.
            var writes = io.Writes("out");
            Assert.Equal(Padded(65, "00 55 03 00 1F 1F 00 00 00"), writes[0]);
            Assert.Equal(1 + 64 + 7 + 149, writes.TakeWhile(w => w[2] != 0x06).Count());
            Assert.All(writes.Skip(1).TakeWhile(w => w[2] != 0x06), w => Assert.Equal(0xE0, w[2]));

            var output = new AnalogKeyInputState();
            io.QueueInput(Mix87Event(26, 341), Mix87Event(4, 10), Mix87Event(0, 170, modifier: 0x02));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.029f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.499f, output.Get(AnalogKeyCodes.LShift)); // (170000 + 170) / 341 = 499
            // Silence is not a release (mchose_mix87_backend.cpp:232).
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
            io.QueueInput(Mix87Event(26, 0));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(2, output.Count);

            session.Stop(io);
            Assert.Equal(new byte[] { 0x08, 0x00 }, kb.FlagWrites.ToArray());
            Assert.False(kb.FlagOn);
            Assert.False(session.DisableOwed);
            Assert.Equal(MchoseMix87Protocol.ChangeResult.Verified, session.CleanupResult);
            Assert.Equal(0x55, kb.Flash[0x2C008]);
            Assert.Equal(Padded(65, "00 55 06 00 08 01 07 00 00 00"), io.Writes("out").Last(w => w[2] == 0x06));

            session.Stop(io); // a second stop owes nothing
            Assert.Equal(2, kb.FlagWrites.Count);
        }

        [Fact]
        public void Mix87_PreEnabledFlag_NoEnableWrite_StillDisabledAtStop()
        {
            // Session test scenario 0 (tests/mchose_mix87_session_test.cpp:94
            // and 98) and MCHOSE_MIX87_LOG35_2026-09-27.md:13-14: a flag found on
            // is used as it is and cleared at the end.
            MchoseMix87Session.ResetGeneration();
            var kb = new Mix87Keyboard(flagOn: true);
            var io = Mix87Transport(kb);
            var session = Mix87(kb, io);
            Assert.True(session.Start(io));
            Assert.Empty(kb.FlagWrites);
            Assert.Null(session.EnableResult);
            Assert.True(session.DisableOwed);
            session.Stop(io);
            Assert.Equal(new byte[] { 0x00 }, kb.FlagWrites.ToArray());
            Assert.False(kb.FlagOn);
        }

        [Fact]
        public void Mix87_OneAutomaticEnablePerGeneration()
        {
            // mchose_mix87_backend.cpp:44-46, 202 and 210, session test
            // scenario 1's recovery (tests/mchose_mix87_session_test.cpp:104-108):
            // after the first session, a keyboard found off is refused without a
            // write. One still on is used.
            MchoseMix87Session.ResetGeneration();
            var kb = new Mix87Keyboard(flagOn: false);
            var io = Mix87Transport(kb);
            var first = Mix87(kb, io);
            Assert.True(first.Start(io));
            first.Stop(io);
            Assert.Equal(2, kb.FlagWrites.Count);

            var again = Mix87Transport(kb);
            Assert.False(Mix87(kb, again).Start(again));
            Assert.Equal(0, FlagWriteCount(again));
            Assert.False(kb.FlagOn);

            kb.Flash[0x2C007] = 0x08; // left on, as after a lost cleanup
            var reuse = Mix87Transport(kb);
            var session = Mix87(kb, reuse);
            Assert.True(session.Start(reuse));
            Assert.Equal(0, FlagWriteCount(reuse));
            session.Stop(reuse);
            Assert.False(kb.FlagOn);
        }

        [Fact]
        public void Mix87_UnreviewedFirmware_NoFlashReads_NoWrites()
        {
            // Session test scenario 2 (tests/mchose_mix87_session_test.cpp:38,
            // 97 and 101) and mchose_mix87_backend.cpp:149: only 1.22 is admitted.
            MchoseMix87Session.ResetGeneration();
            var kb = new Mix87Keyboard(flagOn: false) { VersionLow = 0x23 };
            var io = Mix87Transport(kb);
            Assert.False(Mix87(kb, io).Start(io));
            Assert.Single(io.Log);
            Assert.Empty(kb.FlagWrites);
        }

        [Fact]
        public void Mix87_FingerprintMismatch_StopsAtTheFirstRegion()
        {
            // Session test scenario 3 (tests/mchose_mix87_session_test.cpp:73,
            // 97 and 102) and mchose_mix87_backend.cpp:152-153.
            MchoseMix87Session.ResetGeneration();
            var kb = new Mix87Keyboard(flagOn: false);
            var digests = kb.Fingerprints();
            kb.Flash[0x131F0 + 100] ^= 1;
            var io = Mix87Transport(kb);
            Assert.False(Mix87(kb, io, digests).Start(io));
            Assert.Equal(1 + 35, io.Log.Count); // 03, then 1952 bytes in 56-byte reads
            Assert.Empty(kb.FlagWrites);

            // HallJoy's own digests reject a keyboard that is not stock 1.22.
            MchoseMix87Session.ResetGeneration();
            var stock = Mix87Transport(kb);
            Assert.False(new MchoseMix87Session { Clock = () => stock.Now }.Start(stock));
            Assert.Equal(1 + 35, stock.Log.Count);
        }

        [Fact]
        public void Mix87_AmbiguousEnable_IsFollowedByADisable()
        {
            // Session test scenario 4 (tests/mchose_mix87_session_test.cpp:53
            // and 100): the write lands but its answer is AB. The enable is
            // Uncertain, the handshake fails, and the disable still runs.
            MchoseMix87Session.ResetGeneration();
            var kb = new Mix87Keyboard(flagOn: false)
            {
                WriteAnswerMarker = request => (request[8] & 0x08) != 0 ? (byte)0xAB : (byte)0xAA,
            };
            var io = Mix87Transport(kb);
            var session = Mix87(kb, io);
            Assert.False(session.Start(io));
            Assert.Equal(MchoseMix87Protocol.ChangeResult.Uncertain, session.EnableResult);
            Assert.Equal(MchoseMix87Protocol.ChangeResult.Verified, session.CleanupResult);
            Assert.Equal(new byte[] { 0x08, 0x00 }, kb.FlagWrites.ToArray());
            Assert.False(kb.FlagOn);
            Assert.Equal(0x55, kb.Flash[0x2C008]);
        }

        [Fact]
        public void Mix87_ReservedSettingsData_ArmsButNeverWrites()
        {
            // mchose_mix87_backend.cpp:203 arms the lease before the attempt,
            // mchose_mix87_protocol.h:71-73 refuses both transactions.
            MchoseMix87Session.ResetGeneration();
            var kb = new Mix87Keyboard(flagOn: false);
            kb.Flash[0x2C000 + 4096] = 0x42;
            var io = Mix87Transport(kb);
            var session = Mix87(kb, io);
            Assert.False(session.Start(io));
            Assert.Equal(MchoseMix87Protocol.ChangeResult.ReservedData, session.EnableResult);
            Assert.Equal(MchoseMix87Protocol.ChangeResult.ReservedData, session.CleanupResult);
            Assert.Empty(kb.FlagWrites);
        }

        [Fact]
        public void Mix87_FailedCleanupWrite_IsReportedAndNotRetried()
        {
            // Session test scenario 8 (tests/mchose_mix87_session_test.cpp:27
            // and 100): the disable's write fails. The flag stays on, the
            // result is Uncertain, and there is no second attempt.
            MchoseMix87Session.ResetGeneration();
            var kb = new Mix87Keyboard(flagOn: false);
            int disables = 0;
            var clocked = Mix87Transport(kb);
            clocked.FailWrite = w => w[2] == 0x06 && (w[9] & 0x08) == 0 && ++disables > 0;
            var session = Mix87(kb, clocked);
            Assert.True(session.Start(clocked));
            session.Stop(clocked);
            Assert.Equal(1, disables);
            Assert.Equal(MchoseMix87Protocol.ChangeResult.Uncertain, session.CleanupResult);
            Assert.True(kb.FlagOn);
        }

        [Fact]
        public void Mix87_ProfileChangedBeforeStop_NoDisableWrite()
        {
            // Session test scenario 9 (tests/mchose_mix87_session_test.cpp:79
            // and 99): the active profile moved, so the cleanup refuses to write
            // into another profile's settings.
            MchoseMix87Session.ResetGeneration();
            var kb = new Mix87Keyboard(flagOn: false);
            var io = Mix87Transport(kb);
            var session = Mix87(kb, io);
            Assert.True(session.Start(io));
            kb.Flash[0x2A002] = 1;
            session.Stop(io);
            Assert.Equal(MchoseMix87Protocol.ChangeResult.StaleProfile, session.CleanupResult);
            Assert.Equal(new byte[] { 0x08 }, kb.FlagWrites.ToArray());
            Assert.True(kb.FlagOn);
        }

        [Fact]
        public void Mix87_UnansweredEnable_TimesOutAndIsCleanedUp()
        {
            // mchose_mix87_backend.cpp:102-113: 800 ms for the answer. The
            // keyboard ignores the enabling write, so the enable is Uncertain,
            // and the cleanup finds the flag off and writes nothing.
            MchoseMix87Session.ResetGeneration();
            var kb = new Mix87Keyboard(flagOn: false) { IgnoreWrite = request => (request[8] & 0x08) != 0 };
            var clocked = Mix87Transport(kb);
            var session = Mix87(kb, clocked);
            long before = clocked.Now;
            Assert.False(session.Start(clocked));
            Assert.InRange(clocked.Now - before, 800, 900);
            Assert.Equal(MchoseMix87Protocol.ChangeResult.Uncertain, session.EnableResult);
            Assert.Equal(MchoseMix87Protocol.ChangeResult.Unchanged, session.CleanupResult);
            Assert.Equal(new byte[] { 0x08 }, kb.FlagWrites.ToArray());
            Assert.False(kb.FlagOn);
        }

        [Fact]
        public void Mix87_Exchange_SkipsQueuedEvents_FailsOnForeignAnswers()
        {
            // mchose_mix87_backend.cpp:108-111: an A0 queued before the answer
            // is skipped, an AA or AB that does not answer the request fails.
            MchoseMix87Session.ResetGeneration();
            var kb = new Mix87Keyboard(flagOn: true) { Before = _ => Mix87Event(26, 100) };
            var io = Mix87Transport(kb);
            var session = Mix87(kb, io);
            Assert.True(session.Start(io));
            session.Stop(io);

            MchoseMix87Session.ResetGeneration();
            var foreign = new Mix87Keyboard(flagOn: true) { Tamper = p => { p[5] ^= 1; p[3] = MchoseMix87Protocol.Checksum(p); return p; } };
            var refused = Mix87Transport(foreign);
            Assert.False(Mix87(foreign, refused).Start(refused));
            Assert.Single(refused.Log);

            MchoseMix87Session.ResetGeneration();
            var numbered = new Mix87Keyboard(flagOn: true) { Before = _ => { var w = Mix87Event(26, 100); w[0] = 2; return w; } };
            var bad = Mix87Transport(numbered);
            Assert.False(Mix87(numbered, bad).Start(bad));
            Assert.Single(bad.Log);
        }

        [Fact]
        public void Mix87_Pass_EndsOnConfiguratorTrafficAndInvalidKeyEvents()
        {
            // mchose_mix87_backend.cpp:231-244 and session test scenario 6
            // (tests/mchose_mix87_session_test.cpp:78): configurator packets, a
            // key event that fails validation (a key outside the 86 read from
            // the keyboard included), a numbered or short report, or a gone
            // device end the session. Other packets are ignored.
            MchoseMix87Session.ResetGeneration();
            var kb = new Mix87Keyboard(flagOn: true);
            var io = Mix87Transport(kb);
            var session = Mix87(kb, io);
            Assert.True(session.Start(io));
            var output = new AnalogKeyInputState();

            foreach (byte marker in new byte[] { 0xA2, 0xA3, 0xAA, 0xAB })
            {
                var packet = new byte[65];
                packet[1] = marker;
                io.QueueInput(packet);
                Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
            }

            io.QueueInput(Mix87Event(26, 0xFF00, maximum: 0));
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
            io.QueueInput(Mix87Event(0x60, 100)); // a valid usage the keyboard did not list
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
            io.QueueInput(Mix87Event(0x74, 100)); // not a key code
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
            io.QueueInput(Mix87Event(26, 342));
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
            io.QueueInput(Mix87Event(0, 100, modifier: 0x03));
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));

            io.QueueInput(Mix87Event(26, 100, type: 0x20));
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            var other = new byte[65];
            other[1] = 0x5A;
            io.QueueInput(other);
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null)); // nothing arrived
            Assert.Equal(0, output.Count);

            var numbered = Mix87Event(26, 100);
            numbered[0] = 1;
            io.QueueInput(numbered);
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
            io.QueueInput(Mix87Event(26, 100).Take(64).ToArray());
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
            io.Gone = true;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
        }

        [Fact]
        public void Mix87_OneSessionAtATime()
        {
            // mchose_mix87_backend.cpp:250-253: HallJoy serves one Mix87. A
            // second keyboard waits, unspoken to, until the first session ends.
            MchoseMix87Session.ResetGeneration();
            var kbA = new Mix87Keyboard(flagOn: true);
            var ioA = Mix87Transport(kbA);
            var a = Mix87(kbA, ioA);
            Assert.True(a.Start(ioA));

            // HallJoy runs no session beside another (mchose_mix87_backend.cpp:250-252),
            // and the default refuses the second at once rather than holding
            // the sweep that opens it.
            Assert.Equal(0, MchoseMix87Session.DefaultSlotWaitMs);
            var kbB = new Mix87Keyboard(flagOn: true);
            var ioB = Mix87Transport(kbB);
            var b = Mix87(kbB, ioB);
            Assert.False(b.Start(ioB));
            Assert.Empty(ioB.Log);

            a.Stop(ioA);
            Assert.True(b.Start(ioB));
            b.Stop(ioB);

            // A failed handshake frees the slot too.
            var kbC = new Mix87Keyboard(flagOn: true) { VersionLow = 0x21 };
            var ioC = Mix87Transport(kbC);
            Assert.False(Mix87(kbC, ioC).Start(ioC));
            var ioD = Mix87Transport(kbA);
            kbA.Flash[0x2C007] = 0x08;
            var d = Mix87(kbA, ioD);
            Assert.True(d.Start(ioD));
            d.Stop(ioD);
        }

        [Fact]
        public void Mix87_Handshake_WaitsForTheLastSessionsTeardown()
        {
            // mchose_mix87_backend.cpp:247-255: HallJoy's worker starts the
            // next session only after the last one's cleanup returned. A
            // reopened keyboard's handshake waits for the stopping session.
            MchoseMix87Session.ResetGeneration();
            var kb = new Mix87Keyboard(flagOn: true);
            var ioA = Mix87Transport(kb);
            var a = Mix87(kb, ioA);
            Assert.True(a.Start(ioA));

            var ioB = Mix87Transport(kb);
            var b = Mix87(kb, ioB);
            b.SlotWaitMs = 30000;
            bool started = false;
            var handshake = new System.Threading.Thread(() => started = b.Start(ioB));
            handshake.Start();
            Assert.False(handshake.Join(100));
            Assert.Empty(ioB.Log);

            // The app turns input back on while the old row still stops.
            MchoseMix87Session.BeginGeneration();
            a.Stop(ioA);
            Assert.True(handshake.Join(30000));
            Assert.True(started);
            Assert.Equal(new byte[] { 0x00, 0x08 }, kb.FlagWrites.ToArray());
            b.Stop(ioB);
            Assert.False(kb.FlagOn);
        }

        [Fact]
        public void Mix87_BeginGeneration_AllowsOneMoreEnable()
        {
            // mchose_mix87_backend.cpp:261: a backend start resets the one
            // automatic enable. The app's counterpart is analog input turned
            // back on.
            MchoseMix87Session.ResetGeneration();
            var kb = new Mix87Keyboard(flagOn: false);
            var io = Mix87Transport(kb);
            var first = Mix87(kb, io);
            Assert.True(first.Start(io));
            first.Stop(io);
            var refused = Mix87Transport(kb);
            Assert.False(Mix87(kb, refused).Start(refused));

            MchoseMix87Session.BeginGeneration();
            var again = Mix87Transport(kb);
            var second = Mix87(kb, again);
            Assert.True(second.Start(again));
            second.Stop(again);
            Assert.Equal(new byte[] { 0x08, 0x00, 0x08, 0x00 }, kb.FlagWrites.ToArray());
        }
    }
}
