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
    /// HallJoy's AULA MINI 60 and W669 routes (issue #468), pinned against
    /// byte fixtures built from HallJoy's source (AGPL-3.0, commit 378f9fe)
    /// and its own test vectors. Citations name files under
    /// src/HallJoyProject/HallJoy, and tests/ for src/HallJoyProject/tests.
    /// Fixtures are what Windows hands ReadFile: byte 0 is the report ID, 0
    /// for the MINI 60, which numbers no reports, and 1 for W669.
    /// </summary>
    public class AnalogKeyboardAulaEventsTests
    {
        // ── Shared scaffolding ──

        /// <summary>The scripted keyboard with a clock that moves only while a
        /// read waits, so every timeout in a session plays out at once.</summary>
        private sealed class ClockedTransport : IAnalogKeyboardTransport
        {
            public readonly AnalogKeyboardTestTransport Device = new();
            public long Now = 10_000;

            /// <summary>Writes that fail: kind ("out" or "ctl") and report.</summary>
            public Func<string, byte[], bool> FailWrite = (kind, report) => false;

            /// <summary>Every write attempt in order, failed ones included.</summary>
            public readonly List<(string Kind, byte[] Data, bool Ok)> Attempts = new();

            private readonly byte[] _drain = new byte[2048];

            public bool Send(byte[] report) => Write("out", report, Device.Send);
            public bool SendOutputReport(byte[] report) => Write("ctl", report, Device.SendOutputReport);

            private bool Write(string kind, byte[] report, Func<byte[], bool> send)
            {
                bool ok = !FailWrite(kind, report) && send(report);
                Attempts.Add((kind, (byte[])report.Clone(), ok));
                return ok;
            }

            public int Receive(byte[] buffer, int timeoutMs)
            {
                int n = Device.Receive(buffer, timeoutMs);
                if (n == 0) Now += Math.Max(1, timeoutMs);
                return n;
            }

            /// <summary>Drops what is queued, as HidD_FlushQueue does.</summary>
            public void DiscardStale()
            {
                Device.DiscardStale();
                while (Device.Receive(_drain, 0) > 0) { }
            }

            public bool SetFeature(byte[] report) => Device.SetFeature(report);
            public int GetFeature(byte[] buffer) => Device.GetFeature(buffer);
            public int InputLength => Device.InputLength;
            public int OutputLength => Device.OutputLength;
            public int FeatureLength => Device.FeatureLength;
        }

        private static void Put16(byte[] r, int at, int value)
        {
            r[at] = (byte)value;
            r[at + 1] = (byte)(value >> 8);
        }

        private static AnalogKeyboardDeviceInfo Info(ushort vid, ushort pid, ushort page, ushort usage,
            int inputLength, int outputLength, string path = null, string product = "")
            => new()
            {
                VendorId = vid,
                ProductId = pid,
                UsagePage = page,
                Usage = usage,
                InputReportLength = (ushort)inputLength,
                OutputReportLength = (ushort)outputLength,
                Path = path ?? $@"\\?\hid#vid_{vid:x4}&pid_{pid:x4}&mi_02#7&1a2b3c4d&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
                ProductString = product,
            };

        private static byte[] Hex(string hex)
        {
            var parts = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Select(p => Convert.ToByte(p, 16)).ToArray();
        }

        [Fact]
        public void Routes_AreInHallJoysOrder_WithHallJoysHandles()
        {
            // native_analog_backends.def:17 lists the MINI 60 and :40 W669.
            var all = AulaEventsRoutes.All;
            Assert.Equal(2, all.Count);
            Assert.Equal(AnalogKeyboardProtocol.AulaMini60, all[0].Protocol);
            Assert.Equal(AnalogKeyboardProtocol.AulaW669, all[1].Protocol);
            Assert.Equal(AulaEventsRoutes.Mini60Id, all[0].Id);
            Assert.Equal(AulaEventsRoutes.W669Id, all[1].Id);
            // aula_mini60_diagnostic.cpp:116-118 and aula_w669_backend.cpp:231-239:
            // shared read-write handles, 512 and 256 input buffers.
            Assert.True(all[0].Writable && !all[0].Exclusive);
            Assert.True(all[1].Writable && !all[1].Exclusive);
            Assert.Equal(512, all[0].InputBuffers);
            Assert.Equal(256, all[1].InputBuffers);
            Assert.Null(all[0].Companion);
            Assert.Null(all[1].Companion);
        }

        // ── MINI 60: identification ──

        [Theory]
        [InlineData(0x8032, "AULA MINI 60 HE")]
        [InlineData(0x80A2, "AULA MINI 60 HE Pro")]
        [InlineData(0x80A1, "AULA MINI 60 HE MAX")]
        public void Mini60_Matches_TheThreeWiredModels(int pid, string name)
        {
            // aula_mini60_diagnostic.cpp:95-108: path, VID 0C45, a supported
            // PID, FF68:0061 and 65-byte input and output reports.
            var info = Info(0x0C45, (ushort)pid, 0xFF68, 0x0061, 65, 65);
            Assert.True(AulaMini60Protocol.Matches(info));
            Assert.Equal(name, AulaEventsRoutes.All[0].Name(info));
        }

        public static IEnumerable<object[]> Mini60NearMisses()
        {
            // aula_mini60_diagnostic.cpp:104: the 2.4 GHz receivers are never commanded.
            yield return new object[] { "receiver FEFE", Info(0x0C45, 0xFEFE, 0xFF68, 0x0061, 65, 65) };
            yield return new object[] { "receiver FEFC", Info(0x0C45, 0xFEFC, 0xFF68, 0x0061, 65, 65) };
            // aula_mini60_diagnostic.cpp:500: the alternate PRO PID is not supported.
            yield return new object[] { "PID 80B2", Info(0x0C45, 0x80B2, 0xFF68, 0x0061, 65, 65) };
            yield return new object[] { "another VID", Info(0x0C46, 0x80A2, 0xFF68, 0x0061, 65, 65) };
            // aula_mini60_diagnostic.cpp:108: usage and both lengths are exact.
            yield return new object[] { "usage page FF60", Info(0x0C45, 0x80A2, 0xFF60, 0x0061, 65, 65) };
            yield return new object[] { "usage 62", Info(0x0C45, 0x80A2, 0xFF68, 0x0062, 65, 65) };
            yield return new object[] { "input 64", Info(0x0C45, 0x80A2, 0xFF68, 0x0061, 64, 65) };
            yield return new object[] { "output 33", Info(0x0C45, 0x80A2, 0xFF68, 0x0061, 65, 33) };
            // aula_mini60_diagnostic.cpp:93-95: only a USB path names vid_0c45&pid_.
            yield return new object[] { "Bluetooth path", Info(0x0C45, 0x80A2, 0xFF68, 0x0061, 65, 65,
                @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&00020c45_pid&80a2&col03#9&1&0002#{4d1e55b2-f16f-11cf-88cb-001111000030}") };
            yield return new object[] { "path names another PID", Info(0x0C45, 0x80A2, 0xFF68, 0x0061, 65, 65,
                @"\\?\hid#vid_0c45&pid_80a1&mi_02#7&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}") };
        }

        [Theory]
        [MemberData(nameof(Mini60NearMisses))]
        public void Mini60_Matches_RejectsNearMisses(string why, AnalogKeyboardDeviceInfo info)
        {
            Assert.False(AulaMini60Protocol.Matches(info), why);
            Assert.False(AulaEventsRoutes.All[0].Matches(info), why);
        }

        [Fact]
        public void Mini60_Matches_RefusesAKeyboardWithTwoEligibleCollections()
        {
            // aula_mini60_diagnostic.cpp:188-191: more than one eligible
            // collection and HallJoy sends no command at all.
            var a = Info(0x0C45, 0x80A2, 0xFF68, 0x0061, 65, 65);
            var twin = Info(0x0C45, 0x80A2, 0xFF68, 0x0061, 65, 65,
                @"\\?\hid#vid_0c45&pid_80a2&mi_03#7&2&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}");
            var keyboard = Info(0x0C45, 0x80A2, 0x0001, 0x0006, 9, 2);
            a.Siblings = new[] { keyboard };
            Assert.True(AulaMini60Protocol.Matches(a));
            a.Siblings = new[] { keyboard, twin };
            Assert.False(AulaMini60Protocol.Matches(a));
        }

        // ── MINI 60: frames ──

        [Fact]
        public void Mini60_Requests_MatchHallJoysFrames()
        {
            // aula_mini60_diagnostic_protocol.h:8-16, the frames the spec
            // compiled from HallJoy's builder.
            void Frame(byte[] actual, string head)
            {
                var expected = new byte[65];
                Hex(head).CopyTo(expected, 0);
                Assert.Equal(expected, actual);
            }
            Frame(AulaMini60Protocol.Request(0x10, 56), "00 AA 10 38 00 00 00 01");
            Frame(AulaMini60Protocol.Request(0x12, 56, 0), "00 AA 12 38 00 00 00 00");
            Frame(AulaMini60Protocol.Request(0x12, 56, 56), "00 AA 12 38 38 00 00 00");
            Frame(AulaMini60Protocol.Request(0x12, 56, 112), "00 AA 12 38 70 00 00 00");
            Frame(AulaMini60Protocol.Request(0x12, 56, 168), "00 AA 12 38 A8 00 00 00");
            Frame(AulaMini60Protocol.Request(0x12, 56, 224), "00 AA 12 38 E0 00 00 00");
            Frame(AulaMini60Protocol.Request(0x12, 56, 280), "00 AA 12 38 18 01 00 00");
            Frame(AulaMini60Protocol.Request(0x12, 56, 336), "00 AA 12 38 50 01 00 00");
            Frame(AulaMini60Protocol.Request(0x12, 56, 392), "00 AA 12 38 88 01 00 00");
            Frame(AulaMini60Protocol.Request(0x12, 56, 448), "00 AA 12 38 C0 01 00 00");
            Frame(AulaMini60Protocol.Request(0x12, 8, 504), "00 AA 12 08 F8 01 00 01");
            Frame(AulaMini60Protocol.Request(0x66), "00 AA 66 00 00 00 00 01");
            Frame(AulaMini60Protocol.Request(0x67), "00 AA 67 00 00 00 00 01");
        }

        [Fact]
        public void Mini60_Requests_OnlyFourCommandsCanBeBuilt()
        {
            // aula_mini60_diagnostic.cpp:544-546: every other command, the
            // calibration pair 64/65 among them, builds nothing.
            for (int command = 0; command < 256; command++)
            {
                bool built = AulaMini60Protocol.Request(command) != null;
                Assert.Equal(command == 0x66 || command == 0x67, built);
            }
            Assert.Null(AulaMini60Protocol.Request(0x64));
            Assert.Null(AulaMini60Protocol.Request(0x65));
            Assert.Null(AulaMini60Protocol.Request(0x10, 55));
            Assert.Null(AulaMini60Protocol.Request(0x10, 56, 56));
            Assert.Null(AulaMini60Protocol.Request(0x12, 56, 500));
            Assert.Null(AulaMini60Protocol.Request(0x12, 57, 0));
            Assert.Null(AulaMini60Protocol.Request(0x66, 0, 1));
            Assert.Equal(0, AulaMini60Protocol.Request(0x12, 56, 56)[7]);
            Assert.Equal(1, AulaMini60Protocol.Request(0x12, 8, 504)[7]);
        }

        [Fact]
        public void Mini60_Sample_DecodesHallJoysSelfTestReport()
        {
            // aula_mini60_diagnostic.cpp:547-551.
            var report = new byte[65];
            report[1] = 0x55; report[2] = 0xFB; report[3] = 7;
            report[5] = 0x98; report[6] = 8; report[7] = 0x6C; report[8] = 0x87; report[9] = 0x20;
            report[10] = 8; report[11] = 170; report[13] = 34;
            Assert.True(AulaMini60Protocol.TryDecodeSample(report, out var s));
            Assert.Equal(7, s.Key);
            Assert.Equal(1900, s.Low);
            Assert.Equal(0x0898, s.High);
            Assert.Equal(0x0820, s.Adc);
            Assert.Equal(170, s.Travel);
            Assert.Equal(34, s.Stroke);
            // A 64-byte read without the report ID decodes the same.
            Assert.True(AulaMini60Protocol.TryDecodeSample(report.AsSpan(1), out var bare));
            Assert.Equal(s, bare);

            Assert.False(AulaMini60Protocol.TryDecodeSample(report.AsSpan(0, 14), out _));
            report[0] = 1;
            Assert.False(AulaMini60Protocol.TryDecodeSample(report, out _));
            report[0] = 0;
            report[3] = 126;
            Assert.False(AulaMini60Protocol.TryDecodeSample(report, out _));
            report[3] = 7;
            report[4] = 2;
            Assert.False(AulaMini60Protocol.TryDecodeSample(report, out _));
        }

        [Fact]
        public void Mini60_Reply_MatchesCommandLengthAndOffset()
        {
            // aula_mini60_diagnostic.cpp:556-557.
            var info = new byte[65];
            info[1] = 0x55; info[2] = 0x10; info[3] = 56;
            var payload = AulaMini60Protocol.Payload(info);
            Assert.True(AulaMini60Protocol.IsReply(payload, 0x10, 56, 0));
            Assert.False(AulaMini60Protocol.IsReply(payload, 0x10, 55, 0));
            Assert.False(AulaMini60Protocol.IsReply(payload, 0x10, 56, 56));
            // aula_mini60_diagnostic_protocol.h:18-22: the payload starts 55.
            info[1] = 0x56;
            Assert.True(AulaMini60Protocol.Payload(info).IsEmpty);
        }

        [Fact]
        public void Mini60_Assigned_FollowsHallJoysVectors()
        {
            // tests/aula_mini60_native_model_test.cpp:16-23.
            int A(int position, params byte[] record) => AulaMini60Protocol.Assigned(position, record);
            Assert.Equal(41, A(0, 0, 0, 0, 0));
            Assert.Equal(0x409, A(85, 0, 0, 0, 0));
            Assert.Equal(0, A(125, 0, 0, 0, 0));
            Assert.Equal(0, A(126, 0, 0, 0, 0));
            Assert.Equal(26, A(49, 2, 0, 26, 0));
            Assert.Equal(225, A(49, 2, 2, 0, 0));
            Assert.Equal(0, A(49, 2, 3, 0, 0));
            Assert.Equal(0, A(49, 2, 1, 26, 0));
            Assert.Equal(0x409, A(49, 2, 0, 175, 0));
            Assert.Equal(0x409, A(85, 2, 0, 175, 0));
            Assert.Equal(0, A(85, 2, 0, 175, 1));
            Assert.Equal(0, A(49, 6, 0, 0, 0));
            // aula_mini60_diagnostic.cpp:531-536 and the spec's compiled checks.
            Assert.Equal(224, A(49, 2, 1, 0, 0));
            Assert.Equal(231, A(49, 2, 0x80, 0, 0));
            Assert.Equal(0, A(49, 3, 0, 0x1A, 0));
            Assert.Equal(0, A(49, 2, 0, 0, 0));
            Assert.Equal(0, A(1, 2, 0, 26, 0)); // no key at index 1
        }

        [Fact]
        public void Mini60_Milli_FollowsHallJoysVectors()
        {
            // tests/aula_mini60_native_model_test.cpp:9-10 and
            // aula_mini60_diagnostic.cpp:528.
            Assert.Equal(0, AulaMini60Protocol.Milli(0, 34));
            Assert.Equal(250, AulaMini60Protocol.Milli(85, 34));
            Assert.Equal(500, AulaMini60Protocol.Milli(170, 34));
            Assert.Equal(1000, AulaMini60Protocol.Milli(340, 34));
            Assert.Equal(1000, AulaMini60Protocol.Milli(369, 34));
            Assert.Equal(1000, AulaMini60Protocol.Milli(370, 34));
            Assert.Equal(0, AulaMini60Protocol.Milli(100, 0));
            Assert.Equal(0, AulaMini60Protocol.Milli(170, 0));
            Assert.Equal(0, AulaMini60Protocol.Milli(100, 65535));
            Assert.Equal(0, AulaMini60Protocol.Milli(65535, 34));
            Assert.Equal(0, AulaMini60Protocol.Milli(1001, 34));
            Assert.Equal(0, AulaMini60Protocol.Milli(100, 9));
            Assert.Equal(0, AulaMini60Protocol.Milli(100, 51));
        }

        [Fact]
        public void Mini60_FactoryTable_Has61UniqueKeys()
        {
            // tests/aula_mini60_native_model_test.cpp:7-8.
            var table = AulaMini60Protocol.FactoryTable;
            Assert.Equal(126, table.Length);
            var keys = table.Where(code => code != 0).ToArray();
            Assert.Equal(61, keys.Length);
            Assert.Equal(61, keys.Distinct().Count());
            Assert.Equal(AnalogKeyCodes.Escape, table[0]);
            Assert.Equal(AnalogKeyCodes.W, table[34]);
            Assert.Equal(AnalogKeyCodes.A, table[49]);
            Assert.Equal(AnalogKeyCodes.S, table[50]);
            Assert.Equal(AnalogKeyCodes.D, table[51]);
            Assert.Equal(AnalogKeyCodes.Space, table[83]);
            Assert.Equal(AnalogKeyCodes.Fn, table[85]);
            Assert.Equal(AnalogKeyCodes.ContextMenu, table[86]);
            Assert.Equal(AnalogKeyCodes.Backspace, table[92]);
            Assert.Equal(61, AnalogKeyboardData.KeysOf(table).Length);
        }

        // ── MINI 60: sessions ──

        /// <summary>A MINI 60 as HallJoy's worker sees it: answers per route,
        /// a 512-byte assignment table of type-0 records, and hooks for the
        /// cases that go wrong.</summary>
        private sealed class Mini60Keyboard
        {
            public int Vid = 0x0C45, Pid = 0x80A2, Manufacturer = 0x0166, Product = 0x110C;
            public readonly byte[] Table = new byte[512];
            public Func<string, int, bool> Answers = (kind, command) => true;
            public Func<int, bool> ChunkAnswered = offset => true;
            public Func<byte[]> SimulationAnswer = () => Mini60Ack(0x66);

            public void Attach(ClockedTransport io)
            {
                io.Device.OnSend = req => Respond("out", req);
                io.Device.OnSendOutputReport = req => Respond("ctl", req);
            }

            private IEnumerable<byte[]> Respond(string kind, byte[] req)
            {
                if (req.Length != 65 || req[1] != 0xAA || !Answers(kind, req[2])) yield break;
                int length = req[3], offset = req[4] | (req[5] << 8);
                switch (req[2])
                {
                    case 0x10:
                        yield return Mini60DeviceInfo(Vid, Pid, Manufacturer, Product);
                        break;
                    case 0x12:
                        if (ChunkAnswered(offset)) yield return Mini60Chunk(offset, length, Table);
                        break;
                    case 0x66:
                        var answer = SimulationAnswer();
                        if (answer != null) yield return answer;
                        break;
                    case 0x67:
                        yield return Mini60Ack(0x67);
                        break;
                }
            }
        }

        private static byte[] Mini60DeviceInfo(int vid, int pid, int manufacturer, int product)
        {
            // Reply header 55 10 38 00 00, fields at payload 12, 14, 16, 20,
            // 22 (aula_mini60_diagnostic.cpp:207-210).
            var r = new byte[65];
            r[1] = 0x55; r[2] = 0x10; r[3] = 56;
            Put16(r, 1 + 12, vid);
            Put16(r, 1 + 14, pid);
            r[1 + 16] = 0x52; r[1 + 17] = 0x01;
            Put16(r, 1 + 20, manufacturer);
            Put16(r, 1 + 22, product);
            return r;
        }

        private static byte[] Mini60Chunk(int offset, int length, byte[] table)
        {
            // Data from payload byte 8 (aula_mini60_diagnostic.cpp:224).
            var r = new byte[65];
            r[1] = 0x55; r[2] = 0x12; r[3] = (byte)length;
            Put16(r, 1 + 3, offset);
            Array.Copy(table, offset, r, 1 + 8, length);
            return r;
        }

        private static byte[] Mini60Ack(int command)
        {
            var r = new byte[65];
            r[1] = 0x55; r[2] = (byte)command;
            return r;
        }

        private static byte[] Mini60Sample(int key, int travel, int stroke = 34)
        {
            var r = new byte[65];
            r[1] = 0x55; r[2] = 0xFB; r[3] = (byte)key;
            Put16(r, 1 + 10, travel);
            Put16(r, 1 + 12, stroke);
            return r;
        }

        private static (ClockedTransport io, Mini60Keyboard kb, AulaMini60Session session) Mini60(ushort pid = 0x80A2)
        {
            var io = new ClockedTransport();
            var kb = new Mini60Keyboard { Pid = pid };
            kb.Attach(io);
            var session = new AulaMini60Session(pid, () => io.Now);
            return (io, kb, session);
        }

        private static byte[][] Commands(ClockedTransport io, string kind, int command)
            => io.Attempts.Where(a => a.Kind == kind && a.Data[2] == command).Select(a => a.Data).ToArray();

        [Fact]
        public void Mini60_Start_ProvesReadsTheTable_AndEntersSimulationMode()
        {
            // aula_mini60_diagnostic.cpp:202-245: device info, ten table
            // reads, simulation mode on, all WriteFile while it answers.
            var (io, _, session) = Mini60();
            Assert.True(session.Start(io));
            var sent = io.Attempts.Select(a => a.Kind + " " + BitConverter.ToString(a.Data, 0, 8)).ToArray();
            var expected = new List<string> { "out 00-AA-10-38-00-00-00-01" };
            for (int offset = 0; offset < 504; offset += 56)
                expected.Add("out " + BitConverter.ToString(AulaMini60Protocol.Request(0x12, 56, offset), 0, 8));
            expected.Add("out 00-AA-12-08-F8-01-00-01");
            expected.Add("out 00-AA-66-00-00-00-00-01");
            Assert.Equal(expected, sent);
            Assert.All(io.Attempts, a => Assert.Equal(65, a.Data.Length));
            Assert.Equal("AULA MINI 60 HE Pro", session.ModelName);
            Assert.True(session.Started);
            Assert.True(session.SimulationModeSent);
            Assert.False(session.UsesOutputReports);
            // Every factory key keeps its place when the table says type 0.
            Assert.Equal(AnalogKeyboardData.KeysOf(AulaMini60Protocol.FactoryTable), session.KeyOrder);
            Assert.Equal(AnalogKeyCodes.Escape, session.KeyOrder[0]);
            Assert.Contains(AnalogKeyCodes.Fn, session.KeyOrder);
        }

        public static IEnumerable<object[]> Mini60WrongIdentities()
        {
            // aula_mini60_native_model.h:14-19 and aula_mini60_diagnostic.cpp:497-499.
            yield return new object[] { "manufacturer 0", 0x80A2, 0x0C45, 0x80A2, 0x0000, 0x110C };
            yield return new object[] { "another PID", 0x80A2, 0x0C45, 0x80A1, 0x0166, 0x110C };
            yield return new object[] { "another VID", 0x80A2, 0x0C46, 0x80A2, 0x0166, 0x110C };
            yield return new object[] { "PRO product", 0x80A2, 0x0C45, 0x80A2, 0x0166, 0x1234 };
        }

        [Theory]
        [MemberData(nameof(Mini60WrongIdentities))]
        public void Mini60_Start_WrongIdentity_SendsNothingMore(string why, int descriptorPid, int vid, int pid,
            int manufacturer, int product)
        {
            // aula_mini60_diagnostic.cpp:208: a mismatch stops every command.
            var (io, kb, session) = Mini60((ushort)descriptorPid);
            kb.Vid = vid; kb.Pid = pid; kb.Manufacturer = manufacturer; kb.Product = product;
            Assert.False(session.Start(io), why);
            Assert.Single(io.Attempts);
            Assert.Equal(0x10, io.Attempts[0].Data[2]);
            session.Stop(io);
            Assert.Single(io.Attempts);
        }

        [Theory]
        [InlineData(0x8032)]
        [InlineData(0x80A1)]
        public void Mini60_Start_BaseAndMaxProduct_IsNotChecked(int pid)
        {
            // aula_mini60_native_model.h:15-18: the base and MAX product comes
            // from an MCU register.
            var (io, kb, session) = Mini60((ushort)pid);
            kb.Product = 0x1234;
            Assert.True(session.Start(io));
            Assert.Equal(AulaMini60Protocol.Model((ushort)pid).Name, session.ModelName);
        }

        [Fact]
        public void Mini60_Start_SilentWriteFile_FallsBackToOutputReports()
        {
            // aula_mini60_diagnostic.cpp:203-215: two WriteFile tries, then
            // HidD_SetOutputReport, which the rest of the session keeps.
            var (io, kb, session) = Mini60();
            kb.Answers = (kind, command) => kind == "ctl";
            Assert.True(session.Start(io));
            Assert.True(session.UsesOutputReports);
            var kinds = io.Attempts.Select(a => a.Kind + ":" + a.Data[2].ToString("X2")).ToArray();
            Assert.Equal(new[] { "out:10", "out:10", "ctl:10" }, kinds.Take(3).ToArray());
            Assert.All(io.Attempts.Skip(2), a => Assert.Equal("ctl", a.Kind));
            Assert.Equal(10, Commands(io, "ctl", 0x12).Length);
            Assert.Single(Commands(io, "ctl", 0x66));
        }

        [Fact]
        public void Mini60_Start_IncompleteTable_RetriesTwice_ThenGivesUpWithoutSimulationMode()
        {
            // aula_mini60_diagnostic.cpp:194, 219-241: every byte must arrive,
            // a missing chunk restarts the attempt, three attempts in all.
            var (io, kb, session) = Mini60();
            kb.ChunkAnswered = offset => offset != 224;
            Assert.False(session.Start(io));
            Assert.Equal(3, Commands(io, "out", 0x10).Length);
            // Nine answered chunks and two tries of the silent one, per attempt.
            Assert.Equal(3 * 11, Commands(io, "out", 0x12).Length);
            Assert.Empty(Commands(io, "out", 0x66));
            Assert.Empty(Commands(io, "ctl", 0x66));
            Assert.Equal(2, io.Device.Discards);
            session.Stop(io);
            Assert.Empty(Commands(io, "out", 0x67));
        }

        [Fact]
        public void Mini60_Start_UnansweredSimulationMode_IsSentTwice_AndTheStreamNeedsProof()
        {
            // aula_mini60_diagnostic.cpp:243-244: no acknowledgement and no
            // sample, so 0x66 goes out again. :249, 254: no proof within 2 s
            // ends the session.
            var (io, kb, session) = Mini60();
            kb.SimulationAnswer = () => null;
            kb.Answers = (kind, command) => command != 0x67;
            Assert.True(session.Start(io));
            Assert.Equal(2, Commands(io, "out", 0x66).Length);
            Assert.False(session.Started);

            long startedAt = io.Now;
            var output = new AnalogKeyInputState();
            var result = AnalogPollResult.Idle;
            for (int i = 0; i < 1000 && result != AnalogPollResult.Failed; i++)
                result = session.Pass(io, output, null);
            Assert.Equal(AnalogPollResult.Failed, result);
            Assert.Equal(2000, io.Now - startedAt);

            // aula_mini60_diagnostic.cpp:165-172, 465: the cleanup is not
            // acknowledged, so the cleanup worker's round sends 0x67 again.
            session.Stop(io);
            Assert.Equal(2, Commands(io, "out", 0x67).Length);
            Assert.False(session.SimulationModeSent);
        }

        [Fact]
        public void Mini60_Start_SampleDuringTheModeRequest_IsStartProof()
        {
            // aula_mini60_diagnostic.cpp:149-154, 244: a stored sample proves
            // the stream and stops a second 0x66.
            var (io, kb, session) = Mini60();
            kb.SimulationAnswer = () => Mini60Sample(34, 170);
            Assert.True(session.Start(io));
            Assert.True(session.Started);
            Assert.Single(Commands(io, "out", 0x66));
        }

        [Fact]
        public void Mini60_Pass_PublishesSamples_AndReleasesKeysAfter50ms()
        {
            // tests/aula_mini60_native_model_test.cpp:11-13: a sample reads
            // 50 ms after it arrived and 0 a millisecond later, and a zero
            // travel reads 0 at once.
            var (io, _, session) = Mini60();
            Assert.True(session.Start(io));
            var output = new AnalogKeyInputState();
            long t0 = io.Now;

            io.Device.QueueInput(Mini60Sample(34, 170));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));

            io.Now = t0 + 40;
            io.Device.QueueInput(Mini60Sample(49, 340));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.A));

            // At exactly 50 ms W still reads. A report that is not a sample
            // makes the pass read without waiting.
            io.Now = t0 + 50;
            io.Device.QueueInput(Mini60Ack(0x10));
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));

            // The pass waits exactly until W expires.
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(t0 + 51, io.Now);
            Assert.Equal(0f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.A));

            io.Device.QueueInput(Mini60Sample(49, 0));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0, output.Count);

            // Fn is HFD key 85, published as 0x409 with its own depth.
            io.Device.QueueInput(Mini60Sample(85, 85));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.25f, output.Get(AnalogKeyCodes.Fn));

            // Index 1 has no key, so its sample changes nothing.
            io.Device.QueueInput(Mini60Sample(1, 340));
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            Assert.Single(output.Codes.Take(output.Count));

            // A quiet keyboard with nothing held is idle, not a miss.
            io.Now += 100;
            output.ResetForReuse();
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
        }

        [Fact]
        public void Mini60_Pass_RemappedPositions_ReadTheDeepest()
        {
            // aula_mini60_diagnostic.cpp:521-526, HallJoy's alias check: key
            // index 49 assigned W (02 00 1A 00) publishes W, and A is gone.
            var (io, kb, session) = Mini60();
            kb.Table[49 * 4] = 2;
            kb.Table[49 * 4 + 2] = 0x1A;
            Assert.True(session.Start(io));
            Assert.Equal(AnalogKeyCodes.W, session.AssignedKeys[49]);
            Assert.DoesNotContain(AnalogKeyCodes.A, session.KeyOrder);
            Assert.Single(session.KeyOrder, code => code == AnalogKeyCodes.W);

            var output = new AnalogKeyInputState();
            io.Device.QueueInput(Mini60Sample(34, 170));
            session.Pass(io, output, null);
            io.Device.QueueInput(Mini60Sample(49, 340));
            session.Pass(io, output, null);
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.A));

            io.Device.QueueInput(Mini60Sample(49, 0));
            session.Pass(io, output, null);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
        }

        [Fact]
        public void Mini60_Pass_DeviceGone_FailsAndReleasesEveryKey()
        {
            // aula_mini60_diagnostic.cpp:130-139, 250: a failed read breaks
            // the transport and ends the stream.
            var (io, _, session) = Mini60();
            Assert.True(session.Start(io));
            var output = new AnalogKeyInputState();
            io.Device.QueueInput(Mini60Sample(34, 340));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            io.Device.Gone = true;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
            Assert.Equal(0, output.Count);
        }

        [Fact]
        public void Mini60_Stop_AcknowledgedCleanup_SendsSimulationOffOnce()
        {
            // aula_mini60_diagnostic.cpp:165-172, 301: an acknowledged cleanup
            // needs no cleanup worker.
            var (io, _, session) = Mini60();
            Assert.True(session.Start(io));
            int before = io.Attempts.Count;
            session.Stop(io);
            var after = io.Attempts.Skip(before).ToArray();
            Assert.Single(after);
            Assert.Equal("out", after[0].Kind);
            var off = new byte[65];
            Hex("00 AA 67 00 00 00 00 01").CopyTo(off, 0);
            Assert.Equal(off, after[0].Data);
            Assert.False(session.SimulationModeSent);
            session.Stop(io);
            Assert.Equal(before + 1, io.Attempts.Count);
        }

        [Fact]
        public void Mini60_Stop_FailedSend_TriesTheOtherRoute_InBothRounds()
        {
            // aula_mini60_diagnostic.cpp:170: a failed 0x67 send flips the
            // route and sends again. The flipped send waits for no
            // acknowledgement, so the cleanup worker round runs too (:465).
            var (io, _, session) = Mini60();
            Assert.True(session.Start(io));
            int before = io.Attempts.Count;
            io.FailWrite = (kind, report) => kind == "out" && report[2] == 0x67;
            session.Stop(io);
            var after = io.Attempts.Skip(before).Select(a => (a.Kind, a.Data[2], a.Ok)).ToArray();
            Assert.Equal(new[]
            {
                ("out", (byte)0x67, false), ("ctl", (byte)0x67, true),
                ("out", (byte)0x67, false), ("ctl", (byte)0x67, true),
            }, after);
        }

        [Fact]
        public void Mini60_Stop_GoneKeyboard_TriesAndReturns()
        {
            // The keyboard left: every send fails at once and Stop returns.
            var (io, _, session) = Mini60();
            Assert.True(session.Start(io));
            io.Device.Gone = true;
            int before = io.Attempts.Count;
            session.Stop(io);
            Assert.Equal(4, io.Attempts.Count - before);
            Assert.All(io.Attempts.Skip(before), a => Assert.False(a.Ok));
        }

        // ── W669: identification ──

        [Theory]
        [InlineData(0x2E3C, 0xC365, 64, 64)]
        [InlineData(0x1234, 0x5678, 64, 64)]
        [InlineData(0x2E3C, 0xC365, 65, 65)]
        public void W669_Matches_AnyVendorWithTheInterfaceShape(int vid, int pid, int input, int output)
        {
            // aula_w669_backend.cpp:207-209: FF1B:0091, reports of at least
            // 64 bytes, no VID or PID filter.
            Assert.True(AulaW669Protocol.Matches(Info((ushort)vid, (ushort)pid, 0xFF1B, 0x0091, input, output)));
        }

        public static IEnumerable<object[]> W669NearMisses()
        {
            // irok_na87_backend.cpp:43-46: the M484 product on the same usage
            // belongs to the IROK NA87 route.
            yield return new object[] { "IROK NA87 / AJAZZ AK820 MAX", Info(0x0416, 0x7372, 0xFF1B, 0x0091, 64, 64) };
            yield return new object[] { "usage 0092", Info(0x2E3C, 0xC365, 0xFF1B, 0x0092, 64, 64) };
            yield return new object[] { "usage page FF1C", Info(0x2E3C, 0xC365, 0xFF1C, 0x0091, 64, 64) };
            yield return new object[] { "input 63", Info(0x2E3C, 0xC365, 0xFF1B, 0x0091, 63, 64) };
            yield return new object[] { "output 63", Info(0x2E3C, 0xC365, 0xFF1B, 0x0091, 64, 63) };
            yield return new object[] { "keyboard collection", Info(0x2E3C, 0xC365, 0x0001, 0x0006, 9, 2) };
        }

        [Theory]
        [MemberData(nameof(W669NearMisses))]
        public void W669_Matches_RejectsNearMisses(string why, AnalogKeyboardDeviceInfo info)
        {
            Assert.False(AulaW669Protocol.Matches(info), why);
            Assert.False(AulaEventsRoutes.All[1].Matches(info), why);
        }

        [Fact]
        public void W669_Matches_OtherProductsOfThatVendor()
        {
            // Only the one product another route owns is excluded.
            Assert.True(AulaW669Protocol.Matches(Info(0x0416, 0x7373, 0xFF1B, 0x0091, 64, 64)));
            Assert.True(AulaW669Protocol.Matches(Info(0x0417, 0x7372, 0xFF1B, 0x0091, 64, 64)));
        }

        // ── W669: frames and decoders ──

        private static byte[] W669Frame(string head)
        {
            var r = new byte[64];
            Hex(head).CopyTo(r, 0);
            return r;
        }

        [Fact]
        public void W669_Requests_MatchHallJoysBuilders()
        {
            // aula_w669_protocol.cpp:27-74, tests/aula_w669_protocol_test.cpp:40, 81.
            Assert.Equal(W669Frame("01 0D"), AulaW669Protocol.DeviceInfoRequest());
            Assert.Equal(W669Frame("01 21 00 00 00 00 04"), AulaW669Protocol.TravelInfoRequest());
            Assert.Equal(W669Frame("01 18 80"), AulaW669Protocol.KeyMapRequest());
            Assert.Equal(W669Frame("01 21 00 00 00 00 0A"), AulaW669Protocol.PollRateQuery());
            Assert.Equal(W669Frame("01 21 00 00 00 00 03"), AulaW669Protocol.UnsubscribeRequest());
        }

        [Fact]
        public void W669_Subscription_PutsTheColumnMaskAtByte7()
        {
            // tests/aula_w669_protocol_test.cpp:174-177.
            var mask = new byte[22];
            mask[3] = 0x15;
            var subscribe = AulaW669Protocol.SubscriptionRequest(mask);
            Assert.Equal(64, subscribe.Length);
            Assert.Equal(0x18, subscribe[5]);
            Assert.Equal(0x02, subscribe[6]);
            Assert.Equal(0x15, subscribe[10]);
            Assert.Equal(W669Frame("01 21 00 00 00 18 02 00 00 00 15"), subscribe);
            Assert.Throws<ArgumentException>(() => AulaW669Protocol.SubscriptionRequest(new byte[21]));
        }

        [Theory]
        [InlineData("w669_k617_us", 61)]
        [InlineData("w669_k617_br", 63)]
        public void W669_Subscription_SelectsEveryMappedPosition(string table, int keys)
        {
            // tests/aula_w669_protocol_test.cpp:12-25: the mask bit of every
            // position equals whether the map has a key there, and a live
            // event at that position decodes to it.
            var map = AnalogKeyboardData.Table(AulaEventsRoutes.DataFile, table);
            Assert.Equal(keys, AulaW669Protocol.MappedCount(map));
            Assert.Equal(26, map[46]);
            Assert.Equal(4, map[68]);
            Assert.Equal(22, map[69]);
            Assert.Equal(7, map[70]);
            Assert.Equal(0xFA, map[120]);
            var subscription = AulaW669Protocol.SubscriptionRequest(AulaW669Protocol.SubscriptionMask(map));
            for (int pos = 0; pos < 132; pos++)
            {
                Assert.Equal(map[pos] != 0, (subscription[7 + pos % 22] & (1 << (pos / 22))) != 0);
                if (map[pos] == 0) continue;
                var live = W669Live(pos / 22, pos % 22, pos, 5);
                Assert.True(AulaW669Protocol.TryDecodeLiveEvent(live, out var ev));
                Assert.Equal(map[pos], map[ev.Row * 22 + ev.Column]);
                Assert.Equal(pos, ev.Travel);
            }
        }

        private static byte[] W669DeviceInfo(string csv)
        {
            // tests/aula_w669_protocol_test.cpp:42-54: byte 5 is 5 plus the
            // text length, the text starts at byte 6.
            var r = new byte[64];
            r[0] = 1; r[1] = 0x0D;
            r[5] = (byte)(5 + csv.Length);
            Encoding.ASCII.GetBytes(csv).CopyTo(r, 6);
            return r;
        }

        [Fact]
        public void W669_DeviceInfo_ReadsTheFifthCsvField()
        {
            // tests/aula_w669_protocol_test.cpp:55-61, 76-78.
            var report = W669DeviceInfo("board,chip,variant,region,SI2828KZHEARGB,V3_17_08");
            Assert.True(AulaW669Protocol.TryDecodeDeviceInfo(report, out string product));
            Assert.Equal("SI2828KZHEARGB", product);
            report[2] = 1;
            Assert.False(AulaW669Protocol.TryDecodeDeviceInfo(report, out _));

            // The K673 BR answer HallJoy's hardware log recorded
            // (docs/research/REDRAGON_K673_SOFTWARE_STATIC_ANALYSIS_2026-08-10.md:128-129).
            Assert.True(AulaW669Protocol.TryDecodeDeviceInfo(
                W669DeviceInfo("W669,34,KB,FR,7272BRHEXYXK673JCARGB,V3.18.01"), out product));
            Assert.Equal("7272BRHEXYXK673JCARGB", product);

            // aula_w669_protocol.cpp:239-265: the product ends the text when
            // no sixth field follows.
            Assert.True(AulaW669Protocol.TryDecodeDeviceInfo(W669DeviceInfo("a,b,c,d,SI2825HEARGB"), out product));
            Assert.Equal("SI2825HEARGB", product);
        }

        [Fact]
        public void W669_DeviceInfo_RejectsMalformedAnswers()
        {
            // aula_w669_protocol.cpp:239-265.
            Assert.False(AulaW669Protocol.TryDecodeDeviceInfo(W669DeviceInfo("a,b,c,SI2825HEARGB"), out _));
            Assert.False(AulaW669Protocol.TryDecodeDeviceInfo(W669DeviceInfo("a,b,c,d,,V1"), out _));
            Assert.False(AulaW669Protocol.TryDecodeDeviceInfo(W669DeviceInfo("a,b,c,d," + new string('X', 32)), out _));
            Assert.True(AulaW669Protocol.TryDecodeDeviceInfo(W669DeviceInfo("a,b,c,d," + new string('X', 31)), out _));
            var tab = W669DeviceInfo("a,b,c,d,SI2825HEARGB");
            tab[8] = 0x09;
            Assert.False(AulaW669Protocol.TryDecodeDeviceInfo(tab, out _));
            var status = W669DeviceInfo("a,b,c,d,SI2825HEARGB");
            status[4] = 1;
            Assert.False(AulaW669Protocol.TryDecodeDeviceInfo(status, out _));
            var shortText = W669DeviceInfo("a,b,c,d,SI2825HEARGB");
            shortText[5] = 5;
            Assert.False(AulaW669Protocol.TryDecodeDeviceInfo(shortText, out _));
            var wide = W669DeviceInfo("a,b,c,d,SI2825HEARGB");
            wide[5] = 64;
            Assert.False(AulaW669Protocol.TryDecodeDeviceInfo(wide, out _));
            Assert.False(AulaW669Protocol.TryDecodeDeviceInfo(wide.AsSpan(0, 63), out _));
            var otherId = W669DeviceInfo("a,b,c,d,SI2825HEARGB");
            otherId[0] = 2;
            Assert.False(AulaW669Protocol.TryDecodeDeviceInfo(otherId, out _));
            // A zero byte ends the text early.
            var cut = W669DeviceInfo("a,b,c,d,SI2825HEARGB,V1");
            cut[6 + "a,b,c,d,SI2825".Length] = 0;
            Assert.True(AulaW669Protocol.TryDecodeDeviceInfo(cut, out string product));
            Assert.Equal("SI2825", product);
        }

        [Theory]
        [InlineData("SI2825HEARGB", "AULA WIN 60 HE")]
        [InlineData("SI2825KRT12HEARGB", "AULA WIN 60 HE")]
        [InlineData("SI2825KR-AHEARGB", "AULA WIN 60 HE")]
        [InlineData(" si2825kr-aheargb ", "AULA WIN 60 HE")]
        [InlineData("SI2825KZHEARGB", "AULA WIN 60 HE")]
        [InlineData("SI2828HEARGB", "AULA WIN 68 HE")]
        [InlineData("SI2828KZHEARGB", "AULA WIN 68 HE")]
        [InlineData("SI2851UKKZHEARGB", "AULA KP-TE153")]
        [InlineData("7272BRHEXYXK673JCARGB", "Redragon K673RGB-M BR")]
        [InlineData("7272UKHEXYXBJCARGB", "Redragon K673RGB-M UK")]
        [InlineData(" 7272ukhexyxbjcargb ", "Redragon K673RGB-M UK")]
        [InlineData("7272USHEXYXK673JCARGB", "Redragon K673WB-RGB-M US")]
        [InlineData("7153USHEXYXCPARGB", "Redragon K617 HE US")]
        [InlineData("7153BRHEXYXCPARGB", "Redragon K617 HE BR")]
        [InlineData("SI 2825 HE\tARGB", "AULA WIN 60 HE")]
        public void W669_ProductCatalog_SelectsHallJoysProfiles(string product, string name)
        {
            // aula_w669_protocol.cpp:184-214, tests/aula_w669_protocol_test.cpp:26-27, 60-71.
            Assert.Equal(name, AulaW669Protocol.ProfileForProduct(product)?.Name);
        }

        [Theory]
        [InlineData("K673RGB-M")]
        [InlineData("UNKNOWN-W669")]
        [InlineData("7153UNKNOWN")]
        [InlineData("SI2825HEARGBX")]
        [InlineData("")]
        [InlineData(null)]
        public void W669_ProductCatalog_UnknownProducts(string product)
        {
            // tests/aula_w669_protocol_test.cpp:28, 72-75.
            Assert.Null(AulaW669Protocol.ProfileForProduct(product));
        }

        [Fact]
        public void W669_DescriptorFallback_OnlyTheThreeAulaNames()
        {
            // aula_w669_backend.cpp:292-306.
            Assert.Equal("AULA WIN 60 HE", AulaW669Protocol.ProfileForDescriptor("WIN 60 HE")?.Name);
            Assert.Equal("AULA WIN 68 HE", AulaW669Protocol.ProfileForDescriptor("  win 68 he ")?.Name);
            Assert.Equal("AULA KP-TE153", AulaW669Protocol.ProfileForDescriptor("KP-TE153")?.Name);
            Assert.Null(AulaW669Protocol.ProfileForDescriptor("K673RGB-M"));
            Assert.Null(AulaW669Protocol.ProfileForDescriptor("WIN 60 HE MAX"));
            Assert.Null(AulaW669Protocol.ProfileForDescriptor("WIN60HE"));
            Assert.Null(AulaW669Protocol.ProfileForDescriptor(""));
        }

        [Fact]
        public void W669_ProductNormalization_KeepsHallJoys31Characters()
        {
            // aula_w669_protocol.cpp:187-194.
            Assert.Equal("SI2825KR-AHEARGB", AulaW669Protocol.NormalizeProduct(" si2825kr-a\r\nheargb "));
            Assert.Equal(new string('A', 31), AulaW669Protocol.NormalizeProduct(new string('a', 40)));
        }

        private static byte[] W669Travel(int low = 0x54, int unit = 1, int middle = 1, int high = 1, int format = 8,
            int declared = 6)
        {
            var r = new byte[64];
            r[0] = 1; r[1] = 0x21; r[5] = (byte)declared; r[6] = 4;
            r[7] = (byte)low; r[8] = (byte)unit; r[9] = (byte)middle; r[10] = (byte)high; r[11] = (byte)format;
            return r;
        }

        [Fact]
        public void W669_TravelInfo_FollowsHallJoysTest()
        {
            // tests/aula_w669_protocol_test.cpp:83-88, aula_w669_protocol.cpp:268-280.
            Assert.True(AulaW669Protocol.TryDecodeTravelInfo(W669Travel(), out var info));
            Assert.Equal(new AulaW669TravelInfo(340, 1, 8), info);
            Assert.False(AulaW669Protocol.TryDecodeTravelInfo(W669Travel(high: 0), out _));
            Assert.False(AulaW669Protocol.TryDecodeTravelInfo(W669Travel(low: 0), out _));
            Assert.False(AulaW669Protocol.TryDecodeTravelInfo(W669Travel(declared: 5), out _));
            Assert.True(AulaW669Protocol.TryDecodeTravelInfo(W669Travel(low: 0x10, high: 0x27), out info));
            Assert.Equal(10000, info.Maximum);
            Assert.False(AulaW669Protocol.TryDecodeTravelInfo(W669Travel(low: 0x11, high: 0x27), out _));
            var subtype = W669Travel();
            subtype[6] = 5;
            Assert.False(AulaW669Protocol.TryDecodeTravelInfo(subtype, out _));
            Assert.False(AulaW669Protocol.TryDecodeTravelInfo(W669Travel().AsSpan(0, 63), out _));
        }

        private static byte[] W669Fragment(int fragment, params (int Record, int Class, int Usage)[] records)
        {
            var r = new byte[64];
            r[0] = 1; r[1] = 0x18; r[2] = 0x80;
            r[3] = (byte)(fragment >> 8); r[4] = (byte)fragment;
            r[5] = (byte)(fragment < 9 ? 56 : 24);
            foreach (var (record, cls, usage) in records)
            {
                r[6 + record * 4] = (byte)cls;
                r[7 + record * 4] = (byte)usage;
            }
            return r;
        }

        [Fact]
        public void W669_KeyMapFragments_InheritOverrideAndDrop()
        {
            // tests/aula_w669_protocol_test.cpp:144-150: fragment 9 record 0
            // is position 126.
            var map = new int[132];
            var fragments = new bool[10];
            Assert.True(AulaW669Protocol.DecodeKeyMapFragment(W669Fragment(9, (0, 1, 4)), map, fragments));
            Assert.Equal(4, map[126]);
            Assert.True(fragments[9]);

            // aula_w669_protocol.cpp:297-301: class 00 and FF keep the factory
            // key, byte 1 of 00 or FF drops the position.
            var win60 = AnalogKeyboardData.Table(AulaEventsRoutes.DataFile, "w669_si2825_win60");
            map = (int[])win60.Clone();
            AulaW669Protocol.DecodeKeyMapFragment(W669Fragment(1, (8, 0xFF, 0x1A), (9, 0x00, 0x1A)), map, fragments);
            Assert.Equal(win60[22], map[22]);
            Assert.Equal(win60[23], map[23]);
            AulaW669Protocol.DecodeKeyMapFragment(W669Fragment(1, (8, 0x01, 0x00), (9, 0x01, 0xFF), (10, 0x07, 0x2C)), map, fragments);
            Assert.Equal(0, map[22]);
            Assert.Equal(0, map[23]);
            Assert.Equal(0x2C, map[24]);

            // Malformed fragments change nothing.
            Assert.False(AulaW669Protocol.DecodeKeyMapFragment(W669Fragment(10), map, fragments));
            var length = W669Fragment(3, (0, 1, 4));
            length[5] = 24;
            Assert.False(AulaW669Protocol.DecodeKeyMapFragment(length, map, fragments));
            var selector = W669Fragment(3, (0, 1, 4));
            selector[2] = 0x81;
            Assert.False(AulaW669Protocol.DecodeKeyMapFragment(selector, map, fragments));
            Assert.False(AulaW669Protocol.DecodeKeyMapFragment(W669Fragment(3, (0, 1, 4)).AsSpan(0, 63), map, fragments));
            Assert.False(fragments[3]);
        }

        [Fact]
        public void W669_KeyMap_StockWin60Log_KeepsEveryKey()
        {
            // tests/aula_w669_protocol_test.cpp:152-172: the physical log of an
            // unmodified WIN 60 HE, all zero records and 01 FA for Fn.
            var map = AulaW669Protocol.FactoryMap(AulaW669Protocol.ProfileForProduct("SI2825KZHEARGB"));
            var fragments = new bool[10];
            for (int fragment = 0; fragment < 10; fragment++)
            {
                var packet = fragment == 8 ? W669Fragment(fragment, (10, 1, 0xFA)) : W669Fragment(fragment);
                Assert.True(AulaW669Protocol.DecodeKeyMapFragment(packet, map, fragments));
            }
            Assert.All(fragments, Assert.True);
            Assert.Equal(61, AulaW669Protocol.MappedCount(map));
            Assert.Equal(0x29, map[22]);
            Assert.Equal(0xFA, map[122]);
            Assert.Equal(new int[132], AulaW669Protocol.FactoryMap(null));
        }

        private static byte[] W669Live(int row, int column, int travel, int declared = 3, int subtype = 1)
        {
            var r = new byte[64];
            r[0] = 1; r[1] = 0x21; r[5] = (byte)declared; r[6] = (byte)subtype;
            r[7] = (byte)row; r[8] = (byte)column;
            Put16(r, 9, travel);
            return r;
        }

        [Fact]
        public void W669_LiveEvent_FollowsHallJoysTest()
        {
            // tests/aula_w669_protocol_test.cpp:91-98.
            Assert.True(AulaW669Protocol.TryDecodeLiveEvent(W669Live(5, 21, 0x134, 5), out var ev));
            Assert.Equal(new AulaW669LiveEvent(5, 21, 0x134, 5), ev);
            Assert.False(AulaW669Protocol.TryDecodeLiveEvent(W669Live(5, 21, 0x134, 5, subtype: 5), out _));
            // aula_w669_protocol.cpp:309-317.
            Assert.True(AulaW669Protocol.TryDecodeLiveEvent(W669Live(0, 0, 0, 3), out _));
            Assert.False(AulaW669Protocol.TryDecodeLiveEvent(W669Live(0, 0, 0, 2), out _));
            Assert.False(AulaW669Protocol.TryDecodeLiveEvent(W669Live(6, 0, 10), out _));
            Assert.False(AulaW669Protocol.TryDecodeLiveEvent(W669Live(0, 22, 10), out _));
            Assert.False(AulaW669Protocol.TryDecodeLiveEvent(W669Live(1, 1, 10).AsSpan(0, 63), out _));
            var otherId = W669Live(1, 1, 10);
            otherId[0] = 2;
            Assert.False(AulaW669Protocol.TryDecodeLiveEvent(otherId, out _));
        }

        [Fact]
        public void W669_PollRate_FollowsHallJoysTest()
        {
            // tests/aula_w669_protocol_test.cpp:178-184, aula_w669_protocol.cpp:334-354.
            var poll = W669Frame("01 21 00 00 00 02 09 08");
            Assert.True(AulaW669Protocol.TryDecodePollRate(poll, out int code, out int hz));
            Assert.Equal((8, 8000), (code, hz));
            poll[7] = 0;
            Assert.True(AulaW669Protocol.TryDecodePollRate(poll, out code, out hz));
            Assert.Equal((0, 0), (code, hz));
            poll[7] = 3;
            Assert.False(AulaW669Protocol.TryDecodePollRate(poll, out _, out _));
            poll[7] = 1;
            poll[5] = 1;
            Assert.False(AulaW669Protocol.TryDecodePollRate(poll, out _, out _));
        }

        [Fact]
        public void W669_ToMilli_RoundsLikeHallJoy()
        {
            // tests/aula_w669_protocol_test.cpp:89, aula_w669_protocol.cpp:356-361.
            Assert.Equal(500, AulaW669Protocol.ToMilli(170, 340));
            Assert.Equal(3, AulaW669Protocol.ToMilli(1, 340));
            Assert.Equal(21, AulaW669Protocol.ToMilli(7, 340));
            Assert.Equal(1000, AulaW669Protocol.ToMilli(340, 340));
            Assert.Equal(1000, AulaW669Protocol.ToMilli(65535, 340));
            Assert.Equal(0, AulaW669Protocol.ToMilli(0, 340));
            Assert.Equal(0, AulaW669Protocol.ToMilli(170, 0));
        }

        private static ulong Fnv(int[] map)
        {
            ulong hash = 1469598103934665603UL;
            foreach (int value in map)
            {
                hash ^= (ulong)value;
                hash *= 1099511628211UL;
            }
            return hash;
        }

        [Fact]
        public void W669_FactoryMaps_MatchHallJoysTests()
        {
            // tests/aula_w669_protocol_test.cpp:100-143.
            int[] T(string name) => AnalogKeyboardData.Table(AulaEventsRoutes.DataFile, name);
            var win60 = T("w669_si2825_win60");
            Assert.Equal(132, win60.Length);
            Assert.Equal(61, AulaW669Protocol.MappedCount(win60));
            Assert.Equal((0x29, 0x14, 0x04, 0x2C, 0xFA), (win60[22], win60[45], win60[68], win60[116], win60[122]));

            var win68 = T("w669_si2828_win68");
            Assert.Equal(68, AulaW669Protocol.MappedCount(win68));
            Assert.Equal((0x49, 0x4C, 0x52, 0x50, 0x51, 0x4F), (win68[38], win68[60], win68[103], win68[124], win68[125], win68[126]));
            Assert.Equal((0xFA, 0xE4), (win68[121], win68[122]));

            var kp = T("w669_si2851_kp_te153_uk");
            Assert.Equal(69, AulaW669Protocol.MappedCount(kp));
            Assert.Equal((0x4A, 0x28, 0x32, 0x64, 0x87), (kp[38], kp[58], kp[79], kp[89], kp[99]));

            var br = T("w669_k673_br");
            Assert.Equal(81, AulaW669Protocol.MappedCount(br));
            Assert.Equal(0xC110AC8CA8C64448UL, Fnv(br));
            Assert.Equal((0x29, 0x3A, 0x45, 0x49), (br[0], br[2], br[14], br[37]));
            Assert.Equal((0x4C, 0x32, 0x28, 0x87), (br[59], br[79], br[80], br[100]));
            Assert.Equal((0x52, 0x4E, 0x50, 0x51, 0x4F), (br[102], br[103], br[123], br[124], br[125]));

            var uk = T("w669_k673_uk");
            Assert.Equal(81, AulaW669Protocol.MappedCount(uk));
            Assert.Equal(0x65301581D7CA3081UL, Fnv(uk));
            Assert.Equal((0x31, 0x00, 0xE5), (uk[79], uk[100], uk[101]));

            var us = T("w669_k673_us");
            Assert.Equal(80, AulaW669Protocol.MappedCount(us));
            Assert.Equal(0x2943AA8B95F576BBUL, Fnv(us));
            Assert.Equal((0x31, 0x00, 0x00, 0xE5), (us[58], us[79], us[89], us[101]));

            // Every profile's table exists and has the 6 x 22 matrix.
            Assert.Equal(8, AulaW669Protocol.Profiles.Count);
            Assert.All(AulaW669Protocol.Profiles, p => Assert.Equal(132, p.FactoryMap().Length));
            Assert.Equal(12, AulaW669Protocol.Profiles.Sum(p => p.Products.Count));
        }

        [Fact]
        public void W669_FnUsage_PublishesAsTheFnKey()
        {
            // docs/current/AULA_LAYOUT_PIPELINE.md:80: W669 maps give Fn 0xFA.
            Assert.Equal(AnalogKeyCodes.Fn, AulaW669Protocol.KeyCode(0xFA));
            Assert.Equal(AnalogKeyCodes.W, AulaW669Protocol.KeyCode(0x1A));
            Assert.Equal(0, AulaW669Protocol.KeyCode(0));
        }

        // ── W669: sessions ──

        /// <summary>A W669 keyboard as HallJoy's Run sees it: a 0D answer, a
        /// 340-count travel answer, ten map fragments of inherit records with
        /// the stock WIN 60 HE's 01 FA at position 122, and a poll code.</summary>
        private sealed class W669Keyboard
        {
            public string Product = "SI2825KZHEARGB";
            public Func<string, bool> AnswersTravel = kind => true;
            public readonly List<(int Fragment, int Record, int Class, int Usage)> Records = new() { (8, 10, 1, 0xFA) };
            public int PollCode = 0;

            public void Attach(ClockedTransport io)
            {
                // W669 reports are 64 bytes with the report ID.
                io.Device.InputLength = 64;
                io.Device.OutputLength = 64;
                io.Device.OnSend = req => Respond("out", req);
                io.Device.OnSendOutputReport = req => Respond("ctl", req);
            }

            private IEnumerable<byte[]> Respond(string kind, byte[] req)
            {
                if (req.Length != 64 || req[0] != 1) yield break;
                if (req[1] == 0x0D && Product != null)
                    yield return W669DeviceInfo("W669,34,KB,FR," + Product + ",V3.17.08");
                if (req[1] == 0x21 && req[6] == 0x04 && AnswersTravel(kind))
                    yield return W669Travel();
                if (req[1] == 0x21 && req[6] == 0x0A)
                    yield return W669Frame("01 21 00 00 00 02 09 " + PollCode.ToString("X2"));
                if (req[1] == 0x18 && req[2] == 0x80)
                {
                    // Fragments in a shuffled order, one live report among them.
                    foreach (int fragment in new[] { 3, 0, 9, 1, 2, 4, 5, 6, 7, 8 })
                    {
                        if (fragment == 5) yield return W669Live(0, 0, 0, 3, subtype: 7);
                        yield return W669Fragment(fragment, Records.Where(r => r.Fragment == fragment)
                            .Select(r => (r.Record, r.Class, r.Usage)).ToArray());
                    }
                }
            }
        }

        private static (ClockedTransport io, W669Keyboard kb, AulaW669Session session) W669(string hidProduct = "")
        {
            var io = new ClockedTransport();
            var kb = new W669Keyboard();
            kb.Attach(io);
            var session = new AulaW669Session(hidProduct, () => io.Now);
            return (io, kb, session);
        }

        private static string Op(byte[] report) => report[1] == 0x21
            ? "21/" + report[6].ToString("X2")
            : report[1] == 0x18 ? "18/" + report[2].ToString("X2") : report[1].ToString("X2");

        [Fact]
        public void W669_Start_FirmwareIdentity_SubscribesEveryMappedPosition()
        {
            // aula_w669_backend.cpp:446-488: 0D, 21/04, 18/80, 21/0A, then
            // 21/02 with the mask of every mapped position.
            var (io, _, session) = W669();
            Assert.True(session.Start(io));
            Assert.Equal(new[] { "out 0D", "out 21/04", "out 18/80", "out 21/0A", "out 21/02" },
                io.Attempts.Select(a => a.Kind + " " + Op(a.Data)).ToArray());
            Assert.All(io.Attempts, a => Assert.Equal(64, a.Data.Length));
            Assert.Equal(AulaW669Protocol.DeviceInfoRequest(), io.Attempts[0].Data);
            Assert.Equal(AulaW669Protocol.TravelInfoRequest(), io.Attempts[1].Data);
            Assert.Equal(AulaW669Protocol.KeyMapRequest(), io.Attempts[2].Data);
            Assert.Equal(AulaW669Protocol.PollRateQuery(), io.Attempts[3].Data);

            var win60 = AnalogKeyboardData.Table(AulaEventsRoutes.DataFile, "w669_si2825_win60");
            var subscribe = io.Attempts[4].Data;
            Assert.Equal(AulaW669Protocol.SubscriptionRequest(AulaW669Protocol.SubscriptionMask(win60)), subscribe);
            // Rows 1 to 5 carry keys in the WIN 60 HE's first 13 columns.
            Assert.Equal(0x3E, subscribe[7 + 0]);
            Assert.Equal(0x00, subscribe[7 + 15]);

            Assert.Equal("AULA WIN 60 HE", session.ModelName);
            Assert.Equal("SI2825KZHEARGB", session.FirmwareProduct);
            Assert.Equal(340, session.MaximumTravel);
            Assert.Equal(0, session.PollRateCode);
            Assert.False(session.UsesOutputReports);
            Assert.Equal(61, session.KeyOrder.Length);
            Assert.Equal(AnalogKeyCodes.Escape, session.KeyOrder[0]);
            Assert.Equal(AnalogKeyCodes.Fn, session.KeyOrder[^1]);
            Assert.DoesNotContain(0xFA, session.KeyOrder);
        }

        [Theory]
        [InlineData("SI2828HEARGB", "AULA WIN 68 HE", 68)]
        [InlineData("SI2851UKKZHEARGB", "AULA KP-TE153", 69)]
        [InlineData("7272BRHEXYXK673JCARGB", "Redragon K673RGB-M BR", 81)]
        [InlineData("7272UKHEXYXBJCARGB", "Redragon K673RGB-M UK", 81)]
        [InlineData("7272USHEXYXK673JCARGB", "Redragon K673WB-RGB-M US", 80)]
        [InlineData("7153USHEXYXCPARGB", "Redragon K617 HE US", 61)]
        [InlineData("7153BRHEXYXCPARGB", "Redragon K617 HE BR", 63)]
        public void W669_Start_NamesEveryModel(string product, string name, int keys)
        {
            // aula_w669_protocol.cpp:198-213. The Fn record of the WIN 60 HE
            // log would add a key to the others, so these keyboards return
            // inherit records only.
            var (io, kb, session) = W669();
            kb.Product = product;
            kb.Records.Clear();
            Assert.True(session.Start(io));
            Assert.Equal(name, session.ModelName);
            Assert.Equal(keys, AulaW669Protocol.MappedCount(session.Map));
        }

        [Fact]
        public void W669_Start_SilentDeviceInfo_FallsBackToTheProductString()
        {
            // aula_w669_backend.cpp:318-348: 400 ms without a 0D answer, then
            // the HID product string decides.
            var (io, kb, session) = W669("WIN 60 HE");
            kb.Product = null;
            long before = io.Now;
            Assert.True(session.Start(io));
            Assert.Equal("AULA WIN 60 HE", session.ModelName);
            Assert.Null(session.FirmwareProduct);
            // Five reads of 80 ms, then every other answer comes at once.
            Assert.Equal(400, io.Now - before);
        }

        [Fact]
        public void W669_Start_RedragonHasNoProductStringFallback()
        {
            // docs/v1.4/PROTOCOL_AUDIT_RM30_W669_2026-09-06.md:19-22: an
            // unknown product needs 20 explicit records, and stock records
            // are all inherit. Both write modes are proven and fail.
            var (io, kb, session) = W669("K673RGB-M");
            kb.Product = null;
            Assert.False(session.Start(io));
            Assert.Equal(new[] { "out 0D", "out 21/04", "out 18/80", "ctl 0D", "ctl 21/04", "ctl 18/80" },
                io.Attempts.Select(a => a.Kind + " " + Op(a.Data)).ToArray());
            session.Stop(io);
            Assert.Equal(6, io.Attempts.Count);
        }

        [Fact]
        public void W669_Start_UnknownFirmwareProduct_IgnoresTheProductString()
        {
            // aula_w669_backend.cpp:318-336: a decoded 0D answer settles the
            // identity, so the HID product string is read only when 0D fails.
            // An unknown product with the stock records maps one key and fails.
            var (io, kb, session) = W669("WIN 60 HE");
            kb.Product = "SI9999HEARGB";
            Assert.False(session.Start(io));
            Assert.DoesNotContain(io.Attempts, a => a.Data[1] == 0x21 && a.Data[6] == 0x02);
        }

        [Fact]
        public void W669_Start_UnknownProduct_WithTwentyExplicitRecords_IsAdmitted()
        {
            // aula_w669_backend.cpp:355-364, aula_w669_protocol.cpp:228: an
            // unknown product starts from an empty map and keeps the explicit
            // records, 20 at least.
            var (io, kb, session) = W669();
            kb.Product = "SI9999HEARGB";
            kb.Records.Clear();
            for (int i = 0; i < 20; i++)
                kb.Records.Add(((22 + i) / 14, (22 + i) % 14, 1, 0x04 + i));
            Assert.True(session.Start(io));
            Assert.Null(session.ModelName);
            Assert.Null(session.Profile);
            Assert.Equal(Enumerable.Range(0x04, 20).ToArray(), session.KeyOrder);
            Assert.Equal("out 21/02", io.Attempts.Last().Kind + " " + Op(io.Attempts.Last().Data));

            var (io2, kb2, session2) = W669();
            kb2.Product = "SI9999HEARGB";
            kb2.Records.Clear();
            for (int i = 0; i < 19; i++)
                kb2.Records.Add(((22 + i) / 14, (22 + i) % 14, 1, 0x04 + i));
            Assert.False(session2.Start(io2));
        }

        [Fact]
        public void W669_Start_SilentWriteFile_ProvesAgainWithOutputReports()
        {
            // aula_w669_backend.cpp:392: WriteFile first, then
            // HidD_SetOutputReport, each with the whole proof.
            var (io, kb, session) = W669();
            kb.AnswersTravel = kind => kind == "ctl";
            Assert.True(session.Start(io));
            Assert.True(session.UsesOutputReports);
            Assert.Equal(new[] { "out 0D", "out 21/04", "ctl 0D", "ctl 21/04", "ctl 18/80", "ctl 21/0A", "ctl 21/02" },
                io.Attempts.Select(a => a.Kind + " " + Op(a.Data)).ToArray());
            Assert.Equal(1, io.Device.Discards);

            // aula_w669_backend.cpp:601: the teardown uses the proven mode.
            session.Stop(io);
            Assert.Equal("ctl", io.Attempts.Last().Kind);
            Assert.Equal(AulaW669Protocol.UnsubscribeRequest(), io.Attempts.Last().Data);
        }

        [Fact]
        public void W669_Start_FailedSubscriptionWrite_StillClearsTheMask()
        {
            // aula_w669_backend.cpp:482 returns without 21/03 here. The write
            // may have reached the keyboard, so 21/03 follows.
            var (io, _, session) = W669();
            io.FailWrite = (kind, report) => report[1] == 0x21 && report[6] == 0x02;
            Assert.False(session.Start(io));
            var tail = io.Attempts.Skip(4).Select(a => (Op(a.Data), a.Ok)).ToArray();
            Assert.Equal(new[] { ("21/02", false), ("21/03", true) }, tail);
            session.Stop(io);
            Assert.Equal(6, io.Attempts.Count);
        }

        [Fact]
        public void W669_Start_GoneDuringTheProof_StopsAtOnce()
        {
            var (io, _, session) = W669();
            io.Device.Gone = true;
            Assert.False(session.Start(io));
            Assert.DoesNotContain(io.Attempts, a => a.Ok);
        }

        [Fact]
        public void W669_Pass_PublishesEvents_AndAliasesReadTheDeepest()
        {
            // aula_w669_backend.cpp:416-434: the event's position picks the
            // key, and a key reads the deepest of its positions, no expiry.
            var (io, kb, session) = W669();
            // An explicit record moves A's position (row 3, column 2) to W.
            kb.Records.Add((68 / 14, 68 % 14, 1, 0x1A));
            Assert.True(session.Start(io));
            Assert.DoesNotContain(AnalogKeyCodes.A, session.KeyOrder);

            var output = new AnalogKeyInputState();
            io.Device.QueueInput(W669Live(2, 2, 170));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));

            io.Device.QueueInput(W669Live(3, 2, 340, 5));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));

            io.Device.QueueInput(W669Live(3, 2, 0));
            session.Pass(io, output, null);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));

            // Hours of silence change nothing: the stream is change-driven.
            io.Now += 3_600_000;
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));

            io.Device.QueueInput(W669Live(2, 2, 0));
            session.Pass(io, output, null);
            Assert.Equal(0, output.Count);

            // Fn at row 5, column 12 of the WIN 60 HE publishes as 0x409.
            io.Device.QueueInput(W669Live(5, 12, 7));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.021f, output.Get(AnalogKeyCodes.Fn), 5);
        }

        [Fact]
        public void W669_Pass_IgnoresEverythingButMappedLiveEvents()
        {
            var (io, _, session) = W669();
            Assert.True(session.Start(io));
            var output = new AnalogKeyInputState();
            io.Device.QueueInput(
                W669Live(2, 2, 170, 5, subtype: 5),     // trigger configuration answer
                W669Live(2, 2, 170, 3, subtype: 7),     // asynchronous status
                W669Live(0, 0, 340),                    // no key at row 0 of the WIN 60 HE
                W669Live(2, 2, 170).AsSpan(0, 40).ToArray(),
                W669Travel());
            for (int i = 0; i < 5; i++)
                Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            Assert.Equal(0, output.Count);
        }

        [Fact]
        public void W669_Pass_ReadError_EndsTheSession_AndReleasesEveryKey()
        {
            // HallJoy's loop counts a read error and keeps reading
            // (aula_w669_backend.cpp:508-523, docs/v1.4/RISK_REGISTER.md:100).
            // The protocol document ends the session instead
            // (docs/protocols/AULA_WIN60HE_STANDARD_PROTOCOL.md:202-204, 225-227).
            var (io, _, session) = W669();
            Assert.True(session.Start(io));
            var output = new AnalogKeyInputState();
            io.Device.QueueInput(W669Live(2, 2, 340));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
            io.Device.Gone = true;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
            Assert.Equal(0, output.Count);
        }

        [Fact]
        public void W669_Stop_Unsubscribes_Once()
        {
            // aula_w669_backend.cpp:601.
            var (io, _, session) = W669();
            Assert.True(session.Start(io));
            session.Stop(io);
            Assert.Equal(W669Frame("01 21 00 00 00 00 03"), io.Attempts.Last().Data);
            Assert.Equal("out", io.Attempts.Last().Kind);
            int count = io.Attempts.Count;
            session.Stop(io);
            Assert.Equal(count, io.Attempts.Count);
        }

        [Fact]
        public void W669_K673_LeavesFnAndTheEncoderUnpublished()
        {
            // aula_w669_protocol.cpp:135-137: positions 15 (encoder) and 121
            // (Fn) carry no key on the K673.
            var (io, kb, session) = W669();
            kb.Product = "7272BRHEXYXK673JCARGB";
            kb.Records.Clear();
            Assert.True(session.Start(io));
            var output = new AnalogKeyInputState();
            io.Device.QueueInput(W669Live(0, 15, 340), W669Live(5, 11, 340));
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            Assert.DoesNotContain(AnalogKeyCodes.Fn, session.KeyOrder);
            // Position 100 is the ABNT2 /? key, International1 (0x87).
            io.Device.QueueInput(W669Live(4, 12, 340));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(1f, output.Get(0x87));
        }
    }
}
