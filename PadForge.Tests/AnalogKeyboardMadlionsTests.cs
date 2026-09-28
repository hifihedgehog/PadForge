using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PadForge.Engine;
using PadForge.Engine.Common.AnalogKeyboard;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// HallJoy's MADLIONS MAD 68 Pro R, ATK Hex80 and IROK M484 routes
    /// (issue #468), pinned against byte fixtures built from HallJoy's source
    /// at commit 378f9fe8. Citations are relative to
    /// src/HallJoyProject/HallJoy/. No such keyboard is on the bench, so every
    /// answer is scripted. A fake clock advances only when a read times out
    /// or the session sleeps, which keeps the timing exact and the tests fast.
    /// </summary>
    public class AnalogKeyboardMadlionsTests
    {
        // ── Harness ──

        private sealed class Clock
        {
            public long Now = 10_000_000;
        }

        /// <summary>The scripted transport with a clock: a read that finds
        /// nothing queued has waited out its timeout. Writes the predicate
        /// refuses fail without reaching the script.</summary>
        private sealed class ClockedTransport : IAnalogKeyboardTransport
        {
            public readonly AnalogKeyboardTestTransport Inner;
            private readonly Clock _clock;
            public Func<byte[], bool> RefuseSend = _ => false;

            public ClockedTransport(AnalogKeyboardTestTransport inner, Clock clock)
            {
                Inner = inner;
                _clock = clock;
            }

            public bool Send(byte[] report) => !RefuseSend(report) && Inner.Send(report);
            public bool SendOutputReport(byte[] report) => !RefuseSend(report) && Inner.SendOutputReport(report);

            public int Receive(byte[] buffer, int timeoutMs)
            {
                int n = Inner.Receive(buffer, timeoutMs);
                if (n == 0) _clock.Now += Math.Max(1, timeoutMs);
                return n;
            }

            public void DiscardStale() => Inner.DiscardStale();
            public bool SetFeature(byte[] report) => Inner.SetFeature(report);
            public int GetFeature(byte[] buffer) => Inner.GetFeature(buffer);
            public int InputLength => Inner.InputLength;
            public int OutputLength => Inner.OutputLength;
            public int FeatureLength => Inner.FeatureLength;
        }

        private static AnalogKeyInputState Keys() => new();

        private static AnalogKeyboardDeviceInfo Info(ushort vid, ushort pid, ushort page, ushort usage,
            ushort input, ushort output, string product = "", string manufacturer = "", string path = "",
            ushort version = 0)
            => new()
            {
                VendorId = vid,
                ProductId = pid,
                UsagePage = page,
                Usage = usage,
                InputReportLength = input,
                OutputReportLength = output,
                ProductString = product,
                ManufacturerString = manufacturer,
                Path = path,
                VersionNumber = version,
            };

        private static byte[] Head(byte[] data, int count) => data.Take(count).ToArray();

        // ── MADLIONS MAD 68 Pro R ──

        private const int W = 0x1A, A = 0x04, S = 0x16, D = 0x07, Q = 0x14;

        private static byte[] Normal(byte opcode) => new byte[] { 0x55, opcode, 0, 0, 0, 0, 0, 0 };

        /// <summary>A 65-byte AA acknowledgment, report ID 0 first.</summary>
        private static byte[] Ack(byte opcode, byte header = 0xAA)
        {
            var r = new byte[65];
            r[1] = header;
            r[2] = opcode;
            return r;
        }

        /// <summary>A 65-byte A0 report for HallJoy's key index.</summary>
        private static byte[] A0(int keyIndex, int raw, int threshold = 500, int baseline = 0, int state = 0)
        {
            var r = new byte[65];
            int d = Mad68ProRProtocol.DescriptorOfKey(keyIndex);
            r[1] = 0xA0;
            r[2] = (byte)(d >> 16);
            r[3] = (byte)(d >> 8);
            r[4] = (byte)d;
            r[5] = (byte)(raw >> 8);
            r[6] = (byte)raw;
            r[11] = (byte)state;
            r[15] = (byte)(threshold >> 8);
            r[16] = (byte)threshold;
            r[19] = (byte)(baseline >> 8);
            r[20] = (byte)baseline;
            return r;
        }

        private static int Key(int hid) => Mad68ProRProtocol.KeyIndexFromHid(hid);

        /// <summary>One forced sweep: all 68 descriptors in key order.</summary>
        private static IEnumerable<byte[]> Sweep(Func<int, int> rawOfHid)
        {
            for (int k = 0; k < Mad68ProRProtocol.PhysicalKeyCount; k++)
                yield return A0(k, rawOfHid(Mad68ProRProtocol.HidOfKey(k)));
        }

        private static (Mad68ProRSession session, ClockedTransport io, Clock clock) Mad68(
            ushort pid = 0x1109, ushort version = 0x0102)
        {
            var clock = new Clock();
            var io = new ClockedTransport(new AnalogKeyboardTestTransport(), clock);
            var session = new Mad68ProRSession(pid, version, () => clock.Now, ms => clock.Now += ms);
            return (session, io, clock);
        }

        private static void PassUntil(Mad68ProRSession session, ClockedTransport io, AnalogKeyInputState output,
            Func<int, bool> held, Func<bool> done, int limit = 20000)
        {
            for (int i = 0; i < limit && !done(); i++)
                session.Pass(io, output, held);
            Assert.True(done(), "the session did not reach the expected state");
        }

        [Fact]
        public void Mad68_Matches_HallJoysAdmission()
        {
            // mad68pr_backend.cpp:575, 586-590: VID 373B, 65/65, usage
            // 0001:0000 or an MI_01 path. mad68pr_backend.cpp:2266-2278: PID
            // 1109 at any bcdDevice, or strings naming the MAD68 family.
            Assert.True(Mad68ProRProtocol.Matches(Info(0x373B, 0x1109, 0x0001, 0x0000, 65, 65, version: 0x0102)));
            Assert.True(Mad68ProRProtocol.Matches(Info(0x373B, 0x1109, 0x0001, 0x0000, 65, 65, version: 0x0200)));
            Assert.True(Mad68ProRProtocol.Matches(Info(0x373B, 0x1109, 0xFF00, 0x0001, 65, 65,
                path: @"\\?\HID#VID_373B&PID_1109&MI_01#7&abc&0&0000#{4d1e55b2}")));
            Assert.True(Mad68ProRProtocol.Matches(Info(0x373B, 0x1120, 0x0001, 0x0000, 65, 65, product: "MAD68 R")));
            Assert.True(Mad68ProRProtocol.Matches(Info(0x373B, 0x1120, 0x0001, 0x0000, 65, 65, product: "MAD 68 Pro")));
            Assert.True(Mad68ProRProtocol.Matches(Info(0x373B, 0x1120, 0x0001, 0x0000, 65, 65, product: "Mad-68")));
            // The manufacturer and the product are joined by a space before the test.
            Assert.True(Mad68ProRProtocol.Matches(Info(0x373B, 0x1120, 0x0001, 0x0000, 65, 65,
                product: "68 Pro", manufacturer: "MADLIONS MAD")));

            // Near misses.
            Assert.False(Mad68ProRProtocol.Matches(Info(0x3434, 0x1109, 0x0001, 0x0000, 65, 65)));
            Assert.False(Mad68ProRProtocol.Matches(Info(0x373B, 0x1109, 0x0001, 0x0000, 64, 65)));
            Assert.False(Mad68ProRProtocol.Matches(Info(0x373B, 0x1109, 0x0001, 0x0000, 65, 33)));
            Assert.False(Mad68ProRProtocol.Matches(Info(0x373B, 0x1109, 0xFF60, 0x0061, 65, 65,
                path: @"\\?\HID#VID_373B&PID_1109&MI_02#7&abc")));
            Assert.False(Mad68ProRProtocol.Matches(Info(0x373B, 0x1120, 0x0001, 0x0000, 65, 65, product: "MAD60HE")));
        }

        [Fact]
        public void Mad68_SoupMadlionsPids_ReachOnlyWhatHallJoyAdmits()
        {
            // The Soup MAD68HE collection (VIA raw HID, FF60:0061, 32-byte
            // reports) fails the 65-byte shape even though its string names
            // the family (mad68pr_backend.cpp:586-590).
            Assert.Equal(AnalogKeyboardProtocol.Madlions,
                AnalogKeyboardCatalog.Identify(0x373B, 0x1058, 0xFF60, 0x61, _ => false));
            Assert.False(Mad68ProRProtocol.Matches(Info(0x373B, 0x1058, 0xFF60, 0x0061, 33, 33, product: "MAD68HE")));
            Assert.False(Mad68ProRProtocol.Matches(Info(0x373B, 0x10A7, 0xFF60, 0x0061, 33, 33, product: "MAD68R")));
            // A 65-byte interface-1 collection of the same PID whose strings
            // name the family is what HallJoy probes, so it matches here and
            // the A9 probe decides.
            Assert.True(Mad68ProRProtocol.Matches(Info(0x373B, 0x1058, 0x0001, 0x0000, 65, 65, product: "MAD68HE")));
            Assert.False(Mad68ProRProtocol.Matches(Info(0x373B, 0x1053, 0x0001, 0x0000, 65, 65, product: "MAD60HE")));
        }

        [Fact]
        public void Mad68_SiblingStrings_AdmitTheSamePid()
        {
            // HallJoy checks any candidate with the same PID
            // (mad68pr_backend.cpp:2273-2276).
            var sibling = Info(0x373B, 0x1120, 0x0001, 0x0000, 65, 65, product: "MAD68 R");
            var info = Info(0x373B, 0x1120, 0xFF00, 0x0001, 65, 65, path: @"\\?\hid#vid_373b&pid_1120&mi_01#x");
            info.Siblings = new[] { sibling };
            Assert.True(Mad68ProRProtocol.Matches(info));
            sibling.InputReportLength = 33;
            Assert.False(Mad68ProRProtocol.Matches(info));
        }

        [Fact]
        public void Mad68_CommandFrames_ByteForByte()
        {
            // MakeZeroPayloadRequest (mad68pr_protocol.cpp:7-27), the spec's
            // table of every frame HallJoy can send.
            var a9 = Mad68ProRProtocol.MakeZeroPayloadRequest(0xA9);
            Assert.Equal(64, a9.Length);
            Assert.Equal(Normal(0xA9), Head(a9, 8));
            Assert.All(a9.Skip(8), b => Assert.Equal(0, b));
            Assert.Equal(Normal(0xA8), Head(Mad68ProRProtocol.MakeZeroPayloadRequest(0xA8), 8));
            Assert.Equal(new byte[] { 0x55, 0xA9, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A },
                Head(Mad68ProRProtocol.MakeZeroPayloadRequest(0xA9, 0x55, 0x5A), 8));
            Assert.Equal(new byte[] { 0x55, 0xA8, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A },
                Head(Mad68ProRProtocol.MakeZeroPayloadRequest(0xA8, 0x55, 0x5A), 8));
            Assert.Equal(new byte[] { 0x5F, 0xA9, 0, 0, 0, 0, 0, 0 },
                Head(Mad68ProRProtocol.MakeZeroPayloadRequest(0xA9, 0x5F, 0x5A), 8));

            // The caps buffer: max(9, 65) bytes, report ID 0 (mad68pr_backend.cpp:895-902).
            var wire = Mad68ProRProtocol.CapsReport(a9, 65);
            Assert.Equal(65, wire.Length);
            Assert.Equal(0, wire[0]);
            Assert.Equal(a9, wire.Skip(1).ToArray());
            // A shorter report still gets HallJoy's 9-byte minimum.
            var small = Mad68ProRProtocol.CapsReport(a9, 8);
            Assert.Equal(9, small.Length);
            Assert.Equal(Normal(0xA9), small.Skip(1).ToArray());
        }

        [Fact]
        public void Mad68_ControlResponses_ChecksumXorAndRaw()
        {
            // DecodeControlResponse (mad68pr_protocol.cpp:130-192).
            Assert.Equal(Mad68ControlKind.Valid,
                Mad68ProRProtocol.DecodeControlResponse(Ack(0xA9).Skip(1).ToArray(), 0x55, 0xA9).Kind);
            Assert.Equal(Mad68ControlKind.Invalid,
                Mad68ProRProtocol.DecodeControlResponse(Ack(0xA8).Skip(1).ToArray(), 0x55, 0xA9).Kind);
            Assert.Equal(Mad68ControlKind.ChecksumError,
                Mad68ProRProtocol.DecodeControlResponse(Ack(0xA9, 0xAB).Skip(1).ToArray(), 0x55, 0xA9).Kind);

            // Length 2 with payload 01 02: the checksum is 2 + 1 + 2 = 5.
            var p = new byte[64];
            p[0] = 0xAA; p[1] = 0xA9; p[3] = 5; p[4] = 2; p[8] = 1; p[9] = 2;
            Assert.Equal(Mad68ControlKind.Valid, Mad68ProRProtocol.DecodeControlResponse(p, 0x55, 0xA9).Kind);
            p[3] = 6;
            var bad = Mad68ProRProtocol.DecodeControlResponse(p, 0x55, 0xA9);
            Assert.Equal(Mad68ControlKind.Invalid, bad.Kind);
            Assert.Equal(5, bad.ExpectedChecksum);

            // The same reply XORed with key 0x5A over bytes 3 to 7 and the payload.
            var x = new byte[64];
            x[0] = 0xAA; x[1] = 0xA9; x[2] = 0x5A;
            byte[] clear = { 5, 2, 0, 0, 0 };
            for (int i = 0; i < 5; i++) x[3 + i] = (byte)(clear[i] ^ 0x5A);
            x[8] = 1 ^ 0x5A; x[9] = 2 ^ 0x5A;
            Assert.Equal(Mad68ControlKind.Valid, Mad68ProRProtocol.DecodeControlResponse(x, 0x55, 0xA9).Kind);

            // A length above 0x38 is invalid (mad68pr_protocol.cpp:163-168).
            var longer = new byte[64];
            longer[0] = 0xAA; longer[1] = 0xA9; longer[4] = 0x39;
            Assert.Equal(Mad68ControlKind.Invalid, Mad68ProRProtocol.DecodeControlResponse(longer, 0x55, 0xA9).Kind);

            // Raw framing needs 5F and the opcode (mad68pr_protocol.cpp:142-149).
            var raw = new byte[64];
            raw[0] = 0x5F; raw[1] = 0xA9;
            Assert.Equal(Mad68ControlKind.Valid, Mad68ProRProtocol.DecodeControlResponse(raw, 0x5F, 0xA9).Kind);
            raw[1] = 0xA8;
            Assert.Equal(Mad68ControlKind.Invalid, Mad68ProRProtocol.DecodeControlResponse(raw, 0x5F, 0xA9).Kind);
            Assert.Equal(Mad68ControlKind.NotControl,
                Mad68ProRProtocol.DecodeControlResponse(Ack(0xA9).Skip(1).ToArray(), 0x5F, 0xA9).Kind);
            Assert.Equal(Mad68ControlKind.NotControl,
                Mad68ProRProtocol.DecodeControlResponse(A0(0, 10).Skip(1).ToArray(), 0x55, 0xA9).Kind);
        }

        [Fact]
        public void Mad68_KeySamples_DescriptorValueAndFields()
        {
            // DecodeKeySample (mad68pr_protocol.cpp:101-128).
            Assert.True(Mad68ProRProtocol.DecodeKeySample(A0(Key(W), 1600, 480, 1234, 7).Skip(1).ToArray(), out var w));
            Assert.Equal(W, w.Hid);
            Assert.Equal(1000, w.Milli);
            Assert.Equal(480, w.Threshold);
            Assert.Equal(1234, w.Baseline);
            Assert.Equal(7, w.State);

            // Above 1600 is rejected, as is an unknown descriptor or a short report.
            Assert.False(Mad68ProRProtocol.DecodeKeySample(A0(Key(W), 1601).Skip(1).ToArray(), out _));
            var unknown = A0(0, 10).Skip(1).ToArray();
            unknown[3] = 0xFF;
            Assert.False(Mad68ProRProtocol.DecodeKeySample(unknown, out _));
            Assert.False(Mad68ProRProtocol.DecodeKeySample(A0(0, 10).Skip(1).Take(19).ToArray(), out _));

            // Modifiers are 10 bb 00 and Fn is F0 FF 01 with no HID
            // (mad68pr_protocol.h:45, 61).
            Assert.Equal(0x102000, Mad68ProRProtocol.DescriptorOfKey(Key(0xE5)));
            Assert.Equal(0xF0FF01, Mad68ProRProtocol.DescriptorOfKey(7));
            Assert.Equal(0, Mad68ProRProtocol.HidOfKey(7));
            Assert.True(Mad68ProRProtocol.DecodeKeySample(A0(7, 800).Skip(1).ToArray(), out var fn));
            Assert.Equal(0, fn.Hid);
        }

        [Theory]
        // (raw * 1000 + 800) / 1600 (mad68pr_protocol.cpp:119-121).
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        [InlineData(800, 500)]
        [InlineData(1599, 999)]
        [InlineData(1600, 1000)]
        public void Mad68_Normalization(int raw, int milli)
        {
            Assert.Equal(milli, Mad68ProRProtocol.ToMilli(raw));
        }

        [Fact]
        public void Mad68_NormalizePayload_FindsTheHeader()
        {
            // NormalizePayload (mad68pr_backend.cpp:628-649).
            var n = new byte[64];
            Assert.True(Mad68ProRProtocol.NormalizePayload(new byte[] { 0x00, 0xA0, 1, 2 }, n));
            Assert.Equal(new byte[] { 0xA0, 1, 2, 0 }, Head(n, 4));
            Assert.True(Mad68ProRProtocol.NormalizePayload(new byte[] { 0xAA, 0xA9 }, n));
            Assert.Equal(new byte[] { 0xAA, 0xA9, 0 }, Head(n, 3));
            Assert.True(Mad68ProRProtocol.NormalizePayload(new byte[] { 0x07, 0x5F, 0xA9 }, n));
            Assert.Equal(new byte[] { 0x5F, 0xA9 }, Head(n, 2));
            Assert.False(Mad68ProRProtocol.NormalizePayload(new byte[] { 0x00, 0x00, 0xA0 }, n));
            Assert.False(Mad68ProRProtocol.NormalizePayload(ReadOnlySpan<byte>.Empty, n));
        }

        [Fact]
        public void Mad68_DigitalAgreementAndPostSweepProof()
        {
            // AnalogTransitionMatchesDigital (mad68pr_protocol.cpp:64-80):
            // press floor max(16, t/4), release ceiling max(32, t/2).
            Assert.True(Mad68ProRProtocol.AnalogTransitionMatchesDigital(true, 400, 0, 100));
            Assert.False(Mad68ProRProtocol.AnalogTransitionMatchesDigital(true, 400, 0, 7));
            Assert.True(Mad68ProRProtocol.AnalogTransitionMatchesDigital(true, 400, 0, 8));
            Assert.True(Mad68ProRProtocol.AnalogTransitionMatchesDigital(false, 400, 900, 200));
            Assert.False(Mad68ProRProtocol.AnalogTransitionMatchesDigital(false, 400, 900, 895));
            Assert.True(Mad68ProRProtocol.AnalogTransitionMatchesDigital(false, 400, 900, 892));
            Assert.True(Mad68ProRProtocol.AnalogTransitionMatchesDigital(false, 0, 0, 32));

            // IsPostSweepAnalogProof (mad68pr_protocol.cpp:82-99): a change of
            // at least 64 crossing the threshold.
            Assert.True(Mad68ProRProtocol.IsPostSweepAnalogProof(0, 600, 500));
            Assert.True(Mad68ProRProtocol.IsPostSweepAnalogProof(600, 400, 500));
            Assert.False(Mad68ProRProtocol.IsPostSweepAnalogProof(470, 520, 500));
            Assert.False(Mad68ProRProtocol.IsPostSweepAnalogProof(0, 400, 500));
            Assert.False(Mad68ProRProtocol.IsPostSweepAnalogProof(0, 600, 0));
            Assert.False(Mad68ProRProtocol.IsPostSweepAnalogProof(0, 1601, 500));
        }

        [Fact]
        public void Mad68_KeyTable_67KeysInLayoutOrder()
        {
            // mad68pr_protocol.h:37-106: 68 descriptors, Fn unpublished,
            // scanner slots 8, 16, 35 and 59 empty.
            var order = Mad68ProRProtocol.KeyOrder();
            Assert.Equal(67, order.Length);
            Assert.Equal(AnalogKeyCodes.Escape, order[0]);
            Assert.Equal(AnalogKeyCodes.ArrowRight, order[^1]);
            Assert.DoesNotContain(0, order);
            Assert.DoesNotContain(AnalogKeyCodes.Fn, order);
            Assert.Equal(order.Length, order.Distinct().Count());
            // Key index 0 is scanner slot 0, Q (mad68pr_protocol.h:38).
            Assert.Equal(Q, Mad68ProRProtocol.HidOfKey(0));
            Assert.Equal(60, Key(W));
            Assert.Equal(new[] { 0x1A, 0x04, 0x16, 0x07 }, Mad68ProRProtocol.WasdHids);
            var session = new Mad68ProRSession(0x1109, 0x0102);
            Assert.Equal("MADLIONS MAD 68 Pro R", session.ModelName);
            Assert.Equal(order, session.KeyOrder);
            Assert.Null(new Mad68ProRSession(0x1120, 0x0100).ModelName);
        }

        [Fact]
        public void Mad68_Start_AuditedIdentity_SendsNothing()
        {
            // mad68pr_backend.cpp:2266-2270: 1109 at bcdDevice 0102 needs no traffic.
            var (session, io, _) = Mad68();
            Assert.True(session.Start(io));
            Assert.Empty(io.Inner.Log);
        }

        [Fact]
        public void Mad68_Start_ProbesOtherIdentitiesWithA9()
        {
            // ProbeNativeControlProtocol (mad68pr_backend.cpp:959-984):
            // strategy 1's A9, a valid AA/A9 within 450 ms, A0 reports skipped.
            var (session, io, _) = Mad68(0x1109, 0x0103);
            io.Inner.OnSend = _ => new[] { A0(0, 10), Ack(0xA9) };
            Assert.True(session.Start(io));
            var sent = Assert.Single(io.Inner.Log);
            Assert.Equal("out", sent.Kind);
            Assert.Equal(65, sent.Data.Length);
            Assert.Equal(0, sent.Data[0]);
            Assert.Equal(Normal(0xA9), sent.Data.Skip(1).Take(8).ToArray());
        }

        [Fact]
        public void Mad68_Start_ProbeRejections()
        {
            // A checksum error or a mismatched reply fails at once, silence
            // fails after 450 ms, and a gone device fails (mad68pr_backend.cpp:969-983).
            foreach (var reply in new[] { Ack(0xA9, 0xAB), Ack(0xA8) })
            {
                var (session, io, _) = Mad68(0x1120, 0x0100);
                io.Inner.OnSend = _ => new[] { reply };
                Assert.False(session.Start(io));
                Assert.Single(io.Inner.Log);
            }
            var (quiet, quietIo, clock) = Mad68(0x1120, 0x0100);
            long started = clock.Now;
            Assert.False(quiet.Start(quietIo));
            Assert.InRange(clock.Now - started, 450, 475);

            var (gone, goneIo, _) = Mad68(0x1120, 0x0100);
            goneIo.Inner.Gone = true;
            Assert.False(gone.Start(goneIo));
        }

        [Fact]
        public void Mad68_PassiveFullSnapshot_PublishesWasdWithoutSending()
        {
            // mad68pr_backend.cpp:1964-1971: 68/68 seen while listening
            // passively for 1400 ms: nothing is sent and W, A, S and D publish.
            var (session, io, clock) = Mad68();
            Assert.True(session.Start(io));
            io.Inner.QueueInput(Sweep(hid => hid == W || hid == Q ? 800 : 0).ToArray());
            var output = Keys();
            PassUntil(session, io, output, _ => false, () => session.PublishMode == Mad68PublishMode.EmergencyWasd);
            Assert.Empty(io.Inner.Log);
            Assert.Equal(0.5f, output.Get(W));
            Assert.Equal(0f, output.Get(Q));
            Assert.False(session.Activating);
        }

        /// <summary>A keyboard that acknowledges A8 and A9 and answers A8 with
        /// three ordered sweeps (docs/protocols/MAD68_PRO_R_FIRMWARE_FINAL_AUDIT.md:199-209).</summary>
        private static void ScriptActivation(ClockedTransport io, Func<int, int> rawOfHid)
        {
            io.Inner.OnSend = req =>
            {
                if (req[2] == 0xA9) return new[] { Ack(0xA9) };
                if (req[2] == 0xA8)
                {
                    var answer = new List<byte[]> { Ack(0xA8) };
                    for (int i = 0; i < 3; i++) answer.AddRange(Sweep(rawOfHid));
                    return answer;
                }
                return Array.Empty<byte[]>();
            };
        }

        [Fact]
        public void Mad68_Activation_A9A8A9_ThenWasd_ThenAllKeysAfterSteadyProof()
        {
            // RunSession and RunStrategy (mad68pr_backend.cpp:1572-1647,
            // 1892-2123): 1400 ms passive, A9 + ack, all keys up 500 ms, A8 +
            // ack, A9 + ack, 68 fresh descriptors publish W A S D only.
            var (session, io, clock) = Mad68();
            ScriptActivation(io, hid => hid == W || hid == Q ? 800 : 0);
            Assert.True(session.Start(io));
            long begun = clock.Now;
            var output = Keys();
            PassUntil(session, io, output, _ => false,
                () => session.PublishMode == Mad68PublishMode.EmergencyWasd && !session.Activating);

            var writes = io.Inner.Writes("out");
            Assert.Equal(3, writes.Count);
            Assert.Equal(Normal(0xA9), writes[0].Skip(1).Take(8).ToArray());
            Assert.Equal(Normal(0xA8), writes[1].Skip(1).Take(8).ToArray());
            Assert.Equal(Normal(0xA9), writes[2].Skip(1).Take(8).ToArray());
            Assert.All(writes, w => Assert.Equal(65, w.Length));
            // The strategy's reopen flushes what arrived during its pause.
            Assert.Equal(1, io.Inner.Discards);
            Assert.Equal(0, session.StrategyIndex);
            Assert.False(session.SteadyStateConfirmed);
            Assert.Equal(0.5f, output.Get(W));
            Assert.Equal(0f, output.Get(Q));
            Assert.True(clock.Now - begun >= Mad68ProRSession.PassiveListenMs + Mad68ProRSession.AllReleasedStableMs);

            // Past the 4500 ms grace, a threshold crossing of at least 64
            // after three ordered sweeps proves the steady state
            // (mad68pr_backend.cpp:1086-1113), and all 67 keys publish.
            long graceEnd = clock.Now + Mad68ProRSession.ForcedSweepGraceMs;
            PassUntil(session, io, output, _ => false, () => clock.Now > graceEnd + 200);
            Assert.Equal(Mad68PublishMode.EmergencyWasd, session.PublishMode);
            io.Inner.QueueInput(A0(Key(W), 0));
            session.Pass(io, output, _ => false);
            Assert.True(session.SteadyStateConfirmed);
            Assert.Equal(Mad68PublishMode.Full, session.PublishMode);
            Assert.Equal(0.5f, output.Get(Q));
            Assert.Equal(0f, output.Get(W));
        }

        [Fact]
        public void Mad68_HeldKeys_DelayA8_AndGateTheirOwnership()
        {
            // WaitForAllReleased (mad68pr_backend.cpp:1412-1447) waits for
            // every key of this keyboard to be up for 500 ms before A8.
            var (session, io, clock) = Mad68();
            ScriptActivation(io, hid => hid == W || hid == Q ? 800 : 0);
            Assert.True(session.Start(io));
            var output = Keys();
            bool holdE = true;
            Func<int, bool> held = code => code == AnalogKeyCodes.E && holdE;
            PassUntil(session, io, output, held, () => io.Inner.Writes("out").Count == 1);
            for (int i = 0; i < 200; i++) session.Pass(io, output, held);
            Assert.Single(io.Inner.Writes("out"));
            holdE = false;
            PassUntil(session, io, output, held, () => io.Inner.Writes("out").Count == 2);
            Assert.Equal(Normal(0xA8), io.Inner.Writes("out")[1].Skip(1).Take(8).ToArray());

            // Reach full ownership, then press Q with no fresh A0: its last
            // sample is older than 250 ms, so the edge takes it away until a
            // new A0 arrives (Mad68ProR_OwnsHid, mad68pr_backend.cpp:2568-2601).
            PassUntil(session, io, output, held,
                () => session.PublishMode == Mad68PublishMode.EmergencyWasd && !session.Activating);
            long graceEnd = clock.Now + Mad68ProRSession.ForcedSweepGraceMs;
            PassUntil(session, io, output, held, () => clock.Now > graceEnd + 200);
            io.Inner.QueueInput(A0(Key(W), 0));
            session.Pass(io, output, held);
            Assert.Equal(Mad68PublishMode.Full, session.PublishMode);
            Assert.Equal(0.5f, output.Get(Q));

            bool holdQ = true;
            Func<int, bool> heldQ = code => code == Q && holdQ;
            session.Pass(io, output, heldQ);
            Assert.Equal(0f, output.Get(Q));
            io.Inner.QueueInput(A0(Key(Q), 1200));
            session.Pass(io, output, heldQ);
            Assert.Equal(0.75f, output.Get(Q));
        }

        [Fact]
        public void Mad68_StrategyList_EveryFrameInOrder_ThenExhausted()
        {
            // kStrategies (mad68pr_backend.cpp:118-134) against a keyboard
            // that never answers: each strategy's A9, then BestEffortRestore's
            // A9 through the strategy and through strategy 1
            // (mad68pr_backend.cpp:1554-1570). The raw 64-byte writes never
            // reach the keyboard. The delayed strategies do not await an
            // acknowledgment, so they go on to A8 and A9 and fail validation.
            var (session, io, _) = Mad68();
            Assert.True(session.Start(io));
            var output = Keys();
            PassUntil(session, io, output, _ => false,
                () => session.StrategiesTried == 13 && !session.Activating, 100000);
            for (int i = 0; i < 100; i++) session.Pass(io, output, _ => false);

            byte[] n9 = Normal(0xA9), n8 = Normal(0xA8);
            byte[] x9 = { 0x55, 0xA9, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A };
            byte[] r9 = { 0x5F, 0xA9, 0, 0, 0, 0, 0, 0 }, r8 = { 0x5F, 0xA8, 0, 0, 0, 0, 0, 0 };
            var expected = new List<(string, byte[])>
            {
                ("out", n9), ("out", n9),                       // 1: A9, restore
                ("out", n9), ("out", n9), ("out", n9),          // 2: A9, restore, primary
                ("out", x9), ("out", x9), ("out", n9),          // 3
                ("out", r9), ("out", r9), ("out", n9),          // 4
                ("out", n9),                                    // 5: raw64, primary only
                ("out", n9),                                    // 6
                ("out", n9),                                    // 7
                ("ctl", n9), ("ctl", n9), ("out", n9),          // 8
                ("ctl", x9), ("ctl", x9), ("out", n9),          // 9
                ("ctl", r9), ("ctl", r9), ("out", n9),          // 10
                ("out", n9), ("out", n8), ("out", n9), ("out", n9), ("out", n9), // 11: delayed
                ("out", r9), ("out", r8), ("out", r9), ("out", r9), ("out", n9), // 12
                ("out", n9),                                    // 13: raw64 delayed
            };
            var log = io.Inner.Log;
            Assert.Equal(expected.Count, log.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Item1, log[i].Kind);
                Assert.Equal(65, log[i].Data.Length);
                Assert.Equal(0, log[i].Data[0]);
                Assert.Equal(expected[i].Item2, log[i].Data.Skip(1).Take(8).ToArray());
            }
            Assert.Equal(13, io.Inner.Discards);
            Assert.Equal(Mad68PublishMode.None, session.PublishMode);

            // Exhausted: passive listening goes on and the close still restores.
            session.Stop(io);
            Assert.Equal(expected.Count + 1, log.Count);
            Assert.Equal(n9, log[^1].Data.Skip(1).Take(8).ToArray());
        }

        [Fact]
        public void Mad68_XorAndRawStrategies_AcceptTheirOwnAcknowledgments()
        {
            // Strategy 3 answers with XOR key 0x5A, strategy 4 with 5F framing.
            var (session, io, _) = Mad68();
            io.Inner.OnSend = req =>
            {
                if (req[1] == 0x55 && req[3] == 0x5A)
                {
                    var ack = Ack(req[2]);
                    ack[3] = 0x5A;
                    for (int i = 4; i <= 8; i++) ack[i] = 0x5A;
                    return new[] { ack };
                }
                return Array.Empty<byte[]>();
            };
            Assert.True(session.Start(io));
            var output = Keys();
            PassUntil(session, io, output, _ => false, () => session.StrategyIndex == 2 && io.Inner.Log.Count >= 7);
            // Strategy 3's A9 was acknowledged, so its next write is A8.
            PassUntil(session, io, output, _ => false,
                () => io.Inner.Log.Last().Data[2] == 0xA8 || session.StrategyIndex > 2);
            Assert.Equal(2, session.StrategyIndex);
            Assert.Equal(new byte[] { 0x55, 0xA8, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A },
                io.Inner.Log.Last().Data.Skip(1).Take(8).ToArray());
        }

        [Fact]
        public void Mad68_Stop_Restores_AsTheSessionEnds()
        {
            // Session end (mad68pr_backend.cpp:2125-2133): A9 through strategy 1.
            var (session, io, _) = Mad68();
            Assert.True(session.Start(io));
            session.Stop(io);
            Assert.Empty(io.Inner.Log); // stopped before the first pass (mad68pr_backend.cpp:1918)

            (session, io, _) = Mad68();
            Assert.True(session.Start(io));
            session.Pass(io, Keys(), _ => false);
            session.Stop(io);
            var only = Assert.Single(io.Inner.Log);
            Assert.Equal(Normal(0xA9), only.Data.Skip(1).Take(8).ToArray());
        }

        [Fact]
        public void Mad68_StopInsideAStrategy_RunsItsRestoreFirst()
        {
            // A stop inside strategy 2 while A8 awaits its acknowledgment:
            // BestEffortRestore's A9 through strategy 2 and strategy 1, then
            // the closing A9 (mad68pr_backend.cpp:1554-1570, 2125-2133).
            var (session, io, _) = Mad68();
            io.Inner.OnSend = req => req[2] == 0xA9 && session.StrategyIndex == 1
                ? new[] { Ack(0xA9) }
                : Array.Empty<byte[]>();
            Assert.True(session.Start(io));
            var output = Keys();
            PassUntil(session, io, output, _ => false,
                () => session.StrategyIndex == 1 && io.Inner.Log.Last().Data[2] == 0xA8);
            int before = io.Inner.Log.Count;
            session.Stop(io);
            var after = io.Inner.Log.Skip(before).ToList();
            Assert.Equal(3, after.Count);
            Assert.All(after, w => Assert.Equal(Normal(0xA9), w.Data.Skip(1).Take(8).ToArray()));
        }

        [Fact]
        public void Mad68_WasdMissOnADeadStream_RearmsFromStrategy1()
        {
            // ObserveDigitalEvents (mad68pr_backend.cpp:1770-1839): a W edge
            // with no fresh A0 within 1000 ms while no A0 at all arrived for
            // over 3000 ms stops serving and reruns the list from strategy 1
            // (mad68pr_backend.cpp:2015-2049).
            var (session, io, clock) = Mad68();
            ScriptActivation(io, hid => hid == W ? 800 : 0);
            Assert.True(session.Start(io));
            var output = Keys();
            PassUntil(session, io, output, _ => false,
                () => session.PublishMode == Mad68PublishMode.EmergencyWasd && !session.Activating);
            int sent = io.Inner.Log.Count;
            long quietUntil = clock.Now + 3500;
            PassUntil(session, io, output, _ => false, () => clock.Now > quietUntil);
            Func<int, bool> holdW = code => code == W;
            PassUntil(session, io, output, holdW, () => io.Inner.Log.Count > sent, 1000);
            Assert.Equal(0, session.StrategyIndex);
            Assert.True(session.Activating);
            Assert.Equal(0, output.Count);
            Assert.Equal(Normal(0xA9), io.Inner.Log.Last().Data.Skip(1).Take(8).ToArray());
        }

        [Fact]
        public void Mad68_ThreeReadErrors_EndTheSession()
        {
            // mad68pr_backend.cpp:1944-1948.
            var (session, io, _) = Mad68();
            Assert.True(session.Start(io));
            io.Inner.Gone = true;
            var output = Keys();
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
        }

        // ── ATK Hex80 ──

        /// <summary>A reply as Windows returns it: report ID 0, then the payload.</summary>
        private static byte[] HexReply(int length, params byte[] payload)
        {
            var r = new byte[length];
            Array.Copy(payload, 0, r, 1, payload.Length);
            return r;
        }

        private static byte[] TravelInfoReply(int travelMax, int length = 33)
            => HexReply(length, 0x02, 0x96, 0x24, (byte)(travelMax >> 8), (byte)travelMax);

        private static byte[] ChunkReply(int offset, int count, Func<int, int> travelOfSlot, int length = 33)
        {
            var payload = new byte[8 + count * 5];
            payload[0] = 0x02; payload[1] = 0x96; payload[2] = 0x1C;
            payload[5] = (byte)(offset >> 8); payload[6] = (byte)offset; payload[7] = (byte)count;
            for (int i = 0; i < count; i++)
            {
                int travel = travelOfSlot(offset + i);
                payload[10 + i * 5] = (byte)(travel >> 8);
                payload[11 + i * 5] = (byte)travel;
            }
            return HexReply(length, payload);
        }

        private static (AtkHex80Session session, ClockedTransport io, Clock clock) Hex80(int length = 33)
        {
            var clock = new Clock();
            var io = new ClockedTransport(new AnalogKeyboardTestTransport { InputLength = length, OutputLength = length }, clock);
            return (new AtkHex80Session(() => clock.Now), io, clock);
        }

        /// <summary>A keyboard with travel scale 3300 that answers every chunk
        /// with the travel <paramref name="travel"/> gives each slot.</summary>
        private static void ScriptHex80(ClockedTransport io, Func<int, int> travel, int travelMax = 3300)
        {
            io.Inner.OnSend = req =>
            {
                if (req[1] == 0x02 && req[3] == 0x24) return new[] { TravelInfoReply(travelMax, io.OutputLength) };
                if (req[1] == 0x02 && req[3] == 0x1C)
                    return new[] { ChunkReply((req[6] << 8) | req[7], req[8], travel, io.OutputLength) };
                return Array.Empty<byte[]>();
            };
        }

        [Fact]
        public void Hex80_Matches_ThreePidsOnFF60()
        {
            // hex80_backend.cpp:285-303, hex80_protocol.h:10-14.
            foreach (ushort pid in new ushort[] { 0x1176, 0x1177, 0x1250 })
            {
                Assert.True(AtkHex80Protocol.Matches(Info(0x373B, pid, 0xFF60, 0x0061, 33, 33)));
                Assert.True(AtkHex80Protocol.Matches(Info(0x373B, pid, 0xFF60, 0x0061, 129, 129)));
            }
            Assert.False(AtkHex80Protocol.Matches(Info(0x373B, 0x1058, 0xFF60, 0x0061, 33, 33)));
            Assert.False(AtkHex80Protocol.Matches(Info(0x373B, 0x1176, 0xFF00, 0x0061, 33, 33)));
            Assert.False(AtkHex80Protocol.Matches(Info(0x373B, 0x1176, 0xFF60, 0x0062, 33, 33)));
            Assert.False(AtkHex80Protocol.Matches(Info(0x373B, 0x1176, 0xFF60, 0x0061, 32, 33)));
            Assert.False(AtkHex80Protocol.Matches(Info(0x373B, 0x1176, 0xFF60, 0x0061, 33, 32)));
            Assert.False(AtkHex80Protocol.Matches(Info(0x373C, 0x1176, 0xFF60, 0x0061, 33, 33)));
            Assert.Equal(new[] { 0x1176, 0x1177, 0x1250 }, AtkHex80Protocol.ProductIds());
        }

        [Fact]
        public void Hex80_Requests_ByteForByte()
        {
            // hex80_protocol.cpp:20-50 and the spec's example frames.
            var info = AtkHex80Protocol.EncodeOutputReport(AtkHex80Protocol.BuildTravelInfoPayload(), 33);
            Assert.Equal(33, info.Length);
            Assert.Equal(new byte[] { 0x00, 0x02, 0x96, 0x24, 0, 0, 0, 0, 0 }, Head(info, 9));
            Assert.Equal(new byte[] { 0x00, 0x02, 0x96, 0x1C, 0, 0, 0x00, 0x00, 0x04 },
                Head(AtkHex80Protocol.EncodeOutputReport(AtkHex80Protocol.BuildTravelBufferPayload(0, 4), 33), 9));
            Assert.Equal(new byte[] { 0x00, 0x02, 0x96, 0x1C, 0, 0, 0x00, 0x64, 0x04 },
                Head(AtkHex80Protocol.EncodeOutputReport(AtkHex80Protocol.BuildTravelBufferPayload(100, 4), 33), 9));
            Assert.Equal(new byte[] { 0x00, 0x03, 0x96, 0x19, 0, 0, 0, 0, 0 },
                Head(AtkHex80Protocol.EncodeOutputReport(AtkHex80Protocol.BuildCalibrationFinishPayload(), 33), 9));
            Assert.Equal(129, AtkHex80Protocol.EncodeOutputReport(AtkHex80Protocol.BuildTravelInfoPayload(), 129).Length);
            Assert.Null(AtkHex80Protocol.EncodeOutputReport(AtkHex80Protocol.BuildTravelInfoPayload(), 32));
            // A nonzero byte past the report would be cut, so the send is refused.
            var wide = AtkHex80Protocol.BuildTravelInfoPayload();
            wide[40] = 1;
            Assert.Null(AtkHex80Protocol.EncodeOutputReport(wide, 33));
        }

        [Theory]
        // NormalizeTravelToMilli (hex80_protocol.cpp:52-64), dead zone 8.
        [InlineData(0, 3300, 0)]
        [InlineData(8, 3300, 0)]
        [InlineData(9, 3300, 0)]
        [InlineData(1654, 3300, 500)]
        [InlineData(3299, 3300, 1000)]
        [InlineData(3300, 3300, 1000)]
        [InlineData(4000, 3300, 1000)]
        [InlineData(500, 8, 0)]
        public void Hex80_Normalization(int travel, int travelMax, int milli)
        {
            Assert.Equal(milli, AtkHex80Protocol.NormalizeTravelToMilli(travel, travelMax));
        }

        [Fact]
        public void Hex80_Replies_MatchDecodeAndReject()
        {
            // DecodeTravelInfo accepts 256 to 20000 (hex80_protocol.cpp:102-116).
            Assert.True(AtkHex80Protocol.DecodeTravelInfo(TravelInfoReply(3300), out int max));
            Assert.Equal(3300, max);
            Assert.True(AtkHex80Protocol.DecodeTravelInfo(TravelInfoReply(3300).Skip(1).ToArray(), out _));
            Assert.False(AtkHex80Protocol.DecodeTravelInfo(TravelInfoReply(255), out _));
            Assert.False(AtkHex80Protocol.DecodeTravelInfo(TravelInfoReply(20001), out _));

            // DecodeTravelChunk (hex80_protocol.cpp:118-167): echoed offset
            // and count, slot to key, and the plausibility limit.
            var entries = new Hex80TravelEntry[4];
            Assert.True(AtkHex80Protocol.DecodeTravelChunk(ChunkReply(36, 4, s => s == 36 ? 3300 : 0), 36, 4, 3300, entries, out int count));
            Assert.Equal(4, count);
            Assert.Equal(W, entries[0].Hid);
            Assert.Equal(1000, entries[0].Milli);
            Assert.False(AtkHex80Protocol.DecodeTravelChunk(ChunkReply(32, 4, _ => 0), 36, 4, 3300, entries, out _));
            Assert.False(AtkHex80Protocol.DecodeTravelChunk(ChunkReply(36, 3, _ => 0), 36, 4, 3300, entries, out _));
            Assert.True(AtkHex80Protocol.DecodeTravelChunk(ChunkReply(36, 4, _ => 6600), 36, 4, 3300, entries, out _));
            Assert.False(AtkHex80Protocol.DecodeTravelChunk(ChunkReply(36, 4, _ => 6601), 36, 4, 3300, entries, out _));
            var shortReply = ChunkReply(36, 4, _ => 0).Take(1 + 8 + 4 * 5 - 1).ToArray();
            Assert.False(AtkHex80Protocol.DecodeTravelChunk(shortReply, 36, 4, 3300, entries, out _));

            // MatchesRequest skips a late reply to another offset (hex80_protocol.cpp:88-100).
            var request = AtkHex80Protocol.BuildTravelBufferPayload(36, 4);
            Assert.True(AtkHex80Protocol.MatchesRequest(request, ChunkReply(36, 4, _ => 0)));
            Assert.False(AtkHex80Protocol.MatchesRequest(request, ChunkReply(40, 4, _ => 0)));
        }

        [Fact]
        public void Hex80_KeyTable_87KeysWithFnAt96()
        {
            // kSlotToHid (hex80_protocol.h:36-54).
            Assert.Equal(AnalogKeyCodes.Escape, AtkHex80Protocol.HidOfSlot(0));
            Assert.Equal(0, AtkHex80Protocol.HidOfSlot(13));
            Assert.Equal(W, AtkHex80Protocol.HidOfSlot(36));
            Assert.Equal(AnalogKeyCodes.Fn, AtkHex80Protocol.HidOfSlot(96));
            Assert.Equal(0, AtkHex80Protocol.HidOfSlot(102));
            Assert.Equal(0, AtkHex80Protocol.HidOfSlot(103));
            var order = AtkHex80Protocol.KeyOrder();
            Assert.Equal(87, order.Length);
            Assert.Contains(AnalogKeyCodes.Fn, order);
            Assert.Equal(AnalogKeyCodes.Escape, order[0]);
            var session = new AtkHex80Session();
            Assert.Equal("ATK x QK Hex80", session.ModelName);
            Assert.Equal(order, session.KeyOrder);
        }

        [Fact]
        public void Hex80_Start_TwoGets_ThenCalibrationFinish()
        {
            // RunSession (hex80_backend.cpp:496-519).
            var (session, io, _) = Hex80();
            ScriptHex80(io, _ => 0);
            Assert.True(session.Start(io));
            Assert.Equal(3300, session.TravelMax);
            var writes = io.Inner.Writes("out");
            Assert.Equal(3, writes.Count);
            Assert.All(writes, w => Assert.Equal(33, w.Length));
            Assert.Equal(new byte[] { 0x00, 0x02, 0x96, 0x24 }, Head(writes[0], 4));
            Assert.Equal(new byte[] { 0x00, 0x02, 0x96, 0x1C, 0, 0, 0, 0, 4 }, Head(writes[1], 9));
            Assert.Equal(new byte[] { 0x00, 0x03, 0x96, 0x19, 0 }, Head(writes[2], 5));
            Assert.Equal(0, io.Inner.Discards);
        }

        [Fact]
        public void Hex80_Start_Rejections()
        {
            // An out-of-range scale, silence and a mismatched chunk fail the
            // proof before the SET, and a failed SET fails the session
            // (hex80_backend.cpp:502-519).
            var (outOfRange, io1, _) = Hex80();
            ScriptHex80(io1, _ => 0, travelMax: 255);
            Assert.False(outOfRange.Start(io1));
            Assert.Single(io1.Inner.Log);

            var (silent, io2, clock) = Hex80();
            long t = clock.Now;
            Assert.False(silent.Start(io2));
            Assert.Equal(AtkHex80Session.ProbeReadTimeoutMs, clock.Now - t);

            var (mismatch, io3, _) = Hex80();
            io3.Inner.OnSend = req => req[3] == 0x24
                ? new[] { TravelInfoReply(3300) }
                : new[] { ChunkReply(0, 3, _ => 0) };
            Assert.False(mismatch.Start(io3));
            Assert.Equal(2, io3.Inner.Log.Count);

            var (refused, io4, _) = Hex80();
            ScriptHex80(io4, _ => 0);
            io4.RefuseSend = req => req[1] == 0x03;
            Assert.False(refused.Start(io4));
        }

        [Fact]
        public void Hex80_Pass_OneChunkPerPass_26PerCycle()
        {
            // hex80_backend.cpp:540-589: offsets 0 to 100, four slots each,
            // the reply found with or without the report ID and after a late
            // reply to another offset.
            var (session, io, _) = Hex80();
            ScriptHex80(io, s => s == 36 ? 3300 : s == 52 ? 1654 : 0);
            Assert.True(session.Start(io));
            io.Inner.Log.Clear();
            var output = Keys();
            for (int i = 0; i < 26; i++)
                Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            var offsets = io.Inner.Writes("out").Select(w => (w[6] << 8) | w[7]).ToArray();
            Assert.Equal(Enumerable.Range(0, 26).Select(i => i * 4).ToArray(), offsets);
            Assert.All(io.Inner.Writes("out"), w => Assert.Equal(4, w[8]));
            Assert.Equal(0, session.NextOffset);
            Assert.Equal(1f, output.Get(W));
            Assert.Equal(0.5f, output.Get(A));
            Assert.Equal(2, output.Count);

            // A late reply is skipped, and a reply without the report ID reads.
            io.Inner.OnSend = req =>
            {
                int offset = (req[6] << 8) | req[7];
                return new[] { ChunkReply(offset + 4, 4, _ => 0), ChunkReply(offset, 4, s => s == 36 ? 1654 : 0).Skip(1).ToArray() };
            };
            for (int i = 0; i < 26; i++) session.Pass(io, output, null);
            Assert.Equal(0.5f, output.Get(W));
            Assert.Equal(0f, output.Get(A));
        }

        [Fact]
        public void Hex80_StaleKeysReadZero_After500Ms()
        {
            // hex80_protocol.h:57-60, hex80_backend.cpp:882-889: a key whose
            // chunk stops answering reads 0 after 500 ms.
            var (session, io, _) = Hex80();
            ScriptHex80(io, s => s == 36 ? 3300 : 0);
            Assert.True(session.Start(io));
            var output = Keys();
            for (int i = 0; i < 26; i++) session.Pass(io, output, null);
            Assert.Equal(1f, output.Get(W));
            // From here only the chunk at offset 36 fails.
            io.Inner.OnSend = req =>
            {
                int offset = (req[6] << 8) | req[7];
                return offset == 36 ? Array.Empty<byte[]>() : new[] { ChunkReply(offset, 4, _ => 0) };
            };
            for (int i = 0; i < 26 * 20 && output.Get(W) != 0f; i++) session.Pass(io, output, null);
            Assert.Equal(0f, output.Get(W));
        }

        [Fact]
        public void Hex80_EightFailedChunksInARow_EndTheSession()
        {
            // kMaxConsecutiveFailures (hex80_backend.cpp:42, 559-565).
            var (session, io, _) = Hex80();
            ScriptHex80(io, _ => 0);
            Assert.True(session.Start(io));
            io.Inner.OnSend = _ => Array.Empty<byte[]>();
            var output = Keys();
            for (int i = 0; i < 7; i++)
                Assert.NotEqual(AnalogPollResult.Failed, session.Pass(io, output, null));
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));

            // A good chunk in between resets the count.
            (session, io, _) = Hex80();
            ScriptHex80(io, _ => 0);
            Assert.True(session.Start(io));
            int call = 0;
            io.Inner.OnSend = req => ++call % 7 == 0
                ? new[] { ChunkReply((req[6] << 8) | req[7], 4, _ => 0) }
                : Array.Empty<byte[]>();
            for (int i = 0; i < 50; i++)
                Assert.NotEqual(AnalogPollResult.Failed, session.Pass(io, output, null));

            // A gone device fails every request, so eight passes end it
            // (hex80_backend.cpp:389, 405).
            (session, io, _) = Hex80();
            ScriptHex80(io, _ => 0);
            Assert.True(session.Start(io));
            io.Inner.Gone = true;
            for (int i = 0; i < 7; i++)
                Assert.NotEqual(AnalogPollResult.Failed, session.Pass(io, output, null));
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
        }

        [Fact]
        public void Hex80_Stop_SendsNothing()
        {
            // Hex80_StopGeneration (hex80_backend.cpp:793-837) sends nothing.
            var (session, io, _) = Hex80();
            ScriptHex80(io, _ => 0);
            Assert.True(session.Start(io));
            int before = io.Inner.Log.Count;
            session.Stop(io);
            Assert.Equal(before, io.Inner.Log.Count);
        }

        // ── IROK M484: NA87 Mag and AJAZZ AK820 MAX RGB ──

        private static byte[] M484Identity(string text)
        {
            var r = new byte[64];
            r[0] = 1; r[1] = 0x0D;
            r[5] = (byte)(5 + text.Length);
            Encoding.ASCII.GetBytes(text).CopyTo(r, 6);
            return r;
        }

        private static byte[] Capability(byte nominal)
        {
            var r = new byte[64];
            r[0] = 1; r[1] = 0x21; r[5] = 0x18; r[6] = 0x04; r[7] = nominal;
            return r;
        }

        private static byte[] MapRow(int row, int[] map)
        {
            var r = new byte[64];
            r[0] = 1; r[1] = 0x10;
            r[3] = (byte)(row < 5 ? 0 : 255);
            r[4] = (byte)(row < 5 ? row + 1 : 255);
            r[5] = 22;
            for (int c = 0; c < 22; c++) r[6 + c] = (byte)map[row * 22 + c];
            return r;
        }

        private static byte[] Event(int row, int column, int travel)
        {
            var r = new byte[64];
            r[0] = 1; r[1] = 0x21; r[5] = 3; r[6] = 1; r[7] = (byte)row; r[8] = (byte)column; r[9] = (byte)travel;
            return r;
        }

        private static byte[] RawRow(int row, Func<int, int> adcOfColumn)
        {
            var r = new byte[64];
            r[0] = 1; r[1] = 0x23; r[4] = (byte)(row + 1); r[5] = 44;
            for (int c = 0; c < 22; c++)
            {
                int adc = adcOfColumn(c);
                r[6 + 2 * c] = (byte)(adc >> 8);
                r[7 + 2 * c] = (byte)adc;
            }
            return r;
        }

        private const string Na87Id = "M484,01,KB,ABT,GK8260HERGB,V1_11_07";
        private const string AjazzId = "M484,01,KB,ABT,SG8994HERGB,V1.13.17  ";

        private static (IrokM484Session session, ClockedTransport io, Clock clock) M484(
            string identity, int[] map = null, byte nominal = 40)
        {
            var clock = new Clock();
            var io = new ClockedTransport(new AnalogKeyboardTestTransport { InputLength = 64, OutputLength = 64 }, clock);
            map ??= IrokM484Protocol.FactoryMap();
            io.Inner.OnSend = req =>
            {
                if (req[1] == 0x0D) return new[] { M484Identity(identity) };
                if (req[1] == 0x21 && req[6] == 0x04) return new[] { Capability(nominal) };
                if (req[1] == 0x10) return Enumerable.Range(0, 6).Select(row => MapRow(row, map)).ToArray();
                return Array.Empty<byte[]>();
            };
            return (new IrokM484Session(() => clock.Now, ms => clock.Now += ms), io, clock);
        }

        [Fact]
        public void M484_Matches_UsageAndLengths()
        {
            // irok_na87_backend.cpp:226-238.
            Assert.True(IrokM484Protocol.Matches(Info(0x0416, 0x7372, 0xFF1B, 0x0091, 64, 64)));
            Assert.False(IrokM484Protocol.Matches(Info(0x0416, 0x7372, 0xFF1B, 0x0092, 64, 64)));
            Assert.False(IrokM484Protocol.Matches(Info(0x0416, 0x7372, 0xFF1C, 0x0091, 64, 64)));
            Assert.False(IrokM484Protocol.Matches(Info(0x0416, 0x7372, 0xFF1B, 0x0091, 65, 64)));
            Assert.False(IrokM484Protocol.Matches(Info(0x0416, 0x7372, 0xFF1B, 0x0091, 64, 65)));
            Assert.False(IrokM484Protocol.Matches(Info(0x0416, 0x7373, 0xFF1B, 0x0091, 64, 64)));
            Assert.False(IrokM484Protocol.Matches(Info(0x0417, 0x7372, 0xFF1B, 0x0091, 64, 64)));
        }

        [Fact]
        public void M484_Requests_ByteForByte()
        {
            // irok_nd75_protocol.cpp:42-71, irok_na87_protocol.h:14, irok_na87_backend.cpp:845-849.
            Assert.Equal(new byte[] { 1, 0x0D, 0 }, Head(IrokM484Protocol.IdentityRequest(), 3));
            Assert.Equal(new byte[] { 1, 0x21, 0, 0, 0, 0x18, 0x04, 0 }, Head(IrokM484Protocol.CapabilityRequest(), 8));
            Assert.Equal(new byte[] { 1, 0x21, 0, 0, 0, 0x18, 0x03, 0 }, Head(IrokM484Protocol.UnsubscribeRequest(), 8));
            Assert.Equal(new byte[] { 1, 0x10, 0 }, Head(IrokM484Protocol.MapRequest(), 3));
            Assert.Equal(new byte[] { 1, 0x23, 0, 0, 0, 0, 1, 0 }, Head(IrokM484Protocol.RawRowsOnRequest(), 8));
            Assert.Equal(new byte[] { 1, 0x23, 0, 0, 0, 0, 0 }, Head(IrokM484Protocol.RawRowsOffRequest(), 7));
            foreach (var r in new[] { IrokM484Protocol.IdentityRequest(), IrokM484Protocol.RawRowsOffRequest() })
            {
                Assert.Equal(64, r.Length);
                Assert.All(r.Skip(2), b => Assert.Equal(0, b));
            }

            // The factory subscription mask (irok_nd75_protocol.cpp:94-103), the spec's companion value.
            var mask = IrokM484Protocol.SubscriptionMask(IrokM484Protocol.FactoryMap());
            Assert.Equal(new byte[]
            {
                0x3F, 0x36, 0x3F, 0x1F, 0x1F, 0x1F, 0x3E, 0x1F, 0x1F, 0x1F, 0x3F,
                0x3F, 0x3F, 0x29, 0x1F, 0x27, 0x37, 0x27, 0x00, 0x00, 0x00, 0x00,
            }, mask);
            var subscribe = IrokM484Protocol.SubscriptionRequest(mask);
            Assert.Equal(new byte[] { 1, 0x21, 0, 0, 0, 0x18, 0x02 }, Head(subscribe, 7));
            Assert.Equal(mask, subscribe.Skip(7).Take(22).ToArray());
            Assert.All(subscribe.Skip(29), b => Assert.Equal(0, b));
        }

        [Fact]
        public void M484_Identity_FieldsAndLimits()
        {
            // DecodeDeviceInfo (irok_nd75_protocol.cpp:105-137).
            Assert.True(IrokM484Protocol.DecodeDeviceInfo(M484Identity(Na87Id), out var na87));
            Assert.Equal("M484", na87.Controller);
            Assert.Equal("GK8260HERGB", na87.Product);
            Assert.Equal("V1_11_07", na87.Firmware);
            Assert.True(IrokM484Protocol.IsNa87(na87));
            Assert.False(IrokM484Protocol.IsAjazzRgb(na87));

            // The AJAZZ firmware is exact once trailing spaces go (irok_na87_backend.cpp:344-353).
            Assert.True(IrokM484Protocol.DecodeDeviceInfo(M484Identity(AjazzId), out var ajazz));
            Assert.True(IrokM484Protocol.IsAjazzRgb(ajazz));
            Assert.True(IrokM484Protocol.DecodeDeviceInfo(M484Identity("M484,01,KB,ABT,SG8994HERGB,V1.13.170"), out var newer));
            Assert.False(IrokM484Protocol.IsAjazzRgb(newer));
            Assert.True(IrokM484Protocol.DecodeDeviceInfo(M484Identity("M484,01,KB,ABT,SG8994HE,V1.13.17"), out var noLight));
            Assert.False(IrokM484Protocol.IsAjazzRgb(noLight));
            Assert.False(IrokM484Protocol.IsNa87(noLight));

            // Five fields suffice, a NUL ends the text, four or seven fail.
            Assert.True(IrokM484Protocol.DecodeDeviceInfo(M484Identity("M484,01,KB,ABT,GK8260HERGB"), out var five));
            Assert.Equal(string.Empty, five.Firmware);
            Assert.True(IrokM484Protocol.DecodeDeviceInfo(M484Identity("M484,01,KB,ABT,GK8260HERGB\0junk"), out var cut));
            Assert.Equal("GK8260HERGB", cut.Product);
            Assert.False(IrokM484Protocol.DecodeDeviceInfo(M484Identity("M484,01,KB,ABT"), out _));
            Assert.False(IrokM484Protocol.DecodeDeviceInfo(M484Identity("M484,01,KB,ABT,X,V1,extra"), out _));
            // Non-printable, over-long and empty fields fail.
            Assert.False(IrokM484Protocol.DecodeDeviceInfo(M484Identity("M484,01,KB,ABT,GK8260\u0001HERGB"), out _));
            Assert.False(IrokM484Protocol.DecodeDeviceInfo(M484Identity("M4840123456789ABC,01,KB,ABT,GK8260HERGB"), out _));
            Assert.False(IrokM484Protocol.DecodeDeviceInfo(M484Identity(",01,KB,ABT,GK8260HERGB"), out _));
            var header = M484Identity(Na87Id);
            header[2] = 1;
            Assert.False(IrokM484Protocol.DecodeDeviceInfo(header, out _));
        }

        [Theory]
        // ToMilli (irok_nd75_protocol.cpp:193-198) and HallJoy's self-test
        // values 10, 20, 30, 40 (irok_na87_backend.cpp:1156-1157).
        [InlineData(0, 0)]
        [InlineData(1, 25)]
        [InlineData(10, 250)]
        [InlineData(20, 500)]
        [InlineData(30, 750)]
        [InlineData(31, 775)]
        [InlineData(40, 1000)]
        public void M484_TravelNormalization(int travel, int milli)
        {
            Assert.True(IrokM484Protocol.TryTravelToMilli(travel, out int value));
            Assert.Equal(milli, value);
        }

        [Fact]
        public void M484_Events_AndTheMapReader()
        {
            // The emulation example 01 21 00 00 00 03 01 03 00 1f
            // (docs/research/IROK_WITMOD_OFFLINE_CLOSURE_2026-09-13.md:90).
            var e = Event(3, 0, 0x1F);
            Assert.True(IrokM484Protocol.IsNa87EventShape(e));
            Assert.True(IrokM484Protocol.DecodeLiveEvent(e, out int row, out int column, out int travel));
            Assert.Equal((3, 0, 31), (row, column, travel));
            Assert.False(IrokM484Protocol.TryTravelToMilli(41, out _));
            Assert.False(IrokM484Protocol.DecodeLiveEvent(Event(6, 0, 10), out _, out _, out _));
            Assert.False(IrokM484Protocol.DecodeLiveEvent(Event(0, 22, 10), out _, out _, out _));
            var odd = Event(3, 2, 10);
            odd[2] = 1;
            Assert.False(IrokM484Protocol.IsNa87EventShape(odd));

            // MapReader (irok_na87_protocol.h:17-47): six rows, FF FF for the last.
            var factory = IrokM484Protocol.FactoryMap();
            var reader = new IrokM484MapReader();
            for (int r = 0; r < 5; r++)
            {
                Assert.True(reader.Feed(MapRow(r, factory)));
                Assert.False(reader.Complete());
            }
            Assert.True(reader.Feed(MapRow(5, factory)));
            Assert.True(reader.Complete());
            Assert.Equal(factory, reader.Map());
            // A repeated row with other content is a conflict (irok_na87_backend.cpp:1170-1171).
            var altered = (int[])factory.Clone();
            altered[5] = 0x1E;
            reader.Feed(MapRow(0, altered));
            Assert.False(reader.Complete());
            // Duplicates are allowed only when asked.
            var duplicated = (int[])factory.Clone();
            duplicated[2 * 22 + 2] = 0x04;
            var strict = new IrokM484MapReader();
            var lenient = new IrokM484MapReader(allowDuplicateHids: true);
            for (int r = 0; r < 6; r++)
            {
                strict.Feed(MapRow(r, duplicated));
                lenient.Feed(MapRow(r, duplicated));
            }
            Assert.False(strict.Complete());
            Assert.True(lenient.Complete());
        }

        [Fact]
        public void M484_FactoryTable_90KeysWithFnTranslated()
        {
            // irok_na87_factory.h:5-18: 90 positions, Fn 0xFA at 121.
            var factory = IrokM484Protocol.FactoryMap();
            Assert.Equal(132, factory.Length);
            Assert.Equal(90, factory.Count(c => c != 0));
            Assert.Equal(0xFA, factory[121]);
            Assert.Equal(W, factory[46]);
            var keys = IrokM484Protocol.FactoryKeyOrder();
            Assert.Equal(90, keys.Length);
            Assert.Contains(AnalogKeyCodes.Fn, keys);
            Assert.DoesNotContain(0xFA, keys);
            Assert.Equal(AnalogKeyCodes.Fn, IrokM484Protocol.HostCode(0xFA));
            Assert.Equal(W, IrokM484Protocol.HostCode(W));
        }

        [Fact]
        public void M484_Na87_Start_ProvesThenSubscribes()
        {
            // Prove and Run (irok_na87_backend.cpp:402-415, 486-517).
            var (session, io, _) = M484(Na87Id);
            Assert.True(session.Start(io));
            Assert.Equal(IrokM484Mode.Na87Events, session.Mode);
            Assert.Equal("IROK NA87 Mag", session.ModelName);
            var writes = io.Inner.Writes("out");
            Assert.Equal(5, writes.Count);
            Assert.Equal(IrokM484Protocol.IdentityRequest(), writes[0]);
            Assert.Equal(IrokM484Protocol.CapabilityRequest(), writes[1]);
            Assert.Equal(IrokM484Protocol.MapRequest(), writes[2]);
            Assert.Equal(IrokM484Protocol.UnsubscribeRequest(), writes[3]);
            Assert.Equal(IrokM484Protocol.SubscriptionRequest(
                IrokM484Protocol.SubscriptionMask(IrokM484Protocol.FactoryMap())), writes[4]);
            Assert.Equal(90, session.KeyOrder.Length);
            Assert.Contains(AnalogKeyCodes.Fn, session.KeyOrder);
        }

        [Fact]
        public void M484_Start_Rejections()
        {
            // The first identity reply decides (irok_na87_backend.cpp:355-373):
            // ND75 and other AJAZZ firmware are refused after one request.
            foreach (var id in new[] { "M484,01,KB,ABT,X86HERGB,V1.00.09", "M484,01,KB,ABT,SG8994HERGB,V1.13.18" })
            {
                var (session, io, _) = M484(id);
                Assert.False(session.Start(io));
                Assert.Single(io.Inner.Log);
            }
            // Capability must report 40 (irok_na87_backend.cpp:409).
            var (cap, capIo, _) = M484(Na87Id, nominal: 30);
            Assert.False(cap.Start(capIo));
            Assert.Equal(2, capIo.Inner.Log.Count);
            // A map with a duplicate code is incomplete for the NA87 and times out.
            var duplicated = IrokM484Protocol.FactoryMap();
            duplicated[2 * 22 + 2] = 0x04;
            var (dup, dupIo, clock) = M484(Na87Id, duplicated);
            long t = clock.Now;
            Assert.False(dup.Start(dupIo));
            Assert.Equal(3, dupIo.Inner.Log.Count);
            Assert.True(clock.Now - t >= IrokM484Session.ProofTimeoutMs);
            // A silent keyboard fails after 1200 ms.
            var (quiet, quietIo, quietClock) = M484(Na87Id);
            quietIo.Inner.OnSend = _ => Array.Empty<byte[]>();
            long q = quietClock.Now;
            Assert.False(quiet.Start(quietIo));
            Assert.InRange(quietClock.Now - q, 1200, 1300);
        }

        [Fact]
        public void M484_Na87_FailedSubscribe_IsUndone()
        {
            // HallJoy returns without undoing a failed subscribe write
            // (irok_na87_backend.cpp:512-517). It may have landed, so the
            // unsubscribe goes out before the route gives up.
            var (session, io, _) = M484(Na87Id);
            io.RefuseSend = req => req[1] == 0x21 && req[6] == 0x02;
            Assert.False(session.Start(io));
            var writes = io.Inner.Writes("out");
            Assert.Equal(IrokM484Protocol.UnsubscribeRequest(), writes[^1]);
            Assert.Equal(IrokM484Protocol.UnsubscribeRequest(), writes[^2]);
        }

        [Fact]
        public void M484_Na87_Events_IndependentDepthsAndReleases()
        {
            // HallJoy's self-test (irok_na87_backend.cpp:1155-1163): W, A, S,
            // D at 10, 20, 30, 40 survive unrelated updates, a release isolates
            // one key, and travel 255 changes nothing.
            var (session, io, _) = M484(Na87Id);
            Assert.True(session.Start(io));
            var output = Keys();
            io.Inner.QueueInput(Event(2, 2, 10), Event(3, 2, 20), Event(3, 3, 30), Event(3, 4, 40));
            for (int i = 0; i < 4; i++) Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.25f, output.Get(W));
            Assert.Equal(0.5f, output.Get(A));
            Assert.Equal(0.75f, output.Get(S));
            Assert.Equal(1f, output.Get(D));
            io.Inner.QueueInput(Event(2, 2, 0));
            session.Pass(io, output, null);
            Assert.Equal(0f, output.Get(W));
            Assert.Equal(0.5f, output.Get(A));
            io.Inner.QueueInput(Event(3, 2, 255));
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(A));
            // Fn publishes as 0x409.
            io.Inner.QueueInput(Event(5, 11, 20));
            session.Pass(io, output, null);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.Fn));
            // A silent key keeps its value (irok_na87_protocol.h:49-50).
            for (int i = 0; i < 5; i++) session.Pass(io, output, null);
            Assert.Equal(0.5f, output.Get(A));
        }

        [Fact]
        public void M484_Na87_Heartbeat_AndItsTimeout()
        {
            // Identity every 1000 ms, no NA87 reply for 3000 ms ends the
            // session without the unsubscribe (irok_na87_backend.cpp:645-656, 721-722).
            var (session, io, _) = M484(Na87Id);
            Assert.True(session.Start(io));
            io.Inner.OnSend = _ => Array.Empty<byte[]>();
            io.Inner.Log.Clear();
            var output = Keys();
            AnalogPollResult result = AnalogPollResult.Idle;
            for (int i = 0; i < 40 && result != AnalogPollResult.Failed; i++) result = session.Pass(io, output, null);
            Assert.Equal(AnalogPollResult.Failed, result);
            Assert.True(session.TransportLost);
            Assert.Equal(3, io.Inner.Writes("out").Count(w => w[1] == 0x0D));
            session.Stop(io);
            Assert.DoesNotContain(io.Inner.Writes("out"), w => w[1] == 0x21);

            // Answered heartbeats keep it alive, and a normal stop unsubscribes.
            (session, io, _) = M484(Na87Id);
            Assert.True(session.Start(io));
            for (int i = 0; i < 100; i++)
                Assert.NotEqual(AnalogPollResult.Failed, session.Pass(io, output, null));
            session.Stop(io);
            Assert.Equal(IrokM484Protocol.UnsubscribeRequest(), io.Inner.Writes("out")[^1]);
        }

        [Fact]
        public void M484_Na87_ReadErrors()
        {
            // A lost device ends the session at once, and three failed reads
            // in a row do too (irok_nd75_protocol.cpp:177-183).
            var (session, io, _) = M484(Na87Id);
            Assert.True(session.Start(io));
            io.Inner.Gone = true;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, Keys(), null));
            Assert.True(session.TransportLost);

            (session, io, _) = M484(Na87Id);
            Assert.True(session.Start(io));
            var shortReport = new byte[10];
            io.Inner.QueueInput(shortReport, shortReport, shortReport);
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, Keys(), null));
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, Keys(), null));
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, Keys(), null));
        }

        [Fact]
        public void M484_Ajazz_Start_RawRowsOn_AndOffAtStop()
        {
            // RunAjazz (irok_na87_backend.cpp:826-876): map with duplicates
            // allowed, raw rows on, raw rows off on every exit.
            var (session, io, _) = M484(AjazzId);
            Assert.True(session.Start(io));
            Assert.Equal(IrokM484Mode.AjazzRawRows, session.Mode);
            Assert.Equal("AJAZZ AK820 MAX RGB", session.ModelName);
            var writes = io.Inner.Writes("out");
            Assert.Equal(4, writes.Count);
            Assert.Equal(IrokM484Protocol.RawRowsOnRequest(), writes[3]);
            Assert.DoesNotContain(writes, w => w[1] == 0x21 && (w[6] == 0x02 || w[6] == 0x03));
            session.Stop(io);
            Assert.Equal(IrokM484Protocol.RawRowsOffRequest(), io.Inner.Writes("out")[^1]);

            // A failed enable is still switched off (irok_na87_backend.cpp:841-850).
            var (failed, failedIo, _) = M484(AjazzId);
            failedIo.RefuseSend = req => req[1] == 0x23 && req[6] == 1;
            Assert.False(failed.Start(failedIo));
            Assert.Equal(IrokM484Protocol.RawRowsOffRequest(), failedIo.Inner.Writes("out")[^1]);
        }

        [Fact]
        public void M484_Ajazz_DynamicLimits_HallJoysVectors()
        {
            // HallJoy's ordinary-build self-test (irok_na87_backend.cpp:1217-1239):
            // A at position 68, S at 69, Fn at 121, rows of 2400 at rest,
            // three samples per row for the median.
            var (session, io, _) = M484(AjazzId);
            Assert.True(session.Start(io));
            var output = Keys();
            byte[] Row(int row, int first, int second) => RawRow(row, c =>
                row == 3 && c == 2 ? first : row == 3 && c == 3 ? second : row == 5 && c == 11 ? first : 2400);
            void Feed(params byte[][] rows)
            {
                foreach (var r in rows)
                {
                    io.Inner.QueueInput(r);
                    session.Pass(io, output, null);
                }
            }

            // Nothing publishes until all six rows arrived.
            Feed(Row(0, 2400, 2400), Row(1, 2400, 2400), Row(2, 2400, 2400), Row(3, 2400, 2400), Row(4, 2400, 2400));
            Assert.False(session.AjazzConnected);
            Feed(Row(5, 2400, 2400));
            Assert.True(session.AjazzConnected);
            Feed(Row(3, 2400, 2400), Row(3, 2400, 2400), Row(5, 2400, 2400), Row(5, 2400, 2400));
            Assert.Equal(0, output.Count);

            Feed(Row(3, 1353, 1355), Row(3, 1353, 1355), Row(3, 1353, 1355));
            Assert.Equal(1f, output.Get(A));
            Assert.Equal(1f, output.Get(S));
            Feed(Row(3, 2400, 1355), Row(3, 2400, 1355), Row(3, 2400, 1355));
            Assert.Equal(0f, output.Get(A));
            Assert.Equal(1f, output.Get(S));
            Feed(Row(5, 1289, 2400), Row(5, 1289, 2400), Row(5, 1289, 2400));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.Fn));

            // A malformed row changes nothing (irok_na87_backend.cpp:1232-1234).
            var malformed = RawRow(3, _ => 1000);
            malformed[4] = 7;
            Feed(malformed);
            Assert.Equal(1f, output.Get(S));
            Assert.Equal(0f, output.Get(A));
        }

        [Fact]
        public void M484_Ajazz_RangeLearning_Midpoint()
        {
            // DynamicLimits::Raw (ajazz_raw_limits.h:41-59): rest 2400, full
            // seed 1353, a median of 1876 is (2400 - 1876) * 1000 / 1047 = 500.
            var limits = new AjazzRawLimits();
            Assert.True(limits.Raw(68, 2400, out int milli));
            Assert.Equal(0, milli);
            Assert.True(limits.Raw(68, 2400, out _));
            Assert.True(limits.Raw(68, 1876, out milli));
            Assert.Equal(0, milli); // median of 2400, 2400, 1876
            Assert.True(limits.Raw(68, 1876, out milli));
            Assert.Equal(500, milli);
            // Not a sensor, or a zero sample.
            Assert.False(limits.Raw(1, 2400, out _));
            Assert.False(limits.Raw(69, 0, out _));
            Assert.Equal(82, Enumerable.Range(0, 132).Count(AjazzRawLimits.Physical));
            Assert.Equal(1289, AjazzRawLimits.InitialFull(121));
            Assert.Equal(1353, AjazzRawLimits.InitialFull(0));
        }

        [Fact]
        public void M484_Ajazz_SilentOrMissingRows_EndTheSession()
        {
            // A row silent over 100 ms, or missing 1200 ms after the start,
            // ends the session (irok_na87_backend.cpp:869-873).
            var (session, io, _) = M484(AjazzId);
            Assert.True(session.Start(io));
            var output = Keys();
            AnalogPollResult result = AnalogPollResult.Idle;
            int passes = 0;
            for (; passes < 100 && result != AnalogPollResult.Failed; passes++) result = session.Pass(io, output, null);
            Assert.Equal(AnalogPollResult.Failed, result);
            Assert.InRange(passes, 60, 62);

            (session, io, _) = M484(AjazzId);
            Assert.True(session.Start(io));
            for (int row = 0; row < 6; row++)
            {
                io.Inner.QueueInput(RawRow(row, _ => 2400));
                session.Pass(io, output, null);
            }
            Assert.True(session.AjazzConnected);
            result = AnalogPollResult.Idle;
            for (passes = 0; passes < 20 && result != AnalogPollResult.Failed; passes++) result = session.Pass(io, output, null);
            Assert.Equal(AnalogPollResult.Failed, result);
            Assert.InRange(passes, 5, 7);
            session.Stop(io);
            Assert.Equal(IrokM484Protocol.RawRowsOffRequest(), io.Inner.Writes("out")[^1]);
        }

        // ── Routes ──

        [Fact]
        public void Routes_HallJoysPriorityOrder_AndOpenModes()
        {
            // native_analog_backends.def:22-23, 29: fourth, fifth and ninth in
            // the release catalog.
            var all = MadlionsRoutes.All;
            Assert.Equal(new[] { AnalogKeyboardProtocol.Mad68ProR, AnalogKeyboardProtocol.AtkHex80, AnalogKeyboardProtocol.IrokNa87 },
                all.Select(r => r.Protocol).ToArray());
            Assert.All(all, r => Assert.False(r.Id.StartsWith("soup-", StringComparison.Ordinal)));
            Assert.All(all, r => Assert.True(r.Writable));
            Assert.All(all, r => Assert.False(r.Exclusive));
            Assert.Equal(new[] { 256, 128, 256 }, all.Select(r => r.InputBuffers).ToArray());

            var mad68 = Info(0x373B, 0x1109, 0x0001, 0x0000, 65, 65, version: 0x0102);
            Assert.True(all[0].Matches(mad68));
            Assert.False(all[1].Matches(mad68));
            Assert.False(all[2].Matches(mad68));
            Assert.Equal("MADLIONS MAD 68 Pro R", all[0].Name(mad68));
            Assert.IsType<Mad68ProRSession>(all[0].CreateSession(mad68));

            var hex = Info(0x373B, 0x1177, 0xFF60, 0x0061, 33, 33);
            Assert.False(all[0].Matches(hex));
            Assert.True(all[1].Matches(hex));
            Assert.IsType<AtkHex80Session>(all[1].CreateSession(hex));

            var m484 = Info(0x0416, 0x7372, 0xFF1B, 0x0091, 64, 64);
            Assert.True(all[2].Matches(m484));
            Assert.Null(all[2].Name(m484));
            Assert.Equal(90, all[2].Keys(m484).Length);
            Assert.IsType<IrokM484Session>(all[2].CreateSession(m484));
        }
    }
}
