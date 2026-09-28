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
    /// The 09 frame routes ported from HallJoy (AGPL-3.0, commit 378f9fe):
    /// AULA HERO, IPI Addressed and generic Addressed (issue #468). No such
    /// keyboard is on the bench, so every rule is pinned against byte
    /// fixtures: the frames HallJoy's own builders printed, HallJoy's test
    /// vectors (tests/ipi_native_test.cpp, tests/addressed_poll_scheduler_test.cpp
    /// and the HERO self-test in aula_hero84he_backend.cpp:647-740), and
    /// scripted keyboards that answer in the record layouts the firmware
    /// research documents. Paths without a directory are in
    /// src/HallJoyProject/HallJoy.
    /// </summary>
    public class AnalogKeyboardAddressedTests
    {
        private const ulong Qbz65 = 0x110000000023;
        private const ulong Qbz75 = 0x11000000002C;
        private const ulong Hero84 = 0x110000000005;
        private const ulong Hero68 = 0x110000000003;

        // ── Fixtures ──

        private static byte[] Frame(byte checksum, params byte[] head)
        {
            var f = new byte[64];
            Array.Copy(head, f, head.Length);
            f[63] = checksum;
            return f;
        }

        /// <summary>An answer: header 09 cmd sub 00 01 00, a data length of
        /// six bytes per record, each record the ID big-endian then a 32-bit
        /// value big-endian, and a valid checksum.</summary>
        private static byte[] Answer(byte command, byte subcommand, params (int Id, uint Value)[] records)
        {
            var f = new byte[64];
            f[0] = 0x09;
            f[1] = command;
            f[2] = subcommand;
            f[4] = 0x01;
            f[6] = (byte)(records.Length * 6);
            for (int i = 0; i < records.Length; i++)
            {
                int at = 7 + i * 6;
                f[at] = (byte)(records[i].Id >> 8);
                f[at + 1] = (byte)records[i].Id;
                f[at + 2] = (byte)(records[i].Value >> 24);
                f[at + 3] = (byte)(records[i].Value >> 16);
                f[at + 4] = (byte)(records[i].Value >> 8);
                f[at + 5] = (byte)records[i].Value;
            }
            AddressedFrame.Finish(f);
            return f;
        }

        private static byte[] UuidAnswer(ulong uuid) => Answer(0x82, 0x01, ((int)(uuid >> 32), (uint)uuid));

        private static (int, int) Command((byte Command, byte Subcommand, ushort[] Ids) request)
            => (request.Command, request.Subcommand);

        private static byte[] Unnumbered(byte[] frame) => new byte[] { 0x00 }.Concat(frame).ToArray();

        /// <summary>The command, subcommand and IDs of a request as written,
        /// with or without a leading zero report ID.</summary>
        private static (byte Command, byte Subcommand, ushort[] Ids) Decode(byte[] wire)
        {
            int at = wire.Length > 64 ? 1 : 0;
            var f = wire.AsSpan(at, 64);
            int count = f[1] == 0x82 ? 0 : f[6] / 2;
            var ids = new ushort[count];
            for (int i = 0; i < count; i++) ids[i] = (ushort)((f[7 + 2 * i] << 8) | f[8 + 2 * i]);
            return (f[1], f[2], ids);
        }

        /// <summary>The vendor key code a factory key carries: modifiers as a
        /// bit at 16 to 23, Fn as 0x0D000000 (tools/prepare_ipi_layouts.py:24-40,
        /// docs/research/AULA_HERO84HE_FACTORY_MAP.md:23-25).</summary>
        private static uint Keycode(int code) => code switch
        {
            AnalogKeyCodes.Fn => 0x0D000000u,
            >= 0xE0 and <= 0xE7 => 1u << (16 + code - 0xE0),
            _ => (uint)code,
        };

        /// <summary>A scripted keyboard of the 09 frame family: answers the
        /// identity read, key map reads (IPI and HERO), the IPI calibration
        /// read and sample reads from its fields, and logs every request.</summary>
        private sealed class NineKeyboard
        {
            public ulong Uuid;
            public int[] Factory;
            public bool Unnumbered;
            public bool AnswerUuid = true;
            public bool AnswerSamples = true;
            public bool ReverseSamples;
            public Func<int, uint> Assignment;
            public Func<int, (ushort Released, ushort Bottom)> Calibration = _ => (10000, 2000);
            public readonly Dictionary<int, ushort> Raw = new();
            public ushort RestRaw = 10000;
            public Func<ushort[], IEnumerable<byte[]>> EmptyMap = _ => new[] { Answer(0x83, 0x00) };
            public Func<byte[], IEnumerable<byte[]>> Before = _ => Array.Empty<byte[]>();
            public readonly List<(byte Command, byte Subcommand, ushort[] Ids)> Requests = new();

            public NineKeyboard(ulong uuid, int[] factory)
            {
                Uuid = uuid;
                Factory = factory;
                Assignment = id => id < Factory.Length ? Keycode(Factory[id]) : 0u;
            }

            public IEnumerable<byte[]> Respond(byte[] wire)
            {
                var request = Decode(wire);
                Requests.Add(request);
                var answers = new List<byte[]>(Before(wire));
                var ids = request.Ids;
                switch ((request.Command, request.Subcommand))
                {
                    case (0x82, 0x01):
                        if (AnswerUuid) answers.Add(UuidAnswer(Uuid));
                        break;
                    case (0x83, 0x00):
                        if (ids.Length == 0) answers.AddRange(EmptyMap(ids));
                        else answers.Add(Answer(0x83, 0x00, ids.Select(id => ((int)id, Assignment(id))).ToArray()));
                        break;
                    case (0x94, 0x05):
                        answers.Add(Answer(0x94, 0x05, ids.Select(id =>
                        {
                            var c = Calibration(id);
                            return ((int)id, (uint)c.Released << 16 | c.Bottom);
                        }).ToArray()));
                        break;
                    case (0x94, 0x02):
                        if (!AnswerSamples) break;
                        var records = ids.Select(id => ((int)id, (uint)RawOf(id) << 16 | 0x1234u)).ToArray();
                        if (ReverseSamples) Array.Reverse(records);
                        answers.Add(Answer(0x94, 0x02, records));
                        break;
                }
                return Unnumbered ? answers.Select(AnalogKeyboardAddressedTests.Unnumbered) : answers;
            }

            public ushort RawOf(int id) => Raw.TryGetValue(id, out ushort raw) ? raw : RestRaw;
        }

        private static AnalogKeyboardTestTransport Transport(NineKeyboard keyboard, int length = 64)
            => new() { InputLength = length, OutputLength = length, OnSend = keyboard.Respond };

        private static NineKeyboard Ipi(ulong uuid = Qbz65)
            => new(uuid, AddressedRoutes.FindIpiModel(uuid).Table);

        private static NineKeyboard Hero(ulong uuid = Hero84)
            => new(uuid, AddressedRoutes.FindAulaHeroModel(uuid).Table);

        private static AnalogKeyboardDeviceInfo Info(ushort vid, ushort pid, ushort page = 0xFF60, ushort usage = 0x61,
            ushort inLength = 64, ushort outLength = 64, bool inReport9 = true, bool outReport9 = true)
            => new()
            {
                VendorId = vid,
                ProductId = pid,
                UsagePage = page,
                Usage = usage,
                InputReportLength = inLength,
                OutputReportLength = outLength,
                HasInputReport = id => inReport9 ? id == 9 : id == 0,
                HasOutputReport = id => outReport9 ? id == 9 : id == 0,
            };

        // ── The shared 09 frame ──

        [Fact]
        public void Frames_MatchTheBytesHallJoysBuildersPrinted()
        {
            // Dumped from ipi::UuidRequest, ipi::Request and the HERO builders
            // (spec 2.4, HERO spec 4.3), ipi_protocol.h:30-49 and
            // aula_hero84he_diagnostic_protocol.cpp:82-102.
            Assert.Equal(Frame(0x6C, 0x09, 0x82, 0x01, 0x00, 0x01, 0x00, 0x06), AddressedFrame.IdentityRequest());
            Assert.Equal(Frame(0x4D, 0x09, 0x94, 0x02, 0x00, 0x01, 0x00, 0x08, 0x00, 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04),
                AddressedFrame.SampleRequest(new ushort[] { 1, 2, 3, 4 }));
            Assert.Equal(Frame(0x60, 0x09, 0x83, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00, 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04),
                AddressedFrame.MapRequest(new byte[] { 1, 2, 3, 4 }));
            Assert.Equal(Frame(0x4A, 0x09, 0x94, 0x05, 0x00, 0x01, 0x00, 0x08, 0x00, 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04),
                AddressedFrame.CalibrationRequest(new byte[] { 1, 2, 3, 4 }));
            Assert.Equal(Frame(0x20, 0x09, 0x94, 0x02, 0x00, 0x01, 0x00, 0x12, 0x00, 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04,
                    0x00, 0x05, 0x00, 0x06, 0x00, 0x07, 0x00, 0x08, 0x00, 0x09),
                AddressedFrame.SampleRequest(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
            // HERO: 83 layer 0 and 94 02 for W A S D Space, then with 1, 2, 3 and 103.
            Assert.Equal(Frame(0x80, 0x09, 0x83, 0x00, 0x00, 0x01, 0x00, 0x0A, 0x00, 0x1E, 0x00, 0x2B, 0x00, 0x2C, 0x00, 0x2D, 0x00, 0x46),
                AddressedFrame.MapRequest(new ushort[] { 30, 43, 44, 45, 70 }));
            Assert.Equal(Frame(0x6D, 0x09, 0x94, 0x02, 0x00, 0x01, 0x00, 0x0A, 0x00, 0x1E, 0x00, 0x2B, 0x00, 0x2C, 0x00, 0x2D, 0x00, 0x46),
                AddressedFrame.SampleRequest(new ushort[] { 30, 43, 44, 45, 70 }));
            Assert.Equal(Frame(0xF8, 0x09, 0x94, 0x02, 0x00, 0x01, 0x00, 0x12, 0x00, 0x1E, 0x00, 0x2B, 0x00, 0x2C, 0x00, 0x2D, 0x00, 0x46,
                    0x00, 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x67),
                AddressedFrame.SampleRequest(new ushort[] { 30, 43, 44, 45, 70, 1, 2, 3, 103 }));
        }

        [Fact]
        public void Frames_ComputedFromMakePacket_SessionStartEmptyMapAndProbe()
        {
            // MakePacket (addressed_analog_backend.cpp:368-385), computed in spec 2.4:
            // 98 02 at session start (:1332), the empty 83 00 (:711), and the
            // generic probe for canonical W A S D, IDs 1E 2B 2C 2D (:76, 79).
            Assert.Equal(Frame(0x5B, 0x09, 0x98, 0x02, 0x00, 0x01, 0x00, 0x00), AddressedFrame.SessionStart());
            Assert.Equal(Frame(0x72, 0x09, 0x83, 0x00, 0x00, 0x01, 0x00, 0x00), AddressedFrame.EmptyMapRequest());
            Assert.Equal(Frame(0xB5, 0x09, 0x94, 0x02, 0x00, 0x01, 0x00, 0x08, 0x00, 0x1E, 0x00, 0x2B, 0x00, 0x2C, 0x00, 0x2D),
                AddressedFrame.SampleRequest(new byte[] { 0x1E, 0x2B, 0x2C, 0x2D }));
        }

        [Fact]
        public void Requests_RefuseWhatHallJoysBuildersRefuse()
        {
            // ipi_protocol.h:40-46, tests/ipi_native_test.cpp:34-40 and HERO's
            // UniquePositions (aula_hero84he_diagnostic_protocol.cpp:18-28).
            Assert.Null(AddressedFrame.MapRequest(Array.Empty<byte>()));
            Assert.Null(AddressedFrame.MapRequest(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }));
            Assert.Null(AddressedFrame.MapRequest(new byte[] { 30, 30 }));
            Assert.Null(AddressedFrame.SampleRequest(new byte[] { 0, 1 }));
            Assert.Null(AddressedFrame.SampleRequest(new ushort[] { 0xFFFF }));
            Assert.Null(AddressedFrame.CalibrationRequest(new byte[] { 5, 6, 5 }));

            // ipi_native_test.cpp:34-35: nine IDs, the last one at byte 24.
            var request = AddressedFrame.SampleRequest(new byte[] { 30, 43, 44, 45, 66, 69, 71, 72, 73 });
            Assert.Equal(18, request[6]);
            Assert.Equal(0, request[7]);
            Assert.Equal(30, request[8]);
            Assert.Equal(73, request[24]);
            Assert.True(AddressedFrame.ChecksumValid(request));
        }

        [Fact]
        public void Locate_FindsNumberedAndUnnumberedFrames_IncludingAZeroChecksum()
        {
            // FindPayload64 tests offsets 0 and 1 (addressed_analog_backend.cpp:356-366).
            var frame = UuidAnswer(Qbz65);
            Assert.Equal(0, AddressedFrame.Locate(frame));
            Assert.Equal(1, AddressedFrame.Locate(Unnumbered(frame)));
            Assert.Equal(-1, AddressedFrame.Locate(frame.Take(63).ToArray()));
            var broken = (byte[])frame.Clone();
            broken[20] ^= 1;
            Assert.Equal(-1, AddressedFrame.Locate(broken));

            // The Plus UUID 11000000005B makes a checksum of 0x00, so the offset 0
            // window of its unnumbered report also sums to 0xFF (spec 2.5).
            // HallJoy's FindPayload64 stops there and loses the frame. Locate
            // requires the 09 too and finds it at offset 1.
            var plus = UuidAnswer(0x11000000005B);
            Assert.Equal(0x00, plus[63]);
            Assert.True(AddressedFrame.ChecksumValid(Unnumbered(plus).AsSpan(0, 64)));
            Assert.Equal(1, AddressedFrame.Locate(Unnumbered(plus)));
            Assert.Equal(0x11000000005BUL, AddressedFrame.ParseUuid(Unnumbered(plus).AsSpan(1, 64)));
        }

        [Fact]
        public void Wire_PutsTheFrameBehindAZeroReportIdOnLongerReports()
        {
            // Transport::Send (addressed_analog_backend.cpp:414-431).
            var frame = AddressedFrame.SessionStart();
            Assert.Equal(frame, AddressedFrame.Wire(frame, 64));
            Assert.Equal(Unnumbered(frame), AddressedFrame.Wire(frame, 65));
        }

        [Fact]
        public void ParseUuid_ReadsBigEndian48_AndRefusesABrokenChecksum()
        {
            // tests/ipi_native_test.cpp:28-32 and ipi_protocol.h:33-37.
            var answer = UuidAnswer(Qbz75);
            Assert.Equal(Qbz75, AddressedFrame.ParseUuid(answer));
            answer[12] ^= 1;
            Assert.Equal(0UL, AddressedFrame.ParseUuid(answer));
            // A data length other than 6 is not an identity answer.
            var wrongLength = UuidAnswer(Qbz75);
            wrongLength[6] = 12;
            AddressedFrame.Finish(wrongLength);
            Assert.Equal(0UL, AddressedFrame.ParseUuid(wrongLength));
        }

        [Fact]
        public void OrderedRecords_RequiresHerosRequestOrder_RecordsAcceptsAnyOrder()
        {
            // HERO CorrelatePositions (aula_hero84he_diagnostic_protocol.cpp:69-79)
            // against ipi::Records (ipi_protocol.h:50-64).
            var inOrder = Answer(0x94, 0x02, (30, 5000u << 16), (43, 6000u << 16));
            var swapped = Answer(0x94, 0x02, (43, 6000u << 16), (30, 5000u << 16));
            var positions = new ushort[] { 30, 43 };
            Assert.True(AddressedFrame.OrderedRecords(inOrder, 0x94, 0x02, positions));
            Assert.False(AddressedFrame.OrderedRecords(swapped, 0x94, 0x02, positions));
            Assert.True(AddressedFrame.Records(swapped, 0x94, 0x02, new byte[] { 30, 43 }));
            Assert.False(AddressedFrame.Records(swapped, 0x94, 0x02, new byte[] { 30, 44 }));
        }

        [Fact]
        public void ProbeAccepts_HallJoysAdmissionRules()
        {
            // ProbeAddressedResponse (addressed_analog_backend.cpp:616-642).
            var ids = new byte[] { 0x1E, 0x2B, 0x2C, 0x2D };
            var good = Answer(0x94, 0x02, (0x1E, 9000u << 16), (0x2B, 0), (0x2C, 0), (0x2D, 0));
            Assert.True(AddressedFrame.ProbeAccepts(good, ids));
            // Bytes 3 to 5 are not checked by the probe.
            var loose = (byte[])good.Clone();
            loose[4] = 0x07;
            AddressedFrame.Finish(loose);
            Assert.True(AddressedFrame.ProbeAccepts(loose, ids));
            // No record with a nonzero raw value.
            Assert.False(AddressedFrame.ProbeAccepts(Answer(0x94, 0x02, (0x1E, 0x80000000u), (0x2B, 0), (0x2C, 0), (0x2D, 0)), ids));
            // Wrong record count, an ID not asked for, an ID twice, another command.
            Assert.False(AddressedFrame.ProbeAccepts(Answer(0x94, 0x02, (0x1E, 9000u << 16)), ids));
            Assert.False(AddressedFrame.ProbeAccepts(Answer(0x94, 0x02, (0x1E, 9000u << 16), (0x2B, 0), (0x2C, 0), (0x2E, 0)), ids));
            Assert.False(AddressedFrame.ProbeAccepts(Answer(0x94, 0x02, (0x1E, 9000u << 16), (0x1E, 0), (0x2C, 0), (0x2D, 0)), ids));
            Assert.False(AddressedFrame.ProbeAccepts(Answer(0x94, 0x05, (0x1E, 9000u << 16), (0x2B, 0), (0x2C, 0), (0x2D, 0)), ids));
        }

        // ── IPI protocol (tests/ipi_native_test.cpp) ──

        [Fact]
        public void IpiHid_DecodesKeycodesAsHallJoyDoes()
        {
            // ipi::Hid (ipi_protocol.h:65-72), every row dumped in spec 5.2.
            Assert.Equal(0, AddressedIpiProtocol.Hid(0));
            Assert.Equal(0x1A, AddressedIpiProtocol.Hid(0x1A));
            Assert.Equal(0xE8, AddressedIpiProtocol.Hid(0xE8)); // below 256 passes through, unlike HERO
            Assert.Equal(AnalogKeyCodes.Fn, AddressedIpiProtocol.Hid(0x0D000000));
            for (int bit = 0; bit < 8; bit++)
                Assert.Equal(0xE0 + bit, AddressedIpiProtocol.Hid(0x10000u << bit));
            Assert.Equal(0, AddressedIpiProtocol.Hid(0x00030000));
            Assert.Equal(0, AddressedIpiProtocol.Hid(0x01000004));
            Assert.Equal(0, AddressedIpiProtocol.Hid(0x00040004));
            Assert.Equal(0, AddressedIpiProtocol.Hid(0x01000000));
        }

        [Fact]
        public void IpiMap_StoresOnlyAfterEveryRecordPasses()
        {
            // tests/ipi_native_test.cpp:36-52.
            var ids = new byte[] { 30, 43, 44, 45, 66, 69, 71, 72, 73 };
            uint[] codes = { 26, 4, 22, 7, 0x00200000, 0x00040000, 0x00400000, 0x0D000000, 0 };
            var map = Answer(0x83, 0x00, ids.Select((id, i) => ((int)id, codes[i])).ToArray());
            var mapping = new int[256];
            Assert.True(AddressedIpiProtocol.Map(map, ids, mapping));
            Assert.Equal(26, mapping[30]);
            Assert.Equal(0xE5, mapping[66]);
            Assert.Equal(0xE2, mapping[69]);
            Assert.Equal(0xE6, mapping[71]);
            Assert.Equal(AnalogKeyCodes.Fn, mapping[72]);
            Assert.Equal(0, mapping[73]);

            var saved = (int[])mapping.Clone();
            var bad = (byte[])map.Clone();
            bad[8] = 43; // record 0 repeats record 1's ID
            AddressedFrame.Finish(bad);
            Assert.False(AddressedIpiProtocol.Map(bad, ids, mapping));
            Assert.Equal(saved, mapping);
            bad = (byte[])map.Clone();
            bad[7] = 1; // ID 0x11E is past 255
            AddressedFrame.Finish(bad);
            Assert.False(AddressedIpiProtocol.Map(bad, ids, mapping));
            bad = (byte[])map.Clone();
            bad[5] = 1; // reserved header byte
            AddressedFrame.Finish(bad);
            Assert.False(AddressedIpiProtocol.Map(bad, ids, mapping));
            bad = (byte[])map.Clone();
            bad[11] ^= 1; // checksum no longer holds
            Assert.False(AddressedIpiProtocol.Map(bad, ids, mapping));
            Assert.Equal(saved, mapping);
        }

        [Fact]
        public void IpiCalibration_AndNormalization_HallJoysVectors()
        {
            // tests/ipi_native_test.cpp:53-64: released 10000+100i, bottom 2000+100i.
            var ids = new byte[] { 30, 43, 44, 45, 66, 69, 71, 72, 73 };
            var cal = Answer(0x94, 0x05, ids.Select((id, i) =>
                ((int)id, (uint)(10000 + i * 100) << 16 | (uint)(2000 + i * 100))).ToArray());
            var released = new ushort[256];
            var bottom = new ushort[256];
            Assert.True(AddressedIpiProtocol.Calibrations(cal, ids, released, bottom));
            foreach (byte id in ids)
            {
                ushort r = released[id], b = bottom[id];
                Assert.Equal(0, AddressedIpiProtocol.Normalize(r, r, b));
                Assert.Equal(1000, AddressedIpiProtocol.Normalize(b, r, b));
                Assert.Equal(500, AddressedIpiProtocol.Normalize((ushort)(b + 4000), r, b));
                Assert.Equal(0, AddressedIpiProtocol.Normalize((ushort)(r + 10), r, b));
                Assert.Equal(1000, AddressedIpiProtocol.Normalize((ushort)(b - 100), r, b));
                Assert.Equal(0, AddressedIpiProtocol.Normalize(0, r, b));
            }

            // One untrusted record rejects the batch and keeps the stored values.
            var bad = Answer(0x94, 0x05, ids.Select((id, i) =>
                ((int)id, i == 8 ? 100u << 16 | 200u : (uint)(10000 + i * 100) << 16 | (uint)(2000 + i * 100))).ToArray());
            Array.Clear(released);
            Assert.True(AddressedIpiProtocol.Calibrations(cal, ids, released, bottom));
            Assert.False(AddressedIpiProtocol.Calibrations(bad, ids, released, bottom));
            Assert.Equal(10000, released[30]);
            Assert.Equal(10800, released[73]);
        }

        [Theory]
        // Dumped with released 10000 and bottom 2000 (spec 5.6, ipi_protocol.h:108-114).
        [InlineData(0, 0)]
        [InlineData(1999, 1000)]
        [InlineData(2000, 1000)]
        [InlineData(2001, 1000)]
        [InlineData(6000, 500)]
        [InlineData(9937, 8)]
        [InlineData(9938, 8)]
        [InlineData(9939, 8)]
        [InlineData(9960, 0)]
        [InlineData(9961, 0)]
        [InlineData(9999, 0)]
        [InlineData(10000, 0)]
        [InlineData(12000, 0)]
        public void IpiNormalize_DumpedValues(int raw, int milli)
        {
            Assert.Equal(milli, AddressedIpiProtocol.Normalize((ushort)raw, 10000, 2000));
        }

        [Fact]
        public void IpiValid_DumpedValues()
        {
            // ipi::Valid (ipi_protocol.h:73-75), spec 5.3.
            Assert.False(AddressedIpiProtocol.Valid(2256, 2000));
            Assert.True(AddressedIpiProtocol.Valid(2257, 2000));
            Assert.False(AddressedIpiProtocol.Valid(0x8000, 2000));
            Assert.False(AddressedIpiProtocol.Valid(10000, 0));
        }

        [Fact]
        public void IpiSamples_RequireANonzeroRawInEveryRecord()
        {
            // tests/ipi_native_test.cpp:65-69 and ipi::Samples (ipi_protocol.h:96-107).
            var ids = new byte[] { 30, 43, 44, 45, 66, 69, 71, 72, 73 };
            var sample = Answer(0x94, 0x02, ids.Select(id => ((int)id, (uint)(0x8000 | 8400) << 16 | 1234u)).ToArray());
            Assert.True(AddressedIpiProtocol.Samples(sample, ids));
            var bad = Answer(0x94, 0x02, ids.Select(id => ((int)id, id == 73 ? 0u : (uint)8400 << 16)).ToArray());
            Assert.False(AddressedIpiProtocol.Samples(bad, ids));
            // The status bit alone is not a raw value.
            var flagOnly = Answer(0x94, 0x02, ids.Select(id => ((int)id, id == 73 ? 0x80000000u : (uint)8400 << 16)).ToArray());
            Assert.False(AddressedIpiProtocol.Samples(flagOnly, ids));
        }

        [Fact]
        public void Publication_AggregatesByMaximum_AndExpiresAfter500Ms()
        {
            // tests/ipi_native_test.cpp:70-77 and physical_analog_state.h:24-52.
            var pub = new AddressedPublication(500);
            Assert.True(pub.Bind(30, 26));
            Assert.True(pub.Bind(43, 26));
            Assert.True(pub.Bind(72, AnalogKeyCodes.Fn));
            Assert.False(pub.Bind(30, 4));
            Assert.False(pub.Bind(0, 4));
            Assert.False(pub.Bind(1, 0xFFFF));
            pub.Publish(30, 800, 1000);
            pub.Publish(43, 500, 1100);
            pub.Publish(72, 700, 1100);
            Assert.Equal(800, pub.Read(26, 1100).Milli);
            Assert.Equal(700, pub.Read(AnalogKeyCodes.Fn, 1100).Milli);
            pub.Publish(43, 0, 1200);
            Assert.Equal(800, pub.Read(26, 1200).Milli);
            pub.Publish(43, 400, 1450);
            Assert.Equal(400, pub.Read(26, 1550).Milli);
            Assert.False(pub.Read(26, 2000).Fresh);
            Assert.False(pub.Read(26, 999).Fresh);
            // An unbound key publishes nothing.
            Assert.False(pub.Publish(99, 900, 1000));

            var output = new AnalogKeyInputState();
            pub.Fill(output, 1550);
            Assert.Equal(0.4f, output.Get(26));
            Assert.Equal(0.7f, output.Get(AnalogKeyCodes.Fn));
            Assert.Equal(2, output.Count);
            pub.Fill(output, 2000);
            Assert.Equal(0, output.Count);
        }

        // ── IPI session ──

        [Fact]
        public void IpiStart_ReadsUuidMapAndCalibrationInBatches_ProbesThenStartsTheSession()
        {
            // ReadIpiProfile (addressed_analog_backend.cpp:479-563), the probe of
            // the first four IDs (:694-702), and 98 02 (:1332).
            var kb = Ipi();
            var io = Transport(kb);
            var session = new AddressedIpiSession();
            Assert.True(session.Start(io));
            Assert.Equal("IPI QBZ65", session.ModelName);
            Assert.Equal(Qbz65, session.Uuid);

            var model = AddressedRoutes.FindIpiModel(Qbz65);
            var sent = kb.Requests;
            Assert.Equal(1 + 8 * 2 + 1 + 1, sent.Count);
            Assert.Equal((0x82, 0x01), Command(sent[0]));
            for (int batch = 0; batch < 8; batch++)
            {
                var ids = model.Order.Skip(batch * 9).Take(9).ToArray();
                Assert.Equal((0x83, 0x00), Command(sent[1 + batch * 2]));
                Assert.Equal(ids, sent[1 + batch * 2].Ids);
                Assert.Equal((0x94, 0x05), Command(sent[2 + batch * 2]));
                Assert.Equal(ids, sent[2 + batch * 2].Ids);
            }
            Assert.Equal(new ushort[] { 98, 99, 102, 103 }, sent[15].Ids);
            Assert.Equal(new ushort[] { 1, 15, 16, 17 }, sent[17].Ids);
            Assert.Equal((0x94, 0x02), Command(sent[17]));
            Assert.Equal((0x98, 0x02), Command(sent[18]));
            var writes = io.Writes("out");
            Assert.Equal(AddressedFrame.IdentityRequest(), writes[0]);
            Assert.Equal(AddressedFrame.SessionStart(), writes[^1]);
            Assert.Empty(io.Writes("ctl"));
            Assert.Empty(io.Writes("setf"));

            // The live map equals the factory map here, Fn included.
            Assert.Equal(AnalogKeyboardData.KeysOf(model.Table), session.KeyOrder);
            Assert.Contains(AnalogKeyCodes.Fn, session.KeyOrder);
            Assert.Equal((10000, 2000), session.CalibrationOf(30));
        }

        [Fact]
        public void IpiStart_UnnumberedReports_UseALeadingZeroReportId()
        {
            // Buffers sized from the caps: a 65-byte report carries the frame at
            // offset 1 (addressed_analog_backend.cpp:414-431, 457-475).
            var kb = Ipi(Qbz75);
            kb.Unnumbered = true;
            kb.Raw[2] = 6000; // F1 at 500
            var io = Transport(kb, 65);
            var session = new AddressedIpiSession();
            Assert.True(session.Start(io));
            Assert.Equal("IPI QBZ75", session.ModelName);
            Assert.Equal(Unnumbered(AddressedFrame.IdentityRequest()), io.Writes("out")[0]);
            // 82 keys: ten batches.
            Assert.Equal(1 + 10 * 2 + 1 + 1, kb.Requests.Count);

            // The poll answers arrive the same way.
            var output = new AnalogKeyInputState();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.F1));
            Assert.All(io.Writes("out"), w => Assert.Equal(65, w.Length));
            Assert.Equal(Unnumbered(AddressedFrame.SampleRequest(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 })),
                io.Writes("out")[^1]);
        }

        [Fact]
        public void IpiStart_NoUuid_TriesTwiceThenRefuses()
        {
            // Two attempts (addressed_analog_backend.cpp:488), then rejection with
            // generic mapping disabled for this identity (:501-505).
            var kb = Ipi();
            kb.AnswerUuid = false;
            var io = Transport(kb);
            Assert.False(new AddressedIpiSession().Start(io));
            Assert.Equal(2, kb.Requests.Count);
            Assert.All(kb.Requests, r => Assert.Equal((byte)0x82, r.Command));
        }

        [Fact]
        public void IpiStart_SkipsOtherFramesWhileWaitingForTheUuid()
        {
            // Replies that fail ParseUuid are skipped (addressed_analog_backend.cpp:492-499).
            var kb = Ipi();
            var junk = Answer(0x94, 0x02, (1, 9000u << 16));
            var badChecksum = UuidAnswer(Qbz65);
            badChecksum[40] ^= 0x10;
            kb.Before = wire => Decode(wire).Command == 0x82 ? new[] { junk, badChecksum, new byte[] { 0x09, 0x82 } } : Array.Empty<byte[]>();
            var io = Transport(kb);
            var session = new AddressedIpiSession();
            Assert.True(session.Start(io));
            Assert.Single(kb.Requests, r => r.Command == 0x82);
        }

        [Fact]
        public void IpiStart_UnknownUuid_IsRefusedBeforeAnyMapRead()
        {
            // The QBZ65+ UUID is not in the catalog (tests/ipi_native_test.cpp:27,
            // addressed_analog_backend.cpp:506-511).
            var kb = Ipi();
            kb.Uuid = 0x11000000005B;
            Assert.False(new AddressedIpiSession().Start(Transport(kb)));
            Assert.Single(kb.Requests);
        }

        [Fact]
        public void IpiStart_MapAnswerWithOtherIds_TwoAttemptsThenRefuses()
        {
            // Each phase allows two attempts (addressed_analog_backend.cpp:524-545).
            var kb = Ipi();
            var io = new AnalogKeyboardTestTransport
            {
                InputLength = 64,
                OutputLength = 64,
                OnSend = wire =>
                {
                    var (command, _, ids) = Decode(wire);
                    if (command != 0x83) return kb.Respond(wire);
                    kb.Requests.Add(Decode(wire));
                    return new[] { Answer(0x83, 0x00, ids.Select(id => (id + 1, 4u)).ToArray()) };
                },
            };
            Assert.False(new AddressedIpiSession().Start(io));
            Assert.Equal(new byte[] { 0x82, 0x83, 0x83 }, kb.Requests.Select(r => r.Command).ToArray());
        }

        [Fact]
        public void IpiStart_UntrustedCalibration_Refuses()
        {
            // ipi::Calibrations rejects the batch (ipi_protocol.h:87-90) and two
            // failed attempts reject the keyboard (addressed_analog_backend.cpp:539-545).
            var kb = Ipi();
            kb.Calibration = id => id == 20 ? ((ushort)100, (ushort)200) : ((ushort)10000, (ushort)2000);
            Assert.False(new AddressedIpiSession().Start(Transport(kb)));
            Assert.Equal(new[] { (0x82, 0x01), (0x83, 0x00), (0x94, 0x05), (0x94, 0x05) },
                kb.Requests.Select(Command).ToArray());
        }

        [Fact]
        public void IpiStart_ProbeUnanswered_RefusesWithoutTheSessionStart()
        {
            // ProbeAddressedResponse waits up to 120 ms (addressed_analog_backend.cpp:616)
            // and a failed probe leaves the keyboard unclaimed (:699).
            var kb = Ipi();
            kb.AnswerSamples = false;
            var io = Transport(kb);
            Assert.False(new AddressedIpiSession().Start(io));
            Assert.Equal((byte)0x94, kb.Requests[^1].Command);
            Assert.DoesNotContain(kb.Requests, r => r.Command == 0x98);

            // All four probe keys at raw 0 is no plausible sample either.
            var still = Ipi();
            still.RestRaw = 0;
            Assert.False(new AddressedIpiSession().Start(Transport(still)));
        }

        [Fact]
        public void IpiStart_DeviceGone_Refuses()
        {
            // A failed send ends the UUID attempts (addressed_analog_backend.cpp:490).
            var io = Transport(Ipi());
            io.Gone = true;
            Assert.False(new AddressedIpiSession().Start(io));
        }

        private static (AddressedIpiSession Session, AnalogKeyboardTestTransport Io, NineKeyboard Keyboard)
            StartedIpi(Func<long> clock, Action<NineKeyboard> setup = null)
        {
            var kb = Ipi();
            setup?.Invoke(kb);
            var io = Transport(kb);
            var session = new AddressedIpiSession { Clock = clock };
            Assert.True(session.Start(io));
            kb.Requests.Clear();
            return (session, io, kb);
        }

        [Fact]
        public void IpiPass_FirstPassSweepsTheFirstNineKeys_UnderTheirAssignedCodes()
        {
            // The initialization sweep (addressed_poll_scheduler.cpp:241-248).
            // Key ID 16 ('2' at the factory) is assigned A in the vendor app and
            // reads 6000 between released 10000 and bottom 2000: 500
            // (ipi_protocol.h:108-114), reported as A, HallJoy's automatic layout
            // reading (addressed_analog_backend.cpp:1744-1754).
            long now = 5_000_000;
            var (session, io, kb) = StartedIpi(() => now, k =>
            {
                k.Assignment = id => id == 16 ? 0x04u : Keycode(k.Factory[id]);
                k.Raw[16] = 6000;
            });
            Assert.Contains(AnalogKeyCodes.A, session.KeyOrder);
            var output = new AnalogKeyInputState();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(new byte[] { 1, 15, 16, 17, 18, 19, 20, 21, 22 }, session.LastPlan.Ids.ToArray());
            Assert.Equal(AddressedFrame.SampleRequest(new byte[] { 1, 15, 16, 17, 18, 19, 20, 21, 22 }), io.Writes("out")[^1]);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.D2));
            Assert.Equal(1, output.Count);
        }

        [Fact]
        public void IpiPass_AcceptsRecordsInAnyOrder_AndSkipsFramesThatDoNotCorrelate()
        {
            // PublishResponse drops frames that are not 09 94 02 and counts wrong
            // sets as invalid while the wait goes on (addressed_analog_backend.cpp:1010-1066).
            long now = 5_000_000;
            var (session, io, kb) = StartedIpi(() => now, k =>
            {
                k.Raw[15] = 3600; // 800
                k.ReverseSamples = true;
            });
            kb.Before = wire => Decode(wire).Command == 0x94
                ? new[]
                {
                    AddressedFrame.SessionStart(),                                  // another command
                    Answer(0x94, 0x02, (1, 9000u << 16)),                            // one record, nine asked
                    Answer(0x94, 0x02, Enumerable.Range(40, 9).Select(id => (id, 9000u << 16)).ToArray()), // other keys
                }
                : Array.Empty<byte[]>();
            var output = new AnalogKeyInputState();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.8f, output.Get(AnalogKeyCodes.D1));
            // One discard per request, standing in for the late answers
            // HallJoy's reader drops (addressed_analog_backend.cpp:1403-1419).
            Assert.Equal(1, io.Discards);
        }

        [Fact]
        public void IpiPass_ZeroRawInOneRecord_IsNoAnswer()
        {
            // ipi::Samples needs a nonzero raw in every record (ipi_protocol.h:103),
            // and the rejected answer publishes nothing.
            long now = 5_000_000;
            var (session, io, _) = StartedIpi(() => now, k => k.Raw[18] = 0);
            var output = new AnalogKeyInputState();
            output.Set(AnalogKeyCodes.Q, 0.3f);
            Assert.Equal(AnalogPollResult.NoAnswer, session.Pass(io, output, null));
            Assert.Equal(0.3f, output.Get(AnalogKeyCodes.Q)); // the reader, not the session, releases on a miss
            // Eight misses in a row end HallJoy's session (addressed_analog_backend.cpp:53).
            Assert.Equal(8, session.MissLimit);
        }

        [Fact]
        public void IpiPass_KeysSharingACode_ReportTheDeepest_AndStaleKeysExpire()
        {
            // physical_analog_state.h:39-52: maximum over fresh keys, 500 ms.
            long now = 5_000_000;
            var (session, io, _) = StartedIpi(() => now, k =>
            {
                k.Assignment = id => id is 15 or 16 ? (uint)AnalogKeyCodes.W : Keycode(k.Factory[id]);
                k.Raw[15] = 3600; // 800
                k.Raw[16] = 6000; // 500
            });
            var output = new AnalogKeyInputState();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.8f, output.Get(AnalogKeyCodes.W));

            // The next sweep passes do not ask for 15 or 16 again. 400 ms later
            // W is still current, 900 ms after its sample it has expired.
            now += 400_000;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.8f, output.Get(AnalogKeyCodes.W));
            now += 500_000;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.W));
        }

        [Fact]
        public void IpiPass_UnassignedKey_IsPolledButNotReported()
        {
            // A key whose live assignment decodes to 0 (a macro here) has no live
            // binding (addressed_analog_backend.cpp:844-847) and publishes nothing.
            long now = 5_000_000;
            var (session, io, kb) = StartedIpi(() => now, k =>
            {
                k.Assignment = id => id == 17 ? 0x03000001u : Keycode(k.Factory[id]);
                k.Raw[17] = 2000;
            });
            Assert.DoesNotContain(AnalogKeyCodes.D3, session.KeyOrder);
            var output = new AnalogKeyInputState();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Contains((ushort)17, kb.Requests[0].Ids);
            Assert.Equal(0, output.Count);
        }

        [Fact]
        public void IpiPass_HeldKeysAreBound_RefreshedEvery25Ms()
        {
            // RefreshBindings every 25 ms (addressed_analog_backend.cpp:1347, 1227-1236).
            // HallJoy binds the keys its gamepad bindings use. Here the bound
            // keys are the ones Windows sees held. W is key ID 30 on the QBZ65.
            long now = 5_000_000;
            var (session, io, _) = StartedIpi(() => now);
            var output = new AnalogKeyInputState();
            session.Pass(io, output, code => code == AnalogKeyCodes.W);
            Assert.Equal(AddressedPollClass.Bound, session.Scheduler.ClassOf(30, (ulong)now));
            Assert.NotEqual(AddressedPollClass.Bound, session.Scheduler.ClassOf(31, (ulong)now));
            now += 10_000;
            session.Pass(io, output, _ => false);
            Assert.Equal(AddressedPollClass.Bound, session.Scheduler.ClassOf(30, (ulong)now));
            now += 20_000;
            session.Pass(io, output, _ => false);
            Assert.NotEqual(AddressedPollClass.Bound, session.Scheduler.ClassOf(30, (ulong)now));
        }

        [Fact]
        public void IpiPass_TheKeysAMappingReads_AreBound_EvenAtRest()
        {
            // HallJoy's bound keys are the ones its gamepad bindings use
            // (Bindings_IsHidBound, addressed_analog_backend.cpp:1227-1236),
            // polled at the top rate even at rest. The row's IsBound says which
            // keys a mapping reads, and it wins over the held-key cue.
            long now = 5_000_000;
            var (session, io, _) = StartedIpi(() => now);
            session.IsBound = code => code == AnalogKeyCodes.W;
            var output = new AnalogKeyInputState();
            session.Pass(io, output, _ => false);
            Assert.Equal(AddressedPollClass.Bound, session.Scheduler.ClassOf(30, (ulong)now));
            now += 30_000;
            session.Pass(io, output, code => code == AnalogKeyCodes.A);
            Assert.Equal(AddressedPollClass.Bound, session.Scheduler.ClassOf(30, (ulong)now));
            Assert.NotEqual(AddressedPollClass.Bound, session.Scheduler.ClassOf(31, (ulong)now));
        }

        [Fact]
        public void IpiPass_NoAnswerForASecond_EndsTheSession()
        {
            // kMaxNoResponseUs (addressed_analog_backend.cpp:54, 1422-1432).
            long now = 5_000_000;
            var (session, io, kb) = StartedIpi(() => now);
            kb.AnswerSamples = false;
            var output = new AnalogKeyInputState();
            now += 999_000;
            Assert.Equal(AnalogPollResult.NoAnswer, session.Pass(io, output, null));
            now += 1_000;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
        }

        [Fact]
        public void IpiPass_DeviceGone_Fails()
        {
            // A failed send ends the session (addressed_analog_backend.cpp:1376-1384).
            long now = 5_000_000;
            var (session, io, _) = StartedIpi(() => now);
            io.Gone = true;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, new AnalogKeyInputState(), null));
        }

        [Fact]
        public void IpiStop_WritesNothing()
        {
            // Nothing is sent on stop and 98 02 is never reversed
            // (addressed_analog_backend.cpp:1444-1466, 1661-1711).
            long now = 5_000_000;
            var (session, io, _) = StartedIpi(() => now);
            int writes = io.Log.Count;
            session.Stop(io);
            Assert.Equal(writes, io.Log.Count);
        }

        [Fact]
        public void SendFrame_FallsBackToTheControlTransfer()
        {
            // Transport::Send tries WriteFile, then HidD_SetOutputReport
            // (addressed_analog_backend.cpp:409-422).
            var inner = new AnalogKeyboardTestTransport { InputLength = 64, OutputLength = 64 };
            var io = new FaultyTransport(inner) { FailWriteFile = true };
            Assert.True(AddressedPollingSession.SendFrame(io, AddressedFrame.SessionStart()));
            Assert.Equal(AddressedFrame.SessionStart(), inner.Writes("ctl").Single());
            Assert.Empty(inner.Writes("out"));
        }

        /// <summary>The scripted transport with a WriteFile that fails or a
        /// read that finds the device gone.</summary>
        private sealed class FaultyTransport : IAnalogKeyboardTransport
        {
            private readonly AnalogKeyboardTestTransport _inner;
            public FaultyTransport(AnalogKeyboardTestTransport inner) => _inner = inner;
            public bool FailWriteFile { get; init; }
            public bool ReadGone { get; init; }
            public bool Send(byte[] report) => !FailWriteFile && _inner.Send(report);
            public bool SendOutputReport(byte[] report) => _inner.SendOutputReport(report);
            public int Receive(byte[] buffer, int timeoutMs) => ReadGone ? -1 : _inner.Receive(buffer, timeoutMs);
            public void DiscardStale() => _inner.DiscardStale();
            public bool SetFeature(byte[] report) => _inner.SetFeature(report);
            public int GetFeature(byte[] buffer) => _inner.GetFeature(buffer);
            public int InputLength => _inner.InputLength;
            public int OutputLength => _inner.OutputLength;
            public int FeatureLength => _inner.FeatureLength;
        }

        // ── Generic Addressed ──

        [Fact]
        public void GenericParseMapPacket_ReadsHallJoysLittleEndianView()
        {
            // ParseMapPacket (addressed_analog_backend.cpp:565-590) against the
            // firmware's big-endian records (spec 5.2): plain usages land, a
            // modifier bitmask gives usage 0 and Fn a key ID past 255, and both
            // are dropped. Only changes count.
            var packet = Answer(0x83, 0x00, (0x1E, 0x1A), (0x37, 0x00020000), (0x48, 0x0D000000), (0x2B, 0x04));
            var map = new ushort[256];
            Assert.Equal(2, AddressedGenericProtocol.ParseMapPacket(packet, map));
            Assert.Equal(AnalogKeyCodes.W, map[0x1E]);
            Assert.Equal(AnalogKeyCodes.A, map[0x2B]);
            Assert.Equal(0, map[0x37]);
            Assert.Equal(0, map[0x48]);
            Assert.Equal(0, AddressedGenericProtocol.ParseMapPacket(packet, map));
            // A zero-length answer, what recent IPI images send to the empty read.
            Assert.Equal(0, AddressedGenericProtocol.ParseMapPacket(Answer(0x83, 0x00), map));
        }

        [Fact]
        public void GenericFindKeyIdForHid_PrefersTheDeviceMap()
        {
            // FindKeyIdForHid (addressed_analog_backend.cpp:592-599).
            var map = new ushort[256];
            Assert.Equal(0x1E, AddressedGenericProtocol.FindKeyIdForHid(map, AnalogKeyCodes.W));
            Assert.Equal(0x2B, AddressedGenericProtocol.FindKeyIdForHid(map, AnalogKeyCodes.A));
            Assert.Equal(0x2C, AddressedGenericProtocol.FindKeyIdForHid(map, AnalogKeyCodes.S));
            Assert.Equal(0x2D, AddressedGenericProtocol.FindKeyIdForHid(map, AnalogKeyCodes.D));
            map[0x50] = AnalogKeyCodes.W;
            map[0x40] = AnalogKeyCodes.W;
            Assert.Equal(0x40, AddressedGenericProtocol.FindKeyIdForHid(map, AnalogKeyCodes.W));
            Assert.Equal(0, AddressedGenericProtocol.FindKeyIdForHid(map, AnalogKeyCodes.F24));
        }

        [Fact]
        public void GenericBuildProfile_TwentyEntriesOrTheCanonicalFallback()
        {
            // BuildProfile (addressed_analog_backend.cpp:655-680).
            var map = new ushort[256];
            var fallback = AddressedGenericProtocol.BuildProfile(map, 0, out string source);
            Assert.Equal("canonical-fallback", source);
            Assert.Equal(82, fallback.Count);
            Assert.Equal(AddressedRoutes.CanonicalOrder.Select(id => (byte)id), fallback.Select(k => k.KeyId));
            Assert.Equal(0, fallback.Single(k => k.KeyId == 0x42).Hid);

            map[0x42] = AnalogKeyCodes.RShift;
            var partial = AddressedGenericProtocol.BuildProfile(map, 1, out source);
            Assert.Equal("canonical+partial-map", source);
            Assert.Equal(AnalogKeyCodes.RShift, partial.Single(k => k.KeyId == 0x42).Hid);

            for (int id = 1; id <= 20; id++) map[id + 100] = (ushort)(0x04 + id);
            var device = AddressedGenericProtocol.BuildProfile(map, 21, out source);
            Assert.Equal("device-map", source);
            Assert.Equal(21, device.Count);
            Assert.Equal(0x42, device[0].KeyId);
            Assert.Equal(101, device[1].KeyId);
        }

        [Fact]
        public void GenericNormalize_LearnsTheRangeFromTheSession()
        {
            // Normalise (addressed_analog_backend.cpp:875-892).
            ushort released = 0, bottom = 0;
            Assert.Equal(0, AddressedGenericProtocol.Normalize(0, 5000, ref released, ref bottom));
            Assert.Equal(0, AddressedGenericProtocol.Normalize(1, 499, ref released, ref bottom));
            Assert.Equal((0, 0), ((int)released, (int)bottom));
            // The first sample is the released value, the bottom 8400 below it.
            Assert.Equal(0, AddressedGenericProtocol.Normalize(1, 9000, ref released, ref bottom));
            Assert.Equal((9000, 600), ((int)released, (int)bottom));
            Assert.Equal(500, AddressedGenericProtocol.Normalize(1, 4800, ref released, ref bottom));
            Assert.Equal(0, AddressedGenericProtocol.Normalize(1, 20001, ref released, ref bottom));
            Assert.Equal(0, AddressedGenericProtocol.Normalize(1, 9500, ref released, ref bottom));
            Assert.Equal(9500, released);
            Assert.Equal(1000, AddressedGenericProtocol.Normalize(1, 550, ref released, ref bottom));
            Assert.Equal(550, bottom);
            // Under 8 is rest: (40 * 1000 + 4475) / 8950 = 4.
            Assert.Equal(0, AddressedGenericProtocol.Normalize(1, 9460, ref released, ref bottom));

            // Below 8400 the bottom starts at 500, and too short a range reads 0.
            ushort r2 = 0, b2 = 0;
            Assert.Equal(0, AddressedGenericProtocol.Normalize(2, 5000, ref r2, ref b2));
            Assert.Equal(500, b2);
            Assert.Equal(500, AddressedGenericProtocol.Normalize(2, 2750, ref r2, ref b2));
            ushort r3 = 0, b3 = 0;
            Assert.Equal(0, AddressedGenericProtocol.Normalize(3, 700, ref r3, ref b3));
            Assert.Equal(0, AddressedGenericProtocol.Normalize(3, 600, ref r3, ref b3));
        }

        [Fact]
        public void GenericStart_NoMap_ProbesCanonicalWasd_AndPollsTheFallback()
        {
            // ProbeCandidate (addressed_analog_backend.cpp:704-737): an empty map
            // read that adds nothing is sent twice, then the probe for 1E 2B 2C 2D,
            // then 98 02.
            var kb = new NineKeyboard(0, AddressedRoutes.CanonicalTable);
            var io = Transport(kb);
            var session = new AddressedGenericSession();
            Assert.True(session.Start(io));
            Assert.Null(session.ModelName);
            Assert.Equal("canonical-fallback", session.ProfileSource);
            var writes = io.Writes("out");
            Assert.Equal(4, writes.Count);
            Assert.Equal(AddressedFrame.EmptyMapRequest(), writes[0]);
            Assert.Equal(AddressedFrame.EmptyMapRequest(), writes[1]);
            Assert.Equal(AddressedFrame.SampleRequest(new byte[] { 0x1E, 0x2B, 0x2C, 0x2D }), writes[2]);
            Assert.Equal(AddressedFrame.SessionStart(), writes[3]);
            Assert.Equal(82, session.Profile.Count);
            Assert.Equal(AnalogKeyboardData.KeysOf(AddressedRoutes.CanonicalTable), session.KeyOrder);
        }

        [Fact]
        public void GenericStart_FullDeviceMap_DefinesTheProfile()
        {
            // 27 entries in three packets: the device map is the profile, key IDs
            // ascending (addressed_analog_backend.cpp:668-673). The second read
            // adds nothing and ends discovery (:723).
            var entries = Enumerable.Range(1, 27).Select(id => (id, (uint)(0x04 + id - 1))).ToArray();
            var kb = new NineKeyboard(0, new int[1])
            {
                EmptyMap = _ => new[]
                {
                    Answer(0x83, 0x00, entries.Take(9).ToArray()),
                    Answer(0x83, 0x00, entries.Skip(9).Take(9).ToArray()),
                    Answer(0x83, 0x00, entries.Skip(18).ToArray()),
                },
            };
            var io = Transport(kb);
            var session = new AddressedGenericSession();
            Assert.True(session.Start(io));
            Assert.Equal("device-map", session.ProfileSource);
            Assert.Equal(27, session.MapEntries);
            Assert.Equal(Enumerable.Range(1, 27).Select(id => (byte)id), session.Profile.Select(k => k.KeyId));
            // W, A, S, D sit at key IDs 23, 1, 19 and 4 in this map.
            Assert.Equal(new ushort[] { 23, 1, 19, 4 }, kb.Requests[2].Ids);
            Assert.Equal(2, kb.Requests.Count(r => r.Command == 0x83));
        }

        [Fact]
        public void GenericStart_OnAulasVendor_NeedsAnIpiUuid_BeforeAnyProbe()
        {
            // 98 02 turns an Addressed keyboard's last-key mode off (HallJoy
            // D-085) but is the HERO firmware's calibration-distance flow
            // (AULA_HERO84HE_FIRMWARE_2026-08-31.md:51), and a HERO answers
            // the W, A, S and D probe. So an AULA-vendor keyboard names an IPI
            // model first, and nothing past 82 01 reaches one that does not.
            var hero = Hero();
            var heroIo = Transport(hero);
            var heroSession = new AddressedGenericSession(AddressedRoutes.AulaVendorId);
            Assert.False(heroSession.Start(heroIo));
            Assert.True(heroSession.NoStartRetry);
            Assert.All(hero.Requests, r => Assert.Equal(0x82, r.Command));

            var silent = new NineKeyboard(0, AddressedRoutes.CanonicalTable) { AnswerUuid = false };
            var silentSession = new AddressedGenericSession(AddressedRoutes.AulaVendorId);
            Assert.False(silentSession.Start(Transport(silent)));
            Assert.DoesNotContain(silent.Requests, r => r.Command != 0x82);

            var ipi = new NineKeyboard(Qbz65, AddressedRoutes.CanonicalTable);
            Assert.True(new AddressedGenericSession(AddressedRoutes.AulaVendorId).Start(Transport(ipi)));
            Assert.Equal(0x82, ipi.Requests[0].Command);
            Assert.Contains(ipi.Requests, r => r.Command == 0x98);

            // Other vendors get HallJoy's probe as it is, no identity read.
            var other = new NineKeyboard(0, AddressedRoutes.CanonicalTable);
            Assert.True(new AddressedGenericSession(0x1234).Start(Transport(other)));
            Assert.DoesNotContain(other.Requests, r => r.Command == 0x82);
        }

        [Fact]
        public void GenericStart_ProbeUnanswered_Refuses()
        {
            // "Unsupported or incomplete responses remain unclaimed"
            // (addressed_analog_backend.cpp:735, IPI_NATIVE_SUPPORT_2026-09-14.md:74).
            var kb = new NineKeyboard(0, AddressedRoutes.CanonicalTable) { AnswerSamples = false };
            var io = Transport(kb);
            Assert.False(new AddressedGenericSession().Start(io));
            Assert.DoesNotContain(kb.Requests, r => r.Command == 0x98);
        }

        [Fact]
        public void GenericPass_FirstSampleIsRest_ThenDepthFromTheLearnedRange()
        {
            // A frozen clock keeps every key "moving" after the sweep, so the
            // weighted fill takes the lowest profile indices first
            // (addressed_poll_scheduler.cpp:181-205, ties at :198).
            long now = 5_000_000;
            var entries = Enumerable.Range(1, 27).Select(id => (id, (uint)(0x04 + id - 1))).ToArray();
            var kb = new NineKeyboard(0, new int[1])
            {
                EmptyMap = _ => new[]
                {
                    Answer(0x83, 0x00, entries.Take(9).ToArray()),
                    Answer(0x83, 0x00, entries.Skip(9).Take(9).ToArray()),
                    Answer(0x83, 0x00, entries.Skip(18).ToArray()),
                },
            };
            kb.Raw[1] = 9000;
            var io = Transport(kb);
            var session = new AddressedGenericSession { Clock = () => now };
            Assert.True(session.Start(io));
            var output = new AnalogKeyInputState();
            for (int pass = 0; pass < 3; pass++) Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.A));
            kb.Raw[1] = 4800;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Contains((byte)1, session.LastPlan.Ids.ToArray());
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.A));
        }

        // ── AULA HERO ──

        [Fact]
        public void HeroDecodeAssignment_CompiledChecks()
        {
            // DecodeAssignment (aula_hero84he_factory.h:93-101), HERO spec 4.7,
            // and the self-test's bit loop (aula_hero84he_backend.cpp:709-711).
            Assert.Equal(0x1A, AulaHeroProtocol.DecodeAssignment(0x1A));
            Assert.Equal(0xE7, AulaHeroProtocol.DecodeAssignment(0xE7));
            Assert.Equal(0, AulaHeroProtocol.DecodeAssignment(0xE8));
            Assert.Equal(0xE0, AulaHeroProtocol.DecodeAssignment(0x00010000));
            Assert.Equal(0xE1, AulaHeroProtocol.DecodeAssignment(0x00020000));
            Assert.Equal(0xE7, AulaHeroProtocol.DecodeAssignment(0x00800000));
            Assert.Equal(0, AulaHeroProtocol.DecodeAssignment(0x00030000));
            Assert.Equal(0, AulaHeroProtocol.DecodeAssignment(0x01000004));
            Assert.Equal(0, AulaHeroProtocol.DecodeAssignment(0x03000001));
            Assert.Equal(0, AulaHeroProtocol.DecodeAssignment(0x00000100));
            Assert.Equal(AnalogKeyCodes.Fn, AulaHeroProtocol.DecodeAssignment(0x0D000000));
            for (int bit = 0; bit < 8; bit++)
                Assert.Equal(0xE0 + bit, AulaHeroProtocol.DecodeAssignment(0x10000u << bit));
        }

        [Fact]
        public void HeroNormalize_SelfTestVectors()
        {
            // aula_hero84he_backend.cpp:654-668: positions 30 and 43 both bound
            // to W, read through the publication.
            var pub = new AddressedPublication(AulaHeroProtocol.FreshMs);
            pub.Bind(30, AnalogKeyCodes.W);
            pub.Bind(43, AnalogKeyCodes.W);
            ushort top30 = 0, bottom30 = 0, top43 = 0, bottom43 = 0;
            void Sample(int position, ushort raw)
            {
                ushort milli = position == 30
                    ? AulaHeroProtocol.Normalize(raw, ref top30, ref bottom30)
                    : AulaHeroProtocol.Normalize(raw, ref top43, ref bottom43);
                pub.Publish(position, milli, 1000);
            }
            Sample(30, 10000); Sample(43, 12000);
            Assert.Equal(0, pub.Read(AnalogKeyCodes.W, 1000).Milli);
            Sample(30, 5000); Sample(43, 6000);
            Assert.Equal(1000, pub.Read(AnalogKeyCodes.W, 1000).Milli);
            Sample(30, 10000);
            Assert.Equal(1000, pub.Read(AnalogKeyCodes.W, 1000).Milli);
            Sample(43, 12000);
            Assert.Equal(0, pub.Read(AnalogKeyCodes.W, 1000).Milli);
            Sample(30, 7500);
            Assert.Equal(500, pub.Read(AnalogKeyCodes.W, 1000).Milli);
            // Stale after 750 ms (aula_hero84he_backend.cpp:676-679).
            Assert.False(pub.Read(AnalogKeyCodes.W, 1751).Fresh);
            Assert.True(pub.Read(AnalogKeyCodes.W, 1750).Fresh);
            // Until the range passes 32 counts a key reads 0.
            ushort top = 0, bottom = 0;
            AulaHeroProtocol.Normalize(10000, ref top, ref bottom);
            Assert.Equal(0, AulaHeroProtocol.Normalize(9968, ref top, ref bottom));
            Assert.Equal(1000, AulaHeroProtocol.Normalize(9967, ref top, ref bottom));
        }

        [Fact]
        public void HeroPlan_MovementKeysFirst_ThenARotatingCursor()
        {
            // Plan (aula_hero84he_backend.cpp:350-376).
            var codes = AddressedRoutes.FindAulaHeroModel(Hero84).Table;
            Span<ushort> positions = stackalloc ushort[9];
            ushort cursor = 1;
            Assert.Equal(9, AulaHeroProtocol.Plan(codes, ref cursor, positions));
            Assert.Equal(new ushort[] { 30, 43, 44, 45, 70, 1, 2, 3, 4 }, positions.ToArray());
            Assert.Equal(2, cursor);
            AulaHeroProtocol.Plan(codes, ref cursor, positions);
            Assert.Equal(new ushort[] { 30, 43, 44, 45, 70, 2, 3, 4, 5 }, positions.ToArray());
            // The movement keys are not repeated when the cursor passes them.
            cursor = 29;
            AulaHeroProtocol.Plan(codes, ref cursor, positions);
            Assert.Equal(new ushort[] { 30, 43, 44, 45, 70, 29, 31, 32, 33 }, positions.ToArray());
            // Past position 103 no position has a key, so the scan wraps to 1.
            cursor = 104;
            AulaHeroProtocol.Plan(codes, ref cursor, positions);
            Assert.Equal(new ushort[] { 30, 43, 44, 45, 70, 1, 2, 3, 4 }, positions.ToArray());
            // A wrapped cursor keeps HallJoy's unsigned arithmetic.
            cursor = 0;
            AulaHeroProtocol.Plan(codes, ref cursor, positions);
            Assert.Equal(new ushort[] { 30, 43, 44, 45, 70, 1, 2, 3, 4 }, positions.ToArray());
            Assert.Equal(1, cursor);

            // 255 plans request every HERO84 position (aula_hero84he_backend.cpp:700-707).
            var requested = new HashSet<ushort>();
            cursor = 1;
            for (int pass = 0; pass < 255; pass++)
            {
                int n = AulaHeroProtocol.Plan(codes, ref cursor, positions);
                for (int i = 0; i < n; i++) requested.Add(positions[i]);
            }
            Assert.Equal(AddressedRoutes.FindAulaHeroModel(Hero84).Order.OrderBy(p => p), requested.OrderBy(p => p));
        }

        [Fact]
        public void HeroStart_IdentityThenAssignmentsInTableOrder()
        {
            // Identity and Map (aula_hero84he_backend.cpp:303-349): 84 positions
            // take ten requests, in the table's source order.
            var kb = Hero();
            var io = Transport(kb);
            var session = new AulaHeroSession();
            Assert.True(session.Start(io));
            Assert.Equal("AULA HERO84 HE", session.ModelName);
            var order = AddressedRoutes.FindAulaHeroModel(Hero84).Order;
            Assert.Equal(11, kb.Requests.Count);
            Assert.Equal((byte)0x82, kb.Requests[0].Command);
            for (int batch = 0; batch < 10; batch++)
            {
                Assert.Equal((0x83, 0x00), Command(kb.Requests[1 + batch]));
                Assert.Equal(order.Skip(batch * 9).Take(9), kb.Requests[1 + batch].Ids);
            }
            Assert.Equal(new ushort[] { 10, 11, 12, 13, 95, 98, 99, 14, 15 }, kb.Requests[2].Ids);
            Assert.Equal(AddressedFrame.IdentityRequest(), io.Writes("out")[0]);
            Assert.Equal(AnalogKeyboardData.KeysOf(AddressedRoutes.FindAulaHeroModel(Hero84).Table), session.KeyOrder);
            Assert.Equal(11, io.Discards);
        }

        [Fact]
        public void HeroStart_ModelTablesByUuid()
        {
            // aula_hero_family.h:178-188: HERO 68 HE reads 68 positions (8 requests).
            var kb = Hero(Hero68);
            var session = new AulaHeroSession();
            Assert.True(session.Start(Transport(kb)));
            Assert.Equal("AULA HERO 68 HE", session.ModelName);
            Assert.Equal(1 + 8, kb.Requests.Count);
            Assert.DoesNotContain(AnalogKeyCodes.F1, session.KeyOrder);
        }

        [Fact]
        public void HeroStart_Refusals()
        {
            // An unknown UUID (aula_hero84he_backend.cpp:310-311): nothing after the identity read.
            var unknown = Hero();
            unknown.Uuid = 0x110000000016; // HERO87, not in HallJoy's table
            Assert.False(new AulaHeroSession().Start(Transport(unknown)));
            Assert.Single(unknown.Requests);

            // Assignments echoed out of order fail CorrelatePositions.
            var shuffled = Hero();
            var io = new AnalogKeyboardTestTransport
            {
                InputLength = 64,
                OutputLength = 64,
                OnSend = wire =>
                {
                    var request = Decode(wire);
                    if (request.Command != 0x83) return shuffled.Respond(wire);
                    shuffled.Requests.Add(request);
                    return new[] { Answer(0x83, 0x00, Enumerable.Reverse(request.Ids).Select(p => ((int)p, 4u)).ToArray()) };
                },
            };
            Assert.False(new AulaHeroSession().Start(io));

            // A short read is a failed exchange (received != 64, aula_hero84he_backend.cpp:270-273).
            var shortRead = new AnalogKeyboardTestTransport
            {
                InputLength = 64,
                OutputLength = 64,
                OnSend = _ => new[] { UuidAnswer(Hero84).Take(40).ToArray() },
            };
            Assert.False(new AulaHeroSession().Start(shortRead));

            var gone = Transport(Hero());
            gone.Gone = true;
            Assert.False(new AulaHeroSession().Start(gone));
        }

        private static (AulaHeroSession Session, AnalogKeyboardTestTransport Io, NineKeyboard Keyboard)
            StartedHero(Func<long> clock, Action<NineKeyboard> setup = null, List<int> sleeps = null)
        {
            var kb = Hero();
            setup?.Invoke(kb);
            var io = Transport(kb);
            var session = new AulaHeroSession { Clock = clock, Sleep = ms => sleeps?.Add(ms) };
            Assert.True(session.Start(io));
            kb.Requests.Clear();
            return (session, io, kb);
        }

        [Fact]
        public void HeroPass_RemappedMovementKeys_SelfTestSequence()
        {
            // aula_hero84he_backend.cpp:682-694: positions 30 and 43 assigned A (4)
            // report as A, and position 53 holds a macro, so it reports nothing.
            long now = 1_000_000;
            var (session, io, kb) = StartedHero(() => now, k =>
                k.Assignment = p => p is 30 or 43 ? 4u : p == 53 ? 0x02000001u : Keycode(k.Factory[p]));
            Assert.Equal(AnalogKeyCodes.A, session.CodeAt(30));
            Assert.Equal(0, session.CodeAt(53));
            var output = new AnalogKeyInputState();

            kb.Raw[30] = 10000; kb.Raw[43] = 12000;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.A));
            kb.Raw[30] = 5000; kb.Raw[43] = 6000;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.W));
            kb.Raw[30] = 10000;
            session.Pass(io, output, null);
            Assert.Equal(1f, output.Get(AnalogKeyCodes.A));
            kb.Raw[43] = 12000;
            session.Pass(io, output, null);
            Assert.Equal(0f, output.Get(AnalogKeyCodes.A));
            // Bit 15, the scanner lock, is masked off (aula_hero84he_diagnostic_protocol.cpp:159-161).
            kb.Raw[30] = 0x8000 | 7500;
            session.Pass(io, output, null);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.A));

            Assert.All(kb.Requests, r => Assert.Equal(new ushort[] { 30, 43, 44, 45, 70 }, r.Ids.Take(5)));

            // A full turn of the cursor asks for 52 and 54 but never for 53,
            // whose assignment is no key (aula_hero84he_backend.cpp:370-373).
            for (int pass = 0; pass < 255; pass++) session.Pass(io, output, null);
            var asked = kb.Requests.SelectMany(r => r.Ids).ToHashSet();
            Assert.Contains((ushort)52, asked);
            Assert.Contains((ushort)54, asked);
            Assert.DoesNotContain((ushort)53, asked);
        }

        [Fact]
        public void HeroPass_BadAnswersAreFailures_ThreeEndTheSession()
        {
            // Three failed exchanges or parses in a row end HallJoy's session
            // (aula_hero84he_backend.cpp:439-453).
            long now = 1_000_000;
            var (session, io, kb) = StartedHero(() => now, k => k.ReverseSamples = true);
            Assert.Equal(3, session.MissLimit);
            Assert.Equal(AnalogPollResult.NoAnswer, session.Pass(io, new AnalogKeyInputState(), null));
            kb.ReverseSamples = false;
            kb.AnswerSamples = false;
            Assert.Equal(AnalogPollResult.NoAnswer, session.Pass(io, new AnalogKeyInputState(), null));
            kb.AnswerSamples = true;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, new AnalogKeyInputState(), null));
            // A failed write is a failed exchange too. A read that finds the
            // device gone ends the session at once.
            Assert.Equal(AnalogPollResult.NoAnswer,
                session.Pass(new FaultyTransport(io) { FailWriteFile = true }, new AnalogKeyInputState(), null));
            Assert.Equal(AnalogPollResult.Failed,
                session.Pass(new FaultyTransport(io) { ReadGone = true }, new AnalogKeyInputState(), null));
        }

        [Fact]
        public void HeroPass_SpacesRequestsOneMillisecondApart()
        {
            // next += 1 ms, sleep only when ahead, restart from now when behind
            // (aula_hero84he_backend.cpp:463-468).
            long now = 0;
            var sleeps = new List<int>();
            var (session, io, _) = StartedHero(() => now, null, sleeps);
            var output = new AnalogKeyInputState();
            session.Pass(io, output, null);
            session.Pass(io, output, null);
            now = 10_000;
            session.Pass(io, output, null);
            session.Pass(io, output, null);
            now = 10_400;
            session.Pass(io, output, null);
            Assert.Equal(new[] { 1, 2, 1, 1 }, sleeps);
        }

        [Fact]
        public void Hero_NeverSendsTheForbiddenCommands()
        {
            // HERO spec 4.9: only 82 01, 83 00 and 94 02, never 94 00, 94 03,
            // 94 04, 94 05 or 98 (docs/research/AULA_HERO84HE_FIRMWARE_2026-08-31.md:42-51).
            var kb = Hero();
            var io = Transport(kb);
            var session = new AulaHeroSession { Sleep = _ => { } };
            Assert.True(session.Start(io));
            var output = new AnalogKeyInputState();
            for (int pass = 0; pass < 300; pass++) session.Pass(io, output, null);
            session.Stop(io);
            Assert.All(kb.Requests, r => Assert.Contains(((int)r.Command, (int)r.Subcommand),
                new[] { (0x82, 0x01), (0x83, 0x00), (0x94, 0x02) }));
            Assert.Empty(io.Writes("ctl"));
            Assert.Empty(io.Writes("setf"));
            Assert.Empty(io.Writes("getf"));
        }

        [Fact]
        public void HeroStop_WritesNothing()
        {
            // aula_hero84he_backend.cpp:548-573.
            long now = 1_000_000;
            var (session, io, _) = StartedHero(() => now);
            int writes = io.Log.Count;
            session.Stop(io);
            Assert.Equal(writes, io.Log.Count);
        }

        // ── Scheduler (tests/addressed_poll_scheduler_test.cpp) ──

        private static (ulong[] Hits, ulong[] MaxGapUs) Simulate(int boundCount, int activeBegin, int activeCount,
            ulong durationUs = 3_000_000, ulong packetUs = 1170)
        {
            const int count = 82;
            var keys = Enumerable.Range(0, count).Select(i => ((byte)(i + 1), (ushort)(i + 1))).ToArray();
            var scheduler = new AddressedPollScheduler(keys);
            scheduler.Reset(0);
            for (int i = 0; i < boundCount; i++) scheduler.SetBound(keys[i].Item2, true);
            var hits = new ulong[count];
            var maxGap = new ulong[count];
            var last = new ulong[count];
            var plan = new AddressedPollPlan();
            for (ulong now = 0; now < durationUs; now += packetUs)
            {
                scheduler.BuildPlan(now, plan);
                Assert.InRange(plan.Count, 1, 9);
                var seen = new bool[256];
                for (int p = 0; p < plan.Count; p++)
                {
                    byte id = plan.KeyIds[p];
                    Assert.False(seen[id]);
                    seen[id] = true;
                    int index = id - 1;
                    Assert.InRange(index, 0, count - 1);
                    if (last[index] != 0) maxGap[index] = Math.Max(maxGap[index], now - last[index]);
                    last[index] = now;
                    hits[index]++;
                    bool active = index >= activeBegin && index < activeBegin + activeCount;
                    scheduler.OnSample(id, (ushort)(10000 - (active ? 4000 : 0)), (ushort)(active ? 500 : 0), now);
                }
            }
            return (hits, maxGap);
        }

        [Fact]
        public void Scheduler_MoreThanAPacketOfBoundKeys_AllServicedAndTheSweepAdvances()
        {
            var (hits, gaps) = Simulate(18, 40, 0);
            for (int i = 0; i < 18; i++)
            {
                Assert.True(hits[i] > 700);
                Assert.True(gaps[i] <= 15000);
            }
            for (int i = 18; i < 82; i++)
            {
                Assert.True(hits[i] > 45);
                Assert.True(gaps[i] <= 50000);
            }
        }

        [Fact]
        public void Scheduler_ActiveUnboundKeys_GetALiveShare()
        {
            var (hits, gaps) = Simulate(12, 30, 12);
            for (int i = 0; i < 12; i++) Assert.True(hits[i] > 700);
            for (int i = 30; i < 42; i++)
            {
                Assert.True(hits[i] > 150);
                Assert.True(gaps[i] <= 15000);
            }
            for (int i = 42; i < 82; i++)
            {
                Assert.True(hits[i] > 40);
                Assert.True(gaps[i] <= 50000);
            }
        }

        [Fact]
        public void Scheduler_SmallBoundSets_FitEveryPacket()
        {
            var (hits, _) = Simulate(4, 40, 0);
            for (int i = 0; i < 4; i++) Assert.True(hits[i] > 2400);
            for (int i = 4; i < 82; i++) Assert.True(hits[i] > 45);
        }

        [Fact]
        public void Scheduler_MixedLoad_BoundKeysStillLead()
        {
            var (hits, _) = Simulate(30, 40, 20);
            ulong bound = 0, active = 0;
            for (int i = 0; i < 30; i++) bound += hits[i];
            for (int i = 40; i < 60; i++) active += hits[i];
            Assert.True(bound / 30 > active / 20);
        }

        [Fact]
        public void Scheduler_FullMatrixChord_NoStarvation()
        {
            var (hits, gaps) = Simulate(24, 0, 82, 5_000_000);
            for (int i = 0; i < 82; i++)
            {
                Assert.True(hits[i] > 100);
                Assert.True(gaps[i] <= 25000);
            }
        }

        [Fact]
        public void Scheduler_SweepsEveryKeyOnceAfterReset_InProfileOrder()
        {
            // addressed_poll_scheduler.cpp:241-248.
            var keys = Enumerable.Range(1, 20).Select(i => ((byte)(i * 3), (ushort)i)).ToArray();
            var scheduler = new AddressedPollScheduler(keys);
            scheduler.Reset(1000);
            var first = scheduler.BuildPlan(1000);
            Assert.Equal(keys.Take(9).Select(k => k.Item1), first.Ids.ToArray());
            // Keys never sampled stay in the sweep.
            Assert.Equal(keys.Take(9).Select(k => k.Item1), scheduler.BuildPlan(1000).Ids.ToArray());
            foreach (var k in keys.Take(9)) scheduler.OnSample(k.Item1, 9000, 0, 1000);
            Assert.Equal(keys.Skip(9).Take(9).Select(k => k.Item1), scheduler.BuildPlan(1000).Ids.ToArray());
            Assert.All(first.Classes.Take(first.Count), c => Assert.Equal(AddressedPollClass.Background, c));
        }

        // ── Identification and routes ──

        [Fact]
        public void AulaHeroMatches_TheExactFilter()
        {
            // aula_hero84he_backend.cpp:209-222.
            Assert.True(AddressedRoutes.MatchesAulaHero(Info(0x372E, 0x103E)));
            Assert.False(AddressedRoutes.MatchesAulaHero(Info(0x372E, 0x103F)));
            Assert.False(AddressedRoutes.MatchesAulaHero(Info(0x372F, 0x103E)));
            Assert.False(AddressedRoutes.MatchesAulaHero(Info(0x372E, 0x103E, page: 0xFF00)));
            Assert.False(AddressedRoutes.MatchesAulaHero(Info(0x372E, 0x103E, usage: 0x62)));
            Assert.False(AddressedRoutes.MatchesAulaHero(Info(0x372E, 0x103E, inLength: 65)));
            Assert.False(AddressedRoutes.MatchesAulaHero(Info(0x372E, 0x103E, outLength: 65)));
            Assert.False(AddressedRoutes.MatchesAulaHero(Info(0x372E, 0x103E, inReport9: false)));
            Assert.False(AddressedRoutes.MatchesAulaHero(Info(0x372E, 0x103E, outReport9: false)));
            Assert.False(AddressedRoutes.MatchesAulaHero(null));

            // No route here reads a HID string (aula_hero84he_backend.cpp:176-233,
            // addressed_analog_backend.cpp:284-289 is never called): a product
            // string neither admits nor refuses.
            var named = Info(0x372E, 0x103E);
            named.ProductString = "Keychron Q1 HE";
            Assert.True(AddressedRoutes.MatchesAulaHero(named));
            var wrongPid = Info(0x372E, 0x1040);
            wrongPid.ProductString = "AULA HERO84 HE";
            Assert.False(AddressedRoutes.MatchesAulaHero(wrongPid));
        }

        [Fact]
        public void AddressedIpiMatches_TheIpiIdentitiesWithTheFingerprint()
        {
            // addressed_analog_backend.cpp:304-309, 484-486.
            Assert.True(AddressedRoutes.MatchesAddressedIpi(Info(0x372E, 0x105C)));
            Assert.True(AddressedRoutes.MatchesAddressedIpi(Info(0x372E, 0x106C)));
            // No report ID is checked, and longer reports pass.
            Assert.True(AddressedRoutes.MatchesAddressedIpi(Info(0x372E, 0x105C, inLength: 65, outLength: 65,
                inReport9: false, outReport9: false)));
            Assert.False(AddressedRoutes.MatchesAddressedIpi(Info(0x372E, 0x10E0))); // Plus revision
            Assert.False(AddressedRoutes.MatchesAddressedIpi(Info(0x372E, 0x103E)));
            Assert.False(AddressedRoutes.MatchesAddressedIpi(Info(0x372F, 0x105C)));
            Assert.False(AddressedRoutes.MatchesAddressedIpi(Info(0x372E, 0x105C, page: 0xFF00)));
            Assert.False(AddressedRoutes.MatchesAddressedIpi(Info(0x372E, 0x105C, usage: 0x60)));
            Assert.False(AddressedRoutes.MatchesAddressedIpi(Info(0x372E, 0x105C, inLength: 63)));
            Assert.False(AddressedRoutes.MatchesAddressedIpi(Info(0x372E, 0x105C, outLength: 33)));
        }

        [Fact]
        public void AddressedGenericMatches_OnlyReportId9With64ByteReports_AndNoOwnedIdentity()
        {
            // HallJoy's fingerprint (addressed_analog_backend.cpp:304-309)
            // narrowed to the 09 frame's report shape (the safety deviation).
            Assert.True(AddressedRoutes.MatchesAddressedGeneric(Info(0x1234, 0x5678)));
            // HallJoy routes the IPI Plus revisions to its probe (spec 3.2).
            Assert.True(AddressedRoutes.MatchesAddressedGeneric(Info(0x372E, 0x10E0)));
            // QMK/VIA raw HID: no report ID, 33-byte reports. VIA's command 0x09
            // saves to EEPROM, so no 09 frame may reach it.
            Assert.False(AddressedRoutes.MatchesAddressedGeneric(Info(0x1234, 0x5678, inLength: 33, outLength: 33,
                inReport9: false, outReport9: false)));
            Assert.False(AddressedRoutes.MatchesAddressedGeneric(Info(0x1234, 0x5678, inLength: 65, outLength: 65,
                inReport9: false, outReport9: false)));
            Assert.False(AddressedRoutes.MatchesAddressedGeneric(Info(0x1234, 0x5678, outReport9: false)));
            Assert.False(AddressedRoutes.MatchesAddressedGeneric(Info(0x1234, 0x5678, inReport9: false)));
            Assert.False(AddressedRoutes.MatchesAddressedGeneric(Info(0x1234, 0x5678, inLength: 65)));
            Assert.False(AddressedRoutes.MatchesAddressedGeneric(Info(0x1234, 0x5678, page: 0xFF00)));
            Assert.False(AddressedRoutes.MatchesAddressedGeneric(Info(0x1234, 0x5678, usage: 0x62)));
            // Identities other routes own.
            Assert.False(AddressedRoutes.MatchesAddressedGeneric(Info(0x3434, 0x0B10)));
            Assert.False(AddressedRoutes.MatchesAddressedGeneric(Info(0x362D, 0x0610)));
            Assert.False(AddressedRoutes.MatchesAddressedGeneric(Info(0x373B, 0x1054)));
            Assert.False(AddressedRoutes.MatchesAddressedGeneric(Info(0x372E, 0x103E)));
            Assert.False(AddressedRoutes.MatchesAddressedGeneric(Info(0x372E, 0x105C)));
            Assert.False(AddressedRoutes.MatchesAddressedGeneric(Info(0x372E, 0x106C)));
        }

        [Fact]
        public void All_ListsTheRoutesInHallJoysPriority()
        {
            // native_analog_backends.def:24-26: HERO before the Addressed route.
            var all = AddressedRoutes.All;
            Assert.Equal(new[] { "halljoy-aula-hero", "halljoy-addressed-ipi", "halljoy-addressed-generic" },
                all.Select(r => r.Id));
            Assert.Equal(new[] { AnalogKeyboardProtocol.AulaHero, AnalogKeyboardProtocol.AddressedIpi,
                AnalogKeyboardProtocol.AddressedGeneric }, all.Select(r => r.Protocol));
            // Shared opens (addressed_analog_backend.cpp:280, aula_hero84he_backend.cpp:250).
            Assert.All(all, r =>
            {
                Assert.True(r.Writable);
                Assert.False(r.Exclusive);
                Assert.Null(r.Companion);
            });
            Assert.Equal(new[] { 256, 128, 128 }, all.Select(r => r.InputBuffers));
            Assert.IsType<AulaHeroSession>(all[0].CreateSession(Info(0x372E, 0x103E)));
            Assert.IsType<AddressedIpiSession>(all[1].CreateSession(Info(0x372E, 0x105C)));
            Assert.IsType<AddressedGenericSession>(all[2].CreateSession(Info(0x1234, 0x5678)));

            // Each identity class reaches exactly one route of the group.
            foreach (var (info, id) in new[]
                     {
                         (Info(0x372E, 0x103E), "halljoy-aula-hero"),
                         (Info(0x372E, 0x105C), "halljoy-addressed-ipi"),
                         (Info(0x372E, 0x106C), "halljoy-addressed-ipi"),
                         (Info(0x1234, 0x5678), "halljoy-addressed-generic"),
                     })
                Assert.Equal(new[] { id }, all.Where(r => r.Matches(info)).Select(r => r.Id));
        }

        // ── Tables ──

        [Fact]
        public void IpiCatalog_HallJoysEightModels()
        {
            // generated/ipi_models.h:8-25.
            var models = AddressedRoutes.IpiModels;
            Assert.Equal(new ulong[] { 0x11000000002C, 0x110000000013, 0x110000000023, 0x110000000010,
                0x120000000003, 0x11000000001F, 0x110000000040, 0x110000000006 }, models.Select(m => m.Uuid));
            Assert.Equal(new[] { 82, 82, 67, 67, 67, 67, 82, 68 }, models.Select(m => m.Order.Length));
            Assert.Equal(new[] { "IPI QBZ75", "IPI Aurora 75", "IPI QBZ65", "IPI AURORA65", "IPI AURORA65W",
                "IPI RAIN65", "IPI Aurora75 PRO", "IPI flash68" }, models.Select(m => m.Name));
            Assert.Null(AddressedRoutes.FindIpiModel(0x11000000005B));

            var ids82 = Enumerable.Range(1, 72).Concat(Enumerable.Range(74, 4)).Append(95).Concat(Enumerable.Range(99, 5));
            var ids67 = new[] { 1 }.Concat(Enumerable.Range(15, 58)).Concat(Enumerable.Range(74, 4))
                .Concat(new[] { 98, 99, 102, 103 });
            var ids68 = new[] { 1 }.Concat(Enumerable.Range(15, 63)).Concat(new[] { 98, 99, 102, 103 });
            Assert.Equal(ids82.Select(i => (ushort)i), AddressedRoutes.FindIpiModel(0x11000000002C).Order);
            Assert.Equal(ids67.Select(i => (ushort)i), AddressedRoutes.FindIpiModel(0x120000000003).Order);
            Assert.Equal(ids68.Select(i => (ushort)i), AddressedRoutes.FindIpiModel(0x110000000006).Order);

            // factoryHids spot checks (generated/ipi_models.h:7, spec A.1).
            Assert.All(models, m =>
            {
                Assert.Equal(AnalogKeyCodes.Escape, m.Table[1]);
                Assert.Equal(AnalogKeyCodes.W, m.Table[30]);
                Assert.Equal(AnalogKeyCodes.RShift, m.Table[66]);
                Assert.Equal(AnalogKeyCodes.LAlt, m.Table[69]);
                Assert.Equal(AnalogKeyCodes.RAlt, m.Table[71]);
                Assert.Equal(AnalogKeyCodes.Fn, m.Table[72]);
                Assert.Equal(AnalogKeyCodes.Delete, m.Table[99]);
                Assert.Equal(AnalogKeyCodes.PageUp, m.Table[102]);
            });
            var flash68 = AddressedRoutes.FindIpiModel(0x110000000006);
            Assert.Equal(AnalogKeyCodes.RCtrl, flash68.Table[73]);
            Assert.Equal(68, AnalogKeyboardData.KeysOf(flash68.Table).Length);
        }

        [Fact]
        public void AulaHeroCatalog_HallJoysSevenUuids()
        {
            // aula_hero_family.h:178-188 and the three tables
            // (aula_hero84he_factory.h:8-91, aula_hero_family.h:6-176).
            var models = AddressedRoutes.AulaHeroModels;
            Assert.Equal(new ulong[] { 0x110000000005, 0x110000000003, 0x110000000014, 0x11000000000F,
                0x110000000015, 0x110000000012, 0x11000000003F }, models.Select(m => m.Uuid));
            Assert.Equal(new[] { "AULA HERO84 HE", "AULA HERO 68 HE", "AULA HERO 68 Air", "AULA HERO 68 MINI",
                "AULA WIN 68 HE Ultra", "AULA HERO 99 HE", "AULA HERO 99 HE" }, models.Select(m => m.Name));
            Assert.Equal(new[] { 84, 68, 68, 68, 68, 101, 101 }, models.Select(m => m.Order.Length));

            var hero84 = AddressedRoutes.FindAulaHeroModel(Hero84);
            Assert.Equal(new ushort[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 95, 98, 99 }, hero84.Order.Take(16));
            Assert.Equal(AnalogKeyCodes.Fn, hero84.Table[72]);
            Assert.Equal(AnalogKeyCodes.RCtrl, hero84.Table[73]);
            Assert.Equal(AnalogKeyCodes.PrintScreen, hero84.Table[95]);
            var hero99 = AddressedRoutes.FindAulaHeroModel(0x11000000003F);
            Assert.Equal(AnalogKeyCodes.NumLock, hero99.Table[78]);
            Assert.Equal(AnalogKeyCodes.ScrollLock, hero99.Table[96]);
            Assert.Equal(0, hero99.Table[71]); // HERO 99 has no Right Alt position
            var hero68 = AddressedRoutes.FindAulaHeroModel(Hero68);
            Assert.Equal(0, hero68.Table[2]);
            Assert.Equal(AnalogKeyCodes.Insert, hero68.Table[98]);
            foreach (var m in models)
            {
                Assert.All(AulaHeroProtocol.MovementPositions, p => Assert.NotEqual(0, m.Table[p]));
                Assert.Equal(m.Order.Length, AnalogKeyboardData.KeysOf(m.Table).Length);
            }
        }

        [Fact]
        public void CanonicalTable_HallJoysFallback()
        {
            // kCanonicalKeys (addressed_analog_backend.cpp:67-89), spec A.2.
            var order = AddressedRoutes.CanonicalOrder;
            var table = AddressedRoutes.CanonicalTable;
            Assert.Equal(82, order.Length);
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 0x5F, 0x64, 0x63 }, order.Take(16));
            Assert.Equal(new[] { 0x48, 0x4C, 0x29, 0x41, 0x4A, 0x67, 0x4B, 0x4D }, order.Skip(74));
            Assert.Equal(AnalogKeyCodes.W, table[0x1E]);
            Assert.Equal(AnalogKeyCodes.RAlt, table[0x45]);
            Assert.Equal(0, table[0x42]);
            Assert.Equal(0, table[0x47]);
            Assert.Equal(0, table[0x48]);
            Assert.Equal(AnalogKeyCodes.PageUp, table[0x63]);
            Assert.Equal(AnalogKeyCodes.Delete, table[0x64]);
            Assert.Equal(79, order.Count(id => table[id] != 0));
        }
    }
}
