using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Common.AnalogKeyboard;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Soup and AnalogSense families with the corrections and additions
    /// the other readers carry (issue #468): Razer's own report 11 parser and
    /// key table and its 8KHz models (Synapse Web), HallJoy's UAP overlay for
    /// DrunkDeer, Keychron and Madlions, HallJoy's Keychron and DrunkDeer
    /// catalogs, and HallJoy's Wooting split-key aliases. Byte fixtures are
    /// what Windows hands ReadFile: byte 0 is the report ID, or 0 for a
    /// collection without one.
    /// </summary>
    public class AnalogKeyboardSoupFamilyTests
    {
        private static AnalogKeyInputState Keys() => new AnalogKeyInputState();

        private static byte[] Report(int length, params byte[] head)
        {
            var r = new byte[length];
            Array.Copy(head, r, head.Length);
            return r;
        }

        // ── Razer ──

        [Theory]
        [InlineData(0x02CF, "Razer Huntsman V3 Pro 8KHz")]
        [InlineData(0x02D0, "Razer Huntsman V3 Pro Tenkeyless 8KHz")]
        [InlineData(0x02D1, "Razer Huntsman V3 Pro Mini 8KHz")]
        [InlineData(0x02D8, "Razer Huntsman Signature Edition")]
        [InlineData(0x02E4, "Razer Huntsman V3 HE Magnetic Mini 65% 8KHz")]
        [InlineData(0x02E5, "Razer Huntsman V3 Tenkeyless 8KHz")]
        [InlineData(0x02E6, "Razer Huntsman V3 Pro Low-profile Tenkeyless 8KHz")]
        [InlineData(0x02EA, "Razer Huntsman V3 HE Magnetic Tenkeyless 8KHz")]
        [InlineData(0x02A6, "Razer Huntsman V3 Pro")]
        public void Razer8KHzModels_AreReadOnReport11_WithSynapsesNames(int pid, string name)
        {
            // The deviceName of each Synapse Web product config.
            bool Id11(byte id) => id == 11;
            Assert.Equal(AnalogKeyboardProtocol.RazerHuntsmanV3,
                AnalogKeyboardCatalog.Identify(0x1532, (ushort)pid, 1, 0, Id11));
            Assert.Equal(name, AnalogKeyboardCatalog.ModelName(AnalogKeyboardProtocol.RazerHuntsmanV3, 0x1532, (ushort)pid));
            Assert.True(AnalogKeyboardCatalog.NeedsSynapse(AnalogKeyboardProtocol.RazerHuntsmanV3));
        }

        [Fact]
        public void RazerOtherProducts_AreNotHuntsmanV3()
        {
            bool Id11(byte id) => id == 11;
            Assert.Equal(AnalogKeyboardProtocol.None, AnalogKeyboardCatalog.Identify(0x1532, 0x02C0, 1, 0, Id11));
            Assert.Equal(AnalogKeyboardProtocol.None, AnalogKeyboardCatalog.Identify(0x1532, 0x02E7, 1, 0, Id11));
        }

        [Fact]
        public void RazerLowProfile8KHz_UsesItsShorterFullScale()
        {
            // eventDataSize 45864 in the 0x02E6 config (2.8 mm at 1638 per
            // 0.1 mm), 65535 on the rest.
            Assert.Equal(45864f, AnalogKeyboardCatalog.RazerFullScale(0x02E6));
            Assert.Equal(65535f, AnalogKeyboardCatalog.RazerFullScale(0x02CF));
            var raw = Report(48, 0x0B, 0x12, 0xB3, 0x28);
            var k = Keys();
            Assert.True(AnalogKeyboardParsers.ParseRazerHuntsmanV3(raw, k, 0x02E6));
            Assert.Equal(1f, k.Get(AnalogKeyCodes.W), 4);
            var half = Report(48, 0x0B, 0x12, 0x59, 0x94);
            AnalogKeyboardParsers.ParseRazerHuntsmanV3(half, k, 0x02E6);
            Assert.Equal(0x5994 / 45864f, k.Get(AnalogKeyCodes.W), 5);
        }

        [Fact]
        public void RazerV3_ZeroTravel_IsNotAKey_AndOtherReportsLeaveTheState()
        {
            var k = Keys();
            k.Set(AnalogKeyCodes.W, 0.5f);
            Assert.False(AnalogKeyboardParsers.ParseRazerHuntsmanV3(Report(48, 0x07, 0x12, 0xFF, 0xFF), k, 0x02A6));
            Assert.Equal(0.5f, k.Get(AnalogKeyCodes.W));
            Assert.True(AnalogKeyboardParsers.ParseRazerHuntsmanV3(Report(48, 0x0B, 0x12, 0x00, 0x00), k, 0x02A6));
            Assert.Equal(0, k.Count);
        }

        [Fact]
        public void RazerV3_ReadsAllFifteenEntries()
        {
            // 47 data bytes hold 15 complete (key, u16) entries.
            var raw = new byte[48];
            raw[0] = 0x0B;
            int[] razer = { 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x11, 0x12, 0x13, 0x14, 0x15 };
            for (int i = 0; i < razer.Length; i++)
            {
                raw[1 + i * 3] = (byte)razer[i];
                raw[2 + i * 3] = 0x80;
            }
            var k = Keys();
            AnalogKeyboardParsers.ParseRazerHuntsmanV3(raw, k, 0x02CF);
            Assert.Equal(15, k.Count);
            Assert.Equal(0x8000 / 65535f, k.Get(AnalogKeyCodes.T), 5);
        }

        // ── Wooting split keys ──

        [Theory]
        [InlineData(0x1340, 4, AnalogKeyCodes.LeftSpace)]
        [InlineData(0x1340, 8, AnalogKeyCodes.RightSpace)]
        [InlineData(0x1340, 6, AnalogKeyCodes.CenterFn)]
        [InlineData(0x1340, 13, AnalogKeyCodes.RightFn)]
        [InlineData(0x1340, 12, 0)]
        [InlineData(0x1410, 12, AnalogKeyCodes.RightFn)]
        [InlineData(0x1410, 13, 0)]
        [InlineData(0x1342, 4, AnalogKeyCodes.LeftSpace)]
        [InlineData(0x1411, 8, AnalogKeyCodes.RightSpace)]
        [InlineData(0x1230, 4, 0)]
        public void WootingSplitKey_FollowsHallJoysPhysicalMap(int pid, int column, int expected)
        {
            // halljoy_wooting_physical.h: row 5 only, row in bits 5 to 7.
            Assert.Equal(expected, AnalogKeyboardParsers.WootingSplitKey((ushort)pid, (5 << 5) | column));
            Assert.Equal(0, AnalogKeyboardParsers.WootingSplitKey((ushort)pid, (4 << 5) | column));
        }

        [Fact]
        public void WootingV2_SplitBoard_PublishesTheAlias_AndTheOrdinaryKey()
        {
            // Left Space half: matrix (5,4) = 0xA4, Space 0x2C at 1023.
            // Right Space half: matrix (5,8) = 0xA8, Space at 512.
            var raw = Report(65, 0x00,
                0xA4, 0x2C, 0xC1, 0xFF,
                0xA8, 0x2C, 0x00, 0x80);
            var k = Keys();
            Assert.True(AnalogKeyboardParsers.ParseWootingV2(raw, k, 0x1340));
            Assert.Equal(1f, k.Get(AnalogKeyCodes.LeftSpace));
            Assert.Equal(512 / 1023f, k.Get(AnalogKeyCodes.RightSpace), 5);
            // The ordinary code takes the deeper half, as HallJoy's plugin
            // host merges records of one code (UniversalAnalogPluginFixed
            // main.cpp:685-704).
            Assert.Equal(1f, k.Get(AnalogKeyCodes.Space));

            var other = Keys();
            AnalogKeyboardParsers.ParseWootingV2(raw, other, 0x1230);
            Assert.Equal(0f, other.Get(AnalogKeyCodes.LeftSpace));
            Assert.Equal(1, other.Count);
        }

        [Fact]
        public void WootingSplitBoards_ListTheirAliases()
        {
            var keys = AnalogKeyboardCatalog.KeysFor(0x31E3, 0x1341);
            Assert.Contains(AnalogKeyCodes.LeftSpace, keys);
            Assert.Contains(AnalogKeyCodes.RightFn, keys);
            Assert.Contains(AnalogKeyCodes.Space, keys);
            Assert.DoesNotContain(AnalogKeyCodes.LeftSpace, AnalogKeyboardCatalog.KeysFor(0x31E3, 0x1230));
        }

        // ── DrunkDeer ──

        private static byte[] DrunkDeerIdentity(byte a, byte b, byte c)
        {
            var r = new byte[64];
            r[0] = 0x04;
            r[1] = 0xA0;
            r[2] = 0x02;
            r[5] = a;
            r[6] = b;
            r[7] = c;
            return r;
        }

        private static byte[] DrunkDeerChunk(int index, params (int at, byte value)[] keys)
        {
            var a = new byte[64];
            a[0] = 0x04;
            a[1] = 0xB7;
            a[4] = (byte)index;
            foreach (var (at, value) in keys) a[5 + at] = value;
            return a;
        }

        [Fact]
        public void DrunkDeerIdentity_RequestBytes()
        {
            // halljoy_drunkdeer_identity.h Request(): 04 A0 02, 64 bytes.
            var r = DrunkDeerPoller.IdentityRequest();
            Assert.Equal(64, r.Length);
            Assert.Equal(new byte[] { 0x04, 0xA0, 0x02, 0x00 }, r.Take(4).ToArray());
            Assert.All(r.Skip(3), b => Assert.Equal(0, b));
        }

        [Theory]
        [InlineData(11, 1, 1, DrunkDeerModel.A75Ansi)]
        [InlineData(11, 4, 1, DrunkDeerModel.A75Ansi)]
        [InlineData(11, 4, 3, DrunkDeerModel.A75Pro)]
        [InlineData(11, 4, 2, DrunkDeerModel.A75Iso)]
        [InlineData(11, 3, 1, DrunkDeerModel.G60)]
        [InlineData(11, 2, 1, DrunkDeerModel.G65)]
        [InlineData(15, 1, 1, DrunkDeerModel.G65)]
        [InlineData(11, 4, 5, DrunkDeerModel.G75Ansi)]
        [InlineData(11, 4, 7, DrunkDeerModel.G75Jis)]
        [InlineData(11, 4, 9, DrunkDeerModel.Unknown)]
        public void DrunkDeerIdentity_SignaturesMatchHallJoy(int a, int b, int c, DrunkDeerModel model)
        {
            // halljoy_drunkdeer_identity.h Parse, bytes 5 to 7.
            Assert.Equal(model, DrunkDeerPoller.ParseIdentity(DrunkDeerIdentity((byte)a, (byte)b, (byte)c)));
        }

        [Fact]
        public void DrunkDeerIdentity_RejectsShortOrForeignAnswers()
        {
            var good = DrunkDeerIdentity(11, 3, 1);
            Assert.Equal(DrunkDeerModel.Unknown, DrunkDeerPoller.ParseIdentity(good.AsSpan(0, 63)));
            var wrongCommand = (byte[])good.Clone();
            wrongCommand[1] = 0xB7;
            Assert.Equal(DrunkDeerModel.Unknown, DrunkDeerPoller.ParseIdentity(wrongCommand));
            var wrongByte3 = (byte[])good.Clone();
            wrongByte3[3] = 1;
            Assert.Equal(DrunkDeerModel.Unknown, DrunkDeerPoller.ParseIdentity(wrongByte3));
        }

        [Fact]
        public void DrunkDeerStart_NamesTheModel_AndReadsThroughItsMap()
        {
            // A G60 (0x2384): Esc sits at offset 21, where the generic table
            // has the grave accent.
            var io = new AnalogKeyboardTestTransport { InputLength = 64, OutputLength = 64 };
            io.OnSend = req => req[1] == 0xA0
                ? new[] { DrunkDeerIdentity(11, 3, 1) }
                : new[] { DrunkDeerChunk(0, (21, 40)), DrunkDeerChunk(1), DrunkDeerChunk(2) };
            var poller = new DrunkDeerPoller(0x2384);
            Assert.True(poller.Start(io));
            Assert.Equal(DrunkDeerModel.G60, poller.Model);
            Assert.Equal("DrunkDeer G60 ANSI", poller.ModelName);
            Assert.Contains(AnalogKeyCodes.Escape, poller.KeyOrder);
            Assert.DoesNotContain(AnalogKeyCodes.F1, poller.KeyOrder);

            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, poller.Pass(io, output, null));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.Escape));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.Backquote));
        }

        [Fact]
        public void DrunkDeerStart_ModelFromAnotherProduct_KeepsTheGenericTable()
        {
            // A G60 signature from the A75's product ID is not taken.
            var io = new AnalogKeyboardTestTransport { InputLength = 64 };
            io.OnSend = req => new[] { DrunkDeerIdentity(11, 3, 1) };
            var poller = new DrunkDeerPoller(0x2383);
            Assert.True(poller.Start(io));
            Assert.Equal(DrunkDeerModel.Unknown, poller.Model);
            Assert.Null(poller.ModelName);
        }

        [Fact]
        public void DrunkDeerStart_UnknownProduct_AsksNothing()
        {
            var io = new AnalogKeyboardTestTransport();
            Assert.True(new DrunkDeerPoller(0x2399).Start(io));
            Assert.Empty(io.Log);
        }

        [Fact]
        public void DrunkDeerStart_Silent_StillReads_GoneFails()
        {
            var silent = new AnalogKeyboardTestTransport();
            Assert.True(new DrunkDeerPoller(0x2383).Start(silent));
            Assert.Single(silent.Writes("out"));
            var gone = new AnalogKeyboardTestTransport();
            gone.OnSend = req =>
            {
                gone.Gone = true;
                return Array.Empty<byte[]>();
            };
            Assert.False(new DrunkDeerPoller(0x2383).Start(gone));
        }

        [Theory]
        [InlineData("length")]
        [InlineData("command")]
        [InlineData("chunk3")]
        [InlineData("duplicate")]
        public void DrunkDeerPass_BadFrame_IsAMiss(string defect)
        {
            // HallJoy's overlay: 64 bytes, 04 B7, chunk below 3, each once.
            var chunks = new List<byte[]> { DrunkDeerChunk(0), DrunkDeerChunk(1), DrunkDeerChunk(2) };
            switch (defect)
            {
                case "length": chunks[1] = chunks[1].Take(63).ToArray(); break;
                case "command": chunks[1][1] = 0xB6; break;
                case "chunk3": chunks[1][4] = 3; break;
                case "duplicate": chunks[1][4] = 0; break;
            }
            var io = new AnalogKeyboardTestTransport();
            io.OnSend = req => chunks;
            Assert.Equal(AnalogPollResult.NoAnswer, new DrunkDeerPoller().Pass(io, Keys(), null));
        }

        [Fact]
        public void DrunkDeerMaps_MatchHallJoysPerModelDepartures()
        {
            int At(string table, int row, int col) => AnalogKeyboardData.Table("drunkdeer.json", table)[row * 21 + col];
            // halljoy_drunkdeer_maps.h
            Assert.Equal(AnalogKeyCodes.Escape, At("g60", 1, 0));
            Assert.Equal(AnalogKeyCodes.Delete, At("g65", 1, 14));
            Assert.Equal(AnalogKeyCodes.PageDown, At("g65", 4, 14));
            Assert.Equal(AnalogKeyCodes.PrintScreen, At("g75_ansi", 0, 13));
            Assert.Equal(AnalogKeyCodes.F1, At("g75_ansi", 0, 1));
            Assert.Equal(AnalogKeyCodes.IntlHash, At("a75_iso", 3, 12));
            Assert.Equal(AnalogKeyCodes.IntlBackslash, At("a75_iso", 4, 1));
            Assert.Equal(0, At("a75_iso", 2, 13));
            Assert.Equal(AnalogKeyCodes.IntlYen, At("g75_jis", 1, 13));
            Assert.Equal(AnalogKeyCodes.IntlRo, At("g75_jis", 4, 12));
            // The A75 ANSI and Pro maps equal the generic table.
            Assert.Equal(AnalogKeyboardData.Table("drunkdeer.json", "generic"), AnalogKeyboardData.Table("drunkdeer.json", "a75_ansi"));
            Assert.Equal(AnalogKeyboardData.Table("drunkdeer.json", "generic"), AnalogKeyboardData.Table("drunkdeer.json", "a75_pro"));
            foreach (var name in new[] { "generic", "a75_ansi", "a75_pro", "a75_iso", "g60", "g65", "g75_ansi", "g75_jis" })
                Assert.Equal(126, AnalogKeyboardData.Table("drunkdeer.json", name).Length);
        }

        // ── Keychron and Lemokey ──

        [Fact]
        public void KeychronCatalog_HasEveryHallJoyIdentity_AndTheLemokeys()
        {
            var boards = AnalogKeyboardCatalog.KeychronBoards;
            // HallJoy's 39 identities, the K6 HE ISO and JIS from
            // paysdelest's Soup fork, and the two Lemokey P1 HE boards.
            Assert.Equal(43, boards.Count);
            Assert.Equal(41, boards.Count(b => b.VendorId == 0x3434));
            Assert.Equal("Keychron K6 HE ISO", AnalogKeyboardCatalog.KeychronModel(0x3434, 0x0E61).Name);
            Assert.Equal(AnalogKeyboardCatalog.KeychronLayout(0x3434, 0x0E60).Keys,
                AnalogKeyboardCatalog.KeychronLayout(0x3434, 0x0E62).Keys);
            Assert.Equal(2, boards.Count(b => b.VendorId == 0x362D));
            foreach (var b in boards)
            {
                Assert.Equal(b.Layout.Rows * b.Layout.Columns, b.Layout.Size);
                Assert.Contains(AnalogKeyCodes.W, b.Layout.Keys);
                Assert.StartsWith(b.VendorId == 0x3434 ? "Keychron " : "Lemokey ", b.Name);
                Assert.Equal(AnalogKeyboardProtocol.Keychron,
                    AnalogKeyboardCatalog.Identify(b.VendorId, b.ProductId, 0xFF60, 0x61, null));
            }
            Assert.Equal(boards.Count, boards.Select(b => (b.VendorId, b.ProductId)).Distinct().Count());
        }

        [Theory]
        [InlineData(0x0E60, "Keychron K6 HE ANSI", 5, 15)]
        [InlineData(0x0B40, "Keychron Q4 HE ANSI", 5, 14)]
        [InlineData(0x1060, "Keychron Q6 HE 8K ANSI", 6, 22)]
        [InlineData(0x1032, "Keychron Q3 HE 8K JIS", 6, 18)]
        [InlineData(0x0EA1, "Keychron K10 HE ISO", 6, 21)]
        [InlineData(0x0E40, "Keychron K4 HE ANSI", 6, 19)]
        public void KeychronCatalog_ShapesAndNames(int pid, string name, int rows, int cols)
        {
            // keychron_layout_identities.h
            var board = AnalogKeyboardCatalog.KeychronModel(0x3434, (ushort)pid);
            Assert.Equal(name, board.Name);
            Assert.Equal(rows, board.Layout.Rows);
            Assert.Equal(cols, board.Layout.Columns);
            Assert.Equal(name, AnalogKeyboardCatalog.ModelName(AnalogKeyboardProtocol.Keychron, 0x3434, (ushort)pid));
        }

        [Fact]
        public void KeychronCatalog_UsesKeychronsOwnMatrices()
        {
            var q3 = AnalogKeyboardCatalog.KeychronLayout(0x3434, 0x0B30);
            // Slash at row 4 column 11, nothing at column 12.
            Assert.Equal(AnalogKeyCodes.Slash, q3.Keys[4 * 16 + 11]);
            Assert.Equal(0, q3.Keys[4 * 16 + 12]);
            // Q1 and Q5 row 5 column 9 is Right Alt.
            Assert.Equal(AnalogKeyCodes.RAlt, AnalogKeyboardCatalog.KeychronLayout(0x3434, 0x0B10).Keys[5 * 15 + 9]);
            Assert.Equal(AnalogKeyCodes.RAlt, AnalogKeyboardCatalog.KeychronLayout(0x3434, 0x0B50).Keys[5 * 19 + 9]);
            // K2 ISO carries its regional keys.
            var k2iso = AnalogKeyboardCatalog.KeychronLayout(0x3434, 0x0E21);
            Assert.Equal(AnalogKeyCodes.Enter, k2iso.Keys[2 * 16 + 13]);
            Assert.Equal(AnalogKeyCodes.IntlHash, k2iso.Keys[3 * 16 + 12]);
            Assert.Equal(AnalogKeyCodes.IntlBackslash, k2iso.Keys[4 * 16 + 1]);
            Assert.Equal(AnalogKeyCodes.Backslash, AnalogKeyboardCatalog.KeychronLayout(0x3434, 0x0E20).Keys[2 * 16 + 13]);
        }

        [Theory]
        [InlineData(70, 3)]
        [InlineData(75, 3)]
        [InlineData(90, 4)]
        [InlineData(96, 4)]
        [InlineData(114, 4)]
        [InlineData(120, 5)]
        [InlineData(132, 5)]
        public void KeychronFullReports_SlotsOver30PlusOne(int slots, int answers)
        {
            // HallJoy's overlay, and FAR's get_realtime_travel_all, which
            // always sends one report after the last full one.
            Assert.Equal(answers, KeychronPoller.FullReportCount(slots));
        }

        private static byte[] KeychronAnswer(byte command, params (int at, byte value)[] data)
        {
            var a = new byte[33];
            a[1] = 0xA9;
            a[2] = command;
            foreach (var (at, value) in data) a[1 + at] = value;
            return a;
        }

        [Fact]
        public void KeychronK6_FarFirmware_ReadsThreeAnswers()
        {
            // K6 HE: 5 x 15 = 75 slots, three answers. The K6 has no function
            // row, so W is row 1 column 2 = index 17, answer 0 byte 17.
            var k6 = AnalogKeyboardCatalog.KeychronLayout(0x3434, 0x0E60);
            Assert.Equal(AnalogKeyCodes.W, k6.Keys[17]);
            var io = new AnalogKeyboardTestTransport();
            io.OnSend = req => req[2] switch
            {
                0x01 => new[] { KeychronAnswer(0x01, (2, 4), (31, 0x45)) },
                0x31 => new[] { KeychronAnswer(0x31, (2 + 17, 235)), KeychronAnswer(0x31), KeychronAnswer(0x31) },
                _ => Array.Empty<byte[]>(),
            };
            var poller = new KeychronPoller(k6);
            Assert.True(poller.Start(io));
            Assert.True(poller.FullReports);
            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, poller.Pass(io, output, null));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(0, io.PendingInput);
        }

        [Fact]
        public void KeychronFar_ShortAnswer_IsAMiss()
        {
            var io = new AnalogKeyboardTestTransport();
            io.OnSend = req => req[2] switch
            {
                0x01 => new[] { KeychronAnswer(0x01, (2, 4), (31, 0x45)) },
                0x31 => new[] { KeychronAnswer(0x31), KeychronAnswer(0x31).Take(20).ToArray() },
                _ => Array.Empty<byte[]>(),
            };
            var poller = new KeychronPoller(AnalogKeyboardCatalog.KeychronLayout(0x3434, 0x0E60));
            Assert.True(poller.Start(io));
            Assert.Equal(AnalogPollResult.NoAnswer, poller.Pass(io, Keys(), null));
        }

        [Fact]
        public void KeychronStart_NeedsAVersionAnswer()
        {
            // HallJoy's overlay drops a board whose A9 01 answer is missing
            // or shorter than three bytes.
            var silent = new AnalogKeyboardTestTransport();
            Assert.False(new KeychronPoller(AnalogKeyboardCatalog.KeychronLayout(0x3434, 0x0B10)).Start(silent));
            var shortAnswer = new AnalogKeyboardTestTransport();
            shortAnswer.OnSend = req => new[] { new byte[] { 0x00, 0xA9, 0x01 } };
            Assert.False(new KeychronPoller(AnalogKeyboardCatalog.KeychronLayout(0x3434, 0x0B10)).Start(shortAnswer));
            Assert.Equal(new byte[] { 0x00, 0xA9, 0x01 }, shortAnswer.Writes("out")[0].Take(3).ToArray());
        }

        [Fact]
        public void KeychronStock_ReadsEveryBoard_AndAShortAnswerIsAMiss()
        {
            // Stock firmware (no 0x45) on a K6, which HallJoy reads only on
            // FAR firmware: A9 30 row col answers with the travel at byte 6.
            var io = new AnalogKeyboardTestTransport();
            io.OnSend = req => req[2] switch
            {
                0x01 => new[] { KeychronAnswer(0x01, (2, 4)) },
                0x30 => new[] { req[3] == 1 && req[4] == 2 ? KeychronAnswer(0x30, (6, 235)) : KeychronAnswer(0x30) },
                _ => Array.Empty<byte[]>(),
            };
            var poller = new KeychronPoller(AnalogKeyboardCatalog.KeychronLayout(0x3434, 0x0E60));
            Assert.True(poller.Start(io));
            Assert.False(poller.FullReports);
            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, poller.Pass(io, output, code => code == AnalogKeyCodes.W));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));

            var cut = new AnalogKeyboardTestTransport();
            cut.OnSend = req => req[2] switch
            {
                0x01 => new[] { KeychronAnswer(0x01, (2, 4)) },
                0x30 => new[] { KeychronAnswer(0x30).Take(7).ToArray() },
                _ => Array.Empty<byte[]>(),
            };
            var stock = new KeychronPoller(AnalogKeyboardCatalog.KeychronLayout(0x3434, 0x0E60));
            Assert.True(stock.Start(cut));
            Assert.Equal(AnalogPollResult.NoAnswer, stock.Pass(cut, Keys(), null));
        }

        // ── Madlions ──

        private static byte[] MadlionsAnswer(params (int key, int travel)[] keys)
        {
            var a = new byte[33];
            foreach (var (key, travel) in keys)
            {
                a[1 + 7 + key * 5 + 3] = (byte)(travel >> 8);
                a[1 + 7 + key * 5 + 4] = (byte)travel;
            }
            return a;
        }

        [Fact]
        public void Madlions_TravelIsClampedTo350()
        {
            Span<ushort> travel = stackalloc ushort[4];
            Assert.True(MadlionsPoller.ReadAnswer(MadlionsAnswer((0, 400), (1, 175)), travel));
            Assert.Equal(350, travel[0]);
            Assert.Equal(175, travel[1]);
            Assert.False(MadlionsPoller.ReadAnswer(new byte[27], travel));
            Assert.True(MadlionsPoller.ReadAnswer(new byte[28], travel));
        }

        [Fact]
        public void Madlions_UnansweredGroup_IsZeroed_TheGroupsBeforeItPublish_AndEightFailuresEndTheSession()
        {
            // MAD60HE: W is index 16. A held key in the group before W's and
            // one in the group after read at full travel with W, then W's
            // group stops answering. HallJoy's overlay (AnalogueKeyboard.cpp:1312-1347)
            // zeroes the group, publishes the keys it collected before it,
            // not those after, and ends the device at the eighth failure.
            var keys = AnalogKeyCodes.MadlionsMad60He.Keys;
            int early = FirstKeyIn(keys, 12), late = FirstKeyIn(keys, 20);
            Assert.True(early >= 0 && late >= 0);
            bool answer = true;
            var io = new AnalogKeyboardTestTransport();
            io.OnSend = req =>
            {
                int offset = req[7];
                if (offset == 16 && !answer) return Array.Empty<byte[]>();
                if (offset == 12) return new[] { MadlionsAnswer((early - 12, 350)) };
                if (offset == 16) return new[] { MadlionsAnswer((0, 350)) };
                if (offset == 20) return new[] { MadlionsAnswer((late - 20, 350)) };
                return new[] { MadlionsAnswer() };
            };
            Func<int, bool> held = code => code == AnalogKeyCodes.W || code == keys[early] || code == keys[late];
            var poller = new MadlionsPoller(AnalogKeyCodes.MadlionsMad60He);
            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, poller.Pass(io, output, held));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(1f, output.Get(keys[early]));
            Assert.Equal(1f, output.Get(keys[late]));

            answer = false;
            for (int i = 1; i < MadlionsPoller.FailuresTolerated; i++)
            {
                Assert.Equal(AnalogPollResult.Ok, poller.Pass(io, output, held));
                Assert.Equal(0f, output.Get(AnalogKeyCodes.W));
                Assert.Equal(1f, output.Get(keys[early]));
                Assert.Equal(0f, output.Get(keys[late]));
            }
            Assert.Equal(AnalogPollResult.Failed, poller.Pass(io, output, held));

            // One good answer clears the count.
            var again = new MadlionsPoller(AnalogKeyCodes.MadlionsMad60He);
            for (int i = 1; i < MadlionsPoller.FailuresTolerated; i++)
                again.Pass(io, Keys(), held);
            answer = true;
            Assert.Equal(AnalogPollResult.Ok, again.Pass(io, Keys(), held));
            answer = false;
            for (int i = 1; i < MadlionsPoller.FailuresTolerated; i++)
                Assert.Equal(AnalogPollResult.Ok, again.Pass(io, Keys(), held));
            Assert.Equal(AnalogPollResult.Failed, again.Pass(io, Keys(), held));
        }

        [Fact]
        public void Madlions_FailedWrite_IsAGroupFailure_ButAGoneDeviceEndsThePass()
        {
            // HallJoy's transactReport returns nothing for a failed write, so
            // it counts against the group (AnalogueKeyboard.cpp:1308-1345).
            var io = new AnalogKeyboardTestTransport();
            io.OnSend = req => new[] { MadlionsAnswer() };
            io.FailSend = req => req[7] == 16;
            var poller = new MadlionsPoller(AnalogKeyCodes.MadlionsMad60He);
            Func<int, bool> held = code => code == AnalogKeyCodes.W;
            for (int i = 1; i < MadlionsPoller.FailuresTolerated; i++)
                Assert.Equal(AnalogPollResult.Ok, poller.Pass(io, Keys(), held));
            Assert.Equal(AnalogPollResult.Failed, poller.Pass(io, Keys(), held));

            var gone = new AnalogKeyboardTestTransport { Gone = true };
            Assert.Equal(AnalogPollResult.Failed, new MadlionsPoller(AnalogKeyCodes.MadlionsMad60He).Pass(gone, Keys(), held));
        }

        /// <summary>The first index of the four from <paramref name="offset"/>
        /// that holds a key, or -1.</summary>
        private static int FirstKeyIn(int[] keys, int offset)
        {
            for (int i = offset; i < offset + 4 && i < keys.Length; i++)
                if (keys[i] != AnalogKeyCodes.None) return i;
            return -1;
        }

        [Fact]
        public void Madlions_RotationWrapsAfterCeilOfKeysOver16()
        {
            // MAD60HE: 70 slots, five blocks of sixteen (the last partial),
            // so the rotating block visits offsets 0, 16, 32, 48, 64, then 0.
            var firstOffsets = new List<int>();
            var io = new AnalogKeyboardTestTransport();
            io.OnSend = req => new[] { MadlionsAnswer() };
            var poller = new MadlionsPoller(AnalogKeyCodes.MadlionsMad60He);
            for (int pass = 0; pass < 6; pass++)
            {
                io.Log.Clear();
                poller.Pass(io, Keys(), null);
                firstOffsets.Add(io.Writes("out")[0][7]);
            }
            Assert.Equal(new[] { 0, 16, 32, 48, 64, 0 }, firstOffsets.ToArray());
        }

        // ── Listening for the 0xA0 event ──

        private static AnalogKeyboardDeviceInfo A0Collection(ushort vid, ushort pid) => new()
        {
            VendorId = vid,
            ProductId = pid,
            UsagePage = 1,
            Usage = 0,
            InputReportLength = 65,
            OutputReportLength = 65,
            HasInputReport = id => id == 0,
            HasOutputReport = id => id == 0,
        };

        [Fact]
        public void A0Listen_ReadsKeyAndBigEndianValue()
        {
            // HallAnalogMapper.py:809-813: key = byte 3 after the A0 type,
            // value = bytes 4 and 5, big-endian. Windows puts report ID 0 first.
            Assert.True(A0ListenRoute.TryParse(Report(65, 0x00, 0xA0, 0x00, 0x00, 0x1A, 0x03, 0x20), out int code, out int value));
            Assert.Equal(AnalogKeyCodes.W, code);
            Assert.Equal(0x0320, value);
            Assert.Equal(0x0320 / 1600f, A0ListenRoute.Depth(value), 5);
            // HallAnalogMapper.py:113-119: 30 or less is rest, 1600 the bottom.
            Assert.Equal(0f, A0ListenRoute.Depth(30));
            Assert.Equal(1f, A0ListenRoute.Depth(2000));
        }

        [Theory]
        [InlineData(0x05, 0xA0, 0x1A)] // a numbered report
        [InlineData(0x00, 0xA1, 0x1A)] // another event type
        [InlineData(0x00, 0xA0, 0x02)] // not a keyboard usage
        [InlineData(0x00, 0xA0, 0xB0)] // past the keypad, below the modifiers
        public void A0Listen_RejectsWhatIsNotAnEvent(int reportId, int type, int key)
        {
            Assert.False(A0ListenRoute.TryParse(Report(65, (byte)reportId, (byte)type, 0x00, 0x00, (byte)key, 0x01, 0x00), out _, out _));
        }

        [Fact]
        public void A0Listen_OnlyWhereNoOtherRouteApplies()
        {
            // An unknown vendor's collection of the event's shape is listened to.
            Assert.True(A0ListenRoute.Matches(A0Collection(0x1234, 0x5678)));
            // NuPhy's collection has a route of its own.
            Assert.False(A0ListenRoute.Matches(A0Collection(0x19F5, 0x6130)));
            // A numbered or shorter report is not the event's shape.
            var numbered = A0Collection(0x1234, 0x5678);
            numbered.HasInputReport = id => id == 4;
            Assert.False(A0ListenRoute.Matches(numbered));
            var short33 = A0Collection(0x1234, 0x5678);
            short33.InputReportLength = 33;
            Assert.False(A0ListenRoute.Matches(short33));
            var vendorPage = A0Collection(0x1234, 0x5678);
            vendorPage.UsagePage = 0xFF00;
            Assert.False(A0ListenRoute.Matches(vendorPage));
            // It sends nothing and shows a row only after an event.
            Assert.False(A0ListenRoute.Route.Writable);
            Assert.True(A0ListenRoute.Route.RegisterOnFirstReport);
            Assert.Same(A0ListenRoute.Route, AnalogKeyboardRoutes.All[^1]);
        }

        [Fact]
        public void A0Listen_SessionUpdatesOneKeyPerEvent_AndSendsNothing()
        {
            var io = new AnalogKeyboardTestTransport();
            io.QueueInput(
                Report(65, 0x00, 0xA0, 0x00, 0x00, 0x1A, 0x06, 0x40),
                Report(65, 0x00, 0xA0, 0x00, 0x00, 0x16, 0x03, 0x20),
                Report(65, 0x00, 0x01, 0x02),
                Report(65, 0x00, 0xA0, 0x00, 0x00, 0x1A, 0x00, 0x00));
            var session = new A0ListenSession();
            Assert.True(session.Start(io));
            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(0x0320 / 1600f, output.Get(AnalogKeyCodes.S), 5);
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            Assert.Empty(io.Log);
            io.Gone = true;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, output, null));
        }

        // ── KeyAxis (0416:7372) ──

        private static AnalogKeyboardDeviceInfo M484Collection() => new()
        {
            VendorId = 0x0416,
            ProductId = 0x7372,
            UsagePage = 0xFF1B,
            Usage = 0x91,
            InputReportLength = 64,
            OutputReportLength = 64,
            HasInputReport = id => id == 1,
            HasOutputReport = id => id == 1,
        };

        [Fact]
        public void KeyAxis_GetsTheCollectionAfterTheNa87Route()
        {
            // HallJoy's NA87 route proves the two board identities it knows
            // first, and KeyAxis's arm reads the rest.
            var ids = AnalogKeyboardRoutes.Candidates(M484Collection()).Select(r => r.Id).ToArray();
            Assert.Equal(new[] { "halljoy-irok-na87-m484", KeyAxisRoute.Id }, ids);
            var other = M484Collection();
            other.ProductId = 0x7373;
            Assert.False(KeyAxisRoute.Matches(other));
        }

        [Fact]
        public void KeyAxis_ArmsAndDisarms_WithItsExactBytes()
        {
            // PROTOCOL.md:90-111, app.py:116-121.
            Assert.Equal(new byte[]
            {
                0x01, 0x21, 0x00, 0x00, 0x00, 0x18, 0x02,
                0x3E, 0x26, 0x3E, 0x1E, 0x1E, 0x1E, 0x3E, 0x1E, 0x1E, 0x3E, 0x1E, 0x3E, 0x2E, 0x10, 0x2E, 0x30, 0x3E,
            }, KeyAxisRoute.ArmRequest());
            Assert.Equal(new byte[] { 0x01, 0x21, 0x00, 0x00, 0x00, 0x18, 0x03 }, KeyAxisRoute.DisarmRequest());

            var io = new AnalogKeyboardTestTransport { OutputLength = 64 };
            var session = new KeyAxisSession();
            Assert.True(session.Start(io));
            session.Stop(io);
            session.Stop(io);
            var writes = io.Writes("out");
            Assert.Equal(2, writes.Count);
            Assert.Equal(KeyAxisRoute.ArmRequest(), writes[0]);
            Assert.Equal(KeyAxisRoute.DisarmRequest(), writes[1]);
        }

        [Fact]
        public void KeyAxis_FailedArm_IsDisarmed()
        {
            // A write that failed may still have reached the keyboard, so the
            // failed arm is followed by a disarm.
            var failing = new FailingSendTransport();
            var session = new KeyAxisSession();
            Assert.False(session.Start(failing));
            Assert.Equal(2, failing.Sends);
        }

        private sealed class FailingSendTransport : IAnalogKeyboardTransport
        {
            public int Sends;
            public bool Send(byte[] report) { Sends++; return false; }
            public bool SendOutputReport(byte[] report) => false;
            public int Receive(byte[] buffer, int timeoutMs) => 0;
            public void DiscardStale() { }
            public bool SetFeature(byte[] report) => false;
            public int GetFeature(byte[] buffer) => -1;
            public int InputLength => 64;
            public int OutputLength => 64;
            public int FeatureLength => 0;
        }

        [Fact]
        public void KeyAxis_ReadsTravelFrames_ByMatrixPosition()
        {
            // PROTOCOL.md:116-137: 01 21 .. .. .. 03 .. row col depth, 0 to 40.
            Assert.True(KeyAxisRoute.TryParse(Report(64, 0x01, 0x21, 0, 0, 0, 0x03, 0, 2, 5, 20), out int row, out int col, out int depth));
            Assert.Equal((2, 5, 20), (row, col, depth));
            Assert.False(KeyAxisRoute.TryParse(Report(64, 0x01, 0x21, 0, 0, 0, 0x01, 0, 2, 5, 20), out _, out _, out _));
            Assert.False(KeyAxisRoute.TryParse(Report(64, 0x02, 0x21, 0, 0, 0, 0x03, 0, 2, 5, 20), out _, out _, out _));
            Assert.True(KeyAxisRoute.TryParse(Report(64, 0x01, 0x21, 0, 0, 0, 0x03, 0, 0, 0, 99), out _, out _, out int capped));
            Assert.Equal(40, capped);
            Assert.Equal(AnalogKeyCodes.PositionBase + 2 * 32 + 5, KeyAxisRoute.PositionCode(2, 5));
            Assert.Equal(0, KeyAxisRoute.PositionCode(8, 0));

            var io = new AnalogKeyboardTestTransport();
            io.QueueInput(Report(64, 0x01, 0x21, 0, 0, 0, 0x03, 0, 1, 2, 40), Report(64, 0x01, 0x21, 0, 0, 0, 0x01));
            var session = new KeyAxisSession();
            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(1f, output.Get(KeyAxisRoute.PositionCode(1, 2)));
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
        }

        // ── The registry ──

        [Fact]
        public void NamedMutexes_CarrySoupsNames_AndExcludeAnotherThread()
        {
            // Soup's getActiveKeys* take these unprefixed names
            // (AnalogueKeyboard.cpp:765, 902, 1128).
            Assert.Equal("DrunkDeerMtx", AnalogKeyboardNamedMutex.DrunkDeer);
            Assert.Equal("KeychronMtx", AnalogKeyboardNamedMutex.Keychron);
            Assert.Equal("MadlionsMtx", AnalogKeyboardNamedMutex.Madlions);

            // A name of this test's own, so no poller running beside it waits.
            string name = "PadForgeTestMtx-" + Guid.NewGuid().ToString("N");
            var mutex = AnalogKeyboardNamedMutex.Get(name);
            Assert.NotNull(mutex);
            Assert.Same(mutex, AnalogKeyboardNamedMutex.Get(name));
            Assert.True(mutex.Wait());
            bool other = true;
            var t = new System.Threading.Thread(() => other = mutex.Wait(50));
            t.Start();
            t.Join();
            Assert.False(other);
            mutex.Release();
            t = new System.Threading.Thread(() =>
            {
                other = mutex.Wait(50);
                if (other) mutex.Release();
            });
            t.Start();
            t.Join();
            Assert.True(other);
        }

        [Fact]
        public void EveryRoute_RetriesAndReconnects_OnItsReferencesTimers()
        {
            // Each timer is the wait its reference's worker takes: after an
            // attempt that ran no session (retry) and after a session
            // (reconnect). A probe-once route admits a new keyboard once per
            // plug-in and retries only a keyboard it opened before.
            var expected = new (string Id, int Retry, int Reconnect, bool ProbeOnce, int Tries)[]
            {
                ("attackshark-pro", 3000, 3000, false, 0),            // attackshark_pro_diagnostic.cpp:363-364, 503
                ("halljoy-aula-mini60", 3000, 3000, false, 0),        // aula_mini60_diagnostic.cpp:459-470
                ("halljoy-mad68-a0", 250, 250, true, 0),              // mad68pr_backend.cpp:2169, 2201
                ("halljoy-hex80-0x96", 250, 250, true, 0),            // hex80_backend.cpp:613, 643
                ("halljoy-aula-hero", 1000, 1000, true, 0),           // aula_hero84he_backend.cpp:474-497
                ("halljoy-addressed-ipi", 5000, 500, true, 0),        // addressed_analog_backend.cpp:1510-1533
                ("halljoy-addressed-generic", 5000, 500, true, 0),
                ("halljoy-aula-sparkplayjoy-6x21", 100, 100, false, 0), // aula_win60he_backend.cpp:1960-1963, 2300-2306
                ("halljoy-irok-na87-m484", 1000, 200, false, 0),      // irok_na87_backend.cpp:918
                (KeyAxisRoute.Id, 150, 150, true, 6),                 // KeyAxis app.py:1174-1194
                ("halljoy-aula-w669", 1000, 200, true, 0),            // aula_w669_backend.cpp:610-614
                ("halljoy-irok-mg75-pro", 1000, 1000, false, 0),      // mg75_pro_backend.cpp:425-440
                ("halljoy-chilkey-slice75", 1000, 1000, false, 0),    // slice75_backend.cpp:421
                ("rongyuan-snapshot", 1000, 1000, false, 0),          // rongyuan_snapshot_backend.cpp:354-373
                ("rongyuan-stream", 1000, 1000, false, 0),            // rongyuan_stream_backend.cpp:407-426
                ("halljoy-neo65", 1000, 1000, false, 0),              // neo65_backend.cpp:145-154
                ("halljoy-steelseries-apex", 1000, 1000, false, 0),   // steelseries_apex_backend.cpp:194-203
                ("halljoy-mchose-mix87", 5000, 5000, false, 0),       // mchose_mix87_backend.cpp:245-254
                ("halljoy-sparklink", 2000, 2000, false, 0),          // backend_sparklink.inc:18
                ("halljoy-sayo-depth", 2000, 2000, false, 0),         // backend_sayo.inc:17
                ("finalmouse-centerpiece-pro", 1000, 1000, false, 0), // universal-analog-plugin main.cpp:288-299
                ("libhmk", 1000, 1000, false, 0),
                ("halljoy-rog-azoth-96-he", 0, 0, false, 0),
                ("logitech-pro-x-tkl-rapid", 0, 1000, false, 0),
                ("nuphy-he", 5000, 0, false, 0),
                ("madlions-a0", 5000, 0, false, 0),
                ("soup-wooting-v2", 0, 1000, false, 0),               // universal-analog-plugin main.cpp:180-199, 288-299
                ("soup-wooting-v1", 0, 1000, false, 0),
                ("soup-razer-huntsman-v2", 0, 1000, false, 0),
                ("soup-razer-huntsman-v3", 0, 1000, false, 0),
                ("soup-razer-tartarus-pro", 0, 1000, false, 0),
                ("soup-drunkdeer", 1000, 1000, false, 0),
                ("soup-keychron", 1000, 1000, false, 0),
                ("soup-madlions", 1000, 1000, false, 0),
                ("soup-bytech", 1000, 1000, false, 0),
                (A0ListenRoute.Id, 0, 0, false, 0),
            };
            var routes = AnalogKeyboardRoutes.All;
            Assert.Equal(expected.Length, routes.Count);
            foreach (var (id, retry, reconnect, probeOnce, tries) in expected)
            {
                var route = AnalogKeyboardRoutes.Find(id);
                Assert.NotNull(route);
                Assert.True(retry == route.StartRetryMs, id + " retry");
                Assert.True(reconnect == route.ReconnectMs, id + " reconnect");
                Assert.True(probeOnce == route.ProbeOnce, id + " probe once");
                Assert.True(tries == route.ReconnectTries, id + " tries");
            }
        }

        [Fact]
        public void Registry_HoldsEveryRouteOnce_InHallJoysOrder_WithTheListenerLast()
        {
            // native_analog_backends.def, then the families no HallJoy route
            // covers, the NuPhy protocol, the Soup families, and the listener.
            string[] expected =
            {
                "attackshark-pro", "halljoy-aula-mini60", "halljoy-mad68-a0", "halljoy-hex80-0x96",
                "halljoy-aula-hero", "halljoy-addressed-ipi", "halljoy-addressed-generic",
                "halljoy-aula-sparkplayjoy-6x21", "halljoy-irok-na87-m484", KeyAxisRoute.Id, "halljoy-aula-w669",
                "halljoy-irok-mg75-pro", "halljoy-chilkey-slice75", "rongyuan-snapshot", "rongyuan-stream",
                "halljoy-neo65", "halljoy-steelseries-apex", "halljoy-mchose-mix87", "halljoy-sparklink",
                "halljoy-sayo-depth", "finalmouse-centerpiece-pro", "libhmk", "halljoy-rog-azoth-96-he",
                "logitech-pro-x-tkl-rapid", "nuphy-he", "madlions-a0", "soup-wooting-v2", "soup-wooting-v1",
                "soup-razer-huntsman-v2", "soup-razer-huntsman-v3", "soup-razer-tartarus-pro", "soup-drunkdeer",
                "soup-keychron", "soup-madlions", "soup-bytech", A0ListenRoute.Id,
            };
            Assert.Equal(expected, AnalogKeyboardRoutes.All.Select(r => r.Id).ToArray());
            // Every route's protocol names it.
            foreach (var route in AnalogKeyboardRoutes.All)
                Assert.True(Enum.IsDefined(typeof(AnalogKeyboardProtocol), route.Protocol), route.Id);
        }
    }
}
