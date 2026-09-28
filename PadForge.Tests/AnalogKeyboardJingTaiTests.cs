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
    /// HallJoy's 5C-frame routes on usage page FFA0 (issue #468): the
    /// SparkPlayJoy RM 6x21 route (AULA and GravaStar), the JingTai V1 route
    /// (IROK MG75 Pro and the pinned-map models) and the Chilkey Slice75 HE
    /// route. No such keyboard is on the bench, so every behavior is pinned
    /// against HallJoy's own fixtures (the oracle frames of
    /// tests/aula_win60he_oracle_fixtures.h and the firmware frames of
    /// tests/mg75_pro_protocol_test.cpp) and against scripted keyboards that
    /// answer the way those fixtures do. HallJoy paths below are relative to
    /// src/HallJoyProject/.
    /// </summary>
    public class AnalogKeyboardJingTaiTests
    {
        // ── Shared helpers ──────────────────────────────────────────────────

        private static byte[] Bytes(params int[] values) => values.Select(v => (byte)v).ToArray();

        /// <summary>A request as the transport receives it: report ID 0, the
        /// protocol bytes, zero-padded to 65.</summary>
        private static byte[] Buffer65(params int[] protocol)
        {
            var r = new byte[65];
            for (int i = 0; i < protocol.Length; i++) r[1 + i] = (byte)protocol[i];
            return r;
        }

        /// <summary>A frame split into 65-byte reports the way the firmware
        /// sends it: report ID 0, then 64 frame bytes per report, the tail of
        /// the last report filled with <paramref name="fill"/>.</summary>
        private static List<byte[]> Split(byte[] frame, byte fill = 0)
        {
            int count = (frame.Length + 63) / 64;
            var reports = new List<byte[]>();
            for (int k = 0; k < count; k++)
            {
                var r = new byte[65];
                for (int i = 1; i < 65; i++) r[i] = fill;
                int take = Math.Min(64, frame.Length - k * 64);
                Array.Copy(frame, k * 64, r, 1, take);
                reports.Add(r);
            }
            return reports;
        }

        /// <summary>An answer frame: 5C, length, command | 80, checksum,
        /// payload.</summary>
        private static byte[] Answer(byte command, byte[] payload)
        {
            var frame = new byte[4 + payload.Length];
            frame[0] = 0x5C;
            frame[1] = (byte)payload.Length;
            frame[2] = (byte)(command | 0x80);
            Array.Copy(payload, 0, frame, 4, payload.Length);
            frame[3] = JingTaiFrames.Checksum(frame);
            return frame;
        }

        /// <summary>A controllable clock for the sessions.</summary>
        private sealed class FakeClock
        {
            public long Now = 1000;
            public long Read() => Now;
        }

        private static AnalogKeyInputState Keys() => new();

        // ── JingTai V1 frame (mg75_pro_protocol.h) ──────────────────────────

        [Fact]
        public void JingTai_RequestFrames_MatchHallJoysBuilders()
        {
            // mg75_pro_protocol_test.cpp:62-64 pins Travel(1) with checksum A6.
            Assert.Equal(Buffer65(0x5C, 0x04, 0x12, 0xA6, 0x02, 0x01, 0xFF, 0xFF), JingTaiFrames.Travel(1));
            Assert.Equal(Buffer65(0x5C, 0x04, 0x12, 0xA6, 0x02, 0x02, 0xFF, 0xFF), JingTaiFrames.Travel(2));
            // mg75_pro_protocol_test.cpp:65: any other half is an empty report.
            Assert.Equal(new byte[65], JingTaiFrames.Travel(0));
            Assert.Equal(new byte[65], JingTaiFrames.Travel(3));
            // Factory rows, checksums C0 C2 C4 (mg75_pro_protocol.h:70-73).
            Assert.Equal(Buffer65(0x5C, 0x03, 0x2B, 0xC0, 0x00, 0x00, 0x01), JingTaiFrames.Factory(0));
            Assert.Equal(Buffer65(0x5C, 0x03, 0x2B, 0xC2, 0x00, 0x02, 0x03), JingTaiFrames.Factory(2));
            Assert.Equal(Buffer65(0x5C, 0x03, 0x2B, 0xC4, 0x00, 0x04, 0x05), JingTaiFrames.Factory(4));
            // A payload over 60 bytes does not fit (mg75_pro_protocol.h:44-45).
            Assert.Equal(new byte[65], JingTaiFrames.Request(0x12, new byte[61]));
        }

        [Fact]
        public void JingTai_LayoutRequest_IsTheCompiledSlice75FirstBatch()
        {
            // The first 0x23 request HallJoy sends a Slice75, as the extraction
            // harness printed it from the real builder (mg75_pro_protocol.h:60-69).
            var expected = Buffer65(0x5C, 0x39, 0x23, 0xED, 0x00,
                0x29, 0, 0, 0, 0x3A, 0, 0, 0, 0x3B, 0, 0, 0, 0x3C, 0, 0, 0, 0x3D, 0, 0, 0,
                0x3E, 0, 0, 0, 0x3F, 0, 0, 0, 0x40, 0, 0, 0, 0x41, 0, 0, 0, 0x42, 0, 0, 0,
                0x43, 0, 0, 0, 0x44, 0, 0, 0, 0x45, 0, 0, 0, 0x49, 0, 0, 0);
            var selectors = Bytes(0x29, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x49);
            Assert.Equal(expected, JingTaiFrames.Layout(selectors));
            // The last payload byte is always 0, so the checksum is always ED.
            Assert.Equal(0xED, JingTaiFrames.Layout(new byte[14])[4]);
        }

        /// <summary>The 132-byte travel answer HallJoy captured by running the
        /// MG75 Pro 1.1.0 serializer with 63 depths i * 53
        /// (mg75_pro_protocol_test.cpp:76-87).</summary>
        private static readonly byte[] FirmwareTravel = Bytes(
            92, 128, 146, 175, 0, 2, 0, 0, 53, 0, 106, 0, 159, 0, 212,
            0, 9, 1, 62, 1, 115, 1, 168, 1, 221, 1, 18, 2, 71, 2,
            124, 2, 177, 2, 230, 2, 27, 3, 80, 3, 133, 3, 186, 3, 239,
            3, 36, 4, 89, 4, 142, 4, 195, 4, 248, 4, 45, 5, 98, 5,
            151, 5, 204, 5, 1, 6, 54, 6, 107, 6, 160, 6, 213, 6, 10,
            7, 63, 7, 116, 7, 169, 7, 222, 7, 19, 8, 72, 8, 125, 8,
            178, 8, 231, 8, 28, 9, 81, 9, 134, 9, 187, 9, 240, 9, 37,
            10, 90, 10, 143, 10, 196, 10, 249, 10, 46, 11, 99, 11, 152, 11,
            205, 11, 2, 12, 55, 12, 108, 12, 161, 12, 214, 12);

        [Fact]
        public void JingTai_Frame_ReassemblesTheFirmwareTravelAcrossThreeReports()
        {
            // mg75_pro_protocol_test.cpp:8-20 and 88-92: three reports, the
            // padding past the frame ignored, complete only after the third.
            Assert.Equal(132, FirmwareTravel.Length);
            var frame = new JingTaiFrame();
            var reports = Split(FirmwareTravel, fill: 0xEE);
            Assert.Equal(3, reports.Count);
            for (int part = 0; part < 3; part++)
            {
                Assert.True(frame.Push(reports[part], 65, 0x12));
                Assert.Equal(part == 2, frame.Complete);
            }
            var values = new ushort[63];
            Assert.True(JingTaiFrames.ParseTravel(frame, values));
            for (int i = 0; i < 63; i++) Assert.Equal(i * 53, values[i]);
        }

        [Fact]
        public void JingTai_Frame_RejectsBadChecksumPartialAndMalformedReports()
        {
            var values = new ushort[63];
            // mg75_pro_protocol_test.cpp:93-103: a flipped checksum fails when
            // the frame completes, not before.
            var bad = (byte[])FirmwareTravel.Clone();
            bad[3] ^= 1;
            var invalid = new JingTaiFrame();
            var parts = Split(bad);
            Assert.True(invalid.Push(parts[0], 65, 0x12));
            Assert.True(invalid.Push(parts[1], 65, 0x12));
            Assert.False(invalid.Push(parts[2], 65, 0x12));
            Assert.False(JingTaiFrames.ParseTravel(invalid, values));

            // mg75_pro_protocol_test.cpp:104-108: one report of three.
            var partial = new JingTaiFrame();
            Assert.True(partial.Push(Split(FirmwareTravel)[0], 65, 0x12));
            Assert.False(partial.Complete);
            Assert.False(JingTaiFrames.ParseTravel(partial, values));

            // mg75_pro_protocol_test.cpp:109-117: 64 bytes, report ID 1, and a
            // reply to another command all fail.
            var first = Split(FirmwareTravel)[0];
            Assert.False(new JingTaiFrame().Push(first, 64, 0x12));
            var id1 = (byte[])first.Clone();
            id1[0] = 1;
            Assert.False(new JingTaiFrame().Push(id1, 65, 0x12));
            var other = (byte[])first.Clone();
            other[3] = 0xA3;
            Assert.False(new JingTaiFrame().Push(other, 65, 0x12));

            // mg75_pro_protocol.h:94: a whole frame needs status 0.
            var status = (byte[])FirmwareTravel.Clone();
            status[4] = 1;
            var failed = new JingTaiFrame();
            foreach (var r in Split(status)) failed.Push(r, 65, 0x12);
            Assert.False(failed.Complete);
        }

        [Fact]
        public void JingTai_Travel_ValueOver6000_RejectsTheHalf()
        {
            // mg75_pro_protocol.h:107. Value 10 sits at frame bytes 26 and 27,
            // which the checksum does not cover.
            var frame6000 = (byte[])FirmwareTravel.Clone();
            frame6000[26] = 6000 & 0xFF;
            frame6000[27] = 6000 >> 8;
            var f = new JingTaiFrame();
            foreach (var r in Split(frame6000)) Assert.True(f.Push(r, 65, 0x12));
            var values = new ushort[63];
            Assert.True(JingTaiFrames.ParseTravel(f, values));
            Assert.Equal(6000, values[10]);

            var frame6001 = (byte[])frame6000.Clone();
            frame6001[26] = 6001 & 0xFF;
            var g = new JingTaiFrame();
            foreach (var r in Split(frame6001)) Assert.True(g.Push(r, 65, 0x12));
            var untouched = new ushort[63];
            Assert.False(JingTaiFrames.ParseTravel(g, untouched));
            Assert.All(untouched, v => Assert.Equal(0, v));
        }

        /// <summary>The six 0x23 answers of MG75 Pro firmware 1.1.0, with the
        /// selectors each answers (mg75_pro_protocol_test.cpp:120-242). The
        /// last carries Fn as 01 00 01 F0 and unused records FF FF FF 00.</summary>
        private static readonly (byte[] Keys, byte[] Frame)[] FirmwareLayouts =
        {
            (Bytes(41, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70), Bytes(
                92, 57, 163, 109, 0, 41, 0, 41, 0, 58, 0, 58, 0, 59, 0, 59,
                0, 60, 0, 60, 0, 61, 0, 61, 0, 62, 0, 62, 0, 63, 0, 63,
                0, 64, 0, 64, 0, 65, 0, 65, 0, 66, 0, 66, 0, 67, 0, 67,
                0, 68, 0, 68, 0, 69, 0, 69, 0, 70, 0, 70, 0)),
            (Bytes(76, 53, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 45, 46), Bytes(
                92, 57, 163, 109, 0, 76, 0, 76, 0, 53, 0, 53, 0, 30, 0, 30,
                0, 31, 0, 31, 0, 32, 0, 32, 0, 33, 0, 33, 0, 34, 0, 34,
                0, 35, 0, 35, 0, 36, 0, 36, 0, 37, 0, 37, 0, 38, 0, 38,
                0, 39, 0, 39, 0, 45, 0, 45, 0, 46, 0, 46, 0)),
            (Bytes(42, 73, 43, 20, 26, 8, 21, 23, 28, 24, 12, 18, 19, 47), Bytes(
                92, 57, 163, 109, 0, 42, 0, 42, 0, 73, 0, 73, 0, 43, 0, 43,
                0, 20, 0, 20, 0, 26, 0, 26, 0, 8, 0, 8, 0, 21, 0, 21,
                0, 23, 0, 23, 0, 28, 0, 28, 0, 24, 0, 24, 0, 12, 0, 12,
                0, 18, 0, 18, 0, 19, 0, 19, 0, 47, 0, 47, 0)),
            (Bytes(48, 49, 75, 57, 4, 22, 7, 9, 10, 11, 13, 14, 15, 51), Bytes(
                92, 57, 163, 109, 0, 48, 0, 48, 0, 49, 0, 49, 0, 75, 0, 75,
                0, 57, 0, 57, 0, 4, 0, 4, 0, 22, 0, 22, 0, 7, 0, 7,
                0, 9, 0, 9, 0, 10, 0, 10, 0, 11, 0, 11, 0, 13, 0, 13,
                0, 14, 0, 14, 0, 15, 0, 15, 0, 51, 0, 51, 0)),
            (Bytes(52, 40, 78, 225, 29, 27, 6, 25, 5, 17, 16, 54, 55, 56), Bytes(
                92, 57, 163, 109, 0, 52, 0, 52, 0, 40, 0, 40, 0, 78, 0, 78,
                0, 225, 0, 225, 0, 29, 0, 29, 0, 27, 0, 27, 0, 6, 0, 6,
                0, 25, 0, 25, 0, 5, 0, 5, 0, 17, 0, 17, 0, 16, 0, 16,
                0, 54, 0, 54, 0, 55, 0, 55, 0, 56, 0, 56, 0)),
            (Bytes(229, 82, 224, 227, 226, 44, 230, 1, 80, 81, 79, 0, 0, 0), Bytes(
                92, 57, 163, 109, 0, 229, 0, 229, 0, 82, 0, 82, 0,
                224, 0, 224, 0, 227, 0, 227, 0, 226, 0, 226, 0, 44,
                0, 44, 0, 230, 0, 230, 0, 1, 0, 1, 240, 80, 0,
                80, 0, 81, 0, 81, 0, 79, 0, 79, 0, 255, 255, 255,
                0, 255, 255, 255, 0, 255, 255, 255, 0)),
        };

        /// <summary>The three 0x2B answers of the same firmware
        /// (mg75_pro_protocol_test.cpp:243-276).</summary>
        private static readonly byte[][] FirmwareFactory =
        {
            Bytes(92, 45, 171, 105, 0, 0, 41, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67,
                68, 69, 70, 76, 0, 0, 0, 0, 0, 0, 1, 53, 30, 31, 32, 33, 34,
                35, 36, 37, 38, 39, 45, 46, 42, 73, 0, 0, 0, 0, 0, 0),
            Bytes(92, 45, 171, 105, 0, 2, 43, 20, 26, 8, 21, 23, 28, 24, 12, 18, 19,
                47, 48, 49, 75, 0, 0, 0, 0, 0, 0, 3, 57, 4, 22, 7, 9, 10,
                11, 13, 14, 15, 51, 52, 0, 40, 78, 0, 0, 0, 0, 0, 0),
            Bytes(92, 45, 171, 105, 0, 4, 225, 0, 29, 27, 6, 25, 5,
                17, 16, 54, 55, 56, 0, 229, 82, 0, 0, 0, 0, 0,
                0, 5, 224, 227, 226, 0, 0, 0, 44, 0, 0, 0, 230,
                1, 80, 81, 79, 0, 0, 0, 0, 0, 0),
        };

        private static JingTaiFrame Frame(byte[] bytes, byte command)
        {
            var f = new JingTaiFrame();
            foreach (var r in Split(bytes)) f.Push(r, 65, command);
            return f;
        }

        [Fact]
        public void JingTai_LayoutAnswers_FromFirmware_ParseAndCheckEachEcho()
        {
            // mg75_pro_protocol_test.cpp:120-242: every record decodes, Fn
            // (selector 1) as 0x409, and a changed selector fails the answer.
            foreach (var (keys, bytes) in FirmwareLayouts)
            {
                var f = Frame(bytes, 0x23);
                Assert.True(f.Complete);
                var assigned = new int[14];
                Assert.True(JingTaiFrames.ParseLayout(f, keys, assigned));
                for (int i = 0; i < 14; i++)
                    if (keys[i] != 0) Assert.Equal(keys[i] == 1 ? AnalogKeyCodes.Fn : keys[i], assigned[i]);
                var wrong = (byte[])keys.Clone();
                wrong[0] = 255;
                Assert.False(JingTaiFrames.ParseLayout(f, wrong, assigned));
            }
        }

        [Fact]
        public void JingTai_FactoryAnswers_FromFirmware_MatchTheMg75ProTable()
        {
            // mg75_pro_protocol_test.cpp:243-276, MatchFactory at
            // mg75_pro_protocol.h:130-139.
            var table = JingTaiRoutes.JingTaiModels[0].Table;
            for (int k = 0; k < 3; k++)
                Assert.True(JingTaiFrames.MatchFactory(Frame(FirmwareFactory[k], 0x2B), k * 2, table));
            // The wrong row, and one changed selector, both fail.
            Assert.False(JingTaiFrames.MatchFactory(Frame(FirmwareFactory[0], 0x2B), 2, table));
            var changed = (byte[])FirmwareFactory[1].Clone();
            changed[10] ^= 1;
            changed[3] = JingTaiFrames.Checksum(changed);
            Assert.False(JingTaiFrames.MatchFactory(Frame(changed, 0x2B), 2, table));
            // Only rows 0, 2 and 4 exist.
            Assert.False(JingTaiFrames.MatchFactory(Frame(FirmwareFactory[0], 0x2B), 1, table));
        }

        [Fact]
        public void JingTai_NormalizeAndDecode_FollowHallJoy()
        {
            // mg75_pro_protocol_test.cpp:74-75.
            Assert.Equal(0, JingTaiFrames.Normalize(0, 3500));
            Assert.Equal(500, JingTaiFrames.Normalize(1750, 3500));
            Assert.Equal(1000, JingTaiFrames.Normalize(3500, 3500));
            // The extraction harness's dumped samples (mg75_pro_protocol.h:140-144).
            Assert.Equal(0, JingTaiFrames.Normalize(1, 3500));
            Assert.Equal(1, JingTaiFrames.Normalize(2, 3500));
            Assert.Equal(438, JingTaiFrames.Normalize(1750, 4000));
            Assert.Equal(486, JingTaiFrames.Normalize(1750, 3600));
            // mg75_pro_protocol_test.cpp:28-31, for every model range.
            foreach (var model in JingTaiRoutes.JingTaiModels)
            {
                Assert.Equal(500, JingTaiFrames.Normalize(model.Range / 2, model.Range));
                Assert.Equal(1000, JingTaiFrames.Normalize(model.Range, model.Range));
                Assert.Equal(1000, JingTaiFrames.Normalize(model.Range + 100, model.Range));
            }
            // three_keyboard_protocol_test.cpp:63-64, Slice75's 3300 range.
            Assert.Equal(1000, JingTaiFrames.Normalize(3300, 3300));
            Assert.Equal(500, JingTaiFrames.Normalize(1650, 3300));
            // mg75_pro_protocol_test.cpp:72-73 and mg75_pro_protocol.h:25-27.
            Assert.Equal(AnalogKeyCodes.Fn, JingTaiFrames.Decode(0xF001));
            Assert.Equal(0xE1, JingTaiFrames.Decode(0xE1));
            Assert.Equal(0, JingTaiFrames.Decode(0x100));
            Assert.Equal(0, JingTaiFrames.Decode(0xF101));
            Assert.Equal(1, JingTaiFrames.Selector(AnalogKeyCodes.Fn));
            Assert.Equal(0x29, JingTaiFrames.Selector(0x29));
        }

        [Fact]
        public void WriteDepths_MergesAliasesByMaximum_AndKeepsTheDeepestPastCapacity()
        {
            // Aliases read their largest depth (physical_analog_state.h:39-52,
            // aula_win60he_client.cpp:640-666). HallJoy publishes every key.
            // PadForge's state holds 64, so past that the deepest are kept.
            var codes = new int[126];
            var milli = new int[126];
            for (int p = 0; p < 70; p++)
            {
                codes[p] = 4 + p;      // codes 4 to 73
                milli[p] = 1 + p;      // depths 1 to 70
            }
            codes[100] = 4;            // an alias of code 4, deeper
            milli[100] = 900;
            codes[101] = 0;            // no key
            milli[101] = 500;
            codes[102] = 0x80;         // a key at rest
            milli[102] = 0;
            var output = Keys();
            output.Set(AnalogKeyCodes.LCtrl, 1f); // outside 4 to 73: replaced, not merged
            JingTaiFrames.WriteDepths(codes, milli, output);
            Assert.Equal(AnalogKeyInputState.MaxKeys, output.Count);
            Assert.Equal(0.9f, output.Get(4));
            for (int code = 5; code <= 10; code++) Assert.Equal(0f, output.Get(code));
            Assert.Equal(0.008f, output.Get(11));
            Assert.Equal(0.07f, output.Get(73));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.LCtrl));
            Assert.Equal(0f, output.Get(0x80));

            // Under capacity every key is written, whatever its position.
            var few = Keys();
            JingTaiFrames.WriteDepths(new[] { 0, AnalogKeyCodes.W, 0, AnalogKeyCodes.W }, new[] { 0, 250, 0, 750 }, few);
            Assert.Equal(1, few.Count);
            Assert.Equal(0.75f, few.Get(AnalogKeyCodes.W));
        }

        [Fact]
        public void Slice75_TravelNeedsAllThreeReports_IncludingTheFourByteTail()
        {
            // three_keyboard_protocol_test.cpp:56-64: value 62 (3300) sits in
            // the four bytes the third report carries.
            var raw = new byte[132];
            raw[0] = 0x5C;
            raw[1] = 128;
            raw[2] = 0x92;
            raw[5] = 2;
            raw[130] = 0xE4;
            raw[131] = 0x0C;
            raw[3] = JingTaiFrames.Checksum(raw);
            var frame = new JingTaiFrame();
            var reports = Split(raw);
            for (int part = 0; part < 3; part++)
            {
                Assert.True(frame.Push(reports[part], 65, 0x12));
                Assert.Equal(part == 2, frame.Complete);
            }
            var v = new ushort[63];
            Assert.True(JingTaiFrames.ParseTravel(frame, v));
            Assert.Equal(3300, v[62]);
            Assert.Equal(1000, JingTaiFrames.Normalize(v[62], JingTaiRoutes.Slice75Model.Range));
        }

        // ── Tables and identities ───────────────────────────────────────────

        [Fact]
        public void JingTai_ModelTables_HaveHallJoysCountsFnSlotsAndUniqueKeys()
        {
            // Counts and ranges: jingtai_v1_profiles.h:6-12,
            // mg75_pro_backend.cpp:31, slice75_protocol.h:13, 17-28.
            var expected = new Dictionary<string, (int Count, int Fn, int Range, string Name)>
            {
                ["MG75PRO-1CA5-0807"] = (81, 116, 3500, "IROK MG75 Pro"),
                ["JT1-K"] = (87, 116, 4000, "IROK NA87 Pro"),
                ["JT1-LE"] = (68, 115, 4000, "IYX MU68 Pro"),
                ["JT1-WE"] = (64, 115, 4000, "IROK ND63"),
                ["JT1-IE"] = (68, 115, 3500, "CAROTMAS Mercury68"),
                ["JT1-TE"] = (68, 115, 3500, "CAROTMAS Mercury68 Pro"),
                ["JT1-LE-CYAN"] = (68, 115, 3600, "IYX MU68 Pro"),
                ["JT1-WE-CYAN"] = (64, 115, 3600, "IROK ND63"),
            };
            Assert.Equal(expected.Count, JingTaiRoutes.JingTaiModels.Count);
            foreach (var model in JingTaiRoutes.JingTaiModels)
            {
                var (count, fn, range, name) = expected[model.Identity];
                Assert.Equal(126, model.Table.Length);
                Assert.Equal(count, model.Count);
                Assert.Equal(count, model.Table.Count(c => c != 0));
                Assert.Equal(fn, Array.IndexOf(model.Table, AnalogKeyCodes.Fn));
                Assert.Equal(range, model.Range);
                Assert.Equal(name, model.Name);
                Assert.Equal(model.Identity == "MG75PRO-1CA5-0807", model.FactoryProof);
                // InstallMap and native_layout::Publish (native_layout_state.h:38-40).
                Assert.True(JingTaiFrames.IsInstallable(model.Table, model.Count));
            }
            var slice = JingTaiRoutes.Slice75Model;
            Assert.Equal(80, slice.Table.Count(c => c != 0));
            Assert.Equal(116, Array.IndexOf(slice.Table, AnalogKeyCodes.Fn));
            Assert.Equal(3300, slice.Range);
            Assert.True(slice.FactoryProof);
            Assert.True(JingTaiFrames.IsInstallable(slice.Table, 80));

            // Spot keys at their slots.
            var mg75 = JingTaiRoutes.JingTaiModels[0].Table;
            Assert.Equal(AnalogKeyCodes.Escape, mg75[0]);
            Assert.Equal(AnalogKeyCodes.PrintScreen, mg75[13]);
            Assert.Equal(AnalogKeyCodes.Delete, mg75[14]);
            Assert.Equal(AnalogKeyCodes.W, mg75[44]);
            Assert.Equal(AnalogKeyCodes.Space, mg75[111]);
            Assert.Equal(AnalogKeyCodes.RAlt, mg75[115]);
            var na87 = JingTaiRoutes.JingTaiModels.First(m => m.Identity == "JT1-K").Table;
            Assert.Equal(AnalogKeyCodes.ContextMenu, na87[117]);
            Assert.Equal(AnalogKeyCodes.RCtrl, na87[118]);
            Assert.Equal(AnalogKeyCodes.Insert, slice.Table[13]);
            // An installable table never holds a code twice.
            var twice = (int[])mg75.Clone();
            twice[15] = AnalogKeyCodes.Escape;
            Assert.False(JingTaiFrames.IsInstallable(twice, 82));
            Assert.False(JingTaiFrames.IsInstallable(mg75, 80));
        }

        [Fact]
        public void JingTai_Identities_AreTheTwentyNineExactTuples()
        {
            // mg75_pro_protocol_test.cpp:24-33 and 66-67.
            Assert.Equal(29, JingTaiRoutes.JingTaiIdentities.Count);
            foreach (var identity in JingTaiRoutes.JingTaiIdentities)
            {
                Assert.Same(identity.Model,
                    JingTaiRoutes.FindJingTaiModel(identity.VendorId, identity.ProductId, identity.Product));
                Assert.Null(JingTaiRoutes.FindJingTaiModel(identity.VendorId, identity.ProductId, "keyboard"));
                Assert.Null(JingTaiRoutes.FindJingTaiModel(0xFFFF, identity.ProductId, identity.Product));
            }
            Assert.Equal("MG75PRO-1CA5-0807", JingTaiRoutes.FindJingTaiModel(0x1CA5, 0x0807, "IROK MG75 PRO").Identity);
            Assert.Null(JingTaiRoutes.FindJingTaiModel(0x1CA5, 0x0807, "IROK MG75"));
            // Trailing white space trimmed and ASCII upper-cased
            // (mg75_pro_backend.cpp:198-203).
            Assert.NotNull(JingTaiRoutes.FindJingTaiModel(0x1CA5, 0x0807, "irok mg75 pro  "));
            // Cyan and TTC names select the 3600 um table (jingtai_v1_profiles.h:11-12).
            Assert.Equal(3600, JingTaiRoutes.FindJingTaiModel(0x1CA2, 0x0406, "IROK ND63 TTC").Range);
            Assert.Equal(3600, JingTaiRoutes.FindJingTaiModel(0x1C4F, 0xEE88, "IYX MU68 PRO CYAN").Range);
            Assert.Equal(4000, JingTaiRoutes.FindJingTaiModel(0x1C4F, 0xEE88, "IYX MU68 PRO").Range);
            // A product string on the wrong USB identity is no identity.
            Assert.Null(JingTaiRoutes.FindJingTaiModel(0x1CA5, 0x0807, "IROK NA87 PRO"));
            Assert.Null(JingTaiRoutes.FindJingTaiModel(0x1CA2, 0x0401, "IYX MU68 PRO"));
        }

        // ── JingTai and Slice75 Matches ─────────────────────────────────────

        private static AnalogKeyboardDeviceInfo JingTaiInfo(ushort vid, ushort pid, string product)
            => new()
            {
                Path = $@"\\?\hid#vid_{vid:x4}&pid_{pid:x4}&mi_02#8&2a1c&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
                VendorId = vid,
                ProductId = pid,
                UsagePage = 0xFFA0,
                Usage = 0x0001,
                InterfaceNumber = 2,
                InputReportLength = 65,
                OutputReportLength = 65,
                ProductString = product,
                HasInputReport = id => id == 0,
                HasOutputReport = id => id == 0,
            };

        [Fact]
        public void JingTaiV1_Matches_EveryIdentity_AndRejectsNearMisses()
        {
            // mg75_pro_backend.cpp:167-207.
            foreach (var identity in JingTaiRoutes.JingTaiIdentities)
                Assert.True(JingTaiRoutes.JingTaiV1.Matches(JingTaiInfo(identity.VendorId, identity.ProductId, identity.Product)));

            var good = JingTaiInfo(0x1CA5, 0x0807, "IROK MG75 PRO");
            Assert.True(JingTaiRoutes.JingTaiV1.Matches(good));
            AnalogKeyboardDeviceInfo With(Action<AnalogKeyboardDeviceInfo> change)
            {
                var info = JingTaiInfo(0x1CA5, 0x0807, "IROK MG75 PRO");
                change(info);
                return info;
            }
            Assert.False(JingTaiRoutes.JingTaiV1.Matches(With(i => i.UsagePage = 0xFF60)));
            Assert.False(JingTaiRoutes.JingTaiV1.Matches(With(i => i.Usage = 0x0002)));
            Assert.False(JingTaiRoutes.JingTaiV1.Matches(With(i => i.InputReportLength = 64)));
            Assert.False(JingTaiRoutes.JingTaiV1.Matches(With(i => i.OutputReportLength = 33)));
            // Numbered reports are not the JingTai collection (mg75_pro_backend.cpp:113-133).
            Assert.False(JingTaiRoutes.JingTaiV1.Matches(With(i => i.HasInputReport = id => id == 1)));
            Assert.False(JingTaiRoutes.JingTaiV1.Matches(With(i => i.HasOutputReport = id => id == 1)));
            Assert.False(JingTaiRoutes.JingTaiV1.Matches(With(i => i.ProductId = 0x0808)));
            Assert.False(JingTaiRoutes.JingTaiV1.Matches(With(i => i.ProductString = "IROK MG75")));
            Assert.False(JingTaiRoutes.JingTaiV1.Matches(With(i => i.ProductString = string.Empty)));
            // The Slice75 is not a JingTai V1 identity.
            Assert.False(JingTaiRoutes.JingTaiV1.Matches(JingTaiInfo(0x1CA3, 0x0701, "SLICE75 HE")));
        }

        [Fact]
        public void Slice75_Matches_TheExactIdentity_AndRejectsNearMisses()
        {
            // slice75_backend.cpp:164-198, three_keyboard_protocol_test.cpp:67-68.
            Assert.True(JingTaiRoutes.ChilkeySlice75.Matches(JingTaiInfo(0x1CA3, 0x0701, "SLICE75 HE")));
            Assert.True(JingTaiRoutes.ChilkeySlice75.Matches(JingTaiInfo(0x1CA3, 0x0701, "Slice75 HE ")));
            Assert.False(JingTaiRoutes.ChilkeySlice75.Matches(JingTaiInfo(0x1CA3, 0x0703, "SLICE75 HE")));
            Assert.False(JingTaiRoutes.ChilkeySlice75.Matches(JingTaiInfo(0x1CA3, 0x0701, "SLICE68 HE")));
            var usage = JingTaiInfo(0x1CA3, 0x0701, "SLICE75 HE");
            usage.Usage = 0x0061;
            Assert.False(JingTaiRoutes.ChilkeySlice75.Matches(usage));
            Assert.False(JingTaiRoutes.ChilkeySlice75.Matches(JingTaiInfo(0x1CA5, 0x0807, "IROK MG75 PRO")));
        }

        // ── JingTai scripted keyboard ───────────────────────────────────────

        /// <summary>A JingTai V1 keyboard that answers the way the firmware
        /// fixtures show: factory rows by selector, travel halves from a slot
        /// array, and 0x23 records echoing each selector with its action
        /// (01 00 01 F0 for Fn) and FF FF FF 00 where none was asked.</summary>
        private sealed class JingTaiKeyboard
        {
            public int[] Table;
            public readonly ushort[] Travel = new ushort[126];
            public readonly Dictionary<byte, int> Actions = new();
            public Func<byte[], List<byte[]>> Override;

            public JingTaiKeyboard(int[] table) => Table = table;

            public IEnumerable<byte[]> Respond(byte[] request)
            {
                var special = Override?.Invoke(request);
                if (special != null) return special;
                switch (request[3])
                {
                    case 0x2B:
                    {
                        int row = request[6];
                        var p = new byte[45];
                        p[1] = (byte)row;
                        p[23] = (byte)(row + 1);
                        for (int c = 0; c < 21; c++)
                        {
                            p[2 + c] = JingTaiFrames.Selector(Table[row * 21 + c]);
                            p[24 + c] = JingTaiFrames.Selector(Table[(row + 1) * 21 + c]);
                        }
                        return Split(Answer(0x2B, p));
                    }
                    case 0x12:
                    {
                        int half = request[6];
                        var p = new byte[128];
                        p[1] = 2;
                        for (int i = 0; i < 63; i++)
                        {
                            ushort v = Travel[(half - 1) * 63 + i];
                            p[2 + 2 * i] = (byte)v;
                            p[3 + 2 * i] = (byte)(v >> 8);
                        }
                        return Split(Answer(0x12, p));
                    }
                    case 0x23:
                    {
                        var p = new byte[57];
                        for (int i = 0; i < 14; i++)
                        {
                            byte sel = request[6 + 4 * i];
                            if (sel == 0)
                            {
                                p[1 + 4 * i] = 0xFF;
                                p[2 + 4 * i] = 0xFF;
                                p[3 + 4 * i] = 0xFF;
                                continue;
                            }
                            int action = Actions.TryGetValue(sel, out int a) ? a : sel == 1 ? 0xF001 : sel;
                            p[1 + 4 * i] = sel;
                            p[3 + 4 * i] = (byte)action;
                            p[4 + 4 * i] = (byte)(action >> 8);
                        }
                        return Split(Answer(0x23, p));
                    }
                }
                return Array.Empty<byte[]>();
            }
        }

        private static (JingTaiSession Session, AnalogKeyboardTestTransport Io, JingTaiKeyboard Keyboard, FakeClock Clock)
            StartedJingTai(JingTaiModel model, Action<JingTaiKeyboard> setup = null, List<int> pauses = null)
        {
            var keyboard = new JingTaiKeyboard(model.Table);
            setup?.Invoke(keyboard);
            var io = new AnalogKeyboardTestTransport { OnSend = keyboard.Respond };
            var clock = new FakeClock();
            var session = new JingTaiSession(model, clock.Read, ms => pauses?.Add(ms));
            Assert.True(session.Start(io));
            return (session, io, keyboard, clock);
        }

        [Fact]
        public void Mg75Pro_Start_WithTheFirmwareFrames_ProvesAndReadsEveryAssignment()
        {
            // Proof then map (mg75_pro_backend.cpp:275-345): three factory
            // reads, both halves, six 0x23 reads of 14, 14, 14, 14, 14 and 11
            // keys. Answers are HallJoy's firmware fixtures, byte for byte.
            var model = JingTaiRoutes.JingTaiModels[0];
            int layout = 0;
            var io = new AnalogKeyboardTestTransport
            {
                OnSend = request => request[3] switch
                {
                    0x2B => Split(FirmwareFactory[request[6] / 2]),
                    0x12 => Split(FirmwareTravel),
                    0x23 => Split(FirmwareLayouts[layout++].Frame),
                    _ => new List<byte[]>(),
                },
            };
            var session = new JingTaiSession(model, new FakeClock().Read, _ => { });
            Assert.True(session.Start(io));

            var writes = io.Writes("out");
            Assert.Equal(11, writes.Count);
            Assert.Equal(JingTaiFrames.Factory(0), writes[0]);
            Assert.Equal(JingTaiFrames.Factory(2), writes[1]);
            Assert.Equal(JingTaiFrames.Factory(4), writes[2]);
            Assert.Equal(JingTaiFrames.Travel(1), writes[3]);
            Assert.Equal(JingTaiFrames.Travel(2), writes[4]);
            for (int b = 0; b < 6; b++)
                Assert.Equal(JingTaiFrames.Layout(FirmwareLayouts[b].Keys), writes[5 + b]);
            // HallJoy flushes once, at open (mg75_pro_backend.cpp:229), which
            // the channel does. No exchange flushes again.
            Assert.Equal(0, io.Discards);
            Assert.Equal("IROK MG75 Pro", session.ModelName);
            Assert.Equal(81, session.KeyOrder.Length);
            Assert.Equal(AnalogKeyCodes.Escape, session.KeyOrder[0]);
            Assert.Contains(AnalogKeyCodes.Fn, session.KeyOrder);
            Assert.Equal(AnalogKeyCodes.Fn, session.Assigned[116]);
            Assert.Equal(AnalogKeyCodes.W, session.Assigned[44]);
        }

        [Fact]
        public void JingTai_KeysPublish_UnderTheKeyboardsOwnAssignments()
        {
            // HallJoy reads the assigned channel while its automatic layout
            // remaps, its default (mg75_pro_backend.cpp:291-315, 527-535): a
            // key remapped on the keyboard moves the key it now types, and a
            // key assigned nothing publishes nothing. NA87 Pro's range is
            // 4000, so W's slot (44, half 1) at 2000 is 500.
            var model = JingTaiRoutes.JingTaiModels.First(m => m.Identity == "JT1-K");
            var (session, io, _, _) = StartedJingTai(model, k =>
            {
                k.Actions[JingTaiFrames.Selector(model.Table[44])] = AnalogKeyCodes.Q;
                k.Actions[JingTaiFrames.Selector(model.Table[111])] = 0;
                k.Travel[44] = 2000;
                k.Travel[111] = 4000;
            });
            Assert.Equal(AnalogKeyCodes.Q, session.PublicationMap[44]);
            Assert.Equal(0, session.PublicationMap[111]);
            Assert.DoesNotContain(AnalogKeyCodes.Space, session.KeyOrder);
            var output = new AnalogKeyInputState();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.Q));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.Space));
        }

        [Fact]
        public void JingTai_PinnedMapModels_SkipTheFactoryReads()
        {
            // mg75_pro_backend.cpp:276-282: only the MG75 Pro reads 0x2B. Read
            // counts are ceil(keys / 14): 7 for 87 keys, 5 for 68 and 64.
            foreach (var (identity, layouts) in new[] { ("JT1-K", 7), ("JT1-LE", 5), ("JT1-WE", 5), ("JT1-IE", 5), ("JT1-TE", 5) })
            {
                var model = JingTaiRoutes.JingTaiModels.First(m => m.Identity == identity);
                var (_, io, _, _) = StartedJingTai(model);
                var writes = io.Writes("out");
                Assert.DoesNotContain(writes, w => w[3] == 0x2B);
                Assert.Equal(2 + layouts, writes.Count);
                Assert.Equal(JingTaiFrames.Travel(1), writes[0]);
                Assert.Equal(JingTaiFrames.Travel(2), writes[1]);
                Assert.All(writes.Skip(2), w => Assert.Equal(0x23, w[3]));
            }
        }

        [Fact]
        public void JingTai_Pass_AlternatesHalves_NormalizesByModelRange_AndPausesBetween()
        {
            // mg75_pro_backend.cpp:389-415 and 346-359. NA87 Pro's range is
            // 4000: W (slot 44, half 1) at 2000 is 500. Space (slot 111, half
            // 2 value 48) at 4000 is 1000. Fn (slot 116) at 1000 is 250.
            var model = JingTaiRoutes.JingTaiModels.First(m => m.Identity == "JT1-K");
            Assert.Equal(AnalogKeyCodes.W, model.Table[44]);
            Assert.Equal(AnalogKeyCodes.Space, model.Table[111]);
            var pauses = new List<int>();
            var (session, io, keyboard, _) = StartedJingTai(model, k =>
            {
                k.Travel[44] = 2000;
                k.Travel[111] = 4000;
                k.Travel[116] = 1000;
            }, pauses);
            int before = io.Writes("out").Count;

            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.Space)); // half 2 not read yet
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.Space));
            Assert.Equal(0.25f, output.Get(AnalogKeyCodes.Fn));
            Assert.Equal(3, output.Count);
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));

            var writes = io.Writes("out").Skip(before).ToList();
            Assert.Equal(new[] { JingTaiFrames.Travel(1), JingTaiFrames.Travel(2), JingTaiFrames.Travel(1) }, writes);
            // A 1 ms wait between exchanges, none before the first pass.
            Assert.Equal(new[] { 1, 1 }, pauses);

            // A released key leaves the output.
            keyboard.Travel[44] = 0;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null)); // half 2
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null)); // half 1
            Assert.Equal(0f, output.Get(AnalogKeyCodes.W));
        }

        [Fact]
        public void JingTai_Pass_AValueNotRefreshedWithin150ms_ReadsZero()
        {
            // mg75_pro_backend.cpp:34 and 530-535, physical_analog_state.h:45:
            // fresh while now - stamp <= 150.
            var model = JingTaiRoutes.JingTaiModels[0];
            var (session, io, _, clock) = StartedJingTai(model, k =>
            {
                k.Travel[44] = 1750; // W, half 1
                k.Travel[111] = 3500; // Space, half 2
            });
            var output = Keys();
            clock.Now = 1000;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null)); // half 1 at 1000
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
            clock.Now = 1150;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null)); // half 2 at 1150
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W)); // 150 ms old: still fresh
            Assert.Equal(1f, output.Get(AnalogKeyCodes.Space));
            clock.Now = 1301;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null)); // half 1 at 1301
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.Space)); // 151 ms old: reads 0
        }

        [Fact]
        public void JingTai_Pass_EveryKeyDown_PublishesTheDeepestSixtyFour()
        {
            // The NA87 Pro reports 87 keys (jingtai_v1_profiles.h:6). With
            // every one down at a different depth, the 64 deepest are written.
            var model = JingTaiRoutes.JingTaiModels.First(m => m.Identity == "JT1-K");
            var occupied = Enumerable.Range(0, 126).Where(s => model.Table[s] != 0).ToArray();
            Assert.Equal(87, occupied.Length);
            var (session, io, _, _) = StartedJingTai(model, k =>
            {
                for (int n = 0; n < occupied.Length; n++) k.Travel[occupied[n]] = (ushort)(100 + 40 * n);
            });
            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(AnalogKeyInputState.MaxKeys, output.Count);
            for (int n = 0; n < occupied.Length; n++)
            {
                float expected = n < 87 - 64 ? 0f : JingTaiFrames.Normalize(100 + 40 * n, 4000) / 1000f;
                Assert.Equal(expected, output.Get(model.Table[occupied[n]]));
            }
        }

        [Fact]
        public void JingTai_Pass_HalfOver6000_EndsTheSession_AndNothingMoreIsSent()
        {
            // mg75_pro_backend.cpp:392-395 with mg75_pro_protocol.h:107.
            var model = JingTaiRoutes.JingTaiModels[0];
            var (session, io, keyboard, _) = StartedJingTai(model);
            keyboard.Travel[0] = 6001;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, Keys(), null));
            Assert.True(session.Poisoned);
            int writes = io.Writes("out").Count;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, Keys(), null));
            Assert.Equal(writes, io.Writes("out").Count);
        }

        [Fact]
        public void JingTai_Pass_NoAnswer_Or_GoneDevice_Fails()
        {
            // A late exchange poisons the session (mg75_pro_protocol.h:74-75).
            var model = JingTaiRoutes.JingTaiModels[0];
            var (session, io, keyboard, _) = StartedJingTai(model);
            keyboard.Override = _ => new List<byte[]>();
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, Keys(), null));

            var (gone, goneIo, _, _) = StartedJingTai(model);
            goneIo.Gone = true;
            Assert.Equal(AnalogPollResult.Failed, gone.Pass(goneIo, Keys(), null));
        }

        [Fact]
        public void JingTai_Start_RejectsFactoryMismatch_BadEcho_Timeout_SlowWrite_AndGoneDevice()
        {
            var mg75 = JingTaiRoutes.JingTaiModels[0];

            // A factory row that differs from the pinned table
            // (mg75_pro_backend.cpp:278-282): rejected after the second read.
            var keyboard = new JingTaiKeyboard(mg75.Table);
            keyboard.Override = r =>
            {
                if (r[3] != 0x2B || r[6] != 2) return null;
                var frame = (byte[])FirmwareFactory[1].Clone();
                frame[6] = 0x2A;
                frame[3] = JingTaiFrames.Checksum(frame);
                return Split(frame);
            };
            var io = new AnalogKeyboardTestTransport { OnSend = keyboard.Respond };
            Assert.False(new JingTaiSession(mg75, new FakeClock().Read, _ => { }).Start(io));
            Assert.Equal(2, io.Writes("out").Count);

            // A 0x23 record echoing another selector (mg75_pro_protocol.h:122-123).
            var echo = new JingTaiKeyboard(mg75.Table);
            echo.Override = r =>
            {
                if (r[3] != 0x23) return null;
                var p = new byte[57];
                for (int i = 0; i < 14; i++)
                {
                    p[1 + 4 * i] = (byte)(r[6 + 4 * i] + 1);
                    p[3 + 4 * i] = r[6 + 4 * i];
                }
                return Split(Answer(0x23, p));
            };
            io = new AnalogKeyboardTestTransport { OnSend = echo.Respond };
            Assert.False(new JingTaiSession(mg75, new FakeClock().Read, _ => { }).Start(io));

            // No answer at all.
            io = new AnalogKeyboardTestTransport();
            var silent = new JingTaiSession(mg75, new FakeClock().Read, _ => { });
            Assert.False(silent.Start(io));
            Assert.Single(io.Writes("out"));
            Assert.True(silent.Poisoned);

            // A write over 50 ms fails (mg75_pro_backend.cpp:240-243). One of 50 ms passes.
            foreach (var (took, ok) in new[] { (51L, false), (50L, true) })
            {
                var clock = new FakeClock();
                var slow = new JingTaiKeyboard(mg75.Table);
                io = new AnalogKeyboardTestTransport
                {
                    OnSend = r =>
                    {
                        clock.Now += took;
                        return slow.Respond(r);
                    },
                };
                Assert.Equal(ok, new JingTaiSession(mg75, clock.Read, _ => { }).Start(io));
            }

            // Gone.
            io = new AnalogKeyboardTestTransport { Gone = true };
            Assert.False(new JingTaiSession(mg75, new FakeClock().Read, _ => { }).Start(io));
        }

        /// <summary>The scripted transport with a clock that moves on every
        /// read, for the answer deadline.</summary>
        private sealed class SlowReads : IAnalogKeyboardTransport
        {
            private readonly AnalogKeyboardTestTransport _inner;
            private readonly FakeClock _clock;
            private readonly long _step;

            public SlowReads(AnalogKeyboardTestTransport inner, FakeClock clock, long step)
            {
                _inner = inner;
                _clock = clock;
                _step = step;
            }

            public bool Send(byte[] report) => _inner.Send(report);
            public bool SendOutputReport(byte[] report) => _inner.SendOutputReport(report);
            public int Receive(byte[] buffer, int timeoutMs)
            {
                _clock.Now += _step;
                return _inner.Receive(buffer, timeoutMs);
            }
            public void DiscardStale() => _inner.DiscardStale();
            public bool SetFeature(byte[] report) => _inner.SetFeature(report);
            public int GetFeature(byte[] buffer) => _inner.GetFeature(buffer);
            public int InputLength => _inner.InputLength;
            public int OutputLength => _inner.OutputLength;
            public int FeatureLength => _inner.FeatureLength;
        }

        [Fact]
        public void JingTai_Exchange_AnswerMustCompleteWithin120ms()
        {
            // mg75_pro_backend.cpp:245-249: the deadline counts from the end of
            // the write and covers every report. With 61 ms per read the third
            // travel report would land at 183 ms, so the exchange fails.
            var model = JingTaiRoutes.JingTaiModels.First(m => m.Identity == "JT1-K");
            var keyboard = new JingTaiKeyboard(model.Table);
            var clock = new FakeClock();
            var io = new SlowReads(new AnalogKeyboardTestTransport { OnSend = keyboard.Respond }, clock, 61);
            var session = new JingTaiSession(model, clock.Read, _ => { });
            Assert.False(session.Start(io));
            Assert.True(session.Poisoned);

            // 40 ms per read completes the three reports within 120 ms.
            clock = new FakeClock();
            io = new SlowReads(new AnalogKeyboardTestTransport { OnSend = keyboard.Respond }, clock, 40);
            Assert.True(new JingTaiSession(model, clock.Read, _ => { }).Start(io));
        }

        [Fact]
        public void Slice75_Start_ProvesTheFactoryMatrix_AndReadsEightyKeys()
        {
            // slice75_backend.cpp:266-327: three factory reads, both halves,
            // six 0x23 reads (14 x 5 and 10).
            var model = JingTaiRoutes.Slice75Model;
            var (session, io, keyboard, _) = StartedJingTai(model, k => k.Travel[44] = 1650);
            var writes = io.Writes("out");
            Assert.Equal(3 + 2 + 6, writes.Count);
            Assert.Equal(JingTaiFrames.Factory(0), writes[0]);
            Assert.Equal(JingTaiFrames.Travel(2), writes[4]);
            Assert.Equal(Buffer65(0x5C, 0x39, 0x23, 0xED, 0x00,
                0x29, 0, 0, 0, 0x3A, 0, 0, 0, 0x3B, 0, 0, 0, 0x3C, 0, 0, 0, 0x3D, 0, 0, 0,
                0x3E, 0, 0, 0, 0x3F, 0, 0, 0, 0x40, 0, 0, 0, 0x41, 0, 0, 0, 0x42, 0, 0, 0,
                0x43, 0, 0, 0, 0x44, 0, 0, 0, 0x45, 0, 0, 0, 0x49, 0, 0, 0), writes[5]);
            // The last read carries ten selectors, the rest 0.
            Assert.Equal(10, Enumerable.Range(0, 14).Count(i => writes[10][6 + 4 * i] != 0));
            Assert.Equal("Chilkey Slice75 HE", session.ModelName);
            Assert.Equal(80, session.KeyOrder.Length);

            // Slice75's 3300 range: 1650 is 500 (slice75_protocol.h:58-61).
            Assert.Equal(AnalogKeyCodes.W, model.Table[44]);
            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
        }

        [Fact]
        public void JingTai_And_Slice75_Stop_SendNothing()
        {
            // mg75_pro_backend.cpp:493-519, slice75_backend.cpp:473-499: stop
            // cancels I/O and sends nothing, as the route changed nothing.
            foreach (var model in new[] { JingTaiRoutes.JingTaiModels[0], JingTaiRoutes.Slice75Model })
            {
                var (session, io, _, _) = StartedJingTai(model);
                int writes = io.Log.Count;
                session.Pass(io, Keys(), null);
                int afterPass = io.Log.Count;
                session.Stop(io);
                Assert.Equal(afterPass, io.Log.Count);
                Assert.True(afterPass > writes);
            }
        }

        // ── RM 6x21: oracle fixtures (tests/aula_win60he_oracle_fixtures.h) ─

        private static readonly byte[] OracleRequestSync = Bytes(0x5C, 0x06, 0x01, 0x97, 0x01, 0x02, 0x03, 0x04, 0xFF, 0xFF);
        private static readonly byte[] OracleRequestPrecision = Bytes(0x5C, 0x03, 0x00, 0x93, 0x25, 0xFF, 0xFF);
        private static readonly byte[][] OracleRequestDefault =
        {
            Bytes(0x5C, 0x03, 0x2B, 0xC0, 0x00, 0x00, 0x01),
            Bytes(0x5C, 0x03, 0x2B, 0xC2, 0x00, 0x02, 0x03),
            Bytes(0x5C, 0x03, 0x2B, 0xC4, 0x00, 0x04, 0x05),
        };

        /// <summary>kActiveQueries: the WIN 60 HE MAX's five Fn0 batches, the
        /// last padded with its final identifier 01.</summary>
        private static readonly byte[][] OracleActiveQueries =
        {
            Bytes(0x29, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x2D, 0x2E, 0x2A),
            Bytes(0x2B, 0x14, 0x1A, 0x08, 0x15, 0x17, 0x1C, 0x18, 0x0C, 0x12, 0x13, 0x2F, 0x30, 0x31),
            Bytes(0x39, 0x04, 0x16, 0x07, 0x09, 0x0A, 0x0B, 0x0D, 0x0E, 0x0F, 0x33, 0x34, 0x28, 0xE1),
            Bytes(0x1D, 0x1B, 0x06, 0x19, 0x05, 0x11, 0x10, 0x36, 0x37, 0x38, 0xE5, 0xE0, 0xE3, 0xE2),
            Bytes(0x2C, 0xE6, 0x65, 0xE4, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01),
        };

        private static readonly byte[] OracleRequestTravel1 = Bytes(0x5C, 0x04, 0x12, 0xA6, 0x02, 0x01, 0xFF, 0xFF);
        private static readonly byte[] OracleRequestTravel2 = Bytes(0x5C, 0x04, 0x12, 0xA6, 0x02, 0x02, 0xFF, 0xFF);

        /// <summary>kResponseSync: the physical WIN 60 HE MAX answer from
        /// HallJoy (3).log, serial redacted to zeros.</summary>
        private static readonly byte[] OracleSync = Bytes(
            0x5C, 0x3C, 0x81, 0x4D, 0x00, 0x02, 0x19, 0x02, 0x0A, 0xC0, 0x01, 0x00,
            0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x41, 0x70, 0x70, 0x20, 0x56, 0x31,
            0x2E, 0x31, 0x2E, 0x36, 0x00, 0x00, 0x31, 0x36, 0x3A, 0x33, 0x10, 0x46,
            0x65, 0x62, 0x20, 0x20, 0x34, 0x20, 0x36, 0x33, 0x32, 0x30, 0x38, 0xFC,
            0x01, 0x03, 0x12, 0xFF);

        private static readonly byte[] OraclePrecision = Bytes(0x5C, 0x07, 0x80, 0x25, 0x00, 0x25, 0x0A, 0x0A, 0x00, 0x48, 0x0D);

        private static readonly byte[][] OracleDefault =
        {
            Bytes(0x5C, 0x2D, 0xAB, 0x69, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x01, 0x29, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24,
                0x25, 0x26, 0x27, 0x2D, 0x2E, 0x2A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00),
            Bytes(0x5C, 0x2D, 0xAB, 0x69, 0x00, 0x02, 0x2B, 0x14, 0x1A, 0x08, 0x15, 0x17,
                0x1C, 0x18, 0x0C, 0x12, 0x13, 0x2F, 0x30, 0x31, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x03, 0x39, 0x04, 0x16, 0x07, 0x09, 0x0A, 0x0B, 0x0D,
                0x0E, 0x0F, 0x33, 0x34, 0x00, 0x28, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00),
            Bytes(0x5C, 0x2D, 0xAB, 0x69, 0x00, 0x04, 0xE1, 0x00, 0x1D, 0x1B, 0x06, 0x19,
                0x05, 0x11, 0x10, 0x36, 0x37, 0x38, 0xE5, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x05, 0xE0, 0xE3, 0xE2, 0x00, 0x00, 0x00, 0x2C, 0x00,
                0x00, 0xE6, 0x65, 0xE4, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00),
        };

        /// <summary>kResponseActiveBatch0..4: the oracle's active map remaps
        /// Esc to Up Arrow (52) and disables A, and Fn reads F001.</summary>
        private static readonly byte[][] OracleActive =
        {
            Bytes(0x5C, 0x39, 0xA3, 0x6D, 0x00, 0x29, 0x00, 0x52, 0x00, 0x1E, 0x00, 0x1E,
                0x00, 0x1F, 0x00, 0x1F, 0x00, 0x20, 0x00, 0x20, 0x00, 0x21, 0x00, 0x21,
                0x00, 0x22, 0x00, 0x22, 0x00, 0x23, 0x00, 0x23, 0x00, 0x24, 0x00, 0x24,
                0x00, 0x25, 0x00, 0x25, 0x00, 0x26, 0x00, 0x26, 0x00, 0x27, 0x00, 0x27,
                0x00, 0x2D, 0x00, 0x2D, 0x00, 0x2E, 0x00, 0x2E, 0x00, 0x2A, 0x00, 0x2A, 0x00),
            Bytes(0x5C, 0x39, 0xA3, 0x6D, 0x00, 0x2B, 0x00, 0x2B, 0x00, 0x14, 0x00, 0x14,
                0x00, 0x1A, 0x00, 0x1A, 0x00, 0x08, 0x00, 0x08, 0x00, 0x15, 0x00, 0x15,
                0x00, 0x17, 0x00, 0x17, 0x00, 0x1C, 0x00, 0x1C, 0x00, 0x18, 0x00, 0x18,
                0x00, 0x0C, 0x00, 0x0C, 0x00, 0x12, 0x00, 0x12, 0x00, 0x13, 0x00, 0x13,
                0x00, 0x2F, 0x00, 0x2F, 0x00, 0x30, 0x00, 0x30, 0x00, 0x31, 0x00, 0x31, 0x00),
            Bytes(0x5C, 0x39, 0xA3, 0x6D, 0x00, 0x39, 0x00, 0x39, 0x00, 0x04, 0x00, 0x00,
                0x00, 0x16, 0x00, 0x16, 0x00, 0x07, 0x00, 0x07, 0x00, 0x09, 0x00, 0x09,
                0x00, 0x0A, 0x00, 0x0A, 0x00, 0x0B, 0x00, 0x0B, 0x00, 0x0D, 0x00, 0x0D,
                0x00, 0x0E, 0x00, 0x0E, 0x00, 0x0F, 0x00, 0x0F, 0x00, 0x33, 0x00, 0x33,
                0x00, 0x34, 0x00, 0x34, 0x00, 0x28, 0x00, 0x28, 0x00, 0xE1, 0x00, 0xE1, 0x00),
            Bytes(0x5C, 0x39, 0xA3, 0x6D, 0x00, 0x1D, 0x00, 0x1D, 0x00, 0x1B, 0x00, 0x1B,
                0x00, 0x06, 0x00, 0x06, 0x00, 0x19, 0x00, 0x19, 0x00, 0x05, 0x00, 0x05,
                0x00, 0x11, 0x00, 0x11, 0x00, 0x10, 0x00, 0x10, 0x00, 0x36, 0x00, 0x36,
                0x00, 0x37, 0x00, 0x37, 0x00, 0x38, 0x00, 0x38, 0x00, 0xE5, 0x00, 0xE5,
                0x00, 0xE0, 0x00, 0xE0, 0x00, 0xE3, 0x00, 0xE3, 0x00, 0xE2, 0x00, 0xE2, 0x00),
            Bytes(0x5C, 0x39, 0xA3, 0x5D, 0x00, 0x2C, 0x00, 0x2C, 0x00, 0xE6, 0x00, 0xE6,
                0x00, 0x65, 0x00, 0x65, 0x00, 0xE4, 0x00, 0xE4, 0x00, 0x01, 0x00, 0x01,
                0xF0, 0x01, 0x00, 0x01, 0xF0, 0x01, 0x00, 0x01, 0xF0, 0x01, 0x00, 0x01,
                0xF0, 0x01, 0x00, 0x01, 0xF0, 0x01, 0x00, 0x01, 0xF0, 0x01, 0x00, 0x01,
                0xF0, 0x01, 0x00, 0x01, 0xF0, 0x01, 0x00, 0x01, 0xF0, 0x01, 0x00, 0x01, 0xF0),
        };

        /// <summary>kResponseTravel: 63 values 7 + 53k, no half echo.</summary>
        private static readonly byte[] OracleTravel = Bytes(
            0x5C, 0x80, 0x92, 0xAF, 0x00, 0x02, 0x07, 0x00, 0x3C, 0x00, 0x71, 0x00,
            0xA6, 0x00, 0xDB, 0x00, 0x10, 0x01, 0x45, 0x01, 0x7A, 0x01, 0xAF, 0x01,
            0xE4, 0x01, 0x19, 0x02, 0x4E, 0x02, 0x83, 0x02, 0xB8, 0x02, 0xED, 0x02,
            0x22, 0x03, 0x57, 0x03, 0x8C, 0x03, 0xC1, 0x03, 0xF6, 0x03, 0x2B, 0x04,
            0x60, 0x04, 0x95, 0x04, 0xCA, 0x04, 0xFF, 0x04, 0x34, 0x05, 0x69, 0x05,
            0x9E, 0x05, 0xD3, 0x05, 0x08, 0x06, 0x3D, 0x06, 0x72, 0x06, 0xA7, 0x06,
            0xDC, 0x06, 0x11, 0x07, 0x46, 0x07, 0x7B, 0x07, 0xB0, 0x07, 0xE5, 0x07,
            0x1A, 0x08, 0x4F, 0x08, 0x84, 0x08, 0xB9, 0x08, 0xEE, 0x08, 0x23, 0x09,
            0x58, 0x09, 0x8D, 0x09, 0xC2, 0x09, 0xF7, 0x09, 0x2C, 0x0A, 0x61, 0x0A,
            0x96, 0x0A, 0xCB, 0x0A, 0x00, 0x0B, 0x35, 0x0B, 0x6A, 0x0B, 0x9F, 0x0B,
            0xD4, 0x0B, 0x09, 0x0C, 0x3E, 0x0C, 0x73, 0x0C, 0xA8, 0x0C, 0xDD, 0x0C);

        /// <summary>A keyboard answering with the oracle frames, byte for byte.</summary>
        private static AnalogKeyboardTestTransport OracleKeyboard()
            => new()
            {
                OnSend = request =>
                {
                    switch (request[3])
                    {
                        case 0x01: return Split(OracleSync);
                        case 0x00: return Split(OraclePrecision);
                        case 0x2B: return Split(OracleDefault[request[6] / 2]);
                        case 0x23:
                            for (int b = 0; b < OracleActiveQueries.Length; b++)
                                if (Enumerable.Range(0, 14).All(i => request[6 + 4 * i] == OracleActiveQueries[b][i]))
                                    return Split(OracleActive[b]);
                            return new List<byte[]>();
                        case 0x12: return Split(OracleTravel);
                    }
                    return new List<byte[]>();
                },
            };

        /// <summary>An oracle request in the 65-byte buffer the transport
        /// writes: report ID 0 in front (aula_win60he_protocol.cpp:128-142).</summary>
        private static byte[] Oracle(byte[] protocol) => Buffer65(protocol.Select(b => (int)b).ToArray());

        /// <summary>kRequestActiveBatchN: 5C 39 23 ED 00, then each queried
        /// key followed by 00 00 00 (aula_win60he_oracle_fixtures.h:38-81).</summary>
        private static byte[] OracleActiveRequest(byte[] query)
        {
            var bytes = new List<int> { 0x5C, 0x39, 0x23, 0xED, 0x00 };
            foreach (byte key in query) bytes.AddRange(new[] { (int)key, 0, 0, 0 });
            return Buffer65(bytes.ToArray());
        }

        [Fact]
        public void AulaRm_RequestFrames_MatchHallJoysOracle()
        {
            // aula_win60he_oracle_test.cpp:38-63 and aula_win60he_protocol_test.cpp:136-187.
            Assert.Equal(Oracle(OracleRequestSync), AulaRmProtocol.SyncRequest());
            Assert.Equal(Oracle(OracleRequestPrecision), AulaRmProtocol.PrecisionRequest());
            for (int k = 0; k < 3; k++)
                Assert.Equal(Oracle(OracleRequestDefault[k]), AulaRmProtocol.DefaultKeyRequest(k * 2, k * 2 + 1));
            Assert.Equal(Oracle(OracleRequestTravel1), AulaRmProtocol.TravelRequest(1));
            Assert.Equal(Oracle(OracleRequestTravel2), AulaRmProtocol.TravelRequest(2));
            for (int b = 0; b < OracleActiveQueries.Length; b++)
                Assert.Equal(OracleActiveRequest(OracleActiveQueries[b]),
                    AulaRmProtocol.KeyFunctionRequest(OracleActiveQueries[b], AulaRmProtocol.LayoutFn0));
            // With layout Fn0 the RM key-function read is the JingTai layout read.
            Assert.Equal(JingTaiFrames.Layout(OracleActiveQueries[4]),
                AulaRmProtocol.KeyFunctionRequest(OracleActiveQueries[4], AulaRmProtocol.LayoutFn0));
        }

        [Fact]
        public void AulaRm_Win60HeMax_ProvesWithTheOracle_AndPublishesTheActiveMap()
        {
            // Client::Probe (aula_win60he_client.cpp:255-400) on the oracle:
            // 17 transactions, each after a flush (aula_win60he_client.cpp:234-242).
            var io = OracleKeyboard();
            var session = new AulaRmSession(0x1CA2, 0x1902, "WIN 60 HE MAX", new FakeClock().Read, _ => { });
            Assert.True(session.Start(io));

            var writes = io.Writes("out");
            Assert.Equal(17, writes.Count);
            Assert.Equal(17, io.Discards);
            var expected = new List<byte[]> { Oracle(OracleRequestSync), Oracle(OracleRequestPrecision) };
            expected.AddRange(OracleRequestDefault.Select(Oracle));
            for (int generation = 0; generation < 2; generation++)
                expected.AddRange(OracleActiveQueries.Select(OracleActiveRequest));
            expected.Add(Oracle(OracleRequestTravel1));
            expected.Add(Oracle(OracleRequestTravel2));
            Assert.Equal(expected, writes);

            Assert.Equal(0x0A021902u, session.BoardId);
            Assert.Equal(10, session.PrecisionUm);
            Assert.Equal(10, session.MinimumTravelUm);
            Assert.Equal(3400, session.MaximumTravelUm);
            Assert.Equal("Aula WIN 60 HE MAX", session.ModelName);
            // kOracleActiveMappedKeyCodes (aula_win60he_oracle_fixtures.h:16).
            Assert.Equal(60, session.MappedKeys);
            // The active Fn0 map names the keys, HallJoy's publication while
            // its automatic layout remaps (aula_win60he_backend.cpp:2204-2206,
            // tests/aula_win60he_end_to_end_test.cpp:629): its 60 codes, Fn
            // among them. The oracle's active map assigns Esc's position the
            // Up arrow (kResponseActiveBatch0, aula_win60he_oracle_fixtures.h:160-161),
            // assigns Esc nowhere, and disables A.
            Assert.Equal(60, session.KeyOrder.Length);
            Assert.Equal(AnalogKeyCodes.ArrowUp, session.KeyOrder[0]);
            Assert.Contains(AnalogKeyCodes.Fn, session.KeyOrder);
            Assert.DoesNotContain(AnalogKeyCodes.Escape, session.KeyOrder);
            Assert.DoesNotContain(AnalogKeyCodes.A, session.KeyOrder);

            // One pass reads both halves. Esc's position (row 1, column 0,
            // half 1 value 21) travels 1120 um of 3400: 329, published as Up.
            // A (row 3, column 1, half 2 value 1) travels 60, and its position
            // publishes nothing because the active map disables it. Fn (row 5,
            // column 12, half 2 value 54) travels 2869: 844.
            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(new[] { Oracle(OracleRequestTravel1), Oracle(OracleRequestTravel2) }, io.Writes("out").Skip(17));
            Assert.Equal(0.329f, output.Get(AnalogKeyCodes.ArrowUp));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.Escape));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(0.844f, output.Get(AnalogKeyCodes.Fn));
            Assert.Equal(60, output.Count);
        }

        [Fact]
        public void AulaRm_Win60HePro_IsNamedByItsExactProductString()
        {
            // aula_win60he_backend.cpp:1570-1571: the product string must be
            // exactly "WIN 60 HE PRO", case included.
            var pro = new AulaRmSession(0x1CA2, 0x1902, "WIN 60 HE PRO", new FakeClock().Read, _ => { });
            Assert.True(pro.Start(OracleKeyboard()));
            Assert.Equal("Aula WIN 60 HE PRO", pro.ModelName);
            var lower = new AulaRmSession(0x1CA2, 0x1902, "win 60 he pro", new FakeClock().Read, _ => { });
            Assert.True(lower.Start(OracleKeyboard()));
            Assert.Equal("Aula WIN 60 HE MAX", lower.ModelName);
        }

        // ── RM 6x21: scripted keyboard ──────────────────────────────────────

        /// <summary>The WIN 60 HE MAX default map, row-major
        /// (aula_win60he_protocol.cpp:12-19).</summary>
        private static byte[] MaxDefaultMap()
        {
            var map = new byte[126];
            for (int k = 0; k < 3; k++)
            {
                var f = OracleDefault[k];
                for (int c = 0; c < 21; c++)
                {
                    map[k * 2 * 21 + c] = f[6 + c];
                    map[(k * 2 + 1) * 21 + c] = f[28 + c];
                }
            }
            return map;
        }

        /// <summary>The physical V75 map HallJoy's end-to-end test models
        /// (aula_win60he_end_to_end_test.cpp:236-243): 79 positions, Fn 01 at
        /// row 5, column 11.</summary>
        private static byte[] V75DefaultMap()
        {
            int[][] rows =
            {
                new[] { 0x29, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0, 0, 0, 0, 0, 0, 0, 0 },
                new[] { 0x35, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x2D, 0x2E, 0x2A, 0x4C, 0, 0, 0, 0, 0, 0 },
                new[] { 0x2B, 0x14, 0x1A, 0x08, 0x15, 0x17, 0x1C, 0x18, 0x0C, 0x12, 0x13, 0x2F, 0x30, 0x31, 0x4B, 0, 0, 0, 0, 0, 0 },
                new[] { 0x39, 0x04, 0x16, 0x07, 0x09, 0x0A, 0x0B, 0x0D, 0x0E, 0x0F, 0x33, 0x34, 0, 0x28, 0x4E, 0, 0, 0, 0, 0, 0 },
                new[] { 0xE1, 0, 0x1D, 0x1B, 0x06, 0x19, 0x05, 0x11, 0x10, 0x36, 0x37, 0x38, 0, 0xE5, 0x52, 0, 0, 0, 0, 0, 0 },
                new[] { 0xE0, 0xE3, 0xE2, 0, 0, 0, 0x2C, 0, 0, 0, 0xE6, 0x01, 0x50, 0x51, 0x4F, 0, 0, 0, 0, 0, 0 },
            };
            return rows.SelectMany(r => r).Select(v => (byte)v).ToArray();
        }

        /// <summary>The physical V75 sync payload from HallJoy (9).log
        /// (aula_win60he_protocol_test.cpp:273-282): board 16052201, platform
        /// bytes 00 04 00, App V1.0.8.</summary>
        private static readonly byte[] V75Sync = Bytes(
            0x00, 0x01, 0x22, 0x05, 0x16, 0x00, 0x04, 0x00,
            0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x10, 0x41, 0x70, 0x70, 0x20, 0x56, 0x31,
            0x2E, 0x30, 0x2E, 0x38, 0x00, 0x00, 0xF1, 0xF7,
            0x16, 0xFF, 0x10, 0x4A, 0x61, 0x6E, 0x20, 0x20,
            0x34, 0x20, 0x36, 0x35, 0x39, 0x32, 0x35, 0x03,
            0xFF, 0x07, 0x16, 0xFF);

        /// <summary>An RM 6x21 keyboard answering every read the way
        /// HallJoy's FirmwareShapedTransport does
        /// (aula_win60he_end_to_end_test.cpp:39-400).</summary>
        private sealed class RmKeyboard
        {
            public byte[] Sync = OracleSync.Skip(4).Take(60).ToArray();
            public int Precision = 10, Minimum = 10, Maximum = 3400;
            public byte[] DefaultMap = MaxDefaultMap();
            public readonly Dictionary<byte, int> Functions = new();
            public readonly ushort[] Travel = new ushort[126];
            public int KeyFunctionReads;
            public Func<byte[], List<byte[]>> Override;
            /// <summary>(read number, key, function) to the function sent.</summary>
            public Func<int, byte, int, int> FunctionFilter;

            public IEnumerable<byte[]> Respond(byte[] request)
            {
                var special = Override?.Invoke(request);
                if (special != null) return special;
                byte command = request[3];
                switch (command)
                {
                    case 0x01:
                        return Split(Answer(0x01, Sync));
                    case 0x00:
                        return Split(Answer(0x00, Bytes(0, 0x25, Precision, Minimum & 0xFF, Minimum >> 8,
                            Maximum & 0xFF, Maximum >> 8)));
                    case 0x2B:
                    {
                        int first = request[6], second = request[7];
                        var p = new byte[45];
                        p[1] = (byte)first;
                        p[23] = (byte)second;
                        for (int c = 0; c < 21; c++)
                        {
                            p[2 + c] = DefaultMap[first * 21 + c];
                            p[24 + c] = DefaultMap[second * 21 + c];
                        }
                        return Split(Answer(0x2B, p));
                    }
                    case 0x23:
                    {
                        int read = KeyFunctionReads++;
                        var p = new byte[57];
                        for (int i = 0; i < 14; i++)
                        {
                            byte key = request[6 + 4 * i];
                            int function = Functions.TryGetValue(key, out int f) ? f : key == 1 ? 0xF001 : key;
                            if (FunctionFilter != null) function = FunctionFilter(read, key, function);
                            p[1 + 4 * i] = key;
                            p[2 + 4 * i] = request[7 + 4 * i];
                            p[3 + 4 * i] = (byte)function;
                            p[4 + 4 * i] = (byte)(function >> 8);
                        }
                        return Split(Answer(0x23, p));
                    }
                    case 0x12:
                    {
                        int half = request[6];
                        var p = new byte[128];
                        p[1] = 2;
                        for (int k = 0; k < 63; k++)
                        {
                            ushort v = Travel[(half - 1) * 63 + k];
                            p[2 + 2 * k] = (byte)v;
                            p[3 + 2 * k] = (byte)(v >> 8);
                        }
                        return Split(Answer(0x12, p));
                    }
                }
                return new List<byte[]>();
            }
        }

        private static (AulaRmSession Session, AnalogKeyboardTestTransport Io, RmKeyboard Keyboard, FakeClock Clock)
            RmSession(ushort vid, ushort pid, Action<RmKeyboard> setup = null, string product = "keyboard",
                List<int> pauses = null)
        {
            var keyboard = new RmKeyboard();
            setup?.Invoke(keyboard);
            var io = new AnalogKeyboardTestTransport { OnSend = keyboard.Respond };
            var clock = new FakeClock();
            var session = new AulaRmSession(vid, pid, product, clock.Read, ms => pauses?.Add(ms));
            return (session, io, keyboard, clock);
        }

        [Fact]
        public void AulaRm_ScriptedKeyboard_ReproducesTheOracleFrames()
        {
            // The scripted keyboard's answers equal HallJoy's oracle frames for
            // the MAX (aula_win60he_oracle_fixtures.h:123-203), so the tests
            // below speak the oracle's dialect.
            var keyboard = new RmKeyboard { Functions = { [0x29] = 0x52, [0x04] = 0x0000 } };
            Assert.Equal(Split(OracleSync), keyboard.Respond(AulaRmProtocol.SyncRequest()));
            Assert.Equal(Split(OraclePrecision), keyboard.Respond(AulaRmProtocol.PrecisionRequest()));
            for (int k = 0; k < 3; k++)
                Assert.Equal(Split(OracleDefault[k]), keyboard.Respond(AulaRmProtocol.DefaultKeyRequest(k * 2, k * 2 + 1)));
            for (int b = 0; b < 5; b++)
                Assert.Equal(Split(OracleActive[b]),
                    keyboard.Respond(AulaRmProtocol.KeyFunctionRequest(OracleActiveQueries[b], 0)));
        }

        [Fact]
        public void AulaRm_GravaStarV75_KnownBoardSkipsThePlatformBytes()
        {
            // aula_win60he_protocol.cpp:447-457: the physical V75 reports 00 04
            // 00 and is admitted by its board. 79 positions give six Fn0 reads
            // per generation, 19 transactions in all.
            var (session, io, keyboard, _) = RmSession(0x1CA5, 0x2201, k =>
            {
                k.Sync = V75Sync;
                k.Precision = 5;
                k.Minimum = 5;
                k.Maximum = 3500;
                k.DefaultMap = V75DefaultMap();
            });
            Assert.True(session.Start(io));
            Assert.Equal(19, io.Writes("out").Count);
            Assert.Equal(0x16052201u, session.BoardId);
            Assert.Equal("GravaStar Mercury V75", session.ModelName);
            Assert.Equal(79, session.KeyOrder.Length);
            Assert.Equal(AnalogKeyCodes.Fn, session.PublicationMap[5 * 21 + 11]);

            // 1750 um of 3500 is 500 (aula_win60he_protocol.cpp:597-609). A
            // sits at row 3, column 1, position 64: half 2, value 1.
            keyboard.Travel[3 * 21 + 1] = 1750;
            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(1, output.Count);
        }

        [Fact]
        public void AulaRm_UnlistedSibling_NeedsTheAulaPlatformBytes()
        {
            // aula_win60he_protocol.cpp:459-468 and
            // aula_win60he_protocol_test.cpp:285-292: the V75 sync is not a
            // sibling's, which must show C0 01 00.
            var (session, io, _, _) = RmSession(0x1CA2, 0x7777, k =>
            {
                k.Sync = V75Sync;
                k.Precision = 5;
                k.Minimum = 5;
                k.Maximum = 3500;
                k.DefaultMap = V75DefaultMap();
            });
            Assert.False(session.Start(io));
            Assert.Single(io.Writes("out"));
        }

        /// <summary>HallJoy's compatible-family profile
        /// (aula_win60he_end_to_end_test.cpp:205-227, 316-330): board
        /// 05771234, C0 01 00, App V2.0.0, 20/20/4000, identifiers 1 to 84,
        /// functions F001 for 1 to 3.</summary>
        private static void CompatibleSibling(RmKeyboard k)
        {
            var sync = OracleSync.Skip(4).Take(60).ToArray();
            sync[1] = 0x34;
            sync[2] = 0x12;
            sync[3] = 0x77;
            sync[4] = 0x05;
            Array.Clear(sync, 26, 10);
            System.Text.Encoding.ASCII.GetBytes("App V2.0.0").CopyTo(sync, 26);
            System.Text.Encoding.ASCII.GetBytes("Mar 15 ").CopyTo(sync, 43);
            k.Sync = sync;
            k.Precision = 20;
            k.Minimum = 20;
            k.Maximum = 4000;
            k.DefaultMap = new byte[126];
            for (int i = 0; i < 84; i++) k.DefaultMap[i] = (byte)(i + 1);
            for (byte key = 1; key <= 3; key++) k.Functions[key] = 0xF001;
        }

        [Fact]
        public void AulaRm_UnlistedSibling_PublishesTheActiveMap_TakingTheLargestAlias()
        {
            // No layout token, so the active Fn0 map names the keys
            // (aula_win60he_backend.cpp:2204-2206), and three positions whose
            // functions are all F001 publish Fn at their largest depth
            // (aula_win60he_client.cpp:640-666).
            var (session, io, keyboard, _) = RmSession(0x1CA2, 0x7777, CompatibleSibling);
            Assert.True(session.Start(io));
            // 84 identifiers: six Fn0 reads per generation, 19 transactions.
            Assert.Equal(19, io.Writes("out").Count);
            Assert.Null(session.ModelName);
            Assert.Equal(0x05771234u, session.BoardId);
            // Fn first (position 0), then usages 4 to 84.
            Assert.Equal(82, session.KeyOrder.Length);
            Assert.Equal(AnalogKeyCodes.Fn, session.KeyOrder[0]);

            keyboard.Travel[0] = 1000;
            keyboard.Travel[1] = 3000;
            keyboard.Travel[2] = 2000;
            keyboard.Travel[3] = 4000; // identifier 4, key A
            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.75f, output.Get(AnalogKeyCodes.Fn));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(2, output.Count);
        }

        [Fact]
        public void AulaRm_KnownBoard_ReturningAnotherBoard_IsRejectedAfterTheSync()
        {
            // aula_win60he_protocol_test.cpp:82-83: WIN 68 (1CA2:1901) with the
            // MAX's board 0A021902 is not compatible.
            var (session, io, _, _) = RmSession(0x1CA2, 0x1901);
            Assert.False(session.Start(io));
            Assert.Single(io.Writes("out"));
            Assert.True(session.Poisoned);
        }

        [Theory]
        [InlineData("app-version")]
        [InlineData("build-label")]
        [InlineData("serial-length")]
        [InlineData("trailer")]
        [InlineData("unprintable-version")]
        [InlineData("precision-499")]
        [InlineData("precision-zero")]
        [InlineData("minimum-over-maximum")]
        [InlineData("duplicate-identifier")]
        [InlineData("one-position")]
        public void AulaRm_Start_RejectsEverySemanticMismatch(string fault)
        {
            // Decoders and family predicates: aula_win60he_protocol.cpp:217-261,
            // 417-500. Each fault alone fails the proof.
            var (session, io, _, _) = RmSession(0x1CA2, 0x1902, k =>
            {
                switch (fault)
                {
                    case "app-version": k.Sync[26] = (byte)'B'; break;
                    case "build-label": k.Sync[43] = 0; break;
                    case "serial-length": k.Sync[8] = 0x0F; break;
                    case "trailer": k.Sync[59] = 0x00; break;
                    case "unprintable-version": k.Sync[33] = 0x7F; break;
                    case "precision-499": k.Maximum = 499; break;
                    case "precision-zero": k.Precision = 0; break;
                    case "minimum-over-maximum": k.Minimum = 3401; break;
                    case "duplicate-identifier": k.DefaultMap[1 * 21 + 1] = k.DefaultMap[1 * 21 + 0]; break;
                    case "one-position":
                        k.DefaultMap = new byte[126];
                        k.DefaultMap[21] = 0x29;
                        break;
                }
            });
            Assert.False(session.Start(io));
        }

        [Fact]
        public void AulaRm_Start_RejectsAnUnstableActiveMap_AndContradictoryPadding()
        {
            // aula_win60he_client.cpp:418-441: the two generations must agree.
            var (unstable, io, _, _) = RmSession(0x1CA2, 0x1902, k =>
                k.FunctionFilter = (read, key, f) => read == 5 && key == 0x29 ? 0x52 : f);
            Assert.False(unstable.Start(io));
            Assert.Equal(1 + 1 + 3 + 10, io.Writes("out").Count); // no travel read

            // aula_win60he_protocol.cpp:310-319 and aula_win60he_oracle_test.cpp:137-148:
            // every echo of the padding key must carry the same function.
            var (padding, io2, _, _) = RmSession(0x1CA2, 0x1902, k =>
                k.Override = r =>
                {
                    if (r[3] != 0x23 || r[6] != 0x2C) return null;
                    var frame = (byte[])OracleActive[4].Clone();
                    frame[4 + 1 + 13 * 4 + 2] ^= 0x01;
                    return Split(frame);
                });
            Assert.False(padding.Start(io2));
        }

        [Fact]
        public void AulaRm_AFailedProof_IsRetried_OnlyWhereHallJoyRetriesIt()
        {
            // IsDeterministicSemanticFailure (aula_win60he_backend.cpp:1819-1834):
            // a failed transfer or decode is tried again 100 ms later, while a
            // decoded proof that names the wrong board or default map, an
            // unstable active map or implausible travel waits for the device
            // to change.
            bool NoRetry(ushort pid, Action<RmKeyboard> setup)
            {
                var (session, io, _, _) = RmSession(0x1CA2, pid, setup);
                Assert.False(session.Start(io));
                return session.NoStartRetry;
            }
            Assert.False(NoRetry(0x1902, k => k.Precision = 0));
            Assert.False(NoRetry(0x1902, k => k.Maximum = 499));
            Assert.False(NoRetry(0x1902, k => k.Override = r => r[3] == 0x01 ? new List<byte[]>() : null));
            Assert.True(NoRetry(0x1901, null));
            Assert.True(NoRetry(0x1902, k => k.FunctionFilter = (read, key, f) => read == 5 && key == 0x29 ? 0x52 : f));
            Assert.True(NoRetry(0x1902, k => k.Travel[21] = 3601));
            Assert.True(NoRetry(0x1902, k =>
            {
                k.DefaultMap = new byte[126];
                k.DefaultMap[21] = 0x29;
            }));
        }

        [Fact]
        public void AulaRm_TravelPlausibility_ChecksOccupiedPositionsOnly()
        {
            // aula_win60he_client.cpp:614-638: max 3400 plus max(8 x 10, 200)
            // is 3600 at a position with a key. An empty position is not checked.
            foreach (var (value, position, ok) in new[] { (3600, 21, true), (3601, 21, false), (9000, 0, true) })
            {
                var (session, io, _, _) = RmSession(0x1CA2, 0x1902, k => k.Travel[position] = (ushort)value);
                Assert.Equal(ok, session.Start(io));
            }
            Assert.True(AulaRmProtocol.TravelPlausible(new ushort[63], MaxDefaultMap(), 0, 10, 10, 3400));
            Assert.False(AulaRmProtocol.TravelPlausible(new ushort[63], MaxDefaultMap(), 4, 10, 10, 3400));
        }

        [Fact]
        public void AulaRm_Start_RejectsTransportFaults()
        {
            // ReadResponse (aula_win60he_client.cpp:140-207),
            // ReadProtocolBlock (aula_win60he_backend.cpp:1329-1356).
            List<byte[]> Fault(string kind, byte[] request)
            {
                var reports = new RmKeyboard().Respond(request).ToList();
                switch (kind)
                {
                    case "silent": return new List<byte[]>();
                    case "short": reports[0] = reports[0].Take(64).ToArray(); break;
                    case "report-id": reports[0][0] = 1; break;
                    case "command": reports[0][3] = 0x92; break;
                    case "checksum": reports[0][4] ^= 0xFF; break;
                    case "status": reports[0][5] = 1; break;
                    case "missing-continuation": reports.RemoveAt(reports.Count - 1); break;
                }
                return reports;
            }
            foreach (var (kind, command) in new[]
            {
                ("silent", 0x01), ("short", 0x01), ("report-id", 0x00), ("command", 0x01),
                ("checksum", 0x2B), ("status", 0x00), ("missing-continuation", 0x12),
            })
            {
                var (session, io, _, _) = RmSession(0x1CA2, 0x1902, k =>
                    k.Override = r => r[3] == command ? Fault(kind, r) : null);
                Assert.False(session.Start(io), kind);
                Assert.True(session.Poisoned, kind);
            }
            var (gone, goneIo, _, _) = RmSession(0x1CA2, 0x1902);
            goneIo.Gone = true;
            Assert.False(gone.Start(goneIo));
        }

        [Fact]
        public void AulaRm_Pass_RefreshesTheActiveMapEvery2000ms_AndPausesBetweenPasses()
        {
            // aula_win60he_backend.cpp:2080-2153 and 2254-2262.
            var pauses = new List<int>();
            var (session, io, _, clock) = RmSession(0x1CA2, 0x1902, pauses: pauses);
            Assert.True(session.Start(io));
            int start = io.Writes("out").Count;

            clock.Now += 1999;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, Keys(), null));
            Assert.Equal(2, io.Writes("out").Count - start);

            clock.Now += 1;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, Keys(), null));
            var writes = io.Writes("out").Skip(start + 2).ToList();
            Assert.Equal(10 + 2, writes.Count);
            Assert.All(writes.Take(10), w => Assert.Equal(0x23, w[3]));
            Assert.Equal(AulaRmProtocol.TravelRequest(1), writes[10]);

            // The next refresh is 2000 ms after that one.
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, Keys(), null));
            Assert.Equal(2, io.Writes("out").Count - start - 14);
            Assert.Equal(new[] { 1, 1 }, pauses);
        }

        [Fact]
        public void AulaRm_Pass_ARefreshedSiblingMap_RenamesTheKeys()
        {
            // aula_win60he_backend.cpp:2133-2142: a changed map is republished.
            var (session, io, keyboard, clock) = RmSession(0x1CA2, 0x7777, CompatibleSibling);
            Assert.True(session.Start(io));
            keyboard.Travel[3] = 2000; // identifier 4
            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.A));

            keyboard.Functions[4] = AnalogKeyCodes.ArrowUp;
            clock.Now += AulaRmSession.ActiveMapRefreshMs;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.ArrowUp));
            Assert.Contains(AnalogKeyCodes.ArrowUp, session.KeyOrder);
            Assert.DoesNotContain(AnalogKeyCodes.A, session.KeyOrder);
        }

        [Fact]
        public void AulaRm_Pass_FailureEndsTheSession_WithNoFurtherRequest()
        {
            // aula_win60he_backend.cpp:2160-2188: a failed travel read destroys
            // the session before any further request.
            var (session, io, keyboard, _) = RmSession(0x1CA2, 0x1902);
            Assert.True(session.Start(io));
            keyboard.Override = _ => new List<byte[]>();
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, Keys(), null));
            int writes = io.Writes("out").Count;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, Keys(), null));
            Assert.Equal(writes, io.Writes("out").Count);

            // An active map that stops agreeing with itself ends it too.
            var (unstable, io2, keyboard2, clock) = RmSession(0x1CA2, 0x1902);
            Assert.True(unstable.Start(io2));
            keyboard2.FunctionFilter = (read, key, f) => read >= 15 && key == 0x29 ? 0x52 : f;
            clock.Now += AulaRmSession.ActiveMapRefreshMs;
            Assert.Equal(AnalogPollResult.Failed, unstable.Pass(io2, Keys(), null));
        }

        [Fact]
        public void AulaRm_Stop_SendsNothing()
        {
            // aula_win60he_backend.cpp:2662-2725: nothing is sent at shutdown.
            var (session, io, _, _) = RmSession(0x1CA2, 0x1902);
            Assert.True(session.Start(io));
            session.Pass(io, Keys(), null);
            int log = io.Log.Count;
            session.Stop(io);
            Assert.Equal(log, io.Log.Count);
        }

        [Fact]
        public void AulaRm_Normalization_And_KeyFunctionDecoding_FollowHallJoy()
        {
            // aula_win60he_protocol_test.cpp:511-516.
            Assert.Equal(0, AulaRmProtocol.NormalizeTravel(0, 3400));
            Assert.Equal(3, AulaRmProtocol.NormalizeTravel(10, 3400));
            Assert.Equal(250, AulaRmProtocol.NormalizeTravel(850, 3400));
            Assert.Equal(500, AulaRmProtocol.NormalizeTravel(1700, 3400));
            Assert.Equal(1000, AulaRmProtocol.NormalizeTravel(3400, 3400));
            Assert.Equal(1000, AulaRmProtocol.NormalizeTravel(4000, 3400));
            Assert.Equal(0, AulaRmProtocol.NormalizeTravel(1, 3400));
            Assert.Equal(1, AulaRmProtocol.NormalizeTravel(2, 3400));
            Assert.Equal(0, AulaRmProtocol.NormalizeTravel(100, 0));

            // aula_win60he_protocol_test.cpp:439-444 and 434-437.
            Assert.Equal(0x04, AulaRmProtocol.KeyCodeForFunction(0x0004));
            Assert.Equal(0xE7, AulaRmProtocol.KeyCodeForFunction(0x00E7));
            Assert.Equal(0, AulaRmProtocol.KeyCodeForFunction(0x0000));
            Assert.Equal(0, AulaRmProtocol.KeyCodeForFunction(0x0003));
            Assert.Equal(AnalogKeyCodes.Fn, AulaRmProtocol.KeyCodeForFunction(0xF001));
            Assert.Equal(0, AulaRmProtocol.KeyCodeForFunction(0x1234));
            Assert.Equal(AnalogKeyCodes.Fn, AulaRmProtocol.FactoryKeyCode(0x01));
            Assert.Equal(0, AulaRmProtocol.FactoryKeyCode(0x02));
            Assert.Equal(AnalogKeyCodes.ContextMenu, AulaRmProtocol.FactoryKeyCode(0x65));

            // aula_win60he_protocol_test.cpp:377-387: a wrong key or layout
            // echo fails the answer.
            var query = OracleActiveQueries[0];
            var values = new ushort[14];
            var payload = OracleActive[0].Skip(4).Take(57).ToArray();
            Assert.True(AulaRmProtocol.DecodeKeyFunctions(0xA3, payload, query, 0, values));
            Assert.Equal(0x52, values[0]);
            var wrongKey = (byte[])payload.Clone();
            wrongKey[5] ^= 1;
            Assert.False(AulaRmProtocol.DecodeKeyFunctions(0xA3, wrongKey, query, 0, values));
            var wrongLayout = (byte[])payload.Clone();
            wrongLayout[6] = 1;
            Assert.False(AulaRmProtocol.DecodeKeyFunctions(0xA3, wrongLayout, query, 0, values));
        }

        [Fact]
        public void AulaRm_FirstReport_MustMatchTheRequestBeforeAnyContinuation()
        {
            // aula_win60he_client.cpp:26-62.
            var def = OracleDefault[1];
            Assert.True(AulaRmProtocol.FirstReportMatches(Split(def)[0].AsSpan(1), 0x2B, 1, 0, 2));
            Assert.False(AulaRmProtocol.FirstReportMatches(Split(def)[0].AsSpan(1), 0x2B, 1, 0, 0));
            var travel = Split(OracleTravel)[0].AsSpan(1);
            Assert.True(AulaRmProtocol.FirstReportMatches(travel, 0x12, 3, 0x02, 1));
            Assert.False(AulaRmProtocol.FirstReportMatches(travel, 0x12, 1, 0x02, 1));
            Assert.False(AulaRmProtocol.FirstReportMatches(travel, 0x12, 3, 0x03, 1));
            Assert.True(AulaRmProtocol.FirstReportMatches(Split(OraclePrecision)[0].AsSpan(1), 0x00, 1, 0x25, 0));
            Assert.True(AulaRmProtocol.FirstReportMatches(Split(OracleSync)[0].AsSpan(1), 0x01, 1, 0, 0));
            Assert.True(AulaRmProtocol.FirstReportMatches(Split(OracleActive[0])[0].AsSpan(1), 0x23, 1, 0, 0));
            Assert.Equal(3, AulaRmProtocol.ResponseReportCount(128));
            Assert.Equal(1, AulaRmProtocol.ResponseReportCount(60));
        }

        // ── RM 6x21 Matches ─────────────────────────────────────────────────

        private static AnalogKeyboardDeviceInfo RmInfo(ushort vid, ushort pid, string product = "keyboard",
            string manufacturer = "")
            => new()
            {
                Path = $@"\\?\hid#vid_{vid:x4}&pid_{pid:x4}&mi_02#8&2a1c&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
                VendorId = vid,
                ProductId = pid,
                UsagePage = 0xFFA0,
                Usage = 0x0001,
                InterfaceNumber = 2,
                InputReportLength = 65,
                OutputReportLength = 65,
                ProductString = product,
                ManufacturerString = manufacturer,
                HasInputReport = id => id == 0,
                HasOutputReport = id => id == 0,
            };

        [Fact]
        public void AulaRm_Matches_TheKnownBoards_AndAula1CA2Siblings_AndRejectsNearMisses()
        {
            // The six known identities (aula_win60he_protocol.h:29-36).
            Assert.Equal(6, JingTaiRoutes.AulaBoards.Count);
            foreach (var board in JingTaiRoutes.AulaBoards)
                Assert.True(JingTaiRoutes.AulaRm.Matches(RmInfo(board.VendorId, board.ProductId)));
            // Any VID 1CA2 collection with the shape (the vid_1ca2 path
            // clause and the VID brand evidence).
            Assert.True(JingTaiRoutes.AulaRm.Matches(RmInfo(0x1CA2, 0x7777)));

            // HERO 68 HE Ultra 1CA5:0407 is not in the table and its path lacks
            // vid_1ca2. HallJoy could reach it only through a SetupAPI token,
            // which this metadata does not carry.
            Assert.False(JingTaiRoutes.AulaRm.Matches(RmInfo(0x1CA5, 0x0407, "AULA HERO 68 HE Ultra")));
            // Shape near misses on a known identity (aula_win60he_backend.cpp:703-707).
            var usage = RmInfo(0x1CA2, 0x1902);
            usage.Usage = 0x0002;
            Assert.False(JingTaiRoutes.AulaRm.Matches(usage));
            var page = RmInfo(0x1CA2, 0x1902);
            page.UsagePage = 0xFF60;
            Assert.False(JingTaiRoutes.AulaRm.Matches(page));
            var input = RmInfo(0x1CA5, 0x2201);
            input.InputReportLength = 64;
            Assert.False(JingTaiRoutes.AulaRm.Matches(input));
            var output = RmInfo(0x1CA5, 0x2201);
            output.OutputReportLength = 33;
            Assert.False(JingTaiRoutes.AulaRm.Matches(output));
            // A path with a known token but no brand evidence in the attributes
            // or strings fails the fingerprint.
            var foreign = RmInfo(0x1234, 0x5678);
            foreign.Path = @"\\?\hid#vid_1ca5&pid_2201&mi_02#x";
            Assert.False(JingTaiRoutes.AulaRm.Matches(foreign));
            foreign.ManufacturerString = "SparkPlayJoy";
            Assert.True(JingTaiRoutes.AulaRm.Matches(foreign));
            // A path with neither token.
            var bluetooth = RmInfo(0x1CA5, 0x2201);
            bluetooth.Path = @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&021ca5_pid&2201_rev&0001#x";
            Assert.False(JingTaiRoutes.AulaRm.Matches(bluetooth));
        }

        [Fact]
        public void AulaRm_PathIdentity_AndFamilyTokens_FollowHallJoysParser()
        {
            // aula_win60he_protocol_test.cpp:105-133.
            Assert.True(AulaRmProtocol.PathContainsKnownUsbIdentity(@"\\?\hid#vid_1ca5&pid_2201&mi_03#7&abc#{guid}"));
            Assert.True(AulaRmProtocol.PathContainsKnownUsbIdentity(@"\\?\HID#VID_1CA5&PID_2202&MI_03#7&ABC#{GUID}"));
            Assert.True(AulaRmProtocol.PathContainsKnownUsbIdentity(@"\\?\hid#vid_1ca2&pid_2201#lite"));
            Assert.True(AulaRmProtocol.PathContainsKnownUsbIdentity(@"\\?\hid#vid_1ca2&pid_1902#aula"));
            Assert.False(AulaRmProtocol.PathContainsKnownUsbIdentity(@"\\?\hid#vid_1ca5&pid_2203#unknown"));
            Assert.False(AulaRmProtocol.PathContainsKnownUsbIdentity(@"\\?\hid#vid_1ca5&rev_0001&pid_2201#not-contiguous"));
            Assert.False(AulaRmProtocol.PathContainsKnownUsbIdentity(@"\\?\hid#vid_1ca&pid_2201#truncated-vid"));
            Assert.False(AulaRmProtocol.PathContainsKnownUsbIdentity(string.Empty));
            Assert.True(AulaRmProtocol.TryReadUsbIdentityFromPath(@"\\?\HID#VID_1CA5&PID_2203&MI_03#unknown",
                out ushort vid, out ushort pid));
            Assert.Equal(0x1CA5, vid);
            Assert.Equal(0x2203, pid);
            Assert.True(AulaRmProtocol.PathContainsAulaVendor(@"\\?\HID#VID_1CA2&PID_7777#x"));
            Assert.False(AulaRmProtocol.PathContainsAulaVendor(@"\\?\hid#vid_1ca5&pid_2201#x"));
            // aula_win60he_backend.cpp:682-692.
            Assert.True(AulaRmProtocol.ContainsFamilyToken("AULA"));
            Assert.True(AulaRmProtocol.ContainsFamilyToken("SparkPlayJoy Keyboard"));
            Assert.True(AulaRmProtocol.ContainsFamilyToken("Spark Play Joy"));
            Assert.False(AulaRmProtocol.ContainsFamilyToken("GravaStar"));
            Assert.False(AulaRmProtocol.ContainsFamilyToken("Aul"));
        }

        // ── Routes ──────────────────────────────────────────────────────────

        [Fact]
        public void Routes_AreInHallJoysCatalogOrder_AndOpenExclusively()
        {
            // native_analog_backends.def:27, 41-42.
            var all = JingTaiRoutes.All;
            Assert.Equal(new[] { AnalogKeyboardProtocol.AulaRm, AnalogKeyboardProtocol.JingTaiV1,
                AnalogKeyboardProtocol.ChilkeySlice75 }, all.Select(r => r.Protocol));
            Assert.Equal(3, all.Select(r => r.Id).Distinct().Count());
            foreach (var route in all)
            {
                Assert.False(route.Id.StartsWith("soup-", StringComparison.Ordinal));
                Assert.True(route.Writable);
                Assert.True(route.Exclusive);
                Assert.Equal(64, route.InputBuffers);
                Assert.Null(route.Companion);
            }
        }

        [Fact]
        public void Routes_NameTheModels_AndCreateTheirSessions()
        {
            // HallJoy's names: jingtai_v1_profiles.h:8 and 12 (the TTC shares
            // the ND63's), slice75_backend.cpp:553, aula_rm_family.h:8-10 and
            // generated/layout_pipeline/identities.h:13, 36-38.
            var ttc = JingTaiInfo(0x1CA2, 0x0406, "IROK ND63 TTC");
            Assert.Equal("IROK ND63", JingTaiRoutes.JingTaiV1.Name(ttc));
            var session = Assert.IsType<JingTaiSession>(JingTaiRoutes.JingTaiV1.CreateSession(ttc));
            Assert.Equal("JT1-WE-CYAN", session.Model.Identity);
            Assert.Equal(64, JingTaiRoutes.JingTaiV1.Keys(ttc).Length);

            var slice = JingTaiInfo(0x1CA3, 0x0701, "SLICE75 HE");
            Assert.Equal("Chilkey Slice75 HE", JingTaiRoutes.ChilkeySlice75.Name(slice));
            Assert.Same(JingTaiRoutes.Slice75Model,
                Assert.IsType<JingTaiSession>(JingTaiRoutes.ChilkeySlice75.CreateSession(slice)).Model);

            Assert.Equal("Aula WIN 60 HE PRO", JingTaiRoutes.AulaRm.Name(RmInfo(0x1CA2, 0x1902, "WIN 60 HE PRO")));
            Assert.Equal("Aula WIN 60 HE MAX", JingTaiRoutes.AulaRm.Name(RmInfo(0x1CA2, 0x1902, "WIN 60 HE MAX")));
            Assert.Equal("Aula WIN 68 HE PRO / MAX", JingTaiRoutes.AulaRm.Name(RmInfo(0x1CA2, 0x1901)));
            Assert.Equal("Aula HERO 68 HE PRO", JingTaiRoutes.AulaRm.Name(RmInfo(0x1CA5, 0x0409)));
            Assert.Equal("GravaStar Mercury V75 Lite", JingTaiRoutes.AulaRm.Name(RmInfo(0x1CA2, 0x2201)));
            Assert.Null(JingTaiRoutes.AulaRm.Name(RmInfo(0x1CA2, 0x7777)));
            Assert.IsType<AulaRmSession>(JingTaiRoutes.AulaRm.CreateSession(RmInfo(0x1CA2, 0x1902)));
        }
    }
}
